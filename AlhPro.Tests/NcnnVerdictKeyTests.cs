using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>
/// F1 的钉子:探测结论键必须带"模型"维度,且"能不能用"的汇总规则必须是保守的
/// (任意一支模型失败 → 整条判定为不可用)。
/// 起因:旧键只有 engine|gpu,于是 animevideov3 的"通过"会给 x4plus 背书 7 天。
/// </summary>
public class NcnnVerdictKeyTests
{
    [Fact]
    public void Key_carries_engine_gpu_and_model()
    {
        Assert.Equal("realesrgan2026|0|realesrgan-x4plus", NcnnVerdictKey.For("realesrgan2026", 0, "realesrgan-x4plus"));
    }

    [Fact]
    public void Different_models_on_the_same_card_get_different_keys()
    {
        // 核心回归点:这就是"x4plus 不该被 animevideov3 的结论背书"的落点
        var a = NcnnVerdictKey.For("realesrgan2026", 0, "realesr-animevideov3");
        var b = NcnnVerdictKey.For("realesrgan2026", 0, "realesrgan-x4plus");
        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData("Realesrgan-X4Plus")]
    [InlineData("  realesrgan-x4plus  ")]
    public void Model_is_normalised_so_the_same_model_never_gets_two_keys(string variant)
    {
        // 大小写/空白不同不能产生第二份缓存,否则同一模型会被反复重测
        Assert.Equal(NcnnVerdictKey.For("realesrgan2026", 0, "realesrgan-x4plus"),
                     NcnnVerdictKey.For("realesrgan2026", 0, variant));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_model_means_engine_level_verdict(string? model)
    {
        // waifu2x 没有"模型"维度(模型文件由引擎自行选择),键的第三段留空
        Assert.Equal("waifu2x|0|", NcnnVerdictKey.For("waifu2x", 0, model));
    }

    [Fact]
    public void Prefix_does_not_leak_between_gpu_ids()
    {
        // ⚠ 陷阱:没有末尾的 '|' 时 "realesrgan2026|1" 会匹配到 "realesrgan2026|10|…"
        Assert.True(NcnnVerdictKey.BelongsTo("realesrgan2026|1|realesrgan-x4plus", "realesrgan2026", 1));
        Assert.False(NcnnVerdictKey.BelongsTo("realesrgan2026|10|realesrgan-x4plus", "realesrgan2026", 1));
        Assert.True(NcnnVerdictKey.BelongsTo("realesrgan2026|10|realesrgan-x4plus", "realesrgan2026", 10));
    }

    [Fact]
    public void Prefix_does_not_leak_between_engines()
    {
        Assert.False(NcnnVerdictKey.BelongsTo("waifu2x|0|", "realesrgan2026", 0));
        Assert.True(NcnnVerdictKey.BelongsTo("waifu2x|0|", "waifu2x", 0));
    }

    [Fact]
    public void Summarize_is_null_when_nothing_measured()
    {
        Assert.Null(NcnnVerdictKey.Summarize(new bool[0]));
        Assert.Null(NcnnVerdictKey.Summarize(null));
    }

    [Fact]
    public void Summarize_is_true_only_when_every_measured_model_passed()
    {
        Assert.True(NcnnVerdictKey.Summarize(new[] { true }));
        Assert.True(NcnnVerdictKey.Summarize(new[] { true, true, true }));
    }

    [Fact]
    public void Summarize_is_false_if_any_measured_model_failed_regardless_of_order()
    {
        // 保守方向:一支失败就整条不可用 —— 否则"x4plus 出坏帧"会被"animevideov3 通过"盖过去
        Assert.False(NcnnVerdictKey.Summarize(new[] { true, false }));
        Assert.False(NcnnVerdictKey.Summarize(new[] { false, true, true }));
        Assert.False(NcnnVerdictKey.Summarize(new[] { false }));
    }
}
