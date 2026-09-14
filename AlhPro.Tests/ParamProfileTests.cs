using AlhPro.Core;
using System;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>本集合含"改全局覆盖层(ParamProfileRuntime)"的测试 → 声明为**不并行**,
/// 免得与 RenderPolicyTests / DevicePerfTests 等读同一批常量的测试相互干扰(避免随机红)。</summary>
[CollectionDefinition("ParamProfileRuntimeGlobal", DisableParallelization = true)]
public sealed class ParamProfileRuntimeGlobalCollection { }

/// <summary>【任务 V】在线"最优参数配置"的**解析 + 逐项校验 + 兜底**(纯逻辑)。
/// 【超时怎么覆盖】解析层看不到网络;"超时"在解析层的等价输入就是 **null/空**(拉取失败/超时后调用方传什么),
/// 所以这里用 null/空 把"拉不到配置 ⇒ 完全用内置"这条路径钉住;真实的 5 秒超时 + 多端点退避在
/// `ALHPro.ParamProfileService` 里通过可注入的 fetch 实现(该文件在 UI 工程,测试工程不引用 —— 这是**未单测**的部分,如实说明)。</summary>
[Collection("ParamProfileRuntimeGlobal")]
public class ParamProfileTests
{
    private const string ValidJson = """
    {
      "version": "2026.09.13-1",
      "scope": "1080p 源 / NVIDIA 40 系",
      "upscaleSecondsPerFrame1080p": { "realesrgan|realesrgan-x4plus|4": 0.22 },
      "interpSecondsPerFrame1080p": { "1080p": 0.09 },
      "weakBatchFrames": 50,
      "normalBatchFrames": 320,
      "strongBatchFrames": 360,
      "strongLongBatchFrames": 750,
      "orderSwitchMinSavingsPercent": 12.5,
      "timelineGapToleranceRatio": 0.30,
      "sceneCutDiffThreshold": 22.0,
      "sceneCutStrongDiffThreshold": 45.0,
      "sceneCutLapDropRatio": 0.55,
      "perfFastSecondsPerFrame": 0.28,
      "perfNormalSecondsPerFrame": 0.9
    }
    """;

    [Fact]
    public void Valid_config_is_fully_adopted()
    {
        var r = ParamProfileParser.ParseAndValidate(ValidJson, null, "在线");
        Assert.Equal(0, r.RejectedTotal);
        Assert.True(r.Accepted >= 12, $"合法配置应被采用多项,实得 {r.Accepted}");
        Assert.Equal("2026.09.13-1", r.Profile.Version);
        Assert.Equal(750, r.Profile.StrongLongBatchFrames);
        Assert.Equal(320, r.Profile.NormalBatchFrames);
        Assert.Equal(0.22, r.Profile.UpscaleSecondsPerFrame1080p["realesrgan|realesrgan-x4plus|4"], 6);
        Assert.Equal(0.9, r.Profile.PerfNormalSecondsPerFrame, 6);
        Assert.Contains("在线", r.LogLine);
        Assert.Contains("校验通过", r.LogLine);
    }

    [Fact]
    public void Missing_fields_keep_builtin_values_and_are_not_counted_as_rejected()
    {
        var r = ParamProfileParser.ParseAndValidate("""{"version":"v1"}""");
        Assert.Equal(0, r.RejectedTotal);                       // 缺字段不是"被拒",是"没给"
        Assert.Equal(ParamProfile.BuiltIn.StrongLongBatchFrames, r.Profile.StrongLongBatchFrames);
        Assert.Equal(ParamProfile.BuiltIn.NormalBatchFrames, r.Profile.NormalBatchFrames);
        // 但 version 被采用
        Assert.Equal("v1", r.Profile.Version);
    }

    [Theory]
    [InlineData("""{"version":"v","strongLongBatchFrames":99999}""", "strongLongBatchFrames")]   // 越界(>2000)
    [InlineData("""{"version":"v","strongLongBatchFrames":10}""", "strongLongBatchFrames")]      // 越界(<50)
    [InlineData("""{"version":"v","normalBatchFrames":"300"}""", "normalBatchFrames")]           // 类型错(字符串)
    [InlineData("""{"version":"v","sceneCutLapDropRatio":7}""", "sceneCutLapDropRatio")]         // 越界(比 >1)
    [InlineData("""{"version":"v","perfFastSecondsPerFrame":-1}""", "perfFastSecondsPerFrame")]  // 越界(负)
    [InlineData("""{"version":"v","perfNormalSecondsPerFrame":999}""", "perfNormalSecondsPerFrame")] // 越界(>60)
    public void Out_of_range_or_wrong_type_items_are_rejected_and_fall_back(string json, string expectMentions)
    {
        var r = ParamProfileParser.ParseAndValidate(json);
        Assert.True(r.RejectedTotal >= 1, "非法项必须被拒");
        Assert.Contains(r.Rejected, x => x.Contains(expectMentions));
        // 被拒项必须回退内置值(不是采用越界值)
        var fb = ParamProfile.BuiltIn;
        Assert.InRange(r.Profile.StrongLongBatchFrames, 50, 2000);
        Assert.InRange(r.Profile.NormalBatchFrames, 50, 2000);
        Assert.InRange(r.Profile.PerfFastSecondsPerFrame, 0.001, 60);
        Assert.InRange(r.Profile.PerfNormalSecondsPerFrame, 0.001, 60);
        Assert.Equal(fb.SceneCutLapDropRatio, r.Profile.SceneCutLapDropRatio, 6);
    }

    [Fact]
    public void Null_empty_and_timeout_equivalent_input_falls_back_to_builtin()
    {
        foreach (var json in new string?[] { null, "", "   " })
        {
            var r = ParamProfileParser.ParseAndValidate(json);
            Assert.Equal(0, r.RejectedTotal);                    // 没拿到 ≠ 坏数据
            AssertIsBuiltIn(r.Profile);                          // 与内置表逐项一致(record 含字典,不能直接比引用)
            Assert.Contains("内置", r.Source);
        }
    }

    /// <summary>逐项断言"这份配置就是内置表"(record 里有 Dictionary,直接 Assert.Equal 比的是引用,会假红)。</summary>
    private static void AssertIsBuiltIn(ParamProfile p)
    {
        var b = ParamProfile.BuiltIn;
        Assert.Equal(b.Version, p.Version);
        Assert.Equal(b.Scope, p.Scope);
        Assert.Equal(b.WeakBatchFrames, p.WeakBatchFrames);
        Assert.Equal(b.NormalBatchFrames, p.NormalBatchFrames);
        Assert.Equal(b.StrongBatchFrames, p.StrongBatchFrames);
        Assert.Equal(b.StrongLongBatchFrames, p.StrongLongBatchFrames);
        Assert.Equal(b.OrderSwitchMinSavingsPercent, p.OrderSwitchMinSavingsPercent, 6);
        Assert.Equal(b.TimelineGapToleranceRatio, p.TimelineGapToleranceRatio, 6);
        Assert.Equal(b.SceneCutDiffThreshold, p.SceneCutDiffThreshold, 6);
        Assert.Equal(b.SceneCutStrongDiffThreshold, p.SceneCutStrongDiffThreshold, 6);
        Assert.Equal(b.SceneCutLapDropRatio, p.SceneCutLapDropRatio, 6);
        Assert.Equal(b.PerfFastSecondsPerFrame, p.PerfFastSecondsPerFrame, 6);
        Assert.Equal(b.PerfNormalSecondsPerFrame, p.PerfNormalSecondsPerFrame, 6);
        Assert.Equal(b.UpscaleSecondsPerFrame1080p.Count, p.UpscaleSecondsPerFrame1080p.Count);
        Assert.Equal(b.InterpSecondsPerFrame1080p.Count, p.InterpSecondsPerFrame1080p.Count);
        Assert.Equal(b.UpscaleSecondsPerFrame1080p.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value:0.####}"),
            p.UpscaleSecondsPerFrame1080p.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value:0.####}"));
    }

    [Fact]
    public void Broken_or_non_object_json_is_rejected_wholesale()
    {
        var bad = ParamProfileParser.ParseAndValidate("{ this is not json ");
        Assert.Equal(1, bad.RejectedTotal);
        Assert.Contains(bad.Rejected, x => x.Contains("JSON 解析失败"));
        AssertIsBuiltIn(bad.Profile);

        var arr = ParamProfileParser.ParseAndValidate("[1,2,3]");
        Assert.Equal(1, arr.RejectedTotal);
        Assert.Contains(arr.Rejected, x => x.Contains("顶层不是 JSON 对象"));
        AssertIsBuiltIn(arr.Profile);
    }

    [Fact]
    public void Batch_tier_ordering_violation_falls_back_as_a_group()
    {
        // normal(400) > strong(200)→ 四个档位整组回退内置
        var r = ParamProfileParser.ParseAndValidate(
            """{"version":"v","weakBatchFrames":50,"normalBatchFrames":400,"strongBatchFrames":200,"strongLongBatchFrames":800}""");
        Assert.Contains(r.Rejected, x => x.Contains("weak ≤ normal ≤ strong ≤ strongLong"));
        Assert.Equal(ParamProfile.BuiltIn.NormalBatchFrames, r.Profile.NormalBatchFrames);
        Assert.Equal(ParamProfile.BuiltIn.StrongBatchFrames, r.Profile.StrongBatchFrames);
    }

    [Fact]
    public void Inverted_perf_thresholds_and_cut_thresholds_fall_back()
    {
        var a = ParamProfileParser.ParseAndValidate(
            """{"version":"v","perfFastSecondsPerFrame":2.0,"perfNormalSecondsPerFrame":1.0}""");
        Assert.Contains(a.Rejected, x => x.Contains("perfNormalSecondsPerFrame"));
        Assert.Equal(ParamProfile.BuiltIn.PerfFastSecondsPerFrame, a.Profile.PerfFastSecondsPerFrame, 6);

        var b = ParamProfileParser.ParseAndValidate(
            """{"version":"v","sceneCutDiffThreshold":80.0,"sceneCutStrongDiffThreshold":50.0}""");
        Assert.Contains(b.Rejected, x => x.Contains("sceneCutStrongDiffThreshold"));
        Assert.Equal(ParamProfile.BuiltIn.SceneCutDiffThreshold, b.Profile.SceneCutDiffThreshold, 6);
    }

    [Fact]
    public void Bad_price_table_entries_are_rejected_per_key()
    {
        var r = ParamProfileParser.ParseAndValidate(
            """{"version":"v","upscaleSecondsPerFrame1080p":{"a|b|4":0.2,"c|d|4":-3,"e|f|4":"x"}}""");
        Assert.Equal(2, r.RejectedTotal);                        // 负值 + 类型错
        Assert.Equal(0.2, r.Profile.UpscaleSecondsPerFrame1080p["a|b|4"], 6);   // 合法项照常采用
        Assert.False(r.Profile.UpscaleSecondsPerFrame1080p.ContainsKey("c|d|4"));   // 非法项被丢弃
        Assert.False(r.Profile.UpscaleSecondsPerFrame1080p.ContainsKey("e|f|4"));
    }

    [Fact]
    public void Builtin_profile_matches_the_code_constants()
    {
        var b = ParamProfile.BuiltIn;
        Assert.Equal(RenderPolicy.WeakDeviceFramesPerBatch, b.WeakBatchFrames);
        Assert.Equal(RenderPolicy.NormalDeviceFramesPerBatch, b.NormalBatchFrames);
        Assert.Equal(RenderPolicy.StrongDeviceFramesPerBatch, b.StrongBatchFrames);
        Assert.Equal(RenderPolicy.StrongDeviceLargeFramesPerBatch, b.StrongLongBatchFrames);
        Assert.Equal(DevicePerf.FastSecondsPerFrame1080p, b.PerfFastSecondsPerFrame, 6);
        Assert.Equal(DevicePerf.NormalSecondsPerFrame1080p, b.PerfNormalSecondsPerFrame, 6);
        Assert.Equal(TimelineFlattenPlan.GapToleranceRatio, b.TimelineGapToleranceRatio, 6);
        Assert.Equal(SceneCutJudge.DiffThreshold, b.SceneCutDiffThreshold, 6);
    }

    // ---------- 覆盖层:配置生效 / 不配置 = 与改动前逐字一致 ----------

    [Fact]
    public void Runtime_overlay_applies_only_when_set_and_restores_exactly()
    {
        try
        {
            // ① 未配置(null)→ 与既有常量同口径(等于"没这份功能")
            Assert.Null(ParamProfileRuntime.Current);
            int baseCap = RenderPolicy.PlanVideoBatches(16.0, 1800, 3600).BatchSize;
            Assert.Equal(RenderPolicy.StrongDeviceLargeFramesPerBatch, baseCap);

            // ② 配置一份合法在线参数 → 覆盖少数可调项(批上限 700 → 900)
            var r = ParamProfileParser.ParseAndValidate(
                """{"version":"v9","weakBatchFrames":50,"normalBatchFrames":300,"strongBatchFrames":350,"strongLongBatchFrames":900,"perfFastSecondsPerFrame":0.05,"perfNormalSecondsPerFrame":0.9}""");
            ParamProfileRuntime.Set(r.Profile);
            Assert.Equal(900, ParamProfileRuntime.StrongLongBatchFrames);
            var p = RenderPolicy.PlanVideoBatches(16.0, 1800, 3600);
            Assert.Equal(900, p.BatchSize);
            // 性能档阈值也被覆盖:0.10 秒/帧在默认阈值(≤0.30=Fast)下是 Fast,覆盖 Fast=0.05 后只算 Normal
            Assert.Equal(PerfScore.Normal, DevicePerf.Score(16.0, 16, 0.10, out _));

            // ③ 关掉(Set(null))→ 逐字回到内置口径
            ParamProfileRuntime.Set(null);
            Assert.Null(ParamProfileRuntime.Current);
            Assert.Equal(700, RenderPolicy.PlanVideoBatches(16.0, 1800, 3600).BatchSize);
            Assert.Equal(RenderPolicy.StrongDeviceLargeFramesPerBatch, ParamProfileRuntime.StrongLongBatchFrames);
        }
        finally
        {
            ParamProfileRuntime.Set(null);   // 无论如何都要还原:这是全局状态
        }
    }
}
