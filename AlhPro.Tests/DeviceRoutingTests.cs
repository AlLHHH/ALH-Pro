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
    public void Device_table_present_never_falls_to_cpu_even_without_recommendation()
    {
        // 铁律:超分/补帧绝不落 CPU。表非空(确实有可用设备)时,即便没有推荐值,
        // 也必须取表内一个设备(最小号)而不是 -1 —— 旧实现此处返回 -1(CPU)是漏洞。
        var (id, remapped) = DeviceRouting.ResolveEngineDevice(3, new[] { 0, 2 }, -1, 3);
        Assert.Equal(0, id);
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

    // ===== ResolveEngineDevice(int, (Id,Name)[], deviceCount) —— 选独显、绝不跑核显/CPU =====

    // 真机形状:注册表枚举 [#0 AMD核显 | #1 NVIDIA],但引擎枚举 [#0 NVIDIA | #1 AMD](两者顺序相反)。
    // 设备表 = 引擎枚举:[0= NVIDIA GeForce RTX 5070 Ti, 1= AMD Radeon(TM) Graphics核显]

    [Fact]
    public void Choose_nvidia_engine_id_when_registry_order_is_reversed()
    {
        // 用户设置 GpuIndex=0(引擎枚举里 0=NVIDIA 独显)。注册表序相反(0=AMD)不构成干扰——
        // 只认引擎枚举,必须原样返回 0(NVIDIA),绝不跑核显。
        var (id, remapped) = DeviceRouting.ResolveEngineDevice(0, new[] { (0, "NVIDIA GeForce RTX 5070 Ti"), (1, "AMD Radeon(TM) Graphics") }, 2);
        Assert.Equal(0, id);
        Assert.False(remapped);
    }

    [Fact]
    public void Stale_registry_index_hitting_integrated_gpu_switches_to_discrete()
    {
        // 陈旧/错误的设置值撞号到核显:settingsIndex=1 = AMD 核显(比如旧版用注册表索引写的 1=AMD),
        // 但表里有 NVIDIA 独显(id=0)。必须换成独显 0,并标记重映射 —— 这是"选独显却跑核显"的根治点。
        var (id, remapped) = DeviceRouting.ResolveEngineDevice(1, new[] { (0, "NVIDIA GeForce RTX 5070 Ti"), (1, "AMD Radeon(TM) Graphics") }, 2);
        Assert.Equal(0, id);
        Assert.True(remapped);
    }

    [Fact]
    public void Missing_id_with_discrete_present_never_falls_to_cpu()
    {
        // 设置值不在表里(如旧驱动枚举变了),表里有 NVIDIA 独显 → 取独显,绝不落 -1(CPU)。
        var (id, remapped) = DeviceRouting.ResolveEngineDevice(3, new[] { (0, "NVIDIA GeForce RTX 5070 Ti"), (2, "Intel(R) UHD Graphics") }, 3);
        Assert.Equal(0, id);
        Assert.True(remapped);
    }

    [Fact]
    public void Missing_id_with_only_integrated_uses_iGPU_not_cpu()
    {
        // 表里只有核显(无独显可用)→ 取表内唯一,绝不落 -1(CPU)。
        var (id, _) = DeviceRouting.ResolveEngineDevice(5, new[] { (2, "Intel(R) UHD Graphics"), (3, "AMD Radeon(TM) Graphics") }, 4);
        Assert.True(id >= 0);
        Assert.True(id == 2 || id == 3);
    }

    [Fact]
    public void User_chose_cpu_is_never_overridden_even_with_discrete()
    {
        // 用户主动选 CPU(settingsIndex<0):表里再有独显也尊重,原样 -1。
        var (id, remapped) = DeviceRouting.ResolveEngineDevice(-1, new[] { (0, "NVIDIA GeForce RTX 5070 Ti"), (1, "AMD Radeon(TM) Graphics") }, 2);
        Assert.Equal(-1, id);
        Assert.False(remapped);
    }

    [Fact]
    public void Translation_layer_device_is_skipped_even_if_named_rtx()
    {
        // 转译层设备(名字含 NVIDIA/RTX 但以 "Microsoft Direct3D12" 开头)会被打分当成独显,
        // 但绝不能选它(输出损坏帧)。选真实 NVIDIA 独显(引擎枚举那台)。
        var (id, _) = DeviceRouting.ResolveEngineDevice(1,
            new[] { (0, "Microsoft Direct3D12 (NVIDIA GeForce RTX 5070 Ti)"), (2, "NVIDIA GeForce RTX 5070 Ti") }, 3);
        Assert.Equal(2, id);
    }

    [Theory]
    [InlineData("AMD Radeon(TM) Graphics", true)]
    [InlineData("Radeon(TM) 780M", true)]
    [InlineData("Intel(R) UHD Graphics", true)]
    [InlineData("Intel(R) Iris(R) Xe Graphics", true)]
    [InlineData("NVIDIA GeForce RTX 5070 Ti", false)]
    [InlineData("AMD Radeon RX 7900 XTX", false)]
    [InlineData("Intel(R) Arc(TM) A770", false)]
    public void Integrated_gpu_detection_matches_ui_rules(string name, bool expected)
        => Assert.Equal(expected, GpuName.IsIntegrated(name));

    // ===== IsPlausibleInterpOf —— 补帧探测的第二/三层判据(均值对得上 + 结构没被抹平)=====
    // 背景:探测输入是"水平渐变 + 同一渐变右移 2 像素",正确插值结果由输入自己决定:
    // 均值≈两输入均值、方差≈输入方差(实测 13 个模型 × 新老两代引擎:均值偏差 ≤0.24、方差比 1.00)。
    // 旧判据只有 IsAchromatic,而"输出整帧黑"的三通道极差是 0 —— 颜色完全均衡,旧判据对它无感(盲区)。

    [Theory]
    [InlineData(127, 127, 127, 5424, 126.87, 5424)]   // 真机实测:均值偏差 0.13、方差比 1.00
    [InlineData(126.76, 126.76, 126.76, 5424, 126.87, 5424)]  // 实测最差的一档(偏差 0.24)
    [InlineData(150, 150, 150, 5424, 126.87, 5424)]   // 偏差 23.1 < 容差 24:放行(边界内侧)
    [InlineData(127, 127, 127, 1400, 126.87, 5424)]   // 方差比 0.258 > 0.25:结构还在(边界内侧)
    public void Plausible_interp_output_passes(double r, double g, double b, double var, double expMean, double expVar)
        => Assert.True(DeviceRouting.IsPlausibleInterpOf(r, g, b, var, expMean, expVar));

    [Theory]
    [InlineData(0, 0, 0, 0, 126.87, 5424)]            // 整帧黑:均值差 127、方差 0 —— 旧判据(只看通道均衡)放行
    [InlineData(255, 255, 255, 0, 126.87, 5424)]      // 整帧白
    [InlineData(127, 127, 127, 0, 126.87, 5424)]      // 均值对得上、通道也均衡,但被抹成一块平的 → 靠方差抓
    [InlineData(151, 151, 151, 5424, 126.87, 5424)]   // 偏差 24.13 > 容差
    [InlineData(127, 127, 127, 1300, 126.87, 5424)]   // 方差比 0.24 < 0.25:结构基本被抹平
    [InlineData(132, 4, 4, 5424, 126.87, 5424)]       // 真机损坏帧(整帧红噪点):颜色那一层拦下
    public void Broken_interp_output_is_rejected(double r, double g, double b, double var, double expMean, double expVar)
        => Assert.False(DeviceRouting.IsPlausibleInterpOf(r, g, b, var, expMean, expVar));

    [Fact]
    public void Interp_judgement_is_calibrated_wide_against_measured_values()
    {
        // 容差必须【远宽于】实测偏差(≤0.24),又【远窄于】损坏值(黑/白帧偏差 127)
        Assert.True(DeviceRouting.InterpProbeMeanTolerance >= 0.24 * 20);
        Assert.True(DeviceRouting.InterpProbeMeanTolerance <= 127 / 4);
        // 结构门槛必须【远低于】实测方差比(1.00),又不至于形同虚设
        Assert.True(DeviceRouting.InterpProbeStructureRatio <= 1.00 / 2);
        Assert.True(DeviceRouting.InterpProbeStructureRatio >= 0.1);
    }

    [Fact]
    public void Interp_judgement_still_composes_with_chroma_check()
    {
        // 颜色那一层没被均值/方差吃掉:均值方差都完美、但通道失衡 → 照样判不可用
        Assert.False(DeviceRouting.IsPlausibleInterpOf(127, 4, 4, 5424, 126.87, 5424));
        // 而纯黑/纯白在旧判据下是"通过"的 —— 单测把"这是新增能力、不是换了个写法"钉住
        Assert.True(DeviceRouting.IsAchromatic(0, 0, 0));
        Assert.True(DeviceRouting.IsAchromatic(255, 255, 255));
    }

    [Fact]
    public void Interp_judgement_does_not_use_variance_when_expected_is_unknown()
    {
        // 预期方差拿不到(≤0)时不拿方差判死:拿不准就放过,不误杀
        Assert.True(DeviceRouting.IsPlausibleInterpOf(127, 127, 127, 0, 126.87, 0));
    }
}
