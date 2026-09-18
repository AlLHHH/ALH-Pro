using AlhPro.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>「切点最小间距(滞回)」= `SceneCutJudge.MinSceneLen`(= 15 帧,照 PySceneDetect 的 `min_scene_len` 默认值)。
///
/// 【为什么要有它 —— 两类**实测**误判,都是"连判相邻切点"】
///   · 单帧全白闪光(合成素材 syn_flash.mp4,192 行灰度采样实测):帧对 29→30 = **127.70**、30→31 = **127.69**
///     → 旧判据给出 **2 处相邻切点**。真机日志实测后果:普通路径切出一个**只含 1 帧**的补帧段
///     (`[台账] 补帧段 2/3(帧 31~31,1 帧)`),为那一帧要付一次补帧引擎启动。
///   · 逐帧交替的极端废片(合成素材 syn_alternate.mp4:480 帧、479 对,每对帧差恒 ~219):
///     旧判据 **479 对判 479 处** → 几乎每一对都被保护 ⇒ 补帧退化成"全拷贝"(等于没插帧)。
///     (用户在自己的素材上报的是"479 对判 478 处",量级一致。)
///
/// 【本文件钉的四件事】①相邻两处超阈值 → 只保留一处;②两处**真实**切点距离 ≥ 最小间距 → 都要保留;
/// ③闪光用例不再误判 2 处;④`minSceneLen = 1` 逐字等于"加滞回之前的行为"(证明变化的只有滞回这一件事)。</summary>
public class SceneCutHysteresisTests
{
    /// <summary>① 相邻两帧都超阈值(单帧闪光/闪频)→ **只保留先出现的那一处**。</summary>
    [Fact]
    public void Adjacent_cuts_collapse_into_the_first_one()
    {
        // 两处强切紧挨着:29 与 30
        var diffs = Enumerable.Repeat(1.0, 59).ToList();
        diffs[29] = 127.70;
        diffs[30] = 127.69;
        var cuts = SceneCutJudge.Detect(diffs, null);
        Assert.Equal(new[] { 29 }, cuts.ToArray());
    }

    /// <summary>② 两个**真实**切点相距大于最小间距时**都要保留** —— 滞回不许把真切点也合并掉。
    /// 用的是本工程实测的真实素材锚点:合成三段硬切的两处切点在源帧 29 与 59(相距 **30 帧** ≥ 15)。</summary>
    [Fact]
    public void Real_cuts_farther_than_the_min_gap_are_both_kept()
    {
        var diffs = Enumerable.Repeat(2.0, 89).ToList();
        diffs[29] = 86.13;      // 实测:syn_3cut.mp4 切点 1
        diffs[59] = 80.01;      // 实测:syn_3cut.mp4 切点 2(相距 30 帧)
        var cuts = SceneCutJudge.Detect(diffs, null);
        Assert.Equal(new[] { 29, 59 }, cuts.ToArray());
    }

    /// <summary>②的边界:距离**正好等于**最小间距 → 必须保留(判据是 `>=`,不是 `>`)。</summary>
    [Theory]
    [InlineData(14, false)]   // 差一帧 → 被合并
    [InlineData(15, true)]    // 正好等于 → 保留
    [InlineData(16, true)]
    public void Boundary_gap_is_inclusive(int gap, bool secondKept)
    {
        var kept = SceneCutJudge.ApplyMinSceneLen(new[] { 10, 10 + gap });
        Assert.Equal(secondKept ? new[] { 10, 10 + gap } : new[] { 10 }, kept.ToArray());
    }

    /// <summary>③ 借道"闪光"用例:不再误判 2 处(并给出 A/B 对照,证明改的确实只是滞回)。</summary>
    [Fact]
    public void Flash_no_longer_reports_two_cuts()
    {
        var diffs = Enumerable.Repeat(1.0, 59).ToList();
        diffs[29] = 127.70;     // 实测值
        diffs[30] = 127.69;     // 实测值
        Assert.Single(SceneCutJudge.Detect(diffs, null));                                  // 现在:1 处
        Assert.Equal(2, SceneCutJudge.Detect(diffs, null, minSceneLen: 1).Count);           // 加滞回之前:2 处
    }

    /// <summary>④ `minSceneLen = 1` ⇒ 逐字等于"加滞回之前的行为"(不抑制)。这是本次改动的**对照基线**:
    /// 它保证了"行为差异只来自滞回",不是来自判据/阈值被动过。</summary>
    [Fact]
    public void Min_scene_len_one_reproduces_the_pre_hysteresis_behaviour()
    {
        var diffs = Enumerable.Repeat(1.0, 59).ToList();
        diffs[0] = 999.0; diffs[1] = 999.0; diffs[2] = 999.0; diffs[29] = 127.7; diffs[30] = 127.69; diffs[58] = 60.0;
        var raw = SceneCutJudge.Detect(diffs, null, minSceneLen: 1);
        // 逐对判据的结果一个不少(0,1,2,29,30,58),顺序升序
        Assert.Equal(new[] { 0, 1, 2, 29, 30, 58 }, raw.ToArray());
        // 默认(15 帧)下只剩:0(首处永远保留)、29、58(29→58 相距 29 帧)
        Assert.Equal(new[] { 0, 29, 58 }, SceneCutJudge.Detect(diffs, null).ToArray());
    }

    /// <summary>首处切点**永远保留**(与 PySceneDetect 的刻意差别:它会把"开场不足 min_scene_len"的切点也压掉,
    /// 我们不压 —— 我们的切点是保护点,压掉片头真切点 = 重新引入跨切鬼影帧;且既有契约已钉住
    /// "两帧之间真的出现巨大差异(999)时必须判切")。</summary>
    [Fact]
    public void The_first_cut_is_always_kept()
    {
        Assert.Equal(new[] { 0 }, SceneCutJudge.Detect(new[] { 999.0 }, null).ToArray());
        Assert.Equal(new[] { 0 }, SceneCutJudge.Detect(new[] { 999.0, 999.0, 999.0 }, null).ToArray());
        Assert.Equal(new[] { 0 }, SceneCutJudge.ApplyMinSceneLen(new[] { 0, 1, 2, 3 }));
    }

    /// <summary>逐帧交替的极端废片不再退化成"全拷贝":479 对全超阈值时,旧行为 479 处 → 现在 32 处
    /// (0,15,30,… 每 15 帧一处;479 对里最后一个下标是 478 ⇒ 0+15×31 = 465 ≤ 478 ⇒ 共 32 处)。
    /// 【为什么这条重要】479 处切点意味着几乎每一对都被强制拷贝 —— 补帧等于没做。</summary>
    [Fact]
    public void Frame_alternating_footage_no_longer_degrades_to_all_copies()
    {
        var diffs = Enumerable.Repeat(102.86, 479).ToList();     // 逐帧交替:每对帧差都远超强切档 50
        Assert.Equal(479, SceneCutJudge.Detect(diffs, null, minSceneLen: 1).Count);   // 加滞回之前:479 对判 479 处
        var cuts = SceneCutJudge.Detect(diffs, null);
        Assert.Equal(32, cuts.Count);                                                 // 现在:32 处
        Assert.Equal(0, cuts[0]);
        Assert.Equal(465, cuts[^1]);
        // 而且**确实是**等距 15 帧,没有漏掉/多出
        for (int k = 1; k < cuts.Count; k++) Assert.Equal(15, cuts[k] - cuts[k - 1]);
    }

    /// <summary>滞回只做"合并",不发明、不重排、不改变其它点:结果必须是输入的子序列(升序)。</summary>
    [Fact]
    public void Hysteresis_only_merges_never_invents_or_reorders()
    {
        var input = new[] { 3, 4, 20, 21, 22, 40, 100 };
        var kept = SceneCutJudge.ApplyMinSceneLen(input);
        Assert.Equal(new[] { 3, 20, 40, 100 }, kept.ToArray());
        int idx = 0;
        foreach (var c in kept)
        {
            while (input[idx] != c) idx++;     // 每个保留点都能在输入里按顺序找到
        }
        // 空输入/单点输入原样返回
        Assert.Empty(SceneCutJudge.ApplyMinSceneLen(Array.Empty<int>()));
        Assert.Equal(new[] { 7 }, SceneCutJudge.ApplyMinSceneLen(new[] { 7 }).ToArray());
    }

    /// <summary>间距大于素材长度 → 最多一处切点(极端兜底,不许越界/抛异常)。</summary>
    [Fact]
    public void Huge_min_scene_len_keeps_at_most_one_cut()
    {
        var diffs = Enumerable.Repeat(999.0, 100).ToList();
        Assert.Single(SceneCutJudge.Detect(diffs, null, minSceneLen: 100_000));
        Assert.Single(SceneCutJudge.ApplyMinSceneLen(new[] { 1, 2, 3 }, 100_000));
    }

    /// <summary>常量与外部依据一致性:默认值必须就是 PySceneDetect 记下来的那个 15
    /// (防止"改了常量却忘了同步 ExternalPractice 里的出处")。</summary>
    [Fact]
    public void Min_scene_len_matches_the_recorded_external_default()
    {
        Assert.Equal(15, SceneCutJudge.MinSceneLen);
        Assert.Equal(ExternalPractice.PySceneDetectMinSceneLenFramesDefault, SceneCutJudge.MinSceneLen);
        // 默认参数确实用的是这个常量(传 0 个间距参数 = 走默认)
        var diffs = Enumerable.Repeat(999.0, 100).ToList();
        Assert.Equal(SceneCutJudge.Detect(diffs, null, SceneCutJudge.MinSceneLen).Count,
                     SceneCutJudge.Detect(diffs, null).Count);
    }
}
