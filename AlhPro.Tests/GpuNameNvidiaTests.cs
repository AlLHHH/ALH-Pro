using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>
/// F2 相关:判定"要用的那张卡"是不是 NVIDIA —— 免探测快速通道的第一步。
/// 起因(2026-09-13 自检):原判据 !HasNonNvidiaGpu() 看的是整机有没有 A/I 卡,
/// 混显笔记本(Intel 核显 + N 卡)因此永远走不了快速通道,每个首次任务白等一次生产帧探测。
/// 真机形状取自本机与用户机器上实际枚举到的设备名。
/// </summary>
public class GpuNameNvidiaTests
{
    [Theory]
    [InlineData("NVIDIA GeForce RTX 4060", true)]
    [InlineData("NVIDIA GeForce RTX 4060 Laptop GPU", true)]
    [InlineData("NVIDIA GeForce RTX 5060 Laptop GPU", true)]          // 50 系(Blackwell)
    [InlineData("NVIDIA GeForce GTX 1060", true)]
    [InlineData("NVIDIA GeForce GTX 960M", true)]
    [InlineData("Quadro RTX 5000", true)]
    [InlineData("NVIDIA RTX 5000 Ada Generation", true)]
    [InlineData("Intel(R) Iris(R) Xe Graphics", false)]
    [InlineData("AMD Radeon(TM) 680M", false)]
    [InlineData("Microsoft Basic Render Driver", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void IsNvidia_recognises_the_names_seen_on_real_machines(string? name, bool want)
    {
        Assert.Equal(want, GpuName.IsNvidia(name));
    }

    [Fact]
    public void Translation_layer_names_look_like_nvidia_and_callers_must_exclude_them()
    {
        // 转译层设备名形如 "Microsoft Direct3D12 (NVIDIA GeForce RTX 4060 Laptop GPU)"。
        // IsNvidia 对它返回 true(名字里确实有 NVIDIA)—— 所以快速通道判据必须再排除 IsD3D12Translation,
        // 否则会把"经转译层跑 ncnn 出损坏帧"的设备当成可用。这条约束与 DeviceRouting 的既有口径一致。
        const string n = "Microsoft Direct3D12 (NVIDIA GeForce RTX 4060 Laptop GPU)";
        Assert.True(GpuName.IsNvidia(n));
        Assert.True(GpuName.IsD3D12Translation(n));
    }

    [Fact]
    public void Non_nvidia_names_are_never_treated_as_nvidia()
    {
        // 反向约束:漏判 NVIDIA 会让可用卡白等探测(慢),但误判非 N 卡为 N 卡更糟 —— 会跳过必须做的探测。
        Assert.False(GpuName.IsNvidia("Intel(R) UHD Graphics 630"));
        Assert.False(GpuName.IsNvidia("AMD Radeon RX 7900 XTX"));
        Assert.False(GpuName.IsNvidia("llvmpipe (LLVM 15.0.7, 256 bits)"));
    }
}
