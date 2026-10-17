using System;
using System.IO;
using System.Linq;
using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>
/// 2026-10-15 真机事故的钉子:补帧(RIFE)探测结论的"尺寸档"必须把**实测故障带**单独分出来。
/// <para>
/// 事故链条(diagnostic.log + ncnn-probe.txt 双证据):
/// ① 一趟 5760×5760(33.2 Mpx)的任务在故障带里探测 ncnn ⇒ 记下"出图但画面坏帧"(判决本身没错);
/// ② 旧键只有 sd/fhd/uhd 三档,5760×5760 与 3840×2160 同属 uhd ⇒ 这条结论被 4K 任务沿用;
/// ③ 于是之后所有 4K 补帧都"沿用已缓存结论:不可用 → 走 ONNX" —— 而 4K(8.3 Mpx)上 ncnn 本来是好的,
///    用户感受就是"补帧突然慢得离谱"。
/// </para>
/// </summary>
public class RifeProbeSizeBucketTests
{
    [Theory]
    [InlineData(320, 240, "sd")]                 // 探测兜底尺寸
    [InlineData(1280, 720, "sd")]                // 门槛没变:旧口径就是"到 1920×1080 才算 fhd"
    [InlineData(1920, 1080, "fhd")]
    [InlineData(3840, 2160, "uhd4k")]            // 实测安全点(ncnn 0 黑帧)
    public void SizeBucket_maps_normal_frame_sizes(int w, int h, string expected)
        => Assert.Equal(expected, NcnnVerdictKey.SizeBucket((long)w * h));

    [Fact]
    public void SizeBucket_keeps_the_defect_band_out_of_the_4k_bucket()
    {
        // 核心回归:5760×5760 = 33.18 Mpx ≥ InterpSizePolicy.NcnnRefusePixels(33 Mpx,实测故障带),
        // 它绝不能和 3840×2160 共用一个档,否则一次大尺寸坏帧会把之后所有 4K 补帧拖去 ONNX。
        Assert.NotEqual(NcnnVerdictKey.SizeBucket(5760L * 5760), NcnnVerdictKey.SizeBucket(3840L * 2160));
        Assert.Equal("uhd8k", NcnnVerdictKey.SizeBucket(5760L * 5760));
        Assert.Equal("uhd8k", NcnnVerdictKey.SizeBucket(7680L * 4320));
        // 档位边界与"按尺寸拒跑 ncnn"的判据同一个常量:两者不能各说各话
        Assert.Equal("uhd4k", NcnnVerdictKey.SizeBucket(InterpSizePolicy.NcnnRefusePixels - 1));
        Assert.Equal("uhd8k", NcnnVerdictKey.SizeBucket(InterpSizePolicy.NcnnRefusePixels));
    }

    [Fact]
    public void SizeBucket_never_emits_the_legacy_uhd_bucket()
    {
        // 旧版本把 4K~33 Mpx 全写成 "uhd":那批结论的探测尺寸不可知(可能是 4K,也可能是 25 Mpx),
        // 一律不再命中 ⇒ 在真实尺寸上重探一次即自愈,且新结论写在 uhd4k 上、不会反复重探。
        Assert.NotEqual("uhd", NcnnVerdictKey.SizeBucket(3840L * 2160));
        Assert.NotEqual("uhd", NcnnVerdictKey.SizeBucket(5760L * 5760));
        Assert.NotEqual("uhd", NcnnVerdictKey.SizeBucket(InterpSizePolicy.NcnnRefusePixels - 1));
    }

    [Fact]
    public void Rife_probe_key_delegates_to_the_shared_bucket_function()
    {
        var eng = ReadRepoFile("ImgUpscalerUI", "EngineService.cs");
        int start = eng.IndexOf("private static string RifeProbeKey(", StringComparison.Ordinal);
        Assert.True(start > 0, "找不到 RifeProbeKey");
        var body = eng.Substring(start, 600);
        Assert.Contains("AlhPro.Core.NcnnVerdictKey.SizeBucket(area)", body);
        // 不许再自己写一套三档判断(那次 5760×5760 事故就是它造成的)
        Assert.DoesNotContain("area >= 3840L * 2160 ? \"uhd\"", body);
    }

    [Fact]
    public void Defect_band_is_never_probed_and_records_nothing()
    {
        var eng = ReadRepoFile("ImgUpscalerUI", "EngineService.cs");
        int fn = eng.IndexOf("public static async Task<bool> EnsureRifeNcnnProbeAsync(", StringComparison.Ordinal);
        Assert.True(fn > 0, "找不到 EnsureRifeNcnnProbeAsync");
        int end = eng.IndexOf("private static string RifeProbeKey(", fn, StringComparison.Ordinal);
        var body = eng.Substring(fn, end - fn);
        // 故障带里直接返回"别走 ncnn",并且这条 return 必须在落盘之前(不许留下一份"这张卡不可用"的假判决)
        Assert.Contains("rifeSizeVerdict.RefuseNcnn", body);
        int skip = body.IndexOf("rifeSizeVerdict.RefuseNcnn", StringComparison.Ordinal);
        int save = body.IndexOf("SaveNcnnVerdict(", StringComparison.Ordinal);
        Assert.True(save > skip, "故障带短路必须发生在写缓存之前");
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
