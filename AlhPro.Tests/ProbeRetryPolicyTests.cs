using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>
/// 显卡 ncnn 探测"重试判据 + 两个档位(profile)+ 取消语义"的纯逻辑单测(2026-09-24)。
///
/// 【为什么必须靠单测钉住】真机踩过的那次是 2026-09-22 22:22「realesrgan GPU(0) 60 秒无响应(疑似 hang)」——
/// 一次瞬时超时 → 当场落失败结论(TTL 1 天)→ 该引擎/该模型【一整天】走 ONNX 慢路(3880 ms/帧)。
/// 触发条件在特定显卡上(重模型 + 冷启动 + 卡正忙恰好压过 60 秒),**没法按需复现**;
/// 所以判据(该不该重试)、档位(试几次/退避多久)与编排(含取消)都放在 AlhPro.Core 里,
/// 用"假探测 + 假退避"把每条路径跑完。
///
/// 本文件按验收逐条覆盖:
///   ① 两档定值:流水线全帧 = 仅超时重试一次、退避 3 秒(3s×1);小图活性检查/挑卡 = 确定性失败也退避 1.5 秒、
///      最多 3 次(1.5s×3,恢复旧保险),瞬时 Hang 同样重试;档位由 fullFrame 选。
///   ② 四条路径:该重试 / 不该重试 / 重试后成功 / 重试后仍失败。
///   ③ 取消(复审 F1):退避窗口里被取消 ⇒ 不打"仍失败"、更不能被当成"确定性失败";
///      outcome.Cancelled=true、Attempts=1(没发起第二次),调用方据此不落盘任何结论。
///   ④ 日志措辞自洽:任何一行都不许出现"重试第 2/1 次"这种分母小于分子的表述。
/// </summary>
public class ProbeRetryPolicyTests
{
    private static readonly ProbeRetryProfile Pipeline = ProbeRetryPolicy.PipelineFullFrameProbe;
    private static readonly ProbeRetryProfile Liveness = ProbeRetryPolicy.DeviceLivenessCheck;

    /// <summary>确定性失败形态(引擎已退出 / 已交出结果 ⇒ 每次都会重现)。</summary>
    private static readonly ProbeFailureKind[] Deterministic =
    {
        ProbeFailureKind.CrashExitCode, ProbeFailureKind.StartupFailed, ProbeFailureKind.NoOutput,
        ProbeFailureKind.EmptyOutput, ProbeFailureKind.DefectiveFrame,
    };

    /// <summary>假探测:按脚本依次给出每次尝试的结果(脚本用完就一直重复最后一个);顺手记账"第几次被调用"。</summary>
    private sealed class FakeProbe
    {
        private readonly Queue<ProbeAttempt> _script;
        private ProbeAttempt _last;
        public List<int> AttemptNumbers { get; } = new();

        public FakeProbe(params ProbeAttempt[] script)
        {
            _script = new Queue<ProbeAttempt>(script);
            _last = script.Length > 0 ? script[^1] : new ProbeAttempt(false, ProbeFailureKind.None, "");
        }

        public Task<ProbeAttempt> NextAsync(int attempt, CancellationToken ct)
        {
            AttemptNumbers.Add(attempt);
            var r = _script.Count > 0 ? _script.Dequeue() : _last;
            _last = r;
            return Task.FromResult(r);
        }
    }

    private static ProbeAttempt Fail(ProbeFailureKind kind, string detail = "") => new(false, kind, detail);
    private static ProbeAttempt Pass() => new(true, ProbeFailureKind.None, "probe ok");

    /// <summary>用假探测 + 假退避跑完整条编排:记下每次退避毫秒与每一条日志,便于逐条断言。</summary>
    private static async Task<(ProbeRetryOutcome Outcome, FakeProbe Probe, List<int> Delays, List<string> Log)>
        RunAsync(ProbeRetryProfile profile, params ProbeAttempt[] script)
    {
        var probe = new FakeProbe(script);
        var delays = new List<int>();
        var log = new List<string>();
        var outcome = await ProbeRetryPolicy.RunAsync(
            profile,
            (attempt, ct) => probe.NextAsync(attempt, ct),
            (ms, ct) => { delays.Add(ms); return Task.CompletedTask; },
            log.Add);
        return (outcome, probe, delays, log);
    }

    // ───────────────── ① 两档定值(队长裁定:1.5s×3 与 3s×1) ─────────────────

    /// <summary>★ 流水线全帧档:只对超时/无响应重试**一次**、退避 **3 秒**;确定性失败一律不重试。</summary>
    [Fact]
    public void Pipeline_profile_retries_only_timeouts_once_with_three_second_backoff()
    {
        Assert.Equal("pipeline-full-frame", Pipeline.Name);
        Assert.True(Pipeline.RetryHang);
        Assert.False(Pipeline.RetryDeterministicFailures);
        Assert.Equal(2, Pipeline.MaxAttempts);
        Assert.Equal(3000, Pipeline.BackoffMs);
        Assert.InRange(Pipeline.BackoffMs, 2000, 5000);   // 与题面建议的 2~5 秒一致

        Assert.Equal(2, ProbeRetryPolicy.MaxAttempts(Pipeline, ProbeFailureKind.Hang));
        Assert.Equal(3000, ProbeRetryPolicy.BackoffMsAfterAttempt(Pipeline, 1, ProbeFailureKind.Hang));
        foreach (var k in Deterministic)
        {
            Assert.False(ProbeRetryPolicy.ShouldRetry(Pipeline, k));
            Assert.Equal(1, ProbeRetryPolicy.MaxAttempts(Pipeline, k));
            Assert.Equal(0, ProbeRetryPolicy.BackoffMsAfterAttempt(Pipeline, 1, k));
        }
    }

    /// <summary>★ 挑卡/活性检查档:确定性失败也退避 **1.5 秒**、最多 **3 次**(恢复被顺带删掉的旧保险);
    /// 瞬时卡死也重试,但**只重试一次(共 2 次)** —— 这一档要在多张候选卡上逐个试,卡死每次都等满 15 秒超时,
    /// 若也试 3 次,"所有候选都卡死"的机器上启动自检最坏 ≈4.8 分钟(独立审查算过)⇒ 卡死单独限 2 次,最坏 ≈3.2 分钟。</summary>
    [Fact]
    public void Liveness_profile_restores_the_old_insurance_and_caps_hang_at_two_attempts()
    {
        Assert.Equal("device-liveness", Liveness.Name);
        Assert.True(Liveness.RetryHang);
        Assert.True(Liveness.RetryDeterministicFailures);
        Assert.Equal(3, Liveness.MaxAttempts);        // 确定性失败:三次保险
        Assert.Equal(2, Liveness.HangMaxAttempts);    // 卡死:单独限两次(见注释)
        Assert.Equal(1500, Liveness.BackoffMs);

        Assert.Equal(2, ProbeRetryPolicy.MaxAttempts(Liveness, ProbeFailureKind.Hang));
        Assert.Equal(1500, ProbeRetryPolicy.BackoffMsAfterAttempt(Liveness, 1, ProbeFailureKind.Hang));
        foreach (var k in Deterministic)
        {
            Assert.True(ProbeRetryPolicy.ShouldRetry(Liveness, k));
            Assert.Equal(3, ProbeRetryPolicy.MaxAttempts(Liveness, k));
            Assert.Equal(1500, ProbeRetryPolicy.BackoffMsAfterAttempt(Liveness, 1, k));
        }
    }

    /// <summary>档位只由 fullFrame 选(调用点不许自己拼数字):true = 流水线生产形态探测,false = 小图活性检查。</summary>
    [Fact]
    public void The_profile_is_chosen_by_full_frame_alone()
    {
        Assert.Equal(Pipeline, ProbeRetryPolicy.ForFullFrame(true));
        Assert.Equal(Liveness, ProbeRetryPolicy.ForFullFrame(false));
    }

    /// <summary>枚举全量自检(两档各一遍):每个形态都必须有明确答案,且"为什么重试/为什么不重试"非空 ——
    /// 否则日志里会出现一句空理由,等于没解释。另外:引擎文件缺失在任何档都不重试(与显卡无关)。</summary>
    [Fact]
    public void Every_kind_has_an_explicit_answer_and_a_reason_in_both_profiles()
    {
        foreach (var profile in new[] { Pipeline, Liveness })
        {
            foreach (ProbeFailureKind kind in Enum.GetValues(typeof(ProbeFailureKind)))
            {
                if (kind == ProbeFailureKind.EngineMissing)
                {
                    Assert.False(ProbeRetryPolicy.ShouldRetry(profile, kind));
                    Assert.Equal(1, ProbeRetryPolicy.MaxAttempts(profile, kind));
                    Assert.False(string.IsNullOrWhiteSpace(ProbeRetryPolicy.WhyNotRetry(profile, kind)));
                    continue;
                }
                if (ProbeRetryPolicy.ShouldRetry(profile, kind))
                {
                    Assert.False(string.IsNullOrWhiteSpace(ProbeRetryPolicy.WhyRetry(profile, kind)));
                    // 卡死可以有单独上限(HangMaxAttempts>0 时只约束 Hang);其余形态一律用该档的 MaxAttempts
                    int expected = kind == ProbeFailureKind.Hang && profile.HangMaxAttempts > 0
                        ? profile.HangMaxAttempts
                        : profile.MaxAttempts;
                    Assert.Equal(expected, ProbeRetryPolicy.MaxAttempts(profile, kind));
                    Assert.Equal(profile.BackoffMs, ProbeRetryPolicy.BackoffMsAfterAttempt(profile, 1, kind));
                }
                else
                {
                    Assert.Equal(1, ProbeRetryPolicy.MaxAttempts(profile, kind));
                    Assert.Equal(0, ProbeRetryPolicy.BackoffMsAfterAttempt(profile, 1, kind));
                    Assert.False(string.IsNullOrWhiteSpace(ProbeRetryPolicy.WhyNotRetry(profile, kind)));
                }
            }
        }
    }

    // ───────────────── ② 流水线档:不该重试(哪怕"再试一次就会成功") ─────────────────

    /// <summary>★ 确定性失败在流水线档一次都不重试。这里让假探测的"第二次"必定成功,
    /// 于是"试了就会变好"也拦得住 —— 证明没有第二次调用,而不是恰好第二次也失败。</summary>
    [Theory]
    [InlineData(ProbeFailureKind.CrashExitCode)]
    [InlineData(ProbeFailureKind.StartupFailed)]
    [InlineData(ProbeFailureKind.DefectiveFrame)]
    [InlineData(ProbeFailureKind.NoOutput)]
    [InlineData(ProbeFailureKind.EmptyOutput)]
    [InlineData(ProbeFailureKind.EngineMissing)]
    public async Task Deterministic_failures_are_never_retried_in_the_pipeline_profile(ProbeFailureKind kind)
    {
        var (outcome, probe, delays, log) = await RunAsync(Pipeline, Fail(kind, "确定性失败"), Pass());

        Assert.False(outcome.Ok);
        Assert.Equal(kind, outcome.Kind);
        Assert.Equal("确定性失败", outcome.Detail);
        Assert.Equal(1, outcome.Attempts);
        Assert.Equal(0, outcome.Retries);
        Assert.False(outcome.Cancelled);
        Assert.Equal(new[] { 1 }, probe.AttemptNumbers);          // 只试了一次
        Assert.Empty(delays);                                      // 一次都没退避
        Assert.Contains(log, l => l.Contains("不重试", StringComparison.Ordinal)
                                  && l.Contains(ProbeRetryPolicy.WhyNotRetry(Pipeline, kind), StringComparison.Ordinal));
        Assert.DoesNotContain(log, l => l.Contains("重试第", StringComparison.Ordinal));   // 没有任何重试行
    }

    // ───────────────── ③ 流水线档:重试后成功 / 重试后仍失败 ─────────────────

    /// <summary>★ 一次瞬时超时不再判死(本轮修复的核心场景):第一次超时 → 退避 3 秒 → 第二次通过 ⇒ 结论为"可用"。</summary>
    [Fact]
    public async Task Timeout_once_then_success_is_reported_as_usable()
    {
        var (outcome, probe, delays, log) = await RunAsync(Pipeline,
            Fail(ProbeFailureKind.Hang, "60 秒无响应"), Pass());

        Assert.True(outcome.Ok);
        Assert.Equal(ProbeFailureKind.None, outcome.Kind);          // 成功:落盘写 ok(TTL 7 天),不写失败结论
        Assert.Equal(2, outcome.Attempts);
        Assert.Equal(1, outcome.Retries);
        Assert.False(outcome.Cancelled);
        Assert.Equal(new[] { 1, 2 }, probe.AttemptNumbers);
        Assert.Equal(new[] { 3000 }, delays);
        // 日志要"第几次、为什么重试、结果"三段齐全,不许静默
        Assert.Contains(log, l => l.Contains("第 1/2 次失败", StringComparison.Ordinal)
                                  && l.Contains("无响应(超时被强杀)", StringComparison.Ordinal)
                                  && l.Contains("60 秒无响应", StringComparison.Ordinal)
                                  && l.Contains("3 秒", StringComparison.Ordinal)
                                  && l.Contains("为什么重试", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Contains("重试第 2/2 次", StringComparison.Ordinal)
                                  && l.Contains("通过", StringComparison.Ordinal));
    }

    /// <summary>★ 重试后仍失败才算不可用:两次都超时 ⇒ Attempts=2、Retries=1,且**不会**再试第三次
    /// (只有"重试后仍失败"才允许落盘失败结论 + TTL 1 天 —— 见 EngineService.EnsureNcnnProbeAsync)。</summary>
    [Fact]
    public async Task Timeout_twice_is_a_failure_after_exactly_one_retry()
    {
        var (outcome, probe, delays, log) = await RunAsync(Pipeline,
            Fail(ProbeFailureKind.Hang, "60 秒无响应"),
            Fail(ProbeFailureKind.Hang, "60 秒无响应"));

        Assert.False(outcome.Ok);
        Assert.Equal(ProbeFailureKind.Hang, outcome.Kind);
        Assert.Equal("60 秒无响应", outcome.Detail);
        Assert.Equal(2, outcome.Attempts);
        Assert.Equal(1, outcome.Retries);
        Assert.False(outcome.Cancelled);                            // 真判死(不是取消)
        Assert.Equal(new[] { 1, 2 }, probe.AttemptNumbers);         // 恰好两次,没有第三次
        Assert.Equal(new[] { 3000 }, delays);
        Assert.Contains(log, l => l.Contains("重试第 2/2 次", StringComparison.Ordinal)
                                  && l.Contains("仍失败", StringComparison.Ordinal));
    }

    /// <summary>第一次就通过:不许退避、不许写任何"重试"日志(否则日志里会凭空多出一次不存在的重试)。</summary>
    [Fact]
    public async Task A_first_attempt_success_neither_waits_nor_logs_a_retry()
    {
        var (outcome, probe, delays, log) = await RunAsync(Pipeline, Pass());

        Assert.True(outcome.Ok);
        Assert.Equal(1, outcome.Attempts);
        Assert.Equal(0, outcome.Retries);
        Assert.Equal(new[] { 1 }, probe.AttemptNumbers);
        Assert.Empty(delays);
        Assert.Empty(log);
    }

    // ───────────────── ④ 挑卡档:确定性失败退避 1.5 秒、最多 3 次(旧保险) ─────────────────

    /// <summary>★ 恢复旧保险:确定性失败在挑卡档要试满 3 次、每次退避 1.5 秒
    /// (一次驱动抽风不该把独显判成没有 GPU、让整个会话停在错卡/核显上)。</summary>
    [Theory]
    [InlineData(ProbeFailureKind.CrashExitCode)]
    [InlineData(ProbeFailureKind.StartupFailed)]
    [InlineData(ProbeFailureKind.NoOutput)]
    [InlineData(ProbeFailureKind.EmptyOutput)]
    [InlineData(ProbeFailureKind.DefectiveFrame)]
    public async Task Liveness_retries_deterministic_failures_three_times_at_1_5s(ProbeFailureKind kind)
    {
        var (outcome, probe, delays, log) = await RunAsync(Liveness, Fail(kind), Fail(kind), Fail(kind));

        Assert.False(outcome.Ok);
        Assert.Equal(kind, outcome.Kind);
        Assert.Equal(3, outcome.Attempts);
        Assert.Equal(2, outcome.Retries);
        Assert.False(outcome.Cancelled);
        Assert.Equal(new[] { 1, 2, 3 }, probe.AttemptNumbers);
        Assert.Equal(new[] { 1500, 1500 }, delays);
        Assert.Contains(log, l => l.Contains("第 1/3 次失败", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Contains("第 2/3 次失败", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Contains("重试第 3/3 次", StringComparison.Ordinal)
                                  && l.Contains("仍失败", StringComparison.Ordinal));
    }

    /// <summary>第 3 次才通过也算可用(旧保险的意义就在这里:多给两次机会,而不是一次就判死)。</summary>
    [Fact]
    public async Task Liveness_third_attempt_success_counts_as_usable()
    {
        var (outcome, probe, delays, _) = await RunAsync(Liveness,
            Fail(ProbeFailureKind.DefectiveFrame), Fail(ProbeFailureKind.DefectiveFrame), Pass());

        Assert.True(outcome.Ok);
        Assert.Equal(ProbeFailureKind.None, outcome.Kind);
        Assert.Equal(3, outcome.Attempts);
        Assert.Equal(new[] { 1500, 1500 }, delays);
        Assert.Equal(new[] { 1, 2, 3 }, probe.AttemptNumbers);
    }

    /// <summary>瞬时 Hang 在挑卡档同样重试(队长裁定):小图 15 秒没退出,也可能只是驱动/设备初始化卡住。</summary>
    [Fact]
    public async Task Liveness_also_retries_a_transient_hang()
    {
        // 【2026-09-24 队长裁定后】挑卡档的卡死只重试**一次**(共 2 次):第一次卡死 → 退避 1.5 秒 → 第二次通过。
        var (outcome, probe, delays, _) = await RunAsync(Liveness,
            Fail(ProbeFailureKind.Hang, "15 秒无响应"), Pass());

        Assert.True(outcome.Ok);
        Assert.Equal(2, outcome.Attempts);
        Assert.Equal(new[] { 1500 }, delays);
        Assert.Equal(new[] { 1, 2 }, probe.AttemptNumbers);
    }

    // ───────────────── ⑤ 取消(复审 F1):不重试、不判死、不写成确定性失败 ─────────────────

    /// <summary>★ F1 的核心:取消落在"失败之后、重试之前"的退避窗口里 ⇒ 不再发起第二次尝试,
    /// outcome 明确标记 Cancelled(调用方据此**不落盘**),且日志里**不许**出现"确定性失败"这种归因
    /// —— 那会把排查方向带偏(实际情况是"用户取消了",不是"这张卡不能跑")。</summary>
    [Fact]
    public async Task Cancellation_during_backoff_marks_cancelled_and_never_retries()
    {
        var probe = new FakeProbe(Fail(ProbeFailureKind.Hang, "60 秒无响应"), Pass());
        using var cts = new CancellationTokenSource();
        var log = new List<string>();
        var outcome = await ProbeRetryPolicy.RunAsync(
            Pipeline,
            (attempt, ct) => probe.NextAsync(attempt, ct),
            (ms, ct) => { cts.Cancel(); return Task.CompletedTask; },   // 退避一开始就取消
            log.Add,
            cts.Token);

        Assert.True(outcome.Cancelled);                             // ★ 调用方据此跳过 SaveNcnnVerdict
        Assert.False(outcome.Ok);
        Assert.Equal(1, outcome.Attempts);                          // 没发起第二次
        Assert.Equal(0, outcome.Retries);
        Assert.Equal(new[] { 1 }, probe.AttemptNumbers);
        Assert.DoesNotContain(log, l => l.Contains("确定性失败", StringComparison.Ordinal));
        Assert.DoesNotContain(log, l => l.Contains("仍失败", StringComparison.Ordinal));
    }

    /// <summary>取消若发生在"下一次尝试之前"(ct 已取消),同样标记 Cancelled 且不发起新尝试、不退避。</summary>
    [Fact]
    public async Task Cancellation_before_the_retry_attempt_marks_cancelled()
    {
        var probe = new FakeProbe(Fail(ProbeFailureKind.Hang, "60 秒无响应"), Pass());
        var delays = new List<int>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var outcome = await ProbeRetryPolicy.RunAsync(
            Pipeline,
            (attempt, ct) => probe.NextAsync(attempt, ct),
            (ms, ct) => { delays.Add(ms); return Task.CompletedTask; },
            ct: cts.Token);

        Assert.True(outcome.Cancelled);
        Assert.Equal(1, outcome.Attempts);
        Assert.Equal(new[] { 1 }, probe.AttemptNumbers);
        Assert.Empty(delays);
    }

    /// <summary>反向保证:真正的失败(跑完了、判据判死)**不许**被标成取消 —— 否则调用方会漏掉该落的结论,
    /// 每次会话都白试一遍。</summary>
    [Fact]
    public async Task Completed_failures_are_never_marked_cancelled()
    {
        var (pipelineDeterministic, _, _, _) = await RunAsync(Pipeline, Fail(ProbeFailureKind.CrashExitCode));
        var (pipelineHangTwice, _, _, _) = await RunAsync(Pipeline, Fail(ProbeFailureKind.Hang), Fail(ProbeFailureKind.Hang));
        var (livenessThree, _, _, _) = await RunAsync(Liveness,
            Fail(ProbeFailureKind.DefectiveFrame), Fail(ProbeFailureKind.DefectiveFrame), Fail(ProbeFailureKind.DefectiveFrame));

        Assert.False(pipelineDeterministic.Cancelled);
        Assert.False(pipelineHangTwice.Cancelled);
        Assert.False(livenessThree.Cancelled);
        // "共探测 N 次"与落盘明细用的就是 Attempts:必须等于实际发起过的尝试数
        Assert.Equal(1, pipelineDeterministic.Attempts);
        Assert.Equal(2, pipelineHangTwice.Attempts);
        Assert.Equal(3, livenessThree.Attempts);
    }

    // ───────────────── ⑥ 日志措辞自洽("重试第 2/1 次" 必须绝迹) ─────────────────

    /// <summary>★ 两次失败的**形态可以不同**(第一次超时、第二次崩):分母必须按实际发起过的尝试上限说。
    /// 旧写法在第二次形态的 MaxAttempts(=1)上取分母 ⇒ 打出"重试第 2/1 次"这种自相矛盾的日志。</summary>
    [Fact]
    public async Task Retry_log_denominator_never_contradicts_the_attempt_number()
    {
        var (outcome, probe, _, log) = await RunAsync(Pipeline,
            Fail(ProbeFailureKind.Hang, "60 秒无响应"),
            Fail(ProbeFailureKind.CrashExitCode, "exit=0xC0000005"));

        Assert.Equal(2, outcome.Attempts);                    // = 实际发起过的尝试数("共探测 N 次"据此)
        Assert.Equal(new[] { 1, 2 }, probe.AttemptNumbers);
        Assert.Contains(log, l => l.Contains("重试第 2/2 次", StringComparison.Ordinal));

        var frac = new Regex(@"第 (\d+)/(\d+) 次");
        foreach (string line in log)
            foreach (Match m in frac.Matches(line))
                Assert.True(int.Parse(m.Groups[1].Value) <= int.Parse(m.Groups[2].Value),
                    "日志里出现了分母小于分子的「第 N/M 次」:" + line);
    }

    /// <summary>日志三段(决策 / 重试结果 / 不重试理由)的措辞逐条钉住 —— 这几行是诊断包里判
    /// "为什么那天走 ONNX"的唯一依据。</summary>
    [Fact]
    public void Log_lines_state_the_attempt_number_the_reason_and_the_result()
    {
        var hang = Fail(ProbeFailureKind.Hang, "60 秒无响应");

        string decision = ProbeRetryPolicy.DescribeRetry(Pipeline, 1, 2, hang, Pipeline.BackoffMs);
        Assert.Contains("第 1/2 次失败", decision);
        Assert.Contains("无响应(超时被强杀)", decision);     // 形态用的是 ProbeDiagnosis.ShortName
        Assert.Contains("60 秒无响应", decision);
        Assert.Contains("3 秒", decision);
        Assert.Contains("为什么重试:", decision);
        Assert.Contains(ProbeRetryPolicy.WhyRetry(Pipeline, ProbeFailureKind.Hang), decision);

        Assert.Contains("通过", ProbeRetryPolicy.DescribeRetryResult(2, 2, Pass()));
        string stillFailed = ProbeRetryPolicy.DescribeRetryResult(2, 2, hang);
        Assert.Contains("重试第 2/2 次", stillFailed);
        Assert.Contains("仍失败", stillFailed);
        Assert.Contains("无响应(超时被强杀)", stillFailed);

        string noRetry = ProbeRetryPolicy.DescribeNoRetry(Pipeline, 1, Fail(ProbeFailureKind.DefectiveFrame, "近黑"));
        Assert.Contains("不重试", noRetry);
        Assert.Contains("近黑", noRetry);
        Assert.Contains(ProbeRetryPolicy.WhyNotRetry(Pipeline, ProbeFailureKind.DefectiveFrame), noRetry);

        // 挑卡档的"为什么重试"必须说明这是挑卡路径的保险(而不是含糊其辞)
        string livenessRetry = ProbeRetryPolicy.DescribeRetry(Liveness, 1, 3, Fail(ProbeFailureKind.DefectiveFrame), Liveness.BackoffMs);
        Assert.Contains("1.5 秒", livenessRetry);
        Assert.Contains("第 1/3 次失败", livenessRetry);
    }

    /// <summary>明细为空时日志不许留下一个孤零零的分号(格式要能直接进诊断包)。</summary>
    [Fact]
    public void Log_lines_do_not_leave_a_dangling_separator_when_detail_is_empty()
    {
        Assert.DoesNotContain(";)", ProbeRetryPolicy.DescribeRetry(Pipeline, 1, 2, Fail(ProbeFailureKind.Hang), 3000));
        Assert.DoesNotContain(";)", ProbeRetryPolicy.DescribeRetryResult(2, 2, Fail(ProbeFailureKind.Hang)));
        Assert.DoesNotContain(";)", ProbeRetryPolicy.DescribeNoRetry(Pipeline, 1, Fail(ProbeFailureKind.NoOutput)));
    }

    /// <summary>空参数要么被挡住、要么给明确结果:null 委托直接抛,别让"没接线的重试"静默变成不重试。</summary>
    [Fact]
    public async Task Missing_probe_or_backoff_delegate_is_rejected_loudly()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            ProbeRetryPolicy.RunAsync(Pipeline, null!, (ms, ct) => Task.CompletedTask));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            ProbeRetryPolicy.RunAsync(Pipeline, (attempt, ct) => Task.FromResult(Pass()), null!));
    }
}
