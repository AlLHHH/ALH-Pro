using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>两支**自训**模型(游戏 · game2x / 现实 · real2x)的登记、命名与文案契约(Rev4)。
///
/// 【命名规则 · 用户 2026-09-15 原话】"不要写实验 而且括号内不应该写的是速度吗 前面的实验写成游戏 现实"
///   ⇒ ① 界面文字只许叫「游戏」「现实」,**任何用户可见字符串里不得出现"实验"**;
///   ⇒ ② 括号里写**速度**(不抄官方模型的数字) —— 该口径由用户 2026-09-16 收窄为**速度档「快」**
///      (Core.ExperimentalEsrgan.SpeedTier);测出来的秒/帧(Core.ExperimentalEsrgan.SpeedText)只允许出现在
///      悬停提示与下拉下方提示里。实测依据见 Core.ExperimentalEsrgan 类注释的实测表:
///      40 帧 1080p 目录批跑 -j 1:1:1 -t 0 -f jpg 冷态,两支都测到 0.35 秒/帧 一档;
///   ⇒ ③ 蓝色小标文字仍是「测试」(用户:先保留,是否改由用户定);
///   ⇒ ④ ToolTip 用**事实陈述**,不用"实验性/测试版"这类定性词。
/// 【第三次定稿 · 用户 2026-09-15:名字改英文】下拉项文字要**跟已有各项同一个格式**
///   —— `类别 · 名字（括号内速度）`(已有项:`动漫 · animevideov3（快）` / `通用 · x4plus（超慢）`),
///   类别词保留中文(现实 / 游戏),名字换成英文 = 权重文件名后缀(real2x / game2x)。
/// 【第四次定稿 · 用户 2026-09-16:括号里写「快」不写时间】原话"新加的两个模型括号内要写快 而不是时间" ——
///   括号里放**速度档词**(与官方项同一套:`快`/`中`/`超慢`),秒/帧数字挪到悬停提示与下方提示,
///   所以逐字钉住 `现实 · real2x（快）` / `游戏 · game2x（快）`。
/// 这些要求在后续编辑里最容易丢(药丸删了、数字抹了、定性词又回来),而**编译/启动/跑视频都不会报错**,
/// 所以这里全部钉成断言。
///
/// 【R/B 缺陷 · 2026-09-15 **已修好**】旧导出 R/B 通道颠倒(源 R143/G110/B92 → 本模型 R94/G111/B137)、画面偏蓝,
/// 那是**修好之前**的事实。重训+重新导出后通道闸 PASS(现实向 Δ+0.20/+0.34/+0.24、游戏向 Δ+0.11/+0.12/+0.18,
/// 判据「每通道差 小于 5」)。界面文案里**不得再出现"偏蓝 / 需重新导出 / 不可用于正片"**这类过时结论 ——
/// 用户点名要求"换成修好后的真实结论",所以这里同时钉正向数字与反向禁令。</summary>
public class ExperimentalEsrganTests
{
    /// <summary>模型名 = XAML 的 Tag = engines\realesrgan\models\ 下的权重文件名;顺序 = VideoModelOrder Rev4 的末两位。</summary>
    [Fact]
    public void Registry_matches_the_two_appended_models()
    {
        Assert.Equal(new[] { "alhpro-real2x", "alhpro-game2x" }, ExperimentalEsrgan.All);
        Assert.Equal(ExperimentalEsrgan.Real2x, ExperimentalEsrgan.All[0]);
        Assert.Equal(ExperimentalEsrgan.Game2x, ExperimentalEsrgan.All[1]);
        // 小标文字:用户 2026-09-15 说先保留「测试」(要不要改由用户定)⇒ 这里钉住现状,改动时会显式看到
        Assert.Equal("测试", ExperimentalEsrgan.Badge);
        Assert.True(ExperimentalEsrgan.IsExperimental(ExperimentalEsrgan.Real2x));
        Assert.True(ExperimentalEsrgan.IsExperimental(ExperimentalEsrgan.Game2x));
    }

    /// <summary>**命名规则**:下拉项文字 = `类别 · 名字（括号里的实测速度）`,与已有各项(`动漫 · animevideov3（快）`)
    /// **逐字同构**:括号里与官方项一样只放**速度档词**(「快」)。
    /// 【2026-09-16 用户定稿】原话"新加的两个模型括号内要写快 而不是时间" —— 括号里不许出现秒/帧;
    /// 测出来的数字由悬停提示与下拉下方提示负责(见下面 ⑥ 那几条断言)。
    /// 【逐字钉住整串 · 为什么】这两串同时是**收起状态**显示的文字,左栏 ComboBox 内容区可用宽度约 212px
    /// (实测:`（快）` 这版比 2026-09-15 的 `（0.35s/帧）`(192 / 205px)、更早的中文名版(211px,压线)都短)。
    /// 文案一变长就会被裁,而**编译/单测/跑视频都不报错**,
    /// 只有人眼看界面才发现 ⇒ 这里把整串钉死,改长时先看见这条测试。</summary>
    [Theory]
    [InlineData("alhpro-real2x", "现实", "现实 · real2x（快）")]
    [InlineData("alhpro-game2x", "游戏", "游戏 · game2x（快）")]
    public void Names_follow_the_same_format_as_the_official_items(string model, string category, string expectedMenu)
    {
        // ① 类别词:游戏 / 现实(用户定名)
        Assert.Equal(category, ExperimentalEsrgan.Category(model));
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

    /// <summary>悬停提示:按用户给的口径写**事实**(三档素材的色偏 vs 官方、detail vs 官方、适合什么场景),
    /// 外加**修好后**的通道验收数字与实测速度;禁用词一律不许出现。
    /// 【为什么三档都要】只挑对自己有利的那一格摆出来就是夸大;实测里现实向在游戏帧上的 detail 其实略高于官方,
    /// 这条也必须在文案里(见 101.x 那一段),所以断言按"逐档逐数"钉。
    /// 【口径】色偏/ detail 来自 `_train\chroma_test_card.py`(修好后的导出,源 1920×1080,
    /// 色偏 = 与源图最近邻 2x 的逐通道均值最大差;detail = 平均 |拉普拉斯|)。</summary>
    [Theory]
    [InlineData("alhpro-real2x",
        "实拍 1.48(官方 6.34)", "动漫帧 0.18(官方 2.73)", "游戏帧 0.34(官方 2.00)",
        "59.13 对 73.66", "1.83 对 2.06", "3.20 略高于官方 2.91",
        "R+0.20/G+0.34/B+0.24")]
    [InlineData("alhpro-game2x",
        "实拍 2.53(官方 6.34", "动漫帧 0.04(官方 2.73)", "游戏帧 0.18(官方 2.00)",
        "59.97 对 73.66", "1.55 对 2.06", "2.36 对 2.91",
        "R+0.11/G+0.12/B+0.18")]
    public void Tooltip_states_facts_with_numbers(string model,
        string photoBias, string animeBias, string gameBias,
        string photoDetail, string animeDetail, string gameDetail,
        string channelDelta)
    {
        var tip = ExperimentalEsrgan.ToolTip(model);
        Assert.Contains("色偏", tip);
        Assert.Contains("低于官方", tip);                                   // 结论限定在实测测过的那件事上
        Assert.Contains(photoBias, tip);                                    // 实拍那一档
        Assert.Contains(animeBias, tip);                                    // 动漫帧那一档
        Assert.Contains(gameBias, tip);                                     // 游戏帧那一档
        Assert.Contains(photoDetail, tip);                                  // 细节也要给(只写色偏等于只说一半)
        Assert.Contains(animeDetail, tip);
        Assert.Contains(gameDetail, tip);
        Assert.Contains("适合优先要颜色准确", tip);                            // 用户给的"适合什么场景"那句话
        Assert.Contains(channelDelta, tip);                                 // **修好后**的通道验收数字(不是旧的 R/B 颠倒)
        Assert.Contains("小于 5", tip);                                      // 验收判据
        Assert.Contains(ExperimentalEsrgan.SpeedText, tip);                  // 实测速度
        foreach (var banned in new[] { "最强", "最好", "更好", "无敌", "远超", "实验" })
            Assert.DoesNotContain(banned, tip);
    }

    /// <summary>**过时结论的禁令**(用户 2026-09-15 收尾:文案必须换成修好后的真实结论)。
    /// 旧文案写着"本机实测输出整幅偏蓝(R/B 通道颠倒),需重新导出后才可用于正片"——修好之后这句就是错的了。
    /// 【为什么必须钉】它读起来像一条"安全警告",看起来越像越没人敢删,而它已经与事实相反;
    /// 且这类句子在编译、启动、跑视频时都不报错,只能靠断言拦。</summary>
    [Fact]
    public void No_user_visible_text_keeps_the_outdated_blue_cast_claim()
    {
        foreach (var m in ExperimentalEsrgan.All)
            foreach (var text in new[]
                     {
                         ExperimentalEsrgan.ToolTip(m), ExperimentalEsrgan.Hint(m),
                         ExperimentalEsrgan.MenuText(m),
                     })
                foreach (var stale in new[] { "偏蓝", "需重新导出", "重新导出后才可用于正片", "不可用于正片" })
                    Assert.DoesNotContain(stale, text);
        // 正向:必须给出"已修"的实测依据,而不是一句空话
        Assert.Contains("通道验收", ExperimentalEsrgan.ToolTip(ExperimentalEsrgan.Real2x));
        Assert.Contains("已修", ExperimentalEsrgan.ChromaText);
    }

    /// <summary>**倍率那段话必须是实测口径**(用户 2026-09-15 追问"2x 是什么、只支持 2x 输出吗"):
    /// 说清"网络原生 2x / 3x4x 靠 2x 跑一遍再缩放(不是跑两遍)/ 实测耗时 / 推荐用 2x"。
    /// 【为什么钉住】这类"机制说明"最容易写成想当然的错话(比如"引擎会跑两遍"—— 实测不是),而没人会去复核。</summary>
    [Fact]
    public void Tooltip_and_hint_explain_the_scale_mechanism_from_measurement()
    {
        // 两支都要有,且逐字用同一个常量(否则两支说法会漂移)
        foreach (var m in ExperimentalEsrgan.All)
        {
            Assert.Contains(ExperimentalEsrgan.ScaleText, ExperimentalEsrgan.ToolTip(m));
            Assert.Contains("推荐用 2x", ExperimentalEsrgan.ToolTip(m));
            Assert.Contains("推荐用 2x", ExperimentalEsrgan.Hint(m));
        }
        var scale = ExperimentalEsrgan.ScaleText;
        Assert.Contains("原生 2x", scale);
        Assert.Contains("不是把引擎跑两遍", scale);       // 实测:一遍 + 缩放,不是两遍
        Assert.Contains("2x=2.0s、3x=3.9s、4x=4.7s", scale);   // 24 帧素材实测耗时
        Assert.Contains("2.2/2.3/2.8s", scale);           // 官方有原生 3x/4x 权重作对照
        Assert.Contains("推荐用 2x", scale);
        Assert.DoesNotContain("跑两遍(", scale);          // 不许把它写成"两遍"那种错描述
    }

    /// <summary>下拉正下方那行可见提示(不悬停也看得见):三档色偏/detail 数字 + 速度 + 倍率建议 + 通道验收。</summary>
    [Theory]
    [InlineData("alhpro-real2x", "实拍 1.48 / 动漫帧 0.18 / 游戏帧 0.34(官方 6.34 / 2.73 / 2.00)", "59.13 / 1.83 / 3.20(官方 73.66 / 2.06 / 2.91)")]
    [InlineData("alhpro-game2x", "游戏帧 0.18 / 动漫帧 0.04 / 实拍 2.53(官方 2.00 / 2.73 / 6.34)", "2.36 / 1.55 / 59.97(官方 2.91 / 2.06 / 73.66)")]
    public void Hint_carries_the_same_numbers(string model, string biasLine, string detailLine)
    {
        var hint = ExperimentalEsrgan.Hint(model);
        Assert.Contains(biasLine, hint);
        Assert.Contains(detailLine, hint);
        Assert.Contains(ExperimentalEsrgan.SpeedText, hint);
        Assert.Contains("R/B", hint);                                        // 旧缺陷已修这件事仍要交代清楚
        Assert.Contains(ExperimentalEsrgan.ChromaText, hint);                // 与悬停提示用同一句,免得两处说法漂移
        Assert.DoesNotContain("实验", hint);
        // 提示里那行数字必须与下拉项里的名字用同一套叫法(免得"提示说一支、下拉写另一支")
        Assert.StartsWith(ExperimentalEsrgan.Label(model) + ":", hint);
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
        Assert.Contains("1.48", real);                 // 现实向的实拍色偏
        Assert.DoesNotContain("2.53", real);           // 那是游戏向的实拍色偏
        Assert.StartsWith("现实 · real2x:", real);

        Assert.Contains("游戏", game);
        Assert.Contains("2.53", game);                 // 游戏向的实拍色偏(它三档里最大的那一档)
        Assert.DoesNotContain("1.48", game);
        Assert.StartsWith("游戏 · game2x:", game);

        // 两支的通道验收数字各自独立(现实向 +0.20/+0.34/+0.24、游戏向 +0.11/+0.12/+0.18),抄错也在这里爆
        Assert.Contains("R+0.20/G+0.34/B+0.24", real);
        Assert.DoesNotContain("R+0.11/G+0.12/B+0.18", real);
        Assert.Contains("R+0.11/G+0.12/B+0.18", game);
        Assert.DoesNotContain("R+0.20/G+0.34/B+0.24", game);
    }

    /// <summary>走 ONNX「稳定引擎」时的如实告知:必须点名用户选的模型(名字+权重名)、并说清"这批是别的模型在处理"。
    /// 【为什么必须】这两支没有 ONNX 权重,ONNX 通道里只能换成官方模型 —— 静默换模型是本仓库明令禁止的。</summary>
    [Fact]
    public void Onnx_fallback_notice_names_the_model_and_says_it_is_substituted()
    {
        var notice = ExperimentalEsrgan.OnnxFallbackNotice(ExperimentalEsrgan.Real2x);
        Assert.Contains("现实 · real2x", notice);
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
