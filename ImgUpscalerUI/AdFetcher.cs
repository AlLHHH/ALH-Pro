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
    /// <summary>广告文件列表(ad1.json~ad5.json)。</summary>
    private static readonly string[] AdFiles = { "ad1.json", "ad2.json", "ad3.json", "ad4.json", "ad5.json" };

    /// <summary>轮询间隔:10 分钟(作者改完 push,用户侧最长 10 分钟看到新内容)。</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(10);

    /// <summary>轮播间隔:每 60 秒换下一张广告卡(本地轮播,不联网)。</summary>
    public static readonly TimeSpan RotateInterval = TimeSpan.FromSeconds(60);

    /// <summary>最近一次缓存到的广告卡数组(线程安全);空=未拉到(隐藏区域)。</summary>
    public static volatile AdInfo[]? Latest;

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>启动时一次性拉取(失败静默,保留旧缓存)。</summary>
    public static async Task RefreshAsync()
    {
        var ads = await FetchAllAsync().ConfigureAwait(false);
        if (ads is { Length: > 0 }) Latest = ads;
    }

    /// <summary>拉取并解析全部广告文件;返回有效的卡数组(跳过 404/损坏/空),全失败返回 empty(非 null),由调用方隐藏。</summary>
    public static async Task<AdInfo[]> FetchAllAsync()
    {
        var result = new System.Collections.Generic.List<AdInfo>();
        foreach (var file in AdFiles)
        {
            var json = await FetchFileRawAsync(file).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json)) continue;
            var ad = ParseAd(json);
            if (ad is not null) result.Add(ad);
        }
        if (result.Count == 0) AppLogger.Info("[广告] 全部广告文件拉取失败/为空(静默隐藏)");
        return result.ToArray();
    }

    /// <summary>拉取单个广告文件原文(官方 + 镜像,任一成功即返回;失败返回 null)。</summary>
    private static async Task<string?> FetchFileRawAsync(string file)
    {
        string[] urls =
        {
            $"https://raw.githubusercontent.com/AlLHHH/ALH-Pro/main/ad/{file}",
            $"https://gh-proxy.com/https://raw.githubusercontent.com/AlLHHH/ALH-Pro/main/ad/{file}",
        };
        foreach (var url in urls)
        {
            try
            {
                _http.DefaultRequestHeaders.UserAgent.ParseAdd($"ALHPro/{UpdateChecker.CurrentVersion}");
                var json = await _http.GetStringAsync(url).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(json)) return json;
            }
            catch (Exception ex)
            {
                AppLogger.Info($"[广告] 文件 {file} 端点失败({url[..Math.Min(42, url.Length)]}...):" + ex.Message.Split('\n')[0]);
            }
        }
        return null;
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

/// <summary>单张广告卡(可空字段=未提供;image 空则纯文字卡)。</summary>
public sealed class AdInfo
{
    public string? Title { get; set; }
    public string? Text { get; set; }
    public string? Image { get; set; }
    public string? Link { get; set; }
    public string? LinkText { get; set; }
}
