using AlhPro.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【t58 · 2026-09-28】收口 t57 复核的三条:M1(探测要有界)+ L1(数字更正)+ L3(RIFE 取消闸门)。
///
/// **M1(medium)**:t56 让"被强杀/超时/取消"不再落盘否定结论(对:探测根本没跑完),代价是那类机器
/// **每次任务**都重探 —— 复核方独立量到最坏 `60 s 超时 + 3 s 退避 + 60 s = **123 秒/引擎**`,
/// 而一次视频任务可能让 2~3 个引擎各等一次(调用点:图片任务 `UpscaleView`、视频预检 `VideoView`、
/// 视频处理 `VideoService` 的预检/兼容检测)。修法:**同一键在一次进程内最多探一次**
/// (<see cref="NcnnProbeSessionLedger"/>,纯内存、永不落盘),最坏值从「每任务每引擎」压到「每会话每键」。
/// **"下次照旧试"必须保住**:台账是本进程的,重启即空 ⇒ 一切照旧重探。
///
/// **L1(low)**:E2 阈值依据里的读数范围原来写窄了(「2.6 / 2.7GB」),实测通过时的最低点是 **2.5GB**
/// (bundle `diagnostic.log:194/235`),已改「2.5 ~ 2.7GB」。
///
/// **L3(low)**:RIFE 探测原来**没有取消闸门**(超分探测有)⇒ 用户点一次取消就会落盘
/// `rife probe failed: 进程起不来;探测过程异常:The operation was canceled.`(TTL 1 天)。
/// 现在与超分探测逐字同口径:取消 ⇒ 不落盘 + 按「本次未测通」记。
///
/// 【证据分层】台账是 Core 纯逻辑 ⇒ 这里**直接执行**(含"两次任务只探一次"的调用计数);
/// UI 侧接线(两道闸的顺序、计数器、RIFE 取消闸门位置)按本仓库惯例用源码断言钉住。</summary>
public class NcnnProbeSessionLedgerTests
{
    private const string EngineCs = "EngineService.cs";

    // ───────────────────────── M1:每会话每键最多探一次 ─────────────────────────

    /// <summary>**M1 定点证据(可执行)**:连着跑两次"任务",第二次**不再重探** —— 用**调用计数**说话
    /// (不是"看代码顺序")。同时钉住:①一次都没落盘结论(两次都不写);②未测通时记的是同一个键;
    /// ③"报告口径"仍是 t56 那句(一个字不变)。
    /// 这条红了 = 探测又变成"每任务都等 123 秒"。</summary>
    [Fact]
    public void Second_task_does_not_probe_again_within_one_process()
    {
        var ledger = new NcnnProbeSessionLedger();
        const string key = "realesrgan2026|0|";        // 与 NcnnVerdictKey.For 拼出来的一致
        const string reason = "60 秒无响应(疑似 hang)被强杀;因取消未重试";
        int probeCalls = 0, verdictWrites = 0;
        string Report()
        {
            // 与 EngineService.DescribeProbeAttempt 同一判据:有结论才说可用/不可用,没结论就是"未测通"
            return NcnnProbeWording.NotConcluded(reason);
        }
        bool TaskOnce()
        {
            if (ledger.ShouldSkip(key)) return false;   // ← EngineService 里的同一道闸(跳过 ⇒ 不探)
            probeCalls++;                               // ← 真正发起探测这一句(会等 123 秒的那一步)
            ledger.MarkNotConcluded(key);               // ← 没测通(超时被强杀 → 取消)
            return false;                               // ← 返回 false:调用方按"没结论"走(不是"不可用")
        }

        string first = Report();
        Assert.False(TaskOnce());                        // 第 1 次任务:探
        Assert.False(TaskOnce());                        // 第 2 次任务:跳过
        Assert.False(TaskOnce());                        // 第 3 次任务:依旧跳过
        Assert.Equal(1, probeCalls);                     // ★ 只探了一次(计数为证)
        Assert.Equal(1, ledger.Count);                   // 只记了一个键
        Assert.Equal(0, verdictWrites);                  // 一次都没落盘(verdictWrites 从未 +1)
        // 报告口径一个字不变(t56/t57 已复核过的那句)
        Assert.Equal(first, Report());
        Assert.Contains("本次未测通(原因:60 秒无响应(疑似 hang)被强杀;因取消未重试)", Report());
        Assert.Contains("不落盘结论(这不代表该卡不可用),下次照旧试", Report());
    }

    /// <summary>**"下次照旧试"的证据(可执行)**:机制是**进程内**的 —— 新开一次软件 = 新台账 = 空的 ⇒
    /// 同一键照旧会探。这条红了 = 有人把它做成了跨进程记忆(那就等于绕回"落盘一天否定结论")。</summary>
    [Fact]
    public void A_new_process_starts_with_an_empty_ledger_and_tries_again()
    {
        var p1 = new NcnnProbeSessionLedger();
        const string key = "realesrgan2026|0|";
        p1.MarkNotConcluded(key);
        Assert.True(p1.ShouldSkip(key));                 // 本次运行:不再探
        var p2 = new NcnnProbeSessionLedger();           // ← "重启软件"= 新实例
        Assert.False(p2.ShouldSkip(key));                // 新进程照旧试 ✓
        Assert.Equal(0, p2.Count);
        // 台账**没有任何 I/O 能力**(不是"我们没写",而是它做不到):源码里不许出现落盘/读盘/时钟
        string src = ReadRepoFile("AlhPro.Core", "NcnnModelVerdicts.cs");
        string cls = Block(src, "public sealed class NcnnProbeSessionLedger", "/// <summary>【2026-09-27 · 依据用户诊断包");
        string clsCode = CodeOnly(cls);
        foreach (var banned in new[] { "File.", "Directory.", "SaveNcnnVerdict", "Save(", "Load(", "DateTime", "Environment." })
            Assert.DoesNotContain(banned, clsCode);
        Assert.Contains("private readonly HashSet<string> _tried = new(StringComparer.Ordinal);", clsCode);
        // 落盘出口仍然只有一处(重试/跳过路径都不许偷偷写结论)
        string eng = CodeOnly(ReadRepoFile("ImgUpscalerUI", EngineCs));
        Assert.Equal(1, Count(eng, "SaveNcnnVerdict(engine, gpuId, model, ok,"));   // 超分:唯一一处
        Assert.Equal(1, Count(eng, "SaveNcnnVerdict(key, gpuId, null, ok,"));       // RIFE:唯一一处
    }

    /// <summary>**M1 接线**:两道"不落盘"的闸(取消 / 没跑完)都要**记台账**;跳过闸必须排在
    /// 缓存与快速通道**之后**(否则会跳过一台其实有结论的机器)、空闲显存闸与真探测**之前**;
    /// 空闲显存闸**刻意不记**(它没花等待时间,而且它给用户的承诺是"关掉占显存的程序后重试")。
    /// 这条红了 = 台账没接上、或接错了位置(前者=白等,后者=误跳过)。</summary>
    [Fact]
    public void The_session_ledger_is_wired_between_cache_and_probe()
    {
        string code = CodeOnly(ReadRepoFile("ImgUpscalerUI", EngineCs));
        int probe = code.IndexOf("public static async Task<bool> EnsureNcnnProbeAsync(", StringComparison.Ordinal);
        Assert.True(probe > 0);
        int fast = code.IndexOf("if (!force && TargetGpuIsPlainNvidia(gpuId)) return true;", probe, StringComparison.Ordinal);
        int cache = code.IndexOf("var cached = TryGetNcnnVerdict(engine, gpuId, model);", probe, StringComparison.Ordinal);
        int skip = code.IndexOf("if (SessionAlreadyTriedNotConcluded(engine, gpuId, model))", probe, StringComparison.Ordinal);
        int vram = code.IndexOf("if (VramTooLowForProductionProbe(out double freeGb, out double needGb))", probe, StringComparison.Ordinal);
        int runCount = code.IndexOf("int runNo = Interlocked.Increment(ref _productionProbeRuns);", probe, StringComparison.Ordinal);
        Assert.True(fast > probe && cache > fast, "快速通道与缓存检查必须最前");
        Assert.True(skip > cache, "跳过闸必须在缓存检查之后(有结论的机器照旧沿用结论)");
        Assert.True(vram > skip, "跳过闸在空闲显存闸之前");
        Assert.True(runCount > vram, "探测计数只在真正要探测那一刻 +1");
        // 跳过闸本身:不落盘、只记日志、直接 return false
        string skipBlock = code[skip..vram];
        Assert.Contains("SessionAlreadyTriedNotConcluded(engine, gpuId, model)", skipBlock);
        Assert.Contains("跳过重复探测", skipBlock);
        Assert.Contains("return false;", skipBlock);
        Assert.DoesNotContain("SaveNcnnVerdict", skipBlock);
        Assert.DoesNotContain("MarkSessionNotConcluded", skipBlock);      // 跳过时不重复记
        // 空闲显存闸:同样不许记台账(它的承诺是"关掉占显存的程序后重试")
        int probeLog = code.IndexOf("首次真机探测(生产帧尺寸 1080×1920", probe, StringComparison.Ordinal);
        string vramBlock = code[vram..probeLog];
        Assert.DoesNotContain("MarkSessionNotConcluded", vramBlock);
        Assert.Contains("NcnnProbeWording.VramShortfallHint(freeGb, needGb)", vramBlock);
        // 两道"没测通"的闸都要记台账(取消 / 没跑完)
        int save = code.IndexOf("SaveNcnnVerdict(engine, gpuId, model, ok,", probe, StringComparison.Ordinal);
        string gates = code[skip..save];
        Assert.Equal(2, Count(gates, "MarkSessionNotConcluded(engine, gpuId, model)"));
        Assert.Equal(1, Count(code, "Interlocked.Increment(ref _productionProbeRuns)"));
        // 计数器与台账都对外可读(诊断/自测)
        Assert.Contains("public static int ProductionProbeRunCount => System.Threading.Volatile.Read(ref _productionProbeRuns);", code);
        Assert.Contains("public static int SessionNotConcludedCount => _sessionNotConcluded.Count;", code);
    }

    /// <summary>**最坏等待有据**:单次探测最长 60 秒(生产帧尺寸档)、超时只重试一次、退避 3 秒
    /// ⇒ 一**次**探测最坏 60+3+60 = **123 秒**;台账把"每任务一次"变成"每会话每键一次"。
    /// (数字由 Core 的重试档提供 —— 可执行;60 秒超时是 EngineService 里的本地常量 —— 源码断言。)</summary>
    [Fact]
    public void The_bound_is_derived_from_the_retry_profile()
    {
        var profile = ProbeRetryPolicy.PipelineFullFrameProbe;
        Assert.Equal(2, ProbeRetryPolicy.MaxAttempts(profile, ProbeFailureKind.Hang));   // 超时只重试一次
        Assert.Equal(3000, ProbeRetryPolicy.BackoffMsAfterAttempt(profile, 1, ProbeFailureKind.Hang));
        string code = CodeOnly(ReadRepoFile("ImgUpscalerUI", EngineCs));
        Assert.Contains("int probeTimeoutSec = fullFrame ? 60 : 15;", code);             // 单次最长 60 秒
        // 123 = 60 + 3 + 60(改前:每任务每引擎一次;改后:每会话每键一次)
        Assert.Equal(123, 60 + 3 + 60);
    }

    // ───────────────────────── L3:RIFE 探测的取消闸门 ─────────────────────────

    /// <summary>**L3 接线**:RIFE 探测也要有取消闸门,而且必须**在落盘之前**;它引用的措辞与超分探测同源
    /// (<see cref="NcnnProbeWording"/>),并且**不许**把 ProbeRetryPolicy 塞进 RIFE 那一节
    /// (既有的 `The_rife_probe_is_left_alone` 守着这条)。
    /// 这条红了 = 用户点一次取消,补帧又会"一整天走 ONNX"(t56 从超分探测里消灭掉的假结论)。</summary>
    [Fact]
    public void The_rife_probe_now_has_a_cancel_gate()
    {
        string code = CodeOnly(ReadRepoFile("ImgUpscalerUI", EngineCs));
        int rife = code.IndexOf("public static async Task<bool> EnsureRifeNcnnProbeAsync(", StringComparison.Ordinal);
        // 只截到 RifeProbeKey 之前(与既有契约 The_rife_probe_is_left_alone 同一段:只管 RIFE 那个函数体)
        int rifeProbe = code.IndexOf("private static string RifeProbeKey(", rife, StringComparison.Ordinal);
        Assert.True(rife > 0 && rifeProbe > rife, "必须能定位 RIFE 探测那一节");
        string block = code[rife..rifeProbe];
        int cancelGate = block.IndexOf("if (ct.IsCancellationRequested)", StringComparison.Ordinal);
        int save = block.IndexOf("SaveNcnnVerdict(key, gpuId, null, ok,", StringComparison.Ordinal);
        int sessionSkip = block.IndexOf("if (_sessionNotConcluded.ShouldSkip(key))", StringComparison.Ordinal);
        int cache = block.IndexOf("var cached = TryGetNcnnVerdict(key, gpuId);", StringComparison.Ordinal);
        Assert.True(sessionSkip > cache, "M1 跳过闸在缓存检查之后");
        Assert.True(cancelGate > 0, "RIFE 探测必须补上取消闸门");
        Assert.True(save > cancelGate, "取消闸门必须在落盘之前(取消 ⇒ 不落盘)");
        string gate = block[cancelGate..save];
        Assert.Contains("NcnnProbeWording.NotConcludedReasonFrom(failKind, failDetail, cancelled: true)", gate);
        Assert.Contains("NcnnProbeWording.NotConcluded(cancelReason)", gate);
        Assert.Contains("_sessionNotConcluded.MarkNotConcluded(key);", gate);
        Assert.Contains("return false;", gate);
        Assert.DoesNotContain("SaveNcnnVerdict", gate);
        // 取消时不许说"进程起不来"(那是归因错误,1355 的日志就这么写的)
        Assert.Contains("cancelled: true", gate);
        // 既有的独立契约:RIFE 那一节不许出现 ProbeRetryPolicy(不许硬塞重试机制)
        Assert.DoesNotContain("ProbeRetryPolicy", block);
        // 日志归因修准:不再写"探测异常(按不可用)"
        Assert.DoesNotContain("RIFE GPU 探测异常(按不可用)", code);
        Assert.Contains("RIFE GPU 探测过程异常(", code);
    }

    // ───────────────────────── L1:读数范围更正 ─────────────────────────

    /// <summary>**L1**:阈值依据里"通过时的读数"必须写**范围** 2.5~2.7GB(04:05 那次实测是 2.5GB),
    /// 而且仓库里(实现文件)不许再留写窄了的旧写法。
    /// 这条红了 = 又把依据写窄(下一个人会以为"至少要 2.6GB")。</summary>
    [Fact]
    public void The_passing_measurement_range_includes_the_2_5gb_case()
    {
        string src = ReadRepoFile("ImgUpscalerUI", EngineCs);
        string doc = Block(src, "/// <summary>【2026-09-27 · E2】生产帧尺寸探测所需的**空闲显存下限**(GB)。", "public const double ProductionProbeMinFreeVramGB = 1.5;");
        Assert.Contains("2.5 ~ 2.7 GB", doc);
        Assert.Contains("2.5GB", doc);
        Assert.DoesNotContain("2.6 / 2.7", doc);
        Assert.DoesNotContain("2.6/2.7", doc);
        Assert.Contains("0.7 GB", doc);                     // 失败那一端保留
        // 我的 t56 证据包(不在仓库内,但凡是抄了这数的地方一起改):由 output 里给出核对记录
        string testSrc = ReadRepoFile("AlhPro.Tests", "NcnnProbeHonestyTests.cs");
        Assert.DoesNotContain("2.6 / 2.7 GB", testSrc);
        Assert.Contains("2.5 ~ 2.7 GB", testSrc);
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
