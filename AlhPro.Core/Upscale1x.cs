namespace AlhPro.Core;

/// <summary>**1x 修复档的模型条目**。
/// 【历史】2026-09-21 用户定案加过两条(动漫 · Anime4K 修复 / 现实 · 1x 修复);
/// **2026-09-27 用户定案:"还有1x 那个 第二个现实档先删掉吧"** ⇒ 下拉里只剩「动漫 · Anime4K 修复」一条。
/// 【2026-10-14:它内部用的自训权重也被删了】原来「现实 · 1x 修复」= 用自训的 alhreal2x 按 2x 跑再缩回,
/// 那支权重已按用户要求删除 ⇒ 回退路径改指官方 <c>realesr-animevideov3</c>(见 <see cref="RealEngineModel"/>)。
/// <see cref="RealTag"/> 与 <see cref="RealEngineModel"/> 仍被两条**活路径**用着,所以留着:
///   ① 老存档/老预设里可能还带着 Tag `alhpro-real1x`(它没有对应的引擎权重,必须排除在 ncnn 模型判定之外,
///      见 <see cref="NcnnModelVerdicts.NonNcnnModels"/> 与 <see cref="NcnnProbePlan"/>);
///   ② Anime4K 着色器不可用时的回退路径要拿一个**真权重**下发给引擎(否则引擎按 `-n anime4k` 找不到权重,
///      静默出一张坏帧、exit=0 —— 本仓库踩过)。
///
/// 【界面上怎么区分】选到 1x 时,超分模型下拉里**只有 1x 修复项可选**,其余放大模型一律置灰
/// (用户 2026-09-21:"选倍率后 不支持的模型就灰掉" —— 只灰 1x 这一档,2x/3x/4x 保持原状不减能力)。</summary>
public static class Upscale1x
{
    /// <summary>「现实 · 1x 修复」的历史 Tag。它**不是真模型**:内部用 <see cref="RealEngineModel"/> 按 2x 跑,再缩回原尺寸。
    /// 【为什么删了下拉项还要认它】老存档/老预设里存过这个 Tag,日志与提示要能认出它是"1x 修复"而不是某个放大模型。</summary>
    public const string RealTag = "alhpro-real1x";

    /// <summary>回退路径真正下发给超分引擎的模型(原生 2x)。
    /// 【2026-10-14 换人】原来是自训的「现实 · alhreal2x」,那支权重已按用户要求删除 ⇒ 改用官方
    /// <c>realesr-animevideov3</c>。
    /// 【为什么是这一支】官方 realesrgan 里唯一带**原生 2x** 权重的模型:general-x4v3 / wdn-x4v3 / x4plus /
    /// x4plus-anime 都只有 4x 权重,拿它们跑回退等于"整趟 4x 只为缩回 1x",纯浪费。
    /// 回退的语义本来就是"按 2x 超分后缩回原尺寸",与原来完全一致。
    /// 【它没有 1x 权重】下发 `-s 1` 会静默输出全黑帧(exit=0),所以引擎倍数由
    /// <see cref="EngineScalePolicy.Decide"/> 固定在 2、再由上层缩回 1x。</summary>
    public const string RealEngineModel = "realesr-animevideov3";

    /// <summary>1x 档的条目(Tag);界面据此判断"哪些项在 1x 下可选"。
    /// Rev11 起现实那条已下线,2026-10-14 删模型后仍是这一条。</summary>
    public static readonly string[] All = { Anime4k.ModelTag };

    /// <summary>这个 Tag 是不是"1x 修复"条目。</summary>
    public static bool Is1xEntry(string? tag) => tag is not null && Array.IndexOf(All, tag) >= 0;

    /// <summary>该 Tag 在 1x 档要不要跑 Anime4K 着色器(历史那条走 2x 超分再缩回)。</summary>
    public static bool UsesAnime4kShader(string? tag) => tag == Anime4k.ModelTag;
}
