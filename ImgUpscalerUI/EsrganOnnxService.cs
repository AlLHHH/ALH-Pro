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
    /// <summary>ONNX 模型路径(engines/rembg/ 下或独立):先找 engines/esrgan-*/RealESRGAN_x4plus.onnx。</summary>
    public static string? FindModel()
    {
        foreach (var f in new[] { "RealESRGAN_x4plus.onnx", "realesrgan-x4plus.onnx" })
        {
            var root = Path.Combine(EngineService.EnginesDir, "rembg");
            foreach (var found in Directory.EnumerateFiles(root, f, SearchOption.AllDirectories))
                return found;
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

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, int), InferenceSession> _sessions = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, int), SemaphoreSlim> _locks = new();
    private static bool _dmlWarned;

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
        try
        {
            // DirectML 不可用(无独显/驱动缺)时降 CPU;可用则 GPU
            if (!VulkanCheck.GpuAvailable && !EngineService.IsBlackwellGpu()) return -1;
        }
        catch { }
        int gpu = AppSettings.GpuIndex >= 0 ? AppSettings.GpuIndex : 0;
        return gpu;
    }

    /// <summary>ONNX 超分一张图(4x)。scale=目标倍数(4x 原生;2x 也走 4x 再缩回)。
    /// modelPath: 指定模型;null = Real-ESRGAN 自动查找。
    /// gpuId 传入 -2 表示"自动"(按输入大小选设备);其余按传入值。</summary>
    public static async Task UpscaleAsync(string input, string output, double scale,
        int gpuId = -1, IProgress<(int pct, string msg)>? progress = null, CancellationToken ct = default,
        string? modelPath = null)
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
        await Task.Run(() => RunCore(input, output, scale, modelPath, gpuId, progress, ct), ct);
        progress?.Report((100, "完成"));
    }

    /// <summary>ONNX 目录批处理(视频逐帧超分用):遍历 inputDir 的 PNG,逐帧 UpscaleAsync 输出到 outputDir。
    /// 供视频超分在 50 系/无独显设备走 ONNX(不走会崩的 ncnn-vulkan)。modelPath=null 用 Real-ESRGAN。
    /// 串行单路(实测 CPU 并发需每 worker 固定会话,否则每次切线程重建 session 反而慢 3 倍——保持简单可靠)。</summary>
    public static async Task UpscaleDirAsync(string inputDir, string outputDir, double scale,
        int gpuId = -1, IProgress<(int pct, string msg)>? progress = null, CancellationToken ct = default,
        string? modelPath = null)
    {
        var files = Directory.EnumerateFiles(inputDir, "*.png")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        Directory.CreateDirectory(outputDir);
        if (files.Length == 0) return;
        // -2 = 每帧自动选设备(视频帧通常大,落 GPU;小帧自动 CPU)
        bool auto = gpuId == -2;
        for (int i = 0; i < files.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var outPath = Path.Combine(outputDir, Path.GetFileName(files[i]));
            await UpscaleAsync(files[i], outPath, scale, auto ? -2 : gpuId, null, ct, modelPath).ConfigureAwait(false);
            progress?.Report(((int)((i + 1) * 100.0 / files.Length),
                $"超分 {i + 1}/{files.Length} 帧({Path.GetFileName(files[i])})"));
        }
    }

    private static void RunCore(string input, string output, double scale, string modelPath, int gpuId,
        IProgress<(int pct, string msg)>? progress, CancellationToken ct)
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
            RunCoreTiled(src, output, scale, modelPath, gpuId, progress, ct, Tile, Overlap);
            return;
        }

        // 单块(整图 ≤ Tile):直接推理
        using var tileBmp = RunTile(src, modelPath, gpuId, ct);
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
        int gpuId, IProgress<(int pct, string msg)>? progress, CancellationToken ct, int tile, int overlap)
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
                RunCoreTiled(rgbSrc, tmpRgb, scale, modelPath, gpuId, null, ct, tile, overlap);
            else
            {
                using var tileBmp = RunTile(rgbSrc, modelPath, gpuId, ct);
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
        int gpuId, IProgress<(int pct, string msg)>? progress, CancellationToken ct, int tile, int overlap)
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
                    using var tileOut = RunTile(cropped, modelPath, gpuId, ct);
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
    private static System.Drawing.Bitmap RunTile(System.Drawing.Bitmap src, string modelPath, int gpuId, CancellationToken ct)
    {
        int inW = src.Width, inH = src.Height;
        var pixels = new float[1 * 3 * inH * inW];
        FillPixelArray(src, pixels, inW, inH);
        var inputTensor = new DenseTensor<float>(pixels, new[] { 1, 3, inH, inW });

        // waifu2x 模型输入名是 x(实测 ONNX 元数据);其余(esrgan/cugan/animevideo)是 input
        string inputName = modelPath.Contains("waifu2x", StringComparison.OrdinalIgnoreCase) ? "x" : "input";

        InferenceSession session;
        var key = (modelPath, gpuId);
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
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

        var inputs = new[] { NamedOnnxValue.CreateFromTensor(inputName, inputTensor) };
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue>? results = null;
        try
        {
            try
            {
                gate.Wait();
                try { results = session.Run(inputs); }
                finally { gate.Release(); }
            }
            catch (Exception ex) when (gpuId >= 0)
            {
                // DirectML 失败 → 换 CPU 会话重试(缓存独立 CPU 会话;与 GPU 会话互不干扰)
                WarnDmlUnavailable("推理失败: " + ex.Message.Split('\n')[0]);
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
