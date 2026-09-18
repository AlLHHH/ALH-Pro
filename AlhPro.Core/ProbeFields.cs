using System.Globalization;

namespace AlhPro.Core;

/// <summary>解析 ffprobe「带标签」输出(唯一稳健的口径)。
///
/// 【为什么不能用 `-of csv=p=0` + Split(',')】真机实测(2026-09-16,ffprobe n7.1):
///   `ffprobe -v error -select_streams v:0 -count_frames -show_entries stream=nb_read_frames,avg_frame_rate -of csv=p=0 file`
///   把整行 **打印两遍**,stdout 实际是 `"24000/1001,518\r\n24000/1001,518\r\n"`。
///   于是 `Trim().Split(',')` 得到 `["24000/1001", "518\r\n\r\n24000/1001", "518"]` ——
///   `parts[1]` 里混进了换行和下一行的内容,`long.TryParse` 直接失败 ⇒
///   `ProbeTrueFramesAndDuration` **永远返回 (0,0)**("真实时长"恒为 0,而调用方只看返回值、不看日志);
///   `ValidateVideoFileAsync` 按 `fields[1]` 取帧数,取到的正是那一串混合文本。
/// 【修法】改成 `-of default=nw=1:nk=0` 的**带标签**输出(`nb_read_frames=518`),按行找 `键=` 前缀取值:
///   · 行序无关、打印几遍都无所谓(同键取第一个非空值);
///   · 遇到空值行(如 `nb_frames=N/A`,常见于 MKV/流式封装)跳过、继续看下一条,不把整次探测判死;
///   · 顺带把 `avg_frame_rate=24000/1001` 这种分数帧率按原口径解析。
/// 纯函数、无 IO —— 便于单测钉住("打印两遍""垃圾行混入""空值在前"三种真机形态都覆盖)。</summary>
public static class ProbeFields
{
    /// <summary>取 `键=值` 形式的小数字段(如 format 的 duration)。找不到 / 空值 / 非数字 → null。</summary>
    public static double? DoubleField(string raw, string key)
    {
        var v = RawField(raw, key);
        if (v == null) return null;
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0 ? d : null;
    }

    /// <summary>取 `键=值` 形式的整数字段(如 nb_read_frames)。找不到 / 空值 / 非数字 → null。</summary>
    public static long? LongField(string raw, string key)
    {
        var v = RawField(raw, key);
        if (v == null) return null;
        return long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    /// <summary>取 `键=值` 形式的帧率字段:支持 `24000/1001` 分数与 `23.976` 小数两种写法。</summary>
    public static double? FpsField(string raw, string key)
    {
        var v = RawField(raw, key);
        if (v == null) return null;
        var inv = CultureInfo.InvariantCulture;
        var parts = v.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, inv, out var num)
            && double.TryParse(parts[1], NumberStyles.Float, inv, out var den) && den > 0)
            return num / den;
        return double.TryParse(v, NumberStyles.Float, inv, out var f) && f > 0 ? f : null;
    }

    /// <summary>取「真实画面时长」的输入对:帧数 + 帧率。任一取不到 → (0,0),与老口径一致(调用方按"探测失败"处理)。
    /// 时长 = (帧数-1)÷帧率:第 n 帧没有时长、只有 n-1 个帧间隔(旧口径注释里的 37/20.844 = 1.7751 就是这条)。</summary>
    public static (long frames, double duration) FramesAndContentDuration(string raw)
    {
        var nf = LongField(raw, "nb_read_frames");
        var fr = FpsField(raw, "avg_frame_rate");
        if (nf is not > 0 || fr is not > 0) return (0, 0);
        double dur = nf >= 2 ? (nf.Value - 1) / fr.Value : 1.0 / fr.Value;
        return dur > 0.01 ? (nf.Value, dur) : (0, 0);
    }

    /// <summary>按行找 `键=` 前缀并取其后内容(去掉尾部 \r 与两端空白)。
    /// 值里带空格也算有效(如 `nb_frames=N/A` 会被判为无效值,由调用方跳过)。</summary>
    public static string? RawField(string raw, string key)
    {
        if (string.IsNullOrEmpty(raw) || string.IsNullOrEmpty(key)) return null;
        var prefix = key + "=";
        using var reader = new StringReader(raw);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var s = line.Trim();
            if (s.Length <= prefix.Length) continue;               // 空值行(键后面什么都没有)一律跳过
            if (!s.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var v = s.Substring(prefix.Length).Trim();
            // "N/A" / "unknown" 这类占位不算值 —— 留着会让 TryParse 失败,不如直接当作"这一行没用"继续找下一行
            if (v.Length == 0 || v.Equals("N/A", StringComparison.OrdinalIgnoreCase)
                || v.Equals("unknown", StringComparison.OrdinalIgnoreCase)) continue;
            return v;
        }
        return null;
    }
}
