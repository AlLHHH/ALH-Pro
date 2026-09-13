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
}
