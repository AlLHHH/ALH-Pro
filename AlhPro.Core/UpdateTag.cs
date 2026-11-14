using System;

namespace AlhPro.Core;

/// <summary>
/// 版本号比较:判断一个发布 tag(如 "v1.4.9"、"1.4.10-beta")是不是比当前版本新。
///
/// 【为什么单独拎出来】2026-11-14 用户反馈"明明装的是新版本,顶部还挂一条要更新的横幅"。
/// 原因是"发现新版本"的记录会写进设置(PendingUpdateTag),升级到该版本后这条记录还在,
/// 下次启动照着它无条件弹横幅 —— 从来没比过版本。判断要能被单元测试钉住,不能埋在界面代码里。
///
/// 规则:只比前面三段数字(1.4.10 &gt; 1.4.9),后缀(-beta、-test)忽略;解析不出来一律当"不新",
/// 宁可不弹,也不误报。
/// </summary>
public static class UpdateTag
{
    /// <summary>tag 是否比 current 新。任一解析失败都返回 false。</summary>
    public static bool IsNewer(string? tag, string? current)
    {
        if (!TryParse(tag, out var latest)) return false;
        if (!TryParse(current, out var cur)) return false;
        return latest > cur;
    }

    /// <summary>把 "v1.4.9" / "1.4.9 (2026-11-14)" / "1.4.9-beta" 归一成 Version。</summary>
    private static bool TryParse(string? raw, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var s = raw.Trim().TrimStart('v', 'V');
        // 去掉括号/空格之后的一切说明,只留版本数字
        int cut = s.IndexOfAny(new[] { ' ', '(', '（' });
        if (cut > 0) s = s[..cut];
        // 去掉 -beta / -test 这类后缀
        int dash = s.IndexOf('-');
        if (dash > 0) s = s[..dash];
        s = s.Trim();
        if (s.Length == 0) return false;
        if (!Version.TryParse(s, out var v) || v is null) return false;
        version = v;
        return true;
    }

    /// <summary>一条"只给旧版本看"的内容(提示/公告)该不该给当前版本显示。
    /// 没写门槛 → 所有版本都显示;写了门槛 n → 只有当前版本比 n 旧才显示。
    /// 门槛解析不出来 → 不显示(宁可少显示一条提示,也不给已是最新的用户添堵)。</summary>
    public static bool AppliesTo(string? onlyForOlderThan, string? current)
    {
        if (string.IsNullOrWhiteSpace(onlyForOlderThan)) return true;
        return IsNewer(onlyForOlderThan, current);
    }
}