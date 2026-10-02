// GpuStatusProbe.cs — 读显示类设备(显卡)的"问题代码",用于解释"注册表里还在、枚举里却不见了"。
// 为什么不用 WMI:本工程没有 System.Management 依赖(离线环境也拉不到新 NuGet 包),
// 这里用 SetupAPI + cfgmgr32 直接问设备树,效果等价:
//   22 = 设备已被禁用(设备管理器里带向下箭头)     43 = 驱动报告异常,设备未能正常启动
//    0 = 没有报告问题
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ALHPro;

/// <summary>显示类设备的状态探测(SetupAPI/cfgmgr32,无外部依赖)。失败一律返回空表,不影响主流程。</summary>
internal static class GpuStatusProbe
{
    /// <summary>一张显示类设备的名字 + 问题代码(0 = 正常)。</summary>
    public readonly record struct AdapterStatus(string Name, int ProblemCode);

    private const int DIGCF_PRESENT = 0x2;
    private const uint SPDRP_DEVICEDESC = 0x0;

    /// <summary>显示适配器设备类 GUID:{4d36e968-e325-11ce-bfc1-08002be10318}</summary>
    private static readonly Guid DisplayClassGuid = new("4d36e968-e325-11ce-bfc1-08002be10318");

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
        uint property, out uint propertyRegDataType, byte[] propertyBuffer, uint propertyBufferSize, out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_Status(out uint status, out uint problemNumber, uint devInst, uint flags);

    /// <summary>枚举当前存在的显示类设备(含被禁用的)+ 各自问题代码。</summary>
    public static List<AdapterStatus> Query()
    {
        var list = new List<AdapterStatus>();
        IntPtr set = IntPtr.Zero;
        try
        {
            var guid = DisplayClassGuid; // readonly 字段不能按 ref 传,取本地副本
            set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return list;

            var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                string name = ReadProperty(set, ref data, SPDRP_DEVICEDESC);
                if (string.IsNullOrWhiteSpace(name)) continue;

                int problem = 0;
                try
                {
                    if (CM_Get_DevNode_Status(out _, out uint problemNumber, data.DevInst, 0) == 0)
                        problem = (int)problemNumber;
                }
                catch { }
                list.Add(new AdapterStatus(name.Trim(), problem));
            }
        }
        catch (Exception ex)
        {
            try { AppLogger.Warn("⚠ 显卡状态探测(SetupAPI)失败,跳过问题码判别:" + ex.GetType().Name + " " + ex.Message); } catch { }
        }
        finally
        {
            if (set != IntPtr.Zero && set != new IntPtr(-1))
            {
                try { SetupDiDestroyDeviceInfoList(set); } catch { }
            }
        }
        return list;
    }

    /// <summary>按显卡名找问题代码;找不到返回 null(报告里按"未取到状态"处理)。</summary>
    public static int? ProblemCodeOf(string? gpuName)
    {
        if (string.IsNullOrWhiteSpace(gpuName)) return null;
        try
        {
            foreach (var a in Query())
                if (AlhPro.Core.GpuVisibility.Same(a.Name, gpuName)) return a.ProblemCode;
        }
        catch { }
        return null;
    }

    private static string ReadProperty(IntPtr set, ref SP_DEVINFO_DATA data, uint property)
    {
        try
        {
            var buf = new byte[1024];
            if (!SetupDiGetDeviceRegistryProperty(set, ref data, property, out uint regType, buf, (uint)buf.Length, out uint needed))
                return "";
            // 只取第一个 UTF-16 终止符之前的字符:缓冲区后面是未写入的零,直接整块解码会带上杂字符。
            int end = 0;
            while (end + 1 < buf.Length && !(buf[end] == 0 && buf[end + 1] == 0)) end += 2;
            return System.Text.Encoding.Unicode.GetString(buf, 0, end).Trim();
        }
        catch { return ""; }
    }
}
