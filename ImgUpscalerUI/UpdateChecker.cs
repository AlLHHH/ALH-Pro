using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace ALHPro;

/// <summary>
/// 更新检查:查询 GitHub Releases 的 latest 接口,对比当前程序集版本。
/// 不做自动下载/安装(需管理员权限+签名),只提示并跳转发布页。
/// 失败(无网/超时/接口异常)一律静默:启动检查不打扰,手动检查才显示失败。
/// </summary>
public static class UpdateChecker
{
    /// <summary>本项目的 GitHub 仓库(改动仓库后只需改这两处)。</summary>
    public const string Owner = "AlLHHHDYTH";
    public const string Repo = "ALH-Pro";

    public static string LatestApiUrl => $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
    public static string ReleasePageUrl => $"https://github.com/{Owner}/{Repo}/releases/latest";

    /// <summary>当前程序集版本(如 1.0.0;csproj &lt;Version&gt; 控制)。</summary>
    public static string CurrentVersion =>
        typeof(UpdateChecker).Assembly.GetName().Version?.ToString(3) ?? "1.0";

    /// <summary>检查结果:null=请求失败(网络/超时/接口异常);HasNew=true 有新版。</summary>
    public static async Task<(bool HasNew, string LatestTag, string LatestVersion)?> CheckAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            // GitHub API 强制要求 User-Agent,否则 403
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"ALHPro/{CurrentVersion}");
            var json = await http.GetStringAsync(LatestApiUrl).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // tag_name 形如 "v1.0";没有版本发布时 GitHub 返回 404(GetStringAsync 会抛异常 → 走静默)
            string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(tag)) return (false, "", "");
            // 去掉 v 前缀与 -后缀(如 v1.0-beta1 → 1.0)
            var latestStr = tag.TrimStart('v', 'V').Split('-')[0].Trim();
            if (!Version.TryParse(latestStr, out var latest)) return (false, tag, latestStr);
            var cur = Version.TryParse(CurrentVersion, out var c) ? c : new Version(1, 0, 0);
            AppLogger.Info($"[更新] 检查:当前 {CurrentVersion},GitHub 最新 {tag} → {(latest > cur ? "有新版本" : "已是最新")}");
            return (latest > cur, tag, latestStr);
        }
        catch (Exception ex)
        {
            AppLogger.Info("[更新] 检查失败(静默):" + ex.Message);
            return null;
        }
    }
}
