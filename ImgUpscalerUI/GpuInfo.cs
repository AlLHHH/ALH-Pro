// GpuInfo.cs — 枚举显卡适配器全名(注册表 PnP 显卡类)
// 显示名与引擎 -g 编号对应:GPU 0 = 第一块(通常为主显卡),依次类推。
using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace ALHPro;

public static class GpuInfo
{
    private const string DisplayClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <summary>常见虚拟显示器/基础适配器关键字(过滤,避免把虚拟设备当 GPU)。</summary>
    private static readonly string[] VirtualKeywords =
    {
        "Microsoft 基本显示适配器",
        "Microsoft Basic Display",
        "IddDriver",
        "Oray",
        "Virtual Display",
        "Virtual Display Adapter",
        "Indirect Display",
    };

    /// <summary>枚举显卡型号名称;顺序即 GPU 编号(与引擎 -g 参数对应)。
    /// 失败或全被过滤时返回空,UI 使用默认项。</summary>
    public static List<string> GetAdapterNames()
    {
        var names = new List<string>();
        try
        {
            using var baseKey = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            if (baseKey == null) return names;
            foreach (var sub in baseKey.GetSubKeyNames())
            {
                using var k = baseKey.OpenSubKey(sub);
                // 优先取完整型号,其次驱动描述
                var name = k?.GetValue("HardwareInformation.AdapterString") as string;
                if (string.IsNullOrWhiteSpace(name))
                    name = k?.GetValue("DriverDesc") as string;
                if (string.IsNullOrWhiteSpace(name)) continue;
                name = name.Trim();
                if (IsVirtual(name)) continue;
                if (!names.Contains(name)) names.Add(name);
            }
        }
        catch { /* 读取失败返回空 */ }
        return names;
    }

    /// <summary>显卡驱动版本列表(与 GetAdapterNames 同序;取不到时为空字符串)。
    /// 来源:注册表显卡类键的 DriverVersion(Windows 通用,NVIDIA/AMD/Intel 都读得到)。</summary>
    public static List<string> GetDriverVersions()
    {
        var versions = new List<string>();
        try
        {
            using var baseKey = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            if (baseKey == null) return versions;
            foreach (var sub in baseKey.GetSubKeyNames())
            {
                using var k = baseKey.OpenSubKey(sub);
                var name = k?.GetValue("HardwareInformation.AdapterString") as string;
                if (string.IsNullOrWhiteSpace(name))
                    name = k?.GetValue("DriverDesc") as string;
                if (string.IsNullOrWhiteSpace(name)) continue;
                name = name.Trim();
                if (IsVirtual(name)) continue;
                var ver = k?.GetValue("DriverVersion") as string;
                versions.Add(string.IsNullOrWhiteSpace(ver) ? "" : ver.Trim());
            }
        }
        catch { /* 读取失败返回空 */ }
        return versions;
    }

    /// <summary>核显识别(跳过):Intel UHD/Iris/HD Graphics 与新版"Intel(R) Graphics"命名、AMD Radeon(TM) Graphics 核显。</summary>
    public static bool IsIntegratedGPU(string n)
    {
        if (string.IsNullOrEmpty(n)) return true;
        if (n.Contains("AMD Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase)) return true;
        if (!n.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return false;
        return n.Contains("UHD", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Iris", StringComparison.OrdinalIgnoreCase)
            || n.Contains("HD Graphics", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Intel(R) Graphics", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Intel(R) Iris", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>设备优先级分数(越大越该用):核显=0(不选),NVIDIA=4,AMD 独显=3,Intel Arc=2,其他独显=1。</summary>
    public static int ScoreDeviceName(string n)
    {
        if (string.IsNullOrEmpty(n) || IsIntegratedGPU(n)) return 0;
        if (n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
            || n.Contains("GeForce", StringComparison.OrdinalIgnoreCase)
            || n.Contains("RTX", StringComparison.OrdinalIgnoreCase))
            return 4;
        if (n.Contains("AMD", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
            return 3;
        if (n.Contains("Arc", StringComparison.OrdinalIgnoreCase))
            return 2;
        return 1;
    }

    /// <summary>推荐索引(优先独显;独显内 NVIDIA &gt; AMD &gt; Intel Arc;全核显/未知时推荐 0)。</summary>
    public static int GetRecommendedIndex(List<string> names)
    {
        int bestIdx = -1, bestScore = -1;
        for (int i = 0; i < names.Count; i++)
        {
            var n = names[i];
            int score = ScoreDeviceName(n);
            if (score <= 0) continue;
            if (score > bestScore) { bestScore = score; bestIdx = i; }
        }
        return bestIdx >= 0 ? bestIdx : (names.Count > 0 ? 0 : -1);
    }

    /// <summary>生成下拉标签列表("GPU N · 型号",推荐项带标记)与推荐索引。</summary>
    public static (List<string> labels, int recommended) BuildLabels()
    {
        var names = GetAdapterNames();
        var labels = new List<string>();
        for (int i = 0; i < names.Count; i++)
            labels.Add($"GPU {i} · {names[i]}");
        var rec = GetRecommendedIndex(names);
        if (rec >= 0 && rec < labels.Count)
            labels[rec] += " (推荐)";
        return (labels, rec);
    }

    private static bool IsVirtual(string name)
    {
        foreach (var kw in VirtualKeywords)
        {
            if (name.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
