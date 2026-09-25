namespace AlhPro.Core;

/// <summary>视频页「超分引擎」的**存盘值口径**(纯逻辑,可单测)。
///
/// 【2026-09-25 用户裁定:视频页移除 waifu2x】原话「**禁用吧 直接删掉在视频页面**」——
/// waifu2x 在视频上的观感远差于图片(已查明四处硬差异:视频路径 TTA 写死 false、`-n` 挂在「视频降噪」
/// 开关后面默认是 `-n -1`、拆帧是 4:2:0 JPEG 中间帧、而且它本身是 2017 年的静帧动漫降噪器)。
/// ⇒ 视频页的引擎单选**只剩两项**:Real-ESRGAN(界面 0)、Real-CUGAN(界面 1)。
/// 【明确不删】图片页(`UpscaleView`)与 `VideoService`/`EngineService` 里的 waifu2x 分支一律保留,
/// 本文件只管"视频页那个下拉里能选什么"。
///
/// 【存盘值一个都没改(老存档必须读得回来)】
///   · `0` = waifu2x —— **已退役**:老存档里的 0 现在**显式迁移**到 Real-ESRGAN
///     (见 <see cref="FromStored"/>),并由 UI 写一行可读日志(原文见 <see cref="Waifu2xRetiredNotice"/>)。
///     ⚠ 不许把它当成"越界"来处理:越界兜底虽然"结果碰巧也对",但**没有任何解释**,
///     用户只会看到自己存的档位悄悄变了(这正是本仓库反复出现的"静默改设置"那一类)。
///   · `1` = Real-ESRGAN
///   · `2` = **历史值**:v1.0~v1.1 期间的 Real-CUGAN;自 v1.1.0 移除 Real-CUGAN 起,
///          这个值**一直被解释成 Real-ESRGAN**,而且用户重新保存时会被规范化成 `1`。
///   · `3` = **新值**:2026-09-24 Real-CUGAN 重新随包后使用(不能复用 2 ——
///          那会让"还存着 2"的老用户升级后**静默换引擎**,他们上次看到的是 Real-ESRGAN)。
/// ⇒ 这四个角色共用一张小表,任何一处口径写错都会表现成"用户的选择存不住 / 静默换成别的引擎",
/// 且**编译期完全看不出来**。所以把"取哪个值、认哪些值、值↔界面索引怎么换"收成这一个模块。
///
/// 【事故本身(2026-09-24 修的那次)】恢复设置时的守卫写的是 `d.Engine is >= 0 and <= 2`,
/// 把新值 `3` 挡在外面 ⇒ 选 Real-CUGAN、重启后被**静默恢复成 Real-ESRGAN**(真机两次复现:
/// 文件里 `Engine=3`、日志"恢复完成: 界面 Engine=0"、那次预览实跑 `engine=realesrgan`)。
/// 守卫的判据现在只此一处:见 <see cref="IsKnownStored"/>。</summary>
public static class EngineChoice
{
    /// <summary>存盘值:waifu2x(**视频页已退役**;读到就迁到 Real-ESRGAN 并记日志)。</summary>
    public const int StoredWaifu2x = 0;
    /// <summary>存盘值:Real-ESRGAN。</summary>
    public const int StoredRealEsrgan = 1;
    /// <summary>存盘值:**历史值** —— v1.0~v1.1 的 Real-CUGAN;自 v1.1.0 起一律按 Real-ESRGAN 解释。</summary>
    public const int StoredLegacyRealCugan = 2;
    /// <summary>存盘值:Real-CUGAN(2026-09-24 重新随包起使用;不复用历史值 2)。</summary>
    public const int StoredRealCugan = 3;

    /// <summary>界面索引:Real-ESRGAN(界面第 1 项,默认)。</summary>
    public const int UiRealEsrgan = 0;
    /// <summary>界面索引:Real-CUGAN(界面第 2 项,**也是最后一项**)。
    /// 【2026-09-25 视频页移除 waifu2x】它原来是第 3 项(索引 2),现在只剩两项 ⇒ 索引不变、项数变。
    /// ⚠ 谁改了 XAML 里 RadioButton 的顺序/项数,就必须同步这里(否则界面说 A、实跑 B —— 本仓库踩过的坑)。</summary>
    public const int UiRealCugan = 1;

    /// <summary>视频页引擎单选的**项数**(XAML 必须与它一致;给契约断言与后人一个可引用的数字)。</summary>
    public const int VideoUiEngineCount = 2;

    /// <summary>读到"已退役的 waifu2x"时,UI 必须原样写下这一行(含迁移结论,用户看得懂)。
    /// 【为什么放 Core】这句话是"存档迁移"的一部分语义,和 <see cref="FromStored"/> 必须同源;
    /// 放在 UI 里散着写,两边迟早各说各话。</summary>
    public const string Waifu2xRetiredNotice =
        "视频页已移除 waifu2x(视频观感远差于图片,2026-09-25 用户裁定):旧设置里的 Engine=0(waifu2x)已改用 Real-ESRGAN";

    /// <summary>这个存盘值是不是"已退役的 waifu2x"(需要**迁移 + 记日志**)。</summary>
    public static bool IsRetiredWaifu2x(int stored) => stored == StoredWaifu2x;

    /// <summary>界面索引 → 存盘值。越界(含 -1「没有选中项」)一律落 Real-ESRGAN(界面默认项),
    /// 绝不返回一个引擎下拉里不存在的值。`0`(退役的 waifu2x)**不再会被写出去** ——
    /// 界面只有 Real-ESRGAN / Real-CUGAN 两项 ⇒ 存盘值只剩 1 与 3。</summary>
    public static int ToStored(int uiIndex) => uiIndex switch
    {
        UiRealCugan => StoredRealCugan,
        _ => StoredRealEsrgan,
    };

    /// <summary>存盘值 → 界面索引。
    /// 【`0`(waifu2x)⇒ **显式**落到 Real-ESRGAN】视频页没有这个档位了 ⇒ 读到它就迁到默认项,
    /// 并由 UI 通过 <see cref="Waifu2xRetiredNotice"/> 写日志说明(不是靠"越界兜底"碰巧达成);
    /// `2`(历史 Real-CUGAN)从 v1.1.0 起也一直是 Real-ESRGAN 语义,改回去等于让老用户静默换引擎
    /// ⇒ 同样落 Real-ESRGAN;越界值(文件被手改)同样落默认项。</summary>
    public static int FromStored(int stored) => stored switch
    {
        StoredRealCugan => UiRealCugan,
        _ => UiRealEsrgan,   // 1(Real-ESRGAN)/ 0(退役 waifu2x → 显式迁移)/ 2(历史 Real-CUGAN)/ 越界
    };

    /// <summary>**恢复设置时的唯一守卫**:这个存盘值是不是本软件**认得的**值。
    /// 【必须包含 3】漏掉 3 的后果是真机事故:用户选 Real-CUGAN 存不住、还被静默换成 Real-ESRGAN。
    /// 【也必须包含 0】它是老存档里的**合法旧值**:认得它才能"迁移 + 写日志",
    /// 而不是把它当坏文件丢掉。要不要迁移由 <see cref="IsRetiredWaifu2x"/> 决定。</summary>
    public static bool IsKnownStored(int stored) => stored is >= StoredWaifu2x and <= StoredRealCugan;

    /// <summary>界面索引 → 引擎名(引擎下拉、日志、探测键共用同一套名字)。
    /// 越界一律 Real-ESRGAN,与 <see cref="ToStored"/> / UI 的默认项保持一致。
    /// 【2026-09-25】不再有"索引 → waifu2x"的映射(视频页没有那个档位);
    /// waifu2x 只可能以**引擎名**出现(图片页、`VideoService` 的既有管线、历史存档的迁移日志)。</summary>
    public static string EngineNameOf(int uiIndex) => uiIndex switch
    {
        UiRealCugan => RealCugan.EngineName,
        _ => "realesrgan",
    };
}
