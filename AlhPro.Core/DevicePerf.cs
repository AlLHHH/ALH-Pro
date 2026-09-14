namespace AlhPro.Core;

/// <summary>设备综合性能档【任务 T】。三档(不是分数):用来把"每批帧数"从"只看空闲内存"升级为
/// "内存 + 实测吞吐"双输入。【依据来源全部是仓库里已有的数据,不新造昂贵探测】</summary>
public enum PerfScore
{
    /// <summary>慢:同预算下应取最保守的每批帧数(用户下界 50)。</summary>
    Slow = 0,
    /// <summary>正常:中间档(300 帧/批)。</summary>
    Normal = 1,
    /// <summary>快:允许档位上限(350 / 长片 700 帧/批)。</summary>
    Fast = 2,
}

/// <summary>设备性能归一(纯函数,可单测)【任务 T 要求 1】。
/// 【输入(全部取仓库现有可得数据,零新探测)】
///   · 空闲内存 `SafeRender.FreeRamGB`(GlobalMemoryStatusEx 实测,跨厂商可靠)—— 既有档位;
///   · 实测吞吐 `PerfMemory.PerFrameFor(...)`(**优先用**)—— 单位"秒/帧 @1080p",是"处理阶段"口径
///     (不含编码/封装,见 PerfMemory 的 SchemaVersion 注释);
///   · CPU 逻辑核数 `Environment.ProcessorCount`(仓库既有的 `SafeRender.CpuCoreCount`);
///   · 显存 `SafeRender.TotalVramGB` —— **只在真测到时才参与**(nvidia-smi;AMD/Intel 测不到,
///     见 SafeRender.FreeVramMeasured 的说明)。注意:`VulkanCheck.Devices` 只有 (Id, Name),
///     **不暴露显存**,所以"Vulkan 自检里的显存"在本仓库并不存在,只能走 SafeRender 那条路。
/// 【规则(自上而下)】
///   ① 有实测吞吐 → 按吞吐分档(阈值见下,依据见常量注释);
///   ② 没有实测吞吐 → **回退纯内存档**(任务 T 明确要求):Weak↔Slow、Normal↔Normal、Strong↔Fast
///      (即"内存够大"在该档里就按"快"处理,这是既有的"设备好"口径,不额外造数据);
///   ③ 单向收紧(只在①成立时叠加,避免"没有实测数据"时把纯内存档搅浑):
///      · 逻辑核数 < CpuCoresForFullSpeed → 降一档(2~4 核机器上大批只会把每批的启动/解码开销堆起来);
///      · 显存**已实测**且 < MinVramGBForFullSpeed → 降一档(显存决定引擎分块,分块小则每批吞吐差);
///      · Fast 还要求空闲内存 ≥ `RenderPolicy.StrongDeviceFreeRamGB`(8G):任务 T 明确"仅 Fast 档且内存 ≥8G
///        才给 700" —— 内存不够时**不许**因为"测得快"就开最大批。
/// 【全部阈值标【待实测标定】:需要真机在不同机型上给出"实测秒/帧 vs 最优每批帧数"的对照。】</summary>
public static class DevicePerf
{
    /// <summary>判为"快"的实测吞吐门槛(秒/帧 @1080p)。【依据】仓库既有真机数字:
    /// ncnn-Vulkan 超分实测 0.24~0.6 秒/帧(EngineService 探测注释)、补帧 0.10~0.35 秒/输出帧;
    /// 取 0.30 作为"这条机器跑得动大批"的下限。【待实测标定】</summary>
    public const double FastSecondsPerFrame1080p = 0.30;

    /// <summary>判为"正常"的实测吞吐门槛(秒/帧 @1080p);超过它算"慢"。【依据】同一批真机数字的上沿 0.6,
    /// 再留一倍余量到 1.00;而"ONNX 落 CPU"实测 8 秒/帧 —— 那种机器无论如何都不该给大批。【待实测标定】</summary>
    public const double NormalSecondsPerFrame1080p = 1.00;

    /// <summary>能拿满吞吐的最少逻辑核数。【依据】仓库既有线程上限取 `ProcessorCount-2` 再钳到 ≥2
    /// (VideoService 的帧并行),4 核以下并行度明显不够。【待实测标定】</summary>
    public const int CpuCoresForFullSpeed = 4;

    /// <summary>能拿满吞吐的最少**已实测**显存(GB)。【依据】仓库既有 `RenderPolicy.OnnxTileSize` 的
    /// 最低档是 &lt;3.5G→512 分块(分块小 = 每批吞吐差)。只在显存真测到时才参与(AMD/Intel 测不到)。【待实测标定】</summary>
    public const double MinVramGBForFullSpeed = 4.0;

    /// <summary>没有实测吞吐时的纯内存档映射(任务 T 要求 1 的"回退")。</summary>
    public static PerfScore FromRam(double freeRamGB) => RenderPolicy.TierFor(freeRamGB) switch
    {
        RenderPolicy.DeviceTier.Strong => PerfScore.Fast,
        RenderPolicy.DeviceTier.Normal => PerfScore.Normal,
        _ => PerfScore.Slow,
    };

    private static PerfScore Lower(PerfScore s) => s == PerfScore.Fast ? PerfScore.Normal
        : s == PerfScore.Normal ? PerfScore.Slow : PerfScore.Slow;

    /// <summary>归一为性能档,并给出**可审计的依据串**(进日志:"性能档位(依据:…)")。</summary>
    public static PerfScore Score(double freeRamGB, int cpuCores, double? measuredSecondsPerFrame1080p,
        out string reason, double? vramGB = null, bool vramMeasured = false)
    {
        bool measured = measuredSecondsPerFrame1080p is { } m && m > 0.001 && double.IsFinite(m);
        PerfScore score;
        var sb = new System.Text.StringBuilder();
        if (measured)
        {
            double mps = measuredSecondsPerFrame1080p!.Value;
            // 【任务 V】两条阈值可被在线参数覆盖(未配置时回落既有常量 → 行为逐字不变)
            double fastThr = ParamProfileRuntime.PerfFastSecondsPerFrame;
            double normThr = ParamProfileRuntime.PerfNormalSecondsPerFrame;
            score = mps <= fastThr ? PerfScore.Fast
                : mps <= normThr ? PerfScore.Normal : PerfScore.Slow;
            sb.Append($"实测吞吐 {mps:0.###} 秒/帧@1080p(阈值 ≤{fastThr:0.##} 快 / ≤{normThr:0.##} 正常)");
        }
        else
        {
            score = FromRam(freeRamGB);
            sb.Append($"无实测吞吐 → 回退纯内存档(空闲 {freeRamGB:0.#}G)");
        }
        int before = (int)score;
        if (measured)
        {
            if (cpuCores > 0 && cpuCores < CpuCoresForFullSpeed && score != PerfScore.Slow)
            {
                score = Lower(score);
                sb.Append($";核数 {cpuCores} < {CpuCoresForFullSpeed} → 降一档");
            }
            else sb.Append($";核数 {cpuCores}");

            if (vramMeasured && vramGB is { } v && v > 0 && v < MinVramGBForFullSpeed && score != PerfScore.Slow)
            {
                score = Lower(score);
                sb.Append($";实测显存 {v:0.#}G < {MinVramGBForFullSpeed:0.#}G → 降一档");
            }
            else sb.Append(vramMeasured ? $";实测显存 {vramGB:0.#}G" : ";显存未实测(仅 NVIDIA 可测,不参与判定)");

            if (score == PerfScore.Fast && freeRamGB < RenderPolicy.StrongDeviceFreeRamGB)
            {
                score = PerfScore.Normal;
                sb.Append($";空闲内存 {freeRamGB:0.#}G < {RenderPolicy.StrongDeviceFreeRamGB:0.#}G → 不给 Fast(700 帧/批要求内存 ≥8G)");
            }
        }
        sb.Append($" → 性能档 {score}({(int)score}/{before})");
        reason = sb.ToString();
        return score;
    }
}
