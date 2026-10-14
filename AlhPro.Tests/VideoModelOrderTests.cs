using AlhPro.Core;
using System;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>视频页超分模型下拉的**序号迁移**(Rev0/1/2 → 最新)。
/// 【为什么单测它】下拉与预设存的都是序号;换顺序后老序号会指向另一支模型,而且是**静默**的:
/// 2026-09-12 那次换位若不迁移,用户"存的 3 = 轻量通用"会变成"3 = x4plus"(实测慢 17 倍)。
/// 这里把各次换位的换算、级联(老文件从 Rev0 一路换到最新)、幂等、越界兜底全钉住。
///
/// 各 Rev 的下拉顺序:
///   Rev0: 0=animevideov3 1=x4plus-anime 2=x4plus     3=general-x4v3
///   Rev1: 0=animevideov3 1=x4plus-anime 2=general-x4v3 3=x4plus
///   Rev2: 0=animevideov3 1=general-x4v3 2=x4plus-anime 3=x4plus  4=wdn-x4v3(末尾追加)
///   Rev3: 0=animevideov3 1=general-x4v3 2=wdn-x4v3     3=x4plus-anime 4=x4plus   ← 按速度/体积排
///   Rev4: 同 Rev3 的 0~4,**末尾追加** 5=alhpro-real2x 6=alhpro-game2x(v1;Rev7 已移除)
///         ⇒ Rev3 → Rev4 是**恒等映射**(只追加、不换位),老用户存的序号含义不变。
///   Rev5: 末尾追加 7=alhpro-game2x-v2;Rev6: 末尾追加 8=alhpro-game2x-v3
///   Rev7: 移除 v1 ⇒ 旧 6→6(v2)、旧 7→6、旧 8→7(**第一次"移除"而不是追加**)
///   Rev8: 末尾追加 anime4k;Rev9: anime4k 移出列表(旧 8→0);Rev10: Anime4K 作为 1x 条目加回来(+「现实 · 1x 修复」)
///   Rev11: 移除「现实 · 1x 修复」⇒ 旧 9→8(Anime4K 成了最后一项)
///   Rev12: **四支自训 2x 模型整体删除**(用户 2026-10-14:"还有那几个测试的模型可以删掉了")
///         ⇒ 旧 5/6/7(real2x / v2 / v3)→ 0(官方 animevideov3)、旧 8(Anime4K)→ 5。
///         现在列表 = 5 支官方放大模型 + 末尾 1 个 1x 修复条目(Anime4K)。
///         【为什么旧 5/6/7 落到 0 而不是相邻的 4】那三支都是"快"档小模型,而 4=x4plus 是 41MB / 15 秒一帧
///         的超慢档 —— 给删掉模型的用户塞一支超慢模型是本末倒置。0 是官方最容易跑、最省的那支。</summary>
public class VideoModelOrderTests
{
    /// <summary>**Rev11 → Rev12 的映射**(2026-10-14 删除四支自训 2x 模型)。这次是"删除 + 末项前移",
    /// 所以**不是恒等映射**,必须逐条钉住:
    ///   旧 0..4(五支官方放大模型)不动;旧 5/6/7(real2x / v2 / v3)全部 → 0;旧 8(Anime4K)→ 5。
    /// 【为什么每条都要单测】漏一条就是"老用户存的 v3 变成了 x4plus"或"1x 档落到放大模型上"这种静默错档 ——
    /// 编译、启动、跑视频都不会报错。</summary>
    [Theory]
    [InlineData(0, 0)]   // animevideov3 不动
    [InlineData(1, 1)]   // general-x4v3 不动
    [InlineData(2, 2)]   // wdn-x4v3 不动
    [InlineData(3, 3)]   // x4plus-anime 不动
    [InlineData(4, 4)]   // x4plus 不动
    [InlineData(5, 0)]   // real2x(已删)→ 官方 animevideov3
    [InlineData(6, 0)]   // game2x-v2(已删)→ 0
    [InlineData(7, 0)]   // game2x-v3(已删)→ 0
    [InlineData(8, 5)]   // Anime4K 从 8 前移到 5(不能用越界兜底:那会变成 0 = 放大模型,1x 档就静默错了)
    public void Rev11_to_current_collapses_the_deleted_models(int saved, int expected)
    {
        int m = VideoModelOrder.Migrate(saved, 11, out int rev);
        Assert.Equal(expected, m);
        Assert.Equal(VideoModelOrder.CurrentRev, rev);
    }

    /// <summary>Rev6(0..4 官方 + 5=real2x + 6=v1 + 7=v2 + 8=v3)级联到最新:五支官方模型不动,
    /// 四支自训项(连带 Rev7 那两支)全部落到 0(它们都已删除)。</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 4)]
    [InlineData(5, 0)]   // real2x(Rev4 追加)
    [InlineData(6, 0)]   // v1:Rev7 先换成 v2,Rev12 再删
    [InlineData(7, 0)]   // v2
    [InlineData(8, 0)]   // v3
    public void Rev6_to_current_collapses_the_self_trained_items(int saved, int expected)
    {
        int m = VideoModelOrder.Migrate(saved, 6, out int rev);
        Assert.Equal(expected, m);
        Assert.Equal(VideoModelOrder.CurrentRev, rev);
    }

    /// <summary>老序号整体换算后必须仍落在范围内(越界会被兜底成 0 = animevideov3,那是静默换模型)。</summary>
    [Fact]
    public void Rev6_range_stays_in_bounds_after_removal()
    {
        for (int i = 0; i < 9; i++)   // Rev6 时代最多 9 项
        {
            int m = VideoModelOrder.Migrate(i, 6, out _);
            Assert.InRange(m, 0, VideoModelOrder.Count - 1);
        }
    }

    /// <summary>**Rev8 → 最新**:Rev8 末尾多过一项(index 8 = anime4k),谁的设置里存过 8,现在都必须落到
    /// **0(animevideov3)** —— 这是 Rev9"把 Anime4K 移出模型列表"时的裁决,至今有效(0..7 一个都不许动)。
    /// 另外 5/6/7 那三支自训 2x 已删除 ⇒ 它们同样落到 0。
    /// 【为什么必须显式写】不写也会被"越界兜底归 0"接住、结果碰巧一样,但日志里看不出发生过换算。</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 0)]   // real2x(已删)
    [InlineData(6, 0)]   // v2(已删)
    [InlineData(7, 0)]   // v3(已删)
    [InlineData(8, 0)]   // 存过 Anime4K ⇒ 落到 animevideov3(最快最省、官方默认那一支)
    public void Rev8_to_current_drops_the_anime4k_slot(int saved, int expected)
    {
        int m = VideoModelOrder.Migrate(saved, 8, out int rev);
        Assert.Equal(expected, m);
        Assert.Equal(VideoModelOrder.CurrentRev, rev);
    }

    /// <summary>Rev3 → Rev4 必须**恒等**:这次只在下拉末尾追加两支自训模型,一个老项都没挪。
    /// 【为什么要专门钉一条恒等】"追加"看起来无害,可一旦有人把新模型插到中间(而不是追加),
    /// 0..4 的含义就变了、而 Rev 也照样 +1 ⇒ 老用户存的序号会被静默解释成另一支模型。
    /// 这条测试 + XAML 顺序契约测试一起,把"新模型只能出现在末尾"钉死。</summary>
    [Theory]
    [InlineData(0, 0)]   // animevideov3
    [InlineData(1, 1)]   // general-x4v3
    [InlineData(2, 2)]   // wdn-x4v3
    [InlineData(3, 3)]   // x4plus-anime
    [InlineData(4, 4)]   // x4plus
    public void Rev3_to_current_is_identity(int saved, int expected)
    {
        int m = VideoModelOrder.Migrate(saved, 3, out int rev);
        Assert.Equal(expected, m);
        Assert.Equal(VideoModelOrder.CurrentRev, rev);
        // 【写死数字是有意的】防"Rev 前进却忘了改这里"。
        // 【2026-09-21 改名**不该**动这个数字】加 `alh` 前缀只改显示名、不改顺序;
        // 【2026-09-21 Rev7】移除「游戏 · alhgame2x」是**改顺序** ⇒ 必须跟着 +1;
        // 【2026-09-21 Rev8/Rev9/Rev10】先追加 Anime4K → 移出列表 → 又作为 1x 条目加回来(+现实 1x)⇒ 三次各 +1;
        // 【2026-09-21 Rev11】移除「现实 · 1x 修复」⇒ 再 +1;
        // 【2026-10-14 Rev12】删除四支自训 2x ⇒ 再 +1(用户的删除也是一次顺序变更,同样必须迁移)。
        Assert.Equal(12, rev);
    }

    /// <summary>老 Rev 的序号级联到当前之后,含义必须与 Rev3 时代完全一致(多走了一段空映射也不能变)。</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 3)]   // x4plus-anime:Rev2 →2,Rev3 →3,之后恒等
    [InlineData(2, 4)]   // x4plus:Rev1 →3,Rev2 不变,Rev3 →4,之后恒等
    [InlineData(3, 1)]   // general-x4v3:Rev1 →2,Rev2 →1,Rev3 不变,之后恒等
    public void Old_revs_cascade_unchanged(int saved, int expected)
        => Assert.Equal(expected, VideoModelOrder.Migrate(saved, 0, out _));

    /// <summary>Rev2 → Rev3 的映射(那次换位的核心):2→3、3→4、4(=wdn)→2,0/1 不变。</summary>
    [Theory]
    [InlineData(0, 0)]   // animevideov3:最快最小,仍是第 1 位
    [InlineData(1, 1)]   // general-x4v3:5MB/0.458s,仍在第 2 位
    [InlineData(2, 3)]   // x4plus-anime 被 wdn 挤到后面
    [InlineData(3, 4)]   // x4plus(超慢)按体积与耗时退到最末
    [InlineData(4, 2)]   // Rev2 里末尾追加的 wdn-x4v3 上移到第 3 位
    public void Rev2_to_Rev3_maps_indices(int saved, int expected)
    {
        int m = VideoModelOrder.Migrate(saved, 2, out int rev);
        Assert.Equal(expected, m);
        Assert.Equal(VideoModelOrder.CurrentRev, rev);
    }

    /// <summary>Rev1(0=anime,1=x4plus-anime,2=general-x4v3,3=x4plus)要级联走完 Rev2 + Rev3。</summary>
    [Theory]
    [InlineData(0, 0)]   // animevideov3
    [InlineData(1, 3)]   // x4plus-anime:Rev2 →2,Rev3 →3
    [InlineData(2, 1)]   // general-x4v3:Rev2 →1,Rev3 不变
    [InlineData(3, 4)]   // x4plus:Rev2 不变,Rev3 →4
    public void Rev1_cascades_to_Rev3(int saved, int expected)
        => Assert.Equal(expected, VideoModelOrder.Migrate(saved, 1, out _));

    /// <summary>Rev0(0=anime,1=x4plus-anime,2=x4plus,3=general-x4v3)要连走 Rev1→Rev2→Rev3。</summary>
    [Theory]
    [InlineData(0, 0)]   // animevideov3
    [InlineData(1, 3)]   // x4plus-anime
    [InlineData(2, 4)]   // x4plus:Rev1 →3,Rev2 不变,Rev3 →4
    [InlineData(3, 1)]   // general-x4v3:Rev1 →2,Rev2 →1,Rev3 不变
    public void Rev0_cascades_to_Rev3(int saved, int expected)
        => Assert.Equal(expected, VideoModelOrder.Migrate(saved, 0, out _));

    /// <summary>幂等:已经是当前 Rev 的数据一个字节都不许动(否则每次启动来回横跳)。</summary>
    [Fact]
    public void Current_rev_is_idempotent()
    {
        for (int i = 0; i < VideoModelOrder.Count; i++)
        {
            int m = VideoModelOrder.Migrate(i, VideoModelOrder.CurrentRev, out int rev);
            Assert.Equal(i, m);
            Assert.Equal(VideoModelOrder.CurrentRev, rev);
        }
    }

    /// <summary>越界序号(手改坏的文件、或从更新版本回退)保守归到 0(animevideov3 = 最快最省那支),不猜中间项。</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(VideoModelOrder.Count)]
    [InlineData(99)]
    public void Out_of_range_falls_back_to_zero(int saved)
    {
        int m = VideoModelOrder.Migrate(saved, VideoModelOrder.CurrentRev, out _);
        Assert.Equal(0, m);
        // 老 Rev 的越界值同样兜底(先换算再看界,换算后仍在界外才归零)
        int m2 = VideoModelOrder.Migrate(saved, 2, out _);
        Assert.InRange(m2, 0, VideoModelOrder.Count - 1);
    }

    /// <summary>下拉项数量必须与"迁移后合法序号范围"一致 —— 加/删模型时这条会立刻报警。</summary>
    [Fact]
    public void Count_matches_the_dropdown()
    {
        Assert.Equal(6, VideoModelOrder.Count);   // Rev12:5 支官方放大模型(0..4)+ Anime4K 1x 条目(5)
        Assert.Equal(12, VideoModelOrder.CurrentRev);
        // 每一项在自己 Rev 下都要能落在合法范围里
        for (int i = 0; i < VideoModelOrder.Count; i++)
            Assert.InRange(VideoModelOrder.Migrate(i, VideoModelOrder.CurrentRev, out _), 0, VideoModelOrder.Count - 1);
    }

    /// <summary>**1x 条目的序号常量**必须就是列表最后一项(1x 超分档靠 Anime4kIndex 选中 Anime4K 那一档
    /// ⇒ 这个常量错了,用户选 1x 会落到别的模型上,而界面上看不出异常 ✗)。
    /// 【2026-09-22 注】原先这句还写着"官方预设「1x 修复（不放大）」靠它选中" —— 那条官方预设已按用户裁决删除
    /// (它当初就是为这条预设加的),但**常量本身仍然必须对**:1x 档的下拉/迁移都依赖它。
    /// 与 Xaml_dropdown_order_matches_the_migration_table 的分工:那条钉"映射表 ↔ XAML 的 Tag 顺序"
    /// (Tag 列表末尾必须是 anime4k),这条钉"常量 ↔ 序号"。两条合起来 = 常量确实指向正确的 Tag。</summary>
    [Fact]
    public void One_x_index_constant_points_at_the_last_entry()
    {
        // Rev12:1x 档只剩 Anime4K 一条,它就是列表最后一项
        Assert.Equal(VideoModelOrder.Count - 1, VideoModelOrder.Anime4kIndex);
        // 【2026-10-14 独立复审修正】这里原来还有一句
        //   `Assert.Equal(Anime4kIndex, Migrate(Anime4kIndex, CurrentRev))` —— 它被上面那句蕴含
        //   (序号 < Count ⇒ 迁移必然恒等返回),是恒真式,把实现改坏也照样绿。删掉它;
        //   真正有信息量的是下面两条:**旧值必须被显式迁到 Anime4K,而不是落到越界兜底的 0**。
        // Rev10 的旧值 9(已下线的「现实 · 1x 修复」)必须显式迁到 Anime4K,而不是落到越界兜底的 0
        Assert.Equal(VideoModelOrder.Anime4kIndex, VideoModelOrder.Migrate(9, 10, out int r));
        Assert.Equal(12, r);
        // Rev11 的 Anime4K 序号(8)在 Rev12 里同样要落到 Anime4kIndex
        Assert.Equal(VideoModelOrder.Anime4kIndex, VideoModelOrder.Migrate(8, 11, out _));
        // 老序号 0..4(五支官方放大模型)在任何一次迁移里都不能动
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(i, VideoModelOrder.Migrate(i, 10, out _));
            Assert.Equal(i, VideoModelOrder.Migrate(i, 11, out _));
        }
        // 已删的三支自训 2x(Rev11 里的 5/6/7)必须落到 0
        foreach (int gone in new[] { 5, 6, 7 })
            Assert.Equal(0, VideoModelOrder.Migrate(gone, 11, out _));
    }

    /// <summary>**预设摘要的模型名表必须覆盖 1x 那一条**(Rev12:Anime4K 是索引 5、也是最后一项)。
    /// 【为什么】摘要按序号取名字表:表短了 `UpEsrganModelName(5)` 会落到越界兜底
    /// 显示「动漫·animevideov3」—— 用户明明选的是"1x 修复",预设摘要却印了一个放大模型的名字,界面上是假话。
    /// 名字必须取自 Core 常量(单一来源),不许在这里另抄一份措辞。</summary>
    [Fact]
    public void Preset_summary_name_table_covers_the_1x_entry()
    {
        string? path = null;
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var cand = System.IO.Path.Combine(dir.FullName, "ImgUpscalerUI", "Views", "VideoView.xaml.cs");
            if (System.IO.File.Exists(cand)) { path = cand; break; }
            dir = dir.Parent;
        }
        Assert.NotNull(path);
        var src = System.IO.File.ReadAllText(path!);
        Assert.Contains("names.Add(AlhPro.Core.Anime4k.MenuText);", src);
        Assert.Equal(VideoModelOrder.Anime4kIndex, 5);   // 与上面那条常量契约联动
    }

    /// <summary>**契约测试**:VideoView.xaml 里超分模型下拉的真实 Tag 顺序,必须与 VideoModelOrder 的映射表逐位一致。
    /// 【为什么必须有】2026-09-14 重排时 XAML 漏掉了一行(只留注释),下拉从 5 项变 4 项,而迁移表仍按 5 项映射
    /// (Rev2 的 4→新 2)—— 老用户存的序号会被解释成另一支模型,而且**编译、单测、启动验证全都通过**,
    /// 只有"人眼看界面"才发现。这条测试把"源码里的顺序"钉住,让这类漏行在下一次 dotnet test 就爆出来。</summary>
    [Fact]
    public void Xaml_dropdown_order_matches_the_migration_table()
    {
        var xaml = ReadVideoViewXaml();

        // 截取 VideoEsrganModelCombo 那一段(到它自己的 </ComboBox> 为止),再取其中的 Tag
        int start = xaml.IndexOf("x:Name=\"VideoEsrganModelCombo\"", StringComparison.Ordinal);
        Assert.True(start > 0, "XAML 里找不到 VideoEsrganModelCombo");
        int end = xaml.IndexOf("</ComboBox>", start, StringComparison.Ordinal);
        Assert.True(end > start, "VideoEsrganModelCombo 没有闭合标签");
        var block = xaml.Substring(start, end - start);

        var tags = System.Text.RegularExpressions.Regex.Matches(block, "Tag=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value).ToArray();

        // Rev12 的目标顺序:五支官方放大模型(快/小 → 慢/大)+ 末尾 1 个 1x 修复条目,与迁移表必须一致
        Assert.Equal(new[]
        {
            "realesr-animevideov3",      // 0 4MB  0.26~0.30 s/帧
            "realesr-general-x4v3",      // 1 5MB  0.458
            "realesr-general-wdn-x4v3",  // 2 5MB  0.460
            "realesrgan-x4plus-anime",   // 3 9MB  3.85
            "realesrgan-x4plus",         // 4 41MB 15.145(超慢排最后)
            "anime4k",                   // 5 **1x 修复**条目(不放大;选 1x 时才可选,2x+ 置灰)
                                         //   Rev12:它前面四支自训 2x 已删除 ⇒ 从 8 前移到 5,仍是最后一项
        }, tags);
        Assert.Equal(VideoModelOrder.Count, tags.Length);
        Assert.Equal(VideoModelOrder.Anime4kIndex, Array.IndexOf(tags, "anime4k"));
        Assert.Equal(AlhPro.Core.Upscale1x.All, tags[^1..]);   // 末尾一项 = 1x 修复条目
    }

    /// <summary>**契约测试**:四支自训 2x 模型与蓝色「测试」药丸必须**彻底消失**(用户 2026-10-14:
    /// "还有那几个测试的模型可以删掉了")。这条与 Xaml_dropdown_order_matches_the_migration_table 互补:
    /// 那条管"顺序对不对",这条管"删干净没有" —— 残留一项会让迁移表与下拉错位(而编译、启动、跑视频都不报错)。
    /// 【历史 · 为什么药丸当初会存在】那时四支自训模型要标"这不是官方权重":药丸宽 33px,收起状态下
    /// 文字右侧只剩约 18px 放不下,所以左栏从 300 加宽到 322;模型删了 ⇒ 药丸、宽度、文案一起退回。</summary>
    [Fact]
    public void No_self_trained_items_or_test_badge_remain()
    {
        var xaml = ReadVideoViewXaml();
        // 注释里**有意**留着这段历史("这里原本是…"),所以判禁令前先把注释剥掉(既有测试同一手法)
        var visible = System.Text.RegularExpressions.Regex.Replace(xaml, "<!--.*?-->", "",
            System.Text.RegularExpressions.RegexOptions.Singleline);

        // ① 药丸样式与它的文字都不许留
        Assert.DoesNotContain("TestBadgePill", visible);
        Assert.DoesNotContain("TestBadgeText", visible);
        // ② 四支自训模型的 Tag / 显示名一个都不许留(显示名里的 alhreal2x / alhgame2x 是当年的前缀写法;
        //    计划中的 1x 修复 Tag 是 alhpro-fix1x,它**不进下拉**,所以这里连 "alhpro-" 一起禁)
        Assert.DoesNotContain("alhpro-", visible);
        Assert.DoesNotContain("alhreal2x", visible);
        Assert.DoesNotContain("alhgame2x", visible);
        // ③ 左栏宽度回到加宽前的 300(那 22px 是为药丸加的,用途已消失)
        Assert.Contains("<ColumnDefinition Width=\"300\"/>", xaml);
        // ④ 下拉整体的悬停提示里不许再提"末尾四项自训模型"
        Assert.DoesNotContain("自训", visible);
    }

    /// <summary>**契约测试**:下拉里那五支官方放大模型的权重必须真的在 `models\` 下(存在才检查,便于无模型的全新克隆)。
    /// 【为什么测】引擎按 `models/{名字}.param` 取权重,Tag 与文件名对不上就是运行时"找不到模型"——
    /// 实测该引擎缺权重时**exit=0 不报错、只画坏帧**(2026-09-15:2x 权重配 -s 4 = 镜像平铺的错帧)。
    /// Rev12 起自训那几支的权重已删除,这里只查官方五支(Anime4K 走着色器,**没有权重文件**)。
    /// ⚠ **animevideov3 是个例外**:它的三档权重是**按倍率分文件**的
    /// (`realesr-animevideov3-x2/-x3/-x4.param`,没有裸 `realesr-animevideov3.param`)——
    /// 这也正是"给它传 `-s 1` 会 `_wfopen …-x1.param failed` 并输出纯黑"的由来。这条断言顺手把命名钉住。</summary>
    [Fact]
    public void Official_weight_files_match_the_tags_when_engines_are_present()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        string? modelsDir = null;
        while (dir != null)
        {
            var cand = System.IO.Path.Combine(dir.FullName, "engines", "realesrgan", "models");
            if (System.IO.Directory.Exists(cand)) { modelsDir = cand; break; }
            dir = dir.Parent;
        }
        if (modelsDir is null) return;   // 无引擎/模型的全新克隆:跳过(不误报)

        foreach (var tag in new[] { "realesr-general-x4v3", "realesr-general-wdn-x4v3",
                                    "realesrgan-x4plus-anime", "realesrgan-x4plus" })
        {
            Assert.True(System.IO.File.Exists(System.IO.Path.Combine(modelsDir, tag + ".param")),
                $"{modelsDir} 下缺少 {tag}.param(引擎按这个目录+名字取权重)");
            Assert.True(System.IO.File.Exists(System.IO.Path.Combine(modelsDir, tag + ".bin")),
                $"{modelsDir} 下缺少 {tag}.bin");
        }
        // animevideov3:三档同名的倍率后缀(缺哪一档,那一档的目标倍数就会静默出坏帧)
        foreach (var suffix in new[] { "-x2", "-x3", "-x4" })
        {
            Assert.True(System.IO.File.Exists(System.IO.Path.Combine(modelsDir, "realesr-animevideov3" + suffix + ".param")),
                $"{modelsDir} 下缺少 realesr-animevideov3{suffix}.param");
            Assert.True(System.IO.File.Exists(System.IO.Path.Combine(modelsDir, "realesr-animevideov3" + suffix + ".bin")),
                $"{modelsDir} 下缺少 realesr-animevideov3{suffix}.bin");
        }
        Assert.False(System.IO.File.Exists(System.IO.Path.Combine(modelsDir, "realesr-animevideov3.param")),
            "不该有裸 realesr-animevideov3.param:那支模型只有 -x2/-x3/-x4 三份权重(-s 1 无路可走)");
    }

    /// <summary>**契约测试**:Rev 迁移必须留日志(既有实现有,见 VideoView.xaml.cs 的 MigrateEsrganModelOrder)。
    /// 迁移会**静默改掉用户存的模型**,日志是排查"为什么突然换成/变慢"的唯一线索;被删掉的话
    /// 编译、单测、启动全都照样通过,所以要在这里把源码钉住。</summary>
    [Fact]
    public void Migration_keeps_its_log_line()
    {
        string? path = null;
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var cand = System.IO.Path.Combine(dir.FullName, "ImgUpscalerUI", "Views", "VideoView.xaml.cs");
            if (System.IO.File.Exists(cand)) { path = cand; break; }
            dir = dir.Parent;
        }
        Assert.NotNull(path);
        var cs = System.IO.File.ReadAllText(path!);

        int at = cs.IndexOf("private static bool MigrateEsrganModelOrder", StringComparison.Ordinal);
        Assert.True(at > 0, "找不到 MigrateEsrganModelOrder");
        int end = cs.IndexOf("\n    }", at, StringComparison.Ordinal);
        var body = cs.Substring(at, end - at);
        Assert.Contains("VideoModelOrder.Migrate", body);          // 换算走纯函数(不在 UI 里各写一份)
        Assert.Contains("VideoModelOrder.CurrentRev", body);       // 版本判断引用常量,禁写死
        Assert.Contains("AppLogger.Info", body);                   // 留日志
        Assert.Contains("迁移", body);
        // 界面那份名字列表:五支官方放大模型 + 末尾 1x 条目,且 1x 那条**从 Core 生成**(不手抄字面量)。
        // 手抄的那版曾写成"实验·real2x(2x·测试)",被用户当场纠正"不要写实验、括号里写速度"。
        Assert.Contains("UpEsrganModelNames", cs);
        Assert.Contains("AlhPro.Core.Anime4k.MenuText", cs);
        // 【2026-10-14 独立复审修正】这里原来断的是 `DoesNotContain("实验·real2x")` / `("实验·game2x")` ——
        //   那两个字符串只由**已删除的代码**产生,任何真实回归都不会让它们出现 ⇒ 断言永不失败,等于没测。
        //   改成一条真不变量:界面那份名字表的**项数必须等于下拉项数**(VideoModelOrder.Count)。
        //   少一项时预设摘要会印出别的模型名(与 Preset_summary_name_table_covers_the_1x_entry 同源);
        //   多一项则说明下拉删了项而名字表没跟着删。
        int buildAt = cs.IndexOf("private static string[] BuildUpEsrganModelNames", StringComparison.Ordinal);
        Assert.True(buildAt > 0, "找不到 BuildUpEsrganModelNames");
        int buildEnd = cs.IndexOf("\n    }", buildAt, StringComparison.Ordinal);
        var namesBody = cs.Substring(buildAt, buildEnd - buildAt);
        int initAt = namesBody.IndexOf('{');
        int initEnd = namesBody.IndexOf("};", initAt, StringComparison.Ordinal);
        Assert.True(initAt > 0 && initEnd > initAt, "解析不了名字表的初始化块");
        int literals = System.Text.RegularExpressions.Regex.Matches(namesBody[initAt..initEnd], "\"[^\"]*\"").Count;
        int adds = System.Text.RegularExpressions.Regex.Matches(namesBody, "names\\.Add\\(").Count;
        Assert.Equal(VideoModelOrder.Count, literals + adds);   // 5 支官方放大模型 + 1 个 1x 条目
    }

    private static string ReadVideoViewXaml()
    {
        string? path = null;
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var cand = System.IO.Path.Combine(dir.FullName, "ImgUpscalerUI", "Views", "VideoView.xaml");
            if (System.IO.File.Exists(cand)) { path = cand; break; }
            dir = dir.Parent;
        }
        Assert.NotNull(path);
        return System.IO.File.ReadAllText(path!);
    }
}
