namespace AlhPro.Core;

/// <summary>视频页「转场识别(转场处不插帧)」开关的**默认口径标记**(纯逻辑,可单测)。
///
/// 【用户最终定调(2026-09-15)】**默认关**,并且**升级不许改变用户已有的勾选状态**。
/// 这条口径是"用户自己决定要不要切点保护":默认不替他开;他勾上,两条路径(普通分段路径与
/// 「平滑时间轴」路径)都生效;他取消勾选,两条路径也都不生效。
///
/// 【Rev 历史 —— 一定要照实读,因为中间反过一次】
///   · Rev0 = 早期版本(那时还没有 `SceneDefaultRev` 字段):默认**关**;
///   · Rev1(2026-09-15 上午):曾把默认改成**开**,并配一次"把老用户的 `Scene=false` 强制改成 true"的迁移
///     —— **该口径已被用户明确撤回**(理由:转场识别要可开可关,不该替他决定);
///   · Rev2(2026-09-15 定稿):默认**关**;迁移**只对齐 Rev、绝不改用户的值** ⇒
///     老文件里的 `Scene` 是 true 就还是 true、是 false 就还是 false(升级前后完全一致)。
///
/// 【为什么"只对齐 Rev"就够了】`SceneDefaultRev` 现在的唯一作用是**版本标记**:
/// 记下"这份设置已按当前口径结算过",让 `MigrateSceneDefault` 只在版本落后时写一次盘,
/// 不在每次启动重复改写文件。它**不携带任何"要改哪个值"的动作** —— 见 <see cref="Migrate"/>。
///
/// 【本类提供的硬保证】`Migrate` 的返回值恒等于传入的 <paramref name="scene"/>;
/// 也就是说**工程里不存在任何"把 Scene 置真"的迁移路径**(单测里有一条契约专门钉这件事)。</summary>
public static class SceneDefaultPolicy
{
    /// <summary>当前口径的 Rev。**只增不减**(减小会让已结算的文件被反复改写)。
    /// 改默认口径时 +1,并同步三处界面默认值(XAML 的 `SceneCheck`、重置路径、本类的 <see cref="DefaultScene"/>)。
    /// 取值 2 = "默认关 + 迁移不改用户的值"(见类注释里的 Rev 历史)。</summary>
    public const int CurrentRev = 2;

    /// <summary>当前 Rev 下 `Scene` 的默认值。**false = 默认关**(用户量最终定调)。
    /// 它只用于"新用户 / 重置"这条没有历史值的路径;有历史值时一律以历史值为准(见 <see cref="Migrate"/>)。</summary>
    public const bool DefaultScene = false;

    /// <summary>把某个 Rev 下保存的 `Scene` 换算成当前 Rev 的口径。
    /// 【本版语义:**不改值,只对齐版本号**】返回值恒等于 <paramref name="scene"/> ——
    /// 用户升级前是勾着的就还是勾着的,没勾的就还是没勾的(与 Rev0/Rev1 的文件一视同仁)。
    /// <paramref name="newRev"/> 出参为应写回的 Rev(只前进,不回退)。
    /// 幂等:传入已是 <see cref="CurrentRev"/> 的数据原样返回、Rev 不变。</summary>
    public static bool Migrate(bool scene, int rev, out int newRev)
    {
        // 【为什么这里什么都不做】Rev1 曾在这一步把 scene 强制置 true(理由是"让老用户也吃到切点保护"),
        // 用户随后明确要求"转场识别要可开可关" ⇒ 那个动作被撤销,并且**不许再有任何等价写法**
        // (`if (rev < 1) scene = true;` 那种)。要改默认值只能改 <see cref="DefaultScene"/>,
        // 它只影响"没有历史值"的新用户/重置路径。
        newRev = Math.Max(rev, CurrentRev);
        return scene;
    }
}
