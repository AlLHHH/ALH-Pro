namespace AlhPro.Core;

/// <summary>【外部参数参照值】本文件只放"有出处的参考数字",可单测、但不改变生产行为。
///
/// 本工程口径:外部资料与真机实测冲突时以实测为准;没有可靠出处的数值只记为"建议"。
/// 当前只用到一个参照值(切点最小间距的默认值),其余研究结论不再随仓保存。</summary>
public static class ExternalPractice
{
    /// <summary>[已接线·旁证] 场景检测类工具常用的"最小场景长度"默认值 = 15 帧。
    /// 本工程 <see cref="SceneCutJudge.MinSceneLen"/> 取 15,用于卡切点最小间距(抑制闪光/频闪误判出的连续切点);
    /// 这是"15 没写错"的外部旁证,实际判定逻辑以本仓库真机实测为准。</summary>
    public const int SceneMinLenFramesDefault = 15;
}
