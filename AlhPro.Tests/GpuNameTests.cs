using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>
/// 显卡名 → Blackwell 判定的单测。误判的代价是隐性的:跑得动 ncnn 的卡被推到更慢的 ONNX,
/// 用户只看到"变慢",日志里一个异常都没有,所以这类正则必须有测试守着。
/// </summary>
public class GpuNameTests
{
    [Theory]
    [InlineData("NVIDIA GeForce RTX 5090")]
    [InlineData("NVIDIA GeForce RTX 5080")]
    [InlineData("NVIDIA GeForce RTX 5070 Ti")]
    [InlineData("NVIDIA GeForce RTX 5060 Laptop GPU")]
    [InlineData("NVIDIA RTX 5060")]
    public void Blackwell_consumer_cards_are_detected(string name)
    {
        Assert.True(GpuName.IsBlackwell(name));
    }

    [Theory]
    // 名字里含 "RTX 50xx"/"RTX 58xx" 却不是 Blackwell,且 ncnn-Vulkan 正常可用
    [InlineData("NVIDIA RTX 5000 Ada Generation")]
    [InlineData("NVIDIA RTX 5880 Ada Generation")]
    [InlineData("NVIDIA RTX 4000 Ada Generation")]
    [InlineData("Quadro RTX 5000")]
    [InlineData("Quadro RTX 8000")]
    // 老架构与核显
    [InlineData("NVIDIA GeForce RTX 4060 Laptop GPU")]
    [InlineData("NVIDIA GeForce GTX 1660 Ti")]
    [InlineData("NVIDIA GeForce RTX 3060")]
    [InlineData("AMD Radeon RX 7900 XTX")]
    [InlineData("Intel(R) UHD Graphics 770")]
    [InlineData("Intel(R) Arc(TM) A770 Graphics")]
    public void Non_blackwell_cards_are_not_detected(string name)
    {
        Assert.False(GpuName.IsBlackwell(name));
    }

    [Theory]
    [InlineData("NVIDIA RTX PRO 6000 Blackwell Workstation Edition")]
    [InlineData("NVIDIA GeForce RTX 5090 Blackwell")]
    public void Blackwell_named_pro_cards_are_detected(string name)
    {
        Assert.True(GpuName.IsBlackwell(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_names_are_not_blackwell(string? name)
    {
        Assert.False(GpuName.IsBlackwell(name));
    }

    [Fact]
    public void AnyIsBlackwell_ignores_the_other_cards_on_a_multi_gpu_machine()
    {
        // 双卡机:一张 50 系 + 一张老卡 → 仍按 50 系走(引擎编号选择另有真机探测兜底)
        Assert.True(GpuName.AnyIsBlackwell(new[] { "Intel(R) UHD Graphics", "NVIDIA GeForce RTX 5070" }));
        // 全是能跑 ncnn 的卡 → 绝不降级到 ONNX
        Assert.False(GpuName.AnyIsBlackwell(new[] { "NVIDIA RTX 5000 Ada Generation", "NVIDIA GeForce RTX 4060" }));
        Assert.False(GpuName.AnyIsBlackwell(new string?[0]));
    }

    // ===== D3D12 转译层设备判定 =====
    // 漏判的代价是用户拿到整帧损坏的补帧视频且日志无异常;误判只是剔除一个冗余设备
    // (同卡原生 Vulkan 设备总在表里),所以负例(绝不能被剔除的名字)比正例更重要。

    [Theory]
    // 真机(2026-09-09 诊断包)引擎实际枚举出的三个转译设备,编号 1/3/5 夹在原生设备 0/2 中间
    [InlineData("Microsoft Direct3D12 (NVIDIA GeForce RTX 4060 Laptop GPU)")]
    [InlineData("Microsoft Direct3D12 (Intel(R) UHD Graphics)")]
    [InlineData("Microsoft Direct3D12 (Microsoft Basic Render Driver)")]
    [InlineData("microsoft direct3d12 (NVIDIA GeForce RTX 4060 Laptop GPU)")]
    public void D3D12_translation_layer_devices_are_detected(string name)
    {
        Assert.True(GpuName.IsD3D12Translation(name));
    }

    [Theory]
    // 同机器的原生 Vulkan 设备:剔除转译设备后它们是唯一可用项,绝不能被误剔
    [InlineData("NVIDIA GeForce RTX 4060 Laptop GPU")]
    [InlineData("Intel(R) UHD Graphics")]
    // 注册表口径的名字(不含 Direct3D12 前缀)
    [InlineData("NVIDIA GeForce RTX 5090")]
    [InlineData("AMD Radeon RX 7900 XTX")]
    // 虚拟适配器由 IsVirtual 负责,不走这个判定
    [InlineData("Microsoft Basic Display Adapter")]
    [InlineData("Microsoft 基本显示适配器")]
    [InlineData("OrayIddDriver Device")]
    // 名字里含 "Direct3D12" 但前缀不同的假想命名:宁可漏剔(有探测兜底),不可误剔好卡
    [InlineData("Direct3D12 (NVIDIA GeForce RTX 4060 Laptop GPU)")]
    public void Native_and_registry_devices_are_not_translation_layer(string name)
    {
        Assert.False(GpuName.IsD3D12Translation(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_names_are_not_translation_layer(string? name)
    {
        Assert.False(GpuName.IsD3D12Translation(name));
    }

    // ===== 核显判定(Vega 分支) =====
    // 历史事故:正则写成 @"Vega\s*(?:3|4|…|11)(?!\s*(?:56|64))",交替组只吃掉【一位】数字,
    // 前瞻随即在剩下的 "6"/"4" 上求值 → "\s*56" 永远匹配不上 → 前瞻恒成功 →
    // RX Vega 56/64(独显)被判成核显。后果不是崩溃而是"用户可见的错误行为且无异常可查":
    // IsWeakDevice 误判、弹假的"当前用核显请开兼容模式"、以及 wantDiscrete 取反去问 Windows 要省电偏好。
    // 这一档此前【零覆盖】,所以错了很久没人发现。
    [Theory]
    [InlineData("AMD Radeon RX Vega 56")]
    [InlineData("AMD Radeon RX Vega 64")]
    [InlineData("AMD Radeon RX Vega 56 8GB")]
    [InlineData("AMD Radeon RX Vega 64 Liquid")]
    [InlineData("Radeon RX Vega 56")]
    public void Vega_discrete_cards_are_not_integrated(string name)
    {
        Assert.False(GpuName.IsIntegrated(name));
    }

    [Theory]
    [InlineData("AMD Radeon(TM) Vega 8 Graphics")]
    [InlineData("AMD Radeon Vega 3")]
    [InlineData("AMD Radeon Vega 8")]
    [InlineData("AMD Radeon Vega 11")]
    [InlineData("AMD Radeon Vega 10")]
    [InlineData("AMD Radeon(TM) Graphics")]   // AMD 核显在注册表里的典型名字(主力机上就是它)
    [InlineData("AMD Radeon Graphics")]
    public void Vega_apu_graphics_are_integrated(string name)
    {
        Assert.True(GpuName.IsIntegrated(name));
    }

    [Theory]
    [InlineData("AMD Radeon RX 580")]
    [InlineData("AMD Radeon RX 7900 XTX")]
    [InlineData("NVIDIA GeForce RTX 5070 Ti")]
    [InlineData("Intel(R) Arc(TM) A770 Graphics")]
    public void Non_vega_cards_are_not_integrated(string name)
    {
        Assert.False(GpuName.IsIntegrated(name));
    }

    [Fact]
    public void Discrete_vega_outscores_integrated_vega()
    {
        // 路由层真正使用的是 Score():核显=0 会被 BestDiscrete 排除。
        // 若回归成"RX Vega 56 是核显",这里会直接变成 0。
        Assert.True(GpuName.Score("AMD Radeon RX Vega 56") > 0);
        Assert.Equal(0, GpuName.Score("AMD Radeon Vega 8"));
    }
}
