using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>「阶段顺序」判据与 ETA 的一致性(任务 H)。
/// 【为什么要有这些测试】"1x/2x 走「超分 → 补帧」新顺序"这句判据一度被写在两处(管线一处、UI 的 ETA 一处,
/// 写法还不同)。2026-09-13 管线侧实测回退成旧顺序后,UI 忘了跟着改 → **界面上给的预计时间在算一个
/// 根本不会执行的顺序**,这正是用户反复抱怨"预计时间不准"的来源之一。
/// 现在顺序判据只有 `VideoPipeline.UpscaleRunsFirst` 一处,下面这些用例把它与 ETA 的口径一起钉住。</summary>
public class UpscaleOrderTests
{
    [Fact]
    public void UpscaleFirst_is_disabled_today()
    {
        // 【单一开关】当前处于 2026-09-13 的实测回退状态:恒为旧顺序「补帧 → 超分」。
        // 若你刚把 UpscaleFirstEnabled 改成 true(重新启用新顺序),本测试会红 —— 那是刻意的提醒:
        // 请同时更新 VideoService 的阶段顺序判定、进度区间(StageProgressPct)与 UI 的 ETA 调用点,
        // 并删掉/改写这条断言,而不是把新顺序和旧估算混在一起。
        Assert.False(VideoPipeline.UpscaleFirstEnabled, "新顺序当前应为回退状态(false)");

        // 任何组合都必须给出 false(开关关着时,其余条件不再参与)
        foreach (var up in new[] { true, false })
            foreach (var scale in new[] { 1.0, 2.0, 3.0, 4.0 })
                foreach (var interp in new[] { true, false })
                    foreach (var shrink in new[] { true, false })
                        Assert.False(VideoPipeline.UpscaleRunsFirst(up, scale, interp, shrink),
                            $"up={up} scale={scale} interp={interp} shrink1x={shrink} 不该判成新顺序");
    }

    [Fact]
    public void Disabled_reason_is_kept_in_one_place()
    {
        // 回退原因必须留在判据旁边(单一来源),否则下次又会有人"按新顺序估"而不知道该看哪里
        Assert.False(string.IsNullOrWhiteSpace(VideoPipeline.UpscaleFirstDisabledReason));
        Assert.Contains("回退", VideoPipeline.UpscaleFirstDisabledReason);
    }

    [Theory]
    [InlineData(true, 2.0, true)]     // 1x/2x + 补帧:回退前这条曾是"新顺序"的典型
    [InlineData(false, 2.0, true)]    // 不超分
    [InlineData(true, 4.0, true)]     // 4x:任何情况下都是旧顺序
    [InlineData(true, 1.0, false)]    // 不补帧
    public void Eta_uses_the_same_order_as_the_pipeline(bool up, double scale, bool interp)
    {
        bool runsFirst = VideoPipeline.UpscaleRunsFirst(up, scale, interp);
        // ETA 传的顺序必须就是上面这个值(管线与 ETA 同一个来源)
        double etaWithSourceOrder = VideoPipeline.EstimateProcessSeconds(10, 30, 1920, 1080,
            up, scale, "waifu2x", interp, 2, dedup: false, 0, upscaleFirst: runsFirst);
        double oldOrder = VideoPipeline.EstimateProcessSeconds(10, 30, 1920, 1080,
            up, scale, "waifu2x", interp, 2, dedup: false, 0, upscaleFirst: false);
        Assert.Equal(oldOrder, etaWithSourceOrder, 9);

        // 且这条断言是有牙齿的:对"1x/2x + 补帧"这种组合,两种顺序的估算确实不同
        // (不同 = 顺序选错就会给出另一个数,正是本次要修的 bug)
        if (up && scale <= 2.001 && interp)
        {
            double newOrder = VideoPipeline.EstimateProcessSeconds(10, 30, 1920, 1080,
                up, scale, "waifu2x", interp, 2, dedup: false, 0, upscaleFirst: true);
            Assert.NotEqual(oldOrder, newOrder);
        }
    }

    [Fact]
    public void Eta_batch_count_follows_the_same_order()
    {
        // 批数取值也必须跟着同一个顺序(任务 E 的批数分支:新顺序按超分侧输入=源帧数算,
        // 旧顺序按补帧后总帧数算)。这里用同一素材把两个顺序的"批启动开销"差额钉住:
        //  素材 10s×30fps=300 帧、补帧 4x、设备好(10.4G):
        //    旧顺序(真实执行):补帧后 1200 帧 > 400 → 不单批;且源 300<900 但补帧后 1200 ≥1200 → 视频长
        //      → 【T 口径变更】700 帧/批 → ⌈1200/700⌉ = 2 批(旧口径 ⌈1200/400⌉=3)
        //    新顺序(当前不会执行):超分侧输入=源 300 帧 ≤400 → 单批 1 批
        const double perBatch = 1.15;   // = VideoPipeline.AssumedEngineStartupSecondsPerBatch(2026-09-13 真机标定:1.0~1.3s)
        double oldOrder = VideoPipeline.EstimateProcessSeconds(10, 30, 1920, 1080, up: true, 2.0, "waifu2x",
            interp: true, 4, dedup: false, 0, upscaleFirst: false, freeRamGB: 10.4)
            - VideoPipeline.EstimateProcessSeconds(10, 30, 1920, 1080, up: true, 2.0, "waifu2x",
            interp: true, 4, dedup: false, 0, upscaleFirst: false);
        double newOrder = VideoPipeline.EstimateProcessSeconds(10, 30, 1920, 1080, up: true, 2.0, "waifu2x",
            interp: true, 4, dedup: false, 0, upscaleFirst: true, freeRamGB: 10.4)
            - VideoPipeline.EstimateProcessSeconds(10, 30, 1920, 1080, up: true, 2.0, "waifu2x",
            interp: true, 4, dedup: false, 0, upscaleFirst: true);
        Assert.Equal(2 * perBatch * 1.15, oldOrder, 6);   // 旧顺序 2 批(【T】700 帧/批)
        Assert.Equal(1 * perBatch * 1.15, newOrder, 6);   // 新顺序 1 批(超分侧单批)
        Assert.NotEqual(oldOrder, newOrder);
    }
}
