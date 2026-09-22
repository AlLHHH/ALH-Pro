// VideoService.cs — 视频超分 + 补帧
// 原理:ffmpeg 拆帧 → 逐帧图片超分 → (可选) RIFE 补帧 → ffmpeg 合帧+音频
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

/// <summary>子进程长时间无任何输出(疑似驱动/解码器挂死),已被无进展看门狗强制终止。
/// 派生自 InvalidOperationException,故现有的回退层(拆帧硬解→软解、编码器降级链)能直接接管。
/// 与用户取消(OperationCanceledException)严格区分:停滞要走回退,取消要立刻收手。
/// 【ProcessStillRunning】= 收到终止请求后进程**没有真的退出**(卡在内核态驱动调用里,TerminateProcess
/// 要等它返回)。这种时候**绝不能走"回退重跑"**:旧进程可能还在往同一个帧目录写文件,新旧两个进程交叉
/// 写出的帧数/内容都是错的(静默坏结果)。所以调用侧见到它为 true 必须直接失败,而不是降级重试。</summary>
public sealed class EngineStallException : InvalidOperationException
{
    public bool ProcessStillRunning { get; }

    public EngineStallException(string message, bool processStillRunning = false) : base(message)
        => ProcessStillRunning = processStillRunning;
}

/// <summary>【F1】补帧输出检出黑帧,且所有换路引擎(ONNX DirectML → 换卡 ncnn)重算后仍然是黑帧。
/// 这是一条"故意让任务失败"的异常:黑帧绝不允许当成功产物交付(真机事故:任务报"成功 1,失败 0",
/// 而成片含 3 段全黑)。派生自 InvalidOperationException 以便既有的通用 catch(Exception) 层能识别,
/// 但凡是要继续往下交付成片的调用点(如"补回"那条路)必须**显式重新抛出**它,不许当普通失败吞掉去走兜底
/// —— 兜底(展开/回退)会让帧数或时间轴错乱,正是用户明令不允许的处置。</summary>
public sealed class BlackFrameRerouteException : InvalidOperationException
{
    public BlackFrameRerouteException(string message) : base(message) { }
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
    /// <summary>备用 ffmpeg(如 8.x,用于 50 系/Blackwell NVENC 硬编在旧 7.1 上失败时的兜底)。
    /// 放 engines/ffmpeg8/ 目录;默认不存在则返回 null,完全不影响现有逻辑。</summary>
    public static string? BackupFfmpegPath => FindInEngines("ffmpeg8", "ffmpeg.exe");
    /// <summary>补帧引擎:ncnn-Vulkan 版 RIFE。
    /// 【优先 2026 重编版】engines/rife/ 下若同时存在 rife-ncnn-vulkan-2026.exe 与老的
    /// rife-ncnn-vulkan.exe,一律优先 2026 版:
    ///  旧版是 2022 年的上游二进制,静态指纹里 VK_EXT_robustness2 / VK_KHR_cooperative_matrix
    ///  命中数全是 0(根本没链那段兼容处理);2026 版用与 realesrgan 重编同一套 2025/2026 ncnn 源码编译,
    ///  指纹 2/2 + cooperative_matrix 364 命中,与已实测可用的 realesrgan-ncnn-vulkan-2026.exe 完全一致。
    /// 只有旧文件时仍返回旧的 —— 不删功能、不改变"引擎缺失"的判定语义。</summary>
    public static string? RifePath
    {
        get
        {
            var rebuilt = FindInEngines("rife", "rife-ncnn-vulkan-2026.exe");
            return rebuilt ?? FindInEngines("rife", "rife-ncnn-vulkan.exe");
        }
    }

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
    /// 用于一开始就显示合理的预计剩余(偏保守,随时间慢慢对齐),而不是从小变大校准。
    /// 【实测校准】同配置在 PerfMemory 有历史实测秒/帧时,用实测重算覆盖固定常数估算(越用越准)。
    /// postFx=是否启用了后处理(与记录端指纹一致,否则查不到导致校准失效)。
    /// freeRamGB/uniqueFrames=【2026-09-13 新增】把"每批引擎启动开销 × 批数"算进估算(长素材的批数
    /// 一多,原公式系统性偏乐观)。不知道空闲内存就传 0 = 保持旧口径(不猜档位);uniqueFrames 传 0
    /// 表示"还没去重,按源帧数估"。估算出的批数口径与真正执行时一致:都走 RenderPolicy.PlanVideoBatches。
    /// 【H · 2026-09-13】**阶段顺序不由调用方决定**:一律取单一事实来源
    /// `AlhPro.Core.PipelineOrderPlan.Decide(...)`(生产路径上"要不要先超分"的判据只有这一处)。
    /// 原先这里有个 `upscaleFirst` 形参、UI 自己算一份判据(写法还与管线不同)→ 管线回退后 UI 仍在按
    /// "根本不会执行的新顺序"估时间,是"预计时间不准"的来源之一。现在参数已删除,UI 无从再传错。
    /// 【备注】旧回退链那三样定义(旧开关常量 / 恒 false 的旧判据 / 零调用点的重复判据)已整体删除;
    /// 本文件里连注释都不再写它们的名字 —— UpscaleOrderTests 是**按名字扫全文**的(注释同样命中)。</summary>
    public static double EstimateProcessSeconds(double duration, double fps, int w, int h,
        bool up, double scale, string engine, bool interp, int interpScale, bool dedup, int videoDenoise,
        bool postFx = false, double freeRamGB = 0, int uniqueFrames = 0)
    {
        var sf = SafeRender.Profile == SafeRender.DeviceProfile.UltraLow ? 6.0 : 1.0;
        // 顺序 = 单一事实来源(管线实际执行的那个顺序);管线侧同一处旋钮见 ProcessVideoAsync 的阶段顺序判定。
        const bool upscaleFirst = false;   // 旧顺序(补帧→超分);新顺序由 PipelineOrderPlan 运行时判定
        double core = AlhPro.Core.VideoPipeline.EstimateProcessSeconds(duration, fps, w, h, up, scale, engine, interp, interpScale, dedup, videoDenoise, sf, upscaleFirst, freeRamGB, uniqueFrames);
        // 【实测校准】查同配置历史秒/帧(1080p 基准),命中则按"源帧数×实测×面积"重算;与固定估算加权(各50%)。
        // 注意:PerfMemory 记录时按【源帧数】归一(不乘补帧倍率),这里也用源帧数 src,避免补帧任务被重复放大。
        try
        {
            double areaN = Math.Max(0.25, (double)w * h / 2_073_600.0);
            int src = (int)Math.Max(1, duration * fps);
            var key = PerfMemory.Fingerprint(engine, scale, 1920, 1080, interpScale, dedup, videoDenoise, postFx);
            double? p = PerfMemory.PerFrameFor(key);
            if (p is { } pf && pf > 0.001)
            {
                double measured = src * pf * areaN;
                core = core * 0.5 + measured * 0.5;
            }
        }
        catch { }
        return core;
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
                // 【任务 P】同时取 avg_frame_rate 与 r_frame_rate:VFR 素材的 avg 实测可能是 `0/0`
                // (旧实现这时会把 "0/0" 原样返回 → 输入框里显示非数字),解析交给 Core.FfprobeFps(纯逻辑 + 单测):
                // avg 无效就自动看 r(容器最大帧率),两个都无效才返回 null(= 留空,处理时按该视频自动探测)。
                var psi = AudioService.NewFfmpegPsi(ffprobe, $"-v error -select_streams v:0 " +
                                $"-show_entries stream=avg_frame_rate,r_frame_rate -of csv=p=0 \"{videoPath}\"");
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using var p = Process.Start(psi);
                if (p == null) return null;
                var o = p.StandardOutput.ReadToEnd().Trim();
                if (o.Length > 0)
                {
                    var parsed = AlhPro.Core.FfprobeFps.Parse(o);
                    if (parsed != null) return parsed;
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

    /// <summary>探测视频是否为可变帧率(VFR)。两路信号,任一命中即判 VFR:
    /// (1) 免解码:ffprobe 的 r_frame_rate ÷ avg_frame_rate 比值(见 Core.VideoPipeline.IsVfrByRateRatio);
    /// (2) 抽查前 60 帧的 PTS 间隔,明显不均匀 → true。
    /// 为什么必须有 (1):(2) 只看开头 60 帧,录屏素材开头常是一段均匀帧 → 整片漏判
    /// (真机日志里的「VFR=自动(未检测到)」就是这么来的,而那个文件的 r/avg ≈ 3200)。
    /// 比值信号看的是全片时间戳粒度,一次 ffprobe 就有、不解码,命中还能省掉后面 60 帧的解码。
    /// VFR 素材(录屏/手机/监控)帧间隔忽大忽小,均匀拆帧会按平均帧率丢弃/复制帧
    /// → 时间轴失真(变快/变慢)。检测到后 UI 标注「可变帧率」并自动按原节奏处理。
    /// (2) 的数据源用 ffmpeg showinfo(滤镜层 = 真实播放时间轴),不用 ffprobe frame=pts_time:
    /// 后者含解码层时间戳,受 B 帧/时间基舍入影响会出现 2 倍间隔假象 → CFR 素材被误报为 VFR。</summary>
    public static async Task<bool> ProbeVfrAsync(string videoPath)
    {
        var ffmpeg = FfmpegPath;
        if (ffmpeg == null) return false;
        // ===== 信号(1):r_frame_rate / avg_frame_rate 比值(免解码) =====
        try
        {
            var ffprobe = FindFfprobe();
            if (ffprobe != null)
            {
                // 必须按 key 解析:ffprobe 的 csv writer 按【内部结构体字段序】输出、忽略 -show_entries
                // 的请求序(同 ProbeHdrToSdrAsync 的教训),位置解析迟早错位。
                var kvLines = await RunCaptureAsync(ffprobe,
                    $"-v error -select_streams v:0 -show_entries stream=r_frame_rate,avg_frame_rate " +
                    $"-of default=nw=1 \"{videoPath}\"", CancellationToken.None);
                double rRate = 0, avgRate = 0;
                foreach (var ln in kvLines)
                {
                    int eq = ln.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = ln.Substring(0, eq).Trim(), v = ln.Substring(eq + 1).Trim();
                    if (!TryParseFps(v, out var f)) continue;
                    if (k.Equals("r_frame_rate", StringComparison.OrdinalIgnoreCase)) rRate = f;
                    else if (k.Equals("avg_frame_rate", StringComparison.OrdinalIgnoreCase)) avgRate = f;
                }
                if (AlhPro.Core.VideoPipeline.IsVfrByRateRatio(rRate, avgRate))
                {
                    AppLogger.Info($"可变帧率判定:r_frame_rate {rRate:0.##} ÷ avg_frame_rate {avgRate:0.##} = " +
                        $"{rRate / Math.Max(0.001, avgRate):0.##} ≥ {AlhPro.Core.VideoPipeline.VfrRateRatioThreshold:0.#} → VFR(免解码信号)");
                    return true;
                }
            }
        }
        catch { }
        // ===== 信号(2):前 60 帧 PTS 间隔抽查 =====
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
            // 间隔统计分析:交给 AlhPro.Core.VideoPipeline.AnalyzeFrameGaps(纯函数、有单测)——
            // 判定规则与抽走前的内联写法逐字一致(max/min > 1.5 且 max > 0.5ms,否则 cv > 0.25),
            // 但它把"为什么判/没判"的原始数字一并带出来,【无论命中与否都写日志】:
            // 旧代码只在命中比值信号时打一行,漏判时日志里什么都没有(手机素材 r/avg 只有 1.04 的形态
            // 就是这样被漏掉的 —— 只有"间隔里散着双倍长间隔"落在抽查窗口内才靠 cv 侥幸命中)。
            var gaps = new System.Collections.Generic.List<double>();
            for (int i = 1; i < ptsList.Count; i++)
            {
                double g = ptsList[i] - ptsList[i - 1];
                if (g > 0) gaps.Add(g);
            }
            var stat = AlhPro.Core.VideoPipeline.AnalyzeFrameGaps(gaps);
            AppLogger.Info($"可变帧率判定(前 {ptsList.Count} 帧 PTS 抽查):{stat.Summary}");
            return stat.IsVfr;
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
    /// <param name="engine">图片超分引擎:waifu2x | realesrgan。</param>
    /// <param name="model">引擎模型参数。</param>
    /// <param name="scale">放大倍数(1/1.5/2/3/4;非整数倍用引擎就近倍数+高保真缩放)。</param>
    /// <param name="doUpscale">是否逐帧超分。</param>
    /// <param name="outWidth">自定义输出分辨率宽(null=按倍率)。</param>
    /// <param name="outHeight">自定义输出分辨率高(null=按倍率)。</param>
    /// <param name="frameInterp">是否 RIFE 补帧。</param>
    /// <param name="inFpsOverride">用户指定输入帧率(>0 时优先于自动探测)。</param>
    /// <param name="interpScale">补帧倍率(2/3/4/8;3 需 v4 架构模型)。</param>
    /// <param name="targetFps">指定输出帧率(仅补帧时生效;null=按倍率计算)。</param>
    /// <param name="dedupMode">去重模式(= VideoView 去重模型下拉索引+1):0=关,1=智能检测(自动识别拍数→网格采样),2=动漫模式(按拍型均匀采样),3=手动模式(按 dedupAlgo 分支)。</param>
    /// <param name="dedupThreshold">去重阈值(自定义模式,scene 分数上限)。</param>
    /// <param name="interpModel">RIFE 模型目录名(相对 engines/rife)。</param>
    /// <param name="sceneCut">「转场识别」这一组用户选项(阈值滑块;null=不检测,转场处不插帧)。
    /// 【2026-09-21 起从 `double? sceneThreshold` 换成这个记录】理由见 <see cref="AlhPro.Core.SceneCutOptions"/>
    /// 的类注释(避免每加一项就往六十多个形参后面接裸参数)。</param>
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
    /// <param name="fastMode">兼容模式(弱设备):tile 减半降显存、单批处理防爆显存、忽略 TTA。</param>
    /// <param name="upscaleShrink1x">旧 1x 行为:内部按 2x 超分后缩回原始尺寸。**只在 Anime4K 不可用时作为回退**使用
    /// (2026-09-21 起 1x 档默认改走 <paramref name="anime4k1x"/>,因为"放大再缩回"实测比原片还软)。</param>
    /// <param name="anime4k1x">1x 修复档:用 Anime4K 着色器(<c>libplacebo</c> + <c>custom_shader_path</c>)在**原分辨率**上
    /// 做修复 + 锐化,不放大也不缩回。要求 <c>upscaleShrink1x=false</c>(超分阶段会被跳过 —— 见下方 1x 不超分那条分支)
    /// 且本机 Anime4K 探测通过(探测在 UI 侧做,不过则回退到 <paramref name="upscaleShrink1x"/>)。</param>
    /// <param name="postDeshake">【形参已删除 · 2026-09-21】原先这里是 `postMotionBlur` / `postDeshake`
    /// 两个「果冻修复」形参。用户当晚下令「果冻修复那个删掉」⇒ 界面整块删除,**处理端同时停用**:
    /// 只删界面不删处理端,就会变成"界面上没了、处理时还在偷偷加 CPU 逐帧滤镜"(tmix 混合 / deshake),
    /// 是本仓库最忌讳的一类静默行为。这里连形参一起删掉,而不是留着参数忽略 —— 留着就还有"哪天有人
    /// 顺手把它接回去"的口子,而用户要的是这块功能不存在。
    /// 【老设置文件的三个字段 `Jello` / `MotionBlur` / `DeShake` 仍保留在 VideoSettings 里】那是
    /// "老文件字段一律保留、只停止读取"的既定规矩(反序列化兼容 + 预设快照结构稳定),它们**不再有任何
    /// 读写点**,也不可能再影响处理结果。</param>
    public static async Task<string> ProcessVideoAsync(
        string inputVideo, string outputVideo,
        string engine, string model, double scale, bool doUpscale,
        bool frameInterp, double? inFpsOverride, int interpScale, double? targetFps,
        int dedupMode, double dedupThreshold, string interpModel,
        AlhPro.Core.SceneCutOptions? sceneCut, double? timeStep, bool tta,
        double? trimStart, double? trimEnd, int gpuId,
        int? outWidth = null, int? outHeight = null,
        IProgress<(int pct, string msg)>? progress = null,
        CancellationToken ct = default,
        int postSharpen = 0, int postClarity = 0, int postUsm = 0,
        int postDetail = 0, int postDeblur = 0,
        int videoDenoise = 0, int denoiseKind = 0, int quality = 0, bool fastMode = false, bool upscaleShrink1x = false,
        bool anime4k1x = false,
        int dedupAlgo = 0, int dedupHi = 12, int dedupLo = 5, double dedupFrac = 0.33,
        double dedupSadThr = 3.0, double dedupSsimThr = 0.97,
        double dedupPanThr = 8, bool dedupPanOn = false,
        double dedupAnimeThr = 0.92,
        int postAa = 0,
        int postEdge = 0,
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
        // 【任务 S3 · 2026-09-13】「平滑时间轴」:统一输出帧率并按场景切换对齐,避免播放顿挫与切点拖影。
        // 默认开(用户实测口径:"填平后开头 1~3 秒的顿挫感消失")。**关上 = 与改动前逐字一致**(见下方接线处)。
        // 【已知代价(照实写,不靠偷偷加锐化找补)】源里"本来静止"的缺口处会插出轻微软化(小样实测中位 ≈6%)。
        bool smoothTimeline = true,
        Func<Task>? pauseWait = null)
    {
        // 静态报告字段清零:防止上一个视频的去重摘要/编码器信息残留在下一个视频的显示里
        LastDedupReport = null;
        LastDedupShort = null;
        LastVideoEncoderInfo = "libx264 (CPU 软编)";
        // 中文路径 → 8.3 短路径:防「中文 Windows 用户名/文件名 + GBK 代码页」下 ffmpeg
        // "Illegal byte sequence"(用户实测 8月28日.mp4 在 C:\Users\小花\ 下必现)。
        // 只影响传给 ffmpeg 的路径字符串;日志/文件系统操作仍用原路径。
        inputVideo = AudioService.FfmpegSafePath(inputVideo);
        outputVideo = AudioService.FfmpegSafePath(outputVideo);
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
        // ===== 【任务 U】"本次处理"数据台账:阶段耗时 / 帧数 / 临时盘 / 清理量 / 顺序判定 =====
        // 【为什么另起一套计时】stageWatch 被 StageElapsed() 反复 Restart(那是给"阶段内即时提示"用的),
        // 拿它做分阶段统计必然串味;这里用独立的表 + 独立秒表,不动既有计时的任何语义。
        // 【诚实口径】下面每一项都写明"怎么测的";测不到的项一律打"未采集",绝不填假值。
        var uStageTimes = new System.Collections.Generic.List<(string Name, double Seconds)>();
        var uWatch = System.Diagnostics.Stopwatch.StartNew();
        void UMark(string name)
        {
            uStageTimes.Add((name, uWatch.Elapsed.TotalSeconds));
            uWatch.Restart();
        }
        // 临时盘采样:每次读一次临时目录所在盘的**剩余空间**,记录初始值与最小值 →
        // 峰值占用 = 初始剩余 − 最小剩余。采样点只有下面三处(帧准备/补帧+超分后/编码前),
        // 所以它是"采样下界",不是逐秒峰值 —— 日志里会照实写明口径。
        long uTempFreeFirst = -1, uTempFreeMin = -1;
        int uTempSamples = 0;
        void USampleTemp()
        {
            try
            {
                long free = new System.IO.DriveInfo(PickTempRoot()).AvailableFreeSpace;
                if (uTempFreeFirst < 0) uTempFreeFirst = free;
                if (uTempFreeMin < 0 || free < uTempFreeMin) uTempFreeMin = free;
                uTempSamples++;
            }
            catch { /* 拿不到盘信息 → 该项标"未采集" */ }
        }
        long uReleasedFrames = 0;   // 各阶段"边用边删"释放的临时帧总数(补帧 + 超分)
        // 顺序判定结论(在 3) 块里算,结算时要与实测耗时一起报出来 → 必须先声明)
        string uOrderLog = "";
        double uOrderSavingsSeconds = 0;
        double uOrderSavingsPercent = 0;
        bool uOrderMeasured = false;
        // ===== 【任务 X3】"各阶段毫秒每帧"专用秒表 =====
        // 【为什么不复用 UMark/uWatch】uStageTimes 里"补帧+超分"本来就是**一项**(两种阶段顺序都跑完才封口),
        // 拿它算不出"补帧 vs 超分"各自多快 —— 而那正是用户要的("一眼判断是素材变大还是软件变慢")。
        // 这里另起两只独立秒表,只包住两个子阶段,**不动既有计时的任何语义**(UMark 该记什么还记什么)。
        // 两个调用点互斥(旧顺序在 3) 块、新顺序在 4.2) 块),所以补帧这只 Stopwatch 用 Start/Stop 累计即可。
        var xInterpWatch = new System.Diagnostics.Stopwatch();
        var xUpscaleWatch = new System.Diagnostics.Stopwatch();
        long xUpscaleFrames = 0;   // 超分阶段【输入】帧数(超分保帧数、不增减 → 结算时用它算该阶段 ms/帧)
        // 【任务 X1/X2】"批大小"结论行是否已在【处理开始】打进界面日志区(打了就不在超分阶段重复打)。
        bool xBatchLineShown = false;

        // 手动模式新增可调判据(默认保持原行为):局部动作保护/参考帧窗口/采样粒度/变化块判线
        dedupProtect = Math.Clamp(dedupProtect, 0.05, 0.60);
        dedupWindow = Math.Clamp(dedupWindow, 2, 12);
        dedupScale = dedupScale is 8 or 24 or 32 ? dedupScale : 16;
        dedupBlockThr = Math.Clamp(dedupBlockThr, 2, 12);

        // 兼容模式(弱设备):忽略 TTA(其速度开销接近翻倍,弱设备不划算)
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

        // ===== C3:临时盘预检——选剩余空间最大的盘做临时目录;预估需求不足则提前中止(不跑到一半爆盘) =====
        double durSec = 0;
        try
        {
            durSec = (trimEnd ?? await ProbeDurationSeconds(inputVideo)) - (trimStart ?? 0);
            if (durSec <= 0) durSec = await ProbeDurationSeconds(inputVideo);
        }
        catch { }
        double estFps = 30;
        try { if (double.TryParse(ProbeFps(inputVideo), System.Globalization.NumberStyles.Float, inv, out var pf) && pf > 0) estFps = pf; } catch { }
        long baseFrames = (long)Math.Ceiling(Math.Max(1.0, durSec * estFps));
        // 【修复 长视频 200 多 G】临时帧峰值估算改【分辨率感知】:固定 3MB(1080p)对高分辨率/放大后的帧严重低估,
        // 导致"预计只需 X GB"但实际跑出 200 多 G(放大后 4K PNG 帧 10~20MB/张,全量并存用于合帧)。
        // v1.2.1 起中间帧改为 JPG(质量 0.85,单帧体积约为 PNG 的 1/3),故单帧估算按 JPG 折算;
        // 峰值帧数 = 补帧后帧数(放大不减帧数,只增单帧大小);单帧大小按 源分辨率 × 放大倍率² 估算(JPG 压缩好,系数压低)。
        (int srcW, int srcH) = await ProbeSizeAsync(inputVideo);
        double srcFrameMB = AlhPro.Core.TempSpaceEstimate.SourceFrameMegabytes(srcW, srcH);   // 源帧 JPG≈1MB/1080p,按面积线性
        double outMult = doUpscale ? (upscaleShrink1x ? 2.0 : Math.Max(1.0, scale)) : 1.0;
        // 放大后单帧(中间帧 JPG):像素×outMult²,放大内容趋于平滑,JPG 压缩好,系数压低;不低于源帧尺寸
        // 【任务 O3 · 2026-09-13】补帧输出改"引擎直出 JPG"后**单帧体积变大**:实测 238 KB → 737 KB
        // (3.1×,引擎 q≈100 vs 程序内 q0.96)—— 峰值帧就是补帧输出帧,所以补帧开着时单帧估算要乘这个系数,
        // 否则"临时空间预估"低估约 3 倍(而这一项正是"长视频跑出 200 多 G"的教训来源)。公式已抽到
        // AlhPro.Core.TempSpaceEstimate(纯逻辑 + 单测:面积/倍率/补帧系数单调、下限保护)。
        double outFrameMB = AlhPro.Core.TempSpaceEstimate.PeakFrameMegabytes(srcW, srcH, outMult, frameInterp);
        long peakFrames = frameInterp ? (long)Math.Ceiling((double)baseFrames * interpScale) : baseFrames;   // 峰值帧数=放大后帧数
        // ① 性能档优先用 PerfMemory 的实测"秒/帧 @1080p"(指纹与 ETA 同口径:引擎/倍率/面积档/去重/降噪/后处理);
        //    没有记录 → DevicePerf 回退纯内存档。核数/已实测显存只做单向收紧(见 Core.DevicePerf)。
        AlhPro.Core.PerfScore perfScore;
        double? perfMeasured;
        int perfBatchFrames = 0;
        string perfReason;
        {
            string perfKey = PerfMemory.Fingerprint(engine, scale, 1920, 1080, interpScale, dedupMode > 0, videoDenoise, postFx: false);
            double? measured = null;
            try { measured = PerfMemory.PerFrameFor(perfKey); } catch { }
            perfScore = SafeRender.GetPerfScore(measured, out perfReason);
            perfMeasured = measured;
        }
        // 与 AvailableFreeSpace 同单位:字节(改为走同一个纯函数,防止两处口径漂移)。
        // 【任务 T】把"每批并存帧"(batchFrames)计入:只抬批上限不抬预估 = 只抬上限不给守门(用户硬约束禁止)。
        string tempRoot = PickTempRoot();
        // 【长视频临时盘水位】磁盘紧张标志:预估需要 ≥ 剩余空间 45% → 降批大小(减少同屏临时帧,防爆盘)。
        // 必须在 EvalTempSpaceGate 之前声明:那个本地函数要往里写(复判只能收紧、不能放松)。
        bool diskTight = false;
        var workDir = Path.Combine(tempRoot, $"imgup_video_{Guid.NewGuid():N}");

        // ===== 【临时空间守门 · 唯一一份判据】拆帧前粗判一次、倍率定稿后再复判一次 =====
        // 【为什么必须是两次】2026-09-15 实盘事故:用户勾补帧 + 填「指定帧率」时,程序会把 interpScale 从
        //   用户选的 2x **自动抬到 4x**;而"本任务要多少空间"这笔账原先只在拆帧之前算过一次,那会儿倍率还是 2x
        //   ⇒ 峰值帧数(源帧数×倍率)被低估近一半、守门按低估的账放行 ⇒ 跑到一半临时盘爆掉,
        //   后半段输出帧损坏被"占位帧"顶替(诊断包 2138 实证:按 2x 报 29.2GB 放行、实际按 4x 跑,
        //   21048 帧里 8550 帧是占位帧)。所以:①拆帧前按用户选的倍率粗判(preliminary=true,早失败省得白拆帧);
        //   ②倍率被「指定帧率」抬高之后按**最终倍率**复判(preliminary=false)—— 这才是能拦住爆盘的那道闸。
        // 【口径只有一个】估算表达式只在本函数里出现一次;调用点不许再内联一份(两份口径必然漂移)。
        (bool t, double needGB) EvalTempSpaceGate(bool preliminary)
        {
            int srcFramesEst = (int)Math.Min(int.MaxValue, Math.Max(0, baseFrames));
            int peakFramesEst = (int)Math.Min(int.MaxValue, Math.Max(0, peakFrames));
            int guardOutW = (int)Math.Max(1, Math.Round(srcW * outMult));
            int guardOutH = (int)Math.Max(1, Math.Round(srcH * outMult));
            var guardPlan = AlhPro.Core.RenderPolicy.PlanVideoBatches(SafeRender.FreeRamGB, srcFramesEst, peakFramesEst,
                false, false, srcW, srcH, guardOutW, guardOutH, perfScore);
            perfBatchFrames = guardPlan.BatchSize;
            double needBytesNow = AlhPro.Core.TempSpaceEstimate.NeedBytes(peakFrames, outFrameMB, perfBatchFrames, srcFrameMB);
            double needGBNow = needBytesNow / (1024.0 * 1024.0 * 1024.0);
            try
            {
                var drive = new System.IO.DriveInfo(tempRoot);
                double free = drive.AvailableFreeSpace;
                if (free < needBytesNow)
                    throw new InvalidOperationException(
                        $"临时磁盘空间不足:{tempRoot} 仅剩 {free / (1024 << 20):0}GB,本任务预计需要约 {needGBNow:0}GB(补帧放大后的临时帧占用大)。" +
                        "请清理磁盘、降低补帧倍率/超分倍率,或把视频放到其它盘后再处理。");
                if (free < 35L * 1024 * 1024 * 1024 || free < needBytesNow * 2.2)
                {
                    diskTight = true;
                    progress?.Report((0, $"⚠ 临时盘 {tempRoot} 剩余 {free / (1024 << 20):0}GB,任务预计 {needGBNow:0}GB——空间偏紧,已自动降低批大小保护(高负荷请留意,空间不足会失败)"));
                    AppLogger.Info($"⚠ 临时盘 {tempRoot} 剩余 {free / (1024 << 20):0}GB,预计需要 {needGBNow:0}GB——自动降低批大小(已自动选剩余最大的盘)");
                }
            }
            catch (InvalidOperationException) { throw; }
            catch { }
            if (!preliminary)
            {
                AppLogger.Info($"临时空间预估修正:补帧倍率按「指定帧率」自动调整为 {interpScale}x,峰值帧数按最终倍率复算为 {peakFrames} 帧,"
                    + "已按该倍率重新核对临时盘空间。");
                progress?.Report((6, $"按最终补帧 {interpScale}x 复核临时空间:预计约需 {needGBNow:0.#} GB(峰值 {peakFrames} 帧)"));
            }
            return (diskTight, needGBNow);
        }
        diskTight = diskTight || EvalTempSpaceGate(true).t;   // ① 拆帧前粗判(早失败)
        var framesIn = Path.Combine(workDir, "frames_in");
        var framesOut = Path.Combine(workDir, "frames_out");
        var framesFinal = Path.Combine(workDir, "frames_final");
        Directory.CreateDirectory(framesIn);
        Directory.CreateDirectory(framesOut);
        Directory.CreateDirectory(framesFinal);
        // 【处理中总闸 · 必须在工作目录建好之后】清理端(App.CleanupTempDirs)会枚举并删 imgup_video_* 工作目录;
        // 关窗那一刻若没有这道闸,它会把后台线程正在读写的那棵树整个删掉 ⇒ 线程下一次碰帧就是
        // DirectoryNotFoundException,日志里像"任务莫名失败"(真机:1440×1440/496 帧那次)。
        // 闸门是线程安全计数(视频管线跑在后台线程、UI 线程读):进闸放在这里,出闸放在 finally 末尾
        // (必须在删工作目录**之后**,否则删目录那一刻闸门已放开,清理端会插进来抢删同一棵树)。
        EngineService.EnterProcessing();

        try
        {
            // 1) 输入帧率:用户指定优先,否则 ffprobe 探测,再兜底 30
            var probed = ProbeFps(inputVideo);
            double probedFps = 0;
            double.TryParse(probed, System.Globalization.NumberStyles.Float, inv, out probedFps);
            double inFps;
            if (inFpsOverride is > 0) inFps = inFpsOverride.Value;
            else inFps = probedFps > 0 ? probedFps : 30.0;
            progress?.Report((1, $"输入帧率:{inFps.ToString("0.##", inv)} fps"));
            // ===== 超限提示:分辨率/帧率过大易出问题,给 3 种黄色警告 =====
            // 用总像素判定"超4K"(3840×2160≈829万像素):竖版4K(2160×3840)不算超,4096×2160(DCI 4K)才算,分档更准
            bool resBig = (long)srcW * srcH > 3840L * 2160L;
            bool fpsBig = inFps > 240;
            if (resBig && fpsBig)
                AppLogger.Warn($"⚠ 视频分辨率({srcW}×{srcH})超过 4K 且帧率({inFps.ToString("0.##", inv)}fps)超过 240——处理可能非常慢或易出错,建议先降低分辨率/帧率,或用小片段测试。");
            else if (resBig)
                AppLogger.Warn($"⚠ 视频分辨率({srcW}×{srcH})超过 4K(3840×2160)——处理可能非常慢或易出错,建议先降低分辨率/倍率,或用小片段测试。");
            else if (fpsBig)
                AppLogger.Warn($"⚠ 视频帧率({inFps.ToString("0.##", inv)}fps)超过 240——处理可能非常慢或易出错,建议先降低帧率或用小片段测试。");
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
                $";去重{dedupDesc};转场{(sceneCut?.Threshold ?? 0):0.##};时间步{(timeStep ?? 0):0.##};裁剪{(trimStart ?? 0):0.###}~{(trimEnd ?? 0):0.###};设备{(gpuId >= 0 ? "GPU " + gpuId : "CPU")}" + StageElapsed()));
            AppLogger.Info($"参数详情:engine={engine},model={model},scale={scale},up={doUpscale}/{upscaleShrink1x},interp={frameInterp}/{interpModel}/{interpScale}x/{tta}/{targetFps},{timeStep},dedup={dedupMode}/{dedupAlgo}/{animeHoldN}/{contentFps}/{dedupSmartMode},scene={sceneCut?.Threshold},trim={trimStart}/{trimEnd},gpu={gpuId},out={outputVideo}");
            // ===== 阶段顺序:1x/2x 走「超分 → 补帧」,3x/4x 保持「补帧 → 超分」=====
            // 【为什么】(2026-09-13 本机实测:1080p 源 240 帧,k=2 补帧,RIFE v4.13,realesr-animevideov3 2x,
            // 两序交错各 3 轮)中位 346.6s(超分→补帧) vs 411.6s(补帧→超分),新顺序快 65s ≈ 15.8%,逐档配对每档都快。
            // 原因:超分单价按帧算(2x@1080p≈0.70s/帧),补帧按【输出帧数】算;先超分 → 超分只跑源帧数(少一半),
            // 补帧帧数不变(源帧数×倍率),总账少一半的超分帧。
            // 1x 也是此列:1x 内部就是「2x 放大后缩回源尺寸」(upscaleShrink1x),补帧仍在源分辨率上做,
            // 与旧顺序的补帧成本完全相同,但超分/缩回次数减半 → 约省 44%。
            // 3x/4x(任何 scale>2.001)按【旧顺序一字不改】:4x 超分要跑到 4320p(2.30s/帧),
            // 而补帧在 4320p 上是 0.76~1.09s/输出帧、在源分辨率上只有 0.10s/输出帧 —— 先补帧能把补帧按便宜价跑。
            // 顺序分档的依据是"补帧单价随分辨率涨得比超分快",不是越新越好。
            bool upscaleRuns = doUpscale && !(scale <= 1.001 && !upscaleShrink1x);   // 超分阶段是否真的会执行
            // 【2026-09-13 实测回退:暂不启用新顺序】原判据为 frameInterp && upscaleRuns && !(scale > 2.001)。
            // 回退依据(用户真机日志:3 秒 / 72 帧 / 1080p / 超分 realesr-animevideov3 2x / 补帧 rife-v4.13 4x):
            //   超分实测 72 帧 20.1s = 279 ms/帧 —— 不是开发期 harness 测到的 0.70 s/帧(那次给引擎传了 -j 1:1:1,
            //   把超分成本高估约 2.5 倍,而"先超分更划算"的全部依据就是"超分贵");补帧搬到 3840×2160 后实测仅 1.83 帧/秒。
            //   该作业:省下的超分 = (4-1)×72 ≈ 51s,多花的补帧 = 285 帧从 1080p 搬到 4K ≈ 128s → 净亏,且随帧数线性增长。
            // 重新启用前必须做两件事(否则仍是没实测就说能用):
            //   1) 用 SafeRender.GetEngineThreadArgs() 同款线程参数,实测"补帧倍率 × 超分倍率 × 片长"矩阵;
            //   2) 按单帧成本之比(而不是"输出帧数超过多少")给门限 —— 帧数越大亏得越多,帧数阈值方向是反的。
            // 另注:进度区间已统一到 Core.ProgressBands,阶段切换时不再需要"随顺序改口径"。
            // 【单一事实来源】阶段顺序的判据只允许有一处:PipelineOrderPlan.Decide(按实测单价判定);
            // 下面那行只是**回退值**(旧顺序),不许在管线或 UI 里另算一份 —— 免得再出现"管线回退了、
            // UI 还在按新顺序估"这种各写一份的老问题。
            bool upscaleFirst = false;   // 回退值 = 旧顺序;真正的顺序由 PipelineOrderPlan(核心库)按实测单价判定
            // 【Q1 · 2026-09-13】上面这行只是【回退值】(全局开关口径)。真正的顺序在去重结果/补帧倍率确定后
            // 由 AlhPro.Core.PipelineOrderPlan 按**实测单价**判定(超分贵就先超分,补帧在放大帧上做太贵就先补帧);
            // 判定的日志会另打一行「顺序判定:… → 选择 X 顺序」(可审计)。这里的进度区间/早期日志仍按回退值打印,
            // 判定点之后 upscaleFirst 会被覆写,而进度区间(interpPctBase/Span)在第 3) 块之前就已确定 ——
            // 见判定点处的注释(两点都为"旧顺序"口径,避免进度条先跳后倒退)。
            // 【2026-09-16 统一到 Core.ProgressBands】不再写死数字:先跑的那个阶段用它自己的区间
            // (补帧 4~19、超分 19~66,按真机实测耗时分配)。顺序互换时跟着互换 ——
            // 否则"先超分后补帧"那档会拿补帧的小区间去跑最贵的超分,进度条反而更骗人。
            int interpPctBase = (int)(upscaleFirst
                ? AlhPro.Core.ProgressBands.Of(AlhPro.Core.ProgressBands.Stage.Upscale).lo
                : AlhPro.Core.ProgressBands.Of(AlhPro.Core.ProgressBands.Stage.Interp).lo);
            int interpPctSpan = (int)(upscaleFirst
                ? AlhPro.Core.ProgressBands.Of(AlhPro.Core.ProgressBands.Stage.Upscale).hi - AlhPro.Core.ProgressBands.Of(AlhPro.Core.ProgressBands.Stage.Upscale).lo
                : AlhPro.Core.ProgressBands.Of(AlhPro.Core.ProgressBands.Stage.Interp).hi - AlhPro.Core.ProgressBands.Of(AlhPro.Core.ProgressBands.Stage.Interp).lo);
            AppLogger.Info($"阶段顺序(回退值,待 Q1 自动判定):{(upscaleFirst ? "超分 → 补帧" : "补帧 → 超分")}"
                + $"(up={doUpscale}/shrink1x={upscaleShrink1x}/scale={scale:0.###},interp={frameInterp},超分是否执行={upscaleRuns})"
                + $" —— 真正的顺序由 AlhPro.Core.PipelineOrderPlan 按实测单价在补帧/超分都确定后判定(见「顺序判定:…」那行)");
            // ===== 设备选择映射诊断(编号错位排查命门):设置 GpuIndex → 实际引擎 gpuId → 设备名 =====
            try
            {
                string devName = gpuId >= 0 ? GpuInfo.GetEngineDeviceName(gpuId) : "(CPU)";
                AppLogger.Info($"设备选择映射:设置 GpuIndex={AppSettings.GpuIndex} → 实际引擎 gpuId={gpuId} → {devName}" +
                    (gpuId != AppSettings.GpuIndex && gpuId >= 0 ? " ⚠注意:设置值与运行时 -g 不一致(可能编号错位)" : ""));
            }
            catch { }

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
            // 【修复】时长探测失败(返回 0)→ origCountEst=0 → 补帧 frameScale=0 会静默退化为"逐帧拷贝"且无告警。
            // 给下限并提示,避免静默产出错误结果。
            if (origCountEst <= 0)
            {
                AppLogger.Warn("⚠ 时长探测失败(0),补帧帧数估计用下限 1,避免静默退化为逐帧拷贝——若结果异常请检查视频时长");
                origCountEst = 1;
            }
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
            // ===== 视频降噪:放在【拆帧阶段】,而不是合帧滤镜链 =====
            // 【为什么挪】原先它挂在合帧/编码的滤镜链上 → 作用在"超分之后的帧"上(1080p 源超分成 4K 后),
            // 实测 nlmeans:s=5 在 4K 上要 1.56 秒/帧、s=7 要 2.15 秒/帧,而源分辨率(1080p)只要 0.49 / 0.61 秒/帧
            // —— 挪到拆帧阶段便宜 2.5~3.5 倍(2 万帧的任务省数小时),顺序也更正确:先降噪再超分,
            // 超分不必再去放大噪点。nlmeans 是纯空间滤镜(只看单帧,不依赖前后帧),挪动安全。
            // 【例外:waifu2x 引擎】它自带降噪档(同一套网络只换权重,不额外耗时,且是专门针对动漫压缩块训练的),
            // 比 nlmeans 更对症 —— 这种情况下把强度交给模型的 -n(见超分调用处),这里就不再叠一层 nlmeans,
            // 避免"双重平滑更糊 + 双重耗时"。
            // 【要不要把降噪交给模型?必须先探一次】ONNX 稳定引擎是固定权重:既不认所选模型、也不认降噪档,
            // 若此时仍把降噪交给"模型档",用户开了「视频降噪」却等于没降噪(静默失效)。
            // 探测用的是一张合成图(不依赖已拆出的帧),且有会话缓存 —— 后面超分阶段不会因此多花时间。
            bool denoiseViaModel = doUpscale && engine == "waifu2x";
            // ===== 【降噪·自动档 · 2026-09-21】"该不该降"由**源素材体检**决定,不再让用户猜 =====
            // 实测依据(`_qa\降噪整改_实测_20260921.md`):干净/轻压缩的源上降噪是**净亏**
            // (H.264 crf30 源:不降噪成片 PSNR 36.23,现行弱/中/强 = 35.93/35.75/35.34);
            // 真噪源上才是净赚(JPEG q15:36.22 → 36.85,边缘宽度 4.04 → 3.72)。
            // 用户自己的素材实测颗粒σ 0.00~0.34(全部落在"干净"区间)⇒ 开着降噪等于白掉细节。
            // 【为什么自动档强制走"拆帧降噪"】waifu2x 的降噪是"模型自带档",这批一旦落到 ONNX 稳定引擎就完全不生效
            // (静默失效,见超分阶段那条提示)。自动档既然答应"帮你判断",就不能让它可能不生效
            // ⇒ 一律走引擎无关的拆帧滤镜链。**手动档的既有行为一个字不改**。
            // 【2026-09-21 用户:"自动不要了"】这段是**已下线**的自动档分支 —— 界面不再有「自动」可选项,
            //   所以 videoDenoise 永远不会 ≥ Auto(4) ⇒ 走到这里的概率为零。**保留代码不删**:逻辑与判据都还有单测
            //   (VideoNoiseProbe / noisecheck 工具),将来要恢复自动档时把界面选项加回来即可。
            if (videoDenoise >= AlhPro.Core.VideoDenoise.Auto)
            {
                var dnStats = await ProbeSourceNoiseAsync(inputVideo, ProbeContainerDurationSync(), ct);
                var dnDec = AlhPro.Core.VideoNoiseProbe.Decide(dnStats ?? default);
                if (dnStats.HasValue)
                    AppLogger.Info($"视频降噪[自动]:源体检 —— 颗粒σ {dnStats.Value.Grain:0.00}"
                        + $" · 块效应 {AlhPro.Core.VideoNoiseProbe.Fmt(dnStats.Value.Blocking)}"
                        + $" · 闪烁σ {AlhPro.Core.VideoNoiseProbe.Fmt(dnStats.Value.Flicker)}"
                        + $"({dnStats.Value.Frames} 帧,源分辨率、超分之前)");
                AppLogger.Info($"视频降噪[自动]:{dnDec.Reason}");
                progress?.Report((2, dnDec.Denoise
                    ? $"视频降噪[自动]:检出噪点 ⇒ 本批按「{AlhPro.Core.VideoDenoise.StrengthName(dnDec.Strength)}」档降噪"
                    : "视频降噪[自动]:源素材干净 ⇒ 本批跳过降噪(避免白掉细节)"));
                videoDenoise = dnDec.Strength;
                denoiseViaModel = false;   // 自动档统一走拆帧降噪:引擎无关、不会静默失效
            }
            if (denoiseViaModel && videoDenoise >= 1)
            {
                // 兼容模式(内部字段 fastMode)会【强制】走 ONNX 稳定引擎(见超分路由条件里的 || fastMode),
                // 所以它同样吃不到模型自带降噪档 —— 先判它,免得白探一次还把降噪判给模型。
                if (fastMode)
                {
                    denoiseViaModel = false;
                    AppLogger.Info("视频降噪:「兼容模式」强制走 ONNX 稳定引擎(无降噪档),降噪改由拆帧阶段 nlmeans 承担");
                }
                else
                {
                    // 【先报进度再探测】这一步要起引擎真跑一帧(可用/黑帧判定),探测失败还会 3 次退避 ≈ 20 秒 ——
                    // 期间界面若一条消息都没有,用户就会觉得"点了开始没反应/卡住"(真机反馈"开始处理时卡3秒")。
                    // 先给条进度(百分比落在拆帧前段),把这段等待变成"可见的检测中"。
                    progress?.Report((1, $"正在检测超分引擎兼容性({engine},首次约 1~20 秒,结论会记住)..." + StageElapsed()));
                    try
                    {
                        denoiseViaModel = await EngineService.EnsureNcnnProbeAsync("waifu2x", gpuId, model, ct);
                        if (!denoiseViaModel)
                            AppLogger.Info("视频降噪:waifu2x 的 ncnn 引擎在本机不可用(将走 ONNX 稳定引擎),降噪改由拆帧阶段 nlmeans 承担");
                    }
                    catch
                    {
                        // 探测异常:保守地按"模型档可用"处理(最坏情况只是该档无效,超分阶段已有如实提示)
                        denoiseViaModel = true;
                    }
                }
            }
            bool nlmeansOn = videoDenoise >= 1 && !denoiseViaModel;
            // 【L · 2026-09-13】waifu2x 模型自带降噪的 -n 值:按【最终下发给引擎的倍率】判,不按 UI 档位想当然。
            // 引擎侧 waifu2x 会把倍率向上取到 2 的幂(EngineService: engineScale = CeilPowerOfTwo(scale)),
            // 而"1x 缩回"(upscaleShrink1x)实际是按 2x 跑再缩回 → 引擎倍率同样是 2。
            // 用这同一个表达式算"引擎倍率",1x 护栏才不会失守(1x 下 -n -1 配 -s 1 必崩,见 Core.Waifu2x 的说明)。
            int waifu2xNoiseArg = AlhPro.Core.Waifu2x.NoiseLevelFor(
                videoDenoise, AlhPro.Core.PathUtil.CeilPowerOfTwo(upscaleShrink1x ? 2.0 : scale));
            if (videoDenoise >= 1 && denoiseViaModel)
                AppLogger.Info($"视频降噪:交由 waifu2x 引擎自带降噪档(-n {waifu2xNoiseArg};UI 档位 {videoDenoise}=弱/中/强 → -n 0/1/2)处理"
                    + "(不叠 nlmeans,更对症且不额外耗时)");
            else if (videoDenoise <= 0 && denoiseViaModel)
                AppLogger.Info($"视频降噪:未开启(关)→ waifu2x 引擎不再启用模型自带降噪(-n {waifu2xNoiseArg}"
                    + $"{(waifu2xNoiseArg == AlhPro.Core.Waifu2x.NoiseOff ? "" : ";注意:1x 档为规避引擎崩溃只能退到最轻档 0")})");
            // ===== HDR / 广色域适配:源为 HDR(PQ/HLG)或宽色域(≠BT.709)→ 拆帧时转成 BT.709 SDR(避免偏色/掉信息),黄字提示 =====
            (string? hdrDesc, string? hdrVf) = await ProbeHdrToSdrAsync(inputVideo, ct);
            if (hdrVf != null)
            {
                scaleVf = $"{scaleVf},{hdrVf}";
                AppLogger.Warn($"⚠ 检测到 HDR/广色域源({hdrDesc}):已自动转成 BT.709 标准 SDR 输出(避免偏色/掉信息)。");
                progress?.Report((2, $"⚠ 检测到 HDR/广色域源,输出已转标准 SDR(避免偏色)"));
            }
            // 【降噪只加给"真正抽帧"的调用】scaleVf 同时被【内容帧率检测 / 时长表 / 转场检测】这些分析调用复用,
            // 它们要解码大量帧;若带上全分辨率的 nlmeans,每个分析 pass 都要多花几十分钟(纯粹白干)。
            // 所以另建 scaleVfDenoise:只在 ExtractFramesCoreAsync(真正落盘的抽帧)上用它,分析调用继续用 scaleVf。
            var scaleVfDenoise = scaleVf;
            if (nlmeansOn)
            {
                var dnFilter = VideoDenoiseFilter(videoDenoise, denoiseKind);
                scaleVfDenoise = $"{dnFilter},{scaleVf}";
                AppLogger.Info($"视频降噪:拆帧阶段应用 {dnFilter} [{DenoiseKindName(denoiseKind)}](源分辨率、超分之前;分析类调用不带它)");
            }
            // 【2026-09-17 根治 10bit/HDR + 降噪 的格式冲突】拆帧落盘用的是 mjpeg(JPG)编码器,它只接受 8bit 全范围格式。
            // 而这条链最容易把格式带到编码器不认的地方:
            //   ① 10bit 源(P010/HEVC Main10)+ nlmeans(nlmeans 只支持 8bit)→ 链首就要隐式转换;
            //   ② HDR→SDR(hdrVf)按设计在 10bit/浮点域做色调映射 → **链末就停在 10bit/浮点**;
            // 于是编码器直接拒开:"Could not open encoder before EOF / Task finished with error code: -22 (Invalid argument)"
            // → 一帧都写不出来,整个任务 1 秒失败(用户素材 ZEB.CS.B.P010.* / LZQN.CS.* 四次全踩到)。
            // 修法:链末显式 format=yuvj420p(JPEG 标准 8bit 全范围),链里是 10bit/浮点/私有格式都能自动转过来。
            scaleVfDenoise = $"{scaleVfDenoise},format=yuvj420p";
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
                        frameCount = await ExtractFramesCoreAsync(ffmpeg, inputVideo, trimArgs, scaleVfDenoise,
                            framesIn, progress, ct, origCountEst);
                        // 【C1 修复 · 2026-09-13】这里原来【无条件】frameDurs = null —— 即使素材是 VFR
                        // (vfrPassthrough=true)也把源时长表丢掉;而下游 preserveRhythm 仍为 true(见下方注释),
                        // 于是合帧只能【静默】造一张均匀表 → 成片退化成纯 CFR:源的可变时间轴 100% 丢失,
                        // 表现就是"成片变速 + 画面相对声音最大滞后几百 ms",而日志照打「时长保护(VFR)…vfrSetpts=有」。
                        // 本分支"一帧不删":frameCount == 源帧数,与 BuildFrameDurationsAsync 的 showinfo 序列
                        // 一一对应,故可以直接用源表(同一命令已在源文件上复现过:855 项、35 个 0.0667 长间隔)。
                        // 判据走 Core.NeedsFrameDurations(有单测):VFR 素材 或 开着去重 就必须建表。
                        frameDurs = AlhPro.Core.VideoPipeline.NeedsFrameDurations(dedup, vfrPassthrough)
                            ? await BuildFrameDurationsAsync(ffmpeg, inputVideo, trimArgs, scaleVf, ct)
                            : null;
                        AppLogger.Info($"拆帧(智能-未采用拍数,不采样):帧数 {frameCount},源时长表 "
                            + $"{(frameDurs != null ? frameDurs.Count + " 项" : "未建")}(VFR 素材={vfrPassthrough},去重={dedup})");
                        // ===== 【任务 M3 · 2026-09-13】信度不足时的兜底:回退「帧差 + SSIM」逐帧检测 =====
                        // 真机反馈:素材确实有一拍N,只是转场/长静止镜头那几个离群间隔把置信度拉低,
                        // 结果"选了去重却一帧没删"。现在改为回退到与【手动-帧差+SSIM】同一条路、同一套默认参数;
                        // 三档按"删帧比例下限"决定是否真的采用(低于下限 = 没有可靠重复 → 仍原样保留):
                        //   激进 <5% → 不去重;均衡 <10% → 不去重;保守档不参与回退(它就是最不愿意删的那档)。
                        // 【待真机标定】5% / 10% 是用户给的初值,尚未真机实测。
                        // 两条早退不参与回退(与 cfInfo 语义一致):几乎无变化、几乎连续运动。
                        // 全过程无随机数:同一素材、同一档位每次结果一致。
                        bool fbStatic = cfInfo.Fps <= 0.5;
                        bool fbContinuous = !fbStatic && cfInfo.Fps >= inFps * 0.95;
                        double fbFloor = dedupSmartMode == 1 ? 0.05 : 0.10;
                        bool fbAdopted = false;
                        string fbNote;
                        if (dedupSmartMode == 2)
                            fbNote = "保守档不参与回退";
                        else if (fbStatic)
                            fbNote = "素材几乎无变化,不适用回退";
                        else if (fbContinuous)
                            fbNote = "素材几乎连续运动,不适用回退";
                        else
                        {
                            // 【任务 M4】三档力度(0.7/1.0/1.5)在这里真正生效 —— 旧实现把 force 放在
                            // DetectDupFramesAdaptive 里,而智能主路径根本到不了那个函数(识别不出拍数就直接返回),
                            // 所以三档"只有门槛不同、处理路径一样"。现在按力度缩放四个阈值(公式见 Core.DedupTier,
                            // 与旧代码同口径、同上下限),并把档位/力度/阈值/实际删帧数打进日志(三档差异可核对)。
                            double fbForce = AlhPro.Core.DedupTier.Force(dedupSmartMode);
                            double fbSadThr = AlhPro.Core.DedupTier.ScaleSad(3.0, fbForce);
                            double fbSsimThr = AlhPro.Core.DedupTier.ScaleSsim(dedupOnlyTrueHold ? 0.995 : 0.97, fbForce, dedupSmartMode);
                            double fbProtect = AlhPro.Core.DedupTier.ScaleProtect(0.30, fbForce);
                            double fbSegSad = AlhPro.Core.DedupTier.ScaleSegSad(5.0, fbForce);
                            progress?.Report((3, $"智能检测({defaultGateName}):拍数未直接采用,回退帧差+SSIM 复核(力度 ×{fbForce:0.#})..."));
                            var fbDrop = await Task.Run(() => DetectDupFramesWithSsim(framesIn, fbSadThr, fbSsimThr, fbProtect,
                                6, 16, 4, 0, fbSegSad, motionCompDedup, ct: ct, progress: progress,
                                stage: "去重分析(智能回退-帧差+SSIM)"), ct);
                            double fbRatio = fbDrop.Count / (double)Math.Max(1, frameCount);
                            fbAdopted = fbDrop.Count > 0 && fbRatio >= fbFloor;
                            AppLogger.Info($"智能检测({defaultGateName})回退帧差+SSIM:力度 ×{fbForce:0.#}"
                                + $",快筛 {fbSadThr:0.##} / SSIM {fbSsimThr:0.###} / 保护 {fbProtect:0.##} / 静止段 {fbSegSad:0.#}"
                                + $",重复 {fbDrop.Count}/{frameCount} 帧({fbRatio:0%}),档位下限 {fbFloor:0%}"
                                + $" → {(fbAdopted ? "采用(删帧)" : "低于下限,原样保留")}");
                            if (fbAdopted)
                            {
                                dedupDroppedFrames.AddRange(fbDrop);
                                var fbAll = Directory.EnumerateFiles(framesIn, "*.jpg")
                                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
                                // 与手动分支同一处落盘逻辑:尾帧保护 + 合并时长表 + 删帧/重命名
                                ApplyDedupDrop(framesIn, fbAll, new System.Collections.Generic.HashSet<int>(fbDrop), frameDurs, fbAll.Length);
                                frameCount = Directory.EnumerateFiles(framesIn, "*.jpg").Count();
                            }
                            fbNote = fbAdopted
                                ? $"回退帧差+SSIM 删 {fbDrop.Count} 帧"
                                : $"回退复核重复 {fbRatio:0%}(低于档位下限 {fbFloor:0%},原样保留)";
                        }
                        if (fbAdopted)
                        {
                            // 与手动-帧差+SSIM 分支同口径重算有效帧率(帧数已变)
                            var fullDur3 = await ProbeDurationSeconds(inputVideo);
                            double effDur3 = (trimEnd ?? fullDur3) - (trimStart ?? 0);
                            int origCount3 = effDur3 > 0 ? (int)Math.Round(effDur3 * inFps) : frameCount;
                            effectiveFps = inFps * frameCount / Math.Max(1, origCount3);
                            var learned3 = new[] { 8.0, 10, 12, 15, 24, 25, 30 }.OrderBy(a => Math.Abs(effectiveFps - a)).First();
                            string learnedMsg3 = Math.Abs(effectiveFps - learned3) / learned3 < 0.1 ? $"(内容帧率 {learned3:0} fps)" : "";
                            progress?.Report((5, $"已拆出 {frameCount} 帧(智能-{fbNote},有效帧率 {effectiveFps.ToString("0.##", inv)} fps {learnedMsg3})"));
                        }
                        else
                        {
                            effectiveFps = inFps;
                            progress?.Report((5, $"已拆出 {frameCount} 帧(智能-未采用拍数,不采样;{fbNote})"));
                        }
                        tempoSrcIdx = null;
                    }
                    else
                    {
                        // 识别出拍数:按内容帧率走网格采样(与动漫/手动同一条路)
                        double smartFc = Math.Clamp(cfInfo.Fps, 1.0, Math.Max(2.0, inFps));
                        double smartIv = inFps / smartFc;
                        progress?.Report((3, $"智能检测({defaultGateName}):内容帧率 ≈{smartFc:0.##} fps(拍型每 {smartIv:0.##} 帧,置信 {cfInfo.Confidence:0%})"));
                        var (smartFc2, smartEff, smartSrc) = await RunSegmentContentFpsAsync(ffmpeg, inputVideo, trimArgs, scaleVfDenoise,
                            framesIn, origCountEst, inFps, smartIv, 0.8, 0.4, $"智能-{smartFc:0.##}fps",
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
                            scaleVfDenoise, framesIn, progress, ct, origCountEst);
                        // 【C1 同类修复】这条分支同样是【整帧抽取、一帧不删】(不是网格采样的子集),
                        // 所以和上面"智能-未采用拍数"一样可以直接用源表 —— 旧代码丢表 → VFR 素材静默变 CFR。
                        frameDurs = AlhPro.Core.VideoPipeline.NeedsFrameDurations(dedup, vfrPassthrough)
                            ? await BuildFrameDurationsAsync(ffmpeg, inputVideo, trimArgs, scaleVf, ct)
                            : null;
                        AppLogger.Info($"拆帧(动漫-全动画,不采样):帧数 {frameCount},源时长表 "
                            + $"{(frameDurs != null ? frameDurs.Count + " 项" : "未建")}(VFR 素材={vfrPassthrough},去重={dedup})");
                        effectiveFps = inFps;
                        progress?.Report((5, $"已拆出 {frameCount} 帧(全动画,不采样)"));
                    }
                    else
                    {
                        // 动漫-拍N:按档位间隔【网格抽帧】(一拍二=每2帧留1、一拍三=每3帧留1),
                        // 与像素相似度无关 → 重编码噪音保持帧也照样去除("选动漫1拍2不去重"修复)。
                        var (fC, eff, srcA) = await RunSegmentContentFpsAsync(ffmpeg, inputVideo, trimArgs, scaleVfDenoise,
                            framesIn, origCountEst, inFps, userInterval, userTol, 0.4, modeNote,
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
                    var (fC2, eff2, srcB) = await RunSegmentContentFpsAsync(ffmpeg, inputVideo, trimArgs, scaleVfDenoise,
                        framesIn, origCountEst, inFps, uIv, 0.8, 0.4, $"手动-内容帧率 {userFc:0.##}fps",
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
                    frameCount = await ExtractFramesCoreAsync(ffmpeg, inputVideo, trimArgs, scaleVfDenoise,
                        framesIn, progress, ct, origCountEst);
                    if (dedup || vfrPassthrough)
                        frameDurs = await BuildFrameDurationsAsync(ffmpeg, inputVideo, trimArgs, scaleVf, ct);
                    // 智能 = 删"肉眼不变"帧(自适应阈值,删后帧帧都有可见变化 → 补帧后全动帧=连续感)
                    var dropA = await Task.Run(() => DetectDupFramesAdaptive(framesIn, progress, 16, dedupSmartMode, motionCompDedup,
                        ct, "去重分析(自适应)"), ct);
                    var allA = Directory.EnumerateFiles(framesIn, "*.jpg")
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
                    var dropSetA = new System.Collections.Generic.HashSet<int>(dropA);
                    if (dropA.Count > 0)
                    {
                        dedupDroppedFrames.AddRange(dropA);
                        // 【统一落盘逻辑】尾帧保护 + 合并时长表 + 删帧/重命名 + 保留源序号
                        tempoSrcIdx = ApplyDedupDrop(framesIn, allA, dropSetA, frameDurs, allA.Length);
                    }
                    else
                    {
                        tempoSrcIdx = new System.Collections.Generic.List<int>();
                        for (int n = 1; n <= allA.Length; n++) tempoSrcIdx.Add(n - 1);
                    }
                    frameCount = Directory.EnumerateFiles(framesIn, "*.jpg").Count();
                    effectiveFps = inFps * frameCount / Math.Max(1, origCountEst);
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
                        scaleVfDenoise, framesIn, progress, ct, origCountEst);
                    if (dedup || vfrPassthrough)
                        frameDurs = await BuildFrameDurationsAsync(ffmpeg, inputVideo, trimArgs, scaleVf, ct);
                    // 【修复】重CPU去重丢后台线程(原先同步调用会冻结UI线程);与 L685 同风格
                    var dropM = await Task.Run(() => DetectDupFramesWithSsim(framesIn,
                        Math.Clamp(dedupSadThr, 0.5, 10.0), Math.Clamp(dedupSsimThr, 0.90, 0.999),
                        dedupProtect, dedupWindow, dedupScale, dedupBlockThr,
                        dedupSegOn ? Math.Clamp(dedupSegSsim, 0.80, 0.99) : 0, Math.Clamp(dedupSegSad, 2, 10),
                        protectSmallMotion: manualProtectSmallMotion,
                        ct: ct, progress: progress, stage: "去重分析(帧差+SSIM)"), ct);   // 手动模式"微动防线"开关(默认开)
                    if (dropM.Count > 0)
                    {
                        dedupDroppedFrames.AddRange(dropM);
                        var allM = Directory.EnumerateFiles(framesIn, "*.jpg")
                            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
                        // 尾帧恒保留(与智能分支同判据):结尾画面组绝不因去重而丢;
                        // 否则保留帧轴到不了源末帧 → 补回提前截止(时长缩水)+ 下游越界。
                        var dropMSet = new System.Collections.Generic.HashSet<int>(dropM);
                        if (dropM.Count > 0)
                        {
                            dedupDroppedFrames.AddRange(dropM);
                            // 【统一落盘逻辑】尾帧保护 + 合并时长表 + 删帧/重命名 + 保留源序号
                            tempoSrcIdx = ApplyDedupDrop(framesIn, allM, dropMSet, frameDurs, allM.Length);
                        }
                        else
                        {
                            tempoSrcIdx = new System.Collections.Generic.List<int>();
                            for (int n = 1; n <= allM.Length; n++) tempoSrcIdx.Add(n - 1);
                        }
                    }
                    frameCount = Directory.EnumerateFiles(framesIn, "*.jpg").Count();
                }
                else if (dedupMode == 3)
                {
                    // 手动模式(重复帧检测):完全按用户自由参数
                    var dedupVf = $"mpdecimate=hi=64*{Math.Clamp(dedupHi, 4, 24)}:lo=64*{Math.Clamp(dedupLo, 2, 10)}:frac={Math.Clamp(dedupFrac, 0.1, 0.6):0.##}";
                    progress?.Report((3, "去重:检测重复帧(mpdecimate)..."));
                    frameCount = await ExtractFramesCoreAsync(ffmpeg, inputVideo, trimArgs,
                        $"{dedupVf},{scaleVfDenoise}", framesIn, progress, ct, origCountEst);
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
                        scaleVfDenoise, framesIn, progress, ct, origCountEst);
                    if (dedup || vfrPassthrough)
                        frameDurs = await BuildFrameDurationsAsync(ffmpeg, inputVideo, trimArgs, scaleVf, ct);
                    // 逐帧检测(全帧解码小图+SAD/SSIM,CPU 重活)→ 后台线程,防拆帧后卡 UI
                    var drop = dedupMode == 1
                        ? await Task.Run(() => DetectDupFramesAdaptive(framesIn, progress, 16, dedupSmartMode, motionCompDedup,
                            ct, "去重分析(自适应)"), ct)
                        : await Task.Run(() => DetectDupFramesWithSsim(framesIn, sadThr, ssimThr, protectRatio, 6, 16, 4, segSsim, segSad,
                            motionCompDedup, ct: ct, progress: progress, stage: "去重分析(帧差+SSIM)"), ct);
                    // 末帧永远保留:视频最后一张画面即使与前一帧相似也必须保留,
                    // 否则输出尾部会缺失原视频末帧内容(用户看到"最后一帧不是原视频最后一帧")。
                    if (drop.Count > 0)
                    {
                        dedupDroppedFrames.AddRange(drop);
                        var allFiles = Directory.EnumerateFiles(framesIn, "*.jpg")
                            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
                        // 【统一落盘逻辑】尾帧保护 + 合并时长表 + 删帧/重命名 + 保留源序号
                        ApplyDedupDrop(framesIn, allFiles, new System.Collections.Generic.HashSet<int>(drop), frameDurs, allFiles.Length);
                    }
                    frameCount = Directory.EnumerateFiles(framesIn, "*.jpg").Count();
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
                string sceneSelect = dedup ? $"select='eq(n,0)+gt(scene,{dedupThr.ToString("0.###", inv)})'," : "";
                string sceneVf = $"{sceneSelect}{scaleVf}";                  // 分析用(时长表/帧号探测:不带降噪,纯解码即可)
                string sceneVfDenoise = $"{sceneSelect}{scaleVfDenoise}";    // 真正抽帧用(带降噪,与其它抽帧路径一致)
                frameCount = await ExtractFramesCoreAsync(ffmpeg, inputVideo, trimArgs,
                    sceneVfDenoise, framesIn, progress, ct, origCountEst);
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
                // 【修复】重CPU运动分析丢后台线程(原先同步调用冻结UI线程)
                var dropPan = await Task.Run(() => DetectDupFramesWithMotion(framesIn, Math.Clamp(dedupPanThr, 1, 10), progress, dedupScale, dedupProtect, dedupBlockThr, Math.Clamp(dedupPanMax, 10, 60), ct), ct);
                if (dropPan.Count > 0)
                {
                    dedupDroppedFrames.AddRange(dropPan);
                    var allPan = Directory.EnumerateFiles(framesIn, "*.jpg")
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
                    // 【统一落盘逻辑】尾帧保护 + 合并时长表 + 删帧/重命名(该分支不构建保留源序号)
                    ApplyDedupDrop(framesIn, allPan, new System.Collections.Generic.HashSet<int>(dropPan), frameDurs, allPan.Length);
                }
                frameCount = Directory.EnumerateFiles(framesIn, "*.jpg").Count();
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
                        ? Directory.EnumerateFiles(framesIn, "*.jpg").Count() + removedTotal
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
            // 【任务 U】阶段①统计:拆帧(+降噪)+ 去重 到此结束
            UMark("准备(拆帧+去重)");
            USampleTemp();

            // 指定输出帧率时:自动算够补帧倍率(不再用固定倍率,保证"填多少最终就多少")。
            // 内容帧率在去重后已确定,目标帧率 ÷ 内容帧率 = 需要的倍率,向上取整。
            if (frameInterp && targetFps is > 0)
            {
                // 达到目标帧率所需倍率:A(内容×倍率)用内容帧率 effectiveFps;B/方案C(原×倍率)用原帧率 inFps。
                // 关键:目标帧率≈输入帧率(如 60 vs 59.94)时 ceil=1 → 完全不补帧 → "指定帧率导出还是卡"。
                // 倍率至少 2x:先补帧平滑再压到目标帧率(否则等于没补帧)。
                // 【唯一一份判据】倍率只允许由 Core.VideoPipeline.InterpScaleForTargetFps 决定 ——
                // 这里原先内联了一遍 `max(2, min(8, ceil(目标 ÷ 基准)))` 外加自己的 2 的幂回退,
                // 与界面预判(VideoView.UpdateTargetFpsHint)各维护一份,"谁改了都会让界面与实跑对不上"。
                // 该函数同时负责:基数只认源帧率(去重后拿内容帧率当基数会把倍率算大 frameScale 倍)、
                // 下限 2(目标≈源帧率时 ceil=1 会完全不补帧 ⇒ 指定了还是卡)、上限 8、非 v4 向上取 2 的幂。
                int needScale = AlhPro.Core.VideoPipeline.InterpScaleForTargetFps(
                    targetFps.Value, inFps, effectiveFps, IsV4Model(interpModel));
                if (needScale != interpScale)
                {
                    progress?.Report((6, $"指定 {targetFps.Value:0.##} fps,自动补帧 {needScale}x(内容帧率 {effectiveFps:0.##} fps)"));
                    AppLogger.Info($"目标帧率:指定 {targetFps.Value:0.##} fps → 自动补帧 {needScale}x(内容帧率 {effectiveFps:0.##} fps)");
                }
                interpScale = needScale;
                // ② 【倍率定稿后必须复判空间】倍率被「指定帧率」抬高 ⇒ 峰值帧数 = 源帧数×倍率 跟着涨,
                //    空间需求可能接近翻倍。原先只在拆帧前算过一次(那时的倍率还是用户选的),守门按低估的账放行,
                //    实盘事故就是"按 2x 报 29.2GB 放行、按 4x 跑"→ 跑到一半临时盘爆掉、后半段成占位帧。
                //    这里按**最终倍率**重算峰值帧数并复判;复判只能收紧不能放松(见下一行)。
                peakFrames = frameInterp ? (long)Math.Ceiling((double)baseFrames * interpScale) : baseFrames;
                {
                    var gateNow = EvalTempSpaceGate(false);
                    diskTight = diskTight || gateNow.t;
                }
            }

            // 需要"重定时"(setpts 保内容时间轴)的情况:真 VFR 素材,【以及去重删过帧的素材】。
            // 去重删过帧后剩余关键帧在真实时间上不等距(有长静止段、有快速动作);补帧若按帧序号均匀铺 + 固定帧率
            // 合帧,会把真实时间压缩/拉长 → 普遍"掉帧/节奏错"(研究结论#1,用户实测"有补帧也不够流畅")。
            // 故去重删过帧时也用「每帧真实时长表」重定时:输出 VFR 时间轴=原视频节奏,补帧只负责填运动、不改变时间。
            bool preserveRhythm = vfrPassthrough || (dedup && frameDurs != null && frameDurs.Count == frameCount);

            // 3) 补帧(可选):按下面 InterpStageAsync 执行,转场识别时按转场点分段,段内插值、转场处不插。
            //    【阶段顺序】1x/2x 走「超分 → 补帧」(补帧在超分输出上做),3x/4x 走「补帧 → 超分」(旧顺序,一字不改)。
            //    旧注释里"避免在大图上补帧造成 9 倍开销"只对高倍率成立:实测 2x@1080p→2160p 补帧 0.10→0.35 秒/输出帧,
            //    而超分帧数减半省下的是 0.70 秒/帧 —— 1x/2x 上新顺序赢;4x(→4320p 补帧 0.76~1.09 秒/输出帧)仍是旧顺序赢。
            var frameFiles = Directory.EnumerateFiles(framesIn, "*.jpg")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
            // 最终帧时长表(合帧用):有原始时长表时,补帧按"每源帧展开"填充,否则 null → 固定帧率输出
            System.Collections.Generic.List<double>? finalDurs = null;
            // 防御:时长表帧数与实际帧数不一致(探测异常)时整体回退固定帧率,绝不让索引越界
            if (frameDurs != null && frameDurs.Count != frameCount) frameDurs = null;
            // ===== 补帧阶段(3b)的计划量与本体 =====
            // 【为什么声明在 3) 之外】1x/2x 新顺序下补帧在超分【之后】执行,而下面 3) 块里算出的 segBounds/
            // frameScale/globalTarget 必须在那之前就绪。它们只依赖 frameCount/origCountEst/fpsMode/cuts ——
            // 与"补帧输入目录是源分辨率还是超分后分辨率"无关(超分保帧数,帧号一一对应),故提前声明、
            // 由 3) 块赋值、两种顺序共用。
            var segBounds = new System.Collections.Generic.List<(int s, int e)>();
            double frameScale = 1.0;
            long globalTarget = 0;
            int globalIdx = 1;
            // 【2026-09-16 · 指定帧率必须改**每源帧展开帧数**,否则帧数对不上、尾部真丢内容】
            // 只改"标称帧率"是不够的:补帧引擎按**整数倍率** ceil(目标÷源) 产出,多补的部分只能被裁掉。
            // 真机实证(源 519 帧 @23.976 / 21.632s,指定 60fps,自动倍率 3x):引擎补出 1555 帧,
            // 而 60fps 只容得下 1298 帧 ⇒ 合帧「裁尾 253 帧」= 尾部 4.2 秒内容消失(画面节奏对了、内容没了)。
            // 修法:这里放一个"分数步长"变量,由下面算出的目标帧数填进来(目标帧数 ÷ 源帧数),
            // 供 RIFE 分段 `-n`、VFR 时长表展开、诊断口径三处共用 —— 三者必须同源。
            // 1.0 = 不覆盖(不指定帧率时逐字走原整数倍率逻辑)。
            double multTargetFpsOverride = 1.0;
            // 【任务 S1 · 2026-09-13】"平滑时间轴(按时轴填平)"的状态:必须声明在 3) 块之外 ——
            // 合成在 InterpStageAsync 里发生(两种阶段顺序共用一个调用点),而下游"帧数对齐 / 帧率口径 /
            // 时长保护"都读它。**flattenActive 为 false 时,老路径一个字节都不变**(硬要求)。
            bool flattenActive = false;
            AlhPro.Core.TimelineFlattenPlan.Plan flatPlan = default;
            int flattenForcedCopies = 0;   // 因切点被强制改拷贝的槽数(审计用)
            int flattenCuts = 0;           // 检出的场景硬切处数(审计用)
            // 【共享切点表 · 2026-09-15 统一】全流程的切点判据**只算一次**(见 3a 块),结果放这里给两条路共用:
            //   · 普通分段路径(segBounds)在 3a 块里直接用 cuts;
            //   · "按真实时间戳排帧"那条路(FlattenTimelineAsync)把它当形参收下 —— 那条路自己**不再采样、不再判定**。
            // 口径 = 源帧下标 i(切点在源帧 i 与 i+1 之间),即 3a 块里 c(= i+1)换算下来的 c-1。
            // 必须声明在 InterpStageAsync **之前**:合成发生在那个局部函数里(两种阶段顺序共用同一个调用点),
            // 而局部函数只能捕获声明在它之前的变量。
            var sceneCutPairs = new System.Collections.Generic.List<int>();

            // 3b) 分段补帧(RIFE)本体 —— 局部函数,两种阶段顺序共用同一份实现,只有"输入/输出目录 + 探测帧尺寸"不同:
            //   · 旧顺序(3x/4x/不实际超分):输入 framesIn(源帧)、输出 framesFinal,在 3) 块里原地调用;
            //   · 新顺序(1x/2x):输入 upOutput(超分输出)、输出 framesInterp,在超分阶段之后调用。
            // probeW/probeH = 本阶段真正要处理的帧尺寸:RIFE 的 GPU 兼容性探测必须按【真实帧尺寸】做 ——
            // 小图能跑 ≠ 真帧能跑(显存/着色器分块压力差一个量级,实测过"小图通过、真分辨率上静默出坏帧")。
            // 【边用边删】每段跑完即删该段已消费的输入帧:分段互不共享输入帧(段 [s,e) 只读 frame_{s+1}..frame_{e}),
            // 且 InterpSegmentAsync 在段首就把这一段全部复制进自己的 segIn 目录,其后所有重试/降级路径
            // (ncnn 原卡、换另一张卡、ONNX DirectML、黑帧回退、残缺重算)都只读 segIn —— 故这些帧可证明已消费。
            // 【同步探针】本方法里凡是"必须在某一步之前拿到值"的地方,都用这个同步版 —— 不再用 await。
            // 【为什么要同步】真机事故(2026-09-16 20:10):`指定帧率口径` 那段里的 `await ProbeDurationSeconds(inputVideo)`
            // 把步长赋值推迟到了**补帧段跑完之后**(日志实证:段执行 20:10:14.433 用了 fractionalMult=1,
            // 而赋值 20:11:14.756 才到)⇒ 引擎仍按整数倍率 3x 铺帧 ⇒ 切点后移 1.19 倍 = 用户看到的"指定补帧依然变速"。
            // 现在改成:在补帧阶段**之前**同步取到容器时长、把分数步长算好并存进 stageFractionalMult,
            // 既不再被 await 推迟,也不依赖后面那段代码的执行时机。
            double ProbeContainerDurationSync()
            {
                try
                {
                    var ffp2 = FfmpegPath;
                    var pdir = ffp2 != null ? Path.GetDirectoryName(ffp2) : null;
                    var probeExe = pdir != null ? Path.Combine(pdir, "ffprobe.exe") : null;
                    if (probeExe == null || !File.Exists(probeExe)) return 0;
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = probeExe,
                        Arguments = $"-v error -show_entries format=duration -of default=nw=1:nk=0 \"{inputVideo}\"",
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true,
                    };
                    using var pr = System.Diagnostics.Process.Start(psi);
                    if (pr == null) return 0;
                    string so = pr.StandardOutput.ReadToEnd();
                    pr.StandardError.ReadToEnd();
                    if (!pr.WaitForExit(15000)) { try { pr.Kill(entireProcessTree: true); } catch { } return 0; }
                    double d = AlhPro.Core.ProbeFields.DoubleField(so, "duration") ?? 0;
                    return d > 0.01 ? d : 0;
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"容器时长同步探针失败(退回内容时长):{ex.GetType().Name}: {ex.Message}");
                    return 0;
                }
            }
            // 补帧阶段真正使用的"每源帧展开帧数"(分数)。1.0 = 不覆盖(走原整数倍率)。
            double stageFractionalMult = 1.0;
            // 指定帧率模式的**目标总帧数**(合帧那边真正容得下的帧数)。分段层的末段必须锚到它,
            // 而不是锚到 globalTarget —— globalTarget 是按整数倍率算的旧口径(真机:907),
            // 末段一旦锚回 907,分数步长就被覆盖(日志实证:fraction=2.5066 但"目标帧数=907")。
            long stageTargetFrames = 0;
            if (frameInterp && targetFps is > 0 && IsV4Model(interpModel) && frameCount > 1)
            {
                double contDur = ProbeContainerDurationSync();
                if (contDur > 0)
                {
                    long want = (long)Math.Round(targetFps.Value * contDur);
                    stageTargetFrames = want;
                    stageFractionalMult = (want - 1) / (double)(frameCount - 1);
                    AppLogger.Info($"指定帧率步长(阶段前同步计算):{targetFps.Value:0.##} fps × 容器 {contDur:0.###}s ≈ {want} 帧,"
                        + $"补帧输入 {frameCount} 帧 ⇒ 每源帧 {stageFractionalMult:0.###} 帧(整数倍率 {interpScale}x 会是 {frameCount * interpScale} 帧)");
                }
            }

            async Task InterpStageAsync(string segSrcDir, string segOutDir, int probeW, int probeH, double fractionalMult, long targetFramesOverride)
            {
                // 【任务 O2 · 2026-09-13】8K 级输入下 ncnn RIFE 会**静默输出全黑帧**(真机实测
                // 7680×4320 + -n 119 → 117/119 全黑,exit=0 无报错;1080p/2160p 同命令 0 黑帧)。
                // 与其白跑一遍再靠事后抽样抓黑帧(还有抽样漏掉的风险),不如按尺寸预检直接改走稳定引擎(ONNX)。
                var sizeVerdict = AlhPro.Core.InterpSizePolicy.JudgeInputSize(probeW, probeH);
                bool forceOnnxInterp = sizeVerdict.RefuseNcnn;
                if (forceOnnxInterp)
                {
                    AppLogger.Warn($"⚠ 补帧输入尺寸预检:{sizeVerdict.Reason} → 本阶段改用稳定引擎(ONNX)补帧,不交给 ncnn");
                    progress?.Report((interpPctBase, $"⚠ 补帧输入 {probeW}×{probeH} 属于 ncnn 已知故障尺寸(会静默出全黑帧),改用稳定引擎(ONNX)..."));
                }
                else if (sizeVerdict.Warn)
                {
                    AppLogger.Warn($"⚠ 补帧输入尺寸提示:{sizeVerdict.Reason}");
                }
                // 【O2 · 1 帧目录 + -n 2 崩溃护栏】RIFE 至少要两帧输入、且目标帧数必须大于输入帧数
                // (真机实测"1 帧目录 + `-n 2`"→ 0xC0000005 访问违例)。本仓调用点**本就不可达**:
                // 单帧段走"直接复制不进引擎"、且 `-n` 有 `segLen + 1` 下限(见下方 InterpSegmentAsync),
                // 这里只做防御性提示,不改变控制流。
                if (AlhPro.Core.InterpSizePolicy.IsDegenerateSegmentInput(frameCount, (int)Math.Min(int.MaxValue, globalTarget)))
                    AppLogger.Warn($"⚠ 补帧入参护栏:输入 {frameCount} 帧 / 目标 {globalTarget} 帧构成退化入参"
                        + "(RIFE 至少需 2 帧输入且目标 > 输入,实测会崩 0xC0000005);本阶段按逐帧复制处理,不给引擎");
                // 【任务 N】这里印的"输出 N 帧"改成用【真实倍率 mult】算的帧数守恒目标:
                // 2x + 未去重时 = (源帧数-1)×2+1(真机那次是 855 → 1709);末段不再产出末帧冻结副本。
                int multStage = AlhPro.Core.VideoPipeline.InterpMultiplier(interpScale, frameScale);
                // 【统一口径变量】指定帧率模式下 multRife = **分数步长**(目标帧数 ÷ 源帧数),
                // 否则 = 与改动前逐字相同的整数倍率。凡是"每源帧展开多少帧"的口径一律用它:
                // RIFE 分段目标 / VFR 时长表展开 / 诊断行 —— 三处必须同源。
                double multRife = multTargetFpsOverride > 1.0 ? multTargetFpsOverride : multStage;
                long expectStage = AlhPro.Core.VideoPipeline.InterpOutputFrameCount(frameCount, (int)Math.Round(multRife));
                progress?.Report((interpPctBase, $"RIFE 补帧({interpScale}x,源 {frameCount} 帧 → 输出 {expectStage} 帧,模型 {interpModel})..."));
                if (interpScale >= 4)
                    AppLogger.Warn($"⚠ 高倍率补帧({interpScale}x):输出帧数是源 {interpScale} 倍,处理耗时会明显变长,属正常,请耐心等待(非卡死)");
                // ===== RIFE GPU 探测(任何可能静默 hang 的设备都不放过,不预检白等 8 分钟)=====
                // 实测 2 帧插 1 帧能否 GPU 出图;不能 → 本视频补帧改用 ONNX(慢但确定能跑),日志+进度提示。
                // rife 非空由调用点保证(两处调用都只在 frameInterp 成立时;而 frameInterp 且 RifePath 缺失
                // 时本方法开头就抛了异常),这里显式取本地量,避免可空性告警。
                string rifeExe = rife ?? throw new InvalidOperationException("补帧引擎未就绪(rife-ncnn-vulkan 未找到),无法补帧");
                int interpGpu = gpuId;
                if (gpuId >= 0)
                {
                    // 【50 系不再"一律禁用 ncnn"】旧逻辑:Blackwell 一律直接改走 ONNX、不做任何探测。
                    // 现在改为"先真机探测、再按结果决定":探测通过 → 用 ncnn-Vulkan 补帧(最快那条路);
                    // 失败(hang/崩/坏帧)→ 才改走 ONNX,并明确告知用户"为稳定性改用 ONNX"。
                    // 结论跨任务缓存(EnsureRifeNcnnProbeAsync),同一设备不会每次任务都白等一遍探测。
                    // 【按真实帧尺寸探】探测帧尺寸 = 本阶段真正要处理的尺寸(旧实现固定 320×240):小图能跑 ≠ 真帧能跑,
                    // 显存/着色器分块压力差一个量级,实测过"小图通过、真分辨率上静默出坏帧"的形态。
                    // 新顺序(1x/2x)下探测尺寸 = 超分后的尺寸(probeW/probeH),旧顺序 = 源尺寸(与改动前逐字一致)。
                    // 【2026-09-18 用户要求:这类"正在检测…首次约 N 秒"的提示别再无脑弹】
                    // 只有**真的会做实测**的卡(50 系/AMD/Intel/已知有风险)才提示;纯 NVIDIA 非 50 系走快速通道,
                    // 检测秒回、无需等待,就不弹这句(免得用户以为在等 10 秒)。探测调用本身保留(它是唯一防线)。
                    bool rifeWillRun = true;
                    try { rifeWillRun = EngineService.RifeProbeWillRun(rifeExe, interpModel, gpuId, probeW, probeH); } catch { }
                    if (rifeWillRun)
                        progress?.Report((interpPctBase, $"正在检测补帧 GPU 兼容性(按补帧输入帧尺寸 {probeW}×{probeH} 实测,首次约 10 秒,结论会记住,失败重试一次)..."));
                    bool rifeOk = await EngineService.EnsureRifeNcnnProbeAsync(rifeExe, interpModel, gpuId, ct, probeW, probeH).ConfigureAwait(false);
                    if (!rifeOk)
                    {
                        // 【按形态说话】失败原因由探测带回(初始化即崩 ≠ 出图但坏帧),措辞集中在 AlhPro.Core.ProbeDiagnosis:
                        // 前者在 Blackwell 上就是 NVIDIA 的 cooperative-matrix 驱动缺陷(要说清"不是本软件的问题"),
                        // 后者属引擎并发/渲染(不许甩锅给驱动)。
                        AppLogger.Warn($"⚠ RIFE {interpModel} 在本机 GPU({gpuId})真机探测失败——为稳定性改用 ONNX 补帧路线。"
                            + EngineService.LastProbeUserMessage);
                        progress?.Report((interpPctBase, $"⚠ 补帧 ncnn 引擎在本机不可用,为稳定性自动改用 ONNX 补帧..."));
                        interpGpu = -1;   // 本视频后续补帧 API 全部走 ONNX(InterpSegmentAsync 传入)
                    }
                    else
                    {
                        AppLogger.Info($"✅ RIFE {interpModel} GPU({gpuId})真机探测通过({probeW}×{probeH})→ 使用 ncnn-Vulkan 补帧(未因 50 系而禁用)");
                    }
                }
                // ===== 【任务 S1 核心】"平滑时间轴"合成:按真实 PTS 逐槽合成,取代按段 `-n` 均匀铺帧 =====
                // 触发前提(缺一不可;任一不满足 → 原样落到下面的分段路径,老行为一个字节不变):
                //   ① 判定"可填平"(源时长表有缺口)且用户在选项里开着「平滑时间轴」→ flatPlan.Flatten(见 3) 块);
                //   ② 本阶段输入帧数与时长表一一对应(否则索引不可信,直接放弃);
                //   ③ 补帧引擎走 ncnn-Vulkan(interpGpu ≥ 0)且不是"8K 级强制 ONNX"的尺寸 —— 因为逐槽合成用的是
                //      层批原语(EngineService.InterpLayerBatchAsync),它只有 ncnn 一条路;探针失败/ONNX 路线下
                //      **不做填平**,回退到分段路径(那里有完整的 ONNX/换卡/黑帧降级链);
                //   ④ 合成结果帧数必须**恰好等于** flatPlan.TargetFrames(不符 → 清掉半成品,回退分段路径)。
                // 【复用而非新写:并发与降级链】槽位→帧的落盘走 FlattenTimelineAsync → EmitSlotFramesAsync,
                // 后者就是"补回(还原源时间轴)"在用的同一套层批原语与并发结构(InterpLayerBatchAsync:
                // 一批帧对平铺成目录序列、一次引擎进程跑完,二叉树逐层细分,每层一次引擎调用)——
                // 本任务**没有**新写并发/降级链;黑帧检出、帧数校验、失败回退在这里补齐。
                if (flatPlan.Flatten && !forceOnnxInterp && interpGpu >= 0 && flatPlan.TargetFrames >= 2)
                {
                    int targetN = flatPlan.TargetFrames;
                    progress?.Report((interpPctBase, $"平滑时间轴:按真实时间轴合成 {targetN} 帧(目标 {flatPlan.TargetFps:0.##} fps)..."));
                    try
                    {
                        var fres = await FlattenTimelineAsync(rifeExe, segSrcDir, segOutDir,
                            frameCount, frameDurs, flatPlan, interpGpu, interpModel, tta, sceneCutPairs, progress, ct);
                        if (fres.written != targetN)
                            throw new InvalidOperationException($"合成帧数 {fres.written} ≠ 目标 {targetN} 帧");
                        if (fres.anyBlack)
                            throw new InvalidOperationException("合成输出检出黑帧(GPU 队列异常)");
                        flattenActive = true;
                        flattenCuts = fres.cuts;
                        flattenForcedCopies = fres.forcedCopies;
                        globalTarget = targetN;
                        globalIdx = fres.written + 1;
                        frameScale = 1.0;        // 时间轴已由真实 PTS 直接给出,不再有"原密度缩放"这一步
                        frameDurs = null;        // 输出是均匀时间轴 → 下游不许再按源时长表铺 PTS(见"时长保护")
                        preserveRhythm = false;  // 同上:填平 = 把时间轴变均匀,不再走 VFR setpts 那条路
                        AppLogger.Info($"平滑时间轴:合成完成 —— 输出 {fres.written} 帧 @ {flatPlan.TargetFps:0.##} fps"
                            + $"(源 {frameCount} 帧 / 真实时长 {flatPlan.TotalSeconds:0.###}s);场景硬切 {fres.cuts} 处、"
                            + $"因切点强制拷贝 {fres.forcedCopies} 槽(不生成跨切混合帧);落在源帧上的槽直接拷贝、不调引擎");
                        progress?.Report((interpPctBase + interpPctSpan, $"补帧完成({fres.written} 帧,平滑时间轴)" + StageElapsed()));
                        return;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // 失败/半成品 → 先清干净再回退:半截帧留在输出目录会让分段路径的帧号重号
                        try { foreach (var f in Directory.EnumerateFiles(segOutDir, "*.*")) File.Delete(f); } catch { }
                        AppLogger.Warn($"⚠ 平滑时间轴合成失败({ex.Message.Split('\n')[0]})→ 回退分段补帧"
                            + "(走既有 ncnn→ONNX→换卡 降级链,行为与改动前一致)");
                    }
                }
                else if (flatPlan.Flatten)
                {
                    AppLogger.Info("平滑时间轴:判定可填平,但本次不做逐槽合成(补帧引擎走 ONNX 路线,或尺寸预检按 8K 级处理) "
                        + "→ 按分段补帧输出(行为与改动前一致;ONNX 逐对路径不支持任意时间步)");
                }
                // 补帧阶段自己的 ETA 时钟:segProg 会用全局帧号重建消息文本,引擎内部那层加不上 ETA,只能在这里算。
                // base = 本阶段开始前已写出的帧数(前一趟/补洞留下的),速率只对"本阶段真正产出的帧"计算,
                // 否则 done 含旧帧 → 速率虚高 → ETA 偏乐观。
                var interpStageStart = DateTime.UtcNow;
                long interpBase = Math.Max(0, globalIdx - 1);
                double interpIdleSec = 0;
                int segNo = 0;
                int releasedConsumed = 0;   // 本阶段已按段释放的输入帧数(审计用)
                int interpCleanupLogged = 0;   // 【R1】已写进日志区的清理进度(每累计 800 帧一行,避免刷屏)
                // 【G-补2 · 2026-09-13】阶段末之后【马上】还有一整批收尾:把补帧输出的 PNG 全部重编码成 JPG
                // (ReencodeDirPngToJpg(framesFinal),耗时正比于帧数 —— 用户那条 2668 帧要跑几分钟)。
                // 这条 ETA 只按补帧引擎自己的帧数外推,不知道后面还有这一步;于是最后一两帧时它会算成
                // "不足 1 秒",界面显示"预计还剩几秒"而后面还有几分钟的活(用户原话:"几???")。
                // 处理:①<1 秒那一档改口径并带上本说明(见 Core.EtaText);
                //      ②这段收尾自己在界面发进度 + 自己的 ETA(G 主条),用户不会停在假数字上。
                string interpWrapUp = $"{globalTarget} 帧整理成 JPG";
                for (int si = 0; si < segBounds.Count; si++)
                {
                    var (s, e) = segBounds[si];
                    segNo++;
                    bool isLastSeg = si == segBounds.Count - 1;
                    // 包装进度:把本段帧号映射到全局累计,显示"总帧慢慢加上去"(而不是已处理/expand 帧数)。
                    // 帧号只从消息的「第 N 帧」取,且单调不回退:没有帧号的消息(降温休息、引擎告警)沿用上一个
                    // 帧号。原先此时改用 t.pct/100*segTarget 猜,而内层各路径的 pct 口径不一(ncnn 目录轮询
                    // 1..90、ONNX 10..45),猜出来既偏小又会回退 → 段内帧号抖动、长时间不动再猛跳。
                    // 区间用 interpPctBase/Span:旧顺序 10~45、新顺序 45~90 —— 与真正在跑的阶段一致。
                    int segLastLocal = 0;
                    IProgress<(int pct, string msg)>? segProg = progress == null ? null
                        : new System.Progress<(int pct, string msg)>(t =>
                        {
                            if (t.msg.Contains("⚠", StringComparison.Ordinal))
                            {
                                int warnPct = interpPctBase + (int)((double)interpPctSpan * segNo / Math.Max(1, segBounds.Count));
                                progress!.Report((warnPct, t.msg));
                                progress!.Report((warnPct, "· " + t.msg));
                                return;
                            }
                            var m = System.Text.RegularExpressions.Regex.Match(t.msg, @"第\s*(\d+)\s*帧");
                            if (m.Success) segLastLocal = int.Parse(m.Groups[1].Value);
                            long gf = (long)Math.Min(globalTarget, (globalIdx - 1) + segLastLocal);
                            progress!.Report((interpPctBase + (int)((double)interpPctSpan * gf / Math.Max(1, globalTarget)),
                                $"补帧 第 {gf} 帧 / 共 {globalTarget} 帧" +
                                EtaStr(gf - interpBase, globalTarget - interpBase,
                                    (DateTime.UtcNow - interpStageStart).TotalSeconds - interpIdleSec, interpWrapUp)));
                        });
                    progress?.Report((interpPctBase + (int)((double)interpPctSpan * segNo / segBounds.Count),
                        $"补帧 第 {globalIdx - 1} 帧 / 共 {globalTarget} 帧(段 {segNo}/{segBounds.Count})" +
                        EtaStr(globalIdx - 1 - interpBase, globalTarget - interpBase,
                            (DateTime.UtcNow - interpStageStart).TotalSeconds - interpIdleSec, interpWrapUp)));
                    // 【让"段间停顿"可见】每一段都是【一次新的 RIFE 进程】(启动 + 模型加载是秒级固定开销,
                    // 高倍率非 v4 模型还要级联多次),先说明"正在启动补帧引擎";引擎真出帧后再报"已就绪(启动 X.Xs)"。
                    // 文案不含「完成」二字:UI 会把含"完成"的进度行改写成"✓ …"并清掉当前步骤行。
                    // 只加两行进度上报,段划分/阶段顺序/引擎参数/并发一律不变。
                    {
                        double segStartSec = AlhPro.Core.VideoPipeline.AssumedEngineStartupSecondsPerBatch;   // 待实测标定
                        progress?.Report((interpPctBase + (int)((double)interpPctSpan * segNo / segBounds.Count),
                            $"第 {segNo}/{segBounds.Count} 段:启动补帧引擎(约 {segStartSec:0.#} 秒,首次较慢;本段 {e - s} 帧)…"));
                    }
                    int segReadyReported = 0;
                    Action<double> segOnEngineReady = sec =>
                    {
                        if (Interlocked.Exchange(ref segReadyReported, 1) != 0) return;
                        try
                        {
                            progress?.Report((interpPctBase + (int)((double)interpPctSpan * segNo / segBounds.Count),
                                $"第 {segNo}/{segBounds.Count} 段:补帧引擎已就绪(启动 {sec:0.0}s),开始本段 {e - s} 帧…"));
                            AppLogger.Info($"第 {segNo}/{segBounds.Count} 段:补帧引擎已就绪(启动 {sec:0.0}s,本段帧 {s + 1}~{e})");
                        }
                        catch { }
                    };
                    // 处理过程也做降温休息检查(单个长视频也能中途休息)
                    var interpIdleT0 = DateTime.UtcNow;
                    await SafeRender.RestIfDueAsync(interpPctBase + (int)((double)interpPctSpan * segNo / segBounds.Count), progress, ct);
                    if (pauseWait != null) await pauseWait();   // 暂停:当前补帧段跑完即停(几秒)
                    interpIdleSec += (DateTime.UtcNow - interpIdleT0).TotalSeconds;
                    // 时长表展开:真 VFR 素材 或 去重删过帧(内容时间轴不均匀)时,展开每帧真实时长供 setpts 重定时;
                    // 否则按固定帧率均匀输出,无需展开。
                    if (frameDurs != null && preserveRhythm)
                    {
                        finalDurs ??= new System.Collections.Generic.List<double>();
                        // 【任务 N · 2026-09-13】展开倍率用补帧【真实倍率 mult】(= round(interpScale×frameScale)),
                        // 不是 interpScale:RIFE 每段就是按 `-n = 段长×mult` 产出,表必须与文件数同口径
                        // (旧实现在去重时表长只有文件数的一半 → AlignDurationsToCount 用均值补尾 → 尾部时间轴失真)。
                        // 末段的末源帧只展开 1 条(承载尾部容积的整段时长):
                        // 于是"表长 == 文件数 == (源帧数-1)×mult+1",帧数守恒(见 Core.AppendExpandedDurations)。
                        // 【指定帧率 · 分数步长】目标帧数给定后用 Exact 版:每段槽位按"该段精确目标帧数 ÷ 段长"分配,
                        // 与 InterpSegmentTargetExact 同口径(表长 == 文件数)。整数倍率那条路逐字不变。
                        if (multTargetFpsOverride > 1.0)
                        {
                            int segTargetForDurs = AlhPro.Core.VideoPipeline.InterpSegmentTargetExact(
                                e - s, e, frameCount, globalTarget, globalIdx - 1);
                            AlhPro.Core.VideoPipeline.AppendExpandedDurationsExact(finalDurs, frameDurs, s, e, segTargetForDurs, isLastSeg);
                        }
                        else
                        {
                            int multExpand = AlhPro.Core.VideoPipeline.InterpMultiplier(interpScale, frameScale);
                            AlhPro.Core.VideoPipeline.AppendExpandedDurations(finalDurs, frameDurs, s, e, multExpand, isLastSeg);
                        }
                    }
                    globalIdx = await InterpSegmentAsync(rifeExe, segSrcDir, segOutDir, s, e, interpScale,
                        interpModel, timeStep, tta, interpGpu, globalIdx, segProg, ct, frameScale,
                        isLastSeg ? globalTarget : 0,
                        false,   // appendTailCopy = false
                        segOnEngineReady,   // 引擎"已就绪"上报(只上报,不改处理)
                        forceOnnx: forceOnnxInterp,   // 【O2】8K 级输入:跳过 ncnn(实测静默全黑),直接走稳定引擎
                        // 【指定帧率】分数步长一路传进分段层:每段 `-n` 按"目标帧数 ÷ 源帧数"精确分割。
                        fractionalMult: fractionalMult,
                        targetFramesOverride: targetFramesOverride,
                        totalSrcFrames: frameCount);
                    // ===== 批处理清盘(边用边删):本段输入帧已证明消费完,立刻释放,不等整阶段结束 =====
                    // 依据:InterpSegmentAsync 段首就把 frame_{s+1}..frame_{e}(恰好 e-s 帧,不含下一段的首帧)
                    // 复制进了它自己的 segIn 目录;其后 ncnn 原卡/换卡、ONNX DirectML、黑帧回退、残缺重算
                    // 全都只读 segIn,不会再碰 segSrcDir —— 故这 e-s 帧可证明已消费,立即删。
                    // 下一段读的是 frame_{e+1}..,与本节无交集(段边界锚点各归下一段),不会删到还要用的帧。
                    int delThisSeg = 0;
                    for (int i = s; i < e; i++)
                    {
                        try
                        {
                            string srcFrame = Path.Combine(segSrcDir, $"frame_{i + 1:D6}.jpg");
                            if (File.Exists(srcFrame)) { File.Delete(srcFrame); delThisSeg++; }
                        }
                        catch { /* 删不掉不影响正确性:阶段收尾还会再扫一次 */ }
                    }
                    releasedConsumed += delThisSeg;
                    uReleasedFrames += delThisSeg;   // 【任务 U】清理释放台账(补帧阶段)
                    AppLogger.Info($"[临时清理] 补帧段 {segNo}/{segBounds.Count}(帧 {s + 1}~{e})完成:已释放" +
                        (upscaleFirst ? "超分输出帧" : "源帧") + $" {delThisSeg} 帧(本阶段累计 {releasedConsumed} 帧;目录 {Path.GetFileName(segSrcDir)})");
                    if (progress != null)
                    {
                        int doneNow = (int)Math.Min(globalTarget, Math.Max(0, globalIdx - 1));   // 钳制:当前帧永不超总帧(修复"第11219帧/共11099帧"溢出)
                        int segPct = interpPctBase + (int)((double)interpPctSpan * doneNow / Math.Max(1, globalTarget));
                        progress.Report((segPct,
                            $"补帧 第 {doneNow} 帧 / 共 {globalTarget} 帧(段 {segNo}/{segBounds.Count})" +
                            EtaStr(doneNow - interpBase, globalTarget - interpBase,
                                (DateTime.UtcNow - interpStageStart).TotalSeconds - interpIdleSec)));
                        // 【界面可见性 · 2026-09-13】逐段清盘过去只写 AppLogger,界面上完全看不到"边跑边释放临时帧"。
                        // 这里就地更新一条轻提示:沿用【刚刚上报的同一个百分比】(只换文字,不把进度往回带):
                        //   · 文案不带帧号 → 不与「补帧 第 N 帧 / 共 M 帧」抢步骤行(UI 的 etaRegex 匹配不上);
                        //   · 不含"完成"二字 → UI 不会把它改写成"✓ …"并清掉当前步骤行;
                        //   · 不带 [临时清理] 前缀(那是日志口径,不搬上界面);
                        //   · 本段没释放到帧(delThisSeg == 0)时不发,避免噪声。
                        // 纯提示:删除时机/并发/处理顺序一律未动。
                        if (delThisSeg > 0)
                            progress.Report((segPct, $"本段已释放 {delThisSeg} 帧临时文件(累计 {releasedConsumed} 帧)"));
                        // 【R1 · 2026-09-13 用户反馈:"我没有看到什么清理的字样啊 左下角日志处要显示 还要无感"】
                        // 上面那条是【状态行】提示,真机确认它会被 UI 的 100ms 节流吞掉(所以用户压根没见过)。
                        // 这里补一条【走左下角日志区】的合并记录:每累计 800 帧才一行(`· ` 前缀 → UI 端在节流之前
                        // 就把它追加进日志区、不动步骤行、不改进度条、不用警告色),长片全程也就几行:看得见、不刷屏。
                        if (releasedConsumed - interpCleanupLogged >= 800)
                        {
                            interpCleanupLogged = releasedConsumed;
                            progress.Report((segPct, $"· 临时文件清理:补帧阶段已释放 {releasedConsumed} 帧输入帧(用完即删,省临时盘)"));
                        }
                    }
                }
                var interpCount = EnumerateFrameFiles(segOutDir).Count();   // 补帧输出可能是 png(旧)或 jpg(新边转边存),统一按两种数
                if (interpCount == 0)
                {
                    // 【补帧 0 帧诊断】打印关键中间值,定位"补帧失败,未生成插帧"根因:
                    // frameScale=origCountEst/frameCount(若 origCountEst 探测失败=0,frameScale=0 → mult=1 → 等于没补帧);
                    // globalTarget / segBounds / 段数 等,以便下次拿到日志精确定位。
                    try
                    {
                        AppLogger.Error($"补帧 0 帧诊断: frameScale={frameScale:0.###}, origCountEst={origCountEst}, frameCount={frameCount}, interpScale={interpScale}, segs={segBounds.Count}, model={interpModel}, fpsMode={fpsMode}, 顺序={(upscaleFirst ? "超分→补帧" : "补帧→超分")}, 输出目录={segOutDir}");
                    }
                    catch { }
                    throw new InvalidOperationException(
                        "补帧失败:未生成任何画面。显卡加速、备用方案和换卡都试过了仍无输出,通常是显卡不兼容或需要更新显卡驱动。建议在「计算设备」里换一个 GPU,或更新显卡驱动后重试。");
                }
                // 帧数对齐已移至"muxDur/outFps 已知处"(时长=源容器 × 帧率),此处不再处理(需帧率公式才能定目标)。
                // 注:补帧诊断(输出帧数/frameScale)也移到合帧前与实际输出帧数一并打印。
                progress?.Report((interpPctBase + interpPctSpan, $"补帧完成({interpCount} 帧,含原始帧)" + StageElapsed()));
            }

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
                    progress?.Report((interpPctBase, $"补回还原源时间轴({tempoSrcIdx[^1] + 1} 帧)..."));
                    try
                    {
                        double srcDur = (trimEnd ?? await ProbeDurationSeconds(inputVideo)) - (trimStart ?? 0);
                        var resR = await RunTempoResampleAsync(rife, framesIn, framesFinal, frameCount,
                            inFps, tempoSrcIdx, 1, inFps, gpuId, srcDur, interpModel, tta, progress, ct);
                        foreach (var f in Directory.EnumerateFiles(framesIn, "*.jpg")) File.Delete(f);
                        foreach (var f in Directory.EnumerateFiles(framesFinal, "*.jpg"))
                            File.Copy(f, Path.Combine(framesIn, Path.GetFileName(f)), true);
                        foreach (var f in Directory.EnumerateFiles(framesFinal, "*.jpg")) File.Delete(f);
                        frameCount = resR.frameCount;
                        effectiveFps = inFps;
                        frameDurs = null;   // 补回 = 源轴 CFR 序列,旧时长表(按删帧合并)已失效,必须清(否则帧数已变,下游按旧表越界)
                        tempoSrcIdx = null;
                        progress?.Report((interpPctBase, $"补回完成({frameCount} 帧,继续 ×{interpScale} 补帧...)"));
                    }
                    catch (Exception ex)
                    {
                        if (ct.IsCancellationRequested) throw;   // 取消必须立刻传播,绝不吞(否则会回退再跑一遍标准补帧)
                        // 【F1】黑帧换路全失败 → 必须让任务失败:这个 catch 原有的"展开兜底"(tempoSrcIdx=null,
                        // 按去重后的帧接着跑)会让帧数/时间轴与设计错乱,正是用户明令不允许的处置方式。
                        // 宁可明确报错并告诉用户原因,也不交付一条含黑场/时间轴错乱的成片。
                        if (ex is BlackFrameRerouteException) throw;
                        AppLogger.Info($"补回来失败(按展开兜底):{ex.Message}");
                        tempoSrcIdx = null;
                    }
                }
                {
                    // 3a) 转场检测(基于拆帧后的图片序列,pts_time=帧号)
                    // 【判据只有一套、只算一次(2026-09-15 统一)】本块是**全流程唯一的**切点判据调用点:
                    //   采样 = ComputeSceneCutMetricsAsync(整片只解码一遍,按帧流式读、常驻内存 2 帧)
                    //   判定 = AlhPro.Core.SceneCutJudge 的双判据(帧差 + 拉普拉斯;本文件里那次调用就是唯一的判据调用点)
                    // 结论存进共享表 sceneCutPairs(源帧下标 i 口径),普通分段路径与"按真实时间戳排帧"那条路都读它。
                    // 【历史上错在哪】两条路各自调了一次采样与判定 ⇒ 同一份判据、两次全片解码、两套口径;更糟的是
                    // "按真实时间戳排帧"那条路**无视「转场识别」开关**(用户明明关了却还有切点保护)。现在开关只管这一处。
                var cuts = new List<int>();
                if (sceneCut is { } sc)
                {
                    progress?.Report((8, $"转场识别(阈值 {sc.Threshold:0.00})..."));
                    var th = sc.Threshold.ToString("0.###", inv);
                    // 开关状态必须看得见(用户明确要求):除了"开/关",这一行还要印**这次真正生效的判据数字** ——
                    // 用户拉了滑块到底有没有起作用,事后全靠这行对账(见 AlhPro.Core.SceneThresholdMap,唯一换算处)。
                    AppLogger.Info($"转场识别=开({sc.Threshold:0.00})"
                        + $"(判据=帧差+拉普拉斯,采样 {AlhPro.Core.SceneCutMetrics.SampleHeight} 行;"
                        + $"生效判据 {sc.Thresholds.Text};滑块原值 {th} 只在采样不可用时回退 ffmpeg scene)");
                    string gateName = "";
                    string judgeWhy = "";
                    (double[] diff, double[]? lapVar) scMetrics = (diff: Array.Empty<double>(), lapVar: null);
                    try
                    {
                        scMetrics = await ComputeSceneCutMetricsAsync(ffmpeg, framesIn, srcW, srcH, frameCount, ct)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        judgeWhy = ex.Message.Split('\n')[0];
                    }
                    if (scMetrics.diff.Length != 0)
                    {
                        // 【口径换算】Detect 给的是 i 口径(切点在源帧 i 与 i+1 之间),分段侧用"下一个源帧号" c = i + 1;
                        // 共享表 sceneCutPairs 存回 i。两处口径的换算是本块唯一的约定,别在别处再算一遍。
                        foreach (var i in AlhPro.Core.SceneCutJudge.Detect(scMetrics.diff, scMetrics.lapVar,
                                     thresholds: sc.Thresholds))
                        {
                            int c = i + 1;
                            if (c > 0 && c < frameCount) cuts.Add(c);
                        }
                        gateName = "帧差+拉普拉斯";
                    }
                    else if (ct.IsCancellationRequested)
                    {
                        gateName = "取消";
                        if (judgeWhy.Length > 0)
                            AppLogger.Info($"转场识别:采样被取消或失败({judgeWhy}),本次按无转场处理");
                    }
                    else
                    {
                        // 采样判据不可用(探测/解码失败)→ 回退旧 ffmpeg scene 判据(与改动前逐字一致)
                        gateName = $"ffmpeg scene {th}";
                        if (judgeWhy.Length > 0)
                            AppLogger.Info($"转场识别:采样判据不可用({judgeWhy})→ 回退旧 ffmpeg scene 判据(与改动前逐字一致)");
                        try
                        {
                            var lines = await RunCaptureAsync(ffmpeg,
                                $"-y -framerate 1 -i \"{Path.Combine(framesIn, "frame_%06d.jpg")}\" " +
                                $"-vf \"select='gt(scene,{th})',metadata=print\" -f rawvideo NUL",
                                ct);
                            foreach (var line in lines)
                            {
                                var m = System.Text.RegularExpressions.Regex.Match(line, @"pts_time:(\d+(?:\.\d+)?)");
                                if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, inv, out var pts))
                                    cuts.Add((int)Math.Round(pts));
                            }
                        }
                        catch { /* 回退判据也失败不阻塞,按无转场处理 */ }
                    }
                    cuts.RemoveAll(c => c <= 0 || c >= frameCount); // 忽略首尾无效点
                    cuts.Sort();
                    // 【切点最小间距】相邻连判合并(闪光/闪频/逐帧交替类素材),依据 PySceneDetect min_scene_len 默认 15 帧。
                    var mergedCuts = AlhPro.Core.SceneCutJudge.ApplyMinSceneLen(cuts, 15);
                    if (mergedCuts.Count != cuts.Count)
                    {
                        AppLogger.Info($"转场识别:按切点最小间距 15 帧合并相邻误判 {cuts.Count} → {mergedCuts.Count} 处"
                            + "(闪光/闪频/逐帧交替类素材的相邻连判;依据 PySceneDetect min_scene_len 默认 15 帧)");
                        cuts.Clear();
                        cuts.AddRange(mergedCuts);
                    }
                    // 【最短补帧段 · 2026-09-21 曾加、2026-09-22 用户裁决整块撤掉】
                    //   来历:用户报"转场补帧分批引擎启动太慢"(128 帧被切成 5 段、每段约 21 帧 ⇒ 约 2/3 时间
                    //   花在反复冷启动)。当时的对策是"把会让某段短于 60 帧的切点合并掉",并把它做成了界面开关。
                    //   撤掉的理由(用户看过代价后判定):它是**拿鬼影防线换速度** —— 被合并掉的切点处照常插帧,
                    //   那恰好是真转场时就插出一帧跨切混合;而「转场阈值」滑块能用**不牺牲保护**的方式达到
                    //   同一个目的(少判切点 ⇒ 引擎同样少启动,保留下来的切点照样强制拷贝)。
                    //   ⚠ 别再把同一套东西加回来:问题(启动慢)是真的,错的是用来交换的东西。
                    //   留档在 AlhPro.Core.SceneCutJudge 类里(原 MinSegmentLen / ApplyMinSegmentLen 的位置)。
                    if (gateName == "帧差+拉普拉斯" && cuts.Count > 0)
                    {
                        // 判据清单(只印前 8 处,长片不刷屏):本次采样的原始数就在这行,给"阈值要不要动"留证据。
                        var sb = new System.Text.StringBuilder();
                        var lapArr = scMetrics.lapVar ?? Array.Empty<double>();
                        foreach (var c in cuts.Take(8))
                        {
                            int i = c - 1;
                            double d = i < scMetrics.diff.Length ? scMetrics.diff[i] : 0;
                            double lpPrev = i - 1 >= 0 && i - 1 < lapArr.Length ? lapArr[i - 1] : 0;
                            double lpCur = i < lapArr.Length ? lapArr[i] : 0;
                            double ratio = lpPrev > 0 ? lpCur / lpPrev : 0;
                            sb.Append($"[源帧 {i}→{c} 帧差 {d:0.##} 拉普拉斯比 {ratio:0.##}] ");
                        }
                        if (cuts.Count > 8) sb.Append($"…(共 {cuts.Count} 处)");
                        AppLogger.Info($"转场识别:切点判据=帧差+拉普拉斯(采样 {AlhPro.Core.SceneCutMetrics.SampleHeight} 行,"
                            + $"生效判据 {sc.Thresholds.Text};来源=界面「转场阈值」滑块 {sc.Threshold:0.00}"
                            + $"({AlhPro.Core.SceneThresholdMap.Describe(sc.Threshold)}))"
                            + "清单 " + sb.ToString().Trim());
                    }
                    // 【共享表落格】两条路读的就是这两行写的值:先清空再灌(不许残留上一次任务/上一次检测的结论)。
                    sceneCutPairs.Clear();
                    foreach (var c in cuts) sceneCutPairs.Add(c - 1);   // c=i+1 → i 口径换算
                    if (cuts.Count > 0)
                    {
                        var sbc = new System.Text.StringBuilder();
                        foreach (var c in cuts.Take(12)) sbc.Append($"{c - 1}→{c} ");
                        if (cuts.Count > 12) sbc.Append($"…(共 {cuts.Count} 处)");
                        int skipped = cuts.Count * Math.Max(0, interpScale - 1);
                        AppLogger.Info($"转场识别:切点 {cuts.Count} 处(源帧号 {sbc.ToString().Trim()};判据={gateName});"
                            + $"因切点跳过跨切插值 {skipped} 次(倍率 {interpScale}x,每处 {Math.Max(0, interpScale - 1)} 帧;"
                            + "跳过处由同场景帧补足,成片帧数不变)");
                    }
                    else
                    {
                        AppLogger.Info($"转场识别:未发现切点(判据={gateName})");
                    }
                    progress?.Report((9, $"转场识别:发现 {cuts.Count} 处转场" + StageElapsed()));
                }
                else
                {
                    // 开关为关 ⇒ 两条路读的是**同一份空表**:这条路不做保护,那条路也不会偷偷做(那正是历史缺陷)。
                    sceneCutPairs.Clear();
                    AppLogger.Info("转场识别=关(用户未勾选「转场识别」)→ 本次不做切点检测;两条路径读同一份(空)切点表");
                    progress?.Report((9, "转场识别:未勾选,按无转场处理" + StageElapsed()));
                }

                // 3b) 分段补帧(输入 framesIn,输出 framesFinal)
                progress?.Report((10, $"RIFE 补帧({interpScale}x,源 {frameCount} 帧 → 输出 {(long)Math.Round((double)((frameCount - 1) * interpScale)) + 1} 帧,模型 {interpModel})..."));
                if (interpScale >= 4)
                    AppLogger.Warn($"⚠ 高倍率补帧({interpScale}x):输出帧数是源 {interpScale} 倍,处理耗时会明显变长,属正常,请耐心等待(非卡死)");
                // ===== RIFE GPU 探测(任何可能静默 hang 的设备都不放过,不预检白等 8 分钟)=====
                // 实测 2 帧插 1 帧能否 GPU 出图;不能 → 本视频补帧改用 ONNX(慢但确定能跑),日志+进度提示。
                int interpGpu = gpuId;
                if (gpuId >= 0)
                {
                    // 【50 系不再"一律禁用 ncnn"】旧逻辑:Blackwell 一律直接改走 ONNX、不做任何探测。
                    // 现在改为"先真机探测、再按结果决定":探测通过 → 用 ncnn-Vulkan 补帧(最快那条路);
                    // 失败(hang/崩/坏帧)→ 才改走 ONNX,并明确告知用户"为稳定性改用 ONNX"。
                    // 结论跨任务缓存(EnsureRifeNcnnProbeAsync),同一设备不会每次任务都白等一遍探测。
                    // 【按源分辨率探】探测帧尺寸=本视频源尺寸(旧实现固定 320×240):小图能跑 ≠ 真帧能跑,
                    // 显存/着色器分块压力差一个量级,实测过"小图通过、真分辨率上静默出坏帧"的形态。
                    bool rifeWillRun2 = true;
                    try { rifeWillRun2 = EngineService.RifeProbeWillRun(rife, interpModel, gpuId, srcW, srcH); } catch { }
                    if (rifeWillRun2)
                        progress?.Report((interpPctBase, $"正在检测补帧 GPU 兼容性(按源分辨率 {srcW}×{srcH} 实测,首次约 10 秒,结论会记住,失败重试一次)..."));
                    bool rifeOk = await EngineService.EnsureRifeNcnnProbeAsync(rife, interpModel, gpuId, ct, srcW, srcH).ConfigureAwait(false);
                    if (!rifeOk)
                    {
                        // 【按形态说话】失败原因由探测带回(初始化即崩 ≠ 出图但坏帧),措辞集中在 AlhPro.Core.ProbeDiagnosis:
                        // 前者在 Blackwell 上就是 NVIDIA 的 cooperative-matrix 驱动缺陷(要说清"不是本软件的问题"),
                        // 后者属引擎并发/渲染(不许甩锅给驱动)。
                        AppLogger.Warn($"⚠ RIFE {interpModel} 在本机 GPU({gpuId})真机探测失败——为稳定性改用 ONNX 补帧路线。"
                            + EngineService.LastProbeUserMessage);
                        progress?.Report((interpPctBase, $"⚠ 补帧 ncnn 引擎在本机不可用,为稳定性自动改用 ONNX 补帧..."));
                        interpGpu = -1;   // 本视频后续补帧 API 全部走 ONNX(InterpSegmentAsync 传入)
                    }
                    else
                    {
                        AppLogger.Info($"✅ RIFE {interpModel} GPU({gpuId})真机探测通过 → 使用 ncnn-Vulkan 补帧(未因 50 系而禁用)");
                    }
                }
                var segStart = 0;
                foreach (var c in cuts)
                {
                    if (c > segStart) segBounds.Add((segStart, c));
                    segStart = c;
                }
                if (segStart < frameCount) segBounds.Add((segStart, frameCount));
                // 【任务 S】"按时轴填平"(= 输出统一帧率)的判定。
                // 判定 = 源时长表有缺口(间隔偏离中位数 >25%)且开了补帧;CFR 源一律不填平(行为逐字不变)。
                // 【S1 接线状态(本次已接入合成)】这里只做**判定**并把计划存进 flatPlan;真正的合成在
                // InterpStageAsync 里(每个目标时刻按真实 PTS 取源帧对 + φ:落在源帧上直接拷贝,否则交既有
                // 层批原语合成),是否真的走那条路还要看补帧引擎探针结果 —— 见 InterpStageAsync 的接线注释。
                // 【口径修正(S2 用户复测)】填平的收益是**消除 VFR 的不均匀节奏(33.3ms 顿挫)**,
                // 不是"往缺口里填运动"(缺口内部几乎无变化,实测 0.871/0.150,填与不填视觉等价)。
                {
                    var plan = AlhPro.Core.TimelineFlattenPlan.Decide(frameDurs, effectiveFps, interpScale);
                    // 【S2 · 2026-09-13 用户复测修正口径】真正要修的是**切点混合帧**(实测鬼影比 0.647/0.692 +
                    // 一帧严重软化 B[57] lapvar 仅源帧 7.8%),而不是"缺口里填运动"(缺口内部几乎无变化:
                    // 实测 0.871/0.150,填与不填视觉等价)。判据 = 帧差 ≥25 且(拉普拉斯能量比 ≤0.6 或帧差 ≥50)【待实测标定】。
                    // 现有 interp 路径本就按转场分段跑(segBounds 来自 cuts)→ RIFE 侧不会跨切混合;这条保护是给
                    // "按时轴逐槽 φ 插值"的排程用的(Core.CutAwareSchedule:切点上强制拷贝、不许合成)。
                    // 【2026-09-22 修 · 自测逮到的自相矛盾】原来这里的注解是按 `cuts.Count == 0` 判的,于是
                    // **「勾了转场识别、但这一片真的一处切点都没有」**也会打出"(注:「转场识别」未勾选 ⇒ 本次不做
                    // 切点检测…)" ✗ —— 同一份日志里上一行刚写「转场识别=开(0.30)」,下一行就说"未勾选",自相矛盾
                    // (2026-09-22 自测真机复现:3 秒预览、判据无切点)。两件事必须分开说:
                    //   · sceneCut == null ⇒ **确实没检测**(用户没勾);
                    //   · 勾了但 cuts.Count == 0 ⇒ **检测了,这片没有切点**(常见于短片段/单一场景)。
                    AppLogger.Info($"时间轴:检测到 {cuts.Count} 处场景切换,已按切点对齐(不生成跨切混合帧;判定阈值【待实测标定】)"
                        + (sceneCut == null
                            ? "(注:「转场识别」未勾选 ⇒ 本次不做切点检测;两条路径(普通分段 / 按真实时间戳排帧)读的是**同一份**切点表,"
                              + "不会出现某条路还在保护的情况)"
                            : cuts.Count == 0
                                ? "(注:本次**检测了**、这一片没有切点 —— 短片段或单一场景本就可能是 0 处;"
                                  + "要让判定更敏感可把「转场阈值」调小)"
                                : ""));
                    // 【⑤ VFR 门(用户最终裁决)】"按真实时间戳排帧"这条**内部**路径只对 VFR 源自动生效:
                    // CFR 源一律走常规处理(不做时序重采样);判不出 VFR 时按 CFR 保守处理。
                    // 判据**复用**源级 VFR 结论 vfrPassthrough(ProbeVfrAsync 的时间戳判据),不自造第二套。
                    bool sourceIsVfr = vfrPassthrough;
                    AppLogger.Info(sourceIsVfr
                        ? "时间轴:源为 VFR → 按真实时间戳排帧(避免重复帧/时长漂移)"
                        : "时间轴:源为 CFR → 常规处理(不做时序重采样)");
                    // 两道门都要过:① 源确实是 VFR;② 时长表里真的检出缺口(plan.Flatten)。
                    // smoothTimeline 现在恒为 true(界面上那个勾选框已删,只表示"允许走这条内部路径")。
                    if (plan.Flatten && sourceIsVfr && smoothTimeline)
                        flatPlan = plan;   // 只有"确实要填平"才留下计划;否则下游一律走老路径(逐字不变)
                    AppLogger.Info(plan.LogLine + (!plan.Flatten
                        ? ""
                        : (!smoothTimeline
                            ? " → 【平滑时间轴】已在选项里关闭,本次按原样输出(与改动前一致)"
                            : (sourceIsVfr
                                ? " → 本次按真实时间戳排帧:每个目标时刻按真实 PTS 取源帧对 + φ 合成(落在源帧上直接拷贝);"
                                  + (sceneCut is not null
                                      ? "有场景硬切时切点两侧强制同场景拷贝,不生成跨帧混合(鬼影)帧"
                                      : "转场识别=关 → 切点处不做保护、照常按比例插值(与改动前一致)")
                                : " → 判定可填平,但**源不是 VFR** ⇒ 按常规路径处理(不做时序重采样,与改动前一致)"))));
                }
                // 【任务 Q1 · 2026-09-13】阶段顺序不再靠全局开关(常量 false),改为**按实测单价自动判定**:
                // 超分单帧成本 u 与"补帧在源分辨率/放大后分辨率的单帧成本"比较,谁便宜谁先跑。
                // 判据/成本表/安全边际(节省 <15% 不切换)/未实测组合回退,全在 AlhPro.Core.PipelineOrderPlan
                // (纯函数 + 单测,成本表每个数字都标了 2026-09-13 真机实测出处)。
                // 【2026-09-14】这三组数字(超分单价表 / 补帧锚点表 / 安全边际)原可被"在线参数"覆盖,
                // 那个功能已被用户判定为累赘并整体删除 → 现在**只读 Core 里的内置实测常量**(逐字等价)。
                // 只有"超分与补帧都要真跑"时顺序才有意义;其余情况保持 upscaleFirst 的原值(全局开关口径)。
                if (doUpscale && frameInterp && upscaleRuns)
                {
                    double upScaleNow = upscaleShrink1x ? 2.0 : scale;   // 引擎实际跑的倍率(1x 缩回 = 按 2x 跑再缩回)
                    double areaScaleNow = upscaleShrink1x ? 1.0 : scale; // 补帧真正吃到的帧相对源帧的放大倍数(缩回后 = 1)
                    var orderPlan = AlhPro.Core.PipelineOrderPlan.Decide(engine, model, upScaleNow, interpScale,
                        srcW, srcH, frameCount, areaScale: areaScaleNow);
                    upscaleFirst = orderPlan.UpscaleFirst;
                    // 【进度区间必须跟着"真正执行的顺序"走】否则进度条会先按旧顺序跳到 45% 再倒退
                    // (H 任务注释里点名的老问题)。这里就在判定点重算,闭包/后续阶段读到的都是新值;
                    // upBase/upEnd 在超分块里读的是"当时的 upscaleFirst"→ 也自动跟着变。
                    interpPctBase = upscaleFirst ? 45 : 10;
                    interpPctSpan = upscaleFirst ? 45 : 35;
                    AppLogger.Info(orderPlan.LogLine);
                    uOrderLog = orderPlan.LogLine;
                    uOrderSavingsSeconds = orderPlan.SavingsSeconds;
                    uOrderSavingsPercent = orderPlan.SavingsPercent;
                    uOrderMeasured = orderPlan.Measured;
                    if (!orderPlan.UpscaleFirst && orderPlan.Measured && orderPlan.SavingsSeconds > 0)
                        AppLogger.Info($"顺序判定说明:新顺序虽然更省但只省 {orderPlan.SavingsPercent:0.#}%(< {AlhPro.Core.PipelineOrderPlan.MinSavingsPercent:0.#}% 安全边际)→ 保持旧顺序,避免临界抖动");
                    // 【任务 X1】界面日志区只放【结论短句】:完整判据(u / r_lo / r_hi / 门槛秒数 / 成本表出处)
                    // 已经由上面两行 AppLogger 写进诊断文件 —— 用户真机就是被那一长串挡住、没找到结论的。
                    // 文案规则(不含"完成"/不含"第 N 帧 / 共 M 帧"/≤60 汉字)由 Core.LogShortText 负责并被单测钉住。
                    progress?.Report((6, "· " + AlhPro.Core.LogShortText.ClampToChineseLimit(
                        $"顺序:{AlhPro.Core.LogShortText.OrderShortText(orderPlan, AlhPro.Core.PipelineOrderPlan.MinSavingsPercent)}")));
                    // 【任务 Q2】两阶段批计划:两个阶段的输入分辨率不同,各自按自己的面积算每批帧数,分别落日志。
                    // (补帧阶段的"每批帧数"是等效参考值 —— 它实际按转场分段跑,见 RenderPolicy.PlanStageBatches)
                    var stagePlans = AlhPro.Core.RenderPolicy.PlanStageBatches(SafeRender.FreeRamGB, frameCount,
                        areaScaleNow, interpScale, srcW, srcH, upscaleFirst, fastMode, diskTight, perfScore);
                    // 【R3 · 用户要求"日志也要显示本次处理分别一批多少个帧"】处理【开始时】一行说清两阶段每批多少帧
                    // (沿用既有「超分批决策:」那行的风格,不新造格式)。旧顺序下补帧批天然大于超分批(见 PlanStageBatches 注释)。
                    // 【任务 T 要求 5】同一行里把"性能档位(依据)+ 每批帧数 + 批数"合并进来。
                    {
                        var ipPlan = stagePlans.FirstOrDefault(s => s.Stage == "补帧");
                        var upPlan = stagePlans.FirstOrDefault(s => s.Stage == "超分");
                        AppLogger.Info($"本次处理:补帧阶段每批 {ipPlan.FramesPerBatch} 帧 × {ipPlan.BatchCount} 批(输入 {ipPlan.InputWidth}×{ipPlan.InputHeight})、"
                            + $"超分阶段每批 {upPlan.FramesPerBatch} 帧 × {upPlan.BatchCount} 批(输入 {upPlan.InputWidth}×{upPlan.InputHeight}"
                            + (upscaleFirst ? $"→输出 {ipPlan.InputWidth}×{ipPlan.InputHeight})" : ")")
                            + $" —— 顺序={(upscaleFirst ? "超分→补帧" : "补帧→超分")};性能档位={perfScore}"
                            + $"({(perfMeasured is { } mp2 ? $"实测 {mp2:0.###} 秒/帧@1080p" : "无实测→回退内存档")}"
                            + $";空闲内存 {SafeRender.FreeRamGB:0.#}G;核数 {SafeRender.CpuCoreCount}"
                            + $";显存 {(SafeRender.FreeVramMeasured ? $"{SafeRender.TotalVramGB:0.#}G(已实测)" : "未实测")})"
                            + $";每批帧数按各阶段【输入+输出并存的像素量】缩放(1080p 为基准)");
                        AppLogger.Info($"性能档判定依据:{perfReason}");
                    }
                    foreach (var sp in stagePlans)
                        AppLogger.Info($"批计划[{sp.Stage}]({sp.Order}):输入 {sp.InputWidth}×{sp.InputHeight}"
                            + $"(面积系数 {sp.AreaFactor:0.###})→ 每批 {sp.FramesPerBatch} 帧 × 预计 {sp.BatchCount} 批"
                            + $"(阶段输入 {sp.StageInputFrames} 帧{(sp.Advisory ? ",等效参考值" : "")})");
                    // ===== 【任务 X1/X2】界面日志区:"每批多少帧/几批"提前到【处理开始】,并且只用一句短话 =====
                    // 【为什么要挪(T 之前的口径)】用户真机找不着批次信息:它原来要等超分阶段开始才写(而且是长句)。
                    // 【诚实口径(任务 X2 明确要求)】补帧阶段**实际是按转场分段跑的**(一段一次 RIFE 调用,
                    //   不按批大小切,见 RenderPolicy.PlanStageBatches 的 Advisory 说明)→ 这里如实写"按转场分段",
                    //   只把"等效每批 N 帧"当参考值报出来,不让人误解成它真的在分批。
                    {
                        var ipUi = stagePlans.FirstOrDefault(s => s.Stage == "补帧");
                        var upUi = stagePlans.FirstOrDefault(s => s.Stage == "超分");
                        string ipTxt = ipUi.Advisory
                            ? $"补帧按转场分段(等效每批 {ipUi.FramesPerBatch} 帧)"
                            : $"补帧 {ipUi.FramesPerBatch} 帧/批 ×{ipUi.BatchCount} 批";
                        progress?.Report((6, "· " + AlhPro.Core.LogShortText.ClampToChineseLimit(
                            $"批大小:{ipTxt}、超分 {upUi.FramesPerBatch} 帧/批 ×{upUi.BatchCount} 批"
                            + $"(设备档:{AlhPro.Core.LogShortText.DeviceTierShortText(SafeRender.FreeRamGB, perfScore)}"
                            + ";批数为预计)")));
                        xBatchLineShown = true;
                    }
                }
                frameScale = frameCount > 0 ? Math.Min(6.0, (double)origCountEst / frameCount) : 1.0;
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
                    globalTarget = Math.Max(frameCount + 1,
                        (long)Math.Round((double)((fpsMode == 1 ? frameCount : origCountEst) - 1) * interpScale) + 1);
                    // ===== 【任务 U】处理开始时的"帧数台账"一行摘要 =====
                    // 【口径】源帧数 = 探测到的原始总帧数 origCountEst(去重前);去重后 = frameCount;
                    // 补帧后 = globalTarget(本任务补帧的**输出目标帧数**);超分**不增减帧数**(超分保帧数,
                    // 帧号一一对应)→ "超分后帧数"与"补帧后帧数"同口径,写清楚免得被读成两笔账;
                    // 目标输出帧率:用户指定优先,否则按输出基准公式(A 内容×倍率 / B 原×倍率)【预计】;
                    // 预计输出总帧数 = globalTarget(下游"帧数对齐/尾帧容积"还会做 ±1 帧级微调,已标注【预计】)。
                    {
                        double planOutFps = targetFps is > 0
                            ? targetFps.Value
                            : (fpsMode == 1 ? effectiveFps : Math.Max(effectiveFps, inFps)) * interpScale;
                        if (flattenActive || flatPlan.Flatten) planOutFps = flatPlan.TargetFps;
                        long planOutFrames = (flattenActive || flatPlan.Flatten) ? flatPlan.TargetFrames : globalTarget;
                        string census = $"本次处理帧数台账:源 {origCountEst} 帧 → 去重后 {frameCount} 帧 → 补帧后 {globalTarget} 帧"
                            + $";超分不增减帧数(超分后同为 {globalTarget} 帧);目标输出帧率【预计】{planOutFps:0.##} fps;"
                            + $"预计输出总帧数【预计】{planOutFrames} 帧"
                            + (flattenActive || flatPlan.Flatten ? $"【平滑时间轴:按真实时长 {flatPlan.TotalSeconds:0.###}s 重铺】" : "")
                            + $";倍率 {interpScale}x(有效倍率 mult={AlhPro.Core.VideoPipeline.InterpMultiplier(interpScale, frameScale):0.##})";
                        AppLogger.Info(census);
                        // 【任务 X1/X3】界面只放一句压缩后的结论(长台账留在文件日志里):
                        // 一行同时给出「源分辨率 + 三段帧数 + 输出帧数/帧率」—— 用户明确要"一眼看懂 + 便于对比快慢"。
                        progress?.Report((6, "· " + AlhPro.Core.LogShortText.ClampToChineseLimit(
                            $"源 {srcW}×{srcH} · 帧数:源 {origCountEst} → 去重后 {frameCount} → 补帧后 {globalTarget}"
                            + $"(输出 {planOutFrames} 帧 @{planOutFps:0.#}fps{(flattenActive || flatPlan.Flatten ? "·平滑时间轴" : "")})")));
                    }
                    // ===== 旧顺序(3x/4x/不实际超分):补帧在这里跑 =====
                    // 输入 framesIn(源帧)、输出 framesFinal;RIFE 探测尺寸 = 源尺寸(srcW×srcH)——与改动前逐字一致。
                    // 新顺序(1x/2x)不在这里跑:同一份 InterpStageAsync 被推迟到超分阶段之后调用
                    // (输入 upOutput(超分输出)、输出 framesInterp、探测尺寸=超分后的真实尺寸)。
                    if (!upscaleFirst)
                    {
                        // 【任务 X3】只包住补帧阶段本身(Start/Stop 累计;新顺序那次调用在 4.2) 块,两处互斥)
                        xInterpWatch.Start();
                        await InterpStageAsync(framesIn, framesFinal, srcW, srcH, stageFractionalMult, stageTargetFrames);
                        xInterpWatch.Stop();
                    }
                }
                }
            }
            else
            {
                // 不补帧:拆帧结果直接作为处理帧
                foreach (var f in frameFiles)
                    File.Copy(f, Path.Combine(framesFinal, Path.GetFileName(f)), true);
                finalDurs = frameDurs != null
                    ? new System.Collections.Generic.List<double>(frameDurs) : null;
                progress?.Report((interpPctBase + interpPctSpan, $"帧准备完成({frameFiles.Length} 帧)"));
            }
            if (!upscaleFirst)
            {
                // ===== 临时文件控制:补帧/帧准备完成后,源帧(framesIn)不再需要,立即删除释放磁盘 =====
                // (长视频 + 高倍率补帧时 framesIn 可占几十 GB;及时清,避免累积到 100+GB)
                try
                {
                    int delCnt = 0;
                    foreach (var f in Directory.EnumerateFiles(framesIn, "*.jpg")) { File.Delete(f); delCnt++; }
                    AppLogger.Info($"[临时清理] 已释放源帧目录 framesIn({delCnt} 帧),后续超分/合帧不再需要");
                    uReleasedFrames += delCnt;   // 【任务 U】清理释放台账(补帧阶段的源帧目录)
                }
                catch { /* 清理失败忽略,不中断 */ }

                // ===== 统一 JPG:帧准备/补帧完成后,把 framesFinal 从引擎 PNG 重编码成 JPG(降临时盘)=====
                // 下游超分(读 framesFinal)/缩放/对齐/合帧统一读 .jpg;引擎 I/O 仍按各自 .png 契约(读取处不改)。
                // 目录已是 JPG(未补帧,直接复制 framesIn)则空跑。
                // 【G · 2026-09-13】这一整批重编码【耗时正比于帧数】(用户那条 2668 帧要跑几十秒~几分钟),
                // 过去它无进度、不可取消(调的是 1 参重载 → progress/ct 都是默认值),界面只停在上一阶段的
                // "预计还剩几秒",用户看到的却是卡死。现在:传 progress + ct(可取消),并上报中性文案
                // "整理帧(JPG) 第 N / M 帧"(沿用本阶段的当前百分比,只换文字不回退;AA 关着也照发)。
                // 耗时计入 LastFrameReencodeSeconds(它本来就含在"处理阶段"墙钟里,现在也有日志可核对了)。
                {
                    int prepPct = interpPctBase + interpPctSpan;   // 本阶段的当前口径(= 补帧/帧准备结束的百分比)
                    var reencWatch = System.Diagnostics.Stopwatch.StartNew();
                    int reencFrames = ReencodeDirPngToJpg(framesFinal, 0, progress, ct, prepPct);
                    reencWatch.Stop();
                    LastFrameReencodeSeconds = reencWatch.Elapsed.TotalSeconds;
                    LastFrameReencodeFrames = reencFrames;
                    if (reencFrames > 0)
                        AppLogger.Info($"整理帧(JPG):{reencFrames} 帧 / {LastFrameReencodeSeconds:0.#} 秒"
                            + $"({LastFrameReencodeSeconds * 1000 / reencFrames:0} ms/帧;引擎 PNG → JPG 降临时盘,完工后进入超分阶段)");
                }
            }
            else
            {
                // 【新顺序(1x/2x)】源帧不能在这里删 —— 它们是超分阶段的输入(超分读 framesIn、写 upOutput),
                // 补帧则在超分之后读 upOutput。源帧的释放交给超分阶段【逐批清盘】(该批输入帧用完即删,见批次 finally);
                // 补帧输出(超分帧)的释放交给补帧阶段【逐段清盘】(每段跑完即删)。
                AppLogger.Info($"[阶段顺序] 新顺序(1x/2x):超分阶段将读源帧目录 framesIn({frameCount} 帧)并逐批释放,补帧读取超分输出");
            }

            // 4) 超分(可选):批处理在【阶段顺序决定的输入】上进行;1x 时直接剔除(不超分,帧原样使用)
            //    1x超分(2x放大后缩回):内部按 2x 超分,再缩回原始尺寸,输出仍是 1x
            //    【输入目录】旧顺序(3x/4x)= framesFinal(补帧结果);新顺序(1x/2x)= framesIn(源帧)—— 由 upInput 决定。
            //    两种顺序的输出目录都是 upOutput(workDir\upscaled),临时文件口径一致。
            var upInput = upscaleFirst ? framesIn : framesFinal;
            var upOutput = Path.Combine(workDir, "upscaled");
            var framesInterp = Path.Combine(workDir, "frames_interp");   // 新顺序:补帧输出(编码/合帧读它)
            if (doUpscale && scale <= 1.001 && !upscaleShrink1x)
            {
                progress?.Report(((int)AlhPro.Core.ProgressBands.Of(AlhPro.Core.ProgressBands.Stage.Upscale).hi, "1x 不超分:跳过超分阶段"));
            }
            else if (doUpscale)
            {
                xUpscaleWatch.Start();   // 【任务 X3】超分阶段墙钟起点(与 UMark 无关的独立计时,只用于"每帧耗时"统计)
                // 【进度动态分段】超分区间随【实际阶段顺序】走,阶段名与真正在跑的阶段必须一致:
                //   新顺序(1x/2x,超分在前)  → 超分 10~45、补帧 45~90;
                //   旧顺序(3x/4x,补帧在前)  → 补帧 10~45、超分 45~90(原口径一字不改)。
                // 【2026-09-16 统一到 Core.ProgressBands】超分的区间**永远**是它自己那一段
                // (真机实测超分占总时长 54%,拿到最宽的一段)。顺序互换时补帧用补帧的、超分用超分的,
                // 不再"看谁先跑就给谁 10~45" —— 那会让最贵的超分只占 35 个点,进度条再次前倾。
                int upBase = (int)AlhPro.Core.ProgressBands.Of(AlhPro.Core.ProgressBands.Stage.Upscale).lo;
                int upEnd = (int)AlhPro.Core.ProgressBands.Of(AlhPro.Core.ProgressBands.Stage.Upscale).hi;
                int upPctLoArg = upBase;
                int upPctHiArg = upEnd;
                // 引擎内部按帧汇报的区间与上面同一份(区间唯一,不再有"旧顺序不传=沿用另一套口径")
                int? origW = null, origH = null;
                if (upscaleShrink1x)
                {
                    // 【必须读 upInput 而不是 framesFinal】新顺序下 framesFinal 此刻还是空的(补帧尚未跑),
                    // 照旧读 framesFinal 会拿不到尺寸 → origW/origH 为 null → 1x 缩回被静默跳过、输出变成 2x。
                    var firstFrame = Directory.EnumerateFiles(upInput, "*.jpg")
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
                bool waifuOnnx = false;   // 50系 waifu2x ncnn 不可用 → 整段视频改走 ONNX(安全网)
                bool upOnnxDml = false;   // 探测失败/不可用 → 走 ONNX 时用 DirectML GPU(-2 自动)而非强制 CPU(-1)
                bool ncnnUnreliable = false;   // 视频超分检测到 ncnn-Vulkan 黑帧 → 后续批次直接走 ONNX(不再每批先 ncnn 失败再降级,省极长时间)
                if (gpuId >= 0)
                {
                    // 【50 系不再"一律禁用 ncnn"】旧逻辑:Blackwell + waifu2x 直接改走 ONNX,不做任何实测。
                    // 旧口径漏检的根因已写明在 EngineService.IsEngineGpuUsableAsync(fullFrame) 上:
                    // 320×240(甚至 1×1)小图能过,真实分辨率才静默出 0KB 空帧/黑帧且【退出码 0】——
                    // 于是探测判"可用",坏帧一路进成片。现在改为:先按【生产形态】真机探测
                    // (1080×1920 + 真实模型 + 生产 -j + 带状黑判据),通过 → 就走 ncnn-Vulkan
                    // (真机实测 0.24~0.6 秒/帧,而"ONNX 落 CPU"是 8 秒/帧);失败 → 才改走 ONNX 并明确告知用户。
                    progress?.Report((upBase, $"正在检测超分 GPU 兼容性({engine},首次最长约 60 秒,结论会记住)..."));
                    bool usable = await EngineService.EnsureNcnnProbeAsync(engine, gpuId, model, ct).ConfigureAwait(false);
                    if (!usable)
                    {
                        if (engine == "waifu2x" && EngineService.IsBlackwellGpu())
                        {
                            // waifu2x 在 50 系:ncnn CPU 模式同样会崩(实测 exit -1073741819)——
                            // 不能像其他引擎那样"降 CPU",而是整段改走 ONNX 稳定版(DirectML/CPU 都行)
                            waifuOnnx = true;
                            upOnnxDml = true;
                            AppLogger.Warn($"⚠ waifu2x 在本机 50 系 GPU 上真机探测失败——为稳定性改用 ONNX 稳定版(整段视频,兼容模式)。"
                                + EngineService.LastProbeUserMessage);
                            progress?.Report((upBase, $"⚠ waifu2x 在本机 50 系 GPU 上不可用,为稳定性改用 ONNX(整段视频)..."));
                        }
                        else
                        {
                            // ncnn GPU 不可用:不急着掉最慢的 ncnn-CPU —— 先试 ONNX DirectML(与 ncnn-Vulkan
                            // 是两套完全独立运行时,这些卡 DirectML 往往能正常 GPU 加速);ONNX 失败才自动掉 CPU。
                            AppLogger.Warn($"⚠ 超分引擎 {engine} 真机探测失败(生产帧尺寸 1080×1920)——为稳定性改用 ONNX DirectML GPU(比 ncnn-CPU 快一个数量级)。"
                                + EngineService.LastProbeUserMessage);
                            progress?.Report((upBase, $"⚠ 超分引擎 {engine} 无法用 ncnn GPU,为稳定性改用 ONNX 稳定引擎(DirectML GPU)..."));
                            upGpu = -1;          // 触发下方 ONNX 分支
                            upOnnxDml = true;    // 且用 DirectML GPU(-2 自动选设备),而非强制 CPU
                            waifuOnnx = engine == "waifu2x" ? true : waifuOnnx;   // waifu2x 探测失败同样走 ONNX
                        }
                    }
                    else
                    {
                        AppLogger.Info($"✅ 超分引擎 {engine} 真机探测通过(生产帧尺寸 1080×1920,GPU {gpuId})→ 使用 ncnn-Vulkan(未因 50 系而禁用)");
                    }
                }
                else if (engine == "waifu2x" && EngineService.IsBlackwellGpu())
                {
                    // 用户选了 CPU(-g -1):50 系 waifu2x 的 ncnn CPU 模式有崩溃 bug → 直接整段走 ONNX 更稳
                    waifuOnnx = true;
                    AppLogger.Warn("⚠ 50系 waifu2x:CPU(-g -1)模式有崩溃 bug,自动改走 ONNX 稳定版");
                }
                // ===== 设备实际用途诊断(进诊断包:一眼分辨"显示 GPU 却实际跑 CPU/慢路径")=====
                // 超分阶段已在此定死:waifuOnnx → ONNX 整段;upGpu<0 → ONNX DirectML 或 CPU;否则 ncnn-GPU。
                // 补帧/编码实际设备在各自阶段已记录;这里只汇总超分(最常掉 CPU 的一步)+ 标注哪些环节待确认。
                {
                    string upPath;
                    if (waifuOnnx) upPath = upOnnxDml ? "ONNX DirectML(GPU 稳定版)" : "ONNX CPU(无 DirectML)";
                    else if (upGpu < 0) upPath = upOnnxDml ? "ONNX DirectML(GPU,探测失败降级)" : "ONNX CPU";
                    else upPath = $"ncnn-Vulkan GPU(编号 {upGpu})";
                    string upDeviceName = upGpu >= 0 ? GpuInfo.GetEngineDeviceName(upGpu) : "(非 GPU)";
                    AppLogger.Info($"设备实际用途:超分[{engine}] 走 {upPath}(设备:{upDeviceName});补帧/编码设备见各自阶段日志(超分是最常掉 CPU 的一步,此处已定死)");
                }
                // 分批目录批处理超分 + 并行 2 批(多 worker):
                // 一次引擎启动处理一批帧,避免每帧启动引擎;批间并行提高 GPU 利用率
                // upInput/upOutput 已在超分阶段入口声明(upInput = 新顺序 framesIn / 旧顺序 framesFinal)。
                Directory.CreateDirectory(upOutput);
                var upFiles = Directory.EnumerateFiles(upInput, "*.jpg")
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
                // 批大小/并发按"安全渲染"墙自适应(内存/显存墙越小越保守)。
                // 【2026-09-13】批大小改为在算出"唯一帧数"之后再定(见下方 PlanVideoBatches 调用点)——
                // 旧口径只看空闲内存、不知道素材规模,几十帧的小素材在低内存档上也会被切成好几批,
                // 每批重新启动一次引擎进程(启动+模型加载是秒级固定开销),用户看到的就是"批间停顿几秒"。
                var total = upFiles.Length;
                // ===== 相同帧只超分一次(无损提速;决策①=B 决策②=拷贝)=====
                // 原理:InterpLayerBatchAsync 的 slotSrc 会把同一源文件写进多个输出槽(:4076 静止帧对 / :4079 phi≤0.001 /
                // :4080 phi≥0.999),framesFinal 里会有多份字节相同帧。超分对相同输入是字节级确定的。
                // → 按内容哈希把相同帧归组,只让"唯一帧"进引擎,重复槽位拷贝代表帧的超分结果。
                // 硬约束:重复槽位必须与代表帧【同批】(批并行跑,跨批引用会在代表帧未产出时崩溃)。
                //        回填放在本批 PNG→JPG 转换后、同一个 task 内立即做。
                // 进度/ETA 按【槽位】记账(不是组号):静止素材多时按组号推进会虚高(违反"预览=结果")。
                // finally 删源帧按【本批显式槽位列表】删(槽位不再连续,:1448 的 start..end 区间写法失效)。
                // 关键:源帧一个都不动(决策①=B)——fallback 三处(:1394/:1404/:1437)都要读 upFiles[i]。
                // batchSize 按【唯一帧(组)数】切批(组的重复槽 = 纯拷贝,不占引擎输入/PNG 峰值)——
                // 磁盘峰值与今天一致,批大小按用户口径(设备档位 + 素材长度)算,见下方「超分批决策」日志。
                // 先按文件长度分组(重复帧长度必然相同),只在同长度组内算哈希 → 长度唯一的帧(绝大多数)不读内容。
                // repIdx[i] = 槽位 i 的代表槽(自身 = 无重复);repOf/group 供切批与回填用。
                int[] repIdx = new int[total];
                for (int i = 0; i < total; i++) repIdx[i] = i;
                var groupsByRep = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<int>>();
                try
                {
                    var byLen = new System.Collections.Generic.Dictionary<long, System.Collections.Generic.List<int>>();
                    for (int i = 0; i < total; i++)
                    {
                        long len;
                        try { len = new FileInfo(upFiles[i]).Length; } catch { continue; }
                        if (!byLen.TryGetValue(len, out var lst)) byLen[len] = lst = new System.Collections.Generic.List<int>();
                        lst.Add(i);
                    }
                    foreach (var kv in byLen)
                    {
                        if (kv.Value.Count < 2) continue;   // 长度唯一 → 必然无重复,不读内容
                        var byHash = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<int>>();
                        foreach (var i in kv.Value)
                        {
                            string h = ContentHash(upFiles[i]);
                            // 哈希失败返回空串:不能让多个"哈希失败"的文件共用空串被当成同一组——
                            // 那样回填会把 A 帧的超分结果盖到 B 帧槽位上(静默错帧)。失败=按无重复处理。
                            if (h.Length == 0) continue;
                            if (!byHash.TryGetValue(h, out var hl)) byHash[h] = hl = new System.Collections.Generic.List<int>();
                            hl.Add(i);
                        }
                        foreach (var hv in byHash)
                        {
                            if (hv.Value.Count < 2) continue;
                            var grp = hv.Value;
                            grp.Sort();                     // 取最小槽号为代表(时间轴最靠前)
                            int rep = grp[0];
                            var slotList = new System.Collections.Generic.List<int>(grp);
                            for (int k = 1; k < grp.Count; k++) repIdx[grp[k]] = rep;
                            groupsByRep[rep] = slotList;    // 含代表自己
                        }
                    }
                }
                catch (Exception de)
                {
                    AppLogger.Warn($"⚠ 超分去重分组失败({de.Message.Split('\n')[0]}),本次按原路径逐帧超分(不影响输出)");
                    groupsByRep.Clear();
                    for (int i = 0; i < total; i++) repIdx[i] = i;
                }
                int uniqueCount = total - groupsByRep.Values.Sum(g => g.Count - 1);
                int dupCount = total - uniqueCount;
                if (dupCount > 0)
                    AppLogger.Info($"超分去重:{total} 帧中 {dupCount} 帧与已处理帧字节相同,已复用结果(省 {Math.Round(100.0 * dupCount / total, 1)}% 超分算力)");
                // ===== 批次决策:设备档位 + 视频长度 + 补帧后总帧数(2026-09-13 按用户口径重定)=====
                // 用户口径:「处理前按当前设备来看;设备正常+视频短+补帧完的帧总数少 → 完全可以不分批;
                // 设备好+视频长 → 批内扩大(200/400 帧);设备差 → 最低 50 一批」。
                // 规则与门槛全在 AlhPro.Core.RenderPolicy.PlanVideoBatches(纯函数、有单测):
                //  · 设备档位 = 空闲内存(<4G 差 / 4~8G 正常 / ≥8G 好;不新造探测);
                //  · 设备 ≥ 正常 且 补帧后总帧数 ≤ 400 → 单批;
                //  · 设备好:源帧数 ≥ 900(≈30s@30fps) → 400 帧/批,否则 200;
                //  · 设备正常:沿用既有内存档(120/180);设备差:50(用户下界);
                //  · fastMode/diskTight 减半保留,但钳到 ≥ 50(用户下界优先)。
                // sourceFrames = 去重后的源帧数(视频长度口径);postInterpFrames = total = 本阶段实际输入帧数
                // (补帧→超分 顺序下超分读的就是补帧输出,即"补帧后总帧数")。
                // 【任务 Q2】还把本阶段输入帧的分辨率传进去:每批帧数按面积反比缩放(1080p 基准),
                // 让"输入帧 + 本批输出帧并存"的峰值临时盘/内存不随分辨率暴涨(4K 源每帧像素是 1080p 的 4 倍)。
                // 【任务 R3】还传本阶段的【输出】分辨率:峰值 = "输入帧 + 本批输出帧并存",
                // 超分阶段输出是放大 scale² 倍的帧 → 这部分必须算进去(否则超分批偏大、峰值被低估)。
                var batchPlan = SafeRender.GetVideoBatchPlan(frameCount, total, fastMode, diskTight, srcW, srcH,
                    (int)Math.Max(1, Math.Round(srcW * (upscaleShrink1x ? 2.0 : Math.Max(1.0, scale)))),
                    (int)Math.Max(1, Math.Round(srcH * (upscaleShrink1x ? 2.0 : Math.Max(1.0, scale)))),
                    perfScore);
                int batchSize = batchPlan.BatchSize;
                // 【任务 T 要求 4:峰值守门必须按**最终生效的**批上限复算】上面那份预估用的是"候选批大小"
                // (diskTight=false 时的最大值);这里批大小已定稿,再用它重算一次并**真的**判一次剩余空间 ——
                // 批上限 400→700(×1.75)后"输入帧 + 本批输出帧并存"的同屏峰值同倍数上涨,不复算就是漏守门。
                try
                {
                    double needBytesFinal = AlhPro.Core.TempSpaceEstimate.NeedBytesForBatch(
                        peakFrames, outFrameMB, batchSize, srcFrameMB);
                    double needGBFinal = needBytesFinal / (1024.0 * 1024.0 * 1024.0);
                    var driveNow = new System.IO.DriveInfo(PickTempRoot());
                    double freeNow = driveNow.AvailableFreeSpace;
                    AppLogger.Info($"峰值守门复算(超分阶段定稿批大小):每批 {batchSize} 帧 → 预计 {needGBFinal:0.#} GB"
                        + $"(全片 {peakFrames} 帧 + 每批并存 {batchSize} 帧),临时盘剩余 {freeNow / (1024 << 20):0}GB");
                    if (freeNow < needBytesFinal)
                        throw new System.IO.IOException(
                            $"临时磁盘空间不足:{driveNow.Name} 仅剩 {freeNow / (1024 << 20):0}GB,"
                            + $"本任务按当前批大小({batchSize} 帧/批)预计需要约 {needGBFinal:0}GB。"
                            + "请清理磁盘、降低补帧/超分倍率,或把视频放到其它盘后再处理。");
                }
                catch (System.IO.IOException) { throw; }
                catch { /* 探测失败不影响处理(与改动前一致:拿不到剩余空间就不做这道判定) */ }
                // 【日志必须能解释批数】档位 / 源帧数 / 补帧后总帧数 / 本阶段输入 / 每批帧数 / 预计批数 / 命中规则,
                // 全部一行写清(PlanVideoBatches 的 Rule 里也带着每条门槛的实际取值与依据)。
                AppLogger.Info($"超分批决策:档位={batchPlan.Tier}(空闲内存 {SafeRender.FreeRamGB:0.#}G)→ 每批 {batchSize} 帧;"
                    + $"本阶段输入 {total} 帧;兼容模式={fastMode},临时盘紧={diskTight};命中规则:{batchPlan.Rule}");
                // 【任务 X3】定格本阶段输入帧数(超分保帧数、不增减 → 结算时用它算"超分 ms/帧")。
                xUpscaleFrames = total;
                // 唯一帧/组按槽号升序排列(保持时间轴顺序);补齐孤立的唯一槽(无重复的帧)
                for (int i = 0; i < total; i++)
                    if (repIdx[i] == i && !groupsByRep.ContainsKey(i))
                        groupsByRep[i] = new System.Collections.Generic.List<int> { i };
                var repSlots = groupsByRep.Keys.OrderBy(i => i).ToList();
                using var sem = new SemaphoreSlim(fastMode ? 1 : SafeRender.GetVideoConcurrency());   // 兼容模式:单批防显存竞争
                int doneFrames = 0;
                int releasedInputFrames = 0;   // 已按【批】释放的输入帧数(边用边删;审计日志用)
                int upCleanupLogged = 0;       // 【R1】已写进日志区的清理进度(每累计 800 帧一行,避免刷屏)
                var tasks = new System.Collections.Generic.List<Task>();
                // 按【唯一帧(组)数】切批(非槽数):每批引擎正好处理 batchSize 个唯一帧 → 磁盘峰值=今天一致。
                var batchGroups = new System.Collections.Generic.List<System.Collections.Generic.List<(int rep, List<int> slots)>>();
                var curBG = new System.Collections.Generic.List<(int rep, List<int> slots)>();
                foreach (var rep in repSlots)
                {
                    if (curBG.Count >= batchSize) { batchGroups.Add(curBG); curBG = new System.Collections.Generic.List<(int rep, List<int> slots)>(); }
                    curBG.Add((rep, groupsByRep[rep]));
                }
                if (curBG.Count > 0) batchGroups.Add(curBG);
                int batchCount = batchGroups.Count;
                // 【任务 X1/X2】界面:批大小结论**只在【处理开始】打一次**;这里仅在"开始那行没打过"时补一句短句
                // (例如只超分不补帧:顺序/批计划那块根本没执行,界面若不打就等于没有批次信息)。
                // 【为什么放在这里】批数要报**真实值**(按去重后的唯一帧组数切出来的),上面那条 plan 里的是【预计值】;
                // 命中规则/档位依据/面积系数等解释性内容一律留在上面那条文件日志里,不上界面。
                if (!xBatchLineShown)
                    progress?.Report((upBase, "· " + AlhPro.Core.LogShortText.ClampToChineseLimit(
                        $"批大小:超分 {batchSize} 帧/批 ×{batchCount} 批"
                        + $"(设备档:{AlhPro.Core.LogShortText.DeviceTierShortText(SafeRender.FreeRamGB, perfScore)})")));
                // 每批的起始槽位 = 【确定性前缀和】(前面各批的槽位数之和),不再读 doneFrames。
                // doneFrames 只在批次【结束】时累加,而批次是并行跑的(SemaphoreSlim(GetVideoConcurrency()) 允许多批在飞),
                // 所以"批次开始时读 doneFrames"读到的是别的批次的进度 → 进度计数器来回跳(诊断包里 65→131→199→33→2→7→11)、
                // "仅首批写自检日志"(batchStartSlot==0)在非首批误触发、超分 ETA 的基准也跟着错。
                // 前缀和对批次序号单调,三个用途一次修好。
                var batchStartSlots = new int[batchCount];
                var batchSlotCounts = new int[batchCount];
                for (int bi = 0, acc = 0; bi < batchCount; bi++)
                {
                    batchStartSlots[bi] = acc;
                    int cnt = 0;
                    foreach (var g in batchGroups[bi]) cnt += g.slots.Count;
                    batchSlotCounts[bi] = cnt;
                    acc += cnt;
                }
                // 【J 修复 · 2026-09-13 批号越界(13/12)】批号+区间在这里【一次性定格】成不可变清单:
                // 原来日志在 async 任务里现算 `第 {bi+1}/{batchCount} 批`,而 `bi` 是 for 循环变量 ——
                // C# 里 for 的循环变量只有一个、被所有闭包共享(foreach 才是每轮一份),任务在 finally 里
                // 读到的往往是"循环已推进、甚至已结束"后的值 → 真机日志出现「超分批 13/12(槽位 2640~2666)」
                // 与「超分批 2/12(槽位 0~239)」:槽位区间是对的(每轮局部量),只有编号被读晚(偏移 +1/+2)。
                // 注意:**实际只跑了 12 批**(batchCount=12,槽位区间连续覆盖 0~2666),不是多起了一次引擎 ——
                // 那是显示口径问题,引擎启动次数没有变化。
                var batchInfos = AlhPro.Core.RenderPolicy.DescribeBatches(batchSlotCounts);
                // 超分阶段自己的 ETA 时钟 + 休息/暂停累计(这两段时间不产出任何帧,必须从耗时里扣掉)
                var srStageStart = DateTime.UtcNow;
                double srIdleSec = 0;
                // 【诊断 · 2026-09-16 超分变慢排查】超分阶段全程采 GPU 利用率,用来定性"慢"到底是
                // 「GPU 没吃饱(宿主侧落盘/编码是瓶颈)」还是「GPU 算力本身不够」。采不到就是 null,照常跳过。
                // 只进日志文件(见下方「超分实测」与「超分诊断」两行),**不上界面**、不改任何处理路径。
                var gpuSampler = SafeRender.StartGpuUtilSampler();
                // 引擎内部的逐帧汇报(超分期间刷屏最频繁的那条)只有"本批"信息、没有 ETA → 包一层统一补上
                // 【G-补1】upcoming = 超分阶段结束之后马上要做的收尾:旧顺序是"合帧前的整理与编码"
                // (统一 JPG + 编码),新顺序则是补帧阶段 —— 不足 1 秒时把它说出来,别再给含糊的"几秒"。
                string srNextStep = upscaleFirst ? "补帧阶段" : "合帧前的整理与编码";
                var srProgress = progress == null ? null : new EtaProgress(progress,
                    () => (long)doneFrames,
                    done => EtaStr(done, total, (DateTime.UtcNow - srStageStart).TotalSeconds - srIdleSec, srNextStep));
                try
                {
                for (int bi = 0; bi < batchCount; bi++)
                {
                    ct.ThrowIfCancellationRequested();
                    var curPG = batchGroups[bi];
                    // 【J 修复】本批的编号/区间取【定格快照】:闭包只捕获这个局部量,捕获不到 for 循环变量 bi
                    // (bi 会被所有任务共享,日志里读它必然读晚 → "13/12")。分母 batchCount 与 batchInfos.Count
                    // 同源同值,所以分子永远 ≤ 分母。
                    var batchInfo = batchInfos[bi];
                    // 该批全部槽位(代表 + 重复),用于进度/ETA 按槽位记账、finally 按槽位删源帧。
                    var batchSlots = new List<int>();
                    foreach (var g in curPG) batchSlots.AddRange(g.slots);
                    batchSlots.Sort();
                    // 处理过程也做降温休息检查(单个长视频也能中途休息;按批检查,高频批时开销极小)
                    // 进度按【槽位】:本批起始槽位 = 前面各批槽数之和(确定性,不读并行更新的 doneFrames)
                    int batchStartSlot = batchStartSlots[bi];
                    var idleT0 = DateTime.UtcNow;
                    await SafeRender.RestIfDueAsync(upBase + (int)((upEnd - upBase) * batchStartSlot / Math.Max(1, total)), progress, ct);
                    if (pauseWait != null) await pauseWait();   // 暂停:当前超分批跑完即停(几秒~十几秒)
                    srIdleSec += (DateTime.UtcNow - idleT0).TotalSeconds;
                    await sem.WaitAsync(ct);
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            var start = batchSlots[0];
                            var swBatchDbg = System.Diagnostics.Stopwatch.StartNew();   // 【诊断】本批墙钟(见下方「超分诊断」行)
                            var batchIn = Path.Combine(workDir, $"up_in_{start}");
                            var batchOut = Path.Combine(workDir, $"up_out_{start}");
                            Directory.CreateDirectory(batchIn);
                            Directory.CreateDirectory(batchOut);
                            // 只拷贝【代表帧】进引擎(唯一帧;重复槽不占引擎输入/PNG 峰值)
                            foreach (var g in curPG)
                                File.Copy(upFiles[g.rep], Path.Combine(batchIn, Path.GetFileName(upFiles[g.rep])), true);
                            progress?.Report((upBase + (int)((upEnd - upBase) * batchStartSlot / Math.Max(1, total)),
                                $"超分 已处理 {batchStartSlot} 帧 / 共 {total} 帧(批次 {batchInfo.Number}/{batchCount}){EtaStr(batchStartSlot, total, (DateTime.UtcNow - srStageStart).TotalSeconds - srIdleSec)}..."));
                            // 引擎真的开始出活时再报一句(带【实际启动耗时】),此后回到引擎自己的逐帧口径
                            // ("超分 第 N 帧 / 共 M 帧")。只报一次:降级链可能在同一批里再起进程(GPU→换卡→…)。
                            int upReadyReported = 0;
                            Action<double> upOnEngineReady = sec =>
                            {
                                if (Interlocked.Exchange(ref upReadyReported, 1) != 0) return;
                                try
                                {
                                    progress?.Report((upBase + (int)((upEnd - upBase) * batchStartSlot / Math.Max(1, total)),
                                        $"第 {batchInfo.Number}/{batchCount} 批:超分引擎已就绪(启动 {sec:0.0}s),开始处理本批 {curPG.Count} 个唯一帧…"));
                                    AppLogger.Info($"第 {batchInfo.Number}/{batchCount} 批:超分引擎已就绪(启动 {sec:0.0}s,本批 {curPG.Count} 个唯一帧,帧号 {batchStartSlot}~{batchSlots[^1]})");
                                }
                                catch { }
                            };
                            // 视频超分:50系/无独显/手动CPU + Real-ESRGAN/waifu2x + ONNX 模型在 → 走 ONNX 逐帧(不走会崩的 ncnn-vulkan)
                            string? onnxModelPath = null;
                            if (upGpu < 0)
                            {
                                // 手动选 CPU:waifu2x/realesrgan 的 ncnn CPU 模式在部分机器崩(实测 exit -1/-1073741819)→ 直接 ONNX
                                if (engine == "realesrgan")
                                    onnxModelPath = EsrganOnnxService.ResolveEsrganOnnxPath(model);
                                else if (engine == "waifu2x")
                                    onnxModelPath = EsrganOnnxService.FindWaifu2xModel(model);
                            }
                            else if (engine == "realesrgan" && (EngineService.ShouldUseOnnxEsrgan() || ncnnUnreliable || fastMode))
                                onnxModelPath = EsrganOnnxService.ResolveEsrganOnnxPath(model);
                            else if (engine == "waifu2x" && (EngineService.ShouldUseOnnxWaifu2x() || waifuOnnx || ncnnUnreliable || fastMode))
                                onnxModelPath = EsrganOnnxService.FindWaifu2xModel(model);
                            // 【让"批间停顿"可见】本批的计算引擎还没起来 —— ncnn 是"进程启动 + 模型加载",
                            // ONNX 稳定引擎是"每批新建 DirectML 推理会话",两者都是秒级固定开销。
                            // 先如实说明"正在启动(约 N 秒)",别让进度条与文案在这几秒里一动不动
                            // (用户唯一的感受就是"卡死")。文案刻意不含「完成」二字:UI 会把含"完成"的
                            // 进度行改写成"✓ …"并清掉当前步骤行,会误判成阶段结束。
                            // 只加这一行进度上报:处理顺序/参数/并发一律不变。
                            {
                                double upStartSec = AlhPro.Core.VideoPipeline.AssumedEngineStartupSecondsPerBatch;   // 待实测标定
                                string upStarting = onnxModelPath != null
                                    ? "正在创建超分推理会话(稳定引擎 ONNX)"
                                    : $"启动超分引擎(约 {upStartSec:0.#} 秒,首次较慢)";
                                progress?.Report((upBase + (int)((upEnd - upBase) * batchStartSlot / Math.Max(1, total)),
                                    $"第 {batchInfo.Number}/{batchCount} 批:{upStarting};本批 {curPG.Count} 个唯一帧…"));
                            }
                            if (onnxModelPath != null)
                            {
                                if (batchStartSlot == 0)   // 仅首批写自检日志(视频批多,避免刷屏)
                                {
                                    AppLogger.Info($"✅ 自检:视频超分({engine})已按当前显卡自动改用稳定引擎(直接处理,无需设置)");
                                    // 【模型如实告知】ONNX 稳定引擎的 waifu2x 目前只有 cunet 一份:用户在 50 系(走 ONNX)
                                    // 下拉选的 upconv_7_photo / upconv_7_anime【用不上】,画面确实比 Real-ESRGAN 柔和。
                                    // 不说明的话,用户只会觉得"视频 waifu 超分差"却查不出原因(实测确认:选的模型被静默忽略)。
                                    if (engine == "waifu2x" && !string.IsNullOrEmpty(model))
                                    {
                                        string want = model.Replace("models-", "", StringComparison.OrdinalIgnoreCase)
                                                           .Replace("models_", "", StringComparison.OrdinalIgnoreCase);
                                        if (!Path.GetFileName(onnxModelPath).Contains(want, StringComparison.OrdinalIgnoreCase))
                                        {
                                            AppLogger.Info($"ℹ 视频超分:稳定引擎(ONNX)的 waifu2x 只有 cunet,已忽略所选模型「{model}」");
                                            progress?.Report((upBase + (int)((upEnd - upBase) * batchStartSlot / Math.Max(1, total)),
                                                $"ℹ 稳定引擎(ONNX)的 waifu2x 仅有 cunet 模型,已按 cunet 处理 —— 你选的「{model}」需要 ncnn 引擎;写实片源建议改选 Real-ESRGAN"));
                                        }
                                    }
                                    // 【2026-09-22 用户追问"自训模型兼容性"时查出来的真缺口】
                                    // 自训那三支(现实 · alhreal2x / 游戏 · alhgame2x-v2 / -v3)**只有 ncnn 权重**,
                                    // 走 ONNX 时上面那个 ResolveEsrganOnnxPath 会把它们按"通用"分支解析成**官方模型**
                                    // (x4plus.onnx,机器上没有就退 animevideov3.onnx)—— 而**一句提示都没有** ✗。
                                    // 这与 RELEASE_NOTES 第 40 行与官网 changelog 写的"软件会明确告诉你…不会闷声换模型"
                                    // **正好相反**,也违反本仓库"静默换模型是明令禁止的"这条规矩。
                                    // 文案的唯一出处是 AlhPro.Core.ExperimentalEsrgan.OnnxFallbackNotice ——
                                    // 它一直写着、也有单测,但**在本轮之前全仓库没有任何地方调用它**(这才是缺口本身)。
                                    if (engine == "realesrgan" && AlhPro.Core.ExperimentalEsrgan.IsExperimental(model))
                                    {
                                        string selNotice = AlhPro.Core.ExperimentalEsrgan.OnnxFallbackNotice(model);
                                        AppLogger.Warn("⚠ 视频超分:" + selNotice);
                                        progress?.Report((upBase + (int)((upEnd - upBase) * batchStartSlot / Math.Max(1, total)),
                                            selNotice));
                                    }
                    // 视频降噪(用户那个「启用视频降噪」开关):拆帧阶段由 nlmeans 处理,超分阶段由 waifu2x 自带降噪处理。
                    // 正常情况下上面已用探测结果保证二者择一;这里兜的是【探测通过、但本批仍走了 ONNX】的边角情形
                    // (例如中途黑帧降级把 ncnnUnreliable 置位、或用户勾了兼容模式)——那时模型档不生效,
                    // 而帧已经拆完(没法回头再补 nlmeans),只能如实告知并给出替代做法。
                    if (denoiseViaModel && videoDenoise > 0)
                    {
                        AppLogger.Info($"ℹ 视频超分:稳定引擎(ONNX)不支持 waifu2x 自带降噪档(视频降噪 {videoDenoise}),本批未降噪");
                        progress?.Report((upBase + (int)((upEnd - upBase) * batchStartSlot / Math.Max(1, total)),
                            $"ℹ 稳定引擎(ONNX)不支持 waifu2x 自带降噪(本批降噪无效)—— 需要降噪请把超分引擎改为 Real-ESRGAN(那时「视频降噪」由拆帧阶段的 nlmeans 执行)"));
                    }
                                    // 【不要轻易掉 CPU】若这就要落 CPU(-1 = 强制 CPU),黄字明示用户(而非静默跑慢几倍)
                                    if (upGpu < 0 && !upOnnxDml)
                                        progress?.Report((upBase + (int)((upEnd - upBase) * batchStartSlot / Math.Max(1, total)),
                                            $"⚠ 本机无可用 GPU 加速(DirectML 不可用),超分已降级为 CPU——速度会变得特别慢(可能慢数倍)。建议更新显卡驱动后重启软件再试"));
                                }
                                progress?.Report((upBase + (int)((upEnd - upBase) * batchStartSlot / Math.Max(1, total)),
                                    $"超分(稳定引擎) 批次 {batchInfo.Number}/{batchCount}{EtaStr(batchStartSlot, total, (DateTime.UtcNow - srStageStart).TotalSeconds - srIdleSec)}..."));
                                await EsrganOnnxService.UpscaleDirAsync(batchIn, batchOut, upScale,
                                    upGpu < 0 ? (upOnnxDml ? -2 : -1) : -2, srProgress, ct, onnxModelPath,
                                    batchStartSlot, total, pauseWait, upPctLoArg, upPctHiArg);   // 用户主动选 CPU(-1)强制 CPU;探测失败(upOnnxDml)用 -2=DirectML GPU 自动;正常 GPU 也 -2 自适应;pauseWait=ONNX/CPU 也能暂停
                                    // 末两参 = 逐帧进度的百分比区间:新顺序(超分排第一)传 10~45,旧顺序传 0/0=沿用引擎原口径
                            }
                            else
                            {
                                await EngineService.UpscaleDirAsync(batchIn, batchOut, engine, model,
                                    upScale, denoiseViaModel ? waifu2xNoiseArg : 0, upGpu, false, srProgress, ct,   // waifu2x 引擎:用它的 -n(模型自带降噪);值由 Core.Waifu2x.NoiseLevelFor(UI 档位, 引擎倍率) 算出(单调 + 1x 护栏)
                                    SafeRender.GetVideoTileSize() / (fastMode ? 2 : 1),   // 显卡家族感知分块(视频超分专用);兼容模式再减半(显存占用约降 4 倍)
                                    watchStage: "超分",   // 逐帧汇报(像补帧一样显示"超分 第 N 帧 / 共 M 帧")
                                    globalBaseFrames: batchStartSlot, globalTotalFrames: total,   // 百分比按全局帧数算,预计时间才准
                                    outFormat: "jpg",   // 引擎直出 JPG:4K 实测 2.98→2.02 秒/帧(省 31%),且省掉下面整段 PNG 解码+q96 重编码
                                    pctLo: upPctLoArg, pctHi: upPctHiArg,   // 新顺序(超分排第一)逐帧区间 10~45;旧顺序 0/0=原有 45~90 口径不变
                                    onEngineReady: upOnEngineReady);   // 引擎真的开始出活时回报"已就绪(启动 X.Xs)"(只上报,不改处理)
                            }
                            // 【诊断】引擎这一段的墙钟(落盘/判黑/回退/回填都不算在内)—— 与"落盘+回填"一起把每帧耗时拆开。
                            double engineSecDbg = swBatchDbg.Elapsed.TotalSeconds;
                            // 【峰值优化】本批超分 PNG 立即转 JPG 再落 upOutput(不再全量 PNG 累积到最后统一转):
                            // 超分过程中只有"当前批的 PNG"存在,upOutput 全程 JPG,峰值降 70%+。
                            // 黑帧防御:ncnn-vulkan 偶发 vkQueueSubmit 失败 → 输出全黑帧(退出码 0 不报错)。
                            // 判黑顺带在转 JPG 已解码的位图上做,不再为查黑把整批多解码一次。
                            // 转不了(0KB 空帧/坏帧——ncnn 在部分驱动上静默产出,退出码仍 0)必抛异常,同样记缺陷:
                            // 放过它们,一路传到合帧只会报"找不到 frame_%06d.jpg"。
                            // 不可提前 break:源帧本就是黑场时会跳过降级,此时每张 JPG 都必须已落盘。
                            bool anyFrame = false, anyDefective = false;
                            // 【黑帧防线·按帧对应】记下"被判黑 / 转码失败"的具体帧,回退前只对这些帧检查其
                            // 【对应源帧】是否也本来就近黑。原先用 DirNearBlack(batchIn) 做整批判断,而它是
                            // 【存在量词】(任一源帧近黑即返回 true)→ 一批 64 帧里只要有一帧是黑场
                            // (片头黑场/淡入淡出/夜戏/闪黑),本批 GPU 输出的【全部】黑帧都被当成"素材本来如此"
                            // 放行,黑帧直接进成片且零日志,ncnnUnreliable 也不会置位(后续批次继续用坏引擎)。
                            var defectiveFrames = new System.Collections.Generic.List<string>();
                            foreach (var f in Directory.EnumerateFiles(batchOut, "*.*")
                                .Where(x => x.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                                         || x.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                                         || x.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)))
                            {
                                anyFrame = true;
                                var dst = Path.Combine(upOutput, Path.ChangeExtension(Path.GetFileName(f), ".jpg"));
                                try
                                {
                                    if (f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                                    {
                                        // 引擎写 PNG(图片分块路径/降级重算/ONNX):解码一次判黑,顺便转成 JPG
                                        EngineService.ConvertPngToJpg(f, dst, VideoFrameJpgQuality, out bool isBlack);
                                        if (isBlack) { anyDefective = true; defectiveFrames.Add(f); }
                                    }
                                    else
                                    {
                                        // 引擎直出 JPG(outFormat:"jpg"):只需解码一次判黑,然后【搬】过去。
                                        // 不再"解码 PNG → 重编码 q96":引擎写的是 q100,画质更好,且 4K 下省掉 31% 超分耗时。
                                        if (EngineService.IsBlackPng(f)) { anyDefective = true; defectiveFrames.Add(f); }
                                        File.Move(f, dst, overwrite: true);
                                    }
                                }
                                catch { anyDefective = true; defectiveFrames.Add(f); try { File.Copy(f, Path.Combine(upOutput, Path.GetFileName(f)), true); } catch { } }
                            }
                            // 兜底防误杀:若【被判黑/失败的每一帧】其源帧本来就近全黑(视频黑场/淡入淡出),
                            // 输出黑是素材本身,不是 GPU 故障——跳过降级,不浪费 CPU 重算。
                            // 空批(!anyFrame)必然是真故障:一帧都没出,与素材内容无关,必须降级。
                            if ((anyDefective || !anyFrame) && !DefectiveFramesAllComeFromNearBlack(batchIn, defectiveFrames))
                            {
                                // ===== 黑帧降级(2026-09-16 二次修订:只重跑黑帧 + 硬预算)=====
                                // 【为什么第二次改】旧逻辑只要本批出现【任何一帧】黑,就删掉整批输出、整批换 ONNX 重跑。
                                // 真机实测(RTX 4060,2026-09-16 21:22):120 帧的批里只有 1 帧被判黑,却整批重跑 ——
                                // ONNX 这边跑了 22 分钟(≈11 秒/帧,健康时约 0.3 秒/帧),用户等了 26 分钟只能强制结束;
                                // 同一形态 09-15 还出现过 4 次(代码里那笔"4060 黑帧重跑 282 分钟"是同一个根因)。
                                // 更糟的是重跑仍黑时【整批回退源帧】⇒ 好帧跟着一起牺牲,那 120 帧整段画质掉档(只缩放、没真超分)。
                                // 现在:① 只重跑【被判黑的那几帧】,同批好帧原样保留(不再白算);
                                //       ② 重跑带硬预算,到点就停、只把没救回来的帧回退源帧 —— 最多等两三分钟,不无限期干等;
                                //       ③ ncnn 是否"不可靠"改为达到【系统性】程度才置位(见下),零星 1 帧不再把全片拖去 ONNX。
                                // 两套运行时独立:ncnn GPU 崩 ≠ DirectML 崩,所以重跑才有意义。
                                string? onnxB = engine == "realesrgan"
                                    ? EsrganOnnxService.ResolveEsrganOnnxPath(model)
                                    : engine == "waifu2x" ? EsrganOnnxService.FindWaifu2xModel(model) : null;
                                int upPctRetry = upBase + (int)((upEnd - upBase) * batchStartSlot / Math.Max(1, total));
                                // ① 待重跑的帧清单。正常 = 被判黑的那几帧(用 batchIn 里的同名副本当输入;
                                //    回退时也拿它当"源帧"—— WriteFallbackFrame 按文件名写回 upOutput,扩展名一致)。
                                //    整批一帧都没产出(真故障,与素材内容无关)→ 只能整批重跑。
                                bool wholeBatchRetry = !anyFrame || defectiveFrames.Count >= curPG.Count;
                                var retrySrcs = new System.Collections.Generic.List<string>();
                                if (wholeBatchRetry)
                                {
                                    foreach (var g in curPG) retrySrcs.Add(upFiles[g.rep]);
                                }
                                else
                                {
                                    foreach (var f in defectiveFrames)
                                    {
                                        string bn = Path.GetFileNameWithoutExtension(f);
                                        string srcIn = Path.Combine(batchIn, bn + ".jpg");
                                        if (!File.Exists(srcIn)) srcIn = Path.Combine(batchIn, bn + ".png");
                                        if (File.Exists(srcIn)) retrySrcs.Add(srcIn);
                                    }
                                }
                                // ② 预算:建会话 + 每帧留 3 秒(健康 DirectML 实测 ≈0.3 秒/帧,约 10 倍余量),
                                //    下限 90 秒(会话创建 + DML 预热),硬顶 240 秒。到点即停 —— 这条闸门就是本轮的修法核心。
                                int retryBudgetSec = Math.Clamp(90 + retrySrcs.Count * 3, 90, 240);
                                // ③ ncnn 是不是【系统性】不可靠:整批空产,或缺陷帧占本批 ≥20%。达标才把后续批次整体改走 ONNX。
                                //    (旧逻辑"1 帧黑 = 后续全片改 ONNX"在 4060 上代价极大:ONNX 实测可能 11 秒/帧。)
                                if (!anyFrame || defectiveFrames.Count * 5 >= Math.Max(1, curPG.Count))
                                {
                                    ncnnUnreliable = true;
                                    AppLogger.Warn($"⚠ ncnn-Vulkan 超分判定为系统性不可靠(本批 {curPG.Count} 帧里 {defectiveFrames.Count} 帧黑"
                                        + (anyFrame ? "" : "、整批未出帧") + ")→ 后续批次直接走 ONNX 稳定引擎");
                                }
                                if (retrySrcs.Count == 0)
                                {
                                    // 防御:一个源帧都定位不到(理论上不可达)→ 退回"整批回退源帧"的老办法。
                                    // 绝不让黑帧留在成片里:宁可这批画质掉档(只缩放),也不交付黑帧。
                                    AppLogger.Warn($"⚠ 批次 {start}~{batchSlots[^1]} 黑帧定位不到对应源帧——该批整批回退源帧(不重跑)");
                                    foreach (var si in batchSlots)
                                        try { WriteFallbackFrame(upFiles[si], upOutput, upScale); } catch { }
                                }
                                else if (onnxB != null)
                                {
                                    // 【设备要说真话】这段文案以前直接写"ONNX DirectML",可它的判据只是"ONNX 模型文件在"
                                    // (复核报告点名):EsrganOnnxService 只是【尝试】挂 DirectML,挂不上会静默建 CPU 会话、
                                    // 照样打印同一句话。这里把【解析出来的真实设备号】一并写进日志,含解析失败的情形。
                                    string dmlText;
                                    try
                                    {
                                        int dmlHere = upGpu >= 0
                                            ? EngineService.ResolveDmlDevice(upGpu) : EsrganOnnxService.DmlFallbackOk;
                                        dmlText = dmlHere >= 0 ? $"DirectML 设备 #{dmlHere}"
                                            : "DirectML 设备解析失败(会退到 CPU 会话,详见后续「会话实际设备」日志)";
                                    }
                                    catch { dmlText = "DirectML 设备解析异常(详见后续「会话实际设备」日志)"; }
                                    progress?.Report((upPctRetry,
                                        (wholeBatchRetry
                                            ? $"⚠ 检测到黑帧(批次 {start}~{batchSlots[^1]},GPU 输出异常),该批改用 ONNX 引擎重处理..."
                                            : $"⚠ 检测到 {retrySrcs.Count} 帧黑帧(批次 {start}~{batchSlots[^1]}),只重跑这几帧(同批其余帧保留)...")
                                        + StageElapsed()));
                                    AppLogger.Warn($"⚠ 批次 {start}~{batchSlots[^1]} 输出黑帧(ncnn-vulkan GPU 队列异常)——"
                                        + (wholeBatchRetry ? "整批" : $"只把被判黑的 {retrySrcs.Count} 帧")
                                        + $"改用 ONNX 稳定引擎({Path.GetFileNameWithoutExtension(onnxB)},目标 {dmlText})重跑"
                                        + (wholeBatchRetry ? "" : $"(同批其余 {curPG.Count - retrySrcs.Count} 帧沿用 ncnn 结果,不重算)"));
                                    // 重跑只在【一个小输入目录 + 一个小输出目录】里做:batchOut 里已经落好的好帧一个都不动。
                                    var retryIn = Path.Combine(workDir, $"up_retry_in_{start}");
                                    var retryOut = Path.Combine(workDir, $"up_retry_out_{start}");
                                    try { Directory.Delete(retryIn, true); } catch { }
                                    try { Directory.Delete(retryOut, true); } catch { }
                                    Directory.CreateDirectory(retryIn);
                                    Directory.CreateDirectory(retryOut);
                                    foreach (var rs in retrySrcs)
                                        try { File.Copy(rs, Path.Combine(retryIn, Path.GetFileName(rs)), true); } catch { }
                                    bool retryBudgetHit = false;
                                    string retryFailWhy = "";
                                    using (var retryCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                                    {
                                        retryCts.CancelAfter(TimeSpan.FromSeconds(retryBudgetSec));
                                        try
                                        {
                                            await EsrganOnnxService.UpscaleDirAsync(retryIn, retryOut, upScale,
                                                upOnnxDml ? -2 : (upGpu < 0 ? -1 : -2), progress, retryCts.Token, onnxB,
                                                start, total, pauseWait, upPctLoArg, upPctHiArg);   // 探测失败/黑帧 → -2(DirectML GPU 自动);主动选 CPU → -1;pauseWait=ONNX/CPU 也能暂停
                                        }
                                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                                        {
                                            retryBudgetHit = true;   // 只超了【重跑预算】,不是用户取消(用户取消必须原样传播出去)
                                        }
                                        catch (Exception ex)
                                        {
                                            if (ct.IsCancellationRequested) throw;
                                            retryFailWhy = ex.Message.Split('\n')[0];
                                        }
                                    }
                                    if (retryBudgetHit)
                                        AppLogger.Warn($"⚠ 批次 {start}~{batchSlots[^1]} 黑帧重跑超过预算 {retryBudgetSec} 秒,已停止重跑"
                                            + $"(要救 {retrySrcs.Count} 帧;健康 DirectML 实测约 0.3 秒/帧,超预算说明这条路过慢)"
                                            + "—— 没出结果的帧回退源帧,不再无限期干等");
                                    if (retryFailWhy.Length > 0)
                                        AppLogger.Warn($"⚠ 批次 {start}~{batchSlots[^1]} 黑帧重跑失败({retryFailWhy})——这 {retrySrcs.Count} 帧按「没救回来」处理");
                                    // 逐帧认领重跑结果:好的写进 upOutput(覆盖那张黑帧),没出/仍黑的【只回退这一帧】。
                                    int retriedOk = 0, retriedBack = 0;
                                    foreach (var rs in retrySrcs)
                                    {
                                        string bn = Path.GetFileNameWithoutExtension(rs);
                                        string? got = null;
                                        foreach (var ext in new[] { ".png", ".jpg", ".jpeg" })
                                        {
                                            string cand = Path.Combine(retryOut, bn + ext);
                                            if (File.Exists(cand)) { got = cand; break; }
                                        }
                                        bool stillBlack = true;   // 没出结果(超预算/引擎没写)= 没救回来
                                        if (got != null)
                                        {
                                            var dst = Path.Combine(upOutput, bn + ".jpg");
                                            try
                                            {
                                                if (got.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                                                {
                                                    EngineService.ConvertPngToJpg(got, dst, VideoFrameJpgQuality, out bool isBlack);
                                                    stillBlack = isBlack;
                                                }
                                                else
                                                {
                                                    // 引擎直出 JPG:解码判黑后直接搬(ONNX 路径目前写 PNG,此处为兼容两种格式)
                                                    stillBlack = EngineService.IsBlackPng(got);
                                                    File.Move(got, dst, overwrite: true);
                                                }
                                            }
                                            catch { stillBlack = true; }   // 0KB/坏帧:当没救回来
                                        }
                                        if (stillBlack)
                                        {
                                            retriedBack++;
                                            try { WriteFallbackFrame(rs, upOutput, upScale); } catch { }
                                        }
                                        else retriedOk++;
                                    }
                                    // 【绝不跑慢速 CPU】ONNX(DirectML)也救不回来的帧,直接回退源帧(超分 CPU 兜底要跑到天荒地老)。
                                    // 【只回退这几帧】旧逻辑在这里整批作废,好帧跟着牺牲 —— 那正是"120 帧整段掉档"的来源。
                                    if (retriedBack > 0)
                                    {
                                        progress?.Report((upPctRetry,
                                            $"⚠ ONNX 重跑仍黑 {retriedBack} 帧(批次 {start}~{batchSlots[^1]}),这几帧回退原帧(不跑慢速 CPU)..." + StageElapsed()));
                                        AppLogger.Warn($"⚠ 批次 {start}~{batchSlots[^1]} ONNX(DirectML)重跑仍黑——{retriedBack} 帧回退源帧"
                                            + $"(已尽力重跑,仍无法得非黑;救回来的 {retriedOk} 帧已保留);若反复出现请更新显卡驱动");
                                    }
                                    else
                                    {
                                        AppLogger.Info($"✅ 批次 {start}~{batchSlots[^1]} 黑帧重跑成功:{retriedOk} 帧已由 ONNX 补回"
                                            + (wholeBatchRetry ? "" : $"(同批其余 {curPG.Count - retrySrcs.Count} 帧未重算,沿用 ncnn 结果)"));
                                    }
                                    try { Directory.Delete(retryIn, true); } catch { }
                                    try { Directory.Delete(retryOut, true); } catch { }
                                }
                                else
                                {
                                    // 无 ONNX 模型:黑帧【不跑慢速 CPU】,只把这【几帧】回退源帧(只做一次缩放,绝不把黑帧写进输出)。
                                    // 【改这里的原因】旧逻辑连"没有 ONNX 可重跑"的情况也整批作废 —— 同批好帧跟着一起被牺牲,没必要。
                                    progress?.Report((upPctRetry,
                                        $"⚠ 检测到黑帧(批次 {start}~{batchSlots[^1]},GPU 输出异常),"
                                        + (wholeBatchRetry ? "该批" : $"被判黑的 {retrySrcs.Count} 帧")
                                        + "回退原帧(无 ONNX 模型,不跑慢速 CPU)..." + StageElapsed()));
                                    AppLogger.Warn($"⚠ 批次 {start}~{batchSlots[^1]} 输出黑帧(GPU 队列异常),无 ONNX 模型——"
                                        + (wholeBatchRetry ? "该批" : $"被判黑的 {retrySrcs.Count} 帧") + "回退源帧(若反复出现请更新显卡驱动)");
                                    foreach (var rs in retrySrcs)
                                        try { WriteFallbackFrame(rs, upOutput, upScale); } catch { }
                                }
                            }
                            // ===== 回填重复槽位:拷贝代表帧的超分输出到该组重复槽(决策②=拷贝,非硬链接)=====
                            // 硬约束:重复槽与代表帧同批 → 代表帧输出已在本批 upOutput 落盘,此处同 task 内立即拷贝,
                            // 无跨批依赖(批并行跑,跨批引用会在代表帧未产出时崩溃)。
                            // 拷贝路径与 :1486/:1512 的"src==dst 原地重写"互补:这里写不同文件(rep→slot),不会踩到
                            // 硬链接的并发原地写坏帧问题。纯磁盘 IO,每次拷贝后查 ct,极长静止组(重复槽多)也即时可取消。
                            int backfilled = 0;
                            foreach (var g in curPG)
                            {
                                if (g.slots.Count <= 1) continue;   // 无重复槽,无需回填
                                var repName = Path.GetFileName(upFiles[g.rep]);
                                string repJpg = Path.Combine(upOutput, Path.ChangeExtension(repName, ".jpg"));
                                if (!File.Exists(repJpg)) continue;   // 拿不到代表输出(异常路径已回退),跳过
                                foreach (var s in g.slots)
                                {
                                    if (s == g.rep) continue;
                                    string dupJpg = Path.Combine(upOutput, Path.ChangeExtension(Path.GetFileName(upFiles[s]), ".jpg"));
                                    if (File.Exists(dupJpg)) continue;   // 已存在(异常回退已写),不覆盖
                                    try { File.Copy(repJpg, dupJpg, true); backfilled++; }
                                    catch { }
                                    if ((backfilled & 0x3F) == 0)   // 每 64 个拷贝报一次进度,长静止组不冻进度
                                        progress?.Report((upBase + (int)((upEnd - upBase) * batchStartSlot / Math.Max(1, total)),
                                            $"超分 复用重复帧({backfilled} 帧已回填)..."));
                                    if (ct.IsCancellationRequested) break;
                                }
                                if (ct.IsCancellationRequested) break;
                            }
                            // 【诊断 · 2026-09-16】一行把"本批每帧耗时"拆开,**只写日志文件**(不上界面、不改处理):
                            //   · 引擎 ms/帧   = 引擎进程这一段(引擎自己也会在「引擎完成」那行报它的耗时)
                            //   · 落盘+回填 ms/帧 = 判黑解码 / 黑帧回退 / 重复帧回填 —— 宿主侧的那部分
                            // 配上「超分实测」里补的 GPU 利用率就能定性:利用率低 + 宿主占比高 ⇒ 瓶颈在宿主/编码;
                            // 利用率高 + 引擎占比高 ⇒ 就是 GPU 算力/驱动这一层,软件侧没有可优化的余地。
                            // 引擎参数也一并如实写出来(旧日志里只写"路线=ncnn-Vulkan",分块/线程/输出格式都看不到)。
                            {
                                double landSecDbg = swBatchDbg.Elapsed.TotalSeconds - engineSecDbg;
                                double perDbg = Math.Max(1, curPG.Count);
                                AppLogger.Info($"[超分诊断] 批 {batchInfo.Number}/{batchCount}(唯一帧 {curPG.Count}):"
                                    + $"引擎 {engineSecDbg:0.0}s = {engineSecDbg * 1000.0 / perDbg:0} ms/帧 · "
                                    + $"落盘+回填 {landSecDbg:0.00}s = {landSecDbg * 1000.0 / perDbg:0} ms/帧 · "
                                    + $"本批墙钟 {swBatchDbg.Elapsed.TotalSeconds:0.0}s · "
                                    + $"引擎参数 -s {upScale:0.##} -t 0(自动){SafeRender.GetEngineThreadArgs()} -f jpg · "
                                    + $"并发路数 {(fastMode ? 1 : SafeRender.GetVideoConcurrency())} · "
                                    + $"批开始空闲显存 {SafeRender.FreeVramGB:0.#}G");
                            }
                            Interlocked.Add(ref doneFrames, batchSlots.Count);
                            progress?.Report((upBase + (int)((upEnd - upBase) * doneFrames / total),
                                $"超分 已处理 {doneFrames} 帧 / 共 {total} 帧"));
                            if (fastMode)
                            {
                                // 兼容模式:强制 GC,内存峰值降到最低(弱设备不内存墙)
                                GC.Collect();
                                GC.WaitForPendingFinalizers();
                            }
                            // 批次完成即释放临时帧(所有模式:大幅降低磁盘峰值,防"爆盘"失败)
                            try { Directory.Delete(batchIn, true); } catch { }
                            try { Directory.Delete(batchOut, true); } catch { }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception batchEx)
                        {
                            // ===== 超分批次失败加固 =====
                            // realesrgan/waifu2x ncnn-vulkan 在长时间运行会中途 AccessViolation 崩溃(exit -1073741819),
                            // 原逻辑无外层 catch → 异常冒泡 → await Task.WhenAll(tasks) 抛错 → 整个超分阶段/任务失败
                            // ("成功 0,失败 1")。现在:引擎崩溃/异常时【该批帧直接回退原帧】,不中断整个任务——
                            // 视频仍能出,只是这批帧未超分(画质略低),绝不"整段视频全丢"。
                            // 取消异常不吞(仍走取消);
                            string head = batchEx.Message.Split('\n')[0];
                            if (head.Length > 90) head = head[..90];
                            AppLogger.Warn($"⚠ 超分批次 {batchSlots[0]}~{batchSlots[^1]} 失败({head})——该批回退原帧,继续(不中断任务)");
                            progress?.Report((upBase + (int)((upEnd - upBase) * batchStartSlot / Math.Max(1, total)),
                                $"⚠ 超分批次 {batchSlots[0]}~{batchSlots[^1]} 异常({head}),该批回退原帧,继续处理..."));
                            // 清空本批半成品,回退原帧(未超分帧缩放后复用源帧;batchOut 临时目录由任务收尾统一清理)
                            foreach (var si in batchSlots)
                            {
                                try { WriteFallbackFrame(upFiles[si], upOutput, upScale); }
                                catch { }
                            }
                            Interlocked.Add(ref doneFrames, batchSlots.Count);
                            progress?.Report((upBase + (int)((upEnd - upBase) * doneFrames / total),
                                $"超分 已处理 {doneFrames} 帧 / 共 {total} 帧(部分回退原帧)"));
                        }
                        finally
                        {
                            // 【降临时盘峰值·批处理清盘】本批输入帧(本批全部槽位:代表+重复)已全部超分输出到 upOutput,
                            // 用完即删,避免"全部输入帧 + 全部超分帧"同时占盘(长视频高倍率时可省几十 G)。
                            // 槽位不再连续(start..end 区间写法失效),必须按本批显式槽位列表删。
                            // 【顺序无关】新顺序(1x/2x)下 upInput = 源帧 framesIn、旧顺序(3x/4x)下 = 补帧结果 framesFinal,
                            // 两种顺序都在这里逐批释放 —— 这正是"边用边删"的落点,不等整阶段结束。
                            // 【为什么在这里删是安全的】本批所有输出(含重复槽回填、黑帧回退原帧、批次异常回退原帧)
                            // 都在本 task 内、finally 之前写完,删的是本批【全部槽位】的输入;批与批之间按槽位不重叠。
                            int relCnt = 0;
                            foreach (var si in batchSlots)
                            {
                                try { File.Delete(upFiles[si]); relCnt++; } catch { /* 删不掉不影响正确性:阶段收尾还会再扫一次 */ }
                            }
                            Interlocked.Add(ref releasedInputFrames, relCnt);
                            Interlocked.Add(ref uReleasedFrames, relCnt);   // 【任务 U】清理释放台账(超分阶段)
                            AppLogger.Info($"[临时清理] 超分批 {batchInfo.Number}/{batchCount}(槽位 {batchSlots[0]}~{batchSlots[^1]})完成:已释放" +
                                (upscaleFirst ? "源帧" : "补帧帧") + $" {relCnt} 帧(本阶段累计 {Volatile.Read(ref releasedInputFrames)} 帧;目录 {Path.GetFileName(upInput)})");
                            // 【界面可见性 · 2026-09-13】逐批清盘过去只写 AppLogger,界面上完全看不到"边跑边释放临时帧"
                            // (长素材批数多,用户只看到盘在掉、界面无任何动静)。这里就地更新一条轻提示:
                            //   · 沿用【当前进度百分比】(doneFrames 单调递增 → 只会往前、不会把进度往回带);
                            //   · 文案不带帧号 → 不与「超分 第 N 帧 / 共 M 帧」抢步骤行;不含"完成"二字 → 不会被
                            //     UI 改写成"✓ …"并清掉步骤行;不带 [临时清理] 前缀(那是日志口径);
                            //   · 本批没释放到帧(relCnt == 0)时不发,避免噪声。
                            // 纯提示:删除时机/并发/处理顺序一律未动(下面 sem.Release 照旧)。
                            if (relCnt > 0)
                            {
                                try
                                {
                                    progress?.Report((upBase + (int)((upEnd - upBase) * Volatile.Read(ref doneFrames) / Math.Max(1, total)),
                                        $"本批已释放 {relCnt} 帧临时文件(累计 {Volatile.Read(ref releasedInputFrames)} 帧)"));
                                    // 【R1】同一条信息再走一遍【左下角日志区】(`· ` 前缀 → UI 在节流之前就追加,
                                    // 不会被吞;不动步骤行/进度条/警告色)。每累计 800 帧才一行,长片几行,无感。
                                    int relTotal = Volatile.Read(ref releasedInputFrames);
                                    int lastLogged = Volatile.Read(ref upCleanupLogged);
                                    if (relTotal - lastLogged >= 800
                                        && Interlocked.CompareExchange(ref upCleanupLogged, relTotal, lastLogged) == lastLogged)
                                        progress?.Report((upBase + (int)((upEnd - upBase) * Volatile.Read(ref doneFrames) / Math.Max(1, total)),
                                            $"· 临时文件清理:超分阶段已释放 {relTotal} 帧输入帧(逐批删,用完即删,省临时盘)"));
                                }
                                catch { }
                            }
                            sem.Release();
                        }
                    }, ct));
                }
                }
                catch (OperationCanceledException)
                {
                    // 取消:等已启动的批任务退出(它们携带 ct,很快收尾),避免与 finally 删 workDir 竞态
                    try { await Task.WhenAll(tasks); } catch { }
                    throw;
                }
                await Task.WhenAll(tasks);
                // ===== 临时文件控制:超分阶段收尾补扫 —— 输入帧其实已由【每批 finally】逐批释放(见上),
                // 这里只兜"某批删除失败/取消残留"的尾巴,正常情况为 0 帧(不再等到整阶段结束才删)。
                // (8x 补帧+超分后输入帧体积巨大,逐批清避免"超分帧+输入帧"同时占盘)
                try
                {
                    int delCnt = 0;
                    foreach (var f in Directory.EnumerateFiles(upInput, "*.jpg")) { File.Delete(f); delCnt++; }
                    AppLogger.Info($"[临时清理] 超分阶段收尾:输入目录 {Path.GetFileName(upInput)} 补扫释放 {delCnt} 帧" +
                        $"(共 {upFiles.Length} 帧,其中 {Volatile.Read(ref releasedInputFrames)} 帧已在逐批清盘中释放)");
                }
                catch { /* 清理失败忽略 */ }
                framesFinal = upOutput;   // 合帧使用超分后的帧
                // 1x 修复(现实那条):2x 超分后缩回原始尺寸。
                // 【2026-09-21 优化 · 先量后改】原实现是**逐帧 C# 缩放**:每帧独立"解码 → GDI 高质量缩放 → 再编码",
                //   而且给**每一帧**都开一个 Task(长片/4K 上几百上千个任务同时抢内存与 GDI 句柄 ✗)。
                //   实测(`_qa\bench_shrink.py`,60 帧 1080p→540p):逐帧串行 34.0 ms/帧、逐帧并行 8.4 ms/帧、
                //   **一条 ffmpeg 命令 4.9 ms/帧** ⇒ 改走 ffmpeg(单进程、内存有界、快 1.7~7 倍)。
                //   ⚠ 但这只占整个 1x 时间的一小部分:2x 超分本机约 350 ms/帧(1080p),缩回约占 1~2%
                //     —— "1x 现实这条慢"的根因是**它本来就要跑一遍 2x**,不是缩回本身(4K 素材上缩回才明显)。
                //   ⚠ 兜底不变:ffmpeg 失败或帧数对不上 ⇒ 回退到原来的逐帧路径(源帧损坏时生成占位帧、编号不断档)。
                if (upscaleShrink1x && origW is > 0 && origH is > 0)
                {
                    var shFiles = EnumerateFrameFiles(framesFinal)
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
                    progress?.Report((upEnd - 2, $"1x 修复:将 {shFiles.Length} 帧从 2x 缩回原始尺寸 {origW}×{origH}..."));
                    string? shrunkDir = await TryShrinkFramesWithFfmpegAsync(
                        ffmpeg, framesFinal, shFiles, origW.Value, origH.Value, progress, upEnd, ct);
                    if (shrunkDir != null)
                    {
                        AppLogger.Info($"[1x 修复] 缩回:ffmpeg 一趟走完 {shFiles.Length} 帧 "
                            + $"(实测 4.9 ms/帧 对 逐帧 34.0 ms/帧;原逐帧路径保留为兜底)");
                        framesFinal = shrunkDir;
                    }
                    else
                    {
                        AppLogger.Warn("[1x 修复] 缩回:ffmpeg 路径不可用/帧数不符 ⇒ 回退逐帧缩放(结果不受影响,只是慢一点)");
                        int doneSh = 0;
                        var shTasks = new System.Collections.Generic.List<Task>();
                        foreach (var f in shFiles)
                        {
                            ct.ThrowIfCancellationRequested();
                            shTasks.Add(Task.Run(() =>
                            {
                                // ResizeImageTo 内部已兜底:源帧损坏时生成同尺寸占位帧,保持 frame_%06d 编号连续、
                                // 可解码,合帧不中断(不删帧/不跳过,否则编号断档会截断输出)。
                                EngineService.ResizeImageTo(f, f, origW.Value, origH.Value);
                                int d = Interlocked.Increment(ref doneSh);
                                if (d % 20 == 0 || d == shFiles.Length)
                                    progress?.Report((upEnd - 2 + (int)(2.0 * d / shFiles.Length),
                                        $"1x 修复 缩回中 {d} 帧 / 共 {shFiles.Length} 帧"));
                            }, ct));
                        }
                        await Task.WhenAll(shTasks);
                    }
                }
                // 【实测速度上报】把"这一阶段到底多快"写进日志 —— 排查"慢 / GPU 占用低"时先看这里,
            // 再与上面那条探测结论行(「真机探测通过→ncnn」 或 「探测失败→改用 ONNX」)对照,
            // 就能立刻判断是"路线选错了"还是"这条路本身就慢"。
            {
                double srSec = (DateTime.UtcNow - srStageStart).TotalSeconds - srIdleSec;
                double msPer = total > 0 ? srSec * 1000.0 / total : 0;
                // 【诊断 · 2026-09-16】把超分阶段全程采到的 GPU 利用率附在这行(采不到就如实写"未采到")。
                // 【怎么用】利用率长期只有 60~80% ⇒ GPU 没吃饱,瓶颈在宿主侧的落盘/编码/解码(可优化);
                //           利用率 95%+ 且「超分诊断」里引擎占比高 ⇒ 就是 GPU 算力这一层,软件侧无可优化。
                string gpuNote = gpuSampler?.Summary() is { } gsum
                    ? $" · GPU 利用率 {gsum}"
                    : " · GPU 利用率 未采到(非 NVIDIA 或驱动异常)";
                gpuSampler?.Dispose();
                gpuSampler = null;
                AppLogger.Info($"超分实测:{total} 帧 / {srSec:0.#}s = {msPer:0} ms/帧"
                    + $"({(srSec > 0.001 ? total / srSec : 0):0.##} 帧/秒) · 路线={(upOnnxDml ? "ONNX 稳定引擎(DirectML)" : "ncnn-Vulkan")}"
                    + gpuNote);
            }
                progress?.Report((upEnd, $"帧超分完成({total} 帧)" + StageElapsed()));
                xUpscaleWatch.Stop();   // 【任务 X3】超分阶段墙钟到此为止(下面新顺序的补帧另有秒表,不许算进来)
                // ===== 4.2) 新顺序(1x/2x)的补帧阶段:读超分输出(upOutput)、写 frames_interp =====
                // 与 3) 块里的旧顺序调用点是【同一个 InterpStageAsync】,只是输入/输出目录与探测尺寸不同 ——
                // 补帧方法、分段(segBounds)、帧数对齐(globalTarget)、时长表(finalDurs)全部照旧,不分叉。
                if (upscaleFirst)
                {
                    // 超分输出必须全是 .jpg:InterpSegmentAsync 按 frame_%06d.jpg 读输入(文件名硬编码 .jpg),
                    // 而超分异常回退路径可能在 upOutput 留下 .png(见本阶段的 File.Copy 兜底)。这里统一成 JPG,
                    // 正常路径(引擎直出 JPG)是空跑,不会重编码。
                    // 【G】同样传 progress + ct(可取消)并上报"整理帧(JPG)"进度;百分比沿用超分阶段结束值 upEnd。
                    {
                        var reencWatch = System.Diagnostics.Stopwatch.StartNew();
                        int reencFrames = ReencodeDirPngToJpg(upOutput, 0, progress, ct, upEnd);
                        reencWatch.Stop();
                        LastFrameReencodeSeconds = reencWatch.Elapsed.TotalSeconds;
                        LastFrameReencodeFrames = reencFrames;
                        if (reencFrames > 0)
                            AppLogger.Info($"整理帧(JPG):{reencFrames} 帧 / {LastFrameReencodeSeconds:0.#} 秒(超分输出兜底 PNG → JPG)");
                    }
                    Directory.CreateDirectory(framesInterp);
                    // RIFE GPU 探测按【补帧真正要处理的尺寸】做:新顺序下 = 超分后的尺寸
                    // (1x 缩回后 = 源尺寸;2x = 源×2)。探测结论带尺寸进缓存 key,不会与源尺寸那次的结论互相顶替。
                    int probeW = srcW, probeH = srcH;
                    try
                    {
                        var f0 = Directory.EnumerateFiles(upOutput, "*.jpg")
                            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                        if (f0 != null) { using var b0 = new System.Drawing.Bitmap(f0); probeW = b0.Width; probeH = b0.Height; }
                    }
                    catch { }
                    AppLogger.Info($"[阶段顺序] 超分阶段完成({total} 帧)→ 进入补帧阶段(输入 = 超分输出 {Path.GetFileName(upOutput)} {probeW}×{probeH},{total} 帧)");
                    xInterpWatch.Start();   // 【任务 X3】新顺序的补帧阶段(与旧顺序那次互斥,Start/Stop 累计)
                    await InterpStageAsync(upOutput, framesInterp, probeW, probeH, stageFractionalMult, stageTargetFrames);
                    xInterpWatch.Stop();
                    framesFinal = framesInterp;   // 编码/合帧继续读 framesFinal(= 补帧输出)
                }
            }
            // 【任务 U】阶段②统计:补帧 + 超分(两种阶段顺序都已跑完)+ 临时盘采样(处理中段)
            UMark("补帧+超分");
            USampleTemp();

            // 【后处理段的进度区间:提前定义】下面"缩放"与"整理帧/边缘抗锯齿"两小节共用同一段区间
            // (ProgressBands.Stage.Post = 66~84):缩放占前 3/4、整理帧/抗锯齿占后 1/4。
            // 依据:真机实测这一步合计 106s(占总时长 15%,其中边缘抗锯齿自己就 101.8s)。
            var postBand = AlhPro.Core.ProgressBands.Of(AlhPro.Core.ProgressBands.Stage.Post);
            int postResizeLo = (int)postBand.lo;
            int postResizeHi = (int)(postBand.lo + (postBand.hi - postBand.lo) * 0.75);

            // 4.5) 自定义输出分辨率:超分/补帧后批量缩放到精确 W×H(未超分时也生效,相当于统一尺寸)
            if (outWidth is > 0 && outHeight is > 0)
            {
                progress?.Report((postResizeLo, $"缩放到自定义分辨率 {outWidth}×{outHeight}..."));
                var resFiles = EnumerateFrameFiles(framesFinal)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
                int doneRes = 0;
                var resTasks = new System.Collections.Generic.List<Task>();
                foreach (var f in resFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    resTasks.Add(Task.Run(() =>
                    {
                        // ResizeImageTo 内部已兜底:坏帧→同尺寸占位帧,保持编号连续可解码,合帧不中断。
                        EngineService.ResizeImageTo(f, f, outWidth.Value, outHeight.Value);
                        int d = Interlocked.Increment(ref doneRes);
                        if (d % 20 == 0 || d == resFiles.Length)
                            progress?.Report((92 + (int)(6.0 * d / resFiles.Length),
                                $"缩放 已处理 {d} 帧 / 共 {resFiles.Length} 帧"));
                    }, ct));
                }
                await Task.WhenAll(resTasks);
            }

            // ===== 统一 JPG(降 200 多 G 的核心):把 framesFinal 剩余 PNG(超分放大后的大帧)重编码成 JPG =====
            // 放在帧数对齐之前、缩放之后。超分后的 4K PNG 单帧 10~20MB,转 JPG 后仅 ~1~2MB;
            // 引擎(超分/补帧)的 PNG 输出在此统一收束成 JPG,下游帧数对齐/合帧只读 .jpg。
            // 【边缘抗锯齿也在这里做】见 ReencodeDirPngToJpg 的注释:ffmpeg sab 在 4K 上要 4.88 秒/帧,
            // 换成这套 C# 并行实现约 0.1 秒/帧,而且与"本来就要做的 JPG 编码"合并,不多一代损失。
            var aaWatch = System.Diagnostics.Stopwatch.StartNew();
            // 【G】这里也传 curPct:AA 关(默认)时核心循环会发"整理帧(JPG) 第 N / M 帧"。
            // curPct 取【上一步实际报过的百分比】,避免百分比回退:
            //   · 跑过"缩放到自定义分辨率"时它报的是 92→98,故本步挂 98;
            //   · 否则取帧阶段结束口径(= 90:旧顺序"超分 45~90"、新顺序"补帧 45~90"都收在这里,
            //     与 StageProgressPct 的阶段划分一致;不能直接引用 upEnd/upBase —— 那两个局部量在超分块里,
            //     出了块就不可见)。
            // 【2026-09-16 统一到 Core.ProgressBands】后处理段(66~84)内部再分两小节:
            // 缩放占前 3/4(66→78),整理帧/边缘抗锯齿占后 1/4(78→84)。依据真机实测 106s(占总时长 15%,
            // 其中边缘抗锯齿自己就 101.8s)—— 原先它们挤在 90→100 那 10 个点里,"条快到底、画面还在磨"。
            int framePhaseEndPct = postResizeLo;
            int prepPct2 = (outWidth is > 0 && outHeight is > 0) ? postResizeHi : postResizeLo;
            int reencFrames2 = ReencodeDirPngToJpg(framesFinal, postAa, progress, ct, prepPct2);
            aaWatch.Stop();
            LastFramePostProcSeconds = postAa > 0 ? aaWatch.Elapsed.TotalSeconds : 0;
            LastFrameReencodeSeconds = aaWatch.Elapsed.TotalSeconds;
            LastFrameReencodeFrames = reencFrames2;
            if (postAa > 0)
            {
                AppLogger.Info($"画面后处理:边缘抗锯齿(强度 {postAa},C# 按帧并行)耗时 {aaWatch.Elapsed.TotalSeconds:0.#} 秒"
                    + " —— 该档原先在 ffmpeg 滤镜链里(4K 实测 4.88 秒/帧),现不再计入'编码/封装'");
            }
            else if (reencFrames2 > 0)
            {
                // AA 关着时这仍是一整批 PNG→JPG 重编码(耗时正比于帧数),过去完全不计时不记帧数;
                // 现在留一行,便于把"补帧跑完之后那段空白"归因清楚(ETA/经验库复盘也要用它)。
                AppLogger.Info($"整理帧(JPG):{reencFrames2} 帧 / {LastFrameReencodeSeconds:0.#} 秒"
                    + $"({LastFrameReencodeSeconds * 1000 / reencFrames2:0} ms/帧;合帧前的最后一次统一 JPG,之后进入编码)");
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
            // 【2026-09-16 用户实测修复 · 指定帧率会慢放 + 尾部丢内容】
            // 真机证据(源 519 帧 @23.976fps / 21.632s):指定 60fps 导出后场景硬切点整体后移,
            // 段长 7.97s → 9.52s = **1.195×**,第三刀直接消失;时长却仍是 21.632s ⇒ 画面被拉慢 1.195 倍。
            // 根因:这里原先不管有没有指定帧率,都按 `(原帧数-1)×倍率 ÷ 真实时长` 算标称帧率
            // (60fps 目标 + 3x ⇒ 标称 71.93fps、引擎补 1555 帧),而合帧阶段又按用户指定的 60fps 铺,
            // 60fps 只容得下 1298 帧 ⇒「帧数对齐:裁尾 253 帧」⇒ 尾部内容丢失 + 同一段内容被摊更长 = 慢放。
            // 正确口径:指定帧率时标称帧率**就是用户要的那个值**,帧数 = 目标帧率 × **源容器时长**
            // (合帧那边定帧率用的正是容器时长;用内容时长会差 1 帧),并且这些帧要覆盖源的全部内容。
            bool targetFpsMode = v4Interp && targetFps > 0;
            // 诊断日志要用:指定帧率模式下真正生效的目标帧率(baseFps 会被后续逻辑改写)。
            double? targetFpsDecl = targetFpsMode ? targetFps : null;
            // 诊断专用:指定帧率模式下按【源容器时长】应得的目标帧数(与 multTargetFpsOverride 同源口径;
            // 下面那条"补帧诊断"要拿它说明"上面那个旧口径的帧数本路不适用")。
            long targetFpsWantFrames = 0;
            double baseFps;
            if (targetFpsMode)
            {
                baseFps = targetFps!.Value;
                if (trueDur > 0.01)
                {
                    // 【帧数口径必须用**容器时长**】合帧那边定帧率用源容器时长(见"方案 B(用户定)"),
                    // 两者能差 1 帧:518 帧 @23.976 ⇒ 内容时长 21.563s、容器 21.632s;
                    // 按内容时长算 = round(60×21.563) = 1294 帧,合帧要 1298 帧 ⇒ 尾帧仍被裁。
                    double durForTarget = await ProbeDurationSeconds(inputVideo);
                    if (!(durForTarget > 0.01)) durForTarget = trueDur;   // 取不到就退回内容时长(防御)
                    long wantFrames = (long)Math.Round(baseFps * durForTarget);
                    targetFpsWantFrames = wantFrames;
                    // 步长基准用**补帧阶段真正消费的帧数** frameCount(去重会删帧,必须与它同源)。
                    multTargetFpsOverride = frameCount > 1
                        ? (wantFrames - 1) / (double)(frameCount - 1)
                        : 0;
                    AppLogger.Info($"指定帧率口径:标称 {baseFps:0.##} fps × 源容器时长 {durForTarget:0.###}s ≈ 目标 {wantFrames} 帧"
                        + $"(内容时长 {trueDur:0.###}s;补帧输入 {frameCount} 帧 ⇒ 每源帧 {multTargetFpsOverride:0.###} 帧,"
                        + $"而非整数倍率 {interpScale}x 的 {frameCount * interpScale} 帧;不再先多补再裁尾)");
                }
            }
            else if (v4Interp && trueFrames > 0)
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
            // 【任务 S1】平滑时间轴:输出网格就是"目标帧率"(= 内容帧率 × 补帧倍率),标称帧率必须按它来,
            // 否则会被下面的公式估成别的数(内容帧率模式/新顺序都可能算出不一样的 baseFps);
            // 而**是否真的走了填平**由 flattenActive 决定(探针失败/ONNX 路线会回退,那时不许改口径)。
            if (flattenActive) baseFps = flatPlan.TargetFps;
            double outFps = baseFps;
            // 视频滤镜链:1x 修复(Anime4K) → 后处理(锐化/清晰/…) → 可选 fps 重映射 → 强制 yuv420p
            // 【2026-09-21 已删】原先这条链里还挂着「果冻修复」的两段(minterpolate→tmix 运动模糊、deshake 去抖),
            // 用户要求整块删除 ⇒ 现在合帧只有**一条**滤镜链,不再有按档位切换的第二张 filter_complex 图。
            var preParts = new System.Collections.Generic.List<string>();
            var postParts = new System.Collections.Generic.List<string>();
            // 【1x 修复(Anime4K)· 2026-09-21】放在**滤镜链最前**:先在原分辨率上做修复+锐化,再做用户的后处理。
            // 为什么在这条链里而不是单开一个阶段:它就是个 ffmpeg 滤镜(libplacebo),跟着合帧一起跑最省事,
            // 而且合帧用的那个 ffmpeg 进程本来就把工作目录设成了自己所在目录 ⇒ 相对路径 `shaders/xxx.glsl` 可用。
            if (anime4k1x)
            {
                preParts.Add(AlhPro.Core.Anime4k.FilterArgument());
                AppLogger.Info($"1x 修复:已启用 {AlhPro.Core.Anime4k.DisplayName} 着色器(原分辨率修复+锐化,不放大;" +
                    $"着色器 {AlhPro.Core.Anime4k.ShaderRelativePath},随 ffmpeg 目录下发)");
            }
            var postFilter = AlhPro.Core.VideoPostFilters.Build(postSharpen, postClarity, postUsm, postDetail, postAa, postEdge);
            if (postFilter != null) preParts.Add(postFilter);
            // 视频降噪(空间+时间,去噪点/闪烁/压缩噪点),放最前:先降噪再锐化
            // 【已挪走】视频降噪原先挂在这里(合帧滤镜链)→ 作用在"超分后的帧"上,4K 下 1.56 秒/帧;
            // 现改为拆帧阶段应用(源分辨率,0.49 秒/帧,便宜 2.5~3.5 倍);waifu2x 引擎则交给模型自带降噪档。
            // 见本文件上方 scaleVf 构造处的「视频降噪:放在拆帧阶段」。
            // 【2026-09-21】原先这里还有一行 `if (postDeshake) postParts.Add("deshake");`(果冻修复·画面去抖)。
            // 整块「果冻修复」已按用户要求删除 —— 界面控件与处理端滤镜链一起删,不留"偷偷还在跑"的口子。
            // 【C3 · 2026-09-16】合帧阶段的"保节奏"判据:只要有真实时长表要保就保,
            // **不再要求"没指定帧率"** —— 指定帧率只决定"倍率补多少",不决定"要不要按真实节奏铺开"。
            // 【2026-09-21 唯一例外已消失】原先这里还排除了「果冻修复·运动模糊」(那条滤镜链自带 CFR 时间
            // 重采样 minterpolate→tmix→fps,与可变时间轴不同源,只能继续挂 fps 滤镜)。整块「果冻修复」已按
            // 用户要求删除 ⇒ 判据回到**只看 preserveRhythm**,下游的 rhythmAtMux 别名也随之取消(一个值两个
            // 名字正是"看着像还有第二条判据"的来源)。行为上:原先运动模糊关着时这条恒等于 preserveRhythm。
            // 【消费点 1/3 · 指定帧率分支】保节奏 ⇒ 输出走**可变帧率时间轴**、绝不挂 fps 滤镜;
            // 不保节奏时才允许挂 fps 精确缩到用户选的那个值。
            if (frameInterp && targetFps is > 0)
            {
                if (preserveRhythm)
                {
                    double avg = baseFps;
                    string why = dedup ? "去重删过帧" : "VFR 素材";
                    if (avg < targetFps.Value - 0.5)
                        AppLogger.Warn($"⚠ 指定 {targetFps.Value:0.##} fps 在「保节奏」模式下达不到:按真实时间轴输出后平均约 {avg:0.##} fps"
                            + $"(内容帧率 {effectiveFps:0.##} fps × 倍率 {interpScale}x)。成片节奏与源一致,帧率比指定值低 —— "
                            + "保节奏与「精确到某一个帧率」本来就取不到同一个值:想要那个数就得取消「去重」后重跑。");
                    else
                        AppLogger.Info($"指定 {targetFps.Value:0.##} fps + 保节奏({why}):改用可变帧率时间轴输出,平均约 {avg:0.##} fps"
                            + $"(内容帧率 {effectiveFps:0.##} fps × 倍率 {interpScale}x);不挂 fps 滤镜 —— 原片的停顿长短按真实时长保留");
                    progress?.Report((94, avg < targetFps.Value - 0.5
                        ? $"⚠ 指定 {targetFps.Value:0.##} fps:保节奏模式平均约 {avg:0.##} fps(节奏与源一致)"
                        : $"指定 {targetFps.Value:0.##} fps:按原片真实节奏重定时(平均约 {avg:0.##} fps)"));
                }
                else
                {
                    // 帧数已够(含上调后):精确重映射到目标帧率(时长不变),输出 = 用户指定值
                    if ((v4Interp ? inFps : effectiveFps) * interpScale >= targetFps.Value - 0.5 || v4Interp)
                    {
                        postParts.Add($"fps={targetFps.Value.ToString("0.##", inv)}");
                        outFps = targetFps.Value;
                        double achievableNow = (v4Interp ? inFps : effectiveFps) * interpScale;
                        // 【死代码说明 · 2026-09-16 审计第 3 条】本分支下面的"达不到"告知在**当前闸门下不可达**:
                        // 外层条件已含 `|| v4Interp`,而「指定帧率」这个下拉只在 v4 模型下可用
                        // (VideoView 侧 TargetFpsRadio 的置灰条件)—— 进入本块时 v4Interp 必为 true ⇒ 永远走上面那一支。
                        // 【为什么保留】它是"引擎能力不足"的真实兜底:一旦 `IsV4Model` 的判据放宽(或将来允许非 v4 指定帧率),
                        // 这一支立刻变成活代码。所以只留说明,不删分支 —— 免得后人当成活路径去测、或当成废码直接删掉。
                        if (achievableNow < targetFps.Value - 0.5)
                            AppLogger.Warn($"⚠ 指定 {targetFps.Value:0.##} fps 超出当前补帧能力({interpScale}x 约 {achievableNow:0.##} fps):"
                                + $"成片仍标 {targetFps.Value:0.##} fps,但差额部分是重复帧(不是插值帧),流畅度不如真补出来的");
                    }
                    else
                    {
                        // 兜底(16x 上限仍不够):按实际输出 + 明确提示,不重复帧凑数
                        // 【死代码说明 · 2026-09-16 审计第 3 条】本支在当前闸门下**当前不可达**(理由见上),
                        // 保留作为"引擎能力不足"的真实兜底。
                        var achievable = (v4Interp ? inFps : effectiveFps) * interpScale;
                        progress?.Report((94,
                            $"⚠ 指定输出 {targetFps.Value:0.##} fps 达不到(补帧后实际只有 {achievable:0.##} fps),已按 {achievable:0.##} fps 输出。"));
                        AppLogger.Info($"目标帧率达不到:指定 {targetFps.Value:0.##} fps,实际只能 {achievable:0.##} fps,按实际输出");
                    }
                }
            }
            // 【2026-09-21】原先这里还有 preChain / postChain 两个"滤镜链拼串"变量 + minterpolate 的子帧数
            // (motionFrames/baseFpsStr/subFpsStr)。它们唯一的消费者是已删除的那张运动模糊 filter_complex 图;
            // 正常的 -vf 链一直直接用 preParts/postParts 列表(见下方 allParts)⇒ 一并删除,不留死变量。
            // ===== 时长保护(关键):不管补出多少帧,输出时长恒=原处理时长,速度恒对、不吞时间、不坏尾段 =====
            // 原理:用 setpts 把"实际输出帧"均匀铺满在"原时长"上(帧率=帧数/原时长,时长=原时长)。
            // 若直接用固定 -framerate=原帧率×倍率,一旦 RIFE 补出帧数 != 原帧数×倍率,时长就漂移
            // (速度对不上、尾部被截)——这正是"吞时间/少一段"的根源。
            string? vfrSetpts = null;
            double muxDur = 0;
            // 帧数精确对齐(v4 + 均匀输出):补/裁到"(真实原帧数-1)×倍率+1"——
            // 这样 时长 = (帧数-1) ÷ (原帧率×倍率) = 真实时长,末帧 PTS = 原末帧,不需要 -t 裁尾(裁尾会吞最后一帧)。
            // 【指定帧率也要走"帧数对齐"】原先这里硬写着 `targetFps == null`:用户一旦指定帧率,
            // 帧数就没人按"时长=源容器"对齐,只能靠合帧的尾部裁剪兜 —— 那正是"尾部内容被删"的来源之一。
            if (v4Interp && !vfrPassthrough && !tempoResample && trueFrames > 0 && !flattenActive)
            {
                long expBase = fpsMode == 1 ? frameCount
                    : (inFpsOverride is > 0 && probedFps > 0 && Math.Abs(inFpsOverride.Value - probedFps) / probedFps > 0.01
                        ? frameCount : trueFrames);
                // 【预期帧数必须与补帧产出同源】指定帧率下引擎按分数步长产出(真机 2.507),
                // 而 interpScale 仍是整数 3 ⇒ 若按 (帧数-1)×3+1 当预期(907),就会先"末帧补足 149 帧"
                // 再"裁尾 148 帧":白白造 149 个冻结帧又删掉。用同一个分数步长算预期即可(758)。
                long expected = stageFractionalMult > 1.0 && frameCount > 1
                    ? (long)Math.Round((double)(frameCount - 1) * stageFractionalMult) + 1
                    : (long)Math.Round((double)(expBase - 1) * interpScale) + 1;
                var seq = Directory.EnumerateFiles(framesFinal, "*.jpg")
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
                if (seq.Length > 0 && seq.Length != expected)
                {
                    if (seq.Length < expected)
                    {
                        var last = seq[seq.Length - 1];
                        for (long i = seq.Length; i < expected; i++)
                            File.Copy(last, Path.Combine(framesFinal, $"frame_{i + 1:D6}.jpg"), true);
                        AppLogger.Info($"帧数对齐:输出 {seq.Length} 帧 < 预期 {expected},末帧补足 {expected - seq.Length} 帧(防吞尾)");
                    }
                    else
                    {
                        for (long i = expected; i < seq.Length; i++)
                        {
                            try { File.Delete(Path.Combine(framesFinal, $"frame_{i + 1:D6}.jpg")); } catch { }
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
            double tailGapSec = 0;
            // 【任务 S1】flattenActive 时输出是"均匀时间轴 + 目标帧数"两条都定死了:既不能再被"帧数对齐"改帧数
            // (上面已排除),也要走这段"尾帧容积"把"帧数 ÷ 源容器时长"的差折进标称帧率 —— 否则总时长会短多半帧。
            // 条件里的 `|| flattenActive` 只对"VFR 直通 + 填平"这一种组合放宽,不是填平时逐字不变。
            if (frameInterp && outFps > 0.01 && (!vfrPassthrough || flattenActive) && frameDurs == null)
            {
                var seqA = Directory.EnumerateFiles(framesFinal, "*.jpg")
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
                if (seqA.Count > 0)
                {
                    double curDur = (seqA.Count - 1) / outFps;
                    double gap = muxDur - curDur;
                    if (gap > 0.005)
                    {
                        // finalDurs 的唯一下游消费者是 BuildVfrSetptsExpr,而它只在
                        // (targetFps==null && preserveRhythm) 分支里跑。均匀输出路径上建表 = 白建:
                        // 日志照打"末帧延长 XXms",成片一毫秒都没变(实测 111 帧源 → 441 帧 @119.47
                        // = 3.691s,源容器 3.761s → 尾部 70ms 有声无画)。均匀时间轴上"只延长末帧"
                        // 做不到,唯一能落地的手段是把这段差折进标称帧率(见下方"帧率保险")。
                        if (preserveRhythm)
                        {
                            finalDurs = new System.Collections.Generic.List<double>();
                            for (int i = 0; i < seqA.Count; i++) finalDurs.Add(1.0 / outFps);
                            finalDurs[^1] += gap;
                            AppLogger.Info($"尾帧容积:末帧延长 {gap * 1000:0}ms(帧数不变,总长 {muxDur:0.###}s)");
                        }
                        else
                        {
                            // 【2026-09-17 修】小缺口(≤3 帧)优先"补末帧定格",不再去改标称帧率:
                            // 改帧率会把输出变成 59.67 / 47.037 这类非标准帧率 —— 60Hz 屏上每帧 1.27 个刷新周期,
                            // 于是全程轻微顿挫、开头那一下最明显(用户实测预览:23.976×2 本该 47.952,成片却是 47.037,
                            // 还比真实慢 1.9%)。补 1~3 帧末帧定格在几秒素材上完全看不出(播放器本来也会在末帧停留),
                            // 换来标准帧率 + 整片速度正确;只有缺口大到要补 4 帧以上(观感会明显"卡住")才回退老办法。
                            int add = (int)Math.Round(muxDur * outFps) - seqA.Count;
                            if (add >= 1 && add <= 3)
                            {
                                var lastF = seqA[^1];
                                for (int i = 0; i < add; i++)
                                {
                                    try { File.Copy(lastF, Path.Combine(framesFinal, $"frame_{seqA.Count + i + 1:D6}.jpg"), true); }
                                    catch { }
                                }
                                AppLogger.Info($"尾帧容积:补 {add} 帧末帧定格({seqA.Count} → {seqA.Count + add} 帧,"
                                    + $"标称帧率保持 {outFps:0.###}fps,总长≈{muxDur:0.###}s)");
                            }
                            else
                            {
                                tailGapSec = gap;
                                AppLogger.Info($"尾帧容积:成片比源容器短 {gap * 1000:0}ms(缺口 {add} 帧太大,折进标称帧率修正)");
                            }
                        }
                    }
                    else if (gap < -0.005 && !flattenActive)
                    {
                        // 多出(超容器):裁多余帧(保留尾帧)
                        // 【任务 S1】flattenActive 时**不裁**:输出帧数必须恰好等于判定给出的目标帧数(硬要求);
                        // 这条分支只在"帧数多于容器时长"时才可能进,而填平时 gap 恒为正(见上方尾帧容积的推导)。
                        int dropN = (int)Math.Round(-gap * outFps);
                        for (int i = seqA.Count - 1; i >= Math.Max(1, seqA.Count - dropN); i--)
                        {
                            try { File.Delete(seqA[i]); } catch { }
                            if (i == 0) break;
                        }
                        AppLogger.Info($"帧数对齐(时长=源):裁尾 {dropN} 帧");
                    }
                }
                // 注:这条"补帧诊断"曾经只挂在本 if 里(条件含 !vfrPassthrough && frameDurs == null),
                // 于是【用户一开 VFR 它就永远不打】—— 正是它灭掉了"时长表为空"的唯一线索。
                // 现在改成无条件打(见下方紧接的一段),这里不再重复。
            }
            // 只有"内容时间轴不均匀(真 VFR 素材 或 去重删过帧)+ 需要保护"才用 setpts 保留原始节奏(输出 VFR);
            // 普通 CFR 素材(未去重)一律均匀输出(原×倍率)——避免 setpts 精度问题引入抖动。
            // 【C2 · 2026-09-13】"回退均匀表"不许再静默:旧代码在表为空时就地造一张均匀表,日志照打
            // 「时长保护(VFR)」、界面照显示「可变帧率时间轴」—— 用户拿着"变速 + 音画不同步"的成片,
            // 日志里却一行异常都没有(真机事故的第二个成因)。现在回退必须打 warn,且文案改口径。
            bool fellBackToUniform = false;
            string fallbackWhy = "";
            if (preserveRhythm)
            {
                int finalFileCount = Directory.EnumerateFiles(framesFinal, "*.jpg").Count();
                if (finalDurs == null || finalDurs.Count == 0)
                {
                    fellBackToUniform = true;
                    fallbackWhy = $"没有可用的帧时长表(finalDurs={(finalDurs == null ? "null" : "0 项")},"
                        + $"源表 frameDurs={(frameDurs != null ? frameDurs.Count + " 项" : "null")},去重后帧数 {frameCount})";
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
                    // 段数 >400(每个"时长不同的连续段"算一段)或表里有非法值 → 退化成均匀时间轴。
                    // 【真机可触发】帧时长逐帧都不同的素材(抖动型 VFR)段数会超过 400 —— 这条路同样必须吵。
                    fellBackToUniform = true;
                    fallbackWhy = $"帧时长表无法生成 setpts 表达式(段数 >400 或含非法值,{finalDurs.Count} 项)";
                    finalDurs = new System.Collections.Generic.List<double>();
                    for (int i = 0; i < finalFileCount; i++) finalDurs.Add(muxDur / Math.Max(1, finalFileCount));
                    vfrSetpts = BuildVfrSetptsExpr(finalDurs);
                }
                bool useVfrTimeline = !fellBackToUniform
                    && AlhPro.Core.VideoPipeline.UsesVfrTimeline(preserveRhythm, finalDurs.Count);
                if (useVfrTimeline)
                {
                    AppLogger.Info($"时长保护(VFR): 帧={finalFileCount}, muxDur={muxDur:0.###}, 总时长={finalDurs.Sum():0.###}, vfrSetpts={(vfrSetpts != null ? "有" : "无")}");
                    if (vfrSetpts != null)
                        progress?.Report((96, $"混合编码({outFps.ToString("0.##", inv)} fps,可变帧率时间轴 {muxDur:0.###}s)..."));
                }
                else
                {
                    // 【口径必须如实】回退后不许再说「时长保护(VFR)」「可变帧率时间轴」——那是骗人。
                    // 源为 VFR 时把后果写清楚(变速 + 音画不同步),并给出用户能做的动作。
                    AppLogger.Warn($"⚠ 可变帧率时间轴【回退为均匀时间轴】:{fallbackWhy};"
                        + $"preserveRhythm={preserveRhythm},VFR 素材={vfrPassthrough},去重={dedup},"
                        + $"目标帧率={(targetFps?.ToString("0.##", inv) ?? "未指定")}。"
                        + (vfrPassthrough
                            ? "源是可变帧率(VFR)素材:回退成均匀时间轴会让成片变速、并随时间与音频逐渐错位(音画不同步);"
                              + "想保留源时间轴请检查源文件的帧时间戳是否过于零碎(逐帧都不同会让段数超过 400 上限)。"
                            : "源按固定帧率处理,成片时间轴均匀(影响仅限于「该保留的节奏没有保留」这一种)。"));
                    progress?.Report((96, $"⚠ 可变帧率时间轴不可用(时长表缺失或段数超限),已按均匀时间轴编码 {muxDur:0.###}s"
                        + (vfrPassthrough ? " —— 源为 VFR,成片可能变速/音画不同步" : "")));
                }
            }
            else
            {
                AppLogger.Info($"时长保护(均匀): muxDur={muxDur:0.###}, baseFps={baseFps:0.##}");
                // 【2026-09-21 这块告知已随功能删除】原先这里还有一条"唯一残留的丢节奏组合":保节奏为真 +
                // 指定了帧率 ⇒ 只剩「果冻修复·运动模糊」这一个原因(那条滤镜链自带 CFR 重采样)。整块果冻修复
                // 已按用户要求删除,而上面那句判据现在**就是** preserveRhythm 本身 ⇒ 进到本分支时 preserveRhythm
                // 必为假,那个 if 永远不可达。留着它会让人以为"还有一条能丢节奏的路"—— 所以连条件一起删掉,
                // 不留"永远不打的死分支"(本仓库对死分支的规矩:要么给出可达理由,要么删)。
            }
            // ===== 补帧/时长表诊断:【无条件打】(2026-09-13) =====
            // 旧代码把它挂在 `frameInterp && !vfrPassthrough && frameDurs == null` 的分支里 ——
            // 恰恰是"用户开了 VFR"(最需要看清时长表状态的场景)时它永远不打,唯一线索就此消失。
            // 现在只要补帧真的跑了就打,并把"源表/最终表/节奏判定"的原始状态全带上。
            if (frameInterp && outFps > 0.01)
            {
                int finalNDiag = Directory.EnumerateFiles(framesFinal, "*.jpg").Count();
                // 【任务 N】把"帧数守恒目标"一并打出来:mult = 补帧真实倍率(round(interpScale×frameScale)),
                // 目标 = (源帧数-1)×mult+1(真机那次 = (855-1)×2+1 = 1709)。
                // 【指定帧率】必须用分数步长而不是 round(interpScale×frameScale):
                // 后者印 1552、真正的产出目标是 1298,排查时会按错的数字找"少了的帧"。
                double multDiag = multTargetFpsOverride > 1.0
                    ? multTargetFpsOverride
                    : AlhPro.Core.VideoPipeline.InterpMultiplier(interpScale, frameScale);
                long expectDiag = AlhPro.Core.VideoPipeline.InterpOutputFrameCount(frameCount, (int)Math.Round(multDiag));
                AppLogger.Info($"补帧诊断: 去重后 {frameCount} 帧,输出 {finalNDiag} 帧(每源帧步长 {multDiag:0.###} 帧,帧数守恒目标 {expectDiag} 帧),interpScale={interpScale},"
                    + $"finalDurs={(finalDurs != null ? finalDurs.Count : -1)},frameDurs={(frameDurs != null ? frameDurs.Count : -1)},"
                    + $"preserveRhythm={preserveRhythm},VFR素材={vfrPassthrough},去重={dedup},"
                    + $"时间轴={(vfrSetpts != null ? "VFR(setpts)" : "均匀")}"
                    // 【任务 S1】填平时"帧数守恒目标"不是 (源帧数-1)×倍率+1,而是 round(真实时长×目标帧率):
                    // 必须把真正的目标写出来,否则上面那个数字会误导排查(它只是老路径的口径)。
                    + (flattenActive
                        ? $";【平滑时间轴】目标帧数 {flatPlan.TargetFrames}(@ {flatPlan.TargetFps:0.##} fps,真实时长 {flatPlan.TotalSeconds:0.###}s),"
                          + $"场景硬切 {flattenCuts} 处、切点强制拷贝 {flattenForcedCopies} 槽"
                        : "")
                    // 【指定帧率口径】上面那个"帧数守恒目标"按 (源帧数-1)×步长+1 算,在指定帧率下就是**真正生效**
                    // 的目标;这里再把"按目标帧率 × 源容器时长应得多少帧"写出来(与合帧的帧数校验同口径,一眼对得上)。
                    + (targetFpsDecl is > 0 && targetFpsWantFrames > 0
                        ? $";【指定帧率口径】按 {targetFpsDecl.Value.ToString("0.##", inv)} fps 应得 {targetFpsWantFrames} 帧"
                          + $"({frameCount} 帧源 ⇒ 每源帧 {multDiag:0.###} 帧,非整数倍率 {interpScale}x)"
                        : ""));
                // 【任务 N · 帧数守恒自检】只在"不会被别的机制调整帧数"的路径上判:
                //   · VFR 路径【不做】合帧前的"帧数对齐"(那会破坏时间轴)→ 少了就是被丢了(任务 N 的丢帧)、
                //     多了就是末帧冻结副本没去掉,两种情况都让"2x 不再是 2x",必须吵;
                //   · 去重(密度还原)/指定输出帧率/韵律重采样那几条路径帧数本就不等于本式,不参与判定。
                bool countCheckApplies = vfrPassthrough && vfrSetpts != null && tempoSrcIdx == null && frameScale <= 1.001;
                if (countCheckApplies && finalNDiag != expectDiag)
                    AppLogger.Warn($"⚠ 帧数守恒自检:输出 {finalNDiag} 帧 ≠ 目标 {expectDiag} 帧(({frameCount}-1)×{multDiag:0.###}+1);"
                        + "补帧倍率会因此对不上(2x 不等于 2x)、或成片尾部少了内容");
            }
            string vfArg, videoMap;
            string vfChainBody = "";   // 有独立 -vf 链时才填(供"编码阶段拆分"抽样实测滤镜链成本)
            // 【2026-09-21 果冻修复整块删除】原先这里是一个二选一:`if (postMotionBlur >= 1)` 走
            // minterpolate 沿运动方向做真实模糊 + tmix 混合子帧 + 运动掩码局部应用(filter_complex),
            // 否则走下面这条普通 -vf 链。用户当晚要求"果冻修复那个删掉" ⇒ 那条分支删除,合帧阶段现在
            // **只有一条**滤镜链,不再有按档位切换的第二张复杂图(它带的 CFR 重采样副作用也一并消失)。
            var chainParts = new System.Collections.Generic.List<string>(preParts);
            chainParts.AddRange(postParts);
            // 链尾强制 yuv420p:部分滤镜输出 gbrp 之类不转格式会让编码器输出黑白;
            // setpts 改时间轴放在滤镜链最后(后处理=时间维度滤镜需要先于重映射的正确 PTS)
            vfArg = chainParts.Count > 0
                ? $" -vf \"{string.Join(",", chainParts)},format=yuv420p{(vfrSetpts != null ? "," + vfrSetpts : "")}\""
                : (vfrSetpts != null ? $" -vf \"format=yuv420p,{vfrSetpts}\"" : "");
            // 供"编码阶段拆分"抽样实测滤镜链成本用(与上面 vfArg 同一份链,去掉 -vf 包装)
            vfChainBody = chainParts.Count > 0
                ? string.Join(",", chainParts) + ",format=yuv420p" + (vfrSetpts != null ? "," + vfrSetpts : "")
                : (vfrSetpts != null ? "format=yuv420p," + vfrSetpts : "");
            videoMap = "-map 0:v:0";
            // 卡顿预防提示:内容帧率低(如 12fps)时,低倍率输出仍会卡,建议提高倍率。
            // v4(原×倍率)输出较足,不提示;v2(内容×倍率)偏低时提示。
            if (frameInterp && !v4Interp && effectiveFps * interpScale < 30)
            {
                var suggest = Math.Ceiling(30.0 / Math.Max(1, effectiveFps));
                if (suggest != interpScale && suggest <= 8)
                    progress?.Report((96, $"内容帧率仅 {effectiveFps:0.##} fps,当前输出 {outFps:0.##} fps 可能仍卡,建议补帧 {suggest:0}x"));
            }
            // 【任务 U】阶段③统计:自定义缩放 / 对齐 / 抗锯齿 等"合帧前收尾"
            UMark("后处理/缩放/合帧准备");
            USampleTemp();
            progress?.Report((96, $"ffmpeg 合成视频({outFps.ToString("0.##", inv)} fps)..."));
            var framePattern = Path.Combine(framesFinal, "frame_%06d.jpg");
            // 6 位小数:83.376 这类非整数帧率用 0.## 会被量化成 83.38,长视频会累积微小漂移(10 分钟约 9ms)
            // 【帧率保险】编码标称帧率 = 实际帧数 ÷ 源时长(而非公式估 baseFps):公式在某分支
            // (内容帧率模式+v4 模型等)可能算错导致输出标称=内容帧率(用户实测:补帧2x输出标称50fps
            // 而非 100);用真实帧数÷时长标称后,播放器按真实帧率播,不再丢插值帧。仅日志提示偏差。
            var frBase = baseFps;
            try
            {
                int fcFinal = Directory.EnumerateFiles(framesFinal, "*.jpg").Count();
                if (fcFinal > 1 && muxDur > 0.01)
                {
                    // 标称帧率 = 实际帧数 ÷ 源容器时长。实测 image2 按 pts=i/R 铺帧,N 帧在容器里
                    // 占 N/R 秒(111 帧 @30fps → duration 3.700s,不是 (N-1)/R 的 3.667s),
                    // 所以用 N/muxDur 才能让成片时长与源容器分毫不差。
                    double realFps = fcFinal / muxDur;
                    // tailGapSec>0 = 上面已判定"成片比源容器短,且均匀时间轴没法只延长末帧"。
                    // 这种案例的偏差可能只有 2%(实测 2.07%),被原来的 3% 门槛整个放过 →
                    // 尾部留下几十毫秒"有声无画"。这里必须改标称帧率把差补回来。
                    // 不会引入慢放风险:新覆盖的这一段按定义偏差 <3%(改完速度差不到 3%,看不出来);
                    // 偏差 >3% 的情况(muxDur 明显失真)本来就走这条路,行为没变。
                    if (tailGapSec > 0 || Math.Abs(realFps - baseFps) / Math.Max(0.01, baseFps) > 0.03)
                    {
                        AppLogger.Info($"⚠ 输出帧率标称修正:{baseFps:0.##} → {realFps:0.##} fps(实际 {fcFinal} 帧 / {muxDur:0.###}s;"
                            + (tailGapSec > 0 ? $"补回尾部 {tailGapSec * 1000:0}ms" : "内容帧率模式偏差自愈") + ")");
                        frBase = realFps;
                    }
                }
            }
            catch { }
            var fr = frBase.ToString("0.######", inv);
            // 时长表输出(VFR):setpts 已在滤镜链构造处接入(精确重映射时间轴,精度=输出时基)。
            // 注:曾用 concat demuxer + duration,实测其内部 image2 时基固定 25fps,0.0333/0.1 被
            // 量化成 0.04/0.08/0.12(30fps 素材半段快 20%),故改为 setpts。
            // 【任务 N · 2026-09-13 关键修复】setpts 的**量化格子 = image2 输入的时基 = 1/(-framerate)**:
            // 标称帧率是"帧数÷时长"(真机那次 1710/29.6667 = 57.64 → 一格 0.017349s),而这条 VFR 时间轴里
            // 最短的一格只有 0.016686s —— **比一格还短** → 相邻两帧被折算到同一个时基整数格(重复 PTS)→
            // 被编码/封装丢掉:实测"日志说 1710 帧、文件里只有 1646 帧"(丢 64),成片里那 64 个"双倍长间隔"
            // 就是丢帧留下的空档 → 2x 补帧实际只有 1.925x,软件自己的输出校验也告警(55.52 vs 预期 57.64)。
            // 修法:VFR 时把输入时基细化到"最短帧时长的一半以内"(Core.VfrInputFramerate),再用
            // Core.CountTimestampCollisions 复算撞格数;若仍有撞格,再细化到 1ms(1000fps)。
            // CFR 分支一字未改:此时 frInput == fr。
            string frInput = fr;
            // 【2026-09-21】原先这里还有一个排除项 `&& postMotionBlur < 1`:运动模糊那条链自带时间维度
            // 重采样(minterpolate=fps=… → tmix → fps=…),把输入时基细化会让它以为素材只有一半长、产出帧数减半。
            // 整块果冻修复已按用户要求删除 ⇒ 该组合不存在,排除项随之取消,条件只剩"确实有 VFR 时长表"。
            // (顺带说明:那条链自己承认的"该组合仍可能撞格丢帧"这个已知空洞也一并消失了 —— 不再有那条链。)
            if (vfrSetpts != null && finalDurs != null && finalDurs.Count > 0)
            {
                double c0 = AlhPro.Core.VideoPipeline.CountTimestampCollisions(finalDurs, frBase);
                double need = AlhPro.Core.VideoPipeline.VfrInputFramerate(finalDurs, frBase);
                if (AlhPro.Core.VideoPipeline.CountTimestampCollisions(finalDurs, need) > 0) need = 1000;
                frInput = need.ToString("0.######", inv);
                int c1 = AlhPro.Core.VideoPipeline.CountTimestampCollisions(finalDurs, need);
                double minDur = finalDurs.Where(d => d > 0 && double.IsFinite(d)).DefaultIfEmpty(0).Min();
                AppLogger.Info($"VFR 时基核算:{finalDurs.Count} 帧时长表,最短 {minDur * 1000:0.###}ms / 标称一格 {1000.0 / Math.Max(0.01, frBase):0.###}ms"
                    + $" → 标称帧率下预计撞格(相邻帧同一时间戳=会被丢帧){c0:0} 帧;输入时基改用 {frInput} fps → 撞格 {c1:0} 帧"
                    + (c1 == 0 ? "(0 = 不再丢帧)" : " ⚠ 仍有撞格:时长表可能异常"));
            }
            var muxInput = $"-framerate {frInput} -i \"{framePattern}\"";
            await EnsureHwProbeAsync(ffmpeg, ct);
            var encoder = PickVideoEncoder(gpuId, codecPref);
            // 【编码最优解·解码侧】硬编生效时,瓶颈会从编码器转到"软件解码 JPG 序列"(4K 实测 ≈39.6 fps)。
            // ⚠【2026-09-13 真机基准:**旧性能数字已无法复现,勿再引用**】这里原来写「NVDEC 实测 4595 fps、
            // 端到端 +52%」—— 本机同条件重测是 **cuvid 192.68 fps vs 软解 193.99 fps(硬解无收益)**,
            // 4595/+52% 那组数字在本机复现不出来(见 HwJpegDecode 的说明)。所以**不再据此主张硬解收益**:
            // 启用条件仍是"硬编生效 + 探测实测通过",纯属"能过就用"(结果不受影响,只影响速度口径)。
            // 只有【实测通过(Usable)】才加硬解:其余三态(未探/确定不支持/探测超时)一律走软解 ——
            // 与改动前的 bool=false 行为完全一致(见 HwJpegDecode 的三态说明)。
            if (_hwJpegDecode == HwJpegDecode.Usable && encoder.Contains("nvenc", StringComparison.OrdinalIgnoreCase))
            {
                muxInput = "-c:v mjpeg_cuvid " + muxInput;
                AppLogger.Info("合帧解码:改用 NVDEC 硬件解码 JPG 序列(解码不再是瓶颈)");
            }
            progress?.Report((96, $"压缩编码器:{LastVideoEncoderInfo}"));
            // 自定义码率模式:用户指定 Mbps(0 = 用质量档 CRF);码率也随回退保持(CPU 软编同样适用)
            double bitrateKbps = customBitrateMbps > 0 ? customBitrateMbps * 1000 : 0;
            var encArgs = EncoderArgs(encoder, quality, bitrateKbps);
            // 【预览片段也上短 GOP,专治"拖进度条不跟手"】
            // 一次定位必须从上一个大关键帧解起:默认 GOP 250 帧在 4K60 上约等于 0.7 秒的解码量
            // (真机实测:拖动时排队的定位要 600~900ms 才落地 → 手感就是"不跟手")。预览片段只用来当场看,
            // 牺牲一点文件体积把 GOP 压到 12,拖动立刻跟手。**用户成片不受影响**(成片输出不落在预览目录里)。
            // 判定方式:输出路径落在 %TEMP%\ALHPro\preview 下 = 这就是预览片段(VideoView 的预览出口就在这里)。
            // 为什么用路径判定而不是加参数:这里是流水线很深的内部函数,一路透传要改十几处签名,风险更大。
            bool isPreviewClip = false;
            try
            {
                isPreviewClip = outputVideo.Contains(System.IO.Path.Combine("ALHPro", "preview"),
                                                     StringComparison.OrdinalIgnoreCase);
            }
            catch { }
            if (isPreviewClip) encArgs += " -g 12";
            // MP4 加 faststart 便于流式播放;MKV 不需要。
            // 【大成片保护】faststart 会把整个 mdat 挪一遍 = 一次全文件读+写;NVMe 上几秒,但机械盘/快满的盘
            // 在用户眼里的"编码期间"可能到分钟级。预估成片超阈值(2GB)时跳过 faststart(流式首帧加载的便利
            // < 大成片整文件重写的时间成本)。预估:码率模式用 码率×时长;CRF 模式用源文件大小做保守上界代理。
            var fastFlag = "";
            if (outputVideo.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                double estMB = 0;
                if (bitrateKbps > 0) estMB = muxDur * bitrateKbps / 8.0 / 1024.0;
                else { try { estMB = new FileInfo(inputVideo).Length / 1024.0 / 1024.0; } catch { }
                       estMB = Math.Max(estMB, 0); }   // 读不到源大小也没有码率 → 按 0 处理(加 faststart,小文件)
                estMB = Math.Max(estMB, 0);
                if (estMB < 2048)
                    fastFlag = " -movflags +faststart";
                else
                    AppLogger.Info($"成片预计 {estMB:0} MB(≥2GB),跳过 -movflags+faststart(避免整文件重写耗时)");
            }
            // 音频:MP4 容器不支持 vorbis/opus/flac 等编码 → 自动转 aac(仅当源码不是可复制的编码);MKV 原样拷贝,
            // 保留无损/环绕音轨(flac/dts/opus 等不再被压成有损 aac)。静音=不映射音轨(无音轨输出)。
            var audioArgs = "-c:a copy";
            if (!mute && outputVideo.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                var acodec = await ProbeAudioCodec(inputVideo);
                // MP4 容器:仅 aac/mp3/ac3/eac3 可原样复制;其它(含探不出/未知)一律转 aac 保兼容
                if (acodec is not ("aac" or "mp3" or "ac3" or "eac3"))
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
            // 音画同步:音频裁剪到画面时长(atrim)+ 把起始时间归零用 PTS-STARTPTS(保留源音频原生时间/间隔,
            // 比 N/SR/TB 重排更贴合视频时间轴,减少"音画不同步")。
            var audioPart = mute ? "" : $" -map 1:a:0? {audioArgs}";
            if (!mute && muxDur > 0.01)
                audioPart += $" -t \"{muxDur.ToString("0.######", inv)}\"";
            // 【2026-09-19 撤回】曾试过加 -filter_threads 提速,实测**中性**(0.084 → 0.084 秒/帧:
            // ffmpeg 的 JPEG 解码本身已多线程),用户要求"不要乱改" ⇒ 已删除该改动,恢复原样 ✔
            // 【2026-09-19 审计落地 · 归档能力】透传源素材的**全局元数据与章节**。
            // 现状:只 map 视频+第一条音轨 ⇒ 标题/艺术家/日期/注释/章节全丢 ✗(归档与留档用途的硬伤)
            // ⚠ 第 0 个输入是 JPEG 序列(无元数据),真正的源在第【1】个输入上 ⇒ 必须写 1 ✔
            // 风险极低:MP4/MKV 都支持;源没有元数据/章节时是空操作 ✔
            var muxArgs = $"{videoMap} -map_metadata 1 -map_chapters 1 {audioPart} {encArgs} {vfArg}{fastFlag} \"{outTmp}\"";
            var muxBase = $"-y {muxInput} {trimArgs} -i \"{inputVideo}\" ";
            // 编码阶段整体进度 96→100 随 ffmpeg 编码帧数推进(否则卡 96%,结尾预计时间虚高失真)
            int encTotal = Math.Max(1, Directory.EnumerateFiles(framesFinal, "*.jpg").Count());
            if (pauseWait != null) await pauseWait();   // 暂停:编码开始前停(已生成的帧不浪费)
            // 探测期已经把"这台机器上这个编码器要什么参数才能编"试出来了(亚秒级、1 帧);
            // 这里直接照配方编,不再拿整片去试错。没探到配方(探测被取消/跳过)才退回主 ffmpeg + 原参数。
            var recipe = GetHwRecipe(encoder);
            string encFfmpeg = recipe?.Ffmpeg ?? ffmpeg;
            string encMuxArgs = recipe?.NoPreset == true ? StripPreset(muxArgs) : muxArgs;
            // 编码实测计时:诊断包用于分辨"CPU 软编慢"还是"硬编用户慢在解 JPG"(见编码性能实测分析)
            double uEncSec = 0, uOutDur = 0, uOutFps = 0;   // 【任务 U】结算用:编码耗时 / 成片时长 / 成片帧率
            string uBlackSeg = "";                          // 【任务 U 补充】输出端黑场自检结果(空 = 无黑场)
            var encSw = System.Diagnostics.Stopwatch.StartNew();
            string encUsed = LastVideoEncoderInfo;
            try
            {
                try
                {
                    // 已知会失败的硬件编码器直接跳过,走 CPU(避免每次先白跑一次)
                    if (BrokenHwEncoders.Contains(encoder))
                        throw new InvalidOperationException("hw-encoder-known-broken");
                    // 【编码阶段必须能报进度】ffmpeg 的 stats 行(打给 stderr)在输出被重定向时不保证持续出现,
                    // 于是"编码"这一步此前只有一条静止的「合成视频…」——用户实测"一直显示合成视频",不知道还要多久、
                    // 也判断不出是死机还是在跑(实测那段可能是几十分钟到数小时)。
                    // 改成 -progress pipe:1:ffmpeg 会把 frame=/fps=/out_time… 等【机器可读】行写到 stdout,
                    // 而 RunAsync 的 FrameRegex 正在解析 frame= → "编码 第 N 帧 / 共 M 帧 + 预计还剩" 就稳定刷新了;
                    // -nostats 顺手去掉 stderr 上重复的统计行。
                    await RunAsync(encFfmpeg, "-nostats -progress pipe:1 " + muxBase + encMuxArgs, progress, ct, "编码", encTotal);
                    // 硬件编码可能留下 0 字节/损坏文件却退出 0,这里校验;无效则触发回退
                    if (!await ValidateVideoFileAsync(outTmp, 1))
                        throw new InvalidOperationException("硬件编码输出文件无效");
                }
                catch (OperationCanceledException) { throw; }   // 【必须排在下面那条之前】取消不是"硬编坏了"
                catch (Exception ex) when (encoder != "libx264" && encoder != "libx265")
                {
                    // 【不再整片重试 GPU】"去掉 -preset"和"换备用 ffmpeg"这两个问题探测期已回答过,
                    // 到这里还失败说明这台机器就是编不了(驱动过旧/硬件不在/输出损坏)。整片长度的重试
                    // = 用户白等一整遍编码时间,而答案在 1 帧探测里就能拿到。直接标记坏 + 回退 CPU。
                    // 【踩过的坑】此前这里没有上面那条 catch:用户点一次「停止」→ 抛取消异常 → 命中本 catch
                    // → BrokenHwEncoders.Add(encoder) —— 从此本次运行所有任务都被判"硬编不可用"、静默走 CPU 软编
                    // (慢数倍),必须重启软件才恢复。日志实证:2026-09-11 21:59 "原因:The operation was canceled."。
                    BrokenHwEncoders.Add(encoder);
                    var cpuEnc = encoder.StartsWith("hevc", StringComparison.OrdinalIgnoreCase) ? "libx265" : "libx264";
                    // 驱动过旧单列:它是最常见且用户能自己解决的一种,提示要说清"去更新驱动"
                    bool driverOld = IsNvencDriverTooOld(ex.Message);
                    if (driverOld)
                        AppLogger.Warn($"⚠ 硬件编码({encoder})不可用(显卡驱动过旧:需更新 NVIDIA 驱动到 610+,当前驱动 nvenc 版本过低)——改用轻量 CPU 编码({cpuEnc})");
                    else
                        AppLogger.Warn($"⚠ 硬件编码({encoder})不可用(原因:{ex.Message.Split('\n')[0]})——改用轻量 CPU 编码({cpuEnc})");
                    progress?.Report((96, $"⚠ 硬件编码({encoder})不可用{(driverOld ? "(显卡驱动过旧)" : "")},改用轻量 CPU 编码({cpuEnc})..."));
                    LastVideoEncoderInfo = $"{cpuEnc} (CPU 软编,硬件编码回退)";
                    await RunAsync(ffmpeg,
                        "-nostats -progress pipe:1 " + muxBase + $"{videoMap}{audioPart} {EncoderArgs(cpuEnc, quality, bitrateKbps)} {vfArg}{fastFlag} \"{outTmp}\"",
                        progress, ct, "编码", encTotal);
                }
                if (!await ValidateVideoFileAsync(outTmp, 1))
                    throw new InvalidOperationException("视频合成失败:输出文件无效(无法被解码)");
                // 校验通过,才以最终文件名出现在输出目录(合帧期间输出目录只有 .tmp,不会误以为完成)
                File.Move(outTmp, outputVideo, true);
                // ===== 编码实测回报(resolve "编码慢" 是 CPU 软编线程限制还是硬编解 JPG 瓶颈)=====
                encSw.Stop();
                double encSec = encSw.Elapsed.TotalSeconds;
                uEncSec = encSec;
                double encFps = encSec > 0.01 ? encTotal / encSec : 0;
                AppLogger.Info($"编码实测:编码器={LastVideoEncoderInfo},帧数={encTotal},耗时={encSec:0.##}s,实测={encFps:0.#}fps{(!encUsed.StartsWith("libx264") && !encUsed.StartsWith("libx265") ? "(硬编)" : "(CPU 软编)")}");
                // 【把"解码""滤镜""编码"三者分开报】"编码/封装"这个数里混着 ffmpeg 的后处理滤镜与 JPG 解码
                // (同一进程)。2026-09-12 就是被这一点误导过:日志显示"硬编只有 1fps",实际是滤镜链里一个 sab
                // 把整条链拖到 4.88 秒/帧(4K),编码器本身有 15~20fps。这里分别抽样实测"解码"与"解码+滤镜",
                // 相减得到纯滤镜成本,三者都写清楚(2026-09-13:原来是混在一起报的,导致用户拿含解码的数去优化滤镜)。
                if (!string.IsNullOrEmpty(vfChainBody))
                {
                    bool hwJpeg = _hwJpegDecode == HwJpegDecode.Usable && encUsed.Contains("nvenc", StringComparison.OrdinalIgnoreCase);
                    var (perFrameFull, perFrameDecode) = await SampleFilterChainCostPerFrameAsync(
                        encFfmpeg, framePattern, vfChainBody, frBase, hwJpeg, ct);
                    if (perFrameFull >= 0)
                    {
                        double decodePer = perFrameDecode >= 0 ? Math.Min(perFrameDecode, perFrameFull) : 0;
                        double filterPer = Math.Max(0, perFrameFull - decodePer);
                        double filterEst = filterPer * encTotal;
                        double decodeEst = decodePer * encTotal;
                        double other = Math.Max(0, encSec - filterEst - decodeEst);
                        AppLogger.Info($"编码阶段拆分:JPG 序列解码(抽样 {decodePer:0.###} 秒/帧{(hwJpeg ? ",NVDEC 硬解" : ",软件解码")})≈ {decodeEst:0.#} 秒"
                            + $" + 后处理滤镜(抽样 {filterPer:0.###} 秒/帧)≈ {filterEst:0.#} 秒"
                            + $" + 编码/音频/封装/校验 ≈ {other:0.#} 秒(合计 {encSec:0.#} 秒)"
                            + (filterPer > 0.5 ? " ⚠ 滤镜占了大头,瓶颈不是编码器" : ""));
                        LastPostFilterCostPerFrame = filterPer;
                        LastJpegDecodeCostPerFrame = decodePer;
                    }
                }
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
            string outWarn = "";
            try
            {
                double durOut = await ProbeDurationSeconds(outputVideo);
                double fpsOut = 30;
                if (double.TryParse(ProbeFps(outputVideo), System.Globalization.NumberStyles.Float, inv, out var fo) && fo > 0)
                    fpsOut = fo;
                uOutDur = durOut; uOutFps = fpsOut;   // 【任务 U】结算用(拿不到就让结算标"未采集")
                string warn = "";
                // 允差 = max(3%, 1 拍):尾帧保留/拍型取整可能差 1 拍,小素材上显示 5% 是正常的,不可算 bug
                double oneBeat = Math.Max(0.01, 1.0 / Math.Max(1, outFps));
                double durTol = Math.Max(0.03, oneBeat / Math.Max(0.01, muxDur));
                if (Math.Abs(durOut - muxDur) / Math.Max(0.01, muxDur) > durTol)
                    warn += $"时长 {durOut:0.###}s vs 预期 {muxDur:0.###}s(偏差 {(durOut - muxDur) / muxDur * 100:0.#}%);";
                if (Math.Abs(fpsOut - outFps) / Math.Max(0.01, outFps) > 0.03)
                    warn += $"帧率 {fpsOut:0.##}vs 预期 {outFps:0.##};";
                // 【输出端黑场自检】后处理与编码两个阶段原本【没有任何黑帧防线】(防线只覆盖超分引擎输出那一步),
                // 所以后处理滤镜产生的黑帧能一路进成片且零日志 —— 用户实际就是这样报上来的。
                // 这里在成片落盘后扫一遍,把黑场位置写进日志与任务提示,让它再也藏不住。
                // 【2026-09-22】把源片与裁剪起点也传进去:让自检能区分"源自带的黑场"与"处理链产生的黑帧"
                // (见 ScanBlackSegmentsAsync 的说明;源自带的黑场不再当缺陷报,也不再把用户叫来发日志)。
                string blackSeg = await ScanBlackSegmentsAsync(outputVideo, ct,
                    inputVideo, trimStart ?? 0).ConfigureAwait(false);
                uBlackSeg = blackSeg;   // 【任务 U 补充】结算行要用它报"有无黑帧"
                if (blackSeg.Length > 0) warn += $"成片含全黑片段({blackSeg});";
                AppLogger.Info($"输出校验:{Path.GetFileName(outputVideo)} 帧率 {fpsOut:0.##}fps,时长 {durOut:0.###}s" +
                    (warn.Length > 0 ? " ⚠ " + warn : " ✓"));
                outWarn = warn;
            }
            catch { /* 校验失败不影响完成 */ }
            // ===== 【任务 U】处理结束的"本次统计"一行(含各阶段实测耗时 / 帧数 / 临时盘 / 清理量 / 顺序判定) =====
            // 【诚实口径】每一项都写明来源;测不到的项一律写"未采集",**不填假数**:
            //   · 阶段耗时 = 独立秒表在 5 个锚点打点(准备/补帧+超分/后处理/编码/输出校验),不串用 StageElapsed 的表;
            //   · 临时盘峰值 = "初始剩余 − 观测到的最小剩余",采样点 3 处 → 是**采样下界**,不是逐秒峰值;
            //   · 顺序判定的"实际 vs 预估"对照【未采集】—— 反事实(换另一顺序再跑一遍)本次没有跑,不编数字。
            UMark("编码/封装");
            {
                double uTotal = uStageTimes.Sum(t => t.Seconds) + uWatch.Elapsed.TotalSeconds;
                string uStages = string.Join(" | ", uStageTimes.Select(t => $"{t.Name} {t.Seconds:0.#}s"));
                int uFinalFrames = 0;
                try { uFinalFrames = Directory.EnumerateFiles(framesFinal, "*.jpg").Count(); } catch { }
                string uPeak = uTempSamples >= 2 && uTempFreeFirst >= 0 && uTempFreeMin >= 0
                    ? $"约 {(uTempFreeFirst - uTempFreeMin) / (1024.0 * 1024 * 1024):0.##} GB"
                      + $"(口径:初始剩余 − 采样到的最小剩余,共 {uTempSamples} 个采样点 → 是下界)"
                    : $"未采集(采样点 {uTempSamples} 个,拿不到临时盘剩余空间)";
                // 界面短句用同一份数字,但不带口径解释(解释留在上面的文件行里)
                string uPeakShort = uTempSamples >= 2 && uTempFreeFirst >= 0 && uTempFreeMin >= 0
                    ? $"约 {(uTempFreeFirst - uTempFreeMin) / (1024.0 * 1024 * 1024):0.##} GB"
                    : "未采集";
                // ===== 【任务 X3】各阶段"帧数 / 耗时 / 毫秒每帧"(统一格式;用户点名的最高价值项)=====
                // 目的:用户以后能一眼判断"是素材变大还是软件变慢"。
                // 【计时口径】补帧/超分用 X3 那两只独立秒表(各自只包住该子阶段);准备/后处理/编码取 uStageTimes 同名项;
                //   "合计"= uTotal。**不动** UMark/uWatch 的既有语义(它们照旧记"补帧+超分"这一整项)。
                // 【帧数口径】准备 = 拆出的源帧数;补帧 = 最终帧数(超分保帧数、不增减;±1 帧级差由帧数对齐/尾帧容积微调);
                //   超分 = 本阶段输入帧数(xUpscaleFrames,在超分批决策处定格);后处理/编码 = 最终帧数。
                // 拿不到的写"未采集",不编数字。
                double xPrepSec = uStageTimes.FirstOrDefault(t => t.Name == "准备(拆帧+去重)").Seconds;
                double xPostSec = uStageTimes.FirstOrDefault(t => t.Name == "后处理/缩放/合帧准备").Seconds;
                double xEncStageSec = uStageTimes.FirstOrDefault(t => t.Name == "编码/封装").Seconds;
                double xInterpSec = xInterpWatch.Elapsed.TotalSeconds;
                double xUpscaleSec = xUpscaleWatch.Elapsed.TotalSeconds;
                long xInterpFrames = frameInterp && xInterpSec > 0 ? uFinalFrames : 0;
                long xUpFrames = doUpscale && xUpscaleSec > 0 ? xUpscaleFrames : 0;
                string xCostPrep = AlhPro.Core.LogShortText.StageCostShort("准备", xPrepSec, origCountEst);
                // 【区分"没开"与"开了但没跑"】doUpscale/frameInterp 是用户选项(没开);选项开着却根本没走到那个阶段
                // (例如 1x 直接跳过超分)→ 写"未跑",不写成"未采集"(后者是"测不到"的意思,两回事)。
                string xCostIp = AlhPro.Core.LogShortText.StageCostOrSkipped("补帧", frameInterp, xInterpSec > 0, xInterpSec, xInterpFrames);
                string xCostUp = AlhPro.Core.LogShortText.StageCostOrSkipped("超分", doUpscale, xUpscaleSec > 0, xUpscaleSec, xUpFrames);
                string xCostPost = AlhPro.Core.LogShortText.StageCostShort("后处理", xPostSec, uFinalFrames);
                string xCostEnc = AlhPro.Core.LogShortText.StageCostShort("编码", xEncStageSec, uFinalFrames);
                AppLogger.Info("===== 本次统计 =====");
                AppLogger.Info($"· 阶段耗时:{uStages} | 收尾 {uWatch.Elapsed.TotalSeconds:0.#}s | 合计 {uTotal:0.#}s(墙钟 {taskWatch.Elapsed.TotalSeconds:0.#}s)");
                AppLogger.Info($"· 帧数台账:源 {origCountEst} 帧 → 去重后 {frameCount} 帧 → 补帧/超分后 {uFinalFrames} 帧;"
                    + $"输出标称 {outFps:0.##} fps(实际 {frBase:0.##} fps)");
                AppLogger.Info($"· 清理释放:补帧 + 超分两阶段「边用边删」共 {uReleasedFrames} 帧临时文件");
                AppLogger.Info($"· 临时盘峰值:{uPeak}");
                AppLogger.Info($"· 实际输出:{uFinalFrames} 帧 / {uOutDur:0.###} s / 平均 {uOutFps:0.##} fps"
                    + $"({Path.GetFileName(outputVideo)},{LastVideoEncoderInfo}" +
                    (uEncSec > 0 ? $",编码 {uEncSec:0.#}s" : ",编码耗时未采集") + ")");
                // 【任务 X3】各阶段每帧成本(界面同数字;文件里多写一行帧数口径,便于事后核对)
                AppLogger.Info($"· 每帧耗时:{xCostPrep} · {xCostIp} · {xCostUp} · {xCostPost} · {xCostEnc}"
                    + $"(帧数口径:准备=拆出源帧、补帧=最终帧数、超分=本阶段输入帧、后处理/编码=最终帧数);"
                    + $"补帧/超分用的是各自的独立秒表(墙钟,含引擎探测/兼容性回退的等待)");
                // 【任务 X1 · 界面与文件的最后一行对得上】上面每一行都写文件;下面四行是界面版(只留结论)。
                // 【任务 U 补充 · 为"音画滞后仍在几十 ms、无黑帧"这条要求提供**可核验的数字**】
                // 画面时长 vs 源容器时长(填平时由"帧率保险"把它钉到 muxDur,理论差 ≤ 半帧);
                // 音频流时长单独问 ffprobe —— 画面/音频各自与源容器的差就是"音画滞后"的上界。
                // 拿不到的一律写"未采集",不编数。
                {
                    double audioDur = 0;
                    if (!mute)
                    {
                        try { audioDur = await ProbeStreamDurationSecondsAsync(outputVideo, "a:0", ct).ConfigureAwait(false); }
                        catch { }
                    }
                    string avTxt = mute
                        ? "静音输出(无音轨,不适用)"
                        : audioDur > 0.01
                            ? $"音频流 {audioDur:0.###}s(与成片容器 {uOutDur:0.###}s 差 {(audioDur - uOutDur) * 1000:0}ms)"
                            : "音频流时长未采集";
                    string vidDiffTxt = uOutDur > 0.01
                        ? $"成片容器 {uOutDur:0.###}s vs 源容器 {muxDur:0.###}s(差 {(uOutDur - muxDur) * 1000:0}ms,理论 ≤ 半帧)"
                        : "成片时长未采集";
                    AppLogger.Info($"· 音画/黑帧:{vidDiffTxt};{avTxt};"
                        + $"输出端黑场自检{(uBlackSeg.Length > 0 ? "⚠ 含全黑片段 " + uBlackSeg : "✓ 无全黑片段")}");
                    progress?.Report((100, "· " + AlhPro.Core.LogShortText.ClampToChineseLimit(
                        $"音画:{(uOutDur > 0.01 ? $"画面 {uOutDur:0.###}s / 源 {muxDur:0.###}s" : "未采集")}"
                        + (mute ? ";静音(无音轨)" : audioDur > 0.01 ? $";音频 {audioDur:0.###}s" : ";音频未采集")
                        + (uBlackSeg.Length > 0 ? ";黑场 ⚠ 有" : ";黑场 ✓ 无"))));
                }
                AppLogger.Info($"· 顺序判定:{uOrderLog}" + (uOrderMeasured
                    ? $"(预估节省 {uOrderSavingsSeconds:0.#}s / {uOrderSavingsPercent:0.#}%)"
                    : "") + ";【实际 vs 预估:未采集】反事实对照要换另一顺序再跑一遍,本次没有跑,不编数字");
                // ===== 【任务 X1/X3】界面日志区:结算只放四行【结论】(带 `· ` 前缀 → 左下角日志区,不挤状态行、不动进度条)=====
                // 【为什么不在这里再报一遍顺序】顺序结论已经在【处理开始】打过一句短话 —— 同一信息不重复上界面
                //   (完整判据 + "实际 vs 预估未采集"仍在上面的文件行里,一个字都没少)。
                // 【为什么不报命中规则/面积系数/取较低者/门槛清单】那些是解释性内容,用户真机反馈"界面一坨、
                //   结论反而找不到" → 一律只写 AppLogger 文件(见 Core.RenderPolicy 生成的那几条长行)。
                progress?.Report((100, "· " + AlhPro.Core.LogShortText.ClampToChineseLimit($"耗时:{xCostPrep} · {xCostIp}")));
                progress?.Report((100, "· " + AlhPro.Core.LogShortText.ClampToChineseLimit(
                    $"耗时:{xCostUp} · {xCostPost} · {xCostEnc} · 合计 {uTotal:0.#}s")));
                progress?.Report((100, "· " + AlhPro.Core.LogShortText.ClampToChineseLimit(
                    $"输出:{uFinalFrames} 帧"
                    + (uOutDur > 0.01 ? $" / {uOutDur:0.###}s / {uOutFps:0.##} fps" : "(时长/帧率未采集)")
                    + $";临时盘峰值 {uPeakShort};清理 {uReleasedFrames} 帧临时文件")));
            }
            // 【修复】原先是先 Report("完成 ⚠ 输出校验:…") 紧接着又 Report("完成")——后者把前者覆盖掉,
            // 而 UI 只在进度 <99% 时做节流,所以那条 ⚠ 用户永远看不到(输出异常被静默吞掉)。合并成一条。
            progress?.Report((100, (outWarn.Length > 0 ? "完成 ⚠ " + outWarn : "完成") + StageElapsed()));

            // 6) 清理临时帧
            try { Directory.Delete(workDir, true); } catch { }
            return outputVideo;
        }
        finally
        {
            try { Directory.Delete(workDir, true); } catch { /* 清理失败忽略 */ }
            // 【出闸必须在删工作目录之后】顺序反了的话,删目录那一刻闸门已经放开,关窗清理会插进来抢删同一棵树。
            EngineService.ExitProcessing();
        }
    }

    /// <summary>
    /// 构建视频后处理滤镜链(参数 0-100,0=关):
    /// 锐化=unsharp 5x5 小核强量;清晰=unsharp 9x9 大核低量(局部对比度);
    /// 钝化蒙版=smartblur 负强度+阈值(经典 USM,阈值保护平坦区);
    /// 保留细节=cas 自适应对比锐化(只锐化边缘,不放大噪点);
    /// 去模糊=smartblur 大半径负强度(反锐化掩膜近似去卷积);
    /// 去模糊=smartblur 大半径负强度(反锐化掩膜近似去卷积);
    /// 边缘抗锯齿=sab 自适应模糊(只在局部对比度强处磨边)。
    /// 【已删除「去频闪」「去杂色」两项,原因有实测证据,不要加回来】
    ///  · 去频闪(deflicker):ffmpeg 的该滤镜按【时间窗口中值】归一化每帧亮度、且完全没有转场识别,
    ///    窗口跨过硬切时中值属于相邻镜头,于是把切点附近的帧整体拉到那个镜头的亮度。
    ///    本机实测(硬切:前 15 帧灰 200、后 15 帧灰 40,30fps;signalstats 取逐帧 YAVG):
    ///    切点前 3 帧 126 → 50,【被压暗 76 级】(0~255 量程的 30%),表现为"转场前两帧黑一下";
    ///    反向转场则是"亮一下"。size=3 只影响 1 帧但同样错 → 是滤镜固有行为,调参解决不了。
    ///    而它本来是给【延时摄影】用的(逐帧自动曝光不一致),正常视频同一镜头内曝光一致,本就没有这个问题
    ///    —— 属于"不适用 + 有害",故直接删除,而非花力气重做。
    ///  · 去杂色(nlmeans):与「视频降噪」是同一个滤镜的重复入口,且代码原本就在两者同时开启时
    ///    把本项从链里删掉(打日志"去杂色跳过")→ 常见配置下它根本是空操作。保留「视频降噪」即可
    ///    (它在 preParts 最前,先降噪再锐化/超分,那才是降噪该在的位置)。
    /// </summary>
    ///  【2026-09 全部重做,每一档都必须名副其实(旧版 6 档里 5 档是同一个 unsharp 的不同半径,
    ///   用户实测反馈"效果都一样、全像锐化",且"边缘抗锯齿"实测空转 —— 见 _qa\ab_waifu\POSTPROC_REPORT.md)】
    ///  现在每档用【不同机制】,并用【边缘区指标】验证过(全帧平均会把副作用稀释掉):
    ///   · 锐化(smartblur 负强度, r=1, 带阈值 3/6)——细节反锐化,阈值保护平坦区与噪点。
    ///     实测 v50:PSNR −0.35 dB、edgePSNR −0.38(旧版 unsharp 5x5 a1.5 是 −3.77 / −4.86 ✗)。
    ///   · 清晰(unsharp 13x13,低强度)——大半径"局部对比",只动中调不通吃边缘。
    ///     (ffmpeg 的 unsharp 没有阈值参数,第 6 个参数是色度强度,所以只能靠低强度控制副作用)
    ///   · 钝化蒙版(smartblur 负强度, r=2, 阈值 8)——只锐化超过阈值的明显边缘;
    ///     实测 v50 edgeSSIM 0.9582 ≥ 基底 0.9571,是唯一不伤边缘结构的档,故预设里给得最多。
    ///   · 保留细节(cas,对比度自适应)——按局部对比自适应增益,设计上不产生白边;
    ///     实测 v50 平坦区误差 2.09(基底 2.07),全档最干净。
    ///   · 边缘抗锯齿(sab 形状自适应模糊)——【旧参数 lr=1:ls=1.5 实测等于没开】(细节只降 2%),
    ///     现改为 lr 随强度 1→3、ls 2→4;实测 v50 细节 −7%、SSIM 反升 0.0123、edgePSNR 反升 0.05
    ///     = 真的在削阶梯/振铃,而不是空转。
    ///  · 【已移除:去模糊】ffmpeg 没有反卷积滤镜(实测卷积核方案 PSNR −8.6 dB ✗),
    ///    视频里那档只是"大半径锐化",名不副实 → 删除。图片页的「去模糊」是真·Richardson-Lucy
    ///    反卷积(C# 实现),那边保留。</summary>
    // 【2026-09-13 迁走】后处理滤镜链的构造已迁到 AlhPro.Core.VideoPostFilters.Build(纯逻辑 + 单测
    // 把每一档产出的 ffmpeg 滤镜字符串逐字钉住,见 AlhPro.Tests.VideoPostFilterTests)。
    // 【下面这些实测结论随实现一起保留在这里,别当废注释删】
    //  · 锐化=v50 PSNR −0.35 dB / edgePSNR −0.38(旧版 unsharp 5x5 a1.5 是 −3.77/−4.86 ✗)
    //  · 钝化蒙版=v50 edgeSSIM 0.9582 ≥ 基底 0.9571(唯一不伤边缘结构的档,预设给最多)
    //  · 保留细节=v50 平坦区误差 2.09(基底 2.07,全档最干净)
    //  · 边缘抗锯齿=旧 sab 参数实测空转;2026-09-12 起这一档不再进 ffmpeg 链(转 C# 并行实现),
    //    见 AlhPro.Core.VideoPostFilters 类注释与 EngineService.ApplyEdgeSmoothToJpeg。
    //  · 去频闪/去杂色/去模糊三项已删除,理由见 git 历史与本文件旧注释(不要加回来)。
    // 调用点一律直接用 AlhPro.Core.VideoPostFilters.Build(...)(不再保留本地同名包装,避免两份实现分叉)。

    /// <summary>视频降噪滤镜(ffmpeg nlmeans 非局部均值,比 hqdn3d 强得多):
    /// 实测 hqdn3d(原实现)对压缩/随机噪点几乎无效果,而 nlmeans 效果真实可见,故改用 nlmeans。
    /// 【参数必须写显式名,绝不能用位置参数 —— 这里踩过坑】原先写成 "nlmeans=3:3:7:3",注释还写成
    /// "sigma空间:radius:patch_size:sigma时间" —— 那是 hqdn3d 的签名,nlmeans 根本没有这个顺序
    /// (本机 `ffmpeg -h filter=nlmeans` 实证,选项为 s/p/pc/r/rc)。于是第 3 个数被当成【色度 patch】
    /// 而第 4 个数被当成【研究窗】:结果色度 patch 比亮度 patch 还大、研究窗被压到 3~7(默认 15)。
    /// 【实测数据】720p24 合成噪声源,PSNR 对干净源(未降噪基准 32.97 dB);耗时=1080p 60 帧批次:
    ///   弱 旧 3:3:7:3 = 37.59 dB / 32.8 ms  → 新 s=3:p=3:r=3 = 40.63 dB / 33.0 ms  (+3.04 dB,代价不变)
    ///   中 旧 5:5:9:5 = 41.34 dB / 79.2 ms  → 新 s=5:p=5:r=5 = 42.45 dB / 77.2 ms  (+1.11 dB,略快)
    ///   强 旧 7:7:11:7 = 42.15 dB / 140.8 ms → 新 s=7:p=7:r=7 = 42.51 dB / 140.5 ms (+0.36 dB,代价不变)
    /// 结论:那个显式 pc(色度 patch)在三档上都是纯亏,去掉它(留默认 0 = 与亮度 p 相同)全部为正收益。
    /// 【为什么刻意不把 r 提到默认 15】实测代价 ∝ r²:s=5:p=7:r=15 是 622 ms/帧(8.3 倍)、只多 2.2 dB ——
    /// 与本项目"治慢"的方向相反。故三档保持小研究窗,并让 r 与 p 同步递增(小窗小块→大窗大块),便于解释。
    /// 【2026-09 按实测重定档 —— "再加强"在 nlmeans 上已经到极限,加强只会更糊】
    /// 测验口径:960×540 压缩素材(crf30)对干净原图,指标取 PSNR / 边缘区 PSNR / 细节 / 毫秒每帧。
    ///   输入未降噪          : PSNR 36.15  边缘 28.83  detail 72.8
    ///   弱 s3p3r3(旧)      : 36.38 / 28.97 / 63.1 / 147ms
    ///   弱 s5p3r5(新)      : 36.51 / 29.04 / 50.4 / 180ms   ← 弱档升级(+0.13dB、边缘 +0.07)
    ///   中 s5p5r5(不变)    : 36.42 / 28.97 / 59.5 / 181ms
    ///   中+ s8p7r9         : 36.37 / 28.94 / 51.6 / 264ms   ← 更"强"反而更差
    ///   强 s7p7r7(不变)    : 36.42 / 28.99 / 56.3 / 217ms
    ///   强+ s10p7r9        : 36.21 / 28.73 / 44.5 / 267ms   ← 更差
    ///   强+++ s15p9r13     : 35.53 / 28.03 / 35.3 / 411ms   ← 明显过降噪(细节只剩一半)
    /// 结论:nlmeans 的中/强档已在甜点,继续加大参数是拿细节换更差的客观指标,故保持不动;
    /// 只把弱档换成"更大的 sigma + 更大的研究窗"这个实测更优的组合。
    /// 【为什么 r 不提大】代价 ∝ r²;曾被试过的 s=5:p=7:r=15 是 622ms/帧(8.3 倍)只多 2.2dB,与"治慢"方向相反。</summary>
    /// 【2026-09 二次加强:补上"时间维"—— 这才是"强档感觉不够厉害"的真正原因】
    /// 实测口径:真实 640×480 视频 20 帧(onepiece_demo.mp4),无干净参考 → 用无参考指标:
    ///   flatNoise 平坦区高频能量(越低越干净)、temporal 相邻帧在平坦区的平均差(=闪烁/颗粒)、detail 边缘细节。
    ///   不降噪             : 0.74 / 0.099 / 5463
    ///   nlmeans 强(旧·强)  : 0.61 / 0.094 / 5280   ← 空间维有效,时间维几乎没动(−5%)
    ///   nlmeans 极强 s15    : 0.57 / 0.089 / 4903   ← 单纯加大参数:细节掉 10%,时间维仍几乎没动
    ///   hqdn3d 强 8:6:12:8  : 0.66 / 0.060 / 5507   ← 时间维 −39%,细节几乎不损
    ///   组合(强 + hqdn3d)  : 0.62 / 0.056 / 5246   ← 时间 −43%、噪点 −16%、细节仅 −4% ← 采用
    ///   waifu2x n=3 (逐帧)  : 0.60 / 0.106 / 5431   ← 逐帧模型对时间噪声无效(甚至更抖)
    ///   smartblur 强模糊    : 0.56 / 0.104 / 18.7   ← 细节被毁,禁用
    /// 【教训】早先判"hqdn3d 无用"是拿单张图测的 —— 时间维降噪在单帧上根本发挥不出来(测试方法本身的盲区)。
    /// 现三档都是"空间 nlmeans + 时间 hqdn3d"组合,并按档位同步放大;参数格式 hqdn3d=亮度空间:色度空间:亮度时间:色度时间。</summary>
    private static string VideoDenoiseFilter(int strength, int kind = 0)
    {
        // 【任务 M1 · 2026-09-13 口径变更:整体削弱,弱档大削弱】参数表已迁到
        // AlhPro.Core.VideoDenoise(纯逻辑 + 单测:三档严格单调、仅空间/仅时间与组合档同表同步)。
        // 新表:弱 = nlmeans s3p3r3 + hqdn3d 2:1.5:3:2 / 中 = s3p3r3 + hqdn3d 4:3:6:4 / 强 = 原【中】档整套。
        // ⚠ 旧注释里「结合模式下三档只放大时间维、空间固定轻档」的说法已作废
        //   (新表空间维也按档位分两级:弱/中 s3p3r3、强 s5p5r5),故旧 inline 参数表已整段删除。
        //   「老用户选强 = 强度变轻一档」是本次有意为之(实测强档已过降噪),不做设置迁移。
        return AlhPro.Core.VideoDenoise.Filter(strength, kind);
    }

    // ===== 历史实测留档(代码不再走这里;现行参数表见 AlhPro.Core.VideoDenoise)=====
    // kind: 0=两者结合(默认,兼容旧设置) 1=仅空间 nlmeans 2=仅时间 hqdn3d
    // 【结合模式为什么空间只用"轻"档】用户素材实测(1080p 2 秒):空间参数调重是拿细节换收益 ——
    //   nlmeans 强 + hqdn3d 强 : 平坦噪点 0.66 / 抖动 1.419 / 细节 19.0
    //   nlmeans 弱 + hqdn3d 强 : 平坦噪点 0.69 / 抖动 1.396 / 细节 20.7  ← 采用(抖动更低、细节多 9%)
    // 【仅时间模式擦不干净单帧噪点】实测把 hqdn3d 空间参数从 8 拉到 16,平坦噪点 0.77→0.77 纹丝不动 ——
    // 它那部分机制天生就弱,这是"部分噪点去不干净"的根因,不是参数没调好(要用结合模式才能清掉)。
    // 【迁移前 inline 参数表】spatialFor: 2→s5p5r5,其他→s5p3r5;spatialOnly: 1→s5p3r5,2→s5p5r5,其他→s7p7r7;
    // temporal: 1→4:3:6:4, 2→8:6:12:8, 其他→12:10:12:8(「强」就是这一行)。

    /// <summary>降噪方式的中文名(日志/提示用,措辞与界面下拉项一致)。</summary>
    private static string DenoiseKindName(int kind) => kind switch
    {
        1 => "nlmeans(仅空间域降噪)",
        2 => "hqdn3d(仅时间域降噪)",
        _ => "nlmeans + hqdn3d(空间域与时间域联合降噪)",
    };

    // ===== 【降噪·自动档 · 2026-09-21】源素材体检 =====
    /// <summary>源素材体检(降噪「自动」档用):抽 6 帧,在**源分辨率**上量 颗粒σ / 块效应 / 闪烁σ。
    /// 【口径与阈值同源】判据在 <see cref="AlhPro.Core.VideoNoiseProbe"/>;量法与 `_qa\denoise_source_audit.py`
    /// 逐条对应 —— 那份 Python 脚本就是标定这套阈值的工具,改这里必须同时改它、并重跑
    /// `_qa\降噪整改_实测_20260921.md`(干净的源上降噪是净亏、有噪的源上才净赚,两条都是实测量出来的)。
    /// 【为什么只取中间一块】整帧 ×6 的浮点数组要几十 MB 且没必要:取居中的 960×540(源更小则整帧),
    /// 起点**对齐到 8 的倍数** —— 块效应指标按 8 像素网格算,起点不对齐会直接把指标量错。
    /// 【失败一律返回 null、绝不抛】调用方按"体检失败 ⇒ 不降噪"处理(见 VideoNoiseProbe.Decide 的默认值):
    /// 抽帧都失败说明源有问题,此时"少做一步"比"多糊一层"安全,用户仍可手动选档强制降。</summary>
    private static async Task<AlhPro.Core.VideoNoiseStats?> ProbeSourceNoiseAsync(string input, double duration, CancellationToken ct)
    {
        // 【两段各 3 张**连续**帧 —— 2026-09-21 从"铺 6 个时间点"改过来】
        // 原因(真机对拍发现):按 10%~90% 铺点取帧,帧间隔十几秒,时间标准差量到的是**换镜头**而不是闪烁,
        // 同一片段给出 19.4 这种假数(相邻帧量出来是 0.4~1.5)⇒ 闪烁那一列就成了误导。
        // 现在:在 30% / 70% 处各抽 3 张相邻帧(一个 ffmpeg 调用就能抽完,比原来 6 次调用还便宜),
        // 颗粒/块效应照旧合起来平均(单帧指标),闪烁按**段**算再平均(相邻才有意义)。
        const int perBurst = 3;
        string dir = Path.Combine(Path.GetTempPath(), "alhpro_noiseprobe_" + Guid.NewGuid().ToString("N"));
        try
        {
            var ff = FfmpegPath;
            if (ff == null) return null;
            Directory.CreateDirectory(dir);
            var frames = new System.Collections.Generic.List<float[]>();
            int cw = 0, ch = 0;
            var fracs = duration > 2.0 ? new[] { 0.30, 0.70 } : new[] { 0.0 };   // 太短的片子就只有一段
            int tag = 0;
            foreach (var frac in fracs)
            {
                ct.ThrowIfCancellationRequested();
                double t0 = duration > 2.0 ? duration * frac : 0.0;
                int myTag = tag++;
                var pngs = await Task.Run(() => RunFfmpegFrames(ff, input, t0, perBurst, dir, myTag), ct).ConfigureAwait(false);
                foreach (var png in pngs)
                {
                    int w = 0, h = 0;
                    var arr = await Task.Run(() => ReadGrayCrop(png, out w, out h), ct).ConfigureAwait(false);
                    if (arr == null || w < 32 || h < 32) continue;
                    if (frames.Count == 0) { cw = w; ch = h; }
                    else if (w != cw || h != ch) continue;      // 尺寸不一致(源中途换分辨率)⇒ 丢掉这帧,别混着算
                    frames.Add(arr);
                }
            }
            if (frames.Count < 3 || cw == 0)
                return new AlhPro.Core.VideoNoiseStats(double.NaN, double.NaN, 0, frames.Count);

            var stats = AlhPro.Core.VideoNoiseProbe.Measure(frames, cw, ch, perBurst);
            AppLogger.Info($"视频降噪体检:取 {stats.Frames} 帧({fracs.Length} 段 × 每段 {perBurst} 张相邻帧)、"
                + $"{cw}×{ch}(源分辨率的居中块,起点对齐 8 像素)");
            return stats;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLogger.Warn($"视频降噪体检失败({ex.GetType().Name}: {ex.Message})⇒ 本批按「不降噪」处理");
            return null;
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* 临时目录删不掉不影响结果 */ }
        }
    }

    /// <summary>从 t0 起抽 n 张**连续**帧(一个 ffmpeg 调用;返回真正落盘的路径表)。
    /// 【为什么一次抽多张】闪烁必须看相邻帧(见 ProbeSourceNoiseAsync 的说明),
    /// 而且一次调用比"每帧起一次 ffmpeg"更便宜 —— 顺带把原来的 6 次进程启动降到 2 次。</summary>
    private static System.Collections.Generic.List<string> RunFfmpegFrames(string ffmpeg, string input, double t0, int n, string dir, int tag)
    {
        var outp = new System.Collections.Generic.List<string>();
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = $"-y -v error -ss {t0.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} "
                          + $"-i \"{input}\" -frames:v {n} \"{Path.Combine(dir, $"b{tag}_%02d.png")}\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using (var pr = System.Diagnostics.Process.Start(psi))
            {
                if (pr == null) return outp;
                pr.StandardOutput.ReadToEnd();
                pr.StandardError.ReadToEnd();
                if (!pr.WaitForExit(20000)) { try { pr.Kill(entireProcessTree: true); } catch { } return outp; }
            }
            for (int i = 1; i <= n; i++)
            {
                var p = Path.Combine(dir, $"b{tag}_{i:00}.png");
                if (File.Exists(p)) outp.Add(p);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"视频降噪体检:抽帧失败({ex.GetType().Name}: {ex.Message})");
        }
        return outp;
    }

    /// <summary>读一张帧文件 → 居中 960×540(不足则整帧、起点对齐 8 的倍数)的灰度 float[]。
    /// 灰度系数与 Python 侧(PIL convert('L'))一致:0.299R + 0.587G + 0.114B。</summary>
    private static float[]? ReadGrayCrop(string png, out int w, out int h)
    {
        w = 0; h = 0;
        try
        {
            using var bmp = new System.Drawing.Bitmap(png);
            if (bmp.Width < 32 || bmp.Height < 32) return null;
            int cw = Math.Min(960, bmp.Width), ch = Math.Min(540, bmp.Height);
            int x0 = (bmp.Width - cw) / 2, y0 = (bmp.Height - ch) / 2;
            x0 -= x0 % 8; y0 -= y0 % 8;
            using var crop = bmp.Clone(new System.Drawing.Rectangle(x0, y0, cw, ch),
                System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            var data = crop.LockBits(new System.Drawing.Rectangle(0, 0, cw, ch),
                System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            try
            {
                var g = new float[cw * ch];
                var row = new byte[data.Stride];
                for (int y = 0; y < ch; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, data.Stride);
                    for (int x = 0; x < cw; x++)
                    {
                        int o = x * 3;   // Format24bppRgb 在内存里是 B,G,R
                        g[y * cw + x] = 0.299f * row[o + 2] + 0.587f * row[o + 1] + 0.114f * row[o];
                    }
                }
                w = cw; h = ch;
                return g;
            }
            finally { crop.UnlockBits(data); }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"视频降噪体检:读帧失败({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }

    /// <summary>waifu2x 模型自带降噪档(-n)的映射已【迁到】<see cref="AlhPro.Core.Waifu2x.NoiseLevelFor"/>
    /// (纯逻辑 + 单测:档位严格单调、关真关、1x 不下发 -n -1)。
    /// 【口径变更 2026-09-13(任务 L,用户要求)】旧实现是 `chosen >= 1 ? chosen : 2`:
    ///   · "关"关不掉(未勾选仍下发 -n 2);· 档位非单调(不勾=2 档、弱=1 档、中=2 档、强=3 档)。
    /// 现在是 关→-1、弱→0、中→1、强→2(严格递增),调用点按【最终下发给引擎的倍率】判 1x 护栏。
    /// 【旧默认 -n 2 的实测数据(保留备查,但不再是默认)】真实动画帧 960×540→1080p:
    ///   压缩素材(h264 crf30)n2 比 n0 PSNR 35.68→36.23、SSIM 0.9434→0.9654(双升);
    ///   干净素材 n2 比 n0 PSNR 略降 0.94 但 SSIM 反升 0.037、细节(拉普拉斯方差)不降 —— 基本无损。
    ///   数据与对比图:_qa\ab_waifu\REPORT.md。用户按观感决定"关"必须是真关,故不再默认替用户开。</summary>

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
    /// globalTarget &gt; 0 **只对末段传非 0**(主流程 `isLastSeg ? globalTarget : 0`),它在方法里当"这是末段"的标志用。
    /// 【任务 N · 2026-09-13 更正口径】末段的 -n 取"本段自然产量" (段长-1)×mult+1(见 Core.InterpSegmentTarget),
    /// **不是**"全局目标 − 已输出"这种差值算法 —— 差值算法在"各段非末段按 段长×mult 产出"时数值恰好相等
    /// ((总帧数-1)×mult+1),但表/文件数的对齐由 Core 的纯函数统一保证,不再依赖两处各算一遍。
    /// appendTailCopy = true(末段,非 VFR):给 RIFE 追加末帧副本,让最后一段真实插值,
    /// 避免 RIFE -n 把末帧复制成 3 帧(尾部"卡住");副本产生的冻结帧由合帧对齐裁掉。</summary>
    /// <summary>从 RIFE 命令行参数里解析出 ONNX 补帧所需的输入目录/输出目录/目标帧数;解析失败返回 false。</summary>
    private static bool TryGetRifeOnnxFrames(string args, out string? sIn, out string? oDir, out int target)
    {
        sIn = null; oDir = null; target = 0;
        try
        {
            var mIn = System.Text.RegularExpressions.Regex.Match(args, @"-i\s+""([^""]+)""");
            var mOut = System.Text.RegularExpressions.Regex.Match(args, @"-o\s+""([^""]+)""");
            var mN = System.Text.RegularExpressions.Regex.Match(args, @"-n\s+(\d+)");
            if (!mIn.Success || !mOut.Success || !mN.Success) return false;
            sIn = mIn.Groups[1].Value;
            oDir = mOut.Groups[1].Value;
            target = int.Parse(mN.Groups[1].Value);
            return target >= 2;
        }
        catch { return false; }
    }

    /// <summary>ONNX 逐对补帧(50 系/GPU 不可用设备稳定路线):逐对插值、黑帧防御、进度汇报、响应取消。
    /// 输入 sIn 的 frame_*.jpg,输出 oDir 的 frame_*.png;target=目标帧数。</summary>
    private static async Task RifeOnnxInterpDirAsync(string sIn, string oDir, int target, int gpuId,
        int watchTotal, string? watchDir, CancellationToken ct,
        IProgress<(int pct, string msg)>? progress)
    {
        await Task.Run(() =>
        {
            Directory.CreateDirectory(oDir);
            var files = Directory.EnumerateFiles(sIn, "frame_*.jpg")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            if (files.Count < 2) return;

            int srcCount = files.Count;
            int pairs = srcCount - 1;
            if (pairs <= 0) return;

            // ===== 多路并行补帧(治"独显占用低/速度慢")=====
            // 关键不变量:输出帧号严格用 InterpFraming 预分配(串行时完全一致),帧号精确连续、不重不漏,
            // 否则合帧缺号/乱序 → 整段视频黑帧/花屏(已用单测钉住 ComputeLayout)。
            var (per, totalOut) = AlhPro.Core.InterpFraming.ComputeLayout(Math.Max(1, target), pairs);
            // 【任务级选卡 · 修形参未使用】形参 gpuId 此前【从未被引用】(恒按全局 AppSettings.GpuIndex 解析),
            // 于是"本次任务选了哪张卡"在 ONNX 补帧这条路上被忽略(H 系列自检点名的缺陷)。
            // 现在:gpuId>=0(具体的引擎编号)= 按它解析;gpuId<0(-2 自动 / -1)= 才回落到全局设置与启动探测结论
            // (-1 不表示"落 CPU":补帧绝不落 CPU 是硬约定,这里一律按自动处理)。
            // 【设备解析的真实语义,注释按实现改】ResolveDmlDevice 在"名称匹配不到"时【返回 -1】,
            // 不会自行改到别的卡(自检 H2:注释曾承诺"编号命中核显→换独显",与实现相反)——
            // 返回 -1 时下面的告警会把原因说清并放弃本段补帧,绝不静默落 CPU 或换到未选定的卡。
            int engineGpu = gpuId >= 0 ? gpuId : AppSettings.GpuIndex;
            int dmlGpu = engineGpu >= 0
                ? EngineService.ResolveDmlDevice(engineGpu)
                : EsrganOnnxService.DmlFallbackOk;
            if (dmlGpu < 0)
            {
                AppLogger.Warn($"⚠ 补帧 ONNX:无可用 DirectML 设备(引擎编号 {engineGpu} 解析失败,或 DirectML 已被摘除)"
                    + "—— 本段不补帧(不落慢速 CPU)。请在「计算设备」里重新选一张卡,或重启软件后重试");
                return;
            }

            // 并发度受显存墙约束;DirectML session 非线程安全 → 每 worker 独占会话,绝不能共用/并发 Run。
            // 【F3】路数改按"可用显存"动态定(不再是"8GB 卡也开 2 路"):真机诊断里 8GB 卡(显存墙 6.0GB)
            // 按旧口径开 2 路 → 两路各持一份推理工作集 → 反复 E_OUTOFMEMORY(0x8007000E) → 逐帧回退。
            // 取舍:显存紧的机器路数减少、吞吐下降(约 20%),但不会因爆显存把整帧丢掉。
            bool wantGpu = dmlGpu >= 0;
            double? freeVramMeasured = SafeRender.FreeVramMeasured ? SafeRender.FreeVramGB : null;
            int concurrency = AlhPro.Core.RenderPolicy.OnnxSessionConcurrency(wantGpu, SafeRender.EffectiveVramGB, freeVramMeasured);
            if (concurrency > pairs) concurrency = Math.Max(1, pairs);
            Microsoft.ML.OnnxRuntime.InferenceSession[] sessions;
            // 【让"段间停顿"可见】ONNX 路线每次调用都要新建 DirectML 会话(实测同样是秒级开销):
            // 建会话前先报一行"正在启动",建好后报"已就绪(启动 X.Xs)" —— 与 ncnn 引擎的启动提示同口径,
            // 让用户知道这几秒是在建推理会话,而不是卡死。只多两行进度上报,处理逻辑一字不改。
            var onnxStartAt = DateTime.UtcNow;
            progress?.Report((0, $"补帧(ONNX 稳定引擎):正在创建 {concurrency} 路推理会话(约数秒)…"));
            AppLogger.Info($"补帧(ONNX 稳定引擎):正在创建 {concurrency} 路推理会话(每段一次,秒级固定开销);"
                + $"目标设备:DirectML #{dmlGpu}(解析自引擎编号 {engineGpu});"
                + $"路数依据:{AlhPro.Core.RenderPolicy.OnnxConcurrencyRule(wantGpu, SafeRender.EffectiveVramGB, freeVramMeasured)}");
            try { sessions = RifeOnnxService.CreateSessions(concurrency, dmlGpu); }
            catch (InvalidOperationException) { throw; }
            catch { sessions = new Microsoft.ML.OnnxRuntime.InferenceSession[] { RifeOnnxService.CreateSessions(1, dmlGpu)[0] }; }
            concurrency = sessions.Length;
            // 注:本方法在视频链路里的 progress 是【段级包装器】(它会把消息重写成"补帧 第 N 帧 / 共 M 帧"),
            // 故"已就绪"这句话在界面上仍显示为帧号文案;真正让停顿可见的是日志这一行与段级包装器外的提示。
            progress?.Report((0, $"补帧(ONNX 稳定引擎)已就绪(会话启动 {(DateTime.UtcNow - onnxStartAt).TotalSeconds:0.0}s,{concurrency} 路),开始处理 {totalOut} 帧…"));
            AppLogger.Info($"补帧(ONNX 稳定引擎)已就绪:会话启动 {(DateTime.UtcNow - onnxStartAt).TotalSeconds:0.0}s,{concurrency} 路,目标 {totalOut} 帧");

            bool onnxDead = EsrganOnnxService.DmlDeviceDead;   // 设备已摘除:剩余帧复制原帧(不落 CPU)
            int abortFlag = 0;
            Exception? fatal = null;
            int doneCount = 0;
            try
            {
                var workers = new System.Threading.Tasks.Task[concurrency];
                for (int w = 0; w < concurrency; w++)
                {
                    int wi = w;
                    workers[w] = System.Threading.Tasks.Task.Run(() =>
                    {
                        for (int p = wi; p < pairs; p += concurrency)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (Volatile.Read(ref abortFlag) != 0) break;
                            int startIdx = AlhPro.Core.InterpFraming.StartIndex(p, per);
                            // 左端点帧(files[p])——与串行一致:复制自身
                            string left = Path.Combine(oDir, $"frame_{startIdx:D6}.png");
                            try { CopyFrame(files[p], left); } catch { }
                            int mids = per[p] - 1;   // 该对帧的中间帧数
                            for (int t = 1; t <= mids; t++)
                            {
                                ct.ThrowIfCancellationRequested();
                                if (Volatile.Read(ref abortFlag) != 0) break;
                                float time = t / (float)(mids + 1);
                                string outF = Path.Combine(oDir, $"frame_{startIdx + t:D6}.png");
                                if (onnxDead)
                                {
                                    CopyFrame(files[p], outF);
                                }
                                else
                                {
                                    try { RifeOnnxService.InterpWithSession(sessions[wi], files[p], files[p + 1], time, outF, dmlGpu); }
                                    catch (Exception ex)
                                    {
                                        if (EsrganOnnxService.DmlDeviceDead
                                            || AlhPro.Core.GpuFault.IsPersistentDeviceError(ex)
                                            || EsrganOnnxService.AnyDmlDeviceUnusable())
                                        {
                                            onnxDead = true;
                                            Interlocked.CompareExchange(ref fatal, ex, null);
                                            Volatile.Write(ref abortFlag, 1);
                                            AppLogger.Warn($"⚠ GPU 补帧已不可用({ex.Message.Split('\n')[0]})——剩余帧改为复制原帧(不降级到慢速 CPU);请重启软件后重试");
                                            break;
                                        }
                                        AppLogger.Warn($"ONNX 补帧失败({ex.Message.Split('\n')[0]})——回退复制原帧");
                                        CopyFrame(files[p], outF);
                                    }
                                }
                                // 【黑帧防御】ONNX/DirectML 偶发静默输出全黑 → 源不黑则回退该帧
                                if (File.Exists(outF) && EngineService.IsBlackPng(outF) && !EngineService.IsBlackPng(files[p]))
                                {
                                    CopyFrame(files[p], outF);
                                    AppLogger.Warn($"⚠ ONNX 补帧第 {startIdx + t} 帧输出黑帧(DirectML 异常),已回退复制该对源帧");
                                }
                                // 进度:补帧阶段 10~45%,按已产中间帧数估算(并行安全:Interlocked 计数)。
                                // 文案格式是契约:上层 segProg / etaRegex 靠「第 N 帧 / 共 M 帧」提取帧号。
                                int dn = Interlocked.Increment(ref doneCount);
                                try
                                {
                                    int pct = Math.Clamp(10 + dn * 35 / Math.Max(1, totalOut), 10, 45);
                                    progress?.Report((pct, $"补帧 第 {dn} 帧 / 共 {totalOut} 帧"));
                                }
                                catch { }
                            }
                        }
                    }, ct);
                }
                try { System.Threading.Tasks.Task.WaitAll(workers); }
                catch (System.AggregateException ae)
                {
                    // 取消(用户点「强制结束」/ct 取消)必须重新抛 OperationCanceledException,否则上层
                    // catch(OperationCanceledException) 接不到 AggregateException,导致"取消被吞、任务异常收尾"。
                    if (ae.Flatten().InnerExceptions.OfType<OperationCanceledException>().Any() || ct.IsCancellationRequested)
                        throw new OperationCanceledException(ct);
                    throw;
                }
                if (fatal != null)
                    throw new InvalidOperationException($"补帧 ONNX 中止:GPU 设备已失效({fatal.Message.Split('\n')[0]})", fatal);
            }
            finally
            {
                foreach (var s in sessions) try { s.Dispose(); } catch { }
            }

            // 端帧(files[^1])——与串行一致,最后复制
            try { CopyFrame(files[^1], Path.Combine(oDir, $"frame_{totalOut + 1:D6}.png")); } catch { }
            try { progress?.Report(((int)AlhPro.Core.ProgressBands.Of(AlhPro.Core.ProgressBands.Stage.Interp).lo, $"补帧(ONNX)完成:{totalOut + 1} 帧")); } catch { }
        }).ConfigureAwait(false);
    }

    /// <summary>复制文件(失败静默忽略)。</summary>
    private static void CopyFrame(string src, string dst)
    {
        try { File.Copy(src, dst, true); }
        catch { }
    }

    private static async Task<int> InterpSegmentAsync(string rife, string framesOut, string framesFinal,
        int start, int end, int interpScale, string interpModel, double? timeStep, bool tta, int gpuId, int globalIdx,
        IProgress<(int pct, string msg)>? progress, CancellationToken ct, double frameScale = 1.0, long globalTarget = 0,
        bool appendTailCopy = false, Action<double>? onEngineReady = null, bool forceOnnx = false,
        double fractionalMult = 1.0, int totalSrcFrames = 0, long targetFramesOverride = 0)
    {
        int segLen = end - start;
        if (totalSrcFrames <= start) totalSrcFrames = end;   // 兜底:调用方没给总帧数时按本段末尾当上限(防越界)
        var workDir = Path.GetDirectoryName(framesFinal)!;
        var segIn = Path.Combine(workDir, $"seg_{start}_{end}_in");
        Directory.CreateDirectory(segIn);
        for (int i = start; i < end; i++)
            File.Copy(Path.Combine(framesOut, $"frame_{i + 1:D6}.jpg"), Path.Combine(segIn, $"frame_{i - start + 1:D6}.jpg"), true);

        // 模型目录前置校验:缺失立即明确报错(而不是等下半天引擎报 stderr 尾部的晦涩错误)
        var rifeDir = Path.GetDirectoryName(rife) ?? ".";
        if (!Directory.Exists(Path.Combine(rifeDir, interpModel)))
            throw new InvalidOperationException($"缺少补帧模型:{interpModel}。");

        // GPU 失败自动降级:当前 GPU → 其他 GPU(多卡机:核显失败切独显)→ ONNX DirectML → CPU(与超分同策略,但优先 ONNX)
        async Task RunRifeAsync(string args, int gpuNow, int watchTotal, string? watchDir)
        {
            // ===== 补帧降级链(用户指定):独显 ncnn → ONNX(DirectML GPU)→ 换卡(另一块 GPU)→ 不落 CPU =====
            // ncnn-CPU 太慢,不做兜底。ONNX 是独立运行时(DirectML),ncnn 崩的卡 DirectML 常能正常 GPU 加速,
            // 故优先于换卡。ONNX 内部已做"逐对失败复制原帧 + 黑帧回退",尽力出帧,不抛异常。
            // 换卡后失败/黑帧或 ONNX 不可用且无卡可换 → 该段报错(绝不回落慢速 CPU)。
            async Task<bool> TryOnnxAsync()
            {
                if (RifeOnnxService.Available() && TryGetRifeOnnxFrames(args, out var onnxIn, out var onnxOut, out var onnxTarget))
                {
                    AppLogger.Info("✅ 补帧降级:改用 ONNX 稳定引擎(rife49.onnx)重算该段 —— 真实设备号见紧随其后的「补帧(ONNX 稳定引擎):正在创建…目标设备」一行"
                        + "(文案不写死 DirectML:挂不上 DML 时会静默落 CPU,只有那一行报的是实话)");
                    progress?.Report((0, "⚠ 补帧改用 ONNX 稳定模型(DirectML GPU)重算..."));
                    await RifeOnnxInterpDirAsync(onnxIn!, onnxOut!, onnxTarget, -2, watchTotal, watchDir, ct, progress).ConfigureAwait(false);
                    return true;
                }
                return false;
            }
            // 【任务 O2】已知 ncnn 在本段输入尺寸下会**静默输出全黑帧**(8K 级,真机实测 117/119 全黑、exit=0):
            // 直接走稳定引擎(ONNX),既不白跑一遍、也不给"事后抽样没抓到"留机会。
            if (forceOnnx)
            {
                if (await TryOnnxAsync().ConfigureAwait(false)) return;
                throw new InvalidOperationException(
                    "补帧输入分辨率过大(8K 级):ncnn 引擎在该尺寸下会静默输出全黑帧(实测 7680×4320 → 117/119 全黑、退出码 0),"
                    + "而稳定引擎(ONNX)当前不可用。请改选「不补帧」,或降低超分倍数/换用较低分辨率素材后重试。");
            }
            // 降级链:ONNX 优先 → 换另一块 GPU(ncnn)重跑 → 无卡可换则报错(不回落 CPU)。
            async Task TryDegradeAsync(int? altGpu)
            {
                if (await TryOnnxAsync().ConfigureAwait(false)) return;   // ① 先 ONNX DirectML GPU
                if (altGpu.HasValue)                                       // ② ONNX 不可用 → 换另一张卡(ncnn)
                {
                    AppLogger.Info($"⚠ ONNX 不可用,改用 GPU {altGpu.Value}(另一块显卡,不落 CPU)重算该段");
                    progress?.Report((0, $"⚠ ONNX 不可用,改用 GPU {altGpu.Value} 重算该段(不落 CPU)..."));
                    await TryGpuAsync(altGpu.Value, null).ConfigureAwait(false);   // 换卡后再失败不再降级
                    return;
                }
                // ③ 无卡可换、ONNX 又不可用 → 该段报错(禁用 CPU 兜底)
                throw new InvalidOperationException(
                    "补帧失败:没有可用的加速显卡。请在「计算设备」里选一块显卡,或确认已启用显卡加速后重试。");
            }
            // 尝试一块 GPU(ncnn);失败/黑帧/0帧 → 走降级链(ONNX→换卡→报错),不回落 CPU。
            async Task TryGpuAsync(int g, int? altGpu)
            {
                try
                {
                    var gArgs = System.Text.RegularExpressions.Regex.Replace(args, @"-g\s+-?\d+", $"-g {g}");
                    await RunAsync(rife, gArgs, progress, ct, "补帧", watchTotal, watchDir, onEngineReady).ConfigureAwait(false);
                    // 黑帧/0帧防御:GPU 输出全黑(vkQueueSubmit 失败但退出码 0)【或不输出任何帧(空跑,退出码 0)】
                    // → 走 ONNX→换卡 降级重跑该段。0帧正是"补帧失败,未生成插帧"的根因(RIFE exit=0 却无输出,须兜底降级)。
                    // 补充【残缺帧数防御】:RIFE 偶发"只输出第 1 帧就 exit 0"(AMD 6750 GRE 实测 140→1 帧,间歇性)——
                    // 此时有帧非黑帧,但帧数远少于目标,须触发降级而非当成功。
                    if (g >= 0 && watchDir != null && Directory.Exists(watchDir))
                    {
                        // 【2026-09-16 用户裁决:删掉事后黑帧判定,改为"事前兼容性提示"】
                        // 原实现抽 8~48 帧判黑,命中就把整段作废、改用 ONNX 重算。实测代价(200 帧 1440p×4x 一段):
                        //   ncnn 重算整段 ≈ 53 秒,而 ONNX 只有 1 路 ≈ 10~20 分钟 —— ncnn 本身跑完全段只花 53 秒。
                        // 更糟的是**误判**:暗场素材里"输出黑 = 源帧本来就黑"(引擎正确复制), 判定却常因
                        //   "输出帧号→源帧号 ±1 偏移"或"判据不对称(输出带条带判定、源侧只判整帧)"而判成故障。
                        // 真机实证(合成素材:黑→灰→白三段)黑段被整段判故障 → 降级 ONNX,产出帧数还退回旧口径。
                        // 用户裁决:收益远小于代价 ⇒ **不再做事后黑帧检测/降级**;黑帧的防与治移到事前预检。
                        // 【保留的两条守卫】与"画面黑不黑"无关,是真正的失败形态:
                        //   ① 0 帧(引擎空跑,exit=0 却没输出)—— 补帧等于没做,必须救;
                        //   ② 帧数残缺(< 目标一半,AMD 6750 实测 140→1 帧)—— 同上。
                        bool anyFrame = Directory.EnumerateFiles(watchDir, "*.*")
                            .Any(x => x.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                                   || x.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                                   || x.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase));
                        if (!anyFrame)
                        {
                            AppLogger.Info($"⚠ 降级:补帧 GPU {g} 未输出任何帧(0帧,引擎空跑),走 ONNX→换卡 重算该段(不落 CPU)");
                            progress?.Report((0, $"⚠ 补帧 GPU {g} 未输出帧,改用 ONNX/换卡重算该段(不落 CPU)..."));
                            await TryDegradeAsync(altGpu).ConfigureAwait(false);   // ONNX→换卡,不回落 CPU
                        }
                        else
                        {
                            // 帧数残缺检测:统计输出目录实际帧数,若远少于目标帧数(如 < 一半)判残缺 → 降级
                            int outCount = EnumerateFrameFiles(watchDir).Count();
                            if (watchTotal > 4 && outCount < watchTotal * 0.5)
                            {
                                AppLogger.Info($"⚠ 降级:补帧 GPU {g} 输出残缺(仅 {outCount}/{watchTotal} 帧,疑似引擎静默丢帧),走 ONNX→换卡 重算该段(不落 CPU)");
                                progress?.Report((0, $"⚠ 补帧 GPU {g} 输出残缺({outCount}/{watchTotal} 帧),改用 ONNX/换卡重算该段..."));
                                await TryDegradeAsync(altGpu).ConfigureAwait(false);
                            }
                        }
                    }
                }
                catch (InvalidOperationException ex) when (g >= 0)
                {
                    AppLogger.Info($"⚠ 降级:补帧 GPU {g} 失败({ex.Message.Split('\n')[0]}),走 ONNX→换卡 重算(不落 CPU)");
                    progress?.Report((0, $"⚠ 补帧 GPU {g} 失败,改用 ONNX/换卡重算(不落 CPU)..."));
                    await TryDegradeAsync(altGpu).ConfigureAwait(false);   // ONNX→换卡,不回落 CPU
                }
            }

            if (gpuNow >= 0)
            {
                // 优先独显:只跑选定的 GPU(gpuNow)。失败/黑帧/0帧 → ONNX → 换卡 → 报错(不用 CPU)。
                // 多卡机器给 altGpu=另一块卡;单卡给 null(无卡可换时尽快报错,不落 CPU)。
                int? alt = null;
                try
                {
                    var devs = VulkanCheck.Devices;
                    if (devs.Count >= 2)
                        alt = devs.FirstOrDefault(d => d.Id != gpuNow).Id;
                }
                catch { }
                await TryGpuAsync(gpuNow, alt).ConfigureAwait(false);
            }
            else
            {
                // ===== ONNX 补帧路线(50 系/GPU 不可用设备):ncnn-CPU 慢,若 ONNX 模型在 → 走 ONNX(DirectML/CPU 逐对)=====
                // 50 系 ncnn-Vulkan 崩(黑帧/静默 hang)→ 原逻辑直接降 ncnn-CPU(慢);
                // 现在优先 ONNX:DirectML GPU 可跑(50 系 DirectML 正常),失败自动 CPU。
                if (RifeOnnxService.Available() && TryGetRifeOnnxFrames(args, out var onnxSegIn, out var onnxOut, out var onnxTarget))
                {
                    AppLogger.Info($"✅ 补帧改走 ONNX 路线(rife49.onnx,DirectML→CPU)——(若 DirectML 不可用则落 CPU,速度会变得特别慢;50 系/GPU 不可用设备仍走此稳定路线)");
                    progress?.Report((0, $"补帧改用 ONNX 模型(50 系/GPU 不可用设备更稳定)..."));
                    // 传原始 gpuId:ONNX 内部 DirectML GPU 优先,失败自动 CPU(单会话加速优于 ncnn-CPU)
                    await RifeOnnxInterpDirAsync(onnxSegIn!, onnxOut!, onnxTarget, -2, watchTotal, watchDir, ct, progress).ConfigureAwait(false);   // -2 = 按帧自动选设备
                }
                else
                {
                    // GPU 不可用(无独显/50 系 ncnn 崩)且 ONNX 模型缺失:此前会静默降 ncnn-CPU(数小时,太慢)。
                    // 按用户要求不落 CPU:直接报错,而不是让视频慢到像卡死。
                    throw new InvalidOperationException(
                        "补帧失败:没有可用的显卡加速(且未检测到补帧引擎)。请在「计算设备」里选一块显卡后重试。");
                }
            }
        }

        // TTA 开关(所有模型可用);时间步仅 v4 架构模型支持
        // 实测:rife-v4.26 模型加 -x(空间TTA)会卡死;加 -z 也会卡(目录/单对都测过)——
        // v4.26 的 TTA 完全不可用(引擎/该模型权重兼容问题)。故 v4.26 一律不传 TTA;UI 侧同时禁勾选。
        var ttaArgs = tta ? (IsV4Model(interpModel) && interpModel == "rife-v4.26" ? "" : " -x -z") : "";
        var gpuArg = gpuId >= 0 ? gpuId : -1;   // ncnn:-1 = CPU
        string finalOut;
        if (segLen >= 2)
        {
            if (IsV4Model(interpModel))
            {
                // v4 架构:支持 -n 自定义目标帧数(时间步 -s 在目录模式下被引擎忽略,已撤掉该功能)。
                // B 方案(原帧率×倍率):全局目标 = (原帧数-1)×倍率+1(末段由主流程传入 globalTarget 补足),
                // 保证 Σ各段 = 全局目标、最后锚点帧落在最后一帧,输出帧率=原帧率×倍率、时长=原、不变速。
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                int targetFrames;
                // 关键修复(源码验证):rife -n 是【整个序列的总目标帧数】,目录模式下 -s 被忽略、时间步按帧索引均分;
                // -n 必须是输入帧数的【整数倍】,否则帧间距不均匀 → "全程轻微漏帧/judder"(用户实测症状)。
                // 【任务 N 更正】-n = 输入帧数 × mult 时,多出来的那 1 帧是 RIFE 把末帧复制出来的"冻结帧"
                // (它只为让最后一段有真实插值,靠合帧前的"帧数对齐"裁掉);VFR 路径不做帧数对齐(要保时间轴),
                // 于是整片比设计目标多 1 帧(真机实测 1710 vs (855-1)×2+1=1709)。现在末段直接要"自然产量"
                // (段长-1)×mult+1 —— 时间步按 (帧数-1)/(输出-1) 均分,2x 时步长恰好 0.5,间距仍然均匀。
                int mult = AlhPro.Core.VideoPipeline.InterpMultiplier(interpScale, frameScale);
                bool lastSeg = globalTarget > 0;   // 主流程只对末段传非 0(见本方法 doc:globalTarget 的语义)
                // 【指定帧率 · 绝对定位】本段应把整片输出"补到"第 round(end×k) 帧(k = 分数步长),
                // 减去已经产出的帧数就是本段该给的 `-n`;末段直接锚到全局目标 ⇒ Σ各段 = 全局目标,
                // **合帧一帧都不用裁**(尾部不再丢内容)。
                if (fractionalMult > 1.0 && globalTarget > 0 && totalSrcFrames >= end)
                    targetFrames = AlhPro.Core.VideoPipeline.InterpSegmentTargetExact(
                        segLen, end, totalSrcFrames,
                        targetFramesOverride > 0 ? targetFramesOverride : globalTarget,   // 【必须用指定帧率口径的目标帧数】
                        Math.Max(0, globalIdx - 1));
                else
                    targetFrames = Math.Max(segLen + 1,
                        AlhPro.Core.VideoPipeline.InterpSegmentTarget(segLen, mult, lastSeg));
                // 【任务 O2】-n 下限再兜一层:实测"1 帧目录 + -n 2"必崩(0xC0000005),且 -n 必须大于输入帧数
                targetFrames = Math.Max(targetFrames, segLen + 1);
                if (appendTailCopy)
                {
                    // 尾部插值修正:追加末帧副本(锚点 +1,目标帧数 +倍率),
                    // 让最后一段(如源 37→38)得到真实插值,而不是被 RIFE 复制成末帧冻结;
                    // 副本段产生的冻结帧会在合帧"帧数对齐"时被裁掉。
                    File.Copy(Path.Combine(segIn, $"frame_{segLen:D6}.jpg"),
                              Path.Combine(segIn, $"frame_{segLen + 1:D6}.jpg"), true);
                    targetFrames += interpScale;
                }
                finalOut = Path.Combine(workDir, $"seg_{start}_{end}_out");
                Directory.CreateDirectory(finalOut);
                // 【任务 O3 · 2026-09-13】补帧输出改【引擎直出 JPG】(`-f frame_%06d.jpg`):
                // 实测 119 帧 PNG 18.650s vs JPG 8.353s(省 86.5ms/帧,2668 帧约省 5 分钟),
                // 且直出 JPG 对 PNG 的 PSNR 48.907 dB —— 比程序内 PNG→JPG(q0.96)还高 1.56 dB(引擎 q≈100)。
                // 代价:中间帧体积 238→737 KB/帧(3.1×),已算进"临时空间预估"(见 ProcessVideoAsync 开头的口径)。
                // 【正式日志】把"分段到底按多少帧产出"留痕:指定帧率下这是排查"变速/丢尾"的第一手证据
                // (真机事故:分数步长算对了却没送到引擎,`-n` 仍是整数倍率的 907,成片节奏被拉长 1.19 倍)。
                AppLogger.Info($"补帧分段:段 {start}~{end}(输入 {segLen} 帧)→ 引擎目标 {targetFrames} 帧"
                    + $"(每源帧步长 {fractionalMult:0.###}{(fractionalMult > 1.0 ? ",指定帧率口径" : ",整数倍率口径")})");
                await RunRifeAsync(
                    $"-i \"{segIn}\" -o \"{finalOut}\" -n {targetFrames} -f \"frame_%06d.jpg\" -m {interpModel} -g {gpuArg}{ttaArgs}{SafeRender.GetEngineThreadArgs()}",
                    gpuId, targetFrames, finalOut);
            }
            else
            {
                // v2 架构模型(anime/HD/UHD/v2.3)不支持 -n(默认 2x),4x/8x 用级联多次 2x
                finalOut = segIn;
                int m = interpScale;
                // 兜底:非 2 幂级联会少补(3/2=1 只补 1 次,12→8 不精确),回落最近 2 的幂
                while (m > 1 && m % 2 != 0) m--;
                int pass = 0;
                int inLen = segLen;
                while (m > 1)
                {
                    m /= 2;
                    int outLen = inLen * 2;   // 本轮 2x 后的目标帧数
                    var curOut = Path.Combine(workDir, $"seg_{start}_{end}_p{pass++}");
                    Directory.CreateDirectory(curOut);
                    await RunRifeAsync(
                        $"-i \"{finalOut}\" -o \"{curOut}\" -f \"frame_%06d.jpg\" -m {interpModel} -g {gpuArg}{ttaArgs}{SafeRender.GetEngineThreadArgs()}",
                        gpuId, outLen, curOut);
                    // 临时文件控制:上一级级联输出(旧 finalOut)已被这一级吃完,删除释放磁盘(级联高倍率时中间级非常大)
                    if (finalOut != segIn)
                    { try { Directory.Delete(finalOut, true); } catch { } }
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
            File.Copy(Path.Combine(segIn, "frame_000001.jpg"), Path.Combine(finalOut, "frame_000001.jpg"), true);
        }

        var files = EnumerateFrameFiles(finalOut)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            // 【任务 O3】引擎现在是【直出 JPG】(见上面的 -f),所以这里是"搬"而不是"转":
            // 省掉整段 PNG 解码 + q0.96 重编码(实测省 86.5ms/帧),画质还更好(引擎 q≈100,+1.56 dB)。
            // 仅当输出真是 PNG 时(ONNX 补帧路径、旧引擎)才走原来的转码。
            var dst = Path.Combine(framesFinal, $"frame_{globalIdx++:D6}.jpg");
            if (f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                try { EngineService.ConvertPngToJpg(f, dst, VideoFrameJpgQuality); }
                catch { try { File.Copy(f, dst, true); } catch { } }
            }
            else
            {
                // 同名同扩展名直接搬(跨目录 Move;失败退回复制),不再解码重编码
                try { File.Move(f, dst, overwrite: true); }
                catch { try { File.Copy(f, dst, true); } catch { } }
            }
        }
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
        // v4.26 的 TTA(-x/-z)均卡死:一律不传(同上,UI 侧禁勾选)
        var ttaArgs = tta ? (interpModel == "rife-v4.26" ? "" : " -x -z") : "";
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

    /// <summary>单个文件是否"缺陷帧"(整帧近黑 **或** 上/中/下 1/3 条带近黑)。
    /// 【必须与输出侧同判据】输出侧用的是 `EngineService.IsBlackPngStrict`(整帧或任一 1/3 条带);
    /// 源帧侧原先只判整帧(`IsNearBlack`)⇒ **判据不对称**:宽银幕黑边/上下黑条这类素材上,
    /// 输出被判缺陷、源却"不算黑",于是必然误降级走 ONNX(真机证:5 帧的源帧近黑比例 94.9%/93.2%/93.0%,
    /// 恰在 95% 线下方,而它们的邻居源帧是黑的)。这里改调 `FrameInspect.IsDefectiveFrame`,与输出侧同源。</summary>
    private static bool IsFileDefective(string path)
    {
        using var bmp = new System.Drawing.Bitmap(path);
        var sums = new System.Collections.Generic.List<int>();
        int total = AlhPro.Core.FrameInspect.ForEachSample(bmp.Width, bmp.Height, (x, y) =>
        {
            var p = bmp.GetPixel(x, y);
            sums.Add((int)p.R + (int)p.G + (int)p.B);
        });
        return AlhPro.Core.FrameInspect.IsDefectiveFrame(sums.ToArray(), total, bmp.Width, bmp.Height);
    }

    /// <summary>取同目录下 ±delta 号源帧的路径(帧名形如 `frame_000123.jpg`)。
    /// 找不到时返回原路径(等价于"这一侧没信息",不会把整段判死)。</summary>
    private static string NeighborFrame(string srcPath, int delta)
    {
        try
        {
            string dir = Path.GetDirectoryName(srcPath) ?? "";
            string stem = Path.GetFileNameWithoutExtension(srcPath);
            int us = stem.LastIndexOf('_');
            if (us < 0) return srcPath;
            if (!int.TryParse(stem.Substring(us + 1), out var idx)) return srcPath;
            int want = idx + delta;
            if (want < 1) return srcPath;
            string ext = Path.GetExtension(srcPath);
            string cand = Path.Combine(dir, stem.Substring(0, us + 1) + want.ToString("D6") + ext);
            if (File.Exists(cand)) return cand;
            string other = Path.ChangeExtension(cand, ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg");
            return File.Exists(other) ? other : cand;
        }
        catch { return srcPath; }
    }

    /// <summary>该源帧本身是否"有资格解释输出黑"(判据同 <see cref="IsFileDefective"/>)。
    /// 拿不准(文件不存在/解码失败)一律 false —— 与"绝不把黑帧写进成片"的口径一致。</summary>
    private static bool SourceFrameJustifiesBlack(string srcPath)
    {
        try { return File.Exists(srcPath) && IsFileDefective(srcPath); }
        catch { return false; }
    }

    /// <summary>检查每一个"被判缺陷"的帧,其【对应源帧】是否也本来就近黑。
    /// 只有全部对应上才返回 true(= 输出黑来自素材本身,不是 GPU 故障,可跳过降级)。
    /// 【为什么按帧而不是按目录】原 DirNearBlack 是存在量词:只要目录里有【任意一张】源帧近黑就豁免整批,
    /// 于是含黑场(片头/夜戏/淡入淡出)的素材上,GPU 真正产出的黑帧会整批放行。
    /// 【邻域容差】引擎有前后帧缓冲、`-n` 又是均分时间步 ⇒ "输出帧号 → 源帧号"本就存在 ±1 偏移,
    /// 只看同号源帧会把"邻居是黑、自己差一点"的帧判成故障(真机因此把整段作废去跑慢一二十倍的 ONNX)。
    /// 判定规则本身在 AlhPro.Core.FrameInspect(有单测钉住)。</summary>
    private static bool DefectiveFramesAllComeFromNearBlack(string srcDir, System.Collections.Generic.List<string> defectiveFrames)
    {
        try
        {
            var verdicts = new System.Collections.Generic.List<bool>(defectiveFrames.Count);
            foreach (var f in defectiveFrames)
            {
                string baseName = Path.GetFileNameWithoutExtension(f);
                string src = Path.Combine(srcDir, baseName + ".jpg");
                if (!File.Exists(src)) src = Path.Combine(srcDir, baseName + ".png");
                verdicts.Add(AlhPro.Core.FrameInspect.IsFrameJustifiedByDarkSource(
                    SourceFrameJustifiesBlack(src),
                    SourceFrameJustifiesBlack(NeighborFrame(src, -1)),
                    SourceFrameJustifiesBlack(NeighborFrame(src, +1))));
            }
            return AlhPro.Core.FrameInspect.ShouldExemptAsSourceBlack(verdicts);
        }
        catch { return false; }
    }

    /// <summary>旧入口说明(2026-09-16 审计):本项目曾有一个只判"整帧近黑"的源帧入口,
    /// 它与输出侧的条带判据不对称 ⇒ 宽银幕/上下黑条素材必然误降级。该入口已删除,
    /// 源帧判据一律走 <see cref="IsFileDefective"/>(与输出侧 EngineService.IsBlackPngStrict 同源)。
    /// 这段说明保留历史原因,便于后人理解"为什么源帧侧必须判条带"。</summary>

    /// <summary>把源帧写进超分输出目录当"该批回退帧",并缩放到与同目录其他帧一致的尺寸。
    /// 【为什么必须缩放】直接 File.Copy 会让输出目录混进两种分辨率。实测(本机 ffmpeg):
    /// frame_%06d.jpg 序列里 64×64 与 320×240 混排 → 退出码 0、不报任何错,但容器按【第一帧】尺寸
    /// 声明流头,混进去的那些帧在播放器里是花的。用户只看到"成片某几秒画面异常",日志里查不到原因。
    /// 参考尺寸取目录里已写出的第一张可解码 JPG(那就是编码器要的统一尺寸);
    /// 一张都没有(本批是首个失败批)时按 源尺寸×倍数 推。</summary>
    private static void WriteFallbackFrame(string srcFile, string upOutputDir, double scale)
    {
        string dst = Path.Combine(upOutputDir, Path.GetFileName(srcFile));
        int w = 0, h = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(upOutputDir, "*.jpg"))
            {
                try
                {
                    if (new FileInfo(f).Length == 0) continue;
                    using var b = new System.Drawing.Bitmap(f);
                    if (b.Width > 0 && b.Height > 0) { w = b.Width; h = b.Height; break; }
                }
                catch { }
            }
        }
        catch { }
        if (w <= 0 || h <= 0)
        {
            int mul = Math.Max(1, (int)Math.Round(scale));
            try { using var s = new System.Drawing.Bitmap(srcFile); w = s.Width * mul; h = s.Height * mul; } catch { }
        }
        if (w > 0 && h > 0) { EngineService.ResizeImageTo(srcFile, dst, w, h); return; }
        try { File.Copy(srcFile, dst, true); } catch { }   // 连尺寸都读不出:尽力保帧号连续
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
            // 用最后一个 fps(流帧率在 stderr 末尾;编解码器信息行/文件名数字会误匹配)
            var fpsMs = System.Text.RegularExpressions.Regex.Matches(err, @"(\d+(?:\.\d+)?)\s*fps");
            var fpsM = fpsMs.Count > 0
                ? fpsMs[fpsMs.Count - 1]
                : System.Text.RegularExpressions.Regex.Matches(err, @"(\d+(?:\.\d+)?)\s*fps").Count > 0
                    ? System.Text.RegularExpressions.Regex.Matches(err, @"(\d+(?:\.\d+)?)\s*fps")[0]
                    : null;
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
                // 用【最后一个】fps 匹配:ffmpeg stderr 里编解码器信息(如 "25 fps")在流信息之前,
                // 文件含 "7fps" 之类也会被误匹配;流帧率行在末尾(与 ProbeFps 同口径)。
                var fpsMs = System.Text.RegularExpressions.Regex.Matches(err, @"(\d+(?:\.\d+)?)\s*fps");
                var fps = fpsMs.Count > 0 ? fpsMs[fpsMs.Count - 1].Groups[1].Value : "?";
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

    /// <summary>临时目录根:用户自定义(设置里可改,不可写自动回退)优先,否则剩余空间最大的本地盘。
    /// 8x 补帧 + 超分临时帧可达 30GB+,系统盘剩余不足时自动换盘,避免"处理到一半爆盘"。</summary>
    public static string PickTempRoot() => EngineService.TempRoot;

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

    /// <summary>【任务 U 补充】取某一路流的时长(秒);拿不到返回 0。
    /// 【为什么需要它】"音画滞后仍在几十 ms"这类要求必须**有可核验的数字**:成片容器时长只能说明画面,
    /// 音频流自己多长要单独问 ffprobe(容器 duration 取的是最长的流,两者不同才是滞后)。
    /// <paramref name="streamSelector"/> 形如 "a:0"(音频第 0 路)/"v:0"(视频)。
    /// 流级 duration 缺失(VFR/无 duration 标签)时回退 -show_format 的容器时长(如实按容器口径报,不编数)。</summary>
    private static async Task<double> ProbeStreamDurationSecondsAsync(string videoPath, string streamSelector, CancellationToken ct = default)
    {
        var ff = FfmpegPath;
        if (ff == null) return 0;
        var dir = Path.GetDirectoryName(ff);
        var ffprobe = dir != null ? Path.Combine(dir, "ffprobe.exe") : null;
        if (ffprobe == null || !File.Exists(ffprobe)) return 0;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            var path = AudioService.FfmpegSafePath(videoPath);
            var lines = await RunCaptureAsync(ffprobe,
                $"-v error -select_streams {streamSelector} -show_entries stream=duration -of csv=p=0 \"{path}\"", ct)
                .ConfigureAwait(false);
            foreach (var l in lines)
            {
                if (double.TryParse(l.Trim(), System.Globalization.NumberStyles.Float, inv, out double d) && d > 0)
                    return d;
            }
        }
        catch { }
        return 0;
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
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffprobe,
                // 【必须是带标签输出】真机实测:同一命令用 `-of csv=p=0` 时 ffprobe 把整行**打印两遍**,
                // `Trim().Split(',')` 的第 2 个字段因此混进换行与下一行内容("518\r\n\r\n24000/1001")
                // ⇒ long.TryParse 失败 ⇒ 本方法**永远返回 (0,0)**(真机日志「按 60 fps 应得 ? 帧(真实时长 0s)」)。
                // 带标签输出(`nb_read_frames=518`)按行取键值,行序/重复打印都不影响,见 Core.ProbeFields。
                Arguments = $"-v error -select_streams v:0 -count_frames " +
                            $"-show_entries stream=nb_read_frames,avg_frame_rate -of default=nw=1:nk=0 \"{videoPath}\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return (0, 0);
            var o = await p.StandardOutput.ReadToEndAsync();
            await p.StandardError.ReadToEndAsync();
            // 【修复】等待 ffprobe 退出加超时+取消:损坏/超大/特殊封装可能让 ffprobe 长期挂起,
            // 原先无超时无取消 → 视频管线永久挂起、取消无效。超时或取消即杀进程树并抛错。
            var exitTask = p.WaitForExitAsync(ct);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30), ct);
            var finished = await Task.WhenAny(exitTask, timeoutTask).ConfigureAwait(false);
            if (finished != exitTask || !p.HasExited)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                ct.ThrowIfCancellationRequested();   // 若因取消则抛取消
                throw new InvalidOperationException("ffprobe 等待超时(读帧率/帧数),疑似损坏/超大视频——已终止");
            }
            // 等待真正退出,确保后续读输出完整
            try { await exitTask.ConfigureAwait(false); } catch (OperationCanceledException) { throw; }
            var parsed = AlhPro.Core.ProbeFields.FramesAndContentDuration(o);
            if (parsed.frames > 0 && parsed.duration > 0.01) return parsed;
            AppLogger.Info($"帧数/内容时长探测失败:ffprobe 输出长度 {o.Length}(帧数或帧率缺失,返回 0 让调用方走兜底)");
        }
        catch (Exception ex)
        {
            // 原先是静默 `catch { }`:返回 (0,0) 却一条线索都没有,排查时只能看到"真实时长 0s"。现在留痕。
            AppLogger.Warn($"帧数/内容时长探测异常:{ex.GetType().Name}: {ex.Message}");
        }
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

    /// <summary>NVDEC(JPG 序列硬解)探测结果。**三态**:只有 <see cref="Usable"/> 才会启用硬解,
    /// 其余一律走软解(保守行为不变);分出 <see cref="ProbeTimeout"/> 只是为了【诊断能分清】
    /// "确定不支持"与"我们自己的 6 秒硬超时(结论未知)"。
    /// 【为什么必须分】真机日志里 8/8 次都只有一句
    /// `硬件解码探测:NVDEC(mjpeg_cuvid)不可用(The operation was canceled.)` —— 而那句
    /// `The operation was canceled.` 是 .NET `OperationCanceledException` 的默认文案,来自
    /// `VideoService.RunAsync` 里 `if (ct.IsCancellationRequested) throw new OperationCanceledException();`
    /// 即【我们自己的 6 秒超时】,不是 ffmpeg/驱动报"不支持"。旧写法把它写成"不可用",于是读日志的人
    /// (包括我们自己)会把"探测超时"当成"本机不支持硬解" —— 结论方向就错了。</summary>
    private enum HwJpegDecode
    {
        /// <summary>还没探(本机没有可用 NVENC 时根本不会探;或探测被外层取消打断)。</summary>
        NotProbed,
        /// <summary>实测通过:8 帧 JPG 序列真的解出来并编出有效文件(唯一会启用硬解的状态)。</summary>
        Usable,
        /// <summary>确定不可用:命令真跑完但报错/输出无效(exit≠0、输出解不开、帧数不足等)。</summary>
        Unsupported,
        /// <summary>探测超时(6 秒硬超时被触发,进程被我们杀掉):**结论未知** —— 既不能当支持,也不能当不支持。</summary>
        ProbeTimeout,
    }

    /// <summary>本会话 NVDEC(JPG 序列硬解)的探测结论(三态,见 <see cref="HwJpegDecode"/> 的说明)。
    /// 【为什么必须有前提】实测(2026-09-11,200 帧 4K JPG 序列):软编时瓶颈是编码器本身——
    /// 软件解码 39.6 fps / NVDEC 4595 fps,但 `libx264 veryfast` 端到端只有 34.5→35.1 fps(+2%,等于没用);
    /// 而硬编(NVENC)生效后瓶颈才会转移到软件解码 JPG,此时 NVDEC 才有价值(调研实测 86.5→131.7 fps,+52%)。
    /// ⚠【任务 O5 · 2026-09-13 真机基准:上面这组数字**本机无法复现,勿再引用**】
    ///   同机同条件重测(生产帧 4:2:0):**cuvid 192.68 fps vs 软解 193.99 fps** —— 硬解**没有收益**;
    ///   4595 fps / +52% 在本机复现不出来(不排除是别的机器/别的素材/别的 pix_fmt 下的旧数据)。
    ///   **结论:维持"不启用硬解"**;下面这条探测链路继续保留(它保证"能过才用、错判只掉性能不掉结果"),
    ///   但**不再据它主张任何性能收益**。另:探测素材必须与生产一致用 4:2:0 —— 4:4:4 时 cuvid 会无限重试挂死
    ///   (实测 120 秒 0 帧、stderr 长到 46MB),所以 6 秒超时判"不可用"是**正确动作**,不是误判。
    /// 所以只在【已有可用 nvenc】时才探测;探测口径与硬编一致:用真实解码器+真实编码参数真编出有效文件。
    /// 【消费点口径一律不变】只有 `== Usable` 才加 `-c:v mjpeg_cuvid`(合帧)、才在耗时拆分里标"NVDEC 硬解";
    /// 其余三态都是"走软解",与改动前的 bool=false 完全一致。</summary>
    private static HwJpegDecode _hwJpegDecode = HwJpegDecode.NotProbed;

    /// <summary>某个硬件编码器实测可用的【调用配方】:用哪个 ffmpeg + 是否必须去掉 -preset。
    /// 只记"哪个编码器能用"是不够的:同一张卡在不同 ffmpeg 上的 NVENC 支持不同(旧 ffmpeg 打不开的 NVENC,
    /// 备用 ffmpeg 8.x 能打开 —— 实测 572 驱动下主 ffmpeg 直接报"需要 610 以上")。
    /// 【"50 系失败 = -preset p4 被拒"这条因果已被否定(2026-09-12 自检按 RESEARCH_SPEED §10.3 #18 修正)】
    /// 真实机制是 NVENC API 版本与驱动版本不匹配(Required 13.1 / Found 13.0),**去掉 preset 照样失败**。
    /// "去掉 preset 再试一档"保留为历史兼容尝试(成本极低,万一有机器只认它),但别再把当它成 50 系的配方。</summary>
    private sealed record HwEncoderRecipe(string Ffmpeg, bool NoPreset);
    private static readonly System.Collections.Generic.Dictionary<string, HwEncoderRecipe> HwRecipes = new();

    /// <summary>去掉 -preset 档(nvenc 专用:amf 用 -quality、qsv 根本没有 -preset,替换是空操作)。</summary>
    private static string StripPreset(string args) =>
        System.Text.RegularExpressions.Regex.Replace(args, @"\s+-preset\s+\w+", "");

    /// <summary>取探测期定下的调用配方;没探到(或已判坏)返回 null,调用方直接走 CPU。</summary>
    private static HwEncoderRecipe? GetHwRecipe(string encoder)
    {
        lock (_hwLock) return HwRecipes.TryGetValue(encoder, out var r) ? r : null;
    }

    /// <summary>最近一次选择的视频压缩编码器描述(供界面/日志展示,不靠猜)。</summary>
    public static string LastVideoEncoderInfo { get; private set; } = "libx264 (CPU 软编)";

    /// <summary>本次任务里"画面后处理:边缘抗锯齿"这一步的耗时(秒)。它现在跑在帧处理阶段(C# 按帧并行),
    /// 不再混进"编码/封装"里 —— 日志要能一眼分出瓶颈在哪(2026-09-12 实测:ffmpeg sab 在 4K 下
    /// 与其它滤镜串起来是 4.88 秒/帧,去掉它只要 0.10 秒/帧,这项拆分就是为了让这种事下次一眼可见)。</summary>
    public static double LastFramePostProcSeconds { get; private set; }

    /// <summary>最近一次"整理帧(JPG)"(把引擎输出的 PNG 整批重编码成 JPG,降临时盘)这一步的耗时(秒)与帧数。
    /// 【为什么要记】这一步耗时正比于帧数(用户那条 2668 帧要跑几十秒~几分钟),过去既无进度也不计时:
    /// 用户看到补帧到 2667/2668 后"卡很久",ETS 还显示"预计还剩几秒"(阶段内 ETA 只按补帧引擎的产出外推,
    /// 不知道后面还有这一步)。现在有进度文案(整理帧(JPG) 第 N / M 帧)+ 这组数字可核对。
    /// 口径:它是"处理阶段"墙钟的一部分(一直都算在里面),只是过去没有任何地方报出来。</summary>
    public static double LastFrameReencodeSeconds { get; private set; }

    /// <summary>最近一次"整理帧(JPG)"处理的帧数(0 = 该目录本来就没有 PNG,整段空跑)。</summary>
    public static int LastFrameReencodeFrames { get; private set; }

    /// <summary>最近一次合帧里【后处理滤镜链本身】的每帧成本(秒/帧,抽样实测;已减掉 JPG 解码那份)。
    /// 用途:诊断"这条片子慢在滤镜还是编码"—— 2026-09-13 之前报的数里混着解码,会误导优化方向。</summary>
    public static double LastPostFilterCostPerFrame { get; private set; }

    /// <summary>最近一次合帧里【JPG 序列解码】的每帧成本(秒/帧,抽样实测;NVDEC 硬解还是软件解码见日志)。</summary>
    public static double LastJpegDecodeCostPerFrame { get; private set; }

    /// <summary>抽样实测"解码 JPG 序列"与"后处理滤镜链"各自的每帧成本(只做 解码→滤镜→丢弃:
    /// 不编码、不落盘、不写文件)。抽 6 帧即可外推到全片 —— 目的是把"编码/封装"这个数里混着的
    /// 【解码】与【滤镜】分别摊开,而不是精确到毫秒。返回 (解码+滤镜, 纯解码) 秒/帧;链为空或失败返回 (-1,-1)。
    /// 【为什么必须分开测(2026-09-13)】本方法原先只测"带链跑一遍",而那条命令是【软件解码 4K JPEG】——
    /// 于是报出来的"后处理滤镜 X 秒/帧"里含着解码成本,用户会照着这个数去优化滤镜,方向就跑偏了
    /// (用户实测:3420 帧 4K 作业报"后处理滤镜 0.084 秒/帧 ≈ 288.6 秒",其中相当一部分其实是
    ///  【软件 mjpeg 解码】—— 本文件上方注释记录过 4K 软件解码 ≈39.6 fps,即约 25 ms/帧)。
    /// 现在跑两遍:带链(解码+滤镜)减去不带链(纯解码)= 真正的滤镜成本;hwJpegDecode 传 true 时
    /// 两遍都用与合帧相同的 `-c:v mjpeg_cuvid`(保持"抽样的解码路径 = 真实合帧的解码路径")。
    /// 【为什么需要它】2026-09-12 实测:4K 下整条 6 档滤镜链 4.88 秒/帧、去掉 sab 只剩 0.10 秒/帧,
    /// 而日志当时只写"编码/封装 X 秒",于是"硬编只有 1fps"的假象持续了很久。</summary>
    private static async Task<(double full, double decode)> SampleFilterChainCostPerFrameAsync(string ffmpegExe,
        string framePattern, string chain, double fps, bool hwJpegDecode, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(chain)) return (-1, -1);
        string hw = hwJpegDecode ? "-c:v mjpeg_cuvid " : "";
        string fpsArg = fps.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
        async Task<double> Measure(string vf)
        {
            try
            {
                const int n = 6;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await RunAsync(ffmpegExe,
                    $"-nostdin -y -v error {hw}-framerate {fpsArg} " +
                    $"-start_number 1 -i \"{framePattern}\" -frames:v {n}{vf} -f null -",
                    null, ct);
                sw.Stop();
                return sw.Elapsed.TotalSeconds / n;
            }
            catch (OperationCanceledException) { throw; }
            catch { return -1; }
        }
        double full = await Measure($" -vf \"{chain}\"");
        if (full < 0) return (-1, -1);
        // 纯解码:同一路输入、同一次数,只是不过滤镜链(-f null 仍会把帧解出来,滤镜为空不影响解码发生)
        double decode = await Measure("");
        return (full, decode);
    }

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

    /// <summary>当前是否处于"用户暂停=子进程已冻结"状态。
    /// 冻结态与僵死态从外部无法区分(进程存活、零输出、不退出),故看门狗必须读它来喂狗,
    /// 否则用户暂停久了会被误判挂死并杀掉任务。</summary>
    internal static bool IsPaused;

    /// <summary>冻结全部当前子进程(暂停生效:进程立即停止计算,占用释放给其他程序)。仅在进程仍在运行时生效。</summary>
    internal static void SuspendActiveProcess()
    {
        IsPaused = true;
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
        IsPaused = false;
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
        // 依优先级探测;能真编出一帧【有效】文件才算可用(驱动过老/无对应硬件会失败被跳过)。
        // H.264 与 H.265(hevc)各探一遍,方便用户选编码格式时直接给出可用的。
        foreach (var enc in new[] { "h264_nvenc", "h264_amf", "h264_qsv", "hevc_nvenc", "hevc_amf", "hevc_qsv" })
        {
            if (ct.IsCancellationRequested)
            {
                // 【闩锁必须放开(2026-09-12 自检发现)】原来不论探没探完都把 _hwProbed 永久置 true:
                // 用户在中途点了停止(或任务被取消)时,循环立刻 return,WorkingHwEncoders 还是空的,
                // 而闩锁已经封死 —— **本会话之后所有任务都会静默走 CPU 软编**(用户只看到"变慢/没有可用硬编",
                // 日志里也没有任何线索,必须重启软件才恢复)。
                // 取消 = "没探完",不算数:把闩锁放开,下一个任务重新探一次。
                lock (_hwLock) { _hwProbed = false; }
                return;
            }
            // 【关键】探测参数 = 真实编码参数(EncoderArgs 里的 -preset/-pix_fmt/-cq 全带上)。
            // 原先只给 `-c:v {enc}`,探的是"这台机器有没有这个编码器",而真实命令要问的是
            // "这套参数在这台机器上能不能编" —— 两者不等价,差集就得靠整片重跑试出来。
            var realArgs = EncoderArgs(enc);
            bool nvenc = enc.Contains("nvenc", StringComparison.OrdinalIgnoreCase);
            // 逐级放宽,第一个成功的组合就是要记的配方:
            //   ① 主 ffmpeg + 真实参数
            //   ② 主 ffmpeg + 去掉 -preset(50 系常见:-preset p4 被拒 exit -22,去掉就能硬编)
            //   ③④ 备用 ffmpeg(8.x,对 Blackwell 的 NVENC 适配更全)+ 上述两种参数
            // ②③④ 只对 nvenc 有意义:-preset 是 nvenc 独有(amf 用 -quality、qsv 没有,替换是空操作),
            // 备用 ffmpeg 也只为更新版的 NVENC 而存在,给它探 amf/qsv 纯属白跑。
            var attempts = new System.Collections.Generic.List<(string Ff, string Args, bool NoPreset)>
                { (ffmpeg, realArgs, false) };
            if (nvenc)
            {
                attempts.Add((ffmpeg, StripPreset(realArgs), true));
                var bak = BackupFfmpegPath;
                if (bak != null)
                {
                    attempts.Add((bak, realArgs, false));
                    attempts.Add((bak, StripPreset(realArgs), true));
                }
            }
            foreach (var (ff, args, noPreset) in attempts)
            {
                if (ct.IsCancellationRequested) return;
                var tmp = Path.Combine(EngineService.TempRoot, $"imgup_encprobe_{enc}_{Guid.NewGuid():N}.mp4");
                try
                {
                    // 探测分辨率 1280×720:之前 320×240 太小,部分编码器(QSV/核显硬编)小图能编出有效文件,
                    // 但真实大分辨率视频下却输出无效文件(用户① RTX2070+核显双卡机实测:qsv 探测可用,合帧却黑屏/失败)。
                    // nvenc 也拒绝过小分辨率(64x64 报 incorrect parameters)。
                    await RunAsync(ff,
                        // 探测要多编几帧(≥5):ValidateVideoFileAsync 对 <5 帧判无效(防 QSV 假成功)。
                        // 原 `-frames:v 1` 只产 1 帧 → 探测恒失败 → 硬编被静默禁用(全走 CPU 软编),等于速度回归。
                        // 改 rate=30 duration=0.4(≈12 帧)且不截断到 1 帧,既快又通过校验。
                        $"-y -f lavfi -i \"testsrc=size=1280x720:rate=30:duration=0.4\" {args} \"{tmp}\"",
                        null, ct);
                    // 不只看"文件非 0 字节":QSV 那类会写出非空但解不开的文件,必须真校验一遍
                    if (await ValidateVideoFileAsync(tmp, 5))
                    {
                        lock (_hwLock)
                        {
                            if (!WorkingHwEncoders.Contains(enc)) WorkingHwEncoders.Add(enc);
                            HwRecipes[enc] = new HwEncoderRecipe(ff, noPreset);
                        }
                        if (noPreset || !ReferenceEquals(ff, ffmpeg))
                            AppLogger.Info($"硬件编码探测:{enc} 需放宽参数才可用(" +
                                $"{(noPreset ? "去掉 -preset" : "")}{(noPreset && !ReferenceEquals(ff, ffmpeg) ? " + " : "")}" +
                                $"{(!ReferenceEquals(ff, ffmpeg) ? "备用 ffmpeg" : "")})");
                        break;
                    }
                }
                catch { /* 该组合在这台机器不可用,继续放宽 */ }
                finally { try { File.Delete(tmp); } catch { } }
            }
        }
        // ===== NVDEC(JPG 序列硬件解码)探测 =====
        // 只在【已有可用 nvenc】时才探:软编时瓶颈是编码器本身,硬解救不了(见 HwJpegDecode 的实测)。
        // 【2026-09-12 加固,三个改动都是踩过的坑】
        //  ① 用【真会去编码的那个 ffmpeg】探(encFfmpeg 的配方来源),而不是永远用主 ffmpeg ——
        //     主 ffmpeg 能过、实际编码用的是备用 ffmpeg8 时,探测结论对不上真实路径。
        //  ② 从"1 帧图 + 1 帧解码"改成【8 帧序列 + 必须解出 8 帧】—— 单帧探不出"多帧序列下 cuvid 卡住"。
        //     实测:ffmpeg8 + h264_nvenc + `-c:v mjpeg_cuvid` 解 1080p JPG **序列**,20 帧的命令永不结束(挂到超时强杀),
        //     而同命令去掉 cuvid 0.67 秒完成。单帧探测对这种形态完全无感。
        //  ③ 探测本身加【硬超时】:即使某台机器真被 cuvid 卡住,也只损失这几秒并把该路径判为不可用,
        //     绝不会让整个视频任务无声挂死(以前的写法一旦挂住就是整任务不动)。
        //     【超时值以代码为准 = 6 秒】本段早先写过"30 秒",与下面 CancelAfter(6) 自相矛盾 —— 2026-09-12 自检统一:
        //     正常 NVDEC 解 8 帧 720p 连 1 秒都用不到,6 秒已是 6 倍余量;真卡的机器多等 24 秒毫无收益。
        try
        {
            if (!ct.IsCancellationRequested && WorkingHwEncoders.Any(e => e.Contains("nvenc", StringComparison.OrdinalIgnoreCase)))
            {
                var nvEnc = WorkingHwEncoders.First(e => e.Contains("nvenc", StringComparison.OrdinalIgnoreCase));
                // 用该硬编配方对应的 ffmpeg(与真实编码一致)
                string decFfmpeg = GetHwRecipe(nvEnc)?.Ffmpeg ?? ffmpeg;
                var probeJpgPattern = Path.Combine(EngineService.TempRoot, $"imgup_decprobe_{Guid.NewGuid():N}_%03d.jpg");
                var probeOut = Path.Combine(EngineService.TempRoot, $"imgup_decprobe_{Guid.NewGuid():N}.mp4");
                try
                {
                    // 【任务 O5 · 2026-09-13】探测素材必须与生产一致用 **4:2:0**:
                    // 生产 JPG 帧序列是 4:2:0(拆帧/超分输出都是 yuv420p 系),而 ffmpeg mjpeg 编码器在
                    // 不给 -pix_fmt 时对 testsrc 会选 4:4:4 —— 实测 **4:4:4 + cuvid 会无限重试挂死**
                    // (120 秒 0 帧、stderr 长到 46MB),于是"6 秒超时判不可用"看起来像误判,其实是探测素材选错了。
                    // 现在显式钉成 yuv420p,与真实合帧的解码路径同口径(超时判定的含义也就随之正确)。
                    await RunAsync(decFfmpeg,
                        $"-y -f lavfi -i \"testsrc=size=1280x720:rate=30:duration=0.4\" -frames:v 8 -q:v 2 -pix_fmt yuv420p \"{probeJpgPattern}\"", null, ct);
                    var probeOne = probeJpgPattern.Replace("_%03d.jpg", "_001.jpg");
                    if (File.Exists(probeOne) && new FileInfo(probeOne).Length > 0)
                    {
                        using var decCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        // 超时 6 秒(不是 30):NVDEC 正常工作时 8 帧 720p 连 1 秒都用不到;
                        // 实测某些机器(本机 ffmpeg8 + mjpeg_cuvid 解 JPG 序列)会**永久挂住**,
                        // 6 秒足够判死,不至于让每次启动都白等半分钟。超时 = 判不可用,绝不挂死任务。
                        decCts.CancelAfter(TimeSpan.FromSeconds(6));
                        bool decodedOk = false;
                        try
                        {
                            // 【TODO(只记录,未实施 · 2026-09-13 调查结论)】探测命令与【真实合帧命令】有两处不一致,
                            // 真机复测硬解时必须按这两条去找原因(改它们前必须先有实测,否则会把"探测误判"变成"合帧挂死"):
                            //   ① 编码参数【没套配方】:这里用 `EncoderArgs(nvEnc)`(含 -preset p4),而真实合帧用的是
                            //      `encMuxArgs = recipe?.NoPreset == true ? StripPreset(muxArgs) : muxArgs`(见本文件合帧处)。
                            //      → 在"需放宽参数才可用(备用 ffmpeg)"的机器上,探测编的参数 ≠ 真实编的参数,探测更易失败。
                            //   ② 输入形态不完全一致:这里多了 `-f image2 -start_number 1`,且固定 720p/30fps/8 帧;
                            //      真实合帧是 `-framerate {fr} -i "frame_%06d.jpg"`(见 muxInput)。→ "序列形态"确实是要探的
                            //      (实测 20 帧 cuvid 命令永不结束),但固定小分辨率/固定帧数是否等价,未验证。
                            //   真机手工复测三步(只计时,不产成品;机器空闲时做):
                            //     a) 纯解码形态:`ffmpeg -v error -framerate 30 -c:v mjpeg_cuvid -i "帧目录\frame_%06d.jpg"
                            //        -frames:v 200 -f null -` → 这条就卡 = cuvid+image2 序列在本机确实不可用;
                            //     b) 组合形态:把 a) 的 `-f null -` 换成「真实 nvenc 编码参数 → mp4, -frames:v 8」→
                            //        a) 快 b) 卡 = 卡在"cuvid 解码 + NVENC 编码同进程"这一组合(与是否解码无关);
                            //     c) 用 `-progress pipe:1` 记"首帧到首输出"的时间 → 判断 6 秒上限是否过紧。
                            await RunAsync(decFfmpeg,
                                $"-y -c:v mjpeg_cuvid -f image2 -framerate 30 -start_number 1 -i \"{probeJpgPattern}\" " +
                                $"-frames:v 8 {EncoderArgs(nvEnc)} \"{probeOut}\"", null, decCts.Token);
                            decodedOk = await ValidateVideoFileAsync(probeOut, 8);
                        }
                        catch (Exception ex) when (!ct.IsCancellationRequested)
                        {
                            // 【I · 2026-09-13 诊断分清两种失败】本 catch 只排除【外层 ct】(用户取消/停止),
                            // 所以走到这里且 decCts 已到期 = **我们自己的 6 秒硬超时**;否则是命令真跑失败。
                            // 旧代码一律写成"不可用(The operation was canceled.)",读日志的人只会得到
                            // "本机不支持硬解"这个【错误结论】(真机 8/8 次都是这种)。
                            bool probeTimedOut = decCts.IsCancellationRequested;
                            string head = ex.Message.Split('\n')[0];
                            if (probeTimedOut)
                            {
                                _hwJpegDecode = HwJpegDecode.ProbeTimeout;
                                AppLogger.Warn($"硬件解码探测:NVDEC(mjpeg_cuvid)【探测超时(6 秒)未完成 —— 不得解读为"
                                    + $"「本机不支持硬解」】({head})。本次按保守处理:合帧走软件解码"
                                    + "(软解 4K 约 25 ms/帧 ≈39.6 fps)。"
                                    + "【2026-09-13 更正】旧文案这里写「历史实测硬解可到 4595 fps、端到端 +52%」——"
                                    + "该数字**本机同条件重测复现不出来**(cuvid 192.68 fps vs 软解 193.99 fps,硬解无收益),"
                                    + "故本机不主张硬解收益;超时只是『没拿到结论』,按软解继续不影响结果。"
                                    + "要确认这台机器到底行不行,必须真机手工复测 —— 见本探测处的 TODO(探测命令与真实合帧命令"
                                    + "还有两处不一致)。");
                            }
                            else
                            {
                                _hwJpegDecode = HwJpegDecode.Unsupported;
                                AppLogger.Info($"硬件解码探测:NVDEC(mjpeg_cuvid)不可用({head})—— 合帧走软件解码(不影响结果,只影响速度)");
                            }
                        }
                        if (decodedOk)
                        {
                            _hwJpegDecode = HwJpegDecode.Usable;
                            AppLogger.Info($"硬件解码探测:NVDEC(mjpeg_cuvid)可用(8 帧序列实测通过)—— 合帧将用硬件解码 JPG 序列({nvEnc})");
                        }
                        else if (_hwJpegDecode == HwJpegDecode.NotProbed)
                        {
                            // 命令没抛异常但输出不合格(0 帧/解不开/帧数不足)→ 明确记"确定不可用"。
                            // 旧代码这条路径【一行日志都不打】,诊断时看不出探过没探过。
                            _hwJpegDecode = HwJpegDecode.Unsupported;
                            AppLogger.Info("硬件解码探测:NVDEC(mjpeg_cuvid)不可用(命令跑完但输出不合格:0 帧/解不开/帧数不足)"
                                + "—— 合帧走软件解码(不影响结果,只影响速度)");
                        }
                    }
                }
                finally
                {
                    try { File.Delete(probeOut); } catch { }
                    try
                    {
                        foreach (var g in Directory.EnumerateFiles(EngineService.TempRoot, Path.GetFileName(probeJpgPattern).Replace("%03d", "*")))
                            File.Delete(g);
                    }
                    catch { }
                }
            }
        }
        catch { /* 探测过程本身异常 = 结论未知:保持 NotProbed(与"保守走软解"一致),合帧照旧走软件解码 */ }
        // 诊断:记录本机可用/不可用的硬件编码器(排查"为什么没走 GPU 编码"一眼可见)
        lock (_hwLock)
        {
            AppLogger.Info("硬件编码器探测:" + (WorkingHwEncoders.Count > 0
                ? "可用 [" + string.Join(", ", WorkingHwEncoders) + "]"
                : "全部不可用(将用 CPU 软编)"));
            // 【I · 2026-09-13】NVDEC(JPG 序列硬解)探测结论也按【三态】写一行 —— 这样诊断包里一眼能分清
            // "没探(本机无可用 NVENC)"、"确定不支持"、"探测超时(结论未知)"与"可用",不必再靠读异常文案猜。
            AppLogger.Info("JPG 序列硬解码(NVDEC)探测结论:" + _hwJpegDecode switch
            {
                HwJpegDecode.Usable => "可用(合帧将用 -c:v mjpeg_cuvid)",
                HwJpegDecode.Unsupported => "确定不可用(命令报错或输出不合格)→ 合帧走软件解码",
                HwJpegDecode.ProbeTimeout => "探测超时(6 秒未完成,【结论未知,不等于不支持】)→ 合帧走软件解码",
                _ => "未探测(本机没有可用 NVENC,或探测被取消)→ 合帧走软件解码",
            });
            // 【备用 ffmpeg 缺失要说清】内置主 ffmpeg 需要 NVIDIA 驱动 ≥610 才能开 NVENC;
            // 驱动较旧(实测 572.83)的机器是靠 engines\ffmpeg8\ffmpeg.exe 这个备用包兜住的。
            // 而 engines\ 是 gitignore、deploy.ps1 也不同步 —— 漏拷一次,硬编就静默消失,用户只会觉得"变慢了"。
            if (WorkingHwEncoders.Count == 0 && BackupFfmpegPath == null)
            {
                AppLogger.Warn("⚠ 硬件编码不可用,且未找到备用 ffmpeg(engines\\ffmpeg8\\ffmpeg.exe)—— 本机将使用 CPU 软编(明显更慢)。"
                    + "若显卡支持硬编:补齐该备用 ffmpeg,或把 NVIDIA 驱动更新到 610 以上(内置 ffmpeg 的 NVENC 需要 610+)。");
            }
            else if (WorkingHwEncoders.Count > 0 && BackupFfmpegPath == null)
            {
                AppLogger.Info("硬件编码可用(未用到备用 ffmpeg)。注:若哪天换到驱动较旧/较新的机器上主 ffmpeg 开不了 NVENC,"
                    + "需要 engines\\ffmpeg8\\ffmpeg.exe 兜底,当前未找到该文件。");
            }
        }
    }

    /// <summary>硬件编码是否因【显卡驱动过旧】而不可用(ffmpeg 的 nvenc 需较新版 NVIDIA 驱动 ≥610.00 / nvenc API 13.1;
    /// 用户驱动旧则报 "Driver does not support the required nvenc API version" / "minimum required Nvidia driver ... 610.00")。
    /// 单挑出来只为提示语:这是硬编失败里唯一一种用户自己能解决的(更新驱动),笼统报"不可用"等于让他错过修复机会。</summary>
    private static bool IsNvencDriverTooOld(string msg)
    {
        if (string.IsNullOrEmpty(msg)) return false;
        return msg.Contains("nvenc API version", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("minimum required Nvidia driver", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Driver does not support the required nvenc", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("610.00", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>按"实测可用"自适应选视频压缩编码器:优先厂商匹配的硬编,其次任一可用硬编,最后 libx264。
    /// codecPref:0=自动(H.264 优先) 1=强制 H.264 2=优先 H.265(hevc,更省空间,老设备可能播不了)。</summary>
    private static string PickVideoEncoder(int gpuId, int codecPref = 0)
    {
        // 按引擎真实 -g 编号取显卡名选硬件编码器;不能用注册表顺序索引(AMD 核显+NVIDIA 独显双卡机上顺序相反)。
        var name = GpuInfo.GetEngineDeviceName(gpuId);
        string h264Vendor = "", hevcVendor = "";
        if (gpuId >= 0 && name.Length > 0)
        {
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
                // H.265:优先厂商匹配的 hevc 硬编(但 qsv 降级——双卡机/集成显卡上易输出无效文件),其次任一可用 hevc 硬编,最后 libx265
                if (hevcVendor.Length > 0 && hevcVendor != "hevc_qsv" && WorkingHwEncoders.Contains(hevcVendor)) chosen = hevcVendor;
                else if (WorkingHwEncoders.Any(e => e.StartsWith("hevc", StringComparison.OrdinalIgnoreCase) && e != "hevc_qsv"))
                    chosen = WorkingHwEncoders.First(e => e.StartsWith("hevc", StringComparison.OrdinalIgnoreCase) && e != "hevc_qsv");
                else if (WorkingHwEncoders.Contains("hevc_qsv")) chosen = "hevc_qsv";
                else chosen = "libx265";
            }
            // H.264:优先厂商匹配硬编;但 qsv(Intel 集显)在双卡机上易输出无效文件(用户① RTX2070+核显实测黑屏),
            // 故 qsv 不作为首选,仅当无任何其它可用硬编时才兜底(避免"匹配到 Intel → 选 QSV → 合成黑屏")。
            else if (h264Vendor.Length > 0 && h264Vendor != "h264_qsv" && WorkingHwEncoders.Contains(h264Vendor)) chosen = h264Vendor;
            else if (WorkingHwEncoders.Any(e => e.StartsWith("h264", StringComparison.OrdinalIgnoreCase) && e != "h264_qsv"))
                chosen = WorkingHwEncoders.First(e => e.StartsWith("h264", StringComparison.OrdinalIgnoreCase) && e != "h264_qsv");
            else if (WorkingHwEncoders.Contains("h264_qsv")) chosen = "h264_qsv";
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

    /// <summary>本会话各视频编码 GPU 硬解(d3d11va)已验证不可用的集合(按编码器区分,如 h264/hevc/av1)。
    /// 某一种编码硬解不了(如旧卡硬解 AV1)只禁用该编码,其它编码仍优先硬解,不再"一次失败、全会话软解"。</summary>
    private static readonly System.Collections.Generic.HashSet<string> _hwDecodeBrokenCodecs = new();

    /// <summary>探测输入视频的编码器名(如 h264/hevc/av1),用于区分硬解可用性;失败/探不出返回 ""。</summary>
    private static async Task<string> ProbeVideoCodecName(string ffmpeg, string video, CancellationToken ct)
    {
        try
        {
            string? dir = Path.GetDirectoryName(ffmpeg);
            string? fp = dir != null ? Path.Combine(dir, "ffprobe.exe") : null;
            if (fp == null || !File.Exists(fp)) return "";
            var lines = await RunCaptureAsync(fp,
                $"-v error -select_streams v:0 -show_entries stream=codec_name -of csv=p=0 \"{video}\"", ct);
            return lines.Count > 0 ? lines[0].Trim() : "";
        }
        catch { return ""; }
    }

    /// <summary>拆帧(优先 GPU 硬解 d3d11va,失败自动回退软解):既省 CPU 又提速。
    /// vfExpr=滤镜表达式(如 scale...);返回实际拆出的帧数。</summary>
    // ===== 视频中间帧统一 JPG(降临时盘)=====
    // 流水线所有中间帧(ffmpeg 拆帧 / 超分后 / 缩放后)统一存为 .jpg。
    // 【2026-09-11 改】超分那一段不再"引擎写 PNG → 应用侧重编码 q96":改为让 ncnn 引擎直出 JPG
    // (`-f jpg`,见 EngineService.UpscaleDirAsync 的 outFormat)——4K 实测 2.98→2.02 秒/帧(省 31%,
    // 因保存线程仅 1 个时 PNG 压缩压不住 GPU),且省掉应用侧整段 PNG 解码+q96 重编码,引擎写的是 q100,画质更好。
    // 补帧(rife)仍写 PNG,在应用侧取回后重编码成 JPG(该段后续可同样改为引擎直出)。
    // 仅列帧(枚举 .png/.jpg 均可),供"格式随路径变化"的读取点使用(去重/缩放/黑帧守卫)。
    private static System.Collections.Generic.IEnumerable<string> EnumerateFrameFiles(string dir)
        => Directory.EnumerateFiles(dir, "*.*").Where(f =>
            f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase));

    // 把 dir 里的 .png 帧重编码成同名 .jpg(应用侧统一 JPG,降低临时盘)。目录已是 JPG/无 PNG 则空跑。
    // 单帧重编码失败时保留原帧内容(复制改名),保证帧号连续可解码、合帧不中断。
    // 中间帧 JPG 质量保持 0.96(近无损,画质优先;不降低以免影响最终成片效果)。
    private const float VideoFrameJpgQuality = 0.96f;

    /// <summary>把帧目录里的中间帧统一成 JPG(降临时盘),可选顺带做「边缘抗锯齿」。
    /// <param name="edgeSmooth">0 = 只转格式;1~100 = 在写 JPG 前对同一张位图做一次抗锯齿(与图片页同语义)。
    /// 【为什么抗锯齿挪到这里 + 并行】见 EngineService.ConvertPngToJpg 的注释:ffmpeg 的 sab 滤镜在 4K 上
    /// 与其它滤镜串起来是 4.88 秒/帧,去掉它只要 0.10 秒/帧。这里用与图片页同一套 C# 实现,
    /// 并**按帧并行**(每帧独立),再与本来就要做的 PNG→JPG 合并成一次编码,不额外多一代 JPG 损失。
    /// 目录里本来就是 JPG 的帧(未超分/未补帧等分支)也会吃到这一档,避免同一开关在不同分支下效果不一致。</param>
    /// 【每帧到底在算什么(2026-09-13 逐行核对,供后续提速对照)】
    ///   A. PNG 输入(引擎写 PNG 的分支)→ EngineService.ConvertPngToJpg(png,jpg,q,out _,aa):
    ///      ① new Bitmap(png) = PNG 解码一次;② 黑帧采样(ForEachSample 抽样,不是全图 GetPixel);
    ///      ③ ApplyEdgeSmoothInMemory:LockBits(32bppArgb)→ 拆 3 个通道平面(3×w×h 字节)
    ///         → 每通道一次 3×3 边缘平滑 → 合回 32bpp;④ SaveJpegViaGdi 再拷一张 24bppRgb(GDI+ DrawImage);
    ///      ⑤ GDI+ JPEG 编码。全流程只有一次解码、一次编码(AA 搭在中间,不多一代损失)。
    ///   B. JPG 输入(引擎直出 JPG,视频正常路径)→ EngineService.ApplyEdgeSmoothToJpeg:
    ///      ① new Bitmap(jpg) = JPEG 解码一次;② 同 A 的 ③/④/⑤(AA + 24bpp 拷贝 + 编码);③ 落盘。
    ///      旧落盘是"写 原文件.aa.jpg 再 File.Copy 覆盖"(同一份 JPG 多一轮全文件读+写),
    ///      2026-09-13 改成"内存里编完再一次性写回原路径"(见 EngineService.ApplyEdgeSmoothToJpeg)。
    ///   本步骤【不】额外做的:黑帧判定只在 A 分支顺带做(B 分支不为查黑再解码一遍)。
    /// 【已知可再提速但会改画质语义 → 只列方案、本次未改】把 AA 挪到超分/缩放【之前】(在源分辨率上做):
    ///   单帧成本随面积下降明显,但"放大前削锯齿"与"放大后削锯齿"是两种画面,须用户看对比图再定。
    private static int ReencodeDirPngToJpg(string dir, int edgeSmooth,
        IProgress<(int pct, string msg)>? progress, CancellationToken ct, int curPct)
    {
        // 【任务 N · 2026-09-13 回答"真机上为什么一条『整理帧(JPG)』都没有"】
        // 这条进度只在【待整理目录里真的存在 .png】时才会发(核心循环只遍历 *.png);
        // 而引擎正常路径都是"写入时就地转 JPG"(超分 :1969-1973、补帧 :3382-3384、补回 :5748),
        // 三个调用点(旧顺序补帧输出、超分输出兜底、合帧前统一)因此基本都是空跑 ——
        // 整条视频 0 条"整理帧"日志是**正常**的,不是日志丢了。
        // 唯一能让 PNG 留在目录里的是"某帧就地转换失败 → File.Copy 原 PNG 兜底"(:1983)、
        // 以及 AA 开启时"已是 JPG 的帧走 AA"那条(那条文案是「边缘抗锯齿 已处理 N 帧」,不是这两处)。
        // curPct = 【edgeSmooth == 0 时】这条整理步骤要"挂在"哪个进度百分比上(只换文字,不推进也不回退):
        //   这一步没有自己的进度区间,硬塞一个区间会与相邻阶段打架(见 ReencodeDirPngToJpgCore 的说明)。
        //   edgeSmooth > 0 时忽略它 —— AA 那条路径的文案与百分比区间保持原样(有实测依据,不许改)。
        // ① 先记下"本来就已经是 JPG"的帧:PNG 转完之后无从区分,所以要提前抓
        string[] preexistingJpg = Array.Empty<string>();
        if (edgeSmooth > 0)
        {
            try { preexistingJpg = Directory.EnumerateFiles(dir, "*.jpg").ToArray(); } catch { }
        }
        int pngCount = ReencodeDirPngToJpgCore(dir, edgeSmooth, progress, ct, 93, 95, curPct);
        // ② 本就已经是 JPG 的帧:就地做一次抗锯齿(先在内存里编完再覆盖原路径,绝不半写坏)
        if (edgeSmooth > 0 && preexistingJpg.Length > 0)
        {
            int done = 0;
            int threads = Math.Clamp(Environment.ProcessorCount - 2, 2, 12);
            var opts = new System.Threading.Tasks.ParallelOptions
            {
                MaxDegreeOfParallelism = threads,
                CancellationToken = ct,
            };
            try
            {
                System.Threading.Tasks.Parallel.ForEach(preexistingJpg, opts, f =>
                {
                    try { EngineService.ApplyEdgeSmoothToJpeg(f, edgeSmooth, VideoFrameJpgQuality); }
                    catch (Exception ex) { AppLogger.Warn($"⚠ 边缘抗锯齿失败({Path.GetFileName(f)}):{ex.Message.Split('\n')[0]}"); }
                    int d = Interlocked.Increment(ref done);
                    if (d % 20 == 0 || d == preexistingJpg.Length)
                        progress?.Report((AlhPro.Core.ProgressBands.Within(AlhPro.Core.ProgressBands.Stage.Post, d, preexistingJpg.Length), $"边缘抗锯齿 已处理 {d} 帧 / 共 {preexistingJpg.Length} 帧"));
                });
            }
            catch (OperationCanceledException) { throw; }
        }
        return pngCount;
    }

    /// <summary>PNG → JPG(可选抗锯齿)。分两遍扫,保持原有"坏帧补同尺寸占位、保帧号连续"的行为。
    /// 返回处理的 PNG 帧数(0 = 目录里本来就没有 PNG)。</summary>
    private static int ReencodeDirPngToJpgCore(string dir, int edgeSmooth,
        IProgress<(int pct, string msg)>? progress, CancellationToken ct, int pctFrom, int pctTo, int curPct)
    {
        int refW = 0, refH = 0;
        try
        {
            foreach (var cand in Directory.EnumerateFiles(dir, "*.png").ToArray())
            {
                try
                {
                    if (new FileInfo(cand).Length == 0) continue;
                    using var b = new System.Drawing.Bitmap(cand);
                    if (b.Width > 0 && b.Height > 0) { refW = b.Width; refH = b.Height; break; }
                }
                catch { }
            }
        }
        catch { }

        var pngs = Directory.EnumerateFiles(dir, "*.png").ToArray();
        int done = 0;
        // 【G-补2】这段整批重编码自己的计时:只用于给它自己算"预计还剩"(同一套 EtaText 公式,
        // 用【本步自己的净耗时】而不是上游阶段的时间 —— 口径与其它阶段一致,不新造公式)。
        var reencSw = System.Diagnostics.Stopwatch.StartNew();
        // 【并行 + 线程上限的依据(2026-09-13 复核,本次未改)】每帧独立、写不同文件,4K 抗锯齿单帧约 1.19 秒,
        // 单线程会让这一步变成新瓶颈,故按帧并行。上限取 min(ProcessorCount-2, 12) 而不是"核数减二":
        //   · 本进程总 CPU 已被 Windows Job 对象按百分比封顶(SafeRender 的「CPU 上限」,自动模式 85%、
        //     自定义 50~95%,见 SafeRender.cs:40/500/589)。16 核 × 85% ≈ 13.6 核的等价额度,
        //     12 路已经贴着这个额度;再往上加线程只会让每路更慢、上下文切换更多,总吞吐不变。
        //   · 这一步跑在整条管线里(ffmpeg/引擎随时可能还有活在跑),留 2 核给界面与其它环节是产品既定的
        //     "防整机卡"策略(与 ApplyProcessPriority 同一条思路)。
        // 【未真机实测】本机禁止占显卡/跑基准,所以"12 是否最优"没有实测数据 —— 要动这个上限必须先测出
        //   "AA 步骤耗时 vs 线程数"的曲线(本条注释只说明现状依据,不代表已标定)。
        // 【任务 O6 · 2026-09-13 现场核对"到底是不是真并行"】(真机基准:2668 帧串行 ≈81~83 s、
        //   12 线程 ≈17~18 s;并行扩展比 GDI+ 4.5× / PIL 6.8×)逐行核对结论:**生产路径本来就是并行,无需改**:
        //   · 本函数(PNG→JPG;AA=0 与 AA>0 都走这里,抗锯齿在 ConvertPngToJpg 内部做)= `Parallel.ForEach`
        //     + 下面这行 `threads2 = Clamp(ProcessorCount-2, 2, 12)`;
        //   · 「已经是 JPG 的帧做 AA」那条分支(ReencodeDirPngToJpg ② 段)同样用 `Parallel.ForEach` +
        //     `Clamp(ProcessorCount-2, 2, 12)`。
        //   所以真机测到的 81~83 s 对应的是**串行复刻**,不代表生产路径(生产是 12 路并行 ≈17~18 s)。
        //   **本次不制造假优化**:没有哪条路是串行的;唯一"可再快"的方向是抬高 12 这个上限或改掉 CPU 85% 封顶,
        //   但 12 路已贴着 85% 的额度(见上),动它必须先真机测出"AA 耗时 vs 线程数"的曲线。
        int threads2 = Math.Clamp(Environment.ProcessorCount - 2, 2, 12);
        var opts = new System.Threading.Tasks.ParallelOptions
        {
            MaxDegreeOfParallelism = threads2,
            CancellationToken = ct,
        };
        try
        {
            System.Threading.Tasks.Parallel.ForEach(pngs, opts, png =>
            {
                var jpg = Path.ChangeExtension(png, ".jpg");
                try { EngineService.ConvertPngToJpg(png, jpg, VideoFrameJpgQuality, out _, edgeSmooth); }
                catch (Exception ex)
                {
                    AppLogger.Warn($"⚠ 帧转 JPG 失败({Path.GetFileName(png)}):{ex.Message.Split('\n')[0]}——用同尺寸占位帧替代,保持编号连续可解码");
                    // 优先生成同尺寸深灰占位(可解码、编号不断);尺寸读不出(彻底损坏)才用参考帧尺寸;
                    // 连参考尺寸都拿不到才退回复制原名(尽力保编号连续)
                    int pw = 0, ph = 0;
                    try { using (var b = new System.Drawing.Bitmap(png)) { pw = b.Width; ph = b.Height; } } catch { }
                    if (pw <= 0 || ph <= 0) { pw = refW; ph = refH; }
                    if (pw > 0 && ph > 0)
                    {
                        using var phb = new System.Drawing.Bitmap(pw, ph);
                        using (var g = System.Drawing.Graphics.FromImage(phb)) g.Clear(System.Drawing.Color.FromArgb(24, 24, 24));
                        phb.Save(jpg, System.Drawing.Imaging.ImageFormat.Jpeg);   // 纯灰占位,System.Drawing JPG 无色偏问题
                    }
                    else
                        try { File.Copy(png, jpg, true); } catch { }
                }
                // 【校验生成的 JPG】ConvertPngToJpg 内部 WinRT 失败会转 GDI,但某些情况下(引擎输出 0 字节/坏 PNG)
                // 可能"不抛异常却写出 0 字节或坏 JPG"。这里兜底:生成的 .jpg 若 0 字节/不可解码,用参考尺寸补一张深灰占位,
                // 否则合帧会因这些 0 字节帧而报"找不到 frame_%06d.jpg / 输出文件无效"。
                try
                {
                    if (File.Exists(jpg) && new FileInfo(jpg).Length == 0)
                    {
                        int pw = refW, ph = refH;
                        try { using (var b = new System.Drawing.Bitmap(png)) { pw = b.Width; ph = b.Height; } } catch { }
                        if (pw <= 0 || ph <= 0) { pw = refW; ph = refH; }
                        if (pw > 0 && ph > 0)
                        {
                            using var phb = new System.Drawing.Bitmap(pw, ph);
                            using (var g = System.Drawing.Graphics.FromImage(phb)) g.Clear(System.Drawing.Color.FromArgb(24, 24, 24));
                            phb.Save(jpg, System.Drawing.Imaging.ImageFormat.Jpeg);
                            AppLogger.Warn($"⚠ 帧转 JPG 输出 0 字节({Path.GetFileName(jpg)}),已用深灰占位替代(保帧号连续)");
                        }
                    }
                }
                catch { }
                try { File.Delete(png); } catch { }
                int d = Interlocked.Increment(ref done);
                if (edgeSmooth > 0)
                {
                    // AA 路径(edgeSmooth > 0):文案与百分比区间【保持原样不动】(2026-09-12 实测依据:4K 下
                    // ffmpeg sab 4.88 秒/帧 → 换成这套 C# 并行实现约 0.1 秒/帧,那条路径的进度口径不许改)。
                    if (d % 20 == 0 || d == pngs.Length)
                        progress?.Report((pctFrom + (int)((pctTo - pctFrom) * (double)d / Math.Max(1, pngs.Length)),
                            $"边缘抗锯齿 已处理 {d} 帧 / 共 {pngs.Length} 帧"));
                }
                else if (d % 20 == 0 || d == pngs.Length)
                {
                    // 【G · 2026-09-13】AA 关(官方预设现已默认清 0)时这条路径原来【一条进度都不发】:
                    // 整批整理帧期间界面只停在上一阶段的"预计还剩几秒",补帧跑到 2667/2668 之后就像卡死
                    // (真机反馈)。现在发中性文案"整理帧(JPG) 第 N / M 帧":
                    //   · 沿用【当前进度百分比】curPct —— 只换文字,不推进也不回退:这一步没有自己的进度区间,
                    //     硬塞一个区间会与相邻阶段(缩放 92~98 / 编码 96~100)打架,反而让进度条乱跳;
                    //   · 文案不带"共"字 → UI 的 etaRegex(要求"第 N 帧 / 共 M 帧")匹配不上 → 不抢步骤行、不改写它;
                    //   · 不含"完成"二字 → 不会被 UI 当成阶段结束行(否则会清掉当前步骤行);
                    //   · 每 20 帧一条 + 末帧一条,和 AA 路径同频(不刷屏)。
                    // 【G-补2】末尾接上本步自己的"预计还剩"(同一套 EtaText 公式 + 本步自己的净耗时):
                    // 这样收尾阶段也有真实数字,而不是让用户对着上一阶段的假 ETA 干等。
                    progress?.Report((curPct, $"整理帧(JPG) 第 {d} / {pngs.Length} 帧"
                        + AlhPro.Core.EtaText.ForRemaining(d, pngs.Length, reencSw.Elapsed.TotalSeconds)));
                }
            });
        }
        catch (OperationCanceledException) { throw; }
        return pngs.Length;
    }

    /// <summary>
    /// 探测源视频色彩空间(ffprobe)。若为 HDR(PQ-HLG)或宽色域(≠BT.709),返回源描述 + 转 BT.709 标准 SDR 的滤镜链,
    /// 用于拆帧阶段提前转换,避免输出偏色/掉信息。保守:任一关键字段未知(unknown)时不做转换(避免 zscale "no path" 报错),
    /// 返回 (null,null)。任何探测/解析失败同样返回 (null,null),不阻断流程。
    /// </summary>
    /// <summary>最近一次探测到的"源素材是 HDR/广色域/10bit"摘要(null = 普通 SDR 8bit)。
    /// 【用途】预览页红字提示用 —— 让用户**在界面上**就知道"HDR 素材会被转成 SDR(有损)",而不是只在日志里 ✗
    /// 【口径】由 ProbeHdrToSdrAsync 在探测时写入,界面只读,不额外探测文件 ✔</summary>
    public static string? LastSourceColorSummary { get; private set; }
    private static async Task<(string? desc, string? vf)> ProbeHdrToSdrAsync(string video, CancellationToken ct)
    {
        try
        {
            string? ffmpegDir = FfmpegPath != null ? Path.GetDirectoryName(FfmpegPath) : null;
            string? ffprobe = ffmpegDir != null ? Path.Combine(ffmpegDir, "ffprobe.exe") : null;
            if (ffprobe == null || !File.Exists(ffprobe)) return (null, null);
            // 不能用 -of csv=p=0 + 位置解析:ffprobe 的 csv writer 按【内部结构体字段序】输出,
            // 完全忽略 -show_entries 的请求序(实测:请求 space,primaries,transfer → 返回 space,transfer,primaries)。
            // 全 bt709 素材上三个值一样,看不出问题;HDR 素材上 smpte2084 落进 prim、bt2020 落进 trc
            // → isHdr 恒为 false,tonemap 分支是死代码,宽色域只走非 tonemap 的 zscale(亮度炸白)。按 key 解析。
            var lines = await RunCaptureAsync(ffprobe,
                $"-v error -select_streams v:0 -show_entries stream=color_space,color_primaries,color_transfer,color_range,pix_fmt,bits_per_raw_sample " +
                $"-of default=nw=1 \"{video}\"", ct);
            var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ln in lines)
            {
                int eq = ln.IndexOf('=');
                if (eq > 0) kv[ln.Substring(0, eq).Trim()] = ln.Substring(eq + 1).Trim();
            }
            // 【2026-09-18 用户报"只开补帧画面却变亮/像变色"】先把**源素材的色彩事实**记进日志:
            // 这类投诉最后总落在"源的 range/矩阵标记"上(全范围没打标记、bt601、bt2020 都会被按不同口径解释),
            // 没有这行就只能靠猜。以后同类反馈:看这行 + 下面那条"已转 BT.709"告警即可定位。
            try
            {
                kv.TryGetValue("color_range", out string? rng);
                kv.TryGetValue("pix_fmt", out string? pf);
                kv.TryGetValue("bits_per_raw_sample", out string? bps);
                AppLogger.Info($"[色彩] 源素材色彩事实:range={rng ?? "unknown"} space={kv.GetValueOrDefault("color_space", "unknown")} " +
                               $"primaries={kv.GetValueOrDefault("color_primaries", "unknown")} transfer={kv.GetValueOrDefault("color_transfer", "unknown")} " +
                               $"pix_fmt={pf ?? "?"} 位深={bps ?? "?"}");
                // 【2026-09-19 · 审计结论落地】把"这是不是 HDR / 10bit 素材"留给界面显示 ——
                // 用户看到"成片偏灰/颜色变淡"时,根因往往就是"HDR 素材被转成 SDR"(App 有 tonemap,但那是**有损**的)✗
                // ⇒ 让他**在界面上就看到**这件事,而不是只有日志里一行,免得以为是软件坏了 ✔
                string trcv = kv.GetValueOrDefault("color_transfer", "").ToLowerInvariant();
                string primv = kv.GetValueOrDefault("color_primaries", "").ToLowerInvariant();
                bool isHdrSrc = trcv is "smpte2084" or "arib-std-b67" or "smpte428" || primv == "bt2020";
                int bits = 0; int.TryParse(bps, out bits);
                if (bits == 0 && pf != null)
                {
                    if (pf.Contains("10le") || pf.Contains("10be")) bits = 10;
                    else if (pf.Contains("12le") || pf.Contains("12be")) bits = 12;
                    else if (pf.Contains("p010")) bits = 10;
                    else bits = 8;
                }
                LastSourceColorSummary = (isHdrSrc || bits > 8)
                    ? $"{(isHdrSrc ? "HDR/广色域" : "")}{(bits > 8 ? (bits + "bit") : "")}（{pf ?? "?"} · transfer={trcv} · primaries={primv}）"
                    : null;
            }
            catch { }
            kv.TryGetValue("color_space", out string? sp);
            kv.TryGetValue("color_primaries", out string? pr);
            kv.TryGetValue("color_transfer", out string? tr);
            string space = sp ?? "", prim = pr ?? "", trc = tr ?? "";
            // 【2026-09-18 诊断包定位到的真凶】原来只把 "unknown" 当"未知",别的值一律当"已知" ✗ ——
            // 而 ffprobe 对**没写标记**的文件会给 **"reserved"**(保留值)/"unspecified" ✗ → 被当成
            // "已知且不是 bt709" → **误判成广色域源 → 白加一层 zscale 色彩转换** → 后果有两种:
            //   ① 拆帧的 mjpeg 编码器开不起来(实测 AMD RX 6650 XT 那台:"Could not open encoder before EOF",
            //      exit -22 → frame=0 → 任务直接失败,用户以为"卡不适配",其实卡没问题:探测是"实测可用");
            //   ② 画面整体被重新解释一遍 → 用户报的"只开补帧却整幅变亮/像变色"。
            // 修法:①把保留值/未指定也当"未知";②"广色域"改成**白名单判定**(只认真正宽的命名),
            //       任何没见过的字符串都不再能触发转换。
            static bool Known(string? v) =>
                !string.IsNullOrWhiteSpace(v)
                && !v.Equals("unknown", StringComparison.OrdinalIgnoreCase)
                && !v.Equals("unspecified", StringComparison.OrdinalIgnoreCase)
                && !v.Equals("reserved", StringComparison.OrdinalIgnoreCase)
                && !v.Equals("none", StringComparison.OrdinalIgnoreCase)
                && !v.Equals("na", StringComparison.OrdinalIgnoreCase);
            bool isHdr = trc.Contains("smpte2084", StringComparison.OrdinalIgnoreCase)
                      || trc.Contains("arib-std-b67", StringComparison.OrdinalIgnoreCase);
            bool primKnown = Known(prim);
            bool spaceKnown = Known(space);
            bool trcKnown = Known(trc);
            bool widePrim = primKnown && (prim.StartsWith("bt2020", StringComparison.OrdinalIgnoreCase)
                                       || prim.Contains("smpte432", StringComparison.OrdinalIgnoreCase)
                                       || prim.Contains("p3", StringComparison.OrdinalIgnoreCase)
                                       || prim.Contains("film", StringComparison.OrdinalIgnoreCase));
            bool wideSpace = spaceKnown && (space.StartsWith("bt2020", StringComparison.OrdinalIgnoreCase)
                                         || space.Contains("ycgco", StringComparison.OrdinalIgnoreCase)
                                         || space.Contains("smpte", StringComparison.OrdinalIgnoreCase)
                                         || space.Contains("fcc", StringComparison.OrdinalIgnoreCase));
            if (!isHdr && !widePrim && !wideSpace)
            {
                // 【非法标记兜底 · 2026-09-18 诊断包 + 本机 ffmpeg 复现确认】
                // 源文件把 colour 标记写成保留值(reserved)时,ffmpeg 会在"帧色彩标记 → JPG 编码器"的
                // 自动转换处直接失败:`Invalid color space` → 拆帧 0 帧 → 任务 1~2 秒失败
                // (诊断包里那台 AMD RX 6650 XT 的报错与本机在**N 卡**上复现出的报错一字不差 → **与显卡无关**)。
                // 实测修法:输入侧显式把标记盖成 bt709 → exit 0、120/120 帧 ✓(叠不叠转换链都能过)。
                // 只在"标记非法/未指定"时兜底;真正的 bt709 源不动它。
                if (!primKnown || !spaceKnown || !trcKnown)
                    InputColorOverride = " -color_primaries bt709 -color_trc bt709 -colorspace bt709";
                return (null, null);
            }
            // 安全:任一关键字段未知 → 不做转换。zscale 需要明确的输入色域/传递/矩阵,缺一即报
            // "no path between colorspaces"(Generic error in an external library),拆帧 0 帧。宁可放过,不可转坏。
            if (!primKnown || !spaceKnown || !trcKnown) return (null, null);
            string desc = $"色域={space}/{prim}/{trc}";
            string vf = isHdr
                ? "zscale=t=linear:npl=100,tonemap=hable:desat=0,zscale=p=bt709:t=bt709:m=bt709:r=tv,format=yuv420p"
                : "zscale=p=bt709:t=bt709:m=bt709:r=tv,format=yuv420p";
            return (desc, vf);
        }
        catch { return (null, null); }
    }

    /// <summary>源文件色彩标记非法(reserved/unspecified/未知)时,拆帧命令要在 -i 之前插入的颜色覆盖参数。
    /// 由 ProbeHdrToSdrAsync 每次探测时写入(单条流水线,静态安全)。
    /// 【实测依据】本机 ffmpeg 复现:输入标记=3(reserved)时,输出 JPG 的 mjpeg 编码器报
    /// `Invalid color space` → `Could not open encoder before EOF` → 0 帧、exit -22;
    /// 在输入前加 `-color_primaries bt709 -color_trc bt709 -colorspace bt709` 后 **exit 0 / 120 帧全出** ✓。</summary>
    private static string InputColorOverride = "";

    private static async Task<int> ExtractFramesCoreAsync(string ffmpeg, string inputVideo, string trimArgs,
        string vfExpr, string framesDir, IProgress<(int pct, string msg)>? progress, CancellationToken ct,
        int origCountEst)
    {
        // 【非法色彩标记兜底】由 ProbeHdrToSdrAsync 探测后写入:源标记是 reserved/unspecified 等非法值时,
        // 在 -i 之前插入颜色覆盖,否则拆帧会在"标记→JPG 编码器"的自动转换处 `Invalid color space` 直接失败。
        string inColor = InputColorOverride;
        // 【阶段收尾上报】ffmpeg 的进度行是周期性的,最后那一帧的 frame= 常常来不及打出(真机日志停在 66/72),
        // 于是"拆帧"看起来永远跑不满就跳到下一阶段(超分/补帧本来各有"完成"那条,只有拆帧漏了)。
        // 文案必须与 UI 的解析正则对齐:`^(?<stage>..)(?:已处理|第) N 帧 / 共 M 帧`,步骤行才会显示"已处理 N/M"。
        int FinishExtract(int n)
        {
            int total = Math.Max(n, origCountEst);
            progress?.Report((StageProgressPct("拆帧", n, total), $"拆帧 已处理 {n} 帧 / 共 {total} 帧"));
            return n;
        }
        var pattern = Path.Combine(framesDir, "frame_%06d.jpg");
        // 拆帧【无条件】passthrough:一个解码帧 = 一个 jpg,永不复制、永不丢弃。
        // 原先只在 VFR 检测通过时才加,检测漏了就落回 ffmpeg 默认的 CFR 补帧路径 ——
        // 该路径按 r_frame_rate 铺栅格,不足就复制帧填满。CFR 素材无害(r=avg),但录屏/手机这类
        // VFR 素材 r_frame_rate 可以远大于 avg_frame_rate:实测合成 VFR(r=60/avg=25.4,真值 253 帧)
        // 出 597 个 jpg(dup=344);真机录屏 r≈96000、avg≈30 → 预估 917 帧对 ~290 万实际帧,
        // speed=0.000127x,按此速率要跑 74 小时(用户连续 8 天每次都在"拆帧 917/917"处强制结束,
        // 日志里就是这条 frame=8643 time=00:00:00.09 dup=9261 的尾巴)。不是硬解挂死,是在写重复帧。
        // 去重也依赖它:mpdecimate 删掉的帧会被 CFR 路径重新复制回来(实测 118→120,dup=2),等于白删。
        // CFR 素材加了完全等价(实测 30fps/5s 两种写法都是 150 帧,首帧字节一致)。
        const string fpsMode = " -fps_mode passthrough";
        // 开关2(分线程):拆帧限流,避免抢系统核(仅开启时生效)
        string threadsArg = SafeRender.SplitCores ? $" -threads {Math.Max(2, SafeRender.CpuCoreCount - 2)}" : "";
        // 硬解优先(仅当该编码本会话没验证过坏);某编码坏过一次就对该编码软解,其它编码仍试硬解
        string hwCodec = await ProbeVideoCodecName(ffmpeg, inputVideo, ct);
        string codecName = hwCodec.Length > 0 ? hwCodec : "未知编码";
        // 探不出编码时不碰闩锁:ProbeVideoCodecName 失败返回 "",而 "" 会被当成一个合法编码键存进
        // 静态表(整会话从不清理),从此所有探测不出编码的视频都被迫走慢速软解 —— 一次偶发探测失败污染全局。
        bool canLatchHw = hwCodec.Length > 0;
        if (!canLatchHw || !_hwDecodeBrokenCodecs.Contains(hwCodec))
        {
            // 【2026-09-17 真机修复】JPEG 编码器只吃 8bit:10bit(P010/HEVC Main10)素材 + 视频降噪(nlmeans 只支持 8bit)
            // + HDR→BT.709 转换(要 10bit/浮点)时,滤镜链末尾留在 10bit/私有格式 → mjpeg 报
            // "Could not open encoder before EOF / error code -22 (Invalid argument)" → 一帧都写不出来 → 整个任务 1 秒失败。
            // 用户素材 ZEB.CS.B.P010.* / LZQN.CS.* 全部踩到(诊断包 18:24~18:41 四次全失败都开着降噪)。
            // 输出端强制 yuvj420p(JPEG 标准 8bit 全范围)即可,链里是什么格式都能自动转过来。
            // 同时把命令写进日志:失败时能直接对着命令行复现(诊断包里原来只有 stderr 尾巴,看不到真实命令)。
            AppLogger.Info($"拆帧命令(硬解):-y {trimArgs}{inColor} -hwaccel d3d11va -i \"{inputVideo}\"{fpsMode}{threadsArg} -vf \"{vfExpr}\" -pix_fmt yuvj420p -qscale:v 2 \"{pattern}\"");
            try
            {
                await RunAsync(ffmpeg,
                    $"-y {trimArgs}{inColor} -hwaccel d3d11va -i \"{inputVideo}\"{fpsMode}{threadsArg} -vf \"{vfExpr}\" -pix_fmt yuvj420p -qscale:v 2 \"{pattern}\"",
                    progress, ct, ExtractStageLabel(vfExpr), origCountEst);
                int n = Directory.EnumerateFiles(framesDir, "*.jpg").Count();
                if (n > 0) return FinishExtract(n);
                if (canLatchHw) _hwDecodeBrokenCodecs.Add(hwCodec);   // 硬解输出 0 帧 → 该编码视为不可用
                AppLogger.Warn($"拆帧:硬解(d3d11va)正常退出但一帧未出(编码 {codecName})→ 本会话该编码改走软解");
            }
            // 用户取消 ≠ 硬解坏:必须直接收手。原先 catch { } 会把取消也当成硬解失败写进
            // _hwDecodeBrokenCodecs(静态、整个会话从不清理),害得之后所有同编码视频都被迫走慢速软解。
            catch (OperationCanceledException) { throw; }
            catch (EngineStallException ex) when (ex.ProcessStillRunning)
            {
                // 【进程收不回来 → 不许回退重跑】旧进程可能还在往同一个帧目录写 jpg,新建的软解进程与它
                // 交叉写出的帧数/内容都是错的(静默坏结果)。宁可这一条任务明确失败,也不产出一份内容错误的成片。
                AppLogger.Warn($"拆帧:硬解进程无法回收(编码 {codecName}),已中止本任务——不回退重跑,避免新旧进程同时写同一帧目录。{ex.Message}");
                throw;
            }
            catch (EngineStallException ex)
            {
                // 看门狗判死:硬解被驱动挂死,进程活着却永不退出 —— 正是"用户只能手动点强制结束"的根因。
                if (canLatchHw) _hwDecodeBrokenCodecs.Add(hwCodec);
                AppLogger.Warn($"拆帧:硬解(d3d11va)停滞被看门狗终止(编码 {codecName})→ 自动回退软解重试。{ex.Message}");
            }
            catch (Exception ex)
            {
                if (canLatchHw) _hwDecodeBrokenCodecs.Add(hwCodec);   // 硬解失败 → 该编码标记坏,回退软解
                AppLogger.Warn($"拆帧:硬解(d3d11va)失败(编码 {codecName})→ 自动回退软解重试。{ex.Message}");
            }
            // 清理硬解可能留下的残缺帧
            foreach (var f in Directory.EnumerateFiles(framesDir, "*.jpg"))
            { try { File.Delete(f); } catch { } }
        }
        await RunAsync(ffmpeg,
            // 软解回退:同样强制 JPEG 标准 8bit(原因见上面硬解那条的注释)
            $"-y {trimArgs}{inColor} -i \"{inputVideo}\"{fpsMode}{threadsArg} -vf \"{vfExpr}\" -pix_fmt yuvj420p -qscale:v 2 \"{pattern}\"",
            progress, ct, "拆帧", origCountEst);
        return FinishExtract(Directory.EnumerateFiles(framesDir, "*.jpg").Count());
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
                $"-y {trimArgs}{InputColorOverride} -i \"{inputVideo}\" -fps_mode passthrough -vf \"{vfExpr},showinfo\" -f null NUL", ct);
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
        => AlhPro.Core.VideoPipeline.MergeDurations(durs, dropped, totalCount);

    /// <summary>
    /// 统一"去重落盘"逻辑(去重各模式共用,消除 4 处重复):
    /// ① 尾帧恒保留(结尾画面组绝不因去重而丢) ② 合并时长表(必须与被删帧同一集合,否则 Count 与帧数不齐)
    /// ③ 删除被删帧、保留帧重命名为连续序号 ④ 返回保留帧的源序号(0-based,升序,供"补回"把内容帧放回源时间轴)。
    /// 注:入参 drop 是 1-based 帧号集合;本方法内部会把尾帧从集合里剔除(保持已删集合不含尾帧,避免时序错位)。
    /// </summary>
    private static System.Collections.Generic.List<int> ApplyDedupDrop(
        string framesIn, string[] src, System.Collections.Generic.HashSet<int> drop,
        System.Collections.Generic.List<double>? frameDurs, int totalCount)
    {
        // 尾帧恒保留:结尾画面组绝不因去重而丢(88889999 的 9)
        if (drop.Remove(totalCount))
            AppLogger.Info("尾帧保护:去重判定含末帧,已强制保留(结尾画面组不丢)");
        // 合并时长表:必须用"已剔除尾帧"的同一集合(否则时长表多删一条 → Count 与帧数不齐 → VFR 时间轴被丢)
        if (frameDurs != null) MergeDurations(frameDurs, drop.ToList(), totalCount);
        // 删除被删帧 + 保留帧重命名为连续序号
        int idx = 0;
        for (int n = 0; n < src.Length; n++)
        {
            if (drop.Contains(n + 1)) { try { File.Delete(src[n]); } catch { } continue; }
            idx++;
            File.Move(src[n], Path.Combine(framesIn, $"frame_{idx:D6}.jpg"), true);
        }
        // 保留帧源号(0-based,升序):"补回"用它把内容帧放回源时间轴
        var kept = new System.Collections.Generic.List<int>();
        for (int n = 1; n <= src.Length; n++)
            if (!drop.Contains(n)) kept.Add(n - 1);
        return kept;
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
        var r = AlhPro.Core.VideoPipeline.BuildVfrSetptsExpr(durs);
        if (r == null && durs != null && durs.Count > 400)
            AppLogger.Info($"VFR 时间轴段数 {durs.Count} 超过上限 400,回退 CFR(避免 setpts 命令超 Windows 命令行 32767 字符)");
        return r;
    }

    /// <summary>去重分析的"心跳":把进度**同时**喂给界面与日志(日志按 5 秒限流)。
    /// 【为什么必须有】用户报"去重卡住"时,日志里往往只有任务开始那一条 —— 事后完全无法判断它是
    /// 停在第几帧、还是根本没开始跑。有了心跳,下一份诊断包就能直接指到帧号(配合耗时一起看)。
    /// 【为什么限流】4 万帧的视频每帧写一行会把日志刷爆;界面进度不限(它是覆盖式的,不落盘)。</summary>
    private sealed class DedupHeartbeat
    {
        private readonly IProgress<(int pct, string msg)>? _progress;
        private readonly string _stage;
        private readonly int _total;
        private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
        private long _lastLogMs;

        public DedupHeartbeat(IProgress<(int pct, string msg)>? progress, string stage, int total)
        {
            _progress = progress;
            _stage = stage;
            _total = total;
            AppLogger.Info($"{_stage}开始:共 {_total} 帧(逐段心跳每 5 秒一条,便于事后判断卡在哪一帧)");
        }

        public void Step(int i, int deleted)
        {
            string msg = $"{_stage} 第 {i} 帧 / 共 {_total} 帧(已判重 {deleted} 帧,已用 {_sw.Elapsed.TotalSeconds:0} 秒)";
            _progress?.Report((3, msg));
            long ms = _sw.ElapsedMilliseconds;
            if (ms - _lastLogMs >= 5000)
            {
                _lastLogMs = ms;
                AppLogger.Info(msg);
            }
        }

        /// <summary>收尾:无论走到哪一步都要落一条"结束"日志(有了它,"开始有、结束没有"就等于卡住)。</summary>
        public void Done(int deleted)
            => AppLogger.Info($"{_stage}完成:共 {_total} 帧,判重 {deleted} 帧,用时 {_sw.Elapsed.TotalSeconds:0.#} 秒");
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
    /// 返回要删除的帧号(1-based,与 frame_%06d.png 序号对应)。
    /// 【2026-09-12 补】新增 ct(可取消)与 progress(界面进度):此前这个函数**既不能取消、也从不报进度** ——
    /// 长片/多帧时界面一动不动、点「停止」也停不下来,用户只能判成"去重卡住了"(实测过的真实反馈)。
    /// 取消是"每 16 帧检查一次"(检查本身极便宜),不会拖慢正常处理。</summary>
    private static System.Collections.Generic.HashSet<int> DetectDupFramesWithSsim(string framesDir,
        double sadThr, double ssimThr, double protectRatio = 0.06,
        int window = 6, int scale = 16, double blockThr = 4,
        double segSsim = 0, double segSad = 5, bool motionComp = true, bool protectSmallMotion = true,
        System.Threading.CancellationToken ct = default,
        IProgress<(int pct, string msg)>? progress = null, string stage = "去重分析")
    {
        var drop = new System.Collections.Generic.HashSet<int>();
        var files = EnumerateFrameFiles(framesDir)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length < 2) return drop;
        window = Math.Clamp(window, 2, 12);
        var hb = new DedupHeartbeat(progress, stage, files.Length);
        var grays = new System.Collections.Generic.List<byte[]>(Math.Min(window, files.Length));
        grays.Add(SampleGray(files[0], scale, out var sw, out var sh));
        // 静止段合并用独立"段首样本"(全序列帧号,不受 grays 窗口裁剪影响——旧实现复用窗口后
        // grays 索引与真实帧号脱节,总在最后十几帧窗口内误删/漏检)
        byte[] segBase = grays[0];
        int segRun = 0;
        for (int i = 1; i < files.Length; i++)
        {
            // 取消 + 进度/日志心跳:每 16 帧一次(取 2 的幂,判断成本可忽略)
            if ((i & 15) == 0)
            {
                ct.ThrowIfCancellationRequested();
                hb.Step(i, drop.Count);
            }
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
        hb.Done(drop.Count);
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
        var dir = Path.Combine(EngineService.TempRoot, $"imgup_duplight_{Guid.NewGuid():N}");
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
                $"-y -i \"{videoPath}\" -vf \"select='not(mod(n,{step}))',scale=160:-2,format=gray\" -pix_fmt yuvj420p -qscale:v 1 \"{pattern}\"",
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

    /// <summary>全文分析:缩到 400 宽灰度拆帧,复用处理阶段的 DetectDupFramesWithSsim / DetectDupFramesAdaptive,
    /// 再算按时间轴的重复分布。
    /// 【任务 M4 · 2026-09-13 更正口径】旧注释与界面提示曾写「与处理时同一套检测算法、数字一致」——**不成立**:
    ///   ① 这里缩到 400 宽灰度(采样格 4),处理时用原分辨率;
    ///   ② 智能模式这里走 DetectDupFramesAdaptive,而处理时走「拍数识别+网格采样」(识别不出才回退帧差+SSIM)。
    /// 所以它只用于「看分布/估个大概」,数字与处理结果可能有小幅差异 —— 界面提示文案已同步改成"预估"。</summary>
    private static async Task<DupProfile> AnalyzeDupAsync(string ffmpeg, string videoPath,
        int dedupMode, double dedupAnimeThr, int dedupSmartMode, bool motionComp, bool dedupOnlyTrueHold, CancellationToken ct)
    {
        var dir = Path.Combine(EngineService.TempRoot, $"imgup_dupanalyze_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            double inFps = 30;
            if (double.TryParse(ProbeFps(videoPath), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var f0) && f0 > 0) inFps = f0;
            double dur = await ProbeDurationSeconds(videoPath);
            var pattern = Path.Combine(dir, "frame_%06d.png");
            await RunAsync(ffmpeg,
                $"-y -i \"{videoPath}\" -vf \"scale=400:-2,format=gray\" -pix_fmt yuvj420p -qscale:v 1 \"{pattern}\"",
                null, ct, "分析", 0);
            int total = Directory.EnumerateFiles(dir, "*.png").Count();
            if (total < 2) return new DupProfile { Summary = "帧数过少,无法分析" };
            int scaleSample = 4;   // 400 宽 / 4 = ~100 宽采样,接近处理阶段口径(处理用 原分辨率/采样粒度)
            System.Collections.Generic.HashSet<int> drop;
            if (dedupMode == 1)   // 智能:自适应
            {
                drop = DetectDupFramesAdaptive(dir, null, scaleSample, dedupSmartMode, motionComp,
                    ct, "去重预估(自适应)");
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
                drop = DetectDupFramesWithSsim(dir, sad, ssim, protect, 6, scaleSample, 4, segSsim, segSad, motionComp,
                    ct: ct, stage: "去重预估(帧差+SSIM)");
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
            var raw = Path.Combine(EngineService.TempRoot, $"imgup_rhythm_{Guid.NewGuid():N}.raw");
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
            var safeVideo = AudioService.FfmpegSafePath(videoPath);   // 中文路径→8.3,防 GBK 代码页乱码
            var lines = await RunCaptureAsync(ffmpeg,
                $"-y -i \"{safeVideo}\" -vf \"scdet=threshold=0,metadata=print\" -f null NUL", ct);
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
                    $"-y -i \"{safeVideo}\" -vf \"select='gt(scene,-1)',metadata=print\" -f null NUL", ct);
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
            // 事件间隔(帧数):转场/长静止镜头会产生离群间隔 → 由 Core 以中位数为锚裁剪 [0.5m, 2m]
            var gaps = new System.Collections.Generic.List<double>();
            for (int j = 1; j < events.Count; j++) gaps.Add(events[j] - events[j - 1]);
            // 【任务 M5 · 2026-09-13】置信度算法迁到 Core(纯逻辑 + 单测,含"有拍数却低置信"回归用例):
            //   一致性 = 间隔落在【中位数 ± max(1 帧, 20% 周期)】的占比(原为均值 ±1 帧);
            //   稳定性 = 1 − MAD/中位数(原为 1 − σ/均值);有效事件占比惩罚开方弱化(原为线性)。
            //   判定阈值一个不动:均衡 0.5 / 激进 0.35 / 保守 0.7 + 常见拍数。
            var sc = AlhPro.Core.ContentFpsConfidence.Compute(gaps, inFps);
            if (sc.Continuous)
                return new ContentFpsInfo
                {
                    Fps = inFps, Confidence = 0.25,
                    Summary = "素材几乎连续运动(无保持帧,内容帧率≈输入帧率)",
                };
            string confTxt = sc.Confidence >= 0.7 ? "高" : sc.Confidence >= 0.45 ? "中" : "低";
            return new ContentFpsInfo
            {
                Fps = sc.ContentFps, Period = sc.Period, Confidence = sc.Confidence,
                Summary = $"内容节奏≈{sc.ContentFps:0.##} fps(间隔≈{sc.MeanGap:0.##} 帧,中位 {sc.MedianGap:0.##} 帧,置信度{confTxt})",
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
        string inputVideo, string trimArgs, string scaleVfDenoise, string framesIn, int origCountEst,
        double inFps, double userInterval, double userTol, double autoConf, string modeNote,
        IProgress<(int pct, string msg)>? progress, CancellationToken ct, bool forceGrid = false, bool phaseAlign = true)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        progress?.Report((3, $"{modeNote}:全量拆帧 + 去重(整片统一判定;随后展开时间轴+标准补帧)..."));
        int frameCount = await ExtractFramesCoreAsync(ffmpeg, inputVideo, trimArgs,
            scaleVfDenoise, framesIn, progress, ct, origCountEst);
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
                $"-y -framerate 1 -i \"{Path.Combine(framesIn, "frame_%06d.jpg")}\" " +
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
        var files = Directory.EnumerateFiles(framesIn, "*.jpg")
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
            // 【2026-09-21 补上逐帧心跳 —— 任务④「去重要不要补逐帧进度」的落地】
            // 这是**去重链上唯一一段没有进度输出的逐帧循环**:它给每一帧都做 LoadFullGray + 直方图均衡化
            // (整帧解码),是纯 CPU 重活,却只在整个段跑完之后才报一条。本段今天**不可达**(三个调用点全部传
            // forceGrid: true ⇒ 进的是上面那个"网格抽帧"分支,那条分支根本不解码帧、只按间隔取序号),
            // 所以这不是当前用户能撞到的缺陷 —— 但它是一个**定时炸弹**:哪天有人把"自动识别拍数"这条路打开
            // (forceGrid 传 false),这一段就会变成"界面卡在那里一动不动"的老毛病(本轮修的就是这类体验)。
            // 与其留个"要记得加"的注释,不如现在就按现有风格接上同一个心跳(成本 3 行,零行为变化)。
            var hbPick = new DedupHeartbeat(progress, "去重分析(拍数识别)", len);
            var prevFull = LoadFullGray(files[s], out var wF, out var hF, out var prevBlue4, out var bw4, out var bh4);
            var prev16 = SampleFrom(prevFull, wF, hF, 16, out var sw16, out var sh16);
            var prevEq4 = EqualizeHist(prevBlue4);
            var sads = new double[len - 1];
            var histds = new double[len - 1];
            for (int i = s + 1; i < e; i++)
            {
                // 每 32 帧一次(取 2 的幂,判断成本可忽略):与 DetectDupFramesWithSsim / Adaptive 同一节流口径
                if (((i - s) & 31) == 0) hbPick.Step(i - s, 0);
                var curFull = LoadFullGray(files[i], out wF, out hF, out var curBlue4, out bw4, out bh4);
                var cur16 = SampleFrom(curFull, wF, hF, 16, out sw16, out sh16);
                var curEq4 = EqualizeHist(curBlue4);
                int k = i - s - 1;
                sads[k] = MeanAbsDiff(prev16, cur16);
                histds[k] = MeanAbsDiff(prevEq4, curEq4);
                prev16 = cur16; prevFull = curFull; prevEq4 = curEq4;
            }
            hbPick.Done(0);
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
        // 【2026-09-21 补上心跳】这一段是**逐帧的文件 I/O**(删除/改名),长片上是实打实的几秒~几十秒;
        // 之前它只在全部做完之后靠 `去重完成:` 那一条交代,中间这段时间界面上没有任何新信息。
        // 与其它逐帧循环同族:每 1024 帧报一次(文件 I/O 比 CPU 判定快得多,所以节流更粗)。
        if (keep.Count < frameCount)
        {
            var hbRe = new DedupHeartbeat(progress, "去重:重排帧号", frameCount);
            int idx = 0;
            for (int n = 0; n < frameCount; n++)
            {
                if ((n & 1023) == 0) hbRe.Step(n, 0);
                if (keep.Contains(n))
                {
                    idx++;
                    if (idx != n + 1)
                        File.Move(files[n], Path.Combine(framesIn, $"frame_{idx:D6}.jpg"), true);
                }
                else
                {
                    try { File.Delete(files[n]); } catch { }
                }
            }
            hbRe.Done(0);
        }
        int keptCount = Directory.EnumerateFiles(framesIn, "*.jpg").Count();
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
    /// 【任务 S1 · 2026-09-13 复用点】本方法同时是「平滑时间轴」的合成原语:调用方可用
    /// <paramref name="planSlots"/>/<paramref name="planSlotSrc"/> 直接给出**外部排程**(每个输出槽是要拷源帧、
    /// 还是在某对源帧之间插值),此时本方法只负责"把排程变成真实帧"—— 层批并发、静止帧对保护、引擎缺帧兜底、
    /// 黑帧提示、JPG 落盘全部照旧,**不另写一套**(用户硬要求)。不传外部排程时,行为与改动前逐字一致。
    private static async Task<(int frameCount, double outFps, bool anyBlack)> RunTempoResampleAsync(string rife,
        string framesIn, string framesFinal, int frameCount, double inFps,
        System.Collections.Generic.List<int>? srcIdx, int interpScale, double? targetFps,
        int gpuId, double srcDur, string interpModel, bool tta,
        IProgress<(int pct, string msg)>? progress, CancellationToken ct,
        IReadOnlyList<(int i, int j, double phi)>? planSlots = null, string[]? planSlotSrc = null,
        Func<int, int>? slotsInPair = null, string stageName = "按源时间轴插帧",
        bool preferFileCopyForJpgSlot = false)
    {
        var files = Directory.EnumerateFiles(framesIn, "*.jpg")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        int n = files.Length;
        if (n < 2)
        {
            foreach (var f in files) File.Copy(f, Path.Combine(framesFinal, Path.GetFileName(f)), true);
            return (n, inFps, false);
        }
        var idx = srcIdx ?? Enumerable.Range(0, n).ToList();
        // 关键帧源时刻:按保留帧在原源轴上的位置,归一化到"源视频已处理时长" srcDur(避免尾部静止段截断时间轴)
        double T = srcDur > 0.01 ? srcDur : (n > 1 ? (double)idx[^1] / inFps : 1.0 / 30);
        if (T <= 0.01) T = 1.0 / 30;
        double TimeOf(int i) => T * (double)idx[i] / Math.Max(1, idx[^1]);
        double F = targetFps ?? inFps * interpScale;
        // 输出帧数 = 源轴真实帧数(idx 跨度,不再 round(T×F)+1:对整帧率会多 1 帧 → ×倍率后多出 1 拍 → 裁尾="少几帧")
        int outN = Math.Max(2, idx[^1] + 1);
        // 【任务 S1】外部排程(平滑时间轴)时,输出帧数由排程表给出(它已按 round(真实时长×目标帧率) 定好)
        bool externalPlan = planSlots != null && planSlotSrc != null && planSlotSrc.Length > 0;
        if (externalPlan) outN = planSlotSrc!.Length;
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
        if (externalPlan)
        {
            // 【任务 S1】平滑时间轴:槽位/拷贝槽/输出帧数全部由 Core.CutAwareSchedule 排好 ——
            // 切点上的槽在上游已被改写成"拷前一场景帧 / 从新场景帧开始",这里**不可能**收到跨切点的 φ∈(0,1) 槽。
            for (int j = 0; j < outN; j++)
            {
                slotSrc[j] = planSlotSrc![j];
                if (string.IsNullOrEmpty(slotSrc[j])) slotSrc[j] = files[Math.Min(j, n - 1)];   // 兜底:宁可重复不可空
            }
            slots.AddRange(planSlots!);
        }
        else
        {
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
        }
        var tempoTempDirs = new System.Collections.Generic.List<string>();
        int slotDone = 0;
        // ===== 提速方案(每层一次引擎调用,替代逐槽 -s,快约 20~50 倍)=====
        // 每个内容帧对内部的槽 phi 是"均匀细分"(槽在源帧网上,DIST 有限)→ 用 dyadic 树
        // 生成过采样网格(4 层 = 16 格,误差 ≤1/16,视觉无差),每个槽按 phi 就近取帧:
        // 时间轴=精确源位置,画面=最近 dyadic 插值。引擎调用:每层一次层批(共 ≤4 次)。
        var activePairs = slots.Select(s => s.i).Distinct().ToList();
        var pairMids = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<(double phi, string file)>>();
        // ===== 【F1/F2】黑帧自检状态(层批路按【块】抽样,整段再补一次均匀抽样)=====
        // 真机诊断:这一步曾检出黑帧却只打提示就继续,黑帧因此当成功产物交付(成片 3 段全黑)。
        // 现在:检出即换路重算(见 RerouteBlackSlotsAsync),换不动就抛异常让任务失败 —— 绝不放行。
        int blackDefects = 0;      // 命中的缺陷帧数(判据见 AlhPro.Core.BlackFrameRecovery.IsRealDefect)
        int blackChecked = 0;      // 已抽样检查的帧数(日志用:说清"查了多少",不说就等于没查)
        int blackExempts = 0;      // 近黑但按"素材本来就是黑场"放行的帧数(必须留痕,见 IsRealDefect 说明)
        int missingMids = 0;       // 引擎没产出、被 InterpLayerBatchAsync 兜底成"左端点副本"的节点数(只记日志)
        var blackSamples = new System.Collections.Generic.List<string>();   // 少量样本(只进日志,便于定位)
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
                    // 【任务 S1】外部排程时深度按"该源帧对内部**实际要插几帧**"算(不再用 idx 距 × 倍率猜)
                    int k0 = slotsInPair != null ? slotsInPair(p)
                        : (int)Math.Round(Math.Max(1, idx[p + 1] - idx[p]) * scaleF);
                    int k = Math.Max(2, k0);
                    int d = 1;
                    while ((1 << d) < k) d++;
                    d = Math.Min(d, 4);
                    return (p, d);
                })
                .OrderBy(g => g.d)
                .GroupBy(g => g.d)
                .ToList();
            var workTmp = Path.Combine(EngineService.TempRoot, "imgup_tempo_layers", Guid.NewGuid().ToString("N"));
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
                        // 【让"层批停顿"可见】每个层批也是一次新的 RIFE 进程(启动 + 模型加载是秒级固定开销):
                        // 先报"正在启动",引擎真出帧后报"已就绪(启动 X.Xs)"(回调只认第一次产出)。
                        // 只多两行进度上报,层批划分/引擎参数/并发一律不变。
                        // 百分比沿用"上一批结束时的口径"(midDone 折算):两条消息都不许把进度往回落,
                        // 否则界面的进度占比法 ETA 会跟着回退(已就绪消息尤其容易写错成固定低值)。
                        int frAtStart = Math.Min(slotTotal, (int)((double)midDone / Math.Max(1, midNeed) * slotTotal));
                        int lbStartPct = 10 + (int)(35.0 * frAtStart / slotTotal);
                        int layerBatchNo = off / LayerBatch + 1;
                        int layerBatchAll = (curNodes.Count + LayerBatch - 1) / LayerBatch;
                        {
                            double lbStartSec = AlhPro.Core.VideoPipeline.AssumedEngineStartupSecondsPerBatch;   // 待实测标定
                            progress?.Report((lbStartPct,
                                $"第 {layerBatchNo}/{layerBatchAll} 层批(第 {lv} 层):启动补帧引擎(约 {lbStartSec:0.#} 秒,首次较慢;本批 {batch.Count} 帧对)…"));
                        }
                        int lbReadyReported = 0;
                        Action<double> lbOnEngineReady = sec =>
                        {
                            if (Interlocked.Exchange(ref lbReadyReported, 1) != 0) return;
                            try
                            {
                                progress?.Report((lbStartPct, $"第 {layerBatchNo}/{layerBatchAll} 层批(第 {lv} 层):补帧引擎已就绪(启动 {sec:0.0}s),开始处理 {batch.Count} 帧对…"));
                                AppLogger.Info($"第 {layerBatchNo}/{layerBatchAll} 层批(第 {lv} 层):补帧引擎已就绪(启动 {sec:0.0}s,{batch.Count} 帧对)");
                            }
                            catch { }
                        };
                        // 层批内逐帧进度:轮询输出文件数 → 映射到全局"已处理 X/共 Y 帧"
                        // 【修复 跨批跳格】旧代码 gf=k/batch.Count*slotTotal 只看当前批(batch 开始时 k 归 0,
                        //  进度会跳回/跳格,用户以为卡死)。改为用"批前已累计 midDone + 当前批已生成 k"折算,
                        //  批内逐帧涨、跨批连续,不再跳格。
                        IProgress<(int pct, string msg)>? layerProg = progress == null ? null
                            : new System.Progress<(int pct, string msg)>(lt =>
                            {
                                var m = System.Text.RegularExpressions.Regex.Match(lt.msg, @"第\s*(\d+)\s*帧");
                                if (!m.Success) { progress.Report(lt); return; }
                                int k = int.Parse(m.Groups[1].Value);
                                int gf = Math.Min(slotTotal, (int)((double)(midDone + k) / Math.Max(1, midNeed) * slotTotal));
                                progress.Report((10 + (int)(35.0 * gf / slotTotal),
                                    $"{stageName} 已处理 {gf} 帧 / 共 {slotTotal} 帧(源 {n} 帧·目标 {F:0.##} fps)"));
                            });
                        var mids = await EngineService.InterpLayerBatchAsync(rife,
                            batch.Select(nd => (nd.a, nd.b)),
                            Path.Combine(workTmp, $"D{depth}_L{lv}_{off / LayerBatch}"), gpuId, ct, interpModel, tta,
                            progress: layerProg, watchStage: stageName,
                            onEngineReady: lbOnEngineReady);   // 只上报"引擎已就绪",不改层批划分/引擎参数
                        for (int k = 0; k < batch.Count; k++)
                        {
                            var nd = batch[k];
                            double midPhi = (nd.phi0 + nd.phi1) / 2;
                            string midF = mids[k];
                            // 【只记日志】InterpLayerBatchAsync 对"引擎没产出的节点"会兜底成左端点源帧
                            // (宁可重复不可空)。那种帧不是黑帧(是合法的重复帧),但说明该次引擎调用丢过活,
                            // 值得在日志里留个数 —— 本任务不改它的行为(避免影响既有去重/统计口径)。
                            if (!midF.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) missingMids++;
                            if (!pairMids.TryGetValue(nd.p, out var list)) pairMids[nd.p] = list = new();
                            list.Add((midPhi, midF));
                            if (lv < depth)
                            {
                                nextNodes.Add((nd.p, nd.phi0, nd.a, midPhi, midF));
                                nextNodes.Add((nd.p, midPhi, midF, nd.phi1, nd.b));
                            }
                        }
                        // ===== 【F2】块级黑帧自检(每次引擎调用单独抽样;首尾必查)=====
                        // 真机形态是"某一次引擎调用整体输出黑帧(GPU 队列异常,退出码仍 0)",块 ≤384 帧 →
                        // 块内均匀抽样必然命中;而旧的"整段只抽前 6 帧"在几千~几万帧的层批里等于没查
                        // (黑片实测长 40~360 帧 → 6 帧抽样命中概率极低,这就是黑帧穿到成片的直接原因)。
                        // 判据:输出近黑【且】两端源帧都不是近黑(任一端本来就是黑场 = 素材内容:片头黑场/淡入淡出/夜戏;
                        // 判重的代价是换路重算后仍判缺陷 → 整条任务失败,见 Core.BlackFrameRecovery.IsRealDefect 的取舍说明)。
                        // 放行的帧数会单独记一行日志(不静默)。
                        foreach (int bk in AlhPro.Core.DefectSampling.Plan(mids.Count))
                        {
                            if (bk >= batch.Count) break;
                            blackChecked++;
                            try
                            {
                                if (!EngineService.IsBlackPng(mids[bk])) continue;
                                var bnd = batch[bk];
                                if (!AlhPro.Core.BlackFrameRecovery.IsRealDefect(true,
                                        EngineService.IsBlackPngStrict(bnd.a), EngineService.IsBlackPngStrict(bnd.b)))
                                {
                                    blackExempts++;   // 相邻源帧本来就黑 = 素材内容(不静默:末尾统一记一行)
                                    continue;
                                }
                                blackDefects++;
                                if (blackSamples.Count < 8)
                                    blackSamples.Add($"第 {lv} 层第 {layerBatchNo}/{layerBatchAll} 层批·帧对 {bnd.p}");
                            }
                            catch { }
                        }
                        midDone += batch.Count;
                        int fr = Math.Min(slotTotal, (int)((double)midDone / Math.Max(1, midNeed) * slotTotal));
                        progress?.Report((10 + (int)(35.0 * fr / slotTotal),
                            $"{stageName} 已处理 {fr} 帧 / 共 {slotTotal} 帧(源 {n} 帧·目标 {F:0.##} fps)"));
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
        // ===== 【F2】整段再补一次均匀抽样(首尾必查)+ 【F1】检出黑帧就必须换路 =====
        // 块级抽样(见上)覆盖的是"某一次引擎调用整块坏掉";整段抽样额外覆盖"散落在块边界/少量坏帧"的情形,
        // 并且它是唯一覆盖 pairMids(最终被选为槽来源的帧集合)的地方。抽样口径见 AlhPro.Core.DefectSampling。
        // 【代价口径】整段最多 48 帧解码(约 0.7 秒),相对层批阶段(分钟级)可忽略 —— 这是刻意的:
        // 真机那次正是"抽样太薄(只抽前 6 帧)→ 黑帧当成功交付",用 0.7 秒换"不漏"远比省这 0.7 秒值。
        {
            var flat = pairMids.OrderBy(kv => kv.Key)
                .SelectMany(kv => kv.Value.Select(v => (p: kv.Key, v.phi, v.file))).ToList();
            foreach (int k in AlhPro.Core.DefectSampling.Plan(flat.Count))
            {
                blackChecked++;
                var (bp, _bphi, bfile) = flat[k];
                try
                {
                    if (!EngineService.IsBlackPng(bfile)) continue;
                    bool aBlack = bp + 1 < files.Length && EngineService.IsBlackPngStrict(files[bp]);
                    bool bBlack = bp + 1 < files.Length && EngineService.IsBlackPngStrict(files[bp + 1]);
                    if (!AlhPro.Core.BlackFrameRecovery.IsRealDefect(true, aBlack, bBlack))
                    {
                        blackExempts++;   // 相邻源帧本来就黑 = 素材内容(不静默:下面统一记一行)
                        continue;
                    }
                    blackDefects++;
                    if (blackSamples.Count < 8) blackSamples.Add($"{Path.GetFileName(bfile)}(帧对 {bp})");
                }
                catch { }
            }
        }
        // 【放行必须留痕】把"按素材黑场放行"的帧数写进日志:否则"这次为什么没换路"在诊断包里无从判断
        // (这也是复核报告点名的要求:豁免与否要给出依据)。口径 = 任一端源帧本来就近黑,见 IsRealDefect。
        if (blackExempts > 0)
            AppLogger.Info($"{stageName}:黑帧自检 —— 抽样检出近黑中间帧 {blackExempts} 帧,但其相邻源帧本来就近黑"
                + "(素材黑场/淡入淡出/夜戏)→ 按素材内容放行(与超分段 ShouldExemptAsSourceBlack 同一思路,"
                + "口径放宽到「相邻任一端」);这批帧不是 GPU 故障,不触发换路)");
        bool anyBlack = blackDefects > 0;   // 返回值口径不变(平滑时间轴那条路用它决定是否回退分段补帧)
        if (missingMids > 0)
            AppLogger.Info($"{stageName}:引擎未产出的节点 {missingMids} 个(已按「左端点副本」兜底,不是黑帧;"
                + "若占比大说明该次引擎调用丢过活,建议更新显卡驱动后重试)");
        if (anyBlack)
        {
            // 【F1 核心】检出黑帧 = "层批原语只有 ncnn 一条路"这条路在本机已不可信 → 必须【换路重算】。
            // 为什么不用超分那种"回退源帧":补帧这一步的任务就是把缺失的时间轴位置生成出来,
            // 拿源帧顶替会让输出帧数与时间轴双双错乱(用户口径:补帧的回退绝不能改帧数/时间轴)。
            // 换路顺序(与分段补帧路径的 ncnn→ONNX→换卡 同思路,但不落 CPU):
            //   ① ONNX(DirectML)按同一张槽表逐槽重算 → ② 换另一块显卡 ncnn 单对 -s 逐槽重算 → ③ 都不行就抛异常让任务失败。
            await RerouteBlackSlotsAsync(rife, interpModel, tta, gpuId, files, slots, slotSrc, tempoTempDirs,
                stageName, blackDefects, blackChecked, blackSamples, progress, ct).ConfigureAwait(false);
            // 换路成功 = 输出已是【逐帧复查过的非黑帧】(换路方法本身失败会抛异常,不会走到这里)。
            // 返回值口径要对齐"最终产物"而不是"曾经检出过":平滑时间轴那条路见到 true 会清掉半成品并回退分段补帧,
            // 把一次已经修好的合成判成失败就白费了这次重算。
            anyBlack = false;
        }
        // 输出:按 j 顺序写帧(帧号连续)。统一重编码成 JPG(源帧已是 JPG,层批中间帧为引擎 PNG),配合下游 framesIn=JPG。
        int written = 0;
        for (int j = 0; j < outN; j++)
        {
            var dst = Path.Combine(framesFinal, $"frame_{++written:D6}.jpg");
            if (preferFileCopyForJpgSlot && slotSrc[j].EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
            {
                // 【任务 S1】"落在源帧上"的槽必须**逐字节原样**落地:再走一次 q0.96 重编码纯属白丢画质
                // (用户口径:φ=0 的槽"直接拷贝该源帧(不调引擎)")。拷不动(占用/权限)→ 落回既有重编码路径。
                try { File.Copy(slotSrc[j], dst, true); continue; }
                catch { }
            }
            try { EngineService.ConvertPngToJpg(slotSrc[j], dst, VideoFrameJpgQuality); }
            catch (Exception ex)
            {
                AppLogger.Warn($"⚠ 补回输出转 JPG 失败({Path.GetFileName(slotSrc[j])}):{ex.Message.Split('\n')[0]}——复制原帧内容");
                try { File.Copy(slotSrc[j], dst, true); } catch { }
            }
        }
        // 输出写完才允许清理临时目录
        foreach (var d in tempoTempDirs) { try { Directory.Delete(d, true); } catch { } }
        double fps = written > 1 ? (written - 1) / T : F;
        AppLogger.Info($"{stageName}:关键帧 {n}(源号 {idx[0]}..{idx[^1]}) → 输出 {written} 帧 @ {fps:0.##} fps(目标 {F:0.##}),时长 {T:0.###}s,逐槽-s {slots.Count} 次(静止对 {pairEqCache.Count(e => e.Value)})");
        return (written, fps, anyBlack);
    }

    /// <summary>【F1】黑帧换路重算:层批原语(InterpLayerBatchAsync)只有 ncnn 一条路,检出黑帧 = 这条路在本机已不可信,
    /// 于是【按同一张逐槽排程】换引擎重算插值帧;所有换路都失败则抛 <see cref="BlackFrameRerouteException"/> 让任务失败
    /// —— 绝不把黑帧当成功产物交付。
    /// 【为什么按槽表重算,而不是"把这一段交给分段补帧路径"】分段补帧(InterpSegmentAsync)做的是
    /// "整段按 -n 均匀插值",与本阶段"每个输出槽落在精确 φ"的排程不是一回事:交给它就会把槽位/输出帧数/时间轴改掉,
    /// 正是用户点名不许发生的"补帧回退导致帧数/时间轴错乱"。而分段路径的降级链(ncnn→ONNX→换卡、绝不落 CPU)
    /// 在这里以【逐槽等价物】复用:① ONNX(DirectML,任意时间步) → ② 换另一块显卡 ncnn 单对 -s(唯一可靠的任意时间步原语)。
    /// 【代价】重算 slots.Count 帧(ONNX 1080p 约 0.5 秒/帧;逐槽 ncnn 约 0.4 秒/槽),只在检出黑帧时才走 ——
    /// 这是"用速度换不把黑帧交给成片"的取舍(用户口径:实在不行宁可明确失败,也不交付坏片)。
    /// 【成功时】直接改写 slotSrc(输出循环随后照旧写帧),帧数/时间轴与层批路完全一致。</summary>
    private static async Task RerouteBlackSlotsAsync(string rife, string interpModel, bool tta, int gpuId,
        string[] files, System.Collections.Generic.List<(int i, int j, double phi)> slots, string[] slotSrc,
        System.Collections.Generic.List<string> tempDirs, string stageName,
        int blackDefects, int blackChecked, System.Collections.Generic.List<string> blackSamples,
        IProgress<(int pct, string msg)>? progress, CancellationToken ct)
    {
        string sampleText = blackSamples.Count > 0 ? $";样本:{string.Join("、", blackSamples)}" : "";
        AppLogger.Warn($"⚠ {stageName}:检测到补帧输出黑帧 {blackDefects} 帧(共抽样 {blackChecked} 帧{sampleText})"
            + " —— 该路(层批原语只有 ncnn)在本机已不可信,改走换路重算(绝不把黑帧交给成片)");
        progress?.Report((40, $"⚠ 检出黑帧:按同一时间轴换引擎重算 {slots.Count} 帧(不把黑帧交给成片)…"));

        // 换路档位:ONNX 可用?有别的卡?模型支持任意时间步? —— 计划本身是纯逻辑(可单测),见 AlhPro.Core.BlackFrameRecovery.Plan
        // 【任务级选卡】优先用【本次任务选定的那块卡】(ncnn 的 -g 编号 gpuId → DirectML 设备号),
        // 只有 gpuId<0(自动/未指定)才回落到全局设置与启动探测结论 —— 否则"任务里选了另一张卡"时,
        // 换路会跑到别的卡上去,与"换卡"档位的语义直接冲突。
        int engineGpu = gpuId >= 0 ? gpuId : AppSettings.GpuIndex;
        int dmlGpu = -1;
        bool onnxUsable = false;
        try
        {
            if (RifeOnnxService.Available() && !EsrganOnnxService.DmlDeviceDead)
            {
                dmlGpu = engineGpu >= 0
                    ? EngineService.ResolveDmlDevice(engineGpu) : EsrganOnnxService.DmlFallbackOk;
                onnxUsable = dmlGpu >= 0;
            }
        }
        catch { onnxUsable = false; }
        int? altGpu = null;
        try
        {
            var devs = VulkanCheck.Devices;
            if (devs.Count >= 2) altGpu = devs.FirstOrDefault(d => d.Id != gpuId).Id;
        }
        catch { }
        bool v4Model = IsV4Model(interpModel);
        var steps = AlhPro.Core.BlackFrameRecovery.Plan(onnxUsable, altGpu.HasValue, v4Model);
        bool triedOnnx = false, triedAlt = false;
        int stillBad = blackDefects;   // 换路后复查仍为真缺陷的帧数(全失败时保持"原始检出数",不编数字)
        var rerouteDirs = new System.Collections.Generic.List<string>();   // 换路临时目录(失败时清干净)
        if (steps.Length == 0)
        {
            // 【说清"为什么没得换"】原先这句只笼统说"没有可用档位",排查时看不出是模型缺失、设备解析失败
            // 还是根本没第二块卡(复核报告点名:H1/H2 的设备路由在名字匹配不到时会返回 -1,用户看到的却是别的说法)。
            var why = new System.Collections.Generic.List<string>();
            if (!RifeOnnxService.Available()) why.Add("未找到 ONNX 补帧模型 rife49.onnx");
            else if (EsrganOnnxService.DmlDeviceDead)
                why.Add("DirectML 设备已被系统摘除/挂死(本进程内不可恢复,需重启软件)");
            else if (dmlGpu < 0)
                why.Add($"无法把 GPU 编号 {engineGpu} 解析成可用的 DirectML 设备(名称匹配不到时按 -1 处理,不会自行改卡)");
            if (!altGpu.HasValue) why.Add("本机没有第二块显卡可换");
            else if (!v4Model) why.Add($"补帧模型 {interpModel} 非 v4 架构,单对 -s 会被引擎忽略,换卡也无意义");
            AppLogger.Warn($"⚠ 黑帧换路:没有可用的换路档位 —— {string.Join(";", why)};只能按失败收尾");
        }
        foreach (var step in steps)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            string outDir = Path.Combine(EngineService.TempRoot, "imgup_blackfix", $"{step}_{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(outDir);
                tempDirs.Add(outDir);
                rerouteDirs.Add(outDir);
                AppLogger.Info($"黑帧换路 {AlhPro.Core.BlackFrameRecovery.StepName(step)}(阶段 {stageName},待重算 {slots.Count} 帧)");
                if (step == AlhPro.Core.BlackFrameRecovery.Step.OnnxSlots)
                {
                    triedOnnx = true;
                    stillBad = await OnnxResampleSlotsAsync(dmlGpu, slots, files, slotSrc, outDir, progress, ct)
                        .ConfigureAwait(false);
                }
                else
                {
                    triedAlt = true;
                    stillBad = await NcnnResampleSlotsAsync(rife, interpModel, tta, altGpu!.Value, slots, files, slotSrc,
                        outDir, progress, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // 该路整体失败(建会话失败/设备不可用/引擎进程失败):不编数字,按"原始检出数"继续下一路。
                AppLogger.Warn($"⚠ 黑帧换路失败({AlhPro.Core.BlackFrameRecovery.StepName(step)}):{ex.Message.Split('\n')[0]}");
                continue;
            }
            if (stillBad == 0)
            {
                AppLogger.Info($"✅ 黑帧换路成功:{AlhPro.Core.BlackFrameRecovery.StepName(step)}重算 {slots.Count} 帧,"
                    + "逐帧复查无黑帧(输出帧数/时间轴与层批路完全一致,未回退源帧、未改帧数)");
                progress?.Report((40, $"✅ 黑帧换路成功:已用 {AlhPro.Core.BlackFrameRecovery.StepName(step)} 重算 {slots.Count} 帧并复查通过"));
                return;
            }
            AppLogger.Warn($"⚠ 黑帧换路后仍有 {stillBad} 帧真缺陷({AlhPro.Core.BlackFrameRecovery.StepName(step)}),继续下一路");
        }
        // 全部换路失败 = 本次任务按失败收尾:先把换路用的临时帧清干净(半成品帧留在临时目录里既占盘、
        // 又可能被后续排查误当成"已产出的帧"),再抛可读异常。
        foreach (var d in rerouteDirs) { try { Directory.Delete(d, true); } catch { } }
        throw new BlackFrameRerouteException(AlhPro.Core.BlackFrameRecovery.FailureMessage(
            stageName, slots.Count, Math.Max(stillBad, blackDefects), triedOnnx, triedAlt));
    }

    /// <summary>黑帧换路①:ONNX(DirectML)按同一张槽表逐槽重算(任意时间步 = rife49.onnx 的原生能力)。
    /// 返回重算后【逐帧复查】仍为真缺陷的帧数(0 = 全部干净)。异常 = 该路整体失败,由调用方换下一路。</summary>
    private static async Task<int> OnnxResampleSlotsAsync(int dmlGpu,
        System.Collections.Generic.List<(int i, int j, double phi)> slots, string[] files, string[] slotSrc,
        string outDir, IProgress<(int pct, string msg)>? progress, CancellationToken ct)
    {
        // 并行路数按可用显存动态定(F3 同一口径):显存紧的机器退到 1 路,宁可慢也不 E_OUTOFMEMORY。
        double? freeVram = SafeRender.FreeVramMeasured ? SafeRender.FreeVramGB : null;
        int concurrency = AlhPro.Core.RenderPolicy.OnnxSessionConcurrency(true, SafeRender.EffectiveVramGB, freeVram);
        if (concurrency > slots.Count) concurrency = Math.Max(1, slots.Count);
        AppLogger.Warn($"⚠ 黑帧换路:改用 ONNX 补帧(DirectML 设备 {dmlGpu})按同一槽表重算 {slots.Count} 帧,{concurrency} 路"
            + $"(依据:{AlhPro.Core.RenderPolicy.OnnxConcurrencyRule(true, SafeRender.EffectiveVramGB, freeVram)})"
            + " —— 帧数/时间轴一字不改,只换引擎;耗时会明显变长(用速度换不把黑帧交给成片)");
        // 【代价提示(诚实口径)】规模大时说清"要等多久、可以放弃",而不是让用户只看到进度条慢慢爬:
        // 速率取 RifeOnnxService 的实测锚点(1080p 整帧约 480ms/帧),只做量级估算(低分辨率更快、4K 更慢)。
        if (slots.Count > 2000)
        {
            double estMin = slots.Count * 0.48 / Math.Max(1, concurrency) / 60.0;
            AppLogger.Warn($"⚠ 黑帧换路规模较大:需重算 {slots.Count} 帧,按 1080p 实测锚点(约 0.48 秒/帧 × {concurrency} 路)"
                + $"量级估算约 {estMin:0} 分钟(分辨率越低越快、4K 更慢);如需放弃本次换路,可直接「强制结束」");
            progress?.Report((40, $"⚠ 黑帧换路:需重算 {slots.Count} 帧,量级估算约 {estMin:0} 分钟(不把黑帧交给成片)…"));
        }
        var sessions = RifeOnnxService.CreateSessions(concurrency, dmlGpu);
        int done = 0;
        try
        {
            await Task.Run(() =>
            {
                var workers = new System.Threading.Tasks.Task[concurrency];
                for (int w = 0; w < concurrency; w++)
                {
                    int wi = w;
                    workers[w] = System.Threading.Tasks.Task.Run(() =>
                    {
                        var sess = sessions[wi];
                        for (int k = wi; k < slots.Count; k += concurrency)
                        {
                            ct.ThrowIfCancellationRequested();
                            var (i, j, phi) = slots[k];
                            // 【线程安全】每个 j 在槽表里只出现一次(槽表唯一)→ 每个输出路径只有一个 worker 写它;
                            // 全部读写都发生在 WaitAll 之后,无竞态,故不加锁。
                            RifeOnnxService.InterpWithSession(sess, files[i], files[i + 1], (float)phi,
                                Path.Combine(outDir, $"slot_{j:D6}.png"), dmlGpu);
                            slotSrc[j] = Path.Combine(outDir, $"slot_{j:D6}.png");
                            int dn = Interlocked.Increment(ref done);
                            if ((dn & 0x1F) == 0 || dn == slots.Count)
                                progress?.Report((40, $"⚠ 黑帧换路(ONNX):已重算 {dn}/{slots.Count} 帧…"));
                        }
                    }, ct);
                }
                try { System.Threading.Tasks.Task.WaitAll(workers); }
                catch (System.AggregateException ae)
                {
                    if (ae.Flatten().InnerExceptions.OfType<OperationCanceledException>().Any() || ct.IsCancellationRequested)
                        throw new OperationCanceledException(ct);
                    throw;
                }
            }, ct).ConfigureAwait(false);
        }
        finally { foreach (var s in sessions) try { s.Dispose(); } catch { } }
        return VerifyReroutedSlots(slots, files, slotSrc);
    }

    /// <summary>黑帧换路②:换另一块显卡,用 ncnn 单对 -s 逐槽重算。
    /// 【为什么必须是单对模式】实测教训(rife-ncnn-vulkan):目录模式【忽略 -s】,时间步只对
    /// 单对 `-0/-1/-o` 生效;所以"任意时间步"在 ncnn 上唯一可靠的原语就是逐槽单对调用(每槽一次引擎进程)。
    /// 也正因如此它只在 v4 架构模型上可用(v2 系模型加 -s 会被忽略 = 拿同样的帧白跑一遍),
    /// 调用方已按 <see cref="AlhPro.Core.BlackFrameRecovery.Plan"/> 过滤。返回复查后仍为真缺陷的帧数。</summary>
    private static async Task<int> NcnnResampleSlotsAsync(string rife, string interpModel, bool tta, int altGpu,
        System.Collections.Generic.List<(int i, int j, double phi)> slots, string[] files, string[] slotSrc,
        string outDir, IProgress<(int pct, string msg)>? progress, CancellationToken ct)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var ttaArgs = tta ? (IsV4Model(interpModel) && interpModel == "rife-v4.26" ? "" : " -x -z") : "";
        AppLogger.Warn($"⚠ 黑帧换路:换 GPU {altGpu}(另一块显卡)ncnn 单对 -s 逐槽重算 {slots.Count} 帧"
            + "(每槽一次引擎进程;耗时会明显变长,用速度换不把黑帧交给成片)");
        for (int k = 0; k < slots.Count; k++)
        {
            ct.ThrowIfCancellationRequested();
            var (i, j, phi) = slots[k];
            string outp = Path.Combine(outDir, $"slot_{j:D6}.png");
            try
            {
                await RunAsync(rife,
                    $"-0 \"{files[i]}\" -1 \"{files[i + 1]}\" -o \"{outp}\" -s {phi.ToString("0.####", inv)} "
                    + $"-m {interpModel} -g {altGpu}{ttaArgs}{SafeRender.GetEngineThreadArgs()}",
                    null, ct, "补帧").ConfigureAwait(false);
                slotSrc[j] = outp;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // 逐槽失败:不改 slotSrc[j](它仍是那条坏路的输出)→ 后面的逐帧复查会把它算成缺陷,
                // 于是这一路判为"没修好",由调用方决定换下一路还是失败。绝不静默换成别的帧。
                AppLogger.Warn($"⚠ 黑帧换路(换卡)第 {j} 槽重算失败({ex.Message.Split('\n')[0]})");
            }
            if (((k + 1) & 0x1F) == 0 || k + 1 == slots.Count)
                progress?.Report((40, $"⚠ 黑帧换路(换卡 GPU {altGpu}):已重算 {k + 1}/{slots.Count} 帧…"));
        }
        return VerifyReroutedSlots(slots, files, slotSrc);
    }

    /// <summary>换路重算后的【逐帧复查】:这批帧就是要交付的帧,所以默认【全查】(不抽样)。
    /// 全查成本 = 每帧一次解码(1080p 约 15ms);只有槽数极大(&gt;4000)时才退回抽样计划,
    /// 避免给已经出故障的任务再压上几分钟的纯解码时间(抽样口径与自检同一套,见 Core.DefectSampling)。
    /// 返回仍为真缺陷的帧数;判据与自检一致:输出近黑【且】两端源帧都不是近黑(任一端近黑 = 素材黑场,放行)。</summary>
    private static int VerifyReroutedSlots(System.Collections.Generic.List<(int i, int j, double phi)> slots,
        string[] files, string[] slotSrc)
    {
        int[] plan = slots.Count <= 4000
            ? System.Linq.Enumerable.Range(0, slots.Count).ToArray()
            : AlhPro.Core.DefectSampling.Plan(slots.Count);
        int bad = 0;
        foreach (int k in plan)
        {
            var (i, j, _phi) = slots[k];
            try
            {
                string f = slotSrc[j];
                if (!File.Exists(f)) { bad++; continue; }            // 没产出 = 缺陷(与"0 帧防御"同口径)
                if (!EngineService.IsBlackPngStrict(f)) continue;    // 只判真近黑:不把"没写完"当黑(见 IsBlackPngStrict 注释)
                if (!AlhPro.Core.BlackFrameRecovery.IsRealDefect(true,
                        EngineService.IsBlackPngStrict(files[i]), EngineService.IsBlackPngStrict(files[i + 1])))
                    continue;                                        // 两端源帧都是黑场 = 内容本来就黑,不算故障
                bad++;
            }
            catch { bad++; }   // 解码失败(0 字节/坏帧)同样算缺陷
        }
        return bad;
    }

    /// <summary>【任务 S2 · 生产接线这半】算全片"逐对相邻源帧"的图像指标(帧差 + 拉普拉斯能量),
    /// 喂给 <see cref="AlhPro.Core.SceneCutJudge.Detect"/> 判场景硬切。
    /// 【为什么交给 ffmpeg 一条灰色 rawvideo 通道】逐帧用 GDI+ 解码采样要按帧付解码开销(几千帧就是几十秒);
    /// 交给 ffmpeg 做 `-vf scale=…,format=gray -f rawvideo` = 一次解码 + 一次降采样,落一个临时 raw 文件,
    /// 再**按帧流式读**算指标(常驻内存只有 2 帧);算完即删(不留垃圾)。采样尺寸见 Core.SceneCutMetrics。
    /// 【失败怎么办】任一环节失败 → 返回空指标 ⇒ Detect 得到 0 处切点 ⇒ "不做切点保护"。
    /// 这个降级**等于改动前的行为**(旧路径本来就不做切点保护),不会让处理失败、也不会改变 CFR 路径。
    /// 【待实测标定】采样高度(192)与 SceneCutJudge 的阈值(帧差 ≥25/≥50、拉普拉斯比 ≤0.6)都需真机复测:
    /// 【2026-09-14】这三个阈值原先经"在线参数覆盖层"读,该功能已整体删除 → 就是上面那几个内置值(逐字不变)。
    /// 阈值是在**原分辨率**上标定的(实测 58.68 / 0.474),换到 192 行采样后绝对量级会变。</summary>
    private static async Task<(double[] diff, double[]? lapVar)> ComputeSceneCutMetricsAsync(
        string ffmpeg, string framesDir, int srcW, int srcH, int frameCount, CancellationToken ct)
    {
        var (sw, sh) = AlhPro.Core.SceneCutMetrics.SampleSize(srcW, srcH);
        if (sw < 3 || sh < 3 || frameCount < 2) return (Array.Empty<double>(), null);
        string raw = Path.Combine(Path.GetDirectoryName(framesDir) ?? ".", $"scenecut_{Guid.NewGuid():N}.gray");
        try
        {
            var args = $"-y -framerate 1 -i \"{Path.Combine(framesDir, "frame_%06d.jpg")}\" "
                + $"-vf \"scale={sw}:{sh},format=gray\" -f rawvideo -pix_fmt gray \"{raw}\"";
            await RunAsync(ffmpeg, args, null, ct, "场景切换检测").ConfigureAwait(false);
            var fi = new FileInfo(raw);
            int frameBytes = sw * sh;
            if (!fi.Exists || fi.Length < (long)frameBytes * 2) return (Array.Empty<double>(), null);
            int avail = (int)Math.Min(int.MaxValue, fi.Length / frameBytes);
            int n = Math.Min(frameCount, avail);
            if (n < 2) return (Array.Empty<double>(), null);
            var diff = new double[n - 1];
            var lap = new double[n];
            var prev = new byte[frameBytes];
            var cur = new byte[frameBytes];
            int done = 0;
            using (var fs = new FileStream(raw, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            {
                for (int i = 0; i < n; i++)
                {
                    if (ct.IsCancellationRequested) break;
                    if (await ReadExactAsync(fs, cur).ConfigureAwait(false) != frameBytes) break;
                    AlhPro.Core.SceneCutMetrics.MeasurePair(prev, cur, sw, sh, out double d, out _, out double? lapCur);
                    lap[i] = lapCur ?? 0;
                    if (i > 0) diff[i - 1] = d;
                    Buffer.BlockCopy(cur, 0, prev, 0, frameBytes);
                    done++;
                }
            }
            if (done < 2) return (Array.Empty<double>(), null);
            if (done != n) { Array.Resize(ref diff, done - 1); Array.Resize(ref lap, done); }
            return (diff, lap);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLogger.Info($"场景切换检测未完成({ex.Message.Split('\n')[0]})→ 本次不做切点保护(与改动前行为一致)");
            return (Array.Empty<double>(), null);
        }
        finally { try { File.Delete(raw); } catch { } }
    }

    /// <summary>把缓冲读满(短读循环)。返回实际读到的字节数(&lt; 期望 = 流到头)。</summary>
    private static async Task<int> ReadExactAsync(Stream s, byte[] buf)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int r = await s.ReadAsync(buf.AsMemory(off, buf.Length - off)).ConfigureAwait(false);
            if (r <= 0) break;
            off += r;
        }
        return off;
    }

    /// <summary>【任务 S1 核心】"平滑时间轴"合成:按**真实时间轴**逐槽合成,取代"按段 `-n` 均匀铺帧"。
    /// 【为什么需要】用户实测口径:把时间轴"填平"(输出统一帧率)后,开头 1~3 秒的顿挫感消失 ——
    /// 消除的是源的不均匀节奏(VFR 的 33.3ms 顿挫),不是"往缺口里填运动"(缺口内部几乎无变化,实测 0.871/0.150)。
    /// 【怎么做】对每个目标时刻 t(均匀网格 i/目标帧率):用 <see cref="AlhPro.Core.TimelineFlattenPlan.MapTargetFrame"/>
    /// 取**包住 t 的两张源帧 + φ**;再经 <see cref="AlhPro.Core.CutAwareSchedule"/> 做**切点对齐**:
    ///   · t 正好落在源帧上(φ=0/1)→ 直接 File.Copy 该源帧,**不进引擎**(避免静止内容被"猜"出差异 —— 小样实测的软化就来自这里);
    ///   · 0&lt;φ&lt;1 且这一对**不是**场景硬切 → 交既有层批原语合成;
    ///   · 这一对**是**场景硬切 → **绝不合成**:φ&lt;0.5 拷前一场景帧、φ≥0.5 从新场景帧开始 →
    ///     输出里不会再有"上个镜头的字叠在新镜头上"的鬼影帧(实测鬼影比 0.647/0.692),
    ///     切点处也不再出现"只剩 7.8% 高频"的严重软化帧(那帧本来就是混合出来的)。
    /// 【帧数/时长守恒】目标帧数 = <see cref="AlhPro.Core.TimelineFlattenPlan.Decide"/> 给出的数(round(真实时长×目标帧率)),
    /// 而"切点对齐"只把**混合槽改写成拷贝槽**,不增删槽 → 帧数与时间轴位置都不变,
    /// 所以不需要"在切点前后各补 1~2 帧同场景拷贝来对齐时轴"(帧数守恒是更强的约束)。
    /// 总时长由调用方按"帧数 ÷ 源容器时长"标称(见 ProcessVideoAsync 的"帧率保险"),偏差 ≤ 半帧。
    /// 【落地方式】完全复用"补回(还原源时间轴)"那条路的既有层批并发结构(EngineService.InterpLayerBatchAsync),
    /// 只是把"槽位怎么排"换成上面这套 —— **没有**新写并发与降级链。
    /// 【失败/黑帧】帧数不符、引擎检出黑帧 → 抛异常,由调用方清掉半成品并回退分段补帧(那里有完整降级链)。
    /// 【已知代价(照实写,不靠偷偷加锐化找补)】源里"本来静止"的缺口处会插出轻微软化(小样实测中位 ≈6%);
    /// 切点处由"糊一帧"变成"硬切两帧"——那是切点本身的性质,视觉上更对。</summary>
    private static async Task<(int written, int cuts, int forcedCopies, bool anyBlack)> FlattenTimelineAsync(
        string rife, string segSrcDir, string segOutDir,
        int frameCount, System.Collections.Generic.List<double>? frameDurs,
        AlhPro.Core.TimelineFlattenPlan.Plan plan, int gpuId, string interpModel, bool tta,
        System.Collections.Generic.IReadOnlyList<int> cutPairs,
        IProgress<(int pct, string msg)>? progress, CancellationToken ct)
    {
        if (frameDurs == null || frameDurs.Count != frameCount)
            throw new InvalidOperationException($"源时长表与帧数不一致(时长表 {(frameDurs?.Count ?? -1)} 项 / 帧数 {frameCount})");
        var files = Directory.EnumerateFiles(segSrcDir, "*.jpg")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length != frameCount)
            throw new InvalidOperationException($"合成输入 {files.Length} 帧 ≠ 时长表 {frameCount} 帧(帧号与时长表索引不可信)");

        // ===== ① 切点直接吃主流程传进来的共享表 —— 本方法**不再**自行采样、不再自行判定(2026-09-15 统一)=====
        // 【为什么必须共享】这条"按真实时间戳排帧"的路原先自己又调了一次采样 + 一次判据:同一份判据、两次全片
        // 解码、两套口径;最糟的是它**无视「转场识别」开关**(用户明明关了,这条路却还在做切点保护)。
        // 现在全流程只在 3a 块里算一次,本方法只消费结论。
        // cutPairs 口径 = 源帧下标 i(切点在源帧 i 与 i+1 之间),正是 CutAwareSchedule 要的口径。
        AppLogger.Info($"时间轴:切点对齐用主流程同一份判据(同一次采样),切点 {cutPairs.Count} 处"
            + (cutPairs.Count == 0 ? "(「转场识别」未勾选,或本次未检出)" : ""));
        var cuts = cutPairs;

        // ===== ② 排程(纯函数):每个目标槽 = 拷哪张源帧 / 在源帧对之间按 φ 合成 =====
        var slotsPlan = AlhPro.Core.CutAwareSchedule.PlanAll(frameDurs, plan.TargetFrames, plan.TargetFps, cuts, out int forced);
        if (slotsPlan.Count != plan.TargetFrames)
            throw new InvalidOperationException($"时间轴排程槽数 {slotsPlan.Count} ≠ 目标帧数 {plan.TargetFrames}(映射越界)");

        // ===== ③ 排程 → 槽位表(拷贝槽 / 引擎槽)=====
        int outN = slotsPlan.Count;
        var slotSrc = new string[outN];
        var engineSlots = new System.Collections.Generic.List<(int i, int j, double phi)>();
        var perPair = new System.Collections.Generic.Dictionary<int, int>();
        int copySlots = 0;
        for (int j = 0; j < outN; j++)
        {
            var s = slotsPlan[j];
            if (s.Copy)
            {
                slotSrc[j] = files[Math.Clamp(s.CopyIndex, 0, files.Length - 1)];
                copySlots++;
                continue;
            }
            int i0 = Math.Clamp(s.Idx0, 0, files.Length - 2);
            slotSrc[j] = files[i0];   // 兜底值:引擎缺帧时层批原语会退回它
            engineSlots.Add((i0, j, s.Phi));
            perPair[i0] = perPair.TryGetValue(i0, out var c) ? c + 1 : 1;
        }
        AppLogger.Info($"平滑时间轴:排程 {outN} 槽(直接拷贝 {copySlots} 槽 / 引擎合成 {engineSlots.Count} 槽;"
            + $"其中因切点强制改拷贝 {forced} 槽);目标 {plan.TargetFps:0.##} fps、真实时长 {plan.TotalSeconds:0.###}s");

        // ===== ④ 交给既有层批原语落盘(并发/降级/黑帧提示/临时目录清理都在那里)=====
        var res = await RunTempoResampleAsync(rife, segSrcDir, segOutDir, frameCount, plan.TargetFps,
            null, 1, plan.TargetFps, gpuId, plan.TotalSeconds, interpModel, tta, progress, ct,
            planSlots: engineSlots, planSlotSrc: slotSrc,
            slotsInPair: p => perPair.TryGetValue(p, out var c) ? c : 1,
            stageName: "平滑时间轴", preferFileCopyForJpgSlot: true).ConfigureAwait(false);
        return (res.frameCount, cuts.Count, forced, res.anyBlack);
    }

    /// <summary>智能模式:自适应去重——不固定阈值,先算素材相邻帧差分布,再自动定"重复帧"分界。
    /// 高动态素材(前后差异大)能自动找到重复簇,精确去重;低动态素材自动收紧到只删"几乎完全相同",
    /// 防止像固定阈值那样把帧删光。判据:相邻帧差的中位数/分布自适应(低动态素材保守,只删几乎相同)+ 低动态保护。
    /// scale=采样粒度(px,全局可调):越细对动漫细线条/口型等细节越敏感。
    /// smartMode 策略:0=均衡(Otsu+低动态分支+段合并 0.95/4) 1=激进(阈值放宽,接近动漫/敏感)
    /// 2=保守(只删几乎相同+长静止段,微动不碰)。</summary>
    private static System.Collections.Generic.HashSet<int> DetectDupFramesAdaptive(string framesDir,
        IProgress<(int pct, string msg)>? progress, int scale = 16, int smartMode = 0, bool motionComp = true,
        System.Threading.CancellationToken ct = default, string stage = "去重分析")
    {
        var drop = new System.Collections.Generic.HashSet<int>();
        var files = EnumerateFrameFiles(framesDir)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length < 2) return drop;
        // 第一遍:采样帧灰度,算相邻对统计标量(逐段报告,检测不会像卡死)
        var hb = new DedupHeartbeat(progress, stage, files.Length);
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
            {
                ct.ThrowIfCancellationRequested();   // 可取消(见 DetectDupFramesWithSsim 注释)
                hb.Step(i, 0);
            }
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
        // 【任务 M4】系数与四个缩放公式已迁到 AlhPro.Core.DedupTier(一个来源 + 单测保证严格单调)。
        // ⚠ 注意:本函数【不是】智能模式的执行路径 —— 智能先去跑拍数识别 + 网格采样,识别不出时走
        //   「回退帧差+SSIM」(同样按 DedupTier 力度缩放),只有"全文分析"按钮等少数入口才调用本函数。
        //   所以三档差异的落地证明要看回退路径的日志(智能检测(...)回退帧差+SSIM:力度 ×1.5/×1.0/×0.7 ...)。
        double force = AlhPro.Core.DedupTier.Force(smartMode);
        sadThr = AlhPro.Core.DedupTier.ScaleSad(sadThr, force);
        // 只删真定格(与主判重一致):SSIM 阈值设 ≥0.99 下限(均衡档 0.99,激进 0.985,保守 0.995),
        // 相似但连续运动的帧不再被当重复删;人物定格交给"镜头运动补偿判据"(对齐残差极小)识别。
        // 注:上一版下限 0.995 过严 → 拍2素材只删到 22.6fps,现放宽到 0.99(拍N 重复帧结构相同度约 0.99x)。
        ssimThr = AlhPro.Core.DedupTier.ScaleSsim(ssimThr, force, smartMode);
        smartProtect = AlhPro.Core.DedupTier.ScaleProtect(smartProtect, force);
        segSsim = AlhPro.Core.DedupTier.ScaleSsim(segSsim, force, smartMode);
        segSad = AlhPro.Core.DedupTier.ScaleSegSad(segSad, force);
        string forceName = AlhPro.Core.DedupTier.Name(smartMode);
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
        hb.Done(drop.Count);
        return drop;
    }

    /// <summary>手动-语义运动分析:静止帧(帧差小+SSIM 高)或镜头平移(全图均匀小动)判为冗余删掉;
    /// 局部动作(人物张嘴等块变化集中)与真实场景切换保留。panAvgThr=镜头运动阈值(1~10);
    /// maxDiffThr=镜头平移时单块最大差异上限(排除场景切换/爆炸);scale/blockThr=采样粒度/变化块判线。</summary>
    private static System.Collections.Generic.HashSet<int> DetectDupFramesWithMotion(string framesDir, double panAvgThr,
        IProgress<(int pct, string msg)>? progress, int scale = 16, double protect = 0.12, double blockThr = 4,
        double maxDiffThr = 20, System.Threading.CancellationToken ct = default, string stage = "去重分析(镜头运动)")
    {
        var drop = new System.Collections.Generic.HashSet<int>();
        var files = EnumerateFrameFiles(framesDir)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length < 2) return drop;
        var hb = new DedupHeartbeat(progress, stage, files.Length);
        var prev = SampleGray(files[0], scale, out var sw, out var sh);
        for (int i = 1; i < files.Length; i++)
        {
            if ((i & 31) == 0)
            {
                ct.ThrowIfCancellationRequested();   // 可取消(见 DetectDupFramesWithSsim 注释)
                hb.Step(i, drop.Count);
            }
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
        hb.Done(drop.Count);
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

    /// <summary>文件内容 SHA-256 十六进制摘要(超分去重分组用;失败返回空串,调用方按无重复处理)。</summary>
    private static string ContentHash(string path)
    {
        try
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var h = sha.ComputeHash(fs);
            var sb = new System.Text.StringBuilder(h.Length * 2);
            foreach (var b in h) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
        catch { return ""; }
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
                "导致输出视频只有几帧(打不开/没有补帧效果)。请降低去重强度,或改用动漫/内容帧率模式。");
        }
    }

    /// <summary>校验视频文件确实是可读的有效视频(非空 + ffprobe 能读出视频流 + 帧数下限)。
    /// 硬件编码失败时 ffmpeg 可能留下 0 字节/损坏的文件但被 File.Exists 误判为成功,这里兜底。
    /// 【minFrames 必须区分两种用途,不能都用 5】
    ///   · 编码器探测(ProbeEncodersAsync):要求 ≥5 帧——目的是识破 QSV 那类"能退出 0 但没真编码"的假成功。
    ///   · 真实输出校验:必须传 1——≤4 帧的成片是**合法**的(单帧视频、极短视频、用户确认"去重过强仍要进行"
    ///     后的短输出)。原先两者共用 "&lt;5 即无效",会把合法成片判死、**删掉已经编码好的文件**,
    ///     还抛"输出文件无效(无法被解码)"这种与事实不符的错误(文件其实完全可解码),用户白等一整轮编码。</summary>
    private static async Task<bool> ValidateVideoFileAsync(string path, int minFrames = 5)
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
            // 【修复】校验进程加超时:ffprobe 曾可能挂起,加 30 秒超时,超时就杀进程树并视为无效(避免卡住)。
            var exitTask = p.WaitForExitAsync();
            var finished = await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false);
            if (finished != exitTask || !p.HasExited)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            await exitTask.ConfigureAwait(false);
            var outp = (await outTask).Trim();
            await errTask;
            if (p.ExitCode != 0 || !outp.Contains("video", StringComparison.OrdinalIgnoreCase)) return false;
            // 帧数下限检查:阈值由调用方给(探测=5,真实输出=1,见方法注释)
            var fields = outp.Split(',');
            if (fields.Length >= 2 && int.TryParse(fields[1], out var nb) && nb < minFrames) return false;
            return true;
        }
        catch { return false; }
    }

    /// <summary>最近一次「输出端黑场自检」发现的黑场片段描述(空 = 未扫过或没发现)。供 UI/任务摘要附加显示。</summary>
    public static string LastBlackScanResult { get; private set; } = "";

    /// <summary>扫描【成片】里有没有"整片全黑"的片段,返回形如 "1.2s~1.6s、8.4s~8.9s" 的描述(无则空串)。
    /// 【为什么必须有这一步】黑帧防线原先只覆盖"超分引擎输出"那一步(ConvertPngToJpg 时判 isBlack),
    /// 而 **后处理阶段与编码阶段完全没有检测**。真实案例(用户 3070 Laptop · 花熏25.mp4):
    /// 任务全程零 WARN、输出校验通过、用户却看到黑帧 —— 黑帧不管来自哪一步都无人发现、日志无痕。
    /// 做法:抽 6fps → 缩到 320 宽 → blackdetect,只为找"整片全黑",不必原分辨率逐帧,成本低一个数量级。
    /// 【阈值是实测选出来的,别随手改大】pic_th=0.98 要求 ≥98% 像素算"黑",配合 pix_th=0.05(亮度 &lt; 约 12.75/255)。
    /// 本机用捆绑 ffmpeg 实测对比:
    ///   · 暗夜场景(luma=20 深灰,非黑帧):pix_th=0.10 → **误报 1 处**;0.05 → 0 处;0.02 → 0 处
    ///   · 真黑帧(且经 h264 压缩带噪):0.10 / 0.05 / 0.02 三者都能命中 1 处
    /// ⇒ 0.10 会把正常夜景当成黑帧,0.05 既有余量拒掉暗场、又留足余量接住压缩噪声后的真黑帧。
    /// 【2026-09-22 用户裁决:把"源片本来就黑"从缺陷里摘掉】
    ///   起因:整机自测发现这条告警**反复误报** —— 用户素材 `GIRL LIKE ME_1.mp4` 开头本来就是 0.417s 黑场(淡入),
    ///   成片如实复现,却每次都被当成"处理链产生的缺陷"并要求用户发日志给作者 ✗。
    ///   实测证据(成片 0s~0.396s ↔ 源 0s~0.417s;源 `YAVG=16`);另一条历史告警 `3s~3.17s` 换算到源时间
    ///   (那次区间起点 2.738s)正好落在源自带的 `5.755~5.881` 黑段上 ⇒ **同一个误报**。
    ///   根因:流水线内部那条黑帧防线有"相邻源帧本来就黑就放行"的豁免(`BlackFrameRecovery.IsRealDefect`),
    ///   **而输出端这条没有** ⇒ 两条防线口径不一致。
    ///   现在:每处成片黑段都去**源片同位置**探一帧亮度,源片那里也黑 ⇒ 不算缺陷(只在对账行里计数)。
    ///   ⚠ 探不到、探测失败、没传源片 ⇒ **一律照旧报**(宁可多报,不可漏报是这条防线的底线)。
    /// 【时间轴怎么对得上】本工程有硬不变量:输出时长恒 = 原处理时长(结算行每次都核对"成片容器 vs 源容器");
    ///   去重只把被删帧的时长并进前一帧、指定帧率与平滑时间轴都保时长 ⇒ **源时刻 = 裁剪起点 + 成片时刻**
    ///   (误差 ≤ 半帧)。所以这里可以直接相加,不需要另做时间轴映射。</summary>
    private static async Task<string> ScanBlackSegmentsAsync(string videoPath, CancellationToken ct,
        string? sourceForCompare = null, double sourceOffsetSec = 0)
    {
        LastBlackScanResult = "";
        var ffmpeg = FfmpegPath;
        if (ffmpeg == null) return "";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = $"-v info -i \"{videoPath}\" " +
                            $"-vf \"fps=6,scale=320:-2,blackdetect=d=0.15:pic_th=0.98:pix_th=0.05\" " +
                            $"-an -f null -",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return "";
            var errTask = p.StandardError.ReadToEndAsync();
            var exitTask = p.WaitForExitAsync(ct);
            // 加超时:卡住的话不能把整个任务拖住(这一步只是附加检查,失败就当没发现)
            var done = await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromMinutes(5), ct)).ConfigureAwait(false);
            if (done != exitTask)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                AppLogger.Info("输出端黑场自检:超时跳过(不影响成片)");
                return "";
            }
            await exitTask.ConfigureAwait(false);
            string err = await errTask.ConfigureAwait(false);

            var segs = new System.Collections.Generic.List<string>();
            var segRanges = new System.Collections.Generic.List<(double s, double e)>();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                err, @"black_start:([0-9.]+)\s+black_end:([0-9.]+)"))
            {
                bool okS = double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var s);
                bool okE = double.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var e);
                if (okS && okE) { segRanges.Add((s, e)); }
                if (segRanges.Count >= 20) break;   // 别把日志刷爆
            }
            if (segRanges.Count == 0) return "";

            // 【2026-09-22】把"源片同位置也黑"的摘掉(见方法注释):逐段探一帧源片亮度。
            int exempt = 0;
            if (!string.IsNullOrEmpty(sourceForCompare) && File.Exists(sourceForCompare))
            {
                var kept = new System.Collections.Generic.List<(double s, double e)>();
                foreach (var (s0, e0) in segRanges)
                {
                    double at = sourceOffsetSec + (s0 + e0) / 2.0;   // 取该段中点探一帧
                    bool srcBlack = await SourceLooksBlackAtAsync(sourceForCompare, at, ct).ConfigureAwait(false);
                    if (srcBlack) exempt++; else kept.Add((s0, e0));
                }
                if (exempt > 0)
                {
                    AppLogger.Info($"输出端黑场自检:成片 {exempt} 处黑段在**源片同一时刻也是黑的** ⇒ 判定为源自带的黑场"
                        + $"(淡入淡出/夜戏/转场),不再当缺陷上报;剩下 {kept.Count} 处需你核对。"
                        + "口径与流水线内部那条黑帧防线一致(它一直有'相邻源帧本来就黑就放行'的豁免)。");
                }
                segRanges = kept;
            }
            if (segRanges.Count == 0) return "";

            foreach (var (s, e) in segRanges) segs.Add($"{s:0.##}s~{e:0.##}s");
            var summary = string.Join("、", segs);
            LastBlackScanResult = summary;
            AppLogger.Warn($"⚠ 输出端黑场自检:成片含 {segs.Count} 处全黑片段({summary})。"
                + "这些位置**在源片同一时刻不是黑的**,说明是处理链某一步产生的 —— 请把本行发作者。"
                + "常见来源:后处理滤镜(去频闪 deflicker / 去模糊)或 ncnn-Vulkan 队列异常。"
                + (exempt > 0 ? $"（另有 {exempt} 处已按「源片本来就黑」豁免，不计入）" : ""));
            return summary;
        }
        catch (OperationCanceledException) { return ""; }
        catch { return ""; }
    }

    /// <summary>【2026-09-22 加】探"源片在 <paramref name="atSec"/> 附近是不是也黑",用于把**源自带的黑场**
    /// 从"输出端黑场自检"里摘掉(见 <see cref="ScanBlackSegmentsAsync"/> 的说明)。
    ///
    /// 【第一版是错的,照实记】(2026-09-22 自测真机复现)
    ///   第一版解一帧、用 `signalstats` 的 **整帧平均亮度 YAVG &lt; 13** 判黑 —— **在真素材上没生效** ✗:
    ///   用户素材开头那段淡入黑场实测 **YAVG=16**(>13)⇒ 判成"源片不黑" ⇒ 告警照旧。
    ///   根因:扫描端 `blackdetect` 的 `pix_th=0.05` + `pic_th=0.98` 是**"低于阈值的像素占比"**这个统计量
    ///   (≥98% 的像素亮度 &lt; 12.75),而**整帧平均**是另一个统计量 —— 前者能判黑、后者不一定 ✗。
    ///   教训:**判据换统计量就等于换了判据**;要口径一致,就得用**同一个滤镜 + 同一组阈值**。
    /// 【现在怎么做】在源片上开一个**以该时刻为中心的小窗口**(±0.3 秒),跑**与扫描端逐字相同**的
    ///   `fps/scale/blackdetect` 参数,只要窗口内报出黑段 ⇒ 源片这里也黑 ⇒ 豁免。
    ///   这样不需要我再"发明"任何阈值(也就不会有第二个统计量跑偏的机会)。
    /// 【失败一律返回 false(= 不豁免 → 照旧报警)】宁可多报也不许漏报,这是这条防线的底线。
    /// 【成本】每处黑段只解 0.6 秒的窗口,一次任务通常 0~3 处 ⇒ 可忽略。</summary>
    private static async Task<bool> SourceLooksBlackAtAsync(string sourcePath, double atSec, CancellationToken ct)
    {
        var ffmpeg = FfmpegPath;
        if (ffmpeg == null || atSec < 0) return false;
        try
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            double from = Math.Max(0, atSec - 0.3);
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                // ⚠ 阈值与 ScanBlackSegmentsAsync 里那串**逐字相同**(只有 d 从 0.15 收到 0.08,
                //   因为窗口本身只有 0.6 秒;黑段本身比这长得多,不会因此漏判)
                Arguments = $"-v info -ss {from.ToString("0.###", inv)} -t 0.6 -i \"{sourcePath}\" " +
                            $"-vf \"fps=6,scale=320:-2,blackdetect=d=0.08:pic_th=0.98:pix_th=0.05\" " +
                            $"-an -f null -",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            var errTask = p.StandardError.ReadToEndAsync();
            var exitTask = p.WaitForExitAsync(ct);
            var done = await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(20), ct)).ConfigureAwait(false);
            if (done != exitTask) { try { p.Kill(entireProcessTree: true); } catch { } return false; }
            await exitTask.ConfigureAwait(false);
            string err = await errTask.ConfigureAwait(false);
            return System.Text.RegularExpressions.Regex.IsMatch(err, @"black_start:[0-9.]+\s+black_end:[0-9.]+");
        }
        catch { return false; }
    }

    /// <summary>编码参数;quality 0=自动 1=低 2=中 3=高 4=极高(CRF 值递减=画质递增,单调)。
    /// bitrateKbps &gt; 0 = 自定义码率(固定码率,替代质量档);codec 支持 H.264/H.265 各硬编 + CPU。</summary>
    private static string EncoderArgs(string encoder, int quality = 0, double bitrateKbps = 0)
    {
        int q = quality switch { 0 => 22, 1 => 26, 2 => 24, 3 => 20, 4 => 15, _ => 22 };
        int th = SafeRender.GetLibx264Threads();
        // 输出强制标 BT.709/tv:中间帧 JPG 不保留色彩元数据,ffmpeg 读 JPG 用默认 bt470bg/pc/yuvj420p,
        // 对 1080p 高清(BT.709)源会造成红蓝错色(用户实测:源 bt709→输出 bt470bg 红蓝)。统一标正确色彩。
        // (顺带纠正 video 帧 JPG 直走 GDI 后,合帧时色彩元数据缺失导致的同类偏差。)
        const string colorArgs = " -color_range tv -colorspace bt709 -color_primaries bt709 -color_trc bt709";
        // 打包版 ffmpeg(n7.1-20240930)的编码器【静默忽略】-color_trc 与 -color_primaries
        // (libx264 与 h264_nvenc 均实测:只有 -colorspace 落进 VUI,另两个探回来是 unknown),
        // 于是成片缺 bt709 的传递/色域标签,播放器只能按默认猜 → 偏色。唯一可靠写法是用
        // bitstream filter 直接改 SPS 里的 VUI(1=bt709);bsf 作用在编码后的码流上,与具体编码器无关。
        // 若某台机器的硬编 + bsf 编不出有效文件,EnsureHwProbeAsync 的 1 帧真实参数探测会先发现并回退 CPU。
        string vuiBsf = encoder.StartsWith("hevc", StringComparison.OrdinalIgnoreCase) || encoder == "libx265"
            ? " -bsf:v hevc_metadata=colour_primaries=1:transfer_characteristics=1:matrix_coefficients=1"
            : " -bsf:v h264_metadata=colour_primaries=1:transfer_characteristics=1:matrix_coefficients=1";
        if (bitrateKbps > 0)
        {
            int k = (int)Math.Max(100, bitrateKbps);
            string core = encoder switch
            {
                "h264_nvenc" or "hevc_nvenc" => $"-c:v {encoder} -preset p4 -rc vbr -cq 18 -b:v {k}K -maxrate {k}K -bufsize {k * 2}K -pix_fmt yuv420p",
                "h264_amf" or "hevc_amf" => $"-c:v {encoder} -quality quality -rc cbr -b:v {k}K -pix_fmt nv12",
                "h264_qsv" or "hevc_qsv" => $"-c:v {encoder} -b:v {k}K -pix_fmt yuv420p",
                "libx265" => $"-c:v libx265 -preset veryfast -b:v {k}K -maxrate {k}K -bufsize {k * 2}K -pix_fmt yuv420p -x265-params threads={th}",
                _ => $"-c:v libx264 -preset veryfast -b:v {k}K -maxrate {k}K -bufsize {k * 2}K -pix_fmt yuv420p -threads {th}",
            };
            return core + colorArgs + vuiBsf;
        }
        string core2 = encoder switch
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
        return core2 + colorArgs + vuiBsf;
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

    /// <summary>运行命令并返回完整输出行(供解析,如转场检测)。
    /// 【同样带无进展看门狗】这里跑的多是"把整片解码一遍"的探测(freezedetect / scdet / showinfo),
    /// 挂死风险与拆帧同级;而 ProbeHdrToSdrAsync / ProbeVideoCodecName / 帧率探测三处传的是
    /// CancellationToken.None —— 没有看门狗就是永久卡住,用户连「停止」都点不动(只能强制结束)。
    /// 正常探测全程持续输出(metadata=print 每帧一行 + ffmpeg 自己的 frame=/speed= 统计),
    /// 故静默阈值沿用 RunAsync 同一套(启动 90s / 运行 120s),不会误杀"慢但正常"的长片探测。</summary>
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
        var lockObj = new object();
        bool sawAnyOutput = false;      // 是否已出现任何输出:区分"启动即挂死"与"跑了一半挂死"
        bool killRequested = false;
        string? killReason = null;
        long lastLiveTicks = DateTime.Now.Ticks;
        void OnChunk(string chunk)
        {
            lock (lockObj)
            {
                sawAnyOutput = true;
                if (chunk.Length > 0) lastLiveTicks = DateTime.Now.Ticks;
            }
        }
        // keepTail:0 = 全量保留。调用方要逐行解析(scdet 的 score、showinfo 的每帧 pts_time),
        // 截成 8KB 尾巴会直接丢掉前面所有帧。
        var drainOut = DrainAsync(p.StandardOutput, OnChunk, ct, keepTail: 0);
        var drainErr = DrainAsync(p.StandardError, OnChunk, ct, keepTail: 0);

        const int CheckEveryMs = 15_000;                                   // 每 15 秒巡检
        const int StartupSilenceLimitSec = 90;                             // 启动后一直零输出:含 -ss 定位/滤镜初始化/休眠盘唤醒
        const int RunningSilenceLimitSec = 120;                            // 已出过输出后转静默:判定挂死
        using var watchdog = new System.Threading.Timer(_ =>
        {
            try
            {
                lock (lockObj)
                {
                    if (killRequested || p.HasExited) return;
                    // 暂停=子进程已被 NtSuspendProcess 冻结,冻结态与僵死态无法区分 → 喂狗,绝不误杀
                    if (IsPaused) { lastLiveTicks = DateTime.Now.Ticks; return; }
                    int limitSec = sawAnyOutput ? RunningSilenceLimitSec : StartupSilenceLimitSec;
                    if (DateTime.Now.Ticks - lastLiveTicks <= TimeSpan.FromSeconds(limitSec).Ticks) return;
                    killRequested = true;
                    killReason = $"探测子进程 {limitSec} 秒无任何输出(疑似解码器/驱动挂死),已强制终止";
                    AppLogger.Warn($"看门狗:{killReason} — exe={Path.GetFileName(exe)} args={args[..Math.Min(args.Length, 120)]}");
                    try { ResumeActiveProcess(); p.Kill(entireProcessTree: true); } catch { }   // 冻结进程 kill 可能失败,先解冻
                }
            }
            catch { }
        }, null, CheckEveryMs, CheckEveryMs);

        while (!p.HasExited)
        {
            if (ct.IsCancellationRequested)
            {
                try { ResumeActiveProcess(); p.Kill(entireProcessTree: true); } catch { }   // 取消前先解冻(冻结进程 kill 可能失败)
                break;
            }
            if (killRequested) break;   // 看门狗已判死并已杀进程,收手去抛停滞异常
            await Task.Delay(100).ConfigureAwait(false);
        }
        // 顺序保持"先 stderr 全部行、再 stdout 全部行"(与改造前一致):调用方按行解析,换序会读错字段
        // 【必须给上限】理由同 RunAsync:Kill 只是"请求终止",进程卡在内核态驱动调用时不会真退出、
        // 也无法读到 EOF;无限 await 两个管道 = 任务永久卡死(看门狗已经打过"已强制终止"的日志也没用)。
        // 最多等 5 秒,等不到就放弃输出、直接按停滞处理(调用侧本来就有非致命兜底,不会因此误判成功)。
        bool drained = true;
        var drainAll = Task.WhenAll(drainErr, drainOut);
        if (await Task.WhenAny(drainAll, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false) != drainAll)
        {
            drained = false;
            AppLogger.Warn($"⚠ 探测子进程未能在 5 秒内退出(已请求强制终止,pid={p.Id})——放弃等待其输出,按停滞处理。");
        }
        var err = drained ? await drainErr.ConfigureAwait(false) : "";
        var stdout = drained ? await drainOut.ConfigureAwait(false) : "";
        watchdog.Dispose();
        App.ActiveProcesses.Unregister(p.Id);
        if (ct.IsCancellationRequested)
            throw new OperationCanceledException();
        // 进程没退出时不许读 ExitCode(会抛),也不许当成"普通失败"被吞掉 → 统一按停滞上报(ProcessStillRunning=true)
        if (!p.HasExited || !drained)
            throw new EngineStallException(killReason
                ?? "探测子进程在收到终止请求后仍未退出;它可能仍占用显卡或临时目录,已放弃本次处理。"
                   + "请结束软件后重启,再重试。", processStillRunning: true);
        // 停滞必须排在 ExitCode 判断之前:被看门狗杀掉的进程退出码非零,否则会被误报成普通"命令失败"
        if (killRequested && p.ExitCode != 0)
            throw new EngineStallException(killReason ?? "探测子进程长时间无输出,已强制终止");
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
    // ==================== 左右对比:把「左原片 + 右处理后」合成一条对比片(2026-09-18) ====================
    // 【为什么是"合成一条",而不是并排摆两个播放器】
    //   两个 MediaPlayerElement = 两个解码器 + 两条时钟。用户真机反馈的五条症状全是这一个根因:
    //   播放卡 / 有时卡死(seek 纠偏把管线拖死,实测片段卡在 1.8 秒)/ 单边卡 /
    //   左右时间对不上(时钟漂移)/ 播放中拖进度两边不协调。合成成一条后只有一个解码器、一条时钟。
    //
    // 【这条命令是实测钉出来的,不要随手改】(本机 4060 Laptop + 4K 素材实测)
    //   · 左原片用 `-ss/-t` 取到与预览片段同一区间(实测帧级准:带烧录时间码的素材,两边第一帧都是 06.000)
    //   · 两边都过 `fps=<右片帧率>`:**这一步不能省**。补帧/去重会改变右侧帧率(实测 30 / 60 / 120),
    //     不归一化时 hstack 的 framesync 按时间戳补帧,输出会变成 `28800/239 ≈ 120.5` 这种非标帧率;
    //     归一到同一帧率后是干净的 CFR(实测 30/1、120/1,帧数与时长分毫不差)
    //   · `hstack=inputs=2:shortest=1`:默认 shortest=0 会"重复短边末帧"补到长边(成片比原片短 1~3 帧很常见),
    //     那正好就是用户抱怨过的"末帧定格"手感,所以显式要 shortest
    //   · **半边尺寸必须是 (W/2, H/2)** —— 方案初稿写的是 `scale=W2:H`(H=原片高),那是把整幅画面
    //     硬拉进一个竖框,实测会**横向压扁一半**(成品整帧里彩条只剩一半宽、斜线变陡,一眼就看得出变形)。
    //     用户要的是"左右各显示一整幅、能对照同一处",所以每半边等比缩到一半 → 合成片 = W × (H/2)。
    //   · **不要用 CUDA 滤镜**:本机 scale_cuda/overlay_cuda 虽然存在,但 scale_cuda+overlay_cuda 链实测失败(输出 17 KB)
    //   · **不要用 pad 做等比留边**:实测把 16:9 素材按 1920x2160 的框等比缩放会变成 1920x1080 + 上下黑边,
    //     半边画面整个缩小(PSNR 掉到 10 dB)。两个输入同源、宽高比本来就一致,纯 scale 即可(实测 PSNR 50.8 dB)
    //   · 颜色标签沿用 EncoderArgs 那套(bt709/tv + VUI bsf):对比片与「看处理效果」里那条成片必须一致,
    //     否则用户一眼就看出"对比模式下颜色不一样"
    //   · 编码器/ffmpeg 一律走 HwRecipes 里实测记下的那套:**不能写死主 ffmpeg** —— 本机主 ffmpeg(N-126247)
    //     对 NVENC 直接报"需要驱动 610 以上",硬编全不可用,真正能编的是备用 ffmpeg8
    //   · 音频 `-map 1:a? -c:a copy`(跟随处理后那条):对比模式下本来就只出处理后的声音,
    //     少了它用户会发现"进对比就没声音了";`?` 让无音轨的素材也能合成成功(实测两种都 exit 0)
    //
    // 【实测耗时(4K)】约 5~11 ms/帧 → 3 秒 60fps 约 1~2 秒、15 秒 120fps 约 8 秒、15 秒 240fps 约 20~40 秒。
    // 全部在后台跑:正式处理那条路径零新增耗时,预览也不用等它(没合成好就先按老路走,合成好了无缝换过去)。

    /// <summary>确保"硬编可用性"已探测过(对比片合成要用它挑编码器)。
    /// 探测本身有闩锁(见 EnsureHwProbeAsync),已探过就是空操作 —— 预览跑完时它早就探过了。</summary>
    public static async Task EnsureHwProbeReadyAsync(CancellationToken ct = default)
    {
        var ff = FfmpegPath;
        if (ff == null) return;
        try { await EnsureHwProbeAsync(ff, ct).ConfigureAwait(false); } catch { }
    }

    /// <summary>挑一个"实测真的能编"的硬编,连同它对应的那个 ffmpeg 二进制(见 HwRecipes)。
    /// 【为什么 h264 优先于 hevc】这条片只在预览页当场播,而 Windows 上 h264 由系统自带解码器解,
    /// hevc 要装"HEVC 视频扩展"才行 —— 挑 hevc 有可能挑出一条自己播不了的对比片(黑屏)。
    /// 一个可用硬编都没有时返回 null,调用方退 CPU 软编。</summary>
    private static (string? enc, string? ffmpeg, bool noPreset) PickCompareEncoder()
    {
        lock (_hwLock)
        {
            foreach (var want in new[] { "h264_nvenc", "hevc_nvenc", "h264_amf", "hevc_amf", "h264_qsv", "hevc_qsv" })
            {
                if (!WorkingHwEncoders.Contains(want)) continue;
                if (HwRecipes.TryGetValue(want, out var r)) return (want, r.Ffmpeg, r.NoPreset);
            }
        }
        return (null, null, false);
    }

    /// <summary>对比片合成结果:Ok=成功;Detail=成功时是编码器/尺寸/帧率摘要、失败时是原因;Width/Height=对比片尺寸。</summary>
    public sealed record CompareClipResult(bool Ok, string Detail, int Width, int Height);

    /// <summary>对比片布局:
    /// <see cref="WholeFrames"/> = 「两者同时」整幅并排(每半边 = 整幅画面等比缩一半,合成片 W×(H/2));
    /// <see cref="SplitLine"/> = 「左右对比」分割线(线左右各取一版画面的**同一处**,各 1:1,合成片 W×H)。</summary>
    public enum CompareLayout { WholeFrames, SplitLine }

    /// <summary>
    /// 合成「左原片 + 右处理后」的对比片(单播放器播放用)。Ok=true 时已写出 outPath。
    /// 失败时 Ok=false + Detail(调用方保留双播放器回退路径,并在日志里留痕)。
    /// </summary>
    /// <param name="startSec">预览起点(秒)—— 左原片要从这里取,才能和右侧那条预览片段同一时刻。</param>
    /// <param name="lenSec">预览长度(秒);会被右片实际时长收窄,保证两边覆盖同一区间。</param>
    /// <param name="layout">布局(见 <see cref="CompareLayout"/>)。</param>
    /// <param name="splitPct">仅在 <see cref="CompareLayout.SplitLine"/> 下用:分割线位置(0~1,左原片右结果)。</param>
    /// <summary>给"处理后"的预览片做一份**带上绝对时间偏移**的副本(纯容器层 remux,零重编码)。
    ///
    /// 【为什么要它 · 2026-09-18 用户提出"两个视频叠一起、线是上层遮罩、播放条同控两个"】
    /// 那个设计要一个 MediaTimelineController 同时驱动两个播放器,而控制器要求**两条片在同一时间轴**上:
    /// 原片(用户的源文件)从 0 开始 ✗,处理后的片段也从它自己的 0 开始 —— 两者差一个"预览起点"。
    /// 实测(off30.mp4:首帧时间戳 30 秒)证明 **MediaPlayer 认绝对时间轴**(播放条显示 00:30/00:36)✔,
    /// 所以只要把处理后那条的**容器时间戳整体平移 _effStart**,它就和源文件对齐了 ✔。
    /// `-c copy` 只重写容器头(实测 1080p/6 秒 &lt;0.5 秒完成),**一帧都不重编码** ✔。
    /// 左侧仍然播**用户的原文件本身**(零重编码、零画质损失)✔ —— 这正是方案①的价值。</summary>
    public static async Task<bool> BuildOffsetCopyAsync(
        string srcClipPath, double offsetSec, string outPath, CancellationToken ct = default)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            if (!File.Exists(srcClipPath) || offsetSec <= 0.05) return false;
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
            var ff = FfmpegPath ?? BackupFfmpegPath;
            if (string.IsNullOrEmpty(ff)) return false;
            string args = "-hide_banner -loglevel error -y -nostats "
                        + $"-i \"{srcClipPath}\" -c copy "
                        + $"-output_ts_offset {offsetSec.ToString("0.###", inv)} "
                        + $"-avoid_negative_ts disabled \"{outPath}\"";
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ff,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            string err = "";
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                if (p == null) return false;
                var errTask = p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync(ct).ConfigureAwait(false);
                err = await errTask.ConfigureAwait(false);
                if (p.ExitCode != 0 || !File.Exists(outPath))
                {
                    AppLogger.Warn($"偏移副本生成失败(exit={p.ExitCode}): {err}");
                    return false;
                }
            }
            // 复核:首帧时间戳必须真的平移到位 —— 不确认就别用,免得两条片错位(那正是"不协调"的根源 ✗)
            double st = 0;
            try
            {
                var probe = FindInEngines("ffmpeg", "ffprobe.exe");
                if (!string.IsNullOrEmpty(probe))
                {
                    var psi2 = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = probe,
                        Arguments = $"-v error -select_streams v -show_entries stream=start_time -of default=nw=1:nk=1 \"{outPath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    using var p2 = System.Diagnostics.Process.Start(psi2);
                    if (p2 != null)
                    {
                        string txt = await p2.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                        await p2.WaitForExitAsync(ct).ConfigureAwait(false);
                        double.TryParse(txt.Trim(), System.Globalization.NumberStyles.Float, inv, out st);
                    }
                }
            }
            catch { }
            bool ok = st <= 0 || Math.Abs(st - offsetSec) < 0.25;   // 探不到(0)时按"命令成功"放行
            if (!ok) AppLogger.Warn($"偏移副本时间戳不符(期望 {offsetSec:0.###},实测 {st:0.###})—— 弃用,退回单播放器方案");
            else AppLogger.Info($"偏移副本就绪:start_time={st:0.###}s(与源文件同轴 · 零重编码 · 供左右对比双播放器同轴播放)");
            return ok;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("偏移副本异常:" + ex.Message);
            return false;
        }
    }

    public static async Task<CompareClipResult> BuildCompareClipAsync(
        string originalPath, string processedPath, double startSec, double lenSec,
        string outPath, IProgress<(int pct, string msg)>? progress = null, CancellationToken ct = default,
        CompareLayout layout = CompareLayout.WholeFrames, double splitPct = 0.5)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        CompareClipResult Fail(string why) => new(false, why, 0, 0);
        try
        {
            var (ow, oh) = await ProbeSizeAsync(originalPath).ConfigureAwait(false);
            if (ow < 32 || oh < 32) return Fail("取不到原片尺寸");
            // 右片实际时长:两边必须覆盖同一区间,否则输出会被 short=1 截在较短的那条上
            double outDur = 0;
            try { outDur = await ProbeDurationSeconds(processedPath).ConfigureAwait(false); } catch { }
            if (outDur > 0.1) lenSec = Math.Min(lenSec, outDur);
            if (lenSec < 0.2) return Fail("预览片段太短,拼不出对比片");
            // 归一化目标帧率 = 处理后那条的帧率(补帧/去重后它才是"用户真正要看"的节奏)
            double fps = 0;
            var fpsTxt = ProbeFps(processedPath) ?? ProbeFps(originalPath);
            if (fpsTxt != null) TryParseFps(fpsTxt, out fps);
            if (fps < 1 || fps > 480) fps = 0;          // 探不到就不写 fps=(交给 ffmpeg 按输入自己定)
            string fpsArg = fps > 0 ? "fps=" + fps.ToString("0.###", inv) + "," : "";
            string filt; int outW, outH;
            if (layout == CompareLayout.SplitLine)
            {
                // 【2026-09-19 修 · 用户实测:"左右的时候右边处理后的画质和看处理效果的那个画质完全不一样"】
                // 根因(证据在代码里):原来右边是 `scale={原片宽}:{原片高}` 之后再裁 ⇒ **处理后的分辨率被丢掉** ✗
                //   (处理成片可能是 4K/8K,合成片里只剩 1080p,还多一次重编码)⇒ 右边看着又糊又假 ✓
                // 修法:合成片改按**处理后尺寸**烘 ——
                //   · 右边:**1:1 原样裁**(不再缩放,保住真像素)✔
                //   · 左边:把原片等比放大到同一尺寸 ⇒ 线两边是同一处画面、同一尺度(擦除对比的语义不变)✔
                //   · 上限 4096 宽:防 8K 合成片过大(超了就两边**同比例**缩,仍然同尺度、可比)✔
                var (pw0, ph0) = await ProbeSizeAsync(processedPath).ConfigureAwait(false);
                if (pw0 < 32 || ph0 < 32) { pw0 = ow; ph0 = oh; }      // 探不到就退回原片尺寸(与旧行为一致)
                double cap = Math.Min(1.0, 4096.0 / Math.Max(1, pw0));
                int cw = (int)Math.Round(pw0 * cap); cw -= cw % 2;
                int chh = (int)Math.Round(ph0 * cap); chh -= chh % 2;
                if (cw < 32 || chh < 32) { cw = ow; chh = oh / 2 * 2; }
                double p = Math.Clamp(splitPct, 0.02, 0.98);
                int lw = (int)Math.Round(cw * p); lw -= lw % 2;      // 左侧宽度(偶)
                if (lw < 2) lw = 2;
                if (cw - lw < 2) lw = cw - 2;
                int rw = cw - lw;
                filt = $"[0:v]{fpsArg}scale={cw}:{chh},crop={lw}:{chh}:0:0,setsar=1,setpts=PTS-STARTPTS[l];"
                     + $"[1:v]{fpsArg}scale={cw}:{chh},crop={rw}:{chh}:{lw}:0,setsar=1,setpts=PTS-STARTPTS[r];"
                     + "[l][r]hstack=inputs=2:shortest=1[v]";
                outW = cw; outH = chh;
            }
            else
            {
                // 【2026-09-19 修 · 与「左右对比」同一类问题】这里原来把两侧都缩到"**原片的一半**"✗:
                //   源 1080p → 每侧只有 960×540,处理后的 4K/8K 分辨率全丢 ⇒
                //   用户实测"闪一下就又变成降采样的效果了"(后台合成片一落地,界面切过去就是这样)✓
                // 修法:每侧按**处理后的尺寸**烘,整条合成片限宽 4096 —— 超了就两侧**同比例**缩(保持同尺度可比)✔
                var (pw2, ph2) = await ProbeSizeAsync(processedPath).ConfigureAwait(false);
                if (pw2 < 32 || ph2 < 32) { pw2 = ow; ph2 = oh; }
                int sideW = Math.Min(pw2, 2048);                 // 每侧宽度上限(整条 ≤ 4096,保证能流畅播)
                double k2 = (double)sideW / Math.Max(1, pw2);
                int w2 = (int)Math.Round(pw2 * k2); w2 -= w2 % 2;  // 半边宽(yuv420p 要求偶数)
                int h2 = (int)Math.Round(ph2 * k2); h2 -= h2 % 2;  // 半边高
                if (w2 < 16 || h2 < 16) return Fail("原片太小,拼不出左右两半");
                string sc = $"scale={w2}:{h2},setsar=1,setpts=PTS-STARTPTS";
                filt = $"[0:v]{fpsArg}{sc}[l];[1:v]{fpsArg}{sc}[r];[l][r]hstack=inputs=2:shortest=1[v]";
                outW = w2 * 2; outH = h2;
            }
            if (outW < 32 || outH < 32) return Fail("原片太小,拼不出对比片");
            // 进度百分比的分母用真实帧数(右片时长 × 帧率),不是拍脑袋的数
            int total = fps > 0 ? (int)Math.Round(lenSec * fps) : 0;

            var (enc, hwFfmpeg, noPreset) = PickCompareEncoder();
            var attempts = new System.Collections.Generic.List<(string ff, string enc, bool noPreset)>();
            if (enc != null && hwFfmpeg != null) attempts.Add((hwFfmpeg, enc, noPreset));
            if (FfmpegPath != null) attempts.Add((FfmpegPath, "libx264", false));
            if (attempts.Count == 0) return Fail("找不到可用的 ffmpeg");

            string? lastErr = null;
            foreach (var at in attempts)
            {
                if (ct.IsCancellationRequested) return Fail("已取消");
                string encArgs = EncoderArgs(at.enc, 3);
                if (at.noPreset) encArgs = StripPreset(encArgs);
                // 【短 GOP,专为"拖进度条跟手"】对比片是**当场播放、随便拖**的临时片,不是交付成片:
                //   一次定位必须从上一个大关键帧解起 —— 默认 GOP 250 帧在 4K60 上约等于 0.7 秒的解码量,
                //   实测"定位落地"要 600~900ms;改成 12 帧后这个量级降到 ~1/20,拖动手感立竿见影。
                //   代价:文件大一点(多几个 I 帧),对临时对比片完全可接受。用户成片**不受影响**(那条路不走这里)。
                encArgs += " -g 12";
                string args = "-hide_banner -loglevel error -y -nostats -progress pipe:1 "
                    + $"-ss {startSec.ToString("0.###", inv)} -t {lenSec.ToString("0.###", inv)} "
                    + $"-i \"{originalPath}\" -i \"{processedPath}\" "
                    // 【2026-09-18 去掉音轨 · 治"播放中切倍速卡一下"】这条片是**当场看画面用**的对比片:
                    // 预览界面默认就是静音(用户要求默认静音),而带音轨时每次变速 MF 都要**重采样音频 + 重定时视频**
                    // —— 纯白干的开销,正是"切倍速那一下卡"的主要来源 ✗。画面比对不需要声音 ✓。
                    // 「看原片 / 看处理效果」播的是源文件与预览成片,**声音与静音键在那里照旧有效** ✓,
                    // 所以这次的静音键不是白做 —— 只是这条纯观看用的合成片不再背音轨 ✓。
                    + $"-filter_complex \"{filt}\" -map \"[v]\" -an {encArgs} "
                    + $"-fps_mode passthrough \"{outPath}\"";
                try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
                try
                {
                    // RunAsync 认不出阶段名时按 fr/total*90 报百分比(见 StageProgressPct 的兜底),
                    // 这里换算回 0~100 再交给界面 —— 数字来自真实帧号,不是编出来的。
                    IProgress<(int pct, string msg)>? scaled = progress == null ? null
                        : new Progress<(int pct, string msg)>(t =>
                            progress.Report((Math.Clamp(t.pct * 100 / 90, 0, 100), t.msg)));
                    await RunAsync(at.ff, args, scaled, ct, "对比片", total).ConfigureAwait(false);
                    // 空文件也算失败:实测"定位越界"那条 ffmpeg 会 exit 0 却写出 0 字节(exit code 骗人)
                    if (!await ValidateVideoFileAsync(outPath, 2).ConfigureAwait(false))
                    { lastErr = $"{at.enc} 输出无效(空文件或解不开)"; continue; }
                    string fpsDesc = fps > 0 ? fps.ToString("0.##", inv) + "fps" : "源帧率";
                    string layoutDesc = layout == CompareLayout.SplitLine ? $"分割线 {splitPct * 100:0}%" : "整幅并排";
                    return new CompareClipResult(true, $"{at.enc} · {layoutDesc} · {outW}x{outH} · {fpsDesc} · {lenSec:0.##}s", outW, outH);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { lastErr = $"{at.enc}: {ex.Message}"; }
            }
            return Fail(lastErr ?? "合成失败");
        }
        catch (OperationCanceledException) { return Fail("已取消"); }
        catch (Exception ex) { return Fail(ex.Message); }
    }

    /// <summary>用**一条 ffmpeg 命令**把整段帧缩回目标尺寸(替代原来的"逐帧 C# 缩放")。
    /// 成功返回新帧目录;任何异常/帧数对不上返回 null ⇒ **调用方回退逐帧路径**(保持"源帧损坏也不中断合帧"的兜底)。
    /// 实测(`_qa\bench_shrink.py`,60 帧 1080p→540p):逐帧串行 34.0 ms/帧、逐帧并行 8.4 ms/帧、
    /// 本方案 **4.9 ms/帧**(单进程、内存有界;原实现还给每帧开一个 Task,长片/4K 上会同时抢内存与 GDI 句柄)。</summary>
    private static async Task<string?> TryShrinkFramesWithFfmpegAsync(
        string ffmpeg, string srcDir, string[] files, int w, int h,
        IProgress<(int pct, string msg)>? progress, int upEnd, CancellationToken ct)
    {
        try
        {
            if (files.Length == 0) return null;
            string name = Path.GetFileName(files[0]);
            var m = System.Text.RegularExpressions.Regex.Match(name, @"^(.*?)(\d+)(\.[^.]+)$");
            if (!m.Success) return null;      // 名字不是"前缀+编号+扩展名"⇒ 不猜,交给逐帧路径
            string pattern = m.Groups[1].Value + "%0" + m.Groups[2].Value.Length + "d" + m.Groups[3].Value;
            string ext = m.Groups[3].Value;
            string outDir = srcDir.TrimEnd('\\', '/') + "_1x";
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            Directory.CreateDirectory(outDir);
            // JPG 中间帧要显式给高质量(默认质量会把上一道编码的损失再叠一次);PNG 无损不需要。
            string q = ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ? "-q:v 2 " : "";
            // 【2026-09-21 修回归】改成"一条 ffmpeg 走完"时**丢了逐帧进度**:原来逐帧 C# 循环会报
            //   「缩回中 N 帧 / 共 M 帧」,改后既没传 watchDir、命令里也没有进度输出 ⇒ 用户只看到一条不动的行 ✗。
            //   这里加 `-nostats -progress pipe:1`:ffmpeg 会把 `frame=` 等机器可读行写到 stdout,
            //   而 RunAsync 里的 FrameRegex 正是解析它 ⇒ 恢复「1x 修复 缩回 第 N 帧 / 共 M 帧」✓
            //   (与「编码」阶段同一套机制,见 RunAsync 的注释)。
            string args = $"-y -v error -nostats -progress pipe:1 -framerate 30 -i \"{Path.Combine(srcDir, pattern)}\" "
                        + $"-vf \"scale={w}:{h}:flags=lanczos\" {q}\"{Path.Combine(outDir, pattern)}\"";
            await RunAsync(ffmpeg, args, progress, ct, "1x 修复 缩回", files.Length).ConfigureAwait(false);
            int n = Directory.EnumerateFiles(outDir).Count();
            if (n != files.Length)
            {
                AppLogger.Warn($"[1x 修复] 缩回:ffmpeg 输出 {n} 帧 ≠ 输入 {files.Length} 帧 ⇒ 回退逐帧路径");
                return null;
            }
            return outDir;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[1x 修复] 缩回:ffmpeg 路径异常({ex.GetType().Name}: {ex.Message})⇒ 回退逐帧路径");
            return null;
        }
    }

    /// <summary>拆帧那趟的阶段名:链里挂了 Anime4K 着色器时叫「1x 修复(Anime4K)」。
    /// 【为什么】1x + Anime4K 那条路**整段超分阶段被跳过**(见 upscaleRuns 的判据):它不跑引擎,
    ///   只是把着色器挂在拆帧这趟的滤镜链首(见 preParts 的构造)。于是进度行原本只写「拆帧 N 帧」✗,
    ///   用户看到的就像是"1x 什么都没跑、也没有属于它的进度" ⇒ 这一步只是**把名字说对**,不改变任何处理行为。
    /// 【怎么判定】Anime4K 的滤镜参数里必然含 `custom_shader_path`(libplacebo 的着色器开关),
    ///   不需要额外传参数进来(拆帧方法没有 anime4k1x 形参,也不想为一句文案加形参)。</summary>
    private static string ExtractStageLabel(string vfExpr)
        => vfExpr.Contains("custom_shader_path", StringComparison.Ordinal) ? "1x 修复(Anime4K)" : "拆帧";

    private static async Task RunAsync(string exe, string args,
        IProgress<(int pct, string msg)>? progress, CancellationToken ct,
        string stage = "", int totalFrames = 0, string? watchDir = null,
        Action<double>? onEngineReady = null)
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
        var engineStartAt = DateTime.Now;   // "引擎已就绪"的实际启动耗时(= 进程启动 + 模型加载)
        SafeRender.ApplyProcessPriority(p);   // 处理时降优先级,防整机卡(可设置关闭)
        App.ActiveProcesses.Register(p);
        var lockObj = new object();
        int maxPct = 0, maxFrame = 0;
        // 本阶段【净】产出耗时(算 ETA 用):累计两次输出之间的间隔,但单个间隔最多计 5 秒。
        // 用户暂停是 NtSuspendProcess 冻结子进程 → 冻结期间零输出 → 这段墙钟时间不该算进速率,
        // 否则恢复后 ETA 按"暂停期间也一直在干活"算,虚高离谱(与超分阶段扣掉休息/暂停同理)。
        // 封顶而不是整段丢弃:ffmpeg 正常每 ~0.5 秒打一次统计(封顶不生效),而万一遇到"干活但很久不打印"
        // 的引擎,整段丢弃会把真实工时也抹掉 → ETA 反过来偏乐观。封顶让两种误差都 ≤5 秒/次。
        double activeSec = 0;
        var lastChunkAt = DateTime.UtcNow;
        // ===== 无进展看门狗状态 =====
        // 时间戳必须是"每次 RunAsync 调用私有"(闭包捕获):用全局静态会被并发任务互相"喂狗"
        // (如 2/3 路并行超分),A 的心跳让 B 真卡死也判不出来,用户无限等。EngineService 同款教训。
        bool sawAnyOutput = false;      // 是否已出现任何输出:区分"启动即挂死"与"跑了一半挂死"
        bool killRequested = false;     // 看门狗已判死,等待循环据此收手
        string? killReason = null;
        long lastLiveTicks = DateTime.Now.Ticks;   // 最近一次"确认在干活"的时刻
        // 引擎不输出进度时(如 rife),轮询输出目录已生成的文件数来逐帧报告
        using var watchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // ===== "引擎已就绪"回调(2026-09-13 新增;与 EngineService.RunAsync 的同名回调同口径)=====
        // 首次产出(首帧落盘 / 首条 stdout)= 进程启动 + 模型加载已完成、开始出活 → 回调一次真实耗时。
        // 调用方据此把界面上的"正在启动补帧引擎(约 N 秒…)"换成带实际耗时的一句,批/段之间的停顿
        // 因此可见;处理顺序/参数/并发一律不变。ffmpeg 调用点不传回调(传 null 时本函数零开销)。
        int readySignaled = 0;
        void SignalEngineReady()
        {
            if (onEngineReady == null) return;
            if (Interlocked.Exchange(ref readySignaled, 1) != 0) return;
            try { onEngineReady((DateTime.Now - engineStartAt).TotalSeconds); } catch { }
        }
        var watchTask = (watchDir != null && totalFrames > 0 && stage.Length > 0)
            ? WatchDirProgressAsync(watchDir, stage, totalFrames, progress, watchCts.Token,
                // 有新帧落盘 = 确实在出活 → 喂狗。补帧引擎可能长时间不打印 stdout,只看 stdout 静默会误杀。
                () => { lock (lockObj) { sawAnyOutput = true; lastLiveTicks = DateTime.Now.Ticks; } SignalEngineReady(); })
            : Task.CompletedTask;
        void OnChunk(string chunk)
        {
            bool firstOutput = false;
            lock (lockObj)
            {
                if (!sawAnyOutput) { sawAnyOutput = true; firstOutput = true; }
                if (chunk.Length > 0) lastLiveTicks = DateTime.Now.Ticks;
                var chunkAt = DateTime.UtcNow;
                double gap = (chunkAt - lastChunkAt).TotalSeconds;
                lastChunkAt = chunkAt;
                if (gap > 0) activeSec += Math.Min(gap, 5);
                // ffmpeg 帧计数
                foreach (System.Text.RegularExpressions.Match m in FrameRegex.Matches(chunk))
                {
                    if (int.TryParse(m.Groups[1].Value, out var fr) && fr > maxFrame)
                    {
                        maxFrame = fr;
                        if (totalFrames > 0 && stage.Length > 0)
                        {
                            // 如实上报真实帧号,不在这里钳到预估值。百分比仍由 StageProgressPct 封顶;
                            // 显示层(VideoView)分两档处理超出:≤5% 的收尾误差照旧封顶,大幅超出则
                            // 标注"已超预估·仍在出帧"。原先在这里 Math.Min(fr, totalFrames) 是为了修
                            // "第11219帧/共11099帧"的收尾溢出,但它同时把"预估偏低 3 倍、ffmpeg 仍在拼命
                            // 出帧"也压成了永远不动的 917/917 —— 用户因此判成死机并强制结束。
                            progress?.Report((StageProgressPct(stage, fr, totalFrames),
                                $"{stage} 第 {fr} 帧 / 共 {totalFrames} 帧{EtaStr(fr, totalFrames, activeSec)}"));
                        }
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
                            progress?.Report((StageProgressPct(stage, fr, totalFrames),
                                $"{stage} 第 {fr} 帧 / 共 {totalFrames} 帧{EtaStr(maxPct, 100, activeSec)}"));
                        }
                        else
                        {
                            progress?.Report((maxPct, $"处理中 {maxPct}%...{EtaStr(maxPct, 100, activeSec)}"));
                        }
                    }
                }
            }
            // 首次产出 = 引擎已就绪(模型加载完、开始出活):在锁外回调,不占着输出的读取锁
            if (firstOutput) SignalEngineReady();
        }
        var drainOut = DrainAsync(p.StandardOutput, OnChunk, ct);
        var drainErr = DrainAsync(p.StandardError, OnChunk, ct);

        // ===== 无进展看门狗 =====
        // 为什么必须有:ffmpeg/引擎被驱动挂死时进程"活着但永不退出",原等待循环 while(!p.HasExited)
        // 会无限转下去,唯一出路是用户手动点「强制结束」(实测有用户同一文件连续 7 次卡死在拆帧)。
        // 判据用"输出静默"而非固定总时长:ffmpeg 拆帧/编码期间会持续打印 frame=/speed= 统计,
        // 引擎也会打印百分比,正常干活时不可能长时间完全静默;而总时长无法设阈值(长视频本来就要跑很久)。
        // 阈值取宽,宁可漏判也不可误杀"慢但正常"的作业——误杀会让每个视频任务都失败,代价远大于多等一会儿。
        const int CheckEveryMs = 15_000;                                   // 每 15 秒巡检
        const int StartupSilenceLimitSec = 90;                             // 启动后一直零输出:含硬解初始化/滤镜初始化/-ss 定位/模型加载与着色器编译
        const int RunningSilenceLimitSec = 120;                            // 已出过输出后转静默:判定挂死
        using var watchdog = new System.Threading.Timer(_ =>
        {
            try
            {
                lock (lockObj)
                {
                    if (killRequested || p.HasExited) return;
                    // 暂停=子进程已被 NtSuspendProcess 冻结,冻结态与僵死态无法区分 → 喂狗,绝不误杀
                    if (IsPaused) { lastLiveTicks = DateTime.Now.Ticks; return; }
                    int limitSec = sawAnyOutput ? RunningSilenceLimitSec : StartupSilenceLimitSec;
                    if (DateTime.Now.Ticks - lastLiveTicks <= TimeSpan.FromSeconds(limitSec).Ticks) return;
                    killRequested = true;
                    killReason = $"子进程 {limitSec} 秒无任何输出(疑似驱动/解码器挂死),已强制终止:{stage}";
                    // Warn 是同步落盘(AppLogger sync),即使随后被用户强杀也留下证据
                    AppLogger.Warn($"看门狗:{killReason} — exe={Path.GetFileName(exe)}");
                    try { ResumeActiveProcess(); p.Kill(entireProcessTree: true); } catch { }   // 冻结进程 kill 可能失败,先解冻
                }
            }
            catch { }
        }, null, CheckEveryMs, CheckEveryMs);

        while (!p.HasExited)
        {
            if (ct.IsCancellationRequested)
            {
                try { ResumeActiveProcess(); p.Kill(entireProcessTree: true); } catch { }   // 取消前先解冻(冻结进程 kill 可能失败)
                break;
            }
            if (killRequested) break;   // 看门狗已判死并已杀进程,收手去抛停滞异常(走回退链)
            await Task.Delay(100).ConfigureAwait(false);
        }
        // ===== 管道读取必须给上限(否则看门狗形同虚设)=====
        // 【为什么】Kill 只是"请求终止":进程若卡在内核态的驱动调用里(TerminateProcess 要等该调用返回),
        // 它会继续活着、继续占着 stdout/stderr 管道 —— 这时 `await Task.WhenAll(drainOut, drainErr)`
        // (读到 EOF 才返回)会**永远等下去**。表现就是:日志里已经打了"看门狗:…已强制终止",界面却再也
        // 不动、点停止也没反应,用户只能强杀软件 —— 与"去重/拆帧卡住"的反馈完全一致,而看门狗以为它赢了。
        // 现在最多等 5 秒:等不到就放弃管道,继续往下走(该走回退走回退、该报错报错),绝不再无限等待。
        bool drained = true;
        var drainAll = Task.WhenAll(drainOut, drainErr);
        if (await Task.WhenAny(drainAll, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false) != drainAll)
        {
            drained = false;
            AppLogger.Warn($"⚠ 子进程未能在 5 秒内退出(已请求强制终止,pid={p.Id},阶段 {stage})"
                + "——放弃等待其输出并继续处理;若它仍占着显卡/文件,建议结束软件后重启。");
        }
        watchdog.Dispose();
        watchCts.Cancel();
        try { await watchTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        App.ActiveProcesses.Unregister(p.Id);
        if (ct.IsCancellationRequested)
            throw new OperationCanceledException();
        // 进程没退出时**不许读 ExitCode**(未退出会抛 InvalidOperationException,把"卡死"伪装成别的错);
        // 也不许走"回退重跑"——它可能还在往同一个帧目录写文件,重跑会与它交叉写出错误帧数(静默坏结果)。
        if (!p.HasExited || !drained)
            throw new EngineStallException(killReason
                ?? $"子进程在收到终止请求后仍未退出(阶段 {stage});它可能仍占用显卡或临时目录,已放弃本次处理。"
                   + "请结束软件后重启,再重试。", processStillRunning: true);
        // 停滞必须排在 ExitCode 判断之前:被看门狗杀掉的进程退出码非零,否则会被误报成普通"命令失败",
        // 调用侧也就分不清"该走回退"还是"参数本身错了"。
        // 加 ExitCode != 0 守卫:若进程恰好在判死的同一瞬间以 0 正常退出,说明活已干完,不能把成功任务误报成停滞。
        if (killRequested && p.ExitCode != 0)
            throw new EngineStallException(killReason ?? $"子进程长时间无输出,已强制终止:{stage}");
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
    /// <summary>把阶段 + 已处理帧数映射到整体进度百分比。
    /// 【2026-09-16 用户反馈"补帧才完、条就快到底了"】区间不再写死在本函数里,改为读
    /// `AlhPro.Core.ProgressBands`(唯一一份判据,按真机实测耗时分配;见那里的追溯说明)。</summary>
    private static int StageProgressPct(string stage, int fr, int totalFrames)
    {
        var (lo, hi) = AlhPro.Core.ProgressBands.OfStageName(stage);
        if (hi <= lo) return Math.Clamp(fr * 90 / Math.Max(1, totalFrames), 1, 90);   // 认不出的阶段:沿用旧兜底
        return (int)Math.Clamp(Math.Round(lo + (hi - lo) * Math.Clamp((double)fr / Math.Max(1, totalFrames), 0, 1)), lo, hi);
    }

    /// <summary>阶段内"预计还剩":速率 = 本阶段已处理帧 ÷ 本阶段【净】耗时。
    /// elapsedSec 必须是本阶段的净耗时(扣掉休息/暂停/挂起),且必须是本阶段自己的时钟 ——
    /// 传任务级耗时会把拆帧/去重/补帧的时间算进当前阶段的速率里,速率被严重低估、ETA 虚高
    /// (这正是 2026-09-08 之前"所有人都觉得预计时间不准"的根因:全文件只有一个任务级 stageStart,
    /// 而它只在超分阶段被使用;降温休息和用户暂停的时间也全算成了产出时间)。
    /// 只在已处理若干帧且净耗时超过 1 秒后才显示:头几帧含模型加载/编码器初始化,用它算速率会离谱。
    /// 【2026-09-13 修 G-补1】不足 1 秒那一档不再输出含糊的"预计还剩几秒"(见 Core.EtaText 的说明):
    /// 阶段末往往后面还有整批收尾(补帧后的整批 PNG→JPG 整理,几分钟),含糊文案会让用户以为马上就好。
    /// 文案生成已抽到 <see cref="AlhPro.Core.EtaText.ForRemaining"/>(纯逻辑 + 单测钉住每一档)。
    /// <param name="upcoming">阶段末之后马上要做的收尾工作描述(只说"后面还有什么",不改其余档位口径)。</param></summary>
    private static string EtaStr(long done, long total, double elapsedSec, string? upcoming = null)
        => AlhPro.Core.EtaText.ForRemaining(done, total, elapsedSec, upcoming);

    /// <summary>把引擎内部的逐帧汇报(EngineService 目录轮询 / EsrganOnnxService)补上"预计还剩"。
    /// 引擎只知道"本批",既不知道整阶段净耗时也不知道全局总帧数,所以它刷屏最频繁的那条消息一直没有 ETA;
    /// 而阶段时钟和总数只有本类有,故在外层包一层进度回调统一补,不去改引擎侧的函数签名。
    /// 已完成帧数优先取消息里引擎自己打的全局帧号("第 N 帧"),取不到才退回批起始计数 ——
    /// 否则 ETA 每批才更新一次,批数少的大视频上等于没有。
    /// 只给"看起来是帧/百分比进度"的消息追加,避免把告警/提示语句改得莫名其妙。</summary>
    private sealed class EtaProgress : IProgress<(int pct, string msg)>
    {
        private static readonly System.Text.RegularExpressions.Regex FrameNoRegex =
            new(@"第\s*(\d+)\s*帧", System.Text.RegularExpressions.RegexOptions.Compiled);
        private readonly IProgress<(int pct, string msg)> _inner;
        private readonly Func<long> _fallbackDone;
        private readonly Func<long, string> _eta;
        // 【2026-09-19 · 超分进度闪动】超分分批**并行**跑,各批上报的是"第 N 帧"(=起始槽位+批内帧号),
        // 界面显示最后到达的那条 ⇒ 批次A 报 220 时批次B 可能才报 21 = 进度倒退/闪动。只让显示口径**单调不回退**。
        private int _maxPct = -1;
        private long _maxFrameNo = -1;
        public EtaProgress(IProgress<(int pct, string msg)> inner, Func<long> fallbackDone, Func<long, string> eta)
        { _inner = inner; _fallbackDone = fallbackDone; _eta = eta; }
        public void Report((int pct, string msg) value)
        {
            string s = value.msg ?? "";
            if (s.Contains('帧') || s.Contains('%'))
            {
                if (!s.Contains("预计还剩", StringComparison.Ordinal))
                {
                    string e;
                    try
                    {
                        long done = _fallbackDone();
                        var m = FrameNoRegex.Match(s);
                        if (m.Success && long.TryParse(m.Groups[1].Value, out var parsed))
                        {
                            long rawNo = parsed;
                            if (parsed < _maxFrameNo) { parsed = _maxFrameNo; } else { _maxFrameNo = parsed; }   // 帧号只许增
                            if (parsed != rawNo) s = FrameNoRegex.Replace(s, "第 " + parsed + " 帧", 1);   // 文字也顶到最大值
                            if (parsed > done) done = parsed;
                        }
                        e = _eta(done);
                    }
                    catch { e = ""; }
                    if (e.Length > 0) s += " · " + e;
                }
            }
            int pctOut = value.pct;
            if (pctOut < _maxPct) pctOut = _maxPct; else _maxPct = pctOut;   // 百分比只许增
            _inner.Report((pctOut, s));
        }
    }

    /// <summary>轮询输出目录已生成的文件数,逐帧报告进度(供不输出进度的引擎如 rife 使用)。
    /// onFrame:每次文件数增长时回调,用作看门狗心跳(引擎可能长时间不打 stdout 但确实在出帧)。</summary>
    private static async Task WatchDirProgressAsync(string dir, string stage, int totalFrames,
        IProgress<(int pct, string msg)>? progress, CancellationToken ct, Action? onFrame = null)
    {
        int lastCount = 0;
        // 净产出耗时(算 ETA 用):按轮询实际间隔累计墙钟,但暂停期间不计 ——
        // 暂停 = NtSuspendProcess 冻结子进程,这段时间一帧都不出,算进速率会让恢复后的 ETA 虚高。
        double activeSec = 0;
        var lastTick = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            var nowTick = DateTime.UtcNow;
            if (!IsPaused) activeSec += Math.Min((nowTick - lastTick).TotalSeconds, 5);   // 封顶:睡眠/长卡顿不算工时
            lastTick = nowTick;
            try
            {
                int count = Directory.Exists(dir) ? EnumerateFrameFiles(dir).Count() : 0;
                if (count > lastCount)
                {
                    lastCount = count;
                    onFrame?.Invoke();
                    progress?.Report((StageProgressPct(stage, count, totalFrames),
                        $"{stage} 第 {Math.Min(count, totalFrames)} 帧 / 共 {totalFrames} 帧{EtaStr(count, totalFrames, activeSec)}"));
                }
            }
            catch { /* 目录尚未就绪等瞬时错误忽略 */ }
            try { await Task.Delay(200, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>逐块异步读取子进程输出(实时解析进度)。keepTail=只保留的尾部字符数(RunAsync 报错只需尾巴);
    /// 传 0 = 全量保留(RunCaptureAsync 的调用方要逐行解析 scdet/showinfo 输出,截断会丢帧)。</summary>
    private static async Task<string> DrainAsync(System.IO.StreamReader reader,
        Action<string> onChunk, CancellationToken ct, int keepTail = 8192)
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
                if (keepTail > 0 && sb.Length > keepTail) sb.Remove(0, sb.Length - keepTail);
                onChunk(chunk);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        return sb.ToString();
    }
}
