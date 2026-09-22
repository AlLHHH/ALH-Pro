using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AlhPro.Core;

namespace ALHPro;

/// <summary>视频抠图请求(界面 → 服务层)。所有参数都能从界面控件一一对上,服务层不认识控件。</summary>
public sealed record VideoMattingRequest(
    string Input,
    string OutDir = "",
    string ModelKey = "",
    int GpuId = -1,
    bool Transparent = false,
    string Container = "mov",
    string BackgroundPath = "",
    string BackgroundColor = "#000000",
    int Fg = 152, int Bg = 84, int Feather = 1, int Edge = 0, int Morph = 28,
    int Stability = 50,
    double StartSec = 0, double DurationSec = 0);

/// <summary>视频抠图结果。<paramref name="Notes"/> 里写"实际用了什么设备/为什么" —— 用户与排查都要看真话。</summary>
public sealed record VideoMattingResult(string OutputPath, int Frames, double ElapsedSec, string Device, string Notes);

/// <summary>视频抠图服务层(纯后台,不碰 UI ⇒ 可被 `_qa/mattingbench` 无头驱动做真机验收)。
///
/// 【流水线】拆帧(复用 VideoService) → 逐帧:只出蒙版(CutoutService.CutoutMaskAsync)
///   → 阈值/羽化/形态学(VideoMatting.PostProcessAlpha) → 时序稳定(AlphaTemporalFilter,切点先 Reset)
///   → ① 换背景:与背景合成;② 透明通道:alpha 单独成灰度 PNG 序列 → ffmpeg 合帧编码。
///
/// 【三条硬约束(来自第一阶段实测,见 docs/2026-09-22-video-matting-plan-2.md)】
/// ① 绝不用 CutoutAsync/PreviewMaskAsync 取蒙版:它们会重复后处理并每帧写 PNG(实测每帧白付 0.12 秒);
/// ② 帧循环必须串行:同一 ONNX 会话不能并发 Run,时序滤波本身也有状态;
/// ③ 开工前用探针帧实测 GPU 是否划算:birefnet-lite 的 GPU 实测比 CPU 还慢 1.5~2 倍**且不报错**。
/// </summary>
public static class VideoMattingService
{
    /// <summary>帧序列命名:自己编号(000001 起),不复用拆帧的命名 —— 否则编码命令里的 pattern 一旦对不上就是"0 帧成片"。</summary>
    private const string SeqPattern = "{0:D6}.jpg";
    private const string SeqPatternPng = "{0:D6}.png";

    public static async Task<VideoMattingResult> RunAsync(VideoMattingRequest req,
        IProgress<(int pct, string msg)>? progress = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var notes = new List<string>();

        string input = req.Input;
        if (!File.Exists(input)) throw new FileNotFoundException($"找不到视频:{input}");

        string ffmpeg = VideoService.FfmpegPath ?? throw new InvalidOperationException("未找到 ffmpeg(engines/ffmpeg)。");
        string modelKey = string.IsNullOrWhiteSpace(req.ModelKey) ? CutoutService.DefaultModelKey : req.ModelKey;
        string outDir = string.IsNullOrWhiteSpace(req.OutDir) ? (Path.GetDirectoryName(input) ?? ".") : req.OutDir;
        Directory.CreateDirectory(outDir);

        var spec = req.Transparent ? MattingOutputSpecs.ForTransparent(req.Container) : MattingOutputSpecs.ForBackground();
        string ext = spec.Container;                       // mov / webm / mp4
        string outName = Path.GetFileNameWithoutExtension(input) + "_抠图." + ext;
        string outPath = UniquePath(Path.Combine(outDir, outName));

        double fps = ParseFps(VideoService.ProbeFps(input)) ?? 30.0;
        string trim = BuildTrim(req.StartSec, req.DurationSec);

        string work = Path.Combine(Path.GetTempPath(), "alhpro_matting_" + Guid.NewGuid().ToString("N"));
        string srcDir = Path.Combine(work, "src");
        string procDir = Path.Combine(work, "proc");       // 换背景:处理后的帧
        string alphaDir = Path.Combine(work, "alpha");     // 透明通道:alpha 灰度序列
        string videoDir = Path.Combine(work, "video");     // 透明通道:原始帧(按自己的编号复制)
        foreach (var d in new[] { srcDir, procDir, alphaDir, videoDir }) Directory.CreateDirectory(d);

        try
        {
            // ---- 1) 拆帧(复用 VideoService:它已处理 HDR→SDR、非法色彩标记兜底、硬解回退) ----
            ct.ThrowIfCancellationRequested();
            var (hdrDesc, hdrVf) = await VideoService.ProbeHdrToSdrAsync(input, ct);
            if (!string.IsNullOrEmpty(hdrDesc)) notes.Add($"色彩:{hdrDesc}");
            progress?.Report((3, "拆帧中..."));
            int frameCount = await VideoService.ExtractFramesCoreAsync(ffmpeg, input, trim,
                string.IsNullOrEmpty(hdrVf) ? "null" : hdrVf, srcDir, progress, ct, 0);
            if (frameCount <= 0) throw new InvalidOperationException("拆帧得到 0 帧:源文件或色彩标记有问题(详见诊断日志)。");

            var frames = Directory.EnumerateFiles(srcDir, "*.jpg").OrderBy(f => f, StringComparer.Ordinal).ToList();
            if (frames.Count == 0) throw new InvalidOperationException("拆帧目录里没有帧。");

            // ---- 2) 探针帧实测:GPU 到底划不划算(硬约束③) ----
            int device = req.GpuId;
            if (device >= 0)
            {
                progress?.Report((8, "实测 GPU 是否划得来..."));
                double gpuMs = await TimeMaskAsync(frames[0], modelKey, device, ct);
                double cpuMs = await TimeMaskAsync(frames[0], modelKey, -1, ct);
                if (gpuMs > 0 && cpuMs > 0 && gpuMs >= cpuMs * 0.9)
                {
                    notes.Add($"GPU 实测 {gpuMs:F0} ms/帧 不优于 CPU {cpuMs:F0} ms/帧 ⇒ 本次改用 CPU");
                    device = -1;
                }
                else
                {
                    notes.Add($"GPU 实测 {gpuMs:F0} ms/帧 vs CPU {cpuMs:F0} ms/帧 ⇒ 用 GPU");
                }
            }
            else notes.Add("按设置使用 CPU");

            // ---- 3) 逐帧:只出蒙版 → 后处理 → 时序稳定 → 合成/alpha ----
            var filter = new AlphaTemporalFilter(req.Stability, 1080, 1920);   // 尺寸在首帧后按真实帧尺寸重置
            bool filterSized = false;
            byte[]? prevGray = null;
            int bw = 0, bh = 0;
            byte[]? bgPixels = req.Transparent ? null : LoadBackground(req, frames[0], out bw, out bh);
            int done = 0;
            var loopSw = Stopwatch.StartNew();

            foreach (string frame in frames)
            {
                ct.ThrowIfCancellationRequested();
                var (alpha, w, h) = await CutoutService.CutoutMaskAsync(frame, modelKey, device, null, ct);
                if (w <= 0 || h <= 0 || alpha.Length < w * h) { done++; continue; }

                VideoMatting.PostProcessAlpha(alpha, w, h, req.Fg, req.Bg, req.Feather, req.Morph);

                // 切点检测:用降采样灰度帧差(与 SceneCutOptions 的默认帧差阈值同口径)
                byte[] gray = GrayThumb(frame, 64, 36);
                if (prevGray != null && VideoMatting.LooksLikeSceneCut(prevGray, gray)) filter.Reset();
                prevGray = gray;

                if (!filterSized) { filter = new AlphaTemporalFilter(req.Stability, w, h); filterSized = true; }
                filter.Push(alpha);

                done++;
                string name = string.Format(SeqPattern, done);
                if (req.Transparent)
                {
                    SaveAlphaPng(alpha, w, h, Path.Combine(alphaDir, string.Format(SeqPatternPng, done)));
                    File.Copy(frame, Path.Combine(videoDir, name), true);
                }
                else
                {
                    var (fg, fw, fh) = LoadRgb(frame);
                    var dst = new byte[fw * fh * 3];
                    // 背景与前景同尺寸时 ResizeCover 直接返回原数组(纯色背景常见),不白拷一份
                    var bg = (bw == fw && bh == fh) ? bgPixels! : ResizeCover(bgPixels!, bw, bh, fw, fh);
                    VideoMatting.Composite(fg, alpha, bg, dst, fw * fh);
                    SaveJpg(dst, fw, fh, Path.Combine(procDir, name));
                }

                double el = loopSw.Elapsed.TotalSeconds;
                int pct = 10 + (int)(done * 80.0 / frames.Count);
                string eta = EtaText.ForRemaining(done, frames.Count, el) ?? "";
                progress?.Report((pct, $"逐帧抠图 {done}/{frames.Count} 帧 {eta}".Trim()));
            }

            // ---- 4) 合帧编码 ----
            progress?.Report((92, "编码合成..."));
            string enc = spec.UseGlobalEncoder ? VideoService.PickVideoEncoder(req.GpuId) : spec.VideoCodec;
            string encArgs = spec.UseGlobalEncoder ? VideoService.EncoderArgs(enc) : "";
            notes.Add($"编码器:{enc}");

            // 【踩过的坑】alphamerge 的输出必须【打标签】再 -map:写成 "[0:v][1:v]alphamerge" + -map "[v]"
            // 会得到 "Output with label 'v' does not exist in any defined filter graph" 并最终报
            // "Error opening output file ... Invalid argument"(误导成编码器问题,实测排查花了不少时间)。
            // 第二路 alpha 还要显式 format=gray:我们落的 alpha 是 24bpp 灰度值 RGB,不是真灰度/带 alpha 格式。
            string vfilter = req.Transparent
                ? $"-framerate {fps:0.####} -i \"{Path.Combine(videoDir, "%06d.jpg")}\" -framerate {fps:0.####} -i \"{Path.Combine(alphaDir, "%06d.png")}\" -filter_complex \"[1:v]format=gray[a];[0:v][a]alphamerge[v]\" -map \"[v]\""
                : $"-framerate {fps:0.####} -i \"{Path.Combine(procDir, "%06d.jpg")}\" -i \"{input}\" -map 0:v -map 1:a?";
            string audio = req.Transparent ? "" : $"{spec.AudioArgs} -shortest";
            string cmd = $"-y -v error {vfilter} -c:v {enc} {encArgs} -pix_fmt {spec.PixelFormat} {audio} {spec.ExtraVideoArgs} \"{outPath}\"";
            AppLogger.Info($"视频抠图编码:{cmd}");
            await VideoService.RunAsync(ffmpeg, cmd, progress, ct, "编码合成", done, procDir);

            if (!File.Exists(outPath)) throw new InvalidOperationException("编码结束但没有产出文件(详见诊断日志)。");
            progress?.Report((100, $"完成:{Path.GetFileName(outPath)}"));
            return new VideoMattingResult(outPath, done, sw.Elapsed.TotalSeconds,
                device >= 0 ? $"GPU({device})" : "CPU", string.Join(" · ", notes));
        }
        finally
        {
            // 临时盘必须清干净(取消路径也一样):几千帧 JPG + alpha 序列是 GB 级
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { /* 清不掉不掩盖主流程结果 */ }
        }
    }

    /// <summary>探针帧计时:取两帧里较快的一次(第一次含会话建立,不算稳态)。</summary>
    private static async Task<double> TimeMaskAsync(string frame, string modelKey, int gpuId, CancellationToken ct)
    {
        double best = double.MaxValue;
        for (int i = 0; i < 2; i++)
        {
            var sw = Stopwatch.StartNew();
            var (a, _, _) = await CutoutService.CutoutMaskAsync(frame, modelKey, gpuId, null, ct);
            sw.Stop();
            if (a.Length == 0) return -1;
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }
        return best;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path) ?? ".";
        string baseName = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 2; i < 1000; i++)
        {
            string cand = Path.Combine(dir, $"{baseName} ({i}){ext}");
            if (!File.Exists(cand)) return cand;
        }
        return Path.Combine(dir, $"{baseName}_{Guid.NewGuid():N}{ext}");
    }

    private static string BuildTrim(double startSec, double durationSec)
    {
        var sb = new System.Text.StringBuilder();
        if (startSec > 0) sb.Append($" -ss {startSec:0.###}");
        if (durationSec > 0) sb.Append($" -t {durationSec:0.###}");
        return sb.ToString();
    }

    /// <summary>帧率字符串("30000/1001" / "30")→ 数值;解析不出返回 null(调用方用 30 兜底)。</summary>
    private static double? ParseFps(string? fps)
    {
        if (string.IsNullOrWhiteSpace(fps)) return null;
        var parts = fps.Trim().Split('/');
        try
        {
            if (parts.Length == 2 && double.TryParse(parts[1], out double den) && den > 0
                && double.TryParse(parts[0], out double num)) return num / den;
            if (double.TryParse(fps, out double v) && v > 0) return v;
        }
        catch { }
        return null;
    }

    /// <summary>降采样灰度缩略图(切点检测用)。</summary>
    private static byte[] GrayThumb(string jpg, int tw, int th)
    {
        using var bmp = new Bitmap(jpg);
        using var small = new Bitmap(tw, th, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.DrawImage(bmp, 0, 0, tw, th);
        }
        var gray = new byte[tw * th];
        var rect = new Rectangle(0, 0, tw, th);
        var data = small.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            unsafe
            {
                var p = (byte*)data.Scan0.ToPointer();
                for (int y = 0; y < th; y++)
                {
                    byte* row = p + y * data.Stride;
                    for (int x = 0; x < tw; x++)
                    {
                        byte* px = row + x * 3;
                        gray[y * tw + x] = (byte)((px[2] * 299 + px[1] * 587 + px[0] * 114) / 1000);
                    }
                }
            }
        }
        finally { small.UnlockBits(data); }
        return gray;
    }

    private static (byte[] rgb, int w, int h) LoadRgb(string jpg)
    {
        using var bmp = new Bitmap(jpg);
        int w = bmp.Width, h = bmp.Height;
        using var conv = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(conv)) g.DrawImage(bmp, 0, 0, w, h);
        var rgb = new byte[w * h * 3];
        var rect = new Rectangle(0, 0, w, h);
        var data = conv.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            unsafe
            {
                var p = (byte*)data.Scan0.ToPointer();
                for (int y = 0; y < h; y++)
                    System.Runtime.InteropServices.Marshal.Copy(
                        data.Scan0 + y * data.Stride, rgb, y * w * 3, w * 3);
            }
        }
        finally { conv.UnlockBits(data); }
        return (rgb, w, h);
    }

    /// <summary>背景:纯色铺满,或图片按"覆盖并居中裁剪"缩放到目标尺寸(不留黑边)。</summary>
    private static byte[] LoadBackground(VideoMattingRequest req, string firstFrame, out int bw, out int bh)
    {
        using var probe = new Bitmap(firstFrame);
        bw = probe.Width; bh = probe.Height;
        if (!string.IsNullOrWhiteSpace(req.BackgroundPath) && File.Exists(req.BackgroundPath))
        {
            var (rgb, _, _) = LoadRgb(req.BackgroundPath);
            return rgb;
        }
        var color = ParseColor(req.BackgroundColor);
        var flat = new byte[bw * bh * 3];
        for (int i = 0; i < bw * bh; i++)
        {
            flat[i * 3] = color[0]; flat[i * 3 + 1] = color[1]; flat[i * 3 + 2] = color[2];
        }
        return flat;
    }

    private static byte[] ParseColor(string hex)
    {
        var s = (hex ?? "").Trim().TrimStart('#');
        if (s.Length == 6
            && byte.TryParse(s.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out byte r)
            && byte.TryParse(s.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out byte g)
            && byte.TryParse(s.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out byte b))
            return new[] { r, g, b };
        return new byte[] { 0, 0, 0 };
    }

    /// <summary>把背景覆盖式缩放到目标尺寸(短边填满、居中裁剪),避免拉伸变形或留边。</summary>
    private static byte[] ResizeCover(byte[] src, int sw, int sh, int dw, int dh)
    {
        if (sw == dw && sh == dh) return src;
        using var sb = new Bitmap(sw, sh, PixelFormat.Format24bppRgb);
        var srect = new Rectangle(0, 0, sw, sh);
        var sdata = sb.LockBits(srect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            for (int y = 0; y < sh; y++)
                System.Runtime.InteropServices.Marshal.Copy(src, y * sw * 3, sdata.Scan0 + y * sdata.Stride, sw * 3);
        }
        finally { sb.UnlockBits(sdata); }

        double scale = Math.Max((double)dw / sw, (double)dh / sh);
        int nw = Math.Max(dw, (int)Math.Ceiling(sw * scale)), nh = Math.Max(dh, (int)Math.Ceiling(sh * scale));
        using var big = new Bitmap(nw, nh, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(big))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(sb, 0, 0, nw, nh);
        }
        using var cropped = new Bitmap(dw, dh, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(cropped))
            g.DrawImage(big, new Rectangle(0, 0, dw, dh), new Rectangle((nw - dw) / 2, (nh - dh) / 2, dw, dh), GraphicsUnit.Pixel);

        var dst = new byte[dw * dh * 3];
        var drect = new Rectangle(0, 0, dw, dh);
        var ddata = cropped.LockBits(drect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            for (int y = 0; y < dh; y++)
                System.Runtime.InteropServices.Marshal.Copy(ddata.Scan0 + y * ddata.Stride, dst, y * dw * 3, dw * 3);
        }
        finally { cropped.UnlockBits(ddata); }
        return dst;
    }

    private static void SaveAlphaPng(float[] alpha, int w, int h, string path)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        var rect = new Rectangle(0, 0, w, h);
        var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[w * 3];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    byte v = (byte)Math.Clamp(alpha[y * w + x] * 255f, 0f, 255f);
                    row[x * 3] = v; row[x * 3 + 1] = v; row[x * 3 + 2] = v;
                }
                System.Runtime.InteropServices.Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, w * 3);
            }
        }
        finally { bmp.UnlockBits(data); }
        bmp.Save(path, ImageFormat.Png);
    }

    private static void SaveJpg(byte[] rgb, int w, int h, string path)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        var rect = new Rectangle(0, 0, w, h);
        var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            for (int y = 0; y < h; y++)
                System.Runtime.InteropServices.Marshal.Copy(rgb, y * w * 3, data.Scan0 + y * data.Stride, w * 3);
        }
        finally { bmp.UnlockBits(data); }
        bmp.Save(path, ImageFormat.Jpeg);
    }
}
