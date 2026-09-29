using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>
/// 帧质量判定(从 EngineService 的**事前探测**判黑抽出的阈值逻辑)的单测。
/// 这是出过回归的纯函数(commit 3ec571c),必须保护。
/// 【2026-09-27】原文件里还有"黑帧降级的防误杀判定"三只测试(ShouldExemptAsSourceBlack)——
/// 那套豁免只为"超分批黑帧降级链"服务,而该链已按作者要求整体删除(黑色转场被误判 ⇒ 转 ONNX 超级慢),
/// 判据本身也随之从 FrameInspect 删除 ⇒ 三只测试与判据一起下线(不是"为了过测试而删")。
/// 保留的四只钉的是**仍然在用**的阈值/采样几何:判黑口径没变,现在服务事前探测与单图守卫两端。
/// </summary>
public class FrameInspectTests
{
    [Fact]
    public void Near_black_detected()
    {
        // 全黑:RGB 和 <24,应判近黑
        var sums = new[] { 9, 9, 9, 9, 9, 9, 9, 9, 9, 9 };
        Assert.True(FrameInspect.IsNearBlack(sums, sums.Length));
    }

    [Fact]
    public void Ordinary_frame_not_black()
    {
        // 中等灰/彩色:RGB 和高于 24,不判黑
        var sums = new[] { 300, 300, 300, 300, 300, 300, 300, 300, 300, 300 };
        Assert.False(FrameInspect.IsNearBlack(sums, sums.Length));
    }

    [Fact]
    public void Threshold_at_95_percent()
    {
        // ≥95% 黑才判黑;90% 黑不判
        var n = 100;
        var ninetyDark = new int[n];
        for (int i = 0; i < 90; i++) ninetyDark[i] = 9;   // 黑
        for (int i = 90; i < n; i++) ninetyDark[i] = 300; // 非黑
        Assert.False(FrameInspect.IsNearBlack(ninetyDark, n));

        var ninetyFiveDark = new int[n];
        for (int i = 0; i < 95; i++) ninetyFiveDark[i] = 9;
        for (int i = 95; i < n; i++) ninetyFiveDark[i] = 300;
        Assert.True(FrameInspect.IsNearBlack(ninetyFiveDark, n));
    }

    [Fact]
    public void Empty_sample_not_black()
    {
        // 无采样点 → 不判黑(避免 1×1 小图误判)
        Assert.False(FrameInspect.IsNearBlack(System.Array.Empty<int>(), 0));
        Assert.False(FrameInspect.IsNearBlack(new[] { 9, 9 }, 0));
    }

    [Fact]
    public void Sample_step_for_tiny_images()
    {
        // 1×1 / 小图:步长至少 4,保证能取到部分像素
        Assert.Equal(4, FrameInspect.SampleStep(1, 1));
        Assert.Equal(4, FrameInspect.SampleStep(10, 10));
        Assert.Equal(4, FrameInspect.SampleStep(100, 100));
        // 大图:步长 = min(w,h)/32
        Assert.True(FrameInspect.SampleStep(1920, 1080) >= 33);
        Assert.True(FrameInspect.SampleStep(1920, 1080) <= 60);
    }

    // ===== 【2026-09-27 已删除】黑帧降级的"防误杀"判定(ShouldExemptAsSourceBlack)=====
    // 原契约:超分批被判黑时,"每一帧的对应源帧都近黑"才允许整批放行(防的是"存在量词"老 bug 把 GPU 真故障放行)。
    // 为什么契约不存在了:放行的对象——"超分批黑帧降级链"——已按作者要求整体删除(超分侧只重跑/换 ONNX 那套),
    // 于是这段豁免再没有调用方;随之从 AlhPro.Core.FrameInspect 删除判据本身。
    // 现有的等价保护:判黑只保留在**事前探测**与"单图/分块成品的自带源图豁免守卫"里,
    // 探测图由程序自造、单张静帧没有黑转场 ⇒ 两条路径都不存在"素材本来就黑"的误伤问题,
    // 因此"防误杀"这个概念在它们身上不需要 —— 视频批量路径上也没有任何生产代码会"因为素材黑而放行输出"。

    [Fact]
    public void ForEachSample_iterates_grid()
    {
        // 10×10 → 步长 4,采样点 x,y ∈ {4,8}:共 2×2=4 点
        int count = FrameInspect.ForEachSample(10, 10, (_, _) => { });
        Assert.Equal(4, count);

        // 8×8 → 步长 4,{4}:1×1=1 点
        count = FrameInspect.ForEachSample(8, 8, (_, _) => { });
        Assert.Equal(1, count);
    }
}
