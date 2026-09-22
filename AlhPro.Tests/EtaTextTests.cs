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
    [InlineData(100, 200, 20, "本阶段预计还剩 20 秒")]      // 剩余 100 帧 × 0.2 秒/帧 = 20 秒
    [InlineData(100, 200, 40, "本阶段预计还剩 40 秒")]      // 0.4 秒/帧 → 40 秒
    public void Seconds_band_is_unchanged(long done, long total, double elapsed, string expected)
    {
        Assert.Equal(expected, EtaText.ForRemaining(done, total, elapsed));
    }

    [Fact]
    public void Every_number_band_says_which_stage_it_belongs_to()
    {
        // 【K1】秒/分钟/小时三档都必须带"本阶段"限定词:真机截图里同一屏既有阶段自己的
        // "预计还剩 3.7 分钟"又有"本片剩余 0:30",用户分不清前者说的是哪个范围。
        Assert.StartsWith("本阶段预计还剩 ", EtaText.ForRemaining(100, 200, 20));
        Assert.StartsWith("本阶段预计还剩 ", EtaText.ForRemaining(60, 180, 60));
        Assert.StartsWith("本阶段预计还剩 ", EtaText.ForRemaining(10, 40, 2400));
        // 秒/分钟/小时的数字算法保持不动(仅加限定词):(200−100) 帧 × 20 秒 ÷ 100 帧 = 20 秒
        Assert.Equal(20, EtaText.RemainingSeconds(100, 200, 20), 6);
    }

    [Fact]
    public void Seconds_band_uses_integer_truncation_as_before()
    {
        // 改动前是 $"{(int)remainSec} 秒"(截断,不进位)—— 口径不动:19.9 → 19
        double elapsed = 1.99;                     // 10 帧用时 1.99 秒 → 每帧 0.199;剩余 90 帧 → 17.91 秒
        string t = EtaText.ForRemaining(10, 100, elapsed);
        Assert.StartsWith("本阶段预计还剩 ", t);
        Assert.EndsWith(" 秒", t);
        int shown = int.Parse(t.Replace("本阶段预计还剩 ", "").Replace(" 秒", ""));
        Assert.Equal((int)(90 * elapsed / 10), shown);
    }

    [Fact]
    public void Minutes_and_hours_bands_are_unchanged()
    {
        // 剩余 120 帧 × 1 秒/帧 = 120 秒 → 2 分钟
        Assert.Equal("本阶段预计还剩 2 分钟", EtaText.ForRemaining(60, 180, 60));
        // 剩余 30 帧 × 240 秒/帧 = 7200 秒 → 2 小时
        Assert.Equal("本阶段预计还剩 2 小时", EtaText.ForRemaining(10, 40, 2400));
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

    // ===== 2026-09-16:按"最近吞吐量"估算(用户反馈「整体预计时间不要乱写」)=====

    /// <summary>**核心回归**:开局那批很慢(含引擎进程启动/模型加载)时,旧口径会把这段开销永久摊进平均速度 ⇒ 剩余时间偏大;
    /// 按最近吞吐量估算必须收敛到真实速度。
    /// 构造:1000 帧里前 200 帧花了 200 秒(每帧 1 秒,含开局开销),之后 100 帧只花 10 秒(每帧 0.1 秒)
    /// ⇒ 真实剩余 ≈ 800 帧 × 0.1 = 80 秒;而累计平均口径会给出 (800)×(210/300) = 560 秒(差 7 倍)。</summary>
    [Fact]
    public void Recent_throughput_beats_cumulative_average_after_slow_start()
    {
        double cumulative = EtaText.RemainingSeconds(300, 1000, 210);
        Assert.Equal(490, cumulative, 0);   // 旧口径:剩余 700 帧 × (210 秒 ÷ 300 帧) = 490 秒,被开局那 200 秒拖死

        double byRate = EtaText.RemainingSecondsByRate(300, 1000, sampleDone: 200, sampleElapsedSec: 200,
            cumulativeElapsedSec: 210, sampleWindowFrames: 60);
        // 最近速率 = (300-200)/(210-200) = 10 帧/秒;权重 = min(300/60,1)×0.8 = 0.8
        // ⇒ 混合速率 0.8×10 + 0.2×(300/210) ≈ 8.29 帧/秒 ⇒ 剩余 700 帧 ≈ 84 秒
        // (与"真实 70~80 秒"同量级;旧口径是 490 秒)
        Assert.InRange(byRate, 40, 130);
        Assert.True(byRate < cumulative / 3, $"按最近速率应显著小于累计口径:rate={byRate} cum={cumulative}");
    }

    /// <summary>没有可用采样点时,必须退化成累计口径(不引入新的抖动源);不满足前置条件时返回 -1(不给数字)。</summary>
    [Fact]
    public void Falls_back_to_cumulative_and_keeps_the_same_guards()
    {
        double cum = EtaText.RemainingSeconds(100, 500, 50);
        double same = EtaText.RemainingSecondsByRate(100, 500, sampleDone: 0, sampleElapsedSec: 0,
            cumulativeElapsedSec: 50);
        Assert.Equal(cum, same, 6);

        Assert.Equal(-1, EtaText.RemainingSecondsByRate(3, 500, 0, 0, 50));      // done<4:不给数字
        Assert.Equal(-1, EtaText.RemainingSecondsByRate(100, 500, 0, 0, 0.5));   // 净耗时<1 秒:不给数字
        Assert.Equal(-1, EtaText.RemainingSecondsByRate(500, 500, 0, 0, 50));    // 已做完
    }

    /// <summary>剩余批数的"每批引擎启动"固定开销必须计入(旧口径只算每帧成本,批数多时系统性偏乐观)。</summary>
    [Fact]
    public void Fixed_per_batch_overhead_is_added_to_the_estimate()
    {
        double without = EtaText.RemainingSecondsByRate(100, 500, 0, 0, 50);
        double with5 = EtaText.RemainingSecondsByRate(100, 500, 0, 0, 50, fixedOverheadSec: 5 * 1.15);
        Assert.Equal(without + 5.75, with5, 6);
    }

    /// <summary>文案档位与用词与旧口径完全一致(只换数据来源,不换说法)。</summary>
    [Fact]
    public void By_rate_text_uses_the_same_bands()
    {
        // 剩余 100 帧、最近速率 10 帧/秒 ⇒ 约 10 秒
        Assert.Equal("本阶段预计还剩 10 秒",
            EtaText.ForRemainingByRate(100, 200, sampleDone: 0, sampleElapsedSec: 0, cumulativeElapsedSec: 10));
        Assert.False(EtaText.ContainsVagueSeconds(
            EtaText.ForRemainingByRate(100, 200, 0, 0, 10)));
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

    // ---------- K2:整片剩余的下限(整片剩余 ≥ 当前阶段剩余)与单调约束 ----------

    [Fact]
    public void Whole_remaining_never_below_current_stage_remaining()
    {
        // 【真机 bug 的场景】进度行显示"编码 第 1324 帧 / 共 2665 帧 本阶段预计还剩 3.7 分钟",
        // 而"本片剩余"显示 0:30 —— 3.7 分钟 = 222 秒。整片剩余必须被抬到 ≥ 222 秒。
        double whole = 30;          // 按"已用÷进度"外推出来的错值(编码只占整体进度的 96~100 四个点)
        double stage = 3.7 * 60;    // 编码阶段自己的剩余(实测速率)
        double fixedUp = EtaText.ClampWholeRemaining(whole, stage, lastShownSec: -1);
        Assert.True(fixedUp >= stage, $"整片剩余 {fixedUp} 不得低于当前阶段剩余 {stage}");
        Assert.Equal(222, fixedUp, 6);
    }

    [Fact]
    public void Whole_remaining_is_monotonic_within_a_stage()
    {
        // 单调:同阶段内只许变小(消除"越等越久"的抖动)
        Assert.Equal(100, EtaText.ClampWholeRemaining(150, 0, lastShownSec: 100), 6);
        Assert.Equal(80, EtaText.ClampWholeRemaining(80, 0, lastShownSec: 100), 6);
        // 阶段剩余(硬下限)高过上次显示值时允许抬上去 —— 下限优先于单调,否则会被永久压死
        Assert.Equal(300, EtaText.ClampWholeRemaining(150, 300, lastShownSec: 100), 6);
        // 没显示过(≤0)时不受单调约束
        Assert.Equal(150, EtaText.ClampWholeRemaining(150, 0, lastShownSec: -1), 6);
    }

    [Fact]
    public void Whole_remaining_handles_unknown_or_bogus_inputs()
    {
        // 阶段剩余未知(0)/非法(NaN)时不参与下限,只做单调 + 非负
        Assert.Equal(50, EtaText.ClampWholeRemaining(50, 0, -1), 6);
        Assert.Equal(50, EtaText.ClampWholeRemaining(50, double.NaN, -1), 6);
        Assert.Equal(0, EtaText.ClampWholeRemaining(double.NaN, 0, -1), 6);
        Assert.Equal(0, EtaText.ClampWholeRemaining(-5, 0, -1), 6);
        Assert.Equal(200, EtaText.ClampWholeRemaining(-5, 200, -1), 6);   // 有确定下限时以它为准
    }

    [Fact]
    public void Stage_remaining_guards_refuse_to_guess()
    {
        // 阶段刚开始/比例太小 → 不给下限(0 = 不参与),拿不准就不给假数
        Assert.Equal(0, EtaText.StageRemainingSeconds(0, 100));
        Assert.Equal(0, EtaText.StageRemainingSeconds(0.01, 100));   // ≤1% 不给
        Assert.Equal(0, EtaText.StageRemainingSeconds(0.5, 3));      // ≤3 秒不给
        Assert.Equal(0, EtaText.StageRemainingSeconds(double.NaN, 100));
        Assert.Equal(0, EtaText.StageRemainingSeconds(0.5, double.NaN));
        // 正常:已完成 50%、净耗时 222 秒 → 还要 222 秒
        Assert.Equal(222, EtaText.StageRemainingSeconds(0.5, 222), 6);
        // 比例被钳到 1(不许出现负的剩余)
        Assert.Equal(0, EtaText.StageRemainingSeconds(1.5, 222), 6);
    }

    [Fact]
    public void Remaining_seconds_shares_the_same_guards_as_the_text()
    {
        // 文案与"下限用到的秒数"必须同源:有文案就必须有秒数,没文案就是 -1
        Assert.True(EtaText.RemainingSeconds(100, 200, 20) > 0);
        Assert.Equal("", EtaText.ForRemaining(3, 200, 20));            // done < 4
        Assert.Equal(-1, EtaText.RemainingSeconds(3, 200, 20), 6);
        Assert.Equal("", EtaText.ForRemaining(200, 200, 20));          // 已完成
        Assert.Equal(-1, EtaText.RemainingSeconds(200, 200, 20), 6);
        Assert.Equal("", EtaText.ForRemaining(100, 200, 0.5));         // 净耗时不足 1 秒
        Assert.Equal(-1, EtaText.RemainingSeconds(100, 200, 0.5), 6);
    }

    // ===== 2026-09-21:「开跑前的预计用时」文案(用户反馈"预览要等很久才开始") =====
    /// <summary>`Duration` 是"这一段**总共**大约要多久"(还没开始时给预期),与 `ForRemaining` 的
    /// "本阶段预计还剩"(跑起来的倒计时)是两件事 —— 共用同一个三档口径,但不许互相借文案,
    /// 否则用户会把"总共 9 分钟"读成"还剩 9 分钟"(那种误导比不给数字更糟)。</summary>
    [Fact]
    public void Duration_text_is_a_total_not_a_remainder()
    {
        Assert.Equal("9 秒", EtaText.Duration(9.4));            // 与 ForRemaining 同口径:(int) 截断
        Assert.Equal("1.5 分钟", EtaText.Duration(90));
        Assert.Equal("8.9 分钟", EtaText.Duration(532));        // 真机实测那次预览(3 秒素材跑了 532 秒)
        Assert.Equal("1.5 小时", EtaText.Duration(5400));
        // 语义必须与"还剩"区分开:两条文案不许互相包含
        Assert.DoesNotContain("预计还剩", EtaText.Duration(532));
        Assert.DoesNotContain("本阶段", EtaText.Duration(532));
        // 不足 1 秒 / 非法值:不给数字(调用方直接判空跳过 —— 那种量级的活不需要预告)
        Assert.Equal("", EtaText.Duration(0.9));
        Assert.Equal("", EtaText.Duration(0));
        Assert.Equal("", EtaText.Duration(-5));
        Assert.Equal("", EtaText.Duration(double.NaN));
        Assert.Equal("", EtaText.Duration(double.PositiveInfinity));
        // 回归守卫:任何档位都不许出现"几秒"这种既非数字又非确定的说法(与 ForRemaining 同一条铁律)
        foreach (double s in new[] { 1, 2, 9.9, 59, 60, 3599, 3600, 100000 })
            Assert.False(EtaText.ContainsVagueSeconds(EtaText.Duration(s)), $"Duration({s}) 里出现了含糊的\"几秒\"");
    }
}
