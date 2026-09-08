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
}
