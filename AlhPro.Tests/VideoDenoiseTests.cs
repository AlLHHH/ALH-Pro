using AlhPro.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace AlhPro.Tests;

/// <summary>视频降噪滤镜链的单测(任务 M1:整体削弱、弱档大削弱)。
/// 【为什么必须有单测】旧实现的三个档位分散在三张 switch 表里(spatialFor / spatialOnly / temporal),
/// 「仅空间」那一维的两档与"结合"档不同步、越界值又落到"强" —— 结果就是**档位非单调**
/// (选"弱"不比"中"轻)。这里把三档 × 三种降噪方式全部钉死,并逐维验证单调性。
/// 【对用户的影响(有意为之)】老用户选「强」的处理强度整体变轻一档(空间研究窗 p7→p5、
/// 时间维 12:10:12:8→8:6:12:8),因为实测「强」已经过降噪(细节只剩一半)。</summary>
public class VideoDenoiseTests
{
    [Theory]
    [InlineData(1, "nlmeans=s=3:p=3:r=3,hqdn3d=2:1.5:3:2")]   // 弱:最轻
    [InlineData(2, "nlmeans=s=3:p=3:r=3,hqdn3d=4:3:6:4")]     // 中: = 旧【弱】档时间维
    [InlineData(3, "nlmeans=s=5:p=5:r=5,hqdn3d=8:6:12:8")]    // 强: = 旧【中】档整套 ← 老用户"强"变轻一档
    public void Combined_filter_is_pinned(int strength, string expected)
        => Assert.Equal(expected, VideoDenoise.Filter(strength, VideoDenoise.KindBoth));

    [Theory]
    [InlineData(1, "nlmeans=s=3:p=3:r=3")]
    [InlineData(2, "nlmeans=s=3:p=3:r=3")]
    [InlineData(3, "nlmeans=s=5:p=5:r=5")]
    public void Spatial_only_filter_is_pinned(int strength, string expected)
        => Assert.Equal(expected, VideoDenoise.Filter(strength, VideoDenoise.KindSpatialOnly));

    [Theory]
    [InlineData(1, "hqdn3d=2:1.5:3:2")]
    [InlineData(2, "hqdn3d=4:3:6:4")]
    [InlineData(3, "hqdn3d=8:6:12:8")]
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
    /// 【仅空间】的弱/中同为 s3p3r3 是**设计如此**(这次削弱主要落在时间维),故那一维只要求非递减。</summary>
    [Theory]
    [InlineData(VideoDenoise.KindBoth, true)]
    [InlineData(VideoDenoise.KindSpatialOnly, false)]
    [InlineData(VideoDenoise.KindTemporalOnly, true)]
    public void Strength_is_monotonic(int kind, bool weakToMidStrict)
    {
        var weak = Numbers(VideoDenoise.Filter(1, kind));
        var mid = Numbers(VideoDenoise.Filter(2, kind));
        var strong = Numbers(VideoDenoise.Filter(3, kind));
        AssertMonotonic(weak, mid, "弱", "中", kind, weakToMidStrict);
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
