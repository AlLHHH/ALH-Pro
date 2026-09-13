using AlhPro.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>内容帧率(拍数)置信度打分的单测(任务 M5)。
/// 【真机反馈 → 本文件的回归用例】「素材明明有拍数,智能-均衡却报信度不足 → 一帧不删」。
/// 根因:旧口径拿**均值**当参照、拿**变异系数**当稳定性,只要有几个转场造成的超长间隔,
/// 正常间隔就不再算"正常" → 分数被压到 0.5 门槛以下。
/// 现在:中位数为锚、MAD 定离散、容差相对周期 ±20%、有效事件占比惩罚开方弱化;
/// **判定阈值(均衡 0.5 / 激进 0.35 / 保守 0.7 + 常见拍数)一个都没动**。</summary>
public class ContentFpsConfidenceTests
{
    /// <summary>回归用例:1拍2(30fps 源 → 间隔 2 帧)但有两次转场把间隔拉到 4/6。
    /// 旧口径被压到 0.5 以下(均衡档拒绝采用 → "选了去重却一帧不删"),新口径 ≥0.5 正常采用。</summary>
    [Fact]
    public void Pattern_with_outlier_gaps_is_no_longer_rejected_by_balanced_gate()
    {
        var gaps = new double[] { 2, 2, 4, 2, 6, 2, 2, 4, 2 };
        var sc = ContentFpsConfidence.Compute(gaps, 30);

        Assert.False(sc.Continuous);
        Assert.True(sc.Confidence >= 0.50, $"新口径置信度 {sc.Confidence:0.###} 仍低于均衡门槛 0.5(回归未修复)");
        // 证据:同一组间隔在旧口径下算出来是 <0.5(所以旧代码真的会拒绝采用)
        double legacy = LegacyConfidence(gaps, 30, out _);
        Assert.True(legacy < 0.50, $"旧口径复刻值 {legacy:0.###} 竟然 ≥0.5 —— 说明这条回归用例选错了输入");
        Assert.True(sc.Confidence > legacy, "新口径没有比旧口径更信任真拍数");
    }

    /// <summary>规则拍数(1拍3:30fps 源 → 间隔 3 帧 → 内容 10fps)必须拿高分。</summary>
    [Fact]
    public void Regular_pattern_scores_high()
    {
        var sc = ContentFpsConfidence.Compute(new double[] { 3, 3, 3, 3, 3, 3, 3, 3 }, 30);
        Assert.False(sc.Continuous);
        Assert.Equal(10.0, sc.ContentFps, 6);
        Assert.Equal(3, sc.Period);
        Assert.Equal(1.0, sc.Confidence, 6);
        Assert.True(sc.Confidence >= 0.70);   // 连"保守"档的 0.7 门槛都过
    }

    /// <summary>长周期相对容差:间隔 ≈10 帧(内容 3fps)时,±20% = ±2 帧,抖动 10~12 帧仍算同一节奏
    /// (旧口径的绝对 ±1 帧会把 12 判成"不一致")。</summary>
    [Fact]
    public void Long_period_tolerates_relative_jitter()
    {
        var gaps = new double[] { 10, 11, 10, 12, 10, 11, 10 };
        var sc = ContentFpsConfidence.Compute(gaps, 30);
        Assert.Equal(7, sc.NearCount);
        Assert.Equal(7, sc.UsedGaps);
        Assert.True(sc.Confidence >= 0.99, $"相对容差没生效:{sc.Confidence:0.###}");
        Assert.True(sc.Confidence >= LegacyConfidence(gaps, 30, out _));
    }

    /// <summary>反面:真正杂乱的间隔(没有节奏)必须低分,连"激进"档的 0.35 门槛都过不去。</summary>
    [Fact]
    public void Irregular_gaps_stay_low()
    {
        var sc = ContentFpsConfidence.Compute(new double[] { 1, 3, 5, 7, 9 }, 30);
        Assert.False(sc.Continuous);
        Assert.True(sc.Confidence < 0.35, $"杂乱间隔被打成 {sc.Confidence:0.###}(不该被采用)");
    }

    /// <summary>离群间隔不参与统计:一个 30 帧的超长间隔不能把内容帧率带偏(1拍3 仍是 10fps)。</summary>
    [Fact]
    public void Outlier_gap_does_not_move_content_fps()
    {
        var sc = ContentFpsConfidence.Compute(new double[] { 3, 3, 3, 3, 3, 3, 30 }, 30);
        Assert.Equal(10.0, sc.ContentFps, 6);
        Assert.Equal(3, sc.Period);
        Assert.Equal(6, sc.UsedGaps);          // 30 被 [0.5m, 2m] 裁掉
        Assert.True(sc.Confidence >= 0.90);
    }

    /// <summary>有效事件占比的惩罚已弱化为 sqrt(原线性):8 个正常 + 4 个离群的段,
    /// 新口径 = sqrt(8/12) ≈ 0.8165,而旧线性只会给 0.667。</summary>
    [Fact]
    public void Coverage_penalty_is_softened_to_sqrt()
    {
        var gaps = new double[] { 4, 4, 4, 4, 4, 4, 4, 4, 40, 40, 40, 40 };
        var sc = ContentFpsConfidence.Compute(gaps, 30);
        Assert.Equal(8, sc.UsedGaps);
        Assert.Equal(12, sc.TotalGaps);
        Assert.Equal(Math.Sqrt(8.0 / 12.0), sc.Confidence, 6);
        Assert.True(sc.Confidence > 8.0 / 12.0, "惩罚没有弱化(还是线性 trimmed/total)");
    }

    /// <summary>「几乎连续运动」:平均间隔 <1.2 帧 → 与旧口径一致地返回输入帧率 + 固定 0.25 置信度。</summary>
    [Theory]
    [InlineData(30.0)]
    [InlineData(25.0)]
    public void Continuous_motion_returns_input_fps(double inFps)
    {
        var sc = ContentFpsConfidence.Compute(new double[] { 1, 1, 1, 1 }, inFps);
        Assert.True(sc.Continuous);
        Assert.Equal(inFps, sc.ContentFps, 6);
        Assert.Equal(0.25, sc.Confidence, 6);
    }

    /// <summary>间隔样本不足(事件太少)→ 与"连续运动"同路径(调用方本来就不会走到这里:事件 <4 已提前返回)。</summary>
    [Theory]
    [InlineData(new double[0])]
    [InlineData(new double[] { 2, 2 })]
    public void Too_few_gaps_falls_back_to_continuous(double[] gaps)
    {
        var sc = ContentFpsConfidence.Compute(gaps, 30);
        Assert.True(sc.Continuous);
        Assert.Equal(30.0, sc.ContentFps, 6);
    }

    /// <summary>确定性:同一输入重复计算结果完全一致(无随机数、无排序歧义)。</summary>
    [Fact]
    public void Score_is_deterministic()
    {
        var gaps = new double[] { 2, 2, 4, 2, 6, 2, 2, 4, 2 };
        var a = ContentFpsConfidence.Compute(gaps, 30);
        var b = ContentFpsConfidence.Compute(gaps, 30);
        Assert.Equal(a, b);
    }

    /// <summary>中位数约定:上中位(偶数个取 index = count/2)—— 与迁移前一致,保证分数可复现。</summary>
    [Fact]
    public void Median_uses_upper_middle_convention()
    {
        Assert.Equal(3.0, ContentFpsConfidence.Median(new double[] { 1, 2, 3, 4 }), 6);
        Assert.Equal(2.0, ContentFpsConfidence.Median(new double[] { 5, 1, 2, 4, 2 }), 6);
        Assert.Equal(0.0, ContentFpsConfidence.Median(Array.Empty<double>()), 6);
    }

    /// <summary>【旧口径复刻 · 只用于给回归用例作证】迁移前的打分公式:
    /// 参照均值、容差绝对 ±1 帧、稳定性 1−σ/μ、覆盖惩罚线性 trimmed/total。
    /// 保留在测试里(而不是源码里)是为了让"旧口径真的会拒"这件事有可执行证据。</summary>
    private static double LegacyConfidence(double[] gaps, double inFps, out double contentFps)
    {
        var g = gaps.OrderBy(x => x).ToList();
        double med = g[g.Count / 2];
        var trimmed = g.Where(x => x >= med * 0.5 && x <= med * 2.0).ToList();
        if (trimmed.Count < 3) trimmed = g;
        double meanGap = trimmed.Average();
        contentFps = Math.Clamp(inFps / meanGap, 0.5, inFps);
        int near = trimmed.Count(x => Math.Abs(x - meanGap) <= 1.0);
        double meanSq = trimmed.Sum(x => (x - meanGap) * (x - meanGap)) / Math.Max(1, trimmed.Count);
        double cv = meanGap > 0 ? Math.Sqrt(meanSq) / meanGap : 1.0;
        return Math.Clamp(
            (double)near / Math.Max(1, trimmed.Count) * Math.Max(0.0, 1.0 - cv)
            * (double)trimmed.Count / Math.Max(1, gaps.Length), 0, 1);
    }
}
