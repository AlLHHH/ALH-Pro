using AlhPro.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【任务 S1 接线契约】"平滑时间轴"的**排程**必须是可验收的:
/// 目标帧数/时间轴由 TimelineFlattenPlan 定,切点保护由 CutAwareSchedule 叠上去 ——
/// 这里钉的是两者**合起来**的四条硬要求(用户口径):
///   ① 输出帧数 == Decide 给的目标帧数(切点保护只把"混合槽"改写成"拷贝槽",不增删槽);
///   ② 切点上绝不出现 φ∈(0,1) 的跨场景混合帧(鬼影帧);
///   ③ 切点之后仍从新场景开始,且新场景内部照常插值(没有退化成"全片拷贝");
///   ④ 没有切点时,排程与"纯时轴插值"逐字一致(等于没接保护)。
/// 【未真机验证】这里只能证明**排程**满足要求;"鬼影是否真的消失"要等真机在源 1~3 秒窗口逐帧比对。</summary>
public class SmoothTimelineScheduleTests
{
    /// <summary>用户那条素材的形状:60 帧、内容 30fps、两处 66.7ms 的紧邻缺口(其余均匀)。</summary>
    private static List<double> UserLikeDurations()
    {
        var d = new List<double>();
        for (int i = 0; i < 60; i++) d.Add(1.0 / 30.0);
        d[52] = 0.0667;
        d[54] = 0.0667;   // 两处紧邻缺口(实测 1.73s / 1.83s 处)
        return d;
    }

    private static double Total(List<double> d) => d.Sum();

    [Fact]
    public void Schedule_keeps_the_target_frame_count_exactly()
    {
        var durs = UserLikeDurations();
        var plan = TimelineFlattenPlan.Decide(durs, 30, 2);
        Assert.True(plan.Flatten, "这条素材必须判定为「可填平」");
        var slots = CutAwareSchedule.PlanAll(durs, plan.TargetFrames, plan.TargetFps, new[] { 56 }, out int forced);
        Assert.Equal(plan.TargetFrames, slots.Count);      // 切点保护不增删槽 → 帧数守恒
        Assert.True(forced > 0, "切点附近应当确实有被强制改拷贝的槽");
    }

    [Fact]
    public void No_slot_blends_across_the_cut_and_new_scene_still_interpolates()
    {
        var durs = UserLikeDurations();
        var plan = TimelineFlattenPlan.Decide(durs, 30, 2);
        var cuts = new[] { 56 };
        var slots = CutAwareSchedule.PlanAll(durs, plan.TargetFrames, plan.TargetFps, cuts, out _);

        // ① 跨切点(源帧 56 → 57)的槽一律是拷贝,绝无 φ∈(0,1) 的合成 → 不会出现"上个镜头的字叠在新镜头上"
        bool sawCutPair = false;
        foreach (var s in slots)
        {
            if (s.Idx0 != 56 || s.Idx1 != 57) continue;
            sawCutPair = true;
            Assert.True(s.Copy, "切点上的槽必须是拷贝(不许合成混合帧)");
            Assert.True(s.Phi == 0 || s.Phi == 1, $"切点槽的 φ 只能是 0/1,实得 {s.Phi}");
        }
        Assert.True(sawCutPair, "这条素材在 60fps 目标下必然有槽落在切点对 (56,57) 上");

        // ② 切点之后(新场景内部)照常插值:排程没有退化成"全部拷贝"
        Assert.Contains(slots, s => !s.Copy && s.Idx0 > 56);
        // ③ 切点之前(前一场景内部)也照常插值
        Assert.Contains(slots, s => !s.Copy && s.Idx1 <= 56);
    }

    [Fact]
    public void Cut_slot_copies_the_previous_scene_before_the_cut_and_the_new_one_after()
    {
        var durs = UserLikeDurations();
        var plan = TimelineFlattenPlan.Decide(durs, 30, 2);
        var slots = CutAwareSchedule.PlanAll(durs, plan.TargetFrames, plan.TargetFps, new[] { 56 }, out _);
        var cutSlots = slots.Where(s => s.Idx0 == 56 && s.Idx1 == 57).ToList();
        Assert.NotEmpty(cutSlots);
        // φ<0.5(切点之前的时间)→ 拷前一场景帧 56;φ≥0.5(切点之后)→ 从新场景帧 57 开始
        foreach (var s in cutSlots)
            Assert.Equal(s.Phi < 0.5 ? 56 : 57, s.CopyIndex);
        Assert.Contains(cutSlots, s => s.Phi < 0.5 && s.CopyIndex == 56);
        Assert.Contains(cutSlots, s => s.Phi >= 0.5 && s.CopyIndex == 57);
    }

    [Fact]
    public void Without_cuts_the_schedule_is_identical_to_plain_timeline_mapping()
    {
        var durs = UserLikeDurations();
        var plan = TimelineFlattenPlan.Decide(durs, 30, 2);
        var slots = CutAwareSchedule.PlanAll(durs, plan.TargetFrames, plan.TargetFps, null, out int forced);
        Assert.Equal(0, forced);
        for (int k = 0; k < slots.Count; k++)
        {
            Assert.True(TimelineFlattenPlan.MapTargetFrame(durs, k, plan.TargetFps,
                out int i0, out int i1, out double phi, out bool exact));
            var s = slots[k];
            Assert.Equal((i0, i1), (s.Idx0, s.Idx1));
            if (exact || phi <= 0) { Assert.True(s.Copy); Assert.Equal(i0, s.CopyIndex); }
            else { Assert.False(s.Copy); Assert.Equal(phi, s.Phi, 9); }
        }
    }

    [Fact]
    public void Uniform_cfr_source_is_never_flattened_so_the_schedule_is_irrelevant()
    {
        // CFR 源(间隔均匀)→ Decide 不填平 → 生产路径根本不会建这道排程(老路径逐字不变)
        var durs = Enumerable.Repeat(1.0 / 30.0, 300).ToList();
        var plan = TimelineFlattenPlan.Decide(durs, 30, 2);
        Assert.False(plan.Flatten);
        Assert.Contains("无缺口", plan.LogLine);
    }

    [Fact]
    public void Target_frame_count_stays_within_half_a_frame_of_the_real_duration()
    {
        var durs = UserLikeDurations();
        var total = Total(durs);
        var plan = TimelineFlattenPlan.Decide(durs, 30, 2);
        // 帧数 = round(真实时长 × 目标帧率) ⇒ "帧数 ÷ 目标帧率"与真实时长最多差半帧
        // (下游再用"帧数 ÷ 源容器时长"标称帧率把这点差折进标称值 → 成片总时长与源一致)
        double durByTargetFps = plan.TargetFrames / plan.TargetFps;
        Assert.True(Math.Abs(durByTargetFps - total) <= 0.5 / plan.TargetFps + 1e-9,
            $"目标帧数 {plan.TargetFrames} 换算时长 {durByTargetFps:0.####} 与真实时长 {total:0.####} 相差超过半帧");
        // 且与旧口径(源帧数-1)×倍率+1 不是同一个数 —— 填平时诊断日志必须打新口径(否则会误导排查)
        Assert.NotEqual((durs.Count - 1) * 2 + 1, plan.TargetFrames);
    }
}
