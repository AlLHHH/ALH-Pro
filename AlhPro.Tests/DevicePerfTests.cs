using AlhPro.Core;
using System;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【任务 T】设备性能归一 + 更激进的档位基准 + 峰值守门随上限上升。
/// 这里钉三件事:① 没有实测吞吐时**必须**回退纯内存档(不许凭空猜性能);
/// ② PerfScore → 每批帧数**单调不减**(Fast ≥ Normal ≥ Slow),且 700 只在 Fast + 内存 ≥8G 时出现;
/// ③ 抬批上限必须同步抬临时盘预估(NeedBytes 的"每批并存帧"项)。</summary>
public class DevicePerfTests
{
    // ---------- ① PerfScore:有实测按实测,没实测回退内存档 ----------

    [Theory]
    [InlineData(0.10, PerfScore.Fast)]      // 比 ncnn 实测最快档(0.24)还快
    [InlineData(0.30, PerfScore.Fast)]      // 边界(含)
    [InlineData(0.31, PerfScore.Normal)]
    [InlineData(1.00, PerfScore.Normal)]    // 边界(含)
    [InlineData(1.01, PerfScore.Slow)]
    [InlineData(8.00, PerfScore.Slow)]      // "ONNX 落 CPU"那种机器:8 秒/帧
    public void Measured_throughput_maps_to_expected_score(double secondsPerFrame, PerfScore expected)
    {
        // 内存 16G、核数 16、显存未实测 → 唯一变量就是吞吐
        var s = DevicePerf.Score(16.0, 16, secondsPerFrame, out string reason);
        Assert.Equal(expected, s);
        Assert.Contains("实测吞吐", reason);
    }

    [Fact]
    public void Without_measurement_it_falls_back_to_the_pure_memory_tier()
    {
        Assert.Equal(PerfScore.Fast, DevicePerf.Score(16.0, 16, null, out string r1));
        Assert.Contains("回退纯内存档", r1);
        Assert.Equal(PerfScore.Normal, DevicePerf.Score(5.0, 16, null, out _));
        Assert.Equal(PerfScore.Slow, DevicePerf.Score(2.0, 16, null, out _));
        // 非法/零/NaN 的"实测值"等同于没有实测
        Assert.Equal(PerfScore.Fast, DevicePerf.Score(16.0, 16, 0.0, out _));
        Assert.Equal(PerfScore.Normal, DevicePerf.Score(5.0, 16, double.NaN, out _));
        // FromRam 是同一口径的显式入口
        Assert.Equal(DevicePerf.FromRam(16.0), PerfScore.Fast);
        Assert.Equal(DevicePerf.FromRam(1.0), PerfScore.Slow);
    }

    [Fact]
    public void Fast_is_denied_without_enough_free_ram()
    {
        // 测得飞快,但空闲内存只有 6G → 不许 Fast(700 帧/批要求内存 ≥8G)
        var s = DevicePerf.Score(6.0, 16, 0.10, out string reason);
        Assert.Equal(PerfScore.Normal, s);
        Assert.Contains("不给 Fast", reason);
    }

    [Fact]
    public void Few_cores_or_tiny_vram_lower_the_score_one_step()
    {
        // 核数 2(< 4)→ 降一档
        Assert.Equal(PerfScore.Normal, DevicePerf.Score(16.0, 2, 0.10, out string r1));
        Assert.Contains("核数", r1);
        // 显存已实测且只有 2G(< 4G)→ 降一档
        Assert.Equal(PerfScore.Normal, DevicePerf.Score(16.0, 16, 0.10, out string r2, vramGB: 2.0, vramMeasured: true));
        Assert.Contains("降一档", r2);
        // 显存**未实测**(AMD/Intel 测不到)→ 不参与判定
        Assert.Equal(PerfScore.Fast, DevicePerf.Score(16.0, 16, 0.10, out string r3, vramGB: 1.0, vramMeasured: false));
        Assert.Contains("显存未实测", r3);
        // 已经是最低档时不再继续降(单调有下界)
        Assert.Equal(PerfScore.Slow, DevicePerf.Score(2.0, 2, 8.0, out _));
    }

    // ---------- ② 新档位基准 + PerfScore 对批大小的单调性 ----------

    [Fact]
    public void New_tier_baselines_are_exactly_as_specified()
    {
        // Strong + 长片 → 700;Strong + 短片 → 350;Normal → 300;Weak → 50(全档位下界)
        Assert.Equal(700, RenderPolicy.PlanVideoBatches(16.0, 1800, 3600, perf: PerfScore.Fast).BatchSize);
        Assert.Equal(350, RenderPolicy.PlanVideoBatches(16.0, 300, 600, perf: PerfScore.Fast).BatchSize);
        Assert.Equal(300, RenderPolicy.PlanVideoBatches(6.0, 2000, 4000, perf: PerfScore.Normal).BatchSize);
        Assert.Equal(50, RenderPolicy.PlanVideoBatches(2.0, 2000, 4000, perf: PerfScore.Slow).BatchSize);
        // 常量与实现必须是同一个数(防止有人只改一处)
        Assert.Equal(700, RenderPolicy.StrongDeviceLargeFramesPerBatch);
        Assert.Equal(350, RenderPolicy.StrongDeviceFramesPerBatch);
        Assert.Equal(300, RenderPolicy.NormalDeviceFramesPerBatch);
        Assert.Equal(50, RenderPolicy.WeakDeviceFramesPerBatch);
    }

    [Fact]
    public void Batch_size_is_monotonic_in_perf_score()
    {
        // PerfScore 升档,每批帧数不许下降(同一素材/同一内存档)
        foreach (var ram in new[] { 2.0, 5.0, 10.4, 16.0 })
            foreach (var (src, post) in new[] { (300, 600), (1800, 3600), (100, 400) })
            {
                int slow = RenderPolicy.PlanVideoBatches(ram, src, post, perf: PerfScore.Slow).BatchSize;
                int normal = RenderPolicy.PlanVideoBatches(ram, src, post, perf: PerfScore.Normal).BatchSize;
                int fast = RenderPolicy.PlanVideoBatches(ram, src, post, perf: PerfScore.Fast).BatchSize;
                Assert.True(slow <= normal, $"ram={ram} src={src} post={post}:Slow {slow} > Normal {normal}");
                Assert.True(normal <= fast, $"ram={ram} src={src} post={post}:Normal {normal} > Fast {fast}");
            }
    }

    [Fact]
    public void Seven_hundred_requires_fast_perf_and_at_least_8gb()
    {
        // 长片 + Fast,但内存只有 6G(Normal 内存档)→ 300,拿不到 700
        var p = RenderPolicy.PlanVideoBatches(6.0, 1800, 3600, perf: PerfScore.Fast);
        Assert.Equal(RenderPolicy.NormalDeviceFramesPerBatch, p.BatchSize);
        // 内存 16G 但实测是 Slow → 50,同样拿不到 700
        var slow = RenderPolicy.PlanVideoBatches(16.0, 1800, 3600, perf: PerfScore.Slow);
        Assert.Equal(RenderPolicy.WeakDeviceFramesPerBatch, slow.BatchSize);
    }

    [Fact]
    public void Perf_score_no_longer_removes_the_short_clip_single_batch()
    {
        // 单批豁免也按"内存档 × 性能档取较低者":实测很慢的机器不该被塞成"整片一批"
        Assert.True(RenderPolicy.PlanVideoBatches(16.0, 200, 400, perf: PerfScore.Fast).SingleBatch);
        Assert.False(RenderPolicy.PlanVideoBatches(16.0, 200, 400, perf: PerfScore.Slow).SingleBatch);
        // 但即使不单批,也仍然覆盖全部帧(不丢帧)
        var p = RenderPolicy.PlanVideoBatches(16.0, 200, 400, perf: PerfScore.Slow);
        Assert.True((long)p.BatchCount * p.BatchSize >= 400);
    }

    // ---------- ③ 峰值守门随上限上升 ----------

    [Fact]
    public void NeedBytes_without_batch_term_is_unchanged()
    {
        // 默认参数必须与改动前逐字一致(batchFrames=0 → 加项为 0)
        double expected = 1000 * 2.0 * 1024 * 1024 * TempSpaceEstimate.SafetyFactor;
        Assert.Equal(expected, TempSpaceEstimate.NeedBytes(1000, 2.0), 3);
        Assert.Equal(expected, TempSpaceEstimate.NeedBytes(1000, 2.0, 0, 0), 3);
    }

    [Fact]
    public void Peak_guard_grows_with_the_batch_cap()
    {
        // 1080p 源、2x 超分、补帧开(与真机口径同量级):全片 3420 帧
        double srcFrameMb = TempSpaceEstimate.SourceFrameMegabytes(1920, 1080);
        double peakFrameMb = TempSpaceEstimate.PeakFrameMegabytes(1920, 1080, 2.0, frameInterp: true);
        double oldCap = TempSpaceEstimate.NeedBytesForBatch(3420, peakFrameMb, 400, srcFrameMb);   // 旧上限
        double newCap = TempSpaceEstimate.NeedBytesForBatch(3420, peakFrameMb, 700, srcFrameMb);   // 新上限
        Assert.True(newCap > oldCap, "批上限 400 → 700 后预估必须同步上升(否则就是只抬上限不给守门)");
        // 上升量 = (700-400) × (峰值帧 + 源帧) × 安全系数
        Assert.Equal(300 * (peakFrameMb + srcFrameMb) * 1024 * 1024 * TempSpaceEstimate.SafetyFactor,
            newCap - oldCap, 3);
        // 且 700/400 = 1.75 的差在"每批项"上严格成立
        Assert.True(newCap - oldCap > 0.5 * 1024 * 1024 * 1024, "这一项在 1080p/2x 上应有 GB 量级,不能是可忽略的小量");
    }

    [Fact]
    public void Peak_guard_is_monotonic_and_floored()
    {
        double cur = 0;
        foreach (int batch in new[] { 0, 50, 175, 300, 350, 700, 2000 })
        {
            double v = TempSpaceEstimate.NeedBytesForBatch(1000, 1.0, batch, 1.0);
            Assert.True(v >= cur, $"每批 {batch} 帧的预估不应低于更小批量");
            cur = v;
        }
        Assert.Equal(0, TempSpaceEstimate.NeedBytesForBatch(0, 1.0, 0, 1.0), 6);
        Assert.Equal(0, TempSpaceEstimate.NeedBytesForBatch(-5, 1.0, -5, -5), 6);
    }
}
