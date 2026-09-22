using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>「处理中的当前帧」通道(逐帧预览面板的数据源)。
/// 【为什么值得单测】它被处理线程**高频写**、被 UI 线程**低频读**,而且刻意不加锁;
/// 这里钉住三条最容易写错的性质:①写入即发布(读到的是最后一次完整写入)②Reset 能清空
/// ③序号单调递增(界面靠它判断"有没有新帧",不递增会导致面板卡住不刷新)。</summary>
public class FrameFeedTests
{
    [Fact]
    public void Report_publishes_the_latest_frame_and_bumps_the_sequence()
    {
        FrameFeed.Reset();
        long s0 = FrameFeed.Sequence;
        FrameFeed.Report("超分", 12, 3, @"C:\in\f_0012.jpg", @"C:\out\f_0012.jpg");
        Assert.Equal(s0 + 1, FrameFeed.Sequence);
        var f = FrameFeed.Latest;
        Assert.Equal("超分", f.Stage);
        Assert.Equal(12, f.OutIndex);
        Assert.Equal(@"C:\in\f_0012.jpg", f.SrcPath);
        Assert.Equal(@"C:\out\f_0012.jpg", f.OutPath);
    }

    [Fact]
    public void Reset_clears_the_frame_but_still_bumps_the_sequence()
    {
        FrameFeed.Report("超分", 5, 1, "a.jpg", "b.jpg");
        long s1 = FrameFeed.Sequence;
        FrameFeed.Reset();
        Assert.Equal(s1 + 1, FrameFeed.Sequence);      // 序号必须前进:否则界面会以为"还是同一帧"而不刷新
        Assert.Null(FrameFeed.Latest.SrcPath);
        Assert.Null(FrameFeed.Latest.OutPath);
        Assert.Equal(0, FrameFeed.Latest.OutIndex);
    }

    /// <summary>只报路径、不解码:这是"对处理速度无可测影响"的前提 —— 传 null 路径必须被原样接受。</summary>
    [Fact]
    public void Null_paths_are_accepted()
    {
        FrameFeed.Report("超分", 7, 0, null, null);
        Assert.Null(FrameFeed.Latest.OutPath);
        Assert.Equal(7, FrameFeed.Latest.OutIndex);
    }
}
