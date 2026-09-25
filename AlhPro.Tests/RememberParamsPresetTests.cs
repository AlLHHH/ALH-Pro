using AlhPro.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【t48 · 2026-09-25】闭环 t47 只读审计查出的「记住参数 / 预设」缺陷 F1~F6。
///
/// 这一组缺陷的共同危害只有两句话(评审时也拿它当尺子):
///   · **用户没动过的东西自己变了** —— F1 预设的 Rev 章漏盖(读侧永远把新数据当老数据);F2 老刻度的
///     5 个后处理强度被原样按新刻度喂进去(**画面直接变**,这条是真的);
///   · **存了却从来没生效** —— F3 视频页「只处理选中的项目」既不入存档也不入预设;F4 图片页设置文件
///     缺 `W2xModelName` ⇒ "按名定位"那条保险从未生效。
///
/// 【证据分两层】本文件里凡是涉及**纯逻辑**的都直接执行 `AlhPro.Core` 的真实现(迁移换算表、等效强度换算、
/// 引擎判据);凡是**只存在于 UI 层**的读写接线(控件的存/读、三处调用点、注释与实现一致)按本仓库既有惯例
/// 用**源码级断言**钉住(AlhPro.Tests 只引用 AlhPro.Core,拿不到 VideoView 的控件)。
/// 【所有源码级断言都先剥掉 `//` 行注释】(见 BlockCode/CodeOnly)—— 否则"把那一行注释掉"也会算通过,
/// 红检就变成假绿(本轮实测踩过一次:注释掉 F1 的盖章后测试仍然绿)。
/// 每个 Fact 的注释里写明"这条红了代表什么回退"。</summary>
public class RememberParamsPresetTests
{
    // ═══════════════════════ F1:降噪档位序号(Rev 漏盖章) ═══════════════════════

    /// <summary>**F1 的"前后对照",跑的是 Core 的真实现**(不是复述代码),而且**与 t47 审计的描述不同** ——
    /// 审计说"预设里的「弱(0)」会被读成「中(1)」",这里用真实现核过:**复现不出来**。
    /// 因为 Rev0→Rev1 与 Rev1→Rev2 两跳**互相抵消**:0→1→0、1→2→1、2→3→2 ⇒ 对「弱/中/强」三个在役档位,
    /// 老数据读出来还是原来那一档(审计只算了第一跳)。真的会变的只有 Rev1 时代的 `0 = 自动`(该档已下线)→「弱」。
    /// 【那为什么还要盖章】两个真理由(见 Save_presets_stamps_the_denoise_rev):
    ///   ① 不盖章 ⇒ 读侧永远认为"这是老数据",每次读都写回一次文件、还多一行"迁移"日志(误导);
    ///   ② 这次的"恒等"只是 Rev1/Rev2 两次换位**恰好抵消**的巧合 —— 以后再动一次序号顺序,没盖章的数据
    ///      就会被按新映射静默换档(本仓库在超分模型下拉上踩过同类坑)。盖章是"写出去的就是本版口径"的凭据。
    /// 这条红了 = 有人改了 DenoiseStrengthOrder 的换算表/CurrentRev(那时必须重新核对预设的盖章与迁移口径)。</summary>
    [Fact]
    public void Preset_denoise_rev_mapping_is_identity_for_the_three_live_grades()
    {
        // 老数据(Rev0)读出来仍是原来那一档:0=弱 / 1=中 / 2=强(两跳抵消)
        for (int i = 0; i <= 2; i++)
            Assert.Equal(i, DenoiseStrengthOrder.Migrate(i, 0));
        Assert.Equal("弱", DenoiseStrengthOrder.Label(DenoiseStrengthOrder.Migrate(0, 0)));
        // 唯一真的会变的是 Rev1 时代的 0 = 自动(已下线)⇒ 落到最轻的「弱」
        Assert.Equal(DenoiseStrengthOrder.Weak, DenoiseStrengthOrder.Migrate(0, 1));
        // -1(关)与越界值一律原样返回(关不能因为迁移变成某个档)
        Assert.Equal(-1, DenoiseStrengthOrder.Migrate(-1, 0));
        Assert.Equal(99, DenoiseStrengthOrder.Migrate(99, 0));
        // 盖上当前 Rev 后三档全短路(值一个字节都不动)⇒ "写的什么、读的就是什么"
        foreach (var v in new[] { -1, 0, 1, 2 })
            Assert.Equal(v, DenoiseStrengthOrder.Migrate(v, DenoiseStrengthOrder.CurrentRev));
    }

    /// <summary>**F1 的接线**:SavePresets 的盖章循环必须把 VideoDenoiseRev 也盖上,而且引用
    /// `DenoiseStrengthOrder.CurrentRev`(不许写死数字 —— 写死的那一刻 Rev 一变,新写的数据立刻"版本落后",
    /// 于是每次读都再换一遍)。
    /// 【红检口径】把 SavePresets 里那一行删掉(**或注释掉**)⇒ 这条立刻变红(已实测,见 output 的 red-check)。
    /// 为什么红检盯"盖章"而不是盯"读回来的档位":见上一条 Fact —— 当前映射对三个在役档位恰好是恒等,
    /// 所以"去掉盖章"在**数值**上看不出差别,但它让读侧永远把新数据当老数据(多写一次盘 + 多一行迁移日志),
    /// 且下一次动序号顺序时就会静默换档 ⇒ 唯一能钉住它的地方就是写侧那行。</summary>
    [Fact]
    public void Save_presets_stamps_the_denoise_rev()
    {
        string save = BlockCode(ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs"),
            "private static void SavePresets(List<VideoPreset> list)", "private const int MaxPresets = 100;");
        Assert.Contains("p.Params.VideoDenoiseRev = AlhPro.Core.DenoiseStrengthOrder.CurrentRev;", save);
        Assert.DoesNotContain("VideoDenoiseRev = 2", save);              // 不许写死数字
        // 盖章循环必须真的遍历列表(不是写了个常量没人用)
        Assert.Contains("foreach (var p in list) if (p?.Params != null)", save);
    }

    // ═══════════════════════ F2:后处理刻度(预设两条读路径都没迁) ═══════════════════════

    /// <summary>**F2 的"为什么必须迁"**:旧刻度与新刻度在同一个数字上代表的实际强度不同 ——
    /// `MigrateStrength` 对「已经是新刻度」的 15 **不是恒等**(锐化 15→21、清晰 15→30)。
    /// 这正是"迁移要迁,但**不能**对官方预设迁第二次"的原因(见 Official_presets_are_exempt_...),
    /// 也是"老预设不迁就画面变"的原因(2026-09-20 之前的用户预设存的都是旧刻度数字)。</summary>
    [Fact]
    public void Old_scale_and_new_scale_numbers_differ_for_the_same_strength()
    {
        // 等效换算(旧→新):数字会变,实际滤镜强度不变
        Assert.Equal(21, VideoPostFilters.MigrateStrength("sharpen", 15));
        Assert.Equal(30, VideoPostFilters.MigrateStrength("clarity", 15));
        Assert.Equal(30, VideoPostFilters.MigrateStrength("usm", 15));
        Assert.Equal(30, VideoPostFilters.MigrateStrength("detail", 15));
        Assert.Equal(21, VideoPostFilters.MigrateStrength("edge", 15));
        // 文档里的定标例子:清晰旧 50(= 实际 0.25)= 新 100
        Assert.Equal(100, VideoPostFilters.MigrateStrength("clarity", 50));
        // 0(关)不被动
        Assert.Equal(0, VideoPostFilters.MigrateStrength("sharpen", 0));
    }

    /// <summary>**F2 的接线 + 幂等**:写侧盖 PostScaleRev 章;读侧(预设加载/导入共用的入口)在 Rev 落后时才
    /// 换算。两条合起来才幂等:盖了章的预设下一次读 `MigratePostStrengths` 直接短路。这条红了 = F2 回退。</summary>
    [Fact]
    public void Preset_write_stamps_the_post_scale_rev_and_read_migrates_only_when_older()
    {
        string src = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
        string save = BlockCode(src, "private static void SavePresets(List<VideoPreset> list)", "private const int MaxPresets = 100;");
        Assert.Contains("p.Params.PostScaleRev = PostScaleCurrentRev;", save);
        // 版本常量只有一处声明(引用它而不是各写一份字面量)
        Assert.Contains("private const int PostScaleCurrentRev = 1;", CodeOnly(src));
        // 迁移函数本身就是幂等的:Rev 不落后直接返回 false(F2 的"只迁一次"就是靠它)
        string migrate = BlockCode(src, "private static bool MigratePostStrengths(VideoSettings d)", "private static bool MigrateDenoiseStrength(VideoSettings d)");
        Assert.Contains("if (d.PostScaleRev >= PostScaleCurrentRev) return false;", migrate);
        Assert.Contains("d.PostScaleRev = PostScaleCurrentRev;", migrate);
        // 5 个强度一个都不能漏(与 Core 的 SafeMax 五档一一对应)
        foreach (var key in new[] { "sharpen", "clarity", "usm", "detail", "edge" })
            Assert.Contains($"AlhPro.Core.VideoPostFilters.MigrateStrength(\"{key}\", d.", migrate);
    }

    /// <summary>**F2 的关键设计决定(与审计给的修法不同,理由在下面):官方预设不做刻度迁移**。
    /// 官方预设那 5 个强度由 `BuiltinPresets()` 基线负责(Rev8 之后写下去的就是新刻度数值),基线改动走
    /// `OfficialRev` 覆盖机制;若再按 PostScaleRev 换一次,用户机器上那份「OfficialRev=8 + PostScaleRev=0」
    /// 的官方预设会被把 15 当成旧值变成 21/30/30/30/21 ⇒ **点一下预设画面就变**(上一条 Fact 的实测值)。
    /// 【这不是假想】本版之前 SavePresets 从来没盖过 PostScaleRev,所以这个组合**必然**存在于老机器上;
    /// 本轮已只读核过用户真实预设文件(`%LOCALAPPDATA%\ALHPro\settings\video-presets.json`,3 条官方预设):
    ///   `OfficialRev=8 / PostSharpen=15 / PostScaleRev=0` —— 正是要被误迁的那种组合。
    /// 这条红了 = 有人把官方预设也拖进了刻度迁移(那会静默改画面)。</summary>
    [Fact]
    public void Official_presets_are_exempt_from_the_post_scale_migration()
    {
        string src = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
        string entry = BlockCode(src, "private static bool MigratePresetParams(VideoPreset p)", "private static List<VideoPreset> LoadPresets()");
        Assert.Contains("if (!p.IsOfficial && MigratePostStrengths(p.Params)) changed = true;", entry);
        // 序号类那两处**不**豁免(官方预设的序号同样是"当前序号",换算对它们是恒等的)
        Assert.Contains("bool changed = MigrateEsrganModelOrder(p.Params);", entry);
        Assert.Contains("if (MigrateDenoiseStrength(p.Params)) changed = true;", entry);
    }

    // ═══════════════════════ t53:t49 复核留下的 low —— "用户预设为什么安全"这条前提也要有断言 ═══════════════════════

    /// <summary>**【t53 · t49 复核留下的 low】上一条 Fact 说明"官方预设的刻度由基线负责、不走 PostScaleRev
    /// 迁移";那么**用户自建预设**凭什么安全?—— 靠的是**界面保存时就已经盖上当前 Rev**:
    /// `CollectVideoParams()`(存档与预设快照**共用**的收集函数)里那两行——
    /// `PostScaleRev = PostScaleCurrentRev` 与 `VideoDenoiseRev = DenoiseStrengthOrder.CurrentRev`。
    /// 有它们,用户保存的预设一定带当前 Rev ⇒ "新刻度数值 + PostScaleRev=0"那个会被误换算的坏组合**造不出来**
    /// (探针已验:官方预设那种组合会被豁免;用户预设带 Rev0 时会被换算 15→21/30/30/30/21)。
    /// 今天这份安全**只靠那两行代码本身**,没有测试钉住 ⇒ 谁把它们删了(或改成写死数字,Rev 一升就"版本落后"),
    /// 坏组合就重新可达,而且是**静默改画面**。这条就是那道钉子。
    /// 【写法】BlockCode 先剥掉 `//` 行注释再比对 —— 否则"把那两行注释掉"也算通过(本轮已踩过一次假绿)。
    /// 【只加断言,不改实现】实现侧一行未动,所以这条红了只能靠改实现(补回那两行)来修。</summary>
    [Fact]
    public void Collect_video_params_stamps_the_current_revs_so_user_presets_are_safe()
    {
        string src = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
        string collect = BlockCode(src, "private VideoSettings CollectVideoParams()", "private void AnimateShowHide(Microsoft.UI.Xaml.UIElement el, bool show)");
        // ① 两行盖章都在
        Assert.Contains("PostScaleRev = PostScaleCurrentRev,", collect);
        Assert.Contains("VideoDenoiseRev = AlhPro.Core.DenoiseStrengthOrder.CurrentRev,", collect);
        // ② 而且都是"引用当前 Rev 常量",不是写死数字(写死的那一刻 Rev 一变,新存的预设就"版本落后")
        Assert.DoesNotContain("PostScaleRev = 1,", collect);
        Assert.DoesNotContain("VideoDenoiseRev = 2,", collect);
        // ③ 存档与预设快照真的走这同一个函数(这条推理的前提)
        Assert.Contains("var d = CollectVideoParams();", BlockCode(src, "private void SaveSettings()", "private VideoSettings CollectVideoParams()"));
        Assert.Contains("Params = CollectVideoParams(),", CodeOnly(src));
        Assert.Equal(3, Count(CodeOnly(src), "CollectVideoParams()"));   // 声明 + 存档 + 预设快照
    }

    // ═══════════════════════ F2 + F6:两条预设读路径共用一个入口 / 注释与实现一致 ═══════════════════════

    /// <summary>**F2 的"两条路径都补了" + F6 的"注释与实现一致"**:
    /// 预设加载(`LoadPresets`)与预设导入(`ImportPresetsAsync`)都走 `MigratePresetParams` 这一个入口,
    /// 入口里三处 Rev 齐全;而注释里「三个调用点」的说法与实现对得上(设置加载直接调 + 预设两条路)。
    /// 这条红了 = 又出现"某条读路径漏了一处迁移"(F2 的原始病灶)或注释又开始骗人(F6)。</summary>
    [Fact]
    public void The_three_preset_migration_paths_are_wired_and_the_comments_match()
    {
        string src = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
        string settings = BlockCode(src, "private void LoadSettings()", "private void ApplyVideoParams(VideoSettings d)");
        string entry = BlockCode(src, "private static bool MigratePresetParams(VideoPreset p)", "private static List<VideoPreset> LoadPresets()");
        string load = BlockCode(src, "private static List<VideoPreset> LoadPresets()", "private static void SavePresets(List<VideoPreset> list)");
        string import = BlockCode(src, "private async Task<int> ImportPresetsAsync()", "private async void ApplyPreset(VideoPreset preset)");

        // 设置加载:三处都有
        Assert.Contains("bool migrated = MigrateEsrganModelOrder(d);", settings);
        Assert.Contains("if (MigratePostStrengths(d)) migrated = true;", settings);
        Assert.Contains("if (MigrateDenoiseStrength(d)) migrated = true;", settings);
        // 预设两条路:都调同一个入口,且入口里三处齐全
        Assert.Contains("if (MigratePresetParams(p)) changed = true;", load);
        Assert.Contains("if (MigratePresetParams(p)) migrated++;", import);
        Assert.Equal(2, Count(CodeOnly(src), "MigratePresetParams(p)"));
        Assert.Contains("private static bool MigratePresetParams(VideoPreset p)", src);
        Assert.Contains("MigrateEsrganModelOrder(p.Params)", entry);
        Assert.Contains("MigrateDenoiseStrength(p.Params)", entry);
        Assert.Contains("MigratePostStrengths(p.Params)", entry);
        // 迁移函数本身只有一处实现(不许各写一份) + 预设那两条路不再直接调它
        Assert.Equal(1, Count(CodeOnly(src), "private static bool MigratePostStrengths(VideoSettings d)"));
        Assert.DoesNotContain("MigratePostStrengths(p.Params)", load);
        Assert.DoesNotContain("MigratePostStrengths(p.Params)", import);

        // F6:注释必须与实现一致 —— 三处调用点 = 设置加载 + 预设加载 + 预设导入
        Assert.Contains("三个调用点", DocOf(src, "private static bool MigrateEsrganModelOrder(VideoSettings d)"));
        Assert.Contains("三个调用点", DocOf(src, "private static bool MigratePostStrengths(VideoSettings d)"));
        Assert.Contains("设置加载", DocOf(src, "private static bool MigratePostStrengths(VideoSettings d)"));
        // 这条注释不许再"冒充别人":它自己点明了预设那两条路走的是哪个入口
        Assert.Contains("MigratePresetParams", DocOf(src, "private static bool MigratePostStrengths(VideoSettings d)"));
    }

    // ═══════════════════════ F3:视频页 SelectedOnly + 图片页那个反方向的漏项 ═══════════════════════

    /// <summary>**F3(视频页)**:「只处理选中的项目」必须**存得下、读得回**。
    /// 四层都要在,少一层就是"存了没生效"或"没存":
    ///   ① 设置模型里有字段(否则根本落不了盘);
    ///   ② `CollectVideoParams` 收集时写(存档与预设共用它);
    ///   ③ `ApplyVideoParams` 恢复时回填(记住参数与套预设共用它);
    ///   ④ 处理路径仍然读**控件本身**(不能被字段架空了运行时行为)。
    /// 这条红了 = 用户那个勾又开始"重启后自己回未勾选"。</summary>
    [Fact]
    public void Video_page_selected_only_round_trips()
    {
        string src = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
        string model = BlockCode(src, "private sealed class VideoSettings", "private static string SettingsFile => ParaPaths.SettingsFile(\"video-settings.json\");");
        Assert.Contains("public bool SelectedOnly { get; set; }", model);
        string collect = BlockCode(src, "private VideoSettings CollectVideoParams()", "private void AnimateShowHide(Microsoft.UI.Xaml.UIElement el, bool show)");
        Assert.Contains("SelectedOnly = SelectedOnlyCheck.IsChecked == true,", collect);
        string apply = BlockCode(src, "private void ApplyVideoParams(VideoSettings d)", "private async void SavePresetBtn_Click(object sender, RoutedEventArgs e)");
        Assert.Contains("SelectedOnlyCheck.IsChecked = d.SelectedOnly;", apply);
        // 处理路径照旧读控件(存盘只是记住状态,不许反过来变成"跑的是文件里的值")
        Assert.Contains("bool onlySelected = pv == null && SelectedOnlyCheck.IsChecked == true;", CodeOnly(src));
        // 控件本身仍在(XAML 是那份勾选框的唯一来源)
        string xaml = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml");
        Assert.Contains("x:Name=\"SelectedOnlyCheck\"", xaml);
    }

    /// <summary>**F3(图片页,反方向的同类漏项)**:`SelectedOnly` 原先只进设置文件、套预设时**不恢复**。
    /// 现在四个方向齐全:SaveSettings 写、LoadSettings 读、CollectSettings 写(预设)、ApplyImgSettings 读(预设)。
    /// 这条红了 = 图片页那个勾又开始"存了却没生效"。</summary>
    [Fact]
    public void Image_page_selected_only_round_trips_in_both_files()
    {
        string src = ReadRepoFile("ImgUpscalerUI", "Views", "UpscaleView.xaml.cs");
        string load = BlockCode(src, "private void LoadSettings()", "private void SaveSettings()");
        string save = BlockCode(src, "private void SaveSettings()", "private string SelectedModelName()");
        string collect = BlockCode(src, "private UpscaleSettings CollectSettings() => new()", "private sealed class UpscalePreset");
        string apply = BlockCode(src, "private void ApplyImgSettings(UpscaleSettings d)", "private async void SavePresetBtn_Click(object sender, RoutedEventArgs e)");
        Assert.Contains("SelectedOnlyCheck.IsChecked = d.SelectedOnly;", load);           // 设置文件读
        Assert.Contains("SelectedOnly = SelectedOnlyCheck.IsChecked == true,", save);    // 设置文件写
        Assert.Contains("SelectedOnly = SelectedOnlyCheck.IsChecked == true,", collect); // 预设写(本轮补的)
        Assert.Contains("SelectedOnlyCheck.IsChecked = d.SelectedOnly;", apply);         // 预设读(本轮补的)
        Assert.Contains("public bool SelectedOnly { get; set; } = false;", src);         // 字段仍在
    }

    // ═══════════════════════ F4:图片页设置文件缺模型名 ═══════════════════════

    /// <summary>**F4**:`SaveSettings` 必须把模型名一起写进 `upscale-settings.json`,否则读侧那两处
    /// "优先按名定位"永远只能退回下标(模型表增删一项 = 所有老用户静默指到别的模型上)。
    /// 同时钉住:写名用的是**唯一那个** `SelectedModelName()`(两处共用,不许各算一份)。</summary>
    [Fact]
    public void Image_settings_file_now_records_the_model_name()
    {
        string src = ReadRepoFile("ImgUpscalerUI", "Views", "UpscaleView.xaml.cs");
        string save = BlockCode(src, "private void SaveSettings()", "private string SelectedModelName()");
        Assert.Contains("W2xModelName = SelectedModelName(),", save);
        Assert.Equal(2, Count(CodeOnly(src), "W2xModelName = SelectedModelName(),"));   // 设置文件 + 预设快照,各一处
        Assert.Contains("private string SelectedModelName()", src);
        // 读侧两处仍然是"优先按名、退回下标"(F4 的修法不许把读侧改掉)
        string load = BlockCode(src, "private void LoadSettings()", "private void SaveSettings()");
        string apply = BlockCode(src, "private void ApplyImgSettings(UpscaleSettings d)", "private async void SavePresetBtn_Click(object sender, RoutedEventArgs e)");
        Assert.Contains("int lmi = FindModelIndexByName(d.W2xModelName, d.Mode == 1);", load);
        Assert.Contains("int mi = FindModelIndexByName(d.W2xModelName, d.Mode == 1);", apply);
        // 名字字段本身仍在(老文件里没有 ⇒ 空串 ⇒ 退回下标,向后兼容)
        Assert.Contains("public string W2xModelName { get; set; } = \"\";", src);
    }

    // ═══════════════════════ F5:图片页模型下标无效时显式回落 ═══════════════════════

    /// <summary>**F5**:名与下标**都无效**时必须显式回落(视频页对同类情况一直这么做),不许"什么都不做"
    /// 停在当前项 —— `ModeRadios.SelectedIndex` 那句会触发 `PopulateModelCombo`,而它只保证"同一个**数字**
    /// 下标不越界" ⇒ 跨模式套预设时会把另一个模式的模型留在下拉上。这条红了 = 跨模式残留又回来了。</summary>
    [Fact]
    public void Image_model_lookup_falls_back_explicitly()
    {
        string src = ReadRepoFile("ImgUpscalerUI", "Views", "UpscaleView.xaml.cs");
        string apply = BlockCode(src, "private void ApplyImgSettings(UpscaleSettings d)", "private async void SavePresetBtn_Click(object sender, RoutedEventArgs e)");
        int byName = apply.IndexOf("int mi = FindModelIndexByName(d.W2xModelName, d.Mode == 1);", StringComparison.Ordinal);
        int byIndex = apply.IndexOf("if (mi < 0) mi = d.W2xModel;", StringComparison.Ordinal);
        int assign = apply.IndexOf("if (mi >= 0 && mi < ModelCombo.Items.Count) ModelCombo.SelectedIndex = mi;", StringComparison.Ordinal);
        int fallback = apply.IndexOf("else ModelCombo.SelectedIndex = 0;", StringComparison.Ordinal);
        Assert.True(byName > 0, "找不到「按名定位」那句");
        Assert.True(byIndex > byName, "找不到「退回下标」那句");
        Assert.True(assign > byIndex, "找不到「合法就赋值」那句");
        Assert.True(fallback > assign, "F5:两个都无效时必须显式回落(ModelCombo.SelectedIndex = 0)");
        // 回落的就是该模式的首选项(PopulateModelCombo 的兜底也是 0),两边口径一致
        string populate = BlockCode(src, "void PopulateModelCombo(bool isAnime)", "ModelCombo.SelectionChanged += (_, _) =>");
        Assert.Contains("ModelCombo.SelectedIndex = (saved >= 0 && saved < ModelCombo.Items.Count) ? saved : 0;", populate);
    }

    // ═══════════════════════ 内置预设的退役值(t46 复核留下的 low) ═══════════════════════

    /// <summary>**t46 的 low**:内置预设「去重补帧4x」原写 `Engine = 0`(waifu2x 退役存值)、「动漫通用」原写
    /// `UpWaifu2xModel = 1` —— 点了会多打一行"视频页已移除 waifu2x…已改用 Real-ESRGAN"的迁移日志。
    /// 现在出厂值改成 Real-ESRGAN 的存值(1)/归零,**而且 Rev 一个都没动**(提 Rev 会连带把用户对官方预设的
    /// 自定义整份覆盖掉)。这条红了 = 要么退役值又回来了,要么有人借这次改动提了 Rev(后者会波及用户自定义)。</summary>
    [Fact]
    public void Builtin_presets_have_no_retired_values_and_keep_their_revs()
    {
        string src = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
        string builtin = BlockCode(src, "private static (string Name, int Rev, Func<VideoSettings> Make)[] BuiltinPresets() => new[]",
            "private void EnsureBuiltinPresets()");
        Assert.DoesNotContain("Engine = 0", builtin);
        Assert.DoesNotContain("UpWaifu2xModel = 1", builtin);
        Assert.Contains("Engine = 1", builtin);                    // Real-ESRGAN 的存值(EngineChoice.StoredRealEsrgan)
        Assert.Equal(1, Count(builtin, "Up = false, Engine = 1")); // 「去重补帧4x」那一行(本轮唯一改的 Engine)
        // 三份官方预设的 Rev **逐字未动**(硬约束:不许为此提 Rev)
        Assert.Contains("\"通用画质增强 不含补帧\", 8,", builtin);
        Assert.Contains("\"动漫通用\", 8,", builtin);
        Assert.Contains("\"去重补帧4x\", 1,", builtin);
        // 与 Core 的存值口径对得上:1 = Real-ESRGAN(不是"随便写个能跑的数字")
        Assert.Equal(1, EngineChoice.StoredRealEsrgan);
        Assert.False(EngineChoice.IsRetiredWaifu2x(EngineChoice.StoredRealEsrgan));
    }

    // ═══════════════════════ t44 复核留下的 low:写死的两行绿字与 Core 联动 ═══════════════════════

    /// <summary>**【t44 low · 顺手加固】**视频页有两项绿字是写死的(`anime4k` → 着色器(无权重)、
    /// `alhpro-real1x` → 权重 2x(缩回 1x))。写死本身是对的(这两个界面 Tag **不是**引擎权重模型名,
    /// Core 对它们返回空串),但原先测试只跟自己的字面量比 ⇒ 将来 Core 改文案时两边会"自洽地错着"。
    /// 现在把这一侧接到 Core 上:
    ///   · 1x 那一行的期望值取自 `NativeWeightLabel(realesrgan, ExperimentalEsrgan.Fix1x)`(引擎侧真名);
    ///   · Anime4K 那一行钉住"Core 此刻**没有**它的标签"(空串)这个前提 —— 前提一变这条就红。
    /// 这条红了 = Core 的权重口径变了(或界面那两行被改成了别的话),两边必须重新对齐。</summary>
    [Fact]
    public void Handwritten_weight_lines_are_linked_to_core()
    {
        string xaml = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml");
        // ① 「现实 · 1x 修复」:界面 Tag 不是权重名,引擎侧真名是 ExperimentalEsrgan.Fix1x
        Assert.True(ExperimentalEsrgan.Is1xModel(ExperimentalEsrgan.Fix1x), "Fix1x 必须被 Core 认成 1x 修复模型");
        Assert.Equal("", EngineScalePolicy.NativeWeightLabel("realesrgan", Upscale1x.RealTag));   // 界面 Tag 本身不是权重模型
        string oneX = EngineScalePolicy.NativeWeightLabel("realesrgan", ExperimentalEsrgan.Fix1x);
        Assert.Equal("权重 2x(缩回 1x)", oneX);
        Assert.Equal("&#x0a;" + oneX, GreenLineOf(xaml, Upscale1x.RealTag));
        // ② 「动漫 · Anime4K 修复」:走着色器,Core **故意**不给标签 ⇒ 界面那行是刻意的覆盖
        Assert.Equal("", EngineScalePolicy.NativeWeightLabel("realesrgan", Anime4k.ModelTag));
        string anime4kLine = GreenLineOf(xaml, Anime4k.ModelTag);
        Assert.Equal("&#x0a;着色器(无权重)", anime4kLine);
        Assert.Contains("无权重", anime4kLine);                      // 面向用户的措辞说明了"它没有权重文件"
        // Core 知道这两个 Tag 是 1x 档的两个条目(界面与判据同源)
        Assert.True(Upscale1x.Is1xEntry(Upscale1x.RealTag));
        Assert.True(Upscale1x.Is1xEntry(Anime4k.ModelTag));
    }

    // ───────────────────────── 工具 ─────────────────────────

    /// <summary>取 XAML 里某个 Tag 那一项的绿字行文本(含开头的 `&#x0a;`)。</summary>
    private static string GreenLineOf(string xaml, string tag)
    {
        int tagPos = xaml.IndexOf("Tag=\"" + tag + "\"", StringComparison.Ordinal);
        Assert.True(tagPos > 0, "视频页找不到 Tag=" + tag);
        int runAt = xaml.IndexOf("<Run Foreground=\"#7BD88F\">", tagPos, StringComparison.Ordinal);
        Assert.True(runAt > 0, tag + ": 找不到绿字行");
        int gt = xaml.IndexOf('>', runAt);
        int lt = xaml.IndexOf("</Run>", gt, StringComparison.Ordinal);
        Assert.True(lt > gt);
        return xaml[(gt + 1)..lt];
    }

    /// <summary>剥掉 `//` 行注释后的源码段(两端标记都必须在**代码**里)。</summary>
    private static string BlockCode(string src, string start, string end) => Block(CodeOnly(src), start, end);

    /// <summary>取 `<paramref name="start"/>` 与 `<paramref name="end"/>` 之间的源码段(两端都不含)。
    /// 用不了就 Assert 失败(避免"标记改了、断言悄悄变成空串比较"这种假绿)。</summary>
    private static string Block(string src, string start, string end)
    {
        int a = src.IndexOf(start, StringComparison.Ordinal);
        Assert.True(a > 0, "找不到起点标记:" + start);
        int b = src.IndexOf(end, a + start.Length, StringComparison.Ordinal);
        Assert.True(b > a, $"在 {start} 之后找不到终点标记:{end}");
        return src[a..b];
    }

    /// <summary>取某声明**上方**连续 `///` 注释块(用来查"注释有没有说错")。</summary>
    private static string DocOf(string src, string decl)
    {
        int i = src.IndexOf(decl, StringComparison.Ordinal);
        Assert.True(i > 0, "找不到声明:" + decl);
        int p = src.LastIndexOf('\n', i - 1) + 1;
        var lines = new List<string>();
        while (p > 0)
        {
            int prev = src.LastIndexOf('\n', p - 2) + 1;
            string line = src[prev..p].TrimEnd('\r', '\n');
            if (!line.TrimStart().StartsWith("///", StringComparison.Ordinal)) break;
            lines.Insert(0, line);
            p = prev;
        }
        Assert.True(lines.Count > 0, "该声明上方没有 XML 注释:" + decl);
        return string.Join('\n', lines);
    }

    private static int Count(string src, string needle)
    {
        int n = 0, at = 0;
        while ((at = src.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { n++; at += needle.Length; }
        return n;
    }

    /// <summary>剥掉 `//` 行注释(本仓库既有做法:注释里为说清来龙去脉会提到"被删掉/被改掉的值",
    /// 拿全文断言会把自己的解释字当成违规)。**逐字符扫引号**:只有在字符串字面量之外遇到的 `//` 才算注释
    /// (否则 `"http://…"` 这类字符串会把后面的代码一起吃掉)。⚠ 不处理块注释:本仓库的块注释里从不上色,
    /// 且块注释内的 `/*` 会把字符串吃掉(踩过)。
    /// 【为什么非要剥】本轮红检实测:把 F1 的盖章那一行**注释掉**,不剥注释的断言仍然是绿的(假绿)。</summary>
    private static string CodeOnly(string src)
    {
        var sb = new System.Text.StringBuilder(src.Length);
        foreach (var line in src.Split('\n'))
        {
            bool inStr = false, inChar = false;
            int cut = line.Length;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inStr)
                {
                    if (c == '\\') { i++; continue; }               // 转义:跳过下一个字符
                    if (c == '"') inStr = false;
                    continue;
                }
                if (inChar)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '\'') inChar = false;
                    continue;
                }
                if (c == '"') { inStr = true; continue; }
                if (c == '\'') { inChar = true; continue; }
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') { cut = i; break; }
            }
            sb.Append(line[..cut]).Append('\n');
        }
        return sb.ToString();
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
