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

    // ===== 素材规模感知的批次决策(2026-09-13 新增)=====
    // 【为什么要加】VideoBatchSize(freeRamGB) 只按空闲内存定批大小,【完全不知道素材规模】:
    // 同一个 60 秒素材在低内存档上会被切成七八批,而每批都要重新启动一次引擎进程
    // (命令行引擎,启动 + 模型加载是秒级固定开销;本机日志:单批 72 帧"引擎启动→引擎完成"17.1s,
    //  而 240 帧/批那一轮每批同样只要 15.7~16.4s)。用户看到的就是"批次之间停顿好几秒,像卡死"。

    /// <summary>每批帧数的绝对上限 = 现有内存档的最大值(240 帧)。
    /// 【为什么钉在这里】批次越大,同屏临时帧越多(输入帧 + 本批输出帧并存,见 VideoService 的逐批清盘注释),
    /// 临时盘/内存峰值随每批帧数近似线性上涨。240 是今天"空闲内存 >8G"档【已经在用】的最大批 ——
    /// 短素材单批豁免永远不越过它,最坏峰值就等于今天高端机型已经在跑的配置,不引入新的爆盘/爆内存风险。
    /// 要再往上抬必须先有实测(同屏临时帧峰值、盘占用、内存水位),不许凭感觉放大。</summary>
    public const int MaxFramesPerBatch = 240;

    /// <summary>每批帧数下限:与 VideoService 原有"减半后不小于 8 帧"(Math.Max(8, batchSize / 2))一致,避免批次碎成每批几帧。</summary>
    public const int MinFramesPerBatch = 8;

    /// <summary>【短素材单批】唯一帧数不超过这个数 → 只跑一批,不再为几十帧反复启动引擎进程。
    /// 取值 = MaxFramesPerBatch(240),依据:
    ///  ① 不超过它时,单批的同屏临时帧数不会超过今天"空闲内存>8G → 240 帧/批"已在用的水平(见 MaxFramesPerBatch);
    ///  ② 一次引擎进程启动/模型加载是秒级固定开销(本机日志:单批 72 帧的引擎启动→完成 17.1s),
    ///     把 2~4 批合并成 1 批,省下的正是这个量级。
    /// 【待实测标定】阈值本身(240)以及"短素材单批确实更快"都还没有在真机空闲时验证过
    /// (验证要独占显卡跑作业,用户正在用机器);fastMode/diskTight 时这个上限按同样比例减半,见 PlanVideoBatches。</summary>
    public const int ShortClipSingleBatchUniqueFrames = MaxFramesPerBatch;

    /// <summary>批次决策结果:每批帧数上限、批数、以及"为什么是这个数"(供日志/事后验收)。</summary>
    public readonly record struct VideoBatchPlan(
        int BatchSize, int BatchCount, int UniqueFrames, int MemoryBatchSize,
        bool SingleBatchByShortClip, bool HalvedForFastMode, bool HalvedForDiskTight, string Reason);

    /// <summary>把【素材规模】纳入批次决策的纯函数(可单测)。
    /// 【规则】
    ///  ① 内存基准 = VideoBatchSize(freeRamGB) —— 既有安全上界,一字不改;
    ///  ② fastMode / diskTight:各自把基准减半(下限 8 帧),次序与 VideoService 原实现一致(fast 先、diskTight 后);
    ///  ③ 短素材单批:唯一帧数 ≤ 单批上限(默认 240,减半时 120 / 60)→ 批数 = 1;
    ///  ④ 长素材:批数 = ⌈唯一帧数 ÷ 每批帧数⌉,每批帧数【永不】超过内存基准(因此不设"批数上限")。
    /// 【为什么不设批数上限 K —— 这是与"峰值盘/内存不能暴涨"的硬冲突,已上报未擅自实现】
    ///  批数 = ⌈N ÷ 每批帧数⌉,要让批数 ≤ K 只能让每批帧数随 N 线性变大:几万帧的长素材上 K=8
    ///  会要求每批几千帧,同屏临时帧(输入帧 + 本批输出帧)按同样倍数上涨,峰值盘/内存随之暴涨 ——
    ///  恰好违反本函数第 ④ 条要守的安全上界。真正能"摊薄启动开销又不涨峰值"的办法是让引擎进程
    ///  【跨批复用】(长驻引擎 / 引擎批输入内含多批),那是管线改造,不在"批次决策"这一层内。</summary>
    public static VideoBatchPlan PlanVideoBatches(double freeRamGB, int uniqueFrames, bool fastMode = false, bool diskTight = false)
    {
        if (uniqueFrames < 0) uniqueFrames = 0;
        int memBatch = VideoBatchSize(freeRamGB);            // ① 内存基准
        int batch = memBatch;
        bool halvedFast = false, halvedDisk = false;
        if (fastMode) { batch = Math.Max(MinFramesPerBatch, batch / 2); halvedFast = true; }    // ② 兼容模式(弱设备)
        if (diskTight) { batch = Math.Max(MinFramesPerBatch, batch / 2); halvedDisk = true; }   // ② 临时盘偏紧(防爆盘)
        // ③ 短素材单批上限:与"减半"同比例收紧(减半必须仍然生效),且永不超过 ShortClipSingleBatchUniqueFrames
        int singleCap = ShortClipSingleBatchUniqueFrames;
        if (halvedFast) singleCap = Math.Max(MinFramesPerBatch, singleCap / 2);
        if (halvedDisk) singleCap = Math.Max(MinFramesPerBatch, singleCap / 2);
        bool single = uniqueFrames > 0 && uniqueFrames <= singleCap;
        if (single) batch = Math.Max(batch, Math.Min(uniqueFrames, singleCap));
        int count = uniqueFrames <= 0 ? 1 : (uniqueFrames + batch - 1) / batch;   // ④ 长素材:按内存基准切,不设批数上限
        string halveTxt = (halvedFast, halvedDisk) switch
        {
            (true, true) => "兼容模式+临时盘紧",
            (true, false) => "兼容模式",
            (false, true) => "临时盘紧",
            _ => "",
        };
        string reason = $"内存基准 {memBatch} 帧/批"
            + (halveTxt.Length > 0 ? $"(减半:{halveTxt} → {batch} 帧/批)" : "")
            + (single
                ? $";唯一帧 {uniqueFrames} ≤ 单批上限 {singleCap} → 1 批(短素材不为几十帧重复启动引擎)"
                : $";唯一帧 {uniqueFrames} 按内存基准切 {count} 批(每批 ≤ {batch} 帧;不设批数上限,理由见 PlanVideoBatches)");
        return new VideoBatchPlan(batch, count, uniqueFrames, memBatch, single, halvedFast, halvedDisk, reason);
    }
}
