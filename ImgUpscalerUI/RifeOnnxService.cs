// RifeOnnxService.cs — 补帧 ONNX 路线(为 50 系/无独显等 ncnn-Vulkan 不可用设备):
// 标准 RIFE v4.9 模型(输入 img0/img1/timestep),DirectML GPU 优先;偶发失败只对【本对帧】CPU 重算一次,
// 设备被摘除(887A)或连续失败达上限则不落 CPU(「补帧绝不落 CPU」是硬约定),由调用方复制原帧。
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

    /// <summary>建一个 DirectML 会话(不缓存)。gpuId≥0 走 DirectML;失败规则与超分一致:
    /// 持久设备错误(887A)重抛(不落 CPU),其它失败打明确日志并回退 CPU。</summary>
    static InferenceSession BuildSession(int gpuId)
    {
        var opts = new SessionOptions();
        if (gpuId >= 0)
        {
            try
            {
                int dm = EngineService.ToDmlDevice(gpuId);
                if (dm < 0)
                    AppLogger.Warn($"⚠ 补帧 ONNX 设备映射:引擎编号 {gpuId} 未匹配到 DirectML 设备,将回退 CPU(速度会特别慢)——请检查显卡/驱动");
                else
                    opts.AppendExecutionProvider_DML(dm);
            }
            catch (Exception dmlEx)
            {
                if (AlhPro.Core.GpuFault.IsPersistentDeviceError(dmlEx)) throw;   // 设备摘除:不落 CPU,交由调用方复制原帧
                AppLogger.Warn($"⚠ 补帧 ONNX DirectML 会话创建失败({gpuId},原因:{dmlEx.Message.Split('\n')[0]})——本机无可用 GPU ONNX,本会话将退回 CPU(速度会特别慢,若持续出现请更新显卡驱动后重试)");
            }
        }
        return new InferenceSession(FindModel()!, opts);
    }

    static InferenceSession GetSession(int gpuId)
    {
        if (_sessions.TryGetValue(gpuId, out var s) && s != null) return s;
        lock (_sessionGate)
        {
            if (_sessions.TryGetValue(gpuId, out var s2) && s2 != null) return s2;
            var ses = BuildSession(gpuId);
            _sessions[gpuId] = ses;
            return ses;
        }
    }

    /// <summary>创建 concurrency 个独立 DirectML 会话(并行 worker 每个独占一个;绝不共用/并发 Run 同一会话,
    /// DirectML InferenceSession 非线程安全)。由调用方负责 finally 里 Dispose。</summary>
    public static InferenceSession[] CreateSessions(int concurrency, int gpuId)
    {
        var arr = new InferenceSession[Math.Max(1, concurrency)];
        for (int i = 0; i < arr.Length; i++)
            arr[i] = BuildSession(gpuId);
        return arr;
    }

    /// <summary>用【指定会话】在 img0/img1 间插 time 帧,写入 outputPng。worker 用自己独占的会话调用,
    /// 不从共享 _sessions 取(否会同会话并发 Run 崩)。gpuId 仅用于错误关联/熔断判定(应传具体设备号)。</summary>
    public static void InterpWithSession(InferenceSession session, string img0, string img1, float time,
        string outputPng, int gpuId)
    {
        var model = FindModel() ?? throw new FileNotFoundException("缺少补帧模型:rife49.onnx");
        if (gpuId != -1 && EsrganOnnxService.DmlDeviceDead)
            throw new InvalidOperationException(
                "GPU(DirectML)已被系统摘除/挂死,本进程内无法恢复——已停止补帧尝试(不降级到慢速 CPU)。请重启软件后重试。");
        if (EsrganOnnxService.DmlDeviceUnusable(gpuId))
            throw new InvalidOperationException(
                $"GPU(DirectML 设备 {gpuId})连续多次补帧推理失败,本进程内视为不可用——已停止补帧尝试(不降级到慢速 CPU)。"
                + "剩余帧将复制原帧;请重启软件后重试。");
        RunCore(session, img0, img1, time, outputPng, gpuId, model);
    }

    /// <summary>用 ONNX 模型在 img0 与 img1 之间插 time(0~1) 帧,输出到 outputPng。gpuId&gt;=0 走 DirectML,
    /// 偶发失败只对本对帧 CPU 重算一次,设备级失效/连续失败则抛出(不落 CPU);
    /// gpuId=-2 表示自动(按输入尺寸:大帧 GPU/小帧 CPU,实测小帧 CPU 反而快 19 倍)。</summary>
    public static void Interp(string img0, string img1, float time, string outputPng, int gpuId = -1)
    {
        var model = FindModel() ?? throw new FileNotFoundException("缺少补帧模型:rife49.onnx");
        // 【设备已死 = 快速失败,绝不落 CPU】887A0005/887A0006 之后本进程的 D3D 设备已被摘除,每次 DirectML 调用
        // 必然失败;这种情况转 CPU 会把整段视频静默拖到 CPU 上补帧(几十分钟起步),违反「补帧绝不落 CPU」。
        // gpuId=-1 表示调用方明确要 CPU(本机无 GPU 可用)——那是唯一允许用 CPU 的场景,不在此列。
        if (gpuId != -1 && EsrganOnnxService.DmlDeviceDead)
            throw new InvalidOperationException(
                "GPU(DirectML)已被系统摘除/挂死,本进程内无法恢复——已停止补帧尝试(不降级到慢速 CPU)。请重启软件后重试。");
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
        // 连续瞬时失败已达上限:快速失败。必须在 -2 解析【之后】查——自动路径传进来的是 -2,解析前查永远命中不了,
        // 于是每对帧都白试一次"建会话 + 注定失败的推理",那正是这个检查要省掉的成本。
        // 抛出后调用方按帧复制原帧(毫秒级)。原先这里把 gpuId 改成 -1,等于一次抖动就让剩下整段视频在 CPU 上补帧。
        if (EsrganOnnxService.DmlDeviceUnusable(gpuId))
            throw new InvalidOperationException(
                $"GPU(DirectML 设备 {gpuId})连续多次补帧推理失败,本进程内视为不可用——已停止补帧尝试(不降级到慢速 CPU)。"
                + "剩余帧将复制原帧;请重启软件后重试。");
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
                EsrganOnnxService.ClearDmlStrikes(gpuId);   // GPU 真跑成功 → 偶发抖动不该累积
                return;
            }
            RunSingle(session, bmp0, bmp1, time, outputPng, w, h, gpuId);
            if (gpuId >= 0) EsrganOnnxService.ClearDmlStrikes(gpuId);
        }
        catch (Exception ex) when (gpuId >= 0)
        {
            // 设备被摘除/挂死(887A):本进程内不可恢复 → 熔断并抛出,让调用方按帧复制原帧(毫秒级)。
            // CPU 整帧重算等于整段视频在 CPU 上补帧(几十分钟起步),违反「补帧绝不落 CPU」。
            if (AlhPro.Core.GpuFault.IsPersistentDeviceError(ex))
            {
                EsrganOnnxService.TripDmlDead(gpuId, ex);
                throw;
            }
            // 偶发失败:本对帧换 CPU 重算一次(代价有界,绝不出黑帧/半帧)。但连击达上限就认定设备不可用 → 抛出,
            // 由调用方复制原帧。原先这里写 _dmlBad[gpuId] 永久闩锁,一次抖动就让剩下整段视频都在 CPU 上补帧。
            if (EsrganOnnxService.NoteDmlTransientFailure(gpuId))
                throw new InvalidOperationException(
                    $"RIFE ONNX 补帧失败:GPU(DirectML 设备 {gpuId})连续多次推理失败,已停止尝试(不降级到慢速 CPU)。"
                    + "剩余帧将复制原帧;请重启软件后重试。", ex);
            AppLogger.Warn($"RIFE ONNX DirectML 失败,本对帧改 CPU 整帧重算: {ex.Message.Split('\n')[0]}");
            DropSession(gpuId);
            RunSingle(GetSession(-1), bmp0, bmp1, time, outputPng, w, h, -1);
        }
    }

    /// <summary>整帧推理(CPU 或小帧 GPU)。GPU 失败一律抛出(设备级失效就地熔断),恢复策略由 RunCore 统一决定。</summary>
    static void RunSingle(InferenceSession session, Bitmap bmp0, Bitmap bmp1, float time, string outputPng,
        int w, int h, int gpuId)
    {
        // 【修复 补帧没效果】RIFE ONNX 输入要求宽高为 4 的倍数。原整帧路径直接把 w/h 喂进模型,
        // 非 4 倍数的帧会抛形状错误 → 上层 catch 后静默复制左端点 → 该帧不插帧(看起来"没效果")。
        // 与分块路径一致:先补到 4 的倍数(用边缘像素复制),推理后再裁回原尺寸。
        int pw = (w + 3) & ~3, ph = (h + 3) & ~3;
        bool padded = pw != w || ph != h;
        var t0 = padded ? ToTensorRect(bmp0, 0, 0, pw, ph) : ToTensor(bmp0);
        var t1 = padded ? ToTensorRect(bmp1, 0, 0, pw, ph) : ToTensor(bmp1);
        var tensor0 = new DenseTensor<float>(t0, new[] { 1, 3, ph, pw });
        var tensor1 = new DenseTensor<float>(t1, new[] { 1, 3, ph, pw });
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
            // 设备被摘除/挂死(887A):就地熔断(越早置位,越多调用点能立刻快速失败),然后抛出。
            if (AlhPro.Core.GpuFault.IsPersistentDeviceError(ex)) EsrganOnnxService.TripDmlDead(gpuId, ex);
            // 恢复策略统一在 RunCore:它才知道该复制原帧还是 CPU 重算一次。原先这里自己转 CPU 重算,失败后异常
            // 传到 RunCore 又转一次 —— 同一对帧做了两次 CPU 推理,白等一倍时间。
            throw;
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
            using var full = FromTensor(pixels, ow, oh);
            if (padded && ow >= w && oh >= h)
            {
                using var cropped = full.Clone(new Rectangle(0, 0, w, h), PixelFormat.Format24bppRgb);
                SavePng(cropped, outputPng);
            }
            else
                SavePng(full, outputPng);
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
        // 数值防御(源头不黑):模型输出若含 NaN/Inf(数值溢出/除0)→ 该帧已损坏,直接抛出让调用方回退源帧,
        // 避免 (int)NaN=0 → 全黑帧 写进输出。正常帧无 NaN,仅一次数组扫描,开销远小于推理。
        for (int i = 0; i < t.Length; i++)
            if (float.IsNaN(t[i]) || float.IsInfinity(t[i]))
                throw new InvalidOperationException("ONNX 输出含 NaN/Inf(数值异常),该帧补帧结果无效");
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
