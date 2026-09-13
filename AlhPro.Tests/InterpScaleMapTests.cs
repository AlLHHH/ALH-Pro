using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>
/// 补帧倍率映射的钉子。
/// 起因:预设摘要把"下拉序号"当"倍率"打印,「去重补帧4x」显示成 2x(序号 2),序号 0 会显示成 0x。
/// 这类错误在界面上"看起来只是文案不对",实际会让用户以为自己选错了档 —— 必须先钉住映射本身。
/// </summary>
public class InterpScaleMapTests
{
    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 3)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 12)]
    [InlineData(5, 16)]
    public void Each_dropdown_index_maps_to_the_advertised_multiplier(int index, int expected)
    {
        // 界面下拉依次是 2x/3x/4x/8x/12x/16x(六档),与这里必须逐项一致
        Assert.Equal(expected, InterpScaleMap.Multiplier(index));
    }

    [Theory]
    [InlineData(-1)]   // 下拉未选中(RadioButtons.SelectedIndex 为 -1)
    [InlineData(6)]    // 越界
    [InlineData(99)]
    public void Out_of_range_falls_back_to_2x(int index)
    {
        // 2x 是唯一所有补帧模型都支持的档,越界时按它兜底(与界面默认档一致)
        Assert.Equal(2, InterpScaleMap.Multiplier(index));
    }

    [Fact]
    public void Label_shows_the_multiplier_not_the_index()
    {
        // 回归点:此前摘要打印 d.InterpScale + "x" —— 序号 2(4x)显示成 "2x",序号 0(2x)显示成 "0x"
        Assert.Equal("4x", InterpScaleMap.Label(2));
        Assert.Equal("2x", InterpScaleMap.Label(0));
        Assert.Equal("16x", InterpScaleMap.Label(5));
    }

    [Fact]
    public void High_rate_warning_covers_16x_too()
    {
        // 回归点:原先写成 SelectedIndex is 3 or 4(8x/12x),漏了序号 5(16x)—— 最高倍率反而没有耗时提示
        Assert.False(InterpScaleMap.IsHighRate(0));   // 2x
        Assert.False(InterpScaleMap.IsHighRate(1));   // 3x
        Assert.False(InterpScaleMap.IsHighRate(2));   // 4x
        Assert.True(InterpScaleMap.IsHighRate(3));    // 8x
        Assert.True(InterpScaleMap.IsHighRate(4));    // 12x
        Assert.True(InterpScaleMap.IsHighRate(5));    // 16x
    }

    [Fact]
    public void Only_v4_only_rates_are_flagged()
    {
        // 3x / 12x / 16x 需 V4 架构模型;2x/4x/8x 可由 2 的幂级联实现,故不算
        Assert.True(InterpScaleMap.NeedsV4Model(1));    // 3x
        Assert.True(InterpScaleMap.NeedsV4Model(4));    // 12x
        Assert.True(InterpScaleMap.NeedsV4Model(5));    // 16x
        Assert.False(InterpScaleMap.NeedsV4Model(0));   // 2x
        Assert.False(InterpScaleMap.NeedsV4Model(2));   // 4x
        Assert.False(InterpScaleMap.NeedsV4Model(3));   // 8x
    }

    [Fact]
    public void Every_index_in_range_is_monotonic_and_distinct()
    {
        // 防"两档映射到同一倍率"这类抄写事故:六档必须严格递增且互不相同
        var seen = new System.Collections.Generic.HashSet<int>();
        int prev = 0;
        for (int i = 0; i <= 5; i++)
        {
            int m = InterpScaleMap.Multiplier(i);
            Assert.True(seen.Add(m), $"倍率 {m} 重复(序号 {i})");
            Assert.True(m > prev, $"序号 {i} 的倍率 {m} 未严格递增");
            prev = m;
        }
    }
}
