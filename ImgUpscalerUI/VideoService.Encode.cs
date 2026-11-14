// VideoService.Encode.cs — 编码器选择与硬件编码探测(从 VideoService.cs 拆出)
// 【为什么拆】原文件一万行,改编码相关的东西要在里面翻半天;拆成 partial 后按职责定位。

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AlhPro.Core;

namespace ALHPro;

public static partial class VideoService
{
    private static readonly System.Collections.Generic.List<string> WorkingHwEncoders = new();
    private static bool _hwProbed;
    private static readonly object _hwLock = new();

    /// <summary>本会话硬件编码探测里【每一档失败的第一行原因】(最多 12 条)。
    /// 【为什么要留】旧写法是 `catch { }` 把每一档的原因全吞掉 ⇒ 真机日志里只剩一句"全部不可用",
    /// 既看不出是驱动太旧、参数被拒、还是输出不合格 —— 1660 / 1050 Ti 那类报障整整三周无法定性,就是这条。
    /// 只用于日志留痕,不参与任何判断。</summary>
    private static readonly System.Collections.Generic.List<string> _hwProbeFailures = new();

    /// <summary>【自动化自验缝】让硬件编码这次调用必定失败(故意塞一个不存在的选项),用来在真机上验证
    /// "重试 N 次 → 提示(不退回 CPU 软编)"这条路径 —— 否则手上没有能稳定复现的硬编失败样本。
    /// 只在 ALH_FORCE_HW_ENCODE_FAIL=1 时生效;不设该变量时零副作用(参数原样返回)。</summary>
    private static string SeamBreakHwEncode(string args)
        => Environment.GetEnvironmentVariable("ALH_FORCE_HW_ENCODE_FAIL") == "1"
           ? args + " -x_alh_force_hw_encode_fail" : args;

    /// <summary>本机有没有【实测可用】的硬件编码器(必须先 await <see cref="EnsureHwProbeAsync"/>)。
    /// 【用途】视频任务开跑前的一次闸门:一个硬编都没有、用户又没显式选 CPU 时直接提示错误,
    /// 不让他白等一小时处理完再在编码这步慢一整轮(见 <see cref="AlhPro.Core.CpuFallbackPolicy"/>)。
    /// 【自验缝】环境变量 ALH_FORCE_NO_HW_ENCODER=1 时假装"本机没有可用硬编",用来在真机上把这条
    /// 提示路径跑出来(本机有 nvenc,否则这条分支永远走不到)。不设该变量时零副作用。</summary>
    internal static bool HasAnyWorkingHwEncoder()
    {
        if (Environment.GetEnvironmentVariable("ALH_FORCE_NO_HW_ENCODER") == "1") return false;
        lock (_hwLock) return WorkingHwEncoders.Count > 0;
    }

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
        /// <summary>确定不可用:命令真跑完但提示错误/输出无效(exit≠0、输出解不开、帧数不足等)。</summary>
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

    /// <summary>【2026-10-17】最近一次处理里**成片是否已经搬到位**(搬完并过了 <c>ValidateVideoFileAsync</c> 的
    /// 解码校验 ⇒ 这就是一份能播的成片)。它只用来回答一个问题:"失败时能不能把输出文件删掉"。
    /// 现场:合帧 try 里 `RetryFileMove(outTmp, outputVideo)` 之后还有收尾的活(编码实测日志、滤镜成本抽样、
    /// 统计台账),这些活万一抛异常,会一路冒泡到界面;界面的失败收尾一律 `File.Delete(outPath)` ——
    /// 于是"成片刚移好、紧接着收尾出错"就会把**已经做好的成片删掉**,用户白跑一整趟(概率低,但代价极大)。
    /// 现在:①后面这段收尾异常在 <c>ProcessVideoAsync</c> 内就被拦下(只写日志,不影响成片与"完成"判定);
    /// ②界面侧删输出前先看这个标记 —— 为 true 就绝不删(见 `VideoView.DropFailedOutput`)。
    /// 每次处理开始先清零(见方法开头的静态字段清零块)。</summary>
    public static bool LastOutputCompleted { get; private set; }

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
        // 【2026-09-23 Anime4K 收口】抽样链里如果挂着 Anime4K(libplacebo),它同样要指定 Vulkan 设备 ——
        // 否则"抽样的滤镜路径 ≠ 真实合帧的滤镜路径"(最轻则抽样失败、最重则在核显上抽出一堆假耗时)。
        // 设备参数是全局选项 ⇒ 必须放在第一个 `-i` 之前,不能塞进 -vf。
        string devArgs = "";
        try
        {
            if (chain.Contains("libplacebo", StringComparison.OrdinalIgnoreCase))
            {
                var forced = Environment.GetEnvironmentVariable("ALH_FORCE_ANIME4K_DEVICE");
                devArgs = !string.IsNullOrWhiteSpace(forced)
                    ? AlhPro.Core.Anime4kVulkanDevice.DeviceArgs(int.Parse(forced.Trim(), System.Globalization.CultureInfo.InvariantCulture))
                    : (EngineService.Anime4kVulkanDeviceIndex is int ai ? AlhPro.Core.Anime4kVulkanDevice.DeviceArgs(ai) : "");
            }
        }
        catch { devArgs = ""; }
        async Task<double> Measure(string vf)
        {
            try
            {
                const int n = 6;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await RunAsync(ffmpegExe,
                    $"-nostdin -y -v error {devArgs}{hw}-framerate {fpsArg} " +
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
        _hwProbeFailures.Clear();
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
            // "逐级放宽"的候选组合与失败留痕都在 TryProbeEncoderAsync 里(与探测误判补救共用同一段)。
            await TryProbeEncoderAsync(enc, ffmpeg, 5,
                (args, tmp) => $"-y -f lavfi -i \"testsrc=size=1280x720:rate=30:duration=0.4\" {args} \"{tmp}\"",
                "", _hwProbeFailures, ct);
        }
        // ===== NVDEC(JPG 序列硬件解码)探测 =====
        // 只在【已有可用 nvenc】时才探:软编时瓶颈是编码器本身,硬解救不了(见 HwJpegDecode 的实测)。
        // 【2026-09-12 加固,三个改动都是踩过的坑】
        //  ① 用【真会去编码的那个 ffmpeg】探(encFfmpeg 的配方来源),而不是永远用主 ffmpeg ——
        //     主 ffmpeg 能过、实际编码用的是备用 ffmpeg8 时,探测结论对不上真实路径。
        //  ② 从"1 帧图 + 1 帧解码"改成【8 帧序列 + 必须解出 8 帧】—— 单帧探不出"多帧序列下 cuvid 停住"。
        //     实测:ffmpeg8 + h264_nvenc + `-c:v mjpeg_cuvid` 解 1080p JPG **序列**,20 帧的命令永不结束(挂到超时强杀),
        //     而同命令去掉 cuvid 0.67 秒完成。单帧探测对这种形态完全无感。
        //  ③ 探测本身加【硬超时】:即使某台机器真被 cuvid 停住,也只损失这几秒并把该路径判为不可用,
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
                : "全部不可用 —— 按「视频不落 CPU」策略视频任务开不了;开跑前会用**本次任务的真实尺寸+帧率**"
                  + "再实测一次(见「探测未认可…」那行),仍不行才提示错误"));
            // 【每一档为什么失败必须留痕】旧写法 catch { } 全吞 ⇒ 日志只剩上面这一句,
            // 1660/1050 Ti 那种"探测说没有、真实参数却能编"的机器无从定性(实证见 RecoverHwEncoderByPreflightAsync)。
            if (WorkingHwEncoders.Count == 0 && _hwProbeFailures.Count > 0)
                AppLogger.Info("硬件编码探测失败原因(每档第一行):" + string.Join(" | ", _hwProbeFailures));
            // 【I · 2026-09-13】NVDEC(JPG 序列硬解)探测结论也按【三态】写一行 —— 这样诊断包里一眼能分清
            // "没探(本机无可用 NVENC)"、"确定不支持"、"探测超时(结论未知)"与"可用",不必再靠读异常文案猜。
            AppLogger.Info("JPG 序列硬解码(NVDEC)探测结论:" + _hwJpegDecode switch
            {
                HwJpegDecode.Usable => "可用(合帧将用 -c:v mjpeg_cuvid)",
                HwJpegDecode.Unsupported => "确定不可用(命令提示错误或输出不合格)→ 合帧走软件解码",
                HwJpegDecode.ProbeTimeout => "探测超时(6 秒未完成,【结论未知,不等于不支持】)→ 合帧走软件解码",
                _ => "未探测(本机没有可用 NVENC,或探测被取消)→ 合帧走软件解码",
            });
            // 【备用 ffmpeg 缺失要说清】内置主 ffmpeg 需要 NVIDIA 驱动 ≥610 才能开 NVENC;
            // 驱动较旧(实测 572.83)的机器是靠 engines\ffmpeg8\ffmpeg.exe 这个备用包兜住的。
            // 而 engines\ 是 gitignore、deploy.ps1 也不同步 —— 漏拷一次,硬编就静默消失,用户只会觉得"变慢了"。
            // 【2026-10-16 文案更正】这里原来写"本机将使用 CPU 软编(明显更慢)" —— 那是 09-23「视频不落 CPU」
            // 之前的口径,现在的真实行为是"视频任务提示错误",照旧文案写会让用户以为还能用。
            if (WorkingHwEncoders.Count == 0 && BackupFfmpegPath == null)
            {
                AppLogger.Warn("⚠ 硬件编码不可用,且未找到备用 ffmpeg(engines\\ffmpeg8\\ffmpeg.exe)。"
                    + "若显卡支持硬编:补齐该备用 ffmpeg,或把 NVIDIA 驱动更新到 610 以上(内置 ffmpeg 的 NVENC 需要 610+)。"
                    + "本机**没有**「CPU 软编」这条退路(「视频不落 CPU」策略):下面会按本次任务真实参数再实测一次,"
                    + "仍不可用才会在开跑前提示错误。");
            }
            else if (WorkingHwEncoders.Count > 0 && BackupFfmpegPath == null)
            {
                AppLogger.Info("硬件编码可用(未用到备用 ffmpeg)。注:若哪天换到驱动较旧/较新的机器上主 ffmpeg 开不了 NVENC,"
                    + "需要 engines\\ffmpeg8\\ffmpeg.exe 兜底,当前未找到该文件。");
            }
        }
    }

    /// <summary>用一个硬件编码器真编一小段,成功即记入 <see cref="WorkingHwEncoders"/> / <see cref="HwRecipes"/>
    /// 并返回 true。**探测(1280×720@30)与"探测误判补救"(本次任务真实尺寸+帧率)共用本函数** ——
    /// 两条路问的必须是同一个问题:"这套参数在这台机器上编得出来吗"。
    ///
    /// 【逐级放宽的四个组合,第一个成功的就是要记的配方】
    ///   ① 主 ffmpeg + 真实参数
    ///   ② 主 ffmpeg + 去掉 -preset(50 系常见:-preset p4 被拒 exit -22,去掉就能硬编)
    ///   ③④ 备用 ffmpeg(8.x,对 Blackwell 的 NVENC 适配更全)+ 上述两种参数
    /// ②③④ 只对 nvenc 有意义:-preset 是 nvenc 独有(amf 用 -quality、qsv 没有,替换是空操作),
    /// 备用 ffmpeg 也只为更新版的 NVENC 而存在,给它探 amf/qsv 纯属白跑。
    ///
    /// 【minFrames】探测要多编几帧(≥5):<see cref="ValidateVideoFileAsync"/> 对 &lt;5 帧判无效(防 QSV 假成功);
    /// 补偿路径按本次任务真实参数只编 1 帧(与 <c>PreflightEncodeOrThrowAsync</c> 同口径)。
    /// 【失败原因必须留痕】旧写法 `catch { }` 把每一档的原因全吞掉 ⇒ 真机日志里只有一句"全部不可用",
    /// 1660 / 1050 Ti 那类报障因此三周都定性不了。现在每档留第一行,由调用方在一个可用硬编都没有时写进日志。</summary>
    private static async Task<bool> TryProbeEncoderAsync(string enc, string ffmpeg, int minFrames,
        Func<string, string, string> buildCommand, string logSuffix,
        System.Collections.Generic.List<string> failures, CancellationToken ct)
    {
        var realArgs = EncoderArgs(enc);
        bool nvenc = enc.Contains("nvenc", StringComparison.OrdinalIgnoreCase);
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
        string lastWhy = "(未跑)";
        foreach (var (ff, args, noPreset) in attempts)
        {
            if (ct.IsCancellationRequested) return false;
            var tmp = Path.Combine(EngineService.TempRoot, $"imgup_encprobe_{enc}_{Guid.NewGuid():N}.mp4");
            try
            {
                // 探测分辨率 1280×720:之前 320×240 太小,部分编码器(QSV/核显硬编)小图能编出有效文件,
                // 但真实大分辨率视频下却输出无效文件(用户① RTX2070+核显双卡机实测:qsv 探测可用,合帧却黑屏/失败)。
                // nvenc 也拒绝过过小分辨率(64x64 报 incorrect parameters)。
                await RunAsync(ff, buildCommand(args, tmp), null, ct);
                // 不只看"文件非 0 字节":QSV 那类会写出非空但解不开的文件,必须真校验一遍
                if (await ValidateVideoFileAsync(tmp, minFrames))
                {
                    lock (_hwLock)
                    {
                        if (!WorkingHwEncoders.Contains(enc)) WorkingHwEncoders.Add(enc);
                        HwRecipes[enc] = new HwEncoderRecipe(ff, noPreset);
                    }
                    if (noPreset || !ReferenceEquals(ff, ffmpeg))
                        AppLogger.Info($"硬件编码探测:{enc} 需放宽参数才可用(" +
                            $"{(noPreset ? "去掉 -preset" : "")}{(noPreset && !ReferenceEquals(ff, ffmpeg) ? " + " : "")}" +
                            $"{(!ReferenceEquals(ff, ffmpeg) ? "备用 ffmpeg" : "")}){logSuffix}");
                    return true;
                }
                lastWhy = $"命令退出 0 但输出不合格(<{minFrames} 帧/解不开)";
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) return false;
                lastWhy = FirstLine(ex.Message);
            }
            finally { try { File.Delete(tmp); } catch { } }
        }
        if (failures.Count < 12) failures.Add($"{enc}:{lastWhy}");
        return false;
    }

    /// <summary>【探测误判的补救 · 2026-10-16】探测说"一个可用硬编都没有"时,**不直接拒绝这次视频任务**:
    /// 先用**本次任务真实要用的尺寸 + 标称帧率**把候选编码器各试 1 帧,能编就把该编码器+配方记进
    /// <see cref="WorkingHwEncoders"/> / <see cref="HwRecipes"/> 并返回它;一个都不行才返回 null(由调用方提示错误)。
    ///
    /// 【为什么必须有(真机实证)】探测写死 `testsrc=1280x720 rate=30`,回答的是"这台机器能不能编 720p30";
    /// 而这次任务要问的是"**这组尺寸+帧率**能不能编"。本机日志里两者直接打过架:
    ///   2026-09-27 16:53:05 / 16:54:58 `硬件编码器探测:全部不可用(将用 CPU 软编)`
    ///   2026-09-27 16:53:06 / 16:54:59 `编码预检通过:h264_nvenc 能编 1920×3416 @ 480 fps`(同一支 ffmpeg.exe)
    /// —— 探测那一刻起 `WorkingHwEncoders` 就是空的,而闸门(<c>ProcessVideoAsync</c> 里那处)只认探测结果,
    /// 于是这台"其实编得动"的机器上视频功能会**整会话不可用**,直到重启(闩锁 _hwProbed 不放开)。
    /// 1660 / 1050 Ti 用户的"视频超分失败"最可能就是这一类,而旧代码连失败原因都不记(见 _hwProbeFailures)。
    ///
    /// 【代价可控】只在"探测说没有硬编"的机器上跑,最多 6 个编码器 × 最多 4 个组合 × 1 帧;
    /// 通常第一个候选就命中(与上面日志同形)。失败也只是几次 1 秒级预检的耗时,发生在整片开始之前。</summary>
    internal static async Task<string?> RecoverHwEncoderByPreflightAsync(string ffmpeg, int gpuId, int width, int height,
        double nominalFps, int codecPref, CancellationToken ct)
    {
        // 候选顺序与 PickVideoEncoder 同源:厂商匹配的硬编优先,再看其余(hevc 优先时把 hevc 那档提到前面)。
        var name = gpuId >= 0 ? GpuInfo.GetEngineDeviceName(gpuId) : "";
        string vH264 = "", vHevc = "";
        if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) { vH264 = "h264_nvenc"; vHevc = "hevc_nvenc"; }
        else if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
        { vH264 = "h264_amf"; vHevc = "hevc_amf"; }
        else if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase)) { vH264 = "h264_qsv"; vHevc = "hevc_qsv"; }
        var order = new System.Collections.Generic.List<string>();
        void Add(string e) { if (e.Length > 0 && !order.Contains(e)) order.Add(e); }
        if (codecPref == 2) { Add(vHevc); Add(vH264); }
        else { Add(vH264); Add(vHevc); }
        foreach (var e in new[] { "h264_nvenc", "h264_amf", "h264_qsv", "hevc_nvenc", "hevc_amf", "hevc_qsv" }) Add(e);

        var failures = new System.Collections.Generic.List<string>();
        foreach (var enc in order)
        {
            ct.ThrowIfCancellationRequested();
            double fps = nominalFps > 0 ? nominalFps : 30;
            int w = width > 0 ? width : 2, h = height > 0 ? height : 2;
            bool ok = await TryProbeEncoderAsync(enc, ffmpeg, 1,
                (args, tmp) => AlhPro.Core.VideoEncodeGuard.BuildPreflightCommand(w, h, fps, args, tmp),
                $" · 本次任务真实参数 {w}×{h} @ {fps:0.###} fps", failures, ct);
            if (ok)
            {
                AppLogger.Warn($"⚠ 探测说本机没有可用硬编,但按本次任务真实参数实测 **{enc} 能编 {w}×{h} @ {fps:0.###} fps**"
                    + $" ⇒ 继续用硬编(探测写死 1280×720@30,与真实任务不等价;上面的「全部不可用」不代表编不了)");
                return enc;
            }
        }
        AppLogger.Warn("⚠ 探测说没有可用硬编,按本次任务真实参数逐个实测也都编不出来:"
            + string.Join(" | ", failures));
        return null;
    }

    /// <summary>本会话"实测可用硬编"的快照(只读;候选链要用它,不能在编码过程中边改边读)。
    /// 返回副本:换编码器那条路会遍历它,而探测线程/下一个任务可能同时改 <see cref="WorkingHwEncoders"/>。</summary>
    private static System.Collections.Generic.List<string> HwEncoderSnapshot()
    {
        lock (_hwLock) return new System.Collections.Generic.List<string>(WorkingHwEncoders);
    }

    /// <summary>【2026-09-30 · t64 B/C】开跑前的**参数级**编码预检:用本次任务真实要用的输出尺寸 + 标称帧率
    /// 真编 1 帧;返回**实际要用**的编码器(打不开就换过)。
    ///
    /// 【为什么不能用既有的 <see cref="EnsureHwProbeAsync"/> 代替】那个探测写死
    /// `testsrc=size=1280x720:rate=30` ⇒ 它回答的是"这台机器能不能编 720p30",而真实任务要问的是
    /// "**这次这组尺寸+帧率**能不能编"(1527 那次是 1920×3416 @ 471.85 fps)⇒ 参数级失败它探不出来,
    /// 只能靠整片重跑试出来 —— 那一次就是这么白跑了 1425 帧 / 49.5 分钟。
    ///
    /// 【失败语义(与 C 的分工)】
    ///   · **打不开编码器**(初始化失败,判据在 <see cref="AlhPro.Core.VideoEncodeGuard"/>):换候选链里的
    ///     下一个**本机实测可用硬编**(优先同厂商另一档,如 h264_qsv ⇒ hevc_qsv),日志写清"换用 X";
    ///     链空了才提示错误(<see cref="AlhPro.Core.CpuFallbackPolicy.DescribeHwEncodeInitFailure"/>)。
    ///   · **瞬时失败**:同一编码器再试 1 次(预检只花 1 秒);仍失败 ⇒ 停下。编码阶段那 3 次重试留给
    ///     "预检通过、真编时才失败"那种情况(5060 那台就是那种),本函数不改变它。
    /// 【不改状态】不写 WorkingHwEncoders / HwRecipes / 会话台账;临时文件用完就删。
    /// 【自验缝】ALH_FORCE_HW_ENCODE_FAIL 只作用于**真实编码命令**(见 <see cref="SeamBreakHwEncode"/> 的用法),
    /// 不拦预检 —— 否则那条缝就没法再用来复现"编码阶段重试"这条路了。</summary>
    internal static async Task<string> PreflightEncodeOrThrowAsync(string ffmpeg, string intendedEncoder, int codecPref,
        int quality, double bitrateKbps, int width, int height, double nominalFps, CancellationToken ct)
    {
        var tried = new System.Collections.Generic.List<string>();
        var chain = new System.Collections.Generic.List<string> { intendedEncoder };
        // 【2026-11-09】把本次**计划输出尺寸**传给候选链:超过 H.264 上限时,h264_* 是确定编不了的,
        // 不能放进候选里再撞一次(旧行为:h264_amf 在 4564×2452 上必失败,还把真因埋掉)。
        chain.AddRange(AlhPro.Core.VideoEncodeGuard.FallbackEncoderChain(
            intendedEncoder, codecPref, HwEncoderSnapshot(), width, height));
        string lastDetail = "";
        for (int i = 0; i < chain.Count; i++)
        {
            string encoder = chain[i];
            tried.Add(encoder);
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var (ok, detail, kind) = await TryPreflightOnceAsync(ffmpeg, encoder, quality, bitrateKbps,
                    width, height, nominalFps, ct).ConfigureAwait(false);
                if (ok)
                {
                    AppLogger.Info(AlhPro.Core.VideoEncodeGuard.DescribePreflightPassed(encoder, width, height, nominalFps));
                    return encoder;
                }
                lastDetail = detail;
                // 打不开 = 确定性失败:同一个编码器再试还是打不开 ⇒ 立刻看下一个候选(不浪费那 1 秒)
                if (kind == AlhPro.Core.VideoEncodeGuard.EncodeFailureKind.EncoderInit) break;
                if (attempt == 1)
                {
                    AppLogger.Warn($"⚠ 编码预检({encoder})第 1 次失败(瞬时类):{FirstLine(detail)} —— 1 秒后再试 1 次");
                    try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { throw; }
                }
            }
            // 这一档不行:还有候选就换,并且把"换用 X"写进日志(不许静默换)
            if (i + 1 < chain.Count)
                AppLogger.Warn(AlhPro.Core.VideoEncodeGuard.DescribeEncoderSwitch(encoder, chain[i + 1], FirstLine(lastDetail)));
        }
        // 【2026-11-09】真因判定:输出超 H.264 上限 + 本机**没有实测可用的 HEVC** ⇒ 这个尺寸就是编不出来
        // (实录那趟:试过 hevc_nvenc 失败、h264_amf 因尺寸必失败;文案必须直说尺寸到顶,而不是"编码器坏了")
        bool h264LimitHit = AlhPro.Core.VideoEncodeGuard.ExceedsH264Limit(width, height)
            && !HwEncoderSnapshot().Any(e => e.StartsWith("hevc", StringComparison.OrdinalIgnoreCase));
        if (h264LimitHit)
            AppLogger.Warn($"⚠ 输出 {width}×{height} 超过 H.264 上限 "
                + $"{AlhPro.Core.VideoEncodeGuard.H264MaxDimension},且本机没有可用的 HEVC 硬编:"
                + "已跳过 h264_* 候选(它在这个尺寸上确定编不了)");
        throw new InvalidOperationException(AlhPro.Core.VideoEncodeGuard.DescribePreflightFailure(
            intendedEncoder, width, height, nominalFps, lastDetail, string.Join(" / ", tried), h264LimitHit));
    }

    /// <summary>预检只编 1 帧:命令由 <see cref="AlhPro.Core.VideoEncodeGuard.BuildPreflightCommand"/> 生成
    /// (所以日志里记的命令与实际跑的命令逐字一致)。返回 (是否通过, 诊断文本, 失败形态)。</summary>
    private static async Task<(bool Ok, string Detail, AlhPro.Core.VideoEncodeGuard.EncodeFailureKind Kind)>
        TryPreflightOnceAsync(string ffmpeg, string encoder, int quality, double bitrateKbps,
        int width, int height, double nominalFps, CancellationToken ct)
    {
        // 参数与真实编码**同一份**(含配方:备用 ffmpeg / 是否去掉 -preset)—— 参数级预检的意义就在这里
        var recipe = GetHwRecipe(encoder);
        string exe = recipe?.Ffmpeg ?? ffmpeg;
        string args = EncoderArgs(encoder, quality, bitrateKbps);
        if (recipe?.NoPreset == true) args = StripPreset(args);
        var tmp = Path.Combine(EngineService.TempRoot, $"imgup_encpreflight_{encoder}_{Guid.NewGuid():N}.mp4");
        string cmd = AlhPro.Core.VideoEncodeGuard.BuildPreflightCommand(width, height, nominalFps, args, tmp);
        try
        {
            AppLogger.Info($"编码预检命令({encoder}):\"{Path.GetFileName(exe)}\" {cmd}");
            await RunAsync(exe, cmd, null, ct, "编码预检", 0).ConfigureAwait(false);
            if (!await ValidateVideoFileAsync(tmp, 1).ConfigureAwait(false))
                return (false, $"命令退出 0 但 1 帧输出文件无效(0 字节/解不开):{Path.GetFileName(tmp)}",
                    AlhPro.Core.VideoEncodeGuard.EncodeFailureKind.Transient);
            return (true, "", AlhPro.Core.VideoEncodeGuard.EncodeFailureKind.Transient);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            string detail = ex.Message ?? "";
            return (false, detail, AlhPro.Core.VideoEncodeGuard.ClassifyEncodeFailure(detail));
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    /// <summary>多行诊断取第一行(给"第 N 次失败/换用 X"这种一行日志用)。</summary>
    private static string FirstLine(string? text)
        => string.IsNullOrWhiteSpace(text) ? "(未给出原因)" : text!.Trim().Split('\n')[0];

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
    /// codecPref:0=自动(H.264 优先) 1=强制 H.264 2=优先 H.265(hevc,更省空间,老设备可能播不了)。
    /// outWidth/outHeight:本次任务的**计划输出尺寸**;任一边超过 H.264 硬上限(4096)时只能走 HEVC
    /// (2026-09-29 用户诊断包实测:h264_amf 在 5760×4320 直接 Invalid argument)——见方法内注释。</summary>
    internal static string PickVideoEncoder(int gpuId, int codecPref = 0, int outWidth = 0, int outHeight = 0)
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

            // 【2026-09-29 · 按尺寸选编码器(用户诊断包)】计划输出任一边 > H.264 上限(4096)时,上面任何 H.264 结论都
            // **物理上编不了**(实测 h264_amf 在 5760×4320 直接 Invalid argument;同一台机器 2880×2160 完全正常)
            // ⇒ 改走 HEVC:先厂商 hevc_*(qsv 那一档也接受 —— 总比确定失败强),再本机任一实测可用的 hevc。
            // ⚠ 这里**故意不看** WorkingHwEncoders 有没有认证过厂商 HEVC:探测是"用生产参数编一小帧",
            //   探测没过 ≠ 这个尺寸编不了;而 h264 在这个尺寸是**确定**编不了 ⇒ 让开跑前那次参数级预检
            //   用真参数试一次 hevc 才是正解(它失败会如实提示错误,不会白跑几十分钟)。
            if (AlhPro.Core.VideoEncodeGuard.ExceedsH264Limit(outWidth, outHeight)
                && !chosen.StartsWith("hevc", StringComparison.OrdinalIgnoreCase))
            {
                string? forcedHevc = hevcVendor.Length > 0
                    ? hevcVendor
                    : WorkingHwEncoders.FirstOrDefault(e => e.StartsWith("hevc", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(forcedHevc)) chosen = forcedHevc;
            }
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
        // 【2026-09-29】按尺寸改走 HEVC 时把原因写进这行(用户与排查者都要看得见为什么不是 H.264)
        if (!isCpu && AlhPro.Core.VideoEncodeGuard.ExceedsH264Limit(outWidth, outHeight))
            LastVideoEncoderInfo += $" — 输出 {outWidth}×{outHeight} 超过 H.264 上限 "
                + $"{AlhPro.Core.VideoEncodeGuard.H264MaxDimension},已按尺寸改用 HEVC";
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

    // 【2026-09-23 删掉 BrokenHwEncoders】它原来是"硬编失败一次 → 本会话所有任务静默走 CPU 软编"的载体。
    // 按用户要求「视频不落 CPU」,现在硬编失败是"重试 N 次 → 提示",没有什么"记坏以后走 CPU"的状态了
    // ⇒ 字段连同它的两处用法一起删掉(死代码);这也顺手根除了旧注释里记的那个坑:
    // 用户点一次「停止」被误判成"硬编坏了",此后本会话全部任务静默慢数倍直到重启。

    /// <summary>本会话各视频编码 GPU 硬解(d3d11va)已验证不可用的集合(按编码器区分,如 h264/hevc/av1)。
    /// 某一种编码硬解不了(如旧卡硬解 AV1)只禁用该编码,其它编码仍优先硬解,不再"一次失败、全会话软解"。</summary>
    private static readonly System.Collections.Generic.HashSet<string> _hwDecodeBrokenCodecs = new();

    /// <summary>【2026-10-15 · 预览"卡"的定位前提】本会话已知"某素材是什么编码"(按路径缓存,只当缓存用)。
    /// 【为什么预览页需要它】预览**播放**走 Media Foundation(系统解码器),而处理路径走自带 ffmpeg 的
    /// d3d11va 硬解 —— 两套完全不同的解码栈。HEVC/AV1 这类编码,系统没装扩展时会**静默软解**
    /// ⇒ 1080p 还凑合、4K 必卡。没有"源是什么编码"这一行事实,所有关于"卡在哪一层"的说法都只是猜。</summary>
    private static readonly System.Collections.Generic.Dictionary<string, string> _codecByPath =
        new(System.StringComparer.OrdinalIgnoreCase);
    private static readonly object _codecByPathLock = new();

    /// <summary>最近一次探到的源素材编码(如 h264/hevc/av1);未知为 null。供界面与日志引用。</summary>
    public static string? LastSourceCodecName { get; private set; }

    /// <summary>探"这个文件是什么编码"(公开给预览页用;失败返回 "",绝不抛)。已探过的路径直接命中缓存,
    /// 所以同一素材切视图/反复播放不会重复跑 ffprobe。</summary>
    public static async Task<string> ProbeSourceCodecAsync(string video, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(video)) return "";
            lock (_codecByPathLock)
            {
                if (_codecByPath.TryGetValue(video, out string? hit)) return hit;
            }
            string ff = FfmpegPath ?? "";
            string codec = ff.Length > 0 ? await ProbeVideoCodecName(ff, video, ct) : "";
            if (codec.Length > 0)
            {
                LastSourceCodecName = codec;
                lock (_codecByPathLock)
                {
                    if (_codecByPath.Count > 16) _codecByPath.Clear();   // 只当缓存用,不做无界增长
                    _codecByPath[video] = codec;
                }
            }
            return codec;
        }
        catch { return ""; }
    }

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
}