using System;
using System.IO;
using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>自训模型的**分支目录**(`models\alhpro\`)。
/// 【为什么必须钉住】这个引擎找不到模型时**不报错** —— 它会画一张坏帧、退出码仍是 0。
/// 所以"某条调用路径忘了改目录"这种疏漏不会有任何日志,只会静默出坏画面(本仓库已踩过两次同类事故)。
/// 这里做两件事:①目录选择逻辑;②**四支自训权重在分支目录下真的存在、且根目录下不再有副本**
/// (后者是"分支是真的"这条承诺的验收:两份并存会各自漂移,只留一份才不会)。</summary>
public class EsrganModelDirTests
{
    [Theory]
    [InlineData("alhpro-real2x", EsrganModelDir.AlhPro)]
    [InlineData("alhpro-game2x", EsrganModelDir.AlhPro)]
    [InlineData("alhpro-game2x-v2", EsrganModelDir.AlhPro)]
    [InlineData("alhpro-game2x-v3", EsrganModelDir.AlhPro)]
    [InlineData("realesr-animevideov3", EsrganModelDir.Official)]
    [InlineData("realesrgan-x4plus", EsrganModelDir.Official)]
    [InlineData("realesr-general-wdn-x4v3", EsrganModelDir.Official)]
    [InlineData("", EsrganModelDir.Official)]
    [InlineData(null, EsrganModelDir.Official)]
    public void Self_trained_models_live_in_the_branch_directory(string? model, string expected)
        => Assert.Equal(expected, EsrganModelDir.For(model));

    /// <summary>分支必须"名副其实":四支权重都在 `models\alhpro\` 下,根目录里**没有**同名副本。
    /// (无引擎/无模型的全新克隆上跳过 —— 与既有契约测试同一口径,不误报。)</summary>
    [Fact]
    public void Branch_weights_exist_and_are_not_duplicated_in_the_root()
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
        foreach (var m in ExperimentalEsrgan.All)
        {
            Assert.True(File.Exists(Path.Combine(branch, m + ".bin")),
                $"{branch} 下缺少 {m}.bin —— 自训模型必须放在分支目录里(引擎按 -m 目录 + -n 名字取权重)");
            Assert.True(File.Exists(Path.Combine(branch, m + ".param")), $"{branch} 下缺少 {m}.param");
            Assert.False(File.Exists(Path.Combine(modelsDir, m + ".bin")),
                $"{modelsDir} 根目录下还留着 {m}.bin 副本 —— 分支要么真、要么假,两份并存会各自漂移");
        }
    }
}
