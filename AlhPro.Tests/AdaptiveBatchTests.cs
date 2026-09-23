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
    // 【2026-09-23 档位 ×2】下界 50 → 80 ⇒ 被下界兜住的那些行跟着变(面积把批压到 80 以下时取 80)
    [InlineData(200, 1920, 1080, 200)]   // 基准分辨率:不变
    [InlineData(200, 3840, 2160, 80)]    // 4K:1/4 → 50 → 被新下界 80 兜住
    [InlineData(400, 3840, 2160, 100)]   // 4K:1/4 → 100(在下界之上,不受影响)
    [InlineData(200, 7680, 4320, 80)]    // 8K:1/16 → 12.5 → 被新下界 80 兜住
    [InlineData(200, 960, 448, 200)]     // 小图:系数 >1 但**不许超过档位基准**(否则会越过任务 E 的口径)
    [InlineData(50, 3840, 2160, 50)]     // 档位基准本身低于下界(旧配置/外部调用):**不许抛异常**、也不许抬到 80
    public void Frames_per_batch_scale_by_area_and_clamp(int tierFrames, int w, int h, int expected)
        => Assert.Equal(expected, RenderPolicy.ScaleFramesForArea(tierFrames, w, h));

    /// <summary>面积反比关系:2160p 的每批帧数 = 1080p 的 1/4;小分辨率不放大到超过档位上限。</summary>
    [Fact]
    public void Plan_frames_per_batch_follow_area_inverse()
    {
        // 设备好(10.4G)+ 长片(1800 帧 ≥ 900)→ 档位基准 1400(【2026-09-23 档位 ×2】700 → 1400)
        var p1080 = RenderPolicy.PlanVideoBatches(10.4, 1800, 1800, false, false, 1920, 1080);
        var p2160 = RenderPolicy.PlanVideoBatches(10.4, 1800, 1800, false, false, 3840, 2160);
        var pSmall = RenderPolicy.PlanVideoBatches(10.4, 1800, 1800, false, false, 960, 448);
        Assert.Equal(1400, p1080.BatchSize);
        Assert.Equal(350, p2160.BatchSize);                  // 1400 ÷ 4
        Assert.Equal(1400, pSmall.BatchSize);                // 小图被档位上限挡住(不放大)
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
        // 4K + 兼容模式:1400 →(面积)350 →(减半)175
        var fast = RenderPolicy.PlanVideoBatches(10.4, 1800, 1800, fastMode: true, diskTight: false, 3840, 2160);
        Assert.Equal(175, fast.BatchSize);
        // 4K + 兼容 + 盘紧:350 → 175 → 87(两次减半;87 在新下界 80 之上,所以下界不再兜住它)
        var both = RenderPolicy.PlanVideoBatches(10.4, 1800, 1800, fastMode: true, diskTight: true, 3840, 2160);
        Assert.Equal(87, both.BatchSize);
        // 短素材(补帧后 ≤800)+ 设备正常 → 单批,面积缩放不影响(整片一批)
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
        Assert.Equal(560, oUp.FramesPerBatch);          // R3:超分阶段按"输入+输出并存"的峰值算(1400 × 0.4)⇒ < 补帧阶段 1400
        Assert.True(oIp.Advisory);                       // 补帧按转场分段跑 → 每批帧数是等效参考值

        // 新顺序:超分阶段输入 = 源帧;补帧阶段输入 = 放大 2x 的帧(面积 ×4 → 每批帧数显著更小)
        var newOrder = RenderPolicy.PlanStageBatches(10.4, 1800, 2.0, 2, 1920, 1080, upscaleFirst: true);
        var nUp = newOrder.Single(s => s.Stage == "超分");
        var nIp = newOrder.Single(s => s.Stage == "补帧");
        Assert.Equal(1800, nUp.StageInputFrames);
        Assert.Equal(3840, nIp.InputWidth);
        Assert.Equal(2160, nIp.InputHeight);
        Assert.Equal(0.25, nIp.AreaFactor, 6);
        Assert.Equal(350, nIp.FramesPerBatch);           // 1400 × 0.25(4K 面积)
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

    /// <summary>报告里那张对照表的**数字本身**钉住(设备好 10.4G、源 1800 帧 = 长片 → 档位基准 1400、2x 超分、2x 补帧)。
    /// 每行 [源分辨率] → 旧顺序[超分/补帧] + 新顺序[超分/补帧] 的每批帧数。
    /// 【2026-09-23 档位 ×2】整表按 700 → 1400 换算(面积系数、面积上限钳位、80 下界都照旧)。</summary>
    [Theory]
    [InlineData(960, 448, 1400, 1400, 1400, 1400)]     // 小图:全被"档位上限 1400"兜住
    [InlineData(1920, 1080, 560, 1400, 560, 350)]      // 基准[R3]:超分峰值系数 0.4 → 560;补帧=源 → 1400(**补帧批 > 超分批**)
    [InlineData(2560, 1440, 315, 788, 315, 197)]       // 2.5K:超分峰值系数 0.225 → 315;补帧 0.5625 → 788;新顺序补帧侧 0.140625 → 197
    [InlineData(3840, 2160, 140, 350, 140, 88)]        // 4K:超分峰值系数 0.1 → 140;补帧 0.25 → 350;新顺序补帧侧 0.0625 → 88
    public void Report_table_numbers_are_pinned(int w, int h, int oldUp, int oldIp, int newUp, int newIp)
    {
        var oldOrder = RenderPolicy.PlanStageBatches(10.4, 1800, 2.0, 2, w, h, upscaleFirst: false);
        var newOrder = RenderPolicy.PlanStageBatches(10.4, 1800, 2.0, 2, w, h, upscaleFirst: true);
        Assert.Equal(oldUp, oldOrder.Single(s => s.Stage == "超分").FramesPerBatch);
        Assert.Equal(oldIp, oldOrder.Single(s => s.Stage == "补帧").FramesPerBatch);
        Assert.Equal(newUp, newOrder.Single(s => s.Stage == "超分").FramesPerBatch);
        Assert.Equal(newIp, newOrder.Single(s => s.Stage == "补帧").FramesPerBatch);
    }

    /// <summary>【R3 · 用户要求「补帧分批要比超分大」】旧顺序下:补帧阶段输入=输出=源面积(批大)、
    /// 超分阶段输入=源 + 输出=放大 scale²(两者并存 → 批小)⇒ **补帧批 &gt; 超分批**;
    /// 新顺序下方向相反(超分阶段吃源帧、补帧阶段吃放大帧)。这条不许退化成"两阶段一样大"。</summary>
    [Theory]
    [InlineData(1920, 1080, 2.0)]
    [InlineData(3840, 2160, 2.0)]
    [InlineData(1920, 1080, 4.0)]
    public void Interp_batch_is_larger_than_upscale_batch_in_old_order(int w, int h, double scale)
    {
        var oldOrder = RenderPolicy.PlanStageBatches(10.4, 1800, scale, 2, w, h, upscaleFirst: false);
        int ip = oldOrder.Single(s => s.Stage == "补帧").FramesPerBatch;
        int up = oldOrder.Single(s => s.Stage == "超分").FramesPerBatch;
        Assert.True(ip > up, $"旧顺序:补帧每批 {ip} 必须大于超分每批 {up}");

        var newOrder = RenderPolicy.PlanStageBatches(10.4, 1800, scale, 2, w, h, upscaleFirst: true);
        int nip = newOrder.Single(s => s.Stage == "补帧").FramesPerBatch;
        int nup = newOrder.Single(s => s.Stage == "超分").FramesPerBatch;
        Assert.True(nup >= nip, $"新顺序:超分每批 {nup} 应不小于补帧每批 {nip}(方向相反)");
    }
}
