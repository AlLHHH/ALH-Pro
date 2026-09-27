namespace AlhPro.Core;

/// <summary>ncnn 真机探测结论的【判定口径】(纯逻辑,可单测)。
///
/// 【为什么单独抽出来】2026-09-22 用户机器(RTX 5060 Laptop)诊断包实测暴露了一个判死的缺陷:
/// `ncnn-probe.txt` 里有三条结论 ——
///     realesrgan2026|0|anime4k   False    probe failed: 无响应(超时被强杀)
///     realesrgan2026|0|          True     probe ok; model=(default)
///     waifu2x|0|                 True     probe ok; model=(default)
/// 而旧口径把"同一引擎下任意一支模型失败"当成【整条引擎不可用】⇒ `realesrgan2026` 整体被判
/// "实测不可用 → 走 ONNX"，于是**四支自训模型(现实/游戏,只有 ncnn 权重、ONNX 里根本没有)
/// 一起被判死** —— 用户看到的就是"50 系跑 real 跑不了"。
/// 但 default 探测明明通过、后来真跑也通过(`走 ncnn-Vulkan(50 系未禁用,走最快路径)`)。
/// 所以正确口径是【按模型判】:
///   ① 引擎级可用性只认 `引擎|设备|`(不带模型)那条 = 引擎本身能不能在本机跑通;
///   ② 单支模型的失败只影响那一支(它自己退 ONNX 或换模型),不牵连同引擎的其它模型;
///   ③ 某支模型没有自己的结论时,回落到引擎级结论(没测过就不额外禁止)。
///
/// 【键格式】与 `NcnnVerdictKey.For` 一致:`引擎|设备号|模型`(模型为空 = default 探测)。</summary>
public static class NcnnModelVerdicts
{
    /// <summary>一条探测结论。</summary>
    public readonly record struct Entry(string Key, bool Ok, long AtUnix);

    /// <summary>【2026-09-24】"伪模型"条目:它们**不是 ncnn 引擎的模型**,结论不该参与
    /// "这个 ncnn 引擎在这张卡上能不能用"的判定。两支都来自界面下拉的 Tag(见 <see cref="NcnnProbePlan"/>):
    ///   · <see cref="Anime4k.ModelTag"/>(`anime4k`)—— Anime4K 修复走着色器,根本不用 ncnn;
    ///   · <see cref="Upscale1x.RealTag"/>(`alhpro-real1x`)—— 它自己写明"不是真模型",真权重是
    ///     <see cref="Upscale1x.RealEngineModel"/>。
    ///
    /// 【为什么必须挡住 · 2026-09-24 真机实测】视频预检曾把当前选中的 Tag 原样当模型名喂给 ncnn
    /// ⇒ 引擎找不到权重 ⇒ 60 秒无响应被强杀 ⇒ 落一条 `realesrgan2026|0|&lt;Tag&gt; = false` 的**假失败**。
    /// 在没有"引擎级(default)结论"的机器上(独显 + 核显 ⇒ 不走免探测快速通道、每次探测都带模型
    /// ⇒ 从不写 default),旧兜底"任一支失败即整条引擎不可用"会把 realesrgan 整体判走 ONNX
    /// ⇒ 视频 **2x 超分实测 3880 ms/帧**,而软件自己标称 animevideov3 是 0.26~0.30 秒/帧(差 13 倍)。
    /// 备注:5060 那台机器因为有 default 行,`EngineUsable` 先命中,所以这条从没暴露。
    /// ⚠ 常量引用而不是字面量:Tag 改名时这里跟着走(否则清单静默失配、毒化回归)。</summary>
    public static readonly string[] NonNcnnModels = { Anime4k.ModelTag, Upscale1x.RealTag };

    /// <summary>这个模型名是不是"伪模型"(不属于 ncnn 引擎,别拿它的结论判 ncnn)。大小写/空白不敏感。</summary>
    public static bool IsNonNcnnModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        string m = model.Trim().ToLowerInvariant();
        foreach (var bad in NonNcnnModels) if (m == bad) return true;
        return false;
    }

    /// <summary>引擎级可用性:只认不带模型的那条(default 探测)。
    /// 返回 null = 该引擎还没有 default 结论(调用方按"未测"处理,不擅自禁用)。</summary>
    public static bool? EngineUsable(IEnumerable<Entry> entries, string engineId, int gpuId)
    {
        if (entries == null) return null;
        string prefix = $"{engineId}|{gpuId}|";
        foreach (var e in entries)
        {
            if (e.Key == prefix) return e.Ok;          // 模型段为空的那条
        }
        return null;
    }

    /// <summary>【2026-09-24】没有 default 结论时的兜底口径:**只统计真 ncnn 模型**的结论
    /// (伪模型如 anime4k 一律跳过;键前缀与 <see cref="EngineUsable"/> 成对)。
    /// 返回 null = 该引擎+该卡连一条真模型结论都没有(调用方按"未测"处理)。
    /// 三条规则同 <see cref="NcnnVerdictKey.Summarize"/>:全通过 → true;出现 false → false(保守)。</summary>
    public static bool? ModelOnlyRisk(IEnumerable<Entry> entries, string engineId, int gpuId)
    {
        if (entries == null) return null;
        string prefix = $"{engineId}|{gpuId}|";
        var oks = new List<bool>();
        foreach (var e in entries)
        {
            if (!e.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (IsNonNcnnModel(e.Key.Substring(prefix.Length))) continue;   // 伪模型:不是 ncnn 的事
            oks.Add(e.Ok);
        }
        return NcnnVerdictKey.Summarize(oks);
    }

    /// <summary>某支模型能不能走 ncnn:优先它自己的结论;没有就回落到引擎级结论。
    /// 返回 null = 完全没测过(调用方不该据此禁用)。</summary>
    public static bool? IsModelUsable(IEnumerable<Entry> entries, string engineId, int gpuId, string? model)
    {
        if (entries == null) return null;
        if (!string.IsNullOrEmpty(model))
        {
            string key = $"{engineId}|{gpuId}|{model}";
            foreach (var e in entries)
                if (e.Key == key) return e.Ok;         // 该模型自己的结论优先
        }
        return EngineUsable(entries, engineId, gpuId);
    }

    /// <summary>汇总成一行(自检报告/日志用)。
    /// 【为什么要按模型说】旧文案只报"2 支模型中有失败" ⇒ 用户看到"整条引擎不可用",
    /// 而实际只有一支失败。这里明确写出"引擎可用;仅 X 未通过"。
    /// 【notConcludedReason】该引擎+该卡这次**没测通**(超时被强杀/取消/空闲显存不足)时传进来:
    /// 那时"未测"要按统一口径说清楚(见 <see cref="NcnnProbeWording.NotConcluded"/>),不能只写一个"未测"
    /// —— 用户会把它当成"测了、不通过"(1355 诊断包里就是这么被读错的)。null = 这次没有"未测通"记录,
    /// 保持原样("未测(首次处理时自动实测)")。**默认 null 保证既有调用点行为一字不变。**</summary>
    public static string Describe(IEnumerable<Entry> entries, string engineId, int gpuId, string? notConcludedReason = null)
    {
        if (entries == null) return NotConcludedOrPlain($"{engineId}=未测", notConcludedReason);
        string prefix = $"{engineId}|{gpuId}|";
        int ok = 0, fail = 0;
        var failed = new List<string>();
        bool? engine = EngineUsable(entries, engineId, gpuId);
        foreach (var e in entries)
        {
            if (!e.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (e.Ok) ok++;
            else
            {
                fail++;
                string model = e.Key.Substring(prefix.Length);
                // 伪模型(anime4k 这类着色器条目)不是 ncnn 的事:标出来,免得报告看着像"ncnn 有一支跑不了"
                failed.Add(string.IsNullOrEmpty(model) ? "(default)"
                    : IsNonNcnnModel(model) ? model + "(非 ncnn 模型,不计入判定)" : model);
            }
        }
        if (ok == 0 && fail == 0) return NotConcludedOrPlain($"{engineId}=未测(首次处理时自动实测)", notConcludedReason);
        // 【2026-09-27 改】这一行原来对所有引擎一律写"→走 ONNX",但 Real-CUGAN **没有 ONNX 版本**
        // (CPU 档实测会崩)⇒ 报告在这里就替用户编了一条不存在的出路。口径统一取自 RealCugan。
        if (engine == false) return $"{engineId}=实测不可用({fail} 支模型失败)—— " + NcnnProbeWording.UnavailableRoute(engineId);
        // 引擎可用:如实指出个别失败的模型,别让人以为整条坏了
        return fail == 0
            ? $"{engineId}=实测可用→走 ncnn({ok} 支模型全通过)"
            : $"{engineId}=实测可用→走 ncnn(仅 {string.Join("、", failed)} 未通过,其余 {ok} 支照走 ncnn)";
    }

    /// <summary>没测通时把统一口径接在"未测"后面(短形式;长句留给逐引擎那一行与日志)。
    /// 单一来源仍是 <see cref="NcnnProbeWording"/>。</summary>
    private static string NotConcludedOrPlain(string plain, string? notConcludedReason)
        => notConcludedReason is null ? plain : $"{plain.Split('=')[0]}={NcnnProbeWording.NotConcludedShort(notConcludedReason)}";
}

/// <summary>一次 ncnn 探测在**报告里**的三种结果(判据见 <see cref="NcnnProbeWording"/> 与
/// <c>EngineService.DescribeProbeAttempt</c>):
///   · <see cref="Available"/> —— 探测跑完、结论通过;
///   · <see cref="Unavailable"/> —— 探测跑完、结论判不可用(**已落盘**,值得让用户知道);
///   · <see cref="NotConcluded"/> —— 探测**没跑完**(超时被强杀 / 取消 / 空闲显存不足跳过):什么都没测到,
///     只能说"本次未测通 + 不落盘 + 下次照旧试"。
/// 【为什么要有这个区分】1355 诊断包里第三类被写成了第二类 ⇒ 用户读到"实测不可用"(显卡坏了)而实际什么都没测到。</summary>
public enum NcnnProbeReport
{
    Available,
    Unavailable,
    NotConcluded,
}

/// <summary>【2026-09-28 · t58 M1】进程内的"同一键**本会话最多探一次**"台账(**纯内存,永不落盘**)。
///
/// 【要解决什么】t56 让"被强杀/超时/取消"不再落盘否定结论(这是对的:探测根本没跑完),
/// 代价是那类机器**每次任务**都会重探 —— 复核方(t57)独立量到最坏 = `60 秒超时 + 3 秒退避 + 60 秒`
/// = **123 秒/引擎**;而调用点有图片任务(`UpscaleView`)、视频预检(`VideoView`)、视频处理
/// (`VideoService` 的预检/兼容检测等)⇒ 一次视频任务可能让 2~3 个引擎各白等一次。
/// 受影响的机器正是"真走探测"的那批:50 系 / AMD / Intel / 双显卡(纯 NVIDIA 非 Blackwell 走快速通道,不探)。
///
/// 【机制】记下"这个键这次已经试过、且**没得出任何结论**"(超时被强杀 / 取消 / 进程起不来);
/// 同一键再次被要求探测时**直接跳过** —— 照样**不落盘任何结论**,照样按"本次未测通"报告,
/// 只是不再白等第二个 123 秒。于是最坏值从「每任务每引擎一次」压到「每会话每键一次」。
///
/// 【为什么必须是进程内】**"下次照旧试"是 t56 定下的硬承诺**:这条记忆只活在本次运行里,
/// 重启软件后是一个全新的空台账 ⇒ 一切照旧重新探测。**任何把它落盘(哪怕只写一行)的做法都是错的**
/// —— 那就等于绕回"落盘一天否定结论",正是 t56 要消灭的东西。所以本类刻意**没有任何 I/O**。
///
/// 【为什么不做"10 分钟冷却"】冷却期内允许再次探测 ⇒ 一个开很久的会话里仍会反复等 2×60 秒;
/// "每会话每键一次"才把最坏值真正钉死。代价照实说:长会话里中途腾出显存(关掉游戏)也**不能**重探本键,
/// 所以跳过时的日志会明确告诉用户"重启软件即可重试"(与"下次照旧试"是同一件事的两种说法)。
///
/// 【粒度】按**结论键**记(超分:引擎|GPU|模型;RIFE:它自己的复合键)。**不做引擎级一刀切**:
/// 同一张卡上换一支模型是另一次探测 —— 重模型与轻模型的表现本来就可能不同(把引擎级拉黑会连带
/// 掩盖"轻模型其实能跑")。代价:同会话里换模型仍可能等一次;这是刻意的取舍。
///
/// 【线程安全】探测会在多个任务线程上发起 ⇒ 内部加锁(与 EngineService 的结论字典同一套做法)。</summary>
public sealed class NcnnProbeSessionLedger
{
    private readonly HashSet<string> _tried = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private int _attemptsAllowed;
    private int _attemptsSkipped;

    /// <summary>这个键**本次运行**是不是已经试过且没测通(true = 别再探了,直接用"本次未测通"回话)。
    /// **只读查询**:不改变任何计数(报告措辞先看它,见 EngineService.WouldSkipProbeThisSession)。
    /// ★ 它与下面那个带计数的重载是**同一个判据**,对同一状态给出**同一答案**
    /// —— t62 的 B1 就是因为"闸门入口"与"只读查询"极性相反才漏出去的,所以现在**只有一份判断**。</summary>
    public bool ShouldSkip(string? key) => ShouldSkip(key, countAttempt: false);

    /// <summary>★ **跳过闸的唯一判据(带计数)**:true = 本会话已试过同一键且没测通 ⇒ **调用点必须跳过**。
    /// 【2026-09-30 · t62 B1 留档】t60 曾把闸门入口写成 `TryBeginAttempt`(语义是"true = 允许发起"),
    /// 而调用点仍按旧语义写 `if (助手(...)) { 跳过 }` ⇒ **台账为空时第一次调用就进跳过分支**:
    /// 整个会话里超分 ncnn 探测(realesrgan/waifu2x/realcugan)永远不会真正发起、日志谎称"本会话已试过",
    /// 能走 ncnn 的机器被静默降级 ONNX。根因是"同一件事有两个极性相反的说法"。
    /// 现在:极性只说一种(**true = 已试过 ⇒ 跳过**),闸门与只读查询共用这一个方法;
    /// `countAttempt: true` 时额外记一次"允许/跳过"(生产路径的真计数,见 <see cref="AttemptsAllowed"/>)。
    /// 【计数为什么要有开关】报告路径(诊断包导出前的措辞)会**只读地问一次**,
    /// 那次不该被算成"发起过一次探测" —— 所以只读版与生产版必须同判据、不同计数。</summary>
    public bool ShouldSkip(string? key, bool countAttempt)
    {
        if (string.IsNullOrEmpty(key))
        {
            if (countAttempt) lock (_lock) _attemptsAllowed++;
            return false;
        }
        lock (_lock)
        {
            bool skip = _tried.Contains(key!);
            if (countAttempt)
            {
                if (skip) _attemptsSkipped++;
                else _attemptsAllowed++;
            }
            return skip;
        }
    }

    /// <summary>记下"这个键试过了、但没得出任何结论"(**纯内存**,本进程有效;重启即清空)。</summary>
    public void MarkNotConcluded(string? key)
    {
        if (string.IsNullOrEmpty(key)) return;
        lock (_lock) _tried.Add(key!);
    }

    /// <summary>已经记下多少个键(日志/自测用)。</summary>
    public int Count { get { lock (_lock) return _tried.Count; } }

    /// <summary>本会话经闸门**允许发起**过多少次探测(生产路径真计数;跳过的不算)。</summary>
    public int AttemptsAllowed { get { lock (_lock) return _attemptsAllowed; } }

    /// <summary>本会话被闸门**跳过**过多少次探测(生产路径真计数)。</summary>
    public int AttemptsSkipped { get { lock (_lock) return _attemptsSkipped; } }
}

/// <summary>【2026-09-27 · 依据用户诊断包 ALHPro_Diag_20260927_1355】ncnn 探测**报告口径**的单一来源。
///
/// 【为什么必须收成一处】那台机(GTX 1050 Ti / 空闲显存 0.7GB)的一次超时被写成了三句互相打脸的话:
///   · 逐引擎探测行:`ncnn 探测(realesrgan, GPU 0): 实测不可用 → 走 ONNX 稳定引擎`
///     —— 而那次探测**根本没跑完**:第 1 次 60 秒无响应被强杀 → 退避 3 秒 → 第 2 次任务被取消;
///   · 同一份文件的汇总行:`ncnn 实测结论(导出时实时): realesrgan2026=未测`;
///   · 启动自检报告:`可用性:GPU 加速可用`。
///   三句话里只有第三句是对的(设备层可用),第二句也不算错(确实没落盘结论),第一句是假话。
///
/// 【规矩】**「实测不可用」只能由"探测真的跑完并判不可用"得出**;超时被强杀 / 进程起不来 / 被取消一律写
/// 「本次未测通(原因:…)+ 不落盘结论 + 下次照旧试」,而且**只有这一处**定义这句话。
/// 三条口径的分工(三处报告点各说什么范围):
///   ① 逐引擎探测行(诊断包 `设备信息.txt`)= **这一次探测**的结果(可用/不可用/未测通);
///   ② `ncnn 实测结论(导出时实时)` 汇总行 = **落盘结论**的汇总(它读的是结论缓存,没落盘就是"未测通");
///   ③ 启动自检报告(VulkanCheck)= **设备/驱动这一层**的快照(能不能被 Vulkan 枚举、驱动/显存够不够),
///      它不判断任何引擎走 ncnn 还是 ONNX —— 那句留给出厂后的生产帧尺寸实测。</summary>
public static class NcnnProbeWording
{
    /// <summary>**「未测通」长句(唯一来源)**:超时被强杀 / 进程起不来 / 探测自身异常 / 被取消 / 空闲显存不足。
    /// 关键三要素:①如实写原因;②明说**不落盘结论**(所以下次照旧试);③明说"这不代表该卡不可用"
    /// (否则用户读成"显卡坏了")。</summary>
    public static string NotConcluded(string? reason)
        => $"本次未测通(原因:{(string.IsNullOrWhiteSpace(reason) ? "未记录(本次探测没跑完)" : reason!.Trim())})"
         + "—— 不落盘结论(这不代表该卡不可用),下次照旧试";

    /// <summary>**「未测通」短句(唯一来源)**:汇总行/自检报告这类一行里塞不下长句的地方用。
    /// 与 <see cref="NotConcluded"/> 同源(同一处定义、同一套要素,只是不展开原因)。</summary>
    public static string NotConcludedShort(string? reason)
        => $"本次未测通(不落盘,下次照旧试{(string.IsNullOrWhiteSpace(reason) ? "" : ";原因:" + reason!.Trim())})";

    /// <summary>怎么把一次失败的探测说成"原因"。
    /// 【关键区分】取消落在探测过程里时,**不许说"进程起不来"**(那是归因错误,1355 的日志就这么写了)——
    /// 被取消的探测可能根本没走完,只该说"未跑完 + 因取消未重试"。
    /// 真实序列(1355:第 1 次 60 秒无响应被强杀 → 退避 3 秒 → 第 2 次取消)拼出来是:
    /// `60 秒无响应(疑似 hang)被强杀;因取消未重试`。</summary>
    public static string NotConcludedReasonFrom(ProbeFailureKind kind, string? detail, bool cancelled)
    {
        string d = (detail ?? "").Trim();
        string shape = kind switch
        {
            ProbeFailureKind.Hang => (d.Length > 0 ? d : "60 秒无响应") + "(疑似 hang)被强杀",
            ProbeFailureKind.None => d.Length > 0 ? d : "探测未跑完",
            // 取消时不许说"进程起不来"(见上):只留过程明细,没有明细就只说形态
            _ when cancelled => d.Length > 0 ? d : ProbeDiagnosis.ShortName(kind),
            _ => ProbeDiagnosis.ShortName(kind) + (d.Length > 0 ? ":" + d : ""),
        };
        return cancelled ? shape + ";因取消未重试" : shape;
    }

    /// <summary>★ **这次探测跑完了吗**?—— 决定要不要落盘结论。
    /// 只有"跑完并得出形态"的失败才配得上「实测不可用」(进程已退出/引擎已交出结果 ⇒ 行为是确定的);
    /// **被强杀的 Hang、进程起不来(StartupFailed)、EngineMissing 之外的未知形态、以及任何取消都不落盘**
    /// —— 否则就是把"没测到"记成"测出不可用"(1355 那台机就是这么被判了一整天)。
    /// **纯函数**(可单测),EngineService 只问它、不许自己写 if。</summary>
    public static bool IsConclusiveOutcome(ProbeFailureKind kind, bool cancelled)
    {
        if (cancelled) return false;
        return kind is ProbeFailureKind.None                       // 通过
            or ProbeFailureKind.CrashExitCode                       // 进程已退出:退出码就是结论
            or ProbeFailureKind.NoOutput                            // 引擎跑完却没交产出(结构性失败)
            or ProbeFailureKind.EmptyOutput                         // 引擎跑完交出 0 字节
            or ProbeFailureKind.DefectiveFrame                      // 引擎跑完交出坏帧
            or ProbeFailureKind.EngineMissing;                      // 引擎文件不在(与显卡无关,但结论确定)
    }

    /// <summary>「实测不可用」后面**那条出路**(与"能不能用"分开说):Real-CUGAN 没有 ONNX 版本,
    /// 不许给它编一条不存在的路(口径取自 <see cref="RealCugan.UnavailableNotice"/>)。</summary>
    public static string UnavailableRoute(string engineId)
        => RealCugan.IsRealCuganId(engineId) ? RealCugan.UnavailableNotice : "走 ONNX 稳定引擎";

    /// <summary>探测**通过**时那一句(逐引擎行用)。</summary>
    public const string AvailableText = "实测可用 → 走 ncnn-Vulkan";

    /// <summary>探测**跑完并判不可用**时那一句(逐引擎行用;出路由 <see cref="UnavailableRoute"/> 决定)。</summary>
    public static string UnavailableText(string engineId) => "实测不可用 —— " + UnavailableRoute(engineId);

    /// <summary>空闲显存不足、**跳过本次生产帧尺寸探测**时给用户的一句话(为什么 + 怎么办)。
    /// 与"不可用"完全分开:这次**不落盘任何结论**,关掉占显存的程序后重试即可。</summary>
    public static string VramShortfallHint(double freeGB, double needGB)
        => $"本次先不跑这项实测:显卡当前只有 {freeGB:0.#}GB 空闲显存,而生产帧尺寸(1080×1920)的实测至少要留约 {needGB:0.#}GB"
         + "(否则引擎容易卡住,白等一分钟还可能被误判成'不可用')。"
         + "可以先关掉占显存的程序(游戏 / 浏览器 / 剪辑软件)再重试,或把「计算设备」改成 CPU(慢但稳);"
         + "这次不会记成「不可用」,下次照旧会再试。";

    /// <summary>跳过探测时记进"未测通原因"的那一句(单一口径,汇总行与逐引擎行共用)。</summary>
    public static string VramShortfallReason(double freeGB, double needGB)
        => $"空闲显存不足(实测 {freeGB:0.#}GB,低于下限 {needGB:0.#}GB)";
}
