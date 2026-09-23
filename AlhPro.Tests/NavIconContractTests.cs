using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace AlhPro.Tests;

/// <summary>左侧导航图标契约(2026-09-23 用户要求:「左侧功能区要有图标,这样子就能很显眼的知道功能」)。
///
/// 【为什么要钉这几条】它们**都不会编译报错**,只能靠契约兜住:
///   ① 图标字体写死 `Segoe Fluent Icons`:那是 Win11 才有的字体,本软件最低支持 Win10 19041
///      —— 写死以后在 Win10 上整排图标显示成方块,而开发机(Win11)上完全正常、看不见问题;
///   ② 按钮/列表项的 `Content` 从纯字符串变成 `StackPanel` 之后,UIA 的 Name 不再自动等于文字
///      ⇒ 变成"无名按钮"(读屏软件念不出来、我们的 `uia_drv` 也只读验证不到它);
///   ③ 以后新加一个页面/入口忘了配图标,或者把两个功能的图标抄成同一个 —— 界面照样跑,
///      只是那一项又变回"看不出是什么功能"。
///
/// 口径与 `VideoMattingPageContractTests` 一致:读仓库源文件做字符串判据(不是跑界面)。
/// **诚实边界**:契约只能证明"XAML 里配了这个字形",证明不了"从本机字体真能渲染出那个图形"
/// —— 后者由 2026-09-23 的真机截图验过(Win11 上 11 个图标逐个看过,见交接清单)。</summary>
public class NavIconContractTests
{
    /// <summary>图标必须走主题字体资源(内含 "Segoe Fluent Icons, Segoe MDL2 Assets" 回退链)。</summary>
    [Fact]
    public void Icons_use_the_theme_font_resource_so_Win10_does_not_show_boxes()
    {
        var xaml = ReadRepoFile("ImgUpscalerUI", "Views", "MainPage.xaml");
        Assert.Contains("FontFamily=\"{ThemeResource SymbolThemeFontFamily}\"", xaml);
        // 只禁"当成属性写死"这一种;注释里提到这个字体名是允许的(注释本来就在解释为什么不能用它)。
        Assert.DoesNotContain("FontFamily=\"Segoe Fluent Icons\"", xaml);
        Assert.DoesNotContain("FontFamily=\"Segoe MDL2 Assets\"", xaml);
    }

    /// <summary>★ 左侧功能区 11 个入口各自一个**指定**字形(重复或挂错都算不合格)。
    /// 【为什么要写死映射,而不只数个数】只数个数的话,把「音频处理」的图标抄成「视频处理」的照样过 ——
    /// 那正是"看不出功能"的病根。想换字形就同时改这里,改动留痕。</summary>
    [Fact]
    public void Each_sidebar_entry_pins_its_own_glyph()
    {
        var sidebar = SidebarBlocks(ReadRepoFile("ImgUpscalerUI", "Views", "MainPage.xaml"));
        // 窗口取 600:按钮那几项的 ToolTip 文字不短("ALH Pro 官方网站:下载最新版 / 使用教程 / 更新日志…"),
        // 用 240 会漏掉带长提示的 3 个入口(2026-09-23 第一版就是这么失败的)。
        var map = Regex.Matches(sidebar,
                "AutomationProperties\\.Name=\"([^\"]+)\"[\\s\\S]{0,600}?Glyph=\"&#x([0-9A-Fa-f]{4});\"")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.ToUpperInvariant());
        var expected = new Dictionary<string, string>
        {
            ["图片放大"] = "E8B9",     // 照片
            ["图片抠图"] = "E8C6",     // 剪刀
            ["视频抠图"] = "E77B",     // 人像(抠的就是人/主体,与"图片抠图"的剪刀区分开)
            ["视频处理"] = "E714",     // 摄像机
            ["音频处理"] = "E8D6",     // 音频
            ["请作者喝咖啡"] = "EB51",  // 爱心(赞助)
            ["ALH Pro 社区"] = "E716",  // 人群
            ["官方网站"] = "E774",     // 地球
            ["使用教程"] = "E7BE",     // 学业帽
            ["设置"] = "E713",         // 齿轮
            ["关于"] = "E946",         // 信息
        };
        Assert.Equal(expected.Count, map.Count);                       // 11 个入口一个不漏
        Assert.Equal(expected.Count, map.Values.Distinct().Count());   // 字形互不重复
        foreach (var (label, glyph) in expected)
        {
            Assert.True(map.TryGetValue(label, out var got), $"左侧栏缺少入口或没配图标:{label}");
            Assert.Equal(glyph, got);
        }
    }

    /// <summary>图标是单色浅灰(用户 2026-09-23 选定「字体图标,全部单色」),别哪天又被改成彩色。
    /// 只看左侧功能区那两段 —— 广告卡里的 `TipIcon` 不是功能区入口,不归本契约管。</summary>
    [Fact]
    public void Icons_are_one_single_light_grey_colour()
    {
        var sidebar = SidebarBlocks(ReadRepoFile("ImgUpscalerUI", "Views", "MainPage.xaml"));
        int icons = Regex.Matches(sidebar, "<FontIcon ").Count;
        int grey = Regex.Matches(sidebar, "<FontIcon [^>]*Foreground=\"#A6B1C2\"").Count;
        Assert.Equal(11, icons);
        Assert.Equal(icons, grey);
    }

    /// <summary>★ `AutomationProperties.Name` 必须显式写:Content 变成面板之后 UIA 的 Name 不再自动
    /// 等于文字(2026-09-23 改完之后实测 `uia_drv tree` 仍能按名字找到这 11 个入口,就是靠这一条)。</summary>
    [Fact]
    public void Sidebar_entries_keep_their_accessible_names()
    {
        var xaml = ReadRepoFile("ImgUpscalerUI", "Views", "MainPage.xaml");
        foreach (var name in new[] { "图片放大", "图片抠图", "视频抠图", "视频处理", "音频处理",
                                     "请作者喝咖啡", "ALH Pro 社区", "官方网站", "使用教程", "设置", "关于" })
            Assert.Contains($"AutomationProperties.Name=\"{name}\"", xaml);
    }

    /// <summary>左侧功能区的两段(5 个主功能 + 底部 6 个入口)。广告卡那块不算。</summary>
    private static string SidebarBlocks(string xaml)
    {
        int a = xaml.IndexOf("<ListView x:Name=\"NavList\"", StringComparison.Ordinal);
        int b = xaml.IndexOf("<!-- ============ 左栏底部「广告」动态区", StringComparison.Ordinal);
        Assert.True(a >= 0 && b > a, "找不到左侧栏的两段(导航列表 / 底部入口)");
        return xaml.Substring(a, b - a);
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
