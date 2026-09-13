using AlhPro.Core;
using System;
using Xunit;

namespace AlhPro.Tests;

/// <summary>临时空间预估的单测(任务 O3:补帧改"引擎直出 JPG"后单帧体积变大,预估必须跟着改)。
/// 【真机数字】119 帧:PNG 18.650s vs 直出 JPG 8.353s(省 86.5 ms/帧);单帧 **238 KB → 737 KB(3.1×)**;
/// 直出 JPG 对 PNG 的 PSNR 48.907 dB(比程序内 q0.96 转换还高 1.56 dB)。</summary>
public class TempSpaceEstimateTests
{
    [Fact]
    public void Source_frame_size_scales_with_area()
    {
        Assert.Equal(1.0, TempSpaceEstimate.SourceFrameMegabytes(1920, 1080), 6);
        Assert.Equal(4.0, TempSpaceEstimate.SourceFrameMegabytes(3840, 2160), 6);
        Assert.Equal(TempSpaceEstimate.MinFrameMb, TempSpaceEstimate.SourceFrameMegabytes(16, 16), 6);   // 下限保护
        Assert.Equal(TempSpaceEstimate.MinFrameMb, TempSpaceEstimate.SourceFrameMegabytes(0, 0), 6);
    }

    /// <summary>补帧开着时必须把引擎直出 JPG 的 3.1× 算进去(否则峰值帧体积低估 3 倍 —— 那正是"跑出 200 多 G"的教训)。
    /// 注意"不低于源帧大小"的下限:倍率小时下限会盖住系数,所以用 2x 放大(缩放项明显高于下限)来验证。</summary>
    [Fact]
    public void Engine_direct_jpg_factor_applies_only_when_interpolating()
    {
        // 1080p 源(1 MB/帧)× 2x 放大:不补帧 = max(1, 1×4×0.18) = 1.0;补帧 = 1×4×0.18×3.1 = 2.232
        double without = TempSpaceEstimate.PeakFrameMegabytes(1920, 1080, 2.0, frameInterp: false);
        double withInterp = TempSpaceEstimate.PeakFrameMegabytes(1920, 1080, 2.0, frameInterp: true);
        Assert.Equal(Math.Max(1.0, 1.0 * 4 * 0.18), without, 6);
        Assert.Equal(1.0 * 4 * 0.18 * 3.1, withInterp, 6);
        Assert.True(withInterp > without);                                    // 方向:补帧只会更占盘
        Assert.Equal(3.1, TempSpaceEstimate.EngineDirectJpgFactor, 6);        // 系数本身钉住(实测 238→737 KB)
    }

    [Fact]
    public void Peak_frame_size_is_monotonic_and_floored()
    {
        double small = TempSpaceEstimate.PeakFrameMegabytes(640, 360, 1.0, true);
        double big = TempSpaceEstimate.PeakFrameMegabytes(1920, 1080, 2.0, true);
        Assert.True(big > small);
        Assert.True(TempSpaceEstimate.PeakFrameMegabytes(1920, 1080, 0, true) > 0);      // 非法倍率兜底
        Assert.True(TempSpaceEstimate.PeakFrameMegabytes(1920, 1080, double.NaN, true) > 0);
        Assert.True(TempSpaceEstimate.PeakFrameMegabytes(16, 16, 1.0, false) >= TempSpaceEstimate.MinFrameMb);
    }

    [Fact]
    public void Need_bytes_includes_the_safety_factor()
    {
        double b = TempSpaceEstimate.NeedBytes(1000, 2.0);
        Assert.Equal(1000 * 2.0 * 1024 * 1024 * TempSpaceEstimate.SafetyFactor, b, 3);
        Assert.Equal(0, TempSpaceEstimate.NeedBytes(0, 2.0), 6);
        Assert.Equal(0, TempSpaceEstimate.NeedBytes(-5, 2.0), 6);
    }
}
