using AlhPro.Core;
using System;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>视频页超分模型下拉的**序号迁移**(Rev0/1/2 → Rev3)。
/// 【为什么单测它】下拉与预设存的都是序号;换顺序后老序号会指向另一支模型,而且是**静默**的:
/// 2026-09-12 那次换位若不迁移,用户"存的 3 = 轻量通用"会变成"3 = x4plus"(实测慢 17 倍)。
/// 这里把三次换位的换算、级联(老文件从 Rev0 一路换到最新)、幂等、越界兜底全钉住。
///
/// 各 Rev 的下拉顺序:
///   Rev0: 0=animevideov3 1=x4plus-anime 2=x4plus     3=general-x4v3
///   Rev1: 0=animevideov3 1=x4plus-anime 2=general-x4v3 3=x4plus
///   Rev2: 0=animevideov3 1=general-x4v3 2=x4plus-anime 3=x4plus  4=wdn-x4v3(末尾追加)
///   Rev3: 0=animevideov3 1=general-x4v3 2=wdn-x4v3     3=x4plus-anime 4=x4plus   ← 按速度/体积排</summary>
public class VideoModelOrderTests
{
    /// <summary>Rev2 → Rev3 的映射(本次换位的核心):2→3、3→4、4(=wdn)→2,0/1 不变。</summary>
    [Theory]
    [InlineData(0, 0)]   // animevideov3:最快最小,仍是第 1 位
    [InlineData(1, 1)]   // general-x4v3:5MB/0.458s,仍在第 2 位
    [InlineData(2, 3)]   // x4plus-anime 被 wdn 挤到后面
    [InlineData(3, 4)]   // x4plus(超慢)按体积与耗时退到最末
    [InlineData(4, 2)]   // Rev2 里末尾追加的 wdn-x4v3 上移到第 3 位
    public void Rev2_to_Rev3_maps_indices(int saved, int expected)
    {
        int m = VideoModelOrder.Migrate(saved, 2, out int rev);
        Assert.Equal(expected, m);
        Assert.Equal(VideoModelOrder.CurrentRev, rev);
    }

    /// <summary>Rev1(0=anime,1=x4plus-anime,2=general-x4v3,3=x4plus)要级联走完 Rev2 + Rev3。</summary>
    [Theory]
    [InlineData(0, 0)]   // animevideov3
    [InlineData(1, 3)]   // x4plus-anime:Rev2 →2,Rev3 →3
    [InlineData(2, 1)]   // general-x4v3:Rev2 →1,Rev3 不变
    [InlineData(3, 4)]   // x4plus:Rev2 不变,Rev3 →4
    public void Rev1_cascades_to_Rev3(int saved, int expected)
        => Assert.Equal(expected, VideoModelOrder.Migrate(saved, 1, out _));

    /// <summary>Rev0(0=anime,1=x4plus-anime,2=x4plus,3=general-x4v3)要连走 Rev1→Rev2→Rev3。</summary>
    [Theory]
    [InlineData(0, 0)]   // animevideov3
    [InlineData(1, 3)]   // x4plus-anime
    [InlineData(2, 4)]   // x4plus:Rev1 →3,Rev2 不变,Rev3 →4
    [InlineData(3, 1)]   // general-x4v3:Rev1 →2,Rev2 →1,Rev3 不变
    public void Rev0_cascades_to_Rev3(int saved, int expected)
        => Assert.Equal(expected, VideoModelOrder.Migrate(saved, 0, out _));

    /// <summary>幂等:已经是当前 Rev 的数据一个字节都不许动(否则每次启动来回横跳)。</summary>
    [Fact]
    public void Current_rev_is_idempotent()
    {
        for (int i = 0; i < VideoModelOrder.Count; i++)
        {
            int m = VideoModelOrder.Migrate(i, VideoModelOrder.CurrentRev, out int rev);
            Assert.Equal(i, m);
            Assert.Equal(VideoModelOrder.CurrentRev, rev);
        }
    }

    /// <summary>越界序号(手改坏的文件、或从更新版本回退)保守归到 0(animevideov3 = 最快最省那支),不猜中间项。</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(VideoModelOrder.Count)]
    [InlineData(99)]
    public void Out_of_range_falls_back_to_zero(int saved)
    {
        int m = VideoModelOrder.Migrate(saved, VideoModelOrder.CurrentRev, out _);
        Assert.Equal(0, m);
        // 老 Rev 的越界值同样兜底(先换算再看界,换算后仍在界外才归零)
        int m2 = VideoModelOrder.Migrate(saved, 2, out _);
        Assert.InRange(m2, 0, VideoModelOrder.Count - 1);
    }

    /// <summary>下拉项数量必须与"迁移后合法序号范围"一致 —— 加/删模型时这条会立刻报警。</summary>
    [Fact]
    public void Count_matches_the_dropdown()
    {
        Assert.Equal(5, VideoModelOrder.Count);
        // 每一项在自己 Rev 下都要能落在合法范围里
        for (int i = 0; i < VideoModelOrder.Count; i++)
            Assert.InRange(VideoModelOrder.Migrate(i, VideoModelOrder.CurrentRev, out _), 0, VideoModelOrder.Count - 1);
    }

    /// <summary>**契约测试**:VideoView.xaml 里超分模型下拉的真实 Tag 顺序,必须与 VideoModelOrder 的映射表逐位一致。
    /// 【为什么必须有】2026-09-14 重排时 XAML 漏掉了一行(只留注释),下拉从 5 项变 4 项,而迁移表仍按 5 项映射
    /// (Rev2 的 4→新 2)—— 老用户存的序号会被解释成另一支模型,而且**编译、单测、启动验证全都通过**,
    /// 只有"人眼看界面"才发现。这条测试把"源码里的顺序"钉住,让这类漏行在下一次 dotnet test 就爆出来。</summary>
    [Fact]
    public void Xaml_dropdown_order_matches_the_migration_table()
    {
        string? path = null;
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var cand = System.IO.Path.Combine(dir.FullName, "ImgUpscalerUI", "Views", "VideoView.xaml");
            if (System.IO.File.Exists(cand)) { path = cand; break; }
            dir = dir.Parent;
        }
        Assert.NotNull(path);
        var xaml = System.IO.File.ReadAllText(path!);

        // 截取 VideoEsrganModelCombo 那一段(到它自己的 </ComboBox> 为止),再取其中的 Tag
        int start = xaml.IndexOf("x:Name=\"VideoEsrganModelCombo\"", StringComparison.Ordinal);
        Assert.True(start > 0, "XAML 里找不到 VideoEsrganModelCombo");
        int end = xaml.IndexOf("</ComboBox>", start, StringComparison.Ordinal);
        Assert.True(end > start, "VideoEsrganModelCombo 没有闭合标签");
        var block = xaml.Substring(start, end - start);

        var tags = System.Text.RegularExpressions.Regex.Matches(block, "Tag=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value).ToArray();

        // Rev3 的目标顺序(快/小 → 慢/大),与 VideoModelOrder 的映射表必须一致
        Assert.Equal(new[]
        {
            "realesr-animevideov3",      // 0 4MB  0.26~0.30 s/帧
            "realesr-general-x4v3",      // 1 5MB  0.458
            "realesr-general-wdn-x4v3",  // 2 5MB  0.460
            "realesrgan-x4plus-anime",   // 3 9MB  3.85
            "realesrgan-x4plus",         // 4 41MB 15.145(超慢排最后)
        }, tags);
        Assert.Equal(VideoModelOrder.Count, tags.Length);
    }
}
