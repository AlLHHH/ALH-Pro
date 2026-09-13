namespace AlhPro.Core;

/// <summary>阶段内「本阶段预计还剩」文案与"整片剩余"的下限规则(从 VideoService.EtaStr / VideoView 抽出的纯逻辑,可单测)。
/// 【为什么抽出来】这条文案是用户唯一能看到的"还要等多久",而它的每一档都踩过坑:
///   · 速率必须用【本阶段的净耗时】(扣掉休息/暂停/挂起),且必须是本阶段自己的时钟 ——
///     传任务级耗时会把拆帧/去重/补帧的时间算进当前阶段的速率里,速率被严重低估、ETA 虚高;
///   · 头几帧含模型加载/编码器初始化,用它算速率会离谱 → 已处理帧 &lt; 4 或净耗时 &lt; 1 秒时不给数字;
///   · 【2026-09-13 修】不足 1 秒时原来输出死文案"预计还剩几秒":用户读不出还要多久,而阶段末往往后面
///     还有整批收尾工作(如补帧跑完的"整批 PNG→JPG 重编码",2668 帧要跑几分钟)→ 于是"显示几秒、实际几分钟"。
///     现在这一档改成"本阶段即将结束"(并用 upcoming 说明后面还有什么),
///     **任何一档都不许再出现"几秒"这种既非数字又非确定的说法**;
///   · 【2026-09-13 修 K1】秒/分钟/小时三档的文案改成"**本阶段**预计还剩 X":原来只写"预计还剩 X",
///     用户分不清那是"本阶段"还是"整片"(真机截图里同一屏既有"预计还剩 3.7 分钟"又有"本片剩余 0:30")。
///     阈值与取整方式(60/3600 分界、`(int)` 截断)保持不动 —— 口径不变得更激进,宁可略保守。
/// 【口径不许变激进】秒/分钟/小时三档的数字算法与改动前逐字一致。</summary>
public static class EtaText
{
    /// <summary>原始外推:剩余工作量 × (本阶段净耗时 ÷ 已完成工作量);无法估算时返回 -1。
    /// 【为什么要单独一个函数】文案与"整片剩余的下限"必须来自同一个数字,否则两处口径又会漂移
    /// (这正是 K2 之前"整片 0:30 / 本阶段 3.7 分钟"并存的成因之一)。</summary>
    public static double RemainingSeconds(double done, double total, double elapsedSec)
    {
        if (!(done >= 4) || !(total > 0) || done >= total) return -1;   // 头几帧含加载/初始化,不给数字
        if (!(elapsedSec >= 1.0)) return -1;
        if (!double.IsFinite(done) || !double.IsFinite(total) || !double.IsFinite(elapsedSec)) return -1;
        return (total - done) * elapsedSec / done;
    }

    /// <summary>生成"本阶段预计还剩"文案;不需要/无法估算时返回空串(调用方直接拼接,空串即不显示)。
    /// <param name="done">本阶段已完成工作量(帧/块)。</param>
    /// <param name="total">本阶段总工作量。</param>
    /// <param name="elapsedSec">本阶段【净】耗时(秒,已扣掉休息/暂停)。</param>
    /// <param name="upcoming">阶段末尾之后【马上就要做】的收尾工作(如"2668 帧整理成 JPG");
    /// 只在"不足 1 秒"那一档被用来说明后面还有什么,其余档位不受影响。null = 没有已知的收尾。</param>
    public static string ForRemaining(double done, double total, double elapsedSec, string? upcoming = null)
    {
        try
        {
            double remainSec = RemainingSeconds(done, total, elapsedSec);
            if (remainSec < 0) return "";
            // <1 秒:不许再写"几秒"(既非数字又非确定,用户读不出还要多久)。
            // 改用"本阶段即将结束";有已知收尾时把收尾也说出来 —— 阶段末那几帧之后往往还有
            // 整批收尾(补帧后的 PNG→JPG 重编码等)要跑几分钟,不说清就还是"以为马上好"。
            // 前导 " · " 只为可读(其余档位的历史文案一字未动,那几档是直接拼在"…帧"后面的)。
            if (remainSec < 1)
                return string.IsNullOrEmpty(upcoming)
                    ? " · 本阶段即将结束"
                    : $" · 本阶段即将结束(随后还有{upcoming})";
            // 三档一律带"本阶段"限定词:与"本片剩余/整批剩余"区分开(K1)。
            if (remainSec < 60) return $"本阶段预计还剩 {(int)remainSec} 秒";
            if (remainSec < 3600) return $"本阶段预计还剩 {remainSec / 60:0.#} 分钟";
            return $"本阶段预计还剩 {remainSec / 3600:0.#} 小时";
        }
        catch { return ""; }
    }

    /// <summary>文案里是否含"含糊的几秒"这种说法(回归守卫用:任何档位都不许出现 —— 见类注释)。</summary>
    public static bool ContainsVagueSeconds(string? text)
        => text != null && text.Contains("几秒", System.StringComparison.Ordinal);

    /// <summary>当前阶段的剩余秒数(界面侧算"整片剩余下限"用):按【阶段内已完成比例】与【阶段净耗时】外推 ——
    /// 与阶段内 ETA 同一个公式,但数据来自界面自己(阶段起点 + fine 百分比区间),
    /// 不必去解析本地化的文案(中文单位/四舍五入都会让解析失真)。
    /// 比例太小(≤1%)或阶段刚开始(≤3 秒)时返回 0 = 不给下限(拿不准就不给假数)。
    /// <param name="stageRatio">阶段内已完成比例(0~1;由 pctFine 落在阶段区间的位置算出)。</param>
    /// <param name="stageElapsedSec">本阶段净耗时(秒)。</param>
    public static double StageRemainingSeconds(double stageRatio, double stageElapsedSec)
    {
        if (!double.IsFinite(stageRatio) || !double.IsFinite(stageElapsedSec)) return 0;
        if (stageRatio <= 0.01 || stageElapsedSec <= 3) return 0;   // 拿不准就不给下限
        double r = Math.Min(1.0, stageRatio);
        return Math.Max(0, stageElapsedSec * (1.0 / r - 1.0));
    }

    /// <summary>整片剩余的下限钳制与单调约束(纯函数,可单测):
    /// ① 同阶段内只许变小(消除"已用÷进度"在小幅抖动下被放大成"越等越久");
    /// ② 【硬规则】整片剩余**不得低于当前阶段剩余** —— 真机 bug:编码阶段自己的 ETA 还有 3.7 分钟,
    ///    而"本片剩余"显示 0:30(编码在整体进度里只占 96~100 四个点,按"已用÷进度"外推必然趋近 0,
    ///    这不是保守而是量级错误);
    /// ③ ②优先于①:阶段剩余高于上次显示值时允许抬上去 —— 否则会被"只许变小"永久压死。
    /// 【为什么下限能压过单调】"阶段还要 3.7 分钟"是由本阶段实测速率推出的确定信息,而"只许变小"只是
    /// 消除抖动的观感优化;让观感优化去掩盖一个量级错误是本末倒置。阶段切换时调用方本来就重置基准,
    /// 所以不会被一路抬上去。
    /// <param name="wholeRemainSec">按进度占比外推出来的整片剩余(秒)。</param>
    /// <param name="stageRemainSec">当前阶段的剩余秒数(见 StageRemainingSeconds;0 = 未知,不参与)。</param>
    /// <param name="lastShownSec">上次显示过的值(秒;≤0 = 还没显示过,不参与单调约束)。</param>
    public static double ClampWholeRemaining(double wholeRemainSec, double stageRemainSec, double lastShownSec)
    {
        double r = double.IsFinite(wholeRemainSec) ? Math.Max(0, wholeRemainSec) : 0;
        if (lastShownSec > 0 && r > lastShownSec) r = lastShownSec;                       // ① 单调(只许变小)
        if (double.IsFinite(stageRemainSec) && stageRemainSec > r) r = stageRemainSec;    // ② 硬下限(优先于①)
        return Math.Max(0, r);
    }
}