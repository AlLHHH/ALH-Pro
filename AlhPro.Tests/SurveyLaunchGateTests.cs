using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>
/// 问卷邀请必须等到第 3 次启动才弹(2026-11-21 用户要求)。
/// 这类"什么时候弹"的规则没法在单测里开真窗口验证,所以直接钉住源码契约:
/// 计数要持久化、每次启动只加一次、判断要发生在其它条件之前、门槛数字是 3。
/// </summary>
public class SurveyLaunchGateTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "ImgUpscalerUI"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("找不到仓库根目录");
    }

    private static string Read(params string[] parts)
    {
        var path = Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray());
        return File.ReadAllText(path);
    }

    [Fact]
    public void The_launch_counter_is_saved_and_restored()
    {
        var s = Read("ImgUpscalerUI", "AppSettings.cs");
        Assert.Contains("public static int LaunchCount { get; set; }", s);
        Assert.Contains("LaunchCount = d.LaunchCount;", s);          // 读回来
        Assert.Contains("LaunchCount = LaunchCount,", s);            // 写出去
        Assert.Contains("public int LaunchCount { get; set; }", s);  // 落盘结构里有这一项
    }

    [Fact]
    public void Each_start_adds_exactly_one_to_the_counter()
    {
        var s = Read("ImgUpscalerUI", "Views", "MainPage.xaml.cs");
        int load = s.IndexOf("AppSettings.Load();", StringComparison.Ordinal);
        int inc = s.IndexOf("AppSettings.LaunchCount++;", StringComparison.Ordinal);
        Assert.True(load >= 0 && inc > load, "启动计数必须写在 AppSettings.Load() 之后");
        // 整个文件里只允许加一次,否则一次启动会被算成两次/三次
        int occurrences = s.Split("AppSettings.LaunchCount++").Length - 1;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void The_survey_waits_for_the_third_launch()
    {
        var s = Read("ImgUpscalerUI", "Views", "MainPage.xaml.cs");
        Assert.Contains("private const int SurveyMinLaunch = 3;", s);

        var gate = s.IndexOf("if (AppSettings.LaunchCount < SurveyMinLaunch)", StringComparison.Ordinal);
        Assert.True(gate > 0, "问卷方法里必须有启动次数闸门");

        // 闸门要排在"填写过""关闭过"这些判断之前:先算清楚第几次启动,再看历史状态
        var shown = s.IndexOf("if (!string.IsNullOrEmpty(AppSettings.SurveyShownVersion)) return;", StringComparison.Ordinal);
        var closed = s.IndexOf("AppSettings.SurveyClosedVersion) && Random.Shared.NextDouble()", StringComparison.Ordinal);
        Assert.True(shown > gate && closed > gate, "启动次数闸门必须排在问卷调查的历史状态判断之前");

        // 不到次数直接返回,不许继续往下弹
        Assert.Contains("还没到第 {SurveyMinLaunch} 次,本次不弹", s);
    }
}