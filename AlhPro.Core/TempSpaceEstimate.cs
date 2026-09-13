namespace AlhPro.Core;

/// <summary>临时空间预估(纯逻辑,可单测)。
/// 【为什么要有它】历史上"预计只需 X GB、实际跑出 200 多 G"就是这条估算太乐观造成的;
/// 而任务的**峰值**帧数是"补帧输出帧数"(放大不减帧):所以单帧体积必须按"补帧之后每帧有多重"来算。
/// 【任务 O3 · 2026-09-13 补帧改"引擎直出 JPG"后的重标定】实测 119 帧:PNG 18.650s vs 直出 JPG 8.353s,
/// 但单帧体积 **238 KB → 737 KB(3.1×)**(引擎 q≈100 vs 程序内 q0.96)。峰值帧就是这些帧,
/// 所以补帧开着时单帧估算要乘 3.1 —— 不乘就等于把预估低估 3 倍(严重时反而爆盘)。</summary>
public static class TempSpaceEstimate
{
    /// <summary>1 MB/1080p 的源帧基准(JPG,按面积线性)。</summary>
    public const double SourceFrameMbPer1080p = 1.0;

    /// <summary>放大后单帧的 JPG 压缩系数(放大内容趋于平滑 → 压缩率更好;与历史口径一致,未改)。</summary>
    public const double UpscaledJpgCoef = 0.18;

    /// <summary>补帧输出改"引擎直出 JPG"后的体积系数(实测 238 KB → 737 KB)。</summary>
    public const double EngineDirectJpgFactor = 3.1;

    /// <summary>单帧体积下限(极小素材也按 0.5 MB 计,避免低估)。</summary>
    public const double MinFrameMb = 0.5;

    /// <summary>整任务预估的安全系数(历史口径,未改)。</summary>
    public const double SafetyFactor = 1.6;

    /// <summary>单帧按面积的粗估(MB):源帧 JPG≈1MB/1080p,再按放大倍率² 计,补帧时乘引擎直出 JPG 系数。</summary>
    public static double SourceFrameMegabytes(int srcW, int srcH)
    {
        if (srcW <= 0 || srcH <= 0) return MinFrameMb;
        double mb = SourceFrameMbPer1080p * ((double)srcW * srcH) / (1920.0 * 1080.0);
        return Math.Max(MinFrameMb, mb);
    }

    /// <summary>峰值帧(补帧输出帧)的单帧体积(MB)。</summary>
    public static double PeakFrameMegabytes(int srcW, int srcH, double outMult, bool frameInterp)
    {
        double src = SourceFrameMegabytes(srcW, srcH);
        double mult = outMult > 0 && double.IsFinite(outMult) ? outMult : 1.0;
        double mb = src * mult * mult * UpscaledJpgCoef * (frameInterp ? EngineDirectJpgFactor : 1.0);
        return Math.Max(src, Math.Max(MinFrameMb, mb));
    }

    /// <summary>整任务需要的字节数(峰值帧数 × 单帧体积 × 安全系数)。</summary>
    public static double NeedBytes(long peakFrames, double peakFrameMb)
        => Math.Max(0, peakFrames) * Math.Max(0, peakFrameMb) * 1024.0 * 1024.0 * SafetyFactor;
}
