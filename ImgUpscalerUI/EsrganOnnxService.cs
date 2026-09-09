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

    /// <summary>同一设备【连续】瞬时失败次数(GPU 成功一次即清零)。上限见 DmlTransientStrikes。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> _dmlStrikes = new();

    /// <summary>瞬时失败连击上限。达限即认定该设备在本进程内不可用,按持续性错误同样口径处理(抛可操作错误 /
    /// 回退源帧),而不是转 CPU。为什么是 3:一次重试的代价是"一块/一对帧的 CPU 推理"(秒级),连吃 3 次
    /// 说明不是偶发抖动;再试下去就是"N 帧 × CPU 推理"的几小时形状——那正是要消灭的东西。</summary>
    private const int DmlTransientStrikes = 3;

    /// <summary>记一次瞬时(非设备级)DML 失败。返回 true = 已达连击上限,该设备视为不可用,调用方不得再转 CPU。</summary>
    internal static bool NoteDmlTransientFailure(int device)
    {
        if (device < 0) return false;
        return _dmlStrikes.AddOrUpdate(device, 1, (_, old) => old + 1) >= DmlTransientStrikes;
    }

    /// <summary>GPU 推理成功 → 清零该设备的连击计数(偶发抖动不该累积成"设备不可用")。</summary>
    internal static void ClearDmlStrikes(int device)
    {
        if (device >= 0) _dmlStrikes.TryRemove(device, out _);
    }

    /// <summary>该设备是否已因连续瞬时失败被判定不可用(本进程内)。用于在建会话/推理之前快速失败——
    /// 这是原 _dmlBad 闩锁里唯一有用的那半(不重复注定失败的调用),去掉的是它"转 CPU"的落点。</summary>
    internal static bool DmlDeviceUnusable(int device)
        => device >= 0 && _dmlStrikes.TryGetValue(device, out var n) && n >= DmlTransientStrikes;

    /// <summary>是否【任一】设备已达连击上限。供只持有"自动"(-2)这类未解析设备号的调用方使用:
    /// 逐对/逐帧循环里认出一次就该停止白试,否则几千帧就是几千次注定失败的调用 + 几千条同样的日志。</summary>
    internal static bool AnyDmlDeviceUnusable()
    {
        foreach (var kv in _dmlStrikes)
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

    // ---- DirectML 设备实测(名字匹配失败时的兜底;启动时后台探测一次)----
    private static int _dmlProbeState;   // 0=未做 1=进行中 2=完成
    private static int _dmlFirstOk = -1; // 第一个能创建 DirectML 会话的设备号

    /// <summary>后台探测 DML 设备 0..3 哪些能用(建会话成功即算可用;设备不可用/驱动缺会抛)。
    /// 结果供 EngineService.ToDmlDevice 在"名字匹配失败"时兜底(不越界、不跑错误设备)。
    /// 幂等;未完成/全失败返回 -1(调用方维持原行为)。</summary>
    public static async Task<int> EnsureDmlProbeAsync(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _dmlProbeState, 1, 0) != 0)
            return _dmlFirstOk;   // 已在做或被别人做过
        try
        {
            var model = FindModel();
            if (model == null) return _dmlFirstOk;
            for (int i = 0; i < 4; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var opts = new SessionOptions();
                    opts.AppendExecutionProvider_DML(i);
                    using var s = new InferenceSession(model, opts);   // DML 设备创建失败 → 抛 → 该号不可用
                    if (_dmlFirstOk < 0) _dmlFirstOk = i;
                }
                catch { }
            }
        }
        catch { }
        finally { Volatile.Write(ref _dmlProbeState, 2); }
        await Task.CompletedTask;   // 保持 async 签名(调用方统一 await;探测本身同步,已在后台任务中跑)
        return _dmlFirstOk;
    }

    /// <summary>实测可用的 DirectML 设备兜底号(-1=未探测/全部不可用)。</summary>
    public static int DmlFallbackOk => _dmlFirstOk;

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
    /// gpuId 传入 -2 表示"自动"(按输入大小选设备);其余按传入值。</summary>
    public static async Task UpscaleAsync(string input, string output, double scale,
        int gpuId = -1, IProgress<(int pct, string msg)>? progress = null, CancellationToken ct = default,
        string? modelPath = null, InferenceSession? sessionOverride = null)
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
        await Task.Run(() => RunCore(input, output, scale, modelPath, gpuId, progress, ct, sessionOverride), ct);
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
            : auto ? (AppSettings.GpuIndex >= 0 ? EngineService.ToDmlDevice(AppSettings.GpuIndex) : EsrganOnnxService.DmlFallbackOk)
            : EngineService.ToDmlDevice(gpuId);
        // 预创建独立会话池(每个并行 worker 一个;绕开共享缓存锁,支持并发 Run)
        var sessions = new Microsoft.ML.OnnxRuntime.InferenceSession?[concurrency];
        for (int s = 0; s < concurrency; s++)
        {
            try
            {
                var opts = new SessionOptions();
                if (wantGpu)
                {
                    try { opts.AppendExecutionProvider_DML(dmDevice); }
                    catch (Exception dmlEx)
                    {
                        // 设备被摘除/挂死时建会话本身就会抛 887A:此时绝不能静默建出 CPU 会话把整批帧跑在 CPU 上
                        // (2~3 路 × 240 帧 × 每帧几十秒 = 几小时)。原样重抛,由外层 catch 统一熔断(避免异常套两层)。
                        if (AlhPro.Core.GpuFault.IsPersistentDeviceError(dmlEx)) throw;
                        // 【不要轻易掉 CPU】DirectML 建会话失败:明确记录"卡在 GPU 哪一步",而不是静默落 CPU。
                        // 这样"4060 显示 GPU 却跑几小时"的诊断包能一眼看到是 DirectML 挂在这(驱动过旧 / DML 设备不可用)。
                        // 走到这里说明本机没有可用的 GPU ONNX 运行时,CPU 是唯一计算设备 —— 这是允许用 CPU 的场景。
                        AppLogger.Warn($"⚠ ONNX DirectML 会话创建失败({dmDevice},原因:{dmlEx.Message.Split('\n')[0]})——本机无可用 GPU ONNX,本会话将退回 CPU(速度会变得特别慢,若持续出现请更新显卡驱动后重试)");
                    }
                }
                sessions[s] = new Microsoft.ML.OnnxRuntime.InferenceSession(modelPath, opts);
            }
            // 设备级失效必须往上抛:被这里吞掉就等于"熔断器又失效一次",整批照样在 CPU 上跑到天荒地老。
            // (new InferenceSession 本身也会在设备被摘除时抛 887A,同样走这条重抛。)
            catch (Exception ex) when (AlhPro.Core.GpuFault.IsPersistentDeviceError(ex))
            {
                TripDmlDead(dmDevice, ex);
                throw new InvalidOperationException(
                    $"GPU(DirectML)已被系统摘除/挂死,无法创建推理会话(不降级到慢速 CPU): {ex.Message.Split('\n')[0]}\n请重启软件后重试。", ex);
            }
            catch { sessions[s] = null; }
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
                            UpscaleAsync(files[i], outPath, scale, auto ? -2 : gpuId, null, ct, modelPath, sess)
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
        IProgress<(int pct, string msg)>? progress, CancellationToken ct, InferenceSession? sessionOverride = null)
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
            RunCoreAlphaSafe(src, output, scale, modelPath, gpuId, progress, ct, TileFor(sw, sh), 64);
            return;
        }

        // ===== 分块保护(实测:GPU 整帧喂 1080p → DirectML OOM 崩溃;CPU 慢到 210s/帧)=====
        // 输入超 512 就切成块(带 32px 重叠羽化拼回):GPU 每块 0.3~0.5s,1080p 也稳;速度数倍提升。
        const int Tile = 512;
        const int Overlap = 64;   // 32→64:分块共享上下文更多,接缝过渡带更宽、高纹理更难看出"分块"(代价:边缘计算略增)
        if (sw > Tile || sh > Tile)
        {
            RunCoreTiled(src, output, scale, modelPath, gpuId, progress, ct, Tile, Overlap, sessionOverride);
            return;
        }

        // 单块(整图 ≤ Tile):直接推理
        using var tileBmp = RunTile(src, modelPath, gpuId, ct, sessionOverride);
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
        InferenceSession? sessionOverride = null)
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
                RunCoreTiled(rgbSrc, tmpRgb, scale, modelPath, gpuId, null, ct, tile, overlap, sessionOverride);
            else
            {
                using var tileBmp = RunTile(rgbSrc, modelPath, gpuId, ct, sessionOverride);
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
        InferenceSession? sessionOverride = null)
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
                            using var tileOut = RunTile(cropped, modelPath, gpuId, ct, sessionOverride);
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

    /// <summary>单块推理(返回 4x 结果位图)。会话按 (modelPath,gpuId) 缓存;GPU 失败自动 CPU 重试。
    /// waifu2x 模型(文件名含 waifu2x)输入名为 x(非 input),其余模型为 input。</summary>
    private static System.Drawing.Bitmap RunTile(System.Drawing.Bitmap src, string modelPath, int gpuId, CancellationToken ct,
        InferenceSession? sessionOverride = null)
    {
        int inW = src.Width, inH = src.Height;
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

        // 【设备已死 = 快速失败,绝不悄悄转 CPU】887A0005/887A0006 之后本进程的 D3D 设备已被 Windows 摘除,
        // 后续每一次 DirectML 调用都必然失败;这种时候转 CPU,分块路径一张大图十几个块、每块一次 CPU 推理
        // = 几十分钟起步,正是诊断包里"设置显示 GPU、实际跑了几小时"的成因。立刻抛出,由调用方整图/整批降级。
        if (gpuId >= 0 && DmlDeviceDead)
            throw new InvalidOperationException(
                "GPU(DirectML)已被系统摘除/挂死,本进程内无法恢复——已停止超分尝试(不降级到慢速 CPU)。请重启软件后重试。");

        // 连续瞬时失败已达上限的设备:同样快速失败,不再重复"建会话 + 注定失败的推理"。
        // 只对共享会话缓存路径(单图/分块)生效——连击计数只由这条路径喂;视频并行路径(sessionOverride 非空)
        // 自带独立会话池与逐帧源帧回退,不该被图片页的失败牵连。
        if (sessionOverride == null && DmlDeviceUnusable(gpuId))
            throw new InvalidOperationException(
                $"GPU(DirectML 设备 {gpuId})已连续 {DmlTransientStrikes} 次推理失败,本进程内视为不可用——"
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
            gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            gate.Wait();
            try
            {
                session = _sessions.GetOrAdd(key, _ =>
                {
                    var opts = new SessionOptions();
                    if (gpuId >= 0)
                    {
                        try { opts.AppendExecutionProvider_DML(EngineService.ToDmlDevice(gpuId)); }
                        catch (Exception dmlEx) { WarnDmlUnavailable("创建 GPU 会话失败: " + dmlEx.Message.Split('\n')[0]); }
                    }
                    return new InferenceSession(modelPath, opts);
                });
            }
            finally { gate.Release(); }
        }

        var inputs = new[] { NamedOnnxValue.CreateFromTensor(inputName, inputTensor) };
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue>? results = null;
        try
        {
            try
            {
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
                // GPU 真跑成功 → 清零连击:偶发抖动不该累积成"设备不可用"
                if (gpuId >= 0) ClearDmlStrikes(gpuId);
            }
            catch (Exception ex) when (gpuId >= 0)
            {
                // 【熔断必须写在早退之前】设备级失效要在任何分支之前记上:视频超分走的正是下面的 sessionOverride
                // 早退分支,熔断写在它后面就一次都记不上 → 每帧都重演"注定失败的 DML 尝试 + 新建 CPU 会话 + CPU 推理"
                // (诊断包里 240 帧跑几小时、4 个线程反复报 887A 的直接原因)。
                bool persistent = AlhPro.Core.GpuFault.IsPersistentDeviceError(ex);
                if (persistent) TripDmlDead(gpuId, ex);

                // 并行路径(sessionOverride):独立会话,失败直接抛出(上层按帧/批次降级),不进入缓存 key 回退
                if (sessionOverride != null)
                {
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
                if (NoteDmlTransientFailure(gpuId))
                {
                    throw new InvalidOperationException(
                        $"ONNX 超分失败:GPU(DirectML 设备 {gpuId})已连续 {DmlTransientStrikes} 次推理失败,"
                        + "已停止尝试(不降级到慢速 CPU)。请重启软件后重试;若反复出现,多为显存不足或驱动问题——"
                        + "建议关闭其他占用显存的程序并更新显卡驱动。\n--\n" + ex.Message, ex);
                }
                var cpuKey = (modelPath, -1);
                var cpuGate = _locks.GetOrAdd(cpuKey, _ => new SemaphoreSlim(1, 1));
                cpuGate.Wait();
                try
                {
                    var cpuSession = _sessions.GetOrAdd(cpuKey, _ => new InferenceSession(modelPath, new SessionOptions()));
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

            var dst = new System.Drawing.Bitmap(oW, oH, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
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
                            int idx = (y * oW + x);
                            float r = outTensor[0, 0, y, x];
                            float g = outTensor[0, 1, y, x];
                            float b = outTensor[0, 2, y, x];
                            byte* px = ptr + y * stride + x * 3;
                            px[0] = (byte)Math.Clamp((int)Math.Round(b * 255f), 0, 255);
                            px[1] = (byte)Math.Clamp((int)Math.Round(g * 255f), 0, 255);
                            px[2] = (byte)Math.Clamp((int)Math.Round(r * 255f), 0, 255);
                        }
                    }
                }
            }
            finally { dst.UnlockBits(data); }
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
