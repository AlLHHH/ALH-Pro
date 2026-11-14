using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>
/// 【2026-11-14 用户定调 · 第 4 条】预计耗时只认实测。
/// 事故:界面标称 0.27 秒/帧,真机 CPU 实测 3880 毫秒/帧;后来同类问题把一条视频估成 44 小时,用户当场退出。
/// 编数字的代价比"说不知道"大得多 —— 所以这里把"没有实测就不许给数字"钉成契约。
/// </summary>
public class EtaHonestyTests
{
    private static string RepoRoot
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "ImgUpscalerUI", "ImgUpscalerUI.csproj")))
                d = d.Parent;
            if (d == null) throw new InvalidOperationException("找不到仓库根目录");
            return d.FullName;
        }
    }

    /// <summary>视频页的路线/耗时判定现在拆在 VideoView.xaml.cs 与 VideoView.Route.cs 两个文件里,一起读。</summary>
    private static string RouteSource()
    {
        var dir = Path.Combine(RepoRoot, "ImgUpscalerUI", "Views");
        var main = Path.Combine(dir, "VideoView.xaml.cs");
        var parts = Directory.GetFiles(dir, "VideoView*.cs")
            .Where(x => !string.Equals(x, main, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x, StringComparer.Ordinal)
            .Select(File.ReadAllText);
        return File.ReadAllText(main) + string.Concat(parts);
    }

    [Fact]
    public void Cpu_estimate_shows_a_number_only_when_both_frame_count_and_measured_speed_exist()
    {
        var src = RouteSource();

        // 帧数/尺寸没探明 -> 不给数字
        Assert.Contains("无法给出秒/帧与总时长预估", src);

        // 有了帧数但没有同配置实测秒/帧 -> 仍然不给数字
        Assert.Contains("if (!fromHistory)", src);
        Assert.Contains("这次给不出预计耗时", src);
        Assert.Contains("跑完会记住实际速度", src);

        // 只有实测值参与的那一行才允许出现"预计约 …"
        var gate = src.IndexOf("if (!fromHistory)", StringComparison.Ordinal);
        var numeric = src.IndexOf("该阶段预计约", StringComparison.Ordinal);
        Assert.True(gate > 0, "找不到 fromHistory 闸门");
        Assert.True(numeric > gate, "『预计约』这行必须排在 fromHistory 闸门之后,否则就是在编数字");
        Assert.Contains("秒/帧取自同配置上次实测", src);
    }

    [Fact]
    public void Backup_route_hint_never_invents_a_total_when_no_measurement_exists()
    {
        var src = RouteSource();
        // 备用路线(ONNX)只在有实测秒/帧时给区间,否则明说给不出
        Assert.Contains("备", src);
        Assert.Contains("慢 20~60 倍", src);
        Assert.Contains("重启", src);
    }
}