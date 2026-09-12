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
    public static string DescribeNcnnVerdicts() => DescribeNcnnVerdicts(AppSettings.GpuIndex);

    /// <summary>同上,但显式指定 GPU 编号。诊断包导出时用 —— 写侧(处理任务)用的是
    /// ResolveEngineGpu 解析出来的编号,读侧若固定用 AppSettings.GpuIndex,在"设备表还没枚举"的窗口里
    /// 会查错键,于是明明测过也显示"未测"。两边必须用同一个编号。</summary>
    public static string DescribeNcnnVerdicts(int gpuId)
    {
        try
        {
            var parts = new System.Collections.Generic.List<string>();
            foreach (var eng in new[] { "realesrgan", "waifu2x" })
            {
                var v = TryGetNcnnVerdict(eng, gpuId);
                parts.Add($"{EngineId(eng)}={(v.HasValue ? (v.Value ? "实测可用→走 ncnn" : "实测不可用→走 ONNX") : "未测(首次处理时自动实测)")}");
            }
            return string.Join(" ", parts);
        }
        catch { return "未测"; }
    }

    /// <summary>ncnn 探测结论落盘文件的路径(诊断包要把这个文件原样带上,供作者核查"测了什么、什么时间、什么键")。
    /// 此前诊断包只收集 settings 目录下的 *.json,而这个文件是 .txt → 被静默漏掉,作者只能看到报告里的"未测"。</summary>
    public static string NcnnProbeCacheFilePath => NcnnProbeCacheFile;

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
    /// gpuId&lt;0(用户选 CPU)直接返回 false:ncnn CPU 模式在 50 系上有崩溃 bug(实测 exit -1073741819)。
    /// <param name="force">true = 忽略快速通道与缓存,无条件真机重测一次(仅"导出诊断包"用)。
    /// 【为什么需要它】诊断报告一直显示"未测"有两个独立原因:①报告文本是【按版本缓存的快照】,
    /// 生成于任何探测之前、之后永不刷新;②纯 NVIDIA 非 Blackwell 机型走快速通道**从不落盘结论**。
    /// 导出诊断包时必须拿到"这一刻的真实结论",所以 force 会绕过 ①的缓存短路 与 ②的快速通道,
    /// 仍然复用同一套生产帧探测口径(不新增第二套判据)。</param>
    public static async Task<bool> EnsureNcnnProbeAsync(string engine, int gpuId, string? model, CancellationToken ct,
        bool force = false)
    {
        if (gpuId < 0) return false;
        // 【快速通道:纯 NVIDIA 非 Blackwell + 机内没有非 NVIDIA 显卡 → 不探测,直接判可用】
        // 理由:①这套组合从无"ncnn-Vulkan 跑不通"的实测证据(实测出问题的是 50 系、AMD、无独显);
        // ②一次探测要 ~15 秒,而图片路径上一个任务可能总共只要 2 秒 —— 不能先白等 15 秒;
        // ③真出问题还有引擎自身的"输出全黑 → 该设备判不可用 + 降级链"兜底(见 IsEngineGpuUsableAsync)。
        // 50 系 / AMD / Intel 核显一律照旧真机探测,该走的自适应一点不少。
        if (!force && !IsBlackwellGpu() && !HasNonNvidiaGpu()) return true;
        if (!force)
        {
            var cached = TryGetNcnnVerdict(engine, gpuId);
            if (cached.HasValue)
            {
                AppLogger.Info($"[探测] {engine} GPU({gpuId}/{SafeDeviceName(gpuId)})沿用已缓存结论:" +
                    (cached.Value ? "ncnn-Vulkan 可用 → 走 ncnn(不重复试跑)" : "ncnn-Vulkan 不可用 → 走 ONNX(不每批重试)"));
                return cached.Value;
            }
        }
        else
        {
            AppLogger.Info($"[探测] {engine} GPU({gpuId})强制真机重测(诊断包导出:绕过快速通道与缓存,拿到当前真实结论)");
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
    public static async Task<bool> EnsureRifeNcnnProbeAsync(string rifeExe, string model, int gpuId, CancellationToken ct,
        int frameW = 0, int frameH = 0)
    {
        if (gpuId < 0 || string.IsNullOrEmpty(rifeExe)) return false;
        var key = RifeProbeKey(rifeExe, model, frameW, frameH);
        var cached = TryGetNcnnVerdict(key, gpuId);
        if (cached.HasValue)
        {
            AppLogger.Info($"[探测] 补帧 {model} GPU({gpuId})沿用已缓存结论:" + (cached.Value ? "可用 → 走 ncnn-Vulkan" : "不可用 → 走 ONNX"));
            return cached.Value;
        }
        AlhPro.Core.ProbeFailureKind failKind = AlhPro.Core.ProbeFailureKind.None;
        string failDetail = "";
        bool ok = await IsRifeGpuUsableAsync(rifeExe, model, gpuId, ct,
            (k, d) => { failKind = k; failDetail = d; },
            frameW <= 0 ? 320 : frameW, frameH <= 0 ? 240 : frameH).ConfigureAwait(false);
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

    /// <summary>补帧(RIFE)探测结论的缓存键:把【引擎二进制指纹】与【探测帧尺寸档】都编进 key。
    /// 【为什么要带引擎指纹】换引擎(旧的 rife-ncnn-vulkan.exe ↔ 2026 重编版)等于换了实现:
    /// 二进制文件名/体积必然不同,于是 key 不同 —— 老结论不会被套到新二进制上。
    /// 否则最坏情况正是"新引擎第一次跑就沿用旧结论、真正的探测永不发生"。
    /// 【为什么要带尺寸档】探测按生产帧尺寸做(小图能跑 ≠ 真帧能跑),所以 1080p 上测出来的结论
    /// 不能拿去给 4K 任务背书。只分三档(SD/FHD/UHD)而不是每个分辨率一个 key:档位有限,
    /// 不会变成"每换一个分辨率就重测一遍";同一档内复用,一次探测管到底。
    /// 指纹只取【文件名 + 字节数】而不含时间戳:文件没变就不该因为"重装一次"白等一遍探测。</summary>
    private static string RifeProbeKey(string rifeExe, string model, int frameW, int frameH)
    {
        string id;
        try
        {
            var fi = new FileInfo(rifeExe);
            id = fi.Exists ? $"{fi.Name}/{fi.Length}" : Path.GetFileName(rifeExe);
        }
        catch { id = Path.GetFileName(rifeExe); }
        long area = (long)(frameW <= 0 ? 320 : frameW) * (frameH <= 0 ? 240 : frameH);
        string bucket = area >= 3840L * 2160 ? "uhd" : area >= 1920L * 1080 ? "fhd" : "sd";
        return $"rife:{id}:{bucket}:{model}";
    }

    /// <summary>【本机实测优先】某引擎的 ncnn-Vulkan 在本机该 GPU 上是否算"风险"(=该走 ONNX)。
    /// ①有实测结论(EnsureNcnnProbeAsync / EnsureRifeNcnnProbeAsync 写入)→ 一律以实测为准:
    ///    实测可用 → false(走 ncnn),实测不可用 → true(走 ONNX);
    /// ②没测过 → 回退保守启发式(treatBlackwellAsRiskyWithoutProbe 决定 50 系算不算风险)。
    /// 【2026-09-12 收紧:只对旧引擎保留"50 系未测即判风险"】Blackwell 那个坑的根因是 **2022 版 ncnn** 的
    /// Vulkan 驱动问题;本仓库现在用的 realesrgan / rife 都是 2025/2026 ncnn 重编版(指纹含 robustness2,旧版为 0),
    /// 并且在 RTX 5060 Laptop 上**实测补帧与超分探测都通过**。再按显卡型号预判,只会让"没走探测的路径"
    /// 白白退回慢得多的 ONNX(用户感受就是"50 系上超分/补帧莫名很慢",且没有任何提示)。
    /// 所以:引擎是 2026 重编版 → 不再按型号预判(交给真机探测与缓存结论);仍是旧引擎 → 保持原保守行为不变。</summary>
    public static bool NcnnGpuRisky(string engine, int gpuId, bool treatBlackwellAsRiskyWithoutProbe = true)
    {
        try
        {
            var v = TryGetNcnnVerdict(engine, gpuId);
            if (v.HasValue) return !v.Value;
        }
        catch { }
        // 2026 重编版引擎:不再按"50 系"预判(见上面注释);旧引擎保持原保守行为
        return OldNcnnGpuRiskyHeuristic(treatBlackwellAsRiskyWithoutProbe && !EngineIsRebuilt2026(engine));
    }

    /// <summary>该引擎是不是本仓库用 2025/2026 ncnn 源码重编的那一份(文件名带 2026)。
    /// 依据:`ENGINE_REALESRGAN_REBUILD.md` 与 `RIFE_ENGINE_AUDIT.md` —— 重编版静态指纹含
    /// `VK_EXT_robustness2`/`VK_KHR_cooperative_matrix`(旧版为 0),这是"50 系 Blackwall 毒点已消失"的可核对标志。
    /// 只用来决定"要不要按显卡型号预判风险",不参与任何用户可见文案。</summary>
    private static bool EngineIsRebuilt2026(string engine)
    {
        try
        {
            string? exe = engine switch
            {
                "realesrgan" => FindRealEsrgan2026(),
                "rife" => VideoService.RifePath,
                _ => null,   // waifu2x 上游 20250915 版走它自己的参数(默认就不按 Blackwell 预判)
            };
            return exe != null && Path.GetFileName(exe).Contains("2026", StringComparison.Ordinal);
        }
        catch { return false; }
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
    /// 逐个做真机探测,返回第一个【实际可用】的引擎编号;-1=全部不可用。
    /// 不只按名字推荐——名字对但驱动/编号/引擎支持有问题时,实测能拦住(真机:RTX5060 三卡机选中 Intel 核显)。
    /// 【口径说明(2026-09-12 自检)】这里的探测是 **320×240 小图的"活性检查"**(fullFrame:false),
    /// **不等于"可用性"**:本项目实测过的失败形态正是"小图能过、生产帧尺寸才静默出空帧/黑帧"。
    /// 所以日志与文案一律写"320×240 活性检查",别再说"1×1 可用";真正的可用性结论来自
    /// `EnsureNcnnProbeAsync`(生产帧尺寸 1080×1920 + 带状黑判据)。
    /// 为什么不升级成生产帧口径:首次自检要给每张候选卡多等一次生产帧探测(实测 x4plus 在 4060 上 24.1 秒),
    /// 该自检只是"挑一张卡",没必要这么贵 —— 先留着,把话说准。</summary>
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
                AppLogger.Info(d.Id + ": " + d.Name + " → " + (ok ? "320×240 活性检查通过" : "不可用"));
                if (ok) return d.Id;
            }
            // ② 独显全不可用 → 核显作"底牌"兜底(核显也是计算设备,能用就用,总比报错强)
            var igpu = devs.Where(d => GpuInfo.ScoreDeviceName(d.Name) == 0).OrderBy(d => d.Id).Take(2);
            foreach (var d in igpu)
            {
                bool ok = await IsEngineGpuUsableAsync("waifu2x", d.Id, ct).ConfigureAwait(false);
                AppLogger.Info(d.Id + ": " + d.Name + "(核显) → " + (ok ? "320×240 活性检查通过(兜底)" : "不可用"));
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
        // (视频页那份下拉 2026-09-12 按用户要求单独调过顺序并带序号迁移;图片页这份不动,避免动到图片预设里的序号。)
        // 【标签(2026-09-12 用户要求)】耗时档写「中」、不再写"轻量通用"(与视频页保持一致口径)。
        ("通用 · realesr-general-x4v3（5MB · 中）", "realesr-general-x4v3"),
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
        // 【管道等待必须给上限】与 VideoService.RunAsync/RunCaptureAsync 同一处修复(2026-09-12):
        // Kill 只是"请求终止";引擎进程若卡在内核态(显卡驱动挂死),它不会真退出、stdout/stderr 也不会 EOF,
        // 于是 `await Task.WhenAll(drainOut, drainErr)` 会**永远等下去** —— 看门狗明明已经打了"强制终止",
        // 界面却再也不动(用户只能强杀软件)。这里最多等 5 秒,超时就放弃输出继续走降级链。
        bool drained = true;
        var drainAll = Task.WhenAll(drainOut, drainErr);
        if (await Task.WhenAny(drainAll, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false) != drainAll)
        {
            drained = false;
            AppLogger.Warn($"⚠ 引擎进程未能在 5 秒内退出(已请求强制终止,pid={p.Id},阶段 {stage})"
                + "——放弃等待其输出并继续处理;若它仍占着显卡/文件,建议结束软件后重启。");
        }
        watchdog.Dispose();
        watchCts.Cancel();
        try { await watchTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        App.ActiveProcesses.Unregister(p.Id);
        // 进程没退出时不许读 ExitCode(会抛 InvalidOperationException,把"卡死"伪装成别的错),
        // 也不许走"回退重跑"(它可能还在往同一个输出目录写文件,重跑会与它交叉写出错结果)。
        if (!p.HasExited || !drained)
            throw new InvalidOperationException(killReason
                ?? $"引擎进程在收到终止请求后仍未退出({stage});它可能仍占用显卡或临时目录,已放弃本批处理。"
                   + "请结束软件后重启再试。");
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
    /// (初始化即崩 ≠ 出图但坏帧;前者在 Blackwell 上是 NVIDIA 驱动缺陷,后者不许甩给驱动)。
    /// <param name="frameW"/><param name="frameH">探测用的帧尺寸。默认 320×240(给不掌握视频尺寸的调用方兜底);
    /// 【为什么必须能传生产尺寸】实测过的漏检形态正是"小图能跑、真分辨率上崩/静默出坏帧":320×240 只占
    /// 1080p 的 1/27 像素、4K 的 1/108,显存与着色器分块压力都差一个量级 —— 在小图上探测通过,不等于真帧上能跑。
    /// 视频路径知道源分辨率,就必须按源分辨率探(见 VideoService 调用处)。</summary>
    public static async Task<bool> IsRifeGpuUsableAsync(string rifeExe, string model, int gpuId, CancellationToken ct, Action<AlhPro.Core.ProbeFailureKind, string>? onFailure, int frameW = 320, int frameH = 240)
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
            // 尺寸兜底:调用方可能传来 0/负数(分辨率未探到)或极端值,夹到合理范围,免得探测本身成为故障源。
            int pw = Math.Clamp(frameW <= 0 ? 320 : frameW, 64, 8192);
            int ph = Math.Clamp(frameH <= 0 ? 240 : frameH, 64, 8192);
            var tmp = Path.Combine(EngineService.TempRoot, $"rife_probe_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tmp);
            var a = Path.Combine(tmp, "a.png");
            var b = Path.Combine(tmp, "b.png");
            var o = Path.Combine(tmp, "out.png");
            try
            {
                // 【探测输入:水平渐变 + 同一渐变右移 2 像素】(2026 换,原来的是"纯黑帧 → 纯白帧"硬切)
                // 【为什么必须换】黑→白硬切对光流是【病态输入】:没有真实运动、只有内容突变,正确输出该是什么
                //  没有定义。实测(本机 RTX 4060 Laptop / rife-v4.6,新老两代引擎一致)那种输入下"中间帧"均值
                //  在 320×240 上只有 2.4(近黑!)、1080p 74.4、4K 90.3 —— 随分辨率乱变。于是旧探测只能查
                //  "通道是否均衡",任何"出黑帧/出糊帧"的损坏都查不出来(本次要堵的盲区)。
                // 换成"渐变 + 右移 2 像素"(=真实的小位移)后,正确输出有确定预期:均值≈两输入均值、
                //  方差≈输入方差。实测 13 个模型 × 新老两代引擎:均值偏差 ≤0.24、方差比 1.00(见 _qa/rifeprobe)。
                // 【必须显式指定 Format24bppRgb —— 这里踩过坑】`new Bitmap(w,h)` 默认是 32bppArgb(带 alpha),
                //  而该引擎的前端对带 alpha 的 PNG 会读错通道:实测同一对渐变图,24bpp 进 → 出帧均值 126.70、
                //  方差 5410(与输入 127.00/5424 一致);换成 32bpp 进 → 出帧直接变成乱码(横剖面 32/158/186/54…
                //  不再是渐变)、均值 158.96、方差 2120。这不是引擎故障,是"喂了它不认的像素格式";
                //  生产帧全是 JPG(拆帧就是 JPG)与 24bpp PNG,所以生产不受影响 —— 但探测图必须与生产同格式,
                //  否则探测会把自己的图当成引擎故障、把可用设备误判成不可用(第一次跑就踩到,日志里是"亮度不对")。
                var px = System.Drawing.Imaging.PixelFormat.Format24bppRgb;
                using (var bmp = new System.Drawing.Bitmap(pw, ph, px))
                {
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                    using (var br = new System.Drawing.Drawing2D.LinearGradientBrush(
                               new System.Drawing.Rectangle(0, 0, pw, ph), System.Drawing.Color.Black, System.Drawing.Color.White, 0f))
                        g.FillRectangle(br, 0, 0, pw, ph);
                    bmp.Save(a, System.Drawing.Imaging.ImageFormat.Png);
                    using var shifted = new System.Drawing.Bitmap(pw, ph, px);
                    using (var g2 = System.Drawing.Graphics.FromImage(shifted))
                    {
                        g2.Clear(System.Drawing.Color.Black);   // 左边缘那 2 列:渐变左端本来就是纯黑,无缝
                        g2.DrawImage(bmp, new System.Drawing.Rectangle(2, 0, pw, ph));
                    }
                    shifted.Save(b, System.Drawing.Imaging.ImageFormat.Png);
                }
                // 【放宽+重试】原 5 秒超时对首次运行(编译着色器)太紧,正常独显被误判→整段补帧被切 ONNX/CPU。
                // 改 10 秒起步;失败再重试一次(再失败才判不可用),避免瞬时抽风误判。
                // 【超时按面积给】实测(RTX 4060 Laptop,rife-v4.6 单对插值):320×240 冷启动 8.3s、热 1.5s;
                // 1080p 1.4~3.0s;4K 2.8~4.4s;最重的 rife-anime/rife 系在 1080p 上热态也要 10s 上下。
                // ≥1080p 给 25 秒(2 倍余量);<1080p 给 15 秒(冷启动 8.3s 的 1.8 倍)。
                // 25 秒仍远小于 8 分钟看门狗 —— 宁可多等一次(结论会缓存),也不要"探测误判 → 整段补帧被切走"。
                int timeoutSec = (long)pw * ph >= 1920L * 1080 ? 25 : 15;
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
                    waitCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
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
                        // 出帧 ≠ 出对帧:某些设备(真机:D3D12 转译层)exit 0 且出图,但插值结果是整帧红噪点;
                        // 还有"根本没插、把输入帧抄回来"(全黑/全白帧)—— 三判据一起看,见 ProbeOutputIsSane。
                        if (ok && !ProbeOutputIsSane(o, a, b, out var why))
                        {
                            AppLogger.Warn($"[探测] RIFE {model} GPU(-g {gpuId})出帧但坏帧({why})——按不可用处理");
                            Note(AlhPro.Core.ProbeFailureKind.DefectiveFrame, "出帧但坏帧:" + why);
                            ok = false;
                        }
                        if (ok)
                        {
                            AppLogger.Info($"[探测] RIFE {model} GPU(-g {gpuId})可用({pw}×{ph} 出帧,耗时远低于 {timeoutSec} 秒上限)");
                            return true;
                        }
                        AppLogger.Warn($"[探测] RIFE {model} GPU(-g {gpuId})第 {attempt} 次不可用(exit={p.ExitCode}/无输出)" + (attempt < 2 ? ",重试一次..." : "——将自动改用 CPU/ONNX 补帧"));
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        AppLogger.Warn($"[探测] RIFE {model} GPU(-g {gpuId}) {timeoutSec} 秒无响应(疑似 hang),按不可用处理");
                        try { p.Kill(entireProcessTree: true); } catch { }
                        onFailure?.Invoke(AlhPro.Core.ProbeFailureKind.Hang, $"{timeoutSec} 秒无响应");
                        return false;   // 超时=真 hang,不重试(重试只会再白等一个超时);仅快速失败(非超时)才走重试
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

    /// <summary>探测帧健全性:输入是"水平渐变 + 同一渐变右移 2 像素"两帧,正确插值结果必然
    /// 【均值≈两输入均值、方差≈输入方差、三通道均衡、不是近黑/带状近黑】。
    /// 【四判据,任一不过 = "出帧但坏帧"】(2026 扩充;旧版只查通道均衡,实测有两个盲区)
    ///  ①通道均衡 <see cref="AlhPro.Core.DeviceRouting.IsAchromatic"/>:抓"D3D12 转译层整帧红噪点";
    ///  ②非缺陷帧 <see cref="AlhPro.Core.FrameInspect.IsDefectiveFrame"/>(整帧 ≥95% 近黑【或】任一 1/3 主条带
    ///    ≥95% 近黑):抓"整帧黑"与"下半 2/3 黑"的条带损坏(近黑帧三通道极差是 0,①对它完全无感);
    ///  ③均值对得上 <see cref="AlhPro.Core.DeviceRouting.InterpProbeMeanTolerance"/>:抓"输出与正确结果亮度
    ///    根本不是一回事"(黑帧、白帧、亮度被拉平);
    ///  ④结构没被抹平 <see cref="AlhPro.Core.DeviceRouting.InterpProbeStructureRatio"/>:抓"均值对得上、
    ///    通道也均衡,但整帧被抹成一块平的"(任何常数灰帧)。①②③都抓不到它,只有方差能。
    /// 【口径】三张图的像素都按 <see cref="AlhPro.Core.FrameInspect.ForEachSample"/> 的同一采样网格取,
    /// 与 ②的条带几何严格一致;预期值由输入帧自己算出来(不写死 127),换探测输入也不用改判据。
    /// 【读图异常时放行(返回 true)】宁可放过,不因探测代码的问题把可用设备判死 —— 与旧实现同口径。
    /// reason 只在返回 false 时有意义,用于写日志/诊断包(说清是哪一条判据不过)。</summary>
    private static bool ProbeOutputIsSane(string outPng, string inAPng, string inBPng, out string reason)
    {
        reason = "";
        if (!SamplePngStats(outPng, out double omR, out double omG, out double omB, out double oVar,
                out var oSum, out int oN, out int w, out int h)) return true;
        if (oN <= 0) return true;

        if (!AlhPro.Core.DeviceRouting.IsAchromatic(omR, omG, omB))
        {
            reason = $"颜色损坏(应出灰阶却通道失衡 R{omR:0.#}/G{omG:0.#}/B{omB:0.#})";
            return false;
        }
        if (AlhPro.Core.FrameInspect.IsDefectiveFrame(oSum, oN, w, h))
        {
            reason = "缺陷帧(近黑/带状近黑)";
            return false;
        }
        // 预期值 = 两个输入帧自己的均值/方差。读不到输入帧(异常)就跳过 ③④ —— 只留"不误杀"的那两条。
        if (SamplePngStats(inAPng, out double amR, out double amG, out double amB, out double aVar,
                out _, out int aN, out _, out _) && aN > 0 &&
            SamplePngStats(inBPng, out double bmR, out double bmG, out double bmB, out double bVar,
                out _, out int bN, out _, out _) && bN > 0)
        {
            double expMean = ((amR + amG + amB) + (bmR + bmG + bmB)) / 6.0;
            double expVar = (aVar + bVar) / 2.0;
            if (!AlhPro.Core.DeviceRouting.IsPlausibleInterpOf(omR, omG, omB, oVar, expMean, expVar))
            {
                double oMean = (omR + omG + omB) / 3.0;
                reason = Math.Abs(oMean - expMean) > AlhPro.Core.DeviceRouting.InterpProbeMeanTolerance
                    ? $"亮度不对(均值 {oMean:0.#},正确结果应≈{expMean:0.#})"
                    : $"画面被抹平(方差 {oVar:0.#},输入方差 {expVar:0.#} —— 插值结果不该是一块平的)";
                return false;
            }
        }
        return true;
    }

    /// <summary>把一张 PNG 按 <see cref="AlhPro.Core.FrameInspect.ForEachSample"/> 的采样网格读成统计量:
    /// 三通道均值、全部通道样本的方差、以及每采样点的 RGB 和(给 IsDefectiveFrame 的条带判定用)。
    /// 读图/解码失败返回 false(调用方按"拿不准就放过"处理)。
    /// 【为什么采样而不是逐像素全扫】判定逻辑(均值/方差/条带比例)在采样口径下等价,而 4K 从 830 万像素
    /// 降到约 1800 个采样点;三张图加起来仍是毫秒级。采样网格与 FrameInspect 共用,不会与条带几何错位。</summary>
    private static bool SamplePngStats(string png,
        out double meanR, out double meanG, out double meanB, out double variance,
        out int[] sumRgb, out int count, out int width, out int height)
    {
        meanR = meanG = meanB = variance = 0;
        sumRgb = Array.Empty<int>();
        count = 0; width = 0; height = 0;
        try
        {
            using var bmp = new System.Drawing.Bitmap(png);
            width = bmp.Width; height = bmp.Height;
            if (width <= 0 || height <= 0) return false;
            AlhPro.Core.FrameInspect.SampleGrid(width, height, out int rows, out int cols);
            if (rows <= 0 || cols <= 0) return false;
            // 【必须是局部数组】下面要在 lambda 里写它,而 out 参数不允许在匿名方法中使用(CS1628)。
            var sums = new int[rows * cols];

            var rect = new System.Drawing.Rectangle(0, 0, width, height);
            var d = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            long sr = 0, sg = 0, sb = 0, sq = 0;
            int n = 0;
            try
            {
                var scan = d.Scan0;
                int stride = d.Stride;
                // 24bpp 字节序 BGR;采样点由 ForEachSample 给出,与 SampleGrid 同几何。
                AlhPro.Core.FrameInspect.ForEachSample(width, height, (x, y) =>
                {
                    int off = y * stride + x * 3;
                    int b = System.Runtime.InteropServices.Marshal.ReadByte(scan, off);
                    int g = System.Runtime.InteropServices.Marshal.ReadByte(scan, off + 1);
                    int r = System.Runtime.InteropServices.Marshal.ReadByte(scan, off + 2);
                    sb += b; sg += g; sr += r;
                    if (n < sums.Length) sums[n] = r + g + b;
                    sq += (long)r * r + (long)g * g + (long)b * b;
                    n++;
                });
            }
            finally { bmp.UnlockBits(d); }
            if (n <= 0) return false;
            count = n;
            sumRgb = sums;
            meanR = (double)sr / n; meanG = (double)sg / n; meanB = (double)sb / n;
            double m1 = (meanR + meanG + meanB) / 3.0;
            double m2 = (double)sq / (3.0 * n);
            variance = Math.Max(0, m2 - m1 * m1);
            return true;
        }
        catch { return false; }
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
            // 【t 的语义(实测)】0 = 让引擎自己决定分块大小(auto);>0 = 显式指定分块边长。
            // 视频路径默认就用 auto(见下面调用点的注释与实测:auto 无接缝/无黑帧)。
            // 【为什么初始值是 0 而不是 tileSize】原来写 `preTiled ? 0 : tileSize`,但两个调用点的 lambda
            // 都把 `-t 0` 硬编码了、**根本没用这个 t** —— 于是下面"显存不足降半分块"的重试管线形同虚设:
            // t 从 512 减到 256/128 全是空转,命令一字未变,日志却写着"分块 512→256 重试"。
            // 在 8GB 小显存卡上(如 RTX 5060 Laptop)这等于把"救回来"变成"同参数白跑 3 次再降级"。
            // 现在:调用点必须用 {t}(正常路径 t=0 与旧行为逐字一致),OOM 时才真正改成显式小分块。
            int t = 0;
            int attempts = 0;
            while (true)
            {
                try
                {
                    await RunEngFallbackGpuAsync(exe, buildArgs(t), progress, ct, watchStage ?? "", watchTotal, watchDir, globalBaseFrames, globalTotalFrames).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (attempts < 3 && IsVramOom(ex))
                {
                    attempts++;
                    int prevT = t;
                    t = t == 0 ? 512 : Math.Max(64, t / 2);   // auto → 512 → 256 → 128:逐级真正降显存
                    AppLogger.Info($"⚠ 降级:显存不足(第 {attempts} 次,原因:{ex.Message.Split('\n')[0]}),分块 {(prevT == 0 ? "auto" : prevT.ToString())}→{t} 重试");
                    progress?.Report((0, $"⚠ 显存不足,自动降低分块 {(prevT == 0 ? "auto" : prevT.ToString())}→{t} 重试(第 {attempts} 次)..."));
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
        => ConvertPngToJpg(pngPath, jpgPath, quality, out nearBlack, 0);

    /// <summary>同上,并可在写 JPG 前顺带做一次「边缘抗锯齿」(edgeSmooth &gt; 0 时;0~100,与图片页同语义)。
    /// 【为什么把视频页的抗锯齿搬到 C#】视频页原先用 ffmpeg 的 sab 滤镜做这一档。2026-09-12 实测(4K JPG 序列):
    ///   加满 6 档后处理滤镜 = 4.88 秒/帧;把「边缘抗锯齿」一档去掉 = 0.10 秒/帧(**差约 48 倍**);
    ///   单个 sab 滤镜单独跑也要 0.90 秒/帧,串进链里更贵。
    /// 同样效果的 C# 实现(与图片页共用 ApplyEdgeSmoothInMemory)4K 单帧 1.19 秒,**多核并行后约 0.1 秒/帧**。
    /// 放在这里做还顺带白赚一次:本方法本来就要把超分后的 PNG 重编码成 JPG(降临时盘),
    /// 抗锯齿在同一张位图上做完再写,**不额外增加一次 JPG 重编码**。
    /// 数据:_qa\encbench(编码/滤镜实测)、_qa\imgpost(滤镜逐个计时)。</summary>
    public static void ConvertPngToJpg(string pngPath, string jpgPath, float quality, out bool nearBlack, int edgeSmooth)
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
        // 抗锯齿必须在黑帧采样【之后】做:先算缺陷帧再动画面,判定口径不受影响
        if (edgeSmooth > 0) ApplyEdgeSmoothInMemory(img, edgeSmooth);
        // 视频中间帧 JPG:直接走 System.Drawing(GDI,转 24bppRgb 规避色偏),不走 WinRT——
        // WinRT BitmapEncoder 在后台/非 UI 线程会系统性抛 HRESULT=0x88982F41(视频处理必失败),
        // 导致每次视频处理都刷"WinRT JPG 编码不可用"日志 + 白试一次。GDI 在后台线程可靠、不刷日志。
        SaveJpegViaGdi(img, jpgPath, quality);
    }

    /// <summary>对【已存在的 JPG】就地做一次「边缘抗锯齿」(写临时文件再替换,绝不半写坏原帧)。
    /// 用途:帧目录里本来就已经是 JPG 的帧(未超分/未补帧、或引擎直接给 JPG 的路径)也要吃到这一档,
    /// 否则同一个开关在不同管线分支下效果不一致。</summary>
    public static void ApplyEdgeSmoothToJpeg(string jpgPath, int strength, float quality)
    {
        var tmp = jpgPath + ".aa.jpg";
        try
        {
            using (var bmp = new System.Drawing.Bitmap(jpgPath))
            {
                ApplyEdgeSmoothInMemory(bmp, strength);
                SaveJpegViaGdi(bmp, tmp, quality);
            }
            File.Copy(tmp, jpgPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* 清理失败忽略 */ }
        }
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
    /// 后处理增强(可叠加,顺序从温和到强烈;此处顺序 = 下面 passes.Add 的真实执行顺序):
    /// 去雾 → 减少杂色(中值·按强度混合) → 保留细节(7×7 盒均值反锐化·保护平坦区) → 细节增强(3×3 高通微细节) →
    /// 清晰(大核反锐化·局部对比度) → 钝化蒙版(4 遍盒模糊反锐化·阈值 8 保护平坦区) → 去模糊(理查森-露西反卷积) →
    /// 边缘增强(Sobel 幅值 + 拉普拉斯符号偏置) → 锐化(小核反锐化·无阈值·最强) → 边缘抗锯齿(只磨边缘阶梯)。
    /// 【2026-09 核对】原注释漏了「去雾」与「细节增强」两项、且顺序与实际不符 —— 已按代码改正(仅注释,行为未变)。
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
        // 保留细节:真 CLAHE(分块直方图均衡 + 限幅)→ 提局部对比但天生不过冲(实测白边 0.000%~0.10%)
        if (detail > 0)       passes.Add(("保留细节", b => ApplyClaheInMemory(b, detail)));
        // 细节增强:3×3 高通 + 软门槛(低幅高频=噪声,按比例压掉,只放大成形的微纹理)
        if (detailEnhance > 0) passes.Add(("细节增强", b => ApplyHighFreqInMemory(b, detailEnhance)));
        // 清晰:大半径局部对比,但按【中调权重】缩放 —— 暗部/高光几乎不动(Lightroom Clarity 的本意)
        if (clarity > 0)      passes.Add(("清晰", b => ApplyClarityInMemory(b, clarity)));
        // 钝化蒙版:标准 USM(逐通道、阈值保护平坦区,弱噪声不被放大)
        if (usm > 0)          passes.Add(("钝化蒙版", b => ApplyUnsharpInMemory(b, usm / 100.0 * 1.4, 8, 4)));
        // 去模糊:真·理查森-露西反卷积(核与迭代都随强度连续变化)
        if (deblur > 0)       passes.Add(("去模糊", b => ApplyDeblurInMemory(b, deblur)));
        // 边缘增强:Sobel 边缘掩膜放缩加到原图(只提边,不糊内部)
        if (edge > 0)         passes.Add(("边缘增强", b => ApplyEdgeEnhanceInMemory(b, edge)));
        // 锐化:亮度域高通 + 【边缘加权】(成形边缘多给、平坦区与细纹理少给)→ 不出彩色描边
        if (sharpen > 0)      passes.Add(("锐化", b => ApplySharpenEdgeInMemory(b, sharpen)));
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

    /// <summary>保留细节:真 CLAHE(Contrast Limited Adaptive Histogram Equalization)。
    /// 做法:在【亮度】上分 8×8 块统计直方图 → 每块按 clip 限幅(超出的计数均摊回全直方图)→
    /// 得到 256 级映射表 → 像素取周围 4 块映射表的双线性插值 → 亮度变化按比例映射回 RGB(不动色相)。
    /// 【为什么换成它 —— 2026-09 实测】原实现自称"CLAHE 风格",实际是 7×7 盒均值反锐化,
    /// 与「锐化/清晰/钝化蒙版/细节增强」是同一个算子(改动图两两相关 0.83~0.98),叠起来只叠白边。
    /// 换成真 CLAHE 之后:①它与锐化族的改动图相关 ≈ **−0.02~−0.05(完全正交)**,是六个档里
    /// 唯一"提局部对比但天生不出白边"的(实测 over% 0.000~0.101,而同强度锐化族是 0.235~1.63);
    /// ②边缘几乎无损(压缩/Real-ESRGAN 基底 edgePSNR 23.19→23.34,干净 waifu2x 38.37→37.63)。
    /// 代价:它提的是"局部明暗层次"而不是拉普拉斯方差,所以 detail 指标基本不动(99%~101%)——
    /// 指标看不见它,得看图。强度 ×0.2 映射(实测不降压时 25 档就 −4.6dB,过猛)。
    /// 数据:_qa\imgpost\imgpost_sharpen.txt / imgpost_sharpen_overlap.txt。</summary>
    private static void ApplyClaheInMemory(System.Drawing.Bitmap bmp, int strength)
    {
        int w = bmp.Width, h = bmp.Height;
        const int tiles = 8;
        if (w < tiles * 2 || h < tiles * 2) return;
        double amount = Math.Clamp(strength / 100.0 * 0.2, 0.0, 1.0);
        const double clipFactor = 2.0;
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
            // ① 亮度
            var lum = new byte[n];
            for (int i = 0; i < n; i++)
                lum[i] = (byte)Math.Clamp((int)Math.Round(0.299 * r[i] + 0.587 * g[i] + 0.114 * b[i]), 0, 255);
            // ② 每块直方图 → 限幅 → 映射表
            int tw = (w + tiles - 1) / tiles, th = (h + tiles - 1) / tiles;
            var lut = new byte[tiles * tiles][];
            int clip = Math.Max(1, (int)(clipFactor * (tw * th) / 256.0));
            for (int ty = 0; ty < tiles; ty++)
            {
                for (int tx = 0; tx < tiles; tx++)
                {
                    int x0 = tx * tw, x1 = Math.Min(w, x0 + tw);
                    int y0 = ty * th, y1 = Math.Min(h, y0 + th);
                    var hist = new int[256];
                    int cnt = 0;
                    for (int y = y0; y < y1; y++)
                        for (int x = x0; x < x1; x++) { hist[lum[y * w + x]]++; cnt++; }
                    int excess = 0;
                    for (int i = 0; i < 256; i++) if (hist[i] > clip) { excess += hist[i] - clip; hist[i] = clip; }
                    if (cnt > 0 && excess > 0)
                    {
                        int share = excess / 256, rest = excess % 256;
                        for (int i = 0; i < 256; i++) hist[i] += share;
                        for (int i = 0; i < rest; i++) hist[i]++;
                    }
                    var map = new byte[256];
                    int acc = 0;
                    for (int i = 0; i < 256; i++)
                    {
                        acc += hist[i];
                        map[i] = (byte)Math.Clamp((int)Math.Round(acc * 255.0 / Math.Max(1, cnt)), 0, 255);
                    }
                    lut[ty * tiles + tx] = map;
                }
            }
            // ③ 双线性插值取映射值 → 按比例映射回 RGB → 与原图按强度混合
            for (int y = 0; y < h; y++)
            {
                double fy = (y - th / 2.0) / th;
                int ty0 = (int)Math.Floor(fy);
                double wy = fy - ty0;
                int tyA = Math.Clamp(ty0, 0, tiles - 1), tyB = Math.Clamp(ty0 + 1, 0, tiles - 1);
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    int v = lum[i];
                    double fx = (x - tw / 2.0) / tw;
                    int tx0 = (int)Math.Floor(fx);
                    double wx = fx - tx0;
                    int txA = Math.Clamp(tx0, 0, tiles - 1), txB = Math.Clamp(tx0 + 1, 0, tiles - 1);
                    double v00 = lut[tyA * tiles + txA][v], v01 = lut[tyA * tiles + txB][v];
                    double v10 = lut[tyB * tiles + txA][v], v11 = lut[tyB * tiles + txB][v];
                    double newY = (v00 * (1 - wx) + v01 * wx) * (1 - wy) + (v10 * (1 - wx) + v11 * wx) * wy;
                    double scale = newY / Math.Max(1.0, v);
                    int nr = (int)Math.Round(r[i] * scale);
                    int ng = (int)Math.Round(g[i] * scale);
                    int nb = (int)Math.Round(b[i] * scale);
                    r[i] = (byte)Math.Clamp((int)Math.Round(r[i] + (nr - r[i]) * amount), 0, 255);
                    g[i] = (byte)Math.Clamp((int)Math.Round(g[i] + (ng - g[i]) * amount), 0, 255);
                    b[i] = (byte)Math.Clamp((int)Math.Round(b[i] + (nb - b[i]) * amount), 0, 255);
                }
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
        finally { bmp.UnlockBits(data); }
    }

    /// <summary>清晰:大半径局部对比(8 遍盒模糊 ≈ 半径 7),但按【中调权重】缩放 ——
    /// 权重 = 1 − (|亮度−128|/140)²,中调满给、亮部/暗部几乎不动。这是 Lightroom Clarity 的本意:
    /// 提"通透感"而不是"锐度",所以高光不会像全图锐化那样被顶出白边。
    /// 【与「锐化」的区别】锐化是亮度域 + 边缘加权的小核高通;清晰是大核 + 中调加权的局部对比,
    /// 两者改动图相关 0.85(旧实现两者是 0.98)。增益实测定标 0.5(1.6 时干净基底 25 档就 −6.6dB)。</summary>
    private static void ApplyClarityInMemory(System.Drawing.Bitmap bmp, int strength)
    {
        int w = bmp.Width, h = bmp.Height;
        if (w < 3 || h < 3) return;
        double amt = Math.Clamp(strength / 100.0, 0.0, 1.0);
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
                    for (int x = 0; x < w; x++)
                    { int i = x * 4; b[idx + x] = row[i]; g[idx + x] = row[i + 1]; r[idx + x] = row[i + 2]; }
                }
            }
            var br = (byte[])r.Clone(); var bg2 = (byte[])g.Clone(); var bb = (byte[])b.Clone();
            var tmp = new byte[n];
            for (int p = 0; p < 8; p++)
            {
                BoxBlur(br, tmp, w, h);
                BoxBlur(bg2, tmp, w, h);
                BoxBlur(bb, tmp, w, h);
            }
            for (int i = 0; i < n; i++)
            {
                double y0 = 0.299 * r[i] + 0.587 * g[i] + 0.114 * b[i];
                double t = Math.Abs(y0 - 128.0) / 140.0;
                double wgt = Math.Max(0.0, 1.0 - t * t);
                double k = amt * 0.5 * wgt;
                r[i] = (byte)Math.Clamp((int)Math.Round(r[i] + k * (r[i] - br[i])), 0, 255);
                g[i] = (byte)Math.Clamp((int)Math.Round(g[i] + k * (g[i] - bg2[i])), 0, 255);
                b[i] = (byte)Math.Clamp((int)Math.Round(b[i] + k * (b[i] - bb[i])), 0, 255);
            }
            unsafe
            {
                byte* p0 = (byte*)data.Scan0.ToPointer();
                for (int y = 0; y < h; y++)
                {
                    byte* row = p0 + y * stride; int idx = y * w;
                    for (int x = 0; x < w; x++)
                    { int i = x * 4; row[i] = b[idx + x]; row[i + 1] = g[idx + x]; row[i + 2] = r[idx + x]; }
                }
            }
        }
        finally { bmp.UnlockBits(data); }
    }

    /// <summary>锐化:在【亮度】上做小核高通(2 遍盒模糊 ≈ 半径 2),并按【局部梯度】加权 ——
    /// 强边缘多给、平坦区与细纹理少给(权重 0.25 + 0.75×梯度归一)。
    /// 【为什么不用原来的写法】原先锐化是"逐通道、全图、无阈值"的 USM:①逐通道处理会在彩色边缘上
    /// 出彩色描边;②全图无差别增强会把噪点和纹理一起提;③它的改动图与「钝化蒙版」相关 0.98,等于同一个旋钮。
    /// 现在改成亮度域 + 边缘加权:实测同强度下白边更少(干净 waifu2x 基底强度 25 时 over% 0.002 vs 钝化蒙版 0.009)、
    /// 与钝化蒙版的相关从 0.98 降到 0.94,与「细节增强」0.92,且不会出彩色描边。</summary>
    private static void ApplySharpenEdgeInMemory(System.Drawing.Bitmap bmp, int strength)
    {
        int w = bmp.Width, h = bmp.Height;
        if (w < 4 || h < 4) return;
        double amt = Math.Clamp(strength / 100.0, 0.0, 1.0);
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
                    for (int x = 0; x < w; x++)
                    { int i = x * 4; b[idx + x] = row[i]; g[idx + x] = row[i + 1]; r[idx + x] = row[i + 2]; }
                }
            }
            var lum = new byte[n];
            for (int i = 0; i < n; i++)
                lum[i] = (byte)Math.Clamp((int)Math.Round(0.299 * r[i] + 0.587 * g[i] + 0.114 * b[i]), 0, 255);
            var blur = (byte[])lum.Clone();
            var tmp = new byte[n];
            BoxBlur(blur, tmp, w, h);
            BoxBlur(blur, tmp, w, h);
            for (int y = 1; y < h - 1; y++)
            {
                int row = y * w;
                for (int x = 1; x < w - 1; x++)
                {
                    int i = row + x;
                    int gx = (blur[i - w + 1] + 2 * blur[i + 1] + blur[i + w + 1])
                           - (blur[i - w - 1] + 2 * blur[i - 1] + blur[i + w - 1]);
                    int gy = (blur[i + w - 1] + 2 * blur[i + w] + blur[i + w + 1])
                           - (blur[i - w - 1] + 2 * blur[i - w] + blur[i - w + 1]);
                    double mag = Math.Sqrt((double)gx * gx + (double)gy * gy) / 4.0;
                    double wgt = Math.Clamp(mag / 48.0, 0.0, 1.0);
                    double add = amt * 1.5 * (lum[i] - blur[i]) * (0.25 + 0.75 * wgt);
                    r[i] = (byte)Math.Clamp((int)Math.Round(r[i] + add), 0, 255);
                    g[i] = (byte)Math.Clamp((int)Math.Round(g[i] + add), 0, 255);
                    b[i] = (byte)Math.Clamp((int)Math.Round(b[i] + add), 0, 255);
                }
            }
            unsafe
            {
                byte* p0 = (byte*)data.Scan0.ToPointer();
                for (int y = 0; y < h; y++)
                {
                    byte* row = p0 + y * stride; int idx = y * w;
                    for (int x = 0; x < w; x++)
                    { int i = x * 4; row[i] = b[idx + x]; row[i + 1] = g[idx + x]; row[i + 2] = r[idx + x]; }
                }
            }
        }
        finally { bmp.UnlockBits(data); }
    }

    /// <summary>细节增强(高通提取 + 软门槛):把"原图 − 3×3 盒均值"按强度加回原图,
    /// 但幅值小于门槛(4 级)的部分按比例压掉 —— 低幅高频就是噪声,只有成形的微纹理才被放大。
    /// 【为什么加门槛】原实现是无门槛高通,而半径 1 的高通对噪点最敏感(UI 却写着"温和,不易放大噪点",说反了)。
    /// 加软门槛后同强度下白边过冲更低(压缩/Real-ESRGAN 基底强度 50:over% 0.235 而对等的锐化为 0.315,
    /// PSNR −1.85dB 对 −2.03dB),而且它是六个锐化档里同等清晰度下代价最小的。
    /// 【与「锐化」的区别】这个是全图高通 + 幅值门槛;锐化是亮度域 + 空间(梯度)加权。</summary>
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

    /// <summary>单通道高通增强 + 软门槛:new = orig + (orig − 3×3 盒均值) × amount × gate,
    /// gate = |hp| ≤ 4 时为 0,否则 (|hp|−4)/|hp| —— 低幅高频(噪声)被压掉,成形的微纹理照常放大。</summary>
    private static void HighFreqChannel(byte[] src, int w, int h, double amount)
    {
        const double thr = 4.0;
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
                int c = orig[row + x];
                double hp = c - sum / 9.0;          // 高频(3×3 盒均值为低频)
                double a = Math.Abs(hp);
                double gate = a <= thr ? 0.0 : (a - thr) / a;   // 软门槛:噪声不放大
                int v = (int)Math.Round(c + amount * hp * gate);
                src[row + x] = (byte)Math.Clamp(v, 0, 255);
            }
        }
    }

    /// <summary>边缘增强:【对称】拉普拉斯锐化 + 边缘加权(2026-09-12 重做机理,档位保留)。
    /// 算法:`v = c + (强度/100) × 0.5 × 归一化拉普拉斯 × 边缘权重`。
    /// 【为什么重做】原实现的两项都"只加正值"(梯度幅值×0.25 + 拉普拉斯**符号**×固定偏置),于是只把边缘
    /// 往亮的方向推 —— 实测**只出白边不出暗边**(over 0.83%→17.78%@50,under 始终 ≈0.05%),是十档里最差的一档。
    /// 换成真正的拉普拉斯(8×中心 − 邻域和,再 /8 归一):暗侧为负、亮侧为正,**天然对称**;再按索伯梯度
    /// (在模糊后的亮度上算,避免被噪声带偏)加权 —— 只动成形的边缘,平坦区与噪点不动。
    /// 【实测(四组基底,出厂代码)】强度 100:
    ///   干净 waifu2x 基底:PSNR 损失 −10.99dB → **−3.65dB**,白边像素 0.027% → **0.002%**,detail 156% → **172%**;
    ///   干净 Real-ESRGAN 基底:−2.91dB → **−1.53dB**,强边缘白边 13.18% → **4.14%**,detail 138% → **196%**;
    /// 且 over/under 由"单边"(0.464%/0.040%)变成**平衡**(0.160%/0.222%)—— 这正是本轮要修的东西。
    /// 数据:_qa\imgpost\imgpost_edgedeblur.txt。</summary>
    private static void ApplyEdgeEnhanceInMemory(System.Drawing.Bitmap bmp, int strength)
    {
        int w = bmp.Width, h = bmp.Height;
        if (w < 4 || h < 4) return;
        double amt = Math.Clamp(strength / 100.0, 0.0, 1.0);
        const double kGain = 0.5;
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
            // 边缘权重用"模糊后的亮度"算(与「锐化」同一做法:避免噪声把权重带满)
            var lum = new byte[n];
            for (int i = 0; i < n; i++)
                lum[i] = (byte)Math.Clamp((int)Math.Round(0.299 * r[i] + 0.587 * g[i] + 0.114 * b[i]), 0, 255);
            var blur = (byte[])lum.Clone();
            var tmp = new byte[n];
            BoxBlur(blur, tmp, w, h);
            foreach (var ch in new[] { r, g, b })
            {
                var orig = (byte[])ch.Clone();
                for (int y = 1; y < h - 1; y++)
                {
                    int row = y * w;
                    for (int x = 1; x < w - 1; x++)
                    {
                        int i = row + x;
                        int c = orig[i];
                        int nb = orig[i - w - 1] + orig[i - w] + orig[i - w + 1]
                               + orig[i - 1] + orig[i + 1]
                               + orig[i + w - 1] + orig[i + w] + orig[i + w + 1];
                        double lap = (8.0 * c - nb) / 8.0;                 // 对称:暗侧负、亮侧正
                        int gx = (blur[i - w + 1] + 2 * blur[i + 1] + blur[i + w + 1])
                               - (blur[i - w - 1] + 2 * blur[i - 1] + blur[i + w - 1]);
                        int gy = (blur[i + w - 1] + 2 * blur[i + w] + blur[i + w + 1])
                               - (blur[i - w - 1] + 2 * blur[i - w] + blur[i - w + 1]);
                        double mag = Math.Sqrt((double)gx * gx + (double)gy * gy) / 4.0;
                        double wgt = Math.Clamp(mag / 48.0, 0.0, 1.0);
                        ch[i] = (byte)Math.Clamp((int)Math.Round(c + amt * kGain * lap * wgt), 0, 255);
                    }
                }
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
        finally { bmp.UnlockBits(data); }
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

    /// <summary>去雾:标准【暗通道先验(何恺明 DCP)】——估计透射率 + 大气光,反演雾图。
    /// 对"灰蒙/泛白/雾霾"图确实有效(实测人工雾图 PSNR 12.97→21.68dB、edgeBad 83%→24%);
    /// 但对**本来没有雾**的图任何强度都是纯亏(强度 10 已经 PSNR −11dB,强度 30 起 37% 像素被截断)。
    /// 【有效区间只有 40~52,峰值 48】<36 几乎无效、>56 开始毁图(56 档 14% 像素截断、70 档 61%)。
    /// 强度 0-100 映射为 amount(与原图混合比例),上限 1.0 —— 见 _qa\imgpost\measure2.py --haze。
    /// 【2026-09 核对】原注释两处与实现不符(仅注释改正,行为未变):①暗通道最小值滤波实际是
    /// 半径 2 跑 6 遍 = 有效半径 12(25×25 窗口),不是 15×15;②透射率实际用固定常数 229.5 归一,
    /// **大气光 A 并没有进入 t 的计算**(A 只在后面减光幕时用到)。</summary>
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
            // ① 暗通道:每像素取 R/G/B 最小值,再做局部最小值滤波(半径 2 × 6 遍 → 有效窗口 25×25)
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
            // ③ 透射率 t = 1 − ω·dark/229.5(固定归一常数,没用上面估的大气光 A —— 与教科书 DCP 的
            //    t = 1 − ω·dark/A 不同;A 只在下一步减光幕时参与)。加下限防除零/过饱和。
            const double omega = 0.95;
            // 【2026-09 实测重定标】强度映射到 0~0.55,不再是 0~1。
            // 原映射 0~100 → amount 0~1,但实测"能用"的只有 amount 0.40~0.52(峰值 0.48):
            //   · amount < 0.36(即强度 <36)几乎无效;
            //   · amount ≥ 0.56(即强度 ≥56)开始截断:56 档 14% 像素被截断、70 档 61%、100 档 100%。
            // 也就是说原滑杆 8/9 的行程要么没用、要么在毁图。压到 0.55 之后整段行程单调有效
            // (峰值落在强度 ≈87),代价是最浓的雾不如原来"够力"——但原来那个"够力"是拿毁图换的。
            // 数据:_qa\imgpost\measure2.py --haze(人工雾图,最浓 0.75A)。
            double amount = strength / 100.0 * 0.55;
            double tMin = Math.Max(0.05, 1.0 - amount * 0.4);   // 强度越大,可去雾越深(透射率下限越低)
            for (int i = 0; i < n; i++)
            {
                // 归一化透射率(按固定常数归一)
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

    /// <summary>减少杂色:3×3 中值滤波(保边缘去噪点),**按强度线性混合** —— 0 = 原图,100 = 两遍中值的完整效果。
    /// 【为什么从"开关"改成"连续"(2026-09 逐档实测)】原实现是 `strength > 50` 时再跑一遍中值,
    /// 于是 101 个滑块位置只有 **2 个不同结果**(实测强度 10/30/50 三档输出逐像素完全相同,70/90/100 也相同),
    /// 用户想"少降一点"做不到 —— 一开就是满力。而全力的 3×3 中值对 **1 像素宽的细结构**
    /// (发丝/细描边)是 100% 抹掉(1080p 细线靶标实测保留率 0.0%,2px/3px 的线则完全不受影响)。
    /// 但中值本身不该扔:四组真实基底上它都是净收益(真实素材 PSNR +0.02~+0.42dB、带颗粒素材最高 +7.86dB),
    /// 比双边/Sigma 等候选都更会去颗粒。所以要修的是**力度不连续**,不是算子。
    /// 新映射:细结构保留率随强度线性(25→75%、50→50%、75→25%、100→0%),强度 100 与旧的 51+ 档逐像素一致。
    /// 数据与对照图:_qa\imgpost\(measure.py / measure2.py / measure4.py / measure5.py)。</summary>
    private static void ApplyMedianInMemory(System.Drawing.Bitmap bmp, int strength)
    {
        int w = bmp.Width, h = bmp.Height;
        if (w < 3 || h < 3) return;
        double amount = Math.Clamp(strength / 100.0, 0.0, 1.0);
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
            // 目标 = 两遍中值(旧的"彻底"档);再与原图按强度线性混合 → 力度连续可调
            var tr = (byte[])r.Clone();
            var tg = (byte[])g.Clone();
            var tb = (byte[])b.Clone();
            MedianChannel(tr, w, h);
            MedianChannel(tr, w, h);
            MedianChannel(tg, w, h);
            MedianChannel(tg, w, h);
            MedianChannel(tb, w, h);
            MedianChannel(tb, w, h);
            for (int i = 0; i < n; i++)
            {
                r[i] = (byte)Math.Clamp((int)Math.Round(r[i] + (tr[i] - r[i]) * amount), 0, 255);
                g[i] = (byte)Math.Clamp((int)Math.Round(g[i] + (tg[i] - g[i]) * amount), 0, 255);
                b[i] = (byte)Math.Clamp((int)Math.Round(b[i] + (tb[i] - b[i]) * amount), 0, 255);
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
    /// 迭代次数随强度线性(3~10 次);核半径 1、强度 ≥60 时改 2(**60 是一条隐藏台阶**,效果不连续)。
    /// 【2026-09 核对】原注释末尾"并在最后与轻微锐化补一下边缘"在函数体里**不存在**(仅注释改正,行为未变)——
    /// 这才是交接说明里想找的那处注释与实现不符(「减少杂色」那条 51+ 中值其实是有的)。
    /// 另:>900 万像素时本函数会**静默退化成 unsharp**(见下方 px 判断),UI 未披露这一点。
    /// 实测(压缩/Real-ESRGAN 基底,强度 10):PSNR 34.99→33.41(−1.58dB)、强边缘过冲 0.83%→9.22%,
    /// 而超分后的图本身并不模糊 —— 也就是说在"先超分再做后处理"的既有流程里它基本是纯亏。</summary>
    private static void ApplyDeblurInMemory(System.Drawing.Bitmap bmp, int strength)
    {
        int w = bmp.Width, h = bmp.Height;
        if (w < 3 || h < 3) return;
        // 【超大图保护】RL 反卷积迭代多次+3通道,内存/耗时随像素数线性增长。
        // >400 万像素(约2000×2000)时降低迭代,>900 万像素(约4K)时改用简单 unsharp 兜底(避免内存峰值/超长耗时)。
        long px = (long)w * h;
        // 【2026-09 改】迭代从 3~10 降到 2~6:配合下面的"过冲钳制"后,多迭代只增加振铃与耗时,
        // 实测 6 次已经拿到全部有效复原(图越不糊,多迭代越是白烧时间 —— 1080p 一帧 3.75 秒主要花在这)。
        int iter = Math.Max(2, Math.Min(6, (int)Math.Round(2 + strength / 100.0 * 4)));
        if (px > 9_000_000) { ApplyUnsharpInMemory(bmp, strength / 100.0 * 1.5, 2, 6); return; }
        if (px > 4_000_000) iter = Math.Max(2, iter - 2);   // 大图收敛快,减迭代防卡太久
        // 【2026-09 改】核半径原来是"强度 ≥60 时从 1 跳到 2"—— 效果在 60 处有台阶(实测 50→70 档
        // detail 反而掉、PSNR 也掉)。改成【连续 sigma】:支撑半径固定 3,σ = 0.6 + 强度/100×1.6,
        // 于是强度是单调的,不再有隐藏的跳变点。
        const int kernelR = 3;
        int ksz = kernelR * 2 + 1;
        var k1d = new double[ksz];
        double ksum = 0; double sigma = 0.6 + strength / 100.0 * 1.6;
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
            // 【过冲钳制(2026-09-12)】预先把"观测图的 3×3 局部范围"算出来,RL 每轮迭代后把估计值夹回这个
            // 范围(±6 级余量)。RL 是病态问题:输入本来不糊时会把噪点当细节放大,并产生振铃/白边
            // (实测原实现强度 10 档就把强边缘过冲像素从 0.83% 推到 9.22%,干净 Real-ESRGAN 基底强度 50 到 22.49%)。
            // 钳制后实测:干净基底强度 100 的白边 2.211%→0.552%、PSNR 损失 −5.10dB→−2.50dB,细节仍有 136%。
            var lo = new double[n]; var hi = new double[n];
            BuildLocalRange(R, lo, hi, w, h);
            R = RichardsonLucy(R, w, h, k1d, kernelR, iter, lo, hi);
            BuildLocalRange(G, lo, hi, w, h);
            G = RichardsonLucy(G, w, h, k1d, kernelR, iter, lo, hi);
            BuildLocalRange(B, lo, hi, w, h);
            B = RichardsonLucy(B, w, h, k1d, kernelR, iter, lo, hi);
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

    /// <summary>算"观测图的 3×3 局部范围"作为 RL 每轮迭代的钳制上下界(留 ±6 级余量,保留一点过冲空间)。
    /// 这是标准的"不让复原结果跑出局部动态范围"约束,专治反卷积的振铃/白边。</summary>
    private static void BuildLocalRange(double[] obs, double[] lo, double[] hi, int w, int h)
    {
        const double margin = 6.0 / 255.0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double mn = 1.0, mx = 0.0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int yy = Math.Clamp(y + dy, 0, h - 1) * w;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        double v = obs[yy + Math.Clamp(x + dx, 0, w - 1)];
                        if (v < mn) mn = v;
                        if (v > mx) mx = v;
                    }
                }
                lo[y * w + x] = mn - margin;
                hi[y * w + x] = mx + margin;
            }
        }
    }

    /// <summary>理查森-露西反卷积:单通道,已知一维可分离(高斯)核。迭代增强高频复原。
    /// lo/hi 非空时每轮迭代后做【过冲钳制】(夹回观测图的局部范围)—— 治振铃/白边,见调用处注释。</summary>
    private static double[] RichardsonLucy(double[] obs, int w, int h, double[] k1d, int kr, int iter,
        double[]? lo = null, double[]? hi = null)
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
            // ④ 过冲钳制(可选):把估计值夹回观测图的局部动态范围
            if (lo != null && hi != null)
                for (int i = 0; i < n; i++)
                    est[i] = Math.Min(hi[i], Math.Max(lo[i], est[i]));
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

    /// <summary>边缘抗锯齿:只对"3×3 邻域最大差 ≥ 16"的像素向邻域均值靠拢,磨平阶梯感;强度越大混合越多(最多 55%)。
    /// 【2026-09 核对】原注释写"平坦区域完全不动,细节不糊" —— 前半对,后半过强:阈值是**局部对比度**,
    /// 任何局部对比 ≥16 的纹理都会被混掉 0.55 的均值,实测 detail 也真的在掉
    /// (压缩/Real-ESRGAN 基底 54.1→36.1@50→23.1@100)。它是十档里唯一"所有客观指标单调变好"的一档
    /// (PSNR/edgePSNR 升、白边降),代价就是拿细节换干净。</summary>
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
