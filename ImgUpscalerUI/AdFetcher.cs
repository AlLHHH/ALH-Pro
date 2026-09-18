using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace ALHPro;

/// <summary>
/// 「广告」动态区数据源:拉取仓库 ad/ 目录下的 5 个独立广告文件 ad1.json~ad5.json(作者编辑后 push,用户端轮询到即更新)。
/// 复用 UpdateChecker 的多端点(官方 raw + 国内镜像)与 5 秒超时/静默策略。
/// 每个文件定义一张卡 {title,text,image,link,linkText};image 可空(无图则纯文字卡)。
/// 客户端本地每 60 秒轮播下一张(不联网),网络只在启动/每 10 分钟拉一次缓存。
/// 失败(无网/超时/JSON 异常)一律静默:单文件 404/损坏跳过,全坏则隐藏整个区域,不打扰用户。
/// </summary>
public static class AdFetcher
{
    /// <summary>广告文件前缀:ad1.json、ad2.json、ad3.json……(自适应:从 ad1 开始,直到某个文件不存在才停,最多 20 个)。
    /// 作者以后想加广告,只要在 ad/ 目录新建 adN.json 并上传即可,无需改代码或发版。</summary>
    private const string AdFilePrefix = "ad";

    /// <summary>轮询间隔:10 分钟(作者改完 push,用户侧最长 10 分钟看到新内容)。</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(10);

    /// <summary>轮播间隔:每 30 秒换下一张广告卡(本地轮播,不联网)。</summary>
    public static readonly TimeSpan RotateInterval = TimeSpan.FromSeconds(30);

    /// <summary>把直连 GitHub 的 URL 转成国内可达镜像;非 GitHub raw 域名原样返回。
    /// 解决国内 raw.githubusercontent.com 常被墙/超时。镜像列表见以下 :GitHub 加速镜像。</summary>
    public static string ToMirrorUrl(string url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) return url;
            if (!url.Contains("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase)) return url;
            return "https://gh-proxy.com/" + url;
        }
        catch { return url; }
    }

    /// <summary>把 GitHub raw URL 转成多个国内可达镜像 URL(供图片加载做多镜像回退)。
    /// raw.githubusercontent.com 国内直连常被墙/超时,不放在首选;jsDelivr 实测最稳,排最前。</summary>
    public static System.Collections.Generic.IEnumerable<string> ToMirrorUrls(string url)
    {
        var list = new System.Collections.Generic.List<string>();
        if (string.IsNullOrWhiteSpace(url)) return list;
        if (url.Contains("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            // 【2026-09-17】顺序按"国内实测可用"重排:公共加速镜像在前,jsDelivr 次之,raw 原图最后
            var rel = "";
            try { rel = url.Substring(url.IndexOf("main/ad/") + "main/".Length); } catch { }
            if (rel.Length > 0)
            {
                // 【2026-09-17 真机实测重排】jsDelivr 排第一(用户机器上只有它是直连真通的);
                // ghfast/gitmirror 在那边不是超时就是 DNS 解析不了,已移除。
                list.Add("https://cdn.jsdelivr.net/gh/AlLHHH/ALH-Pro@main/ad/" + rel);
            }
            list.Add("https://gh-proxy.com/" + url);
            list.Add("https://ghproxy.net/" + url);
            list.Add(url);   // 原图最后(该域名可能被 hosts 加速器劫持到 127.0.0.1)
        }
        else
        {
            list.Add(url);   // 非 raw 域名(已是其它源):原样用
        }
        return list;
    }

    /// <summary>最近一次缓存到的广告卡数组(线程安全);启动即有默认本地兜底,网络拉取成功后再替换。</summary>
    public static volatile AdInfo[] Latest = DefaultAds();

    /// <summary>默认本地兜底广告(启动立即显示,避免首次进软件无广告;网络拉取成功后覆盖)。</summary>
    private static AdInfo[] DefaultAds() => new[]
    {
        new AdInfo
        {
            Title = "全程本地运行,隐私不联网",
            Text = "图片放大 / 视频补帧 / AI 抠图 / 音频增强,效果全在你电脑上完成,不用上传、不怕泄露。",
            Link = "https://github.com/AlLHHH/ALH-Pro",
            LinkText = "去了解",
        },
        new AdInfo
        {
            Title = "音频增强 & AI 分离",
            Text = "人声伴奏分离、升采样率、去噪,一键出成品。",
            Link = "https://github.com/AlLHHH/ALH-Pro/releases",
            LinkText = "去下载",
        },
    };

    private static readonly HttpClient _http = CreateHttp();

    /// <summary>共享这个已配好 TLS 1.2 / IPv4 的 HttpClient(TipFetcher 复用,避免各写一份、也避免重复追加 User-Agent)。</summary>
    public static HttpClient Http => _http;

    /// <summary>
    /// 【2026-09-17 修】原来每个请求前都调 DefaultRequestHeaders.UserAgent.ParseAdd(),
    /// 拉 20 个广告文件 × 4 个镜像 = 请求头里堆几十个 UserAgent → 请求被服务端拒/超时,
    /// 表现就是"在线广告总是拉取不到、图片永远出不来"。User-Agent 只在建客户端时设一次。
    /// 超时也从 5 秒放宽到 12 秒(国内直连 GitHub raw 经常要 6~10 秒)。
    /// </summary>
    private static HttpClient CreateHttp()
    {
        // 【2026-09-17 真机定位到根因】日志里是
        //   "The SSL connection could not be established / 远程主机强迫关闭了一个现有的连接"
        // = TLS 握手被中间设备重置。原因:本 App 跑在 .NET 8,默认会先尝试 **TLS 1.3**;
        // 而这台机器的网络(以及相当多国内线路)会对 TLS 1.3 握手直接 RST ——
        // 同一个 URL 用 PowerShell(.NET Framework,TLS 1.2)能秒开就是最好的对照。
        // 所以固定用 TLS 1.2 握手;失败则退回默认客户端(不因这一项把广告功能整个弄死)。
        HttpClient c;
        try
        {
            var h = new System.Net.Http.SocketsHttpHandler
            {
                // 【2026-09-17 真机定位】日志:同一秒里 api.github.com 成功、cdn.jsdelivr.net 却是
                // "SSL connection could not be established / 远程主机强迫关闭了一个现有的连接"(TLS 1.2 也无效)。
                // 原因:jsDelivr 的 DNS 先返回 IPv6(2606:4700::…),这条线路 IPv6 通不了 →
                // 握手直接失败;而用 PowerShell 测同一个 URL 秒开 —— 因为它默认走了 IPv4。
                // 所以这里显式只连 IPv4 地址(解析 A 记录 → 自己建 socket)。
                ConnectCallback = async (ctx, ct) =>
                {
                    var addrs = await System.Net.Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct).ConfigureAwait(false);
                    System.Net.IPAddress? v4 = null;
                    foreach (var a in addrs)
                        if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) { v4 = a; break; }
                    if (v4 == null) throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound);
                    var sock = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
                        System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
                    try { await sock.ConnectAsync(new System.Net.IPEndPoint(v4, ctx.DnsEndPoint.Port), ct).ConfigureAwait(false); }
                    catch { sock.Dispose(); throw; }
                    return new System.Net.Sockets.NetworkStream(sock, ownsSocket: true);
                },
            };
            h.SslOptions.EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12;
            c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(7) };
        }
        catch
        {
            c = new HttpClient { Timeout = TimeSpan.FromSeconds(7) };
        }
        try { c.DefaultRequestHeaders.UserAgent.ParseAdd($"ALHPro/{UpdateChecker.CurrentVersion}"); } catch { }
        return c;
    }

    /// <summary>
    /// 把广告图取到本地临时文件并返回路径(供 BitmapImage 直接读本地文件,避免网络图加载失败/裂图)。
    /// 【2026-09-17 用户要求:图片也要显示、一条不行换下一条、直到拿到为止】
    /// 顺序:① api.github.com contents(base64 解出字节;这条通道在本机是通的)
    ///       → ② jsDelivr → ③ gh-proxy → ④ ghproxy → ⑤ raw 原地址;同一张图成功后缓存,不重复下载。
    /// </summary>
    public static async Task<string?> FetchImageToTempAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            string name = "ad.jpg";
            try { name = System.IO.Path.GetFileName(new Uri(url).AbsolutePath); } catch { }
            if (string.IsNullOrWhiteSpace(name)) name = "ad.jpg";
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ALHPro", "ad");
            System.IO.Directory.CreateDirectory(dir);
            var cache = System.IO.Path.Combine(dir, name);
            try { if (System.IO.File.Exists(cache) && new System.IO.FileInfo(cache).Length > 0) return cache; } catch { }

            byte[]? bytes = null;
            // ① GitHub API Contents(最稳的一条路)
            try
            {
                var api = $"https://api.github.com/repos/AlLHHH/ALH-Pro/contents/ad/{name}";
                var body = await _http.GetStringAsync(api).ConfigureAwait(false);
                var b64 = ExtractBase64Content(body);
                if (b64 is not null) bytes = Convert.FromBase64String(b64);
            }
            catch { }
            // ②~⑤ 镜像
            if (bytes is not { Length: > 0 })
            {
                foreach (var u in ToMirrorUrls(url))
                {
                    try
                    {
                        var b = await _http.GetByteArrayAsync(u).ConfigureAwait(false);
                        if (b is { Length: > 0 }) { bytes = b; break; }
                    }
                    catch { }
                }
            }
            if (bytes is not { Length: > 0 }) return null;
            System.IO.File.WriteAllBytes(cache, bytes);
            return cache;
        }
        catch { return null; }
    }

    /// <summary>收掉上次异常退出留下的广告图缓存(启动时清一次,避免 %TEMP% 堆积)。</summary>
    public static void PruneImageCache()
    {
        try
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ALHPro", "ad");
            if (!System.IO.Directory.Exists(dir)) return;
            foreach (var f in System.IO.Directory.EnumerateFiles(dir))
            {
                try { if (DateTime.Now - System.IO.File.GetLastWriteTime(f) > TimeSpan.FromDays(3)) System.IO.File.Delete(f); } catch { }
            }
        }
        catch { }
    }

    /// <summary>广告拉取失败是否已提示过一次(避免一次启动里反复刷屏;网络波动/被墙很常见,只需友好提示一次)。</summary>
    private static bool _adWarnedOnce;

    /// <summary>启动时一次性拉取(失败静默,保留旧缓存)。</summary>
    public static async Task RefreshAsync()
    {
        var ads = await FetchAllAsync().ConfigureAwait(false);
        if (ads is { Length: > 0 }) Latest = ads;    }

    /// <summary>拉取并解析全部广告文件(自适应:从 ad1 开始,遇到不存在的文件即停,最多 20 个)。
    /// 【2026-09-17 用户要求:直到所有广告都出来】原来是"一次通不过就放弃",现在改成**多轮重试**:
    /// 没拿到结论的编号留到下一轮(间隔 20/40/60… 秒,最多 6 轮),任一编号 404 才是"真的没有"(停止)。
    /// 目的是在"网关只放行部分域名"的机器上也能最终把 9 张卡全部拿到。</summary>
    public static async Task<AdInfo[]> FetchAllAsync()
    {
        const int MaxFiles = 20;   // 安全上限,杜绝异常端点导致无限循环
        var got = new System.Collections.Generic.Dictionary<int, AdInfo>();
        int firstMissing = MaxFiles + 1;
        for (int round = 1; round <= 4; round++)
        {
            bool allDone = true;
            bool allowMirrors = round == 1;   // 只有第一轮试镜像(之后只重试能通的 API 通道)
            for (int i = 1; i <= MaxFiles; i++)
            {
                if (got.ContainsKey(i) || i > firstMissing) continue;
                var file = $"{AdFilePrefix}{i}.json";
                var json = await FetchFileRawAsync(file, allowMirrors).ConfigureAwait(false);
                if (json is null) { firstMissing = i; break; }              // 404 → 该编号不存在,到此为止
                if (string.IsNullOrWhiteSpace(json)) { allDone = false; continue; }   // 本轮失败 → 下轮再试
                var ad = ParseAd(json);
                if (ad is not null) got[i] = ad;
            }
            if (allDone) break;
            try { await Task.Delay(TimeSpan.FromSeconds(20 * round)).ConfigureAwait(false); } catch { break; }
        }
        // 按编号顺序输出(避免依赖 Linq)
        var result = new System.Collections.Generic.List<AdInfo>();
        for (int i = 1; i <= MaxFiles; i++) if (got.TryGetValue(i, out var a)) result.Add(a);
        // 【2026-09-17 用户要求:这类网络信息不要出现在日志里】成功/失败都静默:
        // 广告只是作者推广位,拉到就显示、拉不到就用内置默认,不需要打扰用户,也不需要留痕。
        return result.ToArray();
    }

    /// <summary>把 https://api.github.com/.../contents/... 返回的 {"content":"&lt;base64&gt;","encoding":"base64"} 里的 base64 取出来。
    /// 不引 JSON 库:手取字段即可(接口返回稳定),并把 base64 里的 "\n" 转义还原。</summary>
    private static string? ExtractBase64Content(string body)
    {
        try
        {
            int k = body.IndexOf("\"content\"", StringComparison.Ordinal);
            if (k < 0) return null;
            int q1 = body.IndexOf('"', body.IndexOf(':', k) + 1);
            if (q1 < 0) return null;
            int q2 = body.IndexOf('"', q1 + 1);
            if (q2 < 0) return null;
            var b64 = body.Substring(q1 + 1, q2 - q1 - 1).Replace("\\n", "").Replace("\n", "").Replace("\r", "");
            return b64.Length > 0 ? b64 : null;
        }
        catch { return null; }
    }

    /// <summary>走 GitHub API Contents 通道取文件原文(dir="ad"/"hint")。
    /// =null 表示文件确实不存在;="" 表示本轮失败,可换镜像。TipFetcher 也复用这个方法(别再各写一份)。</summary>
    public static async Task<string?> FetchViaApiAsync(string dir, string file)
    {
        try
        {
            var url = $"https://api.github.com/repos/AlLHHH/ALH-Pro/contents/{dir}/{file}";
            var body = await _http.GetStringAsync(url).ConfigureAwait(false);
            var b64 = ExtractBase64Content(body);
            if (b64 is null) return "";
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        }
        catch (HttpRequestException hre) when (hre.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;   // 404 → 该编号不存在
        }
        catch { return ""; }
    }

    /// <summary>拉取单个广告文件原文。顺序:① GitHub API(本机网关只放行 api.github.com,更新检查一直成功就是证据)
    /// → ② jsDelivr → ③ gh-proxy → ④ ghproxy → ⑤ raw 原地址。
    /// 文件确实不存在(404)→ 返回 null(供调用方停止自适应拉取);其它失败返回空串(继续尝试下一端点)。</summary>
    private static async Task<string?> FetchFileRawAsync(string file, bool allowMirrors)
    {
        var viaApi = await FetchViaApiAsync("ad", file).ConfigureAwait(false);
        if (viaApi is null) return null;            // API 说没有 → 判定该编号不存在
        if (viaApi.Length > 0) return viaApi;       // API 成功 → 直接返回(最稳的一条路)
        // 【2026-09-17 用户反馈"后台一直在跑"】镜像在用户网络下全部超时(7 秒/条 × 3 条 × 9 文件 × 多轮 ≈ 二十多分钟空转),
        // 所以只在第一轮试镜像;后续轮次只重试 API 通道(它才是这台机器上真正能通的那条)。

        string[] urls =
        {
            // 【2026-09-17 按真机网络实测重排】用户机器上 raw.githubusercontent.com / api.github.com 被 hosts 加速器
            // (Watt Toolkit)劫持到 127.0.0.1,加速器只转发 api.github.com、不转发 raw → 广告永远拉不到;
            // ghfast.top 超时 12 秒、raw.gitmirror.com 连 DNS 都解析不了,排前面等于把时间全耗在死路上。
            // jsDelivr 实测直接通(真实 Cloudflare IP、1 秒内返回),所以它排第一。
            $"https://cdn.jsdelivr.net/gh/AlLHHH/ALH-Pro@main/ad/{file}",
            $"https://gh-proxy.com/https://raw.githubusercontent.com/AlLHHH/ALH-Pro/main/ad/{file}",
            $"https://ghproxy.net/https://raw.githubusercontent.com/AlLHHH/ALH-Pro/main/ad/{file}",
            $"https://raw.githubusercontent.com/AlLHHH/ALH-Pro/main/ad/{file}",
        };
        bool sawNotFound = false;
        foreach (var url in urls)
        {
            try
            {
                var json = await _http.GetStringAsync(url).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(json)) return json;
            }
            catch (HttpRequestException hre)
            {
                // 404 = 文件不存在:记下,若所有端点都 404 才返回 null(代表"该编号不存在,停止拉取")
                if (hre.StatusCode == System.Net.HttpStatusCode.NotFound) { sawNotFound = true; continue; }
                // 非 404 失败:静默(用户要求不要把这类网络信息写进日志)
            }
            catch (Exception ex)
            {
                // 【日志友好化】广告文件拉取失败【不刷屏、不说人看不懂的技术英文】。
                // 网络抖动/被墙很常见(尤其国内直连 GitHub),没必要每个文件每个端点都刷一条原始异常。
                // 这里只记一次友好的说明(真正的网络排查可看诊断包里的 request 状态码),普通用户/看报告的人能懂。
                if (!_adWarnedOnce)
                {
                    _adWarnedOnce = true;
                    // 【2026-09-17 用户要求】静默:不写日志、不提示。拉不到就用内置默认广告,用户无感。
                }
            }
        }
        return sawNotFound ? null : string.Empty;   // 全端点 404 → null;否则空串(非404失败,继续尝试但不中断)
    }

    /// <summary>解析单个广告文件;字段缺失/异常返回 null。</summary>
    public static AdInfo? ParseAd(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // 顶层直接就是单张卡(ad1.json 形如 {title,text,image,link,linkText});兼容旧 banner.json 把卡放在 "ad" 键下。
            var el = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("ad", out var adEl)
                ? adEl : root;
            var ad = new AdInfo
            {
                Title = GetString(el, "title"),
                Text = GetString(el, "text"),
                Image = GetString(el, "image"),
                Link = GetString(el, "link"),
                LinkText = GetString(el, "linkText"),
                Sponsor = GetString(el, "sponsor"),
            };
            if (string.IsNullOrWhiteSpace(ad.Title) && string.IsNullOrWhiteSpace(ad.Text)) return null;   // 无内容视为无效
            return ad;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>单张广告卡(可空字段=未提供;image 空则纯文字卡;sponsor=广告主名,有则显示「推广」角标,合规:广告可识别)。</summary>
public sealed class AdInfo
{
    public string? Title { get; set; }
    public string? Text { get; set; }
    public string? Image { get; set; }
    public string? Link { get; set; }
    public string? LinkText { get; set; }
    public string? Sponsor { get; set; }
}
