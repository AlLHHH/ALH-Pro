namespace AlhPro.Core;

/// <summary>视频降噪滤镜链(纯逻辑,可单测)。
///
/// 【2026-09-21 参数表整体加强 · 用户要求"降噪效果要可观"】
///   动机:实测旧表**开了跟没开差不多** —— 干净度提升有限、帧间闪烁几乎没动(旧弱档闪烁σ 0.979,
///   而有噪源本身才 1.084),而且"弱"与"中"的**空间参数完全相同**(选弱/中在空间降噪上毫无区别)。
///   新表(全部为实测值,口径见 `_qa\denoise_effect.py`;素材 = 用户自己的片子 6 张连续帧压 JPEG q15):
///     参照:干净 σ0.949 / 细节 110.7 / 闪烁 0.703;有噪源 σ1.181(1.24× 干净) / 细节 97% / 闪烁 1.084
///     组合(默认)  弱:降噪 **23%** 细节 74% 闪烁 0.709   (旧:16% / 75% / 0.979)
///                 中:降噪 **30%** 细节 72% 闪烁 0.536   (旧:19% / 75% / 0.852)
///                 强:降噪 **39%** 细节 58% 闪烁 0.446   (旧:26% / 68% / 0.687)
///     仅空间      弱/中/强 = 15% / 19% / 23%(细节 74% / 74% / 61%;几乎不降闪烁 —— 空间域本来就不管帧间)
///     仅时间      弱/中/强 = 14% / 23% / 29%(细节 91% / 84% / 77%;闪烁 0.741 / 0.562 / **0.467**)
///   ⇒ 每一档的"去掉多少噪声"都明显提高,帧间闪烁大幅改善;代价是强档细节保留从 68% 降到 62%
///     (强档本来就是"最狠"的档,界面提示里已如实写明代价)。
///
/// 【参数表结构与不变量】`组合 = 空间档 + 时间档`(有单测钉住)—— 所以三档的 nlmeans 与 hqdn3d 必须**同步**递增,
/// 否则"组合档"与"仅空间/仅时间"两套口径会各说各话(2026-09-13 修过一次同样的非单调问题)。
///
/// 【历史沿革(保留备查)】最早是"整体削弱、弱档大削弱"(2026-09-13,任务 M1),因为用户反馈"降噪感太强、发假、
/// 塑料感、没有棱角";此后默认一直是**关**。2026-09-21 用户重新提出"效果要可观" ⇒ 参数加强,
/// 但**默认仍不开**(开关默认关,用户自己在「视频降噪」里打开;自动档会先体检素材再决定)。</summary>
public static class VideoDenoise
{
    /// <summary>降噪方式:空间域 + 时间域联合(默认,兼容旧设置)。</summary>
    public const int KindBoth = 0;
    /// <summary>降噪方式:仅空间域(nlmeans)。</summary>
    public const int KindSpatialOnly = 1;
    /// <summary>降噪方式:仅时间域(hqdn3d)。</summary>
    public const int KindTemporalOnly = 2;

    /// <summary>「降噪强度 = 自动」的档位序号(**界面上是"弱/中/强"之后的第 4 项,追加在后 ⇒ 老设置的 1/2/3 含义不变、不需要迁移**)。
    /// 【为什么用 4 这个值】界面上传下来的强度 = <c>SelectedIndex + 1</c>(0=关),所以第 4 项(索引 3)天然是 4。
    /// 【谁会吃掉它】管线在拆帧之前先做一次**源素材体检**(<see cref="VideoNoiseProbe"/>),
    /// 把 4 换成 0~3 里的一个再往下走 —— 所以 <see cref="Filter"/> **永远不该收到 4**;
    /// 万一收到,<see cref="StrengthOf"/> 按"中"兜底(自动档的保守上限),绝不按"强"处理。</summary>
    public const int Auto = 4;

    /// <summary>档位归一:1=弱、2=中、3=强。**严格单调**是这次变更的硬要求(旧实现里"仅空间"的
    /// 三档与组合档不同步、且越界值落到"强",导致选"弱"反而不比"中"轻)。调用方只会传 1~3
    /// (videoDenoise==0 表示"关",根本不会调用滤镜链;<see cref="Auto"/>=4 会被管线先换成 0~3)。
    /// 【Auto 与越界值的分工(2026-09-21)】正好等于 <see cref="Auto"/>(4) ⇒ 按「中」兜底(自动档的保守上限);
    /// **大于 4 仍然按旧契约落到「强」** —— 既有单测 `Strength_is_clamped(99 → 3)` 钉的就是这条,
    /// 不能因为新增了 4 就顺手把越界处理也改松。</summary>
    public static int StrengthOf(int strength)
        => strength == Auto ? 2
         : strength >= 3 ? 3
         : strength <= 1 ? 1 : 2;

    /// <summary>空间维 nlmeans:弱 = s3 p3 r3、中 = s4 p4 r4、强 = s6 p5 r5(参数越大降得越狠)。
    /// 【2026-09-21 加强】旧表里"弱"与"中"的**空间参数完全相同**(都是 s3p3r3),选弱/中在空间降噪上毫无区别 ——
    /// 用户反馈"降噪效果不可观"正是这个原因之一。现在三档严格递增。</summary>
    public static string SpatialFilter(int strength) => StrengthOf(strength) switch
    {
        3 => "nlmeans=s=6:p=5:r=5",   // 强:单独用实测降噪 23% / 细节 61%
        2 => "nlmeans=s=4:p=4:r=4",   // 中:单独用实测降噪 19% / 细节 74%
        _ => "nlmeans=s=3:p=3:r=3",   // 弱:单独用实测降噪 15% / 细节 74%
    };

    /// <summary>时间维 hqdn3d(亮度空间:色度空间:亮度时间:色度时间):弱 8:6:12:8、中 16:12:24:16、强 24:18:36:24。
    /// 【2026-09-21 加强】旧表弱/中档是 2:1.5:3:2 与 4:3:6:4 —— 实测"弱"只去掉 14% 噪点、且**几乎不降帧间闪烁**
    /// (闪烁σ 0.979,而有噪源本身就是 1.084),等于开了跟没开一样。现在最轻的一档就能把闪烁压到 0.741。
    /// 实测(仅时间档):弱 14%/细节 91%/闪烁 0.741 · 中 23%/84%/0.562 · 强 29%/77%/0.467。</summary>
    public static string TemporalFilter(int strength) => StrengthOf(strength) switch
    {
        1 => "hqdn3d=8:6:12:8",
        2 => "hqdn3d=16:12:24:16",
        _ => "hqdn3d=24:18:36:24",
    };

    /// <summary>档位中文名(日志/提示用,措辞与界面下拉项一致)。</summary>
    public static string StrengthName(int strength) => strength switch
    {
        1 => "弱",
        2 => "中",
        _ => "强",
    };

    /// <summary>按降噪方式与强度拼 ffmpeg 滤镜串。
    /// <param name="strength">强度:1=弱、2=中、3=强(0 与越界值当"强"处理,与旧实现的 switch 兜底一致)。</param>
    /// <param name="kind">0=空间+时间、1=仅空间、2=仅时间。</param></summary>
    public static string Filter(int strength, int kind = KindBoth) => kind switch
    {
        KindSpatialOnly => SpatialFilter(strength),
        KindTemporalOnly => TemporalFilter(strength),
        _ => SpatialFilter(strength) + "," + TemporalFilter(strength),
    };
}
