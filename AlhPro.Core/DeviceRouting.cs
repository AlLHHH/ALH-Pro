namespace AlhPro.Core;

/// <summary>
/// 计算设备选择与探测帧健全性的【纯判定】部分。硬件枚举、日志、会话创建都留在 UI 工程,
/// 这里只认数字 —— 因为这两条规则恰恰是拿不到真机就无法验证的那种:
/// 转译层设备只在特定机器上出现,损坏帧只在特定驱动上产生。抽出来才能用单测钉住。
/// </summary>
public static class DeviceRouting
{
    /// <summary>
    /// 把"设置里存的计算设备编号"解析成本次要传给引擎的 -g 编号。
    /// </summary>
    /// <param name="settingsIndex">用户设置里的编号;&lt;0 = 用户主动选了 CPU,必须原样尊重。</param>
    /// <param name="availableIds">引擎自检出的可用设备编号表(已剔除不可用/转译层设备)。可能为空(未枚举)。</param>
    /// <param name="recommendedId">推荐的原生设备编号;&lt;0 = 没有可推荐的。</param>
    /// <param name="deviceCount">设备数量兜底用(仅在 availableIds 为空、即设备表没枚举出来时使用)。</param>
    /// <returns>要传给引擎的编号(-1 = CPU),以及是否发生了重映射(调用方据此留痕)。</returns>
    /// <remarks>
    /// 为什么必须按"编号是否在表里"而不是"编号 &lt; 数量"判有效:引擎 -g 编号可能是稀疏/不从 0 开始的
    /// (注册表 [0 Intel][1 NVIDIA],引擎实际 [1 NVIDIA][2 Intel]),此时编号 2 ≥ 数量 2,
    /// 数量比对会把选中的独显误判成无效并掉成 CPU。
    /// 为什么编号不在表里时【不能】原样照传:引擎内部的 -g 编号不受我们剔除表的影响,
    /// 照传 -g 1 仍会命中已被剔除的 D3D12 转译层设备并输出损坏帧。必须换成推荐的原生设备。
    /// </remarks>
    public static (int Id, bool Remapped) ResolveEngineDevice(
        int settingsIndex, IReadOnlyCollection<int> availableIds, int recommendedId, int deviceCount)
    {
        if (settingsIndex < 0) return (-1, false);
        if (availableIds.Count > 0)
        {
            foreach (var id in availableIds)
                if (id == settingsIndex) return (settingsIndex, false);
            // 表里没有设置里存的编号 → 必须换成表里的原生设备,绝不落 CPU(铁律:超分/补帧不准锁 CPU)。
            // 此时表非空(确实有可用设备),即便没有推荐值也要取表内一个(取最小,确定性)而不是 -1。
            int fallback = recommendedId >= 0 ? recommendedId : availableIds.Min();
            return (fallback, true);
        }
        return (settingsIndex < deviceCount ? settingsIndex : -1, false);
    }

    /// <summary>
    /// 带设备名的新重载:把"设置里存的计算设备编号"解析成本次要传给引擎的 -g 编号。
    /// 相比 <see cref="ResolveEngineDevice(int, IReadOnlyCollection{int}, int, int)"/>,
    /// 这里能直接用设备名判【核显/独显】,做到"选独显、绝不跑核显/绝不落 CPU"。
    /// </summary>
    /// <param name="settingsIndex">用户设置里的编号;&lt;0 = 用户主动选了 CPU,必须原样尊重。</param>
    /// <param name="devices">引擎自检出的可用设备表(编号 + 名字,已剔除不可用/转译层设备)。可能为空(未枚举)。</param>
    /// <param name="deviceCount">设备数量兜底用(仅在 devices 为空、即设备表没枚举出来时使用)。</param>
    /// <returns>要传给引擎的编号(-1 = CPU),以及是否发生了重映射(调用方据此留痕)。</returns>
    /// <remarks>
    /// 规则(铁律:超分/补帧不准锁 CPU,也不准落到核显):
    /// 1) settingsIndex &lt; 0 → 用户主动选 CPU,原样尊重(-1)。
    /// 2) 设备表非空:
    ///    a. settingsIndex 在表里 → 它就是用户选的那块卡,原样返回(非重映射);
    ///       ⚠ 但若它恰好是【核显】且表里另有独显,说明设置存的是陈旧的注册表索引/错号,
    ///       换成最佳独显并标记重映射 —— 这正是"选独显却跑核显"的根治点。
    ///    b. 不在表里 → 必须是表里的一块(绝不落 CPU):取【最佳独显】(Score 最高、非核显、
    ///       剔除转译层);表里只有核显时才用核显兜底。
    /// 3) 设备表为空 → 数量兜底(settingsIndex &lt; deviceCount ? settingsIndex : -1),仅此一路允许 -1。
    /// </remarks>
    public static (int Id, bool Remapped) ResolveEngineDevice(
        int settingsIndex, IReadOnlyList<(int Id, string Name)> devices, int deviceCount)
    {
        if (settingsIndex < 0) return (-1, false);
        if (devices.Count > 0)
        {
            // 用户选的编号在表里:还得确认它不是核显(陈旧注册表索引可能撞号到核显)
            foreach (var d in devices)
                if (d.Id == settingsIndex)
                {
                    if (!GpuName.IsIntegrated(d.Name))
                        return (settingsIndex, false);
                    // 选中的是核显:表里若有独显则换过去,否则保留核显(别无选择)
                    var bestDiscrete = BestDiscrete(devices);
                    if (bestDiscrete >= 0) return (bestDiscrete, true);
                    return (settingsIndex, false);
                }
            // 不在表里 → 取表内最佳独显,绝不落 CPU
            var best = BestDiscrete(devices);
            return (best >= 0 ? best : devices[0].Id, true);
        }
        return (settingsIndex < deviceCount ? settingsIndex : -1, false);
    }

    /// <summary>表内得分最高的【独显】(Score 最高、非核显、非转译层);无独显返回 -1。</summary>
    private static int BestDiscrete(IReadOnlyList<(int Id, string Name)> devices)
    {
        int bestId = -1, bestScore = -1;
        foreach (var d in devices)
        {
            if (GpuName.IsD3D12Translation(d.Name)) continue;   // 转译层:宁缺毋滥(输出损坏帧)
            int s = GpuName.Score(d.Name);
            if (s > bestScore) { bestScore = s; bestId = d.Id; }
        }
        return bestId;
    }

    /// <summary>
    /// 三通道均值极差容差。定标依据(真机实测):正常灰帧 ≤3、好卡实测 0.00,
    /// 损坏帧 24~30。取 12 = 两侧都留出数倍余量,不靠边界吃饭。
    /// </summary>
    public const double AchromaticSpreadTolerance = 12;

    /// <summary>
    /// 探测输出是否为【无彩色】灰阶。RIFE 探测的输入是纯黑 + 纯白两帧,
    /// 任何正常引擎插出的中间帧都不该带色;带色即说明 GPU 计算或读回环节坏了。
    /// 引擎退化成直接输出黑/白端点帧时极差 = 0,不会被误杀。
    /// </summary>
    public static bool IsAchromatic(double meanR, double meanG, double meanB)
    {
        double max = Math.Max(meanR, Math.Max(meanG, meanB));
        double min = Math.Min(meanR, Math.Min(meanG, meanB));
        return max - min <= AchromaticSpreadTolerance;
    }
}
