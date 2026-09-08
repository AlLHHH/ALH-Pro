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

    /// <summary>视频逐帧超分的批大小(帧):只按【空闲内存】决定(空余不足→小批=内存峰值低、稳)。
    /// 不再看空闲显存:批大小决定的是每批缓冲在磁盘/内存里的帧数,显存峰值由 VideoTileSize(分块)界定,
    /// 与批大小无关;而空闲显存在 AMD/Intel 上无法真测,拿估算值砍批次只会白白损失吞吐。
    /// 空闲内存由 GlobalMemoryStatusEx 实测,跨厂商可靠。</summary>
    public static int VideoBatchSize(double freeRamGB)
    {
        if (freeRamGB <= 1.5) return 25;     // 极端紧张
        if (freeRamGB <= 2.5) return 40;
        if (freeRamGB <= 4) return 60;
        if (freeRamGB <= 6) return 120;
        if (freeRamGB <= 8) return 180;      // 中档
        return 240;                          // 空余内存 >8G:最快
    }
}
