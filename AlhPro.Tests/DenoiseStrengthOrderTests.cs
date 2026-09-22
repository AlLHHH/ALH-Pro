using AlhPro.Core;
using System;
using Xunit;

namespace AlhPro.Tests;

/// <summary>「降噪强度」档位迁移的契约(Rev1:自动放最上面 → Rev2:自动下线,只剩 弱/中/强)。
/// 【为什么要单测】这里错了**不会报错**:老用户"存的 0(弱)"会被当成"自动"、"存的 2(强)"会变成"中",
/// 处理结果静默改变、界面看不出异常。本仓库在超分模型下拉改顺序时踩过同一类坑(当时漏了一项),
/// 所以迁移一律抽到 Core 纯函数 + 逐条钉住。</summary>
public class DenoiseStrengthOrderTests
{
    /// <summary>**Rev0 → 当前**:老序号(0=弱 1=中 2=强 3=自动)经两跳换算后,用户看到的档位不能变 ——
    /// 只有"自动"这一档没了(用户 2026-09-21:"自动不要了")⇒ 按它最轻的出手档落到"弱"。</summary>
    [Theory]
    [InlineData(0, DenoiseStrengthOrder.Weak)]      // 老"弱" → 弱
    [InlineData(1, DenoiseStrengthOrder.Medium)]    // 老"中" → 中
    [InlineData(2, DenoiseStrengthOrder.Strong)]    // 老"强" → 强
    [InlineData(3, DenoiseStrengthOrder.Weak)]      // 老"自动" → 弱(离线档,取最轻的出手档)
    public void Rev0_is_remapped_through_both_hops(int oldIndex, int expected)
        => Assert.Equal(expected, DenoiseStrengthOrder.Migrate(oldIndex, 0));

    /// <summary>**Rev1 → 当前**(自动档下线的这一跳):0=自动 1=弱 2=中 3=强 → 0=弱 1=中 2=强。
    /// 【为什么必须单钉】Rev1 时代"Rev0→Rev1"的换算写在函数末尾,靠提前返回只对老数据生效;
    ///   加 Rev2 时若照抄那个结构,末尾那段会**在 Rev1 数据上再套一次** ⇒ 档位被连乘两次 ✗(这次真踩到并修了)。</summary>
    [Theory]
    [InlineData(0, DenoiseStrengthOrder.Weak)]      // 自动 → 弱
    [InlineData(1, DenoiseStrengthOrder.Weak)]      // 弱 → 弱
    [InlineData(2, DenoiseStrengthOrder.Medium)]    // 中 → 中
    [InlineData(3, DenoiseStrengthOrder.Strong)]    // 强 → 强
    public void Rev1_is_remapped_after_auto_was_removed(int oldIndex, int expected)
        => Assert.Equal(expected, DenoiseStrengthOrder.Migrate(oldIndex, 1));

    /// <summary>-1(关)与越界值**不许**被迁移动过:`关` 不能因为迁移变成某个档(那会让用户"明明没开却在降噪")。</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(-7)]
    [InlineData(4)]
    [InlineData(99)]
    public void Off_and_out_of_range_values_pass_through(int stored)
    {
        Assert.Equal(stored, DenoiseStrengthOrder.Migrate(stored, 0));
        Assert.Equal(stored, DenoiseStrengthOrder.Migrate(stored, 1));
    }

    /// <summary>幂等:已经是本 Rev 的原样返回(否则每次启动都会来回横跳 —— 仓库里为此专门盖过章)。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(-1)]
    public void Migration_is_idempotent_at_current_rev(int stored)
    {
        Assert.Equal(stored, DenoiseStrengthOrder.Migrate(stored, DenoiseStrengthOrder.CurrentRev));
        Assert.Equal(stored, DenoiseStrengthOrder.Migrate(stored, DenoiseStrengthOrder.CurrentRev + 5));
    }

    /// <summary>界面序号 ↔ 管线取值:三个档位都要换对,且**必须是一一对应**(往返能回到原值)。</summary>
    [Theory]
    [InlineData(DenoiseStrengthOrder.Weak, 1)]
    [InlineData(DenoiseStrengthOrder.Medium, 2)]
    [InlineData(DenoiseStrengthOrder.Strong, 3)]
    public void Index_maps_to_the_expected_pipeline_value(int index, int pipeline)
    {
        Assert.Equal(pipeline, DenoiseStrengthOrder.ToPipeline(index));
        Assert.Equal(index, DenoiseStrengthOrder.FromPipeline(pipeline));
    }

    /// <summary>越界的界面序号一律当**弱**(最轻的一档):比当"强"安全 —— 强档会削细节、用户明确要过"别发假"。
    /// (Rev2 之前这里兜的是"自动";自动下线后兜底值改成弱,语义一致。) </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(99)]
    [InlineData(-3)]
    public void Out_of_range_index_falls_back_to_weak(int index)
        => Assert.Equal(1, DenoiseStrengthOrder.ToPipeline(index));

    /// <summary>管线值 0(关)按"弱"返回:关不关由开关决定,档位不跟着开关走。
    /// (Rev2 起兜底值是"弱" —— 自动档已下线,不再有"自动"这个界面档。) </summary>
    [Fact]
    public void Pipeline_zero_maps_back_to_weak()
        => Assert.Equal(DenoiseStrengthOrder.Weak, DenoiseStrengthOrder.FromPipeline(0));

    /// <summary>档位名唯一源:界面单选项的文字必须与这里一致(Rev2 起 = 弱/中/强)。</summary>
    [Fact]
    public void Labels_follow_the_new_order()
    {
        Assert.Equal("弱", DenoiseStrengthOrder.Label(DenoiseStrengthOrder.Weak));
        Assert.Equal("中", DenoiseStrengthOrder.Label(DenoiseStrengthOrder.Medium));
        Assert.Equal("强", DenoiseStrengthOrder.Label(DenoiseStrengthOrder.Strong));
        Assert.Equal("弱", DenoiseStrengthOrder.Label(DenoiseStrengthOrder.Weak));
        Assert.Equal("中", DenoiseStrengthOrder.Label(DenoiseStrengthOrder.Medium));
        Assert.Equal("强", DenoiseStrengthOrder.Label(DenoiseStrengthOrder.Strong));
    }

    /// <summary>**XAML 契约**:单选项顺序必须是 弱/中/强,且**不许再出现「自动」** ——
    /// Rev2 用户明确"自动不要了";如果哪天把自动加回来而没 +1 Rev,老设置又会被静默改档。</summary>
    [Fact]
    public void Xaml_radios_are_in_the_new_order()
    {
        var xaml = ReadVideoViewXaml();
        int at = xaml.IndexOf("x:Name=\"DenoiseStrongPanel\"", StringComparison.Ordinal);
        Assert.True(at > 0, "XAML 里找不到 DenoiseStrongPanel");
        int end = xaml.IndexOf("</StackPanel>", at, StringComparison.Ordinal);
        Assert.True(end > at, "DenoiseStrongPanel 没有闭合");
        var block = xaml.Substring(at, end - at);
        Assert.DoesNotContain("自动（先体检素材）", block);   // 自动档已下线,不许回到界面
        Assert.DoesNotContain("DenoiseAutoRadio", block);
        int iWeak = block.IndexOf("Content=\"弱\"", StringComparison.Ordinal);
        int iMed = block.IndexOf("Content=\"中\"", StringComparison.Ordinal);
        int iStrong = block.IndexOf("Content=\"强\"", StringComparison.Ordinal);
        Assert.True(iWeak > 0 && iMed > 0 && iStrong > 0,
            $"三个档位单选项必须都在(iWeak={iWeak} iMed={iMed} iStrong={iStrong})");
        Assert.True(iWeak < iMed && iMed < iStrong,
            "单选项顺序必须是 弱 → 中 → 强(与 DenoiseStrengthOrder 的序号一致)");
        // 命中序号的控件名也要对得上,否则 DenoiseStrengthIndex 会算错档
        Assert.True(block.IndexOf("x:Name=\"DenoiseWeakRadio\"", StringComparison.Ordinal) < iMed, "DenoiseWeakRadio 应对应「弱」");
        Assert.True(block.IndexOf("x:Name=\"DenoiseMediumRadio\"", StringComparison.Ordinal) < iStrong, "DenoiseMediumRadio 应对应「中」");
        Assert.True(block.IndexOf("x:Name=\"DenoiseStrongRadio\"", StringComparison.Ordinal) > iMed, "DenoiseStrongRadio 应对应「强」");
    }

    /// <summary>**初始化顺序契约**:降噪档位的默认值必须设在 <c>LoadSettings()</c> **之前**。
    /// 【为什么钉】2026-09-21 真机踩过:把默认档写在 `LoadSettings()` 之后,
    /// 于是每次启动都用默认值**覆盖用户恢复出来的档位**,紧接着 OnOptionChanged 把被覆盖的值写回
    /// video-settings.json ⇒ 用户的档位被静默清成"自动"(日志铁证:加载 13:04:03 → 写回 13:04:04,Strong 1→0)。
    /// 这种错没有异常、界面也照常显示,只能靠"源码顺序"这种断言守。</summary>
    [Fact]
    public void Default_tier_is_set_before_LoadSettings()
    {
        var src = ReadVideoViewCodeBehind();
        int setDefault = src.IndexOf("SetDenoiseStrengthIndex(AlhPro.Core.DenoiseStrengthOrder.Weak)", StringComparison.Ordinal);
        int load = src.IndexOf("        LoadSettings();", StringComparison.Ordinal);
        Assert.True(setDefault > 0, "构造函数里找不到降噪档位默认值(SetDenoiseStrengthIndex(...Auto))");
        Assert.True(load > 0, "构造函数里找不到 LoadSettings();");
        Assert.True(setDefault < load, "降噪档位默认值必须设在 LoadSettings() **之前**,否则会覆盖用户设置并被写回");
    }

    /// <summary>**迁移必须挂在四条路径上**:设置加载 / 预设加载 / 预设导入 / 写回盖章。
    /// 少一条就会出现"某一类老数据被当成新序号"(预设和设置是两份独立文件)。</summary>
    [Fact]
    public void Migration_is_wired_on_every_read_and_write_path()
    {
        var src = ReadVideoViewCodeBehind();
        Assert.True(src.Contains("if (MigrateDenoiseStrength(d)) migrated = true;"), "设置加载路径没有挂迁移");
        Assert.True(src.Contains("MigrateDenoiseStrength(p.Params)"), "预设加载/导入路径没有挂迁移");
        Assert.True(src.Contains("VideoDenoiseRev = AlhPro.Core.DenoiseStrengthOrder.CurrentRev,"), "收集参数时没有盖章 VideoDenoiseRev");
        Assert.True(src.Contains("d.VideoDenoiseRev = AlhPro.Core.DenoiseStrengthOrder.CurrentRev;"), "预设写回时没有盖章 VideoDenoiseRev");
    }

    private static string ReadVideoViewCodeBehind()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var cand = System.IO.Path.Combine(dir.FullName, "ImgUpscalerUI", "Views", "VideoView.xaml.cs");
            if (System.IO.File.Exists(cand)) return System.IO.File.ReadAllText(cand);
            dir = dir.Parent;
        }
        throw new System.IO.FileNotFoundException("找不到 VideoView.xaml.cs");
    }

    private static string ReadVideoViewXaml()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var cand = System.IO.Path.Combine(dir.FullName, "ImgUpscalerUI", "Views", "VideoView.xaml");
            if (System.IO.File.Exists(cand)) return System.IO.File.ReadAllText(cand);
            dir = dir.Parent;
        }
        throw new System.IO.FileNotFoundException("找不到 VideoView.xaml");
    }
}
