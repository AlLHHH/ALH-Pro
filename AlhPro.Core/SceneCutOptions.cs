namespace AlhPro.Core;

/// <summary>「转场识别」在一次任务里真正生效的**一组**用户选项(目前只有阈值滑块一项)。
///
/// 【为什么留着一个"只有一个字段的记录",而不直接传一个裸 double】
/// 这条链上的选项天生会**成组**变化(它们全部由「转场识别」那一个勾选框派生),而
/// `ProcessVideoAsync` 已经有六十多个形参 —— 每加一项就往里塞一个裸形参,读代码的人看不出它属于哪套功能。
/// 【沿革 · 照实读,因为它刚被撤过一次】
///   · 2026-09-21 曾把「最短补帧段 60 帧」这个开关也放进这里(阈值 + 最短段两项同组传);
///   · 2026-09-22 用户看过代价后裁决**整块撤掉**:那条规则是拿"鬼影防线"去换引擎启动次数
///     (被合并掉的切点处照常插帧 ⇒ 可能出一帧跨切混合),而「转场阈值」滑块能用**不牺牲任何保护**的方式
///     达到同一个目的(少判切点 ⇒ 引擎同样少启动,保留下来的切点照样受保护)⇒ 记录里只剩阈值一项。
///   留这个类型,是为了下次真要给「转场识别」加选项时,不必再往那个六十多参数的签名里塞裸参数
///   (删掉它就得把 `sceneCut` 又改回一个裸 `double?`,那正是 09-21 那次改动的反面)。
///
/// 【传 null = 转场识别关】与加这些选项之前的行为逐字一致(不采样判据、不做切点保护)。
///
/// 【老设置兼容】滑块值沿用老字段 `VideoSettings.SceneThr`(0~1,默认 0.3),保留在设置文件里;
/// 缺字段的老文件按默认值走 —— 升级不会改变用户的画面结果(见 VideoView 的 ApplyVideoParams)。</summary>
public sealed record SceneCutOptions(double Threshold)
{
    /// <summary>本次生效的切点判据(由滑块值映射;默认档**逐字**等于 <see cref="SceneCutThresholds.BuiltIn"/>)。
    /// 唯一换算处 = <see cref="SceneThresholdMap.For"/>。</summary>
    public SceneCutThresholds Thresholds => SceneThresholdMap.For(Threshold);

    /// <summary>日志/提示用的一句话口径(不参与判定)。</summary>
    public string Text => SceneThresholdMap.Describe(Threshold);
}
