// LavaSrService.cs — 音频超分辨率(带宽扩展,Apache-2.0/MIT):
// LavaSR(Sharma, Interspeech 2026):输入 8~48kHz → 输出 48kHz(补高频,保留低频)。
// 流程:源→16k→(可选降噪)→44.1k → STFT → mel→backbone ONNX → spec_head ONNX → ISTFT
//      → Linkwitz-Riley 合并(低频保留原信号)→ 48kHz。纯 ONNX Runtime + C# DSP(无 Python)。
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ALHPro;

public static class LavaSrService
{
    const int ENH_SR = 44100, OUT_SR = 48000, NFFT = 2048, HOP = 512, NMELS = 80;
    const int DEN_SR = 16000;

    static InferenceSession? _backbone, _head;
    static float[,]? _melFb;
    static readonly object _lock = new();

    public static string? ModelsDir => Path.Combine(EngineService.EnginesDir, "lavasr");

    /// <summary>模型就绪:engines/lavasr/ 下有 backbone.onnx + spec_head.onnx。</summary>
    public static bool Available()
    {
        var d = ModelsDir;
        return d != null && File.Exists(Path.Combine(d, "backbone.onnx")) && File.Exists(Path.Combine(d, "spec_head.onnx"));
    }

    /// <summary>超分一个 WAV(读 16-bit PCM,任意采样率)→ 48kHz 输出。inputSampleRate 需已知。</summary>
    public static async Task<byte[]> UpscaleWavAsync(string inputWav, int inputSampleRate,
        IProgress<(int pct, string msg)>? progress = null, CancellationToken ct = default)
    {
        var (wave, sr) = ReadWav16(inputWav);
        if (sr != inputSampleRate) sr = inputSampleRate;
        progress?.Report((5, "音频超分:准备(重采样)..."));
        return await Task.Run(() =>
        {
            var (backbone, head, melFb) = EnsureSessions();
            float[] merged = RunCore(wave, sr, backbone, head, melFb, progress, ct);
            return WriteWav16(merged, OUT_SR);
        }, ct);
    }

    static (InferenceSession, InferenceSession, float[,]) EnsureSessions()
    {
        lock (_lock)
        {
            if (_backbone == null || _head == null)
            {
                var dir = ModelsDir!;
                _backbone = new InferenceSession(Path.Combine(dir, "backbone.onnx"));
                _head = new InferenceSession(Path.Combine(dir, "spec_head.onnx"));
                _melFb = To2D(AudioSrsDsp.BuildMelFilterbank(ENH_SR, NFFT, NMELS, 0.0, 8000.0));
            }
            return (_backbone!, _head!, _melFb!);
        }
    }

    static float[] RunCore(float[] wave, int sr, InferenceSession backbone, InferenceSession head,
        float[,] melFb, IProgress<(int pct, string msg)>? progress, CancellationToken ct)
    {
        // 1) → 16k → (去噪可选,跳) → 44.1k
        float[] w16 = AudioSrsDsp.ResamplePoly(wave, DEN_SR, sr);
        // 16k→44.1k:先 44100(up) 后 16000(down) 的 resample_poly 等价:up=44100/g, down=16000/g
        int g = Gcd(44100, 16000);
        float[] w441 = AudioSrsDsp.ResamplePoly(w16, 44100 / g, 16000 / g);
        progress?.Report((20, "音频超分:STFT 频谱..."));
        ct.ThrowIfCancellationRequested();

        // 2) STFT(2048/512, hann) → |mag| → mel(80) → log
        var spec = AudioSrsDsp.Stft(w441, NFFT, HOP);
        int nFrames = spec.GetLength(0), bins = spec.GetLength(1);
        var mel = new float[1, NMELS, nFrames];
        for (int f = 0; f < nFrames; f++)
            for (int m = 0; m < NMELS; m++)
            {
                float acc = 0;
                for (int k = 0; k < bins; k++)
                {
                    double mag = Math.Sqrt(spec[f, k].Re * spec[f, k].Re + spec[f, k].Im * spec[f, k].Im);
                    acc += (float)(melFb[m, k] * mag);
                }
                mel[0, m, f] = (float)Math.Log(Math.Max(acc, 1e-5));
            }
        progress?.Report((40, "音频超分:AI 推理(backbone)..."));
        ct.ThrowIfCancellationRequested();

        // 3) backbone → hidden → head
        var melTensor = new DenseTensor<float>(Flatten(mel), new[] { 1, NMELS, nFrames });
        using var bOut = backbone.Run(new[] { NamedOnnxValue.CreateFromTensor(backbone.InputMetadata.Keys.First(), melTensor) });
        var hidden = bOut.First().AsTensor<float>();   // [1,T,512]
        var hiddenTensor = new DenseTensor<float>(hidden.ToArray(), hidden.Dimensions.ToArray());
        progress?.Report((60, "音频超分:AI 推理(频谱头)..."));
        using var hOut = head.Run(new[] { NamedOnnxValue.CreateFromTensor(head.InputMetadata.Keys.First(), hiddenTensor) });
        var outArr = hOut.ToArray();
        // head 输出:real/imag 均为 [1, F=1025, T(帧数)](实测形状!)
        var realT = outArr[0].AsTensor<float>();
        var imagT = outArr[1].AsTensor<float>();
        int fDim = realT.Dimensions[^2];    // 频段(1025)
        int outFrames = realT.Dimensions[^1];   // 帧数
        int bins2 = fDim;

        // 4) 重建复杂频谱(real + j*imag) → ISTFT → 增强波形
        var complexSpec = new AudioSrsDsp.Complex[outFrames, bins2];
        for (int f = 0; f < outFrames; f++)
            for (int k = 0; k < bins2; k++)
            {
                float r = realT[0, k, f];
                float im = imagT[0, k, f];
                complexSpec[f, k] = new AudioSrsDsp.Complex(r, im);
            }
        progress?.Report((75, "音频超分:ISTFT 重建..."));
        float[] enhanced = AudioSrsDsp.Istft(complexSpec, NFFT, HOP, w441.Length);

        // 5) Linkwitz-Riley 合并(低频保留原信号)→ 48k
        float[] merged = AudioSrsDsp.SpectralMerge(w441, enhanced, ENH_SR,
            Math.Min(sr, 16000) / 2.0, 1024);
        progress?.Report((90, "音频超分:输出 48kHz..."));
        return AudioSrsDsp.ResamplePoly(merged, 48000, ENH_SR);
    }

    // ---------- WAV 读写(16-bit PCM) ----------
    static (float[], int) ReadWav16(string path)
    {
        using var br = new BinaryReader(File.OpenRead(path));
        br.ReadChars(4); br.ReadInt32(); br.ReadChars(4);
        while (br.BaseStream.Position < br.BaseStream.Length)
        {
            string id = new string(br.ReadChars(4));
            int size = br.ReadInt32();
            if (id == "fmt ")
            {
                br.ReadInt16(); int ch = br.ReadInt16(); int sr = br.ReadInt32();
                br.ReadInt32(); br.ReadInt16(); int bits = br.ReadInt16();
                if (size > 16) br.BaseStream.Seek(size - 16, SeekOrigin.Current);
                if (id == "data") break;
                continue;
            }
            if (id == "data")
            {
                int samples = size / 2 / 2;   // 假设 stereo 16bit
                var mix = new float[samples];
                for (int s = 0; s < samples; s++) { mix[s] = br.ReadInt16() / 32768f; br.ReadInt16(); }
                return (mix, 0);
            }
            br.BaseStream.Seek(size + (size % 2), SeekOrigin.Current);
        }
        throw new InvalidDataException("WAV 读取失败");
    }

    static byte[] WriteWav16(float[] mono, int sr)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        int dataSize = mono.Length * 2;
        bw.Write("RIFF"u8); bw.Write(36 + dataSize); bw.Write("WAVE"u8);
        bw.Write("fmt "u8); bw.Write(16); bw.Write((short)1); bw.Write((short)1);
        bw.Write(sr); bw.Write(sr * 2); bw.Write((short)2); bw.Write((short)16);
        bw.Write("data"u8); bw.Write(dataSize);
        foreach (var v in mono) bw.Write((short)(Math.Clamp(v, -1f, 1f) * 32767f));
        bw.Flush();
        return ms.ToArray();
    }

    static int Gcd(int a, int b) { while (b != 0) { int t = a % b; a = b; b = t; } return a; }

    static float[,] To2D(float[,] src) => src;   // 已是 2D

    static float[] Flatten(float[,,] arr)
    {
        var r = new float[arr.GetLength(0) * arr.GetLength(1) * arr.GetLength(2)];
        int i = 0;
        foreach (var v in arr) r[i++] = v;
        return r;
    }
}
