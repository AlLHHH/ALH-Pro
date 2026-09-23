namespace AlhPro.Core;

/// <summary>视频处理的「绝不落 CPU」策略(2026-09-23 用户要求:「落到 CPU 根本不现实太慢了 —— 应该重试几次,
/// 如果还是不行直接报错,不应该落到 CPU」)。
///
/// 【真机依据】RTX 5060 Laptop 上硬件编码**瞬时**失败(ffmpeg exit -542398533)后,旧逻辑一次都不重试就掉到
/// libx264:编码阶段 17.0s → 115.7s(**7 倍**);而同一台机器"上一次任务"的 nvenc 还是 16.7 fps
/// ⇒ 属瞬时失败,重试是有依据的解,而不是"这台机器编不了"。更糟的是那行回报还误标成"(硬编)",
/// 用户与排查者都看不出已经掉到 CPU —— 这两点一起才是本条策略的由来。
///
/// 【为什么只覆盖视频】图片放大 / 抠图 / 音频的 CPU 路径是另一个量级(几百张图、单张推理、音频分块),
/// 那些功能用 CPU 完全可用 ⇒ 这条策略**只**管视频。视频侧的超分/补帧早就是"不落 CPU"的硬约定
/// (见 <c>RifeOnnxService.EnsureDeviceUsable</c> 与 EsrganOnnxService 里多处"不降级到慢速 CPU"),
/// 本次把**编码器**这最后一处大口子堵上。
///
/// 【唯一例外】用户在「设置 → 计算设备」里显式选「CPU 计算」(引擎设备号 &lt; 0)—— 那是他自己的选择,照跑。
/// 纯逻辑、零副作用 ⇒ 可单测(见 <c>CpuFallbackPolicyTests</c>)。</summary>
public static class CpuFallbackPolicy
{
    /// <summary>是否允许落 CPU:只有用户显式选了 CPU(引擎设备号 &lt; 0)才允许。
    /// 0/1/2… 都算"要用 GPU"—— 那种情况下 CPU 是**降级**而不是选择。</summary>
    public static bool AllowsCpuFallback(int gpuId) => gpuId < 0;

    /// <summary>硬件编码失败后的重试间隔(毫秒)。第一次 3 秒(挡瞬时失败:子进程/驱动刚崩过),
    /// 第二次 8 秒(给驱动一点时间把上一个失败的编码会话彻底释放)。</summary>
    public static readonly int[] HwEncodeRetryDelaysMs = { 3000, 8000 };

    /// <summary>硬件编码的总尝试次数 = 首次 + 重试次数。</summary>
    public static int HwEncodeTotalAttempts => 1 + HwEncodeRetryDelaysMs.Length;

    /// <summary>第 <paramref name="attempt"/> 次(1 起)失败后该等多少毫秒再重试;已无重试则返回 0。</summary>
    public static int RetryDelayMsAfterAttempt(int attempt)
        => attempt >= 1 && attempt <= HwEncodeRetryDelaysMs.Length ? HwEncodeRetryDelaysMs[attempt - 1] : 0;

    /// <summary>本机连一个可用硬编都没有时的报错正文(给用户看的,必须让他能自己救回来)。</summary>
    public static string DescribeNoHwEncoder()
        => "本机没有可用的硬件编码器(已实测 nvenc / amf / qsv 均无法编码),视频处理只能退回 CPU 软编"
         + "——按当前设定不予使用(实测 CPU 软编慢 7 倍以上,一段几分钟的视频会拖成几小时)。\n"
         + "请更新显卡驱动后重试;若你确实要用 CPU 软编(会很慢),请到「设置 → 计算设备」选「CPU 计算」再试。";

    /// <summary>硬件编码重试若干次仍失败时的报错正文。</summary>
    public static string DescribeHwEncodeFailure(string encoder, int attempts, string? reason, bool driverTooOld)
    {
        string why = string.IsNullOrWhiteSpace(reason) ? "(未给出原因)" : reason.Trim();
        string advice = driverTooOld
            ? "看起来是【显卡驱动过旧】(nvenc 需要较新驱动),请更新显卡驱动后重试"
            : "请更新显卡驱动、关闭占用显卡的程序后重试";
        return $"硬件编码({encoder})连续 {attempts} 次失败,已停止 —— 按当前设定不会退回 CPU 软编"
             + "(实测 CPU 软编慢 7 倍以上)。\n最后一次原因:" + why + "\n" + advice
             + ";若你确实要用 CPU 软编(会很慢),请到「设置 → 计算设备」选「CPU 计算」再试。";
    }
}
