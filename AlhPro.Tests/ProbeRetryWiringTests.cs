using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【接线契约】判据与档位由 <see cref="ProbeRetryPolicyTests"/> 钉死,这里钉的是**接线**:
/// ① 重试编排只能有一处,且走 AlhPro.Core.ProbeRetryPolicy.RunAsync(不许各处自写 if);
/// ② 档位必须按 fullFrame 选(`ForFullFrame`),不许调用点自己拼数字;
/// ③ 失败形态必须喂给判据(不喂 ⇒ 判据只看到 None ⇒ 永远不重试,"修了等于没修");
/// ④ 顺序必须是"重试完 → 才落盘结论",且**取消时一个结论都不落**(复审 F1);
/// ⑤ 重试决策/结果必须进日志(不许静默);⑥ RIFE 那条独立探测不许被顺手改掉。
///
/// 【为什么这类契约要单独钉】真机那次"一次超时判死一整天"是接线问题而非判据问题:
/// 探测里明明有"确定失败重试 3 次"的循环,偏偏把超时写成"不重试"直接 return false。
/// 复审 F1 同样是接线问题:判据已经把"取消"标出来了,落盘那一步没问它,一次取消照样写成 1 天的否定结论。
/// 判据再对,接线漏一环症状就一模一样地复发,而纯逻辑单测看不出来。</summary>
public class ProbeRetryWiringTests
{
    private static string EngineServiceCode()
        => StripComments(ReadRepoFile("ImgUpscalerUI", "EngineService.cs"));

    /// <summary>★ 编排只有一处、走 Core;档位只由 fullFrame 选;日志接上 Warn(不静默)。</summary>
    [Fact]
    public void Retry_orchestration_lives_in_one_place_and_picks_the_profile_by_full_frame()
    {
        var code = EngineServiceCode();
        int helper = code.IndexOf("private static async Task<AlhPro.Core.ProbeRetryOutcome> ProbeEngineGpuAsync(", StringComparison.Ordinal);
        int run = code.IndexOf("AlhPro.Core.ProbeRetryPolicy.RunAsync(", helper, StringComparison.Ordinal);
        int profile = code.IndexOf("AlhPro.Core.ProbeRetryPolicy.ForFullFrame(fullFrame)", helper, StringComparison.Ordinal);

        Assert.True(helper > 0, "必须有一个统一的探测编排入口(IsEngineGpuUsableAsync 被挑卡路径调用,不能只给 EnsureNcnnProbeAsync 加循环)");
        Assert.True(run > helper, "编排入口必须走 AlhPro.Core.ProbeRetryPolicy.RunAsync");
        Assert.True(profile > helper, "档位必须按 fullFrame 选(ForFullFrame),不许调用点自己拼数字");
        Assert.Equal(1, CountOccurrences(code, "AlhPro.Core.ProbeRetryPolicy.RunAsync("));
        Assert.Contains("log: AppLogger.Warn", code);
        // 失败形态必须真的喂进判据:只回传 bool 的话判据永远看到 None ⇒ 一次都不会重试
        Assert.Contains("new AlhPro.Core.ProbeAttempt(", code);
        Assert.Contains("false, failKind, failDetail", code);
    }

    /// <summary>★ 复审 F1:取消必须在落盘之前拦住 —— 任何时刻 ct 已取消都不写结论,且日志按"因取消未重试"说,
    /// 不许把它归因成"确定性失败"。而"确定性失败,按判据未重试"这句只允许出现在取消分支**之后**。</summary>
    [Fact]
    public void Cancellation_is_checked_before_the_verdict_is_written()
    {
        var code = EngineServiceCode();
        int probe = code.IndexOf("public static async Task<bool> EnsureNcnnProbeAsync(", StringComparison.Ordinal);
        int cancelGuard = code.IndexOf("if (ct.IsCancellationRequested || outcome.Cancelled)", probe, StringComparison.Ordinal);
        int save = code.IndexOf("SaveNcnnVerdict(engine, gpuId, model, ok,", probe, StringComparison.Ordinal);
        int deterministicText = code.IndexOf("(确定性失败,按判据未重试)", probe, StringComparison.Ordinal);

        Assert.True(probe > 0, "必须能找到 EnsureNcnnProbeAsync");
        Assert.True(cancelGuard > probe, "落盘前必须有取消闸门(ct 已取消 ⇒ 不落任何结论)");
        Assert.True(save > cancelGuard, "取消闸门必须在 SaveNcnnVerdict 之前");
        Assert.True(deterministicText > save, "“确定性失败,按判据未重试”只允许出现在取消闸门之后(取消那一路已经 return)");
        // 闸门里要如实写"因取消未重试",且与 outcome.Cancelled 对得上
        string guardBlock = code.Substring(cancelGuard, save - cancelGuard);   // 闸门 → 落盘之间
        Assert.Contains("因取消未重试", guardBlock);
        Assert.Contains("不落盘结论", guardBlock);
        Assert.DoesNotContain("确定性失败", guardBlock);
    }

    /// <summary>顺序:先让判据决定重试,再落盘;全文只允许一处落盘(重试路径里不许提前写失败结论)。</summary>
    [Fact]
    public void The_verdict_is_written_once_after_the_retry_decision()
    {
        var code = EngineServiceCode();
        int probe = code.IndexOf("public static async Task<bool> EnsureNcnnProbeAsync(", StringComparison.Ordinal);
        int outcome = code.IndexOf("var outcome = await ProbeEngineGpuAsync(", probe, StringComparison.Ordinal);
        int save = code.IndexOf("SaveNcnnVerdict(engine, gpuId, model, ok,", probe, StringComparison.Ordinal);

        Assert.True(outcome > probe, "ncnn 探测必须走统一编排(先重试完)");
        Assert.True(save > outcome, "先让判据决定要不要重试,再落盘结论");
        Assert.Equal(1, CountOccurrences(code, "SaveNcnnVerdict(engine, gpuId, model, ok,"));
        // 落盘明细与"共探测 N 次"都取自实际尝试数
        Assert.Contains("attempts={outcome.Attempts}", code);
        Assert.Contains("共探测 {outcome.Attempts} 次", code);
    }

    /// <summary>TTL 口径不变(重试成功 = 7 天;重试后仍失败 = 1 天)—— 判据/档位改动不该偷偷改 TTL。</summary>
    [Fact]
    public void Verdict_ttls_are_unchanged()
    {
        var code = EngineServiceCode();
        Assert.Contains("TimeSpan.FromDays(7)", code);
        Assert.Contains("TimeSpan.FromDays(1)", code);
    }

    /// <summary>RIFE 那条独立探测(RIFE 的 hang 是文档化的既有行为)不许被顺手改成走同一套档位。</summary>
    [Fact]
    public void The_rife_probe_is_left_alone()
    {
        var code = EngineServiceCode();
        int rife = code.IndexOf("public static async Task<bool> EnsureRifeNcnnProbeAsync(", StringComparison.Ordinal);
        int key = code.IndexOf("private static string RifeProbeKey(", rife, StringComparison.Ordinal);
        Assert.True(rife > 0 && key > rife, "必须能定位 RIFE 探测那一节");
        Assert.DoesNotContain("ProbeRetryPolicy", code.Substring(rife, key - rife));
    }

    private static int CountOccurrences(string text, string needle)
    {
        int n = 0, i = 0;
        while ((i = text.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
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
