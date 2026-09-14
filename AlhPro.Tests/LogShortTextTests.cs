using AlhPro.Core;
using System;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【任务 X1/X2/X3/X4】界面日志区短句文案的硬规则(用户原话:「日志那么一坨 后面新加的能不能简洁一点」)。
/// 这里钉住的是"界面那几行"的规矩 —— 数字与判定仍然来自既有纯函数,本文件只测文案形态与口径:
///   ① 不含"第 N 帧 / 共 M 帧"结构(会被 UI 的进度解析 etaRegex 抢走,变成步骤行);
///   ② 不含"完成"二字(会被 UI 改写成 "✓ …" 并清掉当前步骤行);
///   ③ 一行不超过约 60 个汉字(超限按汉字位置截断并补省略号,数字不参与计数);
///   ④ 顺序"没切到新顺序"的两种情形必须分开写(只快一点点 vs 反而更慢,不许混)。
/// </summary>
public class LogShortTextTests
{
    /// <summary>手搓一个顺序判定结果:用来精确打某一条文案分支(不依赖成本表的当前数值)。
    /// 字段顺序见 <see cref="PipelineOrderPlan.Decision"/>。</summary>
    private static PipelineOrderPlan.Decision Decision(bool upscaleFirst, double savingsSeconds, double savingsPercent)
        => new(
            UpscaleFirst: upscaleFirst, OldOrderSeconds: 100, NewOrderSeconds: 100 - savingsSeconds,
            SavingsSeconds: savingsSeconds, SavingsPercent: savingsPercent, ThresholdSecondsPerFrame: 0.1,
            UpscalePerFrame: 0.2, InterpLoPerFrame: 0.1, InterpHiPerFrame: 0.2,
            SourcePixels: 1920.0 * 1080, HiPixels: 3840L * 2160,
            MarginInsufficient: false, Measured: true, Reason: "单测构造");

    // ===== ③ 汉字计数与截断 =====

    [Fact]
    public void Chinese_count_counts_only_cjk_and_ignores_digits()
    {
        Assert.Equal(5, LogShortText.ChineseCharCount("补帧 285 帧/批 ×1 批"));
        Assert.Equal(0, LogShortText.ChineseCharCount("1920x1080 @95.9fps - 285/1"));
        Assert.Equal(0, LogShortText.ChineseCharCount(""));
        Assert.Equal(0, LogShortText.ChineseCharCount(null));
    }

    [Fact]
    public void Clamp_is_noop_when_within_limit()
    {
        string line = "批大小:补帧按转场分段(等效每批 285 帧)、超分 285 帧/批 ×1 批(设备档:内存好×性能快)";
        Assert.True(LogShortText.ChineseCharCount(line) <= LogShortText.MaxChineseCharsPerLine);
        Assert.Equal(line, LogShortText.ClampToChineseLimit(line));
    }

    [Fact]
    public void Clamp_cuts_at_the_last_fitting_chinese_char_and_keeps_key_numbers()
    {
        // 关键数字(digits)不参与计数 → 截断只会砍汉字,不会把 "285 帧/批" 这种数字砍掉
        string longLine = string.Concat(Enumerable.Repeat("超分批", 30)) + " 每批 285 帧";
        string clamped = LogShortText.ClampToChineseLimit(longLine);
        Assert.EndsWith("…", clamped);
        Assert.True(LogShortText.ChineseCharCount(clamped) <= LogShortText.MaxChineseCharsPerLine);
        Assert.StartsWith("超分", clamped);
        Assert.DoesNotContain("完成", clamped);
    }

    [Fact]
    public void Clamp_limit_is_configurable_and_never_throws_on_edge_input()
    {
        Assert.Equal("超…", LogShortText.ClampToChineseLimit("超分批决策", 1));
        Assert.Equal("", LogShortText.ClampToChineseLimit(""));
        Assert.Equal("abc", LogShortText.ClampToChineseLimit("abc", 0));   // 没有汉字 → 无从截断,原样返回
    }

    // ===== ① / ② 界面文案不许踩进度解析与步骤行的雷 =====

    [Fact]
    public void Ui_short_lines_never_contain_the_progress_or_step_line_triggers()
    {
        string[] lines =
        {
            LogShortText.OrderShortText(Decision(false, -0.4, -0.4), 15.0),
            LogShortText.OrderShortText(Decision(true, 47, 47), 15.0),
            "批大小:补帧按转场分段(等效每批 285 帧)、超分 285 帧/批 ×1 批(设备档:内存好×性能快;批数为预计)",
            "源 1920×1080 · 帧数:源 72 → 去重后 72 → 补帧后 285(输出 285 帧 @95.9fps)",
            LogShortText.StageCostShort("补帧", 11.9, 285),
            LogShortText.StageCostShort("超分", 25.1, 285),
            LogShortText.StageCostOrSkipped("补帧", true, false, 0, 0),
            "输出:285 帧 / 2.973s / 95.9 fps;临时盘峰值 约 1.2 GB;清理 285 帧临时文件",
            "音画:画面 2.973s / 源 2.973s;音频 2.973s;黑场 ✓ 无",
        };
        foreach (string line in lines)
        {
            Assert.DoesNotContain(LogShortText.StepLineKillerWord, line);          // 不许含"完成"
            Assert.DoesNotContain(LogShortText.ProgressStealingFragment, line);    // 不许含"共"(第 N 帧 / 共 M 帧)
            Assert.True(LogShortText.ChineseCharCount(line) <= LogShortText.MaxChineseCharsPerLine,
                $"界面一行超过 {LogShortText.MaxChineseCharsPerLine} 个汉字:{line}");
        }
    }

    // ===== ④ 顺序短句:两种情况必须分开写 =====

    [Fact]
    public void Order_short_text_says_slower_when_the_new_order_loses()
    {
        // 真机那次:「新顺序反而更慢 0.4% → 选择 旧顺序」—— 界面必须照实写"反而慢",不许写"只快"(那是骗人)
        string line = LogShortText.OrderShortText(Decision(false, -0.4, -0.4), 15.0);
        Assert.Equal("补帧→超分(先超分反而慢 0.4%,未达 15% 门槛)", line);
    }

    [Fact]
    public void Order_short_text_says_faster_when_savings_exist_but_miss_the_margin()
    {
        // 更省但没到安全边际(任务 Q1:节省 <15% 不切换)→ 写"只快 X%" + 门槛,不许写成切换了
        string line = LogShortText.OrderShortText(Decision(false, 2.0, 2.0), 15.0);
        Assert.Equal("补帧→超分(先超分只快 2%,未达 15% 门槛)", line);
    }

    [Fact]
    public void Order_short_text_announces_the_switch_with_savings()
    {
        string line = LogShortText.OrderShortText(Decision(true, 12.0, 47.0), 15.0);
        Assert.Equal("超分→补帧(预计省 47%)", line);
        Assert.DoesNotContain("未达", line);
    }

    /// <summary>真实判定入口(用成本表算出来的 Decision)也必须满足界面规则 —— 不是只有手搓结果才合规。</summary>
    [Fact]
    public void Order_short_text_from_the_real_decider_also_obeys_ui_rules()
    {
        var d = PipelineOrderPlan.Decide("realesrgan", "realesr-animevideov3", 4.0, 3, 1920, 1080, 72);
        string line = LogShortText.OrderShortText(d, 15.0);
        Assert.Contains(d.UpscaleFirst ? "超分→补帧" : "补帧→超分", line);
        Assert.DoesNotContain("完成", line);
        Assert.DoesNotContain("共", line);
        // 长判据(u / r_lo / r_hi / 门槛 / 成本表出处)只在文件日志那行里,界面这行必须短得多
        Assert.True(line.Length < d.LogLine.Length);
    }

    // ===== 设备档短词 =====

    [Fact]
    public void Device_tier_short_text_uses_the_same_tiers_as_render_policy()
    {
        Assert.Equal("内存好×性能快", LogShortText.DeviceTierShortText(19.1, PerfScore.Fast));
        Assert.Equal("内存正常×性能正常", LogShortText.DeviceTierShortText(6.0, PerfScore.Normal));
        Assert.Equal("内存差×性能慢", LogShortText.DeviceTierShortText(1.0, PerfScore.Slow));
        // 档位口径必须与 RenderPolicy.TierFor 同源(不许各写一套阈值)
        Assert.Equal(RenderPolicy.DeviceTier.Strong, RenderPolicy.TierFor(19.1));
        Assert.Equal(RenderPolicy.DeviceTier.Normal, RenderPolicy.TierFor(6.0));
        Assert.Equal(RenderPolicy.DeviceTier.Weak, RenderPolicy.TierFor(1.0));
        Assert.True(LogShortText.ChineseCharCount(LogShortText.DeviceTierShortText(19.1, PerfScore.Fast)) <= 10);
    }

    // ===== X3 阶段成本短句 =====

    [Fact]
    public void Stage_cost_short_has_the_uniform_frames_seconds_ms_per_frame_format()
    {
        Assert.Equal("补帧 11.9s(285 帧,42 ms/帧)", LogShortText.StageCostShort("补帧", 11.9, 285));
        Assert.Equal("超分 25.1s(285 帧,88 ms/帧)", LogShortText.StageCostShort("超分", 25.1, 285));
        // 数字不参与 60 汉字上限 → 这个格式自身不可能超限
        Assert.True(LogShortText.ChineseCharCount(LogShortText.StageCostShort("后处理", 12345.6, 999999)) <= 10);
    }

    [Fact]
    public void Stage_cost_short_reports_uncollected_instead_of_faking_numbers()
    {
        Assert.Equal("准备 未采集", LogShortText.StageCostShort("准备", 0, 100));   // 没测到耗时
        Assert.Equal("准备 未采集", LogShortText.StageCostShort("准备", 3.2, 0));   // 不知道帧数
        Assert.Equal("准备 未采集", LogShortText.StageCostShort("准备", 0, 0));
    }

    [Fact]
    public void Stage_cost_distinguishes_not_enabled_from_enabled_but_skipped()
    {
        // 用户没勾 → "未开";勾了但本次跳过(如 1x 不超分)→ "未跑";都不许写成"未采集"
        Assert.Equal("补帧 未开", LogShortText.StageCostOrSkipped("补帧", false, false, 0, 0));
        Assert.Equal("超分 未跑", LogShortText.StageCostOrSkipped("超分", true, false, 0, 0));
        Assert.Equal("超分 未采集", LogShortText.StageCostOrSkipped("超分", true, true, 0, 0));
        Assert.Equal("超分 25.1s(285 帧,88 ms/帧)", LogShortText.StageCostOrSkipped("超分", true, true, 25.1, 285));
    }
}
