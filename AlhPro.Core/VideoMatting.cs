namespace AlhPro.Core;

/// <summary>视频抠图的纯逻辑部分(alpha 后处理 / 时域滤波 / 合成)。
///
/// 【为什么全放 Core 而不是 UI 工程】这三段算法是"用户能不能接受这条片"的关键,
/// 必须能在无 UI、无显卡、无 ffmpeg 的环境里被单测钉住 —— 见 docs/2026-09-22-video-matting.md §四。
/// ONNX 推理本身仍由 `CutoutService` 负责,这里只吃它吐出来的 0~1 浮点蒙版。
///
/// 【口径与图片抠图保持一致】阈值都是 0~255 的整数滑条,羽化/形态学也是同一套档位,
/// 免得同一个人在两个页面看到"同样的参数、不一样的结果"。
/// </summary>
public static class VideoMatting
{
    /// <summary>逐帧 alpha 后处理:阈值 + 羽化 + 形态学(原地修改)。
    /// fg/bg 是 0~255 的阈值;<c>fg ≤ 0 且 bg ≤ 0 且 feather ≤ 0 且 morph ≤ 0</c> 时原样返回(全关)。
    ///
    /// 【为什么中间区不二值化】硬二值会把发丝、半透明边缘变成锯齿,视频里还会逐帧闪 ✗;
    /// 保留过渡带再交给羽化与合成,边缘更自然 —— 这也是图片抠图页"自适应阈值"滑条背后的同一考虑。
    /// 【为什么阈值只在两个都给了才拉伸】只给一个阈值时没有可用的过渡区间,拉伸会把整个蒙版压成 0/1;
    /// 这时保持原值、只让羽化/形态学生效更安全(与计划的判据一致,并有单测钉住)。
    /// </summary>
    public static void PostProcessAlpha(float[] alpha, int w, int h, int fg, int bg, int feather, int morph)
    {
        if (alpha == null || w <= 0 || h <= 0 || alpha.Length < w * h) return;
        if (fg <= 0 && bg <= 0 && feather <= 0 && morph <= 0) return;

        int n = w * h;
        float lo = bg / 255f, hi = fg / 255f;

        if (fg > 0 && bg > 0 && hi > lo)
        {
            float span = hi - lo;
            for (int i = 0; i < n; i++)
            {
                float v = alpha[i];
                if (v <= lo) alpha[i] = 0f;                 // 背景阈值以下 ⇒ 完全透明
                else if (v >= hi) alpha[i] = 1f;            // 前景阈值以上 ⇒ 完全实心
                else alpha[i] = (v - lo) / span;            // 中间带线性拉伸到 0~1(不二值化)
            }
        }

        if (feather > 0) BoxBlurAlpha(alpha, w, h, Math.Max(1, feather / 2));
        if (morph > 0) MorphOpenAlpha(alpha, w, h, Math.Max(1, morph / 25));
    }

    /// <summary>alpha 的方框模糊(可分离:先横后竖)。
    /// 【为什么可以用方框而不是高斯】羽化半径很小(≤10)且只作用在蒙版上,方框的"平顶"看不出来;
    /// 可分离后复杂度 O(n·r),1080p 默认档(feather=1 ⇒ r=1)每帧只有几毫秒。
    /// 【边界为什么按"跳过越界样本"而不是补零】补零会把画面四边往外压出一圈透明边 ✗;
    /// 跳过越界样本 ⇒ 边缘像素用邻近的真实值平均,不留人工边。
    /// </summary>
    internal static void BoxBlurAlpha(float[] a, int w, int h, int r)
    {
        var tmp = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                float s = 0f; int n = 0;
                int x0 = Math.Max(0, x - r), x1 = Math.Min(w - 1, x + r);
                for (int xx = x0; xx <= x1; xx++) { s += a[row + xx]; n++; }
                tmp[row + x] = s / n;
            }
        }
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            int y0 = Math.Max(0, y - r), y1 = Math.Min(h - 1, y + r);
            for (int x = 0; x < w; x++)
            {
                float s = 0f; int n = 0;
                for (int yy = y0; yy <= y1; yy++) { s += tmp[yy * w + x]; n++; }
                a[row + x] = s / n;
            }
        }
    }

    /// <summary>形态学开运算(先腐蚀后膨胀):削掉孤立噪点与毛刺,主体大小不变。
    /// 【为什么用开运算而不是单纯腐蚀】腐蚀会让主体整体瘦一圈 ✗;开运算只削"扛不住腐蚀"的孤立结构。
    ///
    /// 【为什么可以拆成"先行后列"】方形结构元的 min/max 是可分离的:
    /// min over 邻域 = min over 行 的 (min over 列),按同样的边界规则(跳过越界样本)写出来与
    /// 朴素二维实现**逐像素等价** —— 这条不是"看着差不多",有单测拿朴素参照实现逐像素比对钉住。
    /// 代价从 O(n·r²) 降到 O(n·r):1080p 默认档(morph=28 ⇒ r=1)每个像素只做 9 次比较,
    /// 而朴素二维实现同样的 r=1 也是 9 次 —— 但 r=4 时朴素实现是 81 次/像素/遍、可分离只有 9 次/像素/遍,
    /// 差 9 倍。这里刻意不给"多少毫秒"的数字:实测数字由 `_qa/mattingbench` 的 --postproc 模式给出
    /// (见报告),没测过的数字不写进注释。
    /// </summary>
    internal static void MorphOpenAlpha(float[] a, int w, int h, int r)
    {
        // 腐蚀(取局部最小)
        var tmp = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                float m = 1f;
                int x0 = Math.Max(0, x - r), x1 = Math.Min(w - 1, x + r);
                for (int xx = x0; xx <= x1; xx++) { float v = a[row + xx]; if (v < m) m = v; }
                tmp[row + x] = m;
            }
        }
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            int y0 = Math.Max(0, y - r), y1 = Math.Min(h - 1, y + r);
            for (int x = 0; x < w; x++)
            {
                float m = 1f;
                for (int yy = y0; yy <= y1; yy++) { float v = tmp[yy * w + x]; if (v < m) m = v; }
                a[row + x] = m;
            }
        }

        // 膨胀(取局部最大)
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                float m = 0f;
                int x0 = Math.Max(0, x - r), x1 = Math.Min(w - 1, x + r);
                for (int xx = x0; xx <= x1; xx++) { float v = a[row + xx]; if (v > m) m = v; }
                tmp[row + x] = m;
            }
        }
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            int y0 = Math.Max(0, y - r), y1 = Math.Min(h - 1, y + r);
            for (int x = 0; x < w; x++)
            {
                float m = 0f;
                for (int yy = y0; yy <= y1; yy++) { float v = tmp[yy * w + x]; if (v > m) m = v; }
                a[row + x] = m;
            }
        }
    }
}
