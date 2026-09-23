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

    /// <summary>只有"用户显式选 CPU"(引擎设备号 &lt; 0)才允许落 CPU;0/1/2… 都算要用 GPU。</summary>
    [Theory]
    [InlineData(-1, true)]    // 设置里选「CPU 计算」
    [InlineData(0, false)]    // 独显(绝大多数机器)
    [InlineData(1, false)]    // 核显 / 第二张卡 —— 仍然"要用 GPU"
    [InlineData(2, false)]
    public void Cpu_is_only_allowed_when_the_user_explicitly_chose_it(int gpuId, bool allowed)
        => Assert.Equal(allowed, AlhPro.Core.CpuFallbackPolicy.AllowsCpuFallback(gpuId));

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

    /// <summary>报错文案必须能自己救回来:说清"硬编不可用"+"不落 CPU"+"去设置里显式选 CPU"。</summary>
    [Fact]
    public void The_no_encoder_error_tells_the_user_how_to_recover()
    {
        string msg = AlhPro.Core.CpuFallbackPolicy.DescribeNoHwEncoder();
        Assert.Contains("硬件编码器", msg);
        Assert.Contains("nvenc", msg);
        Assert.Contains("amf", msg);
        Assert.Contains("qsv", msg);
        Assert.Contains("CPU 软编", msg);
        Assert.Contains("设置", msg);          // 自救入口:设置里显式选 CPU
        Assert.Contains("驱动", msg);
    }

    /// <summary>重试全失败的报错:要带上编码器名、尝试次数、最后一次原因,并按是否驱动过旧给不同建议。</summary>
    [Fact]
    public void The_retry_exhausted_error_carries_encoder_attempts_and_reason()
    {
        string normal = AlhPro.Core.CpuFallbackPolicy.DescribeHwEncodeFailure("h264_nvenc", 3, "exit -542398533", false);
        Assert.Contains("h264_nvenc", normal);
        Assert.Contains("3", normal);
        Assert.Contains("exit -542398533", normal);
        Assert.Contains("设置", normal);
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
        Assert.Contains("CpuFallbackPolicy.AllowsCpuFallback(gpuId)", code);
    }

    /// <summary>★ "编码实测"那行回报不许再靠预编码缓存的名字判硬编/软编(旧写法导致 CPU 软编被标成"(硬编)")。</summary>
    [Fact]
    public void The_encode_report_labels_hardware_or_cpu_from_the_real_encoder()
    {
        var code = ReadRepoFile("ImgUpscalerUI", "VideoService.cs");
        Assert.Contains("encoder is \"libx264\" or \"libx265\" ? \"(CPU 软编)\" : \"(硬编)\"", code);
        Assert.DoesNotContain("encUsed", code);   // 那个会被回退"带旧"的缓存变量已删除
    }

    /// <summary>★ GPU 黑块时**不许**再用 ncnn-CPU 逐块重算视频帧(该方法是视频专用)。</summary>
    [Fact]
    public void Engine_blackout_no_longer_reprocesses_tiles_on_cpu()
    {
        var code = ReadRepoFile("ImgUpscalerUI", "EngineService.cs");
        Assert.DoesNotContain("scale, noise, -1, tta", code);   // 旧 CPU 逐块重算的调用形态
        Assert.DoesNotContain("改用 CPU 软解重处理", code);
        Assert.Contains("按「视频不落 CPU」策略停止", code);
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
