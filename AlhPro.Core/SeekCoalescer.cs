namespace AlhPro.Core;

/// <summary>
/// 「合并式定位」的纯逻辑核 —— 2026-09-23 从 <c>VideoView.xaml.cs</c> 抽出来,**按播放器分槽**。
///
/// ================= 为什么必须抽出来、又为什么必须分槽 =================
/// 症状(用户 2026-09-22 反馈「左右预览不协调」)在日志里留下的实证:
///   `片段[停 0/3.003s] · 原片[停 2.834/3.003s] · 漂移 2834ms`
///   —— 用户把时间线拖到开头,**只有一条播放器跳过去了**,另一条还在片尾附近,
///      于是"中间那条分割线两边根本不是同一帧"。
///
/// 根因(读代码即可证实):旧实现用**三个全局字段**
/// (`_seekWantMp` / `_seekWantSec` / `_seekInFlight`)给四条播放器共用**同一个槽位**:
///   ① 左右对比(遮罩模式)每次定位**必须打两条**(上层原片半幅 + 下层处理后半幅,装的是同一条合成片),
///      而第二条 `RequestSeek` 会把第一条寄存的目标**直接覆盖**掉 ⇒ 第一条永远拿不回来;
///   ② 被覆盖/寄存的目标**只在位置回调里**才会补发(`NoteSeekMaybeLanded`),而
///      **暂停态不产生位置回调**(实测:`位置回调 0 次/5s` 能持续几分钟)⇒ 目标永久丢失;
///   ③ 更隐蔽的一条:`ApplySeekNow` 的"寄存"分支**忘了起那个小定时器**(只有 `RequestSeek` 起),
///      于是"没有回调"就等于"目标被丢掉",连超时兜底都没有。
///
/// 现在:每条播放器(key 用引用本身)**各自一个槽位**,各自"在飞/寄存",谁也不覆盖谁;
/// 而且**任何一次寄存都允许调用方起定时器**(本类提供 <see cref="HasWant"/> / <see cref="TakeAnyWant"/>)。
///
/// ================= 判据(与旧实现逐字一致,不许改) =================
///   · 最小间隔 <see cref="MinIntervalMs"/>(110ms):拖动期间合并掉中间目标,不制造 seek 风暴;
///   · 在飞超时 <see cref="InFlightTimeoutMs"/>(900ms):落地判不出来就放行下一次,免得卡死;
///   · 落地判定:位置落进目标 ±<see cref="LandToleranceSec"/>(0.35s,播放器会吸附到帧/关键帧),
///     或者相对"发定位前的位置"动过 &gt; <see cref="LandMovedSec"/>(0.05s,播放中定位就是这种情形)。
/// 【线程】调用点分布在 UI 线程与媒体线程 ⇒ 内部加锁;所有方法都不碰 UI,纯字段运算(可单测)。
/// </summary>
public sealed class SeekCoalescer
{
    /// <summary>两次真正下发之间的最小间隔(拖动合并用)。旧实现写死 110ms。</summary>
    public const long MinIntervalMs = 110;

    /// <summary>一次定位在飞的最长时间;超过就当作已失败放行下一次(旧实现写死 900ms)。</summary>
    public const long InFlightTimeoutMs = 900;

    /// <summary>落地容差(秒):播放器定位后会吸附到帧/关键帧,差几十毫秒是常态。旧实现写死 0.35。</summary>
    public const double LandToleranceSec = 0.35;

    /// <summary>"位置相对发定位前动过"也当作落地(播放中定位)。旧实现写死 0.05。</summary>
    public const double LandMovedSec = 0.05;

    /// <summary>一次请求的结论。</summary>
    public enum Decision
    {
        /// <summary>现在就下发 Position。</summary>
        ApplyNow,
        /// <summary>已有定位在飞 / 间隔太近 ⇒ 目标已寄存,等落地回调或定时器再补。</summary>
        Parked,
    }

    /// <summary>寄存下来、等待补发的目标。<see cref="Key"/> 就是调用方传进来的那条播放器。</summary>
    public readonly record struct Want(object Key, double Seconds, bool Verify);

    /// <summary><see cref="NotePosition"/> 的结论。</summary>
    /// <param name="Landed">上一次定位是否判定为已落地。</param>
    /// <param name="FirstReport">这是本次定位第一次报"落地"(耗时埋点只该记一次)。</param>
    /// <param name="Target">那次定位的目标秒数(埋点用)。</param>
    /// <param name="ElapsedMs">从发出到判定的毫秒数(埋点用)。</param>
    /// <param name="HasPending">这条播放器还有寄存目标 ⇒ 调用方应立刻补发。</param>
    public readonly record struct LandResult(bool Landed, bool FirstReport, double Target, long ElapsedMs, bool HasPending);

    private sealed class Slot
    {
        public bool InFlight;
        public long InFlightAt;
        public double ReqTarget;
        public double BeforePos = -1;
        public long ReqAt;
        public bool AppliedLogged;
        public bool HasWant;
        public double WantSec;
        public bool WantVerify;
        public long LastAppliedAt = long.MinValue / 4;
    }

    private readonly Dictionary<object, Slot> _slots = new();
    private readonly object _gate = new();
    private readonly long _minIntervalMs;
    private readonly long _inFlightTimeoutMs;

    public SeekCoalescer(long minIntervalMs = MinIntervalMs, long inFlightTimeoutMs = InFlightTimeoutMs)
    {
        _minIntervalMs = minIntervalMs < 0 ? 0 : minIntervalMs;
        _inFlightTimeoutMs = inFlightTimeoutMs <= 0 ? InFlightTimeoutMs : inFlightTimeoutMs;
    }

    /// <summary>当前被跟踪的播放器条数(测试与诊断用)。</summary>
    public int TrackedKeys
    {
        get { lock (_gate) return _slots.Count; }
    }

    /// <summary>是否还有任何一条播放器存着待落地目标(调用方据此决定要不要起定时器)。</summary>
    public bool HasWant
    {
        get { lock (_gate) return _slots.Values.Any(s => s.HasWant); }
    }

    /// <summary>请求一次定位。
    /// <paramref name="immediate"/>=true 表示"能落地就立刻落地,别管最小间隔"(单击/松手/对齐这类);
    /// =false 表示"按 110ms 合并"(拖动中的高频擦洗)。两种情况都可能在飞 ⇒ 那就寄存。</summary>
    public Decision Request(object key, double seconds, bool verify, long nowMs, double beforePos, bool immediate = false)
    {
        if (key == null) return Decision.Parked;
        lock (_gate)
        {
            var s = SlotOf(key);
            if (InFlightLocked(s, nowMs) || (!immediate && nowMs - s.LastAppliedAt < _minIntervalMs))
            {
                ParkLocked(s, seconds, verify);
                return Decision.Parked;
            }
            BeginLocked(s, seconds, verify, nowMs, beforePos);
            return Decision.ApplyNow;
        }
    }

    /// <summary>某条播放器的位置回调:判断它上一次定位是否已落地。</summary>
    public LandResult NotePosition(object key, double pos, long nowMs)
    {
        if (key == null) return default;
        lock (_gate)
        {
            var s = SlotOf(key);
            if (!s.InFlight) return new LandResult(false, false, s.ReqTarget, 0, s.HasWant);
            bool landed = Math.Abs(pos - s.ReqTarget) < LandToleranceSec
                          || (s.BeforePos >= 0 && Math.Abs(pos - s.BeforePos) > LandMovedSec);
            if (!landed) return new LandResult(false, false, s.ReqTarget, 0, s.HasWant);
            s.InFlight = false;
            bool first = s.ReqAt > 0 && !s.AppliedLogged;
            if (first) s.AppliedLogged = true;
            long elapsed = s.ReqAt > 0 ? nowMs - s.ReqAt : 0;
            return new LandResult(true, first, s.ReqTarget, elapsed, s.HasWant);
        }
    }

    /// <summary>是否有定位在飞(含 900ms 超时释放)。</summary>
    public bool InFlight(object key, long nowMs)
    {
        if (key == null) return false;
        lock (_gate) return InFlightLocked(SlotOf(key), nowMs);
    }

    /// <summary>取走某条播放器寄存的目标(没有就返回 null)。</summary>
    public Want? TakeWant(object key)
    {
        if (key == null) return null;
        lock (_gate)
        {
            if (!_slots.TryGetValue(key, out var s) || !s.HasWant) return null;
            var w = TakeLocked(key, s);
            return w;
        }
    }

    /// <summary>取走**任意一条**寄存的目标(小定时器用:一次 Tick 把所有槽位都补发掉)。
    /// 【为什么是"任意"而不是"当前那条"】旧实现在这里就是坏的:它只认那**唯一一个**全局槽位,
    /// 而遮罩模式有两条 ⇒ 补发的永远不是缺的那条。</summary>
    public Want? TakeAnyWant()
    {
        lock (_gate)
        {
            foreach (var kv in _slots)
            {
                if (!kv.Value.HasWant) continue;
                return TakeLocked(kv.Key, kv.Value);
            }
            return null;
        }
    }

    /// <summary>丢弃某条播放器的全部状态(例如它被重新装载/直接赋 Position,不由本合并器管了)。</summary>
    public void Clear(object key)
    {
        if (key == null) return;
        lock (_gate) _slots.Remove(key);
    }

    /// <summary>清空所有槽位(离开预览页时调用,免得留下上一轮的"在飞"挡住新的一轮)。</summary>
    public void ClearAll()
    {
        lock (_gate) _slots.Clear();
    }

    private Slot SlotOf(object key)
    {
        if (!_slots.TryGetValue(key, out var s)) { s = new Slot(); _slots[key] = s; }
        return s;
    }

    private bool InFlightLocked(Slot s, long nowMs)
    {
        if (!s.InFlight) return false;
        if (nowMs - s.InFlightAt > _inFlightTimeoutMs) { s.InFlight = false; return false; }
        return true;
    }

    private void ParkLocked(Slot s, double seconds, bool verify)
    {
        s.HasWant = true;
        s.WantSec = seconds;
        s.WantVerify = verify;
    }

    private void BeginLocked(Slot s, double seconds, bool verify, long nowMs, double beforePos)
    {
        s.InFlight = true;
        s.InFlightAt = nowMs;
        s.LastAppliedAt = nowMs;
        s.ReqAt = nowMs;
        s.ReqTarget = Math.Max(0, seconds);
        s.BeforePos = beforePos;
        s.AppliedLogged = false;
        // 【必须清掉寄存的目标】正在落地的这次已经取代了旧的寄存目标。
        // (旧实现没清 ⇒ 旧目标会在下一次"落地回调"里被补发一次 = 把画面拽回用户早就拖过的位置 ✗)
        s.HasWant = false;
    }

    private static Want TakeLocked(object key, Slot s)
    {
        var w = new Want(key, s.WantSec, s.WantVerify);
        s.HasWant = false;
        s.WantSec = -1;
        s.WantVerify = false;
        return w;
    }
}
