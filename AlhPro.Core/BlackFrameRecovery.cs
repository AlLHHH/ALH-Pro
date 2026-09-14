namespace AlhPro.Core;

/// <summary>补帧输出【检出黑帧之后的处置策略】(F1,纯逻辑、可单测)。
///
/// 【要修的事】真机诊断:补帧的「补回(还原源时间轴)」这一步检出黑帧时,代码只打了
/// 「⚠ 补回层批输出含黑帧(GPU 队列异常)— 建议更新显卡驱动/换卡后重试」就继续往下走,
/// 于是黑帧当成功产物交付(任务状态"成功 1,失败 0",成片里 3 段全黑)。层批原语只有 ncnn 一条路,
/// 它坏了就没有第二条路 —— 所以"只提示"等于"放行"。
///
/// 【为什么不能像超分那样"回退源帧"】超分的回退是把源帧缩放后顶替输出,帧号/时间轴不变,语义成立;
/// 而补帧这一步的任务就是"把缺失的时间轴位置生成出来",把源帧顶上去会让输出帧数与时间轴错乱
/// (用户口径:补帧的回退绝不能改帧数/时间轴)。所以这里要的是【换引擎/换卡,按同一张槽表重算】。
///
/// 【换路顺序(与分段补帧路径的降级链同思路:ncnn → ONNX → 换卡 → 绝不落 CPU)】
///   ① <see cref="Step.OnnxSlots"/>:ONNX(rife49.onnx,任意时间步)按【同一张逐槽排程】重算插值帧 ——
///      引擎换成 DirectML,槽位/输出帧数/时间轴一字不改。
///   ② <see cref="Step.AltGpuNcnnSlots"/>:换另一块显卡,用 ncnn 单对 -s 逐槽重算
///      (单对模式是 ncnn 唯一可靠的"任意时间步"原语;目录模式会忽略 -s)。
///   ③ 都不行 → <see cref="FailureMessage"/>:抛出可读异常让【任务失败】,绝不放黑帧进成片。
///
/// 【绝不落 CPU】补帧 CPU 慢到用户以为卡死,是本产品的硬约定,故这里没有 CPU 档。</summary>
public static class BlackFrameRecovery
{
    /// <summary>换路档位。</summary>
    public enum Step
    {
        /// <summary>ONNX(DirectML)按同一张槽表逐槽重算。</summary>
        OnnxSlots,
        /// <summary>换另一块显卡,ncnn 单对 -s 逐槽重算。</summary>
        AltGpuNcnnSlots,
    }

    /// <summary>本次可用的换路计划(按优先级)。
    /// <paramref name="onnxAvailable"/> = ONNX 补帧模型在且能解析出可用的 DirectML 设备;
    /// <paramref name="altGpuAvailable"/> = 本机还有另一块显卡可换;
    /// <paramref name="arbitraryTimestepModel"/> = 当前补帧模型是 v4 架构(单对 -s 才被引擎采纳,
    /// v2 系模型加 -s 会被忽略 → 拿同样的帧重跑一遍毫无意义,故直接不入计划)。</summary>
    public static Step[] Plan(bool onnxAvailable, bool altGpuAvailable, bool arbitraryTimestepModel)
    {
        var steps = new System.Collections.Generic.List<Step>(2);
        if (onnxAvailable) steps.Add(Step.OnnxSlots);
        if (altGpuAvailable && arbitraryTimestepModel) steps.Add(Step.AltGpuNcnnSlots);
        return steps.ToArray();
    }

    /// <summary>一行日志/界面文案用的档位名。</summary>
    public static string StepName(Step s) => s switch
    {
        Step.OnnxSlots => "ONNX(DirectML)按同一槽表逐槽重算",
        _ => "换另一块显卡(ncnn 单对 -s)逐槽重算",
    };

    /// <summary>这一帧到底算不算"真缺陷"。判据 =【输出近黑】且【两个源端点不都是近黑】。
    /// 【为什么要求"两个端点都不近黑"】插值帧没有逐帧对应的源帧(超分那边可以逐帧比对),所以只能看它夹在哪对帧之间:
    /// 两端都近黑 ⇒ 这一对本来就落在纯黑区间(片头黑场/淡入淡出/夜戏),输出近黑是内容而不是故障,误判会让
    /// 用户的任务白失败一次;只要有一端不是黑场,而输出整帧近黑,就不是插值能产生的画面(GPU 队列异常的真机形态)。
    /// 【宁可多判一点】判漏的代价是黑帧进成片(正是本次要修的事),判重的代价只是一次换路重算。
    /// 两端都近黑才豁免,是这两者之间最保守的取法。</summary>
    public static bool IsRealDefect(bool outputNearBlack, bool sourceANearBlack, bool sourceBNearBlack)
        => outputNearBlack && !(sourceANearBlack && sourceBNearBlack);

    /// <summary>换路全失败时的可读失败文案(抛 InvalidOperationException 的正文)。
    /// 必须说清三件事:发生了什么(黑帧)、已经试过什么(换路清单)、用户能做什么。</summary>
    public static string FailureMessage(string stage, int slotCount, int blackAfterReroute,
        bool triedOnnx, bool triedAltGpu)
    {
        string tried = triedOnnx && triedAltGpu ? "ONNX(DirectML)重算与换卡重算都试过了"
            : triedOnnx ? "ONNX(DirectML)重算试过了(本机没有第二块显卡可换)"
            : triedAltGpu ? "换卡重算试过了(本机没有可用的 ONNX 补帧引擎)"
            : "本机既没有可用的 ONNX 补帧引擎,也没有第二块显卡可换";
        return $"补帧「{stage}」输出含黑帧,且换路重算后仍然是黑帧:{slotCount} 个插值槽里检出 {blackAfterReroute} 帧黑帧"
            + $"(这两个源帧都不是黑场,不可能是素材内容)。{tried}。"
            + "为避免把黑帧写进成片,本次任务按失败收尾(不会生成含黑场的成片)。"
            + "建议:更新显卡驱动后重试;或先在「计算设备」里换一块显卡/关闭兼容模式再试;"
            + "若素材本身有大段纯黑画面(片头黑场/夜戏),可改选「不补帧」后重试。";
    }
}
