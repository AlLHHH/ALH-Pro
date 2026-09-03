// AudioEnhanceService.cs — HT-Demucs 音乐分离/降噪(纯 C#,ONNX Runtime,MIT 可发布)
// 功能:音乐源分离(人声/伴奏/鼓/贝斯/其他)、卡拉OK(去人声)、仅人声/仅伴奏输出。
// 模型:engines/demucs/htdemucs.onnx(158MB fp16,MIT,StemSplitio/htdemucs-onnx)
// 流程:输入任意音频 → ffmpeg 转 44.1kHz 立体声 WAV → 分块(7.8s+重叠窗)推理 → 输出所选轨 WAV。
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ALHPro;

public static class AudioEnhanceService
{
    const int SAMPLE_RATE = 44100;
    const double SEGMENT_S = 7.8;
    const int N_SAMPLES = (int)(SEGMENT_S * SAMPLE_RATE);   // 343,980
    const int N_CHANNELS = 2;

    /// <summary>分离目标:0=人声,1=伴奏(去人声),2=鼓,3=贝斯,4=其他,5=仅人声+伴奏重混增强(保原声场)。</summary>
    public static string[] TargetLabels = { "人声", "伴奏(去人声)", "鼓", "贝斯", "其他", "人声+伴奏" };

    public static string? FindModel()
    {
        var root = Path.Combine(EngineService.EnginesDir, "demucs");
        // 优先"人声微调版"(htdemucs_ft_vocals):专门优化人声提取更干净 → 伴奏=原曲−人声 残差更小
        // 实测:标准版伴奏残留人声(分离误差),ft_vocals 版明显改善。缺失时回退标准 htdemucs。
        if (Directory.Exists(root))
        {
            foreach (var f in Directory.EnumerateFiles(root, "htdemucs_ft_vocals*.onnx", SearchOption.AllDirectories))
                return f;
            foreach (var f in Directory.EnumerateFiles(root, "htdemucs*.onnx", SearchOption.AllDirectories))
                return f;
        }
        var direct = Path.Combine(EngineService.EnginesDir, "htdemucs.onnx");
        return File.Exists(direct) ? direct : null;
    }

    // 【修复】会话/设备缓存(替换原来无用的 _sessions/_locks):
    // _dmlBad: 记录 DirectML 失败的设备号(进程内),避免每个分块都反复重试 GPU → 灾难级慢;
    // _cpuSession: 复用的 CPU 会话(DML 失败/纯 CPU 时用),避免每个分块都新建 158MB 模型 → 同样灾难级慢;
    // _gpuSession: 按 gpuId 复用的 GPU 会话(避免每批新建,但音频单次任务并发低,仍加锁保护)。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> _dmlBad = new();
    private static InferenceSession? _cpuSession;
    private static string? _cpuSessionPath;
    private static readonly object _sessionLock = new();
    private static InferenceSession? _gpuSession;
    private static int _gpuSessionId = int.MinValue;

    /// <summary>分离音频。input=任意音频(程序内先用 ffmpeg 转成 44.1k stereo wav);输出所选轨 wav。
    /// target:0人声 1伴奏 2鼓 3贝斯 4其他 5重混 6分离(输出 人声+伴奏 两文件)。
    /// vocalStrength=0~1:人声轨混合比例(0=原曲,0.5=一半,1=纯人声)——用于"人声强度"滑条手动调整。</summary>
    public static async Task SeparateAsync(string inputWav, string outputWav, int target,
        int gpuId = -1, float vocalStrength = 1f,
        IProgress<(int pct, string msg)>? progress = null, CancellationToken ct = default)
    {
        var modelPath = FindModel()
            ?? throw new FileNotFoundException("未找到 HT-Demucs 模型,请放入 engines\\demucs\\htdemucs.onnx");
        await Task.Run(() => RunCore(inputWav, outputWav, target, modelPath, gpuId, vocalStrength, progress, ct), ct);
        progress?.Report((100, "完成"));
    }

    private static void RunCore(string inputWav, string outputWav, int target, string modelPath, int gpuId,
        float vocalStrength, IProgress<(int pct, string msg)>? progress, CancellationToken ct)
    {
        // 【修复】复用会话(原先每次/每批都新建 158MB 模型,慢且 DML 失败时每分块重建):
        // 优先复用 GPU 会话;若该设备已标记 DML 失败,则复用 CPU 会话。音频单次任务并发低,用锁串行化创建。
        InferenceSession session;
        lock (_sessionLock)
        {
            if (gpuId >= 0 && !_dmlBad.ContainsKey(gpuId))
            {
                if (_gpuSession == null || _gpuSessionId != gpuId)
                {
                    _gpuSession?.Dispose();
                    var opts = new SessionOptions();
                    try { opts.AppendExecutionProvider_DML(EngineService.ToDmlDevice(gpuId)); } catch { /* DML 不可用回退 CPU */ }
                    _gpuSession = new InferenceSession(modelPath, opts);
                    _gpuSessionId = gpuId;
                }
                session = _gpuSession;
            }
            else
            {
                if (_cpuSession == null || _cpuSessionPath != modelPath)
                {
                    _cpuSession?.Dispose();
                    _cpuSession = new InferenceSession(modelPath, new SessionOptions());
                    _cpuSessionPath = modelPath;
                }
                session = _cpuSession;
            }
        }

            // 读取 WAV(44.1k stereo float32)
            var (mix, samples) = ReadWav(inputWav);
            int total = samples;
            // 内存预估(4 轨输出缓冲/选择/拷贝等 ≈ 80B/采样):超阈值提前明确报错,避免整进程 OOM
            double estMB = total * 80.0 / (1024.0 * 1024.0);
            if (estMB > 2500)
                throw new InvalidOperationException(
                    $"音频过长({(double)total / SAMPLE_RATE / 60.0:0.##} 分钟),AI 分离内存需求约 {estMB / 1024.0:0.#}GB,可能超出本机内存 — 请用波形两端裁剪缩短后再试,或分段处理。");
            int overlap = N_SAMPLES / 4;
            int stride = N_SAMPLES - overlap;
            int nChunks = Math.Max(1, (total + stride - 1) / stride);
            var window = MakeWindow(N_SAMPLES, overlap);

            // 输出累积(4 轨 × 2 声道 × total)
            var outBuf = new float[4, N_CHANNELS, total];
            var weight = new float[total];

            for (int i = 0; i < nChunks; i++)
            {
                ct.ThrowIfCancellationRequested();
                int start = i * stride;
                int end = Math.Min(start + N_SAMPLES, total);
                var chunk = new float[1 * N_CHANNELS * N_SAMPLES];
                for (int c = 0; c < N_CHANNELS; c++)
                    for (int s = 0; s < end - start; s++)
                        chunk[0 * N_CHANNELS * N_SAMPLES + c * N_SAMPLES + s] = mix[c, start + s];
                // 尾部补零(不足 N_SAMPLES)
                var tensor = new DenseTensor<float>(chunk, new[] { 1, N_CHANNELS, N_SAMPLES });
                IDisposableReadOnlyCollection<DisposableNamedOnnxValue>? results = null;
                try
                {
                    results = session.Run(new[] { NamedOnnxValue.CreateFromTensor("mix", tensor) });
                }
                catch (Exception ex) when (gpuId >= 0)
                {
                    // 【修复】DirectML 失败 → 标记该设备不再用 GPU,改用【复用的 CPU 会话】重试(不再每分块新建 158MB 模型、
                    // 不再每块反复重试 GPU=灾难级慢)。后续分块直接走 CPU。
                    AppLogger.Info($"⚠ 音频分离 GPU 推理失败({ex.Message.Split('\n')[0]}),标记该 GPU 不可用,改用 CPU 会话");
                    _dmlBad[gpuId] = true;
                    InferenceSession cpuS;
                    lock (_sessionLock)
                    {
                        if (_cpuSession == null || _cpuSessionPath != modelPath)
                        {
                            _cpuSession?.Dispose();
                            _cpuSession = new InferenceSession(modelPath, new SessionOptions());
                            _cpuSessionPath = modelPath;
                        }
                        cpuS = _cpuSession;
                    }
                    results = cpuS.Run(new[] { NamedOnnxValue.CreateFromTensor("mix", tensor) });
                }
                using (results)
                {
                    var stems = results!.First().AsTensor<float>();   // (1,4,2,N)
                    int clen = end - start;
                    for (int st = 0; st < 4; st++)
                        for (int c = 0; c < N_CHANNELS; c++)
                            for (int s = 0; s < clen; s++)
                                outBuf[st, c, start + s] += stems[0, st, c, s] * window[s];
                    for (int s = 0; s < clen; s++) weight[start + s] += window[s];
                }
                progress?.Report(((int)((double)(i + 1) / nChunks * 90), $"分离 {i + 1}/{nChunks} 段..."));
            }

            // 归一化权重 + 选轨输出
            int outCount = target == 6 ? 2 : target == 7 ? 4 : 1;   // 6=人声+伴奏;7=四轨全部;其余 1 个
            var sel = new float[outCount, N_CHANNELS, total];
            for (int c = 0; c < N_CHANNELS; c++)
                for (int s = 0; s < total; s++)
                {
                    float w = Math.Max(weight[s], 1e-8f);
                    // 实测(用户试听 10s《GIRL LIKE ME》):单体输出轨序 = 轨0:伴奏 · 轨1/2:其他 · 轨3:人声!
                    // (官方"drums/bass/other/vocals"标注与引擎实际输出不符——以听感为准)
                    float acc1 = outBuf[0, c, s] / w;   // 伴奏(轨0)
                    float o1 = outBuf[1, c, s] / w;     // 其他1
                    float o2 = outBuf[2, c, s] / w;     // 其他2
                    float v = outBuf[3, c, s] / w;      // 人声(轨3)
                    float org = mix[c, s];              // 原曲
                    float acc = acc1;                   // 伴奏 = 轨0(轨1/2 奇怪,不加)
                    // 伴奏洗净力度(vocalStrength 0~100;k=1=精确全洗"伴奏=原曲−人声",>1=过洗削伴奏,<1=留人声)
                    // 默认传 100 → k=1(标准);不再有滑条(已删),固定标准全洗。
                    float k = Math.Clamp(vocalStrength / 100f, 0f, 1.5f);
                    float vM = v;                        // 人声输出纯(不混原曲,分离就是要纯人声)
                    float accM = org - k * v;
                    if (target >= 100)
                    {
                        // 自定义组合:100+bitmask(1人声 2伴奏 4其他1 8其他2)——左右声道分别合成
                        int mask = target - 100;
                        float sum = 0;
                        if ((mask & 1) != 0) sum += vM;
                        if ((mask & 2) != 0) sum += accM;
                        if ((mask & 4) != 0) sum += o1;
                        if ((mask & 8) != 0) sum += o2;
                        sel[0, c, s] = sum;
                    }
                    else if (target == 6)
                    {
                        sel[0, c, s] = vM;                // 人声(轨3,强度混合)
                        sel[1, c, s] = accM;              // 伴奏(原曲−人声,干净)
                    }
                    else if (target == 7)
                    {
                        sel[0, c, s] = vM;                // 人声(轨3)
                        sel[1, c, s] = accM;              // 伴奏(原曲−人声)
                        sel[2, c, s] = o1;                // 其他1(轨1)
                        sel[3, c, s] = o2;                // 其他2(轨2)
                    }
                    else
                    {
                        sel[0, c, s] = target switch
                        {
                            0 => vM,                      // 人声(强度混合)
                            1 => accM,                    // 伴奏(原曲−人声)
                            2 => o1,                      // 其他1
                            3 => o2,                      // 其他2
                            4 => accM,                    // 伴奏(近似)
                            _ => vM + accM,               // 人声+伴奏(重混=近似原曲)
                        };
                    }
                }
            if (target == 6)
            {
                // 分离:输出 2 个文件(人声/伴奏,从 outputWav 派生文件名)
                var dir = System.IO.Path.GetDirectoryName(outputWav) ?? ".";
                var baseName = System.IO.Path.GetFileNameWithoutExtension(outputWav);
                var ext = System.IO.Path.GetExtension(outputWav);
                var vocals = System.IO.Path.Combine(dir, baseName + "_人声" + ext);
                var accomp = System.IO.Path.Combine(dir, baseName + "_伴奏" + ext);
                var v = new float[N_CHANNELS, total];
                var a2 = new float[N_CHANNELS, total];
                for (int c = 0; c < N_CHANNELS; c++)
                    for (int s = 0; s < total; s++)
                    {
                        v[c, s] = sel[0, c, s];
                        a2[c, s] = sel[1, c, s];
                    }
                WriteWav(vocals, v, total);
                WriteWav(accomp, a2, total);
            }
            else if (target == 7)
            {
                // 全轨:输出 4 个文件(人声/伴奏/其他1/其他2)——一次推理供"分离+升采样率"共用,免两次分轨
                var dir = System.IO.Path.GetDirectoryName(outputWav) ?? ".";
                var baseName = System.IO.Path.GetFileNameWithoutExtension(outputWav);
                var ext = System.IO.Path.GetExtension(outputWav);
                var names = new[] { "_人声", "_伴奏", "_其他1", "_其他2" };
                for (int st = 0; st < 4; st++)
                {
                    var path = System.IO.Path.Combine(dir, baseName + names[st] + ext);
                    var d = new float[N_CHANNELS, total];
                    for (int c = 0; c < N_CHANNELS; c++)
                        for (int s = 0; s < total; s++)
                            d[c, s] = sel[st, c, s];
                    WriteWav(path, d, total);
                }
            }
            else
            {
                var only = new float[N_CHANNELS, total];
                for (int c = 0; c < N_CHANNELS; c++)
                    for (int s = 0; s < total; s++)
                        only[c, s] = sel[0, c, s];
                WriteWav(outputWav, only, total);
            }
    }

    private static float[] MakeWindow(int n, int overlap)
    {
        var w = new float[n];
        for (int i = 0; i < n; i++) w[i] = 1f;
        for (int i = 0; i < overlap; i++)
        {
            float f = (float)i / overlap;
            w[i] = f;
            w[n - 1 - i] = f;
        }
        return w;
    }

    /// <summary>读取 16-bit PCM WAV(44.1k 立体声),返回 (float[channels, samples], sampleCount)。</summary>
    private static (float[,], int) ReadWav(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        // RIFF 头
        string riff = new string(br.ReadChars(4));
        if (riff != "RIFF") throw new InvalidDataException("不是 WAV");
        br.ReadInt32();
        string wave = new string(br.ReadChars(4));
        int fmtChunk = 0; short audioFormat = 0, channels = 0; int sampleRate = 0, bits = 0;
        while (fs.Position < fs.Length && fmtChunk == 0)
        {
            string id = new string(br.ReadChars(4));
            int size = br.ReadInt32();
            if (id == "fmt ")
            {
                audioFormat = br.ReadInt16();
                channels = br.ReadInt16();
                sampleRate = br.ReadInt32();
                br.ReadInt32();
                br.ReadInt16();
                bits = br.ReadInt16();
                fmtChunk = 1;
                // 跳过剩余 fmt
                if (size > 16) fs.Seek(size - 16, SeekOrigin.Current);
            }
            else fs.Seek(size + (size % 2), SeekOrigin.Current);
        }
        while (true)
        {
            if (fs.Position + 8 > fs.Length) break;
            string id = new string(br.ReadChars(4));
            int size = br.ReadInt32();
            if (id == "data")
            {
                int bytesPerSample = bits / 8;
                int samples = size / (bytesPerSample * channels);
                var mix = new float[channels, samples];
                for (int s = 0; s < samples; s++)
                    for (int c = 0; c < channels; c++)
                        mix[c, s] = br.ReadInt16() / 32768f;
                // 立体声保护:Demucs 需双声道;单声道输入复制到双声道(避免越界崩溃)
                if (channels == 1)
                {
                    var stereo = new float[2, samples];
                    for (int s = 0; s < samples; s++) { stereo[0, s] = mix[0, s]; stereo[1, s] = mix[0, s]; }
                    return (stereo, samples);
                }
                return (mix, samples);
            }
            fs.Seek(size + (size % 2), SeekOrigin.Current);
        }
        throw new InvalidDataException("WAV 无 data 块");
    }

    /// <summary>写出 16-bit PCM WAV(立体声)。</summary>
    private static void WriteWav(string path, float[,] data, int samples)
    {
        int channels = data.GetLength(0);
        int dataSize = samples * channels * 2;
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataSize);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1);            // PCM
        bw.Write((short)channels);
        bw.Write(SAMPLE_RATE);
        bw.Write(SAMPLE_RATE * channels * 2);
        bw.Write((short)(channels * 2));
        bw.Write((short)16);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(dataSize);
        for (int s = 0; s < samples; s++)
            for (int c = 0; c < channels; c++)
                bw.Write((short)(Math.Clamp(data[c, s], -1f, 1f) * 32767f));
    }
}
