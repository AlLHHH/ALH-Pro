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
                            "realesr-general-x4v3", "unknown-model" };
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
    public void RealEsrgan_scale_is_pinned(string model, double want, int expectEngine, double expectRatio)
    {
        var d = EngineScalePolicy.Decide("realesrgan", model, want);
        Assert.Equal(expectEngine, d.EngineScale);
        Assert.Equal(expectRatio, d.ShrinkRatio, 6);
        Assert.Equal(Math.Abs(expectRatio - 1.0) > 1e-6, d.NeedsShrinkBack);
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

    /// <summary>静默全黑判定:输出缺陷 + 源帧正常 = 引擎故障;源帧本来就是黑场(片头/夜景)= 放行,不误杀。</summary>
    [Theory]
    [InlineData(false, true, true)]     // 源正常、输出全黑 → 引擎静默故障
    [InlineData(true, true, false)]     // 源本来就是黑场 → 不算故障
    [InlineData(false, false, false)]   // 输出正常
    [InlineData(true, false, false)]    // 源黑、输出不黑(降噪/提亮) → 不是故障
    public void Silent_black_detection_matches_the_rule(bool inputBlack, bool outputBlack, bool expected)
        => Assert.Equal(expected, FrameInspect.IsSilentBlackFailure(inputBlack, outputBlack));
}
