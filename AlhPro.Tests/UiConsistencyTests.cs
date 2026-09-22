using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace AlhPro.Tests;

/// <summary>界面一致性与"拖放用不了"的接线契约(2026-09-16 两条用户实测反馈)。
///
/// ① **各页面的「强制结束」必须都是红色危险按钮**(#E5484D)。
///    音频页曾经漏了这一行 ⇒ 它看起来跟旁边的普通灰按钮一模一样,用户反馈"音频的结束不是红色"。
///    这类缺陷编译器/运行期都不会报,只能在源码上钉住"四处必须一致"。
///
/// ② **以管理员权限运行时必须被识别并明确告知**。用户反馈"一部分人的电脑拖放素材不行":
///    根因是 Windows 的 UIPI —— 不同完整性级别的进程之间**静默禁止**拖放,提权后的本程序收不到放,
///    用户只看到"拖进去没反应、也没有任何报错"。这事**修不了**(系统限制),能做的只有:
///    识别 + 明确告知 + 给出"点「添加」按钮"这条不受影响的替代路径。
///    本程序自身不需要管理员权限(app.manifest 是 asInvoker,设置写在 %LOCALAPPDATA%)。</summary>
public class UiConsistencyTests
{
    /// <summary>① 四个页面的「强制结束」按钮标签里都必须带红色背景。</summary>
    [Fact]
    public void Every_pages_danger_button_is_red()
    {
        foreach (var f in new[] { "VideoView.xaml", "CutoutView.xaml", "UpscaleView.xaml", "AudioView.xaml" })
        {
            var xaml = ReadRepoFile("ImgUpscalerUI", "Views", f);
            var hits = Regex.Matches(xaml, "Content=\"强制结束\"");
            Assert.True(hits.Count > 0, $"{f} 里找不到「强制结束」按钮");
            foreach (Match m in hits)
            {
                // 只看这个按钮【自己的标签】(从 Content 到该标签的 "/>"),不越到下一个元素
                int end = xaml.IndexOf("/>", m.Index, StringComparison.Ordinal);
                Assert.True(end > m.Index, $"{f} 的「强制结束」按钮标签没有闭合");
                var tag = xaml.Substring(m.Index, end - m.Index);
                Assert.True(tag.Contains("Background=\"#E5484D\"", StringComparison.Ordinal),
                    $"{f} 的「强制结束」不是红色危险按钮(缺 Background=\"#E5484D\")");
            }
        }
    }

    /// <summary>② 提权**只写日志,不上界面**(2026-09-16 用户裁决:「这种提示没有人要」)。
    /// 一度加过"启动弹一次说明 + 拖放区提示",用户明确否掉 —— 契约改成钉住"界面上一字不加",
    /// 免得以后有人觉得"该提醒一下"又把它加回来。</summary>
    [Fact]
    public void Running_as_admin_is_logged_but_never_shown_in_the_ui()
    {
        var app = ReadRepoFile("ImgUpscalerUI", "App.xaml.cs");
        var mp = ReadRepoFile("ImgUpscalerUI", "Views", "MainPage.xaml.cs");

        // 判据保留:进程是否提权(读不到就当普通权限,宁可漏记也不误报)
        Assert.Contains("public static bool IsRunningElevated { get; } = ComputeIsRunningElevated();", app);
        Assert.Contains("WindowsBuiltInRole.Administrator", app);
        // 诊断日志里留一条 —— 用户说"拖不动"时,不必先问他是不是用管理员开的
        Assert.Contains("Windows 会禁止从资源管理器拖入文件(UIPI)", app);

        // 【用户裁决】界面上一个字都不加:不弹窗、不在拖放区写"管理员权限"之类的说明
        Assert.DoesNotContain("ShowElevationNoticeAsync", mp);
        Assert.DoesNotContain("_elevationNoticeShown", mp);
        foreach (var f in new[] { "VideoView.xaml", "CutoutView.xaml", "UpscaleView.xaml", "AudioView.xaml" })
        {
            var xaml = ReadRepoFile("ImgUpscalerUI", "Views", f);
            Assert.DoesNotContain("管理员", xaml);
        }
    }

    /// <summary>③ 预览页(以及整个视频页)**不许再有"按空格"的鼠标悬停提示**(2026-09-22 用户:「预览界面有空格键的
    /// 鼠标悬停提示 这个不要有」)。只删提示文字,**按键功能照旧**(加速键 + PreviewKeyDown 两条入口都留着)——
    /// 所以这条契约查的是"悬停提示文本",不是代码里有没有 Space。</summary>
    [Fact]
    public void No_tooltip_tells_the_user_to_press_space()
    {
        var xaml = Regex.Replace(ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml"),
                                 @"<!--.*?-->", " ", RegexOptions.Singleline);   // 先剥注释:留档注释里提到空格是允许的
        var tips = Regex.Matches(xaml, "ToolTipService\\.ToolTip=\"([^\"]*)\"", RegexOptions.Singleline);
        Assert.True(tips.Count > 10, "这个页面本来有一堆悬停提示,数量对不上说明解析错了");
        foreach (Match m in tips)
            Assert.False(m.Groups[1].Value.Contains("空格", StringComparison.Ordinal),
                "悬停提示里不该再出现\"空格\":" + m.Groups[1].Value);
        // 功能不許被顺手删掉
        var cs = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
        Assert.Contains("PreviewSpaceAccel_Invoked", cs);
        Assert.Contains("Key=\"Space\"", xaml);
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
