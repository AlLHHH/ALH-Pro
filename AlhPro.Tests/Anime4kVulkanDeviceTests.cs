using System.Collections.Generic;
using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【2026-09-23 Anime4K 收口】用户那台 5060 Laptop 的诊断包里,1x 修复(Anime4K)的探测结论是
/// `无响应(超时被强杀)`,而请求里给的方向是"让它用 ALH_FORCE_ANIME4K_DEVICE=&lt;n&gt; 试出可用索引 →
/// 接进正式滤镜 → 做成按显卡名自动选设备"。这个文件把**自动选设备**的判据钉住:
///   · 枚举结果的解析(用本机真机跑出来的原文当样本);
///   · 三条选择策略(单设备不动 / 按界面选的卡 / 优先独显 NVIDIA);
///   · 索引越界是硬错误(真机实测 exit -19)⇒ 所以**宁可返回 null 也不猜**。</summary>
public class Anime4kVulkanDeviceTests
{
    /// <summary>本机真机跑的 ffmpeg 输出原文(2026-09-23,443ms)。**这是唯一被实测过的样本**。</summary>
    private const string RealSingleDeviceLog = @"
[Vulkan @ 000002b5baa72140] Supported layers:
[Vulkan @ 000002b5baa72140] 	VK_LAYER_NV_optimus
[Vulkan @ 000002b5baa72140] GPU listing:
[Vulkan @ 000002b5baa72140]     0: NVIDIA GeForce RTX 4060 Laptop GPU (discrete) (0x28a0)
[Vulkan @ 000002b5baa72140] Device 0 selected: NVIDIA GeForce RTX 4060 Laptop GPU (discrete) (0x28a0)
[Vulkan @ 000002b5baa72140] Using device extension VK_KHR_push_descriptor
";

    /// <summary>双显卡机的形状(按同一格式构造,用于策略测试;**不是**真机样本)。</summary>
    private const string DualGpuLog = @"
[Vulkan @ 000001a9987ec4c0] GPU listing:
[Vulkan @ 000001a9987ec4c0]     0: Intel(R) Iris(R) Xe Graphics (integrated) (0x9a49)
[Vulkan @ 000001a9987ec4c0]     1: NVIDIA GeForce RTX 5060 Laptop GPU (discrete) (0x2d19)
[Vulkan @ 000001a9987ec4c0] Device 0 selected: Intel(R) Iris(R) Xe Graphics (integrated) (0x9a49)
";

    [Fact]
    public void Parses_the_real_single_gpu_listing()
    {
        var ds = Anime4kVulkanDevice.ParseGpuListing(RealSingleDeviceLog);
        Assert.Single(ds);
        Assert.Equal(0, ds[0].Index);
        Assert.Equal("NVIDIA GeForce RTX 4060 Laptop GPU", ds[0].Name);   // 括号里的类型与十六进制 id 都不进名字
        Assert.True(ds[0].Discrete);
        Assert.False(ds[0].Integrated);
    }

    [Fact]
    public void Parses_a_dual_gpu_listing_with_kinds()
    {
        var ds = Anime4kVulkanDevice.ParseGpuListing(DualGpuLog);
        Assert.Equal(2, ds.Count);
        Assert.True(ds[0].Integrated);
        Assert.Equal("Intel(R) Iris(R) Xe Graphics", ds[0].Name);
        Assert.True(ds[1].Discrete);
        Assert.Equal(1, ds[1].Index);
    }

    /// <summary>★ 本机的真实情形:只有一台设备 ⇒ **不加任何参数**(与改动前逐字一致 ——
    /// 本机 1x 修复本来就是好的,不该被这次改动碰)。</summary>
    [Fact]
    public void Single_device_keeps_the_default_behaviour()
    {
        var ds = Anime4kVulkanDevice.ParseGpuListing(RealSingleDeviceLog);
        Assert.Null(Anime4kVulkanDevice.Pick(ds, "NVIDIA GeForce RTX 4060 Laptop GPU"));
    }

    /// <summary>★ 双显卡机:必须选到**独显**(那台 5060),不能选核显 —— 这是本条请求的核心。</summary>
    [Fact]
    public void Dual_gpu_prefers_the_discrete_nvidia()
    {
        var ds = Anime4kVulkanDevice.ParseGpuListing(DualGpuLog);
        Assert.Equal(1, Anime4kVulkanDevice.Pick(ds, null));
        // 就算界面选的卡名写法与 ffmpeg 不同(少了 "Laptop"),也要选到它(型号数字兜底)
        Assert.Equal(1, Anime4kVulkanDevice.Pick(ds, "NVIDIA GeForce RTX 5060"));
    }

    /// <summary>如果用户**明确**在界面上选了核显,那就别自作主张钉到独显上(处理与滤镜用同一张卡才自洽)。</summary>
    [Fact]
    public void Explicitly_selected_device_wins()
    {
        var ds = Anime4kVulkanDevice.ParseGpuListing(DualGpuLog);
        Assert.Equal(0, Anime4kVulkanDevice.Pick(ds, "Intel(R) Iris(R) Xe Graphics"));
    }

    /// <summary>【不猜】全是核显/虚拟设备、或解析不出来 ⇒ 返回 null(保持 ffmpeg 默认)。
    /// 理由:钉错设备的代价(超时/挂死)比"可能选到核显"更糟。</summary>
    [Fact]
    public void No_discrete_device_is_not_guessed()
    {
        var onlyIntegrated = Anime4kVulkanDevice.ParseGpuListing(@"
[Vulkan @ 1] GPU listing:
[Vulkan @ 1]     0: Intel(R) UHD Graphics 630 (integrated) (0x3e9b)
[Vulkan @ 1]     1: Microsoft Basic Render Driver (virtual) (0x0000)
[Vulkan @ 1] Device 0 selected: Intel(R) UHD Graphics 630 (integrated) (0x3e9b)
");
        Assert.Equal(2, onlyIntegrated.Count);
        Assert.Null(Anime4kVulkanDevice.Pick(onlyIntegrated, "Some Other Card"));
        Assert.Null(Anime4kVulkanDevice.Pick(new List<Anime4kVulkanDevice.Device>(), null));
        Assert.Empty(Anime4kVulkanDevice.ParseGpuListing(null));
        Assert.Empty(Anime4kVulkanDevice.ParseGpuListing(""));
        Assert.Empty(Anime4kVulkanDevice.ParseGpuListing("完全无关的输出\n没有任何列表"));
    }

    /// <summary>名字匹配**不许**用"包含数字"这种模糊口径:否则 "RTX 4060" 可能匹配到别的型号,
    /// 而选错的后果是滤镜跑到核显/别的卡上 ⇒ 宁可放弃匹配走保守策略。</summary>
    [Fact]
    public void Name_matching_does_not_fuzzily_pick_a_different_model()
    {
        var ds = new List<Anime4kVulkanDevice.Device>
        {
            new(0, "Intel(R) Arc(TM) A770 Graphics (discrete)", "discrete"),
            new(1, "NVIDIA GeForce RTX 5070 Ti (discrete)", "discrete"),
        };
        // "4060" 与两台都不沾边 ⇒ 走 ③(独立+NVIDIA)= 索引 1,而不是"看着像就选第一台"
        Assert.Equal(1, Anime4kVulkanDevice.Pick(ds, "NVIDIA GeForce RTX 4060 Laptop GPU"));
    }

    /// <summary>设备参数的形状(真机实测:索引 0 + Anime4K 滤镜 → exit 0;索引越界 → exit -19 硬错误)。</summary>
    [Fact]
    public void Device_args_shape()
    {
        Assert.Equal("-init_hw_device vulkan=alh:3 -filter_hw_device alh ", Anime4kVulkanDevice.DeviceArgs(3));
        Assert.Contains("-init_hw_device vulkan=alh:0", Anime4kVulkanDevice.DeviceArgs(0));
        // 解析 + 设备参数连起来:索引来自解析结果,不是写死的
        var ds = Anime4kVulkanDevice.ParseGpuListing(DualGpuLog);
        Assert.Contains("alh:1", Anime4kVulkanDevice.DeviceArgs(Anime4kVulkanDevice.Pick(ds, null)!.Value));
    }

    /// <summary>日志里必须同时有"枚举结果"和"为什么这么选"(否则下次还是得让用户跑一遍 ffmpeg)。</summary>
    [Fact]
    public void Describe_reports_both_the_listing_and_the_decision()
    {
        var ds = Anime4kVulkanDevice.ParseGpuListing(DualGpuLog);
        var s = Anime4kVulkanDevice.Describe(ds, Anime4kVulkanDevice.Pick(ds, "NVIDIA GeForce RTX 5060 Laptop GPU"),
            "NVIDIA GeForce RTX 5060 Laptop GPU", "自动检测");
        Assert.Contains("[0] Intel(R) Iris(R) Xe Graphics(核显)", s);
        Assert.Contains("[1] NVIDIA GeForce RTX 5060 Laptop GPU(独显)", s);
        Assert.Contains("⇒ 使用索引 1", s);
        Assert.Contains("界面选的卡", s);

        var s1 = Anime4kVulkanDevice.Describe(Anime4kVulkanDevice.ParseGpuListing(RealSingleDeviceLog), null, null, "自动检测");
        Assert.Contains("只有一台设备,不加参数", s1);
    }

    /// <summary>枚举命令必须是"不碰素材、不写文件"的那一条(本机实测 443ms)。</summary>
    [Fact]
    public void List_command_is_harmless_and_verbose()
    {
        Assert.Contains("-v verbose", Anime4kVulkanDevice.ListDevicesArgs);
        Assert.Contains("-init_hw_device vulkan=alh", Anime4kVulkanDevice.ListDevicesArgs);
        Assert.Contains("-f null -", Anime4kVulkanDevice.ListDevicesArgs);
        Assert.Contains("lavfi", Anime4kVulkanDevice.ListDevicesArgs);
    }
}
