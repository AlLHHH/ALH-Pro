using System;
using System.IO;
using System.Linq;
using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>自训模型的**分支目录**(`models\alhpro\`)。
/// 【为什么必须钉住】这个引擎找不到模型时**不报错** —— 它会画一张坏帧、退出码仍是 0。
/// 所以"某条调用路径忘了改目录"这种疏漏不会有任何日志,只会静默出坏画面(本仓库已踩过两次同类事故)。
/// 【2026-10-14 大改】四支自训 2x 模型已按用户要求整体删除(用户:"还有那几个测试的模型可以删掉了")
/// ⇒ 分支目录的依据只剩还没训练完的 1x 修复(`alhpro-fix1x`,不进下拉)。
/// 这里做三件事:①目录选择逻辑;②**那四支已删的模型不许再映射到分支目录**(老预设里存着它们时,
/// 引擎会去一个不存在的目录找一个不存在的权重 —— 只出坏帧、不退错);③分支目录与根目录下都不再有那些权重。</summary>
public class EsrganModelDirTests
{
    [Theory]
    // 计划中的 1x 修复(尚未训练完,不进下拉):它是"自训"这一定义的唯一现存成员
    [InlineData("alhpro-fix1x", EsrganModelDir.AlhPro)]
    // 【已删除的四支 · 不许再指向分支目录】它们已经没有权重了,指向分支目录只会让引擎静默出坏帧
    [InlineData("alhpro-real2x", EsrganModelDir.Official)]
    [InlineData("alhpro-game2x", EsrganModelDir.Official)]
    [InlineData("alhpro-game2x-v2", EsrganModelDir.Official)]
    [InlineData("alhpro-game2x-v3", EsrganModelDir.Official)]
    [InlineData("realesr-animevideov3", EsrganModelDir.Official)]
    [InlineData("realesrgan-x4plus", EsrganModelDir.Official)]
    [InlineData("realesr-general-wdn-x4v3", EsrganModelDir.Official)]
    [InlineData("", EsrganModelDir.Official)]
    [InlineData(null, EsrganModelDir.Official)]
    public void Only_the_not_yet_trained_1x_model_lives_in_the_branch_directory(string? model, string expected)
        => Assert.Equal(expected, EsrganModelDir.For(model));

    /// <summary>【删除验收】四支自训 2x 的权重必须**两份都没有**:`models\alhpro\` 分支下没有,根目录下也没有
    /// (两份并存会各自漂移;留一份则是"删了但没删干净" —— 那是本次删除的验收点)。
    /// (无引擎/无模型的全新克隆上跳过 —— 与既有契约测试同一口径,不误报。)</summary>
    [Fact]
    public void Deleted_self_trained_weights_are_gone_from_both_directories()
    {
        string? modelsDir = null;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var cand = Path.Combine(dir.FullName, "engines", "realesrgan", "models");
            if (Directory.Exists(cand)) { modelsDir = cand; break; }
            dir = dir.Parent;
        }
        if (modelsDir is null) return;

        string branch = Path.Combine(modelsDir, "alhpro");
        foreach (var m in new[] { "alhpro-real2x", "alhpro-game2x", "alhpro-game2x-v2", "alhpro-game2x-v3" })
        {
            Assert.False(File.Exists(Path.Combine(branch, m + ".bin")), $"{branch} 下还留着 {m}.bin —— 已按用户要求删除");
            Assert.False(File.Exists(Path.Combine(branch, m + ".param")), $"{branch} 下还留着 {m}.param");
            Assert.False(File.Exists(Path.Combine(modelsDir, m + ".bin")), $"{modelsDir} 根目录下还留着 {m}.bin");
            Assert.False(File.Exists(Path.Combine(modelsDir, m + ".param")), $"{modelsDir} 根目录下还留着 {m}.param");
        }
        // 分支目录本身应当已经空了(整个目录随四支权重一起删除);1x 修复还没训练 ⇒ 不该有任何东西
        if (Directory.Exists(branch))
            Assert.Empty(Directory.GetFiles(branch));
    }

    /// <summary>【2026-10-14 独立复审 F1 的自动守卫】**已经发出去**的下拉项,按 <see cref="EsrganModelDir.For"/>
    /// 找到的那个目录里必须真有它的权重 —— 否则引擎"找不到权重、exit=0、只画坏帧"(本文件最上面那条)。
    /// 现在分支目录是空的 ⇒ 这条同时挡住"先把 `alhpro-fix1x` 接进下拉、权重却还没随包发"的接法;
    /// 等那支权重真落地,这条自动变成它的验收测试(权重不在就红)。
    /// 【权重命名】官方 `realesr-animevideov3` 的权重是**按倍率分文件**的(`-x2/-x3/-x4.param`),
    /// 没有裸 `<tag>.param` ⇒ 这里按"任一命名命中即算有"来判(与引擎 `"%s/%s-x%s.param"` 的拼法一致)。
    /// (无引擎/无模型的全新克隆上跳过 —— 与既有契约测试同一口径,不误报。)</summary>
    [Fact]
    public void Every_shipped_dropdown_tag_finds_its_weights_in_the_directory_the_engine_looks_at()
    {
        string? modelsDir = null;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var cand = Path.Combine(dir.FullName, "engines", "realesrgan", "models");
            if (Directory.Exists(cand)) { modelsDir = cand; break; }
            dir = dir.Parent;
        }
        if (modelsDir is null) return;

        // 界面下拉的真实 Tag(取法与 VideoModelOrderTests.Xaml_dropdown_order_matches_the_migration_table 相同)
        string? xamlPath = null;
        var uiDir = new DirectoryInfo(AppContext.BaseDirectory);
        while (uiDir != null)
        {
            var cand = Path.Combine(uiDir.FullName, "ImgUpscalerUI", "Views", "VideoView.xaml");
            if (File.Exists(cand)) { xamlPath = cand; break; }
            uiDir = uiDir.Parent;
        }
        Assert.NotNull(xamlPath);
        var xaml = File.ReadAllText(xamlPath!);
        int start = xaml.IndexOf("x:Name=\"VideoEsrganModelCombo\"", StringComparison.Ordinal);
        Assert.True(start > 0, "XAML 里找不到 VideoEsrganModelCombo");
        int end = xaml.IndexOf("</ComboBox>", start, StringComparison.Ordinal);
        var tags = System.Text.RegularExpressions.Regex
            .Matches(xaml[start..end], "Tag=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value).ToArray();
        Assert.NotEmpty(tags);

        foreach (var tag in tags)
        {
            if (tag == Anime4k.ModelTag) continue;   // 1x 修复走着色器:没有权重文件,也不经 ncnn
            string rel = EsrganModelDir.For(tag).Replace("models", "").TrimStart('\\');
            string target = rel.Length == 0 ? modelsDir : Path.Combine(modelsDir, rel);
            string[] candidates = new[] { tag + ".param" }
                .Concat(new[] { 2, 3, 4 }.Select(s => $"{tag}-x{s}.param"))
                .ToArray();
            Assert.True(candidates.Any(f => File.Exists(Path.Combine(target, f))),
                $"下拉项 {tag} 按 EsrganModelDir.For 会去 {target} 找权重,但那里没有 {string.Join(" / ", candidates)} —— 引擎会静默画坏帧");
        }
    }
}
