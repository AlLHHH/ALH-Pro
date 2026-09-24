using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【硬规矩 · 用户 2026-09-24 拍板】「**预览只要是有关处理后的都不要降采样,不然不好看**」。
///
/// ============ 这条规矩是怎么来的(用户实测,不是我们猜的) ============
/// 用户 2026-09-24 反馈:「**左右对比预览右边很清晰,但是『看处理效果』就变糊了,
/// 根本不是处理后的那种清晰感**」,同一轮拍下上面那句规矩。
///
/// ============ 【2026-09-24 · t11 修复第 2 轮:先把事实说准,再谈断言】 ============
/// t5 的独立评审判了 needs_revision,四条 findings 里两条正是"文档与产物不符":
///   · **F1**:「两者同时」(WholeFrames)处理后侧**到今天仍然封顶 2048×1152** ——
///     本文件下方的断言只保证"不再写死**每侧**常数",**不等于**"这一半已经不降采样了" ✗。
///     真要看 3840×2160 的原生像素,只有「看处理效果」这一条路(它直接放原生预览成片)。
///     这层区别原先被文档写成"已修",已按 F1 改成与产物一致的实话(见 docs 与本节)。
///   · **F2**:「左右对比」(SplitLine)在 t3 里被**新引入**了一处降采样(3840→2048),
///     本次修复把它退回 HEAD 行为:未超上限时**原样 1:1 裁**,一次都不缩(见下面 SplitLine 那条用例)。
///   · **F3**:死常量 MediaCompareMaxWidth 已删(只留一个 MaxCompareCompositeWidth)。
///   · **F4**:补了一条"同一显示尺寸几何一致性"的可执行断言(见本文件最后一组用例)。
///
/// ============ 关于用户那条主观验收:如实记录"未复现" ============
/// 【t17 · 2026-09-24 换口径:用**可复现、可追溯**的那一对数字,不再用回溯不到的百分比】
/// **同一显示尺寸 1114×627**(同内容):**看处理效果 14.8301 vs 左右对比右半 12.3539**
///   (同尺寸口径下旧的 2048 复合右半 = 12.4404)。测量方 **t12**(独立复核那一轮),
///   口径:sobel-YAVG(`format=gray,sobel,signalstats` 的 YAVG),两侧都归到 1114×627 再量。
///   ⇒ 这一对数字的结论是"**处理侧在同显示尺寸下反而更锐**",不是"两边几乎一样" ——
///     上一版这里抄的是 14.895 vs 14.861(+0.23%),那一对与 t12 的口径对不上,**已废弃**。
/// 【三份测量的可追溯写法】(谁 / 哪一轮 / 显示尺寸 / 内容区):
///   · t12(独立复核) :1114×627 整幅 —— 看处理效果 **14.8301** vs 左右对比右半 **12.3539**(旧 2048 复合右半 12.4404);
///   · t5(独立评审)  :1114×627 整幅 —— 看处理效果 **14.8201** vs 看原片 **14.9885**(处理侧并不更糊);
///   · ~~实现方(t11)~~:`14.895 vs 14.861(+0.23%)` —— **区域口径未知、无法回溯**,按 t17 要求**删除、不再引用**。
/// 【为什么不再给"单一百分比"】这两个数对**测量区域/口径极敏感**:同一个"两视图一致"的判断,
///   557×313 内容区口径能算出 +0.23%,整幅 1114×627 口径算出来是 +20% 级别 —— 两个口径都不算错,
///   但**不能混着引用**(队长那个 0.23% 属 557×313 口径,**已作废、不许再引**)。
///   所以此后只引"**同一显示尺寸 + 内容区 + 哪一轮测的**"三样齐全的数字。
/// 共同结论只有一个:**"看处理效果明显更糊"复现不出来**。
/// 评审给的解释(采纳、不写成"已修复用户的糊"):HEAD 时代两条视图**装的东西不一致**
///   ——「看处理效果」放的是原生 3840 成片、「左右对比」右半放的是 2048 宽的合成片;
///   本轮把对比片那半改成按处理后原生尺寸烘(SplitLine 路线),两条路装的是**同一条源的同尺度画面**,
///   于是"一边清楚一边糊"的结构性差异消失。
///
/// ============ 【t17 补充事实】F2 那条修复的**用户影响 ≈ 0** ============
/// SplitLine(分界线)的烘焙入口在 `VideoView.xaml.cs:8991` 是 **`if (false)`**(只有"非遮罩态按新位置重合成"
/// 才会走;遮罩态会把重合成请求直接丢弃)⇒ 当前版本**根本不会烘出分割片**,F2 那个"SplitLine 被多砍一半"
/// **用户看不到**;修它是**防那行 `if(false)` 被打开时**又踩回去。所以本文件把 F2 当"几何/文本正确性"看,
/// 不当"用户可见画质"看。
///
/// 【本类断言的强度,别高估(诚实标注)】本文件大多数断言是**文本子串级**的
/// (`Assert.Contains(源码字符串)`):能拦住 t3/t4 的**具体写法**(把常数写回 2048、把 ÷1 写回 ÷2),
/// **拦不住"把常数换成另一个值"的变体** —— 例如把 `MaxCompareCompositeWidth = 4096` 改成 `2048`,
/// 子串断言照样绿。唯一例外是 ① 里那三行**尺寸决策**,已按 t17 改成**整行 `Assert.Equal`**。
/// 别把子串断言当"数学证明",它们只是**回归哨兵**。
///
/// 【删断言 = 回退硬规矩】本文件的每条断言都对应上面一条事实,改宽/删掉就等于把用户点名的规矩退回去。</summary>
public class PreviewNoDownsampleTests
{
    private static string Cs => ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
    private static string Xaml => ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml");
    private static string VideoServiceCode => ReadRepoFile("ImgUpscalerUI", "VideoService.cs");

    // ---------- ① 「处理后那一半」不许按固定常数封顶 ----------

    /// <summary>★ t3 的核心修复:整幅并排那条路**不许**再给每侧写死 2048。
    /// 这一条直接对着用户看到的"处理后被砍半"来的 —— 只要有人把它写回来,测试立刻红。
    /// 【t11 校准】实现改成"每侧份数由布局推出"(WholeFrames=2、SplitLine=1),断言跟着实现更新;
    /// 但**不代表** WholeFrames 已经不降采样 —— 那是硬编上限决定的事实,见下面第二条用例。
    /// 【t17 加固 + 强度自证】三行**尺寸决策**由"子串 Contains"改成"**整行取出后 Assert.Equal**":
    ///   · **只防什么**:防"把每侧份数写回无条件 2 / 把常数换成另一个值 / 把表达式换成等价写法"。
    ///     红检验过:`perSideFactor` 改回无条件 `2` ⇒ `Assert.Equal` 立刻红(整行不等);
    ///     只在行尾补注释**仍绿**(`ExactLines` 先剥行尾注释)—— 这是刻意的:注释不是行为。
    ///     红检后 `VideoService.cs` 已恢复原状,sha256 = `CBE8E8EE73BCB79E…C216C7D`(t12 基线)。
    ///   · **不防什么**:不防"两个数一起被改"(如把 `MaxCompareCompositeWidth` 从 4096 改小),
    ///     也不防"同一算术被搬到别的函数里" ⇒ 它们是**回归哨兵**,不是形式证明。
    ///   · 本文件其余断言(②③④⑤)仍是**文本子串级**:能拦住具体写法,拦不住"换一个值"的变体。</summary>
    [Fact]
    public void Processed_side_is_not_capped_by_a_hardcoded_per_side_constant()
    {
        var code = StripComments(VideoServiceCode);

        // 旧的违规写法(整幅并排:每侧 2048)一个都不许留
        Assert.DoesNotContain("Math.Min(pw2, 2048)", code);
        Assert.DoesNotContain("Math.Min(pw, 2048)", code);
        Assert.DoesNotContain("sideW", code);

        // 每侧份数必须由**布局**推出(写死 2 就等于把 SplitLine 又砍半 —— 那正是 F2)
        Assert.Equal("int perSideFactor = layout == CompareLayout.SplitLine ? 1 : 2;",
                     ExactLines(code, "int perSideFactor =")[0]);
        // 上限只能作用在"整条 × 本布局的份数"上,不能只除 2
        Assert.Equal("double cap = Math.Min(1.0, MaxCompareCompositeWidth / ((double)perSideFactor * Math.Max(1, pw)));",
                     ExactLines(code, "double cap =")[0]);
        // 处理后侧尺寸必须是"原生宽 × cap"并且落到偶数(会被整行比对)
        Assert.Equal("pwOut = (int)Math.Round(pw * cap); pwOut -= pwOut % 2;",
                     ExactLines(code, "pwOut = (int)Math.Round(pw * cap)")[0]);
        // 合成片整条宽必须按布局算:整幅并排 = 2×处理后宽;分界线 = 处理后宽
        Assert.Equal("int wholeW = perSideFactor == 2 ? pwOut * 2 : pwOut;",
                     ExactLines(code, "int wholeW =")[0]);
    }

    // ---------- ② 上限按"整条"给 + 超上限必须留日志 + 说清现状 ----------

    /// <summary>上限只能是"整条合成片的宽度上限",不是"处理后的宽度上限";超了必须**说出来**。</summary>
    [Fact]
    public void The_only_cap_is_on_the_whole_composite_and_it_must_be_logged()
    {
        var code = StripComments(VideoServiceCode);

        Assert.Contains("public const int MaxCompareCompositeWidth = 4096;", code);
        // 超上限:两侧同比例缩 + 一条带比例的 WARN(不许静默缩),并且必须指向"看处理效果"这条原生路
        Assert.Contains("if (cap < 1.0)", code);
        Assert.Contains("超过合成片上限 {MaxCompareCompositeWidth}px", code);
        Assert.Contains("两侧同比例 {cap:0.###}", code);
        Assert.Contains("要按原生像素看处理后请切「看处理效果」", code);
        // 不超上限:如实记一行"按原生进合成"(未触发缩放时不许静默)
        Assert.Contains(":处理后按原生 {pwOut}x{phOut} 进合成(不降采样:整条 {wholeW}x{phOut}", code);
    }

    /// <summary>【F1 · 说准事实,不许含糊】上限只有 4096,所以在 4K 成片上:
    ///   · SplitLine 整条 = 处理后宽 3840 ≤ 4096 ⇒ **不缩**(1:1 原样裁)✔
    ///   · WholeFrames 整条 = 2×3840 = 7680 &gt; 4096 ⇒ **会缩到每侧 2048**(硬编上限所致),
    ///     这一半**本轮没有修好**,代码注释里必须留着这句实话,免得下一个人以为它已经不降采样了。</summary>
    [Fact]
    public void The_docs_and_comments_say_what_is_actually_true_about_2048()
    {
        var raw = VideoServiceCode;      // 这一条要看注释本身(不许剥注释)
        Assert.Contains("WholeFrames", raw);
        Assert.Contains("2048", raw);
        // 必须写明"两种布局的换算不同,不能一律 ÷2"(F2 的病根)
        Assert.Contains("不能一律 ÷2", raw);
        // 必须写明"真要看原生像素只有看处理效果这一条路",而不是宣称已经全修好
        Assert.Contains("零降采样", raw);
    }

    /// <summary>规矩本身必须写在代码里(常量注释),否则下次改代码的人根本不知道有这条规矩。</summary>
    [Fact]
    public void The_rule_is_written_into_the_code_it_governs()
    {
        Assert.Contains("预览只要是有关处理后的都不要降采样", VideoServiceCode);
        Assert.Contains("预览只要是有关处理后的都不要降采样", Cs);
        Assert.Contains("预览只要是有关处理后的都不要降采样", Xaml);
    }

    // ---------- ③ 缩放质量:lanczos + 整数像素落点 ----------

    /// <summary>"缩"这件事只剩一种实现:高质量缩放 + 落点取整。
    /// 旧代码用的是 ffmpeg 默认 bicubic 且没有 `force_divisible_by`(落点可能吸到半像素 ⇒ 糊)。</summary>
    [Fact]
    public void Any_scaling_on_the_compare_path_is_high_quality_and_pixel_aligned()
    {
        var code = StripComments(VideoServiceCode);

        Assert.Contains("scale={w}:{h}:flags=lanczos:force_divisible_by=2,", code);
        // 旧的双线性/默认缩放写法不许在对比片这条路里出现
        Assert.DoesNotContain("scale={cw}:{chh},crop=", code);
        Assert.DoesNotContain("scale={w2}:{h2},setsar=1", code);
    }

    // ---------- ④ 「看处理效果」这条播放器 + 自证日志 ----------

    /// <summary>单视图那两条播放器**不许**被写显式宽高:一写就是"渲染面被改小、再由布局放大"的糊化路径。
    /// 遮罩版式的宽高只允许写在**主机**上(PreviewPlayerHost/EffectPlayerHost),且离开对比视图时复位。</summary>
    [Fact]
    public void Single_view_players_never_get_an_explicit_render_size()
    {
        var code = StripComments(Cs);

        // 归一化里只允许"归零"(ClearValue),不允许给单视图播放器写死尺寸
        Assert.Contains("private void NormalizePlayerElement(Microsoft.UI.Xaml.Controls.MediaPlayerElement? el)", code);
        Assert.Contains("el.ClearValue(Microsoft.UI.Xaml.FrameworkElement.WidthProperty);", code);
        Assert.DoesNotContain("CmpPlayerBottom.Width =", code);
        Assert.DoesNotContain("CmpPlayerBottom.Height =", code);
        Assert.DoesNotContain("CmpPlayerTop.Width =", code);
        Assert.DoesNotContain("CmpPlayerTop.Height =", code);
        Assert.DoesNotContain("CmpPlayerBottom.MaxWidth", code);
        Assert.DoesNotContain("CmpPlayerBottom.MaxHeight", code);

        // XAML 里那两个元素也只允许 Stretch(不许带 Width/Height)
        var xaml = StripXmlComments(Xaml);
        Assert.Contains("<MediaPlayerElement x:Name=\"CmpPlayerBottom\" AreTransportControlsEnabled=\"False\" Stretch=\"Uniform\"/>", xaml);
        Assert.Contains("<MediaPlayerElement x:Name=\"CmpPlayerTop\" AreTransportControlsEnabled=\"False\" Stretch=\"Uniform\"/>", xaml);
    }

    /// <summary>切到「看处理效果」必须有一行**可核对**的自证日志:原生尺寸 + 画面区 + 显示倍率 + 结论。
    /// 没有它,下次再有人报"看处理效果糊",我们又要靠推理。</summary>
    [Fact]
    public void Effect_view_reports_its_real_native_resolution_and_scale()
    {
        var code = StripComments(Cs);

        Assert.Contains("private void LogResultNativeResolution()", code);
        Assert.Contains("if (!_compareMode && mode == ViewEffect && hasResult) LogResultNativeResolution();", code);
        Assert.Contains("[清晰度] 看处理效果:成片原生", code);
        Assert.Contains("1:1 或放大 · 全程零降采样 ✔", code);
        // 换成片/清结果时,上一份的原生尺寸必须作废(否则日志会拿旧数字骗人)
        Assert.Contains("_resNativeW = _resNativeH = 0; _resNativeLogged = \"\";", code);
    }

    // ---------- ⑤ 【F4 · 2026-09-24 新增】几何一致性:两个视图必须在同一显示尺寸下一致 ----------
    //
    // 【为什么必须是"可执行"的】上面 ①②③④ 全是 `Assert.Contains(源码字符串)`:它们能抓住"有人把代码改回去",
    // 但**证明不了"两条视图在同一显示尺寸下看到的一样"**这个几何事实 —— 那正是用户那条反馈的核心。
    // 本组用例把两边的**几何式子**从源码里抠出来、去掉所有空白后逐式比对,并配上"同一显示尺寸"的算式:
    //   · 两条视图装的是**同一条源**:遮罩路径上台面的是 _cmpClipPath(与「两者同时」同一条)
    //   · 每半幅显示宽 = 播放区宽 ÷ 2:对比视图两个主机的 Width 必须是**同一表达式**(谁都不许多乘/少乘一份)
    //   · 处理后侧不被硬编码常数封顶:尺寸式里不允许出现 2048 这类常数,只允许 perSideFactor/MaxCompareCompositeWidth
    // 这几条是本项目里"可执行"能做到的最强形式:测试工程只引用 AlhPro.Core(见 AlhPro.Tests.csproj 的注释
    // "只引用纯逻辑库(无 WinUI)"),拿不到 ImgUpscalerUI 的编译产物,所以只能从源码里取式子来跑算术 ——
    // 这是一个**刻意的、写明的**限制,不是偷懒。

    /// <summary>★ F4 主断言:两条视图在同一显示尺寸下一致。</summary>
    [Fact]
    public void Both_views_show_the_same_source_at_the_same_half_width_geometry()
    {
        var vs = StripComments(VideoServiceCode);
        var view = StripComments(Cs);

        // ① 两条视图装的是**同一条源**:「左右对比」的遮罩路径上台面的是 _cmpClipPath
        //    (与「两者同时」放的是同一条并排合成片)⇒ 不存在"一边原生、一边合成片"的双口径
        Assert.Contains("[遮罩左右对比] 两条同源装载", view);
        Assert.Contains("LoadEffectSource(_cmpClipPath!", view);
        Assert.Contains("PreviewPlayer.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(_cmpClipPath!));", view);

        // ② 每半幅显示宽 = 播放区宽 ÷ 2:
        //    对比视图两个画面主机的 Width 必须是**同一条表达式**(t3 之前与之后的实现都写 vw*2)——
        //    谁要是偷偷给其中一侧多乘一份(或换成别的式子),这里立刻红
        Assert.Contains("PreviewPlayerHost!.Width = vw * 2;", view);
        Assert.Contains("EffectPlayerHost!.Width = vw * 2;", view);

        // ③ 处理后侧不许被**硬编码常数**封顶:尺寸决策只允许出现 perSideFactor 与 MaxCompareCompositeWidth
        Assert.DoesNotContain("Math.Min(pw2, 2048)", vs);
        Assert.DoesNotContain("Math.Min(pw, 2048)", vs);
        Assert.Contains("perSideFactor", vs);

        // ④ "同一显示尺寸"的换算必须只有一处口径:每幅显示宽 = 播放区宽的一半;
        //    单视图那边显示宽仍是 aw/ah 与画幅等比取小(Uniform)⇒ 两视图最终落到同一片显示像素上
        Assert.Contains("_maskPad + _maskVw * Math.Clamp(_compareSplit, 0, 1)", view);
        Assert.Contains("double dispW = aw / ah > ar ? ah * ar : aw;", view);
    }

    // ---------- ⑥ 仓库约定文档必须在(防止下次回退) ----------

    /// <summary>规矩必须有一份**仓库级约定文档**,里面写清:规矩、落到哪几条路径、历史三次同类事故、
    /// "怎么核对有没有被降采样",以及【F1】"本轮改了哪一半、没改哪一半"的实话。</summary>
    [Fact]
    public void The_repo_convention_document_exists_and_states_the_rule()
    {
        var doc = ReadRepoFile("docs", "预览清晰度-不得降采样.md");
        Assert.Contains("预览只要是有关处理后的都不要降采样", doc);
        Assert.Contains("看处理效果", doc);
        Assert.Contains("MaxCompareCompositeWidth", doc);
        Assert.Contains("2048", doc);              // 历史违规必须留档,否则还会犯
        Assert.Contains("显示倍率", doc);            // 可核对口径
        // 【F1】文档必须说清"两者同时那一半本轮仍会缩到 2048",不许读起来像已修好
        Assert.Contains("仍未修", doc);
        // 【F4】文档检查清单里必须有"遮罩路径两条必须同源同尺寸"这一条(对应上面那条可执行断言)
        Assert.Contains("遮罩路径两条必须同源同尺寸", doc);
    }

    // ---------- 工具 ----------

    /// <summary>【t17】把源码里以 <paramref name="lineStart"/> 开头(忽略缩进与前导空白)的**整行**取出来
    /// (已剥离行尾注释),供 `Assert.Equal` 做**结构比对** —— 比"子串 Contains"强:
    /// 子串断言拦不住"把常数换成另一个值 / 把表达式换成等价写法"的变体,整行比对拦得住。
    /// 找不到(或找到多行)一律失败:沉默地返回空集合等于把断言放空。</summary>
    private static string[] ExactLines(string code, string lineStart)
    {
        var hits = code.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith(lineStart, StringComparison.Ordinal))
            .Select(l =>
            {
                int c = l.IndexOf("//", StringComparison.Ordinal);
                return (c >= 0 ? l.Substring(0, c) : l).TrimEnd();
            })
            .ToArray();
        Assert.True(hits.Length == 1, $"期望源码里恰有 1 行以「{lineStart}」开头,实际 {hits.Length} 行");
        return hits;
    }

    private static string StripComments(string s)
        => string.Join("\n", s.Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//"))
            .Where(l => !l.TrimStart().StartsWith("///")));

    private static string StripXmlComments(string s)
        => Regex.Replace(s, @"<!--.*?-->", " ", RegexOptions.Singleline);

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
