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
            // 按实测可靠性排序:jsDelivr → gh-proxy → ghproxy →(最后才 raw 原图,直连最不稳)
            try
            {
                var rel = url.Substring(url.IndexOf("main/ad/") + "main/".Length);
                list.Add("https://cdn.jsdelivr.net/gh/AlLHHH/ALH-Pro@main/ad/" + rel);
            }
            catch { }
            list.Add("https://gh-proxy.com/" + url);
            list.Add("https://ghproxy.net/" + url);
            list.Add(url);   // 原图最后(大部分情况用不上)
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

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>启动时一次性拉取(失败静默,保留旧缓存)。</summary>
    public static async Task RefreshAsync()
    {
        var ads = await FetchAllAsync().ConfigureAwait(false);
        if (ads is { Length: > 0 }) Latest = ads;    }

    /// <summary>拉取并解析全部广告文件(自适应:从 ad1 开始,遇到不存在的文件即停,最多 20 个);
    /// 返回有效的卡数组(跳过损坏/空),全失败返回 empty(非 null),由调用方隐藏。</summary>
    public static async Task<AdInfo[]> FetchAllAsync()
    {
        var result = new System.Collections.Generic.List<AdInfo>();
        const int MaxFiles = 20;   // 安全上限,杜绝异常端点导致无限循环
        for (int i = 1; i <= MaxFiles; i++)
        {
            var file = $"{AdFilePrefix}{i}.json";
            var json = await FetchFileRawAsync(file).ConfigureAwait(false);
            if (json is null) break;   // 404:该编号文件不存在 → 停止(后续编号也不会有)
            if (string.IsNullOrWhiteSpace(json)) continue;
            var ad = ParseAd(json);
            if (ad is not null) result.Add(ad);
        }
        if (result.Count == 0) AppLogger.Info("[广告] 全部广告文件拉取失败/为空(静默隐藏)");
        return result.ToArray();
    }

    /// <summary>拉取单个广告文件原文(按实测可靠性排序:jsDelivr 最稳→gh-proxy→ghproxy→raw 最后;任一成功即返回)。
    /// 文件确实不存在(404)→ 返回 null(供调用方停止自适应拉取);其它失败返回空串(继续尝试下一端点)。</summary>
    private static async Task<string?> FetchFileRawAsync(string file)
    {
        string[] urls =
        {
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
                _http.DefaultRequestHeaders.UserAgent.ParseAdd($"ALHPro/{UpdateChecker.CurrentVersion}");
                var json = await _http.GetStringAsync(url).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(json)) return json;
            }
            catch (HttpRequestException hre)
            {
                // 404 = 文件不存在:记下,若所有端点都 404 才返回 null(代表"该编号不存在,停止拉取")
                if (hre.StatusCode == System.Net.HttpStatusCode.NotFound) { sawNotFound = true; continue; }
            }
            catch (Exception ex)
            {
                AppLogger.Info($"[广告] 文件 {file} 端点失败({url[..Math.Min(42, url.Length)]}...):" + ex.Message.Split('\n')[0]);
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
