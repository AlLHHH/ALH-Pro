using System;
using System.IO;
using System.Linq;
using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【2026-09-23 · 来自一份真实用户反馈】"用户反馈不能补帧" —— 诊断包 `ALHPro_Diag_20260923_1345`
/// (AMD RX 6750 GRE 那台)里的真相**不是补帧坏了**,而是临时盘守门在开跑前就拒了:
///   `临时磁盘空间不足:F:\ALHProTemp 仅剩 132GB,本任务预计需要约 262GB`
///   6 次尝试全部 **0.2 秒失败**,用户看到的就是"补帧不能用"。
/// 素材:4K(3840×2160)源 + 2x 超分(输出 7680×4320)+ 2x 补帧;反算出的源帧数 ≈9340(≈5.2 分钟 @30fps);
/// 该机上批大小被面积缩放到 70 帧(峰值像素 20.7 Mpx)。
///
/// 这个文件做两件事:
///   ① 用**他的真实数字**钉住"8K+2x补帧 = 262GB"这个量级(口径没被人悄悄改小/改大);
///   ② 钉住"该往哪降"的答案:**关掉超分(输出留 4K、补帧保留)≈118GB —— 正好塞进 132GB**。
///      这条是本轮真正的产品缺口:原来的提示只说"降低倍率",用户根本不知道关掉超分就能跑。
/// </summary>
public class TempSpaceAdviceTests
{
    // 该用户素材的真实参数(从诊断包反算)
    private const int SrcW = 3840, SrcH = 2160;
    private const long BaseFrames = 9340;
    private const bool Interp = true;
    private const int InterpScale = 2;
    private const double OutMult = 2.0;      // 2x 超分 ⇒ 输出 7680×4320
    private const int BatchFrames = 70;      // 面积缩放后的每批帧数(峰值像素 20.7 Mpx)
    private const double TheirFreeGB = 132;  // F:\ALHProTemp 当时的剩余

    /// <summary>★ 复现他的现场:8K + 2x 补帧 ≈ 262GB(与诊断包里的数字同量级)。</summary>
    [Fact]
    public void Reproduces_the_reported_262_gb_for_4k_upscaled_2x_plus_2x_interp()
    {
        double gb = TempSpaceEstimate.NeedGigabytesFor(SrcW, SrcH, BaseFrames, Interp, InterpScale, OutMult, BatchFrames);
        Assert.InRange(gb, 240, 285);          // 诊断包写的是 262GB
        Assert.True(gb > TheirFreeGB, "这条路线本来就是放不下的(守门拒绝是对的)");
    }

    /// <summary>★★ 关键答案:**关掉超分**(输出保持 4K、补帧保留)≈118GB ⇒ 放得下。
    /// 这正是那个用户真正想要的(他要的是补帧),而原提示没有告诉他。</summary>
    [Fact]
    public void Turning_off_upscale_fits_in_their_free_space_and_keeps_interpolation()
    {
        double gbNoUp = TempSpaceEstimate.NeedGigabytesFor(SrcW, SrcH, BaseFrames, Interp, InterpScale, 1.0, BatchFrames);
        Assert.True(gbNoUp < TheirFreeGB, $"关超分后 {gbNoUp:0}GB 应当能塞进 {TheirFreeGB}GB");
        Assert.InRange(gbNoUp, 100, 130);
    }

    /// <summary>三条路线的**排序**必须成立(改参数只能让它更省,不许算反):
    /// 都不关 &gt; 只关一个 &gt; 都关;且"关补帧"比"关超分"更省(8K 单帧体积是 4K 的 4 倍量级)。</summary>
    [Fact]
    public void Options_are_ordered_from_most_to_least_demanding()
    {
        double both = TempSpaceEstimate.NeedGigabytesFor(SrcW, SrcH, BaseFrames, Interp, InterpScale, OutMult, BatchFrames);
        double noUp = TempSpaceEstimate.NeedGigabytesFor(SrcW, SrcH, BaseFrames, Interp, InterpScale, 1.0, BatchFrames);
        double noInterp = TempSpaceEstimate.NeedGigabytesFor(SrcW, SrcH, BaseFrames, false, 1, OutMult, BatchFrames);
        double neither = TempSpaceEstimate.NeedGigabytesFor(SrcW, SrcH, BaseFrames, false, 1, 1.0, BatchFrames);
        Assert.True(both > noUp, "关超分必须更省");
        Assert.True(both > noInterp, "关补帧必须更省");
        Assert.True(noUp >= neither, "再关补帧不应变贵");
        Assert.True(noInterp >= neither, "再关超分不应变贵");
    }

    /// <summary>估算是单调的:帧数越多、每批越大、倍率越高 ⇒ 需求不减(防"降参数反而更贵"这种算错)。</summary>
    [Fact]
    public void Need_is_monotonic_in_frames_batch_and_multiplier()
    {
        double a = TempSpaceEstimate.NeedGigabytesFor(SrcW, SrcH, BaseFrames, Interp, InterpScale, OutMult, BatchFrames);
        double moreFrames = TempSpaceEstimate.NeedGigabytesFor(SrcW, SrcH, BaseFrames * 2, Interp, InterpScale, OutMult, BatchFrames);
        double moreBatch = TempSpaceEstimate.NeedGigabytesFor(SrcW, SrcH, BaseFrames, Interp, InterpScale, OutMult, BatchFrames * 2);
        double biggerScale = TempSpaceEstimate.NeedGigabytesFor(SrcW, SrcH, BaseFrames, Interp, InterpScale, 3.0, BatchFrames);
        double moreInterp = TempSpaceEstimate.NeedGigabytesFor(SrcW, SrcH, BaseFrames, Interp, 4, OutMult, BatchFrames);
        Assert.True(moreFrames > a);
        Assert.True(moreBatch > a);
        Assert.True(biggerScale > a);
        Assert.True(moreInterp > a);
    }

    /// <summary>与既有口径同源:`NeedGigabytesFor` 算出来的必须与 `NeedBytes` 逐字节一致
    /// (不许出现"第二套估算公式" —— 那正是历史上"预计 X GB、实际 200 多 G"的来源)。</summary>
    [Fact]
    public void Uses_exactly_the_same_formula_as_the_guard()
    {
        double srcMb = TempSpaceEstimate.SourceFrameMegabytes(SrcW, SrcH);
        double outMb = TempSpaceEstimate.PeakFrameMegabytes(SrcW, SrcH, OutMult, Interp);
        long peak = BaseFrames * InterpScale;
        double expected = TempSpaceEstimate.NeedBytes(peak, outMb, BatchFrames, srcMb) / (1024.0 * 1024 * 1024.0);
        double actual = TempSpaceEstimate.NeedGigabytesFor(SrcW, SrcH, BaseFrames, Interp, InterpScale, OutMult, BatchFrames);
        Assert.Equal(expected, actual, 9);
    }

    /// <summary>接线契约:守门抛出的那句话必须**带上可选路线与判定**,并且真的用了这个计算器。</summary>
    [Fact]
    public void The_shortage_message_quantifies_the_ways_out()
    {
        var code = StripComments(ReadRepoFile("ImgUpscalerUI", "VideoService.cs"));
        Assert.Contains("string ShortageMessage(double freeBytes, double needGB)", code);
        Assert.Contains("AlhPro.Core.TempSpaceEstimate.NeedGigabytesFor(", code);
        Assert.Contains("关掉「超分」(输出保持", code);
        Assert.Contains("关掉「补帧」", code);
        Assert.Contains("✓放得下", code);
        // 【一个真实的坑,必须写进提示】把超分改成「1x 修复」**不会省盘**:它内部照样按 2x 跑再缩回,
        // 临时帧一样是放大后的尺寸(守门口径里 upscaleShrink1x ⇒ outMult=2.0,见 outMult 的赋值)。
        Assert.Contains("改成「1x 修复」**不省盘**", code);
        Assert.Contains("其它盘剩余:", code);
        Assert.Contains("throw new InvalidOperationException(ShortageMessage(free, needGBNow));", code);
        // 旧提示(只说"降低倍率"、不给数字)必须消失
        Assert.DoesNotContain("请清理磁盘、降低补帧倍率/超分倍率,或把视频放到其它盘后再处理。", code);
    }

    private static string StripComments(string text)
        => string.Join("\n", text.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));

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
