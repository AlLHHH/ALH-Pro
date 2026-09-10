using System;
using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>
/// 音频重采样与分频合并的数值判据(AUDIT_v1.3.4 第十节 音频 C1 / C2)。
/// 这些断言全部是数值判据,不看听感 —— 因为这两条缺陷在听感上只是"变糊/齿音发毛",
/// 无法靠复测确认,必须让它们在改前【必然红】、改后【必然绿】。
///
/// 改前实测(把旧算法原样复刻出来跑同一套指标得到的数,与审计报告一致):
///   重采样 16k→44.1k:SNR 18.9 dB、63.7% 相邻输出样本逐位相同(等价低通+最近邻)
///   重采样 16k→48k :SNR 19.4 dB、66.7% 逐位相同
///   重采样 44.1k→48k:SNR 27.7 dB、8.1% 逐位相同
///   分频合并(44.1k、17.64kHz 分量、n=352800):该分量衰减 −141.8 dB(保留上限仅 14.8kHz)
/// 所以下面每一条断言在修复前都成立地失败,修复后全部通过。
/// </summary>
public class AudioResampleTests
{
    const int SrcRate = 16000;
    const double ToneHz = 1000.0;
    const double Amp = 0.5;

    /// <summary>在 sr 上生成 toneHz 正弦。</summary>
    static double[] Sine(int len, double f, int sr, double amp = Amp)
    {
        var x = new double[len];
        for (int i = 0; i < len; i++) x[i] = amp * Math.Sin(2 * Math.PI * f * i / sr);
        return x;
    }

    /// <summary>理想连续正弦在"输出样本 i 对应源时间 i/ratio"处的取值(重采样的解析真值)。</summary>
    static double[] IdealSine(int outLen, double ratio, double f, int sr, double amp = Amp)
    {
        var y = new double[outLen];
        for (int i = 0; i < outLen; i++) y[i] = amp * Math.Sin(2 * Math.PI * f * (i / ratio) / sr);
        return y;
    }

    /// <summary>信噪比(dB):跳过两端 skip 个样本(滤波器边缘效应区)。</summary>
    static double SnrDb(double[] got, double[] ideal, int skip)
    {
        double sig = 0, err = 0;
        int end = Math.Min(got.Length, ideal.Length) - skip;
        for (int i = skip; i < end; i++)
        {
            sig += ideal[i] * ideal[i];
            double d = got[i] - ideal[i];
            err += d * d;
        }
        return err <= 0 ? 999 : 10 * Math.Log10(sig / err);
    }

    /// <summary>相邻输出样本【逐位相同】的比例(%):零阶保持(丢小数相位)的最直接指纹。</summary>
    static double BitIdenticalAdjacentPercent(double[] y)
    {
        int rep = 0;
        for (int i = 1; i < y.Length; i++)
            if (BitConverter.DoubleToInt64Bits(y[i]) == BitConverter.DoubleToInt64Bits(y[i - 1])) rep++;
        return 100.0 * rep / (y.Length - 1);
    }

    /// <summary>窄带峰值幅度:中段 32768 点加 Hann 窗做单频 DFT,在 f±20Hz 内取最大。
    /// 不能直接比较输出样本值 —— 线性相位滤波器会把输出整体延迟若干样本。</summary>
    static double ToneAmp(double[] y, double f, int sr, double spanHz = 20)
    {
        int seg = Math.Min(32768, y.Length);
        int start = (y.Length - seg) / 2;
        var w = new double[seg];
        double wsum = 0;
        for (int i = 0; i < seg; i++) { w[i] = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (seg - 1))); wsum += w[i]; }
        double best = 0;
        for (double df = -spanHz; df <= spanHz; df += 0.25)
        {
            double re = 0, im = 0;
            for (int i = 0; i < seg; i++)
            {
                double ph = 2 * Math.PI * (f + df) * (start + i) / sr;
                re += y[start + i] * w[i] * Math.Sin(ph);
                im += y[start + i] * w[i] * Math.Cos(ph);
            }
            best = Math.Max(best, 2.0 * Math.Sqrt(re * re + im * im) / wsum);
        }
        return best;
    }

    // ---------- C2:重采样小数相位 ----------

    [Theory]
    [InlineData(44100, 16000)]   // LavaSR 第一级 16k→44.1k
    [InlineData(48000, 16000)]   // 16k→48k
    public void Upsample_1kHz_tone_SNR_above_80dB(int up, int down)
    {
        var x = Sine(SrcRate, ToneHz, SrcRate);
        var y = AudioResample.Resample(x, up, down);
        double ratio = (double)up / down;
        var ideal = IdealSine(y.Length, ratio, ToneHz, SrcRate);

        Assert.Equal((int)Math.Round(SrcRate * ratio), y.Length);
        // 改前:18.9 dB / 19.4 dB(零阶保持的量化+镜像失真) → 必然红
        double snr = SnrDb(y, ideal, Math.Max(512, y.Length / 100));
        Assert.True(snr > 80.0, $"16k→{(int)Math.Round(SrcRate * ratio)} 对理想正弦 SNR 只有 {snr:F1} dB(应 > 80 dB)");
    }

    [Theory]
    [InlineData(44100, 16000)]
    [InlineData(48000, 16000)]
    public void Upsample_has_no_bit_identical_adjacent_samples(int up, int down)
    {
        var x = Sine(SrcRate, ToneHz, SrcRate);
        var y = AudioResample.Resample(x, up, down);
        double pct = BitIdenticalAdjacentPercent(y);
        // 改前:63.7% / 66.7%(同一 Round(pos) 的输出样本逐位重复 = 最近邻) → 必然红
        Assert.True(pct < 1.0, $"相邻输出样本逐位相同的比例 {pct:F2}%(应 < 1%):小数相位被丢弃,等价最近邻");
    }

    [Fact]
    public void Downsample_48k_to_16k_does_not_regress()
    {
        // 下采样路径(ratio<1,cutoff=ratio×0.95 防混叠)改前实测正常(62.2 dB),修 C2 不得把它改坏
        var x = Sine(48000, ToneHz, 48000);
        var y = AudioResample.Resample(x, 16000, 48000);
        var ideal = IdealSine(y.Length, 1.0 / 3.0, ToneHz, 48000);
        Assert.Equal(16000, y.Length);

        double snr = SnrDb(y, ideal, Math.Max(512, y.Length / 100));
        Assert.True(snr > 55.0, $"48k→16k 下采样 SNR 只有 {snr:F1} dB(应 > 55 dB,改前为 62.2 dB)");
        Assert.True(BitIdenticalAdjacentPercent(y) < 1.0, "下采样结果出现逐位重复样本");
    }

    [Fact]
    public void Upsample_preserves_passband_tone_level()
    {
        // 重采样必须对通带内的原声"透明":改前 0.95 截止把 7kHz 压掉 5.3 dB(听感就是高频发闷)
        const double f = 7000.0;
        var x = Sine(SrcRate, f, SrcRate);
        var y = AudioResample.Resample(x, 48000, 16000);
        double inAmp = ToneAmp(x, f, SrcRate);
        double outAmp = ToneAmp(y, f, 48000);
        double db = 20 * Math.Log10(outAmp / inAmp);
        Assert.True(Math.Abs(db) < 0.2, $"7kHz(16k 源通带内)经过 16k→48k 后电平变了 {db:F2} dB(应 < 0.2 dB)");
    }

    // ---------- C1:分频合并(谱轴) ----------

    [Fact]
    public void SpectralMerge_does_not_drop_17k_tone_on_non_power_of_two_length()
    {
        // 352800 = 8 秒 × 44.1kHz,正是"8 秒 16k→48k"自测用例的长度;非 2 的幂(2^18=262144 < 352800 < 2^19)
        // 改前:RfftD 零填充到 524288 做 np 点 FFT 却只留 n/2+1 个 bin → 输出被硬限带到 14.8kHz,
        //       17.64kHz 分量衰减 −141.8 dB → 必然红
        const int n = 352800;
        const double f = 17640.0;
        var original = Sine(n, f, 44100);
        var enhanced = Sine(n, f, 44100);   // 与 original 相同 → 分频结果必须与原信号完全一致
        var merged = AudioResample.SpectralMerge(original, enhanced, 44100, 8000.0, 1024);

        Assert.Equal(n, merged.Length);
        double db = 20 * Math.Log10(ToneAmp(merged, f, 44100) / ToneAmp(original, f, 44100));
        Assert.True(Math.Abs(db) < 1.0, $"17.64kHz 分量经过分频后变了 {db:F1} dB(应 < 1 dB;改前为 −141.8 dB)");
    }

    [Fact]
    public void SpectralMerge_keeps_crossover_at_8kHz_and_adds_ai_highband()
    {
        // 真实的 16k 源:原声真实带宽只到 8kHz,AI 补的是 8kHz 以上。
        // 改前的谱轴错误让分频点掉到 5.4kHz → 5~8kHz 的真实原声被交给 AI"猜"(这个功能最该避免的);
        // 且 17.64kHz 的 AI 高频被成段丢弃。这里两头都要钉住。
        const int n = 352800;
        const double origHz = 1000.0;      // 真实原声
        const double aiHz = 17640.0;       // AI"补出来"的高频
        var original = Sine(n, origHz, 44100);
        var enhanced = new double[n];
        for (int i = 0; i < n; i++) enhanced[i] = Amp * Math.Sin(2 * Math.PI * aiHz * i / 44100);

        var merged = AudioResample.SpectralMerge(original, enhanced, 44100, 8000.0, 1024);

        // 交叉点以下:必须原样保留原声(1kHz 驱动于 original,原声路径不能被 AI 覆盖)
        double loDb = 20 * Math.Log10(ToneAmp(merged, origHz, 44100) / ToneAmp(original, origHz, 44100));
        Assert.True(Math.Abs(loDb) < 0.5, $"交叉点以下 {origHz}Hz 原声电平变了 {loDb:F2} dB(应 < 0.5 dB)");

        // 交叉点以上:必须拿到 AI 补的高频(改前被成段丢弃)
        double hiAmp = ToneAmp(merged, aiHz, 44100);
        double hiDb = 20 * Math.Log10(hiAmp / Amp);
        Assert.True(hiDb > -1.0, $"AI 补的 {aiHz}Hz 高频经过合并后只有 {hiDb:F2} dB(应 > −1 dB)");
    }

    [Fact]
    public void SpectralMerge_is_identity_when_original_equals_enhanced()
    {
        // 分频合并的数学不变量(低频取原信号 + 高频取增强信号):两路相同时输出必须等于输入。
        // 这条是"分频点搬到 5.4kHz"这类谱轴错误的通用探针 —— 频率扫描全频带都不许掉电平。
        const int n = 65536;
        var src = new double[n];
        for (int i = 0; i < n; i++)
            src[i] = 0.3 * Math.Sin(2 * Math.PI * 300.0 * i / 44100) + 0.3 * Math.Sin(2 * Math.PI * 17640.0 * i / 44100);
        var merged = AudioResample.SpectralMerge(src, src, 44100, 8000.0, 1024);
        double snr = SnrDb(merged, src, 1024);
        Assert.True(snr > 60.0, $"original==enhanced 时输出偏离输入 {snr:F1} dB(应 > 60 dB)");
    }

    [Fact]
    public void SpectralMerge_preserves_signal_length_independent_of_cutoff()
    {
        // 与信号长度解耦:C1 的根因是"块长 = 信号长度"。任意长度都必须保持长度与幅度。
        foreach (int n in new[] { 4096, 4097, 100000, 352800 })
        {
            var original = Sine(n, 1000.0, 44100);
            var enhanced = new double[n];
            var merged = AudioResample.SpectralMerge(original, enhanced, 44100, 8000.0, 1024);
            Assert.Equal(n, merged.Length);
            double db = 20 * Math.Log10(ToneAmp(merged, 1000.0, 44100) / ToneAmp(original, 1000.0, 44100));
            Assert.True(Math.Abs(db) < 0.5, $"n={n} 时 1kHz 原声电平变了 {db:F2} dB");
        }
    }
}
