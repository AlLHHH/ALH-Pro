using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【2026-09-27】抠图蒙版后处理的**分辨率口径**契约:阈值映射与形态学清洗必须在**全分辨率**上做。
///
/// 【为什么要有这条】作者反馈「抠图抠出来有像素点一样的感觉、方块边缘」。实测(反射**已编译产物**跑真图,
/// 3840×2160 + ISNet 1024 输入):遮罩一个像素被放大成 **3.75 个真实像素**,边界是肉眼可见的阶梯,
/// 并且有 **13 个 &lt;=64px 的前景孤岛**(放大后就是"像素点");U²-Net 那两支(320 输入)每格 **12 个像素**。
/// 根因:`ProcessMask` 原先在**低分辨率**蒙版上先 `mask >= fg` 二值化 + `MorphBinary`,**然后才**放大 ——
/// 边界几何在小图上就被冻结成方格了(与 `AlhPro.Core.VideoMatting.PostProcessAlpha` 注释里那句
/// 「硬二值会把发丝、半透明边缘变成锯齿」是同一件事)。
///
/// 【口径】ImgUpscalerUI 的私有实现拿不到编译产物 ⇒ 与 RealCuganContractTests / VideoPageWaifu2xRemovedTests
/// 同手法:读源码文本 + 去注释断言形态。**行为级证据**(台阶 3.75px → 1.0px、孤岛 13 → 0)由仓库外的
/// 反射探针在**本轮构建出来的 ALHPro.dll** 上给出,见提交说明与 `_t66`/`_cutout` 证据。</summary>
public class CutoutMaskQualityTests
{
    private static string CutoutCode => CodeOnly(ReadRepoFile("ImgUpscalerUI", "CutoutService.cs"));

    /// <summary>低分辨率上的二值化必须绝迹:`mask[y, x] >= fg ? (byte)1 : (byte)0` 是方格台阶的来源。</summary>
    [Fact]
    public void Mask_is_never_binarized_on_the_low_resolution_grid()
    {
        Assert.DoesNotContain("mask[y, x] >= fg ? (byte)1 : (byte)0", CutoutCode);
    }

    /// <summary>低分辨率上的形态学清洗也必须绝迹(`MorphBinary` 的**调用**,不是它的声明)。</summary>
    [Fact]
    public void Low_resolution_morphology_call_is_gone()
    {
        Assert.DoesNotContain("MorphBinary(m,", CutoutCode);
    }

    /// <summary>形态学清洗改走 Core 的全分辨率件:`PostProcessAlpha(fg=0,bg=0,feather=0)` 只剩开运算;
    /// 并且紧跟一条**按面积**的去孤岛(开运算半径 1px 管不到大图上的斑点)。</summary>
    [Fact]
    public void Morphology_reuses_the_full_resolution_core_primitive()
    {
        Assert.Contains("AlhPro.Core.VideoMatting.PostProcessAlpha(alpha, w, h, 0, 0, 0, morphStrength)", CutoutCode);
        Assert.Contains("RemoveSmallIslands(alpha, w, h, Math.Max(4, areaPerMaskPixel * 3))", CutoutCode);
        Assert.Contains("static void RemoveSmallIslands(float[] a, int aw, int ah, int minArea)", CutoutCode);
    }

    /// <summary>去孤岛只保留在抠图页的本地函数里(纯数组操作)。**行为级证据在套件之外**:
    /// 反射**已编译产物**的探针在 4K 真图上量到的孤岛数量与边缘台阶,见提交说明与 `_cutout` 证据。</summary>
    [Fact]
    public void Despeckle_helper_stays_self_contained()
    {
        Assert.Contains("if (comp.Count < minArea)", CutoutCode);
        Assert.Contains("if (minArea <= 1) return;", CutoutCode);   // 守卫:minArea<=1 = 空操作
    }

    /// <summary>阈值映射不许再被形态学开关挡住 —— 它现在是全分辨率上产生"清晰边缘"的那一步。</summary>
    [Fact]
    public void Threshold_mapping_always_runs()
    {
        Assert.DoesNotContain("if (morphStrength <= 0)", CutoutCode);
    }

    /// <summary>顺序判定日志只能有一个前缀:`Decision.LogLine` 自己就以「顺序判定:」开头,
    /// 调用点再拼一次会打出「· 顺序判定:顺序判定:…」(2026-09-27 用户可见的重复)。</summary>
    [Fact]
    public void Order_decision_log_line_keeps_exactly_one_prefix()
    {
        string svc = CodeOnly(ReadRepoFile("ImgUpscalerUI", "VideoService.cs"));
        Assert.Contains("AppLogger.Info($\"· {uOrderLog}\"", svc);
        Assert.DoesNotContain("顺序判定:{uOrderLog}", svc);
    }

    /// <summary>去掉整行 `//` 注释后的源码(判接线时用):注释里会用反例话说清"旧写法长什么样",
    /// 带注释判会把说明本身当成违规命中。</summary>
    private static string CodeOnly(string src)
    {
        var lines = src.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var kept = new System.Collections.Generic.List<string>();
        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
            kept.Add(line);
        }
        return string.Join("\n", kept);
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
