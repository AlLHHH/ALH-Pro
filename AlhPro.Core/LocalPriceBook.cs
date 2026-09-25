using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlhPro.Core;

/// <summary>**本机实测**的超分单价(一次标定的结果)。
///
/// 【为什么需要它 · 用户 2026-09-25 的原话】「那别人使用时,这个检查决定先超分还是先补帧也是用我的设备???」
/// —— **是的,旧实现就是这样**:<see cref="PipelineOrderPlan.UpscaleRates"/> 全部来自开发机
/// (RTX 4060 Laptop)一台机器,别人的阶段顺序由开发机的秒/帧决定。本记录是"用户自己这台机器"的实测。
///
/// 【字段口径】
///   · <see cref="SecondsPerFrame"/> = **采样分辨率下的秒/帧**,由两点法算出并**已扣掉每进程固定地板**
///     (启动 + 模型加载 + 首次着色器/管线创建;见 <see cref="CalibrationSample"/>);
///   · <see cref="SamplePixels"/> = 采样时的**源面积**(px),用于折算 1080p;
///   · <see cref="SampleFrames"/> = 两点法里"多点"那一次的帧数 N(<see cref="LocalPriceBook.MinSampleFrames"/> 起);
///   · <see cref="FloorSeconds"/> / <see cref="SampleSeconds"/> = 两点法两次的原始耗时(可审计);
///   · <see cref="MachineKey"/> = 机器指纹(GPU 名称 + 引擎可执行文件标识);
///     **指纹不一致的记录一律不参与判定**(换显卡/换引擎重编版 ⇒ 自动失效并重标);
///   · <see cref="Backend"/> = **这条单价是在哪条超分后端上测出来的**
///     (<see cref="UpscaleBackendPlan.NcnnVulkan"/> / <see cref="UpscaleBackendPlan.OnnxDml"/> /
///      <see cref="UpscaleBackendPlan.OnnxCpu"/>)。
///     【为什么必须进键(2026-09-25 修订 · F1)】ncnn-Vulkan 与 ONNX 是两套完全不同的运行时,秒/帧差一个数量级
///     (ncnn 0.24~0.6 s/帧 vs ONNX 落 CPU 8 s/帧)。拿"在 ncnn 上测的"单价去判定"本次其实走 ONNX"的运行,
///     就会得出偏小的 u ⇒ 判定偏向「补帧→超分」—— 这正是 2026-09-25 那次 39 分钟误判的同一类错。
///     ⇒ **后端不进键的记录一律拒收**(见 <see cref="LocalPriceBook.TryBuild"/>);查找也必须带后端匹配。
///
/// 【纯逻辑】本文件不做任何文件 IO(读写由 UI 侧的 `CalibMemory` 负责),可单测。</summary>
public readonly record struct LocalPrice(
    string ModelKey,
    int EngineScale,
    double SecondsPerFrame,
    long SamplePixels,
    int SampleFrames,
    double FloorSeconds,
    double SampleSeconds,
    string MeasuredAtUtc,
    string MachineKey,
    string Backend,
    string Note)
{
    /// <summary>折算到 1080p 源的秒/帧(判据用的就是它)。
    /// 依据:超分成本**近似正比于源面积**(本仓库实测支持,见 <see cref="PipelineOrderPlan"/> 类注释里
    /// 的"面积线性验证":扣掉地板后比值 3.98 对面积比 4.00,偏差 −0.5%~−7%)。</summary>
    public double SecondsPerFrame1080p => SamplePixels > 0
        ? SecondsPerFrame * (PipelineOrderPlan.ReferencePixels1080p / SamplePixels)
        : SecondsPerFrame;

    /// <summary>一行可审计出处(判据日志里原样引用)。
    /// 【必须写出后端】否则两次不同后端的标定在日志里长得一样,事后没法核"这个 u 是哪条路测的"。</summary>
    public string Provenance =>
        $"本机实测 {MeasuredAtUtc} 两点法(1 帧 {FloorSeconds:0.###}s + {SampleFrames} 帧 {SampleSeconds:0.###}s,"
        + $"源 {PixelsToText(SamplePixels)},后端 {UpscaleBackendPlan.Label(Backend)})→ {SecondsPerFrame:0.####} s/帧@{PixelsToText(SamplePixels)},"
        + $"折算 1080p {SecondsPerFrame1080p:0.####} s/帧";

    /// <summary>把像素数写成 `1920×1080` 这样的人话(认得出常见档位时);否则写"xx 万像素"。
    /// 公开给 UI 层复用(日志/进度文案里写"采样源 1920×1080"比写 2073600 清楚)。</summary>
    public static string PixelsToText(long pixels)
    {
        if (pixels <= 0) return "未知面积";
        // 只用整数算术,避免浮点误差把 2073600 印成 2073599
        long w = 0, h = 0;
        foreach (var (a, b) in new[] { (1920L, 1080L), (3840L, 2160L), (7680L, 4320L), (1280L, 720L), (960L, 540L), (640L, 360L) })
            if (pixels == a * b) { w = a; h = b; break; }
        return w > 0 ? $"{w}×{h}" : $"{pixels / 10000.0:0.##} 万像素";
    }
}

/// <summary>本机实测单价的**判定用书**(纯逻辑:查找 / 折算 / 拒绝条件 / JSON 编解码;不做文件 IO)。
///
/// 【与内置表的关系】`PipelineOrderPlan.UpscaleRates` 是**他机实测**(开发机),只作资料与回归基线;
/// 生产判定只看这里 —— 用户这台机器上真实测出来的数字。</summary>
public static class LocalPriceBook
{
    /// <summary>低于这个帧数的样本差值会被噪声吃掉(本仓库实测:1~3 帧样本给出的单价都不可信)。</summary>
    public const int MinSampleFrames = 4;

    /// <summary>单价下限(秒/帧):小到 0.5 毫秒以下必然是把某次瞬时抖动算进去了,拒收。</summary>
    public const double MinSecondsPerFrame = 0.0005;

    /// <summary>单价上限(秒/帧):超过 2 分钟/帧的样本不可能是"正常超分",拒收(避免一条脏数据把顺序永久带偏)。</summary>
    public const double MaxSecondsPerFrame = 120.0;

    /// <summary>落盘 schema 版本(`engine-prices.json` 的 `schema` 字段)。</summary>
    public const int SchemaVersion = 1;

    /// <summary>判据用的键(不含后端):`模型键@引擎倍率`(如 `realcugan-se@2x`)。
    /// **只用于显示/资料**:判定用的键必须带后端(见下面的三参重载)。</summary>
    public static string Key(string modelKey, int engineScale) => $"{modelKey}@{engineScale}x";

    /// <summary>**判定用的键(含后端)**:`模型键@引擎倍率@后端`(如 `realcugan-se@2x@ncnn-vulkan`)。
    /// 写入去重、查找、日志出处三处都用它 —— 后端不进键 = 允许"在 ncnn 上测的单价判定 ONNX 运行",
    /// 那是 2026-09-25 误判的同一类错,必须从键上就杜绝。</summary>
    public static string Key(string modelKey, int engineScale, string? backend)
        => $"{modelKey}@{engineScale}x@{NormalizeBackend(backend)}";

    /// <summary>后端描述归一(未知/空白 ⇒ <see cref="UpscaleBackendPlan.Unknown"/>)。</summary>
    public static string NormalizeBackend(string? backend) => UpscaleBackendPlan.NormalizeBackend(backend);

    /// <summary>一次查找的结果。<paramref name="SameMachine"/> = 命中的那条**就是本机**测的
    /// (只有这种情况才允许参与判定)。
    /// <paramref name="OtherBackend"/> = **同一台机器、同一个模型倍率,但在另一条后端上**测过的标定
    /// (归一后的后端名;<c>null</c> = 没有)。它存在的意义只是"如实说出来":判定时若因为后端不匹配而弃用,
    /// 理由里要像"另一台机器"那样**明写**「另一后端的标定(X)已忽略」,别让人以为"本机压根没标过"。</summary>
    public readonly record struct Lookup(LocalPrice? Price, bool SameMachine, string? OtherMachineKey, string? OtherBackend)
    {
        /// <summary>能否作为本机判据:必须命中**且**机器指纹一致。</summary>
        public bool Usable => Price is not null && SameMachine;
    }

    /// <summary>按 (模型, 引擎倍率[, 后端]) 查;**同时**判定机器指纹是否一致(这是本方案的核心,不能只看模型)。
    /// <paramref name="machineKey"/> 传 null/空白 = 不做机器过滤(纯资料查询,<see cref="Lookup.SameMachine"/>=true)。
    /// <paramref name="backend"/> 传 <c>null</c> = **不做后端过滤**(只用于"本机有没有这一格"的资料式提问);
    /// **判定必须传**(生产由 <c>VideoService</c> 传本次定稿的后端)⇒ 在别的后端上测的单价绝不会被采用。
    /// 【2026-09-25 修订 · F1-I2/I4】传了**非 null** 但归一成 <see cref="UpscaleBackendPlan.Unknown"/> 的值
    /// (空串、空白、以及认不出的名字如 <c>"vulkan-2027"</c>)⇒ **一格都不命中**,绝不当成"不过滤" ——
    /// 那正是"拿 ncnn 的偏小单价去判定 ONNX 运行"的入口。注意 <see cref="UpscaleBackendPlan.Unknown"/>
    /// **本身就是空串**,所以判据必须是"是否显式传了非 null",不能只看归一结果的长度。
    /// 命中多条时取 <see cref="LocalPrice.MeasuredAtUtc"/> 最大的那条(最新标定优先)。
    /// 不做任何 IO、不抛异常。</summary>
    public static Lookup Resolve(IEnumerable<LocalPrice>? book, string? model, int engineScale, string? machineKey = null,
        string? backend = null)
    {
        if (book is null) return new Lookup(null, false, null, null);
        string? wantKey = PipelineOrderPlan.NormalizeModel(model);
        if (wantKey is null) return new Lookup(null, false, null, null);
        // 【2026-09-25 修订 · F1-I2/I4】后端过滤的三种情形必须严格分开(原实现把"给了名字但认不出"混进了
        // "不过滤",于是 `Resolve(..., backend: Unknown)` 会命中 ncnn 那一格 —— 那正是把 ncnn 的偏小单价
        // 拿去判定 ONNX 运行的入口):
        //   · backend == null          ⇒ 资料式提问,不过滤;
        //   · 认得出的后端              ⇒ 只认同后端的记录;
        //   · 给了名字但归一成 Unknown  ⇒ **一格都不命中**。
        string wantBackend = NormalizeBackend(backend);
        if (backend is not null && wantBackend.Length == 0) return new Lookup(null, false, null, null);
        bool filterBackend = backend is not null;

        LocalPrice? best = null, bestOtherMachine = null, bestOtherBackend = null;
        foreach (var p in book)
        {
            if (p.ModelKey != wantKey || p.EngineScale != engineScale) continue;
            bool sameMachine = Matches(p.MachineKey, machineKey);
            bool sameBackend = !filterBackend || NormalizeBackend(p.Backend) == wantBackend;
            if (sameMachine && sameBackend)
            {
                if (best is null || Newer(p, best.Value)) best = p;
                continue;
            }
            // 【R2】同一台机器、只是后端不同 ⇒ 单独记下来(理由里要明写"另一后端的标定已忽略")
            if (sameMachine)
            {
                if (bestOtherBackend is null || Newer(p, bestOtherBackend.Value)) bestOtherBackend = p;
                continue;
            }
            if (bestOtherMachine is null || Newer(p, bestOtherMachine.Value)) bestOtherMachine = p;
        }
        if (best is not null) return new Lookup(best, true, bestOtherMachine?.MachineKey, null);
        string? otherMachineKey = bestOtherMachine?.MachineKey;
        string? otherBackend = bestOtherBackend is { } ob ? NormalizeBackend(ob.Backend) : null;
        if (otherBackend is not null && otherBackend.Length == 0) otherBackend = null;
        return new Lookup(null, false, otherMachineKey,
            otherBackend is null || !filterBackend ? null : otherBackend);
    }

    /// <summary>按 (模型, 引擎倍率) 查一条(**不判机器、不判后端**;资料查询/单测用)。要参与判定请用 <see cref="Resolve"/>。</summary>
    public static LocalPrice? Find(IEnumerable<LocalPrice>? book, string? model, int engineScale)
        => Resolve(book, model, engineScale, machineKey: null, backend: null).Price;

    /// <summary>折算到 1080p 的秒/帧;查不到返回 null,出处写进 <paramref name="provenance"/>。</summary>
    public static double? PerFrame1080p(IEnumerable<LocalPrice>? book, string? model, int engineScale, out string provenance,
        string? machineKey = null, string? backend = null)
    {
        var look = Resolve(book, model, engineScale, machineKey, backend);
        if (look.Usable && look.Price is { } p) { provenance = p.Provenance; return p.SecondsPerFrame1080p; }
        if (look.Price is null && look.OtherMachineKey is null && look.OtherBackend is not null)
        {
            provenance = $"另一后端的标定({UpscaleBackendPlan.Label(look.OtherBackend)})已忽略";
            return null;
        }
        provenance = look.Price is null && look.OtherMachineKey is null
            ? "本机没有这一格(模型×引擎倍率)的实测单价"
            : $"另一台机器({Digest(look.OtherMachineKey)})的标定,已忽略";
        return null;
    }

    /// <summary>机器指纹摘要(日志里用,不泄漏整串)。</summary>
    public static string Digest(string? machineKey)
    {
        if (string.IsNullOrWhiteSpace(machineKey)) return "(空指纹)";
        string k = machineKey.Trim();
        return k.Length <= 18 ? k : k[..18] + "…";
    }

    private static bool Matches(string stored, string? current)
        => string.IsNullOrWhiteSpace(current) || string.Equals(stored?.Trim(), current!.Trim(), StringComparison.Ordinal);

    private static bool Newer(LocalPrice a, LocalPrice b)
        => string.CompareOrdinal(a.MeasuredAtUtc ?? "", b.MeasuredAtUtc ?? "") > 0;

    /// <summary>两点法的结果 → 一条可落盘的 <see cref="LocalPrice"/>。
    /// **拒收条件全部返回中文原因**(不抛异常),调用方照写日志即可:
    /// ① 模型键认不出 / 引擎倍率 &lt;1;② 采样帧数 &lt; <see cref="MinSampleFrames"/>;
    /// ③ 两次耗时或面积非法(非有限 / ≤0);④ 机器指纹空白;
    /// ⑤ **后端未确认**(空白/认不出)—— 见 <see cref="UpscaleBackendPlan.NormalizeBackend"/>;
    /// ⑥ **两点法退化** `sampleSeconds &lt;= floorSeconds`(等于没测出正斜率 → 纯噪声,必须拒收,
    ///    **不许**回退成 `sampleSeconds / N` —— 那正是把地板当单价的旧错);
    /// ⑦ **相对噪声门槛(2026-09-25 修订 · F3)**:差值必须 ≥
    ///    <see cref="CalibrationSample.MinDeltaRatio"/> × 1 帧那次的耗时,否则与两次进程启动的抖动同量级;
    /// ⑧ 算出单价超出 [<see cref="MinSecondsPerFrame"/>, <see cref="MaxSecondsPerFrame"/>]。</summary>
    public static bool TryBuild(string? model, int engineScale, int sampleFrames, double floorSeconds, double sampleSeconds,
        long samplePixels, string machineKey, string backend, string measuredAtUtc, string note,
        out LocalPrice price, out string reject)
    {
        price = default;
        string? key = PipelineOrderPlan.NormalizeModel(model);
        if (key is null) { reject = $"认不出这个模型名:{model ?? "(空)"} —— 无法与单价表的键对应,拒收"; return false; }
        if (engineScale < 1) { reject = $"引擎倍率非法:{engineScale}(必须 ≥1),拒收"; return false; }
        if (sampleFrames < MinSampleFrames) { reject = $"采样帧数太少:{sampleFrames} < {MinSampleFrames},差值会被噪声吃掉,拒收"; return false; }
        if (!double.IsFinite(floorSeconds) || floorSeconds <= 0) { reject = $"1 帧那次耗时非法:{floorSeconds},拒收"; return false; }
        if (!double.IsFinite(sampleSeconds) || sampleSeconds <= 0) { reject = $"{sampleFrames} 帧那次耗时非法:{sampleSeconds},拒收"; return false; }
        if (samplePixels <= 0) { reject = $"采样源面积非法:{samplePixels},拒收"; return false; }
        if (string.IsNullOrWhiteSpace(machineKey)) { reject = "机器指纹为空 —— 无法判断这条标定属于哪台机器,拒收"; return false; }
        string bk = NormalizeBackend(backend);
        if (bk.Length == 0)
        {
            reject = $"超分后端未确认({(string.IsNullOrWhiteSpace(backend) ? "(空)" : backend)})—— "
                + "后端没定稿就落盘,等于允许「在 ncnn 上测的单价」去判定「本次其实走 ONNX」的运行(2026-09-25 误判的同一类),拒收";
            return false;
        }
        if (sampleSeconds <= floorSeconds)
        {
            reject = $"两点法退化:{sampleFrames} 帧那次({sampleSeconds:0.###}s)不比 1 帧那次({floorSeconds:0.###}s)慢 —— "
                + "说明这段差值被噪声/降频吃掉了,不是单价。拒收(**不回退**成 总量÷帧数:那会把每进程地板当成单帧成本)";
            return false;
        }
        double delta = sampleSeconds - floorSeconds;
        double needDelta = CalibrationSample.MinDeltaRatio * floorSeconds;
        if (delta < needDelta)
        {
            // 【F3 · 相对噪声门槛】绝对阈值挡不住"差值只有抖动大小"的样本:两者都是 0.5s 量级、
            // 只差 0.05s 时,算出来的是一个 4 位小数的"本机实测",其实全是噪声(假精度)。
            reject = $"两点法差值太小:差值 tN−t1={delta:0.###}s 不足 1 帧那次耗时({floorSeconds:0.###}s)的 "
                + $"{CalibrationSample.MinDeltaRatio * 100:0}%(需 ≥{needDelta:0.###}s)—— "
                + "这个量级与两次进程启动的地板抖动/GPU 热降频同量级,算出来的单价是噪声(会给出看着很准的假精度),拒收";
            return false;
        }
        double perFrame = CalibrationSample.TwoPointPerFrame(sampleFrames, floorSeconds, sampleSeconds);
        if (!double.IsFinite(perFrame) || perFrame <= MinSecondsPerFrame || perFrame > MaxSecondsPerFrame)
        {
            reject = $"算出单价 {perFrame:0.#####} s/帧 超出合理区间 [{MinSecondsPerFrame}, {MaxSecondsPerFrame}],拒收";
            return false;
        }
        price = new LocalPrice(key, engineScale, perFrame, samplePixels, sampleFrames, floorSeconds, sampleSeconds,
            measuredAtUtc ?? "", machineKey.Trim(), bk, note ?? "");
        reject = "";
        return true;
    }

    /// <summary>合并一条标定:同 (**模型键, 倍率, 机器指纹, 后端**) 覆盖旧记录,其余原样保留(可审计)。
    /// 【后端必须参与去重】否则同一模型在 ncnn / ONNX 两条路上的单价会互相覆盖 —— 一条被删、另一条错用。
    /// 返回新列表(纯函数,不改入参)。</summary>
    public static IReadOnlyList<LocalPrice> Upsert(IEnumerable<LocalPrice>? book, LocalPrice price)
    {
        var list = new List<LocalPrice>();
        bool replaced = false;
        string priceBackend = NormalizeBackend(price.Backend);
        if (book is not null)
            foreach (var p in book)
            {
                if (p.ModelKey == price.ModelKey && p.EngineScale == price.EngineScale && p.MachineKey == price.MachineKey
                    && NormalizeBackend(p.Backend) == priceBackend)
                {
                    if (!replaced) { list.Add(price); replaced = true; }   // 后写覆盖(去重)
                    continue;
                }
                list.Add(p);
            }
        if (!replaced) list.Add(price);
        return list;
    }

    // ───────────────────────── JSON 编解码(纯字符串进出,不做文件 IO) ─────────────────────────

    private sealed class FileModel
    {
        [JsonPropertyName("schema")] public int Schema { get; set; } = SchemaVersion;
        [JsonPropertyName("prices")] public List<JsonPrice> Prices { get; set; } = new();
    }

    private sealed class JsonPrice
    {
        [JsonPropertyName("modelKey")] public string ModelKey { get; set; } = "";
        [JsonPropertyName("engineScale")] public int EngineScale { get; set; }
        [JsonPropertyName("secondsPerFrame")] public double SecondsPerFrame { get; set; }
        [JsonPropertyName("samplePixels")] public long SamplePixels { get; set; }
        [JsonPropertyName("sampleFrames")] public int SampleFrames { get; set; }
        [JsonPropertyName("floorSeconds")] public double FloorSeconds { get; set; }
        [JsonPropertyName("sampleSeconds")] public double SampleSeconds { get; set; }
        [JsonPropertyName("measuredAtUtc")] public string MeasuredAtUtc { get; set; } = "";
        [JsonPropertyName("machineKey")] public string MachineKey { get; set; } = "";
        [JsonPropertyName("backend")] public string Backend { get; set; } = "";
        [JsonPropertyName("note")] public string Note { get; set; } = "";
    }

    /// <summary>读一份 `engine-prices.json` 文本。**文件缺失/截断/字段非法/schema 不认识 → 一律空表,绝不抛**
    /// (标定文件坏掉不能影响任务,大不了重新标一次)。逐条校验:字段缺失或数值非法的那条**只丢它自己**。
    /// 【后端是硬要求(F1-I4 的机械保证)】`backend` 空白/认不出的记录**读回来直接丢弃** ⇒
    /// 文件里不可能存在"后端未确认"的单价,即使有人手写成那样。</summary>
    public static IReadOnlyList<LocalPrice> ParseJson(string? json)
    {
        var empty = (IReadOnlyList<LocalPrice>)Array.Empty<LocalPrice>();
        if (string.IsNullOrWhiteSpace(json)) return empty;
        try
        {
            var model = JsonSerializer.Deserialize<FileModel>(json!);
            if (model is null) return empty;
            if (model.Schema != SchemaVersion) return empty;          // 不认识的 schema:宁可当空表重标
            if (model.Prices is null) return empty;
            var list = new List<LocalPrice>(model.Prices.Count);
            foreach (var p in model.Prices)
            {
                if (p is null || string.IsNullOrWhiteSpace(p.ModelKey)) continue;
                if (p.EngineScale < 1 || p.SamplePixels <= 0 || p.SampleFrames <= 0) continue;
                if (!double.IsFinite(p.SecondsPerFrame) || p.SecondsPerFrame <= 0) continue;
                if (string.IsNullOrWhiteSpace(p.MachineKey)) continue;
                if (NormalizeBackend(p.Backend).Length == 0) continue;   // 后端未确认 ⇒ 不是可用的标定(见上)
                list.Add(new LocalPrice(p.ModelKey, p.EngineScale, p.SecondsPerFrame, p.SamplePixels, p.SampleFrames,
                    p.FloorSeconds, p.SampleSeconds, p.MeasuredAtUtc ?? "", p.MachineKey.Trim(),
                    NormalizeBackend(p.Backend), p.Note ?? ""));
            }
            return list;
        }
        catch { return empty; }
    }

    /// <summary>写成 `engine-prices.json` 文本(schema 1)。</summary>
    public static string ToJson(IEnumerable<LocalPrice>? prices)
    {
        var fm = new FileModel { Schema = SchemaVersion, Prices = new List<JsonPrice>() };
        if (prices is not null)
            foreach (var p in prices)
                fm.Prices.Add(new JsonPrice
                {
                    ModelKey = p.ModelKey, EngineScale = p.EngineScale, SecondsPerFrame = p.SecondsPerFrame,
                    SamplePixels = p.SamplePixels, SampleFrames = p.SampleFrames, FloorSeconds = p.FloorSeconds,
                    SampleSeconds = p.SampleSeconds, MeasuredAtUtc = p.MeasuredAtUtc, MachineKey = p.MachineKey,
                    Backend = NormalizeBackend(p.Backend),
                    Note = p.Note,
                });
        return JsonSerializer.Serialize(fm, new JsonSerializerOptions { WriteIndented = true });
    }
}
