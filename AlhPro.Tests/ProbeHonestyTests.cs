using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【2026-11-15 · 用户裁决「全部修理」】
/// 这一组测试只钉一件事:**探测结论与"没有显卡"这两件事,不许在任何一个页面被压成 bool**。
/// 背景(全部有实测证据):
/// ①图片页两处调 `IsWaifu2xNcnnUsableAsync`(单值壳),它把「显存不足跳过探测 / 被取消」也压成 false
///   ⇒ 没测成被当成测出不可用 ⇒ 静默切 ONNX(DirectML),而那是慢 20~60 倍的保命路。
/// ②`EsrganOnnxService.PickDevice` 里 `NoGpuDetected && !IsBlackwellGpu()` 是按显卡型号开后门:
///   名字不是 50 系的机器,只要 Vulkan 挂了(驱动升级未重启)就被判 CPU,哪怕 DirectML 实测可用。
/// ③`TargetGpuIsPlainNvidia` 末尾 `!IsBlackwell(name)` 让 50 系永远进不了免探测快速通道 —— 即使随包引擎
///   已经是 2026 重编版、多台真机实测通过,每台 50 系仍要白等一次 60 秒探测。
/// ④视频页 `engine == "waifu2x"` 的两块分支走不到(视频页只有 Real-ESRGAN / Real-CUGAN),
///   却会在日志里写「50 系 waifu2x 不可用」,把后来的排查人带偏。
/// 写法与仓库其它源码契约测试一致:读源码 + 断言关键字,不做字符串模糊匹配。</summary>
public class ProbeHonestyTests
{
    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            if (File.Exists(Path.Combine(d.FullName, "ImgUpscalerUI", "Views", "MainPage.xaml")))
                return d.FullName;
            d = d.Parent;
        }
        throw new InvalidOperationException("找不到仓库根目录");
    }

    private static string Read(params string[] parts)
    {
        var path = Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray());
        Assert.True(File.Exists(path), "文件不存在:" + path);
        return File.ReadAllText(path);
    }

    private static int Count(string src, string needle)
    {
        int n = 0, i = 0;
        while ((i = src.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    /// <summary>去掉注释再断言:本仓库的注释里会引用"被删掉的旧写法"(那是给后来人看的原因),
    /// 直接对全文做 DoesNotContain 会被自己的注释绊倒 —— 这条坑 2026-11-15 又踩了一次。</summary>
    private static string CodeOnly(string src)
    {
        var sb = new System.Text.StringBuilder(src.Length);
        bool line = false, block = false, str = false, chr = false;
        for (int i = 0; i < src.Length; i++)
        {
            char c = src[i];
            char next = i + 1 < src.Length ? src[i + 1] : '\0';
            if (line) { if (c == '\n') { line = false; sb.Append(c); } continue; }
            if (block) { if (c == '*' && next == '/') { block = false; i++; } continue; }
            if (!str && !chr && c == '/' && next == '/') { line = true; continue; }
            if (!str && !chr && c == '/' && next == '*') { block = true; i++; continue; }
            if (!chr && c == '"' && (i == 0 || src[i - 1] != '\\')) str = !str;
            else if (!str && c == '\'' && (i == 0 || src[i - 1] != '\\')) chr = !chr;
            sb.Append(c);
        }
        return sb.ToString();
    }

    [Fact]
    public void The_image_page_asks_for_a_three_state_verdict_not_a_bool()
    {
        var up = CodeOnly(Read("ImgUpscalerUI", "Views", "UpscaleView.xaml.cs"));

        // 单值壳一旦回到图片页,「没测成」就又会变成「不可用」⇒ 静默降级,用户只觉得越用越慢。
        Assert.DoesNotContain("IsWaifu2xNcnnUsableAsync", up);
        Assert.True(Count(up, "EnsureNcnnProbeDecisionAsync(\"waifu2x\", gpuId, null, ct)") >= 2,
            "图片页两处降噪路由都必须拿三态结论");
        Assert.True(Count(up, "NcnnProbeRouting.KeepsNcnn") >= 2, "三态结论必须经 KeepsNcnn 解释");
    }

    [Fact]
    public void The_bool_shell_is_deleted_so_nobody_can_collapse_not_measured_into_unusable()
    {
        var es = CodeOnly(Read("ImgUpscalerUI", "EngineService.cs"));
        Assert.DoesNotContain("public static async Task<bool> IsWaifu2xNcnnUsableAsync", es);
        // 三态入口必须还在(删壳不能顺手删了正主)
        Assert.Contains("EnsureNcnnProbeDecisionAsync", es);
        Assert.Contains("enum NcnnProbeReport", Read("AlhPro.Core", "NcnnModelVerdicts.cs"));
    }

    [Fact]
    public void The_no_gpu_decision_is_one_predicate_and_has_no_model_name_back_door()
    {
        var onnx = CodeOnly(Read("ImgUpscalerUI", "EsrganOnnxService.cs"));
        Assert.Contains("if (VulkanCheck.NoComputeDeviceDetected) return -1;", onnx);
        Assert.DoesNotContain("IsBlackwellGpu()", onnx);

        // 谓词本体只有一处定义,含义写死:Vulkan 不可用 **且** DirectML 也不可用。
        var vk = Read("ImgUpscalerUI", "VulkanCheck.cs");
        Assert.Contains("public static bool NoComputeDeviceDetected => NoGpuDetected && !DmlAvailable();", vk);
    }

    [Fact]
    public void The_fast_path_asks_whether_the_engine_is_rebuilt_not_whether_the_card_is_fifty_series()
    {
        var es = CodeOnly(Read("ImgUpscalerUI", "EngineService.cs"));
        Assert.Contains("|| EngineIsRebuilt2026(engine ?? string.Empty)", es);
        // 50 系能不能走快速通道,只允许由"引擎是不是 2026 重编版"这一条实测事实决定。
        Assert.DoesNotContain("&& !AlhPro.Core.GpuName.IsBlackwell(name);", es);
    }

    [Fact]
    public void The_video_path_keeps_no_waifu2x_only_branches()
    {
        var vs = CodeOnly(Read("ImgUpscalerUI", "VideoService.cs"));
        Assert.DoesNotContain("engine == \"waifu2x\" && EngineService.IsBlackwellGpu()", vs);
        // 视频页永远给不出 waifu2x ⇒ 这两处"50 系 waifu2x"日志必须是死代码,已删。
        Assert.DoesNotContain("50系 waifu2x", vs);
    }
}