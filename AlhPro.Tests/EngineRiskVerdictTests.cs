using System;
using System.IO;
using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【2026-09-16 用户裁决:删掉事后全黑画面检测 ⇒ 事前兼容性判定成为唯一防线】
/// Core.InterpSizePolicy.JudgeEngineRisk 的判据:哪些"尺寸 + 本机实测结论"组合已知会**静默输出全黑画面**,
/// 以及有 ONNX 可用时该不该直接换稳定引擎。
///
/// 【2026-11-15 改】参数从"是不是 50 系"换成"这台机器的补帧实测结论"。
/// 用户原话:「明明已经做了 50 系适配 却还是有 50 系适配黄字的问题」——
/// 旧写法按型号一律提示,等于对实测跑得通的 50 系用户说反话。所以这里钉住:
/// 只有【测过且不可用】才提示;没测过(null)不提示;测过通过(true)不提示。</summary>
public class EngineRiskVerdictTests
{
    /// <summary>8K 级:实测 ncnn 静默全黑(7680×4320 + -n 119 → 117/119)→ 有 ONNX 就直接换。</summary>
    [Fact]
    public void Eight_k_input_switches_to_the_stable_engine()
    {
        var v = InterpSizePolicy.JudgeEngineRisk(7680, 4320, ncnnAvailable: true, gpuRiskyOldNcnn: false, onnxModelAvailable: true);
        Assert.True(v.UseStableEngine);
        Assert.True(v.Warn);
        Assert.Contains("全全黑画面", v.Reason);
    }

    /// <summary>同样是 8K,但本机没有 ONNX 模型 → 换不了,必须明确提示"会出全黑画面"(不许静默照跑)。</summary>
    [Fact]
    public void Eight_k_without_onnx_warns_loudly_instead_of_silently_running()
    {
        var v = InterpSizePolicy.JudgeEngineRisk(7680, 4320, true, false, onnxModelAvailable: false);
        Assert.False(v.UseStableEngine);
        Assert.True(v.Warn);
        Assert.Contains("建议降低分辨率", v.Reason);
    }

    /// <summary>本机实测补帧引擎跑不通:1080p 也一样换稳定引擎,且文案说的是"实测",不再提显卡型号。</summary>
    [Fact]
    public void A_measured_failure_switches_even_at_1080p()
    {
        var v = InterpSizePolicy.JudgeEngineRisk(1920, 1080, ncnnAvailable: false, gpuRiskyOldNcnn: false, onnxModelAvailable: true);
        Assert.True(v.UseStableEngine);
        Assert.True(v.Warn);
        Assert.Contains("实测", v.Reason);
        Assert.DoesNotContain("RTX 50", v.Reason);
    }

    /// <summary>【本次修复的核心】还没测过 → 什么都不说。以前这里会凭"是 50 系"就挂黄字。</summary>
    [Fact]
    public void An_unmeasured_machine_is_not_warned_about_by_model_name()
    {
        var v = InterpSizePolicy.JudgeEngineRisk(1920, 1080, ncnnAvailable: null, gpuRiskyOldNcnn: false, onnxModelAvailable: true);
        Assert.False(v.UseStableEngine);
        Assert.False(v.Warn);
        Assert.Equal("", v.Reason);
    }

    /// <summary>测过且通过(典型:50 系 + 重编版 ncnn)→ 不提示、不换引擎。</summary>
    [Fact]
    public void A_measured_pass_stays_on_ncnn_without_any_warning()
    {
        var v = InterpSizePolicy.JudgeEngineRisk(3840, 2160, ncnnAvailable: true, gpuRiskyOldNcnn: false, onnxModelAvailable: true);
        Assert.False(v.UseStableEngine);
        Assert.False(v.Warn);
    }

    /// <summary>无独显 / Vulkan 不可用:同属已知会出无效画面的组合。</summary>
    [Fact]
    public void No_vulkan_device_is_treated_as_risky()
    {
        var v = InterpSizePolicy.JudgeEngineRisk(1920, 1080, ncnnAvailable: null, gpuRiskyOldNcnn: true, onnxModelAvailable: true);
        Assert.True(v.UseStableEngine);
        Assert.Contains("Vulkan", v.Reason);
    }

    /// <summary>正常组合(4060 + 1080p + 实测通过):不换引擎、不提示 —— 不许对好机器说废话。</summary>
    [Fact]
    public void Healthy_combination_is_left_alone()
    {
        var v = InterpSizePolicy.JudgeEngineRisk(1920, 1080, true, false, onnxModelAvailable: true);
        Assert.False(v.UseStableEngine);
        Assert.False(v.Warn);
        Assert.Equal("", v.Reason);
    }

    /// <summary>4K~8K 中间带:没有实测数据 → 不换引擎、只提示【待实测标定】。</summary>
    [Fact]
    public void Between_4k_and_8k_only_warns()
    {
        var v = InterpSizePolicy.JudgeEngineRisk(5120, 2880, true, false, onnxModelAvailable: true);   // 14.7 Mpx
        Assert.False(v.UseStableEngine);
        Assert.True(v.Warn);
        Assert.Contains("待实测标定", v.Reason);
    }

    /// <summary>尺寸未知(还没探到)→ 不许因为 0×0 就被判进 8K 故障带。</summary>
    [Fact]
    public void Unknown_size_does_not_count_as_the_failure_band()
    {
        var v = InterpSizePolicy.JudgeEngineRisk(0, 0, true, false, onnxModelAvailable: true);
        Assert.False(v.UseStableEngine);
        Assert.False(v.Warn);
    }

    /// <summary>源码契约:界面这侧不许再按显卡型号判"补帧有风险",必须传实测结论。</summary>
    [Fact]
    public void The_video_page_feeds_the_probe_verdict_instead_of_the_model_name()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "ImgUpscalerUI", "Views", "MainPage.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        var view = File.ReadAllText(Path.Combine(root!.FullName, "ImgUpscalerUI", "Views", "VideoView.xaml.cs"));

        Assert.Contains("ncnnAvailable: EngineService.TryGetEngineVerdictSummary(\"rife\", AppSettings.GpuIndex)", view);
        Assert.DoesNotContain("gpuIs50Series:", view);
    }
}