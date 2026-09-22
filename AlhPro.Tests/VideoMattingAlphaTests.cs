using System;
using System.Linq;
using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>
/// 逐帧 alpha 后处理的口径契约(2026-09-22)。与图片抠图同口径:
/// 前景阈值以上算实心、背景阈值以下算透明,中间做羽化过渡。
///
/// 【为什么值得钉】这一段跑在【每一帧】上,而且是"肉眼最容易看出问题"的一段:
/// 阈值口径错了整幅边缘发虚,羽化错了发丝变锯齿,形态学半径大了主体被啃瘦。
/// 视频里这些都还会随时间晃动,比单图难查得多。
/// </summary>
public class VideoMattingAlphaTests
{
    [Fact]
    public void Thresholds_push_pixels_to_solid_or_transparent()
    {
        var a = new float[] { 0.10f, 0.50f, 0.95f };  // fg=200/255≈0.784, bg=64/255≈0.251
        VideoMatting.PostProcessAlpha(a, 3, 1, fg: 200, bg: 64, feather: 0, morph: 0);
        Assert.Equal(0f, a[0], 3);   // 低于背景阈值 ⇒ 完全透明
        Assert.Equal(1f, a[2], 3);   // 高于前景阈值 ⇒ 完全实心
        Assert.InRange(a[1], 0.01f, 0.99f);  // 中间区保留过渡(不被二值化)
    }

    /// <summary>羽化把"硬台阶"磨成"缓坡",而且不许把过渡带整体抹平。
    ///
    /// 【为什么不用计划初稿里的线性斜坡(ramp i/8)】那条判据在数学上恒不成立:
    /// **线性斜坡是方框模糊的不动点** —— 斜坡上任意邻域的均值仍落在同一条斜线上,
    /// 所以模糊前后相邻像素差一模一样(实测都是 0.125,断言 < 0.125 必然失败)。
    /// 用它当输入等于什么都没测。硬台阶(0→1)才是"边缘割裂"的真实现场。</summary>
    [Fact]
    public void Feather_smooths_a_hard_step_edge()
    {
        var hard = Enumerable.Repeat(0f, 16).Concat(Enumerable.Repeat(1f, 16)).ToArray();
        var soft = (float[])hard.Clone();
        VideoMatting.PostProcessAlpha(hard, 32, 1, fg: 255, bg: 0, feather: 0, morph: 0);
        VideoMatting.PostProcessAlpha(soft, 32, 1, fg: 255, bg: 0, feather: 8, morph: 0);

        float dMax(float[] v) => Enumerable.Range(1, v.Length - 1).Max(i => Math.Abs(v[i] - v[i - 1]));
        Assert.Equal(1f, dMax(hard), 3);                       // 未羽化:一步从 0 跳到 1
        Assert.True(dMax(soft) < 0.5f, $"羽化没把台阶磨缓:dMax={dMax(soft):0.000}(判据 <0.5)");
        Assert.Contains(soft, v => v > 0f && v < 1f);           // 且是缓坡,不是被抹平
    }

    [Fact]
    public void Zero_parameters_is_identity()
    {
        var a = new float[] { 0f, 0.4f, 1f };
        var b = (float[])a.Clone();
        VideoMatting.PostProcessAlpha(a, 3, 1, fg: 0, bg: 0, feather: 0, morph: 0);
        Assert.Equal(b, a);  // fg=bg=0 表示"两个阈值都不启用"⇒ 原样返回
    }

    // ---- 以下两条是执行计划时补的把关测试:形态学改成可分离实现,必须证明语义没变 ----

    /// <summary>孤立噪点(单像素)应被开运算削掉,而成片主体必须保留原大小(开运算不是腐蚀)。
    /// 半径:r = max(1, morph/25) ⇒ morph=50 → r=2。主体宽 9 ⇒ 腐蚀成 5、膨胀回 9。</summary>
    [Fact]
    public void Isolated_speck_is_removed_but_solid_body_keeps_its_size()
    {
        const int w = 41;
        var a = new float[w];
        for (int x = 16; x < 25; x++) a[x] = 1f;   // 宽 9 的主体
        a[3] = 1f;                                  // 远处一个孤立噪点

        VideoMatting.PostProcessAlpha(a, w, 1, fg: 0, bg: 0, feather: 0, morph: 50);

        Assert.Equal(0f, a[3], 3);                  // 噪点被削掉
        for (int x = 16; x < 25; x++) Assert.Equal(1f, a[x], 3);   // 主体一根不少
        Assert.Equal(0f, a[15], 3);                 // 主体没被向外撑大
        Assert.Equal(0f, a[25], 3);
    }

    /// <summary>可分离形态学(先行后列)必须与朴素二维实现【逐像素等价】——
    /// 这不是"差不多",因为边界处理(越界邻居跳过)在两种写法里必须完全一致,
    /// 差一点就会在画面边缘留一圈啃痕。</summary>
    [Fact]
    public void Separable_morphology_matches_naive_2d_reference()
    {
        const int w = 37, h = 23;
        var rnd = new Random(20260922);
        var src = new float[w * h];
        for (int i = 0; i < src.Length; i++) src[i] = (float)rnd.NextDouble();

        foreach (int morph in new[] { 25, 50, 100 })   // r = 1 / 2 / 4
        {
            var got = (float[])src.Clone();
            VideoMatting.PostProcessAlpha(got, w, h, fg: 0, bg: 0, feather: 0, morph: morph);

            int r = Math.Max(1, morph / 25);
            var want = NaiveOpen(src, w, h, r);

            for (int i = 0; i < src.Length; i++)
                Assert.True(Math.Abs(got[i] - want[i]) < 1e-6f,
                    $"morph={morph} 第 {i} 个像素不一致:可分离={got[i]} 朴素={want[i]}");
        }
    }

    /// <summary>朴素二维开运算(测试内参照实现,故意写得最直白)。</summary>
    private static float[] NaiveOpen(float[] src, int w, int h, int r)
    {
        var e = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float m = 1f;
                for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx < 0 || xx >= w || yy < 0 || yy >= h) continue;
                        m = Math.Min(m, src[yy * w + xx]);
                    }
                e[y * w + x] = m;
            }
        var d = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float m = 0f;
                for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx < 0 || xx >= w || yy < 0 || yy >= h) continue;
                        m = Math.Max(m, e[yy * w + xx]);
                    }
                d[y * w + x] = m;
            }
        return d;
    }

    /// <summary>长度不足 / 空数组 / 负尺寸都不许抛(视频长片里"某帧读失败"很常见)。</summary>
    [Fact]
    public void Bad_input_never_throws()
    {
        VideoMatting.PostProcessAlpha(new float[3], 3, 1, 200, 64, 6, 50);      // 尺寸合法、长度刚好 ⇒ 正常
        VideoMatting.PostProcessAlpha(new float[1], 3, 1, 200, 64, 6, 50);      // 长度不足
        VideoMatting.PostProcessAlpha(new float[3], 0, 0, 200, 64, 6, 50);      // 尺寸为 0
        VideoMatting.PostProcessAlpha(new float[3], -2, 1, 200, 64, 6, 50);     // 负尺寸
        var tiny = new float[] { 0.9f };
        VideoMatting.PostProcessAlpha(tiny, 1, 1, 200, 64, 6, 50);              // 1×1 也要能跑
        Assert.InRange(tiny[0], 0f, 1f);
    }

    /// <summary>无论怎么调参,输出必须始终留在 0~1(合成阶段会直接当乘法系数用)。</summary>
    [Fact]
    public void Output_always_stays_normalized()
    {
        var rnd = new Random(7);
        var a = new float[64 * 64];
        for (int i = 0; i < a.Length; i++) a[i] = (float)rnd.NextDouble();
        foreach (int feather in new[] { 0, 6, 20 })
            foreach (int morph in new[] { 0, 25, 100 })
            {
                var x = (float[])a.Clone();
                VideoMatting.PostProcessAlpha(x, 64, 64, fg: 200, bg: 64, feather: feather, morph: morph);
                Assert.All(x, v => Assert.InRange(v, 0f, 1f));
            }
    }
}
