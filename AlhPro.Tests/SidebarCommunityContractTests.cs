using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>
/// 【2026-11-15 用户要求】左下角「ALH Pro 社区」(爱发电社区)改成官方 Q 群反馈,
/// 同时「使用教程」板块整条下线。
///
/// 这些断言守的是「界面上到底有哪几个入口」——用户点得到的按钮才叫功能,
/// 按钮文案写着 A、点开却是 B,是桌面软件里最难自己发现的那类错误。
/// </summary>
public class SidebarCommunityContractTests
{
    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            if (File.Exists(Path.Combine(d.FullName, "ImgUpscalerUI", "Views", "MainPage.xaml"))) return d.FullName;
            d = d.Parent;
        }
        throw new FileNotFoundException("找不到仓库根目录(没有 ImgUpscalerUI\\Views\\MainPage.xaml)");
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

    /// <summary>侧栏底部那排入口:只留「官方Q群」,社区/爱发电/教程三个入口都不许再出现。</summary>
    [Fact]
    public void The_sidebar_offers_the_qq_group_and_no_longer_the_tutorial()
    {
        var xaml = Read("ImgUpscalerUI", "Views", "MainPage.xaml");
        Assert.Contains("Content=\"官方Q群\"", xaml);
        Assert.Contains("Click=\"Community_Click\"", xaml);
        Assert.Contains("767832870", xaml);                    // 群号直接写在提示里,用户不必先点开才知道
        Assert.DoesNotContain("Content=\"使用教程\"", xaml);
        Assert.DoesNotContain("Tutorial_Click", xaml);
        Assert.DoesNotContain("Content=\"ALH Pro 社区\"", xaml);
    }

    /// <summary>点「官方Q群」= 弹群二维码 + 群号,不再跳爱发电;二维码图必须随包。</summary>
    [Fact]
    public void The_qq_group_click_shows_a_qr_code_instead_of_opening_a_web_page()
    {
        var cs = Read("ImgUpscalerUI", "Views", "MainPage.xaml.cs");
        Assert.Contains("private const string QqGroupNumber = \"767832870\";", cs);
        Assert.Contains("ms-appx:///Assets/qrcode_qq.png", cs);
        Assert.DoesNotContain("ifdian.net/group/", cs);          // 爱发电「社区」入口已下线(赞助页 al/AlL666 保留)
        Assert.DoesNotContain("new TutorialView(", cs);          // 教程页已删,不该再被 new 出来
        Assert.DoesNotContain("private void Tutorial_Click", cs);
    }

    /// <summary>二维码图真的存在、够大(裂图/占位图那种几十字节的文件一眼能看出来)。</summary>
    [Fact]
    public void The_group_qr_code_ships_with_the_app()
    {
        var png = Path.Combine(RepoRoot(), "ImgUpscalerUI", "Assets", "qrcode_qq.png");
        Assert.True(File.Exists(png), "缺少 Assets\\qrcode_qq.png,弹窗里会是裂图");
        Assert.True(new FileInfo(png).Length > 20_000, "二维码图太小,可能是占位图");

        var csproj = Read("ImgUpscalerUI", "ImgUpscalerUI.csproj");
        Assert.Contains("Assets\\qrcode_qq.png", csproj);
        Assert.Contains("CopyToPublishDirectory=\"PreserveNewest\"", csproj);   // 不复制 = 发布版里没有这张图
    }

    /// <summary>教程板块下线要落到底:文件、打包项、安装脚本的提示语一起清掉。</summary>
    [Fact]
    public void The_tutorial_is_gone_from_the_repo_the_package_and_the_installer()
    {
        Assert.False(File.Exists(Path.Combine(RepoRoot(), "使用教程.md")), "教程文件还在");
        Assert.False(File.Exists(Path.Combine(RepoRoot(), "ImgUpscalerUI", "Views", "TutorialView.xaml")), "教程页 XAML 还在");

        var csproj = Read("ImgUpscalerUI", "ImgUpscalerUI.csproj");
        Assert.DoesNotContain("Link=\"使用教程.md\"", csproj);   // 打包项已删(注释里提到文件名不算)

        foreach (var iss in new[] { "installer.iss", "installer_full.iss", "installer_nocut.iss" })
        {
            var text = Read(iss);
            Assert.DoesNotContain("软件内「使用教程」", text);
        }
    }
}