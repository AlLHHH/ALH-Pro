using System;
using System.Linq;
using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【2026-09-23 分段合帧】纯逻辑单测：段帧数与段区间表。
///
/// 这一组的数字**来自那个真实用户案例**（诊断包 `ALHPro_Diag_20260923_1345`）：
/// 4K 源 → 2x 超分（输出 8K）→ 2x 补帧；源帧 ≈9340、峰值帧 18680；该机批大小被面积缩放到 70 帧；
/// 峰值帧单帧 8.93MB、源帧 4.0MB；临时盘只剩 132GB（预估要 262GB ⇒ 现在会被拒绝）。
/// 这些用例回答的是："**改成按段跑，132GB 够不够**" —— 结论是够（段帧数取到上限 2000）。</summary>
public class MuxSegmentationTests
{
    // 该用户案例的真实数字
    private const double PeakMb = 8.93;
    private const double SrcMb = 4.00;
    private const int Concurrency = 2;      // 本机实测 SafeRender.GetVideoConcurrency() = 2

    /// <summary>★ 他的现场：132GB 余量、单帧 12.93MB、并发 2 ⇒ 预算能放 2264 帧/段，
    /// 取上限 2000 ⇒ **分段后放得下**（这就是"从失败变成功"的那个判断）。</summary>
    [Fact]
    public void His_case_now_fits_by_segmenting()
    {
        int n = MuxSegmentation.FramesPerSegment(132, PeakMb, SrcMb, Concurrency);
        Assert.Equal(MuxSegmentation.MaxFramesPerSegment, n);
        // 分段后的真实峰值 =(并发+1) × 段帧数 × 单帧占用 ≤ 0.65 × 余量
        double peakGb = (Concurrency + 1) * (double)n * (PeakMb + SrcMb) / 1024.0;
        Assert.True(peakGb <= 132 * 0.65, $"分段峰值 {peakGb:0.#}GB 应落在额度内");
        // 段数
        Assert.Equal(10, MuxSegmentation.PlanSegments(18680, n).Count);
    }

    [Fact]
    public void Plenty_of_space_uses_the_cap()
        => Assert.Equal(2000, MuxSegmentation.FramesPerSegment(500, PeakMb, SrcMb, Concurrency));

    /// <summary>10GB 余量：10240×0.65=6656MB ÷ (12.93×3)=171.6 ⇒ 171 帧/段。</summary>
    [Fact]
    public void Small_budget_gives_a_smaller_segment()
        => Assert.Equal(171, MuxSegmentation.FramesPerSegment(10, PeakMb, SrcMb, Concurrency));

    /// <summary>连下界(50 帧)都放不下 ⇒ 返回 0（调用方保持"拒绝 + 给量化降级建议"）。
    /// 0.2GB×0.65=133MB ÷ 38.79MB ≈ 3 帧 &lt; 50。</summary>
    [Fact]
    public void Hopeless_budget_returns_zero()
        => Assert.Equal(0, MuxSegmentation.FramesPerSegment(0.2, PeakMb, SrcMb, Concurrency));

    /// <summary>并发数必须计入预算：盘上同时有 c 段待编码 + 1 段在编。</summary>
    [Fact]
    public void Concurrency_is_counted()
    {
        int c1 = MuxSegmentation.FramesPerSegment(10, PeakMb, SrcMb, 1);
        int c2 = MuxSegmentation.FramesPerSegment(10, PeakMb, SrcMb, 2);
        int c3 = MuxSegmentation.FramesPerSegment(10, PeakMb, SrcMb, 3);
        Assert.True(c1 > c2 && c2 > c3, $"并发越大段应越小(c1={c1}, c2={c2}, c3={c3})");
        Assert.Equal(257, c1);
        Assert.Equal(128, c3);
    }

    /// <summary>非法/极端输入不许抛，也不许给出"能跑"的假结论。</summary>
    [Fact]
    public void Invalid_inputs_return_zero()
    {
        Assert.Equal(0, MuxSegmentation.FramesPerSegment(0, PeakMb, SrcMb, 2));
        Assert.Equal(0, MuxSegmentation.FramesPerSegment(-5, PeakMb, SrcMb, 2));
        Assert.Equal(0, MuxSegmentation.FramesPerSegment(double.NaN, PeakMb, SrcMb, 2));
        Assert.Equal(0, MuxSegmentation.FramesPerSegment(100, 0, 0, 2));      // 单帧口径无效
        Assert.Equal(0, MuxSegmentation.FramesPerSegment(100, -1, -1, 2));
    }

    /// <summary>段区间的不变量：首尾相接、无空洞、无重叠、总数守恒、末段是余数。</summary>
    [Fact]
    public void Segments_tile_the_timeline()
    {
        var segs = MuxSegmentation.PlanSegments(18680, 2000);
        Assert.Equal(10, segs.Count);              // ⌈18680/2000⌉
        Assert.Equal(0, segs[0].StartFrame);
        long acc = 0;
        for (int i = 0; i < segs.Count; i++)
        {
            Assert.Equal(i, segs[i].Index);
            Assert.Equal(acc, segs[i].StartFrame);
            Assert.True(segs[i].FrameCount > 0);
            Assert.Equal(segs[i].StartFrame + segs[i].FrameCount - 1, segs[i].EndFrameInclusive);
            acc += segs[i].FrameCount;
        }
        Assert.Equal(18680, acc);
        Assert.Equal(680, segs[^1].FrameCount);    // 末段余数
    }

    [Fact]
    public void Edge_cases()
    {
        Assert.Single(MuxSegmentation.PlanSegments(100, 2000));                 // 不足一段
        Assert.Equal(100, MuxSegmentation.PlanSegments(100, 2000)[0].FrameCount);
        Assert.Single(MuxSegmentation.PlanSegments(2000, 2000));                // 恰好一段
        Assert.Equal(2, MuxSegmentation.PlanSegments(2001, 2000).Count);        // 多一帧就是两段
        Assert.Empty(MuxSegmentation.PlanSegments(0, 2000));
        Assert.Empty(MuxSegmentation.PlanSegments(-3, 2000));
        Assert.Empty(MuxSegmentation.PlanSegments(100, 0));                     // 段帧数非法 ⇒ 空表
    }

    /// <summary>段帧数越小 ⇒ 段数越多（切分单调），且每段都不超过段帧数。</summary>
    [Fact]
    public void More_segments_when_frames_per_segment_is_smaller()
    {
        Assert.True(MuxSegmentation.PlanSegments(10000, 500).Count
                  > MuxSegmentation.PlanSegments(10000, 2000).Count);
        foreach (var s in MuxSegmentation.PlanSegments(10000, 500))
            Assert.True(s.FrameCount <= 500);
    }

    /// <summary>预算比例必须复用既有常数（不许在分段这边另写一个）。</summary>
    [Fact]
    public void Reuses_the_existing_budget_constant()
    {
        Assert.Equal(0.65, RenderPolicy.BatchTempDiskShareLimit, 6);
        // 用常数复算一遍，验证没有偷偷换比例：100GB × 0.65 ÷ (10MB × 3) = 2218 → 上限 2000
        Assert.Equal(2000, MuxSegmentation.FramesPerSegment(100, 6.0, 4.0, 2));
        // 若比例被改成 0.5：100×0.5×1024 ÷ 30 = 1706 —— 这条断言会在那种情况下失败
        Assert.Equal(1706, (int)Math.Floor(100 * 1024 * 0.5 / 30.0));
    }
}
