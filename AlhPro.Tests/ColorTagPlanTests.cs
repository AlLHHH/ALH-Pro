using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>输出色彩标记「跟随源」的判据(纯逻辑 + 源码契约)。
///
/// 【为什么值得单独钉】整条流水线的输出色标原先**写死** bt709/tv;改完之后必须满足两件事:
///   ① **默认路径与改动前逐字一致**(HDR 素材、标记缺失的素材一个字节都不许变)——
///      否则就是"为了修一个 SD 源,把所有 1080p 成片的标签全动了",风险远大于收益;
///   ② **只有"源明确标了已知 SDR 且不是 bt709"时才跟随源**(如 SD 的 bt470bg / smpte170m),
///      而且 ffmpeg 参数名与 H.273 VUI 码必须都对 —— 写错数字 = 成片标错色,画面上却看不出来。</summary>
public class ColorTagPlanTests
{
    // ───────────────── ① 默认路径:与改动前逐字一致 ─────────────────

    [Fact]
    public void Default_plan_is_byte_identical_to_the_old_hardcoded_strings()
    {
        Assert.Equal(" -color_range tv -colorspace bt709 -color_primaries bt709 -color_trc bt709",
            AlhPro.Core.ColorTagPlan.Bt709Args);
        Assert.Equal("colour_primaries=1:transfer_characteristics=1:matrix_coefficients=1",
            AlhPro.Core.ColorTagPlan.Bt709VuiBsf);
    }

    [Fact]
    public void Unknown_or_incomplete_tags_keep_todays_bt709_output()
    {
        foreach (var (r, p, t, m) in new[]
                 {
                     ("unknown", "unknown", "unknown", "unknown"),
                     ("reserved", "reserved", "reserved", "reserved"),
                     ("tv", "bt709", "bt709", ""),                 // matrix 缺
                     ("", "", "", ""),
                     ("tv", "unspecified", "bt709", "bt709"),
                 })
        {
            var plan = AlhPro.Core.ColorTagPlan.ForSource(r, p, t, m, convertedToBt709: false);
            Assert.Equal(AlhPro.Core.ColorTagPlan.Bt709Args, plan.args);
            Assert.Equal(AlhPro.Core.ColorTagPlan.Bt709VuiBsf, plan.vuiBsf);
        }
    }

    [Fact]
    public void Bt709_source_keeps_todays_output()
    {
        var plan = AlhPro.Core.ColorTagPlan.ForSource("tv", "bt709", "bt709", "bt709", convertedToBt709: false);
        Assert.Equal(AlhPro.Core.ColorTagPlan.Bt709Args, plan.args);
    }

    [Fact]
    public void Converted_source_keeps_todays_output()
    {
        // HDR/广色域那条路:拆帧已经 zscale → bt709,输出**必须**继续标 bt709
        var plan = AlhPro.Core.ColorTagPlan.ForSource("tv", "bt2020", "smpte2084", "bt2020nc", convertedToBt709: true);
        Assert.Equal(AlhPro.Core.ColorTagPlan.Bt709Args, plan.args);
        Assert.Equal(AlhPro.Core.ColorTagPlan.Bt709VuiBsf, plan.vuiBsf);
    }

    // ───────────────── ② 跟随源:只有"已知 SDR 非 bt709" ─────────────────

    [Fact]
    public void Known_sd_source_makes_the_output_follow_the_source()
    {
        var plan = AlhPro.Core.ColorTagPlan.ForSource("tv", "bt470bg", "bt470bg", "bt470bg", convertedToBt709: false);
        Assert.Equal(" -color_range tv -colorspace bt470bg -color_primaries bt470bg -color_trc bt470bg", plan.args);
        Assert.Equal("colour_primaries=5:transfer_characteristics=5:matrix_coefficients=5", plan.vuiBsf);
        Assert.Contains("跟随源", plan.describe);
    }

    [Fact]
    public void Full_range_source_keeps_pc_in_the_output()
    {
        var plan = AlhPro.Core.ColorTagPlan.ForSource("pc", "smpte170m", "smpte170m", "smpte170m", convertedToBt709: false);
        Assert.StartsWith(" -color_range pc ", plan.args);
        Assert.Equal("colour_primaries=6:transfer_characteristics=6:matrix_coefficients=6", plan.vuiBsf);
    }

    [Fact]
    public void Case_and_whitespace_are_tolerated()
    {
        var plan = AlhPro.Core.ColorTagPlan.ForSource(" TV ", " BT470BG ", "Bt470Bg", "bt470bg", convertedToBt709: false);
        Assert.Equal(" -color_range tv -colorspace bt470bg -color_primaries bt470bg -color_trc bt470bg", plan.args);
    }

    // ───────────────── ③ 名字 → H.273 码 ─────────────────

    [Fact]
    public void Name_to_code_mapping_is_h273_and_falls_back_to_bt709()
    {
        Assert.Equal(1, AlhPro.Core.ColorTagPlan.PrimariesCode("bt709"));
        Assert.Equal(5, AlhPro.Core.ColorTagPlan.PrimariesCode("bt470bg"));
        Assert.Equal(6, AlhPro.Core.ColorTagPlan.PrimariesCode("smpte170m"));
        Assert.Equal(9, AlhPro.Core.ColorTagPlan.PrimariesCode("bt2020"));
        Assert.Equal(1, AlhPro.Core.ColorTagPlan.PrimariesCode("谁知道这是什么"));   // 认不出 → bt709

        Assert.Equal(1, AlhPro.Core.ColorTagPlan.TransferCode("bt709"));
        Assert.Equal(6, AlhPro.Core.ColorTagPlan.TransferCode("smpte170m"));
        Assert.Equal(16, AlhPro.Core.ColorTagPlan.TransferCode("smpte2084"));
        Assert.Equal(18, AlhPro.Core.ColorTagPlan.TransferCode("arib-std-b67"));
        Assert.Equal(1, AlhPro.Core.ColorTagPlan.TransferCode(null));

        Assert.Equal(1, AlhPro.Core.ColorTagPlan.MatrixCode("bt709"));
        Assert.Equal(5, AlhPro.Core.ColorTagPlan.MatrixCode("bt470bg"));
        Assert.Equal(6, AlhPro.Core.ColorTagPlan.MatrixCode("smpte170m"));
        Assert.Equal(9, AlhPro.Core.ColorTagPlan.MatrixCode("bt2020nc"));
        Assert.Equal(1, AlhPro.Core.ColorTagPlan.MatrixCode(""));
    }

    // ───────────────── ④ 接线:编码参数真的用了这个计划 ─────────────────

    [Fact]
    public void Encoder_and_probe_are_wired_to_the_plan()
    {
        string? path = null;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var cand = Path.Combine(dir.FullName, "ImgUpscalerUI", "VideoService.cs");
            if (File.Exists(cand)) { path = cand; break; }
            dir = dir.Parent;
        }
        Assert.NotNull(path);
        var src = File.ReadAllText(path!);
        // 编码参数取计划(而不是写死那串 bt709)
        Assert.Contains("(string colorArgs, string vuiCodes) = _outputColorPlan;", src);
        Assert.Contains("? \" -bsf:v hevc_metadata=\" + vuiCodes", src);
        Assert.Contains(": \" -bsf:v h264_metadata=\" + vuiCodes;", src);
        // 探测阶段:每次先复位 + 只有那一路覆盖
        Assert.Contains("_outputColorPlan = (AlhPro.Core.ColorTagPlan.Bt709Args, AlhPro.Core.ColorTagPlan.Bt709VuiBsf);", src);
        Assert.Contains("AlhPro.Core.ColorTagPlan.ForSource(", src);
        // 写死那串不许再回到 EncoderArgs 里(注释里可以提)
        Assert.DoesNotContain("const string colorArgs = \" -color_range tv", src);
    }
}
