namespace AlhPro.Core;

/// <summary>阶段内「预计还剩」文案(从 VideoService.EtaStr 抽出的纯逻辑,可单测)。
/// 【为什么抽出来】这条文案是用户唯一能看到的"还要等多久",而它的每一档都踩过坑:
///   · 速率必须用【本阶段的净耗时】(扣掉休息/暂停/挂起),且必须是本阶段自己的时钟 ——
///     传任务级耗时会把拆帧/去重/补帧的时间算进当前阶段的速率里,速率被严重低估、ETA 虚高;
///   · 头几帧含模型加载/编码器初始化,用它算速率会离谱 → 已处理帧 &lt; 4 或净耗时 &lt; 1 秒时不给数字;
///   · 【2026-09-13 修】不足 1 秒时原来输出死文案"预计还剩几秒":用户读不出还要多久,
///     而阶段末往往后面还有整批收尾工作(如补帧跑完的"整批 PNG→JPG 重编码",2668 帧要跑几分钟)
///     → 于是"显示几秒、实际几分钟"。现在这一档改成"本阶段即将结束"(并用 upcoming 说明后面还有什么),
///     **任何一档都不许再出现"几秒"这种既非数字又非确定的说法**。
/// 【口径不许变激进】秒/分钟/小时三档的阈值与取整方式与改动前逐字一致(宁可略保守)。</summary>
public static class EtaText
{
    /// <summary>生成"预计还剩"文案;不需要/无法估算时返回空串(调用方直接拼接,空串即不显示)。
    /// <param name="done">本阶段已完成工作量(帧/块)。</param>
    /// <param name="total">本阶段总工作量。</param>
    /// <param name="elapsedSec">本阶段【净】耗时(秒,已扣掉休息/暂停)。</param>
    /// <param name="upcoming">阶段末尾之后【马上就要做】的收尾工作(如"2668 帧整理成 JPG");
    /// 只在"不足 1 秒"那一档被用来说明后面还有什么,其余档位不受影响。null = 没有已知的收尾。</param>
    public static string ForRemaining(double done, double total, double elapsedSec, string? upcoming = null)
    {
        try
        {
            if (done < 4 || total <= 0 || done >= total) return "";
            if (elapsedSec < 1.0) return "";
            double remainSec = (total - done) * elapsedSec / done;
            // <1 秒:不许再写"几秒"(既非数字又非确定,用户读不出还要多久)。
            // 改用"本阶段即将结束";有已知收尾时把收尾也说出来 —— 阶段末那几帧之后往往还有
            // 整批收尾(补帧后的 PNG→JPG 重编码等)要跑几分钟,不说清就还是"以为马上好"。
            // 前导 " · " 只为可读(其余档位的历史文案一字未动,那几档是直接拼在"…帧"后面的)。
            if (remainSec < 1)
                return string.IsNullOrEmpty(upcoming)
                    ? " · 本阶段即将结束"
                    : $" · 本阶段即将结束(随后还有{upcoming})";
            if (remainSec < 60) return $"预计还剩 {(int)remainSec} 秒";
            if (remainSec < 3600) return $"预计还剩 {remainSec / 60:0.#} 分钟";
            return $"预计还剩 {remainSec / 3600:0.#} 小时";
        }
        catch { return ""; }
    }

    /// <summary>文案里是否含"含糊的几秒"这种说法(回归守卫用:任何档位都不许出现 —— 见类注释)。</summary>
    public static bool ContainsVagueSeconds(string? text)
        => text != null && text.Contains("几秒", System.StringComparison.Ordinal);
}
