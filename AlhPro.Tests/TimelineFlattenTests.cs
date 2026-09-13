using AlhPro.Core;
using System;
using System.Collections.Generic;
using Xunit;

namespace AlhPro.Tests;

/// <summary>「平滑时间轴(按时轴填平)」计划器的单测(任务 S)。
/// 用户亲测确认有效:「小样填平没有震颤了」。成因:源 1.73s/1.83s 处紧邻缺口,软件把它切成
/// "小动 → 冻结 → 大跳40 → 大跳29"四段(周围是均匀的 ~2.0)→ 可见顿挫。</summary>
public class TimelineFlattenTests
{
    /// <summary>用户那条素材的形状:大部分间隔均匀(2/60 s),1.73s/1.83s 处有一处紧邻异常。</summary>
    private static List<double> SourceWithGap()
    {
        var d = new List<double>();
        for (int i = 0; i < 100; i++) d.Add(2.0 / 60.0);   // 均匀 2 槽
        d[51] = 1.0 / 60.0;                                 // 缺口:一个短槽(内容一跳)
        d[52] = 1.0 / 60.0;                                 // 紧邻再一个短槽(冻结回补)
        return d;
    }

    [Fact]
    public void Gap_source_gets_flattened_with_uniform_target_fps()
    {
        var d = SourceWithGap();
        var p = TimelineFlattenPlan.Decide(d, 30, 2);
        Assert.True(p.Flatten);
        Assert.Equal(60.0, p.TargetFps, 6);                                  // 源内容帧率 30 × 补帧 2x
        Assert.True(p.GapCount >= 2);
        double total = 0; foreach (var x in d) total += x;
        Assert.Equal(total, p.TotalSeconds, 9);
        Assert.Equal((int)Math.Round(total * 60.0), p.TargetFrames);          // 目标帧数 = round(时长 × 目标帧率)
        Assert.Contains("填平", p.LogLine);
        Assert.Contains("输出", p.LogLine);
    }

    /// <summary>【硬要求】CFR 源(间隔均匀)必须逐字保持现状:不填平。</summary>
    [Fact]
    public void Uniform_source_is_not_flattened()
    {
        var d = new List<double>();
        for (int i = 0; i < 120; i++) d.Add(1.0 / 30.0);
        var p = TimelineFlattenPlan.Decide(d, 30, 2);
        Assert.False(p.Flatten);
        Assert.Equal(0, p.GapCount);
        Assert.Contains("无缺口", p.LogLine);
        Assert.Contains("按原样", p.LogLine);
    }

    [Theory]
    [InlineData(new double[] { })]                                     // 空表
    [InlineData(new double[] { 0.033 })]                               // 不足 3 帧
    [InlineData(new double[] { 0.033, 0.05, double.NaN })]             // 含非法值
    public void Degenerate_tables_do_not_flatten(double[] durs)
        => Assert.False(TimelineFlattenPlan.Decide(durs, 30, 2).Flatten);

    [Fact]
    public void No_interp_or_unknown_fps_does_not_flatten()
    {
        var d = SourceWithGap();
        Assert.False(TimelineFlattenPlan.Decide(d, 30, 1).Flatten);     // 未开补帧
        Assert.False(TimelineFlattenPlan.Decide(d, 0, 2).Flatten);      // 内容帧率未知
    }

    /// <summary>目标时刻正好落在源帧上 → 直接拷贝(不插值),避免静止内容被"猜"出差异(小样那处轻微软化的来源)。</summary>
    [Fact]
    public void Exact_source_hit_copies_instead_of_interpolating()
    {
        var d = new List<double> { 1.0 / 30.0, 1.0 / 30.0, 1.0 / 30.0 };
        // 目标帧率 = 30 → 第 1 个目标时刻 = 1/30 = 源帧 1 的起点 → 必须拷贝
        Assert.True(TimelineFlattenPlan.MapTargetFrame(d, 1, 30, out int i0, out _, out double phi, out bool exact));
        Assert.True(exact);
        Assert.Equal(1, i0);
        Assert.Equal(0.0, phi, 9);
    }

    /// <summary>落在两张源帧之间 → 给出源帧对与 φ∈(0,1),且 φ 随时间单调。</summary>
    [Fact]
    public void Between_frames_maps_to_a_pair_with_monotonic_phi()
    {
        var d = new List<double> { 1.0 / 30.0, 1.0 / 30.0, 1.0 / 30.0 };
        int middle = 0;
        for (int k = 1; k <= 3; k++)   // 60fps 下 k=1/3 落在两帧之间,k=2 正好落在源帧上
        {
            Assert.True(TimelineFlattenPlan.MapTargetFrame(d, k, 60, out int i0, out int i1, out double phi, out bool exact));
            Assert.True(i1 == i0 + 1 || i1 == i0);
            Assert.InRange(phi, 0.0, 1.0);
            if (!exact) { Assert.True(phi > 0 && phi < 1); middle++; }
            else Assert.Equal(0.0, phi, 9);       // 落在源帧上 → 拷贝,φ=0(不许插值)
        }
        Assert.Equal(2, middle);                  // 至少有两个"中间帧"确实走了插值(φ∈(0,1))
        // 越界(时刻超过总时长)→ 不映射
        Assert.False(TimelineFlattenPlan.MapTargetFrame(d, 999, 60, out _, out _, out _, out _));
    }

    /// <summary>累计时刻表:首项 0、末项 = 总时长、严格单调不减。</summary>
    [Fact]
    public void Cumulative_times_are_monotonic_and_end_at_total()
    {
        var d = new List<double> { 0.1, 0.2, 0.3 };
        var pts = TimelineFlattenPlan.CumulativeTimes(d);
        Assert.Equal(4, pts.Length);
        Assert.Equal(0.0, pts[0], 9);
        Assert.Equal(0.6, pts[3], 9);
        for (int i = 1; i < pts.Length; i++) Assert.True(pts[i] >= pts[i - 1]);
    }

    /// <summary>填平后"输出帧数 = round(时长 × 目标帧率)"与任务 N 的时长守恒不冲突:总时长仍 = 源总时长。</summary>
    [Fact]
    public void Flatten_preserves_total_duration()
    {
        var d = SourceWithGap();
        var p = TimelineFlattenPlan.Decide(d, 30, 4);
        Assert.Equal(120.0, p.TargetFps, 6);
        Assert.Equal((int)Math.Round(p.TotalSeconds * 120.0), p.TargetFrames);
        // 目标帧数 × 目标帧率 的尾部误差不超过半帧
        Assert.InRange(Math.Abs(p.TargetFrames / p.TargetFps - p.TotalSeconds), 0, 0.5 / p.TargetFps);
    }
}
