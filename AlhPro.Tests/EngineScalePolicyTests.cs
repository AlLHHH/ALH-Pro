using AlhPro.Core;
using System;
using System.Collections.Generic;
using Xunit;

namespace AlhPro.Tests;

/// <summary>超分引擎"实际下发倍数"与"静默全黑"判定的单测(任务 O1)。
/// 【真机依据】Real-ESRGAN 对 `realesr-animevideov3` 传 `-s 1` → 输出纯黑(mean=0.00/std=0.00/uniq=1,
/// 两张不同帧复现)且 **exit=0 无报错**,stdout 里只有 `_wfopen models/realesr-animevideov3-x1.param failed`
/// —— 模型目录实测只有 x2/x3/x4 三套权重(x4plus / general-x4v3 / x4plus-anime 同样没有 x1)。
/// 这里把"绝不返回 1"钉成不变量,并钉住各模型 × 各目标倍数的下发倍数与缩放比。</summary>
public class EngineScalePolicyTests
{
    /// <summary>核心不变量:Real-ESRGAN 的判定**永远不返回 1**(返回 1 = 下发 -s 1 = 静默全黑)。</summary>
    [Fact]
    public void RealEsrgan_never_gets_engine_scale_one()
    {
        string[] models = { "realesr-animevideov3", "realesrgan-x4plus", "realesrgan-x4plus-anime",
                            "realesr-general-x4v3", "realesr-general-wdn-x4v3", "unknown-model",
                            "alhpro-real2x", "alhpro-game2x", "alhpro-game2x-v2", "alhpro-game2x-v3" };   // 末四支 = 自训 2x(Rev4 + Rev5 + Rev6)
        for (double want = 0.25; want <= 4.001; want += 0.25)
            foreach (var m in models)
            {
                var d = EngineScalePolicy.Decide("realesrgan", m, want);
                Assert.True(d.EngineScale >= 2, $"模型 {m} 目标 {want}x 得到引擎倍数 {d.EngineScale}(会下发 -s 1 → 全黑)");
                Assert.False(EngineScalePolicy.ModelHasNative1x("realesrgan", m));
            }
    }

    [Theory]
    [InlineData("realesr-animevideov3", 1.0, 2, 0.5)]    // 目标 1x → 2x 放大后缩回(护栏)
    [InlineData("realesr-animevideov3", 0.5, 2, 0.25)]   // 目标比 1x 还小 → 同样走 2x(缩得更多)
    [InlineData("realesr-animevideov3", 2.0, 2, 1.0)]    // 原生 2x 权重:直接 2x,不缩放
    [InlineData("realesr-animevideov3", 3.0, 3, 1.0)]    // 原生 3x 权重
    [InlineData("realesr-animevideov3", 4.0, 4, 1.0)]
    [InlineData("realesr-animevideov3", 1.5, 2, 0.75)]   // 非原生倍数 → 2x 后缩回
    [InlineData("realesrgan-x4plus", 1.0, 4, 0.25)]      // 只有 4x 权重:必须按原生 4x 跑
    [InlineData("realesrgan-x4plus", 2.0, 4, 0.5)]
    [InlineData("realesrgan-x4plus-anime", 3.0, 4, 0.75)]
    [InlineData("realesr-general-x4v3", 2.0, 4, 0.5)]
    // 【2026-09-14 自转的 wdn-x4v3】名字不含 "general-x4v3",若漏登记就会被当成普通模型 → 目标 2x 时下发 -s 2,
    // 实测那条路径的输出与双三次 PSNR 只有 ~14 dB(官方 general-x4v3 走 -s 2 同样 13.92 dB)= 坏路径。
    // 这三条就是钉住"它必须按原生 4x 跑再缩回"。
    [InlineData("realesr-general-wdn-x4v3", 2.0, 4, 0.5)]
    [InlineData("realesr-general-wdn-x4v3", 4.0, 4, 1.0)]
    [InlineData("realesr-general-wdn-x4v3", 1.0, 4, 0.25)]
    // 【2026-09-15 Rev4 · 自训的两支实验模型:只有 **2x** 原生权重】
    // 实测(220×220 输入、-t 0、`-m models -n alhpro-real2x`):-s 2 → 440×440 正常;
    // -s 4 → 880×880 但成图是**镜像平铺的错帧**(切成 2×2 镜像重复、下半幅偏黄),与"2x 后双三次放大"
    // 的 PSNR 只有 7.27 dB —— 引擎按目标倍数贴回分块,而网络只放大 2x。**exit=0、零报错**。
    // ⇒ 这几条就是钉住"它们永远按原生 2x 跑,再由上层缩放到目标尺寸"。
    [InlineData("alhpro-real2x", 2.0, 2, 1.0)]    // 原生 2x:正常路径,不缩回
    [InlineData("alhpro-real2x", 3.0, 2, 1.5)]    // 目标 3x → 按 2x 跑再放大
    [InlineData("alhpro-real2x", 4.0, 2, 2.0)]    // 目标 4x → 按 2x 跑再放大(下发 -s 4 会得到错帧)
    [InlineData("alhpro-real2x", 1.0, 2, 0.5)]    // 目标 1x → 2x 后缩回
    [InlineData("alhpro-game2x", 2.0, 2, 1.0)]
    [InlineData("alhpro-game2x", 4.0, 2, 2.0)]
    public void RealEsrgan_scale_is_pinned(string model, double want, int expectEngine, double expectRatio)
    {
        var d = EngineScalePolicy.Decide("realesrgan", model, want);
        Assert.Equal(expectEngine, d.EngineScale);
        Assert.Equal(expectRatio, d.ShrinkRatio, 6);
        Assert.Equal(Math.Abs(expectRatio - 1.0) > 1e-6, d.NeedsShrinkBack);
    }

    /// <summary>自训 2x 模型:原生 2x 是正常路径(不刷日志);3x/4x 才给"为什么改了倍数"的理由,
    /// 且理由要说**实测到的那件事**(镜像平铺的错帧),不许搬 x4plus 的"全黑"来充数。</summary>
    [Fact]
    public void X2_only_models_rewrite_the_scale_with_an_honest_reason()
    {
        foreach (var m in new[] { "alhpro-real2x", "alhpro-game2x" })
        {
            Assert.Equal("", EngineScalePolicy.Decide("realesrgan", m, 2.0).Reason);
            var d4 = EngineScalePolicy.Decide("realesrgan", m, 4.0);
            Assert.Contains("2x 原生权重", d4.Reason);
            Assert.Contains("镜像平铺", d4.Reason);
            Assert.DoesNotContain("全黑", d4.Reason);   // 这两支在 3x/4x 上实测的是错帧,不是全黑
            Assert.True(EngineScalePolicy.IsX2OnlyModel(m));
            Assert.False(EngineScalePolicy.Is4xOnlyModel(m));   // 与 4x 系判定互斥
        }
        // 官方模型不能被误判进 2x 单独那一档
        Assert.False(EngineScalePolicy.IsX2OnlyModel("realesr-animevideov3"));
        Assert.False(EngineScalePolicy.IsX2OnlyModel("realesrgan-x4plus"));
        Assert.False(EngineScalePolicy.IsX2OnlyModel(null));
    }

    /// <summary>1x 护栏必须给出可读理由(用户要看得懂"为什么改了倍数"),2x/4x 正常路径不要噪音。</summary>
    [Fact]
    public void Guard_reason_is_present_only_when_it_rewrote_the_scale()
    {
        var guarded = EngineScalePolicy.Decide("realesrgan", "realesr-animevideov3", 1.0);
        Assert.Contains("1x 权重", guarded.Reason);
        Assert.Contains("全黑", guarded.Reason);
        Assert.Equal("", EngineScalePolicy.Decide("realesrgan", "realesr-animevideov3", 2.0).Reason);
        Assert.Equal("", EngineScalePolicy.Decide("realesrgan", "realesrgan-x4plus", 4.0).Reason);
    }

    /// <summary>waifu2x 的口径不变(取不小于目标的最大 2 的幂;它的 1x 特例依赖 noise,由调用方处理)。</summary>
    [Theory]
    [InlineData(1.0, 1)]     // 1x:调用方按 noise<0 复制 / 否则改 2x(既有行为,本策略不掺和)
    [InlineData(2.0, 2)]
    [InlineData(3.0, 4)]
    [InlineData(4.0, 4)]
    [InlineData(1.5, 2)]
    public void Waifu2x_keeps_the_old_power_of_two_rule(double want, int expect)
    {
        var d = EngineScalePolicy.Decide("waifu2x", "models-cunet", want);
        Assert.Equal(expect, d.EngineScale);
        Assert.Equal("", d.Reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(double.NaN)]
    public void Invalid_requested_scale_does_not_crash(double want)
    {
        var d = EngineScalePolicy.Decide("realesrgan", "realesr-animevideov3", want);
        Assert.True(d.EngineScale >= 2);
        Assert.True(d.ShrinkRatio > 0);
    }

    /// <summary>**1x 修复模型(同尺寸)**的倍率:1x 目标是它的原生用法 ⇒ 引擎倍数 1、不缩放、不留理由。
    /// 【为什么允许引擎倍数 1】上面那条"绝不返回 1"守的是"**没有 x1 权重**的模型传 -s 1 会静默全黑";
    /// 这一支的网络尾层就是 3 通道、倍率写死 1 ⇒ -s 1 正常(上线前必须真机跑一次确认非黑 + 尺寸不变)。</summary>
    [Fact]
    public void Fix1x_model_runs_natively_at_scale_one()
    {
        var d = EngineScalePolicy.Decide("realesrgan", ExperimentalEsrgan.Fix1x, 1.0);
        Assert.Equal(1, d.EngineScale);
        Assert.Equal(1.0, d.ShrinkRatio, 6);
        Assert.False(d.NeedsShrinkBack);
        Assert.Equal("", d.Reason);                                  // 原生路径不刷日志
        Assert.True(EngineScalePolicy.ModelHasNative1x("realesrgan", ExperimentalEsrgan.Fix1x));
        Assert.True(ExperimentalEsrgan.Is1xModel(ExperimentalEsrgan.Fix1x));
        // 其它模型仍然没有原生 1x ⇒ 那条"绝不返回 1"的护栏不受影响
        foreach (var m in ExperimentalEsrgan.All)
        {
            Assert.False(EngineScalePolicy.ModelHasNative1x("realesrgan", m));
            Assert.False(ExperimentalEsrgan.Is1xModel(m));
        }
    }

    /// <summary>拿「1x 修复模型」去放大:不能悄悄放大(它只会被普通放大),必须给出可读的理由让用户换模型。</summary>
    [Fact]
    public void Fix1x_model_refuses_to_upscale_with_a_readable_reason()
    {
        var d = EngineScalePolicy.Decide("realesrgan", ExperimentalEsrgan.Fix1x, 2.0);
        Assert.Equal(1, d.EngineScale);
        Assert.Equal(2.0, d.ShrinkRatio, 6);
        Assert.Contains("不能放大", d.Reason);
        Assert.Contains("1x 修复", d.Reason);
    }

    /// <summary>**1x 目标 + 2x/4x 模型**这条路的实测结论必须写进理由里(用户要能看懂"为什么 1x 会更糊")。
    /// 实测(素材(13) 第 20 秒):2x 超分再缩回 1080p = detail 822 / 边宽 5.86px,原片 = 1152 / 7.02px。</summary>
    [Fact]
    public void One_x_target_with_a_2x_model_states_the_measured_softness()
    {
        var d = EngineScalePolicy.Decide("realesrgan", ExperimentalEsrgan.Game2xV2, 1.0);
        Assert.Equal(2, d.EngineScale);
        Assert.Equal(0.5, d.ShrinkRatio, 6);
        Assert.Contains("比原片更软", d.Reason);
        Assert.Contains("1x 修复", d.Reason);      // 给出出路:换 1x 修复模型
    }

    [Theory]
    [InlineData(false, true, true)]     // 源正常、输出全黑 → 引擎静默故障
    [InlineData(true, true, false)]     // 源本来就是黑场 → 不算故障
    [InlineData(false, false, false)]   // 输出正常
    [InlineData(true, false, false)]    // 源黑、输出不黑(降噪/提亮) → 不是故障
    public void Silent_black_detection_matches_the_rule(bool inputBlack, bool outputBlack, bool expected)
        => Assert.Equal(expected, FrameInspect.IsSilentBlackFailure(inputBlack, outputBlack));
}
