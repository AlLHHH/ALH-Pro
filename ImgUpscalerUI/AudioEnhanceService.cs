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
        if (Directory.Exists(root))
            foreach (var f in Directory.EnumerateFiles(root, "htdemucs*.onnx", SearchOption.AllDirectories))
                return f;
        var direct = Path.Combine(EngineService.EnginesDir, "htdemucs.onnx");
        return File.Exists(direct) ? direct : null;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, InferenceSession> _sessions = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    /// <summary>分离音频。input=任意音频(程序内先用 ffmpeg 转成 44.1k stereo wav);输出所选轨 wav。
    /// target:0人声 1伴奏 2鼓 3贝斯 4其他 5重混 6分离(输出 人声+伴奏 两文件)。</summary>
    public static async Task SeparateAsync(string inputWav, string outputWav, int target,
        int gpuId = -1, IProgress<(int pct, string msg)>? progress = null, CancellationToken ct = default)
    {
        var modelPath = FindModel()
            ?? throw new FileNotFoundException("未找到 HT-Demucs 模型,请放入 engines\\demucs\\htdemucs.onnx");
        await Task.Run(() => RunCore(inputWav, outputWav, target, modelPath, gpuId, progress, ct), ct);
        progress?.Report((100, "完成"));
    }

    private static void RunCore(string inputWav, string outputWav, int target, string modelPath, int gpuId,
        IProgress<(int pct, string msg)>? progress, CancellationToken ct)
    {
        var key = (modelPath, gpuId);
        // 分离是用户单次操作,无需并发;且全局 gate.Wait() 在后台线程 + UI 上下文下可能死锁(实测 CPU 0 增量)。
        // 直接每次新建 session(不复用 gate/缓存)——简单可靠,158MB 模型加载约 2~5 秒,可接受。
        var opts0 = new SessionOptions();
        if (gpuId >= 0)
        {
            try { opts0.AppendExecutionProvider_DML(gpuId); } catch { /* DirectML 不可用回退 CPU */ }
        }
        using var session = new InferenceSession(modelPath, opts0);

            // 读取 WAV(44.1k stereo float32)
            var (mix, samples) = ReadWav(inputWav);
            int total = samples;
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
                    // DirectML 失败 → 改用 CPU 重试(兼容);新建 CPU session(原复用已删)
                    progress?.Report((0, "⚠ GPU 推理失败,改用 CPU 重试..."));
                    using var cpuSession = new InferenceSession(modelPath, new SessionOptions());
                    results = cpuSession.Run(new[] { NamedOnnxValue.CreateFromTensor("mix", tensor) });
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
            int outCount = target == 6 ? 2 : 1;   // 分离=2 个(人声+伴奏),其余 1 个
            var sel = new float[outCount, N_CHANNELS, total];
            for (int c = 0; c < N_CHANNELS; c++)
                for (int s = 0; s < total; s++)
                {
                    float w = Math.Max(weight[s], 1e-8f);
                    float a = outBuf[0, c, s] / w;   // vocals
                    float b = outBuf[1, c, s] / w;   // drums
                    float d = outBuf[2, c, s] / w;   // bass
                    float o = outBuf[3, c, s] / w;   // other
                    if (target == 6)
                    {
                        sel[0, c, s] = a;                 // 人声
                        sel[1, c, s] = b + d + o;         // 伴奏(去人声)
                    }
                    else
                    {
                        sel[0, c, s] = target switch
                        {
                            0 => a,                       // 人声
                            1 => b + d + o,               // 伴奏(去人声)
                            2 => b,                       // 鼓
                            3 => d,                       // 贝斯
                            4 => o,                       // 其他
                            _ => a + b + d + o,           // 人声+伴奏(重混=近似原曲,增强后)
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
