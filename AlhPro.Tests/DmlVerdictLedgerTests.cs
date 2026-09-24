using System;
using System.Collections.Generic;
using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>
/// 「本机这个(业务域 · 模型 · 设备)上 DirectML 能不能跑」的**落盘结论账本**单测(2026-09-24)。
///
/// 【为什么要有它】真机日志(diagnostic.log,2026-09-23)里音频分离在 21:41:58 / 21:42:48 / 23:07:52 /
/// 23:28:25 四次 <c>音频分离 GPU 推理失败 — DmlFusedNode_0_0</c>(HT-Demucs 图融合失败,失败点在
/// session.Run 而不是建会话),每次白等 5~9 秒;而进程内连击表要连吃 3 次才熔断、**进程重启就忘**
/// ⇒ 每个应用会话都重新白试。结论必须落盘,并且必须细到(域 · 模型 · 设备)。
///
/// 【为什么用固定时间戳而不是 DateTime.Now】账本的过期判定收 <c>nowUnix</c> 参数(时钟注入),
/// 于是"1 天后过期"这种规则可以在一瞬间被钉住 —— 触发条件在特定显卡上,没法按需复现,只能靠单测。
///
/// 本文件按验收逐条覆盖:① 没结论⇒该试 ② 失败到上限⇒不再试 ③ 成功⇒清除 ④ 过期⇒重新试
/// ⑤ 键按 域·模型·设备 隔离 ⑥ 落盘文件损坏或为空⇒不崩且按"未测"处理。
/// </summary>
public class DmlVerdictLedgerTests
{
    private const string Domain = "audio";
    private const string Model = "htdemucs_ft_vocals.onnx";
    private const string OtherModel = "htdemucs.onnx";

    /// <summary>固定时刻(UTC 秒)。测试全程只用它做加法,不读真实时钟 ⇒ 不会因机器时间/时区飘。</summary>
    private const long T0 = 1_790_000_000;

    private static DmlVerdictLedger NewLedger(int limit = 3) => new(limit);
    private static string Key(string domain, string model, int device) => DmlVerdictLedger.Key(domain, model, device);

    // ───────────────── ① 没结论 ⇒ 该试(空账本 / 空文件 / 空文本) ─────────────────

    [Fact]
    public void Untested_machine_must_still_try_dml_once()
    {
        var ledger = NewLedger();
        Assert.True(ledger.ShouldAttempt(Domain, Model, 0, T0));
        Assert.Null(ledger.Verdict(Domain, Model, 0, T0));
        Assert.Equal(0, ledger.Failures(Domain, Model, 0, T0));
    }

    /// <summary>空文件 = 从没测过 ⇒ 照现状试一次(绝不能因为"读不到结论"就判 CPU)。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\n\t\n")]
    [InlineData("# key\tok\tfailures\tatUnix\tnote\n")]
    public void An_empty_or_header_only_file_means_untested(string? text)
    {
        var ledger = DmlVerdictLedger.FromText(text, 3);
        Assert.True(ledger.ShouldAttempt(Domain, Model, 0, T0));
        Assert.Null(ledger.Verdict(Domain, Model, 0, T0));
    }

    // ───────────────── ② 失败累计到上限 ⇒ 不再试 ─────────────────

    /// <summary>前两次仍给机会(偶发抖动不该一次就判死),第三次才认定"这个组合在本机跑不了"。</summary>
    [Fact]
    public void Stops_attempting_only_after_the_failure_limit()
    {
        var ledger = NewLedger(3);

        Assert.False(ledger.NoteFailure(Domain, Model, 0, T0));          // 第 1 次:没到上限
        Assert.True(ledger.ShouldAttempt(Domain, Model, 0, T0));
        Assert.Equal(1, ledger.Failures(Domain, Model, 0, T0));

        Assert.False(ledger.NoteFailure(Domain, Model, 0, T0 + 1));      // 第 2 次
        Assert.True(ledger.ShouldAttempt(Domain, Model, 0, T0 + 1));

        Assert.True(ledger.NoteFailure(Domain, Model, 0, T0 + 2));       // 第 3 次:达上限
        Assert.False(ledger.ShouldAttempt(Domain, Model, 0, T0 + 2));
        Assert.False(ledger.Verdict(Domain, Model, 0, T0 + 2));
        Assert.Equal(3, ledger.Failures(Domain, Model, 0, T0 + 2));
    }

    /// <summary>上限 1 的组合(建会话就失败那类结构性失败)一次即生效。</summary>
    [Fact]
    public void A_structural_failure_can_be_latched_immediately()
    {
        var ledger = NewLedger(3);
        ledger.NoteDenial(Domain, Model, 0, T0, "建会话失败");
        Assert.False(ledger.ShouldAttempt(Domain, Model, 0, T0));
        Assert.Equal(3, ledger.Failures(Domain, Model, 0, T0));
    }

    /// <summary>失败累计跨"进程重启"有效:同一份落盘文本新开一个账本,结论照样生效(这才是本条修复的意义)。</summary>
    [Fact]
    public void A_denial_survives_a_restart_through_the_file()
    {
        var before = NewLedger(3);
        before.NoteFailure(Domain, Model, 0, T0);
        before.NoteFailure(Domain, Model, 0, T0 + 1);
        before.NoteFailure(Domain, Model, 0, T0 + 2);

        // 模拟重启:只把文本交给一个全新账本(文件 I/O 由调用方负责,这里只验纯逻辑)
        string text = DmlVerdictLedger.Format(before.Snapshot(T0 + 2));
        var after = DmlVerdictLedger.FromText(text, 3);

        Assert.False(after.ShouldAttempt(Domain, Model, 0, T0 + 3));
        Assert.Equal(3, after.Failures(Domain, Model, 0, T0 + 3));
    }

    /// <summary>重启后继续失败要接着数(不是每次从 1 开始):落盘 1 次 + 本次 2 次 = 上限。</summary>
    [Fact]
    public void Failures_keep_accumulating_across_restarts()
    {
        var first = NewLedger(3);
        first.NoteFailure(Domain, Model, 0, T0);

        var second = DmlVerdictLedger.FromText(DmlVerdictLedger.Format(first.Snapshot(T0)), 3);
        Assert.False(second.NoteFailure(Domain, Model, 0, T0 + 10));      // 累计 2
        Assert.True(second.NoteFailure(Domain, Model, 0, T0 + 20));       // 累计 3 ⇒ 判死
        Assert.False(second.ShouldAttempt(Domain, Model, 0, T0 + 20));
    }

    // ───────────────── ③ 成功 ⇒ 清除该键(连击归零、否定结论立即失效) ─────────────────

    [Fact]
    public void A_success_clears_the_failure_verdict()
    {
        var ledger = NewLedger(3);
        ledger.NoteFailure(Domain, Model, 0, T0);
        ledger.NoteFailure(Domain, Model, 0, T0 + 1);
        ledger.NoteFailure(Domain, Model, 0, T0 + 2);
        Assert.False(ledger.ShouldAttempt(Domain, Model, 0, T0 + 2));    // 已判死

        Assert.True(ledger.NoteSuccess(Domain, Model, 0, T0 + 3));       // 返回值 = 清掉了一条生效中的否定结论

        Assert.True(ledger.ShouldAttempt(Domain, Model, 0, T0 + 3));
        Assert.True(ledger.Verdict(Domain, Model, 0, T0 + 3));
        Assert.Equal(0, ledger.Failures(Domain, Model, 0, T0 + 3));
        Assert.False(ledger.NoteFailure(Domain, Model, 0, T0 + 4));      // 重新从第 1 次开始数
        Assert.Equal(1, ledger.Failures(Domain, Model, 0, T0 + 4));
    }

    /// <summary>没有否定结论可清时,成功不该报"已复位"(否则日志每块都喊一次)。</summary>
    [Fact]
    public void A_success_without_a_denial_reports_nothing_cleared()
    {
        var ledger = NewLedger(3);
        Assert.False(ledger.NoteSuccess(Domain, Model, 0, T0));
        ledger.NoteFailure(Domain, Model, 0, T0);                       // 只有 1 次,还没判死
        Assert.False(ledger.NoteSuccess(Domain, Model, 0, T0 + 1));
    }

    // ───────────────── ④ 过期 ⇒ 重新试(成功/失败两种 TTL) ─────────────────

    /// <summary>失败结论 TTL 到点即失效 ⇒ 允许重新试一次(让机器自愈:换驱动/腾显存之后不该永久禁用)。</summary>
    [Fact]
    public void An_expired_failure_verdict_allows_a_retry()
    {
        var ledger = new DmlVerdictLedger(3, successTtl: TimeSpan.FromDays(7), failureTtl: TimeSpan.FromDays(1));
        for (int i = 0; i < 3; i++) ledger.NoteFailure(Domain, Model, 0, T0);

        Assert.False(ledger.ShouldAttempt(Domain, Model, 0, T0 + 3600));            // 1 小时后仍生效
        Assert.False(ledger.ShouldAttempt(Domain, Model, 0, T0 + 24 * 3600));       // 正好到 TTL 边界:仍算有效

        long after = T0 + 24 * 3600 + 1;
        Assert.True(ledger.ShouldAttempt(Domain, Model, 0, after));                 // 过期 ⇒ 重新试
        Assert.Null(ledger.Verdict(Domain, Model, 0, after));
        Assert.Equal(0, ledger.Failures(Domain, Model, 0, after));
    }

    /// <summary>成功与失败用**不同** TTL:成功留得久(7 天),失败短(1 小时)。
    /// 用两条同一时刻写下的结论对照,把"不对称"钉死。</summary>
    [Fact]
    public void Success_and_failure_use_different_ttls()
    {
        var ledger = new DmlVerdictLedger(1, successTtl: TimeSpan.FromDays(7), failureTtl: TimeSpan.FromHours(1));
        ledger.NoteFailure(Domain, OtherModel, 0, T0);      // 失败 → 1 小时后过期
        ledger.NoteSuccess(Domain, Model, 0, T0);           // 成功 → 7 天后过期

        long twoHoursLater = T0 + 2 * 3600;
        Assert.True(ledger.ShouldAttempt(Domain, OtherModel, 0, twoHoursLater));     // 失败结论已过期 ⇒ 重新试
        Assert.True(ledger.Verdict(Domain, Model, 0, twoHoursLater));                // 成功结论仍然有效

        long eightDaysLater = T0 + 8 * 24 * 3600;
        Assert.Null(ledger.Verdict(Domain, Model, 0, eightDaysLater));               // 成功结论也过期 ⇒ 当"未测"
        Assert.True(ledger.ShouldAttempt(Domain, Model, 0, eightDaysLater));
    }

    /// <summary>【2026-09-24 复审修正 · 原先这条断言把缺陷钉成了期望值】AtUnix 落在未来(时钟回拨 / RTC 偏差 /
    /// VM 快照回滚 / 手改文件)时按「未测」处理:摘掉该条并允许重新试一次 —— 与 EngineService 的 ncnn
    /// 结论口径一致(<c>EnsureNcnnVerdictsLoaded_NoLock</c> 对未来时间戳就是丢弃 = 未测)。
    /// 【旧行为错在哪】原实现把年龄压成 0(当作"刚写下"),于是一条未来 1 年的否定结论会让这台机器
    /// **整整一年不再试 DML**(审查者独立探针复现:AtUnix=2033、now=2026-09 ⇒ ShouldAttempt 一直 false),
    /// 只能靠手删 settings\dml-verdicts.txt 恢复。fail-open 的代价只是"再试一次"(几秒),
    /// 远小于"永久走 CPU"。</summary>
    [Fact]
    public void A_future_timestamp_is_discarded_and_means_untested()
    {
        var ledger = new DmlVerdictLedger(3, failureTtl: TimeSpan.FromDays(1));
        for (int i = 0; i < 3; i++) ledger.NoteFailure(Domain, Model, 0, T0 + 100_000);
        Assert.False(ledger.ShouldAttempt(Domain, Model, 0, T0 + 100_000));   // 正常计时下它本来是"判死"

        // 时钟回拨(now 早于写入时刻)⇒ 这条结论不生效、且被摘除 ⇒ 重新试一次
        Assert.True(ledger.ShouldAttempt(Domain, Model, 0, T0));
        Assert.Null(ledger.Verdict(Domain, Model, 0, T0));
        Assert.Equal(0, ledger.Failures(Domain, Model, 0, T0));
        Assert.Empty(ledger.Snapshot(T0));

        // 摘除后时钟再走回未来也不会"复活"(不会被当成刚写下)
        Assert.True(ledger.ShouldAttempt(Domain, Model, 0, T0 + 100_000 + 3600));
        Assert.Null(ledger.Verdict(Domain, Model, 0, T0 + 100_000 + 3600));

        // 审查者独立探针的原始场景:写入时刻落在 2033(而 now = 2026-09)时,旧行为会让这台机器一直
        // ShouldAttempt=false(再走 6 年仍 false)。现在:到那一刻之前它就是"未来时间戳" ⇒ 当未测。
        long at2033 = T0 + 6L * 365 * 24 * 3600;
        var far = new DmlVerdictLedger(3, failureTtl: TimeSpan.FromDays(1));
        for (int i = 0; i < 3; i++) far.NoteFailure(Domain, Model, 0, at2033);
        Assert.False(far.ShouldAttempt(Domain, Model, 0, at2033));             // 它确实是一条"判死"结论……
        Assert.True(far.ShouldAttempt(Domain, Model, 0, at2033 - 3600));       // ……但落在未来 ⇒ 不生效,重新试
    }

    /// <summary>未来时间戳的**成功**结论同样丢弃:fail-open 的代价仅是下一次成功时多写一次文件
    /// (它只用来清账与展示,丢了也不会让谁被误判)。</summary>
    [Fact]
    public void A_future_timestamped_success_is_also_discarded()
    {
        var ledger = NewLedger();
        ledger.NoteSuccess(Domain, Model, 0, T0 + 100_000);

        Assert.Null(ledger.Verdict(Domain, Model, 0, T0));
        Assert.True(ledger.ShouldAttempt(Domain, Model, 0, T0));
        Assert.Empty(ledger.Snapshot(T0));
    }

    // ───────────────── ⑤ 键按 域 · 模型 · 设备 隔离 ─────────────────

    [Fact]
    public void Keys_are_isolated_by_domain_model_and_device()
    {
        var ledger = NewLedger(1);
        ledger.NoteFailure(Domain, Model, 0, T0);

        Assert.False(ledger.ShouldAttempt(Domain, Model, 0, T0));            // 本键:判死
        Assert.True(ledger.ShouldAttempt("video", Model, 0, T0));            // 别的域不受牵连
        Assert.True(ledger.ShouldAttempt(Domain, OtherModel, 0, T0));        // 别的模型不受牵连
        Assert.True(ledger.ShouldAttempt(Domain, Model, 1, T0));             // 别的卡不受牵连
    }

    /// <summary>设备号是整数段 + 整键相等匹配 ⇒ 设备 1 与 10 绝不互相误吃
    /// (ncnn 那边因为前缀匹配少了收尾的 <c>|</c> 踩过这个坑)。</summary>
    [Fact]
    public void Device_numbers_do_not_bleed_into_each_other()
    {
        var ledger = NewLedger(1);
        ledger.NoteFailure(Domain, Model, 1, T0);
        Assert.False(ledger.ShouldAttempt(Domain, Model, 1, T0));
        Assert.True(ledger.ShouldAttempt(Domain, Model, 10, T0));
        Assert.True(ledger.ShouldAttempt(Domain, Model, 0, T0));
        Assert.NotEqual(Key(Domain, Model, 1), Key(Domain, Model, 10));
    }

    /// <summary>归一化:大小写/空白不敏感(Windows 路径与模型文件名都不区分大小写),但域/模型之间的
    /// 分隔符注入不会拼出同一个键。</summary>
    [Fact]
    public void Keys_are_normalized_and_cannot_collide()
    {
        Assert.Equal(Key(Domain, Model, 0), Key(" Audio ", " HTDemucs_FT_VOCALS.ONNX ", 0));
        Assert.NotEqual(Key("a|b", "c", 0), Key("a", "b|c", 0));
        Assert.NotEqual(Key("a\tb", "c", 0), Key("a", "b\tc", 0));
    }

    /// <summary>用户主动选 CPU(设备 &lt; 0)时账本不介入 —— 原样放行,且不记账
    /// (与 <see cref="DmlAttemptLedger"/> 同口径)。</summary>
    [Fact]
    public void The_ledger_ignores_negative_devices()
    {
        var ledger = NewLedger(1);
        ledger.NoteFailure(Domain, Model, 0, T0);
        Assert.True(ledger.ShouldAttempt(Domain, Model, -1, T0));
        Assert.False(ledger.NoteFailure(Domain, Model, -1, T0));
        Assert.False(ledger.NoteSuccess(Domain, Model, -1, T0));
        Assert.Null(ledger.Verdict(Domain, Model, -1, T0));
    }

    // ───────────────── ⑥ 落盘文件损坏/为空 ⇒ 不崩,按"未测"处理 ─────────────────

    /// <summary>各种坏行都只跳过该行,绝不抛异常、也绝不因此判 CPU。</summary>
    [Theory]
    [InlineData("整行都是垃圾")]
    [InlineData("a\tTrue\t0")]                                  // 字段不够
    [InlineData("a\tmaybe\t0\t1790000000")]                     // bool 解析失败
    [InlineData("a\tTrue\txx\t1790000000")]                     // 次数解析失败
    [InlineData("a\tTrue\t-1\t1790000000")]                     // 负数次数
    [InlineData("a\tTrue\t0\tzzz")]                             // 时间解析失败
    [InlineData("a\tTrue\t0\t0")]                               // 非法时间戳
    [InlineData("a\tFalse\t-1\t1790000000")]                    // 负数次数(False 也拒)
    [InlineData("\tTrue\t0\t1790000000")]                       // 空键
    public void Corrupt_lines_never_throw_and_mean_untested(string text)
    {
        var ledger = DmlVerdictLedger.FromText(text, 3);
        Assert.True(ledger.ShouldAttempt(Domain, Model, 0, T0));
        Assert.Empty(ledger.Snapshot(T0));                          // 坏行一条都不留
        Assert.Empty(DmlVerdictLedger.Parse(text));                 // 纯解析也不抛异常
    }

    /// <summary>多余字段(给人和将来扩展留的 note 只有一列)只忽略,不影响这条结论成立。</summary>
    [Fact]
    public void Extra_fields_are_ignored_but_the_row_still_counts()
    {
        var one = DmlVerdictLedger.Parse("audio|m|0\tTrue\t0\t" + T0 + "\t说明阶段\textra\tmore");
        Assert.Single(one);
        Assert.Equal("audio|m|0", one[0].Key);
        Assert.True(one[0].Ok);
        Assert.Equal("说明阶段", one[0].Note);
    }

    /// <summary>文件里一半是好的、一半是坏的:好的照常生效,坏的只跳过 —— 缓存坏了绝不能影响处理。</summary>
    [Fact]
    public void Good_rows_still_work_next_to_corrupt_rows()
    {
        string text =
            DmlVerdictLedger.FileHeader + "\n" +
            "音频|模型|0\tFalse\t3\t" + T0 + "\t别管这行\n" +      // 键不认识(大小写/中文都无所谓,就是没这个键)
            Key(Domain, Model, 0) + "\tFalse\t3\t" + T0 + "\t图融合失败\n" +
            "坏行\n" +
            "别的键\tTrue\t0\t不是数字\n";

        var ledger = DmlVerdictLedger.FromText(text, 3);
        Assert.False(ledger.ShouldAttempt(Domain, Model, 0, T0));       // 好行生效
        Assert.True(ledger.ShouldAttempt(Domain, OtherModel, 0, T0));   // 没结论的键照旧试
    }

    /// <summary>写出去再读回来必须是同一份结论(键的拼法与解析必须成对,否则会表现成"写进去读不出来"
    /// ⇒ 每次重试、诊断包里永远"未测")。</summary>
    [Fact]
    public void File_round_trip_keeps_the_verdict()
    {
        var ledger = NewLedger(3);
        ledger.NoteDenial(Domain, Model, 0, T0, "建会话失败\t带分隔符\n带换行");
        ledger.NoteSuccess(Domain, OtherModel, 1, T0 + 5, "成功");

        string text = DmlVerdictLedger.Format(ledger.Snapshot(T0 + 6));
        var again = DmlVerdictLedger.FromText(text, 3);

        Assert.False(again.ShouldAttempt(Domain, Model, 0, T0 + 6));
        Assert.True(again.ShouldAttempt(Domain, OtherModel, 1, T0 + 6));
        Assert.True(again.Verdict(Domain, OtherModel, 1, T0 + 6));
        Assert.Equal(2, DmlVerdictLedger.Parse(text).Count);            // 注释行/空行不算条目
        Assert.StartsWith("#", text);                                   // 首行是给人看的说明
        Assert.DoesNotContain("\r", text);                              // 行尾固定 LF(仓库约定)
    }

    /// <summary>文件里 ok=True 却带着失败次数(手改/旧版本写的)⇒ 归一化成 0,绝不拿它当否定结论。</summary>
    [Fact]
    public void A_success_row_with_legacy_failure_count_is_normalized()
    {
        string text = Key(Domain, Model, 0) + "\tTrue\t9\t" + T0 + "\n";
        var ledger = DmlVerdictLedger.FromText(text, 3);
        Assert.True(ledger.ShouldAttempt(Domain, Model, 0, T0));
        Assert.Equal(0, ledger.Failures(Domain, Model, 0, T0));
        Assert.True(ledger.Verdict(Domain, Model, 0, T0));
        Assert.Contains("\tTrue\t0\t", DmlVerdictLedger.Format(ledger.Snapshot(T0)));
    }

    /// <summary>损坏的落盘文本 + 一个新账本 = 至少不能丢"没结论就试一次"这条契约。</summary>
    [Fact]
    public void A_completely_garbage_file_leads_to_one_attempt()
    {
        var ledger = DmlVerdictLedger.FromText(new string('x', 4096) + "\n\u0000\u0001\n", 3);
        Assert.True(ledger.ShouldAttempt(Domain, Model, 0, T0));
        Assert.Empty(ledger.Snapshot(T0));
    }

    // ───────────────── 参数校验 ─────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Strike_limit_must_be_positive(int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DmlVerdictLedger(limit));
    }

    /// <summary>Snapshot 只给未过期的条目(落盘时顺手清垃圾),且顺序稳定(文件 diff 友好)。</summary>
    [Fact]
    public void Snapshot_drops_expired_rows_and_sorts_by_key()
    {
        var ledger = new DmlVerdictLedger(3, failureTtl: TimeSpan.FromHours(1), successTtl: TimeSpan.FromHours(1));
        ledger.NoteFailure(Domain, OtherModel, 0, T0);
        ledger.NoteFailure(Domain, Model, 0, T0 + 1800);

        var fresh = ledger.Snapshot(T0 + 3600 + 1);
        Assert.Single(fresh);
        Assert.Equal(Key(Domain, Model, 0), fresh[0].Key);
        Assert.True(ledger.ShouldAttempt(Domain, OtherModel, 0, T0 + 3600 + 1));
    }
}
