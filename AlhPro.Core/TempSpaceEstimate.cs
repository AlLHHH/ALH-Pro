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

    /// <summary>整任务需要的字节数 =(全片峰值帧 + **每批并存帧**) × 单帧体积 × 安全系数。
    /// 【任务 T · 2026-09-13:为什么必须加"每批并存帧"这一项】
    /// 旧的 peakFrames 是"补帧后总帧数"(全片口径),它不随每批帧数变 —— 而任务 T 把批上限
    /// 从 400 抬到 700(×1.75)后,"输入帧 + 本批输出帧并存"的**同屏峰值**也按同一倍数涨:
    /// 只抬上限不把这个量算进预估,就是"只抬上限、不给守门"(用户硬约束明确禁止)。
    /// 所以这里加一项 `batchFrames × (峰值帧单帧体积 + 源帧单帧体积)` —— 正是一批里同时占盘的两侧。
    /// 【兼容性】batchFrames/sourceFrameMb 默认 0 → 与改动前**逐字一致**(既有断言与口径不变)。
    /// 【量级参考(1080p 源、2x 超分、补帧开)】单帧源 ≈1 MB、峰值帧 ≈0.72 MB;
    /// 每批 700 帧 → 该项 ≈ 700×1.72 MB ≈ 1.2 GB,乘 1.6 安全系数 ≈ 1.9 GB —— 与"全片项"同量级,
    /// 正是不能漏掉的原因。【待实测标定】"并存"实际有几批同时占盘取决于并发
    /// (SafeRender.GetVideoConcurrency());这里按**一批**保守计入。</summary>
    public static double NeedBytes(long peakFrames, double peakFrameMb,
        int batchFrames = 0, double sourceFrameMb = 0)
    {
        double all = Math.Max(0, peakFrames) * Math.Max(0, peakFrameMb);
        double perBatch = Math.Max(0, batchFrames) * (Math.Max(0, peakFrameMb) + Math.Max(0, sourceFrameMb));
        return (all + perBatch) * 1024.0 * 1024.0 * SafetyFactor;
    }

    /// <summary>【任务 T 守门复算】按"最终真正生效的每批帧数"再算一次预估:
    /// 调用方在批计划算定之后必须用它再判一次剩余空间(批上限抬到 700 后,预估必须同步抬)。
    /// 与 <see cref="NeedBytes"/> 同一条公式,只是把口径写进函数名,避免调用点漏传 batchFrames。</summary>
    public static double NeedBytesForBatch(long peakFrames, double peakFrameMb, int batchFrames, double sourceFrameMb)
        => NeedBytes(peakFrames, peakFrameMb, batchFrames, sourceFrameMb);

    /// <summary>【2026-09-23 加】按**任意一组参数**算整任务临时盘需求(GB)。
    /// 【为什么要它 —— 来自一份真实用户反馈】某个用户报"不能补帧",诊断包里真相是:
    /// 4K 源 + 2x 超分(输出 8K)+ 2x 补帧的 5 分钟素材,预估要 **262GB**,而临时盘只剩 132GB
    /// ⇒ 守门在开跑前就拒绝(6 次尝试全是 0.2 秒失败),用户看到的是"补帧不能用" ✗。
    /// 而同样的素材只要**关掉超分**(输出留在 4K、补帧保留)就只要约 118GB —— **放得下**。
    /// 所以"跑不动时该往哪降"这件事必须由软件算出来告诉用户,而不是只丢一句"降低倍率"。
    /// 本函数就是那个计算器:同一套公式、换一组参数重算,不新增第二套口径。</summary>
    public static double NeedGigabytesFor(int srcW, int srcH, long baseFrames, bool frameInterp, int interpScale,
        double upscaleMult, int batchFrames)
    {
        double srcMb = SourceFrameMegabytes(srcW, srcH);
        double outMb = PeakFrameMegabytes(srcW, srcH, upscaleMult, frameInterp);
        long peak = frameInterp
            ? (long)Math.Ceiling(Math.Max(0L, baseFrames) * (double)Math.Max(1, interpScale))
            : Math.Max(0L, baseFrames);
        return NeedBytes(peak, outMb, batchFrames, srcMb) / (1024.0 * 1024.0 * 1024.0);
    }
}
