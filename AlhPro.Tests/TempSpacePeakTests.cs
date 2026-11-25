using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【2026-11-25 用户反馈】「多个视频处理会弹出占用可能会过 100G 的提示,并且想开始会报错」。
///
/// 复现出的口径错误(纯算术,不需要跑视频):
///   界面的占盘预检按 **总和** 累加 —— `totalNeedGB += 峰值帧数 × 单帧MB × 1.6`。
///   但多个视频是 **一个一个跑**,每个视频跑完 `VideoService` 的 finally 立刻删掉临时帧
///   (VideoService.cs 的 `Directory.Delete(workDir, true)`),同一时刻盘上只有一片的帧。
///   ⇒ 2 个视频报成 2 倍、5 个报成 5 倍,于是「几个视频就说过 100GB」;
///     而处理端守门是 **逐片** 判的,数字对不上 —— 界面说装不下、处理端立刻拒绝,用户看到的就是「开始就报错」。
///
/// 另外界面还自己写了一份单帧体积公式(漏了补帧直出 JPG 的 3.1 倍、也漏了每批并存帧),
/// 与 `AlhPro.Core.TempSpaceEstimate` 必然漂移 —— 现在一律走 Core 的同一个函数。
///
/// 本文件钉住:峰值取最大不相加、界面只调 Core 的口径、放不下时要说清往哪降。
public class TempSpacePeakTests
{
    private static string ReadRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var p = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(p)) return File.ReadAllText(p);
            dir = dir.Parent;
        }
        throw new FileNotFoundException(string.Join("\\", parts));
    }

    private static string View() => ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");

    /// <summary>多个视频的整任务峰值 = 最大那一片,不是相加。</summary>
    [Fact]
    public void The_peak_of_several_videos_is_the_largest_one_not_the_sum()
    {
        double peak = AlhPro.Core.TempSpaceEstimate.TaskPeakGigabytes(new[] { 10.0, 45.0, 12.0 });
        Assert.Equal(45.0, peak, 3);
        Assert.NotEqual(67.0, peak, 3);
    }

    /// <summary>没有视频(或全是不合法值)时不占盘,不能返回 NaN/负数。</summary>
    [Fact]
    public void No_videos_means_no_footprint()
    {
        Assert.Equal(0.0, AlhPro.Core.TempSpaceEstimate.TaskPeakGigabytes(null), 3);
        Assert.Equal(0.0, AlhPro.Core.TempSpaceEstimate.TaskPeakGigabytes(Array.Empty<double>()), 3);
        Assert.Equal(0.0, AlhPro.Core.TempSpaceEstimate.TaskPeakGigabytes(new[] { double.NaN, -5.0 }), 3);
    }

    /// <summary>界面不许再自己累加(总量口径),必须调 Core 的峰值函数。</summary>
    [Fact]
    public void The_view_takes_the_peak_instead_of_adding_up_every_video()
    {
        var src = View();
        Assert.DoesNotContain("totalNeedGB +=", src);
        Assert.Contains("AlhPro.Core.TempSpaceEstimate.TaskPeakGigabytes(", src);
        Assert.Contains("AlhPro.Core.TempSpaceEstimate.NeedGigabytesFor(", src);
    }

    /// <summary>放不下时,诊断框里要直接给「关哪一项、改完多少 GB、放不放得下」。</summary>
    [Fact]
    public void The_dialog_says_which_switch_to_turn_off_and_what_it_costs()
    {
        var src = View();
        Assert.Contains("关掉「超分」(补帧保留)→ 约", src);
        Assert.Contains("关掉「补帧」→ 约", src);
        Assert.Contains("跑完立刻清临时帧,不会叠加", src);
    }

    /// <summary>临时盘放不下时,状态栏要说人话(长细节仍留在日志区)。</summary>
    [Fact]
    public void The_failure_message_points_at_the_way_out()
    {
        var src = View();
        Assert.Contains("临时盘放不下这个视频", src);
    }
}