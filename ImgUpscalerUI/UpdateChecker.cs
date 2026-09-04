using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace ALHPro;

/// <summary>
/// 更新检查:查询 GitHub Releases 的 latest 接口,对比当前程序集版本。
/// 不做自动下载/安装(需管理员权限+签名),只提示并跳转发布页。
/// 失败(无网/超时/接口异常)一律静默:启动检查不打扰,手动检查才显示失败。
/// 内置加速:GitHub API 国内直连常超时 → 按顺序尝试「官方 API → 国内镜像1 → 镜像2」,
/// 任一成功即用(镜像只是代理转发,返回内容一致)。
/// </summary>
public static class UpdateChecker
{
    /// <summary>本项目的 GitHub 仓库(改动仓库后只需改这两处)。</summary>
    public const string Owner = "AlLHHH";
    public const string Repo = "ALH-Pro";

    public static string ReleasePageUrl => $"https://github.com/{Owner}/{Repo}/releases/latest";

    /// <summary>检查端点列表(按优先级):官方 API + 国内镜像(代理 GitHub Releases API)。
    /// 镜像实测(2026-09):gh-proxy.com 可用(1.7s);ghfast.top 超时、ghproxy.net SSL 错、gh.ddlc.top 404——
    /// 只留实测通的,避免"主源失败+镜像全挂"白等。</summary>
    private static readonly string[] Endpoints =
    {
        $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest",              // 官方(最快直连/海外)
        $"https://gh-proxy.com/https://api.github.com/repos/{Owner}/{Repo}/releases/latest", // 镜像(国内实测可用)
    };

    /// <summary>当前程序集版本(如 1.0.0;csproj &lt;Version&gt; 控制)。</summary>
    public static string CurrentVersion =>
        typeof(UpdateChecker).Assembly.GetName().Version?.ToString(3) ?? "1.0";

    /// <summary>检查结果:null=全部端点失败(网络/超时/接口异常);HasNew=true 有新版。</summary>
    public static async Task<(bool HasNew, string LatestTag, string LatestVersion)?> CheckAsync()
    {
        // 【测试开关】ALH_FORCE_UPDATE=1:强制模拟"检测到新版本"(不联网),用于本机测试更新弹窗/检查更新页面的网盘链接。
        // 正常用户不设置此变量,无任何影响。
        if (Environment.GetEnvironmentVariable("ALH_FORCE_UPDATE") == "1")
        {
            AppLogger.Info("[更新] 测试模式:强制视为有新版本(ALH_FORCE_UPDATE=1)");
            return (true, "v9.9.9-test", "9.9.9");
        }
        foreach (var url in Endpoints)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };   // 每端点 5 秒,总最长 15 秒
                // GitHub API 强制要求 User-Agent,否则 403
                http.DefaultRequestHeaders.UserAgent.ParseAdd($"ALHPro/{CurrentVersion}");
                var json = await http.GetStringAsync(url).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                // tag_name 形如 "v1.0";没有版本发布时 GitHub 返回 404(GetStringAsync 会抛异常 → 换端点)
                string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(tag)) return (false, "", "");
                // 去掉 v 前缀与 -后缀(如 v1.0-beta1 → 1.0)
                var latestStr = tag.TrimStart('v', 'V').Split('-')[0].Trim();
                if (!Version.TryParse(latestStr, out var latest)) return (false, tag, latestStr);
                var cur = Version.TryParse(CurrentVersion, out var c) ? c : new Version(1, 0, 0);
                AppLogger.Info($"[更新] 检查:当前 {CurrentVersion},GitHub 最新 {tag} → {(latest > cur ? "有新版本" : "已是最新")}(端点 {url[..Math.Min(40, url.Length)]}...)");
                return (latest > cur, tag, latestStr);
            }
            catch (Exception ex)
            {
                AppLogger.Info($"[更新] 端点失败({url[..Math.Min(40, url.Length)]}...):" + ex.Message.Split('\n')[0]);
            }
        }
        AppLogger.Info("[更新] 检查失败(全部端点,静默)");
        return null;
    }
}
