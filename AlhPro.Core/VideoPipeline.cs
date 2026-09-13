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
    /// <summary>每批超分引擎进程的"启动 + 模型加载"固定开销(秒/批)【待实测标定】。
    /// 【依据(本机只读日志,2026-09-13)】
    ///  · 19:00:16→19:00:33 单批 72 帧:引擎启动 → 引擎完成 = 17.1s(整阶段 20.1s / 72 帧 = 279 ms/帧);
    ///  · 同一晚 19:12 那一轮(同样 realesrgan 2x、240 帧/批):每批引擎耗时 15.7~16.4s(≈67 ms/帧),
    ///    而只剩 60 帧的末批只要 4.1s —— 说明"帧数少但耗时并不等比下降",固定开销客观存在;
    ///  · 现有日志无法把它精确分离:批次是并发跑的(2~3 批在飞),引擎/驱动的着色器缓存冷热不同,
    ///    各轮的模型与倍率也不一样。两个观测只能把它夹在"热启动 ≈ 0.1s"与"冷启动 ≈ 10s"之间。
    /// 【取值】3.0s = 上述区间的保守中值,宁可把"长素材偏乐观"的偏差收掉一部分,也不虚报精度。
    /// 【验证方式(真机空闲后)】同一素材跑两种批大小(如 240 帧/批 vs 60 帧/批),用
    /// "引擎完成"日志的耗时做两点线性拟合(批数 × 启动开销 + 帧数 × 每帧成本)即可标定。
    /// 未标定前不要把本常数当实测值引用。</summary>
    public const double AssumedEngineStartupSecondsPerBatch = 3.0;

    /// <summary>估算整个视频处理流程的大致秒数(用于处理前"预计剩余时间")。slowFactor=弱机放大系数(默认 1)。
    /// upscaleFirst=「超分 → 补帧」的新阶段顺序(1x/2x 走这条,见 VideoService.ProcessVideoAsync 的阶段顺序说明):
    /// 超分先按【源帧数】跑,补帧在放大后的帧上做(单价按放大后的面积算)。
    /// 默认 false = 旧顺序「补帧 → 超分」,与改动前逐字一致;scale&gt;2.001(4x 等)在函数内一律钳回旧顺序
    /// —— 阶段顺序只对 1x/2x 生效,钳住可保证"估算口径"与真正执行的顺序不会各说各话。
    /// freeRamGB/uniqueFrames=【2026-09-13 新增】批数与每批帧数的入参(走 RenderPolicy.PlanVideoBatches,
    /// 与真正执行时同一套规则):freeRamGB&lt;=0 = 不知道内存档 → 不加批启动开销(保持旧口径,不猜);
    /// uniqueFrames&lt;=0 = 还没去重 → 按源帧数估。</summary>
    public static double EstimateProcessSeconds(double duration, double fps, int w, int h,
        bool up, double scale, string engine, bool interp, int interpScale, bool dedup, int videoDenoise,
        double slowFactor = 1.0, bool upscaleFirst = false,
        double freeRamGB = 0, int uniqueFrames = 0)
    {
        int src = (int)Math.Max(1, duration * fps);
        double s = src * 0.02 + 1.5;                 // 拆帧(含引擎启动)
        if (dedup) s += Math.Max(1.5, src * 0.010);   // 去重检测(随帧数)
        int frames = src;
        // 补帧/超分的每帧成本按面积缩放(基准 1080p=2073600):固定常数会让 4K/大图严重低估
        double areaN = Math.Max(0.25, (double)w * h / 2073600.0);
        // 阶段顺序只对 1x/2x 成立(4x 及任何 scale>2.001 保持旧顺序):调用方传错倍数时在这里钳住。
        bool upFirst = upscaleFirst && !(scale > 2.001);
        if (interp && interpScale > 1)
        {
            // 整段一次 RIFE 成本 ≈ 【新增】帧数 × 每帧(按面积)。
            // 【2026-09-13 修单位 bug】原来写 frames * interpScale(等于按"输出帧数"算),而 RIFE 只为
            // 【新增】的帧做推理:N 帧做 k 倍补帧 → 新增 (k-1)N 帧、输出 kN-1 帧。
            // 于是旧写法在 k=2 时把成本高估 2 倍、k=4 时高估 1.33 倍,高倍率补帧的 ETA 被显著拉长。
            // 常数 0.09 秒/帧(1080p)保持不动 —— 本机批量实测 RIFE v4.13 ≈0.102 秒/输出帧,同量级。
            // 新增帧数两种顺序都一样(超分保帧数)= src × (k-1);变的是【每帧单价】。
            double add = Math.Max(1, interpScale - 1) * src * 0.09;
            if (upFirst && up && scale > 1.001)
            {
                // 新顺序:补帧在【超分后的帧】上做 → 单价按放大后的面积算(实测 1080p 0.10 → 2160p 0.35 秒/输出帧,
                // 与 scale²=4 倍的面积量级相符);超分则只跑源帧数(见下面 up 分支)。
                s += add * areaN * scale * scale;
            }
            else
            {
                s += add * areaN;
            }
            frames *= interpScale;
        }
        if (up && scale > 1.001)
        {
            // 超分逐帧成本:1080p 单帧 waifu2x≈0.18s / realesrgan≈0.45s,按面积缩放
            double per = engine switch { "waifu2x" => 0.18, _ => 0.45 };
            per *= areaN * Math.Max(0.5, scale / 1.0);
            // 新顺序:超分只跑【源帧数】(补帧排在超分之后,不再让超分帧数翻倍)——这是新顺序省钱的全部来源。
            // 旧顺序:超分跑补帧后的帧数 frames(= src × 倍率),与改动前逐字一致。
            s += (upFirst && interp && interpScale > 1 ? src : frames) * per;
        }
        if (videoDenoise > 0) s *= 1.05;              // 降噪滤镜
        // ===== 每批引擎启动的固定开销(2026-09-13 新增)=====
        // 超分/补帧都是【按批】重新起一次引擎进程(每批一次进程启动 + 模型加载),而上面的公式只有"每帧成本";
        // 素材一长就被切成十几批(批数 = ⌈唯一帧数 ÷ 每批帧数⌉),这笔固定开销完全没进估算 →
        // 长素材的预计时间系统性偏乐观(用户看到"还剩 5 分钟"却跑了半小时),而且批数越多偏得越狠。
        // 【只在水/内存档可知时计入】不知道空闲内存就不知道批大小、更算不出批数,硬套一个默认档位等于编数据;
        // freeRamGB<=0 时保持旧口径(调用方没传 = 老行为,一字不变)。
        // 【为什么只算超分批、不算补帧段】补帧的分段取决于转场识别/去重结果(段数在估算时还不知道),
        // 这里只能覆盖"超分批"这一笔 —— 所以本项仍是【下限】,不是完整开销(见 AssumedEngineStartupSecondsPerBatch)。
        if (up && scale > 1.001 && freeRamGB > 0)
        {
            int uniq = uniqueFrames > 0 ? uniqueFrames : src;
            var plan = RenderPolicy.PlanVideoBatches(freeRamGB, uniq);
            s += plan.BatchCount * AssumedEngineStartupSecondsPerBatch;
        }
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

    // ===== VFR 时间轴的两个决策(2026-09-13 修「可变帧率静默失效」时抽出,纯函数、有单测)=====
    // 【事故】原代码在"智能去重-未采用拍数识别"分支里【无条件】frameDurs = null —— 即使素材是 VFR
    // (vfrPassthrough=true)也把源时长表丢掉;而下游 preserveRhythm 仍为 true,于是合帧只好静默造一张
    // 均匀表 → 成片退化成纯 CFR(源可变时间轴 100% 丢失:成片变速 + 画面相对声音最大滞后数百 ms),
    // 日志却照打「时长保护(VFR) … vfrSetpts=有」。
    // 把"建不建表""有没有真的用上表"抽成下面两个纯函数,就是为了让这类静默退化不可能再复发(有测试)。

    /// <summary>拆帧阶段:是否需要"源帧时长表"。【需要 = 素材是 VFR(要保留可变节奏)或开着去重
    /// (删帧后必须靠表把被删帧的时长归并回保留帧,否则时间轴被压缩 → 变速)】。
    /// 【为什么条件里必须带 vfrPassthrough】这正是事故点:VFR 素材 + 该分支"一帧不删"时,旧代码仍把表丢掉,
    /// 合帧拿不到源节奏 → 静默 CFR。带上它 = 只要判定是 VFR 就一定建表。</summary>
    public static bool NeedsFrameDurations(bool dedup, bool vfrPassthrough) => dedup || vfrPassthrough;

    /// <summary>合帧阶段:是否真的用上了 VFR 时间轴。【preserveRhythm=false 或没有可用时长表 → false】
    /// 注意 `preserveRhythm && !UsesVfrTimeline(...)` = 【回退均匀时间轴】:调用方必须打 warn 并把
    /// 「时长保护(VFR)」「可变帧率时间轴」这类文案去掉 —— 否则日志和界面都在骗用户(事故的第二个成因)。</summary>
    public static bool UsesVfrTimeline(bool preserveRhythm, int finalDursCount) => preserveRhythm && finalDursCount > 0;

    /// <summary>帧间隔(VFR)统计:把"一组相邻帧的 PTS 间隔"浓缩成可判定的几个数(纯函数,有单测)。
    /// 判定规则与 2026-09-13 之前 VideoService.ProbeVfrAsync 抽查分支里的内联写法【逐字一致】
    /// (先 maxG &gt; minG×1.5 且 maxG &gt; 0.5ms,否则看变异系数 cv &gt; 0.25)—— 抽出来只是为了让
    /// 判定可测、并把 maxG/minG/cv 写进日志(旧代码只在命中时打一行,漏判时什么都看不到)。
    /// 【为什么需要它】手机/录屏素材的 r_frame_rate÷avg_frame_rate 可能只有 1.04(远离 2.0 门槛),
    /// 单靠比值信号会漏判;而"间隔里散着若干个双倍长的间隔"这类形态用 CV / max-min 比一看就出来 ——
    /// 前提是这段间隔落在被抽查的窗口里(全片直方图见报告里的方案,未实现)。</summary>
    public readonly record struct FrameGapStats(int Count, double MinGap, double MaxGap, double AvgGap,
        double MaxOverMin, double Cv, bool VfrByRatio, bool VfrByCv)
    {
        /// <summary>是否判为可变帧率(任一路命中)。间隔样本少于 7 个时一律 false(帧太少无法判断)。</summary>
        public bool IsVfr => VfrByRatio || VfrByCv;

        /// <summary>一句话诊断(写日志用):把判定依据的原始数字都带上,便于复盘为什么判/没判。</summary>
        public string Summary =>
            $"间隔样本 {Count},min {MinGap * 1000:0.##}ms,max {MaxGap * 1000:0.##}ms,均值 {AvgGap * 1000:0.##}ms,"
            + $"max/min {MaxOverMin:0.##},cv {Cv:0.###} → {(IsVfr ? "VFR" : "CFR")}"
            + $"(比值判据 {VfrByRatio},cv 判据 {VfrByCv})";
    }

    /// <summary>帧间隔直方图式判定(见 FrameGapStats 的说明)。gaps 为相邻帧 PTS 差(秒,升序)。
    /// 非正/非有限(NaN)的间隔一律忽略(与调用点的 `if (g &gt; 0)` 过滤同口径),剩下的样本少于 7 个 → 一律判 CFR。</summary>
    public static FrameGapStats AnalyzeFrameGaps(IReadOnlyList<double> gaps)
    {
        var g2 = new List<double>();
        if (gaps != null)
            foreach (var g in gaps)
                if (g > 0 && double.IsFinite(g)) g2.Add(g);
        if (g2.Count < 7) return new FrameGapStats(g2.Count, 0, 0, 0, 0, 0, false, false);
        double minG = g2[0], maxG = g2[0], sum = 0;
        foreach (var g in g2)
        {
            if (g < minG) minG = g;
            if (g > maxG) maxG = g;
            sum += g;
        }
        double avgG = sum / g2.Count;
        double maxOverMin = minG > 0 ? maxG / minG : double.PositiveInfinity;
        double varSum = 0;
        foreach (var g in g2) { double d = g - avgG; varSum += d * d; }
        double cv = avgG > 0 ? Math.Sqrt(varSum / g2.Count) / avgG : 0;
        bool byRatio = maxG > minG * 1.5 && maxG > 0.0005;   // 0.5ms 以下的抖动忽略(噪声)
        bool byCv = cv > 0.25;                              // 间隔波动 >25% → 视为可变帧率
        return new FrameGapStats(g2.Count, minG, maxG, avgG, maxOverMin, cv, byRatio, byCv);
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
