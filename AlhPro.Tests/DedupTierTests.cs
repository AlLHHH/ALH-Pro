using AlhPro.Core;
using System;
using Xunit;

namespace AlhPro.Tests;

/// <summary>去重「智能档」三档力度的单测(任务 M4)。
/// 【为什么必须有单测】三档力度(0.7/1.0/1.5)原来只写在 DetectDupFramesAdaptive 里,而**智能主路径
/// 到不了那个函数**(智能先走拍数识别+网格采样,识别不出就走回退),所以"选激进/保守没区别"是真的。
/// 现在力度系数与四个缩放公式统一放在 Core,由真正执行的回退路径调用 —— 这里钉死系数与严格单调方向:
/// 激进只会删得更多,保守只会删得更少。</summary>
public class DedupTierTests
{
    [Theory]
    [InlineData(0, 1.0)]    // 均衡(内置标定,不动)
    [InlineData(1, 1.5)]    // 激进
    [InlineData(2, 0.7)]    // 保守
    public void Force_is_pinned(int smartMode, double expected)
        => Assert.Equal(expected, DedupTier.Force(smartMode));

    [Theory]
    [InlineData(0, "均衡")]
    [InlineData(1, "激进")]
    [InlineData(2, "保守")]
    public void Name_matches_ui(int smartMode, string expected)
        => Assert.Equal(expected, DedupTier.Name(smartMode));

    /// <summary>三档缩放后的阈值必须两两不同 —— 这就是"三档产出不同"的前提(门槛若相同,路径再对也没区别)。</summary>
    [Fact]
    public void Three_tiers_produce_different_threshold_sets()
    {
        var sets = new System.Collections.Generic.List<string>();
        foreach (int mode in new[] { 0, 1, 2 })
        {
            double f = DedupTier.Force(mode);
            sets.Add(string.Join("|",
                DedupTier.ScaleSad(3.0, f).ToString("0.####"),
                DedupTier.ScaleSsim(0.97, f, mode).ToString("0.####"),
                DedupTier.ScaleProtect(0.30, f).ToString("0.####"),
                DedupTier.ScaleSegSad(5.0, f).ToString("0.####")));
        }
        Assert.Equal(3, new System.Collections.Generic.HashSet<string>(sets).Count);
    }

    /// <summary>SAD 快筛:力度越大阈值越宽 → 更多帧进入精验 → 删得只会更多。</summary>
    [Fact]
    public void Sad_threshold_grows_with_force()
    {
        double aggressive = DedupTier.ScaleSad(3.0, DedupTier.Force(1));
        double balanced = DedupTier.ScaleSad(3.0, DedupTier.Force(0));
        double conservative = DedupTier.ScaleSad(3.0, DedupTier.Force(2));
        Assert.Equal(4.5, aggressive, 6);
        Assert.Equal(3.0, balanced, 6);
        Assert.Equal(2.1, conservative, 6);
        Assert.True(aggressive > balanced && balanced > conservative);
    }

    /// <summary>保护闸:力度越大允许的"变化块占比"越大(更敢删);SSIM 门槛越低(更容易判重)。</summary>
    [Fact]
    public void Protect_and_ssim_move_in_delete_more_direction()
    {
        Assert.True(DedupTier.ScaleProtect(0.30, DedupTier.Force(1))
            > DedupTier.ScaleProtect(0.30, DedupTier.Force(0)));
        Assert.True(DedupTier.ScaleProtect(0.30, DedupTier.Force(0))
            > DedupTier.ScaleProtect(0.30, DedupTier.Force(2)));
        // SSIM 越小越容易判为重复 → 激进的 SSIM 门槛必须最低
        double sAggressive = DedupTier.ScaleSsim(0.97, DedupTier.Force(1), 1);
        double sBalanced = DedupTier.ScaleSsim(0.97, DedupTier.Force(0), 0);
        double sConservative = DedupTier.ScaleSsim(0.97, DedupTier.Force(2), 2);
        Assert.Equal(0.985, sAggressive, 6);   // 档位专属下限:激进不允许比 0.985 更松
        Assert.Equal(0.99, sBalanced, 6);
        Assert.Equal(0.995, sConservative, 6);
        Assert.True(sAggressive < sBalanced && sBalanced < sConservative);
        // 静止段合并阈值与 SAD 同向
        Assert.True(DedupTier.ScaleSegSad(5.0, DedupTier.Force(1))
            > DedupTier.ScaleSegSad(5.0, DedupTier.Force(0)));
        Assert.Equal(7.5, DedupTier.ScaleSegSad(5.0, DedupTier.Force(1)), 6);
        Assert.Equal(3.5, DedupTier.ScaleSegSad(5.0, DedupTier.Force(2)), 6);
    }

    /// <summary>钳位上下限必须与迁移前的 inline 公式一致(否则三档实际力度会被悄悄改掉)。</summary>
    [Theory]
    [InlineData(20.0, 1.5, 7.0)]    // ScaleSad 上限 7.0
    [InlineData(0.1, 0.7, 1.0)]     // ScaleSad 下限 1.0
    [InlineData(10.0, 0.7, 7.0)]
    public void Sad_clamps_like_before(double sadThr, double force, double expected)
        => Assert.Equal(expected, DedupTier.ScaleSad(sadThr, force), 6);

    [Theory]
    [InlineData(0.80, 1.5, 0.60)]   // ScaleProtect 上限 0.60
    [InlineData(0.01, 0.7, 0.05)]   // ScaleProtect 下限 0.05
    public void Protect_clamps_like_before(double protect, double force, double expected)
        => Assert.Equal(expected, DedupTier.ScaleProtect(protect, force), 6);

    [Theory]
    [InlineData(10.0, 1.5, 8.0)]    // ScaleSegSad 上限 8.0
    [InlineData(1.0, 0.7, 2.0)]     // ScaleSegSad 下限 2.0
    public void Seg_sad_clamps_like_before(double segSad, double force, double expected)
        => Assert.Equal(expected, DedupTier.ScaleSegSad(segSad, force), 6);
}
