using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【2026-09-23 视频抠图页欠账】三条:① 输入框右侧色块 + 焦点离开归一化(前两次因**脚本改 XAML**
/// 锚点不匹配而回退 ⇒ 这次整份重写 XAML);② 框选(与 VideoView 的最后一个真差异);
/// ③ 清掉 .cs 里的死方法 `SetRunningOld`。
///
/// 【为什么只能这么钉】WinUI 的界面逻辑跑不起来(需要 UI 线程 + 真窗口),所以这里钉两样**可检查**的东西:
///   · 纯逻辑部分交给真正的单测(<see cref="HexColorTests"/>)—— "归一化"的判据全在那里;
///   · 接线部分钉源码 token(与仓库既有的 Anime4kWiringTests / PreviewPlaybackDiagnosticsTests 同一手法)。
/// **诚实边界**:token 测试只能证明"代码里有这条接线",不能证明"点下去真的按预期动" ——
/// 那部分见交接清单里的"必须人工验"清单。</summary>
public class VideoMattingPageContractTests
{
    [Fact]
    public void Xaml_was_rewritten_whole_file_not_patched()
    {
        // 【纪律】上一轮在 XAML 上用脚本字符串补丁,坏了 4 次(改坏闭合标签 / 锚点不匹配)。
        // 这份文件是整份重写的,所以结构标记必须完整且唯一。
        var xaml = ReadRepoFile("ImgUpscalerUI", "Views", "VideoMattingView.xaml");
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\" ?>", xaml);
        Assert.Contains("x:Class=\"ALHPro.Views.VideoMattingView\"", xaml);
        // 【行尾】仓库里是 LF(PowerShell 的 `Out-File` 会把 LF 显示成 CRLF,所以别用管道测行尾)。
        // 这里只要求"以闭合标签结尾",不咬死行尾字符 —— 免得把一次无关的行尾规范化变成失败。
        Assert.EndsWith("</UserControl>", xaml.TrimEnd('\r', '\n', ' ', '\t'));
        Assert.True(xaml.Contains("</UserControl>\n") || xaml.Contains("</UserControl>\r\n"), "末尾必须有换行");
        Assert.Equal(1, CountOf(xaml, "<UserControl"));
        Assert.Equal(1, CountOf(xaml, "</UserControl>"));
        // 左右两栏的主 Grid 仍然存在(结构没被改坏)
        Assert.Contains("<ColumnDefinition Width=\"322\"/>", xaml);
        Assert.Contains("x:Name=\"TaskList\"", xaml);
    }

    /// <summary>★ ①色块必须在**输入框右侧**(同一行 Grid 的两列),而不是像以前那样在输入框上面另起一行。</summary>
    [Fact]
    public void Color_swatch_sits_to_the_right_of_the_hex_box()
    {
        var xaml = ReadRepoFile("ImgUpscalerUI", "Views", "VideoMattingView.xaml");
        int box = xaml.IndexOf("x:Name=\"BgColorBox\"", StringComparison.Ordinal);
        int swatch = xaml.IndexOf("x:Name=\"SwatchCurrent\"", StringComparison.Ordinal);
        int swatchFill = xaml.IndexOf("x:Name=\"SwatchCurrentFill\"", StringComparison.Ordinal);
        Assert.True(box > 0 && swatch > box, "色块必须在输入框之后(即右侧那一列)");
        Assert.True(swatchFill > swatch, "色块里要有一个能被 code-behind 改 Background 的 Border");
        // 两列布局:输入框占 * (可伸缩),色块固定 46
        int gridOpen = xaml.LastIndexOf("<Grid ColumnSpacing=\"6\">", box, StringComparison.Ordinal);
        Assert.True(gridOpen > 0 && gridOpen < box, "输入框与色块必须在同一个两列 Grid 里");
        Assert.Contains("<ColumnDefinition Width=\"*\"/>", xaml.Substring(gridOpen, box - gridOpen));
        Assert.Contains("<ColumnDefinition Width=\"46\"/>", xaml.Substring(gridOpen, box - gridOpen));
        // 输入框仍然带 LostFocus(归一化的触发点)与 KeyDown(回车也归一化)
        Assert.Contains("LostFocus=\"BgColorBox_LostFocus\"", xaml);
        Assert.Contains("KeyDown=\"BgColorBox_KeyDown\"", xaml);
        // 调色板挂在这个色块上(点色块就开调色板),不再是另一个独立按钮
        int flyout = xaml.IndexOf("<Button.Flyout>", swatch, StringComparison.Ordinal);
        int gridClose = xaml.IndexOf("</Grid>", swatch, StringComparison.Ordinal);
        Assert.True(flyout > 0 && flyout < gridClose, "调色板 Flyout 必须挂在这个色块按钮上");
        Assert.Contains("<ColorPicker x:Name=\"BgColorPicker\"", xaml);
        // 提示行(归一化失败时显示原因)
        Assert.Contains("x:Name=\"BgColorHint\"", xaml);
    }

    /// <summary>归一化的三条纪律:唯一写入口 / 归一化后写回 / 失败退回上一个有效值(绝不静默变黑)。</summary>
    [Fact]
    public void Color_normalization_has_one_write_path_and_never_falls_back_to_black()
    {
        var code = StripComments(ReadRepoFile("ImgUpscalerUI", "Views", "VideoMattingView.xaml.cs"));
        Assert.Contains("private void SetBgColor(string hex)", code);
        Assert.Contains("private void NormalizeBgColorText()", code);
        Assert.Contains("private void BgColorBox_LostFocus(object sender, RoutedEventArgs e) => NormalizeBgColorText();", code);
        Assert.Contains("if (e.Key != Windows.System.VirtualKey.Enter) return;", code);
        // 三个入口都走同一个写入口(色块 / 调色板 / 归一化)
        Assert.Contains("if (sender is Button b && b.Tag is string hex) SetBgColor(hex);", code);
        Assert.Contains("SetBgColor(AlhPro.Core.HexColor.Format(args.NewColor.R, args.NewColor.G, args.NewColor.B));", code);
        // 失败回退:先退回上一个有效值,再提示 —— 顺序不能反(先 SetBgColor 会把提示收起来)
        int fallback = code.IndexOf("SetBgColor(_lastValidBgColor);", StringComparison.Ordinal);
        int hint = code.IndexOf("BgColorHint.Text = $\"「{raw}」不是有效颜色", StringComparison.Ordinal);
        Assert.True(fallback > 0 && hint > fallback, "必须先把值退回上一个有效色,再给出提示");
        Assert.Contains("private string _lastValidBgColor = \"#1E3A5F\";", code);
        // 下发前也要归一化(用户可能没失焦就点了「开始处理」)
        Assert.Contains("BackgroundColor: AlhPro.Core.HexColor.NormalizeOr(BgColorBox.Text, _lastValidBgColor),", code);
        // 色块显示要跟着走(否则"框里一个色、块上另一个色")
        Assert.Contains("SwatchCurrentFill.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));", code);
        // 初始化时同步一次
        Assert.Contains("SetBgColor(BgColorBox.Text);", code);
    }

    /// <summary>下游解析口径必须与界面同一份(<see cref="AlhPro.Core.HexColor"/>),否则"界面归一化好了、
    /// 处理端又按老规则判非法" ⇒ 还是变黑。</summary>
    [Fact]
    public void Service_parses_colors_with_the_same_core_helper()
    {
        var svc = StripComments(ReadRepoFile("ImgUpscalerUI", "VideoMattingService.cs"));
        Assert.Contains("if (AlhPro.Core.HexColor.TryParseRgb(hex, out byte r, out byte g, out byte b))", svc);
        Assert.Contains("return new byte[] { 0, 0, 0 };", svc);   // 真非法仍然回退黑色(但界面会先提示)
        Assert.DoesNotContain("s.Length == 6", svc);              // 旧的"只认 6 位"判据必须消失
    }

    /// <summary>★ ②框选:四个指针处理器必须挂在宿主 Grid 上,橡皮筋画在宿主里的 Canvas 上
    /// (照 VideoView 的 VideoGridHost —— 它是本页与视频页此前的最后一个真差异)。</summary>
    [Fact]
    public void Rubber_band_selection_matches_the_video_page_recipe()
    {
        var xaml = ReadRepoFile("ImgUpscalerUI", "Views", "VideoMattingView.xaml");
        var code = StripComments(ReadRepoFile("ImgUpscalerUI", "Views", "VideoMattingView.xaml.cs"));
        // XAML:宿主 Grid 挂着四个处理器(逐个写清楚,别用循环+条件拼字符串 —— 那样写错了自己都不知道)
        Assert.Contains("x:Name=\"TaskGridHost\"", xaml);
        Assert.Contains("PointerPressed=\"TaskGridHost_PointerPressed\"", xaml);
        Assert.Contains("PointerMoved=\"TaskGridHost_PointerMoved\"", xaml);
        Assert.Contains("PointerReleased=\"TaskGridHost_PointerReleased\"", xaml);
        Assert.Contains("PointerCaptureLost=\"TaskGridHost_PointerCaptureLost\"", xaml);
        // 橡皮筋矩形 + 不吃指针的 Canvas
        Assert.Contains("x:Name=\"RbRectM\"", xaml);
        Assert.Contains("<Canvas IsHitTestVisible=\"False\"", xaml);
        // code-behind:四个处理器 + 三条纪律
        foreach (var m in new[] { "private void TaskGridHost_PointerPressed(",
                                  "private void TaskGridHost_PointerMoved(",
                                  "private void TaskGridHost_PointerReleased(",
                                  "private void TaskGridHost_PointerCaptureLost(",
                                  "private bool IsPressOnTaskItem(Windows.Foundation.Point pt)",
                                  "private void UpdateRbRectM(Windows.Foundation.Point cur)",
                                  "private void ApplyRubberSelectionM()",
                                  "private static bool RectIntersectsM(" })
            Assert.Contains(m, code);
        Assert.Contains("if (IsPressOnTaskItem(e.GetCurrentPoint(TaskGridHost).Position)) return;", code);
        Assert.Contains("TaskGridHost.CapturePointer(e.Pointer);", code);
        Assert.Contains("private const double RbThresholdM = 4;", code);
        Assert.Contains("TaskList.SelectedItems.Clear();   // 单击空白 = 取消选中", code);
        // 框选完要把按钮状态刷新掉(否则框了一堆「删除选中」还是灰的)
        Assert.Contains("UpdateButtons();   // 框选完要让「删除选中」亮起来", code);
    }

    /// <summary>★ ③死方法必须清掉(它就是"两个 SetRunning"里那个没人调的)。</summary>
    [Fact]
    public void The_dead_SetRunningOld_is_gone()
    {
        var code = ReadRepoFile("ImgUpscalerUI", "Views", "VideoMattingView.xaml.cs");
        Assert.DoesNotContain("SetRunningOld", code);
        Assert.Contains("private void SetRunning(bool running) => UpdateButtons();", code);
    }

    /// <summary>★ ④【开发中】2026-09-23 用户要求:这一页要标成"开发中/敬请期待"、背景模糊。
    /// 契约 = ① XAML 里有那一层磨砂 + 居中横幅(文案、AcrylicBrush 都得在);
    ///        ② 代码后端构造函数末尾把**整页**禁用(锁死输入),而且必须锁在初始化之后。
    /// 为什么要这么细:横幅删了看得见,锁删了**看不见** —— 页面照样显示"开发中"却能点,最坑。</summary>
    [Fact]
    public void The_page_is_locked_behind_a_coming_soon_banner()
    {
        var xaml = ReadRepoFile("ImgUpscalerUI", "Views", "VideoMattingView.xaml");
        var code = StripComments(ReadRepoFile("ImgUpscalerUI", "Views", "VideoMattingView.xaml.cs"));
        // ① 磨砂层 + 横幅文案
        Assert.Contains("x:Name=\"DevBannerOverlay\"", xaml);
        Assert.Contains("<AcrylicBrush", xaml);
        Assert.Contains("视频抠图 · 开发中", xaml);
        Assert.Contains("敬请期待", xaml);
        // 【实测结论,别再照着 UWP 文档写】WinUI 3 的 AcrylicBrush 没有 BackgroundSource 属性,
        //   Grid 也没有 IsEnabled(XamlCompiler 报 WMC0011)。只查"有没有当属性用",注释里提到不算。
        Assert.DoesNotContain("BackgroundSource=\"", xaml);
        // ② 锁:一行禁用整页 —— 本页是 UserControl,而 UserControl 本身就是 Control。
        //    【为什么带上缩进和新行】本文件前面还有 `MuteCheck.IsEnabled = false;`(第 107 行那个),
        //    只找 "IsEnabled = false;" 会先撞上它、把顺序断言判错(第一版就是这么写错的)。
        const string lockLine = "\n        IsEnabled = false;";
        Assert.Contains(lockLine, code);
        int init = code.IndexOf("_ready = true;", StringComparison.Ordinal);
        int lockAt = code.IndexOf(lockLine, StringComparison.Ordinal);
        Assert.True(init >= 0 && lockAt > init,
            "整页禁用必须排在初始化完成(_ready = true)之后,否则页面加载会被自己的锁影响");
    }

    private static int CountOf(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    private static string StripComments(string text)
        => string.Join("\n", text.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));

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
