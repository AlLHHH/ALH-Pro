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
/// 【2026-09-23 二次修正 · 例外条款作废(本轮 B8)】原文写着「唯一例外:用户在『设置 → 计算设备』里显式选
/// 『CPU 计算』(引擎设备号 &lt; 0)—— 那是他自己的选择,照跑」。但设置页的「计算设备」下拉**只列显卡、
/// 早就不再提供 CPU 选项**(见 MainPage.xaml.cs 那段「不再提供 CPU 选项」的说明),所以那条例外在实际产品里
/// **无处可达** ⇒ 它只会变成一句"照着做也找不到"的假建议(报错文案原文就写着"请到设置里选「CPU 计算」")。
/// 更麻烦的是它还留下一个真口子:Vulkan 自检失败时 MainPage 会把 GpuIndex 临时置 -1(仅本次会话,
/// 目的只是别把用户选好的卡覆盖掉),按旧判据 <c>gpuId &lt; 0</c> 就等于"用户主动选了 CPU"
/// ⇒ 视频会**静默**走 CPU 软编(正是这条策略要消灭的东西)。
/// 现在把口径写死:<see cref="AllowsCpuFallback"/> 恒为 false —— 视频要么用硬编,要么在开跑前就报错。
/// 若将来真把「CPU 计算」放回设置页,只需让这一个方法接到那个开关上(其余调用点一个都不用动)。
///
/// 【2026-09-30 · t64 C:重试策略只管"瞬时失败"】本类的重试间隔只适用于**跑起来之后的瞬时失败**
/// (5060 那台:`exit -542398533`,上一次任务 16.7 fps、下一次就编不了 ⇒ 重试有依据)。
/// 编码器**打不开/初始化失败**是另一条路:同一个编码器重试不会有不同结果,正解是换本机另一个
/// 实测可用硬编(判据与候选链在 <see cref="VideoEncodeGuard"/>)⇒ 那条路的报错文案是
/// <see cref="DescribeHwEncodeInitFailure"/>,不是"连续 3 次失败"。
/// 纯逻辑、零副作用 ⇒ 可单测(见 <c>CpuFallbackPolicyTests</c>)。</summary>
public static class CpuFallbackPolicy
{
    /// <summary>视频是否允许落 CPU 编码:**恒为 false**。
    /// 【为什么不留参数】判据原先收一个 <c>gpuId</c>,用 <c>gpuId &lt; 0</c> 当"用户选了 CPU"的证据;
    /// 但界面上已经没有这个选项了,那个值只可能来自"自检失败临时置 -1"⇒ 它是**自动降级**,不是用户选择。
    /// 留个假口子比删掉更危险:没人看得出它到底代表谁的意思。</summary>
    public static bool AllowsCpuFallback() => false;

    /// <summary>硬件编码失败后的重试间隔(毫秒)。第一次 3 秒(挡瞬时失败:子进程/驱动刚崩过),
    /// 第二次 8 秒(给驱动一点时间把上一个失败的编码会话彻底释放)。</summary>
    public static readonly int[] HwEncodeRetryDelaysMs = { 3000, 8000 };

    /// <summary>硬件编码的总尝试次数 = 首次 + 重试次数。</summary>
    public static int HwEncodeTotalAttempts => 1 + HwEncodeRetryDelaysMs.Length;

    /// <summary>第 <paramref name="attempt"/> 次(1 起)失败后该等多少毫秒再重试;已无重试则返回 0。</summary>
    public static int RetryDelayMsAfterAttempt(int attempt)
        => attempt >= 1 && attempt <= HwEncodeRetryDelaysMs.Length ? HwEncodeRetryDelaysMs[attempt - 1] : 0;

    /// <summary>本机连一个可用硬编都没有时的报错正文(给用户看的,必须让他能自己救回来)。
    /// 【文案红线 · B8】不许再让用户去设置里找「CPU 计算」——那个选项不存在(找得到才怪)。
    /// 能给的自救只有三条:更新驱动、重启、换/修显卡;实在不行就说清"这块功能本机用不了",
    /// 而不是把人支去一个点不到的地方。</summary>
    public static string DescribeNoHwEncoder()
        => "本机没有可用的硬件编码器(已实测 nvenc / amf / qsv 三种都无法编码,也可能是没检测到可用显卡),"
         + "视频处理按「不落 CPU」的设定直接停下。\n"
         + "为什么要停:实测 CPU 软编比硬编慢 7 倍以上(同一段片子编码 17.0 秒 → 115.7 秒),"
         + "几分钟的视频会拖成几小时,所以这里选择报错而不是悄悄变慢。\n"
         + "怎么办:① 更新显卡驱动(或重装一次驱动)后重试;② 刚更新过驱动的话重启一次电脑;"
         + "③ 笔记本请确认没有把独显禁用(设备管理器里显卡应无黄色感叹号)。\n"
         + "说明:视频处理**不提供** CPU 软编选项 —— 图片放大 / 抠图 / 音频处理不受影响(那三项用 CPU 完全可用)。";

    /// <summary>硬件编码重试若干次仍失败时的报错正文。</summary>
    public static string DescribeHwEncodeFailure(string encoder, int attempts, string? reason, bool driverTooOld)
    {
        string why = string.IsNullOrWhiteSpace(reason) ? "(未给出原因)" : reason.Trim();
        string advice = driverTooOld
            ? "看起来是【显卡驱动过旧】(nvenc 需要较新驱动),请更新显卡驱动后重试"
            : "请更新显卡驱动、关闭占用显卡的程序后重试;偶发抽风时重启软件再试一次最有效";
        return $"硬件编码({encoder})连续 {attempts} 次失败,已停止 —— 按当前设定不会退回 CPU 软编"
             + "(实测 CPU 软编慢 7 倍以上)。\n最后一次原因:" + why + "\n" + advice + "。";
    }

    /// <summary>【2026-09-30 · t64 C】编码器**打不开**(初始化失败)且本机再没有别的可用硬编时的报错正文。
    ///
    /// 【为什么必须与 <see cref="DescribeHwEncodeFailure"/> 分开】那条说的是"**连续 3 次**失败" ——
    /// 那是**瞬时失败**的结论(有重试才有这个数)。而"打不开编码器"是**确定性**的:同一个编码器
    /// 重试再多遍还是打不开(2026-09-27 那台 3 次全是 `Could not open encoder before EOF`)。
    /// 沿用旧文案会同时骗两个人:用户以为"它替我重试了很多次"(其实我们一次都没重试 —— 正解是
    /// **换本机另一个可用硬编**,那台机器上 `hevc_qsv` 就在可用列表里),排查者也会去数重试次数。
    ///
    /// 【文案红线】同 <see cref="DescribeNoHwEncoder"/>:不许提"去设置里选 CPU 软编"。</summary>
    /// <param name="encoder">打不开的那个编码器。</param>
    /// <param name="triedEncoders">本次已经试过的编码器(含换过的),用于让用户看清"换了也没成"。</param>
    /// <param name="reason">最后一次的 ffmpeg 原话(头+尾,见 <c>VideoEncodeGuard.DescribeProcessFailure</c>)。</param>
    public static string DescribeHwEncodeInitFailure(string encoder, string? triedEncoders, string? reason)
    {
        string why = string.IsNullOrWhiteSpace(reason) ? "(未给出原因)" : reason.Trim();
        string tried = string.IsNullOrWhiteSpace(triedEncoders) ? encoder : triedEncoders!.Trim();
        return $"硬件编码({encoder})打不开编码器(初始化失败),本机也没有别的实测可用硬编可换 —— 已停止"
             + "(按「视频不落 CPU」策略不退回 CPU 软编)。\n"
             + $"已试过的编码器:{tried}。\n"
             + "为什么不再重试:打不开是确定性的(同一个编码器重试还是打不开),换一个可用硬编才有意义 —— "
             + "这条路径上我们没有拿它反复试。\n"
             + "怎么办:① 更新显卡驱动(或重装一次)后重试;② 关闭占用显卡的程序/结束其它转码软件后重试;"
             + "③ 笔记本请确认独显没有被禁用。\n"
             + "说明:视频处理**不提供** CPU 软编选项(实测慢 7 倍以上)。\n最后一次原因:" + why;
    }
}
