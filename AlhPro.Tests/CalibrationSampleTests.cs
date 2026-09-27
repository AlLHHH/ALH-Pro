using AlhPro.Core;
using System;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【2026-09-25 A+B】两点法(定标数学)与采样帧数选择的单测(契约 A2)。
///
/// 为什么不能"跑一次目录批跑取总时长÷帧数":引擎每**进程**有固定地板(启动 + 模型加载 + 首次着色器/管线创建),
/// 本仓库实测地板约为单帧成本的 **3 倍**(1 帧目录 1.01~1.19 s/帧 vs 40 帧目录 0.25~0.30 s/帧)。
/// 两点法把这部分**减掉**:`p = (tN − t1)/(N − 1)`、`F = t1 − p`。</summary>
public class CalibrationSampleTests
{
    private const long Px1080 = 1920L * 1080;

    /// <summary>用自洽的数验证公式:`F + i·p` 造出的两次耗时,必须精确还原 p 与 F。</summary>
    [Theory]
    [InlineData(4, 0.9, 1.65)]
    [InlineData(6, 0.9, 1.65)]
    [InlineData(8, 1.26, 0.0807)]
    [InlineData(4, 0.75, 15.87)]
    [InlineData(2, 0.5, 3.0)]
    public void Two_point_math_recovers_p_and_the_floor_exactly(int n, double floor, double per)
    {
        double t1 = floor + per;
        double tN = floor + n * per;
        Assert.Equal(per, CalibrationSample.TwoPointPerFrame(n, t1, tN), 9);
        Assert.Equal(floor, CalibrationSample.TwoPointFloor(n, t1, tN), 9);
    }

    /// <summary>退化/非法输入不许抛、也不许算出 NaN:返回 0(由 `LocalPriceBook.TryBuild` 负责拒收)。</summary>
    [Theory]
    [InlineData(1, 0.9, 10.0)]                     // N<2:除零
    [InlineData(0, 0.9, 10.0)]
    [InlineData(-3, 0.9, 10.0)]
    [InlineData(6, double.NaN, 10.0)]
    [InlineData(6, 0.9, double.PositiveInfinity)]
    public void Two_point_math_is_safe_on_degenerate_input(int n, double t1, double tN)
        => Assert.Equal(0, CalibrationSample.TwoPointPerFrame(n, t1, tN));

    /// <summary>采样帧数 N 的选择:便宜的模型多采几帧、贵的少采,总等待被 30 秒量级框住,
    /// 且**永不**低于 <see cref="LocalPriceBook.MinSampleFrames"/>(1~3 帧的差值不可信)、不高于 8。
    /// 【2026-09-27 提速】贵档(每帧 ≥ <see cref="CalibrationSample.ExpensivePerFrame"/>)再按 4 封顶;
    /// 便宜档保持 8 —— 降了它差值过不了相对噪声门槛,反而白标一场(下面 0.028 那一格就是反例)。</summary>
    [Fact]
    public void FramesFor_keeps_the_budget_and_respects_the_bounds()
    {
        Assert.Equal(CalibrationSample.MinFrames, CalibrationSample.MinFrames);          // 4
        Assert.Equal(CalibrationSample.MinFrames, LocalPriceBook.MinSampleFrames);
        Assert.Equal(8, CalibrationSample.MaxFrames);
        Assert.Equal(4, CalibrationSample.MaxFramesExpensive);
        Assert.Equal(0.20, CalibrationSample.ExpensivePerFrame, 9);
        Assert.Equal(30.0, CalibrationSample.BudgetSeconds, 9);

        // 贵档(≥0.20 s/帧):封顶 4 帧 —— 作者实测 Real-CUGAN 2x 从 1+8(14.2 s)降到 1+4(约 8 s)
        Assert.Equal(4, CalibrationSample.FramesFor(1.65, Px1080));                       // Real-CUGAN 2x:原先 8
        Assert.Equal(4, CalibrationSample.FramesFor(5.0, Px1080));                        // 30/5=6 → 贵档封顶 4
        Assert.Equal(4, CalibrationSample.FramesFor(15.87, Px1080));                      // 30/15.87=1 → 抬到下限 4
        Assert.Equal(4, CalibrationSample.FramesFor(0.20, Px1080));                       // 门槛边界:含等于
        // 便宜档(<0.20 s/帧):仍用满 8(差值要靠帧数撑过噪声门槛)
        Assert.Equal(8, CalibrationSample.FramesFor(0.19, Px1080));
        Assert.Equal(8, CalibrationSample.FramesFor(0.028, Px1080));                      // 最便宜的 1x 档:必须 8
        Assert.Equal(CalibrationSample.MaxFrames, CalibrationSample.FramesFor(0, Px1080));      // 估值非法 → 上限
        Assert.Equal(CalibrationSample.MaxFrames, CalibrationSample.FramesFor(double.NaN, Px1080));
        Assert.Equal(CalibrationSample.MaxFrames, CalibrationSample.FramesFor(-1, Px1080));
        // 预算非法 ⇒ 用内置预算;min/max 反了 ⇒ 按 min 处理(不许返回一个比下限还小的值;贵档封顶也不许压破 min)
        Assert.Equal(4, CalibrationSample.FramesFor(1.65, Px1080, budgetSeconds: 0));
        Assert.Equal(6, CalibrationSample.FramesFor(1.65, Px1080, min: 6, max: 4));
        Assert.Equal(6, CalibrationSample.FramesFor(1.65, Px1080, min: 6));
    }

    /// <summary>【2026-09-27 提速的**安全前提**】4 帧样本在贵档上仍必须过 <see cref="CalibrationSample.MinDeltaRatio"/>
    /// 那道相对噪声门槛 —— 否则"少等 6 秒"换来的是"标定被拒收、保守走旧顺序",那就白改了。
    /// 用两点法的自洽模型(`t1 = F + p`、`t4 = F + 4p`)代入真实量级算比值。</summary>
    [Theory]
    [InlineData(0.75, 1.65)]     // Real-CUGAN 2x(作者机器:地板 ~0.75~0.92,每帧 1.3202 实测)
    [InlineData(0.92, 1.3202)]
    [InlineData(0.90, 0.50)]     // 贵档里的下沿:0.5 s/帧
    [InlineData(0.90, 0.20)]     // 门槛本身
    public void Four_frame_sample_still_clears_the_noise_ratio(double floor, double per)
    {
        double t1 = floor + per;
        double t4 = floor + 4 * per;
        Assert.True(t4 - t1 >= CalibrationSample.MinDeltaRatio * t1,
            $"地板 {floor}、每帧 {per}:4 帧差值 {(t4 - t1):0.###} 必须 ≥ 门槛 {CalibrationSample.MinDeltaRatio * t1:0.###}");
    }

    /// <summary>估值走**内置表(他机资料)+ 面积折算**:Real-CUGAN 2x 在 1080p 上是 1.65 s/帧,
    /// 4K 素材上按面积 ×4;查不到的模型回落到 1.0 s/帧(偏悲观,但只用于选帧数、不参与判据)。</summary>
    [Fact]
    public void EstimatePerFrame_uses_the_builtin_table_with_area_scaling()
    {
        Assert.Equal(1.65, CalibrationSample.EstimatePerFrame("models-se:0", 2, Px1080), 6);
        Assert.Equal(1.65 * 4, CalibrationSample.EstimatePerFrame("models-se:0", 2, Px1080 * 4), 6);
        // 没有实测的倍率(Real-CUGAN 4x)⇒ 回落值 1.0
        Assert.Equal(1.0, CalibrationSample.EstimatePerFrame("models-se:0", 4, Px1080), 6);
        Assert.Equal(1.0, CalibrationSample.EstimatePerFrame("some-new-model", 4, Px1080), 6);
        Assert.Equal(2.5, CalibrationSample.EstimatePerFrame("some-new-model", 4, Px1080, fallbackPerFrame1080p: 2.5), 6);
    }

    /// <summary>面积折算的 1080p 化:1080p 原样、2160p ÷4(与 `LocalPrice.SecondsPerFrame1080p` 同一口径)。</summary>
    [Fact]
    public void To1080p_matches_the_record_conversion()
    {
        Assert.Equal(1.65, CalibrationSample.To1080p(1.65, Px1080), 9);
        Assert.Equal(1.65, CalibrationSample.To1080p(6.6, Px1080 * 4), 9);
        Assert.Equal(1.65, CalibrationSample.To1080p(1.65, 0), 9);      // 面积非法:原样返回
        Assert.Equal(1.65, LocalPriceFixture.AtPixels("models-se:0", 2, 6.6, Px1080 * 4).SecondsPerFrame1080p, 9);
    }

    /// <summary>采样下标:等间隔、**严格递增不重复**、优先取中段(避开片头黑场/片尾定格);
    /// 素材太短时如实返回"能取到几张"(由调用方报「素材帧数不足」)。</summary>
    [Fact]
    public void SampleIndices_are_strictly_increasing_and_prefer_the_middle()
    {
        var mid = CalibrationSample.SampleIndices(100, 9);
        Assert.Equal(9, mid.Count);
        Assert.Equal(mid.OrderBy(i => i), mid);                      // 升序
        Assert.Equal(mid.Distinct().Count(), mid.Count);             // 不重复
        Assert.True(mid[0] >= 25 && mid[^1] <= 74, $"应优先取中段,实际 {mid[0]}~{mid[^1]}");
        Assert.True(mid[0] > 0 && mid[^1] < 99);                     // 片头/片尾各留出四分之一

        // 只有 5 帧、要 9 帧 ⇒ 返回全部 5 帧(不重复;调用方按"不足"处理)
        var few = CalibrationSample.SampleIndices(5, 9);
        Assert.Equal(5, few.Count);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, few);

        // 中段放不下 ⇒ 退回全片等间隔
        var shortButOk = CalibrationSample.SampleIndices(10, 9);
        Assert.Equal(9, shortButOk.Count);
        Assert.Equal(shortButOk.OrderBy(i => i), shortButOk);
        Assert.Equal(shortButOk.Distinct().Count(), shortButOk.Count);

        // 单帧 / 非法输入
        Assert.Equal(new[] { 50 }, CalibrationSample.SampleIndices(100, 1));
        Assert.Empty(CalibrationSample.SampleIndices(0, 9));
        Assert.Empty(CalibrationSample.SampleIndices(10, 0));
        Assert.Empty(CalibrationSample.SampleIndices(-5, 9));

        // 抖动(四舍五入撞在一起)也不许出现重复下标
        for (int frames = 5; frames <= 40; frames++)
            for (int need = 4; need <= 9; need++)
            {
                var idx = CalibrationSample.SampleIndices(frames, need);
                Assert.Equal(idx.Distinct().Count(), idx.Count);
                Assert.All(idx, i => Assert.InRange(i, 0, frames - 1));
            }
    }

    // ═══════════════ 【2026-09-25 修订 · F3】相对噪声门槛 ═══════════════

    /// <summary>门槛是**相对**的(占"1 帧那次耗时"的比例),不是一个绝对秒数 —— 抖动量级随机器/温度走。
    /// 0.15 的取法:两次进程启动的地板离散实测 ~23%(0.75→0.92s),门槛取同一量级;常用档都不误杀
    /// (Real-CUGAN 2x 比值 5.5、animevideov3 2x 2.6、最便宜的 1x 档 1.21)。</summary>
    [Fact]
    public void MinDeltaRatio_is_a_relative_noise_gate()
    {
        Assert.Equal(0.15, CalibrationSample.MinDeltaRatio, 9);
        Assert.True(CalibrationSample.MinDeltaRatio > 0 && CalibrationSample.MinDeltaRatio < 1);
        // 契约点名的反例:t1=0.50 / tN=0.55 / N=4 ⇒ 差值 0.05 只有 t1 的 10% < 15% ⇒ 不达门槛
        double delta = 0.55 - 0.50;
        Assert.True(delta < CalibrationSample.MinDeltaRatio * 0.50);
        // 常用档都过线(不误杀)
        Assert.True((0.9 + 8 * 1.65) - (0.9 + 1.65) >= CalibrationSample.MinDeltaRatio * (0.9 + 1.65));   // Real-CUGAN 2x
        Assert.True((0.9 + 8 * 0.26) - (0.9 + 0.26) >= CalibrationSample.MinDeltaRatio * (0.9 + 0.26));   // animevideov3 2x
        Assert.True((0.9 + 8 * 0.028) - (0.9 + 0.028) >= CalibrationSample.MinDeltaRatio * (0.9 + 0.028)); // 最便宜的 1x 档
    }

    // ═══════════════ 【2026-09-25 修订 · F1-I3】样本输出体检(黑帧/坏帧/不可读 ⇒ 拒收) ═══════════════

    /// <summary>全好 ⇒ null(可落盘);几种坏法各给中文原因:
    /// 空目录 / 帧数对不上 / 没落地 / 0 字节空帧 / 被判黑(含带状坏帧)。</summary>
    [Fact]
    public void OutputDefect_rejects_black_broken_and_missing_frames()
    {
        static CalibrationSample.SampleOutput Ok() => new(Present: true, Bytes: 123456, Defective: false);

        Assert.Null(CalibrationSample.OutputDefect(new[] { Ok(), Ok() }, 2));      // 全好

        Assert.Contains("空的", CalibrationSample.OutputDefect(Array.Empty<CalibrationSample.SampleOutput>(), 2));
        Assert.Contains("帧数对不上", CalibrationSample.OutputDefect(new[] { Ok() }, 2));
        Assert.Contains("没落地",
            CalibrationSample.OutputDefect(new[] { Ok(), new CalibrationSample.SampleOutput(false, 0, false) }, 2));
        Assert.Contains("0 字节空帧",
            CalibrationSample.OutputDefect(new[] { Ok(), new CalibrationSample.SampleOutput(true, 0, false) }, 2));
        var black = CalibrationSample.OutputDefect(new[] { Ok(), new CalibrationSample.SampleOutput(true, 123456, true) }, 2);
        Assert.Contains("黑帧", black);
        Assert.Contains("既有判黑口径", black);      // 复用既有口径,不新造第二套
        Assert.Contains("期望帧数非法", CalibrationSample.OutputDefect(new[] { Ok() }, 0));
    }
}
