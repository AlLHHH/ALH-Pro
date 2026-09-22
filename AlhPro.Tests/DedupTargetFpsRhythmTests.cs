using AlhPro.Core;
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace AlhPro.Tests;

/// <summary>「指定输出帧率」这条路的两条接线契约(2026-09-16 用户裁决)。
///
/// 【一 · 处理侧:指定帧率不许再吃掉原片节奏】
/// 去重真删帧时,会把被删帧的时长**合并进前一个保留帧**(`ApplyDedupDrop` → `VideoPipeline.MergeDurations`),
/// 这张"每帧真实时长"表唯一的下游消费者就是可变帧率时间轴(setpts)。而那条路以前**硬写着 `targetFps == null`**:
/// 用户一旦同时勾「去重」+「指定帧率」,这张表就没人消费 → 所有输出帧按目标帧率**均匀**铺开(3 秒定格被快进、
/// 紧接着的快动作被拉慢);总时长不变、画面一帧不丢,但**节奏变了** —— 用户感知就是"内容好像丢了"。
/// 现在判据只看"有没有真实时长表要保"(preserveRhythm),与是否指定帧率无关;指定帧率只决定"倍率补多少"。
/// 代价必须说清楚:保节奏时输出是可变帧率时间轴,**平均帧率 = 基准帧率×倍率**(不再恰好等于指定值)——
/// 所以日志与界面都按"平均约 N fps"上报,不许再写"输出仍精确 N fps"。
/// 【2026-09-21 唯一例外已消失】上一版这里还有一个例外:「果冻修复·运动模糊」—— 那条滤镜链自带 CFR
/// 时间重采样(minterpolate→tmix→fps),与可变时间轴不同源,只能继续挂 fps 滤镜。整块「果冻修复」已按用户
/// 要求从**界面与处理端一起**删除 ⇒ 判据里不再有第二个条件。下面两条用例已按新口径改写:
/// ① 门槛只看 preserveRhythm;② 原来的"唯一例外必须告知"改成 **钉住它彻底消失**(不留"还有一条能丢节奏
/// 的路"的错觉,也不留一条永远打不出来的死分支)。
///
/// 【二 · 界面侧:取值口径只允许有一条】
/// 「指定输出帧率」原来是"勾选框 + 手输数字",而处理端本来就会按目标帧率把倍率算够 —— 界面却把倍率那一栏置灰、
/// 什么都不说,用户看到的就是个黑箱(用户原话:"不应该是自动吗 比如选200帧就自动补帧到200往上倍率什么的")。
/// 现在是一个**预设下拉**(选中即生效),倍率与"大约输出多少帧率"写在下面那行提示里。
/// 取值必须只走 `SelectedTargetFps()` 一处 —— 否则"下拉选 120、某条老代码却去读输入框"这类错位会静默生效。
///
/// 【为什么用契约测试】两条都是"组合条件错位 + 沉默":编译不报错、单测也测不到(要真跑一条删帧疏密不均的
/// 去重素材 / 真人把界面点一遍),只能按本仓库既定手法把源码接线钉住(同 TempSpaceGateTests / SceneSwitchCoverageTests)。</summary>
public class DedupTargetFpsRhythmTests
{
    /// <summary>**核心契约**:保节奏的门槛只看 preserveRhythm,不再要求"没指定帧率";
    /// 【2026-09-21】也不再有任何"例外组合"(运动模糊已整块删除)。</summary>
    [Fact]
    public void Target_fps_no_longer_turns_off_the_vfr_timeline()
    {
        var svc = ReadRepoFile("ImgUpscalerUI", "VideoService.cs");
        // 标识符层面的断言只查**代码行**(注释里会写沿革:"rhythmAtMux 这个别名随之取消"这类说明必须能留)
        var svcCode = string.Join("\n", svc.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));

        // ① 判据只剩 preserveRhythm 一个条件(2026-09-21 起 `rhythmAtMux` 这个别名也取消了 ——
        //    一个值两个名字会让人以为"还有第二条判据")
        Assert.DoesNotContain("rhythmAtMux", svcCode);
        Assert.DoesNotContain("postMotionBlur", svcCode);
        //    **必须有**三个消费点:指定帧率分支 + 尾帧容积 + setpts 本体(少一处说明结构被改过)
        Assert.Equal(3, Count(svcCode, @"if \(preserveRhythm\)"));
        // 老写法必须彻底消失(残一处 = 指定帧率又去吃节奏了)
        Assert.DoesNotContain("if (targetFps == null && preserveRhythm)", svcCode);
        Assert.DoesNotContain("preserveRhythm && (targetFps == null", svcCode);

        // ② 保节奏时不许再挂 fps 滤镜:它按均匀网格重采样,挂上就把节奏抹平了
        int fpsFilter = svc.IndexOf("postParts.Add($\"fps=", StringComparison.Ordinal);
        Assert.True(fpsFilter > 0, "找不到合帧阶段挂 fps 滤镜那行");
        Assert.Equal(1, Count(svcCode, @"postParts\.Add\(\$""fps="));
        int rhythmBranch = svcCode.IndexOf("if (preserveRhythm)", StringComparison.Ordinal);
        Assert.True(rhythmBranch > 0 && rhythmBranch < fpsFilter,
            "fps 滤镜必须挂在「非保节奏」那一支里(preserveRhythm 判断之后)");

        // ③ 倍率只在拆帧前算一次;合帧阶段不许再"临时改倍率"—— 帧早就补完了,改了也补不出来,只会骗人
        Assert.DoesNotContain("已临时按", svc);
        Assert.DoesNotContain("输出仍精确", svc);

        // ④ 口径必须如实:保节奏时输出是平均帧率(不是那个指定值),日志与界面都要说清楚
        Assert.Contains("保节奏", svc);
        Assert.Contains("平均约", svc);

        // ⑤ 时长表只在"去重真的删过帧、且表长度与帧数对得上"时才被信任(这条判据本身不许放宽)
        Assert.Contains(
            "bool preserveRhythm = vfrPassthrough || (dedup && frameDurs != null && frameDurs.Count == frameCount);",
            svc);
    }

    /// <summary>【2026-09-21 反向契约:那条"唯一例外"必须**彻底消失**】
    /// 它原来是「果冻修复·运动模糊」带来的副作用 —— 那条滤镜链自带 CFR 时间重采样,是唯一还能"丢掉原片节奏"
    /// 的组合,所以配了一段 Warn + 一行界面提示。
    ///
    /// 功能整块删除后,若**只删控件、留下那段告知**,会变成两条都错的东西:
    ///   ① 一段**永远打不出来**的死分支(进去的前提 `preserveRhythm && targetFps > 0` 与"进本分支"
    ///      本身矛盾 —— 本分支就是 preserveRhythm == false 那一支);
    ///   ② 更重要的是,它会让下一个读代码的人以为"工程里还有一条能丢节奏的路",于是去找那个不存在的开关。
    /// 所以按本仓库对死分支的规矩(要么给出可达理由,要么删掉)一起删,并在这里钉住"删干净了":
    /// 处理端不许再出现 motion blur 的任何处理逻辑,界面侧也不许再留这条提示文案。</summary>
    [Fact]
    public void The_motion_blur_rhythm_exception_is_gone_entirely()
    {
        var svc = ReadRepoFile("ImgUpscalerUI", "VideoService.cs");
        var cs = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
        // 【只查代码行,不查注释】沿革说明(为什么删、删了哪几样)必须能留在源码里 —— 那是给后来人看的;
        // 这里要钉的是**活代码里**不许再引用它们(真有活引用编译期就报错了,再钉一道防有人"照着注释恢复")。
        var svcCode = string.Join("\n", svc.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));
        var csCode = string.Join("\n", cs.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));

        // ① 旧告知(日志 + 界面)一个都不许剩 —— 它们是与功能一起消失的用户可见文案
        Assert.DoesNotContain("成片节奏将被均匀化", svcCode);
        Assert.DoesNotContain("又指定了「输出帧率」,且「果冻修复·运动模糊」开着", svcCode);

        // ② 那条滤镜链本身(minterpolate→tmix→fps 的 filter_complex 图 + 它的三个变量)彻底没了
        Assert.DoesNotContain("minterpolate=fps={subFpsStr}", svcCode);
        Assert.DoesNotContain("maskedmerge", svcCode);
        Assert.DoesNotContain("subFpsStr", svcCode);
        Assert.DoesNotContain("motionFrames", svcCode);

        // ③ 处理端形参与变量全删(不是"留着形参、忽略它"):留着就还有"哪天顺手接回去"的口子
        Assert.DoesNotContain("postMotionBlur", svcCode);
        Assert.DoesNotContain("postDeshake", svcCode);
        // 连带那个"一个值两个名字"的别名也一并取消(见上面第一条用例)
        Assert.DoesNotContain("rhythmAtMux", svcCode);

        // ④ 界面侧:三个控件名不许再有**活引用**
        Assert.DoesNotContain("MotionBlurCombo", csCode);
        Assert.DoesNotContain("DeShakeCheck", csCode);
        Assert.DoesNotContain("JellySlowHint", csCode);
        Assert.DoesNotContain("mblurNow", csCode);
        Assert.DoesNotContain("deshakeNow", csCode);

        // ⑤ 但"老文件字段一律保留"这条规矩仍然成立:三个字段必须还在 VideoSettings 里(否则老设置直接读不进来)
        Assert.Contains("public int Jello { get; set; }", csCode);
        Assert.Contains("public int MotionBlur { get; set; }", csCode);
        Assert.Contains("public bool DeShake { get; set; }", csCode);
        // ⑥ 而且读取/写入两端都必须**真的不碰它们**(只删界面不删处理端 = 静默行为,本仓库最忌讳)
        Assert.DoesNotContain("= d.MotionBlur", csCode);
        Assert.DoesNotContain("= d.DeShake", csCode);
        Assert.DoesNotContain("MotionBlur = MotionBlurCombo", csCode);
        Assert.DoesNotContain("DeShake = DeShakeCheck", csCode);

        // ⑦ 界面 XAML 里不许再有这三个控件(整块「果冻修复」删除,连标题与那条橙色提示一起)
        var xaml = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml");
        Assert.DoesNotContain("x:Name=\"MotionBlurCombo\"", xaml);
        Assert.DoesNotContain("x:Name=\"DeShakeCheck\"", xaml);
        Assert.DoesNotContain("x:Name=\"JellySlowHint\"", xaml);
        Assert.DoesNotContain("Text=\"果冻修复\"", xaml);
        Assert.DoesNotContain("运动模糊（果冻部分）", xaml);

        // ⑧ 【用户给的验收判据】「视频页看不到「果冻修复」字样」+「用老设置文件启动一次,日志里不再出现
        //    果冻/去抖相关行」。这里按**真正会被用户看到的内容**来判,而不是按整个文件扫字符串:
        //      · XAML:去掉 XML 注释(那是给后来人看的沿革说明,不渲染)之后,一个"果冻/去抖"都不许剩;
        //      · .cs:只查**会写进日志行**的那些语句(Log/AppLogger/progress?.Report),注释同样不算。
        var xamlVisible = Regex.Replace(xaml, "<!--.*?-->", "", RegexOptions.Singleline);
        Assert.DoesNotContain("果冻", xamlVisible);
        Assert.DoesNotContain("去抖", xamlVisible);
        Assert.DoesNotContain("运动模糊", xamlVisible);
        foreach (var src in new[] { svc, cs })
        foreach (var line in src.Split('\n'))
        {
            if (line.TrimStart().StartsWith("//")) continue;   // 沿革注释允许提到它
            if (!line.Contains("Log(\"") && !line.Contains("Log($\"")
                && !line.Contains("AppLogger.") && !line.Contains("progress?.Report(")) continue;
            Assert.DoesNotContain("果冻", line);
            Assert.DoesNotContain("去抖", line);
        }
    }

    /// <summary>界面契约(2026-09-16 用户第三次反馈定稿):**不要**单独的模式按钮组 ——
    /// 「指定帧率」就是「输出倍率」那组单选按钮里**多出来的第 7 项**(序号 6),输入框选倍率时置灰、选中它才可输入。
    /// 用户原话:「不要这个多出来的界面 就像是多出来一个倍率的单选按钮 输入框置灰
    /// 选择这个指定帧率的时候输入框可以输入 而不是多此一举的多一个选倍率和选指定的按钮」。</summary>
    [Fact]
    public void Target_fps_is_a_seventh_item_in_the_multiplier_radio_group()
    {
        var xaml = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml");
        var cs = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");

        // ① 「指定帧率」必须在 InterpScaleRadios 这一组里(同一组 = 同一个 RadioButtons 元素体内)
        var group = Regex.Match(xaml, "x:Name=\"InterpScaleRadios\"[\\s\\S]*?</RadioButtons>");
        Assert.True(group.Success, "找不到 InterpScaleRadios 的元素体");
        Assert.Contains("x:Name=\"TargetFpsRadio\"", group.Value);
        Assert.Contains("Content=\"指定帧率\"", group.Value);
        // 6 档倍率也都在同一组里(不许被挪走)
        foreach (var label in new[] { "\"2x\"", "\"3x\"", "\"4x\"", "\"8x\"", "\"12x\"", "\"16x\"" })
            Assert.Contains($"Content={label}", group.Value);
        // 序号 6 = 指定帧率(契约常量),且这组不许有 MaxColumns(要竖排,与用户要的观感一致)
        Assert.Contains("private const int TargetFpsScaleIndex = 6;", cs);
        Assert.DoesNotContain("MaxColumns", group.Value);

        // ② **不许**再有单独的模式按钮组(那是上一版用户明确否掉的)
        Assert.DoesNotContain("TargetFpsModeRadios", xaml);
        Assert.DoesNotContain("TargetFpsModeRadios", cs);
        Assert.DoesNotContain("FpsModeByScaleRadio", xaml);
        Assert.DoesNotContain("FpsModeByTargetRadio", xaml);
        Assert.DoesNotContain("TargetFpsModeByTarget", cs);

        // ③ 老下拉那一代也必须彻底绝迹
        Assert.DoesNotContain("x:Name=\"TargetFpsCombo\"", xaml);
        Assert.DoesNotContain("TargetFpsCombo", cs);
        Assert.DoesNotContain("TargetFpsPresets", cs);
        Assert.DoesNotContain("TargetFpsIdxOff", cs);

        // ④ 序号 6 不许丢给 InterpScaleMap(它会按越界落回 2x):真正读倍率一律走 CurrentScaleIndex()
        Assert.Contains("private int CurrentScaleIndex()", cs);
        Assert.DoesNotContain("InterpScaleMap.Multiplier(InterpScaleRadios.SelectedIndex)", cs);
        Assert.DoesNotContain("InterpScaleMap.IsHighRate(InterpScaleRadios.SelectedIndex)", cs);
        Assert.Contains("InterpScaleMap.Multiplier(CurrentScaleIndex())", cs);

        // ⑤ 取值只允许走 SelectedTargetFps() 一处;输入框的可输入性由"选中第 7 项"决定
        Assert.Equal(1, Count(cs, @"TryParse\(TargetFpsBox"));
        Assert.Contains("private double? SelectedTargetFps()", cs);
        Assert.Contains("private bool IsTargetFpsMode() => (InterpScaleRadios?.SelectedIndex ?? 0) == TargetFpsScaleIndex;", cs);
        Assert.Contains("TargetFpsBox.IsEnabled = targetMode;", cs);

        // ⑥ 用户裁决"不许留空":空值必须被拦下
        Assert.Contains("IsTargetFpsMode() && SelectedTargetFps() is null", cs);
        Assert.Contains("请填一个目标帧率", cs);

        // ⑦ 必须把"会自动补多少倍"显示出来(用户明确要的就是这个"自动")
        Assert.Contains("private void UpdateTargetFpsHint(bool interp, bool v4Model)", cs);
        Assert.Contains("自动 {need}x 补帧", cs);
    }

    /// <summary>界面契约(2026-09-16 审计第 3 条):目标帧率**低于**源帧率时,提示不许再说"输出仍为输入帧率"。
    ///
    /// 处理侧对目标帧率至少按 2x 补帧、补完挂 `fps` 滤镜缩回用户选的那个值 ⇒ 输出就是用户选的值。
    /// 旧文案("补帧只能增帧,输出仍为 {输入帧率} fps")来自"勾选框+手输"那一代,现在是**说反了的死文案**。</summary>
    [Fact]
    public void Target_fps_below_source_rate_must_not_claim_the_output_stays_at_source_rate()
    {
        var cs = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");

        // ① 旧口径必须彻底消失 —— 查的是**旧代码模式**,不是字符串:
        //    修订说明的注释里会解释"为什么删",所以不能拿旧文案去扫全文件(注释本身会命中)。
        //    这里钉的是旧代码那一行的特征片段(它绝不该再出现在任何地方,含注释)。
        Assert.DoesNotContain("补帧只能增帧,输出仍为", cs);
        Assert.DoesNotContain("InterpHint.Text += $\"\\n注意:指定 {tfNow:0.##} fps 低于输入帧率 {inFps:0.##} fps,补帧只能增帧", cs);

        // ② 新口径必须如实:说清"仍会自动补帧、补完缩到选定的那个值"
        Assert.Contains("仍会自动至少 2x 补帧", cs);
        Assert.Contains("补完再缩到", cs);
        Assert.Contains("输出就是", cs);

        // ③ 处理侧那两条"重复帧/达不到"告知在当前闸门下不可达,必须留说明 —— 免得后人当成活分支去测/去删
        var svc = ReadRepoFile("ImgUpscalerUI", "VideoService.cs");
        Assert.Contains("死代码说明 · 2026-09-16 审计第 3 条", svc);
        Assert.Contains("当前不可达", svc);
    }

    /// <summary>倍率口径(2026-09-16 审计第 4 条):目标帧率的倍率**只有一份**判据 ——
    /// `VideoPipeline.InterpScaleForTargetFps`,界面预判与处理端都必须调它。</summary>
    [Fact]
    public void Target_fps_scale_is_computed_in_exactly_one_place()
    {
        var core = ReadRepoFile("AlhPro.Core", "VideoPipeline.cs");
        var svc = ReadRepoFile("ImgUpscalerUI", "VideoService.cs");
        var cs = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");

        // ① Core 里那份存在,签名不许改(改了调用点会静默走别的分支)
        Assert.Contains(
            "public static int InterpScaleForTargetFps(double targetFps, double inFps, double effectiveFps,",
            core);
        Assert.Contains("bool thisV4, int minScale = 2, int maxScale = 8)", core);

        // ② 两个调用点都在(少一处 = 又有人自己算了一遍)
        Assert.Contains("AlhPro.Core.VideoPipeline.InterpScaleForTargetFps(", svc);
        Assert.Contains("AlhPro.Core.VideoPipeline.InterpScaleForTargetFps(", cs);

        // ③ 老的内联算法不许复活:两处各只剩这一种"目标 ÷ 帧率"写法(注释里的算式不算,只查真代码)
        Assert.DoesNotContain("Math.Ceiling(tf.Value / baseFps)", cs);
        Assert.DoesNotContain("Math.Ceiling(targetFps.Value / Math.Max(1, scaleBase))", svc);
        Assert.DoesNotContain("double scaleBase = fpsMode == 1 ? effectiveFps : inFps;", svc);
    }

    /// <summary>倍率判据本身的行为:基数只认源帧率、下限 2、上限 8、非 v4 取 2 的幂。</summary>
    [Fact]
    public void Target_fps_scale_uses_the_source_rate_and_respects_the_limits()
    {
        // 目标 ≈ 源(60 vs 59.94)→ 仍至少 2x(否则"指定了帧率导出还是卡")
        Assert.Equal(2, VideoPipeline.InterpScaleForTargetFps(60, 59.94, 30, thisV4: true));
        // 目标 120 / 源 60 → 2x
        Assert.Equal(2, VideoPipeline.InterpScaleForTargetFps(120, 60, 60, true));
        // **关键**:去重后内容帧率只有 20,也**不许**拿它当基数(那会把倍率算成 6x,补出来再被丢掉)
        Assert.Equal(2, VideoPipeline.InterpScaleForTargetFps(120, 60, 20, true));
        // 上限 8 是硬顶
        Assert.Equal(8, VideoPipeline.InterpScaleForTargetFps(1000, 60, 60, true));
        // 非 v4(老架构)只能 2 的幂:3 → 4;而 v4 可以精确 3
        Assert.Equal(4, VideoPipeline.InterpScaleForTargetFps(180, 60, 60, thisV4: false));
        Assert.Equal(3, VideoPipeline.InterpScaleForTargetFps(180, 60, 60, thisV4: true));
        // 非 v4 取幂后也受 8 封顶(2→4→8,不会给出 16)
        Assert.Equal(8, VideoPipeline.InterpScaleForTargetFps(1000, 60, 60, thisV4: false));
        // 源帧率取不到时退回内容帧率(而不是崩/给 1)
        Assert.Equal(2, VideoPipeline.InterpScaleForTargetFps(60, 0, 30, true));
        // 非法目标值 → 下限(防御,不抛)
        Assert.Equal(2, VideoPipeline.InterpScaleForTargetFps(0, 60, 60, true));
    }

    /// <summary>记忆参数 / 预设的往返契约(2026-09-16 · 用户要求「预设 记住参数什么的记得也要更新」)。
    ///
    /// 「指定帧率」是「输出倍率」组里多出来的第 7 项(序号 6),但它**不是倍率**。于是存盘这一环有两个坑,
    /// 都是静默的 —— 编译不报错、跑起来也不报错,只有用户重开软件/套预设时才会发现"怎么不是我要的那个":
    ///   ① **存**:若把下拉选中项(6)直接写进 `InterpScale`(约定 0~5=2x…16x),
    ///      读取端那条 `is >= 0 and <= 5` 会静默丢弃它 ⇒ 下次开软件真实倍率变回 2x;
    ///      预设摘要又要拿它去 `InterpScaleMap.Label(6)` 印成 "2x"(把序号当倍率),等于告诉用户一个错数。
    ///      所以必须写 `CurrentScaleIndex()`(选中 6 时返回上一个真实倍率),并另用 Target/TargetFps 记"是否指定"。
    ///   ② **读/恢复**:恢复顺序必须是「先落 `_lastScaleIndex`,再设下拉选中项」。反过来的话,
    ///      `SetTargetFpsSelection` 把选中项切到 6 之后,`UpdateOptions()` 里"选中 6 时不覆盖"那条守卫
    ///      就变成"一个都不记" ⇒ `_lastScaleIndex` 停在初值 0 ⇒ 用户点回倍率 / 换回老模型时被悄悄降成 2x,
    ///      而且下一次 `CollectVideoParams()` 还会把这个 2x 写进记忆参数。
    /// 用户的原话是"预设 记住参数什么的记得也要更新什么的"—— 这两条就是那份"更新"必须落到的位置。</summary>
    [Fact]
    public void Target_fps_round_trips_through_presets_and_remembered_params()
    {
        var cs = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");

        // ① 存:只存真实倍率序号(0~5),"是否指定帧率"另用 Target/TargetFps 记(且都落在同一个快照对象里,
        //    少任何一项 = 重启后要么倍率丢失、要么"指定帧率"整档丢失)
        Assert.Contains("InterpScale = CurrentScaleIndex(),", cs);
        Assert.Contains("Target = targetFpsStored is > 0,", cs);
        Assert.Contains("TargetFps = targetFpsStored?.ToString(\"0.###\", CultureInfo.InvariantCulture) ?? \"\",", cs);
        Assert.DoesNotContain("InterpScale = InterpScaleRadios.SelectedIndex", cs);

        // ② 读:序号 6 绝不能被当成倍率读回来(那会让 TargetFpsRadio 自己变成"倍率 2x")
        Assert.Contains("if (d.InterpScale is >= 0 and <= 5)", cs);
        // ...而且这次读回来时必须**顺带**落 `_lastScaleIndex`:否则下一步切到「指定帧率」时,
        //    "上一个真实倍率"就永远不知道是什么了(这就是 ② 那个坑的入口)
        Assert.Contains("_lastScaleIndex = d.InterpScale;", cs);

        // ③ 恢复顺序:**先**落 `_lastScaleIndex`,**后**切「指定帧率」那一档
        int lastIdxPos = cs.IndexOf("_lastScaleIndex = d.InterpScale;", StringComparison.Ordinal);
        int selectionPos = cs.IndexOf("SetTargetFpsSelection(d.Target, d.TargetFps);", StringComparison.Ordinal);
        Assert.True(lastIdxPos > 0 && selectionPos > 0, "找不到恢复倍率 / 恢复指定帧率这两步");
        Assert.True(lastIdxPos < selectionPos,
            "恢复顺序反了:必须先记 `_lastScaleIndex` 再切「指定帧率」,否则重启后倍率被静默降成 2x");

        // ④ 界面摘要也要跟着更新:预设悬停里必须写清"这一档的倍率是按各片帧率现算的",
        //    否则用户只看到 "60 fps(指定)",无从知道它和「补帧 2x」有什么区别
        Assert.Contains("fps(指定,倍率按各片帧率自动定)", cs);
        Assert.Contains("private static string BuildPresetSummary(VideoPreset p)", cs);
    }

    /// <summary>帧数口径(2026-09-16 · 真机验收):指定帧率时"每源帧展开帧数"必须是**分数步长**,
    /// 且各段严格相加 = 目标帧数 —— 否则"慢放治好了、尾部内容仍然丢"。
    ///
    /// 真机实测(1920×1080 源 519 帧 / 518 去重后 @23.976fps / 21.632s,指定 60fps,自动倍率 3x):
    ///   只改标称帧率时参数行照旧打「补帧后 1555 帧(输出 1555 帧 @60fps)」,而 60fps 只容得下 60×21.632 ≈ 1298 帧
    ///   ⇒ 合帧照样要「裁尾 257 帧(约 4.3 秒)」= 尾部内容被删。根因:整数倍率 ceil(60÷23.976)=3 为了"够用"取上界,
    ///   多补的 257 帧只能丢掉。修法:步长改成 (目标帧数-1) ÷ (源帧数-1) 的分数值,让引擎**恰好**产出目标帧数。
    /// 本测试钉住的就是 Core 那两条"精确分割"运算(界面与处理端都靠它们)。</summary>
    [Fact]
    public void Target_fps_uses_a_fractional_step_and_segments_add_up_exactly()
    {
        var core = ReadRepoFile("AlhPro.Core", "VideoPipeline.cs");
        var svc = ReadRepoFile("ImgUpscalerUI", "VideoService.cs");

        // ① 分数步长必须真的接进补帧阶段:分段目标 / VFR 时长表展开 / 诊断口径三处都读同一个变量
        Assert.Contains("public static int InterpSegmentTargetExact(", core);
        Assert.Contains("public static int AppendExpandedDurationsExact(", core);
        Assert.Contains("InterpSegmentTargetExact(", svc);
        Assert.Contains("AppendExpandedDurationsExact(", svc);
        Assert.Contains("double multRife = multTargetFpsOverride > 1.0 ? multTargetFpsOverride : multStage;", svc);

        // ② 分段目标"严格相加 = 全局目标"(否则下游还要裁尾 = 又丢内容)。
        // 口径 · 绝对定位:第 i 段把整片输出补到第 round(end×k) 帧(k = (totalOut-1)÷(totalSrc-1)),
        // 减去已产出帧数就是本段 `-n`;末段锚到全局目标 ⇒ 合计**构造性**等于目标,一帧不用裁。
        Assert.Equal(1298, VideoPipeline.InterpSegmentTargetExact(518, 518, 518, 1298, producedSoFar: 0));
        const int totalSrc = 518, totalOut = 1298;
        int[] lens = { 130, 129, 129, 130 };   // 合计 518
        int segEnd = 0;
        long cum = 0;                          // 本段开始前已产出的帧数
        for (int i = 0; i < lens.Length; i++)
        {
            segEnd += lens[i];
            int seg = VideoPipeline.InterpSegmentTargetExact(lens[i], segEnd, totalSrc, totalOut, cum);
            Assert.True(seg > lens[i], $"每段产出必须多于输入帧(段{i+1}: {seg} ≤ {lens[i]}),否则 RIFE 退化");
            cum += seg;                        // 已产出 = 前面各段之和 + 首帧
        }
        // 【关键判据】引擎产出必须**恰好**等于目标帧数:少了会被"末帧补足"伪造冻结帧,多了又被裁尾 = 又丢内容
        Assert.Equal(totalOut, cum);

        // ③ 时长表与文件数同源:分数步长下每帧槽数只能是整数,故槽位总数与目标帧数会差**不到一段**的零头
        // (下游 AlignDurationsToCount 按均值补/裁这点零头)。这里钉住"差得在段长以内",而不是假装严格相等。
        var durs = new double[20];
        for (int i = 0; i < durs.Length; i++) durs[i] = 1.0 / 23.976;
        var dst = new System.Collections.Generic.List<double>();
        int segTarget = VideoPipeline.InterpSegmentTargetExact(10, 10, 20, 50, producedSoFar: 0);
        VideoPipeline.AppendExpandedDurationsExact(dst, durs, 0, 10, segTarget, lastSegment: false);
        Assert.True(Math.Abs(dst.Count - segTarget) < 10, $"时长表 {dst.Count} 与目标帧数 {segTarget} 差得超过一段");
        // 末段最后一个源帧只 1 槽(承载尾部容积):槽位总数 ≈ 目标帧数(同样只差不到一段)
        var dst2 = new System.Collections.Generic.List<double>();
        int lastTarget = VideoPipeline.InterpSegmentTargetExact(10, 20, 20, 50, producedSoFar: segTarget);
        VideoPipeline.AppendExpandedDurationsExact(dst2, durs, 10, 20, lastTarget, lastSegment: true);
        Assert.True(Math.Abs(dst2.Count - lastTarget) < 10, $"末段时长表 {dst2.Count} 与目标帧数 {lastTarget} 差得超过一段");
    }

    private static int Count(string text, string pattern)
        => Regex.Matches(text, pattern).Count;

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
