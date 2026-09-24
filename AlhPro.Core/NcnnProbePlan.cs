namespace AlhPro.Core;

/// <summary>【2026-09-24】"界面模型条目 → 该不该做 ncnn 探测、用哪个模型名去探"的唯一换算(纯逻辑,可单测)。
///
/// 【为什么必须有这一层 · 真机实测挖出来的性能回归】界面上的模型下拉里,Tag **不一定**是超分引擎认识的
/// 权重名:
///   · `anime4k`(= <see cref="Anime4k.ModelTag"/>)——「动漫 · Anime4K 修复(快)」:实际走 ffmpeg + libplacebo
///     **着色器**,完全不经过 ncnn;
///   · `alhpro-real1x`(= <see cref="Upscale1x.RealTag"/>)——「现实 · 1x 修复(中)」:它自己也写明**不是真模型**,
///     真正下发给引擎的是 <see cref="Upscale1x.RealEngineModel"/>(自训现实 2x,再缩回原尺寸)。
/// 而视频开跑前的"显卡兼容性预检"曾把**当前选中的 Tag 原样**当模型名交给 `EnsureNcnnProbeAsync`
/// ⇒ ncnn 被要求加载一个不存在的权重 ⇒ 探 60 秒无响应被强杀 ⇒ 落一条 `realesrgan2026|0|&lt;Tag&gt; = false`
/// 的**假失败结论**。在没有"引擎级(default)结论"的机器上(如"独显 + 核显"⇒ 不走免探测快速通道、
/// 每次探测都带模型 ⇒ 从不写 default),这条假失败就是整条 Real-ESRGAN 的判据
/// ⇒ **所有** 2x/3x/4x 视频超分被推到 ONNX 慢路(实测 3880 ms/帧,而软件标称 0.26~0.30 秒/帧)。
///
/// 【换算规则(与界面的判定逐条对应)】
///   ① 走着色器的条目(anime4k)⇒ **不探测**(该批根本不用 ncnn;探测只会白等一分钟并写脏结论);
///   ② "1x 现实"条目 ⇒ 用 <see cref="Upscale1x.RealEngineModel"/> 探(与真正下发给引擎的模型一致);
///   ③ 其余(官方三支 / 自训三支 / waifu2x 各档)⇒ 原样透传。
///
/// ⚠ 这条换算同时用于"探测"与"下发给引擎"两处,别再各写一份 —— 两处不一致就会出现
/// "探测说可用、引擎却拿不到权重"(exit 0 只出坏帧,本仓库踩过)。
/// </summary>
public static class NcnnProbePlan
{
    /// <summary>一次预检该怎么做的决定。
    /// <paramref name="ShouldProbe"/>=false ⇒ 跳过探测(<paramref name="Model"/> 无意义);
    /// <paramref name="Why"/> 是给日志/排障用的一句话。</summary>
    public readonly record struct Plan(bool ShouldProbe, string? Model, string Why);

    /// <summary>按界面的模型 Tag 得出探测计划(见类型注释的三条规则)。</summary>
    public static Plan For(string? uiTag)
    {
        // ① 着色器条目:不经 ncnn,不探测(避免"探一个不存在的模型"⇒ 60 秒超时 ⇒ 脏结论)
        if (Upscale1x.UsesAnime4kShader(uiTag))
            return new Plan(false, null, $"{Anime4k.ModelTag} 走着色器(不经 ncnn),跳过探测");

        // ② 1x 现实:Tag 不是真模型,换成真正下发给引擎的那支去探
        if (uiTag == Upscale1x.RealTag)
            return new Plan(true, Upscale1x.RealEngineModel, $"1x 现实的真实权重={Upscale1x.RealEngineModel}");

        // ③ 其余(含 null / 空):原样 —— null 表示"探引擎默认模型"
        return new Plan(true, uiTag, string.IsNullOrEmpty(uiTag) ? "未指定模型 ⇒ 探引擎默认模型" : "原样透传");
    }
}
