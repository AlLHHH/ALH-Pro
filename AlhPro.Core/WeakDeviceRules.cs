namespace AlhPro.Core;

/// <summary>「本机到底有没有可用的 GPU」——**三态**,不是布尔。
/// 【为什么必须是三态 · 2026-10-17 用户真机(RTX 4060 Laptop / 8GB 显存 / 32GB 内存 / 10 核)】
/// 用户看到开始前诊断卡片写着「⚠ 设备配置较低(核显/小显存/内存小)」并当场反问「我设备低???」。
/// 诊断包日志给出了完整的时序(同一秒内):
///   00:32:07 Vulkan 自检:设备表为空(缓存命中或尚未枚举),现场补一次引擎设备枚举
///   00:32:07 ⚠ 检测到设备配置较低(未检测到可用 GPU(Vulkan)),建议勾选「兼容模式」…
///   00:32:07 Vulkan 自检:补枚举成功,枚举到 1 个 Vulkan 设备
///   00:32:07 Vulkan 自检:GPU 引擎可用(Vulkan 设备枚举成功)
/// 根因有两层:①自检是后台跑的,`Done` 在**结果出来之前**就已置位(引擎正在枚举的那段时间里
/// `Done==true && GpuAvailable==false`),旧判据 `Done &amp;&amp; !GpuAvailable` 把这段"还不知道"读成了
/// "确定没有 GPU";②这个结论还被 `_weak ??=` 缓存到进程结束,自检随后测出 GPU 也不会纠正
/// (同一份日志里 8GB 显存、nvidia-smi 空闲 7GB 都写着)。
/// ⇒ 判据必须区分 **Unknown(没测完/没结论)** 与 **Absent(测完了,确实没有)**:
/// 只有 Absent 才允许说"未检测到可用 GPU",Unknown 一律按"有 GPU"处理(不冤枉好机器)。</summary>
public enum GpuPresence
{
    /// <summary>自检还没给出结论(后台探测进行中 / 缓存里只有旧结论)。**不许据此说用户没有显卡**。</summary>
    Unknown,
    /// <summary>自检有结论:引擎枚举到了可用设备。</summary>
    Usable,
    /// <summary>自检有结论:引擎一个设备都没枚举到(或引擎缺失/探测异常)。</summary>
    Absent,
}

/// <summary>弱设备判据的**唯一来源**(纯逻辑,不读本机状态)。抽到 Core 是为了能用真机画像做定点验证:
/// 本机(RTX 4060 Laptop)在"自检进行中"这一瞬**绝不许**被判弱;那台 GTX 1050 Ti 机
/// (VRAM 4GB / RAM 7.9GB / 8 核 / GPU 可用)照旧是弱(它真的偏弱,只是以前被说成"没有 GPU")。</summary>
public static class WeakDeviceRules
{
    /// <summary>显存小于这个数(GB)算"小显存"。</summary>
    public const double MinVramGb = 6.0;
    /// <summary>**标称**内存小于这个数(GB)算"内存小"(读数要先过 <see cref="NominalGb"/>)。</summary>
    public const double MinNominalRamGb = 8.0;
    /// <summary>核数小于等于这个数算"核心数较少"。</summary>
    public const int MaxFewCores = 4;

    /// <summary>自检状态 → 三态。**判据只有一条**:自检有没有给出结论。
    /// <paramref name="probeConcluded"/> 为 false 时无论 <paramref name="gpuAvailable"/> 是什么都是
    /// <see cref="GpuPresence.Unknown"/> —— 那正是启动早期那段"Done 已置位、结果还没出来"的窗口。</summary>
    public static GpuPresence Resolve(bool probeConcluded, bool gpuAvailable)
        => !probeConcluded ? GpuPresence.Unknown : (gpuAvailable ? GpuPresence.Usable : GpuPresence.Absent);

    /// <summary>只有"有结论且不可用"才是"没有 GPU"。</summary>
    public static bool IsNoGpu(GpuPresence presence) => presence == GpuPresence.Absent;

    /// <summary>把系统报的内存读数(GiB)归一到**标称容量**(GB)。门槛(8/16/32)是按标称容量写的,
    /// 而读数永远比标称小一点:标称 8GB 机实测报 7.9、16GB 报 15.9、32GB 报 31.797 ⇒ 不归一就会"差 0.1 掉一整档"。
    /// 归一口径 = 最近的整数 GiB(正好半整数时 AwayFromZero)。</summary>
    public static double NominalGb(double measuredGb) => Math.Round(measuredGb, MidpointRounding.AwayFromZero);

    /// <summary>是不是弱设备(无 GPU / 核显(仅核显,无独显) / 小显存 / 小内存 / 核数少)。
    /// **Unknown 不算"无 GPU"**,但仍会因显存/内存/核数/核显为真 —— 那些是即时读数,不受自检时序影响。</summary>
    public static bool IsWeak(GpuPresence presence, double vramGb, double ramGb, int cores, bool onlyIntegratedGpu)
        => IsNoGpu(presence)
           || vramGb < MinVramGb
           || NominalGb(ramGb) < MinNominalRamGb
           || cores <= MaxFewCores
           || onlyIntegratedGpu;

    /// <summary>用户能读懂的短理由清单(顿号分隔;空串 = 不弱)。只列**真的成立**的那几条 ——
    /// 没结论时绝不许出现"未检测到可用 GPU(Vulkan)"(2026-10-17 误报的原话)。</summary>
    public static string Describe(GpuPresence presence, double vramGb, double ramGb, int cores, bool onlyIntegratedGpu)
    {
        var list = new System.Collections.Generic.List<string>();
        if (IsNoGpu(presence)) list.Add("未检测到可用 GPU(Vulkan)");
        if (vramGb < MinVramGb) list.Add($"显存仅 {vramGb:0.#}GB");
        if (NominalGb(ramGb) < MinNominalRamGb) list.Add($"内存 {ramGb:0.#}GB");
        if (cores <= MaxFewCores) list.Add("核心数较少");
        if (onlyIntegratedGpu) list.Add("核显(共享显存,较慢)");
        return string.Join("、", list);
    }
}
