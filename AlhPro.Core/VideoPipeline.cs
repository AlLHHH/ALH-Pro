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
            // 整段一次 RIFE 成本 ≈ 【新增】帧数 × 每帧(按面积)。
            // 【2026-09-13 修单位 bug】原来写 frames * interpScale(等于按"输出帧数"算),而 RIFE 只为
            // 【新增】的帧做推理:N 帧做 k 倍补帧 → 新增 (k-1)N 帧、输出 kN-1 帧。
            // 于是旧写法在 k=2 时把成本高估 2 倍、k=4 时高估 1.33 倍,高倍率补帧的 ETA 被显著拉长。
            // 常数 0.09 秒/帧(1080p)保持不动 —— 本机批量实测 RIFE v4.13 ≈0.102 秒/输出帧,同量级。
            s += Math.Max(1, interpScale - 1) * frames * 0.09 * areaN;
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

    /// <summary>由时长表生成 ffmpeg VFR setpts 表达式(合并相邻相同时长段;段数&gt;400 返回 null=回退 CFR)。
    /// 时长表里出现非有限值(NaN/±Inf)或非正值时一律返回 null=回退 CFR:这不是"精度差一点"，
    /// 而是会让本函数的合并循环永不推进(见下方守卫注释)。</summary>
    public static string? BuildVfrSetptsExpr(List<double> durs)
    {
        try
        {
            // 入口守卫:非有限/非正的时长直接回退 CFR(本函数注释承诺的兜底行为)。
            // 为什么必须在入口拦:合并循环用 Math.Abs(durs[i] - d) < 1e-5 判定"同一段",而
            // Math.Abs(NaN - d) < 1e-5 恒为假 → i 永不推进,外层 while (i < durs.Count) 永不退出,
            // 每轮还往 segs 里塞一段 → 无界增长到 OOM。这不是异常,末尾的 catch 拦不住,
            // 调用方的看门狗和"停止"按钮也救不回来(进程直接挂死)。NaN/±Inf 一旦从某个新探测源
            // 传进来就是必挂,所以这里按契约直接回退,而不是试图"算出一个近似结果"。
            foreach (var x in durs)
                if (!double.IsFinite(x) || x <= 0) return null;

            // 合并相邻相同时长成段(±1e-5 视为相同)
            var segs = new System.Collections.Generic.List<(int s, int e, double p0, double d)>();
            int i = 0;
            double acc = 0;
            while (i < durs.Count)
            {
                int s = i;
                double d = durs[i];
                while (i < durs.Count && Math.Abs(durs[i] - d) < 1e-5) i++;
                // 保险:即便将来有人改宽上面的守卫(或把容差换成相对判据),也必须保证 i 单调前进 ——
                // 否则这里又变回"每轮加一段、i 不动"的死循环。
                if (i == s) i++;
                segs.Add((s, i, acc, d));
                // 段数上限放进循环内:原先放在循环之后,一旦合并循环退化,这个判断永远到不了,
                // 等于没有上限。放在这里保证它一定可达,且超限时立即返回、不再继续建表。
                if (segs.Count > 400) return null;   // 超长:回退 CFR(避免 setpts 命令超命令行长度)
                acc += d * (i - s);
            }
            if (segs.Count == 0) return null;
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder("setpts=(");
            for (int k = 0; k < segs.Count; k++)
            {
                var (s, e, p0, d) = segs[k];
                if (k > 0) sb.Append(" + ");
                // 末段只留 gte(N,s)、去掉 lt(N,e) 上界:setpts 位于滤镜链末尾,其前面还有
                // minterpolate/fps 重采样,送进来的帧数比时长表多 1 是常态。而末段的 e 就是
                // durs.Count,多出来的那一帧会让【所有】段项都为 0 → PTS=0(与首帧同刻),
                // 播放器把它当重复时间戳丢掉 → 成片末尾少一截/抖一下。各段条件本身互斥
                // (前面各段仍带 lt 上界),所以末段去掉上界不会与它们重叠。
                bool last = k == segs.Count - 1;
                string range = last ? $"gte(N\\,{s})" : $"lt(N\\,{e})*gte(N\\,{s})";
                sb.Append($"({range}*({p0.ToString("0.######", inv)}+(N-{s})*{d.ToString("0.######", inv)}))");
            }
            sb.Append(")/TB");
            return sb.ToString();
        }
        catch { return null; }
    }
}
