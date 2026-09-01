using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ALHPro;

/// <summary>
/// 音频处理服务:基于 ffmpeg 7.1(引擎已有)的滤镜链。
/// 功能(可组合,按顺序):
///   1) 降噪:anlmdn(非局部均值宽带降噪,零模型)——强度档控制窗口/强度
///   2) 响度:loudnorm(EBU R128 响度归一,音量音质统一)
///   3) 低切:highpass(去低频隆隆声/直流) + 均衡(equalizer 微调)
/// 输出:WAV(无损) / FLAC(无损) / MP3(有损,码率可调)。
/// 全部本地 ffmpeg,无联网无模型。
/// </summary>
public static class AudioService
{
    public static string FfmpegPath
    {
        get
        {
            foreach (var d in new[] { "ffmpeg", "ffmpeg.exe" })
            {
                var p = Path.Combine(AppContext.BaseDirectory, "engines", "ffmpeg", d);
                if (File.Exists(p)) return p;
            }
            return "ffmpeg";
        }
    }

    /// <summary>ffmpeg/ffprobe 进程参数预处理:强制 UTF-8 路径解析。
    /// 中文路径在部分旧版 ffmpeg/系统代码页下会 "Illegal byte sequence"(字节序列非法),
    /// 设置 LANG/LC_ALL=UTF-8 可让路径按 UTF-8 解析(对 7.x 及以上无副作用)。</summary>
    public static ProcessStartInfo NewFfmpegPsi(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["LANG"] = "C.UTF-8";
        psi.Environment["LC_ALL"] = "C.UTF-8";
        return psi;
    }

    /// <summary>路径转 8.3 短路径(纯 ASCII),防「中文路径 + GBK 系统代码页」下
    /// ffmpeg 报 Illegal byte sequence。失败(如 8.3 被禁用)时原样返回。
    /// 含中文/非 ASCII 的路径才转,纯 ASCII 路径直接返回(避免多一毫秒)。</summary>
    public static string FfmpegSafePath(string path)
    {
        try
        {
            bool needShort = false;
            foreach (var c in path) if (c > 127) { needShort = true; break; }
            if (!needShort) return path;
            var sb = new System.Text.StringBuilder(512);
            uint r = GetShortPathName(path, sb, 512);
            return r > 0 ? sb.ToString() : path;
        }
        catch { return path; }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern uint GetShortPathName(string lpszLongPath, System.Text.StringBuilder lpszShortPath, uint cchBuffer);

    /// <summary>音频文件信息:时长、声道、采样率(用于进度估算/展示)。</summary>
    public static (double DurationSec, int Channels, int SampleRate, double MaxVolumeDb, double MeanVolumeDb) Probe(string path)
    {
        try
        {
            var psi = NewFfmpegPsi(FfmpegPath, $"-ss 0 -t 30 -i \"{path}\" -af volumedetect -f null -");   // 只解码前 30 秒测峰值(快;通常音量稳定)
            psi.RedirectStandardError = true;
            using var p = Process.Start(psi);
            if (p == null) return (0, 0, 0, 0, 0);
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            double dur = 0; int ch = 0, sr = 0; double maxDb = -99; double meanDb = -99;
            // 解析 "Duration: 00:01:23.45" 与 "audio: 44100 Hz, stereo" 与 "max_volume: -0.0 dB"/"mean_volume"
            foreach (var line in err.Split('\n'))
            {
                var m = System.Text.RegularExpressions.Regex.Match(line,
                    @"Duration:\s*(\d+):(\d+):(\d+\.\d+)");
                if (m.Success && m.Groups.Count > 3)
                    dur = double.Parse(m.Groups[1].Value) * 3600 + double.Parse(m.Groups[2].Value) * 60 + double.Parse(m.Groups[3].Value);
                var m2 = System.Text.RegularExpressions.Regex.Match(line, @"(\d+)\s*Hz");
                if (m2.Success) sr = int.Parse(m2.Groups[1].Value);
                var m3 = System.Text.RegularExpressions.Regex.Match(line, @"(mono|stereo|5\.1|7\.1)");
                if (m3.Success) ch = m3.Groups[1].Value == "mono" ? 1 : m3.Groups[1].Value == "stereo" ? 2 : 6;
                var m4 = System.Text.RegularExpressions.Regex.Match(line, @"max_volume:\s*(-?[\d.]+)\s*dB");
                if (m4.Success && double.TryParse(m4.Groups[1].Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var db))
                    maxDb = db;
                var m5 = System.Text.RegularExpressions.Regex.Match(line, @"mean_volume:\s*(-?[\d.]+)\s*dB");
                if (m5.Success && double.TryParse(m5.Groups[1].Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var db2))
                    meanDb = db2;
            }
            return (dur, ch, sr, maxDb, meanDb);
        }
        catch { return (0, 0, 0, 0, 0); }
    }

    /// <summary>解码音频为单声道 8kHz 采样(用于波形显示,所有格式 MP3/WAV/FLAC 都支持,
    /// 用 ffmpeg 输出原始 PCM,免第三方解码器)。返回归一化 0~1 采样幅度数组。</summary>
    public static async Task<float[]> DecodeWaveformAsync(string path, int maxSamples = 2000, CancellationToken ct = default)
    {
        try
        {
            var psi = NewFfmpegPsi(FfmpegPath, $"-i \"{path}\" -f s16le -ac 1 -ar 8000 -");
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using var p = Process.Start(psi);
            if (p == null) return Array.Empty<float>();
            var ms = new MemoryStream();
            await p.StandardOutput.BaseStream.CopyToAsync(ms, ct);
            await p.WaitForExitAsync(ct);
            var data = ms.ToArray();
            if (data.Length < 2) return Array.Empty<float>();
            // s16le:每 2 字节一个 int16 采样
            int samples = data.Length / 2;
            // 压缩到 maxSamples:每桶取绝对值平均(峰值)
            var result = new float[Math.Min(maxSamples, samples)];
            int perBucket = Math.Max(1, samples / result.Length);
            for (int i = 0; i < result.Length; i++)
            {
                int start = i * perBucket;
                int end = Math.Min(samples, start + perBucket);
                float sum = 0;
                for (int j = start; j < end; j++)
                {
                    short v = (short)(data[j * 2] | (data[j * 2 + 1] << 8));
                    sum += Math.Abs(v) / 32768.0f;
                }
                result[i] = Math.Min(1f, sum / Math.Max(1, end - start));
            }
            return result;
        }
        catch { return Array.Empty<float>(); }
    }

    /// <summary>当前处理音频的时长(秒),供进度百分比换算;0=未知(进度只显示秒数)。</summary>
    private static double DurSec;

    /// <summary>
    /// 音频增强主流程。denoise:0~2(关/弱/强), loudness:bool, lowcut:bool, eq:bool;
    /// 输出格式:0=WAV,1=FLAC,2=MP3;outDir=输出目录(空=源目录)。
    /// trimStart/trimEnd:裁剪(秒;0=不裁)。进度按 ffmpeg 时间戳百分比(0-100)。
    /// </summary>
    public static async Task EnhanceAsync(string input, string output, int denoise, bool loudness,
        bool lowcut, bool eq, int outFmt, int mp3BitrateKbps, double? outDir,
        IProgress<(int pct, string msg)>? progress, CancellationToken ct,
        double trimStart = 0, double trimEnd = 0)
    {
        // 总时长(进度百分比基准;失败给 0=只显示秒数)
        DurSec = 0;
        try { DurSec = Probe(input).DurationSec; } catch { }
        // 保留原采样率:loudnorm 内部按 192k 处理、输出会变 48k(实测 44100→48000=降频),
        // 滤镜链末尾 aresample=原采样率 强制还原;失败则跟随输出默认(不崩)。
        double srcRate = 0;
        try { srcRate = Probe(input).SampleRate; } catch { }
        var keepRate = srcRate > 0 ? $"aresample={srcRate.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}" : "";
        var filters = new System.Collections.Generic.List<string>();
        // 1) 降噪(afftdn FFT 降噪:经实测定于此 ffmpeg 构建有效;原 anlmdn 在此构建无效果——已替换)
        // denoise:0=关 1=弱 2=中 3=强
        if (denoise == 1) filters.Add("afftdn=nf=-25");
        else if (denoise == 2) filters.Add("afftdn=nf=-30");
        else if (denoise == 3) filters.Add("afftdn=nf=-35");
        // 2) 低切(去低频隆隆/直流,人声/音乐更干净)
        if (lowcut) filters.Add("highpass=f=40");
        // 3) 均衡(柔和高频增强,提清晰度)
        if (eq) filters.Add("equalizer=f=3500:t=q:w=1:g=2");
        // 4) 响度归一(EBU R128)
        if (loudness)
        {
            // 实测保护:loudnorm 会按目标 I=-16 压缩,源【已经很响】(mean > -14dB,如 AI 混音/母带,
            // 本机测试源 mean=-11.9)会被压掉 5dB+ → 音量变小、听感变差=破坏!
            // 规则:源 mean_volume > -14dB(已够响)→ 完全跳过 loudnorm(不破坏);
            // 源偏小(mean ≤ -14,如旧录音/音量小)→ 才做响度归一(真正需要)。
            double srcMean = Probe(input).MeanVolumeDb;
            if (srcMean > -14.0)
            {
                // 源已响:跳过(静默,不打补丁) —— 不加入 filters
            }
            else
            {
                filters.Add("loudnorm=I=-16:TP=-1.5:LRA=11");
            }
        }

        var af = filters.Count > 0
            ? $"-af \"{string.Join(",", filters)}{(keepRate.Length > 0 ? "," + keepRate : "")}\""
            : "";
        // 裁剪:起点用 -ss(输入侧,快);终点用 -t(duration)
        var trim = "";
        if (trimStart > 0.01) trim += $"-ss {trimStart.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} ";
        if (trimEnd > 0.01) trim += $"-t {Math.Max(0.1, trimEnd - trimStart).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} ";
        var codec = outFmt switch
        {
            0 => $"-c:a libmp3lame -b:a {mp3BitrateKbps}k",   // MP3(UI:0)
            1 => "-c:a pcm_s16le",                            // WAV(UI:1)
            _ => "-c:a flac",                                 // FLAC(UI:2)
        };
        var psi = NewFfmpegPsi(FfmpegPath, $"-y {trim} -i \"{input}\" {af} {codec} -progress pipe:1 -nostats \"{output}\"");
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        // 进度:ffmpeg -progress 输出 out_time_ms
        using var p = Process.Start(psi);
        if (p == null) throw new InvalidOperationException("无法启动 ffmpeg");
        App.ActiveProcesses.Register(p);
        try
        {
            var lineTask = Task.Run(() =>
            {
                string? line;
                while ((line = p.StandardOutput.ReadLine()) != null)
                {
                    // ffmpeg -progress 的 out_time_ms 实际是【微秒】(历史坑:名字叫 ms 实为 us);
                    // 优先用 out_time_us(=微秒),两者除以 1,000,000 才是秒。
                    var m = System.Text.RegularExpressions.Regex.Match(line, @"out_time_us=(\d+)");
                    if (!m.Success)
                        m = System.Text.RegularExpressions.Regex.Match(line, @"out_time_ms=(\d+)");
                    if (m.Success)
                    {
                        double sec = double.Parse(m.Groups[1].Value) / 1_000_000.0;
                        int pct = (int)Math.Min(99, sec / (DurSec > 0 ? DurSec : 1) * 100);
                        progress?.Report((pct, $"处理中 {sec:0.#}s"));
                    }
                }
            });
            var errTask = p.StandardError.ReadToEndAsync();
            while (!p.HasExited && !ct.IsCancellationRequested) await Task.Delay(100, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                throw new OperationCanceledException();
            }
            await Task.WhenAll(lineTask, errTask).ConfigureAwait(false);
            if (p.ExitCode != 0)
            {
                var errTail = await errTask.ConfigureAwait(false);
                if (errTail.Length > 400) errTail = errTail[^400..];
                throw new InvalidOperationException($"ffmpeg 处理失败(exit {p.ExitCode}):\n{errTail}");
            }
            progress?.Report((100, "完成"));
        }
        finally
        {
            App.ActiveProcesses.Unregister(p.Id);
        }
    }

    /// <summary>AI 超分:人声/伴奏两轨【分别】优化(均衡+压缩)后重新混音+限幅。
    /// 为什么分轨:人声提升不会把乐器底噪一起放大,伴奏补低频也不会压人声——这就是"AI 重制"的意义。
    /// level:1=柔和 2=标准 3=强力。输入:44.1k 立体声 WAV(由 Demucs 分离产出);输出:44.1k 立体声 WAV。</summary>
    public static async Task RemasterAsync(string vocalsWav, string accompWav, string outputWav, int level,
        IProgress<(int pct, string msg)>? progress, CancellationToken ct = default)
    {
        DurSec = 0;
        try { DurSec = Probe(vocalsWav).DurationSec; } catch { }
        // 人声链:亮度/咬字提升(3.5kHz 人声清晰区 + 9kHz 空气感)+ 轻压缩
        // 伴奏链:去低频闷声(highpass 30Hz)+ 暖度(120Hz)+ 高频点缀(8k)+ 轻压缩
        string vChain, aChain;
        if (level == 1)   // 柔和
        {
            vChain = "equalizer=f=3500:t=q:w=1:g=1.5,acompressor=threshold=0.08:ratio=2:attack=20:release=200";
            aChain = "highpass=f=30,equalizer=f=120:t=q:w=1:g=1.5,equalizer=f=8000:t=q:w=1:g=1,acompressor=threshold=0.09:ratio=1.8:attack=25:release=220";
        }
        else if (level == 2)   // 标准
        {
            vChain = "equalizer=f=3500:t=q:w=1:g=2.5,equalizer=f=9000:t=q:w=1:g=1.2,acompressor=threshold=0.08:ratio=2.5:attack=15:release=180";
            aChain = "highpass=f=30,equalizer=f=120:t=q:w=1:g=2,equalizer=f=8000:t=q:w=0.8:g=1.5,acompressor=threshold=0.08:ratio=2:attack=20:release=200";
        }
        else   // 强力
        {
            vChain = "equalizer=f=3500:t=q:w=1:g=4,equalizer=f=9000:t=q:w=1:g=2,acompressor=threshold=0.09:ratio=3:attack=12:release=160";
            aChain = "highpass=f=34,equalizer=f=120:t=q:w=1:g=3,equalizer=f=9000:t=q:w=0.8:g=2,acompressor=threshold=0.08:ratio=2.2:attack=18:release=190";
        }
        // amix normalize=0:直接求和(不自动减半音量);末尾限幅防削波
        var fc = $"[0:a]{vChain}[v];[1:a]{aChain}[a];[v][a]amix=inputs=2:duration=first:normalize=0,alimiter=limit=0.95[out]";
        var psi = NewFfmpegPsi(FfmpegPath, $"-y -i \"{vocalsWav}\" -i \"{accompWav}\" -filter_complex \"{fc}\" -map \"[out]\" -ar 44100 -ac 2 -c:a pcm_s16le -progress pipe:1 -nostats \"{outputWav}\"");
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        using var p = Process.Start(psi);
        if (p == null) throw new InvalidOperationException("无法启动 ffmpeg");
        App.ActiveProcesses.Register(p);
        try
        {
            var lineTask = Task.Run(() =>
            {
                string? line;
                while ((line = p.StandardOutput.ReadLine()) != null)
                {
                    var m = System.Text.RegularExpressions.Regex.Match(line, @"out_time_us=(\d+)");
                    if (!m.Success)
                        m = System.Text.RegularExpressions.Regex.Match(line, @"out_time_ms=(\d+)");
                    if (m.Success)
                    {
                        double sec = double.Parse(m.Groups[1].Value) / 1_000_000.0;
                        int pct = (int)Math.Min(99, sec / (DurSec > 0 ? DurSec : 1) * 100);
                        progress?.Report((pct, $"重新混音中 {sec:0.#}s"));
                    }
                }
            });
            var errTask = p.StandardError.ReadToEndAsync();
            while (!p.HasExited && !ct.IsCancellationRequested) await Task.Delay(100, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                throw new OperationCanceledException();
            }
            await Task.WhenAll(lineTask, errTask).ConfigureAwait(false);
            if (p.ExitCode != 0)
            {
                var errTail = await errTask.ConfigureAwait(false);
                if (errTail.Length > 400) errTail = errTail[^400..];
                throw new InvalidOperationException($"ffmpeg 混音失败(exit {p.ExitCode}):\n{errTail}");
            }
            progress?.Report((100, "混音完成"));
        }
        finally
        {
            App.ActiveProcesses.Unregister(p.Id);
        }
    }

    /// <summary>若干 44.1k 立体声 WAV 混合成一个(等量求和,限幅防削波)。1 个时直接复制;0 个抛错。
    /// 用于"自定义组合"多轨合成——各轨来自同一次 AI 分轨,无相位问题。</summary>
    public static async Task MixWavsAsync(System.Collections.Generic.List<string> inputs, string outputWav,
        IProgress<(int pct, string msg)>? progress = null, CancellationToken ct = default)
    {
        if (inputs.Count == 0) throw new ArgumentException("没有可混合的轨道");
        if (inputs.Count == 1)
        {
            System.IO.File.Copy(inputs[0], outputWav, true);
            progress?.Report((100, "完成"));
            return;
        }
        DurSec = 0;
        try { DurSec = Probe(inputs[0]).DurationSec; } catch { }
        var ins = string.Concat(inputs.Select(f => $"-i \"{f}\" "));
        var labels = string.Concat(Enumerable.Range(0, inputs.Count).Select(i => $"[{i}:a]"));
        var fc = $"{labels}amix=inputs={inputs.Count}:duration=first:normalize=0,alimiter=limit=0.98[out]";
        var psi = NewFfmpegPsi(FfmpegPath, $"-y {ins}-filter_complex \"{fc}\" -map \"[out]\" -ar 44100 -ac 2 -c:a pcm_s16le -progress pipe:1 -nostats \"{outputWav}\"");
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        using var p = Process.Start(psi);
        if (p == null) throw new InvalidOperationException("无法启动 ffmpeg");
        App.ActiveProcesses.Register(p);
        try
        {
            var lineTask = Task.Run(() =>
            {
                string? line;
                while ((line = p.StandardOutput.ReadLine()) != null)
                {
                    var m = System.Text.RegularExpressions.Regex.Match(line, @"out_time_us=(\d+)");
                    if (!m.Success)
                        m = System.Text.RegularExpressions.Regex.Match(line, @"out_time_ms=(\d+)");
                    if (m.Success)
                    {
                        double sec = double.Parse(m.Groups[1].Value) / 1_000_000.0;
                        int pct = (int)Math.Min(99, sec / (DurSec > 0 ? DurSec : 1) * 100);
                        progress?.Report((pct, $"混合轨道中 {sec:0.#}s"));
                    }
                }
            });
            var errTask = p.StandardError.ReadToEndAsync();
            while (!p.HasExited && !ct.IsCancellationRequested) await Task.Delay(100, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                throw new OperationCanceledException();
            }
            await Task.WhenAll(lineTask, errTask).ConfigureAwait(false);
            if (p.ExitCode != 0)
            {
                var errTail = await errTask.ConfigureAwait(false);
                if (errTail.Length > 400) errTail = errTail[^400..];
                throw new InvalidOperationException($"ffmpeg 混轨失败(exit {p.ExitCode}):\n{errTail}");
            }
            progress?.Report((100, "完成"));
        }
        finally
        {
            App.ActiveProcesses.Unregister(p.Id);
        }
    }

    /// <summary>任意音频 → 44.1kHz 立体声 16-bit WAV(Demucs 模型输入要求)。</summary>
    public static async Task ConvertToWav44kAsync(string input, string outputWav)
    {
        var psi = NewFfmpegPsi(FfmpegPath, $"-y -i \"{input}\" -ar 44100 -ac 2 -c:a pcm_s16le \"{outputWav}\"");
        psi.RedirectStandardError = true;
        using var p = Process.Start(psi);
        if (p == null) throw new InvalidOperationException("无法启动 ffmpeg");
        var err = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0 || !File.Exists(outputWav) || new FileInfo(outputWav).Length < 100)
            throw new InvalidOperationException("转换为 44.1kHz WAV 失败");
    }

    /// <summary>44.1kHz WAV → 目标格式(0=MP3 320k 1=WAV 2=FLAC)。</summary>
    public static async Task ConvertWavToAsync(string inputWav, string output, int outFmt)
    {
        var codec = outFmt switch
        {
            0 => "-c:a libmp3lame -b:a 320k",
            1 => "-c:a pcm_s16le",
            _ => "-c:a flac",
        };
        var psi = NewFfmpegPsi(FfmpegPath, $"-y -i \"{inputWav}\" {codec} \"{output}\"");
        psi.RedirectStandardError = true;
        using var p = Process.Start(psi);
        if (p == null) throw new InvalidOperationException("无法启动 ffmpeg");
        var err = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0 || !File.Exists(output) || new FileInfo(output).Length < 100)
            throw new InvalidOperationException("转换输出格式失败");
    }
}
