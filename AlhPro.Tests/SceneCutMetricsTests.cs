using AlhPro.Core;
using System;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【任务 S2 · 生产接线】场景硬切检测的**度量**那半步(SceneCutMetrics)。
/// 判据本身(SceneCutJudge)在 SceneCutTests 里按用户实测量级钉住;这里钉的是"喂给判据的数字怎么来的":
/// 采样尺寸口径、帧差、拉普拉斯能量、以及"坏输入不许抛异常"(判切失败只等于"不保护",不能拖垮处理)。</summary>
public class SceneCutMetricsTests
{
    [Fact]
    public void Sample_size_keeps_aspect_ratio_and_caps_height()
    {
        var (w, h) = SceneCutMetrics.SampleSize(1920, 1080);
        Assert.Equal(SceneCutMetrics.SampleHeight, h);                 // 1080p → 降到 192 行
        Assert.Equal(341, w);                                          // round(1920 × 192 / 1080)
    }

    [Fact]
    public void Sample_size_never_upscales_and_rejects_bad_input()
    {
        var (w, h) = SceneCutMetrics.SampleSize(320, 180);             // 源本来就比采样高度矮
        Assert.Equal((320, 180), (w, h));
        Assert.Equal((0, 0), SceneCutMetrics.SampleSize(0, 1080));
        Assert.Equal((0, 0), SceneCutMetrics.SampleSize(1920, -1));
        Assert.Equal((0, 0), SceneCutMetrics.SampleSize(1920, 1080, 0));
    }

    [Fact]
    public void Portrait_source_keeps_aspect_ratio()
    {
        var (w, h) = SceneCutMetrics.SampleSize(1080, 1920);
        Assert.Equal(SceneCutMetrics.SampleHeight, h);
        Assert.Equal(108, w);                                          // round(1080 × 192 / 1920)
    }

    [Fact]
    public void Mean_abs_diff_is_zero_for_identical_and_scales_with_offset()
    {
        var a = new byte[64];
        var b = new byte[64];
        for (int i = 0; i < b.Length; i++) b[i] = 100;
        Assert.Equal(0, SceneCutMetrics.MeanAbsDiff(a, a), 6);
        Assert.Equal(100, SceneCutMetrics.MeanAbsDiff(a, b), 6);
        // 长度不等时按较短者算(防御:宁可少比,也不越界)
        var c = new byte[32];
        for (int i = 0; i < c.Length; i++) c[i] = 100;
        Assert.Equal(100, SceneCutMetrics.MeanAbsDiff(a, c), 6);
    }

    [Fact]
    public void Lap_var_is_zero_on_flat_and_linear_ramp_and_positive_on_structure()
    {
        const int w = 9, h = 9;
        var flat = new byte[w * h];
        Assert.Equal(0, SceneCutMetrics.LapVar(flat, w, h)!.Value, 6);

        // 线性斜坡:4x−(x−1)−(x+1)−x−x = 0 → 拉普拉斯响应恒 0
        var ramp = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) ramp[y * w + x] = (byte)(x * 10);
        Assert.Equal(0, SceneCutMetrics.LapVar(ramp, w, h)!.Value, 6);

        // 棋盘:结构能量大
        var checker = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) checker[y * w + x] = (byte)(((x + y) % 2 == 0) ? 255 : 0);
        Assert.True(SceneCutMetrics.LapVar(checker, w, h) > 1000);
    }

    [Fact]
    public void Lap_var_returns_null_when_unmeasurable()
    {
        Assert.Null(SceneCutMetrics.LapVar(new byte[4], 2, 2));                     // 太小(没有内点)
        Assert.Null(SceneCutMetrics.LapVar(new byte[4], 3, 3));                     // 长度不足
        Assert.Null(SceneCutMetrics.LapVar(null!, 3, 3));
    }

    [Fact]
    public void Measure_pair_reports_prev_and_cur_energies()
    {
        const int w = 9, h = 9;
        var smooth = new byte[w * h];
        var noisy = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) noisy[y * w + x] = (byte)(((x + y) % 2 == 0) ? 255 : 0);
        SceneCutMetrics.MeasurePair(smooth, noisy, w, h, out double diff, out double? lapPrev, out double? lapCur);
        Assert.True(diff > 100, $"帧差应很大,实得 {diff}");      // 全黑 → 棋盘:平均差 ≈ 127
        Assert.Equal(0, lapPrev!.Value, 6);                       // 前帧平坦
        Assert.True(lapCur > 1000);                               // 后帧结构能量大 → 比值≈0 ⇒ 判据会判"结构突变"
    }
}
