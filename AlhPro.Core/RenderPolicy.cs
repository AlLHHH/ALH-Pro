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

    /// <summary>ONNX(DirectML)超分的分块大小 —— 以【本机实测】为锚点,只按显存定,不按显卡型号定。
    /// 【实测锚点】(RTX 4060 Laptop 8GB / waifu2x-cunet2x / 2x / DirectML,每百万像素耗时):
    ///   256→337、512→385、768→360、【1024→327(最优)】ms/Mpix;1280→崩塌 13.7~20 倍(显存溢出);1536/1920→OOM。
    /// 【为什么上限就钉在 1024】①效率曲线从 256 到 1024 基本平坦,1024 略优(块大→块数少→重叠浪费少);
    /// ②再往上只多几个百分点,而一旦超过显存会【静默慢十余倍】(不是崩溃,用户只看到"卡"),不值得冒险;
    /// ③小显存按 √(显存/8) 收缩(显存需求随像素近似线性)。
    /// 【为什么不按显卡型号】旧策略把 50 系/AMD 一律压到 512,而实测 8GB 上 1024 既安全又快约 15%。
    /// integrated=true(核显/共享显存)时一律 512:核显报的"显存总量"是共享内存,不是真实可用量。
    /// 【两个显存口径取小】totalVramGB = 物理总显存(决定上限),effectiveVramGB = 显存墙
    /// (自动模式=总显存×0.75,用户可自定义)。自动模式下 8GB 卡的墙是 6.0 —— 若只按墙取值会白白退回 768,
    /// 而实测 1024 块的峰值(≈6.9GB)本就在墙之上却没溢出;但用户主动把墙收紧时必须听用户的。
    /// 【要放大必须先有数据】将来若在大显存卡(16GB+)上实测确认 1280/1536 安全且更快,再抬高这个上限。</summary>
    public static int OnnxTileSize(double totalVramGB, double effectiveVramGB, bool integrated = false)
    {
        if (integrated) return 512;
        // 【档位要带容差 —— 这里踩过坑】显存是按 MiB 报的,所谓"8GB 卡"实际是 8188MiB=7.996GB,
        // 用 `>= 8` 判定会差 0.004GB 掉到下一档(真机验证:日志打出 ONNX 768 而不是 1024)。
        // 故档位边界一律下压半档(7.5/5.5/3.5),按"显存档"而不是精确数值来分。
        // 按【物理总显存】算上限:实测锚点就是"8GB 档的卡 → 1024 最优且未溢出"。
        int byTotal = totalVramGB >= 7.5 ? 1024 : totalVramGB >= 5.5 ? 768 : totalVramGB >= 3.5 ? 640 : 512;
        // 同时尊重【显存墙】(自动模式 = 总显存 ×0.75,用户也可自定义收紧):用户主动限制显存时听用户的。
        int byWall = effectiveVramGB >= 5.5 ? 1024 : effectiveVramGB >= 4.0 ? 768 : effectiveVramGB >= 2.5 ? 640 : 512;
        return Math.Min(byTotal, byWall);
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
