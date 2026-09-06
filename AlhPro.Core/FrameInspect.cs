namespace AlhPro.Core;

/// <summary>
/// 帧质量判定(纯逻辑,不含图像解码):判断"帧是否近全黑 / 缺陷"。
/// 从 VideoService.batchOutHasDefectiveFrame / EngineService.IsBlackPng 抽出可测的阈值逻辑。
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
