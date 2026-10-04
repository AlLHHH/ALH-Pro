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

    // ===================== 【2026-10-04 用户报「软件内广告等好久都不出现」】 =====================
    // 一次刷新要按编号试 ad1…adN,每条文件都从固定顺序重头试:死通道(实测 jsDelivr 超时 7 秒)挡在前面,
    // 9 个文件就白等两分钟。记住第一条文件探出来的可用通道(按 host 认),后面的文件与广告图直接先试它。

    [Fact]
    public void 通道host_能认出各家镜像与内嵌URL()
    {
        Assert.Equal("raw.githubusercontent.com", AdMirror.HostOf("https://raw.githubusercontent.com/AlLHHH/ALH-Pro/main/ad/ad1.json"));
        // gh-proxy 的地址里嵌着一条 https://…,只能按"第一个 /"取 host
        Assert.Equal("gh-proxy.com", AdMirror.HostOf("https://gh-proxy.com/https://raw.githubusercontent.com/AlLHHH/ALH-Pro/main/ad/ad1.json"));
        Assert.Equal("api.github.com", AdMirror.HostOf("https://API.github.com/repos/AlLHHH/ALH-Pro/contents/ad/{0}"));   // 统一小写
        Assert.Equal("api", AdMirror.HostOf("api"));       // 非 URL 标识原样返回
        Assert.Equal("", AdMirror.HostOf(""));
        Assert.Equal("", AdMirror.HostOf(null));
    }

    [Fact]
    public void 优先通道_命中则排最前且不留重复()
    {
        var urls = new List<string>
        {
            "https://api.github.com/repos/AlLHHH/ALH-Pro/contents/ad/{0}",
            "https://cdn.jsdelivr.net/gh/AlLHHH/ALH-Pro@main/ad/{0}",
            "https://gh-proxy.com/https://raw.githubusercontent.com/AlLHHH/ALH-Pro/main/ad/{0}",
            "https://raw.githubusercontent.com/AlLHHH/ALH-Pro/main/ad/{0}",
        };

        var reordered = AdMirror.PreferHost(urls, "gh-proxy.com");

        Assert.Equal(4, reordered.Count);                                  // 不能把命中的那条复制一份
        Assert.Equal("https://gh-proxy.com/https://raw.githubusercontent.com/AlLHHH/ALH-Pro/main/ad/{0}", reordered[0]);
        Assert.Single(reordered, u => u.Contains("gh-proxy.com", StringComparison.Ordinal));
        Assert.Equal("https://api.github.com/repos/AlLHHH/ALH-Pro/contents/ad/{0}", reordered[1]);   // 其余保持原顺序
    }

    [Fact]
    public void 优先通道_本来就在最前则原样返回()
    {
        var urls = new List<string>
        {
            "https://api.github.com/repos/AlLHHH/ALH-Pro/contents/ad/{0}",
            "https://cdn.jsdelivr.net/gh/AlLHHH/ALH-Pro@main/ad/{0}",
        };

        var reordered = AdMirror.PreferHost(urls, "api.github.com");

        Assert.Equal(urls, reordered);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("没这条通道.com")]
    public void 优先通道_空值或未命中保持原顺序(string? preferred)
    {
        var urls = new List<string>
        {
            "https://api.github.com/repos/AlLHHH/ALH-Pro/contents/ad/{0}",
            "https://cdn.jsdelivr.net/gh/AlLHHH/ALH-Pro@main/ad/{0}",
        };

        var reordered = AdMirror.PreferHost(urls, preferred);

        Assert.Equal(urls, reordered);
    }

    [Fact]
    public void 优先通道_空列表不抛异常()
    {
        Assert.Empty(AdMirror.PreferHost(null, "gh-proxy.com"));
        Assert.Empty(AdMirror.PreferHost(new List<string>(), "gh-proxy.com"));
    }
}
