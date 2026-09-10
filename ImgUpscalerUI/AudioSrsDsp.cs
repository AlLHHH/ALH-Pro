// AudioSrsDsp.cs — 音频超分辨率 DSP(纯 C#,对齐 LavaSR Python 实现 Apache-2.0):
// STFT/ISTFT(Hann 窗、onesided)、mel-filterbank(Lucas 系)、resample_poly、
// Linkwitz-Riley 分频合并。不用 scipy,全部手写数学(与 numpy 输出对齐)。
//
// 【为什么重采样与分频合并改为委托 AlhPro.Core.AudioResample】
// 这两段是"纯数学",以前埋在本文件里 → 单测项目(只引用 AlhPro.Core,不引 WinUI)碰不到,
// 于是两个真实缺陷长期无法用数字证明、也没人敢改(听感复测看不出来):
//   1) ResamplePoly 丢小数相位 = 低通 + 最近邻,16k→44.1k 实测 SNR 仅 18.9 dB、63.7% 相邻输出样本逐位相同;
//   2) SpectralMerge 按 n 解释谱轴而 FFT 按 nextPow2(n) 做 → 长度非 2 的幂时分频点/保留上限整体下移
//      (352800 点实测 17.64kHz 分量被丢掉、保留上限只剩 14.8kHz)。
// 现在这里只做参数与数据类型的适配(short↔double),行为由 Core 的构造保证,
// 测试钉住的 AlhPro.Core.AudioResample 就是生产路径本身,不存在"测试一套、跑的又一套"。
using System;
using System.Linq;

namespace ALHPro;

public static class AudioSrsDsp
{
    // ---------- 重采样(窗化 sinc 多相核,与 scipy.resample_poly 质量可比) ----------
    public static float[] ResamplePoly(float[] x, int up, int down)
    {
        if (x.Length == 0) return Array.Empty<float>();
        if (up == down) return x;
        // float → double:Core 按 double 计算(与 Python 参考实现的精度一致,float 会让本级的 −115dB 级
        // 改善被 float 的 24bit 尾数吃掉一部分)。本函数的入参是 int16 量级(±32767),不是 ±1.0 归一化值,
        // 故 Core 里的 Math.Clamp(±1.0) 只会截掉真正越界的样本,与本文件改前的行为一致。
        var d = new double[x.Length];
        for (int i = 0; i < x.Length; i++) d[i] = x[i];
        var y = AlhPro.Core.AudioResample.Resample(d, up, down);
        var r = new float[y.Length];
        for (int i = 0; i < y.Length; i++) r[i] = (float)y[i];
        return r;
    }

    // ---------- STFT(与 scipy.signal.stft onesided hann 对齐) ----------
    public static Complex[,] Stft(float[] wave, int nfft, int hop)
    {
        int nFrames = 1 + (wave.Length - nfft) / hop;   // padded=True boundary=zeros
        if (nFrames < 1) nFrames = 1;
        var win = HannWindow(nfft);
        int bins = nfft / 2 + 1;
        var spec = new Complex[nFrames, bins];
        for (int f = 0; f < nFrames; f++)
        {
            int start = f * hop;
            var frame = new double[nfft];
            for (int i = 0; i < nfft; i++)
            {
                int idx = start + i;
                frame[i] = (idx < wave.Length ? wave[idx] : 0.0) * win[i];
            }
            // rfft
            var comp = Rfft(frame, nfft);
            for (int k = 0; k < bins; k++)
                spec[f, k] = comp[k];
        }
        return spec;
    }

    // ---------- FFT(Cooley-Tukey radix-2,加速 Rfft/Irfft ≈200×) ----------
    private static void Fft(Complex[] a, bool inverse)
    {
        int n = a.Length;
        // 位序重排
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (a[i], a[j]) = (a[j], a[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = 2 * Math.PI / len * (inverse ? 1 : -1);
            var wlen = new Complex(Math.Cos(ang), Math.Sin(ang));
            for (int i = 0; i < n; i += len)
            {
                var w = new Complex(1, 0);
                for (int j = 0; j < len / 2; j++)
                {
                    var u = a[i + j];
                    var v = new Complex(a[i + j + len / 2].Re * w.Re - a[i + j + len / 2].Im * w.Im,
                                        a[i + j + len / 2].Re * w.Im + a[i + j + len / 2].Im * w.Re);
                    a[i + j] = new Complex(u.Re + v.Re, u.Im + v.Im);
                    a[i + j + len / 2] = new Complex(u.Re - v.Re, u.Im - v.Im);
                    double wRe = w.Re * wlen.Re - w.Im * wlen.Im;
                    w = new Complex(wRe, w.Re * wlen.Im + w.Im * wlen.Re);
                }
            }
        }
        if (inverse)
            for (int i = 0; i < n; i++) a[i] = new Complex(a[i].Re / n, a[i].Im / n);
    }

    public static Complex[] Rfft(double[] frame, int n)
    {
        var c = new Complex[n];
        for (int i = 0; i < n; i++) c[i] = new Complex(frame[i], 0);
        Fft(c, false);
        var half = new Complex[n / 2 + 1];
        for (int k = 0; k <= n / 2; k++) half[k] = c[k];
        return half;
    }

    public static double[] Irfft(Complex[] spec, int n)
    {
        // onesided → full
        var c = new Complex[n];
        for (int k = 0; k <= n / 2; k++) c[k] = spec[k];
        for (int k = n / 2 + 1; k < n; k++) c[k] = new Complex(spec[n - k].Re, -spec[n - k].Im);
        Fft(c, true);
        var wav = new double[n];
        for (int i = 0; i < n; i++) wav[i] = c[i].Re;
        return wav;
    }

    // ---------- ISTFT(overlap-add,对齐 scipy) ----------
    public static float[] Istft(Complex[,] spec, int nfft, int hop, int targetLen)
    {
        int nFrames = spec.GetLength(0);
        int bins = spec.GetLength(1);
        var win = HannWindow(nfft);
        int outLen = nFrames > 0 ? (nFrames - 1) * hop + nfft : 0;
        var wave = new double[outLen];
        var norm = new double[outLen];
        for (int f = 0; f < nFrames; f++)
        {
            var frame = new Complex[nfft];
            // 对称复数(onesided → full)
            for (int k = 0; k < bins; k++)
            {
                frame[k] = spec[f, k];
                if (k > 0 && k < nfft / 2) frame[nfft - k] = new Complex(spec[f, k].Re, -spec[f, k].Im);
                else if (k == 0) frame[0] = spec[f, 0];
                else if (k == nfft / 2) frame[nfft / 2] = spec[f, nfft / 2];
            }
            var wav = Irfft(frame, nfft);
            int start = f * hop;
            for (int i = 0; i < nfft; i++)
            {
                int idx = start + i;
                if (idx < wave.Length) { wave[idx] += wav[i] * win[i]; norm[idx] += win[i] * win[i]; }
            }
        }
        var result = new float[Math.Max(targetLen, outLen)];
        for (int i = 0; i < outLen; i++)
            result[i] = norm[i] > 1e-8 ? (float)(wave[i] / norm[i]) : 0f;
        if (result.Length > targetLen)
        {
            var trimmed = new float[targetLen];
            Array.Copy(result, trimmed, targetLen);
            return trimmed;
        }
        return result;
    }

    // ---------- mel filterbank(与 _build_mel_filterbank 对齐) ----------
    public static float[,] BuildMelFilterbank(int sr, int nfft, int nMels, double fmin, double fmax)
    {
        int bins = nfft / 2 + 1;
        var fftFreqs = new double[bins];
        for (int i = 0; i < bins; i++) fftFreqs[i] = sr / 2.0 * i / (bins - 1);
        double melMin = HzToMel(fmin), melMax = HzToMel(fmax);
        var melEdges = new double[nMels + 2];
        for (int i = 0; i < nMels + 2; i++) melEdges[i] = melMin + (melMax - melMin) * i / (nMels + 1);
        var hzEdges = melEdges.Select(MelToHz).ToArray();
        var fb = new float[nMels, bins];
        for (int m = 0; m < nMels; m++)
        {
            double left = hzEdges[m], center = hzEdges[m + 1], right = hzEdges[m + 2];
            if (center <= left || right <= center) continue;
            for (int k = 0; k < bins; k++)
            {
                double f = fftFreqs[k];
                double up = (f - left) / (center - left);
                double down = (right - f) / (right - center);
                double v = Math.Min(up, down);
                fb[m, k] = (float)(Math.Max(0.0, v) * (2.0 / Math.Max(1e-8, right - left)));
            }
        }
        return fb;
    }

    public static double HzToMel(double f)
    {
        double fSp = 200.0 / 3.0, minLogHz = 1000.0;
        double minLogMel = minLogHz / fSp;
        double logstep = Math.Log(6.4) / 27.0;
        if (f < minLogHz) return f / fSp;
        return minLogMel + Math.Log(f / minLogHz) / logstep;
    }

    public static double MelToHz(double mel)
    {
        double fSp = 200.0 / 3.0, minLogHz = 1000.0;
        double minLogMel = minLogHz / fSp;
        double logstep = Math.Log(6.4) / 27.0;
        if (mel < minLogMel) return mel * fSp;
        return minLogHz * Math.Exp(logstep * (mel - minLogMel));
    }

    // ---------- 分频合并(低频取原信号、高频取增强信号) ----------
    // 改前这里是"np 点 FFT + 按 n 解释谱轴 + 只留 n/2+1 个 bin",三个口径不一致 →
    // 长度非 2 的幂时等效把输出硬限带到 (n/np)×fs/2、分频点整体乘 n/np(0.5~1 倍):
    // 实测 44.1kHz 下 n=4097 时 8kHz 的交叉点掉到 4kHz、17.64kHz 分量被丢掉;
    // n=352800(8 秒 16k→48k 的自测用例长度)保留上限只剩 14.8kHz → 5~8kHz 的真实原声被 AI 猜测替换。
    // 现在改成时域零相位 FIR 分频(见 AlhPro.Core.AudioResample.SpectralMerge),与信号长度彻底解耦。
    public static float[] SpectralMerge(float[] original, float[] enhanced, int sr, double cutoffHz, int transitionBins)
    {
        int n = Math.Min(original.Length, enhanced.Length);
        if (n <= 0) return enhanced;
        var o = new double[n];
        var e = new double[n];
        for (int i = 0; i < n; i++) { o[i] = original[i]; e[i] = enhanced[i]; }
        var merged = AlhPro.Core.AudioResample.SpectralMerge(o, e, sr, cutoffHz, transitionBins);
        var result = new float[n];
        for (int i = 0; i < n; i++) result[i] = (float)merged[i];
        return result;
    }

    // ---------- 工具 ----------
    public static double[] HannWindow(int n)
    {
        var w = new double[n];
        for (int i = 0; i < n; i++) w[i] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (n - 1)));
        return w;
    }

    public readonly struct Complex
    {
        public readonly double Re, Im;
        public Complex(double re, double im) { Re = re; Im = im; }
    }
}
