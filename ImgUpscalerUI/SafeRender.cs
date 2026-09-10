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
    /// <summary>【第 4 项②】本进程自己在做 CPU 神经网络推理(ONNX CPU 会话)期间的更保守 CPU 上限(默认 65%)。
    /// 【为什么 85% 不够】85% 是按"CPU 负载都在【子进程】里(ffmpeg/ncnn 引擎)"设计的:那些进程被
    /// AssignToCpuJob 装进了 Job 对象,Job 的硬上限对它们生效。但 ONNX 的 CPU 推理跑在【本进程内】
    /// (EsrganOnnxService/RifeOnnxService/AudioEnhanceService 的 session.Run 都在 UI 进程的线程池上),
    /// Job 对象【根本不覆盖本进程】—— 所以"安全渲染:CPU 硬上限 85%"对 ONNX CPU 推理一行都不生效,
    /// 表现就是"上限写着 85%,任务管理器却是 100%,界面卡"。
    /// 取值 65%:留 1/3 的 CPU 给 UI 线程/系统/其它软件;与 85% 的差值只在【CPU 计算路径】生效,
    /// GPU 路径(不进 OwnCpuCompute 作用域)仍按原值,不拖慢正常情况。</summary>
    public static double CpuComputeCapPct { get; set; } = 65.0;
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

    /// <summary>读取当前 GPU 温度(°C,仅 NVIDIA 可靠);失败返回 null(A 卡/Intel 无通用 CLI)。
    /// 走 RunNvidiaSmi:温度墙会周期性调用,绝不能因为驱动异常把调用线程永久挂住。</summary>
    public static double? GetGpuTempC()
    {
        var line = RunNvidiaSmi("--query-gpu=temperature.gpu --format=csv,noheader,nounits");
        if (line != null && double.TryParse(line, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var t) && t > 0)
            return t;
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
    private static double? _vramTotal, _ramTotal;

    // 空闲显存的三态缓存。用 int 标志 + double 值(而非 double?),是为了能配 Volatile 做无锁读写——
    // 探测要 spawn 子进程,不能把它放进 lock 里(会把资源自检整条路径串行化在子进程上)。
    private static double _vramFreeGb;      // 实测值(GB);未测到时为 0
    private static int _vramFreeMeasured;   // 1 = nvidia-smi 真值;0 = 估算
    private static int _vramFreeProbed;     // 1 = 本轮已探测过(成功或失败都算)

    /// <summary>本机显存总量(GB)。探测顺序见 ProbeTotalVramGb;全失败给保守值 4(宁可低估)。</summary>
    public static double TotalVramGB => _vramTotal ??= ProbeTotalVramGb();

    /// <summary>当前空闲显存(GB)。<b>只有 NVIDIA 能真测</b>(nvidia-smi);其他厂商测不到时这里返回的是
    /// 估算值,调用方【必须】先看 FreeVramMeasured 再决定是否拿它当判据。</summary>
    public static double FreeVramGB { get { EnsureFreeVramProbed(); return _vramFreeGb > 0 ? _vramFreeGb : TotalVramGB * 0.8; } }

    /// <summary>FreeVramGB 是否为真实测值(仅 NVIDIA/nvidia-smi 可用时为 true)。
    /// AMD/Intel 没有跨厂商的空闲显存查询接口,原先代码在这种机器上返回"总量×0.8"当实测值用,
    /// 一个凭空造的数字同时喂给批次档位与并发档位,导致好机器被误降档(实测有机器批次从 180 掉到 120)。</summary>
    public static bool FreeVramMeasured { get { EnsureFreeVramProbed(); return Volatile.Read(ref _vramFreeMeasured) != 0; } }

    /// <summary>空闲显存的显示串:真测到给数值,测不到明确标"未实测"。
    /// UI/诊断包里出现一个凭空造的"空闲显存 6.4 GB"会误导排查——曾据此误判视频批次为何从 180 掉到 120。</summary>
    public static string FreeVramText => FreeVramMeasured
        ? FreeVramGB.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " GB"
        : "未实测(仅 NVIDIA 可测)";

    /// <summary>惰性探测一次空闲显存。<b>FreeVramGB 与 FreeVramMeasured 都必须经过这里</b>——
    /// 此前 FreeVramMeasured 是个不触发探测的自动属性,而 GetVideoConcurrency() 的条件写的是
    /// <c>!FreeVramMeasured || FreeVramGB &gt;= 3</c>:短路之后 FreeVramGB 一次都没被读过 → 探测永远不跑 →
    /// 标志永远 false → ①纯 NVIDIA 机器的自检/诊断包也报"未实测"(v1.3.2 报 7 GB,v1.3.3 报未实测的回归)
    /// ②并发档位的"空闲显存 ≥3G/≥8G"门槛整体失效,显存吃紧时也照样放 2~3 路并行。
    /// 允许极小概率的并发重复探测(结果幂等,代价只是多 spawn 一次 nvidia-smi)。</summary>
    private static void EnsureFreeVramProbed()
    {
        if (Volatile.Read(ref _vramFreeProbed) != 0) return;
        var smi = ProbeNvidiaSmi("memory.free");
        if (smi is > 0) { _vramFreeGb = smi.Value; Volatile.Write(ref _vramFreeMeasured, 1); }
        else { _vramFreeGb = 0; Volatile.Write(ref _vramFreeMeasured, 0); }
        Volatile.Write(ref _vramFreeProbed, 1);   // 最后置位:别人看到"已探测"时,值必定已写好
    }

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
        _vramFreeGb = 0;
        Volatile.Write(ref _vramFreeMeasured, 0);   // 重测前必须先复位,否则两次访问之间会读到上一次的陈旧标志
        Volatile.Write(ref _vramFreeProbed, 0);     // 允许本轮重探:上一次 nvidia-smi 偶发失败不该锁死整场任务
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

    /// <summary>nvidia-smi 候选路径:先试绝对路径,再退回 PATH 查找。
    /// 裸进程名依赖调用方的 PATH 环境变量——它被裁剪/改写时(某些启动方式、某些安全软件)明明装着 NVIDIA 驱动
    /// 也找不到 nvidia-smi,于是空闲显存测不到、显存墙整层失效。</summary>
    private static readonly string[] NvidiaSmiCandidates = new[]
    {
        Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
        @"C:\Windows\System32\nvidia-smi.exe",
        @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe",
        "nvidia-smi",
    };

    /// <summary>跑一次 nvidia-smi 返回首行输出;不存在/超时/非 NVIDIA 一律 null。
    /// 【必须带超时】探测在资源自检里同步调用,驱动异常时挂住就等于 UI 卡死;
    /// 且用异步读——同步 ReadLine 在子进程不输出时会一起挂住,连超时的机会都没有。</summary>
    private static string? RunNvidiaSmi(string arguments)
    {
        foreach (var exe in NvidiaSmiCandidates)
        {
            try
            {
                if (exe != "nvidia-smi" && !File.Exists(exe)) continue;
                var psi = new ProcessStartInfo(exe, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var p = Process.Start(psi);
                if (p == null) continue;
                var readTask = p.StandardOutput.ReadLineAsync();
                if (!readTask.Wait(3000))
                {
                    try { p.Kill(true); } catch { }
                    try { p.WaitForExit(1000); } catch { }
                    return null;   // 挂死:换候选也没意义(同一个驱动),直接放弃本轮探测
                }
                try { p.WaitForExit(1000); } catch { }
                return readTask.Result?.Trim();
            }
            catch { /* 该候选不可用,试下一个 */ }
        }
        return null;
    }

    /// <summary>nvidia-smi 查询显存字段(MB→GB;仅 NVIDIA 可用);任何失败返回 null —— 绝不返回估算值冒充实测值。</summary>
    private static double? ProbeNvidiaSmi(string field)
    {
        var line = RunNvidiaSmi($"--query-gpu={field} --format=csv,noheader,nounits");
        if (line != null && double.TryParse(line, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var mb) && mb > 0)
            return mb / 1024.0;
        return null;
    }

    /// <summary>显存总量(GB):①nvidia-smi(NVIDIA 最准)②DXGI DedicatedVideoMemory(唯一跨厂商真值,
    /// AMD/Intel 独显不再被低估)③注册表 qwMemorySize(核显走这条:报告的是共享内存配额,即核显真实预算)
    /// ④保守 4.0。兜底值从 8.0 降到 4.0 是有意的:全探测失败时低估只让分块变小(慢但安全),
    /// 而高估会让 4GB 卡按 8GB 去开 640 分块 → 爆显存 → 黑帧/崩溃,代价大得多。</summary>
    private static double ProbeTotalVramGb()
    {
        var smi = ProbeNvidiaSmi("memory.total");
        if (smi is > 0) return smi.Value;
        try { var dxgi = ALHPro.EngineService.TryGetDxgiVramGb(); if (dxgi is > 0) return dxgi.Value; } catch { }
        try { var reg = ALHPro.GpuInfo.GetDiscreteVramGb(); if (reg is > 0) return reg.Value; } catch { }
        return 4.0;
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
    /// 本机实测(8000×6000 大图):tile 320≈0.8GB / 400≈1.2GB / 512≈1.9GB(waifu2x 4x 与 realesrgan 2x 一致),
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

    /// <summary>视频逐帧超分专用分块大小【显卡家族感知】:不同显卡家族的 ncnn-Vulkan 稳定性/显存表现不同,
    /// 一刀切 GetTileSize 会让 1660Ti/20系(小显存)和 50系(Blackwell)、A卡(驱动差异)用同一参数,适配差。
    /// 各家族取舍(基于权威 ncnn 引擎参数 + 项目历史黑帧/爆显存实测):
    /// - Blackwell(RTX50):ncnn-Vulkan 易崩,偏保守 tile,且主路径走 ONNX
    /// - NVIDIA Turing(20系/1660Ti/1060,6~8G):中等 tile,快且不炸显存
    /// - AMD 独显(驱动差异大):保守 tile,配合 ONNX 兜底
    /// - 大显存(12G+):放大 tile 提速(块少、接缝少、质量更好)
    /// 引擎侧仍保留 OOM 自动降级分块重试(EngineService.vkAllocateMemory→减半),本值只是起始保守上限。</summary>
    public static int GetVideoTileSize()
    {
        double v = EffectiveVramGB;
        AlhPro.Core.GpuCategory cat = AlhPro.Core.GpuCategory.Other;
        try
        {
            bool blackwell = ALHPro.EngineService.IsBlackwellGpu();
            if (VulkanCheck.Devices.Count > 0)
            {
                var n = VulkanCheck.Devices[0].Name ?? "";
                bool nvidia = n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || n.Contains("GeForce", StringComparison.OrdinalIgnoreCase);
                bool amd = n.Contains("AMD", StringComparison.OrdinalIgnoreCase) || n.Contains("Radeon", StringComparison.OrdinalIgnoreCase);
                cat = blackwell ? AlhPro.Core.GpuCategory.Blackwell
                    : nvidia ? AlhPro.Core.GpuCategory.Nvidia
                    : amd ? AlhPro.Core.GpuCategory.Amd
                    : AlhPro.Core.GpuCategory.Other;
            }
        }
        catch { }
        // 纯计算逻辑抽到 AlhPro.Core.RenderPolicy(可单测):显存+GPU类别 → 分块
        return AlhPro.Core.RenderPolicy.VideoTileSize(v, cat);
    }

    /// <summary>视频逐帧超分的批大小(帧):只看【空闲】内存(实测可靠)。空余不足按档回退
    /// (小批 = 内存峰值低,稳)。判定用"当前空闲"而非名义值:名义 32G 但开着浏览器+剪辑器的机器,
    /// 空余可能只剩 4G → 该小批。显存不参与:显存峰值由分块大小界定,与批大小无关。</summary>
    public static int GetVideoBatchSize()
    {
        return AlhPro.Core.RenderPolicy.VideoBatchSize(FreeRamGB);
    }

    /// <summary>视频超分的并行批数(同时几个引擎实例):按显存/内存/核数自动定。
    /// 显存充足 + 内存大 + 多核才多路(每路独立引擎实例,GPU 并行算力翻倍);
    /// 条件不够一律单批(多路会让显存/CPU 吃满,后台卡甚至爆)。快速模式强制 1 路。</summary>
    public static int GetVideoConcurrency()
    {
        // 弱机一律单批(GPU 算力节流);Balanced 最多 2 路;只有 High 才允许 3 路。
        if (Profile is DeviceProfile.UltraLow or DeviceProfile.Low) return 1;
        double r = EffectiveRamGB;
        double v = EffectiveVramGB;
        int cores = CpuCoreCount;
        // 【分发给所有用户】放宽"SplitCores 非 High 一律单批":之前一刀切把满足 2 路资源条件
        // (16G 内存/6G 显存/≥8核) 的中端机也卡成单路,浪费算力。改为"资源够才多路",条件仍保守:
        // two 要求 内存≥16G、有效显存≥6G、核数≥8,缺一就单路——不会让低端机爆显存/吃满 CPU。
        // SplitCores 现在只影响下游 ffmpeg 软编的线程参数(-j 已恒定 1:1:1,见 GetEngineThreadArgs),
        // 不再一刀切压制路数。
        // 并发是显存的【真约束】(N 路 = N 份分块缓冲同时在显存里),所以这里必须按显存判;
        // 但空闲显存只有 NVIDIA 能真测,AMD/Intel 测不到时不再拿"总量×0.8"这个伪造值当门槛,
        // 退回上面已按 75% 折减的有效显存门槛(v≥6 / v≥10),宁可不加这一层也不要按假数据判。
        bool vramOk2 = !FreeVramMeasured || FreeVramGB >= 3;
        bool vramOk3 = !FreeVramMeasured || FreeVramGB >= 8;
        // 2 路:显存 ≥6G(实测到空闲时再要求空闲 ≥3G)、内存 ≥16G、核数 ≥8
        bool two = r >= 16 && v >= 6 && vramOk2 && cores >= 8;
        // 3 路:仅 High 且更宽裕才上(显存 ≥10G、实测空闲 ≥8G、内存 ≥24G、核数 ≥16)
        bool three = Profile == DeviceProfile.High && r >= 24 && v >= 10 && vramOk3 && cores >= 16;
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
            // 超过 64 逻辑核就别做亲和性了:掩码只有 64 位,而 C# 对 ulong 的移位量按 &0x3F 取模,
            // i≥64 时 1UL<<i 会绕回去把低位重设一遍 → 掩码变成全 1,预留核心【静默失效】
            // (恰恰是核最多的 HEDT/服务器机型上完全没用,且日志里看不出任何异常)。
            // SetProcessAffinityMask 本身也只作用于当前处理器组,跨组预留无从表达。
            // 这类机器富余核本来就多,保留上面已设的"低于正常"优先级即可。
            if (cores > 64) return;
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
    /// <summary>上一次真正写进 Job 的 CpuRate(1/100 %)。用来【去重】:值没变就不重复调 SetInformationJobObject ——
    /// 本进程内 CPU 计算的作用域会频繁进出(每帧一次),每次都做系统调用是白费。</summary>
    private static uint _appliedCapX100;

    /// <summary>按【当前】上限值写一次 Job 的 CPU 硬上限(调用方必须已持有 _cpuJobLock 且确认 Job 已存在)。
    /// 值没变则跳过(见 _appliedCapX100)。</summary>
    private static void ApplyCpuCapLocked()
    {
        if (_cpuJob is null || _cpuJob.Value == IntPtr.Zero) return;
        uint rate = (uint)Math.Round(GetEffectiveCpuCapPct() * 100);
        if (rate == _appliedCapX100) return;
        var info = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
        {
            ControlFlags = 0x1 | 0x4,   // JOB_OBJECT_CPU_RATE_CONTROL_ENABLE | HARD_CAP
            CpuRate = rate,
        };
        try
        {
            if (SetInformationJobObject(_cpuJob.Value, 15 /* JobObjectCpuRateControlInformation */, ref info, (uint)System.Runtime.InteropServices.Marshal.SizeOf(info)))
                _appliedCapX100 = rate;
        }
        catch { }
    }

    /// <summary>让 Job 的 CPU 硬上限【立刻】按当前状态重算(进入/退出"本进程内 CPU 计算"时调用)。
    /// 【为什么需要】原实现只在"有新子进程注册"时才重设(见 GetCpuJob 说明),而 ONNX CPU 推理
    /// 不产生任何子进程 —— 于是"CPU 计算期间把上限压到 65%"这件事若不主动重设就永远不会生效。
    /// 【不在此路径创建 Job】GetCpuJob() 会顺带做一次 ~400ms 的系统负载采样,而本方法由
    /// 会话创建/推理进出触发,不该引入这种开销;Job 已创建时 SetInformationJobObject 对 Job 内
    /// 全部进程立即生效,已创建则这里就是即时的。</summary>
    internal static void RefreshCpuCapNow()
    {
        if (!LimitCpuJob) return;
        lock (_cpuJobLock) { ApplyCpuCapLocked(); }
    }

    /// <summary>取 Job 句柄(按 LimitCpuJob 创建一次);每次调用前按【当前系统负载】重设 CPU 硬上限,
    /// 保证"其他软件占用高时软件自动让路"。(Job 创建后 CpuRate 可随时覆盖。)</summary>
    internal static IntPtr GetCpuJob()
    {
        if (!LimitCpuJob) return IntPtr.Zero;
        // 重采含 ~400ms 睡眠,必须放在锁外:否则会长时间持有 _cpuJobLock,阻塞其他并发任务的进程注册。
        MaybeResampleOtherCpu();
        lock (_cpuJobLock)
        {
            if (_cpuJob is null)
            {
                _cpuJob = CreateJobObject(IntPtr.Zero, null);
                if (_cpuJob.Value == IntPtr.Zero) return IntPtr.Zero;
            }
            // 每次分配进程前刷新上限:GetEffectiveCpuCapPct 内部按"其他软件"占用动态降档
            ApplyCpuCapLocked();
            return _cpuJob.Value;
        }
    }

    /// <summary>有效 CPU 上限百分比:手动模式=滑条值(钳 50~95);
    /// 自动模式=85,但**按"其他软件"当前占用动态降档**——别人已占了 70%,
    /// 软件再占 85% 会让整机 155% 爆卡。规则:别人占得越多,软件上限越低(软件永远让位)。
    /// 【关键】读数必须扣除本软件自身负载(见 ResampleOtherCpuLoad),否则引擎跑起来后系统占用读数
    /// 会包含软件自己(85%+),按它降档会把软件限死→更慢→振荡。扣除后即可全程周期重采(60 秒节流),
    /// 不再被任务开始前那一瞬间的读数锁死整场作业。
    /// 上限的生效时机=有新子进程注册时(SetInformationJobObject 一改即对 Job 内全部进程生效),
    /// 视频流水线每个阶段/每批都会启动进程,故实际刷新足够频繁。</summary>
    public static double GetEffectiveCpuCapPct()
    {
        double pct = Mode == 1 ? Math.Clamp(CpuCapPct, 50.0, 95.0) : GetEffectiveCpuCapPctRaw(_sysLoadIdle);
        // 【第 4 项②】本进程内正在跑 CPU 神经网络推理(ONNX CPU 会话)时再压一档:
        // 那种负载不受 Job 约束(见 CpuComputeCapPct 说明),只能靠"少派活"来保界面流畅。
        if (OwnCpuComputeActive) pct = Math.Min(pct, Math.Clamp(CpuComputeCapPct, 50.0, 95.0));
        return pct;
    }

    // ---------- 第 4 项:本进程内 CPU 计算(ONNX CPU 会话)----------

    /// <summary>本进程内【正在】跑 CPU 神经网络推理的并发计数(0 = 没有)。</summary>
    private static int _ownCpuCompute;

    /// <summary>本进程内是否有正在运行的 CPU 神经网络推理。供日志/诊断显示"当前上限为什么是 65% 而不是 85%"。</summary>
    public static bool OwnCpuComputeActive => Volatile.Read(ref _ownCpuCompute) > 0;

    /// <summary>进入"本进程内 CPU 计算"作用域:必须 using(退出时自动恢复上限)。
    /// 只在【计数 0↔1 的切换瞬间】重设 Job 上限,不在每条推理上做系统调用。</summary>
    internal static IDisposable EnterOwnCpuCompute() => new OwnCpuComputeScope();

    private sealed class OwnCpuComputeScope : IDisposable
    {
        private bool _disposed;
        internal OwnCpuComputeScope()
        {
            if (Interlocked.Increment(ref _ownCpuCompute) == 1) RefreshCpuCapNow();
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (Interlocked.Decrement(ref _ownCpuCompute) == 0) RefreshCpuCapNow();
        }
    }

    /// <summary>ONNX CPU 会话的 intra-op(算子内并行)线程数。【第 4 项①:这是真正能压住 CPU 的旋钮】
    /// 【为什么必须显式设】ONNX Runtime 在 intra_op_num_threads=0(默认)时【自己按物理核开一个线程池】,
    /// 那个线程池在【本进程内】,既不受 Job 对象约束(见 CpuComputeCapPct 说明),也没有"低于正常"优先级
    /// (ApplyProcessPriority 只作用于子进程)—— 于是它和 UI 线程同优先级抢满 16 物理核,
    /// 这就是"CPU 100% + 界面卡"的直接成因。
    /// 【取值依据(不编)】物理核 ≈ 逻辑核/2(超线程按 2 计;Environment.ProcessorCount 是逻辑核);
    /// 再按本文件 ApplyProcessPriority 的同一口径"给前台预留 1~2 核"(那里是进程亲和性预留 1~2 个核
    /// —— 这里把同一个原则用在 ONNX 线程池上,避免两处口径打架);低档再减半,高档允许放宽到只留 1 核。
    /// CPU 档位 = EffectiveCpuLevel(已有的 0=自动/1=低/2=中/3=高,自动档在 >4 核机器上就是"中")。
    /// 下限 2(与 GetLibx264Threads 的低档值一致:单/双核机若只给 1 线程,CPU 路径会慢到不可用)。
    /// 例:32 逻辑核(16 物理核)的 Ryzen 9 8940HX → 中档 = 16−2 = 14 线程,始终留 2 个物理核给界面。</summary>
    public static int OnnxCpuIntraOpThreads
    {
        get
        {
            int logical = Math.Max(1, Environment.ProcessorCount);
            int phys = Math.Max(1, logical / 2);
            int reserve = phys <= 4 ? 1 : 2;
            int t = EffectiveCpuLevel switch
            {
                1 => phys / 2,          // 低档:物理核一半
                3 => phys - 1,          // 高档:只留 1 核
                _ => phys - reserve,    // 自动/中档:留 1~2 核给前台(与 ApplyProcessPriority 同口径)
            };
            return Math.Clamp(t, 2, Math.Max(2, logical - 1));
        }
    }

    /// <summary>ONNX CPU 会话的 inter-op(可并行子图/算子)线程数。【恒 1】
    /// inter-op &gt;1 是"多个算子节点各自开线程"——它会与 intra-op 线程池叠加相乘,
    /// 实际线程数变成 intra×inter(旧行为:默认值让总线程数远超核数,互相抢占,吞吐不升反降,
    /// 且 CPU 占用尖峰更狠)。CPU 路径的瓶颈是内存带宽/单算子并行度,不需要算子间再并行。</summary>
    public static int OnnxCpuInterOpThreads => 1;

    /// <summary>任务开始前刷新的"其他软件 CPU 占用"缓存(已扣除本软件自身负载)。</summary>
    private static double _sysLoadIdle;

    /// <summary>当前采用的"其他软件 CPU 占用"(0~1),供日志/诊断显示。CPU 硬上限即由此推导。</summary>
    public static double IdleCpuLoad => _sysLoadIdle;

    /// <summary>任务开始前调用:采样"其他软件"的真实 CPU 占用(见 ResampleOtherCpuLoad)。</summary>
    public static void RefreshIdleCpu()
    {
        ResampleOtherCpuLoad();
        _lastOtherCpuSample = DateTime.UtcNow;   // 刚采过,别让紧随其后的进程注册再白采一次
        try { AppLogger.Info($"[资源] 处理前其他软件占用 {_sysLoadIdle * 100:0}% → 软件 CPU 上限 {GetEffectiveCpuCapPctRaw(_sysLoadIdle)}%(防整机过载)"); }
        catch { }
    }

    private static DateTime _lastOtherCpuSample = DateTime.MinValue;
    private static readonly object _sampleLock = new();

    /// <summary>距上次重采超过 60 秒才重采(手动模式下上限由滑条决定,与采样无关,直接跳过)。</summary>
    private static void MaybeResampleOtherCpu()
    {
        if (Mode == 1) return;
        lock (_sampleLock)
        {
            if ((DateTime.UtcNow - _lastOtherCpuSample).TotalSeconds < 60) return;
            _lastOtherCpuSample = DateTime.UtcNow;
        }
        ResampleOtherCpuLoad();
    }

    /// <summary>本软件自身(所有已注册子进程 + 本进程)累计占用的 CPU 毫秒数。
    /// 与 GetSystemTimes 的 kernel+user 同为"跨所有核心的 CPU 时间"口径,可直接相减。
    /// 单个进程可能已退出/无权限 → 逐个 try,漏掉一个不影响量级判断。</summary>
    private static long SumOwnCpuMs()
    {
        long ms = 0;
        // 本进程(UI 线程做 PNG↔JPG 转码/去重 SSIM/黑帧检查,CPU 占用不小);它不在 ActiveProcesses 里
        // (那只是子进程表),故与下面的循环不会重复计数。
        try { using var self = Process.GetCurrentProcess(); ms += (long)self.TotalProcessorTime.TotalMilliseconds; } catch { }
        try
        {
            foreach (var p in App.ActiveProcesses.Snapshot())
            {
                try { ms += (long)p.TotalProcessorTime.TotalMilliseconds; } catch { }
            }
        }
        catch { }
        return ms;
    }

    /// <summary>重估"其他软件"的 CPU 占用 = 系统总占用 − 本软件自身占用。
    /// 【为什么必须减自己】处理期间引擎把 CPU 打满,直接读系统占用会读到 85%+,据此降档会把软件限死
    /// → 更慢 → 振荡;这正是原设计只敢在任务开始前采样一次的原因。但一次性采样的代价极大:
    /// 任务开始那一瞬间的任何后台负载(杀软扫描/系统更新/浏览器)都会把可能跑数小时的作业锁死在 35% CPU,
    /// 而用户中途关掉其他软件也升不回来。减掉自身后读数只反映"别人",稳定不振荡,于是可以全程周期重采。</summary>
    public static void ResampleOtherCpuLoad()
    {
        try
        {
            if (!GetSystemTimes(out var i0, out var k0, out var u0)) return;
            long own0 = SumOwnCpuMs();
            System.Threading.Thread.Sleep(400);
            if (!GetSystemTimes(out var i1, out var k1, out var u1)) return;
            long idle = i1.ToMilliseconds() - i0.ToMilliseconds();
            // kernel 时间在 Windows 上【已包含】idle,故 total=kernel+user、busy=total-idle 是标准算法
            long total = (k1.ToMilliseconds() - k0.ToMilliseconds()) + (u1.ToMilliseconds() - u0.ToMilliseconds());
            if (total <= 0) return;
            double sys = Math.Clamp(1.0 - (double)idle / total, 0, 1);
            double own = Math.Clamp((double)(SumOwnCpuMs() - own0) / total, 0, 1);
            _sysLoadIdle = Math.Clamp(sys - own, 0, 1);
        }
        catch { /* 采样失败保留上一次的值,绝不清零(清零=当成全空闲=不再让位) */ }
    }

    private static double GetEffectiveCpuCapPctRaw(double sysUsed)
    {
        // 【分发给所有用户】放宽"防整机过载"下限,但保留让位机制:
        // 之前 >85%→8% 会把软件压到只让 1~2 核,在"DirectML 可用但 CPU 上限被误判"的机器上
        // 让 ffmpeg 读 PNG/编码、AI 超分的 CPU 部分都骤降,表现为"明显比同类慢"。
        // 改为更宽松的分档:即使系统被其他软件占满,也保证软件至少有几个核能干活。
        // 兼顾:高压档仍明显让位(35%),正常空闲档给满(85%)。
        if (sysUsed > 0.85) return 35;
        if (sysUsed > 0.70) return 50;
        if (sysUsed > 0.50) return 65;
        if (sysUsed > 0.30) return 75;
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

    /// <summary>CPU 软编(libx264)线程数:仅低档(≤4 核)用 2;自动/中/高档直接用满可用核。
    /// 「系统流畅优先」开启时进程级已通过亲和性预留 1~2 核(低优先级+亲和性),线程数无需再双重收紧。
    /// 实测(2026-09-08,RTX 4060 Laptop/16 核):4→16 线程编码段 1.95×、整段 1.42×;原自动档(>4核机器)只给 4
    /// 线程,是 CPU 软编慢的主因——进程级保护已存在,再限线程属于重复防御。</summary>
    public static int GetLibx264Threads()
    {
        int max = Math.Max(1, CpuCoreCount - (LowPriorityEnabled ? (CpuCoreCount <= 4 ? 1 : 2) : 0));
        if (EffectiveCpuLevel == 1) return Math.Clamp(2, 1, max);   // 低档:保守 2 线程
        return max;                                                 // 自动/中/高档:用满可用核
    }

    /// <summary>AI 引擎(ncnn)线程参数(-j 加载:计算:保存)。三个线程数一律取 1 —— 依据是实测,不再是"按核数调优"。
    /// 【compute 为什么恒 1 —— 2026 实测:本机 RTX 4060 Laptop 8GB / 驱动 572.83 / 16 核,
    ///  waifu2x-ncnn-vulkan 20250915 + models-cunet,2x,1080×1920 四帧目录批,-t 0,同素材同参数逐档对比】
    /// · compute=4(旧中档在本机解析出的 -j 1:4:1)是【坏帧制造机】:6 次运行里 5 次(83%)打印 200+ 条
    ///   "vkQueueSubmit failed",输出【每帧下 2/3 全黑、上 1/3 正常】或整帧全黑;
    ///   而【退出码全是 0、引擎不报任何错】—— 用户侧只看到"超分成片里有黑帧"。
    ///   同机 compute=1 → 0 失败、0.497 秒/帧、GPU 利用率 92.4%;compute=2 → 5/5 干净。
    /// · 【提高 compute 既无吞吐收益、还更慢】同一素材逐档实测:1:1:1 = 0.87 秒/帧、GPU 59.5%;
    ///   1:2:1 = 1.03 / 53.3%;1:4:1(旧值)= 1.27~1.38 / 46~50%。
    ///   另一组生产形态实测 1:2:1=0.415、1:1:1=0.469、1:4:1(旧)=0.509 秒/帧,
    ///   1:6:1 / 1:8:1 / 2:4:4 / 4:8:8 全落在 ±10% 噪声内 —— "档位越高越快"从来就不成立。
    ///   超分本身是 GPU 瓶颈:多开 compute 线程只是把 Vulkan 提交队列挤爆,引擎反复重投/卡住,
    ///   GPU 利用率从 92% 掉到 18~25%,活干得更少 —— 这就是用户报的"超分慢 + GPU 占用很低"。
    /// · 硬上界 2:后续任何"按核数/并发调 compute"的尝试都不得越过 2(≥4 必掉进上述 vkQueueSubmit 风暴)。
    ///   连"多路并发按路数分摊"也不再需要:compute 恒 1 时,即便 SplitCores 许可 2 路并发,
    ///   聚合 proc 线程也只有 2 个,天然到不了 ≥4 的坏区间(原先的 ÷并发路数 正是为压制这个而存在)。
    /// 【旧注释的依据已作废,故不再沿用】旧注释称"compute=8 会让 realesrgan x4plus 系全黑",但 2026 在
    ///  1080×1920 上【没能复现】:1:8:1 与 1:6:1 都是 3.82/3.83 秒/帧、0 失败。旧依据靠不住,
    ///  而"compute≥4 → 83% 坏帧"这条是可复现的实测,所以取 1,而不是沿用"封顶 6/8"那套。
    /// 【load / save】恒 1:save>1 历史上同样会触发 vkQueueSubmit failed(黑帧,表现为"导出全黑/开头黑");
    ///  save 只写磁盘,单线程不会明显拖慢,稳定优先。
    /// 【共用性】本函数被图片超分 / 视频超分 / 逐块超分 / 补帧(rife)等多处共用。
    ///  收紧到 1 是【更保守】的方向,不会给任何一条路径引入新的黑帧风险;也已逐档实测确认没有路径变慢。</summary>
    public static string GetEngineThreadArgs()
    {
        const int load = 1;
        const int compute = 1;   // 硬上界 2(见上):≥4 必出 vkQueueSubmit 风暴 → 带状/整帧黑帧
        const int save = 1;      // 恒 1:防 ncnn-vulkan save 并发触发 GPU 队列失败(黑帧)
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
                progress?.Report((pct, $"已连续处理 {intervalTxt},休息 {RestDurationMin} 分钟给设备降温(点底部「跳过休息」可继续)..."));
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
    /// <summary>真弱设备(无GPU/核显/显存&lt;6/内存&lt;8/核数≤4) → 显示黄字提示。</summary>
    public static bool IsWeakDevice => _weak ??= ComputeWeakDevice();
    /// <summary>弱设备原因文案(如 "未检测到可用 GPU(Vulkan)、显存 8GB")。</summary>
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
            // 【裸设备·核显】只有【没有独显】才算弱(仅核显/无GPU);混合本(核显+独显)不算——否则会误判强机为弱设备
            bool igpu = false;
            try
            {
                bool hasDiscrete = false;
                if (ALHPro.VulkanCheck.Devices.Count > 0)
                    hasDiscrete = ALHPro.VulkanCheck.Devices.Any(d => GpuInfo.ScoreDeviceName(d.Name) > 0);
                else
                    foreach (var n in GpuInfo.GetAdapterNames())
                        if (GpuInfo.ScoreDeviceName(n) > 0) { hasDiscrete = true; break; }
                igpu = !hasDiscrete;   // 无独显 → 只有核显/无 GPU → 弱
            }
            catch { }
            return noGpu || smallVram || smallRam || fewCores || igpu;
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
            try
            {
                bool hasDiscrete = false;
                if (ALHPro.VulkanCheck.Devices.Count > 0)
                    hasDiscrete = ALHPro.VulkanCheck.Devices.Any(d => GpuInfo.ScoreDeviceName(d.Name) > 0);
                else
                    foreach (var n in GpuInfo.GetAdapterNames())
                        if (GpuInfo.ScoreDeviceName(n) > 0) { hasDiscrete = true; break; }
                if (!hasDiscrete) list.Add("核显(共享显存,较慢)");
            }
            catch { }
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
            SplitCores = true;    // 默认开启(下游软编线程按可用核分配;-j 已恒 1:1:1,见 GetEngineThreadArgs)
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
