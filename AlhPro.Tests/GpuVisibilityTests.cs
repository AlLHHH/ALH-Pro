using System.Collections.Generic;
using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>
/// 「注册表里看得见、枚举里不见了」的显卡诊断纯逻辑测试。
/// 形状取自 2026-10-02 真机诊断包:注册表枚举[#0 NVIDIA GeForce RTX 5060 Laptop GPU | #1 AMD Radeon(TM) 610M],
/// 而当天引擎枚举只剩[#0 AMD Radeon(TM) 610M]、DXGI 枚举[#0 AMD | #1 Microsoft Basic Render Driver]。
/// </summary>
public class GpuVisibilityTests
{
    private static readonly List<string> Registry = new()
    {
        "NVIDIA GeForce RTX 5060 Laptop GPU",
        "AMD Radeon(TM) 610M",
    };

    [Fact]
    public void MissingFromEnumerations_能识别注册表可见但两套枚举都不见的独显()
    {
        var missing = GpuVisibility.MissingFromEnumerations(
            Registry,
            new List<string> { "AMD Radeon(TM) 610M" },
            new List<string> { "AMD Radeon(TM) 610M", "Microsoft Basic Render Driver" });

        Assert.Single(missing);
        Assert.Equal("NVIDIA GeForce RTX 5060 Laptop GPU", missing[0]);
    }

    [Fact]
    public void MissingFromEnumerations_两套枚举都齐全时没有任何缺失()
    {
        var missing = GpuVisibility.MissingFromEnumerations(
            Registry,
            new List<string> { "AMD Radeon(TM) 610M", "NVIDIA GeForce RTX 5060 Laptop GPU" },
            new List<string> { "AMD Radeon(TM) 610M", "NVIDIA GeForce RTX 5060 Laptop GPU", "Microsoft Basic Render Driver" });

        Assert.Empty(missing);
    }

    [Fact]
    public void MissingFromEnumerations_只在DXGI里出现也算可见()
    {
        // 引擎没枚举到、但 DXGI 里有 → 不算"消失"(DirectML 这条路还能用)
        var missing = GpuVisibility.MissingFromEnumerations(
            new List<string> { "NVIDIA GeForce RTX 5060 Laptop GPU" },
            new List<string>(),
            new List<string> { "NVIDIA GeForce RTX 5060 Laptop GPU" });

        Assert.Empty(missing);
    }

    [Fact]
    public void MissingFromEnumerations_同一张卡重复出现只报一次()
    {
        var missing = GpuVisibility.MissingFromEnumerations(
            new List<string> { "NVIDIA GeForce RTX 5060 Laptop GPU", "NVIDIA GeForce RTX 5060 Laptop GPU" },
            new List<string>(),
            new List<string>());

        Assert.Single(missing);
    }

    [Fact]
    public void MissingFromEnumerations_null表不炸()
    {
        Assert.Empty(GpuVisibility.MissingFromEnumerations(null, null, null));
        Assert.Empty(GpuVisibility.MissingFromEnumerations(new List<string>(), null, null));
    }

    [Fact]
    public void MissingFromEnumerations_null引擎表时仍能报出缺失()
    {
        var missing = GpuVisibility.MissingFromEnumerations(
            new List<string> { "NVIDIA GeForce RTX 5060 Laptop GPU" }, null, null);
        Assert.Single(missing);
    }

    [Fact]
    public void Same_大小写与包含都算同一张卡()
    {
        Assert.True(GpuVisibility.Same("NVIDIA GeForce RTX 5060 Laptop GPU", "nvidia geforce rtx 5060 laptop gpu"));
        Assert.True(GpuVisibility.Same("AMD Radeon(TM) 610M", "AMD Radeon(TM) 610M"));
    }

    [Fact]
    public void Same_太短或空的名字不算同一张卡()
    {
        Assert.False(GpuVisibility.Same("NVIDIA", "NVIDIA GeForce RTX 5060 Laptop GPU"));
        Assert.False(GpuVisibility.Same("", "AMD Radeon(TM) 610M"));
        Assert.False(GpuVisibility.Same(null, null));
    }

    [Fact]
    public void ExplainProblemCode_禁用与驱动异常给出可执行解释()
    {
        Assert.Contains("禁用", GpuVisibility.ExplainProblemCode(22));
        Assert.Contains("启用设备", GpuVisibility.ExplainProblemCode(22));
        Assert.Contains("43", GpuVisibility.ExplainProblemCode(43));
        Assert.Contains("正常", GpuVisibility.ExplainProblemCode(0));
        Assert.Contains("设备管理器", GpuVisibility.ExplainProblemCode(null));
        Assert.Contains("789", GpuVisibility.ExplainProblemCode(789));
    }

    [Fact]
    public void DescribeMissingGpu_包含卡名状态码与逐步处置()
    {
        var text = GpuVisibility.DescribeMissingGpu("NVIDIA GeForce RTX 5060 Laptop GPU", 22);
        Assert.Contains("NVIDIA GeForce RTX 5060 Laptop GPU", text);
        Assert.Contains("设备管理器", text);
        Assert.Contains("GPU 模式", text);
        Assert.Contains("计算设备", text);
        Assert.Contains("重启", text);
        Assert.Contains("插上电源", text);          // 真机线索:用户当时没插电
        Assert.DoesNotContain("设备本身没报错", text);
    }

    [Fact]
    public void DescribeMissingGpu_设备没报错时点明供电与显卡模式()
    {
        var text = GpuVisibility.DescribeMissingGpu("NVIDIA GeForce RTX 5060 Laptop GPU", 0);
        Assert.Contains("设备本身没报错", text);
        Assert.Contains("没插电源", text);
        Assert.Contains("重新检测", text);
    }
}
