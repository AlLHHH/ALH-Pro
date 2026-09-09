using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>
/// 补帧输出帧号分配纯逻辑。并行补帧必须保证帧号【精确连续、不重不漏】,否则合帧缺号/乱序 → 整段视频损坏。
/// 这里验证 ComputeLayout 与串行手算一致,且相邻帧对起始帧号严格 +帧数 递增(证明"连续")。
/// </summary>
public class InterpFramingTests
{
    [Fact]
    public void Layout_target5_pairs2_is_two_pairs_of_two()
    {
        // 串行手算:target=5,pairs=2 → (4/2)-1=1,mids=1(两对都+1) → 每对=左端点1+中间1=2
        var (per, total) = InterpFraming.ComputeLayout(5, 2);
        Assert.Equal(new[] { 2, 2 }, per);
        Assert.Equal(4, total);   // 不含端帧 files[^1];最终 = total+1 = 5 = target ✓
    }

    [Fact]
    public void Layout_target8_pairs3_extra_mid_goes_to_first_pair()
    {
        // (8-1)/3=2 → mids=1;(8-1)%3=1 → 只有 p<1(即第0对) mids=2 → 3帧;其余两对 mids=1 → 2帧
        var (per, total) = InterpFraming.ComputeLayout(8, 3);
        Assert.Equal(new[] { 3, 2, 2 }, per);
        Assert.Equal(7, total);   // +端帧1 = 8 = target ✓
    }

    [Fact]
    public void StartIndexes_are_continuous_and_gapless()
    {
        // 关键不变量:每帧对起始帧号 = 前一对起始 + 前一对帧数(连续、无缺号),且首对从 1 起。
        var (per, _) = InterpFraming.ComputeLayout(20, 5);
        int idx = 1;
        for (int p = 0; p < per.Length; p++)
        {
            Assert.Equal(idx, InterpFraming.StartIndex(p, per));   // 第 p 对从预期号开始
            idx += per[p];   // 下一对从这一对之后紧接
        }
        // 最后一对结束号 + 1 = 总帧数(端帧在 idx,连续)
        Assert.Equal(idx, InterpFraming.StartIndex(per.Length, per) + 0);   // 语义:整个序列最后帧号 = 各对帧数和 + 1(端帧)
    }

    [Fact]
    public void TotalFrames_matches_serial_hand_calc()
    {
        foreach (var (target, pairs) in new[] { (5, 2), (8, 3), (20, 5), (100, 37), (5079, 2540) })
        {
            var (per, total) = InterpFraming.ComputeLayout(target, pairs);
            Assert.Equal(target, total + 1);   // 最终 = 各对帧数和 + 1 个端帧 = target
            Assert.Equal(pairs, per.Length);
        }
    }
}
