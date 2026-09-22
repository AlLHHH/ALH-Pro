using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>视频后处理滤镜链构造的单测。
/// 【为什么钉这么死】这条链直接决定成片画面,而 ffmpeg 侧的每一档都是【不同机制、不同参数】;
/// 一旦有人"顺手合并/近似",画面就变了却不会报错。这里把每一档产出的滤镜字符串【逐字】固定下来,
/// 并显式断言"四档各自独立、顺序固定、没有被合并"(2026-09-13 用户问到"能不能合并",结论见
/// AlhPro.Core.VideoPostFilters 类注释:不能 —— 会改画面)。</summary>
public class VideoPostFilterTests
{
    // 官方预设一「通用画质增强 不含补帧」:锐化20 清晰25 钝化35 细节40,AA 已按用户要求清零
    private const string Preset1 =
        "smartblur=luma_radius=1:luma_strength=-0.20:luma_threshold=3," +
        "unsharp=13:13:0.13:13:13:0," +
        "smartblur=luma_radius=2:luma_strength=-0.35:luma_threshold=8," +
        "cas=strength=0.24";

    // 官方预设二「动漫通用」:锐化20 清晰25 钝化40 细节40,AA 清零(与预设一只差钝化强度)
    private const string Preset2 =
        "smartblur=luma_radius=1:luma_strength=-0.20:luma_threshold=3," +
        "unsharp=13:13:0.13:13:13:0," +
        "smartblur=luma_radius=2:luma_strength=-0.40:luma_threshold=8," +
        "cas=strength=0.24";

    [Fact]
    public void Preset1_chain_is_pinned()
    {
        // 预设一的**新刻度**数值(29/50/70/80)必须产出与老刻度(20/25/35/40)**逐字相同**的滤镜链
        // ⇒ 这就是"迁移只改数字、不改画面"的证明(2026-09-20 后处理刻度重新定标)。
        Assert.Equal(0.125, 50 / 100.0 * VideoPostFilters.SafeMax["clarity"], 9);
        Assert.Equal(Preset1, VideoPostFilters.Build(29, 50, 70, 80, aa: 0));
    }

    [Fact]
    public void Preset2_chain_is_pinned()
    {
        Assert.Equal(0.40, 80 / 100.0 * VideoPostFilters.SafeMax["usm"], 9);
        Assert.Equal(Preset2, VideoPostFilters.Build(29, 50, 80, 80, aa: 0));
    }

    /// <summary>**老设置/老预设的等效迁移**(2026-09-20 刻度重新定标)。
    /// 老刻度:锐化/钝化/边缘 100→1.00,清晰 100→0.50,保留细节 100→0.60;
    /// 新刻度(实测安全上限):0.70 / 0.25 / 0.50 / 0.30 / 0.70。
    /// ⇒ 旧值 20/25/35/40 必须换算成 29/50/70/80,这样**同一份预设的画面强度不变**。</summary>
    [Theory]
    [InlineData("sharpen", 20, 29)]     // 0.20 = 29×0.0070
    [InlineData("clarity", 25, 50)]     // 0.125 = 50×0.0025
    [InlineData("usm", 35, 70)]         // 0.35 = 70×0.0050
    [InlineData("detail", 40, 80)]      // 0.24 = 80×0.0030
    [InlineData("edge", 30, 43)]        // 0.30 = 43×0.0070
    [InlineData("edge", 60, 86)]        // 0.60 = 86×0.0070
    [InlineData("usm", 0, 0)]           // 关着的档不动
    public void Old_strengths_migrate_to_equivalent_new_values(string key, int oldValue, int expected)
        => Assert.Equal(expected, VideoPostFilters.MigrateStrength(key, oldValue));

    /// <summary>迁移是幂等思路上的"一次性":迁移后的值再迁一次必须不变(否则每次启动都会越迁越强)。</summary>
    [Fact]
    public void Migration_is_a_one_shot()
    {
        foreach (var key in new[] { "sharpen", "clarity", "usm", "detail", "edge" })
        {
            int once = VideoPostFilters.MigrateStrength(key, 40);
            // 迁移后的值按"已经是新刻度"处理:再喂给迁移函数也不该超过 100 且不会被二次放大成别的档
            Assert.InRange(once, 0, 100);
            Assert.Equal(VideoPostFilters.Strength(key, once), System.Math.Min(VideoPostFilters.SafeMax[key], once / 100.0 * VideoPostFilters.SafeMax[key]), 9);
        }
    }

    [Fact]
    public void Four_stages_stay_four_independent_filters()
    {
        var chain = VideoPostFilters.Build(20, 25, 35, 40, 0)!;
        // 两档 smartblur 半径不同(1 与 2)、阈值不同;一趟 unsharp;一趟 cas —— 四档不许被合并成一档
        Assert.Equal(2, CountOf(chain, "smartblur="));
        Assert.Equal(1, CountOf(chain, "unsharp="));
        Assert.Equal(1, CountOf(chain, "cas="));
        Assert.Contains("luma_radius=1", chain);
        Assert.Contains("luma_radius=2", chain);
        Assert.Contains("luma_threshold=3", chain);
        Assert.Contains("luma_threshold=8", chain);
        // 顺序固定:锐化 → 清晰 → 钝化蒙版 → 保留细节
        Assert.True(chain.IndexOf("luma_radius=1", System.StringComparison.Ordinal)
                  < chain.IndexOf("unsharp=", System.StringComparison.Ordinal));
        Assert.True(chain.IndexOf("unsharp=", System.StringComparison.Ordinal)
                  < chain.IndexOf("luma_radius=2", System.StringComparison.Ordinal));
        Assert.True(chain.IndexOf("luma_radius=2", System.StringComparison.Ordinal)
                  < chain.IndexOf("cas=", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Aa_parameter_produces_no_filter()
    {
        // 边缘抗锯齿自 2026-09-12 起改走 C# 并行实现(见 EngineService.ApplyEdgeSmoothToJpeg):
        // 无论传多少,这条链都不该多出滤镜 —— 也绝不该回到 ffmpeg 的 sab。
        Assert.Equal(VideoPostFilters.Build(20, 25, 35, 40, 0), VideoPostFilters.Build(20, 25, 35, 40, 45));
        Assert.Equal(VideoPostFilters.Build(20, 25, 35, 40, 0), VideoPostFilters.Build(20, 25, 35, 40, 100));
        Assert.DoesNotContain("sab", VideoPostFilters.Build(20, 25, 35, 40, 100)!);
    }

    [Fact]
    public void All_zero_means_no_filter_chain()
    {
        Assert.Null(VideoPostFilters.Build(0, 0, 0, 0));
        Assert.Null(VideoPostFilters.Build(0, 0, 0, 0, 45));   // 只开 AA 时链仍然为空(AA 不在 ffmpeg 链里)
    }

    [Fact]
    public void Only_enabled_stages_are_emitted()
    {
        Assert.Equal("unsharp=13:13:0.06:13:13:0,cas=strength=0.12", VideoPostFilters.Build(0, 25, 0, 40));
        Assert.Equal("smartblur=luma_radius=1:luma_strength=-0.35:luma_threshold=3", VideoPostFilters.Build(50, 0, 0, 0));
    }

    [Theory]
    [InlineData(100, "-0.70", 6)]   // 【2026-09-20 新刻度】100 = 实测安全上限 0.70(旧刻度是 1.00);>60 走阈值 6
    [InlineData(200, "-0.70", 6)]
    [InlineData(1, "-0.01", 3)]
    public void Sharpen_strength_is_capped(int value, string expected, int threshold)
    {
        Assert.Equal($"smartblur=luma_radius=1:luma_strength={expected}:luma_threshold={threshold}",
            VideoPostFilters.Build(value, 0, 0, 0));
    }

    [Theory]
    [InlineData(60, 3)]   // ≤60 用 3
    [InlineData(61, 6)]   // >60 用 6
    [InlineData(100, 6)]
    public void Sharpen_threshold_switches_at_60(int value, int threshold)
    {
        Assert.Contains($"luma_threshold={threshold}", VideoPostFilters.Build(value, 0, 0, 0)!);
    }

    [Fact]
    public void Clarity_usm_detail_are_capped()
    {
        // 【2026-09-20 新刻度】每一档的 100 = 实测安全上限(依据 _qa\post_filter_audit.py 的噪声/边宽实测):
        //   清晰 0.25(旧 0.50:100 档会把边缘从 6.89px 拉宽到 9.83px ✗)
        //   钝化蒙版 0.50(旧 1.00:100 档平坦噪声 2.03× ✗)
        //   保留细节 0.30(旧 0.60:40 档就已 1.63× 噪声 ✗)
        Assert.Equal("unsharp=13:13:0.25:13:13:0", VideoPostFilters.Build(0, 100, 0, 0));
        Assert.Equal("unsharp=13:13:0.25:13:13:0", VideoPostFilters.Build(0, 500, 0, 0));
        Assert.Equal("smartblur=luma_radius=2:luma_strength=-0.50:luma_threshold=8", VideoPostFilters.Build(0, 0, 100, 0));
        Assert.Equal("cas=strength=0.30", VideoPostFilters.Build(0, 0, 0, 100));
        Assert.Equal("cas=strength=0.30", VideoPostFilters.Build(0, 0, 0, 999));
    }

    [Fact]
    public void Chroma_strength_of_unsharp_stays_zero()
    {
        // unsharp 的色度强度必须保持 0:它是"只锐化亮度"的关键(第 6 个参数,不是阈值)
        Assert.EndsWith(":13:13:0", VideoPostFilters.Build(0, 25, 0, 0));
    }

    private static int CountOf(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, System.StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }
}
