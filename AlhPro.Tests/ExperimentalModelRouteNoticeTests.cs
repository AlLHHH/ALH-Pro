using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace AlhPro.Tests;

/// <summary>「自训模型只有 ncnn 权重,走 ONNX 时会被换成官方模型 —— 必须**明确告知**,不许闷声换」的契约
/// (2026-09-22 用户追问"新模型 / 自训那几支在 AMD / N 卡上的兼容性"时,查出来的真缺口)。
///
/// 【缺口长什么样】`AlhPro.Core.ExperimentalEsrgan.OnnxFallbackNotice` 那句"本机走的稳定引擎(ONNX)里没有
/// 这支自训模型(它只有 ncnn 权重),本批是按另一支官方模型处理…"**一直写着、也有单测**,
/// 但**全仓库没有任何地方调用它** ⇒ 实际行为是:用户在下拉里选了自训模型、机器走 ONNX,
/// 程序静默换成 RealESRGAN_x4plus.onnx(没有就 animevideov3.onnx)跑完,一句提示都没有 ✗。
/// 而 `RELEASE_NOTES.md` 与官网 changelog 都写着"软件会明确告诉你…**不会闷声换模型**" ——
/// 承诺与行为正好相反,也违反本仓库"静默换模型是明令禁止的"这条规矩。
///
/// 【本契约钉四件事】①文案本体仍在 Core(不许各处自写一份措辞);
/// ②视频路径与图片路径都必须**真的调用**它(这是缺口本身:定义了 ≠ 接上了);
/// ③用户在下拉旁就要能看到(选之前,而不是处理时);
/// ④自检报告里必须单列"自训模型能不能用"(以前只报引擎级,用户看不出自己选的那支行不行)。</summary>
public class ExperimentalModelRouteNoticeTests
{
    private static string VideoSvc => ReadRepoFile("ImgUpscalerUI", "VideoService.cs");
    private static string UpscaleView => ReadRepoFile("ImgUpscalerUI", "Views", "UpscaleView.xaml.cs");
    private static string VideoView => ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
    private static string VulkanCheck => ReadRepoFile("ImgUpscalerUI", "VulkanCheck.cs");
    private static string CoreExperiment => ReadRepoFile("AlhPro.Core", "ExperimentalEsrgan.cs");

    /// <summary>① 文案唯一出处仍在 Core,且仍然说清"只有 ncnn 权重 / 按另一支官方模型处理"。</summary>
    [Fact]
    public void The_notice_text_stays_in_core_and_still_says_the_two_key_facts()
    {
        Assert.Contains("public static string OnnxFallbackNotice(", CoreExperiment);
        Assert.Contains("只有 ncnn 权重", CoreExperiment);
        Assert.Contains("另一支官方模型", CoreExperiment);
    }

    /// <summary>② 视频路径与图片路径都必须调用它 —— 只定义不调用就是当初那个缺口。</summary>
    [Fact]
    public void Both_the_video_and_image_paths_actually_use_it()
    {
        Assert.Contains("ExperimentalEsrgan.OnnxFallbackNotice(model)", VideoSvc);
        Assert.Contains("ExperimentalEsrgan.IsExperimental(model)", VideoSvc);
        Assert.Contains("ExperimentalEsrgan.OnnxFallbackNotice(model)", UpscaleView);
        Assert.Contains("ExperimentalEsrgan.IsExperimental(model)", UpscaleView);
        // 图片路径把"被忽略的选项"集中列出来给用户看:自训模型必须算进这一类
        Assert.Contains("这支只有 ncnn 权重", UpscaleView);
    }

    /// <summary>③ 下拉旁那条提示必须提前说(选之前),而且只在**本机真会走 ONNX** 时才说。</summary>
    [Fact]
    public void The_dropdown_hint_warns_before_the_user_runs_it()
    {
        var m = Regex.Match(VideoView, @"else if \(experimentalModel\)\s*\{(?<body>.*?)\n            \}",
                            RegexOptions.Singleline);
        Assert.True(m.Success, "找不到自训模型的提示分支");
        var body = m.Groups["body"].Value;
        Assert.Contains("OnnxUpscaleRouteLikely()", body);
        Assert.Contains("只有 ncnn 权重,用不上", body);
    }

    /// <summary>④ 自检报告必须单列一条自训模型的可用性(按真机实测结论说,不按显卡型号猜)。</summary>
    [Fact]
    public void The_self_check_report_lists_the_self_trained_models()
    {
        Assert.Contains("自训模型(现实 · alhreal2x / 游戏 · alhgame2x-v2 / -v3)", VulkanCheck);
        Assert.Contains("这三支只有 ncnn 权重", VulkanCheck);
        Assert.Contains("这三支没有 ONNX 权重", VulkanCheck);
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
