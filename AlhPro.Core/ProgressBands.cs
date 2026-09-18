namespace AlhPro.Core;

/// <summary>整体进度条各阶段的百分比区间(唯一一份判据)。
///
/// 【为什么必须有这个类】同一套区间原先硬编码在 **四** 个地方,各自维护、谁也约束不了谁:
///   · `VideoService.StageProgressPct`(帧号 → 百分比)
///   · `VideoService` 里各阶段显式 Report 的 base/end(超分 upBase/upEnd、补帧 interpPctBase/Span…)
///   · `EngineService` 逐帧轮询里的 `45 + done*45/gt`
///   · `VideoView` 的 `etaRegex` 平滑映射与 `StageSpanOf`(算"当前阶段还剩")
/// 于是"阶段顺序改了/加了新阶段"时,总会漏掉一两处 —— 症状就是用户看到的**进度条与真实剩余量对不上**
/// (补帧一结束条就冲到一半、后处理那一大段没有自己的区间)。
///
/// 【这些数从哪来 · 必须可追溯,不许凭感觉调】取用户真机一次完整导出(2026-09-16 18:24,
/// 源 1440×1440 / 519 帧,补帧 3x,超分 2x,RTX 4060 Laptop)的实测每帧成本与阶段耗时:
///   准备(拆帧+去重) 1.7s(0%) · 补帧 83.6s(12%) · 超分 369.1s(54%)
///   · 后处理(边缘抗锯齿等) 106.1s(15%) · 编码/封装 127.3s(18%)  合计 687.8s
/// 按占比分配 100 个点 ⇒ “条走多少 ≈ 活干完多少”,剩余时间才不会骗人。
/// 【另一台机器比例会变】超分/后处理/编码三者的比例随分辨率与编码器档次变化;
/// 这里取的是"超分是瓶颈"这一典型档(用户实际素材)。若将来要在别档也精确,
/// 应改成按本次任务的实测单价动态算 —— 但那是更大的改动,当前先用固定表把"后处理没有区间"这个
/// 明显错位修掉(改动面最小、可回退)。</summary>
public static class ProgressBands
{
    /// <summary>阶段键(与 UI/日志里的阶段名一一对应;`StageKeyOf` 负责把中文阶段名映射过来)。</summary>
    public enum Stage
    {
        /// <summary>拆帧 + 去重(含智能检测/内容帧率采样)。</summary>
        Prepare,
        /// <summary>补帧(RIFE;含按源时间轴插帧那条路 —— 它与补帧同一个区间)。</summary>
        Interp,
        /// <summary>超分(Real-ESRGAN / waifu2x,含 1x 缩回)。</summary>
        Upscale,
        /// <summary>后处理与合帧准备(锐化/清晰/钝化/边缘抗锯齿/降噪/缩放/帧整理)。</summary>
        Post,
        /// <summary>编码 + 音频 + 封装 + 输出校验。</summary>
        Encode,
    }

    /// <summary>准备段终点(= 补帧起点)。</summary>
    public const double PrepareEnd = 4;
    /// <summary>补帧段终点(= 超分起点)。</summary>
    public const double InterpEnd = 19;
    /// <summary>超分段终点(= 后处理起点)。</summary>
    public const double UpscaleEnd = 66;
    /// <summary>后处理段终点(= 编码起点)。</summary>
    public const double PostEnd = 84;
    /// <summary>编码段终点(整个任务完成)。</summary>
    public const double EncodeEnd = 100;

    /// <summary>取某阶段的 (起点, 终点)。调用方一律用这个,不要再写数字。</summary>
    public static (double lo, double hi) Of(Stage s) => s switch
    {
        Stage.Prepare => (0, PrepareEnd),
        Stage.Interp => (PrepareEnd, InterpEnd),
        Stage.Upscale => (InterpEnd, UpscaleEnd),
        Stage.Post => (UpscaleEnd, PostEnd),
        Stage.Encode => (PostEnd, EncodeEnd),
        _ => (0, 0),
    };

    /// <summary>把"已处理 fr / 共 total"映射成该阶段内的整数百分比(取整后钳在区间内)。</summary>
    public static int Within(Stage s, long fr, long total)
    {
        var (lo, hi) = Of(s);
        if (total <= 0) return (int)lo;
        double r = Math.Clamp((double)fr / total, 0, 1);
        return (int)Math.Clamp(Math.Round(lo + (hi - lo) * r), lo, hi);
    }

    /// <summary>按阶段名(中文,取自进度消息里的阶段词)取区间;认不出 → (0,0) = 不参与进度换算。
    /// 【顺序敏感】先判"补帧/插帧",再判"超分":阶段名里出现过两个词时以更具体的为准(与历史实现一致)。</summary>
    public static (double lo, double hi) OfStageName(string? stageName)
    {
        if (string.IsNullOrEmpty(stageName)) return (0, 0);
        if (stageName.Contains("编码", StringComparison.Ordinal)) return Of(Stage.Encode);
        if (stageName.Contains("拆帧", StringComparison.Ordinal)) return Of(Stage.Prepare);
        if (stageName.Contains("插帧", StringComparison.Ordinal)
            || stageName.Contains("补帧", StringComparison.Ordinal)) return Of(Stage.Interp);
        if (stageName.Contains("超分", StringComparison.Ordinal)) return Of(Stage.Upscale);
        if (stageName.Contains("后处理", StringComparison.Ordinal)) return Of(Stage.Post);
        return (0, 0);
    }

    /// <summary>把阶段名转成 UI 用的阶段键(与 VideoView.EtaStageKey 的口径一致)。
    /// 返回 "" = 认不出(调用方不给阶段内下限推算)。</summary>
    public static string KeyOfStageName(string? stageName)
    {
        if (string.IsNullOrEmpty(stageName)) return "";
        if (stageName.Contains("编码", StringComparison.Ordinal)) return "enc";
        if (stageName.Contains("拆帧", StringComparison.Ordinal)) return "split";
        if (stageName.Contains("插帧", StringComparison.Ordinal)
            || stageName.Contains("补帧", StringComparison.Ordinal)) return "interp";
        if (stageName.Contains("超分", StringComparison.Ordinal)) return "up";
        if (stageName.Contains("后处理", StringComparison.Ordinal)) return "post";
        return "";
    }

    /// <summary>阶段键 → 区间(给 UI 用)。与 <see cref="OfStageName"/> 同源。</summary>
    public static (double lo, double hi) OfKey(string? key) => key switch
    {
        "split" => Of(Stage.Prepare),
        "interp" => Of(Stage.Interp),
        "up" => Of(Stage.Upscale),
        "post" => Of(Stage.Post),
        "enc" => Of(Stage.Encode),
        _ => (0, 0),
    };
}
