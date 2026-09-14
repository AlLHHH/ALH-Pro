using AlhPro.Core;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>补帧黑帧【换路策略】的单测(F1)。
/// 这是"黑帧绝不允许穿到成片"的判定核心:换路档位怎么排、什么算真缺陷(不误杀素材本身的纯黑画面)、
/// 全部换路失败时的失败文案必须说清"试过什么/用户能做什么"。</summary>
public class BlackFrameRecoveryTests
{
    [Fact]
    public void Plan_onnx_first_then_alt_gpu()
    {
        var plan = BlackFrameRecovery.Plan(onnxAvailable: true, altGpuAvailable: true, arbitraryTimestepModel: true);
        Assert.Equal(new[] { BlackFrameRecovery.Step.OnnxSlots, BlackFrameRecovery.Step.AltGpuNcnnSlots }, plan);
    }

    [Fact]
    public void Plan_skips_alt_gpu_when_model_cannot_take_timestep()
    {
        // v2 系模型加 -s 会被引擎忽略 = 拿同样的帧白跑一遍,故不入计划(只留 ONNX)
        var plan = BlackFrameRecovery.Plan(true, altGpuAvailable: true, arbitraryTimestepModel: false);
        Assert.Equal(new[] { BlackFrameRecovery.Step.OnnxSlots }, plan);
    }

    [Fact]
    public void Plan_alt_gpu_only_when_onnx_missing()
    {
        var plan = BlackFrameRecovery.Plan(onnxAvailable: false, altGpuAvailable: true, arbitraryTimestepModel: true);
        Assert.Equal(new[] { BlackFrameRecovery.Step.AltGpuNcnnSlots }, plan);
    }

    [Fact]
    public void Plan_empty_when_nothing_available_means_task_must_fail()
    {
        Assert.Empty(BlackFrameRecovery.Plan(false, false, true));
        Assert.Empty(BlackFrameRecovery.Plan(false, false, false));
        // 有卡但模型不支持任意时间步 = 换卡也没用 → 同样是"无路可走"
        Assert.Empty(BlackFrameRecovery.Plan(false, altGpuAvailable: true, arbitraryTimestepModel: false));
    }

    [Fact]
    public void IsRealDefect_black_output_with_bright_sources_is_a_defect()
    {
        // 两端源帧都不是黑场,而插值帧整帧近黑 → 不是插值能产生的画面(GPU 队列异常的真机形态)
        Assert.True(BlackFrameRecovery.IsRealDefect(true, false, false));
    }

    [Fact]
    public void IsRealDefect_a_single_near_black_neighbour_exempts_as_content()
    {
        // 【口径】任一端源帧本来就近黑 = 素材内容(片头黑场/淡入淡出/夜戏/闪黑),放行。
        // 依据:判重的代价是"换路重算出来还是近黑 → 复查仍判缺陷 → 整条任务失败"，
        // 而"一端黑一端亮"的帧对几乎只出现在硬切黑场/淡出处(那里插值本就无意义)。
        Assert.False(BlackFrameRecovery.IsRealDefect(true, true, false));
        Assert.False(BlackFrameRecovery.IsRealDefect(true, false, true));
        Assert.False(BlackFrameRecovery.IsRealDefect(true, true, true));
    }

    [Fact]
    public void IsRealDefect_requires_output_to_be_black()
    {
        Assert.False(BlackFrameRecovery.IsRealDefect(false, false, false));
        Assert.False(BlackFrameRecovery.IsRealDefect(false, true, true));
    }

    [Fact]
    public void IsRealDefect_only_fires_when_no_neighbour_is_near_black()
    {
        // 穷举 8 种组合,把口径钉死:唯一的"真缺陷"= 输出近黑 + 两端都不近黑
        int defects = 0;
        foreach (bool outBlack in new[] { true, false })
            foreach (bool aBlack in new[] { true, false })
                foreach (bool bBlack in new[] { true, false })
                    if (BlackFrameRecovery.IsRealDefect(outBlack, aBlack, bBlack)) defects++;
        Assert.Equal(1, defects);
    }

    [Fact]
    public void StepName_is_human_readable_and_mentions_engine()
    {
        Assert.Contains("ONNX", BlackFrameRecovery.StepName(BlackFrameRecovery.Step.OnnxSlots));
        Assert.Contains("换", BlackFrameRecovery.StepName(BlackFrameRecovery.Step.AltGpuNcnnSlots));
    }

    [Fact]
    public void FailureMessage_states_facts_tried_steps_and_next_action()
    {
        string m = BlackFrameRecovery.FailureMessage("按源时间轴插帧", slotCount: 120, blackAfterReroute: 7,
            triedOnnx: true, triedAltGpu: true);
        Assert.Contains("按源时间轴插帧", m);          // 哪个阶段
        Assert.Contains("120", m);                     // 槽数
        Assert.Contains("7", m);                        // 换路后仍黑帧数
        Assert.Contains("ONNX", m);                     // 试过什么
        Assert.Contains("换卡", m);
        Assert.Contains("任务按失败收尾", m);           // 处置(绝不放黑帧进成片)
        Assert.Contains("更新显卡驱动", m);             // 用户能做什么
    }

    [Fact]
    public void FailureMessage_reports_which_steps_were_unavailable()
    {
        string none = BlackFrameRecovery.FailureMessage("补回", 10, 10, triedOnnx: false, triedAltGpu: false);
        Assert.Contains("既没有可用的 ONNX 补帧引擎", none);
        string onlyOnnx = BlackFrameRecovery.FailureMessage("补回", 10, 10, triedOnnx: true, triedAltGpu: false);
        Assert.Contains("没有第二块显卡可换", onlyOnnx);
        string onlyAlt = BlackFrameRecovery.FailureMessage("补回", 10, 10, triedOnnx: false, triedAltGpu: true);
        Assert.Contains("没有可用的 ONNX 补帧引擎", onlyAlt);
    }

    [Fact]
    public void FailureMessage_never_suggests_cpu_interpolation()
    {
        // 「补帧绝不落 CPU」是产品硬约定:文案里绝不能引导用户去选 CPU(CPU 补帧慢到用户以为卡死)
        string m = BlackFrameRecovery.FailureMessage("补回", 10, 10, true, true);
        Assert.DoesNotContain("CPU", m);
    }

    [Fact]
    public void Plan_never_contains_cpu_fallback()
    {
        // 档位表本身也不许有 CPU:换路只有"换引擎/换卡"两档
        var plan = BlackFrameRecovery.Plan(true, true, true);
        Assert.Equal(2, plan.Length);
        Assert.All(plan, s => Assert.True(s == BlackFrameRecovery.Step.OnnxSlots
            || s == BlackFrameRecovery.Step.AltGpuNcnnSlots));
        Assert.Equal(plan.Length, plan.Distinct().Count());
    }
}
