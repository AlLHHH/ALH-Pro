using System.IO;
using System.Text;
using AlhPro.Core;

namespace ALHPro;

/// <summary>**本机超分单帧耗时标定**的落盘记忆(`engine-prices.json`)。
///
/// 【为什么不复用 <see cref="PerfMemory"/>】那张表记的是"**整条链路**的秒/帧"(含拆帧/去重/补帧/编码),
/// 用来估 ETA;而阶段顺序判据要的是"**纯超分引擎**的秒/帧"(已扣掉每进程地板)—— 两者口径不同,
/// 混在一个文件里迟早互相污染。<see cref="AlhPro.Core.LocalPriceBook"/> 里那套记录/拒收规则才是判据。
///
/// 【位置】`ParaPaths.SettingsFile("engine-prices.json")` ⇒ `%LOCALAPPDATA%\ALHPro\settings\engine-prices.json`
/// (与 `perf-history.json` 同目录;旧版根目录文件由 ParaPaths 自动迁移)。
/// 【结构】`{ "schema": 1, "prices": [ … ] }`,按 (模型键, 引擎倍率, 机器指纹) 去重、后写覆盖。
/// 【容错】文件缺失 / 截断 / 字段非法 / schema 不认识 ⇒ **一律空表,绝不抛**
/// (标定文件坏掉不能影响任务;大不了重新标一次)。逐条校验:坏的只丢坏的那条。
/// 【写盘】先写 `engine-prices.json.tmp` 再 `File.Move(..., overwrite: true)` —— 中途断电/被杀
/// 也不会留下半截 JSON 把整个表废掉。
///
/// 纯逻辑部分(两点的数值校验、拒收理由、JSON 编解码)全在 `AlhPro.Core.LocalPriceBook`(有单测);
/// 本文件只负责"路径 + 文件 IO + 机器指纹"这三件必须依赖 UI 层的事。</summary>
public static class CalibMemory
{
    private static readonly object Lock = new();
    private static IReadOnlyList<LocalPrice>? _cache;
    private static string? _machineKey;

    /// <summary>落盘路径(与 PerfMemory 同目录)。</summary>
    public static string FilePath => ParaPaths.SettingsFile("engine-prices.json");

    /// <summary>本机已有的全部标定记录(**不判机器指纹**;判机器由
    /// <see cref="LocalPriceBook.Resolve"/> 做)。任何异常都收敛成空表。</summary>
    public static IReadOnlyList<LocalPrice> All()
    {
        lock (Lock)
        {
            if (_cache != null) return _cache;
            try
            {
                string path = FilePath;
                _cache = File.Exists(path)
                    ? LocalPriceBook.ParseJson(File.ReadAllText(path))
                    : Array.Empty<LocalPrice>();
                if (_cache.Count == 0 && File.Exists(path))
                    AppLogger.Warn($"⚠ 超分单帧耗时标定表为空或不可读({path})—— 本次按「本机未标定」保守用旧顺序,稍后可重新标定");
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"⚠ 读超分单帧耗时标定表失败,按空表继续(不影响任务):{ex.Message}");
                _cache = Array.Empty<LocalPrice>();
            }
            return _cache;
        }
    }

    /// <summary>写入/覆盖一条标定(同 模型键 + 倍率 + 机器指纹 覆盖旧的;其它机器的旧记录原样保留,可审计)。
    /// 落盘失败只记日志,不抛 —— 标定失败不该中断任务。</summary>
    public static void Upsert(LocalPrice price)
    {
        lock (Lock)
        {
            var merged = LocalPriceBook.Upsert(_cache ?? All(), price);
            _cache = merged;
            try
            {
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, LocalPriceBook.ToJson(merged), new UTF8Encoding(false));
                File.Move(tmp, FilePath, overwrite: true);   // 先写临时文件再原子替换,防写坏
                AppLogger.Info($"超分单帧耗时标定已记住:{price.ModelKey}@{price.EngineScale}x = {price.SecondsPerFrame:0.####} s/帧"
                    + $"(采样 {LocalPrice.PixelsToText(price.SamplePixels)})→ {FilePath}");
            }
            catch (Exception ex) { AppLogger.Warn($"⚠ 写超分单帧耗时标定表失败(本次仍按刚测出的数字判定,只是下次要重标):{ex.Message}"); }
        }
    }

    /// <summary>**当前机器指纹**:GPU 名称 + 三个 ncnn 引擎可执行文件的标识(文件名 + 字节数 + 最后写入时间)。
    /// 换显卡、换/重编引擎 ⇒ 指纹变 ⇒ 旧标定自动失效并重标(不匹配的旧记录保留在文件里,但不参与判定)。
    /// 【为什么含文件名与字节数】引擎目录里同名不同版本的 exe 是最容易被忽略的"换了机器"情形。
    /// 结果按进程缓存(GPU/引擎在一次运行内不会变);取不到任何信息时返回 `no-gpu|…:none`,绝不抛。</summary>
    public static string MachineKeyOf()
    {
        if (_machineKey != null) return _machineKey;
        var sb = new StringBuilder();
        try
        {
            var names = GpuInfo.GetAdapterNames();
            sb.Append(names.Count > 0 ? string.Join("+", names) : "no-gpu");
        }
        catch { sb.Append("no-gpu"); }
        AppendEngine(sb, "realesrgan", SafeExe(EngineService.FindRealESRGAN));
        AppendEngine(sb, "waifu2x", SafeExe(EngineService.FindWaifu2x));
        AppendEngine(sb, "realcugan", SafeExe(EngineService.FindRealCugan));
        _machineKey = sb.ToString();
        return _machineKey;
    }

    private static string? SafeExe(Func<string?> find)
    {
        try { return find(); } catch { return null; }
    }

    private static void AppendEngine(StringBuilder sb, string tag, string? path)
    {
        sb.Append('|').Append(tag).Append(':');
        try
        {
            if (string.IsNullOrEmpty(path)) { sb.Append("none"); return; }
            var fi = new FileInfo(path);
            sb.Append(Path.GetFileName(path)).Append('@').Append(fi.Length).Append('@').Append(fi.LastWriteTimeUtc.Ticks);
        }
        catch { sb.Append("unknown"); }
    }

    /// <summary>仅供单测/诊断:清掉进程内缓存(下次 <see cref="All"/> 重新读盘)。</summary>
    internal static void ResetCacheForTest()
    {
        lock (Lock) { _cache = null; _machineKey = null; }
    }
}
