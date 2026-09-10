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

    // ===== 黑帧降级的"防误杀"判定 =====
    // 产品铁律「绝不把黑帧写进输出」在历史上被这条判定整批绕过:
    // 旧实现用的是【存在量词】(目录里任一源帧近黑 → 豁免整批),
    // 于是含黑场的素材(片头黑场/淡入淡出/夜戏/闪黑)上,GPU 真正故障产出的黑帧会整批放行,且零日志。
    // 现在必须是"每一帧的源帧都近黑"才豁免。
    [Fact]
    public void Exempt_only_when_every_defective_frame_comes_from_black_source()
    {
        // 全部源帧都近黑 → 输出黑来自素材,可豁免(不浪费 CPU 重算)
        Assert.True(FrameInspect.ShouldExemptAsSourceBlack(new[] { true, true, true }));
        Assert.True(FrameInspect.ShouldExemptAsSourceBlack(new[] { true }));
    }

    [Fact]
    public void Single_non_black_source_forbids_exemption()
    {
        // 这是历史 bug 的核心:64 帧里 63 帧源黑、只有 1 帧源不黑 —— 那一帧就是 GPU 故障,必须降级。
        // 旧实现("存在量词")会因为那 63 帧而豁免整批,把这一帧的黑帧写进成片。
        var flags = new bool[64];
        for (int i = 0; i < 63; i++) flags[i] = true;
        flags[63] = false;
        Assert.False(FrameInspect.ShouldExemptAsSourceBlack(flags));

        // 反向:只有第一帧源黑、其余都不是 → 同样不豁免
        var flags2 = new bool[64];
        flags2[0] = true;
        Assert.False(FrameInspect.ShouldExemptAsSourceBlack(flags2));

        // 全部都不是黑场源 → 明确的 GPU 故障,必须降级
        Assert.False(FrameInspect.ShouldExemptAsSourceBlack(new[] { false, false }));
    }

    [Fact]
    public void Empty_defective_set_never_exempts()
    {
        // 引擎一帧都没输出(空批)= 真故障,与素材内容无关 → 必须降级
        Assert.False(FrameInspect.ShouldExemptAsSourceBlack(null));
        Assert.False(FrameInspect.ShouldExemptAsSourceBlack(System.Array.Empty<bool>()));
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
