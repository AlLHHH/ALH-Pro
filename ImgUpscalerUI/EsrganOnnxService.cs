// EsrganOnnxService.cs — Real-ESRGAN ONNX 超分(纯 C#,ONNX Runtime,无 Python)
// 目的:50 系 Blackwell + CPU 都稳定的超分实现(ncnn-vulkan 老引擎在 50 系/CUDA 系崩溃)。
// 路径:引擎文件 realesrgan-ncnn-vulkan.exe(2022)在 50 系不可用,此服务用 ONNX 模型替代。
// CPU/GPU(DirectML)双模式,GPU 失败自动降 CPU(与 CutoutService 同策略)。
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ALHPro;

public static class EsrganOnnxService
{
    /// <summary>ONNX 超分模型路径:搜索 engines/rembg + engines/realesrgan + 程序根目录。
    /// (RealESRGAN_x4plus.onnx 已放 realesrgan 引擎目录——安装器排除 engines\rembg\*.onnx 是为 1.4GB 抠图模型,
    /// 超分 ONNX 模型不能只放 rembg 目录,否则安装版机器永远缺失,黑块降级 ONNX 会直接失败:真机 v1.1.1 已复现)</summary>
    public static string? FindModel()
    {
        var roots = new[]
        {
            Path.Combine(EngineService.EnginesDir, "rembg"),
            Path.Combine(EngineService.EnginesDir, "realesrgan"),
        };
        foreach (var f in new[] { "RealESRGAN_x4plus.onnx", "realesrgan-x4plus.onnx" })
        {
            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var found in Directory.EnumerateFiles(root, f, SearchOption.AllDirectories))
                    return found;
            }
            var direct = Path.Combine(EngineService.EnginesDir, f);
            if (File.Exists(direct)) return direct;
        }
        return null;
    }

    /// <summary>waifu2x ONNX 模型路径(engines/waifu2x/ waifu2x-cunet2x.onnx;nagadomi/nunif 官方导出)。
    /// 注意:该模型输入名为 x(不是 input)——用于核显/无独显设备(waifu2x ncnn CPU 模式有 bug 会崩)。</summary>
    public static string? FindWaifu2xModel()
    {
        var root = Path.Combine(EngineService.EnginesDir, "waifu2x");
        foreach (var f in new[] { "waifu2x-cunet2x.onnx", "waifu2x_cunet2x.onnx" })
        {
            foreach (var found in Directory.EnumerateFiles(root, f, SearchOption.AllDirectories))
                return found;
            var direct = Path.Combine(root, f);
            if (File.Exists(direct)) return direct;
        }
        return null;
    }

    /// <summary>动漫动画模型 ONNX(engines/realesrgan/ realesr-animevideov3.onnx;2.4MB)。
    /// 50系无独显走 ONNX 时也保持"动漫动画"画质(而非退到 x4plus 通用画质)。</summary>
    public static string? FindAnimeVideoModel()
    {
        var root = Path.Combine(EngineService.EnginesDir, "realesrgan");
        foreach (var f in new[] { "realesr-animevideov3.onnx", "RealESR-AnimeVideo-v3_x4.onnx" })
        {
            foreach (var found in Directory.EnumerateFiles(root, f, SearchOption.AllDirectories))
                return found;
            var direct = Path.Combine(root, f);
            if (File.Exists(direct)) return direct;
        }
        return null;
    }

    /// <summary>按所选 Real-ESRGAN 模型名解析对应的 ONNX 模型路径:动漫模型(名字含 anime,如 animevideov3/x4plus-anime)
    /// → 优先动漫 ONNX(保持动漫画质),无则通用;通用模型(x4plus)→ 通用 ONNX,若无通用 ONNX 则回退动漫 ONNX
    /// (风险设备上"能用"优先于"画质精确",避免硬走会崩的 ncnn-GPU)。</summary>
    public static string? ResolveEsrganOnnxPath(string model)
    {
        if (model.Contains("anime", StringComparison.OrdinalIgnoreCase))
            return FindAnimeVideoModel() ?? FindModel();
        return FindModel() ?? FindAnimeVideoModel();
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, int), InferenceSession> _sessions = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, int), SemaphoreSlim> _locks = new();
    private static bool _dmlWarned;

    /// <summary>DirectML 设备已被 Windows 摘除/挂死(进程级,不可恢复)。置位后本进程内所有 GPU ONNX 请求快速失败,
    /// 由调用方按【批次】回退源帧——不再逐帧重试(重试必然再失败),也不再落 CPU(慢到不可接受)。</summary>
    private static int _dmlDead;
    private static int _dmlDeadWarned;

    /// <summary>本进程的 DirectML 是否已永久失效(需重启软件才能恢复)。</summary>
    public static bool DmlDeviceDead => Volatile.Read(ref _dmlDead) != 0;

    /// <summary>同一设备【连续】瞬时失败次数(GPU 成功一次即清零)。上限见 DmlTransientStrikes。
    /// 【键的编号空间:DirectML 设备号】—— 全表只认 DML 号,绝不混入引擎 -g 编号(见 DmlForEngineDevice 说明)。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> _dmlStrikes = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> _audioDmlStrikes = new();

    /// <summary>【引擎 -g 编号 → DirectML 设备号】的进程内缓存。
    /// 【为什么必须有缓存】EngineService.ToDmlDevice 内部会【真枚举 DXGI】(CreateDXGIFactory1 + 遍历适配器,
    /// 本身没有缓存),而超分是按【块】调用的(1080p 一张图 12 块、4K 更多块,视频还要逐帧)——连击表的
    /// 记录/查询/清零若每次现算一次,就把设备号解析变成了新的性能问题。所以编号空间只解析一次并复用。
    /// 【为什么失败不缓存】ToDmlDevice 匹配不到同名卡时返回 -1(宁可慢不跑错卡);那是"当前匹配不上",
    /// 不是硬件事实——启动探测(DmlFallbackOk)稍后就绪时还可能兜底,缓存 -1 会把临时结果钉成永久结论。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> _engineToDmlCache = new();

    /// <summary>(模型, DML 设备号) → 已确认"建不出 DirectML 会话"(非持续性失败)。
    /// 【为什么需要记】超分是按【块】调用的:一张 1080p 图 12 块、视频还要逐帧。不记的话每一块都会重试一次
    /// 注定失败的 AppendExecutionProvider_DML,还会把同一条告警刷满日志(I-19 那种刷屏)。
    /// 记下之后该组合直接走独立的 CPU 键,且只告警一次(进程级;驱动修好后重启软件即可恢复)。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, int), bool> _dmlBrokenSessions = new();

    /// <summary>瞬时失败连击上限。达限即认定该设备在本进程内不可用,按持续性错误同样口径处理(抛可操作错误 /
    /// 回退源帧),而不是转 CPU。为什么是 3:一次重试的代价是"一块/一对帧的 CPU 推理"(秒级),连吃 3 次
    /// 说明不是偶发抖动;再试下去就是"N 帧 × CPU 推理"的几小时形状——那正是要消灭的东西。</summary>
    private const int DmlTransientStrikes = 3;

    /// <summary>业务域,决定用哪张连击表。音频(HT-Demucs)的显存压力与并发模型与图片/视频超分完全不同,
    /// 失败成因(模型 OOM、输入形状)也不同,必须与视频分表,否则音频连吃 3 次失败会把图片超分/补帧
    /// 判成设备不可用,或音频成功一次就 Clear 掉视频攒的连击 —— 双向污染。</summary>
    internal enum DmlDomain { Video, Audio }

    private static System.Collections.Concurrent.ConcurrentDictionary<int, int> Strikes(DmlDomain domain)
        => domain == DmlDomain.Audio ? _audioDmlStrikes : _dmlStrikes;

    /// <summary>记一次瞬时(非设备级)DML 失败。返回 true = 已达连击上限,该设备视为不可用,调用方不得再转 CPU。</summary>
    internal static bool NoteDmlTransientFailure(int device, DmlDomain domain = DmlDomain.Video)
    {
        if (device < 0) return false;
        return Strikes(domain).AddOrUpdate(device, 1, (_, old) => old + 1) >= DmlTransientStrikes;
    }

    /// <summary>GPU 推理成功 → 清零该设备的连击计数(偶发抖动不该累积成"设备不可用")。</summary>
    internal static void ClearDmlStrikes(int device, DmlDomain domain = DmlDomain.Video)
    {
        if (device >= 0) Strikes(domain).TryRemove(device, out _);
    }

    /// <summary>该设备是否已因连续瞬时失败被判定不可用(本进程内)。用于在建会话/推理之前快速失败——
    /// 这是原 _dmlBad 闩锁里唯一有用的那半(不重复注定失败的调用),去掉的是它"转 CPU"的落点。</summary>
    internal static bool DmlDeviceUnusable(int device, DmlDomain domain = DmlDomain.Video)
        => device >= 0 && Strikes(domain).TryGetValue(device, out var n) && n >= DmlTransientStrikes;

    /// <summary>是否【任一】设备已达连击上限。供只持有"自动"(-2)这类未解析设备号的调用方使用:
    /// 逐对/逐帧循环里认出一次就该停止白试,否则几千帧就是几千次注定失败的调用 + 几千条同样的日志。</summary>
    internal static bool AnyDmlDeviceUnusable(DmlDomain domain = DmlDomain.Video)
    {
        foreach (var kv in Strikes(domain))
            if (kv.Value >= DmlTransientStrikes) return true;
        return false;
    }

    /// <summary>熔断 DirectML:记录设备号、置进程级失效标志,并只提示一次(后续批次静默快速失败,不刷屏)。</summary>
    internal static void TripDmlDead(int dmDevice, Exception ex)
    {
        Volatile.Write(ref _dmlDead, 1);
        if (Interlocked.CompareExchange(ref _dmlDeadWarned, 1, 0) == 0)
        {
            AppLogger.Warn($"⚠ GPU 已被系统摘除/挂死(DirectML 设备 {dmDevice}:{ex.Message.Split('\n')[0]})。"
                + "该错误在本进程内不可恢复,已停止所有 GPU 超分尝试;超分批次将回退为源帧缩放(不跑慢速 CPU)。"
                + "请重启软件后重试;若重启后仍出现,多为显存不足或显卡驱动问题——建议关闭其他占用显存的程序并更新显卡驱动。");
        }
    }

    // ---- DirectML 建会话失败的完整诊断(第 1 项)----

    /// <summary>把 DirectML 建会话 / provider 注册的失败整理成**一行可定性的完整诊断**。
    /// 【为什么必须有】原日志只留 "DirectML 不可用/探测没成功",拿不到根因 —— 诊断包里只能看到
    /// "显示 GPU、实际跑 CPU",分不清是显存不足(0x8007000E)、设备被摘除(0x887A0005/6),
    /// 还是 provider 注册失败。三者处置完全不同(关软件腾显存 / 更新驱动重启 / 驱动重装),
    /// 没有 HRESULT 就只能靠猜。
    /// 一行里同时给出:尝试的设备号、异常类型、**HRESULT(十六进制)**、Message 首行、
    /// 以及【InnerException 链】每一层的类型与 HRESULT —— ONNX Runtime/DirectML 常把真正的
    /// DXGI 错误埋在两三层 InnerException 里(且内层 Message 常常为空,只有 HRESULT 有信息)。
    /// 末尾附上 GpuFault 的判定结论,便于一眼看出"会不会落 CPU"。</summary>
    internal static string DescribeDmlFailure(string stage, int dmlDevice, Exception? ex)
    {
        if (ex == null) return $"DirectML 失败诊断[阶段={stage}, 设备号={(dmlDevice < 0 ? "(未解析/-1)" : dmlDevice.ToString())}, 无异常对象]";
        var sb = new System.Text.StringBuilder();
        sb.Append("DirectML 失败诊断[阶段=").Append(stage)
          .Append(" | 设备号=").Append(dmlDevice < 0 ? "(未解析/-1)" : dmlDevice.ToString())
          .Append(" | 异常=").Append(ex.GetType().Name)
          .Append(" | HRESULT=0x").Append(((uint)ex.HResult).ToString("X8"))
          .Append('(').Append(ClassifyDmlHresult((uint)ex.HResult)).Append(')')
          .Append(" | Message=").Append(FirstLine(ex.Message));
        var inner = ex.InnerException;
        for (int depth = 1; inner != null && depth <= 4; depth++, inner = inner.InnerException)
        {
            sb.Append(" | Inner").Append(depth).Append("[类型=").Append(inner.GetType().Name)
              .Append(", HRESULT=0x").Append(((uint)inner.HResult).ToString("X8"))
              .Append('(').Append(ClassifyDmlHresult((uint)inner.HResult)).Append(')')
              .Append(", Message=").Append(FirstLine(inner.Message)).Append(']');
        }
        sb.Append(AlhPro.Core.GpuFault.IsPersistentDeviceError(ex)
            ? " | 判定=持久性设备错误(设备已被系统摘除,不落 CPU,直接重抛)"
            : " | 判定=非持久性(可回退 CPU / 可换设备重试)");
        return sb.ToString();
    }

    /// <summary>把一个 HRESULT 翻成中文定性(便于一眼分辨"显存不足 / 设备摘除 / provider 注册失败")。
    /// 只做已知码的映射;未知码返回 "未知错误码",绝不猜测成因。
    /// 0x8007000E = E_OUTOFMEMORY(HRESULT_FROM_WIN32(ERROR_OUTOFMEMORY)):
    ///   DirectML 建会话时常见于【显存/共享内存不足】(真机诊断包里的音频分离 DML 就是这个码)。
    /// 0x887A0005/06/07/20 = DXGI DEVICE_REMOVED/HUNG/RESET/DRIVER_INTERNAL_ERROR:设备级失效。
    /// 0x80004005 = E_FAIL:provider 注册/设备创建被拒绝(驱动不完整、DML 运行时不匹配等)。
    /// 0x80070057 = E_INVALIDARG:设备号越界/参数非法(代码侧问题,不是硬件问题)。</summary>
    internal static string ClassifyDmlHresult(uint hr) => hr switch
    {
        0x8007000Eu => "内存/显存资源不足 E_OUTOFMEMORY",
        0x887A0005u => "GPU 设备已被摘除 DXGI_ERROR_DEVICE_REMOVED",
        0x887A0006u => "GPU 设备挂死 DXGI_ERROR_DEVICE_HUNG",
        0x887A0007u => "GPU 设备已重置 DXGI_ERROR_DEVICE_RESET",
        0x887A0020u => "显卡驱动内部错误 DXGI_ERROR_DRIVER_INTERNAL_ERROR",
        0x80004005u => "E_FAIL(provider 注册/设备创建被拒绝,多为驱动或 DirectML 运行时不匹配)",
        0x80070057u => "E_INVALIDARG(设备号/参数非法,属代码或编号空间问题)",
        0x80070005u => "E_ACCESSDENIED(权限不足)",
        0x00000000u => "S_OK(无错误码)",
        _ => "未知错误码",
    };

    /// <summary>Message 首行(去掉换行与超长尾部):ONNX/DirectML 的 Message 常常是几十行堆栈,
    /// 首行才是人话;截断是为了不让一行日志变成几百行。</summary>
    private static string FirstLine(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "(无消息)";
        int nl = s.IndexOfAny(new[] { '\r', '\n' });
        var line = nl > 0 ? s.Substring(0, nl) : s;
        line = line.Trim();
        if (line.Length == 0) return "(消息首行为空)";
        return line.Length > 300 ? line.Substring(0, 300) + "…" : line;
    }

    /// <summary>按"是否持久性设备错误"选级别记录 DirectML 失败:持久性用 Error(带堆栈,同步落盘),
    /// 其余用 Warn。【不要刷屏】调用方必须先过 once 闩锁(_dmlWarned / _dmlBrokenSessions),
    /// 本方法只负责"把一次失败写完整"。</summary>
    internal static void LogDmlFailure(string stage, int dmlDevice, Exception ex)
    {
        string detail = DescribeDmlFailure(stage, dmlDevice, ex);
        if (AlhPro.Core.GpuFault.IsPersistentDeviceError(ex)) AppLogger.Error("⚠ " + detail, ex);
        else AppLogger.Warn("⚠ " + detail);
    }

    /// <summary>运行期实测到"目标 DML 设备建不出会话"时,把完整原因写进 DmlUnavailableReason(第 1 项:
    /// 让诊断包与界面提示拿得到根因)。**不改 DmlFallbackOk**:它的语义是"启动探测的结论",
    /// 运行期失效另有 _dmlBrokenSessions / 连击表 / DmlDeviceDead 各自负责,不在这里越权改写。</summary>
    private static void SetDmlUnavailableByRuntime(int dmlDevice, Exception ex)
    {
        try
        {
            if (_dmlFirstOk >= 0) return;   // 启动探测已确认可用:运行期个例不覆盖"可用"这个结论
            _dmlUnavailableReason = DescribeDmlFailure("运行期建 DirectML 会话", dmlDevice, ex);
        }
        catch { }
    }

    // ---- 本进程 CPU 会话(第 4 项①:显式限制 ONNX 线程数)----

    /// <summary>**所有 CPU(非 DirectML)会话的 SessionOptions 都必须走这里**。
    /// 【为什么】ONNX Runtime 默认(intra_op_num_threads=0)按【物理核】自建线程池,那个线程池在
    /// 本进程内、不受 Job 对象的 CPU 硬上限约束、也没有"低于正常"优先级 —— 结果就是
    /// "安全渲染写着 85%,任务管理器却是 100%,界面卡"。这里显式按 SafeRender 的 CPU 档位
    /// 给保守值(依据见 SafeRender.OnnxCpuIntraOpThreads),inter-op 恒 1(避免线程数相乘)。
    /// DirectML 会话不调用本方法:intra-op 对 GPU 路径无意义(算子跑在设备上,不是本进程线程池)。</summary>
    internal static SessionOptions NewCpuSessionOptions()
    {
        var o = new SessionOptions();
        ApplyConservativeCpuThreads(o);
        return o;
    }

    /// <summary>把"保守的 ONNX CPU 线程数"写进【已建好但尚未用于建会话】的 SessionOptions(第 4 项①)。
    /// **只在确实没有 DirectML 的会话上调用**(GPU 会话不设:算子跑在设备上,不占本进程线程池)。
    /// 两处 try 分开写:inter-op 若因 ORT 版本策略被拒,不能连带把更关键的 intra-op 一起丢掉。
    /// 设置失败只告警不抛 —— 退回 ONNX 默认(吃满物理核)虽然慢/卡,但比任务直接失败强;
    /// 而"设置失败"必须在日志里看得见(否则又会变成"以为限了线程、其实没限")。</summary>
    internal static void ApplyConservativeCpuThreads(SessionOptions opts)
    {
        int intra = SafeRender.OnnxCpuIntraOpThreads;
        try
        {
            opts.IntraOpNumThreads = intra;
            AppLogger.Info($"ONNX CPU 会话线程数:intra-op={intra}(inter-op={SafeRender.OnnxCpuInterOpThreads},"
                + $"逻辑核={Environment.ProcessorCount},CPU档位={SafeRender.EffectiveCpuLevel})—— 显式限制,避免默认按物理核吃满导致界面卡");
        }
        catch (Exception ex) { AppLogger.Warn("⚠ ONNX CPU intra-op 线程数设置失败(将退回 ONNX 默认=按物理核吃满,CPU 占用会很高):" + FirstLine(ex.Message)); }
        try { opts.InterOpNumThreads = SafeRender.OnnxCpuInterOpThreads; }
        catch (Exception ex) { AppLogger.Warn("⚠ ONNX CPU inter-op 线程数设置失败(保持默认):" + FirstLine(ex.Message)); }
    }

    // ---- DirectML 设备实测(只探【映射出的目标设备】;启动时后台探测一次)----
    private static int _dmlProbeState;   // 0=未做 1=进行中 2=完成
    private static int _dmlFirstOk = -1; // 能创建 DirectML 会话的设备号;-1 = 无可用设备
    private static volatile string _dmlUnavailableReason = "";

    /// <summary>DirectML 探测是否已完成。【必须与 DmlFallbackOk 一起看】:`DmlFallbackOk==-1` 有两种
    /// 完全不同的含义 —— "还没探测(未知)" 和 "探测完成、确认不可用(结论)"。下游(视频页处理前诊断/
    /// 内联红字提示)必须先看本标志,否则"未探测"会被当成"不可用"而误报慢速提示。</summary>
    public static bool DmlProbeCompleted => Volatile.Read(ref _dmlProbeState) == 2;

    /// <summary>DirectML 不可用的原因(完整诊断串;可用或未探测时为空)。供诊断包/界面直接显示。</summary>
    public static string DmlUnavailableReason => _dmlUnavailableReason;

    /// <summary>解析"本次探测应该建 DirectML 会话的唯一设备号"。-1 = 解析不出。
    /// 顺序:①设置里指定的卡(尊重用户选择,经 ResolveDmlDevice 名称匹配到真卡);
    ///       ②引擎映射表里第一个【独显】对应的 DML 号(设置里的卡无效/未选时的兜底)。
    /// 【绝不遍历设备 0..3】原实现 for i in 0..3 逐个建会话:会把核显、以及 DXGI 里重复出现的同名
    /// 适配器条目(诊断包里 NVIDIA RTX 5060 在 #0/#2/#3 各有一条)一并拉起来建 DirectML 会话,
    /// 每个都要吃一份显存/共享内存 —— 这正是 8007000E(内存资源不足)的常见来源,
    /// 而且"探测到的第一个可用设备"未必是用户要用的那张卡。</summary>
    private static int ResolveProbeDmlDevice(out string why)
    {
        var tried = new System.Collections.Generic.List<string>();
        // ① 设置里的计算设备(用户选择优先;无效编号由 ResolveDmlDevice 内部兜底到推荐独显)
        try
        {
            int want = AppSettings.GpuIndex;
            if (want >= 0)
            {
                int dm = EngineService.ResolveDmlDevice(want);
                if (dm >= 0)
                {
                    why = $"设置的计算设备 #{want} 经名称匹配 → DirectML #{dm}";
                    return dm;
                }
                tried.Add($"设置设备#{want}({GpuInfo.GetEngineDeviceName(want)})未匹配到 DirectML 设备");
            }
            else tried.Add("设置的计算设备=-1(用户选了 CPU)");
        }
        catch (Exception ex) { tried.Add("设置设备解析异常:" + ex.GetType().Name); }
        // ② 引擎映射表里第一个独显
        try
        {
            var devs = VulkanCheck.Devices;
            if (devs.Count == 0) tried.Add("引擎设备表为空(未枚举)");
            foreach (var d in devs)
            {
                if (GpuInfo.IsIntegratedGPU(d.Name)) continue;   // 核显不探(慢且不是用户要的卡)
                int dm = EngineService.ToDmlDevice(d.Id);
                if (dm >= 0)
                {
                    why = $"引擎映射表第一个独显 引擎#{d.Id}({d.Name}) → DirectML #{dm}";
                    return dm;
                }
                tried.Add($"引擎#{d.Id}({d.Name})未匹配到 DirectML 设备");
            }
        }
        catch (Exception ex) { tried.Add("引擎映射解析异常:" + ex.GetType().Name); }
        why = string.Join(";", tried);
        return -1;
    }

    /// <summary>探测用模型:**任一可用的超分 ONNX**(ESRGAN x4plus → waifu2x → 动漫)。
    /// 【为什么不能用 FindModel() 一个】机器上不一定装了 x4plus 那个通用模型(仓库/安装包里
    /// realesrgan 目录常常只有 realesr-animevideov3.onnx,waifu2x 目录只有 waifu2x-cunet2x.onnx),
    /// 而视频超分在 50 系/无独显机器上走的正是 **waifu2x ONNX** 这条路 ——
    /// 若探测只认 x4plus,这种机器上探测会因"模型缺失"而永远拿不到 DirectML 结论,
    /// 于是又回到"静默落 CPU、日志里没有根因"的老问题。任一模型都能建 DirectML 会话,
    /// 探测只需要"能不能在该设备上把会话建起来"这一个事实。</summary>
    internal static string? FindProbeModel()
        => FindModel() ?? FindWaifu2xModel() ?? FindAnimeVideoModel();

    /// <summary>实测 DirectML 设备是否可用 —— **只探映射出的那一个目标设备**(见 ResolveProbeDmlDevice)。
    /// 目标设备建会话失败时,再退到"引擎映射表里第一个独显"重试一次(最多 2 次,不是 0..3 的遍历)。
    /// 结果供 EngineService.ToDmlDevice 在"名字匹配失败"时兜底,也供 PickDevice / 界面提示判定。
    /// 幂等;失败时留下**完整原因**(DescribeDmlFailure 的 HRESULT/类型/Message/InnerException),
    /// 并置 DmlFallbackOk=-1(=-1 仍表示不可用,公开签名与语义不变)。
    /// 注意区分持久性设备错误:那类错误照样只记日志(本函数只做探测,不做熔断),不改变既有重抛行为。</summary>
    public static async Task<int> EnsureDmlProbeAsync(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _dmlProbeState, 1, 0) != 0)
            return _dmlFirstOk;   // 已在做或被别人做过
        int nextState = 2;
        try
        {
            // 探测模型:任一可用的超分 ONNX(见 FindProbeModel —— 只认 x4plus 会在只装了 waifu2x
            // / 动漫模型(视频超分实际用的那两个)的机器上永远探不出结论)。
            var model = FindProbeModel();
            if (model == null)
            {
                _dmlUnavailableReason = "未找到任何超分 ONNX 模型(" + Path.Combine(EngineService.EnginesDir, "realesrgan")
                    + " 与 " + Path.Combine(EngineService.EnginesDir, "waifu2x") + " 下都没有),无法用建会话的方式实测 DirectML";
                AppLogger.Warn("⚠ DirectML 不可用 — " + _dmlUnavailableReason + ";DmlFallbackOk 保持 -1(下游按不可用处理)。");
                return _dmlFirstOk;
            }
            int target = ResolveProbeDmlDevice(out var why);
            if (target < 0)
            {
                _dmlUnavailableReason = $"无法解析出要探测的 DirectML 设备号({why})";
                AppLogger.Warn("⚠ DirectML 不可用 — " + _dmlUnavailableReason
                    + "。已【不再】遍历设备 0..3(会把核显与 DXGI 里重复的同名适配器条目一并拉起建会话,是 8007000E 内存资源不足的常见来源)。"
                    + "DmlFallbackOk=-1(明确按不可用处理),下游将改用 CPU 或回退源帧。");
                return _dmlFirstOk;
            }
            ct.ThrowIfCancellationRequested();
            int ok = TryProbeDmlDevice(model, target, out var fail);
            if (ok < 0)
            {
                // 目标设备建不出会话:退到"引擎映射表里第一个独显"再试一次(若与目标不同)
                int second = FirstDiscreteDmlDevice();
                if (second >= 0 && second != target)
                {
                    AppLogger.Warn($"⚠ DirectML 探测:目标设备 #{target} 建会话失败,再试引擎映射表里第一个独显 → DirectML #{second}");
                    ok = TryProbeDmlDevice(model, second, out var fail2);
                    if (ok < 0) _dmlUnavailableReason = $"{fail} ; 兜底设备 #{second}:{fail2}";
                    else { _dmlFirstOk = ok; _dmlUnavailableReason = ""; }
                }
                else _dmlUnavailableReason = fail;
            }
            else
            {
                _dmlFirstOk = ok;
                _dmlUnavailableReason = "";
            }
            if (_dmlFirstOk < 0)
                AppLogger.Warn($"⚠ DirectML 不可用(实测建会话失败,{why}) — {_dmlUnavailableReason}"
                    + "。DmlFallbackOk=-1 = 明确的「DirectML 不可用」结论(不是「还没探」);"
                    + "ONNX 超分/补帧在对应路径上会改用 CPU 或回退源帧,并非静默跳过。");
        }
        catch (OperationCanceledException)
        {
            nextState = 0;   // 取消:状态复位,下次调用可重新探测(不把"被取消"钉成"不可用"的结论)
            AppLogger.Info("DirectML 探测:已取消,状态复位(下次调用会重新探测)");
        }
        catch (Exception ex)
        {
            _dmlUnavailableReason = DescribeDmlFailure("EnsureDmlProbeAsync(探测外层意外异常)", -1, ex);
            AppLogger.Error("⚠ DirectML 探测意外失败(按不可用处理):" + _dmlUnavailableReason, ex);
        }
        finally { Volatile.Write(ref _dmlProbeState, nextState); }
        await Task.CompletedTask;   // 保持 async 签名(调用方统一 await;探测本身同步,已在后台任务中跑)
        return _dmlFirstOk;
    }

    /// <summary>引擎映射表里第一个独显 → DirectML 设备号(-1 = 没有/不匹配)。</summary>
    private static int FirstDiscreteDmlDevice()
    {
        try
        {
            foreach (var d in VulkanCheck.Devices)
            {
                if (GpuInfo.IsIntegratedGPU(d.Name)) continue;
                int dm = EngineService.ToDmlDevice(d.Id);
                if (dm >= 0) return dm;
            }
        }
        catch { }
        return -1;
    }

    /// <summary>对【一个】设备号做一次真实探测(建 DirectML 会话)。成功返回该设备号,失败返回 -1 并把
    /// 完整诊断写入 fail(含 HRESULT/类型/Message/InnerException)。只建会话、不推理(探测极轻)。</summary>
    private static int TryProbeDmlDevice(string model, int dmlDevice, out string fail)
    {
        fail = "";
        try
        {
            var opts = new SessionOptions();
            opts.AppendExecutionProvider_DML(dmlDevice);
            using var s = new InferenceSession(model, opts);   // DML 设备创建失败 → 抛 → 该号不可用
            AppLogger.Info($"DirectML 探测:设备 #{dmlDevice} 建会话成功(只探这一个映射目标,不再遍历 0..3)");
            return dmlDevice;
        }
        catch (Exception ex)
        {
            fail = DescribeDmlFailure("EnsureDmlProbeAsync → AppendExecutionProvider_DML + new InferenceSession", dmlDevice, ex);
            LogDmlFailure("DirectML 探测(设备 #" + dmlDevice + " 建会话)", dmlDevice, ex);
            return -1;
        }
    }

    /// <summary>实测可用的 DirectML 设备号(-1=未探测/不可用;与 DmlProbeCompleted 一起看才能区分)。</summary>
    public static int DmlFallbackOk => _dmlFirstOk;

    /// <summary>CPU(ONNX)逐帧超分的【保守】秒/帧常数(1080p 源面积基准)。第 3 项:预估"落到 CPU 后要多久"。
    /// 【取值来源 —— 不是编的,是两处真实实测中【较小】的那个】
    /// ① 本项目既有的 GPU 逐帧常数在 AlhPro.Core.VideoPipeline:1080p 单帧 waifu2x≈0.18 秒、realesrgan≈0.45 秒
    ///    (ncnn-Vulkan GPU 实测);
    /// ② 真实诊断包实测:RTX 5060 Laptop(Blackwell)+ Ryzen 9 8940HX(32 线程)的机器上,DirectML 建会话失败后
    ///    ONNX 超分静默落 CPU,日志实测 35 秒 3 帧、64 秒 8 帧 → **≈8 秒/帧**(同一素材同一参数);
    /// ③ 本机复测(2026 验证工程 _dmlfix_verify,真实 EsrganOnnxService 代码,i7-12650H 16 线程、
    ///    本次新增的 intra-op=6 限制下):waifu2x-cunet2x 1080p 源帧 = **12.6 秒/帧**
    ///    (768² 小图 3.0 秒/帧 @ 并行度 5.5 核,1080p 12.6 秒/帧 @ 5.2 核);
    /// ④ 8 / 0.18 ≈ 44 倍 —— 与"CPU 逐帧神经网络推理比 GPU 慢一到两个数量级"的常识量级一致
    ///    (项目内另有"极小输入下 CPU 反而快 13/19 倍"的实测,那只在 96px 级小图上成立,不能用来做保守预估)。
    /// 【取 8.0,即两处实测中较小的那个】含义要说清楚:该常数在【更慢的 CPU 上会低估】(本机实测 12.6,已是 8 的 1.6 倍)。
    /// 之所以仍取 8.0:它对应本报告的真实故障场景(同一台机器、同一引擎、DirectML 不可用 → CPU),
    /// 且面积项已按源像素线性放大;界面/日志里因此【明确写出"秒/帧来源"并注明实际可能更慢】,
    /// 而不是把预估值当成承诺。若将来拿到更多机器数据,应上调本常数(只允许上调:宁可高估,不可低估)。
    /// 按【源】面积线性缩放(ONNX 模型是固定 4x 或 2x 的,推理成本只取决于模型输入=源帧尺寸,
    /// 与用户选的输出倍率无关,因此这里【不】再乘倍率 —— 乘了会和面积项重复放大)。
    /// 命中 PerfMemory 实测时取 max(实测, 本常数):只增不减,避免"上次是 GPU 跑的 0.18 秒/帧"把 CPU 预估压低。</summary>
    public const double CpuSecondsPerFrame1080p = 8.0;

    /// <summary>预估"若超分落到 CPU,该阶段约需多少秒"。frames = 待超分帧数(= 源帧数,补帧在超分之后放大帧数)。
    /// measuredPerFrame1080p:调用方从 PerfMemory 查到的同配置实测秒/帧(1080p 基准;没有传 null 或 0)。
    /// perFrame 为出参:实际采用的"秒/帧"(1080p 基准×面积),供界面显示"约 X 秒/帧"。</summary>
    public static double EstimateCpuUpscaleSeconds(long frames, int srcW, int srcH, double measuredPerFrame1080p, out double perFrame)
    {
        double areaN = Math.Max(0.25, (double)Math.Max(1, srcW) * Math.Max(1, srcH) / 2073600.0);   // 与 PerfMemory/VideoPipeline 同口径
        double basePer = Math.Max(CpuSecondsPerFrame1080p, measuredPerFrame1080p > 0 ? measuredPerFrame1080p : 0);
        perFrame = basePer * areaN;
        return Math.Max(0, frames) * perFrame;
    }

    /// <summary>DirectML 不可用时的一次性明确提示(避免"静默掉 CPU → 慢几倍 → 以为不能用")。
    /// 常见于 RTX 50 系(Blackwell)但驱动较旧、或 AMD/Intel 驱动不完整。</summary>
    private static void WarnDmlUnavailable(string detail)
    {
        if (_dmlWarned) return;
        _dmlWarned = true;
        AppLogger.Warn($"⚠ GPU 加速(DirectML)不可用 — {detail}。已自动改用 CPU(稳定但慢数倍),建议更新显卡驱动(50 系需较新驱动)后重启软件再试。");
    }

    /// <summary>按输入尺寸选择 ONNX 推理设备:大图(>256px)→ DirectML GPU(快,实测 512→2048 快 7.7 倍);
    /// 小图 → CPU(小任务 GPU 启动开销 > 算力收益,实测 96px GPU 反而慢 13 倍)。
    /// wantsGpu=false(调用方要求纯 CPU,如抠图)时返回 -1。</summary>
    public static int PickDevice(int width, int height, bool wantsGpu = true)
    {
        if (!wantsGpu) return -1;
        int maxSide = Math.Max(width, height);
        if (maxSide <= 256) return -1;   // 小图:CPU 更快(不折腾 GPU)
        // 用户显式选了 CPU(GpuIndex<0):必须尊重(不按尺寸拉回 GPU)——GPU 有问题的机器正是这么选的
        if (AppSettings.GpuIndex < 0) return -1;
        // 【修复】判定 DirectML 是否可用:用"直接调用 DirectML 建会话"的实测结果(EnsureDmlProbeAsync → _dmlFirstOk),
        // 而不是用 VulkanCheck.GpuAvailable——Vulkan 与 DirectML 是两套完全不同的运行时,
        // 之前用 Vulkan 判定会在"DirectML 可用但 Vulkan 检测失败(无 Vulkan runtime/驱动缺/某 GPU Vulkan 支持不全)"
        // 的机器上误判为不可用,把 ONNX 超分/视频超分静默拖回 CPU(表现为:用户选了 GPU,实际 CPU 在跑)。
        // 探测结果有效(已完成)且确认无任何 DirectML 设备时才降 CPU;探测进行中/成功时走 GPU。
        if (_dmlProbeState == 2)
        {
            if (_dmlFirstOk < 0)
            {
                WarnDmlUnavailable("DirectML 无可用设备(启动自检/探测失败)");
                return -1;
            }
            // 有可用 DirectML 设备:优先用户选的 GpuIndex,否则用实测可用的第一个(避免越界/误判)
            int gpu = AppSettings.GpuIndex >= 0 ? AppSettings.GpuIndex : _dmlFirstOk;
            return gpu;
        }
        try
        {
            // 探测尚未完成(启动后极短暂窗口):保守判断,避免因 Vulkan 误判而否定 GPU
            if (!VulkanCheck.GpuAvailable && !EngineService.IsBlackwellGpu()) return -1;
        }
        catch { }
        int fallback = AppSettings.GpuIndex >= 0 ? AppSettings.GpuIndex : 0;
        return fallback;
    }

    /// <summary>ONNX 超分一张图(4x)。scale=目标倍数(4x 原生;2x 也走 4x 再缩回)。
    /// modelPath: 指定模型;null = Real-ESRGAN 自动查找。
    /// gpuId 传入 -2 表示"自动"(按输入大小选设备);其余按传入值。
    /// <paramref name="dmlDeviceHint"/>:调用方【已经解析好】的 DirectML 设备号(与 sessionOverride 配套:
    /// 那批会话就是按它建的)。null = 未告知,由 RunTile 自行按 gpuId 解析。见 RunTile 的 C-2 说明。</summary>
    public static async Task UpscaleAsync(string input, string output, double scale,
        int gpuId = -1, IProgress<(int pct, string msg)>? progress = null, CancellationToken ct = default,
        string? modelPath = null, InferenceSession? sessionOverride = null, int? dmlDeviceHint = null)
    {
        modelPath ??= FindModel()
            ?? throw new FileNotFoundException("缺少超分模型:RealESRGAN_x4plus.onnx。");
        // -2 = 自动选设备(按输入尺寸)
        try
        {
            if (gpuId == -2)
            {
                using var probe = new System.Drawing.Bitmap(input);
                gpuId = PickDevice(probe.Width, probe.Height);
            }
        }
        catch { }
        progress?.Report((5, "加载 ONNX 模型..."));
        await Task.Run(() => RunCore(input, output, scale, modelPath, gpuId, progress, ct, sessionOverride, dmlDeviceHint), ct);
        progress?.Report((100, "完成"));
    }

    /// <summary>ONNX 目录批处理(视频逐帧超分用):遍历 inputDir 的 PNG,逐帧 UpscaleAsync 输出到 outputDir。
    /// 供视频超分在 50 系/无独显设备走 ONNX(不走会崩的 ncnn-vulkan)。modelPath=null 用 Real-ESRGAN。
    /// 【并行优化】视频超分逐帧并行:早期串行(被共享缓存锁串行化,GPU 只用了单路)。
    /// 现用 2 路独立 DirectML 会话并行(实测 2 路 ≈1.25x,tile 分块下 GPU 算力爬升;3/4 路不再增益,仅显存吃紧)。
    /// 仍保留逐帧进度 + 取消,失败帧回退原帧(不中断)。</summary>
    public static async Task UpscaleDirAsync(string inputDir, string outputDir, double scale,
        int gpuId = -1, IProgress<(int pct, string msg)>? progress = null, CancellationToken ct = default,
        string? modelPath = null, int globalBaseFrames = 0, int globalTotalFrames = 0, Func<Task>? pauseWait = null)
    {
        var files = Directory.EnumerateFiles(inputDir, "*.*")
            .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        Directory.CreateDirectory(outputDir);
        if (files.Length == 0) return;
        modelPath ??= FindModel()
            ?? throw new FileNotFoundException("缺少超分模型:RealESRGAN_x4plus.onnx。");
        // -2 = 每帧自动选设备(视频帧通常大,落 GPU;小帧自动 CPU)
        bool auto = gpuId == -2;
        // 决定会话数:GPU 走 2 路并行(DirectML 多会话);CPU 保持 1(CPU 多会话每帧建会增加开销)
        bool wantGpu = auto ? true : gpuId >= 0;
        // 大显存(12G+)ONNX 逐帧超分用 3 路并行(5070 Ti 等更有算力,多活能让 GPU 更饱和);小显存保持 2,避免爆显存
        int concurrency = wantGpu ? (SafeRender.EffectiveVramGB >= 12 ? 3 : 2) : 1;
        // 【熔断快速失败】DirectML 已被系统摘除/挂死(887A0005/887A0006):本进程内不可能恢复,再建会话、再逐帧试
        // 都必然失败。立刻抛出让调用方按【批次】回退源帧(几十秒),而不是每批重来一遍(几小时)。
        // wantGpu=false 表示调用方明确要 CPU(本机无 GPU 可用)——那是唯一允许用 CPU 的场景,不在此列。
        if (wantGpu && DmlDeviceDead)
            throw new InvalidOperationException(
                "GPU(DirectML)已被系统摘除/挂死,本进程内无法恢复——已停止超分尝试(不降级到慢速 CPU)。请重启软件后重试。");
        // 逐帧进度用【全局帧】(跨批次累计),显示"超分 第 N 帧 / 共 M 帧",百分比按全局帧算
        bool global = globalTotalFrames > 0;
        // 【修复 用户落到核显】auto(-2)此前硬编码设备 0:混合显卡(AMD/Intel 核显+独显)机上
        // Vulkan 设备 0 往往是核显,视频超分会静默跑核显(慢).改成用启动自检已纠偏的
        // AppSettings.GpuIndex(=实测可用的独显 ncnn 编号),经 ToDmlDevice 名匹配映射到正确 DirectML 卡.
        // 提到循环外:每路 worker 解析出的都是同一个设备号,且熔断日志要报得出真实设备号(报 -1 会误导排查)。
        int dmDevice = !wantGpu ? -1
            : auto ? (AppSettings.GpuIndex >= 0 ? EngineService.ResolveDmlDevice(AppSettings.GpuIndex) : EsrganOnnxService.DmlFallbackOk)
            : EngineService.ResolveDmlDevice(gpuId);
        // 【C-3】想要 GPU 却解析不出合法的 DirectML 设备号(名字匹配失败返回 -1 / 启动探测未完成 / DmlFallbackOk=-1)时:直接抛。
        // 原代码会把这个非法号交给 AppendExecutionProvider_DML → 必然失败 → 被 catch 吞掉 → 静默建出【纯 CPU 会话】,
        // 于是"设置里写 GPU、实际整批 3 路 × 240 帧全跑 CPU(一整夜)"且日志里只有一条容易被忽略的 warning。
        // 这里宁可抛出:VideoService 会按【批次】回退源帧/改走 ncnn 重跑,不会静默锁 CPU。
        if (wantGpu && dmDevice < 0)
            throw new InvalidOperationException(
                $"ONNX 超分:无法把 GPU 编号 {gpuId} 映射到可用的 DirectML 设备(不降级到慢速 CPU)——"
                + "请在设置里重新选择显卡后重试;若反复出现,建议更新显卡驱动。");
        // 预创建独立会话池(每个并行 worker 一个;绕开共享缓存锁,支持并发 Run)
        var sessions = new Microsoft.ML.OnnxRuntime.InferenceSession?[concurrency];
        // 每个会话【真实】所在的 DirectML 设备号(-1 = 该会话其实是 CPU 会话)。绝不靠"想要 GPU"推断:
        // CPU 会话推理成功后若去清零 GPU 连击,设备级熔断就永远无法触发(见 RunTile 的 C-4 说明)。
        var sessionDml = new int[concurrency];
        for (int s = 0; s < concurrency; s++) sessionDml[s] = -1;
        int poolFailed = 0;   // 【C-3】建会话失败的 worker 数:必须留痕,不许静默少一路并行
        for (int s = 0; s < concurrency; s++)
        {
            try
            {
                // 【第 4 项①】CPU 会话必须显式限制线程数(见 ApplyConservativeCpuThreads)。
                // 注意:下面的 opts 在 DML append 失败时会【降级成 CPU 会话】,所以线程数必须在
                // append 结果确定之后、new InferenceSession 之前补上 —— 否则"本想跑 GPU、实际落 CPU"
                // 的那条路径会退回"ONNX 默认吃满全部物理核"(正是 CPU 100% / 界面卡的成因)。
                var opts = new SessionOptions();
                bool onDml = false;
                if (wantGpu)
                {
                    try { opts.AppendExecutionProvider_DML(dmDevice); onDml = true; }
                    catch (Exception dmlEx)
                    {
                        // 设备被摘除/挂死时建会话本身就会抛 887A:此时绝不能静默建出 CPU 会话把整批帧跑在 CPU 上
                        // (2~3 路 × 240 帧 × 每帧几十秒 = 几小时)。原样重抛,由外层 catch 统一熔断(避免异常套两层)。
                        if (AlhPro.Core.GpuFault.IsPersistentDeviceError(dmlEx)) throw;
                        // 【不要轻易掉 CPU】DirectML 建会话失败:明确记录"卡在 GPU 哪一步",而不是静默落 CPU。
                        // 这样"4060 显示 GPU 却跑几小时"的诊断包能一眼看到是 DirectML 挂在这(驱动过旧 / DML 设备不可用)。
                        // 走到这里说明本机没有可用的 GPU ONNX 运行时,CPU 是唯一计算设备 —— 这是允许用 CPU 的场景。
                        // 【第 1 项】原因升级为完整诊断(设备号/HRESULT 十六进制/异常类型/Message 首行/InnerException
                        // 类型与 HRESULT + 定性结论):只留 "原因:xxx" 时,诊断包里分不清是显存不足 0x8007000E、
                        // 设备摘除 0x887A0005/6,还是 provider 注册失败 —— 三者处置完全不同。
                        AppLogger.Warn($"⚠ ONNX DirectML 会话创建失败(本会话将退回 CPU) — {DescribeDmlFailure("UpscaleDirAsync 预建会话池.AppendExecutionProvider_DML", dmDevice, dmlEx)}"
                            + " ——本机无可用 GPU ONNX,本会话将退回 CPU(第 3 项:已按实测速度给出预估并在界面醒目提示;若持续出现请更新显卡驱动后重试)");
                    }
                }
                // 【第 4 项①】到此还没有 DML(本就要 CPU,或 append 刚刚失败)→ 显式限制 ONNX CPU 线程数。
                // GPU 会话(onDml=true)不设:算子跑在设备上,不该被本进程线程数约束(不动正常路径)。
                if (!onDml) ApplyConservativeCpuThreads(opts);
                sessions[s] = new Microsoft.ML.OnnxRuntime.InferenceSession(modelPath, opts);
                // 【C-4 ④】只有 DML append 真的成功,这个会话才算"跑在 DML 上";否则它其实是 CPU 会话,
                // 之后绝不能拿它去清零/查询 GPU 连击表。这个号会随会话一起传给 RunTile 复用(编号空间只解析一次)。
                sessionDml[s] = onDml ? dmDevice : -1;
            }
            // 设备级失效必须往上抛:被这里吞掉就等于"熔断器又失效一次",整批照样在 CPU 上跑到天荒地老。
            // (new InferenceSession 本身也会在设备被摘除时抛 887A,同样走这条重抛。)
            catch (Exception ex) when (AlhPro.Core.GpuFault.IsPersistentDeviceError(ex))
            {
                TripDmlDead(dmDevice, ex);
                foreach (var ss in sessions) try { ss?.Dispose(); } catch { }   // 【不泄漏】已建的会话先 Dispose 再抛
                throw new InvalidOperationException(
                    $"GPU(DirectML)已被系统摘除/挂死,无法创建推理会话(不降级到慢速 CPU): {ex.Message.Split('\n')[0]}\n请重启软件后重试。", ex);
            }
            // 【C-3】原先是 `catch { sessions[s] = null; }`:零日志吞掉任何非持续性异常(OOM、非法设备号、
            // provider 注册失败)。那个 null 会让该 worker 回落到共享缓存路径(可能建出 CPU 会话)→ 整批帧里
            // 有一路静默跑 CPU,而日志一行线索都没有。现在:记日志 + 计数,下面统一降并发并明确告警。
            catch (Exception ex)
            {
                sessions[s] = null;
                poolFailed++;
                AppLogger.Warn($"⚠ ONNX 超分会话创建失败(第 {s + 1}/{concurrency} 路,原因:{ex.Message.Split('\n')[0]})");
            }
        }
        if (poolFailed > 0 || (wantGpu && Array.Exists(sessionDml, x => x < 0)))
        {
            // 【C-3】会话池降级,必须显式且留痕:只保留一个可用会话(其余 Dispose,绝不泄漏)。
            // ① 任一路建会话失败(原先 `catch { sessions[s]=null; }` 静默吞掉):失败的 worker 会回落到共享缓存
            //    路径,整批帧里就有一路"静默走别的路径",而日志一行线索都没有;
            // ② 想要 GPU 却只建出 CPU 会话:3 路并行 CPU 推理只是把 CPU 抢成 3 份(每路一个线程池),总时间不变、
            //    内存三倍,而且"设置写 GPU、实际跑 CPU"必须是一眼能看到的告警(绝不静默共存)。
            int keep = Array.FindIndex(sessions, x => x != null);
            for (int s = 0; s < sessions.Length; s++)
                if (s != keep) { try { sessions[s]?.Dispose(); } catch { } }
            if (keep < 0)
                throw new InvalidOperationException(
                    $"ONNX 超分:本批 {concurrency} 路推理会话全部创建失败(不降级到慢速 CPU)——该批帧将回退源帧缩放。"
                    + "请重启软件后重试;若反复出现,建议关闭其他占用显存的程序并更新显卡驱动。");
            AppLogger.Warn(poolFailed > 0
                ? $"⚠ ONNX 超分会话池创建不完整({concurrency - poolFailed}/{concurrency} 成功)——本批并行度由 {concurrency} 降为 1,速度会变慢(原因见上一条日志)"
                : $"⚠ ONNX 超分:想要 GPU 却至少有一路只建出 CPU 会话(DirectML 设备 {dmDevice} 不可用)——本批并行度由 {concurrency} 降为 1,并整批在 CPU 上运行(不占 GPU 键,设备级熔断不受影响;详见上一条日志)");
            sessions = new[] { sessions[keep] };
            sessionDml = new[] { sessionDml[keep] };
            concurrency = 1;
        }
        try
        {
            int done = 0;
            // 【中止协调】设备被摘除后要让所有 worker 尽快停手。不在 worker 内直接抛:那样 Task.WhenAll 可能挑中
            // 兄弟 worker 的 OperationCanceledException,被上层当成"用户取消"而中止整段视频。改为记录致命异常、
            // 置中止标志,等 WhenAll 收齐后再统一抛出。
            int abortFlag = 0;
            Exception? fatal = null;
            // 【正确并行】每个 worker 独占一个 session, worker 之间分片处理帧 —— 保证同一个 session
            // 同一时刻只被一个 worker 用(同一 InferenceSession 不能并发 Run,否则 AccessViolation/OnnxRuntimeException)。
            var workers = new System.Threading.Tasks.Task[concurrency];
            for (int w = 0; w < concurrency; w++)
            {
                int wi = w;
                workers[w] = System.Threading.Tasks.Task.Run(async () =>
                {
                    for (int i = wi; i < files.Length; i += concurrency)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (Volatile.Read(ref abortFlag) != 0) break;   // 兄弟帧已确认设备永久失效,别再白试
                        if (pauseWait != null) await pauseWait();   // 暂停:当前帧跑完即停(ONNX/CPU 也能暂停)
                        var outPath = Path.Combine(outputDir, Path.ChangeExtension(Path.GetFileName(files[i]), ".png"));
                        try
                        {
                            var sess = sessions[wi];
                            int sessDml = sessionDml[wi];   // 该会话【真实】所在的 DML 号(-1 = 其实是 CPU 会话)
                            UpscaleAsync(files[i], outPath, scale, auto ? -2 : gpuId, null, ct, modelPath, sess, sessDml)
                                .GetAwaiter().GetResult();
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception ex)
                        {
                            // 【持续性设备错误 = 熔断,不重试也不落 CPU】887A0005/887A0006 之后本进程的 D3D 设备已被
                            // Windows 摘除,后续每一次 DirectML 调用必然失败;CPU 推理虽然能跑但要几小时(项目铁律
                            // 「绝不跑慢速 CPU」)。→ 记下致命异常并中止,交调用方按【批次】回退源帧。
                            if (wantGpu && AlhPro.Core.GpuFault.IsPersistentDeviceError(ex))
                            {
                                TripDmlDead(dmDevice, ex);
                                Interlocked.CompareExchange(ref fatal, ex, null);
                                Volatile.Write(ref abortFlag, 1);
                                break;
                            }
                            // 【单帧偶发失败 = 缩放源帧,不跑 CPU 推理】尺寸必须与正常输出一致:直接 File.Copy 会往
                            // 输出目录混进低分辨率帧,ffmpeg 按第一帧声明流头 → 成片花屏、退出码 0、日志无迹可查。
                            // 缩放只要几十毫秒;CPU 神经网络推理要几十秒/帧,一批 240 帧就是几小时。
                            AppLogger.Warn($"ONNX 超分失败({ex.Message.Split('\n')[0]})——该帧回退为源帧缩放(不跑慢速 CPU)");
                            try { WriteResizedFallback(files[i], outPath, scale); }
                            catch { try { File.Copy(files[i], outPath, true); } catch { } }
                        }
                        finally
                        {
                            int d = System.Threading.Interlocked.Increment(ref done);
                            if (Volatile.Read(ref abortFlag) == 0)
                            {
                                if (global)
                                {
                                    // 全局逐帧进度:当前帧全局号 = globalBase(本批起始) + d(本批已完成)
                                    int globalDone = globalBaseFrames + d;
                                    int pct = (int)Math.Clamp(globalDone * 100.0 / globalTotalFrames, 0, 100);
                                    progress?.Report((pct, $"超分 第 {globalDone} 帧 / 共 {globalTotalFrames} 帧"));
                                }
                                else
                                {
                                    int pct = (int)(d * 100.0 / files.Length);
                                    progress?.Report((pct, $"超分 {d}/{files.Length} 帧({Path.GetFileName(files[i])})"));
                                }
                            }
                        }
                    }
                }, ct);
            }
            await System.Threading.Tasks.Task.WhenAll(workers).ConfigureAwait(false);
            // 设备永久失效:把真正的病因抛给调用方(而不是被吞掉后让上层以为这批"跑完了")
            if (fatal != null)
                throw new InvalidOperationException($"ONNX 超分中止:GPU 设备已失效({fatal.Message.Split('\n')[0]})", fatal);
        }
        finally
        {
            foreach (var s in sessions) try { s?.Dispose(); } catch { }
        }
    }

    /// <summary>单帧超分失败时的降级写出:把源帧按目标尺寸高质量缩放(几十毫秒),不跑 CPU 神经网络推理。
    /// 尺寸必须等于 源×倍数 —— 混进不同分辨率的帧会让 ffmpeg 按第一帧声明流头,成片花屏且日志查不到原因。</summary>
    private static void WriteResizedFallback(string srcFile, string outPath, double scale)
    {
        int w = 0, h = 0;
        try
        {
            using var s = new System.Drawing.Bitmap(srcFile);
            w = Math.Max(1, (int)Math.Round(s.Width * scale));
            h = Math.Max(1, (int)Math.Round(s.Height * scale));
        }
        catch { }
        if (w > 0 && h > 0) { EngineService.ResizeImageTo(srcFile, outPath, w, h); return; }
        File.Copy(srcFile, outPath, true);   // 连尺寸都读不出:尽力保帧号连续
    }

    private static void RunCore(string input, string output, double scale, string modelPath, int gpuId,
        IProgress<(int pct, string msg)>? progress, CancellationToken ct, InferenceSession? sessionOverride = null,
        int? dmlDeviceHint = null)
    {
        using var src = new System.Drawing.Bitmap(input);
        int sw = src.Width, sh = src.Height;
        int ow = (int)Math.Round(sw * scale), oh = (int)Math.Round(sh * scale);
        bool hasAlpha = src.PixelFormat.HasFlag(System.Drawing.Imaging.PixelFormat.Alpha)
            || src.PixelFormat == System.Drawing.Imaging.PixelFormat.Format32bppArgb;

        if (hasAlpha)
        {
            // ===== 透明底保护:ONNX 只处理 RGB(alpha 会丢/脏),这里分离处理 =====
            // ① 提取 alpha 通道(缩放后恢复用) ② RGB 填白(防透明区超分成脏色) ③ 超分 ④ 恢复 alpha
            RunCoreAlphaSafe(src, output, scale, modelPath, gpuId, progress, ct, TileFor(sw, sh), 64, sessionOverride, dmlDeviceHint);
            return;
        }

        // ===== 分块保护(实测:GPU 整帧喂 1080p → DirectML OOM 崩溃;CPU 慢到 210s/帧)=====
        // 输入超 512 就切成块(带 32px 重叠羽化拼回):GPU 每块 0.3~0.5s,1080p 也稳;速度数倍提升。
        const int Tile = 512;
        const int Overlap = 64;   // 32→64:分块共享上下文更多,接缝过渡带更宽、高纹理更难看出"分块"(代价:边缘计算略增)
        if (sw > Tile || sh > Tile)
        {
            RunCoreTiled(src, output, scale, modelPath, gpuId, progress, ct, Tile, Overlap, sessionOverride, dmlDeviceHint);
            return;
        }

        // 单块(整图 ≤ Tile):直接推理
        using var tileBmp = RunTile(src, modelPath, gpuId, ct, sessionOverride, dmlDeviceHint);
        int tW = tileBmp.Width, tH = tileBmp.Height;
        if (Math.Abs(tW - ow) > 1 || Math.Abs(tH - oh) > 1)
            SaveScaled(tileBmp, output, ow, oh);
        else
            tileBmp.Save(output, System.Drawing.Imaging.ImageFormat.Png);
        progress?.Report((100, "完成"));
    }

    private static int TileFor(int w, int h)
    {
        int max = Math.Max(w, h);
        // 显存自适应分块(与 ncnn 同口径):大显存卡(12G+,如 5070 Ti)用更大的块(最多 768,块少→GPU 吃得饱→更快),
        // 小显存沿用保守值(512);小图(≤512)直接用原尺寸、无需分块。
        if (max <= 512) return Math.Max(64, max);
        return Math.Max(512, SafeRender.GetTileSize());
    }

    /// <summary>透明底保护:提取 alpha → RGB 填白 → 超分 → 恢复 alpha(输出 32bpp Argb)。</summary>
    private static void RunCoreAlphaSafe(System.Drawing.Bitmap src, string output, double scale, string modelPath,
        int gpuId, IProgress<(int pct, string msg)>? progress, CancellationToken ct, int tile, int overlap,
        InferenceSession? sessionOverride = null, int? dmlDeviceHint = null)
    {
        int sw = src.Width, sh = src.Height;
        int ow = (int)Math.Round(sw * scale), oh = (int)Math.Round(sh * scale);
        // 提取 alpha(经图像缩放,带插值,透明边缘柔和)
        using var alphaBmp = new System.Drawing.Bitmap(sw, sh, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(alphaBmp))
        {
            g.Clear(System.Drawing.Color.Transparent);
            g.DrawImage(src, 0, 0);
        }
        // RGB 填白(透明区 → 白,防 ONNX 把透明区超分成黑边/脏色)
        using var rgbSrc = new System.Drawing.Bitmap(sw, sh, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (var g = System.Drawing.Graphics.FromImage(rgbSrc))
        {
            g.Clear(System.Drawing.Color.White);
            g.DrawImage(src, 0, 0);
        }
        // 超分 RGB(临时文件)
        var tmpRgb = Path.Combine(EngineService.TempRoot, $"alh_alpha_{Guid.NewGuid():N}.png");
        try
        {
            if (sw > tile || sh > tile)
                RunCoreTiled(rgbSrc, tmpRgb, scale, modelPath, gpuId, null, ct, tile, overlap, sessionOverride, dmlDeviceHint);
            else
            {
                using var tileBmp = RunTile(rgbSrc, modelPath, gpuId, ct, sessionOverride, dmlDeviceHint);
                if (Math.Abs(tileBmp.Width - ow) > 1 || Math.Abs(tileBmp.Height - oh) > 1)
                    SaveScaled(tileBmp, tmpRgb, ow, oh);
                else
                    tileBmp.Save(tmpRgb, System.Drawing.Imaging.ImageFormat.Png);
            }
            // 输出 32bpp:超分 RGB + 缩放后的 alpha(高效:LockBits 指针写 BGRA,不用逐像素 SetPixel)
            using var outBmp = new System.Drawing.Bitmap(ow, oh, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var rgbFull = new System.Drawing.Bitmap(tmpRgb);
            using var scaledAlpha = new System.Drawing.Bitmap(alphaBmp, ow, oh);   // 缩放 alpha(带插值)
            unsafe
            {
                var rect = new System.Drawing.Rectangle(0, 0, ow, oh);
                var dstData = outBmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var rgbData = rgbFull.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                    System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                var aData = scaledAlpha.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try
                {
                    byte* dst = (byte*)dstData.Scan0;
                    byte* rgb = (byte*)rgbData.Scan0;
                    byte* alpha = (byte*)aData.Scan0;
                    for (int y = 0; y < oh; y++)
                    {
                        byte* dRow = dst + y * dstData.Stride;
                        byte* rRow = rgb + y * rgbData.Stride;
                        byte* aRow = alpha + y * aData.Stride;
                        for (int x = 0; x < ow; x++)
                        {
                            dRow[x * 4] = rRow[x * 3];         // B
                            dRow[x * 4 + 1] = rRow[x * 3 + 1]; // G
                            dRow[x * 4 + 2] = rRow[x * 3 + 2]; // R
                            dRow[x * 4 + 3] = aRow[x * 4 + 3]; // A(从缩放 alpha 取)
                        }
                    }
                }
                finally
                {
                    outBmp.UnlockBits(dstData);
                    rgbFull.UnlockBits(rgbData);
                    scaledAlpha.UnlockBits(aData);
                }
            }
            outBmp.Save(output, System.Drawing.Imaging.ImageFormat.Png);
        }
        finally
        {
            try { File.Delete(tmpRgb); } catch { }
        }
        progress?.Report((100, "完成"));
    }

    /// <summary>分块超分:大图切 Tile 网格(步长=Tile-Overlap),逐块推理后按"邻块左/上边缘 smoothstep 羽化"贴回。
    /// 关键两点(消除"一块一块"拼贴接缝):
    /// ①【倍率对齐】模型固有倍率 modelScale(waifu2x=2,其余=4)与用户 scale 不一致时(如 esrgan 4x 模型做 2x/3x、
    ///    waifu2x 2x 模型做 4x),先把每块缩放到目标倍率 scale——否则按 x0*scale 定位会错位成"马赛克拼贴";
    /// ②【羽化融合】相邻块的左/上边缘用 smoothstep 权重交叉混合(而非硬复制贴回),消除块边界的"硬切换"接缝。
    /// 这是 ncnn 路径(UpscaleTiledAsync)早已采用的正确做法;此前 ONNX 路径只有硬复制且倍率错位,故 50 系/无独显
    ///    (走 ONNX)大图/视频帧会出现明显拼贴接缝。</summary>
    private static void RunCoreTiled(System.Drawing.Bitmap src, string output, double scale, string modelPath,
        int gpuId, IProgress<(int pct, string msg)>? progress, CancellationToken ct, int tile, int overlap,
        InferenceSession? sessionOverride = null, int? dmlDeviceHint = null)
    {
        int sw = src.Width, sh = src.Height;
        int ow = (int)Math.Round(sw * scale), oh = (int)Math.Round(sh * scale);
        using var outBmp = new System.Drawing.Bitmap(ow, oh, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (var g = System.Drawing.Graphics.FromImage(outBmp))
            g.Clear(System.Drawing.Color.FromArgb(255, 18, 18, 18));   // 中性底色,防右/下细缝显黑

        int stride = tile - overlap;
        int cols = (sw + stride - 1) / stride;
        int rows = (sh + stride - 1) / stride;
        int total = cols * rows;
        progress?.Report((5, $"大图分块: {cols}×{rows}={total} 块(超分 {scale:0.##}x,自动分块防爆显存)..."));

        int modelScale = modelPath.Contains("waifu2x", StringComparison.OrdinalIgnoreCase) ? 2 : 4;
        // 重叠区羽化宽度(输出像素),留 2px 余量保证淡入区落在真实重叠内
        int ovFade = Math.Max(1, (int)Math.Round(overlap * scale) - 2);

        var rect = new System.Drawing.Rectangle(0, 0, ow, oh);
        var cData = outBmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadWrite,
            System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        try
        {
            unsafe
            {
                byte* cP0 = (byte*)cData.Scan0.ToPointer();
                int cStride = cData.Stride;
                int done = 0;
                for (int ty = 0; ty < rows; ty++)
                {
                    for (int tx = 0; tx < cols; tx++)
                    {
                        ct.ThrowIfCancellationRequested();
                        int x0 = tx * stride, y0 = ty * stride;
                        int tw = Math.Min(tile, sw - x0), th = Math.Min(tile, sh - y0);
                        using (var cropped = new System.Drawing.Bitmap(tw, th, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
                        {
                            using (var g = System.Drawing.Graphics.FromImage(cropped))
                                g.DrawImage(src, new System.Drawing.Rectangle(0, 0, tw, th),
                                    new System.Drawing.Rectangle(x0, y0, tw, th), System.Drawing.GraphicsUnit.Pixel);
                            using var tileOut = RunTile(cropped, modelPath, gpuId, ct, sessionOverride, dmlDeviceHint);
                            // ①缩放到目标倍率:模型固有倍率 != 用户 scale 时先 resize(显式 24bpp,保证 LockBits 格式)
                            var tileFinal = tileOut;
                            if (Math.Abs(modelScale - scale) > 0.001)
                            {
                                int dw = Math.Max(1, (int)Math.Round(tw * scale));
                                int dh = Math.Max(1, (int)Math.Round(th * scale));
                                if (Math.Abs(dw - tileOut.Width) > 1 || Math.Abs(dh - tileOut.Height) > 1)
                                {
                                    var rs = new System.Drawing.Bitmap(dw, dh, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                                    using (var gg = System.Drawing.Graphics.FromImage(rs))
                                        gg.DrawImage(tileOut, 0, 0, dw, dh);
                                    tileFinal = rs;
                                }
                            }
                            int dx = (int)Math.Round(x0 * scale), dy = (int)Math.Round(y0 * scale);
                            int twd = tileFinal.Width, thd = tileFinal.Height;
                            var tRect = new System.Drawing.Rectangle(0, 0, twd, thd);
                            var tData = tileFinal.LockBits(tRect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                                System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                            try
                            {
                                byte* tP0 = (byte*)tData.Scan0.ToPointer();
                                int tStride = tData.Stride;
                                for (int py = 0; py < thd; py++)
                                {
                                    int cy = dy + py;
                                    if (cy >= oh) break;
                                    byte* tRow = tP0 + py * tStride;
                                    byte* cRow = cP0 + cy * cStride;
                                    // ②smoothstep 淡入:左/上边缘 0→1,避免硬切换接缝
                                    double wy = (ty > 0 && py < ovFade) ? SmoothStep((double)(py + 1) / ovFade) : 1.0;
                                    for (int px = 0; px < twd; px++)
                                    {
                                        int cx = dx + px;
                                        if (cx >= ow) break;
                                        double wx = (tx > 0 && px < ovFade) ? SmoothStep((double)(px + 1) / ovFade) : 1.0;
                                        double w = wx * wy;
                                        int ti = px * 3, ci = cx * 3;
                                        if (w >= 1.0)
                                        {
                                            cRow[ci] = tRow[ti];
                                            cRow[ci + 1] = tRow[ti + 1];
                                            cRow[ci + 2] = tRow[ti + 2];
                                        }
                                        else
                                        {
                                            double iw = 1.0 - w;
                                            cRow[ci] = (byte)(cRow[ci] * iw + tRow[ti] * w);
                                            cRow[ci + 1] = (byte)(cRow[ci + 1] * iw + tRow[ti + 1] * w);
                                            cRow[ci + 2] = (byte)(cRow[ci + 2] * iw + tRow[ti + 2] * w);
                                        }
                                    }
                                }
                            }
                            finally
                            {
                                tileFinal.UnlockBits(tData);
                                if (!ReferenceEquals(tileFinal, tileOut)) tileFinal.Dispose();
                            }
                        }
                        done++;
                        progress?.Report((5 + (int)(85.0 * done / total), $"AI 超分 已处理 {done}/{total} 块..."));
                    }
                }
            }
        }
        finally { outBmp.UnlockBits(cData); }

        // 兜底:舍入致输出尺寸与 ow/oh 略有出入时精确缩放回去
        if (Math.Abs(ow - outBmp.Width) > 1 || Math.Abs(oh - outBmp.Height) > 1)
            SaveScaled(outBmp, output, ow, oh);
        else
            outBmp.Save(output, System.Drawing.Imaging.ImageFormat.Png);
        progress?.Report((100, "完成"));
    }

    /// <summary>smoothstep(0→1):平滑缓入,避免线性交叉的"硬线",让跨块淡入更无痕(与 ncnn 路径一致)。</summary>
    private static double SmoothStep(double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    /// <summary>把【引擎 -g 编号】解析成【DirectML 设备号】,结果进程内缓存(见 _engineToDmlCache 说明)。
    /// 这是整个 ONNX 超分里【唯一】允许调用 EngineService.ToDmlDevice 的地方 —— 连击表(_dmlStrikes)、
    /// 熔断(TripDmlDead)、会话创建(AppendExecutionProvider_DML)三处必须共用同一个号,且只解析一次。
    /// 【为什么必须统一】两套编号在双卡机上并不相同(引擎序 [0 独显][1 核显] vs DirectML/DXGI 序可能相反),
    /// 混用会出现"图片页在核显连吃 3 次失败 → 把 DML 设备 0(=独显)标成不可用 → 补帧直接抛 →
    /// 剩余整段视频静默变成复制原帧",而独显完全健康。单卡机两套编号恰好同值,所以只看单卡机永远发现不了。
    /// 解析失败返回 -1:调用方【必须抛出】(绝不拿非法号去建会话、更不落 CPU)。</summary>
    private static int DmlForEngineDevice(int engineGpu)
    {
        if (engineGpu < 0) return -1;
        // 引擎设备表 ≤1 项(单卡机 / 表尚未就绪)时 ToDmlDevice 是【原样返回】,不是真映射:
        // 这种情况下不缓存 —— 否则会把"设备表还没就绪"时的临时值钉成永久结论。
        if (VulkanCheck.Devices.Count <= 1) return EngineService.ToDmlDevice(engineGpu);
        if (_engineToDmlCache.TryGetValue(engineGpu, out var cached)) return cached;
        int dm = EngineService.ToDmlDevice(engineGpu);
        if (dm < 0) return -1;   // 匹配失败不缓存(见字段说明)
        _engineToDmlCache[engineGpu] = dm;
        AppLogger.Info($"设备映射(缓存):ONNX 超分引擎 -g {engineGpu} → DirectML 设备 {dm}(本进程内不再重复枚举 DXGI)");
        return dm;
    }

    /// <summary>单块推理(返回 4x 结果位图)。会话按 (modelPath,gpuId) 缓存;GPU 失败自动 CPU 重试。
    /// waifu2x 模型(文件名含 waifu2x)输入名为 x(非 input),其余模型为 input。
    /// <paramref name="dmlDeviceHint"/>:调用方【已经解析好】的 DirectML 设备号(null = 未告知,本方法自行解析)。
    /// 若调用方传 sessionOverride(会话池),必须同时传这个号 —— 那批会话就是按它建的,连击表/熔断/清零要复用它。</summary>
    private static System.Drawing.Bitmap RunTile(System.Drawing.Bitmap src, string modelPath, int gpuId, CancellationToken ct,
        InferenceSession? sessionOverride = null, int? dmlDeviceHint = null)
    {
        int inW = src.Width, inH = src.Height;
        // 【C-3】-2 = "自动选设备",那是【调用方的请求】,不等于"要 CPU":UpscaleAsync 的自动探测一旦抛异常被吞,
        // gpuId 就会以 -2 落到这里,而下面所有 `gpuId >= 0` 判定在 -2 上全为假 → 静默建出 CPU 会话,
        // 该 worker 剩下的每一帧都跑 CPU。这里就地解析一次(PickDevice 是纯函数,代价可忽略):
        // 它返回 -1 时是【有明确理由并已记日志】的 CPU 决定(小图 CPU 更快 / DirectML 无可用设备),那是唯一允许 CPU 的场景。
        if (gpuId == -2 && sessionOverride == null)
            gpuId = PickDevice(src.Width, src.Height);
        // 【修复 448x449 BroadcastIterator + 分块网格缝】waifu2x-cunet2x ONNX 模型要求输入宽高必须为偶数且 ≥38,
        // 否则内部 Add 节点广播轴错配直接崩溃(真机 CPU 无 Vulkan/50系走 ONNX 已复现)。
        // 同时该模型输出有固定内裁(实测 out = 2*in - 72,等价于每边裁 18px),导致分块贴回时按 2*in 假设错位,
        // 产生"一块一块"的网格缝(ncnn 引擎内部自会补齐,仅 ONNX 路径踩坑)。
        // 这里给输入【四周各垫 18px(边缘复制)】:模型输出恰好 = 2*内容、无偏移 → 与 RunCoreTiled 的
        // x0*scale 定位吻合,网格缝消除。尺寸仍需偶数 ≥38(垫后若为奇数,右下再补 1px)。
        System.Drawing.Bitmap? padSrc = null;
        if (modelPath.Contains("waifu2x", StringComparison.OrdinalIgnoreCase))
        {
            const int PF = 18;   // waifu2x 模型固定内裁:每边 18px(P 实测吻合,见 _verify_pad.py)
            int pw = inW + PF * 2;
            int ph = inH + PF * 2;
            if ((pw & 1) == 1) pw++;
            if ((ph & 1) == 1) ph++;
            if (pw < 38) pw = 38;
            if (ph < 38) ph = 38;
            if (pw != inW || ph != inH)
            {
                padSrc = PadEdgeReplicateSym(src, pw, ph, PF);
                inW = pw; inH = ph;
            }
        }
        var pixels = new float[1 * 3 * inH * inW];
        FillPixelArray(padSrc ?? src, pixels, inW, inH);
        var inputTensor = new DenseTensor<float>(pixels, new[] { 1, 3, inH, inW });

        // waifu2x 模型输入名是 x(实测 ONNX 元数据);其余(esrgan/cugan/animevideo)是 input
        string inputName = modelPath.Contains("waifu2x", StringComparison.OrdinalIgnoreCase) ? "x" : "input";

        // 【C-2 编号空间统一】dmDevice = 本帧【实际使用】的 DirectML 设备号 = 引擎 -g 编号 gpuId 经 ToDmlDevice 的映射。
        // 下面的建会话 / 连击表 / 熔断 / 清零【全部只认这个号】,绝不再用引擎 -g 编号做键:两套编号在双卡机上不同,
        // 混用会让"核显上连吃 3 次失败"把"DirectML 设备 0(=独显)"标成不可用 → 补帧直接抛 → 剩余整段视频静默复制原帧。
        // 【只解析一次】:结果存成局部变量复用;解析走 DmlForEngineDevice(进程内缓存),不会每块/每帧真枚举 DXGI。
        // 会话池路径已解析过并显式传进来(dmlDeviceHint),这里直接用,不重复解析。
        int dmDevice;
        if (dmlDeviceHint.HasValue)
            dmDevice = dmlDeviceHint.Value;   // 调用方明示:那批会话就是建在这个号上(-1 = 那些会话其实是 CPU 会话)
        else if (gpuId >= 0)
        {
            dmDevice = DmlForEngineDevice(gpuId);
            // 【C-4 ③】解析不出 DirectML 设备号时直接抛:原代码把 -1/非法号交给 AppendExecutionProvider_DML,
            // 必然失败 → 再被 catch 吞掉 → 静默建出【CPU 会话】并缓存到 GPU 键下,之后同键全命中 CPU。
            if (dmDevice < 0)
                throw new InvalidOperationException(
                    $"ONNX 超分:无法把 GPU 编号 {gpuId} 映射到可用的 DirectML 设备(不降级到慢速 CPU)——"
                    + "请在设置里重新选择显卡后重试;若反复出现,建议更新显卡驱动。");
        }
        else dmDevice = -1;

        // 【C-4 ④】会话是否【确实跑在 DirectML 上】—— 唯一权威标志。绝不用 gpuId>=0 推断:
        // gpuId 只表示"调用方想要 GPU",而 DML 建不起来时会话其实是 CPU;拿这种会话在推理成功后
        // ClearDmlStrikes(设备号),等于每次 CPU 成功都把 GPU 的死活洗白 → 设备级熔断永远无法触发
        // (DmlDeviceUnusable/AnyDmlDeviceUnusable 一直报健康,视频/补帧持续重试那块死掉的卡)。
        // 共享缓存路径一旦退到 CPU 会话,下面会把它改回 false。
        bool dmlSession = dmDevice >= 0;

        // 【设备已死 = 快速失败,绝不悄悄转 CPU】887A0005/887A0006 之后本进程的 D3D 设备已被 Windows 摘除,
        // 后续每一次 DirectML 调用都必然失败;这种时候转 CPU,分块路径一张大图十几个块、每块一次 CPU 推理
        // = 几十分钟起步,正是诊断包里"设置显示 GPU、实际跑了几小时"的成因。立刻抛出,由调用方整图/整批降级。
        if (gpuId >= 0 && DmlDeviceDead)
            throw new InvalidOperationException(
                "GPU(DirectML)已被系统摘除/挂死,本进程内无法恢复——已停止超分尝试(不降级到慢速 CPU)。请重启软件后重试。");

        // 连续瞬时失败已达上限的设备:同样快速失败,不再重复"建会话 + 注定失败的推理"。
        // 只对共享会话缓存路径(单图/分块)生效——视频并行路径(sessionOverride 非空)自带独立会话池与逐帧
        // 源帧回退,不该被图片页的失败牵连(它的失败会记在【同一个真实 DML 号】上,由调用方按批次处理)。
        if (sessionOverride == null && DmlDeviceUnusable(dmDevice))
            throw new InvalidOperationException(
                $"GPU(DirectML 设备 {dmDevice})已连续 {DmlTransientStrikes} 次推理失败,本进程内视为不可用——"
                + "已停止超分尝试(不降级到慢速 CPU)。请重启软件后重试;若反复出现,建议关闭其他占用显存的程序并更新显卡驱动。");

        // 【并行优化】sessionOverride 非空:直接用调用方传入的独立会话(绕开共享缓存锁,支持多 session 并行),
        // 供 UpscaleDirAsync 并行超分用;否则按原按 (modelPath,gpuId) 缓存单会话 + 锁串行化(单图/单块路径不变)。
        InferenceSession session;
        var key = (modelPath, gpuId);
        SemaphoreSlim? gate = null;
        if (sessionOverride != null)
        {
            session = sessionOverride;
        }
        else
        {
            var keyGate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            keyGate.Wait();
            try
            {
                gate = keyGate;
                if (_sessions.TryGetValue(key, out var cached) && cached != null)
                {
                    session = cached;
                }
                else
                {
                    var opts = new SessionOptions();
                    bool onDml = false;
                    var dmlKey = (modelPath, dmDevice);
                    // 【C-4 ②】这个组合已经确认建不出 DML 会话 → 不再逐块/逐帧重试 append(白等一次失败 + 刷屏日志)。
                    bool dmlKnownBroken = dmDevice >= 0 && _dmlBrokenSessions.ContainsKey(dmlKey);
                    if (dmDevice >= 0 && !dmlKnownBroken)
                    {
                        try { opts.AppendExecutionProvider_DML(dmDevice); onDml = true; }
                        catch (Exception dmlEx)
                        {
                            // 【C-4 ①】设备被摘除/挂死(887A)是【持续性】错误:重抛(绝不落 CPU),并就地熔断 ——
                            // 否则分块路径一张图十几个块会逐块重演"建会话必然失败",违反"设备摘除→熔断,不逐帧重试"。
                            if (AlhPro.Core.GpuFault.IsPersistentDeviceError(dmlEx))
                            {
                                TripDmlDead(dmDevice, dmlEx);
                                throw;
                            }
                            // 【C-4 ②】非持续性失败(驱动/provider 注册等):明确日志,再由下面落到独立的
                            // (modelPath,-1) 键 —— 绝不把 CPU 会话缓存进 GPU 键。
                            // 【第 1 项】完整诊断(HRESULT 十六进制/异常类型/Message 首行/InnerException 链 + 定性),
                            // 并直接落进 DmlUnavailableReason,让诊断包/界面提示拿得到根因。
                            SetDmlUnavailableByRuntime(dmDevice, dmlEx);
                            LogDmlFailure("RunTile 共享会话.AppendExecutionProvider_DML", dmDevice, dmlEx);
                        }
                    }
                    // 【第 4 项①】没有 DML → 这是 CPU 会话,必须显式限制线程数(见 ApplyConservativeCpuThreads)。
                    if (!onDml) ApplyConservativeCpuThreads(opts);
                    if (dmDevice < 0 || onDml)
                    {
                        // 明确要 CPU(dmDevice<0,键就是 (modelPath,-1))或 DML 建成功(键与"会话真实设备"一致):直接缓存
                        session = new InferenceSession(modelPath, opts);
                        _sessions[key] = session;
                        dmlSession = onDml;
                    }
                    else
                    {
                        // 【C-4 ②】DML 建不起来 → 落到【独立的 (modelPath,-1) CPU 键】,绝不让 GPU 键指向 CPU 会话:
                        // 否则之后同键全部命中它,而且它推理成功还会 ClearDmlStrikes(GPU 设备号) → 设备级熔断永不触发。
                        // 该会话的 Run 必须用它自己那把锁(同一 InferenceSession 不能并发 Run)。
                        var cpuKey = (modelPath, -1);
                        var cpuGate = _locks.GetOrAdd(cpuKey, _ => new SemaphoreSlim(1, 1));
                        cpuGate.Wait();
                        try
                        {
                            if (!_sessions.TryGetValue(cpuKey, out session!) || session == null)
                            {
                                session = new InferenceSession(modelPath, NewCpuSessionOptions());
                                _sessions[cpuKey] = session;
                            }
                        }
                        finally { cpuGate.Release(); }
                        gate = cpuGate;
                        dmlSession = false;
                        // 只报一次(每 模型×设备 一条):不记的话分块路径每块都刷一遍同样的告警
                        if (_dmlBrokenSessions.TryAdd(dmlKey, true))
                            AppLogger.Warn($"⚠ ONNX 超分:DirectML 设备 {dmDevice} 无法创建推理会话,本进程内改用 CPU 会话(缓存键 (模型,-1),不占 GPU 键)——"
                                + "GPU 键不会指向 CPU 会话,设备级熔断不受影响;更新显卡驱动后重启软件即可恢复(本提示只报一次)。"
                                + $"第 4 项:CPU 会话线程数已显式限制为 intra-op={SafeRender.OnnxCpuIntraOpThreads}(默认会按物理核吃满 → CPU 100%/界面卡)");
                    }
                }
            }
            finally { keyGate.Release(); }
        }

        var inputs = new[] { NamedOnnxValue.CreateFromTensor(inputName, inputTensor) };
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue>? results = null;
        try
        {
            try
            {
                // 【第 4 项②】CPU 会话的推理必须在"本进程内 CPU 计算"作用域里跑:它决定 SafeRender 的
                // CPU 硬上限是否压到 CpuComputeCapPct(65%)。GPU 会话(dmlSession=true)不进作用域,
                // GPU 路径的上限仍是原来的 85%(不拖慢正常情况)。
                using var cpuScope = dmlSession ? null : SafeRender.EnterOwnCpuCompute();
                if (sessionOverride != null)
                {
                    // 并行路径:session 来自调用方(独立会话),无需共享锁,直接推理
                    results = session.Run(inputs);
                }
                else
                {
                    gate!.Wait();
                    try { results = session.Run(inputs); }
                    finally { gate!.Release(); }
                }
                // 【C-4 ④】只有会话确实跑在 DirectML 上,才算"GPU 真跑成功":CPU 会话成功不能清零 GPU 设备的连击
                if (dmlSession) ClearDmlStrikes(dmDevice);
            }
            catch (Exception ex) when (dmlSession)
            {
                // 【熔断必须写在早退之前】设备级失效要在任何分支之前记上:视频超分走的正是下面的 sessionOverride
                // 早退分支,熔断写在它后面就一次都记不上 → 每帧都重演"注定失败的 DML 尝试 + 新建 CPU 会话 + CPU 推理"
                // (诊断包里 240 帧跑几小时、4 个线程反复报 887A 的直接原因)。
                bool persistent = AlhPro.Core.GpuFault.IsPersistentDeviceError(ex);
                if (persistent) TripDmlDead(dmDevice, ex);

                // 并行路径(sessionOverride):独立会话,失败直接抛出(上层按帧/批次降级),不进入缓存 key 回退
                if (sessionOverride != null)
                {
                    // 【C-2 附带修复】原先这条分支直接抛,连击表一次都不写 → 一块真坏掉的 DML 设备会被【逐帧】
                    // 白试(每帧一次注定失败的推理 + 每帧一条同样的日志)。现在把失败记在【真实 DML 号】上:
                    // 连吃 3 次后调用方(VideoService 的 AnyDmlDeviceUnusable)会让整批立即停手、按批次回退源帧。
                    if (NoteDmlTransientFailure(dmDevice))
                        AppLogger.Warn($"⚠ ONNX 超分:DirectML 设备 {dmDevice} 连续 {DmlTransientStrikes} 次推理失败,本进程内视为不可用——"
                            + "剩余帧按批次回退源帧(不降级到慢速 CPU);请重启软件后重试");
                    throw new InvalidOperationException($"ONNX 超分失败(并行会话): {ex.Message}", ex);
                }
                // 设备级失效:单图/分块路径也不落 CPU。设备已被摘除,后续每块必然再失败,而"每块一次 CPU 推理"
                // 一张大图就是十几分钟起步——这不是可接受的降级目标(与视频路径口径一致)。
                if (persistent)
                {
                    throw new InvalidOperationException(
                        $"ONNX 超分失败(GPU 设备已失效,不降级到慢速 CPU): {ex.Message}\n请重启软件后重试;若反复出现请更新显卡驱动或关闭其他占用显存的程序。", ex);
                }
                // DirectML 偶发失败(非设备级:瞬时显存不足/单块异常)→ 【本块】换 CPU 会话重试一次,代价有界。
                WarnDmlUnavailable("推理失败: " + ex.Message.Split('\n')[0]);
                // 【修复】Remove+Dispose 持 key 锁(否则另一线程可能 Run 已 Dispose 的会话 → ObjectDisposed)
                gate!.Wait();
                try { if (_sessions.TryRemove(key, out var gone)) gone.Dispose(); }
                finally { gate!.Release(); }
                // 连击达限 = 该设备本进程内不可用 → 抛出,不转 CPU。原先这里写 _dmlBad[gpuId] 永久闩锁,
                // 于是【一次】瞬时失败就让此后整个进程的每张图都跑 CPU(大图分块 × 每块一次 CPU 推理 = 单张十几分钟、
                // 批量几小时,只有重启能解)。保留闩锁里"不重复注定失败调用"的那半(见 RunTile 开头的快速失败),
                // 去掉的是"转 CPU"这个落点。
                if (NoteDmlTransientFailure(dmDevice))
                {
                    throw new InvalidOperationException(
                        $"ONNX 超分失败:GPU(DirectML 设备 {dmDevice})已连续 {DmlTransientStrikes} 次推理失败,"
                        + "已停止尝试(不降级到慢速 CPU)。请重启软件后重试;若反复出现,多为显存不足或驱动问题——"
                        + "建议关闭其他占用显存的程序并更新显卡驱动。\n--\n" + ex.Message, ex);
                }
                var cpuKey = (modelPath, -1);
                var cpuGate = _locks.GetOrAdd(cpuKey, _ => new SemaphoreSlim(1, 1));
                cpuGate.Wait();
                try
                {
                    // 【第 4 项①】CPU 回退会话同样必须显式限制线程数(NewCpuSessionOptions)
                    var cpuSession = _sessions.GetOrAdd(cpuKey, _ => new InferenceSession(modelPath, NewCpuSessionOptions()));
                    // 【第 4 项②】这段是纯 CPU 推理:进"本进程内 CPU 计算"作用域,让 Job 上限压到 CpuComputeCapPct
                    using (SafeRender.EnterOwnCpuCompute())
                        results = cpuSession.Run(inputs);
                }
                catch (Exception cpuEx) { throw new InvalidOperationException($"ONNX 超分失败(GPU+CPU 均失败): {cpuEx.Message}\n--\n{ex.Message}"); }
                finally { cpuGate.Release(); }
            }

            var outTensor = results!.First().AsTensor<float>();
            var dims = outTensor.Dimensions;
            if (dims.Length != 4 || dims[1] != 3)
                throw new InvalidOperationException($"ONNX 输出形状异常: {string.Join("x", dims.ToArray())}");
            int oH = dims[2], oW = dims[3];
            // 【C-1】输出尺寸正负/上界校验(与 RIFE 侧 RifeOnnxService.RunSingle 同款守卫):
            // 非正值会让 new Bitmap 抛出语义不明的异常,超大值直接 OOM —— 这里给一条能定位"模型/输入尺寸"的明确错误。
            if (oW <= 0 || oH <= 0 || oW > 16384 || oH > 16384)
                throw new InvalidOperationException($"ONNX 输出尺寸异常({oW}×{oH},输入 {inW}×{inH}),模型输出异常,无法落图");

            var dst = new System.Drawing.Bitmap(oW, oH, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            try
            {
                var rect = new System.Drawing.Rectangle(0, 0, oW, oH);
                var data = dst.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly,
                    System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                try
                {
                    unsafe
                    {
                        var ptr = (byte*)data.Scan0.ToPointer();
                        int stride = data.Stride;
                        for (int y = 0; y < oH; y++)
                        {
                            for (int x = 0; x < oW; x++)
                            {
                                float r = outTensor[0, 0, y, x];
                                float g = outTensor[0, 1, y, x];
                                float b = outTensor[0, 2, y, x];
                                // 【C-1 数值防线】NaN/±Inf 经 (int)Math.Round(b*255f) 在 .NET 8/x64 得 int.MinValue,
                                // Clamp(...,0,255) 之后 = 0 = 纯黑:而"部分块变黑"既不满足 IsBlackPng 的"≥95% 近黑",
                                // 图片页更是完全没有判黑兜底 → 黑块会静默进成图/成片且日志无痕。
                                // 这里按"该帧作废"抛出,交给既有降级链(整图/该帧回退源帧缩放,尺寸与正常输出一致)。
                                if (float.IsNaN(r) || float.IsInfinity(r) || float.IsNaN(g) || float.IsInfinity(g)
                                    || float.IsNaN(b) || float.IsInfinity(b))
                                    throw new InvalidOperationException(
                                        $"ONNX 超分输出含 NaN/Inf(数值异常,输出 {oW}×{oH}),该帧结果无效");
                                byte* px = ptr + y * stride + x * 3;
                                px[0] = (byte)Math.Clamp((int)Math.Round(b * 255f), 0, 255);
                                px[1] = (byte)Math.Clamp((int)Math.Round(g * 255f), 0, 255);
                                px[2] = (byte)Math.Clamp((int)Math.Round(r * 255f), 0, 255);
                            }
                        }
                    }
                }
                finally { dst.UnlockBits(data); }
            }
            catch
            {
                dst.Dispose();   // 【不泄漏】异常路径的 GDI+ 位图:视频路径一帧一次失败会一路累积到 GC 回收
                throw;
            }
            return dst;
        }
        finally
        {
            padSrc?.Dispose();
            if (results != null)
                foreach (var r in results) r.Dispose();
        }
    }

    /// <summary>把位图垫到指定尺寸并做边缘复制(不是拉伸):多余行/列用最边缘像素补,确保模型输入对齐。</summary>
    private static System.Drawing.Bitmap PadEdgeReplicate(System.Drawing.Bitmap src, int pw, int ph)
    {
        int sw = src.Width, sh = src.Height;
        if (sw == pw && sh == ph) return (System.Drawing.Bitmap)src.Clone();
        var dst = new System.Drawing.Bitmap(pw, ph, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (var g = System.Drawing.Graphics.FromImage(dst))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            // 先画原图(左上角)
            g.DrawImage(src, new System.Drawing.Rectangle(0, 0, sw, sh),
                new System.Drawing.Rectangle(0, 0, sw, sh), System.Drawing.GraphicsUnit.Pixel);
            // 右侧多余列:复制最右列
            if (pw > sw)
                g.DrawImage(src, new System.Drawing.Rectangle(sw, 0, pw - sw, sh),
                    new System.Drawing.Rectangle(sw - 1, 0, 1, sh), System.Drawing.GraphicsUnit.Pixel);
            // 底部多余行:复制最下行
            if (ph > sh)
                g.DrawImage(src, new System.Drawing.Rectangle(0, sh, sw, ph - sh),
                    new System.Drawing.Rectangle(0, sh - 1, sw, 1), System.Drawing.GraphicsUnit.Pixel);
            // 右下角:复制最右下像素
            if (pw > sw && ph > sh)
                g.DrawImage(src, new System.Drawing.Rectangle(sw, sh, pw - sw, ph - sh),
                    new System.Drawing.Rectangle(sw - 1, sh - 1, 1, 1), System.Drawing.GraphicsUnit.Pixel);
        }
        return dst;
    }

    /// <summary>把位图【四周各垫 pad 像素】并做边缘复制(不是拉伸):上下左右多余的用最边缘行列补,
    /// 使模型输出恰好为内容尺寸的整数倍、无偏移(waifu2x cunet 内裁 18px/边,故需对称垫)。
    /// 内容锚定在 (pad,pad);若某边还需补足偶数/最小,调用方已把多出部分计入 pw/ph,由右侧/下侧带吸收。</summary>
    private static System.Drawing.Bitmap PadEdgeReplicateSym(System.Drawing.Bitmap src, int pw, int ph, int pad)
    {
        int sw = src.Width, sh = src.Height;
        if (sw == pw && sh == ph) return (System.Drawing.Bitmap)src.Clone();
        var dst = new System.Drawing.Bitmap(pw, ph, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (var g = System.Drawing.Graphics.FromImage(dst))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            // 内容放 (pad,pad) 处(左上锚定)
            g.DrawImage(src, new System.Drawing.Rectangle(pad, pad, sw, sh),
                new System.Drawing.Rectangle(0, 0, sw, sh), System.Drawing.GraphicsUnit.Pixel);
            // 四周用最边缘行列复制填充
            int leftW = Math.Min(pad, pw - sw), rightW = pw - pad - sw;
            int topH = Math.Min(pad, ph - sh), botH = ph - pad - sh;
            if (leftW > 0)
                g.DrawImage(src, new System.Drawing.Rectangle(0, pad, leftW, sh),
                    new System.Drawing.Rectangle(0, 0, 1, sh), System.Drawing.GraphicsUnit.Pixel);
            if (rightW > 0)
                g.DrawImage(src, new System.Drawing.Rectangle(pad + sw, pad, rightW, sh),
                    new System.Drawing.Rectangle(sw - 1, 0, 1, sh), System.Drawing.GraphicsUnit.Pixel);
            if (topH > 0)
                g.DrawImage(src, new System.Drawing.Rectangle(pad, 0, sw, topH),
                    new System.Drawing.Rectangle(0, 0, sw, 1), System.Drawing.GraphicsUnit.Pixel);
            if (botH > 0)
                g.DrawImage(src, new System.Drawing.Rectangle(pad, pad + sh, sw, botH),
                    new System.Drawing.Rectangle(0, sh - 1, sw, 1), System.Drawing.GraphicsUnit.Pixel);
            if (leftW > 0 && topH > 0)
                g.DrawImage(src, new System.Drawing.Rectangle(0, 0, leftW, topH),
                    new System.Drawing.Rectangle(0, 0, 1, 1), System.Drawing.GraphicsUnit.Pixel);
            if (rightW > 0 && topH > 0)
                g.DrawImage(src, new System.Drawing.Rectangle(pad + sw, 0, rightW, topH),
                    new System.Drawing.Rectangle(sw - 1, 0, 1, 1), System.Drawing.GraphicsUnit.Pixel);
            if (leftW > 0 && botH > 0)
                g.DrawImage(src, new System.Drawing.Rectangle(0, pad + sh, leftW, botH),
                    new System.Drawing.Rectangle(0, sh - 1, 1, 1), System.Drawing.GraphicsUnit.Pixel);
            if (rightW > 0 && botH > 0)
                g.DrawImage(src, new System.Drawing.Rectangle(pad + sw, pad + sh, rightW, botH),
                    new System.Drawing.Rectangle(sw - 1, sh - 1, 1, 1), System.Drawing.GraphicsUnit.Pixel);
        }
        return dst;
    }

    private static void FillPixelArray(System.Drawing.Bitmap src, float[] tensor, int w, int h)
    {
        var rect = new System.Drawing.Rectangle(0, 0, w, h);
        var data = src.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        try
        {
            unsafe
            {
                var ptr = (byte*)data.Scan0.ToPointer();
                int stride = data.Stride;
                int plane = w * h;
                for (int y = 0; y < h; y++)
                {
                    byte* row = ptr + y * stride;
                    for (int x = 0; x < w; x++)
                    {
                        int idx = y * w + x;
                        tensor[idx] = row[x * 3 + 2] / 255f;               // R
                        tensor[plane + idx] = row[x * 3 + 1] / 255f;      // G
                        tensor[plane * 2 + idx] = row[x * 3 + 0] / 255f;  // B
                    }
                }
            }
        }
        finally { src.UnlockBits(data); }
    }

    private static void SaveScaled(System.Drawing.Bitmap src, string output, int ow, int oh)
    {
        using var dst = new System.Drawing.Bitmap(Math.Max(1, ow), Math.Max(1, oh));
        using var g = System.Drawing.Graphics.FromImage(dst);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
        g.DrawImage(src, 0, 0, dst.Width, dst.Height);
        dst.Save(output, System.Drawing.Imaging.ImageFormat.Png);
    }
}
