using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace AlhPro.Tests;

/// <summary>补帧下拉精简(2026-09-16 用户裁决:「补帧只留 13,再留一个兼容性强的,其他全部删除」)
/// 与「超分模型悬停提示」这两件事的**接线契约**。
/// 【2026-09-24 更新】用户又裁决把 `RIFE v4.26` 作为第 3 支加回来(实测 1080p 79~92 ms/输出帧,
/// 权重随包)⇒ 本文件的口径由「恰好两支」改为「恰好三支」,`RetiredInterpItems` 相应由 5 支减为 4 支。
///
/// 【为什么必须单测】下拉的**序号就是存进设置的取值口径**(AppSettings.Model = 下拉 SelectedIndex),
/// 而「序号 → 引擎模型目录名」的映射写在 VideoView.xaml.cs 的 SelectedInterpModel 里。
/// 删项时只要漏改映射,编译、启动、跑一段小视频**全都不报错** —— 后果是引擎收到一个
/// **不存在的模型目录**(或老用户存的序号悄悄指向另一支模型),要到用户那儿才炸。
/// 本仓库对这类"编译期看不见的错位"的既定手法就是契约测试(见 VideoModelOrderTests)。
///
/// 【口径来源】真相只有一个:磁盘上 `发布版\engines\rife`(或 `engines\rife`)下**真实存在**的模型目录。
/// 本文件**不手抄模型名清单** —— 抄了就会各写各的(仓库为此专门有过 AnnouncementCopyTests)。
///
/// 【注释豁免】判"下架项不许回来"时必须先剥掉 `<!-- -->` 注释:注释里会写规则本身
/// (比如"删掉的 5 项是……"),不剥就会自己绊倒自己 —— 这个坑 VideoModelOrderTests 第一次跑就踩过。</summary>
public class InterpDropdownContractTests
{
    /// <summary>2026-09-16 已下架、不许再作为**下拉项**出现的 4 支。
    /// 它们在 `发布版\engines\rife` 下的权重目录也同时搬去了 `_retired_rife\`。
    /// 【2026-09-24】原先是 5 支 —— `RIFE v4.26` 已由用户裁决作为第 3 支**上架**(权重搬回
    /// `发布版\engines\rife\rife-v4.26`,实测 1080p 79~92 ms/输出帧)⇒ 从本表移除。
    /// ⚠ 不是"删断言换绿":同一支模型被同时加进了下拉(下面的项数 2→3 与序号映射断言都跟着改了),
    ///   `Interp_dropdown_has_exactly_three_items` 与 `Interp_index_to_model_directory_mapping_matches_the_dropdown`
    ///   会一起把它按住;下架的那 4 支仍在表里、仍被断言不许回来。</summary>
    private static readonly string[] RetiredInterpItems =
    {
        "动漫专用", "高清 (RIFE HD)", "超高清 (RIFE UHD)", "经典兼容",
    };

    /// <summary>应当**恰好**三支:0 = v4.13(推荐)、1 = v4.6(同架构备用)、2 = v4.26(2026-09-24 末尾追加,实测最快)。
    /// 写成常量而不是从 XAML 反推,是为了让"有人再往下拉里塞一支"立刻失败 —— 那时候
    /// 序号映射、名字表、以及"老设置序号 3~6 会被范围检查挡掉"这三处都得跟着复核。</summary>
    private const int ExpectedInterpItemCount = 3;

    // ───────────────────────── ① 补帧下拉:只留三支 ─────────────────────────

    /// <summary>补帧下拉**恰好三项**,且依次是 v4.13 / v4.6 / v4.26(顺序即序号口径,不能反)。</summary>
    [Fact]
    public void Interp_dropdown_has_exactly_three_items_v413_then_v46_then_v426()
    {
        var items = InterpItems();

        Assert.Equal(ExpectedInterpItemCount, items.Count);
        Assert.Contains("通用画质最新 (RIFE v4.13)", items[0]);
        Assert.Contains("通用画质 (RIFE v4.6)", items[1]);
        Assert.Contains("通用画质再新 (RIFE v4.26)", items[2]);
    }

    /// <summary>下架的那 4 支不许再作为下拉项回来(先剥注释:注释里会写"删掉的 4 项是……"这规则本身)。</summary>
    [Fact]
    public void Interp_dropdown_no_longer_offers_the_four_retired_models()
    {
        var visible = InterpItems().Select(StripComments).ToArray();

        foreach (var retired in RetiredInterpItems)
        {
            var pattern = "Content=\"[^\"]*" + Regex.Escape(retired) + "[^\"]*\"";
            Assert.DoesNotContain(visible, chunk => Regex.IsMatch(chunk, pattern));
        }
    }

    // ───────────────────────── ② 每一项都要有「悬停提示 + 模型大小」 ─────────────────────────

    /// <summary>用户要求:"超分模型鼠标放上去要用提示、官方提示,并且最后显示模型大小"。
    /// 【2026-09-25】waifu2x 的模型下拉已随引擎项一起从视频页删除(用户裁定)→ 那条断言改成
    /// "它**不该再存在**";Real-ESRGAN 那 10 项照旧逐项要求提示 + 模型大小。</summary>
    [Fact]
    public void Every_super_resolution_item_carries_a_tooltip_that_ends_with_the_model_size()
    {
        var xaml = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml");

        // Real-ESRGAN 10 项(5 官方 + 3 自训 + Rev10 追加的两个 1x 修复条目)
        AssertItemsAllHaveTooltipWithSize(ComboItems(xaml, "VideoEsrganModelCombo"), 9, "Real-ESRGAN");
        // waifu2x 那个下拉与它的 3 个模型项都已删除 ⇒ 视频页不许再有它的入口
        Assert.DoesNotContain("VideoWaifu2xModelCombo", xaml);
        Assert.DoesNotContain("models-cunet", xaml);
        Assert.DoesNotContain("upconv_7_anime", xaml);
        Assert.DoesNotContain("upconv_7_photo", xaml);
    }

    /// <summary>补帧那两项同样要有提示与模型大小(精简后两项，一个都不能少)。</summary>
    [Fact]
    public void Every_interp_item_carries_a_tooltip_that_ends_with_the_model_size()
    {
        AssertItemsAllHaveTooltipWithSize(InterpItems(), ExpectedInterpItemCount, "补帧");
    }

    // ───────────────────────── ③ 序号 ↔ 引擎模型目录名 ─────────────────────────

    /// <summary>**核心契约**:下拉有多少项,`SelectedInterpModel` 就得映射多少个序号,而且
    /// 0/1/2 必须分别是 rife-v4.13 / rife-v4.6 / rife-v4.26(顺序与下拉一致)。
    /// 映射里只要残留一支下架模型,引擎就会拿到不存在的模型目录 —— 而这是编译期查不出来的。</summary>
    [Fact]
    public void Interp_index_to_model_directory_mapping_matches_the_dropdown()
    {
        var cs = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
        var map = ParseIndexToModelSwitch(cs, "private string SelectedInterpModel =>", "rife-");
        var items = InterpItems();

        Assert.Equal(items.Count, map.Count);
        Assert.Equal("rife-v4.13", map[0]);
        Assert.Equal("rife-v4.6", map[1]);
        Assert.Equal("rife-v4.26", map[2]);

        // 映射到的模型目录必须**真实存在**(引擎按目录名取权重)。
        // ⚠ 引擎目录是 .gitignore 的(engines/ 与 发布版/),全新克隆上不存在 ⇒ 找不到就跳过这条,
        //   其余断言照旧生效;本机(有发布版)上会真判。
        var engineDir = FindRifeEngineDir();
        if (engineDir == null) return;
        foreach (var name in map.Values.Distinct())
            Assert.True(Directory.Exists(Path.Combine(engineDir, name)),
                $"SelectedInterpModel 映射到「{name}」,但引擎目录下没有这个模型:{engineDir}");
    }

    /// <summary>历史/摘要里印的模型名表也必须与下拉同长度 —— 否则报表会印出"实跑哪支"之外的名字。
    /// (原先该表兜底给 "?",越界时看不出真正跑的是哪支;现已改为兜底给 0 的名字。)</summary>
    [Fact]
    public void Interp_model_name_table_has_one_entry_per_dropdown_item()
    {
        var cs = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
        var block = SwitchBodyOf(cs, "private static string InterpModelName(int idx)");
        var arms = Regex.Matches(block, @"\d+\s*=>").Count;

        Assert.Equal(InterpItems().Count, arms);
    }

    // ───────────────────────── 小工具 ─────────────────────────

    /// <summary>取补帧下拉那一段(到它自己的 `</ComboBox>` 为止)并按 `<ComboBoxItem` 切项。</summary>
    private static List<string> InterpItems()
    {
        var xaml = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml");
        return ComboItems(xaml, "InterpModelCombo");
    }

    private static List<string> ComboItems(string xaml, string comboName)
    {
        int start = xaml.IndexOf("x:Name=\"" + comboName + "\"", StringComparison.Ordinal);
        Assert.True(start > 0, "XAML 里找不到 " + comboName);
        int end = xaml.IndexOf("</ComboBox>", start, StringComparison.Ordinal);
        Assert.True(end > start, comboName + " 没有闭合的 </ComboBox>");
        var block = xaml.Substring(start, end - start);

        // 切项:第一段是 `<ComboBoxItem` 之前的表头/注释,丢掉
        return block.Split(new[] { "<ComboBoxItem" }, StringSplitOptions.None).Skip(1).ToList();
    }

    private static void AssertItemsAllHaveTooltipWithSize(List<string> items, int expectedCount, string which)
    {
        Assert.Equal(expectedCount, items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            Assert.True(items[i].Contains("ToolTipService.ToolTip"),
                $"{which} 第 {i + 1} 项没有悬停提示");
            // 【1x 条目例外 · Rev11】「动漫 · Anime4K 修复」(libplacebo 着色器)**不是独立权重**,
            // 提示里天然没有「模型大小」—— 要求它写等于编一个不存在的数字。换成对等的契约:
            // 必须说清它是什么(不放大),否则用户会以为它是普通放大模型,在 2x/4x 下选到它。
            // (Rev11 起「现实 · 1x 修复」已下线,这条例外只剩 Anime4K 一支。)
            bool is1xEntry = items[i].Contains($"Tag=\"{AlhPro.Core.Anime4k.ModelTag}\"");
            if (is1xEntry)
            {
                Assert.Contains("不放大", items[i]);
                continue;
            }
            Assert.True(items[i].Contains("模型大小"),
                $"{which} 第 {i + 1} 项的悬停提示里没有「模型大小」");
        }
    }

    private static string StripComments(string s)
        => Regex.Replace(s, "<!--.*?-->", "", RegexOptions.Singleline);

    /// <summary>解析 `switch { 0 => "rife-v4.13", ... }` 形式的序号→目录名映射(只收数字分支)。</summary>
    private static Dictionary<int, string> ParseIndexToModelSwitch(string cs, string signature, string prefix)
    {
        var block = SwitchBodyOf(cs, signature);
        var map = new Dictionary<int, string>();
        foreach (Match m in Regex.Matches(block, @"(?m)^\s*(\d+)\s*=>\s*""(" + Regex.Escape(prefix) + @"[^""]+)"""))
            map[int.Parse(m.Groups[1].Value)] = m.Groups[2].Value;
        return map;
    }

    private static string SwitchBodyOf(string cs, string signature)
    {
        int at = cs.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at > 0, "找不到成员:" + signature);
        int end = cs.IndexOf("};", at, StringComparison.Ordinal);
        Assert.True(end > at, signature + " 没有找到收尾的 };");
        return cs.Substring(at, end - at);
    }

    /// <summary>找引擎 rife 目录:先 发布版\engines\rife,再 engines\rife;都没有返回 null。</summary>
    private static string? FindRifeEngineDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            foreach (var parts in new[] { new[] { "发布版", "engines", "rife" }, new[] { "engines", "rife" } })
            {
                var cand = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
                if (Directory.Exists(cand)) return cand;
            }
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>从测试输出目录往上找仓库根,再取相对路径。</summary>
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
