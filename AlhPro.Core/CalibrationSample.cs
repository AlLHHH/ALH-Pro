namespace AlhPro.Core;

/// <summary>「超分引擎每帧成本」的**两点法**标定算式(纯逻辑,不做 IO、不起进程)。
///
/// ==== 为什么要"两点" ====
/// 直接跑一次目录批跑取"总时长 ÷ 帧数"是错的:每**进程**有一次固定地板
/// (<c>PipelineOrderPlan</c> 类注释里的实测:1 帧目录 1.01~1.19 s/帧,其中固定地板 0.75~0.92 s;
/// 40 帧目录批跑只有 0.25~0.30 s/帧 —— **地板约为单帧成本的 3 倍**)。
/// 于是测两次:1 帧一次(t1)、N 帧一次(tN),记 p=每帧可变成本、F=每进程地板 :
///     t1 = F + 1·p
///     tN = F + N·p
/// 相减 ⇒ **p = (tN − t1) / (N − 1)**,再代回 ⇒ **F = t1 − p**。
/// 两次都跑**同一个引擎进程**吗?不是 —— 两次是各自独立的进程启动,所以 F 里含两次自己的地板;
/// 但只要地板**可复现**,相减就把"固定部分"整体消掉,剩下的正是随帧数增长的那部分,这就是单价。
/// 【已知限制(必须写进文档)】两次进程启动之间笔记本 GPU 会热降频,同机多次测量的地板可差到 1.9 倍,
/// 所以 p 是**当前热状态下的快照**,不是物理常数。
///
/// ==== 采样帧数怎么选 ====
/// 样本差值要显著大于噪声:N 太小 ⇒ (tN−t1) 被地板抖动吃掉;N 太大 ⇒ 标定本身要花掉几十秒。
/// <see cref="FramesFor"/> 用内置单价(他机实测)估一个每帧成本,把"标定总预算 ≈ <see cref="BudgetSeconds"/> 秒"
/// 摊成帧数,并夹在 [<see cref="MinFrames"/>, <see cref="MaxFrames"/>] 内。</summary>
public static class CalibrationSample
{
    /// <summary>标定预算(秒):用户多点一次"开始",最多多等这么久。
    /// 【口径】30 s 是"不打扰用户"与"样本要够大"之间的折中(120 帧素材上约占 1/4 的处理时间里的一小段)。</summary>
    public const double BudgetSeconds = 30.0;

    /// <summary>最少采样帧数 = <see cref="LocalPriceBook.MinSampleFrames"/>(低于 4 帧的样本一律不可信)。</summary>
    public const int MinFrames = LocalPriceBook.MinSampleFrames;

    /// <summary>最多采样帧数:再多也不会更准(抖动由热状态决定,不是由样本量决定),只会让用户白等。</summary>
    public const int MaxFrames = 8;

    /// <summary>【F3 · 相对噪声门槛】两点法的差值 `tN − t1` 至少要占到"1 帧那次耗时"的这么多倍,否则拒收。
    ///
    /// 【为什么是相对门槛,而不是再来一条绝对阈值】九条既有拒收全是绝对数(帧数、秒数上下限),
    /// 挡不住这种形态:`t1 = 0.50 s`、`tN = 0.55 s`、`N = 4` —— 差值只有 0.05 s,与两次进程启动的
    /// **地板抖动**(本仓库实测地板 0.75~0.92 s,离散 ~23%;再叠笔记本 GPU 热降频)同量级,
    /// 却能被算成"0.0167 s/帧"这种 4 位小数的**本机实测**(假精度)。抖动会随机器/温度/负载变化,
    /// 用"占 t1 的比例"表达才跟着机器走。
    ///
    /// 【0.15 这个数怎么来的】取"地板抖动量级"本身:两次启动的地板离散实测 ~23%(0.75→0.92),
    /// 门槛定在与它同一量级但更松的 15% ⇒ 差值必须明显大于抖动才采信。对常用档的实测影响(都不误杀):
    ///   · Real-CUGAN 2x(1.65 s/帧,地板 0.9):t1=2.55、t8=14.1 → 比值 5.5 ✔
    ///   · animevideov3 2x(0.26 s/帧):t1=1.16、t8=2.98 → 比值 2.6 ✔
    ///   · 最便宜的 1x 档(0.028 s/帧,且该档输出本身全黑):t1=0.93、t8=1.12 → 比值 1.21 ✔(过线)
    /// 被它挡下的正是"差值只有抖动大小"的样本(如 0.50/0.55 → 比值 1.10 ✗)。
    /// 【拒收后果】这次不落盘、本次按"未标定"保守走旧顺序(下次换个更贵的档/更长的素材再标)。</summary>
    public const double MinDeltaRatio = 0.15;

    /// <summary>两点法:每帧可变成本 p(秒/帧,采样分辨率下)。
    /// N 必须 ≥2(否则除零);退化输入返回 0,由 <see cref="LocalPriceBook.TryBuild"/> 负责拒收。</summary>
    public static double TwoPointPerFrame(int sampleFrames, double floorSeconds, double sampleSeconds)
    {
        if (sampleFrames < 2) return 0;
        if (!double.IsFinite(floorSeconds) || !double.IsFinite(sampleSeconds)) return 0;
        return (sampleSeconds - floorSeconds) / (sampleFrames - 1);
    }

    /// <summary>两点法:每**进程**固定地板 F(秒)。F 只用于日志与审计,不参与判据。</summary>
    public static double TwoPointFloor(int sampleFrames, double floorSeconds, double sampleSeconds)
        => floorSeconds - TwoPointPerFrame(sampleFrames, floorSeconds, sampleSeconds);

    /// <summary>按内置单价(他机实测)估一帧成本,用来选采样帧数;面积按 <paramref name="pixels"/> 折算
    /// (超分成本 ∝ 源面积)。查不到该组合 ⇒ 用 <paramref name="fallbackPerFrame1080p"/>(默认 1.0 s/帧,
    /// 与 Real-CUGAN 这一档同量级 —— 宁可少采样几帧,也不要让标定变成一次长跑)。
    /// 【注意】这里的估值**只用于决定"测几帧"**,不会进入任何判据。</summary>
    public static double EstimatePerFrame(string? model, int engineScale, long pixels, double fallbackPerFrame1080p = 1.0)
    {
        double? known = PipelineOrderPlan.LookupUpscaleSecondsPerFrame(model, engineScale, out _);
        double baseRate = known ?? fallbackPerFrame1080p;
        return PipelineOrderPlan.ScaleUpscaleCostToPixels(baseRate, pixels);
    }

    /// <summary>选采样帧数 N ∈ [<paramref name="min"/>, <paramref name="max"/>]:
    /// 取 `预算 ÷ 每帧估值` 向下取整;估值非法(≤0 / 非有限)⇒ 取**上限**(估值不可信时宁多采几帧,
    /// 反正两点法只把"多出来的那部分"算成单价,采到坏值时由 `TryBuild` 的拒收条件兜住)。</summary>
    public static int FramesFor(double perFrameEstimate, long pixels, double budgetSeconds = BudgetSeconds,
        int min = MinFrames, int max = MaxFrames)
    {
        if (max < min) max = min;
        if (!double.IsFinite(perFrameEstimate) || perFrameEstimate <= 0) return max;
        double budget = double.IsFinite(budgetSeconds) && budgetSeconds > 0 ? budgetSeconds : BudgetSeconds;
        // 两点法要跑 1 帧 + N 帧,两者都含一份地板;预算只用来限制 N,不限制地板(地板与帧数无关,躲不开)。
        int n = (int)Math.Floor(budget / perFrameEstimate);
        if (n < min) n = min;
        return n > max ? max : n;
    }

    /// <summary>从 <paramref name="frameCount"/> 帧里挑 <paramref name="need"/> 帧的**下标**(等间隔,优先取中段)。
    /// 纯函数(采样策略可单测,不必起引擎):
    ///   · **避开片头/片尾** —— 片头常有黑场/标题(引擎可能省算力)、片尾常有定格,取中段 50% 更代表正片;
    ///     中段放不下(素材太短)时如实退回全片等间隔取;
    ///   · 取不满 `need`(素材帧数不足)→ 返回**实际能取到**的个数(可能少于 need、甚至为空集合),
    ///     由调用方决定"如实报『素材帧数不足,跳过标定』";
    ///   · 结果**严格递增且不重复**(重复帧会让两点法的差值偏小 → 单价偏小)。</summary>
    public static IReadOnlyList<int> SampleIndices(int frameCount, int need)
    {
        var list = new List<int>();
        if (frameCount <= 0 || need <= 0) return list;
        if (need == 1) { list.Add(frameCount / 2); return list; }
        if (need >= frameCount) { for (int i = 0; i < frameCount; i++) list.Add(i); return list; }
        // 中段 50%:[frameCount/4, frameCount-1-frameCount/4];放不下 need 帧就退回全片。
        int lo = frameCount / 4, hi = frameCount - 1 - frameCount / 4;
        if (hi - lo + 1 < need) { lo = 0; hi = frameCount - 1; }
        for (int i = 0; i < need; i++)
        {
            int idx = lo + (int)Math.Round((hi - lo) * (double)i / (need - 1), MidpointRounding.AwayFromZero);
            if (list.Count > 0 && idx <= list[^1]) idx = list[^1] + 1;   // 兜底:严格递增(四舍五入可能撞在一起)
            if (idx > frameCount - 1) break;
            list.Add(idx);
        }
        return list;
    }

    /// <summary>采样分辨率下的秒/帧 → 1080p 基准的秒/帧(判据统一用 1080p 单价)。</summary>
    public static double To1080p(double secondsPerFrame, long samplePixels)
        => samplePixels > 0 ? secondsPerFrame * (PipelineOrderPlan.ReferencePixels1080p / samplePixels) : secondsPerFrame;

    // ───────────────────── 样本输出的体检(契约 F1-I3:黑帧/坏帧/不可读 ⇒ 拒收,不落盘) ─────────────────────

    /// <summary>一帧样本输出的体检输入(由调用方测好:**是否落地**、**字节数**、**是否被既有判黑口径判为缺陷帧**)。
    /// 【为什么要分三项】三种坏法在 ncnn 上都会**静默发生且退出码 0**:写不出文件(ncnn 少数驱动上直接崩)、
    /// 写出 0 字节空帧、写出黑帧/带状坏帧。任一种都必须拒收,否则会把一个**偏小**的单价永久落盘。</summary>
    public readonly record struct SampleOutput(bool Present, long Bytes, bool Defective);

    /// <summary>体检一组样本输出:全好返回 null;有缺陷返回**中文原因**(与 `TryBuild` 的九条拒收同一风格)。
    /// **纯逻辑**:判黑本身复用既有口径(`AlhPro.Core.FrameInspect.IsDefectiveFrame`,由调用方通过
    /// <see cref="SampleOutput.Defective"/> 传进来)—— 这里**不新造第二套判黑**。
    /// <paramref name="expected"/> = 这一批应该产出多少帧(引擎保帧数)。</summary>
    public static string? OutputDefect(IReadOnlyList<SampleOutput> frames, int expected)
    {
        if (expected <= 0) return $"样本期望帧数非法:{expected}";
        if (frames is null || frames.Count == 0) return "样本输出目录是空的(引擎没产出任何帧)";
        if (frames.Count != expected)
            return $"样本输出帧数对不上:期望 {expected} 帧,实际 {frames.Count} 帧";
        for (int i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            if (!f.Present) return $"样本第 {i + 1} 帧没落地(引擎静默失败;ncnn 部分驱动会退 0 但不写文件)";
            if (f.Bytes <= 0) return $"样本第 {i + 1} 帧是 0 字节空帧(ncnn 在 50 系/部分驱动上的已知症状,退出码仍是 0)";
            if (f.Defective) return $"样本第 {i + 1} 帧被判为黑帧/带状坏帧(既有判黑口径:整帧或任一 1/3 主条带 ≥95% 近黑)";
        }
        return null;
    }
}
