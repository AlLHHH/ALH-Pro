using System.Globalization;

namespace AlhPro.Core;

/// <summary>「输入帧率」框该显示谁的帧率(纯逻辑,可单测)。
/// 【任务 P · 用户报障】「输入帧率」框在选中/激活视频后依然为空。定位结论(读代码):
/// 视频列表是 `SelectionMode="Multiple"`(ViewVideo.xaml),**点一个"已经选中"的项在 WinUI 多选模式下是
/// 【取消选中】**;而选择变化回调在"没选中任何项"时按设计把框清空(`SyncInputFpsAsync(null)` → 空串)。
/// 处理完成后那个项仍是选中态(变灰但没被取消选中)→ 用户"点一下激活"实际是"取消选中"→ 框被清空 ✓ 复现报障。
/// 另一个缺口:回填只发生在"选中变化"和"入列时待处理视频恰好 1 个",**已完成的项没有任何回填路径**。
/// 现在的口径:**把"用户刚点过的那个视频"记下来当显示来源** —— 即使这一点把选中取消了,框里仍显示"它"的帧率;
/// 只有"从来没点过任何视频"(列表刚清空/删完)才允许为空(空 = 处理时按该视频自动探测)。
/// 【与"残留防线"的关系】**只放开显示,不放宽归属判定**:框的来源(owner)照旧记录,
/// 处理时仍要求 owner 必须是本次要处理的第一个视频,否则忽略并记日志(见 VideoView.RunBtn_Click 的残留防线)。</summary>
public static class InputFpsSyncPolicy
{
    /// <summary>框里这个值的来源。</summary>
    public enum Source
    {
        /// <summary>没有对应视频 → 清空(空 = 处理时自动探测)。</summary>
        None,
        /// <summary>用当前选中项(多选时取最后点击的那一项)。</summary>
        Selection,
        /// <summary>用"刚被点击过的那一项"——点击把选中取消了也要显示它。</summary>
        LastClicked,
    }

    /// <param name="selectionCount">当前选中项数(0 = 没选中;多选 ≥1 时取最后点击项)。</param>
    /// <param name="hasLastClicked">是否记得"刚被点击过的那个视频"(列表清空/删掉该项后必须清掉这个记忆)。</param>
    public static Source Decide(int selectionCount, bool hasLastClicked)
    {
        if (selectionCount > 0) return Source.Selection;
        return hasLastClicked ? Source.LastClicked : Source.None;
    }
}

/// <summary>ffprobe 帧率字段的解析(纯逻辑,可单测)。
/// 【为什么单独抽出来】旧实现直接对 `-show_entries stream=avg_frame_rate` 的输出跑 `(\d+)/(\d+)`:
/// 正则一命中就返回 `num/den` 相除;**`den == 0` 时却落到 `return o`,把原始串(如 `0/0`)写进了输入框**
/// —— VFR 素材(录屏/手机视频)的 `avg_frame_rate` 实测就可能是 `0/0`,框里于是显示 `0/0` 这种非数字。
/// 现在的口径:逐个候选值解析,**只有"分母 > 0 且分子 > 0"才采用**;
/// `avg_frame_rate` 无效(`0/0`/`N/A`)时自动看下一个候选(调用方按 `avg_frame_rate,r_frame_rate` 取值顺序),
/// 全都无效 → 返回 null,调用方留空(= 处理时按该视频自动探测,语义正确)。</summary>
public static class FfprobeFps
{
    /// <summary>把 ffprobe 的帧率输出解析成"可显示、可被 double.TryParse"的数字文本;拿不到有效值返回 null。
    /// 接受:多行 / 逗号分隔(ffprobe `-of csv=p=0` 多字段就输出逗号分隔)/ 单值;
    /// 形态:`30/1`、`30000/1001`、`29.97`、`0/0`(跳过)、`N/A`(跳过)、空(返回 null)。</summary>
    public static string? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        foreach (var token in raw!.Split(new[] { ',', '\n', '\r', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int slash = token.IndexOf('/');
            if (slash > 0)
            {
                string a = token[..slash], b = token[(slash + 1)..];
                if (long.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out var num)
                    && long.TryParse(b, NumberStyles.Integer, CultureInfo.InvariantCulture, out var den)
                    && num > 0 && den > 0)
                    return (num / (double)den).ToString("0.##", CultureInfo.InvariantCulture);
                continue;   // 0/0 之类:本候选无效 → 继续看下一个(如 r_frame_rate)
            }
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                && d > 0 && double.IsFinite(d))
                return d.ToString("0.##", CultureInfo.InvariantCulture);
        }
        return null;
    }
}
