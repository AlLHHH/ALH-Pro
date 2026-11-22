using System;
using System.IO;
using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests
{
    /// <summary>
    /// 【2026-11-22 · 用户反馈「处理特别慢」的根因】诊断包 ALHPro_Diag_20261122_1930:
    /// RTX 5060 Laptop 8GB,源 1280×720 → CUGAN 2x 到 3840×2160,补帧 4x,开了 TTA。
    /// 补帧实测 4854 ms/输出帧(3960.6 s / 816 帧),整任务 69 分 31 秒,补帧占了 95%。
    ///
    /// 顺序判定(PipelineOrderPlan)原来只按面积查补帧锚点(2160p = 0.2776 s/帧),
    /// 完全不知道 TTA 会让补帧贵 14 倍 ⇒ 一定判成「超分很贵,先超分」,把任务推进慢路。
    /// 这里锁住:同样的机器与设置,开了 TTA 就必须判「先补帧」。
    /// </summary>
    public class TtaInterpSlowdownTests
    {
        // 与诊断包同一组数字:2x 超分实测 1.6673 s/帧(1080p 源),补帧 4x,源 1920×1080。
        private static PipelineOrderPlan.CostInput CuganCost() =>
            new("realcugan-se", 2, Measured: true, UpscalePerFrame1080p: 1.6673);

        [Fact]
        public void Without_tta_the_expensive_upscale_goes_first()
        {
            var d = PipelineOrderPlan.Decide(CuganCost(), 2.0, 4, 1920, 1080, 103);
            Assert.True(d.UpscaleFirst, d.Reason);
        }

        [Fact]
        public void With_tta_the_plan_switches_to_interpolate_first()
        {
            var d = PipelineOrderPlan.Decide(CuganCost(), 2.0, 4, 1920, 1080, 103,
                interpSlowdown: PipelineOrderPlan.TtaInterpSlowdown);
            Assert.False(d.UpscaleFirst, d.Reason);
            // 理由里必须写明这件事,否则日志里补帧成本与锚点表对不上,没人看得懂。
            Assert.Contains("TTA", d.Reason);
        }

        [Fact]
        public void The_slowdown_constant_stays_between_measured_and_safe()
        {
            // 实测 14.4 倍(4.854 / 0.337);取保守值,但不能小到判不出区别(< 10 就回到老结论)。
            Assert.InRange(PipelineOrderPlan.TtaInterpSlowdown, 10.0, 14.4);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-3.0)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void A_bogus_slowdown_is_ignored(double bogus)
        {
            var d = PipelineOrderPlan.Decide(CuganCost(), 2.0, 4, 1920, 1080, 103, interpSlowdown: bogus);
            Assert.True(d.UpscaleFirst, d.Reason);
        }

        [Fact]
        public void The_video_pipeline_actually_feeds_the_tta_switch_in()
        {
            // 源码契约:判定点必须把「这次开不开 TTA」传下去;不传等于这条修复不存在。
            string dir = AppContext.BaseDirectory;
            string root = null;
            var probe = new DirectoryInfo(dir);
            while (probe != null && !File.Exists(Path.Combine(probe.FullName, "ImgUpscalerUI", "VideoService.cs")))
                probe = probe.Parent;
            root = probe?.FullName;
            Assert.NotNull(root);

            string svc = File.ReadAllText(Path.Combine(root, "ImgUpscalerUI", "VideoService.cs"));
            Assert.Contains("interpSlowdown:", svc);
            Assert.Contains("PipelineOrderPlan.TtaInterpSlowdown", svc);
            // v4.26 不传 TTA,不能算成慢。
            Assert.Contains("interpModel != \"rife-v4.26\"", svc);
        }
    }
}