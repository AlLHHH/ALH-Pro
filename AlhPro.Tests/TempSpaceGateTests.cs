using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>「临时空间守门必须在补帧倍率**定稿之后**再判一次」的接线契约(2026-09-15 实盘事故修复)。
///
/// 【事故】用户勾了补帧、又填了「指定帧率」(200fps):去重后的内容帧率约 50fps,程序把 interpScale
/// 从用户选的 2x **自动抬到 4x**。但"本任务要多少空间"这笔账是在**拆帧之前**算的,那会儿倍率还是 2x ——
/// 而峰值帧数 = 源帧数 × 倍率,倍率翻倍 ⇒ 峰值帧数翻倍 ⇒ 空间需求被低估近一半。守门按低估的账放行,
/// 跑到一半临时盘爆掉 ⇒ 后半段输出帧读损坏,被深灰"占位帧"顶替(帧号连续、内容是废的,用户只看到满屏花帧)。
/// 诊断包 2138 实证:预估按 2x 报 29.2GB 放行,实际按 4x 跑(5262 → 21048 帧),其中 8550/21048 帧是占位帧。
///
/// 【修法】把"按当前 peakFrames 算空间 + 判临时盘水位"抽成本地函数 `EvalTempSpaceGate`,**调用两次**:
///   ①拆帧之前按**用户选的倍率**粗判一次(true)—— 早失败,省得白拆帧;
///   ②倍率被「指定帧率」自动抬高之后、开跑之前,按**最终倍率**再判一次(false)—— 这才是能拦住爆盘的那道闸。
/// 下方 2303 行"峰值守门复算(超分阶段定稿批大小)"用的是同一个 `peakFrames` 变量,所以自动跟着变正确。
///
/// 这类"顺序错位"缺陷编译不报错、集成测试也不好跑(得真把盘占满才复现),只能把源码接线钉住。</summary>
public class TempSpaceGateTests
{
    /// <summary>**核心契约**:倍率被自动抬高之后,必须重算峰值帧数,并按最终倍率复判一次空间。</summary>
    [Fact]
    public void Space_gate_is_re_evaluated_after_the_target_fps_escalates_the_interp_scale()
    {
        var svc = ReadRepoFile("ImgUpscalerUI", "VideoService.cs");

        // "指定帧率 → 自动算够倍率"那一步(自动抬高 interpScale 的唯一一处)
        int escalate = svc.IndexOf("interpScale = needScale;", StringComparison.Ordinal);
        Assert.True(escalate > 0, "找不到「指定帧率」自动抬高补帧倍率的那行(interpScale = needScale;)");

        // 拆帧前的粗判必须在抬高之前(否则"早失败"就没了,而且第一判注定用的是最终倍率 —— 那也行,
        // 但那意味着预估整段被搬到拆帧之后,与现在的结构不符;这里把"两次判、一前一后"的顺序钉死)
        int prelim = svc.IndexOf("EvalTempSpaceGate(true)", StringComparison.Ordinal);
        Assert.True(prelim > 0, "拆帧前的那次粗判不见了(EvalTempSpaceGate(true))");
        Assert.True(prelim < escalate, "拆帧前的粗判必须仍在倍率抬高之前");

        // 抬高之后:必须重算 peakFrames(按最终倍率)
        const string recompute = "peakFrames = frameInterp ? (long)Math.Ceiling((double)baseFrames * interpScale) : baseFrames;";
        int recomputeAfter = svc.IndexOf(recompute, escalate, StringComparison.Ordinal);
        Assert.True(recomputeAfter > escalate, "倍率抬高之后没有重算峰值帧数 —— 空间账还是按旧倍率算的(低估近一半)");

        // 抬高之后:必须按最终倍率复判一次空间
        int regate = svc.IndexOf("EvalTempSpaceGate(false)", escalate, StringComparison.Ordinal);
        Assert.True(regate > escalate, "倍率抬高后没有按最终倍率复判空间(缺 EvalTempSpaceGate(false))");

        // 复判只能收紧、不能放松:先前判过"空间紧"就必须保持紧(directly 覆盖会把降批保护抹掉)
        Assert.Contains("diskTight = diskTight || gateNow.t;", svc);
    }

    /// <summary>空间账**只走一个函数**:估算表达式不得再在调用点内联一份(两份口径必然漂移),
    /// 且"临时磁盘空间不足"这条硬中止只允许有两处 —— 本地守门函数一处、超分阶段复算一处。</summary>
    [Fact]
    public void Space_estimate_goes_through_one_place_only()
    {
        var svc = ReadRepoFile("ImgUpscalerUI", "VideoService.cs");

        // 峰值帧数的计算式只允许出现两次:拆帧前的初值 + 倍率定稿后的复核(多一份就是又有人内联了)
        Assert.Equal(2, Regex_Count(svc,
            "peakFrames = frameInterp \\? \\(long\\)Math\\.Ceiling\\(\\(double\\)baseFrames \\* interpScale\\) : baseFrames;"));

        // 估算调用点只允许一份(在本地守门函数里)
        Assert.Equal(1, Regex_Count(svc,
            "TempSpaceEstimate\\.NeedBytes\\(peakFrames, outFrameMB, perfBatchFrames, srcFrameMB\\)"));

        // 硬中止文案两处:本地守门函数(拆帧前/倍率定稿后共用) + 超分阶段按定稿批大小复算
        Assert.Equal(2, Regex_Count(svc, "临时磁盘空间不足"));
    }

    /// <summary>把"低估多少"用数字钉住:同一个任务、倍率从 2x 抬到 4x,空间需求就是**翻倍**量级 ——
    /// 这正是"按 2x 放行、按 4x 跑"会爆盘的原因(纯 Core 逻辑,不依赖 UI)。</summary>
    [Fact]
    public void Doubling_the_interp_scale_nearly_doubles_the_space_need()
    {
        const int srcW = 1920, srcH = 1080;
        double srcFrameMb = AlhPro.Core.TempSpaceEstimate.SourceFrameMegabytes(srcW, srcH);
        double peakFrameMb = AlhPro.Core.TempSpaceEstimate.PeakFrameMegabytes(srcW, srcH, 2.0, frameInterp: true);

        long frames2x = 5262 * 2;      // 事故里的量级:源 5262 帧
        long frames4x = 5262 * 4;

        double need2x = AlhPro.Core.TempSpaceEstimate.NeedBytes(frames2x, peakFrameMb, 200, srcFrameMb);
        double need4x = AlhPro.Core.TempSpaceEstimate.NeedBytes(frames4x, peakFrameMb, 200, srcFrameMb);

        Assert.True(need4x > need2x * 1.9,
            $"倍率 2x→4x 后空间需求应接近翻倍(实得 {need2x / (1024.0 * 1024 * 1024):0.#}GB → {need4x / (1024.0 * 1024 * 1024):0.#}GB)");
    }

    private static int Regex_Count(string text, string pattern)
        => System.Text.RegularExpressions.Regex.Matches(text, pattern).Count;

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
