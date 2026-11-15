using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>
/// 启动清理不许误杀本次运行自己拉起的引擎。
/// 真机事故:启动时的「回收残留引擎进程」把刚为列表探测启动的 ffmpeg 一起杀了 ⇒
/// 列表项显示「? fps」、时长与分辨率全空(用户看到的就是这一条)。
/// </summary>
public class StaleEngineGuardTests
{
    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "ImgUpscalerUI", "Views", "MainPage.xaml")))
            d = d.Parent;
        if (d == null) throw new InvalidOperationException("找不到仓库根目录");
        return d.FullName;
    }

    private static string EngineServiceSource()
        => File.ReadAllText(Path.Combine(RepoRoot(), "ImgUpscalerUI", "EngineService.cs"));

    [Fact]
    public void Stale_engine_cleanup_skips_processes_started_after_this_run()
    {
        var src = EngineServiceSource();
        var start = src.IndexOf("public static int KillStaleEngines()", StringComparison.Ordinal);
        Assert.True(start > 0, "逻辑删掉了?");
        var body = src.Substring(start, Math.Min(4000, src.Length - start));

        // 必须有"本进程启动时刻"这个基准,并真正拿它划界
        Assert.Contains("_appStartedAt", body);
        Assert.Contains("p.StartTime", body);

        // 划界判断必须排在 Kill 之前 —— 顺序反了等于没判
        var guardAt = body.IndexOf("startedAfterUs", StringComparison.Ordinal);
        var killAt = body.IndexOf("p.Kill(true)", StringComparison.Ordinal);
        Assert.True(guardAt > 0 && killAt > 0 && guardAt < killAt, "先判后杀,顺序不能反");
        Assert.Contains("if (startedAfterUs) continue;", body);

        // 读不到启动时间时保守跳过,不许默认去杀
        Assert.Contains("catch { startedAfterUs = true; }", body);
    }

    [Fact]
    public void Stale_engine_cleanup_still_only_touches_our_own_engines_folder()
    {
        var src = EngineServiceSource();
        var start = src.IndexOf("public static int KillStaleEngines()", StringComparison.Ordinal);
        var body = src.Substring(start, Math.Min(4000, src.Length - start));
        Assert.Contains("path.StartsWith(engines, StringComparison.OrdinalIgnoreCase)", body);
        Assert.Contains("if (path.Length == 0) continue;", body);
    }
}