using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>「编不出来」必须说清是**驱动太旧**，而不是笼统的「没有可用硬件编码器」。
///
/// 【真机 2026-11-16 · RTX 3060 那台】六档编码器全失败，日志只有 `exit -40`,
/// 界面只说「本机没有可用的硬件编码器（也可能是没检测到可用显卡）」。
/// 用户看到这句的结论只能是"我的电脑不行了"，实际只是驱动 31.0.15.4630（2023 年）太旧，
/// 更新一次驱动就好 —— 唯一能自救的动作却完全没被告知。
/// 这条测试钉死：①ffmpeg 说了 nvenc 版本不符时，提示必须直说驱动太旧；②这个分支要排在
/// 尺寸超限分支之前；③界面状态栏也要把这句话顶上来，不能只留"完成 0 个"。</summary>
public class DriverTooOldHonestyTests
{
    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "ImgUpscalerUI", "Views", "MainPage.xaml")))
            d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

    [Fact]
    public void Old_nvidia_driver_is_named_as_the_cause_and_says_what_to_do()
    {
        string msg = AlhPro.Core.VideoEncodeGuard.DescribePreflightFailure(
            "h264_nvenc", 1920, 1080, 60, "[h264_nvenc] Driver does not support the required nvenc API version",
            "h264_nvenc / hevc_nvenc / h264_amf / h264_qsv", h264LimitHit: false, nvencDriverTooOld: true);

        Assert.Contains("驱动太旧", msg);
        Assert.Contains("更新 NVIDIA 显卡驱动", msg);          // 用户能做的动作
        Assert.Contains("不是显卡坏了", msg);                  // 别让他以为要换电脑
        Assert.Contains("开始处理之前", msg);                  // 没有白跑
        Assert.DoesNotContain("可能是没检测到可用显卡", msg);   // 旧的口径不再出现
    }

    [Fact]
    public void Without_that_evidence_the_message_stays_generic()
    {
        string msg = AlhPro.Core.VideoEncodeGuard.DescribePreflightFailure(
            "h264_nvenc", 1920, 1080, 60, "[命令] 命令失败 (exit -40):", "h264_nvenc", false, false);

        Assert.DoesNotContain("驱动太旧", msg);
        Assert.Contains("编码预检失败", msg);
    }

    [Fact]
    public void Preflight_judges_on_every_encoder_not_just_the_last_one()
    {
        string code = Read("ImgUpscalerUI", "VideoService.Encode.cs");

        Assert.Contains("allDetails.Add(detail);", code);
        Assert.Contains("allDetails.Any(IsNvencDriverTooOld)", code);
        Assert.Contains("nvencDriverTooOld));", code);          // 传进 Core 的文案
    }

    [Fact]
    public void Status_bar_repeats_the_cause_instead_of_only_a_count()
    {
        string code = Read("ImgUpscalerUI", "Views", "VideoView.xaml.cs");

        Assert.Contains("NVIDIA 显卡驱动太旧", code);
        Assert.Contains("更新 NVIDIA 驱动后重试", code);
    }
}