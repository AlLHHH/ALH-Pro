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
    // ===== 阶段顺序的【单一事实来源】(2026-09-13 新增,任务 H)=====
    // 【为什么要它】"1x/2x 走「超分 → 补帧」新顺序"这个判据一度被写在两处:VideoService 的管线里一处、
    // UI 的 ETA 估算里又各写一份(而且写法还不一样)。2026-09-13 管线侧实测回退成旧顺序后,
    // UI 忘了跟着改 → **界面上给的预计时间在算一个根本不会执行的顺序**,这正是"预计时间不准"的来源之一。
    // 现在:是否走新顺序只有下面这一个开关 + 一个纯函数,管线和 UI 都必须走它,不许各写一份。

    /// <summary>「超分 → 补帧」新顺序总开关。【当前 = false(已回退)】。
    /// 【为什么回退】2026-09-13 用户真机日志(3 秒 / 72 帧 / 1080p / realesr-animevideov3 2x / rife-v4.13 4x):
    /// 超分实测 72 帧 20.1s = 279 ms/帧(开发期 harness 测到的 0.70 s/帧高估了约 2.5 倍,而"先超分更划算"
    /// 的全部依据就是"超分贵"),补帧搬到 3840×2160 后只有 1.83 帧/秒 → 该作业净亏,且随帧数线性增长。
    /// 【重新启用前必须做的两件事】① 用 SafeRender.GetEngineThreadArgs() 同款线程参数实测
    /// "补帧倍率 × 超分倍率 × 片长"矩阵;② 按单帧成本之比(而非"帧数超过多少")给门限。
    /// 另外:启用时还要同步改 StageProgressPct 的超分区间与各处进度区间,否则进度条会先跳后倒退。</summary>
    public const bool UpscaleFirstEnabled = false;

    /// <summary>回退原因(写进日志/诊断用,单一来源,避免各处各写一句)。</summary>
    public const string UpscaleFirstDisabledReason =
        "新顺序「超分 → 补帧」已于 2026-09-13 实测回退(4x 补帧/短视频上净亏:超分实测 279 ms/帧而非预估的 0.70 s/帧)," +
        "当前一律按旧顺序「补帧 → 超分」执行";

    /// <summary>本次任务实际是否走「超分 → 补帧」新顺序(纯函数,单一事实来源:管线与 ETA 都必须调它)。
    /// 条件与回退前的原判据一致,只是多了总开关:
    ///   ① 总开关开启(当前恒 false);② 真的要补帧;③ 超分阶段真的会执行(1x 缩回也算执行);
    ///   ④ 超分倍数 ≤ 2.001(3x/4x 等一律旧顺序 —— 补帧单价随分辨率涨得比超分快,先补帧能把补帧按便宜价跑)。
    /// 【为什么把"超分是否执行"也收进来】VideoService 里原本用 `doUpscale && !(scale <= 1.001 && !shrink1x)`
    /// 算 upscaleRuns,这里按同一口径复刻,避免两处条件漂移。</summary>
    public static bool UpscaleRunsFirst(bool up, double scale, bool interp, bool shrink1x = false)
    {
        // 总开关关着 → 一律旧顺序(当前恒走这一行:上面 UpscaleFirstEnabled = false)。
        if (!UpscaleFirstEnabled) return false;
        // 【下面这段是"开关打开之后"的真实判据,必须留着】与回退前的原判据逐字一致:
        //   ① 真的要补帧;② 超分阶段真的会执行(1x 缩回也算执行);③ 超分倍数 ≤ 2.001。
        // 因为 UpscaleFirstEnabled 是 const,编译器会把这里判成"不可达代码"(CS0162)——
        // 这是刻意的(代码要为"重新启用"保留),用 pragma 就地抑制,免得给构建新增警告。
#pragma warning disable CS0162
        bool upscaleRuns = up && !(scale <= 1.001 && !shrink1x);   // 与 VideoService 的 upscaleRuns 同口径
        return interp && upscaleRuns && !(scale > 2.001);
#pragma warning restore CS0162
    }
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
            int srcFrames = uniqueFrames > 0 ? uniqueFrames : src;
            // 【批数口径必须与真正执行的顺序一致】
            //  · 旧顺序(补帧→超分,当前启用):超分阶段读的是【补帧输出】→ 批数按"补帧后总帧数"算;
            //  · 新顺序(超分→补帧,当前关闭):超分阶段读的是【源帧】、补帧在放大后的帧上跑 →
            //    两侧帧数/分辨率都不同,走 PlanUpscaleFirstBatches 单独算(取它的超分侧批数)。
            int batchCount;
            if (upFirst && interp && interpScale > 1)
                batchCount = RenderPolicy.PlanUpscaleFirstBatches(freeRamGB, srcFrames, scale, interpScale).UpscaleBatchCount;
            else
            {
                int postInterp = (interp && interpScale > 1) ? srcFrames * interpScale : srcFrames;
                batchCount = RenderPolicy.PlanVideoBatches(freeRamGB, srcFrames, postInterp).BatchCount;
            }
            s += batchCount * AssumedEngineStartupSecondsPerBatch;
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

    // ===== 【任务 N · 2026-09-13】补帧"帧数守恒"与 VFR 时间轴的时基(纯逻辑,全部可单测) =====
    //
    // 真机现象(2x 超分 + 2x 补帧 + 智能去重 + VFR 自动,855 帧 VFR 素材):
    //   日志自称"输出 1710 帧 / 编码 帧数=1710",`ffprobe -count_frames` 实测 **nb_frames=1646** ——
    //   少了 64 帧,恰好等于成片里"双倍长间隔"的个数(2x 补帧实际只有 1.925x),软件自己的输出校验也告警
    //   「帧率 55.52 vs 预期 57.64」(55.52 = 1646 / 29.6667)。
    // 根因(定量对得上):**setpts 的量化格子 = image2 输入的时基 = 1/`-framerate`**,而当时的标称帧率
    //   fr = 帧数 ÷ 时长 = 1710/29.6667 = **57.64**,即一格 0.017349s;VFR 时间轴里"最短的一格"
    //   (1/60 × 归一化 ≈ **0.016686s**)**比一格还短** → 相邻两帧被折算到同一个时基整数格
    //   (重复 PTS)→ 被编码/封装丢掉。丢帧数 = 帧数 ×(1 − 时长/一格)= 1710 ×(1 − 0.016686×57.64)
    //   ≈ **65 ≈ 实测 64**;成片中那 64 个"双倍长间隔"就是丢帧留下的空档。
    // 结论:**VFR(带时长表)时,输入时基必须比"最短帧时长"更细**,否则时间轴再正确也留不住帧。
    // 下面 CountTimestampCollisions 就是这条判据的可执行形式(在真机上没跑之前,它先用纯数学复算对账)。

    /// <summary>补帧的"每源帧展开帧数"倍率 mult = round(倍率 × 密度还原系数)。
    /// RIFE 每段就是按 `-n = 段长 × mult` 产出,时长表也必须按同一个 mult 展开;
    /// 旧实现用 `interpScale` 展开,只有 frameScale==1(未去重)时才自洽。</summary>
    public static int InterpMultiplier(double interpScale, double frameScale)
    {
        double m = interpScale * frameScale;
        if (!double.IsFinite(m) || m < 1) return 1;
        return Math.Max(1, (int)Math.Round(m));
    }

    /// <summary>一段补帧输入应产出的帧数。**末段用"自然产量" (段长-1)×mult+1**:
    /// RIFE 被要求产出 `段长×mult` 时,最后 1 帧是把末帧复制出来的"冻结帧"(它的存在只为让最后一段
    /// 得到真实插值,最终靠合帧前的"帧数对齐"裁掉)。VFR 路径不做帧数对齐(要保时间轴),于是整片会
    /// 比设计目标多 1 帧(真机实测 1710 vs (855-1)×2+1=1709)—— 这里直接从源头不产出它。
    /// 【为什么非末段仍按 段长×mult】各段是拼起来的:Σ(非末段 L×mult) + ((末段 L-1)×mult+1)
    /// = (总帧数-1)×mult+1,与整片目标严格相等(见 InterpOutputFrameCount 与单测)。</summary>
    public static int InterpSegmentTarget(int segLen, int mult, bool lastSegment)
    {
        int m = Math.Max(1, mult);
        if (segLen <= 1) return m > 1 ? m : 1;
        return lastSegment ? (segLen - 1) * m + 1 : segLen * m;
    }

    /// <summary>整片补帧输出的目标帧数 = (源帧数-1)×mult+1(帧数守恒)。
    /// A 拍 N 素材 30fps 源、2x 补帧:855 帧 → 1709 帧(= 用户的验收式)。</summary>
    public static long InterpOutputFrameCount(int sourceFrames, int mult)
    {
        if (sourceFrames <= 0) return 0;
        return (long)(sourceFrames - 1) * Math.Max(1, mult) + 1;
    }

    /// <summary>把源帧时长表按补帧倍率展开成"输出帧时长表"(供 setpts 重定时),返回追加的条目数。
    /// 每源帧展开 mult 条、每条 = 该源帧时长 / mult(总时长严格不变);
    /// **末段的最后一个源帧只展开 1 条**,承载它(尾部容积)的整段时长 —— 与 InterpSegmentTarget 配套,
    /// 保证"表长 == 文件数 == (源帧数-1)×mult+1"。</summary>
    public static int AppendExpandedDurations(List<double> dst, IReadOnlyList<double> srcDurs, int s, int e, int mult, bool lastSegment)
    {
        if (dst == null) return 0;
        int m = Math.Max(1, mult);
        int added = 0;
        for (int k = Math.Max(0, s); k < Math.Min(e, srcDurs.Count); k++)
        {
            bool tailFrame = lastSegment && k == srcDurs.Count - 1;
            int copies = tailFrame ? 1 : m;
            double d = Math.Max(0.0005, srcDurs[k] / copies);
            for (int i = 0; i < copies; i++) { dst.Add(d); added++; }
        }
        return added;
    }

    /// <summary>VFR(setpts)时 image2 输入该用的 `-framerate`:保证一个时基格 ≤ 最短帧时长的一半。
    /// 时基格 = 1/framerate,而 setpts 会把"目标秒数 ÷ 时基"折算成整数 → 格子比最短帧还粗时,
    /// 相邻两帧撞进同一格(重复 PTS)→ 被丢掉(真机实测 1710 → 1646)。取 2 倍安全系数:
    /// 每帧至少推进 2 格,量化抖动只占帧时长的 25% 以下且不累积(每帧的目标时间是绝对量)。
    /// 返回 ceil,并保证不低于标称帧率(不许把时基变粗)——CFR 路径不调用它。</summary>
    public static double VfrInputFramerate(IReadOnlyList<double> durs, double nominalFps)
    {
        double minD = double.MaxValue;
        if (durs != null)
            foreach (var d in durs)
                if (d > 0 && double.IsFinite(d) && d < minD) minD = d;
        double byDur = minD == double.MaxValue ? 0 : 2.0 / minD;
        double nom = nominalFps > 0 && double.IsFinite(nominalFps) ? nominalFps : 1.0;
        return Math.Clamp(Math.Ceiling(Math.Max(nom, byDur)), 1.0, 100000.0);
    }

    /// <summary>按 ffmpeg setpts 的换算法模拟"每一帧落在哪个时基格",返回**与前帧撞格**(PTS 相同,
    /// 会被编码/封装丢掉)的帧数。0 = 这套时间轴在此时基下不会丢帧。
    /// 与 BuildVfrSetptsExpr 逐字同口径:相邻时长差 &lt;1e-5 视为同一段,段内
    /// 目标时间 = 段起点累计 + (帧序号 − 段首)×段时长,再除以时基 1/framerate 取整数(截断)。
    /// 【用途】两个:①新增实现在选输入时基前先自检(有撞格就再细化);②单测拿真机那组时长表复算,
    /// 与实测"丢 64 帧"对账(有它就不必靠猜)。</summary>
    public static int CountTimestampCollisions(IReadOnlyList<double> durs, double framerate)
    {
        if (durs == null || durs.Count < 2 || !(framerate > 0) || !double.IsFinite(framerate)) return 0;
        // 非法时长(NaN/±Inf/非正)一律不判:这种表根本到不了 setpts —— BuildVfrSetptsExpr 的入口守卫
        // 已经把它挡回"均匀时间轴"了。这里按同一契约返回 0,避免用垃圾值算出一堆假"撞格"。
        foreach (var d in durs)
            if (!double.IsFinite(d) || d <= 0) return 0;
        double tb = 1.0 / framerate;
        int collisions = 0;
        bool havePrev = false;
        long prev = 0;
        int segStart = 0;
        double segDur = durs[0];
        double acc = 0;
        for (int n = 0; n < durs.Count; n++)
        {
            if (n > segStart && Math.Abs(durs[n] - segDur) >= 1e-5)
            {
                acc += segDur * (n - segStart);
                segStart = n;
                segDur = durs[n];
            }
            double t = acc + (n - segStart) * segDur;
            long pts = (long)(t / tb);
            if (havePrev && pts == prev) collisions++;
            prev = pts;
            havePrev = true;
        }
        return collisions;
    }
}
