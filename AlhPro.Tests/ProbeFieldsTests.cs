using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>ffprobe「带标签」字段解析契约(2026-09-16 真机修)。
///
/// 【为什么值得单测】这条 bug 的形态是"静默 + 只在真机出现":
///   · 代码编译通过、单测原先也测不到(要真调 ffprobe);
///   · 症状是 `ProbeTrueFramesAndDuration` **永远返回 (0,0)**,而调用方只看返回值 ⇒
///     "真实时长"恒为 0("指定帧率口径:… (真实时长 0s)"就是这么来的),指定帧率的帧率口径整条失效;
///   · 同一缺陷还让 `ValidateVideoFileAsync` 的帧数下限检查形同虚设(它按 csv 第 2 字段取帧数)。
/// 根因是 ffprobe 对同一命令**把整行打印两遍**(真机 stdout 实测 `"24000/1001,518\r\n24000/1001,518\r\n"`),
/// 而 `Trim().Split(',')` 只去首尾空白 ⇒ 第 2 个字段里混进 `\r\n` 和下一行内容。
/// 因此下面每个用例都覆盖一种真机形态,而不是只测"理想输入"。</summary>
public class ProbeFieldsTests
{
    /// <summary>真机原样:同一行被 ffprobe 打印两遍(csv 形态)。带标签解析必须照样取值。</summary>
    [Fact]
    public void Handles_the_duplicated_line_ffprobe_actually_prints()
    {
        const string duplicated = "nb_read_frames=518\r\nnb_read_frames=518\r\n";
        Assert.Equal(518, ProbeFields.LongField(duplicated, "nb_read_frames"));

        var (frames, dur) = ProbeFields.FramesAndContentDuration(
            "nb_read_frames=518\r\navg_frame_rate=24000/1001\r\n" +
            "nb_read_frames=518\r\navg_frame_rate=24000/1001\r\n");
        Assert.Equal(518, frames);
        // 内容时长 = (518-1) ÷ 23.976023976 = 21.563208…(不是 518÷23.976 = 21.605 —— 第 518 帧没有时长)
        Assert.Equal(21.563, dur, 3);
    }

    /// <summary>分数帧率必须按原口径解析(`24000/1001` ≠ 24000)。</summary>
    [Fact]
    public void Parses_fractional_frame_rates()
    {
        Assert.Equal(23.976023976, ProbeFields.FpsField("avg_frame_rate=24000/1001", "avg_frame_rate")!.Value, 6);
        Assert.Equal(60.0, ProbeFields.FpsField("avg_frame_rate=60/1", "avg_frame_rate")!.Value, 6);
        // 小数写法也要认(有些封装/老版本 ffprobe 直接给 23.976)
        Assert.Equal(29.97, ProbeFields.FpsField("avg_frame_rate=29.97", "avg_frame_rate")!.Value, 6);
    }

    /// <summary>`N/A` / `unknown` / 空值行必须被跳过(不能把整次探测判死),并继续看后面的有效行。</summary>
    [Fact]
    public void Skips_placeholder_values_and_keeps_looking()
    {
        // MKV/流式封装常见:nb_frames=N/A —— 但 nb_read_frames 是有效的
        const string mkv = "codec_type=video\r\nnb_frames=N/A\r\nnb_read_frames=1200\r\n";
        Assert.Null(ProbeFields.LongField(mkv, "nb_frames"));
        Assert.Equal(1200, ProbeFields.LongField(mkv, "nb_read_frames"));
        Assert.Equal("video", ProbeFields.RawField(mkv, "codec_type"));
        // 空值行(键后面什么都没有)同样跳过
        Assert.Equal(7, ProbeFields.LongField("nb_frames=\r\nnb_frames=7\r\n", "nb_frames"));
        Assert.Null(ProbeFields.LongField("nb_frames=unknown\r\n", "nb_frames"));
    }

    /// <summary>取不到 / 非法输入一律给 (0,0) 或 null(与老口径一致:调用方按"探测失败"走兜底,不抛)。</summary>
    [Fact]
    public void Fails_softly_on_missing_or_garbage_input()
    {
        Assert.Equal((0L, 0.0), ProbeFields.FramesAndContentDuration(""));
        Assert.Equal((0L, 0.0), ProbeFields.FramesAndContentDuration(null!));
        Assert.Equal((0L, 0.0), ProbeFields.FramesAndContentDuration("nb_read_frames=518\r\n"));   // 缺帧率
        Assert.Equal((0L, 0.0), ProbeFields.FramesAndContentDuration("avg_frame_rate=24000/1001\r\n"));  // 缺帧数
        Assert.Null(ProbeFields.LongField("随便一行没有等号", "nb_read_frames"));
        // 单帧视频:除以帧率也不能崩(帧数=1 时按 1 个帧间隔算)
        Assert.Equal(1L, ProbeFields.LongField("nb_read_frames=1", "nb_read_frames"));
    }
}
