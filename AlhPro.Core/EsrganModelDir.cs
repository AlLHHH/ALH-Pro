namespace AlhPro.Core;

/// <summary>Real-ESRGAN 引擎的**模型目录选择**(纯逻辑,可单测)。
/// 【2026-09-20 用户要求"把 alhpro 模型搞成一个新模型分支"】
/// 官方权重留在 `models\`,本项目自训的四支(real2x / game2x / game2x-v2 / game2x-v3)挪进
/// **独立子目录 `models\alhpro\`** —— 这就是"分支"在文件系统上的真实形态 ✓。
///
/// 【为什么值得单独抽一个函数,而不是在 4 处调用点各写一次三元表达式】
/// 这个引擎**不会因为找不到模型而报错**:它会"画一张坏帧、退出码 0"。
/// 也就是说,漏改任意一处调用点(比如图片页那条路径)都不会有任何报错,只会**静默出坏画面** ——
/// 本仓库已经踩过两次同类事故(`-s 4` 配 2x 权重 = 镜像平铺错帧;相对路径 `-m` = 跑成别的模型)。
/// 所以目录选择必须是**唯一来源**、由单测钉住,并且顺带断言"分支目录下四支权重都真的存在"。
///
/// 【迁移口径】老版本把四支权重放在 `models\` 根下;挪走之后**根下不再保留副本**(否则分支是假的、
/// 两份还会各自漂移)。真正需要担心的是"某条路径忘了改 ⇒ 静默坏帧",这一点由 <see cref="EsrganModelDirTests"/> 兜住。</summary>
public static class EsrganModelDir
{
    /// <summary>官方模型目录(引擎内置的 model zoo)。</summary>
    public const string Official = "models";

    /// <summary>本项目自训模型的目录(分支)。</summary>
    public const string AlhPro = @"models\alhpro";

    /// <summary>该模型该去哪个目录取权重。自训四支走 <see cref="AlhPro"/>,其余走 <see cref="Official"/>。</summary>
    public static string For(string? model)
        => ExperimentalEsrgan.IsExperimental(model) ? AlhPro : Official;
}
