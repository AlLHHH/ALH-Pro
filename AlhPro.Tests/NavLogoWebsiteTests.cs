using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace AlhPro.Tests;

/// <summary>「打开官方网站」这条链路的接线(用户 2026-09-16):**左上角「图标 + 软件名」整块**可点,
/// 之后又加了**左下角「官方网站」按钮** —— 两个入口共用一个去处。
/// 【为什么单测它】这类接线编译期一件都查不出来:
/// ① 忘了给 StackPanel 加 Background="Transparent" —— Panel 没有背景时只有子元素参与命中测试,
///    结果「只有图标和字能点、两者之间那段 10px 点不动」,肉眼看很难判断是漏了哪一行;
/// ② XAML 里的事件名与 cs 里的方法名对不上(Tapped="NavLogo_Taped")—— 属于 XAML 编译期错误,
///    而且只在真正点下去时才暴露;
/// ③ 域名写错(少了 https、写成 alhpro.com)照样能编译能运行,只是点开一个不存在的站;
/// ④ 第二个入口最容易被写成"再抄一份 Process.Start" —— 抄完两处就开始各自漂移(改一处漏一处),
///    所以钉住"真正干活只有 OpenWebsite 一处、两个入口都调它"。
/// 这四件事全部只能钉在源码层。</summary>
public class NavLogoWebsiteTests
{
    /// <summary>官方站地址。软件源码里本没有官网域名(只有 GitHub 链接),这里是唯一出处。</summary>
    private const string Website = "https://alhpro.cn/";

    /// <summary>整块可点:事件要挂在 NavLogo 这块 StackPanel 上,而且必须有透明背景(否则空区点不到)。</summary>
    [Fact]
    public void Logo_block_is_the_click_target_and_is_hit_testable()
    {
        var xaml = ReadRepoFile("ImgUpscalerUI", "Views", "MainPage.xaml");

        Assert.Contains("x:Name=\"NavLogo\"", xaml);
        // 透明背景 = 让「图标与文字之间的间距」也能命中;漏了它就只有子元素本身能点
        Assert.Contains("Background=\"Transparent\"", xaml);
        // 纯文字 + 小图标看起来完全不像链接,必须给一句提示才知道能点
        Assert.Contains("官网", xaml);

        // 三个事件必须挂在 NavLogo 那一个元素的开标签里(挂到别处 = 点了没反应/悬停没反馈)
        var open = Regex.Match(xaml, "<StackPanel x:Name=\"NavLogo\"[\\s\\S]*?>");
        Assert.True(open.Success, "找不到 x:Name=\"NavLogo\" 的 StackPanel 开标签");
        Assert.Contains("Tapped=\"NavLogo_Tapped\"", open.Value);
        Assert.Contains("PointerEntered=\"NavLogo_PointerEntered\"", open.Value);
        Assert.Contains("PointerExited=\"NavLogo_PointerExited\"", open.Value);

        // 图标与软件名仍要落在这块「可点区域」之后(整块可点不能把它俩搬出这块 / 改名)
        Assert.Contains("x:Name=\"NavLogoIcon\"", xaml);
        Assert.Contains("Text=\"ALH Pro\"", xaml);
        Assert.True(xaml.IndexOf("x:Name=\"NavLogoIcon\"", StringComparison.Ordinal)
                    > xaml.IndexOf("x:Name=\"NavLogo\"", StringComparison.Ordinal),
            "图标必须排在 NavLogo 之后,即仍在这块可点区域里");
        Assert.True(xaml.IndexOf("Text=\"ALH Pro\"", StringComparison.Ordinal)
                    > xaml.IndexOf("x:Name=\"NavLogo\"", StringComparison.Ordinal),
            "软件名必须排在 NavLogo 之后,即仍在这块可点区域里");
    }

    /// <summary>左下角第二个入口(「官方网站」按钮):要和左上角走同一条路,不许另抄一份跳转代码。</summary>
    [Fact]
    public void Bottom_nav_website_button_uses_the_same_handler_as_the_logo()
    {
        var xaml = ReadRepoFile("ImgUpscalerUI", "Views", "MainPage.xaml");

        // 【2026-09-23 改锚点】用户要求「左侧功能区要有图标」⇒ 这个按钮的内容从纯字符串
        // `Content="官方网站"` 变成了「图标 + 文字」面板。**Content 变成面板后 UIA 的 Name 不再自动
        // 等于文字**,所以这一项现在显式写着 AutomationProperties.Name="官方网站" —— 正好当新锚点用。
        Assert.Contains("AutomationProperties.Name=\"官方网站\"", xaml);
        Assert.Contains("Click=\"Website_Click\"", xaml);

        // 按钮自己那一段里必须有悬停提示:一排按钮里没提示的话,它跟「设置」长得没区别
        var btn = Regex.Match(xaml, "<Button Click=\"Website_Click\"[\\s\\S]*?</Button>");
        Assert.True(btn.Success, "找不到「官方网站」按钮的元素体");
        Assert.Contains("ToolTipService.ToolTip=", btn.Value);
        Assert.Contains("alhpro.cn", btn.Value);
    }

    /// <summary>处理器本体:两个入口签名各自对得上 XAML,并且都只转到同一处 OpenWebsite。</summary>
    [Fact]
    public void Both_entries_open_the_official_site_in_the_default_browser()
    {
        var cs = ReadRepoFile("ImgUpscalerUI", "Views", "MainPage.xaml.cs");

        // 签名必须与 XAML 对得上:NavLogo 是 Tapped(传 TappedRoutedEventArgs)、按钮是 Click(传 RoutedEventArgs)
        Assert.Contains(
            "private void NavLogo_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)",
            cs);
        Assert.Contains("private void Website_Click(object sender, RoutedEventArgs e)", cs);
        Assert.Contains("private const string WebsiteUrl = \"" + Website + "\";", cs);

        // ⭐ 真正干活只允许一处:两个入口都 `=> OpenWebsite();`,Process.Start 全链路只此一份
        Assert.Contains("private void OpenWebsite()", cs);
        Assert.Equal(1, Count(cs, @"ProcessStartInfo\(WebsiteUrl\) \{ UseShellExecute = true \}"));
        Assert.Equal(1, Count(cs, @"private void NavLogo_Tapped[^;]+=> OpenWebsite\(\);"));
        Assert.Equal(1, Count(cs, @"private void Website_Click[^;]+=> OpenWebsite\(\);"));

        // 必须走 UseShellExecute:缺了它 .NET 会把 URL 当可执行文件去跑,直接抛异常被 catch 吞掉
        // (这一条由上面那句计数同时保证:整个 MainPage 里带 WebsiteUrl 的 ProcessStartInfo 只有 1 处)

        // 提示语里写的域名 = 真正打开的域名,不能写岔
        Assert.Contains("alhpro.cn", cs);
        Assert.DoesNotContain("alhpro.com", cs);
        Assert.DoesNotContain("http://alhpro.cn", cs);   // 必须 https
    }

    /// <summary>悬停提亮必须成对:只有 Entered 没有 Exited,鼠标移开后这块永远停在暗色。</summary>
    [Fact]
    public void Hover_highlight_is_paired()
    {
        var cs = ReadRepoFile("ImgUpscalerUI", "Views", "MainPage.xaml.cs");
        Assert.Contains("private void NavLogo_PointerEntered", cs);
        Assert.Contains("private void NavLogo_PointerExited", cs);
        Assert.Equal(1, Count(cs, @"NavLogo\.Opacity = 0\.7;"));
        Assert.Equal(1, Count(cs, @"NavLogo\.Opacity = 1\.0;"));
    }

    private static int Count(string text, string pattern) => Regex.Matches(text, pattern).Count;

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
