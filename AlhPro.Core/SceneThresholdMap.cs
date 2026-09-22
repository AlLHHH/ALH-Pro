namespace AlhPro.Core;

/// <summary>一次「转场识别」判定真正使用的一组阈值(帧差 / 强切 / 拉普拉斯下降比)。
/// <see cref="SceneCutJudge.IsCut"/> 的三个内置常量就是它的 <see cref="BuiltIn"/> 实例 ——
/// 把三个数打包成一个值传,是为了让"用户滑块调出来的判据"与"内置判据"走**同一条代码路径**,
/// 不给"界面上调了、判定按另一套"留任何缝隙。</summary>
public readonly record struct SceneCutThresholds(double Diff, double StrongDiff, double LapDrop)
{
    /// <summary>今天的内置判据(用户不动滑块时的行为) —— 逐字等于 <see cref="SceneCutJudge"/> 的三个常量。</summary>
    public static SceneCutThresholds BuiltIn =>
        new(SceneCutJudge.DiffThreshold, SceneCutJudge.StrongDiffThreshold, SceneCutJudge.LapDropRatio);

    /// <summary>日志/提示用的一句话口径(不参与判定)。</summary>
    public string Text => $"帧差≥{Diff:0.##} 且(清晰度↓≥{(1 - LapDrop) * 100:0}% 或 帧差≥{StrongDiff:0.##})";
}

/// <summary>「转场阈值」滑块(0.15~0.90) → <see cref="SceneCutThresholds"/> 的**唯一映射**(纯逻辑,可单测)。
///
/// ================== 为什么需要它(2026-09-21) ==================
/// 「转场阈值」滑块 2026-09-15 被删掉,理由是"判定用的是内置阈值,这条滑条根本不影响判定"——
/// 那个判断在当时是**对的**:滑块的值只喂给"采样判据不可用时的 ffmpeg `scene` 回退",内置判据
/// (<see cref="SceneCutJudge.DiffThreshold"/>/<see cref="SceneCutJudge.LapDropRatio"/>)压根不看它。
/// 结果就是一条**假控件**:拉它画面不变,留着只会让人以为要调。
///
/// 2026-09-21 用户要求"把滑块加回来,并且让它真的生效" ⇒ 必须有这一层映射:把滑块值换算成
/// 上面那两组判据的数字。本类是**唯一换算处**,别在 UI 或 VideoService 里再写一份。
///
/// ================== 换算口径 ==================
/// 滑块 = 一条**严格度**轴(值越大 = 越要求"像一次真转场" ⇒ 切点越少 ⇒ 补帧段越长 ⇒ 引擎少启动):
///   · <see cref="SliderMin"/> = 0.15 = **半倍**判据(帧差 12.5,更敏感,切点更多);
///   · <see cref="DefaultSlider"/> = 0.30 = **1 倍** = 今天的内置值(**逐字一致**:25 / 50 / 0.60);
///   · <see cref="SliderMax"/> = 0.90 = **3 倍**判据(帧差 75,只认强转场,切点更少)。
/// 中间线性(严格度倍率 k = 滑块值 ÷ 0.30),两端夹住。即:
///   帧差阈值 = 25 × k;强切阈值 = 50 × k;拉普拉斯下降比 = 0.60 ÷ k(夹在 [0.2, 1.0])。
///
/// 【三个数同向才是对的】调大滑块必须让**切点只会变少、不会变多**,否则用户"调大→切成更多段→
/// 引擎启动更慢"就是反直觉的。三条判据的方向都对得上:
///   · <see cref="SceneCutThresholds.Diff"/> 抬 = 更少的帧对能过第一道闸;
///   · <see cref="SceneCutThresholds.StrongDiff"/> 抬 = 更多帧对落到"看清晰度"那一步 ⇒ 那一步可以否掉它;
///   · <see cref="SceneCutThresholds.LapDrop"/> 降 = 清晰度那一步更难过。
/// 单测(<c>SceneThresholdMapTests</c>)钉住"严格度单调"这三条,并且逐字钉住默认档。
///
/// 【为什么下限是 0.15 而不是 0】滑块若允许拉到 0,映射出来就是"帧差阈值 0"——每一对帧都算切点,
/// 判定退化、也没有任何实际含义(等于"每帧都保护")。0.15(判据减半)已经是明显更强的灵敏度,
/// 再往下没有用户能分辨的差别,所以把刻度做在"半倍~三倍"这一段上,整条行程都真的起作用。
///
/// 【为什么拉普拉斯比要夹上界 1.0】k = 0.5 时 0.60 ÷ 0.5 = 1.2,而"新帧能量 ÷ 旧帧能量 ≤ 1.2"
/// 几乎恒真 ⇒ 那一步等于不参与判定(只剩帧差这一条)。与其让一个 >1 的比例看起来像"还有意义",
/// 不如显式夹到 1.0 并在注释里写明:低档位上"清晰度"这条判据按"不否决"处理。
///
/// 【那个 0.3 还有第二层含义,别混】滑块原值同时还是"采样判据拿不到数据时回退 **ffmpeg scene**"
/// 的阈值(ffmpeg 的 `scene` 分数本来就是 0~1)。回退路径**继续用原值**(与改动前逐字一致),
/// 只有这里的**内置判据**才吃换算结果 —— 两条路的方向一致(越大越严),不会互相打架。</summary>
public static class SceneThresholdMap
{
    /// <summary>滑块刻度下限。取值理由见类注释(0.15 = 判据半倍)。</summary>
    public const double SliderMin = 0.15;

    /// <summary>滑块刻度上限。取值理由见类注释(0.90 = 判据三倍)。</summary>
    public const double SliderMax = 0.90;

    /// <summary>滑块刻度步长(界面的 StepFrequency 必须用它,否则用户拖不到默认档那个点)。</summary>
    public const double SliderStep = 0.05;

    /// <summary>默认档 = 判据 1 倍 = 今天的内置值。**不许改这里去改行为**:要改行为改 SceneCutJudge 的常量。</summary>
    public const double DefaultSlider = 0.30;

    /// <summary>把任意值吸附到刻度上(两位小数 + 夹进 [<see cref="SliderMin"/>, <see cref="SliderMax"/>])。
    /// 【为什么必须吸附】界面的 Slider 会把值算成 `0.15 + 3 × 0.05 = 0.30000000000000004` 这种带浮点尾巴的数;
    /// 拿它去算倍率会得到 1.0000000000000002 ⇒ 帧差阈值变成 25.000000000000004,
    /// "默认档与内置值逐字一致"就成了空话(单测也会红)。吸附后 0.30 就是 0.30。
    /// NaN / ±∞ / 非法值一律回落到默认档(不抛异常:这条链上不该因为一个坏数字让整次处理失败)。</summary>
    public static double Snap(double slider)
    {
        double v = double.IsFinite(slider) ? slider : DefaultSlider;
        int steps = (int)Math.Round((v - SliderMin) / SliderStep, MidpointRounding.AwayFromZero);
        steps = Math.Clamp(steps, 0, MaxSteps);
        return Math.Round(SliderMin + steps * SliderStep, 2);
    }

    /// <summary>刻度格数(0.15 → 0.90 共 15 格)。</summary>
    public static int MaxSteps => (int)Math.Round((SliderMax - SliderMin) / SliderStep);

    /// <summary>严格度倍率 k(默认档 = 1;范围 0.5~3.0)。</summary>
    public static double Strictness(double slider) => Snap(slider) / DefaultSlider;

    /// <summary>滑块值 → 本次生效的判据。<see cref="DefaultSlider"/> 处**逐字**返回 <see cref="SceneCutThresholds.BuiltIn"/>。</summary>
    public static SceneCutThresholds For(double slider)
    {
        double k = Strictness(slider);
        return new SceneCutThresholds(
            Diff: SceneCutJudge.DiffThreshold * k,
            StrongDiff: SceneCutJudge.StrongDiffThreshold * k,
            LapDrop: Math.Clamp(SceneCutJudge.LapDropRatio / k, 0.2, 1.0));
    }

    /// <summary>日志/提示用:一行说清"这次用的是哪个档、算出来的判据是什么"。
    /// 形如 <c>0.30(默认档,判据 1 倍:帧差≥25 且(清晰度↓≥40% 或 帧差≥50))</c>。</summary>
    public static string Describe(double slider)
    {
        double s = Snap(slider);
        double k = Strictness(s);
        string tier = Math.Abs(s - DefaultSlider) < 1e-9 ? "默认档" : (s < DefaultSlider ? "更敏感" : "更严格");
        return $"{s:0.00}({tier},判据 {k:0.##} 倍:{For(s).Text})";
    }
}
