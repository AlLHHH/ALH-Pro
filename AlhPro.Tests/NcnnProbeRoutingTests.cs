// 三态探测路由的契约(2026-11-12)
// 【为什么需要它】诊断包 ALHPro_Diag_20261112_1854(RTX 5060 Ti 8GB):启动时空闲显存只剩 0.2GB
// ⇒ 生产尺寸探测被"空闲显存闸"跳过 ⇒ 旧的 bool 接口返回 false ⇒ 调用点把它当成"ncnn 实测不可用"
// ⇒ 整段超分从 ncnn-Vulkan 切到 ONNX DirectML(实测 6 秒/帧,而同卡 ncnn 约 0.1~0.3 秒/帧)。
// 这里同时钉住"判定规则"(Core 纯函数)与"调用点真的用了三态"(源码契约,UI 项目不被测试引用)。
using System;
using System.IO;
using System.Linq;
using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests
{
    public class NcnnProbeRoutingTests
    {
        [Fact]
        public void Only_a_conclusive_failure_falls_back_to_onnx()
        {
            Assert.True(NcnnProbeRouting.WantsOnnxFallback(NcnnProbeReport.Unavailable));
            Assert.False(NcnnProbeRouting.WantsOnnxFallback(NcnnProbeReport.Available));
            // 这条是整个修复的核心:没测通不许换路。
            Assert.False(NcnnProbeRouting.WantsOnnxFallback(NcnnProbeReport.NotConcluded));
        }

        [Fact]
        public void Not_concluded_keeps_ncnn()
        {
            Assert.True(NcnnProbeRouting.KeepsNcnn(NcnnProbeReport.Available));
            Assert.True(NcnnProbeRouting.KeepsNcnn(NcnnProbeReport.NotConcluded));
            Assert.False(NcnnProbeRouting.KeepsNcnn(NcnnProbeReport.Unavailable));
        }

        [Fact]
        public void Legacy_boolean_only_maps_two_states()
        {
            Assert.Equal(NcnnProbeReport.Available, NcnnProbeRouting.FromBoolean(true));
            Assert.Equal(NcnnProbeReport.Unavailable, NcnnProbeRouting.FromBoolean(false));
        }

        /// <summary>三个调用点(视频流水线 / 图片页 / 视频页预检)必须走三态接口。
        /// 谁要是改回 `EnsureNcnnProbeAsync` 直接拿 bool 当结论,这条会红。</summary>
        [Fact]
        public void Call_sites_route_by_tri_state_not_by_bool()
        {
            var video = ReadFile("ImgUpscalerUI", "VideoService.cs");
            Assert.Contains("EnsureNcnnProbeDecisionAsync", video);
            Assert.Contains("NcnnProbeRouting.KeepsNcnn", video);
            // 老写法:把 bool 直接赋给 usable —— 修好之后不该再出现。
            Assert.DoesNotContain("usable = await EngineService.EnsureNcnnProbeAsync", video);
            Assert.DoesNotContain("denoiseViaModel = dnPlan.ShouldProbe\n                            && await EngineService.EnsureNcnnProbeAsync", video);

            var imagePage = ReadFile("ImgUpscalerUI", "Views", "UpscaleView.xaml.cs");
            Assert.Contains("EnsureNcnnProbeDecisionAsync(\"realesrgan\"", imagePage);
            Assert.Contains("NcnnProbeRouting.KeepsNcnn", imagePage);

            var videoPage = ReadFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
            Assert.Contains("EnsureNcnnProbeDecisionAsync", videoPage);
            Assert.Contains("NcnnProbeRouting.KeepsNcnn", videoPage);
        }

        /// <summary>探测前必须重读空闲显存,不能沿用"启动时那一次"的读数。</summary>
        [Fact]
        public void Probe_refreshes_free_vram_before_the_gate()
        {
            var engine = ReadFile("ImgUpscalerUI", "EngineService.cs");
            var gate = engine.IndexOf("if (VramTooLowForProductionProbe(", System.StringComparison.Ordinal);
            var refresh = engine.IndexOf("SafeRender.RefreshFreeVram();", System.StringComparison.Ordinal);
            Assert.True(refresh > 0, "EngineService 里没有重读空闲显存");
            Assert.True(gate > 0, "找不到显存闸");
            Assert.True(refresh < gate, "重读要发生在显存闸之前");
        }

        /// <summary>三态接口本身必须把"没测成"的出口都映射成 NotConcluded,不能再返回"不可用"。</summary>
        [Fact]
        public void Every_inconclusive_exit_maps_to_not_concluded()
        {
            var engine = ReadFile("ImgUpscalerUI", "EngineService.cs");
            Assert.Contains("EnsureNcnnProbeDecisionAsync", engine);
            Assert.Contains("return NotConcluded;", engine);
            Assert.Contains("return ok ? Available : Unavailable;", engine);
        }

        private static string ReadFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var cand = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
                if (File.Exists(cand)) return File.ReadAllText(cand);
                dir = dir.Parent;
            }
            throw new FileNotFoundException("找不到仓库文件: " + string.Join('/', parts));
        }
    }
}
