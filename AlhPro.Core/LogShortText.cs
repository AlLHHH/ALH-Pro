namespace AlhPro.Core;

/// <summary>【任务 X1/X2/X3】界面日志区(左下角)专用的**短句**文案生成(纯函数,可单测)。
/// 【为什么单独一层】用户真机原话:「日志那么一坨 后面新加的能不能简洁一点」—— 结论被解释性长句淹没,
/// 用户根本没找到批次信息(它其实写了,只是在超分阶段开始时才出现、而且太长)。
/// 于是把"界面要的那几句"变成纯函数放在这里:界面文案可被单测钉住(见 AlhPro.Tests/LogShortTextTests),
/// 而解释性内容(命中规则 / 面积系数 / "取较低者" / 阈值清单 / 性能档判定依据串 / 守门公式细节 / 切点清单)
/// 一律只写 <c>AppLogger</c> 文件日志,**不上界面**。
/// 【硬规则(任务 X4;有单测钉住,别绕过)】
///   ① 不含"第 N 帧 / 共 M 帧"结构 —— 那种文案会被界面进度解析(etaRegex)当成步骤行抢走;
///   ② 不含"完成"二字 —— 含它会被界面改写成 "✓ …" 并把当前步骤行清掉;
///   ③ 一行不超过约 <see cref="MaxChineseCharsPerLine"/> 个汉字(超限按 <see cref="ClampToChineseLimit"/> 截断,
///      全量数字仍在文件日志里,截断只影响界面这一行);
///   ④ 不用警告色:调用方照旧加 `· ` 前缀(那是"进日志区、不抢步骤行、不动进度条、不走 ⚠ 黄字"的既有通道)。
/// 【口径来源】所有数字都取自调用方传入的既有判定结果(本类不做任何判定、不改任何行为)。</summary>
public static class LogShortText
{
    /// <summary>界面日志区一行允许的汉字上限(用户口径:一行不超过约 60 个汉字;数字/符号不参与计数)。</summary>
    public const int MaxChineseCharsPerLine = 60;

    /// <summary>"第 N 帧 / 共 M 帧"结构里会被进度解析抢先的关键片段(单测用它断言界面文案不踩雷)。</summary>
    public const string ProgressStealingFragment = "共";

    /// <summary>会被界面改写成 "✓ …"、并清掉当前步骤行的字样(单测用它断言界面文案不踩雷)。</summary>
    public const string StepLineKillerWord = "完成";

    /// <summary>一行里的汉字个数(只数 CJK 统一表意文字基本区 U+4E00~U+9FFF;与"≤60 个汉字"口径一致)。</summary>
    public static int ChineseCharCount(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int n = 0;
        foreach (char c in text!)
            if (c >= '\u4E00' && c <= '\u9FFF') n++;
        return n;
    }

    /// <summary>超限就截断到"能装下的最后一个汉字"并补省略号;不超限原样返回。
    /// 【为什么按汉字位置截】用户口径是按汉字算长短,而数字(int/double 格式化出来的)不算 ——
    /// 按字符数截会把"285 帧/批 ×1 批"这类关键数字砍掉,反而更难读。</summary>
    public static string ClampToChineseLimit(string text, int maxChineseChars = MaxChineseCharsPerLine)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        if (ChineseCharCount(text) <= maxChineseChars) return text;
        int seen = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] >= '\u4E00' && text[i] <= '\u9FFF')
            {
                seen++;
                if (seen == maxChineseChars) return text[..(i + 1)] + "…";
            }
        }
        return text;
    }

    /// <summary>【任务 X1】设备档位的**界面短词**:"内存好×性能快"这种一眼能懂的说法
    /// (内存档来自 <see cref="RenderPolicy.TierFor"/>,性能档来自 <see cref="PerfScore"/>;
    /// 两者"取较低者"的完整推导仍只写文件日志,界面只报这两个输入档)。
    /// 用户示例口径:<c>设备档:内存好×性能快</c>。</summary>
    public static string DeviceTierShortText(double freeRamGB, PerfScore perf)
    {
        static string Word(RenderPolicy.DeviceTier t) => t switch
        {
            RenderPolicy.DeviceTier.Strong => "好",
            RenderPolicy.DeviceTier.Normal => "正常",
            _ => "差",
        };
        string perfWord = perf == PerfScore.Fast ? "快" : perf == PerfScore.Normal ? "正常" : "慢";
        return $"内存{Word(RenderPolicy.TierFor(freeRamGB))}×性能{perfWord}";
    }

    /// <summary>【任务 X1】顺序判定的**界面短句**:只说"选了什么 + 差多少 + 够不够门槛"。
    /// 完整判据(u / r_lo / r_hi / 门槛秒数 / 成本表实测出处)仍由
    /// <see cref="PipelineOrderPlan.Decision.LogLine"/> 写文件日志。
    /// 【两种"没切到新顺序"的写法必须分开】更省但不够门槛 = "只快 X%";反而更慢 = "反而慢 X%"
    /// —— 用户真机那次就是"先超分反而更慢 0.4%",写"只快"会骗人(任务 Q1 的诚实口径)。</summary>
    public static string OrderShortText(PipelineOrderPlan.Decision d, double minSavingsPercent)
    {
        string pick = d.UpscaleFirst ? "超分→补帧" : "补帧→超分";
        if (d.UpscaleFirst) return $"{pick}(预计省 {d.SavingsPercent:0.#}%)";
        return d.SavingsSeconds >= 0
            ? $"{pick}(先超分只快 {d.SavingsPercent:0.#}%,未达 {minSavingsPercent:0.#}% 门槛)"
            : $"{pick}(先超分反而慢 {Math.Abs(d.SavingsPercent):0.#}%,未达 {minSavingsPercent:0.#}% 门槛)";
    }

    /// <summary>【任务 X3】统一格式的"阶段成本"短句:耗时 + 帧数 + 毫秒每帧(用户点名的最高价值项:
    /// "以后能一眼判断是素材变大还是软件变慢")。
    /// 帧数或耗时任一拿不到 → 写"未采集",**不编数字**(与"本次统计"的既有诚实口径一致)。</summary>
    public static string StageCostShort(string stage, double seconds, long frames)
        => frames > 0 && seconds > 0
            ? $"{stage} {seconds:0.#}s({frames} 帧,{seconds * 1000.0 / frames:0} ms/帧)"
            : $"{stage} 未采集";

    /// <summary>把一个阶段"没开"与"开了但没跑"分开写(两者含义不同,不能混成一句"未采集")。
    /// <paramref name="enabled"/> = 用户选项是否开着;<paramref name="ran"/> = 本次是否真的走到了那个阶段
    /// (例如 1x 不超分:选项开着但跳过)。</summary>
    public static string StageCostOrSkipped(string stage, bool enabled, bool ran, double seconds, long frames)
        => !enabled ? $"{stage} 未开"
            : !ran ? $"{stage} 未跑"
            : StageCostShort(stage, seconds, frames);
}
