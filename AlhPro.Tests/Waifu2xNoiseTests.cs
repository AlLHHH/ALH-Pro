using AlhPro.Core;
using System;
using Xunit;

namespace AlhPro.Tests;

/// <summary>waifu2x 模型自带降噪档(-n)映射的单测(任务 L)。
/// 【真机反馈】旧映射 `chosen >= 1 ? chosen : 2` 有两个问题:①"关"关不掉(未勾选仍下发 -n 2);
/// ②档位非单调(不勾=2、弱=1、中=2、强=3 —— 选"弱"反而比不勾更轻)。
/// 现在:关→-1、弱→0、中→1、强→2(严格递增);且 1x 档不得下发 -n -1
/// (真机实测 `waifu2x -n -1` 配 `-s 1` 必崩:exit -1073741819、输出 0 字节,三个模型全复现)。</summary>
public class Waifu2xNoiseTests
{
    [Theory]
    // UI 档位(0=关,1=弱,2=中,3=强) × 引擎倍率 → 期望的 -n
    [InlineData(0, 2, -1)]    // 关:真关(旧实现这里给 2)
    [InlineData(1, 2, 0)]     // 弱
    [InlineData(2, 2, 1)]     // 中
    [InlineData(3, 2, 2)]     // 强
    [InlineData(0, 4, -1)]
    [InlineData(3, 4, 2)]
    [InlineData(0, 1, 0)]     // 【1x 护栏】关 → 退最轻档 0,绝不给 -1
    [InlineData(1, 1, 0)]
    [InlineData(2, 1, 1)]
    [InlineData(3, 1, 2)]
    [InlineData(0, 0, 0)]     // 引擎倍率非法(0/负)按 1x 处理(防御)
    [InlineData(0, -3, 0)]
    public void Noise_level_mapping_is_pinned(int uiLevel, int engineScale, int expected)
    {
        Assert.Equal(expected, Waifu2x.NoiseLevelFor(uiLevel, engineScale));
    }

    [Fact]
    public void Levels_are_strictly_monotonic_where_the_engine_can_take_it()
    {
        // 引擎倍率 ≥2(能正常吃 -n -1)时:关 < 弱 < 中 < 强 严格递增 —— 这是用户要求的核心
        foreach (var scale in new[] { 2, 4, 8 })
        {
            int prev = int.MinValue;
            for (int lv = 0; lv <= 3; lv++)
            {
                int n = Waifu2x.NoiseLevelFor(lv, scale);
                Assert.True(n > prev, $"engineScale={scale} 档位 {lv} 的 -n({n}) 必须大于更轻档的 -n({prev})");
                prev = n;
            }
            Assert.Equal(-1, Waifu2x.NoiseLevelFor(0, scale));
            Assert.Equal(2, Waifu2x.NoiseLevelFor(3, scale));
        }
    }

    [Fact]
    public void One_x_never_gets_minus_one()
    {
        // 【硬约束】1x 下 -n -1 配 -s 1 必崩 → 任何档位都不许出现 -1(只是"非递减",不再严格递增)
        foreach (var scale in new[] { 0, 1, -5 })
            for (int lv = -3; lv <= 6; lv++)
                Assert.True(Waifu2x.NoiseLevelFor(lv, scale) != Waifu2x.NoiseOff,
                    $"engineScale={scale} 档位 {lv} 不得返回 -1(1x 崩溃护栏)");
        // 1x 下档位仍是非递减的(关=弱=0,中=1,强=2)
        Assert.Equal(0, Waifu2x.NoiseLevelFor(0, 1));
        Assert.Equal(0, Waifu2x.NoiseLevelFor(1, 1));
        Assert.Equal(1, Waifu2x.NoiseLevelFor(2, 1));
        Assert.Equal(2, Waifu2x.NoiseLevelFor(3, 1));
    }

    [Fact]
    public void Out_of_range_inputs_are_clamped_not_crashing()
    {
        // 档位越界:负数当"关"、≥3 当"强"
        Assert.Equal(Waifu2x.NoiseLevelFor(0, 2), Waifu2x.NoiseLevelFor(-7, 2));
        Assert.Equal(Waifu2x.NoiseLevelFor(3, 2), Waifu2x.NoiseLevelFor(99, 2));
        // 关 + 大倍率仍是真关(只有 1x 才退最轻档)
        Assert.Equal(-1, Waifu2x.NoiseLevelFor(0, 999));
        // 全组合都不抛异常,且值域恒在 [-1, 2]
        for (int lv = -5; lv <= 10; lv++)
            foreach (var scale in new[] { -1, 0, 1, 2, 3, 4, 8, 64 })
            {
                int n = Waifu2x.NoiseLevelFor(lv, scale);
                Assert.InRange(n, -1, 2);
            }
    }

    [Fact]
    public void Off_is_the_lightest_and_strong_is_the_heaviest()
    {
        // 语义自检:关(-1)必须严格轻于弱(0);强(2)是当前引擎支持的最重档
        Assert.True(Waifu2x.NoiseLevelFor(0, 2) < Waifu2x.NoiseLevelFor(1, 2));
        Assert.Equal(2, Waifu2x.NoiseLevelFor(3, 4));
        Assert.Equal(-1, Waifu2x.NoiseOff);
    }
}
