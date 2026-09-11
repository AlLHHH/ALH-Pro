using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>
/// 安全渲染策略(显存/GPU类别 → 分块大小 & 批大小)的单测。
/// 这是"改错就爆显存/黑帧/卡死"的逻辑,必须保护。
/// </summary>
public class RenderPolicyTests
{
    [Theory]
    [InlineData(2.0, GpuCategory.Nvidia, 256)]
    [InlineData(3.9, GpuCategory.Nvidia, 256)]
    [InlineData(3.9, GpuCategory.Amd, 256)]
    [InlineData(3.9, GpuCategory.Blackwell, 256)]
    public void VideoTileSize_low_vram_always_conservative(double vram, GpuCategory cat, int expected)
    {
        Assert.Equal(expected, RenderPolicy.VideoTileSize(vram, cat));
    }

    [Theory]
    [InlineData(8.0, GpuCategory.Nvidia, 640)]
    [InlineData(12.0, GpuCategory.Nvidia, 768)]
    [InlineData(6.0, GpuCategory.Nvidia, 512)]
    public void VideoTileSize_nvidia_by_vram(double vram, GpuCategory cat, int expected)
    {
        Assert.Equal(expected, RenderPolicy.VideoTileSize(vram, cat));
    }

    [Theory]
    [InlineData(12.0, GpuCategory.Blackwell, 640)]
    [InlineData(6.0, GpuCategory.Blackwell, 512)]
    [InlineData(12.0, GpuCategory.Amd, 640)]
    [InlineData(6.0, GpuCategory.Amd, 512)]
    public void VideoTileSize_blackwell_and_amd_conservative(double vram, GpuCategory cat, int expected)
    {
        Assert.Equal(expected, RenderPolicy.VideoTileSize(vram, cat));
    }

    [Theory]
    [InlineData(6.0, GpuCategory.Other, 512)]
    [InlineData(10.0, GpuCategory.Other, 640)]
    [InlineData(16.0, GpuCategory.Other, 768)]
    public void VideoTileSize_unknown_gpu_generic(double vram, GpuCategory cat, int expected)
    {
        Assert.Equal(expected, RenderPolicy.VideoTileSize(vram, cat));
    }

    [Theory]
    [InlineData(8.0, 6.0, 1024)]     // 实测锚点:8GB 卡(自动模式墙 6.0)→ 1024 最优且未溢出
    [InlineData(7.996, 6.0, 1024)]   // 【回归】真机值:8GB 卡实际报 8188MiB=7.996GB —— 阈值必须是"档位"不是精确 8
    [InlineData(16.0, 12.0, 1024)]   // 更大显存不继续放大:1024 已饱和,再大一旦溢出会静默慢十余倍
    [InlineData(24.0, 18.0, 1024)]
    [InlineData(12.0, 9.0, 1024)]
    [InlineData(8.0, 3.0, 640)]      // 用户主动把显存墙收紧 → 听用户的
    [InlineData(8.0, 4.5, 768)]
    [InlineData(6.0, 4.5, 768)]
    [InlineData(6.0, 3.0, 640)]
    [InlineData(4.0, 3.0, 640)]
    [InlineData(2.0, 1.5, 512)]
    public void OnnxTileSize_takes_lower_of_total_and_wall(double total, double wall, int expected)
        => Assert.Equal(expected, RenderPolicy.OnnxTileSize(total, wall));

    [Theory]
    [InlineData(64.0, 48.0)]
    [InlineData(16.0, 12.0)]
    [InlineData(8.0, 6.0)]
    public void OnnxTileSize_integrated_always_conservative(double total, double wall)
    {
        // 核显用共享内存:报出来的"显存总量"不是真实可用量,不许按它放大分块
        Assert.Equal(512, RenderPolicy.OnnxTileSize(total, wall, integrated: true));
    }

    [Fact]
    public void OnnxTileSize_never_exceeds_the_measured_safe_optimum()
    {
        // 上限必须是实测安全的 1024 —— 再大在本机实测中会【静默慢 13.7~20 倍】(显存溢出,不报错)。
        // 这条断言的作用:将来有人想"大显存就放大"时,必须先有真机数据,并先改掉这条测试。
        for (double v = 1; v <= 64; v += 0.5)
            Assert.InRange(RenderPolicy.OnnxTileSize(v, v * 0.75), 512, 1024);
    }

    [Fact]
    public void OnnxTileSize_is_non_decreasing_in_both_dimensions()
    {
        int prev = 0;
        for (double v = 1; v <= 64; v += 0.5)
        {
            int t = RenderPolicy.OnnxTileSize(v, v * 0.75);
            Assert.True(t >= prev, $"显存 {v}GB 的分块({t})小于更小显存的取值({prev})");
            prev = t;
        }
        // 墙更小时不得更大(单调性)
        for (double wall = 1; wall <= 12; wall += 0.5)
            Assert.True(RenderPolicy.OnnxTileSize(16, wall) <= RenderPolicy.OnnxTileSize(16, wall + 0.5));
    }

    [Theory]
    [InlineData(10.0, 240)]   // 空余内存 >8G:最快
    [InlineData(8.5, 240)]
    [InlineData(8.0, 180)]    // 档位边界:=8 属中档
    [InlineData(6.0, 120)]
    [InlineData(4.0, 60)]
    [InlineData(2.0, 40)]
    [InlineData(1.0, 25)]     // 极端紧张
    public void VideoBatchSize_by_free_ram(double freeRam, int expected)
    {
        Assert.Equal(expected, RenderPolicy.VideoBatchSize(freeRam));
    }
}
