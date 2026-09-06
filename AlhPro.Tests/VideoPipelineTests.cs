using AlhPro.Core;
using System.Collections.Generic;
using Xunit;

namespace AlhPro.Tests;

/// <summary>视频管线纯函数单测(耗时估算/时长合并/VFR setpts)。</summary>
public class VideoPipelineTests
{
    // ---------- EstimateProcessSeconds ----------
    [Fact]
    public void Estimate_scales_with_resolution_and_interp()
    {
        // 720p 超分2x 无补帧
        double d720 = VideoPipeline.EstimateProcessSeconds(10, 30, 1280, 720, up: true, 2.0, "waifu2x", interp: false, 2, dedup: false, 0);
        // 4K 超分2x 无补帧:面积更大 → 更慢
        double d4k = VideoPipeline.EstimateProcessSeconds(10, 30, 3840, 2160, up: true, 2.0, "waifu2x", interp: false, 2, dedup: false, 0);
        Assert.True(d4k > d720, "4K 应比 720p 慢");
    }

    [Fact]
    public void Estimate_interp_increases_cost()
    {
        double noInterp = VideoPipeline.EstimateProcessSeconds(10, 30, 1280, 720, up: false, 1.0, "waifu2x", interp: false, 2, dedup: false, 0);
        double withInterp = VideoPipeline.EstimateProcessSeconds(10, 30, 1280, 720, up: false, 1.0, "waifu2x", interp: true, 2, dedup: false, 0);
        Assert.True(withInterp > noInterp, "补帧应更慢");
    }

    [Fact]
    public void Estimate_waifu2x_faster_than_realesrgan()
    {
        double waifu = VideoPipeline.EstimateProcessSeconds(10, 30, 1280, 720, up: true, 2.0, "waifu2x", interp: false, 2, dedup: false, 0);
        double esr = VideoPipeline.EstimateProcessSeconds(10, 30, 1280, 720, up: true, 2.0, "realesrgan", interp: false, 2, dedup: false, 0);
        Assert.True(waifu < esr, "waifu2x 单帧成本比 realesrgan 低");
    }

    [Fact]
    public void Estimate_weak_machine_slowdown()
    {
        double fast = VideoPipeline.EstimateProcessSeconds(10, 30, 1280, 720, up: true, 2.0, "waifu2x", interp: false, 2, dedup: false, 0, slowFactor: 1.0);
        double weak = VideoPipeline.EstimateProcessSeconds(10, 30, 1280, 720, up: true, 2.0, "waifu2x", interp: false, 2, dedup: false, 0, slowFactor: 6.0);
        Assert.True(weak > fast * 3, "弱机系数应显著放大估算");
    }

    // ---------- MergeDurations ----------
    [Fact]
    public void MergeDurations_merges_dropped_frames_into_previous_kept()
    {
        // 4 帧,各 1s;删第 2、3 帧 → 第1帧时长 = 1+1+1 = 3s,保留第4 帧
        var durs = new List<double> { 1.0, 1.0, 1.0, 1.0 };
        VideoPipeline.MergeDurations(durs, new[] { 2, 3 }, 4);
        Assert.Equal(3.0, durs[0], 5);   // 1+1+1
        Assert.Equal(1.0, durs[1], 5);   // 原第 4 帧
        Assert.Equal(2, durs.Count);
    }

    [Fact]
    public void MergeDurations_keeps_tail_protected_frame()
    {
        // 删尾帧(第4)→ 时长并入前面保留帧;但尾帧保护在调用方处理,这里只验基础合并
        var durs = new List<double> { 1.0, 1.0, 1.0, 1.0 };
        VideoPipeline.MergeDurations(durs, new[] { 4 }, 4);
        Assert.Equal(3, durs.Count);   // 删了 1 条
        Assert.Equal(2.0, durs[^1], 5); // 第3帧 + 第4帧
    }

    [Fact]
    public void MergeDurations_empty_drop_no_change()
    {
        var durs = new List<double> { 1.0, 2.0 };
        VideoPipeline.MergeDurations(durs, System.Array.Empty<int>(), 2);
        Assert.Equal(new List<double> { 1.0, 2.0 }, durs);
    }

    // ---------- BuildVfrSetptsExpr ----------
    [Fact]
    public void BuildVfrSetpts_expr_contains_setpts_and_tb()
    {
        var expr = VideoPipeline.BuildVfrSetptsExpr(new List<double> { 0.04, 0.04, 0.08 });
        Assert.NotNull(expr);
        Assert.StartsWith("setpts=(", expr);
        Assert.EndsWith(")/TB", expr);
    }

    [Fact]
    public void BuildVfrSetpts_too_many_segments_returns_null()
    {
        var many = new List<double>();
        for (int i = 0; i < 500; i++) many.Add(0.04 + (i % 10) * 0.001);   // 大量不同时长 → 段数>400
        Assert.Null(VideoPipeline.BuildVfrSetptsExpr(many));
    }

    [Fact]
    public void BuildVfrSetpts_empty_returns_null()
    {
        Assert.Null(VideoPipeline.BuildVfrSetptsExpr(new List<double>()));
    }
}
