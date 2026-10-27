namespace AlhPro.Core;

/// <summary>本项目**自训** Real-ESRGAN 模型的登记表(权重住在独立分支目录 `models\alhpro\`,不走引擎内置 zoo)。
///
/// 【2026-10-14 用户定案:四支自训 2x 模型全部下线】
/// 用户原话:"还有那几个测试的模型可以删掉了";追问删除范围时选定「彻底去掉(删权重 + 下拉/Core/测试同步 + 1x 修复换官方模型)」。
/// 删掉的是 `alhpro-real2x`(现实向)、`alhpro-game2x`(游戏向 v1)、`alhpro-game2x-v2`、`alhpro-game2x-v3`:
///   · 权重文件(真实副本两处:`engines\realesrgan\models\alhpro\` 与 `发布版\engines\realesrgan\models\alhpro\`);
///   · 下拉里那三项与蓝色「测试」药丸;
///   · 本文件原先那一整套命名/悬停提示/实测文案(速度、PSNR、色偏、detail、`-s 3`/`-s 4` 的镜像平铺错帧);
///   · 只服务它们的三处 UI 接线(下拉下方提示、ONNX 换模型告知、自检报告里那一条)。
/// 老存档里存过的序号由 <see cref="VideoModelOrder"/> Rev12 显式换算(旧 5/6/7 → 0 = animevideov3、旧 8 → 5 = anime4k),
/// 不靠越界兜底。作废的实测数据需要时查 git 历史(删除前那一版本文件)。
///
/// 【现在这里只剩「1x 修复」那一支的位置】<see cref="Fix1x"/>,**计划中、尚未训练完**
/// ⇒ **不进任何下拉**(只有训练/导出/验收都完成后才允许登记)。它的用途是"同尺寸修复"(不放大):
/// 实测「2x 超分 + 缩回 1x」会让画面**比原片更软**(素材(13)第 20 秒:输出 detail 822 / 边宽 5.86px,原片 1152 / 7.02px)
/// ⇒ 1x 想要"更清晰"必须换网络。这正是同类工具 的做法(它的 1x 用 Anime4K 着色器在原分辨率做修复)。
/// 【网络】与 2x 同族(SRVGGNetCompact 主干,从官方权重继承),**尾层 3 通道、倍率写死 1** ⇒ `-s 1` 是原生用法。
///
/// 【1x 回退路径不再用自训模型】Anime4K 着色器不可用时的回退(见 <see cref="Upscale1x.RealEngineModel"/>)
/// 改用官方 `realesr-animevideov3` —— 它是官方 realesrgan 里唯一带**原生 2x** 权重的模型
/// (general-x4v3 / wdn-x4v3 / x4plus / x4plus-anime 都只有 4x 权重)。回退语义("按 2x 超分后缩回原尺寸")
/// 和原来一模一样,只是不再依赖自训权重。</summary>
public static class ExperimentalEsrgan
{
    /// <summary>**1x 修复模型**(同尺寸:去压缩痕 + 边缘恢复,不放大)。
    /// 【为什么需要】用户明确要求"1x 缩放必须存在";而实测「2x 超分 + 缩回 1x」会让画面**比原片更软**
    /// (素材(13)第 20 秒:输出 detail 822 / 边宽 5.86px,原片 1152 / 7.02px)⇒ 1x 想要"更清晰"必须换网络。
    /// 【网络】与 2x 同族(SRVGGNetCompact 主干,从官方权重继承),**尾层 3 通道、倍率写死 1** ⇒ `-s 1` 是原生用法。
    /// 【状态】2026-09-20 训练中(训练 tag = fix1)。**权重 + 验收数据齐了再登记进下拉**。</summary>
    public const string Fix1x = "alhpro-fix1x";

    /// <summary>是不是「1x 修复(同尺寸)」那一支 ⇒ <see cref="EngineScalePolicy"/> 据此让它走原生 `-s 1`(倍率 1、不缩放)。</summary>
    public static bool Is1xModel(string? model)
        => !string.IsNullOrEmpty(model) && model.Contains(Fix1x, StringComparison.OrdinalIgnoreCase);

    /// <summary>是不是本项目自训的模型(权重住独立分支目录,不是引擎内置 zoo)。
    /// 【为什么单独留一个判据】<see cref="EsrganModelDir.For"/> 靠它选目录,而这个引擎**找不到权重时不报错**:
    /// 它会画一张坏帧、退出码 0 ⇒ 目录选错是**静默故障**,判据只能有一处、由单测钉住。
    /// 2026-10-14 四支自训 2x 下线后,这里只剩计划中的 <see cref="Fix1x"/> 一支。</summary>
    public static bool IsSelfTrained(string? model) => Is1xModel(model);
}
