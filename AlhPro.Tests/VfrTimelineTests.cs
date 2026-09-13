using AlhPro.Core;
using System;
using System.Collections.Generic;
using Xunit;

namespace AlhPro.Tests;

/// <summary>「可变帧率(VFR)时间轴」判定的单元测试 —— 对应 2026-09-13 的真机事故:
/// 智能去重"未采用拍数识别"分支【无条件】把 frameDurs 置 null(即使素材是 VFR),
/// 合帧只好静默造一张均匀表 → 成片退化成纯 CFR(变速 + 画面相对声音最大滞后 684ms),
/// 而日志照打「时长保护(VFR) … vfrSetpts=有」。
/// 这里把"建不建表""有没有真的用上表""帧间隔算不算 VFR"三件事钉死,防止同类静默退化复发。</summary>
public class VfrTimelineTests
{
    // ---------- ① 拆帧阶段:要不要建"源帧时长表"(C1 修复的核心判据) ----------
    [Theory]
    [InlineData(false, true, true)]    // 【C1】VFR 素材:必须建表(旧代码在这里无条件丢掉 → 事故)
    [InlineData(true, false, true)]    // 开着去重:必须建表(删帧后要靠表把时长并回保留帧)
    [InlineData(true, true, true)]     // 两者都开
    [InlineData(false, false, false)]  // 普通 CFR 且不去重:不建表(省一遍全片解码)
    public void NeedsFrameDurations_decides_whether_to_build_source_table(bool dedup, bool vfr, bool expected)
    {
        Assert.Equal(expected, VideoPipeline.NeedsFrameDurations(dedup, vfr));
    }

    // ---------- ② 合帧阶段:有没有真的用上 VFR 时间轴 ----------
    [Fact]
    public void UsesVfrTimeline_requires_both_rhythm_need_and_a_real_table()
    {
        Assert.True(VideoPipeline.UsesVfrTimeline(true, 3420));
        Assert.False(VideoPipeline.UsesVfrTimeline(false, 3420));   // 不需要保节奏:均匀输出是正常的
        Assert.False(VideoPipeline.UsesVfrTimeline(true, 0));       // ← 事故路径:需要保节奏但没有表 = 静默回退
        Assert.False(VideoPipeline.UsesVfrTimeline(true, -1));      // 负值 = "没有表"的哨兵口径
    }

    /// <summary>事故回归:C1 修复前,VFR 素材(855 帧/29s)在"未采用拍数"分支被丢掉时长表,
    /// 于是 preserveRhythm=true 但表为空 → 回退均匀时间轴(成片全部等距)。修复后必须走 VFR 时间轴。
    /// 【这正是"必须真机重跑才能确认"之外、能在单测里钉住的那一半】。</summary>
    [Fact]
    public void Regression_vfr_source_must_not_silently_fall_back_to_uniform()
    {
        bool vfrPassthrough = true, dedup = true;
        int sourceFrames = 855;                       // 实测:819×1/30 + 35×2/30
        // 修复后的链路:建表 → preserveRhythm 成立 → 真的用上 VFR 时间轴
        bool buildTable = VideoPipeline.NeedsFrameDurations(dedup, vfrPassthrough);
        Assert.True(buildTable);
        int frameDursCount = buildTable ? sourceFrames : 0;          // 一帧不删 → 表长 == 源帧数
        bool preserveRhythm = vfrPassthrough
            || (dedup && frameDursCount > 0 && frameDursCount == sourceFrames);
        Assert.True(preserveRhythm);
        Assert.True(VideoPipeline.UsesVfrTimeline(preserveRhythm, frameDursCount));
        // 旧代码的状态(frameDurs 被无条件清空)必须被判成"回退" —— 调用方必须因此打 warn + 改文案
        Assert.False(VideoPipeline.UsesVfrTimeline(preserveRhythm, 0));
    }

    // ---------- ③ 帧间隔(VFR)判定(原 ProbeVfrAsync 抽查分支抽出的纯逻辑) ----------
    private static List<double> CfrGaps(int n, double fps = 30)
    {
        var g = new List<double>();
        for (int i = 0; i < n; i++) g.Add(1.0 / fps);
        return g;
    }

    [Fact]
    public void Gap_analysis_says_no_for_uniform_cfr()
    {
        var st = VideoPipeline.AnalyzeFrameGaps(CfrGaps(59));
        Assert.False(st.IsVfr);
        Assert.Equal(1.0, st.MaxOverMin, 3);
        Assert.True(st.Cv < 0.01);
        Assert.Contains("CFR", st.Summary);
    }

    /// <summary>真机素材形态:855 帧里散落 35 个"双倍长"间隔(手机可变帧率)。
    /// 取前 60 帧窗口(ProbeVfrAsync 的抽查口径)时必须能命中 —— 且理由来自 max/min 比值。</summary>
    [Fact]
    public void Gap_analysis_detects_scattered_double_length_gaps()
    {
        var gaps = new List<double>();
        for (int i = 0; i < 59; i++) gaps.Add(i % 3 == 0 ? 2.0 / 30 : 1.0 / 30);
        var st = VideoPipeline.AnalyzeFrameGaps(gaps);
        Assert.True(st.IsVfr);
        Assert.True(st.VfrByRatio);
        Assert.Equal(2.0, st.MaxOverMin, 3);
        Assert.Contains("VFR", st.Summary);
    }

    /// <summary>普通手机 CFR 的轻微抖动(r/avg 只有 1.04 那种):不能判成 VFR ——
    /// 否则会把基本均匀的素材也改走 VFR 时间轴(旧注释里明确要避免的行为)。</summary>
    [Fact]
    public void Gap_analysis_tolerates_small_jitter()
    {
        var gaps = new List<double>();
        for (int i = 0; i < 59; i++) gaps.Add((1.0 / 30) * (i % 2 == 0 ? 1.0 : 1.03));
        var st = VideoPipeline.AnalyzeFrameGaps(gaps);
        Assert.False(st.IsVfr);
        Assert.True(st.MaxOverMin < 1.5);
        Assert.True(st.Cv < 0.25);
    }

    [Fact]
    public void Gap_analysis_cv_route_only_matters_below_the_noise_floor()
    {
        // 【口径事实(本测试实测钉住,不是推测)】max/min ≤ 1.5 时 cv 恒 ≤ 0.25 —— 有界分布的性质:
        // cv ≤ (M−m)/(2m) = (r−1)/2;要让 cv > 0.25 必须 r > 1.5。所以两路判据【并不独立】:
        // ≥0.5ms 的形态一定由 max/min 判据先命中,cv 判据只在"间隔小到被 0.5ms 噪声门限挡掉"时单独生效。
        // 写下来是为了防止以后有人以为"cv 路还能兜住别的形态"而据此改判定。
        var tiny = new List<double>();
        for (int i = 0; i < 40; i++) tiny.Add(i % 2 == 0 ? 0.0001 : 0.0002);
        var st = VideoPipeline.AnalyzeFrameGaps(tiny);
        Assert.False(st.VfrByRatio);    // maxG = 0.2ms < 0.5ms 噪声门限 → 比值判据被抑制
        Assert.True(st.VfrByCv);
        Assert.True(st.IsVfr);
    }

    [Fact]
    public void Gap_analysis_needs_enough_samples()
    {
        Assert.False(VideoPipeline.AnalyzeFrameGaps(CfrGaps(6)).IsVfr);                  // 6 个样本:判不了
        Assert.Equal(6, VideoPipeline.AnalyzeFrameGaps(CfrGaps(6)).Count);
        Assert.False(VideoPipeline.AnalyzeFrameGaps(new List<double>()).IsVfr);
        Assert.False(VideoPipeline.AnalyzeFrameGaps(null!).IsVfr);
    }

    [Fact]
    public void Gap_analysis_ignores_non_positive_and_non_finite_gaps()
    {
        // 调用点会先过滤 g>0;这里再兜一层:0/NaN/负值不得把判定带偏(0 会让 max/min 变成 Inf)
        var gaps = new List<double> { 0, double.NaN, -0.01, double.PositiveInfinity };
        gaps.AddRange(CfrGaps(59));
        var st = VideoPipeline.AnalyzeFrameGaps(gaps);
        Assert.Equal(59, st.Count);
        Assert.False(st.IsVfr);
    }

    [Fact]
    public void Gap_analysis_summary_carries_the_raw_numbers()
    {
        // 摘要必须带 min/max/均值/cv(漏判时也能复盘),这是"静默必须变成吵闹"的一部分
        var st = VideoPipeline.AnalyzeFrameGaps(new List<double> { 0.0333, 0.0666, 0.0333, 0.0666, 0.0333, 0.0666, 0.0333 });
        Assert.Contains("min ", st.Summary);
        Assert.Contains("max ", st.Summary);
        Assert.Contains("cv ", st.Summary);
        Assert.Contains("VFR", st.Summary);
    }
}
