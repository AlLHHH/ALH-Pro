namespace AlhPro.Core;

/// <summary>ncnn 真机探测结论的【判定口径】(纯逻辑,可单测)。
///
/// 【为什么单独抽出来】2026-09-22 用户机器(RTX 5060 Laptop)诊断包实测暴露了一个判死的缺陷:
/// `ncnn-probe.txt` 里有三条结论 ——
///     realesrgan2026|0|anime4k   False    probe failed: 无响应(超时被强杀)
///     realesrgan2026|0|          True     probe ok; model=(default)
///     waifu2x|0|                 True     probe ok; model=(default)
/// 而旧口径把"同一引擎下任意一支模型失败"当成【整条引擎不可用】⇒ `realesrgan2026` 整体被判
/// "实测不可用 → 走 ONNX"，于是**四支自训模型(现实/游戏,只有 ncnn 权重、ONNX 里根本没有)
/// 一起被判死** —— 用户看到的就是"50 系跑 real 跑不了"。
/// 但 default 探测明明通过、后来真跑也通过(`走 ncnn-Vulkan(50 系未禁用,走最快路径)`)。
/// 所以正确口径是【按模型判】:
///   ① 引擎级可用性只认 `引擎|设备|`(不带模型)那条 = 引擎本身能不能在本机跑通;
///   ② 单支模型的失败只影响那一支(它自己退 ONNX 或换模型),不牵连同引擎的其它模型;
///   ③ 某支模型没有自己的结论时,回落到引擎级结论(没测过就不额外禁止)。
///
/// 【键格式】与 `NcnnVerdictKey.For` 一致:`引擎|设备号|模型`(模型为空 = default 探测)。</summary>
public static class NcnnModelVerdicts
{
    /// <summary>一条探测结论。</summary>
    public readonly record struct Entry(string Key, bool Ok, long AtUnix);

    /// <summary>【2026-09-24】"伪模型"条目:它们**不是 ncnn 引擎的模型**,结论不该参与
    /// "这个 ncnn 引擎在这张卡上能不能用"的判定。两支都来自界面下拉的 Tag(见 <see cref="NcnnProbePlan"/>):
    ///   · <see cref="Anime4k.ModelTag"/>(`anime4k`)—— Anime4K 修复走着色器,根本不用 ncnn;
    ///   · <see cref="Upscale1x.RealTag"/>(`alhpro-real1x`)—— 它自己写明"不是真模型",真权重是
    ///     <see cref="Upscale1x.RealEngineModel"/>。
    ///
    /// 【为什么必须挡住 · 2026-09-24 真机实测】视频预检曾把当前选中的 Tag 原样当模型名喂给 ncnn
    /// ⇒ 引擎找不到权重 ⇒ 60 秒无响应被强杀 ⇒ 落一条 `realesrgan2026|0|&lt;Tag&gt; = false` 的**假失败**。
    /// 在没有"引擎级(default)结论"的机器上(独显 + 核显 ⇒ 不走免探测快速通道、每次探测都带模型
    /// ⇒ 从不写 default),旧兜底"任一支失败即整条引擎不可用"会把 realesrgan 整体判走 ONNX
    /// ⇒ 视频 **2x 超分实测 3880 ms/帧**,而软件自己标称 animevideov3 是 0.26~0.30 秒/帧(差 13 倍)。
    /// 备注:5060 那台机器因为有 default 行,`EngineUsable` 先命中,所以这条从没暴露。
    /// ⚠ 常量引用而不是字面量:Tag 改名时这里跟着走(否则清单静默失配、毒化回归)。</summary>
    public static readonly string[] NonNcnnModels = { Anime4k.ModelTag, Upscale1x.RealTag };

    /// <summary>这个模型名是不是"伪模型"(不属于 ncnn 引擎,别拿它的结论判 ncnn)。大小写/空白不敏感。</summary>
    public static bool IsNonNcnnModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        string m = model.Trim().ToLowerInvariant();
        foreach (var bad in NonNcnnModels) if (m == bad) return true;
        return false;
    }

    /// <summary>引擎级可用性:只认不带模型的那条(default 探测)。
    /// 返回 null = 该引擎还没有 default 结论(调用方按"未测"处理,不擅自禁用)。</summary>
    public static bool? EngineUsable(IEnumerable<Entry> entries, string engineId, int gpuId)
    {
        if (entries == null) return null;
        string prefix = $"{engineId}|{gpuId}|";
        foreach (var e in entries)
        {
            if (e.Key == prefix) return e.Ok;          // 模型段为空的那条
        }
        return null;
    }

    /// <summary>【2026-09-24】没有 default 结论时的兜底口径:**只统计真 ncnn 模型**的结论
    /// (伪模型如 anime4k 一律跳过;键前缀与 <see cref="EngineUsable"/> 成对)。
    /// 返回 null = 该引擎+该卡连一条真模型结论都没有(调用方按"未测"处理)。
    /// 三条规则同 <see cref="NcnnVerdictKey.Summarize"/>:全通过 → true;出现 false → false(保守)。</summary>
    public static bool? ModelOnlyRisk(IEnumerable<Entry> entries, string engineId, int gpuId)
    {
        if (entries == null) return null;
        string prefix = $"{engineId}|{gpuId}|";
        var oks = new List<bool>();
        foreach (var e in entries)
        {
            if (!e.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (IsNonNcnnModel(e.Key.Substring(prefix.Length))) continue;   // 伪模型:不是 ncnn 的事
            oks.Add(e.Ok);
        }
        return NcnnVerdictKey.Summarize(oks);
    }

    /// <summary>某支模型能不能走 ncnn:优先它自己的结论;没有就回落到引擎级结论。
    /// 返回 null = 完全没测过(调用方不该据此禁用)。</summary>
    public static bool? IsModelUsable(IEnumerable<Entry> entries, string engineId, int gpuId, string? model)
    {
        if (entries == null) return null;
        if (!string.IsNullOrEmpty(model))
        {
            string key = $"{engineId}|{gpuId}|{model}";
            foreach (var e in entries)
                if (e.Key == key) return e.Ok;         // 该模型自己的结论优先
        }
        return EngineUsable(entries, engineId, gpuId);
    }

    /// <summary>汇总成一行(自检报告/日志用)。
    /// 【为什么要按模型说】旧文案只报"2 支模型中有失败" ⇒ 用户看到"整条引擎不可用",
    /// 而实际只有一支失败。这里明确写出"引擎可用;仅 X 未通过"。</summary>
    public static string Describe(IEnumerable<Entry> entries, string engineId, int gpuId)
    {
        if (entries == null) return $"{engineId}=未测";
        string prefix = $"{engineId}|{gpuId}|";
        int ok = 0, fail = 0;
        var failed = new List<string>();
        bool? engine = EngineUsable(entries, engineId, gpuId);
        foreach (var e in entries)
        {
            if (!e.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (e.Ok) ok++;
            else
            {
                fail++;
                string model = e.Key.Substring(prefix.Length);
                // 伪模型(anime4k 这类着色器条目)不是 ncnn 的事:标出来,免得报告看着像"ncnn 有一支跑不了"
                failed.Add(string.IsNullOrEmpty(model) ? "(default)"
                    : IsNonNcnnModel(model) ? model + "(非 ncnn 模型,不计入判定)" : model);
            }
        }
        if (ok == 0 && fail == 0) return $"{engineId}=未测(首次处理时自动实测)";
        if (engine == false) return $"{engineId}=实测不可用→走 ONNX({fail} 支模型失败)";
        // 引擎可用:如实指出个别失败的模型,别让人以为整条坏了
        return fail == 0
            ? $"{engineId}=实测可用→走 ncnn({ok} 支模型全通过)"
            : $"{engineId}=实测可用→走 ncnn(仅 {string.Join("、", failed)} 未通过,其余 {ok} 支照走 ncnn)";    }
}
