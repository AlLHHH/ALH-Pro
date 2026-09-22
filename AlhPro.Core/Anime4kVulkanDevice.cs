using System.Globalization;
using System.Text.RegularExpressions;

namespace AlhPro.Core;

/// <summary>Anime4K(<c>libplacebo</c>)**该用哪个 Vulkan 设备** —— 纯逻辑(解析 + 选择策略),可单测。
///
/// ================= 为什么需要"选设备"这件事 =================
/// 双显卡机(Intel/AMD 核显 + NVIDIA 独显,典型就是 5060 Laptop 那一类)上,`libplacebo` 可能把 Vulkan
/// 设备初始化到核显上:结果是**着色器编译极慢甚至挂死**(用户那台机器的诊断包里这条探测结论正是
/// `无响应(超时被强杀)`),而"选错设备"从错误信息里完全看不出来。
/// `libplacebo` 滤镜**自己没有选设备的选项**(已用 `ffmpeg -h filter=libplacebo` 查实,只有 `inherit_device`),
/// 唯一的杠杆是 **ffmpeg 的全局设备初始化**:`-init_hw_device vulkan=alh:&lt;n&gt; -filter_hw_device alh`
/// ⇒ 索引 n 指的是 ffmpeg 自己枚举出来的 Vulkan 设备序号。
///
/// ================= 索引从哪来(不靠猜) =================
/// `ffmpeg -v verbose -init_hw_device vulkan=alh …` 会把枚举结果打印出来(本机实测 443ms):
/// <code>
/// [Vulkan @ 000002b5baa72140] GPU listing:
/// [Vulkan @ 000002b5baa72140]     0: NVIDIA GeForce RTX 4060 Laptop GPU (discrete) (0x28a0)
/// [Vulkan @ 000002b5baa72140] Device 0 selected: NVIDIA GeForce RTX 4060 Laptop GPU (discrete) (0x28a0)
/// </code>
/// 于是"索引 ↔ 设备名"是**可读的事实**,而不是试出来的。
///
/// ================= 选择策略(三条,逐条都有理由) =================
///   ① 只有一台 Vulkan 设备 ⇒ **不加任何参数** —— 与改动前的行为逐字一致(本机就是这种:
///      ffmpeg 只列出 RTX 4060,没有核显)。能不改变已验证过的路径就不改。
///   ② 界面上选的那张卡(按名字)能在列表里找到 ⇒ 用它 —— setup 里 GpuIndex 与 GpuName 是一起存的
///      (`AppSettings.GpuName`),处理用哪张卡、Anime4K 就该用哪张卡,不能各选各的。
///   ③ 找不到名字 ⇒ 优先"**独立显卡 + NVIDIA**"那一台(核显正是要避开的那个);再退而求其次取任意独显;
///      实在分不出来(全是核显/虚拟设备)就返回 null = 交给 ffmpeg 默认(不猜)。
/// 【为什么把"没独立显卡就不指定"写进策略】宁可保持默认行为,也不要凭一个猜测把滤镜钉到某张卡上 ——
/// 钉错的后果(超时/挂死)比"可能选到核显"更糟,而后者在单设备机器上根本不存在。</summary>
public static class Anime4kVulkanDevice
{
    /// <summary>枚举到的 Vulkan 设备(来自 ffmpeg 的 `GPU listing:` 段)。</summary>
    /// <param name="Index">ffmpeg 的 Vulkan 设备序号(= `-init_hw_device vulkan=alh:N` 里的 N)。</param>
    /// <param name="Name">ffmpeg 报的设备名(完整,例如 "NVIDIA GeForce RTX 4060 Laptop GPU")。</param>
    /// <param name="Kind">设备类别:discrete / integrated / virtual / cpu / 空(ffmpeg 没报时)。</param>
    public readonly record struct Device(int Index, string Name, string Kind)
    {
        public bool Discrete => string.Equals(Kind, "discrete", StringComparison.OrdinalIgnoreCase);
        public bool Integrated => string.Equals(Kind, "integrated", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>枚举命令用的参数(给调用方拼 `ffmpeg &lt;这些&gt;`):只初始化设备、不做任何实际处理。
    /// 【为什么要真跑一次 ffmpeg】ffmpeg 没有"只列设备"的开关;这条 lavfi 空源 + 1 帧 null 输出
    /// 是本机实测做法(443ms,不碰素材、不写文件)。</summary>
    public const string ListDevicesArgs =
        "-v verbose -init_hw_device vulkan=alh -f lavfi -i \"nullsrc=s=64x64:d=0.1\" -frames:v 1 -f null -";

    /// <summary>把"用第 n 台设备"变成 ffmpeg 的**全局**参数(必须放在第一个 `-i` 之前)。
    /// 本机实测:索引 0 + Anime4K 滤镜 → exit 0;索引越界 → `Unable to find device with index 1!`(exit -19),
    /// 即索引写错是**立刻可见的硬错误**,不会静默跑到核显上。</summary>
    public static string DeviceArgs(int index)
        => "-init_hw_device vulkan=alh:" + index.ToString(CultureInfo.InvariantCulture) + " -filter_hw_device alh ";

    /// <summary>从 ffmpeg `-v verbose` 的完整输出里解析 `GPU listing:` 段。
    /// 【容错】只认"列表开始之后"的行,且必须形如 `&lt;可选的 [Vulkan @ 0x…] 前缀&gt; &lt;序号&gt;: &lt;名字&gt;`;
    /// 名字末尾的 `(discrete)`/`(integrated)`/`(virtual)`/`(cpu)` 与 `(0x28a0)` 这类十六进制 id 都不进名字。
    /// 解析不出来就返回空列表(调用方据此保持默认行为,不猜)。</summary>
    public static List<Device> ParseGpuListing(string? ffmpegVerboseLog)
    {
        var result = new List<Device>();
        if (string.IsNullOrEmpty(ffmpegVerboseLog)) return result;
        bool inList = false;
        foreach (var rawLine in ffmpegVerboseLog.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;
            if (!inList)
            {
                if (line.Contains("GPU listing", StringComparison.OrdinalIgnoreCase)) inList = true;
                continue;
            }
            // 列表结束的标志:出现"Device N selected"之后就是扩展/日志了
            if (line.Contains("selected:", StringComparison.OrdinalIgnoreCase)) break;
            // 只取 "…] <n>: <name>…" 这种形状(前面可能带 [Vulkan @ 0x…] 前缀)
            var m = Regex.Match(line, @"(?:^\]|\])\s*(\d+):\s*(.+)$");
            if (!m.Success) m = Regex.Match(line, @"^(\d+):\s*(.+)$");
            if (!m.Success) continue;
            if (!int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx)) continue;
            var rest = m.Groups[2].Value.Trim();
            // 【顺序要紧:先剥十六进制 id,再认设备类别】ffmpeg 的原文是
            //   `0: NVIDIA GeForce RTX 4060 Laptop GPU (discrete) (0x28a0)`
            // —— 类别括号**不在行尾**(行尾是 `(0x28a0)`),所以"先认类别"那种写法会认不到,
            // 名字里会残留 `(discrete)`(2026-09-23 单测第一次跑就逮到了这个)。
            rest = Regex.Replace(rest, @"\s*\(0x[0-9a-fA-F]+\)\s*$", "").Trim();
            string kind = "";
            var km = Regex.Match(rest, @"\((discrete|integrated|virtual|cpu)\)\s*$", RegexOptions.IgnoreCase);
            if (km.Success)
            {
                kind = km.Groups[1].Value.ToLowerInvariant();
                rest = rest.Substring(0, km.Index).Trim();
            }
            if (rest.Length == 0) continue;
            if (result.Any(d => d.Index == idx)) continue;
            result.Add(new Device(idx, rest, kind));
        }
        return result;
    }

    /// <summary>选哪一台(返回 null = 不加任何设备参数,保持 ffmpeg 默认行为)。策略见类注释。</summary>
    public static int? Pick(IReadOnlyList<Device> devices, string? preferredName)
    {
        if (devices == null || devices.Count == 0) return null;
        if (devices.Count == 1) return null;                      // ① 只有一台:默认就是它,不动
        var byName = MatchByName(devices, preferredName);
        if (byName.HasValue) return byName;                       // ② 界面选的那张卡优先
        var nv = devices.FirstOrDefault(d => d.Discrete && GpuName.IsNvidia(d.Name));
        if (nv.Name != null) return nv.Index;                     // ③ 独立 + NVIDIA
        var anyDiscrete = devices.FirstOrDefault(d => d.Discrete);
        if (anyDiscrete.Name != null) return anyDiscrete.Index;   // ③b 任意独显
        return null;                                              // ④ 分不出来就不猜
    }

    /// <summary>按名字找设备。三级匹配:完全一致 → 互相包含 → 型号数字相同(如 "5060")。
    /// 【为什么要第三级】一个名字来自我们的设备枚举(ncnn/DXGI),另一个来自 ffmpeg 的 Vulkan 枚举,
    /// 同一个卡两边的写法偶尔不完全一致(例如带不带 "Laptop"/"Laptop GPU");型号数字是最稳的锚点。
    /// 【为什么必须要求"数字相同"而不是"包含数字"】否则 "RTX 4060" 会匹配到 "RTX 40608"(不存在的型号),
    /// 这类模糊匹配一旦选错,后果是滤镜跑到核显上(超时/挂死)⇒ 宁可放弃匹配、走 ③ 的保守策略。</summary>
    public static int? MatchByName(IReadOnlyList<Device> devices, string? preferredName)
    {
        if (devices == null || string.IsNullOrWhiteSpace(preferredName)) return null;
        var want = Norm(preferredName);
        if (want.Length == 0) return null;
        foreach (var d in devices)
            if (Norm(d.Name) == want) return d.Index;
        foreach (var d in devices)
        {
            var have = Norm(d.Name);
            if (have.Contains(want, StringComparison.Ordinal) || want.Contains(have, StringComparison.Ordinal))
                return d.Index;
        }
        var wm = ModelTokens(want);
        if (wm.Count > 0)
        {
            foreach (var d in devices)
                if (ModelTokens(Norm(d.Name)).Overlaps(wm)) return d.Index;
        }
        return null;
    }

    /// <summary>归一化:小写、去掉厂商/通用词与多余空格(只保留可比较的部分)。</summary>
    private static string Norm(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var t = s.ToLowerInvariant();
        foreach (var drop in new[] { "nvidia", "geforce", "amd", "radeon", "intel", "graphics", "gpu" })
            t = t.Replace(drop, " ");
        t = Regex.Replace(t, @"[^a-z0-9 ]+", " ");
        return string.Join(" ", t.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>取名字里的"4 位型号数字"(RTX 4060 → 4060;Arc A770 → 770 太短不算)。</summary>
    private static HashSet<string> ModelTokens(string normalized)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(normalized, @"\b\d{4}\b")) set.Add(m.Value);
        return set;
    }

    /// <summary>日志用的一句话(把"枚举结果 + 为什么这么选"写全,排查时不必再跑一次 ffmpeg)。</summary>
    public static string Describe(IReadOnlyList<Device> devices, int? picked, string? preferredName, string source)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Anime4K Vulkan 设备枚举(").Append(source).Append("):");
        if (devices.Count == 0) sb.Append("(没解析到设备列表)");
        foreach (var d in devices)
            sb.Append($" [{d.Index}] {d.Name}{(d.Discrete ? "(独显)" : d.Integrated ? "(核显)" : d.Kind.Length > 0 ? $"({d.Kind})" : "")}");
        sb.Append(picked.HasValue
            ? $" ⇒ 使用索引 {picked.Value}"
            : devices.Count == 1 ? " ⇒ 只有一台设备,不加参数(保持 ffmpeg 默认)"
            : " ⇒ 不指定设备(保持 ffmpeg 默认:没有可信依据时不猜)");
        if (!string.IsNullOrWhiteSpace(preferredName)) sb.Append($"(界面选的卡:{preferredName})");
        return sb.ToString();
    }
}
