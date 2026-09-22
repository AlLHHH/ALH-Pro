# 视频抠图 · 第一阶段（性能标定 + Core 纯逻辑）实现计划

> **给执行者：** 本计划按任务逐条执行。步骤用 `- [ ]` 勾选跟踪。
> **设计依据必须先读：** `docs/2026-09-22-video-matting.md`（设计方案，本计划只实现它的 §五/§六/§九 三个部分）

**目标：** 交付三样可独立验证的东西 —— ①性能实测报告（决定默认模型与 ETA 系数）②`AlhPro.Core` 里的纯逻辑模块（时序滤波 / alpha 后处理 / 合成 / 输出规格决策）③它们的单元测试。

**做法（架构）：** 所有图像处理逻辑放 `AlhPro.Core`（纯函数 + 一个有状态滤波器），**不碰 WinUI**；因此可以在 `AlhPro.Tests` 里完整单测——这是本项目"没实测不许说能用"红线的落点。ONNX 推理与 ffmpeg 都**不在本阶段**（它们在 `ImgUpscalerUI`，属于第二阶段计划）。

**技术栈：** .NET 8、C# 12、xUnit（`AlhPro.Tests`）、`_qa/` 独立小工具（反射加载 `ALHPro.dll`，照 `_qa/onnxpix` 套路）。

**Spec：** `docs/2026-09-22-video-matting.md`

## 全局约束（每个任务都隐含遵守）

- **不引入任何新 NuGet 包 / 新模型权重 / 新二进制**（许可红线：RVM 是 GPL-3.0，已否决）。
- **`AlhPro.Core` 不许引用 WinUI / WindowsAppSDK**（`AlhPro.Tests` 只引用 Core，引了会构建不了）。
- **中文注释、中文日志文案**，与仓库现有风格一致（"为什么这样做"必须写进注释）。
- 抠图参数的**取值范围与默认值必须与图片抠图同口径**：前景阈值 0~255、背景阈值 0~255、羽化 0~20、边缘增强 0~100、形态学 0~100。
- 时序稳定档 **0~100，默认 50**（中档）。
- 测试命令统一：`dotnet test AlhPro.Tests\AlhPro.Tests.csproj -c Release --filter "FullyQualifiedName~<类名>"`
- 基线：全量测试 **1074 项 / 1 项既有失败**（`NavLogoWebsiteTests`）；改动后失败数不许增加。

---

### Task 1: 性能标定工具 `_qa/mattingbench`

**Files:**
- Create: `_qa/mattingbench/mattingbench.csproj`
- Create: `_qa/mattingbench/Program.cs`
- Create（产出）: `_qa/视频抠图_性能实测.md`

**Interfaces:**
- Consumes: `发布版/ALHPro.dll` 里的 `ALHPro.CutoutService`（反射调用，签名见下）
- Produces: 一份实测报告，决定后续阶段的**默认模型**与**ETA 系数**

> 反射调用的目标签名（来自 `ImgUpscalerUI/CutoutService.cs:77`，参数名以实际为准）：
> `public static async Task<string> CutoutAsync(string input, string output, string modelKey, int fg, int bg, int feather, int edge, int morph, int gpuId, IProgress<(int,string)> progress, CancellationToken ct)`
> **执行者注意：** 先打开 `CutoutService.cs` 核对该方法的**真实参数顺序**，按真实签名传参；不要照抄本行的猜测。若签名不同，以源码为准并把差异记进报告。

- [ ] **Step 1: 建工具工程（照 `_qa/onnxpix` 的 csproj 套路）**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <!-- 视频抠图性能标定(2026-09-22):反射加载发布版 ALHPro.dll,直接测产品代码路径的 ms/帧。
       与 _qa\onnxpix 同款:独立小工具,不进主解决方案、不进装机包(_qa\ 已被 gitignore)。 -->
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <LangVersion>12</LangVersion>
    <AssemblyName>MattingBench</AssemblyName>
    <RootNamespace>MattingBench</RootNamespace>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
    <Platforms>AnyCPU;x64</Platforms>
    <PlatformTarget>x64</PlatformTarget>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <SelfContained>false</SelfContained>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="System.Drawing.Common" Version="8.0.0" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: 写测量主程序**

要求（写成 `Program.cs`，参数：`<ALHPro.dll 所在目录> <探针帧图片> [帧数=6]`）：
1. 用 `Assembly.LoadFrom(Path.Combine(dllDir, "ALHPro.dll"))` 反射取 `ALHPro.CutoutService`；
2. 对**四个模型**（`birefnet-lite` / `isnet-general-use` / `u2net` / `u2netp`）× **两个设备**（`gpuId = 0`（DirectML）、`gpuId = -1`（CPU））各跑 `帧数` 帧（同一张 1080p 探针图，循环调用以摊薄冷启动）；
3. 记录：首帧耗时、稳态平均 ms/帧、异常/回退信息（直接 `Console.WriteLine`，中文）；
4. CPU 那一档如果单帧超过 30 秒就打印"CPU 太慢，跳过剩余帧"并停止该组合（避免一跑半小时）；
5. 结果按 CSV 风格打印，便于粘进报告。

```csharp
// 关键片段(执行者按真实签名补全其余参数)
using System.Diagnostics;
using System.Reflection;

static async Task<(double first, double avg, string note)> Measure(
    MethodInfo cutout, string modelKey, string inPath, string outDir, int gpuId, int frames)
{
    double first = 0, sum = 0; int done = 0; string note = "";
    for (int i = 0; i < frames; i++)
    {
        var outPath = Path.Combine(outDir, $"{modelKey}_g{gpuId}_{i}.png");
        var sw = Stopwatch.StartNew();
        try
        {
            var task = (Task)cutout.Invoke(null, new object[] { inPath, outPath, modelKey,
                /*fg*/0, /*bg*/0, /*feather*/0, /*edge*/0, /*morph*/0, gpuId, null!, CancellationToken.None })!;
            await task;
        }
        catch (TargetInvocationException ex) { note = "调用异常:" + ex.InnerException?.Message; break; }
        sw.Stop();
        if (i == 0) first = sw.Elapsed.TotalMilliseconds;
        sum += sw.Elapsed.TotalMilliseconds; done++;
        if (gpuId < 0 && sw.Elapsed.TotalSeconds > 30) { note = "CPU 太慢,已提前停止"; break; }
    }
    return (first, done > 0 ? sum / done : -1, note);
}
```

- [ ] **Step 3: 跑一遍并核对**

Run:
```powershell
cd D:\deep\alh-pro
dotnet run -c Release --project _qa\mattingbench -- "发布版" "C:\Users\AlL\Desktop\GIRL LIKE ME_1.mp4_20260914_192004.920.jpg" 6
```
（探针图不存在就先用 ffmpeg 从 `GIRL LIKE ME_1.mp4` 抽一帧成 PNG：`发布版\engines\ffmpeg\ffmpeg.exe -y -i "C:\Users\AlL\Desktop\GIRL LIKE ME_1.mp4" -frames:v 1 -ss 1 probe.png`）

Expected：打印出 8 行结果（4 模型 × 2 设备），GPU 档明显快于 CPU 档；若 GPU 档报错会带 DirectML 回退提示。

- [ ] **Step 4: 写报告 `_qa/视频抠图_性能实测.md`**

必须包含：测量日期、机器（RTX 4060 Laptop）、探针图分辨率、**8 组数字的表格**、以及两条结论：
① **默认模型**选哪个（若 `birefnet-lite` 的 GPU ms/帧 ≤ 500ms 则仍选它；否则降 `isnet-general-use` / `u2netp`）；
② **ETA 系数**（用于第二阶段按帧估算："已处理 N/M 帧 · 预计还需 X"）。

- [ ] **Step 5: 提交**

```powershell
git add -A; git commit -m "video-matting: 性能标定工具与实测报告(4 模型 × GPU/CPU × 1080p)"
```
（`_qa/` 已被 `.gitignore` 覆盖 ⇒ 实际只会提交到本地工作树；**报告结论要复制进第二阶段的默认值常量**，别只留在本地。）

---

### Task 2: 输出规格决策表（`AlhPro.Core`）—— 已执行（2026-09-22），含两处对计划的修正

> **执行记录（2026-09-22，提交见 git log「video-matting: 输出规格决策表」）**
> 1. **修正一：换背景一路不写死编码器。** 计划里 `ForBackground(hevc)` 要返回 `libx264`/`libx265`，
>    但仓库里已经有一套自适应编码器策略（`VideoService.EncoderArgs` / `SelectVideoEncoder`：
>    厂商硬编 → 任一硬编 → libx264/libx265，带探针实测）。再写一份就会出现"视频页走 NVENC、
>    抠图页走软编"的双策略漂移。改成 `UseGlobalEncoder = true` + 空的 `VideoCodec`，
>    只钉死 `pix_fmt=yuv420p`、`-c:a copy`、`+faststart`。
> 2. **修正二：音频字段改成 ffmpeg 参数片段**（`AudioArgs`），并显式区分：换背景 `-c:a copy`（应用惯例，
>    `VideoService.cs:3715`），透明通道 `-c:a libopus` / `-c:a pcm_s16le` —— 两种透明容器都装不了 aac，
>    照抄 copy 会直接失败或丢音轨。
> 3. **实测补强（编码往返，见 `_qa/视频抠图_性能实测_20260922.md` §四）**：两种容器的 alpha 都真实存在
>    （抽出 alpha 平面数点：2400 透明 / 2400 不透明，与源图一致）；但发现两个坑并写进代码注释 ——
>    ① WebM 的 alpha 不在 `pix_fmt` 里（ffprobe 仍报 yuv420p，靠 `alpha_mode=1` 承载），
>    ② **ffmpeg 原生 `vp9` 解码器会静默丢 alpha**，读回必须 `-c:v libvpx-vp9`。
> 4. 测试从计划的 5 条扩到 18 条（含容器回落、归一化、"永远不能返回不带 alpha 的组合"、
>    "界面下拉的容器名必须都被实现"）。

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

**Files:**
- Create: `AlhPro.Core/VideoMatting.cs`（本任务只放 `PostProcessAlpha`）
- Test: `AlhPro.Tests/VideoMattingAlphaTests.cs`

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
