namespace AlhPro.Core;

/// <summary>
/// 补帧倍率【下拉序号 ↔ 实际倍率】的唯一映射。
/// <para>
/// 【为什么要单独抽出来 · 2026-09-13 用户报告】预设悬停摘要里「去重补帧4x」显示成 <c>2x</c> ——
/// 那处把<b>下拉序号</b>当倍率直接打印了(存的序号 2 就打 "2x";序号 0 更会打成 "0x")。
/// 而序号与倍率是两套值:0→2x、1→3x、2→4x、3→8x、4→12x、5→16x。
/// 同一张映射表在 UI 里被手抄了 5 份、摘要那处干脆没映射,而"抄多份"正是本项目反复出错的成因
/// (历史上补帧倍率的 switch 漏了 12x/16x,选高倍率会静默落回 2x,也是同一类问题)。
/// 故收敛为一份纯函数并加单测钉住,UI 侧一律调用这里。
/// </para>
/// </summary>
public static class InterpScaleMap
{
    /// <summary>下拉序号 → 实际倍率。0→2x, 1→3x, 2→4x, 3→8x, 4→12x, 5→16x。
    /// 越界(含 -1 / 下拉未选中时的 -1)按 <b>2x</b> —— 与界面默认档一致(2x 是所有补帧模型都支持的档)。</summary>
    public static int Multiplier(int index) => index switch
    {
        1 => 3,
        2 => 4,
        3 => 8,
        4 => 12,
        5 => 16,
        _ => 2,
    };

    /// <summary>下拉序号 → 显示文案(序号 2 → "4x")。用于预设摘要/提示,避免再出现"序号当倍率"的错。</summary>
    public static string Label(int index) => Multiplier(index) + "x";

    /// <summary>是否属于"高倍率补帧"(≥ 8x)—— 用于"处理时间显著增加"的黄字提示。
    /// 【为什么要有它】原先那处写成 <c>SelectedIndex is 3 or 4</c>(= 8x/12x),把 16x(序号 5)漏了,
    /// 于是最高倍率反而不提示耗时。用倍率判定就不会再漏档。</summary>
    public static bool IsHighRate(int index) => Multiplier(index) >= 8;

    /// <summary>该倍率是否只能由「通用画质」系列(V4)补帧模型做到。
    /// 3x / 12x / 16x 需要 V4 架构;其它模型只能按 2 的幂级联,故这三档对非 V4 模型必须回落到 2x。</summary>
    public static bool NeedsV4Model(int index) => index is 1 or 4 or 5;
}
