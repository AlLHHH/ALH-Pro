using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【2026-11-22 用户反馈】「全屏模式下预览界面底下按钮都没了,普通情况下预览界面下方也是被覆盖了」。
///
/// 实测复现(把窗口缩到 884×481,等价于 1366×768 笔记本 + 125% 缩放后的可视区):
///   ① 画面那一行写死 `MinHeight="140"` —— 窗口不够高时它不肯让位,**后面三条 Auto 行被挤出可视区**,
///      而且画面还会和底下的播放条/时间线**重叠**;
///   ② 顶上那一条是「名字 | 视图四选一 | 退出」一行四列,单选项这一列是 Auto 不肯压窄 ⇒ 整行右溢,
///      「左右对比」被切、连右上角的「退出」都被顶到窗口外面;
///   ③ 对比播放条底行挤了「播放键 + 四个倍速 + 静音 + 时间码」,而播放区在窄窗口里只有三百多像素
///      (左边导航 178 + 参数栏 300 就吃掉了)⇒ 慢动作4×/8× 和时间码被切。
///
/// 本文件把三条契约钉住:画面行可以让位、退出按钮永远在第一行、时间码不再和倍速抢那一行 ——
/// 谁再把这些值改回去,这四条会立刻红。
public class PreviewLayoutTests
{
    private static string Xaml() => StripBom(ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml"));

    /// <summary>画面那一行必须是「可以让位」的 * 行:MinHeight 只能 0。</summary>
    [Fact]
    public void The_picture_row_can_shrink_so_the_controls_are_never_pushed_out()
    {
        var xaml = Xaml();
        Assert.Contains("<RowDefinition Height=\"*\" MinHeight=\"0\"/>", xaml);
        Assert.DoesNotContain("MinHeight=\"140\"", xaml);
    }

    /// <summary>顶栏拆成两行:退出按钮在第一行(名字右边),视图四选一单独占第二行。</summary>
    [Fact]
    public void The_exit_button_sits_on_the_first_row_and_the_view_switch_on_the_second()
    {
        var xaml = Xaml();

        // 退出按钮:第一行 + 最后一列
        var exitLine = xaml.Split('\n').FirstOrDefault(l => l.Contains("Content=\"退出\"") && l.Contains("PreviewClose_Click"));
        Assert.NotNull(exitLine);
        Assert.Contains("Grid.Row=\"0\"", exitLine);
        Assert.Contains("Grid.Column=\"1\"", exitLine);

        // 视图四选一:第二行、横跨两列(窄窗口时它自己换行,不挤别人)
        var radios = xaml.Split('\n').FirstOrDefault(l => l.Contains("x:Name=\"PreviewViewRadios\""));
        Assert.NotNull(radios);
        var block = string.Join('\n', xaml.Split('\n').SkipWhile(l => !l.Contains("x:Name=\"PreviewViewRadios\"")).Take(3));
        Assert.Contains("Grid.Row=\"1\"", block);
        Assert.Contains("Grid.ColumnSpan=\"2\"", block);
    }

    /// <summary>时间码离开倍速那一行(挪到进度条右端),底行只留播放键与倍速。</summary>
    [Fact]
    public void The_time_code_no_longer_shares_the_row_with_the_speed_buttons()
    {
        var xaml = Xaml();

        var timeLine = xaml.Split('\n').FirstOrDefault(l => l.Contains("x:Name=\"CmpTime\""));
        Assert.NotNull(timeLine);
        // 它在进度条那一行的容器里(第 0 行),不再挂在底行最后一列(第 3 列)
        Assert.Contains("Grid.Column=\"1\"", timeLine);
        Assert.DoesNotContain("Grid.Column=\"3\"", timeLine);

        // 四个倍速按钮排成 2×2:每一对各自一个横向 StackPanel
        var rate1 = xaml.IndexOf("x:Name=\"CmpRate1Btn\"", StringComparison.Ordinal);
        var rate2 = xaml.IndexOf("x:Name=\"CmpRate2Btn\"", StringComparison.Ordinal);
        var rate4 = xaml.IndexOf("x:Name=\"CmpRate4Btn\"", StringComparison.Ordinal);
        var rate8 = xaml.IndexOf("x:Name=\"CmpRate8Btn\"", StringComparison.Ordinal);
        Assert.True(rate1 > 0 && rate2 > rate1 && rate4 > rate2 && rate8 > rate4);

        // 2×2 的判据:前两个与后两个之间隔着一个尚未闭合的 StackPanel 段
        var between = xaml.Substring(rate2, rate4 - rate2);
        Assert.Contains("</StackPanel>", between);
        Assert.Contains("<StackPanel Orientation=\"Horizontal\"", between);
    }

    // ---------- 小工具 ----------

    private static string StripBom(string s) => s.Length > 0 && s[0] == '\uFEFF' ? s.Substring(1) : s;

    private static string ReadRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var cand = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(cand)) return File.ReadAllText(cand);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("找不到仓库文件: " + string.Join('/', parts));
    }
}