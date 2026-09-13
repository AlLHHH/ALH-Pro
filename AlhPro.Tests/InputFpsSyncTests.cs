using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>「输入帧率」框显示来源与 ffprobe 帧率解析的单测(任务 P)。
/// 【用户报障】「输入帧率」框在选中/激活视频后依然为空 —— 根因见 `InputFpsSyncPolicy` 的类注释:
/// 多选模式下点"已选中"的项 = 取消选中,而选择变化回调在没选中时按设计清空该框;
/// 且已完成的项没有任何回填路径。这里把"该显示谁"的判定钉死(含"取消选中仍显示刚点过的那个")。</summary>
public class InputFpsSyncTests
{
    [Theory]
    [InlineData(1, false, InputFpsSyncPolicy.Source.Selection)]     // 正常选中:显示选中项
    [InlineData(1, true, InputFpsSyncPolicy.Source.Selection)]      // 有选中项时选中项优先(不看"点过的")
    [InlineData(2, true, InputFpsSyncPolicy.Source.Selection)]      // 多选:调用方取"最后点击"的那一项
    [InlineData(0, true, InputFpsSyncPolicy.Source.LastClicked)]    // ★ 用户报障场景:点一下把选中取消了,仍显示刚点过的那个
    [InlineData(0, false, InputFpsSyncPolicy.Source.None)]          // 从没点过(列表刚清空/删完)→ 清空(空 = 自动探测)
    public void Decide_matches_the_reported_scenario(int selectionCount, bool hasLastClicked, InputFpsSyncPolicy.Source expected)
        => Assert.Equal(expected, InputFpsSyncPolicy.Decide(selectionCount, hasLastClicked));

    [Theory]
    [InlineData("30/1", "30")]                    // 常见 CFR
    [InlineData("30000/1001", "29.97")]           // 29.97(NTSC)
    [InlineData("25/1", "25")]
    [InlineData(" 60/1 \r\n", "60")]              // 换行 + 空格
    [InlineData("23.976", "23.98")]               // 直接给数字
    public void Parse_reads_valid_frame_rates(string raw, string expected)
        => Assert.Equal(expected, FfprobeFps.Parse(raw));

    /// <summary>VFR 素材实测:`avg_frame_rate` 是 `0/0`(旧实现会把 "0/0" 原样写进输入框);
    /// 现在跳过无效候选,后面给的 `r_frame_rate` 顶上;两个都无效才返回 null(= 留空,处理时自动探测)。</summary>
    [Theory]
    [InlineData("0/0,30/1", "30")]                // 逗号分隔(ffprobe -of csv=p=0 多字段)
    [InlineData("0/0\n30/1", "30")]               // 多行
    [InlineData("0/0,0/0", null)]                 // 全无效 → 留空
    [InlineData("0/0", null)]
    [InlineData("N/A", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    [InlineData("-1/1", null)]                    // 负数不采用
    [InlineData("abc", null)]
    public void Parse_rejects_invalid_values_and_falls_through(string? raw, string? expected)
        => Assert.Equal(expected, FfprobeFps.Parse(raw));

    /// <summary>解析结果必须能被界面那条 `double.TryParse` 直接吃下(否则框里等于显示了一个无效值)。</summary>
    [Theory]
    [InlineData("30/1")]
    [InlineData("0/0,30000/1001")]
    [InlineData("23.976")]
    public void Parse_result_is_numeric(string raw)
    {
        var s = FfprobeFps.Parse(raw);
        Assert.NotNull(s);
        Assert.True(double.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0);
    }
}
