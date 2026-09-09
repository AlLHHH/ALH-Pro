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
            return (recommendedId >= 0 ? recommendedId : -1, true);
        }
        return (settingsIndex < deviceCount ? settingsIndex : -1, false);
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
