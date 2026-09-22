using AlhPro.Core;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>**接线契约**:1x 修复档(Anime4K)必须真的接进流水线与流水线调用点。
/// 【为什么要这么测】这条能力横跨三处,任何一处漏了都**不报错、只是悄悄没生效**(或者悄悄走了旧行为):
///   ① 合帧滤镜链里没加滤镜 ⇒ 1x 档什么都不做(画面=原片,用户以为"修复开了没用");
///   ② VideoService 没收到 anime4k1x ⇒ 同上;
///   ③ 探测结果没用来翻 upscaleShrink1x ⇒ 变成"既跑 2x 超分又不加滤镜"(白花几倍时间)。
/// 端到端真跑一次很难自动化(要在界面上选文件、点开始),所以这里按本仓库既有做法,把源码里的接线钉住。
/// 真机实测(用我们自己的 ffmpeg 跑同一条滤镜串)见 `_qa\anime4k_check.py` 与 `_qa\降噪整改` 同级的记录。</summary>
public class Anime4kWiringTests
{
    [Fact]
    public void VideoService_inserts_the_shader_into_the_filter_chain()
    {
        var src = ReadFile("ImgUpscalerUI", "VideoService.cs");
        Assert.Contains("if (anime4k1x)", src);
        Assert.Contains("preParts.Add(AlhPro.Core.Anime4k.FilterArgument());", src);
        // 必须加在**滤镜链最前**(先修复,再用户的后处理)
        int anime = src.IndexOf("preParts.Add(AlhPro.Core.Anime4k.FilterArgument());", StringComparison.Ordinal);
        int post = src.IndexOf("var postFilter = AlhPro.Core.VideoPostFilters.Build(", StringComparison.Ordinal);
        Assert.True(anime > 0 && post > anime, "Anime4K 必须插在后处理之前(先修复再锐化)");
    }

    [Fact]
    public void Job_start_probes_and_flips_the_flags()
    {
        var src = ReadFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
        // ① 选了 1x 才探;② 探过就翻成 anime4k1x + 关掉旧的 shrink1x;③ 探不过就回退
        Assert.Contains("await EngineService.EnsureAnime4kProbeAsync(", src);
        Assert.Contains("anime4k1x = true;", src);
        Assert.Contains("upscaleShrink1x = false;", src);
        Assert.Contains("anime4k1x = false;", src);
        Assert.Contains("upscaleShrink1x = true;", src);   // 回退那支
        // ④ 必须把开关传给流水线,否则前面全白做
        Assert.Contains("anime4k1x: anime4k1x,", src);
    }

    /// <summary>1x 档的界面文案必须点明 Anime4K(用户定案:整档换掉,不再是"2x 放大后缩回")。</summary>
    [Fact]
    public void One_x_radio_says_Anime4K()
    {
        var xaml = ReadFile("ImgUpscalerUI", "Views", "VideoView.xaml");
        int at = xaml.IndexOf("x:Name=\"VScale1xRadio\"", StringComparison.Ordinal);
        Assert.True(at > 0, "XAML 里找不到 VScale1xRadio");
        var block = xaml.Substring(at, Math.Min(700, xaml.Length - at));
        Assert.Contains("Anime4K", block);
        Assert.DoesNotContain("1x 超分（2x 放大后缩回）", block);   // 旧文案不许回来
        // 【2026-09-21 用户:"提示太长了 导致界面被下移"】实测数字不再堆在这条悬停提示里
        //   (改放桌面对比图与报告)⇒ 这里改钉**对用户有用的那一条**:必须写明硬件要求与回退行为。
        Assert.Contains("Vulkan", block);
        // 【2026-09-21 更新】回退目标变了:原来是"退回旧的 2x 放大后缩回",现在退回的是
        //   **列表里那一支正式条目「现实 · 1x 修复」**(它的内部就是"用自训模型 2x 跑再缩回")
        //   ⇒ 文案与断言一起改(旧措辞"自动退回旧做法"已不存在)。同时钉住"这条提示要说明是**二选一**",
        //   因为 1x 现在有两条(只讲 Anime4K 会让用户以为没有别的选择)。
        Assert.Contains("现实 · 1x 修复", block);
        Assert.Contains("两个条目二选一", block);
    }

    /// <summary>**1x 档的模型可选性**(2026-09-21 用户定案:"选倍率后 不支持的模型就灰掉"、"1x 也是可以选模型")。
    /// 规则(用户选的 A 方案):**只灰 1x 这一档** ——
    ///   · 1x   ⇒ 只有两个 1x 修复条目可选(动漫 · Anime4K 修复 / 现实 · 1x 修复),其余放大模型全部置灰;
    ///   · 2x+  ⇒ 反过来:放大模型全可选(**不减少任何现有能力**),两个 1x 条目置灰;
    ///   · 切换倍率时当前选中项若不可用 ⇒ 自动切到该档"上次用的那支",并写日志(不静默)。
    /// 【为什么必须钉】灰错了不会报错:只会在 1x 下让用户选到一个"根本不放大"的放大模型(白跑一遍还看不出问题)。</summary>
    [Fact]
    public void One_x_only_allows_the_1x_entries_and_vice_versa()
    {
        var cs = ReadFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
        Assert.Contains("private void UpdateModelScaleAvailability()", cs);
        // 判据来自 Core 的唯一源(不写死 Tag 字面量)
        Assert.Contains("AlhPro.Core.Upscale1x.Is1xEntry(", cs);
        // 1x 只留 1x 条目、2x+ 只留放大模型(对称)
        Assert.Contains("bool usable = is1x ? is1xEntry : !is1xEntry;", cs);
        // 只置灰**已存在**的项,绝不碰集合(碰 Items 会触发 WinRT E_INVALIDARG 崩溃,踩过)
        Assert.DoesNotContain("VideoEsrganModelCombo.Items.Add(", cs);
        Assert.DoesNotContain("VideoEsrganModelCombo.Items.Remove", cs);
        Assert.DoesNotContain("VideoEsrganModelCombo.Items.IndexOf(", cs);
        // 自动切换 + 日志(不许静默改用户的选择)
        Assert.Contains("_lastUpscaleModelIndex", cs);
        Assert.Contains("_last1xModelIndex", cs);
        Assert.Contains("模型已自动从", cs);
    }

    /// <summary>「现实 · 1x 修复」**不是真模型**:必须把 Tag 映射成真正的权重名(alhreal2x)再下发引擎,
    /// 否则引擎会去找一个不存在的模型 —— 找不到权重时 exit=0、只出坏帧(本仓库踩过)。</summary>
    [Fact]
    public void Real_1x_maps_to_a_real_engine_model()
    {
        Assert.Equal("alhpro-real2x", AlhPro.Core.Upscale1x.RealEngineModel);
        Assert.Contains("AlhPro.Core.Upscale1x.RealEngineModel", ReadFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs"));
        // 两个 1x 条目都要能被 Core 认出来
        Assert.True(AlhPro.Core.Upscale1x.Is1xEntry("anime4k"));
        Assert.True(AlhPro.Core.Upscale1x.Is1xEntry("alhpro-real1x"));
        Assert.False(AlhPro.Core.Upscale1x.Is1xEntry("realesrgan-x4plus"));
        // 只有 Anime4K 那条走着色器(现实那条走 2x 超分再缩回)
        Assert.True(AlhPro.Core.Upscale1x.UsesAnime4kShader("anime4k"));
        Assert.False(AlhPro.Core.Upscale1x.UsesAnime4kShader("alhpro-real1x"));
    }

    /// <summary>**倍率段必须排在模型段之前**(用户 2026-09-21:"倍率放在模型上面")——
    /// 先选倍率、再看哪些模型可用,这是这次的交互用意;排反了用户会先选到不支持的模型再被改掉。</summary>
    [Fact]
    public void Scale_section_comes_before_the_model_section()
    {
        var xaml = ReadFile("ImgUpscalerUI", "Views", "VideoView.xaml");
        int scale = xaml.IndexOf("Text=\"放大倍数\"", StringComparison.Ordinal);
        int model = xaml.IndexOf("x:Name=\"VideoModelLabel\"", StringComparison.Ordinal);
        int engine = xaml.IndexOf("Text=\"超分引擎\"", StringComparison.Ordinal);
        Assert.True(scale > 0 && model > 0 && engine > 0);
        Assert.True(scale < engine, "放大倍数应排在超分引擎之前");
        Assert.True(scale < model, "放大倍数应排在超分模型之前");
    }

    /// <summary>**下拉文案/提示必须与 Core 逐字一致**(本仓库对下拉项的既有做法:改了 Core 忘了 XAML 会立刻红)。
    /// 【自审补回 · 2026-09-21】上一轮替换契约测试时**漏了**它 —— 结果两个 1x 条目的文案只活在 XAML 里,
    ///   Core 常量(日志/摘要会引用)可能悄悄分叉 ✗。</summary>
    [Fact]
    public void One_x_item_texts_match_core_constants()
    {
        var xaml = ReadFile("ImgUpscalerUI", "Views", "VideoView.xaml");
        int combo = xaml.IndexOf("x:Name=\"VideoEsrganModelCombo\"", StringComparison.Ordinal);
        int end = xaml.IndexOf("</ComboBox>", combo, StringComparison.Ordinal);
        var dropdown = xaml.Substring(combo, end - combo);
        Assert.Contains(Anime4k.MenuText, dropdown);
        Assert.Contains(Anime4k.Tooltip, dropdown);
        Assert.Contains(Upscale1x.RealMenuText, dropdown);
        Assert.Contains(Upscale1x.RealTooltip, dropdown);
        // 两个 1x 条目必须挂在**这个**下拉里(上一版按第一个 </ComboBox> 找锚点,插进了 waifu2x 那个下拉 ✗)
        Assert.Contains($"Tag=\"{Anime4k.ModelTag}\"", dropdown);
        Assert.Contains($"Tag=\"{Upscale1x.RealTag}\"", dropdown);
        // 而且不许出现在 waifu2x 的下拉里
        int w = xaml.IndexOf("x:Name=\"VideoWaifu2xModelCombo\"", StringComparison.Ordinal);
        int we = xaml.IndexOf("</ComboBox>", w, StringComparison.Ordinal);
        var waifu = xaml.Substring(w, we - w);
        Assert.DoesNotContain($"Tag=\"{Anime4k.ModelTag}\"", waifu);
        Assert.DoesNotContain($"Tag=\"{Upscale1x.RealTag}\"", waifu);
    }

    /// <summary>**1x 时必须把引擎锁到 Real-ESRGAN**(自审抓到的界面谎话):
    /// 1x 的两个条目都在 Real-ESRGAN 的下拉里;若引擎是 waifu2x,那个下拉是隐藏的 ⇒ 用户看到的却是
    /// waifu2x 的三个 2x 模型(一个都没灰),而提示写着"这里只列 1x 修复模型" ✗✗。</summary>
    [Fact]
    public void One_x_locks_the_engine_to_real_esrgan()
    {
        var cs = ReadFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
        Assert.Contains("_lastEngineIndexBefore1x", cs);
        Assert.Contains("VideoEngineRadios.IsEnabled = false;", cs);
        Assert.Contains("VideoEngineRadios.IsEnabled = true;", cs);   // 离开 1x 恢复
    }

    /// <summary>不支持的项直接**隐藏**(不只置灰):用户抱怨"要滚过 8 个用不了的才看到能用的"。
    /// 隐藏只改项自身的 Visibility —— 依然不碰集合(碰集合会触发 WinRT E_INVALIDARG 崩溃)。</summary>
    [Fact]
    public void Unsupported_items_are_hidden_not_just_greyed()
    {
        var cs = ReadFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
        Assert.Contains("it.Visibility = usable ? Visibility.Visible : Visibility.Collapsed;", cs);
        Assert.Contains("it.IsEnabled = usable;", cs);   // 双保险:即使某版本对项 Visibility 不生效,也是灰而不可选
    }

    private static string ReadFile(params string[] parts)
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
