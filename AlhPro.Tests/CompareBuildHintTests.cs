using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace AlhPro.Tests;

/// <summary>「对比片还在后台合成」那句提示文案的契约(2026-09-22 用户:「生成50画面的提示词该更新了」)。
///
/// 【旧文案为什么必须改】原文是 `正在生成 {50}% 处的对比画面 {37}%`:
///   ① 说成"生成**画面**",可后台合成的是一条**对比片**(左原片+右处理后并排)——
///      用户就是把它读成"生成 50% 画面"的 ✗;
///   ② 两个百分号紧挨着,`50%` 是分界位置、`37%` 是合成进度,谁也分不清 ✗。
///
/// 【契约钉住三件事】①旧文案不许回来(含只活在注释外的任何副本);
///   ②文案里必须说清"后台合成的是对比片"+"给哪个视角用"+只留一个进度百分号;
///   ③文案**只有一个出处**(VideoView.xaml.cs 的 CompareBuildText),免得又冒出第二份各说各话。</summary>
public class CompareBuildHintTests
{
    private static string Xaml => ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml");
    private static string Cs => ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");

    /// <summary>① 旧文案的正文一个都不许留(XAML 与 .cs 都要干净)。
    /// 【为什么先剥注释】留档注释里**故意**引用了那句旧文案(说明它为什么错),那是资产不是负债 ——
    /// 要钉的是"**界面上真正显示出来的字**",所以先把注释剥掉再查。</summary>
    [Fact]
    public void The_stale_wording_must_not_come_back()
    {
        var cs = StripCsComments(Cs);
        var xaml = StripXmlComments(Xaml);
        Assert.DoesNotContain("处的对比画面", cs);
        Assert.DoesNotContain("正在生成对比画面", cs);
        Assert.DoesNotContain("正在生成对比画面", xaml);
        // 用户当初读到的那句,连同它的两个百分号形态一起钉住
        Assert.DoesNotMatch(new Regex(@"正在生成\s*\{[^}]*\}\s*%"), cs);
    }

    /// <summary>剥掉 C# 注释(// 与 /* */):只检查"会显示给用户的字"。</summary>
    private static string StripCsComments(string s)
    {
        s = Regex.Replace(s, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        s = Regex.Replace(s, @"//[^\n]*", " ");
        return s;
    }

    /// <summary>剥掉 XML 注释(&lt;!-- --&gt;)。</summary>
    private static string StripXmlComments(string s)
        => Regex.Replace(s, @"<!--.*?-->", " ", RegexOptions.Singleline);

    /// <summary>② 新文案:说清"后台合成对比片"+给哪个视角+进度只占一个百分号。</summary>
    [Fact]
    public void The_hint_says_what_is_built_and_for_which_view()
    {
        Assert.Contains("private static string CompareBuildText(", Cs);
        Assert.Contains("正在后台合成「两者同时」的对比片", Cs);
        Assert.Contains("正在后台合成左右对比片", Cs);
        // 分界与进度必须有各自的说法,不许再糊成两个裸百分号
        Assert.Contains("分界 {splitPct * 100:0}%", Cs);
        Assert.Contains("进度 {pct}%", Cs);
    }

    /// <summary>③ 文案只有一个出处:凡是给 CmpBuildingLabel 赋值的行,都必须走 CompareBuildText。</summary>
    [Fact]
    public void Every_assignment_of_the_label_goes_through_the_single_helper()
    {
        var lines = Cs.Split('\n')
                      .Select(l => l.Trim())
                      .Where(l => l.StartsWith("CmpBuildingLabel.Text =", StringComparison.Ordinal))
                      .ToList();
        Assert.True(lines.Count >= 2, "给 CmpBuildingLabel 赋值的地方少了(说明这行提示被删了?)");
        foreach (var l in lines)
            Assert.True(l.Contains("CompareBuildText(", StringComparison.Ordinal),
                "这句提示没走 CompareBuildText(会又变成各写各的): " + l);
        // XAML 里那份"还没开始合成就先显示"的默认文案也要是新口径,不许是旧的"生成画面"
        Assert.Contains("Text=\"正在后台合成对比片…\"", Xaml);
    }

    /// <summary>④ 视角选择器的提示里要说清"哪个要等、哪个不用等" —— 这正是用户困惑的来源。</summary>
    [Fact]
    public void The_view_tooltip_tells_which_view_waits()
    {
        var m = Regex.Match(Xaml, "PreviewViewRadios[^>]*ToolTipService\\.ToolTip=\"([^\"]*)\"", RegexOptions.Singleline);
        Assert.True(m.Success, "找不到视角选择器的提示文字");
        var tip = m.Groups[1].Value;
        Assert.Contains("后台", tip);
        Assert.Contains("不用等", tip);
    }

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
