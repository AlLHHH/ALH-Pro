using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlhPro.Core;

/// <summary>单张广告卡(可空字段=未提供;image 空则纯文字卡;sponsor=广告主名,有则显示「推广」角标,合规:广告可识别)。
/// 【2026-10-17】从 <c>ImgUpscalerUI/AdFetcher.cs</c> 挪进 Core:广告内容现在要落本地缓存,而"存进去还能读回来"
/// 必须能单测(测试工程只引用 AlhPro.Core)——字段名/大小写一处不一致,整份缓存就会静默读不回来。</summary>
public sealed class AdInfo
{
    public string? Title { get; set; }
    public string? Text { get; set; }
    public string? Image { get; set; }
    public string? Link { get; set; }
    public string? LinkText { get; set; }
    public string? Sponsor { get; set; }
}

/// <summary>广告列表的本地缓存:**格式 + 判据**(纯逻辑,只算路径与字符串,不做文件 IO —— 读写由 UI 侧的 AdFetcher 负责)。
///
/// 【为什么有它 · 用户 2026-10-17 的原话】「还有这个广告 在上次刷新成功后 将缓存在本地
/// 直到下次打开时使用时检测到更新然后再更新广告」—— 原实现只把广告放在内存里(<c>AdFetcher.Latest</c>),
/// 每次启动都先摆两张内置兜底卡、再等一轮网络(本机实测冷启动 8.7 秒、被墙时几分钟)才换成真广告。
/// 现在:启动先把上次成功拉到的广告**从本地读回来立刻显示**,再联网检测,内容真的变了才覆盖。
///
/// 【落盘约定】目录 <c>%LOCALAPPDATA%\ALHPro\ads\</c>,文件名与作者仓库里的 <c>adN.json</c> **同名同编号**
/// (人可读、可直接对照;<see cref="AdCache.StampFileName"/> 另存一行 UTC 时间,给日志用)。</summary>
public static class AdCache
{
    /// <summary>最多几张卡(与网络侧同一口径:ad1..ad20,遇到不存在的编号即止)。</summary>
    public const int MaxFiles = 20;

    /// <summary>记录"这份缓存是什么时候存下的"的小文件(只写一行 UTC ISO 时间,给人看/给日志看)。</summary>
    public const string StampFileName = "saved.txt";

    /// <summary>缓存目录(与记住通道的 ad-channel.txt 同一层)。参数是 %LOCALAPPDATA% 的值(测试可传任意根)。</summary>
    public static string Dir(string? localAppDataRoot)
        => Path.Combine(localAppDataRoot ?? "", "ALHPro", "ads");

    /// <summary>第 <paramref name="index"/> 张卡(1 起)在缓存里的文件名。</summary>
    public static string FileNameOf(int index) => $"ad{index}.json";

    /// <summary>从缓存文件名解析编号(<c>ad3.json</c> → 3);不是 <c>adN.json</c> 形状返回 0(调用方据此判断"这份文件该不该收掉")。
    /// 认不出就返回 0 是刻意的:清理时宁可留下一个看不懂的文件,也不删掉别的东西。</summary>
    public static int IndexOf(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return 0;
        var s = fileName!.Trim();
        int slash = s.LastIndexOfAny(new[] { '\\', '/' });
        if (slash >= 0) s = s.Substring(slash + 1);
        if (s.Length <= 7) return 0;   // 至少 "ad1.json"
        if (!s.StartsWith("ad", StringComparison.OrdinalIgnoreCase)) return 0;
        if (!s.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return 0;
        var digits = s.Substring(2, s.Length - 2 - 5);
        return int.TryParse(digits, out var n) && n >= 1 && n <= MaxFiles ? n : 0;
    }

    /// <summary>写一张卡的 JSON(字段名**必须**小写 camelCase,与作者仓库里的 adN.json 同形 ——
    /// <see cref="ParseAd"/> 是按小写名取值的,写成 PascalCase 会让整份缓存读不回来;有回归用例钉住这条)。</summary>
    public static string ToJson(AdInfo ad) => JsonSerializer.Serialize(ad, WriteOptions);

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>解析单个广告文件;字段缺失/异常返回 null(判据与旧实现在 AdFetcher 里时逐字一致:
    /// Title/Text 全空视为无效)。兼容旧 <c>banner.json</c> 把卡放在 <c>"ad"</c> 键下的写法。</summary>
    public static AdInfo? ParseAd(string? json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            using var doc = JsonDocument.Parse(json!);
            var root = doc.RootElement;
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

    /// <summary>这批广告引用到的**图片缓存文件名**(与 <see cref="AdMirror.CacheFileName"/> 同一套命名)。
    /// 给启动清理用:广告列表现在跨启动留着,它引用的图就不能按"1 天前"一律删掉 ——
    /// 否则下次打开又得重新联网下图,正是用户要避免的那种"等"。</summary>
    public static HashSet<string> ReferencedImageNames(IEnumerable<AdInfo>? ads)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ads is null) return set;
        foreach (var a in ads)
        {
            var url = a?.Image;
            if (string.IsNullOrWhiteSpace(url)) continue;   // 纯文字卡:不引用任何图
            var n = AdMirror.CacheFileName(url);
            if (!string.IsNullOrWhiteSpace(n)) set.Add(n);
        }
        return set;
    }

    /// <summary>一张广告图缓存能不能删:文件名认得出、不在"仍被缓存广告引用"的名单里、且写入时间早于
    /// <paramref name="cutoffLocal"/>。名单用大小写不敏感的集合传(Windows 文件系统不区分大小写)。</summary>
    public static bool IsImagePrunable(string? fileName, DateTime lastWriteLocal, DateTime cutoffLocal, ISet<string>? keepFileNames)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        if (keepFileNames is not null && keepFileNames.Contains(fileName!)) return false;
        return lastWriteLocal < cutoffLocal;
    }
}
