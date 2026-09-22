using System;
using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>前景×alpha 与背景的合成契约(2026-09-22)。换背景路径的正确性全靠它。
///
/// 【为什么值得单独钉】这一段每帧要跑 pixels 次,而且一旦算错(比如把 alpha 用成 1-alpha、
/// 或把 RGB 通道顺序搞反)整条片都是错的 —— 但在小图上肉眼看不出,只有单测能第一时间拦住。
/// </summary>
public class VideoMattingCompositeTests
{
    [Fact]
    public void Alpha_zero_shows_background_only()
    {
        var fg = new byte[] { 255, 0, 0 }; var bg = new byte[] { 0, 0, 255 };
        var dst = new byte[3]; var a = new float[] { 0f };
        VideoMatting.Composite(fg, a, bg, dst, 1);
        Assert.Equal(new byte[] { 0, 0, 255 }, dst);
    }

    [Fact]
    public void Alpha_one_shows_foreground_only()
    {
        var fg = new byte[] { 255, 0, 0 }; var bg = new byte[] { 0, 0, 255 };
        var dst = new byte[3]; var a = new float[] { 1f };
        VideoMatting.Composite(fg, a, bg, dst, 1);
        Assert.Equal(new byte[] { 255, 0, 0 }, dst);
    }

    [Fact]
    public void Alpha_half_blends_linearly()
    {
        var fg = new byte[] { 200, 100, 0 }; var bg = new byte[] { 0, 100, 200 };
        var dst = new byte[3]; var a = new float[] { 0.5f };
        VideoMatting.Composite(fg, a, bg, dst, 1);
        Assert.Equal(100, dst[0]); Assert.Equal(100, dst[1]); Assert.Equal(100, dst[2]);
    }

    [Fact]
    public void Short_input_is_ignored_not_crashing()
    {
        var dst = new byte[3];
        VideoMatting.Composite(new byte[1], new float[1], new byte[3], dst, 1);   // 不许抛
        Assert.Equal(new byte[3], dst);
    }

    // ---- 以下为执行计划时补的把关测试 ----

    /// <summary>【dst 长度不足也必须安全返回】计划里的守卫只检查了 fg/bg/alpha 三路长度,
    /// 没检查 dst —— 而调用方复用的缓冲区长度不匹配(例如上一段视频是 4K、这一段是 1080p)
    /// 恰恰是这种代码最现实的崩法。这里钉住"越界前先返回,一个字节都不写"。</summary>
    [Fact]
    public void Short_destination_is_ignored_not_overrunning()
    {
        var fg = new byte[] { 255, 255, 255 }; var bg = new byte[] { 0, 0, 0 };
        var dst = new byte[2];                        // 少一个字节
        VideoMatting.Composite(fg, new[] { 1f }, bg, dst, 1);
        Assert.Equal(new byte[2], dst);               // 没被写脏
    }

    /// <summary>多像素 + 逐个通道都要对:只测 1 个像素测不出"循环步进写错一格"这类问题。</summary>
    [Fact]
    public void Multiple_pixels_walk_in_correct_order()
    {
        var fg = new byte[] { 10, 20, 30,  40, 50, 60 };
        var bg = new byte[] { 200, 210, 220,  230, 240, 250 };
        var a  = new float[] { 0f, 1f };
        var dst = new byte[6];
        VideoMatting.Composite(fg, a, bg, dst, 2);
        Assert.Equal(new byte[] { 200, 210, 220, 40, 50, 60 }, dst);
    }

    /// <summary>alpha 越界(模型偶尔吐出 &lt;0 或 &gt;1 的 logits 归一化残差)必须被夹住,
    /// 不能回绕成"亮暗反转"的怪色。</summary>
    [Fact]
    public void Out_of_range_alpha_is_clamped()
    {
        var fg = new byte[] { 255, 255, 255 }; var bg = new byte[] { 0, 0, 0 };
        var dst = new byte[3];
        VideoMatting.Composite(fg, new[] { 2f }, bg, dst, 1);
        Assert.Equal(new byte[] { 255, 255, 255 }, dst);
        VideoMatting.Composite(fg, new[] { -1f }, bg, dst, 1);
        Assert.Equal(new byte[] { 0, 0, 0 }, dst);
    }

    /// <summary>四舍五入:0.5 混合两个相邻值必须落在最近的那个整数上(截断会整体偏暗一格)。</summary>
    [Fact]
    public void Blending_rounds_to_nearest_not_truncates()
    {
        var fg = new byte[] { 101, 0, 0 }; var bg = new byte[] { 100, 0, 0 };
        var dst = new byte[3];
        VideoMatting.Composite(fg, new[] { 0.5f }, bg, dst, 1);
        Assert.Equal(101, dst[0]);        // 100.5 ⇒ 101(截断会得到 100)
    }
}
