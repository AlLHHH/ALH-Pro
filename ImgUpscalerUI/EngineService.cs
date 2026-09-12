// EngineService.cs — 调用放大引擎的后台服务
// 支持:模型/GPU 选择、实时进度解析(引擎 stdout 中的 "xx%")、取消(杀进程)、区域放大(先裁剪再放大)
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ALHPro;

public static partial class EngineService
{
    /// <summary>是否 RTX 50 系(Blackwell)显卡:从 VulkanCheck/GPU 枚举名字判断。
    /// 【它现在只决定"要不要真机探测",不再决定"禁不禁 ncnn"】原先据此一律改走 ONNX;现在 ncnn
    /// 能否用由 EnsureNcnnProbeAsync 实测决定(见 NcnnGpuRisky / ShouldUseOnnx*),50 系探测通过就走 ncnn。
    /// 仍需名字判定的场景:①探测的快速通道(纯 NVIDIA 非 Blackwell 且机内无非 N 卡 → 不探测直接判可用)
    /// ②少数确实只见于 50 系的引擎缺陷兜底(如 waifu2x 的 ncnn CPU 模式崩溃 exit -1073741819)。
    /// 测试钩子:环境变量 ALH_FORCE_BLACKWELL=1 时强制视为 50 系(开发/诊断用,正常用户不生效)。</summary>
    public static bool IsBlackwellGpu()
    {
        try
        {
            if (Environment.GetEnvironmentVariable("ALH_FORCE_BLACKWELL") == "1") return true;
            var names = new System.Collections.Generic.List<string?>();
            try { names.AddRange(VulkanCheck.Devices.Select(d => d.Name)); } catch { }
            try { names.AddRange(GpuInfo.GetAdapterNames()); } catch { }
            // 判定交给 AlhPro.Core.GpuName(纯函数,有单测):原先这里的 RTX 5[0-9]{2}
            // 连 "RTX 5000 Ada"/"Quadro RTX 5000"(Turing) 一起吞,把跑得动 ncnn 的卡推到更慢的 ONNX。
            return AlhPro.Core.GpuName.AnyIsBlackwell(names);
        }
        catch { return false; }
    }

    /// <summary>照片超分(Real-ESRGAN)是否应走 ONNX 路线:
    /// ①【本机实测】ncnn 在该 GPU 上跑不通(优先判据)②无独显/Vulkan 不可用(只能 CPU,而 ncnn CPU 也崩)
    /// —— 都走 ONNX(DML/CPU 稳)。
    /// 【不再"50 系一律禁用 ncnn"】:50 系上只要真机探测通过(EnsureNcnnProbeAsync,生产帧尺寸 +
    /// 带状黑判据)就走更快的 ncnn-Vulkan。
    /// 【未测过时也不再把 Blackwell 当风险(treatBlackwellAsRiskyWithoutProbe:false)】——与 ShouldUseOnnxWaifu2x
    /// 同一口径。原因:本函数同时被"自检报告 / 界面提示 / ETA 估算"用来【断言】"会走 ONNX",而按型号断言会让
    /// 50 系用户在实测通过(走 ncnn)的情况下仍被告知"已自动改用 ONNX" —— 报告与行为相反(已被用户实测抓到)。
    /// 路由本身不受影响:真正决定走哪条路的地方(VideoService / 图片路径)都是先跑探测、再读结论。</summary>
    public static bool ShouldUseOnnxEsrgan()
    {
        if (EsrganOnnxService.FindModel() == null && EsrganOnnxService.FindAnimeVideoModel() == null)
            return false;
        return NcnnGpuRisky("realesrgan", AppSettings.GpuIndex, treatBlackwellAsRiskyWithoutProbe: false);
    }

    /// <summary>waifu2x 是否应走 ONNX 路线:①【本机实测】ncnn 不可用(优先判据)
    /// ②无独显/Vulkan 不可用(此时只能 CPU,而 waifu2x ncnn CPU 模式有 bug 会崩)。
    /// 50 系 waifu2x 20250915 新版引擎兼容 Blackwell —— 但"到底行不行"不再靠型号猜,由真机探测决定
    /// (EnsureNcnnProbeAsync:生产帧尺寸 1080×1920 + 带状黑判据);探测通过就用 ncnn(快),失败才 ONNX。
    /// 未探测过时按"Blackwell 不算风险"处理 = 与本次改动前的行为逐字节一致(不引入回归)。</summary>
    public static bool ShouldUseOnnxWaifu2x()
    {
        if (EsrganOnnxService.FindWaifu2xModel() == null) return false;
        return NcnnGpuRisky("waifu2x", AppSettings.GpuIndex, treatBlackwellAsRiskyWithoutProbe: false);
    }

    // ========== ncnn 可用性"真机探测"结论缓存(50 系路由的唯一判据来源)==========
    /// <summary>探测结论(进程内):key = "引擎|GPU编号" → 该设备上 ncnn-Vulkan 实测能否用。
    /// 【为什么要有它】此前 50 系是"一律禁用 ncnn、直接改 ONNX":实测后果是 5060 上 DirectML 建不出会话
    /// (探测 #-1 + 8007000E OOM)→ 落 CPU → 8 秒/帧(40 系走 ncnn 只要 0.24 秒/帧,慢 33 倍)。
    /// 现在改为"先真机探测 → 按结果决定":通过就用 ncnn(快的那条路),失败才 ONNX(明确告知用户)。
    /// 结论缓存(进程内 + 落盘),同一设备不重复试跑;失败过的设备也不会每批重试。</summary>
    private static readonly System.Collections.Generic.Dictionary<string, NcnnVerdictEntry> _ncnnVerdicts = new();
    private static bool _ncnnVerdictsLoaded;   // 落盘文件是否已合并进上面的字典(只读一次,避免反复 I/O)
    private static readonly object _ncnnVerdictLock = new();
    /// <summary>结论有效期(过期自动重测):驱动/引擎/模型都会更新,旧结论不该永久钉死路由。
    /// 【成功 7 天,失败只记 1 天 —— 代价不对称】一次偶发失败(驱动瞬时故障 / GPU 被别的程序占满 /
    /// 探测时传错模型名)会让用户整整一周被挡在慢路上,而重测一次只要 ~15 秒。失败用短 TTL 给机器自愈机会。
    /// 这条不是理论:实测刚踩过 —— 探测调用曾被无条件执行,waifu2x 模式下拿 waifu2x 的模型名去探
    /// realesrgan,必然失败,于是"realesrgan 不可用"这个假结论会被钉 7 天。</summary>
    private static readonly TimeSpan NcnnVerdictTtlOk = TimeSpan.FromDays(7);
    private static readonly TimeSpan NcnnVerdictTtlFail = TimeSpan.FromDays(1);
    private static bool? _nonNvidiaCache;
    /// <summary>本会话内确认"ncnn CPU(-g -1)模式崩溃"(exit -1073741819 内存访问违规)后置位:
    /// 之后所有引擎的 CPU 兜底直接跳过,改为 GPU 0 重算,避免反复崩溃拖慢/卡住(双卡机/部分机型实测)。</summary>
    private static bool _ncnnCpuBroken;
    /// <summary>GPU 引擎重试的【递归深度】(0=顶层失败,该重试;≥1=已在重试递归里,直接走降级链防无限递归)。
    /// 此前是静态 bool 闩锁:置位后永不复位 → 宣传的"同设备重试 3 次"整个进程只兑现一次(诊断包里第二次 GPU 失败
    /// 直接报"不再重试"),而且第一次重试就被它自己的递归消耗掉。改成计数 + finally 归还,每次任务都恢复重试能力。</summary>
    private static int _gpuRetryDepth;

    /// <summary>本会话内确认"WinRT BitmapEncoder 编码 JPG 不可用"(视频/后台线程上系统性抛 HRESULT,空消息)
    /// 后置位:后续帧直接走 System.Drawing(转 24bppRgb),不再逐帧尝试 WinRT + 逐帧刷失败日志。</summary>
    private static bool _winrtJpegBroken;

    /// <summary>最近一次探测失败的【用户可读原因】(空 = 上次探测通过/还没探过)。
    /// 供调用方(视频/图片路径)在提示里原样引用,避免各处自写一套措辞;
    /// 措辞规则集中在 AlhPro.Core.ProbeDiagnosis,并有单测守着"坏帧不许甩锅给驱动"这条约束。</summary>
    public static string LastProbeUserMessage { get; private set; } = "";

    public static async Task<bool> IsWaifu2xNcnnUsableAsync(int gpuId, CancellationToken ct)
    {
        if (!IsBlackwellGpu() && !HasNonNvidiaGpu()) return true;
        // 统一走"生产帧尺寸"探测并共享同一份结论缓存(原先这里另有一套 1×1/320×240 小图缓存,
        // 小图在 Blackwell 上会假通过 —— 两套缓存还可能给出互相矛盾的结论,故合并为一套)。
        return await EnsureNcnnProbeAsync("waifu2x", gpuId, null, ct).ConfigureAwait(false);
    }

    /// <summary>探测结论落盘条目。
    /// 【为什么用纯文本而不是 JSON】决策键/值都是短标量,JSON 只会引入"私有嵌套类型的反射序列化 +
    /// 裁剪(trim)风险"这类与需求无关的失败面;纯文本还可让人直接读诊断包核查。字段用 TAB 分隔。</summary>
    private sealed class NcnnVerdictEntry
    {
        public bool Ok { get; set; }
        public long At { get; set; }          // Unix 秒(UTC)
        public string Device { get; set; } = "";
        public string Detail { get; set; } = "";
    }

    // 【引擎身份键】realesrgan 会被规整成 realesrgan / realesrgan2026(见 RealEsrganEngineId):
    // 新旧引擎共用同一个键的话,旧版在 50 系上"出坏帧"的结论会把新引擎一起判死。
    private static string NcnnVerdictKey(string engine, int gpuId) => EngineId(engine) + "|" + gpuId;

    /// <summary>探测结论落盘文件(与其他设置同在 settings 目录)。首行是格式说明,便于人工核查。</summary>
    private static string NcnnProbeCacheFile => ParaPaths.SettingsFile("ncnn-probe.txt");

    private static string SafeDeviceName(int gpuId)
    {
        try { return GpuInfo.GetEngineDeviceName(gpuId) ?? ""; } catch { return ""; }
    }

    /// <summary>字段净化:去掉分隔符与换行,保证一行一条(诊断包里也不会串行)。</summary>
    private static string OneLine(string s)
        => (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>读落盘结论。文件不存在/不可读/单行损坏都只跳过该行 —— 缓存坏了绝不能影响处理。</summary>
    private static System.Collections.Generic.Dictionary<string, NcnnVerdictEntry>? LoadNcnnVerdicts()
    {
        var path = NcnnProbeCacheFile;
        if (!File.Exists(path)) return null;
        var map = new System.Collections.Generic.Dictionary<string, NcnnVerdictEntry>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line) || line[0] == '#') continue;
            var f = line.Split('\t');
            if (f.Length < 4) continue;
            if (!bool.TryParse(f[1], out var ok)) continue;
            if (!long.TryParse(f[2], out var at)) continue;
            map[f[0]] = new NcnnVerdictEntry { Ok = ok, At = at, Device = f[3], Detail = f.Length > 4 ? f[4] : "" };
        }
        return map;
    }

    /// <summary>把落盘结论一次性合并进进程内字典(已合并过则直接返回)。过期条目直接丢弃 = 过期自动重测。
    /// 调用方必须已持有 _ncnnVerdictLock。</summary>
    private static void EnsureNcnnVerdictsLoaded_NoLock()
    {
        if (_ncnnVerdictsLoaded) return;
        _ncnnVerdictsLoaded = true;
        try
        {
            var map = LoadNcnnVerdicts();
            if (map == null) return;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var kv in map)
                if (kv.Value != null && now - kv.Value.At >= 0
                    && now - kv.Value.At <= (long)(kv.Value.Ok ? NcnnVerdictTtlOk : NcnnVerdictTtlFail).TotalSeconds)
                    _ncnnVerdicts[kv.Key] = kv.Value;
        }
        catch { }
    }

    /// <summary>取已缓存的 ncnn 可用性结论。null = 没测过(调用方回退保守启发式)。
    /// 首次调用把落盘结论合并进进程内,之后纯内存查表(零 I/O)。全程 try/catch:缓存坏了绝不能影响处理。</summary>
    public static bool? TryGetNcnnVerdict(string engine, int gpuId)
    {
        var key = NcnnVerdictKey(engine, gpuId);
        try
        {
            lock (_ncnnVerdictLock)
            {
                EnsureNcnnVerdictsLoaded_NoLock();
                return _ncnnVerdicts.TryGetValue(key, out var e) ? e.Ok : (bool?)null;
            }
        }
        catch { return null; }
    }

    /// <summary>把已缓存的 ncnn 真机探测结论汇总成一行,供自检报告/日志展示。
    /// 【为什么需要它】自检报告此前按显卡型号断言"50 系将走 ONNX",而真实路由由实测决定 ——
    /// 报告必须报实测,否则用户看到的结论和软件实际行为相反(已被用户抓到一次)。没测过就如实写"未测"。</summary>
    public static string DescribeNcnnVerdicts()
    {
        try
        {
            var parts = new System.Collections.Generic.List<string>();
            foreach (var eng in new[] { "realesrgan", "waifu2x" })
            {
                var v = TryGetNcnnVerdict(eng, AppSettings.GpuIndex);
                parts.Add($"{EngineId(eng)}={(v.HasValue ? (v.Value ? "实测可用→走 ncnn" : "实测不可用→走 ONNX") : "未测(首次处理时自动实测)")}");
            }
            return string.Join(" ", parts);
        }
        catch { return "未测"; }
    }

    /// <summary>记录探测结论(进程内 + 落盘)。落盘失败只记日志,绝不影响处理。</summary>
    private static void SaveNcnnVerdict(string engine, int gpuId, bool ok, string detail)
    {
        var key = NcnnVerdictKey(engine, gpuId);
        System.Collections.Generic.List<string> lines = new();
        lock (_ncnnVerdictLock)
        {
            EnsureNcnnVerdictsLoaded_NoLock();
            _ncnnVerdicts[key] = new NcnnVerdictEntry
            {
                Ok = ok,
                At = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Device = SafeDeviceName(gpuId),
                Detail = detail,
            };
            lines.Add("# key\tok\tatUnix\tdevice\tdetail  (ALH Pro ncnn 真机探测结论缓存;删掉本文件即强制重新探测)");
            foreach (var kv in _ncnnVerdicts)
                lines.Add($"{OneLine(kv.Key)}\t{kv.Value.Ok}\t{kv.Value.At}\t{OneLine(kv.Value.Device)}\t{OneLine(kv.Value.Detail)}");
        }
        try { File.WriteAllLines(NcnnProbeCacheFile, lines); }
        catch (Exception ex) { AppLogger.Warn($"[探测] ncnn 探测结论落盘失败(不影响本次处理):{ex.Message}"); }
    }

    /// <summary>【50 系路由核心】真机探测某引擎的 ncnn-Vulkan 在本机该 GPU 上能否按【生产形态】跑通,
    /// 结论缓存(进程内 + 落盘),供 NcnnGpuRisky / ShouldUseOnnx* 决定走 ncnn 还是 ONNX。
    /// 【与旧探测口径的区别 —— 旧口径正是"50 系一律禁用"的根因】
    /// ①探测图用【生产帧尺寸 1080×1920】(旧 320×240 太小:真机证据是"小图能过、真实分辨率静默出 0KB 空帧/黑帧,
    ///   退出码还是 0",于是探测判"可用"→ 整段黑);
    /// ②参数与生产逐字一致(-s 2 -n 0 -t 0 -j 1:1:1 + 真实模型),而不是裸 -s 2;
    /// ③判据含【带状近黑】:IsBlackPng → FrameInspect.IsDefectiveFrame = 整帧 ≥95% 近黑 或 任一 1/3 主条带 ≥95% 近黑
    ///   (整帧量词在"下 2/3 全黑、上 1/3 正常"时只黑 66%,判不出来 —— 这正是旧探测漏检的形态)。
    /// gpuId&lt;0(用户选 CPU)直接返回 false:ncnn CPU 模式在 50 系上有崩溃 bug(实测 exit -1073741819)。</summary>
    public static async Task<bool> EnsureNcnnProbeAsync(string engine, int gpuId, string? model, CancellationToken ct)
    {
        if (gpuId < 0) return false;
        // 【快速通道:纯 NVIDIA 非 Blackwell + 机内没有非 NVIDIA 显卡 → 不探测,直接判可用】
        // 理由:①这套组合从无"ncnn-Vulkan 跑不通"的实测证据(实测出问题的是 50 系、AMD、无独显);
        // ②一次探测要 ~15 秒,而图片路径上一个任务可能总共只要 2 秒 —— 不能先白等 15 秒;
        // ③真出问题还有引擎自身的"输出全黑 → 该设备判不可用 + 降级链"兜底(见 IsEngineGpuUsableAsync)。
        // 50 系 / AMD / Intel 核显一律照旧真机探测,该走的自适应一点不少。
        if (!IsBlackwellGpu() && !HasNonNvidiaGpu()) return true;
        var cached = TryGetNcnnVerdict(engine, gpuId);
        if (cached.HasValue)
        {
            AppLogger.Info($"[探测] {engine} GPU({gpuId}/{SafeDeviceName(gpuId)})沿用已缓存结论:" +
                (cached.Value ? "ncnn-Vulkan 可用 → 走 ncnn(不重复试跑)" : "ncnn-Vulkan 不可用 → 走 ONNX(不每批重试)"));
            return cached.Value;
        }
        AppLogger.Info($"[探测] {engine} GPU({gpuId})首次真机探测(生产帧尺寸 1080×1920,模型 {model ?? "(默认)"} + 带状黑判据,最长约 60 秒)...");
        // 接住失败形态:日志、落盘明细、以及给用户看的话都由它决定(见 AlhPro.Core.ProbeDiagnosis)
        AlhPro.Core.ProbeFailureKind failKind = AlhPro.Core.ProbeFailureKind.None;
        string failDetail = "";
        bool ok = await IsEngineGpuUsableAsync(engine, gpuId, ct, fullFrame: true, model: model,
            (k, d) => { failKind = k; failDetail = d; }).ConfigureAwait(false);
        // 结论按【引擎|GPU】记账(决策键);明细带上失败形态 —— 诊断包里一眼能分出"初始化即崩"还是"出图但坏帧"。
        SaveNcnnVerdict(engine, gpuId, ok,
            (ok ? "probe ok" : "probe failed: " + AlhPro.Core.ProbeDiagnosis.ShortName(failKind))
            + $"; model={model ?? "(default)"}");
        if (ok)
        {
            LastProbeUserMessage = "";
            AppLogger.Info($"[探测] {engine} GPU({gpuId})真机探测通过 → 使用 ncnn-Vulkan(50 系未禁用,走最快路径)");
        }
        else
        {
            // 【按形态说话】初始化即崩(Blackwell 上=NVIDIA 驱动缺陷)与"出图但坏帧"是两回事,不能混为一谈
            LastProbeUserMessage = AlhPro.Core.ProbeDiagnosis.Describe(failKind, IsBlackwellGpu(),
                EngineLabel(engine));
            AppLogger.Warn($"[探测] {engine} GPU({gpuId})真机探测失败({AlhPro.Core.ProbeDiagnosis.ShortName(failKind)}"
                + (failDetail.Length > 0 ? ";" + failDetail : "") + ")→ 为稳定性改用 ONNX(结论已记住,不再重复试)。"
                + LastProbeUserMessage);
        }
        return ok;
    }

    /// <summary>补帧(RIFE)同款"先探后决定 + 结论缓存"。
    /// 旧逻辑在 50 系上【直接】改走 ONNX、不做任何探测(原文:"50系(Blackwell)ncnn 补帧引擎会 hang,直接改用 ONNX")。
    /// 现在改为真实插一帧实测(含"出帧但颜色损坏"判据):通过就用更快的 ncnn-Vulkan 补帧,失败才 ONNX。
    /// 模型名进缓存 key:NIHUI 老模型(anime/HD/UHD/v2.3/anime)与 v4.x 架构不同,稳定性不能互相顶替。</summary>
    public static async Task<bool> EnsureRifeNcnnProbeAsync(string rifeExe, string model, int gpuId, CancellationToken ct)
    {
        if (gpuId < 0 || string.IsNullOrEmpty(rifeExe)) return false;
        var key = "rife:" + model;
        var cached = TryGetNcnnVerdict(key, gpuId);
        if (cached.HasValue)
        {
            AppLogger.Info($"[探测] 补帧 {model} GPU({gpuId})沿用已缓存结论:" + (cached.Value ? "可用 → 走 ncnn-Vulkan" : "不可用 → 走 ONNX"));
            return cached.Value;
        }
        AlhPro.Core.ProbeFailureKind failKind = AlhPro.Core.ProbeFailureKind.None;
        string failDetail = "";
        bool ok = await IsRifeGpuUsableAsync(rifeExe, model, gpuId, ct,
            (k, d) => { failKind = k; failDetail = d; }).ConfigureAwait(false);
        SaveNcnnVerdict(key, gpuId, ok,
            (ok ? "rife probe ok" : "rife probe failed: " + AlhPro.Core.ProbeDiagnosis.ShortName(failKind)) + $"; model={model}");
        if (ok)
        {
            LastProbeUserMessage = "";
            AppLogger.Info($"[探测] 补帧 {model} GPU({gpuId})真机探测通过 → 使用 ncnn-Vulkan 补帧");
        }
        else
        {
            LastProbeUserMessage = AlhPro.Core.ProbeDiagnosis.Describe(failKind, IsBlackwellGpu(), "RIFE");
            AppLogger.Warn($"[探测] 补帧 {model} GPU({gpuId})真机探测失败({AlhPro.Core.ProbeDiagnosis.ShortName(failKind)}"
                + (failDetail.Length > 0 ? ";" + failDetail : "") + ")→ 为稳定性改用 ONNX 补帧(结论已记住)。"
                + LastProbeUserMessage);
        }
        return ok;
    }

    /// <summary>【本机实测优先】某引擎的 ncnn-Vulkan 在本机该 GPU 上是否算"风险"(=该走 ONNX)。
    /// ①有实测结论(EnsureNcnnProbeAsync / EnsureRifeNcnnProbeAsync 写入)→ 一律以实测为准:
    ///    实测可用 → false(走 ncnn),实测不可用 → true(走 ONNX);
    /// ②没测过 → 回退原保守启发式(treatBlackwellAsRiskyWithoutProbe 决定 50 系算不算风险),
    ///    保证"没探测过的调用路径"与本次改动前行为一致,不引入回归。</summary>
    public static bool NcnnGpuRisky(string engine, int gpuId, bool treatBlackwellAsRiskyWithoutProbe = true)
    {
        try
        {
            var v = TryGetNcnnVerdict(engine, gpuId);
            if (v.HasValue) return !v.Value;
        }
        catch { }
        return OldNcnnGpuRiskyHeuristic(treatBlackwellAsRiskyWithoutProbe);
    }

    /// <summary>是否存在非 NVIDIA 显卡(AMD/Intel,含核显):驱动差异大,需要真机探测兜底。</summary>
    private static bool HasNonNvidiaGpu()
    {
        if (_nonNvidiaCache.HasValue) return _nonNvidiaCache.Value;
        try
        {
            var names = new System.Collections.Generic.List<string>();
            try { names.AddRange(VulkanCheck.Devices.Select(d => d.Name)); } catch { }
            try { names.AddRange(GpuInfo.GetAdapterNames()); } catch { }
            bool hasNv = names.Any(n => n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                || n.Contains("GeForce", StringComparison.OrdinalIgnoreCase));
            bool hasOther = names.Any(n =>
                n.Contains("AMD", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Intel", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Arc", StringComparison.OrdinalIgnoreCase));
            _nonNvidiaCache = hasOther || (!hasNv && names.Count > 0);
        }
        catch { _nonNvidiaCache = false; }
        return _nonNvidiaCache.Value;
    }

    /// <summary>ncnn 引擎的 -g 编号 → DirectML 设备号。
    /// 双卡机(AMD 核显 + NVIDIA 独显 / Intel 核显 + 独显等)上,Vulkan 引擎枚举顺序与 DirectML(DXGI)
    /// 枚举顺序【可能不同】——直接拿 ncnn 编号喂 DirectML 会跑错卡。
    /// 【修复】按 DXGI 真枚举(名匹配)得到正确 DirectML 设备号;DXGI 不可用时回退注册表名匹配;
    /// 匹配不到宁可落 CPU(明确日志),不静默跑核显。</summary>
    public static int ToDmlDevice(int engineGpu)
    {
        try
        {
            if (engineGpu < 0) return engineGpu;
            var devs = VulkanCheck.Devices;
            if (devs.Count <= 1) return engineGpu;   // 单卡:无歧义
            var want = devs.FirstOrDefault(d => d.Id == engineGpu);
            if (string.IsNullOrWhiteSpace(want.Name)) return engineGpu;
            // ① DXGI 真枚举:名匹配 → DirectML 设备号(顺序=DXGI,与注册表可能不同)
            try
            {
                var dxAdapters = TryEnumerateDxgiAdapters();
                foreach (var (idx, name, _, _) in dxAdapters)
                    if (name.Equals(want.Name, StringComparison.OrdinalIgnoreCase)
                        || name.Contains(want.Name, StringComparison.OrdinalIgnoreCase)
                        || want.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
                        return idx;
                // ①b 名字没命中 → 用【显存】可靠识别独显(独显 GB 级,核显只有几十~几百 MB):完全不依赖名字,
                //     这样"选独显"时即使名字格式有差异也一定落到真独显。这是"锁定用独显"的最后一道可靠保险。
                // 【已移除】原 ①b「Windows 官方 GPU 偏好(EnumAdapterByGpuPreference)」:
                //   本项目手写的 IDXGIFactory6 声明漏了 IDXGIObject::GetPrivateData,槽位整体前移一位,
                //   实测按该签名调用会跨签名 UB —— 在委托探针里直接 AccessViolation 终止进程(不可 catch)。
                //   槽位修好后 ① 名字匹配已真正生效,该兜底收益远小于崩溃风险,故整段删除。
                if (dxAdapters.Count > 0 && !AlhPro.Core.GpuName.IsIntegrated(want.Name))
                {
                    var biggest = dxAdapters.OrderByDescending(a => a.Vram).First();
                    if (biggest.Vram > 1073741824L)   // >1GB = 独显级(核显 DedicatedVideoMemory 通常只有几十~几百 MB)
                    {
                        AppLogger.Info($"设备映射:引擎 {engineGpu}({want.Name}) 名字未命中 DXGI 表,已按【显存最大】定位独显 → DXGI#{biggest.Index}({biggest.Name},{biggest.Vram / 1073741824.0:0.#}GB)");
                        return biggest.Index;
                    }
                }
            }
            catch { }
            // ② DXGI 不可用(罕见):回退注册表名匹配(≈DXGI 序)——【风险路径】注册表序可能与 DirectML 序相反,
            // 双卡机上会把 NVIDIA 映射到核显号。明确留痕,便于诊断包定位"选独显跑核显"。
            try
            {
                var names = GpuInfo.GetAdapterNames();
                for (int i = 0; i < names.Count; i++)
                    if (names[i].Equals(want.Name, StringComparison.OrdinalIgnoreCase)
                        || names[i].Contains(want.Name, StringComparison.OrdinalIgnoreCase)
                        || want.Name.Contains(names[i], StringComparison.OrdinalIgnoreCase))
                    {
                        AppLogger.Warn($"⚠ 设备映射:DXGI 枚举不可用,已回退【注册表序】匹配——引擎编号 {engineGpu}({want.Name}) → 注册表#{i}。" +
                            $"注册表序可能与 DirectML 设备号序相反(双卡机常见),若出现'选独显却跑核显'请把本行发作者。DXGI错误={LastDxgiError}");
                        return i;
                    }
            }
            catch { }
            // ③ 匹配不到:落 CPU(宁可慢不跑错卡)
            AppLogger.Warn($"⚠ 设备映射:引擎编号 {engineGpu} 未匹配到同名 DirectML 设备;为避免静默跑核显,本次已改用 CPU(软件计算)。可用引擎设备={string.Join(",", devs.Select(d => d.Id + ":" + d.Name))}");
            return -1;
        }
        catch { return engineGpu; }
    }

    /// <summary>把"设置里存的计算设备编号"解析成本次要传给引擎的 -g 编号(唯一权威入口,尊重用户选择)。
    /// 规则(比"强制锁定独显"更尊重用户,满足"选独显跑独显、选核显跑核显、默认独显"):
    ///   ① settingsIndex &lt; 0 → 用户主动选 CPU,原样 -1(尊重);
    ///   ② 编号在设备表 → 原样返回它的引擎 -g 编号【不管核显/独显】——用户选核显就核显;
    ///   ③ 编号不在表 / 表为空 → 返回推荐的独显编号(GetRecommendedEngineId / 表内独显 / 兜底),避免无效编号崩。
    /// 默认"选独显"由设备下拉默认选中推荐项(即最佳独显)实现,这里不做强制纠正。
    /// </summary>
    public static int ResolveEngineGpu(int settingsIndex)
    {
        if (settingsIndex < 0) return -1;   // 用户主动选 CPU
        try
        {
            var devs = VulkanCheck.Devices;
            if (devs.Count > 0)
                foreach (var d in devs)
                    if (d.Id == settingsIndex) return settingsIndex;   // 尊重用户选择(核显就核显)
            // 编号不在表/表空:用推荐(通常独显),绝不落 CPU
            int rec = GpuInfo.GetRecommendedEngineId();
            if (rec >= 0) return rec;
            if (devs.Count > 0) return devs[0].Id;
        }
        catch { }
        return settingsIndex < GpuInfo.EngineDeviceCount ? settingsIndex : -1;   // 兜底
    }

    /// <summary>把"请求的计算设备编号"解析成应实际使用的 DirectML 设备号——经 ResolveEngineGpu(尊重用户)
    /// 得到引擎 -g 编号,再 ToDmlDevice 按名字匹配映射到 DirectML 设备。</summary>
    public static int ResolveDmlDevice(int requestedGpu)
    {
        int engineGpu = ResolveEngineGpu(requestedGpu);
        return ToDmlDevice(engineGpu);
    }

    /// <summary>诊断用:一次性打印"DXGI 枚举序(=DirectML 设备号) + 每个引擎编号→DirectML号 的映射"。
    /// 排查"选独显实际跑核显"的关键数据——注册表序、引擎序、DXGI(DirectML)序是三套可能互不相同的编号,
    /// 只有并排才能一眼看出 ToDmlDevice 映射对不对(此前进诊断包的只有"注册表 vs 引擎",缺 DXGI 这一环)。</summary>
    public static string DescribeDmlMapping()
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            var dx = TryEnumerateDxgiAdapters();
            sb.Append("DXGI(DirectML)枚举[");
            for (int i = 0; i < dx.Count; i++) { if (i > 0) sb.Append(" | "); sb.Append($"#{dx[i].Index} {dx[i].Name}"); }
            if (dx.Count == 0) sb.Append("(枚举失败)");
            sb.Append("] 引擎→DML 映射[");
            var devs = VulkanCheck.Devices;
            for (int i = 0; i < devs.Count; i++)
            {
                if (i > 0) sb.Append(" | ");
                int dm = ToDmlDevice(devs[i].Id);
                string dmName;
                if (dm < 0) dmName = "(未匹配→走CPU)";
                else
                {
                    dmName = "(?";
                    try { foreach (var a in dx) if (a.Index == dm) { dmName = a.Name; break; } } catch { }
                    dmName = $"({dmName})";
                }
                sb.Append($"引擎#{devs[i].Id}[{devs[i].Name}]→DML#{dm}{dmName}");
            }
            if (devs.Count == 0) sb.Append("(引擎未枚举)");
            sb.Append("] DML探测首可用=#").Append(EsrganOnnxService.DmlFallbackOk);
            // 【第 1/2 项】把"探测是否已完成 + 失败的真实原因"直接写进诊断行:
            // DmlFallbackOk=-1 有两种含义(还没探 / 探完确认不可用),下游与诊断包都必须能区分;
            // 失败原因带 HRESULT(十六进制)/异常类型/Message/InnerException,一眼定性是显存不足(0x8007000E)、
            // 设备摘除(0x887A0005/6)还是 provider 注册失败 —— 不必再去日志里翻上下文。
            if (!EsrganOnnxService.DmlProbeCompleted) sb.Append("(探测未完成/未做)");
            else if (EsrganOnnxService.DmlFallbackOk < 0)
                sb.Append("(探测已完成:DirectML 不可用;原因=").Append(EsrganOnnxService.DmlUnavailableReason).Append(')');
            else
                sb.Append("(探测已完成:可用;本次只探映射目标设备,不遍历 0..3)");
            if (!string.IsNullOrEmpty(LastDxgiError)) sb.Append(" DXGI失败原因=").Append(LastDxgiError);
        }
        catch (Exception ex) { sb.Append("(诊断失败:").Append(ex.Message).Append(')'); }
        return sb.ToString();
    }

    // ===== DXGI 真枚举:显卡名 → DXGI/DirectML 设备号(替代按注册表顺序猜,双卡机上注册表序≠DXGI 序会选错卡) =====
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValArray, SizeConst = 128, ArraySubType = System.Runtime.InteropServices.UnmanagedType.U2)]
        public char[] Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public long DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }
    [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("29038f61-3839-4626-91fd-086879011a05"), System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        // 【声明顺序 = vtable 顺序,一个都不能少、不能换位】槽位见下方 Slot* 常量。
        // IDXGIObject(3..6)
        [System.Runtime.InteropServices.PreserveSig] int SetPrivateData(System.Guid Name, uint DataSize, System.IntPtr data);
        [System.Runtime.InteropServices.PreserveSig] int SetPrivateDataInterface(System.Guid Name, System.IntPtr data);
        [System.Runtime.InteropServices.PreserveSig] int GetPrivateData(System.Guid Name, ref uint DataSize, System.IntPtr data);
        [System.Runtime.InteropServices.PreserveSig] int GetParent(ref System.Guid riid, out System.IntPtr ppParent);
        // IDXGIAdapter(7..9):EnumOutputs → GetDesc → CheckInterfaceSupport
        [System.Runtime.InteropServices.PreserveSig] int EnumOutputs(uint Output, out System.IntPtr ppOutput);
        [System.Runtime.InteropServices.PreserveSig] int GetDesc(out DXGI_ADAPTER_DESC1 pDesc);
        [System.Runtime.InteropServices.PreserveSig] int CheckInterfaceSupport(ref System.Guid riid, out long pUMDVersion);
        // IDXGIAdapter1(10)
        [System.Runtime.InteropServices.PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 pDesc);
    }
    [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("770aae78-f26f-4dba-a829-253c83d1b387"), System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        [System.Runtime.InteropServices.PreserveSig] int SetPrivateData(System.Guid Name, uint DataSize, System.IntPtr data);
        [System.Runtime.InteropServices.PreserveSig] int SetPrivateDataInterface(System.Guid Name, System.IntPtr data);
        [System.Runtime.InteropServices.PreserveSig] int GetPrivateData(System.Guid Name, ref uint DataSize, System.IntPtr data);
        [System.Runtime.InteropServices.PreserveSig] int GetParent(ref System.Guid riid, out System.IntPtr ppParent);
        // IDXGIFactory(7..11):EnumAdapters → MakeWindowAssociation → GetWindowAssociation
        //                       → CreateSwapChain → CreateSoftwareAdapter
        // 【这三个占位缺一不可】原先只声明了前两个,直接导致 EnumAdapters1 落到 CreateSwapChain 的槽位上。
        [System.Runtime.InteropServices.PreserveSig] int EnumAdapters(uint Adapter, out System.IntPtr ppAdapter);
        [System.Runtime.InteropServices.PreserveSig] int MakeWindowAssociation(System.IntPtr hwnd, uint flags);
        [System.Runtime.InteropServices.PreserveSig] int GetWindowAssociation(out System.IntPtr phwnd);
        [System.Runtime.InteropServices.PreserveSig] int CreateSwapChain(System.IntPtr pDevice, System.IntPtr pDesc, out System.IntPtr ppSwapChain);
        [System.Runtime.InteropServices.PreserveSig] int CreateSoftwareAdapter(System.IntPtr Module, out System.IntPtr ppAdapter);
        // IDXGIFactory1(12..13)
        [System.Runtime.InteropServices.PreserveSig] int EnumAdapters1(uint Adapter, out IDXGIAdapter1 ppAdapter);
        [System.Runtime.InteropServices.PreserveSig] int IsCurrent();
    }
    [System.Runtime.InteropServices.DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref System.Guid riid, out System.IntPtr ppFactory);

    // ===== DXGI 真枚举:vtable 手动调用(不依赖 built-in COM interop)=====
    // 【为什么必须这么做】原实现用 Marshal.GetObjectForIUnknown + COM 接口强转,而 .NET 5+ 的 built-in
    // COM interop 默认**关闭**(csproj 未开 BuiltInComInteropSupport)→ 该调用抛异常,又被 catch{} 静默吞掉
    // → TryEnumerateDxgiAdapters 永远返回空 → ToDmlDevice 的"DXGI 名匹配"整条失效,只能回退到【注册表序】名匹配。
    // 而双卡机上注册表序(实测 [#0 AMD][#1 NVIDIA])与 DirectML 设备号序相反 → 引擎编号 0(NVIDIA) 被映射到
    // DirectML 的核显号 → 表现为"选独显却跑核显"。改用手动读 vtable 调 COM 方法,彻底摆脱该开关依赖。
    private delegate int DxgiEnumAdapters1Fn(IntPtr self, uint index, out IntPtr adapter);
    private delegate int DxgiGetDesc1Fn(IntPtr self, out DXGI_ADAPTER_DESC1 desc);
    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.StdCall)]
    private delegate uint DxgiReleaseFn(IntPtr self);

    /// <summary>从 COM 对象的 vtable 取第 slot 个方法指针。</summary>
    private static IntPtr VtblSlot(IntPtr comObj, int slot)
    {
        var vtbl = System.Runtime.InteropServices.Marshal.ReadIntPtr(comObj);
        return System.Runtime.InteropServices.Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size);
    }

    // vtable 槽位(继承链累计)。【这张表曾经算错两位,导致 DXGI 枚举从未成功过一次,务必按下面核对】
    //   IUnknown:    QueryInterface=0  AddRef=1  Release=2
    //   IDXGIObject: +SetPrivateData=3 SetPrivateDataInterface=4 【GetPrivateData=5】GetParent=6
    //                ↑ 老注释漏了 GetPrivateData,使 GetParent 之后全部前移一位
    //   IDXGIFactory:  +EnumAdapters=7 MakeWindowAssociation=8 GetWindowAssociation=9
    //                  CreateSwapChain=10 CreateSoftwareAdapter=11
    //   IDXGIFactory1: +EnumAdapters1=12 IsCurrent=13
    //   IDXGIAdapter:  (继承 IDXGIObject) +EnumOutputs=7 GetDesc=8 CheckInterfaceSupport=9
    //   IDXGIAdapter1: +GetDesc1=10
    // 【实测证明 2026-09-10】在本机用独立探针直接调槽位验证:
    //   factory slot 11 → hr=0x887A0001(那其实是 CreateSoftwareAdapter(module=0) 的报错),slot 12 → hr=0,返回真实显卡名
    //   adapter slot 9  → hr=0x887A0004(CheckInterfaceSupport),slot 10 → hr=0,返回 'NVIDIA GeForce RTX 4060 Laptop GPU'
    private const int SlotEnumAdapters1 = 12;
    private const int SlotGetDesc1 = 10;
    private const int SlotRelease = 2;

    /// <summary>枚举 DXGI 适配器(顺序 = DirectML 设备号)。返回 (索引, 名字, LUID)。失败/无卡返回空;
    /// 失败原因写入 lastDxgiError 供诊断包定位(不再静默吞掉)。</summary>
    public static string LastDxgiError { get; private set; } = "";

    private static System.Collections.Generic.List<(int Index, string Name, long Luid, long Vram)> TryEnumerateDxgiAdapters()
    {
        var list = new System.Collections.Generic.List<(int, string, long, long)>();
        LastDxgiError = "";
        // ① 首选 COM interop(csproj 已开 BuiltInComInteropSupport=true):DXGI 真枚举,索引 = DirectML 设备号
        try
        {
            var viaCom = TryEnumerateDxgiAdaptersCom();
            if (viaCom.Count > 0) return viaCom;
        }
        catch (Exception ex) { LastDxgiError = "COM方式:" + ex.GetType().Name + "(" + ex.Message.Split('\n')[0] + ")"; }
        // ② 兜底:vtable 手动调用(不依赖 COM interop 开关)
        IntPtr factoryPtr = IntPtr.Zero;
        try
        {
            var riid = new System.Guid("770aae78-f26f-4dba-a829-253c83d1b387");   // IDXGIFactory1
            int hr = CreateDXGIFactory1(ref riid, out factoryPtr);
            if (hr != 0 || factoryPtr == IntPtr.Zero)
            {
                LastDxgiError = $"CreateDXGIFactory1 hr=0x{hr:X8}";
                return list;
            }
            var enumAdapters1 = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<DxgiEnumAdapters1Fn>(VtblSlot(factoryPtr, SlotEnumAdapters1));
            var releaseFactory = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<DxgiReleaseFn>(VtblSlot(factoryPtr, SlotRelease));
            for (uint i = 0; ; i++)
            {
                if (enumAdapters1(factoryPtr, i, out var adapterPtr) != 0 || adapterPtr == IntPtr.Zero) break;
                try
                {
                    var getDesc1 = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<DxgiGetDesc1Fn>(VtblSlot(adapterPtr, SlotGetDesc1));
                    if (getDesc1(adapterPtr, out var desc) == 0)
                    {
                        var name = desc.Description != null ? new string(desc.Description).TrimEnd('\0', ' ') : "";
                        if (name.Length > 0) list.Add(((int)i, name, desc.AdapterLuid, desc.DedicatedVideoMemory));
                    }
                }
                finally
                {
                    try { System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<DxgiReleaseFn>(VtblSlot(adapterPtr, SlotRelease))(adapterPtr); } catch { }
                }
            }
            releaseFactory(factoryPtr);
        }
        catch (Exception ex)
        {
            LastDxgiError = (LastDxgiError.Length > 0 ? LastDxgiError + " | " : "") + "vtable:" + ex.GetType().Name + ": " + ex.Message.Split('\n')[0];
        }
        // 【诊断加固】"调用失败"与"调用成功但返回 0 张卡"必须区分开:
        // 原实现两种情况都只是得到一张空表,于是槽位写错(2026-09-10 已证实曾错两位)时
        // 表现为"DXGI 名字匹配总是失效"而毫无提示,只能一路静默回退注册表序。
        if (list.Count == 0)
        {
            if (LastDxgiError.Length == 0) LastDxgiError = "DXGI 调用成功但返回 0 张适配器";
            if (System.Threading.Interlocked.CompareExchange(ref _dxgiEmptyWarned, 1, 0) == 0)
                AppLogger.Warn($"⚠ DXGI 枚举未拿到任何适配器({LastDxgiError})——设备映射将回退【注册表序】,双卡机上可能选错卡;" +
                    $"注册表序与引擎序相反时表现为'选独显跑核显'。请把本行连同 'GPU→DirectML 映射对照' 一起发作者。");
        }
        return list;
    }

    /// <summary>「DXGI 枚举为空」只提示一次(该方法在每次建会话时都会被调到,不能刷屏)。</summary>
    private static int _dxgiEmptyWarned;

    /// <summary>COM interop 方式的 DXGI 枚举(需 csproj BuiltInComInteropSupport=true)。
    /// 这是官方支持的路径;vtable 方式作为不依赖该开关的兜底。</summary>
    private static System.Collections.Generic.List<(int Index, string Name, long Luid, long Vram)> TryEnumerateDxgiAdaptersCom()
    {
        var list = new System.Collections.Generic.List<(int, string, long, long)>();
        var riid = new System.Guid("770aae78-f26f-4dba-a829-253c83d1b387");   // IDXGIFactory1
        if (CreateDXGIFactory1(ref riid, out var factoryPtr) != 0 || factoryPtr == IntPtr.Zero) return list;
        var factory = (IDXGIFactory1)System.Runtime.InteropServices.Marshal.GetObjectForIUnknown(factoryPtr);
        try
        {
            for (uint i = 0; ; i++)
            {
                if (factory.EnumAdapters1(i, out var adapter) != 0 || adapter == null) break;
                try
                {
                    if (adapter.GetDesc1(out var desc) == 0)
                    {
                        var name = desc.Description != null ? new string(desc.Description).TrimEnd('\0', ' ') : "";
                        if (name.Length > 0) list.Add(((int)i, name, desc.AdapterLuid, desc.DedicatedVideoMemory));
                    }
                }
                finally { System.Runtime.InteropServices.Marshal.ReleaseComObject(adapter); }
            }
        }
        finally { System.Runtime.InteropServices.Marshal.ReleaseComObject(factory); }
        return list;
    }

    /// <summary>DXGI 实测的独显显存总量(GB):所有非软件适配器中 DedicatedVideoMemory 的最大值。
    /// 这是唯一跨厂商(NVIDIA/AMD/Intel)可靠的显存真值来源,用于 nvidia-smi 不可用时探测。
    /// 只有独显级(&gt;1GB)才返回:核显的 DedicatedVideoMemory 通常只有几十~几百 MB(其余走共享内存),
    /// 返回 null 让上层改用注册表 qwMemorySize —— 注册表对核显报告的是共享内存配额,那才是核显真正可用的预算。
    /// 失败/无独显返回 null。</summary>
    public static double? TryGetDxgiVramGb()
    {
        IntPtr factoryPtr = IntPtr.Zero;
        try
        {
            var riid = new System.Guid("770aae78-f26f-4dba-a829-253c83d1b387");   // IDXGIFactory1
            if (CreateDXGIFactory1(ref riid, out factoryPtr) != 0 || factoryPtr == IntPtr.Zero) return null;
            // 同 TryEnumerateDxgiAdapters:vtable 手动调用(不依赖 built-in COM interop;原 COM 方式在 .NET 5+ 下必抛并被吞)
            var enumAdapters1 = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<DxgiEnumAdapters1Fn>(VtblSlot(factoryPtr, SlotEnumAdapters1));
            var releaseFactory = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<DxgiReleaseFn>(VtblSlot(factoryPtr, SlotRelease));
            long best = 0;
            for (uint i = 0; ; i++)
            {
                if (enumAdapters1(factoryPtr, i, out var adapterPtr) != 0 || adapterPtr == IntPtr.Zero) break;
                try
                {
                    var getDesc1 = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<DxgiGetDesc1Fn>(VtblSlot(adapterPtr, SlotGetDesc1));
                    if (getDesc1(adapterPtr, out var desc) == 0)
                    {
                        const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;   // Microsoft 基本渲染驱动:无显存,必须排除
                        if ((desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) == 0 && desc.DedicatedVideoMemory > best)
                            best = desc.DedicatedVideoMemory;
                    }
                }
                finally
                {
                    try { System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<DxgiReleaseFn>(VtblSlot(adapterPtr, SlotRelease))(adapterPtr); } catch { }
                }
            }
            releaseFactory(factoryPtr);
            return best > 1073741824L ? best / 1073741824.0 : null;
        }
        catch { return null; }
    }

    /// <summary>风险启发式的公共实现。treatBlackwellAsRisky=false 给 waifu2x 用:
    /// 它自带的是 20250915 版(上游最新)引擎,未实测时不该按"风险"处理 —— 否则又退回"50 系一刀切禁用 ncnn"。</summary>
    private static bool OldNcnnGpuRiskyHeuristic(bool treatBlackwellAsRisky)
    {
        // Blackwell:历史上是 2022 版 ncnn 的 Vulkan 驱动问题(引擎够新则不一定)
        if (treatBlackwellAsRisky && IsBlackwellGpu()) return true;
        // Vulkan 不可用(无独显/驱动缺):引擎 GPU 无法跑,CPU 模式也崩 → 风险
        try { if (!ALHPro.VulkanCheck.GpuAvailable) return true; } catch { }
        // 其余(AMD/Intel 核显/NVIDIA 老卡):Vulkan 正常即可用,不预判(避免误报)
        return false;
    }

    /// <summary>【实测验证】推荐 GPU:引擎枚举的设备按优先级(NVIDIA&gt;AMD 独显&gt;Arc&gt;其他,核显排除)
    /// 逐个做 1×1 真机探测,返回第一个【实际可用】的引擎编号;-1=全部不可用。
    /// 不只按名字推荐——名字对但驱动/编号/引擎支持有问题时,实测能拦住(真机:RTX5060 三卡机选中 Intel 核显)。</summary>
    public static async Task<int> FindBestWorkingGpuAsync(CancellationToken ct = default)
    {
        try
        {
            var devs = VulkanCheck.Devices;
            if (devs == null || devs.Count == 0) return -1;
            // ① 优先独显(NVIDIA>AMD独显>Arc>其他,核显得分0不首选)
            var ordered = devs
                .Select(d => new { d.Id, d.Name, Score = GpuInfo.ScoreDeviceName(d.Name) })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score).ThenBy(x => x.Id)
                .Take(4);   // 最多探测 4 台(多卡机 3~4 张封顶,速度可控)
            foreach (var d in ordered)
            {
                bool ok = await IsEngineGpuUsableAsync("waifu2x", d.Id, ct).ConfigureAwait(false);
                AppLogger.Info(d.Id + ": " + d.Name + " → " + (ok ? "1×1 可用" : "不可用"));
                if (ok) return d.Id;
            }
            // ② 独显全不可用 → 核显作"底牌"兜底(核显也是计算设备,能用就用,总比报错强)
            var igpu = devs.Where(d => GpuInfo.ScoreDeviceName(d.Name) == 0).OrderBy(d => d.Id).Take(2);
            foreach (var d in igpu)
            {
                bool ok = await IsEngineGpuUsableAsync("waifu2x", d.Id, ct).ConfigureAwait(false);
                AppLogger.Info(d.Id + ": " + d.Name + "(核显) → " + (ok ? "1×1 可用(兜底)" : "不可用"));
                if (ok)
                {
                    AppLogger.Warn($"⚠ 独显均不可用,已降级使用核显: GPU {d.Id}({d.Name})——处理会明显变慢,建议更新显卡驱动(需支持 Vulkan)后重试。");
                    return d.Id;
                }
            }
            return -1;
        }
        catch { return -1; }
    }

    /// <summary>临时文件根目录(所有页面/引擎的临时帧、中间文件统一放这里)。
    /// 优先级:①设置里用户自定义(需存在且可写,否则自动回退)②剩余空间最大的本地盘 ③系统 %TEMP%。
    /// 清理:任务完成自动删;软件启动会清理残留(imgup_*/alh_* 前缀,绝不碰用户文件)。</summary>
    public static string TempRoot
    {
        get
        {
            var cfg = AppSettings.TempDir;
            if (!string.IsNullOrWhiteSpace(cfg))
            {
                try
                {
                    // 【修复 自定义临时目录指向盘根】用户可能手动把临时目录设成盘根(如 C:\)——
                    // 盘根对普通用户 Access denied,不能直接当临时目录。这里拒绝纯盘根,并回退。
                    var cfgRoot = Path.GetPathRoot(cfg)?.TrimEnd('\\', '/');
                    var cfgTrim = cfg.TrimEnd('\\', '/');
                    if (cfgTrim.Equals(cfgRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        AppLogger.Warn($"⚠ 临时目录不能设为盘根({cfg})——已自动回退(剩余空间最大的盘)");
                    }
                    else if (Directory.Exists(cfg))
                    {
                        var probe = Path.Combine(cfg, ".alh_pro_w.tmp");
                        File.WriteAllText(probe, "x");
                        File.Delete(probe);
                        RecordTempRoot(cfg);
                        return cfg;
                    }
                }
                catch { }
                AppLogger.Warn($"⚠ 设置的临时目录不可用: {cfg} —— 已自动回退(剩余空间最大的盘)");
            }
            // 自动:剩余空间最大的本地盘(8x 补帧+超分峰值可达 30GB+,避免系统盘被写爆)
            string best = null!;
            long bestFree = -1;
            try
            {
                foreach (var d in System.IO.DriveInfo.GetDrives())
                {
                    try
                    {
                        if (d.DriveType != System.IO.DriveType.Fixed || !d.IsReady) continue;
                        if (d.AvailableFreeSpace > bestFree)
                        {
                            bestFree = d.AvailableFreeSpace;
                            best = d.RootDirectory.FullName;
                        }
                    }
                    catch { }
                }
            }
            catch { }
            // 【修复 盘根不可写】不能直接用盘根(如 C:\)做临时目录——普通用户对盘根 Access denied,
            // 会导致 GPU 自检/诊断导出/临时帧全部写失败(用户实测:临时目录 C:\ 但 Access to path 'C:\imgup_vk_*.png' is denied)。
            // 统一在盘根下建一个可写子目录(如 C:\ALHProTemp 或 D:\ALHProTemp)再用;若该盘根连子目录都建不了(罕见),退回 %TEMP%。
            if (!string.IsNullOrWhiteSpace(best))
            {
                var probeRoot = System.IO.Path.Combine(best.TrimEnd('\\', '/'), "ALHProTemp");
                try
                {
                    System.IO.Directory.CreateDirectory(probeRoot);
                    var probe = System.IO.Path.Combine(probeRoot, ".alh_pro_w.tmp");
                    System.IO.File.WriteAllText(probe, "x");
                    System.IO.File.Delete(probe);
                    best = probeRoot;
                }
                catch
                {
                    // 盘根不可写 → 退回系统 %TEMP%(一定可写)
                    best = System.IO.Path.GetTempPath().TrimEnd('\\', '/');
                }
            }
            if (string.IsNullOrWhiteSpace(best) || bestFree <= 0)
                best = System.IO.Path.GetTempPath().TrimEnd('\\', '/');
            RecordTempRoot(best);
            return best;
        }
    }

    // ===== 记录"用过的临时根目录",供启动清理扫旧路径残留 =====
    // 【修复 换盘/换路径后残留】自动指定路径可能落在任意"剩余最大固定盘"根目录;若下次自动选到别的盘/用户改了自定义路径,
    // 上次用的盘/目录里的 imgup_*/alh_* 残留不会被 CleanupTempDirs 扫到(只扫 %TEMP%+当前路径)→ 累积成几十上百 G。
    // 这里把每个实际用过的临时根目录持久化下来,启动清理时一并扫描。
    private static readonly System.Collections.Generic.HashSet<string> _usedTempRoots = new(System.StringComparer.OrdinalIgnoreCase);
    private static readonly object _rootsLock = new();
    private static bool _rootsLoaded;
    private static string TempRootsFile => ParaPaths.SettingsFile("temp-roots.json");
    private static void LoadUsedTempRoots()
    {
        if (_rootsLoaded) return;
        try
        {
            lock (_rootsLock)
            {
                if (_rootsLoaded) return;
                _rootsLoaded = true;
                if (File.Exists(TempRootsFile))
                    foreach (var line in File.ReadAllLines(TempRootsFile))
                        if (!string.IsNullOrWhiteSpace(line)) _usedTempRoots.Add(line.Trim());
            }
        }
        catch { }
    }
    /// <summary>记录本软件用过的一个临时根目录(自动选的盘 / 自定义路径),供启动清理旧路径残留。幂等,新增才落盘。</summary>
    public static void RecordTempRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return;
        LoadUsedTempRoots();
        lock (_rootsLock)
        {
            if (_usedTempRoots.Add(root))
            {
                try { File.WriteAllLines(TempRootsFile, _usedTempRoots); } catch { }
            }
        }
    }
    /// <summary>本软件用过的全部临时根目录(含已换掉的旧路径)。返回只读快照,避免外部拿到内部可变集合。</summary>
    public static System.Collections.Generic.IReadOnlyCollection<string> UsedTempRoots
    {
        get { lock (_rootsLock) { LoadUsedTempRoots(); return _usedTempRoots.ToArray(); } }
    }

    // 引擎根目录:优先 exe 旁 engines/ 目录;否则从当前目录向上逐级搜索(覆盖源码布局/输出目录)
    public static string EnginesDir
    {
        get
        {
            var exeDir = AppContext.BaseDirectory;
            var local = Path.Combine(exeDir, "engines");
            if (Directory.Exists(local)) return local;
            var dir = new DirectoryInfo(exeDir);
            for (int i = 0; i < 8 && dir != null; i++)
            {
                var cand = Path.Combine(dir.FullName, "engines");
                if (Directory.Exists(cand)) return cand;
                dir = dir.Parent;
            }
            return exeDir;
        }
    }

    /// <summary>动漫模式可选模型(waifu2x 系,全部 MIT 许可)。显示名=类别·模型名(体量 MB · 快/中/慢);取值用 Model=模型名。
    /// 【为什么就这几个】联网逐个核过许可:效果更强的社区模型要么前端是 AGPL(Upscayl / SPAN-ncnn / Universal-NCNN-Upscaler)、
    /// 要么权重是非商用(NMKD 等),要么官方只发 .pth(ncnn 权重官方没有,如 realesr-general-x4v3)——都不能随安装包分发。
    /// 详见 ENGINE_REALESRGAN_REBUILD.md 的"可用模型盘点"一节。</summary>
    public static readonly (string Label, string Engine, string Model)[] AnimeModels =
    {
        ("通用 · models-cunet（24MB · 中）", "waifu2x", "models-cunet"),
        ("动漫 · models-upconv_7_anime_style_art_rgb（5MB · 快）", "waifu2x", "models-upconv_7_anime_style_art_rgb"),
        ("实拍/照片 · models-upconv_7_photo（5MB · 快）", "waifu2x", "models-upconv_7_photo"),
    };

    // 注意:预处理降噪用 cunet(models-cunet 自带 1x 降噪模型 noise_model.bin);
    // upconv_7_photo/upconv_7_anime 只有 2x 降噪模型(noiseN_scale2.0x),-s 1 降噪会失败。

    /// <summary>Real-ESRGAN 可选模型(照片模式)。显示名=类别·模型名(体量 MB · 快/中/慢);取值用 Name=模型名。</summary>
    public static readonly (string Label, string Name)[] PhotoModels =
    {
        ("动漫 · realesr-animevideov3（4MB · 快）", "realesr-animevideov3"),
        ("动漫 · realesrgan-x4plus-anime（9MB · 中）", "realesrgan-x4plus-anime"),
        ("通用 · realesrgan-x4plus（41MB · 慢）", "realesrgan-x4plus"),
        // 【必须追加在末尾】自转的 ncnn 版轻量通用模型。官方只发 .pth/onnx,没有 ncnn 权重;
        // 用 ncnn 老版 onnx2ncnn + ncnnoptimize 转出:实测 960x540→4K 1.7 秒、PSNR 33.13dB(与 x4plus 同档),4.85MB。
        // 转换踩的坑:① 引擎必须开 Clip 层 ② 图内 3 处引用 input、输入层定义却叫 data,全局统一后 ncnnoptimize 才能加载
        // ③ 工具要在 MSYS2 环境跑(依赖 protobuf DLL)。
        // 【位置约定】列表存的是序号(index),新模型一律追加末尾,否则老用户已保存的选择会集体错位。
        ("通用 · realesr-general-x4v3（5MB · 快 · 轻量通用）", "realesr-general-x4v3"),
    };

    /// <summary>分块尺寸:大图按 tile 分块超分再拼接(防显存爆)。
    /// 默认值由「安全渲染」墙(SafeRender.GetTileSize)自动决定,不再写死。</summary>

    private static string? FindExe(string engineName, string exeName)
    {
        var root = Path.Combine(EnginesDir, engineName);
        if (Directory.Exists(root))
        {
            foreach (var f in Directory.EnumerateFiles(root, exeName, SearchOption.AllDirectories))
                return f;
        }
        var direct = Path.Combine(EnginesDir, exeName);
        return File.Exists(direct) ? direct : null;
    }

    public static string? FindWaifu2x() => FindExe("waifu2x", "waifu2x-ncnn-vulkan.exe");

    /// <summary>新版 Real-ESRGAN 引擎(2026 重编译:官方 MIT 前端 + 含 Blackwell 修复的 ncnn)。
    /// 官方 2022 版 exe 在 50 系上会出坏帧,必须靠新版才能走 ncnn 快路。</summary>
    public static string? FindRealEsrgan2026() => FindExe("realesrgan", "realesrgan-ncnn-vulkan-2026.exe");

    /// <summary>实际使用的 Real-ESRGAN 引擎:有新版就用新版,否则回退官方 2022 版。</summary>
    public static string? FindRealESRGAN() => FindRealEsrgan2026() ?? FindExe("realesrgan", "realesrgan-ncnn-vulkan.exe");

    /// <summary>Real-ESRGAN 引擎的"身份键"。装了新版就用 "realesrgan2026",否则沿用 "realesrgan"。
    /// 【为什么必须区分】探测结论按 "引擎|GPU" 落盘缓存(成功 7 天):官方 2022 版在 50 系上"出坏帧"是真结论,
    /// 若沿用同一个键,新引擎会被这条旧结论直接判死 → 又回到又慢又糊的 ONNX。换了引擎就必须重新实测。</summary>
    public static string RealEsrganEngineId => FindRealEsrgan2026() is not null ? "realesrgan2026" : "realesrgan";

    /// <summary>把对外的引擎名规整成实际身份键(探测 / 结论缓存 / 日志统一走这里)。</summary>
    public static string EngineId(string engine) => engine == "realesrgan" ? RealEsrganEngineId : engine;

    /// <summary>引擎的中文显示名(带新旧版区分,便于用户反馈时对号)。</summary>
    public static string EngineLabel(string engine) => engine switch
    {
        "realesrgan" or "realesrgan2026" => RealEsrganEngineId == "realesrgan2026" ? "Real-ESRGAN(新版引擎)" : "Real-ESRGAN",
        _ => "waifu2x",
    };

    public static string? FindU2NetModel()
    {
        var root = Path.Combine(EnginesDir, "rembg");
        if (Directory.Exists(root))
        {
            foreach (var f in Directory.EnumerateFiles(root, "u2net.onnx", SearchOption.AllDirectories))
                return f;
        }
        var direct = Path.Combine(EnginesDir, "u2net.onnx");
        return File.Exists(direct) ? direct : null;
    }

    /// <summary>在 engines/rembg 目录下(含 models 子目录)查找指定抠图模型文件。</summary>
    public static string? FindCutoutModel(string fileName)
    {
        var root = Path.Combine(EnginesDir, "rembg");
        if (Directory.Exists(root))
        {
            foreach (var f in Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories))
                return f;
        }
        var direct = Path.Combine(EnginesDir, fileName);
        return File.Exists(direct) ? direct : null;
    }

    public static bool CheckEngines(out string missing)
    {
        var list = new System.Collections.Generic.List<string>();
        if (FindWaifu2x() is null) list.Add("waifu2x");
        if (FindRealESRGAN() is null) list.Add("realesrgan");
        if (VideoService.FfmpegPath is null) list.Add("ffmpeg");
        if (VideoService.RifePath is null) list.Add("rife");
        // 抠图模型:检查默认使用的高精度模型(缺了它,默认抠图不可用)
        if (FindCutoutModel("birefnet-lite.onnx") is null) list.Add("rembg 模型(默认用 BiRefNet 高精度)");
        missing = string.Join(", ", list);
        return list.Count == 0;
    }

    /// <summary>本进程生成的临时文件(EXIF 旋转等),进程退出时统一清理,防止 temp 目录无限增长。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentBag<string> _tempFiles = new();

    /// <summary>注册一个待清理的临时文件。</summary>
    public static void RegisterTempFile(string path)
    {
        if (!string.IsNullOrEmpty(path)) _tempFiles.Add(path);
    }

    /// <summary>启动或退出时清理所有已注册临时文件。</summary>
    public static void CleanupTempFiles()
    {
        foreach (var f in _tempFiles)
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { }
        }
        _tempFiles.Clear();
    }

    /// <summary>若图片带 EXIF 方向标记(手机照片),返回【转正后】的临时 PNG 路径;否则原样返回入参路径。
    /// 用于预处理(降噪/超分)前标准化方向,保证标记坐标与处理结果同坐标系。
    /// 【已收敛到 ExifFix】此前这里只处理 6/8/3,方向 2/4/5/7(镜像/转置)会静默不处理 → 坐标错位;
    /// 而且为了读 EXIF 先 new Bitmap(input) 全解码一次、再 new 一次做变换 —— 现在只解码一次。</summary>
    public static string NormalizeExif(string input)
    {
        try
        {
            using var bmp = new System.Drawing.Bitmap(input);
            if (!AlhPro.Core.ExifOrientation.NeedsTransform(ExifFix.ReadOrientation(bmp))) return input;
            ExifFix.ApplyInPlace(bmp);   // 就地转正,并清掉 0x0112 标记(否则下游会再转一次 → 双重旋转)
            var outPath = Path.Combine(EngineService.TempRoot, $"imgup_exif_{Guid.NewGuid():N}.png");
            RegisterTempFile(outPath);   // 注册待清理
            bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
            return outPath;
        }
        catch { }
        return input;
    }

    /// <summary>解析引擎输出中的百分比。引擎用 "\r" 刷新进度行,故不能按行读,需逐块扫描。
    /// 注意:引擎输出形如 "25.00%",带两位小数。</summary>
    private static readonly Regex PctRegex = new(@"(\d+(?:\.\d+)?)\s*%", RegexOptions.Compiled);
    // 引擎启动/完成日志节流状态(同阶段 10 秒内只记首次,防"引擎启动中"刷屏)
    private static string _lastEngineStageLog = "";
    private static DateTime _lastEngineStartLog = DateTime.MinValue;
    // 引擎"无进展看门狗":时间戳已改为 RunAsync 每次调用私有(局部变量 lastOutTicks/lastFrameTicks,
    // 闭包捕获)——原全局静态已被并发任务"喂狗"导致看门狗失效,已废弃(无引用)。
    // (引擎崩溃=进程退出→RunAsync 抛异常→降级链接管,不依赖看门狗;看门狗兜底"引擎 hang 不退出的情况")
    // 分块处理已改为"一次目录批量启动引擎"(UnscaleTiledAsync),不再逐块启动,故不再需要单块平均耗时心跳字段。

    /// <summary>启动引擎子进程,实时读取输出并解析进度,支持取消(杀进程树)。返回引擎日志尾部。</summary>
    private static async Task<string> RunAsync(string exe, string args,
        IProgress<(int pct, string msg)>? progress, CancellationToken ct,
        string stage = "", int totalFrames = 0, string? watchDir = null,
        int watchBase = 0, int watchGlobalTotal = 0)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? ".",
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动引擎: " + exe);
        var startTime = DateTime.Now;   // 引擎耗时统计
        bool sawAnyOutput = false;   // 启动超时看门狗:是否已出现任何输出(引擎启动即产出 → 排除"启动即挂死")
        SafeRender.ApplyProcessPriority(p);   // 处理时降优先级,防整机卡
        App.ActiveProcesses.Register(p);   // 纳入"暂停=冻结"管理(冻结遍历整个注册表,含并发多路)
        // 诊断:记录引擎使用的设备编号(-g;日志一眼看出是在用 GPU 还是 CPU)
        var gMatch = System.Text.RegularExpressions.Regex.Match(args, @"-g\s+(-?\d+)");
        // 启动/完成日志节流:同阶段 10 秒内只记首次(补回层批等多次启停时日志不再滚动刷屏)
        bool logEngine = stage != _lastEngineStageLog || (DateTime.Now - _lastEngineStartLog).TotalSeconds > 10;
        if (logEngine)
        {
            _lastEngineStageLog = stage;
            _lastEngineStartLog = DateTime.Now;
            AppLogger.Info($"引擎启动:{Path.GetFileNameWithoutExtension(exe)}({stage}) 设备 -g {(gMatch.Success ? gMatch.Groups[1].Value : "?")}" +
                (gMatch.Success && gMatch.Groups[1].Value == "-1" ? "(CPU 软件计算)" : gMatch.Success ? "(GPU)" : ""));
        }

        // 引擎不输出百分比时(目录模式),轮询输出目录已生成帧数,像补帧那样逐帧报告
        using var watchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var log = new StringBuilder();
        var lockObj = new object();
        int maxPct = 0;
        bool killRequested = false;
        // 看门狗时间戳(每次 RunAsync 私有,不再用全局静态共享——否则多引擎并发时 A 的心跳会"喂狗"B,
        // B 真卡死看门狗也不判死,用户无限等)。闭包捕获,每个引擎进程独立哨兵。
        long lastOutTicks = DateTime.Now.Ticks;
        long lastFrameTicks = DateTime.Now.Ticks;
        var watchTask = (watchDir != null && totalFrames > 0 && stage.Length > 0)
            ? WatchDirProgressAsync(watchDir, stage, totalFrames, progress, watchCts.Token, watchBase, watchGlobalTotal,
                () => { lastOutTicks = DateTime.Now.Ticks; lastFrameTicks = DateTime.Now.Ticks; })   // 完成帧回调:刷新本引擎私有看门狗时间戳
            : Task.CompletedTask;
        // 引擎无进度输出时(部分模型/CPU 软算):每 4 秒若有变化就渐 +1(上限 98),避免进度条"空→满"跳变。
        // 【关键修复】watchDir(目录轮询)场景禁用空闲心跳:它会 10 秒内把进度虚推到 96~98%,
        // 之后真实帧/块进度(数值更小)被 Math.Max 卡住 → 进度条永远定格 98% 假装满——"进度条不会动"的根因。
        int lastIdlePct = -1;
        using var idleTimer = watchDir == null
            ? new System.Threading.Timer(_ =>
            {
                try
                {
                    lock (lockObj)
                    {
                        if (!p.HasExited && maxPct == lastIdlePct && maxPct < 98)
                        {
                            maxPct = Math.Min(98, maxPct + 1);
                            lastOutTicks = DateTime.Now.Ticks;
                            progress?.Report((maxPct, $"引擎处理中 {maxPct}%..."));
                        }
                        lastIdlePct = maxPct;
                    }
                }
                catch { }
            }, null, 4000, 3000)
            : null;

        void OnChunk(string chunk)
        {
            lock (lockObj)
            {
                sawAnyOutput = true;
                if (chunk.Length > 0) lastOutTicks = DateTime.Now.Ticks;
                log.Append(chunk);
                if (log.Length > 4096) log.Remove(0, log.Length - 4096); // 只保留尾部,防内存膨胀
                foreach (Match m in PctRegex.Matches(chunk))
                {
                    if (double.TryParse(m.Groups[1].Value, NumberStyles.Float,
                            CultureInfo.InvariantCulture, out var dv) &&
                        (int)Math.Round(dv) > maxPct)
                    {
                        maxPct = (int)Math.Round(dv);
                        progress?.Report((maxPct, $"引擎处理中 {maxPct}%..."));
                    }
                }
            }
        }

        var drainOut = DrainAsync(p.StandardOutput, OnChunk, ct);
        var drainErr = DrainAsync(p.StandardError, OnChunk, ct);

        // 无进展看门狗(分设备超时 + 实质停滞监控):
        // 1) 无输出超时:GPU 8 分钟无任何输出 → 驱动/引擎挂死;CPU 20 分钟无输出(CPU 慢,宽限);
        // 2) 实质停滞:引擎有帧级心跳但 10 分钟未完成任何一帧(CPU 逐帧爬但慢到不可接受)→ 强制终止,
        //    避免"半天不动一帧"让用户无限等待(实测:CPU 软解 1280×720 单帧可达 10 分钟级)。
        // 3) 启动超时(新增):引擎启动后 30 秒无任何输出 → 大概率立即挂死(如 Blackwell + 旧 ncnn:
        //    vkQueueSubmit 失败但进程不退出,原逻辑要等 8 分钟才降级!)。30 秒即杀降级,不再让用户白等。
        using var watchdog = new System.Threading.Timer(_ =>
        {
            try
            {
                lock (lockObj)
                {
                    if (killRequested || p.HasExited) return;
                    // 用户暂停=子进程已被 NtSuspendProcess 冻结,冻结态与僵死态外部无法区分
                    // (存活、零输出、不退出)→ 喂狗,否则暂停久了会被误判挂死而杀掉任务。
                    if (VideoService.IsPaused)
                    {
                        lastOutTicks = DateTime.Now.Ticks;
                        lastFrameTicks = DateTime.Now.Ticks;
                        return;
                    }
                    bool cpu = args.Contains("-g -1", StringComparison.Ordinal);
                    // 【识别核显】核显共享显存(报告值常高达8~16G)但实际很慢,不能按"≥6G=强卡"给1分钟看门狗
                    // (否则处理大帧>1分钟零输出会被误杀)。核显也按 CPU/慢机宽容。
                    bool igpu = false;
                    try
                    {
                        var gm = System.Text.RegularExpressions.Regex.Match(args, @"-g\s+(\-?\d+)");
                        if (gm.Success && int.TryParse(gm.Groups[1].Value, out var gi) && gi >= 0)
                            igpu = GpuInfo.IsIntegratedGPU(GpuInfo.GetEngineDeviceName(gi));
                    }
                    catch { }
                    double vram = 0;
                    try { vram = SafeRender.TotalVramGB; } catch { }
                    // 【按设备档次自适应看门狗】高分辨率/高倍率的大帧单帧处理可能接近或超过 1 分钟,
                    // 原强独显 1 分钟零输出/3 分钟无帧会误杀"慢但正常"。放宽:强独显 2/6 分钟,弱独显 4/8 分钟,CPU/核显 8/10 分钟。
                    long noOutLimitTicks, stallLimitTicks;
                    if (cpu || igpu) { noOutLimitTicks = TimeSpan.FromMinutes(8).Ticks; stallLimitTicks = TimeSpan.FromMinutes(10).Ticks; }
                    else if (vram >= 6) { noOutLimitTicks = TimeSpan.FromMinutes(2).Ticks; stallLimitTicks = TimeSpan.FromMinutes(6).Ticks; }
                    else { noOutLimitTicks = TimeSpan.FromMinutes(4).Ticks; stallLimitTicks = TimeSpan.FromMinutes(8).Ticks; }
                    long sinceOut = DateTime.Now.Ticks - lastOutTicks;
                    long sinceFrame = DateTime.Now.Ticks - lastFrameTicks;
                    // ① 启动超时:强独显 30 秒零输出即杀;CPU/核显(慢)放宽到 90 秒(慢机加载/编译着色器更久)
                    long startupLimitTicks = (cpu || igpu) ? TimeSpan.FromSeconds(90).Ticks : TimeSpan.FromSeconds(30).Ticks;
                    if (!sawAnyOutput && sinceOut > startupLimitTicks)
                    {
                        killRequested = true;
                        AppLogger.Warn($"看门狗:引擎 ({stage}) 启动 30 秒无任何输出(疑似驱动/引擎挂死,常见于 50 系+旧 ncnn)——强制终止降级");
                        try { p.Kill(entireProcessTree: true); } catch { }
                    }
                    // ② 无输出(连心跳都没有,已有输出后)
                    else if (sawAnyOutput && sinceOut > noOutLimitTicks)
                    {
                        killRequested = true;
                        AppLogger.Info($"看门狗:引擎 ({stage}) {(cpu ? "CPU" : "GPU")} {noOutLimitTicks / TimeSpan.TicksPerMinute} 分钟无输出(疑似驱动/引擎挂死),强制终止");
                        try { p.Kill(entireProcessTree: true); } catch { }
                    }
                    // ③ 有输出但 X 分钟未完成一帧:仅在"逐帧进度模式"(watchDir!=null,有帧完成回调刷新 lastFrameTicks)下才可靠;
                    // stdout 模式无帧回调,lastFrameTicks 停在启动值 → sinceFrame=总耗时,会误杀"正常慢速但持续出活"的作业,故跳过
                    else if (watchDir != null && sinceFrame > stallLimitTicks)
                    {
                        killRequested = true;
                        AppLogger.Info($"看门狗:引擎 ({stage}) {stallLimitTicks / TimeSpan.TicksPerMinute} 分钟未完成一帧(计算过慢或停滞),强制终止——建议改用 GPU/调低倍率/调小分辨率");
                        try { p.Kill(entireProcessTree: true); } catch { }
                    }
                }
            }
            catch { }
        }, null, 30000, 30000);   // 每 30 秒检查一次(原 60 秒,降级更及时)

        // 等待退出;取消时杀掉进程树
        string? killReason = null;
        while (!p.HasExited)
        {
            if (ct.IsCancellationRequested)
            {
                try { VideoService.ResumeActiveProcess(); p.Kill(entireProcessTree: true); } catch { /* 进程可能已退出 */ }
                break;
            }
            if (killRequested)
            {
                killReason = $"引擎无进展(已强制终止): {stage} — 建议改用 GPU 或降低倍率/分辨率后重试";
                break;
            }
            await Task.Delay(100).ConfigureAwait(false);
        }
        // 清理永远执行(即使强制终止/kill 也释放看门狗定时器与轮询任务,不留泄漏)
        await Task.WhenAll(drainOut, drainErr).ConfigureAwait(false);
        watchdog.Dispose();
        watchCts.Cancel();
        try { await watchTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        App.ActiveProcesses.Unregister(p.Id);
        if (killReason != null) throw new InvalidOperationException(killReason);

        string tail;
        lock (lockObj) { tail = log.ToString().Trim(); }

        if (ct.IsCancellationRequested)
            throw new OperationCanceledException("已取消");

        if (p.ExitCode != 0)
        {
            if (tail.Length > 600) tail = tail[^600..];
            throw new InvalidOperationException($"引擎处理失败 (exit {p.ExitCode}):\n{tail}");
        }
        // 诊断:记录引擎本次运行的耗时(秒),用于判断卡在哪个环节(与启动日志同节流:不刷屏)
        try
        {
            var elapsed = (DateTime.Now - startTime).TotalSeconds;
            if (logEngine)
                AppLogger.Info($"引擎完成:{Path.GetFileNameWithoutExtension(exe)}({stage}) 耗时 {elapsed:0.0}s");
        }
        catch { }
        return tail;
    }

    /// <summary>探测指定超分引擎能否用 GPU(-g 0)成功跑一张 320×240 图。
    /// 用途:视频处理开始前,若当前引擎在用户显卡上跑不通(不仅 RTX 50 系——
    /// AMD/Intel/老驱动等任何"该引擎不支持"的场景),提前提示换引擎,而不是处理中默默降级。
    /// 返回 false = GPU 不可用(建议换 waifu2x);异常/超时一律按 false 处理(不中断主流程)。
    /// 注意:仅探测(小图,毫秒级),不影响正常处理;结果不缓存(显卡/驱动随时可能变)。
    /// 【保持原行为】这个 3 参重载 = fullFrame:false,与改动前逐字一致(所有既有调用点都走它)。
    /// 需要"能堵住 Blackwell 静默空帧/黑帧"的强判据时,用 fullFrame:true 的重载(见下)。</summary>
    public static async Task<bool> IsEngineGpuUsableAsync(string engine, int gpuId, CancellationToken ct)
        => await IsEngineGpuUsableAsync(engine, gpuId, ct, fullFrame: false, model: null).ConfigureAwait(false);

    /// <summary>同上,但 fullFrame=true 时按【生产形态】探测:1080×1920 真帧尺寸 + 生产参数
    /// (-s 2 -n 0 -t 0 -j 1:1:1 + 真实模型)。这是"50 系该走 ncnn 还是 ONNX"的判据来源。
    /// 【为什么小图不够】真机证据(诊断包):320×240 甚至 1×1 探测都能通过,真实分辨率却静默输出
    /// 0KB 空帧/黑帧、【退出码还是 0】——于是旧探测判"可用",整段视频的黑帧一路进成片。
    /// 判据仍沿用 IsBlackPng(= FrameInspect.IsDefectiveFrame:整帧 ≥95% 近黑 或 任一 1/3 主条带 ≥95% 近黑),
    /// 因此"下 2/3 全黑、上 1/3 正常"这种带状坏帧也能被这一层拦住。</summary>
    public static async Task<bool> IsEngineGpuUsableAsync(string engine, int gpuId, CancellationToken ct, bool fullFrame, string? model)
        => await IsEngineGpuUsableAsync(engine, gpuId, ct, fullFrame, model, null).ConfigureAwait(false);

    /// <summary>同上,并把【失败形态】回传给 onFailure(只报最终决定性的那一次,不报每次重试)。
    /// 【为什么需要它】原返回 bool 把失败原因全丢了,调用方只能含糊说"崩溃/空帧/黑帧/超时"。
    /// 而"初始化即崩"与"出图但坏帧"是两件完全不同的事:前者在 Blackwell 上就是 NVIDIA 的
    /// cooperative-matrix 驱动缺陷(必须说清楚,否则用户会一直来找我们),后者属引擎并发/渲染
    /// (绝不能甩给驱动)。文案规则见 AlhPro.Core.ProbeDiagnosis(有单测守着这两条约束)。</summary>
    public static async Task<bool> IsEngineGpuUsableAsync(string engine, int gpuId, CancellationToken ct, bool fullFrame, string? model, Action<AlhPro.Core.ProbeFailureKind, string>? onFailure)
    {
        AlhPro.Core.ProbeFailureKind failKind = AlhPro.Core.ProbeFailureKind.None;
        string failDetail = "";
        // 重试链里以【最后一次】的形态为准 —— 那才是导致判"不可用"的原因
        void Note(AlhPro.Core.ProbeFailureKind k, string d) { failKind = k; failDetail = d; }
        try
        {
            string? exe = engine switch
            {
                "waifu2x" => FindWaifu2x(),
                "realesrgan" => FindRealESRGAN(),
                _ => null,
            };
            if (exe == null)
            {
                onFailure?.Invoke(AlhPro.Core.ProbeFailureKind.EngineMissing, engine);
                return false;
            }
            // 生成测试图:fullFrame=false 用 320×240(原 1×1 会假通过:部分引擎能跑 1×1,但在真实帧尺寸上因分块/显存/驱动崩);
            // fullFrame=true 用生产帧尺寸 1080×1920 —— 见下方 overload 的说明。
            var inPng = Path.Combine(EngineService.TempRoot, $"eng_probe_{Guid.NewGuid():N}.png");
            var outPng = Path.Combine(EngineService.TempRoot, $"eng_probe_out_{Guid.NewGuid():N}.png");
            try
            {
                if (fullFrame)
                {
                    // 生产帧尺寸(1080×1920 = 短竖屏视频原生帧):只有够大才复现"小图能过、真实分辨率静默出空帧/黑帧"。
                    // 三段内容刻意都不黑(上亮/中灰/下中亮):这样"某个 1/3 条带近黑"只可能来自引擎故障,而不是素材本身。
                    using (var bmp = new System.Drawing.Bitmap(1080, 1920))
                    {
                        using (var g = System.Drawing.Graphics.FromImage(bmp))
                        {
                            g.Clear(System.Drawing.Color.FromArgb(96, 128, 168));                       // 中 1/3:灰蓝
                            using var b1 = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(238, 236, 228));
                            g.FillRectangle(b1, 0, 0, 1080, 640);                                       // 上 1/3:亮
                            using var b2 = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(150, 158, 140));
                            g.FillRectangle(b2, 0, 1280, 1080, 640);                                    // 下 1/3:中亮
                        }
                        bmp.Save(inPng, System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
                else
                {
                    using (var bmp = new System.Drawing.Bitmap(320, 240))
                    {
                        using (var g = System.Drawing.Graphics.FromImage(bmp))
                        {
                            g.Clear(System.Drawing.Color.Red);
                            using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.White);
                            g.FillRectangle(brush, 0, 0, 160, 120);   // 有明暗变化,更接近真实帧
                        }
                        bmp.Save(inPng, System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
                // 引擎参数:fullFrame=false 统一 -s 2(2x 模型,与原口径逐字一致);
                // fullFrame=true 用与生产【逐字一致】的形态:真实模型 + -t 0 + 现行 -j(1:1:1)。
                string args;
                if (fullFrame && engine == "waifu2x")
                {
                    var modelDir = Path.Combine(Path.GetDirectoryName(exe)!, string.IsNullOrEmpty(model) ? "models-cunet" : model);
                    args = $"-i \"{inPng}\" -o \"{outPng}\" -s 2 -n 0 -t 0 -g {gpuId} -m \"{modelDir}\"{SafeRender.GetEngineThreadArgs()}";
                }
                else if (fullFrame)
                {
                    args = $"-i \"{inPng}\" -o \"{outPng}\" -s 2 -m models -n {model ?? "realesrgan-x4plus"} -t 0 -g {gpuId}{SafeRender.GetEngineThreadArgs()}";
                }
                else
                {
                    args = $"-i \"{inPng}\" -o \"{outPng}\" -s 2 -g {gpuId}";
                }
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? ".",
                };
                // 【有独显时不轻易掉 CPU】探测重试 3 次:快速失败(exit≠0/无输出/黑帧)多为瞬时抽风,退避后重试;
                // 超时(真 hang)不重试(重试只会再白等 15s);3 次全失败才判不可用。避免"一次驱动抽风就把 4060 判成没 GPU、整段掉 CPU"。
                const int MaxAttempts = 3;
                for (int attempt = 1; attempt <= MaxAttempts && !ct.IsCancellationRequested; attempt++)
                {
                    if (File.Exists(outPng)) { try { File.Delete(outPng); } catch { } }
                    using var p = Process.Start(psi);
                    if (p == null)
                    {
                        AppLogger.Warn($"[探测] 引擎 {engine} GPU(-g {gpuId})第 {attempt}/{MaxAttempts} 次启动失败(进程为空),退避后重试...");
                        Note(AlhPro.Core.ProbeFailureKind.StartupFailed, "进程为空(引擎未启动起来)");
                        if (attempt < MaxAttempts) { try { await Task.Delay(1500, ct).ConfigureAwait(false); } catch { } }
                        continue;
                    }
                    using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    // 探测超时:小图 15 秒(旧 5 秒对"冷启动慢的 ncnn 卡"会误报 hang,如 4060 首次加载 Vulkan 要 >5s);
                    // 【生产帧尺寸探测必须给足 60 秒】实测(4060,1080×1920,-s 2):waifu2x-cunet 1.6s、
                    // realesrgan-animevideov3 1.7s,但【realesrgan-x4plus 要 24.1s】—— 30 秒余量对重模型太紧,
                    // 慢卡上会把能用的设备误判"不可用"→ 永久推回 ONNX(假失败代价很大:5060 上 ONNX 落 CPU 是 8 秒/帧)。
                    // 真 hang 不会因超时变长而变慢:超时后直接返回 false,不重试。
                    int probeTimeoutSec = fullFrame ? 60 : 15;
                    waitCts.CancelAfter(TimeSpan.FromSeconds(probeTimeoutSec));
                    try
                    {
                        await p.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
                        // 【失败形态分类,决定对用户怎么说】
                        // 退出码非 0 = 初始化/运行期崩溃(0xC0000005 访问违例是驱动缺陷的典型特征);
                        // 退出码 0 但无产出/产出 0 字节 = 静默失败;产出非空但近黑 = 坏帧(另一类问题)。
                        if (p.ExitCode != 0)
                            Note(AlhPro.Core.ProbeFailureKind.CrashExitCode, $"exit=0x{p.ExitCode:X8}");
                        else if (!File.Exists(outPng))
                            Note(AlhPro.Core.ProbeFailureKind.NoOutput, "退出码 0 但无产出文件");
                        else if (new FileInfo(outPng).Length == 0)
                            Note(AlhPro.Core.ProbeFailureKind.EmptyOutput, "产出文件 0 字节");
                        else
                            Note(AlhPro.Core.ProbeFailureKind.DefectiveFrame, "产出非空,但判为缺陷帧(近黑/带状近黑)");
                        // 判定:退出码 0 且输出文件存在(引擎正常出图)
                        bool ok = p.ExitCode == 0 && File.Exists(outPng) && new FileInfo(outPng).Length > 0;
                        // 【黑帧自检】引擎输出存在但全黑(静默黑帧 bug,如旧 ncnn on 50系/AMD 驱动异常)→ 该设备视为不可用,
                        // 立即改用其它卡/ONNX;否则黑帧设备会被误判"可用",后续补帧/超分一路黑。
                        // 判据是 IsBlackPng = FrameInspect.IsDefectiveFrame(整帧近黑【或】任一 1/3 主条带近黑)——
                        // 带状黑("下 2/3 全黑、上 1/3 正常")也拦得住,不是只查整帧全黑。
                        if (ok) { try { if (IsBlackPng(outPng)) { ok = false; } } catch { } }
                        if (ok)
                        {
                            AppLogger.Info($"[探测] 引擎 {engine} GPU(-g {gpuId})可用(第 {attempt} 次," +
                                (fullFrame ? "生产帧尺寸 1080×1920 出图,非黑/非带状黑" : "320×240 小图出图,非黑") + ")");
                            return true;
                        }
                        AppLogger.Warn($"[探测] 引擎 {engine} GPU(-g {gpuId})第 {attempt}/{MaxAttempts} 次不可用(exit={p.ExitCode}/无输出/空帧/黑帧)" + (attempt < MaxAttempts ? ",退避后重试..." : "——将自动改用其它设备或 ONNX"));
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // 超时(真 hang):重试只会再白等,直接判不可用并杀进程
                        AppLogger.Warn($"[探测] 引擎 {engine} GPU(-g {gpuId}) {probeTimeoutSec} 秒无响应(疑似 hang)" +
                            (fullFrame ? "(探测用 1080×1920 生产帧尺寸:重模型本身也可能跑数十秒,本次按不可用保守处理)" : "") +
                            "——按不可用处理,已终止探测(不重试)");
                        try { p.Kill(entireProcessTree: true); } catch { }
                        onFailure?.Invoke(AlhPro.Core.ProbeFailureKind.Hang, $"{probeTimeoutSec} 秒无响应");
                        return false;
                    }
                    catch (OperationCanceledException)
                    {
                        try { p.Kill(entireProcessTree: true); } catch { }
                        throw;
                    }
                    if (attempt < MaxAttempts) { try { await Task.Delay(1500, ct).ConfigureAwait(false); } catch { } }
                }
                onFailure?.Invoke(failKind, failDetail);
                return false;
            }
            finally
            {
                try { File.Delete(inPng); } catch { }
                try { File.Delete(outPng); } catch { }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[探测] 引擎 {engine} GPU 探测异常(按不可用):{ex.Message}");
            onFailure?.Invoke(AlhPro.Core.ProbeFailureKind.StartupFailed, "探测过程异常:" + ex.Message);
            return false;
        }
    }

    /// <summary>探测 RIFE 补帧引擎能否用 GPU(-g)插出一帧(2 帧输入→1 帧中间帧)。
    /// 用途:补帧开始前实测(50 系/AMD/Intel 等:RIFE 可能静默 hang,不出图也不报错——
    /// 不预检用户只能白等 8 分钟看门狗)。失败返回 false,调用方改用 CPU。
    /// 注意:RIFE 单对模式(-0 -1 -o)而非目录模式(目录模式需 ≥2 帧输入,探测用单对最快)。</summary>
    public static async Task<bool> IsRifeGpuUsableAsync(string rifeExe, string model, int gpuId, CancellationToken ct)
        => await IsRifeGpuUsableAsync(rifeExe, model, gpuId, ct, null).ConfigureAwait(false);

    /// <summary>同上,并回传失败形态 —— 理由同 IsEngineGpuUsableAsync:调用方要按形态说话
    /// (初始化即崩 ≠ 出图但坏帧;前者在 Blackwell 上是 NVIDIA 驱动缺陷,后者不许甩给驱动)。</summary>
    public static async Task<bool> IsRifeGpuUsableAsync(string rifeExe, string model, int gpuId, CancellationToken ct, Action<AlhPro.Core.ProbeFailureKind, string>? onFailure)
    {
        AlhPro.Core.ProbeFailureKind failKind = AlhPro.Core.ProbeFailureKind.None;
        string failDetail = "";
        void Note(AlhPro.Core.ProbeFailureKind k, string d) { failKind = k; failDetail = d; }
        try
        {
            if (gpuId < 0 || rifeExe == null)
            {
                onFailure?.Invoke(AlhPro.Core.ProbeFailureKind.EngineMissing, "未指定补帧引擎或选了 CPU");
                return false;
            }
            var tmp = Path.Combine(EngineService.TempRoot, $"rife_probe_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tmp);
            var a = Path.Combine(tmp, "a.png");
            var b = Path.Combine(tmp, "b.png");
            var o = Path.Combine(tmp, "out.png");
            try
            {
                // 两帧:黑→白(有显著运动,引擎必然尝试插帧)。用 320×240 真实尺寸(原 64×64 太小,部分引擎在小尺寸能跑、真帧上崩)。
                using (var bmp = new System.Drawing.Bitmap(320, 240))
                {
                    using var g = System.Drawing.Graphics.FromImage(bmp);
                    g.Clear(System.Drawing.Color.Black);
                    bmp.Save(a, System.Drawing.Imaging.ImageFormat.Png);
                    g.Clear(System.Drawing.Color.White);
                    bmp.Save(b, System.Drawing.Imaging.ImageFormat.Png);
                }
                // 【放宽+重试】原 5 秒超时对首次运行(编译着色器)太紧,正常独显被误判→整段补帧被切 ONNX/CPU。
                // 改 10 秒;失败再重试一次(再失败才判不可用),避免瞬时抽风误判。
                var psi = new ProcessStartInfo
                {
                    FileName = rifeExe,
                    Arguments = $"-0 \"{a}\" -1 \"{b}\" -o \"{o}\" -m {model} -g {gpuId}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(rifeExe) ?? ".",
                };
                for (int attempt = 1; attempt <= 2; attempt++)
                {
                    try { if (File.Exists(o)) File.Delete(o); } catch { }
                    using var p = Process.Start(psi);
                    if (p == null) { onFailure?.Invoke(AlhPro.Core.ProbeFailureKind.StartupFailed, "进程为空"); return false; }
                    using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    waitCts.CancelAfter(TimeSpan.FromSeconds(10));
                    try
                    {
                        await p.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
                        // 失败形态分类(同超分探测):非零退出 = 崩溃;退出 0 无产出/空产出 = 静默失败
                        if (p.ExitCode != 0)
                            Note(AlhPro.Core.ProbeFailureKind.CrashExitCode, $"exit=0x{p.ExitCode:X8}");
                        else if (!File.Exists(o))
                            Note(AlhPro.Core.ProbeFailureKind.NoOutput, "退出码 0 但无产出");
                        else if (new FileInfo(o).Length == 0)
                            Note(AlhPro.Core.ProbeFailureKind.EmptyOutput, "产出 0 字节");
                        bool ok = p.ExitCode == 0 && File.Exists(o) && new FileInfo(o).Length > 0;
                        // 出帧 ≠ 出对帧:某些设备(真机:D3D12 转译层)exit 0 且出图,但插值结果是整帧红噪点。
                        // 探测输入是纯黑+纯白,正常引擎的中间帧必为无彩色灰阶 → 带色即损坏,按不可用处理。
                        if (ok && !ProbeOutputIsAchromatic(o))
                        {
                            AppLogger.Warn($"[探测] RIFE {model} GPU(-g {gpuId})出帧但颜色损坏(黑→白应插出灰帧,实测通道严重失衡)——按不可用处理");
                            Note(AlhPro.Core.ProbeFailureKind.DefectiveFrame, "出帧但颜色损坏(应插出灰阶却通道失衡)");
                            ok = false;
                        }
                        if (ok)
                        {
                            AppLogger.Info($"[探测] RIFE {model} GPU(-g {gpuId})可用(1~2 秒出帧)");
                            return true;
                        }
                        AppLogger.Warn($"[探测] RIFE {model} GPU(-g {gpuId})第 {attempt} 次不可用(exit={p.ExitCode}/无输出)" + (attempt < 2 ? ",重试一次..." : "——将自动改用 CPU/ONNX 补帧"));
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        AppLogger.Warn($"[探测] RIFE {model} GPU(-g {gpuId}) {10} 秒无响应(疑似 hang),按不可用处理");
                        try { p.Kill(entireProcessTree: true); } catch { }
                        onFailure?.Invoke(AlhPro.Core.ProbeFailureKind.Hang, "10 秒无响应");
                        return false;   // 超时=真 hang,不重试(重试只会再白等 10 秒);仅快速失败(非超时)才走重试
                    }
                    catch (OperationCanceledException)
                    {
                        try { p.Kill(entireProcessTree: true); } catch { }
                        throw;
                    }
                }
                onFailure?.Invoke(failKind, failDetail);
                return false;
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[探测] RIFE GPU 探测异常(按不可用):{ex.Message}");
            onFailure?.Invoke(AlhPro.Core.ProbeFailureKind.StartupFailed, "探测过程异常:" + ex.Message);
            return false;
        }
    }

    /// <summary>探测帧健全性:RIFE 探测的输入是纯黑+纯白两帧,任何正常引擎插出的中间帧都应是【无彩色】灰阶。
    /// 判定阈值在 <see cref="AlhPro.Core.DeviceRouting.IsAchromatic"/>(有单测);这里只负责把像素读成三通道均值。
    /// 读图自身异常时放行:宁可放过,不因探测代码的问题把可用设备判死。</summary>
    private static bool ProbeOutputIsAchromatic(string png)
    {
        try
        {
            using var bmp = new System.Drawing.Bitmap(png);
            var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
            var d = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            try
            {
                long sr = 0, sg = 0, sb = 0;
                int n = d.Width * d.Height;
                var row = new byte[d.Stride];
                for (int y = 0; y < d.Height; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(d.Scan0 + y * d.Stride, row, 0, d.Stride);
                    for (int x = 0; x < d.Width; x++)
                    {
                        sb += row[x * 3]; sg += row[x * 3 + 1]; sr += row[x * 3 + 2];
                    }
                }
                if (n == 0) return true;
                double mr = (double)sr / n, mg = (double)sg / n, mb = (double)sb / n;
                return AlhPro.Core.DeviceRouting.IsAchromatic(mr, mg, mb);
            }
            finally { bmp.UnlockBits(d); }
        }
        catch { return true; }
    }

    /// <summary>运行引擎命令;若命令使用 GPU(-g ≥0)且启动失败(如新显卡 RTX 50 系与 ncnn-vulkan
    /// 兼容问题 "invalid gpu device"),按降级链重算:当前 GPU → 其他 GPU(引擎自检过的,尊重用户
    /// 主动选的卡;绝不给"选了 GPU1 却只降 CPU"这种无视其他卡的处理)→ CPU。失败不再直接中断任务。
    /// CPU(-g -1)模式在这批引擎二进制上也有崩溃风险(实测 waifu2x 20250915 CPU 模式 exit -1073741819),
    /// 故 CPU 失败时反向再试 GPU 0,双路都死才报带指引的错误。</summary>
    private static async Task RunEngFallbackGpuAsync(string exe, string args,
        IProgress<(int pct, string msg)>? progress, CancellationToken ct,
        string stage = "", int totalFrames = 0, string? watchDir = null,
        int watchBase = 0, int watchGlobalTotal = 0)
    {
        bool usesGpu = System.Text.RegularExpressions.Regex.IsMatch(args, @"-g\s+[0-9]+");
        string runArgs = args;
        // CPU(-g -1)本会话已确认崩溃(exit -1073741819):直接改用 GPU 0 重算,不再尝试 CPU,避免反复崩溃拖慢/卡住
        if (!usesGpu && _ncnnCpuBroken)
        {
            runArgs = System.Text.RegularExpressions.Regex.Replace(args, @"-g\s+-?\d+", "-g 0");
            AppLogger.Info($"⚠ 本会话已确认 ncnn CPU(-g -1)模式崩溃,跳过 CPU,自动改用 GPU 0({GpuName(0)}) 重算");
            progress?.Report((0, $"⚠ ncnn CPU 模式崩溃,自动改用 GPU 0({GpuName(0)}) 重算..."));
        }
        try
        {
            await RunAsync(exe, runArgs, progress, ct, stage, totalFrames, watchDir, watchBase, watchGlobalTotal).ConfigureAwait(false);
            return;
        }
        catch (InvalidOperationException ex) when (!usesGpu)
        {
            // CPU(-g -1)初始模式:这批 ncnn 引擎的 CPU 模式有 bug(实测 waifu2x 20250915
            // -g -1 直接 exit -1073741819 内存访问违规)→ 反向试 GPU 0,再失败抛指引异常
            // 闩锁只在"引擎二进制自身崩溃"时置位:退出码落在 NTSTATUS 0xC0000000 段(表现为 ≤ -1073741824)
            // 才是访问违规/堆损坏一类崩溃。原先任何 InvalidOperationException 都置位——磁盘满、看门狗判死、
            // 命令行写错全算,之后整个会话把用户【主动选的 CPU】悄悄改写成 -g 0;在"GPU 才是坏件"的机器上
            // 正好反了,还把真实错误(如磁盘满)掩盖成"CPU 模式崩溃"。本次调用内仍照常重试 GPU 0。
            // 【修正阈值方向】崩溃码 0xC0000005 = -1073741819 > -1073741824,原先 `<=` 永不匹配 → 闩锁永不生效。
            // NTSTATUS 0xC0000000~0xFFFFFFFF 区段的崩溃码作为有符号 long 落在 [-1073741824, -1],因此用 `>= -1073741824L`(且为负)。
            if (long.TryParse(ExtractExit(ex.Message), out var exitCodeNum)
                && exitCodeNum < 0 && exitCodeNum >= -1073741824L)
                _ncnnCpuBroken = true;   // 记录本会话 CPU 崩溃,后续跳过 CPU
            string head = ex.Message.Split('\n')[0];
            if (head.Length > 90) head = head[..90];
            string gpu0Name = GpuName(0);
            AppLogger.Info($"⚠ CPU 模式不可用(引擎 CPU 路径不稳定: {head})——自动改用 GPU 0({gpu0Name}) 重算");
            progress?.Report((0, $"⚠ CPU 模式不可用,自动改用 GPU 0({gpu0Name}) 重算(更快更稳)..."));
            var gpuArgs = System.Text.RegularExpressions.Regex.Replace(args, @"-g\s+-?\d+", "-g 0");
            try
            {
                await RunAsync(exe, gpuArgs, progress, ct, stage, totalFrames, watchDir, watchBase, watchGlobalTotal).ConfigureAwait(false);
            }
            catch (InvalidOperationException gpuEx)
            {
                throw new InvalidOperationException(
                    $"超分引擎在 GPU 和 CPU 模式都不行(exit {ExtractExit(gpuEx.Message)}):\n" +
                    $"这多半是引擎版本与显卡不兼容(如 RTX 50 系 + 旧版 ncnn-vulkan,或引擎自身 CPU 模式 bug)。\n" +
                    $"建议:①换用 waifu2x 引擎(官方新版,兼容 50 系/Blackwell);" +
                    "②或到 https://github.com/nihui/waifu2x-ncnn-vulkan/releases 下载最新版替换 engines/waifu2x/ 下的文件。" +
                    $"\n--\n{gpuEx.Message}");
            }
        }
        catch (InvalidOperationException ex)
        {
            // GPU 初始模式:按原降级链 当前GPU → 其他GPU → CPU,CPU 也崩则反向试 GPU0
            string head = ex.Message.Split('\n')[0];
            if (head.Length > 90) head = head[..90];

            // 【失败反复重试 2~3 次再降级(不轻易掉)】同一条 GPU 命令(同参数/同设备)重跑多次:
            // 瞬时驱动抽风/编译着色器/显存短暂被占,重试能救回;全部失败才走下方降级链。
            // 只在 GPU 初始模式重试(_gpuRetryDepth 防递归),CPU 模式(-g -1)不重试(已知会崩)。
            const int GpuRetryTimes = 3;
            int retriedTimes = 0;
            if (Interlocked.CompareExchange(ref _gpuRetryDepth, 0, 0) == 0)
            {
                Interlocked.Increment(ref _gpuRetryDepth);
                try
                {
                    for (int r = 1; r <= GpuRetryTimes && !ct.IsCancellationRequested; r++)
                    {
                        AppLogger.Info($"⚠ GPU 引擎失败({head}),同设备重试 {r}/{GpuRetryTimes} 次,仍失败才降级...");
                        progress?.Report((0, $"⚠ GPU 引擎失败,重试 {r}/{GpuRetryTimes} 次(仍失败才降级)..."));
                        try
                        {
                            await RunAsync(exe, args, progress, ct, stage, totalFrames, watchDir, watchBase, watchGlobalTotal).ConfigureAwait(false);
                            return;
                        }
                        catch (InvalidOperationException retryEx)
                        {
                            string head2 = retryEx.Message.Split('\n')[0];
                            if (head2.Length > 90) head2 = head2[..90];
                            head = head2;   // 用最新错误走下方降级
                            retriedTimes = r;
                            AppLogger.Warn($"⚠ GPU 重试 {r}/{GpuRetryTimes} 仍失败({head2})" + (r < GpuRetryTimes ? ",继续重试..." : ",走降级链(不自动转CPU)"));
                            if (r < GpuRetryTimes) { try { await Task.Delay(1500, ct).ConfigureAwait(false); } catch { } }
                        }
                    }
                }
                finally { Interlocked.Decrement(ref _gpuRetryDepth); }   // 归还深度:下一次任务/下一批重新获得完整重试次数
                AppLogger.Warn($"⚠ GPU 已重试 {retriedTimes} 次全部失败({head}),按降级链处理(备用GPU→报错,不自动转CPU)");
            }
            else
            {
                AppLogger.Warn($"⚠ GPU 二次失败({head}),不再重试,走降级链(备用GPU→报错,不自动转CPU)");
            }

            // 【OOM 减半分块】GPU 显存不足且当前 -t > 64:先把分块减半在 GPU 上重试(显存降到约 1/4,能留在 GPU 上跑),
            // 而非直接落 CPU(慢得多)。多次 OOM 再走下方 其他GPU→CPU 降级链。
            if (LooksLikeOom(ex))
            {
                var tMatch = System.Text.RegularExpressions.Regex.Match(args, @"-t\s+(\d+)");
                if (tMatch.Success && int.TryParse(tMatch.Groups[1].Value, out var tCur) && tCur > 64)
                {
                    int tHalved = Math.Max(64, tCur / 2);
                    var oomArgs = System.Text.RegularExpressions.Regex.Replace(args, @"-t\s+\d+", $"-t {tHalved}");
                    AppLogger.Warn($"⚠ 显存不足,分块 {tCur}→{tHalved} 在 GPU 上重试(不是卡死,是自动降分块)...");
                    progress?.Report((0, $"⚠ 显存不足,自动降低分块 {tCur}→{tHalved} 重试(更快更稳)..."));
                    try
                    {
                        await RunAsync(exe, oomArgs, progress, ct, stage, totalFrames, watchDir, watchBase, watchGlobalTotal).ConfigureAwait(false);
                        return;
                    }
                    catch (InvalidOperationException oomEx)
                    {
                        AppLogger.Info($"⚠ 降低分块后仍显存不足({oomEx.Message.Split('\n')[0]}),继续降级...");
                    }
                }
            }

            int curGpu = 0;
            try
            {
                var m = System.Text.RegularExpressions.Regex.Match(args, @"-g\s+(\-?\d+)");
                if (m.Success) curGpu = int.Parse(m.Groups[1].Value);
            }
            catch { }

            // ① 其他 GPU(多卡机:独显故障→核显兜底)
            var altGpu = TryGetAlternateGpu(curGpu);
            if (altGpu.HasValue)
            {
                string altName = GpuName(altGpu.Value);
                AppLogger.Info($"⚠ 降级:GPU 引擎失败({head}),改用 GPU {altGpu.Value}({altName}) 重算...");
                progress?.Report((0, $"⚠ GPU 引擎失败({head}),改用 GPU {altGpu.Value}({altName}) 重算..."));
                var altArgs = System.Text.RegularExpressions.Regex.Replace(args, @"-g\s+-?\d+", $"-g {altGpu.Value}");
                try
                {
                    await RunAsync(exe, altArgs, progress, ct, stage, totalFrames, watchDir, watchBase, watchGlobalTotal).ConfigureAwait(false);
                    return;
                }
                catch (InvalidOperationException ex2)
                {
                    string head2 = ex2.Message.Split('\n')[0];
                    if (head2.Length > 90) head2 = head2[..90];
                    AppLogger.Info($"⚠ GPU {altGpu.Value}({altName}) 也失败({head2}),继续降级 CPU 重算");
                }
            }

            // ② 【原则 A:任何情况不自动转 CPU】当前及其它 GPU 都失败 → 直接报错给可行建议,而非默默跑慢速 CPU。
            // 超分/补帧在 CPU 上慢到不可接受;只有用户在设置里【手动选 CPU】才走 CPU(见上方 !usesGpu 分支,那里保留)。
            throw new InvalidOperationException(
                $"超分引擎在当前及其它 GPU 上均失败(exit {ExtractExit(ex.Message)}):\n" +
                $"这多半是引擎与显卡/驱动不兼容(如 RTX 50 系 + 旧版 ncnn-vulkan 的已知崩溃)。\n" +
                $"建议:①换用 waifu2x 引擎(官方新版支持 50 系/Blackwell);②更新 NVIDIA 显卡驱动;" +
                $"③或到 https://github.com/nihui/waifu2x-ncnn-vulkan/releases 下载最新版替换 engines/waifu2x/ 下的文件。" +
                $"\n(已按「不自动转 CPU」设置停止,避免慢速超分;确需 CPU 请在设置中手动选择)\n--\n{ex.Message}");
        }
    }

    private static string ExtractExit(string msg)
    {
        var m = System.Text.RegularExpressions.Regex.Match(msg, @"exit (-?\d+)");
        return m.Success ? m.Groups[1].Value : "?";
    }

    /// <summary>判断引擎失败是否为显存不足(OOM):vkAllocateMemory / out of memory / vk:: / memory 等关键字。
    /// 用于在掉 CPU 之前先"减半分块"留在 GPU 上重试。</summary>
    private static bool LooksLikeOom(Exception ex)
    {
        var s = ex.Message ?? "";
        return s.Contains("vkAllocateMemory", StringComparison.OrdinalIgnoreCase)
            || s.Contains("out of memory", StringComparison.OrdinalIgnoreCase)
            || s.Contains("vk::", StringComparison.OrdinalIgnoreCase)
            || s.Contains("memory insufficient", StringComparison.OrdinalIgnoreCase)
            || s.Contains("out of device memory", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>GPU 显示名(降级日志用):从 VulkanCheck 枚举取;取不到回退 "GPU {id}"。</summary>
    private static string GpuName(int id)
    {
        try
        {
            foreach (var (devId, name) in VulkanCheck.Devices)
                if (devId == id && !string.IsNullOrWhiteSpace(name)) return name;
        }
        catch { }
        return $"GPU {id}";
    }

    /// <summary>取"当前 GPU 之外"的备用 GPU 编号(引擎实测枚举到的 Vulkan 设备;多卡机可用)。
    /// 尊重用户主动选择:当前用 GPU1 → 备用为 GPU0;当前 GPU0 → 备用为其余卡(优先编号小的非当前卡)。
    /// 无第二张卡(单卡/核显未启用)返回 null → 调用方直接降级 CPU。</summary>
    private static int? TryGetAlternateGpu(int currentGpu)
    {
        try
        {
            var devs = VulkanCheck.Devices;
            if (devs.Count < 2) return null;   // 只有一张卡,无卡可降
            foreach (var (id, _) in devs)
                if (id != currentGpu) return id;   // 返回第一张"不是当前"的(通常即另一张独显/核显)
        }
        catch { }
        return null;
    }

    /// <summary>层批"任意 t 插值"(M2):对一批帧对同时做 0.5 插值——把各对的 (a_i,b_i) 平铺成目录序列,
    /// 一次 RIFE 目录模式跑完,再提取每对的中间帧(丢弃跨界对(B_i,A_{i+1})的产物)。
    /// 返回中间帧路径列表(顺序=pairs)。调用方按二叉树逐层递归调用本方法 = 每层一次引擎调用。
    /// 注意:目录模式必须带 -f 帧名模式,否则引擎用默认 %08d 命名,frame_4i-2 提取永远落空
    /// (曾致"全部中间帧兜底为左端点 = 输出全是关键帧副本/没有补帧")。</summary>
    public static async Task<List<string>> InterpLayerBatchAsync(string rifeExe,
        IEnumerable<(string a, string b)> pairs, string workDir, int gpuId, CancellationToken ct,
        string model = null, bool tta = false, double? timestep = null,
        IProgress<(int pct, string msg)>? progress = null, string? watchStage = null)
    {
        var pairList = new List<(string a, string b)>(pairs);
        if (pairList.Count == 0) return new List<string>();
        var inDir = Path.Combine(workDir, "layer_in");
        var outDir = Path.Combine(workDir, "layer_out");
        Directory.CreateDirectory(inDir);
        Directory.CreateDirectory(outDir);
        int n = 0;
        foreach (var (a, b) in pairList)
        {
            File.Copy(a, Path.Combine(inDir, $"frame_{++n:D6}.png"), true);
            File.Copy(b, Path.Combine(inDir, $"frame_{++n:D6}.png"), true);
        }
        var ttaArgs = tta ? " -x -z" : "";
        var modelArgs = !string.IsNullOrEmpty(model) ? $" -m {model}" : "";
        var timeArgs = timestep.HasValue ? $" -s {timestep.Value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}" : "";
        var args = $"-i \"{inDir}\" -o \"{outDir}\" -f \"frame_%06d.png\"{modelArgs}{timeArgs} -g {gpuId}{ttaArgs}{SafeRender.GetEngineThreadArgs()}";
        // 层批进度:watchStage 非空时轮询 outDir 帧数(逐帧报告"按源时间轴插帧 第 N 帧")
        string watchStageNow = watchStage ?? "";
        try { await RunAsync(rifeExe, args, progress, ct, watchStageNow, 0, watchStage != null ? outDir : null).ConfigureAwait(false); }
        catch (InvalidOperationException ex) when (gpuId >= 0)
        {
            // 「补帧绝不落 CPU」:原先这里用 -g -1 重跑 ncnn-CPU,直接违反该约定,而且 CPU 补帧慢到
            // 用户以为卡死(看门狗判死抛的 EngineStallException 也派生自 InvalidOperationException,
            // 同样会被这个 catch 捞去走 CPU)。改为按既定降级链换一张卡再试;没有第二张卡就把异常抛回
            // 调用方,由上层接 ONNX(DirectML) 或明确报错——绝不静默降级到 CPU。
            int? altGpu = TryGetAlternateGpu(gpuId);
            if (altGpu == null) throw;
            AppLogger.Info($"降级:任意 t 层批 GPU{gpuId} 失败({ex.Message.Split('\n')[0]}),改用 GPU{altGpu}(不落 CPU)");
            await RunAsync(rifeExe, System.Text.RegularExpressions.Regex.Replace(args, @"-g\s+-?\d+", $"-g {altGpu}"),
                progress, ct, watchStageNow, 0, watchStage != null ? outDir : null).ConfigureAwait(false);
        }
        // 输出序列:out[0]=a1, out[1]=mid1, out[2]=b1, out[3]=mid(b1,a2) 丢弃, out[4]=a2, out[5]=mid2 ...
        // mid_i 的 1-based 文件序号 = 4i-2(0-based 索引 4i-3)
        var mids = new List<string>();
        for (int i = 1; i <= pairList.Count; i++)
        {
            var f = Path.Combine(outDir, $"frame_{4 * i - 2:D6}.png");
            if (File.Exists(f)) mids.Add(f);
            else mids.Add(pairList[i - 1].a);   // 缺失兜底:用前帧(宁可重复不可空)
        }
        return mids;
    }

    private static async Task DrainAsync(StreamReader reader, Action<string> onChunk, CancellationToken ct)
    {
        var buf = new char[8192];
        try
        {
            while (true)
            {
                int n = await reader.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false);
                if (n <= 0) break;
                onChunk(new string(buf, 0, n));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* 读取失败不影响主流程 */ }
    }

    /// <summary>轮询输出目录已生成的帧数,逐帧报告"超分 第 N 帧 / 共 M 帧"(目录模式引擎不输出百分比)。
    /// baseFrames=本批起始的全局已处理帧数,globalTotal=全局总帧数:百分比按全局算,预计时间才准。
    /// onFrameDone=完成一帧回调(刷新本引擎私有看门狗时间戳,不刷全局——防并发"喂狗")。</summary>
    private static async Task WatchDirProgressAsync(string dir, string stage, int totalFrames,
        IProgress<(int pct, string msg)>? progress, CancellationToken ct,
        int baseFrames = 0, int globalTotal = 0, System.Action? onFrameDone = null)
    {
        int lastCount = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                int count = Directory.Exists(dir) ? EnumerateImageFiles(dir).Count() : 0;
                if (count > lastCount)
                {
                    lastCount = count;
                    onFrameDone?.Invoke();   // 实质完成帧:刷新本引擎私有看门狗时间戳(不再刷全局,防并发喂狗)
                    int done = baseFrames + count;
                    int gt = globalTotal > 0 ? globalTotal : totalFrames;
                    int pct = stage == "超分"
                        ? Math.Clamp(45 + done * 45 / Math.Max(1, gt), 45, 90)
                        : Math.Clamp(done * 90 / Math.Max(1, gt), 1, 90);
                    progress?.Report((pct, $"{stage} 第 {Math.Min(done, gt)} 帧 / 共 {gt} 帧"));
                }
            }
            catch { /* 目录尚未就绪等瞬时错误忽略 */ }
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
    }

    /// <summary>放大单张图。</summary>
    /// <param name="engine">引擎:waifu2x | realesrgan。</param>
    /// <param name="model">模型:waifu2x 为模型目录名,realesrgan 为模型名。</param>
    /// <param name="gpuId">GPU 编号;-1 = CPU。</param>
    public static async Task<string> UpscaleAsync(
        string input, string output, string engine, string model,
        double scale, int noise, int gpuId, bool tta,
        IProgress<(int pct, string msg)>? progress = null,
        CancellationToken ct = default,
        int tileSize = 0,
        bool allowTiling = true,
        bool upscaleShrink1x = false,
        float jpgQuality = 0.92f, int pngCompress = 6)
    {
        if (scale <= 0 || scale > 32)
            throw new ArgumentOutOfRangeException(nameof(scale), "放大倍数必须在 0~32 之间");
        tileSize = SafeRender.ResolveTile(tileSize);   // 未显式指定时按"安全渲染"墙自适应
        // 中文路径 → 8.3 短路径(引擎按 ANSI/GBK 解析参数,中文路径会 Illegal byte sequence)
        input = AudioService.FfmpegSafePath(input);
        output = AudioService.FfmpegSafePath(output);
        // 调用引擎前清理旧输出(含引擎可能改名的 output.png),
        // 避免上次残留干扰输出收拢判断
        try { if (File.Exists(output)) File.Delete(output); } catch { }
        try { if (File.Exists(output + ".png")) File.Delete(output + ".png"); } catch { }

        // 1x 超分(放大后缩回):内部先按"可用上限倍率"超分,再把结果精确缩回【源图尺寸】,画质比直接 1x 更好。
        // 注意:照片模型 realesrgan-x4plus 只有 4x 权重(-s 2 会拿 4x 模型硬缩=模糊/伪影),
        // 故 realesrgan 的 1x 超分中间倍率用 4x(4x→缩 0.25=原尺寸);waifu2x 用 2x。
        // 【修"静默错尺寸"】原条件写成 `upscaleShrink1x && scale <= 1.001`,只在"调用方传 1"时成立;
        // 而图片页现在传的是 2(EngineScaleOf:1x超分也要真 2x 放大再缩回)——条件恒假,于是 ncnn 路径的
        // 1x 超分【直接输出了 2x 尺寸】,界面与预设却都写着"输出仍 1x",全程不报错。
        // 改为:只要 upscaleShrink1x 为真,就按引擎可用上限放大,再精确缩回源尺寸(不再依赖调用方传 1)。
        if (upscaleShrink1x)
        {
            int upper = engine == "realesrgan" ? 4 : 2;
            int useScale = scale > 1.001 ? Math.Max(upper, (int)Math.Round(scale)) : upper;
            int srcW = 0, srcH = 0;
            try { using var probe = new System.Drawing.Bitmap(input); srcW = probe.Width; srcH = probe.Height; } catch { }
            var tmpUp = output + ".tmp1x.png";
            try
            {
                await UpscaleAsync(input, tmpUp, engine, model, useScale, noise, gpuId, tta,
                    progress, ct, tileSize, allowTiling).ConfigureAwait(false);
                if (srcW > 0 && srcH > 0)
                    await Task.Run(() => ResizeImageTo(tmpUp, output, srcW, srcH), ct).ConfigureAwait(false);
                else
                    await Task.Run(() => ResizeImage(tmpUp, output, 1.0 / useScale), ct).ConfigureAwait(false);
                return output;
            }
            finally
            {
                try { File.Delete(tmpUp); } catch { /* 清理失败忽略 */ }
            }
        }

        // 大图分块决策:超过引擎安全块尺寸 → 手动带重叠分块,保证每块"单块整处理、无内部子块接缝"。
        // (引擎内部 -t 分块/自动 tile 会在真实照片上切出"方正拼贴"接缝;App 手动分块 + overlap 羽化 + 单块 -t 0 才无痕)
        // 【提速优化】分块阈值用"安全块×3"(而非安全块):每块单独启动引擎开销大(实测 2000×2000 分块≈36.7s ,
        // 整图直跑≈25.9s,分块反而慢 40%)。能整图直跑(显存够)的图就不分块,省去反复启动引擎。
        // 实测 4060 8GB 整图直跑到 4000×4000 仍正常;阈值 = tileSize(≈1280)×3 = 3840,留足安全余量。
        if (allowTiling && scale > 1.001)
        {
            int iw = 0, ih = 0;
            try { using (var probe = new System.Drawing.Bitmap(input)) { iw = probe.Width; ih = probe.Height; } }
            catch { /* 探不到尺寸就走单张路径,由引擎报错 */ }
            int safeWholeTile = Math.Max(tileSize * 3, tileSize);   // 整图直跑安全上限(能整跑就不分块,省启动开销)
            if (iw > safeWholeTile || ih > safeWholeTile)
                // 分块拼接(SetPixel 羽化 + 合成 + 保存)很吃 CPU,放后台线程,避免卡 UI
                return await Task.Run(() => UpscaleTiledAsync(input, output, engine, model,
                    scale, noise, gpuId, tta, progress, ct, tileSize)).ConfigureAwait(false);
        }

        if (engine == "waifu2x")
        {
            var exe = FindWaifu2x() ?? throw new FileNotFoundException("未找到 waifu2x 引擎");
            var exeDir = Path.GetDirectoryName(exe)!;
            var modelDir = Path.Combine(exeDir, model);
            if (!Directory.Exists(modelDir))
                throw new FileNotFoundException($"缺少 waifu2x 模型:{model}。");
            // waifu2x 只支持 2 的幂倍数(2/4/8...);非 2 的幂(如 3x/1.5x)用更高倍数放大后再缩回,画质更好(不吞画质)
            var engineScale = CeilPowerOfTwo(scale);
            // 1x:引擎 -s 1 会段错误崩溃,不再直连 -s 1。不降噪直接复制原图;降噪则用 2x 降噪模型处理后高保真缩回 1x
            if (engineScale == 1)
            {
                if (noise < 0)
                {
                    File.Copy(input, output, overwrite: true);
                    return output;
                }
                engineScale = 2;
            }
            // -m 用相对引擎目录的路径(部分引擎会把传入路径再次拼接到 exe 目录,绝对路径会出错)
            var modelArg = Path.GetRelativePath(exeDir, modelDir);
            var args = $"-i \"{input}\" -o \"{output}\" -s {engineScale} -n {noise} " +
                $"-t 0 -g {gpuId} -m \"{modelArg}\"{SafeRender.GetEngineThreadArgs()}";
            if (tta) args += " -x";
            progress?.Report((0, "启动 waifu2x 引擎..."));
            // 诊断:记录实际使用的引擎与设备编号(-g;日志一看便知是在用 GPU 还是 CPU)
            AppLogger.Info($"引擎 {engine}/{model} 启动:设备 -g {gpuId}{(gpuId < 0 ? "(CPU 软件计算)" : "(GPU)")},tile {tileSize}");
            await RunEngFallbackGpuAsync(exe, args, progress, ct).ConfigureAwait(false);
            EnsureFinalOutput(output, jpgQuality, pngCompress);
            if (Math.Abs(engineScale - scale) > 0.001)
            {
                progress?.Report((96, $"输出 {scale:0.##}x(引擎 {engineScale}x 放大后精确调整)..."));
                await Task.Run(() => ResizeImage(output, output, scale / engineScale), ct)
                    .ConfigureAwait(false);
            }
            return output;
        }
        if (engine == "realcugan")
        {
            // realcugan 已整体移除(许可不明,见 THIRD_PARTY_NOTICES):兜底为 waifu2x
            throw new InvalidOperationException("Real-CUGAN 已移除(许可不明),请改用 waifu2x 或 Real-ESRGAN");
        }
        else
        {
            var exe = FindRealESRGAN() ?? throw new FileNotFoundException("未找到 Real-ESRGAN 引擎");
            // realesrgan 只支持 2/3/4 倍(无 1x 权重):1x 直接复制原图
            if (scale <= 1.001)
            {
                File.Copy(input, output, overwrite: true);
                return output;
            }
            // realesrgan 模型权重基本只有 2x/4x(x4plus 仅 4x):非原生倍率(3x/1.5x)与 1x 超分
            // 统一用 4x 放大后高保真缩回(用户指令:3x=4x 处理完缩到 3x;比 -s 2/-s 3 硬缩更清晰、不依赖缺失权重)
            int engineScale = 4;
            var args = $"-i \"{input}\" -o \"{output}\" -s {engineScale} -n {model} " +
                $"-t 0 -g {gpuId}{SafeRender.GetEngineThreadArgs()}";
            if (tta) args += " -x";
            progress?.Report((0, "启动 Real-ESRGAN 引擎..."));
            await RunEngFallbackGpuAsync(exe, args, progress, ct).ConfigureAwait(false);
            EnsureFinalOutput(output, jpgQuality, pngCompress);
            if (Math.Abs(engineScale - scale) > 0.001)
            {
                progress?.Report((96, $"输出 {scale:0.##}x(引擎 {engineScale}x 放大后精确调整)..."));
                await Task.Run(() => ResizeImage(output, output, scale / engineScale), ct)
                    .ConfigureAwait(false);
            }
            return output;
        }
    }

    /// <summary>逐块单文件超分:一块(≤tile)用"单文件 + -t 0"整块处理(引擎不内部切块→无接缝、无黑帧)。
    /// 关键:引擎"目录批量 + -t tileSize"会 vkQueueSubmit 失败→全黑;引擎内部 tiling(-t 带数值)也出接缝;
    /// 实测"单文件 + -t 0"整块直算最干净(无接缝、无黑帧),故逐块各自调用,由 App 羽化拼接。</summary>
    private static async Task UpOneTileAsync(string input, string output, string engine, string model,
        double scale, int noise, int gpuId, bool tta,
        IProgress<(int pct, string msg)>? progress, CancellationToken ct, int tileSize)
    {
        tileSize = SafeRender.ResolveTile(tileSize);
        if (engine == "waifu2x")
        {
            var exe = FindWaifu2x() ?? throw new FileNotFoundException("未找到 waifu2x 引擎");
            var exeDir = Path.GetDirectoryName(exe)!;
            var modelDir = Path.Combine(exeDir, model);
            if (!Directory.Exists(modelDir))
                throw new FileNotFoundException($"缺少 waifu2x 模型:{model}。");
            int engineScale = CeilPowerOfTwo(scale);
            var modelArg = Path.GetRelativePath(exeDir, modelDir);
            var args = $"-i \"{input}\" -o \"{output}\" -s {engineScale} -n {noise} " +
                $"-t 0 -g {gpuId} -m \"{modelArg}\"{SafeRender.GetEngineThreadArgs()}";
            if (tta) args += " -x";
            await RunEngFallbackGpuAsync(exe, args, progress, ct).ConfigureAwait(false);
            EnsureFinalOutput(output);
            if (Math.Abs(engineScale - scale) > 0.001)
                await Task.Run(() => ResizeImage(output, output, scale / engineScale), ct).ConfigureAwait(false);
        }
        else if (engine == "realcugan")
        {
            // realcugan 已整体移除(许可不明,见 THIRD_PARTY_NOTICES):兜底为 waifu2x
            throw new InvalidOperationException("Real-CUGAN 已移除(许可不明),请改用 waifu2x 或 Real-ESRGAN");
        }
        else
        {
            var exe = FindRealESRGAN() ?? throw new FileNotFoundException("未找到 Real-ESRGAN 引擎");
            // 与单张路径一致:realesrgan 权重基本只有 2x/4x,非原生倍率统一 4x 后高保真缩回
            int engineScale = 4;
            // -m 显式模型目录(models),-n 模型名(=realesrgan-x4plus)——显式写全,不依赖引擎默认/工作目录
            var args = $"-i \"{input}\" -o \"{output}\" -s {engineScale} -m models -n {model} " +
                $"-t 0 -g {gpuId}{SafeRender.GetEngineThreadArgs()}";
            // 实测:realesrgan(2022 版)加 -x(TTA)会卡死(120秒无输出,引擎兼容问题)——禁用,仅 waifu2x 新版支持 TTA;
            // 50 系适配升级新版引擎后如支持再放开。
            await RunEngFallbackGpuAsync(exe, args, progress, ct).ConfigureAwait(false);
            EnsureFinalOutput(output);
            if (Math.Abs(engineScale - scale) > 0.001)
                await Task.Run(() => ResizeImage(output, output, scale / engineScale), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 大图重叠分块超分:把超过 tile 的大图切成带 overlap 重叠的小块,一次引擎启动批量处理全部块,
    /// 再按"左/上边缘淡入"的羽化权重交叉融合,消除引擎内部 tiling 的"一块一块"拼接接缝。
    /// </summary>
    private static async Task<string> UpscaleTiledAsync(
        string input, string output, string engine, string model,
        double scale, int noise, int gpuId, bool tta,
        IProgress<(int pct, string msg)>? progress, CancellationToken ct,
        int tileSize)
    {
        const int overlap = 256;   // 相邻块重叠像素(越大过渡越平滑;真实照片纹理/渐变对块边界极敏感,加大到 256 让每块有更充足共享上下文,几乎无痕)
        var tmpDir = Path.Combine(EngineService.TempRoot, $"imgup_tiles_{Guid.NewGuid():N}");
        var inDir = Path.Combine(tmpDir, "in");
        var outDir = Path.Combine(tmpDir, "out");
        Directory.CreateDirectory(inDir);
        Directory.CreateDirectory(outDir);

        int srcW, srcH;
        using (var src = new System.Drawing.Bitmap(input)) { srcW = src.Width; srcH = src.Height; }

        // 【尺寸/内存保护】超大图避免最终拼整图时 OOM:GDI+ 32bpp canvas = 4 字节/像素,再加上百块 PNG。
        // 超出安全上限直接报清晰错误并提示降倍率,而不是处理几十分钟后崩在拼图这一步(用户以为卡死/白跑)。
        int outWg = (int)Math.Round(srcW * scale);
        int outHg = (int)Math.Round(srcH * scale);
        const int MaxDim = 32768;
        const long MaxPx = 160_000_000L;   // 约 1.6 亿像素 ≈ 640MB 32bpp canvas,给 PNG/内存留余量
        long outPx = (long)outWg * outHg;
        if (outWg > MaxDim || outHg > MaxDim || outPx > MaxPx)
            throw new InvalidOperationException(
                $"图片过大:缩放后 {outWg}×{outHg} = {outPx:N0} 像素,超出安全上限(单边≤{MaxDim},总像素≤{MaxPx:N0})。" +
                "请降低缩放倍率,或改用更大的源图处理。");

        // 网格:stride = tile - overlap;末尾不足 tile 的块自动收窄
        int stride = Math.Max(tileSize - overlap, 32);
        var xs = new System.Collections.Generic.List<int>();
        var ys = new System.Collections.Generic.List<int>();
        for (int x = 0; x < srcW; x += stride) xs.Add(x);
        for (int y = 0; y < srcH; y += stride) ys.Add(y);
        int cols = xs.Count, rows = ys.Count;
        int totalTiles = cols * rows;

        progress?.Report((2, $"大图分块超分:{cols}×{rows}={totalTiles} 块(带 {overlap}px 重叠平滑拼接,消除接缝)..."));

        // 1) 切块并按行_列顺序命名,供引擎目录模式一次处理
        using (var s = new System.Drawing.Bitmap(input))
        {
            int idx = 0;
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    int x0 = xs[c], y0 = ys[r];
                    int tw = Math.Min(tileSize, srcW - x0);
                    int th = Math.Min(tileSize, srcH - y0);
                    using var cropped = s.Clone(new System.Drawing.Rectangle(x0, y0, tw, th), s.PixelFormat);
                    cropped.Save(Path.Combine(inDir, $"tile_{idx++:D4}.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
        }

        // 2) 一次目录批量启动引擎处理所有块(-t 0:引擎不内部 tiling → 无接缝/黑帧;且省去逐块启动引擎的开销)。
        //    实测"目录批量 + -t 0"一次启动可正常处理全部块(无黑帧/无接缝),而逐块启动每块都要 3~6 秒启动开销,
        //    块多时会显著拖慢(用户反馈"一块一块重新启动引擎太慢")。这里一次喂整个块目录给引擎。
        progress?.Report((5, $"超分 处理 {totalTiles} 块(一次启动引擎)..."));
        int engineScale2;
        if (engine == "waifu2x")
        {
            var exe = FindWaifu2x() ?? throw new FileNotFoundException("未找到 waifu2x 引擎");
            var exeDir = Path.GetDirectoryName(exe)!;
            var modelDir = Path.Combine(exeDir, model);
            if (!Directory.Exists(modelDir))
                throw new FileNotFoundException($"缺少 waifu2x 模型:{model}。");
            var modelArg = Path.GetRelativePath(exeDir, modelDir);
            engineScale2 = CeilPowerOfTwo(scale);
            var args = $"-i \"{inDir}\" -o \"{outDir}\" -s {engineScale2} -n {noise} " +
                $"-t 0 -g {gpuId} -m \"{modelArg}\"{SafeRender.GetEngineThreadArgs()}" + (tta ? " -x" : "");
            await RunEngFallbackGpuAsync(exe, args, progress, ct).ConfigureAwait(false);
        }
        else
        {
            var exe = FindRealESRGAN() ?? throw new FileNotFoundException("未找到 Real-ESRGAN 引擎");
            engineScale2 = 4;
            var args = $"-i \"{inDir}\" -o \"{outDir}\" -s {engineScale2} -m models -n {model} " +
                $"-t 0 -g {gpuId}{SafeRender.GetEngineThreadArgs()}";
            await RunEngFallbackGpuAsync(exe, args, progress, ct).ConfigureAwait(false);
        }
        // 目录批量完成后,非原生倍数(如 3x)统一缩回(引擎输出 engineScale2 倍,缩回用户 scale 倍)
        if (Math.Abs(engineScale2 - scale) > 0.001)
        {
            progress?.Report((88, $"输出 {scale:0.##}x(引擎 {engineScale2}x 放大后精确调整)..."));
            foreach (var f in Directory.EnumerateFiles(outDir, "*.png"))
                await Task.Run(() => ResizeImage(f, f, scale / engineScale2), ct).ConfigureAwait(false);
        }
        // 黑帧防御:目录批量中任一块 vkQueueSubmit 失败→全黑(退出码仍 0)。有黑块则逐块用 CPU 软解重处理该块;
        // CPU 仍黑/不可用 → 抛"转 ONNX"信号(上层改用 ONNX 稳定引擎,不再反复 GPU 黑块死循环)。
        if (gpuId >= 0 && HasBlackPng(outDir))
        {
            // 【改进】有 ONNX 模型时【先】走 ONNX DirectML(GPU 加速、独立运行时,ncnn-GPU 崩≠DirectML 崩),
            // 而非先走最慢的 ncnn-CPU 逐块重算——与视频超分黑帧降级(ONNX→CPU 顺序)一致。
            // 仅当该引擎/模型无 ONNX 版(如某些 waifu2x 模型)才退回 ncnn-CPU 逐块兜底。
            string? onnxModel = engine is "realesrgan" ? EsrganOnnxService.ResolveEsrganOnnxPath(model)
                : engine is "waifu2x" ? EsrganOnnxService.FindWaifu2xModel() : null;
            if (onnxModel != null)
            {
                progress?.Report((89, "⚠ 检测到超分输出黑帧(GPU 队列异常),改用 ONNX 稳定引擎重算整图..."));
                AppLogger.Info("⚠ 目录批量超分检测到黑块(GPU 队列异常),改用 ONNX 稳定引擎重算整图");
                throw new InvalidOperationException("BLACKOUT_NEED_ONNX:GPU 黑块,转用 ONNX 稳定引擎");
            }
            progress?.Report((89, "⚠ 检测到超分输出黑帧(GPU 队列异常),改用 CPU 软解重处理受影响块..."));
            AppLogger.Info("⚠ 目录批量超分检测到黑块(GPU 队列异常),不同引擎/模型无 ONNX 版,改用 CPU 软解重处理");
            foreach (var tf in Directory.EnumerateFiles(inDir, "*.png"))
            {
                ct.ThrowIfCancellationRequested();
                var of = Path.Combine(outDir, Path.GetFileName(tf));
                if (!File.Exists(of) || IsBlackPng(of))
                {
                    if (File.Exists(of)) try { File.Delete(of); } catch { }
                    try { await UpOneTileAsync(tf, of, engine, model, scale, noise, -1, tta, progress, ct, tileSize).ConfigureAwait(false); }
                    catch
                    {
                        try { File.Delete(of); } catch { }
                        throw new InvalidOperationException("BLACKOUT_NEED_ONNX:GPU 黑块且 CPU 模式不可用,转用 ONNX 稳定引擎");
                    }
                    if (File.Exists(of) && IsBlackPng(of))
                    {
                        try { File.Delete(of); } catch { }
                        throw new InvalidOperationException("BLACKOUT_NEED_ONNX:GPU 持续黑块(CPU 修复无效),转用 ONNX 稳定引擎");
                    }
                }
            }
        }
        progress?.Report((90, $"超分 完成({totalTiles} 块)"));

        // 3) 手动羽化融合回整图(直接逐像素加权,BGRA 内存,不依赖 GDI+ alpha 混合,避免大面积崩坏)
        int outW = Math.Max(1, (int)Math.Round(srcW * scale));
        int outH = Math.Max(1, (int)Math.Round(srcH * scale));
        using var canvas = new System.Drawing.Bitmap(outW, outH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var cRect = new System.Drawing.Rectangle(0, 0, outW, outH);
        var cData = canvas.LockBits(cRect, System.Drawing.Imaging.ImageLockMode.ReadWrite,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            // 重叠区输出像素数,留 2px 余量保证淡入区完全落在真实重叠内(避免边缘透明缝)
            int ovFade = Math.Max(1, (int)(overlap * scale) - 2);
            unsafe
            {
                byte* cP0 = (byte*)cData.Scan0.ToPointer();
                int cStride = cData.Stride;
                int idx = 0;
                for (int r = 0; r < rows; r++)
                {
                    for (int c = 0; c < cols; c++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var tileOutPath = Path.Combine(outDir, $"tile_{idx:D4}.png");
                        // 引擎输出的图不一定是 32bppArgb(可能是 24bpp 无 alpha),
                        // 必须先克隆成 32bppArgb 再 LockBits,否则按 4 字节/像素读取会错位 → 崩坏
                        using var raw = new System.Drawing.Bitmap(tileOutPath);
                        using var tb = raw.Clone(new System.Drawing.Rectangle(0, 0, raw.Width, raw.Height),
                            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                        var tRect = new System.Drawing.Rectangle(0, 0, tb.Width, tb.Height);
                        var tData = tb.LockBits(tRect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                        try
                        {
                            byte* tP0 = (byte*)tData.Scan0.ToPointer();
                            int tStride = tData.Stride;
                            int dx = (int)Math.Round(xs[c] * scale);
                            int dy = (int)Math.Round(ys[r] * scale);
                            int tw = tb.Width, th = tb.Height;
                            for (int py = 0; py < th; py++)
                            {
                                int cy = dy + py;
                                if (cy >= outH) break;
                                byte* tRow = tP0 + py * tStride;
                                byte* cRow = cP0 + cy * cStride;
                                // smoothstep 淡入:避免线性交叉的中点"硬线",让跨块过渡更无痕(真实照片尤其明显)
                                double wy = (r > 0 && py < ovFade) ? SmoothStep((double)(py + 1) / ovFade) : 1.0;
                                for (int px = 0; px < tw; px++)
                                {
                                    int cx = dx + px;
                                    if (cx >= outW) break;
                                    double wx = (c > 0 && px < ovFade) ? SmoothStep((double)(px + 1) / ovFade) : 1.0;
                                    double w = wx * wy;   // 左/上边缘淡入权重(0→1)
                                    int ti = px * 4;
                                    int ci = cx * 4;
                                    if (w >= 1.0)
                                    {
                                        cRow[ci] = tRow[ti];
                                        cRow[ci + 1] = tRow[ti + 1];
                                        cRow[ci + 2] = tRow[ti + 2];
                                        cRow[ci + 3] = 255;
                                    }
                                    else
                                    {
                                        double iw = 1.0 - w;
                                        cRow[ci] = (byte)(cRow[ci] * iw + tRow[ti] * w);
                                        cRow[ci + 1] = (byte)(cRow[ci + 1] * iw + tRow[ti + 1] * w);
                                        cRow[ci + 2] = (byte)(cRow[ci + 2] * iw + tRow[ti + 2] * w);
                                        cRow[ci + 3] = 255;
                                    }
                                }
                            }
                        }
                        finally
                        {
                            tb.UnlockBits(tData);
                        }
                        idx++;
                        progress?.Report((90 + (int)(10.0 * idx / totalTiles), $"拼接融合 {idx}/{totalTiles} 块..."));
                    }
                }
            }
        }
        finally
        {
            canvas.UnlockBits(cData);
        }

        var tmpOut = Path.Combine(tmpDir, "final.png");
        canvas.Save(tmpOut, System.Drawing.Imaging.ImageFormat.Png);
        File.Copy(tmpOut, output, overwrite: true);

        try { Directory.Delete(tmpDir, true); } catch { /* 清理失败忽略 */ }
        return output;
    }

    /// <summary>smoothstep(0→1):平滑缓入,避免线性交叉的"硬线",让跨块淡入更无痕。</summary>
    private static double SmoothStep(double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    /// <summary>枚举目录里的帧图(png/jpg/jpeg)。
    /// 【为什么必须扩展名无关】引擎目录模式可直出 JPG(`-f jpg`,4K 实测省 31%),此后输出目录里【没有 PNG】;
    /// 若某些环节仍写死 "*.png",会静默失效:进度看门狗数不到帧(喂不了狗→误判挂起)、
    /// 非原生倍率缩回整段跳过(3x 变成 4x 输出)、黑帧巡检漏检整批。故统一走这里。</summary>
    private static System.Collections.Generic.IEnumerable<string> EnumerateImageFiles(string dir) =>
        Directory.EnumerateFiles(dir, "*.*").Where(x =>
            x.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
            x.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
            x.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase));

    /// <summary>检测目录里的 PNG 是否有全黑块(ncnn-vulkan GPU 队列失败时输出全黑/带状黑,退出码仍 0)。
    /// 采样近似:任一张图【整帧 ≥95% 像素接近全黑】或【某个 1/3 主条带 ≥95% 近黑】即判黑
    /// —— 实测坏帧是"下 2/3 全黑、上 1/3 正常",只看整帧会让整批黑块静默通过。</summary>
    private static bool HasBlackPng(string dir)
    {
        try
        {
            foreach (var f in EnumerateImageFiles(dir))
            {
                if (IsBlackPng(f)) return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>检测单个 PNG 是否近全黑(95% 以上像素 RGB 和 < 24)。internal:视频补帧/层批复用(黑帧=GPU 队列异常兼容症状)。
    /// 同步把"读不出的帧"(0 字节 / 空 / 损坏)视为缺陷帧返回 true —— ncnn-vulkan 在 50 系/部分驱动上会静默输出 0KB 空帧
    /// (退出码 0 不报错),若这里返回 false,空帧会被当成正常帧放行,一路传到合帧导致"找不到 frame_%06d.jpg"。
    /// 【判定口径】走 FrameInspect.IsDefectiveFrame = 整帧 ≥95% 近黑【或】任一条 1/3 主条带 ≥95% 近黑:
    /// 实测(ncnn-vulkan,RTX 4060)坏帧更常见的形态是"每帧下 2/3 全黑、上 1/3 正常",整帧口径会漏检。</summary>
    internal static bool IsBlackPng(string file) => NearBlackProbe(file, failIsDefect: true);

    /// <summary>只判"真·近全黑 / 带状近全黑"(可解码、确实 ≥95% 像素[或某个 1/3 条带]近黑)。空/0字节/未写完/解码失败的帧 → false(不算黑)。
    /// 用于【补帧黑帧防御】抽样:那里要找的是"GPU 输出真黑帧",若把"引擎还没写完的瞬时空帧"也当成黑,
    /// 会误触发整段补帧降级重算 → 补帧帧被清空 → upInput=0 → 超分无帧 / 合帧报"找不到 frame_%06d.jpg"。
    /// 空/坏帧在这里应"跳过不判黑",交给后续帧完整校验处理,而不是当黑帧降级。
    /// (条带判定只针对"已成功解码的完整帧",与"帧还没写完"这个场景互不干扰,故不引入上面的误触发风险。)</summary>
    internal static bool IsBlackPngStrict(string file) => NearBlackProbe(file, failIsDefect: false);

    /// <summary>上面两个入口的唯一实现:差别只在"读不出的帧"算不算缺陷(failIsDefect)。
    /// 判定阈值统一走 AlhPro.Core.FrameInspect(纯函数,可单测)。</summary>
    private static bool NearBlackProbe(string file, bool failIsDefect)
    {
        try
        {
            // 空/0 字节:必然不可解码(旧逻辑 new Bitmap 抛异常被 catch 吞掉返回 false,正是空帧漏检的根源)
            if (!File.Exists(file) || new FileInfo(file).Length == 0) return failIsDefect;
            using var bmp = new System.Drawing.Bitmap(file);
            if (bmp.Width <= 0 || bmp.Height <= 0) return failIsDefect;   // 尺寸非法
            var sums = new System.Collections.Generic.List<int>();
            int total = AlhPro.Core.FrameInspect.ForEachSample(bmp.Width, bmp.Height, (x, y) =>
            {
                var p = bmp.GetPixel(x, y);
                sums.Add((int)p.R + (int)p.G + (int)p.B);
            });
            // IsDefectiveFrame = 整帧近全黑(原语义一字未改)【或】任一 1/3 主条带近全黑(新增):
            // 实测坏帧是"下 2/3 全黑、上 1/3 正常",只黑约 66% 像素,整帧口径必然漏检(详见 FrameInspect 注释)。
            // 这样"黑帧"探测点(GPU 探测自检 / 分块黑块修复 / 补帧与层批抽样)都能识别带状坏帧,
            // 从而走既有的降级链,而不是把它当正常帧放行。
            return AlhPro.Core.FrameInspect.IsDefectiveFrame(sums.ToArray(), total, bmp.Width, bmp.Height);
        }
        catch { return failIsDefect; }
    }

    /// <summary>给分块做羽化 alpha:左/上边缘在 overlapPx 内从 0 淡入到 1(右/下保持不透明)。</summary>
    private static void ApplyFeatherAlpha(System.Drawing.Bitmap tile, int overlapPx, bool fadeLeft, bool fadeTop)
    {
        if (!fadeLeft && !fadeTop) return;
        for (int y = 0; y < tile.Height; y++)
        {
            for (int x = 0; x < tile.Width; x++)
            {
                double ax = fadeLeft && x < overlapPx ? (double)(x + 1) / overlapPx : 1.0;
                double ay = fadeTop && y < overlapPx ? (double)(y + 1) / overlapPx : 1.0;
                double a = ax * ay;
                if (a >= 1.0) continue;
                var col = tile.GetPixel(x, y);
                tile.SetPixel(x, y, System.Drawing.Color.FromArgb(
                    (int)Math.Round(a * 255), col.R, col.G, col.B));
            }
        }
    }

    /// <summary>
    /// 目录批处理超分:一次引擎启动处理目录内全部图片(视频逐帧超分用,避免每帧启动引擎)。
    /// 输出文件名与输入同名;非引擎原生倍数(如 1.5x/3x)先按引擎倍数放大,再批量缩放到目标倍数。
    /// </summary>
    public static async Task UpscaleDirAsync(string inputDir, string outputDir, string engine, string model,
        double scale, int noise, int gpuId, bool tta,
        IProgress<(int pct, string msg)>? progress = null, CancellationToken ct = default,
        int tileSize = 0, string? watchStage = null,
        int globalBaseFrames = 0, int globalTotalFrames = 0,
        bool preTiled = false, string outFormat = "png")
    {
        // 【引擎直出 JPG:4K 下实测省 31% 的超分耗时】
        // 实测(2026-09-11,RTX 4060 Laptop,waifu2x models-cunet 2x,1920×1080→3840×2160,同参数同素材各跑 2 次):
        //   引擎写 PNG = 2.98 秒/帧(5.6MB)、写 JPG = **2.02 秒/帧(2.8MB)**、写 webp = 9.99 秒/帧(更慢,勿选)。
        // 原因是 -j 1:1:1 时保存线程只有 1 个 4K 帧的 PNG 压缩压不住 GPU 计算 → 保存成了瓶颈;JPG(q100)快得多。
        // 视频链路本来就要把超分输出统一转成 JPG(q96)再合帧,所以让引擎直出 JPG 是【零画质代价的纯提速】:
        //   省掉引擎的 PNG 压缩 + App 侧的 PNG 解码 + q96 重编码整段(而且 q100 > q96,画质反而更好)。
        // 默认仍为 png(图片路径/分块路径依赖 *.png 枚举,行为一字不变),只在视频链路显式传 "jpg"。
        if (scale <= 0 || scale > 32)
            throw new ArgumentOutOfRangeException(nameof(scale), "放大倍数必须在 0~32 之间");
        tileSize = SafeRender.ResolveTile(tileSize);   // 未显式指定时按"安全渲染"墙自适应
        // 引擎目录模式下输出目录需已存在
        Directory.CreateDirectory(outputDir);
        var inCount = Directory.EnumerateFiles(inputDir).Count();
        if (inCount == 0)
            throw new InvalidOperationException("批处理输入目录为空");
        // 逐帧汇报(watchStage 非空时):引擎目录模式不输出百分比,轮询输出目录已生成帧数,
        // 像补帧那样逐帧显示"超分 第 N 帧 / 共 M 帧"(仅视频页启用)。
        // 百分比按"全局帧数"计算(globalBase 为本批起始的全局已处理帧数),
        // 否则每批都会从 45 冲到 90,进度虚高、预计时间严重失真。
        var watchDir = watchStage != null ? outputDir : null;
        var watchTotal = watchStage != null ? inCount : 0;

        // 显存不足(如 vkAllocateMemory 失败)时自动降分块重试,避免爆显存崩溃
        async Task RunEngAsync(string exe, Func<int, string> buildArgs)
        {
            // preTiled(图片分块路径):输入块已 ≤ tileSize,无需引擎再内部 tiling。
            // 关键修复(真实照片超分变黑):引擎在这些已≤tile 的块上再 -t 会触发 ncnn-vulkan vkQueueSubmit 失败→全黑。
            // 传 -t 0(关闭引擎侧 tiling)即可(块够小,无需 tiling,且更快)。
            int t = preTiled ? 0 : tileSize;
            int attempts = 0;
            while (true)
            {
                try
                {
                    await RunEngFallbackGpuAsync(exe, buildArgs(t), progress, ct, watchStage ?? "", watchTotal, watchDir, globalBaseFrames, globalTotalFrames).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (attempts < 3 && t > 64 && IsVramOom(ex))
                {
                    attempts++;
                    t = Math.Max(64, t / 2);
                    AppLogger.Info($"⚠ 降级:显存不足(第 {attempts} 次,原因:{ex.Message}),分块 {tileSize}→{t} 重试");
                    progress?.Report((0, $"⚠ 显存不足,自动降低分块 {tileSize}→{t} 重试(第 {attempts} 次)..."));
                }
            }
        }
        static bool IsVramOom(Exception ex)
        {
            var s = ex.Message ?? "";
            return s.Contains("vkAllocateMemory", StringComparison.OrdinalIgnoreCase)
                || s.Contains("out of memory", StringComparison.OrdinalIgnoreCase)
                || s.Contains("vk::", StringComparison.OrdinalIgnoreCase)
                || s.Contains("memory", StringComparison.OrdinalIgnoreCase);
        }

        // 引擎实际执行的倍数:waifu2x 取最大 2 的幂;realesrgan 的 x4plus 系【只有 4x 权重,必须按原生 4x 跑】,
        // 其余(如 realesr-animevideov3)按目标倍数。
        // 【为什么 x4plus 系不能按目标倍数跑】实测(2026-09-11,同一 1080p 帧、目录批量 + -t 0 -j 1:1:1):
        //   非原生 -s 2 时引擎的输出相对源帧【整体位移】(1080p 源约偏 56×40 像素:与源帧相关 0.71),
        //   而原生 -s 4 时相关 0.999;realesr-animevideov3 两种倍数都是 1.000 —— 引擎是按"模型原生倍率"
        //   计算分块贴回位置的,倍率不匹配时贴回就偏了。视频里表现为"每帧都偏一点、边缘还错",不报任何错。
        // 图片路径一直是按 4x 跑再缩回的(见 UpOneTileAsync 的注释),视频路径此前漏了这一步。
        int engineScale;
        if (engine == "waifu2x") engineScale = CeilPowerOfTwo(scale);
        else if (model.Contains("x4plus", StringComparison.OrdinalIgnoreCase)
                 || model.Contains("general-x4v3", StringComparison.OrdinalIgnoreCase)) engineScale = 4;   // 4x 专用权重:必须按原生 4x 跑,x4plus 与自转的 general-x4v3 同理
        else engineScale = Math.Clamp((int)Math.Ceiling(scale), 1, 4);

        if (engine == "waifu2x")
        {
            var exe = FindWaifu2x() ?? throw new FileNotFoundException("未找到 waifu2x 引擎");
            var modelDir = Path.Combine(Path.GetDirectoryName(exe)!, model);
            if (!Directory.Exists(modelDir))
                throw new FileNotFoundException($"缺少 waifu2x 模型:{model}。");
            if (engineScale == 1)
            {
                // 与单图路径完全一致:-s 1 在部分机型段错误崩溃,不再直连;
                // 不降噪时直接复制原图,降噪则用 2x 降噪模型处理后缩回 1x(画质更好)
                if (noise < 0)
                {
                    foreach (var f in Directory.EnumerateFiles(inputDir, "*.*")
                        .Where(x => x.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                                 || x.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                                 || x.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)))
                    {
                        // 【一致性】视频侧读批输出按调用方要的扩展名:若输入是 .jpg 而调用方要 png,复制到 .png 名
                        // (字节供 GDI+/ffmpeg 嗅探,可解码),避免"视频读某扩展名却因输入是另一种而得到 0 帧"。
                        // 1x 无降噪分支在视频正常路径不触发,这里统一成 outFormat 只是保持一致。
                        var dest = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(f) + "." + outFormat);
                        File.Copy(f, dest, overwrite: true);
                    }
                    return;
                }
                engineScale = 2;
            }
            await RunEngAsync(exe, t => $"-i \"{inputDir}\" -o \"{outputDir}\" -s {engineScale} -n {noise} " +
                $"-t 0 -g {gpuId} -m \"{modelDir}\"{SafeRender.GetEngineThreadArgs()} -f {outFormat}" + (tta ? " -x" : "")).ConfigureAwait(false);
        }
        else if (engine == "realcugan")
        {
            // realcugan 已整体移除(许可不明,见 THIRD_PARTY_NOTICES):兜底为 waifu2x
            throw new InvalidOperationException("Real-CUGAN 已移除(许可不明),请改用 waifu2x 或 Real-ESRGAN");
        }
        else
        {
            var exe = FindRealESRGAN() ?? throw new FileNotFoundException("未找到 Real-ESRGAN 引擎");
            // 同单张路径:显式 -m models -n 模型名(缺 -m 会找不到模型加载失败);TTA(-x)在 2022 老引擎上会卡死,故不传
            // -t 0 的语义是【引擎自己决定分块大小(auto)】,不是"关闭 tiling"——实测 ncnn-vulkan 引擎帮助里写的是
            //   "-t tile-size (>=32/0=auto, default=0)",0 即 auto(旧注释写成"关闭引擎内部 tiling",与引擎语义相反)。
            // 行为不变(仍传 0):整帧直算交给引擎按显存自选分块,分块过大才会 vkQueueSubmit 失败 → 黑帧/OOM,
            // 那种情况由 RunEngAsync 的"降分块重试"与上层的黑帧降级链接住。
            // 视频帧整帧直算(OOM 时 RunEngAsync 自动降级重试/减 tile),避免逐帧"一块一块"。
            await RunEngAsync(exe, t => $"-i \"{inputDir}\" -o \"{outputDir}\" -s {engineScale} -m models -n {model} " +
                $"-t 0 -g {gpuId}{SafeRender.GetEngineThreadArgs()} -f {outFormat}").ConfigureAwait(false);
        }

        // 非引擎原生倍数:批量缩放到目标倍数
        if (Math.Abs(engineScale - scale) > 0.001)
        {
            var ratio = scale / engineScale;
            progress?.Report((95, $"输出 {scale:0.##}x(引擎 {engineScale}x 放大后精确调整)..."));
            foreach (var f in EnumerateImageFiles(outputDir))
                await Task.Run(() => ResizeImage(f, f, ratio), ct).ConfigureAwait(false);
        }

        var outCount = Directory.EnumerateFiles(outputDir).Count();
        if (outCount == 0)
            throw new InvalidOperationException("引擎批处理未生成输出");
    }

    /// <summary>
    /// 收拢引擎实际输出到预期路径:
    /// 引擎对带 alpha 的输入输出 JPG 时会自动把文件名改为 "xxx.jpg.png"(保持 PNG 格式),
    /// 这里检测并处理:PNG 目标直接改名,JPG 目标做真实格式转换。
    /// 同时应用输出码率:JPG 按质量重新编码;PNG 按压缩级别重新保存(无损,只影响文件大小/速度)。
    /// </summary>
    private static void EnsureFinalOutput(string output, float jpgQuality = 0.92f, int pngCompress = 6)
    {
        var actual = File.Exists(output) ? output :
            (File.Exists(output + ".png") ? output + ".png" : null);
        if (actual == null)
            throw new InvalidOperationException(
                "引擎未生成输出文件:输入图片可能已损坏,或格式不被支持");
        var fi = new FileInfo(actual);
        if (fi.Length == 0)
            throw new InvalidOperationException(
                "引擎输出文件为空(0 字节):输入图片可能无法解码,已视为失败");
        if (actual == output) return;

        if (output.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
            output.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            // 引擎给了 PNG 内容:转成真正的 JPG(按用户码率质量)
            ConvertPngToJpg(actual, output, jpgQuality);
            // 新生成文件可能被 Defender/索引服务瞬时锁定,删除需重试
            for (int i = 0; i < 5; i++)
            {
                try { File.Delete(actual); break; }
                catch { Thread.Sleep(150); }
            }
        }
        else
        {
            // PNG 目标:引擎输出已是无损 PNG。默认档(pngCompress < 0)=原样无损输出,不额外压缩;
            // 其他档位按用户压缩级别重存(仍无损,只影响文件大小/保存速度)
            if (pngCompress < 0)
            {
                File.Move(actual, output, overwrite: true);
                return;
            }
            try
            {
                using var bmp = new System.Drawing.Bitmap(actual);
                SavePngWithCompression(bmp, output, pngCompress);
            }
            catch
            {
                // 重存失败(如文件被占用)退化为直接改名,不丢结果
                File.Move(actual, output, overwrite: true);
                return;
            }
            for (int i = 0; i < 5; i++)
            {
                try { File.Delete(actual); break; }
                catch { Thread.Sleep(150); }
            }
        }
    }

    /// <summary>把 PNG 转成 JPG(按质量),写入 jpgPath。
    /// 按内容解码(SourceBitmap 按魔数识别),故也接受 .png 名但实际为 JPG 字节的文件;供视频流水线「引擎输出转 JPG」复用。</summary>
    public static void ConvertPngToJpg(string pngPath, string jpgPath, float quality = 0.96f)
        => ConvertPngToJpg(pngPath, jpgPath, quality, out _);

    /// <summary>同上,并顺带报告该帧是否"缺陷帧"(nearBlack)——整帧近全黑【或】某条 1/3 主条带近全黑。
    /// 黑帧判定直接在已解码的位图上采样,不再另开一次全尺寸解码——视频批每帧本来就要解码转 JPG,
    /// 为查黑帧再解码一遍等于把这条最热路径的开销翻倍。
    /// 【为什么必须带条带判定】实测坏帧形态"下 2/3 全黑、上 1/3 正常"只黑约 66% 像素,
    /// 旧口径(整帧 ≥95%)判它正常 → 坏帧静默进成片、零日志、ncnnUnreliable 不置位(用户报的问题)。
    /// 参数名沿用它原来的 nearBlack,但语义是"是否缺陷帧":true 的调用方一律按缺陷走既有降级链。
    /// 注意:整帧近全黑的老语义【没有】被放宽(整帧近黑必然仍为 true),只是补上了漏检的那一类。</summary>
    public static void ConvertPngToJpg(string pngPath, string jpgPath, float quality, out bool nearBlack)
    {
        // 解码抛出/尺寸非法时默认留 true:异常路径由调用方按缺陷处理,这里偏保守不会漏判。
        nearBlack = true;
        using var img = new System.Drawing.Bitmap(pngPath);
        if (img.Width > 0 && img.Height > 0)
        {
            var sums = new System.Collections.Generic.List<int>();
            int total = AlhPro.Core.FrameInspect.ForEachSample(img.Width, img.Height, (x, y) =>
            {
                var p = img.GetPixel(x, y);
                sums.Add((int)p.R + (int)p.G + (int)p.B);
            });
            nearBlack = AlhPro.Core.FrameInspect.IsDefectiveFrame(sums.ToArray(), total, img.Width, img.Height);
        }
        // 视频中间帧 JPG:直接走 System.Drawing(GDI,转 24bppRgb 规避色偏),不走 WinRT——
        // WinRT BitmapEncoder 在后台/非 UI 线程会系统性抛 HRESULT=0x88982F41(视频处理必失败),
        // 导致每次视频处理都刷"WinRT JPG 编码不可用"日志 + 白试一次。GDI 在后台线程可靠、不刷日志。
        SaveJpegViaGdi(img, jpgPath, quality);
    }

    /// <summary>
    /// 用 WinRT 编码器写 JPG(颜色准确)。System.Drawing 的 JPG 编码会把颜色严重偏掉
    /// (红→黄绿、蓝→黑),故 JPG 输出统一走这里。阻塞 WinRT 异步(MTA 线程池完成,不会死锁)。
    /// 【兼容修复】部分机型/后台线程上 WinRT BitmapEncoder 会抛 HRESULT(空消息)→ 此前每帧转 JPG 失败
    /// 被替换成深灰占位帧 → 输出整片冻结。现改为 WinRT 失败时回退 System.Drawing(转 24bppRgb 规避色偏),
    /// 保证永远输出真实画面,不再出现占位/冻结帧;并记录 HRESULT 供排查。</summary>
    private static void SaveJpegViaWinRT(System.Drawing.Bitmap bmp, string jpgPath, float quality = 0.92f)
    {
        // 本会话已确认 WinRT 坏 → 直接走 GDI 兜底。原先这层兜底被包在 if(!_winrtJpegBroken) 块【内部】,
        // 闩锁一旦置位,整个方法空返回、一个字节都不写,调用方随后因输出文件缺失而失败(照片 JPG 全断)。
        if (_winrtJpegBroken) { SaveJpegViaGdi(bmp, jpgPath, quality); return; }
        int w = bmp.Width, h = bmp.Height;
        try
        {
            // 转成 32bppArgb 再取像素(System.Drawing 内存布局为 BGRA,需转成 RGBA 给 WinRT)
            using var argb = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(argb))
                g.DrawImage(bmp, 0, 0, w, h);
            var rect = new System.Drawing.Rectangle(0, 0, w, h);
            var data = argb.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            byte[] rgba;
            try
            {
                rgba = new byte[w * h * 4];
                unsafe
                {
                    byte* p0 = (byte*)data.Scan0.ToPointer();
                    for (int y = 0; y < h; y++)
                    {
                        byte* row = p0 + y * data.Stride;
                        int oy = y * w;
                        for (int x = 0; x < w; x++)
                        {
                            int i = x * 4;
                            int o = (oy + x) * 4;
                            rgba[o] = row[i + 2];     // R (内存 BGRA)
                            rgba[o + 1] = row[i + 1]; // G
                            rgba[o + 2] = row[i];     // B
                            rgba[o + 3] = 255;        // A
                        }
                    }
                }
            }
            finally
            {
                argb.UnlockBits(data);
            }

            var mem = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoder = Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                Windows.Graphics.Imaging.BitmapEncoder.JpegEncoderId, mem).GetAwaiter().GetResult();
            var props = new Windows.Graphics.Imaging.BitmapPropertySet();
            props.Add("ImageQuality", new Windows.Graphics.Imaging.BitmapTypedValue(
                Math.Clamp(quality, 0.1f, 1.0f), Windows.Foundation.PropertyType.Single));
            encoder.BitmapProperties.SetPropertiesAsync(props).GetAwaiter().GetResult();
            encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Rgba8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Ignore,
                (uint)w, (uint)h, 96, 96, rgba);
            encoder.FlushAsync().GetAwaiter().GetResult();
            mem.Seek(0);
            using var fs = File.Create(jpgPath);
            mem.AsStreamForRead().CopyTo(fs);
            return;   // WinRT 成功即返回
        }
        catch (Exception ex)
        {
            _winrtJpegBroken = true;
            AppLogger.Warn($"⚠ WinRT JPG 编码不可用({ex.GetType().Name},HRESULT=0x{ex.HResult:X8})——本会话改成 System.Drawing(转 24bppRgb)编码,避免占位/冻结帧;{ex.Message?.Split('\n')[0]}");
        }
        // WinRT 本次失败 → System.Drawing 兜底(真实画面,不再占位/冻结)
        SaveJpegViaGdi(bmp, jpgPath, quality);
    }

    /// <summary>System.Drawing 编码 JPG 的回退路径:转成无 alpha 的 24bppRgb 再编码,规避 GDI+ 对 ARGB 的色偏。</summary>
    private static void SaveJpegViaGdi(System.Drawing.Bitmap bmp, string jpgPath, float quality = 0.92f)
    {
        using var rgb = new System.Drawing.Bitmap(bmp.Width, bmp.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (var g = System.Drawing.Graphics.FromImage(rgb))
            g.DrawImage(bmp, 0, 0, bmp.Width, bmp.Height);
        var codec = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
        if (codec == null) { rgb.Save(jpgPath, System.Drawing.Imaging.ImageFormat.Jpeg); return; }
        using var enc = new System.Drawing.Imaging.EncoderParameters(1);
        enc.Param[0] = new System.Drawing.Imaging.EncoderParameter(
            System.Drawing.Imaging.Encoder.Quality, (long)Math.Round(Math.Clamp(quality, 0.1f, 1.0f) * 100.0));
        rgb.Save(jpgPath, codec, enc);
    }

    /// <summary>PNG 无损保存并指定压缩级别(0-9:低=快/文件大,高=慢/文件小;不影响画质)。</summary>
    private static void SavePngWithCompression(System.Drawing.Bitmap bmp, string pngPath, int level)
    {
        var ici = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
            .First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Png.Guid);
        using var ep = new System.Drawing.Imaging.EncoderParameters(1);
        ep.Param[0] = new System.Drawing.Imaging.EncoderParameter(
            System.Drawing.Imaging.Encoder.Compression, (long)Math.Clamp(level, 0, 9));
        bmp.Save(pngPath, ici, ep);
    }

    /// <summary>
    /// 后处理增强(可叠加,顺序从温和到强烈):
    /// 减少杂色(中值滤波·保边缘) → 保留细节(温和·保护平坦区) → 清晰(大核局部对比度) → 钝化蒙版(经典 USM·阈值保护) →
    /// 去模糊(大半径反锐化) → 边缘增强(只强边缘) → 锐化(强·全边缘) → 边缘抗锯齿(只磨边缘阶梯)。
    /// </summary>
    /// <param name="path">图片路径(原地处理,按扩展名保存 PNG/JPG)。</param>
    public static void EnhanceImage(string path, int sharpen, int detail,
        int clarity = 0, int deblur = 0, int usm = 0, int edge = 0, int detailEnhance = 0,
        IProgress<(int pct, string msg)>? progress = null,
        int denoise = 0, int aa = 0, int dehaze = 0,
        float jpgQuality = 0.92f, int pngCompress = 6)
    {
        if (sharpen <= 0 && detail <= 0 && clarity <= 0 && deblur <= 0 && usm <= 0 && edge <= 0 && detailEnhance <= 0
            && denoise <= 0 && aa <= 0 && dehaze <= 0) return;
        // 逐项增强:全部在内存里对同一张图处理(只读一次、只存一次,比每项都重读重写快很多)
        var passes = new System.Collections.Generic.List<(string name, System.Action<System.Drawing.Bitmap> run)>();
        if (dehaze > 0)       passes.Add(("去雾", b => ApplyDehazeInMemory(b, dehaze)));
        if (denoise > 0)      passes.Add(("减少杂色", b => ApplyMedianInMemory(b, denoise)));
        // 保留细节:CLAHE 风格局部对比度(提升局部细节,不动整体影调)——不是全局锐化
        if (detail > 0)       passes.Add(("保留细节", b => ApplyLocalContrastInMemory(b, detail)));
        // 细节增强:高通提取(原图 - 高斯模糊)加强微细节
        if (detailEnhance > 0) passes.Add(("细节增强", b => ApplyHighFreqInMemory(b, detailEnhance)));
        // 清晰:大半径 unsharp = 局部对比度/中调对比(Lightroom Clarity 常用)
        if (clarity > 0)      passes.Add(("清晰", b => ApplyUnsharpInMemory(b, clarity / 100.0 * 1.1, 0, 8)));
        // 钝化蒙版:标准 USM(阈值保护平坦区,弱噪声不被放大)
        if (usm > 0)          passes.Add(("钝化蒙版", b => ApplyUnsharpInMemory(b, usm / 100.0 * 1.4, 8, 4)));
        // 去模糊:真·理查森-露西反卷积
        if (deblur > 0)       passes.Add(("去模糊", b => ApplyDeblurInMemory(b, deblur)));
        // 边缘增强:Sobel 边缘掩膜放缩加到原图(只提边,不糊内部)
        if (edge > 0)         passes.Add(("边缘增强", b => ApplyEdgeEnhanceInMemory(b, edge)));
        // 锐化:小核 USM(强烈、无阈值,边缘清晰)
        if (sharpen > 0)      passes.Add(("锐化", b => ApplyUnsharpInMemory(b, sharpen / 100.0 * 2.0, 0, 2)));
        if (aa > 0)           passes.Add(("边缘抗锯齿", b => ApplyEdgeSmoothInMemory(b, aa)));
        int total = passes.Count, done = 0;
        using var bmp = new System.Drawing.Bitmap(path);
        foreach (var p in passes)
        {
            progress?.Report((done * 100 / Math.Max(1, total), $"画质增强:{p.name}({done + 1}/{total})..."));
            p.run(bmp);
            done++;
        }
        progress?.Report((100, "画质增强:完成"));

        // 只保存一次(临时文件放 temp 目录,避免输出目录出现临时文件)
        var tmpSave = Path.Combine(EngineService.TempRoot, $"imgup_enh_{Guid.NewGuid():N}.png");
        try
        {
            if (path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                SaveJpegViaWinRT(bmp, tmpSave, jpgQuality);   // JPG:压缩质量可调
            else if (pngCompress >= 0)
                SavePngWithCompression(bmp, tmpSave, pngCompress);   // PNG 无损:按用户压缩级别
            else
                bmp.Save(tmpSave, System.Drawing.Imaging.ImageFormat.Png);   // 默认档:原样无损保存
            bmp.Dispose();
            File.Copy(tmpSave, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(tmpSave); } catch { /* 清理失败忽略 */ }
        }
    }

    /// <summary>保留细节(CLAHE 风格局部对比度):以像素为中心取局部窗口,把该像素向"局部对比度拉伸"方向调整,
    /// 提升局部细节而**不改变整体影调/全局对比**。强度 0-100 控制提升幅度。</summary>
    private static void ApplyLocalContrastInMemory(System.Drawing.Bitmap bmp, int strength)
    {
        int w = bmp.Width, h = bmp.Height;
        if (w < 3 || h < 3) return;
        double amount = strength / 100.0;
        int R = 3;   // 局部窗口半径(3×3~7×7 邻域)
        var rect = new System.Drawing.Rectangle(0, 0, w, h);
        var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadWrite,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride; int n = w * h;
            var rc = new byte[n]; var gc = new byte[n]; var bc = new byte[n];
            unsafe
            {
                byte* p0 = (byte*)data.Scan0.ToPointer();
                for (int y = 0; y < h; y++) { byte* row = p0 + y * stride; int idx = y * w;
                    for (int x = 0; x < w; x++) { int i = x * 4; bc[idx + x] = row[i]; gc[idx + x] = row[i + 1]; rc[idx + x] = row[i + 2]; } }
            }
            LocalContrastChannel(rc, w, h, R, amount);
            LocalContrastChannel(gc, w, h, R, amount);
            LocalContrastChannel(bc, w, h, R, amount);
            unsafe
            {
                byte* p0 = (byte*)data.Scan0.ToPointer();
                for (int y = 0; y < h; y++) { byte* row = p0 + y * stride; int idx = y * w;
                    for (int x = 0; x < w; x++) { int i = x * 4; row[i] = bc[idx + x]; row[i + 1] = gc[idx + x]; row[i + 2] = rc[idx + x]; } }
            }
        }
        finally { bmp.UnlockBits(data); }
    }

    /// <summary>单通道局部对比度增强:像素新值 = 原值 + (原值 - 局部均值) × k(放大局部偏离,保留细节)。
    /// 局部均值用 (2R+1)² 均值近似;k 随强度,最大约 +0.6。</summary>
    private static void LocalContrastChannel(byte[] src, int w, int h, int R, double amount)
    {
        var orig = (byte[])src.Clone();
        double k = amount * 0.6;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                long sum = 0; int cnt = 0;
                for (int dy = -R; dy <= R; dy++)
                {
                    int yy = Math.Clamp(y + dy, 0, h - 1) * w;
                    for (int dx = -R; dx <= R; dx++)
                    {
                        int xx = Math.Clamp(x + dx, 0, w - 1);
                        sum += orig[yy + xx]; cnt++;
                    }
                }
                int center = orig[y * w + x];
                double localAvg = (double)sum / cnt;
                int v = (int)Math.Round(center + (center - localAvg) * k);
                src[y * w + x] = (byte)Math.Clamp(v, 0, 255);
            }
        }
    }

    /// <summary>细节增强(高通提取):把"原图 - 高斯模糊"(= 高频细节)按强度加回原图。比 unsharp 更细、更贴微细节。</summary>
    private static void ApplyHighFreqInMemory(System.Drawing.Bitmap bmp, int strength)
    {
        int w = bmp.Width, h = bmp.Height;
        if (w < 3 || h < 3) return;
        double amount = strength / 100.0 * 0.7;
        var rect = new System.Drawing.Rectangle(0, 0, w, h);
        var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadWrite,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride; int n = w * h;
            var rc = new byte[n]; var gc = new byte[n]; var bc = new byte[n];
            unsafe
            {
                byte* p0 = (byte*)data.Scan0.ToPointer();
                for (int y = 0; y < h; y++) { byte* row = p0 + y * stride; int idx = y * w;
                    for (int x = 0; x < w; x++) { int i = x * 4; bc[idx + x] = row[i]; gc[idx + x] = row[i + 1]; rc[idx + x] = row[i + 2]; } }
            }
            HighFreqChannel(rc, w, h, amount);
            HighFreqChannel(gc, w, h, amount);
            HighFreqChannel(bc, w, h, amount);
            unsafe
            {
                byte* p0 = (byte*)data.Scan0.ToPointer();
                for (int y = 0; y < h; y++) { byte* row = p0 + y * stride; int idx = y * w;
                    for (int x = 0; x < w; x++) { int i = x * 4; row[i] = bc[idx + x]; row[i + 1] = gc[idx + x]; row[i + 2] = rc[idx + x]; } }
            }
        }
        finally { bmp.UnlockBits(data); }
    }

    /// <summary>单通道高通增强:new = orig + (orig - blur(3×3均值)) × amount。</summary>
    private static void HighFreqChannel(byte[] src, int w, int h, double amount)
    {
        var orig = (byte[])src.Clone();
        for (int y = 1; y < h - 1; y++)
        {
            int row = y * w;
            for (int x = 1; x < w - 1; x++)
            {
                int sum = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int yy = row + dy * w;
                    for (int dx = -1; dx <= 1; dx++) sum += orig[yy + x + dx];
                }
                int blur = sum / 9;
                int edge = orig[row + x] - blur;   // 高频
                int v = (int)Math.Round(orig[row + x] + edge * amount);
                src[row + x] = (byte)Math.Clamp(v, 0, 255);
            }
        }
    }

    /// <summary>边缘增强(Sobel 掩膜):计算梯度幅值,把边缘处像素沿梯度方向放大,边缘锐利但内部平坦区不动。</summary>
    private static void ApplyEdgeEnhanceInMemory(System.Drawing.Bitmap bmp, int strength)
    {
        int w = bmp.Width, h = bmp.Height;
        if (w < 3 || h < 3) return;
        double amount = strength / 100.0 * 0.8;
        var rect = new System.Drawing.Rectangle(0, 0, w, h);
        var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadWrite,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride; int n = w * h;
            var rc = new byte[n]; var gc = new byte[n]; var bc = new byte[n];
            unsafe
            {
                byte* p0 = (byte*)data.Scan0.ToPointer();
                for (int y = 0; y < h; y++) { byte* row = p0 + y * stride; int idx = y * w;
                    for (int x = 0; x < w; x++) { int i = x * 4; bc[idx + x] = row[i]; gc[idx + x] = row[i + 1]; rc[idx + x] = row[i + 2]; } }
            }
            EdgeEnhanceChannel(rc, w, h, amount);
            EdgeEnhanceChannel(gc, w, h, amount);
            EdgeEnhanceChannel(bc, w, h, amount);
            unsafe
            {
                byte* p0 = (byte*)data.Scan0.ToPointer();
                for (int y = 0; y < h; y++) { byte* row = p0 + y * stride; int idx = y * w;
                    for (int x = 0; x < w; x++) { int i = x * 4; row[i] = bc[idx + x]; row[i + 1] = gc[idx + x]; row[i + 2] = rc[idx + x]; } }
            }
        }
        finally { bmp.UnlockBits(data); }
    }

    /// <summary>单通道边缘增强:Sobel 梯度 gx/gy → 梯度幅值 m;沿梯度方向加一次差分以锐化边缘。</summary>
    private static void EdgeEnhanceChannel(byte[] src, int w, int h, double amount)
    {
        var orig = (byte[])src.Clone();
        for (int y = 1; y < h - 1; y++)
        {
            int row = y * w;
            for (int x = 1; x < w - 1; x++)
            {
                int a = orig[(y - 1) * w + (x - 1)], b2 = orig[(y - 1) * w + x], c = orig[(y - 1) * w + (x + 1)];
                int d = orig[row + (x - 1)], e = orig[row + x], f = orig[row + (x + 1)];
                int g = orig[(y + 1) * w + (x - 1)], h2 = orig[(y + 1) * w + x], i = orig[(y + 1) * w + (x + 1)];
                int gx = (c + 2 * f + i) - (a + 2 * d + g);
                int gy = (g + 2 * h2 + i) - (a + 2 * b2 + c);
                int mag = (int)Math.Abs(gx) + (int)Math.Abs(gy);   // 梯度幅值(粗)
                // 沿梯度方向二阶梯微分强化边缘
                int laplace = (a + b2 + c + d + f + g + h2 + i) - 8 * e;
                int v = (int)Math.Round(e + mag * amount * 0.25 + Math.Sign(laplace) * amount * 8);
                src[row + x] = (byte)Math.Clamp(v, 0, 255);
            }
        }
    }

    /// <summary>在内存 Bitmap 上执行一轮 unsharp 增强(不读盘不存盘,多轮增强共用一张图)。passes = box blur 次数。</summary>
    private static void ApplyUnsharpInMemory(System.Drawing.Bitmap bmp, double amount, int threshold, int passes)
    {
        int w = bmp.Width, h = bmp.Height;
        if (w <= 0 || h <= 0) return;
        var rect = new System.Drawing.Rectangle(0, 0, w, h);
        var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadWrite,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            int n = w * h;
            // 通道分离(避免每像素 GetPixel 的开销)
            var r = new byte[n];
            var g = new byte[n];
            var b = new byte[n];
            unsafe
            {
                byte* p0 = (byte*)data.Scan0.ToPointer();
                for (int y = 0; y < h; y++)
                {
                    byte* row = p0 + y * stride;
                    int idx = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int i = x * 4;
                        b[idx + x] = row[i];
                        g[idx + x] = row[i + 1];
                        r[idx + x] = row[i + 2];
                    }
                }
            }

            var tmp = new byte[n];
            UnsharpChannel(r, tmp, w, h, amount, threshold, passes);
            UnsharpChannel(g, tmp, w, h, amount, threshold, passes);
            UnsharpChannel(b, tmp, w, h, amount, threshold, passes);

            unsafe
            {
                byte* p0 = (byte*)data.Scan0.ToPointer();
                for (int y = 0; y < h; y++)
                {
                    byte* row = p0 + y * stride;
                    int idx = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int i = x * 4;
                        row[i] = b[idx + x];
                        row[i + 1] = g[idx + x];
                        row[i + 2] = r[idx + x];
                    }
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>
    /// 单通道 unsharp mask:两次 box blur 近似高斯,超出阈值的差异增强。
    /// 注意:必须保留原图副本做差分基底;tmp 最终保存模糊结果,src 写增强结果。
    /// </summary>
    private static void UnsharpChannel(byte[] src, byte[] tmp, int w, int h,
        double amount, int threshold, int passes)
    {
        // 原图副本(差分基底)
        var orig = new byte[src.Length];
        Buffer.BlockCopy(src, 0, orig, 0, src.Length);

        // 多次 box blur 近似更大核的高斯;结束后 tmp = 最终模糊结果
        for (int p = 0; p < Math.Max(1, passes); p++)
            BoxBlur(src, tmp, w, h);

        for (int i = 0; i < src.Length; i++)
        {
            int diff = orig[i] - tmp[i];          // 原图 - 模糊图 = 边缘信息
            int v = Math.Abs(diff) > threshold    // 只有差异超过阈值才增强,否则原样保留
                ? (int)(orig[i] + amount * diff)
                : orig[i];
            src[i] = (byte)Math.Clamp(v, 0, 255);
        }
    }

    /// <summary>水平+垂直 box blur(半径 1),结果写入 src;tmp 为临时缓冲。</summary>
    private static void BoxBlur(byte[] src, byte[] tmp, int w, int h)
    {
        // 水平(仅当 w > 1)
        if (w > 1)
        {
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                tmp[row] = (byte)((src[row] + src[row + 1] + 1) / 2);
                for (int x = 1; x < w - 1; x++)
                    tmp[row + x] = (byte)((src[row + x - 1] + src[row + x] + src[row + x + 1] + 1) / 3);
                tmp[row + w - 1] = (byte)((src[row + w - 2] + src[row + w - 1] + 1) / 2);
            }
        }
        else
        {
            Buffer.BlockCopy(src, 0, tmp, 0, src.Length);
        }
        // 垂直(仅当 h > 1)
        if (h > 1)
        {
            for (int x = 0; x < w; x++)
            {
                src[x] = (byte)((tmp[x] + tmp[w + x] + 1) / 2);
                for (int y = 1; y < h - 1; y++)
                {
                    int i = w * y + x;
                    src[i] = (byte)((tmp[i - w] + tmp[i] + tmp[i + w] + 1) / 3);
                }
                src[w * (h - 1) + x] = (byte)((tmp[w * (h - 2) + x] + tmp[w * (h - 1) + x] + 1) / 2);
            }
        }
        else
        {
            Buffer.BlockCopy(tmp, 0, src, 0, src.Length);
        }
        // tmp = 本次模糊结果(供 unsharp 差分使用)
        Array.Copy(src, tmp, src.Length);
    }

    /// <summary>去雾:标准【暗通道先验(何恺明 DCP)】——估计透射率 + 大气光,反演雾图,比简单直方图拉伸真正有效。
    /// 对"灰蒙/泛白/雾霾"图显著去除;强度 0-100 控制还原程度(与原图混合)。</summary>
    private static void ApplyDehazeInMemory(System.Drawing.Bitmap bmp, int strength)
    {
        int w = bmp.Width, h = bmp.Height;
        if (w < 3 || h < 3) return;
        var rect = new System.Drawing.Rectangle(0, 0, w, h);
        var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadWrite,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            int n = w * h;
            var r = new byte[n]; var g = new byte[n]; var b = new byte[n];
            unsafe
            {
                byte* p0 = (byte*)data.Scan0.ToPointer();
                for (int y = 0; y < h; y++)
                {
                    byte* row = p0 + y * stride; int idx = y * w;
                    for (int x = 0; x < w; x++) { int i = x * 4; b[idx + x] = row[i]; g[idx + x] = row[i + 1]; r[idx + x] = row[i + 2]; }
                }
            }
            // ① 暗通道:每像素取 R/G/B 最小值,再做局部(15×15)最小值滤波(近似 DCP)
            var dark = new byte[n];
            for (int i = 0; i < n; i++)
                dark[i] = (byte)Math.Min(r[i], Math.Min(g[i], b[i]));
            // 局部最小值(半径 7,N 次近似更大窗口)
            var tmp = new byte[n];
            for (int p = 0; p < 3; p++) { MinFilter(dark, tmp, w, h, 2); MinFilter(tmp, dark, w, h, 2); }
            // ② 大气光 A = 暗通道最亮前 0.1% 像素的均值(按原亮度)
            var idxByLuma = new int[n];
            for (int i = 0; i < n; i++) idxByLuma[i] = i;
            // 按暗通道值降序排列(取最亮大气光)
            Array.Sort(idxByLuma, (i1, i2) => dark[i2].CompareTo(dark[i1]));
            double aR = 0, aG = 0, aB = 0; int cnt = Math.Max(1, n / 1000);
            for (int k = 0; k < cnt; k++) { int i = idxByLuma[k]; aR += r[i]; aG += g[i]; aB += b[i]; }
            aR /= cnt; aG /= cnt; aB /= cnt;
            // ③ 透射率 t = 1 - ω·dark/A(ω=0.95 保留一点雾);加下限防除零/过饱和
            const double omega = 0.95;
            double amount = strength / 100.0;
            double tMin = Math.Max(0.05, 1.0 - amount * 0.4);   // 强度越大,可去雾越深(透射率下限越低)
            for (int i = 0; i < n; i++)
            {
                double darkNorm = dark[i] / 255.0;
                // 归一化透射率(按大气光归一)
                double t = 1.0 - omega * Math.Min(1.0, dark[i] / (255.0 * 0.9 + 1.0));
                t = Math.Max(tMin, Math.Min(1.0, t));
                int re = (int)((r[i] - amount * aR) / t);
                int ge = (int)((g[i] - amount * aG) / t);
                int be = (int)((b[i] - amount * aB) / t);
                // 与原图按强度混合(强度低时改动小,避免过度)
                r[i] = (byte)Math.Clamp((int)((r[i] * (1 - amount) + re * amount)), 0, 255);
                g[i] = (byte)Math.Clamp((int)((g[i] * (1 - amount) + ge * amount)), 0, 255);
                b[i] = (byte)Math.Clamp((int)((b[i] * (1 - amount) + be * amount)), 0, 255);
            }
            unsafe
            {
                byte* p0 = (byte*)data.Scan0.ToPointer();
                for (int y = 0; y < h; y++)
                {
                    byte* row = p0 + y * stride; int idx = y * w;
                    for (int x = 0; x < w; x++) { int i = x * 4; row[i] = b[idx + x]; row[i + 1] = g[idx + x]; row[i + 2] = r[idx + x]; }
                }
            }
        }
        finally { bmp.UnlockBits(data); }
    }

    /// <summary>单通道局部最小值滤波(半径 r):用于暗通道先验的 min 窗口。</summary>
    private static void MinFilter(byte[] src, byte[] dst, int w, int h, int r)
    {
        // 水平滑窗最小值
        var tmp = new byte[src.Length];
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int lo = byte.MaxValue;
                for (int dx = -r; dx <= r; dx++)
                {
                    int xx = Math.Clamp(x + dx, 0, w - 1);
                    if (src[row + xx] < lo) lo = src[row + xx];
                }
                tmp[row + x] = (byte)lo;
            }
        }
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int lo = byte.MaxValue;
                for (int dy = -r; dy <= r; dy++)
                {
                    int yy = Math.Clamp(y + dy, 0, h - 1) * w;
                    if (tmp[yy + x] < lo) lo = tmp[yy + x];
                }
                dst[y * w + x] = (byte)lo;
            }
        }
    }

    /// <summary>减少杂色:3×3 中值滤波(保边缘去噪点),强度 51+ 时再做一遍(更彻底)。</summary>
    private static void ApplyMedianInMemory(System.Drawing.Bitmap bmp, int strength)
    {
        int w = bmp.Width, h = bmp.Height;
        if (w < 3 || h < 3) return;
        var rect = new System.Drawing.Rectangle(0, 0, w, h);
        var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadWrite,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            int n = w * h;
            var r = new byte[n];
            var g = new byte[n];
            var b = new byte[n];
            unsafe
            {
                byte* p0 = (byte*)data.Scan0.ToPointer();
                for (int y = 0; y < h; y++)
                {
                    byte* row = p0 + y * stride;
                    int idx = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int i = x * 4;
                        b[idx + x] = row[i];
                        g[idx + x] = row[i + 1];
                        r[idx + x] = row[i + 2];
                    }
                }
            }
            MedianChannel(r, w, h);
            MedianChannel(g, w, h);
            MedianChannel(b, w, h);
            if (strength > 50)
            {
                MedianChannel(r, w, h);
                MedianChannel(g, w, h);
                MedianChannel(b, w, h);
            }
            unsafe
            {
                byte* p0 = (byte*)data.Scan0.ToPointer();
                for (int y = 0; y < h; y++)
                {
                    byte* row = p0 + y * stride;
                    int idx = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int i = x * 4;
                        row[i] = b[idx + x];
                        row[i + 1] = g[idx + x];
                        row[i + 2] = r[idx + x];
                    }
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>单通道 3×3 中值滤波(边缘保留型降噪);原地写 src,输入副本取自 orig。</summary>
    private static void MedianChannel(byte[] src, int w, int h)
    {
        var orig = new byte[src.Length];
        Buffer.BlockCopy(src, 0, orig, 0, src.Length);
        // 固定 9 元素窗口,用选择排序取中值(避免每次分配数组)
        var win = new byte[9];
        for (int y = 0; y < h; y++)
        {
            int rowBase = y * w;
            for (int x = 0; x < w; x++)
            {
                int k = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int yy = Math.Clamp(y + dy, 0, h - 1) * w;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = Math.Clamp(x + dx, 0, w - 1);
                        win[k++] = orig[yy + xx];
                    }
                }
                // 插入排序(9 元素),取第 5 小(中值)
                for (int i = 1; i < 9; i++)
                {
                    byte v = win[i];
                    int j = i - 1;
                    while (j >= 0 && win[j] > v) { win[j + 1] = win[j]; j--; }
                    win[j + 1] = v;
                }
                src[rowBase + x] = win[4];
            }
        }
    }

    /// <summary>真·去模糊:理查森-露西(Richardson-Lucy)反卷积。对运动/散焦/高斯类模糊有真实复原效果,
    /// 区别于 unsharp 反锐化(后者只是"增强边缘",对真模糊无效)。用高斯核 + 若干次迭代(强度决定迭代数)。
    /// 为控制耗时/噪点,迭代次数随强度线性(3~10 次),并在最后与"轻微锐化"补一下边缘。</summary>
    private static void ApplyDeblurInMemory(System.Drawing.Bitmap bmp, int strength)
    {
        int w = bmp.Width, h = bmp.Height;
        if (w < 3 || h < 3) return;
        // 【超大图保护】RL 反卷积迭代多次+3通道,内存/耗时随像素数线性增长。
        // >400 万像素(约2000×2000)时降低迭代,>900 万像素(约4K)时改用简单 unsharp 兜底(避免内存峰值/超长耗时)。
        long px = (long)w * h;
        int iter = Math.Max(3, Math.Min(10, (int)Math.Round(strength / 100.0 * 9) + 2));
        if (px > 9_000_000) { ApplyUnsharpInMemory(bmp, strength / 100.0 * 1.5, 2, 6); return; }
        if (px > 4_000_000) iter = Math.Max(2, iter - 3);   // 大图收敛快,减迭代防卡太久
        int kernelR = strength >= 60 ? 2 : 1;   // 强度大 → 更大模糊核(对应更严重的模糊)
        // 预计算归一化一维高斯核(对称可分离):RL 用可分离卷积大幅提速(二维→两次一维)
        int ksz = kernelR * 2 + 1;
        var k1d = new double[ksz];
        double ksum = 0; double sigma = kernelR * 0.8 + 0.6;
        for (int dx = -kernelR; dx <= kernelR; dx++)
        {
            double v = Math.Exp(-(dx * dx) / (2 * sigma * sigma));
            k1d[dx + kernelR] = v; ksum += v;
        }
        for (int i = 0; i < k1d.Length; i++) k1d[i] /= ksum;

        var rect = new System.Drawing.Rectangle(0, 0, w, h);
        var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadWrite,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride; int n = w * h;
            var r = new byte[n]; var g = new byte[n]; var b = new byte[n];
            unsafe
            {
                byte* p0 = (byte*)data.Scan0.ToPointer();
                for (int y = 0; y < h; y++) { byte* row = p0 + y * stride; int idx = y * w;
                    for (int x = 0; x < w; x++) { int i = x * 4; b[idx + x] = row[i]; g[idx + x] = row[i + 1]; r[idx + x] = row[i + 2]; } }
            }
            // 归一化到 0..1 双精度数组做 RL
            double[] R = ToDouble(r), G = ToDouble(g), B = ToDouble(b);
            R = RichardsonLucy(R, w, h, k1d, kernelR, iter);
            G = RichardsonLucy(G, w, h, k1d, kernelR, iter);
            B = RichardsonLucy(B, w, h, k1d, kernelR, iter);
            // 写回(RL 结果可能轻微过冲,收紧)
            for (int i = 0; i < n; i++)
            {
                r[i] = (byte)Math.Clamp((int)Math.Round(R[i] * 255.0), 0, 255);
                g[i] = (byte)Math.Clamp((int)Math.Round(G[i] * 255.0), 0, 255);
                b[i] = (byte)Math.Clamp((int)Math.Round(B[i] * 255.0), 0, 255);
            }
            unsafe
            {
                byte* p0 = (byte*)data.Scan0.ToPointer();
                for (int y = 0; y < h; y++) { byte* row = p0 + y * stride; int idx = y * w;
                    for (int x = 0; x < w; x++) { int i = x * 4; row[i] = b[idx + x]; row[i + 1] = g[idx + x]; row[i + 2] = r[idx + x]; } }
            }
        }
        finally { bmp.UnlockBits(data); }
    }

    /// <summary>理查森-露西反卷积:单通道,已知一维可分离(高斯)核。迭代增强高频复原。</summary>
    private static double[] RichardsonLucy(double[] obs, int w, int h, double[] k1d, int kr, int iter)
    {
        int n = w * h;
        var est = (double[])obs.Clone();   // 初始估计 = 退化图
        var blur = new double[n];
        var rel = new double[n];
        for (int it = 0; it < iter; it++)
        {
            // ① 估计图卷积核 → 模拟退化(blur = est ⊛ k)
            SepConv(est, blur, w, h, k1d, kr);
            // ② 比值 = 观测 / 退化(加微小值防除零)
            for (int i = 0; i < n; i++) rel[i] = obs[i] / Math.Max(1e-6, blur[i]);
            // ③ 比值再卷积核(相关 = 翻转核卷积),更新估计
            SepConv(rel, blur, w, h, k1d, kr);
            for (int i = 0; i < n; i++) est[i] *= Math.Max(0.0, blur[i]);
        }
        return est;
    }

    /// <summary>单通道可分离卷积(水平+垂直,对称高斯核),边框 clamped。RL 前向退化/后向更新用。</summary>
    private static void SepConv(double[] src, double[] dst, int w, int h, double[] k1d, int kr)
    {
        int n = w * h;
        var tmp = new double[n];
        // 水平
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                double acc = 0;
                for (int dx = -kr; dx <= kr; dx++)
                {
                    int xx = Math.Clamp(x + dx, 0, w - 1);
                    acc += src[row + xx] * k1d[dx + kr];
                }
                tmp[row + x] = acc;
            }
        }
        // 垂直
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                double acc = 0;
                for (int dy = -kr; dy <= kr; dy++)
                {
                    int yy = Math.Clamp(y + dy, 0, h - 1) * w;
                    acc += tmp[yy + x] * k1d[dy + kr];
                }
                dst[row + x] = acc;
            }
        }
    }

    private static double[] ToDouble(byte[] a)
    {
        var d = new double[a.Length];
        for (int i = 0; i < a.Length; i++) d[i] = a[i] / 255.0;
        return d;
    }

    /// <summary>边缘抗锯齿:只对边缘(3×3 局部对比度大)的像素向邻域均值靠拢,
    /// 磨平阶梯感;平坦区域完全不动,细节不糊。强度越大混合越多(最多 55%)。</summary>
    private static void ApplyEdgeSmoothInMemory(System.Drawing.Bitmap bmp, int strength)
    {
        int w = bmp.Width, h = bmp.Height;
        if (w < 3 || h < 3) return;
        double mix = strength / 100.0 * 0.55;
        var rect = new System.Drawing.Rectangle(0, 0, w, h);
        var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadWrite,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            int n = w * h;
            var r = new byte[n];
            var g = new byte[n];
            var b = new byte[n];
            unsafe
            {
                byte* p0 = (byte*)data.Scan0.ToPointer();
                for (int y = 0; y < h; y++)
                {
                    byte* row = p0 + y * stride;
                    int idx = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int i = x * 4;
                        b[idx + x] = row[i];
                        g[idx + x] = row[i + 1];
                        r[idx + x] = row[i + 2];
                    }
                }
            }
            EdgeSmoothChannel(r, w, h, mix);
            EdgeSmoothChannel(g, w, h, mix);
            EdgeSmoothChannel(b, w, h, mix);
            unsafe
            {
                byte* p0 = (byte*)data.Scan0.ToPointer();
                for (int y = 0; y < h; y++)
                {
                    byte* row = p0 + y * stride;
                    int idx = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int i = x * 4;
                        row[i] = b[idx + x];
                        row[i + 1] = g[idx + x];
                        row[i + 2] = r[idx + x];
                    }
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>单通道边缘平滑:边缘像素 = 原值 + (3×3 均值 - 原值) × mix;局部对比度低于阈值视为平坦区,不动。</summary>
    private static void EdgeSmoothChannel(byte[] src, int w, int h, double mix)
    {
        const int edgeThreshold = 16;   // 中心与邻域最大差超过该值才算边缘
        var orig = new byte[src.Length];
        Buffer.BlockCopy(src, 0, orig, 0, src.Length);
        for (int y = 0; y < h; y++)
        {
            int rowBase = y * w;
            for (int x = 0; x < w; x++)
            {
                int center = orig[rowBase + x];
                int sum = 0, maxDiff = 0, cnt = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int yy = Math.Clamp(y + dy, 0, h - 1) * w;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = Math.Clamp(x + dx, 0, w - 1);
                        int v = orig[yy + xx];
                        sum += v;
                        cnt++;
                        int d = v > center ? v - center : center - v;
                        if (d > maxDiff) maxDiff = d;
                    }
                }
                if (maxDiff < edgeThreshold) continue;   // 平坦区:不动
                int mean = sum / cnt;
                int outV = center + (int)Math.Round((mean - center) * mix);
                src[rowBase + x] = (byte)Math.Clamp(outV, 0, 255);
            }
        }
    }

    /// <summary>
    /// 用 WinRT 图像解码器把任意图片转码为标准 8 位 PNG(临时目录)。
    /// 用于引擎解码失败的输入(部分 PNG/特殊编码),转码后重试处理。
    /// </summary>
    public static async Task<string> ConvertToStandardPngAsync(string input)
    {
        var outPath = Path.Combine(EngineService.TempRoot, $"imgup_conv_{Guid.NewGuid():N}.png");
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(input);
        using var stream = await file.OpenReadAsync();
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        // 显式转成标准 8 位 RGBA(不取文件原生格式,16 位等特殊编码才能被引擎读取)
        var pixels = await decoder.GetPixelDataAsync(
            Windows.Graphics.Imaging.BitmapPixelFormat.Rgba8,
            Windows.Graphics.Imaging.BitmapAlphaMode.Straight,
            new Windows.Graphics.Imaging.BitmapTransform(),
            Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation,
            Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
        using var mem = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
            Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, mem);
        encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Rgba8,
            Windows.Graphics.Imaging.BitmapAlphaMode.Straight,
            decoder.PixelWidth, decoder.PixelHeight, decoder.DpiX, decoder.DpiY,
            pixels.DetachPixelData());
        await encoder.FlushAsync();
        mem.Seek(0);
        using var fs = File.Create(outPath);
        await mem.AsStreamForRead().CopyToAsync(fs);
        return outPath;
    }

    /// <summary>不小于 n 的最小 2 的幂(waifu2x 只接受 2 的幂倍数)。
    /// 非 2 的幂倍率(如 3x/1.5x)用「更高倍数放大再缩回」,比「低倍数放大再拉伸」更清晰(不吞画质)。</summary>
    internal static int CeilPowerOfTwo(double n) => AlhPro.Core.PathUtil.CeilPowerOfTwo(n);

    /// <summary>把图片高保真缩放到精确尺寸后写回 outputPath(保持 PNG 格式)。
    /// 源 Bitmap 持有文件句柄,须先释放再覆盖,故先写临时文件(先写临时、src 释放后再覆盖目标)。</summary>
    public static void ResizeImageTo(string path, string outputPath, int width, int height)
    {
        var guid = Guid.NewGuid().ToString("N");
        var tmp = Path.Combine(EngineService.TempRoot, $"imgup_resize_{guid}.png");
        var tmpJpg = Path.Combine(EngineService.TempRoot, $"imgup_resize_{guid}.jpg");
        // 按目标扩展名保存:JPG 目标用 WinRT 编码(否则 PNG 字节装进 .jpg 文件,格式契约被破坏,且 System.Drawing JPG 会色偏)
        bool isJpg = outputPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                     outputPath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);
        try
        {
            int w = Math.Max(1, width);
            int h = Math.Max(1, height);
            try
            {
                using (var src = new System.Drawing.Bitmap(path))
                {
                    using var dst = new System.Drawing.Bitmap(w, h);
                    using (var g = System.Drawing.Graphics.FromImage(dst))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                        g.DrawImage(src, 0, 0, w, h);
                    }
                    // 【修复 全黑帧】JPG 分支此前直接写 outputPath——视频流水线里 outputPath==path,而 src Bitmap 仍持有该文件句柄,
                    // File.Create 触发共享冲突 → 走 catch → 每帧都被换成深灰占位。改成也先写临时文件,src 释放后再覆盖目标。
                    if (isJpg) SaveJpegViaWinRT(dst, tmpJpg);
                    else dst.Save(tmp, System.Drawing.Imaging.ImageFormat.Png);
                }
                // 此处 src/dst 已释放,可安全覆盖目标
                if (isJpg) File.Move(tmpJpg, outputPath, overwrite: true);
                else File.Copy(tmp, outputPath, overwrite: true);
            }
            catch (Exception ex)
            {
                // 【兜底】源帧损坏/0字节/非图像(引擎偶发写出坏帧或磁盘不足截断)→ GDI+ 读源抛
                // "Parameter is not valid"。此时【不能删帧/不跳过】——合帧按编号连续读,缺帧会造成编号断档 → ffmpeg 提前停止/输出截断。
                string head = ex.Message.Split('\n')[0];
                if (head.Length > 70) head = head[..70];
                AppLogger.Warn($"⚠ 缩回:源帧损坏({Path.GetFileName(path)})——{head};已用同尺寸占位帧替代,合帧不中断");
                using var ph = new System.Drawing.Bitmap(w, h);
                using (var g = System.Drawing.Graphics.FromImage(ph))
                {
                    g.Clear(System.Drawing.Color.FromArgb(24, 24, 24));   // 深灰,avoid 黑帧被误判为损坏
                }
                if (isJpg) SaveJpegViaWinRT(ph, tmpJpg);
                else ph.Save(tmp, System.Drawing.Imaging.ImageFormat.Png);
                if (isJpg) File.Move(tmpJpg, outputPath, overwrite: true);
                else File.Copy(tmp, outputPath, overwrite: true);
            }
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* 清理失败忽略 */ }
            try { File.Delete(tmpJpg); } catch { /* 清理失败忽略 */ }
        }
    }

    /// <summary>把图片按 factor 高保真缩放后写回原路径(保持 PNG 格式)。
    /// 注意:源 Bitmap 持有文件句柄,须先释放再覆盖,故先写临时文件。</summary>
    private static void ResizeImage(string path, string outputPath, double factor)
    {
        var tmp = Path.Combine(EngineService.TempRoot, $"imgup_resize_{Guid.NewGuid():N}.png");
        try
        {
            using (var src = new System.Drawing.Bitmap(path))
            {
                int w = Math.Max(1, (int)Math.Round(src.Width * factor));
                int h = Math.Max(1, (int)Math.Round(src.Height * factor));
                using var dst = new System.Drawing.Bitmap(w, h);
                using (var g = System.Drawing.Graphics.FromImage(dst))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                    g.DrawImage(src, 0, 0, w, h);
                }
                // 按输出扩展名保存:JPG 目标写 JPG(否则 PNG 字节装进 .jpg 文件,格式契约被破坏)
                if (outputPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                    outputPath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                {
                    SaveJpegViaWinRT(dst, tmp);   // 用 WinRT 编码,避免 System.Drawing JPG 色偏
                }
                else
                {
                    dst.Save(tmp, System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            File.Copy(tmp, outputPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* 清理失败忽略 */ }
        }
    }

    /// <summary>
    /// 区域放大:先从原图裁剪 (x,y,w,h) 像素区域,再放大输出(只输出选区放大图)。
    /// 坐标会自动做越界保护。
    /// </summary>
    public static async Task<string> UpscaleRegionAsync(
        string input, string output, int x, int y, int w, int h,
        string engine, string model, int scale, int noise,
        int gpuId, bool tta,
        IProgress<(int pct, string msg)>? progress = null,
        CancellationToken ct = default)
    {
        var tmp = Path.Combine(EngineService.TempRoot, $"imgup_crop_{Guid.NewGuid():N}.png");
        try
        {
            progress?.Report((0, "裁剪选区..."));
            await Task.Run(() =>
            {
                using var src = new System.Drawing.Bitmap(input);
                // 越界保护
                x = Math.Clamp(x, 0, Math.Max(0, src.Width - 1));
                y = Math.Clamp(y, 0, Math.Max(0, src.Height - 1));
                w = Math.Clamp(w, 1, src.Width - x);
                h = Math.Clamp(h, 1, src.Height - y);
                using var cropped = src.Clone(
                    new System.Drawing.Rectangle(x, y, w, h), src.PixelFormat);
                cropped.Save(tmp, System.Drawing.Imaging.ImageFormat.Png);
            }, ct).ConfigureAwait(false);

            return await UpscaleAsync(tmp, output, engine, model,
                scale, noise, gpuId, tta, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* 清理失败忽略 */ }
        }
    }
}
