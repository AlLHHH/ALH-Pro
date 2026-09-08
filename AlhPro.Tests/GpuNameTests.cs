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
}
