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

    /// <summary>设备差的每批帧数。【2026-09-23 档位 ×2:50 → 80】这是【全档位下界】:任何档位、任何模式
    /// (含 fastMode/diskTight 减半)算出来的每批帧数都不许低于它。
    /// 【为什么跟着一起抬】下界如果不动,高档位抬到 2 倍之后,"设备差"这一档在总区间里的占比会变得极小,
    /// 减半保护 + 面积缩放一叠就全落在这个地板上(等于这批机器完全得不到这次提速);
    /// 而 80 帧在一批里占的临时盘(1080p 约 0.14GB)对任何能跑这个软件的机器都还是小数目。
    /// 【硬约束】抬它必须同时抬"临时盘余量"那道闸门(见 <see cref="LimitBatchByTempDisk"/>)——
    /// 只抬数字不给守门,就是任务 T 那条硬约束("抬上限必须同时抬峰值守门")的反面。</summary>
    public const int WeakDeviceFramesPerBatch = 80;

    /// <summary>设备正常(内存档或性能档判定为"正常"):每批 600 帧。【2026-09-23 档位 ×2:300 → 600】
    /// 【依据】用户本轮口径「档位 ×2」;300 仍低于"设备好+长片"的 1400,峰值(输入帧+本批输出帧并存)只有它的 3/14。</summary>
    public const int NormalDeviceFramesPerBatch = 600;

    /// <summary>设备好 + 视频不长:每批 700 帧。【2026-09-23 档位 ×2:350 → 700】
    /// 【依据】用户本轮口径「档位 ×2」。</summary>
    public const int StrongDeviceFramesPerBatch = 700;

    /// <summary>设备好 + 视频长("批内扩大"):每批 1400 帧。【2026-09-23 档位 ×2:700 → 1400】
    /// 【硬条件】只有"性能档 = Fast 且空闲内存 ≥ <see cref="StrongDeviceFreeRamGB"/> 才给" ——
    /// 见 <see cref="TierBaseFrames"/>;上界抬高同时必须抬高**峰值守门**(见 TempSpaceEstimate.NeedBytes
    /// 的"每批并存帧"项、<see cref="LimitBatchByTempDisk"/> 与 VideoService 的守门复算),不允许只抬上限不给守门。
    /// 【黑帧代价:已核对,不再随批大小线性放大(2026-09-23)】抬批之前必须先回答"批越大黑帧代价越大吗":
    /// VideoService 里那条"整批重跑"**已经不再是默认行为**(2026-09-16 二次修订)——
    /// `wholeBatchRetry = !anyFrame || defectiveFrames.Count >= curPG.Count`,即**只有"整批一帧没出"
    /// 或"整批全被判黑"才整批重跑**;普通的零星黑帧只重跑那几帧(同批好帧保留),而且带硬预算
    /// (90 + 帧数×3 秒,上限 240 秒)。所以批 ×2 的后果是:① 每批的**发生概率**下降(批数少了一半);
    /// ② 单次事故最坏仍被 240 秒预算封顶(超预算的帧回退源帧缩放,不是无限等)。
    /// ⚠ 唯一会放大的量:整批空产那一次要重跑的帧数(×2)⇒ 预算内救回的帧占比下降,
    /// 也就是"最坏情况下画质掉档的帧数"可能变多 —— 这是本轮**明知并接受**的取舍(墙钟不变,画质风险变大);
    /// 真要再进一步,得把整批重跑改成分块重跑,那是另一轮的活。</summary>
    public const int StrongDeviceLargeFramesPerBatch = 1400;

    /// <summary>「视频长」门槛之一(按时长):源帧数 ≥ 900(≈30 秒 @30fps)。【依据】用户点名的"短素材"是
    /// 72 帧/3 秒;900 帧(30 秒)是"明显属于长片"的下限。【待实测标定】</summary>
    public const int LongClipMinSourceFrames = 900;

    /// <summary>「视频长」门槛之二(按体量):补帧后总帧数 ≥ 1200。【依据】① 用户点名的"长素材"那条
    /// (他明确说的是"补帧完的帧总数")补帧后是 3420 帧,命中;点名的短素材补帧后只有 288 帧,不命中;
    /// ② 与批数挂钩:1200 帧按 600/批 是 2 批,扩到 1400/批 变 1 批 —— 省下的是引擎进程启动(每次秒级,
    /// 见 VideoPipeline.AssumedEngineStartupSecondsPerBatch),这正是"批内扩大"的收益来源。【待实测标定】</summary>
    public const int LongClipMinPostInterpFrames = 1200;

    /// <summary>【不分批】上限:补帧后总帧数 ≤ 800 且设备档位 ≥ 正常 → 整条素材一批跑完。
    /// 【2026-09-23 档位 ×2:400 → 800】【依据】800 与新的"设备好+短片"档(700)同量级:
    /// 整片都不超过这个数时,"不分批"的同屏临时帧不会超过用户已经认可的最大批太多,峰值不越界;
    /// 省下的是每批一次的引擎进程启动(秒级/次)。【待实测标定】
    /// 注:因为"补帧后总帧数 ≥ 源帧数",本条件已隐含"视频短";源帧数条件保留只为把用户口径写全。</summary>
    public const int SingleBatchMaxPostInterpFrames = 800;

    /// <summary>【2026-09-23 用户新条件】一批占的临时盘 ≤ 临时盘余量 × 这个比例。
    /// 【为什么要它】档位 ×2 之后,一批在同一时刻占盘的帧数也 ×2(输入帧 + 本批输出帧并存);
    /// 原来只有一道"整任务预估 vs 剩余空间"的闸门(超过就**直接让任务失败**),粒度太粗:
    /// ① 它算的是全片口径 + 1.6 安全系数,批大小在里面的权重随素材长度变化,短的素材几乎影响不到判定;
    /// ② 它只会**报错**,不会**自动把批调小** —— 而这正是档位放大后需要的动作(能跑就跑小点,别失败)。
    /// 【0.65 的来历】用户直接给的数;含义是"一批最多吃掉余量的 65%,留 35% 给编码器临时文件、
    /// 其它进程与并发批"。它比"整任务守门的 1.6 安全系数"更松(后者等价于批占比 ≤ 1/1.6 = 0.625),
    /// 所以**常见情形下它是第二道、不会误伤**;真正会拦住的是"余量小 + 单帧大(4K/8K)"那种峰值极端。</summary>
    public const double BatchTempDiskShareLimit = 0.65;

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
        // 【2026-09-14】这四个数字原先经"在线参数覆盖层"(ParamProfileRuntime)读,该功能整体删除后
        // 直接读本文件常量 —— 与"覆盖层为 null 时回落常量"逐字等价,批大小行为一个字节都没变。
        if (effective == DeviceTier.Strong)
            return longClip && perf == PerfScore.Fast && freeRamGB >= StrongDeviceFreeRamGB
                ? StrongDeviceLargeFramesPerBatch     // 700
                : StrongDeviceFramesPerBatch;         // 350
        if (effective == DeviceTier.Normal) return NormalDeviceFramesPerBatch;   // 300
        return WeakDeviceFramesPerBatch;                                         // 50(仍是全档位下界)
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
        // 【2026-09-23 修】下界必须与"档位基准"取小:`Math.Clamp(x, 80, tierFrames)` 在 tierFrames < 80
        // (旧配置/外部直接调用)时会**抛 ArgumentException**(Clamp 要求 min ≤ max)✗。
        // 语义仍然是"不许低于下界,也不许超过档位基准",只是当档位基准本身低于下界时以它为准。
        int floor = Math.Min(WeakDeviceFramesPerBatch, tierFrames);
        return Math.Clamp(scaled, floor, tierFrames);
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

    /// <summary>减半但【不破下界】:结果钳到 ≥ WeakDeviceFramesPerBatch;同时**绝不允许减半把批变大**
    /// (传进来的值本来就比下界小时以原值为准 —— 与 ScaleFramesForArea 的下界处理同一个道理)。</summary>
    private static int HalveWithFloor(int frames) => Math.Min(frames, Math.Max(WeakDeviceFramesPerBatch, frames / 2));

    // ===== 【2026-09-23 新条件】批内临时帧 ≤ 临时盘余量 × 0.65 =====

    /// <summary>临时盘闸门的结论。</summary>
    /// <param name="BatchFrames">闸门**之后**真正该用的每批帧数。</param>
    /// <param name="MaxFramesByDisk">按余量算出来的上限(每批并存帧口径)。</param>
    /// <param name="Shrunk">是否真的被这道闸门调小了(调用方据此决定要不要写日志)。</param>
    /// <param name="BatchGB">闸门后一批占的临时盘(GB,输入帧 + 本批输出帧并存)。</param>
    /// <param name="BudgetGB">余量里分给"一批"的额度(GB)= 余量 × <see cref="BatchTempDiskShareLimit"/>。</param>
    /// <param name="Note">一行说明(写日志用;拿不到余量时也如实说明)。</param>
    public readonly record struct TempDiskBatchGate(
        int BatchFrames, int MaxFramesByDisk, bool Shrunk, double BatchGB, double BudgetGB, string Note);

    /// <summary>【用户条件】一批占的临时盘 ≤ 临时盘余量 × <see cref="BatchTempDiskShareLimit"/>。
    /// 超了就**把批调小**到能放下为止(而不是让任务失败 —— 这是与既有"整任务守门"最大的区别:
    /// 那道闸门是"放不下就别跑",这道是"放不下就跑小一点")。
    ///
    /// 【口径:一批占多少】= 每批帧数 × (峰值帧体积 + 源帧体积)。
    /// 与 <see cref="TempSpaceEstimate.NeedBytes"/> 里那一项**同一个口径**(一批里同时占盘的两侧:
    /// 输入帧还没删、输出帧已经写下来),所以两处的数字可以直接对照。
    /// 【为什么不乘安全系数 1.6】用户给的条件就是"批内临时帧 ≤ 余量 × 0.65",这条本身已经把余量切走 35%;
    /// 再叠一个 1.6 会让"实际只占 40%"的机器也被拦住,与"激进放大"的意图相反。
    /// 整任务那道闸门(带 1.6)原样保留 —— 两道闸门管的是不同的事。
    /// 【地板】调小不得低于 <paramref name="floorFrames"/>:批太小会把"每批一次引擎启动"的开销摊到每帧上。
    /// 万一余量连地板都放不下,这里**不再往下压**(压到 1 帧/批也救不了整任务),并如实写进 Note ——
    /// 那种情形该由整任务守门去报错。
    /// 【拿不到余量怎么办】freeDiskGB ≤ 0 或单帧体积 ≤ 0(探测失败)→ 原样返回,并说明"没做这道闸门"
    /// (与仓库既有口径一致:拿不到数据就不做判定,而不是猜一个)。</summary>
    public static TempDiskBatchGate LimitBatchByTempDisk(int batchFrames, double freeDiskGB,
        double peakFrameMb, double sourceFrameMb, int floorFrames = WeakDeviceFramesPerBatch)
    {
        if (batchFrames <= 0) return new TempDiskBatchGate(batchFrames, batchFrames, false, 0, 0, "批大小无效,未做临时盘闸门");
        if (freeDiskGB <= 0 || peakFrameMb <= 0)
            return new TempDiskBatchGate(batchFrames, batchFrames, false, 0, 0,
                "拿不到临时盘余量/单帧体积 ⇒ 未做这道闸门(交由整任务守门判定)");
        double perFrameMb = peakFrameMb + Math.Max(0, sourceFrameMb);
        if (perFrameMb <= 0)
            return new TempDiskBatchGate(batchFrames, batchFrames, false, 0, 0, "单帧体积口径无效 ⇒ 未做这道闸门");
        double budgetMb = freeDiskGB * 1024.0 * BatchTempDiskShareLimit;
        int maxByDisk = (int)Math.Max(1, Math.Floor(budgetMb / perFrameMb));
        int floor = Math.Max(1, floorFrames);
        int target = Math.Min(batchFrames, maxByDisk);
        bool belowFloor = target < floor;
        if (belowFloor) target = Math.Min(batchFrames, floor);   // 地板优先:再压也救不了整任务,交给整任务守门报错
        double batchGB = target * perFrameMb / 1024.0;
        double budgetGB = budgetMb / 1024.0;
        string note = target < batchFrames
            ? $"临时盘闸门:一批 {target} 帧 ≈ {batchGB:0.##}GB(余量 {freeDiskGB:0.#}GB × {BatchTempDiskShareLimit:0.##} = {budgetGB:0.##}GB)"
              + (belowFloor ? $";按余量算只能 {maxByDisk} 帧,已保地板 {floor} 帧(整任务守门随后判定)" : "")
            : $"临时盘闸门未触发(一批 {batchFrames} 帧 ≈ {batchGB:0.##}GB ≤ 额度 {budgetGB:0.##}GB)";
        return new TempDiskBatchGate(target, maxByDisk, target < batchFrames, batchGB, budgetGB, note);
    }

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

    // ===================== F3:ONNX 显存策略(并发路数 / 分块真降档) =====================

    /// <summary>ONNX(DirectML)逐帧推理的【并行会话路数】—— 按"可用显存"动态定,不再按"总显存档位"一刀切。
    ///
    /// 【要修的事】真机诊断(RTX 5060 Laptop 8GB / 16GB 内存):8GB 卡的有效显存墙只有 6.0GB,
    /// 旧口径只按 <c>EffectiveVramGB</c> 判档(≥12→3 路、否则 2 路)⇒ 这台机器照样开 2 路,
    /// 两路各持一份 DirectML 推理工作集,于是 ONNX 超分侧反复 E_OUTOFMEMORY(0x8007000E),
    /// 每个失败帧都回退成"源帧缩放"(等于没放大,用户看到的却是"成功")。
    ///
    /// 【新口径】预算 = min(有效显存, 实测空闲显存)—— 空闲显存只有当调用方【确实实测到】时才参与
    /// (AMD/Intel 上那是估算值,SafeRender 的注释明确警告过不能拿它当判据;故用 <c>double?</c> 表达
    /// "没实测就别传")。预算 ≥12GB 才 3 路,≥8GB 才 2 路,否则 1 路。
    ///
    /// 【取舍(用速度换不失败)】路数减少 = 吞吐下降(实测 2 路 ≈1.25x,故从 2 路退到 1 路约慢 20%)。
    /// 这是刻意的:一次 E_OUTOFMEMORY 会让该帧彻底失去超分(回退源帧缩放),比慢 20% 糟得多;
    /// 而显存充裕的机器(预算 ≥8GB)口径不变,不为其降速。</summary>
    public static int OnnxSessionConcurrency(bool wantGpu, double effectiveVramGB, double? freeVramGBMeasured)
    {
        if (!wantGpu) return 1;   // 明确要 CPU:多会话只是把 CPU 抢成几份,总时间不变、内存翻倍
        double budget = effectiveVramGB;
        if (freeVramGBMeasured.HasValue && freeVramGBMeasured.Value > 0)
            budget = Math.Min(budget, freeVramGBMeasured.Value);
        if (budget >= 12) return 3;
        if (budget >= 8) return 2;
        return 1;
    }

    /// <summary>写进日志的"路数依据"一行(排查"为什么只开 1 路"时必须一眼看到输入值)。</summary>
    public static string OnnxConcurrencyRule(bool wantGpu, double effectiveVramGB, double? freeVramGBMeasured)
    {
        if (!wantGpu) return "明确使用 CPU → 固定 1 路(多路只会把 CPU 抢成几份)";
        // 一律一位小数:显存是按 MiB 报的,格式化掉小数会让"6.0GB 墙"看起来像"6GB 总量"(排查时最容易看错的一处)
        string free = freeVramGBMeasured.HasValue && freeVramGBMeasured.Value > 0
            ? $"{freeVramGBMeasured.Value:0.0}GB(已实测)"
            : "未实测(不参与判定)";
        double budget = freeVramGBMeasured.HasValue && freeVramGBMeasured.Value > 0
            ? Math.Min(effectiveVramGB, freeVramGBMeasured.Value) : effectiveVramGB;
        return $"有效显存 {effectiveVramGB:0.0}GB / 空闲显存 {free} → 预算 {budget:0.0}GB;"
             + $"档位口径:预算 ≥12GB→3 路、≥8GB→2 路、否则 1 路";
    }

    /// <summary>ONNX 分块的【真降档阶梯】(F3):整图/大块在显存不足时按 自动 → 512 → 256 → 128 逐级真降重试。
    ///
    /// 【要修的事】v1.3.5 公告的"显存不足自动降分块 auto→512→256→128 真降档"只落在【ncnn 引擎路径】
    /// (EngineService.RunEngAsync,真机日志里那句"显存不足,分块 512→256 在 GPU 上重试"就是它);
    /// ONNX(DirectML)路径**从来没有**这条:分块由 TileFor(显存) 算一次,失败就抛,
    /// 逐帧 worker 只好把该帧"回退源帧缩放"—— 诊断包里"ONNX 超分几十次失败、超分等于没放大"就是这么来的。
    /// 本函数把阶梯补齐,ONNX 侧 OOM 也能先降块重试,而不是立刻放弃整帧。
    ///
    /// 【取舍(用速度换不失败)】块越小,每块固定的 DirectML 往返 + 张量拷贝开销重复得越多
    /// (实测 1080p:整帧 480ms vs 512 分块 1078ms)。所以阶梯【只在失败后】走,正常帧一字不变;
    /// 且最低到 <see cref="MinOnnxTile"/> 就停(再小纯亏,显存收益已趋平)。
    /// 元素严格递减(不会拿同一块重试),首元素永远是调用方算出来的自动档。</summary>
    public static int[] OnnxTileLadder(int autoTile)
    {
        int cur = Math.Max(1, autoTile);
        var res = new System.Collections.Generic.List<int>(4) { cur };
        foreach (int step in OnnxTileLadderSteps)
        {
            if (step >= cur) continue;   // 只往更小降:不重复、也不为了"降档"反而变大
            res.Add(step);
            cur = step;
        }
        return res.ToArray();
    }

    /// <summary>ONNX 降档阶梯的档位(与 ncnn 路径的 512→256→128 同口径,便于日志对照)。</summary>
    public static readonly int[] OnnxTileLadderSteps = { 512, 256, 128 };

    /// <summary>ONNX 分块下限:再小就没有意义(块越小 Run 次数越多,显存收益已趋平)。</summary>
    public const int MinOnnxTile = 128;
}
