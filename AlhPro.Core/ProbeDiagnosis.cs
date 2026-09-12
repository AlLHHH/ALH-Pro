namespace AlhPro.Core;

/// <summary>ncnn 引擎"真机探测"失败的【形态】。
/// 【为什么必须区分】同一个"探测失败"背后是完全不同的两件事,对用户要说的话也完全不同:
///   · 初始化阶段就崩/无响应 —— 在 RTX 50 系(Blackwell)上这是 NVIDIA 驱动的已知缺陷
///     (驱动错误宣称支持 cooperative-matrix,应用一查属性就访问违例;见 NVIDIA 开发者论坛 369162 / 371808,
///      到驱动 610.43.03 仍未修复),**不是本软件的问题**;
///   · 引擎能出图但画面近黑/带状近黑 —— 属另一类问题(ncnn 的 Pipeline 被多线程共享等),
///     **绝不能甩给驱动**,否则会把排查方向带偏(我们已按此修过 -j 并发档)。
/// 混淆这两者,要么让用户白等驱动修复,要么让用户误以为软件坏了。</summary>
public enum ProbeFailureKind
{
    None = 0,
    EngineMissing,     // 引擎文件不存在(与显卡无关)
    StartupFailed,     // 进程起不来
    CrashExitCode,     // 非零退出码(含 0xC0000005 访问违例 —— 驱动缺陷的典型特征)
    NoOutput,          // 退出码 0 但没产出文件
    EmptyOutput,       // 产出文件存在但 0 字节
    DefectiveFrame,    // 出图但画面坏:整帧近黑 / 任一 1/3 条带近黑 / 通道失衡 / 亮度不对 / 被抹成一块平的
    Hang,              // 超时无响应(已强杀)
}

/// <summary>探测失败形态 → 判定与用户文案。纯函数,有单测守着两条硬约束:
/// ①"初始化即崩/挂死"才允许提到 NVIDIA/驱动,且必须带上"不是本软件问题"这层意思;
/// ②"出图但坏帧"一律不得出现 NVIDIA/驱动字样。</summary>
public static class ProbeDiagnosis
{
    /// <summary>是否属于"引擎初始化阶段就没跑起来"(崩溃/挂死/进程起不来)。
    /// 只有这一类才可能与 Blackwell 的 cooperative-matrix 驱动缺陷对应。</summary>
    public static bool IsInitStageFailure(ProbeFailureKind kind)
        => kind is ProbeFailureKind.CrashExitCode or ProbeFailureKind.Hang or ProbeFailureKind.StartupFailed;

    /// <summary>失败形态的简名(日志/诊断包用,便于人工核查)。</summary>
    public static string ShortName(ProbeFailureKind kind) => kind switch
    {
        ProbeFailureKind.None => "无失败(正常)",
        ProbeFailureKind.EngineMissing => "引擎文件缺失",
        ProbeFailureKind.StartupFailed => "进程起不来",
        ProbeFailureKind.CrashExitCode => "初始化即崩(非零退出码)",
        ProbeFailureKind.NoOutput => "无产出文件",
        ProbeFailureKind.EmptyOutput => "产出空文件",
        ProbeFailureKind.DefectiveFrame => "出图但画面坏帧",
        ProbeFailureKind.Hang => "无响应(超时被强杀)",
        _ => "未知",
    };

    /// <summary>给用户看的一句话。blackwell = 本机是否存在 RTX 50 系(Blackwell)显卡。
    /// engineLabel 例:"Real-ESRGAN" / "waifu2x" / "RIFE"。
    /// 【措辞原则】①先说要紧的:已自动改用 ONNX,功能不受影响;②再说原因;③若是驱动缺陷,明确"不是本软件的问题"
    /// 且给出"等驱动修复"的出路(本软件每次都会重新实测,不需要用户改设置)。</summary>
    public static string Describe(ProbeFailureKind kind, bool blackwell, string engineLabel)
    {
        if (kind == ProbeFailureKind.EngineMissing)
            return $"未找到「{engineLabel}」引擎文件,已自动改用 ONNX 稳定路线。";

        if (kind == ProbeFailureKind.DefectiveFrame)
            // 【硬约束】坏帧是引擎并发/渲染这一类问题,不提驱动、不提 NVIDIA。
            // 【措辞】2026 起坏帧判据不止"近黑"了(还有通道失衡/亮度不对/被抹平),所以文案不能再只说近黑 ——
            // 说错形态会把排查方向带偏(连"是黑帧还是彩色噪点"都不是一回事)。明细在探测日志与诊断包里。
            return $"「{engineLabel}」引擎能出图但画面是坏的(黑帧/亮度或结构不对),已自动改用 ONNX 稳定路线。";

        if (IsInitStageFailure(kind))
        {
            string cause = blackwell
                ? "本机是 RTX 50 系(Blackwell):这是 NVIDIA 显卡驱动在 cooperative-matrix 查询上的已知缺陷,不是本软件的问题;"
                : "多为显卡驱动或引擎与该显卡不兼容;";
            string advice = blackwell
                ? "更新 NVIDIA 驱动后本软件会自动重新实测(无需改设置),期间用 ONNX 路线处理。"
                : "建议更新显卡驱动后重试。";
            return $"「{engineLabel}」引擎在初始化阶段就没能启动({ShortName(kind)})——{cause}已自动改用 ONNX 稳定路线,功能不受影响。{advice}";
        }

        return $"「{engineLabel}」引擎真机探测未通过({ShortName(kind)}),已自动改用 ONNX 稳定路线。";
    }
}
