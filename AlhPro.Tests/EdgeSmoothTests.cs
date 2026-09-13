using AlhPro.Core;
using System;
using Xunit;

namespace AlhPro.Tests;

/// <summary>「边缘抗锯齿」像素级实现的单测。
/// 【为什么必须有】这一档直接决定成片画面(视频页 4K/4320p 每帧都要跑),而 2026-09-13 的提速重写
/// 把"每像素 9 次 Math.Clamp 求邻域坐标"换成了"边界列单独处理、内部像素免 clamp"——
/// 属于"看着等价、写错就静默改画面"的改动,必须用【朴素参考实现逐字节比对】钉住,
/// 而不是靠"看起来对"。</summary>
public class EdgeSmoothTests
{
    /// <summary>朴素参考实现 = 提速前 EngineService.EdgeSmoothChannel 的逐字复刻(每像素 9 次 Math.Clamp)。
    /// 判据是"同一输入 → 逐字节相同的输出平面",边界列/单像素宽/单像素高都覆盖。</summary>
    private static byte[] Naive(byte[] src, int w, int h, int strength)
    {
        var dst = (byte[])src.Clone();
        if (strength <= 0) return dst;
        double mix = strength / 100.0 * 0.55;
        var orig = (byte[])src.Clone();
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int center = orig[y * w + x];
                int sum = 0, maxDiff = 0, cnt = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int yy = Math.Clamp(y + dy, 0, h - 1) * w;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = Math.Clamp(x + dx, 0, w - 1);
                        int v = orig[yy + xx];
                        sum += v;
                        cnt++;
                        int d = v > center ? v - center : center - v;
                        if (d > maxDiff) maxDiff = d;
                    }
                }
                if (maxDiff < 16) continue;                 // 平坦区不动(与生产口径同一个阈值)
                int mean = sum / cnt;
                int outV = center + (int)Math.Round((mean - center) * mix);
                dst[y * w + x] = (byte)Math.Clamp(outV, 0, 255);
            }
        }
        return dst;
    }

    private static byte[] RandomPlane(int w, int h, int seed, int maxValue = 255)
    {
        var rnd = new Random(seed);
        var p = new byte[w * h];
        for (int i = 0; i < p.Length; i++) p[i] = (byte)rnd.Next(0, maxValue + 1);
        return p;
    }

    [Theory]
    // 尺寸覆盖:w/h = 1(单像素宽/高)、2、3(最小有效尺寸)以及常规尺寸;强度覆盖弱/中/满档
    [InlineData(1, 1, 45)]
    [InlineData(1, 5, 45)]
    [InlineData(5, 1, 45)]
    [InlineData(2, 2, 45)]
    [InlineData(2, 7, 100)]
    [InlineData(3, 3, 45)]
    [InlineData(3, 3, 1)]
    [InlineData(3, 3, 100)]
    [InlineData(4, 7, 45)]
    [InlineData(17, 13, 45)]
    [InlineData(64, 33, 100)]
    [InlineData(33, 64, 10)]
    public void Matches_naive_reference_bit_exactly(int w, int h, int strength)
    {
        for (int seed = 1; seed <= 4; seed++)
        {
            var src = RandomPlane(w, h, seed * 1000 + w * 31 + h);
            var expect = Naive(src, w, h, strength);
            var got = (byte[])src.Clone();
            EdgeSmooth.Channel(got, w, h, strength);
            Assert.Equal(expect, got);   // 逐字节相同:提速不许改画面
        }
    }

    [Fact]
    public void Matches_naive_reference_on_low_contrast_planes()
    {
        // 低对比度平面:绝大多数像素的 maxDiff < 16(平坦区),只要边界列的判定写错就会露馅
        for (int seed = 1; seed <= 6; seed++)
        {
            var src = RandomPlane(23, 9, seed, maxValue: 20);
            var expect = Naive(src, 23, 9, 45);
            var got = (byte[])src.Clone();
            EdgeSmooth.Channel(got, 23, 9, 45);
            Assert.Equal(expect, got);
        }
    }

    [Fact]
    public void Flat_area_is_untouched()
    {
        var src = new byte[16 * 16];
        Array.Fill(src, (byte)137);
        var got = (byte[])src.Clone();
        EdgeSmooth.Channel(got, 16, 16, 100);
        Assert.Equal(src, got);   // 均匀画面:没有任何像素算边缘 → 一个字节都不许变
    }

    [Fact]
    public void Strength_zero_is_noop()
    {
        var src = RandomPlane(9, 9, 7);
        var got = (byte[])src.Clone();
        EdgeSmooth.Channel(got, 9, 9, 0);
        Assert.Equal(src, got);
        Assert.Equal(0.0, EdgeSmooth.MixFor(0), 9);
        Assert.Equal(0.55, EdgeSmooth.MixFor(100), 9);   // 满档系数仍是 0.55(与旧实现同一口径)
    }

    [Fact]
    public void Edge_pixel_moves_toward_local_mean_by_exact_amount()
    {
        // 5×5 全 100,中心(2,2)放一个 255。强度 45 → mix = 0.45 × 0.55 = 0.2475。
        // 中心像素:9 邻域和 = 8×100 + 255 = 1055 → 均值 = 1055/9 = 117(整数除法)
        //   → out = 255 + round((117−255) × 0.2475) = 255 + round(−34.155) = 221
        // 它的右邻 (3,2)(值 100):同样的邻域和 1055 → 117
        //   → out = 100 + round((117−100) × 0.2475) = 100 + round(4.2075) = 104
        var src = new byte[25];
        Array.Fill(src, (byte)100);
        src[12] = 255;   // (x=2,y=2)
        var got = (byte[])src.Clone();
        EdgeSmooth.Channel(got, 5, 5, 45);
        Assert.Equal(221, got[12]);
        Assert.Equal(104, got[13]);
        // 与朴素实现一致(同一条口径的交叉验证)
        Assert.Equal(Naive(src, 5, 5, 45), got);
    }

    [Fact]
    public void Edge_threshold_is_16_inclusive()
    {
        // 中心 100、邻域里放一个 115:maxDiff = 15 < 16 → 中心像素【不动】
        var below = new byte[25];
        Array.Fill(below, (byte)100);
        below[13] = 115;   // (x=3,y=2)
        var b = (byte[])below.Clone();
        EdgeSmooth.Channel(b, 5, 5, 100);
        Assert.Equal(100, b[12]);   // 中心(2,2)的邻域含 115 → 差 15 → 不算边缘

        // 换成 116:maxDiff = 16 → 中心像素被平滑
        var at = new byte[25];
        Array.Fill(at, (byte)100);
        at[13] = 116;
        var a = (byte[])at.Clone();
        EdgeSmooth.Channel(a, 5, 5, 100);
        Assert.NotEqual(100, a[12]);
        Assert.Equal(Naive(at, 5, 5, 100), a);
    }

    [Fact]
    public void Output_never_wraps_at_extremes()
    {
        // 0/255 棋盘:所有像素都是边缘,混完必须仍在 0~255(不许因字节运算回绕)
        var src = new byte[32 * 32];
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
                src[y * 32 + x] = (byte)(((x + y) & 1) == 0 ? 0 : 255);
        var got = (byte[])src.Clone();
        EdgeSmooth.Channel(got, 32, 32, 100);
        Assert.All(got, v => Assert.InRange(v, (byte)0, (byte)255));
        Assert.Equal(Naive(src, 32, 32, 100), got);
    }

    [Fact]
    public void Buffer_too_small_is_left_untouched()
    {
        // 尺寸与缓冲不匹配(调用方 bug):不越界、不改动,而不是按错误尺寸乱写
        var src = new byte[10];
        Array.Fill(src, (byte)200);
        var got = (byte[])src.Clone();
        EdgeSmooth.Channel(got, 100, 100, 45);
        Assert.Equal(src, got);
    }
}
