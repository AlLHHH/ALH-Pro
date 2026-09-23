namespace AlhPro.Core;

/// <summary>【2026-09-23】分段合帧的**纯逻辑**：算"每段多少帧"与"段区间表"。无 IO、无 ffmpeg、可单测。
///
/// ================= 为什么需要它 =================
/// 用户反馈「不能补帧」的真相是**临时盘守门在开跑前拒绝**（诊断包 `ALHPro_Diag_20260923_1345`：
/// 4K 源 + 2x 超分到 8K + 2x 补帧 ≈ 5 分钟素材预估 **262GB**，临时盘只剩 132GB，6 次尝试全部 0.2 秒失败）。
/// 拒绝本身是对的（2026-09-15 那次没拒 ⇒ 跑到一半爆盘、8550 帧被占位帧顶替），但用户的诉求是
/// **能跑完**。做法：空间不够时不再"直接失败"，而是**按段编码 + 无缝拼接**，让盘上只需要同时存在
/// 少数几段的帧。
///
/// ================= 为什么段按"连续帧号区间"而不是按批 =================
/// 去重开着时，一个批处理的是若干**代表帧**，而每个代表帧带着**散布在整条时间轴上的全部重复槽位**
/// （见 `VideoService` 切批处的 `batchGroups`：每项带 `slots` = 同代表帧的全部槽位，注释也写过
/// "同组成员不连续"）⇒ **批的输出帧号不是连续区间**。所以段的完成判据只能是
/// "这段帧号区间里每一帧都已落盘"，与批边界彻底解耦 —— 这样去重开/关都对。
///
/// ================= 口径 =================
/// · 段上限 <see cref="MaxFramesPerSegment"/>=2000（2026-09-13 批准的口径，不新造第二个数）；
/// · 段下界 <see cref="MinFramesPerSegment"/>=50：段太小则"每段一次编码器启动"（秒级/次）摊到每帧上
///   不划算；50 帧 ≈ 1.7 秒素材，是"分段还有意义"的下界。低于它说明余量已紧到任何分段都救不了
///   ⇒ 返回 0，调用方保持"拒绝 + 给量化降级建议"（2026-09-23 已实现那段文案）。
/// · 预算比例**复用** <see cref="RenderPolicy.BatchTempDiskShareLimit"/>（0.65），不新增常数；
/// · 单帧占用**必须**由调用方用 `TempSpaceEstimate` 的峰值帧/源帧口径传入（与 `NeedBytes` 同源，
///   禁止在这里另写一套估算 —— 历史上"预计 X GB、实际 200 多 G"就是两套口径造成的）。</summary>
public static class MuxSegmentation
{
    /// <summary>段上限（帧）。2026-09-13 批准的分批口径；抬高它必须同时确认"在飞段数 × 段帧数"仍放得进余量。</summary>
    public const int MaxFramesPerSegment = 2000;

    /// <summary>段下界（帧）。低于它就别分段了：每段一次编码器启动不划算，且说明余量已经救不回来。</summary>
    public const int MinFramesPerSegment = 50;

    /// <summary>一段：<paramref name="StartFrame"/> 是 0 基的**最终帧号**，长度 <paramref name="FrameCount"/>。
    /// 帧号口径 = 合帧目录里 `frame_%06d.jpg` 的序号（即"队列里的第几帧"），与 `-start_number` 直接对应。</summary>
    public readonly record struct Segment(int Index, long StartFrame, long FrameCount)
    {
        /// <summary>末帧的 0 基帧号（含）。</summary>
        public long EndFrameInclusive => StartFrame + FrameCount - 1;
    }

    /// <summary>按剩余空间算"每段多少帧"。
    /// 返回 **0** 表示"连下界都放不下 ⇒ 不该分段，调用方应保持拒绝"。
    /// 【为什么按 (并发数 + 1) 切预算】分段是**并发跑**的（用户 2026-09-23 选定"保持并发"）：
    /// 盘上最多同时存在 `并发数` 段"已落盘待编码" + `1` 段正在编码 ⇒ 峰值就是这么多段的帧。
    /// 只按 1 段算会低估 `并发数` 倍，正是"分段了却还是爆盘"的来源。</summary>
    public static int FramesPerSegment(double freeDiskGB, double peakFrameMb, double sourceFrameMb,
        int concurrency, int maxFramesPerSegment = MaxFramesPerSegment, int minFramesPerSegment = MinFramesPerSegment)
    {
        if (!double.IsFinite(freeDiskGB) || freeDiskGB <= 0) return 0;
        double perFrameMb = Math.Max(0, peakFrameMb) + Math.Max(0, sourceFrameMb);
        if (perFrameMb <= 0) return 0;
        int c = Math.Max(1, concurrency);
        double budgetMb = freeDiskGB * 1024.0 * RenderPolicy.BatchTempDiskShareLimit;
        int fit = (int)Math.Floor(budgetMb / (perFrameMb * (c + 1)));
        int floor = Math.Max(1, minFramesPerSegment);
        int cap = Math.Max(floor, maxFramesPerSegment);
        if (fit < floor) return 0;                 // 连下界都放不下 ⇒ 老实拒绝
        return Math.Min(cap, fit);
    }

    /// <summary>把总帧数切成**首尾相接**的段区间（0 基 StartFrame；末段是余数）。
    /// 【不变量（有单测）】① 段号 0..N-1 递增；② 第 i 段的 Start = 前 i 段之和（无空洞、无重叠）；
    /// ③ Σ 段长 == totalFrames；④ 每段至少 1 帧。总帧数或段帧数非法 ⇒ 返回空表（调用方不会走到）。</summary>
    public static IReadOnlyList<Segment> PlanSegments(long totalFrames, int framesPerSegment)
    {
        var list = new List<Segment>();
        if (totalFrames <= 0 || framesPerSegment <= 0) return list;
        long start = 0;
        int idx = 0;
        while (start < totalFrames)
        {
            long n = Math.Min(framesPerSegment, totalFrames - start);
            list.Add(new Segment(idx++, start, n));
            start += n;
        }
        return list;
    }
}
