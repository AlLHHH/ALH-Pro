using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>降噪「自动」档的判据契约(2026-09-21 实测标定)。
/// 【为什么要单测】判据决定"这批到底降不降",而它**错了不会报错**:要么该降的没降(用户以为开了),
/// 要么干净的素材被白糊一层(用户 2026-09-13 的原话:"降噪感太强、发假、塑料感、没有棱角")。
/// 阈值与理由都来自 `_qa\降噪整改_实测_20260921.md` 的实测,改阈值必须同时改那份报告。</summary>
public class VideoNoiseProbeTests
{
    /// <summary>干净素材(用户自己的片子实测颗粒σ 0.00~0.34)⇒ 不降噪,且理由里带上数字。</summary>
    [Theory]
    [InlineData(0.00, 1.045)]
    [InlineData(0.22, 1.045)]
    [InlineData(0.34, 0.797)]
    [InlineData(2.19, 1.30)]      // 贴着阈值内侧
    public void Clean_source_is_not_denoised(double grain, double blocking)
    {
        var d = VideoNoiseProbe.Decide(new VideoNoiseStats(grain, blocking, 1.2, 6));
        Assert.False(d.Denoise);
        Assert.Equal(0, d.Strength);
        Assert.Contains("跳过降噪", d.Reason);
        Assert.Contains(grain.ToString("0.00"), d.Reason);          // 如实带出体检数字
    }

    /// <summary>有噪点 ⇒ 弱档;噪点明显 ⇒ 中档。**自动档永不给"强"**(强档只在手动里)。</summary>
    [Theory]
    [InlineData(2.20, 1)]
    [InlineData(3.50, 1)]
    [InlineData(4.99, 1)]
    [InlineData(5.00, 2)]
    [InlineData(9.20, 2)]
    public void Noisy_source_gets_a_conservative_strength(double grain, int expected)
    {
        var d = VideoNoiseProbe.Decide(new VideoNoiseStats(grain, 1.02, 2.0, 6));
        Assert.True(d.Denoise);
        Assert.Equal(expected, d.Strength);
        Assert.Contains("检出噪点", d.Reason);
    }

    /// <summary>自动档**任何输入**都不许给出"强"(3):中/强实测没拉开差距,而强档观感更塑料(见类注释)。</summary>
    [Fact]
    public void Auto_never_picks_the_strongest_tier()
    {
        foreach (var g in new[] { 0.0, 1.0, 2.2, 4.0, 5.0, 7.5, 12.0, 40.0 })
            Assert.True(VideoNoiseProbe.Decide(new VideoNoiseStats(g, 1.0, 1.0, 6)).Strength <= 2,
                $"颗粒σ {g} 时自动档给出了 3(强)");
    }

    /// <summary>体检失败(抽帧不够)⇒ **不降噪**并说明原因:源有问题时"少做一步"比"多糊一层"安全。</summary>
    [Theory]
    [InlineData(2.0, 2)]      // 只剩 2 帧(<3)⇒ 体检失败
    [InlineData(2.0, 0)]      // 一帧都没抽到
    public void Failed_probe_falls_back_to_no_denoise(double grain, int frames)
    {
        var d = VideoNoiseProbe.Decide(new VideoNoiseStats(grain, 1.0, 0, frames));
        Assert.False(d.Denoise);
        Assert.Contains("体检未取到足够帧", d.Reason);
        Assert.Contains(frames.ToString(), d.Reason);
    }

    /// <summary>NaN(颗粒算不出来)也走"失败"这条路,不许把它当成"很干净"或"很脏"。
    /// ⚠ 块效应 NaN **不算失败**:纯平坦画面(黑场/纯色)天然量不出 8 像素比值,而那种画面最不需要降噪
    /// (早期版本把它判成"体检失败",在黑场片头直接误判 —— 单测当场抓到)。</summary>
    [Fact]
    public void NaN_grain_means_failed_probe_but_nan_blocking_does_not()
    {
        Assert.False(VideoNoiseProbe.Decide(new VideoNoiseStats(double.NaN, 1.0, 1.0, 6)).Denoise);
        var flat = new VideoNoiseStats(0.1, double.NaN, 0.2, 6);
        Assert.True(flat.IsValid);
        Assert.False(VideoNoiseProbe.Decide(flat).Denoise);
        Assert.Contains("块效应未测出", VideoNoiseProbe.Decide(flat).Reason);
        Assert.DoesNotContain("NaN", VideoNoiseProbe.Decide(flat).Reason);   // 日志/提示里不许出现 NaN 字样
    }

    /// <summary>块效应只"如实报告"、不改变决策(动作留给后续的 deblock 改动),但措辞要跟阈值一致。</summary>
    [Theory]
    [InlineData(1.04, "无块效应")]
    [InlineData(1.06, "块效应轻微")]
    [InlineData(1.20, "块效应明显")]
    public void Blocking_is_reported_not_acted_on(double blocking, string expected)
    {
        var clean = VideoNoiseProbe.Decide(new VideoNoiseStats(0.1, blocking, 1.0, 6));
        var noisy = VideoNoiseProbe.Decide(new VideoNoiseStats(6.0, blocking, 1.0, 6));
        Assert.Equal(0, clean.Strength);        // 块效应再重,只要颗粒干净就不降噪
        Assert.Equal(2, noisy.Strength);        // 有噪点就降,块效应不改变档位
        Assert.Contains(expected, clean.Reason);
        Assert.Contains(expected, noisy.Reason);
    }

    // ===== 度量函数本身(像素级,数学错了"降不降"就全错,所以必须钉)=====

    private static float[] Flat(int w, int h, float v)
    {
        var a = new float[w * h];
        for (int i = 0; i < a.Length; i++) a[i] = v;
        return a;
    }

    /// <summary>3×3 盒均值:常量图必须原样返回(这一步错了,后面的方差/颗粒全废)。</summary>
    [Fact]
    public void Box3_keeps_a_constant_image_unchanged()
    {
        var a = Flat(41, 23, 128f);
        var m = VideoNoiseProbe.Box3(a, 41, 23);
        Assert.Equal(a.Length, m.Length);
        Assert.All(m, v => Assert.Equal(128f, v, 3));
    }

    /// <summary>颗粒σ:纯平图 = 0;噪点越大读数越大;翻倍振幅 ≈ 翻倍读数(线性)。</summary>
    [Fact]
    public void Grain_scales_with_noise_amplitude()
    {
        const int W = 120, H = 90;
        Assert.Equal(0.0, VideoNoiseProbe.Grain(Flat(W, H, 100f), W, H), 6);

        double GrainOf(double amp)
        {
            var rnd = new System.Random(20260921);
            var a = new float[W * H];
            for (int i = 0; i < a.Length; i++) a[i] = (float)(100 + (rnd.NextDouble() * 2 - 1) * amp);
            return VideoNoiseProbe.Grain(a, W, H);
        }
        double g1 = GrainOf(3.0), g2 = GrainOf(6.0);
        Assert.True(g1 > 0.5, $"振幅 3 的噪点读出来只有 {g1:0.000},太小了");
        Assert.True(g2 > g1 * 1.6 && g2 < g1 * 2.4, $"振幅翻倍但读数从 {g1:0.000} 变成 {g2:0.000},不成比例");
    }

    /// <summary>块效应:线性渐变(处处一样陡)≈1.0;每 8 列出现一次台阶 ⇒ 明显 &gt;1.1(能判"压缩块")。</summary>
    [Fact]
    public void Blocking_detects_8px_steps_and_ignores_smooth_ramps()
    {
        const int W = 128, H = 48;
        var ramp = new float[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++) ramp[y * W + x] = 100 + x * 0.5f;      // 平滑渐变
        double rb = VideoNoiseProbe.Blocking(ramp, W, H);
        Assert.True(rb > 0.98 && rb < 1.02, $"平滑渐变的块效应应为 ≈1.0,实得 {rb:0.000}");

        var blocked = new float[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++) blocked[y * W + x] = 100 + x * 0.5f + (x / 8) * 12f;   // 每 8 列跳 12 级
        double bb = VideoNoiseProbe.Blocking(blocked, W, H);
        Assert.True(bb > 1.1, $"每 8 列一个台阶的图,块效应应 >1.1,实得 {bb:0.000}");
    }

    /// <summary>闪烁σ:6 帧完全相同 ⇒ 0;一半亮一半暗的交替 ⇒ 时间标准差就是那个幅度。</summary>
    [Fact]
    public void Flicker_measures_temporal_std_only_where_static()
    {
        const int W = 64, H = 64;
        var same = new System.Collections.Generic.List<float[]>();
        for (int k = 0; k < 6; k++) same.Add(Flat(W, H, 128f));
        Assert.Equal(0.0, VideoNoiseProbe.Flicker(same, W, H), 6);

        var alt = new System.Collections.Generic.List<float[]>();
        for (int k = 0; k < 6; k++)
        {
            var f = Flat(W, H, 128f);
            if (k % 2 == 1) for (int i = 0; i < f.Length; i++) f[i] = 148f;    // 隔帧 +20 ⇒ 时间标准差 = 10
            alt.Add(f);
        }
        Assert.Equal(10.0, VideoNoiseProbe.Flicker(alt, W, H), 1);
    }

    /// <summary>分段测闪烁:两段各自算再平均 —— 整串一起算会把"段间差异"当成闪烁。
    /// 真机踩到过:按 10%~90% 铺 6 个时间点取帧,量出 19.4 的闪烁(实际相邻帧口径只有 0.4~1.5),
    /// 因为那些帧之间隔了十几秒、差的是**镜头**不是闪烁。抽帧侧因此改成"两段各 3 张连续帧"。</summary>
    [Fact]
    public void Measure_computes_flicker_per_burst()
    {
        const int W = 32, H = 32;
        var frames = new System.Collections.Generic.List<float[]>();
        for (int k = 0; k < 3; k++) frames.Add(Flat(W, H, k % 2 == 0 ? 100f : 120f));   // 第 1 段:帧间 +20 ⇒ σ≈9.4
        for (int k = 0; k < 3; k++) frames.Add(Flat(W, H, 60f));                        // 第 2 段:完全静止 ⇒ σ=0

        var perBurst = VideoNoiseProbe.Measure(frames, W, H, 3);
        Assert.Equal(6, perBurst.Frames);
        Assert.InRange(perBurst.Flicker, 4.0, 5.5);        // (9.4 + 0) / 2

        var asOne = VideoNoiseProbe.Measure(frames, W, H); // 整串当一段(错误口径,只用来证明差别有多大)
        Assert.True(asOne.Flicker > 20, $"整串一起算应把段间差异算成 ~24 的假闪烁,实得 {asOne.Flicker:0.0}");
    }

    /// <summary>Measure() 是 UI 侧唯一入口:帧数不足 / 尺寸不符时不许崩,也不许把无效值当有效。</summary>
    [Fact]
    public void Measure_rejects_insufficient_or_mismatched_frames()
    {
        const int W = 64, H = 64;
        var two = new System.Collections.Generic.List<float[]> { Flat(W, H, 128f), Flat(W, H, 128f) };
        Assert.False(VideoNoiseProbe.Measure(two, W, H).IsValid);          // 2 帧 < 3 ⇒ 无效

        var mixed = new System.Collections.Generic.List<float[]>();
        for (int k = 0; k < 6; k++) mixed.Add(Flat(W, H, 128f));
        mixed[3] = Flat(W / 2, H, 128f);                                    // 一帧尺寸不对
        var st = VideoNoiseProbe.Measure(mixed, W, H);                      // 不许抛
        Assert.Equal(5, st.Frames);                                         // 只统计真正用上的 5 帧
        Assert.True(st.IsValid);                                            // 5 帧仍然够判

        Assert.False(VideoNoiseProbe.Measure(new System.Collections.Generic.List<float[]>(), W, H).IsValid);
        Assert.False(VideoNoiseProbe.Measure(null!, W, H).IsValid);
    }
}
