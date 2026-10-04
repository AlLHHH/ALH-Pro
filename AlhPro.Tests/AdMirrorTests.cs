using System;
using System.Collections.Generic;
using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>
/// 广告抓取纯逻辑(AdMirror)的单元测试。用例全部对应 **2026-10-04 用户报的真问题**
/// 「我改的 ad8 还有 img4 为什么图片没有生效」定位到的两个坑:
/// <list type="number">
/// <item>图片的 jsDelivr 镜像地址被拼成 <c>@main/ad/ad/img4.jpg</c>(多一层 ad/)→ 永远 404;</item>
/// <item>图片缓存只判断"文件存在就复用" → 替换同名图片永远不生效。</item>
/// </list>
/// </summary>
public class AdMirrorTests
{
    private const string RawImg4 = "https://raw.githubusercontent.com/AlLHHH/ALH-Pro/main/ad/img4.jpg";

    [Fact]
    public void 镜像列表_jsDelivr路径不得重复ad层()
    {
        var list = AdMirror.MirrorsFor(RawImg4);

        Assert.Equal(4, list.Count);
        Assert.Equal("https://cdn.jsdelivr.net/gh/AlLHHH/ALH-Pro@main/ad/img4.jpg", list[0]);
        // 回归断言:旧实现拼出的是 .../@main/ad/ad/img4.jpg
        Assert.DoesNotContain(list, u => u.Contains("/ad/ad/", StringComparison.Ordinal));
        Assert.Equal("https://gh-proxy.com/" + RawImg4, list[1]);
        Assert.Equal("https://ghproxy.net/" + RawImg4, list[2]);
        Assert.Equal(RawImg4, list[3]);
    }

    [Fact]
    public void 镜像列表_非raw域名原样返回()
    {
        var list = AdMirror.MirrorsFor("https://example.com/a/b.jpg");

        Assert.Single(list);
        Assert.Equal("https://example.com/a/b.jpg", list[0]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 镜像列表_空URL返回空(string? url)
    {
        Assert.Empty(AdMirror.MirrorsFor(url));
    }

    [Fact]
    public void 相对路径_取仓库分支之后的文件路径()
    {
        Assert.Equal("ad/img4.jpg", AdMirror.RelativePathOf(RawImg4));
        // 分支名/仓库名不同也照常解析(不再硬编码 main/ad/)
        Assert.Equal("ad/ad9.json", AdMirror.RelativePathOf("https://raw.githubusercontent.com/o/r/dev/ad/ad9.json"));
    }

    [Theory]
    [InlineData("https://raw.githubusercontent.com/AlLHHH/ALH-Pro/main", "")]          // 只有 owner/repo/branch
    [InlineData("https://raw.githubusercontent.com/AlLHHH/ALH-Pro", "")]
    [InlineData("不是URL", "")]
    [InlineData(null, "")]
    public void 相对路径_解析不出来返回空串(string? url, string expected)
    {
        Assert.Equal(expected, AdMirror.RelativePathOf(url));
    }

    [Fact]
    public void 缓存文件名_取最后一段并丢掉查询串()
    {
        Assert.Equal("img4.jpg", AdMirror.CacheFileName(RawImg4));
        Assert.Equal("img4.jpg", AdMirror.CacheFileName("https://raw.githubusercontent.com/o/r/main/ad/img4.jpg?v=2"));
        Assert.Equal("ad.jpg", AdMirror.CacheFileName(""));
        Assert.Equal("ad.jpg", AdMirror.CacheFileName("不是URL"));
    }

    [Fact]
    public void 缓存复用_新鲜可用_过期或内容变化或空文件不可用()
    {
        var now = new DateTime(2026, 10, 4, 15, 0, 0, DateTimeKind.Utc);
        var ttlAgo = now - TimeSpan.FromHours(3);

        Assert.True(AdMirror.IsCacheReusable(true, 2111, now - TimeSpan.FromMinutes(1), ttlAgo));   // 新鲜且在有效期内
        Assert.False(AdMirror.IsCacheReusable(true, 2111, now - TimeSpan.FromHours(4), ttlAgo));    // 超过有效期 → 重下
        Assert.False(AdMirror.IsCacheReusable(true, 2111, now - TimeSpan.FromMinutes(1), now));     // 内容刚变过 → 重下
        Assert.False(AdMirror.IsCacheReusable(false, 0, now, ttlAgo));                              // 文件不存在
        Assert.False(AdMirror.IsCacheReusable(true, 0, now, ttlAgo));                               // 空文件(上次下崩了)
    }

    [Fact]
    public void 指纹_内容相同则相同_改一个字就变()
    {
        var a = new List<string> { "FrameNeo|交流群634806709|...img4.jpg|||", "卡2|正文|...|链接||" };
        var b = new List<string> { "FrameNeo|交流群634806709|...img4.jpg|||", "卡2|正文|...|链接||" };
        var c = new List<string> { "FrameNeo|交流群634806709|...img4.jpg|||", "卡2|正文改过|...|链接||" };

        Assert.Equal(AdMirror.Fingerprint(a), AdMirror.Fingerprint(b));
        Assert.NotEqual(AdMirror.Fingerprint(a), AdMirror.Fingerprint(c));
        Assert.NotEqual(AdMirror.Fingerprint(a), AdMirror.Fingerprint(new List<string>()));
    }

    [Fact]
    public void 指纹_空输入不抛异常()
    {
        Assert.NotEmpty(AdMirror.Fingerprint(null));
    }
}
