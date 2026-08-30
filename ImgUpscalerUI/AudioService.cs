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

    /// <summary>音频文件信息:时长、声道、采样率(用于进度估算/展示)。</summary>
    public static (double DurationSec, int Channels, int SampleRate) Probe(string path)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = FfmpegPath,
                Arguments = $"-i \"{path}\" -f null -",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return (0, 0, 0);
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            double dur = 0; int ch = 0, sr = 0;
            // 解析 "Duration: 00:01:23.45" 与 "audio: 44100 Hz, stereo"
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
            }
            return (dur, ch, sr);
        }
        catch { return (0, 0, 0); }
    }

    /// <summary>
    /// 音频增强主流程。denoise:0~2(关/弱/强), loudness:bool, lowcut:bool, eq:bool;
    /// 输出格式:0=WAV,1=FLAC,2=MP3;outDir=输出目录(空=源目录)。
    /// 进度:按 ffmpeg 时间戳百分比(0-100)。
    /// </summary>
    public static async Task EnhanceAsync(string input, string output, int denoise, bool loudness,
        bool lowcut, bool eq, int outFmt, int mp3BitrateKbps, double? outDir,
        IProgress<(int pct, string msg)>? progress, CancellationToken ct)
    {
        var filters = new System.Collections.Generic.List<string>();
        // 1) 降噪(anlmdn 非局部均值,零模型)
        if (denoise == 1) filters.Add("anlmdn=strength=0.01");
        else if (denoise == 2) filters.Add("anlmdn=strength=0.08");
        // 2) 低切(去低频隆隆/直流,人声/音乐更干净)
        if (lowcut) filters.Add("highpass=f=40");
        // 3) 均衡(柔和高频增强,提清晰度)
        if (eq) filters.Add("equalizer=f=3500:t=q:w=1:g=2");
        // 4) 响度归一(EBU R128)
        if (loudness) filters.Add("loudnorm=I=-16:TP=-1.5:LRA=11");

        var af = filters.Count > 0 ? $"-af \"{string.Join(",", filters)}\"" : "";
        var codec = outFmt switch
        {
            0 => "-c:a pcm_s16le",
            1 => "-c:a flac",
            _ => $"-c:a libmp3lame -b:a {mp3BitrateKbps}k",
        };
        var psi = new ProcessStartInfo
        {
            FileName = FfmpegPath,
            Arguments = $"-y -i \"{input}\" {af} {codec} -progress pipe:1 -nostats \"{output}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
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
                    var m = System.Text.RegularExpressions.Regex.Match(line, @"out_time_ms=(\d+)");
                    if (m.Success)
                    {
                        double ms = double.Parse(m.Groups[1].Value);
                        int pct = (int)Math.Min(99, ms / 1000.0);
                        progress?.Report((pct, $"处理中 {ms / 1000.0:0.0}s..."));
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
                throw new InvalidOperationException($"ffmpeg 处理失败(exit {p.ExitCode})");
            progress?.Report((100, "完成"));
        }
        finally
        {
            App.ActiveProcesses.Unregister(p.Id);
        }
    }
}
