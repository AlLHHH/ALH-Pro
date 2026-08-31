// VideoService.cs — 视频超分 + 补帧
// 原理(video2X 同款):ffmpeg 拆帧 → 逐帧图片超分 → (可选) RIFE 补帧 → ffmpeg 合帧+音频
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ALHPro;

/// <summary>去重后帧数过少(防删光保护触发)。用户确认"仍要进行"后,可通过 allowFewFrames 跳过保护继续。</summary>
public sealed class DedupTooStrongException : InvalidOperationException
{
    public DedupTooStrongException(string message) : base(message) { }
}

public static class VideoService
{
    /// <summary>引擎目录下定位可执行文件(向上搜索 engines 根)。</summary>
    private static string? FindInEngines(string subDir, string exeName)
    {
        var root = Path.Combine(EngineService.EnginesDir, subDir);
        if (Directory.Exists(root))
        {
            foreach (var f in Directory.EnumerateFiles(root, exeName, SearchOption.AllDirectories))
                return f;
        }
        return null;
    }

    public static string? FfmpegPath => FindInEngines("ffmpeg", "ffmpeg.exe");
    public static string? RifePath => FindInEngines("rife", "rife-ncnn-vulkan.exe");

    /// <summary>组件状态(界面显示用)。</summary>
    public static (bool ffmpeg, bool rife) CheckComponents()
        => (FfmpegPath != null, RifePath != null);

    /// <summary>探测视频分辨率(宽,高);失败给 1920×1080 兜底。</summary>
    public static async Task<(int w, int h)> ProbeSizeAsync(string videoPath)
    {
        try
        {
            var ffmpegDir = FfmpegPath != null ? Path.GetDirectoryName(FfmpegPath) : null;
            var ffprobe = ffmpegDir != null ? Path.Combine(ffmpegDir, "ffprobe.exe") : null;
            if (ffprobe == null || !File.Exists(ffprobe)) return (1920, 1080);
            var psi = AudioService.NewFfmpegPsi(ffprobe, $"-v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0 \"{videoPath}\"");
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using var p = Process.Start(psi);
            if (p == null) return (1920, 1080);
            var line = (await p.StandardOutput.ReadToEndAsync()).Trim();
            var parts = line.Split(',');
            if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
                return (w > 0 ? w : 1920, h > 0 ? h : 1080);
        }
        catch { }
        return (1920, 1080);
    }

    /// <summary>任务开始前的总时长估算(秒):按启用的处理项 + 视频时长/帧率/分辨率粗算。
    /// 用于一开始就显示合理的预计剩余(偏保守,随时间慢慢对齐),而不是从小变大校准。</summary>
    public static double EstimateProcessSeconds(double duration, double fps, int w, int h,
        bool up, double scale, string engine, bool interp, int interpScale, bool dedup, int videoDenoise)
    {
        int src = (int)Math.Max(1, duration * fps);
        double s = src * 0.02 + 1.5;                 // 拆帧(含引擎启动)
        if (dedup) s += Math.Max(1.5, src * 0.010);   // 去重检测(随帧数)
        int frames = src;
        // 补帧/超分的每帧成本按面积缩放(基准 1080p=2073600):固定常数会让 4K/大图严重低估
        double areaN = Math.Max(0.25, (double)w * h / 2073600.0);
        if (interp && interpScale > 1)
        {
            // 整段一次 RIFE 成本 ≈ 输出帧数 × 每帧(按面积)
            s += frames * Math.Max(2, interpScale) * 0.09 * areaN;
            frames *= interpScale;
        }
        if (up && scale > 1.001)
        {
            // 超分逐帧成本:1080p 单帧 waifu2x≈0.18s / realcugan≈0.3s / realesrgan≈0.45s,按面积缩放
            double per = engine switch { "waifu2x" => 0.18, "realesrgan" => 0.45, _ => 0.3 };
            per *= areaN * Math.Max(0.5, scale / 1.0);
            s += frames * per;
        }
        if (videoDenoise > 0) s *= 1.05;              // 降噪滤镜
        s += frames * 0.12;                           // 合成编码(平均)
        // 弱机(CPU 兜底)明显更慢,放大概率系数
        if (SafeRender.Profile == SafeRender.DeviceProfile.UltraLow) s *= 6.0;
        return s * 1.15;                              // 略保守:从大往小对齐,不从小变大
    }

    public static string? ProbeFps(string videoPath)
    {
        var ffmpeg = FfmpegPath;
        if (ffmpeg == null) return null;
        // 用 ffprobe avg_frame_rate(精确);绝不用 `ffmpeg -i` 的 stderr 正则——
        // 文件名里含 "7fps" 之类时会被误匹配(用户实测:内容帧率7fps.mp4 → 探出 7)
        var dir = Path.GetDirectoryName(ffmpeg);
        var ffprobe = dir != null ? Path.Combine(dir, "ffprobe.exe") : null;
        if (ffprobe != null && File.Exists(ffprobe))
        {
            try
            {
                var psi = AudioService.NewFfmpegPsi(ffprobe, $"-v error -select_streams v:0 " +
                                $"-show_entries stream=avg_frame_rate -of csv=p=0 \"{videoPath}\"");
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using var p = Process.Start(psi);
                if (p == null) return null;
                var o = p.StandardOutput.ReadToEnd().Trim();
                if (o.Length > 0)
                {
                    var m = System.Text.RegularExpressions.Regex.Match(o, @"(\d+)/(\d+)");
                    if (m.Success)
                    {
                        long num = long.Parse(m.Groups[1].Value), den = long.Parse(m.Groups[2].Value);
                        if (den > 0) return (num / (double)den).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                    }
                    return o;
                }
            }
            catch { }
        }
        // 回退:旧 stderr 正则但改用【最后一个】匹配(流信息行在末,文件名在最前)
        try
        {
            var psi = AudioService.NewFfmpegPsi(ffmpeg, $"-i \"{videoPath}\"");
            psi.RedirectStandardError = true;
            using var p = Process.Start(psi);
            if (p == null) return null;
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            var ms = System.Text.RegularExpressions.Regex.Matches(err, @"(\d+(?:\.\d+)?)\s*fps");
            return ms.Count > 0 ? ms[ms.Count - 1].Groups[1].Value : null;
        }
        catch { return null; }
    }

    /// <summary>探测视频是否为可变帧率(VFR):抽查前 60 帧的 PTS 间隔,明显不均匀 → true。
    /// VFR 素材(录屏/手机/监控)帧间隔忽大忽小,默认均匀拆帧会按平均帧率丢弃/复制帧
    /// → 时间轴失真(变快/变慢)。检测到后 UI 标注「可变帧率」并自动启用 VFR 拆帧。
    /// 数据源用 ffmpeg showinfo(滤镜层 = 真实播放时间轴),不用 ffprobe frame=pts_time:
    /// 后者含解码层时间戳,受 B 帧/时间基舍入影响会出现 2 倍间隔假象 → CFR 素材被误报为 VFR。</summary>
    public static async Task<bool> ProbeVfrAsync(string videoPath)
    {
        var ffmpeg = FfmpegPath;
        if (ffmpeg == null) return false;
        try
        {
            var lines = await RunCaptureAsync(ffmpeg,
                $"-y -i \"{videoPath}\" -fps_mode passthrough -vf showinfo -frames:v 60 -f null NUL",
                CancellationToken.None);
            var ptsList = new System.Collections.Generic.List<double>();
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var l in lines)
            {
                var m = System.Text.RegularExpressions.Regex.Match(l, @"pts_time:([0-9.]+)");
                if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, inv, out var t))
                    ptsList.Add(t);
            }
            if (ptsList.Count < 8) return false;   // 帧太少无法判断(视作非 VFR)
            // 间隔统计分析:若最大间隔 ≈ 最小间隔的 1.5 倍以上 → 帧率不均匀(VFR)
            var gaps = new System.Collections.Generic.List<double>();
            for (int i = 1; i < ptsList.Count; i++)
            {
                double g = ptsList[i] - ptsList[i - 1];
                if (g > 0) gaps.Add(g);
            }
            if (gaps.Count < 7) return false;
            double minG = gaps.Min(), maxG = gaps.Max();
            double avgG = gaps.Average();
            if (avgG <= 0) return false;
            // 判定:(1) 最大/最小间隔比 > 1.5 → 明显不均匀;(2) 或间隔相对标准差大
            if (maxG > minG * 1.5 && maxG > 0.0005) return true;   // 0.5ms 以下的抖动忽略(噪声)
            double varSum = 0;
            foreach (var g in gaps) { double d = g - avgG; varSum += d * d; }
            double cv = Math.Sqrt(varSum / gaps.Count) / avgG;   // 变异系数
            return cv > 0.25;   // 间隔波动 >25% → 视为可变帧率
        }
        catch { return false; }
    }

    /// <summary>定位打包内的 ffprobe.exe(与 ffmpeg 同目录;缺失则 null,检测自动跳过)。</summary>
    private static string? FindFfprobe()
    {
        var ff = FfmpegPath;
        if (ff == null) return null;
        var dir = Path.GetDirectoryName(ff);
        if (dir == null) return null;
        var p = Path.Combine(dir, "ffprobe.exe");
        return File.Exists(p) ? p : null;
    }

    /// <summary>
    /// 视频处理主流程(超分 / 补帧 独立开关,可任意组合)。
    /// </summary>
    /// <param name="engine">图片超分引擎:waifu2x | realcugan | realesrgan。</param>
    /// <param name="model">引擎模型参数。</param>
    /// <param name="scale">放大倍数(1/1.5/2/3/4;非整数倍用引擎就近倍数+高保真缩放)。</param>
    /// <param name="doUpscale">是否逐帧超分。</param>
    /// <param name="outWidth">自定义输出分辨率宽(null=按倍率)。</param>
    /// <param name="outHeight">自定义输出分辨率高(null=按倍率)。</param>
    /// <param name="frameInterp">是否 RIFE 补帧。</param>
    /// <param name="inFpsOverride">用户指定输入帧率(>0 时优先于自动探测)。</param>
    /// <param name="interpScale">补帧倍率(2/3/4/8;3 需 v4 架构模型)。</param>
    /// <param name="targetFps">指定输出帧率(仅补帧时生效;null=按倍率计算)。</param>
    /// <param name="dedupMode">去重模式:0=关,1=严格(完全相同),2=标准,3=自定义阈值。</param>
    /// <param name="dedupThreshold">去重阈值(自定义模式,scene 分数上限)。</param>
    /// <param name="interpModel">RIFE 模型目录名(相对 engines/rife)。</param>
    /// <param name="sceneThreshold">转场识别阈值 0~1(null=不检测;转场处不插帧)。</param>
    /// <param name="timeStep">光流时间步 0~1(null=默认 0.5;仅 v4 架构模型生效)。</param>
    /// <param name="tta">TTA 高质量(减少光流伪影,更慢)。</param>
    /// <param name="trimStart">裁剪开始时间(秒,null=不裁剪)。</param>
    /// <param name="trimEnd">裁剪结束时间(秒,null=到结尾)。</param>
    /// <param name="gpuId">计算设备:-1=CPU;>=0=GPU 编号(ncnn -g 参数)。</param>
    /// <param name="postSharpen">后处理·锐化 0-100(0=关;unsharp 5x5)。</param>
    /// <param name="postClarity">后处理·清晰 0-100(0=关;unsharp 9x9 局部对比度)。</param>
    /// <param name="postUsm">后处理·钝化蒙版 0-100(0=关;smartblur 负强度+阈值)。</param>
    /// <param name="postDetail">后处理·保留细节 0-100(0=关;cas 自适应锐化)。</param>
    /// <param name="postDeblur">后处理·去模糊 0-100(0=关;smartblur 大半径反锐化)。</param>
    /// <param name="fastMode">快速模式(弱设备):tile 减半降显存、单批处理防爆显存、忽略 TTA。</param>
    /// <param name="upscaleShrink1x">1x超分:内部按 2x 超分后缩回原始尺寸(输出仍是 1x,画质更好)。</param>
    /// <param name="postJello">果冻修复·减少果冻 0=关 1=弱 2=中 3=强(dejudder + 时间平滑)。</param>
    /// <param name="postMotionBlur">果冻修复·运动模糊 0=关 1=弱 2=中 3=强(tmix 混合帧数递增)。</param>
    /// <param name="postDeshake">果冻修复·画面去抖(deshake 轻量稳定)。</param>
    public static async Task<string> ProcessVideoAsync(
        string inputVideo, string outputVideo,
        string engine, string model, double scale, bool doUpscale,
        bool frameInterp, double? inFpsOverride, int interpScale, double? targetFps,
        int dedupMode, double dedupThreshold, string interpModel,
        double? sceneThreshold, double? timeStep, bool tta,
        double? trimStart, double? trimEnd, int gpuId,
        int? outWidth = null, int? outHeight = null,
        IProgress<(int pct, string msg)>? progress = null,
        CancellationToken ct = default,
        int postSharpen = 0, int postClarity = 0, int postUsm = 0,
        int postDetail = 0, int postDeblur = 0,
        int postJello = 0, int postMotionBlur = 0, bool postDeshake = false,
        int videoDenoise = 0, int quality = 0, bool fastMode = false, bool upscaleShrink1x = false,
        int dedupAlgo = 0, int dedupHi = 12, int dedupLo = 5, double dedupFrac = 0.33,
        double dedupSadThr = 3.0, double dedupSsimThr = 0.97,
        double dedupPanThr = 8, bool dedupPanOn = false,
        double dedupAnimeThr = 0.92,
        int postFlicker = 0, int postDenoise = 0, int postAa = 0,
        bool mute = false, bool allowFewFrames = false,
        int codecPref = 0, double customBitrateMbps = 0,
        bool vfrPassthrough = false,
        double dedupProtect = 0.10, int dedupWindow = 6, int dedupScale = 16, double dedupBlockThr = 4,
        bool dedupSegOn = true, double dedupSegSsim = 0.92, double dedupSegSad = 5, double dedupPanMax = 20,
        bool manualProtectSmallMotion = true,
        bool phaseAlign = true,   // 网格模式(动漫拍N/内容帧率)"相位自动对齐":高置信才移相,默认开
        int dedupSmartMode = 0,
        bool motionCompDedup = false,   // 镜头运动补偿:背景 pan 下识别"人物定格"(对齐后残差极小)。默认关(老版行为);需手动开
        bool dedupOnlyTrueHold = false, // 只删"真定格"(SSIM≥0.995):默认关 → 用老版旧阈值(0.85~0.97);开 → 收紧到只删真定格
        int fpsMode = 0,   // 输出帧率基准:0=原帧率×倍率(B,标准);1=内容/处理帧×倍率(A,补帧.mp4 同款节奏)
        double contentFps = 0,   // 内容帧率模式(去重模型 7):按 fc 时间网格均匀采样,不做逐帧判定;≤0=报错
        double animeHoldN = 0,   // 动漫模式(去重模型 2):动画帧率变种"一拍N"(2/3/2.5=混合拍二+三/4/5/6;0/1=不采样=内容帧率=输入帧率)
        bool tempoResample = false,   // 节奏重采样(实验):任意 t 插帧按关键帧真实时长分布(自研任意 t 方案)
        Func<Task>? pauseWait = null)
    {
        // 静态报告字段清零:防止上一个视频的去重摘要/编码器信息残留在下一个视频的显示里
        LastDedupReport = null;
        LastDedupShort = null;
        LastVideoEncoderInfo = "libx264 (CPU 软编)";
        var ffmpeg = FfmpegPath ?? throw new FileNotFoundException("未找到 ffmpeg,请将其放入 engines/ffmpeg/ 目录");
        var rife = frameInterp ? RifePath
            ?? throw new FileNotFoundException("未找到 rife-ncnn-vulkan,请将其放入 engines/rife/ 目录") : null;
        var inv = System.Globalization.CultureInfo.InvariantCulture;

        // 阶段计时:每阶段结束输出"本阶段耗时/累计耗时",定位瓶颈
        var taskWatch = System.Diagnostics.Stopwatch.StartNew();
        var stageWatch = System.Diagnostics.Stopwatch.StartNew();
        string StageElapsed()
        {
            string s = $" · 累计 {taskWatch.Elapsed.TotalSeconds:0.#}s";
            stageWatch.Restart();
            return s;
        }

        // 手动模式新增可调判据(默认保持原行为):局部动作保护/参考帧窗口/采样粒度/变化块判线
        dedupProtect = Math.Clamp(dedupProtect, 0.05, 0.60);
        dedupWindow = Math.Clamp(dedupWindow, 2, 12);
        dedupScale = dedupScale is 8 or 24 or 32 ? dedupScale : 16;
        dedupBlockThr = Math.Clamp(dedupBlockThr, 2, 12);

        // 快速模式(弱设备):忽略 TTA(其速度开销接近翻倍,弱设备不划算)
        if (fastMode) tta = false;

        // 内容帧率管线(智能 1 / 动漫 2 / 手动-内容帧率采样 3):按转场切段、每段自适应压缩复制帧
        // (变化帧压缩),得到"内容关键帧"序列喂给 RIFE。
        // 注意:输出帧率基准 = 【原帧率×补帧倍率】(用户指定帧率则=指定值),
        // 去重只影响 RIFE 的输入序列,不改变输出标签(否则"4x 后只剩 20fps",用户质疑得对)。
        // 由 fpsMode(方案 B/C)与 globalTarget=原帧数×倍率 保证(帧数乘以 frameScale 展开)。

        // 3x 补帧仅 v4 架构模型支持(其他模型只能 2 的幂级联);指定输出帧率时倍率会自动算,不在此校验。
        // 用户选了非 v4 模型却要 3x:自动切换到 v4.13(保持 3x),而不是报错失败
        bool isV4Arch = IsV4Model(interpModel);
        if (frameInterp && targetFps == null && interpScale == 3 && !isV4Arch)
        {
            interpModel = "rife-v4.13";
            progress?.Report((2, "⚠ 3x 补帧需要 v4 架构模型,已自动切换为「通用画质最新 (RIFE v4.13)」"));
            AppLogger.Info("⚠ 3x 补帧需要 v4 架构模型,已自动切换为「通用画质最新 (RIFE v4.13)」");
        }

        // 输出扩展名确保 .mp4 / .mkv
        if (!outputVideo.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) &&
            !outputVideo.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
            outputVideo += ".mp4";

        var workDir = Path.Combine(Path.GetTempPath(), $"imgup_video_{Guid.NewGuid():N}");
        var framesIn = Path.Combine(workDir, "frames_in");
        var framesOut = Path.Combine(workDir, "frames_out");
        var framesFinal = Path.Combine(workDir, "frames_final");
        Directory.CreateDirectory(framesIn);
        Directory.CreateDirectory(framesOut);
        Directory.CreateDirectory(framesFinal);

        try
        {
            // C3:临时磁盘空间检查(不足 2GB 直接提示;8x+TTA 补帧每帧几 MB,512 帧可超 30GB)
            try
            {
                var drive = new System.IO.DriveInfo(Path.GetPathRoot(workDir)!);
                if (drive.AvailableFreeSpace < 2L * 1024 * 1024 * 1024)
                {
                    progress?.Report((0, $"⚠ 临时盘({workDir[..3]})剩余 {drive.AvailableFreeSpace / (1024 << 20):0}GB,可能不够,建议清理磁盘"));
                    AppLogger.Info($"⚠ 临时盘({workDir[..3]})剩余 {drive.AvailableFreeSpace / (1024 << 20):0}GB,可能不够,建议清理磁盘");
                }
                // 38GB 级:补帧(8x+TTA)加超分可能临时占用 30GB+,剩余不足 35GB 提醒"高负荷可能不够"
                else if (drive.AvailableFreeSpace < 35L * 1024 * 1024 * 1024)
                {
                    progress?.Report((0, $"临时盘({workDir[..3]})剩余 {drive.AvailableFreeSpace / (1024 << 20):0}GB — 8x 补帧+超分临时文件较多,若提示空间不足请清理或改输出到其它盘"));
                    AppLogger.Info($"临时盘({workDir[..3]})剩余 {drive.AvailableFreeSpace / (1024 << 20):0}GB — 高负荷(补帧8x+超分)可能占 30GB+,建议清理磁盘");
                }
            }
            catch { }

            // 1) 输入帧率:用户指定优先,否则 ffprobe 探测,再兜底 30
            var probed = ProbeFps(inputVideo);
            double probedFps = 0;
            double.TryParse(probed, System.Globalization.NumberStyles.Float, inv, out probedFps);
            double inFps;
            if (inFpsOverride is > 0) inFps = inFpsOverride.Value;
            else inFps = probedFps > 0 ? probedFps : 30.0;
            progress?.Report((1, $"输入帧率:{inFps.ToString("0.##", inv)} fps"));
            // ===== 参数摘要(完整生效参数,处理开始即打印,对照排查) =====
            string dedupDesc = dedupMode switch
            {
                1 => $"智能(策略{dedupSmartMode})",
                2 => $"动漫(N={animeHoldN:0.#})",
                3 when dedupAlgo == 3 => $"手动-内容帧率({contentFps:0.##}fps)",
                3 when dedupAlgo == 2 => "手动-帧差+SSIM",
                3 when dedupAlgo == 1 => "手动-变化阈值",
                3 => "手动-重复帧",
                _ => "关",
            };
            progress?.Report((2, $"参数:超分{(doUpscale ? $"{engine}/{model} {scale:0.##}x{(upscaleShrink1x ? "(1x缩回)" : "")}" : "关")}" +
                $";补帧{(frameInterp ? $"{interpModel} {interpScale}x{(tta ? " TTA" : "")}" + (targetFps is > 0 ? $"→{targetFps.Value:0.##}fps" : "") : "关")}" +
                $";去重{dedupDesc};转场{(sceneThreshold ?? 0):0.##};时间步{(timeStep ?? 0):0.##};裁剪{(trimStart ?? 0):0.###}~{(trimEnd ?? 0):0.###};设备{(gpuId >= 0 ? "GPU " + gpuId : "CPU")}" + StageElapsed()));
            AppLogger.Info($"参数详情:engine={engine},model={model},scale={scale},up={doUpscale}/{upscaleShrink1x},interp={frameInterp}/{interpModel}/{interpScale}x/{tta}/{targetFps},{timeStep},dedup={dedupMode}/{dedupAlgo}/{animeHoldN}/{contentFps}/{dedupSmartMode},scene={sceneThreshold},trim={trimStart}/{trimEnd},gpu={gpuId},out={outputVideo}");

            // 2) 拆帧(可选去重 + 裁剪)
            // 去重模型:0=关,1=智能检测(freezedetect 自适应),2=动漫模式(freezedetect 高去重),3=标准模式(scene),4=手动模式(scene)
            var dedup = dedupMode > 0;
            progress?.Report((2, (dedup ? "ffmpeg 拆帧(去重)..." : "ffmpeg 拆帧...") +
                (trimStart != null || trimEnd != null ? "(裁剪)..." : "")));
            var trimArgs = "";
            if (trimStart is > 0) trimArgs += $" -ss {trimStart.Value.ToString("0.###", inv)}";
            if (trimEnd is > 0) trimArgs += $" -to {trimEnd.Value.ToString("0.###", inv)}";
            var origCountEst = (int)Math.Round(
                ((trimEnd ?? await ProbeDurationSeconds(inputVideo)) - (trimStart ?? 0)) * inFps);
            double effectiveFps = inFps;
            int frameCount;
            // 每帧原始时长表(去重/VFR 素材时启用):贯穿 拆帧→去重→补帧→合帧,
            // 合帧按该表输出 VFR 时间轴 → 去重删帧/静态段不会压缩时间(不再变速)。
            System.Collections.Generic.List<double>? frameDurs = null;
            var scaleVf = "scale=trunc(iw/2)*2:trunc(ih/2)*2";
            // 用户覆盖输入帧率且与探测值差异>1%:拆帧按用户帧率抽帧/补帧(fps 滤镜会均匀抽/复制)。
            // 关键:若不抽帧,拆出的帧数=源帧率×时长,而合帧帧数目标按用户帧率×时长算 → 两者矛盾
            // → 输出时间轴被压缩/拉伸(实测"输入帧率改小后导出特别快",根因就在这)。
            // 抽帧后帧数=时长×用户帧率,有效帧率=用户帧率,三者一致,时长恒=原长。
            if (inFpsOverride is > 0 && probedFps > 0 && Math.Abs(inFpsOverride.Value - probedFps) / probedFps > 0.01)
            {
                scaleVf = $"fps={inFps.ToString("0.###", inv)},{scaleVf}";
                progress?.Report((2, $"输入帧率覆盖为 {inFps:0.##} fps(探测 {probedFps:0.##}),拆帧按覆盖帧率抽帧/补帧"));
            }
            // 去重统计报告收集:记录各算法判定为重复而被删的帧号(1-based,相对删帧前的序列),
            // 供最终生成"哪个时间段重复最多"的报告;mpdecimate/scene 直接在拆帧滤镜里丢帧,
            // 拿不到逐帧号,只统计数量(origCountEst - frameCount)。
            var dedupDroppedFrames = new System.Collections.Generic.List<int>();
            // 节奏重采样(实验):内容管线保留帧的源帧号集合(时间戳基准);tempoOutFps=实际输出帧率
            System.Collections.Generic.List<int>? tempoSrcIdx = null;
            double tempoOutFps = 0;

            // 智能/动漫去重:帧差法(SAD)快筛 + 分块 SSIM 精确验证——
            // 相邻帧差异极小才初判疑似,再算 SSIM(亮度/对比度/结构三维),SSIM 高才算真重复帧删除。
            // 比 mpdecimate/freezedetect 更符合人眼感知,不会误删"口型/眨眼"等微动帧。
            // 手动模式(重复帧检测)仍用 mpdecimate 自由参数。
            // 手动-语义运动分析(独立叠加开关):与上方算法同时生效——先按算法去重,
            // 再叠加检测镜头平移/背景滚动:整幅画面均匀移动=冗余帧删,局部动作保留。
            // ===== 智能(自动识别拍数)/ 动漫(一拍N)/ 手动-内容帧率采样:分段内容帧率化 =====
            // 不做逐帧判定:先全量拆帧,按转场切段;每段自适应估计内容间隔(一拍N),段内按网格保留内容帧;
            // 之后 RIFE 在内容帧上均匀补帧 = 标准 CFR(段内均匀,节奏按段精确)。
            // 找不准节奏的段(低置信/无保持帧)原样保留,一帧不删——不会"删多/删错/补不回来"。
            // 智能 = 先自动识别拍数(scdet 事件间隔估计),再按拍数网格采样;不再用自适应多判据(用户定案:
            // 那套"自适应 SAD+SSIM+变化块+镜头补偿+保护闸"删不干净/过严,结果虚,拍数识别+网格又快又准)。
            if (dedup && (dedupMode is 1 or 2 || (dedupMode == 3 && dedupAlgo == 3)))
            {
                double userInterval = 0, userTol = 0;
                string modeNote;
                if (dedupMode == 1)
                {
                    // ===== 智能 = 自动识别拍数→网格采样(用户定案:拍数识别比自适应判据快且准) =====
                    // 三档 = 拍数识别的"采用门槛":
                    //   均衡(0)=置信度 ≥0.5 才采用(估不准就回退保留,不硬猜)
                    //   激进(1)=置信度 ≥0.35 就采用(确定有冗余素材,宁可冒点节奏偏差)
                    //   保守(2)=置信度 ≥0.7 采用,且内容帧率是常见拍数(8/10/12/15/20/24/30 附近)才采用
                    double confGate = dedupSmartMode switch { 1 => 0.35, 2 => 0.70, _ => 0.50 };
                    string defaultGateName = dedupSmartMode switch { 1 => "激进", 2 => "保守", _ => "均衡" };
                    progress?.Report((3, "智能检测:识别素材拍数(一拍N)..."));
                    var cfInfo = await EstimateContentFpsWithAsync(ffmpeg, inputVideo, ct);
                    bool commonRatio = IsCommonContentFps(cfInfo.Fps, inFps);
                    if (cfInfo.Fps <= 0.5 || cfInfo.Confidence < confGate || (dedupSmartMode == 2 && !commonRatio))
                    {
                        // 识别不出拍数(连续运动/无保持帧)或置信度不足:原样保留,一帧不删(不硬猜,与"连续运动闸"一致)
                        AppLogger.Info($"智能检测({defaultGateName}):未采用拍数识别({cfInfo.Summary},置信 {cfInfo.Confidence:0%})→ 原样保留(不删帧)");
                        progress?.Report((4, $"智能检测({defaultGateName}):{cfInfo.Summary},原样保留..."));
                        frameCount = await ExtractFramesCoreAsync(ffmpeg, inputVideo, trimArgs, scaleVf,
                            framesIn, progress, ct, origCountEst, vfrPassthrough);
                        frameDurs = null;
                        effectiveFps = inFps;
                        tempoSrcIdx = null;
                        progress?.Report((5, $"已拆出 {frameCount} 帧(智能-未采用拍数,不采样)"));
                    }
                    else
                    {
                        // 识别出拍数:按内容帧率走网格采样(与动漫/手动同一条路)
                        double smartFc = Math.Clamp(cfInfo.Fps, 1.0, Math.Max(2.0, inFps));
                        double smartIv = inFps / smartFc;
                        progress?.Report((3, $"智能检测({defaultGateName}):内容帧率 ≈{smartFc:0.##} fps(拍型每 {smartIv:0.##} 帧,置信 {cfInfo.Confidence:0%})"));
                        var (smartFc2, smartEff, smartSrc) = await RunSegmentContentFpsAsync(ffmpeg, inputVideo, trimArgs, scaleVf,
                            framesIn, origCountEst, vfrPassthrough, inFps, smartIv, 0.8, 0.4, $"智能-{smartFc:0.##}fps",
                            progress, ct, forceGrid: true, phaseAlign: phaseAlign);
                        frameCount = smartFc2;
                        effectiveFps = smartEff;
                        tempoSrcIdx = smartSrc;
                        frameDurs = null;
                    }
                }
                else if (dedupMode == 2)
                {
                    modeNote = animeHoldN switch
                    {
                        1 => "动漫-全动画", 1.6 => "动漫-半拍二", 2 => "动漫-一拍二",
                        2.5 => "动漫-混合拍二+三", 3 => "动漫-一拍三", 4 => "动漫-一拍四", _ => "动漫",
                    };
                    userInterval = animeHoldN >= 1.01 ? animeHoldN : 0;
                    userTol = animeHoldN switch { 2 => 0.6, 3 => 0.6, 2.5 => 0.9, 1.6 => 0.45, _ => 0.6 };
                    if (userInterval <= 1.01)
                    {
                        // 动漫-全动画:不做节奏处理,原样输出(内容帧率=素材帧率)
                        progress?.Report((3, "动漫-全动画:不做节奏处理,原样输出..."));
                        frameCount = await ExtractFramesCoreAsync(ffmpeg, inputVideo, trimArgs,
                            scaleVf, framesIn, progress, ct, origCountEst, vfrPassthrough);
                        frameDurs = null;
                        effectiveFps = inFps;
                        progress?.Report((5, $"已拆出 {frameCount} 帧(全动画,不采样)"));
                    }
                    else
                    {
                        // 动漫-拍N:按档位间隔【网格抽帧】(一拍二=每2帧留1、一拍三=每3帧留1),
                        // 与像素相似度无关 → 重编码噪音保持帧也照样去除("选动漫1拍2不去重"修复)。
                        var (fC, eff, srcA) = await RunSegmentContentFpsAsync(ffmpeg, inputVideo, trimArgs, scaleVf,
                            framesIn, origCountEst, vfrPassthrough, inFps, userInterval, userTol, 0.4, modeNote,
                            progress, ct, forceGrid: true, phaseAlign: phaseAlign);
                        frameCount = fC;
                        effectiveFps = eff;
                        tempoSrcIdx = srcA;
                    }
                }
                else if (dedupMode == 3)
                {
                    // 手动-内容帧率采样:用户填任意值(留空=报错,不做猜测);段级校正在小偏差内微调
                    if (contentFps <= 0.01)
                        throw new InvalidOperationException("内容帧率未填写:请先填写素材真实内容帧率(动漫素材可直接用「动漫模式」选一拍N)");
                    double userFc = Math.Clamp(contentFps, 1.0, Math.Max(2.0, inFps));
                    double uIv = inFps / userFc;
                    var (fC2, eff2, srcB) = await RunSegmentContentFpsAsync(ffmpeg, inputVideo, trimArgs, scaleVf,
                        framesIn, origCountEst, vfrPassthrough, inFps, uIv, 0.8, 0.4, $"手动-内容帧率 {userFc:0.##}fps",
                        progress, ct, forceGrid: true, phaseAlign: phaseAlign);
                    frameCount = fC2;
                    effectiveFps = eff2;
                    tempoSrcIdx = srcB;
                    // 内容帧率模式 = 只决定"采哪些帧";下游与智能/动漫完全一致:
                    // 展开 frameScale=原帧数/内容帧数 → 补帧按原素材帧率补足(输出=原帧率×倍率、时长=原)。
                    // 只清空 frameDurs 防"真实时间轴/VFR"时间戳不均匀;绝不设 fpsMode=1
                    // (它会触发 frameScale=1 → 内容不展开 → 39帧@慢/快 → 用户实测"去重后×3")。
                    frameDurs = null;
                }
                else
                {
                    // ===== 智能(用户定案:回退 8/25 老版方案,砍掉分段/拍数网格)=====
                    // 老版(用户认可的成果) = 全片自适应精确判据删"真重复帧" + 标准补帧,
                    // 不做转场切段、不做每段拍数识别——识别不出来的素材就按差值精确删,不硬猜。
                    progress?.Report((3, "去重:自适应检测(先算差异分布再自动定阈值)..."));
                    frameCount = await ExtractFramesCoreAsync(ffmpeg, inputVideo, trimArgs, scaleVf,
                        framesIn, progress, ct, origCountEst, vfrPassthrough);
                    if (dedup || vfrPassthrough)
                        frameDurs = await BuildFrameDurationsAsync(ffmpeg, inputVideo, trimArgs, scaleVf, ct);
                    // 智能 = 删"肉眼不变"帧(自适应阈值,删后帧帧都有可见变化 → 补帧后全动帧=连续感)
                    var dropA = await Task.Run(() => DetectDupFramesAdaptive(framesIn, progress, 16, dedupSmartMode, motionCompDedup), ct);
                    var allA = Directory.EnumerateFiles(framesIn, "*.png")
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
                    var dropSetA = new System.Collections.Generic.HashSet<int>(dropA);
                    if (dropSetA.Remove(allA.Length))   // 尾帧恒保留:结尾画面组绝不因去重而丢(88889999 的 9)
                        AppLogger.Info("尾帧保护:智能去重判定含末帧,已强制保留(结尾画面组不丢)");
                    if (dropA.Count > 0)
                    {
                        dedupDroppedFrames.AddRange(dropA);
                        // 注意:时长表合并必须用"已移除尾帧"的 dropSetA(与文件删除同一集合),
                        // 否则时长表多删一条 → Count 与 frameCount 不齐 → VFR 时间轴被静默丢弃。
                        if (frameDurs != null) MergeDurations(frameDurs, dropSetA.ToList(), allA.Length);
                        int idxA = 0;
                        for (int n = 0; n < allA.Length; n++)
                        {
                            if (dropSetA.Contains(n + 1)) { try { File.Delete(allA[n]); } catch { } continue; }
                            idxA++;
                            File.Move(allA[n], Path.Combine(framesIn, $"frame_{idxA:D6}.png"), true);
                        }
                    }
                    frameCount = Directory.EnumerateFiles(framesIn, "*.png").Count();
                    effectiveFps = inFps * frameCount / Math.Max(1, origCountEst);
                    // 保留帧源号(1-based,升序):"补缺"用它把内容帧放回源时间轴
                    tempoSrcIdx = new System.Collections.Generic.List<int>();
                    for (int n = 1; n <= allA.Length; n++)
                        if (!dropSetA.Contains(n)) tempoSrcIdx.Add(n - 1);
                    if (!allowFewFrames) EnsureDedupResultSane(frameCount, origCountEst);
                    progress?.Report((5, $"智能去重完成:{origCountEst}→{frameCount} 帧,内容帧率≈{effectiveFps:0.##} fps"));
                }
            }
            else if (dedup && dedupMode == 3 && (dedupAlgo is 0 or 2))
            {
                if (dedupMode == 3 && dedupAlgo == 2)
                {
                    // 手动-帧差+SSIM 精确去重:用户自由阈值
                    progress?.Report((3, "去重:帧差初筛 + SSIM 精确验证(手动参数)..."));
                    frameCount = await ExtractFramesCoreAsync(ffmpeg, inputVideo, trimArgs,
                        scaleVf, framesIn, progress, ct, origCountEst, vfrPassthrough);
                    if (dedup || vfrPassthrough)
                        frameDurs = await BuildFrameDurationsAsync(ffmpeg, inputVideo, trimArgs, scaleVf, ct);
                    var dropM = DetectDupFramesWithSsim(framesIn,
                        Math.Clamp(dedupSadThr, 0.5, 10.0), Math.Clamp(dedupSsimThr, 0.90, 0.999),
                        dedupProtect, dedupWindow, dedupScale, dedupBlockThr,
                        dedupSegOn ? Math.Clamp(dedupSegSsim, 0.80, 0.99) : 0, Math.Clamp(dedupSegSad, 2, 10),
                        protectSmallMotion: manualProtectSmallMotion);   // 手动模式"微动防线"开关(默认开)
                    if (dropM.Count > 0)
                    {
                        dedupDroppedFrames.AddRange(dropM);
                        var allM = Directory.EnumerateFiles(framesIn, "*.png")
                            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
                        // 尾帧恒保留(与智能分支同判据):结尾画面组绝不因去重而丢;
                        // 否则保留帧轴到不了源末帧 → 补回提前截止(时长缩水)+ 下游越界。
                        if (dropM.Contains(allM.Length))
                        {
                            dropM.Remove(allM.Length);
                            AppLogger.Info("尾帧保护:帧差+SSIM 判定删除含末帧,已强制保留(结尾画面组不丢)");
                        }
                        if (frameDurs != null) MergeDurations(frameDurs, dropM, allM.Length);
                        int idxM = 0;
                        for (int n = 0; n < allM.Length; n++)
                        {
                            if (dropM.Contains(n + 1)) { try { File.Delete(allM[n]); } catch { } continue; }
                            idxM++;
                            File.Move(allM[n], Path.Combine(framesIn, $"frame_{idxM:D6}.png"), true);
                        }
                        // 保留帧源号(0-based,升序):帧差+SSIM 也走补回判定(非等距→补回生成,节奏精确;
                        // 否则每对统一 round 帧数,间隔不等时产生"停-跳-停"——用户实测卡)。
                        tempoSrcIdx = new System.Collections.Generic.List<int>();
                        for (int n = 1; n <= allM.Length; n++)
                            if (!dropM.Contains(n)) tempoSrcIdx.Add(n - 1);
                    }
                    frameCount = Directory.EnumerateFiles(framesIn, "*.png").Count();
                }
                else if (dedupMode == 3)
                {
                    // 手动模式(重复帧检测):完全按用户自由参数
                    var dedupVf = $"mpdecimate=hi=64*{Math.Clamp(dedupHi, 4, 24)}:lo=64*{Math.Clamp(dedupLo, 2, 10)}:frac={Math.Clamp(dedupFrac, 0.1, 0.6):0.##}";
                    progress?.Report((3, "去重:检测重复帧(mpdecimate)..."));
                    frameCount = await ExtractFramesCoreAsync(ffmpeg, inputVideo, trimArgs,
                        $"{dedupVf},{scaleVf}", framesIn, progress, ct, origCountEst, vfrPassthrough);
                    if (dedup || vfrPassthrough)
                        frameDurs = await BuildFrameDurationsAsync(ffmpeg, inputVideo, trimArgs, $"{dedupVf},{scaleVf}", ct);
                    // 保留帧源号:滤镜内丢帧,用 metadata=print 探测(同滤镜确定性输出);失败→null→回退标准补帧
                    tempoSrcIdx = await ProbeKeptFrameIdxAsync(ffmpeg, inputVideo, trimArgs,
                        $"{dedupVf},{scaleVf}", inFps, ct);
                    if (tempoSrcIdx != null)
                        AppLogger.Info($"韵律源帧:mpdecimate 保留帧源号 {tempoSrcIdx.Count} 个(末号 {tempoSrcIdx[^1]},源共 {origCountEst})");
                    else
                        AppLogger.Info("韵律源帧:mpdecimate 保留帧号探测失败/不足 → 回退标准补帧(旧行为)");
                }
                else
                {
                    // 智能(自适应)/动漫/标准/敏感模式:先全量拆帧,再帧差+SSIM 检测删帧。
                    // 动漫模式 SSIM 0.92:识别"一拍二/一拍三"的保持帧(含压缩噪声),去重后内容帧率回到 12/8fps;
                    // 标准模式 SSIM 0.97(固定):保守,只删几乎相同的帧,结果可预期;
                    // 敏感模式 SSIM 0.88 + 快筛放宽:特别宽,冗余极大的素材删得最狠,可能误删微动;
                    // 动漫去重:SSIM 用档位原值(弱0.92/中0.90/强0.88/极强0.85)——动漫的"一拍二/拍三"定格常带
                    // 压缩噪声,SSIM 未必拉到 0.95+,用 0.88/0.85 才能把它们也认出来(否则只删最干净的几帧=去重太弱)。
                    // 关键:不乱删真实微动靠【局部动作保护 protectRatio 压低】——定格"变化块占比≈0"仍删,真实动作(占比>阈值)保留,
                    // 所以强度差异靠 SSIM 带宽(0.92→0.85)拉大,而不是把 protectRatio 放大(那才会误删动作→卡)。
                    // 关键修正(实测驱动):低 SSIM 阈值(0.85~0.97)会把"相似但连续运动"的帧(素材1 实测相邻
                    // SSIM 0.86~0.99、对齐残差 5~26,无真定格)误当重复删掉 → 内容被过删(如 38→10)→ 补帧去桥大
                    // gap → 卡/糊。故把"原始 SSIM 判重"收紧为【只删真定格(≥0.995)】;人物定格交给"镜头运动
                    // 补偿判据"(对齐残差极小,见 DetectDupFramesWithSsim/Adaptive 的 motionComp 分支)去识别,
                    // 它不受背景平移/压缩噪声干扰,能抓住背景 pan 下的"一拍二/三"定格而不误删连续运动。
                    double ssimThr = dedupOnlyTrueHold
                        ? (dedupMode == 2 ? Math.Max(dedupAnimeThr, 0.995) : Math.Max(dedupMode == 5 ? 0.87 : 0.97, 0.995))
                        : (dedupMode == 2 ? dedupAnimeThr : (dedupMode == 5 ? 0.87 : 0.97));   // 关=老版旧阈值
                    // 动漫/敏感快筛阈值随强度:弱 0.92→3.0、中 0.90→3.5、强 0.88→4.0、极强 0.85→4.5;敏感→4.5
                    double sadThr = dedupMode switch
                    {
                        5 => 4.5,
                        2 => dedupAnimeThr switch { 0.90 => 3.5, 0.88 => 4.0, 0.85 => 4.5, _ => 3.0 },
                        _ => 3.0,
                    };
                    // 局部动作保护:区分两类帧——真动漫定格(一拍二/拍三,画面几乎不变,变化块占比≈0~0.1)
                    // 与"带轻微差异的重复帧"(占比 0.1~0.3)。protectRatio 太低(0.08)会只删最干净的真定格(只见 5 帧),
                    // 太高(0.45,敏感)会把带微动的也删光(27 帧→跳卡)。这里按强度给 0.15~0.28,
                    // 让强/极强能删到智能那种量级(≈15),又不至于像敏感那样删光。强度差异主要靠 SSIM 带宽 + protect。
                    double protectRatio = dedupMode switch
                    {
                        5 => 0.45,
                        2 => dedupAnimeThr switch { 0.90 => 0.18, 0.88 => 0.22, 0.85 => 0.28, _ => 0.15 },
                        _ => 0.12,
                    };
                    // 静止段合并:配合上面的"只删真定格"——把 segSsim 也抬到 ≥0.995(只合并"几乎完全相同"的静止段),
                    // 不再把"相似但连续运动"的长段并掉(那会过删→卡)。人物定格由运动补偿判据负责。
                    // 动漫模式勾选了「静止段合并」时,直接用右侧滑条的用户值(动漫页可调),否则用按强度的内置值。
                    double segSsim = 0, segSad = 5;
                    if (dedupMode == 2)
                    {
                        if (dedupSegOn)
                        {
                            segSsim = dedupOnlyTrueHold ? Math.Max(dedupSegSsim, 0.995) : dedupSegSsim;
                            segSad = dedupSegSad;
                        }
                        else
                        {
                            double raw = dedupAnimeThr switch { 0.85 => 0.93, 0.88 => 0.94, 0.90 => 0.94, _ => 0.95 };
                            segSsim = dedupOnlyTrueHold ? Math.Max(raw, 0.995) : raw;   // 关=老版旧阈值
                            segSad = dedupAnimeThr switch { 0.90 => 5.0, 0.88 => 6.0, 0.85 => 6.5, _ => 4.0 };
                        }
                    }
                    else if (dedupMode == 5) { segSsim = dedupOnlyTrueHold ? Math.Max(0.88, 0.995) : 0.88; segSad = 6.5; }
                    progress?.Report((3, dedupMode == 1
                        ? "去重:自适应检测(先算差异分布再自动定阈值)..."
                        : "去重:帧差初筛 + SSIM 精确验证(手动参数)..."));
                    frameCount = await ExtractFramesCoreAsync(ffmpeg, inputVideo, trimArgs,
                        scaleVf, framesIn, progress, ct, origCountEst, vfrPassthrough);
                    if (dedup || vfrPassthrough)
                        frameDurs = await BuildFrameDurationsAsync(ffmpeg, inputVideo, trimArgs, scaleVf, ct);
                    // 逐帧检测(全帧解码小图+SAD/SSIM,CPU 重活)→ 后台线程,防拆帧后卡 UI
                    var drop = dedupMode == 1
                        ? await Task.Run(() => DetectDupFramesAdaptive(framesIn, progress, 16, dedupSmartMode, motionCompDedup), ct)
                        : await Task.Run(() => DetectDupFramesWithSsim(framesIn, sadThr, ssimThr, protectRatio, 6, 16, 4, segSsim, segSad, motionCompDedup), ct);
                    // 末帧永远保留:视频最后一张画面即使与前一帧相似也必须保留,
                    // 否则输出尾部会缺失原视频末帧内容(用户看到"最后一帧不是原视频最后一帧")。
                    if (drop.Count > 0 && frameCount > 0) drop.Remove(frameCount);
                    if (drop.Count > 0)
                    {
                        dedupDroppedFrames.AddRange(drop);
                        var allFiles = Directory.EnumerateFiles(framesIn, "*.png")
                            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
                        if (frameDurs != null) MergeDurations(frameDurs, drop, allFiles.Length);
                        int idx = 0;
                        for (int n = 0; n < allFiles.Length; n++)
                        {
                            if (drop.Contains(n + 1)) { try { File.Delete(allFiles[n]); } catch { } continue; }
                            idx++;
                            File.Move(allFiles[n],
                                Path.Combine(framesIn, $"frame_{idx:D6}.png"), true);
                        }
                    }
                    frameCount = Directory.EnumerateFiles(framesIn, "*.png").Count();
                }
                if (!allowFewFrames) EnsureDedupResultSane(frameCount, origCountEst);
                if (frameCount == 0)
                    throw new InvalidOperationException("去重后无有效帧,请降低去重强度或关闭去重");
                var fullDur2 = await ProbeDurationSeconds(inputVideo);
                var effDur2 = (trimEnd ?? fullDur2) - (trimStart ?? 0);
                var origCount2 = effDur2 > 0 ? (int)Math.Round(effDur2 * inFps) : frameCount;
                effectiveFps = inFps * frameCount / Math.Max(1, origCount2);
                var learned2 = new[] { 8.0, 10, 12, 15, 24, 25, 30 }.OrderBy(a => Math.Abs(effectiveFps - a)).First();
                var learnedMsg2 = Math.Abs(effectiveFps - learned2) / learned2 < 0.1 ? $"(内容帧率 {learned2:0} fps)" : "";
                progress?.Report((5, $"已拆出 {frameCount} 帧(去重约 {Math.Max(0, origCountEst - frameCount)} 帧重复画面,有效帧率 {effectiveFps.ToString("0.##", inv)} fps {learnedMsg2})"));
            }
            else
            {
                // scene 反选模式(温和/手动-画面变化阈值):保留首帧 + 与前一帧差异 > 阈值的帧
                double dedupThr;
                if (!dedup) dedupThr = 0;
                else if (dedupMode == 3) dedupThr = Math.Clamp(dedupThreshold, 0.001, 0.5);   // 手动-scene:滑条
                else dedupThr = 0.005;                                                         // 兜底:默认 0.005
                string sceneVf = $"{(dedup ? $"select='eq(n,0)+gt(scene,{dedupThr.ToString("0.###", inv)})'," : "")}{scaleVf}";
                frameCount = await ExtractFramesCoreAsync(ffmpeg, inputVideo, trimArgs,
                    sceneVf, framesIn, progress, ct, origCountEst, vfrPassthrough);
                if (dedup || vfrPassthrough)
                    frameDurs = await BuildFrameDurationsAsync(ffmpeg, inputVideo, trimArgs, sceneVf, ct);
                // 保留帧源号:scene 滤镜内丢帧,用 metadata=print 探测(纯 select,不含 fps/scale——真实保留数);
                // 失败→null→回退标准补帧
                if (dedup)
                {
                    tempoSrcIdx = await ProbeKeptFrameIdxAsync(ffmpeg, inputVideo, trimArgs,
                        $"select='eq(n,0)+gt(scene,{dedupThr.ToString("0.###", inv)})'", inFps, ct);
                    if (tempoSrcIdx != null)
                    {
                        AppLogger.Info($"韵律源帧:scene 保留帧源号 {tempoSrcIdx.Count} 个(末号 {tempoSrcIdx[^1]},源共 {origCountEst})");
                        // 阈值过高的守卫:变化帧太少(只剩首帧级)→ 帧率覆盖时会被 fps 复制成"满帧"假象
                        // (画面定格视频),必须明确提示,绝不无声产出。
                        double keepPct = 100.0 * tempoSrcIdx.Count / Math.Max(1, origCountEst);
                        if (keepPct < 20)
                        {
                            progress?.Report((4, $"⚠ 变化阈值 {dedupThr:0.###} 过高:仅保留 {tempoSrcIdx.Count} 帧(相当于 {keepPct:0}% 画面),输出会接近静止——建议降低阈值(≤0.05),或改用「内容帧率采样/智能检测」"));
                            AppLogger.Info($"⚠ 变化阈值 {dedupThr:0.###} 过高:仅保留 {tempoSrcIdx.Count}/{origCountEst} 帧({keepPct:0}% 画面),输出接近静止(视频将几乎定格)");
                        }
                    }
                    else
                        AppLogger.Info("韵律源帧:scene 保留帧号探测失败/不足 → 回退标准补帧(旧行为)");
                }
                if (frameCount == 0)
                    throw new InvalidOperationException("视频拆帧失败,未能提取到帧画面(请检查裁剪时间是否有效)");
                // 去重后有效帧率:按帧数等比换算,保持时长。
                // 注意:有裁剪时基准帧数按"裁剪段时长"计算,不能按原视频总帧数(否则帧率被稀释导致慢放)
                if (dedup)
                {
                    var fullDur = await ProbeDurationSeconds(inputVideo);
                    var effDur = (trimEnd ?? fullDur) - (trimStart ?? 0);
                    var origCount = effDur > 0 ? (int)Math.Round(effDur * inFps) : frameCount;
                    effectiveFps = inFps * frameCount / Math.Max(1, origCount);
                    var learned = new[] { 8.0, 10, 12, 15, 24, 25, 30 }.OrderBy(a => Math.Abs(effectiveFps - a)).First();
                    var learnedMsg = Math.Abs(effectiveFps - learned) / learned < 0.1
                        ? $"(内容帧率 {learned:0} fps)"
                        : "";
                    if (!allowFewFrames) EnsureDedupResultSane(frameCount, origCountEst);
                    progress?.Report((5, $"已拆出 {frameCount} 帧(去重,有效帧率 {effectiveFps.ToString("0.##", inv)} fps {learnedMsg})"));
                }
                else
                {
                    progress?.Report((5, $"已拆出 {frameCount} 帧"));
                }
            }

            // 手动-语义运动分析(独立叠加开关):上方算法去重完成后,再叠加检测镜头平移/背景滚动。
            // 在已去重的帧上二次分析:整幅画面均匀移动(镜头平移/背景滚动)=内容相同的冗余帧删;
            // 只有局部轮廓动(人物张嘴/眨眼)=角色动作保留;真实场景切换/大变化保留。
            if (dedup && dedupMode == 3 && dedupPanOn)
            {
                progress?.Report((3, "去重(叠加):语义运动分析(镜头均匀移动=冗余,局部动作=保留)..."));
                var dropPan = DetectDupFramesWithMotion(framesIn, Math.Clamp(dedupPanThr, 1, 10), progress, dedupScale, dedupProtect, dedupBlockThr, Math.Clamp(dedupPanMax, 10, 60));
                // 末帧永远保留(同主去重:输出必须包含原视频最后一张画面)
                if (dropPan.Count > 0 && frameCount > 0) dropPan.Remove(frameCount);
                if (dropPan.Count > 0)
                {
                    dedupDroppedFrames.AddRange(dropPan);
                    var allPan = Directory.EnumerateFiles(framesIn, "*.png")
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
                    if (frameDurs != null) MergeDurations(frameDurs, dropPan, allPan.Length);
                    int idxPan = 0;
                    for (int n = 0; n < allPan.Length; n++)
                    {
                        if (dropPan.Contains(n + 1)) { try { File.Delete(allPan[n]); } catch { } continue; }
                        idxPan++;
                        File.Move(allPan[n], Path.Combine(framesIn, $"frame_{idxPan:D6}.png"), true);
                    }
                }
                frameCount = Directory.EnumerateFiles(framesIn, "*.png").Count();
                if (!allowFewFrames) EnsureDedupResultSane(frameCount, origCountEst);
                if (frameCount == 0)
                    throw new InvalidOperationException("去重后无有效帧,请降低去重强度或关闭去重");
                var fullDurPan = await ProbeDurationSeconds(inputVideo);
                var effDurPan = (trimEnd ?? fullDurPan) - (trimStart ?? 0);
                var origCountPan = effDurPan > 0 ? (int)Math.Round(effDurPan * inFps) : frameCount;
                effectiveFps = inFps * frameCount / Math.Max(1, origCountPan);
                var learnedPan = new[] { 8.0, 10, 12, 15, 24, 25, 30 }.OrderBy(a => Math.Abs(effectiveFps - a)).First();
                var learnedMsgPan = Math.Abs(effectiveFps - learnedPan) / learnedPan < 0.1 ? $"(内容帧率 {learnedPan:0} fps)" : "";
                progress?.Report((5, $"叠加去重后 {frameCount} 帧(语义运动分析,有效帧率 {effectiveFps.ToString("0.##", inv)} fps {learnedMsgPan})"));
            }

            // 去重统计报告:删了多少帧、有效帧率变化(只在真删过帧时输出)。
            // SAD+SSIM 路径逐帧记录删除帧号 → 能算"最集中在哪段时间";scene/mpdecimate 的丢帧发生在 ffmpeg
            // 滤镜内部,拿不到逐帧号 → 用总删除数统计,并明确标注(不误导)。
            bool havePerFrame = dedupDroppedFrames.Count > 0;
            int removedTotalAll = Math.Max(0, origCountEst - frameCount);
            if (dedup && dedupMode == 3 && (dedupAlgo is 0 or 1 or 2) && (havePerFrame || removedTotalAll > 0))
            {
                try
                {
                    int removedTotal = havePerFrame ? dedupDroppedFrames.Count : removedTotalAll;
                    var allFrameCount = havePerFrame
                        ? Directory.EnumerateFiles(framesIn, "*.png").Count() + removedTotal
                        : origCountEst;
                    string locNote;
                    if (havePerFrame)
                    {
                        // 按时间分 8 段(段号 → 删除帧数),找重复最集中的时间段
                        const int segs = 8;
                        var segCount = new int[segs];
                        foreach (var fn in dedupDroppedFrames)
                        {
                            int seg = Math.Clamp((fn - 1) * segs / Math.Max(1, allFrameCount), 0, segs - 1);
                            segCount[seg]++;
                        }
                        int bestSeg = 0;
                        for (int s = 1; s < segs; s++) if (segCount[s] > segCount[bestSeg]) bestSeg = s;
                        var fullDur = (trimEnd ?? await ProbeDurationSeconds(inputVideo)) - (trimStart ?? 0);
                        double segDur = fullDur / segs;
                        var segStart = trimStart ?? 0;
                        var t0 = segStart + bestSeg * segDur;
                        var t1 = segStart + (bestSeg + 1) * segDur;
                        locNote = $"最集中在 {FormatTime(t0)}~{FormatTime(t1)},";
                    }
                    else
                    {
                        locNote = "逐帧分布仅帧差+SSIM路径精确,";
                    }
                    var pct = 100.0 * removedTotal / Math.Max(1, allFrameCount);
                    progress?.Report((5, $"去重完成:{allFrameCount}→{allFrameCount - removedTotal} 帧,删 {removedTotal} 帧({pct:0.0}%),有效帧率 {inFps:0.##}→{effectiveFps:0.##} fps"));
                    LastDedupReport = $"去重:{removedTotal} 帧/{allFrameCount}({pct:0.0}%),{locNote}有效帧率 {inFps:0.##}→{effectiveFps:0.##} fps";
                    LastDedupShort = $"内容帧率 {inFps:0.#}→{effectiveFps:0.#} fps · 去重 {allFrameCount}→{allFrameCount - removedTotal} 帧";
                    AppLogger.Info(LastDedupReport);
                }
                catch { /* 统计失败不影响主流程 */ }
            }

            // 指定输出帧率时:自动算够补帧倍率(不再用固定倍率,保证"填多少最终就多少")。
            // 内容帧率在去重后已确定,目标帧率 ÷ 内容帧率 = 需要的倍率,向上取整。
            if (frameInterp && targetFps is > 0)
            {
                // 达到目标帧率所需倍率:A(内容×倍率)用内容帧率 effectiveFps;B/方案C(原×倍率)用原帧率 inFps。
                // 关键:目标帧率≈输入帧率(如 60 vs 59.94)时 ceil=1 → 完全不补帧 → "指定帧率导出还是卡"。
                // 倍率至少 2x:先补帧平滑再压到目标帧率(否则等于没补帧)。
                double scaleBase = fpsMode == 1 ? effectiveFps : inFps;
                int needScale = Math.Max(2, Math.Clamp((int)Math.Ceiling(targetFps.Value / Math.Max(1, scaleBase)), 1, 8));
                // v2 模型只能 2 的幂倍率(2/4/8),就近向上取 2 的幂(可能补到略高于目标,再由 fps 滤镜抽准)
                if (!IsV4Model(interpModel))
                {
                    int p = 1;
                    while (p < needScale) p *= 2;
                    needScale = Math.Min(p, 8);
                }
                if (needScale != interpScale)
                {
                    progress?.Report((6, $"指定 {targetFps.Value:0.##} fps,自动补帧 {needScale}x(内容帧率 {effectiveFps:0.##} fps)"));
                    AppLogger.Info($"目标帧率:指定 {targetFps.Value:0.##} fps → 自动补帧 {needScale}x(内容帧率 {effectiveFps:0.##} fps)");
                }
                interpScale = needScale;
            }

            // 需要"重定时"(setpts 保内容时间轴)的情况:真 VFR 素材,【以及去重删过帧的素材】。
            // 去重删过帧后剩余关键帧在真实时间上不等距(有长静止段、有快速动作);补帧若按帧序号均匀铺 + 固定帧率
            // 合帧,会把真实时间压缩/拉长 → 普遍"掉帧/节奏错"(研究结论#1,用户实测"有补帧也不够流畅")。
            // 故去重删过帧时也用「每帧真实时长表」重定时:输出 VFR 时间轴=原视频节奏,补帧只负责填运动、不改变时间。
            bool preserveRhythm = vfrPassthrough || (dedup && frameDurs != null && frameDurs.Count == frameCount);

            // 3) 补帧(可选):RIFE 在原始分辨率上插值(video2X 同款顺序:先补帧后超分,
            //    避免在大图上补帧造成 9 倍开销);转场识别时按转场点分段,段内插值、转场处不插
            var frameFiles = Directory.EnumerateFiles(framesIn, "*.png")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
            // 最终帧时长表(合帧用):有原始时长表时,补帧按"每源帧展开"填充,否则 null → 固定帧率输出
            System.Collections.Generic.List<double>? finalDurs = null;
            // 防御:时长表帧数与实际帧数不一致(探测异常)时整体回退固定帧率,绝不让索引越界
            if (frameDurs != null && frameDurs.Count != frameCount) frameDurs = null;
            if (frameInterp && rife != null)
            {
                // ===== 拍数等距(拍二/三/四 × 整数倍率)→ 标准补帧一次 = "直接倍数补回来"(无需补回/层批) =====
                // 标准补帧 mult=round(frameScale×倍率):等距整数时=拍距×倍率,精确(每对=4/6/8 帧)。
                // 非整(4.17×8=33.4/4.008×8=32.06 或智能删帧非等距)→ 才需要"补回(生成)"(逐对精确,否则 round 压缩)。
                if (tempoSrcIdx != null && tempoSrcIdx.Count > 2)
                {
                    // 轴完整性守卫:保留帧轴必须到"源末帧"(±2 帧容差)——滤镜内丢帧的路径(mpdecimate/scene)
                    // 可能把尾帧也丢了,补回只能输出到最后一个保留帧 → 时长缩水+下游越界;此时回退标准补帧(旧行为)。
                    int srcLast = Math.Max(1, origCountEst - 1);
                    if (tempoSrcIdx[^1] < srcLast - 2)
                    {
                        AppLogger.Info($"轴完整性守卫:保留帧轴仅到源号 {tempoSrcIdx[^1]}/{srcLast}(尾部缺失),回退标准补帧");
                        tempoSrcIdx = null;
                    }
                }
                if (tempoSrcIdx != null && tempoSrcIdx.Count > 2)
                {
                    int minGap = int.MaxValue, maxGap = 0;
                    for (int i = 1; i < tempoSrcIdx.Count - 1; i++)   // 尾帧保护不参与
                    {
                        int g = tempoSrcIdx[i] - tempoSrcIdx[i - 1];
                        if (g < 1) g = 1;
                        if (g < minGap) minGap = g;
                        if (g > maxGap) maxGap = g;
                    }
                    bool closed = Math.Abs((double)minGap * interpScale - Math.Round((double)minGap * interpScale)) < 0.02;
                    if (maxGap == minGap && closed)
                    {
                        AppLogger.Info($"拍型判定:等距 {minGap} 帧×{interpScale}={minGap * interpScale} 整 → 标准补帧(一次,快)");
                        tempoSrcIdx = null;   // 等距+整拍:标准补帧(一次,快)
                    }
                    else
                    {
                        AppLogger.Info($"拍型判定:{(maxGap != minGap ? $"非等距(间隔 {minGap}~{maxGap} 帧)" : $"等距 {minGap} 帧×{interpScale} 非整")} → 补回(生成)再 ×{interpScale}(逐槽精确)");
                    }
                }
                // ===== 补回(生成版,仅非整格):内容帧对之间"真·生成渐变帧"还原源时间轴 → 再 ×倍率 =====
                if (tempoSrcIdx != null && tempoSrcIdx.Count > 1)
                {
                    progress?.Report((10, $"补回还原源时间轴({tempoSrcIdx[^1] + 1} 帧)..."));
                    try
                    {
                        double srcDur = (trimEnd ?? await ProbeDurationSeconds(inputVideo)) - (trimStart ?? 0);
                        var resR = await RunTempoResampleAsync(rife, framesIn, framesFinal, frameCount,
                            inFps, tempoSrcIdx, 1, inFps, gpuId, srcDur, interpModel, tta, progress, ct);
                        foreach (var f in Directory.EnumerateFiles(framesIn, "*.png")) File.Delete(f);
                        foreach (var f in Directory.EnumerateFiles(framesFinal, "*.png"))
                            File.Copy(f, Path.Combine(framesIn, Path.GetFileName(f)), true);
                        foreach (var f in Directory.EnumerateFiles(framesFinal, "*.png")) File.Delete(f);
                        frameCount = resR.frameCount;
                        effectiveFps = inFps;
                        frameDurs = null;   // 补回 = 源轴 CFR 序列,旧时长表(按删帧合并)已失效,必须清(否则帧数已变,下游按旧表越界)
                        tempoSrcIdx = null;
                        progress?.Report((10, $"补回完成({frameCount} 帧,继续 ×{interpScale} 补帧...)"));
                    }
                    catch (Exception ex)
                    {
                        if (ct.IsCancellationRequested) throw;   // 取消必须立刻传播,绝不吞(否则会回退再跑一遍标准补帧)
                        AppLogger.Info($"补回来失败(按展开兜底):{ex.Message}");
                        tempoSrcIdx = null;
                    }
                }
                {
                    // 3a) 转场检测(基于拆帧后的图片序列,pts_time=帧号)
                var cuts = new List<int>();
                if (sceneThreshold is > 0)
                {
                    progress?.Report((8, $"转场识别(阈值 {sceneThreshold.Value:0.00})..."));
                    var th = sceneThreshold.Value.ToString("0.###", inv);
                    try
                    {
                        var lines = await RunCaptureAsync(ffmpeg,
                            $"-y -framerate 1 -i \"{Path.Combine(framesIn, "frame_%06d.png")}\" " +
                            $"-vf \"select='gt(scene,{th})',metadata=print\" -f rawvideo NUL",
                            ct);
                        foreach (var line in lines)
                        {
                            var m = System.Text.RegularExpressions.Regex.Match(line, @"pts_time:(\d+(?:\.\d+)?)");
                            if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, inv, out var pts))
                                cuts.Add((int)Math.Round(pts));
                        }
                        cuts.RemoveAll(c => c <= 0 || c >= frameCount); // 忽略首尾无效点
                        cuts.Sort();
                    }
                    catch { /* 检测失败不阻塞,按无转场处理 */ }
                    progress?.Report((9, $"转场识别:发现 {cuts.Count} 处转场" + StageElapsed()));
                }

                // 3b) 分段补帧(输入 framesIn,输出 framesFinal)
                progress?.Report((10, $"RIFE 补帧({interpScale}x,源 {frameCount} 帧 → 输出 {(long)Math.Round((double)((frameCount - 1) * interpScale)) + 1} 帧,模型 {interpModel})..."));
                // ===== RIFE GPU 探测(50 系等可能静默 hang,不预检白等 8 分钟)=====
                // 实测 2 帧插 1 帧能否 GPU 出图;不能 → 本视频补帧改用 CPU(慢但确定能跑),日志+进度提示。
                int interpGpu = gpuId;
                if (gpuId >= 0)
                {
                    progress?.Report((10, $"正在检测补帧 GPU 兼容性(最长 5 秒)..."));
                    bool rifeOk = await EngineService.IsRifeGpuUsableAsync(rife, interpModel, gpuId, ct).ConfigureAwait(false);
                    if (!rifeOk)
                    {
                        AppLogger.Warn($"⚠ RIFE {interpModel} GPU 探测失败,改用 CPU 补帧(慢但不会挂起白等)");
                        progress?.Report((10, $"⚠ RIFE 无法用 GPU,自动改用 CPU 补帧(较慢但稳定)..."));
                        interpGpu = -1;   // 本视频后续补帧 API 全部 CPU(InterpSegmentAsync 传入)
                    }
                }
                var segStart = 0;
                int globalIdx = 1;
                int segNo = 0;
                var segBounds = new System.Collections.Generic.List<(int s, int e)>();
                foreach (var c in cuts)
                {
                    if (c > segStart) segBounds.Add((segStart, c));
                    segStart = c;
                }
                if (segStart < frameCount) segBounds.Add((segStart, frameCount));
                double frameScale = frameCount > 0 ? Math.Min(6.0, (double)origCountEst / frameCount) : 1.0;
                bool v4Model = IsV4Model(interpModel);
                // ===== 方案 C(真实时间轴插值/对齐丝滑):「密度还原 → 整段一次 RIFE → 帧数精确对齐」=====
                // 整段序列喂给 RIFE,光流上下文足(估得准、不糊不扭);密度还原把各状态的真实停留时长铺回同一条
                // CFR 网格,时长=原、不吞尾;输出帧数由下方 globalTarget=原帧数×倍率 精确对齐(与参考补帧同款结果)。
                {
                    // C(真实时间轴插值,推荐):直接在"去重后关键帧"上按 frameScale 插值(不重复帧)。
                    // 关键:密度还原(按真实停留时长把关键帧重复 g 次)会让 RIFE 在重复的相同帧之间保持静止,
                    // → 人物"停顿-跳-停顿"(一拍二/三的步进感=卡)。参考期老版(fullsmoke)就是"不重复帧、
                    // 用 frameScale=origCount/frameCount 缩放 -n 直接插",人物连贯平滑。故此处停用密度还原,
                    // 保持 frameScale(上面 L635 已算好),与老版一致。
                    /*
                    if ((fpsMode == 0 || fpsMode == 2) && frameDurs != null && frameDurs.Count == frameCount && frameCount > 1)
                    {
                        ... 密度还原:重复关键帧 ...
                    }
                    */
                    // A(极致流畅)/ 未去重 / 非 v4:均匀插值(单次 RIFE)。A=内容×倍率,不做原密度缩放 → frameScale=1。
                    if (fpsMode == 1) frameScale = 1.0;
                    // 全局输出帧数目标 = (内容帧数-1)×倍率+1(A)或 (原帧数-1)×倍率+1(B/未去重):
                    // 末段 RIFE -n 补足,使最后锚点帧精确落在最后一帧(避免合帧裁剪吞尾帧)。
                    long globalTarget = Math.Max(frameCount + 1,
                        (long)Math.Round((double)((fpsMode == 1 ? frameCount : origCountEst) - 1) * interpScale) + 1);
                    for (int si = 0; si < segBounds.Count; si++)
                    {
                        var (s, e) = segBounds[si];
                        segNo++;
                        bool isLastSeg = si == segBounds.Count - 1;
                        int segLen = e - s;
                        // 本段插值目标帧数(与 InterpSegmentAsync 内部一致,用于把本段帧数映射到全局输出)
                        int segTarget = isLastSeg
                            ? (int)Math.Max(segLen + 1, globalTarget - (globalIdx - 1))
                            : (int)Math.Max(segLen + 1, (int)Math.Round(segLen * interpScale * frameScale));
                        // 包装进度:把本段帧数(1..segTarget)映射到全局累计,显示"总帧慢慢加上去"(而不是已处理/expand 帧数)
                        IProgress<(int pct, string msg)>? segProg = progress == null ? null
                            : new System.Progress<(int pct, string msg)>(t =>
                            {
                                int local = 0;
                                var m = System.Text.RegularExpressions.Regex.Match(t.msg, @"第\s*(\d+)\s*帧");
                                if (m.Success) local = int.Parse(m.Groups[1].Value);
                                else local = (int)(t.pct / 100.0 * Math.Max(1, segTarget));
                                long gf = (long)Math.Min(globalTarget, (globalIdx - 1) + Math.Max(0, local));
                                progress!.Report((10 + (int)(35.0 * gf / Math.Max(1, globalTarget)), $"补帧 第 {gf} 帧 / 共 {globalTarget} 帧"));
                            });
                        progress?.Report((10 + (int)(35.0 * segNo / segBounds.Count),
                            $"补帧 第 {globalIdx - 1} 帧 / 共 {globalTarget} 帧(段 {segNo}/{segBounds.Count})..."));
                        // 处理过程也做降温休息检查(单个长视频也能中途休息)
                        await SafeRender.RestIfDueAsync(10 + (int)(35.0 * segNo / segBounds.Count), progress, ct);
                        if (pauseWait != null) await pauseWait();   // 暂停:当前补帧段跑完即停(几秒)
                        // 时长表展开:真 VFR 素材 或 去重删过帧(内容时间轴不均匀)时,展开每帧真实时长供 setpts 重定时;
                        // 否则按固定帧率均匀输出,无需展开。
                        if (frameDurs != null && preserveRhythm)
                        {
                            finalDurs ??= new System.Collections.Generic.List<double>();
                            double per = interpScale;
                            int perN = Math.Max(1, (int)Math.Round(per));
                            for (int k = s; k < e; k++)
                            {
                                double d = Math.Max(0.0005, frameDurs[k] / per);
                                for (int m = 0; m < perN; m++) finalDurs.Add(d);
                            }
                        }
                        globalIdx = await InterpSegmentAsync(rife, framesIn, framesFinal, s, e, interpScale,
                            interpModel, timeStep, tta, interpGpu, globalIdx, segProg, ct, frameScale,
                            isLastSeg ? globalTarget : 0,
                            false);   // appendTailCopy = false
                        if (progress != null)
                            progress.Report((10 + (int)(35.0 * (globalIdx - 1) / Math.Max(1, globalTarget)),
                                $"补帧 第 {globalIdx - 1} 帧 / 共 {globalTarget} 帧(段 {segNo}/{segBounds.Count})"));
                    }
                }

                var interpCount = Directory.EnumerateFiles(framesFinal, "*.png").Count();
                if (interpCount == 0)
                    throw new InvalidOperationException("补帧失败,未生成插帧");
                // 帧数对齐已移至"muxDur/outFps 已知处"(时长=源容器 × 帧率),此处不再处理(需帧率公式才能定目标)。
                // 注:补帧诊断(输出帧数/frameScale)也移到合帧前与实际输出帧数一并打印。
                progress?.Report((45, $"补帧完成({interpCount} 帧,含原始帧)" + StageElapsed()));
                }
            }
            else
            {
                // 不补帧:拆帧结果直接作为处理帧
                foreach (var f in frameFiles)
                    File.Copy(f, Path.Combine(framesFinal, Path.GetFileName(f)), true);
                finalDurs = frameDurs != null
                    ? new System.Collections.Generic.List<double>(frameDurs) : null;
                progress?.Report((45, $"帧准备完成({frameFiles.Length} 帧)"));
            }

            // 4) 超分(可选):批处理在补帧后的帧上进行;1x 时直接剔除(不超分,帧原样使用)
            //    1x超分(2x放大后缩回):内部按 2x 超分,再缩回原始尺寸,输出仍是 1x
            if (doUpscale && scale <= 1.001 && !upscaleShrink1x)
            {
                progress?.Report((45, "1x 不超分:跳过超分阶段"));
            }
            else if (doUpscale)
            {
                // 1x超分:记录原始帧尺寸(拆帧/补帧后的帧尺寸),供 2x 超分后缩回
                int? origW = null, origH = null;
                if (upscaleShrink1x)
                {
                    var firstFrame = Directory.EnumerateFiles(framesFinal, "*.png")
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                    if (firstFrame != null)
                    {
                        using var fb = new System.Drawing.Bitmap(firstFrame);
                        origW = fb.Width; origH = fb.Height;
                    }
                }
                var upScale = upscaleShrink1x ? 2.0 : scale;
                // ===== 超分 GPU 探测(避免"GPU hang 8 分钟"白等)=====
                // 50 系/AMD/Intel/老驱动等:当前引擎在 GPU 上跑 1×1 图如果能出图 → GPU 放心用;
                // 不能 → 直接改 CPU,并提示用户(不再等引擎启动失败/黑帧降级,省时间)。
                int upGpu = gpuId;
                if (gpuId >= 0)
                {
                    progress?.Report((45, $"正在检测超分 GPU 兼容性(最长 5 秒)..."));
                    bool usable = await EngineService.IsEngineGpuUsableAsync(engine, gpuId, ct).ConfigureAwait(false);
                    if (!usable)
                    {
                        AppLogger.Warn($"⚠ 超分引擎 {engine} GPU 探测失败,改用 CPU(视频超分会慢,但不会卡死白等)");
                        progress?.Report((45, $"⚠ 超分引擎 {engine} 无法用 GPU,自动改用 CPU 计算(较慢但稳定)..."));
                        upGpu = -1;
                    }
                }
                // 分批目录批处理超分 + 并行 2 批(video2x 式多 worker):
                // 一次引擎启动处理一批帧,避免每帧启动引擎;批间并行提高 GPU 利用率
                var upInput = framesFinal;
                var upOutput = Path.Combine(workDir, "upscaled");
                Directory.CreateDirectory(upOutput);
                var upFiles = Directory.EnumerateFiles(upInput, "*.png")
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
                // 批大小/并发按"安全渲染"墙自适应(内存/显存墙越小越保守)
                int batchSize = SafeRender.GetVideoBatchSize();
                if (fastMode) batchSize = Math.Max(8, batchSize / 2);   // 快速模式:帧批减半,内存峰值更低(弱设备)
                var total = upFiles.Length;
                var batches = (total + batchSize - 1) / batchSize;
                using var sem = new SemaphoreSlim(fastMode ? 1 : SafeRender.GetVideoConcurrency());   // 快速模式:单批防显存竞争
                int doneFrames = 0;
                var tasks = new System.Collections.Generic.List<Task>();
                for (int b = 0; b < total; b += batchSize)
                {
                    ct.ThrowIfCancellationRequested();
                    // 处理过程也做降温休息检查(单个长视频也能中途休息;按批检查,高频批时开销极小)
                    await SafeRender.RestIfDueAsync(45 + (int)(45.0 * b / Math.Max(1, total)), progress, ct);
                    if (pauseWait != null) await pauseWait();   // 暂停:当前超分批跑完即停(几秒~十几秒)
                    int start = b;
                    int end = Math.Min(total, b + batchSize);
                    await sem.WaitAsync(ct);
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            var batchIn = Path.Combine(workDir, $"up_in_{start}");
                            var batchOut = Path.Combine(workDir, $"up_out_{start}");
                            Directory.CreateDirectory(batchIn);
                            Directory.CreateDirectory(batchOut);
                            for (int i = start; i < end; i++)
                                File.Copy(upFiles[i], Path.Combine(batchIn, Path.GetFileName(upFiles[i])), true);
                            progress?.Report((45 + (int)(45.0 * start / total),
                                $"超分 已处理 {start} 帧 / 共 {total} 帧(批次 {start / batchSize + 1}/{batches})..."));
                            await EngineService.UpscaleDirAsync(batchIn, batchOut, engine, model,
                                upScale, 0, upGpu, false, progress, ct,
                                SafeRender.GetTileSize() / (fastMode ? 2 : 1),   // 分块按"安全渲染"墙;快速模式再减半(显存占用约降 4 倍)
                                watchStage: "超分",   // 逐帧汇报(像补帧一样显示"超分 第 N 帧 / 共 M 帧")
                                globalBaseFrames: start, globalTotalFrames: total);   // 百分比按全局帧数算,预计时间才准
                            foreach (var f in Directory.EnumerateFiles(batchOut, "*.png"))
                                File.Copy(f, Path.Combine(upOutput, Path.GetFileName(f)), true);
                            // 黑帧防御:ncnn-vulkan 偶发 vkQueueSubmit 失败 → 输出全黑帧(退出码 0 不报错)。
                            // 检测到黑帧即用 CPU 重处理该批(引擎线程参数已改 save=1 降低概率,这里兜底:
                            // 万一还是黑,CPU 软解不依赖 GPU 队列,绝不出黑帧)。
                            // 兜底防误杀:若【源帧】本来就近全黑(视频黑场/淡入淡出),输出黑是素材本身,
                            // 不是 GPU 故障——跳过降级,不浪费 CPU 重算。
                            if (batchOutDirHasBlack(batchOut) && !DirNearBlack(batchIn))
                            {
                                progress?.Report((45 + (int)(45.0 * start / total),
                                    $"⚠ 检测到黑帧(批次 {start}~{end - 1},GPU 输出异常),该批改用 CPU 重处理..." + StageElapsed()));
                                AppLogger.Info($"降级:批次 {start}~{end - 1} 输出黑帧(ncnn-vulkan GPU 队列异常),已用 CPU 重处理该批");
                                await EngineService.UpscaleDirAsync(batchIn, batchOut, engine, model,
                                    upScale, 0, -1, false, progress, ct,
                                    SafeRender.GetTileSize() / (fastMode ? 2 : 1),
                                    watchStage: "超分",
                                    globalBaseFrames: start, globalTotalFrames: total);
                                foreach (var f in Directory.EnumerateFiles(batchOut, "*.png"))
                                    File.Copy(f, Path.Combine(upOutput, Path.GetFileName(f)), true);
                            }
                            Interlocked.Add(ref doneFrames, end - start);
                            progress?.Report((45 + (int)(45.0 * doneFrames / total),
                                $"超分 已处理 {doneFrames} 帧 / 共 {total} 帧"));
                            if (fastMode)
                            {
                                // 快速模式:批后立即释放临时帧 + 强制 GC,内存峰值降到最低(弱设备不内存墙)
                                try { Directory.Delete(batchIn, true); } catch { }
                                try { Directory.Delete(batchOut, true); } catch { }
                                GC.Collect();
                                GC.WaitForPendingFinalizers();
                            }
                        }
                        finally
                        {
                            sem.Release();
                        }
                    }, ct));
                }
                await Task.WhenAll(tasks);
                framesFinal = upOutput;   // 合帧使用超分后的帧
                // 1x超分:2x超分后缩回原始尺寸(画质比直接 1x 更好)
                if (upscaleShrink1x && origW is > 0 && origH is > 0)
                {
                    var shFiles = Directory.EnumerateFiles(framesFinal, "*.png")
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
                    int doneSh = 0;
                    var shTasks = new System.Collections.Generic.List<Task>();
                    progress?.Report((88, $"1x超分:将 {shFiles.Length} 帧从 2x 缩回原始尺寸 {origW}×{origH}..."));
                    foreach (var f in shFiles)
                    {
                        ct.ThrowIfCancellationRequested();
                        shTasks.Add(Task.Run(() =>
                        {
                            EngineService.ResizeImageTo(f, f, origW.Value, origH.Value);
                            int d = Interlocked.Increment(ref doneSh);
                            if (d % 20 == 0 || d == shFiles.Length)
                                progress?.Report((88 + (int)(2.0 * d / shFiles.Length),
                                    $"1x超分 缩回中 {d} 帧 / 共 {shFiles.Length} 帧"));
                        }, ct));
                    }
                    await Task.WhenAll(shTasks);
                }
                progress?.Report((90, $"帧超分完成({total} 帧)" + StageElapsed()));
            }

            // 4.5) 自定义输出分辨率:超分/补帧后批量缩放到精确 W×H(未超分时也生效,相当于统一尺寸)
            if (outWidth is > 0 && outHeight is > 0)
            {
                progress?.Report((92, $"缩放到自定义分辨率 {outWidth}×{outHeight}..."));
                var resFiles = Directory.EnumerateFiles(framesFinal, "*.png")
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
                int doneRes = 0;
                var resTasks = new System.Collections.Generic.List<Task>();
                foreach (var f in resFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    resTasks.Add(Task.Run(() =>
                    {
                        EngineService.ResizeImageTo(f, f, outWidth.Value, outHeight.Value);
                        int d = Interlocked.Increment(ref doneRes);
                        if (d % 20 == 0 || d == resFiles.Length)
                            progress?.Report((92 + (int)(6.0 * d / resFiles.Length),
                                $"缩放 已处理 {d} 帧 / 共 {resFiles.Length} 帧"));
                    }, ct));
                }
                await Task.WhenAll(resTasks);
            }

            // 5) 合帧 + 音频
            // 输出基准帧率:
            //   v4 模型(v4/v4.6,可精确补足) = 原帧率×倍率(B 方案),帧数=(原帧数-1)×倍率+1 → 时长=原、末帧=原末帧;
            //   v2 模型(只能 2 的幂级联,无法精确补足) = 去重后内容帧率(effectiveFps)×倍率,兜底不变速。
            // 去重只删重复画面(真实时间流逝不变),帧数按"原帧数"补足,故输出时长恒=原时长,不会时快时慢。
            bool v4Interp = frameInterp && IsV4Model(interpModel);
            // 输出帧率/帧数准则(关键,已 CLI 实测验证,2026-08-26):
            //   帧数 = (真实帧数-1)×倍率 + 1   (素材1: (38-1)×4+1 = 149)
            //   帧率 = 原帧率×倍率 = (真实帧数-1)×倍率 ÷ 真实时长   (素材1: 37×4/1.7751 = 83.376 = 20.844×4)
            // 视频末帧 PTS = (帧数-1)÷帧率 = 真实时长 = 原末帧 PTS(素材1: 148/83.376 = 1.7751s)✓
            // 反例(本 bug 根源):帧数=原帧数×倍率(152)配原帧率×倍率(83.376) → 末帧 1.8114(+2% 慢=变速感);
            // 旧折中 (152-1)/1.7751=85.066 时长虽对,但帧率≠原帧率×倍率,不满足 B 方案标签。
            long trueFrames = 0;
            double trueDur = 0;
            if (v4Interp && (trimStart == null && trimEnd == null))
            {
                var (tf, td) = await ProbeTrueFramesAndDuration(inputVideo, ct);
                if (tf > 0 && td > 0.01) { trueFrames = tf; trueDur = td; }
            }
            double baseFps;
            if (v4Interp && trueFrames > 0)
            {
                // A(内容/处理帧×倍率):用去重后的内容帧数; B(原帧率×倍率):用原始帧数。
                // 去重后内容帧很稀疏(如 14),内容×倍率能复刻"补帧.mp4 同款节奏";原×倍率帧多但须靠 setpts 重定时对齐。
                // 关键修复:只有"用户真正覆盖了输入帧率(与探测差>1%)"才用 frameCount 当基准;
                // 否则单视频自动填探测值 59.94 会被误判为覆盖 → 去重后 23 帧当基准 → 输出 13fps = 慢动作(用户实测)。
                bool userOverrideFpsNow = inFpsOverride is > 0 && probedFps > 0
                    && Math.Abs(inFpsOverride.Value - probedFps) / probedFps > 0.01;
                long fpsBase = fpsMode == 1 ? frameCount : (userOverrideFpsNow ? frameCount : trueFrames);
                baseFps = fpsBase > 1 ? (fpsBase - 1) * interpScale / trueDur : effectiveFps * interpScale;
            }
            else
                // 兜底帧率:真实时间轴档(B)= 原帧率×倍率;匀速档(A)= 内容帧率×倍率。
                // (内容帧率采样会把 effectiveFps 压到内容节奏;A 档必须用内容值,
                //  B 档绝不低于原帧率。探测失败时按档位各取所需。)
                baseFps = frameInterp
                    ? (fpsMode == 1 ? effectiveFps : Math.Max(effectiveFps, inFps)) * interpScale
                    : effectiveFps;
            // 节奏重采样:输出帧率由 tempo 路径精确给出(覆盖公式估算)
            if (tempoResample && tempoOutFps > 0) baseFps = tempoOutFps;
            double outFps = baseFps;
            // 视频滤镜链:后处理(锐化/清晰/…) → 果冻修复 → 运动模糊 → 去抖 → 可选 fps 重映射
            var preParts = new System.Collections.Generic.List<string>();
            var postParts = new System.Collections.Generic.List<string>();
            var postFilter = BuildPostFilter(postSharpen, postClarity, postUsm, postDetail, postDeblur,
                postFlicker, postDenoise, postAa, inv);
            // nlmeans 去重:视频降噪(主开关)与后处理去杂色同为 nlmeans,同时开启时去杂色跳过——
            // 两个 nlmeans 串行跑一倍耗时至多,画质无增益(降噪强度由主开关决定)。
            if (postFilter != null && videoDenoise >= 1 && postDenoise > 0)
            {
                AppLogger.Info($"去杂色跳过:视频降噪(强度 {videoDenoise})已含 nlmeans 降噪,后处理去杂色不再重复执行");
                postFilter = postFilter.Replace($"nlmeans={Math.Min(7.0, 1.0 + postDenoise / 25.0).ToString("0.#", inv)}:5:9,", "");
            }
            if (postFilter != null) preParts.Add(postFilter);
            // 视频降噪(空间+时间,去噪点/闪烁/压缩噪点),放最前:先降噪再锐化
            if (videoDenoise >= 1) preParts.Insert(0, VideoDenoiseFilter(videoDenoise));
            // 减少果冻:弱=dejudder;中/强=dejudder+时间平滑(tmix);运动模糊开启时果冻不再追加 tmix
            if (postJello >= 1) preParts.Add("dejudder");
            if (postJello >= 2 && postMotionBlur == 0) preParts.Add("tmix=frames=2");
            if (postJello >= 3 && postMotionBlur == 0) preParts.Add("tmix=frames=3");
            if (postDeshake) postParts.Add("deshake");                      // 画面去抖:轻量稳定
            if (frameInterp && targetFps is > 0)
            {
                // 目标帧率(用户指定):输出精确 = 指定值,时长不变。
                // 倍率只决定"插出多少中间帧"——帧数不够(当前倍率达不到)才调大倍率【凑帧数】,
                // 凑够后一律用 fps 滤镜精确重映射到目标帧率(绝不"输出高于用户指定")。
                double needScale = targetFps.Value / Math.Max(1, effectiveFps);
                if (v4Interp && (effectiveFps * interpScale) < targetFps.Value - 0.5 && needScale <= 16.5)
                {
                    // v4 + 帧数不够:倍率直接取"≥目标的最小整数"(RIFE -n 只要整数即可,不限 2/3/4/8/12/16 预设档)——
                    // 5x/6x/7x/9x 都能用,比"从预设档跳"更省(旧逻辑 需要5x 时跳到 8x,多算 60% 帧再丢)
                    int pickInt = Math.Max(2, (int)Math.Ceiling(needScale - 0.01));
                    if (pickInt > 16) pickInt = 16;   // 引擎- n 上限保护
                    progress?.Report((94,
                        $"⚠ 指定 {targetFps.Value:0.##} fps 需 {needScale:0.##}x 帧数,当前 {interpScale}x 不够——已临时按 {pickInt}x 补帧(输出仍精确 {targetFps.Value:0.##} fps)"));
                    AppLogger.Info($"指定 {targetFps.Value:0.##} fps:倍率 {interpScale}x → {pickInt}x(最小整数凑帧,输出帧率不变)");
                    interpScale = pickInt;
                }
                // 帧数已够(含上调后):精确重映射到目标帧率(时长不变),输出 = 用户指定值
                if ((v4Interp ? inFps : effectiveFps) * interpScale >= targetFps.Value - 0.5 || v4Interp)
                {
                    postParts.Add($"fps={targetFps.Value.ToString("0.##", inv)}");
                    outFps = targetFps.Value;
                }
                else
                {
                    // 兜底(16x 上限仍不够):按实际输出 + 明确提示,不重复帧凑数
                    var achievable = (v4Interp ? inFps : effectiveFps) * interpScale;
                    progress?.Report((94,
                        $"⚠ 指定输出 {targetFps.Value:0.##} fps 达不到(补帧后实际只有 {achievable:0.##} fps),已按 {achievable:0.##} fps 输出。"));
                    AppLogger.Info($"目标帧率达不到:指定 {targetFps.Value:0.##} fps,实际只能 {achievable:0.##} fps,按实际输出");
                }
            }
            // 运动模糊(1/2/3 档 → 2/3/5 子帧):用运动补偿插帧(minterpolate)沿运动方向做真实模糊,再局部应用
            var motionFrames = postMotionBlur switch { 1 => 2, 2 => 3, 3 => 5, _ => 0 };
            var baseFpsStr = baseFps.ToString("0.##", inv);
            var subFpsStr = (baseFps * motionFrames).ToString("0.##", inv);
            var preChain = string.Join(",", preParts);
            var postChain = string.Join(",", postParts);
            // ===== 时长保护(关键):不管补出多少帧,输出时长恒=原处理时长,速度恒对、不吞时间、不坏尾段 =====
            // 原理:用 setpts 把"实际输出帧"均匀铺满在"原时长"上(帧率=帧数/原时长,时长=原时长)。
            // 若直接用固定 -framerate=原帧率×倍率,一旦 RIFE 补出帧数 != 原帧数×倍率,时长就漂移
            // (速度对不上、尾部被截)——这正是"吞时间/少一段"的根源。
            string? vfrSetpts = null;
            double muxDur = 0;
            // 帧数精确对齐(v4 + 均匀输出):补/裁到"(真实原帧数-1)×倍率+1"——
            // 这样 时长 = (帧数-1) ÷ (原帧率×倍率) = 真实时长,末帧 PTS = 原末帧,不需要 -t 裁尾(裁尾会吞最后一帧)。
            if (v4Interp && targetFps == null && !vfrPassthrough && !tempoResample && trueFrames > 0)
            {
                long expBase = fpsMode == 1 ? frameCount
                    : (inFpsOverride is > 0 && probedFps > 0 && Math.Abs(inFpsOverride.Value - probedFps) / probedFps > 0.01
                        ? frameCount : trueFrames);
                long expected = (long)Math.Round((double)(expBase - 1) * interpScale) + 1;
                var seq = Directory.EnumerateFiles(framesFinal, "*.png")
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
                if (seq.Length > 0 && seq.Length != expected)
                {
                    if (seq.Length < expected)
                    {
                        var last = seq[seq.Length - 1];
                        for (long i = seq.Length; i < expected; i++)
                            File.Copy(last, Path.Combine(framesFinal, $"frame_{i + 1:D6}.png"), true);
                        AppLogger.Info($"帧数对齐:输出 {seq.Length} 帧 < 预期 {expected},末帧补足 {expected - seq.Length} 帧(防吞尾)");
                    }
                    else
                    {
                        for (long i = expected; i < seq.Length; i++)
                        {
                            try { File.Delete(Path.Combine(framesFinal, $"frame_{i + 1:D6}.png")); } catch { }
                        }
                    }
                }
            }
            // 方案 B(用户定):时长基准 = 源容器时长(ffprobe duration;含尾帧容积,与源容器分毫不差)。
            // 之前用"真实画面时长(帧数÷帧率)"会让输出比源"少 1 帧"→ 播放快一点点(用户实测);改回容器时长。
            {
                double durForMux = await ProbeDurationSeconds(inputVideo);
                muxDur = (trimEnd ?? durForMux) - (trimStart ?? 0);
                muxDur = muxDur > 0.01 ? muxDur : 1.0 / 30.0;
            }
            // ===== B 版(修正):时长=源容器 —— 多余时长给"最后一帧加长"(VFR 末帧 PTS 延到源容器时长),
            // 不复制"尾帧定格"(7 帧一样的观感差);播放器在末帧停留=与源尾帧容积一致,内容速度不变。 =====
            if (frameInterp && outFps > 0.01 && !vfrPassthrough && frameDurs == null)
            {
                var seqA = Directory.EnumerateFiles(framesFinal, "*.png")
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
                if (seqA.Count > 0)
                {
                    double curDur = (seqA.Count - 1) / outFps;
                    double gap = muxDur - curDur;
                    if (gap > 0.005)
                    {
                        // 末帧加长:时长表(下游 VFR setpts 自动采用)
                        finalDurs = new System.Collections.Generic.List<double>();
                        for (int i = 0; i < seqA.Count; i++) finalDurs.Add(1.0 / outFps);
                        finalDurs[^1] += gap;
                        AppLogger.Info($"尾帧容积:末帧延长 {gap * 1000:0}ms(帧数不变,总长 {muxDur:0.###}s)");
                    }
                    else if (gap < -0.005)
                    {
                        // 多出(超容器):裁多余帧(保留尾帧)
                        int dropN = (int)Math.Round(-gap * outFps);
                        for (int i = seqA.Count - 1; i >= Math.Max(1, seqA.Count - dropN); i--)
                        {
                            try { File.Delete(seqA[i]); } catch { }
                            if (i == 0) break;
                        }
                        AppLogger.Info($"帧数对齐(时长=源):裁尾 {dropN} 帧");
                    }
                }
                int finalN = Directory.EnumerateFiles(framesFinal, "*.png").Count();
                AppLogger.Info($"补帧诊断: 去重后 {frameCount} 帧,输出 {finalN} 帧,interpScale={interpScale},finalDurs={(finalDurs != null ? finalDurs.Count : -1)}");
            }
            // 只有"内容时间轴不均匀(真 VFR 素材 或 去重删过帧)+ 需要保护"才用 setpts 保留原始节奏(输出 VFR);
            // 普通 CFR 素材(未去重)一律均匀输出(原×倍率)——避免 setpts 精度问题引入抖动。
            if (targetFps == null && preserveRhythm)
            {
                int finalFileCount = Directory.EnumerateFiles(framesFinal, "*.png").Count();
                if (finalDurs == null || finalDurs.Count == 0)
                {
                    finalDurs = new System.Collections.Generic.List<double>();
                    for (int i = 0; i < finalFileCount; i++) finalDurs.Add(muxDur / Math.Max(1, finalFileCount));
                }
                else
                {
                    AlignDurationsToCount(finalDurs, finalFileCount);
                }
                double sumDur = 0;
                foreach (var d in finalDurs) sumDur += d;
                if (sumDur > 0 && Math.Abs(sumDur - muxDur) > 0.0005)
                {
                    double k = muxDur / sumDur;
                    for (int i = 0; i < finalDurs.Count; i++) finalDurs[i] *= k;
                }
                vfrSetpts = BuildVfrSetptsExpr(finalDurs);
                if (vfrSetpts == null)
                {
                    finalDurs = new System.Collections.Generic.List<double>();
                    for (int i = 0; i < finalFileCount; i++) finalDurs.Add(muxDur / Math.Max(1, finalFileCount));
                    vfrSetpts = BuildVfrSetptsExpr(finalDurs);
                }
                AppLogger.Info($"时长保护(VFR): 帧={finalFileCount}, muxDur={muxDur:0.###}, 总时长={finalDurs.Sum():0.###}, vfrSetpts={(vfrSetpts != null ? "有" : "无")}");
                if (vfrSetpts != null)
                    progress?.Report((96, $"混合编码({outFps.ToString("0.##", inv)} fps,可变帧率时间轴 {muxDur:0.###}s)..."));
            }
            else
            {
                AppLogger.Info($"时长保护(均匀): muxDur={muxDur:0.###}, baseFps={baseFps:0.##}");
            }
            string vfArg, videoMap;
            if (postMotionBlur >= 1)
            {
                // 局部真实运动模糊:先运动补偿插帧到 N 倍帧率,再平均 N 子帧回原帧率(沿运动方向拖尾),
                // 用帧间差异做运动掩码,只在运动区应用,静止区保持清晰。
                // 注意:minterpolate 输出 gbrp,链中必须转 yuv420p,否则编码器输出黑白!
                var graph = $"[0:v]" +
                    (preChain.Length > 0 ? preChain + "," : "") +
                    $"split=3[o1][o2][o3];" +
                    $"[o1]minterpolate=fps={subFpsStr}:mi_mode=mci:mc_mode=aobmc:me_mode=bidir:vsbmc=1,tmix=frames={motionFrames},fps={baseFpsStr},format=yuv420p[b];" +
                    $"[o2]tblend=all_mode=difference,format=gray,dilation=threshold0=0[m];" +
                    $"[o3]format=yuv420p[o3f];" +
                    $"[o3f][b][m]maskedmerge" +
                    (postChain.Length > 0 ? $",{postChain}" : "") +
                    $",format=yuv420p{(vfrSetpts != null ? "," + vfrSetpts : "")}[vout]";
                vfArg = $" -filter_complex \"{graph}\"";
                videoMap = "-map \"[vout]\"";
            }
            else
            {
                var allParts = new System.Collections.Generic.List<string>(preParts);
                allParts.AddRange(postParts);
                // 链尾强制 yuv420p:部分滤镜(如 minterpolate 输出 gbrp)不转格式会让编码器输出黑白;
                // setpts 改时间轴放在滤镜链最后(后处理=时间维度滤镜需要先于重映射的正确 PTS)
                vfArg = allParts.Count > 0
                    ? $" -vf \"{string.Join(",", allParts)},format=yuv420p{(vfrSetpts != null ? "," + vfrSetpts : "")}\""
                    : (vfrSetpts != null ? $" -vf \"format=yuv420p,{vfrSetpts}\"" : "");
                videoMap = "-map 0:v:0";
            }
            // 卡顿预防提示:内容帧率低(如 12fps)时,低倍率输出仍会卡,建议提高倍率。
            // v4(原×倍率)输出较足,不提示;v2(内容×倍率)偏低时提示。
            if (frameInterp && !v4Interp && effectiveFps * interpScale < 30)
            {
                var suggest = Math.Ceiling(30.0 / Math.Max(1, effectiveFps));
                if (suggest != interpScale && suggest <= 8)
                    progress?.Report((96, $"内容帧率仅 {effectiveFps:0.##} fps,当前输出 {outFps:0.##} fps 可能仍卡,建议补帧 {suggest:0}x"));
            }
            progress?.Report((96, $"ffmpeg 合成视频({outFps.ToString("0.##", inv)} fps)..."));
            var framePattern = Path.Combine(framesFinal, "frame_%06d.png");
            // 6 位小数:83.376 这类非整数帧率用 0.## 会被量化成 83.38,长视频会累积微小漂移(10 分钟约 9ms)
            var fr = baseFps.ToString("0.######", inv);
            // 时长表输出(VFR):setpts 已在滤镜链构造处接入(精确重映射时间轴,精度=输出时基)。
            // 注:曾用 concat demuxer + duration,实测其内部 image2 时基固定 25fps,0.0333/0.1 被
            // 量化成 0.04/0.08/0.12(30fps 素材半段快 20%),故改为 setpts。
            var muxInput = $"-framerate {fr} -i \"{framePattern}\"";
            await EnsureHwProbeAsync(ffmpeg, ct);
            var encoder = PickVideoEncoder(gpuId, codecPref);
            progress?.Report((96, $"压缩编码器:{LastVideoEncoderInfo}"));
            // 自定义码率模式:用户指定 Mbps(0 = 用质量档 CRF);码率也随回退保持(CPU 软编同样适用)
            double bitrateKbps = customBitrateMbps > 0 ? customBitrateMbps * 1000 : 0;
            var encArgs = EncoderArgs(encoder, quality, bitrateKbps);
            // MP4 加 faststart 便于流式播放;MKV 不需要
            var fastFlag = outputVideo.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                ? " -movflags +faststart" : "";
            // 音频(恢复原逻辑):MP4 容器不支持 vorbis/opus/flac 等编码,自动转 aac;MKV 原样拷贝;
            // 静音=不映射音轨(无音轨输出)
            var audioArgs = "-c:a copy";
            if (!mute && outputVideo.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                var acodec = await ProbeAudioCodec(inputVideo);
                if (acodec.Length > 0 && acodec is not ("aac" or "mp3" or "ac3" or "eac3"))
                    audioArgs = "-c:a aac";
            }
            // 先写临时文件,合帧真正完成并校验通过后再原子改名成最终文件名:
            // 避免"还在合帧时输出目录就出现半成品文件",用户误以为处理完去打开,结果打不开/损坏
            // 注意:临时名要保留真实扩展名(.tmp 插在扩展名之前,如 xxx.tmp.mp4),
            // 否则 ffmpeg 无法按扩展名选择封装格式(mp4/mkv),报 "Unable to choose an output format"
            var outTmp = Path.Combine(Path.GetDirectoryName(outputVideo)!,
                Path.GetFileNameWithoutExtension(outputVideo) + $".tmp{Guid.NewGuid():N}" + Path.GetExtension(outputVideo));
            // 静音:只用视频流(-an);否则映射音频
            // 不用 -shortest/-t(会截视频尾帧)。音频可能比画面长(容器含尾帧容积,如素材1 音频1.82 vs 画面1.7751),
            // 把音频裁剪到画面时长(atrim,只裁不补),避免 MP4 duration 被音频顶长导致"结尾停帧"。
            var audioPart = mute ? "" : $" -map 1:a:0? {audioArgs}";
            if (!mute && muxDur > 0.01)
                audioPart += $" -af \"atrim=duration={muxDur.ToString("0.######", inv)},asetpts=N/SR/TB\" -c:a aac";
            var muxArgs = $"{videoMap}{audioPart} {encArgs} {vfArg}{fastFlag} \"{outTmp}\"";
            var muxBase = $"-y {muxInput} {trimArgs} -i \"{inputVideo}\" ";
            // 编码阶段整体进度 96→100 随 ffmpeg 编码帧数推进(否则卡 96%,结尾预计时间虚高失真)
            int encTotal = Math.Max(1, Directory.EnumerateFiles(framesFinal, "*.png").Count());
            if (pauseWait != null) await pauseWait();   // 暂停:编码开始前停(已生成的帧不浪费)
            try
            {
                try
                {
                    // 已知会失败的硬件编码器直接跳过,走 CPU(避免每次先白跑一次)
                    if (BrokenHwEncoders.Contains(encoder))
                        throw new InvalidOperationException("hw-encoder-known-broken");
                    await RunAsync(ffmpeg, muxBase + muxArgs, progress, ct, "编码", encTotal);
                    // 硬件编码可能留下 0 字节/损坏文件却退出 0,这里校验;无效则触发回退
                    if (!await ValidateVideoFileAsync(outTmp))
                        throw new InvalidOperationException("硬件编码输出文件无效");
                }
                catch (Exception ex) when (encoder != "libx264" && encoder != "libx265")
                {
                    // 硬件编码失败(驱动/不支持)或输出损坏时回退轻量 CPU 编码(限线程,不跑满 CPU);
                    // 用户选 H.265 时回退到 libx265,否则回退 libx264
                    BrokenHwEncoders.Add(encoder);
                    var cpuEncoder = encoder.StartsWith("hevc", StringComparison.OrdinalIgnoreCase) ? "libx265" : "libx264";
                    AppLogger.Info($"降级:硬件编码({encoder})不可用(原因:{ex.Message}),改用 CPU 编码({cpuEncoder})");
                    progress?.Report((96, $"⚠ 硬件编码({encoder})不可用,改用轻量 CPU 编码({cpuEncoder});原因:{ex.Message}"));
                    AppLogger.Info($"⚠ 硬件编码({encoder})不可用,改用轻量 CPU 编码({cpuEncoder});原因:{ex.Message}");
                    await RunAsync(ffmpeg,
                        muxBase + $"{videoMap}{audioPart} {EncoderArgs(cpuEncoder, quality, bitrateKbps)} {vfArg}{fastFlag} \"{outTmp}\"",
                        progress, ct, "编码", encTotal);
                }
                if (!await ValidateVideoFileAsync(outTmp))
                    throw new InvalidOperationException("视频合成失败:输出文件无效(无法被解码)");
                // 校验通过,才以最终文件名出现在输出目录(合帧期间输出目录只有 .tmp,不会误以为完成)
                File.Move(outTmp, outputVideo, true);
            }
            catch (IOException ex) when (File.Exists(outputVideo))
            {
                // 目标文件被占用(用户在播放/打开同名文件):保留已编码好的 tmp,提示而不是删成品
                AppLogger.Info($"输出被占用:{Path.GetFileName(outputVideo)}({ex.Message}),已编码文件保留为 {Path.GetFileName(outTmp)}");
                throw new InvalidOperationException(
                    $"输出文件被占用,无法覆盖:{Path.GetFileName(outputVideo)} — 请关闭正在播放/预览该文件的程序后重试(已编码结果临时保留为 {Path.GetFileName(outTmp)})");
            }
            catch
            {
                try { if (File.Exists(outTmp)) File.Delete(outTmp); } catch { }
                throw;
            }
            // ===== 输出校验:帧率/时长与预期对比,偏差大告警(找出封装/编码异常) =====
            try
            {
                double durOut = await ProbeDurationSeconds(outputVideo);
                double fpsOut = 30;
                if (double.TryParse(ProbeFps(outputVideo), System.Globalization.NumberStyles.Float, inv, out var fo) && fo > 0)
                    fpsOut = fo;
                string warn = "";
                // 允差 = max(3%, 1 拍):尾帧保留/拍型取整可能差 1 拍,小素材上显示 5% 是正常的,不可算 bug
                double oneBeat = Math.Max(0.01, 1.0 / Math.Max(1, outFps));
                double durTol = Math.Max(0.03, oneBeat / Math.Max(0.01, muxDur));
                if (Math.Abs(durOut - muxDur) / Math.Max(0.01, muxDur) > durTol)
                    warn += $"时长 {durOut:0.###}s vs 预期 {muxDur:0.###}s(偏差 {(durOut - muxDur) / muxDur * 100:0.#}%);";
                if (Math.Abs(fpsOut - outFps) / Math.Max(0.01, outFps) > 0.03)
                    warn += $"帧率 {fpsOut:0.##}vs 预期 {outFps:0.##};";
                AppLogger.Info($"输出校验:{Path.GetFileName(outputVideo)} 帧率 {fpsOut:0.##}fps,时长 {durOut:0.###}s" +
                    (warn.Length > 0 ? " ⚠ " + warn : " ✓"));
                if (warn.Length > 0)
                    progress?.Report((100, $"完成 ⚠ 输出校验:{warn}"));
            }
            catch { /* 校验失败不影响完成 */ }
            progress?.Report((100, "完成" + StageElapsed()));

            // 6) 清理临时帧
            try { Directory.Delete(workDir, true); } catch { }
            return outputVideo;
        }
        finally
        {
            try { Directory.Delete(workDir, true); } catch { /* 清理失败忽略 */ }
        }
    }

    /// <summary>
    /// 构建视频后处理滤镜链(参数 0-100,0=关):
    /// 锐化=unsharp 5x5 小核强量;清晰=unsharp 9x9 大核低量(局部对比度);
    /// 钝化蒙版=smartblur 负强度+阈值(经典 USM,阈值保护平坦区);
    /// 保留细节=cas 自适应对比锐化(只锐化边缘,不放大噪点);
    /// 去模糊=smartblur 大半径负强度(反锐化掩膜近似去卷积);
    /// 去频闪=deflicker 时间中值(亮度忽明忽暗);去杂色=nlmeans 空间降噪(旧 hqdn3d 实测几乎无效);
    /// 边缘抗锯齿=sab 自适应模糊(只在局部对比度强处磨边)。
    /// </summary>
    private static string? BuildPostFilter(int sharpen, int clarity, int usm, int detail, int deblur,
        int flicker, int postDenoise, int aa, System.Globalization.CultureInfo inv)
    {
        var parts = new System.Collections.Generic.List<string>();
        if (sharpen > 0)
            parts.Add($"unsharp=5:5:{Math.Min(2.0, sharpen / 100.0 * 1.5).ToString("0.00", inv)}:5:5:0");
        if (clarity > 0)
            parts.Add($"unsharp=9:9:{Math.Min(2.0, clarity / 100.0 * 0.8).ToString("0.00", inv)}:9:9:0");
        if (usm > 0)
            parts.Add($"smartblur=luma_radius=2:luma_strength=-{Math.Min(1.0, usm / 100.0).ToString("0.00", inv)}:luma_threshold=8");
        if (detail > 0)
            parts.Add($"cas=strength={Math.Min(1.0, detail / 100.0).ToString("0.00", inv)}");
        if (deblur > 0)
            parts.Add($"smartblur=luma_radius=3:luma_strength=-{Math.Min(0.8, deblur / 100.0 * 0.8).ToString("0.00", inv)}:luma_threshold=2");
        if (flicker > 0)
            parts.Add($"deflicker=size=5:mode=median");   // 亮度闪烁:时间域中值,去除忽明忽暗
        if (postDenoise > 0)
            parts.Add($"nlmeans={Math.Min(7.0, 1.0 + postDenoise / 25.0).ToString("0.#", inv)}:5:9");   // 空间去杂色(nlmeans 有效)
        if (aa > 0)
            parts.Add($"sab=lr=1:ls={Math.Max(0.5, aa / 100.0 * 3).ToString("0.##", inv)}");   // 自适应模糊:磨超分/放大后的边缘锯齿
        return parts.Count > 0 ? string.Join(",", parts) : null;
    }

    /// <summary>视频降噪滤镜(ffmpeg nlmeans 非局部均值,比 hqdn3d 强得多):
    /// 实测 hqdn3d(原实现,参数 5~12 档)对压缩/随机噪点几乎无效果(标准差仅降 0.2%),
    /// nlmeans 同条件降噪 53%(14.4→6.8)——故改为 nlmeans,效果真实可见。
    /// 参数 = sigma空间:radius:patch_size:sigma时间,越大越强;档位按强度递增。</summary>
    private static string VideoDenoiseFilter(int strength)
    {
        return strength switch
        {
            1 => "nlmeans=3:3:7:3",      // 弱(明显去噪,细节轻微损失)
            2 => "nlmeans=5:5:9:5",      // 中
            3 => "nlmeans=7:7:11:7",     // 强(去噪最狠,可能略糊)
            _ => "nlmeans=5:5:9:5",
        };
    }

    /// <summary>
    /// freezedetect 检测冻结(静止)段:返回 (开始秒, 结束秒) 列表。
    /// 专业冻结检测:连续帧亮度差低于噪声阈值且持续超过 0.1s 视为静止段。
    /// </summary>
    private static async Task<List<(double s, double e)>> DetectFreezeAsync(string ffmpeg, string input,
        string trimArgs, double noise, CancellationToken ct)
    {
        var segs = new List<(double, double)>();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            var lines = await RunCaptureAsync(ffmpeg,
                $"-y {trimArgs} -i \"{input}\" -vf \"freezedetect=n={noise.ToString("0.###", inv)}:d=0.04,metadata=print\" -f rawvideo NUL",
                ct);
            double curStart = -1;
            foreach (var l in lines)
            {
                var ms = System.Text.RegularExpressions.Regex.Match(l, @"freeze_start=([\d.]+)");
                if (ms.Success && double.TryParse(ms.Groups[1].Value, System.Globalization.NumberStyles.Float, inv, out var s))
                    curStart = s;
                var me = System.Text.RegularExpressions.Regex.Match(l, @"freeze_end=([\d.]+)");
                if (me.Success && curStart >= 0
                    && double.TryParse(me.Groups[1].Value, System.Globalization.NumberStyles.Float, inv, out var e))
                {
                    if (e > curStart) segs.Add((curStart, e));
                    curStart = -1;
                }
            }
        }
        catch { /* 检测失败按无静止段处理 */ }
        return segs;
    }

    /// <summary>CPU 重算预计时长(分钟)估算:补帧 CPU 软解约 2~6 秒/帧(随分辨率),给个上界让用户有"可等"预期。
    /// 避免降级后进度条久不动,用户以为卡死。</summary>
    private static string EstimateCpuTime(int segFrames, int watchTotal)
    {
        try
        {
            // 按常用 1080p 估算:CPU 补帧 1 帧约 3~5 秒;帧数按"整段剩余"最坏估计
            var per = 30 + Math.Min(150, watchTotal / 20);   // 30~150 秒/帧区间的粗估(取较大值=更保守)
            double minutes = Math.Max(1, segFrames /*实际当前段*/ * per / 60.0);
            return minutes > 120 ? "2 小时以上" : $"约 {minutes:0} 分钟";
        }
        catch { return "较长时间"; }
    }

    /// <summary>对 [start, end) 帧区间跑一次 RIFE,输出合并到 framesFinal(帧号全局递增)。返回新的全局帧号。
    /// frameScale = 原帧数/去重后帧数(补帧按原素材帧率补足,去重不降低输出帧率/缩短时长)。
    /// globalTarget &gt; 0 时(末段):-n = 全局目标帧数 - 已输出帧数,保证最后锚点帧精确落在最后一帧。
    /// appendTailCopy = true(末段,非 VFR):给 RIFE 追加末帧副本,让最后一段真实插值,
    /// 避免 RIFE -n 把末帧复制成 3 帧(尾部"卡住");副本产生的冻结帧由合帧对齐裁掉。</summary>
    private static async Task<int> InterpSegmentAsync(string rife, string framesOut, string framesFinal,
        int start, int end, int interpScale, string interpModel, double? timeStep, bool tta, int gpuId, int globalIdx,
        IProgress<(int pct, string msg)>? progress, CancellationToken ct, double frameScale = 1.0, long globalTarget = 0,
        bool appendTailCopy = false)
    {
        int segLen = end - start;
        var workDir = Path.GetDirectoryName(framesFinal)!;
        var segIn = Path.Combine(workDir, $"seg_{start}_{end}_in");
        Directory.CreateDirectory(segIn);
        for (int i = start; i < end; i++)
            File.Copy(Path.Combine(framesOut, $"frame_{i + 1:D6}.png"), Path.Combine(segIn, $"frame_{i - start + 1:D6}.png"), true);

        // 模型目录前置校验:缺失立即明确报错(而不是等下半天引擎报 stderr 尾部的晦涩错误)
        var rifeDir = Path.GetDirectoryName(rife) ?? ".";
        if (!Directory.Exists(Path.Combine(rifeDir, interpModel)))
            throw new InvalidOperationException($"未找到补帧模型目录:{Path.Combine(rifeDir, interpModel)} — 请检查 engines/rife 下的模型文件夹(如 rife-v4.13)");

        // GPU 失败自动降级:当前 GPU → 其他 GPU(多卡机:核显失败切独显)→ CPU(与超分同策略)
        async Task RunRifeAsync(string args, int gpuNow, int watchTotal, string? watchDir)
        {
            // 尝试一张 GPU;失败/黑帧时传入 alt 走"换卡,再不行 CPU"链
            async Task TryGpuAsync(int g, int? altGpu)
            {
                try
                {
                    var gArgs = System.Text.RegularExpressions.Regex.Replace(args, @"-g\s+-?\d+", $"-g {g}");
                    await RunAsync(rife, gArgs, progress, ct, "补帧", watchTotal, watchDir).ConfigureAwait(false);
                    // 黑帧防御:GPU 输出全黑(vkQueueSubmit 失败但退出码 0)→ 换卡/CPU 重跑该段
                    if (g >= 0 && watchDir != null && Directory.Exists(watchDir))
                    {
                        bool anyBlack = false;
                        foreach (var f in Directory.EnumerateFiles(watchDir, "*.png").Take(4))
                        {
                            try { if (EngineService.IsBlackPng(f)) { anyBlack = true; break; } } catch { }
                        }
                        // 防误杀:段【源帧】(segIn)本来就近黑(素材黑场/淡入淡出)→ 输出黑正常,不降级
                        if (anyBlack && !DirNearBlack(segIn))
                        {
                            AppLogger.Info($"⚠ 降级:补帧 GPU {g} 输出黑帧(GPU 队列异常),{(altGpu.HasValue ? $"改用 GPU {altGpu.Value}" : "改用 CPU")}重算该段");
                            progress?.Report((0, $"⚠ 补帧 GPU {g} 输出黑帧,{(altGpu.HasValue ? $"改用 GPU {altGpu.Value}" : "改用 CPU 重算该段,约 {EstimateCpuTime(segLen, watchTotal)} 分钟...")}"));
                            if (altGpu.HasValue)
                                await TryGpuAsync(altGpu.Value, null).ConfigureAwait(false);   // 只再降一级:换卡后失败直接 CPU
                            else
                            {
                                var cpuArgs = System.Text.RegularExpressions.Regex.Replace(args, @"-g\s+-?\d+", "-g -1");
                                await RunAsync(rife, cpuArgs, progress, ct, "补帧", watchTotal, watchDir).ConfigureAwait(false);
                            }
                        }
                    }
                }
                catch (InvalidOperationException ex) when (g >= 0)
                {
                    AppLogger.Info($"⚠ 降级:补帧 GPU {g} 失败({ex.Message.Split('\n')[0]}),{(altGpu.HasValue ? $"改用 GPU {altGpu.Value}" : "改用 CPU")}重算");
                    progress?.Report((0, $"⚠ 补帧 GPU {g} 失败,{(altGpu.HasValue ? $"改用 GPU {altGpu.Value}" : "改用 CPU")}重算..."));
                    if (altGpu.HasValue)
                        await TryGpuAsync(altGpu.Value, null).ConfigureAwait(false);
                    else
                    {
                        var cpuArgs = System.Text.RegularExpressions.Regex.Replace(args, @"-g\s+-?\d+", "-g -1");
                        await RunAsync(rife, cpuArgs, progress, ct, "补帧", watchTotal, watchDir).ConfigureAwait(false);
                    }
                }
            }

            if (gpuNow >= 0)
            {
                // ① 当前用户选的 GPU;② 其他 GPU(VulkanCheck 枚举到的另一张,如核显失败切独显);③ CPU
                int? alt = null;
                try
                {
                    var devs = VulkanCheck.Devices;
                    if (devs.Count >= 2)
                        alt = devs.FirstOrDefault(d => d.Id != gpuNow).Id;
                }
                catch { }
                if (alt.HasValue)
                    await TryGpuAsync(gpuNow, alt).ConfigureAwait(false);
                else
                    await TryGpuAsync(gpuNow, null).ConfigureAwait(false);   // 单卡:失败黑帧直接 CPU
            }
            else
            {
                await RunAsync(rife, args, progress, ct, "补帧", watchTotal, watchDir).ConfigureAwait(false);
            }
        }

        // TTA 开关(所有模型可用);时间步仅 v4 架构模型支持
        var ttaArgs = tta ? " -x -z" : "";
        var gpuArg = gpuId >= 0 ? gpuId : -1;   // ncnn:-1 = CPU
        string finalOut;
        if (segLen >= 2)
        {
            if (IsV4Model(interpModel))
            {
                // v4 架构:支持 -n 自定义目标帧数 + -s 时间步。
                // B 方案(原帧率×倍率):全局目标 = (原帧数-1)×倍率+1(末段由主流程传入 globalTarget 补足),
                // 保证 Σ各段 = 全局目标、最后锚点帧落在最后一帧,输出帧率=原帧率×倍率、时长=原、不变速。
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var ts = timeStep is > 0 and <= 1 ? timeStep.Value.ToString("0.##", inv) : "0.5";
                int targetFrames;
                // 关键修复(源码验证):rife -n 是【整个序列的总目标帧数】,目录模式下 -s 被忽略、时间步按帧索引均分;
                // -n 必须是输入帧数的【整数倍】,否则帧间距不均匀 → "全程轻微漏帧/judder"(用户实测症状)。
                // 故用"整数倍率"取整:mult = round(interpScale × frameScale),-n = 本段输入帧数 × mult(保证可被整除)。
                int mult = Math.Max(1, (int)Math.Round(interpScale * frameScale));
                targetFrames = Math.Max(segLen + 1, segLen * mult);
                if (appendTailCopy)
                {
                    // 尾部插值修正:追加末帧副本(锚点 +1,目标帧数 +倍率),
                    // 让最后一段(如源 37→38)得到真实插值,而不是被 RIFE 复制成末帧冻结;
                    // 副本段产生的冻结帧会在合帧"帧数对齐"时被裁掉。
                    File.Copy(Path.Combine(segIn, $"frame_{segLen:D6}.png"),
                              Path.Combine(segIn, $"frame_{segLen + 1:D6}.png"), true);
                    targetFrames += interpScale;
                }
                finalOut = Path.Combine(workDir, $"seg_{start}_{end}_out");
                Directory.CreateDirectory(finalOut);
                await RunRifeAsync(
                    $"-i \"{segIn}\" -o \"{finalOut}\" -n {targetFrames} -f \"frame_%06d.png\" -m {interpModel} -g {gpuArg}{ttaArgs}{SafeRender.GetEngineThreadArgs()}",
                    gpuId, targetFrames, finalOut);
            }
            else
            {
                // v2 架构模型(anime/HD/UHD/v2.3)不支持 -n(默认 2x),4x/8x 用级联多次 2x
                finalOut = segIn;
                int m = interpScale;
                int pass = 0;
                int inLen = segLen;
                while (m > 1)
                {
                    m /= 2;
                    int outLen = inLen * 2;   // 本轮 2x 后的目标帧数
                    var curOut = Path.Combine(workDir, $"seg_{start}_{end}_p{pass++}");
                    Directory.CreateDirectory(curOut);
                    await RunRifeAsync(
                        $"-i \"{finalOut}\" -o \"{curOut}\" -f \"frame_%06d.png\" -m {interpModel} -g {gpuArg}{ttaArgs}{SafeRender.GetEngineThreadArgs()}",
                        gpuId, outLen, curOut);
                    finalOut = curOut;
                    inLen = outLen;
                }
            }
        }
        else
        {
            // 单帧段:直接复制,不插值
            finalOut = Path.Combine(workDir, $"seg_{start}_{end}_out");
            Directory.CreateDirectory(finalOut);
            File.Copy(Path.Combine(segIn, "frame_000001.png"), Path.Combine(finalOut, "frame_000001.png"), true);
        }

        var files = Directory.EnumerateFiles(finalOut, "*.png")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
            File.Copy(f, Path.Combine(framesFinal, $"frame_{globalIdx++:D6}.png"), true);
        try { Directory.Delete(segIn, true); } catch { }
        if (finalOut != segIn) { try { Directory.Delete(finalOut, true); } catch { } }
        return globalIdx;
    }

    /// <summary>
    /// 历史遗留:早期"方案 C"按关键帧间隙逐段插值的实现。已废弃。
    /// 现在方案 C(对齐丝滑)与 B 同源,统一走「密度还原 → 整段一次 RIFE → 帧数精确对齐」的批处理路径
    /// (见主流程 else 分支),整段上下文让 RIFE 光流更稳、不糊不扭,不再按间隙逐段、也不再需要此方法。
    /// 保留仅供回溯;请勿在任何新调用路径里使用。
    /// </summary>
    [Obsolete("方案 C 已收编为整段一次 RIFE + setpts 重定时,不再按关键帧间歇逐段插值;此方法仅供历史回溯,勿用于新调用。")]
    private static async Task InterpKeyframeGapsAsync(string rife, string keyframesDir, string framesFinal,
        double[] stateDurs, double outFps, string interpModel, double? timeStep, bool tta, int gpuId,
        System.Collections.Generic.List<int> cuts, IProgress<(int pct, string msg)>? progress, CancellationToken ct,
        Func<Task>? pauseWait)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var keys = Directory.EnumerateFiles(keyframesDir, "*.png")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        int n = keys.Length;
        var workDir = Path.GetDirectoryName(framesFinal)!;
        var ts = timeStep is > 0 and <= 1 ? timeStep.Value.ToString("0.##", inv) : "0.5";
        var gpuArg = gpuId >= 0 ? gpuId : -1;   // ncnn:-1 = CPU
        var ttaArgs = tta ? " -x -z" : "";
        int gOut = 1;
        double outFpsSafe = outFps > 0.01 ? outFps : 60;

        // 全局均匀网格:累积取整,误差不累积。n_k = round(P_k × F_out),P_0=0,P_{k+1}=P_k+stateDurs[k]。
        var grid = new long[n + 1];
        double cum = 0; grid[0] = 0;
        for (int j = 0; j < n; j++) { cum += Math.Max(0.0001, stateDurs[j]); grid[j + 1] = (long)Math.Round(cum * outFpsSafe); }

        // 段边界(关键帧索引空间):cuts 已在关键帧索引空间(主流程 scene 检测后按 frameCount 裁剪)。
        var bounds = new System.Collections.Generic.List<(int s, int e)>();
        int segStart = 0;
        foreach (var c in cuts) { if (c > segStart && c < n) { bounds.Add((segStart, c)); segStart = c; } }
        if (segStart < n) bounds.Add((segStart, n));
        if (bounds.Count == 0) bounds.Add((0, n));

        int totalGaps = 0;
        foreach (var (s, e) in bounds) totalGaps += Math.Max(0, e - s - 1);
        if (totalGaps == 0) totalGaps = 1;
        long totalFrames = Math.Max(1, grid[n] + 1);   // 最终总帧数(显示用,先算好)

        foreach (var (s, e) in bounds)
        {
            if (ct.IsCancellationRequested) break;
            for (int j = s; j < e - 1; j++)
            {
                ct.ThrowIfCancellationRequested();
                // 该动作段插值帧数 = 同一条网格上相邻边界的差(累积,误差不累积)。
                int targetFrames = (int)Math.Max(2, grid[j + 1] - grid[j]);
                int framesBefore = gOut - 1;   // 本段开始前已生成的帧数(全局累计)
                // ===== 上下文窗口:不只喂 (k,k+1),而是喂 [k-1, k, k+1, k+2] 让 RIFE 光流有上下文估运动(更干净/不糊)=====
                var gapDir = Path.Combine(workDir, $"gap_{j}_in");
                Directory.CreateDirectory(gapDir);
                var winIdx = new System.Collections.Generic.List<int>();
                for (int w = j - 1; w <= j + 2; w++)
                {
                    int wi = Math.Clamp(w, 0, n - 1);
                    if (!winIdx.Contains(wi)) winIdx.Add(wi);
                }
                for (int w = 0; w < winIdx.Count; w++)
                    File.Copy(keys[winIdx[w]], Path.Combine(gapDir, $"frame_{w + 1:D6}.png"), true);
                var gapOut = Path.Combine(workDir, $"gap_{j}_out");
                Directory.CreateDirectory(gapOut);
                // 包装进度:把本段内部帧数(1..targetFrames)映射到全局累计,显示"总帧慢慢加上去"(而不是"X/Y 动作段")
                IProgress<(int pct, string msg)>? gapProg = null;
                if (progress != null)
                {
                    int tf = targetFrames, fb = framesBefore;
                    var global = progress;
                    gapProg = new System.Progress<(int pct, string msg)>(t =>
                    {
                        int local = 0;
                        var m = System.Text.RegularExpressions.Regex.Match(t.msg, @"第\s*(\d+)\s*帧");
                        if (m.Success) local = int.Parse(m.Groups[1].Value);
                        else local = (int)(t.pct / 100.0 * tf);
                        int gf = (int)Math.Min(totalFrames, fb + Math.Max(0, local));
                        global.Report((10 + (int)(35.0 * gf / totalFrames), $"补帧 第 {gf} 帧 / 共 {totalFrames} 帧"));
                    });
                }
                // -n:给足帧数(整个窗口各子段 ~targetFrames 帧),再由 SSIM 锚定 k↔k+1 子段
                int outTarget = Math.Max(2, winIdx.Count * targetFrames + 1);
                await RunAsync(rife,
                    $"-i \"{gapDir}\" -o \"{gapOut}\" -n {outTarget} -s {ts} -f \"frame_%06d.png\" -m {interpModel} -g {gpuArg}{ttaArgs}{SafeRender.GetEngineThreadArgs()}",
                    gapProg, ct, "补帧", outTarget, gapOut);
                var fs = Directory.EnumerateFiles(gapOut, "*.png")
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
                // SSIM 锚定:找输出里最接近 k(keys[j])与 k+1(keys[j+1])的帧位置,取 k→k+1 子段(其余是上下文,不输出)
                int posK = 0, posK1 = Math.Min(fs.Length - 1, targetFrames * 2);
                try
                {
                    var kGray = SampleGray(keys[j], 4, out var sw, out var sh);
                    var k1Gray = SampleGray(keys[j + 1], 4, out _, out _);
                    double bestK = -1, bestK1 = -1;
                    for (int w = 0; w < fs.Length; w++)
                    {
                        var g = SampleGray(fs[w], 4, out _, out _);
                        // 前 2/3 找 k,后一半找 k+1(防同帧)
                        if (w < fs.Length * 2 / 3)
                        {
                            double s1 = BlockSsim(kGray, g, sw, sh);
                            if (s1 > bestK) { bestK = s1; posK = w; }
                        }
                        if (w >= fs.Length / 3)
                        {
                            double s2 = BlockSsim(k1Gray, g, sw, sh);
                            if (s2 > bestK1) { bestK1 = s2; posK1 = w; }
                        }
                    }
                }
                catch { }
                if (posK1 <= posK) posK1 = Math.Min(fs.Length - 1, posK + targetFrames);   // 兜底
                int take = Math.Min(targetFrames, Math.Max(1, posK1 - posK));
                for (int m = posK; m < Math.Min(fs.Length, posK + take); m++)
                    File.Copy(fs[m], Path.Combine(framesFinal, $"frame_{gOut++:D6}.png"), true);
                try { Directory.Delete(gapDir, true); } catch { }
                try { Directory.Delete(gapOut, true); } catch { }
                if (gOut - 1 >= framesBefore + (targetFrames - 1))
                    progress?.Report((10 + (int)(35.0 * (gOut - 1) / totalFrames), $"补帧 第 {gOut - 1} 帧 / 共 {totalFrames} 帧"));
                if (pauseWait != null) await pauseWait();
                await SafeRender.RestIfDueAsync(10 + (int)(35.0 * (gOut - 1) / totalFrames), progress, ct);
            }
            // 段尾关键帧(该段最后一个关键画,直接落帧)
            File.Copy(keys[e - 1], Path.Combine(framesFinal, $"frame_{gOut++:D6}.png"), true);
        }
        AppLogger.Info($"方案C 累积网格插值:{n} 关键画 → {gOut - 1} 帧({totalGaps} 个动作段,F_out={outFpsSafe:0.##},CFR 对齐输出)");
    }

    /// <summary>检测目录里的 PNG 是否有全黑帧(ncnn-vulkan GPU 队列失败时输出全黑,退出码仍 0)。</summary>
    private static bool batchOutDirHasBlack(string dir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.png"))
            {
                using var bmp = new System.Drawing.Bitmap(f);
                int step = Math.Max(4, Math.Min(bmp.Width, bmp.Height) / 32);
                int dark = 0, total = 0;
                for (int y = step; y < bmp.Height; y += step)
                    for (int x = step; x < bmp.Width; x += step)
                    {
                        var p = bmp.GetPixel(x, y);
                        total++;
                        if ((int)p.R + (int)p.G + (int)p.B < 24) dark++;   // 接近全黑
                    }
                if (total > 0 && dark >= total * 0.95) return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>目录中【源帧】是否本来就近全黑(≥95% 像素 RGB 和 &lt; 24):
    /// 用于"黑帧防误杀"——素材本身的黑场(淡入淡出/片头黑场/夜间纯黑镜头)
    /// 输出黑是正常结果,不是 GPU 故障,不需要 CPU 重算。</summary>
    private static bool DirNearBlack(string dir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.png"))
            {
                using var bmp = new System.Drawing.Bitmap(f);
                int step = Math.Max(4, Math.Min(bmp.Width, bmp.Height) / 32);
                int dark = 0, total = 0;
                for (int y = step; y < bmp.Height; y += step)
                    for (int x = step; x < bmp.Width; x += step)
                    {
                        var p = bmp.GetPixel(x, y);
                        total++;
                        if ((int)p.R + (int)p.G + (int)p.B < 24) dark++;
                    }
                if (total > 0 && dark >= total * 0.95) return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>探测视频总帧数(时长 × 帧率,去重换算用)。</summary>
    private static int? ProbeFrameCount(string videoPath)
    {
        var ffmpeg = FfmpegPath;
        if (ffmpeg == null) return null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = $"-i \"{videoPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var durM = System.Text.RegularExpressions.Regex.Match(err, @"Duration: (\d+):(\d+):([\d.]+)");
            var fpsM = System.Text.RegularExpressions.Regex.Match(err, @"(\d+(?:\.\d+)?)\s*fps");
            if (durM.Success && fpsM.Success
                && double.TryParse(durM.Groups[3].Value, System.Globalization.NumberStyles.Float, inv, out var sec)
                && double.TryParse(fpsM.Groups[1].Value, System.Globalization.NumberStyles.Float, inv, out var fps)
                && fps > 0)
            {
                var dur = int.Parse(durM.Groups[1].Value) * 3600 + int.Parse(durM.Groups[2].Value) * 60 + sec;
                return (int)Math.Round(dur * fps);
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>探测视频信息(帧率/时长/分辨率),返回展示用文本。</summary>
    public static async Task<string> ProbeVideoInfoAsync(string videoPath)
    {
        var ffmpeg = FfmpegPath;
        if (ffmpeg == null) return "";
        return await Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = $"-i \"{videoPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                };
                using var p = Process.Start(psi);
                if (p == null) return "";
                var err = p.StandardError.ReadToEnd();
                p.WaitForExit();
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var fpsM = System.Text.RegularExpressions.Regex.Match(err, @"(\d+(?:\.\d+)?)\s*fps");
                var fps = fpsM.Success ? fpsM.Groups[1].Value : "?";
                var durM = System.Text.RegularExpressions.Regex.Match(err, @"Duration: (\d+):(\d+):([\d.]+)");
                var dur = "";
                if (durM.Success)
                {
                    double sec = int.Parse(durM.Groups[1].Value) * 3600 + int.Parse(durM.Groups[2].Value) * 60
                        + double.Parse(durM.Groups[3].Value, inv);
                    var ts = TimeSpan.FromSeconds(sec);
                    dur = ts.Hours > 0 ? $"{ts.Hours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                        : $"{ts.Minutes}:{ts.Seconds:D2}";
                }
                var resM = System.Text.RegularExpressions.Regex.Match(err, @"(\d{2,5})x(\d{2,5})");
                var res = resM.Success ? $"{resM.Groups[1].Value}×{resM.Groups[2].Value}" : "";
                var parts = new System.Collections.Generic.List<string>();
                if (dur.Length > 0) parts.Add(dur);
                parts.Add($"{fps} fps");
                if (res.Length > 0) parts.Add(res);
                return string.Join(" · ", parts);
            }
            catch { return ""; }
        });
    }

    /// <summary>探测视频时长(秒)。</summary>
    public static async Task<double> ProbeDurationSeconds(string videoPath)
    {
        var ffmpeg = FfmpegPath;
        if (ffmpeg == null) return 0;
        return await Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = $"-i \"{videoPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                };
                using var p = Process.Start(psi);
                if (p == null) return 0.0;
                var err = p.StandardError.ReadToEnd();
                p.WaitForExit();
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var m = System.Text.RegularExpressions.Regex.Match(err, @"Duration: (\d+):(\d+):([\d.]+)");
                if (!m.Success) return 0.0;
                return int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60
                    + double.Parse(m.Groups[3].Value, inv);
            }
            catch { return 0.0; }
        });
    }

    /// <summary>真实画面时长(帧数 ÷ 平均帧率),比容器 duration 精确:
    /// MP4 容器 Duration 常比实际画面多含尾帧容积/编辑轨道(如素材1 容器 1.82s,实际画面 1.775s),
    /// 用它做"时长保护"会导致输出比原片长 2%(时间对不上)。用 ffprobe count_frames 求精确值。</summary>
    public static async Task<double> ProbeTrueDurationSeconds(string videoPath, CancellationToken ct = default)
    {
        var (frames, dur) = await ProbeTrueFramesAndDuration(videoPath, ct);
        return dur > 0.01 ? dur : await ProbeDurationSeconds(videoPath);
    }

    /// <summary>ffprobe count_frames 求"真实帧数 + 真实画面时长"(帧数÷平均帧率),比容器 duration 精确。</summary>
    public static async Task<(long frames, double duration)> ProbeTrueFramesAndDuration(string videoPath, CancellationToken ct = default)
    {
        var ff = FfmpegPath;
        if (ff == null) return (0, 0);
        var dir = Path.GetDirectoryName(ff);
        var ffprobe = dir != null ? Path.Combine(dir, "ffprobe.exe") : null;
        if (ffprobe == null || !File.Exists(ffprobe)) return (0, 0);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffprobe,
                Arguments = $"-v error -select_streams v:0 -count_frames " +
                            $"-show_entries stream=nb_read_frames,avg_frame_rate -of csv=p=0 \"{videoPath}\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return (0, 0);
            var o = await p.StandardOutput.ReadToEndAsync();
            await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync().ConfigureAwait(false);
            var parts = o.Trim().Split(',');
            // ffprobe 实测输出顺序为: avg_frame_rate,nb_read_frames (如 "5211/250,38")——
            // 帧率在前、帧数在后;老版本可能相反,两种都兼容解析。
            if (parts.Length >= 2)
            {
                var a = parts[0].Trim(); var b = parts[1].Trim();
                long nf = 0; double fr = 0;
                if (long.TryParse(a, out var n1) && n1 > 0 && TryParseFps(b, out var f1)) { nf = n1; fr = f1; }
                else if (long.TryParse(b, out var n2) && n2 > 0 && TryParseFps(a, out var f2)) { nf = n2; fr = f2; }
                if (nf > 0 && fr > 0)
                {
                    // 真实画面时长 = (帧数-1)÷帧率:第 nf 帧没有"时长",只有 nf-1 个帧间隔
                    // (素材1: 37/20.844 = 1.7751,不是 38/20.844=1.8231——后者正是"结尾多 2%"的根源)
                    double dur = nf >= 2 ? (nf - 1) / fr : 1.0 / fr;
                    if (dur > 0.01) return (nf, dur);
                }
            }
        }
        catch { }
        return (0, 0);
    }

    /// <summary>解析 "5211/250" 或 "20.84" 形式的帧率。</summary>
    private static bool TryParseFps(string s, out double fps)
    {
        fps = 0;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var fr = s.Split('/');
        if (fr.Length == 2 && double.TryParse(fr[0], System.Globalization.NumberStyles.Float, inv, out var nu)
            && double.TryParse(fr[1], System.Globalization.NumberStyles.Float, inv, out var de) && de > 0)
        { fps = nu / de; return true; }
        if (double.TryParse(s, System.Globalization.NumberStyles.Float, inv, out var f) && f > 0)
        { fps = f; return true; }
        return false;
    }

    /// <summary>本会话已实测"真的能编码"的硬件编码器,按优先级 nvenc &gt; amf &gt; qsv。</summary>
    private static readonly System.Collections.Generic.List<string> WorkingHwEncoders = new();
    private static bool _hwProbed;
    private static readonly object _hwLock = new();

    /// <summary>最近一次选择的视频压缩编码器描述(供界面/日志展示,不靠猜)。</summary>
    public static string LastVideoEncoderInfo { get; private set; } = "libx264 (CPU 软编)";

    /// <summary>最近一次去重的报告文本(删帧数/集中时段/有效帧率),供任务完成后显示在输出信息与日志,
    /// 解决"已拆出 N 帧/有效帧率"提示一闪而过看不清的问题。</summary>
    public static string? LastDedupReport { get; set; }

    /// <summary>简短版去重结果(蓝色小字用,保证不截断):如 "去重:107→77 帧 (有效 21.5 fps)"。</summary>
    public static string? LastDedupShort { get; set; }

    // ===== 真暂停:冻结/恢复全部子进程(随点随停、随点随恢复、进度零丢失) =====
    // 用 App.ActiveProcesses(所有 RunAsync/RunCaptureAsync/引擎进程都会 Register 进去)遍历冻结,
    // 覆盖补帧(RIFE)、拆帧(ffmpeg)、合帧编码 等所有重步骤;多路并发(如 2/3 路并行超分)也能全部冻结。

    [DllImport("ntdll.dll", PreserveSig = false, SetLastError = true)]
    private static extern void NtSuspendProcess(IntPtr processHandle);
    [DllImport("ntdll.dll", PreserveSig = false, SetLastError = true)]
    private static extern void NtResumeProcess(IntPtr processHandle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
    private const uint PROCESS_ALL_ACCESS = 0x001F0FFF;

    /// <summary>冻结全部当前子进程(暂停生效:进程立即停止计算,占用释放给其他程序)。仅在进程仍在运行时生效。</summary>
    internal static void SuspendActiveProcess()
    {
        foreach (var p in App.ActiveProcesses.Snapshot())
        {
            if (p.HasExited) continue;
            var h = OpenProcess(PROCESS_ALL_ACCESS, false, p.Id);
            if (h != IntPtr.Zero) { try { NtSuspendProcess(h); } catch { } finally { CloseHandle(h); } }
        }
    }

    /// <summary>解冻全部当前子进程(恢复继续,从冻结点接着算,不重算)。</summary>
    internal static void ResumeActiveProcess()
    {
        foreach (var p in App.ActiveProcesses.Snapshot())
        {
            if (p.HasExited) continue;
            var h = OpenProcess(PROCESS_ALL_ACCESS, false, p.Id);
            if (h != IntPtr.Zero) { try { NtResumeProcess(h); } catch { } finally { CloseHandle(h); } }
        }
    }

    /// <summary>运行时实测各硬件编码器到底能不能用(不靠显卡名猜):每个都编一段极小画面,能出有效文件才算可用。
    /// 首次调用初始化,之后缓存复用;并发探测只跑一次。</summary>
    private static async Task EnsureHwProbeAsync(string ffmpeg, CancellationToken ct = default)
    {
        lock (_hwLock) { if (_hwProbed) return; _hwProbed = true; }
        // 依优先级探测;能真编出一帧有效文件才算可用(驱动过老/无对应硬件会失败被跳过)。
        // 注意:测试画面不能太小(nvenc 拒绝过小分辨率,64x64 会报 incorrect parameters),
        // 用 320x240 这种常规尺寸才能真实反映编码器可用性。
        // H.264 与 H.265(hevc)各探一遍,方便用户选编码格式时直接给出可用的
        foreach (var enc in new[] { "h264_nvenc", "h264_amf", "h264_qsv", "hevc_nvenc", "hevc_amf", "hevc_qsv" })
        {
            if (ct.IsCancellationRequested) return;   // 用户取消/任务中止:探测立即收手(不再不可取消卡住)
            var tmp = Path.Combine(Path.GetTempPath(), $"imgup_encprobe_{enc}_{Guid.NewGuid():N}.mp4");
            try
            {
                await RunAsync(ffmpeg,
                    $"-y -f lavfi -i \"testsrc=size=320x240:rate=1:duration=0.4\" -frames:v 1 -c:v {enc} " +
                    $"\"{tmp}\"",
                    null, ct);
                if (File.Exists(tmp) && new FileInfo(tmp).Length > 0)
                    lock (_hwLock) WorkingHwEncoders.Add(enc);
            }
            catch { /* 该编码器在这台机器不可用 */ }
            finally { try { File.Delete(tmp); } catch { } }
        }
        // 诊断:记录本机可用/不可用的硬件编码器(排查"为什么没走 GPU 编码"一眼可见)
        lock (_hwLock)
        {
            AppLogger.Info("硬件编码器探测:" + (WorkingHwEncoders.Count > 0
                ? "可用 [" + string.Join(", ", WorkingHwEncoders) + "]"
                : "全部不可用(将用 CPU 软编)"));
        }
    }

    /// <summary>按"实测可用"自适应选视频压缩编码器:优先厂商匹配的硬编,其次任一可用硬编,最后 libx264。
    /// codecPref:0=自动(H.264 优先) 1=强制 H.264 2=优先 H.265(hevc,更省空间,老设备可能播不了)。</summary>
    private static string PickVideoEncoder(int gpuId, int codecPref = 0)
    {
        var gpus = GpuInfo.GetAdapterNames();
        string h264Vendor = "", hevcVendor = "";
        if (gpuId >= 0 && gpuId < gpus.Count)
        {
            var name = gpus[gpuId];
            bool nv = name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
            bool amd = name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase);
            bool intel = name.Contains("Intel", StringComparison.OrdinalIgnoreCase);
            if (nv) { h264Vendor = "h264_nvenc"; hevcVendor = "hevc_nvenc"; }
            else if (amd) { h264Vendor = "h264_amf"; hevcVendor = "hevc_amf"; }
            else if (intel) { h264Vendor = "h264_qsv"; hevcVendor = "hevc_qsv"; }
        }
        string chosen;
        lock (_hwLock)
        {
            if (codecPref == 2)
            {
                // H.265:优先厂商匹配的 hevc 硬编,其次任一可用 hevc 硬编,最后 libx265
                if (hevcVendor.Length > 0 && WorkingHwEncoders.Contains(hevcVendor)) chosen = hevcVendor;
                else if (WorkingHwEncoders.Any(e => e.StartsWith("hevc", StringComparison.OrdinalIgnoreCase)))
                    chosen = WorkingHwEncoders.First(e => e.StartsWith("hevc", StringComparison.OrdinalIgnoreCase));
                else chosen = "libx265";
            }
            else if (h264Vendor.Length > 0 && WorkingHwEncoders.Contains(h264Vendor)) chosen = h264Vendor;
            else if (WorkingHwEncoders.Count > 0) chosen = WorkingHwEncoders[0];
            else chosen = "libx264";
        }
        bool isCpu = chosen is "libx264" or "libx265";
        bool isHevc = chosen.StartsWith("hevc", StringComparison.OrdinalIgnoreCase) || chosen == "libx265";
        LastVideoEncoderInfo = isCpu
            ? $"{chosen} (CPU 软编,本机无可用 GPU 硬编)"
            : $"{chosen} (GPU 硬编)";
        if (!isCpu && (codecPref == 2 ? hevcVendor : h264Vendor).Length > 0
            && chosen != (codecPref == 2 ? hevcVendor : h264Vendor))
            LastVideoEncoderInfo += $" — 厂商编码器不可用,改用 {chosen}";
        if (isHevc) LastVideoEncoderInfo += " (H.265 更省空间;极老设备可能无法播放)";
        return chosen;
    }

    /// <summary>探测视频音频编码名(小写;无音频/失败返回空)。</summary>
    public static async Task<string> ProbeAudioCodec(string video)
    {
        var ffmpegDir = FfmpegPath != null ? Path.GetDirectoryName(FfmpegPath) : null;
        var ffprobe = ffmpegDir != null ? Path.Combine(ffmpegDir, "ffprobe.exe") : null;
        if (ffprobe == null || !File.Exists(ffprobe)) return "";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffprobe,
                Arguments = $"-v error -select_streams a:0 -show_entries stream=codec_name -of csv=p=0 \"{video}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return "";
            var line = await p.StandardOutput.ReadToEndAsync();
            return line.Trim().ToLowerInvariant();
        }
        catch { return ""; }
    }

    /// <summary>本会话已知会失败的硬件编码器(如 nvenc 驱动过老),避免每次先白跑一次硬件编码再回退。</summary>
    private static readonly System.Collections.Generic.HashSet<string> BrokenHwEncoders = new();

    /// <summary>本会话 GPU 硬解(d3d11va)已验证不可用:后续拆帧直接软解,不再每次白试一次。</summary>
    private static bool _hwDecodeBroken;

    /// <summary>拆帧(优先 GPU 硬解 d3d11va,失败自动回退软解):既省 CPU 又提速。
    /// vfExpr=滤镜表达式(如 scale...);返回实际拆出的帧数。</summary>
    private static async Task<int> ExtractFramesCoreAsync(string ffmpeg, string inputVideo, string trimArgs,
        string vfExpr, string framesDir, IProgress<(int pct, string msg)>? progress, CancellationToken ct,
        int origCountEst, bool vfrPts = false)
    {
        var pattern = Path.Combine(framesDir, "frame_%06d.png");
        // VFR(可变帧率)素材:拆帧加 -fps_mode passthrough 保留每帧真实时间戳,不按平均帧率丢弃/复制帧,
        // 避免时间轴失真(变速感)。仅在用户开启「可变帧率素材」时生效;CFR 素材不需要,默认关闭。
        string fpsMode = vfrPts ? " -fps_mode passthrough" : "";
        // 开关2(分线程):拆帧限流,避免抢系统核(仅开启时生效)
        string threadsArg = SafeRender.SplitCores ? $" -threads {Math.Max(2, SafeRender.CpuCoreCount - 2)}" : "";
        // 硬解优先(仅当本会话没验证过坏);坏过一次就永久软解,不再白跑
        if (!_hwDecodeBroken)
        {
            try
            {
                await RunAsync(ffmpeg,
                    $"-y {trimArgs} -hwaccel d3d11va -i \"{inputVideo}\"{fpsMode}{threadsArg} -vf \"{vfExpr}\" -qscale:v 1 \"{pattern}\"",
                    progress, ct, "拆帧", origCountEst);
                int n = Directory.EnumerateFiles(framesDir, "*.png").Count();
                if (n > 0) return n;
                _hwDecodeBroken = true;   // 硬解输出 0 帧 → 视为不可用
            }
            catch { _hwDecodeBroken = true; }   // 硬解失败 → 标记坏,回退软解
            // 清理硬解可能留下的残缺帧
            foreach (var f in Directory.EnumerateFiles(framesDir, "*.png"))
            { try { File.Delete(f); } catch { } }
        }
        await RunAsync(ffmpeg,
            $"-y {trimArgs} -i \"{inputVideo}\"{fpsMode}{threadsArg} -vf \"{vfExpr}\" -qscale:v 1 \"{pattern}\"",
            progress, ct, "拆帧", origCountEst);
        return Directory.EnumerateFiles(framesDir, "*.png").Count();
    }

    /// <summary>
    /// 生成"每帧原始时长"表(与拆帧输出帧序列一一对应,单位:秒)。
    /// 用与拆帧完全相同的滤镜链跑一遍 showinfo:滤镜链里丢帧(select/mpdecimate)时,
    /// showinfo 报告的就是"保留帧"的时间戳——时长表天然与拆帧结果对齐。
    /// 这样去重删帧后,输出时间轴仍按原视频 PTS 铺(静态段帧时长=多帧之和),不再变速。
    /// 失败/帧数异常时返回 null(调用侧回退固定帧率输出)。
    /// </summary>
    private static async Task<List<double>?> BuildFrameDurationsAsync(string ffmpeg, string inputVideo,
        string trimArgs, string vfExpr, CancellationToken ct)
    {
        var durs = new List<double>();
        try
        {
            // -fps_mode passthrough 是输出选项,必须放在 -i 之后:
            // 输出时间戳=输入时间戳,不让 ffmpeg 按平均帧率补帧/复制帧(否则 VFR 变 CFR)
            var lines = await RunCaptureAsync(ffmpeg,
                $"-y {trimArgs} -i \"{inputVideo}\" -fps_mode passthrough -vf \"{vfExpr},showinfo\" -f null NUL", ct);
            var pts = new List<double>();
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var l in lines)
            {
                var m = System.Text.RegularExpressions.Regex.Match(l, @"pts_time:([0-9.]+)");
                if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, inv, out var t))
                    pts.Add(t);
            }
            if (pts.Count < 2) return null;   // 没拿到时间戳 → 回退固定帧率
            for (int i = 1; i < pts.Count; i++)
                durs.Add(Math.Max(0.0005, pts[i] - pts[i - 1]));
            // 末帧时长按前几帧均值补(没有下一帧的时间戳)
            double avg = durs.Take(Math.Min(8, durs.Count)).Average();
            durs.Add(Math.Max(0.0005, avg));
        }
        catch { return null; }   // 时长表失败:调用侧回退到固定帧率输出
        return durs;
    }

    /// <summary>按"删掉的帧号(1-based)"把帧时长归并到前一个保留帧上,并**移除被删帧的条目**,
    /// 使时长表与删帧后的帧序列一一对应(删帧不压缩时间轴,同时表长必须对齐帧数,
    /// 否则会被"帧数匹配校验"整体回退成固定帧率 → 变速)。
    /// 从后往前删:删除后面的条目不影响前面索引,时长并入"前面最近的保留帧"。</summary>
    private static void MergeDurations(List<double> durs, System.Collections.Generic.IEnumerable<int> dropped, int totalCount)
    {
        var dropSet = dropped as System.Collections.Generic.HashSet<int>
            ?? new System.Collections.Generic.HashSet<int>(dropped);
        int actual = Math.Min(durs.Count, totalCount);
        for (int i = actual - 1; i >= 0; i--)
        {
            int frameNo = i + 1;
            if (!dropSet.Contains(frameNo)) continue;
            // 找"前面最近的保留帧"(若前面连续都是被删帧则递推到更前)
            int k = i - 1;
            while (k >= 0 && dropSet.Contains(k + 1)) k--;
            if (k >= 0 && k < durs.Count && k < i) durs[k] += durs[i];
            durs.RemoveAt(i);
        }
    }

    /// <summary>时长表与最终帧文件数对齐(补帧输出数可能与展开数差 ±1,尾部均摊/裁剪即可,无视觉影响)。</summary>
    private static void AlignDurationsToCount(System.Collections.Generic.List<double> d, int n)
    {
        if (d.Count == n) return;
        double avg = d.Count > 0 ? d.Average() : 1.0 / 30.0;
        while (d.Count < n) d.Add(avg);
        while (d.Count > n) d.RemoveAt(d.Count - 1);
    }

    /// <summary>
    /// 把"每帧时长表"转成 ffmpeg setpts 分段表达式(精确 VFR 时间轴,精度=输出时基,无 concat 25fps 量化)。
    /// 相邻时长相同的帧合并成段,帧 k 的目标时间 = 段起点累计 + (k-段首帧)*段时长;
    /// 表达式用 lt/gte(比较返回 0/1,逗号以 "\," 转义避免 filtergraph 分隔)。
    /// 段数过多(异常表)返回 null → 调用侧回退固定帧率。
    /// </summary>
    private static string? BuildVfrSetptsExpr(System.Collections.Generic.List<double> durs)
    {
        try
        {
            // 合并相邻相同时长成段(±1e-5 视为相同)
            var segs = new System.Collections.Generic.List<(int s, int e, double p0, double d)>();
            int i = 0;
            double acc = 0;
            while (i < durs.Count)
            {
                int s = i;
                double d = durs[i];
                while (i < durs.Count && Math.Abs(durs[i] - d) < 1e-5) i++;
                segs.Add((s, i, acc, d));
                acc += d * (i - s);
            }
            if (segs.Count == 0) return null;
            if (segs.Count > 400) { AppLogger.Info($"VFR 时间轴段数 {segs.Count} 超过上限 400,回退 CFR(避免 setpts 命令超 Windows 命令行 32767 字符)"); return null; }   // 异常/过长:回退并提示
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder("setpts=(");
            bool first = true;
            foreach (var (s, e, p0, d) in segs)
            {
                if (!first) sb.Append(" + ");
                first = false;
                sb.Append($"(lt(N\\,{e})*gte(N\\,{s})*({p0.ToString("0.######", inv)}+(N-{s})*{d.ToString("0.######", inv)}))");
            }
            sb.Append(")/TB");
            return sb.ToString();
        }
        catch { return null; }
    }

    /// <summary>帧差法(SAD)快筛 + 分块 SSIM 精确验证的动漫去重:
    /// 1) 与前面 N 帧(默认 6)做帧差(SAD)比较——不只相邻帧:循环动画/正反打镜头(来回重复的画面)
    ///    与"上一帧"往往不同,但和前面某帧几乎相同,多参考帧能识别并删除这种重复;
    /// 2) 疑似帧对算分块 SSIM(亮度/对比度/结构三维),SSIM 高于阈值才最终判定重复并删除;
    /// 3) 局部动作保护(protectRatio=变化块占比上限):完全静止的帧照删;
    ///    但明显区域在动的帧视为角色动作,保留不删——防止说话/眨眼被误删。
    ///    window=多参考帧范围/scale=采样粒度(px)/blockThr=一块平均差异超过多少算"在动"(决定保护判据)。
    /// 4) 静止段合并(segSsim>0 时启用):连续 N(≥3)帧都与"段首帧"近似(与段首比,不是相邻比),
    ///    说明整段画面没动(长保持/静止镜头)→ 段内除首帧全部删除,只留段首代表帧。
    ///    动漫/敏感模式启用(强度联动),标准/智能不启用(保守)。
    /// 返回要删除的帧号(1-based,与 frame_%06d.png 序号对应)。</summary>
    private static System.Collections.Generic.HashSet<int> DetectDupFramesWithSsim(string framesDir,
        double sadThr, double ssimThr, double protectRatio = 0.06,
        int window = 6, int scale = 16, double blockThr = 4,
        double segSsim = 0, double segSad = 5, bool motionComp = true, bool protectSmallMotion = true)
    {
        var drop = new System.Collections.Generic.HashSet<int>();
        var files = Directory.EnumerateFiles(framesDir, "*.png")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length < 2) return drop;
        window = Math.Clamp(window, 2, 12);
        var grays = new System.Collections.Generic.List<byte[]>(Math.Min(window, files.Length));
        grays.Add(SampleGray(files[0], scale, out var sw, out var sh));
        // 静止段合并用独立"段首样本"(全序列帧号,不受 grays 窗口裁剪影响——旧实现复用窗口后
        // grays 索引与真实帧号脱节,总在最后十几帧窗口内误删/漏检)
        byte[] segBase = grays[0];
        int segRun = 0;
        for (int i = 1; i < files.Length; i++)
        {
            var cur = SampleGray(files[i], scale, out sw, out sh);
            // 与窗口内前面各帧比较:命中任一"几乎相同"的参考帧即判重复(多参考帧:抓循环/回切)
            for (int k = Math.Max(0, grays.Count - window); k < grays.Count; k++)
            {
                var prev = grays[k];
                double sad = MeanAbsDiff(prev, cur);
                bool isDup = false;
                if (sad < sadThr)          // 帧差快筛:差异极小才继续精确验证
                {
                    double ssim = BlockSsim(prev, cur, sw, sh);
                    if (ssim > ssimThr)
                    {
                        // 局部动作保护:变化块占比低于阈值才算"保持帧/一拍二"可删;
                        // 高于阈值=角色/镜头在动 → 保留(阈值随强度放宽,见调用方)。
                        // 关键防线(研究):maxDiff≥blockThr*2 = 有【某个大块真的在动】(小口型也会让它超阈值)
                        // → 绝不判重复,否则"张嘴/眨眼"这类小面积微动帧会被误删(用户实测"后半段口型动画没了")。
                        // 【手动模式 protectSmallMotion=false:防线关闭,用户参数完全生效(专家自担风险)】。
                        var (changedRatio, _, maxDiff) = FrameMotionStats(prev, cur, sw, sh, blockThr);
                        if (changedRatio < protectRatio && (!protectSmallMotion || maxDiff < blockThr * 2)) isDup = true;
                    }
                }
                // 镜头运动补偿(只对"紧邻上一帧"做):背景持续 pan 时整帧 SAD/SSIM 到不了"相同",
                // 但先估相机平移并"对齐"后,残差极小+变化块占比极低 = 人物没动(定格/冗余)→ 判重删除。
                if (!isDup && motionComp && k == grays.Count - 1)
                {
                    var (_, _, alignedSad, chRatio) = EstimateGlobalShift(prev, cur, sw, sh);
                    if (alignedSad < 2.5 && chRatio < 0.08) isDup = true;
                }
                if (isDup) { drop.Add(i + 1); break; }
            }
            // 静止段合并(与 grays 窗口无关,用独立段首样本):与段首帧持续近似(≥3 帧)→ 段内除首帧全删
            if (segSsim > 0)
            {
                double sSad = MeanAbsDiff(segBase, cur);
                double sSsim = BlockSsim(segBase, cur, sw, sh);
                if (sSad < segSad && sSsim > segSsim)
                {
                    segRun++;
                    if (segRun >= 2) drop.Add(i + 1);   // 段长≥3(首帧+第1、2帧后):该帧视为静止段冗余帧,删
                }
                else { segBase = cur; segRun = 0; }   // 画面变了 → 开新段
            }
            grays.Add(cur);
            if (grays.Count > window * 2) grays.RemoveAt(0);   // 只留最近窗口,防内存膨胀
        }
        return drop;
    }

    // ===== 重复帧预览(轻量预估 + 选中全文分析;分析复用处理阶段的同一套检测器,保证数字口径一致) =====
    /// <summary>重复段信息(时间轴某段内删了多少重复帧)。</summary>
    public sealed class DupSegInfo
    {
        public double Start { get; set; }   // 秒
        public double End { get; set; }     // 秒
        public int Deduped { get; set; }    // 该段被判为重复、会被删除的帧数
    }

    /// <summary>重复帧画像(预览用):重复占比 + 内容帧率 + 按时间分布。</summary>
    public sealed class DupProfile
    {
        public double DupRatioPct { get; set; }   // 重复占比 %
        public double ContentFps { get; set; }    // 内容帧率
        public bool Estimated { get; set; }        // true=轻量预估(标"预估");false=选中后全文分析
        public string Summary { get; set; } = "";
        public System.Collections.Generic.List<DupSegInfo> Segs { get; set; } = new();
    }

    /// <summary>按相邻采样帧的"变化像素占比"估算重复占比% + 内容帧率(轻量预估用)。
    /// 占比 &lt; 0.006(仅 &lt;0.6% 像素明显变化)视为"真近重复"。用占比而非整幅均值差值——
    /// 均值差值会被静止背景稀释,把"细节帧很多、主体在动"的视频误判成 ~75% 重复(用户实测失真根因)。</summary>
    private static (double dupPct, double contentFps) EstimateFromChanges(System.Collections.Generic.List<double> changes, double inFps)
    {
        if (changes.Count == 0) return (0, inFps);
        double thr = 0.006;   // 明显变化(>dt)像素占比 < 0.6% = 真近重复帧
        int dup = changes.Count(x => x < thr);
        double dupPct = 100.0 * dup / changes.Count;
        double cFps = inFps * (1 - dupPct / 100.0);
        if (cFps < 0.5) cFps = 0.5;
        return (dupPct, cFps);
    }

    /// <summary>把"被判重复的帧号集合"按时间分 8 段,统计每段重复帧数(找集中时段)。</summary>
    private static System.Collections.Generic.List<DupSegInfo> BuildDupSegments(
        System.Collections.Generic.HashSet<int> drop, int totalFrames, double duration)
    {
        var segs = new System.Collections.Generic.List<DupSegInfo>();
        if (totalFrames <= 0 || duration <= 0) return segs;
        const int N = 8;
        var counts = new int[N];
        foreach (var f in drop)
        {
            int s = Math.Clamp((f - 1) * N / Math.Max(1, totalFrames), 0, N - 1);
            counts[s]++;
        }
        for (int s = 0; s < N; s++)
            if (counts[s] > 0)
                segs.Add(new DupSegInfo { Start = s * duration / N, End = (s + 1) * duration / N, Deduped = counts[s] });
        return segs;
    }

    /// <summary>轻量预估:每 N 帧抽 1 + 缩到 160 宽灰度,算相邻帧差,估算"重复占比% + 内容帧率"。快速,只做预览(标"预估")。</summary>
    private static async Task<DupProfile> ProbeDupLightAsync(string ffmpeg, string videoPath, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"imgup_duplight_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            double inFps = 30;
            if (double.TryParse(ProbeFps(videoPath), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var f0) && f0 > 0) inFps = f0;
            double dur = await ProbeDurationSeconds(videoPath);
            if (dur <= 0.05) return new DupProfile { Estimated = true, Summary = "时长未知,无法预估" };
            int approxFrames = Math.Max(8, (int)Math.Round(inFps * dur));
            int step = Math.Max(4, (int)Math.Ceiling(approxFrames / 240.0));   // 采样 ≤~240 帧,防长视频过慢
            var pattern = Path.Combine(dir, "f_%06d.png");
            await RunAsync(ffmpeg,
                $"-y -i \"{videoPath}\" -vf \"select='not(mod(n,{step}))',scale=160:-2,format=gray\" -qscale:v 1 \"{pattern}\"",
                null, ct, "预估", 0);
            var files = Directory.EnumerateFiles(dir, "*.png").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            if (files.Count < 2) return new DupProfile { Estimated = true, Summary = "帧数过少,无法预估" };
            var sads = new System.Collections.Generic.List<double>();
            var prev = SampleGray(files[0], 1, out var sw, out var sh);
            for (int i = 1; i < files.Count; i++)
            {
                var cur = SampleGray(files[i], 1, out sw, out sh);
                sads.Add(ChangedRatio(prev, cur, 8));   // 用"变化像素占比"而非均值差,避免背景稀释误判大量重复
                prev = cur;
            }
            var (dupPct, cFps) = EstimateFromChanges(sads, inFps);
            return new DupProfile
            {
                DupRatioPct = dupPct, ContentFps = cFps, Estimated = true,
                Summary = $"预估:重复约 {dupPct:0}%,内容帧率 ≈{cFps:0.##} fps",
            };
        }
        catch { return new DupProfile { Estimated = true, Summary = "预估失败" }; }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>全文分析:缩到 400 宽灰度拆帧,复用处理阶段同一套 DetectDupFramesWithSsim / DetectDupFramesAdaptive,
    /// 再算按时间轴的重复分布 → 预览数字与处理结果同口径(分辨率近似,算法一致)。</summary>
    private static async Task<DupProfile> AnalyzeDupAsync(string ffmpeg, string videoPath,
        int dedupMode, double dedupAnimeThr, int dedupSmartMode, bool motionComp, bool dedupOnlyTrueHold, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"imgup_dupanalyze_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            double inFps = 30;
            if (double.TryParse(ProbeFps(videoPath), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var f0) && f0 > 0) inFps = f0;
            double dur = await ProbeDurationSeconds(videoPath);
            var pattern = Path.Combine(dir, "frame_%06d.png");
            await RunAsync(ffmpeg,
                $"-y -i \"{videoPath}\" -vf \"scale=400:-2,format=gray\" -qscale:v 1 \"{pattern}\"",
                null, ct, "分析", 0);
            int total = Directory.EnumerateFiles(dir, "*.png").Count();
            if (total < 2) return new DupProfile { Summary = "帧数过少,无法分析" };
            int scaleSample = 4;   // 400 宽 / 4 = ~100 宽采样,接近处理阶段口径(处理用 原分辨率/采样粒度)
            System.Collections.Generic.HashSet<int> drop;
            if (dedupMode == 1)   // 智能:自适应
            {
                drop = DetectDupFramesAdaptive(dir, null, scaleSample, dedupSmartMode, motionComp);
            }
            else
            {
                double ssim = dedupMode == 2 ? dedupAnimeThr : dedupMode == 5 ? 0.87 : 0.97;
                // 高保真去重(与处理一致):SSIM 收紧到只删"真定格"(≥0.995)
                if (dedupOnlyTrueHold) ssim = Math.Max(ssim, 0.995);
                double sad = dedupMode switch
                {
                    5 => 4.5,
                    2 => dedupAnimeThr switch { 0.90 => 3.5, 0.88 => 4.0, 0.85 => 4.5, _ => 3.0 },
                    _ => 3.0,
                };
                double protect = dedupMode switch
                {
                    5 => 0.45,
                    2 => dedupAnimeThr switch { 0.90 => 0.18, 0.88 => 0.22, 0.85 => 0.28, _ => 0.15 },
                    _ => 0.12,
                };
                double segSsim = 0, segSad = 5;
                if (dedupMode == 2) { segSsim = dedupAnimeThr switch { 0.85 => 0.93, 0.88 => 0.94, 0.90 => 0.94, _ => 0.95 }; segSad = dedupAnimeThr switch { 0.90 => 5.0, 0.88 => 6.0, 0.85 => 6.5, _ => 4.0 }; }
                else if (dedupMode == 5) { segSsim = 0.88; segSad = 6.5; }
                if (dedupOnlyTrueHold && segSsim > 0) segSsim = Math.Max(segSsim, 0.995);
                drop = DetectDupFramesWithSsim(dir, sad, ssim, protect, 6, scaleSample, 4, segSsim, segSad, motionComp);
            }
            double dupPct = 100.0 * drop.Count / Math.Max(1, total);
            double cFps = inFps * (1 - dupPct / 100.0); if (cFps < 0.5) cFps = 0.5;
            return new DupProfile
            {
                DupRatioPct = dupPct, ContentFps = cFps, Estimated = false,
                Segs = BuildDupSegments(drop, total, dur),
                Summary = $"重复约 {dupPct:0}%(删 {drop.Count} 帧),内容帧率 ≈{cFps:0.##} fps",
            };
        }
        catch { return new DupProfile { Summary = "分析失败" }; }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>一键参考预估(用户定案):徽标=原视频真实内容帧率的自动识别(参考,不被手填值覆盖)。
    /// 实现:均匀抽样 ~120 帧 → 16px 灰度相邻 SAD → 大变化事件间隔中位数 = 拍型 →
    /// 内容帧率 = 源帧率 ÷ 间隔;删除率 = (间隔-1)/间隔;无节奏时按"肉眼无感"占比估。快(3~8 秒)。</summary>
    public static async Task<DupProfile> ProbeRhythmAsync(string videoPath, double srcFps, CancellationToken ct)
    {
        var ffmpeg = FfmpegPath;
        if (ffmpeg == null)
        {
            AppLogger.Info($"预估失败:未找到 ffmpeg({Path.GetFileName(videoPath)})");
            return new DupProfile { Estimated = true, Summary = "未找到 ffmpeg" };
        }
        try
        {
            double dur = await ProbeDurationSeconds(videoPath);
            double total = Math.Max(4, Math.Round(dur * Math.Max(1, srcFps)));
            int step = Math.Max(1, (int)Math.Round(total / 120.0));
            var raw = Path.Combine(Path.GetTempPath(), $"imgup_rhythm_{Guid.NewGuid():N}.raw");
            try
            {
                await RunAsync(ffmpeg,
                    $"-y -v error -i \"{videoPath}\" -vf \"select=not(mod(n\\,{step})),scale=64:36,format=gray\" -vsync 0 -f rawvideo -pix_fmt gray \"{raw}\"",
                    null, ct);
            }
            catch
            {
                AppLogger.Info($"预估失败:节奏采样命令失败({Path.GetFileName(videoPath)},step={step})");
                return new DupProfile { Estimated = true, Summary = "预估失败" };
            }
            if (!File.Exists(raw) || new FileInfo(raw).Length < 2 * 2304)
            {
                AppLogger.Info($"预估失败:采样帧数过少({Path.GetFileName(videoPath)},step={step})");
                return new DupProfile { Estimated = true, Summary = "帧数过少,无法预估" };
            }
            var bytes = File.ReadAllBytes(raw);
            int nf = bytes.Length / 2304;
            var sads = new double[nf - 1];
            for (int i = 0; i < nf - 1; i++)
            {
                int o1 = i * 2304, o2 = o1 + 2304, sum = 0;
                for (int k = 0; k < 2304; k++)
                {
                    int d = bytes[o1 + k] - bytes[o2 + k];
                    if (d < 0) d = -d;
                    sum += d;
                }
                sads[i] = sum / 2304.0;
            }
            // ===== 连续运动闸(实测数据定界):素材几乎没有"完全没变"的帧(相邻差 ≤0.15 占比 <5%)=
            // 每帧都在动(摇摄/走路/剪辑素材,如 12121121121211121 实测仅 3.1%)→ 不存在保持帧,
            // "拍型/事件间隔"推断不适用(会把连续运动错估成 3fps/删88%,用户实测)。直接给真实值。
            double nearRoot = sads.Count(v => v <= 0.15) / (double)Math.Max(1, sads.Length);
            if (nearRoot < 0.05)
            {
                AppLogger.Info($"预估:连续运动素材(近同帧仅 {nearRoot:P0})→ 内容≈{srcFps:0.##}fps,无重复可删 · {Path.GetFileName(videoPath)}");
                return new DupProfile
                {
                    Estimated = true, DupRatioPct = 0, ContentFps = srcFps,
                    Summary = $"预估:连续运动素材(每帧都在动),内容帧率 ≈{srcFps:0.##} fps,无重复可删",
                };
            }
            var sorted = sads.OrderBy(v => v).ToList();
            double med = sorted[sorted.Count / 2];
            double thrEv = Math.Max(1.4, med * 1.5);
            var evs = new System.Collections.Generic.List<int>();
            for (int i = 0; i < sads.Length; i++) if (sads[i] > thrEv) evs.Add(i);
            if (evs.Count < 2)
            {
                int near0 = sads.Count(v => v <= 0.8);
                double pctN = 100.0 * near0 / Math.Max(1, sads.Length);
                double cfN = Math.Max(0.5, srcFps * (1 - pctN / 100.0));
                AppLogger.Info($"预估:内容≈{cfN:0.##}fps(删{pctN:0}%)· {Path.GetFileName(videoPath)}");
                return new DupProfile
                {
                    Estimated = true, DupRatioPct = pctN, ContentFps = cfN,
                    Summary = $"预估:重复约 {pctN:0}%,内容帧率 ≈{cfN:0.##} fps",
                };
            }
            var gaps = new System.Collections.Generic.List<double>();
            for (int j = 1; j < evs.Count; j++) gaps.Add(evs[j] - evs[j - 1]);
            gaps.Sort();
            double gmed = Math.Max(1.4, gaps[gaps.Count / 2] * step);
            gmed = Math.Clamp(gmed, 1.4, Math.Max(2.0, srcFps / 2.0));
            double cf = Math.Max(0.5, srcFps / gmed);
            double pct = gmed > 1.4 ? (gmed - 1.0) / gmed * 100.0 : 0.0;
            AppLogger.Info($"预估:拍型≈每{gmed:0.#}帧(内容≈{cf:0.##}fps,删{pct:0}%)· {Path.GetFileName(videoPath)}");
            return new DupProfile
            {
                Estimated = true, DupRatioPct = pct, ContentFps = cf,
                Summary = $"预估:拍型≈每{gmed:0.#}帧(内容帧率 ≈{cf:0.##} fps),可删约 {pct:0}%",
            };
        }
        catch
        {
            AppLogger.Info($"预估失败:异常({Path.GetFileName(videoPath)})");
            return new DupProfile { Estimated = true, Summary = "预估失败" };
        }
    }

    /// <summary>供 VideoView 调用的入口:入列预估。现在【直接复用"选中后全文分析"的同一套检测器】
    /// (按当前去重模式,参考 AnalyzeDupAsync),保证"预估徽标 = 分析 = 处理结果"三个数字一致。
    /// 之前预估用粗糙的"变化像素占比"会虚高(如 92% vs 分析 8%),用户要求统一。</summary>
    public static Task<DupProfile> ProbeDupAsync(string videoPath,
        int dedupMode, double dedupAnimeThr, int dedupSmartMode, bool motionComp = true, bool dedupOnlyTrueHold = false,
        CancellationToken ct = default)
    {
        var ffmpeg = FfmpegPath;
        if (ffmpeg == null) return Task.FromResult(new DupProfile { Estimated = true, Summary = "未找到 ffmpeg" });
        return ProbeDupUnifiedAsync(ffmpeg, videoPath, dedupMode, dedupAnimeThr, dedupSmartMode, motionComp, dedupOnlyTrueHold, ct);
    }

    /// <summary>与"选中后分析"完全同口径的入列预估:复用 AnalyzeDupAsync 检测,仅标记为"预估"。</summary>
    private static async Task<DupProfile> ProbeDupUnifiedAsync(string ffmpeg, string videoPath,
        int dedupMode, double dedupAnimeThr, int dedupSmartMode, bool motionComp, bool dedupOnlyTrueHold, CancellationToken ct)
    {
        var p = await AnalyzeDupAsync(ffmpeg, videoPath, dedupMode, dedupAnimeThr, dedupSmartMode, motionComp, dedupOnlyTrueHold, ct);
        p.Estimated = true;
        p.Summary = "预估:" + p.Summary;
        return p;
    }

    /// <summary>供 VideoView 调用的入口:选中后全文分析(与处理同口径)。</summary>
    public static Task<DupProfile> AnalyzeDupAsync(string videoPath,
        int dedupMode, double dedupAnimeThr, int dedupSmartMode, bool motionComp = true,
        bool dedupOnlyTrueHold = false, CancellationToken ct = default)
    {
        var ffmpeg = FfmpegPath;
        if (ffmpeg == null) return Task.FromResult(new DupProfile { Summary = "未找到 ffmpeg" });
        return AnalyzeDupAsync(ffmpeg, videoPath, dedupMode, dedupAnimeThr, dedupSmartMode, motionComp, dedupOnlyTrueHold, ct);
    }

    // ===== 内容帧率估计(智能模式自动的内容帧率化用):scdet 逐帧评分 → 变化事件间隔 → 内容帧率 = 输入帧率/平均间隔 =====
    /// <summary>内容帧率是否为"常见拍数"(8/10/12/15/20/24/30fps ±8%)——保守档用:
    /// 只有识别结果落在常见值附近才信任(否则可能把连续运动误估成奇怪拍数)。</summary>
    private static bool IsCommonContentFps(double fps, double inFps)
    {
        if (fps <= 0) return false;
        // 先按绝对常见值,再按"输入帧率的整数分频"(30→15/10/7.5/6、24→12/8/6、60→30/20/15)
        double[] common = { 8, 10, 12, 15, 20, 24, 30, 60 };
        foreach (var c in common)
            if (Math.Abs(fps - c) <= c * 0.08) return true;
        // 整数分频:inFps/N (N=1..6) 附近也算(拍N的常见形态)
        for (int n = 1; n <= 6; n++)
        {
            double c = inFps / n;
            if (Math.Abs(fps - c) <= c * 0.08) return true;
        }
        return false;
    }

    /// <summary>内容帧率估计结果(智能模式内部使用;手动模式由用户手填,不经此估计器)。</summary>
    private sealed class ContentFpsInfo
    {
        public double Fps { get; set; }          // 内容帧率(0=无法估计)
        public int Period { get; set; }          // 估计的"内容帧间隔"(输入帧数,0=不可靠)
        public double Confidence { get; set; }   // 0..1
        public string Summary { get; set; } = "";
    }

    /// <summary>内容帧率估计核心:ffmpeg scdet 滤镜逐帧输出 lavfi.scd.score(相邻帧归一化差异)。
    /// 保持帧(一拍二/拍三)≈0,内容切换帧≈0.1+ → 变化事件 = score>阈值;事件间隔=内容帧间隔(帧数)
    /// → fc = 输入帧率 ÷ 平均间隔。置信度 = 间隔一致性 × 间隔稳定性(1 - 变异系数)。</summary>
    private static async Task<ContentFpsInfo> EstimateContentFpsWithAsync(string ffmpeg, string videoPath, CancellationToken ct)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        double inFps = 30;
        if (double.TryParse(ProbeFps(videoPath), System.Globalization.NumberStyles.Float, inv, out var f0) && f0 > 0)
            inFps = f0;
        try
        {
            // scdet 滤镜:每帧输出 lavfi.scd.score(新版 ffmpeg 已移除 scene 滤镜;无 scdet 时回退 select-scene)
            var lines = await RunCaptureAsync(ffmpeg,
                $"-y -i \"{videoPath}\" -vf \"scdet=threshold=0,metadata=print\" -f null NUL", ct);
            var scores = new System.Collections.Generic.List<double>();
            foreach (var l in lines)
            {
                var m = System.Text.RegularExpressions.Regex.Match(l, @"lavfi\.scd\.score=([\d.]+)");
                if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, inv, out var s))
                    scores.Add(s);
            }
            if (scores.Count == 0)
            {
                lines = await RunCaptureAsync(ffmpeg,
                    $"-y -i \"{videoPath}\" -vf \"select='gt(scene,-1)',metadata=print\" -f null NUL", ct);
                foreach (var l in lines)
                {
                    var m = System.Text.RegularExpressions.Regex.Match(l, @"lavfi\.scene_score=([\d.]+)");
                    if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, inv, out var s))
                        scores.Add(s);
                }
            }
            if (scores.Count < 4)
                return new ContentFpsInfo { Summary = "帧数过少,无法估计内容帧率" };

            // 变化事件:score > 阈值(自适应:0.02 事件太少则逐级放宽到 0.01/0.005)
            System.Collections.Generic.List<int> events = new();
            foreach (double thr in new[] { 0.02, 0.01, 0.005 })
            {
                events.Clear();
                for (int i = 0; i < scores.Count; i++)
                    if (scores[i] > thr) events.Add(i);
                if (events.Count >= 4) break;
            }
            if (events.Count < 4)
                return new ContentFpsInfo
                {
                    Fps = 0, Confidence = 0.3,
                    Summary = "素材几乎无变化(整段接近静止)",
                };
            // 事件间隔(帧数):转场/长静止镜头会产生离群间隔 → 以中位数为锚裁剪 [0.5m, 2m]
            var gaps = new System.Collections.Generic.List<double>();
            for (int j = 1; j < events.Count; j++) gaps.Add(events[j] - events[j - 1]);
            gaps.Sort();
            double med = gaps[gaps.Count / 2];
            var trimmed = gaps.Where(g => g >= med * 0.5 && g <= med * 2.0).ToList();
            if (trimmed.Count < 3) trimmed = gaps;
            double meanGap = trimmed.Average();
            if (meanGap < 1.2)
                return new ContentFpsInfo
                {
                    Fps = inFps, Confidence = 0.25,
                    Summary = "素材几乎连续运动(无保持帧,内容帧率≈输入帧率)",
                };
            double fc = Math.Clamp(inFps / meanGap, 0.5, inFps);
            // 置信度 = 间隔一致性(±1 帧内占比) × 间隔稳定性(1 - 变异系数):
            // 1拍N 素材间隔几乎定值(σ/μ 小)→ 高;间隔忽大忽小(1/2/3 混杂)→ 低
            int near = trimmed.Count(g => Math.Abs(g - meanGap) <= 1.0);
            double meanSq = trimmed.Sum(g => (g - meanGap) * (g - meanGap)) / Math.Max(1, trimmed.Count);
            double sigma = Math.Sqrt(meanSq);
            double cv = meanGap > 0 ? sigma / meanGap : 1.0;
            double conf = Math.Clamp(
                (double)near / Math.Max(1, trimmed.Count) * Math.Max(0.0, 1.0 - cv)
                * (double)trimmed.Count / Math.Max(1, gaps.Count), 0, 1);
            int period = (int)Math.Round(meanGap);
            string confTxt = conf >= 0.7 ? "高" : conf >= 0.45 ? "中" : "低";
            return new ContentFpsInfo
            {
                Fps = fc, Period = period, Confidence = conf,
                Summary = $"内容节奏≈{fc:0.##} fps(间隔≈{meanGap:0.##} 帧,置信度{confTxt})",
            };
        }
        catch (Exception ex)
        {
            return new ContentFpsInfo { Summary = "估计失败:" + ex.Message };
        }
    }

    // ===== 分段内容帧率化(智能/动漫/手动共有):转场切段 → 每段自适应估计内容间隔 → 段内网格保留 =====
    private sealed class SegmentFpsResult
    {
        public int UsedSegs;
        public int Kept;
        public double EffFps;
        public string Note = "";
        public System.Collections.Generic.List<int> KeptSrcIdx = new();   // 保留帧的源帧号(升序,节奏重采样时间戳用)
    }

    /// <summary>分段内容帧率化包装:全量拆帧后按段处理(不逐帧判重)。userInterval&gt;0 = 用户声明的间隔
    /// (动漫档/手动值,段估计在容差内才采用);=0 = 纯自动(智能,按置信度门槛)。</summary>
    private static async Task<(int frameCount, double effectiveFps, System.Collections.Generic.List<int> srcIdx)> RunSegmentContentFpsAsync(string ffmpeg,
        string inputVideo, string trimArgs, string scaleVf, string framesIn, int origCountEst,
        bool vfrPassthrough, double inFps, double userInterval, double userTol, double autoConf, string modeNote,
        IProgress<(int pct, string msg)>? progress, CancellationToken ct, bool forceGrid = false, bool phaseAlign = true)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        progress?.Report((3, $"{modeNote}:全量拆帧 + 去重(整片统一判定;随后展开时间轴+标准补帧)..."));
        int frameCount = await ExtractFramesCoreAsync(ffmpeg, inputVideo, trimArgs,
            scaleVf, framesIn, progress, ct, origCountEst, vfrPassthrough);
        // 用户定案:去重线【不分段】——分段(转场切段)只会把"全场等距"打成"段间有落差"
        // → 被迫走补缺慢路+段间不一致;去重=全片一个算法/网格(拍型/节奏是全局的)。
        // 「转场识别」仍独立(补帧时勾选才用),与去重互不干扰。
        var segs = new System.Collections.Generic.List<(int s, int e)> { (0, frameCount) };
        // 段级分析(逐帧解码小图+SAD/网格)→ CPU 重活放后台线程,防"拆帧完卡一下"
        var res = await Task.Run(() => SegmentContentFpsCoreSync(framesIn, frameCount, inFps,
            segs, userInterval, userTol, autoConf, progress, forceGrid, phaseAlign), ct);
        // 统一为"去重完成:"前缀:界面日志区只认这个前缀(旧逐帧算法同款格式),保证能看到删了多少帧
        int del = Math.Max(0, frameCount - res.Kept);
        double pct = 100.0 * del / Math.Max(1, frameCount);
        progress?.Report((5, $"去重完成:{frameCount}→{res.Kept} 帧,删 {del} 帧({pct:0.0}%),{res.Note} · 拆帧 {sw.Elapsed.TotalSeconds:0.#}s"));
        return (res.Kept, res.EffFps, res.KeptSrcIdx);
    }

    /// <summary>转场切段:scene 评分(阈值 0.3;转场显著高于内容切换),返回段边界。</summary>
    private static async Task<System.Collections.Generic.List<(int s, int e)>> DetectFpsSegmentsAsync(
        string ffmpeg, string framesIn, int frameCount, CancellationToken ct)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var cuts = new System.Collections.Generic.List<int>();
        try
        {
            var lines = await RunCaptureAsync(ffmpeg,
                $"-y -framerate 1 -i \"{Path.Combine(framesIn, "frame_%06d.png")}\" " +
                $"-vf \"select='gt(scene,0.3)',metadata=print\" -f rawvideo NUL", ct);
            foreach (var l in lines)
            {
                var m = System.Text.RegularExpressions.Regex.Match(l, @"pts_time:(\d+(?:\.\d+)?)");
                if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, inv, out var pts))
                    cuts.Add((int)Math.Round(pts));
            }
            cuts.RemoveAll(c => c <= 0 || c >= frameCount);
            cuts.Sort();
        }
        catch { /* 检测失败按整段处理 */ }
        var segs = new System.Collections.Generic.List<(int s, int e)>();
        int segStart = 0;
        foreach (var c in cuts)
        {
            if (c > segStart) segs.Add((segStart, c));
            segStart = c;
        }
        if (segStart < frameCount) segs.Add((segStart, frameCount));
        if (segs.Count == 0) segs.Add((0, frameCount));
        return segs;
    }

    /// <summary>分段内容帧率化核心(在已拆帧序列上,后台线程执行):每段"节奏网格 + 变化帧保护",
    /// 段内保留内容帧;找不准节奏的段仅删真静止帧。</summary>
    private static SegmentFpsResult SegmentContentFpsCoreSync(string framesIn, int frameCount,
        double inFps, System.Collections.Generic.List<(int s, int e)> segs,
        double userInterval, double userTol, double autoConf,
        IProgress<(int pct, string msg)>? progress, bool forceGrid = false, bool phaseAlign = true)
    {
        var files = Directory.EnumerateFiles(framesIn, "*.png")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        var keep = new System.Collections.Generic.HashSet<int>();
        int used = 0;
        var segNotes = new System.Collections.Generic.List<string>();

        // ← 手动-内容帧率采样(大道至简):按用户填写的真实内容帧率均匀抽帧,
        //   不做任何逐帧判定(二次剪辑/帧率乱套素材由用户给真值,我们只照做)。
        //   forceGrid=专有标志(与间隔大小无关:20.84fps 源填内容 10 → 间隔 2.08 ≤2.5
        //   曾误入 0.8 判据去重 → "选10不去重";现在内容帧率模式恒走采样)。
        //   【转场识别全模式生效】:网格按段(转场切段)单独起算——每段从段首重启网格,
        //   相位按段对齐(剪辑到新场景不会继承上一段的错拍),转场处采样段首帧(转场保帧)。
        if (forceGrid)
        {
            double step = Math.Max(1.05, userInterval);   // 调用方已换算:inFps/userFc
            foreach (var (s, e) in segs)
            {
                // 相位自动对齐:素材保持帧起点带相位偏移时,网格从真实相位起算(高置信才移相)
                int ph = phaseAlign ? EstimateGridPhase(files, s, e, step) : 0;
                if (ph != 0)
                    AppLogger.Info($"相位对齐:段[{s},{e}) 起算点偏移 {ph} 帧(高置信确认,已从真实保持帧起算)");
                var segKeep = new System.Collections.Generic.HashSet<int>();
                for (double t = s + ph; t < e; t += step) segKeep.Add((int)Math.Round(t));
                segKeep.Add(s);
                int lastK = -1;
                foreach (var f in segKeep) if (f > lastK) lastK = f;
                if (e - 1 - lastK > step * 0.5) segKeep.Add(e - 1);
                foreach (var f in segKeep) keep.Add(f);
                keep.Add(e - 1);   // 尾帧恒保留:结尾画面组(88889999 的 9)即使被网格跳过也不丢
                // 进度文案(大白话):告诉用户"删了多少帧、内容帧率≈多少",而不是术语"网格抽帧"
                int delCnt = Math.Max(0, (e - s) - segKeep.Count);
                double cf = inFps / step;
                progress?.Report((4, $"去重|[{s},{e}) 删 {delCnt} 帧,保留 {segKeep.Count} 帧(内容帧率≈{cf:0.###} fps)" + (ph > 0 ? $"(相位 {ph})" : "")));
            }
            AppLogger.Info($"分段|手动内容帧率采样:间隔 {step:0.###} 帧,段数 {segs.Count},抽存 {keep.Count}/{frameCount}(相位对齐:{(phaseAlign ? "开" : "关")})");
            // 档位校验:内容帧率模式填低(实拍被抽稀)时,内容帧对差异会很大 → 提示用户(只提示,不代改)
            try
            {
                var keptIdxList = keep.OrderBy(i => i).ToList();
                int pairN = 0; double meanSad = 0;
                var prevG = SampleGray(files[keptIdxList[0]], 16, out var _, out var _);
                for (int i = 1; i < keptIdxList.Count && pairN < 24; i++)
                {
                    var cur = SampleGray(files[keptIdxList[i]], 16, out _, out _);
                    meanSad += MeanAbsDiff(prevG, cur);
                    pairN++;
                    prevG = cur;
                }
                if (pairN >= 3)
                {
                    meanSad /= pairN;
                    if (meanSad > 3.5)
                    {
                        progress?.Report((4, $"⚠ 档位校验:相邻内容帧平均差异 {meanSad:0.#}(较大)——若素材为实拍/高内容帧率,该内容帧率可能把画面抽稀;仍按本次填写处理"));
                        AppLogger.Info($"⚠ 档位校验:相邻内容帧平均差异 {meanSad:0.#}(较大) → 若素材为实拍/高内容帧率,该内容帧率可能把画面抽稀;仍按本次填写处理");
                    }
                }
            }
            catch { /* 校验失败忽略 */ }
        }

        if (!forceGrid)
        for (int si = 0; si < segs.Count; si++)
        {
            var (s, e) = segs[si];
            if (forceGrid) continue;   // 理论不可达(网格模式不进循环)
            int len = e - s;
            if (len < 6)
            {
                for (int i = s; i < e; i++) keep.Add(i);
                continue;
            }
            // 智能 = 专门识别拍数(大道至简):只用两个判据——
            // ① SAD16(16px 小图)找"大变化事件"(节奏确认用);
            // ② histDiff(蓝通道 4 步采样+均衡化)当"安全闸":网格只删"真保持帧(均衡差≤0.8)",
            //    微差帧(呼吸/微动=时间流逝)绝不丢。无固定节奏的段 = 真人连续运动 → 原样保留,一帧不删。
            var prevFull = LoadFullGray(files[s], out var wF, out var hF, out var prevBlue4, out var bw4, out var bh4);
            var prev16 = SampleFrom(prevFull, wF, hF, 16, out var sw16, out var sh16);
            var prevEq4 = EqualizeHist(prevBlue4);
            var sads = new double[len - 1];
            var histds = new double[len - 1];
            for (int i = s + 1; i < e; i++)
            {
                var curFull = LoadFullGray(files[i], out wF, out hF, out var curBlue4, out bw4, out bh4);
                var cur16 = SampleFrom(curFull, wF, hF, 16, out sw16, out sh16);
                var curEq4 = EqualizeHist(curBlue4);
                int k = i - s - 1;
                sads[k] = MeanAbsDiff(prev16, cur16);
                histds[k] = MeanAbsDiff(prevEq4, curEq4);
                prev16 = cur16; prevFull = curFull; prevEq4 = curEq4;
            }
            // ===== 节奏确认(变化帧应呈固定间隔)=====
            var sorted = sads.OrderBy(v => v).ToList();
            double med = sorted[sorted.Count / 2];
            double thrEv = Math.Max(1.4, med * 1.5);      // 大 diff 阈值(变化帧)
            var eventsIdxs = new System.Collections.Generic.List<int>();
            for (int i = 0; i < sads.Length; i++) if (sads[i] > thrEv) eventsIdxs.Add(i);
            // 节奏确认:大 diff 间隔与档位周期吻合(≥60% 事件落在同一相位)才算"拍N 成立"
            double pConfirmed = 0;
            foreach (double pc in userInterval > 0
                ? new[] { userInterval }
                : new[] { 2.0, 2.5, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0 })
            {
                if (eventsIdxs.Count < 2) break;
                var groups = eventsIdxs.GroupBy(x => ((x % pc) + pc) % pc).ToList();
                int bestN = 0;
                foreach (var g in groups) bestN = Math.Max(bestN, g.Count());
                if ((double)bestN / eventsIdxs.Count >= 0.6) { pConfirmed = pc; break; }
            }
            // ===== 合并去重(双素材仿真验证):=============
            // 删 = 组判定标记(节奏保持段)∩ 三闸安全集(像素级安全);
            // ① 安全集:三闸全过(与口型保护同判据,任何局部运动被 maxD 挡住);
            // ② 组判定:拍N 节奏检测骨架(独立实现)——40x40 级均衡差
            //    路径和代理光流距离,相对判据 d1<d0&&d1<d2 + count 公式;
            //    裸组判定会在混合/真人素材大量误删(实测转头口型 10/10 全灭),
            //    必须 ∩ 安全集(实测误删归零);
            // ③ 守护:删除比例 >45% 时回退为"仅安全集"(=原三闸行为)。
            // 安全集/组判定 = 负优化已移除(智能=专门识别拍数)。
            var segKeep = new System.Collections.Generic.HashSet<int>();
            int histKeepCnt = 0, histDelCnt = 0, groupMarksCnt = 0;
            if (pConfirmed > 0)
            {
                // ===== 节奏确认 → 拍型网格(只删"真保持帧",微差帧绝不丢) =====
                // 网格决定"内容帧位置";删除只允许发生在"网格外 AND 均衡差≤0.8(=真保持/复制帧)"。
                // 网格外但差异大的帧(呼吸/眼球/微动 = 时间在流逝)→ 保留,绝不抽稀动作。
                // → 拍二/拍三(复制帧)删干净;微差实拍(测试2 类)动作信息不丢。
                int st = Math.Max(2, (int)Math.Round(pConfirmed));
                var phCnt = new int[st];
                foreach (var ev in eventsIdxs) phCnt[((ev % st) + st) % st]++;
                int bestPh = 0;
                for (int p2 = 1; p2 < st; p2++) if (phCnt[p2] > phCnt[bestPh]) bestPh = p2;
                var gridSet = new System.Collections.Generic.HashSet<int>();
                for (double t = s + bestPh; t < e; t += pConfirmed) gridSet.Add((int)Math.Round(t));
                for (int i = s; i < e; i++)
                {
                    if (gridSet.Contains(i)) { segKeep.Add(i); continue; }
                    int k = i - s - 1;
                    if (k >= 0 && k < histds.Length && histds[k] > 0.8) segKeep.Add(i);   // 微差帧=变化帧→保留
                }
                segKeep.Add(s);
                segKeep.Add(e - 1);
                histKeepCnt = segKeep.Count;   // 仅日志口径
                groupMarksCnt = 0;
            }
            else
            {
                // ===== 无固定节奏 = 真人/连续运动素材:原样保留,一帧不删 =====
                for (int i = s; i < e; i++) segKeep.Add(i);
            }
            if ((double)segKeep.Count / len >= 0.97)
            {
                // 几乎全是变化帧:连续运动段,无重复可删 → 原样保留(防误删细节)
                for (int i = s; i < e; i++) keep.Add(i);
                AppLogger.Info($"去重|[{s},{e}) 节奏未确认,原样保留({segKeep.Count}/{len})");
                continue;
            }
            foreach (var i2 in segKeep) keep.Add(i2);
            used++;
            var keptIdx = segKeep.OrderBy(i3 => i3).ToList();
            double avgGap = keptIdx.Count >= 2 ? (double)(keptIdx[^1] - keptIdx[0]) / (keptIdx.Count - 1) : 1;
            double fc = inFps / Math.Max(1.2, avgGap);
            string pTxt = pConfirmed > 0 ? $"拍{Math.Round(pConfirmed)}" : "无固定节奏(原样保留)";
            progress?.Report((4, $"去重|[{s},{e}) 自动识别:{pTxt},删保持帧 {len - segKeep.Count}/{len},内容≈{fc:0.#}fps"));
            segNotes.Add($"段{si + 1}:{pTxt},删保持帧 {len - segKeep.Count}/{len},内容≈{fc:0.#}fps");
            AppLogger.Info($"去重|[{s},{e}) 自动识别拍数={pConfirmed:0.##},删保持帧:{segKeep.Count}/{len},内容帧率≈{fc:0.#}fps");
        }
        // 3) 删除非保留帧并重命名(保持连续帧号)
        if (keep.Count < frameCount)
        {
            int idx = 0;
            for (int n = 0; n < frameCount; n++)
            {
                if (keep.Contains(n))
                {
                    idx++;
                    if (idx != n + 1)
                        File.Move(files[n], Path.Combine(framesIn, $"frame_{idx:D6}.png"), true);
                }
                else
                {
                    try { File.Delete(files[n]); } catch { }
                }
            }
        }
        int keptCount = Directory.EnumerateFiles(framesIn, "*.png").Count();
        double eff = keptCount > 0 && frameCount > 0 ? inFps * keptCount / frameCount : inFps;
        string note = keptCount >= frameCount
            ? $"未发现可压缩冗余(原样保留),有效帧率 {eff:0.##} fps"
            : $"有效帧率 {eff:0.##} fps";
        // 注:去重全片一体处理(无分段);展开+标准补帧在补帧阶段完成。
        // 注意:段级细节(每段删多少/内容≈Xfps)已逐段实时上报界面,此处不再堆长文本。
        var res = new SegmentFpsResult { UsedSegs = used, Kept = keptCount, EffFps = eff, Note = note };
        res.KeptSrcIdx.AddRange(keep.OrderBy(i => i));
        return res;
    }

    /// <summary>硬链接(kernel32):零拷贝创建同一文件的新路径(展开序列复用内容帧,不占额外磁盘)。</summary>
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, System.IntPtr lpSecurityAttributes);

    private static void TryCreateHardLink(string dst, string src)
    {
        if (!CreateHardLinkW(dst, src, System.IntPtr.Zero))
            throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
    }

    /// <summary>网格相位估计(相位自动对齐):段内相邻帧 SAD16,取"大变化事件"按整拍 st 取模的众数相位。
    /// 高置信才返回偏移 ph(否则 0=从段首起算):仅整拍(2/3/4)有意义(混合 2.5/半拍 1.6 无固定相位,跳过);
    /// 事件≥3 且事件间隔中位数≈档位(节奏确实吻合)且众数占比≥60%(相位集中)三关全过才移相。
    /// 估算窗=段首最多 max(18, st×10) 帧(新拍型通常段首即稳定;窗小省 CPU)。</summary>
    private static int EstimateGridPhase(string[] files, int s, int e, double step)
    {
        int st = (int)Math.Round(step);
        if (st < 2 || Math.Abs(step - st) > 0.35) return 0;       // 仅整拍可对齐
        int len = e - s;
        if (len < st * 3 + 4) return 0;                           // 段太短,无统计意义
        int w = Math.Min(len - 1, Math.Max(18, st * 10));
        var sads = new double[w];
        var prev = SampleGray(files[s], 16, out var sw, out var sh);
        for (int i = 1; i <= w; i++)
        {
            var cur = SampleGray(files[s + i], 16, out sw, out sh);
            sads[i - 1] = MeanAbsDiff(prev, cur);
            prev = cur;
        }
        var sorted = sads.OrderBy(v => v).ToList();
        double med = sorted[sorted.Count / 2];
        double thrEv = Math.Max(1.4, med * 1.5);                  // 大变化阈值(与智能分支同口径)
        var evs = new System.Collections.Generic.List<int>();
        for (int i = 0; i < w; i++) if (sads[i] > thrEv) evs.Add(i);
        if (evs.Count < 3) return 0;                              // 事件不足(纯静止/连续运动)不硬猜
        var gaps = new System.Collections.Generic.List<double>();
        for (int j = 1; j < evs.Count; j++) gaps.Add(evs[j] - evs[j - 1]);
        double gmed = gaps.OrderBy(g => g).ElementAt(gaps.Count / 2);
        if (Math.Abs(gmed - step) > 1.0) return 0;                // 实际节奏与档位不符(档位选错/变拍)不硬套
        var counts = new int[st];
        foreach (var ev in evs) counts[((ev % st) + st) % st]++;
        int best = 0, bestCnt = 0;
        for (int p = 0; p < st; p++) if (counts[p] > bestCnt) { bestCnt = counts[p]; best = p; }
        if ((double)bestCnt / evs.Count < 0.6) return 0;          // 相位分散=无固定拍,保持原样
        AppLogger.Info($"去重|[{s},{e}) 相位对齐:相位 {best},置信 {bestCnt}/{evs.Count}(间隔中位 {gmed:0.##})");
        return best;
    }

    /// <summary>按源时间轴任意 t 插帧(保持原时间轴):输出网格 = 源帧率×倍率,
    /// 每个输出槽落在源轴上,内容帧对之间的槽由 RIFE 单对 -s 任意 t 精确插值。</summary>
    /// 用 RIFE 任意时间步直插(-s,量化 φ 分桶,每桶一次引擎批)在每个关键帧对间的**精确 t** 生成中间帧。
    /// 时空重采样(慢段密插/快段疏插/时长=原),独立实现,不依赖第三方任意 t 接口。
    /// (旧实现为 0.5 二分级联:只能 dyadic 时刻 + 多层累计误差;直接 -s 一次到位,快且准。)</summary>
    private static async Task<(int frameCount, double outFps)> RunTempoResampleAsync(string rife,
        string framesIn, string framesFinal, int frameCount, double inFps,
        System.Collections.Generic.List<int>? srcIdx, int interpScale, double? targetFps,
        int gpuId, double srcDur, string interpModel, bool tta,
        IProgress<(int pct, string msg)>? progress, CancellationToken ct)
    {
        var files = Directory.EnumerateFiles(framesIn, "*.png")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        int n = files.Length;
        if (n < 2)
        {
            foreach (var f in files) File.Copy(f, Path.Combine(framesFinal, Path.GetFileName(f)), true);
            return (n, inFps);
        }
        var idx = srcIdx ?? Enumerable.Range(0, n).ToList();
        // 关键帧源时刻:按保留帧在原源轴上的位置,归一化到"源视频已处理时长" srcDur(避免尾部静止段截断时间轴)
        double T = srcDur > 0.01 ? srcDur : (n > 1 ? (double)idx[^1] / inFps : 1.0 / 30);
        if (T <= 0.01) T = 1.0 / 30;
        double TimeOf(int i) => T * (double)idx[i] / Math.Max(1, idx[^1]);
        double F = targetFps ?? inFps * interpScale;
        // 输出帧数 = 源轴真实帧数(idx 跨度,不再 round(T×F)+1:对整帧率会多 1 帧 → ×倍率后多出 1 拍 → 裁尾="少几帧")
        int outN = Math.Max(2, idx[^1] + 1);
        // 每输出槽 → 源帧路径:端点直接映射;中间槽逐槽单对 -s 精确时间步。
        // 关键教训(rife-ncnn-vulkan):【目录模式忽略 -s】(-s 只对单对 -0/-1/-o 有效);
        // -n 的输出含端点且中间帧数不稳定(实测 -n 6 = [A,3中间,B,B]);故任意 t
        // 唯一可靠原语 = 单对 -s 逐槽调用(每槽一次引擎进程,~0.4s/槽无 TTA)。
        var slotSrc = new string[outN];
        var slots = new List<(int i, int j, double phi)>();
        // P5 静止保护:两端帧完全相同(字节级)的帧对 → 所有槽位=左端点,不进引擎
        var pairEqCache = new Dictionary<int, bool>();
        bool PairEq(int i)
        {
            if (pairEqCache.TryGetValue(i, out var v)) return v;
            v = FilesEqual(files[i], files[i + 1]);
            pairEqCache[i] = v;
            return v;
        }
        for (int j = 0; j < outN; j++)
        {
            double t = T * j / (outN - 1);
            int i = 0;
            while (i < n - 2 && TimeOf(i + 1) <= t) i++;
            if (PairEq(i)) { slotSrc[j] = files[i]; continue; }
            double t0 = TimeOf(i), t1 = TimeOf(i + 1);
            double phi = Math.Clamp(t1 > t0 ? (t - t0) / (t1 - t0) : 0, 0, 1);
            if (phi <= 0.001) slotSrc[j] = files[i];
            else if (phi >= 0.999) slotSrc[j] = files[i + 1];
            else slots.Add((i, j, phi));
        }
        var tempoTempDirs = new System.Collections.Generic.List<string>();
        int slotDone = 0;
        // ===== 提速方案(每层一次引擎调用,替代逐槽 -s,快约 20~50 倍)=====
        // 每个内容帧对内部的槽 phi 是"均匀细分"(槽在源帧网上,DIST 有限)→ 用 dyadic 树
        // 生成过采样网格(4 层 = 16 格,误差 ≤1/16,视觉无差),每个槽按 phi 就近取帧:
        // 时间轴=精确源位置,画面=最近 dyadic 插值。引擎调用:每层一次层批(共 ≤4 次)。
        var activePairs = slots.Select(s => s.i).Distinct().ToList();
        var pairMids = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<(double phi, string file)>>();
        if (slots.Count > 0)
        {
            try
            {
            // 每帧对按"实际需要的深度"分层(不再全局 4 层过采样):
            // K=帧对输出帧数(=距×倍率);depth=ceil(log2 K);每对独立深度(浅对不浪费)。
            // 【安全硬上限 4】最多 2^4-1=15 个中间帧/对——dyadic 4 层误差 ≤1/16,视觉无差,
            // 超长静止段(gap 巨大)不再生成 63 张中间 PN G(省时省磁盘,够用)。
            // 引擎启动次数=深度组数(碎片化素材依然多次启动,但引擎日志已节流,不再刷屏)。
            double scaleF = Math.Max(1.0, F / Math.Max(1.0, inFps));
            var depthGroups = activePairs
                .Select(p =>
                {
                    int dist = Math.Max(1, idx[p + 1] - idx[p]);
                    int k = Math.Max(2, (int)Math.Round(dist * scaleF));
                    int d = 1;
                    while ((1 << d) < k) d++;
                    d = Math.Min(d, 4);
                    return (p, d);
                })
                .OrderBy(g => g.d)
                .GroupBy(g => g.d)
                .ToList();
            var workTmp = Path.Combine(Path.GetTempPath(), "imgup_tempo_layers", Guid.NewGuid().ToString("N"));
            tempoTempDirs.Add(workTmp);
            foreach (var group in depthGroups)
            {
                int depth = group.Key;
                var curNodes = group.Select(g => (p: g.p, phi0: 0.0, a: files[g.p], phi1: 1.0, b: files[g.p + 1])).ToList();
                int slotTotal = Math.Max(1, slots.Count);
                int midNeed = group.Count() * ((1 << depth) - 1);   // 该组树的总中间帧数(映射槽进度用)
                int midDone = 0;
                for (int lv = 1; lv <= depth && curNodes.Count > 0; lv++)
                {
                    // 分块批(每块一次引擎进程):块间按真实生成比例上报"已处理 X/总 Y 帧"(逐帧可感)
                    const int LayerBatch = 384;
                    var nextNodes = new System.Collections.Generic.List<(int p, double phi0, string a, double phi1, string b)>();
                    for (int off = 0; off < curNodes.Count; off += LayerBatch)
                    {
                        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                        var batch = curNodes.Skip(off).Take(LayerBatch).ToList();
                        // 层批内逐帧进度:轮询输出文件数 → 映射到全局"已处理 X/共 Y 帧"
                        IProgress<(int pct, string msg)>? layerProg = progress == null ? null
                            : new System.Progress<(int pct, string msg)>(lt =>
                            {
                                var m = System.Text.RegularExpressions.Regex.Match(lt.msg, @"第\s*(\d+)\s*帧");
                                if (!m.Success) { progress.Report(lt); return; }
                                int k = int.Parse(m.Groups[1].Value);
                                // 层批每层:每帧对生成 1 张中间帧 → 一批 batch.Count 对 = batch.Count 张;
                                // 旧代码 /(batch.Count*3) 把进度低估 3 倍 → 进度条长时间不动
                                int gf = Math.Min(slotTotal, (int)((double)k / Math.Max(1, batch.Count) * slotTotal));
                                progress.Report((10 + (int)(35.0 * gf / slotTotal),
                                    $"按源时间轴插帧 已处理 {gf} 帧 / 共 {slotTotal} 帧(源 {n} 帧·目标 {F:0.##} fps)"));
                            });
                        var mids = await EngineService.InterpLayerBatchAsync(rife,
                            batch.Select(nd => (nd.a, nd.b)),
                            Path.Combine(workTmp, $"D{depth}_L{lv}_{off / LayerBatch}"), gpuId, ct, interpModel, tta,
                            progress: layerProg, watchStage: "按源时间轴插帧");
                        for (int k = 0; k < batch.Count; k++)
                        {
                            var nd = batch[k];
                            double midPhi = (nd.phi0 + nd.phi1) / 2;
                            string midF = mids[k];
                            if (!pairMids.TryGetValue(nd.p, out var list)) pairMids[nd.p] = list = new();
                            list.Add((midPhi, midF));
                            if (lv < depth)
                            {
                                nextNodes.Add((nd.p, nd.phi0, nd.a, midPhi, midF));
                                nextNodes.Add((nd.p, midPhi, midF, nd.phi1, nd.b));
                            }
                        }
                        midDone += batch.Count;
                        int fr = Math.Min(slotTotal, (int)((double)midDone / Math.Max(1, midNeed) * slotTotal));
                        progress?.Report((10 + (int)(35.0 * fr / slotTotal),
                            $"按源时间轴插帧 已处理 {fr} 帧 / 共 {slotTotal} 帧(源 {n} 帧·目标 {F:0.##} fps)"));
                    }
                    curNodes = nextNodes;
                }
            }
            // 槽按 phi 就近取树中 dyadic 帧
            foreach (var (i, j, phi) in slots)
            {
                string? best = null; double bestErr = double.MaxValue;
                if (pairMids.TryGetValue(i, out var ms))
                    foreach (var (p, f) in ms)
                    {
                        double err = Math.Abs(p - phi);
                        if (err < bestErr) { bestErr = err; best = f; }
                    }
                slotSrc[j] = best ?? files[i + 1];
            }
            }
            catch
            {
                // 层批中途失败/取消:清掉临时层批目录(残留大量 PNG 会持续占盘)
                foreach (var d in tempoTempDirs) { try { Directory.Delete(d, true); } catch { } }
                throw;
            }
        }
        // 黑帧提示(GPU 队列异常兼容症状):层批中间帧有全黑 → 提示(自动重跑整段成本高;用户可换 CPU 设备重试)
        {
            int checkedN = 0;
            foreach (var (p, f) in pairMids.SelectMany(kv => kv.Value))
            {
                if (++checkedN > 6) break;
                try { if (EngineService.IsBlackPng(f)) { progress?.Report((40, "⚠ 补回输出含黑帧(GPU 队列异常),建议改用 CPU 设备或调小分块重试")); AppLogger.Info("⚠ 补回层批输出含黑帧(GPU 队列异常)— 建议改用 CPU 设备或调小分块后重试"); break; } } catch { }
            }
        }
        // 输出:按 j 顺序写帧(帧号连续)
        int written = 0;
        for (int j = 0; j < outN; j++)
            File.Copy(slotSrc[j], Path.Combine(framesFinal, $"frame_{++written:D6}.png"), true);
        // 输出写完才允许清理临时目录
        foreach (var d in tempoTempDirs) { try { Directory.Delete(d, true); } catch { } }
        double fps = written > 1 ? (written - 1) / T : F;
        AppLogger.Info($"按源时间轴插帧:关键帧 {n}(源号 {idx[0]}..{idx[^1]}) → 输出 {written} 帧 @ {fps:0.##} fps(目标 {F:0.##}),时长 {T:0.###}s,逐槽-s {slots.Count} 次(静止对 {pairEqCache.Count(e => e.Value)})");
        return (written, fps);
    }

    /// <summary>智能模式:自适应去重——不固定阈值,先算素材相邻帧差分布,再自动定"重复帧"分界。
    /// 高动态素材(前后差异大)能自动找到重复簇,精确去重;低动态素材自动收紧到只删"几乎完全相同",
    /// 防止像固定阈值那样把帧删光。判据:相邻帧差的中位数/分布自适应(低动态素材保守,只删几乎相同)+ 低动态保护。
    /// scale=采样粒度(px,全局可调):越细对动漫细线条/口型等细节越敏感。
    /// smartMode 策略:0=均衡(Otsu+低动态分支+段合并 0.95/4) 1=激进(阈值放宽,接近动漫/敏感)
    /// 2=保守(只删几乎相同+长静止段,微动不碰)。</summary>
    private static System.Collections.Generic.HashSet<int> DetectDupFramesAdaptive(string framesDir,
        IProgress<(int pct, string msg)>? progress, int scale = 16, int smartMode = 0, bool motionComp = true)
    {
        var drop = new System.Collections.Generic.HashSet<int>();
        var files = Directory.EnumerateFiles(framesDir, "*.png")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length < 2) return drop;
        // 第一遍:采样帧灰度,算相邻对统计标量(逐段报告,检测不会像卡死)
        var prev = SampleGray(files[0], scale, out var sw, out var sh);
        int pairCount = files.Length - 1;
        // ===== 滚动统计(不再全量驻留 grays,长视频内存从 ~800MB 降到 O(1) 标量) =====
        // 只存每相邻对的标量(SAD/SSIM/变化块/均块差/最大块差),第二遍直接套阈值,无需重解码、无需存帧。
        var sads = new double[pairCount];
        var sims = new double[pairCount];
        var crs = new double[pairCount];
        var avgs = new double[pairCount];
        var maxes = new double[pairCount];
        // 镜头运动补偿:每相邻对存"对齐后SAD/变化块占比"(仅在开启时计算,否则保持 0 → 第二遍不启用)
        var alignedSads = new double[pairCount];
        var alignedCrs = new double[pairCount];
        int nStaticAdj = 0;   // 近静态相邻对数量(估算静止段占比)
        for (int i = 1; i < files.Length; i++)
        {
            if ((i & 31) == 0)
                progress?.Report((3, $"去重分析 第 {i} 帧 / 共 {files.Length} 帧..."));
            var cur = SampleGray(files[i], scale, out sw, out sh);
            int p = i - 1;
            sads[p] = MeanAbsDiff(prev, cur);
            sims[p] = BlockSsim(prev, cur, sw, sh);
            var (cr, avg, mx) = FrameMotionStats(prev, cur, sw, sh);
            crs[p] = cr; avgs[p] = avg; maxes[p] = mx;
            if (motionComp)
            {
                var (_, _, alignedSad, chRatio) = EstimateGlobalShift(prev, cur, sw, sh);
                alignedSads[p] = alignedSad; alignedCrs[p] = chRatio;
            }
            if (sads[p] < 4.0 && sims[p] > 0.95) nStaticAdj++;
            prev = cur;
        }
        // ===== 智能 = 自适应算阈值(随素材变)+ 三档 = 你的"整体力度旋钮"(在自动结果上整体缩放) =====
        // 一个素材一个"基准参数组",由特征(中位数/重复占比/静止段)自动算;三档选择只是整体乘一个力度系数,
        // 让"自动适配"与"你的偏好"各司其职——不会出现"智能自己选完、你没得选"。
        int nearDupCnt = 0;
        foreach (var s in sads) if (s < 3) nearDupCnt++;
        double dupRatio = (double)nearDupCnt / Math.Max(1, sads.Length);
        var sortedSads = sads.OrderBy(s => s).ToList();
        double median = sortedSads[sortedSads.Count / 2];
        bool lowDynamic = median < 5;
        if (dupRatio < 0.12 && !lowDynamic) return drop;   // 真没重复:放弃(素材几乎帧帧都在动)

        // 自适应基准参数(随素材变化,不写死):动静越大,快筛阈值越宽;重复越多,删得越有条件(整体已加强)
        double sadThr = Math.Clamp(median * 1.1 + dupRatio * 3.0, 2.0, 5.5);
        double ssimThr = lowDynamic ? 0.94 : 0.95;
        // 变化块闸:均衡放宽(0.34/0.30)——拍2 素材"按拍重复"的帧间微动占比常见 0.2~0.35,
        // 闸太紧(0.18)会把真重复帧当"有动作"保留(截图:22.6fps 删不干净);局部动作(口型/眨眼,
        // 变化块远超 0.34)仍被保护保留。保守档维持紧闸防误删。
        double smartProtect = smartMode == 1 ? 0.45 : smartMode == 2 ? 0.22 : (lowDynamic ? 0.34 : 0.30);
        double segSsim = 0.92, segSad = 5.0;

        // 三档 = 整体力度系数(0.7 保守 / 1.0 均衡 / 1.5 激进):在"自适应基准"上整体放大/缩小删除倾向
        double force = smartMode switch { 1 => 1.5, 2 => 0.7, _ => 1.0 };
        sadThr = Math.Clamp(sadThr * force, 1.0, 7.0);
        // 只删真定格(与主判重一致):SSIM 阈值设 ≥0.99 下限(均衡档 0.99,激进 0.985,保守 0.995),
        // 相似但连续运动的帧不再被当重复删;人物定格交给"镜头运动补偿判据"(对齐残差极小)识别。
        // 注:上一版下限 0.995 过严 → 拍2素材只删到 22.6fps,现放宽到 0.99(拍N 重复帧结构相同度约 0.99x)。
        ssimThr = Math.Max(1.0 - (1.0 - ssimThr) * force, smartMode == 1 ? 0.985 : smartMode == 2 ? 0.995 : 0.99);
        smartProtect = Math.Clamp(smartProtect * force, 0.05, 0.60);
        segSsim = Math.Max(1.0 - (1.0 - segSsim) * force, smartMode == 1 ? 0.985 : smartMode == 2 ? 0.995 : 0.99);
        segSad = Math.Clamp(segSad * force, 2.0, 8.0);
        string forceName = smartMode switch { 1 => "激进", 2 => "保守", _ => "均衡" };
        // 静止段占比自适应:近静态相邻对占比 ≥25% 才启用段合并(防高动态素材误删)
        bool segOn = pairCount >= 5 && (double)nStaticAdj / Math.Max(1, pairCount) >= 0.25;
        progress?.Report((3, $"智能去重:自适应(重复占比 {dupRatio:0%},动态中位 {median:0.0}),力度:{(force == 1.0 ? "均衡" : forceName)} ×{force:0.#}{(segOn ? "+静止段合并" : "")}..."));

        // 第二遍:静止帧(帧差+SSIM+保护门禁)或镜头平移(全图均匀小动)判为重复帧;局部动作/大变化保留。
        // 核心:先看"谁在动"——整幅画面均匀移动=镜头运动(内容相同,删);只有局部轮廓动=角色动作(保留)。
        for (int i = 0; i < pairCount; i++)
        {
            bool isStatic = sads[i] < sadThr && sims[i] > ssimThr && crs[i] < smartProtect;
            bool isPan = lowDynamic
                ? (crs[i] > 0.7 && avgs[i] < 3 && maxes[i] < 8)
                : (crs[i] > 0.5 && avgs[i] < 8 && maxes[i] < 20);
            // 镜头运动补偿:背景持续 pan 时整帧 SAD/SSIM 到不了"相同",但先估相机平移并"对齐"后,
            // 残差极小+变化块占比极低 = 人物没动(定格/冗余)→ 判重删除;人物真动仍保留。
            bool motionCompHold = motionComp && alignedSads[i] < 2.5 && alignedCrs[i] < 0.08;
            if (isStatic || isPan || motionCompHold)
                drop.Add(i + 2);   // 第 i+2 帧(1-based)重复/镜头平移 → 删除
        }

        // 静止段合并(仅 segOn):相邻对全静的连续段 = 静止段,段内除首帧外删(用已算好的标量,不重解码)
        if (segOn && pairCount >= 3)
        {
            int runStart = -1;
            for (int i = 0; i < pairCount; i++)
            {
                bool staticPair = sads[i] < segSad && sims[i] > segSsim;
                if (staticPair)
                {
                    if (runStart < 0) runStart = i;
                    if (i - runStart >= 2) drop.Add(i + 2);
                }
                else runStart = -1;
            }
        }
        return drop;
    }

    /// <summary>手动-语义运动分析:静止帧(帧差小+SSIM 高)或镜头平移(全图均匀小动)判为冗余删掉;
    /// 局部动作(人物张嘴等块变化集中)与真实场景切换保留。panAvgThr=镜头运动阈值(1~10);
    /// maxDiffThr=镜头平移时单块最大差异上限(排除场景切换/爆炸);scale/blockThr=采样粒度/变化块判线。</summary>
    private static System.Collections.Generic.HashSet<int> DetectDupFramesWithMotion(string framesDir, double panAvgThr,
        IProgress<(int pct, string msg)>? progress, int scale = 16, double protect = 0.12, double blockThr = 4,
        double maxDiffThr = 20)
    {
        var drop = new System.Collections.Generic.HashSet<int>();
        var files = Directory.EnumerateFiles(framesDir, "*.png")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length < 2) return drop;
        var prev = SampleGray(files[0], scale, out var sw, out var sh);
        for (int i = 1; i < files.Length; i++)
        {
            if ((i & 31) == 0)
                progress?.Report((3, $"去重分析 第 {i} 帧 / 共 {files.Length} 帧..."));
            var cur = SampleGray(files[i], scale, out sw, out sh);
            double sad = MeanAbsDiff(prev, cur);
            // 关键防线(研究):maxDiff 高 = 有大块真的在动(小口型也会触发)→ 不判静止,避免误删微动帧
            var (cr, _, maxD) = FrameMotionStats(prev, cur, sw, sh, blockThr);
            // 静止帧:快筛阈值随"变化块判线"自适应(判线大 → 快筛放宽,与帧差+SSIM 算法联动);
            // 保护门禁同帧差+SSIM 算法
            double staticSad = Math.Max(1.5, 2.5 * blockThr / 4.0);
            bool isStatic = sad < staticSad && BlockSsim(prev, cur, sw, sh) > 0.955 && cr < protect && maxD < blockThr * 2;
            bool isPan = IsUniformMotion(prev, cur, sw, sh, false, Math.Clamp(panAvgThr, 1, 10), Math.Clamp(maxDiffThr, 10, 60));
            if (isStatic || isPan) drop.Add(i + 1);   // 第 i+1 帧(1-based)重复/镜头平移 → 删除
            prev = cur;
        }
        return drop;
    }

    /// <summary>镜头平移(背景滚动/摇移)判定:帧内容其实是同一画面,只是均匀位移。
    /// 判据:变化的块覆盖大半画面(ratio 高)、但每块差异都不大(均匀小移动)、且没有超大差异块
    /// (排除真实场景切换/爆炸等大变化);局部动作(人物张嘴)变化块比例低,不属于此,会保留。</summary>
    private static bool IsUniformMotion(byte[] a, byte[] b, int w, int h, bool lowDynamic, double panAvgThr = 8, double maxDiffThr = 20)
    {
        var (ratio, avg, max) = FrameMotionStats(a, b, w, h);
        // 低动态素材:只认"极均匀、极小"的平移(比一般素材更保守,防误删)
        if (lowDynamic)
            return ratio > 0.7 && avg < Math.Min(3, panAvgThr) && max < 8;
        // 变化覆盖大半画面 + 每块差异都小于敏感度阈值 + 没有超大突变(排除场景切换)
        return ratio > 0.5 && avg < panAvgThr && max < Math.Max(maxDiffThr, panAvgThr * 2.5);
    }

    /// <summary>块级运动统计:把缩略图分成 4×4 块,每块算与上一帧的平均绝对差。
    /// 返回 (变化块比例, 平均块差异, 最大块差异);变化块 = 块差异 &gt; blockThr(默认 4,手动可调)。</summary>
    private static (double changedRatio, double avgDiff, double maxDiff) FrameMotionStats(
        byte[] a, byte[] b, int w, int h, double blockThr = 4)
    {
        const int blocksX = 4, blocksY = 4;
        int bw = Math.Max(1, w / blocksX);
        int bh = Math.Max(1, h / blocksY);
        int changed = 0, total = blocksX * blocksY;
        long sum = 0;
        double max = 0;
        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                long s = 0; int cnt = 0;
                int x0 = bx * bw, x1 = Math.Min(w, (bx + 1) * bw);
                int y0 = by * bh, y1 = Math.Min(h, (by + 1) * bh);
                for (int y = y0; y < y1; y++)
                {
                    int row = y * w;
                    for (int x = x0; x < x1; x++)
                    {
                        s += Math.Abs(a[row + x] - b[row + x]);
                        cnt++;
                    }
                }
                double d = cnt > 0 ? (double)s / cnt : 0;
                sum += (long)d;
                if (d > max) max = d;
                if (d > blockThr) changed++;
            }
        }
        return (changed / (double)total, sum / (double)total, max);
    }

    private static byte[] SampleGray(string png, int scale, out int sw, out int sh)
    {
        using var bmp = new System.Drawing.Bitmap(png);
        sw = Math.Max(1, bmp.Width / scale);
        sh = Math.Max(1, bmp.Height / scale);
        var gray = new byte[sw * sh];
        var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
        // 统一克隆为 24bpp Rgb,避免灰度/索引/ARGB PNG 直接 LockBits(24bppRgb) 抛异常 → 整条处理崩
        using var rgb = bmp.Clone(rect, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        var data = rgb.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        try
        {
            int stride = data.Stride;
            unsafe
            {
                var p = (byte*)data.Scan0;
                for (int y = 0; y < sh; y++)
                {
                    int srcY = Math.Min(rgb.Height - 1, y * scale);
                    for (int x = 0; x < sw; x++)
                    {
                        int srcX = Math.Min(rgb.Width - 1, x * scale);
                        byte* px = p + srcY * stride + srcX * 3;   // 24bpp 顺序 BGR
                        gray[y * sw + x] = (byte)((px[2] * 77 + px[1] * 150 + px[0] * 29) >> 8);   // 亮度 Y
                    }
                }
            }
        }
        finally { rgb.UnlockBits(data); }
        return gray;
    }

    private static double MeanAbsDiff(byte[] a, byte[] b)
    {
        long sum = 0;
        for (int i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return (double)sum / a.Length;
    }

    /// <summary>拍N 组判定骨架(独立实现,非翻译):在段内帧号列表上,
    /// 窗口 q=4..maxQ(拍N+2),相对判据 d1&lt;d0 且 d1&lt;d2(d=均衡差路径和,
    /// 代理光流距离),count==(q(q-5)+6)/2 命中即标记组内中间 q-3 帧。
    /// 注意:本函数只"标记",实际删除必须再与三闸安全集求交(实测裸标记会误删口型)。</summary>
    private static System.Collections.Generic.HashSet<int> DetectGroupHolds(int s, int e,
        double[] histds, int maxQ)
    {
        var marks = new System.Collections.Generic.HashSet<int>();
        // Pass1(预处理):与上一保留帧均衡差 <0.001 的帧不入候选(近似重复视为已删)
        var K = new System.Collections.Generic.List<int>();
        for (int f = s; f < e; f++)
        {
            if (f > s && histds[f - s - 1] < 0.001) continue;
            K.Add(f);
        }
        double PathSum(int a, int b)
        {
            double sum = 0;
            int lo = Math.Min(a, b), hi = Math.Max(a, b);
            for (int k = lo - s; k < hi - s; k++) sum += histds[k];
            return sum;
        }
        for (int q = 4; q <= maxQ; q++)
        {
            int i = 1;
            while (i < K.Count - (q - 1))
            {
                int cnt = 0;
                for (int step = 1; step <= q - 3; step++)
                {
                    int pos = 1;
                    while (pos + step <= q - 2)
                    {
                        int f0 = K[i], m0 = K[i + pos], m1 = K[i + pos + step], f1 = K[i + q - 1];
                        double d0 = PathSum(f0, m0), d1 = PathSum(m0, m1), d2 = PathSum(m1, f1);
                        if (d1 < d0 && d1 < d2) cnt++;
                        pos++;
                    }
                }
                if (cnt == (q * (q - 5) + 6) / 2)
                {
                    for (int t = 1; t <= q - 3; t++) marks.Add(K[i + t]);
                    i += q - 3;
                }
                i++;
            }
        }
        return marks;
    }

    /// <summary>两帧图像文件是否字节级相同(先比长度,再全量比对)。</summary>
    private static bool FilesEqual(string a, string b)
    {
        try
        {
            var fa = new FileInfo(a); var fb = new FileInfo(b);
            if (fa.Length != fb.Length) return false;
            return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
        }
        catch { return false; }
    }

    /// <summary>直方图均衡(cv2.equalizeHist 同款:灰阶 cdf 映射),用于归一化差判据。</summary>
    private static byte[] EqualizeHist(byte[] src)
    {
        int[] hist = new int[256];
        for (int i = 0; i < src.Length; i++) hist[src[i]]++;
        double total = src.Length;
        byte[] lut = new byte[256];
        double acc = 0;
        for (int i = 0; i < 256; i++)
        {
            acc += hist[i];
            lut[i] = (byte)Math.Round(acc * 255.0 / total);
        }
        var r = new byte[src.Length];
        for (int i = 0; i < src.Length; i++) r[i] = lut[src[i]];
        return r;
    }

    /// <summary>读取 PNG 为全分辨率灰度(Y 亮度,24bpp 克隆防 Bitmap 位深异常),
    /// 并从同一解码缓冲派生"蓝通道 + 4/2 步采样"(归一化图同款:
    /// 高>1000 取 ::4,否则 ::2;供均衡差判据)。</summary>
    private static byte[] LoadFullGray(string png, out int w, out int h, out byte[] blue4, out int bw4, out int bh4)
    {
        using var bmp = new System.Drawing.Bitmap(png);
        w = bmp.Width; h = bmp.Height;
        var gray = new byte[w * h];
        int step = h > 1000 ? 4 : 2;
        bw4 = Math.Max(1, w / step); bh4 = Math.Max(1, h / step);
        blue4 = new byte[bw4 * bh4];
        var rect = new System.Drawing.Rectangle(0, 0, w, h);
        using var rgb = bmp.Clone(rect, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        var data = rgb.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        try
        {
            int stride = data.Stride;
            unsafe
            {
                var p = (byte*)data.Scan0;
                for (int y = 0; y < h; y++)
                {
                    byte* row = p + y * stride;
                    for (int x = 0; x < w; x++)
                    {
                        byte* px = row + x * 3;                        // 24bpp 顺序 BGR
                        gray[y * w + x] = (byte)((px[2] * 77 + px[1] * 150 + px[0] * 29) >> 8);   // 亮度 Y
                    }
                }
                for (int y = 0; y < bh4; y++)
                {
                    int sy = Math.Min(h - 1, y * step);
                    byte* row = p + sy * stride;
                    for (int x = 0; x < bw4; x++)
                        blue4[y * bw4 + x] = row[Math.Min(w - 1, x * step) * 3];   // BGR 第 0 字节 = 蓝
                }
            }
        }
        finally { rgb.UnlockBits(data); }
        return gray;
    }

    /// <summary>从全分辨率灰度做等步长点采样(与 SampleGray 同口径:取 (x*scale, y*scale) 单像素)。</summary>
    private static byte[] SampleFrom(byte[] full, int w, int h, int scale, out int sw, out int sh)
    {
        sw = Math.Max(1, w / scale); sh = Math.Max(1, h / scale);
        var g = new byte[sw * sh];
        for (int y = 0; y < sh; y++)
        {
            int sy = Math.Min(h - 1, y * scale);
            int row = sy * w;
            for (int x = 0; x < sw; x++)
                g[y * sw + x] = full[row + Math.Min(w - 1, x * scale)];
        }
        return g;
    }

    /// <summary>全分辨率分块(bs×bs)平均绝对差的最大值。整幅均值会被大面积静止背景稀释,
    /// 而 max 块差抓住"局部运动"(嘴/眼皮/手指):真复制帧全画面无局部运动(maxD≈0);
    /// 口型/微动帧局部块 maxD 通常&gt;20,即使整幅 SAD 仍然很小 → 判"在动"必须保留。</summary>
    private static double MaxBlockD(byte[] a, byte[] b, int w, int h, int bs)
    {
        double max = 0;
        for (int by = 0; by < h; by += bs)
        {
            int be = Math.Min(h, by + bs);
            for (int bx = 0; bx < w; bx += bs)
            {
                long sum = 0; int n = 0;
                int xe = Math.Min(w, bx + bs);
                for (int y = by; y < be; y++)
                {
                    int rowA = y * w, rowB = y * w;
                    for (int x = bx; x < xe; x++) { sum += Math.Abs(a[rowA + x] - b[rowB + x]); n++; }
                }
                if (n > 0) max = Math.Max(max, (double)sum / n);
            }
        }
        return max;
    }

    /// <summary>两灰度帧中"发生明显变化"(|差|&gt; dt)的像素占比。用占比而非整幅均值:
    /// 均值会被大面积静止背景稀释(背景静止+主体小幅移动→均值差很小),导致"细节丰富的视频"被误判大量重复;
    /// 占比能抓住局部运动,判"真近重复"更准。</summary>
    private static double ChangedRatio(byte[] a, byte[] b, int dt)
    {
        long cnt = 0;
        for (int i = 0; i < a.Length; i++) if (Math.Abs(a[i] - b[i]) > dt) cnt++;
        return (double)cnt / a.Length;
    }

    /// <summary>估计两帧间全局平移(相机 pan)并算"对齐后残差"。
    /// 对齐后残差极小 = 去除镜头运动后两帧几乎相同 = 人物没动(定格/冗余),应删;
    /// 残差仍大 = 人物确实在动,保留。
    /// range=搜索半径(px,小灰度图上够用)。返回(最佳dx, 最佳dy, 对齐后SAD, 对齐后变化块占比)。
    /// 思路:背景持续 pan 时整帧 SAD 永远偏大,整帧 SSIM 到不了"相同";
    /// 先估计相机平移并"对齐",再在残差上判"人物有没有动",才能精准抓出人物定格。</summary>
    private static (int dx, int dy, double alignedSad, double changedRatio) EstimateGlobalShift(
        byte[] a, byte[] b, int w, int h, int range = 8)
    {
        int bestDx = 0, bestDy = 0;
        double bestSad = double.MaxValue;
        for (int dy = -range; dy <= range; dy++)
        {
            for (int dx = -range; dx <= range; dx++)
            {
                long sum = 0;
                int cnt = 0;
                for (int y = 0; y < h; y++)
                {
                    int sy = y + dy;
                    if (sy < 0 || sy >= h) continue;
                    int rowA = sy * w, rowB = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int sx = x + dx;
                        if (sx < 0 || sx >= w) continue;
                        sum += Math.Abs(a[rowA + sx] - b[rowB + x]);
                        cnt++;
                    }
                }
                double sad = cnt > 0 ? (double)sum / cnt : double.MaxValue;
                if (sad < bestSad) { bestSad = sad; bestDx = dx; bestDy = dy; }
            }
        }
        // 对齐后变化块占比:用最佳位移对齐后再分块统计(4x4 块,阈值 4)
        double changedRatio = ComputeAlignedChangedRatio(a, b, w, h, bestDx, bestDy);
        return (bestDx, bestDy, bestSad, changedRatio);
    }

    /// <summary>按 (dx,dy) 位移对齐后,统计"仍变化"的块占比(0~1),用 4x4 块、单块平均差>4 算变化。</summary>
    private static double ComputeAlignedChangedRatio(byte[] a, byte[] b, int w, int h, int dx, int dy)
    {
        const int bxN = 4, byN = 4;
        int bw = Math.Max(1, w / bxN), bh = Math.Max(1, h / byN);
        int changed = 0, total = bxN * byN;
        for (int by = 0; by < byN; by++)
        {
            for (int bx = 0; bx < bxN; bx++)
            {
                long s = 0;
                int cnt = 0;
                int x0 = bx * bw, x1 = Math.Min(w, (bx + 1) * bw);
                int y0 = by * bh, y1 = Math.Min(h, (by + 1) * bh);
                for (int y = y0; y < y1; y++)
                {
                    int sy = y + dy;
                    if (sy < 0 || sy >= h) continue;
                    int rowA = sy * w;
                    for (int x = x0; x < x1; x++)
                    {
                        int sx = x + dx;
                        if (sx < 0 || sx >= w) continue;
                        s += Math.Abs(a[rowA + sx] - b[y * w + x]);
                        cnt++;
                    }
                }
                double d = cnt > 0 ? (double)s / cnt : 0;
                if (d > 4) changed++;
            }
        }
        return (double)changed / total;
    }

    /// <summary>秒数格式化为 mm:ss(或 h:mm:ss)。</summary>
    private static string FormatTime(double seconds)
    {
        if (seconds < 0) seconds = 0;
        int total = (int)Math.Round(seconds);
        int h = total / 3600, m = total % 3600 / 60, s = total % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
    }

    /// <summary>分块 SSIM(16×16 块):亮度/对比度/结构三维相似度,0~1,越接近 1 越像。</summary>
    private static double BlockSsim(byte[] a, byte[] b, int w, int h)
    {
        const double C1 = (0.01 * 255) * (0.01 * 255);
        const double C2 = (0.03 * 255) * (0.03 * 255);
        const int bs = 16;
        double total = 0;
        int blocks = 0;
        for (int by = 0; by < h; by += bs)
        {
            for (int bx = 0; bx < w; bx += bs)
            {
                int bwe = Math.Min(w, bx + bs), bhe = Math.Min(h, by + bs);
                long sumA = 0, sumB = 0, sumAA = 0, sumBB = 0, sumAB = 0;
                int n = 0;
                for (int y = by; y < bhe; y++)
                {
                    for (int x = bx; x < bwe; x++)
                    {
                        int va = a[y * w + x], vb = b[y * w + x];
                        sumA += va; sumB += vb; sumAA += va * va; sumBB += vb * vb; sumAB += va * vb;
                        n++;
                    }
                }
                if (n == 0) continue;
                double mA = sumA / (double)n, mB = sumB / (double)n;
                double vA = sumAA / (double)n - mA * mA;
                double vB = sumBB / (double)n - mB * mB;
                double cov = sumAB / (double)n - mA * mB;
                double ssim = ((2 * mA * mB + C1) * (2 * cov + C2)) /
                    ((mA * mA + mB * mB + C1) * (vA + vB + C2));
                total += ssim;
                blocks++;
            }
        }
        return blocks > 0 ? total / blocks : 0;
    }

    /// <summary>去重保护:删掉过多帧说明素材动态过低(如几乎静止的短视频),
    /// 继续处理会得到只有几帧的"坏"视频(打不开/没补帧),直接报错而不是假装成功。
    /// 用户确认"仍要进行"时可通过 allowFewFrames 跳过本保护。</summary>
    /// <summary>v4 架构模型判定(统一口径):rife-v4 / rife-v4.6 / rife-v4.13 / rife-v4.26…(可精确补足、非 2 的幂)。
    /// 曾散落 3 处写死 "rife-v4" or "rife-v4.6",导致默认模型 rife-v4.13 走 v2 兜底逻辑(帧数/时长次优)。</summary>
    internal static bool IsV4Model(string model) =>
        model == "rife-v4" || model.StartsWith("rife-v4.", StringComparison.Ordinal);

    private static void EnsureDedupResultSane(int frameCount, int origEst)
    {
        if (frameCount < Math.Max(3, (int)(origEst * 0.15)))
        {
            AppLogger.Info($"去重过强拦截:原 {origEst} 帧只剩 {frameCount} 帧(低于 15%)→ 拒绝处理,防输出只有几帧");
            throw new DedupTooStrongException(
                $"去重过强:原 {origEst} 帧只剩 {frameCount} 帧。素材画面变化太小时去重会几乎删光帧," +
                "导致输出视频只有几帧(打不开/没有补帧效果)。请关闭去重,或把去重强度调低。");
        }
    }

    /// <summary>校验合成的视频文件确实是可读的有效视频(非空 + ffprobe 能读出视频流 + 帧数不少于 5 帧)。
    /// 硬件编码失败时 ffmpeg 可能留下 0 字节/损坏的文件但被 File.Exists 误判为成功,这里兜底。</summary>
    private static async Task<bool> ValidateVideoFileAsync(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length == 0) return false;
            var ffmpegDir = FfmpegPath != null ? Path.GetDirectoryName(FfmpegPath) : null;
            var ffprobe = ffmpegDir != null ? Path.Combine(ffmpegDir, "ffprobe.exe") : null;
            if (ffprobe == null || !File.Exists(ffprobe)) return true;   // 无 ffprobe 时退化为只校验大小
            var psi = new ProcessStartInfo
            {
                FileName = ffprobe,
                Arguments = $"-v error -select_streams v:0 -show_entries stream=codec_type,nb_frames -of csv=p=0 \"{path}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            var outp = (await outTask).Trim();
            await errTask;
            if (p.ExitCode != 0 || !outp.Contains("video", StringComparison.OrdinalIgnoreCase)) return false;
            // 帧数下限检查:只有几帧的"视频"视为无效(去重过度/异常),防止假成功
            var fields = outp.Split(',');
            if (fields.Length >= 2 && int.TryParse(fields[1], out var nb) && nb < 5) return false;
            return true;
        }
        catch { return false; }
    }

    /// <summary>编码参数;quality 0=自动 1=低 2=中 3=高 4=极高(CRF 值递减=画质递增,单调)。
    /// bitrateKbps &gt; 0 = 自定义码率(固定码率,替代质量档);codec 支持 H.264/H.265 各硬编 + CPU。</summary>
    private static string EncoderArgs(string encoder, int quality = 0, double bitrateKbps = 0)
    {
        int q = quality switch { 0 => 22, 1 => 26, 2 => 24, 3 => 20, 4 => 15, _ => 22 };
        int th = SafeRender.GetLibx264Threads();
        if (bitrateKbps > 0)
        {
            int k = (int)Math.Max(100, bitrateKbps);
            return encoder switch
            {
                "h264_nvenc" or "hevc_nvenc" => $"-c:v {encoder} -preset p4 -rc vbr -cq 18 -b:v {k}K -maxrate {k}K -bufsize {k * 2}K -pix_fmt yuv420p",
                "h264_amf" or "hevc_amf" => $"-c:v {encoder} -quality quality -rc cbr -b:v {k}K -pix_fmt nv12",
                "h264_qsv" or "hevc_qsv" => $"-c:v {encoder} -b:v {k}K -pix_fmt yuv420p",
                "libx265" => $"-c:v libx265 -preset veryfast -b:v {k}K -maxrate {k}K -bufsize {k * 2}K -pix_fmt yuv420p -x265-params threads={th}",
                _ => $"-c:v libx264 -preset veryfast -b:v {k}K -maxrate {k}K -bufsize {k * 2}K -pix_fmt yuv420p -threads {th}",
            };
        }
        return encoder switch
        {
            "h264_nvenc" => $"-c:v h264_nvenc -preset p4 -cq {q} -pix_fmt yuv420p",
            "h264_amf" => $"-c:v h264_amf -quality quality -rc cqp -qp_i {q} -qp_p {q} -pix_fmt nv12",   // AMF 必须给 NV12,否则黑屏
            "h264_qsv" => $"-c:v h264_qsv -global_quality {q} -pix_fmt yuv420p",
            "hevc_nvenc" => $"-c:v hevc_nvenc -preset p4 -cq {q} -pix_fmt yuv420p",
            "hevc_amf" => $"-c:v hevc_amf -quality quality -rc cqp -qp_i {q} -qp_p {q} -pix_fmt nv12",
            "hevc_qsv" => $"-c:v hevc_qsv -global_quality {q} -pix_fmt yuv420p",
            "libx265" => $"-c:v libx265 -preset veryfast -crf {q} -pix_fmt yuv420p -x265-params threads={th}",
            // 轻量 CPU 模式:限制线程 + 快速预设,不把 CPU 跑满;线程数按"安全渲染"CPU 墙
            _ => $"-c:v libx264 -preset veryfast -crf {q} -pix_fmt yuv420p -threads {th}",
        };
    }

    /// <summary>按开始/结束时间裁剪并保存(重编码保证精确,保留音频)。</summary>
    public static async Task<string> AddTrimAsync(string input, string output,
        double start, double end, int gpuId,
        IProgress<(int pct, string msg)>? progress = null, CancellationToken ct = default)
    {
        var ffmpeg = FfmpegPath ?? throw new FileNotFoundException("未找到 ffmpeg");
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (!output.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            output += ".mp4";
        var dur = end - start;
        if (dur <= 0)
            throw new InvalidOperationException("裁剪结束时间必须晚于开始时间");
        progress?.Report((10, $"裁剪 {start:0.##}s ~ {end:0.##}s..."));
        await EnsureHwProbeAsync(ffmpeg, ct);
        var encoder = PickVideoEncoder(gpuId);
        var encArgs = EncoderArgs(encoder);
        // 先写临时文件,完成后改名:裁剪期间输出目录不出现半成品
        // 临时名保留真实扩展名(.tmp 在扩展名前),否则 ffmpeg 无法识别输出格式
        var tmp = Path.Combine(Path.GetDirectoryName(output)!,
            Path.GetFileNameWithoutExtension(output) + ".tmp" + Path.GetExtension(output));
        try
        {
            try
            {
                await RunAsync(ffmpeg,
                    $"-y -ss {start.ToString("0.###", inv)} -i \"{input}\" -t {dur.ToString("0.###", inv)} " +
                    $"{encArgs} -c:a copy -movflags +faststart \"{tmp}\"",
                    progress, ct);
            }
            catch when (encoder != "libx264")
            {
                await RunAsync(ffmpeg,
                    $"-y -ss {start.ToString("0.###", inv)} -i \"{input}\" -t {dur.ToString("0.###", inv)} " +
                    $"{EncoderArgs("libx264")} -c:a copy -movflags +faststart \"{tmp}\"",
                    progress, ct);
            }
            if (!File.Exists(tmp))
                throw new InvalidOperationException("裁剪输出失败");
            File.Move(tmp, output, true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
        progress?.Report((100, "完成"));
        return output;
    }

    /// <summary>滤镜内丢帧路径(手动-mpdecimate/手动-scene)的"保留帧源号"探测:
    /// 用 metadata=print 输出每个通过滤镜的帧的 pts → 反推源帧号(CFR:号=round(pts×inFps))。
    /// 滤镜是确定性的,探测结果与拆帧滤镜一致;失败/无输出返回 null(调用方回退原标准补帧,行为不变)。</summary>
    private static async Task<System.Collections.Generic.List<int>?> ProbeKeptFrameIdxAsync(
        string ffmpeg, string inputVideo, string trimArgs, string vf, double inFps, CancellationToken ct)
    {
        try
        {
            var lines = await RunCaptureAsync(ffmpeg,
                $"-y -i \"{inputVideo}\" {trimArgs} -vf \"{vf},metadata=print\" -f rawvideo NUL", ct);
            var idx = new System.Collections.Generic.List<int>();
            foreach (var ln in lines)
            {
                var m = System.Text.RegularExpressions.Regex.Match(ln, @"pts_time:(\d+(?:\.\d+)?)\s*$");
                if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var pts))
                    idx.Add((int)Math.Round(pts * inFps));
            }
            if (idx.Count < 2) return null;
            var clean = new System.Collections.Generic.List<int>();
            int last = -1;
            foreach (var n in idx) { if (n > last) { clean.Add(n); last = n; } }
            return clean.Count >= 2 ? clean : null;
        }
        catch { return null; }
    }

    /// <summary>运行命令并返回完整输出行(供解析,如转场检测)。</summary>
    private static async Task<List<string>> RunCaptureAsync(string exe, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? ".",
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动: " + exe);
        SafeRender.ApplyProcessPriority(p);   // 处理时降优先级,防整机卡(可设置关闭)
        App.ActiveProcesses.Register(p);   // 纳入"暂停=冻结"(遍历整个注册表冻结,含并发多路)
        var errTask = p.StandardError.ReadToEndAsync();
        var outTask = p.StandardOutput.ReadToEndAsync();
        while (!p.HasExited)
        {
            if (ct.IsCancellationRequested)
            {
                try { ResumeActiveProcess(); p.Kill(entireProcessTree: true); } catch { }   // 取消前先解冻(冻结进程 kill 可能失败)
                break;
            }
            await Task.Delay(100).ConfigureAwait(false);
        }
        App.ActiveProcesses.Unregister(p.Id);
        var err = await errTask;
        var stdout = await outTask;
        if (ct.IsCancellationRequested)
            throw new OperationCanceledException();
        if (p.ExitCode != 0)
        {
            // 探测类命令(转场/评分)失败不致命,但必须留痕:记录命令与输出尾部,便于定位(如 ffmpeg 滤镜不存在)
            var tail = (err + "\n" + stdout).Trim();
            if (tail.Length > 800) tail = tail[^800..];
            AppLogger.Error($"命令失败(exit {p.ExitCode}):{Path.GetFileName(exe)} {args[..Math.Min(args.Length, 120)]} | {tail}");
        }
        var all = (err + "\n" + stdout).Split('\n').ToList();
        return all;
    }

    // ffmpeg 的帧计数输出(frame=  123 fps=...)
    private static readonly System.Text.RegularExpressions.Regex FrameRegex = new(
        @"frame=\s*(\d+)", System.Text.RegularExpressions.RegexOptions.Compiled);
    // 引擎百分比输出(12.5%)
    private static readonly System.Text.RegularExpressions.Regex VideoPctRegex = new(
        @"(\d+(?:\.\d+)?)\s*%", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// 运行命令,实时解析进度并报告。stage+totalFrames 非空时逐帧报告
    /// ("补帧 第 12 帧 / 共 48 帧"),由 ffmpeg 的 frame= 或引擎百分比换算。
    /// </summary>
    private static async Task RunAsync(string exe, string args,
        IProgress<(int pct, string msg)>? progress, CancellationToken ct,
        string stage = "", int totalFrames = 0, string? watchDir = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? ".",
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动: " + exe);
        SafeRender.ApplyProcessPriority(p);   // 处理时降优先级,防整机卡(可设置关闭)
        App.ActiveProcesses.Register(p);
        // 引擎不输出进度时(如 rife),轮询输出目录已生成的文件数来逐帧报告
        using var watchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watchTask = (watchDir != null && totalFrames > 0 && stage.Length > 0)
            ? WatchDirProgressAsync(watchDir, stage, totalFrames, progress, watchCts.Token)
            : Task.CompletedTask;
        var lockObj = new object();
        int maxPct = 0, maxFrame = 0;
        void OnChunk(string chunk)
        {
            lock (lockObj)
            {
                // ffmpeg 帧计数
                foreach (System.Text.RegularExpressions.Match m in FrameRegex.Matches(chunk))
                {
                    if (int.TryParse(m.Groups[1].Value, out var fr) && fr > maxFrame)
                    {
                        maxFrame = fr;
                        if (totalFrames > 0 && stage.Length > 0)
                            progress?.Report((StageProgressPct(stage, fr, totalFrames),
                                $"{stage} 第 {fr} 帧 / 共 {totalFrames} 帧"));
                    }
                }
                // 引擎百分比(拆帧/补帧引擎无 frame= 时用百分比换算帧号)
                foreach (System.Text.RegularExpressions.Match m in VideoPctRegex.Matches(chunk))
                {
                    if (double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var dv) &&
                        (int)Math.Round(dv) > maxPct)
                    {
                        maxPct = (int)Math.Round(dv);
                        if (totalFrames > 0 && stage.Length > 0)
                        {
                            int fr = Math.Clamp(totalFrames * maxPct / 100, 1, totalFrames);
                            progress?.Report((StageProgressPct(stage, fr, totalFrames), $"{stage} 第 {fr} 帧 / 共 {totalFrames} 帧"));
                        }
                        else
                        {
                            progress?.Report((maxPct, $"处理中 {maxPct}%..."));
                        }
                    }
                }
            }
        }
        var drainOut = DrainAsync(p.StandardOutput, OnChunk, ct);
        var drainErr = DrainAsync(p.StandardError, OnChunk, ct);
        while (!p.HasExited)
        {
            if (ct.IsCancellationRequested)
            {
                try { ResumeActiveProcess(); p.Kill(entireProcessTree: true); } catch { }   // 取消前先解冻(冻结进程 kill 可能失败)
                break;
            }
            await Task.Delay(100).ConfigureAwait(false);
        }
        await Task.WhenAll(drainOut, drainErr).ConfigureAwait(false);
        watchCts.Cancel();
        try { await watchTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        App.ActiveProcesses.Unregister(p.Id);
        if (ct.IsCancellationRequested)
            throw new OperationCanceledException();
        if (p.ExitCode != 0)
        {
            var tail = (await drainErr).Trim();
            if (tail.Length > 500) tail = tail[^500..];
            // 杀软/防护拦截检测:引擎启动后 <5 秒就退出(毫秒级)且无正常输出 → 大概率被安全软件拦截
            try
            {
                bool quickExit = false;
                try { quickExit = (DateTime.Now - p.StartTime).TotalSeconds < 5; } catch { }
                if (quickExit && tail.Contains("access", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"命令失败 (exit {p.ExitCode}) — 引擎可能被杀毒/安全软件拦截:\n{tail}");
            }
            catch (InvalidOperationException) { throw; }
            catch { }
            throw new InvalidOperationException($"命令失败 (exit {p.ExitCode}):\n{tail}");
        }
    }

    /// <summary>把阶段 + 已处理帧数映射到整体进度百分比(拆帧 2~5、补帧 10~45、超分 45~90、编码 96~100)。</summary>
    private static int StageProgressPct(string stage, int fr, int totalFrames)
    {
        if (stage == "拆帧") return Math.Clamp(2 + fr * 3 / Math.Max(1, totalFrames), 2, 5);
        if (stage == "补帧") return Math.Clamp(10 + fr * 35 / Math.Max(1, totalFrames), 10, 45);
        if (stage == "超分") return Math.Clamp(45 + fr * 45 / Math.Max(1, totalFrames), 45, 90);
        if (stage == "编码") return Math.Clamp(96 + fr * 4 / Math.Max(1, totalFrames), 96, 100);
        return Math.Clamp(fr * 90 / Math.Max(1, totalFrames), 1, 90);
    }

    /// <summary>轮询输出目录已生成的文件数,逐帧报告进度(供不输出进度的引擎如 rife 使用)。</summary>
    private static async Task WatchDirProgressAsync(string dir, string stage, int totalFrames,
        IProgress<(int pct, string msg)>? progress, CancellationToken ct)
    {
        int lastCount = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                int count = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.png").Count() : 0;
                if (count > lastCount)
                {
                    lastCount = count;
                    progress?.Report((StageProgressPct(stage, count, totalFrames),
                        $"{stage} 第 {Math.Min(count, totalFrames)} 帧 / 共 {totalFrames} 帧"));
                }
            }
            catch { /* 目录尚未就绪等瞬时错误忽略 */ }
            try { await Task.Delay(200, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    // 逐块异步读取子进程输出(实时解析进度)
    private static async Task<string> DrainAsync(System.IO.StreamReader reader,
        Action<string> onChunk, CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder();
        var buf = new char[4096];
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int n = await reader.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false);
                if (n <= 0) break;
                var chunk = new string(buf, 0, n);
                sb.Append(chunk);
                if (sb.Length > 8192) sb.Remove(0, sb.Length - 8192);
                onChunk(chunk);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        return sb.ToString();
    }
}
