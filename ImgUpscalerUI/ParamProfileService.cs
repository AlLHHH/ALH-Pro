using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ALHPro;

/// <summary>【任务 V】在线"最优参数配置"的拉取 + 缓存 + 落地(只下载、不上传)。
///
/// 【隐私硬前提(写死在这里,不许改)】
///   · **只下载这一份参数配置**;任何用户数据、文件名、路径、机型指纹、GPU 名称、使用统计**一律不上传** ——
///     本类只有 GET,没有 POST/PUT、没有请求体、没有查询串、没有 Cookie、没有自定义标识头
///     (UA 只是 "ALHPro/版本号",与既有 TipFetcher/UpdateChecker 一致,不含设备信息);
///   · **离线完全可用**:网络失败/超时/返回异常 → 静默使用内置默认表(见 AlhPro.Core.ParamProfile.BuiltIn),
///     不报错、不阻塞界面、不改变默认行为;失败只写一行日志(且只写一次,不刷屏);
///   · 短超时(5 秒)+ 不重试轰炸(每个镜像端点各试一次即止,与 TipFetcher 同风格)。
///
/// 【拉取地址与缓存位置(写进代码注释,便于排查)】
///   · 地址(按实测可靠性排序,任一成功即止):
///       https://cdn.jsdelivr.net/gh/AlLHHH/ALH-Pro@main/params/param-profile.json
///       https://gh-proxy.com/https://raw.githubusercontent.com/AlLHHH/ALH-Pro/main/params/param-profile.json
///       https://ghproxy.net/https://raw.githubusercontent.com/AlLHHH/ALH-Pro/main/params/param-profile.json
///       https://raw.githubusercontent.com/AlLHHH/ALH-Pro/main/params/param-profile.json
///   · 缓存:%LOCALAPPDATA%\ALHPro\param-profile-cache.json(存 {fetchedAt, raw} 两个字段)
///   · **只在"缓存缺失/过期(TTL 7 天)"时才发请求** —— 绝不在每次处理时都请求。
///
/// 【可离线测试】真实网络被抽象成 `Func<CancellationToken, Task<string?>>`;
/// 离线环境注入一个"返回 null / 抛超时"的假实现即可完整覆盖兜底路径。
/// 【与既有机制的关系】超时/异常处理风格照抄 AdFetcher/TipFetcher/UpdateChecker,但**不耦合**它们的业务逻辑。</summary>
public static class ParamProfileService
{
    /// <summary>缓存有效期:7 天(过期才重新拉;版本更新由作者在 JSON 里改 version 字段体现)。</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);

    private static readonly string[] Urls =
    {
        "https://cdn.jsdelivr.net/gh/AlLHHH/ALH-Pro@main/params/param-profile.json",
        "https://gh-proxy.com/https://raw.githubusercontent.com/AlLHHH/ALH-Pro/main/params/param-profile.json",
        "https://ghproxy.net/https://raw.githubusercontent.com/AlLHHH/ALH-Pro/main/params/param-profile.json",
        "https://raw.githubusercontent.com/AlLHHH/ALH-Pro/main/params/param-profile.json",
    };

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };   // 与 AdFetcher/TipFetcher 同口径

    private static string CacheFile => ParaPaths.SettingsFile("param-profile-cache.json");

    private static int _appliedOnce;      // 幂等:同一次运行只"落地 + 打一行日志"一次
    private static int _failLoggedOnce;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>最近一次生效的"参数来源"说明(供诊断/统计行引用)。</summary>
    public static string LastSourceLine { get; private set; } = "参数来源:内置(尚未拉取)";

    /// <summary>缓存结构(只存原文,避免二次序列化把坏字段"洗白")。</summary>
    private sealed class Cache
    {
        public DateTime FetchedAt { get; set; }
        public string? Raw { get; set; }
    }

    /// <summary>确保在线参数已按当前开关落地。**绝不抛异常、绝不阻塞界面**(调用方 fire-and-forget)。
    /// <paramref name="enabled"/>=false(设置里关掉)→ 直接用内置表并把覆盖层清空(等于完全走内置)。
    /// <paramref name="fetchOverride"/>=非空则用它替代真实网络(离线单测/故障注入用)。</summary>
    public static async Task RefreshAsync(bool enabled, Func<CancellationToken, Task<string?>>? fetchOverride = null)
    {
        try
        {
            if (!enabled)
            {
                if (Interlocked.Exchange(ref _appliedOnce, 1) == 0)
                {
                    AlhPro.Core.ParamProfileRuntime.Set(null);
                    LastSourceLine = "参数来源:内置(在线参数已关闭)";
                    AppLogger.Info(LastSourceLine);
                }
                return;
            }
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var cached = ReadCache();
                bool fresh = cached != null && (DateTime.Now - cached.FetchedAt) < CacheTtl;
                string? raw;
                string origin;
                if (fresh)
                {
                    raw = cached!.Raw;
                    origin = $"缓存({cached.FetchedAt:yyyy-MM-dd})";
                }
                else
                {
                    raw = await FetchWithOverrideAsync(fetchOverride).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        WriteCache(raw!);
                        origin = "在线";
                    }
                    else if (cached?.Raw is { Length: > 0 })
                    {
                        raw = cached.Raw;   // 拉取失败但有过期缓存 → 用旧的(比没有好,且校验会拦住坏数据)
                        origin = "缓存(已过期,本次拉取失败)";
                        LogFailureOnce("在线参数暂时拉取不到(网络波动或访问受限);已沿用上次缓存,不影响软件任何功能。");
                    }
                    else
                    {
                        AlhPro.Core.ParamProfileRuntime.Set(null);
                        LastSourceLine = "参数来源:内置(在线配置不可达)";
                        LogFailureOnce("在线参数暂时拉取不到(网络波动或访问受限);已使用内置参数,不影响软件任何功能。");
                        if (Interlocked.Exchange(ref _appliedOnce, 1) == 0) AppLogger.Info(LastSourceLine);
                        return;
                    }
                }

                var res = AlhPro.Core.ParamProfileParser.ParseAndValidate(raw, null, origin);
                AlhPro.Core.ParamProfileRuntime.Set(res.Profile);
                LastSourceLine = res.LogLine + $" / 适用范围:{res.Profile.Scope}";
                foreach (var r in res.Rejected) AppLogger.Info($"[参数] 被拒项:{r}");
                if (Interlocked.Exchange(ref _appliedOnce, 1) == 0) AppLogger.Info(LastSourceLine);
                else AppLogger.Info($"[参数] 已刷新:{LastSourceLine}");
            }
            finally { Gate.Release(); }
        }
        catch (Exception ex)
        {
            // 任何意外都不许影响处理:退回内置,只记一行
            try { AlhPro.Core.ParamProfileRuntime.Set(null); } catch { }
            LastSourceLine = "参数来源:内置(在线配置处理异常)";
            LogFailureOnce($"在线参数处理异常({ex.Message.Split('\n')[0]});已使用内置参数,不影响软件任何功能。");
        }
    }

    /// <summary>真实拉取(多端点各试一次,任一成功即止;全失败返回 null)。</summary>
    private static async Task<string?> FetchWithOverrideAsync(Func<CancellationToken, Task<string?>>? fetchOverride)
    {
        if (fetchOverride != null)
        {
            try { return await fetchOverride(CancellationToken.None).ConfigureAwait(false); }
            catch { return null; }
        }
        foreach (var url in Urls)
        {
            try
            {
                _http.DefaultRequestHeaders.UserAgent.ParseAdd($"ALHPro/{UpdateChecker.CurrentVersion}");
                var json = await _http.GetStringAsync(url).ConfigureAwait(false);   // 只有 GET:不发送任何本机信息
                if (!string.IsNullOrWhiteSpace(json)) return json;
            }
            catch { /* 该端点失败 → 下一个;全失败由调用方兜底 */ }
        }
        return null;
    }

    private static Cache? ReadCache()
    {
        try
        {
            if (!File.Exists(CacheFile)) return null;
            return JsonSerializer.Deserialize<Cache>(File.ReadAllText(CacheFile));
        }
        catch { return null; }
    }

    private static void WriteCache(string raw)
    {
        try
        {
            File.WriteAllText(CacheFile, JsonSerializer.Serialize(new Cache { FetchedAt = DateTime.Now, Raw = raw }),
                new System.Text.UTF8Encoding(false));
        }
        catch { /* 写不进缓存不影响本次生效 */ }
    }

    private static void LogFailureOnce(string msg)
    {
        if (Interlocked.Exchange(ref _failLoggedOnce, 1) == 0) AppLogger.Info("[参数] " + msg);
    }
}
