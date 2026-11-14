// Vulkan 挂了 ≠ 没有显卡(2026-11-14)
// 【为什么需要它】诊断包 ALHPro_Diag_20261113_2340(RTX 5060 Laptop 8GB):
// 显卡驱动在使用中途从 32.0.16.1088 升到 32.0.16.1714 且没重启 ⇒ Vulkan 引擎枚举 0 个设备、
// libplacebo 报 VK_ERROR_INCOMPATIBLE_DRIVER,而 DirectML 建会话成功(DML探测首可用=#0)。
// 旧逻辑只看 Vulkan:自检把处理设备切成 CPU(日志「未检测到可用 GPU → 处理设备=CPU」),
// 界面再报「本机 DirectML 不可用,超分将使用 CPU」—— 假话,并按 CPU 15.8 秒/帧估出 44 小时。
// 这里钉住:①"没有计算设备"必须两个运行时都不可用;②三处调用点不许只看 Vulkan。
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests
{
    public class GpuRouteHonestyTests
    {
        /// <summary>"没有可用的计算设备" = Vulkan 不可用 **且** DirectML 也不可用。
        /// 只判 Vulkan 会把还能跑 ONNX 的机器说成"只能 CPU"。</summary>
        [Fact]
        public void No_compute_device_requires_both_runtimes_to_fail()
        {
            var vk = ReadFile("ImgUpscalerUI", "VulkanCheck.cs");
            Assert.Contains("public static bool NoComputeDeviceDetected => NoGpuDetected && !DmlAvailable();", vk);
            // DirectML 判定要给外面用(自检联动、设备提示、ONNX 选设备都要同一套)
            Assert.Contains("public static bool DmlAvailable()", vk);
            // ncnn 风险的判据保持 Vulkan 语义(那才是 Vulkan 能表达的东西)
            Assert.Contains("public static bool NoGpuDetected => ProbeConcluded && !GpuAvailable;", vk);
        }

        /// <summary>自检联动:Vulkan 不可用时必须先问 DirectML,能在 DML 上跑就不许把设备切成 CPU。
        /// 这条红了 = 又有人把 `if (!gpuOk)` 分支写回"直接切 CPU"。</summary>
        [Fact]
        public void Self_check_keeps_the_gpu_when_directml_still_works()
        {
            var mp = ReadFile("ImgUpscalerUI", "Views", "MainPage.xaml.cs");
            int branch = mp.IndexOf("if (!gpuOk)", StringComparison.Ordinal);
            Assert.True(branch > 0, "MainPage 里找不到自检失败分支");
            int downgrade = mp.IndexOf("AppSettings.GpuIndex = -1;", branch, StringComparison.Ordinal);
            int ask = mp.IndexOf("VulkanCheck.DmlAvailable()", branch, StringComparison.Ordinal);
            Assert.True(ask > 0, "自检失败分支没有先问 DirectML 还能不能用");
            Assert.True(downgrade < 0 || ask < downgrade, "先切 CPU 再问 DirectML,顺序反了");

            // 光有顺序不够:DML 可用那条分支**体内**不许再切 CPU,而且要给用户一句说明。
            // (把分支清空成 `if (DmlAvailable()) { }` 这种假修复,下面两条会红。)
            int bodyEnd = mp.IndexOf("else if (AppSettings.GpuIndex >= 0)", ask, StringComparison.Ordinal);
            Assert.True(bodyEnd > ask, "找不到 DML 分支的结束位置");
            string dmlBranch = mp.Substring(ask, bodyEnd - ask);
            Assert.DoesNotContain("AppSettings.GpuIndex = -1", dmlBranch);
            Assert.Contains("autoMsg", dmlBranch);
            Assert.Contains("ONNX", dmlBranch);
        }

        /// <summary>选设备:DirectML 还能建会话时不许因为 GpuIndex&lt;0 直接返回 CPU。
        /// 设置页早已没有"CPU 计算"选项,-1 只剩"自检自动降级"与老版本存值两种来路。</summary>
        [Fact]
        public void Onnx_device_pick_does_not_treat_auto_downgrade_as_cpu_choice()
        {
            var onnx = ReadFile("ImgUpscalerUI", "EsrganOnnxService.cs");
            Assert.DoesNotContain("if (AppSettings.GpuIndex < 0) return -1;", onnx);
            Assert.Contains("if (AppSettings.GpuIndex < 0 && !VulkanCheck.DmlAvailable()) return -1;", onnx);
        }

        /// <summary>界面那句"超分将使用 CPU"只在 DML 真的不可用时才允许出现。</summary>
        [Fact]
        public void Cpu_warning_requires_directml_to_be_really_unavailable()
        {
            var vv = ReadFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
            Assert.Contains("if (AppSettings.GpuIndex < 0 && !ALHPro.VulkanCheck.DmlAvailable()) return true;", vv);
            // 设备提示语与硬件画像同样走"两个运行时都不可用"的判据
            Assert.Contains("bool noGpu = ALHPro.VulkanCheck.NoComputeDeviceDetected;", vv);
            var sr = ReadFile("ImgUpscalerUI", "SafeRender.cs");
            Assert.Contains("gpu = !ALHPro.VulkanCheck.NoComputeDeviceDetected;", sr);
        }

        /// <summary>自检报告里"结论"和"可用性"不许自相矛盾:结论说只能 CPU、可用性又说 DML 可用。</summary>
        [Fact]
        public void Report_conclusion_does_not_contradict_its_availability_section()
        {
            var vk = ReadFile("ImgUpscalerUI", "VulkanCheck.cs");
            int conclusion = vk.IndexOf("else if (!gpuOk && DmlAvailable())", StringComparison.Ordinal);
            int cpuOnly = vk.IndexOf("else if (!gpuOk)", StringComparison.Ordinal);
            Assert.True(conclusion > 0, "报告结论里没有 DirectML 分支");
            Assert.True(cpuOnly > conclusion, "DirectML 分支必须在「只能 CPU」之前判");
        }

        /// <summary>"要不要探测 DirectML"与"DirectML 算不算可用"必须同一套判据。
        /// 原来探测那处只问 esrgan 超分模型,只有 waifu2x/RIFE ONNX 的机器不探测 ⇒ DmlFallbackOk 恒 -1 ⇒ 被判 CPU。</summary>
        [Fact]
        public void Probe_trigger_and_availability_share_one_model_predicate()
        {
            var onnx = ReadFile("ImgUpscalerUI", "EsrganOnnxService.cs");
            Assert.Contains("public static bool HasAnyOnnxModel()", onnx);
            // 一个来源:判定直接用探测那套模型集,不自己再拼一份(漏了 anime/wdn/RIFE 就是原事故)
            Assert.Contains("return FindProbeModel() != null;", onnx);
            Assert.Contains("FindWdnModel() ?? FindModel() ?? FindWaifu2xModel() ?? FindAnimeVideoModel()", onnx);
            Assert.Contains("?? ALHPro.RifeOnnxService.FindModel();", onnx);   // 只有 rife49.onnx 的机器也要能探
            // 缺目录不许让 EnumerateFiles 抛(抛了会被 catch 吞成"一条 ONNX 都没有")
            Assert.Equal(3, Count(onnx, "if (!Directory.Exists(root)) return null;"));

            var vk = ReadFile("ImgUpscalerUI", "VulkanCheck.cs");
            Assert.Contains("return ALHPro.EsrganOnnxService.HasAnyOnnxModel();", vk);
            Assert.DoesNotContain("|| ALHPro.RifeOnnxService.Available();", vk);   // 不许再自己拼一份

            var mp = ReadFile("ImgUpscalerUI", "Views", "MainPage.xaml.cs");
            Assert.Contains("if (ALHPro.EsrganOnnxService.HasAnyOnnxModel())", mp);
            Assert.DoesNotContain("if (ALHPro.EsrganOnnxService.FindModel() != null)", mp);
        }

        /// <summary>硬件画像的缓存键必须含 DirectML 探测结论:Vulkan 挂掉但 DML 可用的机器,
        /// 结论要等 DML 探测跑完才准;少了这一位,启动早期算出的"没有 GPU"会一路缓存到进程结束。</summary>
        [Fact]
        public void Profile_cache_key_includes_the_directml_verdict()
        {
            var sr = ReadFile("ImgUpscalerUI", "SafeRender.cs");
            Assert.Contains("ALHPro.EsrganOnnxService.DmlFallbackOk);", sr);
        }

        private static int Count(string src, string needle)
        {
            int n = 0, at = 0;
            while ((at = src.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { n++; at += needle.Length; }
            return n;
        }

        private static string ReadFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var cand = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
                if (File.Exists(cand))
                {
                    // 2026-11-14: 大类拆成 partial 后,契约断言要看"整个类"的源码,不能只看主文件。
                    var d2 = Path.GetDirectoryName(cand)!;
                    var stem = Path.GetFileName(cand).Split('.')[0];
                var parts2 = Directory.GetFiles(d2, stem + ".*.cs")
                    .Where(x => !string.Equals(x, cand, StringComparison.OrdinalIgnoreCase));
                return string.Concat(new[] { cand }.Concat(parts2.OrderBy(x => x, StringComparer.Ordinal)).Select(File.ReadAllText));
                }
                dir = dir.Parent;
            }
            throw new FileNotFoundException("找不到仓库文件: " + string.Join('/', parts));
        }
    }
}