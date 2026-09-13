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

    // ---------- PlanVideoBatches:把【素材规模】纳入批次决策(2026-09-13) ----------
    // 守护三件事:① 短素材必须单批(不再为几十帧反复启动引擎);② 每批帧数【绝不】超过内存档基准
    // (峰值盘/内存不因"规模修正"上涨);③ fastMode/diskTight 的减半仍然生效。

    [Fact]
    public void PlanVideoBatches_short_clip_single_batch()
    {
        // 唯一帧 60、空闲内存 10.4G(内存基准 240):1 批。每批帧数是"上限",内存基准本来就有富余,
        // 保持 240 不往下收窄(收窄只会白白多切几批);关键是【批数 = 1】——不再为 60 帧启动两次引擎。
        var p = RenderPolicy.PlanVideoBatches(10.4, 60);
        Assert.Equal(1, p.BatchCount);
        Assert.Equal(240, p.BatchSize);
        Assert.Equal(240, p.MemoryBatchSize);
        Assert.True(p.SingleBatchByShortClip);
    }

    [Fact]
    public void PlanVideoBatches_short_clip_low_ram_raises_up_to_cap_only()
    {
        // 空闲内存 1.0G(内存基准 25)但素材只有 100 帧:短素材单批 → 1 批,
        // 每批 100 帧仍在"今天已在用的最大批 240"以内(峰值不超过既有最坏情况)
        var p = RenderPolicy.PlanVideoBatches(1.0, 100);
        Assert.Equal(1, p.BatchCount);
        Assert.Equal(100, p.BatchSize);
        Assert.True(p.BatchSize <= RenderPolicy.MaxFramesPerBatch);
        Assert.True(p.SingleBatchByShortClip);
    }

    [Fact]
    public void PlanVideoBatches_short_clip_cap_is_halved_by_fast_and_disk_flags()
    {
        // 130 帧:正常单批;开了兼容模式(上限 240→120)→ 130 > 120,退回内存基准(120)→ 2 批。
        // 减半必须仍然生效(防爆盘/弱机),不许被"短素材单批"吃掉。
        var normal = RenderPolicy.PlanVideoBatches(10.4, 130);
        Assert.Equal(1, normal.BatchCount);
        var fast = RenderPolicy.PlanVideoBatches(10.4, 130, fastMode: true);
        Assert.Equal(120, fast.BatchSize);
        Assert.Equal(2, fast.BatchCount);
        Assert.True(fast.HalvedForFastMode);
        var both = RenderPolicy.PlanVideoBatches(10.4, 130, fastMode: true, diskTight: true);
        Assert.Equal(60, both.BatchSize);
        Assert.Equal(3, both.BatchCount);
        Assert.True(both.HalvedForFastMode && both.HalvedForDiskTight);
    }

    [Fact]
    public void PlanVideoBatches_long_clip_uses_memory_baseline_no_frame_inflation()
    {
        // 唯一帧 3420、空闲内存 10.4G → 每批 240(=内存基准,不因素材长而放大) → 15 批。
        // 【这条断言就是"不设批数上限"的守护】:批数随素材线性增长是刻意的 —— 限批数只能让每批帧数
        // 变大,同屏临时帧(输入+输出)会跟着涨,峰值盘/内存就守不住了(见 PlanVideoBatches 注释)。
        var p = RenderPolicy.PlanVideoBatches(10.4, 3420);
        Assert.Equal(240, p.BatchSize);
        Assert.Equal(15, p.BatchCount);
        Assert.False(p.SingleBatchByShortClip);
        var tight = RenderPolicy.PlanVideoBatches(10.4, 3420, diskTight: true);
        Assert.Equal(120, tight.BatchSize);
        Assert.Equal(29, tight.BatchCount);
    }

    [Fact]
    public void PlanVideoBatches_never_exceeds_absolute_per_batch_ceiling()
    {
        // 任意内存档 + 任意素材规模:每批帧数都不超过 MaxFramesPerBatch(= 今天内存档的最大批),
        // 也就是"短素材单批豁免"永远不会把同屏临时帧推高到今天已在用的水平之上。
        foreach (var ram in new[] { 0.5, 1.0, 2.0, 3.0, 5.0, 7.0, 9.0, 32.0 })
            foreach (var n in new[] { 0, 1, 60, 240, 241, 1000, 3417, 100_000 })
            {
                var p = RenderPolicy.PlanVideoBatches(ram, n);
                Assert.True(p.BatchSize <= RenderPolicy.MaxFramesPerBatch,
                    $"ram={ram} n={n} → 每批 {p.BatchSize} 帧,超过绝对上限 {RenderPolicy.MaxFramesPerBatch}");
                Assert.True(p.BatchSize >= RenderPolicy.MinFramesPerBatch, $"ram={ram} n={n} → 每批帧数低于下限");
                Assert.True(p.BatchCount >= 1);
                // 批数 × 每批帧数必须覆盖全部唯一帧(切批不许漏帧)
                if (n > 0) Assert.True((long)p.BatchCount * p.BatchSize >= n, $"ram={ram} n={n} 切批覆盖不足");
            }
    }

    [Fact]
    public void PlanVideoBatches_batch_count_is_monotonic_in_frame_count()
    {
        int prev = 0;
        for (int n = 0; n <= 5000; n += 37)
        {
            var p = RenderPolicy.PlanVideoBatches(10.4, n);
            Assert.True(p.BatchCount >= prev, $"帧数增加后批数反而减少了(n={n})");
            prev = p.BatchCount;
        }
    }
}
