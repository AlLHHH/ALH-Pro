using System;
using System.IO;
using System.Linq;
using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【2026-09-29 · 用户诊断包】按尺寸选编码器的契约:H.264 硬编实测上限 4096,超过就必须走 HEVC。
///
/// 真机现场(AMD RX 9070 XT,v1.4.3):同一台机器
///   · 2880×2160(不放大)→ `h264_amf` 正常出片;
///   · 2880×2160 **×2 超分 = 5760×4320 @120fps** → `[h264_amf] Task finished with error: Invalid argument`。
/// 参数串本身没问题(本地逐条验过选项都能解析),是 H.264 编不了这个宽度(RDNA 的 VCN:H.264 上限 4096,HEVC 可到 8192)
/// ⇒ 这种尺寸**只能**上 HEVC。判据是纯函数(可单测);接线(把计划输出尺寸真的传进 PickVideoEncoder)用源码文本钉住,
/// 与仓库既有手法(LocalCalibrationWiringTests 等)一致 —— 只改签名不改调用点,就会静默回到老路。</summary>
public class EncoderSizeCeilingTests
{
    [Theory]
    [InlineData(1920, 1080, false)]
    [InlineData(4096, 2160, false)]     // 正好贴上限:允许(实测 4096 以内 H.264 正常)
    [InlineData(3840, 2160, false)]     // 用户跑成功的那一趟
    [InlineData(4097, 2160, true)]      // 超一个像素就得换 HEVC
    [InlineData(5760, 4320, true)]      // 失败的这一趟
    [InlineData(3840, 4321, true)]      // 高度方向同样受约束
    [InlineData(0, 0, false)]           // 尺寸未知:不许乱改编码器
    [InlineData(0, 4320, true)]         // 已知的那一边超了就要换
    public void Exceeds_h264_limit_matches_the_measured_ceiling(int w, int h, bool expected)
        => Assert.Equal(expected, VideoEncodeGuard.ExceedsH264Limit(w, h));

    [Fact]
    public void Ceiling_constant_is_4096()
        => Assert.Equal(4096, VideoEncodeGuard.H264MaxDimension);

    /// <summary>接线钉死:① 选配函数必须接住尺寸参数;② 两个调用点(开跑前预检 / 编码阶段兜底)都必须把它传进去;
    /// ③ 超上限时的处置必须写成"改走 HEVC"而不是继续用 H.264。</summary>
    [Fact]
    public void Both_call_sites_pass_the_planned_output_size_and_switch_to_hevc()
    {
        var svc = ReadRepoFile("ImgUpscalerUI", "VideoService.cs");
        Assert.Contains("PickVideoEncoder(int gpuId, int codecPref = 0, int outWidth = 0, int outHeight = 0)", svc);
        Assert.Contains("PickVideoEncoder(gpuId, codecPref, plannedOutW, plannedOutH)", svc);
        Assert.Contains("VideoEncodeGuard.ExceedsH264Limit(outWidth, outHeight)", svc);
        // 超上限 ⇒ 走 HEVC(而不是只记一行日志了事)
        Assert.Contains("if (!string.IsNullOrEmpty(forcedHevc)) chosen = forcedHevc;", svc);
        // 按尺寸改走的那条路只从"厂商 HEVC / 本机实测可用的 hevc"里挑,绝不落 CPU
        // (既有的「优先 H.265」分支里那句 `chosen = "libx265"` 是另一条路,本次不动)
        Assert.Contains("string? forcedHevc = hevcVendor.Length > 0", svc);
        Assert.DoesNotContain("forcedHevc = \"libx265\"", svc);
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
