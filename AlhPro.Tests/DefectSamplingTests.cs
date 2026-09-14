using AlhPro.Core;
using System;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>黑帧抽样计划(F2)的单测。
/// 这是"黑帧会不会漏检"的判定基础:真机事故就是"抽样太薄(整段只抽前 6 帧)"导致黑帧当成功交付。
/// 口径要点:首尾必查、均匀分散、严格递增不重复、上下限夹紧、总量小的时候不超过总帧数。</summary>
public class DefectSamplingTests
{
    [Fact]
    public void SampleCount_zero_or_negative_is_zero()
    {
        Assert.Equal(0, DefectSampling.SampleCount(0));
        Assert.Equal(0, DefectSampling.SampleCount(-5));
    }

    [Fact]
    public void SampleCount_tiny_totals_return_everything()
    {
        Assert.Equal(1, DefectSampling.SampleCount(1));
        Assert.Equal(8, DefectSampling.SampleCount(8));      // 恰好等于下限
    }

    [Fact]
    public void SampleCount_respects_min_and_max()
    {
        Assert.Equal(DefectSampling.MinSamples, DefectSampling.SampleCount(100));       // 100/32=4 → 抬到下限 8
        Assert.Equal(DefectSampling.MaxSamples, DefectSampling.SampleCount(20_000));    // 625 → 压到上限 48
        Assert.Equal(DefectSampling.MaxSamples, DefectSampling.SampleCount(int.MaxValue));
    }

    [Fact]
    public void SampleCount_proportional_between_limits()
    {
        // 32 帧制:每 32 帧一抽(向上取整),夹在 [8,48] 之间
        Assert.Equal(40, DefectSampling.SampleCount(32 * 40));    // 1280/32=40,未触上限
        Assert.Equal(48, DefectSampling.SampleCount(32 * 48 + 1)); // 触上限
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(9)]
    [InlineData(100)]
    [InlineData(15000)]
    public void Plan_always_has_head_and_tail_and_is_strictly_increasing(int total)
    {
        var idx = DefectSampling.Plan(total);
        Assert.Equal(DefectSampling.SampleCount(total), idx.Length);
        Assert.Equal(0, idx[0]);                    // 首帧必查
        Assert.Equal(total - 1, idx[^1]);           // 末帧必查
        for (int k = 1; k < idx.Length; k++)
            Assert.True(idx[k] > idx[k - 1], $"下标必须严格递增:{k} 处 {idx[k - 1]} → {idx[k]}");
        Assert.True(idx[^1] < total);               // 不越界
    }

    [Fact]
    public void Plan_never_returns_more_indices_than_frames()
    {
        var idx = DefectSampling.Plan(3);
        Assert.Equal(3, idx.Length);
        Assert.Equal(new[] { 0, 1, 2 }, idx);
    }

    [Fact]
    public void Indices_count_one_returns_first_frame_only()
    {
        Assert.Equal(new[] { 0 }, DefectSampling.Indices(500, 1));
    }

    [Fact]
    public void Indices_count_clamped_to_total()
    {
        // 请求 48 个但只有 5 帧:不许重复下标,也不许越界
        var idx = DefectSampling.Indices(5, 48);
        Assert.Equal(5, idx.Length);
        Assert.Equal(5, idx.Distinct().Count());
    }

    [Fact]
    public void Plan_discrete_15k_layout_is_evenly_spread()
    {
        // 15000 帧 / 48 个采样点:相邻间隔应均匀(±1),这是"均匀分散抽样"的可验证形态
        var idx = DefectSampling.Plan(15000);
        int maxGap = 0, minGap = int.MaxValue;
        for (int k = 1; k < idx.Length; k++)
        {
            int gap = idx[k] - idx[k - 1];
            if (gap > maxGap) maxGap = gap;
            if (gap < minGap) minGap = gap;
        }
        Assert.True(maxGap - minGap <= 1, $"间隔应均匀:min={minGap},max={maxGap}");
        // 与上限口径对齐:47 个间隔覆盖 14999 帧 → 每个间隔 319~320 帧
        // (旧口径只抽前 6 帧 = 第 6 帧之后的黑片全漏)
        int evenGap = (15000 - 1) / (DefectSampling.MaxSamples - 1) + 1;
        Assert.True(maxGap <= evenGap, $"间隔应 ≈ 总量/采样数:{maxGap} > {evenGap}");
    }

    [Fact]
    public void Describe_reports_sample_and_hit_counts()
    {
        string s = DefectSampling.Describe(15000, 3);
        Assert.Contains("48/15000", s);
        Assert.Contains("命中缺陷 3 帧", s);
    }
}
