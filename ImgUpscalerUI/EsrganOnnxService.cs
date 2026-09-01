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

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, InferenceSession> _sessions = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, SemaphoreSlim> _locks = new();

    /// <summary>ONNX 超分一张图(4x)。scale=目标倍数(4x 原生;2x 也走 4x 再缩回)。</summary>
    public static async Task UpscaleAsync(string input, string output, double scale,
        int gpuId = -1, IProgress<(int pct, string msg)>? progress = null, CancellationToken ct = default)
    {
        var modelPath = FindModel()
            ?? throw new FileNotFoundException(
                "未找到 RealESRGAN_x4plus.onnx,请放入 engines/rembg/ 目录(或程序目录)");
        progress?.Report((5, "加载 ONNX 模型..."));
        await Task.Run(() => RunCore(input, output, scale, modelPath, gpuId, progress, ct), ct);
        progress?.Report((100, "完成"));
    }

    /// <summary>ONNX 目录批处理(视频逐帧超分用):遍历 inputDir 的 PNG,逐帧 UpscaleAsync 输出到 outputDir。
    /// 供视频超分在 50 系/无独显设备走 ONNX(不走会崩的 ncnn-vulkan)。</summary>
    public static async Task UpscaleDirAsync(string inputDir, string outputDir, double scale,
        int gpuId = -1, IProgress<(int pct, string msg)>? progress = null, CancellationToken ct = default)
    {
        var files = Directory.EnumerateFiles(inputDir, "*.png")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        Directory.CreateDirectory(outputDir);
        if (files.Length == 0) return;
        for (int i = 0; i < files.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var outPath = Path.Combine(outputDir, Path.GetFileName(files[i]));
            await UpscaleAsync(files[i], outPath, scale, gpuId, null, ct).ConfigureAwait(false);
            progress?.Report(((int)((double)(i + 1) / files.Length * 100),
                $"ONNX 超分 {i + 1}/{files.Length} 帧({Path.GetFileName(files[i])})"));
        }
    }

    private static void RunCore(string input, string output, double scale, string modelPath, int gpuId,
        IProgress<(int pct, string msg)>? progress, CancellationToken ct)
    {
        using var src = new System.Drawing.Bitmap(input);
        int sw = src.Width, sh = src.Height;
        int ow = (int)Math.Round(sw * scale), oh = (int)Math.Round(sh * scale);

        // 输入张量(清一次生成):[1,3,H,W] float32,RGB
        // RealESRGAN 输入像素通常 0-1 归一化输出 0-1;差异用基准缩放修正
        int inW = sw, inH = sh;
        var pixels = new float[1 * 3 * inH * inW];
        FillPixelArray(src, pixels, inW, inH);
        var inputTensor = new DenseTensor<float>(pixels, new[] { 1, 3, inH, inW });

        InferenceSession session;
        var key = gpuId;
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
            gate.Wait();
            try { results = session.Run(inputs); }
            finally { gate.Release(); }

            var outTensor = results!.First().AsTensor<float>();
            // 输出通道首维:[1,3,OH,OW] — 校验并转 RGB;缩放差异由上层 ResizeImage 处理
            var dims = outTensor.Dimensions;
            if (dims.Length != 4 || dims[1] != 3)
                throw new InvalidOperationException($"ONNX 输出形状异常: {string.Join("x", dims.ToArray())}");
            int oH = dims[2], oW = dims[3];

            using var dst = new System.Drawing.Bitmap(oW, oH, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
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
                        if (y % 100 == 0 && progress != null)
                            progress.Report((20 + (int)(70.0 * y / oH), $"ONNX 推理 {y}/{oH}..."));
                    }
                }
            }
            finally { dst.UnlockBits(data); }

            // 目标尺度:原生 4x,若 scale<4 且接近 2x,引擎已按 4x 出,由调用方缩回;此处直接按 scale 缩放
            if (Math.Abs(oW - ow) > 1 || Math.Abs(oH - oh) > 1)
                SaveScaled(dst, output, ow, oh);
            else
                dst.Save(output, System.Drawing.Imaging.ImageFormat.Png);
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
