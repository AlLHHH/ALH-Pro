// EsrganOnnxService.cs — Real-ESRGAN ONNX 超分(纯 C#,ONNX Runtime,无 Python)
// 目的:50 系 Blackwell + CPU 都稳定的超分实现(ncnn-vulkan 老引擎在 50 系/CUDA 系崩溃)。
// 路径:引擎文件 realesrgan-ncnn-vulkan.exe(2022)在 50 系不可用,此服务用 ONNX 模型替代。
// CPU/GPU(DirectML)双模式,GPU 失败自动降 CPU(与 CutoutService 同策略)。
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ALHPro;

public static class EsrganOnnxService
{
    /// <summary>ONNX 超分模型路径:搜索 engines/rembg + engines/realesrgan + 程序根目录。
    /// (RealESRGAN_x4plus.onnx 已放 realesrgan 引擎目录——安装器排除 engines\rembg\*.onnx 是为 1.4GB 抠图模型,
    /// 超分 ONNX 模型不能只放 rembg 目录,否则安装版机器永远缺失,黑块降级 ONNX 会直接失败:真机 v1.1.1 已复现)</summary>
    public static string? FindModel()
    {
        var roots = new[]
        {
            Path.Combine(EngineService.EnginesDir, "rembg"),
            Path.Combine(EngineService.EnginesDir, "realesrgan"),
        };
        foreach (var f in new[] { "RealESRGAN_x4plus.onnx", "realesrgan-x4plus.onnx" })
        {
            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var found in Directory.EnumerateFiles(root, f, SearchOption.AllDirectories))
                    return found;
            }
            var direct = Path.Combine(EngineService.EnginesDir, f);
            if (File.Exists(direct)) return direct;
        }
        return null;
    }

    /// <summary>waifu2x ONNX 模型路径(engines/waifu2x/ waifu2x-cunet2x.onnx;nagadomi/nunif 官方导出)。
    /// 注意:该模型输入名为 x(不是 input)——用于核显/无独显设备(waifu2x ncnn CPU 模式有 bug 会崩)。</summary>
    public static string? FindWaifu2xModel()
    {
        var root = Path.Combine(EngineService.EnginesDir, "waifu2x");
        foreach (var f in new[] { "waifu2x-cunet2x.onnx", "waifu2x_cunet2x.onnx" })
        {
            foreach (var found in Directory.EnumerateFiles(root, f, SearchOption.AllDirectories))
                return found;
            var direct = Path.Combine(root, f);
            if (File.Exists(direct)) return direct;
        }
        return null;
    }

    /// <summary>动漫动画模型 ONNX(engines/realesrgan/ realesr-animevideov3.onnx;2.4MB)。
    /// 50系无独显走 ONNX 时也保持"动漫动画"画质(而非退到 x4plus 通用画质)。</summary>
    public static string? FindAnimeVideoModel()
    {
        var root = Path.Combine(EngineService.EnginesDir, "realesrgan");
        foreach (var f in new[] { "realesr-animevideov3.onnx", "RealESR-AnimeVideo-v3_x4.onnx" })
        {
            foreach (var found in Directory.EnumerateFiles(root, f, SearchOption.AllDirectories))
                return found;
            var direct = Path.Combine(root, f);
            if (File.Exists(direct)) return direct;
        }
        return null;
    }

    /// <summary>按所选 Real-ESRGAN 模型名解析对应的 ONNX 模型路径:动漫模型(名字含 anime,如 animevideov3/x4plus-anime)
    /// → 优先动漫 ONNX(保持动漫画质),无则通用;通用模型(x4plus)→ 通用 ONNX。
    /// 视频/图片多处 ONNX 兜底共用,避免与"只认 animevideo"的旧判断不一致导致动漫画质被降级。</summary>
    public static string? ResolveEsrganOnnxPath(string model)
    {
        // 动漫模型:realesr-animevideov3 与 realesrgan-x4plus-anime 均含 "anime"。
        // 通用 realesrgan-x4plus 不含 anime(不会误判),仍走通用。
        if (model.Contains("anime", StringComparison.OrdinalIgnoreCase))
            return FindAnimeVideoModel() ?? FindModel();
        return FindModel();
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, int), InferenceSession> _sessions = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, int), SemaphoreSlim> _locks = new();
    private static bool _dmlWarned;
    /// <summary>已确认不可用的 DirectML 设备(运行期失败):后续直接走 CPU,不再每帧/tile 重复一次失败调用。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> _dmlBad = new();

    // ---- DirectML 设备实测(名字匹配失败时的兜底;启动时后台探测一次)----
    private static int _dmlProbeState;   // 0=未做 1=进行中 2=完成
    private static int _dmlFirstOk = -1; // 第一个能创建 DirectML 会话的设备号

    /// <summary>后台探测 DML 设备 0..3 哪些能用(建会话成功即算可用;设备不可用/驱动缺会抛)。
    /// 结果供 EngineService.ToDmlDevice 在"名字匹配失败"时兜底(不越界、不跑错误设备)。
    /// 幂等;未完成/全失败返回 -1(调用方维持原行为)。</summary>
    public static async Task<int> EnsureDmlProbeAsync(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _dmlProbeState, 1, 0) != 0)
            return _dmlFirstOk;   // 已在做或被别人做过
        try
        {
            var model = FindModel();
            if (model == null) return _dmlFirstOk;
            for (int i = 0; i < 4; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var opts = new SessionOptions();
                    opts.AppendExecutionProvider_DML(i);
                    using var s = new InferenceSession(model, opts);   // DML 设备创建失败 → 抛 → 该号不可用
                    if (_dmlFirstOk < 0) _dmlFirstOk = i;
                }
                catch { }
            }
        }
        catch { }
        finally { Volatile.Write(ref _dmlProbeState, 2); }
        await Task.CompletedTask;   // 保持 async 签名(调用方统一 await;探测本身同步,已在后台任务中跑)
        return _dmlFirstOk;
    }

    /// <summary>实测可用的 DirectML 设备兜底号(-1=未探测/全部不可用)。</summary>
    public static int DmlFallbackOk => _dmlFirstOk;

    /// <summary>DirectML 不可用时的一次性明确提示(避免"静默掉 CPU → 慢几倍 → 以为不能用")。
    /// 常见于 RTX 50 系(Blackwell)但驱动较旧、或 AMD/Intel 驱动不完整。</summary>
    private static void WarnDmlUnavailable(string detail)
    {
        if (_dmlWarned) return;
        _dmlWarned = true;
        AppLogger.Warn($"⚠ GPU 加速(DirectML)不可用 — {detail}。已自动改用 CPU(稳定但慢数倍),建议更新显卡驱动(50 系需较新驱动)后重启软件再试。");
    }

    /// <summary>按输入尺寸选择 ONNX 推理设备:大图(>256px)→ DirectML GPU(快,实测 512→2048 快 7.7 倍);
    /// 小图 → CPU(小任务 GPU 启动开销 > 算力收益,实测 96px GPU 反而慢 13 倍)。
    /// wantsGpu=false(调用方要求纯 CPU,如抠图)时返回 -1。</summary>
    public static int PickDevice(int width, int height, bool wantsGpu = true)
    {
        if (!wantsGpu) return -1;
        int maxSide = Math.Max(width, height);
        if (maxSide <= 256) return -1;   // 小图:CPU 更快(不折腾 GPU)
        // 用户显式选了 CPU(GpuIndex<0):必须尊重(不按尺寸拉回 GPU)——GPU 有问题的机器正是这么选的
        if (AppSettings.GpuIndex < 0) return -1;
        // 【修复】判定 DirectML 是否可用:用"直接调用 DirectML 建会话"的实测结果(EnsureDmlProbeAsync → _dmlFirstOk),
        // 而不是用 VulkanCheck.GpuAvailable——Vulkan 与 DirectML 是两套完全不同的运行时,
        // 之前用 Vulkan 判定会在"DirectML 可用但 Vulkan 检测失败(无 Vulkan runtime/驱动缺/某 GPU Vulkan 支持不全)"
        // 的机器上误判为不可用,把 ONNX 超分/视频超分静默拖回 CPU(表现为:用户选了 GPU,实际 CPU 在跑)。
        // 探测结果有效(已完成)且确认无任何 DirectML 设备时才降 CPU;探测进行中/成功时走 GPU。
        if (_dmlProbeState == 2)
        {
            if (_dmlFirstOk < 0)
            {
                WarnDmlUnavailable("DirectML 无可用设备(启动自检/探测失败)");
                return -1;
            }
            // 有可用 DirectML 设备:优先用户选的 GpuIndex,否则用实测可用的第一个(避免越界/误判)
            int gpu = AppSettings.GpuIndex >= 0 ? AppSettings.GpuIndex : _dmlFirstOk;
            return gpu;
        }
        try
        {
            // 探测尚未完成(启动后极短暂窗口):保守判断,避免因 Vulkan 误判而否定 GPU
            if (!VulkanCheck.GpuAvailable && !EngineService.IsBlackwellGpu()) return -1;
        }
        catch { }
        int fallback = AppSettings.GpuIndex >= 0 ? AppSettings.GpuIndex : 0;
        return fallback;
    }

    /// <summary>ONNX 超分一张图(4x)。scale=目标倍数(4x 原生;2x 也走 4x 再缩回)。
    /// modelPath: 指定模型;null = Real-ESRGAN 自动查找。
    /// gpuId 传入 -2 表示"自动"(按输入大小选设备);其余按传入值。</summary>
    public static async Task UpscaleAsync(string input, string output, double scale,
        int gpuId = -1, IProgress<(int pct, string msg)>? progress = null, CancellationToken ct = default,
        string? modelPath = null, InferenceSession? sessionOverride = null)
    {
        modelPath ??= FindModel()
            ?? throw new FileNotFoundException(
                "未找到 RealESRGAN_x4plus.onnx,请放入 engines/rembg/ 目录(或程序目录)");
        // -2 = 自动选设备(按输入尺寸)
        try
        {
            if (gpuId == -2)
            {
                using var probe = new System.Drawing.Bitmap(input);
                gpuId = PickDevice(probe.Width, probe.Height);
            }
        }
        catch { }
        progress?.Report((5, "加载 ONNX 模型..."));
        await Task.Run(() => RunCore(input, output, scale, modelPath, gpuId, progress, ct, sessionOverride), ct);
        progress?.Report((100, "完成"));
    }

    /// <summary>ONNX 目录批处理(视频逐帧超分用):遍历 inputDir 的 PNG,逐帧 UpscaleAsync 输出到 outputDir。
    /// 供视频超分在 50 系/无独显设备走 ONNX(不走会崩的 ncnn-vulkan)。modelPath=null 用 Real-ESRGAN。
    /// 【并行优化】视频超分逐帧并行:早期串行(被共享缓存锁串行化,GPU 只用了单路)。
    /// 现用 2 路独立 DirectML 会话并行(实测 2 路 ≈1.25x,tile 分块下 GPU 算力爬升;3/4 路不再增益,仅显存吃紧)。
    /// 仍保留逐帧进度 + 取消,失败帧回退原帧(不中断)。</summary>
    public static async Task UpscaleDirAsync(string inputDir, string outputDir, double scale,
        int gpuId = -1, IProgress<(int pct, string msg)>? progress = null, CancellationToken ct = default,
        string? modelPath = null, int globalBaseFrames = 0, int globalTotalFrames = 0)
    {
        var files = Directory.EnumerateFiles(inputDir, "*.png")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        Directory.CreateDirectory(outputDir);
        if (files.Length == 0) return;
        modelPath ??= FindModel()
            ?? throw new FileNotFoundException("未找到 ONNX 超分模型");
        // -2 = 每帧自动选设备(视频帧通常大,落 GPU;小帧自动 CPU)
        bool auto = gpuId == -2;
        // 决定会话数:GPU 走 2 路并行(DirectML 多会话);CPU 保持 1(CPU 多会话每帧建会增加开销)
        bool wantGpu = auto ? true : gpuId >= 0;
        int concurrency = wantGpu ? 2 : 1;
        // 逐帧进度用【全局帧】(跨批次累计),显示"超分 第 N 帧 / 共 M 帧",百分比按全局帧算
        bool global = globalTotalFrames > 0;
        // 预创建独立会话池(每个并行 worker 一个;绕开共享缓存锁,支持并发 Run)
        var sessions = new Microsoft.ML.OnnxRuntime.InferenceSession?[concurrency];
        for (int s = 0; s < concurrency; s++)
        {
            try
            {
                var opts = new SessionOptions();
                if (wantGpu)
                {
                    try { opts.AppendExecutionProvider_DML(EngineService.ToDmlDevice(auto ? 0 : gpuId)); }
                    catch { /* DML 不可用回退 CPU */ }
                }
                sessions[s] = new Microsoft.ML.OnnxRuntime.InferenceSession(modelPath, opts);
            }
            catch { sessions[s] = null; }
        }
        try
        {
            int done = 0;
            // 【正确并行】每个 worker 独占一个 session, worker 之间分片处理帧 —— 保证同一个 session
            // 同一时刻只被一个 worker 用(同一 InferenceSession 不能并发 Run,否则 AccessViolation/OnnxRuntimeException)。
            var workers = new System.Threading.Tasks.Task[concurrency];
            for (int w = 0; w < concurrency; w++)
            {
                int wi = w;
                workers[w] = System.Threading.Tasks.Task.Run(() =>
                {
                    for (int i = wi; i < files.Length; i += concurrency)
                    {
                        ct.ThrowIfCancellationRequested();
                        var outPath = Path.Combine(outputDir, Path.GetFileName(files[i]));
                        try
                        {
                            var sess = sessions[wi];
                            UpscaleAsync(files[i], outPath, scale, auto ? -2 : gpuId, null, ct, modelPath, sess)
                                .GetAwaiter().GetResult();
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Warn($"ONNX 超分失败({ex.Message.Split('\n')[0]})——保留原帧");
                            try { File.Copy(files[i], outPath, true); } catch { }
                        }
                        finally
                        {
                            int d = System.Threading.Interlocked.Increment(ref done);
                            if (global)
                            {
                                // 全局逐帧进度:当前帧全局号 = globalBase(本批起始) + d(本批已完成)
                                int globalDone = globalBaseFrames + d;
                                int pct = (int)Math.Clamp(globalDone * 100.0 / globalTotalFrames, 0, 100);
                                progress?.Report((pct, $"超分 第 {globalDone} 帧 / 共 {globalTotalFrames} 帧"));
                            }
                            else
                            {
                                int pct = (int)(d * 100.0 / files.Length);
                                progress?.Report((pct, $"超分 {d}/{files.Length} 帧({Path.GetFileName(files[i])})"));
                            }
                        }
                    }
                }, ct);
            }
            await System.Threading.Tasks.Task.WhenAll(workers).ConfigureAwait(false);
        }
        finally
        {
            foreach (var s in sessions) try { s?.Dispose(); } catch { }
        }
    }

    private static void RunCore(string input, string output, double scale, string modelPath, int gpuId,
        IProgress<(int pct, string msg)>? progress, CancellationToken ct, InferenceSession? sessionOverride = null)
    {
        using var src = new System.Drawing.Bitmap(input);
        int sw = src.Width, sh = src.Height;
        int ow = (int)Math.Round(sw * scale), oh = (int)Math.Round(sh * scale);
        bool hasAlpha = src.PixelFormat.HasFlag(System.Drawing.Imaging.PixelFormat.Alpha)
            || src.PixelFormat == System.Drawing.Imaging.PixelFormat.Format32bppArgb;

        if (hasAlpha)
        {
            // ===== 透明底保护:ONNX 只处理 RGB(alpha 会丢/脏),这里分离处理 =====
            // ① 提取 alpha 通道(缩放后恢复用) ② RGB 填白(防透明区超分成脏色) ③ 超分 ④ 恢复 alpha
            RunCoreAlphaSafe(src, output, scale, modelPath, gpuId, progress, ct, TileFor(sw, sh), 32);
            return;
        }

        // ===== 分块保护(实测:GPU 整帧喂 1080p → DirectML OOM 崩溃;CPU 慢到 210s/帧)=====
        // 输入超 512 就切成块(带 32px 重叠羽化拼回):GPU 每块 0.3~0.5s,1080p 也稳;速度数倍提升。
        const int Tile = 512;
        const int Overlap = 32;
        if (sw > Tile || sh > Tile)
        {
            RunCoreTiled(src, output, scale, modelPath, gpuId, progress, ct, Tile, Overlap, sessionOverride);
            return;
        }

        // 单块(整图 ≤ Tile):直接推理
        using var tileBmp = RunTile(src, modelPath, gpuId, ct, sessionOverride);
        int tW = tileBmp.Width, tH = tileBmp.Height;
        if (Math.Abs(tW - ow) > 1 || Math.Abs(tH - oh) > 1)
            SaveScaled(tileBmp, output, ow, oh);
        else
            tileBmp.Save(output, System.Drawing.Imaging.ImageFormat.Png);
        progress?.Report((100, "完成"));
    }

    private static int TileFor(int w, int h) => Math.Max(w, h) > 512 ? 512 : Math.Max(w, h);

    /// <summary>透明底保护:提取 alpha → RGB 填白 → 超分 → 恢复 alpha(输出 32bpp Argb)。</summary>
    private static void RunCoreAlphaSafe(System.Drawing.Bitmap src, string output, double scale, string modelPath,
        int gpuId, IProgress<(int pct, string msg)>? progress, CancellationToken ct, int tile, int overlap,
        InferenceSession? sessionOverride = null)
    {
        int sw = src.Width, sh = src.Height;
        int ow = (int)Math.Round(sw * scale), oh = (int)Math.Round(sh * scale);
        // 提取 alpha(经图像缩放,带插值,透明边缘柔和)
        using var alphaBmp = new System.Drawing.Bitmap(sw, sh, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(alphaBmp))
        {
            g.Clear(System.Drawing.Color.Transparent);
            g.DrawImage(src, 0, 0);
        }
        // RGB 填白(透明区 → 白,防 ONNX 把透明区超分成黑边/脏色)
        using var rgbSrc = new System.Drawing.Bitmap(sw, sh, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (var g = System.Drawing.Graphics.FromImage(rgbSrc))
        {
            g.Clear(System.Drawing.Color.White);
            g.DrawImage(src, 0, 0);
        }
        // 超分 RGB(临时文件)
        var tmpRgb = Path.Combine(EngineService.TempRoot, $"alh_alpha_{Guid.NewGuid():N}.png");
        try
        {
            if (sw > tile || sh > tile)
                RunCoreTiled(rgbSrc, tmpRgb, scale, modelPath, gpuId, null, ct, tile, overlap, sessionOverride);
            else
            {
                using var tileBmp = RunTile(rgbSrc, modelPath, gpuId, ct, sessionOverride);
                if (Math.Abs(tileBmp.Width - ow) > 1 || Math.Abs(tileBmp.Height - oh) > 1)
                    SaveScaled(tileBmp, tmpRgb, ow, oh);
                else
                    tileBmp.Save(tmpRgb, System.Drawing.Imaging.ImageFormat.Png);
            }
            // 输出 32bpp:超分 RGB + 缩放后的 alpha(高效:LockBits 指针写 BGRA,不用逐像素 SetPixel)
            using var outBmp = new System.Drawing.Bitmap(ow, oh, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var rgbFull = new System.Drawing.Bitmap(tmpRgb);
            using var scaledAlpha = new System.Drawing.Bitmap(alphaBmp, ow, oh);   // 缩放 alpha(带插值)
            unsafe
            {
                var rect = new System.Drawing.Rectangle(0, 0, ow, oh);
                var dstData = outBmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var rgbData = rgbFull.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                    System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                var aData = scaledAlpha.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try
                {
                    byte* dst = (byte*)dstData.Scan0;
                    byte* rgb = (byte*)rgbData.Scan0;
                    byte* alpha = (byte*)aData.Scan0;
                    for (int y = 0; y < oh; y++)
                    {
                        byte* dRow = dst + y * dstData.Stride;
                        byte* rRow = rgb + y * rgbData.Stride;
                        byte* aRow = alpha + y * aData.Stride;
                        for (int x = 0; x < ow; x++)
                        {
                            dRow[x * 4] = rRow[x * 3];         // B
                            dRow[x * 4 + 1] = rRow[x * 3 + 1]; // G
                            dRow[x * 4 + 2] = rRow[x * 3 + 2]; // R
                            dRow[x * 4 + 3] = aRow[x * 4 + 3]; // A(从缩放 alpha 取)
                        }
                    }
                }
                finally
                {
                    outBmp.UnlockBits(dstData);
                    rgbFull.UnlockBits(rgbData);
                    scaledAlpha.UnlockBits(aData);
                }
            }
            outBmp.Save(output, System.Drawing.Imaging.ImageFormat.Png);
        }
        finally
        {
            try { File.Delete(tmpRgb); } catch { }
        }
        progress?.Report((100, "完成"));
    }

    /// <summary>分块超分:大图切 Tile 网格(步长=Tile-Overlap),逐块推理后按"核心区"贴回(重叠区相邻块覆盖,无接缝)。</summary>
    private static void RunCoreTiled(System.Drawing.Bitmap src, string output, double scale, string modelPath,
        int gpuId, IProgress<(int pct, string msg)>? progress, CancellationToken ct, int tile, int overlap,
        InferenceSession? sessionOverride = null)
    {
        int sw = src.Width, sh = src.Height;
        int ow = (int)Math.Round(sw * scale), oh = (int)Math.Round(sh * scale);
        using var outBmp = new System.Drawing.Bitmap(ow, oh, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (var g = System.Drawing.Graphics.FromImage(outBmp))
            g.Clear(System.Drawing.Color.Black);

        int stride = tile - overlap;
        int cols = (sw + stride - 1) / stride;
        int rows = (sh + stride - 1) / stride;
        int total = cols * rows;
        progress?.Report((5, $"大图分块: {cols}×{rows}={total} 块(超分 4x,自动分块防爆显存)..."));

        int done = 0;
        for (int ty = 0; ty < rows; ty++)
        {
            for (int tx = 0; tx < cols; tx++)
            {
                ct.ThrowIfCancellationRequested();
                int x0 = tx * stride, y0 = ty * stride;
                int tw = Math.Min(tile, sw - x0), th = Math.Min(tile, sh - y0);
                using (var cropped = new System.Drawing.Bitmap(tw, th, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
                {
                    using (var g = System.Drawing.Graphics.FromImage(cropped))
                        g.DrawImage(src, new System.Drawing.Rectangle(0, 0, tw, th),
                            new System.Drawing.Rectangle(x0, y0, tw, th), System.Drawing.GraphicsUnit.Pixel);
                    using var tileOut = RunTile(cropped, modelPath, gpuId, ct, sessionOverride);
                    // 贴回核心区(去掉 overlap 半宽)
                    int coreX0 = tx == 0 ? 0 : overlap / 2;
                    int coreY0 = ty == 0 ? 0 : overlap / 2;
                    int coreW = Math.Min(tileOut.Width - coreX0, ow - (int)(x0 * scale) - coreX0);
                    int coreH = Math.Min(tileOut.Height - coreY0, oh - (int)(y0 * scale) - coreY0);
                    if (coreW > 0 && coreH > 0)
                    {
                        using (var g = System.Drawing.Graphics.FromImage(outBmp))
                            g.DrawImage(tileOut,
                                new System.Drawing.Rectangle((int)(x0 * scale) + coreX0, (int)(y0 * scale) + coreY0, coreW, coreH),
                                new System.Drawing.Rectangle(coreX0, coreY0, coreW, coreH),
                                System.Drawing.GraphicsUnit.Pixel);
                    }
                }
                done++;
                progress?.Report((5 + (int)(85.0 * done / total), $"AI 超分 已处理 {done}/{total} 块..."));
            }
        }

        // 缩放到目标(模型 4x;要 2x/3x 时缩回)
        if (Math.Abs(ow - outBmp.Width) > 1 || Math.Abs(oh - outBmp.Height) > 1)
            SaveScaled(outBmp, output, ow, oh);
        else
            outBmp.Save(output, System.Drawing.Imaging.ImageFormat.Png);
        progress?.Report((100, "完成"));
    }

    /// <summary>单块推理(返回 4x 结果位图)。会话按 (modelPath,gpuId) 缓存;GPU 失败自动 CPU 重试。
    /// waifu2x 模型(文件名含 waifu2x)输入名为 x(非 input),其余模型为 input。</summary>
    private static System.Drawing.Bitmap RunTile(System.Drawing.Bitmap src, string modelPath, int gpuId, CancellationToken ct,
        InferenceSession? sessionOverride = null)
    {
        int inW = src.Width, inH = src.Height;
        var pixels = new float[1 * 3 * inH * inW];
        FillPixelArray(src, pixels, inW, inH);
        var inputTensor = new DenseTensor<float>(pixels, new[] { 1, 3, inH, inW });

        // waifu2x 模型输入名是 x(实测 ONNX 元数据);其余(esrgan/cugan/animevideo)是 input
        string inputName = modelPath.Contains("waifu2x", StringComparison.OrdinalIgnoreCase) ? "x" : "input";

        // 运行期已确认失败的 DirectML 设备:直接 CPU(该设备会话已丢弃,不再重复失败调用)
        if (gpuId >= 0 && _dmlBad.ContainsKey(gpuId))
        {
            gpuId = -1;
        }

        // 【并行优化】sessionOverride 非空:直接用调用方传入的独立会话(绕开共享缓存锁,支持多 session 并行),
        // 供 UpscaleDirAsync 并行超分用;否则按原按 (modelPath,gpuId) 缓存单会话 + 锁串行化(单图/单块路径不变)。
        InferenceSession session;
        var key = (modelPath, gpuId);
        SemaphoreSlim? gate = null;
        if (sessionOverride != null)
        {
            session = sessionOverride;
        }
        else
        {
            gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            gate.Wait();
            try
            {
                session = _sessions.GetOrAdd(key, _ =>
                {
                    var opts = new SessionOptions();
                    if (gpuId >= 0)
                    {
                        try { opts.AppendExecutionProvider_DML(EngineService.ToDmlDevice(gpuId)); }
                        catch (Exception dmlEx) { WarnDmlUnavailable("创建 GPU 会话失败: " + dmlEx.Message.Split('\n')[0]); }
                    }
                    return new InferenceSession(modelPath, opts);
                });
            }
            finally { gate.Release(); }
        }

        var inputs = new[] { NamedOnnxValue.CreateFromTensor(inputName, inputTensor) };
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue>? results = null;
        try
        {
            try
            {
                if (sessionOverride != null)
                {
                    // 并行路径:session 来自调用方(独立会话),无需共享锁,直接推理
                    results = session.Run(inputs);
                }
                else
                {
                    gate!.Wait();
                    try { results = session.Run(inputs); }
                    finally { gate!.Release(); }
                }
            }
            catch (Exception ex) when (gpuId >= 0)
            {
                // 并行路径(sessionOverride):独立会话,失败直接抛出(上层回退原帧),不进入缓存 key 回退
                if (sessionOverride != null)
                {
                    throw new InvalidOperationException($"ONNX 超分失败(并行会话): {ex.Message}", ex);
                }
                // DirectML 失败 → 换 CPU 会话重试(缓存独立 CPU 会话;与 GPU 会话互不干扰);
                // 并标记该设备不可用:后续帧直接 CPU,不做无谓的失败调用
                WarnDmlUnavailable("推理失败: " + ex.Message.Split('\n')[0]);
                _dmlBad[gpuId] = 0;
                // 【修复】Remove+Dispose 持 key 锁(否则另一线程可能 Run 已 Dispose 的会话 → ObjectDisposed)
                gate!.Wait();
                try { if (_sessions.TryRemove(key, out var gone)) gone.Dispose(); }
                finally { gate!.Release(); }
                var cpuKey = (modelPath, -1);
                var cpuGate = _locks.GetOrAdd(cpuKey, _ => new SemaphoreSlim(1, 1));
                cpuGate.Wait();
                try
                {
                    var cpuSession = _sessions.GetOrAdd(cpuKey, _ => new InferenceSession(modelPath, new SessionOptions()));
                    results = cpuSession.Run(inputs);
                }
                catch (Exception cpuEx) { throw new InvalidOperationException($"ONNX 超分失败(GPU+CPU 均失败): {cpuEx.Message}\n--\n{ex.Message}"); }
                finally { cpuGate.Release(); }
            }

            var outTensor = results!.First().AsTensor<float>();
            var dims = outTensor.Dimensions;
            if (dims.Length != 4 || dims[1] != 3)
                throw new InvalidOperationException($"ONNX 输出形状异常: {string.Join("x", dims.ToArray())}");
            int oH = dims[2], oW = dims[3];

            var dst = new System.Drawing.Bitmap(oW, oH, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            var rect = new System.Drawing.Rectangle(0, 0, oW, oH);
            var data = dst.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly,
                System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            try
            {
                unsafe
                {
                    var ptr = (byte*)data.Scan0.ToPointer();
                    int stride = data.Stride;
                    for (int y = 0; y < oH; y++)
                    {
                        for (int x = 0; x < oW; x++)
                        {
                            int idx = (y * oW + x);
                            float r = outTensor[0, 0, y, x];
                            float g = outTensor[0, 1, y, x];
                            float b = outTensor[0, 2, y, x];
                            byte* px = ptr + y * stride + x * 3;
                            px[0] = (byte)Math.Clamp((int)Math.Round(b * 255f), 0, 255);
                            px[1] = (byte)Math.Clamp((int)Math.Round(g * 255f), 0, 255);
                            px[2] = (byte)Math.Clamp((int)Math.Round(r * 255f), 0, 255);
                        }
                    }
                }
            }
            finally { dst.UnlockBits(data); }
            return dst;
        }
        finally
        {
            if (results != null)
                foreach (var r in results) r.Dispose();
        }
    }

    private static void FillPixelArray(System.Drawing.Bitmap src, float[] tensor, int w, int h)
    {
        var rect = new System.Drawing.Rectangle(0, 0, w, h);
        var data = src.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        try
        {
            unsafe
            {
                var ptr = (byte*)data.Scan0.ToPointer();
                int stride = data.Stride;
                int plane = w * h;
                for (int y = 0; y < h; y++)
                {
                    byte* row = ptr + y * stride;
                    for (int x = 0; x < w; x++)
                    {
                        int idx = y * w + x;
                        tensor[idx] = row[x * 3 + 2] / 255f;               // R
                        tensor[plane + idx] = row[x * 3 + 1] / 255f;      // G
                        tensor[plane * 2 + idx] = row[x * 3 + 0] / 255f;  // B
                    }
                }
            }
        }
        finally { src.UnlockBits(data); }
    }

    private static void SaveScaled(System.Drawing.Bitmap src, string output, int ow, int oh)
    {
        using var dst = new System.Drawing.Bitmap(Math.Max(1, ow), Math.Max(1, oh));
        using var g = System.Drawing.Graphics.FromImage(dst);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
        g.DrawImage(src, 0, 0, dst.Width, dst.Height);
        dst.Save(output, System.Drawing.Imaging.ImageFormat.Png);
    }
}
