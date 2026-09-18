using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>输出文件名的命名契约(2026-09-16 用户反馈:"指定帧率导出的名字为什么还要用倍率补帧,
/// 直接显示 60fps 就行了")。
///
/// 【为什么要有这条】指定「输出帧率」时,那个倍率(如 60fps → 3x)只是**为了凑到目标帧率而算出来的
/// 内部中间量**,不是用户设定的东西 —— 写进文件名既误导又啰嗦。旧写法还会叠成三段重复,真机实测
/// (2026-09-16 19:18,指定 60fps)生成的是:`…_补帧2x(自动3x)_rife413_60fps.mp4`。
/// 现在是:`…_补帧60fps_rife413.mp4`(不指定帧率时仍写实际倍率,与旧行为逐字一致)。
///
/// 【本文件由真实日志实证命名现状】
///   17:17 `…_补帧2x_rife413_60fps.mp4` / 19:18 `…_补帧2x(自动3x)_rife413_60fps.mp4`
///   / 22:04 `…_补帧3x_rife413_60fps.mp4` —— 三处都同时写了倍率与帧率。</summary>
public class OutputFileNameTests
{
    /// <summary>① 指定帧率时:补帧那一栏写帧率、不写倍率,而且不再另外重复追加一次 fps。</summary>
    [Fact]
    public void Target_fps_export_names_show_the_fps_not_the_multiplier()
    {
        var vv = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");

        // 标记按目标帧率走
        Assert.Contains("string interpLabelForName = targetFps is > 0", vv);
        Assert.Contains("? $\"{targetFps.Value:0.##}fps\"", vv);
        // 旧的"倍率 + 后面再追加一次 fps"必须彻底消失(那一处正是三段重复的来源)
        Assert.DoesNotContain("+ (targetFps != null ? $\"_{targetFps:0.##}fps\" : \"\")", vv);
        // 文件名过长的最短化路径也要跟着走帧率(否则同一个素材两条路径给出两种名字)
        Assert.Contains("(targetFps is > 0 ? $\"_补帧{targetFps.Value:0.##}fps\" : $\"_补帧{interpScale}x\")", vv);
    }

    /// <summary>② 没指定帧率时仍写**实际倍率**(这是用户设的那个,不能一起改掉)。</summary>
    [Fact]
    public void Without_a_target_fps_the_name_still_shows_the_multiplier()
    {
        var vv = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
        Assert.Contains(": (effScaleForName != interpScale ? $\"{interpScale}x(自动{effScaleForName}x)\" : $\"{interpScale}x\");", vv);
        // 补帧那一栏仍然接在文件名后缀里(不是被整段删掉)
        Assert.Contains("(interp ? $\"_补帧{interpLabelForName}_{UpscaleView.ModelShort(interpModel)}\" : \"\")", vv);
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var cand = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(cand)) return File.ReadAllText(cand);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("找不到仓库文件: " + string.Join('/', parts));
    }
}
