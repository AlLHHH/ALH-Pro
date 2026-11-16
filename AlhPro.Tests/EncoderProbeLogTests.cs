using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>硬编探测的失败原因必须带得住 ffmpeg 的原话。
///
/// 【真机事故 2026-11-16】那位 RTX 3060 用户的诊断包里,六档编码器的失败原因全长这样:
///   `h264_nvenc:[命令] 命令失败 (exit -40):`
/// 冒号后面是空的 —— 因为日志只留了 stderr 的**第一行**,而 ffmpeg 把真因写在第二行往后
/// (例如 `[h264_nvenc @ ...] Cannot load nvcuda.dll` / `Driver does not support the required
/// nvenc API version`)。
/// 结果:用户在 QQ 群里只能被问"再跑一次给我看日志",我们也只能猜。
/// 所以这里钉死两件事:①取头部要取多行;②探测失败的原因不许再退回"只取一行"。</summary>
public class EncoderProbeLogTests
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
    public void Multi_line_ffmpeg_error_keeps_the_three_head_lines()
    {
        string stderr =
            "[h264_nvenc @ 000001] Cannot load nvcuda.dll\r\n"
            + "[h264_nvenc @ 000001] Driver does not support the required nvenc API version. Required: 13.1 Found: 12.2\r\n"
            + "[h264_nvenc @ 000001] The minimum required Nvidia driver for nvenc is 610.00 or newer\r\n"
            + "Conversion failed!\r\n";

        string head = AlhPro.Core.VideoEncodeGuard.HeadOf(stderr, 3, 300);

        Assert.Contains("Cannot load nvcuda.dll", head);
        Assert.Contains("nvenc API version", head);           // 真因常在第二行
        Assert.DoesNotContain("Conversion failed!", head);     // 只取前 3 行,不把整段倒进日志
    }

    [Fact]
    public void Probe_failure_reason_is_built_from_three_lines_not_one()
    {
        string code = Read("ImgUpscalerUI", "VideoService.Encode.cs");

        // 三处给用户/日志看的原因都走 OneLineHead(多行)
        Assert.Contains("lastWhy = OneLineHead(ex.Message);", code);
        Assert.Contains("{OneLineHead(detail)}", code);
        Assert.Contains("OneLineHead(lastDetail)", code);

        // 取头部的实现:3 行 / 300 字符
        Assert.Contains("VideoEncodeGuard.HeadOf(text, 3, 300)", code);

        // 不许再出现"只取第一行"的写法(事故根因)
        Assert.DoesNotContain("Split('\\n')[0]", code);
        Assert.DoesNotContain("private static string FirstLine", code);
    }
}