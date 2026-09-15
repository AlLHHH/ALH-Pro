using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>「边缘增强」档(2026-09-15 新增,用户要求:模型边缘糊 → 按模型给 0.3 / 0.6)。
/// 实测依据(游戏帧 1080p→2x,记录在 VideoPostFilters.Build 的注释里):
///   animevideov3 边缘宽度 2.23→2.15px、强边缘对比 52.3→58.0(+11%),过冲 1.05%→1.40%;
///   其它模型用 0.6:2.24→2.16px、56.6→69.3(+22%),过冲 1.23%→1.96%。
/// 这里把"参数 → 滤镜链"的映射钉住,防止以后有人改阈值/半径把实测标定的档位悄悄换掉。</summary>
public class VideoEdgeBoostTests
{
    [Fact]
    public void Zero_edge_boost_produces_no_filter() 
        => Assert.Null(VideoPostFilters.Build(0, 0, 0, 0, 0, 0));

    [Fact]
    public void Edge_boost_only_chain_is_the_thresholded_smartblur()
        => Assert.Equal("smartblur=luma_radius=1:luma_strength=-0.30:luma_threshold=8",
                        VideoPostFilters.Build(0, 0, 0, 0, 0, 30));

    [Theory]
    [InlineData(30, "-0.30")]   // v3 推荐档
    [InlineData(60, "-0.60")]   // 其它模型推荐档
    [InlineData(100, "-1.00")]
    [InlineData(500, "-1.00")]  // 越界钳到 1.00
    public void Edge_boost_strength_mapping(int value, string strength)
    {
        var chain = VideoPostFilters.Build(0, 0, 0, 0, 0, value);
        Assert.NotNull(chain);
        Assert.Contains($"luma_strength={strength}", chain!);
        Assert.Contains("luma_radius=1", chain!);
        Assert.Contains("luma_threshold=8", chain!);   // 阈值 8 = 只动明确边缘(实测关键)
    }

    [Fact]
    public void Edge_boost_comes_after_detail_and_composes()
    {
        var chain = VideoPostFilters.Build(0, 0, 0, 100, 0, 60)!;
        Assert.Equal("cas=strength=0.60,smartblur=luma_radius=1:luma_strength=-0.60:luma_threshold=8", chain);
        Assert.EndsWith("luma_threshold=8", chain);
    }

    /// <summary>与"锐化"档的区别必须保留:锐化阈值 3/6、边缘增强阈值 8 —— 两者同时开是两条独立滤镜。</summary>
    [Fact]
    public void Edge_boost_is_distinct_from_sharpen()
    {
        var both = VideoPostFilters.Build(50, 0, 0, 0, 0, 60)!;
        Assert.Contains("luma_strength=-0.50:luma_threshold=3", both);   // 锐化
        Assert.Contains("luma_strength=-0.60:luma_threshold=8", both);   // 边缘增强
    }

    /// <summary>aa 形参仍然不进 ffmpeg 链(历史口径不变),加 edgeBoost 不许把它带回来。</summary>
    [Fact]
    public void Edge_boost_does_not_reintroduce_the_aa_filter()
    {
        var chain = VideoPostFilters.Build(0, 0, 0, 0, 100, 30)!;
        Assert.DoesNotContain("sab", chain);
        Assert.Equal("smartblur=luma_radius=1:luma_strength=-0.30:luma_threshold=8", chain);
    }
}
