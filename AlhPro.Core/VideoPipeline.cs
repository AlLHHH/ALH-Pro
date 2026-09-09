using System.Globalization;
using System.Text;

namespace AlhPro.Core;

/// <summary>
/// 视频管线纯函数(从 VideoService 抽出、可单测)。
/// 这些是去重/补帧/超分/估算里"输入若干数字 → 输出若干结果"的纯逻辑——改错会直接导致
/// 补帧帧数不对/时长表错位/估算离谱,最需要测试保护。ffmpeg 子进程调用等留在 VideoService。
/// </summary>
public static class VideoPipeline
{
    /// <summary>估算整个视频处理流程的大致秒数(用于处理前"预计剩余时间")。slowFactor=弱机放大系数(默认 1)。</summary>
    public static double EstimateProcessSeconds(double duration, double fps, int w, int h,
        bool up, double scale, string engine, bool interp, int interpScale, bool dedup, int videoDenoise,
        double slowFactor = 1.0)
    {
        int src = (int)Math.Max(1, duration * fps);
        double s = src * 0.02 + 1.5;                 // 拆帧(含引擎启动)
        if (dedup) s += Math.Max(1.5, src * 0.010);   // 去重检测(随帧数)
        int frames = src;
        // 补帧/超分的每帧成本按面积缩放(基准 1080p=2073600):固定常数会让 4K/大图严重低估
        double areaN = Math.Max(0.25, (double)w * h / 2073600.0);
        if (interp && interpScale > 1)
        {
            // 整段一次 RIFE 成本 ≈ 输出帧数 × 每帧(按面积)
            s += frames * Math.Max(2, interpScale) * 0.09 * areaN;
            frames *= interpScale;
        }
        if (up && scale > 1.001)
        {
            // 超分逐帧成本:1080p 单帧 waifu2x≈0.18s / realesrgan≈0.45s,按面积缩放
            double per = engine switch { "waifu2x" => 0.18, _ => 0.45 };
            per *= areaN * Math.Max(0.5, scale / 1.0);
            s += frames * per;
        }
        if (videoDenoise > 0) s *= 1.05;              // 降噪滤镜
        s += frames * 0.12;                           // 合成编码(平均)
        if (slowFactor > 1) s *= slowFactor;          // 弱机(CPU 兜底)明显更慢
        return s * 1.15;                              // 略保守:从大往小对齐,不从小变大
    }

    /// <summary>VFR 判定的免解码信号:r_frame_rate ÷ avg_frame_rate。
    /// CFR 素材两者相等(比值 1.00);VFR 素材的 r_frame_rate 是"能精确表示全部时间戳的最低帧率",
    /// 只要存在一对相邻帧间隔极小就会被抬得很高。实测:CFR 30fps → 1.00;合成 VFR(r=60/avg=25.4)→ 2.36;
    /// 真机录屏(r≈96000/avg≈30)→ ≈3200。门槛取 2.0:远低于录屏/突发型 VFR,又高于普通手机 VFR 的轻微抖动
    /// (典型 r=30/avg=28 → 1.07),避免把基本均匀的素材误判成 VFR 而改掉输出时间轴口径。</summary>
    public const double VfrRateRatioThreshold = 2.0;

    /// <summary>任一帧率无效(0/缺失)→ 不作判定(false),交由逐帧 PTS 抽查决定。</summary>
    public static bool IsVfrByRateRatio(double rFrameRate, double avgFrameRate)
    {
        if (rFrameRate <= 0 || avgFrameRate <= 0) return false;
        return rFrameRate / avgFrameRate >= VfrRateRatioThreshold;
    }

    /// <summary>合并被删帧的时长到其前面最近的保留帧(逐条前移;durs 会被原地修改)。</summary>
    public static void MergeDurations(List<double> durs, System.Collections.Generic.IEnumerable<int> dropped, int totalCount)
    {
        var dropSet = dropped as System.Collections.Generic.HashSet<int>
            ?? new System.Collections.Generic.HashSet<int>(dropped);
        int actual = Math.Min(durs.Count, totalCount);
        for (int i = actual - 1; i >= 0; i--)
        {
            int frameNo = i + 1;
            if (!dropSet.Contains(frameNo)) continue;
            // 找"前面最近的保留帧"(若前面连续都是被删帧则递推到更前)
            int k = i - 1;
            while (k >= 0 && dropSet.Contains(k + 1)) k--;
            if (k >= 0 && k < durs.Count && k < i) durs[k] += durs[i];
            durs.RemoveAt(i);
        }
    }

    /// <summary>由时长表生成 ffmpeg VFR setpts 表达式(合并相邻相同时长段;段数&gt;400 返回 null=回退 CFR)。</summary>
    public static string? BuildVfrSetptsExpr(List<double> durs)
    {
        try
        {
            // 合并相邻相同时长成段(±1e-5 视为相同)
            var segs = new System.Collections.Generic.List<(int s, int e, double p0, double d)>();
            int i = 0;
            double acc = 0;
            while (i < durs.Count)
            {
                int s = i;
                double d = durs[i];
                while (i < durs.Count && Math.Abs(durs[i] - d) < 1e-5) i++;
                segs.Add((s, i, acc, d));
                acc += d * (i - s);
            }
            if (segs.Count == 0) return null;
            if (segs.Count > 400) return null;   // 超长:回退 CFR(避免 setpts 命令超命令行长度)
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder("setpts=(");
            bool first = true;
            foreach (var (s, e, p0, d) in segs)
            {
                if (!first) sb.Append(" + ");
                first = false;
                sb.Append($"(lt(N\\,{e})*gte(N\\,{s})*({p0.ToString("0.######", inv)}+(N-{s})*{d.ToString("0.######", inv)}))");
            }
            sb.Append(")/TB");
            return sb.ToString();
        }
        catch { return null; }
    }
}
