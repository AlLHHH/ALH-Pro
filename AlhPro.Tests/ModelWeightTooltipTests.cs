using AlhPro.Core;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【用户要求 2026-09-25】「**在每一个模型的悬停提示里面最后加一个绿字提醒「权重 2x」这样子**」。
///
/// 本文件钉三件事:
///   ① 视频页(`Views/VideoView.xaml`)**每一个超分模型项**的悬停提示**末尾**都有一行绿字,内容以「权重」开头
///      (Anime4K 那项写「着色器(无权重)」),且**与 Core 的 <see cref="EngineScalePolicy.NativeWeightLabel"/>
///      逐字一致**(手上写死的那两项单独钉);
///   ② 绿字色值就是 `#7BD88F`(全文件只许出现这一种拼法),且**只出现在这 13 个超分模型项上** ——
///      补帧(RIFE)等其它提示一行都不加(本轮只做超分模型);
///   ③ 图片页 `Views/UpscaleView.xaml.cs` 的 `PopulateModelCombo` **每一项**都设了悬停提示(此前一项都没有),
///      基础文字只用模型**已有**字段(Label),末尾同样接 Core 算出来的权重行。
/// 另外:原文不许被改写/丢失 —— 每一项的提示必须仍以 `<ToolTip><TextBlock TextWrapping="Wrap" MaxWidth="440">`
/// 承载(纯文本 tooltip 上不了色),且 `Content` / `Tag` 一个都没动(模型仍按索引读写、存档兼容)。</summary>
public class ModelWeightTooltipTests
{
    private const string Green = "Foreground=\"#7BD88F\"";

    /// <summary>视频页里**超分模型**各项:(Tag, 原有 Content 属性值(空=该项是富文本 StackPanel), engine, model)。
    /// `engine`/`model` 用来从 Core 反算期望的绿字 —— 界面文字与判据必须同源。</summary>
    private static readonly (string Tag, string Content, string Engine, string Model)[] Items =
    {
        // 【2026-09-25】waifu2x 的 3 个模型项已随引擎项一起从视频页删除(用户裁定「直接删掉在视频页面」)
        // ⇒ 这里不再列它们;视频页现在共 **13** 个超分模型项(Real-ESRGAN 8 + 1x 栏 2 + Real-CUGAN 3)。
        // Real-ESRGAN 组合(官方 5 项 + 自训 3 项;富文本项没有 Content 属性)
        ("realesr-animevideov3", "动漫 · animevideov3（快）", "realesrgan", "realesr-animevideov3"),
        ("realesr-general-x4v3", "通用 · general-x4v3（快）", "realesrgan", "realesr-general-x4v3"),
        ("realesr-general-wdn-x4v3", "通用 · wdn-x4v3（快）", "realesrgan", "realesr-general-wdn-x4v3"),
        ("realesrgan-x4plus-anime", "动漫 · x4plus-anime（中）", "realesrgan", "realesrgan-x4plus-anime"),
        ("realesrgan-x4plus", "", "realesrgan", "realesrgan-x4plus"),
        ("alhpro-real2x", "", "realesrgan", "alhpro-real2x"),
        ("alhpro-game2x-v2", "", "realesrgan", "alhpro-game2x-v2"),
        ("alhpro-game2x-v3", "", "realesrgan", "alhpro-game2x-v3"),
        // 1x 一栏的两项:Anime4K 走着色器(没有权重文件)、1x 修复是 2x 跑再缩回 —— 都写死(不蹭 Core 判据)
        ("anime4k", "", "@handwritten", "着色器(无权重)"),
        ("alhpro-real1x", "", "@handwritten", "权重 2x(缩回 1x)"),
        // Real-CUGAN 组合(3 项)
        ("models-se:-1", "动漫 · models-se -1（中）", "realcugan", "models-se:-1"),
        ("models-se:0", "动漫 · models-se 0（快）", "realcugan", "models-se:0"),
        ("models-se:3", "动漫 · models-se 3（慢）", "realcugan", "models-se:3"),
    };

    private static string Expect(string engine, string model)
        => engine == "@handwritten" ? model : EngineScalePolicy.NativeWeightLabel(engine, model);

    // ───────────────────────── ① 视频页:每个超分模型项的提示末尾都有绿字 ─────────────────────────

    [Fact]
    public void Every_super_resolution_model_item_ends_with_a_green_weight_line()
    {
        string xaml = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml");
        foreach (var (tag, content, engine, model) in Items)
        {
            string needle = "Tag=\"" + tag + "\"";
            int tagPos = xaml.IndexOf(needle, StringComparison.Ordinal);
            Assert.True(tagPos > 0, $"视频页找不到超分模型项:{tag}");
            int itemStart = xaml.LastIndexOf("<ComboBoxItem", tagPos, StringComparison.Ordinal);
            Assert.True(itemStart > 0, $"{tag}: 定位不到 ComboBoxItem");

            int open = xaml.IndexOf("<ToolTipService.ToolTip>", itemStart, StringComparison.Ordinal);
            int close = xaml.IndexOf("</ToolTipService.ToolTip>", open + 1, StringComparison.Ordinal);
            Assert.True(open > 0 && close > open, $"{tag}: 悬停提示不是属性元素形态(纯文本 tooltip 上不了色)");
            string tip = xaml[open..(close + "</ToolTipService.ToolTip>".Length)];

            // 承载形式:ToolTip + TextBlock(与合同给的写法一致)
            Assert.Contains("<ToolTip><TextBlock TextWrapping=\"Wrap\" MaxWidth=\"440\">", tip);
            // 末尾就是那行绿字(不多不少:Run 结束 + TextBlock/ToolTip/属性元素三个闭合标签)
            string weight = Expect(engine, model);
            Assert.True(weight.Length > 0, $"{tag}: Core 与手写映射都没给出权重文字");
            string suffix = $"<Run {Green}>&#x0a;{weight}</Run></TextBlock></ToolTip></ToolTipService.ToolTip>";
            Assert.EndsWith(suffix, tip);
            // 原文没有丢:绿字行之前必须还有内容(至少 20 个字符)
            int runAt = tip.IndexOf("<Run ", StringComparison.Ordinal);
            Assert.True(runAt > "<ToolTip><TextBlock TextWrapping=\"Wrap\" MaxWidth=\"440\">".Length + 20,
                $"{tag}: 原有说明文字被清空了?");

            // Content / Tag 一个都没动(富文本那几项的 Content 是 StackPanel,故只在有 Content 属性时校验)
            if (content.Length > 0) Assert.Contains($"Content=\"{content}\"", xaml);
            Assert.Contains(needle, xaml);
        }
    }

    /// <summary>绿字色值唯一、且**只**出现在这 13 个超分模型项上(补帧等其它提示一行都没有)。</summary>
    [Fact]
    public void The_weight_line_uses_the_project_green_and_only_super_resolution_items_have_one()
    {
        string xaml = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml");
        int green = xaml.Split(Green).Length - 1;
        Assert.Equal(Items.Length, green);                       // 13 项,一项不多一项不少
        Assert.Equal(green, xaml.Split("#7BD88F").Length - 1);    // 色值只许这一种写法(没有 #7bd88f / #7BD88F 混写)
        Assert.DoesNotContain("Foreground=\"#7bd88f\"", xaml);
        Assert.DoesNotContain("Foreground=\"#7BD88E\"", xaml);   // 防手误改色值
    }

    /// <summary>补帧(RIFE)模型的提示**不加**权重行 —— 本轮只做超分模型(non-goal)。
    /// 【怎么钉】那三项没有 Tag,按 Content 定位它们的提示属性,断言里面没有绿字、也没有「权重」二字。</summary>
    [Theory]
    [InlineData("通用画质最新 (RIFE v4.13)")]
    [InlineData("通用画质 (RIFE v4.6)")]
    [InlineData("通用画质再新 (RIFE v4.26)")]
    public void Interp_model_items_have_no_weight_line(string content)
    {
        string xaml = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml");
        int at = xaml.IndexOf($"Content=\"{content}\"", StringComparison.Ordinal);
        Assert.True(at > 0, $"找不到补帧模型项:{content}");
        int tipPos = xaml.IndexOf("ToolTipService.ToolTip=", at, StringComparison.Ordinal);
        Assert.True(tipPos > 0, $"{content}: 这一项没有悬停提示?");
        int endQuote = xaml.IndexOf("\"/>", tipPos, StringComparison.Ordinal);
        string tip = xaml[tipPos..endQuote];
        // 【注意】不能用「权重」二字判:补帧提示里本来就有"权重与代码同为 MIT"这种合法用法 ⇒
        // 只判"没有绿字行"(而全文件绿字行总数 = 13 的断言已经保证了没有多出来的一行)。
        Assert.DoesNotContain("#7BD88F", tip);
        Assert.DoesNotContain("<Run ", tip);
    }

    /// <summary>视频页里的绿字**必须与 Core 同源**:14 项取 `NativeWeightLabel` 的值,2 项(Anime4K / 1x 修复)
    /// 写死但取值也被本文件的表钉住。这条挡的是"界面写 4x、判据按 2x 跑"那类自相矛盾。</summary>
    [Fact]
    public void Video_page_weight_text_matches_the_core_function()
    {
        string xaml = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml");
        int coreCovered = 0;
        foreach (var (tag, _, engine, model) in Items)
        {
            if (engine == "@handwritten") continue;
            string expected = EngineScalePolicy.NativeWeightLabel(engine, model);
            int tagPos = xaml.IndexOf("Tag=\"" + tag + "\"", StringComparison.Ordinal);
            int runAt = xaml.IndexOf("<Run Foreground=\"#7BD88F\">", tagPos, StringComparison.Ordinal);
            int gt = xaml.IndexOf('>', runAt);
            int lt = xaml.IndexOf("</Run>", gt, StringComparison.Ordinal);
            string actual = xaml[(gt + 1)..lt];
            Assert.Equal("&#x0a;" + expected, actual);
            coreCovered++;
        }
        Assert.Equal(11, coreCovered);   // 13 项里 11 项走 Core 判据、2 项写死
    }

    // ───────────────────────── ③ 图片页:每一项都挂提示(此前一项都没有) ─────────────────────────

    [Fact]
    public void Image_page_sets_a_tooltip_on_every_model_item_from_the_same_core_function()
    {
        string src = ReadRepoFile("ImgUpscalerUI", "Views", "UpscaleView.xaml.cs");
        // 装配处:动漫模式与照片模式两个循环都调用同一个装配函数
        Assert.Contains("SetModelItemToolTip(item, m.Label, m.Engine, m.Model);", src);
        Assert.Contains("SetModelItemToolTip(item, m.Label, \"realesrgan\", m.Name);", src);
        // 装配函数:真的设了工具提示;基础文字只用已有的 Label;绿字来自 Core
        Assert.Contains("ToolTipService.SetToolTip(item, tb);", src);
        Assert.Contains("AlhPro.Core.EngineScalePolicy.NativeWeightLabel(engine, model)", src);
        Assert.Contains("Text = \"\\n\" + weight", src);
        Assert.Contains("0x7B, 0xD8, 0x8F", src);              // #7BD88F,与视频页同一个绿
        Assert.Contains("Text = label", src);                  // 基础文字 = 既有字段,不编造数字
        // 只设提示:不动 Content(模型仍按索引读写,图片预设存的是序号)
        Assert.Contains("Content = m.Label", src);
        Assert.DoesNotContain("Content = m.Label + ", src);
    }

    /// <summary>图片页两个模型表(`EngineService.AnimeModels` / `PhotoModels`)的**每一项**都能算出权重行 ——
    /// 否则那一项就不会有绿字,而要求是"每一个模型"。**表内容直接从源码解析**(不是在这里另抄一份清单):
    /// 以后谁往表里加一支新模型,只要 Core 的判据认不出它,这条就红。</summary>
    [Fact]
    public void Every_image_page_model_can_produce_a_weight_line()
    {
        string src = ReadRepoFile("ImgUpscalerUI", "EngineService.cs");
        // 动漫模式:("Label", "waifu2x", "Model")
        int animeAt = src.IndexOf("AnimeModels =", StringComparison.Ordinal);
        int animeEnd = src.IndexOf("};", animeAt, StringComparison.Ordinal);
        Assert.True(animeAt > 0 && animeEnd > animeAt);
        var animeRows = System.Text.RegularExpressions.Regex.Matches(
            src[animeAt..animeEnd], "\\(\"([^\"]+)\",\\s*\"([^\"]+)\",\\s*\"([^\"]+)\"\\)");
        Assert.True(animeRows.Count >= 3, "AnimeModels 解析失败");
        foreach (System.Text.RegularExpressions.Match m in animeRows)
            Assert.Equal("权重 2x", EngineScalePolicy.NativeWeightLabel(m.Groups[2].Value, m.Groups[3].Value));

        // 照片模式:("Label", "Name") —— 引擎固定是 realesrgan
        int photoAt = src.IndexOf("PhotoModels =", StringComparison.Ordinal);
        int photoEnd = src.IndexOf("};", photoAt, StringComparison.Ordinal);
        Assert.True(photoAt > 0 && photoEnd > photoAt);
        var photoRows = System.Text.RegularExpressions.Regex.Matches(
            src[photoAt..photoEnd], "\\(\"([^\"]+)\",\\s*\"([^\"]+)\"\\)");
        Assert.True(photoRows.Count >= 4, "PhotoModels 解析失败");
        foreach (System.Text.RegularExpressions.Match m in photoRows)
            Assert.NotEqual("", EngineScalePolicy.NativeWeightLabel("realesrgan", m.Groups[2].Value));
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
