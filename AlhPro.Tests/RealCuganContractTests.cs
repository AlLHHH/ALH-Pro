using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【2026-09-24 Real-CUGAN 重新随包】的接线契约。
///
/// 【为什么必须单测 · 这件事在本仓库踩过两次】
///  ① **CLI 不兼容**:Real-CUGAN 的 `-n` 是**降噪档**(-1/0/1/2/3),`-m` 才是模型目录;
///     Real-ESRGAN 正好相反(`-m` 目录 / `-n` 模型名)。照抄 realesrgan 的拼法**不会编译报错**,
///     只会让引擎按错档跑或报参数错 —— 编译、启动、跑一段小视频都可能看不出来。
///  ② **安装器会把刚装好的引擎再删一次**:`installer*.iss` 的 `[InstallDelete]` 里有一条
///     `Name: "{app}\engines\realcugan"`(v1.0 时代删旧引擎用的)。忘记删掉它,升级安装就会把
///     新装的引擎目录删掉 —— 功能白做,而且要装一次包才能发现(CI 看不见)。
/// 两条都属于"编译期查不出来的错位",本仓库对这类问题的既定手法就是契约测试。
///
/// ⚠ 本测试项目**只引用 AlhPro.Core**(见 csproj 注释),所以引擎参数拼装这类
///   ImgUpscalerUI 里的东西一律用"读源码文本断言"的方式锁形态(与 InterpDropdownContractTests 同手法)。</summary>
public class RealCuganContractTests
{
    // ───────────────────────── ① 纯逻辑:模型标签解析 ─────────────────────────

    [Fact]
    public void Tag_parsing_splits_directory_and_denoise_level()
    {
        Assert.Equal("models-se", AlhPro.Core.RealCugan.ModelDir("models-se:-1"));
        Assert.Equal(-1, AlhPro.Core.RealCugan.Noise("models-se:-1"));
        Assert.Equal("models-se", AlhPro.Core.RealCugan.ModelDir("models-se:3"));
        Assert.Equal(3, AlhPro.Core.RealCugan.Noise("models-se:3"));
        Assert.Equal("models-se", AlhPro.Core.RealCugan.ModelDir("models-se:0"));
        Assert.Equal(0, AlhPro.Core.RealCugan.Noise("models-se:0"));
    }

    /// <summary>非法标签**绝不**返回空目录名:空串会让引擎去找 `\up2x-*.param`,
    /// 找不到权重时它不报错、只画一张坏帧、exit=0(本仓库的固定事故形态)。</summary>
    [Fact]
    public void Invalid_tags_fall_back_to_the_shipped_directory_and_default_level()
    {
        foreach (var bad in new string?[] { null, "", "   ", "models-se", ":3", "models-pro" })
        {
            Assert.Equal(AlhPro.Core.RealCugan.ShippedModelDir, AlhPro.Core.RealCugan.ModelDir(bad));
            Assert.Equal(-1, AlhPro.Core.RealCugan.Noise(bad));   // 官方默认档 = conservative
        }
    }

    /// <summary>界面下拉只许放**随包的、且三档都具备 2x/3x/4x 权重**的档位。
    /// denoise1x / denoise2x 只有 up2x ⇒ 一旦被列进下拉,用户在 3x/4x 上会加载不存在的权重。</summary>
    [Fact]
    public void Dropdown_tags_are_all_supported_and_point_at_the_shipped_directory()
    {
        Assert.NotEmpty(AlhPro.Core.RealCugan.Tags);
        foreach (var t in AlhPro.Core.RealCugan.Tags)
        {
            Assert.True(AlhPro.Core.RealCugan.IsSupportedTag(t.Tag), "下拉项不在受支持表里:" + t.Tag);
            Assert.Equal(AlhPro.Core.RealCugan.ShippedModelDir, AlhPro.Core.RealCugan.ModelDir(t.Tag));
            Assert.False(string.IsNullOrWhiteSpace(t.Label));
            Assert.False(string.IsNullOrWhiteSpace(t.Hint));
        }
        Assert.True(AlhPro.Core.RealCugan.IsSupportedTag(AlhPro.Core.RealCugan.DefaultTag));
    }

    /// <summary>与其它两个下拉同口径:每一项的提示里必须写**模型大小**(用户明确要求过)。
    /// 断言打在 XAML 上 —— 那才是用户真正看到的文本(Core 里的 Hint 只是给代码用的说明)。</summary>
    [Fact]
    public void Every_realcugan_dropdown_item_tooltip_states_the_model_size()
    {
        var xaml = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml");
        int at = xaml.IndexOf("x:Name=\"VideoRealcuganModelCombo\"", StringComparison.Ordinal);
        Assert.True(at > 0);
        int end = xaml.IndexOf("</ComboBox>", at, StringComparison.Ordinal);
        var block = xaml.Substring(at, end - at);
        foreach (Match m in Regex.Matches(block, "<ComboBoxItem[^>]*>", RegexOptions.Singleline))
            Assert.True(m.Value.Contains("模型大小"), "Real-CUGAN 的下拉项提示里没有「模型大小」:" + m.Value);
    }

    /// <summary>`DisplayOrSelf` 只把 Real-CUGAN 的标签翻译成人话名;**别的引擎的模型名必须原样返回**
    /// (日志那一行会拿任意引擎的 model 调它,翻译错了就成了"引擎 realesrgan / 模型 Real-CUGAN")。</summary>
    [Fact]
    public void DisplayOrSelf_only_translates_realcugan_tags()
    {
        Assert.Equal(AlhPro.Core.RealCugan.Label(AlhPro.Core.RealCugan.DefaultTag),
            AlhPro.Core.RealCugan.DisplayOrSelf(AlhPro.Core.RealCugan.DefaultTag));
        Assert.Equal("realesr-animevideov3", AlhPro.Core.RealCugan.DisplayOrSelf("realesr-animevideov3"));
        Assert.Equal("models-cunet", AlhPro.Core.RealCugan.DisplayOrSelf("models-cunet"));
    }

    // ───────────────────────── ② 纯逻辑:引擎倍数护栏 ─────────────────────────

    /// <summary>Real-CUGAN 的 models-se 只有 2x/3x/4x 权重(没有 1x)⇒ 目标 ≤1x 必须走"2x 后缩回",
    /// **绝不能下发 `-s 1`**(没有 x1 权重的模型在 ncnn-vulkan 引擎里会静默输出全黑帧、exit=0)。</summary>
    [Fact]
    public void Scale_policy_never_asks_realcugan_for_1x_and_keeps_native_2_3_4()
    {
        Assert.Equal(2, AlhPro.Core.EngineScalePolicy.Decide(AlhPro.Core.RealCugan.EngineName, AlhPro.Core.RealCugan.DefaultTag, 1).EngineScale);
        Assert.Equal(2, AlhPro.Core.EngineScalePolicy.Decide(AlhPro.Core.RealCugan.EngineName, AlhPro.Core.RealCugan.DefaultTag, 2).EngineScale);
        Assert.Equal(3, AlhPro.Core.EngineScalePolicy.Decide(AlhPro.Core.RealCugan.EngineName, AlhPro.Core.RealCugan.DefaultTag, 3).EngineScale);
        Assert.Equal(4, AlhPro.Core.EngineScalePolicy.Decide(AlhPro.Core.RealCugan.EngineName, AlhPro.Core.RealCugan.DefaultTag, 4).EngineScale);
        // 越界/更大目标:封顶 4x 再缩回(不返回 5/8 —— 模型没有那些档)
        Assert.Equal(4, AlhPro.Core.EngineScalePolicy.Decide(AlhPro.Core.RealCugan.EngineName, AlhPro.Core.RealCugan.DefaultTag, 6).EngineScale);

        var one = AlhPro.Core.EngineScalePolicy.Decide(AlhPro.Core.RealCugan.EngineName, AlhPro.Core.RealCugan.DefaultTag, 1);
        Assert.True(one.NeedsShrinkBack);
        Assert.Contains("1x", one.Reason);   // 发生了护栏改写就要有可读的理由(用户要看得懂为什么改了倍数)
    }

    // ───────────────────────── ③ 引擎参数形态(读源码文本) ─────────────────────────

    /// <summary>**本文件的核心断言**:Real-CUGAN 的参数拼装必须是"`-m <目录> -n <降噪档>`",
    /// 且**不许**出现 realesrgan 那种"`-m <目录> -n <模型名>`"的拼法。</summary>
    [Fact]
    public void Realcugan_args_use_denoise_level_for_n_and_directory_for_m()
    {
        var cs = ReadRepoFile("ImgUpscalerUI", "EngineService.cs");
        int at = cs.IndexOf("internal static string RealCuganArgs(", StringComparison.Ordinal);
        Assert.True(at > 0, "找不到 EngineService.RealCuganArgs —— 它是 Real-CUGAN 参数拼装的唯一来源");
        int end = cs.IndexOf("\n    }", at, StringComparison.Ordinal);
        Assert.True(end > at);
        var body = cs.Substring(at, end - at);

        // `-n {noise}` / `-m {dir}`:两维都来自 AlhPro.Core.RealCugan 的解析结果
        Assert.Contains("AlhPro.Core.RealCugan.ModelDir(modelTag)", body);
        Assert.Contains("AlhPro.Core.RealCugan.Noise(modelTag)", body);
        Assert.Contains("-n {noise}", body);
        Assert.Contains("-m {dir}", body);
        // 不许出现 realesrgan 式的 `-m {EsrganModelDir...} -n {model}`
        Assert.DoesNotContain("EsrganModelDir", body);
    }

    /// <summary>三处调用点(单图 / 单块 / 目录批量)都必须走 RealCuganArgs,
    /// 且"已移除(许可不明)"那三处抛错必须彻底消失 —— 留着任何一处,用户选了 Real-CUGAN 就会当场失败。</summary>
    [Fact]
    public void No_leftover_real_cugan_removed_throw_and_all_call_sites_use_the_helper()
    {
        var cs = ReadRepoFile("ImgUpscalerUI", "EngineService.cs");
        Assert.DoesNotContain("Real-CUGAN 已移除", cs);
        Assert.DoesNotContain("engine == \"realcugan\"", cs);   // 一律用 Core.RealCugan.EngineName 常量

        int uses = Regex.Matches(cs, @"RealCuganArgs\(").Count;
        Assert.True(uses >= 4, $"RealCuganArgs 的调用点只有 {uses} 处;单图/单块/逐块拼接/目录批量四处都应改到" +
                               "(目录批量是视频真跑那一条,漏了它视频选 Real-CUGAN 就会走老路径)");
        // 引擎身份键、显示名、探针、残留进程回收名单都要登记
        Assert.Contains("RealCuganEngineId", cs);
        Assert.Contains("realcugan-ncnn-vulkan", cs);
        Assert.Contains("AlhPro.Core.RealCugan.EngineName => FindRealCugan()", cs);
    }

    // ───────────────────────── ④ 安装器:不许把刚装的引擎删掉 ─────────────────────────

    /// <summary>**这条直接防住"功能白做"**:三个安装器脚本的 `[InstallDelete]` 段里
    /// 不许再出现 `engines\realcugan`(v1.0 时代的清理规则),否则升级安装会删掉新装的引擎。</summary>
    [Theory]
    [InlineData("installer.iss")]
    [InlineData("installer_full.iss")]
    [InlineData("installer_nocut.iss")]
    public void Installers_no_longer_delete_the_realcugan_engine_directory(string iss)
    {
        var text = ReadRepoFile(iss);
        int at = text.IndexOf("[InstallDelete]", StringComparison.Ordinal);
        Assert.True(at > 0, iss + " 里没有 [InstallDelete] 段?");
        int end = text.IndexOf("\n[", at + 1, StringComparison.Ordinal);
        var section = end > at ? text.Substring(at, end - at) : text.Substring(at);
        // 【必须先剥注释】修好之后我们**故意**在注释里写明"这条规则被删掉了、为什么" ——
        // 连注释一起搜会自己绊倒自己(InterpDropdownContractTests 第一次跑就踩过同一个坑)。
        var effective = string.Join("\n", section.Split('\n')
            .Where(l => !l.TrimStart().StartsWith(";", StringComparison.Ordinal)));
        Assert.DoesNotContain(@"engines\realcugan", effective);
    }

    // ───────────────────────── ⑤ 打包必带清单 + 界面接线 ─────────────────────────

    [Fact]
    public void Packaging_requires_the_realcugan_engine_and_weights()
    {
        var ps = ReadRepoFile("打包.ps1");
        Assert.Contains(@"engines\realcugan\realcugan-ncnn-vulkan-2026.exe", ps);
        Assert.Contains(@"engines\realcugan\models-se\up2x-conservative.param", ps);
        Assert.Contains(@"licenses\Real-CUGAN-MIT-bilibili-2022.txt", ps);
    }

    /// <summary>视频页:引擎单选**恰好三项**,第三项是 Real-CUGAN(末尾追加 = 老存盘值不被改含义)。</summary>
    [Fact]
    public void Video_page_engine_radios_have_real_cugan_appended_last()
    {
        var xaml = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml");
        int at = xaml.IndexOf("x:Name=\"VideoEngineRadios\"", StringComparison.Ordinal);
        Assert.True(at > 0);
        int end = xaml.IndexOf("</RadioButtons>", at, StringComparison.Ordinal);
        var block = xaml.Substring(at, end - at);
        var items = Regex.Matches(block, "<RadioButton\\s+Content=\"([^\"]*)\"");
        Assert.Equal(3, items.Count);
        Assert.Equal("Real-ESRGAN", items[0].Groups[1].Value);
        Assert.Equal("waifu2x", items[1].Groups[1].Value);
        Assert.Equal("Real-CUGAN", items[2].Groups[1].Value);
    }

    /// <summary>Real-CUGAN 的模型下拉项必须与 <see cref="AlhPro.Core.RealCugan.Tags"/> 逐项对应
    /// (内容顺序 + Tag 取值),否则界面选的档位与下发给引擎的档位会分叉。</summary>
    [Fact]
    public void Realcugan_model_dropdown_matches_the_core_tag_table()
    {
        var xaml = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml");
        int at = xaml.IndexOf("x:Name=\"VideoRealcuganModelCombo\"", StringComparison.Ordinal);
        Assert.True(at > 0, "XAML 里没有 VideoRealcuganModelCombo");
        int end = xaml.IndexOf("</ComboBox>", at, StringComparison.Ordinal);
        var block = xaml.Substring(at, end - at);
        var tags = Regex.Matches(block, "Tag=\"([^\"]*)\"").Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(AlhPro.Core.RealCugan.Tags.Select(t => t.Tag).ToArray(), tags);
    }

    /// <summary>存盘口径:**行为断言**(直接调 `AlhPro.Core.EngineChoice`,不再只读源码文本)。
    /// 铁律:Real-CUGAN 用**新值 3**,历史值 2 继续表示 Real-ESRGAN ——
    /// 反过来(2 → Real-CUGAN)会让还存着 2 的老用户升级后**静默换引擎**,是明令禁止的。</summary>
    [Fact]
    public void Stored_engine_codes_keep_legacy_two_meaning_real_esrgan()
    {
        // ① 值 → 界面索引:0=waifu2x(界面第 2 项)、3=Real-CUGAN(界面第 3 项)、1 与 2 都落 Real-ESRGAN(界面第 1 项)
        Assert.Equal(AlhPro.Core.EngineChoice.UiWaifu2x, AlhPro.Core.EngineChoice.FromStored(0));
        Assert.Equal(AlhPro.Core.EngineChoice.UiRealEsrgan, AlhPro.Core.EngineChoice.FromStored(1));
        Assert.Equal(AlhPro.Core.EngineChoice.UiRealEsrgan, AlhPro.Core.EngineChoice.FromStored(2));   // 历史值 2 仍是 Real-ESRGAN
        Assert.Equal(AlhPro.Core.EngineChoice.UiRealCugan, AlhPro.Core.EngineChoice.FromStored(3));   // 新值 3 = Real-CUGAN

        // ② 界面索引 → 值(反向),并逐项做**往返**:存得住、读得回(G4 类事故就是往返被守卫截断)
        Assert.Equal(0, AlhPro.Core.EngineChoice.ToStored(AlhPro.Core.EngineChoice.UiWaifu2x));
        Assert.Equal(1, AlhPro.Core.EngineChoice.ToStored(AlhPro.Core.EngineChoice.UiRealEsrgan));
        Assert.Equal(3, AlhPro.Core.EngineChoice.ToStored(AlhPro.Core.EngineChoice.UiRealCugan));
        for (int ui = 0; ui <= AlhPro.Core.EngineChoice.UiRealCugan; ui++)
            Assert.Equal(ui, AlhPro.Core.EngineChoice.FromStored(AlhPro.Core.EngineChoice.ToStored(ui)));

        // ③ 引擎名与界面索引一一对应(Real-CUGAN 必须落在 realcugan,不能悄悄是 waifu2x/realesrgan)
        Assert.Equal("realesrgan", AlhPro.Core.EngineChoice.EngineNameOf(AlhPro.Core.EngineChoice.UiRealEsrgan));
        Assert.Equal("waifu2x", AlhPro.Core.EngineChoice.EngineNameOf(AlhPro.Core.EngineChoice.UiWaifu2x));
        Assert.Equal(AlhPro.Core.RealCugan.EngineName, AlhPro.Core.EngineChoice.EngineNameOf(AlhPro.Core.EngineChoice.UiRealCugan));

        // ④ 越界(含界面未选中时的 -1)一律落 Real-ESRGAN,绝不返回下拉里不存在的引擎
        foreach (var bad in new[] { -1, 4, 99, int.MinValue, int.MaxValue })
        {
            Assert.Equal(AlhPro.Core.EngineChoice.UiRealEsrgan, AlhPro.Core.EngineChoice.FromStored(bad));
            Assert.Equal(1, AlhPro.Core.EngineChoice.ToStored(bad));
        }
    }

    /// <summary>**恢复设置时的守卫**(真机事故本体):认的存盘值必须**包含 3**。
    /// 这一条是行为断言 —— 旧的 `d.Engine is >= 0 and <= 2` 会让这里第 4 行直接红。</summary>
    [Fact]
    public void Stored_engine_guard_accepts_the_new_realcugan_value_and_rejects_junk()
    {
        Assert.True(AlhPro.Core.EngineChoice.IsKnownStored(0));
        Assert.True(AlhPro.Core.EngineChoice.IsKnownStored(1));
        Assert.True(AlhPro.Core.EngineChoice.IsKnownStored(2));   // 历史值也要认(否则老用户设置被当成损坏文件)
        Assert.True(AlhPro.Core.EngineChoice.IsKnownStored(3));   // ★ 事故点:3 必须被认
        Assert.False(AlhPro.Core.EngineChoice.IsKnownStored(-1));
        Assert.False(AlhPro.Core.EngineChoice.IsKnownStored(4));
        Assert.False(AlhPro.Core.EngineChoice.IsKnownStored(99));
    }

    /// <summary>**接线断言**:视频页那边必须用上面这个守卫、且不得再出现写死的区间。
    /// (纯逻辑的真相在 <see cref="AlhPro.Core.EngineChoice"/>;这条只钉"页面确实用了它"。)</summary>
    [Fact]
    public void Video_page_uses_the_shared_guard_and_mapping()
    {
        var cs = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");

        // ⚠ 判"旧写法不许回来"之前**必须先剥注释**:修好之后我们**故意**在注释里写明了旧写法
        //   是什么、为什么错 —— 连注释一起搜会自己绊倒自己(InterpDropdownContractTests 第一次跑就踩过同一个坑)。
        var code = StripCsharpComments(cs);

        // 事故写法:任何对 d.Engine 的写死区间都不许再出现(它把新值 3 挡在外面)
        Assert.DoesNotContain("d.Engine is >= 0 and <= 2", code);
        Assert.DoesNotContain("d.Engine < 0 || d.Engine > 2", code);
        // 必须改用共享守卫(这条查的是**代码**,不是注释)
        Assert.Contains("if (AlhPro.Core.EngineChoice.IsKnownStored(d.Engine))", code);
        // 两张映射表都必须转发到 Core(页面里不许留第二份 switch —— 两份迟早分叉)
        Assert.Contains("private static int EngineToStored(int uiIndex) => AlhPro.Core.EngineChoice.ToStored(uiIndex);", code);
        Assert.Contains("private static int EngineFromStored(int stored) => AlhPro.Core.EngineChoice.FromStored(stored);", code);
        // 引擎名也要走同一个来源(此前是页面里的第二个 switch)
        Assert.Contains("AlhPro.Core.EngineChoice.EngineNameOf(VideoEngineRadios.SelectedIndex)", code);
    }

    /// <summary>剥掉 C# 的 `//` 行注释(判"旧写法不许回来"时用)。
    /// ⚠ **只剥行注释,不要顺手剥 `/* */`**:这个文件里 `/*` 会出现在**字符串字面量**里
    /// (文件名通配 / URL 之类),一个天真的块注释正则会把文件"从那个字符串一路吃到很远处的一个 `*/`"
    /// —— 实测把 600,255 字节吃成 55,212 字节(91%),于是所有 Contains 断言全假红。
    /// 行注释正则的已知局限:字符串里出现 `//`(如 `https://…`)会把该行后半截掉;
    /// 对本测试要查的四行(守卫调用与两句转发)无影响 —— 它们所在的行都不含 `//`。</summary>
    private static string StripCsharpComments(string s) => Regex.Replace(s, @"//[^\n]*", "");

    // ───────────────────────── 小工具 ─────────────────────────

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
