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
    /// (6/6/1 —— 退回而不是再提,避免二次覆盖用户对官方预设的自定义,理由见 BuiltinPresets 注释)。
    /// 【为什么这条重要】预设是"一整套参数快照",里面就带着 Scene;若预设里是 true,用户点一下
    /// 「动漫通用」就会**莫名打开**转场识别(用户看到的只是那个勾自己出现了)。</summary>
    [Fact]
    public void Builtin_presets_keep_scene_off_and_revert_their_rev()
    {
        var cs = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
        Assert.DoesNotContain("Scene = true,", cs);
        int off = Regex_Count(cs, "Scene = false, SceneThr = 0.3,");
        Assert.True(off >= 3, $"官方预设里的 Scene=false 只有 {off} 处(应为 3 处)");
        // Rev 已退回原值(不再是"为默认开而提上去"的 7/7/2)
        Assert.Contains("( \"通用画质增强 不含补帧\", 6,", cs);
        Assert.Contains("( \"动漫通用\", 6,", cs);
        Assert.Contains("( \"去重补帧4x\", 1,", cs);
        Assert.DoesNotContain("( \"动漫通用\", 7,", cs);
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

    /// <summary>【2026-09-15 最后一轮界面清理】四个"用户不需要决策"的选项从界面删除,固定为内建口径:
    /// ① 「转场阈值」滑块(`SceneSlider`/`SceneVal`,值改用内置常量 `SceneThresholdBuiltIn`);
    /// ② 「可变帧率保护」面板(`VfrToggleBtn`/`VfrPanel`/`VfrModeRadios`/`VfrHintText` → 固定自动);
    /// ③ 「平滑时间轴」勾选框(→ 固定"开"由 VFR 门决定是否真走);
    /// ④ 「补帧输出帧率基准」下拉(`FpsBaseCombo` → 固定真实时间轴,`fpsMode=2`)。
    /// 保留:「转场识别」勾选框(可开可关、默认不勾)、去重、输入帧率、指定输出帧率、补帧开关/模型/倍率、后处理、兼容模式、裁剪。
    /// 【钉什么】控件与界面文字确实不存在;活代码里 0 引用;写盘写内建值;**日志信息不丢**(改成打印内建值)。</summary>
    [Fact]
    public void Removed_options_are_gone_with_builtin_values_and_logs_kept()
    {
        var xaml = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml");
        foreach (var name in new[] { "SceneSlider", "SceneVal", "VfrToggleBtn", "VfrPanel", "VfrModeRadios",
                                     "VfrHintText", "FpsBaseCombo", "SmoothTimelineCheck", "SmoothTimelineHint" })
            Assert.DoesNotContain($"x:Name=\"{name}\"", xaml);
        // 界面上也不许再有这些选项的可见文字(注释里为沿革提到名字是允许的,所以只查元素/标题形态)
        Assert.DoesNotContain("Text=\"转场阈值\"", xaml);
        Assert.DoesNotContain("可变帧率保护 ▾", xaml);
        Assert.DoesNotContain("Text=\"补帧输出帧率基准\"", xaml);
        Assert.DoesNotContain(">平滑时间轴<", xaml);

        var cs = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
        var code = string.Join("\n", cs.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));
        // 注意:去重那边有 **Dedup**SceneSlider / **Dedup**SceneVal(同名前缀,属于保留的功能),
        // 所以这里用"前面不是 Dedup"的精确匹配,别把它们误判成残留。
        foreach (var name in new[] { "VfrModeRadios", "VfrPanel", "VfrToggleBtn", "FpsBaseCombo", "SmoothTimelineCheck" })
            Assert.DoesNotContain(name, code);
        Assert.False(System.Text.RegularExpressions.Regex.IsMatch(code, @"(?<!Dedup)SceneSlider"), "SceneSlider 还有活引用");
        Assert.False(System.Text.RegularExpressions.Regex.IsMatch(code, @"(?<!Dedup)SceneVal\b"), "SceneVal 还有活引用");

        // 内建口径(写入时盖章,老值不会被反复采纳/改写)
        Assert.Contains("SceneThr = SceneThresholdBuiltIn,", code);
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
