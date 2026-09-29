using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【2026-09-23 用户要求】「落到 CPU 根本不现实太慢了 —— 应该重试几次,如果还是不行直接报错,
/// 不应该落到 CPU」。范围经用户确认:**只视频这一侧**(图片/抠图/音频允许 CPU)。
///
/// 这里钉两样:
///   ① 纯逻辑判据(<see cref="AlhPro.Core.CpuFallbackPolicy"/>)—— 谁能落 CPU、重试几次、报错说什么;
///   ② 源码契约 —— 旧的两处"自动落 CPU"写法必须**再也搜不到**(它们不会编译报错,只会静默变慢):
///      · VideoService 里"硬编失败 → libx264 软编"那一整段;
///      · EngineService 里"GPU 黑块 → 用 ncnn-CPU 逐块重算"那一整段。
/// **诚实边界**:契约只能证明"这条降级路径被删掉了",证明不了"真机跑起来确实走的是重试+报错" ——
/// 后者要真实失败一次才能看到(本轮无法在真机上人为制造硬编失败,见交接清单的"未实测"清单)。</summary>
public class CpuFallbackPolicyTests
{
    // ───────────────────────── ① 纯逻辑判据 ─────────────────────────

    /// <summary>★ B8(2026-09-23 二次修正):视频**一律**不许落 CPU —— 判据恒为 false。
    /// 旧判据是 <c>gpuId &lt; 0</c>(当作"用户显式选 CPU"),但设置页早已不提供 CPU 选项,
    /// 那个值的唯一来源是"Vulkan 自检失败 → MainPage 临时置 -1"(自动降级)⇒ 拿它当用户选择就是留后门。</summary>
    [Fact]
    public void Video_never_falls_back_to_cpu()
        => Assert.False(AlhPro.Core.CpuFallbackPolicy.AllowsCpuFallback());

    /// <summary>重试次数与间隔:首次失败后重试 2 次(共 3 次尝试),间隔递增(给驱动释放编码会话的时间)。</summary>
    [Fact]
    public void Hardware_encode_retries_twice_before_giving_up()
    {
        var p = typeof(AlhPro.Core.CpuFallbackPolicy);
        Assert.Equal(3, AlhPro.Core.CpuFallbackPolicy.HwEncodeTotalAttempts);
        Assert.Equal(2, AlhPro.Core.CpuFallbackPolicy.HwEncodeRetryDelaysMs.Length);
        Assert.All(AlhPro.Core.CpuFallbackPolicy.HwEncodeRetryDelaysMs, ms => Assert.True(ms > 0, "重试间隔必须为正"));
        Assert.True(AlhPro.Core.CpuFallbackPolicy.HwEncodeRetryDelaysMs[1]
                    > AlhPro.Core.CpuFallbackPolicy.HwEncodeRetryDelaysMs[0], "第二次间隔应更长");
        // 第 n 次失败后该等多久;没有下一次了必须返回 0(调用方据此判定"放弃并报错")
        Assert.Equal(AlhPro.Core.CpuFallbackPolicy.HwEncodeRetryDelaysMs[0],
                     AlhPro.Core.CpuFallbackPolicy.RetryDelayMsAfterAttempt(1));
        Assert.Equal(AlhPro.Core.CpuFallbackPolicy.HwEncodeRetryDelaysMs[1],
                     AlhPro.Core.CpuFallbackPolicy.RetryDelayMsAfterAttempt(2));
        Assert.Equal(0, AlhPro.Core.CpuFallbackPolicy.RetryDelayMsAfterAttempt(3));
        Assert.Equal(0, AlhPro.Core.CpuFallbackPolicy.RetryDelayMsAfterAttempt(0));
        Assert.Equal(0, AlhPro.Core.CpuFallbackPolicy.RetryDelayMsAfterAttempt(99));
        Assert.NotNull(p);
    }

    /// <summary>报错文案必须能自己救回来:说清"硬编不可用" + 给可执行的自救动作(更新驱动/重启),
    /// 而且**不许**再让人去设置里找「CPU 计算」—— 那个选项不存在(B8)。</summary>
    [Fact]
    public void The_no_encoder_error_tells_the_user_how_to_recover()
    {
        string msg = AlhPro.Core.CpuFallbackPolicy.DescribeNoHwEncoder();
        Assert.Contains("硬件编码器", msg);
        Assert.Contains("nvenc", msg);
        Assert.Contains("amf", msg);
        Assert.Contains("qsv", msg);
        Assert.Contains("CPU 软编", msg);
        Assert.Contains("驱动", msg);          // 自救入口 ①:更新驱动
        Assert.Contains("重启", msg);          // 自救入口 ②:重启
        Assert.Contains("不提供", msg);         // 说清"本软件没有 CPU 选项",别把人支到点不到的地方
        Assert.DoesNotContain("CPU 计算", msg); // ★ 不许再指向那个已不存在的设置项
    }

    /// <summary>重试全失败的报错:要带上编码器名、尝试次数、最后一次原因,并按是否驱动过旧给不同建议。</summary>
    [Fact]
    public void The_retry_exhausted_error_carries_encoder_attempts_and_reason()
    {
        string normal = AlhPro.Core.CpuFallbackPolicy.DescribeHwEncodeFailure("h264_nvenc", 3, "exit -542398533", false);
        Assert.Contains("h264_nvenc", normal);
        Assert.Contains("3", normal);
        Assert.Contains("exit -542398533", normal);
        Assert.Contains("驱动", normal);                       // 可执行建议(不再是"去设置里选 CPU")
        Assert.DoesNotContain("CPU 计算", normal);              // ★ B8:不指向已不存在的设置项
        Assert.Contains("不会退回 CPU 软编", normal);

        string driver = AlhPro.Core.CpuFallbackPolicy.DescribeHwEncodeFailure("h264_nvenc", 1, "minimum required Nvidia driver", true);
        Assert.Contains("驱动过旧", driver);
        Assert.Contains("更新显卡驱动", driver);

        // 原因拿不到时不能崩、也不能留空
        string empty = AlhPro.Core.CpuFallbackPolicy.DescribeHwEncodeFailure("h264_amf", 3, null, false);
        Assert.Contains("(未给出原因)", empty);
        Assert.Contains("h264_amf", empty);
    }

    // ───────────────────────── ② 源码契约 ─────────────────────────

    /// <summary>★ 视频编码失败**不许**再出现"改用 CPU 软编"那条路(它不会编译报错,只会静默慢 7 倍)。</summary>
    [Fact]
    public void Video_encoder_failure_no_longer_falls_back_to_cpu()
    {
        var code = ReadRepoFile("ImgUpscalerUI", "VideoService.cs");
        Assert.DoesNotContain("EncoderArgs(cpuEnc", code);                 // 旧回退那条 ffmpeg 命令
        Assert.DoesNotContain("(CPU 软编,硬件编码回退)", code);             // 旧回退的显示名
        Assert.DoesNotContain("BrokenHwEncoders.Add(", code);              // 旧"记坏→以后静默走 CPU"
        Assert.DoesNotContain("HashSet<string> BrokenHwEncoders", code);   // 旧字段本身
        // 新路径必须在:重试 + 两类报错 + 开跑前的闸门
        Assert.Contains("CpuFallbackPolicy.RetryDelayMsAfterAttempt", code);
        Assert.Contains("CpuFallbackPolicy.DescribeHwEncodeFailure", code);
        Assert.Contains("CpuFallbackPolicy.DescribeNoHwEncoder", code);
        Assert.Contains("HasAnyWorkingHwEncoder()", code);
        Assert.Contains("CpuFallbackPolicy.AllowsCpuFallback()", code);   // B8:不再传 gpuId(设置里没有 CPU 选项了)
        Assert.DoesNotContain("AllowsCpuFallback(gpuId)", code);
    }

    /// <summary>★ "编码实测"那行回报不许再靠预编码缓存的名字判硬编/软编(旧写法导致 CPU 软编被标成"(硬编)")。</summary>
    [Fact]
    public void The_encode_report_labels_hardware_or_cpu_from_the_real_encoder()
    {
        var code = ReadRepoFile("ImgUpscalerUI", "VideoService.cs");
        Assert.Contains("encoder is \"libx264\" or \"libx265\" ? \"(CPU 软编)\" : \"(硬编)\"", code);
        Assert.DoesNotContain("encUsed", code);   // 那个会被回退"带旧"的缓存变量已删除
    }

    /// <summary>★ GPU 黑块那套**视频批量**事后判定已整体删除(2026-09-27),且**绝不许**再出现 CPU 逐块重算;
    /// 而"单图/分块成品"的自带源图豁免守卫 `GuardSilentBlackOutput` 按同一次收口**恢复保留**。
    /// 【原契约】2026-09-23 那次要求:检测到 GPU 黑块时不许用 ncnn-CPU 逐块重算视频帧(视频专用路径),
    /// 必须明确报错停止 —— 当时钉的三条是"没有 CPU 调用形态 / 没有那句旧文案 / 有'按「视频不落 CPU」策略停止'"。
    /// 【为什么契约变了】2026-09-27 作者要求"黑帧判断这个功能直接删掉":视频那条"检测到黑块 → 换 ONNX 或报错停止"
    /// 的链本身(**单图分块**路径 EngineService.UpscaleTiledAsync 里的 HasBlackPng 分支 —— 注意这是单图路径、不是视频批量)已删除 ⇒ "停止"的文案不复存在,
    /// 但"绝不用 CPU 逐块重算视频帧"这条**更强**了(连同判黑一起没了)。
    /// 【2026-09-27 二次收口】同一次大删除里被一并误删的**单图/分块成品守卫**已按作者指令恢复:
    /// 它的判据自带源图豁免(`IsSilentBlackFailure` = 输出近黑 **且** 输入不近黑),单张静帧不存在"黑转场"
    /// 这回事 ⇒ 不会被素材黑误伤;图片页没有别的兜底,删掉它 ⇒ 真实故障静默出黑图且零日志。
    /// 所以本测试现在钉两件事:①视频批量那套(判黑入口 / 换路信号 / CPU 逐块重算)一律不许回来;
    /// ②单图守卫必须在,且判据必须仍然走自带源图豁免的那条纯函数。</summary>
    [Fact]
    public void Video_batch_blackout_judgement_is_gone_but_the_single_image_guard_is_back()
    {
        // 【为什么先剥注释】删除说明本身就要写清"删了哪个入口、原来是什么行为"(2026-09-27),
        // 注释里必然出现 HasBlackPng / BLACKOUT_NEED_ONNX 这些名字;这条测试钉的是**代码**,不是注释。
        var code = StripLineComments(ReadRepoFile("ImgUpscalerUI", "EngineService.cs"));
        Assert.DoesNotContain("scale, noise, -1, tta", code);      // 旧 CPU 逐块重算的调用形态
        Assert.DoesNotContain("改用 CPU 软解重处理", code);
        Assert.DoesNotContain("按「视频不落 CPU」策略停止", code);   // 该分支已随判黑一起删除
        Assert.DoesNotContain("HasBlackPng", code);                 // 视频批量事后判黑入口已删除
        Assert.DoesNotContain("BLACKOUT_NEED_ONNX", code);          // "转 ONNX"信号已删除
        Assert.DoesNotContain("ProbeBatchBlackOutputHint", code);   // 批量事后抽样提示已删除
        Assert.DoesNotContain("DefectSampling", code);              // 抽样判黑本体(AlhPro.Core.DefectSampling)已删除
        // 恢复保留的部分:单图/分块成品守卫必须在,判据必须自带源图豁免(不是"输出黑就报")
        Assert.Contains("GuardSilentBlackOutput", code);
        Assert.Contains("FrameInspect.IsSilentBlackFailure(inBlack, outBlack)", code);
    }

    /// <summary>剥掉源码里的行注释(含 `///`,它们都以 `//` 开头)——
    /// 仅用于"某符号必须不存在"的源码契约断言:注释可以(也应该)解释删掉了什么。</summary>
    private static string StripLineComments(string src)
        => string.Join("\n", src.Split('\n').Select(l =>
        {
            int i = l.IndexOf("//", System.StringComparison.Ordinal);
            return i >= 0 ? l.Substring(0, i) : l;
        }));

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
