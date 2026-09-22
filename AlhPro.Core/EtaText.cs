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

    /// <summary>把"一段预计要用多久"写成大白话(秒/分钟/小时三档),用于**开跑前**把预计时长告诉用户。
    /// 【为什么与 ForRemaining 分开】语义不同:`ForRemaining` 说的是"还剩多久"(跑起来的倒计时),
    /// 这里说的是"这一段**总共**大约要多久"(还没开始时的预期) —— 复用同一句"本阶段预计还剩"会让用户
    /// 以为活儿已经跑了一半。
    /// 【2026-09-21 新增的用途】用户反馈"预览要等很久才开始":预览故意按完整参数真跑、不降档(用户明确要求),
    /// 所以只能把"要等多久"提前说清。数字来源与「开始处理」同一个估算函数(VideoService.EstimateProcessSeconds)。
    /// 【口径】与 <see cref="ForRemaining"/> 同族:不足 1 秒不给数字(那种量级的活不需要预告)、
    /// 60/3600 分界、`(int)` 截断;非法值(NaN/±∞/负数)返回空串,调用方直接判空跳过。
    /// 【故意**不**四舍五入到"约"以外的东西】不写"预计 3.7 分钟(±…)"这类伪精度:这个数只有量级意义。</summary>
    public static string Duration(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 1) return "";
        if (seconds < 60) return $"{(int)seconds} 秒";
        if (seconds < 3600) return $"{seconds / 60:0.#} 分钟";
        return $"{seconds / 3600:0.#} 小时";
    }

    /// <summary>【2026-09-16 用户反馈「整体预计时间不要乱写」】按**最近的实际吞吐量**估算剩余秒数。
    ///
    /// 【为什么要另开一条口径】旧口径(RemainingSeconds)用的是"本阶段开始至今的**累计平均**速度":
    /// 超分/补帧都是**按批起引擎进程**,第一批量里含着"进程启动 + 模型加载 + 首帧着色器预热"
    /// (实测每批固定开销约 1.15 秒、地板 0.75~0.92 秒;头几帧更慢)——这段开局开销会被永久摊进平均速度,
    /// 而平均速度不会回补 ⇒ **开局给出一个明显偏大的剩余时间**,用户看到的就是"数字乱跳/一会儿一个样"。
    /// 改用"最近 N 帧的吞吐量"外推后:开局之后很快收敛到真实速度,机器快慢变化时也跟着变。
    ///
    /// 【平滑与安全】完全用瞬时速率会抖,故与累计速率按权重混合:已处理帧越多,越信"最近速率"
    /// (见 RecentWeight:低于一窗时只用累计速率 = 等价于旧口径,不引入新的抖动源)。
    /// </summary>
    /// <param name="done">本阶段已完成帧数。</param>
    /// <param name="total">本阶段总帧数。</param>
    /// <param name="sampleDone">最近一次采样的已完成帧数(0 = 还没采到,回退累计口径)。</param>
    /// <param name="sampleElapsedSec">采样点当时的净耗时(秒)。</param>
    /// <param name="cumulativeElapsedSec">本阶段累计净耗时(秒,含开局开销)。</param>
    /// <param name="sampleWindowFrames">采样窗口(帧);越大越平滑、越小越灵敏。</param>
    /// <param name="fixedOverheadSec">剩余工作里还要再付的固定开销(如剩余批数 × 每批启动);≤0 = 不考虑。</param>
    /// <returns>剩余秒数;无法估算返回 -1(与 RemainingSeconds 同约定)。</returns>
    public static double RemainingSecondsByRate(double done, double total, double sampleDone,
        double sampleElapsedSec, double cumulativeElapsedSec, double sampleWindowFrames = 60,
        double fixedOverheadSec = 0)
    {
        if (!double.IsFinite(done) || !double.IsFinite(total) || !(total > 0) || done >= total) return -1;
        if (!(done >= 4) || !(cumulativeElapsedSec >= 1.0)) return -1;      // 头几帧含加载/初始化,不给数字
        double remaining = total - done;

        // 累计速率(帧/秒)——旧口径的同源量;任何情况下都作为兜底
        double rateCum = done / cumulativeElapsedSec;
        if (!(rateCum > 0) || !double.IsFinite(rateCum)) return -1;

        // 最近速率:只在"采样点有效且确有推进"时才算
        double rateRecent = 0;
        if (double.IsFinite(sampleDone) && double.IsFinite(sampleElapsedSec)
            && sampleDone > 0 && sampleDone < done && sampleElapsedSec > 0.2)
        {
            double r = (done - sampleDone) / (sampleElapsedSec > 0 ? (cumulativeElapsedSec - sampleElapsedSec) : 0);
            if (double.IsFinite(r) && r > 0) rateRecent = r;
        }

        double rate;
        if (rateRecent > 0)
        {
            // 权重随"已处理帧数 / 采样窗口"增长,上限 0.8:窗口没铺满时更信累计值(避免开局抖动),
            // 铺满之后以最近速率为主 —— 这样"最近变快了"能立刻反映出来。
            double w = Math.Clamp(done / Math.Max(1, sampleWindowFrames), 0, 1) * 0.8;
            rate = w * rateRecent + (1 - w) * rateCum;
        }
        else rate = rateCum;

        if (!(rate > 0) || !double.IsFinite(rate)) return -1;
        double remain = remaining / rate + Math.Max(0, fixedOverheadSec);
        return double.IsFinite(remain) ? remain : -1;
    }

    /// <summary>与 <see cref="RemainingSecondsByRate"/> 同口径的文案(档位与用词完全复用 ForRemaining 的规则)。</summary>
    public static string ForRemainingByRate(double done, double total, double sampleDone,
        double sampleElapsedSec, double cumulativeElapsedSec, string? upcoming = null,
        double sampleWindowFrames = 60, double fixedOverheadSec = 0)
    {
        try
        {
            double remainSec = RemainingSecondsByRate(done, total, sampleDone, sampleElapsedSec,
                cumulativeElapsedSec, sampleWindowFrames, fixedOverheadSec);
            if (remainSec < 0) return "";
            if (remainSec < 1)
                return string.IsNullOrEmpty(upcoming) ? " · 本阶段即将结束" : $" · 本阶段即将结束(随后还有{upcoming})";
            if (remainSec < 60) return $"本阶段预计还剩 {(int)remainSec} 秒";
            if (remainSec < 3600) return $"本阶段预计还剩 {remainSec / 60:0.#} 分钟";
            return $"本阶段预计还剩 {remainSec / 3600:0.#} 小时";
        }
        catch { return ""; }
    }

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