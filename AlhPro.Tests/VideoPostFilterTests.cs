using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>视频后处理滤镜链构造的单测。
/// 【为什么钉这么死】这条链直接决定成片画面,而 ffmpeg 侧的每一档都是【不同机制、不同参数】;
/// 一旦有人"顺手合并/近似",画面就变了却不会报错。这里把每一档产出的滤镜字符串【逐字】固定下来,
/// 并显式断言"四档各自独立、顺序固定、没有被合并"(2026-09-13 用户问到"能不能合并",结论见
/// AlhPro.Core.VideoPostFilters 类注释:不能 —— 会改画面)。</summary>
public class VideoPostFilterTests
{
    // 官方预设一「通用画质增强 不含补帧」:锐化20 清晰25 钝化35 细节40,AA 已按用户要求清零
    private const string Preset1 =
        "smartblur=luma_radius=1:luma_strength=-0.20:luma_threshold=3," +
        "unsharp=13:13:0.13:13:13:0," +
        "smartblur=luma_radius=2:luma_strength=-0.35:luma_threshold=8," +
        "cas=strength=0.24";

    // 官方预设二「动漫通用」:锐化20 清晰25 钝化40 细节40,AA 清零(与预设一只差钝化强度)
    private const string Preset2 =
        "smartblur=luma_radius=1:luma_strength=-0.20:luma_threshold=3," +
        "unsharp=13:13:0.13:13:13:0," +
        "smartblur=luma_radius=2:luma_strength=-0.40:luma_threshold=8," +
        "cas=strength=0.24";

    [Fact]
    public void Preset1_chain_is_pinned()
    {
        // 25/100×0.50 = 0.125 → 固定两位小数(与迁移前同一份公式,逐字不变)
        Assert.Equal(0.125, 25 / 100.0 * 0.50, 9);
        Assert.Equal(Preset1, VideoPostFilters.Build(20, 25, 35, 40, aa: 0));
    }

    [Fact]
    public void Preset2_chain_is_pinned()
    {
        Assert.Equal(0.40, 40 / 100.0, 9);
        Assert.Equal(Preset2, VideoPostFilters.Build(20, 25, 40, 40, aa: 0));
    }

    [Fact]
    public void Four_stages_stay_four_independent_filters()
    {
        var chain = VideoPostFilters.Build(20, 25, 35, 40, 0)!;
        // 两档 smartblur 半径不同(1 与 2)、阈值不同;一趟 unsharp;一趟 cas —— 四档不许被合并成一档
        Assert.Equal(2, CountOf(chain, "smartblur="));
        Assert.Equal(1, CountOf(chain, "unsharp="));
        Assert.Equal(1, CountOf(chain, "cas="));
        Assert.Contains("luma_radius=1", chain);
        Assert.Contains("luma_radius=2", chain);
        Assert.Contains("luma_threshold=3", chain);
        Assert.Contains("luma_threshold=8", chain);
        // 顺序固定:锐化 → 清晰 → 钝化蒙版 → 保留细节
        Assert.True(chain.IndexOf("luma_radius=1", System.StringComparison.Ordinal)
                  < chain.IndexOf("unsharp=", System.StringComparison.Ordinal));
        Assert.True(chain.IndexOf("unsharp=", System.StringComparison.Ordinal)
                  < chain.IndexOf("luma_radius=2", System.StringComparison.Ordinal));
        Assert.True(chain.IndexOf("luma_radius=2", System.StringComparison.Ordinal)
                  < chain.IndexOf("cas=", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Aa_parameter_produces_no_filter()
    {
        // 边缘抗锯齿自 2026-09-12 起改走 C# 并行实现(见 EngineService.ApplyEdgeSmoothToJpeg):
        // 无论传多少,这条链都不该多出滤镜 —— 也绝不该回到 ffmpeg 的 sab。
        Assert.Equal(VideoPostFilters.Build(20, 25, 35, 40, 0), VideoPostFilters.Build(20, 25, 35, 40, 45));
        Assert.Equal(VideoPostFilters.Build(20, 25, 35, 40, 0), VideoPostFilters.Build(20, 25, 35, 40, 100));
        Assert.DoesNotContain("sab", VideoPostFilters.Build(20, 25, 35, 40, 100)!);
    }

    [Fact]
    public void All_zero_means_no_filter_chain()
    {
        Assert.Null(VideoPostFilters.Build(0, 0, 0, 0));
        Assert.Null(VideoPostFilters.Build(0, 0, 0, 0, 45));   // 只开 AA 时链仍然为空(AA 不在 ffmpeg 链里)
    }

    [Fact]
    public void Only_enabled_stages_are_emitted()
    {
        Assert.Equal("unsharp=13:13:0.13:13:13:0,cas=strength=0.24", VideoPostFilters.Build(0, 25, 0, 40));
        Assert.Equal("smartblur=luma_radius=1:luma_strength=-0.50:luma_threshold=3", VideoPostFilters.Build(50, 0, 0, 0));
    }

    [Theory]
    [InlineData(100, "-1.00", 6)]   // 超上限钳到 1.00;>60 走阈值 6
    [InlineData(200, "-1.00", 6)]
    [InlineData(1, "-0.01", 3)]
    public void Sharpen_strength_is_capped(int value, string expected, int threshold)
    {
        Assert.Equal($"smartblur=luma_radius=1:luma_strength={expected}:luma_threshold={threshold}",
            VideoPostFilters.Build(value, 0, 0, 0));
    }

    [Theory]
    [InlineData(60, 3)]   // ≤60 用 3
    [InlineData(61, 6)]   // >60 用 6
    [InlineData(100, 6)]
    public void Sharpen_threshold_switches_at_60(int value, int threshold)
    {
        Assert.Contains($"luma_threshold={threshold}", VideoPostFilters.Build(value, 0, 0, 0)!);
    }

    [Fact]
    public void Clarity_usm_detail_are_capped()
    {
        // 清晰 ≤0.50、钝化 ≤1.00、细节 ≤0.60(与迁移前同一批上限)
        Assert.Equal("unsharp=13:13:0.50:13:13:0", VideoPostFilters.Build(0, 100, 0, 0));
        Assert.Equal("unsharp=13:13:0.50:13:13:0", VideoPostFilters.Build(0, 500, 0, 0));
        Assert.Equal("smartblur=luma_radius=2:luma_strength=-1.00:luma_threshold=8", VideoPostFilters.Build(0, 0, 100, 0));
        Assert.Equal("cas=strength=0.60", VideoPostFilters.Build(0, 0, 0, 100));
        Assert.Equal("cas=strength=0.60", VideoPostFilters.Build(0, 0, 0, 999));
    }

    [Fact]
    public void Chroma_strength_of_unsharp_stays_zero()
    {
        // unsharp 的色度强度必须保持 0:它是"只锐化亮度"的关键(第 6 个参数,不是阈值)
        Assert.EndsWith(":13:13:0", VideoPostFilters.Build(0, 25, 0, 0));
    }

    private static int CountOf(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, System.StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }
}
