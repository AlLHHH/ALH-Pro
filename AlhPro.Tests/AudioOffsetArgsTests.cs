using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【2026-09-30 用户反馈 · 音画不同步】画面来自 JPG 序列(PTS 从 0 开始),音频却保留源素材自己的起始时间
/// ⇒ 源视频流的 `start_time ≠ 0` 时(剪映/手机导出很常见)两者就差这一个 start_time = **固定偏移的音画不同步**,
/// 而软件原有自检只比【时长】(31.04 = 31.04)根本看不见它。
///
/// 这条纯函数决定"给音频输入加不加 `-itsoffset`":必须是**输入级**参数 —— 滤镜(`asetpts`/`atrim`)与 `-c:a copy`
/// 互斥,会把"能原样复制音轨"的机器逼成重编码。</summary>
public class AudioOffsetArgsTests
{
    [Theory]
    [InlineData(0.0, "")]                 // 常见 MP4:start_time 就是 0 ⇒ 零操作
    [InlineData(0.0009, "")]              // 1ms 以内:视为 0
    [InlineData(double.NaN, "")]          // 读不到 ⇒ 不偏移
    [InlineData(double.PositiveInfinity, "")]
    [InlineData(0.023, " -itsoffset -0.023 ")]        // 常见的一帧级偏移
    [InlineData(0.5, " -itsoffset -0.5 ")]            // 剪映/手机导出里肉眼可见的那种
    [InlineData(0.066667, " -itsoffset -0.066667 ")]  // 保留 6 位小数
    public void Decides_the_input_level_offset(double start, string expected)
        => Assert.Equal(expected, VideoEncodeGuard.AudioOffsetArgs(start));

    /// <summary>【2026-10-20 用户诊断包 · 编码 100% 失败】调用处写的是
    /// `{audioShiftArgs}-i "{inputVideo}"`(紧贴下一个参数),返回值必须**自带尾空格**,
    /// 否则拼成 `-itsoffset -0.083-i "x.mp4"` ⇒ ffmpeg 把源文件当输出 url,
    /// `exit -22 … Option itsoffset cannot be applied to output url`,且只在那一步才炸。</summary>
    [Fact]
    public void Offset_value_carries_its_own_trailing_space_so_it_never_glues_to_the_next_arg()
    {
        foreach (var start in new[] { 0.023, 0.083, 0.5, 1.5 })
        {
            var a = VideoEncodeGuard.AudioOffsetArgs(start);
            Assert.NotEmpty(a);
            Assert.EndsWith(" ", a);                      // ★ 自带尾空格 ⇒ 不会与下一个 -i 粘成一串
            Assert.False(System.Text.RegularExpressions.Regex.IsMatch(a, @"\d-i\s*$"),
                "偏移值不许把下一个参数(-i)吞进来:" + a);
        }
    }

    /// <summary>钉住"用输入级参数、不用滤镜"这条口径(滤镜会把 -c:a copy 变不合法)。</summary>
    [Fact]
    public void Uses_input_level_option_not_a_filter()
    {
        var a = VideoEncodeGuard.AudioOffsetArgs(0.5);
        Assert.Contains("-itsoffset", a);
        Assert.DoesNotContain("asetpts", a);
        Assert.DoesNotContain("-af", a);
        Assert.DoesNotContain("atrim", a);
    }
}
