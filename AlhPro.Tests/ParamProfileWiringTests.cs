using AlhPro.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【任务 W】把「在线参数(V)」的**其余字段真正接到决策函数上**的接线测试。
/// 【两条必须同时成立的硬要求,本文件逐条钉住】
///   ① **覆盖层为 null(未配置/离线/全部既有单测)→ 行为逐字不变**:
///      每一个接线点都有一条"null 时等于既有常量口径"的断言(含返回的 Plan/Decision 字段与日志串);
///   ② **配置了就真的改变判定**:超分单价表、补帧锚点表、顺序判定边际、时间轴缺口容差、切点三阈值,
///      每一项都有一条"设了覆盖层之后结果确实变了"的断言 —— 否则"接线"只是注释。
/// 【为什么要单开一个不并行的集合】本文件会改全局覆盖层 <see cref="ParamProfileRuntime"/>;
/// 与 <see cref="ParamProfileTests"/> 一样归入 <c>ParamProfileRuntimeGlobal</c>(xunit 对声明了
/// DisableParallelization 的集合不与其他集合并行),并且每个测试都在 finally 里还原为 null。</summary>
[Collection("ParamProfileRuntimeGlobal")]
public class ParamProfileWiringTests
{
    /// <summary>在一份在线 JSON 生效的前提下跑一段断言,跑完**无论如何**还原为 null(全局状态)。</summary>
    private static void WithOverlay(string json, Action body)
    {
        var res = ParamProfileParser.ParseAndValidate(json, null, "在线(单测)");
        try
        {
            ParamProfileRuntime.Set(res.Profile);
            body();
        }
        finally
        {
            ParamProfileRuntime.Set(null);
        }
    }

    // ==================== ① null = 逐字不变 ====================

    /// <summary>【接线点 5/5】切点三阈值:null 时 <see cref="SceneCutJudge.IsCut"/> 必须与"直接用常量算"逐点一致。</summary>
    [Fact]
    public void Null_overlay_scene_cut_judge_matches_the_constants_exactly()
    {
        Assert.Null(ParamProfileRuntime.Current);
        foreach (double diff in new[] { 0.0, 24.999, 25.0, 30.0, 49.999, 50.0, 58.68, 200.0 })
            foreach (double? p in new double?[] { null, 0.0, 1000.0 })
                foreach (double? c in new double?[] { null, 0.0, 474.0 })
                {
                    // 与既有三阈值常量逐个手算的期望值(不许因为接线而改变任何一个点)
                    bool expect;
                    if (!double.IsFinite(diff) || diff < SceneCutJudge.DiffThreshold) expect = false;
                    else if (diff >= SceneCutJudge.StrongDiffThreshold) expect = true;
                    else if (p is > 0 && c is >= 0) expect = c!.Value / p!.Value <= SceneCutJudge.LapDropRatio;
                    else expect = true;
                    Assert.Equal(expect, SceneCutJudge.IsCut(diff, p, c));
                }
        // 逐条外部参照:用户实测的 58.68 / lapvar 比 0.474 这一对必须仍然判为切点
        Assert.True(SceneCutJudge.IsCut(58.68, 7237, 3430));
        Assert.False(SceneCutJudge.IsCut(10.0, 7237, 100));
    }

    /// <summary>【接线点 4/5】时间轴缺口容差:null 时 <see cref="TimelineFlattenPlan.Decide"/> 的
    /// **每一个字段**都与"用常量算"一致(含 Reason 串 —— 日志逐字不变)。</summary>
    [Fact]
    public void Null_overlay_timeline_decide_is_field_for_field_identical()
    {
        Assert.Null(ParamProfileRuntime.Current);
        var withGaps = new double[] { 0.2, 0.2, 0.2, 0.5, 0.2, 0.2 };
        var noGaps = new double[] { 0.2, 0.2, 0.2, 0.2, 0.2, 0.2 };
        foreach (var durs in new[] { withGaps, noGaps })
            foreach (var (fps, k) in new[] { (24.0, 2), (24.0, 1), (0.0, 2) })
            {
                var got = TimelineFlattenPlan.Decide(durs, fps, k);
                // 期望值:照抄既有实现(容差用常量)
                double total = durs.Sum();
                var sorted = durs.ToList(); sorted.Sort();
                double med = sorted[sorted.Count / 2];
                int gaps = durs.Count(d => Math.Abs(d - med) / med > TimelineFlattenPlan.GapToleranceRatio);
                bool expectFlatten = k >= 2 && fps > 0 && gaps > 0;
                Assert.Equal(expectFlatten, got.Flatten);
                Assert.Equal(durs.Length, got.SourceFrames);
                Assert.Equal(expectFlatten ? gaps : (gaps == 0 ? 0 : 0), got.GapCount);
                if (expectFlatten)
                {
                    Assert.Equal(fps * k, got.TargetFps, 9);
                    Assert.Equal(Math.Max(2, (int)Math.Round(total * fps * k)), got.TargetFrames);
                    Assert.Equal(total, got.TotalSeconds, 9);
                    Assert.Equal("检出缺口", got.Reason);
                    Assert.Contains("填平", got.LogLine);
                }
                else
                {
                    Assert.Equal(0, got.TargetFrames);
                    Assert.DoesNotContain("填平", got.LogLine);
                }
            }
        Assert.Equal("间隔均匀(无缺口)", TimelineFlattenPlan.Decide(noGaps, 24.0, 2).Reason);
    }

    /// <summary>【接线点 3/5】顺序判定边际:null 时"省略边际"与"显式传 15.0"必须给出**同一个** Decision。</summary>
    [Fact]
    public void Null_overlay_order_margin_default_equals_explicit_builtin_value()
    {
        Assert.Null(ParamProfileRuntime.Current);
        Assert.Equal(PipelineOrderPlan.MinSavingsPercent, ParamProfileRuntime.OrderSwitchMinSavingsPercent, 9);
        var cost = new PipelineOrderPlan.CostInput("synthetic", 2, true, 0.70);
        var byDefault = PipelineOrderPlan.Decide(cost, 2.0, 2, 1920, 1080, 1800);
        var explicit15 = PipelineOrderPlan.Decide(cost, 2.0, 2, 1920, 1080, 1800, minSavingsPercent: 15.0);
        Assert.Equal(explicit15.UpscaleFirst, byDefault.UpscaleFirst);
        Assert.Equal(explicit15.MarginInsufficient, byDefault.MarginInsufficient);
        Assert.Equal(explicit15.SavingsPercent, byDefault.SavingsPercent, 9);
        Assert.Equal(explicit15.Reason, byDefault.Reason);
        Assert.Equal(explicit15.LogLine, byDefault.LogLine);
    }

    /// <summary>【接线点 1/5 + 2/5】单价表:null 时超分查表与补帧锚点表都必须是既有实测值。</summary>
    [Fact]
    public void Null_overlay_price_tables_return_the_measured_values()
    {
        Assert.Null(ParamProfileRuntime.Current);
        Assert.Equal(15.145, PipelineOrderPlan.LookupUpscaleSecondsPerFrame("realesrgan", "realesrgan-x4plus", 4, out var p1)!.Value, 6);
        Assert.Contains("12 帧样本", p1);      // 出处串仍是"内置实测",不是"在线参数"
        Assert.Equal(0.3685, PipelineOrderPlan.LookupUpscaleSecondsPerFrame(null, "models-cunet", 2, out _)!.Value, 6);

        var (px, sc) = PipelineOrderPlan.ResolveInterpAnchors();
        Assert.Same(PipelineOrderPlan.InterpAnchorPixels, px);   // 一个对象都没多分配:原样返回
        Assert.Same(PipelineOrderPlan.InterpAnchorSeconds, sc);
        Assert.Equal(0.0807, PipelineOrderPlan.InterpSecondsPerOutputFrame(1920L * 1080), 6);
        Assert.Equal(0.2776, PipelineOrderPlan.InterpSecondsPerOutputFrame(3840L * 2160), 6);
        Assert.Equal(0.5186, PipelineOrderPlan.InterpSecondsPerOutputFrame(7680L * 4320), 6);
        Assert.Equal(3, PipelineOrderPlan.ResolveInterpAnchors().Pixels.Length);   // 1080p / 2160p / 4320p 三个实测锚点
    }

    /// <summary>内置表**由实测表派生**(不允许存在第二份数字):这张断言专门防"以后有人又抄一份进去"。</summary>
    [Fact]
    public void BuiltIn_tables_are_derived_from_the_measured_constants()
    {
        var b = ParamProfile.BuiltIn;
        var expectUp = PipelineOrderPlan.BuiltInUpscaleMap();
        Assert.Equal(expectUp.Count, b.UpscaleSecondsPerFrame1080p.Count);
        foreach (var kv in expectUp)
            Assert.Equal(kv.Value, b.UpscaleSecondsPerFrame1080p[kv.Key], 9);
        // 旧内置那两行错数字(x4plus 0.24 / cunet 0.25)已作废,必须以实测为准
        Assert.Equal(15.145, b.UpscaleSecondsPerFrame1080p["realesrgan|x4plus|4"], 6);
        Assert.Equal(0.3685, b.UpscaleSecondsPerFrame1080p["waifu2x|cunet|2"], 6);
        Assert.False(b.UpscaleSecondsPerFrame1080p.ContainsKey("waifu2x|models-upconv_7_anime_style_art_rgb|2"));

        var expectIp = PipelineOrderPlan.BuiltInInterpMap();
        Assert.Equal(expectIp.Count, b.InterpSecondsPerFrame1080p.Count);
        foreach (var kv in expectIp)
            Assert.Equal(kv.Value, b.InterpSecondsPerFrame1080p[kv.Key], 9);
        Assert.Equal(0.0807, b.InterpSecondsPerFrame1080p["1080p"], 6);
        Assert.Equal(0.2776, b.InterpSecondsPerFrame1080p["2160p"], 6);
        Assert.Equal(0.5186, b.InterpSecondsPerFrame1080p["4320p"], 6);
        Assert.False(b.InterpSecondsPerFrame1080p.ContainsKey("1440p"));   // 未实测 → 不写死
        Assert.Equal(PipelineOrderPlan.MinSavingsPercent, b.OrderSwitchMinSavingsPercent, 9);
    }

    // ==================== ② 配置了必须真的改变判定 ====================

    /// <summary>超分单价表:在线值生效;两种键写法(模型键 / 引擎侧全名)都能命中;查不到的组合仍回落内置。</summary>
    [Theory]
    [InlineData("realesrgan|x4plus|4")]
    [InlineData("realesrgan|realesrgan-x4plus|4")]     // 引擎侧全名 → 归一后等价
    [InlineData("realesrgan|RealESRGAN-x4Plus|4")]     // 大小写/引擎侧写法混着写也认
    public void Online_upscale_price_overrides_and_key_is_normalized(string key)
    {
        string json = "{\"version\":\"w1\",\"upscaleSecondsPerFrame1080p\":{\"" + key + "\":0.5}}";
        WithOverlay(json, () =>
        {
            Assert.Equal(0.5, PipelineOrderPlan.LookupUpscaleSecondsPerFrame("realesrgan", "realesrgan-x4plus", 4, out var prov)!.Value, 9);
            Assert.Contains("在线参数", prov);
            Assert.Contains("w1", prov);
            // 判定真的跟着变:u 的具体数字必须从实测的 15.145 变成在线的 0.5(接线生效的最直接证据)
            var d = PipelineOrderPlan.Decide("realesrgan", "realesrgan-x4plus", 4.0, 2, 1920, 1080, 1800);
            Assert.Equal(0.5, d.UpscalePerFrame, 9);
            // 没被覆盖的模型/倍率照旧走内置实测表
            Assert.Equal(0.297, PipelineOrderPlan.LookupUpscaleSecondsPerFrame("realesrgan", "realesr-animevideov3", 4, out _)!.Value, 6);
            // 内置表里没有的组合,在线表也没有 → 仍然是"未实测"(不许被在线表"洗白")
            Assert.Null(PipelineOrderPlan.LookupUpscaleSecondsPerFrame("realesrgan", "realesr-animevideov3", 3, out _));
        });
    }

    /// <summary>超分单价低到跨过门槛 → 阶段顺序判定真的翻转(接线点 1/5 的端到端证据)。</summary>
    [Fact]
    public void Online_upscale_price_can_flip_the_stage_order()
    {
        var before = PipelineOrderPlan.Decide("realesrgan", "realesrgan-x4plus", 4.0, 2, 1920, 1080, 1800);
        Assert.True(before.UpscaleFirst);                              // 15.145s/帧 → 先超分
        WithOverlay("""{"version":"w2","upscaleSecondsPerFrame1080p":{"realesrgan|x4plus|4":0.05}}""", () =>
        {
            var after = PipelineOrderPlan.Decide("realesrgan", "realesrgan-x4plus", 4.0, 2, 1920, 1080, 1800);
            Assert.Equal(0.05, after.UpscalePerFrame, 9);
            Assert.False(after.UpscaleFirst);                          // 变得便宜 → 回到旧顺序
            Assert.Equal(0.05, after.UpscalePerFrame, 9);
        });
        // 还原之后再判一次:必须逐字回到覆盖前
        var again = PipelineOrderPlan.Decide("realesrgan", "realesrgan-x4plus", 4.0, 2, 1920, 1080, 1800);
        Assert.Equal(before.LogLine, again.LogLine);
    }

    /// <summary>补帧锚点表:被覆盖的锚点换值,**没被覆盖的锚点保持实测值**;1440p 只在被给到时才插入。</summary>
    [Fact]
    public void Online_interp_anchors_replace_only_the_overridden_anchor()
    {
        WithOverlay("""{"version":"w3","interpSecondsPerFrame1080p":{"1080p":0.2}}""", () =>
        {
            Assert.Equal(0.2, PipelineOrderPlan.InterpSecondsPerOutputFrame(1920L * 1080), 9);
            Assert.Equal(0.2776, PipelineOrderPlan.InterpSecondsPerOutputFrame(3840L * 2160), 6);   // 未覆盖 → 实测
            Assert.Equal(0.5186, PipelineOrderPlan.InterpSecondsPerOutputFrame(7680L * 4320), 6);
            Assert.Equal(3, PipelineOrderPlan.ResolveInterpAnchors().Pixels.Length);                 // 仍是 3 个实测锚点
        });

        WithOverlay("""{"version":"w4","interpSecondsPerFrame1080p":{"1440p":0.5,"4k":0.3}}""", () =>
        {
            var (px, sc) = PipelineOrderPlan.ResolveInterpAnchors();
            Assert.Equal(4, px.Length);                                     // 1440p 被插入
            Assert.Equal(0.5, PipelineOrderPlan.InterpSecondsPerOutputFrame(2560L * 1440), 9);
            Assert.Equal(0.3, PipelineOrderPlan.InterpSecondsPerOutputFrame(3840L * 2160), 9);       // "4k" 别名命中 2160p
            Assert.Equal(0.0807, PipelineOrderPlan.InterpSecondsPerOutputFrame(1920L * 1080), 6);    // 未覆盖 → 实测
        });
    }

    /// <summary>顺序判定边际:同样的成本,边际从 15% 抬到 40% → 由"切新顺序"变成"边际不足,保持旧顺序"。</summary>
    [Fact]
    public void Online_order_margin_changes_the_margin_verdict()
    {
        var cost = new PipelineOrderPlan.CostInput("synthetic", 2, true, 0.70);   // 预估节省 ≈19.6%
        var def = PipelineOrderPlan.Decide(cost, 2.0, 2, 1920, 1080, 1800);
        Assert.True(def.UpscaleFirst);
        Assert.False(def.MarginInsufficient);
        Assert.InRange(def.SavingsPercent, 15.5, 25.0);

        WithOverlay("""{"version":"w5","orderSwitchMinSavingsPercent":40}""", () =>
        {
            Assert.Equal(40.0, ParamProfileRuntime.OrderSwitchMinSavingsPercent, 9);
            var d = PipelineOrderPlan.Decide(cost, 2.0, 2, 1920, 1080, 1800);
            Assert.False(d.UpscaleFirst);
            Assert.True(d.MarginInsufficient);
            Assert.Contains("安全边际", d.Reason);
            // 显式传具体值时**完全听调用方**(哨兵只在省略/负数时生效)
            var forced = PipelineOrderPlan.Decide(cost, 2.0, 2, 1920, 1080, 1800, minSavingsPercent: 5.0);
            Assert.True(forced.UpscaleFirst);
            var zero = PipelineOrderPlan.Decide(cost, 2.0, 2, 1920, 1080, 1800, minSavingsPercent: 0.0);
            Assert.True(zero.UpscaleFirst);   // 0 是合法值,不许被当成"省略"
        });
    }

    /// <summary>时间轴缺口容差:容差放宽到 2.0 → 原先被记为"缺口"的间隔不再算缺口 → 不填平。</summary>
    [Fact]
    public void Online_gap_tolerance_changes_the_flatten_verdict()
    {
        var durs = new double[] { 0.04, 0.04, 0.04, 0.04, 0.12 };   // 偏离中位数 2.0 倍
        var def = TimelineFlattenPlan.Decide(durs, 24.0, 2);
        Assert.True(def.Flatten);
        Assert.Equal(1, def.GapCount);

        WithOverlay("""{"version":"w6","timelineGapToleranceRatio":2.0}""", () =>
        {
            Assert.Equal(2.0, ParamProfileRuntime.TimelineGapToleranceRatio, 9);
            var d = TimelineFlattenPlan.Decide(durs, 24.0, 2);
            Assert.False(d.Flatten);                                  // 差值恰好 = 2.0,不严格大于 → 不算缺口
            Assert.Equal("间隔均匀(无缺口)", d.Reason);
            Assert.Equal(0, d.TargetFrames);
            Assert.Equal(durs.Sum(), d.TotalSeconds, 9);              // 总时长口径不变
        });
        // 收回覆盖:立刻回到"填平"
        Assert.True(TimelineFlattenPlan.Decide(durs, 24.0, 2).Flatten);
    }

    /// <summary>切点三阈值:帧差阈值抬高 → 原来判为切点的一对不再判切(插值保护范围真的变了)。</summary>
    [Fact]
    public void Online_scene_cut_thresholds_change_the_judgement()
    {
        Assert.True(SceneCutJudge.IsCut(30.0, 1000, 400));            // 内置:30 ≥ 25 且 lapvar 比 0.4 ≤ 0.6 → 切
        var diffs = new[] { 20.0, 30.0, 65.0 };
        var laps = new[] { 1000.0, 1000.0, 400.0, 400.0 };
        // 内置口径:20 < 25 不切;30 走 lapvar 比 0.4 ≤ 0.6 → 切;65 ≥ 50 强切 → 切
        Assert.Equal(new[] { 1, 2 }, SceneCutJudge.Detect(diffs, laps).ToArray());

        WithOverlay(
            """{"version":"w7","sceneCutDiffThreshold":60,"sceneCutStrongDiffThreshold":70,"sceneCutLapDropRatio":0.9}""",
            () =>
            {
                Assert.Equal(60.0, ParamProfileRuntime.SceneCutDiffThreshold, 9);
                Assert.Equal(70.0, ParamProfileRuntime.SceneCutStrongDiffThreshold, 9);
                Assert.Equal(0.9, ParamProfileRuntime.SceneCutLapDropRatio, 9);
                Assert.False(SceneCutJudge.IsCut(30.0, 1000, 400));   // 30 < 60 → 不切
                Assert.True(SceneCutJudge.IsCut(65.0, 1000, 400));    // 65 < 70,但 lapvar 比 0.4 ≤ 0.9 → 切
                Assert.True(SceneCutJudge.IsCut(75.0, 1000, 400));    // ≥ 强切阈值
                // 批量清单也必须跟着变(整批走同一份阈值):65 处 lapvar 比是 400/400=1.0 > 0.9 → 一个都不切
                Assert.Empty(SceneCutJudge.Detect(diffs, laps));
            });
        // 收回覆盖:立刻回到内置口径
        Assert.Equal(new[] { 1, 2 }, SceneCutJudge.Detect(diffs, laps).ToArray());
        Assert.True(SceneCutJudge.IsCut(30.0, 1000, 400));
    }

    /// <summary>被判为"非法"的在线项必须**逐项回落内置**,不许半生效。</summary>
    [Fact]
    public void Rejected_online_items_leave_the_builtin_behaviour_untouched()
    {
        // 切点阈值倒挂 → 两项整组回退内置 → IsCut 与内置完全一致
        WithOverlay(
            """{"version":"w8","sceneCutDiffThreshold":80,"sceneCutStrongDiffThreshold":50,"timelineGapToleranceRatio":9}""",
            () =>
            {
                Assert.Equal(SceneCutJudge.DiffThreshold, ParamProfileRuntime.SceneCutDiffThreshold, 9);
                Assert.Equal(SceneCutJudge.StrongDiffThreshold, ParamProfileRuntime.SceneCutStrongDiffThreshold, 9);
                Assert.Equal(TimelineFlattenPlan.GapToleranceRatio, ParamProfileRuntime.TimelineGapToleranceRatio, 9);
                Assert.True(SceneCutJudge.IsCut(30.0, 1000, 400));
                Assert.False(SceneCutJudge.IsCut(10.0, 1000, 400));
            });

        // ① 非法单价(负值被拒 → 该键回退内置 15.145);② 键格式不对(值合法但永远不命中)
        WithOverlay(
            """{"version":"w9","upscaleSecondsPerFrame1080p":{"realesrgan|x4plus|4":-1,"x4plus":0.01}}""",
            () =>
            {
                Assert.Equal(15.145, PipelineOrderPlan.LookupUpscaleSecondsPerFrame("realesrgan", "realesrgan-x4plus", 4, out var prov)!.Value, 6);
                Assert.Contains("12 帧样本", prov);          // 出处仍是"内置实测" —— 被拒项没被当成在线值
            });
    }

    /// <summary>键规范化的纯函数(两侧归一 —— 这是"键写对了却查不到"这类哑巴问题的唯一防线)。</summary>
    [Theory]
    [InlineData("realesrgan", "realesrgan-x4plus", 4, "realesrgan|x4plus|4")]
    [InlineData("realesrgan", "x4plus", 4, "realesrgan|x4plus|4")]
    [InlineData("RealESRGAN", "RealESRGAN_x4plus_anime_6B", 4, "realesrgan|x4plus-anime|4")]   // 官方下划线写法;必须先判 anime
    [InlineData(null, "models-cunet", 2, "waifu2x|cunet|2")]                                  // 引擎按模型反推
    [InlineData("waifu2x-ncnn-vulkan", "models-upconv_7_photo", 2, "waifu2x|upconv_7_photo|2")]
    public void Canonical_upscale_key_normalization(string? engine, string model, int scale, string expected)
        => Assert.Equal(expected, PipelineOrderPlan.CanonicalUpscaleKey(engine, model, scale));

    [Theory]
    [InlineData("realesrgan|x4plus|4", "realesrgan|x4plus|4")]
    [InlineData("realesrgan|realesrgan-x4plus|4", "realesrgan|x4plus|4")]
    [InlineData("x4plus", null)]           // 不是三段式 → 忽略该键
    [InlineData("a|b|c", null)]            // 倍率不是数字 → 忽略该键
    [InlineData("", null)]
    public void Canonical_table_key_rejects_malformed_keys(string tableKey, string? expected)
        => Assert.Equal(expected, PipelineOrderPlan.CanonicalUpscaleKeyFromTableKey(tableKey));

    // ==================== ③ 研究结论的落点(数值钉住,防手滑) ====================

    /// <summary><see cref="ExternalPractice"/> 里的外部参照值必须与研究原文一致;
    /// 这些值**没有任何生产代码读取**(标 [仅建议] 的那几条尤其如此),这里只是"研究结论不被手滑改掉"。</summary>
    [Fact]
    public void External_practice_reference_values_are_pinned()
    {
        Assert.Equal(27.0, ExternalPractice.PySceneDetectContentThresholdDefault, 9);
        Assert.Equal(15.0, ExternalPractice.PySceneDetectAdaptiveMinContentVal, 9);
        Assert.Equal(3.0, ExternalPractice.PySceneDetectAdaptiveThresholdDefault, 9);
        Assert.Equal(2, ExternalPractice.PySceneDetectAdaptiveWindowWidth);
        Assert.Equal(15, ExternalPractice.PySceneDetectMinSceneLenFramesDefault);
        Assert.Equal(0.10, ExternalPractice.MiscScDetectThresholdDefault, 9);
        Assert.Equal(0.8, ExternalPractice.MiscScDetectThresholdHardCaseRatio, 9);
        Assert.Equal("1:2:2", ExternalPractice.UpstreamEngineThreadArgsDefault);
        Assert.Equal(0, ExternalPractice.UpstreamTileSizeAuto);
        Assert.Equal("realesr-animevideov3", ExternalPractice.OfficialAnimeVideoModel);
        Assert.Equal("RealESRGAN_x4plus_anime_6B", ExternalPractice.OfficialAnimeImageModel);
        Assert.Equal("RealESRGAN_x4plus", ExternalPractice.OfficialGeneralImageModel);
        Assert.Equal("realesr-general-x4v3", ExternalPractice.OfficialTinyGeneralModel);
        Assert.Contains("BSD-3-Clause", ExternalPractice.RealEsrganLicense);
        Assert.Contains("MIT", ExternalPractice.RealEsrganNcnnVulkanLicense);
        Assert.Contains("待核实", ExternalPractice.RifeNcnnVulkanLicense);
        Assert.Contains("待核实", ExternalPractice.Waifu2xNcnnVulkanLicense);
        // 上游源码核对出来的机理结论(只作文档,不参与判定)
        Assert.Contains("compute_queue_count", ExternalPractice.ProcThreadsAreClampedToComputeQueues);
        Assert.Contains("2/3/4", ExternalPractice.Scale1IsOutsideUpstreamContract);
        Assert.Contains("不是拆单图", ExternalPractice.UpstreamMultiGpuHint);
        Assert.Contains("Duplicate Frames", ExternalPractice.TopazSceneDetectionAndDuplicateFrames);
        Assert.Contains("Topaz", ExternalPractice.DedupFirstIsIndustryPractice);
        Assert.Contains("无公开", ExternalPractice.NoPublicVramToBatchTable);
        Assert.Contains("Real-CUGAN", ExternalPractice.NoVerifiedAdditionalRedistributableModel);

        // 外部参照 27.0 与我们实际使用的 25.0 必须**同量级**(这是"25 没写错"的唯一外部旁证;
        // 若哪天有人把 DiffThreshold 改成 5 或 100,这条会红,逼他给出新出处)
        double ratio = SceneCutJudge.DiffThreshold / ExternalPractice.PySceneDetectContentThresholdDefault;
        Assert.InRange(ratio, 0.7, 1.4);
        // 上游 `-j` 默认与我们实际使用的形态**故意不同**(实测推翻了上游建议)—— 记录这个差异本身
        Assert.NotEqual(ExternalPractice.UpstreamEngineThreadArgsDefault, "1:1:1");
    }

    /// <summary>覆盖层是"进程内全局"—— 每一条设置路径都必须能干净还原(否则测试之间会互相污染)。</summary>
    [Fact]
    public void Overlay_is_fully_restored_after_every_wiring_test()
    {
        Assert.Null(ParamProfileRuntime.Current);
        Assert.Equal(RenderPolicy.StrongDeviceLargeFramesPerBatch, ParamProfileRuntime.StrongLongBatchFrames);
        Assert.Equal(PipelineOrderPlan.MinSavingsPercent, ParamProfileRuntime.OrderSwitchMinSavingsPercent, 9);
        Assert.Same(PipelineOrderPlan.InterpAnchorPixels, PipelineOrderPlan.ResolveInterpAnchors().Pixels);
    }
}
