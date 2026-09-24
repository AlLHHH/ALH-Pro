using System;
using System.Collections.Generic;

namespace AlhPro.Core;

/// <summary>Real-CUGAN 的**唯一真相**:CLI 语义、模型标签、引擎/权重文件名(纯逻辑,可单测)。
///
/// 【为什么必须单独抽一层 · 实测踩过】Real-CUGAN 的 CLI 与 Real-ESRGAN **不兼容**,
/// 照抄 realesrgan 的参数拼法一定会错(要么引擎报错、要么静默按错档跑):
///   · Real-ESRGAN:`-n &lt;模型名&gt;`、`-m &lt;模型目录&gt;`
///   · Real-CUGAN :**`-n` 是降噪档**(-1/0/1/2/3)、**`-m` 才是模型目录**(models-se / models-pro / models-nose)
///     —— 实测 `realcugan-ncnn-vulkan.exe -h` 的输出,见 t2 文档 §2.1。
/// 本工程的超分调用点原先把 `-n` 当模型名传(EngineService 的单图/单块/目录批量三处),
/// 所以 Real-CUGAN 必须有**自己的 args 构造**,不许复用 Real-ESRGAN 那套。
///
/// 【模型标签口径】界面下拉的 Tag 形如 `models-se:-1`(= `&lt;权重目录&gt;:&lt;降噪档&gt;`),
/// 由 <see cref="ModelDir"/> / <see cref="Noise"/> 解析。Tag 里同时带出这两维,是因为
/// Real-CUGAN 的"选哪支模型"本来就是 (目录 × 降噪档) 两个自由度的组合,而不是一个模型名。
///
/// 【只随包 models-se】models-pro / models-nose 在本仓库**不进安装包**(少 13 MB 且没有额外能力);
/// 因此 <see cref="Tags"/> 里只登记 models-se 的档位,别在界面下拉里放 pro/nose ——
/// 引擎找不到权重时**不报错、只画一张坏帧、exit=0**(本仓库为此专门有过两次事故记录)。</summary>
public static class RealCugan
{
    /// <summary>引擎名(与 realesrgan / waifu2x 同级,进引擎单选、探测缓存键、日志)。</summary>
    public const string EngineName = "realcugan";

    /// <summary>官方引擎文件名(上游 20220728 包里的名字)。</summary>
    public const string OfficialExeName = "realcugan-ncnn-vulkan.exe";

    /// <summary>本仓库用 2025/2026 版 ncnn 重编后的引擎文件名。
    /// 【为什么必须带 2026 后缀】官方 20220728 那份 exe 实测 `VK_EXT_robustness2` **0 处**
    /// (与 Real-ESRGAN 官方 2022 版逐项同类)⇒ 在 NVIDIA 50 系上属「退出码 0 + 坏帧」那一类;
    /// 后缀同时是 <c>EngineService.EngineIsRebuilt2026</c> 的判据,决定"要不要按显卡型号预判风险"。</summary>
    public const string Rebuilt2026ExeName = "realcugan-ncnn-vulkan-2026.exe";

    /// <summary>随包的权重目录(相对 engines\realcugan)。</summary>
    public const string ShippedModelDir = "models-se";

    /// <summary>界面下拉的第一项(默认):保守档 = 官方 `-n -1`,保留纹理、不打磨。</summary>
    public const string DefaultTag = "models-se:-1";

    /// <summary>界面可选的三个档位。**都是 models-se**,且都同时具备 up2x / up3x / up4x 权重
    /// (实测逐个数过 models-se 目录里的文件:conservative / no-denoise / denoise3x 各带 2x/3x/4x;
    ///  denoise1x / denoise2x 只有 up2x ⇒ **不进下拉**,否则 3x/4x 目标会加载不存在的权重、静默出坏帧)。</summary>
    public static readonly (string Tag, string Label, string Hint)[] Tags =
    {
        ("models-se:-1", "动漫 · 保守（保留纹理 · 中）",
            "Real-CUGAN 官方保守档(-n -1 = conservative):尽量保留原纹理、不做强打磨,动漫线条最干净。"),
        ("models-se:0", "动漫 · 不降噪（快）",
            "Real-CUGAN 无降噪档(-n 0):输入已经干净时用它,速度与保守档同档。"),
        ("models-se:3", "动漫 · 强降噪（慢）",
            "Real-CUGAN 最强降噪档(-n 3):老片源噪点多、压缩块明显时用它;会一并抹掉部分纹理。"),
    };

    /// <summary>引擎在哪条路线上能跑:Real-CUGAN **只有 ncnn 权重**(engines\ 下没有任何 realcugan 的 .onnx)
    /// ⇒ 探测失败时没有 ONNX 兜底,只能按 CPU 跑或如实拒绝。四类机器上的口径见
    /// <c>docs\Real-CUGAN-四类机器口径.md</c>。</summary>
    public const bool HasOnnxFallback = false;

    /// <summary>这个标签是不是 Real-CUGAN 的模型标签(`&lt;目录&gt;:&lt;降噪档&gt;`)。</summary>
    public static bool IsRealCuganTag(string? tag)
        => !string.IsNullOrWhiteSpace(tag) && tag.IndexOf(':') > 0;

    /// <summary>解析权重目录(冒号前)。非法标签一律回落到随包目录 —— 绝不返回空串
    /// (空串会让引擎去找 `\up2x-*.param`,找不到就画坏帧、exit=0)。</summary>
    public static string ModelDir(string? tag)
    {
        if (!IsRealCuganTag(tag)) return ShippedModelDir;
        string dir = tag!.Substring(0, tag.IndexOf(':')).Trim();
        return dir.Length > 0 ? dir : ShippedModelDir;
    }

    /// <summary>解析降噪档(冒号后,`-n`)。解析不出来就用官方默认档 -1。</summary>
    public static int Noise(string? tag)
    {
        if (!IsRealCuganTag(tag)) return -1;
        string tail = tag!.Substring(tag.IndexOf(':') + 1).Trim();
        return int.TryParse(tail, out var n) ? n : -1;
    }

    /// <summary>给界面/日志用的短名(与下拉显示名同口径)。</summary>
    public static string Label(string? tag)
    {
        foreach (var t in Tags)
            if (string.Equals(t.Tag, tag, StringComparison.OrdinalIgnoreCase)) return t.Label;
        return IsRealCuganTag(tag) ? $"Real-CUGAN({ModelDir(tag)} / -n {Noise(tag)})" : "Real-CUGAN";
    }

    /// <summary>日志/摘要用:Real-CUGAN 的 Tag → 人话名;**不是** Real-CUGAN 的 Tag 就原样返回
    /// (调用点的 `model` 可能是 realesrgan 的模型名或 waifu2x 的目录名,不能一概套 Real-CUGAN 的名字)。</summary>
    public static string DisplayOrSelf(string? tag)
        => IsSupportedTag(tag) ? Label(tag) : (tag ?? "");

    /// <summary>标签是不是受支持的档位(不在表里的标签不许下发给引擎)。</summary>
    public static bool IsSupportedTag(string? tag)
    {
        foreach (var t in Tags)
            if (string.Equals(t.Tag, tag, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>该档位有哪些原生 -s 权重可用(实测逐文件数过 models-se)。
    /// 目标倍数不在表里时由调用方"按原生倍数跑 + 缩回"(与 x4plus 系同一手法)。</summary>
    public static IReadOnlyList<int> NativeScales(string? tag) => new[] { 2, 3, 4 };

    /// <summary>【F4 兜底 · 2026-09-24】单支档位被"瞬时误判"时,按顺序给出**其它可以改用的档位**
    /// (返回里不含当前这支;三档权重都在包里,换档不需要用户做任何事)。
    ///
    /// 【为什么要有它】Real-CUGAN 一跑就要记一条「引擎|GPU|模型」的探测结论;若某次探测恰好撞上
    /// 显存瞬时紧张 / 驱动抽风 ⇒ 单支失败 ⇒ 旧代码直接**整批拒绝**。而这三档共用同一个引擎、
    /// 同一份 network,只是降噪权重不同 ⇒ 单支探测失败**不能**推出「整条引擎不可用」。
    /// 顺序按「改动观感最小」排:先不降噪(0)、再强降噪(3)、最后保守(-1)。
    /// 调用方应对每个候选**实测一次**,通过就用(见 VideoService 的 Real-CUGAN 分支)。</summary>
    public static IReadOnlyList<string> AlternativeTags(string? current)
    {
        var order = new[] { "models-se:0", "models-se:3", "models-se:-1" };
        var list = new List<string>(order.Length);
        foreach (var t in order)
        {
            if (string.Equals(t, current, StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsSupportedTag(t)) continue;      // 表里的档位才是随包、且具备 2x/3x/4x 权重的
            list.Add(t);
        }
        return list;
    }
}
