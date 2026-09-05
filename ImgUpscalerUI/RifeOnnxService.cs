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
    /// <summary>已确认运行期失败的 DirectML 设备:后续帧直接 CPU(不再每对帧失败一次)。</summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> _dmlBad = new();

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
        // 运行期已确认失败的 DirectML 设备:直接 CPU(每对帧不再重复失败调用)
        if (gpuId >= 0 && _dmlBad.ContainsKey(gpuId)) gpuId = -1;
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
        try
        {
            // GPU + 大帧:DirectML 显存有限,整帧 4K 会 OOM → 分块插帧(带边缘余量,无接缝)
            const int Tile = 512;
            if (gpuId >= 0 && (w > Tile || h > Tile))
            {
                RunTiled(session, bmp0, bmp1, time, outputPng, w, h);
                return;
            }
            RunSingle(session, bmp0, bmp1, time, outputPng, w, h, gpuId);
        }
        catch (Exception ex) when (gpuId >= 0)
        {
            // DirectML 失败/分块失败 → 标记设备不可用 + CPU 整帧重试(稳定优先,绝不出黑帧/半帧)
            AppLogger.Warn($"RIFE ONNX DirectML 失败,已改 CPU 整帧重算: {ex.Message.Split('\n')[0]}");
            _dmlBad[gpuId] = 0;
            DropSession(gpuId);
            RunSingle(GetSession(-1), bmp0, bmp1, time, outputPng, w, h, -1);
        }
    }

    /// <summary>整帧推理(CPU 或小帧 GPU)。失败时 GPU 自动降 CPU 重试并标记设备不可用。</summary>
    static void RunSingle(InferenceSession session, Bitmap bmp0, Bitmap bmp1, float time, string outputPng,
        int w, int h, int gpuId)
    {
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
            // DirectML 失败 → 丢弃该设备的会话,CPU 会话重试;标记设备不可用(后续帧直接 CPU)
            _dmlBad[gpuId] = 0;
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
            if (oh <= 0 || ow <= 0 || oh > 16384 || ow > 16384)
                throw new InvalidOperationException($"ONNX 输出尺寸异常({ow}x{oh}),模型输出异常,无法落图");
            var pixels = new float[3 * oh * ow];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = outTensor.GetValue(i);
            SavePng(FromTensor(pixels, ow, oh), outputPng);
        }
    }

    /// <summary>分块插帧:512×512 块 + 边缘余量(M=16)防接缝;块尺寸取 4 的倍数(RIFE 输入要求),越界用边缘像素填充。</summary>
    static void RunTiled(InferenceSession session, Bitmap bmp0, Bitmap bmp1, float time, string outputPng,
        int w, int h)
    {
        const int Tile = 512;
        const int M = 16;
        using var outBmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        var rect = new Rectangle(0, 0, w, h);
        var data = outBmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            unsafe
            {
                var ptr = (byte*)data.Scan0.ToPointer();
                for (int ty = 0; ty < h; ty += Tile)
                {
                    for (int tx = 0; tx < w; tx += Tile)
                    {
                        int tw = Math.Min(Tile, w - tx);
                        int th = Math.Min(Tile, h - ty);
                        int sx0 = Math.Max(0, tx - M), sy0 = Math.Max(0, ty - M);
                        int bw = Math.Min(w, tx + tw + M) - sx0;
                        int bh = Math.Min(h, ty + th + M) - sy0;
                        int pw = (bw + 3) & ~3, ph = (bh + 3) & ~3;   // 向上取 4 的倍数
                        var t0 = ToTensorRect(bmp0, sx0, sy0, pw, ph);
                        var t1 = ToTensorRect(bmp1, sx0, sy0, pw, ph);
                        var tensor0 = new DenseTensor<float>(t0, new[] { 1, 3, ph, pw });
                        var tensor1 = new DenseTensor<float>(t1, new[] { 1, 3, ph, pw });
                        var ts = new DenseTensor<float>(new[] { time }, new[] { 1 });
                        using var results = session.Run(new[]
                        {
                            NamedOnnxValue.CreateFromTensor("img0", tensor0),
                            NamedOnnxValue.CreateFromTensor("img1", tensor1),
                            NamedOnnxValue.CreateFromTensor("timestep", ts),
                        });
                        var outT = results.First().AsTensor<float>();
                        // 只写块有效中心区(丢弃边缘余量,防接缝)
                        int ox = tx - sx0, oy = ty - sy0;
                        for (int yy = 0; yy < th; yy++)
                        {
                            byte* row = ptr + (ty + yy) * data.Stride + tx * 3;
                            for (int xx = 0; xx < tw; xx++)
                            {
                                float r = outT[0, 0, oy + yy, ox + xx];
                                float g2 = outT[0, 1, oy + yy, ox + xx];
                                float b2 = outT[0, 2, oy + yy, ox + xx];
                                row[xx * 3] = (byte)Math.Clamp((int)Math.Round(b2 * 255f), 0, 255);
                                row[xx * 3 + 1] = (byte)Math.Clamp((int)Math.Round(g2 * 255f), 0, 255);
                                row[xx * 3 + 2] = (byte)Math.Clamp((int)Math.Round(r * 255f), 0, 255);
                            }
                        }
                    }
                }
            }
        }
        finally { outBmp.UnlockBits(data); }
        SavePng(outBmp, outputPng);
    }

    /// <summary>按区域读像素(偏移 + 越界边缘填充;写满 pw×ph 张量,供 RIFE 分块使用)。</summary>
    static float[] ToTensorRect(Bitmap src, int ox, int oy, int pw, int ph)
    {
        int w = src.Width, h = src.Height;
        var pixels = new float[3 * ph * pw];
        var rect = new Rectangle(0, 0, w, h);
        var data = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            unsafe
            {
                byte* basePtr = (byte*)data.Scan0.ToPointer();
                int plane = pw * ph;
                for (int yy = 0; yy < ph; yy++)
                {
                    int sy = Math.Min(oy + yy, h - 1);
                    for (int xx = 0; xx < pw; xx++)
                    {
                        int sx = Math.Min(ox + xx, w - 1);
                        byte* px = basePtr + sy * data.Stride + sx * 3;
                        int idx = yy * pw + xx;
                        pixels[idx] = px[2] / 255f;               // R
                        pixels[plane + idx] = px[1] / 255f;      // G
                        pixels[plane * 2 + idx] = px[0] / 255f;  // B
                    }
                }
            }
        }
        finally { src.UnlockBits(data); }
        return pixels;
    }

    static void DropSession(int gpuId)
    {
        if (_sessions.TryRemove(gpuId, out var old)) old?.Dispose();
    }

    // ---------- System.Drawing 工具(与电脑版 EsrganOnnxService 一致) ----------

    static Bitmap LoadBitmap(string path)
    {
        Bitmap probe;
        try { probe = new Bitmap(path); }
        catch (Exception ex) { throw new InvalidOperationException($"补帧输入帧无法解码:{Path.GetFileName(path)}——{ex.Message}", ex); }
        using (probe)
        {
            if (probe.Width <= 0 || probe.Height <= 0)
                throw new InvalidOperationException($"补帧输入帧尺寸异常(0×0):{Path.GetFileName(path)}");
            // 复制为 24bpp(保证 LockBits 像素格式稳定;System.Drawing 读取的默认格式可移植性差)
            var dst = new Bitmap(probe.Width, probe.Height, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(dst);
            g.DrawImage(probe, 0, 0, probe.Width, probe.Height);
            return dst;
        }
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
