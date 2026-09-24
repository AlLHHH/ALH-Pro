using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace ALHPro;

/// <summary>【跨厂商显存读数 · 2026-09-24】用 Windows 性能计数器「GPU Adapter Memory」读"当前已用显存",
/// 让 AMD/Intel 机器也能真测空闲显存(此前只有 NVIDIA 能读 nvidia-smi ⇒ 显存墙在 A 卡/核显上整层失效)。
///
/// 【为什么走 PDH 而不是厂商 SDK】ADL(AMD)/厂商私有接口都要额外依赖与各自硬件才能验证;性能计数器是
/// Windows 自带、1709 起就有、**跨厂商**。读数换算与 LUID 解析都在
/// <see cref="AlhPro.Core.GpuMemCounters"/>(纯逻辑 + 单测),这里只负责 P/Invoke 与控制台语系无关的
/// 计数器名(用 <c>PdhAddEnglishCounter</c>,中文系统上也能工作)。
///
/// 【失败即返回 null】任何一步失败(计数器集不存在、权限、驱动不给数)都返回 null ⇒ 上层保持"未实测",
/// **绝不返回估值冒充实测**(这条是 1.3.x 的教训:曾经用"总量×0.8"当实测值,把好机器误降档)。
///
/// 【已知边界】① 多实例(多卡)且拿不到目标 LUID 时,<see cref="AlhPro.Core.GpuMemCounters.SumDedicatedBytes"/>
/// 会把各适配器已用求和 ⇒ 多卡机偏保守(低估空闲、少放批次),不会反向放行;
/// ② 计数器的"已用"与 DXGI 的 DedicatedVideoMemory 口径不完全一致,换算后由
/// <see cref="AlhPro.Core.GpuMemCounters.FreeVramGB"/> 钳到 [0, 总量]。 </summary>
internal static class GpuMemoryPdh
{
    private const uint PDH_MORE_DATA = 0x800007D2;
    private const uint PDH_CSTATUS_VALID_DATA = 0x00000000;
    private const uint PDH_CSTATUS_NEW_DATA = 0x00000001;
    private const uint PDH_FMT_DOUBLE = 0x00000200;

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE { public uint CStatus; public double doubleValue; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_ITEM
    {
        public IntPtr szName;
        public PDH_FMT_COUNTERVALUE FmtValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufSize,
        out uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    /// <summary>读数:各适配器实例的 (实例名, 已用字节)。失败返回 null(调用方保持"未实测")。</summary>
    internal static List<(string Name, double Bytes)>? TryReadDedicatedUsage()
    {
        IntPtr query = IntPtr.Zero, counter = IntPtr.Zero;
        try
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out query) != 0) return null;
            // 英文计数器路径:系统语言为中文时 PdhAddCounterW 会失败,必须用 AddEnglishCounter
            if (PdhAddEnglishCounterW(query, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out counter) != 0)
                return null;
            if (PdhCollectQueryData(query) != 0) return null;
            // 第一次采集后"已用"就是即时值(不是速率),不需要第二遍

            uint size = 0, count = 0;
            uint rc = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref size, out count, IntPtr.Zero);
            if (rc != PDH_MORE_DATA || size == 0) return null;
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                rc = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref size, out count, buf);
                if (rc != 0) return null;
                var list = new List<(string, double)>((int)count);
                int stride = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
                for (int i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buf + i * stride);
                    string name = item.szName == IntPtr.Zero ? "" : (Marshal.PtrToStringUni(item.szName) ?? "");
                    if (item.FmtValue.CStatus is not (PDH_CSTATUS_VALID_DATA or PDH_CSTATUS_NEW_DATA)) continue;
                    list.Add((name, item.FmtValue.doubleValue));
                }
                return list;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return null; }
        finally
        {
            if (counter != IntPtr.Zero) { /* 由 query 释放 */ }
            if (query != IntPtr.Zero) PdhCloseQuery(query);
        }
    }

    /// <summary>空闲显存(GB):用 PDH 的"已用"与给定总量换算;读不到计数器返回 null。</summary>
    internal static double? TryGetFreeVramGB(double totalVramGB)
    {
        var instances = TryReadDedicatedUsage();
        if (instances == null || instances.Count == 0) return null;
        double used = AlhPro.Core.GpuMemCounters.SumDedicatedBytes(instances.Select(x => (x.Name, x.Bytes)));
        return AlhPro.Core.GpuMemCounters.FreeVramGB(totalVramGB, used);
    }
}
