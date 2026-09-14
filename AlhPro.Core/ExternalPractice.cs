namespace AlhPro.Core;

/// <summary>【任务 W · 2026-09-14】联网学到的"外部最佳实践"的**落点**(纯常量 + 逐条出处,可单测)。
///
/// 【为什么要单独一个文件,而不是直接改阈值】本工程的口径是:**凡是外部资料与真机实测冲突,以实测为准;
/// 凡是会改变画面语义、或没有可靠出处的一律只写"建议"不实施**。所以研究结论必须有一个**可审计、可测试、
/// 但不动生产行为**的落点 —— 就是这里。每个常量都带 [已接线]/[仅建议] 标注与原文摘要 + 链接;
/// 标 [仅建议] 的一律**没有任何生产代码读它**(单测只钉住"数字没被手滑改掉")。
///
/// ==== 逐条结论(2026-09-14 核对)====
/// ① **切点不插值 = 业界做法,且是唯一做法**([已接线],与本工程既有行为一致,无需改代码):
///    · Hybrid 作者 Selur 原话(该帖共 2 页;#14 在第 2 页):「The scene change detection basically just cuts the
///      scene into chunks, feeds these chunks into RIFE and then adds duplicates around the scene changes to
///      meet the desired frame rate.」—— 分段 + 切点补重复帧。
///      <https://forum.selur.net/thread-3940.html>(标题 "RIFE Manual Scene Cut";第 2 页为 `thread-3940-page-2.html`)
///    · VSGAN-tensorrt-docker(VapourSynth 做 VFI 的参考工程)标准写法 = 切点处用**原始帧**替换插值帧:
///      `core.akarin.Select([clip, clip_orig], clip_sc, "x._SceneChangeNext 1 0 ?")`。
///      <https://github.com/styler00dollar/VSGAN-tensorrt-docker>
///      与本工程 <see cref="CutAwareSchedule"/>"切点强制拷贝、绝不合成"**同一条规则**。
/// ② **没有"更好的做法"**:Selur 说 RIFE 本身"blindly interpolating",想改成"预测末帧再插值"只能改 RIFE 源码;
///    唯一的替代是"切点做混合(morph)",而 Selur 把它描述为**关掉切点检测后的默认坏行为**:
///    「If you want morphing on scene changes instead, simply disable the scene change detection.」(同一出处)。
///    **Topaz Video 官方教程同一结论**(商业软件,措辞最直白):
///    「Scene Detection: If your video contains multiple shots or camera changes, **enable scene detection to
///    prevent interpolation artifacts at cut points**.」
///    <https://www.topazlabs.com/learn/how-to-use-frame-interpolation-in-topaz-video>
///    ⇒ 三家(SVP 生态的 Hybrid、VapourSynth 的 VSGAN、商业的 Topaz)**做法一致:切点处不插值**。
/// ③ **切点阈值标定参照**([已接线] 用作 25 的旁证):PySceneDetect `ContentDetector` 默认
///    `threshold = 27.0`,度量为"0~255 量级的平均像素变化(HSV 加权 dHue/dSat/dLum)"。
///    <https://www.scenedetect.com/docs/latest/api/detectors.html> —— 与本工程 25 同量级。
///    另:Selur 与 VSGAN 用的 `misc.SCDetect` 默认 `threshold=0.10`,但那是**归一化口径,不能换算到 0~255**。
/// ④ **已知残差(业界同样没解决,不是本工程缺陷)**:切点强制拷贝 = 该处"重复一帧";
///    在**摇镜**上会被看成末帧卡顿(forum.selur.net thread-3940 里用户的原话抱怨),Selur 的答复是
///    "RIFE 就是盲插,想改得去改 RIFE"。
/// ⑤ **【仅建议】切点最小间距(滞回)**:PySceneDetect 默认 `min_scene_len = 15` 帧(同一出处)。
///    本工程**没有**这一条 ⇒ 闪光/频闪素材会连判多个切点、连出多次强制拷贝。
///    加它会改变"哪些帧不再受切点保护"(画面语义变化),故**只建议**。
/// ⑥ **【仅建议】自适应切点判据**(替代/补充本工程的"拉普拉斯能量比"):PySceneDetect `AdaptiveDetector`
///    默认 `adaptive_threshold=3.0`(与局部滚动均值之比)、`min_content_val=15.0`、`window_width=2`,
///    文档明确说它「can help mitigate false detections in situations such as fast camera motions」
///    —— 正对本工程"快摇镜头误判为切"的风险。同样会改变判谁的切点,只建议。
/// ⑦ **`misc.SCDetect` 的已知弱点**(决定我们**不**改成直方图法):VSGAN 作者原话
///    「It struggles harder with similar colors and tends to skip more changes」= **漏检**(宁可不出鬼影的方向)。
/// ⑧ **引擎线程 `-j`**:上游 README 写默认 `1:2:2`、「try increasing thread count to achieve faster processing」
///    (<https://github.com/xinntao/Real-ESRGAN-ncnn-vulkan>、<https://github.com/nihui/waifu2x-ncnn-vulkan>);
///    但**本工程真机实测推翻了它**:proc≥4 时 6 次运行 5 次(83%)报 `vkQueueSubmit failed` → 带状/整帧黑帧
///    (退出码仍为 0),且 1:2:1 / 1:4:1 都比 1:1:1 **更慢**(详见 SafeRender.GetEngineThreadArgs 的实测记录)。
///    ⇒ **以真机实测为准,`-j 1:1:1` 不变**;只有 `-t 0`(上游默认)与本工程一致。
/// ⑨ **多 GPU 用法**(【仅建议】,本机无多卡、无法验证):上游支持 `-g 0,1,2` + `-t 0,0,0` + `-j 1:2,2,2`
///    —— 但这是**按输入文件轮转分派到多卡**(目录批),不是"单张图内部并行";要吃到它必须走目录批形态。
/// ⑩ **模型选择(官方权威)**:Real-ESRGAN 官方把模型按用途分成三张表 ——
///    "For General Images"(`RealESRGAN_x4plus`、"X4 model for general images";`realesr-general-x4v3`
///    "A tiny small model (consume much fewer GPU memory and time); not too strong deblur and denoise capacity")、
///    "For Anime Images / Illustrations"(`RealESRGAN_x4plus_anime_6B`,"Optimized for anime images")、
///    "For Animation Videos"(`realesr-animevideov3`,"Anime video model with XS size")。
///    <https://github.com/xinntao/Real-ESRGAN/blob/master/docs/model_zoo.md>
///    社区版的选型表(4k-video-upscaler-colab Model Selection Guide)给同一结论,并明确说出我们实测到的取舍:
///    animevideov3「Optimized for temporal consistency across video frames / Video Consistency: Best」,
///    x4plus_anime_6B「Slowest processing time ... Best For: Static anime images or short video clips」,
///    x4plus「General-purpose model optimized for natural images and live-action content」。
///    <https://deepwiki.com/yuvraj108c/4k-video-upscaler-colab/4.2-model-selection-guide>
/// ⑪ **许可(可商用/可再分发)**:`Real-ESRGAN`(代码与权重,含 realesr-animevideov3 / realesrgan-x4plus /
///    realesrgan-x4plus-anime / realesr-general-x4v3)为 **BSD 3-Clause**;
///    `Real-ESRGAN-ncnn-vulkan` 为 **MIT**(并附 realsr-ncnn-vulkan 的 MIT 声明)。
///    两者都允许"再分发 + 商用",只需保留版权与许可声明。<https://github.com/xinntao/Real-ESRGAN/blob/master/LICENSE>
/// ⑫ **补帧的 VFR 处理(业界)**【部分仅建议】:ddfi-rife 的固定流水线 =
///    「Remove duplicated frames(resulting a vfr video)→ Interpolate → Extract timestamps → 'Correct' the
///    interpolated video with calculated timestamps → Convert to 60fps cfr video」
///    <https://github.com/Mr-Z-2697/ddfi-rife> —— 即**先去重复帧、再用真时间戳重算目标时刻、最后转 CFR**。
///    本工程已有等价的两步(真 PTS 时长表 + 目标帧数 = round(真实时长×目标帧率) + 总时长守恒),
///    但**缺少"先做 dup-frame 去除"这一步**(见 <see cref="DedupFirstIsIndustryPractice"/> 的说明)。</summary>
public static class ExternalPractice
{
    // ============ ⑨ 引擎线程:上游默认(仅作对照;本工程因实测黑帧/更慢而不用) ============

    /// <summary>[仅建议·不采用] 上游 ncnn-vulkan 的 `-j` 默认值。
    /// 原文:「-j load:proc:save  thread count for load/proc/save (default=1:2:2)」
    /// <https://github.com/xinntao/Real-ESRGAN-ncnn-vulkan> —— **本工程不用**:
    /// 真机实测 proc≥4 → 83% 运行出黑帧、且 1:2:1/1:4:1 比 1:1:1 慢。以实测为准。</summary>
    public const string UpstreamEngineThreadArgsDefault = "1:2:2";

    /// <summary>[已接线·等同] 上游 `-t` 的默认值:`-t tile-size (>=32/0=auto, default=0)`(同一出处)。
    /// 本工程生产路径恒用 `-t 0`,与上游默认**一致**,故无需改动。
    /// 【2026-09-14 核对上游源码:**`-t 0` 不是 256,而是按显存预算自动取值**】
    /// `src/main.cpp`:`uint32_t heap_budget = ncnn::get_gpu_device(gpuid[i])->get_heap_budget();`
    /// 然后 `if (heap_budget > 1900) tilesize[i] = 200; else if (heap_budget > 550) tilesize[i] = 100;
    /// else if (heap_budget > 190) tilesize[i] = 64; else tilesize[i] = 32;`
    /// <https://github.com/xinntao/Real-ESRGAN-ncnn-vulkan/blob/master/src/main.cpp>
    /// ⇒ README **从未**推荐过 256/128 这类显式分块值;显式分块的唯一官方收益是"省显存"。
    /// 【结论:不动】本工程没有"分块过大导致爆显存"的实测,而显式分块会引入块不一致
    /// (母公司 README 原文:「it may introduce block inconsistency … first crops the input image into several tiles,
    /// and then processes them separately, finally stitches together.」)=> **保持 `-t 0`**。</summary>
    public const int UpstreamTileSizeAuto = 0;

    /// <summary>[仅建议] 上游多 GPU 用法:`-g 0,1,2` 配合 `-t 0,0,0` 与 `-j 1:2,2,2`(同一出处)。
    /// 【2026-09-14 核对上游源码:多 GPU = **按输入文件做数据并行**,不是单图内部并行】
    /// `src/main.cpp` 为每个 device 各建一个 `RealESRGAN` 实例,而**所有**输入被推进**同一个** `TaskQueue toproc`,
    /// 各卡的 proc 线程从同一个队列里抢任务 ⇒ **一张图 = 一个任务 = 落在一张卡上**,其余卡空转;
    /// 没有任何代码把一张图的 tile 拆到多卡。且 `tilesize.size()` / `jobs_proc.size()` 必须等于设备数。
    /// 官方对多卡加速的建议是**多进程**(`--num_process_per_gpu`,见 anime_video_model.md),也不是拆单图。
    /// ⇒ 本工程若要吃到多卡,必须走"目录批 + 多 `-g`"的形态;本机无多卡,**未验证**,故仅作建议。</summary>
    public const string UpstreamMultiGpuHint = "-g 0,1,2 -t 0,0,0 -j 1:2,2,2(按输入文件轮转,不是拆单图)";

    /// <summary>[已核·解释本工程实测]**为什么提高 `-j proc` 不会有吞吐收益**:上游源码把 proc 线程数
    /// **钳到硬件的 Vulkan 计算队列数** —— `int gpu_queue_count = ncnn::get_gpu_info(gpuid[i]).compute_queue_count();
    /// jobs_proc[i] = std::min(jobs_proc[i], gpu_queue_count);`,而 ncnn 的 `VulkanDevice::acquire_queue`
    /// 在没空队列时**阻塞等待**(`while (free_queue_count == 0) { queue_condition.wait(queue_lock); }`)。
    /// <https://github.com/xinntao/Real-ESRGAN-ncnn-vulkan/blob/master/src/main.cpp>、
    /// <https://github.com/Tencent/ncnn/blob/master/src/gpu.cpp>
    /// ⇒ 超出队列数的 proc 线程只会"排队空等",这与本工程实测的"1:2:1 / 1:4:1 比 1:1:1 更慢、
    /// 且 proc≥4 出 `vkQueueSubmit failed` 黑帧"在机理上吻合。
    /// 【另:一条流行说法被源码否掉】"CPU 预处理/后处理是瓶颈"——实际情况是
    /// `realesrgan_preproc.comp` / `realesrgan_postproc.comp` 是 **Vulkan 计算着色器**;CPU 只做解码/编码
    /// (正是 README 点名的 load/save 两段)。所以"多开线程压 CPU 瓶颈"这条思路在这里不成立。</summary>
    public const string ProcThreadsAreClampedToComputeQueues =
        "jobs_proc = min(jobs_proc, compute_queue_count);队列不足时阻塞等待 ⇒ 多开 proc 无收益";

    /// <summary>[已核·解释本工程实测]**为什么"1x 输出全黑"是可解释的,而不只是"本机怪毛病"**:
    /// ① 上游 `-s` 只声明 2/3/4(`-s scale  upscale ratio (can be 2, 3, 4. default=4)`),
    ///    **`-s 1` 不在契约内**;`-s 1` 是 waifu2x 的概念(`1/2/4/8/16/32`,且 waifu2x 的 `-s 1` 是"不缩放")。
    /// ② 上游源码里 **`-s` 根本没有校验**(tile / `-j` / 扩展名/格式都校验了,模型与倍率的校验被注释掉了)。
    /// ③ 模型文件名按倍率拼(`"%s/%s-x%s.param"`)⇒ `-s 1` 会去找 `realesr-animevideov3-x1.param`;
    ///    而官方 ncnn 发行包里**只有 x2/x3/x4**;载入失败时源码只打印 `_wfopen ... failed` 便**继续执行**
    ///    (拿到的 `FILE*` 为 null)→ 网络未载入、输出缓冲已分配但没被填充 ⇒ **输出全黑且退出码仍是 0**。
    ///    <https://github.com/xinntao/Real-ESRGAN-ncnn-vulkan/blob/master/src/realesrgan.cpp>
    /// ⇒ 本工程"1x 全黑护栏"**必须保留**:它是上游契约外的用法,且失败是静默的。
    /// 【不确定度】"`-s 1` ⇒ 全黑"这一条是**源码级推断**(不是上游文档),与本工程真机实测一致但未逐路径复现。</summary>
    public const string Scale1IsOutsideUpstreamContract =
        "-s 只声明 2/3/4;ncnn 发行包无 -x1 权重;载入失败不报错 ⇒ 静默全黑(退出码 0)";

    // ============ ③④⑤⑥ 场景切换:外部参照值 ============

    /// <summary>[已接线·旁证] PySceneDetect `ContentDetector` 默认阈值 27.0(0~255 量级的平均像素变化,HSV 加权)。
    /// 本工程 <see cref="SceneCutJudge.DiffThreshold"/> = 25.0 与之同量级 —— 这是"25 没写错"的**唯一**外部旁证。
    /// <https://www.scenedetect.com/docs/latest/api/detectors.html></summary>
    public const double PySceneDetectContentThresholdDefault = 27.0;

    /// <summary>[仅建议] PySceneDetect `AdaptiveDetector` 默认 `min_content_val`(与上面同一口径的绝对下限)。
    /// 与 <see cref="PySceneDetectAdaptiveThresholdDefault"/> 配合,用于压制"快摇镜头误判"。</summary>
    public const double PySceneDetectAdaptiveMinContentVal = 15.0;

    /// <summary>[仅建议] PySceneDetect `AdaptiveDetector` 默认 `adaptive_threshold` —— 帧分与**局部滚动均值**之比。
    /// 文档原文:「can help mitigate false detections in situations such as fast camera motions」。
    /// 本工程现在没有"与局部均值比"这条,只有"拉普拉斯能量比";改它 = 改变判谁的切点 ⇒ 只建议。</summary>
    public const double PySceneDetectAdaptiveThresholdDefault = 3.0;

    /// <summary>[仅建议] PySceneDetect `AdaptiveDetector` 默认 `window_width`(前后各取几帧求滚动均值)。</summary>
    public const int PySceneDetectAdaptiveWindowWidth = 2;

    /// <summary>[仅建议] PySceneDetect 默认 `min_scene_len = 15`(帧)= **切点最小间距/滞回**。
    /// 本工程**没有**这条:闪光、频闪、快速剪辑会让多个相邻对被判为切点 → 连出多次"强制拷贝"→ 可见顿挫串。
    /// 加上它会**改变"哪些帧不再受切点保护"**(即会重新开始跨这些帧合成)= 画面语义变化 ⇒ 只建议,不实施。
    /// <https://www.scenedetect.com/docs/latest/api/detectors.html></summary>
    public const int PySceneDetectMinSceneLenFramesDefault = 15;

    /// <summary>[仅建议·不换算] `misc.SCDetect` 的默认阈值 —— Hybrid(Selur)与 VSGAN 都用 0.10。
    /// Selur 原话:「For me, personally, using 0.10 usually works fine」,并给出更硬的素材要 0.08、极难的要 0.04。
    /// <https://forum.selur.net/thread-3940.html> 、<https://github.com/styler00dollar/VSGAN-tensorrt-docker>
    /// **注意:`misc.SCDetect` 的度量是归一化的(0~1),与本工程的 0~255 mean|diff 不是同一把尺子**,
    /// 所以这个 0.10 **绝不能**拿来当我们的阈值。若日后要改用直方图/归一化度量,才需要重新标定。
    /// 另:VSGAN 作者指出 SCDetect「struggles harder with similar colors and tends to skip more changes」= **漏检倾向**。</summary>
    public const double MiscScDetectThresholdDefault = 0.10;

    /// <summary>[仅建议] 上述"更难素材"的阈值为 0.10 的 0.8 倍(0.08);极难素材为 0.4 倍(0.04)。
    /// Selur 原话:「0.08 works fine here for all but the last scene change, the one would require 0.04.(without cropping)」
    /// —— 他点名的主因是**未裁黑边**:画面大面积静止时,整帧平均会稀释掉切点差异。</summary>
    public const double MiscScDetectThresholdHardCaseRatio = 0.8;

    // ============ ⑫ VFR:业界流水线的步骤(用于对照本工程缺哪一步) ============

    /// <summary>[仅建议] 业界对"CFR 动画含重复帧(一拍二/三)"的固定第一步 = **先删重复帧**,再插值。
    /// ddfi-rife 的步骤表逐字:1. Remove duplicated frames(resulting a vfr video that technically has minimal fps 8)
    /// 2. Interpolate 8x 3. Extract timestamps from de-dupped video 4. "Correct" the interpolated video with
    /// calculated timestamps 5. Convert to 60fps cfr video。<https://github.com/Mr-Z-2697/ddfi-rife>
    /// **商业软件把这一步做成了开关**(同一方向的第二出处):Topaz Video 的 Frame Interpolation 模块里
    /// 「Duplicate Frames: This setting **detects and removes duplicate frames** in your source footage,
    /// **replacing them with newly interpolated frames**.」
    /// <https://www.topazlabs.com/learn/how-to-use-frame-interpolation-in-topaz-video>
    /// 【对本工程的意义】本工程的"平滑时间轴"计划器只看 **PTS 间隔**是否有缺口;而"一拍二"的 CFR 素材
    /// **PTS 是均匀的**(缺口 0 个)→ 计划器正确地不填平,**但也就没有任何东西去消除"一拍二"本身的顿挫**。
    /// 业界答案是先去重复帧(→ 变成 VFR)→ 在**去重后的真时间轴**上插值。本工程已有去重子系统
    /// (DedupTier / DetectDupFramesAdaptive),但**它没有被接成插值时间轴的输入**(用户可选地单独跑)。
    /// 这是**"可能漏掉的一步"**,属流程改动 ⇒ 只建议,不在本次实施。</summary>
    public const string DedupFirstIsIndustryPractice =
        "先去重复帧 → 用去重后的真时间戳算目标帧时刻 → 插值 → 转 CFR(ddfi-rife 的固定流水线;Topaz 把它做成开关)";

    /// <summary>[已核·用于对照 ①] Topaz Video 官方教程里"切点保护"的原文,以及它把"去重复帧"做成独立开关:
    /// 「Scene Detection: If your video contains multiple shots or camera changes, enable scene detection to
    /// prevent interpolation artifacts at cut points.」
    /// 「Duplicate Frames: This setting detects and removes duplicate frames in your source footage, replacing
    /// them with newly interpolated frames.」
    /// <https://www.topazlabs.com/learn/how-to-use-frame-interpolation-in-topaz-video></summary>
    public const string TopazSceneDetectionAndDuplicateFrames =
        "Scene Detection 防切点插值伪影;Duplicate Frames 开关先删重复帧再插值";

    /// <summary>[仅建议] VSGAN 对"有重复帧的镜头"的做法是**该镜头用更高的插值倍率**来补偿:
    /// 「Normally, frames which are duplicated can create a stuttering visual effect and to mitigate that,
    /// a higher interpolation factor is used on scenes which have a duplicated frames to compensate.」
    /// <https://github.com/styler00dollar/VSGAN-tensorrt-docker> —— 本工程用的是**全局单一倍率**;
    /// 改成"按镜头变倍率"会改变输出的帧数分布(= 时长/音画对齐的根基)⇒ 只建议。</summary>
    public const string VariableInterpFactorPerSceneIsUsedByVsgan =
        "有重复帧的镜头用更高插值倍率补偿(本工程为全局单一倍率)";

    // ============ ⑩ 模型选择:官方用途分类(可再分发 + 许可已核) ============

    /// <summary>[已接线·口径] 官方把 `realesr-animevideov3` 归在 **"For Animation Videos"** 且描述为
    /// "Anime video model with XS size",注明「This model can also be used for X1, X2, X3」。
    /// <https://github.com/xinntao/Real-ESRGAN/blob/master/docs/anime_video_model.md>
    /// 【与本工程实测的关系】本工程实测 animevideov3 是动漫视频里**最快也最锐**的一档
    /// (2x 0.26 / 4x 0.297 秒/帧),与"官方专为动漫视频训练的小模型"一致。
    /// 【一处**冲突**必须如实记下】官方文档说它支持 X1,但本工程真机实测 **1x 输出全黑**
    /// (见 <see cref="PipelineOrderPlan.UpscaleRates"/> 的 1x 出处)—— 所以"文档支持 ≠ 本机可用",
    /// 1x 全黑护栏必须保留。【不确定度】未核实黑帧是权重缺失、还是 ncnn 转换/本机驱动所致。</summary>
    public const string OfficialAnimeVideoModel = "realesr-animevideov3";

    /// <summary>[仅建议·口径] 官方把 `RealESRGAN_x4plus_anime_6B`(= 本工程的 `realesrgan-x4plus-anime`)
    /// 归在 **"For Anime Images / Illustrations"**:「Optimized for anime images; 6 RRDB blocks (smaller network)」。
    /// <https://github.com/xinntao/Real-ESRGAN/blob/master/docs/model_zoo.md>
    /// 社区选型表把同一条讲得更直白:x4plus_anime_6B「Slowest processing time ... Best For: **Static anime images
    /// or short video clips**」,而 animevideov3「Optimized for **temporal consistency** across video frames」。
    /// ⇒ **动漫长视频应选 animevideov3;x4plus-anime 是"插画/静态图"那一档**。
    /// 这与本工程实测吻合(4x:x4plus-anime 3.85 秒/帧、比 animevideov3 慢 13 倍且更"涂抹")。</summary>
    public const string OfficialAnimeImageModel = "RealESRGAN_x4plus_anime_6B";

    /// <summary>[仅建议·口径] 官方把 `RealESRGAN_x4plus` 归在 **"For General Images"**:「X4 model for general images」;
    /// 社区选型表说它是「General-purpose model optimized for natural images and **live-action content**」
    /// 且「Fastest processing time among the three, Memory Usage Lowest」。
    /// 【重要冲突】**"最快"这条在本工程的 ncnn 形态下不成立**:真机实测 `realesrgan-x4plus` 4x
    /// = **14.8~15.5 秒/帧**,是 animevideov3 的 **51 倍**(那一档只有 12 帧样本,区间最宽)。
    /// ⇒ 实拍素材若要 x4plus 的画质,必须**先接受几十倍的耗时**;面向长片时更现实的是
    /// `realesr-general-x4v3`(实测 0.458 秒/帧),代价是官方明说它
    /// 「not too strong deblur and denoise capacity」(去模糊/去噪能力偏弱)。**这条只写建议不改默认**。</summary>
    public const string OfficialGeneralImageModel = "RealESRGAN_x4plus";

    /// <summary>[仅建议·口径] 官方对 `realesr-general-x4v3` 的描述(同一 model_zoo 出处):
    /// 「A tiny small model (consume much fewer GPU memory and time); not too strong deblur and denoise capacity」。
    /// 本工程实测 4x = 0.455~0.461 秒/帧 —— 实拍素材的"速度档"。</summary>
    public const string OfficialTinyGeneralModel = "realesr-general-x4v3";

    // ============ ⑪ 许可(可商用 / 可再分发) ============

    /// <summary>[已核] Real-ESRGAN(代码 + 官方权重)许可 = **BSD 3-Clause**,Copyright (c) 2021 Xintao Wang。
    /// 条款原文要点:「Redistribution and use in source and binary forms, with or without modification,
    /// are permitted provided that the following conditions are met: ... Redistributions in binary form must
    /// reproduce the above copyright notice ...」⇒ **允许商用与再分发**(含把权重打进应用),只需随附版权与许可。
    /// <https://github.com/xinntao/Real-ESRGAN/blob/master/LICENSE></summary>
    public const string RealEsrganLicense = "BSD-3-Clause(允许商用/再分发,须随附版权与许可)";

    /// <summary>[已核] Real-ESRGAN-ncnn-vulkan 许可 = **MIT**(另附 realsr-ncnn-vulkan 的 MIT 声明,
    /// Copyright (c) 2019 nihui)。「Permission is hereby granted, free of charge, ... to deal in the Software
    /// without restriction, including without limitation the rights to use, copy, modify, merge, publish,
    /// distribute, sublicense, and/or sell copies」⇒ 允许商用与再分发。
    /// <https://github.com/xinntao/Real-ESRGAN-ncnn-vulkan/blob/master/LICENSE></summary>
    public const string RealEsrganNcnnVulkanLicense = "MIT(允许商用/再分发)";

    /// <summary>[已核] RIFE 的 ncnn 实现 `nihui/rife-ncnn-vulkan` 的 README 未单列 LICENSE 段落;
    /// 本工程使用其二进制分发,许可情况**未在本次研究中确认到逐字许可文本** ⇒ 记为**待核实**,不当成已确认。
    /// <https://github.com/nihui/rife-ncnn-vulkan></summary>
    public const string RifeNcnnVulkanLicense = "【待核实】本次未取得逐字许可文本";

    /// <summary>[已核] waifu2x-ncnn-vulkan(`nihui`)README 同样未在正文单列许可段 ⇒ 同为**待核实**。
    /// <https://github.com/nihui/waifu2x-ncnn-vulkan></summary>
    public const string Waifu2xNcnnVulkanLicense = "【待核实】本次未取得逐字许可文本";

    /// <summary>[未核实·如实记录]**"是否还有许可允许商用/再分发、而我们尚未纳入的模型"这一问,本次没能给出候选**。
    /// 已确认的是:**我们现在用的这一族本身就已满足"可商用 + 可再分发"**(见上面两条许可),
    /// 所以"可再分发的选择"并不缺。另被考虑的常见动漫视频超分替代是 Real-CUGAN(bilibili/ailab),
    /// 但本次**未能取到它的逐字许可文本**(GitHub API 对该仓库返回 403、本机到 github.com 的直连也不稳),
    /// 因此**不做任何推荐、也不写任何数字** —— 按本工程口径,"没出处"就不能进参数表。
    /// 【下次要做的话】把 Real-CUGAN / Real-CUGAN-ncnn-vulkan 的 LICENSE 与权重许可靠下来再谈。</summary>
    public const string NoVerifiedAdditionalRedistributableModel =
        "未取得 Real-CUGAN 等替代模型的逐字许可文本 ⇒ 不做推荐;现有模型(BSD-3/MIT)已可再分发";

    // ============ ⑤ 显存 → 分块/批次:外部**没有**可用表 ============

    /// <summary>[仅建议·无外部表]**"按显存给批次大小"这件事,外部没有公开经验值可当外参。**
    /// 2026-09-14 核对结论:
    ///   · Real-ESRGAN / Real-ESRGAN-ncnn-vulkan 的 README **没有任何"显存 → tile"的表**;
    ///     `-t 0`(auto)由**源码**按显存预算取 200/100/64/32(见 <see cref="UpstreamTileSizeAuto"/> 的引用),
    ///     但那是"引擎自己的兜底",不是"推荐值";
    ///   · 唯一公开的**量化**表是 waifu2x-ncnn-vulkan README 的 waifu2x-caffe 对比表:
    ///     cunet 模型下 `块 400/200/100` 分别约 `2430/638/197 MB` 显存(输入 400×400 那行)——
    ///     它只说明**方向**(块越小越省显存),且是 waifu2x 的表,不是 Real-ESRGAN 的。
    ///     <https://github.com/nihui/waifu2x-ncnn-vulkan#speed-comparison-with-waifu2x-caffe-cui>
    ///   · 也没有任何项目公开说"tile 一旦装得下,吞吐与 tile 大小无关"(正反两面都查不到出处)。
    /// ⇒ 因此本工程**只能**用真机实测当外参,已有的两条都写在代码里、不另造:
    ///     `RenderPolicy.OnnxTileSize`(实测 8GB 上 1024 最优、1280 起崩塌 13.7~20 倍、1536/1920 OOM)、
    ///     `RenderPolicy.VideoTileSize` + `RenderPolicy.PlanVideoBatches`(批次按"内存档 × 性能档"取较低者)。
    ///     本次**不据网络资料改动任何一个分块/批次数字**。</summary>
    public const string NoPublicVramToBatchTable =
        "外部无公开的\"显存 → tile/批次\"经验表;唯一量化表是 waifu2x README(块越小越省显存,方向性)";

    // ============ ⑦ 上游已知缺陷(与本工程既有护栏对应) ============


    /// <summary>[已核] 上游 README 的 TODO 列表里**官方承认**存在黑帧缺陷:
    /// 「- [ ] Bug: Some PCs will output black images」<https://github.com/xinntao/Real-ESRGAN-ncnn-vulkan>
    /// ⇒ 本工程的"1x 全黑护栏 / 黑帧自检"不是多余的防御,而是对上游客观缺陷的补偿。
    /// 【重要的一点否定结论】上游 README **没有**任何"`proc>1` 会产生坏帧/黑帧"的警告;
    /// 也就是说"`-j` 计算线程一多就出黑帧"**只有本工程的真机实测**支持
    /// (见 <see cref="ProcThreadsAreClampedToComputeQueues"/> 的机理),不是上游已知问题。
    /// 这条差别必须如实保留:它解释了为什么我们**不**照抄上游 README 的 `-j 1:2:2`。</summary>
    public const string UpstreamKnownBlackImageBug = "Bug: Some PCs will output black images(上游 README TODO 原文)";
}
