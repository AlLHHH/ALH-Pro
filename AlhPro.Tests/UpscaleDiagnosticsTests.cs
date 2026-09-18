using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace AlhPro.Tests;

/// <summary>超分阶段「变慢诊断」的接线契约(2026-09-16 用户批准新增)。
///
/// 【为什么要有】排查"超分怎么变慢了"时,日志里**没有任何一个数**能区分
/// 「GPU 没吃饱(宿主侧落盘/编码是瓶颈)」和「GPU 算力本身不够」—— 只能一遍遍手工跑引擎基准去猜。
/// 现在超分阶段会采 GPU 利用率,并把每批每帧耗时拆成「引擎」与「落盘+回填」两段。
///
/// 【用户明确要求:只进日志文件,不上界面】左下角那个日志区不要被诊断刷屏 ⇒ 诊断一律走 `AppLogger.Info`,
/// 一处都不许走 `progress?.Report`。这条最容易被后来人"顺手"改回去(只差一个 API 名),所以在这里钉住。
///
/// 【另一条】采不到就如实写"未采到",**绝不许编数字**(非 NVIDIA / 驱动异常时必须静默降级)。</summary>
public class UpscaleDiagnosticsTests
{
    /// <summary>① 诊断只写日志文件:每一处「[超分诊断]」都必须出自 AppLogger.Info,不许走 progress。</summary>
    [Fact]
    public void Upscale_diagnostics_go_to_the_log_file_only()
    {
        var svc = ReadRepoFile("ImgUpscalerUI", "VideoService.cs");

        int all = Regex.Matches(svc, @"\[超分诊断\]").Count;
        int viaLog = Regex.Matches(svc, @"AppLogger\.Info\(\$""\[超分诊断\]").Count;
        Assert.True(all > 0, "找不到「超分诊断」那行日志");
        Assert.Equal(all, viaLog);   // 有一处走 progress/Console 就会不相等(用户明确不上界面)

        // GPU 利用率必须附在「超分实测」那行,并且采不到时如实说"未采到"(不许编数字)
        Assert.Contains("GPU 利用率", svc);
        Assert.Contains("gpuSampler?.Summary()", svc);
        Assert.Contains("GPU 利用率 未采到", svc);
    }

    /// <summary>② 每批耗时要拆成「引擎」与「落盘+回填」两段,并把真实引擎参数写出来(否则又只能猜)。</summary>
    [Fact]
    public void Per_batch_diagnostic_breaks_down_engine_vs_host_time()
    {
        var svc = ReadRepoFile("ImgUpscalerUI", "VideoService.cs");

        Assert.Contains("var swBatchDbg = System.Diagnostics.Stopwatch.StartNew();", svc);
        Assert.Contains("double engineSecDbg = swBatchDbg.Elapsed.TotalSeconds;", svc);
        Assert.Contains("落盘+回填", svc);
        // 引擎参数如实写出来:旧日志只有"路线=ncnn-Vulkan",分块/线程/输出格式/并发路数全都看不到
        Assert.Contains("SafeRender.GetEngineThreadArgs()", svc);
        Assert.Contains("并发路数", svc);
        Assert.Contains("批开始空闲显存", svc);
    }

    /// <summary>③ 采样器必须"静默降级 + 有兜底":采不到返回 null、最多跑 90 分钟自己停(防调用方漏 Dispose)。</summary>
    [Fact]
    public void Gpu_util_sampler_degrades_silently_and_is_capped()
    {
        var sr = ReadRepoFile("ImgUpscalerUI", "SafeRender.cs");

        Assert.Contains("public static GpuUtilSampler? StartGpuUtilSampler()", sr);
        Assert.Contains("if (a.Length == 0) return null;", sr);          // 采不到 → null,不抛不编
        Assert.Contains("DateTime.UtcNow.AddMinutes(90)", sr);           // 兜底上限
        Assert.Contains("catch { return null; }", sr);                    // 起不来也静默
    }

    /// <summary>④ 显存档位比较必须带容差 —— 这是"8GB 卡掉回单路"的根因,一行之差(2026-09-16 真机实测)。
    /// 显存墙 = 总量×0.75 是算出来的:8188 MiB → 7.996GB → 5.997,与门槛 6.0 只差 3MB。
    /// 没有容差,整张 8GB 卡就被判成"不够 2 路",超分少掉实测 1.63 倍的吞吐。
    /// 后人若把 `- vramEps` 当成"多余的减法"删掉,这里会立刻红。</summary>
    [Fact]
    public void Vram_tier_comparison_keeps_its_rounding_tolerance()
    {
        var sr = ReadRepoFile("ImgUpscalerUI", "SafeRender.cs");
        Assert.Contains("const double vramEps = 0.05;", sr);
        Assert.Contains("bool two = r >= 16 && v >= 6 - vramEps && vramOk2 && cores >= 8;", sr);
        Assert.Contains("bool three = Profile == DeviceProfile.High && r >= 24 && v >= 10 - vramEps && vramOk3 && cores >= 16;", sr);
        // 理由必须留在代码里(否则下一个人不知道这是实测结论,只看到两个魔法数)
        Assert.Contains("8188 MiB", sr);
        Assert.Contains("1.63 倍", sr);
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
