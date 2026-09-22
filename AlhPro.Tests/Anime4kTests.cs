using AlhPro.Core;
using System;
using System.IO;
using Xunit;

namespace AlhPro.Tests;

/// <summary>1x 修复档(Anime4K)的滤镜串与路径约定(2026-09-21 用户定案:把 1x 档从「2x 放大后缩回」换成 Anime4K)。
/// 【为什么单测】这里错了**不会在编译期报错**,而是运行时 ffmpeg 直接报一句看不懂的话:
/// 绝对路径里的 `D:` 会被滤镜参数解析当成**选项分隔符**(实测报 "No option name near '/Video2X Qt6/...'",
/// 看着像路径不存在,其实是解析错误);反斜杠在滤镜里是转义字符,同样会被吃掉。
/// ⇒ 约定:只传**文件名 + 正斜杠**,并且要求 ffmpeg 以自己所在目录为工作目录。</summary>
public class Anime4kTests
{
    /// <summary>滤镜串只能有文件名:不许出现盘符冒号、不许出现反斜杠。</summary>
    [Fact]
    public void Filter_argument_carries_only_a_file_name()
    {
        string f = Anime4k.FilterArgument();
        Assert.StartsWith("libplacebo=custom_shader_path=", f);
        Assert.EndsWith(Anime4k.ShaderFileName, f);
        Assert.DoesNotContain(":", f.Substring("libplacebo=custom_shader_path=".Length));   // ⚠ 冒号=选项分隔符
        Assert.DoesNotContain("\\", f);                                                     // ⚠ 反斜杠=转义符
        // 【2026-09-22 实测改判】原来断言连正斜杠都不给,那是错的:实测不带 shaders/ 前缀退出码 -1、反斜杠被吃掉退出码 -1、正斜杠退出码 0 ⇒ 正斜杠必须保留。
            Assert.Contains("shaders/", f);                                                      // 连正斜杠都不给:最保险
    }

    /// <summary>探测用的滤镜串同样只许文件名(它就是这条约定的第一个受害者)。</summary>
    [Fact]
    public void Probe_filter_follows_the_same_rule()
    {
        Assert.StartsWith("libplacebo=custom_shader_path=", Anime4k.ProbeFilter);
        Assert.Contains(":w=64:h=64", Anime4k.ProbeFilter);          // 合成图很小,亚秒级
        string afterEq = Anime4k.ProbeFilter.Substring("libplacebo=custom_shader_path=".Length);
        string nameOnly = afterEq.Substring(0, afterEq.IndexOf(':'));
        Assert.Equal(Anime4k.ShaderFileName, nameOnly);
        Assert.DoesNotContain("\\", Anime4k.ProbeFilter);
    }

    /// <summary>报告/日志里给的是相对路径(相对 ffmpeg 目录),同样不含盘符冒号。</summary>
    [Fact]
    public void Relative_path_is_relative_and_colon_free()
    {
        Assert.False(Path.IsPathRooted(Anime4k.ShaderRelativePath));
        Assert.DoesNotContain(":", Anime4k.ShaderRelativePath);
        Assert.EndsWith(Anime4k.ShaderFileName, Anime4k.ShaderRelativePath);
    }

    /// <summary>**随包断言**:着色器文件必须真的躺在 engines\ffmpeg\shaders\ 下(权重文件那条测试的同款思路)。
    /// 引擎找不到它时 ffmpeg 会直接失败 —— 而这条链只有在真跑 1x 档时才触发,不测就等着用户撞。
    /// 目录不存在(全新克隆/没装引擎)时跳过,不误报。</summary>
    [Fact]
    public void Shader_file_ships_next_to_ffmpeg()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            foreach (var baseName in new[] { Path.Combine("engines", "ffmpeg"), Path.Combine("发布版", "engines", "ffmpeg") })
            {
                var ffDir = Path.Combine(dir.FullName, baseName);
                if (!File.Exists(Path.Combine(ffDir, "ffmpeg.exe"))) continue;
                string shader = Path.Combine(ffDir, Anime4k.ShaderRelativePath.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(shader), $"缺少随包的 Anime4K 着色器:{shader}(1x 修复档会直接失败)");
                var text = File.ReadAllText(shader);
                // 随包即合规:MIT 许可声明必须在文件里
                Assert.Contains("MIT License", text);
                Assert.Contains("bloc97", text);
                return;   // 只验第一个存在的引擎目录即可
            }
            dir = dir.Parent;
        }
    }
}
