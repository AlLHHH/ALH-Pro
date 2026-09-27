using AlhPro.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【t54 · 2026-09-26 · 依据用户诊断包 ALHPro_Diag_20260926_1704(GTX 1050 Ti 机)】
/// 这台机器的显卡**其实被正常识别并真的用上了**(`Vulkan 自检:GPU 引擎可用(枚举成功)`、
/// `RIFE rife-v4.13 GPU(1)真机探测通过 → 使用 ncnn-Vulkan 补帧`、DirectML 建会话成功、
/// 按名字重定位 #0→#1 成功),**是软件对用户说了三句错话**,读起来就是"10 系显卡检测不到"。本文件钉住它们已被改正:
///
/// **A · 假警报「⚠ 无 GPU/弱设备:视频超分/补帧将用 CPU 计算」**(bundle 里出现 3 次:15:46、15:47、15:52)
///   根因链:`TotalRamGB` 取的是 `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes`(**GC 眼里的上限**,不是物理内存);
///   8GB 标称机实测报 **7.9** ⇒ `r &gt;= 8` 差 0.1 不成立 ⇒ 掉到 `UltraLow`,而 UltraLow 又被那句
///   "无 GPU…将用 CPU"接住(判据 `Profile == UltraLow || !VulkanCheck.GpuAvailable`)。
///   修法:①来源换 `GlobalMemoryStatusEx.ullTotalPhys`;②判档前用 `NominalGB` 归一到标称容量;
///   ③**把两件事拆成两句话**(只有真的没有可用 GPU 才说"将用 CPU")。
///   另:弱设备原因清单里还有第三句错话(bundle 15:51、16:21:「检测到设备配置较低(**未检测到可用 GPU(Vulkan)**…)」
///   而同一份日志写着 Vulkan 可用)—— 原因是它没等自检跑完,现已与判据侧同口径。
///
/// **B · 自检汇总行自相矛盾「超分 / 补帧引擎: 缺失」**(bundle 的 app-settings.json 原文:同一份报告里
///   `超分 / 补帧引擎: 缺失` 下面逐条写着 waifu2x/realesrgan/ffmpeg/rife **已安装**,真身是 `抠图模型 rembg: 缺失`)
///   根因:`CheckEngines()` 把 rembg 抠图模型也算进"引擎齐全",且**缺的具体项没打印**。
///   修法:抠图模型分出去单列(专用判据 `CheckCutoutModel`),汇总行经纯函数 `ReportSummary` 拼装并打印缺项。
///
/// 【证据分层(诚实说明)】AlhPro.Tests 只引用 AlhPro.Core(无 WinUI,见 csproj 注释),**拿不到 App 的程序集**
/// ⇒ 本文件钉的是**源码契约 + 判据常数 + 文档案例**;真正的**执行**证据在反射探针 `D:\deep\_t54\probe`
/// (加载已编译的 ALHPro.dll,直接跑 `ProfileFor` / `DeviceHintText` / `ReportSummary`,喂那台机的画像
/// 4GB VRAM / 7.9GB RAM / 8 核 / GPU 可用)。两边口径必须一致:任一侧改动都会让另一侧变红。</summary>
public class DiagnosticWordingHonestyTests
{
    // ───────────────────────── A1:内存判据有据可查 ─────────────────────────

    /// <summary>**A1 · 内存来源**:`TotalRamGB` 必须是**物理内存**(GlobalMemoryStatusEx.ullTotalPhys),
    /// GC 的 `TotalAvailableMemoryBytes` 只能当**读不到时的退路**(本机实测两来源逐字节相同,所以换来源
    /// 本身不改档位 —— 真正改档位的是归一化,见下一条;换来源是为了不让报告在带上限的 Job/容器里说谎)。
    /// 这条红了 = 有人把来源改回 GC 口径(或删掉了物理内存探测)。</summary>
    [Fact]
    public void Total_ram_comes_from_physical_memory_with_the_gc_value_only_as_fallback()
    {
        string src = ReadRepoFile("ImgUpscalerUI", "SafeRender.cs");
        string code = CodeOnly(src);
        Assert.Contains("private static double ProbeTotalRamGB()", code);
        Assert.Contains("public static double TotalRamGB => _ramTotal ??= ProbeTotalRamGB();", code);
        string probe = Block(src, "private static double ProbeTotalRamGB()", "internal static double NominalGB");
        Assert.Contains("if (GlobalMemoryStatusEx(ref mi) && mi.ullTotalPhys > 0)", probe);
        Assert.Contains("return mi.ullTotalPhys / 1073741824.0;", probe);
        // GC 口径只剩一处:退路(位置必须在物理内存之后)
        Assert.Equal(1, Count(probe, "GC.GetGCMemoryInfo().TotalAvailableMemoryBytes"));
        Assert.True(probe.IndexOf("GC.GetGCMemoryInfo()", StringComparison.Ordinal)
                    > probe.IndexOf("return mi.ullTotalPhys", StringComparison.Ordinal),
            "GC 口径必须只是物理内存读不到时的退路(写在它之后)");
        // 退路也要给个数:报 0 会让 EffectiveRamGB 掉到 2GB 地板,把好机器按最差档处理
        Assert.Contains("catch { return 0; }", probe);
    }

    /// <summary>**A1 · 归一化 + 案例表**:8/16/32GB 标称机的读数分别是 **7.9 / 15.9 / 31.797**,
    /// 门槛(8/16/32)是按**标称容量**写的 ⇒ 必须先归一再比;`ComputeProfile`(分档)与 `ComputeWeakDevice` /
    /// `ComputeWeakReason`(弱点)三处必须同口径 —— 这正是队长提醒的那个最容易漏的点。
    /// 这条红了 = 有人去掉 NominalGB、把某个门槛改回裸读数,或让分档/弱点两处用了不同口径。</summary>
    [Fact]
    public void Ram_tiers_and_weakness_use_the_same_nominal_capacity_basis()
    {
        string src = ReadRepoFile("ImgUpscalerUI", "SafeRender.cs");
        Assert.Contains("internal static double NominalGB(double gib) => Math.Round(gib, MidpointRounding.AwayFromZero);", CodeOnly(src));
        // 三条实测读数写在文档里(案例表):8GB→7.9、16GB→15.9、32GB→31.797
        Assert.Contains("7.9", src);
        Assert.Contains("15.9", src);
        Assert.Contains("31.797", src);
        // 分档:纯函数 ProfileFor,内存先归一;缓存入口 Profile 只是取本机值后调它(单一判据)
        string profile = Block(src, "internal static DeviceProfile ProfileFor(double vramGB, double ramGB, int cores, bool gpu)",
            "internal static string DeviceHintText(bool noGpu, DeviceProfile profile, string weakReason)");
        Assert.Contains("double v = vramGB, r = NominalGB(ramGB);", profile);
        Assert.Contains("if (v >= 12 && r >= 32 && cores >= 16) return DeviceProfile.High;", profile);
        Assert.Contains("if (v >= 6 && r >= 16 && cores >= 8) return DeviceProfile.Balanced;", profile);
        Assert.Contains("if (v >= 3 && r >= 8) return DeviceProfile.Low;", profile);
        Assert.Contains("if (!gpu) return DeviceProfile.UltraLow;", profile);
        Assert.Contains("return ProfileFor(v, r, c, gpu);", CodeOnly(src));
        // 弱点判据:同口径(内存那条;显存/核数/核显照旧)
        string weak = Block(src, "private static bool ComputeWeakDevice()", "private static string ComputeWeakReason()");
        Assert.Contains("bool smallRam = NominalGB(TotalRamGB) < 8;", weak);
        Assert.DoesNotContain("bool smallRam = TotalRamGB < 8;", weak);
        string reason = Block(src, "private static string ComputeWeakReason()", "return string.Join(\"、\", list);");
        Assert.Contains("if (NominalGB(TotalRamGB) < 8) list.Add($\"内存 {TotalRamGB:0.#}GB\");", reason);
    }

    // ───────────────────────── A2:两件事必须分两句说 ─────────────────────────

    /// <summary>**A2 · 话术拆分**:`DeviceHintText` 里 ——
    /// ① 只有"没有可用 GPU"那一支才允许出现「将用 CPU」;
    /// ② `UltraLow`(设备偏弱)那一支只能说偏弱,并且要写明**仍在用 GPU**。
    /// 这条红了 = 两句又被合并(那台 1050Ti 机会再次被说成"显卡检测不到")。</summary>
    [Fact]
    public void Only_a_missing_gpu_may_say_cpu()
    {
        string src = ReadRepoFile("ImgUpscalerUI", "SafeRender.cs");
        string hint = Block(src, "internal static string DeviceHintText(bool noGpu, DeviceProfile profile, string weakReason)",
            "private static bool? _weak;");
        int noGpuAt = hint.IndexOf("if (noGpu)", StringComparison.Ordinal);
        int ultraAt = hint.IndexOf("if (profile == DeviceProfile.UltraLow)", StringComparison.Ordinal);
        Assert.True(noGpuAt >= 0 && ultraAt > noGpuAt, "两支的顺序必须是:先「没有可用 GPU」,再「设备偏弱」");
        string noGpuBranch = hint[noGpuAt..ultraAt];
        string ultraBranch = hint[ultraAt..];
        Assert.Contains("将用 CPU 计算", noGpuBranch);       // 只有这一支能说
        Assert.DoesNotContain("将用 CPU", ultraBranch);       // 偏弱分支绝不许说
        Assert.Contains("设备偏弱", ultraBranch);
        Assert.Contains("仍在用 GPU", ultraBranch);           // 还要把"没改用 CPU"说清楚
        Assert.Contains("return \"\";", hint);                // 强机不刷屏
    }

    /// <summary>**A2 · 页面调用点**:那句合并了两种判据的老话必须删掉,改叫唯一来源的 `DeviceHintText`;
    /// 且"没有可用 GPU"要等 Vulkan 自检**跑完**(`Done`)才成立 —— 自检未完成时 GpuAvailable 还是 false,
    /// 拿它当结论会在启动早期误报(bundle 15:51 那条就是这类)。
    /// 这条红了 = 页面又自己拼文案,或把 Done 守卫删了。</summary>
    [Fact]
    public void The_video_page_asks_the_shared_helper_and_waits_for_the_probe()
    {
        string code = CodeOnly(ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs"));
        Assert.DoesNotContain("无 GPU/弱设备:视频超分/补帧将用 CPU 计算", code);
        Assert.DoesNotContain("SafeRender.DeviceProfile.UltraLow || !ALHPro.VulkanCheck.GpuAvailable", code);
        Assert.Contains("bool noGpu = ALHPro.VulkanCheck.Done && !ALHPro.VulkanCheck.GpuAvailable;", code);
        Assert.Contains("string devHint = SafeRender.DeviceHintText(noGpu, SafeRender.Profile, SafeRender.WeakDeviceReason);", code);
        Assert.Contains("if (devHint.Length > 0) Log(devHint);", code);
    }

    /// <summary>**A2 · 弱设备原因清单(第三句错话)**:bundle 15:51 与 16:21 打出
    /// 「检测到设备配置较低(**未检测到可用 GPU(Vulkan)**、显存仅 4GB、内存 7.9GB)」,而同一份日志 15:34 写着
    /// 「Vulkan 自检:GPU 引擎可用(设备枚举成功)」、15:47 写着「RIFE GPU(1)真机探测通过 → ncnn-Vulkan」。
    /// 原因:`ComputeWeakReason` 只看 `!GpuAvailable`(**没等自检跑完**),而 `ComputeWeakDevice` 有 Done 守卫。
    /// 这条红了 = 原因清单又在"自检没跑完/GPU 明明可用"时说"未检测到可用 GPU"。</summary>
    [Fact]
    public void The_weak_device_reason_list_uses_the_same_guards_as_the_verdict()
    {
        string src = ReadRepoFile("ImgUpscalerUI", "SafeRender.cs");
        string reason = Block(src, "private static string ComputeWeakReason()", "return string.Join(\"、\", list);");
        Assert.Contains("if (ALHPro.VulkanCheck.Done && !ALHPro.VulkanCheck.GpuAvailable) list.Add(\"未检测到可用 GPU(Vulkan)\");", reason);
        Assert.DoesNotContain("if (!ALHPro.VulkanCheck.GpuAvailable) list.Add", reason);   // 旧写法(无 Done 守卫)
        string weak = Block(src, "private static bool ComputeWeakDevice()", "private static string ComputeWeakReason()");
        Assert.Contains("bool noGpu = ALHPro.VulkanCheck.Done && !ALHPro.VulkanCheck.GpuAvailable;", weak);
        Assert.Contains("bool smallVram = TotalVramGB < 6;", weak);
        Assert.Contains("bool fewCores = CpuCoreCount <= 4;", weak);
    }

    // ───────────────────────── B:自检汇总行只反映标题说的东西 ─────────────────────────

    /// <summary>**B · 归属拆分**:「超分 / 补帧引擎」那一行不许再被抠图模型影响 —— `CheckEngines()` 里不许出现
    /// rembg/抠图;抠图模型走自己的 `CheckCutoutModel()`;汇总文本经纯函数 `SummaryText`/`ReportSummary` 拼装
    /// (齐全/缺失 + **具体缺哪一项**)。
    /// 这条红了 = 抠图模型又混进引擎那一行(自检会再次自相矛盾)。</summary>
    [Fact]
    public void The_engine_row_no_longer_counts_the_cutout_model()
    {
        string src = ReadRepoFile("ImgUpscalerUI", "EngineService.cs");
        string engines = CodeOnly(Block(src, "public static bool CheckEngines(out string missing)",
            "public static bool CheckCutoutModel(out string missing)"));
        Assert.DoesNotContain("rembg", engines);
        Assert.DoesNotContain("FindCutoutModel", engines);
        Assert.Contains("if (FindWaifu2x() is null) list.Add(\"waifu2x 引擎\");", engines);
        Assert.Contains("if (VideoService.RifePath is null) list.Add(\"RIFE 补帧引擎\");", engines);
        Assert.Contains("if (FindRealCugan() is null) list.Add(\"Real-CUGAN 引擎\");", engines);
        Assert.Contains("missing = string.Join(\"、\", list);", engines);      // 给用户看的名字,不再输出内部键
        // 抠图模型:专用判据(默认模型与 CutoutService 同源)
        string cutout = CodeOnly(Block(src, "public static bool CheckCutoutModel(out string missing)",
            "public static string SummaryText(bool ok, string missing)"));
        Assert.Contains("FindCutoutModel(\"isnet-general-use.onnx\") is null", cutout);
        Assert.Contains("rembg 抠图模型包", cutout);
        // 文本拼装:纯函数(无文件系统探测)+ 打印缺项
        string summary = Block(src, "public static string SummaryText(bool ok, string missing)",
            "public static (bool EnginesOk, string EnginesLine, bool CutoutOk, string CutoutLine) ReportSummary(");
        Assert.Contains("=> ok ? \"齐全\" : $\"缺失(缺 {missing})\";", summary);
        string report = Block(src, "public static (bool EnginesOk, string EnginesLine, bool CutoutOk, string CutoutLine) ReportSummary(",
            "private static readonly System.Collections.Concurrent.ConcurrentBag<string> _tempFiles");
        Assert.Contains("SummaryText(enginesOk, enginesMissing)", report);
        Assert.Contains("cutoutOk ? \"已安装\" : SummaryText(cutoutOk, cutoutMissing)", report);
        Assert.DoesNotContain("Directory.", report);        // 纯函数:只拼文本,不探测文件系统
        Assert.DoesNotContain("File.", report);
    }

    /// <summary>**B · 两处报告 + 两条健康行**:浮层报告与设置页文本都必须经 `ReportSummary` 出这两行
    /// (以前各拼一份字符串,改一处漏一处),并且**抠图模型单列一行**;另外那两条健康行
    /// (「抠图模型 rembg」「图片抠图」)原先错用 `CheckEngines(out _)`(= 整机引擎健康)来判断抠图模型 ——
    /// 必须换成专用判据,`CheckEngines(out _)` 这种错用写法要绝迹。
    /// 这条红了 = 汇总行或健康行又回到"自相矛盾"的写法。</summary>
    [Fact]
    public void Both_reports_use_the_same_summary_and_the_cutout_row_is_separate()
    {
        string code = CodeOnly(ReadRepoFile("ImgUpscalerUI", "Views", "MainPage.xaml.cs"));
        Assert.Equal(2, Count(code, "EngineService.ReportSummary(enginesOk, enginesMissing, cutoutOk, cutoutMissing)"));
        Assert.Equal(2, Count(code, "EngineService.CheckCutoutModel(out var cutoutMissing)"));
        // 浮层报告:两行,抠图单列
        Assert.Contains("AddReportLine(\"超分 / 补帧引擎\", summary.EnginesLine, summary.EnginesOk);", code);
        Assert.Contains("AddReportLine(\"抠图模型 rembg\", summary.CutoutLine, summary.CutoutOk);", code);
        // 设置页文本:同样两行
        Assert.Contains("sb.Append(\"超分 / 补帧引擎: \").Append(summary.EnginesLine).Append('\\n');", code);
        Assert.Contains("sb.Append(\"抠图模型 rembg: \").Append(summary.CutoutLine).Append('\\n');", code);
        // 旧写法不许再出现:整行只说"齐全/缺失"(不打印缺什么)
        Assert.DoesNotContain("enginesOk ? \"齐全\" : \"缺失\"", code);
        // 健康行的判据换成专用检查
        Assert.Contains("list.Add((\"抠图模型 rembg\", EngineService.CheckCutoutModel(out _)));", code);
        Assert.Contains("list.Add((\"图片抠图\", EngineService.CheckCutoutModel(out _)));", code);
        Assert.DoesNotContain("CheckEngines(out _)", code);
    }

    // ───────────────────────── 真实案例(1050Ti 画像)的契约侧 ─────────────────────────

    /// <summary>**用真实案例当测试 · 契约侧**:把诊断包那台机的画像(VRAM 4GB / RAM 7.9GB / 8 核 / GPU 可用)
    /// 与"引擎齐、抠图模型缺"写进契约,钉住 ①它**不是**"无 GPU"、②引擎行**仍是齐全**。
    /// 【两侧同源】可执行的那一半在反射探针 `_t54\probe`(直接跑已编译的 ALHPro.dll):
    /// `ProfileFor(4, 7.9, 8, gpu:true)` 必须是 **Low**(不是 UltraLow)、
    /// `DeviceHintText(noGpu:false, Low, …)` 必须是**空串**(不再有任何"无 GPU/将用 CPU"的话)、
    /// `ReportSummary(true, "", false, "rembg 抠图模型包…")` 的引擎行必须是 **"齐全"**。
    /// 这条红了 = 案例表或"两件事分开说"的口径被改掉了(那时探针也要同步复核)。</summary>
    [Fact]
    public void The_1050ti_case_is_documented_in_the_source_and_stays_consistent()
    {
        string safe = ReadRepoFile("ImgUpscalerUI", "SafeRender.cs");
        Assert.Contains("GTX 1050 Ti", safe);
        Assert.Contains("VRAM 4GB / RAM 7.9GB / 8 核 / GPU 可用", safe);
        Assert.Contains("必须是 **Low**", safe);
        Assert.Contains("无 GPU…将用 CPU", safe);      // 用户读到的那句错话留档
        string eng = ReadRepoFile("ImgUpscalerUI", "EngineService.cs");
        Assert.Contains("GTX 1050Ti", eng);
        Assert.Contains("引擎行必须仍是「齐全」", eng);
    }

    // ───────────────────────── 工具 ─────────────────────────

    private static string Block(string src, string start, string end) => BlockExact(CodeOnly(src), start, end);

    private static string BlockExact(string src, string start, string end)
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

    /// <summary>剥掉 `//` 行注释(逐字符扫引号:字符串里的 `//` 不算注释)。不剥注释的源码断言会被
    /// "把那一行注释掉"骗过去(本仓库踩过,见 RememberParamsPresetTests 的红检记录)。</summary>
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
