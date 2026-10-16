using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using AlhPro.Core;

namespace ALHPro;

/// <summary>
/// 「广告」动态区数据源:拉取仓库 ad/ 目录下的 5 个独立广告文件 ad1.json~ad5.json(作者编辑后 push,用户端轮询到即更新)。
/// 复用 UpdateChecker 的多端点(官方 raw + 国内镜像)与 5 秒超时/静默策略。
/// 每个文件定义一张卡 {title,text,image,link,linkText};image 可空(无图则纯文字卡)。
/// 客户端本地每 30 秒轮播下一张(不联网),网络只在启动/每 10 分钟拉一次。
/// 【2026-10-17 用户要求】拉到的那一份**落本地缓存**(%LOCALAPPDATA%\ALHPro\ads\,见 <see cref="AdCache"/>)。
/// 启动先读缓存立刻显示上次的广告,再联网检测有没有更新 —— 不再"先摆内置兜底卡、等网络"。
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
    /// 实现在 <see cref="AdMirror.MirrorsFor"/> —— 纯逻辑、可单测。
    /// 【2026-10-04 修】原实现在这里手写,jsDelivr 那条拼成了 <c>@main/ad/ad/img4.jpg</c>(多一层 ad/)→ 永远 404。
    /// raw.githubusercontent.com 国内直连常被墙/超时,不放在首选;jsDelivr 实测最稳,排最前。</summary>
    public static System.Collections.Generic.IEnumerable<string> ToMirrorUrls(string url) => AdMirror.MirrorsFor(url);

    /// <summary>最近一次拿到的广告卡数组(线程安全);启动先由 <see cref="LoadCachedAds"/> 换成"上次成功拉到的那份"
    /// (本地读不到才留下面两张内置兜底卡),网络拉取成功后再替换。</summary>
    public static volatile AdInfo[] Latest = DefaultAds();

    /// <summary>界面当前这条列表的"张数下限"(本地缓存的张数 / 上一轮完整结果的张数)。
    /// 渐进发布时不许发比它更短的列表:半截的一轮(某几个文件这轮没拉到)不该把一整排广告顶少。</summary>
    private static int _adBaselineCount;

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

    /// <summary>图片缓存有效期:同一个文件名超过这段时间就重新下载一次。
    /// 【2026-10-04 修】原来只判断"缓存文件存在且非空"就永远复用 ⇒ 作者替换同名图片后界面一直显示旧图,
    /// 只能等启动时清掉 3 天前的文件。现在:超过有效期重下;广告内容一改立即重下(见 RefreshAsync)。</summary>
    public static readonly TimeSpan ImageCacheTtl = TimeSpan.FromHours(2);

    /// <summary>下载失败后的静默期:这段时间内不再重试同一张图(有旧图就先用旧图),避免离线时每 30 秒各端点轮一遍。</summary>
    private static readonly TimeSpan ImageRetryCooldown = TimeSpan.FromMinutes(10);

    private static readonly System.Collections.Generic.Dictionary<string, DateTime> _imgRetryAfterUtc = new();

    /// <summary>
    /// 把广告图取到本地临时文件并返回路径(供 BitmapImage 直接读本地文件,避免网络图加载失败/裂图)。
    /// 【2026-09-17 用户要求:图片也要显示、一条不行换下一条、直到拿到为止】
    /// 顺序:① api.github.com contents(base64 解出字节;这条通道在本机是通的)
    ///       → ② jsDelivr → ③ gh-proxy → ④ ghproxy → ⑤ raw 原地址。
    /// 缓存策略见 <see cref="ImageCacheTtl"/>:过期或广告内容变化就重下,重下失败则退回旧图(不裂图)。
    /// <para>【2026-10-17 <paramref name="preferLocal"/>】只求"先把图显示出来"的调用方(渲染广告卡、后台预取)传 true:
    /// 本地已有旧图就**立刻**返回,刷新丢到后台做(见 <see cref="ScheduleImageRefresh"/>)。
    /// 原来必须先在网上等一轮(最坏 7 秒/端点 × 5 个端点)图才补上来 —— 那正是"打开软件广告图还要等"的观感来源。</para>
    /// </summary>
    public static async Task<string?> FetchImageToTempAsync(string url, bool preferLocal = false)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            var name = AdMirror.CacheFileName(url);
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ALHPro", "ad");
            System.IO.Directory.CreateDirectory(dir);
            var cache = System.IO.Path.Combine(dir, name);

            // 缓存能不能直接用?存在 + 非空 + 写入时间不早于 max(现在 - 有效期, 广告内容上次变化时间)。
            // 后者保证"作者换了同名图片"最多等一个有效期就自动刷新,而不是永远不刷新。
            var minWrite = DateTime.UtcNow - ImageCacheTtl;
            if (_adsChangedAtUtc > minWrite) minWrite = _adsChangedAtUtc;
            bool exists = false; long len = 0; DateTime write = DateTime.MinValue;
            try
            {
                var fi = new System.IO.FileInfo(cache);
                exists = fi.Exists;
                if (exists) { len = fi.Length; write = fi.LastWriteTimeUtc; }
            }
            catch { }
            if (AdMirror.IsCacheReusable(exists, len, write, minWrite)) return cache;

            // 【2026-10-17】本地有旧图、调用方又只求"先显示出来":立刻给旧图,刷新放后台(下一次轮播/重绘就是新图)。
            // 这样"启动先把上次的广告连图一起显示出来"才真的成立(否则图文卡还要等一轮网络才补上图)。
            if (preferLocal && exists && len > 0)
            {
                ScheduleImageRefresh(url, cache);
                return cache;
            }

            // 刚下载失败过 → 静默期内不再重试(有旧图就用旧图,不裂图也不空转)。
            if (_imgRetryAfterUtc.TryGetValue(cache, out var until) && DateTime.UtcNow < until)
                return exists && len > 0 ? cache : null;

            byte[]? bytes = null;
            // ① GitHub API Contents(有额度时最稳的一条路)。【2026-10-04】API 已在冷却期(403 限流/被墙)就跳过它,
            // 直接走镜像 —— 别为了几张图再把"60 次/小时"的额度花掉,那是"检查更新"也要用的。
            if (DateTime.UtcNow >= _apiCooldownUntilUtc)
            {
                try
                {
                    var api = $"https://api.github.com/repos/AlLHHH/ALH-Pro/contents/ad/{name}";
                    var body = await _http.GetStringAsync(api).ConfigureAwait(false);
                    var b64 = ExtractBase64Content(body);
                    if (b64 is not null) { bytes = Convert.FromBase64String(b64); RememberChannel(api); }
                }
                catch
                {
                    _apiCooldownUntilUtc = DateTime.UtcNow + ApiRetryCooldown;   // 403/超时 → 与 JSON 通道共用冷却
                }
            }
            // ②~⑤ 镜像(上一轮用通的通道排最前,免得每张图都先等死通道超时)
            if (bytes is not { Length: > 0 })
            {
                foreach (var u in AdMirror.PreferHost(ToMirrorUrls(url), _preferredHost))
                {
                    try
                    {
                        var b = await _http.GetByteArrayAsync(u).ConfigureAwait(false);
                        if (b is { Length: > 0 }) { bytes = b; RememberChannel(u); break; }
                    }
                    catch { }
                }
            }
            if (bytes is not { Length: > 0 })
            {
                _imgRetryAfterUtc[cache] = DateTime.UtcNow + ImageRetryCooldown;
                return exists && len > 0 ? cache : null;   // 下载失败:有旧图先用旧图
            }
            _imgRetryAfterUtc.Remove(cache);
            System.IO.File.WriteAllBytes(cache, bytes);
            return cache;
        }
        catch { return null; }
    }

    /// <summary>后台补刷一张"已在本地但已过期"的广告图:同一张最多每 10 分钟排一次,失败静默(旧图照用)。
    /// 【2026-10-17】给 <see cref="FetchImageToTempAsync"/> 的 preferLocal 用 —— 先显示旧图不等于不管更新:
    /// 刷到了会直接覆盖缓存文件,之后的轮播/重绘就是新图(作者替换同名图片的场景仍有兜底)。</summary>
    private static void ScheduleImageRefresh(string url, string cache)
    {
        try
        {
            lock (_imgRefreshAtUtc)
            {
                if (_imgRefreshAtUtc.TryGetValue(cache, out var until) && DateTime.UtcNow < until) return;
                _imgRefreshAtUtc[cache] = DateTime.UtcNow + ImageRetryCooldown;
            }
            // 不带 preferLocal:走完整下载路径(下载/写缓存/失败静默都在那条路上)
            _ = Task.Run(async () => { try { await FetchImageToTempAsync(url).ConfigureAwait(false); } catch { } });
        }
        catch { }
    }

    private static readonly System.Collections.Generic.Dictionary<string, DateTime> _imgRefreshAtUtc = new();

    /// <summary>收掉上次异常退出留下的广告图缓存(启动时清一次,避免 %TEMP% 堆积)。
    /// 【2026-10-04】从"清 3 天前"收紧到"清 1 天前":文件名相同的图片本来就可能被替换,缓存留太久只会让人以为改图没生效。
    /// 【2026-10-17】多一个 <paramref name="keepFileNames"/>:广告列表现在跨启动留着(见 <see cref="LoadCachedAds"/>),
    /// 它引用的图**无论多旧都留着** —— 否则下次打开又得联网下图,等于白缓存一场。</summary>
    public static void PruneImageCache(System.Collections.Generic.ISet<string>? keepFileNames = null)
    {
        try
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ALHPro", "ad");
            if (!System.IO.Directory.Exists(dir)) return;
            var cutoff = DateTime.Now - TimeSpan.FromDays(1);
            foreach (var f in System.IO.Directory.EnumerateFiles(dir))
            {
                try
                {
                    if (AdCache.IsImagePrunable(System.IO.Path.GetFileName(f),
                            System.IO.File.GetLastWriteTime(f), cutoff, keepFileNames))
                        System.IO.File.Delete(f);
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>广告拉取失败是否已提示过一次(避免一次启动里反复刷屏;网络波动/被墙很常见,只需友好提示一次)。</summary>
    private static bool _adWarnedOnce;

    /// <summary>安全上限:最多探测 ad1..ad20(自适应停止,这里只是兜底)。与本地缓存同一口径(<see cref="AdCache.MaxFiles"/>)。</summary>
    private const int MaxFiles = AdCache.MaxFiles;

    /// <summary>GitHub API Contents 通道(URL 模板,`{0}` = 广告文件名)。它返回的是 base64 包着 JSON,所以单独走
    /// <see cref="FetchViaApiAsync"/> 解析;这里用真实 URL 模板是为了"哪家通道"能统一按 host 认(见 AdMirror.HostOf)。</summary>
    private const string ApiChannel = "https://api.github.com/repos/AlLHHH/ALH-Pro/contents/ad/{0}";

    /// <summary>上次用通的那家通道 host(广告文件与广告图共用),**跨启动记住**。
    /// 【2026-10-04 修「广告等好久都不出现」】冷启动时不必再从"本机必超时的 jsDelivr(7 秒)"开始等:
    /// 真机实测,记住通道后第一条广告文件 0.3 秒就回来(冷启动 8.7 秒)。</summary>
    private static volatile string? _preferredHost = LoadPreferredHost();

    /// <summary>GitHub API Contents 通道暂停使用的截止时刻(UTC)。
    /// 【2026-10-04 定位到的根因之一】一次刷新最多要问 20 个文件,而**未认证** api.github.com 限额只有
    /// 60 次/小时/出口 IP,还和"检查更新"共用 —— 广告把额度吃干后更新检查也一起 403
    /// (诊断日志 2026-10-04 15:44:18 就是 `403 (rate limit exceeded)`;实测 403 后每个文件还得再等
    /// jsDelivr 超时 7 秒 + raw 超时 9 秒,9 个文件就是两分钟)。
    /// 现在 API 一旦失败就冷却:冷却期内一个文件都不问它,优先走镜像,把额度留给更新检查。</summary>
    private static DateTime _apiCooldownUntilUtc = DateTime.MinValue;

    /// <summary>API 通道失败后的冷却时长(取轮询间隔:下次轮询时再试一次,通了就继续用它 —— 它是最快的一条)。</summary>
    private static readonly TimeSpan ApiRetryCooldown = TimeSpan.FromMinutes(10);

    /// <summary>把"上次用通的通道 host"存在本地(纯文本一行),下次启动直接先试它。</summary>
    private static string AdChannelStateFile => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ALHPro", "ad-channel.txt");

    /// <summary>读上次记住的通道(读不到就 null = 按默认顺序试)。全程 try/catch:这只是提速缓存,不影响任何功能。</summary>
    private static string? LoadPreferredHost()
    {
        try
        {
            var f = AdChannelStateFile;
            if (System.IO.File.Exists(f))
            {
                var s = System.IO.File.ReadAllText(f).Trim();
                if (s.Length is > 0 and < 64) return s;
            }
        }
        catch { }
        return null;
    }

    /// <summary>记住这次用通的通道(只记 host;写文件失败不影响任何功能)。</summary>
    private static void RememberChannel(string? url)
    {
        var host = AdMirror.HostOf(url);
        if (host.Length == 0 || string.Equals(host, _preferredHost, StringComparison.OrdinalIgnoreCase)) return;
        _preferredHost = host;
        try
        {
            var f = AdChannelStateFile;
            var dir = System.IO.Path.GetDirectoryName(f);
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(f, host);
        }
        catch { }
    }

    /// <summary>广告列表的本地缓存目录(<c>%LOCALAPPDATA%\ALHPro\ads\</c>)。格式与判据在纯逻辑类 <see cref="AdCache"/>。</summary>
    private static string AdsCacheDir =>
        AdCache.Dir(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>上次成功保存本地缓存的时刻(UTC;读不到就是 MinValue),只用于日志。</summary>
    public static DateTime CacheSavedUtc { get; private set; } = DateTime.MinValue;

    /// <summary>启动时先把**上次成功拉到的那份广告**从本地读回来(同步、极快、全程 try/catch)。
    /// 【2026-10-17 用户要求】「上次刷新成功后缓存在本地,直到下次打开时使用时检测到更新然后再更新广告」:
    /// 原实现每次启动都是两张内置兜底卡先顶着、等一轮网络(本机实测冷启动 8.7 秒,被墙时几分钟),
    /// 现在打开就能看到上次的广告,网络那边只是"检测有没有更新"。
    /// <para>读回来时同步记住内容指纹:这一轮再拉到同样内容就不算"变了",不会被误判成更新而白刷图片缓存。</para>
    /// <returns>true = 读到了(界面可以直接显示真广告)。</returns></summary>
    public static bool LoadCachedAds()
    {
        try
        {
            var dir = AdsCacheDir;
            if (!System.IO.Directory.Exists(dir)) return false;
            var list = new System.Collections.Generic.List<AdInfo>();
            for (int i = 1; i <= AdCache.MaxFiles; i++)
            {
                var f = System.IO.Path.Combine(dir, AdCache.FileNameOf(i));
                if (!System.IO.File.Exists(f)) break;   // 断号即止(与联网拉取同一口径)
                var ad = AdCache.ParseAd(System.IO.File.ReadAllText(f));
                if (ad is null) continue;               // 单个文件坏了只丢它自己,后面照读
                list.Add(ad);
            }
            if (list.Count == 0) return false;
            var ads = list.ToArray();
            Latest = ads;
            _adBaselineCount = ads.Length;
            _lastAdsFingerprint = AdMirror.Fingerprint(FingerprintLines(ads));
            try
            {
                var stamp = System.IO.Path.Combine(dir, AdCache.StampFileName);
                if (System.IO.File.Exists(stamp)
                    && DateTime.TryParse(System.IO.File.ReadAllText(stamp).Trim(), null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var t))
                    CacheSavedUtc = t.ToUniversalTime();
            }
            catch { }
            return true;
        }
        catch { return false; }
    }

    /// <summary>把这一轮**完整**拉到的广告写进本地缓存(残缺的一轮不写,免得把好缓存覆盖成半截)。
    /// 先写 <c>.tmp</c> 再改名:中途断电/被杀也只会留下一个不被读的文件,不会得到半截 JSON。</summary>
    private static void SaveCachedAds(AdInfo[] ads)
    {
        try
        {
            var dir = AdsCacheDir;
            System.IO.Directory.CreateDirectory(dir);
            for (int i = 0; i < ads.Length; i++)
            {
                var f = System.IO.Path.Combine(dir, AdCache.FileNameOf(i + 1));
                var tmp = f + ".tmp";
                System.IO.File.WriteAllText(tmp, AdCache.ToJson(ads[i]), new System.Text.UTF8Encoding(false));
                System.IO.File.Move(tmp, f, overwrite: true);
            }
            // 作者删了广告 → 把多出来的旧缓存文件收掉(编号在本次张数之外的;认不出编号的不动)
            foreach (var f in System.IO.Directory.EnumerateFiles(dir, "ad*.json"))
            {
                var idx = AdCache.IndexOf(System.IO.Path.GetFileName(f));
                if (idx <= 0 || idx > ads.Length) { try { System.IO.File.Delete(f); } catch { } }
            }
            var savedUtc = DateTime.UtcNow;
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, AdCache.StampFileName), savedUtc.ToString("o"));
            CacheSavedUtc = savedUtc;
            _adBaselineCount = ads.Length;
        }
        catch { }
    }

    /// <summary>上一次拿到的广告内容指纹,以及内容发生变化的时刻(UTC)。
    /// 【2026-10-04】作者一改 ad/adN.json(文案或图片地址),指纹就变 → 图片缓存立即失效、下次显示时重新下载,
    /// 不用等缓存有效期,也避免"改了图没反应"。</summary>
    private static string _lastAdsFingerprint = "";
    private static DateTime _adsChangedAtUtc = DateTime.MinValue;

    /// <summary>把广告列表摊成指纹用的行(按编号顺序,内容一变指纹就变)。</summary>
    private static System.Collections.Generic.List<string> FingerprintLines(AdInfo[] ads)
    {
        var lines = new System.Collections.Generic.List<string>(ads.Length);
        foreach (var a in ads)
            lines.Add($"{a.Title}|{a.Text}|{a.Image}|{a.Link}|{a.LinkText}|{a.Sponsor}");
        return lines;
    }

    /// <summary>启动时一次性拉取(失败静默,保留旧缓存)。
    /// <paramref name="onUpdated"/>:每拿到一张新卡就回调一次(界面借此立刻重绘),可空。</summary>
    public static async Task RefreshAsync(Action? onUpdated = null)
    {
        // 【2026-10-04 修「广告等好久都不出现」】原实现是"整轮全拉完才 Latest = ads",
        // 于是首屏那两张内置兜底卡要顶到整轮(最坏几分钟)结束才被在线广告换掉。
        // 现在每拿到一张就先发布一次:在线广告一张一张出现,不再等整轮。
        void Publish(AdInfo[] ads)
        {
            if (ads is not { Length: > 0 }) return;
            Latest = ads;
            try { onUpdated?.Invoke(); } catch { }
        }

        // 【2026-10-17】渐进发布加一道下限:本地缓存/上一轮完整结果有 N 张时,半截的一轮(某个文件这轮没拉到、
        // 只拿到 2 张)不许把界面上那排广告顶少 —— 上一轮的内容先留着,等这一轮完整了再换。
        void PublishProgressive(AdInfo[] ads)
        {
            if (ads is not { Length: > 0 } || ads.Length < _adBaselineCount) return;
            Publish(ads);
        }

        var (ads, complete) = await FetchAllDetailedAsync(PublishProgressive).ConfigureAwait(false);
        if (ads is not { Length: > 0 }) return;
        var fp = AdMirror.Fingerprint(FingerprintLines(ads));
        if (!string.Equals(fp, _lastAdsFingerprint, StringComparison.Ordinal))
        {
            _lastAdsFingerprint = fp;
            _adsChangedAtUtc = DateTime.UtcNow;   // 内容变了 → 之前缓存的广告图一律按过期处理
        }
        Publish(ads);
        // 【2026-10-17 用户要求】整份拉到才落本地缓存:残缺的一轮不写,免得把上次的好缓存换成半截。
        if (complete) SaveCachedAds(ads);

        // 【2026-10-04 用户报「广告图也要等很久才加载出来」】列表一拿到就**后台预取图片**:
        // 图片原来只在"轮播到那张卡"的一瞬间才开始下载(文字先出现、图几秒后才补上)。预取后,
        // 等 30 秒轮播转到带图的卡时,图早就在本地缓存里了(缓存 2 小时有效,有效期内一个请求都不发)。
        _ = PrefetchImagesAsync(ads);
    }

    /// <summary>已经预取成功的图片地址(同一张不用重复排队)。</summary>
    private static readonly System.Collections.Generic.HashSet<string> _prefetched
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>后台把广告图先下到本地缓存,轮到显示时就是本地文件(不再"文字先来、图等几秒")。
    /// 顺序执行、失败静默:不并发打网络,顺带把"哪家通道能通"喂给后面的图片(见 <see cref="RememberChannel"/>)。
    /// 缓存仍在有效期内时 <see cref="FetchImageToTempAsync"/> 直接返回本地文件,所以重复调用也很便宜。</summary>
    public static async Task PrefetchImagesAsync(AdInfo[]? ads = null)
    {
        try
        {
            var list = ads is { Length: > 0 } ? ads : Latest;
            if (list is not { Length: > 0 }) return;
            foreach (var a in list)
            {
                var url = a?.Image;
                if (string.IsNullOrWhiteSpace(url)) continue;
                lock (_prefetched) { if (_prefetched.Contains(url)) continue; }
                var local = await FetchImageToTempAsync(url, preferLocal: true).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(local))
                {
                    lock (_prefetched) { _prefetched.Add(url); }
                }
            }
        }
        catch { }
    }

    /// <summary>按编号顺序把已拿到的卡摊成数组(增量发布与最终结果共用)。</summary>
    private static AdInfo[] Snapshot(System.Collections.Generic.Dictionary<int, AdInfo> got)
    {
        var result = new System.Collections.Generic.List<AdInfo>(got.Count);
        for (int i = 1; i <= MaxFiles; i++) if (got.TryGetValue(i, out var a)) result.Add(a);
        return result.ToArray();
    }

    /// <summary>拉取并解析全部广告文件,并说明这一轮**完整不完整**(供 <see cref="RefreshAsync"/> 决定能不能落本地缓存)。
    /// 【2026-09-17 用户要求:直到所有广告都出来】原来是"一次通不过就放弃",现在改成**多轮重试**:
    /// 没拿到结论的编号留到下一轮(间隔 20/40/60… 秒,最多 6 轮),任一编号 404 才是"真的没有"(停止)。
    /// 目的是在"网关只放行部分域名"的机器上也能最终把 9 张卡全部拿到。
    /// <para><paramref name="onProgress"/>:每多拿到一张就回调一次(已拿到的按编号顺序),界面可以边拉边显示。</para>
    /// <para>【2026-10-17】Complete = 从 ad1 到"第一个不存在的编号"之间每个编号都拿到了 —— 只有这种完整的一轮
    /// 才允许覆盖本地缓存,否则一次网络抖动就会把用户攒下的整份广告换成残缺的几张。</para></summary>
    public static async Task<(AdInfo[] Ads, bool Complete)> FetchAllDetailedAsync(Action<AdInfo[]>? onProgress = null)
    {
        var got = new System.Collections.Generic.Dictionary<int, AdInfo>();
        int published = 0;
        int firstMissing = MaxFiles + 1;
        for (int round = 1; round <= 4; round++)
        {
            // 第二轮起只走 API 通道;API 已冷却(限流/被墙)就一条通道都没有 → 不必空等 20/40/60 秒。
            if (round > 1 && DateTime.UtcNow < _apiCooldownUntilUtc) break;
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
                if (ad is not null)
                {
                    got[i] = ad;
                    // 逐张发布:在线广告一张一张顶掉内置兜底,不用等整轮跑完(见 RefreshAsync 的注释)。
                    if (onProgress is not null && got.Count != published)
                    {
                        published = got.Count;
                        try { onProgress(Snapshot(got)); } catch { }
                    }
                }
            }
            if (allDone) break;
            try { await Task.Delay(TimeSpan.FromSeconds(20 * round)).ConfigureAwait(false); } catch { break; }
        }
        // 按编号顺序输出(避免依赖 Linq)
        // 【2026-09-17 用户要求:这类网络信息不要出现在日志里】成功/失败都静默:
        // 广告只是作者推广位,拉到就显示、拉不到就用内置默认,不需要打扰用户,也不需要留痕。
        // 完整性:1..(第一个不存在的编号-1) 全都拿到才算完整(见方法注释)。
        bool complete = true;
        for (int i = 1; i < firstMissing; i++)
            if (!got.ContainsKey(i)) { complete = false; break; }
        return (Snapshot(got), complete);
    }

    /// <summary>拉取全部广告文件(只要结果;完整性判据见 <see cref="FetchAllDetailedAsync"/>)。</summary>
    public static async Task<AdInfo[]> FetchAllAsync(Action<AdInfo[]>? onProgress = null)
        => (await FetchAllDetailedAsync(onProgress).ConfigureAwait(false)).Ads;

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

    /// <summary>镜像通道(URL 模板,`{0}` = 广告文件名)。顺序按真机实测可用性排 ——
    /// 说明见 <see cref="FetchFileRawAsync"/> 里的历史注释(jsDelivr 排第一)。</summary>
    private static string[] MirrorChannels() => new[]
    {
        $"https://cdn.jsdelivr.net/gh/{AdMirror.Repo}@{AdMirror.Branch}/ad/{{0}}",
        $"https://gh-proxy.com/https://raw.githubusercontent.com/{AdMirror.Repo}/{AdMirror.Branch}/ad/{{0}}",
        $"https://ghproxy.net/https://raw.githubusercontent.com/{AdMirror.Repo}/{AdMirror.Branch}/ad/{{0}}",
        $"https://raw.githubusercontent.com/{AdMirror.Repo}/{AdMirror.Branch}/ad/{{0}}",
    };

    /// <summary>拉取单个广告文件原文。顺序:① GitHub API(有额度时最稳最快)
    /// → ② cdn.jsdelivr.net → ③ gh-proxy.com → ④ ghproxy.net → ⑤ raw 原地址。
    /// 文件确实不存在(404)→ 返回 null(供调用方停止自适应拉取);其它失败返回空串(继续尝试下一端点)。
    /// <para>【2026-10-04】两处提速:API 失败即冷却(免得 20 个文件白问 20 次、把 60 次/小时的额度吃干);
    /// 记住上次成功的通道并优先它(见 <see cref="AdMirror.PreferHost"/>)—— 否则每个文件都要重头等一遍死通道超时。</para></summary>
    private static async Task<string?> FetchFileRawAsync(string file, bool allowMirrors)
    {
        // 候选通道:API(不在冷却期时)+ 镜像(只在第一轮)。上次用通的那家排最前。
        var channels = new System.Collections.Generic.List<string>(6);
        if (DateTime.UtcNow >= _apiCooldownUntilUtc) channels.Add(ApiChannel);
        if (allowMirrors) channels.AddRange(MirrorChannels());
        var ordered = AdMirror.PreferHost(channels, _preferredHost);

        // 【2026-09-17 用户反馈"后台一直在跑"】镜像在用户网络下全部超时(7 秒/条 × 3 条 × 9 文件 × 多轮 ≈ 二十多分钟空转),
        // 所以只在第一轮试镜像;后续轮次只重试 API 通道(它才是这台机器上真正能通的那条)。

        bool sawNotFound = false;
        foreach (var ch in ordered)
        {
            if (ch == ApiChannel)
            {
                var viaApi = await FetchViaApiAsync("ad", file).ConfigureAwait(false);
                if (viaApi is null) return null;              // API 明确说文件不存在 → 停止自适应
                if (viaApi.Length > 0) { RememberChannel(ApiChannel); return viaApi; }
                // 403(限流)/超时/被墙 → 冷却:后面的文件不再逐个问 API(额度留给"检查更新")。
                _apiCooldownUntilUtc = DateTime.UtcNow + ApiRetryCooldown;
                continue;
            }

            var url = string.Format(ch, file);
            try
            {
                var json = await _http.GetStringAsync(url).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(json)) { RememberChannel(url); return json; }
            }
            catch (HttpRequestException hre)
            {
                // 404 = 文件不存在:记下,若所有端点都 404 才返回 null(代表"该编号不存在,停止拉取")
                if (hre.StatusCode == System.Net.HttpStatusCode.NotFound) { sawNotFound = true; continue; }
                // 非 404 失败:静默(用户要求不要把这类网络信息写进日志)
            }
            catch (Exception)
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

    /// <summary>解析单个广告文件;字段缺失/异常返回 null。
    /// 【2026-10-17】实现挪到 <see cref="AdCache.ParseAd"/> —— 本地缓存要"写进去还能读回来",解析规则必须可单测。</summary>
    public static AdInfo? ParseAd(string json) => AdCache.ParseAd(json);
}
