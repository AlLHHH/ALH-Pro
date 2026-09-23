using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【2026-09-23 第二批核查修复 · 契约测试】本轮按用户「修」的指示处理的是核查报告里的
/// B4 / B5 / B6 / B7 / B8 + C1~C4。这些条目大多是**文案与死代码**,不会编译报错、也不会让测试变红,
/// 正是最容易"改过又漂回去"的一类 ⇒ 每条都钉一个源码契约。
/// (纯逻辑部分另有 <c>CpuFallbackPolicyTests</c>;这里只管"代码/文案里到底还有没有那个东西"。)
///
/// 【为什么断言"带签名的形态"而不是裸名字】删除原因写在同文件的注释里,注释会提到这些名字
/// (如"DetectFreezeAsync 已删"),用裸名字断言会被自己的注释判失败 —— 与 PixelCompareRemovedTests 同一口径。
///
/// **诚实边界**:契约只能证明源码里没有残留,证明不了"界面上确实不再出现" —— 后者要靠真机(UIA/截图)验。</summary>
public class AuditFixes_20260923_Tests
{
    // ───────────────────────── B4:音频 DirectML 账本用 DML 设备号 ─────────────────────────

    /// <summary>★ 连击表只认【DirectML 设备号】,而音频拿到的是【引擎 -g 号】——
    /// 原先三个调用点(查熔断/记失败/清连击)都直接传 gpuId ⇒ 记的是别人的键,熔断永远触发不了,
    /// 每个任务都白试一次注定失败的 DML(实测白等 2~11 秒)。现在必须先 ToDmlDevice 解析。</summary>
    [Fact]
    public void Audio_dml_ledger_uses_the_directml_device_number()
    {
        var cs = ReadRepoFile("ImgUpscalerUI", "AudioEnhanceService.cs");
        Assert.Contains("EngineService.ToDmlDevice(gpuId)", cs);            // 解析一次
        Assert.DoesNotContain("DmlDeviceUnusable(gpuId", cs);               // 旧:查的是别人的键
        Assert.DoesNotContain("NoteDmlTransientFailure(gpuId", cs);         // 旧:记的是别人的键
        Assert.DoesNotContain("ClearDmlStrikes(gpuId", cs);                 // 旧:清的是别人的键
        Assert.Contains("DmlDeviceUnusable(dmDevice", cs);
        Assert.Contains("NoteDmlTransientFailure(dmDevice", cs);
        Assert.Contains("ClearDmlStrikes(dmDevice", cs);
    }

    /// <summary>★ 建会话失败 = 结构性失败,必须立刻判该设备在音频域不可用(否则下一个任务还会白试一次)。
    /// 这条闩锁是"每个任务白等 2~11 秒"的直接解药。</summary>
    [Fact]
    public void A_failed_dml_session_is_latched_immediately()
    {
        var svc = ReadRepoFile("ImgUpscalerUI", "EsrganOnnxService.cs");
        Assert.Contains("internal static void NoteDmlSessionCreationFailure(", svc);
        var audio = ReadRepoFile("ImgUpscalerUI", "AudioEnhanceService.cs");
        Assert.Contains("NoteDmlSessionCreationFailure(dmDevice", audio);
    }

    // ───────────────────────── B5:自定义分辨率必须有入口 ─────────────────────────

    /// <summary>★「自定义分辨率」面板只在 SelectedIndex == 4 时可见,而单选组原先只有 0~3 四项
    /// ⇒ 面板永久隐藏、代码里所有 `== 4` 的分支都是死路(使用教程却写着让用户去选它)。</summary>
    [Fact]
    public void The_custom_resolution_entry_exists_so_its_panel_is_reachable()
    {
        var xaml = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml");
        Assert.Contains("x:Name=\"VScaleCustomRadio\"", xaml);
        foreach (var name in new[] { "VScale1xRadio", "VScale2xRadio", "VScale3xRadio", "VScale4xRadio", "VScaleCustomRadio" })
            Assert.Contains($"x:Name=\"{name}\"", xaml);
        // 代码侧的可见性判据仍然是索引 4(与新条目位置一致)
        var cs = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
        Assert.Contains("CustomSizePanel.Visibility = up && scaleIdx == 4", cs);
    }

    // ───────────────────────── B6:过时文案 ─────────────────────────

    /// <summary>★ 降噪强度只剩 弱/中/强(「自动」档 2026-09-21 已按用户要求下线),
    /// 提示里不许再说"选「自动」时会…"(用户找不到那个档)。</summary>
    [Fact]
    public void The_denoise_tooltip_no_longer_mentions_the_retired_auto_level()
    {
        var xaml = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml");
        Assert.DoesNotContain("强度选「自动」", xaml);
        Assert.DoesNotContain("「自动」档为了必定生效", xaml);
        Assert.Contains("只有 弱 / 中 / 强 三档", xaml);
    }

    /// <summary>★ 图片抠图**固定走 CPU**(GPU 推理会占满显卡),提示里不许再写"本机 GPU 实测最快"。</summary>
    [Fact]
    public void The_cutout_tooltip_stops_claiming_gpu_speed_on_a_cpu_only_page()
    {
        var xaml = ReadRepoFile("ImgUpscalerUI", "Views", "CutoutView.xaml");
        Assert.DoesNotContain("本机 GPU 实测最快", xaml);
        Assert.DoesNotContain("本机 DirectML 上跑不动,会回退 CPU", xaml);
        Assert.Contains("图片抠图固定用 CPU 推理", xaml);
    }

    /// <summary>★ 音频分离只是在**尝试** DML(建会话失败就自动改 CPU),日志/状态栏不许断言"显卡加速中"。</summary>
    [Fact]
    public void The_audio_page_no_longer_asserts_gpu_acceleration()
    {
        var cs = ReadRepoFile("ImgUpscalerUI", "Views", "AudioView.xaml.cs");
        Assert.DoesNotContain("显卡加速,请耐心等待", cs);
        Assert.DoesNotContain("AI 分离中(显卡加速)", cs);
        Assert.Contains("优先显卡加速", cs);
    }

    /// <summary>★ 批大小的报错正文里不许再写死具体帧数(B6):档位改过一次,写死的 "(700 的硬条件)" 就成了假话。</summary>
    [Fact]
    public void The_batch_rule_line_does_not_hardcode_a_retired_frame_count()
    {
        var cs = ReadRepoFile("AlhPro.Core", "RenderPolicy.cs");
        Assert.DoesNotContain("(700 的硬条件", cs);
        Assert.Contains("StrongDeviceLargeFramesPerBatch} 的硬条件", cs);   // 改为按常量插值
    }

    /// <summary>★ 使用教程里的过时指引:RIFE 模型只剩两支 v4(没有 v2.3)、VFR 已无开关、
    /// 50 系不再按型号一刀切、视频处理不再落到纯 CPU(会直接报错)。</summary>
    [Fact]
    public void The_tutorial_drops_retired_options()
    {
        var md = ReadRepoFile("使用教程.md");
        Assert.DoesNotContain("老素材不兼容时再试 v2.3", md);
        Assert.DoesNotContain("默认「自动」,录屏", md);
        Assert.DoesNotContain("新架构与内置引擎暂不完全兼容", md);
        Assert.DoesNotContain("只有极少数情况(异常)才会落到纯 CPU", md);
        Assert.Contains("视频处理不会再落到纯 CPU", md);
    }

    // ───────────────────────── B7:不许静默丢帧 ─────────────────────────

    /// <summary>★ 逐帧交付失败必须留痕、且只有成功才推进全局帧号(否则序列出现空洞、成片静默变短)。</summary>
    [Fact]
    public void Failed_frame_delivery_is_no_longer_silent()
    {
        var cs = ReadRepoFile("ImgUpscalerUI", "VideoService.cs");
        Assert.DoesNotContain("frame_{globalIdx++:D6}", cs);      // 旧:失败了也吃掉编号
        Assert.Contains("TryDeliverFrame(", cs);                  // 新:交付 + 兜底重试,返回成败
        Assert.Contains("补帧帧交付失败", cs);                     // 逐帧 WARN
        Assert.Contains("没能交付到最终目录", cs);                 // 段末 ERROR 汇总
    }

    // ───────────────────────── C1 ~ C4:死界面 / 死代码 / 日志噪音 / 列表刷新 ─────────────────────────

    /// <summary>★ C1:三处"永不出现"的界面已删(橙色倍率提示、无名裁剪提示、对比虚影线)。</summary>
    [Fact]
    public void The_three_never_visible_ui_elements_are_gone()
    {
        var video = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml");
        Assert.DoesNotContain("x:Name=\"CompareSplitGhost\"", video);
        Assert.DoesNotContain("裁剪:在右侧选中视频后可设置开始/结束时间", video);
        var up = ReadRepoFile("ImgUpscalerUI", "Views", "UpscaleView.xaml");
        Assert.DoesNotContain("x:Name=\"ScaleHint\"", up);
        // code-behind 里那 5/1 处"恒 Collapsed"的赋值也不许留
        var videoCs = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
        Assert.DoesNotContain("CompareSplitGhost != null", videoCs);
        var upCs = ReadRepoFile("ImgUpscalerUI", "Views", "UpscaleView.xaml.cs");
        Assert.DoesNotContain("ScaleHint.Visibility = Visibility.Collapsed", upCs);
    }

    /// <summary>★ C2:零调用点的死代码已删(硬链接工具、两个检测方法、RIFE 位图重载与它的解码入口)。</summary>
    [Fact]
    public void The_zero_call_site_dead_code_is_gone()
    {
        var video = ReadRepoFile("ImgUpscalerUI", "VideoService.cs");
        Assert.DoesNotContain("void TryCreateHardLink(", video);
        Assert.DoesNotContain("extern bool CreateHardLinkW(", video);
        Assert.DoesNotContain("DetectFreezeAsync(", video);
        Assert.DoesNotContain("DetectFpsSegmentsAsync(", video);
        var rife = ReadRepoFile("ImgUpscalerUI", "RifeOnnxService.cs");
        Assert.DoesNotContain("public static Bitmap LoadFrameBitmap(", rife);
        // public 的位图重载(零调用点)——内部那个"位图 RunCore"是字符串重载的实现,必须还在
        Assert.DoesNotContain("public static void InterpWithSession(InferenceSession session, Bitmap bmp0", rife);
        Assert.Contains("static void RunCore(InferenceSession session, Bitmap bmp0", rife);
        // 两个**还在用**的入口不许被误删
        Assert.Contains("public static void InterpWithSession(InferenceSession session, string img0", rife);
        Assert.Contains("static Bitmap LoadBitmap(string path)", rife);
    }

    /// <summary>★ C3:更新检查失败的日志按端点去重(本机一次启动曾刷 6 条相同 INFO)。</summary>
    [Fact]
    public void The_update_check_failure_log_is_deduped()
    {
        var cs = ReadRepoFile("ImgUpscalerUI", "UpdateChecker.cs");
        Assert.Contains("_endpointFailCount", cs);
        Assert.Contains("_endpointFailCount.AddOrUpdate", cs);
        Assert.Contains("不再重复报", cs);
    }

    /// <summary>★ C4:音频列表项要实现 INotifyPropertyChanged,否则"已处理/失败"这些文字不会刷新到界面。</summary>
    [Fact]
    public void The_audio_list_item_notifies_the_ui()
    {
        var cs = ReadRepoFile("ImgUpscalerUI", "Views", "AudioView.xaml.cs");
        Assert.Contains("class AudioItem : System.ComponentModel.INotifyPropertyChanged", cs);
        Assert.Contains("Raise(nameof(Display))", cs);
        Assert.Contains("PropertyChanged?.Invoke(this", cs);
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
