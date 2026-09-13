using AlhPro.Core;
using System;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>自适应分批(按输入分辨率缩放每批帧数)的单测(任务 Q2)。
/// 口径:以 1080p(2.07 Mpx)为基准,每批帧数按 `基准 × (1080p 面积 ÷ 实际输入像素)` 缩放,
/// 钳到 `[50, 档位基准]`;fastMode/diskTight 减半、短素材单批、设备档位门槛全部保留(优先级见 RenderPolicy 注释)。
/// 两个阶段的输入分辨率不同 → 各自算(旧顺序:补帧输入=源帧、超分输入=补帧输出;新顺序:超分输入=源帧、
/// 补帧输入=放大帧(面积 ×scale²))。</summary>
public class AdaptiveBatchTests
{
    [Fact]
    public void Area_factor_is_inverse_to_pixels()
    {
        Assert.Equal(1.0, RenderPolicy.AreaFactor(1920, 1080), 6);
        Assert.Equal(0.25, RenderPolicy.AreaFactor(3840, 2160), 6);          // 4 倍像素 → 1/4
        Assert.Equal(4.0, RenderPolicy.AreaFactor(960, 540), 6);
        Assert.Equal(1.0, RenderPolicy.AreaFactor(0, 0), 6);                 // 非法 → 不缩放
        Assert.Equal(64.0, RenderPolicy.AreaFactor(1, 1), 6);                // 病态输入被钳住
    }

    [Theory]
    [InlineData(200, 1920, 1080, 200)]   // 基准分辨率:不变
    [InlineData(200, 3840, 2160, 50)]    // 4K:1/4 → 50(正好等于用户下界)
    [InlineData(400, 3840, 2160, 100)]   // 4K:1/4
    [InlineData(200, 7680, 4320, 50)]    // 8K:1/16 → 12.5 → 被 50 下界兜住
    [InlineData(200, 960, 448, 200)]     // 小图:系数 >1 但**不许超过档位基准**(否则会越过任务 E 的口径)
    [InlineData(50, 3840, 2160, 50)]     // 已是下界:减不动
    public void Frames_per_batch_scale_by_area_and_clamp(int tierFrames, int w, int h, int expected)
        => Assert.Equal(expected, RenderPolicy.ScaleFramesForArea(tierFrames, w, h));

    /// <summary>面积反比关系:2160p 的每批帧数 = 1080p 的 1/4;小分辨率不放大到超过档位上限。</summary>
    [Fact]
    public void Plan_frames_per_batch_follow_area_inverse()
    {
        // 设备好(10.4G)+ 长片(1800 帧 ≥ 900)→ 档位基准 400
        var p1080 = RenderPolicy.PlanVideoBatches(10.4, 1800, 1800, false, false, 1920, 1080);
        var p2160 = RenderPolicy.PlanVideoBatches(10.4, 1800, 1800, false, false, 3840, 2160);
        var pSmall = RenderPolicy.PlanVideoBatches(10.4, 1800, 1800, false, false, 960, 448);
        Assert.Equal(400, p1080.BatchSize);
        Assert.Equal(100, p2160.BatchSize);
        Assert.Equal(400, pSmall.BatchSize);                 // 小图被档位上限挡住(不放大)
        Assert.Equal(p1080.BatchSize / 4, p2160.BatchSize);
        Assert.True(p2160.BatchCount > p1080.BatchCount);    // 每批更小 → 批数更多
    }

    /// <summary>不传分辨率 → 行为与改动前逐字一致(既有调用点/既有断言不受影响)。</summary>
    [Fact]
    public void Without_resolution_the_old_behaviour_is_unchanged()
    {
        var a = RenderPolicy.PlanVideoBatches(10.4, 1800, 1800);
        var b = RenderPolicy.PlanVideoBatches(10.4, 1800, 1800, false, false, 1920, 1080);
        Assert.Equal(a.BatchSize, b.BatchSize);
        Assert.Equal(a.BatchCount, b.BatchCount);
        Assert.Equal(a.TierBaseFrames, b.TierBaseFrames);
    }

    /// <summary>与既有规则叠加:fastMode/diskTight 减半照旧、50 下界优先、短素材单批覆盖一切。</summary>
    [Fact]
    public void Existing_rules_still_apply_on_top_of_area_scaling()
    {
        // 4K + 兼容模式:400 →(面积)100 →(减半)50
        var fast = RenderPolicy.PlanVideoBatches(10.4, 1800, 1800, fastMode: true, diskTight: false, 3840, 2160);
        Assert.Equal(50, fast.BatchSize);
        // 4K + 兼容 + 盘紧:100 → 50 → 50(下界优先,第二个减半不再生效)
        var both = RenderPolicy.PlanVideoBatches(10.4, 1800, 1800, fastMode: true, diskTight: true, 3840, 2160);
        Assert.Equal(50, both.BatchSize);
        // 短素材(补帧后 ≤400)+ 设备正常 → 单批,面积缩放不影响(整片一批)
        var single = RenderPolicy.PlanVideoBatches(6.0, 200, 400, false, false, 3840, 2160);
        Assert.True(single.SingleBatch);
        Assert.Equal(400, single.BatchSize);
        Assert.Equal(1, single.BatchCount);
    }

    /// <summary>不变量(多分辨率 × 多档位 × 两种保护开关):每批 ≥50、批数 ≥1、覆盖全部帧。</summary>
    [Fact]
    public void Invariants_hold_across_resolutions_and_tiers()
    {
        foreach (var ram in new[] { 2.0, 6.0, 10.4, 16.0 })
            foreach (var (w, h) in new[] { (960, 448), (1920, 1080), (2560, 1440), (3840, 2160), (7680, 4320) })
                foreach (var fast in new[] { false, true })
                    foreach (var disk in new[] { false, true })
                        foreach (int total in new[] { 1, 50, 399, 400, 401, 1800, 5000 })
                        {
                            var p = RenderPolicy.PlanVideoBatches(ram, 2000, total, fast, disk, w, h);
                            Assert.True(p.BatchSize >= RenderPolicy.WeakDeviceFramesPerBatch, $"每批 {p.BatchSize} < 50");
                            Assert.True(p.BatchCount >= 1);
                            Assert.True((long)p.BatchCount * p.BatchSize >= total,
                                $"{w}×{h} ram={ram} total={total}:{p.BatchCount}×{p.BatchSize} 覆盖不了");
                            // 注意:PlanVideoBatches 会把"补帧后总帧数"抬到 ≥ 源帧数(补帧倍率 ≥1 的语义),
                            // 所以这里算期望批数要用抬过之后的量(Math.Max(total, 2000))。
                            long effective = Math.Max(total, 2000);
                            int expectCount = (int)((effective + p.BatchSize - 1) / p.BatchSize);
                            Assert.Equal(expectCount, p.BatchCount);
                        }
    }

    /// <summary>两个阶段各自算(任务 Q2 要求 2):旧顺序超分阶段输入 = 补帧输出;新顺序补帧阶段输入 = 放大帧。</summary>
    [Fact]
    public void Two_stages_use_their_own_input_resolution()
    {
        // 旧顺序:两阶段输入分辨率都是源(补帧输出仍是源分辨率),但超分阶段帧数 = 源×补帧倍率
        var oldOrder = RenderPolicy.PlanStageBatches(10.4, 1800, 2.0, 2, 1920, 1080, upscaleFirst: false);
        var oUp = oldOrder.Single(s => s.Stage == "超分");
        var oIp = oldOrder.Single(s => s.Stage == "补帧");
        Assert.Equal(1920, oUp.InputWidth);
        Assert.Equal(3600, oUp.StageInputFrames);        // 超分读补帧输出
        Assert.Equal(1800, oIp.StageInputFrames);
        Assert.Equal(400, oUp.FramesPerBatch);
        Assert.True(oIp.Advisory);                       // 补帧按转场分段跑 → 每批帧数是等效参考值

        // 新顺序:超分阶段输入 = 源帧;补帧阶段输入 = 放大 2x 的帧(面积 ×4 → 每批帧数显著更小)
        var newOrder = RenderPolicy.PlanStageBatches(10.4, 1800, 2.0, 2, 1920, 1080, upscaleFirst: true);
        var nUp = newOrder.Single(s => s.Stage == "超分");
        var nIp = newOrder.Single(s => s.Stage == "补帧");
        Assert.Equal(1800, nUp.StageInputFrames);
        Assert.Equal(3840, nIp.InputWidth);
        Assert.Equal(2160, nIp.InputHeight);
        Assert.Equal(0.25, nIp.AreaFactor, 6);
        Assert.Equal(100, nIp.FramesPerBatch);
        Assert.True(nIp.FramesPerBatch < oIp.FramesPerBatch, "新顺序补帧阶段的每批帧数必须显著更小");
    }

    /// <summary>报告用的对照表(源分辨率 → 每批帧数),同时作为"关系必须成立"的断言。</summary>
    [Theory]
    [InlineData(960, 448)]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    [InlineData(3840, 2160)]
    public void Report_table_rows_are_consistent(int w, int h)
    {
        var oldOrder = RenderPolicy.PlanStageBatches(10.4, 1800, 2.0, 2, w, h, upscaleFirst: false);
        var newOrder = RenderPolicy.PlanStageBatches(10.4, 1800, 2.0, 2, w, h, upscaleFirst: true);
        foreach (var s in oldOrder.Concat(newOrder))
        {
            Assert.True(s.FramesPerBatch >= RenderPolicy.WeakDeviceFramesPerBatch);
            Assert.True(s.FramesPerBatch <= RenderPolicy.StrongDeviceLargeFramesPerBatch);
            Assert.True((long)s.BatchCount * s.FramesPerBatch >= Math.Max(1, s.StageInputFrames));
            Assert.NotEqual(0, s.AreaFactor);
        }
        // 两个顺序的超分阶段输入分辨率相同(都是源),补帧阶段不同(新顺序是放大帧)
        Assert.Equal(oldOrder.Single(s => s.Stage == "超分").InputWidth, newOrder.Single(s => s.Stage == "超分").InputWidth);
        Assert.True(newOrder.Single(s => s.Stage == "补帧").InputWidth
            >= oldOrder.Single(s => s.Stage == "补帧").InputWidth);
    }

    /// <summary>报告里那张对照表的**数字本身**钉住(设备好 10.4G、源 1800 帧 = 长片 → 档位基准 400、2x 超分、2x 补帧)。
    /// 每行 [源分辨率] → 旧顺序[超分/补帧] + 新顺序[超分/补帧] 的每批帧数。</summary>
    [Theory]
    [InlineData(960, 448, 400, 400, 400, 400)]     // 小图:全被"档位上限 400"兜住(面积系数 >1 但不许超过档位基准)
    [InlineData(1920, 1080, 400, 400, 400, 100)]   // 基准:旧顺序不变;新顺序补帧侧面积 ×4 → 100
    [InlineData(2560, 1440, 225, 225, 225, 56)]    // 2.5K:面积系数 0.5625 → 225;新顺序补帧侧 14.7Mpx → 56
    [InlineData(3840, 2160, 100, 100, 100, 50)]    // 4K:面积系数 0.25 → 100;新顺序补帧侧 33Mpx → 25 被 50 下界兜住
    public void Report_table_numbers_are_pinned(int w, int h, int oldUp, int oldIp, int newUp, int newIp)
    {
        var oldOrder = RenderPolicy.PlanStageBatches(10.4, 1800, 2.0, 2, w, h, upscaleFirst: false);
        var newOrder = RenderPolicy.PlanStageBatches(10.4, 1800, 2.0, 2, w, h, upscaleFirst: true);
        Assert.Equal(oldUp, oldOrder.Single(s => s.Stage == "超分").FramesPerBatch);
        Assert.Equal(oldIp, oldOrder.Single(s => s.Stage == "补帧").FramesPerBatch);
        Assert.Equal(newUp, newOrder.Single(s => s.Stage == "超分").FramesPerBatch);
        Assert.Equal(newIp, newOrder.Single(s => s.Stage == "补帧").FramesPerBatch);
    }
}
