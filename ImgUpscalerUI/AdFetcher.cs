using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace ALHPro;

/// <summary>
/// 「广告 + 弹幕」动态区数据源:拉取仓库里的 ad/banner.json(作者编辑后 push,用户端轮询到即更新)。
/// 复用 UpdateChecker 的多端点(官方 raw + 国内镜像)与 5 秒超时/静默策略。
/// 失败(无网/超时/JSON 异常)一律静默返回 null,由调用方隐藏整个区域,不打扰用户。
/// </summary>
public static class AdFetcher
{
    /// <summary>banner.json 拉取端点(官方 raw + 实测可用的国内镜像)。</summary>
    private static readonly string[] Endpoints =
    {
        "https://raw.githubusercontent.com/AlLHHH/ALH-Pro/main/ad/banner.json",                 // 官方
        "https://gh-proxy.com/https://raw.githubusercontent.com/AlLHHH/ALH-Pro/main/ad/banner.json", // 镜像(国内)
    };

    /// <summary>轮询间隔:10 分钟(作者改完 push,用户侧最长 10 分钟看到)。</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(10);

    /// <summary>最近一次拉取到的广告+弹幕(线程安全);null=未拉到(隐藏区域)。</summary>
    public static volatile AdData? Latest;

    /// <summary>启动时一次性拉取(失败静默)。</summary>
    public static async Task RefreshAsync()
    {
        var data = await FetchAsync().ConfigureAwait(false);
        if (data is not null) Latest = data;
    }

    /// <summary>拉取并解析 banner.json;全部端点失败返回 null。</summary>
    public static async Task<AdData?> FetchAsync()
    {
        foreach (var url in Endpoints)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd($"ALHPro/{UpdateChecker.CurrentVersion}");
                var json = await http.GetStringAsync(url).ConfigureAwait(false);
                var data = Parse(json);
                AppLogger.Info($"[广告] 拉取成功({url[..Math.Min(40, url.Length)]}...),弹幕 {data?.Danmaku?.Count ?? 0} 条");
                return data;
            }
            catch (Exception ex)
            {
                AppLogger.Info($"[广告] 端点失败({url[..Math.Min(40, url.Length)]}...):" + ex.Message.Split('\n')[0]);
            }
        }
        AppLogger.Info("[广告] 拉取失败(全部端点,静默隐藏)");
        return null;
    }

    /// <summary>解析 banner.json;字段缺失/异常返回 null。</summary>
    public static AdData? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var ad = default(AdInfo);
            if (root.TryGetProperty("ad", out var adEl) && adEl.ValueKind == JsonValueKind.Object)
            {
                ad = new AdInfo
                {
                    Title = GetString(adEl, "title"),
                    Text = GetString(adEl, "text"),
                    Image = GetString(adEl, "image"),
                    Link = GetString(adEl, "link"),
                    LinkText = GetString(adEl, "linkText"),
                };
            }
            var danmaku = new System.Collections.Generic.List<string>();
            if (root.TryGetProperty("danmaku", out var dmEl) && dmEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in dmEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                        danmaku.Add(item.GetString()!.Trim());
                }
            }
            if (ad is null && danmaku.Count == 0) return null;   // 全是空的视为无效
            return new AdData { Ad = ad, Danmaku = danmaku };
        }
        catch
        {
            return null;
        }
    }

    private static string? GetString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>广告 + 弹幕(解析自 ad/banner.json)。</summary>
public sealed class AdData
{
    public AdInfo? Ad { get; set; }
    public System.Collections.Generic.List<string> Danmaku { get; set; } = new();
}

/// <summary>单条广告信息(可空字段=未提供)。</summary>
public sealed class AdInfo
{
    public string? Title { get; set; }
    public string? Text { get; set; }
    public string? Image { get; set; }
    public string? Link { get; set; }
    public string? LinkText { get; set; }
}
