namespace AlhPro.Core;

/// <summary>
/// 一张 DXGI 适配器(DirectML 设备号的唯一来源)。
///
/// <para><paramref name="Software"/> = true 表示它是 "Microsoft Basic Render Driver"(WARP)这类
/// 软件适配器:它会出现在 DXGI 枚举里,却**永远建不出 DirectML 会话**
/// (真机证据:2026-10-02 diagnostic.log 里对它建会话抛 OnnxRuntimeException,
/// HRESULT 0x80131500,内部 C0262002「指定的显示适配器无效」)。</para>
/// </summary>
public readonly record struct DxgiAdapterInfo(int Index, string Name, long DedicatedVramBytes, bool Software)
{
    /// <summary>是否可用于 DirectML:有名字,且不是软件适配器。</summary>
    public bool IsHardware => !Software && !string.IsNullOrWhiteSpace(Name);
}

/// <summary>从 DXGI 硬件适配器里挑出的 DirectML 设备号 + 是否属于降级 + 可直接写进日志的理由。</summary>
public readonly record struct DmlDevicePick(int Index, bool Degraded, string Reason);

/// <summary>
/// DirectML 设备兜底路由(纯函数,真机证据驱动)。
///
/// <para>背景(2026-10-02 事故):设置里选中的显卡(「计算设备 GPU 1」)在引擎枚举里不存在时,
/// 旧代码把设置里存的编号原样当成 DirectML 设备号,结果落到 DXGI 的软件适配器
/// (Microsoft Basic Render Driver)上,建会话必然失败 → 程序直接判定「DirectML 不可用」
/// → 整机退回 CPU(≈8 秒/帧),而旁边那张真正能用的硬件卡一次都没被试过。</para>
///
/// <para>因此这里的规矩是:**DirectML 设备号只能来自 DXGI 硬件适配器枚举**,
/// 软件适配器一律剔除;首选不可用时降级到其它硬件 GPU 并显式留痕,而不是静默失败。</para>
/// </summary>
public static class DmlDeviceRouting
{
    /// <summary>独显级显存门槛:1 GiB。</summary>
    public const long OneGiB = 1073741824L;

    /// <summary>只保留硬件适配器(DXGI 原始序)。</summary>
    public static List<DxgiAdapterInfo> HardwareOnly(IReadOnlyList<DxgiAdapterInfo>? adapters)
    {
        var list = new List<DxgiAdapterInfo>();
        if (adapters == null) return list;
        foreach (var a in adapters)
            if (a.IsHardware) list.Add(a);
        list.Sort((x, y) => x.Index.CompareTo(y.Index));
        return list;
    }

    /// <summary>独显级(专用显存 &gt; 1 GiB)适配器,显存大的在前。</summary>
    public static List<DxgiAdapterInfo> DiscreteOnly(IReadOnlyList<DxgiAdapterInfo>? adapters)
    {
        var list = new List<DxgiAdapterInfo>();
        foreach (var a in HardwareOnly(adapters))
            if (a.DedicatedVramBytes > OneGiB) list.Add(a);
        list.Sort((x, y) => y.DedicatedVramBytes.CompareTo(x.DedicatedVramBytes));
        return list;
    }

    /// <summary>
    /// 首选不可用时在硬件适配器里挑一个尽量好的:
    /// ① 专用显存最大的独显级卡;② 否则第一张硬件卡(核显也能跑 DirectML,但标 <c>Degraded = true</c>);
    /// ③ 一张硬件卡都没有 → <c>Index = -1</c>。
    /// </summary>
    public static DmlDevicePick PickFallback(IReadOnlyList<DxgiAdapterInfo>? adapters)
    {
        var hw = HardwareOnly(adapters);
        if (hw.Count == 0)
            return new DmlDevicePick(-1, false, "DXGI 枚举里没有硬件适配器(全是软件适配器,或枚举失败)");

        var discrete = DiscreteOnly(hw);
        if (discrete.Count > 0)
        {
            var best = discrete[0];
            bool igpu = GpuName.IsIntegrated(best.Name);
            return new DmlDevicePick(best.Index, igpu,
                $"降级选用 DXGI#{best.Index}({best.Name},专用显存约 {best.DedicatedVramBytes / OneGiB} GiB)");
        }

        var first = hw[0];
        return new DmlDevicePick(first.Index, true,
            $"没有独显级(专用显存 > 1 GiB)硬件适配器,降级用核显/集显 DXGI#{first.Index}({first.Name})");
    }

    /// <summary>
    /// 校验「想要」的 DirectML 设备号:它确实是一张硬件适配器就直接用它;
    /// 否则(编号不存在,或指向软件适配器)走 <see cref="PickFallback"/> 降级,并在理由里说清原因。
    /// </summary>
    public static DmlDevicePick ConfirmOrFallback(int preferredIndex, IReadOnlyList<DxgiAdapterInfo>? adapters)
    {
        bool pointsAtSoftware = false;
        if (preferredIndex >= 0)
        {
            foreach (var a in HardwareOnly(adapters))
                if (a.Index == preferredIndex)
                    return new DmlDevicePick(a.Index, false, $"DirectML #{a.Index}({a.Name}) 是硬件适配器");

            if (adapters != null)
                foreach (var a in adapters)
                    if (a.Index == preferredIndex && a.Software)
                    {
                        pointsAtSoftware = true;
                        break;
                    }
        }

        var fb = PickFallback(adapters);
        if (fb.Index < 0) return fb;

        string why =
            preferredIndex < 0 ? fb.Reason
            : pointsAtSoftware ? $"指定的 DirectML #{preferredIndex} 是软件适配器(Microsoft Basic Render Driver,不可用)→ {fb.Reason}"
            : $"指定的 DirectML #{preferredIndex} 在 DXGI 硬件适配器里不存在 → {fb.Reason}";
        return fb with { Reason = why };
    }
}
