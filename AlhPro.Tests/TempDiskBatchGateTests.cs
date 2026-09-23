using System;
using System.IO;
using System.Linq;
using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【2026-09-23 分批激进放大】用户给的两条:
/// ① 档位整体 ×2(下界 50→80、正常 300→600、好 350→700 / 长片 700→1400、单批门槛 400→800);
/// ② 新增条件「批内临时帧 ≤ 临时盘余量 × 0.65」。
/// 档位那些数字由既有的 RenderPolicyTests / AdaptiveBatchTests / DevicePerfTests 钉住(已随本轮一并更新);
/// 这个文件钉的是**新条件**——它是本次"敢把批放大"的唯一新守卫,必须逐条可验:
///   · 触发时**把批调小**(不是让任务失败 —— 那是既有整任务守门的活);
///   · 不破地板;拿不到余量就不做判定(不猜);
///   · 与 TempSpaceEstimate 的"每批并存帧"口径一致(两处数字能直接对照)。</summary>
public class TempDiskBatchGateTests
{
    // 典型量级(1080p 源、2x 超分、补帧开):源帧 ≈1MB、峰值帧 ≈0.72MB → 每帧并存 ≈1.72MB
    private const double SrcMb = 1.0;
    private const double PeakMb = 0.72;

    [Fact]
    public void Gate_does_nothing_when_there_is_room()
    {
        var g = RenderPolicy.LimitBatchByTempDisk(1400, freeDiskGB: 200, PeakMb, SrcMb);
        Assert.Equal(1400, g.BatchFrames);
        Assert.False(g.Shrunk);
        Assert.True(g.MaxFramesByDisk > 1400);   // 200GB 余量能放几万帧 ⇒ 上限远大于候选批,所以不动它
        Assert.Contains("未触发", g.Note);
    }

    /// <summary>★ 核心行为:余量不够就**把批调小**(而不是报错)。</summary>
    [Fact]
    public void Gate_shrinks_the_batch_instead_of_failing()
    {
        // 余量 10GB × 0.65 = 6.656GB = 6815MB;每帧 1.72MB → 最多 3962 帧 → 1400 帧本来就放得下
        var wide = RenderPolicy.LimitBatchByTempDisk(1400, 10, PeakMb, SrcMb);
        Assert.False(wide.Shrunk);
        // 余量 1GB × 0.65 = 665.6MB ÷ 1.72MB(每帧并存)= 386 帧
        var tight = RenderPolicy.LimitBatchByTempDisk(1400, 1, PeakMb, SrcMb);
        Assert.True(tight.Shrunk);
        Assert.Equal(386, tight.BatchFrames);
        Assert.Equal(386, tight.MaxFramesByDisk);
        Assert.Contains("临时盘闸门", tight.Note);
    }

    /// <summary>地板优先:余量连"最小批"都放不下时不再往下压(压到 1 帧/批也救不了整任务),
    /// 并如实写进 Note —— 那种情形该由"整任务守门"去报错。</summary>
    [Fact]
    public void Gate_never_goes_below_the_floor()
    {
        // 余量 0.05GB × 0.65 = 33MB → 只能放 19 帧,低于下界 80
        var g = RenderPolicy.LimitBatchByTempDisk(1400, 0.05, PeakMb, SrcMb);
        Assert.True(g.Shrunk);
        Assert.Equal(RenderPolicy.WeakDeviceFramesPerBatch, g.BatchFrames);
        Assert.Equal(19, g.MaxFramesByDisk);
        Assert.Contains("已保地板", g.Note);
        // 自定义地板也认
        var g2 = RenderPolicy.LimitBatchByTempDisk(1400, 0.05, PeakMb, SrcMb, floorFrames: 30);
        Assert.Equal(30, g2.BatchFrames);
    }

    /// <summary>拿不到余量/单帧体积 ⇒ **不猜**:原样返回并说明"没做这道闸门"。</summary>
    [Fact]
    public void Gate_is_skipped_when_inputs_are_unavailable()
    {
        var noFree = RenderPolicy.LimitBatchByTempDisk(1400, 0, PeakMb, SrcMb);
        Assert.Equal(1400, noFree.BatchFrames);
        Assert.False(noFree.Shrunk);
        Assert.Contains("未做这道闸门", noFree.Note);
        var noFrame = RenderPolicy.LimitBatchByTempDisk(1400, 10, 0, SrcMb);
        Assert.Equal(1400, noFrame.BatchFrames);
        Assert.Contains("未做这道闸门", noFrame.Note);
        var bad = RenderPolicy.LimitBatchByTempDisk(0, 10, PeakMb, SrcMb);
        Assert.Contains("批大小无效", bad.Note);
    }

    /// <summary>口径必须与 TempSpaceEstimate 的"每批并存帧"那一项**同一个数**(一处改了另一处不许漂)。</summary>
    [Fact]
    public void Gate_uses_the_same_per_batch_accounting_as_the_space_estimate()
    {
        const int frames = 400;
        var g = RenderPolicy.LimitBatchByTempDisk(frames, 100, PeakMb, SrcMb);   // 余量足够 → 不缩
        Assert.Equal(frames, g.BatchFrames);
        // NeedBytes 的 perBatch 项 = batchFrames × (峰值帧 + 源帧) MB(单位 MB,与 g.BatchGB 同口径)
        double perBatchMb = g.BatchGB * 1024;
        Assert.Equal(frames * (PeakMb + SrcMb), perBatchMb, 6);
    }

    /// <summary>用户条件本身:**闸门之后的批**绝不超过"余量 × 0.65"(除非被地板顶住 —— 那种情形已如实标注)。</summary>
    [Theory]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(4.0)]
    [InlineData(32.0)]
    [InlineData(500.0)]
    public void After_the_gate_the_batch_fits_in_the_budget(double freeGb)
    {
        var g = RenderPolicy.LimitBatchByTempDisk(1400, freeGb, PeakMb, SrcMb);
        if (g.MaxFramesByDisk >= RenderPolicy.WeakDeviceFramesPerBatch)
            Assert.True(g.BatchGB <= g.BudgetGB + 1e-9,
                $"free={freeGb}GB:批 {g.BatchGB:0.##}GB 超过额度 {g.BudgetGB:0.##}GB");
    }

    /// <summary>余量越大,允许的帧数不减(单调),且闸门后的批不超过传入值。</summary>
    [Fact]
    public void Gate_is_monotonic_in_free_space_and_never_grows_the_batch()
    {
        int prev = 0;
        foreach (double free in new[] { 0.05, 0.2, 0.5, 1, 2, 5, 10, 50, 200 })
        {
            var g = RenderPolicy.LimitBatchByTempDisk(1400, free, PeakMb, SrcMb);
            Assert.True(g.BatchFrames <= 1400);
            Assert.True(g.MaxFramesByDisk >= prev, $"余量 {free}GB 的上限比更小的余量还低");
            prev = g.MaxFramesByDisk;
        }
    }

    /// <summary>比例常数就是用户给的 0.65(改它必须显式改测试)。</summary>
    [Fact]
    public void Share_limit_is_exactly_the_user_specification()
    {
        Assert.Equal(0.65, RenderPolicy.BatchTempDiskShareLimit, 6);
    }

    /// <summary>接线契约:VideoService 必须在**批大小定稿处**过这道闸门,而且闸门后的大小要进既有的
    /// 峰值守门复算(否则档位翻倍后守门算的还是旧批大小)。</summary>
    [Fact]
    public void VideoService_applies_the_gate_before_the_peak_guard()
    {
        var code = StripComments(ReadRepoFile("ImgUpscalerUI", "VideoService.cs"));
        int gate = code.IndexOf("AlhPro.Core.RenderPolicy.LimitBatchByTempDisk(batchSize, tempFreeGB, outFrameMB, srcFrameMB)", StringComparison.Ordinal);
        int guard = code.IndexOf("NeedBytesForBatch(\n                        peakFrames, outFrameMB, batchSize, srcFrameMB)", StringComparison.Ordinal);
        Assert.True(gate > 0, "定稿处必须调用临时盘闸门");
        Assert.True(guard > gate, "闸门必须在峰值守门复算之前(守门要用闸门后的大小)");
        Assert.Contains("batchSize = diskGate.BatchFrames;", code);
        // 粗判那一道也要过同一个闸门(否则会出现"粗判按 1400 帧算出要 30GB 直接不让跑")
        Assert.Contains("var gateNow = AlhPro.Core.RenderPolicy.LimitBatchByTempDisk(perfBatchFrames, freeNowGb, outFrameMB, srcFrameMB);", code);
        Assert.Contains("perfBatchFrames = gateNow.BatchFrames;", code);
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
