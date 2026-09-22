using AlhPro.Core;
using System;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>视频页超分模型下拉的**序号迁移**(Rev0/1/2 → Rev4)。
/// 【为什么单测它】下拉与预设存的都是序号;换顺序后老序号会指向另一支模型,而且是**静默**的:
/// 2026-09-12 那次换位若不迁移,用户"存的 3 = 轻量通用"会变成"3 = x4plus"(实测慢 17 倍)。
/// 这里把三次换位的换算、级联(老文件从 Rev0 一路换到最新)、幂等、越界兜底全钉住。
///
/// 各 Rev 的下拉顺序:
///   Rev0: 0=animevideov3 1=x4plus-anime 2=x4plus     3=general-x4v3
///   Rev1: 0=animevideov3 1=x4plus-anime 2=general-x4v3 3=x4plus
///   Rev2: 0=animevideov3 1=general-x4v3 2=x4plus-anime 3=x4plus  4=wdn-x4v3(末尾追加)
///   Rev3: 0=animevideov3 1=general-x4v3 2=wdn-x4v3     3=x4plus-anime 4=x4plus   ← 按速度/体积排
///   Rev4: 同 Rev3 的 0~4,**末尾追加** 5=alhpro-real2x(现实 · alhreal2x) 6=alhpro-game2x(游戏 · alhgame2x;Rev7 已移除)
///         ⇒ Rev3 → Rev4 是**恒等映射**(只追加、不换位),老用户存的序号含义不变。
///         【命名 · 用户 2026-09-15 定】界面文字按 `类别 · 名字（速度）` 写,类别是「游戏」「现实」,
///         名字是英文(real2x / game2x = 权重文件名后缀),不出现"实验"字样;
///         【名字前缀 · 用户 2026-09-21 定】"改名字 前面加上alh" ⇒ 名字写作 `alhreal2x` / `alhgame2x` 这样
///         (alh 与名字连写、无连字符;**只是显示名**,权重文件名/Tag/序号都没动 ⇒ 不需要 +1 Rev);
///         【括号内容 · 用户 2026-09-16 定】"括号内要写快 而不是时间" ⇒ 括号里是速度档词「快」
///         (Core.ExperimentalEsrgan.SpeedTier);秒/帧数字只在悬停提示与下拉下方提示里。</summary>
public class VideoModelOrderTests
{
    /// <summary>**Rev6 → Rev7 的映射**(2026-09-21 移除「游戏 · alhgame2x」)。这是本仓库第一次做**移除**而不是追加,
    /// 所以**不是恒等映射**,必须逐条钉住:
    ///   旧 0..5 不动;旧 6(被移除的 v1)→ 6(v2,同族最保真);旧 7(v2)→ 6;旧 8(v3)→ 7。
    /// 【为什么每条都要单测】移除项本身不报错:漏一条就是"老用户存的 v3 变成了 v2"这种静默错档。</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 4)]
    [InlineData(5, 5)]   // real2x 不动
    [InlineData(6, 6)]   // 被移除的 v1 → v2(只升不降:实测 v2 全面优于 v1)
    [InlineData(7, 6)]   // v2 → 6
    [InlineData(8, 7)]   // v3 → 7
    public void Rev6_to_current_moves_the_three_self_trained_items(int saved, int expected)
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

    /// <summary>**Rev8 → Rev9 的映射**(2026-09-21 把 Anime4K 移出模型列表)。Rev8 时末尾多过一项(index 8 = anime4k),
    /// 谁的设置里存过 8,现在都必须落到 **0(animevideov3)** —— 0..7 一个都不许动(Anime4K 当初就是末尾追加的)。
    /// 【为什么必须显式写】不写也会被"越界兜底归 0"接住、结果碰巧一样,但日志里看不出发生过换算。</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 5)]
    [InlineData(6, 6)]
    [InlineData(7, 7)]
    [InlineData(8, 0)]   // 存过 Anime4K ⇒ 落到 animevideov3(最快最省、官方默认那一支)
    public void Rev8_to_current_drops_the_anime4k_slot(int saved, int expected)
    {
        int m = VideoModelOrder.Migrate(saved, 8, out int rev);
        Assert.Equal(expected, m);
        Assert.Equal(VideoModelOrder.CurrentRev, rev);
    }

    /// <summary>Rev3 → Rev4 必须**恒等**:这次只在下拉末尾追加两支自训模型(现实 · alhreal2x / 游戏 · alhgame2x),一个老项都没挪。
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
        // 【2026-09-21 Rev8/Rev9/Rev10】先追加 Anime4K → 移出列表 → 又作为 1x 条目加回来(+现实 1x)⇒ 三次各 +1。
        Assert.Equal(10, rev);
    }

    /// <summary>老 Rev 的序号级联到 Rev4 之后,含义必须与 Rev3 时代完全一致(多走了一段空映射也不能变)。</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 3)]   // x4plus-anime:Rev2 →2,Rev3 →3,Rev4 恒等
    [InlineData(2, 4)]   // x4plus:Rev1 →3,Rev2 不变,Rev3 →4,Rev4 恒等
    [InlineData(3, 1)]   // general-x4v3:Rev1 →2,Rev2 →1,Rev3 不变,Rev4 恒等
    public void Old_revs_cascade_to_Rev4_unchanged(int saved, int expected)
        => Assert.Equal(expected, VideoModelOrder.Migrate(saved, 0, out _));

    /// <summary>Rev2 → Rev3 的映射(本次换位的核心):2→3、3→4、4(=wdn)→2,0/1 不变。</summary>
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
        Assert.Equal(10, VideoModelOrder.Count);   // Rev10 = Rev9 的 8 项 + 两个 1x 修复条目
        Assert.Equal(10, VideoModelOrder.CurrentRev);
        // 每一项在自己 Rev 下都要能落在合法范围里
        for (int i = 0; i < VideoModelOrder.Count; i++)
            Assert.InRange(VideoModelOrder.Migrate(i, VideoModelOrder.CurrentRev, out _), 0, VideoModelOrder.Count - 1);
    }

    /// <summary>**1x 两个条目的序号常量**必须就是列表最后两项(1x 超分档靠 Anime4kIndex 选中 Anime4K 那一档
    /// ⇒ 这个常量错了,用户选 1x 会落到别的模型上,而界面上看不出异常 ✗)。
    /// 【2026-09-22 注】原先这句还写着"官方预设「1x 修复（不放大）」靠它选中" —— 那条官方预设已按用户裁决删除
    /// (它当初就是为这条预设加的),但**常量本身仍然必须对**:1x 档的下拉/迁移都依赖它。
    /// 与 Xaml_dropdown_order_matches_the_migration_table 的分工:那条钉"映射表 ↔ XAML 的 Tag 顺序"
    /// (Tag 列表末尾必须是 alhpro-game2x-v3 / anime4k / alhpro-real1x),这条钉"常量 ↔ 序号"。
    /// 两条合起来 = 常量确实指向正确的 Tag。</summary>
    [Fact]
    public void One_x_index_constants_point_at_the_last_two_entries()
    {
        Assert.Equal(VideoModelOrder.Count - 2, VideoModelOrder.Anime4kIndex);
        Assert.Equal(VideoModelOrder.Count - 1, VideoModelOrder.Real1xIndex);
        Assert.True(VideoModelOrder.Anime4kIndex < VideoModelOrder.Real1xIndex);
        // 当前 Rev 下迁移必须幂等(否则每次启动都会把这支模型挪走)
        Assert.Equal(VideoModelOrder.Anime4kIndex,
            VideoModelOrder.Migrate(VideoModelOrder.Anime4kIndex, VideoModelOrder.CurrentRev, out _));
        Assert.Equal(VideoModelOrder.Real1xIndex,
            VideoModelOrder.Migrate(VideoModelOrder.Real1xIndex, VideoModelOrder.CurrentRev, out _));
    }

    /// <summary>**契约测试**:VideoView.xaml 里超分模型下拉的真实 Tag 顺序,必须与 VideoModelOrder 的映射表逐位一致。
    /// 【为什么必须有】2026-09-14 重排时 XAML 漏掉了一行(只留注释),下拉从 5 项变 4 项,而迁移表仍按 5 项映射
    /// (Rev2 的 4→新 2)—— 老用户存的序号会被解释成另一支模型,而且**编译、单测、启动验证全都通过**,
    /// 只有"人眼看界面"才发现。这条测试把"源码里的顺序"钉住,让这类漏行在下一次 dotnet test 就爆出来。</summary>
    [Fact]
    public void Xaml_dropdown_order_matches_the_migration_table()
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
        var xaml = System.IO.File.ReadAllText(path!);

        // 截取 VideoEsrganModelCombo 那一段(到它自己的 </ComboBox> 为止),再取其中的 Tag
        int start = xaml.IndexOf("x:Name=\"VideoEsrganModelCombo\"", StringComparison.Ordinal);
        Assert.True(start > 0, "XAML 里找不到 VideoEsrganModelCombo");
        int end = xaml.IndexOf("</ComboBox>", start, StringComparison.Ordinal);
        Assert.True(end > start, "VideoEsrganModelCombo 没有闭合标签");
        var block = xaml.Substring(start, end - start);

        var tags = System.Text.RegularExpressions.Regex.Matches(block, "Tag=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value).ToArray();

        // Rev4 的目标顺序(Rev3 的快/小 → 慢/大,末尾追加两支自训 2x),与 VideoModelOrder 的映射表必须一致
        Assert.Equal(new[]
        {
            "realesr-animevideov3",      // 0 4MB  0.26~0.30 s/帧
            "realesr-general-x4v3",      // 1 5MB  0.458
            "realesr-general-wdn-x4v3",  // 2 5MB  0.460
            "realesrgan-x4plus-anime",   // 3 9MB  3.85
            "realesrgan-x4plus",         // 4 41MB 15.145(超慢排最后)
            "alhpro-real2x",             // 5 Rev4 追加:现实 · alhreal2x
            "alhpro-game2x-v2",          // 6 Rev7:原 7。v1(alhpro-game2x)已从下拉移除 ⇒ 后面整体前移
            "alhpro-game2x-v3",          // 7 Rev7:原 8
            "anime4k",                   // 8 Rev10:**1x 修复**条目(不放大;选 1x 时才可选,2x+ 置灰)
            "alhpro-real1x",             // 9 Rev10:**现实 1x 修复**(内部 alhreal2x 2x→缩回;同样只在 1x 可选)
        }, tags);
        Assert.Equal(VideoModelOrder.Count, tags.Length);
        // 追加的自训模型必须与 Core 里登记的名字一字不差(名字同时是 models\ 下的权重文件名:
        // 引擎按 `models/{Tag}.param` 取权重,Tag 写错 = 运行时找不到模型/坏帧)。
        // Rev8 起 Anime4K 排在自训模型**之后**(它不属于 ExperimentalEsrgan.All:那是"自训"名单)⇒ 取 [^4..^1]。
        // Rev10 起:自训三支在中间(5..7),**末尾两个是 1x 修复条目** ⇒ 自训那段要按 [^5..^2] 取
        Assert.Equal(AlhPro.Core.ExperimentalEsrgan.All, tags[^(AlhPro.Core.ExperimentalEsrgan.All.Length + 2)..^2]);
        Assert.Equal(AlhPro.Core.Upscale1x.All, tags[^2..]);   // 末尾两项 = 1x 修复条目(顺序也要一致)
    }

    /// <summary>**契约测试**:四项自训模型在下拉里必须带 `alh` 名字前缀 + 蓝色「测试」小药丸 + 事实性悬停提示,且**不许写"实验"**。
    /// 【为什么要测】用户的要求是"名字叫游戏/现实、名字前面加 alh、括号里写实测速度、不要写实验、末尾那个测试标要留着":
    /// 前缀没了、药丸没了、字样跑回来、或提示被删成空话,这条要求就落空了,而**编译、启动、跑视频都不会报错**。
    /// 这里钉五件事:① 四个 Tag 所在项都带 Core 给的名字(`alh` 前缀)**且都挂着药丸**、项内不出现"实验";
    /// ② 药丸样式是**收小后的**蓝色配色;
    /// ③ 下拉项文字 = Core 给的 `类别 · alh名字（速度）` 逐字一致;
    /// ④ 每项都有 ToolTip,与 Core 的文案逐字相同(含三档色偏/detail/速度/通道验收数字);
    /// ⑤ 不再出现"偏蓝 / 需重新导出 / 不可用于正片"这类**修好之后就已过时**的结论。
    ///
    /// 【蓝标尺寸 · 用户 2026-09-15 收尾要求"更小更简约"】原来是 FontSize 12 + Padding 6,1 + 圆角 7 + 1px 蓝描边,
    /// 药丸宽 33px;现改为 FontSize 11 + Padding 4,0 + 圆角 4 + **无描边**。
    /// 配色也压暗一档(#2A3E63/#A8C8FF → #232E42/#8FA9D6),不再抢模型名。
    /// 这几条数值同样是**用户点名要的**(去边框或极淡、字号 10~11、Padding 3,0~4,1、圆角 4~5、颜色克制),
    /// 所以逐字钉住 —— 样式本身别乱改。
    ///
    /// 【宽度账本 · 2026-09-21:药丸先撤掉、当天又放回,来龙去脉都在这里】
    ///   ① 名字加 `alh` 后,收起状态实测(截图像素,组合框 235px):内容起点 x=16(蓝条)→ 文字 x=25..183
    ///      → 下拉箭头 x=207..214 ⇒ 文字右侧**只剩约 18px**;而药丸要 6(间距)+30 = 36px ⇒ 会被箭头裁掉。
    ///   ② 于是当天先撤了药丸(`alh` 前缀当标记),并把这件事报告给用户。
    ///   ③ 用户看到后问"测试标记呢",并**在三个方案里选了"左栏加宽 22px"** ⇒ `VideoView.xaml` 的左栏 300 → 322,
    ///      余量 18 → 40px,**药丸原样放回**。
    ///   ⚠ 所以这条测试现在是**正向断言**(项里必须有药丸);谁要再撤,先量收起状态再说,别只看展开的下拉 ——
    ///     展开态的下拉项是**按内容自动变宽**的(实测:内容 177px → 项宽 201px;内容 195px → 项宽 219px),
    ///     "展开态没被裁"不等于"收起态不会被裁"。</summary>
    [Fact]
    public void Experimental_items_carry_the_alh_prefix_and_measured_tooltip()
    {
        var xaml = ReadVideoViewXaml();

        // ① 药丸样式本身:蓝色系(深蓝底 + 柔和蓝字),且**已按用户要求收小**;四个自训项都在用它。
        int pill = xaml.IndexOf("x:Key=\"TestBadgePill\"", StringComparison.Ordinal);
        Assert.True(pill > 0, "XAML 里找不到 TestBadgePill 样式(蓝色「测试」小标)");
        var pillBlock = xaml.Substring(pill, Math.Min(900, xaml.Length - pill - 1));
        Assert.Contains("<Setter Property=\"Background\" Value=\"#232E42\"/>", pillBlock);   // 深蓝底(压暗一档,与 App 深色调色板同族)
        Assert.Contains("<Setter Property=\"Foreground\" Value=\"#8FA9D6\"/>", pillBlock);   // 柔和蓝字(比原来的 #A8C8FF 更克制)
        Assert.Contains("<Setter Property=\"BorderThickness\" Value=\"0\"/>", pillBlock);    // **去掉描边**(用户:去边框或极淡)
        Assert.Contains("<Setter Property=\"CornerRadius\" Value=\"4\"/>", pillBlock);       // 小圆角(原来 7)
        Assert.Contains("<Setter Property=\"Padding\" Value=\"4,0\"/>", pillBlock);          // 内边距(原来 6,1)
        Assert.Contains("<Setter Property=\"Text\" Value=\"测试\"/>", pillBlock);            // 药丸文字(用户:先保留「测试」)
        Assert.Contains("<Setter Property=\"FontSize\" Value=\"11\"/>", pillBlock);          // 11px(原来 12px)
        Assert.DoesNotContain("<Setter Property=\"BorderBrush\"", pillBlock);                // 没有描边画刷了
        Assert.DoesNotContain("CornerRadius\" Value=\"7\"", pillBlock);                      // 不许退回旧的大圆角
        Assert.DoesNotContain("FontSize\" Value=\"12\"", pillBlock);                         // 不许退回旧字号

        // ② 四个 Tag 所在项:名字(alh 前缀)+ 蓝色「测试」药丸 + ToolTip,且不许出现"实验"
        foreach (var tag in AlhPro.Core.ExperimentalEsrgan.All)
        {
            int at = xaml.IndexOf($"Tag=\"{tag}\"", StringComparison.Ordinal);
            Assert.True(at > 0, $"XAML 里找不到下拉项 Tag=\"{tag}\"");
            int endItem = xaml.IndexOf("</ComboBoxItem>", at, StringComparison.Ordinal);
            Assert.True(endItem > at, $"{tag} 的 ComboBoxItem 没有闭合");
            var item = xaml.Substring(at, endItem - at);
            // 【只在"用户可见文字"上判禁令】XAML 项里的 `<!-- -->` 注释可以写"不许出现实验"这句规则本身,
            // 所以先把注释剥掉再判 —— 否则这条断言会被自己的说明文字绊倒(第一次跑就是这么失败的)。
            var visible = System.Text.RegularExpressions.Regex.Replace(item, "<!--.*?-->", "",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            // 药丸必须在(用户 2026-09-21:"测试标记呢" ⇒ 三个方案里选了加宽左栏 22px 把它放回来)。
            // 若将来有人想撤它:先量收起状态(截图找箭头左边缘),算式与来龙去脉见本测试 summary。
            Assert.Contains("TestBadgePill", item);
            // 名字必须带项目前缀(用户 2026-09-21:"改名字 前面加上alh")
            Assert.Contains("alh", AlhPro.Core.ExperimentalEsrgan.MenuName(tag));
            Assert.Contains("ToolTipService.ToolTip", item);                 // 悬停提示
            Assert.Contains("色偏", visible);                                 // 写的是实测口径……
            Assert.Contains("PSNR", visible);                                 // ……并且给出客观指标(不再与官方逐条对比)
            Assert.Contains("秒/帧", visible);                                // 悬停里给实测秒/帧(括号内只放速度档「快」)
            Assert.Contains("模型大小", visible);                              // 体积仍在提示里
            Assert.DoesNotContain("实验", visible);                           // **用户点名:不许出现"实验"**
            Assert.DoesNotContain("测试版", visible);
            Assert.DoesNotContain("最强", visible);                           // 不许夸大
            Assert.DoesNotContain("最好", visible);
            Assert.DoesNotContain("更好", visible);
            // ⚠ v3 是**有意的例外**:它提示里"实拍照片 ΔB 8.85 级偏蓝"是当下的实测短板(不是那句过时的 R/B 颠倒结论);
            //   所以只对非 v3 的项禁"偏蓝"。别的过时说法对**所有**项一律禁(下一行起)。
            if (!tag.Contains("game2x-v3", StringComparison.Ordinal)) Assert.DoesNotContain("偏蓝", visible);
            Assert.DoesNotContain("需重新导出", visible);
            Assert.DoesNotContain("不可用于正片", visible);
            // 【2026-09-19 用户点名】提示要"官方一点":不许出现"本机""你"这类口水词
            Assert.DoesNotContain("本机", visible);
            Assert.DoesNotContain("你", visible);
            // 下拉项显示文字必须与 Core 逐字一致(`类别 · 名字（括号里是速度档「快」）`)
            Assert.Contains("Text=\"" + AlhPro.Core.ExperimentalEsrgan.MenuText(tag) + "\"", item);
            // 可访问性:富文本 Content 的项 UIA 算出的 Name 是空的,读屏软件念不出选了什么 ⇒ 必须显式给名字
            // (UIA 名字 = 与界面上逐字相同的那串 + 小标文字;2026-09-16 起不再另给"1080p 口径"那种长式,
            //  因为用户定稿"括号内写快不写时间" —— 数字统一由悬停提示提供,读屏也能听到 ToolTip)
            Assert.Contains("AutomationProperties.Name=\"" + AlhPro.Core.ExperimentalEsrgan.MenuText(tag)
                + AlhPro.Core.ExperimentalEsrgan.Badge + "\"", item);

            // ③ XAML 里那行 ToolTip 必须与 Core.ExperimentalEsrgan.ToolTip **逐字相同**。
            // 【为什么钉逐字】提示词写了两处:下拉项里(XAML,声明式)与 Core(给"选中后下拉正下方那行提示"复用)。
            // 两处各写一份数字 = 迟早对不上(本仓库有前车之鉴:摘要印"轻量"、下拉写"快",用户以为两份说的是不同模型)。
            // XAML 里换行写成 &#x0a;(元素内容里的裸换行会被 XAML 折叠掉),所以比对前归一化成 \n。
            var m = System.Text.RegularExpressions.Regex.Match(item,
                "<ToolTipService.ToolTip>(.*?)</ToolTipService.ToolTip>",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            Assert.True(m.Success, $"{tag} 的下拉项没有内联 ToolTip 文本");
            Assert.Equal(AlhPro.Core.ExperimentalEsrgan.ToolTip(tag),
                m.Groups[1].Value.Replace("&#x0a;", "\n"));
        }

        // ④ 下拉整体的 ToolTip(收起状态悬停时显示的)也不许把这几支说成"实验模型"(注释同样先剥掉);
        //    并且必须用**新的叫法**(`游戏 · alhgame2x` / `现实 · alhreal2x`),不许留着「游戏向/现实向」那种老写法。
        int comboAt = xaml.IndexOf("x:Name=\"VideoEsrganModelCombo\"", StringComparison.Ordinal);
        var comboLine = System.Text.RegularExpressions.Regex.Replace(
            xaml.Substring(comboAt, xaml.IndexOf("SelectionChanged", comboAt, StringComparison.Ordinal) - comboAt),
            "<!--.*?-->", "", System.Text.RegularExpressions.RegexOptions.Singleline);
        foreach (var m in AlhPro.Core.ExperimentalEsrgan.All)
            Assert.Contains(AlhPro.Core.ExperimentalEsrgan.Label(m), comboLine);
        Assert.DoesNotContain("实验", comboLine);
        Assert.DoesNotContain("偏蓝", comboLine);            // 修好之后的过时结论
        Assert.DoesNotContain("需重新导出", comboLine);
    }

    /// <summary>**契约测试**:权重文件必须与 Tag 同名地存在(存在才检查,便于无模型的全新克隆)。
    /// 【为什么测】引擎按 `models/{Tag}.param` 取权重,Tag 与文件名对不上就是运行时"找不到模型"——
    /// 实测该引擎缺权重时**exit=0 不报错、只画坏帧**(2026-09-15:2x 权重配 -s 4 = 镜像平铺的错帧)。</summary>
    [Fact]
    public void Weight_files_match_the_tags_when_engines_are_present()
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
        foreach (var tag in AlhPro.Core.ExperimentalEsrgan.All)
        {
            // 【2026-09-20】自训四支已挪进**分支目录** `models\alhpro\`(用户要求"搞成一个新模型分支"),
            // 目录选择由 Core.EsrganModelDir 唯一决定 ⇒ 这里跟着它走,别再写死根目录(写死就会漏掉分支)。
            var dir_ = System.IO.Path.Combine(modelsDir,
                AlhPro.Core.EsrganModelDir.AlhPro.Replace("models\\", ""));
            Assert.True(System.IO.File.Exists(System.IO.Path.Combine(dir_, tag + ".param")),
                $"{dir_} 下缺少 {tag}.param(引擎按这个目录+名字取权重)");
            Assert.True(System.IO.File.Exists(System.IO.Path.Combine(dir_, tag + ".bin")),
                $"{dir_} 下缺少 {tag}.bin");
        }
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
        // 界面那份名字列表:第 6、7 项必须是自训的两支,且**从 Core 生成**(不手抄字面量)——
        // 手抄的那版曾写成"实验·real2x(2x·测试)",被用户当场纠正"不要写实验、括号里写速度";【2026-09-16】该口径进一步定死为档词「快」(不写秒/帧),见 ExperimentalEsrganTests。
        Assert.Contains("UpEsrganModelNames", cs);
        Assert.Contains("Core.ExperimentalEsrgan.SummaryText", cs);
        Assert.DoesNotContain("实验·real2x", cs);
        Assert.DoesNotContain("实验·game2x", cs);
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
