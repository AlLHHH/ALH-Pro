using System;
using System.Collections.Generic;
using System.IO;
using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>
/// 广告本地缓存(<see cref="AdCache"/>)的单元测试。用例对着 **2026-10-17 用户要求**
/// 「上次刷新成功后缓存在本地,直到下次打开时使用时检测到更新然后再更新广告」。
/// <para>最要紧的一条是"存进去必须能读回来":缓存文件字段名一旦写成 PascalCase,
/// <see cref="AdCache.ParseAd"/> 按小写名取值会一条都读不回来 —— 整份缓存静默失效,
/// 用户看到的还是那两张内置兜底卡,而且没有任何报错(见 <c>缓存JSON_字段名必须小写…</c>)。</para>
/// </summary>
public class AdCacheTests
{
    private const string RawImg4 = "https://raw.githubusercontent.com/AlLHHH/ALH-Pro/main/ad/img4.jpg";

    private static AdInfo MakeAd() => new()
    {
        Title = "标题",
        Text = "正文",
        Image = RawImg4,
        Link = "https://example.com/a",
        LinkText = "去看看",
        Sponsor = "广告主",
    };

    [Fact]
    public void 缓存文件名_与仓库里的adN同名同编号()
    {
        Assert.Equal("ad1.json", AdCache.FileNameOf(1));
        Assert.Equal("ad9.json", AdCache.FileNameOf(9));
        Assert.Equal("ad20.json", AdCache.FileNameOf(20));
    }

    [Fact]
    public void 缓存编号_adN认得出_别的一律返回0()
    {
        Assert.Equal(3, AdCache.IndexOf("ad3.json"));
        Assert.Equal(1, AdCache.IndexOf(@"C:\Users\X\AppData\Local\ALHPro\ads\ad1.json"));
        for (int i = 1; i <= AdCache.MaxFiles; i++) Assert.Equal(i, AdCache.IndexOf(AdCache.FileNameOf(i)));

        // 认不出就返回 0:清理旧缓存时宁可留下一个看不懂的文件,也不误删/误算。
        Assert.Equal(0, AdCache.IndexOf(null));
        Assert.Equal(0, AdCache.IndexOf(""));
        Assert.Equal(0, AdCache.IndexOf("   "));
        Assert.Equal(0, AdCache.IndexOf("ad.json"));         // 没有编号
        Assert.Equal(0, AdCache.IndexOf("ad0.json"));        // 编号从 1 起
        Assert.Equal(0, AdCache.IndexOf("ad21.json"));       // 超出上限
        Assert.Equal(0, AdCache.IndexOf("ad3.txt"));
        Assert.Equal(0, AdCache.IndexOf("ad3.json.tmp"));    // 写一半的临时文件
        Assert.Equal(0, AdCache.IndexOf("img4.jpg"));
        Assert.Equal(0, AdCache.IndexOf("saved.txt"));
    }

    [Fact]
    public void 缓存目录_在本机LOCALAPPDATA的ALHPro下()
    {
        var root = Path.Combine("C:", "Users", "X", "AppData", "Local");

        Assert.Equal(Path.Combine(root, "ALHPro", "ads"), AdCache.Dir(root));
        Assert.NotNull(AdCache.Dir(null));   // 取不到 %LOCALAPPDATA% 也不抛
    }

    [Fact]
    public void 存进去要能读回来_六个字段一个不少()
    {
        var back = AdCache.ParseAd(AdCache.ToJson(MakeAd()));

        Assert.NotNull(back);
        Assert.Equal("标题", back!.Title);
        Assert.Equal("正文", back.Text);
        Assert.Equal(RawImg4, back.Image);
        Assert.Equal("https://example.com/a", back.Link);
        Assert.Equal("去看看", back.LinkText);
        Assert.Equal("广告主", back.Sponsor);
    }

    [Fact]
    public void 缓存JSON_字段名必须小写_写成PascalCase整份缓存就读不回来()
    {
        var json = AdCache.ToJson(MakeAd());

        Assert.Contains("\"title\"", json);
        Assert.Contains("\"text\"", json);
        Assert.Contains("\"image\"", json);
        Assert.Contains("\"link\"", json);
        Assert.Contains("\"linkText\"", json);
        Assert.Contains("\"sponsor\"", json);
        Assert.DoesNotContain("\"Title\"", json);
        Assert.DoesNotContain("\"Image\"", json);
        Assert.DoesNotContain("\"LinkText\"", json);
    }

    [Fact]
    public void 空字段不写进缓存文件_免得写一堆null()
    {
        var json = AdCache.ToJson(new AdInfo { Title = "只有标题" });

        Assert.Contains("title", json);
        Assert.DoesNotContain("image", json);
        Assert.DoesNotContain("sponsor", json);
        Assert.DoesNotContain("null", json);
    }

    [Fact]
    public void 解析_兼容旧的ad键包裹写法()
    {
        var ad = AdCache.ParseAd("{\"ad\":{\"title\":\"T\",\"text\":\"X\",\"image\":\"img1.jpg\"}}");

        Assert.NotNull(ad);
        Assert.Equal("T", ad!.Title);
        Assert.Equal("img1.jpg", ad.Image);
    }

    [Fact]
    public void 解析_没有标题也没有正文视为无效_半截JSON也不抛()
    {
        Assert.Null(AdCache.ParseAd("{\"image\":\"img1.jpg\"}"));
        Assert.Null(AdCache.ParseAd(""));
        Assert.Null(AdCache.ParseAd(null));
        Assert.Null(AdCache.ParseAd("{\"title\":\"T\""));   // 断电/被杀留下的半截文件
    }

    [Fact]
    public void 引用图片名_从各条广告的图地址里取缓存文件名()
    {
        var keep = AdCache.ReferencedImageNames(new[]
        {
            new AdInfo { Title = "A", Image = RawImg4 },
            new AdInfo { Title = "B", Image = "https://cdn.jsdelivr.net/gh/AlLHHH/ALH-Pro@main/ad/img9.jpg?v=2" },
            new AdInfo { Title = "C" },   // 纯文字卡:不引用图片(不该因此留住 ad.jpg)
        });

        Assert.Equal(2, keep.Count);
        Assert.Contains("img4.jpg", keep);
        Assert.Contains("img9.jpg", keep);
        Assert.DoesNotContain("ad.jpg", keep);
    }

    [Fact]
    public void 图片可清理_被缓存广告引用的图无论多旧都留着()
    {
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "img4.jpg" };
        var old = DateTime.Now - TimeSpan.FromDays(30);
        var fresh = DateTime.Now - TimeSpan.FromMinutes(5);
        var cutoff = DateTime.Now - TimeSpan.FromDays(1);

        Assert.False(AdCache.IsImagePrunable("img4.jpg", old, cutoff, keep));     // 还在用:留着
        Assert.False(AdCache.IsImagePrunable("IMG4.JPG", old, cutoff, keep));     // 名单按 Windows 口径(不区分大小写)
        Assert.True(AdCache.IsImagePrunable("img9.jpg", old, cutoff, keep));      // 没人引用的旧图:删
        Assert.False(AdCache.IsImagePrunable("img9.jpg", fresh, cutoff, keep));   // 还新鲜:留着
        Assert.False(AdCache.IsImagePrunable(null, old, cutoff, keep));
        Assert.False(AdCache.IsImagePrunable("", old, cutoff, keep));
        Assert.True(AdCache.IsImagePrunable("img9.jpg", old, cutoff, null));      // 没有名单时按老规则(只看时间)
    }
}
