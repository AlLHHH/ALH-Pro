// 备用引擎(ONNX)不许静默接管(2026-11-16 用户定调 m11897)
// 【用户原话】「不用 就多次重试 如果还是不行 就提示 并且用户可以选择取消和使用备用引擎 但是会慢」。
// 这里钉住三件事:
//   ① ncnn 探测"不可用"不再一次定生死 —— 至少再补测两轮(共 3 次)才认;
//   ② 超分/补帧真要掉到 ONNX 时,必须先拿到用户答复,答复否就直接停任务;
//   ③ 那个询问框本身要有「取消」并且默认落在取消上,不许回车就切到慢路上。
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests
{
    public class BackupEngineConsentTests
    {
        [Fact]
        public void Unavailable_verdict_is_reprobed_before_being_believed()
        {
            var es = ReadFile("ImgUpscalerUI", "EngineService.cs");
            Assert.Contains("public static readonly int[] NcnnUnavailableReprobeDelaysMs = { 3000, 8000 };", es);
            int start = es.IndexOf("public static async Task<AlhPro.Core.NcnnProbeReport> EnsureNcnnProbeDecisionAsync(", StringComparison.Ordinal);
            Assert.True(start > 0, "找不到三态探测入口");
            int end = es.IndexOf("public static async Task<bool> EnsureNcnnProbeAsync(", start, StringComparison.Ordinal);
            Assert.True(end > start, "探测入口结构变了,测试需要更新");
            var block = es.Substring(start, end - start);

            Assert.Contains("NcnnUnavailableReprobeDelaysMs.Length", block);            // 真的按表重试
            Assert.Contains("force: true", block);                                      // 重试必须绕过上次的失败缓存
            Assert.Contains("if (decision != AlhPro.Core.NcnnProbeReport.Unavailable)", block); // 只在"不可用"时重试
            Assert.Contains("连测", block);                                            // 连试次数进日志,能对上诊断包
        }

        [Fact]
        public void Upscale_and_interp_fallbacks_ask_the_user_first()
        {
            var vs = ReadFile("ImgUpscalerUI", "VideoService.cs");

            // 超分:询问必须在把 upGpu 打成 -1(触发 ONNX 分支)之前
            int upAsk = vs.IndexOf("\"超分引擎在这张显卡上跑不起来\"", StringComparison.Ordinal);
            int upOnnx = vs.IndexOf("upGpu = -1;          // 触发下方 ONNX 分支", StringComparison.Ordinal);
            Assert.True(upAsk > 0 && upOnnx > upAsk, "超分掉 ONNX 之前没有询问用户");

            // 补帧:两处分支都要问
            var interpAsks = IndexesOf(vs, "\"补帧引擎在这张显卡上跑不起来\"");
            Assert.Equal(2, interpAsks.Count);
            var interpOnnx = IndexesOf(vs, "interpGpu = -1;   // 本视频后续补帧 API 全部走 ONNX");
            Assert.Equal(2, interpOnnx.Count);
            for (int i = 0; i < 2; i++)
                Assert.True(interpAsks[i] < interpOnnx[i], $"第 {i + 1} 处补帧掉 ONNX 之前没有询问用户");

            // 答复否 ⇒ 停止,不"先跑起来再说"
            Assert.Contains("本次任务已停止", vs);
        }

        [Fact]
        public void A_missing_asker_falls_back_to_old_behaviour_instead_of_crashing()
        {
            var vs = ReadFile("ImgUpscalerUI", "VideoService.cs");
            int start = vs.IndexOf("private static async Task<bool> ConfirmBackupEngineAsync(", StringComparison.Ordinal);
            Assert.True(start > 0, "找不到 ConfirmBackupEngineAsync");
            var block = vs.Substring(start, vs.IndexOf("public static async Task<string> ProcessVideoAsync(", start, StringComparison.Ordinal) - start);
            Assert.Contains("if (ask is null) return true;", block);   // 无界面路径(单测)保持旧行为
            Assert.Contains("[备用引擎]", block);                      // 用户的选择必须进日志
        }

        [Fact]
        public void The_dialog_defaults_to_cancel_and_passes_the_asker_in()
        {
            var vv = ReadFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
            Assert.Contains("askBackupEngine: _backupEngineApproved ? null : AskBackupEngineAsync,", vv);   // 界面确实把它接上了

            int start = vv.IndexOf("private Task<bool> AskBackupEngineAsync(string what, string reason)", StringComparison.Ordinal);
            Assert.True(start > 0, "找不到询问框实现");
            var block = vv.Substring(start, vv.IndexOf("private async Task AskBlackwellOldEngineAsync(", start, StringComparison.Ordinal) - start);

            Assert.Contains("DefaultButton = ContentDialogButton.Close", block);  // 回车 = 取消,不误切慢路
            Assert.Contains("CloseButtonText = \"取消\"", block);
            Assert.Contains("慢 20~60 倍", block);                                // 代价必须写在按钮旁边
            Assert.Contains("重启", block);                                       // 先给"怎么回正常路线"
            Assert.Contains("更新显卡驱动", block);
            Assert.Contains("DispatcherQueue.TryEnqueue", block);                 // 后台线程问话必须回 UI 线程
        }

        [Fact]
        public void The_pre_run_check_offers_cancel_instead_of_only_informing()
        {
            // 【2026-11-16 真机复验发现的缺口】开跑前那道检查原来只弹「知道了」就自动改用 ONNX,
            // 用户压根没得选。现在必须:问 → 选取消就一批都不跑(而不是先跑起来再说)。
            var vv = ReadFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");

            Assert.Contains("if (await AskBackupEngineAsync($\"", vv);                 // 开跑前真的问了
            Assert.DoesNotContain("await AskBlackwellCompatibleAsync(probeLabel);", vv); // 不再"只告知"
            Assert.Contains("_backupEngineApproved = false;", vv);                     // 每批重新问
            Assert.Contains("_backupEngineApproved = true;", vv);                      // 同意过才放行

            int cancel = vv.IndexOf("已取消:超分只剩备用引擎", StringComparison.Ordinal);
            Assert.True(cancel > 0, "选取消时没有明确的停止说明");
            var after = vv.Substring(cancel, Math.Min(220, vv.Length - cancel));
            Assert.Contains("return;", after);                                        // 真的停,不是继续跑
        }

        // 【2026-11-16 全面自检抓到】缓存里的一条「不可用」把整段任务静默钉在 ONNX 上:
        // 日志写「超分[realesrgan] 走 ncnn-Vulkan GPU(编号 0)」,随后这批实际是 ONNX,1.16 秒/帧。
        // 定路线之前必须按真实引擎取一次结论(缓存失败会被复核),而不是直接读缓存。
        [Fact]
        public void The_route_is_rechecked_with_the_real_engine_before_it_is_decided()
        {
            var vs = ReadFile("ImgUpscalerUI", "VideoService.cs");

            int recheck = vs.IndexOf("[探测] 超分路线复核:", StringComparison.Ordinal);
            Assert.True(recheck > 0, "定路线之前没有按真实引擎复核探测结论");

            int call = vs.IndexOf("await EngineService.EnsureNcnnProbeDecisionAsync(engine, upGpu", StringComparison.Ordinal);
            Assert.True(call > 0 && call < recheck, "复核日志之前没有真的调用三态探测入口");

            // 复核必须发生在"决定走 ONNX"之前(批内那次判定)
            int decide = vs.IndexOf("AlhPro.Core.UpscaleBackendPlan.UseOnnx(engine, upGpu, onnxPreferredEsrgan", StringComparison.Ordinal);
            Assert.True(decide > call, "批内 ONNX 判定发生在复核之前 —— 又会拿缓存决定路线");
        }

        // 用户自己选 ONNX / 兼容模式不算"探测失败",不许拿它弹框;只有"实测不可用"这一种切换要问
        [Fact]
        public void Switching_to_onnx_because_the_probe_failed_also_asks_the_user()
        {
            var vs = ReadFile("ImgUpscalerUI", "VideoService.cs");
            int flag = vs.IndexOf("bool probeFailedSwitch = upGpu >= 0 && !ncnnProbePassed", StringComparison.Ordinal);
            Assert.True(flag > 0, "没有把「因探测失败而切 ONNX」这一种情况单独标出来");
            Assert.Contains("&& !onnxPreferredEsrgan && !onnxPreferredWaifu2x && !fastMode;", vs);

            var asks = IndexesOf(vs, "\"超分引擎在这张显卡上跑不起来\"");
            Assert.True(asks.Count >= 2, "批内切 ONNX 前没有询问用户(只剩开跑前那一处)");

            // 询问之后必须能停:同一段里要有 throw,不许"问了但照样跑"
            var tail = vs.Substring(asks[asks.Count - 1], Math.Min(400, vs.Length - asks[asks.Count - 1]));
            Assert.Contains("本次任务已停止", tail);
        }

        // 标定样本不再在"ncnn 从没测通"的机器上偷偷走 ONNX:那 10 秒白花,还会写出
        // 「样本输出目录是空的(引擎没产出任何帧)」这种噪音(自检日志实证)。
        [Fact]
        public void Calibration_never_falls_back_to_the_onnx_engine()
        {
            var vs = ReadFile("ImgUpscalerUI", "VideoService.cs");
            Assert.Contains("bool calibOnnx = calibNcnnProbePassed && AlhPro.Core.UpscaleBackendPlan.UseOnnx(", vs);
        }

        // 免探测快速通道的机器不写结论:只看缓存时,一条旧失败会把路线钉在 ONNX,
// 与「超分路线复核 → 可用」自相矛盾(自检实测同一份日志两种声音,1690 ms/帧)。
        [Fact]
        public void The_pass_verdict_uses_the_same_fast_path_as_the_probe()
        {
            var es = ReadFile("ImgUpscalerUI", "EngineService.cs");
            int start = es.IndexOf("public static bool NcnnProbePassed(string engine, int gpuId)", StringComparison.Ordinal);
            Assert.True(start > 0, "找不到 NcnnProbePassed");
            int end = es.IndexOf("// 【2026-09-24】这里曾有一个便捷入口", start, StringComparison.Ordinal);
            Assert.True(end > start, "NcnnProbePassed 结构变了,测试需要更新");
            var block = es.Substring(start, end - start);
            Assert.Contains("TargetGpuIsPlainNvidia(gpuId, engine)) return true;", block);
            Assert.Contains("TryGetEngineVerdictSummary(engine, gpuId) == true", block);
        }

        // 风险判定也必须跟探测核心同口径:免探测的机器不该被一条更早的模型级失败拽到 ONNX。
        [Fact]
        public void The_risk_predicate_uses_the_same_fast_path_as_the_probe()
        {
            var es = ReadFile("ImgUpscalerUI", "EngineService.cs");
            int start = es.IndexOf("public static bool NcnnGpuRisky(string engine, int gpuId", StringComparison.Ordinal);
            Assert.True(start > 0, "找不到 NcnnGpuRisky");
            int end = es.IndexOf("private static bool OldNcnnGpuRiskyHeuristic", start, StringComparison.Ordinal);
            Assert.True(end > start, "NcnnGpuRisky 结构变了,测试需要更新");
            var block = es.Substring(start, end - start);
            int fast = block.IndexOf("TargetGpuIsPlainNvidia(gpuId, engine)) return false;", StringComparison.Ordinal);
            int cache = block.IndexOf("TryGetEngineVerdictSummary(engine, gpuId)", StringComparison.Ordinal);
            Assert.True(fast > 0, "NcnnGpuRisky 没有和探测核心用同一条免探测判据");
            Assert.True(cache > fast, "先读缓存再判快速通道 —— 旧结论仍会赢");
        }

        private static System.Collections.Generic.List<int> IndexesOf(string text, string needle)
        {
            var list = new System.Collections.Generic.List<int>();
            int i = text.IndexOf(needle, StringComparison.Ordinal);
            while (i >= 0)
            {
                list.Add(i);
                i = text.IndexOf(needle, i + 1, StringComparison.Ordinal);
            }
            return list;
        }


        // 【2026-11-16 用户要求】更新提示条里也要有「官方Q群」入口,点开是同一个二维码弹窗。
        [Fact]
        public void The_update_banner_offers_the_official_qq_group()
        {
            var xaml = ReadFile("ImgUpscalerUI", "Views", "MainPage.xaml");
            int bar = xaml.IndexOf("x:Name=\"UpdateBar\"", StringComparison.Ordinal);
            Assert.True(bar > 0, "找不到更新提示条");
            int close = xaml.IndexOf("Content=\"✕\"", bar, StringComparison.Ordinal);
            Assert.True(close > bar, "更新提示条结构变了,测试需要更新");
            var block = xaml.Substring(bar, close - bar);
            Assert.Contains("Content=\"官方Q群\"", block);
            Assert.Contains("Click=\"Community_Click\"", block);
            // 列数必须够用:主列 + 官网/GitHub/网盘/官方Q群 + 关闭 = 6 列
            var cols = xaml.Substring(bar, xaml.IndexOf("</Grid.ColumnDefinitions>", bar, StringComparison.Ordinal) - bar);
            Assert.Equal(6, System.Text.RegularExpressions.Regex.Matches(cols, "<ColumnDefinition ").Count);
        }        private static string ReadFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var cand = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
                if (File.Exists(cand))
                {
                    // 大类拆成 partial 后,契约断言要看"整个类"的源码,不能只看主文件。
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