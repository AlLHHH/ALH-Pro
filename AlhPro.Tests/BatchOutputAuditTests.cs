using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>
/// 超分批"引擎少写帧"对账(AlhPro.Core.BatchOutputAudit)的单测。
/// 【承重场景】引擎退出码 0 却少产出若干帧 ⇒ 旧代码(只判"输出目录一个文件都没有")静默放行 ⇒
/// 合帧按 `frame_%06d` 连续读时少帧/报错,日志里无痕迹。真机实测(2026-10-15,RTX 4060 Laptop,
/// 老版 2022 realesrgan-ncnn-vulkan 目录模式 `-t 384`)就是"同批里好帧与黑帧混排、退出码 0"。
/// 这里钉的是**匹配口径**(主名、剥多层扩展名、大小写、非图片产出不算数),像素判黑不在本类职责内。
/// </summary>
public class BatchOutputAuditTests
{
    [Fact]
    public void Same_stem_with_a_different_extension_is_not_missing()
    {
        // 视频超分走 `-f jpg`:输入 frame_000001.png → 产出 frame_000001.jpg(换扩展名不算缺帧)
        var missing = BatchOutputAudit.MissingOutputs(
            new[] { "frame_000001.png", "frame_000002.png" },
            new[] { "frame_000001.jpg", "frame_000002.jpg" });
        Assert.Empty(missing);
    }

    [Fact]
    public void The_alpha_input_naming_quirk_still_matches()
    {
        // 带 alpha 的 PNG 输入 + -f jpg:引擎把输出名改成 "xxx.jpg.png"(见 EngineService.EnsureFinalOutput),
        // 宿主侧 Path.ChangeExtension 还可能得到 "xxx.jpg.jpg" —— 两种都必须算"有产出",否则会把好帧当缺帧。
        var missing = BatchOutputAudit.MissingOutputs(
            new[] { "frame_000001.png", "frame_000002.png" },
            new[] { "frame_000001.jpg.png", "frame_000002.jpg.jpg" });
        Assert.Empty(missing);
    }

    [Fact]
    public void Only_the_absent_frame_is_reported()
    {
        var missing = BatchOutputAudit.MissingOutputs(
            new[] { "frame_000001.png", "frame_000002.png", "frame_000003.png" },
            new[] { "frame_000001.jpg", "frame_000003.jpg" });
        Assert.Equal(new[] { "frame_000002.png" }, missing);
    }

    [Fact]
    public void An_empty_output_directory_reports_every_input()
    {
        var missing = BatchOutputAudit.MissingOutputs(
            new[] { "frame_000001.png", "frame_000002.png" },
            System.Array.Empty<string>());
        Assert.Equal(2, missing.Count);
    }

    [Fact]
    public void A_non_image_output_does_not_satisfy_a_frame()
    {
        // 引擎/宿主在同一目录里顺手写的日志、说明文件不能顶替产出帧(否则真正的缺帧会被掩盖)
        var missing = BatchOutputAudit.MissingOutputs(
            new[] { "frame_000001.png" },
            new[] { "frame_000001.txt", "log", "run.log" });
        Assert.Equal(new[] { "frame_000001.png" }, missing);
    }

    [Fact]
    public void Matching_ignores_case_and_directories()
    {
        var missing = BatchOutputAudit.MissingOutputs(
            new[] { @"D:\tmp\up_in_0\FRAME_000001.PNG" },
            new[] { @"D:\tmp\up_out_0\frame_000001.JPG" });
        Assert.Empty(missing);
    }

    [Fact]
    public void Stem_strips_every_known_extension_but_keeps_inner_dots()
    {
        Assert.Equal("frame_000001", BatchOutputAudit.FrameStem("frame_000001.jpg.png"));
        Assert.Equal("frame_000001", BatchOutputAudit.FrameStem(@"C:\a\frame_000001.png"));
        Assert.Equal("a.b.c", BatchOutputAudit.FrameStem("a.b.c.png"));     // 只剥末尾的图片扩展名,不动主名里的点
        Assert.Equal("readme.txt", BatchOutputAudit.FrameStem("readme.txt"));   // 非图片扩展名不剥(原样)
        Assert.Equal("", BatchOutputAudit.FrameStem(".png"));               // 只剩扩展名 ⇒ 空(调用方跳过)
    }
}
