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
    private const string MainPageCs = "MainPage.xaml.cs";

    // ───────────────────────── M1:每会话每键最多探一次 ─────────────────────────

    /// <summary>**M1 定点证据(可执行 + 生产路径真计数)**:
    /// 【t60 F4 重写 / t62 B1 修正】旧版在测试里自己写计数器(且有个永不递增的 `verdictWrites`);
    /// t60 改用台账计数,但把闸门入口接成了"true = 允许发起"而调用点仍按"true = 已试过"写 ⇒
    /// **极性反转**(t61 的 blocker B1)。现在**极性只有一个说法:true = 已试过 ⇒ 跳过**
    /// (Core `ShouldSkip(key, countAttempt: true)`,EngineService 的私有助手就转发到它)。
    /// 本用例用**与闸门同一个入口**驱动,并断言台账内部的真计数
    /// (`AttemptsAllowed` / `AttemptsSkipped`)与 `Count` —— 实现一改(把闸门去掉或极性反过来)就变红。
    /// 同时保留:报告口径仍是 t56 那句、一个字不变。</summary>
    [Fact]
    public void Second_task_does_not_probe_again_within_one_process()
    {
        var ledger = new NcnnProbeSessionLedger();
        const string key = "realesrgan2026|0|";        // 与 NcnnVerdictKey.For 拼出来的一致
        const string reason = "60 秒无响应(疑似 hang)被强杀;因取消未重试";
        string Report()                                 // 与 EngineService.DescribeProbeAttempt 同一判据
            => NcnnProbeWording.NotConcluded(reason);
        bool TaskOnce()
        {
            // ★ 与 EngineService 的跳过闸**同一个入口**(同一判据 + 生产计数):
            //   true = 已试过 ⇒ 调用点跳过(与 EngineService 里那道 `if (助手(...)) { 跳过 }` 逐字同极性)
            if (ledger.ShouldSkip(key, countAttempt: true)) return false;
            ledger.MarkNotConcluded(key);               // ← 没测通(超时被强杀 → 取消):不落盘
            return false;                               // ← 返回 false:调用方按"没结论"走(不是"不可用")
        }

        string first = Report();
        Assert.False(TaskOnce());                        // 第 1 次任务:探(台账为空 ⇒ 放行)
        Assert.False(TaskOnce());                        // 第 2 次任务:跳过
        Assert.False(TaskOnce());                        // 第 3 次任务:依旧跳过
        // ★ 真计数(在 Core 台账里,由生产路径那道门驱动):只允许一次、跳过两次
        Assert.Equal(1, ledger.AttemptsAllowed);
        Assert.Equal(2, ledger.AttemptsSkipped);
        Assert.True(ledger.AttemptsAllowed + ledger.AttemptsSkipped == 3, "三次调用必须都记进台账(计数不许漏)");
        Assert.Equal(1, ledger.Count);                   // 只记了一个键
        // 报告口径一个字不变(t56/t57 已复核过的那句)
        Assert.Equal(first, Report());
        Assert.Contains("本次未测通(原因:60 秒无响应(疑似 hang)被强杀;因取消未重试)", Report());
        Assert.Contains("不落盘结论(这不代表该卡不可用),下次照旧试", Report());
        // 【接线】EngineService 的跳过闸必须走这个计数入口,而且**没有 `!`**(true = 已试过 ⇒ 跳过)
        string engCode = CodeOnly(ReadRepoFile("ImgUpscalerUI", EngineCs));
        Assert.Contains("private static bool SessionAlreadyTriedNotConcluded(string engine, int gpuId, string? model)", engCode);
        Assert.Contains("_sessionNotConcluded.ShouldSkip(NcnnVerdictKey(engine, gpuId, model), countAttempt: true)", engCode);
        Assert.Contains("if (SessionAlreadyTriedNotConcluded(engine, gpuId, model))", engCode);
        Assert.DoesNotContain("if (!SessionAlreadyTriedNotConcluded(", engCode);            // 不许有 `!`(那会把极性再反过来)
        Assert.DoesNotContain("TryBeginAttempt", engCode);                                // B1 那个极性相反的 API 已删除
        // 对外也有真计数可读(诊断/自测)
        Assert.Contains("public static int SessionProbeAttemptsAllowed => _sessionNotConcluded.AttemptsAllowed;", engCode);
        Assert.Contains("public static int SessionProbeAttemptsSkipped => _sessionNotConcluded.AttemptsSkipped;", engCode);
        // 【F3 的只读查询】不许有副作用:连问三次不该改变任何计数
        int allowedBefore = ledger.AttemptsAllowed, skippedBefore = ledger.AttemptsSkipped;
        Assert.True(ledger.ShouldSkip(key));
        Assert.True(ledger.ShouldSkip(key));
        Assert.True(ledger.ShouldSkip(key));
        Assert.Equal(allowedBefore, ledger.AttemptsAllowed);
        Assert.Equal(skippedBefore, ledger.AttemptsSkipped);
        // 【新进程】= 新台账 = 空计数(与"下次照旧试"同源)
        var fresh = new NcnnProbeSessionLedger();
        Assert.Equal(0, fresh.AttemptsAllowed);
        Assert.Equal(0, fresh.AttemptsSkipped);
    }

    /// <summary>**极性契约测试(t62 B1 的防回退钉子)**:**闸门入口与只读查询对同一状态必须给出同一答案**。
    /// 【为什么必须有它】t60 把闸门接成 Core 的 `TryBeginAttempt`(true = 允许发起),而调用点按
    /// "true = 已试过"写 ⇒ 台账为空时第一次调用就进跳过分支(整个会话超分探测永不发起)。
    /// 当时 1500 条测试全绿也没拦住它——因为没有任何一条断言把"闸门入口"与"只读查询"放在一起比。
    /// 现在两边都是 Core 的 `ShouldSkip`,**同一个判据**;本用例在 fresh / afterMark 两态各比一次,
    /// 并断言计数语义(空台账后第一次 = 允许 1 / 跳过 0;同一键第二次 = 跳过 1)。
    /// 【能真红】把 `ShouldSkip(key, countAttempt: true)` 的返回极性反过来(红检 R1)⇒ 这里立刻红。</summary>
    [Fact]
    public void The_gate_and_the_read_only_query_answer_the_same_way_on_the_same_state()
    {
        var ledger = new NcnnProbeSessionLedger();
        const string key = "realesrgan2026|0|";
        const string otherKey = "waifu2x|0|";

        // —— fresh:台账为空 ⇒ 两边都必须说"别跳过"(第一次调用**真的会去探测**)——
        bool readOnlyFresh = ledger.ShouldSkip(key);                       // 只读查询(报告路径)
        bool gateFresh = ledger.ShouldSkip(key, countAttempt: true);       // 闸门入口(生产路径)
        Assert.False(readOnlyFresh);
        Assert.False(gateFresh);
        Assert.Equal(readOnlyFresh, gateFresh);                            // ★ 同极性不变量
        Assert.Equal(1, ledger.AttemptsAllowed);                           // 空台账后第一次 = 允许 1 / 跳过 0
        Assert.Equal(0, ledger.AttemptsSkipped);
        Assert.Equal(0, ledger.Count);
        // 换一把键互不串台
        Assert.False(ledger.ShouldSkip(otherKey, countAttempt: true));
        Assert.Equal(2, ledger.AttemptsAllowed);

        // —— afterMark:同一键已试过且没测通 ⇒ 两边都必须说"跳过" ——
        ledger.MarkNotConcluded(key);
        bool readOnlyAfter = ledger.ShouldSkip(key);
        bool gateAfter = ledger.ShouldSkip(key, countAttempt: true);       // 同一键第二次 = 跳过 1
        Assert.True(readOnlyAfter);
        Assert.True(gateAfter);
        Assert.Equal(readOnlyAfter, gateAfter);                            // ★ 同极性不变量
        Assert.Equal(1, ledger.AttemptsSkipped);
        Assert.Equal(2, ledger.AttemptsAllowed);                           // 只读查询不计数(仍停在 2)
        // 另一把键不受影响(粒度是"每键")
        Assert.False(ledger.ShouldSkip(otherKey, countAttempt: true));

        // —— "计数开关"不许改变判断本身(只读 vs 生产,同一状态同一答案)——
        var a = new NcnnProbeSessionLedger();
        a.MarkNotConcluded(key);
        bool readOnlyOnly = a.ShouldSkip(key);
        Assert.True(readOnlyOnly);
        Assert.Equal(0, a.AttemptsAllowed + a.AttemptsSkipped);            // 只读那一次没被算成"发起过"
        Assert.Equal(readOnlyOnly, a.ShouldSkip(key, countAttempt: true)); // 计数重载给**同一答案**(并记一次跳过)
        Assert.Equal(1, a.AttemptsSkipped);
        // —— 接线侧:EngineService 的闸门与只读查询必须共用同一判据(Core 的 ShouldSkip)——
        string engCode = CodeOnly(ReadRepoFile("ImgUpscalerUI", EngineCs));
        int gate = engCode.IndexOf("private static bool SessionAlreadyTriedNotConcluded(string engine, int gpuId, string? model)", StringComparison.Ordinal);
        int query = engCode.IndexOf("public static bool WouldSkipProbeThisSession(string engine, int gpuId, string? model = null)", StringComparison.Ordinal);
        Assert.True(gate > 0 && query > gate, "必须能找到闸门助手与只读查询");
        string gateBody = engCode[gate..query];
        // ★ 整个表达式都要对上(`=> ` 也包含在内):只在前面加一个 `!` 就会让这条红
        Assert.Contains("=> _sessionNotConcluded.ShouldSkip(NcnnVerdictKey(engine, gpuId, model), countAttempt: true);", gateBody);
        string queryBody = engCode[query..engCode.IndexOf("public static int SessionProbeAttemptsAllowed", query, StringComparison.Ordinal)];
        Assert.Contains("_sessionNotConcluded.ShouldSkip(NcnnVerdictKey(engine, gpuId, model))", queryBody);
        Assert.DoesNotContain("countAttempt: true", queryBody);            // 只读:绝不计数
        Assert.DoesNotContain("!", gateBody[gateBody.IndexOf("_sessionNotConcluded.ShouldSkip", StringComparison.Ordinal)..gateBody.IndexOf(";", gateBody.IndexOf("_sessionNotConcluded.ShouldSkip", StringComparison.Ordinal))]);   // 不许对判据取反
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

    /// <summary>**L1**(t58)**+ t60 F1**:阈值依据里"通过时的读数"必须写**范围** 2.5~2.7GB
    /// (04:05 那次实测是 2.5GB),而且**两种写法都抓得到** —— t59 复核指出 t58 加的那条 pin 只查带空格的
    /// `2.6 / 2.7 GB`,而文件头注释里还留着一个**不带空格**的 `2.6/2.7GB` 残留 ⇒ 旧的 pin 抓不到它。
    /// 现在:旧数字的**四种写法**(带/不带空格 × 两种斜杠间距)一律不许出现在实现文件与 t56 测试文件里;
    /// 实现侧的 2.5GB 结论与 1.5GB 阈值一字未改。
    /// 这条红了 = 又把依据写窄(下一个人会以为"至少要 2.6GB")。</summary>
    [Fact]
    public void The_passing_measurement_range_includes_the_2_5gb_case()
    {
        string src = ReadRepoFile("ImgUpscalerUI", EngineCs);
        string doc = Block(src, "/// <summary>【2026-09-27 · E2】生产帧尺寸探测所需的**空闲显存下限**(GB)。", "public const double ProductionProbeMinFreeVramGB = 1.5;");
        Assert.Contains("2.5 ~ 2.7 GB", doc);
        Assert.Contains("2.5GB", doc);
        Assert.Contains("0.7 GB", doc);                     // 失败那一端保留
        // 【t60 F1】防回退:两种写法(带空格 / 不带空格)都要抓 —— 逐个文件、逐个变体扫
        string testSrc = ReadRepoFile("AlhPro.Tests", "NcnnProbeHonestyTests.cs");
        foreach (var stale in new[] { "2.6 / 2.7 GB", "2.6/2.7GB", "2.6/2.7 GB", "2.6 / 2.7GB" })
        {
            Assert.DoesNotContain(stale, doc);              // 实现侧注释
            Assert.DoesNotContain(stale, testSrc);          // t56 测试文件(注释也算——t59 ④ 的残留就在文件头注释里)
        }
        Assert.Contains("2.5 ~ 2.7 GB", testSrc);           // 更正后的写法确实在(不是"全删了")
        Assert.Contains("2.5~2.7GB", testSrc);
        // 阈值本身一字未改(与 t58 同)
        Assert.Contains("public const double ProductionProbeMinFreeVramGB = 1.5;", CodeOnly(src));
    }

    // ───────────────────────── F2:RIFE 无取消的超时也不许落盘 ─────────────────────────

    /// <summary>**F2(t59 ④ 的另一半)**:RIFE 探测**超时被强杀(没有取消)** 也不得落盘成已定论失败。
    /// 旧代码只拦了"取消"那一半 ⇒ `IsRifeGpuUsableAsync` 的 Hang 分支照样走到
    /// `SaveNcnnVerdict(... "rife probe failed: 无响应(超时被强杀)" ...)`(TTL **1 天**)⇒ 一次无响应
    /// 就让用户一整天走 ONNX;与 t56 给超分探测定下的 E1 口径(「实测不可用」只能由"跑完并得出形态"得出)矛盾。
    /// 现在用**同一个纯函数** `NcnnProbeWording.IsConclusiveOutcome(failKind, false)` 拦:
    /// Hang / 进程起不来 / 探测自身异常 ⇒ 记台账 + 「本次未测通」 + `return false`(**不落盘**);
    /// 该次补帧照旧降级(返回 false ⇒ 调用方按"没结论"走)。
    /// 这条红了 = 一次超时又会让补帧"一整天不可用"。</summary>
    [Fact]
    public void The_rife_probe_never_persists_a_timeout()
    {
        // ① 判据(可执行):Hang / StartupFailed 都不是"已定论"
        Assert.False(NcnnProbeWording.IsConclusiveOutcome(ProbeFailureKind.Hang, cancelled: false));
        Assert.False(NcnnProbeWording.IsConclusiveOutcome(ProbeFailureKind.StartupFailed, cancelled: false));
        // 真跑完的四种仍照旧允许落盘(真话没丢)
        foreach (var kind in new[] { ProbeFailureKind.CrashExitCode, ProbeFailureKind.NoOutput,
                                     ProbeFailureKind.EmptyOutput, ProbeFailureKind.DefectiveFrame })
            Assert.True(NcnnProbeWording.IsConclusiveOutcome(kind, cancelled: false), kind + " 跑完了 ⇒ 允许落盘");
        // ② 超时的"未测通"文案(可执行):可读、且**不含**"实测不可用"
        string reason = NcnnProbeWording.NotConcludedReasonFrom(ProbeFailureKind.Hang, "25 秒无响应", cancelled: false);
        Assert.Contains("25 秒无响应", reason);
        Assert.Contains("(疑似 hang)被强杀", reason);
        Assert.DoesNotContain("因取消未重试", reason);           // 这次不是取消,不许套取消的话
        string text = NcnnProbeWording.NotConcluded(reason);
        Assert.Contains("本次未测通", text);
        Assert.Contains("不落盘结论(这不代表该卡不可用),下次照旧试", text);
        Assert.DoesNotContain("实测不可用", text);
        // ③ 接线:RIFE 那个函数体里,两道闸(取消 / 没跑完)**都在**唯一的落盘之前
        string code = CodeOnly(ReadRepoFile("ImgUpscalerUI", EngineCs));
        int rife = code.IndexOf("public static async Task<bool> EnsureRifeNcnnProbeAsync(", StringComparison.Ordinal);
        int rifeEnd = code.IndexOf("private static string RifeProbeKey(", rife, StringComparison.Ordinal);
        Assert.True(rife > 0 && rifeEnd > rife, "必须能定位 RIFE 探测那一节");
        string block = code[rife..rifeEnd];
        int cancelGate = block.IndexOf("if (ct.IsCancellationRequested)", StringComparison.Ordinal);
        int inconclusiveGate = block.IndexOf("if (!AlhPro.Core.NcnnProbeWording.IsConclusiveOutcome(failKind, cancelled: false))", StringComparison.Ordinal);
        int save = block.IndexOf("SaveNcnnVerdict(key, gpuId, null, ok,", StringComparison.Ordinal);
        Assert.True(cancelGate > 0, "取消闸门必须在");
        Assert.True(inconclusiveGate > cancelGate, "取消闸门之后还要有「没跑完就不落盘」的闸门(Hang 走这条)");
        Assert.True(save > inconclusiveGate, "落盘必须在两道闸门之后");
        string hangGate = block[inconclusiveGate..save];
        Assert.Contains("NcnnProbeWording.NotConcludedReasonFrom(failKind, failDetail, cancelled: false)", hangGate);
        Assert.Contains("NcnnProbeWording.NotConcluded(inconclusiveReason)", hangGate);
        Assert.Contains("_sessionNotConcluded.MarkNotConcluded(key);", hangGate);
        Assert.Contains("LastProbeUserMessage = \"\";", hangGate);   // 不留"探测未通过"那类已定论措辞
        Assert.Contains("return false;", hangGate);
        Assert.DoesNotContain("SaveNcnnVerdict", hangGate);
        // ④ 仍不引入 ProbeRetryPolicy(既有契约 The_rife_probe_is_left_alone 的同一口径)
        Assert.DoesNotContain("ProbeRetryPolicy", block);
        // ⑤ 落盘出口仍唯一
        Assert.Equal(1, Count(code, "SaveNcnnVerdict(key, gpuId, null, ok,"));
    }

    // ───────────────────────── F3:导出状态栏不许"宣称在实测其实跳过" ─────────────────────────

    /// <summary>**F3(t59 的另一条 low)**:诊断包导出前先声明「正在实测 ncnn 引擎(诊断包,最长约 90 秒)…」,
    /// 但 M1 台账会让"本会话已试过同一键"的引擎**跳过** ⇒ 同一会话第二次导出就是"说在实测、其实跳过"。
    /// 现在先**只读**问一次台账(`EngineService.WouldSkipProbeThisSession`,无副作用),按实际会不会探给两种文案;
    /// 报告文本的诚实性不降低(逐引擎行仍由 `DescribeProbeAttempt` 写"未测通",导出信息里写明本次跳过)。
    /// 这条红了 = 状态栏又开始宣称没发生的事。</summary>
    [Fact]
    public void The_diagnostic_export_status_text_matches_the_actual_path()
    {
        string mp = CodeOnly(ReadRepoFile("ImgUpscalerUI", "Views", MainPageCs));
        // ① 先查台账(只读)再措辞
        Assert.Contains("ALHPro.EngineService.WouldSkipProbeThisSession(eng, probeGpu)", mp);
        Assert.Contains("var willProbe = new System.Collections.Generic.List<string>();", mp);
        Assert.Contains("var skippedBySession = new System.Collections.Generic.List<string>();", mp);
        // ② 两种文案:只有"真的会探"才说正在实测;全被跳过时说清"不再重复实测"
        Assert.Contains("if (willProbe.Count > 0)", mp);
        Assert.Contains("正在实测 ncnn 引擎({string.Join(\"/\", willProbe)},诊断包,最长约 90 秒)", mp);
        Assert.Contains("本会话已试过这几个引擎(未测通)", mp);
        // ③ 老那句"无条件宣称在实测"必须绝迹
        Assert.DoesNotContain("StatusText.Text = \"正在实测 ncnn 引擎(诊断包,最长约 90 秒)…\";", mp);
        // ③b 【t62 L2】顺序断言:先问台账 → 再写状态栏 → 最后才真探测
        //     (t61 复核的 L2:光有"两种文案"还不够,顺序被调换就会又变成"先宣称再判断")
        int qIdx = mp.IndexOf("ALHPro.EngineService.WouldSkipProbeThisSession(eng, probeGpu)", StringComparison.Ordinal);
        int claimIdx = mp.IndexOf("StatusText.Text = $\"正在实测", StringComparison.Ordinal);
        int claimGuardIdx = mp.IndexOf("if (willProbe.Count == 0)", StringComparison.Ordinal);
        int probeLoopIdx = mp.IndexOf("if (willProbe.Count > 0)", StringComparison.Ordinal);
        Assert.True(qIdx > 0, "必须能找到台账查询");
        Assert.True(claimIdx > qIdx, "查台账必须在写状态栏**之前**(否则就是先宣称再判断)");
        Assert.True(claimGuardIdx > 0 && claimGuardIdx < claimIdx, "「正在实测」这句必须被 willProbe.Count == 0 的 else 分支守着");
        Assert.True(probeLoopIdx > claimIdx, "真探测的 if (willProbe.Count > 0) 必须排在写状态栏**之后**");
        Assert.True(claimIdx < mp.IndexOf("var probe = ALHPro.EngineService.DescribeProbeAttempt(eng, probeGpu);", StringComparison.Ordinal),
            "报告行在状态栏之后");
        Assert.True(mp.IndexOf("var probe = ALHPro.EngineService.DescribeProbeAttempt(eng, probeGpu);", StringComparison.Ordinal) > probeLoopIdx,
            "报告行要在真探测之后(它读的是探测后的结论)");
        // ④ 报告诚实性不降低:逐引擎行仍是 DescribeProbeAttempt(未测通 + 原因),且导出信息写明本次跳过
        Assert.Contains("var probe = ALHPro.EngineService.DescribeProbeAttempt(eng, probeGpu);", mp);
        Assert.Contains("本次跳过", mp);
        Assert.Contains("报告照实写「未测通」", mp);
        // ⑤ 引擎侧的只读查询:无副作用(不计数、不改台账)—— 行为由 Second_task… 用例钉住
        string eng = CodeOnly(ReadRepoFile("ImgUpscalerUI", EngineCs));
        int q = eng.IndexOf("public static bool WouldSkipProbeThisSession(string engine, int gpuId, string? model = null)", StringComparison.Ordinal);
        Assert.True(q > 0, "必须有只读查询 WouldSkipProbeThisSession");
        string qBlock = eng[q..eng.IndexOf("public static int SessionProbeAttemptsAllowed", q, StringComparison.Ordinal)];
        Assert.Contains("_sessionNotConcluded.ShouldSkip(NcnnVerdictKey(engine, gpuId, model))", qBlock);
        Assert.DoesNotContain("countAttempt: true", qBlock);   // 只读:绝不走计数入口
        Assert.DoesNotContain("MarkNotConcluded", qBlock);
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

    /// <summary>剥掉 `//` 行注释(逐字符扫引号)。不剥注释的源码断言会被"注释掉那一行"骗过去(本仓库踩过)。
    /// 【2026-09-29 · t60】同时**去掉行尾的 `\r`**:本仓库源码是 CRLF,而拼接式断言(`"…\n        => …"`)按 `\n` 写,
    /// 不剥 `\r` 就会永远匹配不上(t60 自查时踩到过)。</summary>
    private static string CodeOnly(string src)
    {
        var sb = new System.Text.StringBuilder(src.Length);
        foreach (var line in src.Split('\n'))
        {
            string l = line.TrimEnd('\r');
            bool inStr = false, inChar = false;
            int cut = l.Length;
            for (int i = 0; i < l.Length; i++)
            {
                char c = l[i];
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
