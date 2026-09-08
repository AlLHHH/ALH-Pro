using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>
/// 帧质量判定(从 ConvertPngToJpg / IsBlackPng 抽出的阈值逻辑)的单测。
/// 这是出过回归的纯函数(commit 3ec571c),必须保护。
/// </summary>
public class FrameInspectTests
{
    [Fact]
    public void Near_black_detected()
    {
        // 全黑:RGB 和 <24,应判近黑
        var sums = new[] { 9, 9, 9, 9, 9, 9, 9, 9, 9, 9 };
        Assert.True(FrameInspect.IsNearBlack(sums, sums.Length));
    }

    [Fact]
    public void Ordinary_frame_not_black()
    {
        // 中等灰/彩色:RGB 和高于 24,不判黑
        var sums = new[] { 300, 300, 300, 300, 300, 300, 300, 300, 300, 300 };
        Assert.False(FrameInspect.IsNearBlack(sums, sums.Length));
    }

    [Fact]
    public void Threshold_at_95_percent()
    {
        // ≥95% 黑才判黑;90% 黑不判
        var n = 100;
        var ninetyDark = new int[n];
        for (int i = 0; i < 90; i++) ninetyDark[i] = 9;   // 黑
        for (int i = 90; i < n; i++) ninetyDark[i] = 300; // 非黑
        Assert.False(FrameInspect.IsNearBlack(ninetyDark, n));

        var ninetyFiveDark = new int[n];
        for (int i = 0; i < 95; i++) ninetyFiveDark[i] = 9;
        for (int i = 95; i < n; i++) ninetyFiveDark[i] = 300;
        Assert.True(FrameInspect.IsNearBlack(ninetyFiveDark, n));
    }

    [Fact]
    public void Empty_sample_not_black()
    {
        // 无采样点 → 不判黑(避免 1×1 小图误判)
        Assert.False(FrameInspect.IsNearBlack(System.Array.Empty<int>(), 0));
        Assert.False(FrameInspect.IsNearBlack(new[] { 9, 9 }, 0));
    }

    [Fact]
    public void Sample_step_for_tiny_images()
    {
        // 1×1 / 小图:步长至少 4,保证能取到部分像素
        Assert.Equal(4, FrameInspect.SampleStep(1, 1));
        Assert.Equal(4, FrameInspect.SampleStep(10, 10));
        Assert.Equal(4, FrameInspect.SampleStep(100, 100));
        // 大图:步长 = min(w,h)/32
        Assert.True(FrameInspect.SampleStep(1920, 1080) >= 33);
        Assert.True(FrameInspect.SampleStep(1920, 1080) <= 60);
    }

    [Fact]
    public void ForEachSample_iterates_grid()
    {
        // 10×10 → 步长 4,采样点 x,y ∈ {4,8}:共 2×2=4 点
        int count = FrameInspect.ForEachSample(10, 10, (_, _) => { });
        Assert.Equal(4, count);

        // 8×8 → 步长 4,{4}:1×1=1 点
        count = FrameInspect.ForEachSample(8, 8, (_, _) => { });
        Assert.Equal(1, count);
    }
}
