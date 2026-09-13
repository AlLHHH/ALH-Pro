using AlhPro.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>批次编号与区间的单测(任务 J)。
/// 【真机 bug】超分批日志出现过「超分批 13/12(槽位 2640~2666)」与「超分批 2/12(槽位 0~239)」:
/// 实际只跑了 12 批(槽位区间连续覆盖 0~2666),编号却被读成了 13 —— 根因是日志在 async 任务里现算
/// `第 {bi+1}/{batchCount} 批`,而 `bi` 是 **for 循环变量**(被所有闭包共享,任务在 finally 里读到的是
/// 循环推进后甚至结束后的值)。修复方式:把编号在启动任务前用 `DescribeBatches` 定格成不可变清单,
/// 下面这些用例钉住这份清单的编号/区间契约。</summary>
public class BatchNumberingTests
{
    [Fact]
    public void Numbers_match_count_and_ranges_are_contiguous()
    {
        // 真机那条:2667 个槽位、每批 240 → 12 批,末批只有 27 帧
        var counts = new List<int>();
        for (int i = 0; i < 11; i++) counts.Add(240);
        counts.Add(27);
        var infos = RenderPolicy.DescribeBatches(counts);

        Assert.Equal(12, infos.Count);
        // 分子(批号)必须落在 1..分母(批数)内,且严格递增 —— 这正是"13/12"违反的契约
        for (int i = 0; i < infos.Count; i++)
        {
            Assert.Equal(i + 1, infos[i].Number);
            Assert.InRange(infos[i].Number, 1, infos.Count);
        }
        // 区间首尾相接、末批结束于最后一个槽位(0-based:2666)
        Assert.Equal(0, infos[0].StartSlot);
        for (int i = 1; i < infos.Count; i++)
            Assert.Equal(infos[i - 1].EndSlot + 1, infos[i].StartSlot);
        Assert.Equal(2667 - 1, infos[^1].EndSlot);
        Assert.Equal(2640, infos[^1].StartSlot);
        // 第 11 批(索引 10)从 2400 开始、到 2639 结束 —— 与真机日志里那两行的槽位区间一致
        Assert.Equal(2400, infos[10].StartSlot);
        Assert.Equal(2639, infos[10].EndSlot);
    }

    [Fact]
    public void Single_batch_covers_everything()
    {
        var infos = RenderPolicy.DescribeBatches(new[] { 72 });
        Assert.Single(infos);
        Assert.Equal(1, infos[0].Number);
        Assert.Equal(0, infos[0].StartSlot);
        Assert.Equal(71, infos[0].EndSlot);
    }

    [Fact]
    public void Empty_input_yields_no_batches()
    {
        Assert.Empty(RenderPolicy.DescribeBatches(Array.Empty<int>()));
        Assert.Empty(RenderPolicy.DescribeBatches(null!));
    }

    [Fact]
    public void Every_slot_is_covered_exactly_once()
    {
        // 覆盖性:把所有名义区间展开,必须恰好覆盖 0..总槽位数-1,不重不漏
        int[] counts = { 3, 1, 240, 27, 5 };
        var infos = RenderPolicy.DescribeBatches(counts);
        var covered = new List<int>();
        foreach (var b in infos)
            for (int s = b.StartSlot; s <= b.EndSlot; s++) covered.Add(s);
        Assert.Equal(counts.Sum(), covered.Count);
        Assert.Equal(Enumerable.Range(0, counts.Sum()).ToList(), covered);
    }

    [Fact]
    public void Batch_numbers_never_exceed_the_denominator_for_any_split()
    {
        // 扫一批切法:无论怎么切(含只剩 1 帧的尾批),分子都不得超过分母、编号不得跳号
        foreach (var per in new[] { 50, 200, 240, 400 })
            foreach (var total in new[] { 1, 27, 240, 241, 2667, 3420, 5000 })
            {
                var counts = new List<int>();
                int left = total;
                while (left > 0) { int take = Math.Min(per, left); counts.Add(take); left -= take; }
                var infos = RenderPolicy.DescribeBatches(counts);
                Assert.Equal(counts.Count, infos.Count);
                for (int i = 0; i < infos.Count; i++)
                {
                    Assert.Equal(i + 1, infos[i].Number);
                    Assert.True(infos[i].Number <= infos.Count, $"per={per} total={total}: 分子超过分母");
                }
                Assert.Equal(total - 1, infos[^1].EndSlot);
            }
    }
}
