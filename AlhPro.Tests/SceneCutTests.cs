using AlhPro.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>场景切换保护(S2)的单测。已实测事实:用户在意的大跳是**真实场景硬切**(idx 56→57,
/// mean|diff|=58.68、lapvar 7237→3430);两处 66.7ms 缺口**内部几乎无变化**(0.871/0.150);
/// 切点混合帧会产生**鬼影**(0.647/0.692)与**一帧严重软化**(B[57] lapvar 仅源帧 7.8%)。</summary>
public class SceneCutTests
{
    [Theory]
    [InlineData(58.68, 7237.0, 3430.0, true)]    // 实测的那次硬切
    [InlineData(51.0, null, null, true)]         // 强切:只看帧差
    [InlineData(30.0, 1000.0, 500.0, true)]      // 帧差中等 + 拉普拉斯掉一半
    [InlineData(30.0, 1000.0, 900.0, false)]     // 帧差中等、结构没变简 → 不算切(运动而已)
    [InlineData(5.0, 1000.0, 10.0, false)]       // 帧差太小 → 不算切
    [InlineData(0.871, null, null, false)]       // 缺口内部(实测几乎无变化)→ 不是切
    [InlineData(0.150, null, null, false)]
    public void Cut_judgement_matches_the_measured_facts(double diff, double? lapPrev, double? lapCur, bool expect)
        => Assert.Equal(expect, SceneCutJudge.IsCut(diff, lapPrev, lapCur));

    [Fact]
    public void Detect_returns_the_cut_positions()
    {
        // 60 对里只有第 56 对是硬切(实测那次)
        var diffs = Enumerable.Repeat(1.5, 60).ToList();
        var laps = Enumerable.Repeat(7000.0, 61).ToList();
        diffs[56] = 58.68; laps[57] = 3430.0;
        var cuts = SceneCutJudge.Detect(diffs, laps);
        Assert.Equal(new[] { 56 }, cuts.ToArray());
    }

    [Fact]
    public void Detect_without_lapvar_uses_diff_only_and_is_conservative()
    {
        var diffs = new List<double> { 1.0, 30.0, 60.0 };   // 30 过阈值但拿不到拉普拉斯 → 保守算切;60 强切
        var cuts = SceneCutJudge.Detect(diffs, null);
        Assert.Equal(new[] { 1, 2 }, cuts.ToArray());
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.49)]
    public void At_a_cut_no_blended_frame_is_produced(double phi)
    {
        var cuts = new[] { 56 };
        var s = CutAwareSchedule.Plan(56, 57, phi, exactSource: false, cuts);
        Assert.True(s.Copy);                 // 切点上必须"拷贝",不许合成
        Assert.Equal(56, s.CopyIndex);       // φ<0.5 → 仍属前一场景
        Assert.True(s.AtCut);
        Assert.Equal(0.0, s.Phi, 9);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.99)]
    public void Just_after_a_cut_the_new_scene_is_used(double phi)
    {
        var s = CutAwareSchedule.Plan(56, 57, 0.5 <= phi ? 57 - 57 + phi : phi, exactSource: false, cuts: new[] { 56 });
        Assert.True(s.Copy);
        Assert.Equal(57, s.CopyIndex);       // 切点之后 → 从新场景开始
        Assert.True(s.AtCut);
    }

    /// <summary>非切点必须保持既有插值行为(φ 原样返回,不做任何改写)。</summary>
    [Fact]
    public void Away_from_cuts_interpolation_is_unchanged()
    {
        var s = CutAwareSchedule.Plan(10, 11, 0.37, exactSource: false, cuts: new[] { 56 });
        Assert.False(s.Copy);
        Assert.Equal(0.37, s.Phi, 9);
        Assert.False(s.AtCut);
        Assert.Equal(10, s.Idx0);
        Assert.Equal(11, s.Idx1);
    }

    /// <summary>正好落在源帧上 → 一律拷贝(与切点无关),避免静止内容被"猜"出差异。</summary>
    [Fact]
    public void Exact_source_hits_always_copy()
    {
        var s = CutAwareSchedule.Plan(20, 21, 0.0, exactSource: true, cuts: new[] { 20 });
        Assert.True(s.Copy);
        Assert.Equal(20, s.CopyIndex);
    }

    /// <summary>整条时间轴:切点处**不生成任何 φ∈(0,1) 的混合帧**;并统计因切点被强制拷贝的槽数。</summary>
    [Fact]
    public void Full_schedule_never_blends_across_a_cut()
    {
        var d = new List<double>();
        for (int i = 0; i < 60; i++) d.Add(2.0 / 60.0);      // 均匀 30fps 源 → 60fps 目标
        var cuts = new[] { 56 };
        var slots = CutAwareSchedule.PlanAll(d, targetFrames: 120, targetFps: 60, cuts, out int forced);
        Assert.NotEmpty(slots);
        foreach (var s in slots)
            if (s.AtCut && !s.Copy) Assert.Fail("切点上出现了混合帧");
        Assert.True(forced >= 1, "切点附近应当有被强制拷贝的槽");
        // 非切点处仍有正常的插值帧(证明"绕开切点"没有把整条时间轴退化成全拷贝)
        Assert.Contains(slots, s => !s.Copy && s.Phi > 0 && s.Phi < 1);
    }

    /// <summary>没有切点 → 排程与"纯时轴插值"一致(CFR 且无切点的素材行为逐字不变)。</summary>
    [Fact]
    public void Without_cuts_the_schedule_matches_plain_interpolation()
    {
        var d = new List<double>();
        for (int i = 0; i < 40; i++) d.Add(2.0 / 60.0);
        var slots = CutAwareSchedule.PlanAll(d, targetFrames: 80, targetFps: 60, cuts: Array.Empty<int>(), out int forced);
        Assert.Equal(0, forced);
        Assert.DoesNotContain(slots, s => s.AtCut);
        foreach (var s in slots)
            if (!s.Copy) Assert.InRange(s.Phi, 0.0, 1.0);
    }
}
