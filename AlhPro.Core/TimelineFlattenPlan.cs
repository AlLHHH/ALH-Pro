namespace AlhPro.Core;

/// <summary>「平滑时间轴(按时轴填平)」计划器(纯逻辑,可单测)。
/// 【任务 S · 用户亲测确认有效】用户在正确窗口(源 1~3 秒)对比后:「小样填平没有震颤了」。
/// 成因(已实测定位):源在 1.73s / 1.83s 处有**紧邻缺口**(内容一跳 mean|diff|=59.2),而软件当前把它切成
/// "小动 → 冻结 → 大跳40 → 大跳29"四段(周围却是均匀的 ~2.0)→ 播放时就是可见顿挫。
/// 【原理】源帧的真实 PTS(`VideoService.BuildFrameDurationsAsync` 的 frameDurs)给出**真实时轴**;
/// 把目标时轴改成**均匀**:目标帧数 = round(窗口真实时长 × 目标帧率),目标帧率 = 源内容帧率 × 补帧倍率;
/// 对每个目标时刻 t 取**包住 t 的两张源帧**作 `-0/-1`,用 `-s φ`(φ=(t−pts0)/(pts1−pts0))合成;
/// t 正好落在某张源帧上时**直接拷贝**该帧。→ 输出均匀帧率、**总时长与源一致**、音画不漂。
/// 【边界(硬要求)】**CFR 源(没有缺口)行为不许变**:本类只在"检出缺口"时才给出 Flatten=true。
/// 【已知代价(小样实测,不在本类里放大锐化)】缺口处内容本来静止时,插值帧可能"猜出来"轻微不同:
/// 小样实测无重影(梯度比均值 0.937),但**有一处轻微软化**(源帧 618、运动最大处降到 72~77% 清晰度)。
/// 【本类只做"要不要填平 + 目标帧数 + 每个目标帧该取哪两张源帧/φ"的纯计算】
/// 真正的合成仍走仓库既有原语(逐槽 `-s` 插帧);接线状态见 VideoService 的注释与本次提交说明。</summary>
public static class TimelineFlattenPlan
{
    /// <summary>相邻间隔偏离"中位间隔"超过这个比例 → 视为缺口/顿挫。【待实测标定】
    /// 【任务 W · 联网核对】**外部没有任何公开的"缺口容差比例"可参照** —— 社区工具不按"与中位间隔比"
    /// 判缺口:最接近的同类工具 ddfi-rife 走的是"**先删重复帧 → 得到真 VFR → 用真时间戳重算目标帧时刻 → 再转 CFR**"
    /// (<https://github.com/Mr-Z-2697/ddfi-rife>:Remove duplicated frames → Interpolate → Extract timestamps →
    /// "Correct" the interpolated video with calculated timestamps → Convert to CFR)。
    /// 也就是说业界把"时间戳"当**事实**来用,而不是像本类这样"先判有没有缺口、再决定要不要填平"。
    /// 因此 0.25 保持本工程口径,**不据外部资料调整**;差别与建议见 <see cref="ExternalPractice"/> 的说明。</summary>
    public const double GapToleranceRatio = 0.25;

    /// <summary>时长/时刻比较用的小量(秒)。</summary>
    public const double EpsSeconds = 1e-6;

    /// <summary>计划结果。<paramref name="Flatten"/> = false 时**必须逐字保持现状**(CFR 源/没开补帧/无缺口)。</summary>
    public readonly record struct Plan(
        bool Flatten, double TargetFps, int TargetFrames, double TotalSeconds, int GapCount, int SourceFrames, string Reason)
    {
        /// <summary>一行可审计日志(用户要求:写清缺口处数、目标帧率、输出帧数、时长)。</summary>
        public string LogLine => Flatten
            ? $"时间轴:源缺口 {GapCount} 处 → 填平(目标 {TargetFps:0.##}fps,输出 {TargetFrames} 帧,时长 {TotalSeconds:0.###}s)"
            : $"时间轴:无缺口,按原样({Reason})";
    }

    /// <summary>按源帧时长表 + 内容帧率 + 补帧倍率决定"是否填平"以及目标时间轴参数。
    /// 缺口判定:逐个间隔与**中位数**比较,偏离超过容差比例记一处;
    /// **一处都没有 = 均匀源(CFR/无顿挫)→ 不填平**(保证既有行为不变)。
    /// 【2026-09-14】容差比例原先经"在线参数覆盖层"(ParamProfileRuntime)读,该功能整体删除后直接取
    /// <see cref="GapToleranceRatio"/> —— 与"覆盖层为 null 时回落常量"逐字等价,判定行为一个字节都没变。</summary>
    public static Plan Decide(IReadOnlyList<double>? frameDurs, double contentFps, int interpScale)
    {
        int n = frameDurs?.Count ?? 0;
        if (n < 3) return new Plan(false, 0, 0, 0, 0, n, "源时长表不足(≤2 帧)");
        if (interpScale < 2) return new Plan(false, 0, 0, 0, 0, n, "未开补帧");
        if (!(contentFps > 0)) return new Plan(false, 0, 0, 0, 0, n, "内容帧率未知");
        double gapToleranceRatio = GapToleranceRatio;
        double total = 0;
        foreach (var d in frameDurs!)
        {
            if (!double.IsFinite(d) || d <= 0) return new Plan(false, 0, 0, 0, 0, n, "源时长表含非法值");
            total += d;
        }
        var sorted = new List<double>(frameDurs!);
        sorted.Sort();
        double median = sorted[sorted.Count / 2];
        if (!(median > 0)) return new Plan(false, 0, 0, 0, 0, n, "中位间隔非正");
        int gaps = 0;
        foreach (var d in frameDurs!)
            if (Math.Abs(d - median) / median > gapToleranceRatio) gaps++;
        if (gaps == 0) return new Plan(false, 0, 0, total, 0, n, "间隔均匀(无缺口)");
        double targetFps = contentFps * interpScale;
        int targetFrames = Math.Max(2, (int)Math.Round(total * targetFps));
        return new Plan(true, targetFps, targetFrames, total, gaps, n, "检出缺口");
    }

    /// <summary>每张源帧的起始时刻(秒),长度 = 帧数 + 1(末项 = 总时长)。</summary>
    public static double[] CumulativeTimes(IReadOnlyList<double> frameDurs)
    {
        int n = frameDurs?.Count ?? 0;
        var pts = new double[n + 1];
        double acc = 0;
        for (int i = 0; i < n; i++) { pts[i] = acc; acc += Math.Max(0, frameDurs![i]); }
        pts[n] = acc;
        return pts;
    }

    /// <summary>把"第 targetIndex 个目标帧(均匀时间轴)"映射到"源帧对 + 插值系数 φ":
    /// 返回 false = 该目标时刻落在源时间轴之外(不该插值)。
    /// <paramref name="exactSource"/> = true 表示 t 正好落在某张源帧上 → **直接拷贝该帧**(<paramref name="phi"/> = 0,idx0 即该帧),
    /// 不插值(避免静止内容被"猜"出轻微差异 —— 小样实测的轻微软化就来自这里)。</summary>
    public static bool MapTargetFrame(IReadOnlyList<double> frameDurs, int targetIndex, double targetFps,
        out int idx0, out int idx1, out double phi, out bool exactSource)
    {
        idx0 = 0; idx1 = 0; phi = 0; exactSource = false;
        int n = frameDurs?.Count ?? 0;
        if (n < 2 || !(targetFps > 0) || targetIndex < 0) return false;
        double t = targetIndex / targetFps;
        var pts = CumulativeTimes(frameDurs);
        if (t < -EpsSeconds || t > pts[n] + EpsSeconds) return false;
        // 找 k:pts[k] ≤ t < pts[k+1]
        int k = 0;
        for (int i = 0; i < n; i++)
        {
            if (t >= pts[i] - EpsSeconds && t < pts[i + 1] - EpsSeconds) { k = i; break; }
            k = i;
        }
        idx0 = k;
        idx1 = Math.Min(k + 1, n - 1);
        double span = pts[idx1] - pts[idx0];
        if (Math.Abs(t - pts[idx0]) <= EpsSeconds) { phi = 0; exactSource = true; return true; }       // 正好落在源帧上
        if (idx1 == idx0 || span <= EpsSeconds) { phi = 0; exactSource = true; return true; }
        phi = Math.Clamp((t - pts[idx0]) / span, 0, 1);
        if (phi >= 1 - EpsSeconds) { phi = 1; exactSource = true; idx0 = idx1; }                          // 落在下一张源帧上
        return true;
    }
}
