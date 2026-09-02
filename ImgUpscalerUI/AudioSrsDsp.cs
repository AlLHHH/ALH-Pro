// AudioSrsDsp.cs — 音频超分辨率 DSP(纯 C#,对齐 LavaSR Python 实现 Apache-2.0):
// STFT/ISTFT(Hann 窗、onesided)、mel-filterbank(Lucas 系)、resample_poly、
// Linkwitz-Riley 频谱合并。不用 scipy,全部手写数学(与 numpy 输出对齐)。
using System;
using System.Collections.Generic;
using System.Linq;

namespace ALHPro;

public static class AudioSrsDsp
{
    // ---------- 重采样(窗化 sinc,与 scipy.resample_poly 质量可比) ----------
    public static float[] ResamplePoly(float[] x, int up, int down)
    {
        if (up == down) return x;
        // 目标长度
        long outN = (long)Math.Round((double)x.Length * up / down);
        var y = new float[outN];
        // 半带 sinc 低通:cutoff 归一化到"输出奈奎斯特 × 目标比"
        // 边缘衰减主因是 cutoff 太窄+窗不够长;用 Kaiser 窗(alpha=5)半径 32
        int halfKernel = 32;
        double ratio = (double)up / down;
        double cutoff = ratio < 1.0 ? ratio : 1.0;   // 下采样防混叠,上采样全频带
        cutoff *= 0.95;
        const double alpha = 5.0;
        var kernel = new double[halfKernel * 2 + 1];
        double ks = 0;
        for (int i = -halfKernel; i <= halfKernel; i++)
        {
            double t = i * cutoff;
            double v = Math.Abs(t) < 1e-9 ? 2.0 * cutoff : Math.Sin(Math.PI * t) / (Math.PI * t) * 2.0 * cutoff;
            double xr = i / (double)halfKernel;
            double win = BesselI0(alpha * Math.Sqrt(Math.Max(0, 1 - xr * xr))) / BesselI0(alpha);
            kernel[i + halfKernel] = v * win;
            ks += v * win;
        }
        for (int i = 0; i < kernel.Length; i++) kernel[i] /= ks;
        for (int i = 0; i < outN; i++)
        {
            double pos = i / ratio;
            int center = (int)Math.Round(pos);
            double acc = 0;
            for (int k = -halfKernel; k <= halfKernel; k++)
            {
                int idx = center + k;
                if (idx < 0) idx = 0;
                if (idx >= x.Length) idx = x.Length - 1;
                acc += x[idx] * kernel[k + halfKernel];
            }
            y[i] = (float)Math.Clamp(acc, -1.0, 1.0);
        }
        return y;
    }

    private static double BesselI0(double x)
    {
        double sum = 1, term = 1, k = 1;
        double xh = x / 2;
        while (true)
        {
            term *= xh / k;
            term *= xh / k;
            sum += term;
            if (term < 1e-9) break;
            k++;
        }
        return sum;
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

    // ---------- Linkwitz-Riley 频谱合并(对齐 _spectral_merge) ----------
    public static float[] SpectralMerge(float[] original, float[] enhanced, int sr, double cutoffHz, int transitionBins)
    {
        int n = Math.Min(original.Length, enhanced.Length);
        if (n <= 0) return enhanced;
        var specO = RfftD(original, n);
        var specE = RfftD(enhanced, n);
        var freqs = new double[specO.Length];
        for (int k = 0; k < specO.Length; k++) freqs[k] = k * sr / (double)n;
        int cutoffBin = 0;
        double best = double.MaxValue;
        for (int k = 0; k < freqs.Length; k++)
        {
            double d = Math.Abs(freqs[k] - cutoffHz);
            if (d < best) { best = d; cutoffBin = k; }
        }
        int half = Math.Max(1, transitionBins / 2);
        int start = Math.Max(0, cutoffBin - half);
        int end = Math.Min(specO.Length - 1, cutoffBin + half);
        var mask = new float[specO.Length];
        if (start > 0) for (int k = 0; k < start; k++) mask[k] = 1f;
        if (end > start)
        {
            int span = end - start + 1;
            for (int i = 0; i < span; i++)
            {
                double t = 1.0 - (double)i / (span - 1);
                mask[start + i] = (float)(3.0 * t * t - 2.0 * t * t * t);
            }
        }
        var merged = new Complex[specO.Length];
        for (int k = 0; k < specO.Length; k++)
        {
            // merged = spec_e + (spec_o - spec_e) * mask
            merged[k] = new Complex(
                specE[k].Re + (specO[k].Re - specE[k].Re) * mask[k],
                specE[k].Im + (specO[k].Im - specE[k].Im) * mask[k]);
        }
        // 逆变换:频段数 N/2+1 → 完整 N(2 幂) — 与 RfftD 的 pad 对称
        int np = 1;
        while (np < n) np <<= 1;
        var fullSpec = new Complex[np];
        for (int k = 0; k < merged.Length; k++) fullSpec[k] = merged[k];
        var wavN = Irfft(fullSpec, np);
        var result = new float[n];
        for (int i = 0; i < n; i++) result[i] = (float)Math.Clamp(wavN[i], -1.0, 1.0);
        return result;
    }

    private static Complex[] RfftD(float[] x, int n)
    {
        // n 必须 2 的幂(FFT);非 2 的幂时零填充到 nextPow2(幅度/频率保持,逆变换后截断)
        int np = 1;
        while (np < n) np <<= 1;
        var frame = new double[np];
        for (int i = 0; i < n; i++) frame[i] = x[i];
        var full = Rfft(frame, np);
        // 截断到 n/2+1(只保留有效频段)
        var half = new Complex[n / 2 + 1];
        int keep = Math.Min(half.Length, full.Length);
        for (int i = 0; i < keep; i++) half[i] = full[i];
        return half;
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
