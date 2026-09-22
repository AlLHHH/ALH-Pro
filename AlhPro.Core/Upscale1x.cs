namespace AlhPro.Core;

/// <summary>**1x 修复档的模型条目**(2026-09-21 用户定案:"1x 也是可以选模型 加一个现实的1x模型")。
///
/// 【为什么 1x 也要有模型可选】1x 的价值是"不变尺寸、只做修复/锐化",而**动漫向**与**实拍向**需要的处理不一样:
///   · 动漫/游戏:线条与平涂为主 ⇒ Anime4K 着色器最对症(实测边宽 6.76→5.96px、纹理不丢);
///   · 现实/实拍:噪点与自然纹理为主 ⇒ 用**自训的现实模型按 2x 跑再缩回**(即本仓库原来的 1x 做法)反而更稳 ——
///     2026-09-21 三素材对比实测:干净素材上它比 Anime4K **更接近原片**(PSNR 36.8 对 35.8dB)、噪点更低(1.54× 对 1.85×)。
/// ⇒ 所以 1x 档现在有两个模型条目,分别对应这两类素材;二者都**不放大**(输出尺寸 = 输入尺寸)。
///
/// 【界面上怎么区分】选到 1x 时,超分模型下拉里**只有这两项是可选的**,其余放大模型一律置灰
///   (用户 2026-09-21:"选倍率后 不支持的模型就灰掉" —— 只灰 1x 这一档,2x/3x/4x 保持原状不减能力)。</summary>
public static class Upscale1x
{
    /// <summary>「现实 · 1x 修复」的 Tag。它**不是真模型**:内部用 <see cref="RealEngineModel"/>(自训现实 2x)按 2x 跑,再缩回原尺寸。</summary>
    public const string RealTag = "alhpro-real1x";

    /// <summary>这个条目真正下发给超分引擎的模型(自训的「现实 · alhreal2x」,原生 2x)。</summary>
    public const string RealEngineModel = ExperimentalEsrgan.Real2x;

    /// <summary>下拉项文字(与 XAML 逐字一致,有单测钉住;风格沿用 `类别 · 名字（速度档）`)。
    /// 【速度档:中 · 2026-09-21 用户指出】原来写的是「快」——但它内部要跑**完整一遍 2x 超分**(本机约 0.35 秒/帧 @1080p),
    /// 而 Anime4K 那条是 15 FPS(约 0.07 秒/帧)⇒ 现实这条慢约 5 倍,标「快」是错的,已改「中」。</summary>
    public const string RealMenuText = "现实 · 1x 修复（中）";

    /// <summary>下拉项悬停提示(与 XAML 逐字一致,有单测钉住)。</summary>
    public const string RealTooltip =
        "现实 · 1x 修复:**画面尺寸不变、不放大**。内部用自训的「现实 · alhreal2x」按 2x 超分,再精确缩回原尺寸。&#x0a;" +
        "实测(2026-09-21 三素材对比):干净素材上它比 Anime4K **更接近原片**(PSNR 36.8 对 35.8dB)、噪点更低(1.54× 对 1.85×);" +
        "有压缩损伤/偏软的素材上则相反。&#x0a;⚠ 速度标「中」是有依据的:它要跑**完整一遍 2x 超分**(本机约 0.35 秒/帧 @1080p),而 Anime4K 那条约 0.07 秒/帧 ⇒ 慢约 5 倍。";

    /// <summary>1x 档的两个条目(Tag);界面据此判断"哪些项在 1x 下可选"。</summary>
    public static readonly string[] All = { Anime4k.ModelTag, RealTag };

    /// <summary>这个 Tag 是不是"1x 修复"条目。</summary>
    public static bool Is1xEntry(string? tag) => tag is not null && Array.IndexOf(All, tag) >= 0;

    /// <summary>该 Tag 在 1x 档要不要跑 Anime4K 着色器(另一个条目走 2x 超分再缩回)。</summary>
    public static bool UsesAnime4kShader(string? tag) => tag == Anime4k.ModelTag;
}
