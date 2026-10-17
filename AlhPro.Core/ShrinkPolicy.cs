namespace AlhPro.Core;

/// <summary>「引擎原生倍率 → 用户要的倍率」缩回这一步的策略(纯算术,不碰本机状态,便于定点验证与单测)。
/// 【为什么要有它 · 2026-10-17 用户反馈(m04853)】4x-only 权重选 2x 时,引擎必须按原生 4x 跑
/// (见 EngineScalePolicy 的实测:非原生倍数会让整帧位移 56×40 像素),跑完再把每帧缩回 2x。
/// 旧实现是 `foreach + Task.Run` **逐帧串行**缩放、整段只报一条不动的「正在精确调整」——
/// 用户原话:「能不能缩放快一点 为什么要逐帧缩放 缩放没有进度」。
/// 实测(batch:4K JPG 解码 + 缩放 + 重编码是主成本,与 ffmpeg 吞吐接近):串行 34 ms/帧,多路并行 8.4 ms/帧。
/// ⇒ 并行度取 CPU 一半、夹在 2..8;进度落在"本批引擎已跑完"的位置,条不倒退、也不假装快完成。</summary>
public static class ShrinkPolicy
{
    /// <summary>并行缩回的线程数:CPU 一半,夹在 [2,8]。
    /// 下限 2:双核机也真的两路并行(单路等于退回逐帧串行);上限 8:每路要吃一张 4K 解码位图,
    /// 再多内存峰值翻倍、并发也抢不过同时在跑的 GPU 批次。</summary>
    public static int Workers(int cpuCount) => Math.Clamp(cpuCount / 2, 2, 8);

    /// <summary>缩回阶段的进度百分比:与引擎逐帧进度(WatchDirProgressAsync)同口径,按"本批已跑完的帧
    /// 在全部帧里的位置"落点 —— 批跑完那一刻正好是本批超分区间的终点,条不会从 60% 跳回去。
    /// <paramref name="doneFrames"/> = 本批起始的全局帧号 + 本批帧数;<paramref name="totalFrames"/> = 全局总帧数。
    /// 没配区间(图片/非视频调用)或参数异常时退回 95(旧实现的固定值)。</summary>
    public static int Pct(int pctLo, int pctHi, int doneFrames, int totalFrames)
    {
        if (pctLo > 0 && pctHi > pctLo && totalFrames > 0)
            return Math.Clamp(pctLo + (int)((long)doneFrames * (pctHi - pctLo) / totalFrames), pctLo, Math.Min(pctHi, 100));
        return 95;
    }
}
