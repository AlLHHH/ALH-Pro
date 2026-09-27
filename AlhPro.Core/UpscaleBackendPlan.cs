namespace AlhPro.Core;

/// <summary>「超分这一批走 ncnn-vulkan 还是走 ONNX(稳定引擎)」的**唯一判定点**(纯逻辑,可单测)。
///
/// 【为什么把它抽出来】这段判定原来是 `VideoService.UpscaleDirAsync` 批循环里的一串 `if/else if`
/// (约 `VideoService.cs` 2792~2805 行)。它决定了"这一批用哪个引擎跑",却**没有任何单测能覆盖**
/// (测试项目只引用 `AlhPro.Core`,碰不到 UI 层),于是每次改超分路径都在赌博。抽成纯函数后:
/// ① 真值表可逐格断言;② 批循环只剩一次调用;③ 将来加引擎(如 Real-CUGAN 的 ONNX 路径)只需改这一处。
///
/// ==== 原实现的真值表(逐字搬过来,不许"顺手优化") ====
///   · `gpuId &lt; 0`(用户手动选 CPU,或 GPU 探测失败):
///       realesrgan / waifu2x → **走 ONNX**(ncnn 的 CPU 模式在部分机器崩,实测 exit -1 / -1073741819);
///       realcugan → 不走(ncnn CPU 实测也崩,但**没有 ONNX 替代**,只能返回 false 让上层去撞);
///   · `gpuId ≥ 0` 且有 GPU:realesrgan → `onnxPreferredEsrgan || ncnnUnreliable || fastMode`;
///     waifu2x → `onnxPreferredWaifu2x || ncnnUnreliable || fastMode`;realcugan → **恒 false**。
/// 【调用方要传什么】`onnxPreferredWaifu2x` 把原来的 `EngineService.ShouldUseOnnxWaifu2x() || waifuOnnx`
/// 两项**一起**传进来 —— 注意保持原短路顺序:`ShouldUseOnnxWaifu2x()` 有副作用(探测/缓存),
/// 不能因为重构就把它挪到后面或者改成先算。</summary>
public static class UpscaleBackendPlan
{
    /// <summary>这一批是否走 ONNX(稳定引擎)。参数与原名一一对应:
    /// <paramref name="engine"/> = "realesrgan" / "waifu2x" / "realcugan"(其它值一律 false);
    /// <paramref name="gpuId"/> = 超分用的 GPU 序号(&lt;0 = CPU/未探测到);
    /// <paramref name="onnxPreferredEsrgan"/> = `EngineService.ShouldUseOnnxEsrgan()`;
    /// <paramref name="onnxPreferredWaifu2x"/> = `EngineService.ShouldUseOnnxWaifu2x() || waifuOnnx`;
    /// <paramref name="ncnnUnreliable"/> = 本机 ncnn 被判为不可靠(1x 缩回等场景);
    /// <paramref name="fastMode"/> = 快模式(ONNX 在中低端 GPU 上更快)。</summary>
    public static bool UseOnnx(string? engine, int gpuId, bool onnxPreferredEsrgan, bool onnxPreferredWaifu2x,
        bool ncnnUnreliable, bool fastMode)
    {
        if (gpuId < 0)
            // 手动选 CPU:waifu2x/realesrgan 的 ncnn CPU 模式在部分机器崩 → 直接 ONNX。
            // Real-CUGAN 没有 ONNX 路径,这里返回 false(与重构前逐字一致)。
            return engine == "realesrgan" || engine == "waifu2x";
        if (engine == "realesrgan") return onnxPreferredEsrgan || ncnnUnreliable || fastMode;
        if (engine == "waifu2x") return onnxPreferredWaifu2x || ncnnUnreliable || fastMode;
        return false;
    }

    // ═══════════════ 【2026-09-25 修订 · F1】"后端身份"必须是一个可落盘的键 ═══════════════
    // 本机标定测出来的秒/帧**只在它被测的那条后端上成立**:ncnn-Vulkan 与 ONNX 是两套完全不同的运行时
    // (实测 ncnn 0.24~0.6 秒/帧,而 ONNX 落 CPU 是 8 秒/帧 —— 差一个数量级)。
    // 拿在 ncnn 上测的单帧耗时去判定"本次其实走 ONNX"的运行 ⇒ u 偏小 ⇒ 判定偏向「补帧→超分」,
    // 正是 2026-09-25 那次 39 分钟误判的同一类错。
    // ⇒ 单帧耗时记录必须**带着后端**落盘,查找也必须按后端匹配(见 LocalPriceBook.Resolve / TryBuild)。

    /// <summary>后端 = ncnn-Vulkan(本机 GPU)。</summary>
    public const string NcnnVulkan = "ncnn-vulkan";

    /// <summary>后端 = ONNX 稳定引擎,device = DirectML GPU(`-2`)。</summary>
    public const string OnnxDml = "onnx-dml";

    /// <summary>后端 = ONNX 稳定引擎,device = 强制 CPU(`-1`)。</summary>
    public const string OnnxCpu = "onnx-cpu";

    /// <summary>后端未确认(空白/认不出)。**这个值永远不许落盘**(见 <see cref="LocalPriceBook.TryBuild"/>)。</summary>
    public const string Unknown = "";

    /// <summary>后端名归一:`null`/空白/认不出 ⇒ <see cref="Unknown"/>;大小写与空格都容忍。
    /// 判据与落盘都走这一个函数,免得两处各写一套比较。</summary>
    public static string NormalizeBackend(string? backend)
    {
        string b = (backend ?? "").Trim().ToLowerInvariant();
        return b switch
        {
            NcnnVulkan => NcnnVulkan,
            OnnxDml => OnnxDml,
            OnnxCpu => OnnxCpu,
            "ncnn" => NcnnVulkan,          // 手写/日志里的简写
            "onnx" => OnnxDml,
            _ => Unknown,
        };
    }

    /// <summary>后端的中文标签(只给日志/界面用,不参与比较)。</summary>
    public static string Label(string? backend) => NormalizeBackend(backend) switch
    {
        NcnnVulkan => "ncnn-Vulkan(本机 GPU)",
        OnnxDml => "ONNX 稳定引擎(DirectML GPU,device -2)",
        OnnxCpu => "ONNX 稳定引擎(CPU,device -1)",
        _ => "后端未确认",
    };

    /// <summary>ONNX 用哪个设备号 —— **批次循环与标定必须走同一个公式**(契约要求的"两处调用同一函数"):
    /// 用户主动选 CPU(`gpuId &lt; 0`)且没开 DirectML ⇒ `-1`(强制 CPU);探测失败改口(`onnxDml`)或正常 GPU ⇒ `-2`
    /// (DirectML 自动选设备)。原实现在批次循环里写的是 `upGpu &lt; 0 ? (upOnnxDml ? -2 : -1) : -2`,逐字等价。</summary>
    public static int OnnxDevice(int gpuId, bool onnxDml) => gpuId < 0 ? (onnxDml ? -2 : -1) : -2;

    /// <summary>**本次真正会走的后端**(落盘键/日志用)。所有入参都必须取**定稿后**的值:
    /// <paramref name="gpuId"/> 传定稿的 `upGpu`、<paramref name="onnxDml"/> 传 `upOnnxDml`、
    /// <paramref name="onnxPreferredWaifu2x"/> 传 `ShouldUseOnnxWaifu2x() || waifuOnnx`(与批次循环同一形状)。
    /// realcugan 没有 ONNX 通道 ⇒ 只要 GPU 可用就是 <see cref="NcnnVulkan"/>(它的 CPU 档会在更早处直接拒绝)。</summary>
    public static string DescribeBackend(string? engine, int gpuId, bool onnxPreferredEsrgan, bool onnxPreferredWaifu2x,
        bool ncnnUnreliable, bool fastMode, bool onnxDml)
    {
        if (!UseOnnx(engine, gpuId, onnxPreferredEsrgan, onnxPreferredWaifu2x, ncnnUnreliable, fastMode))
            return gpuId >= 0 ? NcnnVulkan : Unknown;   // 没 GPU 又没 ONNX 通道(realcugan)= 后端未确认,不许落盘
        return OnnxDevice(gpuId, onnxDml) == -1 ? OnnxCpu : OnnxDml;
    }
}
