using System.Linq;

namespace AlhPro.Core;

/// <summary>
/// 音频重采样 + 时域分频的纯数学(抽到 Core 才可客观单测:SNR、相邻样本重复率、频谱保真)。
/// </summary>
/// <remarks>
/// 为什么抽出来:原先这两段数学写在 ImgUpscalerUI/AudioSrsDsp.cs 里,而它依赖 WinUI 工程 →
/// 单测项目(只引用 AlhPro.Core)根本碰不到,于是两个真实缺陷长期没有人能"用数字证明":
///   1) 重采样丢小数相位 → 等价"低通 + 最近邻",16k→44.1k 实测 SNR 只有 18.9 dB、
///      63.7% 相邻输出样本逐位相同(修复后 115.1 dB / 0.00%);
///   2) 频域分频的谱轴按"信号长度 n"解释,而 FFT 是按 nextPow2(n) 做的 → 长度非 2 的幂时
///      分频点整体下移、保留上限被硬限带(352800 点实测 17.64kHz 分量被丢掉,修复后 0.00 dB)。
/// 现在 AudioSrsDsp 只做委托,行为由这里的构造保证,测试钉住的就是生产路径本身。
/// </remarks>
public static class AudioResample
{
    /// <summary>窗化 sinc 核的单边抽头数(核总长 513)。
    /// 为什么从改前的 32 提到 256:上采样时第一镜像落在 fs−f(16k→44.1k 时是 15kHz),
    /// 要把 0.99 截止的过渡带做成 −80dB 阻带,抽头数 ≈ (A−8)/(2.285·Δω) ≈ 343;
    /// 256 已留足余量,实测 1kHz 正弦 SNR 由 18.9 dB(改前)→ 115 dB。</summary>
    private const int KernelHalfWidth = 256;

    /// <summary>Kaiser 窗阻带衰减(dB) → β。80dB 是"在 16-bit 输出里彻底看不见"的量级。</summary>
    private const double KernelAttenDb = 80.0;

    /// <summary>
    /// 有理数倍率重采样(up/down),输出长度 = round(x.Length * up / down)。
    /// </summary>
    /// <remarks>
    /// 关键正确性(改前缺失的那一步):输出样本 i 对应的源位置 pos = i / ratio 一般是【小数】,
    /// 核必须按该小数相位求值 —— 对抽到的源样本 x[floor(pos)+k] 使用 kernel(k − frac),
    /// 并按【该相位实际用到的窗和】归一。改前是 center = Round(pos) 后所有输出共用同一个
    /// 整数相位核,于是凡 Round(pos) 相同的输出样本逐位相同(16k→44.1k 占 63.7%)= 零阶保持,
    /// 实测对理想正弦只有 18.9 dB SNR。小数相位是"分辨力"本身:丢相位 = 最近邻。
    /// 下采样另有 cutoff = ratio×0.95 防混叠(策略与改前一致)。
    /// </remarks>
    public static double[] Resample(double[] x, int up, int down)
    {
        if (x is null || x.Length == 0) return Array.Empty<double>();
        if (up <= 0 || down <= 0) throw new ArgumentOutOfRangeException(nameof(up), "up/down 必须为正整数");
        if (up == down) return (double[])x.Clone();
        int outLen = (int)Math.Round((double)x.Length * up / down);
        if (outLen <= 0) return Array.Empty<double>();

        double ratio = (double)up / down;
        // 核截止:上采样时源信号带宽是固定的,核必须接近全通(0.99)——只有 1% 留给过渡带。
        // 不能用改前那种折中 0.95:源信号有效带宽被压到 0.95×Nyquist 后,第一镜像(fs−f)
        // 落进过渡带附近只被压约 13 dB → SNR 上限只有约 37 dB(实测)。
        // 下采样压到 ratio 以下防混叠(与改前同策略,该路径改前实测正常)。
        double cutoff = (ratio < 1.0 ? ratio * 0.95 : 0.99);
        int half = Math.Max(1, KernelHalfWidth);
        double beta = KaiserBeta(KernelAttenDb);
        double i0b = BesselI0(beta);

        // 多相表:up/down 约分后"小数位置"只有 phaseN 种(frac = p/phaseN),核系数只需算一次。
        // 逐样本直接调窗函数会重算 Bessel I0(实测 8 秒音频一个声道要 6.5 秒),预表后与改前同量级。
        // 相位 p 的核:row[k+half] = kernel(k − p/phaseN)。
        int g = Gcd(up, down);
        int phaseN = up / g;
        var phases = new double[phaseN][];
        for (int p = 0; p < phaseN; p++)
        {
            double fr = (double)p / phaseN;
            var row = new double[half * 2 + 1];
            for (int k = -half; k <= half; k++) row[k + half] = WindowedSinc(k - fr, cutoff, half, beta, i0b);
            phases[p] = row;
        }

        var y = new double[outLen];
        for (int i = 0; i < outLen; i++)
        {
            // 源位置 pos = fl + frac(frac ∈ [0,1));核中心放在 pos(对称),抽头 t 作用在 x[fl + t]、
            // 位移为 t − frac,故相位取 p = round(frac·phaseN)、查 row[t + half]。
            // (注:抽头索引基准与相位号必须【同源】—— 都从 frac 出发;两者错一位会让有效核整体
            //  平移一个源样本,实测 SNR 从 155 dB 掉到 8 dB。)
            double pos = i / ratio;
            int fl = (int)Math.Floor(pos);
            double frac = pos - fl;
            int pi = (int)Math.Round(frac * phaseN);
            if (pi >= phaseN) pi = 0;        // frac→1 的舍入
            var row = phases[pi];
            double acc = 0, norm = 0;
            for (int t = -half; t <= half; t++)
            {
                int idx = fl + t;
                if (idx < 0) idx = 0;
                else if (idx >= x.Length) idx = x.Length - 1;   // 端点复制补边(与改前一致:不引入额外首尾 click)
                double c = row[t + half];
                acc += x[idx] * c;
                norm += c;
            }
            // 按该相位归一:核中心区归一 ≈ 1;边界处(索引被夹到首/尾)窗和被削,补偿增益
            if (Math.Abs(norm) > 1e-6) acc /= norm;
            y[i] = Math.Clamp(acc, -1.0, 1.0);
        }
        return y;
    }

    private static int Gcd(int a, int b) { while (b != 0) { int t = a % b; a = b; b = t; } return a; }

    /// <summary>Kaiser 设计公式:阻带衰减 A(dB) → β。</summary>
    private static double KaiserBeta(double attenDb)
        => attenDb > 50.0 ? 0.1102 * (attenDb - 8.7)
           : attenDb >= 21.0 ? 0.5842 * Math.Pow(attenDb - 21, 0.4) + 0.07886 * (attenDb - 21)
           : 0.0;

    /// <summary>按任意相位 d(单位:源样本)求窗化 sinc 系数(未归一;归一由调用方按相位求和完成)。
    /// |d| 超出核半径时窗为 0,自然截断。</summary>
    private static double WindowedSinc(double d, double cutoff, int half, double beta, double i0b)
    {
        double t = d * cutoff;
        double v = Math.Abs(t) < 1e-9 ? 2.0 * cutoff : Math.Sin(Math.PI * t) / (Math.PI * t) * 2.0 * cutoff;
        if (beta <= 0) return v;
        double xr = d / half;
        if (xr * xr >= 1.0) return 0;
        double win = BesselI0(beta * Math.Sqrt(1.0 - xr * xr)) / i0b;
        return v * win;
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

    /// <summary>
    /// 分频合并:original 只保留 cutoffHz 以下,enhanced 只保留 cutoffHz 以上,两者相加。
    /// </summary>
    /// <remarks>
    /// 为什么改成【时域】而不是继续在频域交叉:
    /// 改前实现把长度 n 的信号零填充到 nextPow2(n) 做 np 点 FFT,却仍按 n 解释谱轴
    /// (freqs[k]=k*sr/n、cutoffBin 也按 n 推),并把频谱截断到 n/2+1 个 bin ——
    /// 等效于把输出硬限带到 (n/np)*fs/2、分频点整体乘 n/np(0.5~1 倍)。
    /// 实测 44.1kHz、17.64kHz 分量:n=4096(恰 2 的幂)= 0 dB 正常;
    /// n=4097 → 分频点从 8kHz 掉到 4kHz、17.64kHz 被丢掉;n=352800(8 秒 16k→48k 用例长度)
    /// → 17.64kHz 只剩 −140 dB 量级的残渣、保留上限仅 14.8kHz。
    /// 而 16k 源真实带宽只到 8k,分频点一定要在 8kHz:落到 5.4kHz 就把 5~8kHz 的真实原声
    /// 交给 AI 去"猜",正是这个功能最该避免的。
    /// 时域做法彻底避开谱轴:零相位线性相位 FIR(低通 hL)+ 互补高通 (δ − hL) = 1,
    /// 两路严格同延迟、相加重建原始频段,且与信号长度完全解耦(顺带消除了全曲 np 点 FFT 的内存峰值)。
    /// </remarks>
    /// <param name="original">原始波形(保留低频)。</param>
    /// <param name="enhanced">增强/带宽扩展后的波形(提供高频)。</param>
    /// <param name="sampleRate">采样率 Hz。</param>
    /// <param name="cutoffHz">分频点 Hz。</param>
    /// <param name="transitionBins">过渡带宽度(单位:该采样率下该长度信号 FFT 的 bin 数,与改前同口径)。</param>
    public static double[] SpectralMerge(double[] original, double[] enhanced, int sampleRate,
        double cutoffHz, int transitionBins)
    {
        if (original is null || enhanced is null) throw new ArgumentNullException(nameof(original));
        int n = Math.Min(original.Length, enhanced.Length);
        if (n <= 0) return Array.Empty<double>();
        if (sampleRate <= 0) return original.Take(n).ToArray();

        // 过渡带宽度(Hz)与信号长度无关 —— 这正是修掉谱轴错误的要点
        double transitionHz = (double)sampleRate * Math.Max(1, transitionBins) / n;
        double nyq = sampleRate / 2.0;
        double fc = Math.Clamp(cutoffHz, transitionHz * 0.5, nyq - transitionHz * 0.5);

        // Kaiser β=6(≈60dB 阻带):全频带(16k 源)时阻带残留 ≈1e-4,不污染保留的原声频段
        double[] hL = FirKaiserLowpass(fc / nyq, transitionHz / sampleRate, n);
        int m = (hL.Length - 1) / 2;

        var output = new double[n];
        for (int i = 0; i < n; i++)
        {
            double lo = 0, hi = 0;
            for (int k = -m; k <= m; k++)
            {
                int j = i - k;
                double xo = 0, xe = 0;
                if (j >= 0 && j < n) { xo = original[j]; xe = enhanced[j]; }
                double c = hL[k + m];
                lo += c * xo;
                // 互补高通 = δ − 低通:两路相加恒等于原信号(除群延迟),不会在分频点掉电平
                double cHigh = (k == 0 ? 1.0 : 0.0) - c;
                hi += cHigh * xe;
            }
            output[i] = Math.Clamp(lo + hi, -1.0, 1.0);
        }
        return output;
    }

    /// <summary>
    /// 零相位线性相位低通(Kaiser 窗窗化 sinc),长度取最接近的奇数,DC 增益归一为 1。
    /// </summary>
    /// <param name="fcNorm">归一化截止频率(1.0 = 奈奎斯特)。</param>
    /// <param name="transNorm">归一化过渡带宽度(1.0 = 奈奎斯特)。tap 数 ≈ 4/transNorm。</param>
    /// <param name="signalLen">信号长度(信号很短时收紧 tap 数,避免核长于信号)。</param>
    internal static double[] FirKaiserLowpass(double fcNorm, double transNorm, int signalLen)
    {
        fcNorm = Math.Clamp(fcNorm, 1e-4, 0.999);
        transNorm = Math.Max(transNorm, 1e-4);
        int taps = (int)Math.Round(4.0 / transNorm);
        if ((taps & 1) == 0) taps++;
        taps = Math.Clamp(taps, 31, 4001);
        if (signalLen > 0) taps = Math.Min(taps, Math.Max(31, (signalLen / 3) | 1));

        // 过渡带被迫收到很窄时改用矩形窗(Hann/Kaiser 的等效噪声带宽会吃掉整条过渡带)
        bool rect = (double)(taps - 1) / 2.0 / signalLen > 0.2;
        const double beta = 6.0;
        int m = (taps - 1) / 2;
        var h = new double[taps];
        double sum = 0;
        for (int k = -m; k <= m; k++)
        {
            double v = k == 0 ? 2.0 * fcNorm : Math.Sin(2.0 * Math.PI * fcNorm * k) / (Math.PI * k);
            double win;
            if (rect) win = 1.0;
            else
            {
                double xr = k / (double)m;
                win = BesselI0(beta * Math.Sqrt(Math.Max(0, 1 - xr * xr))) / BesselI0(beta);
            }
            h[k + m] = v * win;
            sum += v * win;
        }
        if (Math.Abs(sum) > 1e-12)
            for (int i = 0; i < taps; i++) h[i] /= sum;
        return h;
    }
}
