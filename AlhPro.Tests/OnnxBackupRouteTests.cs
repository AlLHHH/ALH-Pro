// ONNX 只是保命兜底,而且必须能回到 ncnn(2026-11-14 用户定调)
// 【为什么需要它】用户原话(2026-11-14):「ONNX 我感觉也是很慢的一个渲染」「A 可以 不过最好就是让用户能稳定的走 ncnn」。
// 同一张卡上 ONNX 比 ncnn-Vulkan 慢 20~60 倍(RTX 5060 Ti 8GB:约 6 秒/帧 vs 0.1~0.3 秒/帧;
// RTX 3050 4GB 约 11 秒/帧;本机 RTX 4060 的 ncnn animevideov3 330~400 ms/帧)。
// 这里钉住两件事,防止以后又被改回去:
//   ① 走备用路线时,开跑前就说清"慢 20~60 倍 + 大概多久 + 怎么回正常路线",而不是让用户跑到一半以为坏了;
//   ② Vulkan 恢复后必须能回到 ncnn —— 开跑前重试一次枚举,恢复就立刻改回 ncnn 并重测,
//      不许因为一条旧的否定结论把整个会话钉死在慢路线上。
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests
{
    public class OnnxBackupRouteTests
    {
        /// <summary>备用路线提示必须在开跑前就能看到(与 CPU 提示同一个显示位),且三样东西齐全:
        /// 慢多少、大概多久(有实测才给数字)、怎么回正常路线。缺一条就退回"用户被吓退/以为坏了"。</summary>
        [Fact]
        public void Backup_route_hint_states_slowdown_estimate_and_way_back()
        {
            var vv = ReadFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
            int start = vv.IndexOf("private string? BackupRouteHint(", StringComparison.Ordinal);
            Assert.True(start > 0, "找不到 BackupRouteHint —— 备用路线的说明被删了?");
            int end = vv.IndexOf("private (double perFrame, double stageMin, long frames)? EstimateCpuUpscale(", start, StringComparison.Ordinal);
            Assert.True(end > start, "BackupRouteHint 与 EstimateCpuUpscale 的结构变了,测试需要更新");
            var block = vv.Substring(start, end - start);

            Assert.Contains("20~60 倍", block);          // 慢多少(同卡实测比值)
            Assert.Contains("* 20 / 60.0", block);       // 预估下界真的乘了 20 倍(不是只在文案里说)
            Assert.Contains("* 60 / 60.0", block);       // 预估上界真的乘了 60 倍
            Assert.Contains("重新检测", block);           // 怎么回正常路线
            Assert.Contains("重启", block);               // 驱动升级未重启是最常见的成因
            Assert.Contains("FormatMinutes(", block);     // 有实测才给区间数字
            Assert.Contains("给不出总耗时", block);        // 拿不到实测时明确说给不出,不编数字
        }

        /// <summary>备用路线只在"Vulkan 用不了 + DirectML 还能跑"时出现:
        /// DML 也不可用时归 CPU 提示管,两条提示不许同时说;Vulkan 正常时这条永远不显示。</summary>
        [Fact]
        public void Backup_route_hint_is_mutually_exclusive_with_cpu_hint()
        {
            var vv = ReadFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
            int start = vv.IndexOf("private string? BackupRouteHint(", StringComparison.Ordinal);
            int end = vv.IndexOf("private (double perFrame, double stageMin, long frames)? EstimateCpuUpscale(", start, StringComparison.Ordinal);
            var block = vv.Substring(start, end - start);

            int vulkanOk = block.IndexOf("if (!ALHPro.VulkanCheck.NoGpuDetected) return null;", StringComparison.Ordinal);
            int dmlDead = block.IndexOf("if (!ALHPro.VulkanCheck.DmlAvailable()) return null;", StringComparison.Ordinal);
            int route = block.IndexOf("OnnxUpscaleRouteLikely()", StringComparison.Ordinal);
            Assert.True(vulkanOk > 0, "备用路线没有排除「Vulkan 正常」这种情况");
            Assert.True(dmlDead > vulkanOk, "DML 不可用时必须让位给 CPU 提示(顺序/条件都不对)");
            Assert.True(route > dmlDead, "没走 ONNX 路线时不该提示备用路线");

            // 显示位:CPU 提示不放 → 先问备用路线(同一个 TextBlock,不会两句话叠在一起)
            int refresh = vv.IndexOf("private void RefreshCpuFallbackHint(", StringComparison.Ordinal);
            var refreshBlock = vv.Substring(refresh, vv.IndexOf("private string? BackupRouteHint(", refresh, StringComparison.Ordinal) - refresh);
            Assert.Contains("BackupRouteHint(", refreshBlock);
        }

        /// <summary>Vulkan 恢复后必须能回到 ncnn:开跑前重试枚举 + 强制重测一次结论 + 把自检降级掉的 GpuIndex 放回去。</summary>
        [Fact]
        public void Start_of_run_retries_vulkan_and_falls_back_to_ncnn_when_it_recovers()
        {
            var vk = ReadFile("ImgUpscalerUI", "VulkanCheck.cs");
            int helper = vk.IndexOf("public static bool TryRecoverVulkan()", StringComparison.Ordinal);
            Assert.True(helper > 0, "缺少 TryRecoverVulkan —— 恢复后回不到 ncnn");
            var helperBlock = vk.Substring(helper, Math.Min(900, vk.Length - helper));
            Assert.Contains("if (!NoGpuDetected) return GpuAvailable;", helperBlock);
            Assert.Contains("ReProbe();", helperBlock);
            // 不许动用户已保存的结论(那是「重新检测」按钮的语义)
            Assert.DoesNotContain("AppSettings.VulkanCheckDone = false", helperBlock);

            var vv = ReadFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
            int pre = vv.IndexOf("bool vulkanRecoveredNow = false;", StringComparison.Ordinal);
            Assert.True(pre > 0, "开跑前没有重试 Vulkan");
            int probe = vv.IndexOf("EnsureNcnnProbeDecisionAsync(probeEngine, gpuId, probePlan.Model, cts.Token, force: vulkanRecoveredNow)", pre, StringComparison.Ordinal);
            Assert.True(probe > pre, "Vulkan 恢复后没有强制重测 —— 旧结论会把这次仍钉在 ONNX 路线上");
            Assert.Contains("AppSettings.GpuIndex = 0;", vv.Substring(pre, probe - pre));
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