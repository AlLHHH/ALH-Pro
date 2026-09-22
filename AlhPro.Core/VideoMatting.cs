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
    /// 代价从 O(n·r²) 降到 O(n·r)。实测(Release,1080p/2.07 MPix,取 3 次最快,工具 `_qa/mattingbench` 的
    /// `--postproc` 模式):只做阈值 11.1 ms/帧;加 feather=1 + morph=28(isnet 默认档)后 82.4 ms/帧;
    /// 极值档(feather=20 + morph=100)183.5 ms/帧。也就是说 **feather/morph 这两档约占后处理的 85%**,
    /// 而它们只作用在蒙版上 —— 将来若嫌慢,优先动这里(整数化/向量化/降分辨率蒙版),别去动阈值那段。
    /// 备注:朴素二维实现在同参数下是 81 次/像素/遍,r=4 时就是这里的 9 倍。
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

    /// <summary>这一帧是否像场景切换(逐像素灰度差均值 ≥ 阈值)。
    ///
    /// 【为什么要它】时域滤波是"有记忆"的:切镜后如果继续掺上一帧的 alpha,新镜头会挂着旧镜头的形状
    /// (可见鬼影)✗。所以调用方在每帧滤波前先问一句"这是不是切点",是就 Reset。
    ///
    /// 【为什么用手写帧差而不是接 SceneCutJudge】那套是给"转场检测"用的重判据(带直方图/阈值地图,
    /// 参数是给整段视频定标的);这里只要一个够用的"该不该重置记忆"信号,而且必须在 Core 里可单测、
    /// 不依赖 UI 设置。默认阈值 25 与 `SceneCutOptions` 的默认帧差阈值同口径,免得两边不一致。
    /// </summary>
    public static bool LooksLikeSceneCut(byte[] grayPrev, byte[] grayCur, double meanDiffThreshold = 25.0)
    {
        if (grayPrev == null || grayCur == null) return false;
        int n = Math.Min(grayPrev.Length, grayCur.Length);
        if (n == 0) return false;
        long sum = 0;
        for (int i = 0; i < n; i++) sum += Math.Abs(grayCur[i] - grayPrev[i]);
        return sum / (double)n >= meanDiffThreshold;
    }

    /// <summary>把前景按 alpha 合成到背景上(结果写到 dst)。三路都是 pixels*3 的字节
    /// (通道顺序由调用方保证一致:BGR 或 RGB 都行,只要三路一致)。
    ///
    /// 【为什么不抛异常】长视频里单帧解码失败、或调用方复用的缓冲区长度对不上,都属常见;
    /// 纯算法层静默返回,"跳过并记录"由调用方统一决定 —— 在这里抛会把"一帧的问题"升级成"整段失败"。
    /// 【为什么 dst 长度也要检查】最容易出事的恰恰是它:调用方复用上一段视频的缓冲区(4K vs 1080p)时
    /// 长度不匹配,少检查一次就是越界写内存。所以任何一个入参不足都直接返回,一个字节都不写。
    /// 【为什么 +0.5f 再截断】直接截断会让每次混合都整体偏暗半格,长片里累积成肉眼可见的偏色。
    /// </summary>
    public static void Composite(byte[] fg, float[] alpha, byte[] bg, byte[] dst, int pixels)
    {
        if (fg == null || bg == null || dst == null || alpha == null) return;
        if (pixels <= 0) return;
        int need = pixels * 3;
        if (fg.Length < need || bg.Length < need || dst.Length < need || alpha.Length < pixels) return;

        for (int i = 0, p = 0; i < pixels; i++, p += 3)
        {
            float a = Math.Clamp(alpha[i], 0f, 1f);
            float inv = 1f - a;
            dst[p]     = (byte)Math.Clamp(fg[p]     * a + bg[p]     * inv + 0.5f, 0f, 255f);
            dst[p + 1] = (byte)Math.Clamp(fg[p + 1] * a + bg[p + 1] * inv + 0.5f, 0f, 255f);
            dst[p + 2] = (byte)Math.Clamp(fg[p + 2] * a + bg[p + 2] * inv + 0.5f, 0f, 255f);
        }
    }
}

/// <summary>alpha 的时域滤波(治"逐帧独立推理导致的边缘闪烁/呼吸")。
///
/// 三个机制(见 docs/2026-09-22-video-matting.md §五):
///   ① 帧差门控的 EMA:静止区多平滑、真运动区少平滑 ⇒ 稳但不拖影;
///   ② 切点重置:调用方用 <see cref="VideoMatting.LooksLikeSceneCut"/> 判断后调 <see cref="Reset"/>;
///   ③ 非过渡带直通:实心/全透明像素直接取当前帧 —— 这是"不拖影"的关键,鬼影永远出现在这两类区域。
///
/// 【为什么用"帧差"当门控而不是光流】光流要额外模型与算力,而这里只需要区分"抖动"(值在动但形状没动)
/// 与"真运动"(形状真的变了)。帧差在过渡带上足够表达这件事,而且零依赖、可单测。
///
/// 【为什么 k 有 0.9 的上限】若允许 1.0(完全不动),静止序列会永久卡在第一次的值上,
/// 之后真运动也拉不回来(死住)。留 10% 的跟随性 = 最坏情况几十帧内跟到位,而不是永不跟。
/// </summary>
public sealed class AlphaTemporalFilter
{
    private readonly float _k;        // 平滑强度:0=不平滑,趋近 0.9=最强
    private readonly int _n;
    private float[]? _prev;           // 上一帧的滤波结果(记忆)
    private float[]? _prevCur;        // 上一帧的原始输入(只为算帧差)
    private bool _hasHistory;

    /// <param name="stability">稳定档 0~100(界面滑条;设计默认 50)。</param>
    public AlphaTemporalFilter(int stability, int w, int h)
    {
        _k = Math.Clamp(stability, 0, 100) / 100f * MaxBlend;
        _n = Math.Max(1, w * h);
    }

    /// <summary>平滑上限(0~1 之间的"最多掺多少旧值")。取 0.9 而不是 1.0,理由见类注释。
    /// 【为什么是 0.9 这个数】定标依据:静止噪声场景(stability=80)要把 8 个独立噪声像素的
    /// 残余极差压到 0.10 以下,而"真运动"场景又必须在 1 帧内跟上大半 ——
    /// 实测档位见 AlphaTemporalFilterTests(两条判据同时成立才放行)。</summary>
    private const float MaxBlend = 0.9f;

    /// <summary>帧差门控系数:帧差 × 它 = "跟手程度"。越大越跟手(越不抹运动)。
    /// 取 3.5 的定标依据同 <see cref="MaxBlend"/>:静止抖动(帧差 ~0.07)门控弱、运动(帧差 ≥0.29)门控接近 1。</summary>
    private const float GateGain = 3.5f;

    /// <summary>清空历史(切点、新素材、换参数时调用)。</summary>
    public void Reset() { _hasHistory = false; _prev = null; _prevCur = null; }

    /// <summary>原地滤波一帧 alpha(长度需 ≥ 构造时的 w*h;不足则忽略、不改历史)。</summary>
    public void Push(float[] alpha)
    {
        if (alpha == null || alpha.Length < _n) return;

        // stability=0 ⇒ 完全不平滑,同时把记忆丢掉(下次开滑条时不该继承旧画面)
        if (_k <= 0f) { Reset(); return; }

        if (!_hasHistory || _prev == null || _prevCur == null)
        {
            _prev = new float[_n]; Array.Copy(alpha, _prev, _n);
            _prevCur = new float[_n]; Array.Copy(alpha, _prevCur, _n);
            _hasHistory = true;
            return;   // 第一帧原样采用(没有可比的历史)
        }

        for (int i = 0; i < _n; i++)
        {
            float cur = alpha[i], prev = _prev[i];

            // ③ 实心/全透明区直通:既避免"实心被慢慢衰减"拖出尾巴,也避免"透明被慢慢填上"粘住背景
            if (prev <= 0.001f || prev >= 0.999f || cur <= 0.001f || cur >= 0.999f)
            {
                alpha[i] = cur;
                _prev[i] = cur;
                _prevCur![i] = cur;
                continue;
            }

            // ① 帧差门控:差得越多越"跟手"(运动区少平滑)
            float diff = Math.Abs(cur - _prevCur![i]);
            float gate = Math.Clamp(diff * GateGain, 0f, 1f);
            float w = _k * (1f - gate);

            float blended = prev + (cur - prev) * (1f - w);
            alpha[i] = blended;
            _prev[i] = blended;
            _prevCur[i] = cur;
        }
    }
}
