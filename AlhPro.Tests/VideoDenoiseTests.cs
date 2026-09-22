using AlhPro.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace AlhPro.Tests;

/// <summary>视频降噪滤镜链的单测。
/// 【为什么必须有单测】旧实现的三个档位分散在三张 switch 表里(spatialFor / spatialOnly / temporal),
/// 「仅空间」那一维的两档与"结合"档不同步、越界值又落到"强" —— 结果就是**档位非单调**(选"弱"不比"中"轻)。
/// 这里把三档 × 三种降噪方式全部钉死,并逐维验证单调性。
/// 【2026-09-21 参数表加强】用户要求"降噪效果要可观" ⇒ 旧表**每一档都更弱**(旧弱/中档的空间参数还完全相同),
/// 实测"开了跟没开差不多"。新表的数字全部来自 `_qa\denoise_effect.py` 的实测(见 VideoDenoise 类注释),
/// 改动时**必须连那份实测一起改** —— 界面提示里的百分比就是从那儿来的。</summary>
public class VideoDenoiseTests
{
    [Theory]
    [InlineData(1, "nlmeans=s=3:p=3:r=3,hqdn3d=8:6:12:8")]      // 弱:实测降噪 23% / 细节 74% / 闪烁 0.709
    [InlineData(2, "nlmeans=s=4:p=4:r=4,hqdn3d=16:12:24:16")]   // 中:实测降噪 30% / 细节 72% / 闪烁 0.536
    [InlineData(3, "nlmeans=s=6:p=5:r=5,hqdn3d=24:18:36:24")]   // 强:实测降噪 39% / 细节 58% / 闪烁 0.446(代价最大的档)
    public void Combined_filter_is_pinned(int strength, string expected)
        => Assert.Equal(expected, VideoDenoise.Filter(strength, VideoDenoise.KindBoth));

    [Theory]
    [InlineData(1, "nlmeans=s=3:p=3:r=3")]   // 实测降噪 15% / 细节 74%
    [InlineData(2, "nlmeans=s=4:p=4:r=4")]   // 实测降噪 19% / 细节 74%
    [InlineData(3, "nlmeans=s=6:p=5:r=5")]   // 实测降噪 23% / 细节 61%
    public void Spatial_only_filter_is_pinned(int strength, string expected)
        => Assert.Equal(expected, VideoDenoise.Filter(strength, VideoDenoise.KindSpatialOnly));

    [Theory]
    [InlineData(1, "hqdn3d=8:6:12:8")]        // 实测降噪 14% / 细节 91% / 闪烁 0.741(有噪源 1.084)
    [InlineData(2, "hqdn3d=16:12:24:16")]     // 实测降噪 23% / 细节 84% / 闪烁 0.562
    [InlineData(3, "hqdn3d=24:18:36:24")]     // 实测降噪 29% / 细节 77% / 闪烁 0.467
    public void Temporal_only_filter_is_pinned(int strength, string expected)
        => Assert.Equal(expected, VideoDenoise.Filter(strength, VideoDenoise.KindTemporalOnly));

    /// <summary>三种降噪方式的【同一维】必须用同一张表:仅空间档 = 组合档的空间部分。</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Spatial_only_matches_combined_spatial_part(int strength)
    {
        string combined = VideoDenoise.Filter(strength, VideoDenoise.KindBoth);
        Assert.Equal(combined.Split(',')[0], VideoDenoise.Filter(strength, VideoDenoise.KindSpatialOnly));
        Assert.Equal(combined.Split(',')[1], VideoDenoise.Filter(strength, VideoDenoise.KindTemporalOnly));
    }

    /// <summary>档位必须单调:弱 ≤ 中 ≤ 强,且相邻两档至少有一处严格变大(nlmeans 的 s/p/r、hqdn3d 的四个数)。
    /// 【2026-09-21】三种方式现在**都**要求弱→中严格变大:旧表里"仅空间"的弱/中同为 s3p3r3
    /// (选哪档在空间降噪上一模一样)正是用户说"效果不可观"的原因之一,已修。</summary>
    [Theory]
    [InlineData(VideoDenoise.KindBoth)]
    [InlineData(VideoDenoise.KindSpatialOnly)]
    [InlineData(VideoDenoise.KindTemporalOnly)]
    public void Strength_is_monotonic(int kind)
    {
        var weak = Numbers(VideoDenoise.Filter(1, kind));
        var mid = Numbers(VideoDenoise.Filter(2, kind));
        var strong = Numbers(VideoDenoise.Filter(3, kind));
        AssertMonotonic(weak, mid, "弱", "中", kind, true);
        AssertMonotonic(mid, strong, "中", "强", kind, true);
        AssertMonotonic(weak, strong, "弱", "强", kind, true);
    }

    [Theory]
    [InlineData(0, 1)]     // 0 或负 = "关",根本不会调用滤镜;越界按最轻处理(防御性,不再落到"强")
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(99, 3)]
    public void Strength_is_clamped(int raw, int expected)
        => Assert.Equal(expected, VideoDenoise.StrengthOf(raw));

    [Theory]
    [InlineData(1, "弱")]
    [InlineData(2, "中")]
    [InlineData(3, "强")]
    public void Strength_name_matches_ui(int strength, string expected)
        => Assert.Equal(expected, VideoDenoise.StrengthName(strength));

    private static List<double> Numbers(string filter)
        => Regex.Matches(filter, @"(\d+(?:\.\d+)?)")
            .Select(m => double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .ToList();

    private static void AssertMonotonic(List<double> lower, List<double> higher, string lo, string hi, int kind, bool requireStrict)
    {
        Assert.Equal(lower.Count, higher.Count);
        bool strictlyLargerSomewhere = false;
        for (int i = 0; i < lower.Count; i++)
        {
            Assert.True(higher[i] >= lower[i], $"kind={kind} 「{hi}」第 {i} 个参数 {higher[i]} < 「{lo}」{lower[i]}(档位非单调)");
            if (higher[i] > lower[i]) strictlyLargerSomewhere = true;
        }
        if (requireStrict)
            Assert.True(strictlyLargerSomewhere, $"kind={kind} 「{lo}」与「{hi}」完全相同(档位没区分度)");
    }
}
