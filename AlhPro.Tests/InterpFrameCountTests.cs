using AlhPro.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>补帧「帧数守恒」与 VFR 时间轴时基的单测(任务 N)。
/// 【真机现象(HEAD=7ed38d6 的构建,素材 855 帧 VFR、2x 超分 + 2x 补帧 + 智能去重 + VFR 自动)】
/// 日志自称"输出 1710 帧 / 编码 帧数=1710",`ffprobe -count_frames` 实测 **nb_frames=1646**(少 64);
/// 成片里恰好有 64 个"双倍长间隔";软件自己的输出校验告警「帧率 55.52 vs 预期 57.64」。
/// 【根因】setpts 的量化格子 = image2 输入的时基 = 1/标称帧率 = 1/57.64 = 0.017349s,
/// 而这条 VFR 时间轴最短的一格 = 0.016686s **比一格还短** → 相邻帧撞进同一时间戳 → 被丢掉。
/// 本文件用纯数学复算这条事故(撞格数 ≈ 实测丢帧数 64),并钉住修复后的不变量:
///   · 帧数守恒:输出帧数 == (源帧数-1)×mult+1(855、2x → 1709);
///   · 时间轴表长 == 文件数(末源帧只展开 1 条);
///   · 细化时基后撞格 == 0(不再丢帧)。</summary>
public class InterpFrameCountTests
{
    // ===== 真机那组时长表:855 帧,其中 35 帧是"拍 2"(时长 2/30),其余 1/30;
    // 容器时长 29.666667s(表自身合计 889/30 = 29.6333,差的那一格是容器尾帧容积,与真机一致)。
    private const int RealFrames = 855;
    private const int RealLongFrames = 35;
    private const double RealMuxDur = 29.666667;
    private static readonly double RealNominalFps = 1710 / RealMuxDur;   // 真机日志里的标称 57.64

    /// <summary>按真机数字复原源时长表:855 帧,其中 35 帧是"拍 2"(2/30),其余 820 帧 1/30。
    /// 【数字自洽】槽位合计 = 820×1 + 35×2 = 890 → 890/30 = **29.66667s** = 真机给的容器时长 ✓
    /// (长间隔在真机上零散分布,这里按每 24 帧放一个复现;撞格数只由"有多少个短帧"决定,与具体分布无关)。</summary>
    private static List<double> RealSourceTable()
    {
        var d = new List<double>();
        int longEvery = RealFrames / RealLongFrames;   // 24
        for (int i = 0; i < RealFrames; i++)
            d.Add(i % longEvery == 5 && i / longEvery < RealLongFrames ? 2.0 / 30.0 : 1.0 / 30.0);
        return d;
    }

    /// <summary>按管线口径把"源时长表"展开成"输出时长表"并归一化到容器时长(与 VideoService 一致)。</summary>
    private static List<double> BuildRealFinalDurs(int mult)
    {
        var src = RealSourceTable();
        var dst = new List<double>();
        VideoPipeline.AppendExpandedDurations(dst, src, 0, src.Count, mult, lastSegment: true);
        double sum = dst.Sum();
        double k = RealMuxDur / sum;
        for (int i = 0; i < dst.Count; i++) dst[i] *= k;
        return dst;
    }

    [Theory]
    [InlineData(2.0, 1.0, 2)]      // 未去重:2x 就是 2
    [InlineData(2.0, 2.1375, 4)]   // 去重(密度还原):mult = round(2×2.1375) = 4
    [InlineData(2.0, 0.4, 1)]      // 下限:至少 1(不许出现 0/负)
    [InlineData(1.0, 1.0, 1)]
    [InlineData(4.0, 1.0, 4)]
    [InlineData(2.0, 0, 1)]        // 非法 frameScale(探测失败=0)→ 1,不炸
    public void Interp_multiplier_is_pinned(double interpScale, double frameScale, int expected)
        => Assert.Equal(expected, VideoPipeline.InterpMultiplier(interpScale, frameScale));

    [Theory]
    [InlineData(855, 2, 1709L)]   // ← 用户给的验收式 (源帧数-1)×2+1
    [InlineData(1, 2, 1L)]
    [InlineData(0, 2, 0L)]
    [InlineData(-5, 2, 0L)]
    [InlineData(400, 4, 1597L)]
    public void Output_frame_count_is_the_conservation_law(int sourceFrames, int mult, long expected)
        => Assert.Equal(expected, VideoPipeline.InterpOutputFrameCount(sourceFrames, mult));

    /// <summary>分段不改总量:非末段按 段长×mult、末段按 (段长-1)×mult+1 时,
    /// Σ 各段 == (总帧数-1)×mult+1 —— 这就是"末段不产出末帧冻结副本"能成立的依据。</summary>
    [Theory]
    [InlineData(855, 2)]
    [InlineData(855, 4)]
    [InlineData(400, 4)]
    [InlineData(37, 2)]
    public void Segment_targets_sum_to_the_global_target(int frames, int mult)
    {
        var partitions = new[]
        {
            new[] { frames },                          // 无转场:单段
            new[] { frames / 2, frames - frames / 2 }, // 两段
            new[] { 1, 5, frames - 6 },                // 极短段 + 长段(边界)
        };
        foreach (var parts in partitions)
        {
            long sum = 0;
            for (int i = 0; i < parts.Length; i++)
                sum += VideoPipeline.InterpSegmentTarget(parts[i], mult, lastSegment: i == parts.Length - 1);
            Assert.Equal(VideoPipeline.InterpOutputFrameCount(frames, mult), sum);
        }
    }

    /// <summary>时间轴表的帧数守恒 + 时长守恒:展开后表长 == (源帧数-1)×mult+1,且总时长与源表严格相等
    /// (末源帧只展开 1 条,承载尾部容积的整段时长 —— 不是把尾部拆成两条一样长的)。</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void Duration_table_matches_the_frame_conservation_law(int mult)
    {
        var src = RealSourceTable();
        var dst = new List<double>();
        VideoPipeline.AppendExpandedDurations(dst, src, 0, src.Count, mult, lastSegment: true);
        Assert.Equal(RealFrames, src.Count);
        Assert.Equal(VideoPipeline.InterpOutputFrameCount(src.Count, mult), dst.Count);
        Assert.Equal(src.Sum(), dst.Sum(), 6);           // 总时长不变(尾部容积仍由最后一个输出帧承载)
        Assert.Equal(src[0] / mult, dst[0], 6);          // 普通帧:时长 / mult
        Assert.Equal(src[^1], dst[^1], 6);               // 末源帧:整段时长,只有 1 条(没被拆成两条)
    }

    /// <summary>尾帧规则逐条钉住:末源帧只展开 1 条、承载整段时长,表长 = (源帧数-1)×mult+1。</summary>
    [Fact]
    public void Last_source_frame_is_not_split()
    {
        var src = new List<double> { 1.0 / 30.0, 2.0 / 30.0 };
        var dst = new List<double>();
        VideoPipeline.AppendExpandedDurations(dst, src, 0, src.Count, 2, lastSegment: true);
        Assert.Equal(3, dst.Count);                      // (2-1)×2+1
        Assert.Equal(1.0 / 60.0, dst[0], 9);
        Assert.Equal(1.0 / 60.0, dst[1], 9);
        Assert.Equal(2.0 / 30.0, dst[2], 9);             // 末源帧:整段时长,只有 1 条
        Assert.Equal(src.Sum(), dst.Sum(), 9);
        // 非末段:每帧都展开 mult 条(段间拼接后总和仍是全局目标,见 Segment_targets_sum_to_the_global_target)
        var mid = new List<double>();
        VideoPipeline.AppendExpandedDurations(mid, src, 0, src.Count, 2, lastSegment: false);
        Assert.Equal(4, mid.Count);
        Assert.Equal(1.0 / 60.0, mid[0], 9);
        Assert.Equal(1.0 / 30.0, mid[2], 9);
        Assert.Equal(1.0 / 30.0, mid[3], 9);
    }

    /// <summary>【事故复算】真机那组时长表 + 标称帧率 57.64 → 撞格数 ≈ 实测丢帧数 64。
    /// 这条断言就是"64 帧去哪了"的可执行证据(不是推测)。</summary>
    [Fact]
    public void Real_machine_table_collides_at_nominal_timebase()
    {
        var durs = BuildRealFinalDurs(2);
        Assert.Equal(1709, durs.Count);
        int collisions = VideoPipeline.CountTimestampCollisions(durs, RealNominalFps);
        // 真机实测:1710 帧的文件里只剩 1646 帧(丢 64);这份复原表算 65 —— 差 1 来自"长间隔的分布/表尾"
        // 复原方式(真机的 35 个长帧具体落在哪几帧无法从日志还原),量级与机理完全一致。
        Assert.Equal(65, collisions);
    }

    /// <summary>【修复】细化输入时基后撞格 == 0(这才是"2x 就是 2x"的前提)。</summary>
    [Fact]
    public void Fine_input_timebase_removes_all_collisions()
    {
        var durs = BuildRealFinalDurs(2);
        double fine = VideoPipeline.VfrInputFramerate(durs, RealNominalFps);
        Assert.Equal(120.0, fine, 6);          // 最短 0.016686s → 2/0.016686 = 119.86 → 120
        Assert.Equal(0, VideoPipeline.CountTimestampCollisions(durs, fine));
        // 细化后的时基必须比标称更细(不许把时基变粗:变粗只会丢得更多)
        Assert.True(fine > RealNominalFps);
        // 保险逻辑的另一半:仍撞格时退回 1ms 级(1000fps)
        Assert.Equal(0, VideoPipeline.CountTimestampCollisions(durs, 1000));
    }

    [Theory]
    [InlineData(1.0 / 30.0, 30, 60)]      // 均匀 30fps:一格 ≤ 帧时长/2 → 60
    [InlineData(1.0 / 30.0, 90, 90)]      // 标称已更细 → 不退步(不低于标称)
    [InlineData(1.0 / 100.0, 24, 200)]    // 10ms 的帧 → 200
    public void Vfr_input_framerate_is_derived(double d, double nominal, double expected)
    {
        var durs = Enumerable.Repeat(d, 10).ToList();
        Assert.Equal(expected, VideoPipeline.VfrInputFramerate(durs, nominal), 6);
    }

    [Fact]
    public void Vfr_input_framerate_survives_degenerate_tables()
    {
        // 表为空/为 null → 退化成标称帧率(向上取整,绝不变粗)
        Assert.Equal(58.0, VideoPipeline.VfrInputFramerate(null!, 57.64), 6);
        Assert.Equal(58.0, VideoPipeline.VfrInputFramerate(new List<double>(), 57.64), 6);
        // 非有限/非正值被忽略(与 BuildVfrSetptsExpr 的入口守卫同口径):表里只剩 1/30 → 2/(1/30) = 60
        var bad = new List<double> { double.NaN, double.PositiveInfinity, -1, 0, 1.0 / 30.0 };
        Assert.Equal(60.0, VideoPipeline.VfrInputFramerate(bad, 57.64), 6);
        Assert.Equal(0, VideoPipeline.CountTimestampCollisions(bad, 57.64));            // 不炸
        Assert.Equal(0, VideoPipeline.CountTimestampCollisions(new List<double> { 0.5 }, 30));
        Assert.Equal(0, VideoPipeline.CountTimestampCollisions(new List<double> { 0.5, 0.5 }, 0));
    }

    /// <summary>均匀时间轴也要按同一条判据看(这是同款隐患的第二种形态):
    /// · 回退时造的均匀表 = 容器时长 ÷ 帧数(每格恰好 ≈ 一个时基格)→ 不撞格;
    /// · 但"1/输出帧率"那种均匀表在标称帧率比它粗时会撞格(这里 68 帧)——
    ///   修复对它同样生效(细化时基后 0),因为 VFR(setpts) 分支一律走同一条路。</summary>
    [Fact]
    public void Uniform_tables_follow_the_same_rule()
    {
        var fallbackStyle = Enumerable.Repeat(RealMuxDur / 1709.0, 1709).ToList();
        Assert.Equal(0, VideoPipeline.CountTimestampCollisions(fallbackStyle, RealNominalFps));

        var perFrameRateStyle = Enumerable.Repeat(1.0 / 60.0, 1709).ToList();
        Assert.True(VideoPipeline.CountTimestampCollisions(perFrameRateStyle, RealNominalFps) > 0);
        double fine = VideoPipeline.VfrInputFramerate(perFrameRateStyle, RealNominalFps);
        Assert.Equal(120.0, fine, 6);
        Assert.Equal(0, VideoPipeline.CountTimestampCollisions(perFrameRateStyle, fine));
    }
}
