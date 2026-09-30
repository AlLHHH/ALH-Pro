namespace AlhPro.Core;

/// <summary>【2026-09-30 · t64】视频编码这一侧的**开跑前**与**失败时**判据(纯逻辑,可单测)。
///
/// 【为什么抽到 Core】2026-09-27 15:13 用户诊断包 `ALHPro_Diag_20260927_1527`(i5-1035G1 核显 + MX330)
/// 暴露了四件事,原先是散在 `VideoService` 的 catch 里、写死的测试命令里、以及"只留最后 500 字符"的
/// 截断里 —— 这类判据藏在 UI 工程里就等于**没有单测**(编译不进 AlhPro.Tests,只能靠读源码):
///   A 命令失败时只留了 stderr 的**尾部 500 字符**(`tail[^500..]`),截图证据里那段文本以
///     `ecc0] [enc:h264_qsv @ ...]` 开头 —— 一是那一行本身被切掉半个(指针标识符只剩尾巴),
///     二是**它前面的整段"现场"全没了**:ffmpeg 开头会打输入流(`mjpeg 1920x3416, 471.85 fps`)
///     与流映射(`Stream #0:0 -> #0:0 (mjpeg -> h264_qsv)`),那正是"这次拿什么尺寸、什么帧率、
///     喂给哪个编码器"的原始记录;日志里也**没有**本次编码的完整命令,只能靠"参数详情"那行去猜。
///   B 开跑前的探测写死 `testsrc=size=1280x720:rate=30` ⇒ 探的是"这台机器能不能编 720p30",
///     而真实任务要问的是"这次这个 **输出尺寸 + 标称帧率** 能不能编" —— 两者不等价,
///     差集只能靠整片重跑试出来(那一次超分 4x 已经跑完 **1425 帧 / 49.5 分钟**才在编码这步失败)。
///   C `h264_qsv` **3 次打不开编码器**(ffmpeg 原话 `Could not open encoder before EOF` / `Invalid argument`),
///     而同一台机器 `hevc_qsv` 就在可用列表里 —— 旧逻辑只会在**同一个**编码器上重试 3 次再报错,
///     从不换一个(同机 NVENC 探测不到,可用硬编只有 `[h264_qsv, hevc_qsv]`)。
///   D 那条任务的标称帧率是 **471.85 fps**(源 ~30fps × 补帧 16x),日志里写了、
///     但**没有**任何面向用户的提醒(超分/补帧/编码一路照跑)。
///
/// 【口径纪律】这里只放**判据与文案**(纯函数);`VideoService` 只做接线(起进程、写日志、换参数)。
/// 每个判据都有一条"能因真实行为错误而变红"的单测(见 `VideoEncodeGuardTests`)。
/// 「视频不落 CPU」这条策略的文案仍在 <see cref="CpuFallbackPolicy"/>(同一族判据),不在这里重复。</summary>
public static class VideoEncodeGuard
{
    // ════════════════════════════════════════════════════════════════════════════
    // A. 命令失败:头 + 尾都要留(真因几乎总在开头几行)
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>失败文本里保留的**头部**行数(口径来源:1527 那次真因在 stderr 的第 1~5 行)。</summary>
    public const int FailureHeadLines = 5;

    /// <summary>失败文本里保留的**头部**字符上限。</summary>
    public const int FailureHeadChars = 400;

    /// <summary>失败文本里保留的**尾部**字符上限(与旧口径一致:尾部留 500 字符,
    /// 因为 ffmpeg 的 `frame= ... Conversion failed!` 总在最后)。</summary>
    public const int FailureTailChars = 500;

    /// <summary>把一次进程失败的输出整理成"头 + 尾"都有的诊断文本。
    /// 【头是什么】ffmpeg 打印的**开头几行** = 这次任务的现场(输入流/尺寸/帧率、流映射、滤镜图),
    /// 也就是"它到底在拿什么参数编"的原始记录。1527 那次在 UI 工程里被 `tail[^500..]` 截过之后,
    /// 这些行**全部**丢失,剩下的只有最后一截**从半个标识符开始**的尾巴 ⇒ 排查时连"哪个尺寸/帧率/
    /// 哪个编码器"都要靠猜(这正是 A 要修的东西)。
    /// 【尾是什么】错误原话与收尾句(`Could not open encoder before EOF` / `Conversion failed!`)——
    /// 旧口径保住的就是这一半,继续保住。
    /// 【超短输出不切】总长度 ≤ 头+尾上限时**原样返回**(不留"省略"标记、不丢字符)。</summary>
    /// <param name="stage">阶段名(如"编码""编码预检"),写进第一行,便于在日志里定位。</param>
    /// <param name="exitCode">进程退出码(ffmpeg 常见 -40、nvenc 的 -542398533 等)。</param>
    /// <param name="errorOutput">stderr(首选,ffmpeg 的错误都写这里)。</param>
    /// <param name="stdout">stdout(有些引擎把错误写到 stdout;与 stderr 拼接,不丢)。</param>
    public static string DescribeProcessFailure(string stage, int exitCode, string? errorOutput, string? stdout = null)
    {
        string full = JoinOutput(errorOutput, stdout);
        string head = $"[{stage}] 命令失败 (exit {exitCode})";
        if (full.Length == 0) return head + ":(进程没有任何输出)";
        if (full.Length <= FailureHeadChars + FailureTailChars)
            return head + $":\n—— 完整输出({full.Length} 字符,未截断)——\n" + full;
        string h = HeadOf(full);
        string t = TailOf(full);
        int omitted = full.Length - h.Length - t.Length;
        return head
             + $":\n—— 【头部 {FailureHeadLines} 行 / ≤{FailureHeadChars} 字符】(真因通常在这几行)——\n" + h
             + (omitted > 0 ? $"\n—— (中间省略 {omitted} 字符) ——\n" : "\n")
             + $"—— 【尾部 {FailureTailChars} 字符】——\n" + t;
    }

    /// <summary>取输出的**开头**:前 <paramref name="maxLines"/> 个非空行,且总字符不超过 <paramref name="maxChars"/>。
    /// 先按字符截断再补一行"(本行被截断…)标记 —— 单行超长(引擎打一行几万字符的长 JSON)时不能把整个头部吃掉。</summary>
    public static string HeadOf(string? text, int maxLines = FailureHeadLines, int maxChars = FailureHeadChars)
    {
        var lines = SplitLines(text);
        var taken = new List<string>();
        int used = 0;
        foreach (var line in lines)
        {
            if (taken.Count >= Math.Max(1, maxLines) || used >= maxChars) break;
            int room = maxChars - used;
            if (line.Length <= room) { taken.Add(line); used += line.Length + 1; }
            else { taken.Add(line.Substring(0, Math.Max(0, room)) + " …(本行被截断)"); used = maxChars; }
        }
        return string.Join("\n", taken);
    }

    /// <summary>取输出的**结尾** <paramref name="maxChars"/> 个字符(与旧口径的尾部保留一致)。
    /// 起始位置尽量对齐到行首(否则第一行又是一个"半个标识符",正是 1527 那张截图的样子)。</summary>
    public static string TailOf(string? text, int maxChars = FailureTailChars)
    {
        if (string.IsNullOrEmpty(text)) return "";
        string t = Normalize(text);
        if (t.Length <= maxChars) return t;
        string cut = t[^maxChars..];
        int nl = cut.IndexOf('\n');
        // 只在下游还有大半内容时才对齐到换行(否则把尾部又切短了一大截,得不偿失)
        if (nl >= 0 && nl < cut.Length - 80) cut = cut[(nl + 1)..];
        return cut;
    }

    // ════════════════════════════════════════════════════════════════════════════
    // B. 参数级预检:计划阶段的**真实输出尺寸 + 标称帧率**(预检与"帧数台账"同一个判据)
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>本任务**计划**的输出尺寸(编码器真正要吃的那组尺寸)。
    /// 【口径】① 用户填了自定义输出分辨率 ⇒ 就是它(`ResizeImageTo` 按原值缩,取整会与真实帧不符);
    /// ② 没超分 ⇒ 源尺寸按 `trunc(iw/2)*2`(与拆帧滤镜 `scale=trunc(iw/2)*2:trunc(ih/2)*2` 同口径);
    /// ③ 超分 ⇒ 源尺寸 × 目标倍数(引擎倍数再缩回目标倍数,净效果就是目标倍数),再取偶。
    /// 【为什么用取偶而不是原值】中间帧是 JPG、编码是 yuv420p 系 ⇒ 奇数尺寸在多数硬编上会直接失败;
    /// 这条也顺带保证"预检尺寸"与真实帧尺寸**同奇偶**。
    /// 【诚实边界】超分那一路是**估计值**(引擎输出再缩回时的取整可能差 1 像素)——预检的用途是
    /// "这组尺寸+帧率能不能编",差 1 像素不影响该结论;日志里按"计划输出尺寸"标注,不写成实测。</summary>
    public static (int Width, int Height) PlanOutputSize(int srcW, int srcH, double scale, bool doUpscale,
        int? outWidth = null, int? outHeight = null)
    {
        if (outWidth is > 0 && outHeight is > 0) return (outWidth.Value, outHeight.Value);
        int w = Math.Max(2, srcW);
        int h = Math.Max(2, srcH);
        if (doUpscale && double.IsFinite(scale) && scale > 0)
        {
            w = (int)Math.Round(w * scale);
            h = (int)Math.Round(h * scale);
        }
        return (EvenFloor(w), EvenFloor(h));
    }

    /// <summary>本任务**计划**的标称输出帧率(【预计】那一组数)。
    /// 【两处必须同判据】① 拆帧**之前**的编码预检(那时去重还没跑,只能给上界:去重只会减少内容帧数,
    /// 标称帧率不会再变高 ⇒ 传 <c>effectiveFps = inFps</c>);② 下面「本次处理帧数台账」那行
    /// (去重后 effectiveFps 已定稿)。两处都调本函数 ⇒ 判据只有一份,改公式两处一起动
    /// (测试钉住:`The_preflight_and_the_frame_census_share_one_nominal_fps_judgement`)。
    /// 【公式来源】`VideoService` 里原本的 inline 写法逐字搬来,未改口径:
    /// 用户指定帧率优先;否则 <c>(fpsMode == 1 ? 内容帧率 : max(内容帧率, 输入帧率)) × 补帧倍率</c>。
    /// 平滑时间轴(flatten)那一档由调用方在拿到 <c>flatPlan.TargetFps</c> 后覆盖 —— 那条只在
    /// "已经决定填平"之后才成立,拆帧前无法预知,故不放进本判据(调用侧注释写明)。</summary>
    public static double PlanNominalOutputFps(double? targetFps, int fpsMode, double effectiveFps, double inFps,
        int interpScale, bool frameInterp)
    {
        double inF = inFps > 0 && double.IsFinite(inFps) ? inFps : 30.0;
        if (!frameInterp) return inF;                       // 不补帧:输出帧率就是输入帧率
        if (targetFps is > 0 && double.IsFinite(targetFps.Value)) return targetFps.Value;
        double eff = effectiveFps > 0 && double.IsFinite(effectiveFps) ? effectiveFps : inF;
        int k = interpScale >= 1 ? interpScale : 1;
        return (fpsMode == 1 ? eff : Math.Max(eff, inF)) * k;
    }

    /// <summary>预检命令(1 帧真实参数)。**必须**由本函数生成:日志里记的命令与实际跑的命令逐字一致
    /// (t64 的 A 项要求"日志里出现本次编码的完整命令",预检这条同理)。
    /// 【为什么是 lavfi testsrc + 真实编码参数】与既有编码器探测同精神(见 `EnsureHwProbeAsync`),
    /// 但尺寸/帧率换成本任务**计划**的那组 —— 这就是 B 与既有探测的唯一区别。
    /// 【尺寸原样用】这里**不取偶**:计划尺寸是奇数时,真实编码同样会失败,而预检的用途正是提前发现它;
    /// 偷偷改成偶数就掩盖了那颗雷(取偶只发生在 <see cref="PlanOutputSize"/> 的源尺寸那一路)。
    /// 【`-hide_banner` 不是装饰】ffmpeg 的 version/built/configuration 三行横幅有 ~600 字符,
    /// 会把 A 的"头部预算"(5 行 / 400 字符)整个吃掉 ⇒ 头部只剩横幅,看不到"输入流尺寸/帧率 + 流映射"
    /// 这些真正的现场。实测(本机 ffmpeg):加 -hide_banner 后失败输出的第一行就是 `Input #0 …`。</summary>
    public static string BuildPreflightCommand(int width, int height, double fps, string encoderArgs, string outputPath,
        int frames = 1)
        => $"-y -hide_banner -f lavfi -i \"testsrc=size={Math.Max(2, width)}x{Math.Max(2, height)}"
         + $":rate={Fmt(Math.Max(0.01, fps))}\" -frames:v {Math.Max(1, frames)} {encoderArgs} \"{outputPath}\"";

    /// <summary>预检通过时的日志行(尺寸 + 帧率 + 编码器都写清楚,便于与"真实任务"对照)。</summary>
    public static string DescribePreflightPassed(string encoder, int width, int height, double fps)
        => $"编码预检通过:{encoder} 能编 {width}×{height} @ {Fmt(fps)} fps(1 帧真实参数;"
         + "之后成功路径的参数与耗时口径均未改动)";

    /// <summary>预检失败的**用户可见**报错(必须让他知道"现在是开跑前停下的,不是跑完几十分钟才失败")。
    /// 【文案红线】不许提"去设置里选 CPU 软编" —— 那个选项不存在(<see cref="CpuFallbackPolicy"/> 的 B8 口径)。</summary>
    public static string DescribePreflightFailure(string encoder, int width, int height, double fps,
        string? detail = null, string? triedEncoders = null)
        => $"编码预检失败:{encoder} 在 {width}×{height} @ {Fmt(fps)} fps 下连 1 帧都编不出来 —— "
         + "**已在开始处理之前停下**(拆帧/补帧/超分都还没开始,不会白跑几十分钟)。\n"
         + "本次用的是这次任务真实要用的输出尺寸 + 标称帧率(不是以往那个写死的 720p 探测)。\n"
         + (string.IsNullOrWhiteSpace(triedEncoders) ? "" : $"已试过的编码器:{triedEncoders}(本机再没有别的实测可用硬编可换了)。\n")
         + "怎么办:① 更新显卡驱动(或重装一次)后重试;② 把补帧倍率/指定帧率降到 240 fps 以内;"
         + "③ 换一个输出分辨率再试。\n"
         + "说明:视频处理**不提供** CPU 软编选项(实测慢 7 倍以上)。\n"
         + "最后一次原因:" + (string.IsNullOrWhiteSpace(detail) ? "(未给出原因)" : detail.Trim());

    // ════════════════════════════════════════════════════════════════════════════
    // C. "打不开编码器" ≠ "跑起来后瞬时失败" —— 前者换一个可用硬编,后者保持重试
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>一次编码失败的形态。</summary>
    public enum EncodeFailureKind
    {
        /// <summary>跑起来之后的失败(**含认不出来的失败**):保持既有"同一编码器重试 3 次"的行为。
        /// 【为什么"认不出"也算这一档】5060 那台就是 `exit -542398533` 这种没头没脑的瞬时失败,
        /// 而它的解**正是**重试(上次任务还是 16.7 fps,下一次就编不了了)⇒ 默认必须是重试,不是换编码器。</summary>
        Transient,

        /// <summary>编码器**打不开/初始化失败**:同一个编码器再试 N 次也是同样结果 ⇒ 该换一个可用硬编
        /// (1527 那台 `h264_qsv` 3 次都是这个形态,而 `hevc_qsv` 就在可用列表里)。</summary>
        EncoderInit,
    }

    /// <summary>"打不开编码器"的**文本身份**(ffmpeg / 各厂商 SDK 的原话,全部实测或来自厂商文档)。
    /// 【为什么不含 `Invalid argument`】1527 的输出里确实有 `Invalid argument`,但它同样出现在
    /// 与编码器初始化**无关**的失败里(例如超分错帧那类)⇒ 单凭它会把"瞬时失败"误判成"打不开",
    /// 于是本该重试的任务被直接换编码器/报错。判据只认下面这些**指向编码器打开动作**的原话。</summary>
    public static readonly string[] EncoderInitMarkers =
    {
        "Could not open encoder",                       // 1527 真机原话(h264_qsv 打不开)
        "Error while opening encoder",                  // ffmpeg 经典输出流初始化失败
        "Error initializing an internal MFX session",   // QSV:MFX 会话建不起来
        "Error creating a MFX session",                 // QSV(旧版措辞)
        "Cannot load nvcuda",                           // NVENC:驱动/运行库缺失
        "Cannot load nvEncodeAPI",                      // NVENC:API 库缺失
        "nvenc API version",                            // NVENC:驱动过旧(Required/Found 那句)
        "minimum required Nvidia driver",               // NVENC:驱动过旧(建议句)
        "OpenEncodeSessionEx failed",                   // NVENC:会话打开失败
        "No capable devices found",                     // NVENC:没有能干这活的设备
        "device creation failed",                       // 通用:设备创建失败
        "Error creating an encoder context",            // 通用:编码器上下文创建失败
    };

    /// <summary>把 ffmpeg 输出(或异常文本)分类成"打不开"或"瞬时失败"。</summary>
    public static EncodeFailureKind ClassifyEncodeFailure(string? output)
        => IsEncoderInitFailure(output) ? EncodeFailureKind.EncoderInit : EncodeFailureKind.Transient;

    /// <summary>文本是否属于"编码器打不开/初始化失败"(见 <see cref="EncoderInitMarkers"/>)。</summary>
    public static bool IsEncoderInitFailure(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return false;
        foreach (var marker in EncoderInitMarkers)
            if (output.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>是不是硬件编码器(nvenc / amf / qsv)。候选链**只**收这一类:
    /// 视频侧「不落 CPU」是硬约定,`libx264`/`libx265` 永不入链(它们只可能在"本机一个硬编都没有"时出现,
    /// 而那种情况在开跑前的闸门就被拦掉了)。</summary>
    public static bool IsHardwareEncoder(string? encoder)
    {
        if (string.IsNullOrWhiteSpace(encoder)) return false;
        return encoder.Contains("nvenc", StringComparison.OrdinalIgnoreCase)
            || encoder.Contains("amf", StringComparison.OrdinalIgnoreCase)
            || encoder.Contains("qsv", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>H.264(各家硬编都算)的**实测尺寸上限**:任一边超过它就编不出来。
    /// 【实测出处 · 2026-09-29 用户诊断包】AMD RX 9070 XT + `h264_amf`:2880×2160 那趟一次通过,
    /// 而 2880×2160 **×2 超分 = 5760×4320** 时引擎直接 `Task finished with error: Invalid argument`
    /// (exit -558323010)—— 这是 H.264 的硬上限(RDNA 的 VCN 上 H.264 最大 4096,HEVC 可到 8192),
    /// **不是参数写错**:同一台机器、同一个编码器,≤4096 完全正常。
    /// ⇒ 计划输出任一边超过它时,**只能**用 HEVC(同厂商的 hevc_*);继续用 h264_* 是确定失败。</summary>
    public const int H264MaxDimension = 4096;

    /// <summary>计划输出尺寸是否超过 H.264 的实测上限(见 <see cref="H264MaxDimension"/>)。
    /// 任一边 ≤0(尺寸未知)时返回 false —— 不许在尺寸未知时乱改编码器。</summary>
    public static bool ExceedsH264Limit(int width, int height)
        => width > H264MaxDimension || height > H264MaxDimension;

    /// <summary>【2026-09-30 修 · 音画不同步】由「源视频流的 start_time」算出给**音频输入**的 `-itsoffset`。
    /// 背景:画面来自 JPG 序列(PTS 从 0 开始),而音频保留源素材自己的起始时间
    /// ⇒ 源视频流的 start_time ≠ 0 时(剪映/手机导出很常见),两者就差这一个 start_time
    /// ⇒ **固定偏移的音画不同步**,而软件原有自检只比「时长」(31.04 = 31.04)看不见它。
    /// 用 `-itsoffset`(输入级参数)而不用 `asetpts`/`atrim`:**滤镜与 `-c:a copy` 互斥**
    /// (ffmpeg 会直接报错),会把“能原样复制音轨”的机器逼成重编码。
    /// 1ms 以内视为 0(返回空串):常见 MP4 的 start_time 就是 0,这条改动对它们是**零操作**。</summary>
    public static string AudioOffsetArgs(double videoStartSeconds)
    {
        if (!double.IsFinite(videoStartSeconds) || Math.Abs(videoStartSeconds) < 0.001) return "";
        return " -itsoffset -" + videoStartSeconds.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>换编码器的**候选链**(按顺序;只含本机**实测可用**的硬编,且不含 <paramref name="current"/>)。
    /// 【顺序判据(1527 那台的真实需求)】
    ///   ① **同厂商另一档**:`h264_qsv` 打不开 ⇒ 先试 `hevc_qsv`(同一块核显的另一套编码器,
    ///      驱动/会话问题常常只卡在其中一套上;这也是 1527 那台最可能救回来的那一档)。
    ///   ② **其它厂商**:`codecPref == 2`(优先 H.265)时先把 hevc 档排前面,否则保持当前编码
    ///      (H.264 兼容性优先:老设备/老播放器都能放)。
    ///   ③ CPU 编码器永不入链。
    /// 【为什么不用"WorkingHwEncoders 的顺序"了事】那个顺序是按探测优先级(nvenc &gt; amf &gt; qsv)排的,
    /// 与"这次换谁最可能成"无关(跨厂商换 = 换设备,同厂商换 = 只换编码器,后者代价小得多)。</summary>
    public static IReadOnlyList<string> FallbackEncoderChain(string current, int codecPref,
        IReadOnlyList<string>? workingEncoders)
    {
        var chain = new List<string>();
        if (workingEncoders == null || workingEncoders.Count == 0) return chain;
        var hw = new List<string>();
        foreach (var e in workingEncoders)
            if (!string.IsNullOrWhiteSpace(e) && IsHardwareEncoder(e) && !hw.Contains(e)) hw.Add(e);
        string curFamily = FamilyOf(current);
        foreach (var family in FamiliesByDistance(curFamily))
            foreach (var enc in CodecOrder(codecPref, CodecOf(current)))
            {
                string candidate = enc + "_" + family;
                if (candidate.Equals(current, StringComparison.OrdinalIgnoreCase)) continue;
                if (hw.Contains(candidate) && !chain.Contains(candidate)) chain.Add(candidate);
            }
        return chain;
    }

    /// <summary>候选链里的第一个"还没试过"的硬编;没有则返回 null(调用方据此报"本机没有备选硬编")。</summary>
    public static string? NextEncoderCandidate(string current, int codecPref, IReadOnlyList<string>? workingEncoders,
        IReadOnlyCollection<string>? alreadyTried = null)
    {
        foreach (var c in FallbackEncoderChain(current, codecPref, workingEncoders))
        {
            bool tried = false;
            if (alreadyTried != null)
                foreach (var t in alreadyTried)
                    if (string.Equals(t, c, StringComparison.OrdinalIgnoreCase)) { tried = true; break; }
            if (!tried) return c;
        }
        return null;
    }

    /// <summary>换编码器时必须写进日志的一句(不许静默换:用户与排查者都要看得见换了谁、为什么换)。</summary>
    public static string DescribeEncoderSwitch(string from, string to, string? reason = null)
        => $"⚠ 硬件编码({from})打不开编码器(初始化失败)⇒ **换用 {to}**(不再拿 {from} 重试 —— "
         + "同一个编码器打不开,重试 N 次还是打不开;换本机另一个实测可用硬编才有意义)"
         + (string.IsNullOrWhiteSpace(reason) ? "" : $"\n原话:{reason.Trim()}");

    // ════════════════════════════════════════════════════════════════════════════
    // D. 标称帧率护栏
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>标称输出帧率的提醒阈值(240 fps)。【为什么是 240】输入侧本来就有 240 fps 的提醒
    /// (见 `VideoService` 里"视频帧率…超过 240"那条),输出侧一直没有;而 1527 那次输出标称
    /// **471.85 fps**(源 ~30fps × 补帧 16x)—— 超高帧率会让文件体积、编码器压力与播放器兼容性
    /// 一起变差,而这台机器上一次失败的任务正好就是唯一那条 471.85 fps 的。**只提醒、不改参数**。</summary>
    public const double NominalFpsWarnThreshold = 240.0;

    /// <summary>标称帧率是否越过阈值(非有限值一律不报警:NaN/Inf 是参数异常,由别的闸门管)。</summary>
    public static bool ExceedsNominalFpsGuard(double nominalFps)
        => double.IsFinite(nominalFps) && nominalFps > NominalFpsWarnThreshold;

    /// <summary>越阈值时要给用户看的提醒;**没越阈值返回 null**(调用方据此决定报不报,避免"永远为真"的恒报)。
    /// 【措辞纪律】只陈述"帧率是首要嫌疑、尚未坐实"——1527 那次真因被截断的日志切掉了,不许写成结论。
    /// 也不许在这里偷偷改参数:用户设的倍率照跑,只是提前把代价说清楚。</summary>
    public static string? NominalFpsGuardReason(double nominalFps)
    {
        if (!ExceedsNominalFpsGuard(nominalFps)) return null;
        return $"⚠ 输出标称帧率 {Fmt(nominalFps)} fps 超过 {NominalFpsWarnThreshold:0} fps:超高帧率会让成片体积、"
             + "编码器压力与播放器兼容性一起变差(2026-09-27 那次失败的任务标称 471.85 fps,真因尚未坐实,"
             + "但帧率是首要嫌疑)。建议把补帧倍率降到「源帧率 × 倍率 ≤ 240」这一档,或用「指定帧率」限制输出。"
             + "本次仍按你的设置照常产出(不会静默改参数),只是提前说明。";
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 内部小工具(纯函数)
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>截到偶数(向下):与拆帧滤镜 `scale=trunc(iw/2)*2:trunc(ih/2)*2` 同口径,
    /// 也是 yuv420p 系编码器的硬要求(奇数尺寸会直接失败)。</summary>
    private static int EvenFloor(int v) => v <= 2 ? 2 : v - (v & 1);

    private static string Fmt(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    private static List<string> SplitLines(string? text)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(text)) return list;
        foreach (var raw in Normalize(text).Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Trim().Length > 0) list.Add(line);
        }
        return list;
    }

    private static string JoinOutput(string? errorOutput, string? stdout)
    {
        string a = Normalize(errorOutput ?? "");
        string b = Normalize(stdout ?? "");
        if (a.Length == 0) return b;
        if (b.Length == 0 || a.Contains(b, StringComparison.Ordinal)) return a;
        return a + "\n" + b;
    }

    /// <summary>编码器名的厂商档(前缀:`h264` / `hevc`),认不出返回空串。</summary>
    private static string CodecOf(string? encoder)
    {
        if (string.IsNullOrWhiteSpace(encoder)) return "";
        int i = encoder.IndexOf('_');
        return i > 0 ? encoder[..i].ToLowerInvariant() : "";
    }

    /// <summary>编码器名的厂商实现(后缀:`nvenc` / `amf` / `qsv`),认不出返回空串。</summary>
    private static string FamilyOf(string? encoder)
    {
        if (string.IsNullOrWhiteSpace(encoder)) return "";
        int i = encoder.IndexOf('_');
        return i > 0 && i < encoder.Length - 1 ? encoder[(i + 1)..].ToLowerInvariant() : "";
    }

    /// <summary>厂商实现按"离当前这个有多远"排:自己那一族永远最前,其余按探测优先级(nvenc &gt; amf &gt; qsv)。</summary>
    private static IEnumerable<string> FamiliesByDistance(string currentFamily)
    {
        if (!string.IsNullOrEmpty(currentFamily)) yield return currentFamily;
        foreach (var f in new[] { "nvenc", "amf", "qsv" })
            if (!string.Equals(f, currentFamily, StringComparison.OrdinalIgnoreCase)) yield return f;
    }

    /// <summary>编码格式的尝试顺序:`codecPref == 2`(优先 H.265)时 hevc 在前,否则保持当前那个编码
    /// (老设备/老播放器都能放,也不改变用户当前选择的结果类型),另一个编码兜底。</summary>
    private static IEnumerable<string> CodecOrder(int codecPref, string currentCodec)
    {
        string first = codecPref == 2 ? "hevc"
            : currentCodec is "hevc" or "h264" ? currentCodec : "h264";
        string second = first == "hevc" ? "h264" : "hevc";
        yield return first;
        yield return second;
    }
}
