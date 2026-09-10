namespace AlhPro.Core;

/// <summary>
/// 帧质量判定(纯逻辑,不含图像解码):判断"帧是否近全黑 / 缺陷"。
/// 抽出可测的阈值逻辑,使用方:EngineService.ConvertPngToJpg(超分批输出)/ IsBlackPng、VideoService.DefectiveFramesAllComeFromNearBlack(源帧黑场防误杀)。
/// 判定规则:采样像素中 ≥95% 的 RGB 和 &lt; 24 视为近黑(缺陷);
/// 空/0字节/非法尺寸/解码失败由调用方(持有图像句柄)负责,本类只做像素采样判定。
/// </summary>
public static class FrameInspect
{
    /// <summary>判断一批采样像素是否"近全黑"(≥95% 的像素 RGB 和 &lt; 24)。total≤0 时视为非黑(不误判)。</summary>
    public static bool IsNearBlack(int[] sumRgb, int total)
    {
        if (total <= 0) return false;
        int dark = 0;
        foreach (var s in sumRgb) if (s < 24) dark++;
        return dark >= total * 0.95;
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
}
