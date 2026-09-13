using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>补帧入参预检的单测(任务 O2)。
/// 【真机依据】7680×4320 + `-n 119` → **117/119 帧全黑**、exit=0 无报错;1080p/2160p 同命令 **0 黑帧**;
/// 另:1 帧目录 + `-n 2` → **0xC0000005 崩溃**。</summary>
public class InterpSizePolicyTests
{
    [Theory]
    [InlineData(1920, 1080)]   // 实测安全
    [InlineData(3840, 2160)]   // 实测安全(8.29 Mpx,0 黑帧)
    [InlineData(2560, 1440)]
    public void Safe_sizes_pass_untouched(int w, int h)
    {
        var v = InterpSizePolicy.JudgeInputSize(w, h);
        Assert.False(v.RefuseNcnn);
        Assert.False(v.Warn);
        Assert.Equal("", v.Reason);
    }

    [Theory]
    [InlineData(7680, 4320)]   // 实测故障点:117/119 全黑
    [InlineData(8192, 4320)]
    [InlineData(7680, 8192)]
    public void Fault_band_refuses_ncnn(int w, int h)
    {
        var v = InterpSizePolicy.JudgeInputSize(w, h);
        Assert.True(v.RefuseNcnn);
        Assert.True(v.Warn);
        Assert.Contains("全黑", v.Reason);
        Assert.Contains("7680×4320", v.Reason);   // 依据必须写在理由里(用户/日志能追溯)
    }

    /// <summary>中间带(超过实测安全带、未到实测故障带)只警告、不拒跑 —— 没有实测数据就不擅自拒跑。</summary>
    [Theory]
    [InlineData(5120, 2880)]   // 14.7 Mpx
    [InlineData(6144, 3456)]   // 21.2 Mpx
    public void Middle_band_only_warns(int w, int h)
    {
        var v = InterpSizePolicy.JudgeInputSize(w, h);
        Assert.False(v.RefuseNcnn);
        Assert.True(v.Warn);
        Assert.Contains("待实测标定", v.Reason);
    }

    [Theory]
    [InlineData(0, 1080)]
    [InlineData(1920, 0)]
    [InlineData(-1, -1)]
    public void Invalid_size_is_not_refused(int w, int h)
    {
        var v = InterpSizePolicy.JudgeInputSize(w, h);
        Assert.False(v.RefuseNcnn);
        Assert.False(v.Warn);
    }

    /// <summary>退化入参:RIFE 至少 2 帧输入,且目标帧数必须大于输入帧数(1 帧目录 + -n 2 = 崩)。</summary>
    [Theory]
    [InlineData(1, 2, true)]    // 实测崩溃组合
    [InlineData(1, 1, true)]
    [InlineData(2, 2, true)]    // 目标 = 输入:没有可插的位置
    [InlineData(2, 3, false)]   // 最小可用
    [InlineData(2, 4, false)]
    [InlineData(855, 1709, false)]
    [InlineData(0, 1, true)]
    public void Degenerate_input_is_rejected(int segFrames, int target, bool expected)
        => Assert.Equal(expected, InterpSizePolicy.IsDegenerateSegmentInput(segFrames, target));

    /// <summary>可达性核对(纯逻辑复述生产代码的两个下限):
    /// 生产里单帧段走"直接复制不进引擎",且 `-n = Math.Max(segLen + 1, (segLen-1)×mult+1)`;
    /// 用这条规则枚举所有 segLen ≥ 2 的组合,必须**永远不**构成退化入参 —— 即"1 帧 + -n 2"崩不了生产。</summary>
    [Fact]
    public void Production_rule_never_yields_a_degenerate_input()
    {
        for (int segLen = 2; segLen <= 40; segLen++)
            foreach (int mult in new[] { 1, 2, 3, 4, 6 })
                foreach (bool last in new[] { true, false })
                {
                    int target = System.Math.Max(segLen + 1, VideoPipeline.InterpSegmentTarget(segLen, mult, last));
                    Assert.False(InterpSizePolicy.IsDegenerateSegmentInput(segLen, target),
                        $"segLen={segLen} mult={mult} last={last} → -n {target} 会崩");
                }
    }
}
