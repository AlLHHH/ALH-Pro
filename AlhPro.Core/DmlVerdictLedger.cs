using System.Globalization;
using System.Text;

namespace AlhPro.Core;

/// <summary>
/// 「本机这个(业务域 · 模型 · 设备)上 DirectML 还能不能跑」的**落盘结论账本**(纯逻辑:不碰文件、不读时钟)。
///
/// 【为什么必须有·真机依据】2026-09-23 的 diagnostic.log:21:41:58 / 21:42:48 / 23:07:52 / 23:28:25
/// 四次 <c>音频分离 GPU 推理失败 — DmlFusedNode_0_0</c>(HT-Demucs 图融合失败,失败点在 session.Run
/// 而不是建会话)。每次白等 5~9 秒,退回 CPU 的行为本身是对的 —— 但"这台卡跑不了 Demucs"这个结论
/// 原先只存在进程内连击表里,**进程一重启就忘** ⇒ 每个应用会话都要重新白试一遍。
/// 所以结论必须落盘:让"本机跑不动"成为一条**跨会话有效**的事实。
///
/// 【为什么键细到 (域 · 模型 · 设备)】同一台机器上不同模型/不同设备的可用性本来就不一样 ——
/// ncnn 探测结论缓存踩过一次坑(旧键 <c>引擎|设备</c> 缺"模型"维度,于是"animevideov3 通过"给
/// x4plus 背书了 7 天,见 <see cref="NcnnVerdictKey"/>)。按设备一刀切会把同一张卡上能跑的模型一起关掉。
///
/// 【为什么成功与失败用不同 TTL → 2026-09-24 改成同一组 7 天】原设计失败只留 1 天,理由是"让机器自愈"。
/// 但真机使用模式暴露了它的反面:**一天只跑一次的用户永远攒不到 3 次连击** —— 每次开软件时上一条失败
/// 都刚好过期 ⇒ 这个用户**每次会话都要白试一次 DML**(实测每次白等 3~13 秒),而"白试"正是本类要消灭的东西。
/// 现在失败同样留 7 天(攒得起来),**自愈改由"环境签名"负责**:落盘文本里带一行
/// <c># sig=显卡名@驱动版本…</c>,读回时若与本机当前签名不一致(换了驱动 / 换了卡 / 插拔了 eGPU),
/// **整份结论一律作废**(= 全部当"没测过",重新试一次)。换驱动正是"跑不了"最常见的解药,
/// 这样既不靠"每天忘一次"来碰运气,也不会在换驱动后继续死认旧结论。
/// (与 EngineService 的 ncnn 探测结论缓存不同:那边没有签名机制,所以只能靠短 TTL。)
///
/// 【为什么"没结论就放行"】<see cref="ShouldAttempt"/> 只在"存在未过期的否定结论"时才说 false。
/// 空文件 / 文件损坏 / 键不认识 / 结论过期 / 时钟异常 —— 一律当作"没测过" ⇒ 调用方照现状试一次。
/// 这条是本类的**契约**:绝不能把没测过的机器一刀切判成 CPU(那会把好路径一起关掉)。
///
/// 【踩过的坑 · 键的拼法】<see cref="Key"/> 里各段先归一化并消掉分隔符:否则
/// <c>域="a|b", 模型="c"</c> 与 <c>域="a", 模型="b|c"</c> 会拼出同一个键(两条结论互相覆盖)。
/// 设备号用整数段且查询按**整键相等**匹配,所以设备 1 与 10 不会互相误吃
/// (ncnn 那边就是因为前缀匹配少了收尾的 <c>|</c> 才踩过这个坑)。
/// </summary>
public sealed class DmlVerdictLedger
{
    /// <summary>一条落盘结论。<paramref name="Failures"/> 只在 <paramref name="Ok"/> 为 false 时有意义
    /// (成功即清零);<paramref name="AtUnix"/> = 写下这条结论的 Unix 秒(UTC)。</summary>
    public readonly record struct Entry(string Key, bool Ok, int Failures, long AtUnix, string Note);

    /// <summary>落盘文本的第一行说明。首字符 <c>#</c> = 注释,解析时跳过(与 ncnn-probe.txt 同一约定)。</summary>
    public const string FileHeader =
        "# key\tok\tfailures\tatUnix\tnote  (ALH Pro DirectML 结论账本;域|模型|设备;删掉本文件即强制重新试一次)";

    /// <summary>字段分隔符(TAB)。用纯文本而不是 JSON:键/值都是短标量,JSON 只会引入
    /// 反射序列化/裁剪风险(同 ncnn-probe.txt 的理由),而且人可以直接读诊断包核查。</summary>
    private const char Sep = '\t';

    /// <summary>成功结论的有效期(见类注释)。</summary>
    public static readonly TimeSpan DefaultSuccessTtl = TimeSpan.FromDays(7);

    /// <summary>失败结论的有效期。**与成功同为 7 天**(2026-09-24 改口径,理由见类注释):
    /// 1 天会让"一天只跑一次"的用户永远攒不到连击、每次会话都白试一次;自愈改由**环境签名**负责
    /// (<see cref="ParseSignature"/>:换驱动/换卡 ⇒ 整份作废重测)。</summary>
    public static readonly TimeSpan DefaultFailureTtl = TimeSpan.FromDays(7);

    /// <summary>落盘文本里"环境签名"那一行的前缀(注释行,解析条目时自然跳过)。</summary>
    public const string SigPrefix = "# sig=";

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly TimeSpan _successTtl;
    private readonly TimeSpan _failureTtl;

    /// <param name="strikeLimit">失败累计到几次认定"这个组合在本机跑不了 DML"(须 ≥1)。
    /// 由调用方传入并与进程内连击表的数字同源,不在这里写死第二个 3。</param>
    /// <param name="successTtl">成功结论有效期(null = <see cref="DefaultSuccessTtl"/>)。</param>
    /// <param name="failureTtl">失败结论有效期(null = <see cref="DefaultFailureTtl"/>)。</param>
    public DmlVerdictLedger(int strikeLimit, TimeSpan? successTtl = null, TimeSpan? failureTtl = null)
    {
        if (strikeLimit < 1)
            throw new ArgumentOutOfRangeException(nameof(strikeLimit), strikeLimit, "失败累计上限必须 ≥1");
        StrikeLimit = strikeLimit;
        _successTtl = successTtl ?? DefaultSuccessTtl;
        _failureTtl = failureTtl ?? DefaultFailureTtl;
    }

    /// <summary>失败累计上限(供日志/界面显示"已累计 N/M 次失败")。</summary>
    public int StrikeLimit { get; }

    // ───────────────────────── 键(纯函数) ─────────────────────────

    /// <summary>键 = <c>域|模型|设备</c>。域/模型做小写 + 去空白 + 消掉分隔符归一化(见类注释的坑);
    /// 设备号是整数段 —— 查询按整键相等,设备 1 与 10 天然隔离。</summary>
    public static string Key(string domain, string model, int device)
        => Normalize(domain) + "|" + Normalize(model) + "|" + device.ToString(CultureInfo.InvariantCulture);

    /// <summary>单独一段的归一化:小写、去空白、消掉 TAB/换行/<c>|</c>(防止跨字段拼出同一个键)。</summary>
    private static string Normalize(string? s)
        => (s ?? "").Trim().ToLowerInvariant()
            .Replace('\t', '_').Replace('\r', '_').Replace('\n', '_').Replace('|', '_');

    /// <summary>一行化(不改变键的结构:键里的 <c>|</c> 必须保留,它才是字段分隔符)。</summary>
    private static string OneLine(string? s)
        => (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();

    // ───────────────────────── 落盘文本 ↔ 条目(纯函数,不碰文件、不读时钟) ─────────────────────────

    /// <summary>解析落盘文本。**宽容**:空/空白、注释行、字段数不足、bool/数字解析失败的行一律跳过
    /// (单行损坏绝不牵连其它行);键重复时后写覆盖先写(与"最后一次实测为准"一致)。
    /// 不做 TTL 判定 —— 过期是**查询时**用注入的 now 判的,便于单测。绝不抛异常。</summary>
    public static List<Entry> Parse(string? text)
    {
        var list = new List<Entry>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        foreach (var raw in text!.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var f = line.Split(Sep);
            if (f.Length < 4) continue;
            string key = f[0].Trim();
            if (key.Length == 0) continue;
            if (!bool.TryParse(f[1].Trim(), out bool ok)) continue;
            if (!int.TryParse(f[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int failures)) continue;
            if (failures < 0) continue;
            if (!long.TryParse(f[3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long at)) continue;
            if (at <= 0) continue;
            list.Add(new Entry(key, ok, ok ? 0 : failures, at, f.Length > 4 ? OneLine(f[4]) : ""));
        }
        return list;
    }

    /// <summary>把条目序列化成落盘文本(第一行是注释说明,键升序 ⇒ 文件 diff 友好)。
    /// 行尾固定 LF(仓库约定),不随平台变;成功条目强制 failures=0。</summary>
    public static string Format(IEnumerable<Entry>? entries) => Format(entries, null);

    /// <summary>同 <see cref="Format(IEnumerable{Entry}?)"/>,但额外写入一行**环境签名**(<see cref="SigPrefix"/>)。
    /// 签名为空则不写(老文件没有这一行时,读回按"无法判断环境是否变过"处理 —— 见 <see cref="FromText"/>,不擅自作废)。</summary>
    public static string Format(IEnumerable<Entry>? entries, string? signature)
    {
        var sb = new StringBuilder();
        sb.Append(FileHeader).Append('\n');
        if (!string.IsNullOrWhiteSpace(signature))
            sb.Append(SigPrefix).Append(OneLine(signature)).Append('\n');
        if (entries != null)
        {
            var list = new List<Entry>();
            foreach (var e in entries)
                if (!string.IsNullOrWhiteSpace(e.Key)) list.Add(e);
            list.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            foreach (var e in list)
                sb.Append(OneLine(e.Key)).Append(Sep)
                  .Append(e.Ok ? "True" : "False").Append(Sep)
                  .Append(Math.Max(0, e.Ok ? 0 : e.Failures).ToString(CultureInfo.InvariantCulture)).Append(Sep)
                  .Append(e.AtUnix.ToString(CultureInfo.InvariantCulture)).Append(Sep)
                  .Append(OneLine(e.Note)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>便捷构造:新建账本并合并一份落盘文本(调用方负责文件 I/O,本类不读文件)。
    /// <paramref name="currentSignature"/> 非空时做**环境签名校验**:落盘文本里的签名与它不一致
    /// (换了驱动 / 换了卡 / 插拔 eGPU)⇒ **整份结论作废**,等效于"没测过"(下次照现状试一次)。
    /// 文本里没有签名的老文件不擅自作废(签名未知 ≠ 环境变过)。</summary>
    public static DmlVerdictLedger FromText(string? text, int strikeLimit,
        TimeSpan? successTtl = null, TimeSpan? failureTtl = null, string? currentSignature = null)
    {
        var ledger = new DmlVerdictLedger(strikeLimit, successTtl, failureTtl);
        string? stored = ParseSignature(text);
        if (!string.IsNullOrWhiteSpace(currentSignature) && !string.IsNullOrWhiteSpace(stored)
            && !string.Equals(stored, OneLine(currentSignature), StringComparison.OrdinalIgnoreCase))
            return ledger;   // 环境变了:一条都不合并(见上面注释)
        ledger.Merge(Parse(text));
        return ledger;
    }

    /// <summary>从落盘文本里取出环境签名(第一行 <c># sig=</c> 之后的原文);没有则 null。纯函数,绝不抛。</summary>
    public static string? ParseSignature(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var raw in text!.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith(SigPrefix, StringComparison.Ordinal))
            {
                var sig = line.Substring(SigPrefix.Length).Trim();
                return sig.Length > 0 ? sig : null;
            }
        }
        return null;
    }

    // ───────────────────────── 实例状态(线程安全) ─────────────────────────

    /// <summary>把一批结论合并进账本(落盘结论优先于进程内已有条目 —— 调用方只在首次使用时调一次)。</summary>
    public void Merge(IEnumerable<Entry>? entries)
    {
        if (entries == null) return;
        lock (_gate)
            foreach (var e in entries)
            {
                if (string.IsNullOrWhiteSpace(e.Key)) continue;
                _entries[e.Key] = e.Ok ? e with { Failures = 0 } : e;
            }
    }

    /// <summary>这次该不该尝试 DirectML。
    /// 【契约】只有"存在未过期的否定结论(失败已达上限)"才返回 false;其余一律 true
    /// —— 没结论 / 结论过期 / 文件损坏 / 键不认识 / 曾经成功,都要照现状试一次。
    /// <paramref name="device"/> &lt; 0 = 调用方本来就选了 CPU,不归账本管,原样放行
    /// (与 <see cref="DmlAttemptLedger"/> 同口径)。</summary>
    public bool ShouldAttempt(string domain, string model, int device, long nowUnix)
    {
        if (device < 0) return true;
        lock (_gate) return !DeniedLocked(Key(domain, model, device), nowUnix);
    }

    /// <summary>键的结论:true=已实测可用 / false=已判不可用 / null=没有生效结论(未测或已过期)。</summary>
    public bool? Verdict(string domain, string model, int device, long nowUnix)
    {
        lock (_gate) return TryGetFreshLocked(Key(domain, model, device), nowUnix, out var e) ? e.Ok : (bool?)null;
    }

    /// <summary>该键当前累计的连续失败次数(成功即清零;没有生效结论 = 0)。</summary>
    public int Failures(string domain, string model, int device, long nowUnix)
    {
        lock (_gate)
        {
            if (!TryGetFreshLocked(Key(domain, model, device), nowUnix, out var e)) return 0;
            return e.Ok ? 0 : e.Failures;
        }
    }

    /// <summary>记一次 DirectML 失败(累计 +1)。返回 true = 累计已达上限,否定结论从此生效
    /// (调用方应只提示一次)。设备 &lt; 0 不记账。</summary>
    public bool NoteFailure(string domain, string model, int device, long nowUnix, string? note = null)
    {
        if (device < 0) return false;
        lock (_gate)
        {
            string key = Key(domain, model, device);
            // 之前的结论若已过期或本来就是成功的,这次从 1 重新数(成功过就不该和旧失败累加)。
            int n = 1;
            if (TryGetFreshLocked(key, nowUnix, out var prev) && !prev.Ok) n = prev.Failures + 1;
            _entries[key] = new Entry(key, false, n, nowUnix, OneLine(note));
            return n >= StrikeLimit;
        }
    }

    /// <summary>直接把否定结论判死(失败数打满上限),不等累计 —— 用于**结构性**失败
    /// (如 DirectML 建会话就抛异常:provider 都挂不上,再试必然再失败一次)。
    /// 与进程内的 <c>NoteDmlSessionCreationFailure</c> 同口径。</summary>
    public void NoteDenial(string domain, string model, int device, long nowUnix, string? note = null)
    {
        if (device < 0) return;
        lock (_gate)
        {
            string key = Key(domain, model, device);
            _entries[key] = new Entry(key, false, StrikeLimit, nowUnix, OneLine(note));
        }
    }

    /// <summary>DirectML 推理成功 → 清除该键的失败结论(连击归零、否定结论立即失效),
    /// 并留一条**成功结论**(用较长的 <see cref="DefaultSuccessTtl"/>)供日志/自检如实说明
    /// "这台卡实测能跑",同时避免下一次启动又把它当"未测"。
    /// 返回 true = 本次确实清掉了一条生效中的否定结论(专供"只提示一次"的日志)。
    /// 设备 &lt; 0 不记账。</summary>
    public bool NoteSuccess(string domain, string model, int device, long nowUnix, string? note = null)
    {
        if (device < 0) return false;
        lock (_gate)
        {
            string key = Key(domain, model, device);
            bool clearedDenial = DeniedLocked(key, nowUnix);
            _entries[key] = new Entry(key, true, 0, nowUnix, OneLine(note));
            return clearedDenial;
        }
    }

    /// <summary>当前**未过期**的全部结论(供落盘)。过期条目顺手丢弃 = 过期自动重测;
    /// **未来时间戳**条目同样会被摘除(取不到 ⇒ 不进快照) —— 见 <see cref="TryGetFreshLocked"/> 的说明。
    /// 【2026-09-24 复审修正】原注释写的是"拒绝在读到未来时间戳时把它当过期",那是旧行为(把年龄压成 0);
    /// 现在未来时间戳一律按"未测"摘除,所以进不了这里的快照 —— 注释已跟着行为改。</summary>
    public List<Entry> Snapshot(long nowUnix)
    {
        lock (_gate)
        {
            var keys = new List<string>(_entries.Keys);      // 先取键快照:判定内部会摘除过期项
            var live = new List<Entry>();
            foreach (var k in keys)
                if (TryGetFreshLocked(k, nowUnix, out var e)) live.Add(e);
            live.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            return live;
        }
    }

    private bool DeniedLocked(string key, long nowUnix)
        => TryGetFreshLocked(key, nowUnix, out var e) && !e.Ok && e.Failures >= StrikeLimit;

    /// <summary>取未过期的条目(过期、或**时间戳落在未来**,都就地摘除)。调用方必须已持有 <c>_gate</c>。
    /// 【为什么未来时间戳也按"未测"处理 · 2026-09-24 复审修正】与 EngineService 的 ncnn 结论同口径
    /// (那里要求 <c>now - At &gt;= 0</c>,否则整条丢弃)。AtUnix 落在未来只可能来自"本机时钟被改过 /
    /// 文件被手改 / 从别的机器拷来的结论文件" —— 这种条目既不该生效,也不该留着。
    /// 原先把它当"刚写下"(年龄记 0)会把它钉成**长期**结论:一条未来 1 年的否定结论会让这台机器
    /// 整整一年不再试 DML,而账本自己毫无察觉(年龄每轮都被压成 0,只有 TTL 到期才可能清掉)。
    /// 现在一律摘除 ⇒ fail-open:下次照现状试一次(代价是几秒重试);成功结论同样丢弃
    /// (代价仅是下一次成功时多写一次文件)。</summary>
    private bool TryGetFreshLocked(string key, long nowUnix, out Entry entry)
    {
        entry = default;
        if (!_entries.TryGetValue(key, out var e)) return false;
        long age = nowUnix - e.AtUnix;
        if (age < 0) { _entries.Remove(key); return false; }     // 未来时间戳:当"未测",允许重新试一次
        long ttl = (long)(e.Ok ? _successTtl : _failureTtl).TotalSeconds;
        if (age > ttl) { _entries.Remove(key); return false; }   // 过期同理;边界:年龄正好 == TTL 仍算有效
        entry = e;
        return true;
    }
}
