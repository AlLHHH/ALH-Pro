namespace AlhPro.Core;

/// <summary>缺陷帧(黑帧)抽样的纯策略(F2):把"抽 6 帧看看"换成"均匀分散 + 首尾必查"的可测计划。
///
/// 【为什么必须加密】真机诊断(2026-09,RTX 5060 Laptop 8GB;任务"对峙的男人.mp4",超分关 + rife 4x 补帧 +
/// 智能去重)一次"成功 1 / 失败 0"的作业里,成片含 3 段全黑(0~0.17s / 14~15.5s / 23.17~23.83s)。
/// 黑帧来自补帧的「补回(还原源时间轴)」这一步,而当时的自检只抽样【前 6 帧】:黑片实测长 40~360 帧,
/// 在几千~几万帧的层批输出里抽前 6 帧几乎必然落空 —— 等于没查,于是黑帧一路进成片且零告警。
///
/// 【为什么不能逐帧全查】<c>EngineService.IsBlackPng</c> 要整张解码(1080p 一次约 10~20ms),
/// 层批中间帧动辄上万张,逐帧全查会把补帧收尾拖成分钟级。故按"总量比例 + 上下限 + 首/中/尾必查"抽样。
///
/// 【残留风险(诚实口径,别把它当保证)】均匀抽样只能保证命中"长度 ≥ 采样间隔"的黑片:
/// 48 个采样点覆盖 15000 帧时间隔约 310 帧,短于 310 帧的黑片仍可能漏。
/// 因此层批路【另外按块抽样】:层批每次引擎调用(块 ≤384 帧)单独抽样一次,
/// "某次引擎调用整体输出黑帧"这一真机形态(见 EngineService.InterpLayerBatchAsync)必然被抓到;
/// 而整段只抽一次 6 帧时,连整块全黑都可能漏。</summary>
public static class DefectSampling
{
    /// <summary>最少采样帧数。下限 8 的依据:分段补帧按段抽样,一段可能只有几十帧(切点密集的素材),
    /// 8 个点已能让"整段绝大部分变黑"必然命中,同时解码开销可忽略(8×15ms)。</summary>
    public const int MinSamples = 8;

    /// <summary>最多采样帧数(硬上限,控制 CPU/IO 开销)。上限 48 的依据:
    /// 1080p 每张解码约 15ms → 48 张约 0.7 秒,相对补帧阶段(分钟级)可忽略;
    /// 再往上加对"命中概率"的边际收益迅速变小(间隔 ∝ 1/N),但收尾会开始肉眼可见。</summary>
    public const int MaxSamples = 48;

    /// <summary>每多少个待检帧至少抽 1 帧(总量比例口径)。取 32 的依据:
    /// 实测黑片最短约 40 帧(诊断包 0.17s 那段 ≈ 5 帧@30fps 的输出槽,对应层批里数十帧连续中间帧),
    /// 32 < 40 意味着"采样间隔 ≤ 最短黑片长度"这一理想条件在中小规模输出上成立;
    /// 只有输出远超 MaxSamples×32=1536 帧时才会被上限截断(那时间隔变大,靠"按块抽样"兜底)。</summary>
    public const int FramesPerSample = 32;

    /// <summary>该抽多少帧:按总量 1/32,夹在 [MinSamples, MaxSamples],且不超过总帧数。
    /// 【用 long 算】total 接近 int.MaxValue 时 (total+N-1) 会溢出成负数 → 抽样数会掉到下限
    /// (等于"帧数极大时反而不查了"),故先升到 long 再取整。</summary>
    public static int SampleCount(int total)
    {
        if (total <= 0) return 0;
        if (total <= MinSamples) return total;
        int want = (int)(((long)total + FramesPerSample - 1) / FramesPerSample);   // 向上取整:宁可多抽一帧
        return System.Math.Clamp(want, MinSamples, MaxSamples);
    }

    /// <summary>采样下标:严格递增、首帧必查(=0)、末帧必查(=total-1)、其余均匀分散。
    /// 返回数量 = min(total, max(1, count));count ≤ total 时不会有重复下标
    /// (步长 (total-1)/(n-1) ≥ 1 → 取整后逐项至少 +1)。</summary>
    public static int[] Indices(int total, int count)
    {
        if (total <= 0) return System.Array.Empty<int>();
        int n = System.Math.Min(total, System.Math.Max(1, count));
        if (n == 1) return new[] { 0 };
        var res = new int[n];
        for (int k = 0; k < n; k++)
            res[k] = (int)((long)k * (total - 1) / (n - 1));
        return res;
    }

    /// <summary>采样方案(总帧数 → 下标)的一步到位入口:调用方只写一行,口径统一,不会两处各算一套。</summary>
    public static int[] Plan(int total) => Indices(total, SampleCount(total));

    /// <summary>写进日志的一行依据说明(排查时能一眼看出"这次到底查了多少、按什么口径算的")。</summary>
    public static string Describe(int total, int blackHits)
        => $"抽样 {SampleCount(total)}/{total} 帧(均匀分散+首尾必查;每 {FramesPerSample} 帧至少 1 帧,"
         + $"上下限 {MinSamples}~{MaxSamples};命中缺陷 {blackHits} 帧)";
}
