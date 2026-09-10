using System;
using System.Collections.Generic;
using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>
/// 带状黑帧判定(FrameInspect.IsBandBlack / IsDefectiveFrame)的单测 —— 本轮"坏帧静默进成片"修复的承重测试。
/// 【为什么需要它】实测(RTX 4060 Laptop / waifu2x-ncnn-vulkan 20250915 + models-cunet,2x,1080×1920 目录批,
/// 6 次里 5 次):ncnn-vulkan 的 vkQueueSubmit 失败会输出【每帧下 2/3 全黑、上 1/3 正常】的坏帧 —— 退出码 0、
/// 引擎不报错、ffmpeg blackdetect 也不报。它只黑约 66% 的像素,所以旧的整帧量词判据(≥95% 近黑)判它"正常",
/// 坏帧于是静默进成片、零日志、ncnnUnreliable 不置位。本文件用与生产完全相同的采样几何(SampleGrid)钉住新判据。
/// 注意:这里不修改 FrameInspectTests.cs 的既有用例 —— 整帧近黑的老语义必须保持原样。
/// </summary>
public class FrameInspectBandBlackTests
{
    private const int Dark = 9;     // RGB 和 9 < FrameInspect.DarkRgbSum(24) → 暗像素
    private const int Bright = 300; // 正常像素

    /// <summary>按生产采样几何(行主序、共 rows*cols 个点)构造一帧的采样值。</summary>
    private static int[] BuildSamples(int width, int height, Func<int, int, int> valueAt)
    {
        FrameInspect.SampleGrid(width, height, out int rows, out int cols);
        var sums = new int[rows * cols];
        int i = 0;
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < cols; x++)
                sums[i++] = valueAt(x, y);
        return sums;
    }

    // ---------- ① 整帧全黑 → 缺陷 ----------

    [Fact]
    public void Whole_frame_black_is_defective()
    {
        const int w = 1080, h = 1920;
        FrameInspect.SampleGrid(w, h, out int rows, out int cols);
        var sums = BuildSamples(w, h, (_, _) => Dark);

        Assert.True(FrameInspect.IsNearBlack(sums, sums.Length));                 // 老判据也认(语义没被削弱)
        Assert.True(FrameInspect.IsBandBlack(sums, rows, cols));                 // 新判据也认
        Assert.True(FrameInspect.IsDefectiveFrame(sums, sums.Length, w, h));
    }

    // ---------- ② 下 2/3 全黑、上 1/3 正常 → 缺陷【本次要修的形态】 ----------

    [Fact]
    public void Bottom_two_thirds_black_is_defective_even_though_whole_frame_is_not_near_black()
    {
        const int w = 1080, h = 1920;
        FrameInspect.SampleGrid(w, h, out int rows, out int cols);
        int topThirdRows = rows / 3;
        var sums = BuildSamples(w, h, (_, y) => y < topThirdRows ? Bright : Dark);

        // 【承重断言】旧口径(整帧量词)确实抓不到它:只黑约 2/3 像素,达不到 95%
        // —— 这正是"坏帧静默进成片"的根因,也是本修复存在的理由
        Assert.False(FrameInspect.IsNearBlack(sums, sums.Length));

        // 新口径(条带)必须抓到:中/下两条 1/3 条带 100% 全黑
        Assert.True(FrameInspect.IsBandBlack(sums, rows, cols));
        Assert.True(FrameInspect.IsDefectiveFrame(sums, sums.Length, w, h));
    }

    // ---------- ③ 全正常 → 不缺陷 ----------

    [Fact]
    public void Ordinary_frame_is_not_defective()
    {
        const int w = 1080, h = 1920;
        FrameInspect.SampleGrid(w, h, out int rows, out int cols);
        var sums = BuildSamples(w, h, (x, y) => Bright + (x * 3 + y * 7) % 40);   // 有细节的正常画面

        Assert.False(FrameInspect.IsNearBlack(sums, sums.Length));
        Assert.False(FrameInspect.IsBandBlack(sums, rows, cols));
        Assert.False(FrameInspect.IsDefectiveFrame(sums, sums.Length, w, h));
    }

    // ---------- ④ 正常暗场(整体偏暗但非全黑)→ 不缺陷【不能误杀】 ----------

    [Fact]
    public void Dark_but_legit_scene_is_not_defective()
    {
        // 夜景/片头淡入淡出:整帧偏暗(亮像素也只有 RGB 和 45,远低于正常画面),
        // 且约 10% 的像素确实低于暗阈值 —— 但没有任何 1/3 条带达到"≥95% 像素 < 24",
        // 所以既不能被整帧判据误杀,也不能被新的条带判据误杀。
        const int w = 1080, h = 1920;
        FrameInspect.SampleGrid(w, h, out int rows, out int cols);
        var sums = BuildSamples(w, h, (x, y) => (x * 31 + y * 17) % 10 == 0 ? 12 : 45);

        Assert.False(FrameInspect.IsNearBlack(sums, sums.Length));
        Assert.False(FrameInspect.IsBandBlack(sums, rows, cols));
        Assert.False(FrameInspect.IsDefectiveFrame(sums, sums.Length, w, h));
    }

    [Fact]
    public void Letterbox_bars_are_not_defective()
    {
        // 常见宽银幕黑边(1920×1080 帧里放 2.39:1,上下各约 12.8% 高)不填满任何一条 1/3 条带
        // —— 这是"三等分"这个切法能成立的边界保障:要被判缺陷,画面里得有连续 ≥1/3 高的纯黑区。
        const int w = 1920, h = 1080;
        FrameInspect.SampleGrid(w, h, out int rows, out int cols);
        int barRows = (int)Math.Round(rows * 0.128);
        var sums = BuildSamples(w, h, (_, y) => (y < barRows || y >= rows - barRows) ? Dark : Bright);

        Assert.False(FrameInspect.IsNearBlack(sums, sums.Length));
        Assert.False(FrameInspect.IsBandBlack(sums, rows, cols));
        Assert.False(FrameInspect.IsDefectiveFrame(sums, sums.Length, w, h));
    }

    // ---------- 条带判定是"任一条带",不是"只有下条带" ----------

    [Fact]
    public void Top_third_black_alone_is_defective()
    {
        const int w = 1080, h = 1920;
        FrameInspect.SampleGrid(w, h, out int rows, out int cols);
        var sums = BuildSamples(w, h, (_, y) => y < rows / 3 ? Dark : Bright);

        Assert.False(FrameInspect.IsNearBlack(sums, sums.Length));   // 只黑 1/3,整帧口径同样抓不到
        Assert.True(FrameInspect.IsDefectiveFrame(sums, sums.Length, w, h));
    }

    // ---------- 条带阈值仍是 95%,不为"只看 1/3 像素"而放宽 ----------

    [Fact]
    public void Band_threshold_is_still_95_percent()
    {
        const int w = 1080, h = 1920;
        FrameInspect.SampleGrid(w, h, out int rows, out int cols);
        int y0 = rows / 3, y1 = 2 * rows / 3;      // 中条带
        int bandRows = y1 - y0;

        // 中条带里 90% 的行全黑 → 该条带暗像素比例约 89.5% < 95%:不判缺陷(避免误杀)
        int darkRows90 = bandRows * 90 / 100;
        var loose = BuildSamples(w, h, (_, y) => y >= y0 && y < y0 + darkRows90 ? Dark : Bright);
        Assert.False(FrameInspect.IsBandBlack(loose, rows, cols));

        // 提到 ≥95% 就要判缺陷
        int darkRows95 = (int)Math.Ceiling(bandRows * 0.95);
        var tight = BuildSamples(w, h, (_, y) => y >= y0 && y < y0 + darkRows95 ? Dark : Bright);
        Assert.True(FrameInspect.IsBandBlack(tight, rows, cols));
    }

    // ---------- 采样几何与数据不符 / 空数据:一律不判缺陷(拿不准不误判) ----------

    [Fact]
    public void Mismatched_or_empty_sample_data_is_not_defective()
    {
        Assert.False(FrameInspect.IsBandBlack(new[] { Dark, Dark, Dark }, 10, 10));   // 数据短于 rows*cols
        Assert.False(FrameInspect.IsBandBlack(System.Array.Empty<int>(), 0, 0));
        Assert.False(FrameInspect.IsBandBlack(null!, 5, 5));
        // 尺寸非法 → 几何为 0×0 → 条带判据不成立;整帧判据按原语义照常(这里给非暗像素)
        Assert.False(FrameInspect.IsDefectiveFrame(new[] { Bright }, 1, 0, 0));
    }

    // ---------- 承重地基:SampleGrid 必须与 ForEachSample 的遍历严格一致 ----------

    [Fact]
    public void Sample_grid_matches_for_each_sample_traversal()
    {
        foreach (var (w, h) in new[]
                 {
                     (1080, 1920), (1920, 1080), (1, 1), (10, 10), (100, 100), (7, 5000), (4000, 3),
                 })
        {
            FrameInspect.SampleGrid(w, h, out int rows, out int cols);
            var pts = new List<(int x, int y)>();
            int total = FrameInspect.ForEachSample(w, h, (x, y) => pts.Add((x, y)));

            Assert.Equal(pts.Count, total);
            Assert.Equal(rows * cols, total);          // IsBandBlack 的 sumRgb.Length == rows*cols 前提
            Assert.True(rows >= 0 && cols >= 0);
            if (total == 0) continue;

            // 行主序:同一行内 y 相同、x 递增 —— IsBandBlack 正是靠 "点序号 / cols = 第几行" 还原条带
            for (int i = 0; i < pts.Count; i++)
            {
                Assert.Equal(pts[(i / cols) * cols].y, pts[i].y);
                if (i % cols != 0) Assert.True(pts[i].x > pts[i - 1].x);
            }
        }
    }

    [Fact]
    public void Sample_grid_geometry_for_1080x1920_is_pinned()
    {
        // 生产最常走的尺寸:step = 1080/32 = 33 → 58 行 × 32 列(共 1856 个采样点)
        FrameInspect.SampleGrid(1080, 1920, out int rows, out int cols);
        Assert.Equal(58, rows);
        Assert.Equal(32, cols);
        Assert.Equal(1856, FrameInspect.ForEachSample(1080, 1920, (_, _) => { }));
    }
}
