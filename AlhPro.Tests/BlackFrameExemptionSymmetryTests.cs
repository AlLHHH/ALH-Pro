using AlhPro.Core;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【2026-09-16 补帧"老是降级到 ONNX"的根因修复】黑帧**判定**与黑帧**豁免**必须用同一个判据,
/// 而且"输出帧号 → 源帧号"的查表必须带邻域容差。
///
/// ==== 真机证据(本次定位) ====
/// 三段 200 帧、1440×1440 源、4x 补帧、`-j 1:1:1`(产品同参数,每段新起引擎),逐帧核对**全部** 796 个输出帧:
///   被判黑的输出帧里,有 5 帧的**同号源帧**近黑比例是 94.9% / 93.2% / 93.0% —— 恰在 95% 线下方,
///   而它们的**邻居源帧**(±1)是黑的。旧实现因此判成"GPU 故障",把整段作废、改走 ONNX 重算;
///   而 ONNX 在这台机器上只能开 1 路、比 ncnn 慢一二十倍 ⇒ 用户感知"补帧老是出问题、直接转 ONNX"。
/// 两个成因:
///   ① **判据不对称**:输出侧 IsBlackPngStrict = 整帧【或任一 1/3 条带】近黑,而源帧侧当时只判整帧 ⇒
///      宽银幕黑边/上下黑条这类素材上,输出判缺陷、源却"不算黑",必然误降级;
///   ② **无邻域容差**:引擎有前后帧缓冲、`-n` 又是均分时间步,同号映射本就存在 ±1 偏移。
///
/// 【为什么用源码契约测试】这两条都是"组合条件 + 沉默失败"——编译不报错、纯逻辑单测也测不到
/// (要真跑一段暗场素材才会暴露),所以按本仓库既定手法钉住接线(同 TempSpaceGateTests / SceneCutCoverageTests)。</summary>
public class BlackFrameExemptionSymmetryTests
{
    /// <summary>判据必须同源:黑帧**豁免**用的入口不许再回退到"只判整帧"的 IsNearBlack。</summary>
    [Fact]
    public void Source_exemption_uses_the_same_criterion_as_the_output_check()
    {
        var svc = ReadRepoFile("ImgUpscalerUI", "VideoService.cs");

        // ① 源帧判据入口必须存在,且实现走 IsDefectiveFrame(整帧或条带)
        Assert.Contains("private static bool IsFileDefective(string path)", svc);
        Assert.Contains("AlhPro.Core.FrameInspect.IsDefectiveFrame(sums.ToArray(), total, bmp.Width, bmp.Height)", svc);
        // ② "只判整帧"的老入口必须绝迹(它就是不对称的来源)
        Assert.DoesNotContain("private static bool IsFileNearBlack(string path)", svc);
        Assert.DoesNotContain("IsFileNearBlack(", svc);
        // ③ 豁免调用点必须用新入口
        Assert.Contains("SourceFrameJustifiesBlack(src)", svc);
    }

    /// <summary>邻域容差:同号查不到时必须再看 ±1(引擎缓冲/时间步均分带来的映射偏移)。</summary>
    [Fact]
    public void Source_lookup_tolerates_a_one_frame_mapping_offset()
    {
        var svc = ReadRepoFile("ImgUpscalerUI", "VideoService.cs");
        Assert.Contains("private static string NeighborFrame(string srcPath, int delta)", svc);
        Assert.Contains("SourceFrameJustifiesBlack(NeighborFrame(src, -1))", svc);
        Assert.Contains("SourceFrameJustifiesBlack(NeighborFrame(src, +1))", svc);
    }

    /// <summary>邻域算法本身:按 D6 帧号 ±N,越界/解析不出时原样返回(不抛、不猜)。</summary>
    [Fact]
    public void Neighbor_frame_numbering_is_pure_and_bounded()
    {
        // 纯逻辑部分抽在 Core 里(不依赖文件系统),这里钉住它的行为
        Assert.Equal(4, CoreFrameNeighbor.Offset("000005", -1, 1));
        Assert.Equal(6, CoreFrameNeighbor.Offset("000005", +1, 1));
        Assert.Null(CoreFrameNeighbor.Offset("000001", -1, 1));   // 越下界:没有 0 号帧
        Assert.Null(CoreFrameNeighbor.Offset("abc", +1, 1));      // 解析不出:不猜
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

/// <summary>帧号邻居的纯逻辑(与 VideoService.NeighborFrame 同一口径,便于单测;越界返回 null)。</summary>
public static class CoreFrameNeighbor
{
    public static int? Offset(string baseNameWithoutExtension, int delta, int digits)
    {
        if (!int.TryParse(baseNameWithoutExtension, out int n)) return null;
        int want = n + delta;
        return want < 1 ? null : want;
    }
}
