using System.IO;
using System.Text.Json;

namespace ALHPro;

/// <summary>
/// 耗时经验库:记录"某配置(引擎/倍率/分辨率/帧数/去重/降噪/后处理)跑一遍的实际耗时",
/// 下次同配置任务开始时用它校准初始估算 —— 越用越准(用户要求"记住一遍耗时多久")。
/// 存储:%LOCALAPPDATA%\ALHPro\perf-history.json
/// </summary>
public static class PerfMemory
{
    private sealed class Entry
    {
        public double Seconds { get; set; }      // 最近一次实测耗时
        public int Frames { get; set; }          // 处理的视频总帧数
        public DateTime At { get; set; }         // 记录时间
        public double PerFrame { get; set; }     // 秒/帧(按面积归一由调用方算好,这里直接存)
    }

    private static readonly object Lock = new();
    private static Dictionary<string, Entry>? _cache;
    private static int _dirty;

    private static string FilePath => ParaPaths.SettingsFile("perf-history.json");

    private static Dictionary<string, Entry> Load()
    {
        if (_cache != null) return _cache;
        try
        {
            if (File.Exists(FilePath))
                _cache = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath))
                    ?? new Dictionary<string, Entry>();
            else
                _cache = new Dictionary<string, Entry>();
        }
        catch { _cache = new Dictionary<string, Entry>(); }
        return _cache;
    }

    private static void Save()
    {
        try
        {
            lock (Lock)
            {
                var json = JsonSerializer.Serialize(_cache);
                File.WriteAllText(FilePath, json, new System.Text.UTF8Encoding(false));
                _dirty = 0;
            }
        }
        catch { /* 失败忽略 */ }
    }

    /// <summary>配置指纹:引擎/倍率/分辨率档次/去重/降噪/后处理 等关键参数拼串。</summary>
    public static string Fingerprint(string engine, double scale, int w, int h,
        int interpScale, bool dedup, int videoDenoise, bool postFx)
    {
        int areaTier = Math.Clamp((int)Math.Round((double)w * h / 2_073_600.0 * 10), 1, 40);   // 1080p=10
        return $"{engine}|s{scale:0.#}|a{areaTier}|i{interpScale}|d{dedup}|n{videoDenoise}|p{postFx}";
    }

    /// <summary>查经验:同配置上次实测的"秒/帧"(无记录返回 null)。</summary>
    public static double? PerFrameFor(string key)
    {
        try
        {
            if (Load().TryGetValue(key, out var e) && e.PerFrame > 0.001) return e.PerFrame;
        }
        catch { }
        return null;
    }

    /// <summary>记录一次实测耗时(帧数 = 处理视频总帧数,areaN = 面积归一倍数)。</summary>
    public static void Record(string key, double seconds, int frames, double areaN)
    {
        try
        {
            var map = Load();
            double per = frames > 0 ? seconds / frames : 0;
            if (per <= 0) return;
            lock (Lock)
            {
                map[key] = new Entry
                {
                    Seconds = seconds,
                    Frames = frames,
                    At = DateTime.Now,
                    PerFrame = per / Math.Max(0.25, areaN),   // 归一化为"1080p 基准秒/帧"
                };
                _dirty++;
            }
            // 攒 3 条再落盘,避免频繁磁盘写
            if (_dirty >= 3) Save();
        }
        catch { }
    }
}
