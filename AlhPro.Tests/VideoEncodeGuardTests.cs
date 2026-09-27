using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【2026-09-30 · t64】视频编码四件事的判据(纯逻辑 + 源码契约):
///   A 命令失败要留**头**+尾(1527 那次真因被 `tail[^500..]` 切掉了);
///   B 参数级预检用**本任务真实输出尺寸 + 标称帧率**(不是写死的 1280×720@30);
///   C "打不开编码器"与"跑起来后瞬时失败"分两条路(前者换本机另一个可用硬编);
///   D 标称帧率超过 240 fps 必须让用户看见。
/// 这里的判据都来自 2026-09-27 15:13 的真实故障(`ALHPro_Diag_20260927_1527`:i5-1035G1 核显 + MX330):
/// h264_qsv 3 次打不开编码器(ffmpeg 原话 `Could not open encoder before EOF` / `Invalid argument`),
/// 而超分 4x 已经跑完 1425 帧 / 49.5 分钟才失败;同机可用硬编只有 `[h264_qsv, hevc_qsv]`(NVENC 探测不到)。</summary>
public class VideoEncodeGuardTests
{
    // ═══════════════════════ A. 失败文本:头 + 尾 ═══════════════════════

    /// <summary>1527 那次的 stderr:**前半段是 ffmpeg 的"现场"**(输入流尺寸/帧率 + 流映射),
    /// 后半段才是错误原话。⚠ 诊断包里那段**以 `ecc0]` 开头**——那不是真开头,是 UI 工程 `tail[^500..]`
    /// 截断后的残片(那一行原本是 `[enc:h264_qsv @ 0000024a1431ecc0] Could not open encoder before EOF`,
    /// 而且它前面的整段"现场"被整块丢掉了)。本用例按**未被截断**的原始形态构造。</summary>
    private const string Real1527Stderr =
        "ffmpeg version N-126247-gb79d4c4c0a-20260823 Copyright (c) 2000-2026 the FFmpeg developers\n" +
        "Input #0, image2, from 'D:\\ALHProTemp\\imgup_video_18091f48\\frames_final\\frame_%06d.jpg':\n" +
        "  Duration: 00:00:03.02, start: 0.000000, bitrate: N/A\n" +
        "  Stream #0:0: Video: mjpeg (Baseline), yuvj420p(pc, bt470bg/unknown/unknown), 1920x3416, 471.85 fps, 471.85 tbr, 471.85 tbn\n" +
        "Input #1, mov,mp4,m4a,3gp,3g2,mj2, from 'D:\\Users\\x\\Desktop\\片段一.mp4':\n" +
        "  Metadata:\n" +
        "    major_brand     : isom\n" +
        "    encoder         : Lavf62.28.102\n" +
        "Stream mapping:\n" +
        "  Stream #0:0 -> #0:0 (mjpeg (native) -> h264 (h264_qsv))\n" +
        "  Stream #1:0 -> #0:1 (aac (native) -> aac (native))\n" +
        "[enc:h264_qsv @ 0000024a14235a00] Could not open encoder before EOF\n" +
        "[vost#0:0/h264_qsv @ 0000024a1431ecc0] Task finished with error: Invalid argument\n" +
        "[vost#0:0/h264_qsv @ 0000024a1431ecc0] Terminating thread with error: Invalid argument\n" +
        "[out#0/mp4 @ 0000024a142c2dc0] Nothing was written into output file, because at least one of its streams received no packets.\n" +
        "frame=    0 fps=0.0 q=0.0 Lsize=       0KiB time=00:00:00.00 bitrate=N/A speed=   0x elapsed=0:00:04.41    \n" +
        "Conversion failed!\n";

    /// <summary>★ A:头部保住"现场"(输入流的尺寸/帧率 + 流映射),尾部保住错误原话。
    /// 旧口径只留最后 500 字符 ⇒ 头部整段丢失(1527 那份诊断包里就是这种形态)。</summary>
    [Fact]
    public void The_failure_text_keeps_the_head_where_the_real_cause_is()
    {
        string text = AlhPro.Core.VideoEncodeGuard.DescribeProcessFailure("编码", -40, Real1527Stderr);
        Assert.Contains("Could not open encoder before EOF", text);   // 尾部:错误原话
        Assert.Contains("Conversion failed!", text);                  // 尾部:ffmpeg 的收尾句
        Assert.Contains("1920x3416, 471.85 fps", text);               // ★ 头部:这次拿什么尺寸/帧率在编
        Assert.Contains("Input #0, image2", text);                    // ★ 头部:输入是帧序列(现场)
        Assert.Contains("中间省略", text);                             // 中间省掉的是"流映射"这类次要行,且**明确标出**
        Assert.Contains("命令失败 (exit -40)", text);
        Assert.Contains("[编码]", text);                              // 阶段要写清(编码还是预检)

        // 对照组:旧口径(只留尾部 500 字符)在同样输入下**丢掉**了头部 —— 这就是 A 要修的东西
        string oldForm = Real1527Stderr.Trim();
        Assert.True(oldForm.Length > 500, "样本要比 500 字符长,否则对照不出'头部被切掉'");
        oldForm = oldForm[^500..];
        Assert.DoesNotContain("1920x3416, 471.85 fps", oldForm);
        Assert.DoesNotContain("Input #0, image2", oldForm);
        // 而且旧口径的尾巴是从半行开始的(1527 那份报告里就是 `ecc0] [enc:...`)
        Assert.False(oldForm.StartsWith("[enc:h264_qsv", StringComparison.Ordinal),
            "旧口径的尾部应当以半个标识符开头(这正是那张截图的样子)");
    }

    /// <summary>★ A:长输出时头尾都在,并且**明确标出中间被省略了多少**(不许假装是完整输出)。</summary>
    [Fact]
    public void A_long_failure_output_keeps_both_ends_and_says_what_was_dropped()
    {
        string head = "[h264_qsv @ 0x1] Could not open encoder before EOF";
        string tail = "Conversion failed!";
        string middle = string.Join("\n", Enumerable.Range(1, 200).Select(i => $"line {i:000} " + new string('x', 60)));
        string text = AlhPro.Core.VideoEncodeGuard.DescribeProcessFailure("编码", -40, head + "\n" + middle + "\n" + tail);
        Assert.Contains(head, text);
        Assert.Contains(tail, text);
        Assert.Contains("中间省略", text);
        Assert.Contains("头部 5 行", text);
        Assert.Contains("尾部 500 字符", text);
    }

    /// <summary>★ A:输出本来就很短时**原样给出**——不许截断、不许加"省略"标记(旧实现会把它切掉)。</summary>
    [Fact]
    public void A_short_failure_output_is_returned_whole()
    {
        string raw = "Unrecognized option 'x_alh_force_hw_encode_fail'.\nError splitting the argument list: Option not found";
        string text = AlhPro.Core.VideoEncodeGuard.DescribeProcessFailure("编码预检", 1, raw);
        Assert.Contains("Unrecognized option 'x_alh_force_hw_encode_fail'.", text);
        Assert.Contains("Error splitting the argument list: Option not found", text);
        Assert.Contains("未截断", text);
        Assert.DoesNotContain("中间省略", text);
        Assert.DoesNotContain("尾部 500 字符", text);
    }

    /// <summary>★ A:头部同时受"行数"和"字符数"两道限制 —— 引擎打一行几万字符的长 JSON 时,
    /// 不能把整个头部预算一次吃光(那样等于又回到了"只留一截"的老问题)。</summary>
    [Fact]
    public void The_head_is_bounded_by_both_lines_and_chars()
    {
        var manyLines = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"l{i}"));
        string headMany = AlhPro.Core.VideoEncodeGuard.HeadOf(manyLines);
        Assert.Equal(AlhPro.Core.VideoEncodeGuard.FailureHeadLines, headMany.Split('\n').Length);
        Assert.Contains("l5", headMany);
        Assert.DoesNotContain("l6", headMany);   // 第 6 行起不进头部(否则"头部"就没意义了)

        string oneHuge = "START" + new string('y', 5000) + "END";
        string headHuge = AlhPro.Core.VideoEncodeGuard.HeadOf(oneHuge);
        Assert.True(headHuge.Length <= AlhPro.Core.VideoEncodeGuard.FailureHeadChars + 20,
            "单行超长时必须按字符截断,不能整行吃下");
        Assert.Contains("START", headHuge);
        Assert.Contains("本行被截断", headHuge);
    }

    // ═══════════════════════ B. 参数级预检的"计划量" ═══════════════════════

    /// <summary>★ B:计划输出尺寸必须是**本任务**的那组 —— 1527 那次是源 480×854 × 4x = 1920×3416,
    /// 而旧探测永远问的是 1280×720(所以参数级失败探不出来)。</summary>
    [Fact]
    public void The_planned_output_size_follows_the_task_not_a_hardcoded_probe()
    {
        var (w, h) = AlhPro.Core.VideoEncodeGuard.PlanOutputSize(480, 854, 4.0, doUpscale: true);
        Assert.Equal(1920, w);
        Assert.Equal(3416, h);
        Assert.NotEqual((1280, 720), (w, h));      // ★ 与写死探测的唯一区别就在这里

        // 1x 修复档(不放大):输出 = 源尺寸(取偶,与拆帧滤镜 trunc(iw/2)*2 同口径)
        var (w1, h1) = AlhPro.Core.VideoEncodeGuard.PlanOutputSize(481, 855, 1.0, doUpscale: false);
        Assert.Equal((480, 854), (w1, h1));

        // 用户填了自定义输出分辨率:原值优先(取偶会与真实帧不符)
        var (wc, hc) = AlhPro.Core.VideoEncodeGuard.PlanOutputSize(480, 854, 4.0, true, 1440, 2560);
        Assert.Equal((1440, 2560), (wc, hc));
    }

    /// <summary>★ B:「帧数台账」那行给的 `【预计】480 fps` 与**拆帧前**的预检必须是**同一个判据**。
    /// 1527 那次:源 30fps、补帧 16x ⇒ 480 fps(去重只会减少内容帧数,所以拆帧前用 inFps 是上界)。</summary>
    [Fact]
    public void The_planned_nominal_fps_is_the_480_from_the_1527_ledger()
    {
        Assert.Equal(480.0, AlhPro.Core.VideoEncodeGuard.PlanNominalOutputFps(
            null, fpsMode: 0, effectiveFps: 30, inFps: 30, interpScale: 16, frameInterp: true), 3);
        // 用户指定帧率优先
        Assert.Equal(120.0, AlhPro.Core.VideoEncodeGuard.PlanNominalOutputFps(
            120, 0, 30, 30, 16, true), 3);
        // fpsMode=1(极致流畅):按**内容帧率**算
        Assert.Equal(64.0, AlhPro.Core.VideoEncodeGuard.PlanNominalOutputFps(
            null, 1, 4.0, 30, 16, true), 3);
        // 不补帧:输出帧率 = 输入帧率
        Assert.Equal(30.0, AlhPro.Core.VideoEncodeGuard.PlanNominalOutputFps(null, 0, 30, 30, 16, false), 3);
        // 输入帧率拿不到时兜底 30(与 VideoService 的 inFps 兜底同口径),不许算出 0 ⇒ 预检用 0 fps 毫无意义
        Assert.Equal(300.0, AlhPro.Core.VideoEncodeGuard.PlanNominalOutputFps(null, 0, 0, 0, 10, true), 3);
    }

    /// <summary>★ B:预检命令必须带上**真实尺寸 + 真实帧率 + 真实编码参数**,而且只编 1 帧。</summary>
    [Fact]
    public void The_preflight_command_carries_the_real_size_fps_and_encoder_args()
    {
        string enc = " -c:v h264_qsv -global_quality 22 -pix_fmt yuv420p";
        string cmd = AlhPro.Core.VideoEncodeGuard.BuildPreflightCommand(1920, 3416, 471.85, enc, @"C:\tmp\x.mp4");
        Assert.Contains("testsrc=size=1920x3416", cmd);
        Assert.Contains("rate=471.85", cmd);
        Assert.Contains("-c:v h264_qsv", cmd);
        Assert.Contains("-global_quality 22", cmd);
        Assert.Contains("-frames:v 1", cmd);
        Assert.DoesNotContain("1280x720", cmd);   // ★ 旧探测的写死尺寸
        Assert.DoesNotContain("rate=30", cmd);    // ★ 旧探测的写死帧率
    }

    /// <summary>★ B:预检失败的报错必须说清"是**开跑前**停下的"(用户最痛的是白跑 49.5 分钟),
    /// 并且不许提"去设置里选 CPU 软编"(那个选项不存在 —— B8 文案红线)。</summary>
    [Fact]
    public void The_preflight_failure_text_says_it_stopped_before_processing()
    {
        string msg = AlhPro.Core.VideoEncodeGuard.DescribePreflightFailure(
            "h264_qsv", 1920, 3416, 471.85, "Could not open encoder before EOF", "h264_qsv / hevc_qsv");
        Assert.Contains("1920×3416", msg);
        Assert.Contains("471.85", msg);
        Assert.Contains("开始处理之前", msg);
        Assert.Contains("拆帧", msg);                       // 说清"处理阶段还没开始"
        Assert.Contains("h264_qsv / hevc_qsv", msg);        // 换过谁也要写清
        Assert.Contains("不提供", msg);
        Assert.DoesNotContain("CPU 计算", msg);             // ★ 不许指向不存在的设置项(文案红线)
    }

    // ═══════════════════════ C. 打不开 ≠ 瞬时失败 ═══════════════════════

    /// <summary>★ C:分类判据必须按**真实原话**分对 —— 1527 那台是"打不开"(该换编码器),
    /// 5060 那台是"瞬时失败"(该重试)。</summary>
    [Fact]
    public void The_1527_init_failure_is_told_apart_from_the_5060_transient_one()
    {
        Assert.Equal(AlhPro.Core.VideoEncodeGuard.EncodeFailureKind.EncoderInit,
            AlhPro.Core.VideoEncodeGuard.ClassifyEncodeFailure(Real1527Stderr));
        Assert.Equal(AlhPro.Core.VideoEncodeGuard.EncodeFailureKind.EncoderInit,
            AlhPro.Core.VideoEncodeGuard.ClassifyEncodeFailure(
                "[h264_nvenc @ 0x1] Driver does not support the required nvenc API version. Required: 13.1 Found: 13.0"));
        Assert.Equal(AlhPro.Core.VideoEncodeGuard.EncodeFailureKind.EncoderInit,
            AlhPro.Core.VideoEncodeGuard.ClassifyEncodeFailure(
                "Error while opening encoder for output stream #0:0 - maybe incorrect parameters such as bit_rate, rate, width or height"));

        // 5060 那台:没头没脑的瞬时失败 ⇒ 必须仍然走"重试 3 次"那条老路(默认档)
        Assert.Equal(AlhPro.Core.VideoEncodeGuard.EncodeFailureKind.Transient,
            AlhPro.Core.VideoEncodeGuard.ClassifyEncodeFailure("命令失败 (exit -542398533):"));
        Assert.Equal(AlhPro.Core.VideoEncodeGuard.EncodeFailureKind.Transient,
            AlhPro.Core.VideoEncodeGuard.ClassifyEncodeFailure(""));
        Assert.Equal(AlhPro.Core.VideoEncodeGuard.EncodeFailureKind.Transient,
            AlhPro.Core.VideoEncodeGuard.ClassifyEncodeFailure(null));
        // ★ 单凭 `Invalid argument` **不算**打不开:它同样出现在与编码器初始化无关的失败里,
        //   误判会把本该重试的任务直接换编码器/报错(1527 的输出里两句话是同时出现的,所以以第一句为准)
        Assert.Equal(AlhPro.Core.VideoEncodeGuard.EncodeFailureKind.Transient,
            AlhPro.Core.VideoEncodeGuard.ClassifyEncodeFailure("[vost#0:0/x @ 0x1] Task finished with error: Invalid argument"));
    }

    /// <summary>★ C:候选链 —— 1527 那台(可用 `[h264_qsv, hevc_qsv]`)打不开 qsv 时要先试**同厂商的 hevc_qsv**;
    /// 只有 qsv 一个可用时链是空的(要能报"本机没有备选硬编");CPU 编码器永不入链。</summary>
    [Fact]
    public void The_candidate_chain_prefers_the_same_vendor_and_never_picks_cpu()
    {
        var machine1527 = new[] { "h264_qsv", "hevc_qsv" };
        var chain = AlhPro.Core.VideoEncodeGuard.FallbackEncoderChain("h264_qsv", 0, machine1527);
        Assert.Equal("hevc_qsv", chain[0]);
        Assert.Equal("hevc_qsv", AlhPro.Core.VideoEncodeGuard.NextEncoderCandidate("h264_qsv", 0, machine1527));
        Assert.DoesNotContain("h264_qsv", chain);                 // 打不开的那个不再进链(不拿它重试)

        // 换了之后还是不成 ⇒ 没有下一个了(不许死循环、也不许悄悄退回 CPU)
        Assert.Null(AlhPro.Core.VideoEncodeGuard.NextEncoderCandidate("h264_qsv", 0, machine1527,
            new[] { "h264_qsv", "hevc_qsv" }));
        // ★ 本机没有备选硬编(1527 那台的形态之二:只剩一个可用编码器)
        Assert.Empty(AlhPro.Core.VideoEncodeGuard.FallbackEncoderChain("h264_qsv", 0, new[] { "h264_qsv" }));
        Assert.Null(AlhPro.Core.VideoEncodeGuard.NextEncoderCandidate("h264_qsv", 0, new[] { "h264_qsv" }));
        Assert.Null(AlhPro.Core.VideoEncodeGuard.NextEncoderCandidate("h264_qsv", 0, Array.Empty<string>()));
        // ★ CPU 编码器永不入链(「视频不落 CPU」)
        Assert.Empty(AlhPro.Core.VideoEncodeGuard.FallbackEncoderChain("libx264", 0, new[] { "libx264", "libx265" }));
        Assert.Empty(AlhPro.Core.VideoEncodeGuard.FallbackEncoderChain("h264_qsv", 0, new[] { "h264_qsv", "libx264" }));
        Assert.False(AlhPro.Core.VideoEncodeGuard.IsHardwareEncoder("libx264"));

        // 双卡机(核显 qsv + 独显 nvenc 都可用):同厂商优先,其次按探测优先级 nvenc > amf > qsv
        var dual = new[] { "h264_qsv", "hevc_qsv", "h264_nvenc", "hevc_nvenc" };
        Assert.Equal("hevc_qsv", AlhPro.Core.VideoEncodeGuard.NextEncoderCandidate("h264_qsv", 0, dual));
        // H.265 优先(pref=2)时,换到别的厂商也先找 hevc 档
        Assert.Equal("hevc_nvenc", AlhPro.Core.VideoEncodeGuard.NextEncoderCandidate("hevc_qsv", 2, new[] { "hevc_qsv", "hevc_nvenc" }));
        Assert.Equal("h264_nvenc", AlhPro.Core.VideoEncodeGuard.NextEncoderCandidate("hevc_qsv", 0, new[] { "hevc_qsv", "h264_nvenc" }));

        // 换编码器必须写日志(不许静默换):文案要含"从谁换成谁 + 为什么"
        string log = AlhPro.Core.VideoEncodeGuard.DescribeEncoderSwitch("h264_qsv", "hevc_qsv", "Could not open encoder before EOF");
        Assert.Contains("h264_qsv", log);
        Assert.Contains("hevc_qsv", log);
        Assert.Contains("换用", log);
        Assert.Contains("初始化失败", log);
        Assert.Contains("Could not open encoder before EOF", log);
    }

    /// <summary>★ C:打不开且没有备选时的报错 —— 不许写成"连续 3 次失败"(那条路我们一次都没重试),
    /// 也不许指向已不存在的 CPU 选项。</summary>
    [Fact]
    public void The_init_failure_without_any_alternative_says_it_did_not_retry()
    {
        string msg = AlhPro.Core.CpuFallbackPolicy.DescribeHwEncodeInitFailure("h264_qsv", "h264_qsv / hevc_qsv",
            "Could not open encoder before EOF");
        Assert.Contains("打不开编码器", msg);
        Assert.Contains("h264_qsv / hevc_qsv", msg);
        Assert.Contains("没有别的实测可用硬编可换", msg);
        Assert.Contains("不退回 CPU 软编", msg);
        Assert.DoesNotContain("连续 3 次失败", msg);   // ★ 与"瞬时失败重试 3 次"那条严格分开
        Assert.DoesNotContain("CPU 计算", msg);
    }

    // ═══════════════════════ D. 标称帧率护栏 ═══════════════════════

    /// <summary>★ D:边界用例(=阈值不报、>阈值报),并且文案里要有实际帧率。</summary>
    [Fact]
    public void The_nominal_fps_guard_fires_exactly_above_the_threshold()
    {
        Assert.Equal(240.0, AlhPro.Core.VideoEncodeGuard.NominalFpsWarnThreshold);
        Assert.False(AlhPro.Core.VideoEncodeGuard.ExceedsNominalFpsGuard(240.0));
        Assert.Null(AlhPro.Core.VideoEncodeGuard.NominalFpsGuardReason(240.0));
        Assert.False(AlhPro.Core.VideoEncodeGuard.ExceedsNominalFpsGuard(120.0));
        Assert.False(AlhPro.Core.VideoEncodeGuard.ExceedsNominalFpsGuard(double.NaN));
        Assert.False(AlhPro.Core.VideoEncodeGuard.ExceedsNominalFpsGuard(0));

        Assert.True(AlhPro.Core.VideoEncodeGuard.ExceedsNominalFpsGuard(240.01));
        string? warn = AlhPro.Core.VideoEncodeGuard.NominalFpsGuardReason(471.85);
        Assert.NotNull(warn);
        Assert.Contains("471.85", warn!);                 // ★ 日志/界面必须写明实际标称帧率
        Assert.Contains("240", warn!);
        Assert.Contains("仍按你的设置", warn!);            // 只提醒,不许静默改参数
        Assert.DoesNotContain("必须改成", warn!);
    }

    // ═══════════════════════ 接线契约(能因真实行为错误而变红) ═══════════════════════
    //
    // 这一组断言的对象是 UI 工程(VideoService/MainPage):AlhPro.Tests **只引用 AlhPro.Core**
    // (不引 WinUI,见测试工程注释)——所以接线只能按源码文本钉。两条纪律:
    //   ① 一律在**去掉行注释**的文本上判(CodeOnly):本文件的注释里会用反例话说清"旧写法长什么样"
    //      (如 `tail[^500..]`),带注释判会把说明本身当成违规命中。
    //   ② 每条断言都要在"接线被改错"时真变红 —— 红检就是把这些接线逐个改回旧形态跑一遍(见证据包)。

    /// <summary>★ B(接线/顺序):预检必须插在**拆帧之前**(这是 B 的全部价值:别白跑几十分钟),
    /// 且在"本机没有可用硬编"那道闸门之后(那时才知道要换谁)。</summary>
    [Fact]
    public void The_preflight_runs_before_frame_extraction()
    {
        string code = CodeOnly(ReadRepoFile("ImgUpscalerUI", "VideoService.cs"));
        int gate = code.IndexOf("CpuFallbackPolicy.DescribeNoHwEncoder()", StringComparison.Ordinal);
        int preflight = code.IndexOf("await PreflightEncodeOrThrowAsync(", StringComparison.Ordinal);
        int extract = code.IndexOf("ffmpeg 拆帧", StringComparison.Ordinal);
        int interp = code.IndexOf("await InterpStageAsync(framesIn", StringComparison.Ordinal);
        Assert.True(gate > 0, "必须能找到'本机没有可用硬编'那道闸门");
        Assert.True(preflight > 0, "必须能找到预检调用点");
        Assert.True(extract > 0, "必须能找到拆帧段");
        Assert.True(gate < preflight, "预检要在硬编闸门之后(那时才知道本机有哪些可用硬编)");
        Assert.True(preflight < extract, "★ 预检必须在拆帧之前 —— 否则又变成'跑完才失败'");
        Assert.True(preflight < interp, "★ 预检必须在补帧之前");
    }

    /// <summary>★ B(同一判据):预检的标称帧率与「帧数台账」那行必须调**同一个** Core 判据
    /// (t60 的 B1 教训:同一件事两处各写一份极性,测试全绿却漏了 —— 这里要求同一函数出现两次)。</summary>
    [Fact]
    public void The_preflight_and_the_frame_census_share_one_nominal_fps_judgement()
    {
        string code = CodeOnly(ReadRepoFile("ImgUpscalerUI", "VideoService.cs"));
        int n = CountOccurrences(code, "AlhPro.Core.VideoEncodeGuard.PlanNominalOutputFps(");
        Assert.True(n >= 2, $"拆帧前预检与帧数台账必须都调 PlanNominalOutputFps(实际出现 {n} 次)");
        Assert.Contains("目标输出帧率【预计】", code);   // 台账那行仍在(不许为了省事删掉)
    }

    /// <summary>★ A(接线):失败文本不再只留尾巴,且日志里必须有本次编码的完整命令。</summary>
    [Fact]
    public void The_failure_text_and_the_encode_command_reach_the_log()
    {
        string code = CodeOnly(ReadRepoFile("ImgUpscalerUI", "VideoService.cs"));
        Assert.Contains("VideoEncodeGuard.DescribeProcessFailure(", code);
        Assert.DoesNotContain("tail[^500..]", code);     // ★ 旧口径:只留最后 500 字符(把真因切了)
        Assert.DoesNotContain("tail[^800..]", code);     // 同族旧口径(探测类命令那条)
        Assert.Contains("编码命令(", code);               // 编码前把完整命令写进日志
        Assert.Contains("视频不落 CPU", code);            // 重试/换编码器两条路的共同策略口径仍在
    }

    /// <summary>★ C(接线):编码失败的两条路都要在,而且**瞬时失败那条仍走 3 次重试**
    /// (不许为了修"打不开"把重试那条路一起换掉 —— 5060 那台正是靠重试恢复的)。</summary>
    [Fact]
    public void The_encode_loop_switches_encoder_on_init_failure_but_still_retries_transient_ones()
    {
        string code = CodeOnly(ReadRepoFile("ImgUpscalerUI", "VideoService.cs"));
        Assert.Contains("VideoEncodeGuard.ClassifyEncodeFailure(", code);
        Assert.Contains("VideoEncodeGuard.NextEncoderCandidate(", code);
        Assert.Contains("VideoEncodeGuard.DescribeEncoderSwitch(", code);
        Assert.Contains("CpuFallbackPolicy.RetryDelayMsAfterAttempt(hwAttempt)", code);   // 瞬时失败:既有重试
        Assert.Contains("CpuFallbackPolicy.DescribeHwEncodeInitFailure(", code);          // 打不开:另一条文案
        Assert.Contains("CpuFallbackPolicy.HwEncodeTotalAttempts", code);
    }

    /// <summary>★ D(接线):越阈值时必须**用户可见**(界面日志区 progress 上报 + 文件日志),
    /// 而不是只写进文件日志。</summary>
    [Fact]
    public void The_nominal_fps_guard_reaches_the_user_visible_log()
    {
        string code = CodeOnly(ReadRepoFile("ImgUpscalerUI", "VideoService.cs"));
        int guard = code.IndexOf("VideoEncodeGuard.NominalFpsGuardReason(", StringComparison.Ordinal);
        Assert.True(guard > 0, "必须调用护栏判据");
        // 判据后面 500 字符内必须同时有"文件日志"与"界面日志区"两处上报(不许只记文件日志)
        string around = code.Substring(guard, Math.Min(500, code.Length - guard));
        Assert.Contains("AppLogger.Warn(", around);
        Assert.Contains("progress?.Report(", around);
    }

    /// <summary>★ low(接线):诊断包导出信息行在"全部被拦"时,开头不再写「导出时强制实测一次」
    /// —— 那句话必须被 willProbe 的条件包住(纯文案,不改行为)。</summary>
    [Fact]
    public void The_diagnostic_export_line_does_not_claim_a_probe_that_did_not_happen()
    {
        string code = CodeOnly(ReadRepoFile("ImgUpscalerUI", "Views", "MainPage.xaml.cs"));
        int idx = code.IndexOf("导出时强制实测一次", StringComparison.Ordinal);
        Assert.True(idx > 0, "必须能找到那行文案");
        string before = code.Substring(Math.Max(0, idx - 220), Math.Min(220, idx));
        Assert.Contains("willProbe.Count", before);      // ★ 被 willProbe 条件包住(全部被拦时不这么写)
        Assert.Contains("全部跳过", code);                // 全部被拦时的说法
    }

    // ═══════════════════════ 工具 ═══════════════════════

    /// <summary>去掉整行 `//` 注释后的源码(判接线时用):注释里会用反例话说清"旧写法长什么样",
    /// 带注释判会把说明本身当成违规命中。口径与重命名前的 t62 测试一致(`\r` 也去掉)。</summary>
    private static string CodeOnly(string src)
    {
        var lines = src.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var kept = new System.Collections.Generic.List<string>();
        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
            kept.Add(line);
        }
        return string.Join("\n", kept);
    }

    private static int CountOccurrences(string text, string needle)
    {
        int n = 0, at = 0;
        while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { n++; at += needle.Length; }
        return n;
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var cand = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(cand)) return File.ReadAllText(cand);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("找不到仓库文件: " + string.Join('/', parts));
    }
}
