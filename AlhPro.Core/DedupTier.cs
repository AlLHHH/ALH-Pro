namespace AlhPro.Core;

/// <summary>去重「智能档」三档力度(均衡/激进/保守)的**唯一来源**(纯逻辑,可单测)。
/// 【任务 M4 · 2026-09-13】真机反馈:「智能三档看起来只有门槛不同,真正执行的处理路径完全一样」。
/// 事实核对:`DetectDupFramesAdaptive` 里那套 `force`(1.5/1.0/0.7)—— 它按力度缩放
/// sadThr/ssimThr/protectRatio/segSad —— 在**智能主路径上根本到不了**:智能模式先走
/// 「拍数识别 + 网格采样」(SegmentContentFpsSync forceGrid),识别不出来时旧实现直接「原样保留」;
/// 只有「全文分析」按钮和少数回退场景才会调用 DetectDupFramesAdaptive。
/// 现在的口径(用户定案):三档力度**必须真的作用在执行路径上** ——
///   · 均衡(smartMode 0):force 1.0、置信门槛 0.50(原样);
///   · 激进(smartMode 1):force 1.5、置信门槛 0.35(删得更多);
///   · 保守(smartMode 2):force 0.7、置信门槛 0.70 + 常见拍数(删得更少);
/// 落地点:智能模式"信度不足"时的【帧差+SSIM 兜底】(任务 M3)按上表缩放四个阈值 —— 这条路径确实
/// 会被执行到,所以三档产出必然不同(日志会打出档位、force 与缩放后的阈值,以及实际删帧数)。
/// 【缩放公式与旧 DetectDupFramesAdaptive 完全同口径】迁移到这里是为了「一个来源」:
/// 旧代码里的那套公式原地保留会与新路径产生第二份实现,正是本次要消灭的问题。
/// 方向性(单测保证严格单调):force 越大 → 快筛阈值越大(更多帧进入精验)、SSIM 门槛越低、保护占比越大、
/// 静止段阈值越大 → **删得只会更多,不会更少**。</summary>
public static class DedupTier
{
    /// <summary>激进档力度(删得最多)。</summary>
    public const double AggressiveForce = 1.5;
    /// <summary>均衡档力度(内置标定,不动)。</summary>
    public const double BalancedForce = 1.0;
    /// <summary>保守档力度(删得最少)。</summary>
    public const double ConservativeForce = 0.7;

    /// <summary>智能档位(0=均衡、1=激进、2=保守)→ 力度系数。</summary>
    public static double Force(int smartMode) => smartMode switch
    {
        1 => AggressiveForce,
        2 => ConservativeForce,
        _ => BalancedForce,
    };

    /// <summary>智能档位中文名(日志/提示用,措辞与界面下拉项一致)。</summary>
    public static string Name(int smartMode) => smartMode switch
    {
        1 => "激进",
        2 => "保守",
        _ => "均衡",
    };

    /// <summary>快筛阈值(SAD)按力度缩放,钳在 [1.0, 7.0](与旧实现同上下限)。</summary>
    public static double ScaleSad(double sadThr, double force) => Math.Clamp(sadThr * force, 1.0, 7.0);

    /// <summary>SSIM 门槛按力度缩放(力度越大门槛越低 = 越容易判定为重复帧),下限按档位:
    /// 激进 ≥? 不,是【上限保护】——激进档不允许比 0.985 更松(防误删微动),保守档不允许比 0.995 更松。</summary>
    public static double ScaleSsim(double ssimThr, double force, int smartMode)
        => Math.Max(1.0 - (1.0 - ssimThr) * force,
            smartMode == 1 ? 0.985 : smartMode == 2 ? 0.995 : 0.99);

    /// <summary>局部动作保护占比按力度缩放,钳在 [0.05, 0.60](与旧实现同上下限)。</summary>
    public static double ScaleProtect(double protectRatio, double force) => Math.Clamp(protectRatio * force, 0.05, 0.60);

    /// <summary>静止段合并阈值(段 SAD)按力度缩放,钳在 [2.0, 8.0](与旧实现同上下限)。</summary>
    public static double ScaleSegSad(double segSad, double force) => Math.Clamp(segSad * force, 2.0, 8.0);
}
