using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【2026-09-16 用户裁决:删掉事后黑帧检测 ⇒ 事前兼容性判定成为唯一防线】
/// Core.InterpSizePolicy.JudgeEngineRisk 的判据:哪些"尺寸 + 显卡"组合已知会**静默输出黑帧**,
/// 以及有 ONNX 可用时该不该直接换稳定引擎。
///
/// 【为什么要有它】删掉事后检测之后,如果这条预检漏判,成片会带着黑段交出去、日志里也查不到 ——
/// 所以每一条已知组合都要有测试钉住(出处逐条写在 InterpSizePolicy 的注释里,都是真机实测)。</summary>
public class EngineRiskVerdictTests
{
    /// <summary>8K 级:实测 ncnn 静默全黑(7680×4320 + -n 119 → 117/119)→ 有 ONNX 就直接换。</summary>
    [Fact]
    public void Eight_k_input_switches_to_the_stable_engine()
    {
        var v = InterpSizePolicy.JudgeEngineRisk(7680, 4320, gpuIs50Series: false, gpuRiskyOldNcnn: false, onnxModelAvailable: true);
        Assert.True(v.UseStableEngine);
        Assert.True(v.Warn);
        Assert.Contains("全黑帧", v.Reason);
    }

    /// <summary>同样是 8K,但本机没有 ONNX 模型 → 换不了,必须明确提示"会出黑帧"(不许静默照跑)。</summary>
    [Fact]
    public void Eight_k_without_onnx_warns_loudly_instead_of_silently_running()
    {
        var v = InterpSizePolicy.JudgeEngineRisk(7680, 4320, false, false, onnxModelAvailable: false);
        Assert.False(v.UseStableEngine);
        Assert.True(v.Warn);
        Assert.Contains("建议降低分辨率", v.Reason);
    }

    /// <summary>RTX 50 系(Blackwell):1080p 也一样换稳定引擎 —— 这正是旧实现按尺寸判会漏掉的组合。</summary>
    [Fact]
    public void Blackwell_gpu_switches_even_at_1080p()
    {
        var v = InterpSizePolicy.JudgeEngineRisk(1920, 1080, gpuIs50Series: true, gpuRiskyOldNcnn: false, onnxModelAvailable: true);
        Assert.True(v.UseStableEngine);
        Assert.Contains("RTX 50", v.Reason);
    }

    /// <summary>无独显 / Vulkan 不可用:同属已知会出坏帧的组合。</summary>
    [Fact]
    public void No_vulkan_device_is_treated_as_risky()
    {
        var v = InterpSizePolicy.JudgeEngineRisk(1920, 1080, gpuIs50Series: false, gpuRiskyOldNcnn: true, onnxModelAvailable: true);
        Assert.True(v.UseStableEngine);
        Assert.Contains("Vulkan", v.Reason);
    }

    /// <summary>正常组合(4060 + 1080p + 有 ONNX):不换引擎、不提示 —— 不许对好机器说废话。</summary>
    [Fact]
    public void Healthy_combination_is_left_alone()
    {
        var v = InterpSizePolicy.JudgeEngineRisk(1920, 1080, false, false, onnxModelAvailable: true);
        Assert.False(v.UseStableEngine);
        Assert.False(v.Warn);
        Assert.Equal("", v.Reason);
    }

    /// <summary>4K~8K 中间带:没有实测数据 → 不换引擎、只提示【待实测标定】。</summary>
    [Fact]
    public void Between_4k_and_8k_only_warns()
    {
        var v = InterpSizePolicy.JudgeEngineRisk(5120, 2880, false, false, onnxModelAvailable: true);   // 14.7 Mpx
        Assert.False(v.UseStableEngine);
        Assert.True(v.Warn);
        Assert.Contains("待实测标定", v.Reason);
    }

    /// <summary>尺寸未知(还没探到)→ 不许因为 0×0 就被判进 8K 故障带。</summary>
    [Fact]
    public void Unknown_size_does_not_count_as_the_failure_band()
    {
        var v = InterpSizePolicy.JudgeEngineRisk(0, 0, false, false, onnxModelAvailable: true);
        Assert.False(v.UseStableEngine);
        Assert.False(v.Warn);
    }
}
