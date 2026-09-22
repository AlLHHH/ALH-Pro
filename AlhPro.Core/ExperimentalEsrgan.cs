namespace AlhPro.Core;

/// <summary>两支**自训**的 Real-ESRGAN 2x 模型:**游戏向**(<see cref="Game2x"/>)与**现实向**(<see cref="Real2x"/>)。
///
/// 【命名 · 2026-09-21 用户第四次定稿:名字前面加 `alh`】**不得出现"实验/实验性"字样**;
/// 下拉项文字**跟已有那几项同一个格式** —— `类别 · 名字（括号内速度）`
/// (已有项:`动漫 · animevideov3（快）` / `通用 · x4plus（超慢）`),
/// 所以四项写成 <see cref="MenuText"/> = `现实 · alhreal2x（快）` / `游戏 · alhgame2x（快）` /
/// `游戏 · alhgame2x-v2（快）` / `游戏 · alhgame2x-v3（快）`
/// (用户原话:"改名字 前面加上alh";写法示例由用户给出 —— `现实 alhreal2x` 这样,**alh 与名字连写、中间不加连字符**)。
/// **类别词保留中文**(现实 / 游戏),`·` 后面那个"名字"是英文并在前面加项目前缀 `alh`
/// (<see cref="NamePrefix"/>) —— 不用看提示就知道这是本项目自训的,而不是官方 `realesr-*` 那几支。
/// 权重文件名与下拉项的 `Tag` 仍是 `alhpro-*`,**一个字都没动**(引擎按 Tag 取权重,改名只动界面文字,不动任何功能)。
/// 【改名连带的一件事 · 2026-09-21:药丸先撤后放】名字变长约 27px 后,末尾那个蓝色「测试」药丸一度放不下
/// (收起状态只剩约 18px、药丸要 36px ⇒ 会被下拉箭头裁),当天先撤掉、用 `alh` 前缀当标记;
/// 用户看到后问"测试标记呢",并在三个方案里选了**加宽左栏 22px**(300 → 322)⇒ **药丸原样放回**。
/// 像素级实测与算式见 <see cref="MenuName"/>,XAML 文件头与对应单测里都有对应的钉子。
/// 【括号内容 · 2026-09-16 用户定稿】"括号内要写快 而不是时间" ——
/// 括号里放的是**速度档词**(与官方项同一套:`快` / `中` / `超慢`),即 <see cref="SpeedTier"/>;
/// 实测的秒/帧数字(<see cref="SpeedText"/>)挪到悬停提示与下拉下方那行提示里(那两处本来就是写数字的地方)。
/// 代码里的**类名/文件名**沿用 `ExperimentalEsrgan`(纯内部标识、不进界面):改名只图好看,
/// 却要动 Core/UI/Tests 三处引用,按用户"不要扩大改动面"的要求保持不动。
///
/// 【与官方那几支的三处本质区别(界面都要如实体现)】
///   ① 只有 ncnn 权重(pth→ncnn 导出),**没有 ONNX** —— 走到「稳定引擎(ONNX)」那条路时不可能用它,
///      必须**明说**"已按别的模型处理"(见 <see cref="OnnxFallbackNotice"/>);
///   ② 只有 2x 原生权重 —— 3x/4x 目标必须按原生 2x 跑再缩回(见 <see cref="EngineScalePolicy.IsX2OnlyModel"/>:
///      给 2x 权重下发 -s 3/-s 4 会输出**镜像平铺的错帧**,exit=0 不报错);
///   ③ 实测**并非全面强于官方** —— 目标域色偏更准、细节与锐度更弱(数字见下),所以括号里给速度档(「快」)、
///      悬停里给事实数字,不写成"更强/推荐"。
///
/// 【实测速度 · 2026-09-15 同批实测,与成本表(PipelineOrderPlan)同口径】
///   口径:40 帧目录批跑(素材 = `D:\deep\_batchrun\in\clip1080p_300f.mp4` 抽帧,1920×1080 PNG)、
///         `-s 2 -t 0 -g 0 -j 1:1:1 -m models -f jpg`(**与 App 视频路径同格式**,视频路径就是 `-f jpg`)、
///         **冷态**(每次全新引擎进程、GPU 空闲 8 秒后再跑),秒/帧 = 整进程墙钟 ÷ 出图帧数。
///   实测(每个模型 3 次冷跑):
///     · alhpro-real2x  0.351 / 0.376 / 0.401(均值 0.376)
///     · alhpro-game2x  0.351 / 0.426 / 0.326(均值 0.368)
///     · 同批对照 realesr-animevideov3 0.351 / 0.351 / 0.376(均值 0.359)
///   ⇒ 两支都在 **0.35 秒/帧** 一档(与官方 animevideov3 同档;两支自身同架构同体积,测不出差异)。
///   界面写 **0.35**(取多次冷跑里最稳的那一档=14.03s/40 帧,**不写 0.376/0.368 这种假精度**)。
///   ⚠ 成本表里官方 animevideov3 2x 记的是 0.252~0.269,比本次同口径实测快约 1.3 倍 ——
///     笔记本 GPU 热降频/室温差异(仓库文档也记过 x4plus 样本 10.19~19.05 的大波动)。
///     所以**界面数字一律用本次同批实测值**,绝不把成本表里官方模型的数字搬来当自训模型的成绩。
///   (2026-09-15 收尾复核:软件端到端跑同一支模型,超分计量 23 帧 / 6.4s = 280 ms/帧,同档。)
///
/// 【倍率真相 · 2026-09-15 实测,用户问"2x 是什么、只支持 2x 输出吗"】
///   这两个网络的**输出倍数是写死在图里的 2.0**(param 末尾 `Interp upsample_nearest_36 … 1=2.0 2=2.0`):
///   它天生就是 2 倍放大,不是"只能出 2x 成片"。软件里的 2x/3x/4x 是**目标输出尺寸**,两者关系:
///   · 目标 2x → 引擎按原生 2x 跑一遍,不缩放(最干净、最快);
///   · 目标 3x/4x → 本软件仍按原生 2x 跑引擎(`EngineScalePolicy`),**再把成片缩放到目标尺寸**
///     (EngineService 的 `ratio = scale / engineScale` 那一步)—— **不是把引擎跑两遍**;
///     实测同一段 24 帧素材:超分阶段 2x = 2.0s、3x = 3.9s、4x = 4.7s(官方 animevideov3 有原生
///     3x/4x 权重,同样条件只要 2.2 / 2.3 / 2.8s)⇒ 3x/4x 更慢,但**清晰度并不会真的变成 3x/4x 细节**。
///   · ⚠ 若**绕过软件**直接给引擎下发 `-s 3` / `-s 4`:输出尺寸变成 3x/4x,但画面是**平铺重复的错帧**
///     (实测 1080p 单帧:`-s 3` → 5760×3240、`-s 4` → 7680×4320,exit=0、零报错,图见 `_probe\rawscale\`;
///     与"2x 成图再放大"的 PSNR 只有 7.27 dB)。软件里已由 `EngineScalePolicy` 强制按 2x 跑,正常操作碰不到。
///   ⇒ 结论(ToolTip 里据实写明):**推荐用 2x**;3x/4x 能用、尺寸正确、不报错,但更慢且画质≈2x 放大。
///
/// 【R/B 缺陷 · 2026-09-15 **已修好** —— 这段文字过去写的是"整幅偏蓝、需重新导出",现已作废】
///   旧(坏)导出:同帧三通道均值 源帧 R143/G110/B92 → 现实向 R94/G111/B137、游戏向 R100/G110/B131,
///   即 R/B 通道颠倒、画面整幅偏蓝;根因在训练侧数据口径(LR 的 R/B 被换过而配对的 HR 仍是 RGB)。
///   本轮**重训 + 重新导出**后,`eval_variant.py` 里的**通道验收闸**已通过(判据:每通道差 <5,
///   且 |ΔR|/|ΔB| ≥20 同时符号相反 ⇒ 判颠倒):
///     · 现实向:源 R112.1/G98.8/B93.3 → 输出 R112.3/G99.2/B93.6(Δ **+0.20 / +0.34 / +0.24**);
///     · 游戏向:Δ **+0.11 / +0.12 / +0.18**(原始 4 位小数 +0.1114 / +0.1245 / +0.1762);
///     · 多宽度自检 1920/2048/2348/2560 全过(尺寸 2×、无黑帧、通道闸 PASS)。
///   ⇒ 界面文案里**不得再出现"偏蓝 / R-B 颠倒 / 需重新导出 / 不可用于正片"**这类已经过时的说法。
///
/// 【色偏 / detail 实测 · 2026-09-15 修好后的权重,口径见下】
///   工具:`D:\deep\_train\chroma_test_card.py`(读 `_train\evalv\` 下引擎 2x 输出,在桌面出对照卡)。
///   口径:源 1920×1080,「色偏」= 与"源图最近邻 2x"的**逐通道均值最大差**(越小越准);
///         「detail」= 平均 |拉普拉斯|(纹理量)。三档素材:游戏帧 / 动漫帧 / 实拍(密集树叶)。
///     游戏向:色偏 游戏帧 0.18(官方 2.00)、动漫帧 0.04(官方 2.73)、实拍 2.53(官方 6.34)
///             detail 游戏帧 2.36(官方 2.91)、动漫帧 1.55(官方 2.06)、实拍 59.97(官方 73.66)
///     现实向:色偏 游戏帧 0.34(官方 2.00)、动漫帧 0.18(官方 2.73)、实拍 1.48(官方 6.34)
///             detail 游戏帧 3.20(官方 2.91)、动漫帧 1.83(官方 2.06)、实拍 59.13(官方 73.66)
///   ⇒ 如实结论:**三档色偏都低于官方**(两支都对得上);detail 多数低于官方(现实向在游戏帧上略高)。
///     不写"更强/最强",也不挑对自己有利的那一格单独摆出来。</summary>
public static class ExperimentalEsrgan
{
    /// <summary>现实向自训 2x(实拍素材色偏更准)。</summary>
    public const string Real2x = "alhpro-real2x";

    /// <summary>游戏向自训 2x(游戏素材色偏更准)。</summary>
    public const string Game2x = "alhpro-game2x";

    /// <summary>游戏向自训 2x · **v2**(2026-09-19:同一套原生 2x 配方,训练数据换成用户自己的 26 段录像;
    /// v1 只用了一段 30 秒录像 = 138 张)。**不替换 v1**,下拉末尾追加。
    ///
    /// 【实测 · 留出视频 4 段 × 2 帧(素材21/22/25/26,训练集里没有),540p→1080p,与 v1 同帧同输入】
    ///   本支:PSNR **31.80** / SSIM **0.9767** / 边缘 PSNR **21.69** / 边缘 SSIM **0.8957** / 斜边锯齿代理 **50.82**
    ///   v1  :31.38 / 0.9681 / 21.24 / 0.8889 / 51.23 ⇒ **四项全面超 v1,锯齿还略好**
    ///   (同一台上 bicubic 30.32/0.9621/20.13、官方 animevideov3 30.10/0.9639/20.46)
    ///
    /// 【实测 · 仓库三档卡片口径(色偏 = 引擎 2x 输出 vs 源的逐通道均值最大差,判据 小于 5;
    ///   detail = 平均 |拉普拉斯|,越高纹理越多)】
    ///   游戏帧 色偏 +0.21/+0.46/+0.24、detail **3.39**(v1 2.52、官方 2.91)
    ///   动漫帧 色偏 +0.17/+0.19/+0.08、detail **2.19**(v1 1.75、官方 2.06)
    ///   实拍   色偏 -0.17/+0.54/+2.00、detail **70.78**(v1 63.82、官方 73.66)
    ///   ⇒ 三档色偏最大 2.00 级(远低于官方 6.34)⇒ 颜色与源一致;detail 三档都高于 v1。
    ///   ⇒ 通道验收(多宽度 1920/2048/2348/2560)尺寸 ✅ 非黑 ✅ 通道闸 PASS。
    /// 【体积/速度】与 v1 同架构同层数 ⇒ 权重 2.29 MB + param 2 KB = 2.4 MB,速度同档(实测未单独计,见速度那段的说明)。</summary>
    public const string Game2xV2 = "alhpro-game2x-v2";

    /// <summary>游戏向自训 2x 第三代「锐化优先」(2026-09-20 训成,训练 tag=game3)。
    /// 【分工】v1 = 第一代(轻、软);v2 = 最保真最干净;v3 = **最锐 + 锯齿最顺滑**(保真低于 v2、高于 v1)。
    /// 【留出视频实测 · 12 用例(每段留出视频 3 帧,540p→1080p,与 v1/v2 同帧同口径)】
    ///   v3:PSNR 31.99 · SSIM 0.9533 · 边缘 PSNR 22.10 · 边缘 SSIM 0.9149 · **detail 760.1** · **jag 50.91**
    ///   v2:32.67 / 0.9544 / 22.67 / 0.9211 / 672.0 / 51.65    v1:31.62 / 0.9475 / 21.56 / 0.8983 / 288.4 / 51.12
    ///   ⇒ **detail 与 jag 都是三支里最好的**;保真让位是**设计如此**(配方把像素项降到 0.5、感知项抬到 2.0、加边缘强度项 3.0)。
    /// 【⚠ 已知短板 —— 必须写进提示、不许藏】①**实拍照片偏蓝**:卡片口径 ΔB **+8.85 级**(判据 小于 5),游戏/动漫帧正常
    ///   (0.49 / 0.13);②**平坦区噪声 1.42×**(v2 1.18×)⇒ 干净素材颗粒感略强;③边缘"更有力"但**宽度未变窄**
    ///   (540p→1080p 边宽 4.93px:v2 5.04、最锐的官方 x4plus-anime 4.09)。
    /// 【配方】`python train_v2.py --variant game --data D:\deep\_train\data_game2 --tag game3 --scale 2
    ///   --minutes 150 --w-pix 0.5 --w-per 2.0 --w-edge 3.0` ⇒ it 19458 / 150 分钟;
    ///   val(保真)按设计变差:起点 0.0202 → 0.0331;锯齿代理 0.5161 对双三次 0.6411(改善 19.5%)。
    /// 【导出验收】多宽度 1920/2048/2348/2560 尺寸 ✅ 非黑 ✅ 通道闸 PASS(ΔR+0.16/ΔG+0.27/ΔB+0.49)。</summary>
    public const string Game2xV3 = "alhpro-game2x-v3";

    /// <summary>下拉里追加的自训模型(顺序 = VideoModelOrder 末尾几位;**Rev7 起 = 3 支**)。
    /// 【2026-09-21 用户:"不要训练那么多模型了 我要留两个最好的游戏 1个现实就行 后面慢慢优化"】
    ///   ⇒ 留 **v2(最保真)+ v3(最锐)+ real2x**;v1(`alhpro-game2x`)从下拉移除,见 <see cref="Retired"/>。
    ///   依据(实测):v1 是三支游戏向里最弱的一支 —— detail 288 对 v2 672 / v3 760,PSNR 31.62 对 32.67 / 31.99,
    ///   而且它只用了一段 138 帧的录像训练(v2 用了 26 段 942 帧)。
    /// ⚠ **1x 修复模型不在这里** —— 只有训练/导出/验收都完成后才允许进下拉(否则用户会选到不存在的模型)。</summary>
    public static readonly string[] All = { Real2x, Game2xV2, Game2xV3 };

    /// <summary>**已从下拉移除、但必须继续被认识**的自训模型(2026-09-21 退休 v1)。
    /// 【为什么退休了还要认它】两条硬理由:
    ///   ① 权重目录选择靠 <see cref="IsExperimental"/> —— 它住在分支目录 `models\alhpro\`,
    ///      一旦不认它,<see cref="EsrganModelDir.For"/> 就会去根目录找 `alhpro-game2x.param`,
    ///      而引擎**找不到权重时 exit=0、只出坏帧**(本仓库踩过这个坑),所以这里必须继续认;
    ///   ② 老预设/老参数行里可能还带着这支 id(序号会被 <see cref="VideoModelOrder"/> 换算到 v2,
    ///      但换算只发生在"读设置"那条路上),日志与提示仍要能正确印出它的名字。
    /// ⇒ 权重文件**保留在 `models\alhpro\`**(2.4 MB,删了反而会让上面两条变成静默坏帧)。</summary>
    public static readonly string[] Retired = { Game2x };

    /// <summary>**1x 修复模型**(同尺寸:去压缩痕 + 边缘恢复,不放大)。
    /// 【为什么需要】用户明确要求"1x 缩放必须存在";而实测「2x 超分 + 缩回 1x」会让画面**比原片更软**
    /// (素材(13)第 20 秒:输出 detail 822 / 边宽 5.86px,原片 1152 / 7.02px)⇒ 1x 想要"更清晰"必须换网络。
    /// 这也正是 Video2X 的做法:它的 1x 用 Anime4K 着色器在原分辨率做修复(实测 detail 1132 / 边宽 4.92px)。
    /// 【网络】与 2x 同族(SRVGGNetCompact 主干,从官方权重继承),**尾层 3 通道、倍率写死 1** ⇒ `-s 1` 是原生用法。
    /// 【状态】2026-09-20 训练中(训练 tag = fix1)。**权重 + 验收数据齐了再登记进 <see cref="All"/>**。</summary>
    public const string Fix1x = "alhpro-fix1x";

    /// <summary>是不是「1x 修复(同尺寸)」那一支 ⇒ <see cref="EngineScalePolicy"/> 据此让它走原生 `-s 1`(倍率 1、不缩放)。</summary>
    public static bool Is1xModel(string? model)
        => !string.IsNullOrEmpty(model) && model.Contains(Fix1x, StringComparison.OrdinalIgnoreCase);

    /// <summary>蓝色小标里的字。【2026-09-15 用户:先保留「测试」】
    /// 【2026-09-21 撤掉过一天又放回 —— 来龙去脉】四项自训模型的名字加了 `alh` 前缀后,收起状态文字右侧只剩约 18px,
    /// 而药丸要 36px(6 间距 + 30)⇒ 当天先撤了药丸(用 `alh` 当标记);用户看到后问"测试标记呢",
    /// 在三个方案(改类别词 / 换更小标记 / 加宽面板)里**选了加宽左栏**:`VideoView.xaml` 左栏 300 → 322,
    /// 余量 18 → 40px ⇒ 药丸原样放回(`VideoView.xaml` 文件头有像素级实测)。
    /// ⇒ 这个常量现在是**在用**的:XAML 里四个自训项的 AutomationProperties.Name = <see cref="MenuText"/> + 它。</summary>
    public const string Badge = "测试";

    /// <summary>括号里那个**速度档**「快」——【用户 2026-09-16 定稿:"新加的两个模型括号内要写快 而不是时间"】。
    /// 与官方项逐字同一套词(`动漫 · animevideov3（快）` / `通用 · general-x4v3（快）` / `通用 · x4plus（超慢）`)。
    /// 用在**所有「模型名 + 括号」的地方**:下拉项(含收起状态)、UIA 名字、预设摘要、参数日志行。
    /// 【为什么不是秒/帧】括号的位置放的是**快慢档形容词**(与已有各项同一格式);实测数字另由
    /// <see cref="SpeedText"/> 提供,放在悬停提示与下拉下方那行提示里(那两处本来就是写数字的地方)。
    /// 【顺带的好处】`现实 · alhreal2x（快）` 比 `（0.35s/帧）` 短,收起状态离可用宽度上限更远(实测见 <see cref="MenuName"/>)。</summary>
    public const string SpeedTier = "快";

    /// <summary>实测速度的**事实数字**(两支同档,见类注释的实测表):只写"约 0.35",
    /// 不写 0.368/0.376 —— 三次冷跑的波动就有 ±0.04,写更细是假精度。
    /// 【只用在陈述句里】悬停提示的「速度:…」与下拉下方那行提示;不再进括号(括号里是 <see cref="SpeedTier"/>)。</summary>
    public const string SpeedText = "1080p 约 0.35 秒/帧";

    /// <summary>下拉项里 `·` 后面那个"名字"部分(**按模型给**):`alhreal2x` / `alhgame2x` / `alhgame2x-v2` / `alhgame2x-v3`。
    /// 【用户 2026-09-21 第四次定稿:名字前面加 `alh`】原话"改名字 前面加上alh",
    /// 写法示例由用户给出:<see cref="NamePrefix"/> 与名字**连写**、中间不加连字符。
    /// 名字本体仍是英文(已有项那里放的就是英文模型名 `animevideov3` / `x4plus`…)。
    /// 【与权重文件名的关系】权重文件与引擎 Tag 是 `alhpro-real2x` / `alhpro-game2x`(**没改**),
    /// 界面上的 `alh` 是它的简称 —— 少了 `pro` 三个字符,是为了下面这条宽度限制。
    /// 【宽度实测 · 2026-09-21 真机(UIA 取实际渲染矩形 + 截图像素,不是估算)】
    ///   收起状态(组合框 235px):内容起点 x=16(蓝条)→ 文字 x=25..183 → 下拉箭头 x=207..214
    ///     ⇒ 文字右侧只剩 **约 18px**;而末尾那个「测试」药丸要 6(间距)+30 = **36px** ⇒ 放不下(会被箭头裁).
    ///   实测文字宽(UIA 渲染值;`现实 · alhreal2x（快）` 由界面字体 14px 量出后按同字体校准):
    ///     `现实 · alhreal2x（快）`        文字 ≈ 136px
    ///     `游戏 · alhgame2x（快）`        文字 ≈ 148px
    ///     `游戏 · alhgame2x-v2 / -v3（快）` 文字 ≈ **168px**(最长,占掉大部分收起宽度)
    ///   ⇒ 【裁决 · 用户 2026-09-21】**左栏 300 → 322px**(用户在三方案里选的):余量 18 → 40px,药丸放回仍有余量。
    ///     ⚠ 展开的下拉与收起状态**不是一回事**:展开态的下拉项**按内容自动变宽**
    ///     (实测:内容 177px → 项宽 201px;内容 195px → 项宽 219px),
    ///     所以"展开态没被裁"不能推出"收起态安全"—— 量宽度一律量收起状态。</summary>
    public static string MenuName(string? model)
        => NamePrefix + (IsV3(model) ? "game2x-v3" : IsV2(model) ? "game2x-v2" : (IsGame(model) ? "game2x" : "real2x"));

    /// <summary>名字前面的项目前缀(用户 2026-09-21 定稿:"改名字 前面加上alh")。
    /// 四支自训模型在下拉里因此长这样:`现实 · alhreal2x（快）` / `游戏 · alhgame2x（快）` /
    /// `游戏 · alhgame2x-v2（快）` / `游戏 · alhgame2x-v3（快）`。
    /// 【为什么连写、不带连字符】用户给的写法就是 `alhreal2x` 这样连写的;连字符还要多占约 5px,
    /// 而这段宽度只剩十几 px 余量(实测见 <see cref="MenuName"/>)。
    /// 【和权重文件名的关系】**只改界面文字** —— 权重文件与引擎 Tag 仍是 `alhpro-*`(引擎按 Tag 取权重)。</summary>
    public const string NamePrefix = "alh";

    /// <summary>下拉项里 `类别 · 名字` 之间的分隔符(与已有各项逐字一致:前后各一个空格)。
    /// 单独提成常量,是为了让"格式对齐已有项"这件事有一条可断言的依据,而不是靠人眼比对源码。</summary>
    public const string NameSeparator = " · ";

    /// <summary>类别词「游戏」/「现实」(用户 2026-09-15 定名;下拉里 `类别 · 名字` 的那个类别)。
    /// 已有项的类别是 `动漫` / `通用` / `现实`,这里是 `游戏` / `现实`。</summary>
    public static string Category(string? model) => IsGame(model) ? "游戏" : "现实";

    /// <summary>界面上的名字:`现实 · alhreal2x` / `游戏 · alhgame2x`(格式 = `类别 · 名字`,
    /// 与 `动漫 · animevideov3` / `通用 · x4plus` 逐字同构)。用户定名,**不得出现"实验"字样**。</summary>
    public static string Label(string? model) => Category(model) + NameSeparator + MenuName(model);

    /// <summary>下拉项显示文字(= 收起状态显示的文字)= `类别 · 名字（括号里是速度档）`,
    /// 逐字形如 `现实 · alhreal2x（快）`(与 `动漫 · animevideov3（快）` 同构)。
    /// 与 VideoView.xaml 里 ComboBoxItem 的 Content 必须逐字一致(有契约测试)。
    /// 【2026-09-16】括号里是 <see cref="SpeedTier"/>(「快」),不是时间 —— 见该常量的说明。</summary>
    public static string MenuText(string? model) => Label(model) + "（" + SpeedTier + "）";

    /// <summary>预设摘要等**纯文本**场合的写法(半角括号,与摘要里其它模型项同一风格:`通用·general-x4v3(快)` /
    /// `通用·x4plus(超慢)`)。
    /// 存在的意义:摘要那份名字列表过去是手抄字面量,抄歪过一次(带了"实验/测试"字样),用户当场纠正;
    /// 现在由本方法生成,永不会与界面口径漂移。
    /// 【2026-09-16】括号里跟摘要里其它项一样是速度档(`(快)`),不写秒/帧。</summary>
    public static string SummaryText(string? model) => Category(model) + "·" + MenuName(model) + "(" + SpeedTier + ")";

    /// <summary>是不是自训模型(**含已退休、已不在下拉里的那支** —— 权重目录选择与日志命名都靠它,见 <see cref="Retired"/>)。</summary>
    public static bool IsExperimental(string? model)
    {
        if (string.IsNullOrEmpty(model)) return false;
        foreach (var m in All)
            if (model.Contains(m, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var m in Retired)
            if (model.Contains(m, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>只有 2x 原生权重(供 <see cref="EngineScalePolicy"/> 决策引擎倍数)。</summary>
    public static bool IsX2Only(string? model) => IsExperimental(model);

    /// <summary>下拉悬停提示(**四行以内 · 事实陈述**)。
    /// 【2026-09-19 用户要求:提示要短、要像官方文档,不出现"本机/你"这类口水】模板:
    ///   ① 一行身份(自训 2x + 训练数据规模);
    ///   ② 留出视频实测(PSNR / SSIM / 边缘 PSNR)+ 三档色偏(游戏/动漫/实拍,判据 <5);
    ///   ③ 三档 detail(平均|拉普拉斯|);
    ///   ④ 速度 · 体积 · 倍率(共用常量)。
    /// 【措辞纪律】只陈述实测;不写"最强/更好"这类结论;不写测量过程中的经过(口径与工具见类注释)。</summary>
    public static string ToolTip(string? model) => IsV3(model) ? ToolTipV3 : IsV2(model) ? ToolTipV2 : IsGame(model)
        ? "游戏 · alhgame2x:自训 2x 超分模型(训练数据:1 段游戏录像 · 138 帧)。\n"
          + "留出视频实测(540p→1080p):PSNR 31.62 · SSIM 0.948 · 边缘 PSNR 21.56;色偏 游戏 0.18 / 动漫 0.04 / 实拍 2.53(判据 小于 5)。\n"
          + "detail 2.52 / 1.75 / 63.82(游戏 / 动漫 / 实拍)。\n"
          + "1080p 约 0.35 秒/帧 · " + SizeLine(model) + " " + ScaleText
        : "现实 · alhreal2x:自训 2x 超分模型(训练数据:实拍照片与人像)。\n"
          + "留出视频实测(540p→1080p):PSNR 31.56 · SSIM 0.952 · 边缘 PSNR 21.55;色偏 游戏 0.34 / 动漫 0.18 / 实拍 1.48(判据 小于 5)。\n"
          + "detail 3.55 / 2.14 / 63.44(游戏 / 动漫 / 实拍)。\n"
          + "1080p 约 0.35 秒/帧 · " + SizeLine(model) + " " + ScaleText;

    /// <summary>通道校验那句(三支共用,短句):输出与源每通道均值差 <2 级(判据 小于 5)。
    /// 【2026-09-19 用户要求"提示要短、官方一点"】旧的"旧导出那次 R/B 颠倒已修"那段历史已删 —— 结论保留、经过不再写进提示。</summary>
    public const string ChromaText = "通道校验:输出与源每通道均值差 小于 2 级(判据 小于 5)。";

    /// <summary>倍率说明(三支共用,一句话):原生 2x;目标 3x/4x 由 2x 成片放大,不重复跑引擎。
    /// 【2026-09-19 用户要求:提示要短、要像官方文档,不出现"本机/你"这类口水】故压成一句。</summary>
    public const string ScaleText = "倍率:原生 2x;目标 3x/4x 由 2x 成片放大。";

    /// <summary>模型体积(用户 2026-09-16 要求"最后显示模型大小";三支同架构 ⇒ 体积相同)。</summary>
    public const string SizeText = "2.4 MB";

    /// <summary>体积那句(三支共用)。【2026-09-19】按用户"简单、官方一点"的要求,不再列权重文件名。</summary>
    public static string SizeLine(string? model) => "模型大小 " + SizeText + "。";

    /// <summary>v2(游戏向)的悬停提示:与另外两支同一模板(身份 → 留出实测+色偏 → detail → 速度/体积/倍率)。
    /// 数字口径见 <see cref="Game2xV2"/> 的注释;⚠ 换最终权重后要重新量卡片数字并同步这里与 XAML(两处逐字一致)。</summary>
    private static readonly string ToolTipV2 =
        "游戏 · alhgame2x-v2:自训 2x 超分模型(训练数据:26 段游戏录像 · 942 帧)。\n"
        + "留出视频实测(540p→1080p):PSNR 32.67 · SSIM 0.954 · 边缘 PSNR 22.67;色偏 游戏 0.21 / 动漫 0.00 / 实拍 1.27(判据 小于 5)。\n"
        + "detail 2.77 / 1.68 / 70.58(游戏 / 动漫 / 实拍)。\n"
        + "1080p 约 0.35 秒/帧 · " + SizeLine(Game2xV2) + " " + ScaleText;
    /// <summary>v2 的下拉下方那行(不悬停也看得见):与其余两支同一套字段,一句话。</summary>
    private static readonly string HintV2 =
        "游戏 · alhgame2x-v2:色偏 0.21 / 0.00 / 1.27 · detail 2.77 / 1.68 / 70.58(游戏 / 动漫 / 实拍) · "
        + SpeedText + " · 原生 2x · " + ChromaText;

    /// <summary>v3(锐化优先)的悬停提示:与另外三支同一模板,但**必须把自己的短板写出来** ——
    /// 实拍照片 ΔB 8.85 级偏蓝(所以**不能套用 ChromaText** 那句"每通道均值差 小于 2 级",那是 v1/v2/real2x 的事实)。
    /// 数字口径见 <see cref="Game2xV3"/> 的注释。</summary>
    private static readonly string ToolTipV3 =
        "游戏 · alhgame2x-v3:自训 2x 超分模型(锐化优先:留出视频实测 detail 760、锯齿 50.91,两项都优于其它几支)。\n"
        + "留出视频实测(540p→1080p):PSNR 31.99 · SSIM 0.953 · 边缘 PSNR 22.10;最干净的 v2 是 32.67 / 0.954 / 22.67。\n"
        + "色偏 游戏 0.49 / 动漫 0.13 / 实拍 8.85(判据 小于 5:照片偏蓝,照片请用 v2);detail 3.72 / 2.28 / 77.26。\n"
        + "1080p 约 0.35 秒/帧 · " + SizeLine(Game2xV3) + " " + ScaleText;

    /// <summary>v3 的下拉下方那行:一句话,并把"照片偏蓝 → 照片用 v2"这条如实带上。</summary>
    private static readonly string HintV3 =
        "游戏 · alhgame2x-v3:色偏 0.49 / 0.13 / 8.85 · detail 3.72 / 2.28 / 77.26(游戏 / 动漫 / 实拍) · "
        + SpeedText + " · 原生 2x · 照片偏蓝,照片建议用 v2";

    public static string Hint(string? model) => IsV3(model) ? HintV3 : IsV2(model) ? HintV2 : IsGame(model)
        ? "游戏 · alhgame2x:色偏 0.18 / 0.04 / 2.53 · detail 2.52 / 1.75 / 63.82(游戏 / 动漫 / 实拍) · "
          + SpeedText + " · 原生 2x · " + ChromaText
        : "现实 · alhreal2x:色偏 0.34 / 0.18 / 1.48 · detail 3.55 / 2.14 / 63.44(游戏 / 动漫 / 实拍) · "
          + SpeedText + " · 原生 2x · " + ChromaText;

    /// <summary>日志/参数行里跟在模型名后的括号(如 `alhpro-real2x(现实 · alhreal2x · 快)`)。
    /// 【用户 2026-09-15】这几处的括号内容统一成**类别 · 名字 · 速度档**,`类别 · 名字` 与下拉项同格式;
    /// 不再是"测试"这种定性词。界面各处显示模型名的地方都要能看出"这是哪一支、什么速度"。
    /// 【用户 2026-09-16】括号里写**速度档「快」**(<see cref="SpeedTier"/>),不写"0.35 秒/帧"那种时间;
    /// 需要数字时看悬停提示或下拉下方那行。</summary>
    public static string LogSuffix(string? model)
        => IsExperimental(model) ? "(" + Label(model) + NameSeparator + SpeedTier + ")" : "";

    /// <summary>走 ONNX「稳定引擎」时的如实告知:这两支**没有 ONNX 权重**,稳定引擎里找不到对应的网络,
    /// 只能按另一支模型跑 —— 必须写成用户看得懂的一句话(静默换模型是本仓库明令禁止的)。</summary>
    public static string OnnxFallbackNotice(string? model)
        => $"⚠ 本机走的稳定引擎(ONNX)里没有「{Label(model)}({model})」这支自训模型(它只有 ncnn 权重),"
         + "本批是按另一支官方模型处理 —— 画面的色偏特征与你在下拉里选的那支不同。"
         + "要用它请用支持 ncnn 的显卡/关掉兼容模式后重试。";

    private static bool IsGame(string? model)
        => !string.IsNullOrEmpty(model) && model.Contains(Game2x, StringComparison.OrdinalIgnoreCase);

    /// <summary>是不是 **v2** 那支(游戏向第二代)。【为什么必须先判它】v2 的 id(`alhpro-game2x-v2`)
    /// **包含** v1 的 id(`alhpro-game2x`),所以 <see cref="IsGame"/> 对它也为真 ⇒ 判定顺序必须是"先 v2 再 v1",
    /// 否则下拉项、日志、悬停提示会把 v2 印成 v1 的名字(两支看起来一模一样)。</summary>
    private static bool IsV2(string? model)
        => !string.IsNullOrEmpty(model) && model.Contains(Game2xV2, StringComparison.OrdinalIgnoreCase);

    /// <summary>是不是 **v3**(锐化优先)那支。【为什么也要单独判】三支的 id 互为前缀关系
    /// ("alhpro-game2x" ⊂ "-v2" ⊂ 各自),判定必须**从新到旧**:v3 → v2 → v1,否则会把 v3 印成 v2 的名字。</summary>
    private static bool IsV3(string? model)
        => !string.IsNullOrEmpty(model) && model.Contains(Game2xV3, StringComparison.OrdinalIgnoreCase);
}
