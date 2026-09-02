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

    /// <summary>Real-CUGAN ONNX 模型路径(engines/realcugan/ realcugan-x4.onnx;社区导出,BSD 许可)。</summary>
    public static string? FindCuganModel()
    {
        var root = Path.Combine(EngineService.EnginesDir, "realcugan");
        foreach (var f in new[] { "realcugan-x4.onnx", "4x-cugan-pretrain.onnx", "RealCUGAN_x4.onnx" })
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
    /// modelPath: 指定模型(Real-CUGAN 等);null = Real-ESRGAN 自动查找。
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
    /// 供视频超分在 50 系/无独显设备走 ONNX(不走会崩的 ncnn-vulkan)。modelPath=null 用 Real-ESRGAN。</summary>
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
            progress?.Report(((int)((double)(i + 1) / files.Length * 100),
                $"超分 {i + 1}/{files.Length} 帧({Path.GetFileName(files[i])})"));
        }
    }

    private static void RunCore(string input, string output, double scale, string modelPath, int gpuId,
        IProgress<(int pct, string msg)>? progress, CancellationToken ct)
    {
        using var src = new System.Drawing.Bitmap(input);
        int sw = src.Width, sh = src.Height;
        int ow = (int)Math.Round(sw * scale), oh = (int)Math.Round(sh * scale);

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

    /// <summary>单块推理(返回 4x 结果位图)。会话按 (modelPath,gpuId) 缓存;GPU 失败自动 CPU 重试。</summary>
    private static System.Drawing.Bitmap RunTile(System.Drawing.Bitmap src, string modelPath, int gpuId, CancellationToken ct)
    {
        int inW = src.Width, inH = src.Height;
        var pixels = new float[1 * 3 * inH * inW];
        FillPixelArray(src, pixels, inW, inH);
        var inputTensor = new DenseTensor<float>(pixels, new[] { 1, 3, inH, inW });

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
                    try { opts.AppendExecutionProvider_DML(gpuId); }
                    catch { /* DirectML 不可用回退 CPU */ }
                }
                return new InferenceSession(modelPath, opts);
            });
        }
        finally { gate.Release(); }

        var inputs = new[] { NamedOnnxValue.CreateFromTensor("input", inputTensor) };
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
