

































































































































### Task 2: 输出规格决策表（`AlhPro.Core`）

**Files:**
- Create: `AlhPro.Core/MattingOutputSpec.cs`
- Test: `AlhPro.Tests/MattingOutputSpecTests.cs`

**Interfaces:**
- Consumes: 无
- Produces:
  - `public enum MattingOutputKind { Background, Transparent }`
  - `public sealed record MattingOutputSpec(string Container, string VideoCodec, string PixelFormat, string AudioCodec, string ExtraVideoArgs)`
  - `public static MattingOutputSpec MattingOutputSpecs.ForBackground(bool hevc)`
  - `public static MattingOutputSpec MattingOutputSpecs.ForTransparent(string container)`（`container` 取 `"webm"` / `"mov"`）

- [ ] **Step 1: 写失败测试**

```csharp
using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>视频抠图两条输出路径的规格表契约(2026-09-22)。
/// 【为什么钉死】透明通道编码器/pix_fmt 写错不会报错,只会"输出看起来是黑底"——
/// 用户与作者都很难察觉;这里把参数钉住,并在测试注释里写明为什么是这几个值。</summary>
public class MattingOutputSpecTests
{
    [Fact] public void Background_h264_is_mp4_with_audio_kept()
    {
        var s = MattingOutputSpecs.ForBackground(hevc: false);
        Assert.Equal("mp4", s.Container);
        Assert.Equal("libx264", s.VideoCodec);
        Assert.Equal("yuv420p", s.PixelFormat);   // 不带 alpha,兼容性最好
        Assert.Equal("aac", s.AudioCodec);        // 换背景路径保留音频
    }

    [Fact] public void Background_hevc_is_mp4_with_hevc()
    {
        var s = MattingOutputSpecs.ForBackground(hevc: true);
        Assert.Equal("mp4", s.Container);
        Assert.Equal("libx265", s.VideoCodec);
        Assert.Equal("yuv420p", s.PixelFormat);
    }

    [Fact] public void Transparent_webm_is_vp9_with_yuva420p()
    {
        var s = MattingOutputSpecs.ForTransparent("webm");
        Assert.Equal("webm", s.Container);
        Assert.Equal("libvpx-vp9", s.VideoCodec);
        Assert.Equal("yuva420p", s.PixelFormat);  // 带 alpha;实测该 ffmpeg 支持
        Assert.Equal("libopus", s.AudioCodec);
    }

    [Fact] public void Transparent_mov_is_prores4444_with_yuva444p10le()
    {
        var s = MattingOutputSpecs.ForTransparent("mov");
        Assert.Equal("mov", s.Container);
        Assert.Equal("prores_ks", s.VideoCodec);
        Assert.Equal("yuva444p10le", s.PixelFormat); // ProRes 4444 才带 alpha 且是 10bit
        Assert.Equal("pcm_s16le", s.AudioCodec);
    }

    [Fact] public void Unknown_container_falls_back_to_webm()
    {
        var s = MattingOutputSpecs.ForTransparent("mp4");  // mp4 装不了 alpha ⇒ 必须回落,不许"静默输出无 alpha 的 mp4"
        Assert.Equal("webm", s.Container);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test AlhPro.Tests\AlhPro.Tests.csproj -c Release --filter "FullyQualifiedName~MattingOutputSpecTests"`
Expected: 编译失败 / `MattingOutputSpecs` 不存在

- [ ] **Step 3: 最小实现**

```csharp
namespace AlhPro.Core;

/// <summary>视频抠图的两种输出形态。</summary>
public enum MattingOutputKind
{
    /// <summary>换背景:前景与背景合成后走普通 RGB 管线(可带音频)。</summary>
    Background,
    /// <summary>透明通道:前景 + alpha 双序列合成带 alpha 的视频。</summary>
    Transparent,
}

/// <summary>一路输出的编码规格(容器 / 视频编码 / 像素格式 / 音频编码 / 额外参数)。
/// 【为什么做成数据】透明输出最怕"看着成功、其实没带 alpha":容器与 pix_fmt 必须成对正确,
/// 集中成一张表 + 单测钉住,比散在 ffmpeg 命令行里安全。</summary>
public sealed record MattingOutputSpec(string Container, string VideoCodec, string PixelFormat,
    string AudioCodec, string ExtraVideoArgs);

/// <summary>输出规格决策表(实现见 docs/2026-09-22-video-matting.md §四④)。
/// 依据:本机 `发布版/engines/ffmpeg` 实测支持 libvpx-vp9 / prores_ks / yuva420p / yuva444p10le。</summary>
public static class MattingOutputSpecs
{
    /// <summary>换背景:普通 mp4,不带 alpha(兼容性优先:微信/抖音/B站/OBS 都能播)。</summary>
    public static MattingOutputSpec ForBackground(bool hevc)
        => hevc
            ? new("mp4", "libx265", "yuv420p", "aac", "-tag:v hvc1")
            : new("mp4", "libx264", "yuv420p", "aac", "-preset medium");

    /// <summary>透明通道:wp9-alpha(体积小、剪辑软件与 OBS 认)或 ProRes 4444(画质最高、体积约十倍)。
    /// 【mp4 必须回落 webm】mp4 标准容器装不了 alpha,若照原样输出会得到"看着是黑底"的假透明 ✗。</summary>
    public static MattingOutputSpec ForTransparent(string container)
        => (container ?? "").Trim().ToLowerInvariant() switch
        {
            "mov" => new("mov", "prores_ks", "yuva444p10le", "pcm_s16le", "-profile:v 4444"),
            _     => new("webm", "libvpx-vp9", "yuva420p", "libopus", "-auto-alt-ref 0"),
        };
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test AlhPro.Tests\AlhPro.Tests.csproj -c Release --filter "FullyQualifiedName~MattingOutputSpecTests"`
Expected: 5 项全 PASS

- [ ] **Step 5: 提交**

```powershell
git add AlhPro.Core/MattingOutputSpec.cs AlhPro.Tests/MattingOutputSpecTests.cs
git commit -m "video-matting: 输出规格决策表(换背景 mp4 / 透明 webm-vp9-alpha、mov-prores4444)+ 单测"
```

---

### Task 3: alpha 后处理（阈值 / 羽化 / 形态学）






















**Interfaces:**
- Consumes: 无
- Produces: `public static void VideoMatting.PostProcessAlpha(float[] alpha, int w, int h, int fg, int bg, int feather, int morph)`
  - `alpha` 为长度 `w*h` 的 0~1 浮点蒙版，**原地修改**
  - `fg`/`bg` 为 0~255 阈值（与图片抠图同口径，见全局约束）；`feather` 0~20；`morph` 0~100

- [ ] **Step 1: 写失败测试**

```csharp
using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>逐帧 alpha 后处理的口径契约(2026-09-22)。与图片抠图同口径:
/// 前景阈值以上算实心、背景阈值以下算透明,中间做羽化过渡。</summary>
public class VideoMattingAlphaTests
{
    [Fact] public void Thresholds_push_pixels_to_solid_or_transparent()
    {
        var a = new float[] { 0.10f, 0.50f, 0.95f };  // fg=200/255≈0.784, bg=64/255≈0.251
        VideoMatting.PostProcessAlpha(a, 3, 1, fg: 200, bg: 64, feather: 0, morph: 0);
        Assert.Equal(0f, a[0], 3);   // 低于背景阈值 ⇒ 完全透明
        Assert.Equal(1f, a[2], 3);   // 高于前景阈值 ⇒ 完全实心
        Assert.InRange(a[1], 0.01f, 0.99f);  // 中间区保留过渡(不被二值化)
    }

    [Fact] public void Feather_smooths_the_transition_band()
    {
        var hard = Enumerable.Range(0, 9).Select(i => i / 8f).ToArray();
        var soft = (float[])hard.Clone();
        VideoMatting.PostProcessAlpha(hard, 9, 1, fg: 255, bg: 0, feather: 0, morph: 0);
        VideoMatting.PostProcessAlpha(soft, 9, 1, fg: 255, bg: 0, feather: 6, morph: 0);
        // 羽化后相邻像素差应更小(更平滑),这是"边缘不割裂"的最小判据
        float dMax(float[] v) => Enumerable.Range(1, v.Length - 1).Max(i => Math.Abs(v[i] - v[i - 1]));
        Assert.True(dMax(soft) < dMax(hard), $"羽化没起作用:{dMax(soft)} 应 < {dMax(hard)}");
    }

    [Fact] public void Zero_parameters_is_identity()
    {
        var a = new float[] { 0f, 0.4f, 1f };
        var b = (float[])a.Clone();
        VideoMatting.PostProcessAlpha(a, 3, 1, fg: 0, bg: 0, feather: 0, morph: 0);
        Assert.Equal(b, a);  // fg=bg=0 表示"两个阈值都不启用"⇒ 原样返回
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test AlhPro.Tests\AlhPro.Tests.csproj -c Release --filter "FullyQualifiedName~VideoMattingAlphaTests"`
Expected: 编译失败 / `VideoMatting` 不存在

- [ ] **Step 3: 最小实现**

```csharp
namespace AlhPro.Core;

/// <summary>视频抠图的纯逻辑部分(时域滤波 / alpha 后处理 / 合成)。
/// 【为什么全放 Core】这些算法必须在无 UI 的环境里可测(见 docs/2026-09-22-video-matting.md §四)。</summary>
public static class VideoMatting
{
    /// <summary>逐帧 alpha 后处理:阈值 + 羽化 + 形态学(原地)。
    /// 口径与图片抠图一致:fg/bg 是 0~255 的阈值;fg=bg=0 表示两个阈值都关闭(原样返回)。
    /// 【为什么中间区不二值化】硬二值会让发丝/半透明边缘变成锯齿 ✗;保留过渡再交给羽化与合成更自然。</summary>
    public static void PostProcessAlpha(float[] alpha, int w, int h, int fg, int bg, int feather, int morph)
    {
        if (alpha == null || w <= 0 || h <= 0 || alpha.Length < w * h) return;
        if (fg <= 0 && bg <= 0 && feather <= 0 && morph <= 0) return;

        float lo = bg / 255f, hi = fg / 255f;
        if (fg > 0 && bg > 0 && hi > lo)
        {
            for (int i = 0; i < w * h; i++)
            {
                float v = alpha[i];
                if (v <= lo) alpha[i] = 0f;
                else if (v >= hi) alpha[i] = 1f;
                else alpha[i] = (v - lo) / (hi - lo);   // 线性拉伸到 0~1
            }
        }
        if (feather > 0) BoxBlurAlpha(alpha, w, h, Math.Max(1, feather / 2));
        if (morph > 0)  MorphOpenAlpha(alpha, w, h, Math.Max(1, morph / 25));   // 先腐蚀后膨胀,去孤立点
    }

    /// <summary>alpha 的方框模糊(可分离:先横后竖,复杂度 O(n·r))。
    /// 只用于"羽化"这一档,半径很小(≤10),所以不必上高斯。</summary>
    internal static void BoxBlurAlpha(float[] a, int w, int h, int r)
    {
        var tmp = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float s = 0; int n = 0;
                for (int k = -r; k <= r; k++) { int xx = x + k; if (xx < 0 || xx >= w) continue; s += a[y * w + xx]; n++; }
                tmp[y * w + x] = s / n;
            }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float s = 0; int n = 0;
                for (int k = -r; k <= r; k++) { int yy = y + k; if (yy < 0 || yy >= h) continue; s += tmp[yy * w + x]; n++; }
                a[y * w + x] = s / n;
            }
    }

    /// <summary>形态学开运算(先腐蚀后膨胀):削掉孤立的噪点与毛刺,保留主体形状。
    /// 【为什么用开运算而不是单纯腐蚀】腐蚀会整体瘦一圈 ✗;开运算只削孤立点,主体大小基本不变。</summary>
    internal static void MorphOpenAlpha(float[] a, int w, int h, int r)
    {
        var e = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float m = 1f;
                for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx < 0 || xx >= w || yy < 0 || yy >= h) continue;
                        m = Math.Min(m, a[yy * w + xx]);
                    }
                e[y * w + x] = m;
            }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float m = 0f;
                for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx < 0 || xx >= w || yy < 0 || yy >= h) continue;
                        m = Math.Max(m, e[yy * w + xx]);
                    }
                a[y * w + x] = m;
            }
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: 同 Step 2 命令
Expected: 3 项全 PASS

- [ ] **Step 5: 提交**

```powershell
git add AlhPro.Core/VideoMatting.cs AlhPro.Tests/VideoMattingAlphaTests.cs
git commit -m "video-matting: 逐帧 alpha 后处理(阈值/羽化/形态学,与图片抠图同口径)+ 单测"
```

---

### Task 4: 时序稳定（治逐帧抖动，本功能的核心）

**Files:**
- Modify: `AlhPro.Core/VideoMatting.cs`（追加 `AlphaTemporalFilter` 类）
- Test: `AlhPro.Tests/AlphaTemporalFilterTests.cs`

**Interfaces:**
- Consumes: Task 3 的 `VideoMatting`（同一文件）
- Produces:
  - `public sealed class AlphaTemporalFilter`
    - `public AlphaTemporalFilter(int stability, int w, int h)`（`stability` 0~100；默认 50 由调用方传）
    - `public void Reset()`（切点 / 新序列时调用，清空历史）
    - `public void Push(float[] alpha)`（原地滤波；内部保留上一帧 alpha 与上一帧灰度）
  - `public static bool VideoMatting.LooksLikeSceneCut(byte[] grayPrev, byte[] grayCur, double meanDiffThreshold = 25.0)`
    - 供调用方判断"这一帧要不要 Reset"（口径与 `SceneCutJudge` 的默认帧差阈值一致）

- [ ] **Step 1: 写失败测试**

```csharp
using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>时序稳定(视频抠图治抖动的核心)契约(2026-09-22)。三条判据,对应设计 §五的三个机制:
/// 静止更稳 / 真运动少拖影 / 切点必须重置。这三条是"能不能拿出去用"的分水岭。</summary>
public class AlphaTemporalFilterTests
{
    static float[] Frame(int n, float v) => Enumerable.Repeat(v, n).ToArray();

    [Fact] public void Static_noise_gets_suppressed()
    {
        var f = new AlphaTemporalFilter(stability: 80, w: 8, h: 1);
        var rnd = new Random(1234);
        float[] last = null!;
        for (int t = 0; t < 20; t++)
        {
            last = Frame(8, 0.5f).Select(v => v + (float)(rnd.NextDouble() - 0.5) * 0.2f).ToArray();
            f.Push(last);
        }
        float spread = last.Max() - last.Min();
        Assert.True(spread < 0.10f, $"静止序列被滤波后仍在抖:spread={spread:0.000}(判据 <0.10)");
    }

    [Fact] public void Real_motion_is_not_smeared()
    {
        var f = new AlphaTemporalFilter(stability: 80, w: 4, h: 1);
        f.Push(Frame(4, 0f));
        var cur = Frame(4, 1f);      // 突然整幅变实心 = 真运动/切换,滤波必须跟上
        f.Push(cur);
        Assert.True(cur.Min() > 0.5f, $"真运动被当成抖动抹掉了:min={cur.Min():0.00}(应 >0.5)");
    }

    [Fact] public void Reset_clears_history()
    {
        var f = new AlphaTemporalFilter(stability: 100, w: 4, h: 1);
        f.Push(Frame(4, 0f));
        f.Reset();
        var cur = Frame(4, 1f);
        f.Push(cur);
        Assert.Equal(1f, cur.Min(), 3);   // 重置后第一帧必须原样采用,不许掺旧历史
    }

    [Fact] public void Stability_zero_is_identity()
    {
        var f = new AlphaTemporalFilter(stability: 0, w: 4, h: 1);
        f.Push(Frame(4, 0f));
        var cur = Frame(4, 0.37f);
        f.Push(cur);
        Assert.Equal(0.37f, cur[0], 3);
    }

    [Fact] public void Scene_cut_detected_by_frame_difference()
    {
        var prev = Enumerable.Repeat((byte)10, 16).ToArray();
        var big  = Enumerable.Repeat((byte)200, 16).ToArray();
        Assert.True(VideoMatting.LooksLikeSceneCut(prev, big));
        Assert.False(VideoMatting.LooksLikeSceneCut(prev, Enumerable.Repeat((byte)12, 16).ToArray()));
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test AlhPro.Tests\AlhPro.Tests.csproj -c Release --filter "FullyQualifiedName~AlphaTemporalFilterTests"`
Expected: 编译失败 / `AlphaTemporalFilter` 不存在

- [ ] **Step 3: 最小实现（追加到 `AlhPro.Core/VideoMatting.cs`）**

```csharp
    /// <summary>这一帧是否像场景切换(帧差超过阈值)。口径与 SceneCutJudge 的默认帧差阈值(25)一致:
    /// 【为什么要它】时域滤波在切点必须 Reset,否则后一个镜头会继承前一个镜头的 alpha ⇒ 可见鬼影 ✗。</summary>
    public static bool LooksLikeSceneCut(byte[] grayPrev, byte[] grayCur, double meanDiffThreshold = 25.0)
    {
        if (grayPrev == null || grayCur == null) return false;
        int n = Math.Min(grayPrev.Length, grayCur.Length);
        if (n == 0) return false;
        long sum = 0;
        for (int i = 0; i < n; i++) sum += Math.Abs(grayCur[i] - grayPrev[i]);
        return sum / (double)n >= meanDiffThreshold;
    }
}

/// <summary>alpha 的时域滤波(治"逐帧独立推理导致的边缘闪烁/呼吸")。
/// 三个机制(见 docs/2026-09-22-video-matting.md §五):
///   ① 帧差门控的 EMA:静止区多平滑、真运动区少平滑 ⇒ 稳但不拖影;
///   ② 切点重置:由调用方用 LooksLikeSceneCut 判断后调用 Reset();
///   ③ 边缘带保护:只对 0&lt;alpha&lt;1 的过渡带做滤波,实心/全透明区直接取当前帧。
/// 【为什么用"归一化帧差"当门控】不需要光流(那要额外模型/算力);帧差足够区分"抖动"与"真运动"。</summary>
public sealed class AlphaTemporalFilter
{
    private readonly float _k;        // 平滑强度:0=不平滑,1=最强
    private readonly int _n;
    private float[]? _prev;           // 上一帧滤波结果
    private float[]? _prevCur;        // 上一帧的原始输入(用于算帧差)
    private bool _hasHistory;

    /// <param name="stability">稳定档 0~100(界面滑条;默认 50)。</param>
    public AlphaTemporalFilter(int stability, int w, int h)
    {
        _k = Math.Clamp(stability, 0, 100) / 100f * 0.9f;   // 上限 0.9:永远保留一点跟随性,避免"死住"
        _n = Math.Max(1, w * h);
    }

    public void Reset() { _hasHistory = false; _prev = null; _prevCur = null; }

    /// <summary>原地滤波一帧 alpha(长度 ≥ 构造时的 w*h)。</summary>
    public void Push(float[] alpha)
    {
        if (alpha == null || alpha.Length < _n) return;
        if (_k <= 0f) { _prev = null; _prevCur = null; _hasHistory = false; return; }
        if (!_hasHistory || _prev == null || _prevCur == null)
        {
            _prev = new float[_n]; Array.Copy(alpha, _prev, _n);
            _prevCur = new float[_n]; Array.Copy(alpha, _prevCur, _n);
            _hasHistory = true;
            return;
        }
        for (int i = 0; i < _n; i++)
        {
            float cur = alpha[i], prev = _prev[i];
            if (prev <= 0.001f || prev >= 0.999f || cur <= 0.001f || cur >= 0.999f)
            { _prev[i] = cur; _prevCur![i] = cur; continue; }   // ③ 非过渡带:直接采用,避免整体拖影
            float diff = Math.Abs(cur - _prevCur![i]);          // ① 帧差门控
            float gate = Math.Clamp(diff * 4f, 0f, 1f);         // 差得越多越"跟手"
            float w = _k * (1f - gate);
            float blended = prev + (cur - prev) * (1f - w);
            alpha[i] = blended;
            _prev[i] = blended; _prevCur[i] = cur;
        }
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: 同 Step 2 命令
Expected: 5 项全 PASS（若"静止更稳"未达标，先调大 `_k` 上限或把 `gate` 系数 4f 调小，再重跑；**不许改测试判据**）

- [ ] **Step 5: 提交**

```powershell
git add AlhPro.Core/VideoMatting.cs AlhPro.Tests/AlphaTemporalFilterTests.cs
git commit -m "video-matting: alpha 时域滤波(帧差门控 EMA + 切点重置 + 过渡带保护)+ 单测"
```

---

### Task 5: 前景 × alpha 与背景合成

**Files:**
- Modify: `AlhPro.Core/VideoMatting.cs`（追加 `Composite`）
- Test: `AlhPro.Tests/VideoMattingCompositeTests.cs`

**Interfaces:**
- Consumes: Task 3/4 的同一文件
- Produces: `public static void VideoMatting.Composite(byte[] fg, float[] alpha, byte[] bg, byte[] dst, int pixels)`
  - 三个输入都是 `pixels*3` 的 RGB 字节（BGR/RGB 顺序由调用方保证一致）——**fg 与 bg 长度不足或为 null 时直接返回，不抛异常**（视频长片里"某帧读失败"不该整段崩）


- [ ] **Step 1: 写失败测试**

```csharp
using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>前景×alpha 与背景的合成契约(2026-09-22)。换背景路径的正确性全靠它。</summary>
public class VideoMattingCompositeTests
{
    [Fact] public void Alpha_zero_shows_background_only()
    {
        var fg = new byte[] { 255, 0, 0 }; var bg = new byte[] { 0, 0, 255 };
        var dst = new byte[3]; var a = new float[] { 0f };
        VideoMatting.Composite(fg, a, bg, dst, 1);
        Assert.Equal(new byte[] { 0, 0, 255 }, dst);
    }

    [Fact] public void Alpha_one_shows_foreground_only()
    {
        var fg = new byte[] { 255, 0, 0 }; var bg = new byte[] { 0, 0, 255 };
        var dst = new byte[3]; var a = new float[] { 1f };
        VideoMatting.Composite(fg, a, bg, dst, 1);
        Assert.Equal(new byte[] { 255, 0, 0 }, dst);
    }

    [Fact] public void Alpha_half_blends_linearly()
    {
        var fg = new byte[] { 200, 100, 0 }; var bg = new byte[] { 0, 100, 200 };
        var dst = new byte[3]; var a = new float[] { 0.5f };
        VideoMatting.Composite(fg, a, bg, dst, 1);
        Assert.Equal(100, dst[0]); Assert.Equal(100, dst[1]); Assert.Equal(100, dst[2]);
    }

    [Fact] public void Short_input_is_ignored_not_crashing()
    {
        var dst = new byte[3];
        VideoMatting.Composite(new byte[1], new float[1], new byte[3], dst, 1);   // 不许抛
        Assert.Equal(new byte[3], dst);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test AlhPro.Tests\AlhPro.Tests.csproj -c Release --filter "FullyQualifiedName~VideoMattingCompositeTests"`
Expected: 编译失败 / `Composite` 不存在

- [ ] **Step 3: 最小实现**

```csharp
    /// <summary>把前景按 alpha 合成到背景上(原地写到 dst)。三路都是 pixels*3 的字节;
    /// 【为什么不抛异常】长视频里单帧解码失败很常见,调用方会跳过该帧 —— 这里静默返回即可,
    /// 让"跳过"由调用方统一决定与记录,而不是在纯算法层抛。</summary>
    public static void Composite(byte[] fg, float[] alpha, byte[] bg, byte[] dst, int pixels)
    {
        if (fg == null || bg == null || dst == null || alpha == null) return;
        if (pixels <= 0 || fg.Length < pixels * 3 || bg.Length < pixels * 3 || alpha.Length < pixels) return;
        for (int i = 0, p = 0; i < pixels; i++, p += 3)
        {
            float a = Math.Clamp(alpha[i], 0f, 1f);
            for (int c = 0; c < 3; c++)
                dst[p + c] = (byte)Math.Clamp(fg[p + c] * a + bg[p + c] * (1f - a) + 0.5f, 0f, 255f);
        }
    }
```

- [ ] **Step 4: 跑测试确认通过**

Run: 同 Step 2 命令
Expected: 4 项全 PASS

- [ ] **Step 5: 提交**

```powershell
git add AlhPro.Core/VideoMatting.cs AlhPro.Tests/VideoMattingCompositeTests.cs
git commit -m "video-matting: 前景×alpha 背景合成(纯函数,缺帧静默跳过)+ 单测"
```

---

### Task 6: 本阶段收尾（回归 + 文档钩子）

**Files:**
- Modify: `docs/2026-09-22-video-matting.md`（在 §九 末尾追加"实测结论"一行，指向报告）

- [ ] **Step 1: 跑全量测试，确认没打破基线**

Run: `dotnet test AlhPro.Tests\AlhPro.Tests.csproj -c Release`
Expected: 失败数仍为 **1**（既有 `NavLogoWebsiteTests`），总数 = 基线 + 本阶段新增（约 17 项）

- [ ] **Step 2: 把性能报告的两条结论写回设计与后续默认值位置**

在设计文档 §九 末尾追加：
```markdown
**实测结论（执行后填写）**：默认模型 = `___`；GPU 稳态 ___ ms/帧；ETA 系数 = ___ 秒/帧（1080p）。
详见 `_qa/视频抠图_性能实测.md`。
```
（第二步计划的默认值常量直接引用这两条。）

- [ ] **Step 3: 提交并推（网络不通就留在本地，并在总结里说明）**

```powershell
git add -A; git commit -m "video-matting: 第一阶段收尾(回归通过 + 实测结论回写设计文档)"
git push origin main
```

---

## 自审记录（写完计划后自查，已修正）

- **spec 覆盖**：§五 时序稳定 → Task 4；§六 参数与默认值 → Task 2/3 + 全局约束；§九 性能标定 → Task 1；§四 模块拆分中的 `AlhPro.Core/VideoMatting.cs` → Task 3/4/5；`MattingOutputSpec` → Task 2。**未覆盖**（属后续阶段，已在计划开头声明）：`VideoMattingService`、UI、ffmpeg 对接、文档与发版同步 —— 它们各自单独出计划。
- **占位符扫描**：无 TBD / "适当处理" 类空话；Task 1 里对 `CutoutAsync` 的真实签名标注了"以源码为准"而不是留空。
- **类型一致性**：`MattingOutputSpec` / `MattingOutputSpecs.For*` / `VideoMatting.PostProcessAlpha` / `Composite` / `AlphaTemporalFilter.Push|Reset` / `LooksLikeSceneCut` 在各任务中命名与签名一致。

