using AlhPro.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>场景切换保护的**成片守恒 + 真机实测锚点**单测(2026-09-15 场景切换验收补充)。
/// 【为什么单开一个文件】既有 SceneCutTests 钉的是"判据/排程语义";这里钉的是**用户点名的两条硬要求**:
///   ① 切点保护**不许改变输出帧数/时长**(只把混合槽改写成拷贝槽,不增删槽);
///   ② 判据在**真实素材实测值**上必须仍然成立(含"强切档"不可删的证据)。
/// 【实测出处】下面 anchor 用例的数字来自 192 行灰度采样口径的真实测量(离线探针 _qa\cutprobe):
///   · engines\realesrgan\onepiece_demo.mp4(真实动画,640x480/23.976fps/181 帧):
///     真硬切在源帧 99→100,diff=57.11、lap比=0.81(帧 97→100 目视确认是"两人同框 → 主角特写"硬切);
///     同片运动帧对 111(diff=25.42、lap比=1.09)/114(diff=24.10、lap比=0.94)是镜头推移,**不是**切点。
///   · 合成三段硬切 _qa\cutverify\syn_3cut.mp4:切点 29→30 diff=86.13、59→60 diff=80.01;全片次高 diff 仅 2.57。</summary>
public class SceneCutInvarianceTests
{
    /// <summary>【用户硬要求】切点保护只改写"切点上的混合槽",槽数与目标帧数**一个都不变** ——
    /// 这是"成片时长/帧率不变"的纯逻辑依据(真机帧数实测见验收报告)。</summary>
    [Fact]
    public void Cut_protection_does_not_change_output_slot_count()
    {
        var d = new List<double>();
        for (int i = 0; i < 90; i++) d.Add(1.0 / 30.0);
        var cuts = new[] { 29, 59 };
        var withCuts = CutAwareSchedule.PlanAll(d, 180, 60, cuts, out int forced);
        var noCuts = CutAwareSchedule.PlanAll(d, 180, 60, Array.Empty<int>(), out int forced0);

        Assert.Equal(noCuts.Count, withCuts.Count);       // 槽数相同 ⇒ 帧数相同
        Assert.Equal(180, withCuts.Count);
        Assert.True(forced > 0, "切点上应当确实发生了强制拷贝");
        Assert.Equal(0, forced0);
    }

    /// <summary>逐槽对照:被改写的槽**只能是**"跨切点的那一对"上的槽;其余槽(Idx0/Idx1/Phi)逐字不变。
    /// 这一条同时否掉了两种错误实现:① 在切点前后**插入**拷贝帧(会改帧数);② 把整条时轴退化成全拷贝。</summary>
    [Fact]
    public void Only_slots_inside_a_cut_pair_are_rewritten()
    {
        var d = new List<double>();
        for (int i = 0; i < 90; i++) d.Add(1.0 / 30.0);
        var cuts = new[] { 29, 59 };
        var withCuts = CutAwareSchedule.PlanAll(d, 180, 60, cuts, out int forced);
        var noCuts = CutAwareSchedule.PlanAll(d, 180, 60, Array.Empty<int>(), out _);

        int rewritten = 0, blendedStill = 0;
        for (int j = 0; j < withCuts.Count; j++)
        {
            var a = noCuts[j];
            var b = withCuts[j];
            Assert.Equal(a.Idx0, b.Idx0);                 // 时轴映射完全相同(位置没变)
            if (b.Copy && !a.Copy)
            {
                rewritten++;
                Assert.Contains(a.Idx0, cuts);            // 只可能是切点那一对
                Assert.False(a.Phi <= 0 || a.Phi >= 1, "只有原本真的要合成的槽才谈得上'被改写'");
            }
            else
            {
                Assert.Equal(a.Copy, b.Copy);
                Assert.Equal(a.Phi, b.Phi, 12);
                if (!b.Copy) blendedStill++;
            }
        }
        // "被改写"的槽数必须正好等于排程自报的强制拷贝数(台账/日志读的就是 forced)
        Assert.Equal(rewritten, forced);
        // 【精确守恒】旧排程里"本来要合成的槽"= 仍然在合成的槽 + 被切点改写成拷贝的槽。
        // 等式成立 ⇒ 切点保护没有把整条时轴退化成全拷贝,也没有凭空多出/少掉任何槽。
        int blendedInOldPlan = noCuts.Count(s => !s.Copy);
        Assert.Equal(blendedInOldPlan, blendedStill + rewritten);
        Assert.True(blendedStill > 50, "非切点处必须仍在正常合成插值帧");
        Assert.True(blendedInOldPlan > 50);
    }

    /// <summary>切点落在**最后一个帧对**上时不得越界/异常(排程要对边界安全)。</summary>
    [Fact]
    public void Cut_on_the_last_pair_is_handled()
    {
        var d = new List<double>();
        for (int i = 0; i < 10; i++) d.Add(1.0 / 30.0);
        var cuts = new[] { 8 };   // 帧对 8→9 就是最后一对
        var slots = CutAwareSchedule.PlanAll(d, 20, 60, cuts, out int forced);
        Assert.Equal(20, slots.Count);
        Assert.True(forced >= 0);
    }

    /// <summary>空/退化输入:不判切、不抛异常(检测失败必须等于"不做切点保护",即改动前行为)。</summary>
    [Fact]
    public void Degenerate_inputs_never_throw_and_never_invent_cuts()
    {
        Assert.Empty(SceneCutJudge.Detect(Array.Empty<double>(), null));        // 没有帧对 → 没有切点
        Assert.Empty(SceneCutJudge.Detect(new[] { double.NaN }, null));         // NaN 不得被判成切点
        Assert.Empty(SceneCutJudge.Detect(new[] { double.NaN, double.NaN }, null));
        Assert.Empty(CutAwareSchedule.PlanAll(Array.Empty<double>(), 0, 60, Array.Empty<int>(), out _));
        // 反过来:两帧之间真的出现巨大差异(999)时**必须**判切(强切档)——否则极端硬切会漏保护
        Assert.Equal(new[] { 0 }, SceneCutJudge.Detect(new[] { 999.0 }, null).ToArray());
    }

    /// <summary>【真机实测锚点】onepiece_demo.mp4 的真实硬切:diff=57.11、lap比=0.81。
    /// 关键:lap比 0.81 &gt; 0.6,**只有**"强切档(≥50)"才能判出来 —— 删掉强切档这条真硬切就漏检。
    /// 同片运动帧对 111(diff=25.42、lap比=1.09)必须**不判切**(实测目视为连续镜头推移)。</summary>
    [Theory]
    [InlineData(57.11, 1856.16, 1507.47, true)]    // 真实硬切(靠强切档命中)
    [InlineData(25.42, 1531.85, 1669.69, false)]   // 真实运动(过了帧差档,被 lap 档挡住)
    [InlineData(24.10, 2167.15, 2045.09, false)]   // 真实运动(连帧差档都没过)
    public void Real_footage_anchors(double diff, double lapPrev, double lapCur, bool expect)
        => Assert.Equal(expect, SceneCutJudge.IsCut(diff, lapPrev, lapCur));

    /// <summary>【合成三段硬切锚点】_qa\cutverify\syn_3cut.mp4:切点 29→30(diff 86.13)/59→60(diff 80.01),
    /// 全片其余帧对 diff ≤ 2.58 —— 判据的分离度(86/80 对 2.58)在本用例里显式钉住。</summary>
    [Fact]
    public void Synthetic_three_cut_anchors_separate_cleanly()
    {
        var diffs = Enumerable.Repeat(2.0, 89).ToList();
        diffs[29] = 86.13; diffs[59] = 80.01;
        var laps = Enumerable.Repeat(440.4, 90).ToList();
        laps[30] = 319.6; laps[60] = 46.0;
        var cuts = SceneCutJudge.Detect(diffs, laps);
        Assert.Equal(new[] { 29, 59 }, cuts.ToArray());
    }

    /// <summary>【原"已知残差",2026-09-15 已修】单帧全白闪光曾被判成**两处相邻切点**
    /// (实测 syn_flash.mp4:pair 29→30 diff=127.70 与 30→31 diff=127.69)。
    /// 现已按 PySceneDetect 的 min_scene_len=15 帧加滞回 ⇒ 只保留**一处**(先出现的 29),
    /// 端到端效果:普通路径不再切出"只含 1 帧的补帧段",也不再多出一次强制拷贝。
    /// 详细用例与边界见 SceneCutHysteresisTests;这里保留"闪光素材"这个真机锚点在**成片守恒**上的检查。</summary>
    [Fact]
    public void Single_frame_flash_yields_one_cut_after_hysteresis()
    {
        var diffs = Enumerable.Repeat(1.0, 59).ToList();
        diffs[29] = 127.70; diffs[30] = 127.69;
        var cuts = SceneCutJudge.Detect(diffs, null);
        Assert.Equal(new[] { 29 }, cuts.ToArray());          // 只剩一处(先出现的)
        // A/B 对照:把最小间距关掉(minSceneLen:1)= 加滞回之前的行为,仍然是两处相邻切点
        Assert.Equal(new[] { 29, 30 }, SceneCutJudge.Detect(diffs, null, minSceneLen: 1).ToArray());

        var d = new List<double>();
        for (int i = 0; i < 60; i++) d.Add(1.0 / 30.0);
        var slots = CutAwareSchedule.PlanAll(d, 120, 60, cuts, out int forced);
        Assert.Equal(120, slots.Count);                 // 帧数依然守恒
        foreach (var s in slots) if (s.AtCut) Assert.True(s.Copy);
        Assert.True(forced >= 1);
    }
}
