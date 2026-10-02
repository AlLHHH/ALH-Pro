namespace AlhPro.Core;

/// <summary>
/// 「注册表里看得见、系统枚举里却不见了」的显卡诊断(纯逻辑,便于单测)。
///
/// <para>真机背景(2026-10-02):注册表枚举里仍有 NVIDIA GeForce RTX 5060 Laptop GPU,
/// 但同一时刻 Vulkan 引擎枚举与 DXGI(DirectML)枚举里都只剩 AMD 核显(+ 软件适配器),
/// 程序因此把独显判为不可用。这里负责把这种状态翻译成人能看懂的告警与处置建议。</para>
/// </summary>
public static class GpuVisibility
{
    /// <summary>
    /// 注册表(系统设备表)里看得见、但引擎枚举与 DXGI 枚举里都没有的显卡名。
    /// 去重并保持注册表原序。
    /// </summary>
    public static List<string> MissingFromEnumerations(
        IReadOnlyList<string>? registryNames,
        IReadOnlyList<string>? engineNames,
        IReadOnlyList<string>? dxgiNames)
    {
        var result = new List<string>();
        if (registryNames == null) return result;
        foreach (var r in registryNames)
        {
            if (string.IsNullOrWhiteSpace(r)) continue;
            if (ContainsName(engineNames, r) || ContainsName(dxgiNames, r)) continue;
            if (ContainsName(result, r)) continue;
            result.Add(r.Trim());
        }
        return result;
    }

    /// <summary>列表里是否有与 <paramref name="probe"/> 指同一张卡的名字。</summary>
    public static bool ContainsName(IReadOnlyList<string>? names, string? probe)
    {
        if (names == null) return false;
        foreach (var n in names)
            if (Same(n, probe)) return true;
        return false;
    }

    /// <summary>
    /// 两个卡名是否指同一张卡:大小写无关 + 双向包含;太短的串(如 "NVIDIA"/"Radeon" 这类厂商名)
    /// 一律不算,否则"注册表里有 NVIDIA 卡、枚举里只有另一张 NVIDIA 卡"会被误判成"没消失"。
    /// </summary>
    public static bool Same(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        string x = a.Trim(), y = b.Trim();
        if (x.Length < MinComparableNameLength || y.Length < MinComparableNameLength) return false;
        return x.Equals(y, StringComparison.OrdinalIgnoreCase)
            || x.Contains(y, StringComparison.OrdinalIgnoreCase)
            || y.Contains(x, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>可比较的最短卡名长度:短于此长度的串只可能是厂商名(AMD/Intel/NVIDIA/Radeon),
    /// 拿来当"同一张卡"的证据会误判。真实型号名(含 "AMD Radeon(TM) 610M" 19 字符)都远长于此。</summary>
    public const int MinComparableNameLength = 8;

    /// <summary>设备/驱动问题码 → 中文解释。0 = 没有报告问题;null = 连状态都没读出来。</summary>
    public static string ExplainProblemCode(int? problemCode) => problemCode switch
    {
        null => "未能读到该设备的问题代码(它可能已从系统里移除/未呈现,或状态查询失败)—— 请先在设备管理器里确认它是否列出",
        0 => "设备状态正常(没有被禁用、驱动也没有报错)",
        22 => "该设备【已被禁用】(问题代码 22)——在设备管理器里右键「启用设备」即可恢复",
        43 => "驱动报告异常(问题代码 43:设备未能正常启动)——重装/更新显卡驱动后重启电脑",
        18 => "需要重新安装驱动(问题代码 18)",
        28 => "驱动未安装完成(问题代码 28)",
        10 => "设备无法启动(问题代码 10)",
        1 => "设备未正确配置(问题代码 1)",
        45 => "设备当前未连接(问题代码 45)",
        _ => $"设备报告了问题代码 {problemCode}",
    };

    /// <summary>
    /// 独显「注册表可见、枚举不见」时的完整告警文案(含那位用户现在就能做的处置步骤)。
    /// 真机补充线索(2026-10-02):该用户当时【没有插电源】—— 不少笔记本在电池供电时会自动切成
    /// 「仅核显 / 关闭独显」,这正是"注册表还在、两套枚举都没有、驱动版本也没变"的典型成因,
    /// 所以处置第①条就是"先插上电源"。
    /// </summary>
    public static string DescribeMissingGpu(string name, int? problemCode)
    {
        bool noProblemReported = problemCode.GetValueOrDefault(0) == 0;
        return "⚠ 显卡「" + name + "」在注册表(系统设备表)里还在,但【Vulkan 引擎枚举】和【DXGI(DirectML)枚举】里都没有它"
            + " —— 程序据此判它当前不可用。"
            + Environment.NewLine + "   设备状态:" + ExplainProblemCode(problemCode)
            + (noProblemReported
                ? Environment.NewLine + "   设备本身没报错却看不见 ⇒ 更可能是【供电 / 显卡模式】把它关掉了:笔记本没插电源(电池供电时不少机型会自动切成「仅核显 / 关闭独显」)、厂商软件的节能(Eco)模式、MUX 切到仅核显,或驱动崩过一次后没能重新加载。"
                : "")
            + Environment.NewLine + "   现在就能试的处置:"
            + "①先给笔记本【插上电源】(电池供电是最常见的诱因),插好后点软件里的「重新检测」再看设备列表;"
            + "②设备管理器 → 显示适配器,看它有没有被禁用或带黄色感叹号(被禁用→右键「启用设备」;问题代码 43→重装/更新显卡驱动后重启);"
            + "③笔记本 GPU 模式(Armoury Crate / 联想电脑管家 / NVIDIA 控制面板「管理显示模式」)是否被切到「仅核显 / Eco / 省电」,切回「混合输出 / 独显优先」;"
            + "④把软件里的「计算设备」改成另一张能看到的硬件卡(比如核显/另一张独显),先把活干起来;"
            + "⑤重启电脑后再试。";
    }
}
