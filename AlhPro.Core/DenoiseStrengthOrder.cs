namespace AlhPro.Core;

/// <summary>「降噪强度」档位的**序号迁移**(纯逻辑,可单测)。
///
/// 【为什么需要它】2026-09-21 用户要求「自动放在最上面」⇒ 档位顺序从
///   旧(Rev0):0=弱 1=中 2=强 3=自动
///   新(Rev1):**0=自动** 1=弱 2=中 3=强
/// 而设置文件与预设里存的都是**序号** ⇒ 不迁移的话,老用户"存的 0(弱)"会被解释成"自动"、
/// "存的 2(强)"会变成"中" —— 静默改变处理结果,界面上看不出任何异常(本仓库在超分模型下拉上踩过同样的坑)。
///
/// 【界面序号 vs 管线取值:两层,别混】管线里的取值沿用 <see cref="VideoDenoise"/> 的既有词汇:
///   0=关、1=弱、2=中、3=强、<see cref="VideoDenoise.Auto"/>(4)=自动 —— **拆帧阶段的代码一个字都不用改**。
/// 本类只负责"界面/存储序号"与"管线取值"之间的换算(<see cref="ToPipeline"/> / <see cref="FromPipeline"/>),
/// 以及老数据的迁移(<see cref="Migrate"/>)。</summary>
public static class DenoiseStrengthOrder
{
    /// <summary>当前档位顺序的版本。**改动档位顺序必须 +1 并补一段映射**。</summary>
    public const int CurrentRev = 2;
    // 【Rev2 · 2026-09-21 用户:"自动不要了"】自动档从界面下线 ⇒ 序号变成 **0=弱 1=中 2=强**
    //   (Rev1 是 0=自动 1=弱 2=中 3=强)。老设置/老预设由 Migrate 换算,别再动序号而不 +1 Rev。

    // ===== 界面/存储序号(Rev1)=====
    /// <summary>自动(先体检素材)—— 2026-09-21 用户要求放在最上面。</summary>
    public const int Weak = 0;
    public const int Medium = 1;
    public const int Strong = 2;
    // 说明:这里**没有** Auto 常量了 —— 自动档已下线(见 CurrentRev 的说明)。旧数据里的 0(自动)由 Migrate 换算。
    /// <summary>关闭。设置里用 -1 表示"开关没开",它不是四个单选项之一。</summary>
    public const int Off = -1;

    /// <summary>把某个 Rev 下存的序号换成本 Rev 的序号(**逐段显式换算**,每段按 rev 升序)。
    /// · Rev0(原始):0=弱 1=中 2=强 3=自动        → Rev1:0=自动 1=弱 2=中 3=强
    /// · Rev1:0=自动 1=弱 2=中 3=强               → Rev2(**当前**):0=弱 1=中 2=强
    /// <see cref="Off"/>(-1)与任何越界值**原样返回**(-1 是"关",不能因为迁移变成某个档)。
    /// 【为什么改成显式两段】Rev1 时代的实现把"Rev0→Rev1"的换算写在函数**末尾**的一个 switch 里
    /// (靠 `rev >= CurrentRev` 提前返回来保证只对老数据生效)。加 Rev2 时如果照抄那个结构,
    /// 末尾那段会**再套一次**在 Rev1 数据上 ⇒ 档位被连乘两次 ✗。现在两段各自只做自己那一跳。</summary>
    public static int Migrate(int stored, int rev)
    {
        if (rev >= CurrentRev) return stored;

        if (rev < 1)
        {
            // Rev0 → Rev1:0(弱)→1、1(中)→2、2(强)→3、3(自动)→0
            stored = stored switch { 0 => 1, 1 => 2, 2 => 3, 3 => 0, _ => stored };
            rev = 1;
        }
        if (rev < 2)
        {
            // Rev1 → Rev2:自动档下线 ⇒ 0(自动)→0(弱)。
            // 【为什么落到"弱"而不是"关"或"强"】自动档在**脏素材**上的取值本来就落在弱/中(干净素材上是"跳过"),
            //   取它最轻的出手档"弱":既不把用户的降噪悄悄变强(强档实测细节只剩 58%),也不至于一点都不降。
            //   其余:1(弱)→0、2(中)→1、3(强)→2;-1(关)与越界值原样不动。
            stored = stored switch { 0 => 0, 1 => 0, 2 => 1, 3 => 2, _ => stored };
            rev = 2;
        }
        return stored;
    }

    /// <summary>界面序号 → 管线取值(0=关 / 1=弱 / 2=中 / 3=强)。越界值一律当**弱**(最轻的一档,安全)。
    /// (Rev2 起自动档下线 ⇒ 界面再也不会给出管线值 4;`VideoDenoise.Auto` 仍保留在 Core 里给已下线的自动分支用。)</summary>
    public static int ToPipeline(int index) => index switch
    {
        Weak => 1,
        Medium => 2,
        Strong => 3,
        Off => 0,
        _ => 1,
    };

    /// <summary>管线取值 → 界面序号(给"从设置里恢复选中项"用)。0(关)也按弱返回 —— 关不关是开关的事,不是档位。</summary>
    public static int FromPipeline(int pipeline) => pipeline switch
    {
        1 => Weak,
        2 => Medium,
        3 => Strong,
        _ => Weak,
    };

    /// <summary>档位名(日志/摘要/界面文案唯一源,措辞与单选项逐字一致)。未知值按"弱"显示(与兜底行为一致)。</summary>
    public static string Label(int index) => index switch
    {
        Weak => "弱",
        Medium => "中",
        Strong => "强",
        _ => "弱",
    };
}
