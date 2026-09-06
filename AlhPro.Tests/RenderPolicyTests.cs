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
    [InlineData(10.0, 5.0, 240)]   // 空余内存>8G + 显存>4G:最快
    [InlineData(6.0, 3.0, 120)]    // 中档
    [InlineData(4.0, 2.0, 60)]
    [InlineData(2.0, 1.0, 40)]
    [InlineData(1.0, 0.5, 25)]     // 极端紧张
    public void VideoBatchSize_by_free_resources(double freeRam, double freeVram, int expected)
    {
        Assert.Equal(expected, RenderPolicy.VideoBatchSize(freeRam, freeVram));
    }
}
