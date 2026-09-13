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

    /// <summary>设备好 + 视频不长:每批 200 帧(用户给定)。</summary>
    public const int StrongDeviceFramesPerBatch = 200;

    /// <summary>设备好 + 视频长("批内扩大"):每批 400 帧(用户给定)。</summary>
    public const int StrongDeviceLargeFramesPerBatch = 400;

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
    ///   ③ 设备好:源帧数 ≥ LongClipMinSourceFrames(900,视频长)→ 400 帧/批;否则 200 帧/批;
    ///   ④ 设备正常:沿用既有内存档 VideoBatchSize(120/180 —— 仓库里既有的实测标定表);
    ///   ⑤ 设备差:50 帧/批(用户下界);
    ///   ⑥ fastMode / diskTight 各自把每批帧数减半(既有的防爆盘/弱机保护),再【钳到 ≥ 50】。
    ///      ⚠ 冲突点(已如实报告、未自行决定别的折中):在"设备差(基准 50)"档上,减半(→25)会被 50 下界
    ///      挡住 = 该档减半不生效;其余档位(200→100、400→200、180→90、120→60)减半照常生效。
    ///   批数 = ⌈补帧后总帧数 ÷ 每批帧数⌉(不分批时 = 1;这是【预计值】,真正切批按去重后的唯一帧组数,
    ///   由调用方按实际结果再记一行日志)。
    /// 【不设批数上限】仍成立:限批数只能让每批帧数随素材线性变大,同屏临时帧(输入+输出并存)跟着涨 ——
    ///  与"峰值不暴涨"直接冲突(旧注释里的论证保持不变)。</summary>
    public static VideoBatchPlan PlanVideoBatches(double freeRamGB, int sourceFrames, int postInterpFrames,
        bool fastMode = false, bool diskTight = false)
    {
        if (sourceFrames < 0) sourceFrames = 0;
        if (postInterpFrames < sourceFrames) postInterpFrames = sourceFrames;   // 补帧后帧数 ≥ 源帧数(倍率 ≥1)
        var tier = TierFor(freeRamGB);
        // ① 每批帧数基准(用户口径)
        int baseFrames;
        string rule;
        if (tier == DeviceTier.Strong)
        {
            // 「视频长」= 时长长(源帧数)或补帧后体量大(总帧数):两者任一命中就按 400 帧/批扩大
            bool longClip = sourceFrames >= LongClipMinSourceFrames || postInterpFrames >= LongClipMinPostInterpFrames;
            baseFrames = longClip ? StrongDeviceLargeFramesPerBatch : StrongDeviceFramesPerBatch;
            rule = $"设备好(空闲内存 {freeRamGB:0.#}G ≥ {StrongDeviceFreeRamGB:0.#}G)+ "
                + (longClip
                    ? $"视频长(源 {sourceFrames} 帧 ≥ {LongClipMinSourceFrames} 或补帧后 {postInterpFrames} 帧 ≥ {LongClipMinPostInterpFrames})→ 批内扩大到 {baseFrames} 帧/批"
                    : $"视频不长(源 {sourceFrames} 帧 < {LongClipMinSourceFrames} 且补帧后 {postInterpFrames} 帧 < {LongClipMinPostInterpFrames})→ {baseFrames} 帧/批");
        }
        else if (tier == DeviceTier.Normal)
        {
            baseFrames = Math.Max(WeakDeviceFramesPerBatch, VideoBatchSize(freeRamGB));   // 既有内存档(≥50 恒成立)
            rule = $"设备正常(空闲内存 {freeRamGB:0.#}G)→ 沿用既有内存档 {baseFrames} 帧/批";
        }
        else
        {
            baseFrames = WeakDeviceFramesPerBatch;
            rule = $"设备差(空闲内存 {freeRamGB:0.#}G < {NormalDeviceFreeRamGB:0.#}G)→ 用户下界 {baseFrames} 帧/批";
        }
        int batch = baseFrames;
        bool halvedFast = false, halvedDisk = false;
        // ⑥ 兼容模式/临时盘紧:减半保护保留,但不得破坏用户给的 50 下界
        if (fastMode) { batch = HalveWithFloor(batch); halvedFast = true; }
        if (diskTight) { batch = HalveWithFloor(batch); halvedDisk = true; }
        // ② 不分批:设备 ≥ 正常 且 补帧后总帧数少(≤ 用户给的最大批)
        bool single = tier != DeviceTier.Weak && postInterpFrames > 0 && postInterpFrames <= SingleBatchMaxPostInterpFrames;
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
        int interpScale, bool fastMode = false, bool diskTight = false)
    {
        if (sourceFrames < 0) sourceFrames = 0;
        int isc = interpScale < 1 ? 1 : interpScale;
        int postInterp = sourceFrames * isc;
        // 超分侧:它要处理的就是源帧数 → 主口径里"补帧后总帧数"传源帧数
        var up = PlanVideoBatches(freeRamGB, sourceFrames, sourceFrames, fastMode, diskTight);
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
