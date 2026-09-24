namespace AlhPro.Core;

/// <summary>【显存监控的跨厂商口径 · 2026-09-24】把 Windows 性能计数器「GPU Adapter Memory」的原始读数
/// 换算成"当前空闲显存"。纯逻辑(不碰 PDH/DXGI),便于单测。
///
/// 【为什么需要它】在此之前"当前空闲显存"只有 NVIDIA 能真读(nvidia-smi);AMD/Intel 上
/// <c>FreeVramMeasured=false</c> ⇒ <c>FreeVramGB</c> 退回"总量×0.8"的估值,而调用方被要求
/// **不许**把估值当判据 ⇒ 显存墙与并发档位在 A 卡/核显上整层失效(只能是"未实测")。
/// Windows 从 1709 起提供性能计数器 <c>\GPU Adapter Memory(*)\Dedicated Usage</c>(按适配器实例),
/// 这是**跨厂商**的:用它 + DXGI 报的 DedicatedVideoMemory 就能算出空闲量,不再只认 N 卡。
///
/// 【口径与边界(必须如实)】
///  ① 计数器给的是"已用"(Dedicated Usage),不是"空闲";空闲 = 总量 − 已用。
///  ② 实例名里带 LUID(形如 <c>luid_0x00000000_0x0000A1B2_phys_0</c>)。本类提供
///     <see cref="ParseInstanceLuid"/> 解析它,调用方**可以**只取目标适配器那一条;
///     拿不到目标 LUID 时 <see cref="SumDedicatedBytes"/> 会把全部实例求和 ——
///     多卡机上这会**高估已用** ⇒ 低估空闲 ⇒ 偏保守(宁可少放批次,也不放行到爆显存)。
///  ③ 数值一律按"字节"进出,返回 GB 时做钳位:负值/超过总量都夹到 [0, total] —— 计数器与
///     DXGI 的口径并不完全一致(共享显存、跨适配器重复计数),算出"空闲比总量还大"必须夹住,
///     否则上层会拿一个不可能的数去放大批次。</summary>
public static class GpuMemCounters
{
    /// <summary>解析 PDH 实例名里的 LUID(<c>luid_0xHIGH_0xLOW_phys_N</c>)。
    /// 解析不出来返回 null(调用方按"这个实例认不出属于谁"处理)。</summary>
    public static (uint High, uint Low)? ParseInstanceLuid(string? instanceName)
    {
        if (string.IsNullOrWhiteSpace(instanceName)) return null;
        string s = instanceName!.Trim();
        int i = s.IndexOf("luid_0x", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        int j = s.IndexOf("_0x", i + 7, StringComparison.OrdinalIgnoreCase);
        if (j < 0) return null;
        string hi = s.Substring(i + 7, j - (i + 7));
        int k = s.IndexOf('_', j + 3);
        string lo = k > 0 ? s.Substring(j + 3, k - (j + 3)) : s.Substring(j + 3);
        if (!uint.TryParse(hi, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out uint h)) return null;
        if (!uint.TryParse(lo, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out uint l)) return null;
        return (h, l);
    }

    /// <summary>把若干实例的"已用字节"求和。<paramref name="luidFilter"/> 非空时只统计匹配该 LUID 的实例
    /// (多卡机只算目标卡);负值/NaN 一律忽略(计数器偶发给脏值时不参与求和)。</summary>
    public static double SumDedicatedBytes(IEnumerable<(string Name, double Bytes)>? instances,
        (uint High, uint Low)? luidFilter = null)
    {
        if (instances == null) return 0;
        double sum = 0;
        foreach (var (name, bytes) in instances)
        {
            if (double.IsNaN(bytes) || bytes <= 0) continue;
            if (luidFilter.HasValue)
            {
                var luid = ParseInstanceLuid(name);
                if (luid is null) continue;                       // 认不出归属:过滤时直接跳过(不猜)
                if (luid.Value.High != luidFilter.Value.High || luid.Value.Low != luidFilter.Value.Low) continue;
            }
            sum += bytes;
        }
        return sum;
    }

    /// <summary>空闲显存(GB)= 总量 − 已用,并钳到 [0, total]。总量 ≤ 0 时返回 null(调用方保持"未实测")。</summary>
    public static double? FreeVramGB(double totalVramGB, double dedicatedUsedBytes)
    {
        if (!(totalVramGB > 0) || double.IsNaN(totalVramGB)) return null;
        double usedGB = dedicatedUsedBytes > 0 ? dedicatedUsedBytes / 1073741824.0 : 0;
        double free = totalVramGB - usedGB;
        if (free < 0) free = 0;
        if (free > totalVramGB) free = totalVramGB;
        return free;
    }
}
