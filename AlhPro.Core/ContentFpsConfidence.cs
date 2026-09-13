namespace AlhPro.Core;

/// <summary>内容帧率(拍数)估计的**置信度打分**(纯逻辑,可单测)。
/// 【任务 M5 · 2026-09-13】真机反馈:「素材明明有拍数(离群间隔只是转场/长静止镜头那两三个),
/// 智能-均衡却说信度不足 → 原样保留不删帧」。根因是旧口径三个脆弱点:
///   ① 参照系用**均值**(一个长间隔就把均值拉走 → 同一段素材的"正常间隔"不再算正常);
///   ② 离散度用**变异系数 σ/μ**(对离群值极敏感,σ 被离群间隔抬高 → 稳定性一刀切掉);
///   ③ 容差是**绝对 ±1 帧**(间隔 2 帧时等于 ±50%,间隔 10 帧时又过严),
///      且"有效事件占比"惩罚是**线性** trimmed/total(离群多则整体压分)。
/// 现在的口径(只改分数算法,**判定阈值一个不动**:均衡 0.5 / 激进 0.35 / 保守 0.7 + 常见拍数):
///   · 参照系 → **中位数 med**(对离群稳健);
///   · 离散度 → **MAD/med**(中位绝对偏差,对离群稳健;≈0.67σ,所以对真拍数更宽松而不放过杂乱间隔);
///   · 容差 → **相对周期 ±20%**(下限 1 帧:极短周期不至于容差归零);
///   · 有效事件占比惩罚 → **开方 sqrt**(弱化:转场/静止镜头造成的少量离群不再整体压分)。
/// 【确定性】**无任何随机数** —— 同一段事件间隔序列每次得到完全相同的分数。
/// 【为什么不是"把阈值调松"】调阈值会同时放过真正无节奏的段(实拍连续运动);
/// 改分数算法只让"有节奏、只是被离群间隔冤枉"的段回到应有分数,杂乱间隔仍然低分(见单测)。</summary>
public static class ContentFpsConfidence
{
    /// <summary>平均间隔小于它 = 几乎每帧都在变(没有保持帧)→ 内容帧率≈输入帧率。</summary>
    public const double ContinuousMotionMeanGap = 1.2;

    /// <summary>相邻间隔容差 = 中位数 × 该比例(相对周期 ±20%)。</summary>
    public const double NearToleranceRatio = 0.20;

    /// <summary>评分结果。<paramref name="Continuous"/> = true 表示「素材几乎连续运动(无保持帧)」,
    /// 此时 ContentFps 取输入帧率、Confidence 固定 0.25(与旧口径一致)。</summary>
    public readonly record struct Score(
        double ContentFps,
        int Period,
        double Confidence,
        double MeanGap,
        double MedianGap,
        double MadRelative,
        int NearCount,
        int UsedGaps,
        int TotalGaps,
        bool Continuous);

    /// <summary>按变化事件的间隔序列打分。
    /// <param name="gaps">相邻变化事件的间隔(帧数),无需排序;离群间隔(转场/长静止)会被中位数口径自动降权。</param>
    /// <param name="inFps">输入帧率(用于把间隔换算成内容帧率)。</param>
    public static Score Compute(IReadOnlyList<double> gaps, double inFps)
    {
        int total = gaps?.Count ?? 0;
        // 间隔样本不足:无法判断节奏 → 与「几乎连续运动」同路径(调用方不会走到这里:事件数 <4 已提前返回)
        if (total < 3)
            return new Score(inFps, 1, 0.25, 0, 0, 1.0, 0, 0, total, true);

        var sorted = new List<double>(gaps!);
        sorted.Sort();
        double med = Median(sorted);
        // 离群裁剪:以中位数为锚 [0.5m, 2m](转场/长静止镜头造成的超长间隔不参与统计)
        var trimmed = sorted.Where(g => g >= med * 0.5 && g <= med * 2.0).ToList();
        if (trimmed.Count < 3) trimmed = sorted;
        double meanGap = trimmed.Average();
        if (meanGap < ContinuousMotionMeanGap)
            return new Score(inFps, 1, 0.25, meanGap, med, 1.0, 0, trimmed.Count, total, true);

        double fc = Math.Clamp(inFps / meanGap, 0.5, inFps);
        int period = (int)Math.Round(meanGap);

        // ① 一致性:间隔落在【中位数 ± max(1 帧, 20% 周期)】内的占比(旧的均值 ±1 帧)
        double nearTol = Math.Max(1.0, NearToleranceRatio * med);
        int near = trimmed.Count(g => Math.Abs(g - med) <= nearTol);
        // ② 稳定性:1 − MAD/中位数(MAD = 各间隔到中位数距离的中位数;对离群稳健)
        double mad = Median(trimmed.Select(g => Math.Abs(g - med)).ToList());
        double madRel = med > 0 ? mad / med : 1.0;
        double stability = Math.Max(0.0, 1.0 - madRel);
        // ③ 有效事件占比:开方弱化(转场/静止镜头导致的少量离群不再整体压分)
        double coverage = Math.Sqrt(trimmed.Count / (double)Math.Max(1, total));

        double conf = Math.Clamp(
            near / (double)Math.Max(1, trimmed.Count) * stability * coverage, 0, 1);
        return new Score(fc, period, conf, meanGap, med, madRel, near, trimmed.Count, total, false);
    }

    /// <summary>中位数(上中位:偶数个取 index = count/2,与旧实现同约定,保证结果可复现)。</summary>
    public static double Median(IReadOnlyList<double> values)
    {
        if (values == null || values.Count == 0) return 0;
        var s = new List<double>(values);
        s.Sort();
        return s[s.Count / 2];
    }
}
