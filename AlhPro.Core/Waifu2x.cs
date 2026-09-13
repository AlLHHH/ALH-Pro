namespace AlhPro.Core;

/// <summary>waifu2x 引擎「模型自带降噪」档位(-n)的映射策略(纯逻辑,可单测)。
/// 【为什么要它 —— 真机反馈 2026-09-13】原来的映射是 `chosen >= 1 ? chosen : 2`:
///   · **"关"关不掉**:未勾选降噪(chosen=0)仍然下发 `-n 2`;
///   · **档位非单调**:不勾=2 档、弱=1 档、中=2 档、强=3 档 —— 选"弱"反而比不勾更轻。
/// 现在的口径(用户明确要求):**关 → -1(真正不降噪)、弱 → 0、中 → 1、强 → 2**,严格递增。
/// 【1x 的硬约束(真机实测,必须遵守)】`waifu2x -n -1` 配 `-s 1` **必崩**
/// (exit -1073741819 访问违例、输出 0 字节;连测 3 次、三个模型全部复现),而 `-s 2`/`-s 4` 正常。
/// 因此当【实际下发给引擎的倍率】为 1x 时**不得**返回 -1 —— 改返回 0(最轻档)。
/// 注意:「关 + 1x」返回 0 与「弱 + 1x」同为 0,所以 1x 下只是**非递减**而不是严格递增;
/// 这是崩溃规避带来的必然例外(1x 下引擎侧本来也不会收到 -s 1:见 EngineService 对 engineScale==1 的处理)。
/// 【为什么要按"引擎倍率"而不是"UI 倍率"判】引擎自己会把 waifu2x 的倍率向上取到 2 的幂
/// (`PathUtil.CeilPowerOfTwo`);调用方必须按**最终下发的那个值**做判断,否则 1x 的护栏会失守。</summary>
public static class Waifu2x
{
    /// <summary>「关」档下发的 -n(不降噪)。</summary>
    public const int NoiseOff = -1;

    /// <summary>UI 档位(0=关,1=弱,2=中,3=强)→ 引擎 -n。引擎倍率 ≤1 时"关"退化为 0(最轻档,规避 1x 崩溃)。
    /// <param name="uiLevel">UI 档位:0=关、1=弱、2=中、3=强(越界自动钳到 0~3)。</param>
    /// <param name="engineScale">**最终下发给引擎的**超分倍率(waifu2x 用 PathUtil.CeilPowerOfTwo 之后的那个值)。</param></summary>
    public static int NoiseLevelFor(int uiLevel, int engineScale)
    {
        int lv = uiLevel < 0 ? 0 : uiLevel > 3 ? 3 : uiLevel;   // 越界兜底:<=0 当关、>=3 当强
        if (lv == 0)
            return engineScale <= 1 ? 0 : NoiseOff;             // 1x 护栏:-n -1 配 -s 1 必崩 → 退最轻档
        return lv - 1;                                          // 弱 0 / 中 1 / 强 2(严格递增)
    }
}
