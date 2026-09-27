namespace AlhPro.Core;

/// <summary>把「源素材的色彩事实」折算成**输出侧**要写的色彩标记(纯逻辑,可单测)。
///
/// 【为什么需要它】现在的输出**一律**写死 bt709/tv(`VideoService.EncoderArgs` 里那个常量 + VUI bsf)。
/// 三种情况下它的对错完全不同:
///   · **源是 HDR / 广色域**:拆帧那一步已经用 zscale 真把像素转成 bt709 ⇒ 输出写 bt709 是**对的**;
///   · **源标记非法/未指定**(reserved/unspecified/unknown):输入侧也按 bt709 解释 ⇒ 输出写 bt709 是
///     **自洽的假设**(不知道真值,这样至少首尾一致);
///   · **源是"已知 SDR、但不是 bt709"**(典型是 SD 的 bt470bg / smpte170m):像素**没被转换**、
///     却被贴上 bt709 标签 ⇒ 任何按标签解释的播放器都会渲染得和源片不一样 ——
///     用户报的"开超分/补帧后看起来变色"里就包含这一种。
/// 本类只处理第三种:把**源的标签原样**折算成 ffmpeg 参数与 H.264/H.265 的 VUI 码;前两种返回
/// <see cref="Bt709Args"/> / <see cref="Bt709VuiBsf"/>,与改动前**一字不差**。
///
/// 【为什么必须有单测】ffmpeg 的 `-color_*` 参数名(如 `bt470bg`)与 H.273 的 VUI **数字码**(如 `5`)
/// 是两套东西,写错一个数字 = 全部成片标错色,而**画面上完全看不出来**(只有按标签解释的播放器才看得出)。</summary>
public static class ColorTagPlan
{
    /// <summary>今天的口径,也是所有"说不清"情况的兜底:BT.709 / tv(与改动前逐字一致)。</summary>
    public const string Bt709Args = " -color_range tv -colorspace bt709 -color_primaries bt709 -color_trc bt709";

    /// <summary>BT.709 的 VUI 码(bits 过滤器参数片段,不含 `-bsf:v xxx_metadata=` 前缀)。</summary>
    public const string Bt709VuiBsf = "colour_primaries=1:transfer_characteristics=1:matrix_coefficients=1";

    /// <summary>色彩原色(primaries)名字 → H.273 码。**认不出一律给 1(bt709)**,与今天的输出一致。</summary>
    public static int PrimariesCode(string? name) => Norm(name) switch
    {
        "bt709" => 1,
        "bt470m" => 4,
        "bt470bg" => 5,
        "smpte170m" => 6,
        "smpte240m" => 7,
        "film" => 8,
        "bt2020" => 9,
        _ => 1,
    };

    /// <summary>传递特性(transfer)名字 → H.273 码。认不出一律 1(bt709)。</summary>
    public static int TransferCode(string? name) => Norm(name) switch
    {
        "bt709" => 1,
        "bt470m" => 4,
        "bt470bg" => 5,
        "smpte170m" => 6,
        "smpte240m" => 7,
        "linear" => 8,
        "log100" => 9,
        "log316" => 10,
        "iec61966-2-4" => 11,
        "bt2020-10" => 14,
        "bt2020-12" => 15,
        "smpte2084" => 16,
        "smpte428" => 17,
        "arib-std-b67" => 18,
        _ => 1,
    };

    /// <summary>矩阵系数(color_space / matrix)名字 → H.273 码。认不出一律 1(bt709)。</summary>
    public static int MatrixCode(string? name) => Norm(name) switch
    {
        "bt709" => 1,
        "fcc" => 4,
        "bt470bg" => 5,
        "smpte170m" => 6,
        "smpte240m" => 7,
        "ycgco" => 8,
        "bt2020nc" => 9,
        "bt2020c" => 10,
        _ => 1,
    };

    /// <summary>源标签 → 输出标记:`(ffmpeg 色彩参数, VUI bsf 参数, 一句可写日志的说明)`。
    ///
    /// 【只有这一种情况"跟随源"】源三件套(primaries / transfer / matrix)**都已知**、**都不是 bt709**、
    /// 且源没有被转换过(`convertedToBt709 == false`)。其余一律回落到 bt709/tv —— 与改动前逐字一致,
    /// 所以这次改动**不会**动到 HDR 素材、也不动标记缺失的素材。</summary>
    public static (string args, string vuiBsf, string describe) ForSource(
        string? range, string? primaries, string? transfer, string? matrix, bool convertedToBt709)
    {
        if (convertedToBt709)
            return (Bt709Args, Bt709VuiBsf, "源已被转换/或本就是 BT.709 ⇒ 输出标 bt709");

        string p = Norm(primaries), t = Norm(transfer), m = Norm(matrix);
        if (!IsKnown(p) || !IsKnown(t) || !IsKnown(m))
            return (Bt709Args, Bt709VuiBsf, "源色彩标记不完整(unknown/reserved)⇒ 沿用 bt709,与改动前一致");

        if (p == "bt709" && t == "bt709" && m == "bt709")
            return (Bt709Args, Bt709VuiBsf, "源就是 bt709 ⇒ 输出标 bt709");

        string rng = Norm(range) == "pc" ? "pc" : "tv";
        string args = $" -color_range {rng} -colorspace {m} -color_primaries {p} -color_trc {t}";
        string bsf = $"colour_primaries={PrimariesCode(p)}:transfer_characteristics={TransferCode(t)}"
                   + $":matrix_coefficients={MatrixCode(m)}";
        return (args, bsf, $"输出**跟随源**:{p}/{t}/{m} · range={rng}(源是已知 SDR、且不是 bt709)");
    }

    private static string Norm(string? v) => (v ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>与 VideoService 里那套口径同源:保留值/未指定/空都算"未知"(它们**不能**当作"已知但不是 bt709"——
    /// 2026-09-18 那次误判就是栽在这里)。</summary>
    private static bool IsKnown(string v) =>
        v.Length > 0 && v != "unknown" && v != "unspecified" && v != "reserved" && v != "none" && v != "na";
}
