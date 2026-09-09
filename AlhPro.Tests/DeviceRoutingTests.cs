using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>
/// 这两组判定的共同点:真机才有触发条件(转译层设备只出现在特定机器,损坏帧只出现在特定驱动),
/// 本机 RTX 4060 复现不出来。单测是唯一能在发布前钉住它们的手段。
/// </summary>
public class DeviceRoutingTests
{
    // ===== ResolveEngineDevice =====

    [Fact]
    public void Negative_setting_means_user_chose_cpu_and_is_never_overridden()
    {
        // 用户主动选 CPU:哪怕设备表里有卡、哪怕有推荐值,也必须原样返回 -1
        var (id, remapped) = DeviceRouting.ResolveEngineDevice(-1, new[] { 0, 1 }, 0, 2);
        Assert.Equal(-1, id);
        Assert.False(remapped);
    }

    [Fact]
    public void Sparse_engine_ids_are_valid_by_membership_not_by_count()
    {
        // 回归点:注册表 [0 Intel][1 NVIDIA],引擎实际编号 [1 NVIDIA][2 Intel]。
        // 用户选了编号 2,设备数量也是 2 —— 按"编号<数量"会误判无效掉成 CPU,
        // 按"编号是否在表里"才正确。这正是当初改成成员判定的原因。
        var (id, remapped) = DeviceRouting.ResolveEngineDevice(2, new[] { 1, 2 }, 1, 2);
        Assert.Equal(2, id);
        Assert.False(remapped);
    }

    [Fact]
    public void Stale_setting_pointing_at_a_removed_translation_device_self_heals()
    {
        // 真机形状:D3D12 转译层设备(#1)已从表里剔除,剩 [0 原生NVIDIA, 2 Intel];
        // 旧设置里还存着 GpuIndex=1。绝不能把 1 照传给引擎(引擎内部编号不受我们剔除影响,
        // -g 1 照样命中转译设备并输出损坏帧),必须换成推荐的原生设备并标记重映射让上层留痕。
        var (id, remapped) = DeviceRouting.ResolveEngineDevice(1, new[] { 0, 2 }, 0, 3);
        Assert.Equal(0, id);
        Assert.True(remapped);
    }

    [Fact]
    public void Missing_setting_with_nothing_to_recommend_falls_back_to_cpu()
    {
        var (id, remapped) = DeviceRouting.ResolveEngineDevice(3, new[] { 0, 2 }, -1, 3);
        Assert.Equal(-1, id);
        Assert.True(remapped);
    }

    [Fact]
    public void Empty_device_table_uses_the_count_fallback()
    {
        // 设备表没枚举出来(VulkanCheck 还没跑/跑失败)时的兜底路径,行为必须与改抽 Core 之前一致
        Assert.Equal(1, DeviceRouting.ResolveEngineDevice(1, System.Array.Empty<int>(), -1, 2).Id);
        Assert.Equal(-1, DeviceRouting.ResolveEngineDevice(2, System.Array.Empty<int>(), -1, 2).Id);
        Assert.False(DeviceRouting.ResolveEngineDevice(1, System.Array.Empty<int>(), -1, 2).Remapped);
    }

    [Fact]
    public void Empty_table_still_never_invents_a_device_when_recommendation_exists()
    {
        // 表为空时不采用推荐值(推荐值本身就来自那张表,表没枚举出来时它也不可信)
        var (id, _) = DeviceRouting.ResolveEngineDevice(5, System.Array.Empty<int>(), 0, 2);
        Assert.Equal(-1, id);
    }

    // ===== IsAchromatic =====

    [Theory]
    [InlineData(0, 0, 0)]        // 纯黑:引擎退化成直接输出端点帧,极差 0,绝不能误杀
    [InlineData(255, 255, 255)]  // 纯白:同上
    [InlineData(128, 128, 128)]  // 黑→白的理想中间帧
    [InlineData(127.5, 128, 128.5)]  // 好卡实测极差 0.00~0.5 一档
    [InlineData(130, 128, 127)]  // 正常灰帧带轻微噪声,极差 3
    public void Achromatic_probe_output_passes(double r, double g, double b)
        => Assert.True(DeviceRouting.IsAchromatic(r, g, b));

    [Theory]
    [InlineData(132, 4, 4)]      // 真机损坏帧:G/B 被压到 ~4,R 保留 → 整帧红噪点
    [InlineData(30, 4, 4)]       // 真机损坏帧实测极差 24~30 区间的低端
    [InlineData(4, 132, 4)]      // 通道顺序无关:换成绿噪点同样要判死
    [InlineData(4, 4, 132)]      // 蓝噪点
    [InlineData(128, 128, 140.01)]   // 刚好越过容差(极差 12.01)
    public void Corrupted_probe_output_is_rejected(double r, double g, double b)
        => Assert.False(DeviceRouting.IsAchromatic(r, g, b));

    [Fact]
    public void Tolerance_boundary_is_inclusive_and_calibrated_wide()
    {
        // 阈值 12 落在"正常 ≤3"与"损坏 24~30"之间,两侧都有数倍余量 —— 不靠边界吃饭
        Assert.True(DeviceRouting.IsAchromatic(128, 128, 140));    // 极差 12 = 容差本身,放行
        Assert.False(DeviceRouting.IsAchromatic(128, 128, 140.5)); // 极差 12.5,判死
        Assert.True(DeviceRouting.AchromaticSpreadTolerance >= 3 * 3);   // ≥ 正常值上界的 3 倍
        Assert.True(DeviceRouting.AchromaticSpreadTolerance <= 24 / 2);  // ≤ 损坏值下界的一半
    }
}
