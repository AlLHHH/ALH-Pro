using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【2026-09-23 修 A2】启动页编号 ↔ 左侧导航项下标 的换算契约。
///
/// 真机实测的病：设置里选「视频处理」→ 重启进「视频抠图」（锁着的"开发中"页）；
/// 选「音频处理」→ 进「视频处理」；选「视频抠图」→ 永远不生效。
/// 根因是**两套编号被当成同一套用**（启动页编号 vs 导航列表书写顺序），这里把换算钉死。
///
/// 这套单测同时是"改导航项时的防呆":以后谁在 XAML 里挪了左侧导航顺序,`NavIndexFor` 与这条单测
/// 必须一起改 —— 否则用户开机又会进错页。</summary>
public class StartupPageMapTests
{
    /// <summary>★ 五个功能页的换算逐条钉住（这条单测就是"导航顺序"的可执行记录）。</summary>
    [Theory]
    [InlineData(0, 0)]   // 图片放大 → 导航第 1 项
    [InlineData(1, 1)]   // 图片抠图 → 导航第 2 项
    [InlineData(2, 3)]   // 视频处理 → 导航第 4 项(NavList 里 2 是视频抠图!)
    [InlineData(3, 4)]   // 音频处理 → 导航第 5 项
    [InlineData(4, 2)]   // 视频抠图 → 导航第 3 项
    public void Startup_page_maps_to_the_right_nav_index(int page, int expectedNavIndex)
        => Assert.Equal(expectedNavIndex, AlhPro.Core.StartupPageMap.NavIndexFor(page));

    /// <summary>五个功能页两两映射到**不同的**导航项（映射错了必然撞车）。</summary>
    [Fact]
    public void The_five_pages_map_to_five_distinct_nav_items()
    {
        var idx = Enumerable.Range(0, 5).Select(AlhPro.Core.StartupPageMap.NavIndexFor).ToArray();
        Assert.Equal(5, idx.Distinct().Count());
        Assert.All(idx, i => Assert.InRange(i, 0, 4));
    }

    /// <summary>非法编号不能乱跳（一律回"图片放大"）。</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(99)]
    [InlineData(int.MinValue)]
    public void Invalid_pages_fall_back_to_the_first_page(int page)
        => Assert.Equal(0, AlhPro.Core.StartupPageMap.NavIndexFor(page));

    /// <summary>Tag ↔ 编号：五个功能页要能互相还原；非功能页(教程/设置)必须返回 -1 = 别记。</summary>
    [Fact]
    public void Tags_round_trip_and_non_function_pages_are_not_recorded()
    {
        foreach (var tag in new[] { "upscale", "cutout", "video", "audio", "matting" })
        {
            int page = AlhPro.Core.StartupPageMap.PageForTag(tag);
            Assert.InRange(page, 0, 4);
            Assert.Equal(AlhPro.Core.StartupPageMap.NavIndexFor(page), AlhPro.Core.StartupPageMap.NavIndexFor(page));
        }
        Assert.Equal(2, AlhPro.Core.StartupPageMap.PageForTag("video"));     // 视频处理 = 2（不是导航下标 3）
        Assert.Equal(3, AlhPro.Core.StartupPageMap.PageForTag("audio"));
        Assert.Equal(4, AlhPro.Core.StartupPageMap.PageForTag("matting"));
        Assert.Equal(-1, AlhPro.Core.StartupPageMap.PageForTag("tutorial"));
        Assert.Equal(-1, AlhPro.Core.StartupPageMap.PageForTag("settings"));
        Assert.Equal(-1, AlhPro.Core.StartupPageMap.PageForTag(null));
        Assert.Equal(-1, AlhPro.Core.StartupPageMap.PageForTag(""));
    }

    /// <summary>读取范围必须放到 4（旧代码只收 0~2 / -1~3 ⇒ 音频处理与视频抠图永远存不进去）。</summary>
    [Fact]
    public void Valid_ranges_cover_all_five_pages()
    {
        Assert.True(AlhPro.Core.StartupPageMap.IsValidPage(-1));
        foreach (var p in Enumerable.Range(0, 5)) Assert.True(AlhPro.Core.StartupPageMap.IsValidPage(p));
        Assert.False(AlhPro.Core.StartupPageMap.IsValidPage(5));
        Assert.False(AlhPro.Core.StartupPageMap.IsValidPage(-2));

        Assert.False(AlhPro.Core.StartupPageMap.IsValidLastPage(-1));    // "上次退出界面"不能存成 -1
        foreach (var p in Enumerable.Range(0, 5)) Assert.True(AlhPro.Core.StartupPageMap.IsValidLastPage(p));
        Assert.False(AlhPro.Core.StartupPageMap.IsValidLastPage(5));
    }

    /// <summary>★ 源码契约：MainPage 必须**通过换算**去设选中项，不许再把编号当下标直接用。</summary>
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
