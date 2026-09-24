using System;
using System.Collections.Generic;
using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【跨厂商显存读数 · 2026-09-24】性能计数器「GPU Adapter Memory」的换算与 LUID 解析契约。
///
/// 背景:此前"当前空闲显存"只有 NVIDIA 能真读(nvidia-smi);AMD/Intel 上只能报"未实测",
/// 于是显存墙与并发档位在 A 卡/核显上整层失效。现在用 Windows 自带的性能计数器补上这条路径
/// (P/Invoke 在 UI 层,换算与解析在这里 ⇒ 可单测)。
///
/// 这里钉的是**口径**:① 实例名 LUID 解析;② 按 LUID 过滤 / 不过滤(多卡)两种求和;
/// ③ 空闲 = 总量 − 已用并钳到 [0, 总量](计数器与 DXGI 口径不同,必须夹住);
/// ④ 脏值不参与求和、总量未知时返回 null(保持"未实测",绝不拿估值冒充实测)。
/// **诚实边界**:契约只证明换算与解析;真机上"这个数字确实来自 PDH"由部署后带
/// <c>ALH_FORCE_PDH_VRAM=1</c> 跑一次、看日志里的读数来源确认(见交接说明)。</summary>
public class GpuMemCountersTests
{
    [Fact]
    public void It_parses_the_luid_from_a_pdh_instance_name()
    {
        Assert.Equal((0u, 0xA1B2u), GpuMemCounters.ParseInstanceLuid("luid_0x00000000_0x0000A1B2_phys_0"));
        Assert.Equal((0u, 0xA1B2u), GpuMemCounters.ParseInstanceLuid("luid_0x00000000_0x0000a1b2_phys_1"));
        Assert.Equal((0x12345678u, 0x9ABCDEF0u),
            GpuMemCounters.ParseInstanceLuid("luid_0x12345678_0x9ABCDEF0_phys_0"));
        // 认不出来的一律 null(调用方按"认不出归属"处理,不猜)
        Assert.Null(GpuMemCounters.ParseInstanceLuid("GPU Engine"));
        Assert.Null(GpuMemCounters.ParseInstanceLuid("luid_0xZZZZ_0x0000_phys_0"));
        Assert.Null(GpuMemCounters.ParseInstanceLuid(""));
        Assert.Null(GpuMemCounters.ParseInstanceLuid(null));
    }

    [Fact]
    public void It_sums_all_instances_when_no_luid_filter_is_given()
    {
        var inst = new List<(string, double)>
        {
            ("luid_0x00000000_0x0000A1B2_phys_0", 2L * 1073741824),   // 2 GB
            ("luid_0x00000000_0x0000C3D4_phys_0", 1L * 1073741824),   // 1 GB
        };
        Assert.Equal(3L * 1073741824, GpuMemCounters.SumDedicatedBytes(inst));
        // 只算指定 LUID(多卡机只认目标卡)
        Assert.Equal(2L * 1073741824, GpuMemCounters.SumDedicatedBytes(inst, (0u, 0xA1B2u)));
        Assert.Equal(0, GpuMemCounters.SumDedicatedBytes(inst, (0u, 0xFFFFu)));
        Assert.Equal(0, GpuMemCounters.SumDedicatedBytes(null));
    }

    [Fact]
    public void Dirty_readings_do_not_join_the_sum()
    {
        var inst = new List<(string, double)>
        {
            ("luid_0x00000000_0x0000A1B2_phys_0", 1073741824),
            ("luid_0x00000000_0x0000C3D4_phys_0", double.NaN),   // 计数器偶发脏值
            ("luid_0x00000000_0x0000E5F6_phys_0", -5),           // 负值
            ("", 0),
        };
        Assert.Equal(1073741824, GpuMemCounters.SumDedicatedBytes(inst));
    }

    [Fact]
    public void Free_memory_is_total_minus_used_and_clamped()
    {
        // 8G 总量、已用 2G ⇒ 空闲 6G
        Assert.Equal(6.0, GpuMemCounters.FreeVramGB(8, 2L * 1073741824)!.Value, 3);
        // 已用 > 总量(计数器口径与 DXGI 不一致时会出现)⇒ 夹到 0,不许出现负数
        Assert.Equal(0.0, GpuMemCounters.FreeVramGB(8, 99L * 1073741824)!.Value, 3);
        // 已用为 0 / 负数 ⇒ 空闲 = 总量(不许超过总量,否则上层会拿不可能的数去放大批次)
        Assert.Equal(8.0, GpuMemCounters.FreeVramGB(8, 0)!.Value, 3);
        Assert.Equal(8.0, GpuMemCounters.FreeVramGB(8, -1)!.Value, 3);
        // 总量未知 ⇒ null(调用方保持"未实测")
        Assert.Null(GpuMemCounters.FreeVramGB(0, 1073741824));
        Assert.Null(GpuMemCounters.FreeVramGB(-1, 1073741824));
        Assert.Null(GpuMemCounters.FreeVramGB(double.NaN, 1073741824));
    }
}
