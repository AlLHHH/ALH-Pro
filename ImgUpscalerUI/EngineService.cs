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
    /// 50 系上 2022 版 ncnn 引擎(Vulkan)会崩 —— 需换 ONNX 路线(不走 Vulkan)。
    /// 测试钩子:环境变量 ALH_FORCE_BLACKWELL=1 时强制视为 50 系(开发/诊断用,正常用户不生效)。</summary>
    public static bool IsBlackwellGpu()
    {
        try
        {
            if (Environment.GetEnvironmentVariable("ALH_FORCE_BLACKWELL") == "1") return true;
            var names = new System.Collections.Generic.List<string>();
            try { names.AddRange(VulkanCheck.Devices.Select(d => d.Name)); } catch { }
            try { names.AddRange(GpuInfo.GetAdapterNames()); } catch { }
            return names.Any(n => Regex.IsMatch(n, @"RTX 5[0-9]{2}", RegexOptions.IgnoreCase));
        }
        catch { return false; }
    }

    /// <summary>照片超分(Real-ESRGAN)是否应走 ONNX 路线:
    /// ①Blackwell(ncnn-Vulkan 崩)②无独显/Vulkan 不可用(只能 CPU,而 ncnn CPU 也崩)—— 都走 ONNX(DML/CPU 稳)。
    /// 非风险设备(常规 GPU)默认 ncnn(GPU 更快),避免无谓切换。</summary>
    public static bool ShouldUseOnnxEsrgan()
    {
        if (EsrganOnnxService.FindModel() == null) return false;
        return IsBlackwellGpu() || OldNcnnGpuRisky();
    }

    /// <summary>waifu2x 是否应走 ONNX 路线:仅在 无独显/Vulkan 不可用时(此时只能 CPU,而 waifu2x ncnn CPU 模式有 bug 会崩)。
    /// 50 系 waifu2x 20250915 新版引擎兼容 Blackwell,无需 ONNX;普通 GPU 走 ncnn 更快。
    /// (50 系引擎到底行不行,由 IsWaifu2xNcnnUsableAsync 真机探测兜底——见下方。)</summary>
    public static bool ShouldUseOnnxWaifu2x()
    {
        if (EsrganOnnxService.FindWaifu2xModel() == null) return false;
        return !IsBlackwellGpu() && OldNcnnGpuRisky();   // 无独显才需要(50系 waifu2x 引擎本身兼容)
    }

    /// <summary>waifu2x ncnn 引擎在【本机】GPU 上是否可用(风险设备安全网):
    /// 1×1 小图实测一次(5 秒超时,崩/无输出 = 不可用)并缓存(进程内,不重复探测)。
    /// 不可用 → 调用方改走 ONNX(waifu2x ONNX 模型稳定,DirectML/CPU 都行)。
    /// 触发条件:①RTX 50 系(Blackwell)②存在 AMD/Intel 显卡(驱动差异大、含核显共享显存机型,真机兜底)。
    /// 纯 NVIDIA 成熟环境不探测(零开销,行为不变)。</summary>
    private static bool? _waifu2xNcnnUsable;
    private static int _waifu2xNcnnProbeGpu = int.MinValue;
    private static readonly object _waifu2xProbeLock = new();
    private static bool? _nonNvidiaCache;

    public static async Task<bool> IsWaifu2xNcnnUsableAsync(int gpuId, CancellationToken ct)
    {
        if (!IsBlackwellGpu() && !HasNonNvidiaGpu()) return true;
        lock (_waifu2xProbeLock)
        {
            if (_waifu2xNcnnUsable.HasValue && _waifu2xNcnnProbeGpu == gpuId)
                return _waifu2xNcnnUsable.Value;
        }
        bool ok = await IsEngineGpuUsableAsync("waifu2x", gpuId, ct).ConfigureAwait(false);
        lock (_waifu2xProbeLock)
        {
            _waifu2xNcnnUsable = ok;
            _waifu2xNcnnProbeGpu = gpuId;
        }
        return ok;
    }

    /// <summary>当前计算设备是否为 NVIDIA 显卡(ncnn-Vulkan 在这类卡上偶发 vkAllocateMemory/黑帧,
    /// 故 N 卡走更稳定的 ONNX+DirectML;AMD/Intel 不预判,保持原 ncnn 或 ONNX 兜底逻辑)。
    /// 综合引擎枚举(VulkanCheck.Devices)与注册表适配器名判断;取不到时按"非 N 卡"保守处理。</summary>
    public static bool IsNvidiaGpu()
    {
        try
        {
            var names = new System.Collections.Generic.List<string>();
            try { names.AddRange(VulkanCheck.Devices.Select(d => d.Name)); } catch { }
            try { names.AddRange(GpuInfo.GetAdapterNames()); } catch { }
            if (names.Count == 0) return false;
            // 只要任一设备是 NVIDIA/GeForce/RTX/GTX → 认为是 N 卡环境
            return names.Any(n => n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                || n.Contains("GeForce", StringComparison.OrdinalIgnoreCase)
                || n.Contains("RTX", StringComparison.OrdinalIgnoreCase)
                || n.Contains("GTX", StringComparison.OrdinalIgnoreCase));
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
    /// 枚举顺序【可能不同】——直接拿 ncnn 编号喂 DirectML 会跑错卡(甚至编号越界失败降级 CPU)。
    /// 按显卡名字匹配(引擎枚举名 → 注册表序≈DXGI 序),匹配不到用原编号(DML 失败会自动降 CPU,不挂)。</summary>
    public static int ToDmlDevice(int engineGpu)
    {
        try
        {
            if (engineGpu < 0) return engineGpu;
            var devs = VulkanCheck.Devices;
            if (devs.Count <= 1) return engineGpu;   // 单卡:无歧义
            var want = devs.FirstOrDefault(d => d.Id == engineGpu);
            if (string.IsNullOrWhiteSpace(want.Name)) return engineGpu;
            var names = GpuInfo.GetAdapterNames();
            for (int i = 0; i < names.Count; i++)
            {
                if (names[i].Equals(want.Name, StringComparison.OrdinalIgnoreCase)
                    || names[i].Contains(want.Name, StringComparison.OrdinalIgnoreCase)
                    || want.Name.Contains(names[i], StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            // 名字匹配失败(罕见:枚举差异/截断):用启动时实测的第一个可用 DirectML 设备兜底(绝不越界/跑错误设备);
            // 探测未完成时仍按原编号(与旧行为一致,运行期失败还有 _dmlBad 标记二次兜底)。
            var dmlOk = EsrganOnnxService.DmlFallbackOk;
            if (dmlOk >= 0) return dmlOk;
            return engineGpu;
        }
        catch { return engineGpu; }
    }

    /// <summary>旧 ncnn 引擎(2022 版,realesrgan ncnn)在 GPU 上可能不可用的设备:
    /// ①RTX 50 系(Blackwell,ncnn-Vulkan vkQueueSubmit 崩,全局已知)②Vulkan 不可用/无独显(只能 CPU,而 CPU 也崩)。
    /// 用于全设备兼容自检提示(不限 50 系)。</summary>
    public static bool OldNcnnGpuRisky()
    {
        // Blackwell:Vulkan 驱动问题,已知崩
        if (IsBlackwellGpu()) return true;
        // Vulkan 不可用(无独显/驱动缺):引擎 GPU 无法跑,CPU 模式也崩 → 风险
        try { if (!ALHPro.VulkanCheck.GpuAvailable) return true; } catch { }
        // 其余(AMD/Intel 核显/NVIDIA 老卡):Vulkan 正常即可用,不预判(避免误报)
        return false;
    }

    /// <summary>旧 rife 模型(anime/HD/UHD/v2.3,ncnn 2022 权重)在 Blackwell 上不稳(其余卡正常)。</summary>
    public static bool OldRifeModelRisky() => IsBlackwellGpu();

    /// <summary>【实测验证】推荐 GPU:引擎枚举的设备按优先级(NVIDIA&gt;AMD 独显&gt;Arc&gt;其他,核显排除)
    /// 逐个做 1×1 真机探测,返回第一个【实际可用】的引擎编号;-1=全部不可用。
    /// 不只按名字推荐——名字对但驱动/编号/引擎支持有问题时,实测能拦住(真机:RTX5060 三卡机选中 Intel 核显)。</summary>
    public static async Task<int> FindBestWorkingGpuAsync(CancellationToken ct = default)
    {
        try
        {
            var devs = VulkanCheck.Devices;
            if (devs == null || devs.Count == 0) return -1;
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
                    if (Directory.Exists(cfg))
                    {
                        var probe = Path.Combine(cfg, ".alh_pro_w.tmp");
                        File.WriteAllText(probe, "x");
                        File.Delete(probe);
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
            if (best == null || bestFree <= 0) best = Path.GetPathRoot(Path.GetTempPath())!;
            return best;
        }
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

    /// <summary>动漫模式可选模型(waifu2x 系,全部 MIT 许可)。</summary>
    public static readonly (string Label, string Engine, string Model)[] AnimeModels =
    {
        ("waifu2x · 通用 (cunet)", "waifu2x", "models-cunet"),
        ("waifu2x · 动漫插画 (upconv_7_anime)", "waifu2x", "models-upconv_7_anime_style_art_rgb"),
    };

    // 注意:预处理降噪用 cunet(models-cunet 自带 1x 降噪模型 noise_model.bin);
    // upconv_7_photo/upconv_7_anime 只有 2x 降噪模型(noiseN_scale2.0x),-s 1 降噪会失败。

    /// <summary>Real-ESRGAN 可选模型(照片模式专用,只开放通用模型)。</summary>
    public static readonly (string Label, string Name)[] PhotoModels =
    {
        ("通用 (x4plus)", "realesrgan-x4plus"),
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
    public static string? FindRealESRGAN() => FindExe("realesrgan", "realesrgan-ncnn-vulkan.exe");

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

    /// <summary>若图片带 EXIF 旋转(手机照片),返回旋转后的临时 PNG 路径;否则返回原路径。
    /// 用于预处理(降噪/超分)前标准化方向,保证标记坐标与处理结果同坐标系。</summary>
    public static string NormalizeExif(string input)
    {
        try
        {
            using (var probe = new System.Drawing.Bitmap(input))
            {
                foreach (System.Drawing.Imaging.PropertyItem pi in probe.PropertyItems)
                {
                    if (pi.Id == 0x0112 && pi.Value is { Length: > 0 } && pi.Value[0] is 6 or 8 or 3)
                    {
                        var outPath = Path.Combine(EngineService.TempRoot, $"imgup_exif_{Guid.NewGuid():N}.png");
                        RegisterTempFile(outPath);   // 注册待清理
                        using var bmp = new System.Drawing.Bitmap(input);
                        switch (pi.Value[0])
                        {
                            case 6: bmp.RotateFlip(System.Drawing.RotateFlipType.Rotate90FlipNone); break;
                            case 8: bmp.RotateFlip(System.Drawing.RotateFlipType.Rotate270FlipNone); break;
                            case 3: bmp.RotateFlip(System.Drawing.RotateFlipType.Rotate180FlipNone); break;
                        }
                        // 旋转后清除 EXIF 方向标记,否则下游 LoadRotatedBitmap 会再转一次(双重旋转→主体偏位)
                        try { bmp.RemovePropertyItem(0x0112); } catch { }
                        bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
                        return outPath;
                    }
                }
            }
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
    // 分块处理中"单块平均耗时"(秒,EMA 平滑):用于块内心跳估算当前块进度(让进度条平滑前进)
    private static double _tileEstAvg = 0;

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
                    bool cpu = args.Contains("-g -1", StringComparison.Ordinal);
                    long noOutLimitTicks = TimeSpan.FromMinutes(cpu ? 20 : 8).Ticks;
                    long stallLimitTicks = TimeSpan.FromMinutes(10).Ticks;
                    long sinceOut = DateTime.Now.Ticks - lastOutTicks;
                    long sinceFrame = DateTime.Now.Ticks - lastFrameTicks;
                    // ① 启动超时:30 秒零输出 + 进程还在(而非立即失败退出)
                    if (!sawAnyOutput && sinceOut > TimeSpan.FromSeconds(30).Ticks)
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
                    // ③ 有输出但 10 分钟未完成一帧(CPU 爬帧过慢/引擎停滞)
                    else if (sinceFrame > stallLimitTicks)
                    {
                        killRequested = true;
                        AppLogger.Info($"看门狗:引擎 ({stage}) 10 分钟未完成一帧(计算过慢或停滞),强制终止——建议改用 GPU/调低倍率/调小分辨率");
                        try { p.Kill(entireProcessTree: true); } catch { }
                    }
                }
            }
            catch { }
        }, null, 60000, 60000);

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

    /// <summary>探测指定超分引擎能否用 GPU(-g 0)成功跑一张 1×1 图。
    /// 用途:视频处理开始前,若当前引擎在用户显卡上跑不通(不仅 RTX 50 系——
    /// AMD/Intel/老驱动等任何"该引擎不支持"的场景),提前提示换引擎,而不是处理中默默降级。
    /// 返回 false = GPU 不可用(建议换 waifu2x);异常/超时一律按 false 处理(不中断主流程)。
    /// 注意:仅探测(1×1 图,毫秒级),不影响正常处理;结果不缓存(显卡/驱动随时可能变)。</summary>
    public static async Task<bool> IsEngineGpuUsableAsync(string engine, int gpuId, CancellationToken ct)
    {
        try
        {
            string? exe = engine switch
            {
                "waifu2x" => FindWaifu2x(),
                "realesrgan" => FindRealESRGAN(),
                _ => null,
            };
            if (exe == null) return false;
            // 生成 1×1 测试图
            var inPng = Path.Combine(EngineService.TempRoot, $"eng_probe_{Guid.NewGuid():N}.png");
            var outPng = Path.Combine(EngineService.TempRoot, $"eng_probe_out_{Guid.NewGuid():N}.png");
            try
            {
                using (var bmp = new System.Drawing.Bitmap(1, 1))
                {
                    bmp.SetPixel(0, 0, System.Drawing.Color.Red);
                    bmp.Save(inPng, System.Drawing.Imaging.ImageFormat.Png);
                }
                // 引擎参数:统一 -s 2(2x 模型)
                var args = $"-i \"{inPng}\" -o \"{outPng}\" -s 2 -g {gpuId}";
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
                using var p = Process.Start(psi);
                if (p == null) return false;
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                waitCts.CancelAfter(TimeSpan.FromSeconds(5));   // 探测超时 5 秒(用户要求:检测不能阻塞太久)
                try
                {
                    await p.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
                    // 判定:退出码 0 且输出文件存在(引擎正常出图)
                    bool ok = p.ExitCode == 0 && File.Exists(outPng) && new FileInfo(outPng).Length > 0;
                    if (ok)
                        AppLogger.Info($"[探测] 引擎 {engine} GPU(-g {gpuId})可用(1×1 图出图)");
                    else
                        AppLogger.Warn($"[探测] 引擎 {engine} GPU(-g {gpuId})不可用(exit={p.ExitCode}/无输出)——将自动改用 CPU");
                    return ok;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // 5 秒无果:判定不可用,并杀掉探测进程(避免孤儿引擎占 GPU/CPU)
                    AppLogger.Warn($"[探测] 引擎 {engine} GPU(-g {gpuId}) 5 秒无响应(疑似 hang)——按不可用处理,已终止探测");
                    try { p.Kill(entireProcessTree: true); } catch { }
                    return false;
                }
                catch (OperationCanceledException)
                {
                    // 用户取消(主令牌被取消):杀掉探测进程,重新抛出(不能让取消失效)
                    try { p.Kill(entireProcessTree: true); } catch { }
                    throw;
                }
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
            return false;
        }
    }

    /// <summary>探测 RIFE 补帧引擎能否用 GPU(-g)插出一帧(2 帧输入→1 帧中间帧)。
    /// 用途:补帧开始前实测(50 系/AMD/Intel 等:RIFE 可能静默 hang,不出图也不报错——
    /// 不预检用户只能白等 8 分钟看门狗)。失败返回 false,调用方改用 CPU。
    /// 注意:RIFE 单对模式(-0 -1 -o)而非目录模式(目录模式需 ≥2 帧输入,探测用单对最快)。</summary>
    public static async Task<bool> IsRifeGpuUsableAsync(string rifeExe, string model, int gpuId, CancellationToken ct)
    {
        try
        {
            if (gpuId < 0 || rifeExe == null) return false;
            var tmp = Path.Combine(EngineService.TempRoot, $"rife_probe_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tmp);
            var a = Path.Combine(tmp, "a.png");
            var b = Path.Combine(tmp, "b.png");
            var o = Path.Combine(tmp, "out.png");
            try
            {
                // 两帧:黑→白(有显著运动,引擎必然尝试插帧)
                using (var bmp = new System.Drawing.Bitmap(64, 64))
                {
                    using var g = System.Drawing.Graphics.FromImage(bmp);
                    g.Clear(System.Drawing.Color.Black);
                    bmp.Save(a, System.Drawing.Imaging.ImageFormat.Png);
                    g.Clear(System.Drawing.Color.White);
                    bmp.Save(b, System.Drawing.Imaging.ImageFormat.Png);
                }
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
                using var p = Process.Start(psi);
                if (p == null) return false;
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                waitCts.CancelAfter(TimeSpan.FromSeconds(5));   // 单对插帧正常 1~2 秒;5 秒无果=引擎 hang(用户要求≤5秒)
                try
                {
                    await p.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
                    bool ok = p.ExitCode == 0 && File.Exists(o) && new FileInfo(o).Length > 0;
                    if (ok)
                        AppLogger.Info($"[探测] RIFE {model} GPU(-g {gpuId})可用(1~2 秒出帧)");
                    else
                        AppLogger.Warn($"[探测] RIFE {model} GPU(-g {gpuId})不可用(exit={p.ExitCode}/无输出)——将自动改用 CPU 补帧");
                    return ok;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    AppLogger.Warn($"[探测] RIFE {model} GPU(-g {gpuId}) 5 秒无响应(疑似 hang)——按不可用处理,已终止探测");
                    try { p.Kill(entireProcessTree: true); } catch { }
                    return false;
                }
                catch (OperationCanceledException)
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    throw;
                }
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[探测] RIFE GPU 探测异常(按不可用):{ex.Message}");
            return false;
        }
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
        try
        {
            await RunAsync(exe, args, progress, ct, stage, totalFrames, watchDir, watchBase, watchGlobalTotal).ConfigureAwait(false);
            return;
        }
        catch (InvalidOperationException ex) when (!usesGpu)
        {
            // CPU(-g -1)初始模式:这批 ncnn 引擎的 CPU 模式有 bug(实测 waifu2x 20250915
            // -g -1 直接 exit -1073741819 内存访问违规)→ 反向试 GPU 0,再失败抛指引异常
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

            // ② CPU
            AppLogger.Info($"⚠ 降级:GPU 引擎失败({head});CPU 模式在此引擎不稳定,自动改用 CPU 重算");
            progress?.Report((0, $"⚠ GPU 引擎失败({head}),自动改用 CPU 重算..."));
            var cpuArgs = System.Text.RegularExpressions.Regex.Replace(args, @"-g\s+-?\d+", "-g -1");
            try
            {
                await RunAsync(exe, cpuArgs, progress, ct, stage, totalFrames, watchDir, watchBase, watchGlobalTotal).ConfigureAwait(false);
            }
            catch (InvalidOperationException cpuEx)
            {
                // ③ CPU 也崩(老式 ncnn 引擎 CPU 模式 bug):反向再试 GPU 0
                if (curGpu != 0)
                {
                    string g0Name = GpuName(0);
                    AppLogger.Info($"⚠ CPU 模式也失败,回退重试 GPU 0({g0Name})...");
                    progress?.Report((0, $"⚠ CPU 模式也失败,回退重试 GPU 0({g0Name})..."));
                    await RunAsync(exe,
                        System.Text.RegularExpressions.Regex.Replace(args, @"-g\s+-?\d+", "-g 0"),
                        progress, ct, stage, totalFrames, watchDir, watchBase, watchGlobalTotal).ConfigureAwait(false);
                }
                else
                {
                    throw new InvalidOperationException(
                        $"超分引擎在 GPU 和 CPU 模式都不行(exit {ExtractExit(cpuEx.Message)}):\n" +
                        $"这多半是引擎版本与显卡不兼容(如 RTX 50 系 + 旧版 ncnn-vulkan)。\n" +
                        $"建议:①换用 waifu2x 引擎(官方新版支持 50 系/Blackwell);" +
                        "②或到 https://github.com/nihui/waifu2x-ncnn-vulkan/releases 下载最新版替换 engines/waifu2x/ 下的文件。" +
                        $"\n--\n{cpuEx.Message}");
                }
            }
        }
    }

    private static string ExtractExit(string msg)
    {
        var m = System.Text.RegularExpressions.Regex.Match(msg, @"exit (-?\d+)");
        return m.Success ? m.Groups[1].Value : "?";
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

    /// <summary>自研"任意时刻插帧"核心(M1):对单帧对 (A,B) 用 RIFE 二分级联生成 2^depth-1 个等距中间帧
    /// (位置 j/2^depth, j=1..2^depth-1),输出到 outDir/interp_{000}.png 起(顺序=时间位置)。
    /// 原理:一次 RIFE 只能插 0.5;按二叉子树逐层对"需要的子帧对"再做 0.5 插补,即可得到任意 dyadic 时刻。
    /// 不依赖任何第三方任意 t 接口,纯自有引擎实现(任意时刻插帧,独立实现)。
    /// 注意(M1):逐节点调用(每节点一次引擎进程);后续 M2 用"层内目录模式批处理"压开销。</summary>
    public static async Task InterpPairMultiFrameAsync(string rifeExe, string imgA, string imgB,
        int depth, string outDir, int gpuId, CancellationToken ct)
    {
        Directory.CreateDirectory(outDir);
        int counter = 0;
        async Task NodeAsync(string a, string b, int level)
        {
            if (level <= 0 || ct.IsCancellationRequested) return;
            var mid = Path.Combine(outDir, $"interp_{++counter:D3}.png");
            // RIFE 单对模式:-0 前一帧, -1 后一帧, -o 输出中间帧(0.5)
            var args = $"-0 \"{a}\" -1 \"{b}\" -o \"{mid}\" -g {gpuId}{SafeRender.GetEngineThreadArgs()}";
            try { await RunAsync(rifeExe, args, null, ct).ConfigureAwait(false); }
            catch (InvalidOperationException ex) when (gpuId >= 0)
            {
                // GPU 失败(新显卡/驱动兼容)自动改用 CPU(与超分同策略)
                AppLogger.Info($"降级:任意 t 插帧 GPU 失败({ex.Message.Split('\n')[0]}),改用 CPU");
                await RunAsync(rifeExe,
                    System.Text.RegularExpressions.Regex.Replace(args, @"-g\s+-?\d+", "-g -1"),
                    null, ct).ConfigureAwait(false);
            }
            await NodeAsync(a, mid, level - 1).ConfigureAwait(false);
            await NodeAsync(mid, b, level - 1).ConfigureAwait(false);
        }
        await NodeAsync(imgA, imgB, depth).ConfigureAwait(false);
    }

    /// <summary>层批"任意 t 插值"(M2):对一批帧对同时做 0.5 插值——把各对的 (a_i,b_i) 平铺成目录序列,
    /// 一次 RIFE 目录模式跑完,再提取每对的中间帧(丢弃跨界对(B_i,A_{i+1})的产物)。
    /// 返回中间帧路径列表(顺序=pairs)。配合 InterpPairMultiFrameAsync 的二叉树递归 = 每层一次引擎调用。
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
            AppLogger.Info($"降级:任意 t 层批 GPU 失败({ex.Message.Split('\n')[0]}),改用 CPU");
            await RunAsync(rifeExe, System.Text.RegularExpressions.Regex.Replace(args, @"-g\s+-?\d+", "-g -1"),
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
                int count = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.png").Count() : 0;
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

        // 1x 超分(2x 放大后缩回):内部先按"可用上限倍率"超分,再把结果精确缩回 1x,画质比直接 1x 更好。
        // 注意:照片模型 realesrgan-x4plus 只有 4x 权重(-s 2 会拿 4x 模型硬缩=模糊/伪影),
        // 故 realesrgan 的 1x 超分中间倍率用 4x(4x→缩 0.25=原尺寸);waifu2x 用 2x。
        if (upscaleShrink1x && scale <= 1.001)
        {
            int upper = engine == "realesrgan" ? 4 : 2;
            var tmp2x = output + ".tmp2x.png";
            try
            {
                await UpscaleAsync(input, tmp2x, engine, model, upper, noise, gpuId, tta,
                    progress, ct, tileSize, allowTiling).ConfigureAwait(false);
                await Task.Run(() => ResizeImage(tmp2x, output, 1.0 / upper), ct).ConfigureAwait(false);
                return output;
            }
            finally
            {
                try { File.Delete(tmp2x); } catch { /* 清理失败忽略 */ }
            }
        }

        // 大图分块决策:超过引擎安全块尺寸 → 手动带重叠分块,保证每块"单块整处理、无内部子块接缝"。
        // (引擎内部 -t 分块/自动 tile 会在真实照片上切出"方正拼贴"接缝;App 手动分块 + overlap 羽化 + 单块 -t 才无痕)
        if (allowTiling && scale > 1.001)
        {
            int iw = 0, ih = 0;
            try { using (var probe = new System.Drawing.Bitmap(input)) { iw = probe.Width; ih = probe.Height; } }
            catch { /* 探不到尺寸就走单张路径,由引擎报错 */ }
            if (iw > tileSize || ih > tileSize)
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
                throw new FileNotFoundException("未找到 waifu2x 模型目录: " + modelDir);
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
                $"-t {tileSize} -g {gpuId} -m \"{modelArg}\"{SafeRender.GetEngineThreadArgs()}";
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
                $"-t {tileSize} -g {gpuId}{SafeRender.GetEngineThreadArgs()}";
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

    /// <summary>逐块单文件超分:一块(≤tile)用"单文件 + -t tileSize"整块处理(引擎不再内部切块→无接缝)。
    /// 关键:引擎"目录批量 + -t tileSize"会 vkQueueSubmit 失败→全黑;"-t 0"又会在块内自动再切子块→接缝;
    /// 实测只有"单文件 + -t tileSize"能干净整块无接缝,故逐块各自调用。</summary>
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
                throw new FileNotFoundException("未找到 waifu2x 模型目录: " + modelDir);
            int engineScale = CeilPowerOfTwo(scale);
            var modelArg = Path.GetRelativePath(exeDir, modelDir);
            var args = $"-i \"{input}\" -o \"{output}\" -s {engineScale} -n {noise} " +
                $"-t {tileSize} -g {gpuId} -m \"{modelArg}\"{SafeRender.GetEngineThreadArgs()}";
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
                $"-t {tileSize} -g {gpuId}{SafeRender.GetEngineThreadArgs()}";
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

        // 2) 逐块单文件放大(每块都 <= tile,且以 -t tileSize 单块整处理 → 无内部子块接缝)。
        //    关键:引擎"目录批量 + -t tileSize"会 vkQueueSubmit 失败→全黑;"-t 0"又会在块内自动再切子块→接缝。
        //    实测只有"单文件 + -t tileSize"能干净整块无接缝,故逐块调用。
        progress?.Report((5, $"超分 已处理 0/{totalTiles} 块..."));
        var orderedTiles = Directory.EnumerateFiles(inDir, "*.png")
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
        int done = 0;
        foreach (var tf in orderedTiles)
        {
            ct.ThrowIfCancellationRequested();
            // 块开始即报进度(引擎启动 3~6 秒内界面不空转):"超分 第 X/64 块(引擎启动/处理中)..."
            // 块处理期间(10~60 秒)无任何上报 → 进度条/文字静止;这里加【块内心跳】:
            // 每 1 秒上报一次"块内进度%"(按已用时间/该块预计耗时 估算),让进度条平滑前进而不是死等。
            var tileWatch = System.Diagnostics.Stopwatch.StartNew();
            // 该块预计耗时:总耗时按"已处理块平均耗时"估算(前 3 块后);无历史用 30s 兜底
            double estTile = _tileEstAvg > 1 ? _tileEstAvg : 30.0;
            using var tileHearth = new System.Threading.Timer(_ =>
            {
                try
                {
                    double frac = Math.Min(0.97, tileWatch.Elapsed.TotalSeconds / estTile);
                    // 进度 = 已处理块数 + 当前块估算比例(只影响 UI 平滑,不改变真实完成判断)
                    progress?.Report((5 + (int)(85.0 * (done + frac) / totalTiles),
                        $"超分 第 {done + 1}/{totalTiles} 块(引擎处理中 {tileWatch.Elapsed.TotalSeconds:0}s)..."));
                }
                catch { }
            }, null, 1000, 1000);
            progress?.Report((5 + (int)(85.0 * done / totalTiles), $"超分 第 {done + 1}/{totalTiles} 块(引擎启动/处理中)..."));
            var of = Path.Combine(outDir, Path.GetFileName(tf));
            try
            {
                await UpOneTileAsync(tf, of, engine, model, scale, noise, gpuId, tta, progress, ct, tileSize).ConfigureAwait(false);
            }
            finally
            {
                tileHearth.Dispose();
                tileWatch.Stop();
                // 更新块平均耗时(EMA:0.3 当前/0.7 历史)
                double took = Math.Max(1.0, tileWatch.Elapsed.TotalSeconds);
                _tileEstAvg = _tileEstAvg > 1 ? 0.7 * _tileEstAvg + 0.3 * took : took;
            }
            // 黑帧防御:单块偶发 vkQueueSubmit 失败→全黑(退出码仍 0)。检测到黑块即用 CPU 软解重处理该块;
            // CPU 结果仍黑 / CPU 不可用 → 抛"转 ONNX"信号(上层改用 ONNX 稳定引擎,不再反复 GPU 黑块死循环)。
            if (gpuId >= 0 && File.Exists(of) && IsBlackPng(of))
            {
                progress?.Report((89, "⚠ 检测到该块超分变黑(GPU 队列异常),改用 CPU 软解重处理..."));
                AppLogger.Info("⚠ 检测到该块超分变黑(GPU 队列异常),改用 CPU 软解重处理...");
                try
                {
                    await UpOneTileAsync(tf, of, engine, model, scale, noise, -1, tta, progress, ct, tileSize).ConfigureAwait(false);
                }
                catch
                {
                    // CPU 重试抛错(引擎 CPU 路径不稳定/部分驱动 invalid gpu device)→ 转 ONNX,不重试 GPU(还是黑)
                    try { File.Delete(of); } catch { }
                    throw new InvalidOperationException("BLACKOUT_NEED_ONNX:GPU 黑块且 CPU 模式不可用,转用 ONNX 稳定引擎");
                }
                // CPU 重试"成功"但输出仍黑(内部回退 GPU 又算一次,结果还是黑)→ 同样转 ONNX
                if (File.Exists(of) && IsBlackPng(of))
                {
                    try { File.Delete(of); } catch { }
                    throw new InvalidOperationException("BLACKOUT_NEED_ONNX:GPU 持续黑块(CPU 修复无效),转用 ONNX 稳定引擎");
                }
            }
            done++;
            progress?.Report((5 + (int)(85.0 * done / totalTiles), $"超分 已处理 {done}/{totalTiles} 块..."));
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

    /// <summary>检测目录里的 PNG 是否有全黑块(ncnn-vulkan GPU 队列失败时输出全黑,退出码仍 0)。
    /// 采样近似:任一张图 95% 以上像素接近全黑即判黑。</summary>
    private static bool HasBlackPng(string dir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.png"))
            {
                if (IsBlackPng(f)) return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>检测单个 PNG 是否近全黑(95% 以上像素 RGB 和 < 24)。internal:视频补帧/层批复用(黑帧=GPU 队列异常兼容症状)。</summary>
    internal static bool IsBlackPng(string file)
    {
        try
        {
            using var bmp = new System.Drawing.Bitmap(file);
            int step = Math.Max(4, Math.Min(bmp.Width, bmp.Height) / 32);
            int dark = 0, total = 0;
            for (int y = step; y < bmp.Height; y += step)
                for (int x = step; x < bmp.Width; x += step)
                {
                    var p = bmp.GetPixel(x, y);
                    total++;
                    if ((int)p.R + (int)p.G + (int)p.B < 24) dark++;
                }
            return total > 0 && dark >= total * 0.95;
        }
        catch { return false; }
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
        bool preTiled = false)
    {
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

        // 引擎实际执行的倍数:waifu2x 取最大 2 的幂;realesrgan 就近取不小于目标的整数
        int engineScale;
        if (engine == "waifu2x") engineScale = CeilPowerOfTwo(scale);
        else engineScale = Math.Clamp((int)Math.Ceiling(scale), 1, 4);

        if (engine == "waifu2x")
        {
            var exe = FindWaifu2x() ?? throw new FileNotFoundException("未找到 waifu2x 引擎");
            var modelDir = Path.Combine(Path.GetDirectoryName(exe)!, model);
            if (!Directory.Exists(modelDir))
                throw new FileNotFoundException("未找到 waifu2x 模型目录: " + modelDir);
            if (engineScale == 1)
            {
                // 与单图路径完全一致:-s 1 在部分机型段错误崩溃,不再直连;
                // 不降噪时直接复制原图,降噪则用 2x 降噪模型处理后缩回 1x(画质更好)
                if (noise < 0)
                {
                    foreach (var f in Directory.EnumerateFiles(inputDir, "*.png"))
                    {
                        var dest = Path.Combine(outputDir, Path.GetFileName(f));
                        File.Copy(f, dest, overwrite: true);
                    }
                    return;
                }
                engineScale = 2;
            }
            var args = $"-i \"{inputDir}\" -o \"{outputDir}\" -s {engineScale} -n {noise} " +
                $"-t {tileSize} -g {gpuId} -m \"{modelDir}\"{SafeRender.GetEngineThreadArgs()}";
            if (tta) args += " -x";
            await RunEngAsync(exe, t => $"-i \"{inputDir}\" -o \"{outputDir}\" -s {engineScale} -n {noise} " +
                $"-t {t} -g {gpuId} -m \"{modelDir}\"{SafeRender.GetEngineThreadArgs()}" + (tta ? " -x" : "")).ConfigureAwait(false);
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
            await RunEngAsync(exe, t => $"-i \"{inputDir}\" -o \"{outputDir}\" -s {engineScale} -m models -n {model} " +
                $"-t {t} -g {gpuId}{SafeRender.GetEngineThreadArgs()}").ConfigureAwait(false);
        }

        // 非引擎原生倍数:批量缩放到目标倍数
        if (Math.Abs(engineScale - scale) > 0.001)
        {
            var ratio = scale / engineScale;
            progress?.Report((95, $"输出 {scale:0.##}x(引擎 {engineScale}x 放大后精确调整)..."));
            foreach (var f in Directory.EnumerateFiles(outputDir, "*.png"))
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

    /// <summary>把 PNG 转成 JPG(按质量),写入 jpgPath。</summary>
    private static void ConvertPngToJpg(string pngPath, string jpgPath, float quality = 0.92f)
    {
        using var img = new System.Drawing.Bitmap(pngPath);
        SaveJpegViaWinRT(img, jpgPath, quality);
    }

    /// <summary>
    /// 用 WinRT 编码器写 JPG(颜色准确)。System.Drawing 的 JPG 编码会把颜色严重偏掉
    /// (红→黄绿、蓝→黑),故 JPG 输出统一走这里。阻塞 WinRT 异步(MTA 线程池完成,不会死锁)。
    /// </summary>
    private static void SaveJpegViaWinRT(System.Drawing.Bitmap bmp, string jpgPath, float quality = 0.92f)
    {
        int w = bmp.Width, h = bmp.Height;
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
        if (detail > 0)       passes.Add(("保留细节", b => ApplyUnsharpInMemory(b, detail / 100.0 * 1.2, 24, 2)));
        if (detailEnhance > 0) passes.Add(("细节增强", b => ApplyUnsharpInMemory(b, detailEnhance / 100.0 * 1.6, 4, 2)));
        if (clarity > 0)      passes.Add(("清晰", b => ApplyUnsharpInMemory(b, clarity / 100.0 * 0.8, 0, 8)));
        if (usm > 0)          passes.Add(("钝化蒙版", b => ApplyUnsharpInMemory(b, usm / 100.0 * 1.5, 8, 4)));
        if (deblur > 0)       passes.Add(("去模糊", b => ApplyUnsharpInMemory(b, deblur / 100.0 * 1.5, 2, 6)));
        if (edge > 0)         passes.Add(("边缘增强", b => ApplyUnsharpInMemory(b, edge / 100.0 * 1.5, 16, 2)));
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

    /// <summary>去雾:线性拉伸亮度直方图(去灰蒙)+ 提升饱和度,与原图按强度混合。</summary>
    private static void ApplyDehazeInMemory(System.Drawing.Bitmap bmp, int strength)
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
            // 亮度直方图:找 5%~95% 分位,只拉伸有效区间(避免把噪点拉爆)
            var hist = new int[256];
            var luma = new byte[n];
            for (int i = 0; i < n; i++)
            {
                byte yv = (byte)((r[i] * 77 + g[i] * 150 + b[i] * 29) >> 8);
                luma[i] = yv;
                hist[yv]++;
            }
            int lo = 0, hi = 255;
            long acc = 0;
            for (int i = 0; i < 256; i++) { acc += hist[i]; if (acc >= n * 5 / 100) { lo = i; break; } }
            acc = 0;
            for (int i = 255; i >= 0; i--) { acc += hist[i]; if (acc >= n * 5 / 100) { hi = i; break; } }
            if (hi - lo < 24) return;   // 对比度太低(近似纯色),拉伸会把噪点拉爆,跳过
            double scale = 255.0 / (hi - lo);
            double mix = strength / 100.0 * 0.85;
            double satBoost = 1.0 + mix * 0.5;   // 饱和度最多 +42%

            for (int i = 0; i < n; i++)
            {
                int sr = (int)Math.Clamp((r[i] - lo) * scale, 0, 255);
                int sg = (int)Math.Clamp((g[i] - lo) * scale, 0, 255);
                int sb = (int)Math.Clamp((b[i] - lo) * scale, 0, 255);
                int sl = (sr * 77 + sg * 150 + sb * 29) >> 8;
                // 饱和度提升:以拉伸后亮度为基准,颜色偏离基准的部分放大
                int orr = (int)Math.Clamp(sl + (sr - sl) * satBoost, 0, 255);
                int org = (int)Math.Clamp(sl + (sg - sl) * satBoost, 0, 255);
                int orb = (int)Math.Clamp(sl + (sb - sl) * satBoost, 0, 255);
                // 与原图混合
                r[i] = (byte)Math.Clamp(r[i] + (orr - r[i]) * mix, 0, 255);
                g[i] = (byte)Math.Clamp(g[i] + (org - g[i]) * mix, 0, 255);
                b[i] = (byte)Math.Clamp(b[i] + (orb - b[i]) * mix, 0, 255);
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
    private static int CeilPowerOfTwo(double n)
    {
        int p = 1;
        while (p < n) p *= 2;
        return p;
    }

    /// <summary>把图片高保真缩放到精确尺寸后写回 outputPath(保持 PNG 格式)。
    /// 源 Bitmap 持有文件句柄,须先释放再覆盖,故先写临时文件。</summary>
    public static void ResizeImageTo(string path, string outputPath, int width, int height)
    {
        var tmp = Path.Combine(EngineService.TempRoot, $"imgup_resize_{Guid.NewGuid():N}.png");
        try
        {
            using (var src = new System.Drawing.Bitmap(path))
            {
                int w = Math.Max(1, width);
                int h = Math.Max(1, height);
                using var dst = new System.Drawing.Bitmap(w, h);
                using (var g = System.Drawing.Graphics.FromImage(dst))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                    g.DrawImage(src, 0, 0, w, h);
                }
                dst.Save(tmp, System.Drawing.Imaging.ImageFormat.Png);
            }
            File.Copy(tmp, outputPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* 清理失败忽略 */ }
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
