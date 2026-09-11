using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>探测失败形态 → 判定与文案的单测。
/// 【为什么必须有】这里守的是两条"不许说错话"的硬约束:
///   ①只有"初始化即崩/挂死"才允许提到 NVIDIA/驱动 —— 否则会把用户引向等驱动修复,而问题其实在别处;
///   ②"出图但坏帧"一律不得出现 NVIDIA/驱动字样 —— 否则会把我们自己的并发问题(已修的 -j 那类)
///     永远甩给厂商,排查方向被带偏。
/// 文案类逻辑最容易在后续改动里被顺手改坏,所以用测试钉住。</summary>
public class ProbeDiagnosisTests
{
    [Theory]
    [InlineData(ProbeFailureKind.CrashExitCode)]
    [InlineData(ProbeFailureKind.Hang)]
    [InlineData(ProbeFailureKind.StartupFailed)]
    public void Init_stage_failures_are_recognized(ProbeFailureKind k)
        => Assert.True(ProbeDiagnosis.IsInitStageFailure(k));

    [Theory]
    [InlineData(ProbeFailureKind.DefectiveFrame)]
    [InlineData(ProbeFailureKind.NoOutput)]
    [InlineData(ProbeFailureKind.EmptyOutput)]
    [InlineData(ProbeFailureKind.EngineMissing)]
    [InlineData(ProbeFailureKind.None)]
    public void Non_init_stage_failures_are_not_recognized(ProbeFailureKind k)
        => Assert.False(ProbeDiagnosis.IsInitStageFailure(k));

    [Theory]
    [InlineData(ProbeFailureKind.CrashExitCode)]
    [InlineData(ProbeFailureKind.Hang)]
    [InlineData(ProbeFailureKind.StartupFailed)]
    public void Blackwel_init_failure_blames_driver_but_says_it_is_not_our_software(ProbeFailureKind k)
    {
        var s = ProbeDiagnosis.Describe(k, blackwell: true, engineLabel: "Real-ESRGAN");
        Assert.Contains("NVIDIA", s);          // 指出真凶
        Assert.Contains("不是本软件的问题", s);   // 但明确不背锅/不误导
        Assert.Contains("ONNX", s);            // 并告知已自动改走稳定路线
        Assert.Contains("Real-ESRGAN", s);     // 指名引擎
    }

    [Theory]
    [InlineData(ProbeFailureKind.CrashExitCode)]
    [InlineData(ProbeFailureKind.Hang)]
    public void Non_blackwell_init_failure_does_not_mention_nvidia(ProbeFailureKind k)
    {
        var s = ProbeDiagnosis.Describe(k, blackwell: false, engineLabel: "waifu2x");
        Assert.DoesNotContain("NVIDIA", s);
        Assert.Contains("驱动", s);            // 但仍提示"驱动/兼容性"这个正确方向
    }

    [Fact]
    public void Defective_frame_never_blames_nvidia()
    {
        // 坏帧是另一类问题(引擎并发/渲染),无论是否 Blackwell 都不得提 NVIDIA。
        foreach (var bw in new[] { true, false })
        {
            var s = ProbeDiagnosis.Describe(ProbeFailureKind.DefectiveFrame, bw, "RIFE");
            Assert.DoesNotContain("NVIDIA", s);
            Assert.DoesNotContain("驱动缺陷", s);
            Assert.Contains("ONNX", s);
        }
    }

    [Fact]
    public void Engine_missing_does_not_blame_gpu_or_driver()
    {
        var s = ProbeDiagnosis.Describe(ProbeFailureKind.EngineMissing, blackwell: true, engineLabel: "waifu2x");
        Assert.DoesNotContain("NVIDIA", s);
        Assert.DoesNotContain("驱动", s);
    }

    [Fact]
    public void Every_kind_has_a_short_name()
    {
        foreach (ProbeFailureKind k in System.Enum.GetValues(typeof(ProbeFailureKind)))
        {
            Assert.False(string.IsNullOrWhiteSpace(ProbeDiagnosis.ShortName(k)));
            Assert.NotEqual("未知", ProbeDiagnosis.ShortName(k));
        }
    }
}
