using AlhPro.Core;
using System.Collections.Generic;
using Xunit;

namespace AlhPro.Tests;

/// <summary>视频管线纯函数单测(耗时估算/时长合并/VFR setpts)。</summary>
public class VideoPipelineTests
{
    // ---------- EstimateProcessSeconds ----------
    [Fact]
    public void Estimate_scales_with_resolution_and_interp()
    {
        // 720p 超分2x 无补帧
        double d720 = VideoPipeline.EstimateProcessSeconds(10, 30, 1280, 720, up: true, 2.0, "waifu2x", interp: false, 2, dedup: false, 0);
        // 4K 超分2x 无补帧:面积更大 → 更慢
        double d4k = VideoPipeline.EstimateProcessSeconds(10, 30, 3840, 2160, up: true, 2.0, "waifu2x", interp: false, 2, dedup: false, 0);
        Assert.True(d4k > d720, "4K 应比 720p 慢");
    }

    [Fact]
    public void Estimate_interp_increases_cost()
    {
        double noInterp = VideoPipeline.EstimateProcessSeconds(10, 30, 1280, 720, up: false, 1.0, "waifu2x", interp: false, 2, dedup: false, 0);
        double withInterp = VideoPipeline.EstimateProcessSeconds(10, 30, 1280, 720, up: false, 1.0, "waifu2x", interp: true, 2, dedup: false, 0);
        Assert.True(withInterp > noInterp, "补帧应更慢");
    }

    [Fact]
    public void Estimate_waifu2x_faster_than_realesrgan()
    {
        double waifu = VideoPipeline.EstimateProcessSeconds(10, 30, 1280, 720, up: true, 2.0, "waifu2x", interp: false, 2, dedup: false, 0);
        double esr = VideoPipeline.EstimateProcessSeconds(10, 30, 1280, 720, up: true, 2.0, "realesrgan", interp: false, 2, dedup: false, 0);
        Assert.True(waifu < esr, "waifu2x 单帧成本比 realesrgan 低");
    }

    [Fact]
    public void Estimate_weak_machine_slowdown()
    {
        double fast = VideoPipeline.EstimateProcessSeconds(10, 30, 1280, 720, up: true, 2.0, "waifu2x", interp: false, 2, dedup: false, 0, slowFactor: 1.0);
        double weak = VideoPipeline.EstimateProcessSeconds(10, 30, 1280, 720, up: true, 2.0, "waifu2x", interp: false, 2, dedup: false, 0, slowFactor: 6.0);
        Assert.True(weak > fast * 3, "弱机系数应显著放大估算");
    }

    // ---------- EstimateProcessSeconds:阶段顺序(1x/2x「超分 → 补帧」)----------
    // 【数值断言】不是"新比旧小"这种单调性断言 —— 必须钉住差额本身:
    //   省下的超分 = 源帧数 × 每帧超分成本(旧顺序超分跑 2×源帧、新顺序只跑源帧 → 省一半)
    //   多出的补帧 = 新增帧数 × 0.09 × 面积 × (scale²-1)(新顺序补帧在放大后的帧上做)
    [Fact]
    public void Estimate_upscaleFirst_cheaper_at_2x_with_exact_delta()
    {
        double oldWay = VideoPipeline.EstimateProcessSeconds(10, 30, 1920, 1080,
            up: true, 2.0, "waifu2x", interp: true, 2, dedup: false, 0);
        double newWay = VideoPipeline.EstimateProcessSeconds(10, 30, 1920, 1080,
            up: true, 2.0, "waifu2x", interp: true, 2, dedup: false, 0, upscaleFirst: true);
        Assert.True(newWay < oldWay, "2x + 补帧:新顺序(超分→补帧)必须比旧顺序便宜");
        double src = 300;               // 10s × 30fps
        double areaN = 1.0;             // 1920×1080 = 基准面积
        double per = 0.18 * areaN * 2;  // waifu2x 2x 每帧超分成本
        // 旧:补帧 src×0.09×areaN + 超分 2src×per;新:超分 src×per + 补帧 src×0.09×areaN×4
        double expectDelta = (src * per - 3 * src * 0.09 * areaN) * 1.15;   // 收尾统一 ×1.15(略保守)
        Assert.Equal(expectDelta, oldWay - newWay, 3);
    }

    [Fact]
    public void Estimate_upscaleFirst_is_noop_without_interp_or_upscale()
    {
        // 不补帧 / 不超分时,阶段顺序没有意义 → 两个 flag 取值必须给出完全相同的估算(防止顺手多算一笔)
        double noInterpA = VideoPipeline.EstimateProcessSeconds(10, 30, 1920, 1080, up: true, 2.0, "waifu2x", interp: false, 2, dedup: false, 0);
        double noInterpB = VideoPipeline.EstimateProcessSeconds(10, 30, 1920, 1080, up: true, 2.0, "waifu2x", interp: false, 2, dedup: false, 0, upscaleFirst: true);
        Assert.Equal(noInterpA, noInterpB, 9);
        double noUpA = VideoPipeline.EstimateProcessSeconds(10, 30, 1920, 1080, up: false, 2.0, "waifu2x", interp: true, 2, dedup: false, 0);
        double noUpB = VideoPipeline.EstimateProcessSeconds(10, 30, 1920, 1080, up: false, 2.0, "waifu2x", interp: true, 2, dedup: false, 0, upscaleFirst: true);
        Assert.Equal(noUpA, noUpB, 9);
    }

    [Fact]
    public void Estimate_upscaleFirst_clamped_back_above_2x()
    {
        // 4x(及任何 scale>2.001)在管线里保持旧顺序「补帧 → 超分」,估算也必须钳回旧口径 ——
        // 否则调用方传错倍数时 ETA 会按一条【实际不会执行】的快路径算,预计时间会偏乐观。
        double oldWay = VideoPipeline.EstimateProcessSeconds(10, 30, 1920, 1080, up: true, 4.0, "realesrgan", interp: true, 2, dedup: false, 0);
        double misused = VideoPipeline.EstimateProcessSeconds(10, 30, 1920, 1080, up: true, 4.0, "realesrgan", interp: true, 2, dedup: false, 0, upscaleFirst: true);
        Assert.Equal(oldWay, misused, 9);
        // 2.001 以内仍按新顺序生效(边界值 2.0 生效)
        double at2Old = VideoPipeline.EstimateProcessSeconds(10, 30, 1920, 1080, up: true, 2.0, "realesrgan", interp: true, 2, dedup: false, 0);
        double at2New = VideoPipeline.EstimateProcessSeconds(10, 30, 1920, 1080, up: true, 2.0, "realesrgan", interp: true, 2, dedup: false, 0, upscaleFirst: true);
        Assert.True(at2New != at2Old, "2.0x 边界必须真的按新顺序算");
    }

    // ---------- EstimateProcessSeconds:每批引擎启动开销(2026-09-13 新增)----------
    // 【为什么必须钉住】原公式只有"每帧成本",完全不知道有【批次】这回事:长素材被切成十几批、
    // 每批重启一次引擎进程,这笔固定开销一分钱没算 → "预计还剩 5 分钟"实际跑半小时。
    // 口径必须与真正执行时同源:批数一律走 RenderPolicy.PlanVideoBatches。

    [Fact]
    public void Estimate_adds_batch_startup_overhead_when_free_ram_known()
    {
        const double perBatch = 3.0;   // = VideoPipeline.AssumedEngineStartupSecondsPerBatch(待实测标定)
        // 10s×30fps = 300 帧,空闲内存 10.4G → 每批 240 → 2 批(旧公式完全不含这 2 次引擎启动)
        double withRam = VideoPipeline.EstimateProcessSeconds(10, 30, 1920, 1080, up: true, 2.0, "waifu2x",
            interp: false, 2, dedup: false, 0, freeRamGB: 10.4);
        double noRam = VideoPipeline.EstimateProcessSeconds(10, 30, 1920, 1080, up: true, 2.0, "waifu2x",
            interp: false, 2, dedup: false, 0);
        Assert.Equal(VideoPipeline.AssumedEngineStartupSecondsPerBatch, perBatch, 9);   // 常数若被改动,本测试必须一起改
        Assert.Equal(2 * perBatch * 1.15, withRam - noRam, 6);   // 收尾统一 ×1.15(与既有口径一致)
    }

    [Fact]
    public void Estimate_short_clip_counts_exactly_one_batch()
    {
        const double perBatch = 3.0;
        // 5s×30fps = 150 帧 ≤ 单批上限 → 1 批(这正是"短素材不为几十帧反复启动引擎"的收益)
        double withRam = VideoPipeline.EstimateProcessSeconds(5, 30, 1920, 1080, up: true, 2.0, "waifu2x",
            interp: false, 2, dedup: false, 0, freeRamGB: 10.4);
        double noRam = VideoPipeline.EstimateProcessSeconds(5, 30, 1920, 1080, up: true, 2.0, "waifu2x",
            interp: false, 2, dedup: false, 0);
        Assert.Equal(1 * perBatch * 1.15, withRam - noRam, 6);
    }

    [Fact]
    public void Estimate_batch_overhead_grows_with_batch_count()
    {
        // 长素材批数多 → 加回的开销也更多(单调),而不是一个固定常数
        double shortClip = VideoPipeline.EstimateProcessSeconds(5, 30, 1920, 1080, up: true, 2.0, "waifu2x",
            interp: false, 2, dedup: false, 0, freeRamGB: 10.4)
            - VideoPipeline.EstimateProcessSeconds(5, 30, 1920, 1080, up: true, 2.0, "waifu2x",
            interp: false, 2, dedup: false, 0);
        // 60s×30fps = 1800 帧 → 8 批(⌈1800/240⌉)
        double longClip = VideoPipeline.EstimateProcessSeconds(60, 30, 1920, 1080, up: true, 2.0, "waifu2x",
            interp: false, 2, dedup: false, 0, freeRamGB: 10.4)
            - VideoPipeline.EstimateProcessSeconds(60, 30, 1920, 1080, up: true, 2.0, "waifu2x",
            interp: false, 2, dedup: false, 0);
        Assert.Equal(8 * VideoPipeline.AssumedEngineStartupSecondsPerBatch * 1.15, longClip, 6);
        Assert.True(longClip > shortClip, "批数多的长素材必须比短素材摊到更多启动开销");
    }

    [Fact]
    public void Estimate_no_batch_overhead_without_free_ram_or_upscale()
    {
        // 没传空闲内存(= 老调用方)= 不猜内存档、不加批次开销,与改动前逐字一致
        double a = VideoPipeline.EstimateProcessSeconds(60, 30, 1920, 1080, up: true, 2.0, "waifu2x", interp: false, 2, dedup: false, 0);
        double b = VideoPipeline.EstimateProcessSeconds(60, 30, 1920, 1080, up: true, 2.0, "waifu2x", interp: false, 2, dedup: false, 0, freeRamGB: 0);
        Assert.Equal(a, b, 9);
        // 不超分就没有超分批 → 传了内存也不加钱(不虚报)
        double noUpA = VideoPipeline.EstimateProcessSeconds(60, 30, 1920, 1080, up: false, 1.0, "waifu2x", interp: false, 2, dedup: false, 0);
        double noUpB = VideoPipeline.EstimateProcessSeconds(60, 30, 1920, 1080, up: false, 1.0, "waifu2x", interp: false, 2, dedup: false, 0, freeRamGB: 10.4);
        Assert.Equal(noUpA, noUpB, 9);
    }

    // ---------- MergeDurations ----------
    [Fact]
    public void MergeDurations_merges_dropped_frames_into_previous_kept()
    {
        // 4 帧,各 1s;删第 2、3 帧 → 第1帧时长 = 1+1+1 = 3s,保留第4 帧
        var durs = new List<double> { 1.0, 1.0, 1.0, 1.0 };
        VideoPipeline.MergeDurations(durs, new[] { 2, 3 }, 4);
        Assert.Equal(3.0, durs[0], 5);   // 1+1+1
        Assert.Equal(1.0, durs[1], 5);   // 原第 4 帧
        Assert.Equal(2, durs.Count);
    }

    [Fact]
    public void MergeDurations_keeps_tail_protected_frame()
    {
        // 删尾帧(第4)→ 时长并入前面保留帧;但尾帧保护在调用方处理,这里只验基础合并
        var durs = new List<double> { 1.0, 1.0, 1.0, 1.0 };
        VideoPipeline.MergeDurations(durs, new[] { 4 }, 4);
        Assert.Equal(3, durs.Count);   // 删了 1 条
        Assert.Equal(2.0, durs[^1], 5); // 第3帧 + 第4帧
    }

    [Fact]
    public void MergeDurations_empty_drop_no_change()
    {
        var durs = new List<double> { 1.0, 2.0 };
        VideoPipeline.MergeDurations(durs, System.Array.Empty<int>(), 2);
        Assert.Equal(new List<double> { 1.0, 2.0 }, durs);
    }

    // ---------- BuildVfrSetptsExpr ----------
    [Fact]
    public void BuildVfrSetpts_expr_contains_setpts_and_tb()
    {
        var expr = VideoPipeline.BuildVfrSetptsExpr(new List<double> { 0.04, 0.04, 0.08 });
        Assert.NotNull(expr);
        Assert.StartsWith("setpts=(", expr);
        Assert.EndsWith(")/TB", expr);
    }

    [Fact]
    public void BuildVfrSetpts_too_many_segments_returns_null()
    {
        var many = new List<double>();
        for (int i = 0; i < 500; i++) many.Add(0.04 + (i % 10) * 0.001);   // 大量不同时长 → 段数>400
        Assert.Null(VideoPipeline.BuildVfrSetptsExpr(many));
    }

    [Fact]
    public void BuildVfrSetpts_empty_returns_null()
    {
        Assert.Null(VideoPipeline.BuildVfrSetptsExpr(new List<double>()));
    }

    // ---------- BuildVfrSetptsExpr:退化时长必须"回退 CFR",而不是死循环 ----------
    // 注意:下列用例在【修前】不是失败而是让测试进程挂死 —— 合并循环里的
    // Math.Abs(durs[i] - d) < 1e-5 在 d 为 NaN/±Inf 时恒为假 → i 永不推进、外层 while 永不退出、
    // segs 无界增长到 OOM,末尾的 catch 也拦不住(那不是异常)。所以是"先修代码、再加这些测试"。

    /// <summary>非有限时长(NaN/±Inf):整表回退 CFR。放在表首、表尾、单元素位置都要拦到(守卫必须扫全表)。</summary>
    [Fact]
    public void BuildVfrSetpts_non_finite_duration_returns_null()
    {
        Assert.Null(VideoPipeline.BuildVfrSetptsExpr(new List<double> { 0.04, 0.04, double.NaN, 0.08 }));
        Assert.Null(VideoPipeline.BuildVfrSetptsExpr(new List<double> { double.NaN }));
        Assert.Null(VideoPipeline.BuildVfrSetptsExpr(new List<double> { double.PositiveInfinity, 0.04 }));
        Assert.Null(VideoPipeline.BuildVfrSetptsExpr(new List<double> { 0.04, double.NegativeInfinity }));
    }

    /// <summary>非正时长(0/负数):同样是契约外输入,回退 CFR(不能进表达式,否则 PTS 会倒退)。</summary>
    [Fact]
    public void BuildVfrSetpts_non_positive_duration_returns_null()
    {
        Assert.Null(VideoPipeline.BuildVfrSetptsExpr(new List<double> { 0.04, 0.0, 0.04 }));
        Assert.Null(VideoPipeline.BuildVfrSetptsExpr(new List<double> { 0.0 }));
        Assert.Null(VideoPipeline.BuildVfrSetptsExpr(new List<double> { 0.04, -0.04 }));
    }

    /// <summary>守卫不能过紧:合法但极小的时长(VFR 素材 1/96000 帧间隔)必须照样出表达式,
    /// 否则真 VFR 素材会被这条守卫整段降级成 CFR。</summary>
    [Fact]
    public void BuildVfrSetpts_tiny_but_valid_durations_still_produce_expr()
    {
        var expr = VideoPipeline.BuildVfrSetptsExpr(
            new List<double> { 1.0 / 96000.0, 1.0 / 96000.0, 1.0 / 30.0 });

        Assert.NotNull(expr);
    }

    /// <summary>末段必须没有 lt(N,e) 上界:setpts 在滤镜链末尾、前面还有 minterpolate/fps 重采样,
    /// 送进来的帧数比时长表多 1 是常态 —— 若末段也带 `lt(N,durs.Count)`,多出来的那帧所有段项都为 0 →
    /// PTS=0(与首帧同刻),播放器会当作重复时间戳丢掉,成片末尾少一截。
    /// 同时钉住"非末段仍必须有上界",否则各段会重叠累加。</summary>
    [Fact]
    public void BuildVfrSetpts_last_segment_has_no_upper_bound()
    {
        // 4 帧(0.04/0.04/0.08/0.08)→ 2 段:段0 上界 2,段1 = 末段
        var expr = VideoPipeline.BuildVfrSetptsExpr(new List<double> { 0.04, 0.04, 0.08, 0.08 });
        Assert.NotNull(expr);

        var parts = expr!.Split(" + ");
        Assert.Equal(2, parts.Length);
        Assert.Contains("lt(N\\,2)*gte(N\\,0)", parts[0]);   // 非末段保留上界(天然不重叠)
        Assert.DoesNotContain("lt(N\\,", parts[1]);          // 末段无上界
        Assert.Contains("gte(N\\,2)", parts[1]);
        // 表尾之外那一帧(N = 4 = 表长)不能被任何 lt(N,4) 关在门外
        Assert.DoesNotContain("lt(N\\,4)", expr);
    }

    // ---------- IsVfrByRateRatio ----------
    // 下列帧率全部是打包版 ffprobe 的真实测量值,不是构想的数字:
    //   CFR 30fps 素材       r_frame_rate=30/1   avg_frame_rate=30/1
    //   合成突发型 VFR 素材  r_frame_rate=60/1   avg_frame_rate=5060/199(真值 253 帧 / 9.95s)
    //   真机录屏(用户诊断包)r≈96000           avg≈30
    [Fact]
    public void VfrRatio_cfr_source_is_not_vfr()
    {
        Assert.False(VideoPipeline.IsVfrByRateRatio(30, 30));
    }

    [Fact]
    public void VfrRatio_ntsc_film_23_976_is_not_vfr()
    {
        double r = 24000.0 / 1001.0;
        Assert.False(VideoPipeline.IsVfrByRateRatio(r, r));
    }

    [Fact]
    public void VfrRatio_phone_style_small_jitter_is_not_vfr()
    {
        // 普通手机 VFR 的轻微抖动(r=30 / avg=28 → 1.07)不该判成 VFR:判了就会改走
        // frameDurs + setpts 的输出时间轴口径,对基本均匀的素材是净损失。
        Assert.False(VideoPipeline.IsVfrByRateRatio(30, 28));
    }

    [Fact]
    public void VfrRatio_measured_bursty_vfr_is_vfr()
    {
        Assert.True(VideoPipeline.IsVfrByRateRatio(60, 5060.0 / 199.0));   // ≈2.36
    }

    [Fact]
    public void VfrRatio_screen_recording_is_vfr()
    {
        // 用户诊断包里那个录屏:ffmpeg 按 r_frame_rate 铺 CFR 栅格 → 预估 917 帧对约 290 万实际帧。
        Assert.True(VideoPipeline.IsVfrByRateRatio(96000, 30));
    }

    [Fact]
    public void VfrRatio_missing_rate_makes_no_decision()
    {
        // 任一帧率缺失/非法 → 不作判定,交给逐帧 PTS 抽查那一路;不能因除零或 0 比值就误判。
        Assert.False(VideoPipeline.IsVfrByRateRatio(0, 30));
        Assert.False(VideoPipeline.IsVfrByRateRatio(60, 0));
        Assert.False(VideoPipeline.IsVfrByRateRatio(0, 0));
        Assert.False(VideoPipeline.IsVfrByRateRatio(-1, 30));
        Assert.False(VideoPipeline.IsVfrByRateRatio(60, -1));
    }

    [Fact]
    public void VfrRatio_threshold_boundary()
    {
        double t = VideoPipeline.VfrRateRatioThreshold;
        Assert.True(VideoPipeline.IsVfrByRateRatio(t * 30, 30));            // 正好达门槛 → 判 VFR
        Assert.False(VideoPipeline.IsVfrByRateRatio(t * 30 * 0.999, 30));   // 略低于门槛 → 不判
    }

    [Fact]
    public void VfrRatio_threshold_is_calibrated_between_jitter_and_real_vfr()
    {
        // 门槛标定:必须高于所有"正常/轻微抖动"的实测比值,且低于所有"真 VFR"的实测比值。
        // 改高了漏判录屏(拆帧帧数爆炸),改低了误伤手机素材(输出时间轴口径被改)——两头都有代价,
        // 所以把两侧余量钉在测试里,以后动门槛会先撞上这条。
        double t = VideoPipeline.VfrRateRatioThreshold;
        double maxNormal = System.Math.Max(30.0 / 30.0, 30.0 / 28.0);   // 1.0714
        double minRealVfr = 60.0 / (5060.0 / 199.0);                    // 2.3597
        Assert.True(t > maxNormal, $"门槛 {t} 必须 > 正常素材最大实测比值 {maxNormal:0.###}");
        Assert.True(t < minRealVfr, $"门槛 {t} 必须 < 真 VFR 最小实测比值 {minRealVfr:0.###}");
    }
}
