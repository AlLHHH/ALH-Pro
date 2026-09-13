using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>「预计还剩」文案的单测(从 VideoService.EtaStr 抽出的纯逻辑)。
/// 对应 2026-09-13 真机反馈:补帧跑到 2667/2668 时界面显示"预计还剩几秒",用户追问"几???",
/// 而后面还有整批 PNG→JPG 收尾要跑几分钟 —— 这是"阶段末假装马上好"的体验 bug。</summary>
public class EtaTextTests
{
    [Fact]
    public void Under_one_second_no_longer_says_vague_seconds()
    {
        // done=2667/total=2668 → 剩余 1 帧;总耗时 300 秒 → 每帧约 0.112 秒 → remainSec ≈ 0.112 < 1
        var t = EtaText.ForRemaining(2667, 2668, 300);
        Assert.Equal(" · 本阶段即将结束", t);
        Assert.False(EtaText.ContainsVagueSeconds(t));   // 【回归守卫】任何档位都不许再出现"几秒"
        Assert.DoesNotContain("几秒", t);
    }

    [Fact]
    public void Under_one_second_names_the_upcoming_wrap_up()
    {
        // 有已知收尾时必须说清"后面还有什么"(用户原话:补帧之后还有整批整理帧要跑几分钟)
        var t = EtaText.ForRemaining(2667, 2668, 300, "2668 帧整理成 JPG");
        Assert.Contains("本阶段即将结束", t);
        Assert.Contains("2668 帧整理成 JPG", t);
        Assert.False(EtaText.ContainsVagueSeconds(t));
        // 空/空白 = 当作没有收尾信息(不许渲染出"(随后还有)")
        Assert.Equal(" · 本阶段即将结束", EtaText.ForRemaining(2667, 2668, 300, ""));
        Assert.Equal(" · 本阶段即将结束", EtaText.ForRemaining(2667, 2668, 300, null));
    }

    [Theory]
    [InlineData(100, 200, 20, "预计还剩 20 秒")]      // 剩余 100 帧 × 0.2 秒/帧 = 20 秒
    [InlineData(100, 200, 40, "预计还剩 40 秒")]      // 0.4 秒/帧 → 40 秒
    public void Seconds_band_is_unchanged(long done, long total, double elapsed, string expected)
    {
        Assert.Equal(expected, EtaText.ForRemaining(done, total, elapsed));
    }

    [Fact]
    public void Seconds_band_uses_integer_truncation_as_before()
    {
        // 改动前是 $"{(int)remainSec} 秒"(截断,不进位)—— 口径不动:19.9 → 19
        double elapsed = 1.99;                     // 10 帧用时 1.99 秒 → 每帧 0.199;剩余 90 帧 → 17.91 秒
        string t = EtaText.ForRemaining(10, 100, elapsed);
        Assert.StartsWith("预计还剩 ", t);
        Assert.EndsWith(" 秒", t);
        int shown = int.Parse(t.Replace("预计还剩 ", "").Replace(" 秒", ""));
        Assert.Equal((int)(90 * elapsed / 10), shown);
    }

    [Fact]
    public void Minutes_and_hours_bands_are_unchanged()
    {
        // 剩余 120 帧 × 1 秒/帧 = 120 秒 → 2 分钟
        Assert.Equal("预计还剩 2 分钟", EtaText.ForRemaining(60, 180, 60));
        // 剩余 30 帧 × 240 秒/帧 = 7200 秒 → 2 小时
        Assert.Equal("预计还剩 2 小时", EtaText.ForRemaining(10, 40, 2400));
    }

    [Fact]
    public void Guards_return_empty_string()
    {
        Assert.Equal("", EtaText.ForRemaining(0, 100, 10));      // 还没开始(done < 4)
        Assert.Equal("", EtaText.ForRemaining(3, 100, 10));      // done < 4:头几帧含模型加载,算速率离谱
        Assert.Equal("", EtaText.ForRemaining(50, 0, 10));       // total 未知
        Assert.Equal("", EtaText.ForRemaining(100, 100, 10));    // 已做完
        Assert.Equal("", EtaText.ForRemaining(200, 100, 10));    // 超出
        Assert.Equal("", EtaText.ForRemaining(50, 100, 0.5));    // 净耗时不足 1 秒:不给数字
    }

    [Fact]
    public void No_band_ever_contains_vague_seconds()
    {
        // 扫一遍各种工作量/耗时组合:文案里永远不许出现"几秒"(这次修复的核心要求)
        double[] elapseds = { 0.9, 1.0, 1.5, 10, 100, 1000, 10000 };
        long[] dones = { 0, 1, 4, 5, 50, 500, 5000 };
        long[] totals = { 0, 4, 5, 50, 500, 5000 };
        foreach (var e in elapseds)
            foreach (var d in dones)
                foreach (var t in totals)
                {
                    var s = EtaText.ForRemaining(d, t, e, "2668 帧整理成 JPG");
                    Assert.False(EtaText.ContainsVagueSeconds(s), $"done={d} total={t} elapsed={e} → \"{s}\"");
                    // 也不许出现"完成"二字:UI 会把含"完成"的非帧号进度行改写成"✓ …"并清掉步骤行
                    Assert.DoesNotContain("完成", s);
                }
    }
}
