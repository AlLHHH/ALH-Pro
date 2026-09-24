namespace AlhPro.Core;

/// <summary>一次"真机探测"尝试的结果:通过与否 + 失败形态 + 明细(纯数据,零 I/O、零时钟)。
/// 【为什么要当成一个类型传】重试判据必须看【失败形态】而不是只看 true/false —— 见 <see cref="ProbeRetryPolicy"/>。</summary>
public readonly record struct ProbeAttempt(bool Ok, ProbeFailureKind Kind, string Detail);

/// <summary>探测(含重试)的最终结果。<c>Attempts</c> 一律如实记录(要写进落盘明细,便于人工核查)。
/// 【Cancelled 为什么必须单独有一个标志】取消不是"这张卡不可用":取消只说明**用户/任务不要这次探测了**,
/// 探测本身可能根本没跑完(最常见的是卡在"超时失败之后、重试之前"那段退避里)。
/// 调用方据此**不落盘任何结论**(不写 1 天的否定结论),否则"点一次取消"就能让该引擎/模型
/// 在接下来一整天里全程走 ONNX 慢路。</summary>
public readonly record struct ProbeRetryOutcome(bool Ok, ProbeFailureKind Kind, string Detail, int Attempts, bool Cancelled = false)
{
    /// <summary>真正重试了几次(= 尝试次数 - 1)。日志与落盘都据此说清"这次到底试了几回"。</summary>
    public int Retries => Attempts - 1;
}

/// <summary>一趟探测的"重试档"(profile):试几次 / 每次退避多久 / 哪些失败形态值得再试。
/// 【为什么必须分档(2026-09-24 队长裁定)】两类调用点的代价与风险**完全不同**,用一套数字必然顾此失彼:
///   · 流水线全帧探测(1080×1920,单次最长 60 秒,结果会落盘 7 天/1 天):它只该为"瞬时超时"多花一个窗口;
///   · 启动自检挑卡(320×240,单次 15 秒,纯进程内结论):这里是"一次驱动抽风就把独显判成没有 GPU、
///     整个会话停在错卡/核显上"的事故高发区(本项目反复出过:选独显跑核显、编号错位),
///     旧代码就是为此重试 3 次 —— 该保险必须留着,代价只是 1.5 秒×2。
/// 判据仍只有一处实现(<see cref="ProbeRetryPolicy"/>),调用点只选档、不各写一份 if。</summary>
public readonly record struct ProbeRetryProfile(
    string Name,
    bool RetryHang,
    bool RetryDeterministicFailures,
    int MaxAttempts,
    int BackoffMs);

/// <summary>【探测失败要不要重试】的纯逻辑判据 + 编排(2026-09-24)。
///
/// 【为什么必须有它 · 真机代价】2026-09-22 22:22 用户机器上一次
/// 「realesrgan GPU(0) 60 秒无响应(疑似 hang)」被强杀 → 当场落了一条失败结论
/// (ncnn-probe.txt,TTL 1 天)→ 之后**一整天**该引擎/该模型全程走 ONNX 慢路
/// (实测 ONNX 落 CPU 是 3880 ms/帧,而标称 ncnn animevideov3 是 0.26~0.30 秒/帧)。
/// 而这次超时本身就"不干净":同一个探测函数的注释自己写着 realesrgan-x4plus 在 4060 上要 24.1 秒
/// —— 重模型 + 冷启动着色器编译 + 卡正忙,健康卡也可能一次踩到 60 秒。一次瞬时超时不该判死一整天。
///
/// 【为什么超时/无响应值得重试】它是**唯一**可能由"当时的瞬时状态"造成的形态 —— 进程一直没退出,
/// 既可能是真 hang,也可能只是慢(GPU 被别的任务占着、驱动刚在忙、显存刚被回收)。这种"忙/慢"会自己好。
/// 【为什么确定性形态在流水线档不重试】初始化即崩/进程起不来(进程**已经退出**,退出码就是它的结论)、
/// 出图但坏帧/无产出/空产出(引擎**跑完了**并交出结果,说明它在这张卡上的行为是确定的,重算一遍同样结果)、
/// 引擎文件缺失(与显卡无关):重试只是把"不能用"这个结论推迟十几秒再告诉用户,结论不会变。
/// 【为什么挑卡档反而要重试确定性形态】见 <see cref="ProbeRetryProfile"/>:那里"一次瞬时抽风"的代价是
/// **选错卡/整段落 CPU**,而不是"多等一次";旧代码就是 1.5 秒×3 的保险,队长裁定恢复。
///
/// 【接口约束】凡是要"重试探测"的地方都必须问这里,不许各处自己写 <c>if (kind == Hang)</c> ——
/// 判据散开以后必然走偏(旧代码就是:确定性失败重试 3 次、真超时反而一次都不试,两边都错)。
/// 判据只认形态与档位、不看次数、不读时钟 ⇒ 可以单测钉住(见 AlhPro.Tests/ProbeRetryPolicyTests.cs)。</summary>
public static class ProbeRetryPolicy
{
    // ───────────────── 两档定值(队长裁定;单测逐项钉住) ─────────────────

    /// <summary>【流水线全帧探测档】<c>EnsureNcnnProbeAsync</c>(fullFrame:true,1080×1920,单次最长 60 秒):
    /// 只对超时/无响应重试**一次**,退避 **3 秒**;确定性失败一次都不重试(理由见类注释)。
    /// 【为什么退避 3 秒】刚被强杀的引擎进程要等驱动回收句柄/释放显存,这个窗口是 1~2 秒量级;
    /// 太短(几百毫秒)等于立刻再撞同一个瞬时状态,太长则让用户白等(建议区间 2~5 秒,取中位)。
    /// 【为什么只加一次】最坏 = 60 秒 + 3 秒 + 60 秒 ≈ 2 分钟;再往上加,用户等待本身会变成新问题,
    /// 而真 hang 不会因为多试就变好。</summary>
    public static readonly ProbeRetryProfile PipelineFullFrameProbe =
        new(Name: "pipeline-full-frame", RetryHang: true, RetryDeterministicFailures: false,
            MaxAttempts: 2, BackoffMs: 3000);

    /// <summary>【小图活性检查/挑卡档】<c>FindBestWorkingGpuAsync</c> 的挑卡、编号纠正、核显切换(fullFrame:false,
    /// 320×240,单次 15 秒):**确定性失败也退避 1.5 秒、最多 3 次**(恢复 t3 里被顺带删掉的旧保险);
    /// 瞬时 Hang 同样重试(同一档位、同一退避)。
    /// 【代价与收益】最坏 3×(15 秒) + 2×1.5 秒 ≈ 48 秒,只发生在"探测真的失败"时(正常卡首探几百毫秒);
    /// 换来的是"一次驱动抽风不至于把独显判成没有 GPU、让整个会话停在错卡/核显上"。
    /// 【为什么退避 1.5 秒而不是 3 秒】这一档本来就快、且它是启动自检的热路径:1.5 秒足够给驱动/显存一个回收窗口,
    /// 又不会让自检明显变慢(与旧代码逐字一致)。</summary>
    public static readonly ProbeRetryProfile DeviceLivenessCheck =
        new(Name: "device-liveness", RetryHang: true, RetryDeterministicFailures: true,
            MaxAttempts: 3, BackoffMs: 1500);

    /// <summary>★ 档位只由 fullFrame 决定(调用点不该自己拼档):true = 流水线生产形态探测,false = 小图活性检查/挑卡。</summary>
    public static ProbeRetryProfile ForFullFrame(bool fullFrame)
        => fullFrame ? PipelineFullFrameProbe : DeviceLivenessCheck;

    /// <summary>确定性失败形态(进程已退出、或引擎已交出结果 ⇒ 每次都会重现)。
    /// <see cref="ProbeFailureKind.EngineMissing"/> 不算:那是"文件不在"的配置问题,与显卡无关,任何档都不重试。</summary>
    public static bool IsDeterministicFailure(ProbeFailureKind kind)
        => kind is ProbeFailureKind.CrashExitCode or ProbeFailureKind.StartupFailed
                or ProbeFailureKind.NoOutput or ProbeFailureKind.EmptyOutput or ProbeFailureKind.DefectiveFrame;

    /// <summary>★ 判据入口:这个形态在**这一档**下该不该再试一次。</summary>
    public static bool ShouldRetry(ProbeRetryProfile profile, ProbeFailureKind kind)
        => kind == ProbeFailureKind.Hang
            ? profile.RetryHang
            : IsDeterministicFailure(kind) && profile.RetryDeterministicFailures;

    /// <summary>这个形态在**这一档**下允许的总尝试次数(可重试 = profile.MaxAttempts,否则 1)。</summary>
    public static int MaxAttempts(ProbeRetryProfile profile, ProbeFailureKind kind)
        => ShouldRetry(profile, kind) ? profile.MaxAttempts : 1;

    /// <summary>第 N 次尝试失败后、重试前要等多久(毫秒)。不该重试的形态返回 0(调用方不该拿它去等)。</summary>
    public static int BackoffMsAfterAttempt(ProbeRetryProfile profile, int failedAttempt, ProbeFailureKind kind)
        => ShouldRetry(profile, kind) && failedAttempt >= 1 ? profile.BackoffMs : 0;

    /// <summary>【为什么重试】—— 日志必须如实写出理由,不许静默重试;不该重试的形态给空串(由 WhyNotRetry 补上)。
    /// 纯函数:措辞被单测钉住,就不会在下次改动里被悄悄丢掉。</summary>
    public static string WhyRetry(ProbeRetryProfile profile, ProbeFailureKind kind) => kind switch
    {
        ProbeFailureKind.Hang =>
            "超时/无响应是唯一可能由瞬时状态(卡正忙/驱动忙/刚被强杀的进程还在回收显存)造成的形态,退避后值得再问一次",
        _ when IsDeterministicFailure(kind) =>
            $"本档是小图活性检查(挑卡/编号纠正):一次瞬时抽风就少一张候选卡、甚至让整个会话停在错卡上,"
            + $"代价只是退避 {profile.BackoffMs} 毫秒再探一次",
        _ => "",
    };

    /// <summary>【为什么不重试】—— 本档策略给出的理由(确定性形态每次都会重现,重试只是白等)。
    /// 理由要能进日志/诊断包,否则下一个人会以为"这里忘了重试"。</summary>
    public static string WhyNotRetry(ProbeRetryProfile profile, ProbeFailureKind kind)
    {
        string baseReason = kind switch
        {
            ProbeFailureKind.CrashExitCode => "进程已退出,退出码就是它的结论(初始化崩溃/驱动缺陷不会因为等几秒而消失)",
            ProbeFailureKind.StartupFailed => "进程起不来是环境问题(路径/权限/被杀),重试同样起不来",
            ProbeFailureKind.DefectiveFrame => "引擎跑完了并交出坏帧,说明它在这张卡上的行为是确定的,重算一遍还是坏帧",
            ProbeFailureKind.NoOutput => "引擎跑完却没交产出文件,属结构性失败(参数/模型加载),重试只是白等",
            ProbeFailureKind.EmptyOutput => "引擎跑完交出 0 字节产出,属结构性失败,重试只是白等",
            ProbeFailureKind.EngineMissing => "引擎文件不存在,与显卡无关,重试一万次也还是不存在",
            ProbeFailureKind.Hang => "超时/无响应",
            ProbeFailureKind.None => "没有失败形态(已经通过),不需要重试",
            _ => "该形态不属超时/无响应",
        };
        if (kind is ProbeFailureKind.EngineMissing or ProbeFailureKind.None) return baseReason;
        if (ShouldRetry(profile, kind)) return baseReason + "(注意:本档其实允许重试该形态,这条只在判据变更后才会出现)";
        return baseReason + $":本档({profile.Name})不重试该形态 —— 重试只是把等待拉长";
    }

    /// <summary>重试决策的日志行:"第几次 · 为什么重试 · 退避多久"。
    /// 【为什么要写"第几次"和"为什么"】诊断包里必须一眼看出"这次探测试了几回、为什么试第二次";
    /// 旧日志在超时路径上一句重试理由都没有,于是"为什么每天都走 ONNX"只能靠猜。</summary>
    public static string DescribeRetry(ProbeRetryProfile profile, int failedAttempt, int maxAttempts, ProbeAttempt failed, int backoffMs)
        => $"[探测] 第 {failedAttempt}/{maxAttempts} 次失败({ProbeDiagnosis.ShortName(failed.Kind)}"
           + (string.IsNullOrEmpty(failed.Detail) ? "" : ";" + failed.Detail)
           + $")→ 退避 {backoffMs / 1000.0:0.#} 秒后重试第 {failedAttempt + 1}/{maxAttempts} 次"
           + $"(为什么重试:{WhyRetry(profile, failed.Kind)})";

    /// <summary>重试那一次的【结果】行(通过 / 仍失败),与 <see cref="DescribeRetry"/> 配对
    /// ⇒ 日志里"第几次、为什么、结果"三段齐全。
    /// 【分母必须 >= 分子】重试的两次失败形态可能不同(第一次超时、第二次变成崩溃):分母按**实际发起过的
    /// 尝试上限**说,绝不许出现"重试第 2/1 次"这种自相矛盾(调用方用 <see cref="RunAsync"/> 时已保证)。</summary>
    public static string DescribeRetryResult(int attempt, int allowedAttempts, ProbeAttempt r)
        => r.Ok
            ? $"[探测] 重试第 {attempt}/{Math.Max(allowedAttempts, attempt)} 次:通过 → 按可用处理(不落盘失败结论)"
            : $"[探测] 重试第 {attempt}/{Math.Max(allowedAttempts, attempt)} 次:仍失败({ProbeDiagnosis.ShortName(r.Kind)}"
              + (string.IsNullOrEmpty(r.Detail) ? "" : ";" + r.Detail) + ")→ 按不可用处理";

    /// <summary>不重试时的日志行(把理由说清楚,免得后来人以为"忘了重试")。</summary>
    public static string DescribeNoRetry(ProbeRetryProfile profile, int attempt, ProbeAttempt failed)
        => $"[探测] 第 {attempt} 次失败({ProbeDiagnosis.ShortName(failed.Kind)}"
           + (string.IsNullOrEmpty(failed.Detail) ? "" : ";" + failed.Detail)
           + $")→ 不重试:{WhyNotRetry(profile, failed.Kind)}";

    /// <summary>★ 重试编排(纯逻辑:不碰进程、不读时钟、不写文件 —— 探测动作、退避等待、日志全部由调用方注入)。
    /// 【为什么把循环也放进 Core】"该不该重试 / 试几次 / 退避多久 / 日志怎么写"是判据的一部分;
    /// 放这里就能用**假探测 + 假退避**把四条路径(该重试 / 不该重试 / 重试后成功 / 重试后仍失败)全钉住,
    /// 完全不依赖真显卡(触发条件在特定卡上,没法按需复现)。
    /// 取消语义:探测动作拿调用方的 CancellationToken;若取消落在退避期间(或下一次尝试之前),这里
    /// **不再发起新尝试**,并返回 <see cref="ProbeRetryOutcome.Cancelled"/>=true —— "取消"与"判据判死"
    /// 是两件事,调用方必须分开处理(取消不落盘)。这里的返回值不是异常:检测到取消时结论已经定了。</summary>
    /// <param name="profile">重试档(<see cref="ForFullFrame"/> 选)。</param>
    /// <param name="attemptAsync">第 N 次(N 从 1 开始)尝试;实现里必须把失败形态填进 <see cref="ProbeAttempt.Kind"/>。</param>
    /// <param name="backoffAsync">退避等待(毫秒);单测里换成"记账不真等"。</param>
    /// <param name="log">日志回调(重试决策与重试结果都从这里出去);null = 不留日志(只有单测会这么用)。</param>
    public static async Task<ProbeRetryOutcome> RunAsync(
        ProbeRetryProfile profile,
        Func<int, CancellationToken, Task<ProbeAttempt>> attemptAsync,
        Func<int, CancellationToken, Task> backoffAsync,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attemptAsync);
        ArgumentNullException.ThrowIfNull(backoffAsync);
        int attempt = 1;
        int allowedAttempts = 1;   // 实际发起过的尝试上限(重试决策时更新),供"重试结果"日志写分母
        while (true)
        {
            var r = await attemptAsync(attempt, ct).ConfigureAwait(false);
            if (r.Ok)
            {
                if (attempt > 1) log?.Invoke(DescribeRetryResult(attempt, allowedAttempts, r));
                return new ProbeRetryOutcome(true, ProbeFailureKind.None, r.Detail, attempt);
            }
            int maxAttempts = MaxAttempts(profile, r.Kind);
            if (!ShouldRetry(profile, r.Kind) || attempt >= maxAttempts)
            {
                log?.Invoke(attempt > 1
                    ? DescribeRetryResult(attempt, allowedAttempts, r)
                    : DescribeNoRetry(profile, attempt, r));
                return new ProbeRetryOutcome(false, r.Kind, r.Detail, attempt);
            }
            if (ct.IsCancellationRequested)
                return new ProbeRetryOutcome(false, r.Kind, r.Detail, attempt, Cancelled: true);
            allowedAttempts = maxAttempts;
            int backoffMs = BackoffMsAfterAttempt(profile, attempt, r.Kind);
            log?.Invoke(DescribeRetry(profile, attempt, maxAttempts, r, backoffMs));
            await backoffAsync(backoffMs, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
                return new ProbeRetryOutcome(false, r.Kind, r.Detail, attempt, Cancelled: true);
            attempt++;
        }
    }
}
