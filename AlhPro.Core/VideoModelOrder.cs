namespace AlhPro.Core;

/// <summary>视频页「超分模型」下拉的**序号迁移**(纯逻辑,可单测)。
///
/// 【为什么需要它】这个下拉保存的是**序号**(<c>VideoSettings.UpEsrganModel</c>),用户自建预设里也存序号。
/// 一旦调整下拉顺序,老序号就会被解释成**另一支模型**,而且是静默的:
/// 2026-09-12 那次把 general-x4v3 上移一位,老用户"存的 3 = 轻量模型"会变成"3 = x4plus"——
/// 1080p 实测 14.5 秒/帧、比 animevideov3 慢 17 倍,界面上却看不出任何异常。
/// 所以"移动过顺序"必须配一条一次性迁移,并按 Rev 逐级做(老文件从 Rev0 一路换到最新)。
///
/// 【顺序变更史(每次改顺序都要 +1 Rev,并在下面加一段)】
///   · Rev0 → Rev1(2026-09-12):0=animevideov3, 1=x4plus-anime, 2=x4plus, 3=general-x4v3
///                               → 0=animevideov3, 1=x4plus-anime, 2=general-x4v3, 3=x4plus
///   · Rev1 → Rev2(2026-09-13):把 general-x4v3 移到 animevideov3 正下方
///                               → 0=animevideov3, 1=general-x4v3, 2=x4plus-anime, 3=x4plus
///   · Rev2 → Rev3(2026-09-14):新增 wdn-x4v3,并按**速度与体积**重排(快/小 → 慢/大)
///                               → 0=animevideov3(4MB, 0.26~0.30 s/帧)
///                                 1=general-x4v3  (5MB, 0.458)
///                                 2=wdn-x4v3      (5MB, 0.460)
///                                 3=x4plus-anime  (9MB, 3.85)
///                                 4=x4plus        (41MB, 15.87, 超慢稳居最后)
///     ⇒ 映射:2→3、3→4、4→2(4 是 Rev2 里刚加的 wdn,它要挪到第 2 位),0/1 不变。
///   · Rev3 → Rev4(2026-09-15):**末尾追加**两个自训的 2x 模型(权重 pth→ncnn,用户定名见下),
///                              老 5 项的位置一个都不动
///                               → 0=animevideov3, 1=general-x4v3, 2=wdn-x4v3, 3=x4plus-anime, 4=x4plus,
///                                 5=alhpro-real2x(现实向)  6=alhpro-game2x(游戏向)
///     ⇒ 映射:**恒等**(0..4 原地不动;老文件不可能存过 5/6,越界兜底那条路照旧归 0)。
///       之所以能恒等,是因为这次只"追加"、不"换位" —— 与 Rev2 期间加 wdn 的手法相同,
///       也是本仓库认可的最安全的加模型方式:老用户存的序号含义不变,升级后模型不会跳。
///      【命名 · 用户 2026-09-15 定】界面文字只许叫「游戏向」「现实向」,不出现"实验"字样;
///      括号里写实测速度(口径与数值见 AlhPro.Core.ExperimentalEsrgan)。
///
/// 【Rev2 期间的 wdn 为什么在 4 位】该模型先以"末尾追加"的方式上线(追加不动老序号,最安全);
/// 用户随后要求按速度/体积排,才引出 Rev3 这次真正的换位。</summary>
public static class VideoModelOrder
{
    /// <summary>当前下拉顺序对应的 Rev。改顺序必须 +1,并同步 XAML 与 <c>UpEsrganModelNames</c>。</summary>
    public const int CurrentRev = 4;

    /// <summary>下拉项数量(0..Count-1)。Rev4 = 原 5 项 + 末尾两个自训模型(现实向 / 游戏向)。</summary>
    public const int Count = 7;

    /// <summary>把某个 Rev 下保存的序号换算成当前 Rev 的序号。
    /// <paramref name="rev"/> 为该数据写入时的 Rev(读出来即 <c>ModelOrderRev</c>)。
    /// 返回换算后的序号;<paramref name="newRev"/> 出参为换算后应写回的 Rev。
    /// 幂等:传入已是 <see cref="CurrentRev"/> 的数据原样返回。</summary>
    public static int Migrate(int model, int rev, out int newRev)
    {
        int m = model;
        if (rev < 1)
        {
            // Rev1:x4plus(超慢) ↔ general-x4v3 互换
            if (m == 2) m = 3;
            else if (m == 3) m = 2;
            rev = 1;
        }
        if (rev < 2)
        {
            // Rev2:x4plus-anime ↔ general-x4v3 互换(把 general-x4v3 移到 animevideov3 正下方)
            if (m == 1) m = 2;
            else if (m == 2) m = 1;
            rev = 2;
        }
        if (rev < 3)
        {
            // Rev3:插入 wdn-x4v3 并按速度/体积重排 —— 2→3、3→4、4(=wdn)→2
            m = m switch { 2 => 3, 3 => 4, 4 => 2, _ => m };
            rev = 3;
        }
        if (rev < 4)
        {
            // Rev4:末尾追加两个自训模型(5=alhpro-real2x 现实向、6=alhpro-game2x 游戏向)⇒ **恒等映射**,老序号含义不变。
            // 【为什么这里什么都不做也要留一段】① 版本号必须前进:不写 rev=4 的话,文件每次都"版本落后",
            //   每次启动都重写一次设置文件;② 留一段空的映射是在明确声明"这次没有换位",
            //   下一个人要加模型时能照着这段抄(本仓库的既有约定:Rev 与映射一一对应,注释里写清为什么恒等)。
            // ⚠ 若日后有人把新模型**插到中间**(而不是追加),这里必须补上真正的换位映射,否则老序号会静默指向别的模型。
            rev = 4;
        }
        newRev = rev;
        // 越界值(手改坏/未来版本回退)保守归到 0(animevideov3,最快最省那一支),不猜中间项
        return m is >= 0 and < Count ? m : 0;
    }
}
