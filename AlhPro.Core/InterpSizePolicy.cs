namespace AlhPro.Core;

/// <summary>补帧(RIFE)入参的尺寸/帧数预检(纯逻辑,可单测)。
/// 【任务 O2 · 2026-09-13 真机基准,两个静默坏片点】
/// ① **8K 输入静默全黑**:7680×4320 + `-n 119` → **117/119 帧全黑**,**exit=0 无报错**;
///    同一条命令在 1080p / 2160p 上 0 黑帧(所以不是"参数写错",而是尺寸相关)。
///    → ncnn-Vulkan RIFE 在 8K 级输入上会静默失败;3840×2160 是实测安全点,7680×4320 是实测故障点。
///      中间带(4K~8K)没有实测数据,所以本策略**只对实测故障带拒跑**,中间带只记一条警告:【待实测标定】。
/// ② **1 帧目录 + `-n 2` 必崩**(0xC0000005):RIFE 至少要两帧才能插值。
///    → 判定做成纯函数;并核对本仓调用点是否可达(见 VideoService.InterpSegmentAsync:
///      `segLen < 2` 直接复制不进引擎、且 `-n ≥ segLen + 1` 有下限,故**当前不可达**,护栏只是防御)。</summary>
public static class InterpSizePolicy
{
    /// <summary>实测故障带起点:7680×4320 = 33.18 Mpx(真机 117/119 全黑)。达到/超过就不要再交给 ncnn。</summary>
    public const long NcnnRefusePixels = 33_000_000;

    /// <summary>实测安全带上限:3840×2160 = 8.29 Mpx(真机 0 黑帧)。超过它但没到故障带 → 只警告。【待实测标定】</summary>
    public const long NcnnWarnPixels = 8_500_000;

    /// <summary>尺寸预检结论。<paramref name="RefuseNcnn"/> = 不要交给 ncnn-Vulkan(改走稳定引擎 ONNX);
    /// <paramref name="Warn"/> = 中间带,照跑但记日志。</summary>
    public readonly record struct SizeVerdict(bool RefuseNcnn, bool Warn, string Reason);

    /// <summary>按输入帧尺寸判定 ncnn 是否可用。</summary>
    public static SizeVerdict JudgeInputSize(int width, int height)
    {
        if (width <= 0 || height <= 0) return new SizeVerdict(false, false, "");
        long px = (long)width * height;
        if (px >= NcnnRefusePixels)
            return new SizeVerdict(true, true,
                $"{width}×{height}({px / 1_000_000.0:0.#} Mpx)属于实测故障带:ncnn 补帧在该尺寸下会静默输出全黑帧"
                + $"(真机 7680×4320 + -n 119 → 117/119 全黑,exit=0 无报错)");
        if (px > NcnnWarnPixels)
            return new SizeVerdict(false, true,
                $"{width}×{height}({px / 1_000_000.0:0.#} Mpx)超过实测安全带(3840×2160 = 8.3 Mpx,0 黑帧),但未到实测故障带"
                + "(7680×4320),按原路径继续【待实测标定】");
        return new SizeVerdict(false, false, "");
    }

    /// <summary>退化入参:ncnn RIFE 至少需要 2 帧输入,且目标帧数必须 &gt; 输入帧数 ——
    /// 实测"1 帧目录 + `-n 2`"必崩(0xC0000005,访问违例)。返回 true = 不许进引擎(该段应直接复制帧)。</summary>
    public static bool IsDegenerateSegmentInput(int segmentFrames, int targetFrames)
        => segmentFrames < 2 || targetFrames <= segmentFrames;

    /// <summary>【2026-09-16 · 用户裁决后的口径】**事前兼容性判定** —— 把已知会"静默出黑帧"的组合
    /// 在**开始处理之前**就换掉或拦下,而不是等出片之后再做事后黑帧检测。
    ///
    /// 【为什么要把防线从"事后"挪到"事前"】用户裁决:删掉黑帧判定(它收益远小于代价 —— 命中就把整段/整批
    /// 推到 ONNX 重算,实测一段 200 帧 1440p 的重算代价是 10~20 分钟,而 ncnn 跑完只要 53 秒;暗场素材还会误判)。
    /// 代价是:引擎若真静默出黑帧,成片会带着黑段交出去、且日志里查不到。⇒ 唯一的补救就是**别让那种组合跑起来**。
    ///
    /// 【已知会静默出黑帧的组合(真机实测,逐条标出处)】
    ///   ① 8K 级输入(≥33 Mpx):ncnn-Vulkan RIFE 静默全黑(7680×4320 + -n 119 → 117/119 全黑,exit=0)—— 见 JudgeInputSize。
    ///   ② RTX 50 系(Blackwell):2022 版 ncnn 内核在该架构上会崩/出坏帧 —— 见 EngineService.IsBlackwellGpu / ShouldUseOnnxEsrgan。
    ///   ③ 无独显 / Vulkan 不可用:同一类共性问题(仓库既有 OnnxFallbackRatherThanNcnn 口径)。
    ///   ④ 4K~8K 中间带:**没有实测数据**【待实测标定】,不拦、只提示。
    ///
    /// 返回:<paramref name="UseStableEngine"/> = 本次应直接走稳定引擎(ONNX),别交给 ncnn;
    ///       <paramref name="Warn"/> = 有已知但不确定的风险(照跑,提示用户);
    ///       <paramref name="Reason"/> = 给日志/界面用的人话说明。</summary>
    public readonly record struct EngineRiskVerdict(bool UseStableEngine, bool Warn, string Reason);

    /// <summary>按"尺寸 + 显卡"判定补帧引擎风险并给出对策。<paramref name="onnxModelAvailable"/> =
    /// 本机有没有 ONNX 补帧模型(rife49.onnx);没有时无法切稳定引擎,只能提示。</summary>
    public static EngineRiskVerdict JudgeEngineRisk(int width, int height, bool gpuIs50Series,
        bool gpuRiskyOldNcnn, bool onnxModelAvailable)
    {
        // ① 8K 级:实测静默全黑 —— 有 ONNX 就直接换,没有就明确提示"会出黑帧"
        if (width > 0 && height > 0)
        {
            long px = (long)width * height;
            if (px >= NcnnRefusePixels)
            {
                if (onnxModelAvailable)
                    return new EngineRiskVerdict(true, true,
                        $"{width}×{height}({px / 1_000_000.0:0.#} Mpx)属实测故障带(ncnn 补帧在该尺寸会静默输出全黑帧)"
                        + $"⇒ 本次直接改用稳定引擎(ONNX)补帧");
                return new EngineRiskVerdict(false, true,
                    $"⚠ {width}×{height}({px / 1_000_000.0:0.#} Mpx)属实测故障带:ncnn 补帧在该尺寸会**静默输出全黑帧**,"
                    + $"而本机没有 ONNX 补帧模型可切换 ⇒ 建议降低分辨率或改用较小素材");
            }
        }
        // ② RTX 50 系 / ③ 无独显或 Vulkan 不可用:同属"ncnn 会出坏帧"的已知组合
        if (gpuIs50Series || gpuRiskyOldNcnn)
        {
            string why = gpuIs50Series ? "当前显卡是 RTX 50 系(Blackwell)" : "当前设备无独立显卡 / Vulkan 不可用";
            if (onnxModelAvailable)
                return new EngineRiskVerdict(true, true, $"{why}:ncnn 补帧在已知组合下会出坏帧 ⇒ 本次直接改用稳定引擎(ONNX)补帧");
            return new EngineRiskVerdict(false, true, $"⚠ {why}:ncnn 补帧可能出坏帧,且本机没有 ONNX 补帧模型可切换");
        }
        // ④ 4K~8K 中间带:无实测数据,不换引擎、只提示
        if (width > 0 && height > 0 && (long)width * height > NcnnWarnPixels)
            return new EngineRiskVerdict(false, true,
                $"{width}×{height} 超过实测安全带(4K 0 黑帧)但未到实测故障带,按原路径继续【待实测标定】");
        return new EngineRiskVerdict(false, false, "");
    }
}
