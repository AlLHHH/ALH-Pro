using System.Globalization;

namespace AlhPro.Core;

/// <summary>十六进制颜色文本的**归一化**（纯逻辑，可单测）。
///
/// 【为什么需要它 —— 2026-09-23 用户欠账"输入框右侧色块 + 焦点离开归一化"】
/// 视频抠图页的背景色输入框直接吃用户手打的文本，而下游 `VideoMattingService.ParseColor` 只认
/// **恰好 6 位十六进制**，其它一律**静默回退成黑色**：
///   · 用户打 `fff`（很自然的简写）→ 变黑 ✗
///   · 用户打 `#FFF` → 变黑 ✗
///   · 用户打 `1E3A5F #背景`（带注释）→ 变黑 ✗
/// 三种都是"看着像设置成功了，实际颜色不对"，而且**没有任何提示**。所以口径是：
///   **能救的救（简写补全、去掉多余符号），救不了的明确告诉用户**（页面回退到上一个有效值并提示），
///   绝不把"打错了"变成"悄悄变黑"。
///
/// 接受的形式（大小写不敏感，允许前后空白）：
///   `#RRGGBB` / `RRGGBB` / `#RGB` / `RGB` / `0xRRGGBB` / `0xRGB`
/// 输出：统一成 `#RRGGBB`（大写、带 `#`）—— 这样文本框里的写法永远只有一种，肉眼可比对。</summary>
public static class HexColor
{
    /// <summary>解析十六进制颜色文本。成功返回 true 并给出 RGB；失败返回 false（调用方负责提示与回退）。</summary>
    public static bool TryParseRgb(string? input, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        var s = (input ?? "").Trim();
        if (s.Length == 0) return false;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        s = s.TrimStart('#').Trim();
        if (s.Length == 3)
        {
            // 简写 #RGB = 每位重复一次（#F0A → #FF00AA）—— 这是 CSS 的通用规则，用户按直觉写就对了
            if (!IsHex(s)) return false;
            r = (byte)(HexVal(s[0]) * 17); g = (byte)(HexVal(s[1]) * 17); b = (byte)(HexVal(s[2]) * 17);
            return true;
        }
        if (s.Length != 6 || !IsHex(s)) return false;
        r = (byte)((HexVal(s[0]) << 4) | HexVal(s[1]));
        g = (byte)((HexVal(s[2]) << 4) | HexVal(s[3]));
        b = (byte)((HexVal(s[4]) << 4) | HexVal(s[5]));
        return true;
    }

    /// <summary>解析成功就返回规范化文本 `#RRGGBB`；失败返回 null（**不猜、不回退**，由调用方决定怎么办）。</summary>
    public static string? Normalize(string? input)
        => TryParseRgb(input, out var r, out var g, out var b) ? Format(r, g, b) : null;

    /// <summary>解析成功返回规范化文本，失败返回 <paramref name="fallback"/>（用上一个有效值兜住，不要变黑）。</summary>
    public static string NormalizeOr(string? input, string fallback)
        => Normalize(input) ?? Normalize(fallback) ?? "#000000";

    /// <summary>把 RGB 写成 `#RRGGBB`（大写）。</summary>
    public static string Format(byte r, byte g, byte b)
        => "#" + r.ToString("X2", CultureInfo.InvariantCulture)
             + g.ToString("X2", CultureInfo.InvariantCulture)
             + b.ToString("X2", CultureInfo.InvariantCulture);

    private static bool IsHex(string s)
    {
        foreach (var c in s)
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }

    private static int HexVal(char c) => Convert.ToInt32(c.ToString(), 16);
}
