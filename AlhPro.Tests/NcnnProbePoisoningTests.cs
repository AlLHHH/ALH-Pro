using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【2026-09-24 真机实测挖出来的性能回归】视频 2x 超分被静默推到 ONNX 慢路(实测 3880 ms/帧,
/// 而软件自己标称 animevideov3 是 0.26~0.30 秒/帧 —— 差 13 倍),链条是:
///
///   ① 视频页「动漫 · Anime4K 修复(快)」那一项在 XAML 里的 Tag 是 <c>anime4k</c>,而它实际走的是
///      ffmpeg + libplacebo 着色器(不经过 ncnn);
///   ② 视频预检把"当前选中的 Tag"**原样**当模型名传给 `EnsureNcnnProbeAsync` ⇒ ncnn 被要求加载一个
///      不存在的模型 ⇒ 60 秒无响应被强杀 ⇒ 落一条 `realesrgan2026|0|anime4k = false`(假失败);
///   ③ 本机(纯 N 卡 + 核显)走不了"免探测快速通道",且每次探测都带模型 ⇒ **从不写 default 结论**;
///   ④ 于是 `TryGetEngineVerdictSummary` 落到旧兜底"任一支失败即整条引擎不可用" ⇒ realesrgan 整体判走 ONNX。
///      同一轮日志里那支真模型的探测**明明是通过的**(`realesrgan2026|0|realesr-animevideov3 = true`)。
///
/// 这里钉住修复后的三条契约(纯逻辑判据在 NcnnModelVerdictsTests,这里钉"接线不许再接错"):
///   ① 探测调用点一律先过 `EngineService.NcnnProbeModel`,不许把界面 Tag 原样喂进去;
///   ② 兜底口径只统计真 ncnn 模型(伪模型不参与);
///   ③ 伪模型清单只认 anime4k —— 官方/自训模型名不许被误判成伪模型(否则真会被跳过)。
/// **诚实边界**:契约只能证明接线与判据,证明不了"这条卡上确实快回来了" —— 那由真机跑一次 2x 得到
/// (日志里应出现「使用 ncnn-Vulkan」且每帧耗时回到百毫秒级)。</summary>
public class NcnnProbePoisoningTests
{
    /// <summary>★ 所有把"界面选中的模型"喂给 ncnn 探测的地方,都必须先过探测计划(NcnnProbePlan/NcnnProbeModel)。</summary>
    [Fact]
    public void Probe_call_sites_map_the_ui_tag_before_probing()
    {
        var video = ReadRepoFile("ImgUpscalerUI", "VideoService.cs");
        var videoView = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
        var upView = ReadRepoFile("ImgUpscalerUI", "Views", "UpscaleView.xaml.cs");

        Assert.Contains("AlhPro.Core.NcnnProbePlan.For(model)", video);
        Assert.Contains("EnsureNcnnProbeAsync(engine, gpuId, upProbePlan.Model, ct)", video);
        Assert.Contains("AlhPro.Core.NcnnProbePlan.For(model)", videoView);
        Assert.Contains("EnsureNcnnProbeAsync(\"realesrgan\", gpuId, probePlan.Model, cts.Token)", videoView);
        Assert.Contains("AlhPro.Core.NcnnProbePlan.For(model)", upView);

        // 旧写法(把界面 Tag 原样喂进去)必须再也搜不到
        Assert.DoesNotContain("EnsureNcnnProbeAsync(engine, gpuId, model, ct)", video);
        Assert.DoesNotContain("EnsureNcnnProbeAsync(\"waifu2x\", gpuId, model, ct)", video);
        Assert.DoesNotContain("NcnnProbeWillRun(\"realesrgan\", gpuId, SelModel(", videoView);
        Assert.DoesNotContain("EnsureNcnnProbeAsync(\"realesrgan\", gpuId,\n                SelModel(", videoView);
    }

    /// <summary>★ 兜底汇总只统计真 ncnn 模型;并且 EngineService 不许再用"全部结论一起 Summarize"。</summary>
    [Fact]
    public void The_fallback_summary_ignores_pseudo_models()
    {
        var svc = ReadRepoFile("ImgUpscalerUI", "EngineService.cs");
        Assert.Contains("NcnnModelVerdicts.ModelOnlyRisk(", svc);
        Assert.DoesNotContain("return AlhPro.Core.NcnnVerdictKey.Summarize(oks);", svc);
        // 旧的两段式便捷入口(伪模型→null=探默认)已删除:调用点全部改用 NcnnProbePlan
        Assert.DoesNotContain("public static string? NcnnProbeModel(", svc);
    }

    /// <summary>★ 探测计划的三条规则(纯逻辑;两支"界面专用 Tag"都必须被处理,别只处理 anime4k)。</summary>
    [Fact]
    public void The_probe_plan_handles_every_ui_only_tag()
    {
        // ① 走着色器的条目:不探测(以前会被当模型名喂给 ncnn ⇒ 60 秒超时 ⇒ 假失败结论)
        var a4k = AlhPro.Core.NcnnProbePlan.For(AlhPro.Core.Anime4k.ModelTag);
        Assert.False(a4k.ShouldProbe);
        Assert.Null(a4k.Model);

        // ② 「现实 · 1x 修复」的 Tag 不是真模型 ⇒ 用真权重去探(review 抓到的第二个洞)
        var real1x = AlhPro.Core.NcnnProbePlan.For(AlhPro.Core.Upscale1x.RealTag);
        Assert.True(real1x.ShouldProbe);
        Assert.Equal(AlhPro.Core.Upscale1x.RealEngineModel, real1x.Model);
        Assert.Equal("alhpro-real2x", real1x.Model);   // 与 ExperimentalEsrgan.Real2x 同值(改这里就说明换了权重)

        // ③ 真模型/未指定:原样透传(未指定 = 探引擎默认模型)
        Assert.Equal("realesr-animevideov3", AlhPro.Core.NcnnProbePlan.For("realesr-animevideov3").Model);
        Assert.True(AlhPro.Core.NcnnProbePlan.For(null).ShouldProbe);
        Assert.Null(AlhPro.Core.NcnnProbePlan.For(null).Model);
    }

    /// <summary>★ 1x Anime4K 回退到「现实 · 1x 修复」时,下发给引擎的模型名必须同步换成真权重
    /// (否则引擎按 `-n anime4k` 找一个不存在的权重 ⇒ exit 0 只出坏帧;此前被"预检必然失败 ⇒ 整批走 ONNX"挡住)。</summary>
    [Fact]
    public void The_anime4k_fallback_rewrites_the_engine_model()
    {
        var cs = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
        Assert.Contains("model = AlhPro.Core.Upscale1x.RealEngineModel;", cs);
    }

    /// <summary>★ 伪模型清单用常量而不是字面量:Tag 改名时清单跟着走(否则静默失配、毒化回归)。</summary>
    [Fact]
    public void The_pseudo_model_list_is_wired_to_the_core_constants()
    {
        var core = ReadRepoFile("AlhPro.Core", "NcnnModelVerdicts.cs");
        Assert.Contains("Anime4k.ModelTag", core);
        Assert.Contains("Upscale1x.RealTag", core);
        Assert.DoesNotContain("NonNcnnModels = { \"anime4k\"", core);

        // 两支界面 Tag 都要被判成"伪模型"(旧缓存/旧结论里可能已经有它们的失败行)
        Assert.True(AlhPro.Core.NcnnModelVerdicts.IsNonNcnnModel(AlhPro.Core.Anime4k.ModelTag));
        Assert.True(AlhPro.Core.NcnnModelVerdicts.IsNonNcnnModel(AlhPro.Core.Upscale1x.RealTag));
    }

    /// <summary>★ 官方三支 + 自训三支 + 1x 现实**真正下发的那支权重** + waifu2x 各档都必须判为真模型。</summary>
    [Fact]
    public void Real_ncnn_model_names_are_not_treated_as_pseudo_models()
    {
        foreach (var m in new[]
                 {
                     "realesrgan-x4plus", "realesrgan-x4plus-anime", "realesr-animevideov3",
                     "realesr-animevideov3-x2", "realesr-general-x4v3", "realesr-general-wdn-x4v3",
                     "alhpro-real2x", "alhpro-game2x-v2", "alhpro-game2x-v3",
                     "models-cunet", "models-upconv_7_photo", null, "", "  ",
                 })
            Assert.False(AlhPro.Core.NcnnModelVerdicts.IsNonNcnnModel(m), $"被误判成伪模型:{m}");
        // 1x 现实真正下发的那支 = 真模型(只有"Tag"是伪的)
        Assert.False(AlhPro.Core.NcnnModelVerdicts.IsNonNcnnModel(AlhPro.Core.Upscale1x.RealEngineModel));
    }

    /// <summary>★ 界面里那两支 Tag 必须与 Core 常量逐字一致(用常量断言,不再靠裸字符串)。</summary>
    [Fact]
    public void The_ui_tags_still_match_the_core_constants()
    {
        var xaml = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml");
        Assert.Contains($"Tag=\"{AlhPro.Core.Anime4k.ModelTag}\"", xaml);
        Assert.Contains($"Tag=\"{AlhPro.Core.Upscale1x.RealTag}\"", xaml);
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
