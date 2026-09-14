namespace AlhPro.Core;

/// <summary>【任务 V】在线"最优参数配置"的**数据模型 + 解析 + 逐项校验 + 兜底**(纯逻辑,可单测)。
/// 【隐私硬前提(写死在实现里,不许改)】
///   1. 只允许**下载**这一份参数配置;**任何用户数据、文件名、路径、机型指纹、GPU 名称、使用统计一律不上传** ——
///      本类型的上游(`ALHPro.ParamProfileFetcher`)只有 GET、没有 POST/PUT,不带任何查询串与自定义标识头;
///   2. **离线必须完全可用**:网络失败 / 超时 / 返回异常 → **静默使用内置默认表**,不报错、不阻塞、不改变默认行为;
///   3. 网络调用短超时(5 秒,与仓库既有 AdFetcher/TipFetcher 同风格)+ 不重试轰炸(多个镜像端点各试一次即止)+ 不阻塞界面。
/// 【校验口径】逐项做范围校验;**任何一项非法 → 该项回退内置值**,并记下"哪一项被拒、为什么"(
/// 见 <see cref="ParamValidationResult.Rejected"/>),绝不因为一个坏字段整份丢弃、也绝不采用越界值。
/// 【与既有机制的关系】超时/异常处理风格照抄 <c>AdFetcher</c>/<c>TipFetcher</c>/<c>UpdateChecker</c>(各 5 秒、失败静默),
/// 但**不耦合**它们的任何业务逻辑;本文件不认识任何网络库,拉取由调用方注入(因此可以完整离线单测)。</summary>
public sealed record ParamProfile
{
    /// <summary>配置版本号(必填、非空)。只在"版本变了或缓存过期"时才重新拉取。</summary>
    public string Version { get; init; } = "";

    /// <summary>适用范围说明(自由文本,仅进日志,不参与判定)。例如 "1080p 源 / NVIDIA 40 系 / 模型 rife-v4.26"。</summary>
    public string Scope { get; init; } = "";

    // ===== ① 超分单价表(秒/帧 @1080p,按"引擎|模型|倍率"键;缺失的键一律回退内置表)=====
    public Dictionary<string, double> UpscaleSecondsPerFrame1080p { get; init; } = new();

    // ===== ② 补帧单价表(秒/输出帧 @1080p,按"面积档"键,如 "1080p"/"4k")=====
    public Dictionary<string, double> InterpSecondsPerFrame1080p { get; init; } = new();

    // ===== ③ 批次档位基准与上限(帧)=====
    public int WeakBatchFrames { get; init; } = 50;
    public int NormalBatchFrames { get; init; } = 300;
    public int StrongBatchFrames { get; init; } = 350;
    public int StrongLongBatchFrames { get; init; } = 700;

    // ===== ④ 顺序判定门槛与安全边际 =====
    public double OrderSwitchMinSavingsPercent { get; init; } = 15.0;

    // ===== ⑤ 时间轴(平滑/切点保护)阈值 =====
    public double TimelineGapToleranceRatio { get; init; } = 0.25;
    public double SceneCutDiffThreshold { get; init; } = 25.0;
    public double SceneCutStrongDiffThreshold { get; init; } = 50.0;
    public double SceneCutLapDropRatio { get; init; } = 0.6;

    // ===== ⑥ 设备性能档阈值(秒/帧 @1080p)=====
    public double PerfFastSecondsPerFrame { get; init; } = 0.30;
    public double PerfNormalSecondsPerFrame { get; init; } = 1.00;

    /// <summary>内置默认表 = 仓库现有的实测/口径常量(与各处 const 同源,避免两套数字)。</summary>
    public static ParamProfile BuiltIn => new()
    {
        Version = "builtin",
        Scope = "内置默认(仓库既有实测与用户口径)",
        UpscaleSecondsPerFrame1080p = new()
        {
            ["realesrgan|realesrgan-x4plus|4"] = 0.24,   // 真机实测 0.24~0.6 秒/帧(见 EngineService 探测注释)
            ["realesrgan|realesrgan-x4plus-anime|4"] = 0.24,
            ["waifu2x|models-cunet|2"] = 0.25,
            ["waifu2x|models-upconv_7_anime_style_art_rgb|2"] = 0.25,
        },
        InterpSecondsPerFrame1080p = new()
        {
            ["1080p"] = 0.10,
            ["1440p"] = 0.20,
            ["2160p"] = 0.35,
        },
        WeakBatchFrames = RenderPolicy.WeakDeviceFramesPerBatch,
        NormalBatchFrames = RenderPolicy.NormalDeviceFramesPerBatch,
        StrongBatchFrames = RenderPolicy.StrongDeviceFramesPerBatch,
        StrongLongBatchFrames = RenderPolicy.StrongDeviceLargeFramesPerBatch,
        OrderSwitchMinSavingsPercent = 15.0,
        TimelineGapToleranceRatio = TimelineFlattenPlan.GapToleranceRatio,
        SceneCutDiffThreshold = SceneCutJudge.DiffThreshold,
        SceneCutStrongDiffThreshold = SceneCutJudge.StrongDiffThreshold,
        SceneCutLapDropRatio = SceneCutJudge.LapDropRatio,
        PerfFastSecondsPerFrame = DevicePerf.FastSecondsPerFrame1080p,
        PerfNormalSecondsPerFrame = DevicePerf.NormalSecondsPerFrame1080p,
    };
}

/// <summary>校验结果:<see cref="Profile"/> 是**逐项兜底后**的可用配置(永远非 null),
/// <see cref="Rejected"/> 逐条写清"哪一项被拒、为什么"(进日志,用户能看见用的是哪套)。</summary>
public sealed record ParamValidationResult(
    ParamProfile Profile, int Accepted, int RejectedTotal, IReadOnlyList<string> Rejected, string Source)
{
    public string LogLine => $"参数来源:{Source}(校验通过 {Accepted} 项、被拒 {RejectedTotal} 项)";
}

/// <summary>【任务 V】在线参数的解析 + 逐项校验 + 兜底(纯函数)。</summary>
public static class ParamProfileParser
{
    // 范围校验上下界(全部标【待实测标定】:上界取"明显不合理"的量级,目的是拦住坏数据而不是精确标定)
    public const double MinSecondsPerFrame = 0.001;   // 再快也不可能 1ms/帧(低于它视为坏数据)
    public const double MaxSecondsPerFrame = 60.0;    // 再慢也不该超过 1 分钟/帧(ONNX 落 CPU 实测 8 秒/帧)
    public const int MinBatchFrames = 50;             // 用户下界(全档位 floor)
    public const int MaxBatchFrames = 2000;           // 用户给定的批上限范围 [50, 2000]
    public const double MinRatio = 0.0, MaxRatio = 1.0;

    /// <summary>解析并校验一份在线配置。<paramref name="json"/> 为 null/空/坏 → 直接返回 <paramref name="fallback"/>(内置)。
    /// 单字段坏 → 只用内置值替换该字段,其余字段照常采用。</summary>
    public static ParamValidationResult ParseAndValidate(string? json, ParamProfile? fallback = null,
        string source = "内置")
    {
        var fb = fallback ?? ParamProfile.BuiltIn;
        var rejected = new List<string>();
        if (string.IsNullOrWhiteSpace(json))
            return new ParamValidationResult(fb, 0, 0, rejected, "内置(无在线配置)");
        System.Text.Json.JsonDocument doc;
        try { doc = System.Text.Json.JsonDocument.Parse(json!); }
        catch (Exception ex)
        {
            rejected.Add($"整份 JSON 解析失败({ex.Message.Split('\n')[0]})");
            return new ParamValidationResult(fb, 0, rejected.Count, rejected, "内置(在线配置无法解析)");
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                rejected.Add($"顶层不是 JSON 对象(实为 {root.ValueKind})");
                return new ParamValidationResult(fb, 0, rejected.Count, rejected, "内置(在线配置格式不对)");
            }
            int accepted = 0;
            var p = fb;
            // ---- 版本与适用范围 ----
            var ver = Str(root, "version");
            if (string.IsNullOrWhiteSpace(ver)) rejected.Add("version 缺失或为空 → 沿内置版本判定");
            else { p = p with { Version = ver! }; accepted++; }
            var scope = Str(root, "scope");
            if (!string.IsNullOrWhiteSpace(scope)) { p = p with { Scope = scope! }; accepted++; }

            // ---- 单价表 ----
            var up = DoubleMap(root, "upscaleSecondsPerFrame1080p", rejected, out int accUp);
            if (up != null) { p = p with { UpscaleSecondsPerFrame1080p = up }; accepted += accUp; }
            var ip = DoubleMap(root, "interpSecondsPerFrame1080p", rejected, out int accIp);
            if (ip != null) { p = p with { InterpSecondsPerFrame1080p = ip }; accepted += accIp; }

            // ---- 批次档位(必须满足 50 ≤ Weak ≤ Normal ≤ Strong ≤ StrongLong ≤ 2000)----
            int w = IntInRange(root, "weakBatchFrames", fb.WeakBatchFrames, MinBatchFrames, MaxBatchFrames, rejected, ref accepted);
            int n = IntInRange(root, "normalBatchFrames", fb.NormalBatchFrames, MinBatchFrames, MaxBatchFrames, rejected, ref accepted);
            int s = IntInRange(root, "strongBatchFrames", fb.StrongBatchFrames, MinBatchFrames, MaxBatchFrames, rejected, ref accepted);
            int sl = IntInRange(root, "strongLongBatchFrames", fb.StrongLongBatchFrames, MinBatchFrames, MaxBatchFrames, rejected, ref accepted);
            if (!(w <= n && n <= s && s <= sl))
            {
                rejected.Add($"批次档位不满足 weak ≤ normal ≤ strong ≤ strongLong(实得 {w} ≤ {n} ≤ {s} ≤ {sl})→ 四个档位整组回退内置");
                w = fb.WeakBatchFrames; n = fb.NormalBatchFrames; s = fb.StrongBatchFrames; sl = fb.StrongLongBatchFrames;
            }
            p = p with { WeakBatchFrames = w, NormalBatchFrames = n, StrongBatchFrames = s, StrongLongBatchFrames = sl };

            // ---- 顺序判定安全边际 ----
            double margin = DoubleInRange(root, "orderSwitchMinSavingsPercent", fb.OrderSwitchMinSavingsPercent, 0, 100, rejected, ref accepted);
            p = p with { OrderSwitchMinSavingsPercent = margin };

            // ---- 时间轴阈值 ----
            double gap = DoubleInRange(root, "timelineGapToleranceRatio", fb.TimelineGapToleranceRatio, 0.01, 2.0, rejected, ref accepted);
            double cutD = DoubleInRange(root, "sceneCutDiffThreshold", fb.SceneCutDiffThreshold, 1, 255, rejected, ref accepted);
            double cutS = DoubleInRange(root, "sceneCutStrongDiffThreshold", fb.SceneCutStrongDiffThreshold, 1, 255, rejected, ref accepted);
            double lap = DoubleInRange(root, "sceneCutLapDropRatio", fb.SceneCutLapDropRatio, 0.01, 1.0, rejected, ref accepted);
            if (cutD > cutS)
            {
                rejected.Add($"sceneCutDiffThreshold({cutD})> sceneCutStrongDiffThreshold({cutS})→ 两项回退内置");
                cutD = fb.SceneCutDiffThreshold; cutS = fb.SceneCutStrongDiffThreshold;
            }
            p = p with
            {
                TimelineGapToleranceRatio = gap,
                SceneCutDiffThreshold = cutD,
                SceneCutStrongDiffThreshold = cutS,
                SceneCutLapDropRatio = lap,
            };

            // ---- 设备性能档阈值(快必须严于正常,否则档位无意义)----
            double pf = DoubleInRange(root, "perfFastSecondsPerFrame", fb.PerfFastSecondsPerFrame, MinSecondsPerFrame, MaxSecondsPerFrame, rejected, ref accepted);
            double pn = DoubleInRange(root, "perfNormalSecondsPerFrame", fb.PerfNormalSecondsPerFrame, MinSecondsPerFrame, MaxSecondsPerFrame, rejected, ref accepted);
            if (pf >= pn)
            {
                rejected.Add($"perfFastSecondsPerFrame({pf})≥ perfNormalSecondsPerFrame({pn})→ 两项回退内置");
                pf = fb.PerfFastSecondsPerFrame; pn = fb.PerfNormalSecondsPerFrame;
            }
            p = p with { PerfFastSecondsPerFrame = pf, PerfNormalSecondsPerFrame = pn };

            string src = rejected.Count > 0
                ? $"在线 v{p.Version}(部分项被拒)"
                : $"在线 v{p.Version}";
            return new ParamValidationResult(p, accepted, rejected.Count, rejected, src);
        }
    }

    private static string? Str(System.Text.Json.JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;

    /// <summary>读"键 → 秒/帧"字典:**逐项**范围校验(非法项被拒并回退内置同名键)。</summary>
    private static Dictionary<string, double>? DoubleMap(System.Text.Json.JsonElement root, string name,
        List<string> rejected, out int accepted)
    {
        accepted = 0;
        if (!root.TryGetProperty(name, out var el)) return null;   // 未提供 → 保持内置(不算被拒)
        if (el.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            rejected.Add($"{name} 不是 JSON 对象(实为 {el.ValueKind})→ 整表回退内置");
            return null;
        }
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in el.EnumerateObject())
        {
            if (kv.Value.ValueKind != System.Text.Json.JsonValueKind.Number
                || !kv.Value.TryGetDouble(out double d)
                || !double.IsFinite(d) || d < MinSecondsPerFrame || d > MaxSecondsPerFrame)
            {
                rejected.Add($"{name}[{kv.Name}] 非法或越界(需 {MinSecondsPerFrame}~{MaxSecondsPerFrame} 秒/帧)→ 该键回退内置");
                continue;
            }
            map[kv.Name] = d;
            accepted++;
        }
        return map;
    }

    private static int IntInRange(System.Text.Json.JsonElement root, string name, int fallback,
        int lo, int hi, List<string> rejected, ref int accepted)
    {
        if (!root.TryGetProperty(name, out var el)) return fallback;
        if (el.ValueKind != System.Text.Json.JsonValueKind.Number || !el.TryGetInt32(out int v) || v < lo || v > hi)
        {
            rejected.Add($"{name} 非法或越界(需 {lo}~{hi})→ 回退内置 {fallback}");
            return fallback;
        }
        accepted++;
        return v;
    }

    private static double DoubleInRange(System.Text.Json.JsonElement root, string name, double fallback,
        double lo, double hi, List<string> rejected, ref int accepted)
    {
        if (!root.TryGetProperty(name, out var el)) return fallback;
        if (el.ValueKind != System.Text.Json.JsonValueKind.Number || !el.TryGetDouble(out double v)
            || !double.IsFinite(v) || v < lo || v > hi)
        {
            rejected.Add($"{name} 非法或越界(需 {lo}~{hi})→ 回退内置 {fallback}");
            return fallback;
        }
        accepted++;
        return v;
    }
}

/// <summary>【任务 V】当前生效的"在线参数"覆盖层(进程内,默认 null = 完全用内置表)。
/// 【为什么是覆盖层而不是到处传参】决策函数(批次/顺序/时间轴/性能档)都是静态纯函数、调用点很多;
/// 用一层"可空的当前配置"让它们**在不配置时与改动前逐字一致**(null → 全部走既有常量),
/// 配置成功后才覆盖少数几个可调项。所有单测都不设置它 ⇒ 测试永远是内置口径、结果确定。</summary>
public static class ParamProfileRuntime
{
    private static ParamProfile? _current;

    /// <summary>当前生效的在线配置(null = 内置)。</summary>
    public static ParamProfile? Current => Volatile.Read(ref _current);

    /// <summary>设置当前配置(null = 恢复内置)。返回是否真的换了(供日志判断)。</summary>
    public static bool Set(ParamProfile? profile)
    {
        var old = Volatile.Read(ref _current);
        Volatile.Write(ref _current, profile);
        return !ReferenceEquals(old, profile);
    }

    // ===== 覆盖层读取点:null 时返回内置常量(保证"没配置 = 行为逐字不变")=====
    public static int WeakBatchFrames => Current?.WeakBatchFrames ?? RenderPolicy.WeakDeviceFramesPerBatch;
    public static int NormalBatchFrames => Current?.NormalBatchFrames ?? RenderPolicy.NormalDeviceFramesPerBatch;
    public static int StrongBatchFrames => Current?.StrongBatchFrames ?? RenderPolicy.StrongDeviceFramesPerBatch;
    public static int StrongLongBatchFrames => Current?.StrongLongBatchFrames ?? RenderPolicy.StrongDeviceLargeFramesPerBatch;
    public static double PerfFastSecondsPerFrame => Current?.PerfFastSecondsPerFrame ?? DevicePerf.FastSecondsPerFrame1080p;
    public static double PerfNormalSecondsPerFrame => Current?.PerfNormalSecondsPerFrame ?? DevicePerf.NormalSecondsPerFrame1080p;
    public static double SceneCutDiffThreshold => Current?.SceneCutDiffThreshold ?? SceneCutJudge.DiffThreshold;
    public static double SceneCutStrongDiffThreshold => Current?.SceneCutStrongDiffThreshold ?? SceneCutJudge.StrongDiffThreshold;
    public static double SceneCutLapDropRatio => Current?.SceneCutLapDropRatio ?? SceneCutJudge.LapDropRatio;
    public static double TimelineGapToleranceRatio => Current?.TimelineGapToleranceRatio ?? TimelineFlattenPlan.GapToleranceRatio;
}
