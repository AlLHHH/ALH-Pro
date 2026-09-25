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

    // ═══════════ 【2026-09-25 · 用户要求:悬停提示末尾那行绿字「权重 Nx」的唯一来源】═══════════

    /// <summary>`NativeWeightLabel` 逐条取值(与合同一一对应)。
    /// 【为什么要有这个函数】各支模型的原生权重倍率不同(animevideov3 = 2/3/4x、x4plus 系 = 只有 4x、
    /// 自训两支 = 只有 2x、waifu2x 三支 = 2x);倍率与权重不一致时上层用重采样补齐 —— **那不是超分**,
    /// 所以要把这件事统一印在悬停提示里。</summary>
    [Theory]
    // Real-CUGAN:models-se 三档都同时具备 up2x/up3x/up4x
    [InlineData("realcugan", "models-se:-1", "权重 2x / 3x / 4x")]
    [InlineData("realcugan", "models-se:0", "权重 2x / 3x / 4x")]
    [InlineData("realcugan", "models-se:3", "权重 2x / 3x / 4x")]
    // Real-ESRGAN:1x 修复(2x 跑再缩回)/ 4x-only 系 / 只有 2x 权重的自训 / animevideov3
    [InlineData("realesrgan", "alhpro-fix1x", "权重 2x(缩回 1x)")]
    [InlineData("realesrgan", "realesrgan-x4plus", "权重 4x")]
    [InlineData("realesrgan", "realesrgan-x4plus-anime", "权重 4x")]
    [InlineData("realesrgan", "realesr-general-x4v3", "权重 4x")]
    [InlineData("realesrgan", "realesr-general-wdn-x4v3", "权重 4x")]
    [InlineData("realesrgan", "alhpro-real2x", "权重 2x")]
    [InlineData("realesrgan", "alhpro-game2x", "权重 2x")]
    [InlineData("realesrgan", "alhpro-game2x-v2", "权重 2x")]
    [InlineData("realesrgan", "alhpro-game2x-v3", "权重 2x")]
    [InlineData("realesrgan", "realesr-animevideov3", "权重 2x / 3x / 4x")]
    [InlineData("realesrgan", "realesr-animevideov3-x2", "权重 2x / 3x / 4x")]   // 名字判定与 NormalizeModel 同源
    // waifu2x:随包三支都是 2x 权重
    [InlineData("waifu2x", "models-cunet", "权重 2x")]
    [InlineData("waifu2x", "models-upconv_7_photo", "权重 2x")]
    [InlineData("waifu2x", "models-upconv_7_anime_style_art_rgb", "权重 2x")]
    // 认不出 / 不适用 ⇒ **空串**(调用方据此不加绿字行)
    [InlineData("waifu2x", "models-unknown", "")]
    [InlineData("realesrgan", "some-new-model", "")]
    [InlineData("realesrgan", "alhpro-real1x", "")]     // 界面上那项的 Tag 不是权重模型名(它自己写死一行绿字)
    [InlineData("anime4k", "anime4k", "")]              // Anime4K 走着色器:没有权重文件
    [InlineData("", "", "")]
    [InlineData("realesrgan", "", "")]
    [InlineData(null!, null!, "")]
    public void Native_weight_label_matches_the_contract(string? engine, string? model, string expected)
        => Assert.Equal(expected, EngineScalePolicy.NativeWeightLabel(engine!, model!));

    /// <summary>只要给了绿字,文字就必须以「权重」或「着色器」开头 —— 界面那行是**提醒**,
    /// 不能变成一句没有信息量的话(界面上写死的两项自己负责,这里只管函数的输出)。</summary>
    [Fact]
    public void Native_weight_label_is_either_empty_or_a_weight_line()
    {
        string[] engines = { "realesrgan", "waifu2x", "realcugan", "anime4k", "other", "" };
        string[] models = { "realesr-animevideov3", "realesrgan-x4plus", "realesr-general-x4v3",
                            "realesr-general-wdn-x4v3", "alhpro-real2x", "alhpro-game2x-v2", "alhpro-game2x-v3",
                            "alhpro-fix1x", "models-cunet", "models-upconv_7_photo",
                            "models-upconv_7_anime_style_art_rgb", "models-se:-1", "models-se:0", "models-se:3",
                            "anime4k", "alhpro-real1x", "whatever", "" };
        foreach (var e in engines)
            foreach (var m in models)
            {
                string s = EngineScalePolicy.NativeWeightLabel(e, m);
                if (s.Length == 0) continue;
                Assert.StartsWith("权重", s);
                Assert.DoesNotContain("&#x0a;", s);       // XAML 里那行换行由调用方拼,函数只管文字
                Assert.DoesNotContain("Foreground", s);   // 颜色也不归函数管
            }
    }

    /// <summary>与 `Decide` 的判据必须**同源**:够 4x-only 的模型在两边都得按 4x 处理/提示;
    /// 只有 2x 权重的自训模型两边都得说 2x。任一处"各说各话"就直接红。</summary>
    [Fact]
    public void Native_weight_label_agrees_with_the_engine_scale_decision()
    {
        // 4x-only 系:Decide 固定按 4 跑 ⇒ 提示必须是「权重 4x」
        foreach (var m in new[] { "realesrgan-x4plus", "realesrgan-x4plus-anime", "realesr-general-x4v3", "realesr-general-wdn-x4v3" })
        {
            Assert.Equal(4, EngineScalePolicy.Decide("realesrgan", m, 2.0).EngineScale);
            Assert.Equal("权重 4x", EngineScalePolicy.NativeWeightLabel("realesrgan", m));
        }
        // 只有 2x 权重的自训:目标 2x 时引擎倍数就是 2 ⇒ 提示「权重 2x」
        foreach (var m in new[] { "alhpro-real2x", "alhpro-game2x-v2", "alhpro-game2x-v3" })
        {
            Assert.Equal(2, EngineScalePolicy.Decide("realesrgan", m, 2.0).EngineScale);
            Assert.Equal("权重 2x", EngineScalePolicy.NativeWeightLabel("realesrgan", m));
        }
        // animevideov3:三档都有权重 ⇒ 目标 2/3/4 直接下发,提示写三档
        Assert.Equal(3, EngineScalePolicy.Decide("realesrgan", "realesr-animevideov3", 3.0).EngineScale);
        Assert.Equal("权重 2x / 3x / 4x", EngineScalePolicy.NativeWeightLabel("realesrgan", "realesr-animevideov3"));
        // Real-CUGAN:三档权重齐 ⇒ 目标 3x 下发 3
        Assert.Equal(3, EngineScalePolicy.Decide("realcugan", "models-se:0", 3.0).EngineScale);
        Assert.Equal("权重 2x / 3x / 4x", EngineScalePolicy.NativeWeightLabel("realcugan", "models-se:0"));
    }
}
