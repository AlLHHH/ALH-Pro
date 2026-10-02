using System.Collections.Generic;
using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>
/// DirectML 设备兜底路由的单元测试。
/// 用例全部来自 2026-10-02 真机诊断包(ALHPro_Diag_20261002_2020)里的真实形状:
/// 注册表枚举[#0 NVIDIA RTX 5060 | #1 AMD Radeon(TM) 610M],而当天引擎/DXGI 枚举里只剩 AMD,
/// DXGI 里还多出一条 Microsoft Basic Render Driver(软件适配器)——旧代码正是把设置里存的编号 1
/// 原样当 DirectML 设备号,落到了这条软件适配器上,建会话报 C0262002 后整机退 CPU。
/// </summary>
public class DmlDeviceRoutingTests
{
    private static List<DxgiAdapterInfo> RealMachineShapes() => new()
    {
        new DxgiAdapterInfo(0, "AMD Radeon(TM) 610M", 536870912L, false),           // 核显:专用显存 0.5 GiB
        new DxgiAdapterInfo(1, "Microsoft Basic Render Driver", 0L, true),          // 软件适配器:必须永不作为结果
    };

    [Fact]
    public void HardwareOnly_必须剔除软件适配器_并保持原始序()
    {
        var hw = DmlDeviceRouting.HardwareOnly(RealMachineShapes());
        Assert.Single(hw);
        Assert.Equal(0, hw[0].Index);
        Assert.Equal("AMD Radeon(TM) 610M", hw[0].Name);
        Assert.DoesNotContain(hw, a => a.Name.Contains("Basic Render"));
    }

    [Fact]
    public void HardwareOnly_空名与null表都安全()
    {
        Assert.Empty(DmlDeviceRouting.HardwareOnly(null));
        Assert.Empty(DmlDeviceRouting.HardwareOnly(new List<DxgiAdapterInfo>
        {
            new DxgiAdapterInfo(0, "", 0L, false),
            new DxgiAdapterInfo(1, "   ", 0L, false),
        }));
    }

    [Fact]
    public void PickFallback_10月2日真机形状_绝不返回软件适配器()
    {
        var pick = DmlDeviceRouting.PickFallback(RealMachineShapes());
        Assert.Equal(0, pick.Index);              // 0 = AMD 硬件核显;1 = 软件适配器,绝不能选中
        Assert.True(pick.Degraded);               // 没有独显级硬件卡 → 明确标记为降级
        Assert.Contains("DXGI#0", pick.Reason);
    }

    [Fact]
    public void PickFallback_有独显级硬件卡时选显存最大的且不算降级()
    {
        var pick = DmlDeviceRouting.PickFallback(new List<DxgiAdapterInfo>
        {
            new DxgiAdapterInfo(0, "Intel(R) UHD Graphics", 134217728L, false),
            new DxgiAdapterInfo(1, "NVIDIA GeForce RTX 5060 Laptop GPU", 8589934592L, false),
            new DxgiAdapterInfo(2, "Microsoft Basic Render Driver", 0L, true),
        });
        Assert.Equal(1, pick.Index);
        Assert.False(pick.Degraded);
        Assert.Contains("RTX 5060", pick.Reason);
    }

    [Fact]
    public void PickFallback_只有软件适配器时返回负一()
    {
        var pick = DmlDeviceRouting.PickFallback(new List<DxgiAdapterInfo>
        {
            new DxgiAdapterInfo(0, "Microsoft Basic Render Driver", 0L, true),
        });
        Assert.Equal(-1, pick.Index);
        Assert.Contains("没有硬件适配器", pick.Reason);
    }

    [Fact]
    public void ConfirmOrFallback_首选指向软件适配器时降级并说明原因()
    {
        // 这正是 2026-10-02 的路径:设置里存着编号 1,DXGI#1 是软件适配器
        var pick = DmlDeviceRouting.ConfirmOrFallback(1, RealMachineShapes());
        Assert.Equal(0, pick.Index);
        Assert.True(pick.Degraded);
        Assert.Contains("软件适配器", pick.Reason);
    }

    [Fact]
    public void ConfirmOrFallback_首选是硬件卡时直接用它()
    {
        var pick = DmlDeviceRouting.ConfirmOrFallback(0, RealMachineShapes());
        Assert.Equal(0, pick.Index);
        Assert.False(pick.Degraded);
        Assert.Contains("是硬件适配器", pick.Reason);
    }

    [Fact]
    public void ConfirmOrFallback_首选编号不存在时降级()
    {
        // 旧编号 3 在任何枚举里都不存在(引擎表为空时旧代码会原样透传这种编号)
        var pick = DmlDeviceRouting.ConfirmOrFallback(3, RealMachineShapes());
        Assert.Equal(0, pick.Index);
        Assert.True(pick.Degraded);
        Assert.Contains("不存在", pick.Reason);
    }

    [Fact]
    public void ConfirmOrFallback_负数首选按兜底处理()
    {
        var pick = DmlDeviceRouting.ConfirmOrFallback(-1, RealMachineShapes());
        Assert.Equal(0, pick.Index);
    }

    [Fact]
    public void DiscreteOnly_只保留大于1GiB的卡并按显存降序()
    {
        var list = DmlDeviceRouting.DiscreteOnly(new List<DxgiAdapterInfo>
        {
            new DxgiAdapterInfo(0, "AMD Radeon(TM) 610M", 536870912L, false),        // 0.5 GiB → 排除
            new DxgiAdapterInfo(1, "NVIDIA GeForce RTX 5060 Laptop GPU", 8589934592L, false),
            new DxgiAdapterInfo(2, "NVIDIA GeForce RTX 4070 Laptop GPU", 4294967296L, false),
        });
        Assert.Equal(2, list.Count);
        Assert.Equal(1, list[0].Index);   // 8 GiB 在前
        Assert.Equal(2, list[1].Index);
    }

    [Fact]
    public void IsHardware_软件适配器与空名都算不可用()
    {
        Assert.True(new DxgiAdapterInfo(0, "AMD Radeon(TM) 610M", 0L, false).IsHardware);
        Assert.False(new DxgiAdapterInfo(1, "Microsoft Basic Render Driver", 0L, true).IsHardware);
        Assert.False(new DxgiAdapterInfo(2, "", 0L, false).IsHardware);
    }
}
