using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace ALHPro;

/// <summary>
/// 「右侧提示」数据源:拉取仓库 hint/ 目录下的独立提示文件 hint1.json~hint5.json(作者编辑后 push,用户端轮询到即更新)。
/// 与 AdFetcher 思路一致,但是独立文件夹(hint/)、独立上传、独立轮播,互不影响。
/// 每个文件定义一条纯文本提示 {text, color, link};text 为文案,color 为文字颜色(hex,#RRGGBB 或 #AARRGGBB),link 可选(点击跳转)。
/// 客户端本地每 30 秒轮播下一条(不联网),网络只在启动/每 10 分钟拉一次缓存。
/// 失败(无网/超时/JSON 异常)一律静默:单文件 404/损坏跳过,全坏则隐藏右侧提示,不打扰用户。
/// </summary>
public static class TipFetcher
{
    /// <summary>提示文件列表(hint1.json~hint5.json)。</summary>
    private static readonly string[] TipFiles = { "hint1.json", "hint2.json", "hint3.json", "hint4.json", "hint5.json" };

    /// <summary>轮询间隔:10 分钟(作者改完 push,用户侧最长 10 分钟看到新内容)。</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(10);

    /// <summary>轮播间隔:每 30 秒换下一条提示(本地轮播,不联网)。</summary>
    public static readonly TimeSpan RotateInterval = TimeSpan.FromSeconds(30);

    /// <summary>最近一次缓存到的提示数组(线程安全);空=未拉到(隐藏)。</summary>
    public static volatile TipInfo[]? Latest;

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>启动时一次性拉取(失败静默,保留旧缓存)。</summary>
    public static async Task RefreshAsync()
    {
        var tips = await FetchAllAsync().ConfigureAwait(false);
        if (tips is { Length: > 0 }) Latest = tips;
    }

    /// <summary>拉取并解析全部提示文件;返回有效的提示数组(跳过 404/损坏/空),全失败返回 empty(非 null),由调用方隐藏。</summary>
    public static async Task<TipInfo[]> FetchAllAsync()
    {
        var result = new System.Collections.Generic.List<TipInfo>();
        foreach (var file in TipFiles)
        {
            var json = await FetchFileRawAsync(file).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json)) continue;
            var tip = ParseTip(json);
            if (tip is not null) result.Add(tip);
        }
        if (result.Count == 0) AppLogger.Info("[提示] 全部提示文件拉取失败/为空(静默隐藏)");
        return result.ToArray();
    }

    /// <summary>拉取单个提示文件原文(官方 + gh-proxy 镜像 + ghproxy + jsDelivr CDN,任一成功即返回;失败返回 null)。
    /// 多镜像提高国内可达性(与广告一致:jsDelivr 最稳)。</summary>
    private static async Task<string?> FetchFileRawAsync(string file)
    {
        string[] urls =
        {
            $"https://raw.githubusercontent.com/AlLHHH/ALH-Pro/main/hint/{file}",
            $"https://gh-proxy.com/https://raw.githubusercontent.com/AlLHHH/ALH-Pro/main/hint/{file}",
            $"https://ghproxy.net/https://raw.githubusercontent.com/AlLHHH/ALH-Pro/main/hint/{file}",
            $"https://cdn.jsdelivr.net/gh/AlLHHH/ALH-Pro@main/hint/{file}",
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
                AppLogger.Info($"[提示] 文件 {file} 端点失败({url[..Math.Min(42, url.Length)]}...):" + ex.Message.Split('\n')[0]);
            }
        }
        return null;
    }

    /// <summary>解析单个提示文件;字段缺失/异常返回 null。</summary>
    public static TipInfo? ParseTip(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // 顶层直接就是一条提示(hint1.json 形如 {text,color,link});兼容旧式把提示放在 "hint" 键下。
            var el = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("hint", out var hintEl)
                ? hintEl : root;
            var tip = new TipInfo
            {
                Text = GetString(el, "text"),
                Color = GetString(el, "color"),
                Link = GetString(el, "link"),
            };
            if (string.IsNullOrWhiteSpace(tip.Text)) return null;   // 无文案视为无效
            return tip;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>一条右侧纯文本提示(color 可空=用默认醒目色;link 可空=不可点击)。</summary>
public sealed class TipInfo
{
    public string? Text { get; set; }
    public string? Color { get; set; }
    public string? Link { get; set; }
}
