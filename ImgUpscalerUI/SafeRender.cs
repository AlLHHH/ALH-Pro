// SafeRender.cs — 安全渲染:给显存(VRAM)/内存(RAM)加"墙"。
// AI 处理(放大/补帧/超分)按墙自适应缩小 分块大小/批大小/并发数,
// 让单次处理峰值不越过墙,避免显存/内存越界导致程序崩溃或拖垮设备。
// 注意:墙不是"硬性内存锁"(无法阻止进程分配),而是"参数上限"——
// 通过限制引擎单次吃进去的规模,从源头把峰值压在墙内,这才是既有效又不会把程序搞崩的做法。
//
// 模式:0=自动(按本机实测显存/内存),1=自定义(用户设上限)。
// 分块经验值(社区实测,Waifu2x-Extension 等):4GB 显存→128~256,6GB→256~512,8GB+→512~1024。
// 这里取偏保守档,墙内再留 ~25% 余量,防止波动时越界。
using System;
using System.Diagnostics;
using System.IO;

namespace ALHPro;

public static class SafeRender
{
    private const string SettingsFile = "safe-render.json";
    private static string ConfigPath => ParaPaths.SettingsFile(SettingsFile);

    /// <summary>0=自动(推荐) 1=自定义。</summary>
    public static int Mode { get; set; } = 0;

    /// <summary>自定义模式:显存上限(GB),0=未设。</summary>
    public static int VramCapGB { get; set; } = 0;

    /// <summary>自定义模式:内存上限(GB),0=未设。</summary>
    public static int RamCapGB { get; set; } = 0;

    /// <summary>CPU 占用级别:0=自动 1=低 2=中 3=高。</summary>
    public static int CpuLevel { get; set; } = 0;

    /// <summary>处理时把 AI/ffmpeg 子进程设为"低于正常"优先级(默认开):
    /// 即使 CPU/GPU 满载,浏览器/其他软件也不卡;处理速度略降,换整机流畅。</summary>
    public static bool LowPriorityEnabled { get; set; } = true;

    // ===== 资源上限保护(给其他程序留余量;3 个手动开关,默认关) =====
    /// <summary>开关1:用 Windows Job 对象把引擎/ffmpeg 总 CPU 占用强制限制在 CpuCapPct。强制开启(留余量,不能关)。</summary>
    public static bool LimitCpuJob { get; set; } = true;
    /// <summary>开关1 的 CPU 上限百分比(默认 85%)。</summary>
    public static double CpuCapPct { get; set; } = 85.0;
    /// <summary>开关2:引擎/ffmpeg 按可用核分线程,并让非 High 档并发恒 1(避免多路挤同一批核超订)。默认开启。</summary>
    public static bool SplitCores { get; set; } = true;

    /// <summary>长时间处理时:每连续处理 1 小时,休息 15 分钟给设备降温。</summary>
    public static bool RestEnabled { get; set; } = false;

    /// <summary>温度墙(独立开关,默认关):N 卡温度 ≥85°C 时强制暂停 10 分钟降温,降到 70°C 提前恢复。</summary>
    public static bool TempWallEnabled { get; set; } = false;

    /// <summary>休息间隔(分钟,默认 60;支持小数如 0.34≈20 秒,可通过 safe-render.json 调整,便于演示/自定)。</summary>
    public static double RestIntervalMin { get; set; } = 60;

    /// <summary>休息时长(分钟,默认 15;可通过 safe-render.json 调整)。</summary>
    public static int RestDurationMin { get; set; } = 15;

    /// <summary>CPU 核心数。</summary>
    public static int CpuCoreCount => Environment.ProcessorCount;

    /// <summary>CPU 型号名称(注册表;失败给空)。</summary>
    public static string CpuName
    {
        get
        {
            try
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                var n = k?.GetValue("ProcessorNameString") as string;
                if (!string.IsNullOrWhiteSpace(n)) return n.Trim();
            }
            catch { }
            return "未知 CPU";
        }
    }

    /// <summary>GPU 型号名称(取第一块显卡,即主显卡;失败给空)。</summary>
    public static string GpuName
    {
        get
        {
            var names = GpuInfo.GetAdapterNames();
            return names.Count > 0 ? names[0] : "未知 GPU";
        }
    }

    /// <summary>读取当前 GPU 温度(°C,仅 NVIDIA 可靠);失败返回 null(A 卡/Intel 无通用 CLI)。</summary>
    public static double? GetGpuTempC()
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi",
                "--query-gpu=temperature.gpu --format=csv,noheader,nounits")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p != null)
            {
                var line = p.StandardOutput.ReadLine();
                if (double.TryParse(line?.Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var t) && t > 0)
                    return t;
            }
        }
        catch { /* 非 NVIDIA */ }
        return null;
    }

    /// <summary>生效的 CPU 级别:手动=用户值;自动=≤4 核用低,其余用中(不自动拉高,留给系统余量)。
    /// 每次启动按当前电脑重新计算,换电脑后自动适配。</summary>
    public static int EffectiveCpuLevel
    {
        get
        {
            if (CpuLevel >= 1 && CpuLevel <= 3) return CpuLevel;
            return CpuCoreCount <= 4 ? 1 : 2;
        }
    }

    // ---------- 硬件探测(缓存) ----------
    private static double? _vramTotal, _vramFree, _ramTotal;

    /// <summary>本机显存总量(GB);探测失败给保守值 8。</summary>
    public static double TotalVramGB => _vramTotal ??= ProbeVram("memory.total", 8.0);

    /// <summary>当前空闲显存(GB);探测失败按总量的 80% 估。</summary>
    public static double FreeVramGB => _vramFree ??= ProbeVram("memory.free", TotalVramGB * 0.8);

    /// <summary>本机物理内存总量(GB)。</summary>
    public static double TotalRamGB => _ramTotal ??= GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0;

    /// <summary>当前空闲物理内存(GB,系统 API 实测);失败按总量的 40% 保守估。</summary>
    public static double FreeRamGB => _ramFree ??= ProbeFreeRam(TotalRamGB * 0.4);

    private static double? _ramFree;

    /// <summary>每次任务开始前调用:清掉空闲资源缓存,下次访问按当前真实空闲重测。
    /// (总量不变,只刷新空闲值 — 开了浏览器/剪辑器后空闲骤降,批次档位要即时跟上。)</summary>
    public static void RefreshFreeResources()
    {
        _ramFree = null;
        _vramFree = null;
    }

    private static double ProbeFreeRam(double fallback)
    {
        try
        {
            var mi = new MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref mi) && mi.ullAvailPhys > 0)
                return mi.ullAvailPhys / 1073741824.0;
        }
        catch { }
        return fallback;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    private static double ProbeVram(string field, double fallback)
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi",
                $"--query-gpu={field} --format=csv,noheader,nounits")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p != null)
            {
                var line = p.StandardOutput.ReadLine();
                if (double.TryParse(line?.Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var mb) && mb > 0)
                    return mb / 1024.0;
            }
        }
        catch { /* 非 NVIDIA 或未安装驱动 */ }
        return fallback;
    }

    // ---------- 生效中的"墙" ----------
    /// <summary>当前生效的显存墙(GB):自定义=用户值(钳制到本机显存内);
    /// 自动=本机总量的 75%,给系统/其他程序留余量,不会拉满。</summary>
    public static double EffectiveVramGB => Mode == 1 && VramCapGB > 0
        ? Math.Clamp(VramCapGB, 1.0, Math.Max(1.0, TotalVramGB))
        : Math.Max(1.5, TotalVramGB * 0.75);

    /// <summary>当前生效的内存墙(GB):自定义=用户值(钳制);
    /// 自动=本机总量的 75%,留余量。</summary>
    public static double EffectiveRamGB => Mode == 1 && RamCapGB > 0
        ? Math.Clamp(RamCapGB, 2.0, Math.Max(2.0, TotalRamGB))
        : Math.Max(2.0, TotalRamGB * 0.75);

    // ---------- 由墙推出的运行参数 ----------
    /// <summary>分块大小(像素):墙越小分块越小,单次引擎峰值显存越低。
    /// 本机实测(8000×6000 大图):tile 320≈0.8GB / 400≈1.2GB / 512≈1.9GB(waifu2x 4x 与 realcugan 2x 一致),
    /// 按 2 倍余量反推映射(引擎+TTA+系统余量)。大显存放宽到 768/1024:块越大分块越少、接缝越少,
    /// 真实照片/大图质量更好(代价:单块峰值显存高,仍按 512≈1.9GB 线性推算留余量)。</summary>
    public static int GetTileSize()
    {
        double v = EffectiveVramGB;
        if (v <= 2) return 256;
        if (v <= 3) return 320;
        if (v <= 4) return 400;
        if (v <= 6) return 512;
        if (v <= 10) return 640;   // 8GB 级:512~640,单块 2.5~3.5GB,留足系统/显示余量
        return 768;                // 12GB+:768(≈4.3GB/块),16GB 级仍安全;封顶防 TTA×2 爆显存
    }

    /// <summary>视频逐帧超分的批大小(帧):看【空闲】资源——空余内存 >8G 且 空余显存 >4G 开 240(最快);
    /// 空余不足按档回退(小批 = 内存/显存峰值低,稳)。判定用"当前空闲"而非名义值:
    /// 名义 32G 但开着浏览器+剪辑器的机器,空余可能只剩 4G → 该小批。</summary>
    public static int GetVideoBatchSize()
    {
        double fr = FreeRamGB;
        double fv = FreeVramGB > 0.5 ? FreeVramGB : EffectiveVramGB * 0.6;
        if (fr > 8 && fv > 4) return 240;          // 空余内存>8G + 空余显存>4G:240(最快)
        if (fr <= 1.5 || fv <= 0.8) return 25;     // 极端紧张
        if (fr <= 2.5 || fv <= 1.5) return 40;
        if (fr <= 4 || fv <= 2.5) return 60;
        if (fr <= 6 || fv <= 3.5) return 120;
        return 180;                                // 中档:空余内存 6~8G 或显存 3.5~4G
    }

    /// <summary>视频超分的并行批数(同时几个引擎实例):按显存/内存/核数自动定。
    /// 显存充足 + 内存大 + 多核才多路(每路独立引擎实例,GPU 并行算力翻倍);
    /// 条件不够一律单批(多路会让显存/CPU 吃满,后台卡甚至爆)。快速模式强制 1 路。</summary>
    public static int GetVideoConcurrency()
    {
        // 弱机一律单批(GPU 算力节流);Balanced 最多 2 路;只有 High 才允许 3 路。
        if (Profile is DeviceProfile.UltraLow or DeviceProfile.Low) return 1;
        // 开关2(SplitCores):非 High 一律单批(避免多路挤同一批核超订)
        if (SplitCores && Profile != DeviceProfile.High) return 1;
        double r = EffectiveRamGB;
        double v = EffectiveVramGB;
        int cores = CpuCoreCount;
        // 2 路:显存 ≥6G 空闲 ≥3G、内存 ≥16G、核数 ≥8
        bool two = r >= 16 && v >= 6 && FreeVramGB >= 3 && cores >= 8;
        // 3 路:仅 High 且更宽裕才上(空闲显存 ≥8G、内存 ≥24G、核数 ≥16)
        bool three = Profile == DeviceProfile.High && r >= 24 && v >= 10 && FreeVramGB >= 8 && cores >= 16;
        if (three) return 3;
        if (two) return 2;
        return 1;
    }

    /// <summary>设为"低于正常"优先级 + 预留核心(处理时防整机卡):子进程启动后调用;关闭低优先级=正常。
    /// 仅调优先级不够:CPU 100% 时每个线程都分到时间片,系统照样卡。
    /// 所以「系统流畅优先」开启时还给引擎进程做处理器亲和性——不占用最后 1~2 个核心,
    /// 让前台软件/系统始终有富余核可用(这才是"满载也不卡"的关键)。</summary>
    public static void ApplyProcessPriority(Process p)
    {
        // 系统流畅优先(低优先级)开启 → 低优先级 + 预留核心
        bool flow = LowPriorityEnabled;
        try { p.PriorityClass = flow ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Normal; }
        catch { /* 某些进程不允许设置,忽略 */ }
        if (!flow) return;
        try
        {
            // 预留核心:核数 ≤4 预留 1 个;否则预留 2 个(让给前台)。亲和性按"除最后 N 个核"计算
            int cores = CpuCoreCount;
            int reserve = cores <= 4 ? 1 : 2;
            ulong mask = 0;
            for (int i = 0; i < Math.Max(1, cores - reserve); i++)
                mask |= 1UL << i;
            if (mask != 0)
            {
                var h = p.Handle;
                var affinity = (IntPtr)(long)mask;
                SetProcessAffinityMask(h, affinity);   // 只允许引擎用前 N 个核,留 1~2 核给系统
            }
        }
        catch { /* 设置失败不影响(仍降了优先级) */ }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool SetProcessAffinityMask(IntPtr hProcess, IntPtr dwProcessAffinityMask);

    // ---- 开关1:Windows Job 对象强制总 CPU 上限 ----（AssignProcessToJobObject 失败(进程已在别的 Job)时静默忽略）
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr hJob, int JobObjectInfoClass,
        ref JOBOBJECT_CPU_RATE_CONTROL_INFORMATION lpJobObjectInformation, uint cbJobObjectInformationLength);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
    {
        public uint ControlFlags;
        public uint CpuRate;   // 单位为 1/100 百分比(85% = 8500)
    }

    private static IntPtr? _cpuJob;
    private static readonly object _cpuJobLock = new();

    /// <summary>取 Job 句柄(按 LimitCpuJob 创建一次);每次调用前按【当前系统负载】重设 CPU 硬上限,
    /// 保证"其他软件占用高时软件自动让路"。(Job 创建后 CpuRate 可随时覆盖。)</summary>
    internal static IntPtr GetCpuJob()
    {
        if (!LimitCpuJob) return IntPtr.Zero;
        lock (_cpuJobLock)
        {
            if (_cpuJob is null)
            {
                _cpuJob = CreateJobObject(IntPtr.Zero, null);
                if (_cpuJob.Value == IntPtr.Zero) return IntPtr.Zero;
            }
            // 每次分配进程前刷新上限:GetEffectiveCpuCapPct 内部按系统已占用动态降档
            var info = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
            {
                ControlFlags = 0x1 | 0x4,   // JOB_OBJECT_CPU_RATE_CONTROL_ENABLE | HARD_CAP
                CpuRate = (uint)(GetEffectiveCpuCapPct() * 100),
            };
            try { SetInformationJobObject(_cpuJob.Value, 15 /* JobObjectCpuRateControlInformation */, ref info, (uint)System.Runtime.InteropServices.Marshal.SizeOf(info)); } catch { }
            return _cpuJob.Value;
        }
    }

    /// <summary>有效 CPU 上限百分比:手动模式=滑条值(钳 50~95);
    /// 自动模式=85,但**按系统当前已占用动态降档**——其他软件已占了 70%,
    /// 软件再占 85% 会让整机 155% 爆卡。规则:系统空闲越少,软件上限越低,
    /// 保证"软件+其他"总占用 ≤ ~100%(软件永远让位)。
    /// 【关键】必须用"处理开始前"采样的闲置负载(见 _sysLoadIdle):软件自己引擎跑起来后
    /// 系统负载读数会包含软件自身(85%+),按它降档会把软件限死到 8%→更慢→振荡。故每次任务
    /// 开始前刷新一次闲置读数(此时引擎还没跑,读到的即"其他软件"占用)。</summary>
    public static double GetEffectiveCpuCapPct()
    {
        if (Mode == 1) return Math.Clamp(CpuCapPct, 50.0, 95.0);
        double sysUsed = _sysLoadIdle;   // 处理开始前的闲置占用(其他软件的真实占用)
        return GetEffectiveCpuCapPctRaw(sysUsed);
    }

    /// <summary>任务开始前刷新的"系统闲置负载"缓存(引擎未启动时采样,过滤掉软件自身负载)。</summary>
    private static double _sysLoadIdle;

    /// <summary>任务开始前调用:采样当前系统占用(此时引擎还没跑,读数≈其他软件真实占用)。
    /// 之后整个任务期间 GetEffectiveCpuCapPct 用此固定值(不再每次采样,避免引擎自身负载引起的振荡)。</summary>
    public static void RefreshIdleCpu()
    {
        try
        {
            _sysLoadIdle = SampleSystemCpuLoad();
            AppLogger.Info($"[资源] 处理前系统占用 {_sysLoadIdle * 100:0}% → 软件 CPU 上限 {GetEffectiveCpuCapPctRaw(_sysLoadIdle)}%(防整机过载)");
        }
        catch { _sysLoadIdle = 0; }
    }

    /// <summary>采样系统整体 CPU 使用率(0~1,GetSystemTimes 双采样,非阻塞由调用方控制)。
    /// 失败返回 0(视为空闲)。</summary>
    private static double SampleSystemCpuLoad()
    {
        if (!GetSystemTimes(out var idle0, out var ker0, out var user0)) return 0;
        System.Threading.Thread.Sleep(250);
        if (!GetSystemTimes(out var idle1, out var ker1, out var user1)) return 0;
        long idle = idle1.ToMilliseconds() - idle0.ToMilliseconds();
        long total = (ker1.ToMilliseconds() - ker0.ToMilliseconds()) + (user1.ToMilliseconds() - user0.ToMilliseconds());
        if (total <= 0) return 0;
        return Math.Clamp(1.0 - idle / total, 0, 1);
    }

    private static double GetEffectiveCpuCapPctRaw(double sysUsed)
    {
        if (sysUsed > 0.85) return 8;
        if (sysUsed > 0.70) return 15;
        if (sysUsed > 0.50) return 40;
        if (sysUsed > 0.30) return 65;
        return 85;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
        public long ToMilliseconds() => (((long)dwHighDateTime << 32) | dwLowDateTime) / 10000L;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

    /// <summary>把子进程分配进 CPU 限制 Job(开关1);失败(进程已在其它 Job)静默。</summary>
    internal static void AssignToCpuJob(IntPtr processHandle)
    {
        var job = GetCpuJob();
        if (job == IntPtr.Zero || processHandle == IntPtr.Zero) return;
        try { AssignProcessToJobObject(job, processHandle); } catch { }
    }

    /// <summary>CPU 软编(libx264)线程数:低=2,中=4,高=8,不超过本机核心数。
    /// 「系统流畅优先」开启时再收紧(亲和性已预留核心,线程设多了也跑不满,反而无效)。</summary>
    public static int GetLibx264Threads()
    {
        int t = EffectiveCpuLevel switch { 1 => 2, 2 => 4, _ => 8 };
        int max = Math.Max(1, CpuCoreCount - (LowPriorityEnabled ? (CpuCoreCount <= 4 ? 1 : 2) : 0));
        return Math.Clamp(t, 1, max);
    }

    /// <summary>AI 引擎(ncnn)线程参数(-j 加载:计算:保存),按 CPU 核数自动调优:
    /// 低=1:1:1;中/高=加载 1,计算按核数分配(多核吃满但留余量,防引擎抢光 CPU 卡死整机)。
    /// 计算线程 = 核数/2(中档封顶 4、高档封顶 8);核数少时自动收紧;「系统流畅优先」时用剩余核。
    /// 开关2(SplitCores)开启时计算线程再除以并发路数,避免多路引擎挤在同一批核上超订。
    /// 注意:save 线程恒定为 1——实测 ncnn-vulkan 20250915 版引擎(waifu2x/realesrgan)在
    /// save>1 时与 Vulkan 提交队列冲突(vkQueueSubmit failed -4),小 tile 大批次下整批输出黑帧
    /// (表现为"视频导出全黑/开头黑")。save 只写磁盘,单线程不会明显拖慢,稳定优先。</summary>
    public static string GetEngineThreadArgs()
    {
        int usable = CpuCoreCount - (LowPriorityEnabled ? (CpuCoreCount <= 4 ? 1 : 2) : 0);
        usable = Math.Max(1, usable);
        // 开关2:计算线程按并发路数分摊(多路时每实例更少线程,不超订)
        int conc = Math.Max(1, GetVideoConcurrency());
        int compute = EffectiveCpuLevel switch
        {
            1 => 1,
            2 => Math.Clamp(usable / 2 / (SplitCores ? conc : 1), 2, 4),
            _ => Math.Clamp(usable / 2 / (SplitCores ? conc : 1), 4, 8),
        };
        int save = 1;                            // 恒 1:防 ncnn-vulkan save 并发触发 GPU 队列失败(黑帧)
        int load = 1;
        return $" -j {load}:{compute}:{save}";
    }

    /// <summary>休息状态变化(供窗口底部状态栏右侧显示休息提示;true=休息中,false=已结束)。</summary>
    public static event Action<bool>? RestUiChanged;

    /// <summary>休息状态同步全局 UI:触发 RestUiChanged(底部状态栏右侧显示休息提示 + 「跳过休息」按钮)。
    /// 注意:不再把任务面板的「停止」按钮改成「跳过休息」——跳过休息只用底部那个显眼的专用按钮。</summary>
    public static void ApplyRestUi(Microsoft.UI.Xaml.Controls.TextBlock status,
        Microsoft.UI.Xaml.Controls.Button cancelBtn, string msg)
    {
        bool resting = msg.Contains("休息", StringComparison.Ordinal);
        RestUiChanged?.Invoke(resting);
    }

    // ---------- 降温休息(时间制 + 温度墙) ----------
    private static DateTime? _lastRestAt;        // 上次休息完成时间(用于 1 小时时间制)
    private static DateTime? _lastTempCheckAt;   // 上次温度检查时间(每 5 分钟查一次,避免频繁起进程)

    /// <summary>休息进行中时非空:界面「取消」点它=跳过本次休息立即继续(而不是中止整个任务)。</summary>
    public static CancellationTokenSource? CurrentRestCts { get; private set; }

    /// <summary>任务循环每处理一项前调用:按"每小时休息"与"温度墙"决定是否需要暂停降温。
    /// 两个开关独立:温度墙(仅 N 卡能读到温度)≥85°C 强制休息,降到 70°C 提前恢复;
    /// 时间制=连续处理 1 小时休息 15 分钟(A 卡/Intel 读不到温度时的兜底)。
    /// 休息中点「取消」= 跳过休息继续处理(CurrentRestCts.Cancel);任务本身不受影响。</summary>
    public static async Task RestIfDueAsync(int pct, IProgress<(int pct, string msg)>? progress, CancellationToken ct)
    {
        if (!RestEnabled && !TempWallEnabled) return;
        var now = DateTime.Now;

        // 温度墙(独立开关):每 5 分钟查一次 GPU 温度
        if (TempWallEnabled && (_lastTempCheckAt == null || (now - _lastTempCheckAt.Value).TotalMinutes >= 5))
        {
            _lastTempCheckAt = now;
            var temp = GetGpuTempC();
            if (temp is >= 85)
            {
                progress?.Report((pct, $"⚠ 显卡 {temp:0}°C 过热,暂停 10 分钟降温(点底部「跳过休息」可继续)..."));
                AppLogger.Info($"⚠ 显卡 {temp:0}°C 过热,暂停 10 分钟降温(点底部「跳过休息」可继续)...");
                await RestAsync(pct, TimeSpan.FromMinutes(10), 70.0, progress, ct);
                return;
            }
        }

        // 时间制(独立开关):连续处理满 RestIntervalMin 分钟 → 休息 RestDurationMin 分钟
        if (RestEnabled)
        {
            if (_lastRestAt == null) { _lastRestAt = now; return; }
            if ((now - _lastRestAt.Value).TotalMinutes >= RestIntervalMin)
            {
                var intervalTxt = RestIntervalMin < 1
                    ? $"{(int)Math.Round(RestIntervalMin * 60)} 秒"
                    : $"{RestIntervalMin:0.#} 分钟";
                progress?.Report((pct, $"😴 已连续处理 {intervalTxt},休息 {RestDurationMin} 分钟给设备降温(点底部「跳过休息」可继续)..."));
                await RestAsync(pct, TimeSpan.FromMinutes(RestDurationMin), null, progress, ct);
            }
        }
    }

    private static async Task RestAsync(int pct, TimeSpan duration, double? resumeBelow,
        IProgress<(int pct, string msg)>? progress, CancellationToken ct)
    {
        using var restCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        CurrentRestCts = restCts;
        try
        {
            var end = DateTime.Now + duration;
            while (DateTime.Now < end)
            {
                var remain = end - DateTime.Now;
                progress?.Report((pct, $"休息中(降温)剩余 {remain.Minutes:D2}:{remain.Seconds:D2},点「跳过休息」可继续..."));
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), restCts.Token);   // 每 1 秒刷新剩余时间
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    break;   // 用户跳过本次休息,继续处理
                }
                if (resumeBelow is > 0)
                {
                    var t = GetGpuTempC();
                    if (t is not null && t < resumeBelow) break;   // 温度降下来了,提前恢复
                }
            }
        }
        finally
        {
            CurrentRestCts = null;
            _lastRestAt = DateTime.Now;   // 休息(或跳过)后重新计时
        }
    }

    /// <summary>解析显式传入的分块:&lt;=0 时用墙自动算出的安全值。</summary>
    public static int ResolveTile(int requested) => requested > 0 ? requested : GetTileSize();

    // ---------- 硬件画像 + 弱设备判定(Part A/B) ----------
    /// <summary>硬件画像档位:UltraLow(无GPU/极低) → High(强机)。集中推导分档参数。</summary>
    public enum DeviceProfile { UltraLow, Low, Balanced, High }

    private static DeviceProfile? _profile;
    /// <summary>当前硬件画像(按显存/内存/核心数/有无GPU推导,缓存)。</summary>
    public static DeviceProfile Profile => _profile ??= ComputeProfile();

    private static DeviceProfile ComputeProfile()
    {
        double v = TotalVramGB, r = TotalRamGB; int c = CpuCoreCount;
        bool gpu = true; try { gpu = ALHPro.VulkanCheck.GpuAvailable; } catch { }
        if (!gpu) return DeviceProfile.UltraLow;
        if (v >= 12 && r >= 32 && c >= 16) return DeviceProfile.High;
        if (v >= 6 && r >= 16 && c >= 8) return DeviceProfile.Balanced;
        if (v >= 3 && r >= 8) return DeviceProfile.Low;
        return DeviceProfile.UltraLow;
    }

    private static bool? _weak;
    private static string? _weakReason;
    /// <summary>真弱设备(无GPU/显存&lt;6/内存&lt;8/核数≤4) → 显示黄字提示。</summary>
    public static bool IsWeakDevice => _weak ??= ComputeWeakDevice();
    /// <summary>弱设备原因文案(如 "未检测到可用 GPU(Vulkan)、内存 8GB")。</summary>
    public static string WeakDeviceReason => _weakReason ??= ComputeWeakReason();

    private static bool ComputeWeakDevice()
    {
        try
        {
            // 无 GPU:只在 Vulkan 自检【已完成】时才判定(否则首次启动自检未跑完,GpuAvailable 暂为 false,
            // 会误把强机当无 GPU);显存/内存/核数是即时硬件值,不受自检时序影响。
            bool noGpu = ALHPro.VulkanCheck.Done && !ALHPro.VulkanCheck.GpuAvailable;
            bool smallVram = TotalVramGB < 6;
            bool smallRam = TotalRamGB < 8;
            bool fewCores = CpuCoreCount <= 4;
            return noGpu || smallVram || smallRam || fewCores;
        }
        catch { return false; }
    }
    private static string ComputeWeakReason()
    {
        var list = new System.Collections.Generic.List<string>();
        try
        {
            if (!ALHPro.VulkanCheck.GpuAvailable) list.Add("未检测到可用 GPU(Vulkan)");
            if (TotalVramGB < 6) list.Add($"显存仅 {TotalVramGB:0.#}GB");
            if (TotalRamGB < 8) list.Add($"内存 {TotalRamGB:0.#}GB");
            if (CpuCoreCount <= 4) list.Add("核心数较少");
        }
        catch { }
        return string.Join("、", list);
    }

    // ---------- 持久化 ----------
    public static void Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;
            var d = System.Text.Json.JsonSerializer.Deserialize<SafeRenderSettings>(File.ReadAllText(ConfigPath));
            if (d is null) return;
            Mode = d.Mode is 0 or 1 ? d.Mode : 0;
            VramCapGB = d.VramCapGB;
            RamCapGB = d.RamCapGB;
            CpuLevel = d.CpuLevel is >= 0 and <= 3 ? d.CpuLevel : 0;
            LowPriorityEnabled = d.LowPriorityEnabled;
            RestEnabled = d.RestEnabled;
            TempWallEnabled = d.TempWallEnabled;
            LimitCpuJob = true;   // 强制开启(给其他程序留余量,不能关);忽略旧存档里的 false
            if (d.CpuCapPct is >= 1 and <= 100) CpuCapPct = d.CpuCapPct;
            SplitCores = true;    // 默认开启(引擎按可用核分线程,避免多路挤核超订)
            if (d.RestIntervalMin is >= 0.2 and <= 600) RestIntervalMin = d.RestIntervalMin;
            if (d.RestDurationMin is >= 1 and <= 120) RestDurationMin = d.RestDurationMin;
        }
        catch { /* 读取失败用默认 */ }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, System.Text.Json.JsonSerializer.Serialize(new SafeRenderSettings
            {
                Mode = Mode,
                VramCapGB = VramCapGB,
                RamCapGB = RamCapGB,
                CpuLevel = CpuLevel,
                LowPriorityEnabled = LowPriorityEnabled,
                RestEnabled = RestEnabled,
                TempWallEnabled = TempWallEnabled,
                LimitCpuJob = LimitCpuJob,
                CpuCapPct = CpuCapPct,
                SplitCores = SplitCores,
                RestIntervalMin = RestIntervalMin,
                RestDurationMin = RestDurationMin,
            }));
        }
        catch { /* 保存失败忽略 */ }
    }

    private sealed class SafeRenderSettings
    {
        public int Mode { get; set; } = 0;
        public int VramCapGB { get; set; } = 0;
        public int RamCapGB { get; set; } = 0;
        public int CpuLevel { get; set; } = 0;
        public bool LowPriorityEnabled { get; set; } = true;
        public bool RestEnabled { get; set; } = false;
        public bool TempWallEnabled { get; set; } = false;
        public bool LimitCpuJob { get; set; }
        public double CpuCapPct { get; set; } = 85.0;
        public bool SplitCores { get; set; }
        public double RestIntervalMin { get; set; } = 60;
        public int RestDurationMin { get; set; } = 15;
    }
}
