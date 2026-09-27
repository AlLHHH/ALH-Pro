using System;
using Xunit;

namespace AlhPro.Tests;

/// <summary>Real-CUGAN 的**预计时间**必须按实测口径算:它的成本几乎与倍率无关(2x→3x 约 +6%、4x 约 +11%),
/// 而不是像其它引擎那样按倍率线性放大。
///
/// 【背景 · 2026-09-27 实测】1080p、40 帧、保守档、含引擎启动:2x = 2.21、3x = 2.35、4x = 2.46 秒/帧
/// (算力主要花在输入分辨率的特征提取上,最后那步上采样占比很小)——
/// 见 `docs\实测-RealCUGAN-3x4x单价-20260927.md`。
/// 旧的 `per *= max(0.5, scale)` 把 1.65(这个常数**本身就是 1080p 2x 的实测价**)再乘一遍倍率 ⇒
/// 4x 的预计时间高估约 2.7 倍(是偏**悲观** —— 早前把这条说成"偏乐观",此处一并更正)。
///
/// 【为什么只改 realcugan】其它引擎没有这样测过 ⇒ 保持原口径一个字不动,不拿没测过的数字去改别人的 ETA。</summary>
public class RealCuganEtaTests
{
    private static double Eta(double scale, string engine) =>
        AlhPro.Core.VideoPipeline.EstimateProcessSeconds(
            duration: 10, fps: 30, w: 1920, h: 1080,
            up: true, scale: scale, engine: engine,
            interp: false, interpScale: 1, dedup: false, videoDenoise: 0);

    [Fact]
    public void Real_cugan_eta_does_not_grow_linearly_with_the_upscale_factor()
    {
        double x2 = Eta(2, "realcugan");
        double x4 = Eta(4, "realcugan");
        double ratio = x4 / x2;
        Assert.True(ratio > 1.02 && ratio < 1.25,
            $"realcugan 的 4x/2x 预计时间比值应≈1.11(实测口径),实际 {ratio:0.###}");
    }

    [Fact]
    public void Real_cugan_eta_at_three_x_is_only_slightly_above_two_x()
    {
        double ratio = Eta(3, "realcugan") / Eta(2, "realcugan");
        Assert.True(ratio > 1.01 && ratio < 1.15, $"3x/2x 应≈1.06,实际 {ratio:0.###}");
    }

    [Fact]
    public void Other_engines_keep_the_old_linear_factor()
    {
        // 其它引擎没被测过 ⇒ 保持"按倍率放大"的旧口径(4x 的 per 是 2x 的两倍)
        double ratio = Eta(4, "realesrgan") / Eta(2, "realesrgan");
        Assert.True(ratio > 1.6, $"其它引擎应保持旧口径(按倍率),实际比值 {ratio:0.###}");
    }

    [Fact]
    public void Real_cugan_is_still_the_slowest_of_the_three()
    {
        Assert.True(Eta(2, "realcugan") > Eta(2, "realesrgan"), "realcugan 仍应比 realesrgan 慢");
        Assert.True(Eta(2, "realesrgan") > Eta(2, "waifu2x"), "realesrgan 仍应比 waifu2x 慢");
    }
}
