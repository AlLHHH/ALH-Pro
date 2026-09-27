using AlhPro.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【t56 · 2026-09-27 · 依据用户诊断包 ALHPro_Diag_20260927_1355】
/// 那台机(AMD Ryzen 5 3600 / 15.9GB / **GTX 1050 Ti 4GB,空闲显存只剩 0.7GB**)的报告里有三句互相打脸的话:
///   · `设备信息.txt:11` 逐引擎行:`ncnn 探测(realesrgan, GPU 0): 实测不可用 → 走 ONNX 稳定引擎`
///     —— 而那次探测**根本没跑完**:日志 13:55:08 起探测 → 13:56:09 `60 秒无响应(疑似 hang)被强杀`
///     → 退避 3 秒重试 → 13:56:38 `A task was canceled.` → 同一次日志 13:56:38 自己写着
///     「因取消未落盘结论(这不代表该卡不可用),下次照旧试」;
///   · 同一份文件 `:13` 汇总行:`ncnn 实测结论(导出时实时): realesrgan2026=未测(首次处理时自动实测)`;
///   · 启动自检报告 `:23`:`可用性:GPU 加速可用,可正常进行图片放大、AI 抠图与视频处理`。
///   三句里只有第三句是对的(设备层可用)、第二句也不算错,第一句是假话 —— 用户读到的正是那句。
///   `ncnn-probe.txt` 也证明"没落盘":里面只有 RIFE 那一条结论,realesrgan/waifu2x 一条都没有。
///
/// 本文件钉两件事(对应合同 E1/E2):
/// **E1** 「实测不可用」只能由"探测**真的跑完**并判不可用"得出;超时被强杀 / 进程起不来 / 被取消 /
///   空闲显存不足一律写「本次未测通(原因:…)+ 不落盘结论 + 下次照旧试」,措辞只有一处来源
///   (<see cref="NcnnProbeWording"/>)。**真话没丢**:跑完并判不可用时照旧得出「实测不可用」。
/// **E2** 生产帧尺寸(1080×1920)探测前先看空闲显存;明显不足就**不跑**那次探测,给一句"为什么 + 怎么办",
///   并且**不落盘任何结论**(阈值依据见 EngineService.ProductionProbeMinFreeVramGB 的注释:同一台机
///   2.6/2.7GB 连过 5 次 vs 0.7GB 超时被强杀,取保守下限 1.5GB,属估计未逐档实测)。
///
/// 【证据分层】措辞与判据是 Core 纯函数 ⇒ 这里**直接执行**(喂 1355 的真实序列);UI 侧接线
/// (MainPage 逐引擎行 / VulkanCheck 报告 / EngineService 闸门顺序)按本仓库惯例用源码断言钉住。</summary>
public class NcnnProbeHonestyTests
{
    private const string EngineCs = "EngineService.cs";
    private const string MainCs = "MainPage.xaml.cs";
    private const string VulkanCs = "VulkanCheck.cs";
    private const string CoreVerdictsCs = "NcnnModelVerdicts.cs";

    // ───────────────────────── E1:没跑完 ≠ 实测不可用 ─────────────────────────

    /// <summary>**E1 定点证据(可执行)**:把 1355 的真实序列喂进判据 —— 第 1 次 60 秒无响应被强杀 → 退避
    /// → 第 2 次被取消。文本必须是"本次未测通(原因:…)+ 不落盘结论 + 下次照旧试",而且**不许**出现
    /// 「实测不可用」(那句是这次假结论的原话)。</summary>
    [Fact]
    public void The_real_1355_sequence_reads_as_not_concluded()
    {
        // 1355 的真实形态:第一次 Hang(60 秒无响应被强杀),随后任务被取消(取消闸门 → 不落盘)
        string reason = NcnnProbeWording.NotConcludedReasonFrom(ProbeFailureKind.Hang, "60 秒无响应", cancelled: true);
        Assert.Contains("60 秒无响应(疑似 hang)被强杀", reason);       // 与日志原话同口径
        Assert.Contains("因取消未重试", reason);
        string text = NcnnProbeWording.NotConcluded(reason);
        Assert.Contains("本次未测通", text);
        Assert.Contains("原因:", text);
        Assert.Contains("60 秒无响应", text);
        Assert.Contains("不落盘结论", text);                            // 关键:没写成"结论已落盘"
        Assert.Contains("这不代表该卡不可用", text);                     // 关键:不吓唬用户
        Assert.Contains("下次照旧试", text);
        Assert.DoesNotContain("实测不可用", text);                      // ← 1355 里那句假话
        // 汇总行用的短形式同源(不带长句)
        string shortForm = NcnnProbeWording.NotConcludedShort(reason);
        Assert.Contains("本次未测通", shortForm);
        Assert.Contains("不落盘", shortForm);
        Assert.Contains("下次照旧试", shortForm);
        Assert.DoesNotContain("实测不可用", shortForm);
        // 其它两种"没跑完"的形态也必须是"未测通"(取消不是唯一的入口)
        Assert.Contains("进程起不来", NcnnProbeWording.NotConcludedReasonFrom(ProbeFailureKind.StartupFailed, "exit=1", cancelled: false));
        Assert.Contains("因取消未重试", NcnnProbeWording.NotConcludedReasonFrom(ProbeFailureKind.StartupFailed, "", cancelled: true));
    }

    /// <summary>**E1 接线**:取消那一路与"没跑完"那一路都必须①记账(供报告写原因)②用统一措辞写日志,
    /// 而且都必须在落盘之前 return(结论一条都不写)。</summary>
    [Fact]
    public void The_cancel_and_inconclusive_paths_record_a_reason_and_never_write_a_verdict()
    {
        string code = CodeOnly(ReadRepoFile("ImgUpscalerUI", EngineCs));
        int probe = code.IndexOf("public static async Task<bool> EnsureNcnnProbeAsync(", StringComparison.Ordinal);
        int cancelGuard = code.IndexOf("if (ct.IsCancellationRequested || outcome.Cancelled)", probe, StringComparison.Ordinal);
        int inconclusiveGuard = code.IndexOf("if (!AlhPro.Core.NcnnProbeWording.IsConclusiveOutcome(outcome.Kind, cancelled: false))", probe, StringComparison.Ordinal);
        int save = code.IndexOf("SaveNcnnVerdict(engine, gpuId, model, ok,", probe, StringComparison.Ordinal);
        Assert.True(cancelGuard > probe, "必须先有取消闸门");
        Assert.True(inconclusiveGuard > cancelGuard, "取消闸门之后还要有「没跑完就不落盘」的闸门");
        Assert.True(save > inconclusiveGuard, "两道闸门都必须排在唯一那次落盘之前");
        // 两道闸门里都要:记账 + 统一措辞的日志 + 立刻 return
        foreach (var (from, to) in new[] { (cancelGuard, inconclusiveGuard), (inconclusiveGuard, save) })
        {
            string block = code[from..to];
            Assert.Contains("NoteNotConcluded(engine, gpuId,", block);
            Assert.Contains("AlhPro.Core.NcnnProbeWording.NotConcluded(", block);
            Assert.Contains("return false;", block);
            Assert.DoesNotContain("SaveNcnnVerdict", block);           // 一个字都不许落
        }
        // 判据是 Core 的纯函数,EngineService 不许自己写 if(kind==Hang) 那种
        Assert.Contains("IsConclusiveOutcome", code);
        Assert.DoesNotContain("outcome.Kind == AlhPro.Core.ProbeFailureKind.Hang", code);
        // 报告口径:逐引擎行只看结论缓存(TryGetNcnnVerdict),不看 EnsureNcnnProbeAsync 的 bool
        string describe = Block(ReadRepoFile("ImgUpscalerUI", EngineCs),
            "public static (AlhPro.Core.NcnnProbeReport Kind, string Text) DescribeProbeAttempt(string engine, int gpuId)",
            "public const double ProductionProbeMinFreeVramGB");
        string describeCode = CodeOnly(describe);
        Assert.Contains("bool? verdict = TryGetNcnnVerdict(engine, gpuId);", describeCode);
        Assert.Contains("AlhPro.Core.NcnnProbeReport.NotConcluded", describeCode);
        // ★ 逐引擎报告行不许再消费那个 bool(它就是 1355 假结论的来源)
        string mp = CodeOnly(ReadRepoFile("ImgUpscalerUI", "Views", MainCs));
        Assert.Contains("var probe = ALHPro.EngineService.DescribeProbeAttempt(eng, probeGpu);", mp);
        Assert.DoesNotContain("bool ok = await ALHPro.EngineService.EnsureNcnnProbeAsync", mp);
    }

    /// <summary>**E1 真话没丢**:探测**跑完**并判不可用时,照旧得出「实测不可用」(而且 Real-CUGAN 那条出路
    /// 照旧按"没有 ONNX 版本"说)。判据 <see cref="NcnnProbeWording.IsConclusiveOutcome"/> 逐形态钉住:
    /// 跑完的形态(崩溃退出码 / 无产出 / 空产出 / 坏帧 / 引擎文件不在)才允许落盘并说"不可用"。</summary>
    [Fact]
    public void A_completed_probe_that_judged_unavailable_still_says_unavailable()
    {
        // 跑完 ⇒ 允许落盘 ⇒ 报告说"实测不可用"
        foreach (var kind in new[] { ProbeFailureKind.CrashExitCode, ProbeFailureKind.NoOutput,
                                     ProbeFailureKind.EmptyOutput, ProbeFailureKind.DefectiveFrame, ProbeFailureKind.EngineMissing })
            Assert.True(NcnnProbeWording.IsConclusiveOutcome(kind, cancelled: false), kind + " 是'跑完并得出形态'的失败,必须允许落盘");
        Assert.True(NcnnProbeWording.IsConclusiveOutcome(ProbeFailureKind.None, cancelled: false));   // 通过
        // 没跑完 ⇒ 不许落盘
        Assert.False(NcnnProbeWording.IsConclusiveOutcome(ProbeFailureKind.Hang, cancelled: false));
        Assert.False(NcnnProbeWording.IsConclusiveOutcome(ProbeFailureKind.StartupFailed, cancelled: false));
        Assert.False(NcnnProbeWording.IsConclusiveOutcome(ProbeFailureKind.None, cancelled: true));
        // 落盘后的两句话:别的引擎"走 ONNX 稳定引擎";Real-CUGAN 没有 ONNX 版本,必须改说真话
        Assert.Equal("实测不可用 —— 走 ONNX 稳定引擎", NcnnProbeWording.UnavailableText("realesrgan"));
        Assert.Contains("明确拒绝", NcnnProbeWording.UnavailableText(RealCugan.EngineName));
        Assert.DoesNotContain("走 ONNX", NcnnProbeWording.UnavailableText(RealCugan.EngineName));
        Assert.Equal("实测可用 → 走 ncnn-Vulkan", NcnnProbeWording.AvailableText);
        // 汇总行:真有不可用结论时照旧说"实测不可用"(别把真话一起删掉)
        var entries = new List<NcnnModelVerdicts.Entry> { new("realesrgan2026|0|", false, 0) };
        Assert.Contains("实测不可用", NcnnModelVerdicts.Describe(entries, "realesrgan2026", 0));
        // 而"没测通"时汇总行要带上统一口径的原因(不再只写一个"未测")
        string nc = NcnnModelVerdicts.Describe(new List<NcnnModelVerdicts.Entry>(), "realesrgan2026", 0, "空闲显存不足(实测 0.7GB,低于下限 1.5GB)");
        Assert.Contains("本次未测通", nc);
        Assert.Contains("空闲显存不足", nc);
        Assert.DoesNotContain("实测不可用", nc);
        // 没记录过原因时保持原样(既有调用点行为不变)
        Assert.Contains("未测", NcnnModelVerdicts.Describe(new List<NcnnModelVerdicts.Entry>(), "realesrgan2026", 0));
    }

    // ───────────────────────── 三处报告点的口径关系 ─────────────────────────

    /// <summary>**三处报告点各说什么范围**(用户在 1355 里读到的正是「实测不可用」+「未测」+「GPU 加速可用」同框):
    ///   ① 逐引擎探测行 = **这一次探测**的结果(可用 / 不可用 / 未测通);
    ///   ② `ncnn 实测结论(导出时实时)` 汇总行 = **落盘结论**的汇总(带"未测通"原因);
    ///   ③ 启动自检报告(VulkanCheck)= **设备/驱动这一层**的快照(能不能被 Vulkan 枚举),
    ///      它里面的"模型兼容性"逐条也按同一口径说"未测通"(不再只是一个裸"未测")。
    /// 这条红了 = 有人又把三处的范围混着说(或把"设备可用"读成"引擎可用")。</summary>
    [Fact]
    public void The_three_report_points_declare_their_scope()
    {
        string mp = CodeOnly(ReadRepoFile("ImgUpscalerUI", "Views", MainCs));
        Assert.Contains("上面逐条=这一次探测的结果;本行=落盘结论的汇总;下面的 Vulkan 自检报告=启动时设备层快照", mp);
        Assert.Contains("Vulkan 自检报告(以下为本次启动时的快照,可能早于上面的实时结论)", mp);
        string vulkan = CodeOnly(ReadRepoFile("ImgUpscalerUI", VulkanCs));
        // 报告范围声明(源码里是拼接的字符串,按三段分别断言)
        Assert.Contains("报告范围:以下只讲【设备/驱动这一层】能不能用;每个引擎走 ncnn 还是 ONNX 由", vulkan);
        Assert.Contains("【生产帧尺寸实测】决定(见下「模型兼容性」逐条;那次实测没跑完时它会如实说", vulkan);
        Assert.Contains("「没测通、不落盘、下次照旧试」,不代表这张卡不可用)", vulkan);
        Assert.Contains("可用性:GPU 加速可用", vulkan);                  // 设备层结论仍在(它说的是"设备可用")
        // 自检报告里"没测过/没测通"两件事必须分开说,且后者引 Core 的统一口径
        Assert.Contains("AlhPro.Core.NcnnProbeWording.NotConcludedShort(reason)", vulkan);
        Assert.Contains("EngineService.LastNotConcludedReason(engine, AppSettings.GpuIndex)", vulkan);
        // 汇总行那边也要把原因接上(两处报告点同源)
        string eng = CodeOnly(ReadRepoFile("ImgUpscalerUI", EngineCs));
        Assert.Contains("NcnnModelVerdicts.Describe(NcnnVerdictEntries(), EngineId(eng), gpuId,", eng);
        Assert.Contains("LastNotConcludedReason(eng, gpuId)", eng);
    }

    /// <summary>**单一口径**:"本次未测通…"这句话**只有一处定义**(<see cref="NcnnProbeWording"/>),
    /// 其它三处报告点只引用它、不许自己拼。这条红了 = 措辞又开始散开(必然再次出现三句打脸)。
    /// 【怎么判"只有一处】三处 UI 源码里**一个字面量都不许有**,Core 里也只许出现在 NcnnProbeWording 内。</summary>
    [Fact]
    public void The_not_concluded_wording_has_exactly_one_definition()
    {
        foreach (var (file, dir) in new[] { (EngineCs, "ImgUpscalerUI"), (MainCs, "ImgUpscalerUI/Views"),
                                            (VulkanCs, "ImgUpscalerUI") })
        {
            string ui = CodeOnly(ReadRepoFile(dir, file));
            Assert.DoesNotContain("本次未测通", ui);                    // 一个字面量都不许有
            Assert.DoesNotContain("不落盘结论", ui);
        }
        string core = ReadRepoFile("AlhPro.Core", CoreVerdictsCs);
        Assert.Contains("public static string NotConcluded(string? reason)", core);
        Assert.Contains("public static string NotConcludedShort(string? reason)", core);
        // 定义只许出现在 NcnnProbeWording 这一个类里
        string coreCode = CodeOnly(core);
        int cls = coreCode.IndexOf("public static class NcnnProbeWording", StringComparison.Ordinal);
        Assert.True(cls > 0, "找不到 NcnnProbeWording(NcnnModelVerdicts.cs 里定义)");
        Assert.Equal(Count(coreCode, "本次未测通"), Count(coreCode[cls..], "本次未测通"));
        // 三处 UI 报告点确实都在引用它(不是"删掉了说不通")
        Assert.Contains("NcnnProbeWording.NotConcluded(", CodeOnly(ReadRepoFile("ImgUpscalerUI", EngineCs)));
        Assert.Contains("NcnnProbeWording.NotConcludedShort(reason)", ReadRepoFile("ImgUpscalerUI", VulkanCs));
    }

    // ───────────────────────── E2:生产尺寸探测前的空闲显存闸 ─────────────────────────

    /// <summary>**E2 接线**:生产帧尺寸探测之前必须读一次空闲显存(仓库既有的 nvidia-smi 口径),明显不足时
    /// **不跑那次探测**、**不落盘任何结论**,并给出一句"为什么 + 怎么办"。测不到空闲显存(AMD/Intel)时
    /// 一律照旧跑 —— 不许拿"未知"当"不足"。</summary>
    [Fact]
    public void The_production_probe_is_gated_by_free_vram_without_writing_a_verdict()
    {
        string code = CodeOnly(ReadRepoFile("ImgUpscalerUI", EngineCs));
        int probe = code.IndexOf("public static async Task<bool> EnsureNcnnProbeAsync(", StringComparison.Ordinal);
        int gate = code.IndexOf("if (VramTooLowForProductionProbe(out double freeGb, out double needGb))", probe, StringComparison.Ordinal);
        int firstProbeLog = code.IndexOf("首次真机探测(生产帧尺寸 1080×1920", probe, StringComparison.Ordinal);
        int save = code.IndexOf("SaveNcnnVerdict(engine, gpuId, model, ok,", probe, StringComparison.Ordinal);
        Assert.True(gate > probe, "探测前必须有空闲显存闸");
        Assert.True(firstProbeLog > gate, "闸门要排在「首次真机探测」那条日志之前(不足时不该先说「要探了」)");
        Assert.True(save > gate, "闸门在落盘之前");
        string gateBlock = code[gate..firstProbeLog];
        Assert.Contains("NcnnProbeWording.VramShortfallReason(freeGb, needGb)", gateBlock);   // 原因(单一口径)
        Assert.Contains("NcnnProbeWording.NotConcluded(reason)", gateBlock);                // 报告口径
        Assert.Contains("NcnnProbeWording.VramShortfallHint(freeGb, needGb)", gateBlock);   // 给用户的一句话
        Assert.Contains("return false;", gateBlock);
        Assert.DoesNotContain("SaveNcnnVerdict", gateBlock);                                 // 不许落盘
        // 闸门判据本体:测不到就不闸;只在"真测到且低于下限"时跳过
        string helper = Block(ReadRepoFile("ImgUpscalerUI", EngineCs),
            "private static bool VramTooLowForProductionProbe(out double freeGb, out double needGb)",
            "public static string NcnnProbeCacheFilePath => NcnnProbeCacheFile;");
        string helperCode = CodeOnly(helper);
        Assert.Contains("if (!SafeRender.FreeVramMeasured) return false;", helperCode);
        Assert.Contains("freeGb = SafeRender.FreeVramGB;", helperCode);
        Assert.Contains("return freeGb < ProductionProbeMinFreeVramGB;", helperCode);
        // 给用户的那句话:说清为什么 + 怎么办,而且不许说成"不可用"
        // (1.5 = EngineService.ProductionProbeMinFreeVramGB;测试项目引用不到 App 程序集,
        //  所以这个数字由上面/下一条 Fact 的源码断言钉住 —— 常量一改,那条就红)
        string hint = NcnnProbeWording.VramShortfallHint(0.7, 1.5);
        Assert.Contains("0.7GB", hint);
        Assert.Contains("1.5GB", hint);
        Assert.Contains("空闲显存", hint);
        Assert.Contains("关掉", hint);
        Assert.Contains("CPU", hint);
        Assert.Contains("不会记成「不可用」", hint);
        Assert.DoesNotContain("实测不可用", hint);
        // 记进"未测通原因"的那句同样是单一口径(汇总行/逐引擎行/自检报告都引用它)
        string reason = NcnnProbeWording.VramShortfallReason(0.7, 1.5);
        Assert.Contains("空闲显存不足", reason);
        Assert.Contains("0.7GB", reason);
        Assert.Contains("1.5GB", reason);
    }

    /// <summary>**E2 阈值必须有依据(不许拍数字)**:常量值 + 注释里的两条依据必须都在,而且**属估计**要写明。
    /// 依据①=同一台机的实测对照(1355:空闲 2.6/2.7GB 连过 5 次 vs 0.7GB 被强杀);
    /// 依据②=仓库既有显存门槛(SafeRender.GetVideoConcurrency 的"空闲 ≥3GB 才 2 路",取一半)。
    /// 这条红了 = 阈值被改成拍脑袋的数字,或依据被删(下一个人再也不敢动它)。</summary>
    [Fact]
    public void The_vram_threshold_cites_its_evidence_and_is_marked_as_an_estimate()
    {
        string src = ReadRepoFile("ImgUpscalerUI", EngineCs);
        Assert.Contains("public const double ProductionProbeMinFreeVramGB = 1.5;", CodeOnly(src));
        string doc = Block(src, "/// <summary>【2026-09-27 · E2】生产帧尺寸探测所需的**空闲显存下限**(GB)。", "public const double ProductionProbeMinFreeVramGB = 1.5;");
        Assert.Contains("估计", doc);                       // 属估计
        Assert.Contains("未逐档实测", doc);
        Assert.Contains("2.6 / 2.7 GB", doc);               // 通过时的实测读数
        Assert.Contains("0.7 GB", doc);                     // 失败时的实测读数
        Assert.Contains("连续通过 5 次", doc);
        Assert.Contains("60 秒无响应被强杀", doc);
        Assert.Contains("SafeRender.GetVideoConcurrency", doc);   // 引用仓库既有门槛
        Assert.Contains("vramOk2 = FreeVramGB >= 3", doc);
        // 阈值只有一处定义、只被闸门引用一次
        string code = CodeOnly(src);
        Assert.Equal(1, Count(code, "ProductionProbeMinFreeVramGB = 1.5"));
        Assert.Equal(1, Count(code, "freeGb < ProductionProbeMinFreeVramGB"));
    }

    // ───────────────────────── 工具 ─────────────────────────

    private static string Block(string src, string start, string end)
    {
        int a = src.IndexOf(start, StringComparison.Ordinal);
        Assert.True(a > 0, "找不到起点标记:" + start);
        int b = src.IndexOf(end, a + start.Length, StringComparison.Ordinal);
        Assert.True(b > a, $"在 {start} 之后找不到终点标记:{end}");
        return src[a..b];
    }

    private static int Count(string src, string needle)
    {
        int n = 0, at = 0;
        while ((at = src.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { n++; at += needle.Length; }
        return n;
    }

    /// <summary>剥掉 `//` 行注释(逐字符扫引号)。不剥注释的源码断言会被"注释掉那一行"骗过去(本仓库踩过)。</summary>
    private static string CodeOnly(string src)
    {
        var sb = new System.Text.StringBuilder(src.Length);
        foreach (var line in src.Split('\n'))
        {
            bool inStr = false, inChar = false;
            int cut = line.Length;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inStr)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '"') inStr = false;
                    continue;
                }
                if (inChar)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '\'') inChar = false;
                    continue;
                }
                if (c == '"') { inStr = true; continue; }
                if (c == '\'') { inChar = true; continue; }
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') { cut = i; break; }
            }
            sb.Append(line[..cut]).Append('\n');
        }
        return sb.ToString();
    }

    private static string ReadRepoFile(params string[] parts)
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
