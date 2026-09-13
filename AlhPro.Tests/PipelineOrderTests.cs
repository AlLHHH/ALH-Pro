using AlhPro.Core;
using System;
using Xunit;

namespace AlhPro.Tests;

/// <summary>阶段顺序自动判定的单测(任务 Q1)。
/// 判据:新顺序(先超分)更省 ⟺ `u > k·(r_hi − r_lo)/(k−1)`(u=超分单帧成本、k=补帧倍率、
/// r_lo/r_hi=补帧在源分辨率/放大后分辨率的单帧成本)。成本表全部来自 2026-09-13 真机实测(见 Core 注释)。
/// 预期结论(用户给定,本文件逐条钉住):①animevideov3 2x+补帧2x → 旧(新慢约 22%)②同模型 4x+补帧2x → 旧(新慢约 73%)
/// ③x4plus 4x+补帧2x → **新(省约 47%)** ④x4plus-anime 4x+补帧4x → 新 ⑤分辨率变化判据随之变化 ⑥边际不足回退旧 ⑦未知/非法回退旧。</summary>
public class PipelineOrderTests
{
    private const int W1080 = 1920, H1080 = 1080;

    [Fact]
    public void Case1_animevideov3_2x_interp2x_keeps_old_order()
    {
        var d = PipelineOrderPlan.Decide("realesrgan", "realesr-animevideov3", 2.0, 2, W1080, H1080, 1800);
        Assert.False(d.UpscaleFirst);
        Assert.True(d.Measured);
        Assert.Equal(0.2605, d.UpscalePerFrame, 6);          // 实测 0.252~0.269 中值
        Assert.Equal(0.0807, d.InterpLoPerFrame, 6);
        Assert.Equal(0.2776, d.InterpHiPerFrame, 6);         // 2160p 锚点(2x 放大后的面积正好是 2160p)
        Assert.Equal(0.3938, d.ThresholdSecondsPerFrame, 6); // k(r_hi−r_lo)/(k−1)
        Assert.True(d.SavingsPercent < 0);                   // 新顺序更慢
        Assert.InRange(Math.Abs(d.SavingsPercent), 15, 25);  // 用户给的"慢约 22%"
    }

    [Fact]
    public void Case2_animevideov3_4x_interp2x_keeps_old_order_much_slower()
    {
        var d = PipelineOrderPlan.Decide("realesrgan", "realesr-animevideov3", 4.0, 2, W1080, H1080, 1800);
        Assert.False(d.UpscaleFirst);
        Assert.Equal(0.297, d.UpscalePerFrame, 6);
        Assert.Equal(0.5186, d.InterpHiPerFrame, 6);        // 4320p 锚点(4x 放大后的面积)
        Assert.True(d.SavingsPercent < 0);
        Assert.InRange(Math.Abs(d.SavingsPercent), 65, 85); // 用户给的"慢约 73%"
    }

    [Fact]
    public void Case3_x4plus_4x_interp2x_picks_new_order_about_47_percent()
    {
        var d = PipelineOrderPlan.Decide("realesrgan", "realesrgan-x4plus", 4.0, 2, W1080, H1080, 1800);
        Assert.True(d.UpscaleFirst);
        Assert.Equal(15.145, d.UpscalePerFrame, 6);                  // 实测 14.82~15.47(仅 12 帧样本)
        Assert.True(d.UpscalePerFrame > d.ThresholdSecondsPerFrame); // u 超过门槛
        Assert.InRange(d.SavingsPercent, 45, 49);                    // 用户给的"省约 47%"
    }

    [Fact]
    public void Case4_x4plus_anime_4x_interp4x_picks_new_order()
    {
        var d = PipelineOrderPlan.Decide("realesrgan", "realesrgan-x4plus-anime", 4.0, 4, W1080, H1080, 1800);
        Assert.True(d.UpscaleFirst);
        Assert.Equal(3.85, d.UpscalePerFrame, 6);                    // 实测 ≈3.3~4.4 中值
        Assert.Equal(4 * (0.5186 - 0.0807) / 3.0, d.ThresholdSecondsPerFrame, 6);
        Assert.True(d.SavingsPercent > 15);
    }

    /// <summary>分辨率一变,判据里的四个数字全变、结论也可能变(同一模型倍率)。</summary>
    [Theory]
    [InlineData(960, 448, false)]     // 0.43 Mpx:超分很便宜 → 旧顺序
    [InlineData(1920, 1080, false)]   // 2.07 Mpx(基准)→ 旧顺序
    [InlineData(3840, 2160, true)]    // 8.29 Mpx:u 随面积涨到 1.04s,超过门槛 0.482 → **新顺序**
    public void Case5_decision_follows_source_resolution(int w, int h, bool expectNew)
    {
        var d = PipelineOrderPlan.Decide("realesrgan", "realesr-animevideov3", 2.0, 2, w, h, 1800);
        Assert.Equal(expectNew, d.UpscaleFirst);
        double expectedU = 0.2605 * ((double)w * h / PipelineOrderPlan.ReferencePixels1080p);
        Assert.Equal(expectedU, d.UpscalePerFrame, 6);   // 超分成本 ∝ 源面积(与输出倍率几乎无关)
        Assert.Equal((long)w * h, d.SourcePixels);
        // 三种分辨率的判据数字必须两两不同(否则"按分辨率变化"没生效)
        var other = PipelineOrderPlan.Decide("realesrgan", "realesr-animevideov3", 2.0, 2, 1920, 1080, 1800);
        if (w * h != 1920 * 1080) Assert.NotEqual(other.ThresholdSecondsPerFrame, d.ThresholdSecondsPerFrame, 9);
    }

    /// <summary>边际不足:新顺序确实更省,但只省 ~10%(< 15%)→ 仍保持旧顺序(避免临界抖动)。
    /// 用注入成本构造 u = 0.5124(1080p、补帧 2x):<16 见注释推导,节省 ≈10.0%。</summary>
    [Fact]
    public void Case6_insufficient_margin_keeps_old_order()
    {
        var cost = new PipelineOrderPlan.CostInput("synthetic", 2, true, 0.5124);
        var d = PipelineOrderPlan.Decide(cost, 2.0, 2, W1080, H1080, 1800);
        Assert.False(d.UpscaleFirst);
        Assert.True(d.MarginInsufficient);
        Assert.True(d.SavingsPercent > 0 && d.SavingsPercent < PipelineOrderPlan.MinSavingsPercent);
        Assert.Contains("安全边际", d.Reason);
        // 同一成本、把边际要求放到 5% → 就切到新顺序(证明确实是"边际"这条在拦)
        var relaxed = PipelineOrderPlan.Decide(cost, 2.0, 2, W1080, H1080, 1800, minSavingsPercent: 5.0);
        Assert.True(relaxed.UpscaleFirst);
        Assert.False(relaxed.MarginInsufficient);
    }

    [Fact]
    public void Case7_unknown_model_and_invalid_inputs_fall_back_to_old_order()
    {
        var unknown = PipelineOrderPlan.Decide("realesrgan", "some-new-model", 4.0, 2, W1080, H1080, 1800);
        Assert.False(unknown.UpscaleFirst);
        Assert.False(unknown.Measured);
        Assert.Contains("待实测标定", unknown.Reason);

        var noInterp = PipelineOrderPlan.Decide("realesrgan", "realesrgan-x4plus", 4.0, 1, W1080, H1080, 1800);
        Assert.False(noInterp.UpscaleFirst);   // 不补帧:顺序无收益

        // 非法入参不许炸,且一律旧顺序
        foreach (var (w, h, s, k) in new[] { (0, 0, 4.0, 2), (-1, 1080, 4.0, 2), (1920, 1080, 0.0, 2), (1920, 1080, 4.0, 0) })
        {
            var d = PipelineOrderPlan.Decide("realesrgan", "realesr-animevideov3", s, k, w, h, 0);
            Assert.False(d.UpscaleFirst);
        }
    }

    /// <summary>日志一行必须写全"模型/倍率/分辨率 → u、r_lo、r_hi、门槛、节省 → 结论"(用户要求可审计)。</summary>
    [Fact]
    public void Decision_log_line_is_auditable()
    {
        var d = PipelineOrderPlan.Decide("realesrgan", "realesrgan-x4plus", 4.0, 2, W1080, H1080, 1800);
        string line = d.LogLine;
        Assert.Contains("顺序判定:", line);
        Assert.Contains("u=", line);
        Assert.Contains("r_lo=", line);
        Assert.Contains("r_hi=", line);
        Assert.Contains("门槛=", line);
        Assert.Contains("预估节省", line);
        Assert.Contains("新顺序(超分→补帧)", line);
    }

    /// <summary>补帧成本按面积分段线性内插:三个实测锚点必须精确落在表上,且随面积单调不减。</summary>
    [Fact]
    public void Interp_cost_interpolates_the_measured_anchors()
    {
        Assert.Equal(0.0807, PipelineOrderPlan.InterpSecondsPerOutputFrame(1920L * 1080), 6);
        Assert.Equal(0.2776, PipelineOrderPlan.InterpSecondsPerOutputFrame(3840L * 2160), 6);
        Assert.Equal(0.5186, PipelineOrderPlan.InterpSecondsPerOutputFrame(7680L * 4320), 6);
        double mid = PipelineOrderPlan.InterpSecondsPerOutputFrame(1920L * 1080 + (3840L * 2160 - 1920L * 1080) / 2);
        Assert.InRange(mid, 0.0807, 0.2776);
        Assert.True(PipelineOrderPlan.InterpSecondsPerOutputFrame(7680L * 4320 * 4) > 0.5186);   // 超出锚点按末段斜率外推
        Assert.True(PipelineOrderPlan.InterpSecondsPerOutputFrame(1) >= PipelineOrderPlan.MinInterpSecondsPerFrame); // 小图有兜底
    }

    /// <summary>模型名归一(表的键)与"4xplus-anime 必须先于 4xplus 判"这条易错点。</summary>
    [Theory]
    [InlineData("realesr-animevideov3", "animevideov3")]
    [InlineData("realesrgan-x4plus", "x4plus")]
    [InlineData("realesrgan-x4plus-anime", "x4plus-anime")]   // 若先判 x4plus 会误判成 15.145s/帧
    [InlineData("realesr-general-x4v3", "general-x4v3")]
    [InlineData("models-cunet", "cunet")]
    [InlineData("models-upconv_7_photo", "upconv_7_photo")]
    [InlineData("whatever", null)]
    public void Model_key_normalization(string model, string? expected)
        => Assert.Equal(expected, PipelineOrderPlan.NormalizeModel(model));

    [Fact]
    public void X4plus_cost_is_not_reused_for_other_models()
    {
        // 每张表的键(模型+倍率)都必须能查到,且 x4plus 的 15.145 不许被别的模型借走
        Assert.Equal(15.145, PipelineOrderPlan.LookupUpscaleSecondsPerFrame("realesrgan-x4plus", 4, out _)!.Value, 6);
        Assert.Equal(0.297, PipelineOrderPlan.LookupUpscaleSecondsPerFrame("realesr-animevideov3", 4, out _)!.Value, 6);
        Assert.Null(PipelineOrderPlan.LookupUpscaleSecondsPerFrame("realesr-animevideov3", 3, out var prov));   // 3x 没实测
        Assert.Contains("待实测标定", prov);
        // waifu2x cunet 2x(噪声三档中值)+ upconv_7_photo 2x
        Assert.Equal(0.3685, PipelineOrderPlan.LookupUpscaleSecondsPerFrame("models-cunet", 2, out _)!.Value, 6);
        Assert.Equal(1.288, PipelineOrderPlan.LookupUpscaleSecondsPerFrame("models-upconv_7_photo", 2, out _)!.Value, 6);
        // "1x 缩回"的面积与倍率分开传:engine 倍率仍按 2x 查表(areaScale 只影响放大后面积)
        var shrink = PipelineOrderPlan.Decide("realesrgan", "realesrgan-x4plus", 2.0, 2, W1080, H1080, 1800, areaScale: 1.0);
        Assert.Equal(15.145, shrink.UpscalePerFrame, 6);
        Assert.Equal((long)W1080 * H1080, shrink.HiPixels);   // 缩回后补帧输入仍是源尺寸
    }
}
