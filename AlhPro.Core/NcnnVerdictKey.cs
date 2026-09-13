namespace AlhPro.Core;

/// <summary>
/// ncnn 真机探测结论的【键格式与汇总规则】—— 纯函数,可单测。
/// <para>
/// 【为什么抽出来 · F1,2026-09-13 自检】结论键原来是 <c>engine|gpu</c>,没有"模型"维度:
/// 于是"animevideov3 实测通过"会给 x4plus(实测最慢、最易出坏帧的那支)背书 7 天 ——
/// 同一张卡上不同模型的可用性本来就不一样,等于长期拿别人的体检报告用。
/// </para>
/// <para>
/// 现在键 = <c>engine|gpu|model</c>(null/空 = 引擎级结论,如 waifu2x)。键一旦带模型,
/// "这个引擎在这张卡上能不能用"就必须【按全部已测模型汇总】而不是查单一键 —— 三条规则:
/// 只要有一支实测不可用 → 不可用(保守);全部已测模型都通过 → 可用;一支都没测过 → 未测(交给启发式)。
/// </para>
/// <para>
/// 这里的三个函数是 UI 侧与缓存文件共用的唯一实现:键的拼法与匹配必须成对,否则"写进去读不出来"
/// 会表现成"每次都要重测一遍"(慢)甚至"永远未测"(诊断包里全是"未测",用户无法判断)。
/// </para>
/// </summary>
public static class NcnnVerdictKey
{
    /// <summary>键 = engineId|gpuId|模型(小写去空白;null/空 = 引擎级)。</summary>
    public static string For(string engineId, int gpuId, string? model)
        => engineId + "|" + gpuId + "|" + (model ?? "").Trim().ToLowerInvariant();

    /// <summary>取"该引擎+该 GPU 的全部模型结论"时的前缀。
    /// ⚠ 末尾那个 <c>|</c> 是关键:没有它,<c>engine|1</c> 会误匹配到 <c>engine|10|…</c>(双位数 GPU 编号),
    /// 于是把 10 号卡的结论当成 1 号卡的(单测有这一条)。</summary>
    public static string PrefixFor(string engineId, int gpuId) => engineId + "|" + gpuId + "|";

    /// <summary>该键是否属于"这个引擎 + 这张卡"(与 <see cref="PrefixFor"/> 严格成对)。</summary>
    public static bool BelongsTo(string key, string engineId, int gpuId)
        => key is not null && key.StartsWith(PrefixFor(engineId, gpuId), System.StringComparison.Ordinal);

    /// <summary>把多支模型的结论折叠成一条:空 → null(未测);全 true → true;出现 false → false(保守)。
    /// 顺序无关,且只要有一支失败就一定是 false —— 这条是本函数存在的全部理由。</summary>
    public static bool? Summarize(System.Collections.Generic.IEnumerable<bool>? measured)
    {
        if (measured is null) return null;
        bool? any = null;
        foreach (var ok in measured) any = (any ?? true) && ok;
        return any;
    }
}
