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
    /// 空闲内存由 GlobalMemoryStatusEx 实测,跨厂商可靠。
    /// 【任务 T · 2026-09-13 口径变更】中/高档数字整体上调(120/180/240 → 300/300/700):
    /// 这一列现在只用于"内存压力"的展示与兜底(UI 日志 `SafeRender.GetVideoBatchSize()`),
    /// 真正决定每批帧数的是 <see cref="PlanVideoBatches"/> 的"内存档 × 性能档"(见那里)。</summary>
    public static int VideoBatchSize(double freeRamGB)
    {
        if (freeRamGB <= 1.5) return 25;     // 极端紧张
        if (freeRamGB <= 2.5) return 40;
        if (freeRamGB <= 4) return 60;
        if (freeRamGB <= 6) return NormalDeviceFramesPerBatch;      // 【T】120 → 300
        if (freeRamGB <= 8) return NormalDeviceFramesPerBatch;      // 【T】180 → 300
        return StrongDeviceLargeFramesPerBatch;                     // 【T】240 → 700(空余内存 >8G:最快档)
    }

    // ===== 批次口径(2026-09-13 按用户口径重定;旧的 240/8 口径已作废)=====
    // 【用户给的口径(逐字)】「要的是动态调整批 给几个批次 处理前按当前设备来看 如果设备正常并且视频短
    //  补帧完的帧总数少 完全可以不分批 但是设备好 视频长 考虑批内扩大 一次200帧 400帧这样子
    //  设备差就最低50一批」
    // 【决策时机】处理前一次算定(不在跑的过程中改批),依据 = 设备档位 + 源帧数 + 补帧后总帧数。
    // 【为什么"设备档位"只看空闲内存、不新造探测】空闲内存由 GlobalMemoryStatusEx 实测、跨厂商可靠,
    //  是仓库里既有的硬指标;显存只有 NVIDIA 能真测(见 VideoBatchSize 注释),做不了通用的档位判据。

    /// <summary>设备档位(只由空闲内存分)。</summary>
    public enum DeviceTier { Weak, Normal, Strong }

    /// <summary>「设备好」门槛:空闲内存 ≥ 8GB。【依据】现有 VideoBatchSize 表在 &gt;8G 才给到它的最大档 240,
    /// 即仓库里既有的"最高档机器"边界 —— 取同一根线,避免出现两套互相矛盾的分档。【待实测标定】</summary>
    public const double StrongDeviceFreeRamGB = 8.0;

    /// <summary>「设备正常」门槛:空闲内存 ≥ 4GB(低于它算设备差)。【依据】现有 VideoBatchSize 表在 ≤4G 只给
    /// 60 帧/批(低档),与用户"设备差最低 50 一批"同一量级;4G 就是既有表里"低档/中档"的分界。【待实测标定】</summary>
    public const double NormalDeviceFreeRamGB = 4.0;

    /// <summary>设备差的每批帧数(用户给定:最低 50 一批)。这是【全档位下界】:任何档位、任何模式
    /// (含 fastMode/diskTight 减半)算出来的每批帧数都不许低于它。</summary>
    public const int WeakDeviceFramesPerBatch = 50;

    /// <summary>设备正常(内存档或性能档判定为"正常"):每批 300 帧。【任务 T · 2026-09-13 口径变更】
    /// 旧口径是"沿用内存档 120/180"。【依据】用户在任务 T 里的口径「Normal 120/180 → 300」;
    /// 300 仍低于"设备好+长片"的 700,峰值(输入帧+本批输出帧并存)只有它的 3/7。【待实测标定】</summary>
    public const int NormalDeviceFramesPerBatch = 300;

    /// <summary>设备好 + 视频不长:每批 350 帧。【任务 T · 2026-09-13 口径变更】旧口径 200。
    /// 【依据】用户口径「Strong 短片 200 → 350」。【待实测标定】</summary>
    public const int StrongDeviceFramesPerBatch = 350;

    /// <summary>设备好 + 视频长("批内扩大"):每批 700 帧。【任务 T · 2026-09-13 口径变更】旧口径 400(×1.75)。
    /// 【硬条件】只有"性能档 = Fast 且空闲内存 ≥ <see cref="StrongDeviceFreeRamGB"/> 才给" ——
    /// 见 <see cref="TierBaseFrames"/>;上界抬高同时必须抬高**峰值守门**(见 TempSpaceEstimate.NeedBytes
    /// 的"每批并存帧"项),不允许只抬上限不给守门。【待实测标定】</summary>
    public const int StrongDeviceLargeFramesPerBatch = 700;

    /// <summary>「视频长」门槛之一(按时长):源帧数 ≥ 900(≈30 秒 @30fps)。【依据】用户点名的"短素材"是
    /// 72 帧/3 秒;900 帧(30 秒)是"明显属于长片"的下限。【待实测标定】</summary>
    public const int LongClipMinSourceFrames = 900;

    /// <summary>「视频长」门槛之二(按体量):补帧后总帧数 ≥ 1200。【依据】① 用户点名的"长素材"那条
    /// (他明确说的是"补帧完的帧总数")补帧后是 3420 帧,命中;点名的短素材补帧后只有 288 帧,不命中;
    /// ② 与批数挂钩:1200 帧按 200/批 是 6 批,扩到 400/批 变 3 批 —— 省下 3 次引擎进程启动(每次秒级,
    /// 见 VideoPipeline.AssumedEngineStartupSecondsPerBatch),这正是"批内扩大"的收益来源。【待实测标定】</summary>
    public const int LongClipMinPostInterpFrames = 1200;

    /// <summary>【不分批】上限:补帧后总帧数 ≤ 400 且设备档位 ≥ 正常 → 整条素材一批跑完。
    /// 【依据】400 就是用户给的【最大批】(设备好+视频长的档):整片都不超过这个数时,不分批的同屏临时帧
    /// 不会超过用户已经认可的最大批,峰值不越界;省下的是每批一次的引擎进程启动(秒级/次,见
    /// VideoPipeline.AssumedEngineStartupSecondsPerBatch)。【待实测标定】
    /// 注:因为"补帧后总帧数 ≥ 源帧数",本条件已隐含"视频短";源帧数条件保留只为把用户口径写全。</summary>
    public const int SingleBatchMaxPostInterpFrames = 400;

    /// <summary>设备档位判定(纯函数):<see cref="NormalDeviceFreeRamGB"/> 以下=差,<see cref="StrongDeviceFreeRamGB"/> 及以上=好,中间=正常。</summary>
    public static DeviceTier TierFor(double freeRamGB)
        => freeRamGB >= StrongDeviceFreeRamGB ? DeviceTier.Strong
         : freeRamGB >= NormalDeviceFreeRamGB ? DeviceTier.Normal
         : DeviceTier.Weak;

    /// <summary>【任务 T】把"性能档"(实测吞吐等)折成与内存档同序的档位,便于取两者**较低**者。</summary>
    public static DeviceTier TierForPerf(PerfScore score)
        => score switch
        {
            PerfScore.Fast => DeviceTier.Strong,
            PerfScore.Normal => DeviceTier.Normal,
            _ => DeviceTier.Weak,
        };

    /// <summary>【任务 T】档位基准帧数(优先级链的第 ① 步):内存档与性能档**取较低者**,再按"视频长不长"取大档。
    /// 【为什么取较低者】内存不够却"测得快"时,大批会把同屏临时帧顶爆(峰值随批线性涨);
    /// 反过来内存够大但"测得慢"(例如 ONNX 落 CPU 8 秒/帧)时,大批只会让每批跑得更久、峰值占盘更久。
    /// 【700 的硬条件】只有 性能档=Fast **且** 空闲内存 ≥ <see cref="StrongDeviceFreeRamGB"/> 才可能取到 700:
    /// 内存档取较低者已经蕴含这一条(Strong 内存档 = ≥8G),这里再显式判一次,免得日后有人改坏。</summary>
    public static int TierBaseFrames(DeviceTier memoryTier, PerfScore perf, double freeRamGB, bool longClip)
    {
        var perfTier = TierForPerf(perf);
        var effective = (DeviceTier)Math.Min((int)memoryTier, (int)perfTier);
        if (effective == DeviceTier.Strong)
            return longClip && perf == PerfScore.Fast && freeRamGB >= StrongDeviceFreeRamGB
                ? StrongDeviceLargeFramesPerBatch   // 700
                : StrongDeviceFramesPerBatch;       // 350
        if (effective == DeviceTier.Normal) return NormalDeviceFramesPerBatch;   // 300
        return WeakDeviceFramesPerBatch;                                         // 50(全档位下界)
    }

    // ===== 面积缩放(任务 Q2 · 2026-09-13)=5====
    /// <summary>面积基准:1080p = 1920×1080 = 2 073 600 px(2.07 Mpx)。批大小按"输入像素"反比缩放时的基准面积。
    /// (与 PipelineOrderPlan 的成本表基准同一个数 —— 那里直接引用本常量,避免两处各写一份。)</summary>
    public const double ReferencePixels1080p = 1920.0 * 1080.0;

    /// <summary>输入分辨率 → 每批帧数的面积系数(1.0 = 1080p;&gt;1 = 比 1080p 小,同批像素量下可以多放帧;
    /// &lt;1 = 每帧更大,必须少放帧)。
    /// 【为什么需要它 —— 任务 Q2】批次大小原先只按"设备档位 × 帧数"定,**没有按分辨率缩放**:
    /// 4K 源每帧像素是 1080p 的 4 倍,同样的"每批 200 帧"意味着同屏临时盘/内存也放大 ~4 倍 —— 峰值随分辨率暴涨。
    /// 【取整与钳位】非法分辨率返回 1.0(不缩放);系数钳到 [1/64, 64] 防病态输入。</summary>
    public static double AreaFactor(int width, int height)
    {
        if (width <= 0 || height <= 0) return 1.0;
        return Math.Clamp(ReferencePixels1080p / ((double)width * height), 1.0 / 64, 64.0);
    }

    /// <summary>把"档位基准帧数"按输入面积缩放,并钳到 [50, 档位基准]。
    /// 【上界为什么是"档位基准"】50/120/180/200/400 本身就是任务 E 定的每批上限;不越过它,
    /// 才能在"更小分辨率"上仍不越任务 E 的口径(也不会把 fastMode/diskTight 已减半的结果再抬回去)。
    /// 【待实测标定】反比缩放是面积口径的近似:没有实测的"每批帧数 vs 分辨率"峰值曲线。</summary>
    public static int ScaleFramesForArea(int tierFrames, int width, int height)
    {
        if (width <= 0 || height <= 0 || tierFrames <= 0) return tierFrames;
        int scaled = (int)Math.Round(tierFrames * AreaFactor(width, height));
        return Math.Clamp(scaled, WeakDeviceFramesPerBatch, tierFrames);
    }

    /// <summary>【任务 R3 · 2026-09-13 用户要求"补帧分批要比超分大"】按**"输入帧 + 本批输出帧并存"的像素量**
    /// (峰值口径)缩放每批帧数:系数 = 1080p 面积 ÷ ((输入面积 + 输出面积) / 2),
    /// 仍钳到 [50, 档位基准]。没给输出尺寸时退回"只看输入面积"的旧口径(与 2 参重载等价)。
    /// 【为什么必须看输出面积】超分阶段输入是源分辨率帧、**输出是放大 scale² 倍的帧**,两者同时存在
    /// (峰值临时盘/内存就是这两者的和)。只看输入会让超分阶段的批偏大 —— 4K/2x 下峰值可达 1080p 的 2.5 倍。
    /// 【方向核对(用户要的方向)】旧顺序下:补帧阶段输入=输出=源面积 → 系数 1.0(批大);
    /// 超分阶段 (源 + 源×scale²)/2 → 系数 1/((1+scale²)/2)(批小)⇒ **补帧批 > 超分批** ✓;
    /// 新顺序下:超分阶段 (源 + 源×scale²)/2(批小)、补帧阶段输入=输出=放大后面积 → 系数 1/scale² ⇒
    /// **超分批 > 补帧批**(方向相反,同样是"按各阶段自己的像素量算"的自然结果)。
    /// 【待实测标定】面积口径是近似(引擎可能按 2 的幂跑再缩回;磁盘/内存的并存比例也未实测)。</summary>
    public static int ScaleFramesForArea(int tierFrames, int inW, int inH, int outW, int outH)
    {
        if (inW <= 0 || inH <= 0 || tierFrames <= 0) return tierFrames;
        if (outW <= 0 || outH <= 0) return ScaleFramesForArea(tierFrames, inW, inH);
        double avgPixels = (((double)inW * inH) + ((double)outW * outH)) / 2.0;
        double factor = Math.Clamp(ReferencePixels1080p / avgPixels, 1.0 / 64, 64.0);
        int scaled = (int)Math.Round(tierFrames * factor);
        return Math.Clamp(scaled, WeakDeviceFramesPerBatch, tierFrames);
    }

    /// <summary>批次决策结果:每批帧数、批数、以及"为什么是这个数"(供日志/事后验收)。</summary>
    public readonly record struct VideoBatchPlan(
        int BatchSize, int BatchCount, int SourceFrames, int PostInterpFrames, int TierBaseFrames,
        DeviceTier Tier, bool SingleBatch, bool HalvedForFastMode, bool HalvedForDiskTight, string Rule);

    /// <summary>按用户口径决定"每批帧数 / 批数"(纯函数,可单测)。
    /// 【输入】freeRamGB=空闲内存(→设备档位);sourceFrames=【去重后】源帧数(视频长度口径,决定"长/短");
    ///   postInterpFrames=【补帧后总帧数】(用户点名要算的量;"补帧→超分"顺序下它就等于超分阶段的输入帧数)。
    /// 【规则(自上而下,命中即止)】
    ///   ① 设备档位 = TierFor(空闲内存):&lt;4G 差 / 4~8G 正常 / ≥8G 好;
    ///   ② 设备 ≥ 正常 且 补帧后总帧数 ≤ SingleBatchMaxPostInterpFrames(400)→ 完全不分批(batchCount=1);
    ///   ③ 【任务 T 口径变更】档位基准 = (内存档 × 性能档)取较低者 → 长片 700 / 短片 350 / 正常 300 / 差 50
    ///      (见 <see cref="TierBaseFrames"/>);"性能档"来自 <see cref="DevicePerf"/>:优先用实测吞吐,
    ///      没有实测数据时回退纯内存档。700 还要求"性能档 = Fast **且** 空闲内存 ≥ 8G"。
    ///   ④ fastMode / diskTight 各自把每批帧数减半(既有的防爆盘/弱机保护),再【钳到 ≥ 50】。
    ///      ⚠ 冲突点(已如实报告、未自行决定别的折中):在"设备差(基准 50)"档上,减半(→25)会被 50 下界
    ///      挡住 = 该档减半不生效;其余档位(700→350、350→175、300→150)减半照常生效。
    ///   批数 = ⌈补帧后总帧数 ÷ 每批帧数⌉(不分批时 = 1;这是【预计值】,真正切批按去重后的唯一帧组数,
    ///   由调用方按实际结果再记一行日志)。
    /// 【不设批数上限】仍成立:限批数只能让每批帧数随素材线性变大,同屏临时帧(输入+输出并存)跟着涨 ——
    ///  与"峰值不暴涨"直接冲突(旧注释里的论证保持不变)。
    /// 【任务 T 要求 4:抬上限必须同时抬峰值守门】批上限 400 → 700(×1.75)后,"输入帧 + 本批输出帧并存"
    ///  的同屏量同倍数上涨 —— 所以调用方的临时空间预估必须把**每批帧数**算进去
    ///  (见 TempSpaceEstimate.NeedBytes 的 batchFrames 项与 VideoService 的守门复算),不允许只抬上限。</summary>
    public static VideoBatchPlan PlanVideoBatches(double freeRamGB, int sourceFrames, int postInterpFrames,
        bool fastMode = false, bool diskTight = false, int srcW = 0, int srcH = 0, int outW = 0, int outH = 0,
        PerfScore? perf = null)
    {
        if (sourceFrames < 0) sourceFrames = 0;
        if (postInterpFrames < sourceFrames) postInterpFrames = sourceFrames;   // 补帧后帧数 ≥ 源帧数(倍率 ≥1)
        var tier = TierFor(freeRamGB);
        // 【任务 T】没给性能档 → 回退纯内存档(任务 T 要求 1);给了就用它,并与内存档取较低者。
        var score = perf ?? DevicePerf.FromRam(freeRamGB);
        // 「视频长」= 时长长(源帧数)或补帧后体量大(总帧数):两者任一命中就按"批内扩大"处理
        bool longClip = sourceFrames >= LongClipMinSourceFrames || postInterpFrames >= LongClipMinPostInterpFrames;
        // ① 每批帧数基准 = 档位基准(内存档 × 性能档,取较低者)
        int baseFrames = TierBaseFrames(tier, score, freeRamGB, longClip);
        string rule;
        {
            var effTier = (DeviceTier)Math.Min((int)tier, (int)TierForPerf(score));
            string effTxt = effTier switch
            {
                DeviceTier.Strong => "好",
                DeviceTier.Normal => "正常",
                _ => "差",
            };
            rule = $"档位=内存{(tier == DeviceTier.Strong ? "好" : tier == DeviceTier.Normal ? "正常" : "差")}"
                + $"(空闲内存 {freeRamGB:0.#}G)×性能{score}({(perf.HasValue ? "实测/外部给定" : "无实测→回退内存档")})"
                + $" → 取较低者={effTxt} → "
                + (longClip
                    ? $"视频长(源 {sourceFrames} 帧 ≥ {LongClipMinSourceFrames} 或补帧后 {postInterpFrames} 帧 ≥ {LongClipMinPostInterpFrames})→ 批内扩大到 {baseFrames} 帧/批"
                    : $"视频不长(源 {sourceFrames} 帧 < {LongClipMinSourceFrames} 且补帧后 {postInterpFrames} 帧 < {LongClipMinPostInterpFrames})→ {baseFrames} 帧/批")
                + (baseFrames == StrongDeviceLargeFramesPerBatch
                    ? $"(700 的硬条件:性能档 Fast 且空闲内存 ≥ {StrongDeviceFreeRamGB:0.#}G,当前 {freeRamGB:0.#}G 满足)"
                    : "");
        }
        int batch = baseFrames;
        // ⑦ 面积缩放(任务 Q2):输入分辨率越大 → 每批帧数越少,让"输入帧 + 本批输出帧并存"的峰值不随分辨率暴涨。
        // 【优先级(自上而下,写死在这里,别再各写一套)】
        //   ① 设备档位 → 基准帧数;② 面积缩放(钳 [50, 档位基准]);③ fastMode/diskTight 各减半(钳 ≥50);
        //   ④ 短素材单批(设备 ≥ 正常 且 补帧后 ≤400)→ 覆盖前面全部(整片一批,批大小无意义)。
        int areaFrames = ScaleFramesForArea(baseFrames, srcW, srcH, outW, outH);
        if (areaFrames != baseFrames)
        {
            double avgPixels = srcW > 0 && srcH > 0 && outW > 0 && outH > 0
                ? (((double)srcW * srcH) + ((double)outW * outH)) / 2.0
                : (srcW > 0 && srcH > 0 ? (double)srcW * srcH : 0);
            rule += $";输入 {srcW}×{srcH}{((outW > 0 && outH > 0) ? $"→输出 {outW}×{outH}(峰值像素 {(long)avgPixels / 1_000_000.0:0.##} Mpx)" : "")}"
                + $"(峰值面积系数 {ReferencePixels1080p / Math.Max(1.0, avgPixels):0.###}×)→ 每批 {areaFrames} 帧";
        }
        batch = areaFrames;
        bool halvedFast = false, halvedDisk = false;
        // ⑥ 兼容模式/临时盘紧:减半保护保留,但不得破坏用户给的 50 下界
        if (fastMode) { batch = HalveWithFloor(batch); halvedFast = true; }
        if (diskTight) { batch = HalveWithFloor(batch); halvedDisk = true; }
        // ② 不分批:档位 ≥ 正常 且 补帧后总帧数少(≤ 用户给的最大批)
        // 【任务 T】"档位"取"内存档 × 性能档"较低者(与基准帧数同一口径):实测很慢的机器即使内存够大,
        // 也不该因为"整片一批"而让每批跑得又久又占盘。
        bool single = Math.Min((int)tier, (int)TierForPerf(score)) >= (int)DeviceTier.Normal
            && postInterpFrames > 0 && postInterpFrames <= SingleBatchMaxPostInterpFrames;
        if (single) batch = Math.Max(batch, postInterpFrames);
        int count = postInterpFrames <= 0 ? 1 : (int)Math.Min(int.MaxValue, ((long)postInterpFrames + batch - 1) / batch);
        string halveTxt = (halvedFast, halvedDisk) switch
        {
            (true, true) => "兼容模式+临时盘紧",
            (true, false) => "兼容模式",
            (false, true) => "临时盘紧",
            _ => "",
        };
        string full = rule
            + (halveTxt.Length > 0 ? $";{halveTxt}减半 → {batch} 帧/批" + (batch == WeakDeviceFramesPerBatch ? $"(被 {WeakDeviceFramesPerBatch} 下界挡住)" : "") : "")
            + (single ? $";补帧后总帧数 {postInterpFrames} ≤ {SingleBatchMaxPostInterpFrames} → 1 批(不分批)" : "")
            + $";源帧数 {sourceFrames},补帧后总帧数 {postInterpFrames} → 每批 {batch} 帧 × 预计 {count} 批";
        return new VideoBatchPlan(batch, count, sourceFrames, postInterpFrames, baseFrames, tier, single,
            halvedFast, halvedDisk, full);
    }

    /// <summary>一批的编号与槽位区间(1-based 序号)。用于日志/诊断,保证"分子(批号)与分母(批数)一定一致"。</summary>
    public readonly record struct VideoBatchInfo(int Number, int SlotCount, int StartSlot, int EndSlot);

    /// <summary>把"每批的槽位数"变成带编号的批次清单(纯函数,可单测)。
    /// 【为什么要有它 —— 真机 bug 2026-09-13】超分批日志原来在 async 任务里现算 `第 {bi+1}/{batchCount} 批`,
    /// 而 `bi` 是 **for 循环变量**(C# 里 for 的循环变量只有一个、被所有闭包共享;foreach 才是每轮一份)。
    /// 任务在 finally 里读到的往往是"循环已经推进、甚至已经结束"之后的值 —— 实测日志出现
    /// 「超分批 13/12(槽位 2640~2666)」与「超分批 2/12(槽位 0~239)」:槽位区间是对的(它是每轮局部量),
    /// 只有编号被读晚了(偏移 +1/+2)。把编号在【启动任务之前】定格成这份不可变清单,闭包就再也改不动它。
    /// 【不变量(有单测)】编号 1..N 严格递增;区间首尾相接(无空洞、无重叠);末批 EndSlot == 总槽位数-1。
    /// 【区间含义】Start/End 是按"本批槽位数"推算的【名义区间】(与切批用的前缀和同一口径);
    /// 日志里另按该批【实际槽位号】打印 min~max —— 去重后重复帧的槽位可能离代表帧很远(同组成员不连续),
    /// 这时两者会不同,不是错,是两种口径。</summary>
    public static IReadOnlyList<VideoBatchInfo> DescribeBatches(IReadOnlyList<int> batchSlotCounts)
    {
        var list = new List<VideoBatchInfo>();
        if (batchSlotCounts == null) return list;
        int acc = 0;
        for (int i = 0; i < batchSlotCounts.Count; i++)
        {
            int n = Math.Max(0, batchSlotCounts[i]);
            list.Add(new VideoBatchInfo(i + 1, n, acc, acc + Math.Max(0, n - 1)));
            acc += n;
        }
        return list;
    }

    /// <summary>减半但【不破用户下界】:结果钳到 ≥ WeakDeviceFramesPerBatch(50)。</summary>
    private static int HalveWithFloor(int frames) => Math.Max(WeakDeviceFramesPerBatch, frames / 2);

    // ===== 两个阶段各自的批计划(任务 Q2 要求 2)=====
    /// <summary>某一个阶段的批计划(含该阶段的输入分辨率/面积系数/每批帧数/批数/是否"仅供参考")。</summary>
    public readonly record struct StageBatchPlan(
        string Stage, string Order, int StageInputFrames, int InputWidth, int InputHeight, double AreaFactor,
        int FramesPerBatch, int BatchCount, bool Advisory, string Note);

    /// <summary>给出「本次顺序下两个阶段各自的批计划」—— 两阶段输入分辨率不同,必须各算各的(任务 Q2)。
    /// 【旧顺序(补帧→超分)】
    ///   · 补帧阶段:输入 = 源帧(分辨率 = 源),每批帧数按**源面积**算;
    ///   · 超分阶段:输入 = 补帧输出(帧数 = 源×补帧倍率,分辨率仍 = 源)→ 按源面积算,但总帧数大 k 倍。
    /// 【新顺序(超分→补帧)】
    ///   · 超分阶段:输入 = 源帧(分辨率 = 源);
    ///   · 补帧阶段:输入 = 超分输出(帧数 = 源帧数,但每帧像素 ×scale²)→ **每批帧数按放大后的面积算,显著更小**。
    /// 【Advisory 的含义】补帧阶段实际是按【转场分段】跑的(一段一次 RIFE 调用,不按批大小切),
    ///   所以它的"每批帧数/批数"是**等效参考值**(用于峰值估算与报告对照),不是执行参数;
    ///   超分阶段才是真正按每批帧数切批的阶段。两行都会写进日志,便于真机核对。
    /// 【待实测标定】面积反比是近似;scale² 也是近似(引擎可能按 2 的幂跑再缩回)。</summary>
    public static IReadOnlyList<StageBatchPlan> PlanStageBatches(double freeRamGB, int sourceFrames, double scale,
        int interpScale, int srcW, int srcH, bool upscaleFirst, bool fastMode = false, bool diskTight = false,
        PerfScore? perf = null)
    {
        if (sourceFrames < 0) sourceFrames = 0;
        int k = interpScale < 1 ? 1 : interpScale;
        int postInterp = sourceFrames * k;
        int hiW = (int)Math.Max(1, Math.Round(srcW * (scale > 0 ? scale : 1)));
        int hiH = (int)Math.Max(1, Math.Round(srcH * (scale > 0 ? scale : 1)));
        var list = new List<StageBatchPlan>();

        // 超分阶段:输入帧数 = 源帧(新顺序)/ 补帧输出(旧顺序);分辨率都按"它读的那批帧"算
        int upIn = upscaleFirst ? sourceFrames : postInterp;
        int upW = upscaleFirst ? srcW : srcW;   // 旧顺序的超分输入是补帧输出 → 仍是源分辨率
        int upH = upscaleFirst ? srcH : srcH;
        var up = PlanVideoBatches(freeRamGB, sourceFrames, upIn, fastMode, diskTight, upW, upH, hiW, hiH, perf);
        list.Add(new StageBatchPlan("超分", upscaleFirst ? "新顺序(超分→补帧)" : "旧顺序(补帧→超分)",
            upIn, upW, upH, AreaFactor(upW, upH), up.BatchSize, up.BatchCount, Advisory: false, Note: up.Rule));

        // 补帧阶段:输入帧数 = 源帧(旧顺序)/ 源帧(新顺序,超分不增减帧数);分辨率 = 源 / 放大后
        int ipW = upscaleFirst ? hiW : srcW;
        int ipH = upscaleFirst ? hiH : srcH;
        var ip = PlanVideoBatches(freeRamGB, sourceFrames, upscaleFirst ? sourceFrames : postInterp, fastMode, diskTight, ipW, ipH, ipW, ipH, perf);
        list.Add(new StageBatchPlan("补帧", upscaleFirst ? "新顺序(超分→补帧)" : "旧顺序(补帧→超分)",
            sourceFrames, ipW, ipH, AreaFactor(ipW, ipH), ip.BatchSize, ip.BatchCount, Advisory: true,
            Note: ip.Rule + ";补帧阶段实际按转场分段跑,此每批帧数为等效参考值"));
        return list;
    }

    /// <summary>「先超分再补帧」顺序的批计划(两侧帧数/分辨率不同 → 分开算,不共用一套参数)。
    /// 【为什么单独一个函数】该顺序下:超分阶段输入 = 源帧(分辨率=源),补帧阶段输入 = 超分输出
    /// (帧数不变,但每帧像素是源帧的 scale² 倍)。若两侧共用同一套"每批帧数",补帧侧的同屏像素量
    /// 会按 scale² 膨胀 —— 这正是不能共用参数的原因(用户明确要求分开算)。
    /// 【当前状态】该顺序【关闭】中(upscaleFirst=false,实测在 4x 补帧/短视频上净亏,见 VideoService 注释),
    /// 本函数只把批计算算好 + 单测覆盖,等将来实测判定值得开启时调用方直接取用即可(不重新启用)。</summary>
    public readonly record struct UpscaleFirstBatchPlan(
        int UpscaleFramesPerBatch, int UpscaleBatchCount, int InterpFramesPerBatch, int InterpBatchCount,
        int SourceFrames, int PostInterpFrames, DeviceTier Tier, string Rule);

    /// <summary>「先超分再补帧」的批计算(纯函数,可单测)。
    /// 超分侧:输入帧数 = 源帧数,分辨率 = 源 → 直接套主口径(源帧数即它要处理的总帧数)。
    /// 补帧侧:输入帧数 = 超分输出(= 源帧数),但每帧像素 × scale² → 每批帧数按面积反比缩小
    ///   (max(50, 超分侧每批帧数 ÷ scale²)),保持"同批像素量"同量级;补帧倍率只决定【输出】总帧数,不改变批大小。
    /// 【待实测标定】scale² 是面积口径的近似,没有实测的分块/显存峰值曲线;50 是用户下界(与主口径同一条)。</summary>
    public static UpscaleFirstBatchPlan PlanUpscaleFirstBatches(double freeRamGB, int sourceFrames, double scale,
        int interpScale, bool fastMode = false, bool diskTight = false, PerfScore? perf = null)
    {
        if (sourceFrames < 0) sourceFrames = 0;
        int isc = interpScale < 1 ? 1 : interpScale;
        int postInterp = sourceFrames * isc;
        // 超分侧:它要处理的就是源帧数 → 主口径里"补帧后总帧数"传源帧数
        var up = PlanVideoBatches(freeRamGB, sourceFrames, sourceFrames, fastMode, diskTight, 0, 0, 0, 0, perf);
        // 补帧侧:按面积反比缩小每批帧数(每帧像素 ≈ scale² 倍)
        double area = scale > 1 ? scale * scale : 1.0;
        int interpPer = Math.Max(WeakDeviceFramesPerBatch, (int)Math.Round(up.BatchSize / area));
        int interpCount = sourceFrames <= 0 ? 1 : (int)Math.Min(int.MaxValue, ((long)sourceFrames + interpPer - 1) / interpPer);
        string rule = $"[先超分再补帧] 档位={up.Tier};超分侧(输入=源帧,分辨率=源):{up.BatchSize} 帧/批 × {up.BatchCount} 批;"
            + $"补帧侧(输入=超分输出,每帧像素 ×{area:0.##}):{interpPer} 帧/批 × {interpCount} 批;"
            + $"源 {sourceFrames} 帧,补帧 {isc}x → 输出 {postInterp} 帧。依据:{up.Rule}";
        return new UpscaleFirstBatchPlan(up.BatchSize, up.BatchCount, interpPer, interpCount,
            sourceFrames, postInterp, up.Tier, rule);
    }
}
