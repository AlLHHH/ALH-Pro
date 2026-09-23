using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【2026-09-23 视频抠图页欠账:输入框焦点离开归一化】这条欠账的**后果**是"静默变黑"：
/// 下游 `VideoMattingService.ParseColor` 只认恰好 6 位十六进制，其余一律回退黑色，而且没有任何提示。
/// 所以归一化必须① 把用户自然的写法救回来、② 救不了时**返回 null 而不是猜**（页面据此提示 + 回退上一个有效值）。
/// 这些用例把"哪些写法算合法"逐条钉住 —— 它是纯逻辑，不需要 UI。</summary>
public class HexColorTests
{
    [Theory]
    [InlineData("#1E3A5F", "#1E3A5F")]     // 标准写法
    [InlineData("1E3A5F", "#1E3A5F")]      // 漏了 #
    [InlineData("1e3a5f", "#1E3A5F")]      // 小写
    [InlineData("  #1e3a5f  ", "#1E3A5F")] // 前后空白(从别处复制粘贴常见)
    [InlineData("0x1E3A5F", "#1E3A5F")]    // 编程习惯写法
    [InlineData("#FFF", "#FFFFFF")]        // CSS 简写 = 每位重复
    [InlineData("fff", "#FFFFFF")]         // 简写 + 漏 #  ← 用户最可能打的一种
    [InlineData("#f0a", "#FF00AA")]
    [InlineData("#000", "#000000")]
    public void Accepts_the_forms_users_actually_type(string input, string expected)
    {
        Assert.Equal(expected, HexColor.Normalize(input));
        Assert.True(HexColor.TryParseRgb(input, out _, out _, out _));
    }

    [Theory]
    [InlineData("")]          // 空
    [InlineData("   ")]       // 全空白
    [InlineData("#")]         // 只有 #
    [InlineData("#FF")]       // 2 位
    [InlineData("#FFFF")]     // 4 位
    [InlineData("#FFFFF")]    // 5 位
    [InlineData("#FFFFFFF")]  // 7 位
    [InlineData("#GGGGGG")]   // 非十六进制字符
    [InlineData("#FFFFFF 白")] // 带了注释/单位 —— 必须判非法(而不是"忽略后半段"猜一个值)
    [InlineData("红色")]
    [InlineData(null)]
    public void Rejects_invalid_input_instead_of_guessing(string? input)
    {
        Assert.Null(HexColor.Normalize(input));
        Assert.False(HexColor.TryParseRgb(input, out _, out _, out _));
    }

    /// <summary>★ 关键口子：非法输入**绝不能**变成黑色（那正是这次要修的"静默变黑"）。
    /// 必须回退到调用方给的上一个有效值。</summary>
    [Fact]
    public void Invalid_input_falls_back_to_the_last_valid_value_not_black()
    {
        Assert.Equal("#1E3A5F", HexColor.NormalizeOr("打错了的东西", "#1E3A5F"));
        Assert.Equal("#00B140", HexColor.NormalizeOr("#zzzzzz", "#00B140"));
        // 连兜底值都非法时才最终落到黑色（这时的黑色是"明确可解释"的默认值，不是静默结果）
        Assert.Equal("#000000", HexColor.NormalizeOr("坏", "也坏"));
        Assert.Equal("#000000", HexColor.NormalizeOr(null, null));
    }

    [Fact]
    public void Format_is_uppercase_with_hash()
    {
        Assert.Equal("#0A0B0C", HexColor.Format(10, 11, 12));
        Assert.Equal("#FFFFFF", HexColor.Format(255, 255, 255));
        Assert.Equal("#000000", HexColor.Format(0, 0, 0));
    }

    /// <summary>往返一致：归一化后的文本再解析必须得到同样的 RGB（页面会把归一化结果写回输入框）。</summary>
    [Theory]
    [InlineData("fff")]
    [InlineData("#1e3a5f")]
    [InlineData("0x00b140")]
    public void Normalize_then_parse_round_trips(string input)
    {
        var once = HexColor.Normalize(input);
        Assert.NotNull(once);
        Assert.Equal(once, HexColor.Normalize(once));
        Assert.True(HexColor.TryParseRgb(input, out var r1, out var g1, out var b1));
        Assert.True(HexColor.TryParseRgb(once, out var r2, out var g2, out var b2));
        Assert.Equal((r1, g1, b1), (r2, g2, b2));
    }
}
