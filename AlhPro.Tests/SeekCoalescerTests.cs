using System;
using System.Threading.Tasks;
using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【2026-09-23 用户:"左右预览不协调"】这条反馈的**代码级根因**就钉在这个文件里:
/// 旧实现用三个全局字段(`_seekWantMp`/`_seekWantSec`/`_seekInFlight`)给四条播放器共用**一个**槽位,
/// 而遮罩左右对比每次定位必须打**两条**(同一条合成片的上层原片半幅 + 下层处理后半幅)⇒
/// 第二条把第一条寄存的目标覆盖掉,且"寄存"只在位置回调里才补发 —— 暂停态没有回调 ⇒ 目标永久丢失。
/// 日志实证:`片段[停 0/3.003s] · 原片[停 2.834/3.003s] · 漂移 2834ms`(用户把时间线拖到开头,只有一条跳了)。
///
/// 这些用例把"分槽 + 寄存必被补发 + 旧判据不许改"逐条钉住 —— 它们都是**纯逻辑**,不需要播放器、不需要 UI。</summary>
public class SeekCoalescerTests
{
    private readonly object _a = new();
    private readonly object _b = new();

    /// <summary>★ 本轮主角:两条播放器**各自**在飞,互不覆盖(旧实现在这里必然失败)。</summary>
    [Fact]
    public void Two_players_have_independent_slots()
    {
        var c = new SeekCoalescer();
        // A 先落地一次
        Assert.Equal(SeekCoalescer.Decision.ApplyNow, c.Request(_a, 10.0, false, 0, 10.0, immediate: true));
        // B 紧接着请求(间隔 0ms —— 旧实现会因为"A 还在飞"把 B 寄存,而 A 那边的槽位是被共用的)
        Assert.Equal(SeekCoalescer.Decision.ApplyNow, c.Request(_b, 10.0, false, 5, 10.0, immediate: true));
        Assert.True(c.InFlight(_a, 5));
        Assert.True(c.InFlight(_b, 5));
        // 两条各自落地:A 的落地不许把 B 的"在飞"清掉,反之亦然
        Assert.True(c.NotePosition(_a, 10.0, 300).Landed);
        Assert.False(c.NotePosition(_a, 10.0, 301).Landed);
        Assert.True(c.InFlight(_b, 301));
    }

    /// <summary>★ 关键:**这一条正是旧实现的病**。旧实现里"第二条请求"会因为**别的播放器**在飞而被寄存
    /// (全局槽位),新实现里每条各自在飞 ⇒ 第二条照常立刻落地。这里同时验证"寄存与取走"仍然可用。</summary>
    [Fact]
    public void Parked_target_of_each_player_can_be_taken_back()
    {
        var c = new SeekCoalescer();
        Assert.Equal(SeekCoalescer.Decision.ApplyNow, c.Request(_a, 1.0, false, 1000, 1.0, immediate: true));
        // 【旧实现在这一行会返回 Parked(被 A 的在飞挡住)—— 那正是"只有一条跳过去"的来源】
        Assert.Equal(SeekCoalescer.Decision.ApplyNow, c.Request(_b, 7.5, true, 1001, 1.0, immediate: true));
        // 同一条自己再请求一次才会寄存(在飞未落地)
        Assert.Equal(SeekCoalescer.Decision.Parked, c.Request(_b, 8.0, true, 1002, 1.0, immediate: true));
        var w = c.TakeWant(_b);
        Assert.NotNull(w);
        Assert.Equal(_b, w!.Value.Key);
        Assert.Equal(8.0, w.Value.Seconds, 6);
        Assert.True(w.Value.Verify);
        Assert.Null(c.TakeWant(_b));   // 取走了就没有了(不会重复补发)
        Assert.Null(c.TakeWant(_a));   // A 没寄存过任何东西(旧实现里这一条会拿到 B 的目标 ✗)
    }

    /// <summary>定时器那条路:一次 Tick 能把**所有**槽位的寄存目标都取走(旧实现只有唯一一个槽位)。</summary>
    [Fact]
    public void TakeAnyWant_drains_every_slot()
    {
        var c = new SeekCoalescer();
        c.Request(_a, 1.0, false, 1000, 1.0, immediate: true);   // A 在飞
        c.Request(_a, 2.0, false, 1001, 1.0, immediate: true);   // 覆盖成 2.0(最新目标优先)
        c.Request(_b, 3.0, false, 1002, 3.0, immediate: true);   // B 在飞
        c.Request(_b, 4.0, false, 1003, 3.0, immediate: true);   // 覆盖成 4.0

        var ones = new System.Collections.Generic.List<SeekCoalescer.Want>();
        SeekCoalescer.Want? w;
        while ((w = c.TakeAnyWant()) != null) ones.Add(w.Value);
        Assert.Equal(2, ones.Count);
        Assert.Contains(ones, x => ReferenceEquals(x.Key, _a) && Math.Abs(x.Seconds - 2.0) < 1e-9);
        Assert.Contains(ones, x => ReferenceEquals(x.Key, _b) && Math.Abs(x.Seconds - 4.0) < 1e-9);
        Assert.False(c.HasWant);
    }

    /// <summary>新目标落地时,旧寄存目标必须被丢弃 —— 否则它会在下一次落地回调里把画面**拽回**去。</summary>
    [Fact]
    public void Beginning_a_new_seek_discards_the_stale_parked_target()
    {
        var c = new SeekCoalescer();
        c.Request(_a, 1.0, false, 0, 1.0, immediate: true);       // 在飞
        c.Request(_a, 5.0, false, 50, 1.0, immediate: false);     // 在飞 + 间隔太近 ⇒ 寄存 5.0
        Assert.True(c.HasWant);
        Assert.Equal(5.0, c.TakeWant(_a)!.Value.Seconds, 6);
        c.Request(_a, 5.0, false, 50, 1.0, immediate: false);     // 再寄存一次
        Assert.True(c.HasWant);
        // 900ms 后放行 ⇒ 6.0 立刻下发,并**丢弃**那个已经过期的 5.0(否则它稍后会把画面拽回去)
        Assert.Equal(SeekCoalescer.Decision.ApplyNow, c.Request(_a, 6.0, false, 1000, 1.0, immediate: true));
        Assert.False(c.HasWant);
    }

    /// <summary>判据①:拖动合并 —— 最小间隔内的第二次请求不许立刻落地(否则就是 seek 风暴)。</summary>
    [Fact]
    public void Non_immediate_requests_within_min_interval_are_merged()
    {
        var c = new SeekCoalescer();
        Assert.Equal(SeekCoalescer.Decision.ApplyNow, c.Request(_a, 1.0, false, 0, 1.0));
        Assert.True(c.NotePosition(_a, 1.0, 50).Landed);          // 先让它落地(in-flight 放掉)
        Assert.Equal(SeekCoalescer.Decision.Parked, c.Request(_a, 1.1, false, 60, 1.0));
        Assert.Equal(SeekCoalescer.Decision.ApplyNow, c.Request(_a, 1.2, false, SeekCoalescer.MinIntervalMs, 1.0));
    }

    /// <summary>判据②:在飞超时 900ms 后放行下一次(否则一次没落地的定位会把播放器永久锁死)。</summary>
    [Fact]
    public void In_flight_is_released_after_the_timeout()
    {
        var c = new SeekCoalescer();
        c.Request(_a, 1.0, false, 0, 1.0, immediate: true);
        Assert.True(c.InFlight(_a, 100));
        Assert.True(c.InFlight(_a, SeekCoalescer.InFlightTimeoutMs));
        Assert.False(c.InFlight(_a, SeekCoalescer.InFlightTimeoutMs + 1));
        Assert.Equal(SeekCoalescer.Decision.ApplyNow,
            c.Request(_a, 2.0, false, SeekCoalescer.InFlightTimeoutMs + 2, 1.0, immediate: true));
    }

    /// <summary>判据③(两条都要,与旧实现逐字一致):
    ///   落地 = 落进目标 ±350ms **或** 相对"发定位前的位置"动过 &gt;50ms(播放中定位就是后一种)。
    /// 注意第二条的实际含义:它**很宽** —— 只要位置从发定位那一刻起动过就算落地。所以"没落地"的情形
    /// 只有一种:位置既远离目标、又一步没动(暂停态的 seek 被播放器吞掉,正是这个形状)。</summary>
    [Fact]
    public void Landing_is_judged_by_tolerance_or_by_movement()
    {
        var c = new SeekCoalescer();
        c.Request(_a, 10.0, false, 0, 12.0, immediate: true);    // 发定位前在 12.0
        Assert.False(c.NotePosition(_a, 12.0, 100).Landed);      // 差 2 秒 + 一步没动 ⇒ 没落地
        Assert.True(c.NotePosition(_a, 10.3, 200).Landed);       // 差 300ms < 350ms ⇒ 落地

        var d = new SeekCoalescer();
        d.Request(_b, 10.0, false, 0, 2.0, immediate: true);
        Assert.True(d.NotePosition(_b, 2.5, 200).Landed);        // 播放中定位:位置从 2.0 走到 2.5 ⇒ 落地
    }

    /// <summary>耗时埋点只报一次(旧实现靠全局 `_seekApplied`,两条播放器会互相把对方的埋点吃掉)。</summary>
    [Fact]
    public void Landing_elapsed_is_reported_once_per_player()
    {
        var c = new SeekCoalescer();
        c.Request(_a, 3.0, false, 1000, 0.0, immediate: true);
        var r1 = c.NotePosition(_a, 3.0, 1180);
        Assert.True(r1.Landed);
        Assert.True(r1.FirstReport);
        Assert.Equal(180, r1.ElapsedMs);
        Assert.Equal(3.0, r1.Target, 6);

        var c2 = new SeekCoalescer();
        c2.Request(_b, 3.0, false, 1000, 0.0, immediate: true);
        c2.NotePosition(_b, 3.0, 1100);
        Assert.False(c2.NotePosition(_b, 3.0, 1200).FirstReport);   // 已经报过
    }

    /// <summary>落地回调要能顺手告诉调用方"你还有寄存目标,接着发"(这条是"拖动期间两条都跟随"的保证)。</summary>
    [Fact]
    public void Landing_reports_pending_work()
    {
        var c = new SeekCoalescer();
        c.Request(_a, 1.0, false, 0, 1.0, immediate: true);
        c.Request(_b, 2.0, false, 1, 2.0, immediate: true);   // B 在飞
        c.Request(_b, 2.5, false, 2, 2.0, immediate: true);   // B 寄存 2.5
        var r = c.NotePosition(_b, 2.0, 300);
        Assert.True(r.Landed);
        Assert.True(r.HasPending);
        Assert.Equal(2.5, c.TakeWant(_b)!.Value.Seconds, 6);
    }

    /// <summary>Clear:直接赋过 Position 的播放器要从合并器里摘掉,免得旧的"在飞"挡住它下一次定位。</summary>
    [Fact]
    public void Clear_releases_the_player()
    {
        var c = new SeekCoalescer();
        c.Request(_a, 1.0, false, 0, 1.0, immediate: true);
        Assert.True(c.InFlight(_a, 10));
        c.Clear(_a);
        Assert.False(c.InFlight(_a, 10));
        Assert.Equal(SeekCoalescer.Decision.ApplyNow, c.Request(_a, 9.0, false, 11, 1.0, immediate: true));
        c.ClearAll();
        Assert.Equal(0, c.TrackedKeys);
    }

    /// <summary>与旧实现逐字一致的三条常数(改了它们就等于改了拖动/定位的行为,必须显式改测试)。</summary>
    [Fact]
    public void Constants_match_the_previous_hard_coded_values()
    {
        Assert.Equal(110, SeekCoalescer.MinIntervalMs);
        Assert.Equal(900, SeekCoalescer.InFlightTimeoutMs);
        Assert.Equal(0.35, SeekCoalescer.LandToleranceSec, 6);
        Assert.Equal(0.05, SeekCoalescer.LandMovedSec, 6);
    }

    /// <summary>null key 不许抛(调用点里 `EffectPlayer.MediaPlayer` 可能还没创建)。</summary>
    [Fact]
    public void Null_key_is_safe()
    {
        var c = new SeekCoalescer();
        _ = c.Request(null!, 1, false, 0, 0, immediate: true);
        _ = c.NotePosition(null!, 1, 0);
        _ = c.TakeWant(null!);
        _ = c.InFlight(null!, 0);
        c.Clear(null!);
        Assert.Equal(0, c.TrackedKeys);
    }

    /// <summary>多线程同时请求不许炸(位置回调在媒体线程、拖动在 UI 线程)。</summary>
    [Fact]
    public void Concurrent_requests_are_safe()
    {
        var c = new SeekCoalescer();
        var keys = new object[] { _a, _b, new(), new() };
        Parallel.For(0, 2000, i =>
        {
            var k = keys[i % keys.Length];
            c.Request(k, i % 100, false, i * 7, i % 100, immediate: (i & 1) == 0);
            c.NotePosition(k, i % 100, i * 7 + 1);
        });
        Assert.True(c.TrackedKeys is > 0 and <= 4);
    }
}
