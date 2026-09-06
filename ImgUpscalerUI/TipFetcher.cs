using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace ALHPro;

/// <summary>
/// 「状态栏常驻提示」数据源:拉取仓库 hint/ 目录下的独立提示文件 hint1.json~hint5.json(作者编辑后 push,用户端轮询到即更新)。
/// 与 AdFetcher 思路一致,但是独立文件夹(hint/)、独立上传、独立轮播,互不影响。
/// 每个文件定义一条纯文本提示 {text, color, link};text 为文案,color 为文字颜色(hex,#RRGGBB 或 #AARRGGBB),link 可选(点击跳转)。
/// 客户端本地每 30 秒轮播下一条(不联网),网络只在启动/每 10 分钟拉一次缓存。
/// 提示为【常驻】:显示在底部状态栏最右侧,不可删除;失败(无网/超时/JSON 异常)一律静默,单文件 404/损坏跳过,
/// 全坏才隐藏(通常用内置默认兜底,保证启动即有内容)。
/// </summary>
public static class TipFetcher
{
    /// <summary>提示文件前缀:hint1.json、hint2.json、hint3.json……(自适应:从 hint1 开始,直到某个文件不存在才停,最多 20 个)。
    /// 作者以后想加提示,只要在 hint/ 目录新建 hintN.json 并上传即可,无需改代码或发版。</summary>
    private const string TipFilePrefix = "hint";

    /// <summary>轮询间隔:10 分钟(作者改完 push,用户侧最长 10 分钟看到新内容)。</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(10);

    /// <summary>轮播间隔:每 30 秒换下一条提示(本地轮播,不联网)。</summary>
    public static readonly TimeSpan RotateInterval = TimeSpan.FromSeconds(30);

    /// <summary>最近一次缓存到的提示数组(线程安全);启动即有默认本地兜底,网络拉取成功后再替换。</summary>
    public static volatile TipInfo[] Latest = DefaultTips();

    /// <summary>默认本地兜底提示(启动立即显示,避免首次进软件无提示;网络拉取成功后覆盖)。</summary>
    private static TipInfo[] DefaultTips() => new[]
    {
        new TipInfo
        {
            Text = "所有处理都在本地完成,不上传、不联网",
            Color = "#7BD88F",
        },
    };

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>启动时一次性拉取(失败静默,保留旧缓存)。</summary>
    public static async Task RefreshAsync()
    {
        var tips = await FetchAllAsync().ConfigureAwait(false);
        if (tips is { Length: > 0 }) Latest = tips;
    }

    /// <summary>拉取并解析全部提示文件(自适应:从 hint1 开始,遇到不存在的文件即停,最多 20 个);
    /// 返回有效的提示数组(跳过损坏/空),全失败返回 empty(非 null),由调用方隐藏。</summary>
    public static async Task<TipInfo[]> FetchAllAsync()
    {
        var result = new System.Collections.Generic.List<TipInfo>();
        const int MaxFiles = 20;   // 安全上限,杜绝异常端点导致无限循环
        for (int i = 1; i <= MaxFiles; i++)
        {
            var file = $"{TipFilePrefix}{i}.json";
            var json = await FetchFileRawAsync(file).ConfigureAwait(false);
            if (json is null) break;   // 404:该编号文件不存在 → 停止(后续编号也不会有)
            if (string.IsNullOrWhiteSpace(json)) continue;
            var tip = ParseTip(json);
            if (tip is not null) result.Add(tip);
        }
        if (result.Count == 0) AppLogger.Info("[提示] 全部提示文件拉取失败/为空(静默隐藏)");
        return result.ToArray();
    }

    /// <summary>拉取单个提示文件原文(按实测可靠性排序:jsDelivr 最稳→gh-proxy→ghproxy→raw 最后;任一成功即返回)。
    /// 文件确实不存在(404)→ 返回 null(供调用方停止自适应拉取);其它失败返回空串(继续尝试下一端点)。</summary>
    private static async Task<string?> FetchFileRawAsync(string file)
    {
        string[] urls =
        {
            $"https://cdn.jsdelivr.net/gh/AlLHHH/ALH-Pro@main/hint/{file}",
            $"https://gh-proxy.com/https://raw.githubusercontent.com/AlLHHH/ALH-Pro/main/hint/{file}",
            $"https://ghproxy.net/https://raw.githubusercontent.com/AlLHHH/ALH-Pro/main/hint/{file}",
            $"https://raw.githubusercontent.com/AlLHHH/ALH-Pro/main/hint/{file}",
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
                AppLogger.Info($"[提示] 文件 {file} 端点失败({url[..Math.Min(42, url.Length)]}...):" + ex.Message.Split('\n')[0]);
            }
        }
        return sawNotFound ? null : string.Empty;   // 全端点 404 → null;否则空串(非404失败,继续尝试但不中断)
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

/// <summary>一条状态栏常驻提示(color 可空=用默认醒目色;link 可空=不可点击)。</summary>
public sealed class TipInfo
{
    public string? Text { get; set; }
    public string? Color { get; set; }
    public string? Link { get; set; }
}
