using AlhPro.Core;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>纯函数测试:CeilPowerOfTwo(倍率向上取 2 的幂,视频/图片超分用)。</summary>
public class EnginePureTests
{
    [Theory]
    [InlineData(1.0, 1)]
    [InlineData(2.0, 2)]
    [InlineData(3.0, 4)]
    [InlineData(4.0, 4)]
    [InlineData(1.5, 2)]
    [InlineData(5.0, 8)]
    [InlineData(8.0, 8)]
    [InlineData(16.0, 16)]
    [InlineData(3.9, 4)]
    public void CeilPowerOfTwo_returns_next_power_of_two(double n, int expected)
    {
        Assert.Equal(expected, PathUtil.CeilPowerOfTwo(n));
    }

    [Fact]
    public void CeilPowerOfTwo_exact_power_returns_itself()
    {
        Assert.Equal(8, PathUtil.CeilPowerOfTwo(8));
        Assert.Equal(32, PathUtil.CeilPowerOfTwo(32));
    }

    [Fact]
    public void CeilPowerOfTwo_just_above_power_rounds_up()
    {
        Assert.Equal(16, PathUtil.CeilPowerOfTwo(9));
        Assert.Equal(4, PathUtil.CeilPowerOfTwo(3.9));
    }

    [Fact]
    public void CeilPowerOfTwo_negative_or_zero_returns_one()
    {
        Assert.Equal(1, PathUtil.CeilPowerOfTwo(0));
        Assert.Equal(1, PathUtil.CeilPowerOfTwo(-3));
    }
}

/// <summary>纯函数测试:FfmpegSafePath(中文/非 ASCII 路径转 8.3 短路径,规避 ffmpeg GBK 乱码)。</summary>
public class FfmpegSafePathTests
{
    [Fact]
    public void Pure_ascii_path_returned_unchanged()
    {
        var p = @"D:\work\video.mp4";
        Assert.Equal(p, PathUtil.FfmpegSafePath(p));
    }

    [Fact]
    public void Chinese_path_with_83_names_shortens()
    {
        // 用真实存在的中文目录测。8.3 短路径是否生效取决于卷是否启用 8.3(部分机器/新目录会禁用),
        // 所以这里只断言"函数不崩、返回合法路径";若 8.3 生效(结果含 '~' 或变短)则进一步校验。
        var dir = Path.Combine(Path.GetTempPath(), "测试目录ALH" + Guid.NewGuid().ToString("N")[..4]);
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "我的视频素材.mp4");
            File.WriteAllText(file, "x");
            var result = PathUtil.FfmpegSafePath(file);
            // 不抛异常,返回非空
            Assert.False(string.IsNullOrEmpty(result));
            // 若 8.3 生效 → 结果含 '~'(短名特征)且变短;若卷禁用 8.3 → 至少正确回退原路径(不崩)
            if (result.Contains('~'))
            {
                Assert.True(result.Length <= file.Length, $"8.3 生效时长度应不增: {result}");
            }
            else
            {
                Assert.Equal(file, result);   // 8.3 禁用时回退原样(不报错即可)
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Null_or_empty_path_handled()
    {
        // 空/未知路径:不抛异常,尽量原样返回
        Assert.Equal("", PathUtil.FfmpegSafePath(""));
    }
}
