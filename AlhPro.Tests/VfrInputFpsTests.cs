using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【2026-09-30 用户反馈】VFR(可变帧率)素材的**输入帧率被翻倍**,并进而造成**音画不同步**。
///
/// 根因:输入帧率探测当时用 `ffprobe … -of csv=p=0` 做**位置解析**,而 ffprobe 的 csv writer 按
/// 【内部结构体字段序】输出、**忽略 `-show_entries` 的请求序** —— 随包 ffprobe 实测(show_streams 与
/// `-of json` 都一样)是 **r_frame_rate 在前、avg_frame_rate 在后**,于是"取第一个有效值"拿到的其实是
/// **r_frame_rate(容器最大帧率)**:VFR 素材 avg=30 / r=60 ⇒ 输入帧率报成 60 ⇒ 翻倍 ✓。
/// 输入帧率又喂给编码的 `-framerate` ⇒ 成片按 2 倍速编出来、音频原速复制 ⇒ **音画不同步**。
///
/// 修法:一律用带标签输出(`-of default=nw=1:nk=0`)+ 按 key 取,**优先 avg_frame_rate**,取不到才退 r。
/// 本文件钉住这条口径(纯函数,不起 ffprobe)。</summary>
public class VfrInputFpsTests
{
    // 真机形态:带标签输出里 r 在前、avg 在后(VFR:峰值 60、真实 30)
    private const string VfrLabeled = "r_frame_rate=60/1\navg_frame_rate=30/1\n";
    // CFR:两个一样
    private const string CfrLabeled = "r_frame_rate=30/1\navg_frame_rate=30/1\n";
    // VFR 且 avg 无效(旧注释记录过:录屏/手机视频的 avg 实测可能是 0/0)
    private const string AvgBroken = "r_frame_rate=60/1\navg_frame_rate=0/0\n";
    private const string BothBroken = "r_frame_rate=N/A\navg_frame_rate=N/A\n";

    /// <summary>★ 这条就是用户反馈的回归:VFR 素材必须取 avg(30),**绝不能**取 r(60)。</summary>
    [Fact]
    public void Vfr_uses_average_not_the_peak_rate()
        => Assert.Equal(30.0, FfprobeFps.PreferAverageFps(VfrLabeled));

    [Fact]
    public void Cfr_is_unaffected()
        => Assert.Equal(30.0, FfprobeFps.PreferAverageFps(CfrLabeled));

    /// <summary>avg 拿不到(`0/0`)才退到 r;两个都无效 ⇒ null(留空,处理时按该视频自动探测)。</summary>
    [Fact]
    public void Falls_back_to_r_only_when_average_is_unusable()
        => Assert.Equal(60.0, FfprobeFps.PreferAverageFps(AvgBroken));

    [Fact]
    public void Returns_null_when_both_are_unusable()
        => Assert.Null(FfprobeFps.PreferAverageFps(BothBroken));

    /// <summary>分数帧率(24000/1001)按原口径解析,且顺序颠倒也一样(按 key 取,与行序无关)。</summary>
    [Theory]
    [InlineData("r_frame_rate=30000/1001\navg_frame_rate=24000/1001\n", 23.976)]
    [InlineData("avg_frame_rate=24000/1001\nr_frame_rate=30000/1001\n", 23.976)]
    public void Fractional_rates_are_parsed_and_line_order_does_not_matter(string raw, double expected)
        => Assert.Equal(expected, FfprobeFps.PreferAverageFps(raw)!.Value, 2);

    /// <summary>没有标签的裸输出(旧格式)不再被当成帧率来源 —— 它正是出错的那条路。
    /// (调用方现在只喂带标签输出;这里钉住"旧格式返回 null"以免有人又接回位置解析。)</summary>
    [Fact]
    public void Unlabeled_legacy_output_is_not_accepted()
        => Assert.Null(FfprobeFps.PreferAverageFps("60/1,30/1"));
}
