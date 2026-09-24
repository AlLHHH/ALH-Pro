using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace AlhPro.Tests;

/// <summary>左侧导航图标契约(2026-09-23 用户要求:「左侧功能区要有图标,这样子就能很显眼的知道功能」;
/// 随后又明确划了范围:「我说的图标不是下面那里 删掉 是只有上面功能区」)。
///
/// **契约 = 只有上面 4 个功能项有图标,底部那排入口没有图标**。三条纪律(都不会编译报错,只能靠契约兜住):
///   ① 图标字体必须走主题资源:写死 `Segoe Fluent Icons` 在 Win10(本软件最低支持 19041)上整排显示成方块,
///      而开发机是 Win11 ⇒ 完全看不出来;
///   ② 4 个功能项的 `AutomationProperties.Name` 必须显式写:Content 从纯字符串变成 `StackPanel` 之后,
///      UIA 的 Name 不再自动等于文字(读屏软件念不出来、`uia_drv` 只读验证也找不到它);
///   ③ 底部那排**不许**再冒出图标来 —— 用户已明说过不要;这条钉住是为了防止"顺手统一风格"又被加回去。
///
/// 【2026-09-23 视频抠图下线】原来第 3 项那个功能页(图标 E77B/人像)随功能整条删除 ⇒ 数量由 5 变 4,
/// 相关断言同步收紧(见 StartupPageMapTests.The_video_matting_feature_is_gone)。
///
/// 口径与 PixelCompareRemovedTests 一致:读仓库源文件做字符串判据(不是跑界面)。
/// **诚实边界**:契约只能证明"XAML 里配了这个字形",证明不了"从本机字体真能渲染出那个图形" ——
/// 后者由 2026-09-23 的真机截图逐个看过(见交接清单)。</summary>
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

    /// <summary>★ 上面 4 个功能项各自一个**指定**字形(挂错/重复都算不合格)。
    /// 【为什么要写死映射,而不只数个数】只数个数的话,把「音频处理」的图标抄成「视频处理」的照样过 ——
    /// 那正是"看不出功能"的病根。想换字形就同时改这里,改动留痕。</summary>
    [Fact]
    public void The_main_functions_each_pin_their_own_glyph()
    {
        var nav = NavListBlock(ReadRepoFile("ImgUpscalerUI", "Views", "MainPage.xaml"));
        // 窗口取 600:这一段的注解不短,窗口太小会漏项(2026-09-23 第一版就是这么失败的)。
        var map = Regex.Matches(nav,
                "AutomationProperties\\.Name=\"([^\"]+)\"[\\s\\S]{0,600}?Glyph=\"&#x([0-9A-Fa-f]{4});\"")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.ToUpperInvariant());
        var expected = new Dictionary<string, string>
        {
            ["图片放大"] = "E8B9",   // 照片
            ["图片抠图"] = "E8C6",   // 剪刀
            ["视频处理"] = "E714",   // 摄像机
            ["音频处理"] = "E8D6",   // 音频
        };
        Assert.Equal(expected.Count, map.Count);                       // 4 个功能项一个不漏
        Assert.Equal(expected.Count, map.Values.Distinct().Count());   // 字形互不重复
        foreach (var (label, glyph) in expected)
        {
            Assert.True(map.TryGetValue(label, out var got), $"导航缺少功能项或没配图标:{label}");
            Assert.Equal(glyph, got);
        }
    }

    /// <summary>★ 底部那排入口**不许**有图标(用户 2026-09-23:「我说的图标不是下面那里 删掉」)。
    /// 同时钉住它们仍是纯文字按钮(Content 是字符串)⇒ UIA 名字自动等于文字,不依赖 Accessibility 属性。</summary>
    [Fact]
    public void The_bottom_entries_have_no_icons()
    {
        var xaml = ReadRepoFile("ImgUpscalerUI", "Views", "MainPage.xaml");
        var bottom = BottomEntriesBlock(xaml);
        Assert.DoesNotContain("<FontIcon", bottom);
        foreach (var label in new[] { "请作者喝咖啡", "ALH Pro 社区", "官方网站", "使用教程", "设置", "关于" })
            Assert.Contains($"Content=\"{label}\"", bottom);
    }

    /// <summary>整个文件里只有 4 个功能区图标 —— 除广告卡的 `TipIcon`(它不是功能区入口,不归本契约管)。</summary>
    [Fact]
    public void Only_the_nav_list_carries_icons()
    {
        var xaml = ReadRepoFile("ImgUpscalerUI", "Views", "MainPage.xaml");
        Assert.Equal(4, Regex.Matches(NavListBlock(xaml), "<FontIcon ").Count);
        Assert.Equal(5, Regex.Matches(xaml, "<FontIcon ").Count);   // 4 个功能区 + 广告卡 TipIcon
        // 颜色:单色浅灰(用户选定「字体图标,全部单色」),别哪天又被改成彩色
        Assert.Equal(4, Regex.Matches(NavListBlock(xaml), "<FontIcon [^>]*Foreground=\"#A6B1C2\"").Count);
    }

    /// <summary>★ 4 个功能项的 `AutomationProperties.Name` 必须显式写(Content 变面板后 UIA 名字会丢)。</summary>
    [Fact]
    public void Main_nav_items_keep_their_accessible_names()
    {
        var nav = NavListBlock(ReadRepoFile("ImgUpscalerUI", "Views", "MainPage.xaml"));
        foreach (var name in new[] { "图片放大", "图片抠图", "视频处理", "音频处理" })
            Assert.Contains($"AutomationProperties.Name=\"{name}\"", nav);
    }

    /// <summary>上面那段:导航 `ListView`(4 个功能项)。</summary>
    private static string NavListBlock(string xaml)
    {
        int a = xaml.IndexOf("<ListView x:Name=\"NavList\"", StringComparison.Ordinal);
        int b = xaml.IndexOf("<!-- 底部署名 + 常用入口 + 关于", StringComparison.Ordinal);
        Assert.True(a >= 0 && b > a, "找不到导航列表那一段");
        return xaml.Substring(a, b - a);
    }

    /// <summary>下面那段:底部入口按钮(`Grid.Row="2"` 那个 StackPanel,到广告卡为止)。</summary>
    private static string BottomEntriesBlock(string xaml)
    {
        int a = xaml.IndexOf("<StackPanel Grid.Row=\"2\"", StringComparison.Ordinal);
        int b = xaml.IndexOf("<!-- ============ 左栏底部「广告」动态区", StringComparison.Ordinal);
        Assert.True(a >= 0 && b > a, "找不到底部入口那一段");
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
