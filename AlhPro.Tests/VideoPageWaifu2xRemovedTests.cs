using AlhPro.Core;
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【用户要求 2026-09-25】「**禁用吧 直接删掉在视频页面**」——
/// 视频页不再提供 waifu2x(引擎本身保留:图片页与既有管线仍在用)。
///
/// 本文件钉四件事(对应合同四条验收):
///   ① **入口消失**:视频页 XAML 里没有 waifu2x 单选项、没有 `VideoWaifu2xModelCombo` 及其 3 个模型项;
///      页面代码里也不再有任何对那个下拉的引用;
///   ② **存档兼容 = 显式迁移**(不是靠越界兜底):`EngineChoice.FromStored(0)` 明确落 Real-ESRGAN,
///      `IsKnownStored(0)` 仍为 true(老存档的合法旧值),`IsRetiredWaifu2x(0)` 才是"要迁移 + 记日志"的信号;
///      并且**值绝不会被回写成 0**(`ToStored(任意索引)` 都不返回 0);
///   ③ **图片页一行未动**:`UpscaleView` 的引擎/模型选择与降噪级别照旧(仍有 waifu2x);
///   ④ **引擎分支不删**:`VideoService` / `EngineService` 里的 waifu2x 分支仍在(它们同时服务图片页与既有管线)。</summary>
public class VideoPageWaifu2xRemovedTests
{
    // ───────────────────────── ① 视频页入口消失 ─────────────────────────

    [Fact]
    public void Video_page_has_no_waifu2x_entry_point()
    {
        string xaml = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml");
        // 引擎单选项:没有 waifu2x
        Assert.DoesNotContain("<RadioButton Content=\"waifu2x\"", xaml);
        // 模型下拉与它的 3 个模型项:整个删掉
        Assert.DoesNotContain("VideoWaifu2xModelCombo", xaml);
        Assert.DoesNotContain("Tag=\"models-cunet\"", xaml);
        Assert.DoesNotContain("models-upconv_7_anime_style_art_rgb", xaml);
        Assert.DoesNotContain("Tag=\"models-upconv_7_photo\"", xaml);
        // 引擎栏恰好两项,且顺序与 Core 常量逐项对得上(与 RealCuganContractTests 互为旁证)
        int at = xaml.IndexOf("x:Name=\"VideoEngineRadios\"", StringComparison.Ordinal);
        int end = xaml.IndexOf("</RadioButtons>", at, StringComparison.Ordinal);
        string block = xaml[at..end];
        var items = Regex.Matches(block, "<RadioButton\\s+Content=\"([^\"]*)\"");
        Assert.Equal(EngineChoice.VideoUiEngineCount, items.Count);
        Assert.Equal("realesrgan", EngineChoice.EngineNameOf(0));
        Assert.Equal(EngineChoice.EngineNameOf(0), items[0].Groups[1].Value == "Real-ESRGAN" ? "realesrgan" : "?");
        Assert.Equal(EngineChoice.EngineNameOf(1), EngineChoice.EngineNameOf(EngineChoice.UiRealCugan));
    }

    /// <summary>页面代码里不许再有那个下拉的任何引用(删了控件却留着引用 = 编译不过;这条是为了防"以后又加回来")。
    /// 【判据只查代码,先剥掉注释】我们**故意**在注释里写明了"原来是什么、为什么删"(那些字面量不该绊倒自己)。</summary>
    [Fact]
    public void Video_page_code_has_no_reference_to_the_waifu2x_combo()
    {
        string cs = StripCsharpComments(ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs"));
        Assert.DoesNotContain("VideoWaifu2xModelCombo", cs);
        Assert.DoesNotContain("UpWaifu2xModelName", cs);          // 那两个"模型显示名"辅助也跟着删了
        Assert.DoesNotContain("UiWaifu2x", cs);                   // 界面索引常量已删除(没有索引映射到 waifu2x)
        // 用户可见文案不许再"劝人换 waifu2x"(视频页没有那个档位)
        Assert.DoesNotContain("换用 waifu2x", cs);
        Assert.DoesNotContain("改用「waifu2x」", cs);
        Assert.DoesNotContain("或 waifu2x 后重试", cs);
    }

    // ───────────────────────── ② Engine=0 → 显式迁移 ─────────────────────────

    /// <summary>**定点复现「旧存档 Engine=0 → 读回 Real-ESRGAN + 有日志」**。
    /// 这里复现的是"读设置"那一步的**纯逻辑**部分(Core)+ 页面接线(源码断言):
    ///   · `IsKnownStored(0)` = true ⇒ 守卫放行(不会被当成损坏文件丢掉);
    ///   · `IsRetiredWaifu2x(0)` = true ⇒ 页面写一行可读日志;
    ///   · `FromStored(0)` = `UiRealEsrgan` ⇒ 界面落在 Real-ESRGAN;
    ///   · `ToStored(UiRealEsrgan)` = 1 ⇒ 下次保存写 1(**绝不会把 0 写回去**);
    ///   · 日志原文里既有 "waifu2x 已移除" 又有 "改用 Real-ESRGAN"(用户看得懂发生了什么)。</summary>
    [Fact]
    public void Stored_engine_zero_migrates_to_real_esrgan_with_a_readable_message()
    {
        // —— 模拟一份老存档:Engine=0(waifu2x)——
        int stored = 0;
        Assert.True(EngineChoice.IsRetiredWaifu2x(stored), "0 必须被识别成'已退役的 waifu2x'");
        Assert.True(EngineChoice.IsKnownStored(stored), "0 是老存档的合法旧值 ⇒ 守卫必须放行(否则被当成坏文件)");

        // 读回:界面落 Real-ESRGAN
        int ui = EngineChoice.FromStored(stored);
        Assert.Equal(EngineChoice.UiRealEsrgan, ui);
        Assert.Equal("realesrgan", EngineChoice.EngineNameOf(ui));

        // 再保存:写出去的是 1,不是 0
        Assert.Equal(EngineChoice.StoredRealEsrgan, EngineChoice.ToStored(ui));
        for (int anyUi = -1; anyUi <= 5; anyUi++)
            Assert.NotEqual(EngineChoice.StoredWaifu2x, EngineChoice.ToStored(anyUi));

        // 日志原文(含结论;页面必须原样写它)
        Assert.Contains("waifu2x", EngineChoice.Waifu2xRetiredNotice);
        Assert.Contains("已移除", EngineChoice.Waifu2xRetiredNotice);
        Assert.Contains("Real-ESRGAN", EngineChoice.Waifu2xRetiredNotice);
        string cs = StripCsharpComments(ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs"));
        Assert.Contains("if (AlhPro.Core.EngineChoice.IsRetiredWaifu2x(d.Engine))", cs);
        Assert.Contains("AppLogger.Info($\"[记忆] {AlhPro.Core.EngineChoice.Waifu2xRetiredNotice}", cs);
        Assert.Contains("Log(\"ℹ \" + AlhPro.Core.EngineChoice.Waifu2xRetiredNotice)", cs);
        // 迁移之后紧接着的赋值必须走 EngineFromStored(不许各写一份)
        Assert.Contains("VideoEngineRadios.SelectedIndex = EngineFromStored(d.Engine);", cs);
    }

    // ───────────────────────── ③ 图片页一行未动 ─────────────────────────

    /// <summary>图片页的 waifu2x 照常可用:它的模式单选(Real-ESRGAN / waifu2x)、模型表与降噪级别都还在,
    /// 而且**没有**被改去用 `EngineChoice`(那是视频页的存盘口径,图片页一直用自己的两模式约定)。</summary>
    [Fact]
    public void Image_page_still_offers_waifu2x_untouched()
    {
        string cs = ReadRepoFile("ImgUpscalerUI", "Views", "UpscaleView.xaml.cs");
        Assert.Contains("PopulateModelCombo(isAnime: true);", cs);              // 动漫模式 = waifu2x 模型表
        Assert.Contains("EngineService.AnimeModels", cs);
        Assert.Contains("EngineService.PhotoModels", cs);
        Assert.Contains("ModeRadios.SelectedIndex == 1", cs);                   // 两个模式单选:0=Real-ESRGAN 1=waifu2x
        Assert.Contains("NoiseCombo.SelectedIndex = 0;", cs);                   // waifu2x 的降噪级别
        Assert.Contains("NoiseCombo.IsEnabled = isAnime;", cs);
        Assert.DoesNotContain("EngineChoice", cs);                              // 图片页不掺视频页的存盘口径
        // 图片页的两个文件不在 t45 的改动面里(用 git 工作区状态旁证由 output 给出;这里只钉源码事实)
        string xaml = ReadRepoFile("ImgUpscalerUI", "Views", "UpscaleView.xaml");
        Assert.Contains("ModeRadios", xaml);
        Assert.Contains("NoiseCombo", xaml);
    }

    // ───────────────────────── ④ 引擎分支不删 ─────────────────────────

    /// <summary>本轮只删视频页入口:`VideoService` / `EngineService` 里的 waifu2x 分支必须仍在
    /// (图片页与既有管线都依赖它们;删了会让图片页的 waifu2x 直接失效)。</summary>
    [Fact]
    public void Engine_branches_for_waifu2x_are_still_there()
    {
        string svc = ReadRepoFile("ImgUpscalerUI", "VideoService.cs");
        Assert.Contains("\"waifu2x\"", svc);                       // 视频管线仍认这支引擎(旧预设/程序内调用)
        Assert.Contains("waifu2xNoiseArg", svc);                    // 它自带的降噪档映射仍在
        string eng = ReadRepoFile("ImgUpscalerUI", "EngineService.cs");
        Assert.Contains("FindWaifu2x", eng);
        Assert.Contains("ShouldUseOnnxWaifu2x", eng);
        Assert.Contains("AnimeModels", eng);
        // 图片页的引擎默认值也仍在(waifu2x 是它的动漫模式)
        string up = ReadRepoFile("ImgUpscalerUI", "Views", "UpscaleView.xaml.cs");
        Assert.Contains("waifu2x", up, StringComparison.OrdinalIgnoreCase);
    }

    // ───────────────────────── 工具 ─────────────────────────

    private static string StripCsharpComments(string src)
    {
        // 只剥 `//` 行注释:块注释会把字符串里的 /* 也吃掉(本仓库踩过)。
        var lines = src.Split('\n');
        var kept = lines.Select(l =>
        {
            int idx = l.IndexOf("//", StringComparison.Ordinal);
            return idx >= 0 ? l[..idx] : l;
        });
        return string.Join('\n', kept);
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
