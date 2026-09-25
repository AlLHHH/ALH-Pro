namespace AlhPro.Core;

/// <summary>视频页「超分引擎」的**存盘值口径**(纯逻辑,可单测)。
///
/// 【为什么必须单独一个模块 · 2026-09-24 真机事故】视频页的引擎单选在界面上是三项
/// (Real-ESRGAN / waifu2x / Real-CUGAN),而**存盘用的是历史约定的一串小整数**:
///   · `0` = waifu2x
///   · `1` = Real-ESRGAN
///   · `2` = **历史值**:v1.0~v1.1 期间的 Real-CUGAN。自 v1.1.0 移除 Real-CUGAN 起,
///          这个值**一直被解释成 Real-ESRGAN**,而且用户重新保存时会被规范化成 `1`。
///   · `3` = **新值**:2026-09-24 Real-CUGAN 重新随包后使用(不能复用 2 ——
///          那会让"还存着 2"的老用户升级后**静默换引擎**,他们上次看到的是 Real-ESRGAN)。
/// ⇒ 这三个角色共用一张小表,任何一处口径写错都会表现成"用户的选择存不住 / 静默换成别的引擎",
/// 且**编译期完全看不出来**。所以把"取哪个值、认哪些值、值↔界面索引怎么换"收成这一个模块。
///
/// 【事故本身(本次修的就是它)】恢复设置时的守卫写的是 `d.Engine is >= 0 and <= 2`,
/// 把新值 `3` 挡在外面 ⇒ 选 Real-CUGAN、重启后被**静默恢复成 Real-ESRGAN**(真机两次复现:
/// 文件里 `Engine=3`、日志"恢复完成: 界面 Engine=0"、那次预览实跑 `engine=realesrgan`)。
/// 守卫的判据现在只此一处:见 <see cref="IsKnownStored"/>。</summary>
public static class EngineChoice
{
    /// <summary>存盘值:waifu2x。</summary>
    public const int StoredWaifu2x = 0;
    /// <summary>存盘值:Real-ESRGAN。</summary>
    public const int StoredRealEsrgan = 1;
    /// <summary>存盘值:**历史值** —— v1.0~v1.1 的 Real-CUGAN;自 v1.1.0 起一律按 Real-ESRGAN 解释。</summary>
    public const int StoredLegacyRealCugan = 2;
    /// <summary>存盘值:Real-CUGAN(2026-09-24 重新随包起使用;不复用历史值 2)。</summary>
    public const int StoredRealCugan = 3;

    /// <summary>界面索引:Real-ESRGAN(界面第 1 项,默认)。</summary>
    public const int UiRealEsrgan = 0;
    /// <summary>界面索引:waifu2x(界面第 2 项)。</summary>
    public const int UiWaifu2x = 1;
    /// <summary>界面索引:Real-CUGAN(界面第 3 项,**末尾追加**)。</summary>
    public const int UiRealCugan = 2;

    /// <summary>界面索引 → 存盘值。越界(含 -1「没有选中项」)一律落 Real-ESRGAN(界面默认项),
    /// 绝不返回一个引擎下拉里不存在的值。</summary>
    public static int ToStored(int uiIndex) => uiIndex switch
    {
        UiWaifu2x => StoredWaifu2x,
        UiRealCugan => StoredRealCugan,
        _ => StoredRealEsrgan,
    };

    /// <summary>存盘值 → 界面索引。`2`(历史 Real-CUGAN)按 **Real-ESRGAN** 处理:
    /// 它从 v1.1.0 起就是这个语义,改回去等于让老用户静默换引擎。</summary>
    public static int FromStored(int stored) => stored switch
    {
        StoredWaifu2x => UiWaifu2x,
        StoredRealCugan => UiRealCugan,
        _ => UiRealEsrgan,   // 1(Real-ESRGAN)/ 2(历史 Real-CUGAN)/ 越界
    };

    /// <summary>**恢复设置时的唯一守卫**:这个存盘值是不是本软件认的值。
    /// 【必须包含 3】漏掉 3 的后果是真机事故:用户选 Real-CUGAN 存不住、还被静默换成 Real-ESRGAN。
    /// 越界值(老版本写的怪数、文件被手改)返回 false ⇒ 调用方保持界面默认项,并把判决权留给"用户上次选的是什么"之外的东西。</summary>
    public static bool IsKnownStored(int stored) => stored is >= StoredWaifu2x and <= StoredRealCugan;

    /// <summary>界面索引 → 引擎名(引擎下拉、日志、探测键共用同一套名字)。
    /// 越界一律 Real-ESRGAN,与 <see cref="ToStored"/> / UI 的默认项保持一致。</summary>
    public static string EngineNameOf(int uiIndex) => uiIndex switch
    {
        UiWaifu2x => "waifu2x",
        UiRealCugan => RealCugan.EngineName,
        _ => "realesrgan",
    };
}
