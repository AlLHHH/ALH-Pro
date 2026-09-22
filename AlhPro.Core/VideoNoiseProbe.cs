namespace AlhPro.Core;

/// <summary>源素材体检的实测值(纯数据,由 UI 侧抽帧算出来后传进来)。
/// 【为什么要有它】"该不该降噪"过去全靠用户猜 —— 实测(2026-09-21,见 `_qa\降噪整改_实测_20260921.md`):
/// 干净/轻压缩的源上开降噪,成片 PSNR 反而掉 0.3~0.9dB(H.264 crf30 源:不降噪 36.23、现行三档 35.34~35.93);
/// 而真正有噪点的源上,降噪能把成片 PSNR 从 36.22 提到 36.85、边缘宽度也收窄。⇒ 必须**先量再决定**。</summary>
/// <param name="Grain">颗粒(空间噪点):最平坦 20% 区域里 3×3 局部标准差的均值。干净 &lt;2.2,压缩噪点 2.2~5,明显 &gt;5。</param>
/// <param name="Blocking">块效应:8 像素块边界处的平均梯度 ÷ 块内平均梯度。无块 &lt;1.05,轻 1.05~1.12,重 &gt;1.12。</param>
/// <param name="Flicker">闪烁(时间噪点):最静的 25% 像素上的时间标准差。只作**日志与提示**,不参与决策(见下)。</param>
/// <param name="Frames">参与统计的帧数;不足 3 帧时视为体检失败。</param>
public readonly record struct VideoNoiseStats(double Grain, double Blocking, double Flicker, int Frames)
{
    /// <summary>够不够做判断。
    /// 【为什么只要求"颗粒"可测】块效应在**纯平坦画面**(黑场、纯色、字幕黑边)上天然是 0/0 ⇒ NaN,
    /// 而那种画面恰恰是最不需要降噪的 —— 早期版本把 NaN 块效相当成"体检失败",会在黑场片头直接判失败(单测抓到)。
    /// ⇒ 判据只看帧数与颗粒;块效应算不出来时按"未测出"如实报告。</summary>
    public bool IsValid => Frames >= 3 && !double.IsNaN(Grain);
}

/// <summary>体检结论 → 降噪决策。<paramref name="Strength"/> 与界面档位同一口径:0=不降、1=弱、2=中、3=强。</summary>
public readonly record struct VideoNoiseDecision(int Strength, string Reason)
{
    /// <summary>本批是否真的降噪。</summary>
    public bool Denoise => Strength >= 1;
}

/// <summary>「降噪强度 = 自动」时的判据(**纯逻辑,可单测**)。
///
/// 【判据来自实测,不是拍脑袋】见 `_qa\降噪整改_实测_20260921.md`:
///   · 颗粒σ &lt; 2.2(干净)⇒ **不降噪**。依据:H.264 crf30 源上,不降噪成片 PSNR 36.23 / SSIM 0.9759,
///     而现行三档(弱/中/强)分别是 35.93 / 35.75 / 35.34、SSIM 0.9468 / 0.9518 / 0.9435 —— **全是负收益**;
///     用户自己的素材实测颗粒σ 0.00~0.34(都在这条线内)。
///   · 颗粒σ ≥ 2.2 ⇒ 降噪,并按强度分两档:2.2~5 → 弱,≥5 → 中。
///     依据:JPEG q15 强噪源上,降噪把成片 PSNR 从 36.22 提到 36.49(弱)/36.63(中)/36.60(强),边宽 4.04 → 3.71~3.83。
/// 【为什么不自动选"强"】① 中/强之间没有拉开差距(36.63 对 36.60),但强档的时间维抖动更狠(×1.78 对 ×2.33)
/// ⇒ 干净观感上更"塑料";② 历史上用户对降噪的反馈就是"太强、发假、塑料感、没有棱角"(2026-09-13 把默认改成关),
/// 所以自动模式**上限只给到中**,想更狠请手动选强。
/// 【闪烁为什么不参与决策】它在不同分辨率下量纲不同(1080p 源上"干净片"也有 0.4~1.5),没有可靠的固定阈值;
/// 只在日志与提示里如实给出,供用户判断"是不是需要时间域降噪"。
/// 【块效应为什么不直接动作】块效应重的源真正对症的是 deblock(实测:成片 PSNR 与不降噪持平、SSIM 最好、
/// 细节保留 68%~86%、只要 0.014 秒/帧),但那是**另一项改动**(见报告第 5 节第 3 条),本类只如实报告它。</summary>
public static class VideoNoiseProbe
{
    /// <summary>颗粒σ 低于此值 = 素材干净(降噪是负收益)。</summary>
    public const double GrainClean = 2.2;
    /// <summary>颗粒σ 高于此值 = 噪点明显(给中档)。</summary>
    public const double GrainHeavy = 5.0;
    /// <summary>块效应低于此值 = 没有可见块效应。</summary>
    public const double BlockClean = 1.05;
    /// <summary>块效应高于此值 = 块效应明显(建议 deblock —— 本类只报告,不动作)。</summary>
    public const double BlockHeavy = 1.12;

    /// <summary>把体检值翻成决策。**体检失败**时保守给"中档"?不 —— 给"不降"并说明原因:
    /// 抽帧都失败说明源有问题,此时"少做一步"比"多糊一层"更安全(用户可手动选档强制降)。</summary>
    public static VideoNoiseDecision Decide(VideoNoiseStats s)
    {
        if (!s.IsValid)
            return new VideoNoiseDecision(0,
                $"源素材体检未取到足够帧(只有 {s.Frames} 帧)⇒ 本批不降噪(想强制降噪请把「降噪强度」手动选成弱/中/强)");

        string block = BlockText(s.Blocking);
        if (s.Grain < GrainClean)
            return new VideoNoiseDecision(0,
                $"素材干净(颗粒σ {s.Grain:0.00} < {GrainClean:0.0};块效应 {Fmt(s.Blocking)}{block})⇒ 本批跳过降噪 — "
                + "实测在干净/轻压缩源上降噪会让成片 PSNR 掉 0.3~0.9dB、细节净损失");

        int strength = s.Grain < GrainHeavy ? 1 : 2;
        string tier = strength == 1 ? "弱" : "中";
        return new VideoNoiseDecision(strength,
            $"检出噪点(颗粒σ {s.Grain:0.00} ≥ {GrainClean:0.0};块效应 {Fmt(s.Blocking)}{block})⇒ 本批按「{tier}」档降噪 — "
            + "实测有噪源上降噪可让成片 PSNR +0.3~0.6dB、边缘宽度收窄(自动档上限只到中,更强请手动选)");
    }

    /// <summary>块效应的中文档位(日志/提示里跟一句,不参与决策)。NaN = 画面太平坦、量不出比值。</summary>
    public static string BlockText(double blocking)
        => double.IsNaN(blocking) ? ",块效应未测出(画面过于平坦)"
         : blocking >= BlockHeavy ? ",块效应明显(建议改用去块滤镜)"
         : blocking >= BlockClean ? ",块效应轻微" : ",无块效应";

    /// <summary>体检数字的显示格式(NaN 要写"未测出",不能直接印 NaN —— 日志/提示里出现 NaN 会让人以为程序坏了)。</summary>
    public static string Fmt(double v) => double.IsNaN(v) ? "未测出" : v.ToString("0.000");

    // ===== 度量实现(纯函数,吃灰度数组;UI 侧只负责抽帧与解码)=====
    // 【为什么数学放在 Core】它决定"降不降",错了不会报错 —— 必须有单测(见 VideoNoiseProbeTests)。
    // UI 侧只做两件事:用 ffmpeg 抽帧、把帧解码成灰度 float[](口径与这里的函数一一对应)。

    /// <summary>3×3 盒均值(可分离:先横后纵;边界取最近像素)。与 Python 标定脚本的 reflect 只差边缘 1 像素。</summary>
    public static float[] Box3(float[] src, int w, int h)
    {
        var tmp = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            int r = y * w;
            for (int x = 0; x < w; x++)
                tmp[r + x] = (src[r + Math.Max(0, x - 1)] + src[r + x] + src[r + Math.Min(w - 1, x + 1)]) / 3f;
        }
        var dst = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            int up = Math.Max(0, y - 1) * w, cur = y * w, dn = Math.Min(h - 1, y + 1) * w;
            for (int x = 0; x < w; x++) dst[cur + x] = (tmp[up + x] + tmp[cur + x] + tmp[dn + x]) / 3f;
        }
        return dst;
    }

    /// <summary>第 p 百分位(0~1)。复制+排序+取下标(与 numpy 的线性插值略有差异,对阈值判定没有影响)。</summary>
    public static float Percentile(float[] v, double p)
    {
        var c = (float[])v.Clone();
        Array.Sort(c);
        return c[Math.Clamp((int)(c.Length * p), 0, c.Length - 1)];
    }

    /// <summary>颗粒σ:最平坦 20%(3×3 局部方差最小的一批)区域里局部标准差的均值。</summary>
    public static double Grain(float[] gray, int w, int h)
    {
        int n = w * h;
        var sq = new float[n];
        for (int i = 0; i < n; i++) sq[i] = gray[i] * gray[i];
        var m = Box3(gray, w, h);
        var m2 = Box3(sq, w, h);
        var v = new float[n];
        for (int i = 0; i < n; i++) { float d = m2[i] - m[i] * m[i]; v[i] = d > 0 ? d : 0; }
        float thr = Percentile(v, 0.20);
        double s = 0; long c = 0;
        for (int i = 0; i < n; i++) if (v[i] <= thr) { s += v[i]; c++; }
        return c == 0 ? 0.0 : Math.Sqrt(s / c);
    }

    /// <summary>块效应:8 像素块边界处的平均 |横向差| ÷ 块内平均 |横向差|。≈1.0 = 没有可见块。</summary>
    public static double Blocking(float[] gray, int w, int h)
    {
        double on = 0, off = 0; long con = 0, coff = 0;
        for (int y = 0; y < h; y++)
        {
            int r = y * w;
            for (int x = 0; x + 1 < w; x++)
            {
                double d = Math.Abs(gray[r + x + 1] - gray[r + x]);
                if (x % 8 == 7) { on += d; con++; } else { off += d; coff++; }
            }
        }
        if (con == 0 || coff == 0) return double.NaN;
        double a = on / con, b = off / coff;
        return b <= 1e-9 ? double.NaN : a / b;
    }

    /// <summary>闪烁σ:逐像素时间标准差里最小的 25%(真正静止的地方)那一批的均值。
    /// **只进日志与提示,不参与决策** —— 它在不同分辨率下量纲不同,没有可靠的固定阈值。</summary>
    public static double Flicker(IReadOnlyList<float[]> frames, int w, int h)
    {
        if (frames == null || frames.Count == 0) return 0;
        int n = w * h, f = frames.Count;
        var tstd = new float[n];
        for (int i = 0; i < n; i++)
        {
            double s = 0, s2 = 0;
            for (int k = 0; k < f; k++) { double v = frames[k][i]; s += v; s2 += v * v; }
            double mean = s / f;
            tstd[i] = (float)Math.Sqrt(Math.Max(0, s2 / f - mean * mean));
        }
        float thr = Percentile(tstd, 0.25);
        double sum = 0; long c = 0;
        for (int i = 0; i < n; i++) if (tstd[i] <= thr) { sum += tstd[i]; c++; }
        return c == 0 ? 0.0 : sum / c;
    }

    /// <summary>一次算齐三项(UI 侧抽完帧就调它)。
    /// 【尺寸不符的帧必须先剔掉再算三项】第一版只在颗粒/块效应里跳过了它们,时间维却拿着整张表去索引
    /// ⇒ 中途换分辨率的源会直接越界崩溃(单测当场抓到)。现在先过滤,再拿同一份干净表算全部指标,
    /// 帧数也按"真正用上的帧"报。
    /// <param name="burstLength">这些帧是"每 N 帧一段连续帧"(0 或 1 = 整串当一段)。
    /// 【为什么要有它】闪烁**必须看相邻帧**:真机对拍时按 10%~90% 铺点取 6 帧(帧间隔十几秒),
    /// "时间标准差"量到的是**换镜头**,直接给出 19.4 这种假数(同一片段用相邻帧量是 0.4~1.5)。
    /// ⇒ 抽帧侧改成"两段各 3 张连续帧",这里按段分别算闪烁再平均;颗粒/块效应本来就是单帧指标,照旧合起来平均。</summary>
    public static VideoNoiseStats Measure(IReadOnlyList<float[]> frames, int w, int h, int burstLength = 0)
    {
        if (frames == null || w <= 0 || h <= 0)
            return new VideoNoiseStats(double.NaN, double.NaN, 0, 0);
        var ok = new List<float[]>(frames.Count);
        foreach (var f in frames)
            if (f != null && f.Length == w * h) ok.Add(f);
        if (ok.Count < 3)
            return new VideoNoiseStats(double.NaN, double.NaN, 0, ok.Count);

        double grain = 0, block = 0; int blockN = 0;
        foreach (var f in ok)
        {
            grain += Grain(f, w, h);
            double b = Blocking(f, w, h);
            if (!double.IsNaN(b)) { block += b; blockN++; }
        }
        grain /= ok.Count;
        block = blockN > 0 ? block / blockN : double.NaN;

        int bl = burstLength <= 1 ? ok.Count : burstLength;
        var flicks = new List<double>();
        for (int s = 0; s < ok.Count; s += bl)
        {
            int n = Math.Min(bl, ok.Count - s);
            if (n < 3) break;                       // 不足 3 帧的一段算不出时间维,直接不算
            flicks.Add(Flicker(ok.GetRange(s, n), w, h));
        }
        double flicker = flicks.Count > 0 ? flicks.Average() : 0;
        return new VideoNoiseStats(grain, block, flicker, ok.Count);
    }
}
