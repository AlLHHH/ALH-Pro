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
                failed.Add(string.IsNullOrEmpty(model) ? "(default)" : model);
            }
        }
        if (ok == 0 && fail == 0) return $"{engineId}=未测(首次处理时自动实测)";
        if (engine == false) return $"{engineId}=实测不可用→走 ONNX({fail} 支模型失败)";
        // 引擎可用:如实指出个别失败的模型,别让人以为整条坏了
        return fail == 0
            ? $"{engineId}=实测可用→走 ncnn({ok} 支模型全通过)"
            : $"{engineId}=实测可用→走 ncnn(仅 {string.Join("、", failed)} 未通过,其余 {ok} 支照走 ncnn)";
    }
}
