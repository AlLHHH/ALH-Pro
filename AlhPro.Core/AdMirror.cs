namespace AlhPro.Core;

/// <summary>
/// 广告(左栏轮播卡)抓取的纯逻辑:raw URL → 镜像地址列表、缓存文件名、图片缓存是否还能复用、内容指纹。
/// 抽到 Core 只为一件事:**可单测**。真机踩过的两个坑都在这里留了回归用例:
/// <list type="number">
/// <item>镜像地址拼错 —— 曾把 jsDelivr 拼成 <c>@main/ad/ad/img4.jpg</c>(多一层 <c>ad/</c>),永远 404,
/// 而注释里写着"有些机器上只有 jsDelivr 直连能通";</item>
/// <item>图片缓存按文件名存、只判断"文件存在就复用" —— 作者替换同名图片后界面永远显示旧图,直到启动时清掉 3 天前的文件。</item>
/// </list>
/// </summary>
public static class AdMirror
{
    /// <summary>广告所在仓库与分支(图片/JSON 都在 <c>&lt;repo&gt;@main/ad/</c> 下)。</summary>
    public const string Repo = "AlLHHH/ALH-Pro";
    public const string Branch = "main";

    private const string RawHost = "raw.githubusercontent.com";

    /// <summary>
    /// 把 GitHub raw URL 转成依次尝试的镜像地址:jsDelivr → gh-proxy → ghproxy → 原地址。
    /// 非 raw 域名(已经是别的源)原样返回。顺序按真机实测可用性排,jsDelivr 排第一。
    /// <para>jsDelivr 的路径按 URL 里的真实相对路径拼(如 <c>ad/img4.jpg</c>),**不再额外加一层 ad/**。</para>
    /// </summary>
    public static IReadOnlyList<string> MirrorsFor(string? url)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(url)) return list;
        if (url.IndexOf(RawHost, StringComparison.OrdinalIgnoreCase) < 0)
        {
            list.Add(url);   // 非 raw 域名(已是其它源):原样用
            return list;
        }

        var rel = RelativePathOf(url);
        if (rel.Length > 0)
            list.Add($"https://cdn.jsdelivr.net/gh/{Repo}@{Branch}/{rel}");
        list.Add("https://gh-proxy.com/" + url);
        list.Add("https://ghproxy.net/" + url);
        list.Add(url);   // 原图最后(该域名可能被 hosts 加速器劫持到 127.0.0.1)
        return list;
    }

    /// <summary>
    /// 取 raw URL 里仓库内的相对路径(<c>https://raw.githubusercontent.com/&lt;owner&gt;/&lt;repo&gt;/&lt;branch&gt;/ad/img4.jpg</c> → <c>ad/img4.jpg</c>)。
    /// 解析不出来返回空串(调用方只拼原地址,不会拼出坏 URL)。
    /// </summary>
    public static string RelativePathOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "";
            var parts = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= 3) return "";   // owner/repo/branch 之后才是文件路径
            return string.Join('/', parts, 3, parts.Length - 3);
        }
        catch { return ""; }
    }

    /// <summary>图片 URL → 本地缓存文件名(取 URL 路径最后一段,丢掉 <c>?query</c>)。取不到用 "ad.jpg"。</summary>
    public static string CacheFileName(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "ad.jpg";
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "ad.jpg";
            var name = System.IO.Path.GetFileName(u.AbsolutePath);
            return string.IsNullOrWhiteSpace(name) ? "ad.jpg" : name;
        }
        catch { return "ad.jpg"; }
    }

    /// <summary>
    /// 缓存文件还能不能直接复用:必须存在、非空、且写入时间不早于 <paramref name="minWriteUtc"/>。
    /// <para><paramref name="minWriteUtc"/> 由调用方算成 <c>max(现在 - 有效期, 广告内容上次变化时间)</c>:
    /// 前者让"作者换了同名图片"最多等一个有效期就自动重新下载;后者让"一改 ad8.json 就立刻重下图",不用等。</para>
    /// </summary>
    public static bool IsCacheReusable(bool exists, long bytes, DateTime lastWriteUtc, DateTime minWriteUtc)
        => exists && bytes > 0 && lastWriteUtc >= minWriteUtc;

    /// <summary>广告内容指纹(FNV-1a 64 位十六进制)。内容没变 → 指纹不变 → 图片缓存继续复用。</summary>
    public static string Fingerprint(IEnumerable<string>? lines)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong h = offset;
        if (lines is null) return h.ToString("x16");
        foreach (var line in lines)
        {
            if (line is not null)
            {
                foreach (var ch in line) { h ^= ch; h *= prime; }
            }
            h ^= '\n';
            h *= prime;
        }
        return h.ToString("x16");
    }
}
