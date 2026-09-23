using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【2026-09-23 用户要求】「1:1 对比」功能**整套删除**(原话:「1:1对比这个功能简直没有用 删掉他」)。
/// 它 2026-09-19 才加上("静止帧原画质对比":在原片与成片各取一帧、按各自原始像素 1:1 并排),
/// 用户实测觉得没用 ⇒ 连入口按钮、整屏面板、code-behind 实现、以及**指向它的提示文案**一起清掉。
///
/// 钉四件事:
///   ① XAML 里入口按钮与整屏面板都不在了;
///   ② code-behind 的实现方法/字段全清(含只被它调用的私有助手 CropFrameAsync / LoadPngAsync);
///   ③ **不许再有文案推荐用户去用它** —— 删了功能却留着"看细节用「1:1 对比」"= 叫人去点不存在的按钮,
///      这是删功能最容易漏的一处,所以单独钉;
///   ④ 相邻的三个对比模式(两者同时 / 左右对比 / 慢放)必须**还在** —— 别把好功能一起误删。
///
/// 【为什么用"带签名的代码形态"而不是光秃秃的方法名】删除原因写在同文件的注释里,注释会提到这些名字
/// (比如"OpenPixelCompareAsync / CropFrameAsync 已删"),用裸名字断言会被自己的注释判失败;
/// 所以断言的是 `Task xx()` / `void xx_Click(` 这种**只有真代码才会有**的形态。
///
/// **诚实边界**:契约只能证明代码里没有残留,证明不了"界面上确实看不到那个按钮了" ——
/// 后者由 2026-09-23 的真机截图验过(见交接清单)。</summary>
public class PixelCompareRemovedTests
{
    [Fact]
    public void The_1to1_compare_ui_is_gone()
    {
        var xaml = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml");
        Assert.DoesNotContain("x:Name=\"PixelCmpBtn\"", xaml);      // 入口按钮
        Assert.DoesNotContain("x:Name=\"PixelCmpPanel\"", xaml);    // 整屏面板
        Assert.DoesNotContain("x:Name=\"PixelCmpArea\"", xaml);
        Assert.DoesNotContain("Click=\"PixelCmpBtn_Click\"", xaml);
        Assert.DoesNotContain("PointerPressed=\"PixelCmp_PointerPressed\"", xaml);
        Assert.DoesNotContain("Content=\"1:1 对比\"", xaml);
        Assert.DoesNotContain("1:1 原画质对比", xaml);              // 面板标题
    }

    [Fact]
    public void The_1to1_compare_code_is_gone()
    {
        var cs = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
        foreach (var signature in new[]
        {
            "void PixelCmpBtn_Click(",
            "void PixelCmpRecap_Click(",
            "void PixelCmpClose_Click(",
            "void PixelCmpCell_SizeChanged(",
            "void PixelCmp_PointerPressed(",
            "Task OpenPixelCompareAsync()",
            "void ClosePixelCompare()",
            "Task RefreshPixelCompareAsync()",
            "double PixelCmpCurrentAbsSeconds()",
            "int CropOrigin(",
            // 下面两个私有助手只被 1:1 对比调用 ⇒ 一并删(否则就是死代码)
            "Task<string?> CropFrameAsync(",
            "Task<BitmapImage?> LoadPngAsync(",
        })
            Assert.DoesNotContain(signature, cs);
        Assert.DoesNotContain("private readonly List<string> _pxTemps", cs);
        Assert.DoesNotContain("_pxOrigW", cs);
        Assert.DoesNotContain("bool _pxOpen", cs);
    }

    /// <summary>★ 删了功能就得改文案:再也不许出现"看细节用「1:1 对比」"这种指引。</summary>
    [Fact]
    public void No_hint_tells_the_user_to_use_the_deleted_feature()
    {
        var cs = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
        Assert.DoesNotContain("细节用「1:1 对比」", cs);
        Assert.DoesNotContain("看细节用「1:1 对比」", cs);
        Assert.DoesNotContain("用「1:1 对比」", cs);
        var xaml = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml");
        Assert.DoesNotContain("用「1:1 对比」", xaml);
        // 提示里那条**可执行**的建议要还在(只是把"看细节用 1:1 对比"那半句去掉了)
        Assert.Contains("建议用 2x 预览", cs);
    }

    /// <summary>★ 相邻的三个对比模式不许被误删。</summary>
    [Fact]
    public void The_neighbouring_compare_modes_are_untouched()
    {
        var xaml = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml");
        Assert.Contains("x:Name=\"ViewBothRadio\"", xaml);      // 两者同时
        Assert.Contains("x:Name=\"ViewSplitRadio\"", xaml);     // 左右对比
        Assert.Contains("x:Name=\"CmpRate2Btn\"", xaml);        // 慢动作 2×
        Assert.Contains("x:Name=\"CmpRate8Btn\"", xaml);        // 慢动作 8×
        var cs = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
        Assert.Contains("void CmpRate_Click(", cs);
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
