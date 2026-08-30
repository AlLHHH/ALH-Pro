// AppSettings.cs — 应用级通用设置(左下角设置弹窗里的开关),存 %LOCALAPPDATA%\ALHPro\app-settings.json
using System;
using System.IO;

namespace ALHPro;

/// <summary>应用级通用设置(所有页面共用,设置弹窗修改)。</summary>
public static class AppSettings
{
    /// <summary>处理完成后自动删除项目(等 3 秒再删,留时间看完成信息)。</summary>
    public static bool AutoRemoveDone { get; set; }

    /// <summary>全局计算设备(gpuId 语义):-1=CPU(软件计算),≥0=GPU 编号。三个功能页共用,设置弹窗统一修改。</summary>
    public static int GpuIndex { get; set; } = 0;

    /// <summary>抠图推理设备:false=GPU(DirectML,默认快);true=CPU 软算(保界面流畅)。
    /// v1.1 起默认 GPU:推理快几倍;GPU 失败自动降级 CPU(见 CutoutService 兼容适配),
    /// 用户可手动切回 CPU(抠图页提示:若窗口抖动再切 CPU)。</summary>
    public static bool CutoutCpuOnly { get; set; } = false;

    /// <summary>是否已完成首次 Vulkan 自检(只跑一次,结果缓存)。</summary>
    public static bool VulkanCheckDone { get; set; }

    /// <summary>首次 Vulkan 自检的友好报告文本(设置界面「计算设备」区常驻显示)。</summary>
    public static string VulkanReport { get; set; } = "";

    private static string FilePath => ParaPaths.SettingsFile("app-settings.json");

    public static void Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                // 首次启动:自动采用推荐 GPU(跳过 Intel/AMD 核显选独立显卡),不再默认 0(核显)。
                // 此前默认 0 会让"软件推荐 NVIDIA 但实际用核显"——补帧/超分极慢,用户以为卡死。
                try
                {
                    var names = GpuInfo.GetAdapterNames();
                    int rec = GpuInfo.GetRecommendedIndex(names);
                    if (rec >= 0) GpuIndex = rec;
                }
                catch { }
                return;
            }
            var d = System.Text.Json.JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath));
            if (d is null) return;
            AutoRemoveDone = d.AutoRemoveDone;
            GpuIndex = d.GpuIndex;
            CutoutCpuOnly = d.CutoutCpuOnly;
            VulkanCheckDone = d.VulkanCheckDone;
            VulkanReport = d.VulkanReport ?? "";
        }
        catch { /* 读取失败用默认值 */ }
    }

    /// <summary>写入锁(多线程并发 Save 时防止文件写入交错损坏)。</summary>
    private static readonly object _saveLock = new();

    public static void Save()
    {
        lock (_saveLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath,
                    System.Text.Json.JsonSerializer.Serialize(new Data
                    {
                        AutoRemoveDone = AutoRemoveDone,
                        GpuIndex = GpuIndex,
                        CutoutCpuOnly = CutoutCpuOnly,
                        VulkanCheckDone = VulkanCheckDone,
                        VulkanReport = VulkanReport,
                    }));
            }
            catch { /* 保存失败忽略 */ }
        }
    }

    private sealed class Data
    {
        public bool AutoRemoveDone { get; set; }
        public int GpuIndex { get; set; } = 0;
        public bool CutoutCpuOnly { get; set; } = false;
        public bool VulkanCheckDone { get; set; }
        public string VulkanReport { get; set; } = "";
    }
}
