namespace AlhPro.Core;

/// <summary>GPU 类别(用于安全渲染策略;不影响引擎调用,只是分块/批大小策略)。</summary>
public enum GpuCategory
{
    Nvidia,
    Blackwell,   // RTX 50 系:驱动/稳定性风险,保守
    Amd,         // 驱动差异,保守
    Other,
}

/// <summary>
/// 安全渲染的纯策略函数(从 SafeRender 抽出、可单测)。
/// 这些是"显存/GPU类别 → 分块大小/批大小"的映射——改错会导致爆显存/黑帧/卡死,
/// 是最需要测试保护的逻辑。硬件探测(IsBlackwellGpu/设备名/FreeRam/FreeVram)留在 SafeRender,
/// 这里只做纯计算。
/// </summary>
public static class RenderPolicy
{
    /// <summary>视频逐帧超分的分块大小,按"有效显存 + GPU 类别"决定。</summary>
    public static int VideoTileSize(double vramGB, GpuCategory cat)
    {
        // 显存是硬约束:小显存一律保守(爆显存→黑帧/崩溃比慢更糟)
        if (vramGB < 4) return 256;
        // 50系/AMD:驱动/稳定性风险,保守(主路径走 ONNX,此处只是 ncnn 兜底)
        if (cat is GpuCategory.Blackwell or GpuCategory.Amd) return vramGB >= 10 ? 640 : 512;
        // NVIDIA 常规(Turing 等):按显存取中间值,快且安全;大显存放大提速
        if (cat == GpuCategory.Nvidia)
        {
            if (vramGB >= 12) return 768;
            if (vramGB >= 8) return 640;
            if (vramGB >= 6) return 512;
            return 384;
        }
        // 未知/其他:沿用按显存的通用保守值
        if (vramGB <= 6) return 512;
        if (vramGB <= 10) return 640;
        return 768;
    }

    /// <summary>视频逐帧超分的批大小(帧):按"空闲内存 + 空闲显存"决定(空余不足小批=内存/显存峰值低、稳)。
    /// 注意:freeVram 由调用方决定兜底值(FreeVramGB>0.5 用实际值,否则用有效显存×0.6),这里只做档位判定。</summary>
    public static int VideoBatchSize(double freeRamGB, double freeVramGB)
    {
        if (freeRamGB > 8 && freeVramGB > 4) return 240;          // 空余内存>8G + 空余显存>4G:240(最快)
        if (freeRamGB <= 1.5 || freeVramGB <= 0.8) return 25;     // 极端紧张
        if (freeRamGB <= 2.5 || freeVramGB <= 1.5) return 40;
        if (freeRamGB <= 4 || freeVramGB <= 2.5) return 60;
        if (freeRamGB <= 6 || freeVramGB <= 3.5) return 120;
        return 180;                                               // 中档:空余内存 6~8G 或显存 3.5~4G
    }
}
