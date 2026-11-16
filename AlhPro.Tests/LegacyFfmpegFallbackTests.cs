using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>
/// 「显卡驱动太旧 → 硬编全败」的兜底:探测链里必须有一支**更老**的 ffmpeg(6.1.1)可以试。
///
/// 【为什么钉它】2026-11-16 那台 RTX 3060 Laptop(驱动 31.0.15.4630,2023 年的)视频任务开跑前就停:
///   硬件编码器探测六档全败 `exit -40`,日志只剩 `Driver does not support the required nvenc API version`。
///   主 ffmpeg 与备用 ffmpeg8 都是 8.x(要 NVENC API 13),那台驱动只到 12.1;同款 6.1.1 实测 exit 0。
///   只加提示(「更新驱动」)虽然诚实,但"暂时不更新驱动的人"就被挡在门外了 —— 所以补一支老引擎。
///   这几条断言保证:①目录名不会被手滑改名而不自知 ②探测链真的会试它 ③日志说清用的是哪一支
///   ④安装包真的把它带上了(engines\ 是 gitignore、deploy.ps1 也不同步 engines,最容易漏)。
/// </summary>
public class LegacyFfmpegFallbackTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, @"ImgUpscalerUI\Views\MainPage.xaml")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("找不到仓库根目录");
    }

    private static string Read(params string[] relative)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(relative).ToArray()));

    [Fact]
    public void The_old_ffmpeg_lives_in_its_own_folder_and_is_discovered_by_name()
    {
        var cs = Read("ImgUpscalerUI", "VideoService.cs");

        Assert.Contains("public static string? LegacyFfmpegPath => FindInEngines(\"ffmpeg61\", \"ffmpeg.exe\");", cs);
        // 目录名只准写在这一处,别的文件不许各写一套字符串
        Assert.Equal(1, cs.Split("FindInEngines(\"ffmpeg61\"").Length - 1);
    }

    [Fact]
    public void The_probe_chain_tries_the_oldest_ffmpeg_after_the_newer_backup()
    {
        var cs = Read("ImgUpscalerUI", "VideoService.Encode.cs");

        var bak = cs.IndexOf("var bak = BackupFfmpegPath;", StringComparison.Ordinal);
        var legacy = cs.IndexOf("var legacy = LegacyFfmpegPath;", StringComparison.Ordinal);
        Assert.True(bak > 0, "探测链里找不到备用 ffmpeg8 那一段");
        Assert.True(legacy > bak, "最老一支 ffmpeg 必须排在 ffmpeg8 之后(先试新的,再试老的)");

        Assert.Contains("attempts.Add((legacy, realArgs, false));", cs);
        Assert.Contains("attempts.Add((legacy, StripPreset(realArgs), true));", cs);
        // 同一个路径不许重复试(主 ffmpeg 已经就是老的那支时)
        Assert.Contains("legacy != null && !string.Equals(legacy, ffmpeg, StringComparison.OrdinalIgnoreCase)", cs);
    }

    [Fact]
    public void The_log_says_which_ffmpeg_was_used_not_just_backup()
    {
        var cs = Read("ImgUpscalerUI", "VideoService.Encode.cs");

        Assert.Contains("最老一支 ffmpeg(6.1,给旧驱动的)", cs);
        Assert.Contains("备用 ffmpeg", cs);
        // 旧的 ReferenceEquals 写法对属性调用永远为 false(每次都是新字符串),必须按值比较
        Assert.DoesNotContain("ReferenceEquals(ff, ffmpeg)", cs);
    }

    [Fact]
    public void The_installer_ships_the_old_ffmpeg()
    {
        var iss = Read("installer.iss");

        Assert.Contains("Source: \"发布版\\engines\\ffmpeg61\\*\"; DestDir: \"{app}\\engines\\ffmpeg61\"", iss);
        Assert.Contains("Source: \"发布版\\engines\\ffmpeg8\\*\"; DestDir: \"{app}\\engines\\ffmpeg8\"", iss);
    }
}