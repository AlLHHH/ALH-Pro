using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【2026-09-23 修 A2；同日随视频抠图下线而简化】启动页编号 ↔ 左侧导航项下标 的换算契约。
///
/// 真机实测的病：设置里选「视频处理」→ 重启进「视频抠图」（锁着的"开发中"页）；选「音频处理」→ 进「视频处理」。
/// 根因是**两套编号被当成同一套用**（启动页编号 vs 导航列表书写顺序）—— 当时视频抠图排在导航第 3 项，
/// 而启动页编号里它却是最后一个，于是整条链错位。这里把换算钉死。
///
/// 【2026-09-23 视频抠图整条下线后】导航只剩 4 个功能页，顺序与启动页编号**恰好一致** ⇒ 换算退化成恒等，
/// 但这一层**保留**：① 合法范围(-1 / 0~3)集中在这里；② 以后往导航里插页时，这里是唯一改动点，
/// 改漏了这两条单测就会红。
///
/// 这套单测同时是"改导航项时的防呆":以后谁在 XAML 里挪了左侧导航顺序,`NavIndexFor` 与这条单测必须一起改。</summary>
public class StartupPageMapTests
{
    /// <summary>★ 四个功能页的换算逐条钉住（这条单测就是"导航顺序"的可执行记录）。</summary>
    [Theory]
    [InlineData(0, 0)]   // 图片放大 → 导航第 1 项
    [InlineData(1, 1)]   // 图片抠图 → 导航第 2 项
    [InlineData(2, 2)]   // 视频处理 → 导航第 3 项
    [InlineData(3, 3)]   // 音频处理 → 导航第 4 项
    public void Startup_page_maps_to_the_right_nav_index(int page, int expectedNavIndex)
        => Assert.Equal(expectedNavIndex, AlhPro.Core.StartupPageMap.NavIndexFor(page));

    /// <summary>四个功能页两两映射到**不同的**导航项（映射错了必然撞车）。</summary>
    [Fact]
    public void The_pages_map_to_distinct_nav_items()
    {
        var idx = Enumerable.Range(0, 4).Select(AlhPro.Core.StartupPageMap.NavIndexFor).ToArray();
        Assert.Equal(4, idx.Distinct().Count());
        Assert.All(idx, i => Assert.InRange(i, 0, 3));
    }

    /// <summary>非法编号不能乱跳（一律回"图片放大"）。4 = 已下线的视频抠图 ⇒ 也属于非法。</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(4)]      // ← 老设置里可能存着这个值(视频抠图),现在必须被挡掉
    [InlineData(5)]
    [InlineData(99)]
    [InlineData(int.MinValue)]
    public void Invalid_pages_fall_back_to_the_first_page(int page)
        => Assert.Equal(0, AlhPro.Core.StartupPageMap.NavIndexFor(page));

    /// <summary>Tag ↔ 编号:功能页要能互相还原;非功能页(教程/设置)与已删的 "matting" 必须返回 -1 = 别记。</summary>
    [Fact]
    public void Tags_round_trip_and_non_function_pages_are_not_recorded()
    {
        foreach (var tag in new[] { "upscale", "cutout", "video", "audio" })
        {
            int page = AlhPro.Core.StartupPageMap.PageForTag(tag);
            Assert.InRange(page, 0, 3);
            Assert.Equal(page, AlhPro.Core.StartupPageMap.NavIndexFor(page));   // 现在两者顺序一致
        }
        Assert.Equal(2, AlhPro.Core.StartupPageMap.PageForTag("video"));
        Assert.Equal(3, AlhPro.Core.StartupPageMap.PageForTag("audio"));
        Assert.Equal(-1, AlhPro.Core.StartupPageMap.PageForTag("matting"));   // 视频抠图已下线
        Assert.Equal(-1, AlhPro.Core.StartupPageMap.PageForTag("tutorial"));
        Assert.Equal(-1, AlhPro.Core.StartupPageMap.PageForTag("settings"));
        Assert.Equal(-1, AlhPro.Core.StartupPageMap.PageForTag(null));
        Assert.Equal(-1, AlhPro.Core.StartupPageMap.PageForTag(""));
    }

    /// <summary>合法范围 = -1(上次退出) 与 0~3 四个功能页;4 已随视频抠图下线作废。</summary>
    [Fact]
    public void Valid_ranges_cover_the_four_pages()
    {
        Assert.True(AlhPro.Core.StartupPageMap.IsValidPage(-1));
        foreach (var p in Enumerable.Range(0, 4)) Assert.True(AlhPro.Core.StartupPageMap.IsValidPage(p));
        Assert.False(AlhPro.Core.StartupPageMap.IsValidPage(4));    // 视频抠图(已删)
        Assert.False(AlhPro.Core.StartupPageMap.IsValidPage(5));
        Assert.False(AlhPro.Core.StartupPageMap.IsValidPage(-2));

        Assert.False(AlhPro.Core.StartupPageMap.IsValidLastPage(-1));    // "上次退出界面"不能存成 -1
        foreach (var p in Enumerable.Range(0, 4)) Assert.True(AlhPro.Core.StartupPageMap.IsValidLastPage(p));
        Assert.False(AlhPro.Core.StartupPageMap.IsValidLastPage(4));
        Assert.False(AlhPro.Core.StartupPageMap.IsValidLastPage(5));
    }

    /// <summary>★ 源码契约:MainPage 必须**通过换算**去设选中项,不许再把编号当下标直接用。</summary>
    [Fact]
    public void MainPage_uses_the_mapper_instead_of_assigning_the_page_number_directly()
    {
        var cs = ReadRepoFile("ImgUpscalerUI", "Views", "MainPage.xaml.cs");
        Assert.Contains("NavList.SelectedIndex = AlhPro.Core.StartupPageMap.NavIndexFor(page0)", cs);
        Assert.Contains("StartupPageMap.PageForTag(_currentTag)", cs);
        Assert.Contains("StartupPageMap.IsValidPage(p)", cs);
        Assert.Contains("StartupPageMap.IsValidLastPage(p)", cs);
        // 旧的三种错写法必须再也搜不到
        Assert.DoesNotContain("NavList.SelectedIndex = page0", cs);
        Assert.DoesNotContain("_currentTag == \"video\" ? 2 : _currentTag == \"cutout\" ? 1 : 0", cs);
        Assert.DoesNotContain("p is >= 0 and <= 2", cs);
        Assert.DoesNotContain("p is >= -1 and <= 3", cs);
    }

    /// <summary>★ 视频抠图整条下线的源码契约(用户 2026-09-23 定案):导航项、页面分支、启动页下拉项都不许留。</summary>
    [Fact]
    public void The_video_matting_feature_is_gone()
    {
        var nav = ReadRepoFile("ImgUpscalerUI", "Views", "MainPage.xaml");
        Assert.DoesNotContain("NavMatting", nav);
        Assert.DoesNotContain("Tag=\"matting\"", nav);
        Assert.DoesNotContain("Text=\"视频抠图\"", nav);      // 导航项标签
        var cs = ReadRepoFile("ImgUpscalerUI", "Views", "MainPage.xaml.cs");
        Assert.DoesNotContain("Tag = \"matting\"", cs);
        Assert.DoesNotContain("Tag == \"matting\"", cs);
        Assert.DoesNotContain("new VideoMattingView()", cs);
        Assert.DoesNotContain("StartupPageMap.PageForTag(\"matting\")", cs);
        // 被删的文件不许再出现在仓库里(引用它的地方会编译报错,这里只是把"彻底删干净"钉成契约)
        Assert.False(File.Exists(RepoPath("ImgUpscalerUI", "Views", "VideoMattingView.xaml")));
        Assert.False(File.Exists(RepoPath("ImgUpscalerUI", "Views", "VideoMattingView.xaml.cs")));
        Assert.False(File.Exists(RepoPath("ImgUpscalerUI", "VideoMattingService.cs")));
        Assert.False(File.Exists(RepoPath("AlhPro.Core", "MattingOutputSpec.cs")));
        Assert.False(File.Exists(RepoPath("AlhPro.Core", "HexColor.cs")));
    }

    /// <summary>★ 但**保留件**不许跟着被误删:`AlhPro.Core.VideoMatting` 的纯逻辑 + 它的单测是有意留下的
    /// (用户:"留一点你认为有用的、有助于发展的")。详见
    /// docs/2026-09-23-视频抠图下线-保留的经验与可复用件.md。</summary>
    [Fact]
    public void The_reusable_alpha_logic_is_deliberately_kept()
    {
        Assert.True(File.Exists(RepoPath("AlhPro.Core", "VideoMatting.cs")));
        Assert.True(File.Exists(RepoPath("AlhPro.Tests", "VideoMattingAlphaTests.cs")));
        Assert.True(File.Exists(RepoPath("AlhPro.Tests", "VideoMattingCompositeTests.cs")));
        Assert.True(File.Exists(RepoPath("AlhPro.Tests", "AlphaTemporalFilterTests.cs")));
        Assert.True(File.Exists(RepoPath("docs", "2026-09-23-视频抠图下线-保留的经验与可复用件.md")));
        Assert.NotNull(typeof(AlhPro.Core.VideoMatting));
        Assert.NotNull(typeof(AlhPro.Core.AlphaTemporalFilter));
    }

    private static string RepoPath(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var cand = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(cand) || Directory.Exists(Path.GetDirectoryName(cand))) return cand;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("找不到仓库根: " + string.Join('/', parts));
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
