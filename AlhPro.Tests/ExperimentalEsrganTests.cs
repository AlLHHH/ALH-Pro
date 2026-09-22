using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>四支**自训**模型(现实 · alhreal2x / 游戏 · alhgame2x / 游戏 · alhgame2x-v2 / 游戏 · alhgame2x-v3)的登记、命名与文案契约(Rev4 ~ Rev6)。
///
/// 【命名规则 · 用户 2026-09-15 原话】"不要写实验 而且括号内不应该写的是速度吗 前面的实验写成游戏 现实"
///   ⇒ ① 界面文字只许叫「游戏」「现实」,**任何用户可见字符串里不得出现"实验"**;
///   ⇒ ② 括号里写**速度**(不抄官方模型的数字) —— 该口径由用户 2026-09-16 收窄为**速度档「快」**
///      (Core.ExperimentalEsrgan.SpeedTier);测出来的秒/帧(Core.ExperimentalEsrgan.SpeedText)只允许出现在
///      悬停提示与下拉下方提示里。实测依据见 Core.ExperimentalEsrgan 类注释的实测表:
///      40 帧 1080p 目录批跑 -j 1:1:1 -t 0 -f jpg 冷态,两支都测到 0.35 秒/帧 一档;
///   ⇒ ③ 蓝色小标文字常量仍是「测试」;【2026-09-21 起**下拉项不再挂药丸**】见下面第五次定稿那条;
///   ⇒ ④ ToolTip 用**事实陈述**,不用"实验性/测试版"这类定性词。
/// 【第三次定稿 · 用户 2026-09-15:名字改英文】下拉项文字要**跟已有各项同一个格式**
///   —— `类别 · 名字（括号内速度）`(已有项:`动漫 · animevideov3（快）` / `通用 · x4plus（超慢）`),
///   类别词保留中文(现实 / 游戏),名字换成英文 = 权重文件名后缀(real2x / game2x)。
/// 【第四次定稿 · 用户 2026-09-16:括号里写「快」不写时间】原话"新加的两个模型括号内要写快 而不是时间" ——
///   括号里放**速度档词**(与官方项同一套:`快`/`中`/`超慢`),秒/帧数字挪到悬停提示与下方提示。
/// 【第五次定稿 · 用户 2026-09-21:名字前面加 alh】原话"改名字 前面加上alh",写法示例由用户给出
///   (`现实 alhreal2x` 这样,alh 与名字**连写、不加连字符**)⇒ 逐字钉住
///   `现实 · alhreal2x（快）` / `游戏 · alhgame2x（快）` / `游戏 · alhgame2x-v2（快）` / `游戏 · alhgame2x-v3（快）`。
///   权重文件名与 Tag 仍是 `alhpro-*`(没改);**同时撤掉下拉项末尾的「测试」药丸** ——
///   名字变长后最长的 v2/v3 两行带药丸会到 214px、可用只有 197px(见下面那条 Theory 的注释)。
/// 这些要求在后续编辑里最容易丢(药丸删了、数字抹了、定性词又回来),而**编译/启动/跑视频都不会报错**,
/// 所以这里全部钉成断言。
///
/// 【R/B 缺陷 · 2026-09-15 **已修好**】旧导出 R/B 通道颠倒(源 R143/G110/B92 → 本模型 R94/G111/B137)、画面偏蓝,
/// 那是**修好之前**的事实。重训+重新导出后通道闸 PASS(现实向 Δ+0.20/+0.34/+0.24、游戏向 Δ+0.11/+0.12/+0.18,
/// 判据「每通道差 小于 5」)。界面文案里**不得再出现"偏蓝 / 需重新导出 / 不可用于正片"**这类过时结论 ——
/// 用户点名要求"换成修好后的真实结论",所以这里同时钉正向数字与反向禁令。</summary>
public class ExperimentalEsrganTests
{
    /// <summary>模型名 = XAML 的 Tag = engines\realesrgan\models\ 下的权重文件名;顺序 = VideoModelOrder 末尾几位。
    /// 【Rev7 · 2026-09-21】**下拉只留 3 支**:用户说"不要训练那么多模型了 我要留两个最好的游戏 1个现实就行" ⇒
    /// v1(`alhpro-game2x`)从 <see cref="ExperimentalEsrgan.All"/> 移除、登记进 <see cref="ExperimentalEsrgan.Retired"/>。</summary>
    [Fact]
    public void Registry_matches_the_appended_models()
    {
        Assert.Equal(new[] { "alhpro-real2x", "alhpro-game2x-v2", "alhpro-game2x-v3" }, ExperimentalEsrgan.All);
        Assert.Equal(ExperimentalEsrgan.Real2x, ExperimentalEsrgan.All[0]);
        Assert.Equal(ExperimentalEsrgan.Game2xV2, ExperimentalEsrgan.All[1]);
        Assert.Equal(ExperimentalEsrgan.Game2xV3, ExperimentalEsrgan.All[2]);
        // 退休的那支必须**仍然被认识**(权重目录选择 + 日志命名都靠 IsExperimental)——
        // 不认它的话 EsrganModelDir 会去根目录找权重,而引擎找不到权重时 exit=0、只出坏帧。
        Assert.Equal(new[] { "alhpro-game2x" }, ExperimentalEsrgan.Retired);
        Assert.True(ExperimentalEsrgan.IsExperimental(ExperimentalEsrgan.Game2x), "退休模型必须仍被 IsExperimental 认出来");
        Assert.DoesNotContain(ExperimentalEsrgan.Game2x, ExperimentalEsrgan.All);
        // 小标文字:用户 2026-09-15 说先保留「测试」(要不要改由用户定)⇒ 常量本身钉住现状。
        // 【2026-09-21 撤掉过一天又放回】名字加 `alh` 前缀后药丸一度放不下(收起态只剩约 18px、药丸要 36px),
        // 用户看到后问"测试标记呢"并选了"加宽左栏 22px"⇒ 左栏 300 → 322、药丸原样放回。
        // 这里钉常量值;药丸有没有挂在每个下拉项上、样式是否收小,由 VideoModelOrderTests 钉在 XAML 上。
        Assert.Equal("测试", ExperimentalEsrgan.Badge);
        Assert.Equal("alh", ExperimentalEsrgan.NamePrefix);
        foreach (var m in ExperimentalEsrgan.All)
            Assert.True(ExperimentalEsrgan.IsExperimental(m));
        // v2/v3 的 id 里**包含** v1 的 id(v3 也自成一串)⇒ 判定必须**从新到旧**:v3 → v2 → v1,
        // 否则名字会印成旧那支(三支看起来一模一样)。
        Assert.Equal("游戏 · alhgame2x-v3（快）", ExperimentalEsrgan.MenuText(ExperimentalEsrgan.Game2xV3));
        Assert.Equal("游戏 · alhgame2x-v2（快）", ExperimentalEsrgan.MenuText(ExperimentalEsrgan.Game2xV2));
        // 退休支仍要能印出正确的名字(老预设/老参数行里可能还带着它)
        Assert.Equal("游戏 · alhgame2x（快）", ExperimentalEsrgan.MenuText(ExperimentalEsrgan.Game2x));
    }

    /// <summary>**命名规则**:下拉项文字 = `类别 · 名字（括号里的实测速度）`,与已有各项(`动漫 · animevideov3（快）`)
    /// **逐字同构**:括号里与官方项一样只放**速度档词**(「快」)。
    /// 【2026-09-16 用户定稿】原话"新加的两个模型括号内要写快 而不是时间" —— 括号里不许出现秒/帧;
    /// 测出来的数字由悬停提示与下拉下方提示负责(见下面 ⑥ 那几条断言)。
    /// 【逐字钉住整串 · 为什么】这几串同时是**收起状态**显示的文字,左栏 ComboBox 内容区可用宽度只有约 197px
    /// (2026-09-21 UIA 实测:项宽 219、左右内边距各 11;行 = 蓝条 3 + 间距 6 + 文字 + 间距 6)。
    /// 2026-09-21 名字加 `alh` 前缀后最长的 v2/v3 两行文字实测 ≈169px ⇒ 行 184px ✓(余 13px);
    /// 若再挂回 30px 的「测试」药丸就是 214px ⇒ 超 17px 必被裁 ⇒ 药丸已撤。
    /// 文案一变长就会被裁,而**编译/单测/跑视频都不报错**,
    /// 只有人眼看界面才发现 ⇒ 这里把整串钉死,改长时先看见这条测试。</summary>
    [Theory]
    [InlineData("alhpro-real2x", "现实", "现实 · alhreal2x（快）")]
    [InlineData("alhpro-game2x", "游戏", "游戏 · alhgame2x（快）")]
    [InlineData("alhpro-game2x-v2", "游戏", "游戏 · alhgame2x-v2（快）")]
    public void Names_follow_the_same_format_as_the_official_items(string model, string category, string expectedMenu)
    {
        // ① 类别词:游戏 / 现实(用户定名)
        Assert.Equal(category, ExperimentalEsrgan.Category(model));
        // ①b 名字必须带项目前缀 alh(用户 2026-09-21:"改名字 前面加上alh"),与权重文件名 alhpro-* 同源
        Assert.StartsWith(ExperimentalEsrgan.NamePrefix, ExperimentalEsrgan.MenuName(model));
        // ② 分隔符与已有各项一致:前后各一个空格
        Assert.Equal(" · ", ExperimentalEsrgan.NameSeparator);
        Assert.Equal(category + " · " + ExperimentalEsrgan.MenuName(model), ExperimentalEsrgan.Label(model));
        // ③ 下拉项(及收起状态)显示的整串 —— 逐字
        Assert.Equal(expectedMenu, ExperimentalEsrgan.MenuText(model));
        // 括号里必须是**速度档词**(用户 2026-09-16:"括号内要写快 而不是时间")——
        // 不许再退回秒/帧那种时间写法(2026-09-15 那版就是这么写的,已被用户否掉)
        Assert.Contains("（" + ExperimentalEsrgan.SpeedTier + "）", ExperimentalEsrgan.MenuText(model));
        Assert.DoesNotContain("/帧", ExperimentalEsrgan.MenuText(model));
        Assert.DoesNotContain("秒", ExperimentalEsrgan.MenuText(model));
        // ④ 预设摘要(纯文本,半角括号):与摘要里 `通用·general-x4v3(快)` / `通用·x4plus(超慢)` 同构
        Assert.Equal(category + "·" + ExperimentalEsrgan.MenuName(model) + "(" + ExperimentalEsrgan.SpeedTier + ")", ExperimentalEsrgan.SummaryText(model));
        Assert.DoesNotContain("/帧", ExperimentalEsrgan.SummaryText(model));
        // ⑤ 日志行那个括号同理:`(类别 · 名字 · 快)`
        Assert.Equal("(" + category + " · " + ExperimentalEsrgan.MenuName(model) + " · " + ExperimentalEsrgan.SpeedTier + ")", ExperimentalEsrgan.LogSuffix(model));
        // ⑥ 实测数字仍在(只是换了地方放):悬停提示与下拉下方提示里必须有秒/帧
        Assert.Contains("0.35", ExperimentalEsrgan.SpeedText);
        Assert.Contains(ExperimentalEsrgan.SpeedText, ExperimentalEsrgan.ToolTip(model));
        Assert.Contains(ExperimentalEsrgan.SpeedText, ExperimentalEsrgan.Hint(model));
    }

    /// <summary>**格式对齐的守卫**:下拉项必须以 `类别 · ` 开头(与 `动漫 · animevideov3（快）` 等同构),
    /// 不许退回「游戏向（…）」这种没有类别的老写法 —— 用户这次的要求就是"仿照这个格式"。</summary>
    [Theory]
    [InlineData("alhpro-real2x")]
    [InlineData("alhpro-game2x")]
    [InlineData("alhpro-game2x-v2")]
    public void Menu_text_keeps_the_category_prefix(string model)
        => Assert.Matches(@"^(游戏|现实) · .+（.+）$", ExperimentalEsrgan.MenuText(model));

    /// <summary>**命名禁令**:任何用户可见字符串都不许出现"实验"。
    /// 【为什么单列一条】用户是看到界面上写着"实验"才提出纠正的;这类字样靠人眼复查守不住,交给断言。</summary>
    [Fact]
    public void No_user_visible_text_says_experimental()
    {
        foreach (var m in ExperimentalEsrgan.All)
        {
            foreach (var text in new[]
                     {
                         ExperimentalEsrgan.Label(m), ExperimentalEsrgan.MenuText(m),
                         ExperimentalEsrgan.SummaryText(m), ExperimentalEsrgan.ToolTip(m), ExperimentalEsrgan.Hint(m),
                         ExperimentalEsrgan.LogSuffix(m), ExperimentalEsrgan.OnnxFallbackNotice(m),
                     })
            {
                Assert.DoesNotContain("实验", text);
                Assert.DoesNotContain("实验性", text);
            }
        }
    }

    /// <summary>判定只认这两支;官方模型/空值/未知名字一律不算(否则会给官方模型挂上「测试」标)。</summary>
    [Theory]
    [InlineData("alhpro-real2x", true)]
    [InlineData("alhpro-game2x", true)]
    [InlineData("alhpro-game2x-v2", true)]
    [InlineData("realesr-animevideov3", false)]
    [InlineData("realesr-general-x4v3", false)]
    [InlineData("realesrgan-x4plus", false)]
    [InlineData("realesr-general-wdn-x4v3", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_the_two_self_trained_models_are_marked(string? model, bool expected)
    {
        Assert.Equal(expected, ExperimentalEsrgan.IsExperimental(model));
        Assert.Equal(expected, ExperimentalEsrgan.IsX2Only(model));          // 这两支正好也只有 2x 权重
        // 日志后缀 = `(类别 · 名字 · 实测速度)`,与下拉项同一套叫法
        Assert.Equal(expected ? "(" + ExperimentalEsrgan.Label(model) + " · " + ExperimentalEsrgan.SpeedTier + ")" : "",
            ExperimentalEsrgan.LogSuffix(model));
    }

    /// <summary>悬停提示:**四行以内 · 只写实测数字**(用户 2026-09-19:"提示那么多干啥 简单的 还有官方一点
    /// 不要什么本机什么什么的 什么你什么什么的")。
    /// 模板:① 身份(自训 2x + 训练数据规模)② 留出实测 + 三档色偏 ③ 三档 detail ④ 速度/体积/倍率。
    /// 这里钉:数字必须在、行数必须短、**不许出现"本机/你"这类口水词**、不许出现结论性夸张与"实验"。</summary>
    [Theory]
    [InlineData("alhpro-real2x", "色偏 游戏 0.34 / 动漫 0.18 / 实拍 1.48", "detail 3.55 / 2.14 / 63.44")]
    [InlineData("alhpro-game2x", "色偏 游戏 0.18 / 动漫 0.04 / 实拍 2.53", "detail 2.52 / 1.75 / 63.82")]
    [InlineData("alhpro-game2x-v2", "色偏 游戏 0.21 / 动漫 0.00 / 实拍 1.27", "detail 2.77 / 1.68 / 70.58")]
    public void Tooltip_is_short_and_states_the_measured_numbers(string model, string biasLine, string detailLine)
    {
        var tip = ExperimentalEsrgan.ToolTip(model);
        Assert.Contains("自训 2x 超分模型", tip);                       // 身份(这是哪一支)
        Assert.Contains("留出视频实测", tip);                          // 数字口径(不是"感觉更清晰")
        Assert.Contains(biasLine, tip);
        Assert.Contains("(判据 小于 5)", tip);
        Assert.Contains(detailLine, tip);
        Assert.Contains(ExperimentalEsrgan.SpeedText, tip);            // 1080p 约 0.35 秒/帧
        Assert.Contains(ExperimentalEsrgan.SizeText, tip);             // 2.4 MB
        Assert.Contains(ExperimentalEsrgan.ScaleText, tip);            // 原生 2x 那句话
        // 【用户点名】不许出现口水词/第二人称/结论性夸张;也不许出现"实验"
        foreach (var banned in new[] { "本机", "你", "实验", "最强", "最好", "更好", "无敌", "远超", "推荐用", "重新导出" })
            Assert.DoesNotContain(banned, tip);
        Assert.True(tip.Split('\n').Length <= 4, "悬停提示变长了(用户要求简单):" + tip);
    }

    /// <summary>**过时结论的禁令**(用户 2026-09-15 收尾:文案必须换成修好后的真实结论)。
    /// 旧文案写着"本机实测输出整幅偏蓝(R/B 通道颠倒),需重新导出后才可用于正片"——修好之后这句就是错的了。
    /// 【为什么必须钉】它读起来像一条"安全警告",看起来越像越没人敢删,而它已经与事实相反;
    /// 且这类句子在编译、启动、跑视频时都不报错,只能靠断言拦。</summary>
    [Fact]
    public void No_user_visible_text_keeps_the_outdated_blue_cast_claim()
    {
        foreach (var m in ExperimentalEsrgan.All)
        {
            if (m == ExperimentalEsrgan.Game2xV3) continue;   // 见下面的例外说明
            foreach (var text in new[]
                     {
                         ExperimentalEsrgan.ToolTip(m), ExperimentalEsrgan.Hint(m),
                         ExperimentalEsrgan.MenuText(m),
                     })
                foreach (var stale in new[] { "偏蓝", "需重新导出", "重新导出后才可用于正片", "不可用于正片" })
                    Assert.DoesNotContain(stale, text);
        }
        // ⚠ **v3 是有意的例外**:"偏蓝"这条禁令针对的是**过时结论**(旧导出那次 R/B 通道颠倒、需重新导出);
        //   而 v3 的提示里写着"实拍照片 ΔB 8.85 级偏蓝(判据 小于 5)"—— 那是它**当下真实的实测短板**,
        //   是必须告诉用户的信息,不是过时说法。所以这里排除 v3,但对 v1/v2/real2x 照旧全禁。
        Assert.DoesNotContain("需重新导出", ExperimentalEsrgan.ToolTip(ExperimentalEsrgan.Game2xV3));
        Assert.DoesNotContain("不可用于正片", ExperimentalEsrgan.ToolTip(ExperimentalEsrgan.Game2xV3));
        // 正向:必须给出"实测校验过"的依据,而不是一句空话(通道校验那句现在放在下拉下方提示里)
        Assert.Contains("通道校验", ExperimentalEsrgan.ChromaText);
        Assert.Contains("每通道", ExperimentalEsrgan.ChromaText);
        foreach (var m in ExperimentalEsrgan.All)
        {
            // ⚠ v3 是**有意的例外**:它的提示里如实写着"实拍照片 ΔB 8.85 级偏蓝(判据 小于 5)",
            //   所以不能套用 ChromaText 那句"每通道均值差 小于 2 级" —— 对它就是假话。
            //   其余模型(v1/v2/real2x)照旧必须带这句。⚠ 同时注意:v3 的提示里**允许**出现"偏蓝"二字,
            //   因为那是它自己的实测短板(上面那条禁令查的是"过时结论:旧导出 R/B 颠倒"那种说法),
            //   所以这里把 v3 从"逐模型必须含 ChromaText"里排除,但**不**把它从禁令里排除。
            if (m == ExperimentalEsrgan.Game2xV3) continue;
            Assert.Contains(ExperimentalEsrgan.ChromaText, ExperimentalEsrgan.Hint(m));
        }
    }

    /// <summary>**倍率那句话**要说清"原生 2x;3x/4x 由 2x 成片放大"(用户 2026-09-15 追问过"2x 是什么")。
    /// 【2026-09-19 用户要求"提示简单一点"】原来那段(跑两遍/实测秒数/官方对照)已压成一句,断言同步收窄。</summary>
    [Fact]
    public void Tooltip_and_hint_explain_the_scale_mechanism()
    {
        foreach (var m in ExperimentalEsrgan.All)
        {
            Assert.Contains(ExperimentalEsrgan.ScaleText, ExperimentalEsrgan.ToolTip(m));
            Assert.Contains("原生 2x", ExperimentalEsrgan.Hint(m));   // 那行是一句话,只说关键结论
        }
        var scale = ExperimentalEsrgan.ScaleText;
        Assert.Contains("原生 2x", scale);
        Assert.Contains("3x/4x", scale);
        Assert.DoesNotContain("秒", scale);            // 提示要短:秒/帧那种数字只留在速度那一处
    }

    /// <summary>下拉正下方那行可见提示(不悬停也看得见):三档色偏 / 三档 detail / 速度 / 倍率 / 通道校验,一句话。</summary>
    [Theory]
    [InlineData("alhpro-real2x", "色偏 0.34 / 0.18 / 1.48", "detail 3.55 / 2.14 / 63.44")]
    [InlineData("alhpro-game2x", "色偏 0.18 / 0.04 / 2.53", "detail 2.52 / 1.75 / 63.82")]
    [InlineData("alhpro-game2x-v2", "色偏 0.21 / 0.00 / 1.27", "detail 2.77 / 1.68 / 70.58")]
    public void Hint_carries_the_same_numbers(string model, string biasLine, string detailLine)
    {
        var hint = ExperimentalEsrgan.Hint(model);
        Assert.Contains(biasLine, hint);
        Assert.Contains(detailLine, hint);
        Assert.Contains(ExperimentalEsrgan.SpeedText, hint);
        Assert.Contains(ExperimentalEsrgan.ChromaText, hint);                // 与悬停提示用同一句,免得两处说法漂移
        Assert.DoesNotContain("实验", hint);
        foreach (var banned in new[] { "本机", "你" })
            Assert.DoesNotContain(banned, hint);
        // 提示里那行数字必须与下拉项里的名字用同一套叫法(免得"提示说一支、下拉写另一支")
        Assert.StartsWith(ExperimentalEsrgan.Label(model) + ":", hint);
        Assert.True(hint.Length <= 200, "下拉下方那行太长了(用户要求简单):" + hint);
    }

    /// <summary>命名对调也要能分辨:给游戏向的提示不能印成现实向那支(文案分开写,最容易抄错)。
    /// 【注】不再用 DoesNotContain("游戏") 判现实向 —— 现实向那支**游戏帧**的实测数字里本来就带"游戏"二字
    /// (0.34 / 3.20 就是游戏帧测出来的);用"逐串钉死各自的数字"比"禁一个字"更能抓住真正的抄错。</summary>
    [Fact]
    public void Real_and_game_texts_are_not_swapped()
    {
        var real = ExperimentalEsrgan.ToolTip(ExperimentalEsrgan.Real2x);
        var game = ExperimentalEsrgan.ToolTip(ExperimentalEsrgan.Game2x);

        Assert.Contains("实拍", real);
        Assert.Contains("色偏 游戏 0.34 / 动漫 0.18 / 实拍 1.48", real);   // 现实向那一套数字
        Assert.DoesNotContain("2.53", real);           // 那是游戏向的实拍色偏
        Assert.StartsWith("现实 · alhreal2x:", real);

        Assert.Contains("游戏", game);
        Assert.Contains("色偏 游戏 0.18 / 动漫 0.04 / 实拍 2.53", game);   // 游戏向那一套数字
        Assert.DoesNotContain("1.48", game);
        Assert.StartsWith("游戏 · alhgame2x:", game);

        // 训练数据规模也必须各自独立(现实向=照片与人像;游戏向=1 段录像 138 帧),抄错在这里爆
        Assert.Contains("实拍照片与人像", real);
        Assert.Contains("1 段游戏录像 · 138 帧", game);
    }

    /// <summary>走 ONNX「稳定引擎」时的如实告知:必须点名用户选的模型(名字+权重名)、并说清"这批是别的模型在处理"。
    /// 【为什么必须】这两支没有 ONNX 权重,ONNX 通道里只能换成官方模型 —— 静默换模型是本仓库明令禁止的。</summary>
    [Fact]
    public void Onnx_fallback_notice_names_the_model_and_says_it_is_substituted()
    {
        var notice = ExperimentalEsrgan.OnnxFallbackNotice(ExperimentalEsrgan.Real2x);
        Assert.Contains("现实 · alhreal2x", notice);
        Assert.Contains("alhpro-real2x", notice);
        Assert.Contains("另一支", notice);
        Assert.Contains("色偏", notice);
    }

    /// <summary>**用户 2026-09-16 定稿:「新加的两个模型括号内要写快 而不是时间」**。
    /// 所有「模型名 + 括号」的出口(下拉项、预设摘要、日志行)括号里只许是**速度档词**;
    /// 秒/帧那种**时间**写法只允许留在悬停提示与下拉下方提示(两处都是陈述句,不是名字的一部分)。
    /// 【为什么钉】这条改的是"括号里放什么"的口径,而括号内容在编译、启动、跑视频时都不报错;
    /// 2026-09-15 那版写的就是 `（0.35s/帧）`,被用户当场否掉 —— 只能靠断言守住。
    /// 【数字没丢】用户 2026-09-15 要过"如实说明数字",所以这里同时钉住数字还在悬停与下方提示里。</summary>
    [Fact]
    public void Parentheses_show_the_speed_tier_never_a_time()
    {
        Assert.Equal("快", ExperimentalEsrgan.SpeedTier);
        foreach (var m in ExperimentalEsrgan.All)
        {
            foreach (var bracketed in new[]
                     {
                         ExperimentalEsrgan.MenuText(m), ExperimentalEsrgan.SummaryText(m),
                         ExperimentalEsrgan.LogSuffix(m),
                     })
            {
                Assert.Contains(ExperimentalEsrgan.SpeedTier, bracketed);
                Assert.DoesNotContain("/帧", bracketed);
                Assert.DoesNotContain("秒", bracketed);
            }
            Assert.Contains(ExperimentalEsrgan.SpeedText, ExperimentalEsrgan.ToolTip(m));
            Assert.Contains(ExperimentalEsrgan.SpeedText, ExperimentalEsrgan.Hint(m));
            Assert.Contains("0.35", ExperimentalEsrgan.SpeedText);
        }
    }
}
