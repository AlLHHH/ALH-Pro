namespace AlhPro.Core;

/// <summary>「超分 ↔ 补帧」阶段顺序的**按实测单价自动判定**(纯逻辑,可单测)。
/// 【任务 Q1 · 2026-09-13】取代原先的全局开关 `VideoPipeline.UpscaleFirstEnabled`(常量 false):
/// 顺序该由**这台机器上这次任务的实际成本**决定,而不是一个"全局关掉"的常量。
///
/// ==== 成本模型(判据的四个数字) ====
/// 记 N=源帧数、k=补帧倍率、u=超分单帧成本(按源面积缩放)、r_lo/r_hi=补帧单个**输出**帧在
/// 源分辨率/放大后分辨率上的成本:
///   · 新顺序(先超分)= N·u + k·N·r_hi
///   · 旧顺序(先补帧)= k·N·r_lo + k·N·u     ← 补帧输出仍是源分辨率,所以超分那侧也是 N·k 帧 × u
/// 令新顺序更省 ⟺ u·(1−k) &lt; k·(r_lo−r_hi) ⟺ (k&gt;1)**u &gt; k·(r_hi−r_lo)/(k−1)** —— 这就是门槛。
///
/// ==== 实测单价表(2026-09-13 基准代理真机测;单位:秒/帧;超分档为 1080p 源、`-j 1:1:1 -t 0`) ====
/// 超分:
///   · realesr-animevideov3 1x = **0.027~0.029**【注意:该档实测输出全黑(缺 x1 权重),数字仅供量级】
///     · 2x = **0.252~0.269** · 4x = **0.294~0.300**
///   · realesr-general-x4v3  4x = **0.455~0.461**
///   · realesr-x4plus        4x = **14.82~15.47**【**仅 12 帧样本**,不确定性最大】
///   · realesr-x4plus-anime  4x ≈ **3.3~4.4**(区间较宽)
///   · waifu2x models-cunet  2x:-n0 = **0.368~0.370**、-n1 = **0.373~0.383**、-n2 = **0.353~0.364**
///     (三档差 &lt;5%,对本判据的影响远小于模型之间的差距,故表里取三档中值)
///   · waifu2x models-upconv_7_photo 2x ≈ **1.288**
/// **关键规律(必须遵守)**:**超分单帧成本正比于源面积,几乎与输出倍率无关**
/// (同一模型 2x=0.25 vs 4x=0.30)—— 所以 `u` 按 `实测值 × (源面积 ÷ 1080p 面积)` 缩放。
/// 补帧(RIFE v4.13,每个**输出**帧):**1080p = 0.0807、2160p = 0.2776、4320p = 0.5116~0.5255**;
///   其它分辨率按**面积**在这三个锚点之间**分段线性内插**(不按过原点的直线 —— 实测明显次线性)。
///
/// ==== 覆盖不到的组合怎么办 ====
/// 表里没有(模型未实测 / 倍率未实测 / 参数非法 / 没开补帧或没开超分)→ **一律回退旧顺序**,
/// 理由里标【待实测标定】。宁可保守,也不拿没测过的数字去改阶段顺序。
/// ==== 安全边际 ====
/// 预估节省 **&lt; 15%** 时**不切换**(避免在临界点上抖动/来回翻),理由写进日志。
///
/// ==== 【2026-09-14 在线参数功能整体删除后的口径】====
/// 本类原先有"三组数字可被在线配置覆盖"的一层(超分单价表 / 补帧锚点表 / 安全边际)—— 那份"在线最优参数"
/// 功能已被用户判定为**累赘**并整体删除(界面复选框、设置项、联网拉取服务、覆盖层全部删掉)。
/// 现在这里**只读内置实测表**:<see cref="UpscaleRates"/> 与 <see cref="InterpAnchorSeconds"/> /
/// <see cref="InterpAnchorPixels"/>,每一格都标着真机实测出处。删除前后**行为逐字一致**:
/// 覆盖层原本只在"配置成功"时才生效,默认(null)就是回落这些常量(见当时的单测口径)。
/// 备注:外部社区从未发布过这类"超分秒/帧"标定表(见 <c>ExternalPractice</c> 的说明),
/// 所以这张表的权威来源只能是本仓库的真机实测。</summary>
public static class PipelineOrderPlan
{
    /// <summary>1080p 面积 = 2 073 600 px(2.07 Mpx):超分单价表的基准面积,也是补帧锚点之一。
    /// 与批大小面积缩放用的是同一个基准(引用 RenderPolicy 的常量,避免两处各写一份)。</summary>
    public const double ReferencePixels1080p = AlhPro.Core.RenderPolicy.ReferencePixels1080p;

    /// <summary>补帧锚点面积(px):1080p / 2160p / 4320p —— 与上面的实测数字一一对应。</summary>
    public static readonly long[] InterpAnchorPixels = { 1920L * 1080, 3840L * 2160, 7680L * 4320 };

    /// <summary>补帧锚点成本(秒/输出帧):1080p / 2160p / 4320p(4320p 取 0.5116~0.5255 的中值)。</summary>
    public static readonly double[] InterpAnchorSeconds = { 0.0807, 0.2776, 0.5186 };

    /// <summary>补帧单帧成本的极小值兜底(小图外推不能算出 0 或负数)。</summary>
    public const double MinInterpSecondsPerFrame = 0.005;

    /// <summary>安全边际:预估节省低于它就不切换顺序(15%)。
    /// 【出处】本仓库口径(任务 Q1),**外部没有可参照的公开数值** —— 社区工具(SVP/Flowframes/Hybrid)
    /// 根本不做"按实测单价自动选阶段顺序"这件事,所以这条边际只能自定义。【不确定度】无外部对照,
    /// 15% 是"避免在临界点抖动"的经验值。(原先还有"在线参数可覆盖"这一层,该功能已于 2026-09-14 删除。)</summary>
    public const double MinSavingsPercent = 15.0;

    /// <summary>「安全边际」的哨兵值:把本值(或省略参数)传给
    /// <see cref="Decide(CostInput,double,int,int,int,int,double,double)"/> = 用内置
    /// <see cref="MinSavingsPercent"/>;显式传 ≥0 的具体值 = 完全听调用方(单测大量依赖这条)。
    /// 【为什么要哨兵】既有调用方全部省略该参数,而成百上千行单测显式传具体值(如 5.0)——
    /// 用 `<0` 当哨兵既不动那些断言,又让"省略 = 用内置边际"这条语义天然成立。</summary>
    public const double UseBuiltInMinSavings = -1.0;

    /// <summary>超分单价表的一行(秒/帧 @1080p 源)。
    /// <paramref name="Engine"/> 记录该行属于哪个引擎(判定只按 <paramref name="ModelKey"/> + 倍率匹配;
    /// 它原是为"在线参数表的键 `引擎|模型|倍率`"而加,在线参数功能已删除,保留只为可读性与将来扩展)。</summary>
    public readonly record struct UpscaleRate(string Engine, string ModelKey, int EngineScale,
        double SecondsPerFrame1080p, string Provenance);

    /// <summary>实测超分单价表(出处逐条标注;区间取中值,区间宽度见 Provenance)。</summary>
    public static readonly UpscaleRate[] UpscaleRates =
    {
        new("realesrgan", "animevideov3", 1, 0.028,  "2026-09-13 实测 0.027~0.029;【该档输出全黑(无 x1 权重),数字仅供量级】"),
        new("realesrgan", "animevideov3", 2, 0.2605, "2026-09-13 实测 0.252~0.269"),
        new("realesrgan", "animevideov3", 4, 0.297,  "2026-09-13 实测 0.294~0.300"),
        new("realesrgan", "general-x4v3", 4, 0.458,  "2026-09-13 实测 0.455~0.461"),
        new("realesrgan", "wdn-x4v3", 4, 0.460,      "2026-09-14 实测:20 帧 1080p 目录批跑 wdn/general 耗时比 1.0042(13.07 vs 13.02 s),按 general-x4v3 锚点 0.458 折算;两者同架构、bin 字节数完全相同(4,853,320 B)"),
        new("realesrgan", "x4plus", 4, 15.145,       "2026-09-13 实测 14.82~15.47;【仅 12 帧样本,不确定性最大】"),
        new("realesrgan", "x4plus-anime", 4, 3.85,   "2026-09-13 实测 ≈3.3~4.4(区间较宽,取中值)"),
        new("waifu2x", "cunet", 2, 0.3685,            "2026-09-13 实测 -n0 0.368~0.370 / -n1 0.373~0.383 / -n2 0.353~0.364(三档中值)"),
        new("waifu2x", "upconv_7_photo", 2, 1.288,    "2026-09-13 实测 ≈1.288"),
    };

    /// <summary>把模型名归一到成本表的键(与表里的 ModelKey 对应);认不出返回 null。
    /// 同时接受**官方仓库里的下划线写法**(`RealESRGAN_x4plus_anime_6B`,
    /// 见 <https://github.com/xinntao/Real-ESRGAN/blob/master/docs/model_zoo.md>)——
    /// 引擎侧名与界面显示名写法不一,不许因为"下划线 vs 连字符"就查不到实测表。</summary>
    public static string? NormalizeModel(string? model)
    {
        string m = model ?? "";
        if (m.Contains("x4plus-anime", StringComparison.OrdinalIgnoreCase)
            || m.Contains("x4plus_anime", StringComparison.OrdinalIgnoreCase)) return "x4plus-anime";   // 必须先于 x4plus 判
        if (m.Contains("x4plus", StringComparison.OrdinalIgnoreCase)) return "x4plus";
        if (m.Contains("animevideov3", StringComparison.OrdinalIgnoreCase)) return "animevideov3";
        // 【2026-09-14】自转的带降噪变体:名字是 general-**wdn**-x4v3,不含 "general-x4v3",
        // 所以要单独判(顺序无所谓,但必须在最后那个 null 之前)。
        if (m.Contains("wdn-x4v3", StringComparison.OrdinalIgnoreCase)
            || m.Contains("wdn_x4v3", StringComparison.OrdinalIgnoreCase)) return "wdn-x4v3";
        if (m.Contains("general-x4v3", StringComparison.OrdinalIgnoreCase)
            || m.Contains("general_x4v3", StringComparison.OrdinalIgnoreCase)) return "general-x4v3";
        if (m.Contains("upconv_7_photo", StringComparison.OrdinalIgnoreCase)) return "upconv_7_photo";
        if (m.Contains("cunet", StringComparison.OrdinalIgnoreCase)) return "cunet";
        return null;
    }

    /// <summary>查"该模型 × 该引擎倍率"的实测超分单价(秒/帧 @1080p 源);没实测过返回 null(调用方回退旧顺序),
    /// 出处写进 <paramref name="provenance"/>。
    /// 【口径】只认本仓库真机实测表 <see cref="UpscaleRates"/> —— "在线参数表"随该功能于 2026-09-14 删除,
    /// 原先的"引擎|模型键|倍率"键与两侧归一(`NormalizeEngine` / `CanonicalUpscaleKey*`)也一并删掉了。</summary>
    public static double? LookupUpscaleSecondsPerFrame(string? model, int engineScale, out string provenance)
    {
        string? key = NormalizeModel(model);
        if (key != null)
            foreach (var r in UpscaleRates)
                if (r.ModelKey == key && r.EngineScale == engineScale) { provenance = r.Provenance; return r.SecondsPerFrame1080p; }
        provenance = "该模型/倍率组合没有实测单价【待实测标定】";
        return null;
    }

    /// <summary>把 1080p 基准单价按源面积缩放(关键规律:超分成本 ∝ 源面积,与输出倍率几乎无关)。
    /// 明确区分"实测锚点"(=1080p)与"按面积外推":provenance 里已注明,调用方在日志里照写。</summary>
    public static double ScaleUpscaleCostToPixels(double secondsPerFrame1080p, long srcPixels)
    {
        if (srcPixels <= 0) return secondsPerFrame1080p;
        return secondsPerFrame1080p * (srcPixels / ReferencePixels1080p);
    }

    /// <summary>补帧"每个输出帧"的成本(秒):按面积在**三个真机实测锚点**之间分段线性内插,
    /// 区间外按相邻段斜率外推(带极小值兜底)。
    /// 【口径出处】三个锚点 0.0807 / 0.2776 / 0.5186 秒/输出帧 = 2026-09-13 真机实测(1080p / 2160p / 4320p);
    /// **外部没有任何公开的"补帧秒/帧"表可参照**(见 <c>ExternalPractice</c>)。
    /// (原先锚点表可被"在线参数"覆盖、另有一个 `ResolveInterpAnchors()` 解析函数与 `1440p` 可选锚点 ——
    /// 那些都随在线参数功能于 2026-09-14 删除;删掉后这里读的就是下面两个实测数组本身。)</summary>
    public static double InterpSecondsPerOutputFrame(long pixels)
    {
        long[] px = InterpAnchorPixels;
        double[] sc = InterpAnchorSeconds;
        if (pixels <= 0) return sc[0];
        // 低于最小锚点:用第一段斜率外推(小图不会更贵,但不许算出 0/负数)
        if (pixels <= px[0]) return Math.Max(MinInterpSecondsPerFrame, sc[0] - (px[0] - pixels) * (sc[1] - sc[0]) / (px[1] - px[0]));
        for (int i = 0; i + 1 < px.Length; i++)
            if (pixels <= px[i + 1])
                return sc[i] + (pixels - px[i]) * (sc[i + 1] - sc[i]) / (px[i + 1] - px[i]);
        return sc[^1] + (pixels - px[^1]) * (sc[^1] - sc[^2]) / (px[^1] - px[^2]);
    }

    /// <summary>一次判定用的成本输入(可注入 → 单测能构造"边际不足"等边界)。</summary>
    public readonly record struct CostInput(string ModelKey, int EngineScale, bool Measured, double UpscalePerFrame1080p);

    /// <summary>顺序判定结果(含判据用的四个数字与预估节省,便于日志/单测/事后审计)。</summary>
    public readonly record struct Decision(
        bool UpscaleFirst, double OldOrderSeconds, double NewOrderSeconds, double SavingsSeconds, double SavingsPercent,
        double ThresholdSecondsPerFrame, double UpscalePerFrame, double InterpLoPerFrame, double InterpHiPerFrame,
        double SourcePixels, long HiPixels, bool MarginInsufficient, bool Measured, string Reason)
    {
        /// <summary>可审计的一行日志(用户要求:一行写清模型/倍率/分辨率/三个成本/门槛/节省/结论)。</summary>
        public string LogLine =>
            $"顺序判定:{Reason} → u={UpscalePerFrame:0.####}s、r_lo={InterpLoPerFrame:0.####}s、r_hi={InterpHiPerFrame:0.####}s、"
            + $"门槛={ThresholdSecondsPerFrame:0.####}s、"
            + (SavingsSeconds >= 0
                ? $"预估节省 {SavingsSeconds:0.##}s({SavingsPercent:0.#}%)"
                : $"新顺序反而更慢 {Math.Abs(SavingsSeconds):0.##}s({Math.Abs(SavingsPercent):0.#}%)")
            + $" → 选择 {(UpscaleFirst ? "新顺序(超分→补帧)" : "旧顺序(补帧→超分)")}";
    }

    /// <summary>按实测表判定(生产入口)。
    /// <param name="engine">引擎("realesrgan"/"waifu2x")。
    /// <param name="model">模型名(引擎侧名,如 realesr-animevideov3 / models-cunet)。
    /// <param name="scale">目标超分倍率(UI 口径;引擎倍数由 Core.EngineScalePolicy 推)。
    /// <param name="interpScale">补帧倍率(1 = 不补帧 → 顺序无意义)。
    /// <param name="srcW"/><param name="srcH">源分辨率。
    /// <param name="sourceFrames">源帧数(只影响两侧总成本的绝对值,N 会被约掉,不影响判据;给 0 也能判)。</param>
    /// <param name="minSavingsPercent">安全边际;省略(= <see cref="UseBuiltInMinSavings"/>)→ 用内置
    /// <see cref="MinSavingsPercent"/>。</param>
    public static Decision Decide(string? engine, string? model, double scale, int interpScale, int srcW, int srcH, int sourceFrames = 900,
        double areaScale = 0, double minSavingsPercent = UseBuiltInMinSavings)
    {
        int engineScale = Math.Max(1, AlhPro.Core.EngineScalePolicy.Decide(engine ?? "", model ?? "", scale).EngineScale);
        double? up = LookupUpscaleSecondsPerFrame(model, engineScale, out string prov);
        var cost = new CostInput(NormalizeModel(model) ?? "?", engineScale, up != null, up ?? 0);
        var d = Decide(cost, scale, interpScale, srcW, srcH, sourceFrames, minSavingsPercent, areaScale);
        return up == null ? d with { Reason = $"{NormalizeModel(model) ?? (model ?? "?")} @ {engineScale}x(实测出处:{prov})" } : d;
    }

    /// <summary>按给定成本判定(可注入成本 → 单测能覆盖"边际不足"等边界)。
    /// 规则:①补帧倍率 &lt;2 → 顺序无意义,旧顺序;②成本未实测 → 旧顺序【待实测标定】;
    /// ③按判据算两侧总成本并比较;④新顺序更省但节省 &lt; 安全边际% → 仍保持旧顺序(边际不足)。
    /// <paramref name="minSavingsPercent"/> 省略(= <see cref="UseBuiltInMinSavings"/>)时用内置
    /// <see cref="MinSavingsPercent"/>;显式传具体值(含 0)则**完全听调用方**。</summary>
    public static Decision Decide(CostInput cost, double scale, int interpScale, int srcW, int srcH, int sourceFrames = 900,
        double minSavingsPercent = UseBuiltInMinSavings, double areaScale = 0)
    {
        if (minSavingsPercent < 0) minSavingsPercent = MinSavingsPercent;
        int k = interpScale < 1 ? 1 : interpScale;
        // areaScale = 补帧真正吃到的那批帧相对源帧的放大倍数("1x 缩回"时超分后帧会被缩回原尺寸 → 传 1.0;
        // 不传(=0)就用 scale。它只影响"放大后面积",不影响超分单价查表用的引擎倍率。)
        double sArea = areaScale > 0 && double.IsFinite(areaScale) ? areaScale : scale;
        if (sArea <= 0) sArea = 1.0;
        long srcPixels = (long)Math.Max(1, srcW) * Math.Max(1, srcH);
        long hiPixels = (long)Math.Max(1, Math.Round(srcW * sArea)) * (long)Math.Max(1, Math.Round(srcH * sArea));
        double u = ScaleUpscaleCostToPixels(cost.UpscalePerFrame1080p, srcPixels);
        double rLo = InterpSecondsPerOutputFrame(srcPixels);
        double rHi = InterpSecondsPerOutputFrame(hiPixels);
        double threshold = k > 1 ? k * (rHi - rLo) / (k - 1) : double.PositiveInfinity;

        long n = Math.Max(1, sourceFrames);
        double oldTotal = k * n * (rLo + u);
        double newTotal = n * u + k * n * rHi;
        double saving = oldTotal - newTotal;
        double pct = oldTotal > 0 ? saving / oldTotal * 100.0 : 0;
        bool margin = pct < minSavingsPercent;

        if (k < 2)
            return new Decision(false, oldTotal, newTotal, saving, pct, threshold, u, rLo, rHi, srcPixels, hiPixels, false, cost.Measured,
                "补帧关闭/单倍(k<2):两侧成本同量级,顺序无收益");
        // 【非法入参必须回退旧顺序】倍率/分辨率非法时"放大后面积"会退化成 1px,门槛被算成负数 ——
        // 那样的结论毫无意义(拿实测的 2x 超分成本去和 1px 的补帧成本比)。一律旧顺序 + 写明原因。
        if (!(scale > 0) || !double.IsFinite(scale) || srcW <= 0 || srcH <= 0)
            return new Decision(false, oldTotal, newTotal, saving, pct, threshold, u, rLo, rHi, srcPixels, hiPixels, false, cost.Measured,
                $"入参非法(超分倍率 {scale:0.###}、源 {srcW}×{srcH})→ 无法按面积算成本,保守用旧顺序");
        if (!cost.Measured)
            return new Decision(false, oldTotal, newTotal, saving, pct, threshold, u, rLo, rHi, srcPixels, hiPixels, false, false,
                $"{cost.ModelKey} @ {cost.EngineScale}x(无实测单价,【待实测标定】,保守用旧顺序)");
        bool useNew = u > threshold && !margin;
        string why = margin
            ? (saving > 0 ? $"新顺序只省 {pct:0.#}%(< {minSavingsPercent:0.#}% 安全边际)→ 保持旧顺序" : $"新顺序反而更慢 {Math.Abs(pct):0.#}%")
            : (useNew ? $"u {u:0.####}s > 门槛 {threshold:0.####}s(超分很贵,先超分能少算补帧)" : $"u {u:0.####}s ≤ 门槛 {threshold:0.####}s(超分便宜,先补帧更省)");
        return new Decision(useNew, oldTotal, newTotal, saving, pct, threshold, u, rLo, rHi, srcPixels, hiPixels, margin, true, why);
    }
}
