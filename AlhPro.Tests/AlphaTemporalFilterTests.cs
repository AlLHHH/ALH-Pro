using System;
using System.Linq;
using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>时序稳定(视频抠图治抖动的核心)契约(2026-09-22)。三条判据,对应设计 §五的三个机制:
/// 静止更稳 / 真运动少拖影 / 切点必须重置。这三条是"能不能拿出去用"的分水岭:
/// 不稳 ⇒ 边缘逐帧呼吸、像有虫在爬;太稳 ⇒ 运动拖出鬼影;不重置 ⇒ 切镜后整个镜头挂着上一个镜头的形状。
/// </summary>
public class AlphaTemporalFilterTests
{
    static float[] Frame(int n, float v) => Enumerable.Repeat(v, n).ToArray();

    [Fact]
    public void Static_noise_gets_suppressed()
    {
        var f = new AlphaTemporalFilter(stability: 80, w: 8, h: 1);
        var rnd = new Random(1234);
        float[] last = null!;
        for (int t = 0; t < 20; t++)
        {
            last = Frame(8, 0.5f).Select(v => v + (float)(rnd.NextDouble() - 0.5) * 0.2f).ToArray();
            f.Push(last);
        }
        float spread = last.Max() - last.Min();
        Assert.True(spread < 0.10f, $"静止序列被滤波后仍在抖:spread={spread:0.000}(判据 <0.10)");
    }

    [Fact]
    public void Real_motion_is_not_smeared()
    {
        var f = new AlphaTemporalFilter(stability: 80, w: 4, h: 1);
        f.Push(Frame(4, 0f));
        var cur = Frame(4, 1f);      // 突然整幅变实心 = 真运动/切换,滤波必须跟上
        f.Push(cur);
        Assert.True(cur.Min() > 0.5f, $"真运动被当成抖动抹掉了:min={cur.Min():0.00}(应 >0.5)");
    }

    [Fact]
    public void Reset_clears_history()
    {
        var f = new AlphaTemporalFilter(stability: 100, w: 4, h: 1);
        f.Push(Frame(4, 0f));
        f.Reset();
        var cur = Frame(4, 1f);
        f.Push(cur);
        Assert.Equal(1f, cur.Min(), 3);   // 重置后第一帧必须原样采用,不许掺旧历史
    }

    [Fact]
    public void Stability_zero_is_identity()
    {
        var f = new AlphaTemporalFilter(stability: 0, w: 4, h: 1);
        f.Push(Frame(4, 0f));
        var cur = Frame(4, 0.37f);
        f.Push(cur);
        Assert.Equal(0.37f, cur[0], 3);
    }

    [Fact]
    public void Scene_cut_detected_by_frame_difference()
    {
        var prev = Enumerable.Repeat((byte)10, 16).ToArray();
        var big  = Enumerable.Repeat((byte)200, 16).ToArray();
        Assert.True(VideoMatting.LooksLikeSceneCut(prev, big));
        Assert.False(VideoMatting.LooksLikeSceneCut(prev, Enumerable.Repeat((byte)12, 16).ToArray()));
    }

    // ---- 以下为执行计划时补的把关测试 ----

    /// <summary>【实心/全透明区不许被平滑】——这是"拖影"最容易出现的地方:
    /// 前景边缘已经移开,旧位置的实心区如果被慢慢衰减,画面就会拖出一条尾巴。
    /// 判据:从实心变透明必须【立刻】到位,不允许残留半透明。</summary>
    [Fact]
    public void Solid_region_follows_immediately_without_ghosting()
    {
        var f = new AlphaTemporalFilter(stability: 100, w: 3, h: 1);   // 最强稳定档,最容易被抓出鬼影
        f.Push(new[] { 1f, 1f, 1f });
        var cur = new[] { 0f, 1f, 0f };                               // 实心区突然只剩中间
        f.Push(cur);
        Assert.Equal(0f, cur[0], 3);
        Assert.Equal(0f, cur[2], 3);
        Assert.Equal(1f, cur[1], 3);
    }

    /// <summary>过渡带里的微小抖动要收敛到一个稳定值(不是原地抖、也不是继续漂)。</summary>
    [Fact]
    public void Transition_band_converges_to_a_stable_value()
    {
        var f = new AlphaTemporalFilter(stability: 60, w: 1, h: 1);
        var v = new[] { 0.5f };
        for (int t = 0; t < 30; t++)
        {
            v[0] = 0.5f + (t % 2 == 0 ? 0.02f : -0.02f);   // 交替抖动 ±0.02
            f.Push(v);
        }
        Assert.InRange(v[0], 0.46f, 0.54f);               // 收敛在中值附近,没有被推向某一侧
    }

    /// <summary>长度不足的帧不许把历史搞坏、也不许抛(长视频里单帧读失败很常见)。</summary>
    [Fact]
    public void Short_frame_is_ignored_without_corrupting_history()
    {
        var f = new AlphaTemporalFilter(stability: 80, w: 4, h: 1);
        f.Push(new[] { 0.5f, 0.5f, 0.5f, 0.5f });
        f.Push(new[] { 0.9f });                          // 太短 ⇒ 忽略
        var cur = new[] { 0.5f, 0.5f, 0.5f, 0.5f };
        f.Push(cur);
        Assert.All(cur, x => Assert.InRange(x, 0.4f, 0.6f));   // 历史仍是 0.5 附近,没被 0.9 带跑
    }

    [Fact]
    public void Scene_cut_handles_empty_and_short_inputs()
    {
        Assert.False(VideoMatting.LooksLikeSceneCut(null!, null!));
        Assert.False(VideoMatting.LooksLikeSceneCut(new byte[0], new byte[0]));
        // 长度不同时按【重叠前缀】比(调用方两帧本就同尺寸,这里只保证不越界、不抛):
        Assert.False(VideoMatting.LooksLikeSceneCut(new byte[] { 0, 0 }, new byte[] { 10 }));    // 前缀差异小 ⇒ 不是切点
        Assert.True(VideoMatting.LooksLikeSceneCut(new byte[] { 0, 0 }, new byte[] { 255 }));    // 前缀差异大 ⇒ 是切点
    }

    /// <summary>稳定性越高,静止噪声抑制得越好(单调性:滑条不是摆设)。</summary>
    [Fact]
    public void Higher_stability_means_less_residual_noise()
    {
        float Residual(int stability)
        {
            var f = new AlphaTemporalFilter(stability, 16, 1);
            var rnd = new Random(99);
            float[] last = null!;
            for (int t = 0; t < 25; t++)
            {
                last = Frame(16, 0.5f).Select(v => v + (float)(rnd.NextDouble() - 0.5) * 0.2f).ToArray();
                f.Push(last);
            }
            return last.Max() - last.Min();
        }
        float low = Residual(30), high = Residual(90);
        Assert.True(high <= low + 0.02f, $"稳定档 90 的残余抖动({high:0.000})不该比 30({low:0.000})还大");
    }
}
