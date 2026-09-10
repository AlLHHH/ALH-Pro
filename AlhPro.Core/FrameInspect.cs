namespace AlhPro.Core;

/// <summary>
/// 帧质量判定(纯逻辑,不含图像解码):判断"帧是否近全黑 / 带状近黑 / 缺陷"。
/// 抽出可测的阈值逻辑,使用方:EngineService.ConvertPngToJpg(超分批输出)/ IsBlackPng、VideoService.DefectiveFramesAllComeFromNearBlack(源帧黑场防误杀)。
/// 判定规则:采样像素中 ≥95% 的 RGB 和 &lt; 24 视为近黑(缺陷)。
/// 【两种损坏形态都要抓】(2026 实测,RTX 4060 Laptop / waifu2x-ncnn-vulkan 20250915 + models-cunet):
/// ①整帧近黑:GPU 队列彻底失败,整张输出全黑 → IsNearBlack(原语义,一字未改);
/// ②带状近黑:同一次故障里更常见的形态是【每帧下 2/3 全黑、上 1/3 正常】——黑了约 66% 像素,
///   整帧量词的 IsNearBlack 判"正常"(ffmpeg blackdetect 同样按整帧比例,也完全不报),
///   坏帧于是静默进成片、零日志、ncnnUnreliable 不置位 → IsBandBlack(本轮新增)。
/// 空/0字节/非法尺寸/解码失败由调用方(持有图像句柄)负责,本类只做像素采样判定。
/// </summary>
public static class FrameInspect
{
    /// <summary>"近黑"判定的单一来源阈值:采样像素 RGB 和 &lt; 此值,算一个暗像素。整帧判定与条带判定共用。</summary>
    public const int DarkRgbSum = 24;

    /// <summary>一条(整帧或某个主条带)内此比例的采样像素为暗像素,即判该条为"近黑"。整帧判定与条带判定共用。</summary>
    public const double DarkRatio = 0.95;

    /// <summary>判断一批采样像素是否"近全黑"(≥95% 的像素 RGB 和 &lt; 24)。total≤0 时视为非黑(不误判)。
    /// 【语义保持不变】这里是"整帧量词",不要再往里面塞条带逻辑 —— 条带判定走 IsBandBlack,
    /// 两者由 IsDefectiveFrame 组合;这样"整帧近黑"的老行为(以及各调用方对它的依赖)一字未改。</summary>
    public static bool IsNearBlack(int[] sumRgb, int total)
    {
        if (total <= 0) return false;
        int dark = 0;
        foreach (var s in sumRgb) if (s < DarkRgbSum) dark++;
        return dark >= total * DarkRatio;
    }

    /// <summary>把采样网格按【行】切成上/中/下三条主条带(各占约 1/3 行),判断是否存在"几乎全黑的条带"。
    /// 【切法为什么是"按行三等分"】实测到的带状损坏是【下 2/3 全黑、上 1/3 正常】:下 1/3 条带 100% 全黑,
    /// 必然被抓到;而整帧量词只黑到 66%,抓不到。切得再细没有实测收益,反而更易误伤:
    /// 三等分既足够粗(常见宽银幕上下黑边各约 12%~22% 高,填不满任何一条 1/3 条带 ——
    /// 要让某条 1/3 条带 ≥95% 全黑,画面里得有连续 ≥1/3 高的纯黑区,约等于内容宽高比 ≥4.8:1),
    /// 又足够细(半张图=1/2 太粗,黑边可能填满;切成 4 条以上太细,正常暗部会误报),故取 3。
    /// 【只按行切、不按列切】实测损坏是横向条带;按列切会把"左右黑边的竖屏/方屏素材"误判成缺陷,
    /// 对这种形态零收益、只剩风险,故不做。
    /// 【阈值】条带内 ≥95%(DarkRatio)的采样像素 RGB 和 &lt; 24(DarkRgbSum)= 该条带近黑,与整帧判定同阈值:
    /// 不因为"只看 1/3 的像素"就放宽,避免把正常暗场(夜景/片头淡入淡出)误判成缺陷。
    /// 采样数据长度与 rows*cols 不符时返回 false(拿不准就不判缺陷,与 IsNearBlack 的"total≤0 不误判"同思路)。
    /// sampledRows/sampledCols 由 SampleGrid(width,height) 算出,必须与 ForEachSample 的遍历一致。</summary>
    public static bool IsBandBlack(int[] sumRgb, int sampledRows, int sampledCols)
    {
        if (sumRgb == null || sampledRows <= 0 || sampledCols <= 0) return false;
        if (sumRgb.Length < sampledRows * sampledCols) return false;   // 采样几何与数据不符:不误判
        for (int band = 0; band < 3; band++)
        {
            int y0 = band * sampledRows / 3;
            int y1 = (band + 1) * sampledRows / 3;
            if (y1 <= y0) continue;                                    // 行数太少时该条带为空:跳过(不误判)
            int dark = 0, n = 0;
            for (int y = y0; y < y1; y++)
                for (int x = 0; x < sampledCols; x++)
                {
                    if (sumRgb[y * sampledCols + x] < DarkRgbSum) dark++;
                    n++;
                }
            if (dark >= n * DarkRatio) return true;
        }
        return false;
    }

    /// <summary>帧缺陷的统一入口 =【整帧近全黑】或【任一主条带近全黑】。
    /// 条带判定【不改变】整帧近全黑的原语义:IsNearBlack 为 true 时本函数必然也为 true,
    /// 它只是把"整帧 ≥95% 近黑"这个过窄的判据放宽到"某个 1/3 条带 ≥95% 近黑",
    /// 于是"下 2/3 全黑、上 1/3 正常"这类坏帧不再漏检。
    /// 调用方(EngineService.ConvertPngToJpg 的 out nearBlack、IsBlackPng/NearBlackProbe)拿到的
    /// 就是这个"是否缺陷帧"的结论 → 触发既有的黑帧降级链(该批重跑 → 回退源帧),
    /// 绝不会把带状坏帧当正常帧放行。width/height 为源图尺寸(用于推导采样几何)。</summary>
    public static bool IsDefectiveFrame(int[] sumRgb, int total, int width, int height)
    {
        if (IsNearBlack(sumRgb, total)) return true;
        SampleGrid(width, height, out int rows, out int cols);
        return IsBandBlack(sumRgb, rows, cols);
    }

    /// <summary>黑帧降级的"防误杀"判定:是否可以把这批【被判黑/转码失败】的帧当作"素材本身就是黑场"放行
    /// (即跳过 ONNX/CPU 重算)。返回 true 仅当:缺陷帧数 &gt; 0,且【每一帧】的对应源帧都近全黑。
    /// 【为什么必须"全部满足",不能"存在一个满足"】历史实现是存在量词(目录里只要有任意一张源帧近黑,
    /// 就豁免【整批】),于是含黑场的素材(片头黑场/淡入淡出/夜戏/闪黑)上,GPU 真正故障产出的黑帧会被
    /// 整批放行 —— 产品铁律「绝不把黑帧写进输出」在这类素材上完全失效,而且静默无日志。
    /// 参数为 null 或空集合(典型:引擎一帧都没输出)必须返回 false —— 空批是真故障,与素材内容无关。
    /// 拿不准时一律返回 false(= 降级):降级最坏只是白算一遍,与"黑帧进成片"不是一个量级的代价。</summary>
    public static bool ShouldExemptAsSourceBlack(System.Collections.Generic.IReadOnlyCollection<bool>? defectiveFrameSourceIsNearBlack)
    {
        if (defectiveFrameSourceIsNearBlack == null || defectiveFrameSourceIsNearBlack.Count == 0) return false;
        foreach (var isBlack in defectiveFrameSourceIsNearBlack)
            if (!isBlack) return false;   // 只要有一帧的源帧不是黑场 → GPU 真的出故障了
        return true;
    }

    /// <summary>根据采样步长,计算需采样的像素总数(图片可能很大,只采一部分;与引擎侧一致)。
    /// 步长 = max(4, min(w,h)/32),保证至少采到一部分像素,避免 TINY 图(如 1×1)采样点过少。
    /// </summary>
    public static int SampleStep(int width, int height)
    {
        if (width <= 0 || height <= 0) return 1;
        return Math.Max(4, Math.Min(width, height) / 32);
    }

    /// <summary>遍历采样网格的回调入口:把 (x,y) 的采样点交给 onPixel 累计,返回采样总数。
    /// 纯逻辑,不依赖 System.Drawing,便于测试步长与遍历边界。</summary>
    public static int ForEachSample(int width, int height, Action<int, int> onPixel)
    {
        int step = SampleStep(width, height);
        int total = 0;
        for (int y = step; y < height; y += step)
            for (int x = step; x < width; x += step)
            {
                onPixel(x, y);
                total++;
            }
        return total;
    }

    /// <summary>采样网格的【行数/列数】—— 必须与 ForEachSample 的遍历严格一致(y、x 都从 step 起步、
    /// 以 step 为步长、都要求 &lt; height/width,且行在外层),否则 IsBandBlack 按下标还原"第几行"时会错位。
    /// 例:1080×1920 → step=33 → 58 行 × 32 列(共 1856 个采样点)。
    /// rows/cols 即 ForEachSample 的返回值 total = rows*cols,所以 sumRgb.Length == rows*cols。
    /// </summary>
    public static void SampleGrid(int width, int height, out int rows, out int cols)
    {
        rows = 0;
        cols = 0;
        if (width <= 0 || height <= 0) return;
        int step = SampleStep(width, height);
        if (height > step) rows = (height - 1 - step) / step + 1;
        if (width > step) cols = (width - 1 - step) / step + 1;
    }
}
