namespace AlhPro.Core;

/// <summary>1x 修复档(不变尺寸)用的 **Anime4K 着色器**(纯逻辑:只负责拼滤镜串与路径约定,可单测)。
///
/// 【它解决什么问题】原来的「1x 超分」是"按 2x 跑再缩回源尺寸" —— 我们自己早前的实测已经判定这是**错的用法**
/// (缩回把细节又缩掉:源细节 1176 → 818,比原片还软),而那正是 Video2X 等同类软件**根本不提供**这种组合的原因。
/// Anime4K 走的是另一条路:在**原分辨率**上做修复 + 锐化,不放大、不缩回。
///
/// 【为什么用得起(2026-09-21 实测,推翻了上一轮的判断)】
///   · 我们自带的 ffmpeg(n7.1)就带 `libplacebo` 滤镜与 `custom_shader_path` 参数,Vulkan 也正常
///     —— 上一轮报的 "Failed initializing vulkan device" 是**瞬时故障**,复现不出来(连跑 3 次全过);
///   · 输出与 Video2X 自带的 Anime4K 逐帧 **44.4 dB**(同一处理,差的只是它走 ffv1/yuv、我们走 PNG/RGB);
///   · 在用户自己的 1080p 片子上:边宽 6.76 → **5.96**(更锐)、纹理 detail 不丢(100%)、
///     与源细节层相关 0.909(是"增强原有细节"而不是编)、代价是**平坦区噪声 1.74×**(干净素材才建议开);
///   · 速度:Video2X 同 GPU 基准约 15 FPS(Video2X 的 -b 模式),我们带 PNG/JPG 落地时瓶颈在 I/O;
///   · 着色器文件本身是 **MIT License(Copyright (c) 2019-2021 bloc97)**,文件头自带许可声明 ⇒ 可随包分发。
///
/// 【路径规则是坑,别改】**`custom_shader_path` 是滤镜参数值,而 ffmpeg 滤镜参数里冒号是选项分隔符** ——
/// 所以:① 不能用绝对路径(`D:` 的冒号会被当成选项分隔)、② 不能用反斜杠(滤镜里是转义字符)。
/// ⇒ 约定:**以 ffmpeg 所在目录为工作目录,只传文件名**(见 <see cref="FilterArgument"/>)。
/// 一开始写成绝对路径时,报的是 "No option name near '\Video2X Qt6\...'(或 /Video2X Qt6/...)" —— 看着像路径不存在,
/// 其实是解析错误。</summary>
public static class Anime4k
{
    /// <summary>随包的着色器文件名(放在 `engines\ffmpeg\shaders\` 下)。Anime4K v4-a = 修复+锐化的通用档。</summary>
    public const string ShaderFileName = "anime4k-v4-a.glsl";

    /// <summary>着色器在 ffmpeg 目录下的相对位置(报告/日志用;真正下发给 ffmpeg 的只有文件名)。</summary>
    public const string ShaderRelativePath = "shaders/anime4k-v4-a.glsl";

    /// <summary>启动时探测这条链能不能跑(要拿真引擎跑一次;见 UI 侧的 Anime4kProbe)。
    /// 【为什么要探】没有 Vulkan 的机器上 libplacebo 会初始化失败 —— 那种机器必须**回退**到旧行为而不是整批失败。</summary>
    public const string ProbeFilter = "libplacebo=custom_shader_path=" + ShaderFileName + ":w=64:h=64";

    /// <summary>下发给 ffmpeg 的滤镜串(`libplacebo=custom_shader_path=<文件名>`)。
    /// ⚠ 只有**文件名**、没有目录:调用方必须把 ffmpeg 的工作目录设成它自己的目录(见类注释的路径规则)。</summary>
    public static string FilterArgument() => "libplacebo=custom_shader_path=" + ShaderFileName;

    /// <summary>日志/界面里那句话(不带路径,避免又变成"看着像找不到文件")。</summary>
    public const string DisplayName = "Anime4K v4-a";

    /// <summary>**模型下拉里的 Tag**(Rev10 起它作为"1x 修复"条目正式挂在超分模型下拉里)。
    /// 它不是超分权重、也没有 .param 文件 —— 引擎侧永远不该拿它去找权重(所以"权重缺失"自检必须放过它)。
    /// 选到它时(且倍率 = 1x):① 超分阶段跳过;② 合帧滤镜链里挂上 Anime4K 那条滤镜。
    /// ⚠ 它在 **2x/3x/4x** 下会被界面**隐藏**(不放大 ⇒ 选它没有意义);倍率切到 1x 时才会出现。
    /// ⚠ 2026-09-21 自审修正:这段描述原先还写着"会自动把放大倍数切到 1x"(那是 Rev8"锁定牌"时代的做法),
    ///   现在由「按倍率决定哪些模型可选」负责 —— 文案与实现不一致就是骗用户,已同步(有单测钉住 XAML 与 Core 逐字一致)。</summary>
    public const string ModelTag = "anime4k";

    /// <summary>下拉项文字(与 XAML 里那一项逐字一致,有单测钉住;风格沿用 `类别 · 名字（速度档）`)。</summary>
    public const string MenuText = "动漫 · Anime4K 修复（快）";

    /// <summary>下拉项悬停提示(与 XAML 逐字一致,有单测钉住)。**改这里必须同时改 XAML**。</summary>
    public const string Tooltip =
        "Anime4K:开源着色器(MIT),在**原分辨率**上做修复 + 锐化,**不放大**(1x)。&#x0a;" +
        "实测(1080p 同帧):边缘过渡宽度 6.76 → 5.96px(更锐)、纹理量不丢、与源的细节相关性 0.909(增强原有细节,不是编出来);" +
        "代价是平坦区噪声 1.74×,**脏素材慎用**。&#x0a;" +
        "速度最快(实测约 15 FPS@1080p);需要可用的 Vulkan 显卡,探测不过会自动退回「现实 · 1x 修复」并写日志。";
}
