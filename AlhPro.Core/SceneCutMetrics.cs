namespace AlhPro.Core;

/// <summary>场景硬切检测的**图像指标测量**(纯计算,输入是采样后的灰度帧缓冲,可单测)【任务 S2 · 生产接线那半】。
/// 【为什么要有它】`SceneCutJudge` 的判据吃的是"逐对相邻源帧的 mean|diff| + 拉普拉斯能量",
/// 但判据本身刻意不碰图像解码(见 SceneCutJudge 的注释);把"解码/采样/度量"这半步单独放这里,
/// 判据才能被真机数据逐条钉住、度量才能被单测覆盖。
/// 【采样分辨率的口径】采样高度固定 <see cref="SampleHeight"/>(默认 192),宽度按源宽高比换算:
///   · 全片采样帧的体量 ≈ 帧数 × 宽 × 192 字节(1080p 2670 帧约 121 MB,写在临时文件里、**按帧流式读**,
///     常驻内存只有 2 帧)—— 原分辨率会把这块临时盘/内存放大几十倍,没必要;
///   · 降采样会**同时**压低帧差与拉普拉斯能量,所以本类只保证"同一口径下可比"。
/// 【必须如实说明的一点】`SceneCutJudge` 的阈值(帧差 ≥25 / ≥50、拉普拉斯比 ≤0.6)是用户在**原分辨率**上
/// 实测标定的(58.68 / 0.474),换到 192 行采样后绝对量级会变 —— 因此这条路必须真机复测标定,
/// 代码里所有阈值都标了【待实测标定】,不许当成已验证。</summary>
public static class SceneCutMetrics
{
    /// <summary>采样高度(像素)。【待实测标定】192 = 1080p 的 1/5.6、4K 的 1/11.25:
    /// 足够保住"整场切换"这种全画面级差异,又把全片采样体量压到百 MB 级。</summary>
    public const int SampleHeight = 192;

    /// <summary>采样尺寸(宽,高):高度取 min(源高, sampleHeight),宽度按源宽高比换算(至少 1 像素)。
    /// 源尺寸非法 → (0,0)(调用方须据此放弃判切,保持既有行为)。</summary>
    public static (int w, int h) SampleSize(int srcW, int srcH, int sampleHeight = SampleHeight)
    {
        if (srcW <= 0 || srcH <= 0 || sampleHeight <= 0) return (0, 0);
        int h = Math.Min(sampleHeight, Math.Max(1, srcH));
        int w = Math.Max(1, (int)Math.Round((double)srcW * h / Math.Max(1, srcH)));
        return (w, h);
    }

    /// <summary>两张同尺寸灰度帧的平均绝对差(0~255 量级)。长度不等按较短者算(防御:宁可少比也不错位)。</summary>
    public static double MeanAbsDiff(byte[] a, byte[] b)
    {
        if (a == null || b == null) return 0;
        int n = Math.Min(a.Length, b.Length);
        if (n <= 0) return 0;
        long sum = 0;
        for (int i = 0; i < n; i++) sum += Math.Abs(a[i] - b[i]);
        return (double)sum / n;
    }

    /// <summary>4 邻域拉普拉斯响应的方差(清晰度/结构能量的常用代理)。
    /// 核 = [0 1 0; 1 -4 1; 0 1 0],只统计内点(不补边,避免人为压低外圈能量)。
    /// 图小于 3×3 或缓冲长度不足 → null(= 拿不到结构能量;SceneCutJudge 对 null 的处置是"保守判切")。</summary>
    public static double? LapVar(byte[] gray, int w, int h)
    {
        if (gray == null || w < 3 || h < 3 || gray.Length < w * h) return null;
        long sum = 0;
        double sumSq = 0;
        int cnt = 0;
        for (int y = 1; y < h - 1; y++)
        {
            int row = y * w;
            for (int x = 1; x < w - 1; x++)
            {
                int i = row + x;
                int lap = 4 * gray[i] - gray[i - 1] - gray[i + 1] - gray[i - w] - gray[i + w];
                sum += lap;
                sumSq += (double)lap * lap;
                cnt++;
            }
        }
        if (cnt <= 0) return null;
        double mean = (double)sum / cnt;
        double var = sumSq / cnt - mean * mean;
        return var > 0 ? var : 0;
    }

    /// <summary>一对相邻采样帧的度量:帧差 + 前后帧各自的拉普拉斯能量(供 <see cref="SceneCutJudge.IsCut"/>)。
    /// 尺寸不一致(探测/拆帧异常)→ 帧差按较短缓冲算、拉普拉斯沿用较小尺寸,不抛异常(判切失败只等于"不保护")。</summary>
    public static void MeasurePair(byte[] prev, byte[] cur, int w, int h,
        out double diff, out double? lapPrev, out double? lapCur)
    {
        diff = MeanAbsDiff(prev, cur);
        lapPrev = LapVar(prev, w, h);
        lapCur = LapVar(cur, w, h);
    }
}
