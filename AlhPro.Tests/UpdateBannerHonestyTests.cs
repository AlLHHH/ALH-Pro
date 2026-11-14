using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>
/// 2026-11-14 用户反馈两条界面问题,这里把它们钉成契约,防止回潮:
/// ① 顶部「发现新版本」横幅在已是最新时仍然弹出 —— 原因是设置里那条 PendingUpdateTag 从不比版本;
/// ② 四个页面都能看到一行「Ctrl+V」—— 那是 WinUI 给 KeyboardAccelerator 自动画的快捷键提示。
/// </summary>
public class UpdateBannerHonestyTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            if (Directory.Exists(Path.Combine(d.FullName, "AlhPro.Core")) &&
                Directory.Exists(Path.Combine(d.FullName, "ImgUpscalerUI")))
                return d.FullName;
            d = d.Parent;
        }
        return AppContext.BaseDirectory;
    }

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot }.Concat(parts).ToArray()));

    // ---------- ① 版本比较本身 ----------

    [Theory]
    [InlineData("v1.4.9", "1.4.9", false)]      // 同一个版本:不是新版(这正是用户遇到的情况)
    [InlineData("1.4.9", "1.4.9", false)]
    [InlineData("v1.4.10", "1.4.9", true)]      // 两位数段要按数字比,不能按字符串比
    [InlineData("v1.4.9", "1.4.10", false)]
    [InlineData("v1.5", "1.4.9", true)]
    [InlineData("v1.4.9-beta", "1.4.8", true)]  // 后缀忽略,只比数字
    [InlineData("v1.4.9 (2026-11-14)", "1.4.8", true)]
    [InlineData("", "1.4.9", false)]            // 空/垃圾输入:一律不弹
    [InlineData("latest", "1.4.9", false)]
    [InlineData("v1.4.9", null, false)]
    public void Tag_comparison_is_numeric_and_never_over_reports(string tag, string current, bool expected)
    {
        Assert.Equal(expected, UpdateTag.IsNewer(tag, current));
    }

    // ---------- ② 界面代码必须真的用它 ----------

    [Fact]
    public void Startup_pending_tag_is_compared_before_the_banner_is_shown()
    {
        var cs = Read("ImgUpscalerUI", "Views", "MainPage.xaml.cs");
        int at = cs.IndexOf("AppSettings.PendingUpdateTag", StringComparison.Ordinal);
        Assert.True(at > 0, "找不到读取 PendingUpdateTag 的地方");
        var tail = cs.Substring(at, Math.Min(1400, cs.Length - at));
        Assert.Contains("IsTagNewerThanCurrent(pendingTag)", tail);
        // 不许再出现"取出来就直接弹"的老写法
        Assert.DoesNotContain("TryEnqueue(() => ShowUpdateBar(pendingTag))", cs);
    }

    [Fact]
    public void Banner_itself_refuses_to_show_for_a_tag_that_is_not_newer()
    {
        var cs = Read("ImgUpscalerUI", "Views", "MainPage.xaml.cs");
        int at = cs.IndexOf("private void ShowUpdateBar(", StringComparison.Ordinal);
        Assert.True(at > 0);
        int end = cs.IndexOf("private DateTime _adHiddenUntil", at, StringComparison.Ordinal);
        Assert.True(end > at);
        var body = cs.Substring(at, end - at);
        Assert.Contains("IsTagNewerThanCurrent(latestTag)", body);
        Assert.True(body.IndexOf("IsTagNewerThanCurrent(latestTag)", StringComparison.Ordinal)
                    < body.IndexOf("UpdateBar.Visibility", StringComparison.Ordinal),
            "必须先在弹之前判断,不能先显示再纠正");
    }

    // ---------- ③ Ctrl+V 不许自己画到界面上 ----------

    [Fact]
    public void Paste_shortcut_does_not_use_a_keyboard_accelerator()
    {
        var cs = Read("ImgUpscalerUI", "Views", "MainPage.xaml.cs");
        // KeyboardAccelerator 会让 WinUI 把 "Ctrl+V" 当提示画在页面根元素上(用户四个页面都能看到)
        Assert.DoesNotContain("KeyboardAccelerators.Add", cs);
        Assert.DoesNotContain("new Microsoft.UI.Xaml.Input.KeyboardAccelerator", cs);
        // 功能本身不能丢:改由 KeyDown 自己接
        Assert.Contains("RootGrid.KeyDown += RootGrid_KeyDown;", cs);
        Assert.Contains("private async void RootGrid_KeyDown(", cs);
    }

    [Fact]
    public void Paste_shortcut_yields_to_text_boxes_and_to_plain_text_pastes()
    {
        var cs = Read("ImgUpscalerUI", "Views", "MainPage.xaml.cs");
        int at = cs.IndexOf("private async void RootGrid_KeyDown(", StringComparison.Ordinal);
        Assert.True(at > 0);
        int end = cs.IndexOf("/// <summary>把一批路径交给当前页面导入", at, StringComparison.Ordinal);
        Assert.True(end > at);
        var body = cs.Substring(at, end - at);
        // 焦点在输入框里要让文本框自己粘贴
        Assert.Contains("is Microsoft.UI.Xaml.Controls.TextBox", body);
        // 剪贴板里没有文件时不能吞掉这次按键
        Assert.Contains("if (!dp.Contains(StandardDataFormats.StorageItems)) return;", body);
        // 必须在确认有文件之后才标记已处理
        Assert.True(body.IndexOf("Contains(StandardDataFormats.StorageItems)", StringComparison.Ordinal)
                    < body.IndexOf("e.Handled = true;", StringComparison.Ordinal),
            "先判有没有文件,再标记 Handled");
    }

    [Fact]
    public void No_page_text_shows_a_shortcut_hint_left_over_from_the_old_registration()
    {
        var xaml = Read("ImgUpscalerUI", "Views", "MainPage.xaml");
        Assert.DoesNotContain("Ctrl+V", xaml);
        Assert.DoesNotContain("KeyboardAccelerator", xaml);
    }
}