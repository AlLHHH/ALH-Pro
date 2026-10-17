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

    /// <summary>补帧(RIFE)探测结论里"尺寸档"那一段 —— 只此一处实现。
    /// <para>
    /// 【2026-10-15 · 5760×5760 毒化事故,真机证据】旧实现只有 sd/fhd/uhd 三档(uhd = ≥3840×2160):
    /// 一次 5760×5760(33.2 Mpx)的探测得出"出图但画面坏帧"之后,这条结论被**同档**的 3840×2160
    /// (8.3 Mpx)任务沿用 —— 可 5760×5760 恰好落在 <see cref="InterpSizePolicy.NcnnRefusePixels"/>
    /// 定义的实测故障带里(ncnn 在该尺寸会静默全黑,本来就不会交给 ncnn 跑),而 4K 上 ncnn 是好的。
    /// 后果:之后所有 4K 补帧都"沿用已缓存结论:不可用 → 走 ONNX"(慢),直到结论过期。
    /// </para>
    /// <para>
    /// 现在:① 故障带单独一档(<c>uhd8k</c>),它与 4K 档互相不背书(档与档之间只做精确匹配);
    /// ② 4K 档由 <c>uhd</c> 改名为 <c>uhd4k</c> —— 旧版本留下的 <c>…:uhd:…</c> 结论(探测尺寸不明,
    /// 可能在 4K~33 Mpx 之间任何尺寸)一律不再命中,于是在真实尺寸上重探一次即自愈;
    /// 也不会因为"每次都读不到"而反复重探(新结论写在 <c>uhd4k</c> 上)。
    /// </para></summary>
    public static string SizeBucket(long pixels)
        => pixels >= InterpSizePolicy.NcnnRefusePixels ? "uhd8k"
         : pixels >= 3840L * 2160 ? "uhd4k"
         : pixels >= 1920L * 1080 ? "fhd"
         : "sd";

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
