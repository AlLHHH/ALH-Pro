using System.Text.RegularExpressions;

namespace AlhPro.Core;

/// <summary>
/// 显卡名 → 家族判定(纯字符串逻辑,可单测)。硬件枚举留在 EngineService/VulkanCheck,这里只认名字。
/// 判错方向的代价不对称:漏判 50 系会让 ncnn-Vulkan 崩(有真机探测兜底);
/// 误判则会把好卡悄悄降到更慢的 ONNX 路线,用户只看到"变慢"、日志里也没有任何异常。
/// </summary>
public static class GpuName
{
    /// <summary>消费级 Blackwell 一律叫 "RTX 50xx";专业卡另有 "RTX PRO 6000 Blackwell" 这种写法。</summary>
    private static readonly Regex BlackwellConsumer =
        new(@"RTX\s*50\d{2}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BlackwellWord =
        new(@"\bBlackwell\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>名字里同样含 "RTX 50xx" 但【不是】Blackwell、且完全支持 ncnn-Vulkan 的卡:
    /// "NVIDIA RTX 5000 Ada Generation"、"NVIDIA RTX 5880 Ada Generation"、"Quadro RTX 5000"(Turing)。
    /// 只写 RTX\s*50\d{2} 会把这些卡一并推到 ONNX —— 白白损失速度,用户侧没有任何报错可查。</summary>
    private static readonly Regex OlderArchWord =
        new(@"\b(Ada|Turing|Ampere|Hopper|Pascal|Volta|Quadro)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>该显卡名是否属于 RTX 50 系(Blackwell)——2022 版 ncnn-Vulkan 引擎在其上会崩。</summary>
    public static bool IsBlackwell(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (OlderArchWord.IsMatch(name)) return false;
        return BlackwellConsumer.IsMatch(name) || BlackwellWord.IsMatch(name);
    }

    /// <summary>设备名集合里是否存在 Blackwell(多卡机:只要有一张 50 系就按 50 系走)。</summary>
    public static bool AnyIsBlackwell(IEnumerable<string?> names) => names.Any(IsBlackwell);

    /// <summary>D3D12 转译层(Mesa Dozen 等)伪装成的"Vulkan"设备,名字形如
    /// "Microsoft Direct3D12 (NVIDIA GeForce RTX 4060 Laptop GPU)"。ncnn 会把它当普通 Vulkan 设备枚举、
    /// 编号还夹在原生设备中间,但经其计算的补帧/超分输出是【损坏帧】(真机:插值帧整帧红噪点+底部黑带,
    /// 源帧正常);名字含 NVIDIA/RTX 又会被打分算法当成独显推荐。判错代价不对称:漏判 = 用户拿到损坏视频
    /// 且日志无异常;误判 = 剔除一个本可用的设备(而同一块物理卡的原生 Vulkan 设备总在表里,不会无路可走)。</summary>
    public static bool IsD3D12Translation(string? name)
        => !string.IsNullOrWhiteSpace(name)
           && name.StartsWith("Microsoft Direct3D12", StringComparison.OrdinalIgnoreCase);

    /// <summary>该显卡名是否属于【核显/集成显卡】(纯字符串判定,可单测)。
    /// 独立显卡全部返回 false: NVIDIA GeForce/RTX、AMD RX/Radeon Pro、Intel Arc/独显等。
    /// 与 UI 工程 GpuInfo.IsIntegratedGPU 同口径 —— 统一搬进 Core,供设备路由「选独显不落核显」判定。</summary>
    public static bool IsIntegrated(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
        {
            if (name.Contains("Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.Contains("Radeon Graphics", StringComparison.OrdinalIgnoreCase)) return true;
            // APU 核显:如 Radeon(TM) 680M / 780M / 480M ...(RX 独显不含 "M" 后缀型号)
            if (System.Text.RegularExpressions.Regex.IsMatch(name,
                @"Radeon(\(TM\))?\s*(?:[3-9]\d{2}M|1\d{2}M)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return true;
            // Vega 核显:Vega 3/5/6/7/8/9/10/11(RX Vega 56/64 独显,排除)
            if (System.Text.RegularExpressions.Regex.IsMatch(name,
                @"Vega\s*(?:3|4|5|6|7|8|9|10|11)(?!\s*(?:56|64))", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return true;
        }
        if (!name.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return false;
        return name.Contains("UHD", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Iris", StringComparison.OrdinalIgnoreCase)
            || name.Contains("HD Graphics", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Intel(R) Graphics", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Intel(R) Iris", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>设备优先级分数(越大越该用):核显=0(不选),NVIDIA=4,AMD 独显=3,Intel Arc=2,其他独显=1。
    /// 与 UI 工程 GpuInfo.ScoreDeviceName 同口径 —— 统一搬进 Core,供设备路由取「最佳独显」。</summary>
    public static int Score(string? name)
    {
        if (string.IsNullOrEmpty(name) || IsIntegrated(name)) return 0;
        if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
            || name.Contains("GeForce", StringComparison.OrdinalIgnoreCase)
            || name.Contains("RTX", StringComparison.OrdinalIgnoreCase))
            return 4;
        if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
            return 3;
        if (name.Contains("Arc", StringComparison.OrdinalIgnoreCase))
            return 2;
        return 1;
    }
}
