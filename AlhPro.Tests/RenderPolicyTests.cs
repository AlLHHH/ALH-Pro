using AlhPro.Core;
using System;
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
    // 【2026-09-23 档位 ×2】中/高档数字再翻一倍(300/300/700 → 600/600/1400)。
    // 这一列是"内存压力"的展示与兜底口径,真正决定每批帧数的是 PlanVideoBatches(内存档 × 性能档)。
    [InlineData(10.0, 1400)]  // 空余内存 >8G:最快档(= StrongDeviceLargeFramesPerBatch)
    [InlineData(8.5, 1400)]
    [InlineData(8.0, 600)]    // 档位边界:=8 属中档(= NormalDeviceFramesPerBatch)
    [InlineData(6.0, 600)]
    [InlineData(4.0, 60)]     // 内存压力档:≤4G 仍按 60 保底(不受档位调整影响)
    [InlineData(2.0, 40)]
    [InlineData(1.0, 25)]     // 极端紧张
    public void VideoBatchSize_by_free_ram(double freeRam, int expected)
    {
        Assert.Equal(expected, RenderPolicy.VideoBatchSize(freeRam));
    }

    // ---------- PlanVideoBatches:设备档位 + 视频长度 + 补帧后总帧数(2026-09-13 用户口径) ----------
    // 守护用户给的四条:① 设备正常/好 + 短素材 → 不分批;② 设备好 + 视频长 → 批内扩大到 400;
    // ③ 设备好 + 视频不长 → 200;④ 设备差 → 最低 50 一批(全档位下界,减半也不许破)。

    [Theory]
    [InlineData(0.5, RenderPolicy.DeviceTier.Weak)]
    [InlineData(3.9, RenderPolicy.DeviceTier.Weak)]
    [InlineData(4.0, RenderPolicy.DeviceTier.Normal)]     // 边界:4G 属正常
    [InlineData(7.9, RenderPolicy.DeviceTier.Normal)]
    [InlineData(8.0, RenderPolicy.DeviceTier.Strong)]     // 边界:8G 属好(与既有内存档同一条线)
    [InlineData(32.0, RenderPolicy.DeviceTier.Strong)]
    public void TierFor_uses_free_ram_boundaries(double freeRam, RenderPolicy.DeviceTier expected)
    {
        Assert.Equal(expected, RenderPolicy.TierFor(freeRam));
    }

    [Fact]
    public void PlanVideoBatches_short_clip_on_normal_or_good_device_is_single_batch()
    {
        // 用户口径:设备正常/好 + 视频短 + 补帧后帧数少 → 完全不分批。
        // 5s×30fps=150 帧、不补帧 → 补帧后总帧数 150 ≤ 800 → 1 批(设备正常/好都成立)
        var normal = RenderPolicy.PlanVideoBatches(6.0, 150, 150);
        Assert.Equal(1, normal.BatchCount);
        Assert.True(normal.SingleBatch);
        Assert.Equal(RenderPolicy.DeviceTier.Normal, normal.Tier);
        var strong = RenderPolicy.PlanVideoBatches(16.0, 150, 150);
        Assert.Equal(1, strong.BatchCount);
        Assert.True(strong.SingleBatch);
        // 边界:补帧后恰为 800 → 仍单批;801 → 不再单批(【2026-09-23 档位 ×2】400 → 800)
        Assert.True(RenderPolicy.PlanVideoBatches(16.0, 100, 800).SingleBatch);
        Assert.False(RenderPolicy.PlanVideoBatches(16.0, 101, 801).SingleBatch);
    }

    [Fact]
    public void PlanVideoBatches_weak_device_keeps_user_floor_of_80()
    {
        // 设备差:每批 80(全档位下界;【2026-09-23 档位 ×2】50 → 80)。设备差不享受"短素材单批"
        // (用户只对"设备正常"说了不分批),只按 80 帧/批切 —— 200 帧 → 3 批(⌈200/80⌉)。
        var weak = RenderPolicy.PlanVideoBatches(2.0, 200, 200);
        Assert.Equal(RenderPolicy.DeviceTier.Weak, weak.Tier);
        Assert.Equal(RenderPolicy.WeakDeviceFramesPerBatch, weak.BatchSize);
        Assert.Equal(3, weak.BatchCount);
        Assert.False(weak.SingleBatch);
    }

    [Fact]
    public void PlanVideoBatches_strong_device_short_and_long_thresholds()
    {
        // 设备好 + 视频不长(源 <900 且补帧后 <1200)→ StrongDeviceFramesPerBatch(=700)
        var shortish = RenderPolicy.PlanVideoBatches(16.0, 300, 600);
        Assert.Equal(RenderPolicy.StrongDeviceFramesPerBatch, shortish.BatchSize);
        Assert.Equal(1, shortish.BatchCount);                       // ⌈600/700⌉ = 1(补齐后 ≤800 本来就是单批)
        // 设备好 + 视频长(按补帧后总帧数命中:真机那条 855 源帧 → 补帧后 3420)→ 1400 帧/批
        var longByVolume = RenderPolicy.PlanVideoBatches(16.0, 855, 3420);
        Assert.Equal(RenderPolicy.StrongDeviceLargeFramesPerBatch, longByVolume.BatchSize);
        Assert.Equal(3, longByVolume.BatchCount);                   // 【档位 ×2】⌈3420/1400⌉(旧 ⌈3420/700⌉=5)
        // 设备好 + 视频长(按时长命中:源帧数 ≥900)
        var longBySource = RenderPolicy.PlanVideoBatches(16.0, 900, 900);
        Assert.Equal(RenderPolicy.StrongDeviceLargeFramesPerBatch, longBySource.BatchSize);
    }

    [Fact]
    public void PlanVideoBatches_normal_device_uses_600_baseline()
    {
        // 【2026-09-23 档位 ×2】设备正常 300 → 600 帧/批;素材够长时不走单批豁免。
        // 性能档未给 → 回退纯内存档。
        var p120 = RenderPolicy.PlanVideoBatches(5.0, 2000, 4000);
        Assert.Equal(RenderPolicy.NormalDeviceFramesPerBatch, p120.BatchSize);
        Assert.Equal(7, p120.BatchCount);                           // ⌈4000/600⌉
        var p180 = RenderPolicy.PlanVideoBatches(7.5, 2000, 4000);
        Assert.Equal(RenderPolicy.NormalDeviceFramesPerBatch, p180.BatchSize);
        Assert.Equal(7, p180.BatchCount);
    }

    [Fact]
    public void PlanVideoBatches_halving_keeps_the_floor_of_80()
    {
        // fastMode/diskTight 减半保护保留:700→350、1400→700、600→300
        // 【样本要挑"不会被单批豁免覆盖"的】豁免门槛抬到 800 之后,(16G, 300 源, 600 补帧后)整片一批
        // ⇒ batch = max(350, 600) = 600,减半被豁免覆盖 —— 那不是减半失效,是豁免优先(见 RenderPolicy 优先级)。
        // 所以这里用补帧后 900 帧:900 > 800 不走豁免,源 300 < 900 也不算长片 → 700 → 减半 350。
        Assert.Equal(350, RenderPolicy.PlanVideoBatches(16.0, 300, 900, fastMode: true).BatchSize);
        Assert.Equal(700, RenderPolicy.PlanVideoBatches(16.0, 855, 3420, fastMode: true).BatchSize);
        Assert.Equal(300, RenderPolicy.PlanVideoBatches(7.5, 2000, 4000, diskTight: true).BatchSize);
        Assert.Equal(150, RenderPolicy.PlanVideoBatches(7.5, 2000, 4000, diskTight: true, fastMode: true).BatchSize);   // 300→150
        // ⚠ 冲突点:设备差基准 80,减半(40)被下界挡住 → 该档减半不生效(报告里已如实说明)
        var weakFast = RenderPolicy.PlanVideoBatches(2.0, 500, 500, fastMode: true);
        Assert.Equal(RenderPolicy.WeakDeviceFramesPerBatch, weakFast.BatchSize);
        Assert.True(weakFast.HalvedForFastMode);
        // 单批豁免在减半之后生效:补帧后 400 帧在设备好档上仍是单批(豁免按补帧后总帧数判)
        Assert.True(RenderPolicy.PlanVideoBatches(16.0, 300, 400, fastMode: true).SingleBatch);
    }

    [Fact]
    public void PlanVideoBatches_invariants_hold_for_every_tier_and_size()
    {
        // 不变量(任意档位/任意规模/任意模式都成立):
        //  ① 每批帧数 ≥ 用户下界 50(减半也不许破);
        //  ② 每批帧数 ≤ 用户给的最大批 400;
        //  ③ 批数 ≥ 1,且 批数 × 每批帧数 ≥ 补帧后总帧数(切批不许漏帧)。
        foreach (var ram in new[] { 0.5, 1.0, 3.9, 4.0, 5.0, 7.9, 8.0, 16.0, 64.0 })
            foreach (var src in new[] { 0, 1, 60, 240, 400, 900, 899, 5000 })
                foreach (var post in new[] { 0, 1, 72, 400, 401, 1200, 1199, 3420, 100_000 })
                {
                    var p = RenderPolicy.PlanVideoBatches(ram, src, post);
                    Assert.True(p.BatchSize >= RenderPolicy.WeakDeviceFramesPerBatch,
                        $"ram={ram} src={src} post={post} → 每批 {p.BatchSize} 帧,低于用户下界 50");
                    Assert.True(p.BatchSize <= RenderPolicy.StrongDeviceLargeFramesPerBatch,
                        $"ram={ram} src={src} post={post} → 每批 {p.BatchSize} 帧,超过最大批 {RenderPolicy.StrongDeviceLargeFramesPerBatch}(【T】400 → 700)");
                    Assert.True(p.BatchCount >= 1);
                    int effPost = Math.Max(src, post);
                    if (effPost > 0)
                        Assert.True((long)p.BatchCount * p.BatchSize >= effPost,
                            $"ram={ram} src={src} post={post} 切批覆盖不足");
                }
    }

    [Fact]
    public void PlanVideoBatches_batch_count_is_monotonic_within_each_rule_branch()
    {
        // 【口径变了,断言跟着改】旧口径是"批数随帧数单调不减";用户新口径里**故意**存在台阶:
        // 设备好档上源帧数跨过 900(或补帧后跨过 1200)= "视频长" → 每批从 200 扩到 400,批数会**下降**
        // (这正是"批内扩大"的目的:减少引擎进程启动次数)。所以单调性只能在同一条规则分支内断言。
        // 设备好(10.4G)、补帧后=源帧数:
        //  · 401..899 帧:200 帧/批 → 批数随帧数单调不减;
        //  · 900..:400 帧/批 → 同样单调不减。
        int prevA = 0;
        // 【2026-09-23 档位 ×2】单批豁免的门槛抬到 800 ⇒ 第一条分支要从 801 起(401..800 都是单批)
        for (int n = 801; n < 900; n += 11)
        {
            var p = RenderPolicy.PlanVideoBatches(10.4, n, n);
            Assert.Equal(RenderPolicy.StrongDeviceFramesPerBatch, p.BatchSize);
            Assert.True(p.BatchCount >= prevA, $"700/批 分支内批数回退(n={n})");
            prevA = p.BatchCount;
        }
        int prevB = 0;
        for (int n = 900; n <= 6000; n += 37)
        {
            var p = RenderPolicy.PlanVideoBatches(10.4, n, n);
            Assert.Equal(RenderPolicy.StrongDeviceLargeFramesPerBatch, p.BatchSize);
            Assert.True(p.BatchCount >= prevB, $"1400/批 分支内批数回退(n={n})");
            prevB = p.BatchCount;
        }
    }

    [Fact]
    public void PlanVideoBatches_long_clip_stepdown_is_intentional()
    {
        // 钉住这条"台阶"本身,免得以后有人当 bug 修掉:899 帧 → 2 批(700/批);900 帧 → 1 批(1400/批)。
        // 依据是用户口径"设备好 视频长 考虑批内扩大"——批数下降是刻意的收益,不是回退。
        // 【2026-09-23 档位 ×2】每批 700/1400 → ⌈899/700⌉=2、⌈900/1400⌉=1。
        var justUnder = RenderPolicy.PlanVideoBatches(10.4, 899, 899);
        Assert.Equal(700, justUnder.BatchSize);
        Assert.Equal(2, justUnder.BatchCount);
        var atThreshold = RenderPolicy.PlanVideoBatches(10.4, 900, 900);
        Assert.Equal(1400, atThreshold.BatchSize);
        Assert.Equal(1, atThreshold.BatchCount);
        Assert.True(atThreshold.BatchCount < justUnder.BatchCount);
    }

    [Fact]
    public void PlanVideoBatches_single_batch_exemption_stepdown_is_intentional()
    {
        // 另一条台阶:补帧后 800 帧 → 单批(1 批);801 帧 → 设备好档 700/批 → 2 批。
        // 依据是用户口径"补帧完的帧总数少 完全可以不分批"。
        // 【2026-09-23 档位 ×2】门槛 400 → 800;⌈801/700⌉ = 2。
        Assert.Equal(1, RenderPolicy.PlanVideoBatches(10.4, 800, 800).BatchCount);
        Assert.Equal(2, RenderPolicy.PlanVideoBatches(10.4, 801, 801).BatchCount);
    }

    // ---------- PlanUpscaleFirstBatches:「先超分再补帧」两侧分开算(该顺序当前关闭,只做准备+单测) ----------

    [Fact]
    public void PlanUpscaleFirstBatches_computes_both_sides_separately()
    {
        // 2x 超分 + 2x 补帧、源 600 帧、设备好(16G)、视频不长(源 600 < 900 → 700 帧/批)——
        // 而补帧后 600 ≤ 800 ⇒ 走"单批豁免"(整片一批):超分侧 = max(700, 600) = 700、1 批。
        // 补帧侧 = 700 ÷ 2² = 175(⌈600/175⌉=4)。
        // 【2026-09-23 档位 ×2】350/88 → 700/175,门槛 400 → 800。
        var p = RenderPolicy.PlanUpscaleFirstBatches(16.0, 600, scale: 2.0, interpScale: 2);
        Assert.Equal(RenderPolicy.DeviceTier.Strong, p.Tier);
        Assert.Equal(700, p.UpscaleFramesPerBatch);                 // 超分侧:输入=源帧
        Assert.Equal(1, p.UpscaleBatchCount);                       // 单批豁免
        Assert.Equal(175, p.InterpFramesPerBatch);                  // 补帧侧:700 ÷ 2² = 175(每帧像素 ×4)
        Assert.Equal(4, p.InterpBatchCount);                        // ⌈600/175⌉
        Assert.Equal(600, p.SourceFrames);
        Assert.Equal(1200, p.PostInterpFrames);
        // 两侧参数【必须不同】:这正是"不能共用同一套批参数"的体现
        Assert.NotEqual(p.UpscaleFramesPerBatch, p.InterpFramesPerBatch);
    }

    [Fact]
    public void PlanUpscaleFirstBatches_interp_side_respects_floor_and_area()
    {
        // 4x 超分:面积 ×16 → 补帧侧每批被面积压到 50(下界),不许更低
        var p4 = RenderPolicy.PlanUpscaleFirstBatches(16.0, 800, scale: 4.0, interpScale: 4);
        Assert.Equal(RenderPolicy.WeakDeviceFramesPerBatch, p4.InterpFramesPerBatch);
        // 1x 超分(面积不变):两侧每批帧数相同
        var p1 = RenderPolicy.PlanUpscaleFirstBatches(16.0, 800, scale: 1.0, interpScale: 2);
        Assert.Equal(p1.UpscaleFramesPerBatch, p1.InterpFramesPerBatch);
        // 弱机:超分侧 50 → 补帧侧也钳在 50(下界),不会因为面积换算掉到 50 以下
        var weak = RenderPolicy.PlanUpscaleFirstBatches(2.0, 800, scale: 2.0, interpScale: 2);
        Assert.Equal(RenderPolicy.WeakDeviceFramesPerBatch, weak.UpscaleFramesPerBatch);
        Assert.Equal(RenderPolicy.WeakDeviceFramesPerBatch, weak.InterpFramesPerBatch);
    }
}
