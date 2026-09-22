using AlhPro.Core;
using System;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>「转场识别(转场处不插帧)」开关的**默认口径与迁移**(2026-09-15 用户最终定调:**默认关**、可开可关)。
///
/// 【本文件钉住的三件事】
///   ① **默认关**:XAML 启动默认不勾、重置回到不勾、`SceneDefaultPolicy.DefaultScene == false`;
///   ② **升级不改用户的勾选** —— 老设置文件里是 false 就还是 false、是 true 就还是 true
///      (Rev 只当版本标记;工程里**不存在任何把 Scene 置真**的迁移路径);
///   ③ 预设不会把转场识别打开/关错(三个官方内置预设都写 `Scene = false`,且 Rev 已退回原值,
///      不会造成"点一下预设就莫名改动勾选")。
///
/// 【Rev 历史】Rev0 = 老文件(无字段,默认关);Rev1 = 曾"默认开 + 强制迁移"(**已撤回**);
/// Rev2 = 定稿"默认关 + 迁移只对齐版本号、不改值"。</summary>
public class SceneDefaultPolicyTests
{
    // ---------- ① 默认关 ----------

    /// <summary>核心口径:默认值必须是 **false**(用户定调:不要替他打开)。</summary>
    [Fact]
    public void Default_scene_is_off()
    {
        Assert.False(SceneDefaultPolicy.DefaultScene);
        Assert.Equal(2, SceneDefaultPolicy.CurrentRev);
    }

    // ---------- ② 升级后老用户的勾选状态与升级前一致 ----------

    /// <summary>**【用户明确要求】**老设置文件(Rev0,没有 `SceneDefaultRev` 字段)+ `Scene = false`
    /// → 迁移后**仍然是 false**,只有 Rev 前进(用来标记"已按当前口径结算过")。
    /// 【向后兼容依据】老文件里没有该字段 → System.Text.Json 不赋值 → CLR 默认 **0** → `0 < CurrentRev`
    /// 成立 → 走一次迁移;而这次迁移**不改值**。</summary>
    [Fact]
    public void Old_file_with_scene_false_stays_false()
    {
        bool scene = SceneDefaultPolicy.Migrate(false, 0, out int newRev);
        Assert.False(scene);
        Assert.Equal(SceneDefaultPolicy.CurrentRev, newRev);
    }

    /// <summary>**【用户明确要求】**老设置 `Scene = true` → 迁移后**仍然是 true**(用户自己勾的就该保持勾着)。</summary>
    [Fact]
    public void Old_file_with_scene_true_stays_true()
    {
        bool scene = SceneDefaultPolicy.Migrate(true, 0, out int newRev);
        Assert.True(scene);
        Assert.Equal(SceneDefaultPolicy.CurrentRev, newRev);
    }

    /// <summary>迁移的返回值**恒等于传入值**(任意 Rev × 任意值都不许变) —— 这一条就是"不存在把 Scene 置真的路径"
    /// 的纯逻辑侧保证。Rev1 那种"rev &lt; 1 就 scene = true"的写法在这里会立刻红。</summary>
    [Theory]
    [InlineData(true, 0)] [InlineData(false, 0)]
    [InlineData(true, 1)] [InlineData(false, 1)]
    [InlineData(true, 2)] [InlineData(false, 2)]
    [InlineData(true, 99)] [InlineData(false, 99)]   // 从更新版本回退:值与 Rev 都不许被改小
    public void Migration_never_changes_the_value(bool scene, int rev)
    {
        bool now = SceneDefaultPolicy.Migrate(scene, rev, out int newRev);
        Assert.Equal(scene, now);                 // 值一个字节都不动
        Assert.True(newRev >= rev);               // Rev 只前进(不回退)
        if (rev >= SceneDefaultPolicy.CurrentRev) Assert.Equal(rev, newRev);   // 已结算过 → Rev 也不动
    }

    /// <summary>幂等:同一份数据反复过迁移,值恒不变(模拟"启动 N 次")。</summary>
    [Fact]
    public void Migration_is_idempotent_across_restarts()
    {
        bool scene = false;
        int rev = 0;
        for (int boot = 0; boot < 5; boot++) scene = SceneDefaultPolicy.Migrate(scene, rev, out rev);
        Assert.False(scene);
        Assert.Equal(SceneDefaultPolicy.CurrentRev, rev);
        // 用户中途勾上 → 之后所有"启动"都必须保持勾着
        scene = SceneDefaultPolicy.Migrate(true, rev, out rev);
        for (int boot = 0; boot < 5; boot++) scene = SceneDefaultPolicy.Migrate(scene, rev, out rev);
        Assert.True(scene);
    }

    // ---------- ④ XAML 默认不勾 ----------

    /// <summary>契约:XAML 里 `SceneCheck` **不得**带 `IsChecked="True"`(新用户/无设置文件那条路的默认值
    /// 就来自这里)。这条同时防止"顺手又改回默认开"。</summary>
    [Fact]
    public void Xaml_scene_checkbox_defaults_to_unchecked()
    {
        var xaml = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml");
        int start = xaml.IndexOf("x:Name=\"SceneCheck\"", StringComparison.Ordinal);
        Assert.True(start > 0, "XAML 里找不到 SceneCheck");
        int end = xaml.IndexOf('>', start);
        Assert.True(end > start, "SceneCheck 标签没有闭合");
        var tag = xaml.Substring(start, end - start);
        Assert.DoesNotContain("IsChecked=\"True\"", tag);
        Assert.Contains("Checked=\"Options_Changed\"", tag);       // 仍可开
        Assert.Contains("Unchecked=\"Options_Changed\"", tag);     // 仍可关
    }

    /// <summary>契约:重置路径与设置字段都走同一份默认口径(不许在别处写死 true/false 或写死 Rev 数字);
    /// 并且**不许再出现任何把 Scene 置真**的写法。</summary>
    [Fact]
    public void Reset_path_follows_the_policy_and_nothing_forces_it_on()
    {
        var cs = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
        Assert.Contains("SceneCheck.IsChecked = AlhPro.Core.SceneDefaultPolicy.DefaultScene;", cs);  // 重置 = 默认(关)
        Assert.Contains("public int SceneDefaultRev { get; set; }", cs);                             // 版本字段保留(兼容)
        Assert.Contains("d.SceneDefaultRev = AlhPro.Core.SceneDefaultPolicy.CurrentRev;", cs);      // 写盘盖章
        Assert.Contains("MigrateSceneDefault(d)", cs);                                               // 加载时确实走了迁移
        // 【反向断言】工程里不许有"把转场识别打开"的硬写法(那正是本次被撤回的动作)
        Assert.DoesNotContain("SceneCheck.IsChecked = true;", cs);
        Assert.DoesNotContain("Scene = true,", cs);
        Assert.DoesNotContain("d.Scene = true", cs);
    }

    // ---------- ③ 预设不会把 Scene 打开/关错 ----------

    /// <summary>三个官方内置预设都必须写 `Scene = false`(与新的默认一致),且 Rev 已退回原值
    /// (Rev 现在是 **8/8/1** —— 2026-09-22 用户裁决:前两条因"后处理四项降到 15"提到 8,理由见 BuiltinPresets 注释;
    ///  提到 8 而不是 7 是因为用户机器上的预设文件里已经是 Rev7,`OfficialRev < rev` 对 7 不成立、覆盖不到。)
    /// 【为什么这条重要】预设是"一整套参数快照",里面就带着 Scene;若预设里是 true,用户点一下
    /// 「动漫通用」就会**莫名打开**转场识别(用户看到的只是那个勾自己出现了)。</summary>
    [Fact]
    public void Builtin_presets_keep_scene_off_and_carry_the_current_rev()
    {
        var cs = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
        Assert.DoesNotContain("Scene = true,", cs);
        int off = Regex_Count(cs, "Scene = false, SceneThr = 0.3,");
        Assert.True(off >= 3, $"官方预设里的 Scene=false 只有 {off} 处(应为 3 处)");
        // Rev 已退回原值(不再是"为默认开而提上去"的 7/7/2)
        Assert.Contains("( \"通用画质增强 不含补帧\", 8,", cs);
        Assert.Contains("( \"动漫通用\", 8,", cs);
        Assert.Contains("( \"去重补帧4x\", 1,", cs);
        Assert.DoesNotContain("( \"动漫通用\", 7,", cs);
        // 【2026-09-22 用户裁决】后处理四项降到 15(两条预设都要)
        int fifteen = Regex_Count(cs, @"PostSharpen = 15, PostClarity = 15, PostUsm = 15, PostDetail = 15,");
        Assert.True(fifteen >= 2, $"两条官方预设的后处理四项应为 15,实际只有 {fifteen} 处");
        // 【已删的官方预设】基线里不许再有它(否则"缺失即创建"会把它重建回来 —— 用户实测踩过)
        Assert.DoesNotContain("1x 修复（不放大）\", 1,", cs);
    }

    // ---------- 保留项:普通路径仍接新判据 + 平滑时间轴固定启用 ----------

    /// <summary>「普通路径也接新判据」是上一轮的功能改进(不是默认值),必须保留 —— 而且现在**判据只有一处**
    /// (2026-09-15 统一:主流程算一次,普通分段路径与"按真实时间戳排帧"那条路共用同一份切点表)。
    /// 旧回退判据(ffmpeg scene)仍在:主判据拿不到采样数据时用它兜底。</summary>
    [Fact]
    public void SceneCutJudge_is_wired_once_and_shared_by_both_paths()
    {
        var svc = ReadRepoFile("ImgUpscalerUI", "VideoService.cs");
        int n = Regex_Count(svc, "SceneCutJudge\\.Detect\\(");
        Assert.Equal(1, n);                                  // 判据只算一次(统一后不再是两处)
        Assert.Equal(1, Regex_Count(svc, "await ComputeSceneCutMetricsAsync\\("));
        Assert.Contains("sceneCutPairs", svc);               // 结果存进共享表给两条路
        Assert.Contains("select='gt(scene,", svc);           // 回退判据仍在
    }

    /// <summary>「平滑时间轴」这个**用户可见选项**必须彻底消失(用户最终裁决:"我什么时候要求添加这个的 不要有这个"),
    /// 但**功能不许被顺手删掉**:它不是被关掉,而是改成"只在源确实是 VFR 时自动生效"(见 SceneSwitchCoverageTests
    /// 的第 ⑤ 条)。这里钉界面侧:
    /// ① XAML 里不再有该控件;② 活代码里不再引用它(不留死引用);
    /// ③ 处理侧仍传 true(表示"允许这条内部路径",是否真的走由 VFR 门决定);
    /// ④ 读设置时**忽略**老值(旧 false 关不掉它);⑤ 写盘时**恒写 true**(不会被老 false 带回去)。</summary>
    [Fact]
    public void Smooth_timeline_option_is_gone_from_the_ui()
    {
        var xaml = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml");
        // 控件本身(带 x:Name 的元素)必须已经不存在;注释里提到它的名字是允许的(那是给后来人看的沿革说明)
        Assert.DoesNotContain("x:Name=\"SmoothTimelineCheck\"", xaml);
        Assert.DoesNotContain("x:Name=\"SmoothTimelineHint\"", xaml);
        // 界面上也不许再有它的文字(勾选框标题/说明/tooltip 文案)
        Assert.DoesNotContain(">平滑时间轴<", xaml);

        var cs = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
        // 【只查代码行,不查注释】留一段沿革说明是好事;但**活代码里**不许再引用已移除的控件
        // (真有活引用编译期就会报错;这里再钉一道,防止有人把注释里那行恢复成真代码)。
        var csCode = string.Join("\n", cs.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));
        Assert.DoesNotContain("SmoothTimelineCheck", csCode);
        Assert.Contains("var smoothTimelineNow = true;", csCode);      // 处理侧仍传 true(VFR 门在 VideoService 里)
        Assert.Contains("SmoothTimeline = true,", csCode);             // 写盘恒 true
        // 读设置时**不许**采纳老值:ApplyVideoParams 里不得出现 `= d.SmoothTimeline`
        Assert.DoesNotContain("= d.SmoothTimeline", csCode);
        // 字段本身保留(兼容旧文件与预设快照)
        Assert.Contains("public bool SmoothTimeline { get; set; }", csCode);
        // 日志行保留(不再是开/关两态)
        Assert.Contains("平滑时间轴", csCode);
    }

    /// <summary>【2026-09-15 最后一轮界面清理】三个"用户不需要决策"的选项从界面删除,固定为内建口径:
    /// ① 「可变帧率保护」面板(`VfrToggleBtn`/`VfrPanel`/`VfrModeRadios`/`VfrHintText` → 固定自动);
    /// ② 「平滑时间轴」勾选框(→ 固定"开"由 VFR 门决定是否真走);
    /// ③ 「补帧输出帧率基准」下拉(`FpsBaseCombo` → 固定真实时间轴,`fpsMode=2`)。
    /// 保留:「转场识别」勾选框(可开可关、默认不勾)、去重、输入帧率、指定输出帧率、补帧开关/模型/倍率、后处理、兼容模式、裁剪。
    /// 【钉什么】控件与界面文字确实不存在;活代码里 0 引用;写盘写内建值;**日志信息不丢**(改成打印内建值)。
    ///
    /// 【2026-09-21 原有的第 ① 项(「转场阈值」滑块)已作废并从这里移出】那条断言当初钉的是
    /// "`SceneSlider`/`SceneVal` 必须不存在"(2026-09-15 用户裁决删滑块时留下的契约)。当晚用户改了口径:
    /// **"转场阈值滑块加回来,并且要真的生效"** ⇒ 该反向断言撤销,滑块与它的全部接线改由下面
    /// <see cref="SceneThresholdSliderIsRestoredAndReallyTakesEffect"/> **正向**钉住
    /// (控件在 + 接进判据 + 默认档与原来写死的值逐字一致)。
    /// 【为什么要在原地写清"为什么撤销"】"某控件必须不存在"这类反向断言过了期就会变成**阻止正确改动的墙**,
    /// 后人看到测试红了只会去猜;把沿革留在注释里,他才知道这是口径变了、不是他改错了。</summary>
    [Fact]
    public void Removed_options_are_gone_with_builtin_values_and_logs_kept()
    {
        var xaml = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml");
        foreach (var name in new[] { "VfrToggleBtn", "VfrPanel", "VfrModeRadios",
                                     "VfrHintText", "FpsBaseCombo", "SmoothTimelineCheck", "SmoothTimelineHint" })
            Assert.DoesNotContain($"x:Name=\"{name}\"", xaml);
        // 界面上也不许再有这些选项的可见文字(注释里为沿革提到名字是允许的,所以只查元素/标题形态)
        Assert.DoesNotContain("可变帧率保护 ▾", xaml);
        Assert.DoesNotContain("Text=\"补帧输出帧率基准\"", xaml);
        Assert.DoesNotContain(">平滑时间轴<", xaml);

        var cs = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
        var code = string.Join("\n", cs.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));
        // 注意:去重那边还有 **Dedup**SceneSlider / **Dedup**SceneVal(同名前缀,属于保留的功能)——
        // 转场那条 SceneSlider/SceneVal 现在也**合法存在**了(见下一个用例),所以不再做任何反向断言。
        foreach (var name in new[] { "VfrModeRadios", "VfrPanel", "VfrToggleBtn", "FpsBaseCombo", "SmoothTimelineCheck" })
            Assert.DoesNotContain(name, code);

        // 内建口径(写入时盖章,老值不会被反复采纳/改写)
        Assert.Contains("VfrMode = 0,", code);
        Assert.Contains("FpsBase = 0,", code);
        Assert.Contains("SmoothTimeline = true,", code);
        Assert.Contains("var fpsBaseNow = 2;", code);                  // 真实时间轴
        Assert.Contains("vfrPassthrough: item.IsVfr,", code);          // 固定自动(不再有"不启用")

        // 兼容日志:老设置/老预设里带着被删选项的旧值时写一行;两条加载路径都要调用
        Assert.Contains("WarnIfLegacyTimelineOptions(d)", code);
        Assert.Contains("WarnIfLegacyTimelineOptions(preset.Params)", code);
        Assert.Contains("[记忆] 设置里有已删除的选项", code);

        // 日志信息不丢:改成打印内建值
        Assert.Contains("输出基准=真实时间轴(原帧率×倍率)(内置)", code);
        Assert.Contains("VFR=自动(", code);
        Assert.Contains("平滑时间轴", code);

        // 【2026-09-21】「转场阈值」已从"被删选项"名单里彻底移出:滑块回来了 ⇒ 它是**活选项**,
        // 老值会被正常采纳(见 ApplyVideoParams)—— 兼容日志里不许再出现"转场阈值=…(内置 …)"这种口径。
        Assert.DoesNotContain("转场阈值=", code);
    }

    /// <summary>【2026-09-21 用户要求:滑块加回来,并且要**真的生效**】正向钉住三件事,缺一不可:
    ///   ① XAML 里控件真的在(标题/滑块/数值/重置按钮四件齐),刻度参数与 Core 的常量逐条对得上;
    ///   ② 它的值**进判据**:读盘采纳用户存的值、写盘写真实值(不再用常量顶替);
    ///   ③ 换算只有一处(<c>AlhPro.Core.SceneThresholdMap</c>),处理端吃的是换算结果而不是滑块原值;
    ///      默认档 0.30 换算出来 = 原来写死的 25 / 50 / 0.6 ⇒ 用户不动滑块,画面一个字节都不变。
    /// 【为什么必须是"正向"契约】"滑块必须不存在"那条反向断言上一版还在,而当晚用户要的正是它回来 ——
    /// 只把旧断言删掉不够,还要有一条正面看着它别再变成**假控件**(2026-09-15 删它的理由正是"不影响判定")。</summary>
    [Fact]
    public void SceneThresholdSliderIsRestoredAndReallyTakesEffect()
    {
        var xaml = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml");
        // ① 四件齐
        Assert.Contains("Text=\"转场阈值\"", xaml);
        Assert.Contains("x:Name=\"SceneSlider\"", xaml);
        Assert.Contains("x:Name=\"SceneVal\"", xaml);
        Assert.Contains("Click=\"ResetSceneBtn_Click\"", xaml);
        // 刻度(0.15~0.90 / 步长 0.05 / 默认 0.3)必须与 Core 常量对得上:界面写的是字面量,这里逐条核对
        int si = xaml.IndexOf("x:Name=\"SceneSlider\"", StringComparison.Ordinal);
        int se = xaml.IndexOf("/>", si, StringComparison.Ordinal);
        var sliderTag = xaml.Substring(si, se - si);
        Assert.Contains($"Minimum=\"{SceneThresholdMap.SliderMin:0.00}\"", sliderTag);   // 0.15
        Assert.Contains($"Maximum=\"{SceneThresholdMap.SliderMax:0.0}\"", sliderTag);    // 0.9
        Assert.Contains($"StepFrequency=\"{SceneThresholdMap.SliderStep:0.00}\"", sliderTag);
        Assert.Contains($"Value=\"{SceneThresholdMap.DefaultSlider:0.0}\"", sliderTag);
        Assert.Contains("ValueChanged=\"Slider_Changed\"", sliderTag);   // 改了就联动(刷数值 + 写盘)

        var cs = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
        var code = string.Join("\n", cs.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));
        // ② 读设置采纳用户的值(上一版是"忽略 d.SceneThr、写盘写内置 0.3")
        Assert.Contains("SceneSlider.Value = AlhPro.Core.SceneThresholdMap.Snap(d.SceneThr);", code);
        // ② 写设置按真实值(不是常量)
        Assert.Contains("SceneThr = AlhPro.Core.SceneThresholdMap.Snap(SceneSlider.Value),", code);
        Assert.DoesNotContain("SceneThr = SceneThresholdBuiltIn,", code);
        // 数值框跟着滑条刷新(否则用户看不出自己拖到了哪一档)
        Assert.Contains("SceneVal.Text = AlhPro.Core.SceneThresholdMap.Snap(SceneSlider.Value)", code);
        // 重置按钮真的在(不是只剩 XAML 上的名字)
        Assert.Contains("private void ResetSceneBtn_Click(", code);

        var svc = ReadRepoFile("ImgUpscalerUI", "VideoService.cs");
        // ③ 处理端吃的是换算结果(不是滑块原值)
        Assert.Contains("thresholds: sc.Thresholds", svc);
    }

    /// <summary>【2026-09-22 用户裁决 · **反向契约**:「最短补帧段」整块撤掉,不许再加回来】
    ///
    /// 【它是什么、为什么撤】见 VideoView.xaml 里那段留档:那条规则把"会让某段短于 60 帧的切点"从清单里合并掉,
    /// 用来减少补帧引擎的冷启动次数;**代价是拿鬼影防线换速度** —— 被合并掉的切点处照常插帧,那恰好是真转场时
    /// 就插出一帧跨切混合。而「转场阈值」滑块能用**不牺牲任何保护**的方式达到同一个目的。
    /// 【为什么要有这条反向断言】它的动机("引擎启动太慢")是**真实存在**的问题 —— 后来人看到那个问题,
    /// 很可能把同一套东西再实现一遍。所以这里把"整块已经删干净"钉死:规则、常量、界面控件、设置字段、日志口径,
    /// 五处都不能留下半个。</summary>
    [Fact]
    public void MinSegmentRuleAndItsSwitch_are_gone_and_must_not_come_back()
    {
        // ① Core:常量与函数都删了(留着就是"随时能接回去"的半成品)
        // 【只查代码行】留档注释里**必须**能提到它们的名字(否则后人不知道删的是什么),所以注释不算。
        var core = ReadRepoFile("AlhPro.Core", "SceneCutPlan.cs");
        var coreCode = string.Join("\n", core.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));
        Assert.DoesNotContain("public const int MinSegmentLen", coreCode);
        Assert.DoesNotContain("ApplyMinSegmentLen", coreCode);
        Assert.DoesNotContain("MinSegmentLen", coreCode);
        // 留档要在:说明"原有两个成员、为什么删、错在拿保护换速度"
        Assert.Contains("已删除 · 2026-09-22", core);
        Assert.Contains("MinSegmentLen", core);
        // ② 处理端不再有任何调用/日志
        var svc = ReadRepoFile("ImgUpscalerUI", "VideoService.cs");
        var svcCode = string.Join("\n", svc.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));
        Assert.DoesNotContain("MinSegmentLen", svcCode);
        Assert.DoesNotContain("合并过短段", svcCode);
        Assert.DoesNotContain("ApplyMinSegmentLen", svcCode);
        // ③ 界面:控件没了(注释里写留档是允许的),而且**新加的重置按钮要有 AutomationId** ——
        //    2026-09-22 整机自测时逮到:视频页 9 个「重置」按钮**都没有 AutomationId**,UIA 只能按模糊的名字
        //    「重置」去猜(永远选中第一个)⇒ 自测无法验证"点这一行的重置"。用户手点没影响,但这属于
        //    "无障碍/自动化拿不到"的缺陷,顺手补上我这轮新加的那个。
        var xamlRaw = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml");
        Assert.DoesNotContain("x:Name=\"SceneMinSegmentCheck\"", xamlRaw);
        Assert.Contains("x:Name=\"ResetSceneBtn\"", xamlRaw);
        // ④ 设置字段没了、读写点也没了
        var cs = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
        var code = string.Join("\n", cs.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));
        Assert.DoesNotContain("SceneMinSegment", code);
        // ⑤ 选项记录里也不再携带它(SceneCutOptions 只剩阈值一项)
        var opts = ReadRepoFile("AlhPro.Core", "SceneCutOptions.cs");
        Assert.DoesNotContain("MergeShortSegments", opts);
        Assert.DoesNotContain("MinSegmentLen", opts);
        Assert.Contains("public sealed record SceneCutOptions(double Threshold)", opts);
        // ⑥ 但"留档"必须在 —— 否则后人只看到"少了个功能",看不到"为什么删、错在哪"
        Assert.Contains("拿\"鬼影防线\"换速度", xamlRaw);
    }

    private static int Regex_Count(string text, string pattern)
        => System.Text.RegularExpressions.Regex.Matches(text, pattern).Count;

    /// <summary>从测试输出目录往上找仓库根,再读指定文件(与 VideoModelOrderTests 同一套定位方式)。</summary>
    private static string ReadRepoFile(params string[] parts)
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var cand = System.IO.Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (System.IO.File.Exists(cand)) return System.IO.File.ReadAllText(cand);
            dir = dir.Parent;
        }
        throw new System.IO.FileNotFoundException("找不到仓库文件: " + string.Join('/', parts));
    }
}
