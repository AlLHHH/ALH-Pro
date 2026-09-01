// CutoutService.cs — 用 ONNX Runtime 直接跑多种抠图模型(纯 C#,无 Python)
// 支持:多模型选择 + 前后景阈值 / 边缘羽化 / 边缘增强参数
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ALHPro;

public static class CutoutService
{
    /// <summary>模型注册表:Key / 显示名 / 文件名 / 输入尺寸 / 逐通道归一化 mean/std(源码核对) / 最优参数预设。
    /// OutputName=掩码输出张量名(空=取第一个 [1,1,H,W] 输出,u2net 系列输出无固定名)。
    /// LogitsOutput=true 表示输出为未激活 logits(如 BiRefNet),需先 sigmoid 再 min-max。</summary>
    public record CutoutModel(string Key, string Label, string FileName, int InputSize,
        float MeanR, float MeanG, float MeanB, float StdR, float StdG, float StdB,
        int FgPreset, int BgPreset, int FeatherPreset, int EdgePreset, int MorphPreset = 0, string OutputName = "output_image", bool LogitsOutput = false);

    /// <summary>智能涂抹笔迹:Keep=true=绿色保留(强制前景),false=红色删除(强制背景);点为像素坐标。</summary>
    public sealed record CutoutScribble(bool Keep, IReadOnlyList<(int X, int Y)> Points);

    public static readonly CutoutModel[] Models =
    {
        // 数值来源(交叉验证):rembg 官方 session 源码 + U²-Net/BiRefNet 原版 + 本地 onnx 元数据实测
        // birefnet-lite/birefnet:ImageNet 归一化 + 输出需 sigmoid;isnet-general:mean 0.5/std 1.0;
        // isnet-anime:ImageNet mean/std 1.0,输出名 mask;u2net:ImageNet,输出无固定名(取首个 [1,1,H,W])
        new("birefnet-lite", "BiRefNet 高精度 (推荐)", "birefnet-lite.onnx",
            1024, 0.485f, 0.456f, 0.406f, 0.229f, 0.224f, 0.225f,
            FgPreset: 168, BgPreset: 90, FeatherPreset: 2, EdgePreset: 0, MorphPreset: 35,
            "output_image", LogitsOutput: true),
        new("birefnet", "BiRefNet 完整版 (复杂背景)", "birefnet.onnx",
            1024, 0.485f, 0.456f, 0.406f, 0.229f, 0.224f, 0.225f,
            FgPreset: 176, BgPreset: 96, FeatherPreset: 2, EdgePreset: 0, MorphPreset: 30,
            "output_image", LogitsOutput: true),
        new("isnet-general-use", "ISNet 精细边缘", "isnet-general-use.onnx",
            1024, 0.5f, 0.5f, 0.5f, 1f, 1f, 1f,
            FgPreset: 152, BgPreset: 84, FeatherPreset: 1, EdgePreset: 0, MorphPreset: 28,
            "output_image"),
        new("isnet-anime", "ISNet 动漫", "isnet-anime.onnx",
            1024, 0.485f, 0.456f, 0.406f, 1f, 1f, 1f,
            FgPreset: 144, BgPreset: 80, FeatherPreset: 1, EdgePreset: 0, MorphPreset: 22,
            "mask"),
        new("u2net", "U²-Net 通用", "u2net.onnx",
            320, 0.485f, 0.456f, 0.406f, 0.229f, 0.224f, 0.225f,
            FgPreset: 160, BgPreset: 88, FeatherPreset: 0, EdgePreset: 0, MorphPreset: 20,
            ""),
        new("u2netp", "U²-Net 轻量 (快速)", "u2netp.onnx",
            320, 0.485f, 0.456f, 0.406f, 0.229f, 0.224f, 0.225f,
            FgPreset: 160, BgPreset: 88, FeatherPreset: 0, EdgePreset: 0, MorphPreset: 15,
            ""),
    };

    public static CutoutModel GetModel(string key)
        => Models.FirstOrDefault(m => m.Key == key) ?? Models[0];

    /// <summary>
    /// AI 抠图主流程。
    /// </summary>
    /// <param name="modelKey">模型 Key(见 Models)。</param>
    /// <param name="fgThreshold">前景阈值 0~255:掩码大于等于该值 → 完全不透明。</param>
    /// <param name="bgThreshold">背景阈值 0~255:掩码小于等于该值 → 完全透明;之间线性过渡。</param>
    /// <param name="featherRadius">边缘羽化半径(像素,0=不羽化)。</param>
    /// <param name="edgeStrength">边缘增强 0~100(0=不增强;越大边缘越锐利)。</param>
    /// <param name="gpuId">计算设备:-1=CPU(默认);&gt;=0=DirectML GPU 编号。</param>
    /// <param name="selX/selY/selW/selH">主体框选(像素坐标,原图坐标系):区域外强制透明,
    /// 边界 16px 渐变过渡。null=全图抠取。</param>
    /// <param name="scribbles">智能涂抹笔迹:绿色(Keep=true)=强制保留,红色(Keep=false)=强制删除,
    /// 并按颜色相近度向周边扩散(用户涂抹的物体整体被识别)。</param>
    /// <param name="tolerance">涂抹颜色容差 0~100:0=只匹配完全同色,100=大幅放宽(按颜色扩散的范围)。</param>
    /// <param name="maxSpread">涂抹扩散距离上限(原图像素,0=不限):涂一笔最多扩散这么远,
    /// 防止同色渐变区域/大面积同色背景被一整笔铺满。</param>
    /// <param name="morphStrength">蒙版形态学清洗强度 0~100(0=关闭):开运算去背景小噪点岛 + 轻微腐蚀去边缘残余。</param>
    /// <param name="autoThreshold">自适应阈值(按蒙版直方图 Otsu 自动定前景/背景界,按图调优)。true 时忽略 fg/bg 滑条。</param>
    public static async Task<string> CutoutAsync(string input, string output, string modelKey,
        int fgThreshold, int bgThreshold, int featherRadius, int edgeStrength, int gpuId = -1,
        int? selX = null, int? selY = null, int? selW = null, int? selH = null,
        IReadOnlyList<CutoutScribble>? scribbles = null, int tolerance = 30, double? brushRadius = null,
        double maxSpread = 0, int morphStrength = 0, bool autoThreshold = false,
        IProgress<(int pct, string msg)>? progress = null,
        CancellationToken ct = default)
    {
        var model = GetModel(modelKey);
        var modelPath = EngineService.FindCutoutModel(model.FileName)
            ?? throw new FileNotFoundException(
                $"未找到抠图模型 {model.FileName},请安装模型包:解压到程序目录 engines\\rembg\\ 文件夹(6 个 .onnx 平铺)");

        progress?.Report((5, $"加载模型({model.Label})..."));
        return await Task.Run(() =>
        {
            var alpha = RunCore(input, model, modelPath, gpuId,
                fgThreshold, bgThreshold, featherRadius, edgeStrength,
                selX, selY, selW, selH, scribbles, tolerance, brushRadius, maxSpread,
                morphStrength, autoThreshold, progress, ct);
            progress?.Report((85, "输出透明 PNG..."));
            // 注意:必须用与 RunCore 一致的旋转后图像(EXIF),否则 alpha 与像素错位
            using var src = LoadRotatedBitmap(input);
            SaveWithAlpha(src, alpha, output);
            progress?.Report((100, "完成"));
            return output;
        }, ct);
    }

    /// <summary>
    /// AI 主体预览:输出黑白蒙版图(白=AI 识别的主体,黑=背景),
    /// 用于抠图前确认模型对"谁是主体"的判断是否正确。
    /// 传入框选/涂抹时,预览会叠加显示它们的最终效果(与抠图输出一致)。
    /// </summary>
    public static async Task<string> PreviewMaskAsync(string input, string output, string modelKey,
        int fgThreshold, int bgThreshold, int gpuId = -1,
        int? selX = null, int? selY = null, int? selW = null, int? selH = null,
        IReadOnlyList<CutoutScribble>? scribbles = null, int tolerance = 30, double? brushRadius = null,
        double maxSpread = 0,
        int featherRadius = 0, int edgeStrength = 0, int morphStrength = 0, bool autoThreshold = false,
        IProgress<(int pct, string msg)>? progress = null,
        CancellationToken ct = default)
    {
        var model = GetModel(modelKey);
        var modelPath = EngineService.FindCutoutModel(model.FileName)
            ?? throw new FileNotFoundException(
                $"未找到抠图模型 {model.FileName},请安装模型包:解压到程序目录 engines\\rembg\\ 文件夹(6 个 .onnx 平铺)");

        progress?.Report((5, $"加载模型({model.Label})..."));
        return await Task.Run(() =>
        {
            var alpha = RunCore(input, model, modelPath, gpuId,
                fgThreshold, bgThreshold, featherRadius, edgeStrength,
                selX, selY, selW, selH, scribbles, tolerance, brushRadius, maxSpread,
                morphStrength, autoThreshold, progress, ct);
            progress?.Report((90, "生成蒙版预览..."));
            using var src = LoadRotatedBitmap(input);   // 与 RunCore 同一坐标系(EXIF 旋转)
            int w = src.Width, h = src.Height;
            using var maskBmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var rect = new System.Drawing.Rectangle(0, 0, w, h);
            var data = maskBmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                unsafe
                {
                    var ptr = (byte*)data.Scan0.ToPointer();
                    int stride = data.Stride;
                    for (int y = 0; y < h; y++)
                    {
                        int row = y * w;
                        int i = y * stride;
                        for (int x = 0; x < w; x++)
                        {
                            byte v = (byte)(Math.Clamp(alpha[row + x], 0f, 1f) * 255f);
                            ptr[i + x * 4] = v;         // B
                            ptr[i + x * 4 + 1] = v;     // G
                            ptr[i + x * 4 + 2] = v;     // R
                            ptr[i + x * 4 + 3] = 255;   // A(整体不透明,白=主体 黑=背景)
                        }
                    }
                }
            }
            finally
            {
                maskBmp.UnlockBits(data);
            }
            maskBmp.Save(output, System.Drawing.Imaging.ImageFormat.Png);
            progress?.Report((100, "完成"));
            return output;
        }, ct);
    }

    /// <summary>模型推理 + 掩码后处理共用核心(抠图输出与蒙版预览复用)。
    /// 该函数在 Task.Run 后台执行,不阻塞 UI。</summary>
    private static float[] RunCore(string input, CutoutModel model, string modelPath, int gpuId,
        int fgThreshold, int bgThreshold, int featherRadius, int edgeStrength,
        int? selX, int? selY, int? selW, int? selH,
        IReadOnlyList<CutoutScribble>? scribbles, int tolerance, double? brushRadius, double maxSpread,
        int morphStrength, bool autoThreshold,
        IProgress<(int pct, string msg)>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Session 缓存:同一模型 + 同一设备复用实例,避免每次抠图/预览都重新加载 ONNX(1~3 秒)。
        // 注意:不能 using 释放缓存里的 session——它是共享实例,using 会在方法结束 Dispose 掉,
        // 导致下一次调用拿到已释放的 session → ObjectDisposed/空引用崩。session 生命周期由缓存管理。

        // 读取图片 → 缩放到模型输入尺寸 → 归一化
        using var src = LoadRotatedBitmap(input);   // 含 EXIF 旋转:与输出/预览同一坐标系

        var (pixels, w, h) = Preprocess(src, model.InputSize, model);

        // 蒙版缓存已移除:曾为省调参时的重复推理,但实测可能复用错蒙版导致输出偏移/崩坏。
        // 每次重新推理(与 harness 验证一致=居中正确);调参卡顿由「CPU 流畅模式」与其它手段分担。
        float[,] mask;
        progress?.Report((30, "AI 分析主体..."));
        // 推理期平滑推进:ONNX 推理无进度回调(CPU 一张 10~40s),期间进度条停在 30%
        // 会让人误以为卡死/只有"空和满"两端——用定时器在 30→66 渐进上涨,推理结束由真实阶段(70)覆盖。
        int smoothPct = 30;
        using var smoothTimer = new System.Threading.Timer(_ =>
        {
            smoothPct = Math.Min(66, smoothPct + 3);
            try { progress?.Report((smoothPct, "AI 分析主体... 推理中")); } catch { }
        }, null, 3000, 1500);
        try
        {
            var session = GetOrCreateSession(modelPath, gpuId);
            ct.ThrowIfCancellationRequested();
            var inputMeta = session.InputMetadata.Keys.First();
            var inputTensor = new DenseTensor<float>(pixels, new[] { 1, 3, model.InputSize, model.InputSize });
            var inputs = new[] { NamedOnnxValue.CreateFromTensor(inputMeta, inputTensor) };

            // 串行 Run(同一模型+设备并发推理会冲突,用信号量保护)
            using (var results = RunSession(session, modelPath, gpuId, inputs))
            {
                smoothTimer.Dispose();   // 推理结束:平滑计时器停止(后续由真实阶段报告)
                ct.ThrowIfCancellationRequested();
                // 选择真正的掩码输出:优先注册表 OutputName;u2net(u2net/u2netp)无固定名 → 取首个 [1,1,H,W] 输出。
                // BiRefNet 输出未激活 logits,ExtractMask 内先 sigmoid;所有模型再 min-max 归一化(rembg 官方)。
                var outputTensor = SelectMaskOutput(results, model.OutputName);
                mask = ExtractMask(outputTensor, model.LogitsOutput);
            }
        }
        catch (Exception ex) when (gpuId >= 0 && ex is not OperationCanceledException)
        {
            // DirectML GPU 兼容适配(新驱动/老显卡/设备编号错):自动改用 CPU 重跑一次,不直接失败
            AppLogger.Info($"降级:DirectML GPU({gpuId})失败:{ex.Message.Split('\n')[0]},改用 CPU 重试(新显卡/老显卡兼容)");
            progress?.Report((30, "⚠ GPU 推理失败,改用 CPU 重试(较慢但稳定)..."));
            AppLogger.Info("⚠ GPU 推理失败,改用 CPU 重试(较慢但稳定)...");
            var session = GetOrCreateSession(modelPath, -1);
            ct.ThrowIfCancellationRequested();
            var inputMeta = session.InputMetadata.Keys.First();
            var inputTensor = new DenseTensor<float>(pixels, new[] { 1, 3, model.InputSize, model.InputSize });
            var inputs = new[] { NamedOnnxValue.CreateFromTensor(inputMeta, inputTensor) };
            using (var results = RunSession(session, modelPath, -1, inputs))
            {
                smoothTimer.Dispose();
                ct.ThrowIfCancellationRequested();
                var outputTensor = SelectMaskOutput(results, model.OutputName);
                mask = ExtractMask(outputTensor, model.LogitsOutput);
            }
        }

        // 放大到原图尺寸 → 参数后处理(阈值 / 形态学清洗 / 主体框选 / 涂抹 / 羽化 / 边缘增强)
        progress?.Report((70, "边缘处理..."));
        return ProcessMask(mask, src.Width, src.Height,
            fgThreshold, bgThreshold, featherRadius, edgeStrength,
            selX, selY, selW, selH, scribbles, tolerance, brushRadius, maxSpread,
            morphStrength, autoThreshold, src);
    }

    /// <summary>Session 缓存(同一模型+设备复用):避免重复加载 ONNX 模型。
    /// 注意:同一 InferenceSession 不能并发 Run(ONNX Runtime 非线程安全)——多个任务(如预览+抠图)同时跑会冲突。
    /// 这里用每 key 一个 SemaphoreSlim 串行化 Run,保证同模型+设备的推理互斥。
    /// 不同模型/设备各自缓存,互不影响。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, int), InferenceSession> _sessionCache = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, int), SemaphoreSlim> _sessionLocks = new();
    // 模型原始蒙版缓存(按 图|模型|设备):模型输出与阈值/羽化/边缘/框选/涂抹等后处理参数无关,
    // 调整参数时复用已算好的蒙版、只重跑后处理,避免每改一个滑条就重跑 8~10s 的 GPU 推理(否则抠图页"很卡")。
    // 只在缓存 > 8 张时清空,防止长会话内存累积。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, float[,]> _rawMaskCache = new();

    private static InferenceSession GetOrCreateSession(string modelPath, int gpuId)
    {
        var key = (modelPath, gpuId);
        // 先取锁(保证并发任务互斥),再取 Session(锁内创建,避免重复加载)
        var gate = _sessionLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        return _sessionCache.GetOrAdd(key, _ =>
        {
            var opts = new SessionOptions();
            if (gpuId >= 0)
            {
                try { opts.AppendExecutionProvider_DML(gpuId); }
                catch { /* DirectML 不可用时回退 CPU */ }
            }
            return new InferenceSession(modelPath, opts);
        });
    }

    /// <summary>串行执行 session.Run(同一模型+设备并发 Run 会冲突)。</summary>
    private static IDisposableReadOnlyCollection<DisposableNamedOnnxValue> RunSession(InferenceSession session, string modelPath, int gpuId, NamedOnnxValue[] inputs)
    {
        var gate = _sessionLocks.GetOrAdd((modelPath, gpuId), _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            return session.Run(inputs);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>缩放 + RGB 归一化 (x/255 - mean) / std,按 CHW 排布。
    /// 用 LockBits 一次性读内存(GDI+ GetPixel 逐像素极慢,1024 图 100 万次调用会卡)。</summary>
    private static (float[] pixels, int w, int h) Preprocess(System.Drawing.Bitmap bmp, int size, CutoutModel model)
    {
        // rembg 官方(源码核对):img.resize((size,size), LANCZOS) 直接【拉伸成正方形】(非 letterbox、不补边),
        // 归一化 (像素/maxPix - mean)/std,maxPix=图像自身最大像素。之前加了 letterbox 反而与官方不一致。
        using var resized = new System.Drawing.Bitmap(bmp, new System.Drawing.Size(size, size));
        var pixels = new float[3 * size * size];
        float mr = model.MeanR, mg = model.MeanG, mb = model.MeanB;
        float sr = model.StdR, sg = model.StdG, sb = model.StdB;
        float invR = 1f / sr, invG = 1f / sg, invB = 1f / sb;
        var rect = new System.Drawing.Rectangle(0, 0, size, size);
        var data = resized.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                var ptr = (byte*)data.Scan0.ToPointer();
                int stride = data.Stride;
                // 1) maxPix:图像自身最大像素值(至少 1,防除零)
                float maxPix = 1f;
                for (int y = 0; y < size; y++)
                {
                    byte* row = ptr + y * stride;
                    for (int x = 0; x < size; x++)
                    {
                        byte* p = row + x * 4;
                        float m = Math.Max(p[2], Math.Max(p[1], p[0]));
                        if (m > maxPix) maxPix = m;
                    }
                }
                // 2) 归一化并写入 CHW
                for (int y = 0; y < size; y++)
                {
                    int row = y * size;
                    int i = y * stride;
                    for (int x = 0; x < size; x++)
                    {
                        byte* p = ptr + i + x * 4;   // BGRA
                        int idx = row + x;
                        pixels[idx] = (p[2] / maxPix - mr) * invR;                        // R
                        pixels[idx + size * size] = (p[1] / maxPix - mg) * invG;         // G
                        pixels[idx + 2 * size * size] = (p[0] / maxPix - mb) * invB;     // B
                    }
                }
            }
        }
        finally
        {
            resized.UnlockBits(data);
        }
        return (pixels, bmp.Width, bmp.Height);
    }

    /// <summary>从模型输出中挑选真正的单通道掩码(优先显式 OutputName,再按形状降级)。</summary>
    private static Tensor<float> SelectMaskOutput(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results,
        string preferredName)
    {
        NamedOnnxValue? named = null;
        // 1) 显式指定输出名(模型注册表标注,最稳;不同模型输出名/结构差异大)
        if (!string.IsNullOrEmpty(preferredName))
        {
            foreach (var r in results)
            {
                if (string.Equals(r.Name, preferredName, StringComparison.OrdinalIgnoreCase))
                {
                    named = r;
                    break;
                }
            }
        }
        // 2) 常用名兜底
        if (named == null)
        {
            foreach (var r in results)
            {
                if (r.Name is "output_image" or "mask") { named = r; break; }
            }
        }
        // 3) 兜底:多输出时优先挑"真正的蒙版张量"——[1,1,H,W] 四维、单通道、空间尺寸最大(全分辨率掩码),
        //    且名字含 output/mask/pred/image 的优先(避免选中下采样特征图/注意力图导致掩码错位)。
        if (named == null)
        {
            NamedOnnxValue? bestV = null; long bestPix = 0; bool bestNamed = false;
            foreach (var r in results)
            {
                var t = r.AsTensor<float>();
                var d = t.Dimensions;
                if (d.Length == 4 && d[0] == 1 && d[1] == 1)
                {
                    long pix = (long)d[^2] * d[^1];
                    bool nm = r.Name.Contains("output", StringComparison.OrdinalIgnoreCase)
                        || r.Name.Contains("mask", StringComparison.OrdinalIgnoreCase)
                        || r.Name.Contains("pred", StringComparison.OrdinalIgnoreCase)
                        || r.Name.Contains("image", StringComparison.OrdinalIgnoreCase);
                    if (bestV == null || pix > bestPix || (pix == bestPix && nm && !bestNamed)) { bestV = r; bestPix = pix; bestNamed = nm; }
                }
            }
            if (bestV != null) named = bestV;
        }
        named ??= results.Last();
        return named.AsTensor<float>();
    }

    private static float[,] ExtractMask(Tensor<float> tensor, bool logitsOutput = false)
    {
        var dims = tensor.Dimensions;
        int h = dims[^2], w = dims[^1];
        var mask = new float[h, w];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float v;
                if (dims.Length == 4)
                    v = tensor[0, 0, y, x];
                else if (dims.Length == 3)
                    v = tensor[0, y, x];
                else
                    v = tensor[0];
                // logits 输出(BiRefNet 等):先 sigmoid 转 [0,1];否则已是概率/未归一化值
                if (logitsOutput) v = 1f / (1f + (float)Math.Exp(-v));
                mask[y, x] = v;
            }
        }
        // min-max 归一化(rembg 官方后处理):(v-min)/(max-min),把输出拉开到 [0,1]
        float mn = float.MaxValue, mx = float.MinValue;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float v = mask[y, x];
                if (v < mn) mn = v;
                if (v > mx) mx = v;
            }
        if (mx - mn > 1e-6f)
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    mask[y, x] = (mask[y, x] - mn) / (mx - mn);
        return mask;
    }

    /// <summary>掩码放大到原图尺寸,依次做阈值映射、形态学清洗、主体框选、智能涂抹、羽化、边缘增强,返回一维 alpha(0~1)。</summary>
    private static float[] ProcessMask(float[,] mask, int w, int h,
        int fgThreshold, int bgThreshold, int featherRadius, int edgeStrength,
        int? selX = null, int? selY = null, int? selW = null, int? selH = null,
        IReadOnlyList<CutoutScribble>? scribbles = null, int tolerance = 30, double? brushRadius = null,
        double maxSpread = 0, int morphStrength = 0, bool autoThreshold = false,
        System.Drawing.Bitmap? srcForColor = null)
    {
        int mh = mask.GetLength(0), mw = mask.GetLength(1);

        // 阈值:自适应时按蒙版直方图 Otsu 自动定界(按图调优);
        // 否则用用户滑条。在前景/背景两条路径共用(二元化或线性映射)。
        float fg, bg;
        if (autoThreshold)
            (fg, bg) = OtsuThreshold(mask);
        else
        {
            fg = Math.Clamp(fgThreshold, 0, 255) / 255f;
            bg = Math.Clamp(bgThreshold, 0, 255) / 255f;
        }
        if (fg <= bg) fg = bg + 0.01f;

        // 1) 掩码 → 小灰度位图 → 双三次放大到原尺寸。
        //    形态学清洗开启时:先在蒙版上做 开运算(去背景小噪点岛)+ 轻微腐蚀(去背景边缘残余),
        //    输出近二元蒙版(主体=白/背景=黑),放大后由双三次插值天然羽化;边沿被"收缩"从而剥离背景残余。
        using var maskSmall = new System.Drawing.Bitmap(mw, mh, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        // 用 LockBits 直接写内存(SetPixel 逐像素调用极慢:1024² 要 100 万次,调参时最卡)——快几个数量级
        var msRect = new System.Drawing.Rectangle(0, 0, mw, mh);
        var msData = maskSmall.LockBits(msRect, System.Drawing.Imaging.ImageLockMode.WriteOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                var msPtr = (byte*)msData.Scan0.ToPointer();
                if (morphStrength > 0)
                {
                    var m = new byte[mw * mh];
                    for (int y = 0; y < mh; y++)
                        for (int x = 0; x < mw; x++)
                            m[y * mw + x] = mask[y, x] >= fg ? (byte)1 : (byte)0;
                    MorphBinary(m, mw, mh, morphStrength);
                    for (int y = 0; y < mh; y++)
                    {
                        byte* row = msPtr + y * msData.Stride;
                        for (int x = 0; x < mw; x++)
                        {
                            byte v = (byte)(m[y * mw + x] * 255f);
                            byte* p = row + x * 4;
                            p[0] = v; p[1] = v; p[2] = v; p[3] = v;
                        }
                    }
                }
                else
                {
                    for (int y = 0; y < mh; y++)
                    {
                        byte* row = msPtr + y * msData.Stride;
                        for (int x = 0; x < mw; x++)
                        {
                            byte v = (byte)(Math.Clamp(mask[y, x], 0f, 1f) * 255f);
                            byte* p = row + x * 4;
                            p[0] = v; p[1] = v; p[2] = v; p[3] = v;
                        }
                    }
                }
            }
        }
        finally
        {
            maskSmall.UnlockBits(msData);
        }
        using var maskFull = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(maskFull))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            // rembg 官方:输入拉伸成正方形(size×size)→ 输出蒙版(size×size)直接拉伸回原图即可(无 letterbox 补边)。
            g.DrawImage(maskSmall, 0, 0, w, h);
        }

        // 2) 读回一维 float alpha
        var alpha = new float[w * h];
        var rect = new System.Drawing.Rectangle(0, 0, w, h);
        var data = maskFull.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                var ptr = (byte*)data.Scan0.ToPointer();
                int stride = data.Stride;
                for (int y = 0; y < h; y++)
                {
                    int row = y * w;
                    int i = y * stride;
                    for (int x = 0; x < w; x++)
                        alpha[row + x] = ptr[i + x * 4 + 3] / 255f;   // 读 A 通道(掩码灰度四通道相等,勿依赖 B)
                }
            }
        }
        finally
        {
            maskFull.UnlockBits(data);
        }

        // 3) 阈值映射:<=bg 全透明,>=fg 全不透明,之间线性过渡(自然渐变)。
        //    形态学路径已二元化(不再线性映射,避免把羽化的渐变再压一次);
        //    非形态学路径保留线性映射,让半透明边缘自然过渡。
        if (morphStrength <= 0)
        {
            for (int i = 0; i < alpha.Length; i++)
            {
                float v = alpha[i];
                if (v <= bg) alpha[i] = 0f;
                else if (v >= fg) alpha[i] = 1f;
                else alpha[i] = (v - bg) / (fg - bg);
            }
        }

        // 3.5) 主体框选:区域外强制透明,边界 24px 渐变过渡(柔和,不像硬裁剪;
        //     框内仍按 AI 抠图——框选只是"限定主体范围",不是把框内原样裁下来)
        if (selW is > 0 && selH is > 0)
        {
            int sx = Math.Clamp(selX!.Value, 0, w), sy = Math.Clamp(selY!.Value, 0, h);
            int ex = Math.Clamp(selX.Value + selW.Value, 0, w), ey = Math.Clamp(selY.Value + selH.Value, 0, h);
            const int soft = 24;
            if (ex > sx && ey > sy)
            {
                for (int y = 0; y < h; y++)
                {
                    int row = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        if (x >= sx && x < ex && y >= sy && y < ey) continue;
                        int dx = Math.Max(sx - x, x - (ex - 1));
                        int dy = Math.Max(sy - y, y - (ey - 1));
                        int d = Math.Max(0, Math.Max(dx, dy));
                        alpha[row + x] *= Math.Clamp((soft - d) / (float)soft, 0f, 1f);
                    }
                }
            }
        }

        // 3.6) 智能涂抹:绿色(保留)区域强制前景、红色(删除)区域强制背景,
        // 并按颜色相近度向周边扩散(用户涂到的物体整体被识别;冲突时以用户为准)
        if (scribbles is { Count: > 0 } && srcForColor != null)
            ApplyScribbles(alpha, srcForColor, w, h, scribbles, tolerance, brushRadius, maxSpread);

        // 4) 边缘羽化:对 alpha 做 box blur(滑动窗口,与半径无关的 O(N))
        if (featherRadius > 0)
            BoxBlur(alpha, w, h, Math.Min(featherRadius, 50));

        // 5) 边缘增强:unsharp on alpha,把半透明边缘推向 0 或 1
        if (edgeStrength > 0)
        {
            var blur = new float[alpha.Length];
            Array.Copy(alpha, blur, alpha.Length);
            BoxBlur(blur, w, h, 1);
            float k = edgeStrength / 100f * 3f;
            for (int i = 0; i < alpha.Length; i++)
            {
                float v = alpha[i] + (alpha[i] - blur[i]) * k;
                alpha[i] = Math.Clamp(v, 0f, 1f);
            }
        }

        return alpha;
    }
    /// <summary>
    /// 智能涂抹应用(Lab 色彩相似度 + 相邻像素突变阻断的连通域泛洪填充):
    /// 1) RGB → Lab 色彩空间(感知均匀,判"同色"更符合人眼,比 RGB 欧氏距离准);
    /// 2) 记录绿笔(前景)/红笔(背景)笔迹点;
    /// 3) 泛洪填充:从笔迹点(含笔刷强制区)沿"Lab 颜色接近该支路参考色"的连通区扩散,
    ///    涂一笔,整个同色连通物体被归入(保留/删除);
    ///    边界阻断:相邻像素 Lab 突变 ≥ ΔE15(≈灰度突变 30+)即真实边界,泛洪不跨
    ///    ——比 Canny 边缘图可靠(弱反差边界也挡得住,不会扩散溢出;纹理/渐变不误挡);
    ///    距离上限:泛洪步进距离超过 maxSpread 像素不再扩散(避免"同色连通区巨大/渐变背景"
    ///    把一整片都选中,可多涂几笔覆盖大区域);
    /// 4) 高斯模糊羽化(半径 5px)软化蒙版边缘,过渡自然。
    /// 参考色:每个笔迹点取自身 3×3 邻域平均 Lab,固定不变(不随泛洪移动漂移,
    ///    不会沿渐变蔓延);一笔跨多色时各段按各自局部色扩散,互不拖累。
    /// 后涂优先:同区域先绿后红(或反之)以后画为准(用画笔序号 order 判定)。
    /// </summary>
    private static void ApplyScribbles(float[] alpha, System.Drawing.Bitmap src, int w, int h,
        IReadOnlyList<CutoutScribble> scribbles, int tolerance, double? brushRadius, double maxSpread)
    {
        // 笔刷半径(图片像素,浮点保留精度——不要截断成 int,否则图片放大显示时半径被砍到 1px,涂了没作用)
        double brushR = brushRadius is > 0 ? Math.Clamp(brushRadius.Value, 1, Math.Max(w, h))
            : Math.Max(3, (int)(Math.Min(w, h) * 0.03));
        double r2 = brushR * brushR;

        // 读像素到内存(只读一次)
        var rect = new System.Drawing.Rectangle(0, 0, w, h);
        var data = src.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var stride = data.Stride;
        var px = new byte[w * h * 4];
        try
        {
            unsafe
            {
                var ptr = (byte*)data.Scan0.ToPointer();
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = (y * w + x) * 4;
                        int j = y * stride + x * 4;
                        px[i] = ptr[j]; px[i + 1] = ptr[j + 1]; px[i + 2] = ptr[j + 2];
                    }
            }
        }
        finally
        {
            src.UnlockBits(data);
        }

        // 容差 → Lab 颜色距离阈值(ΔE 感知距离):0=几乎只同色(~8),100=较宽(~40)
        double thr = 8.0 + tolerance / 100.0 * 32.0;
        double thrSq = thr * thr;

        // 1) RGB → Lab(全图一次)
        double[] l = new double[w*h], a = new double[w*h], bb = new double[w*h];
        for (int i = 0; i < w*h; i++)
        {
            int R = px[i*4+2], G = px[i*4+1], B = px[i*4];
            RgbToLab(R, G, B, out l[i], out a[i], out bb[i]);
        }

        // 3) 每笔的编号与类型(用于"后涂优先")。参考色不取整笔平均——一笔跨多种颜色时,
        //    平均色"谁都不像",邻域全部超容差 → 泛洪一步都走不出去(表现为"笔刷不扩散")。
        //    改为每个笔迹点取「自身 3×3 邻域平均 Lab」作该支路的固定参考:
        //    固定不漂移(不随泛洪移动,不会沿渐变蔓延),又贴近笔迹点真实颜色(同色连通区正常扩散)。
        int nS = scribbles.Count;
        var sWant = new int[nS]; var sOrder = new int[nS];
        int order = 0;
        for (int si = 0; si < nS; si++)
        {
            var sb = scribbles[si];
            int wv = sb.Keep ? 1 : 2;
            bool any = false;
            foreach (var (sxp, syp) in sb.Points)
                if (sxp >= 0 && syp >= 0 && sxp < w && syp < h) { any = true; break; }
            if (!any) { sOrder[si] = -1; continue; }
            sWant[si] = wv; sOrder[si] = order++;
        }
        if (order == 0) return;

        // 4) 泛洪填充:从各笔迹点出发,沿"Lab 颜色接近该支路参考色 + 非 Canny 边缘"的连通区扩散。
        //    用 sOrder 实现"后涂优先":重叠处后画的笔盖过先画的(以用户最终意图为准)。
        var want = new byte[w*h];            // 0=未定 1=保留 2=删除
        var orderArr = new int[w*h];         // 该像素被第几笔标记;未标记=-1
        Array.Fill(orderArr, -1);
        var queue = new System.Collections.Generic.Queue<(int X, int Y, double rl, double ra, double rb, int want, int order, int dist)>();
        for (int si = 0; si < nS; si++)
        {
            if (sOrder[si] < 0) continue;
            var sb = scribbles[si];
            int wv = sWant[si], ord = sOrder[si];
            foreach (var (sxp, syp) in sb.Points)
            {
                if (sxp < 0 || syp < 0 || sxp >= w || syp >= h) continue;
                // 支路参考色 = 笔迹点 3×3 邻域平均 Lab(局部颜色,固定;抗单像素噪点)
                double rl2 = 0, ra2 = 0, rb2 = 0; int refN = 0;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int y = syp + dy, x = sxp + dx;
                        if (y < 0 || y >= h || x < 0 || x >= w) continue;
                        int ii = y * w + x;
                        rl2 += l[ii]; ra2 += a[ii]; rb2 += bb[ii]; refN++;
                    }
                if (refN == 0) continue;
                rl2 /= refN; ra2 /= refN; rb2 /= refN;
                // 笔刷半径内强制设为该笔类型(后画优先,不覆盖已有更新一笔)。
                // 关键:强制区每个像素也要入队——否则队列里只有笔迹种子点,种子点的邻居全在
                // 强制区内被"本笔已定"跳过且从未入队,泛洪一步都走不出笔刷盘(表现为"笔刷不扩散")。
                for (int dy = -(int)Math.Ceiling(brushR); dy <= (int)Math.Ceiling(brushR); dy++)
                {
                    int y = syp + dy;
                    if (y < 0 || y >= h) continue;
                    for (int dx = -(int)Math.Ceiling(brushR); dx <= (int)Math.Ceiling(brushR); dx++)
                    {
                        int x = sxp + dx;
                        if (x < 0 || x >= w) continue;
                        if (dx * dx + dy * dy > r2) continue;
                        int ii = y * w + x;
                        if (orderArr[ii] < ord)
                        {
                            want[ii] = (byte)wv; orderArr[ii] = ord;
                            queue.Enqueue((x, y, rl2, ra2, rb2, wv, ord, 0));   // 强制区:距离 0
                        }
                    }
                }
            }
        }
        // 泛洪:邻域 Lab 接近"该支路参考色"才传播;后画优先,不覆盖已有更新一笔。
        // 边界阻断不用 Canny 边缘图——实测 Canny 对弱反差边界(灰度差 30~50)大面积漏检,
        // 泛洪从缺口溢出导致"扩散太大";且阈值调高又挡不住。改用「相邻像素 Lab 突变」阻断:
        // 相邻像素颜色突变 ≥ ΔE 15 ≈ 灰度突变 30+(即真实边界),泛洪不跨;
        // 内部纹理/渐变相邻差小,不误挡。按像素即时计算,无需全图边缘预扫描。
        // 距离上限:泛洪步进 dist 超过 maxSpread 即停(曼哈顿距离近似),防止同色渐变区域铺满全图。
        const double adjThrSq = 15.0 * 15.0;
        double spreadCap = maxSpread > 0 ? maxSpread : double.MaxValue;
        while (queue.Count > 0)
        {
            var (cx, cy, rl2, ra2, rb2, wv, ord, dist) = queue.Dequeue();
            int cidx = cy * w + cx;
            for (int d = 0; d < 4; d++)
            {
                int nx = cx + (d == 0 ? 1 : d == 1 ? -1 : 0);
                int ny = cy + (d == 2 ? 1 : d == 3 ? -1 : 0);
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                int nidx = ny * w + nx;
                if (orderArr[nidx] > ord) continue;              // 已被更新一笔定,不覆盖
                if (orderArr[nidx] == ord && want[nidx] == wv) continue;   // 本笔已定,跳过(防死循环)
                // 相邻像素 Lab 突变 → 真实边界,阻断(比 Canny 边缘图可靠:弱反差也挡得住)
                double adjl = l[nidx] - l[cidx], adja = a[nidx] - a[cidx], adjb = bb[nidx] - bb[cidx];
                if (adjl*adjl + adja*adja + adjb*adjb > adjThrSq) continue;
                int ndist = dist + 1;
                if (ndist > spreadCap) continue;                 // 超出扩散距离上限
                double dl = l[nidx] - rl2, da = a[nidx] - ra2, db3 = bb[nidx] - rb2;
                double de2 = dl*dl + da*da + db3*db3;
                if (de2 > thrSq) continue;                       // 与该支路参考色差大,不扩散
                want[nidx] = (byte)wv;
                orderArr[nidx] = ord;
                queue.Enqueue((nx, ny, rl2, ra2, rb2, wv, ord, ndist));
            }
        }

        // 应用:保留→alpha=1,删除→alpha=0(彻底)
        for (int i = 0; i < w * h; i++)
        {
            if (want[i] == 1) alpha[i] = 1f;
            else if (want[i] == 2) alpha[i] = 0f;
        }

        // 5) 高斯模糊羽化(半径 5px):软化蒙版边缘,过渡自然
        GaussianBlur(alpha, w, h, 5);
    }

    /// <summary>sRGB → CIELAB(感知均匀色彩空间;用于"同色"判断,比 RGB 欧氏距离更准)。</summary>
    private static void RgbToLab(int R, int G, int B, out double L, out double A, out double oB)
    {
        // sRGB → 线性化
        double fr = R / 255.0, fg = G / 255.0, fb = B / 255.0;
        fr = fr <= 0.04045 ? fr / 12.92 : Math.Pow((fr + 0.055) / 1.055, 2.4);
        fg = fg <= 0.04045 ? fg / 12.92 : Math.Pow((fg + 0.055) / 1.055, 2.4);
        fb = fb <= 0.04045 ? fb / 12.92 : Math.Pow((fb + 0.055) / 1.055, 2.4);
        // 线性 sRGB → XYZ(D65)
        double X = (fr * 0.4124564 + fg * 0.3575761 + fb * 0.1804375) / 0.95047;
        double Y = (fr * 0.2126729 + fg * 0.7151522 + fb * 0.0721750) / 1.00000;
        double Z = (fr * 0.0193339 + fg * 0.1191920 + fb * 0.9503041) / 1.08883;
        // XYZ → Lab
        double fx = F(X), fy = F(Y), fz = F(Z);
        L = 116.0 * fy - 16.0;
        A = 500.0 * (fx - fy);
        oB = 200.0 * (fy - fz);
        static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : (7.787 * t + 16.0 / 116.0);
    }

    /// <summary>高斯模糊(半径 radius,sigma=radius/3 近似;分离式先水平后垂直)。</summary>
    private static void GaussianBlur(float[] a, int w, int h, int radius)
    {
        if (radius <= 0 || a.Length == 0) return;
        double sigma = Math.Max(0.5, radius / 3.0);
        int k = radius * 2 + 1;
        var kernel = new double[k];
        double sum = 0;
        for (int i = -radius; i <= radius; i++)
        {
            double v = Math.Exp(-(i * i) / (2 * sigma * sigma));
            kernel[i + radius] = v; sum += v;
        }
        for (int i = 0; i < k; i++) kernel[i] /= sum;

        var tmp = new float[a.Length];
        int wm = w - 1, hm = h - 1;
        // 水平
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                double s = 0;
                for (int i = -radius; i <= radius; i++)
                    s += a[row + Math.Clamp(x + i, 0, wm)] * kernel[i + radius];
                tmp[row + x] = (float)s;
            }
        }
        // 垂直
        for (int x = 0; x < w; x++)
        {
            for (int y = 0; y < h; y++)
            {
                double s = 0;
                for (int i = -radius; i <= radius; i++)
                    s += tmp[Math.Clamp(y + i, 0, hm) * w + x] * kernel[i + radius];
                a[y * w + x] = (float)s;
            }
        }
    }

    /// <summary>加载图片并应用 EXIF 方向旋转(与 RunCore/输出/预览使用同一坐标系)。</summary>
    private static System.Drawing.Bitmap LoadRotatedBitmap(string input)
    {
        var bmp = System.Drawing.Image.FromFile(input) as System.Drawing.Bitmap
            ?? throw new InvalidOperationException("无法读取图片");
        ApplyExifRotation(bmp, input);
        return bmp;
    }

    /// <summary>应用 EXIF 方向旋转(手机照片;System.Drawing 读取不自动旋转,须手动处理,
    /// 否则掩码/坐标与预览显示(已旋转)不一致)。</summary>
    private static void ApplyExifRotation(System.Drawing.Bitmap bmp, string path)
    {
        try
        {
            using var probe = new System.Drawing.Bitmap(path);
            foreach (System.Drawing.Imaging.PropertyItem pi in probe.PropertyItems)
            {
                if (pi.Id == 0x0112 && pi.Value is { Length: > 0 })
                {
                    switch (pi.Value[0])
                    {
                        case 6: bmp.RotateFlip(System.Drawing.RotateFlipType.Rotate90FlipNone); break;
                        case 8: bmp.RotateFlip(System.Drawing.RotateFlipType.Rotate270FlipNone); break;
                        case 3: bmp.RotateFlip(System.Drawing.RotateFlipType.Rotate180FlipNone); break;
                    }
                    break;
                }
            }
        }
        catch { /* 无 EXIF 或读取失败按原样处理 */ }
    }

    /// <summary>滑动窗口 box blur(水平 + 垂直各一遍,边界取 clamp)。</summary>
    private static void BoxBlur(float[] a, int w, int h, int radius)
    {
        if (radius <= 0 || a.Length == 0) return;
        var tmp = new float[a.Length];
        int k = radius * 2 + 1;
        float inv = 1f / k;
        int wm = w - 1, hm = h - 1;

        // 水平
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            float sum = 0f;
            for (int x = -radius; x <= radius; x++)
                sum += a[row + Math.Clamp(x, 0, wm)];
            for (int x = 0; x < w; x++)
            {
                tmp[row + x] = sum * inv;
                int add = Math.Clamp(x + radius + 1, 0, wm);
                int sub = Math.Clamp(x - radius, 0, wm);
                sum += a[row + add] - a[row + sub];
            }
        }

        // 垂直
        for (int x = 0; x < w; x++)
        {
            float sum = 0f;
            for (int y = -radius; y <= radius; y++)
                sum += tmp[Math.Clamp(y, 0, hm) * w + x];
            for (int y = 0; y < h; y++)
            {
                a[y * w + x] = sum * inv;
                int add = Math.Clamp(y + radius + 1, 0, hm);
                int sub = Math.Clamp(y - radius, 0, hm);
                sum += tmp[add * w + x] - tmp[sub * w + x];
            }
        }
    }

    /// <summary>自适应阈值:对蒙版灰度直方图做 Otsu,自动找出前景/背景分界(按图调优,
    /// 深色/深红背景这类与主体接近的蒙版值能自动被归到背景)。返回 (fg,bg) ∈ [0,1]。</summary>
    private static (float fg, float bg) OtsuThreshold(float[,] mask)
    {
        int mh = mask.GetLength(0), mw = mask.GetLength(1);
        var hist = new int[256];
        int n = 0;
        for (int y = 0; y < mh; y++)
            for (int x = 0; x < mw; x++)
            {
                hist[(int)(Math.Clamp(mask[y, x], 0f, 1f) * 255f + 0.5f)]++;
                n++;
            }
        if (n == 0) return (0.78f, 0.42f);
        double sumAll = 0;
        for (int i = 0; i < 256; i++) sumAll += (double)i * hist[i];
        double sumB = 0; int wB = 0; double maxVar = -1; int best = 128;
        for (int t = 0; t < 256; t++)
        {
            wB += hist[t]; if (wB == 0) continue;
            int wF = n - wB; if (wF == 0) break;
            sumB += (double)t * hist[t];
            double mB = sumB / wB, mF = (sumAll - sumB) / wF;
            double v = (double)wB * wF * (mB - mF) * (mB - mF);
            if (v > maxVar) { maxVar = v; best = t; }
        }
        float fg = Math.Clamp(best / 255f, 0.35f, 0.95f);
        float bg = Math.Clamp(fg - 0.14f, 0.03f, 0.85f);
        return (fg, bg);
    }

    /// <summary>蒙版二元形态学清洗:开运算(去背景小噪点岛)+ 轻微腐蚀(去背景边缘残余)。
    /// strength 0~100 映射到腐蚀半径(保护细节:上限小)。输入字节 0/1,原地写回。</summary>
    private static void MorphBinary(byte[] m, int mw, int mh, int strength)
    {
        int erode = Math.Clamp((int)Math.Round(strength / 100.0 * 3), 1, 3);   // 开运算半径(去噪点岛)
        int shave = Math.Clamp((int)Math.Round(strength / 100.0 * 2), 1, 2);   // 额外轻微腐蚀(去边缘残余)
        // 开运算:腐蚀 erode → 膨胀 erode,去掉背景里的小前景噪点岛
        var tmp = ErodeBinary(m, mw, mh, erode);
        var opened = DilateBinary(tmp, mw, mh, erode);
        // 轻微腐蚀:再腐蚀 shave,把边缘残留的背景"外壳"剥掉(同时略收细主体边)
        var final = ErodeBinary(opened, mw, mh, shave);
        Array.Copy(final, m, m.Length);
    }

    private static byte[] ErodeBinary(byte[] src, int w, int h, int r)
    {
        var horiz = new byte[src.Length];
        var res = new byte[src.Length];
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                byte mn = 1;
                for (int i = -r; i <= r; i++)
                {
                    int xx = Math.Clamp(x + i, 0, w - 1);
                    if (src[row + xx] == 0) { mn = 0; break; }
                }
                horiz[row + x] = mn;
            }
        }
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                byte mn = 1;
                for (int i = -r; i <= r; i++)
                {
                    int yy = Math.Clamp(y + i, 0, h - 1);
                    if (horiz[yy * w + x] == 0) { mn = 0; break; }
                }
                res[row + x] = mn;
            }
        }
        return res;
    }

    private static byte[] DilateBinary(byte[] src, int w, int h, int r)
    {
        var horiz = new byte[src.Length];
        var res = new byte[src.Length];
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                byte mx = 0;
                for (int i = -r; i <= r; i++)
                {
                    int xx = Math.Clamp(x + i, 0, w - 1);
                    if (src[row + xx] == 1) { mx = 1; break; }
                }
                horiz[row + x] = mx;
            }
        }
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                byte mx = 0;
                for (int i = -r; i <= r; i++)
                {
                    int yy = Math.Clamp(y + i, 0, h - 1);
                    if (horiz[yy * w + x] == 1) { mx = 1; break; }
                }
                res[row + x] = mx;
            }
        }
        return res;
    }

    /// <summary>把 alpha(0~1)写入原图 alpha 通道后保存 PNG。</summary>
    private static void SaveWithAlpha(System.Drawing.Bitmap src, float[] alpha, string output)
    {
        int w = src.Width, h = src.Height;
        using var result = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        // DPI 继承源图:GDI+ 新位图默认 96dpi,若源图非 96(如 300dpi 打印图),
        // 查看器按 DPI 显示会导致"内容缩放";像素数与源图一致 + DPI 一致 = 所见即所得。
        double dpiX = src.HorizontalResolution > 0.01 ? src.HorizontalResolution : 96.0;
        double dpiY = src.VerticalResolution > 0.01 ? src.VerticalResolution : 96.0;
        result.SetResolution((float)dpiX, (float)dpiY);
        using (var g = System.Drawing.Graphics.FromImage(result))
        {
            g.DrawImageUnscaled(src, 0, 0);
        }

        var rect = new System.Drawing.Rectangle(0, 0, w, h);
        var resData = result.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadWrite,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                var resPtr = (byte*)resData.Scan0.ToPointer();
                int stride = resData.Stride;
                for (int y = 0; y < h; y++)
                {
                    int row = y * w;
                    int i = y * stride;
                    for (int x = 0; x < w; x++)
                        resPtr[i + x * 4 + 3] = (byte)(Math.Clamp(alpha[row + x], 0f, 1f) * 255f);
                }
            }
        }
        finally
        {
            result.UnlockBits(resData);
        }
        result.Save(output, System.Drawing.Imaging.ImageFormat.Png);
    }
}
