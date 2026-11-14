using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【2026-11-09 用户诊断包 ALHPro_Diag_20261109_1924】内建 Administrator 账户登录时,
/// 资源管理器本身也是高完整性 ⇒ 「降权重启」拿到的新进程照样是管理员,每次启动都白重启一轮
/// (用户只看到窗口闪一下,拖放永远不可用)。
/// 【契约】启动流程必须:①降权重启后仍是管理员就记下来;②下次启动看到记录就跳过重启;
/// ③普通权限启动时清掉这条记录(换了账户/关了 UAC 之后要能恢复自动降权)。
/// 这些是启动期行为,单测只能按源码契约钉住(与其它 UI 契约测试同一手法)。</summary>
public class DeelevateMemoryTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ImgUpscalerUI", "App.xaml.cs"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("找不到仓库根(向上找 ImgUpscalerUI/App.xaml.cs 失败)");
    }

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

    [Fact]
    public void Startup_skips_the_de_elevate_restart_once_it_is_known_to_be_useless()
    {
        string app = Read("ImgUpscalerUI", "App.xaml.cs");
        Assert.Contains("RelaunchKnownIneffective()", app);
        // 已知无效时不许再调 TryRelaunchNormal(否则又白重启一轮)
        int guardIdx = app.IndexOf("RelaunchKnownIneffective()", StringComparison.Ordinal);
        int relaunchIdx = app.IndexOf("DeElevate.TryRelaunchNormal()", StringComparison.Ordinal);
        Assert.True(guardIdx > 0 && relaunchIdx > guardIdx, "跳过判定必须在降权重启之前");
    }

    [Fact]
    public void De_elevated_child_that_is_still_elevated_marks_the_machine()
    {
        string app = Read("ImgUpscalerUI", "App.xaml.cs");
        Assert.Contains("DeElevate.IsElevated() && DeElevate.HasFlag()", app);
        Assert.Contains("MarkRelaunchIneffective(", app);
        // 普通权限启动要清掉记录,换了账户能恢复
        Assert.Contains("ClearIneffectiveMark()", app);
    }

    [Fact]
    public void The_mark_is_a_plain_file_under_settings_and_expires()
    {
        string de = Read("ImgUpscalerUI", "DeElevate.cs");
        Assert.Contains("ParaPaths.SettingsFile(\"deelevate-ineffective.txt\")", de);
        Assert.Contains("TimeSpan.FromDays(7)", de);   // 记录会过期,不永久钉死一台机器
    }
}
