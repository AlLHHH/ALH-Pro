// RifeOnnxService.cs — 补帧 ONNX 路线(为 50 系/无独显等 ncnn-Vulkan 不可用设备):
// 标准 RIFE v4.9 模型(输入 img0/img1/timestep),DirectML GPU 优先,失败自动改 CPU(与 EsrganOnnxService 同策略)。
// 模型:engines/rife/rife49.onnx(20.5MB,MIT,社区 yuvraj108c/rife-onnx 导出)。
// 用途:VideoService 在 RIFE ncnn 引擎 GPU 探测失败时,优先走本 ONNX 路线(而非直接降 CPU)。
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ALHPro;

public static class RifeOnnxService
{
    // 会话按设备号缓存:首帧可能是小帧 CPU 会话,若单会话复用,后续大帧全落 CPU(慢 ~19 倍)。
    static readonly System.Collections.Concurrent.ConcurrentDictionary<int, InferenceSession> _sessions = new();
    static readonly object _sessionGate = new();

    /// <summary>ONNX 模型路径(engines/rife/rife49.onnx;不存在返回 null = 不启用 ONNX 路线)。</summary>
    public static string? FindModel()
    {
        var root = Path.Combine(EngineService.EnginesDir, "rife");
        var f = Path.Combine(root, "rife49.onnx");
        return File.Exists(f) ? f : null;
    }

    /// <summary>是否可走 ONNX 补帧路线(模型在才考虑;调用方还需 GPU 探测失败才真正用)。</summary>
    public static bool Available() => FindModel() != null;

    static InferenceSession GetSession(int gpuId)
    {
        if (_sessions.TryGetValue(gpuId, out var s) && s != null) return s;
        lock (_sessionGate)
        {
            if (_sessions.TryGetValue(gpuId, out var s2) && s2 != null) return s2;
            var opts = new SessionOptions();
            if (gpuId >= 0)
            {
                try { opts.AppendExecutionProvider_DML(EngineService.ToDmlDevice(gpuId)); }
                catch { /* DirectML 不可用回退 CPU */ }
            }
            var ses = new InferenceSession(FindModel()!, opts);
            _sessions[gpuId] = ses;
            return ses;
        }
    }

    /// <summary>用 ONNX 模型在 img0 与 img1 之间插 time(0~1) 帧,输出到 outputPng。gpuId&gt;=0 走 DirectML,失败自动 CPU;
    /// gpuId=-2 表示自动(按输入尺寸:大帧 GPU/小帧 CPU,实测小帧 CPU 反而快 19 倍)。</summary>
    public static void Interp(string img0, string img1, float time, string outputPng, int gpuId = -1)
    {
        var model = FindModel() ?? throw new FileNotFoundException("未找到 rife49.onnx(ONNX 补帧模型)");
        // -2 = 自动选设备
        if (gpuId == -2)
        {
            try
            {
                using (var probe = new System.Drawing.Bitmap(img0))
                    gpuId = EsrganOnnxService.PickDevice(probe.Width, probe.Height);
            }
            catch { gpuId = -1; }
        }
        var session = GetSession(gpuId);
        RunCore(session, img0, img1, time, outputPng, gpuId, model);
    }

    static void RunCore(InferenceSession session, string img0, string img1, float time, string outputPng,
        int gpuId, string model)
    {
        using var bmp0 = LoadBitmap(img0);
        using var bmp1 = LoadBitmap(img1);
        if (bmp0.Width != bmp1.Width || bmp0.Height != bmp1.Height)
            throw new InvalidOperationException("两帧尺寸不一致,无法补帧");

        int w = bmp0.Width, h = bmp0.Height;
        var t0 = ToTensor(bmp0);
        var t1 = ToTensor(bmp1);
        var tensor0 = new DenseTensor<float>(t0, new[] { 1, 3, h, w });
        var tensor1 = new DenseTensor<float>(t1, new[] { 1, 3, h, w });
        var ts = new DenseTensor<float>(new[] { time }, new[] { 1 });

        IDisposableReadOnlyCollection<DisposableNamedOnnxValue>? results = null;
        try
        {
            results = session.Run(new[]
            {
                NamedOnnxValue.CreateFromTensor("img0", tensor0),
                NamedOnnxValue.CreateFromTensor("img1", tensor1),
                NamedOnnxValue.CreateFromTensor("timestep", ts),
            });
        }
        catch (Exception ex) when (gpuId >= 0)
        {
            // DirectML 失败 → 丢弃该设备的会话,CPU 会话重试
            DropSession(gpuId);
            results = GetSession(-1).Run(new[]
            {
                NamedOnnxValue.CreateFromTensor("img0", tensor0),
                NamedOnnxValue.CreateFromTensor("img1", tensor1),
                NamedOnnxValue.CreateFromTensor("timestep", ts),
            });
        }
        using (results)
        {
            var outTensor = results!.First().AsTensor<float>();
            var dims = outTensor.Dimensions;
            if (dims.Length != 4 || dims[1] != 3)
                throw new InvalidOperationException($"ONNX 输出形状异常: {string.Join("x", dims.ToArray())}");
            int oh = dims[2], ow = dims[3];
            var pixels = new float[3 * oh * ow];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = outTensor.GetValue(i);
            SavePng(FromTensor(pixels, ow, oh), outputPng);
        }
    }

    static void DropSession(int gpuId)
    {
        if (_sessions.TryRemove(gpuId, out var old)) old?.Dispose();
    }

    // ---------- System.Drawing 工具(与电脑版 EsrganOnnxService 一致) ----------

    static Bitmap LoadBitmap(string path)
    {
        using var probe = new Bitmap(path);
        // 复制为 24bpp(保证 LockBits 像素格式稳定;System.Drawing 读取的默认格式可移植性差)
        var dst = new Bitmap(probe.Width, probe.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(dst);
        g.DrawImage(probe, 0, 0, probe.Width, probe.Height);
        return dst;
    }

    static float[] ToTensor(Bitmap src)
    {
        int w = src.Width, h = src.Height;
        var pixels = new float[3 * h * w];
        var rect = new Rectangle(0, 0, w, h);
        var data = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            int plane = w * h;
            unsafe
            {
                byte* basePtr = (byte*)data.Scan0;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int idx = y * w + x;
                        byte* px = basePtr + y * data.Stride + x * 3;
                        pixels[idx] = px[2] / 255f;               // R
                        pixels[plane + idx] = px[1] / 255f;      // G
                        pixels[plane * 2 + idx] = px[0] / 255f;  // B
                    }
            }
        }
        finally { src.UnlockBits(data); }
        return pixels;
    }

    static Bitmap FromTensor(float[] t, int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        var rect = new Rectangle(0, 0, w, h);
        var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            int plane = w * h;
            unsafe
            {
                byte* basePtr = (byte*)data.Scan0;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int idx = y * w + x;
                        byte* px = basePtr + y * data.Stride + x * 3;
                        px[0] = (byte)Math.Clamp((int)Math.Round(t[plane * 2 + idx] * 255f), 0, 255);
                        px[1] = (byte)Math.Clamp((int)Math.Round(t[plane + idx] * 255f), 0, 255);
                        px[2] = (byte)Math.Clamp((int)Math.Round(t[idx] * 255f), 0, 255);
                    }
            }
        }
        finally { bmp.UnlockBits(data); }
        return bmp;
    }

    static void SavePng(Bitmap bmp, string path)
    {
        bmp.Save(path, ImageFormat.Png);
    }
}
