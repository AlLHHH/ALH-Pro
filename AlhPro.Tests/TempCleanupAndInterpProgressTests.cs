using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>「临时残留清理」与「补帧进度如实显示」两条接线契约(2026-09-16 用户实测"处理到一半卡住"的两处根因)。
///
/// 【现场】1440×1440 源、496 帧、补帧 4x(目标 1981 帧)+ 超分 2x。界面停在
/// `补帧 第 1981 帧 / 共 1981 帧` 一动不动,用户以为卡死;日志证明 ONNX 稳定引擎一直在出帧。三层原因:
///   ① ncnn-Vulkan 那段输出黑帧(队列异常,退出码 0)→ 自动降级成 ONNX DirectML 重算该整段(约 6 倍慢);
///   ② 段级进度包装器把帧号钳成"单调不回退" —— 重算轮从第 1 帧重新报数,于是永远显示上一轮的最大值 1981/1981;
///      而且包装器把所有消息都重写成帧号文案,连"⚠ 补帧改用 ONNX…重算该段"这句都被整句吃掉,用户看不到降级;
///   ③ 用户关窗 → `window.Closed` 里 CleanupTempDirs() 把后台线程正在写的 imgup_video_* 工作目录整个删掉 →
///      线程下一次碰帧就是 DirectoryNotFoundException(frames_final),日志里像"任务莫名失败"。
///
/// 这三处全是"编译不报错、集成测试不好跑"(要么得真跑一遍抽到黑帧,要么得在处理中途关窗)—— 只能把源码接线钉住。</summary>
public class TempCleanupAndInterpProgressTests
{
    // ===================== 一、「处理中」总闸 =====================

    /// <summary>① 闸门必须真的存在,而且是线程安全计数(视频管线跑在后台线程,UI 线程读)。</summary>
    [Fact]
    public void Processing_gate_exists_and_is_thread_safe()
    {
        var svc = ReadRepoFile("ImgUpscalerUI", "EngineService.cs");

        Assert.Contains("public static void EnterProcessing() => System.Threading.Interlocked.Increment(ref _processingCount);", svc);
        Assert.Contains("public static void ExitProcessing()", svc);
        Assert.Contains("System.Threading.Interlocked.Decrement(ref _processingCount)", svc);
        Assert.Contains("public static bool AnyProcessing => System.Threading.Volatile.Read(ref _processingCount) > 0;", svc);
    }

    /// <summary>② 视频管线:建工作目录之后进闸门、`finally` 里出闸门(出闸门必须在删工作目录之后,
    /// 否则删目录那一刻闸门已经放开,关窗清理会插进来抢删同一棵树)。</summary>
    [Fact]
    public void Video_pipeline_enters_and_exits_the_gate_around_the_work_dir()
    {
        var svc = ReadRepoFile("ImgUpscalerUI", "VideoService.cs");

        int mk = svc.IndexOf("Directory.CreateDirectory(framesFinal);", StringComparison.Ordinal);
        int enter = svc.IndexOf("EngineService.EnterProcessing();", StringComparison.Ordinal);
        int exit = svc.IndexOf("EngineService.ExitProcessing();", StringComparison.Ordinal);
        Assert.True(mk > 0, "找不到工作目录创建点(Directory.CreateDirectory(framesFinal))");
        Assert.True(enter > mk, "进闸门必须在工作目录建好之后");
        Assert.True(exit > enter, "找不到出闸门(EngineService.ExitProcessing)");

        // 成对且各只有一次(多一处少一处都会让闸门计数永久偏移)
        Assert.Equal(1, Regex_Count(svc, "EngineService\\.EnterProcessing\\(\\);"));
        Assert.Equal(1, Regex_Count(svc, "EngineService\\.ExitProcessing\\(\\);"));

        // 出闸门必须写在 finally 块里,而且排在"删工作目录"之后
        // (若排在删目录之前,那一刻闸门已放开,关窗清理会插进来抢删同一棵树)
        int lastFinally = svc.LastIndexOf("finally", exit, StringComparison.Ordinal);
        Assert.True(lastFinally > enter && lastFinally < exit, "出闸门必须写在 finally 块里");
        var finallyBody = svc.Substring(lastFinally, exit - lastFinally);
        Assert.Contains("Directory.Delete(workDir, true)", finallyBody);

        // 出闸门与删目录之间不许再夹别的 finally(否则上面那条可能量到别的块上)
        Assert.Equal(1, Regex_Count(finallyBody, "finally"));
    }

    // ===================== 二、清理端:先看闸门、再删;还要删 imgup_* 文件 =====================

    /// <summary>③ 清理残留前必须先问闸门(顺序钉死:闸门判断若跑到删除循环后面,等于没拦)。</summary>
    [Fact]
    public void CleanupTempDirs_checks_the_gate_before_deleting_anything()
    {
        var app = ReadRepoFile("ImgUpscalerUI", "App.xaml.cs");

        int gate = app.IndexOf("if (EngineService.AnyProcessing)", StringComparison.Ordinal);
        int firstDeleteLoop = app.IndexOf("Directory.EnumerateDirectories(root, \"imgup*\")", StringComparison.Ordinal);
        Assert.True(gate > 0, "CleanupTempDirs 里找不到处理中闸门(EngineService.AnyProcessing)");
        Assert.True(firstDeleteLoop > gate, "闸门必须排在第一个删除循环之前");

        // 跳过时如实回执(界面上要说"已跳过",不能笼统报"清理了 0 个")
        Assert.Contains("return (0, 0, 0, true);", app);
        Assert.Contains("AppLogger.Info(\"清理临时文件残留:当前有处理任务在跑", app);

        // 注册临时文件(EXIF 转正等中间文件)那条路同样有闸门
        var svc = ReadRepoFile("ImgUpscalerUI", "EngineService.cs");
        int bagGuard = svc.IndexOf("public static void CleanupTempFiles()", StringComparison.Ordinal);
        Assert.True(bagGuard > 0);
        var body = svc.Substring(bagGuard, Math.Min(600, svc.Length - bagGuard));
        Assert.Contains("if (AnyProcessing) return;", body);
    }

    /// <summary>④ 清理要连 `imgup_*` **文件**一起删(旧版只 EnumerateDirectories,于是
    /// imgup_rhythm_*.raw / imgup_exif_*.png / imgup_vk_*.png 这类文件永远清不掉)。</summary>
    [Fact]
    public void CleanupTempDirs_also_deletes_imgup_files_not_only_directories()
    {
        var app = ReadRepoFile("ImgUpscalerUI", "App.xaml.cs");
        Assert.Contains("Directory.EnumerateFiles(root, \"imgup_*\"))", app);

        // 这批文件的真实来源(任一处改名都会让上面那条扫描失效,故连前缀一起钉)
        var svc = ReadRepoFile("ImgUpscalerUI", "VideoService.cs");
        Assert.Contains("imgup_rhythm_{Guid.NewGuid():N}.raw", svc);
        var engine = ReadRepoFile("ImgUpscalerUI", "EngineService.cs");
        Assert.Contains("imgup_exif_{Guid.NewGuid():N}.png", engine);
    }

    /// <summary>⑤ 清理必须有第二个时机:启动期。关窗那次会被闸门整轮跳过(处理中关窗),
    /// 若没有启动期兜底,残留就再没有时机可清 —— 而且被强杀/崩溃留下的 imgup_video_* 只有启动期才清得掉。</summary>
    [Fact]
    public void Cleanup_also_runs_at_startup()
    {
        var app = ReadRepoFile("ImgUpscalerUI", "App.xaml.cs");

        int startupTask = app.IndexOf("System.Threading.Tasks.Task.Run((System.Action)(() =>", StringComparison.Ordinal);
        Assert.True(startupTask > 0, "找不到 OnLaunched 里的后台启动任务");
        int cleanup = app.IndexOf("CleanupTempDirs();", startupTask, StringComparison.Ordinal);
        Assert.True(cleanup > startupTask, "启动期的后台任务里没有清理临时残留 —— 关窗跳过后的残留就永远清不掉了");

        // 启动期清理是安全的:靠单实例锁排除"另一个实例正在用同一批临时文件"
        Assert.Contains("ALHPro_SingleInstance_Mutex", app);
    }

    /// <summary>⑥ 手动「立即清理」在跳过时必须如实告诉用户(不能只说"清理了 0 个")。</summary>
    [Fact]
    public void Manual_cleanup_says_so_when_it_skips()
    {
        var mp = ReadRepoFile("ImgUpscalerUI", "Views", "MainPage.xaml.cs");
        Assert.Contains("var (dirs, files, bytes, skipped) = App.CleanupTempDirs();", mp);
        Assert.Contains("Title = skipped ? \"已跳过清理\" : \"清理完成\",", mp);
    }

    // ===================== 三、补帧段级进度:不许把"正在重算"演成"卡死" =====================

    /// <summary>⑦ 段级进度包装器:帧号【不再】被 Math.Max 钳成单调;变小 = 新一轮引擎重算本段,如实采用。
    /// 另:带 ⚠ 的告警要原样放行(降级换引擎那句必须能被用户看见)。</summary>
    [Fact]
    public void Interp_segment_progress_reports_recompute_honestly()
    {
        var svc = ReadRepoFile("ImgUpscalerUI", "VideoService.cs");

        const string head = "IProgress<(int pct, string msg)>? segProg = progress == null ? null";
        int start = svc.IndexOf(head, StringComparison.Ordinal);
        Assert.True(start > 0, "找不到补帧段级进度包装器 segProg");
        int end = svc.IndexOf("});", start, StringComparison.Ordinal);
        Assert.True(end > start);
        var segProg = svc.Substring(start, end - start);

        // ① 不再钳制:原来的 `segLastLocal = Math.Max(segLastLocal, …)` 正是"永远停在 1981/1981"的根因
        Assert.DoesNotContain("segLastLocal = Math.Max(", segProg);
        Assert.Contains("if (m.Success) segLastLocal = int.Parse(m.Groups[1].Value);", segProg);

        // ② 告警原样放行:帧号文案不许把"⚠ 补帧改用 ONNX…重算该段"整句吃掉。
        //    两条上报都在:①状态行原样一条 ②`· ` 前缀一条(进左下角日志区,不被下一帧覆盖)
        Assert.Contains("t.msg.Contains(\"⚠\", StringComparison.Ordinal)", segProg);
        Assert.Contains("progress!.Report((warnPct, t.msg));", segProg);
        Assert.Contains("progress!.Report((warnPct, \"· \" + t.msg));", segProg);
        Assert.Contains("return;", segProg);

        // ③ 产生这些告警的降级点必须真的存在(否则上面的放行规则就是空转)
        Assert.Contains("⚠ 补帧改用 ONNX 稳定模型(DirectML GPU)重算...", svc);
        Assert.Contains("改用 ONNX/换卡重算该段(不落 CPU)", svc);
    }

    // ===================== 四、启动清理绝不许枚举盘根(否则一打开就闪退) =====================

    /// <summary>⑧ 启动残留清理【绝不许把盘根(C:\ / D:\ / E:\)拿去枚举 · 2026-09-16 用户实测"一打开软件就闪退"。
    ///
    /// 【现场】旧版 CleanupTempDirs 里有一句"把所有固定盘根目录也加进扫描列表"(DriveInfo.GetDrives()),
    /// 接着对每个 root 做 EnumerateDirectories(root, "imgup*")。而本机枚举 D:\ 盘根会让任意 .NET 8 进程
    /// 直接 AccessViolation(退出码 0xC0000005):CLR 打的是 "Fatal error.",走 fail-fast 直接杀进程 ——
    /// 连外层那句 `try { CleanupTempDirs(); } catch { }` 都拦不住(独立控制台程序复刻该写法,3/3 全崩)。
    /// 偏偏这句在 09-16 被挪进了 OnLaunched 的后台启动任务 ⇒ publish 版一打开就闪退(事件日志栈:
    /// FileSystemEnumerator.MoveNext → App.CleanupTempDirs → OnLaunched)。编译期完全看不出来,只能钉接线。</summary>
    [Fact]
    public void CleanupTempDirs_never_enumerates_a_raw_drive_root()
    {
        var app = ReadRepoFile("ImgUpscalerUI", "App.xaml.cs");

        int begin = app.IndexOf("CleanupTempDirs()", StringComparison.Ordinal);
        Assert.True(begin > 0, "找不到 CleanupTempDirs");
        int guardDef = app.IndexOf("private static string? NormalizeCleanupRoot(", StringComparison.Ordinal);
        Assert.True(guardDef > begin, "找不到把关函数 NormalizeCleanupRoot");
        // 注释里会为了讲清原因引用到 GetDrives,故先剥掉行注释再比代码
        var code = StripLineComments(app.Substring(begin, guardDef - begin));

        // ① 绝不再枚举盘根
        Assert.DoesNotContain("DriveInfo.GetDrives", code);
        // ② 所有候选根目录只留一个入口(roots.Add 只有一处)⇒ 每个都必须经过把关函数
        Assert.Equal(1, Regex_Count(code, @"roots\.Add\("));
        // ③ 四个候选来源全在:系统临时目录 / 历史用过的根 / 用户自定义 / 当前自动
        Assert.Equal(4, Regex_Count(code, @"AddCleanupRoot\((?!string)"));
        Assert.Contains("AddCleanupRoot(Path.GetTempPath());", code);
        Assert.Contains("EngineService.UsedTempRoots", code);
        Assert.Contains("AddCleanupRoot(AppSettings.TempDir);", code);
        Assert.Contains("AddCleanupRoot(EngineService.TempRoot);", code);

        // ④ 把关函数必须真的能拒掉盘根:靠"这个路径本身就等于它的盘根"来判定
        var guard = StripLineComments(app.Substring(guardDef, 900));
        Assert.Contains("Path.GetPathRoot", guard);
        Assert.Contains("return null;", guard);
    }

    /// <summary>剥掉源码里的行注释(仅用于源码契约断言:注释可以解释原因,不该被当成代码命中)。</summary>
    private static string StripLineComments(string src)
        => string.Join("\n", src.Split('\n').Select(l =>
        {
            int i = l.IndexOf("//", StringComparison.Ordinal);
            return i >= 0 ? l.Substring(0, i) : l;
        }));

    private static int Regex_Count(string text, string pattern)
        => System.Text.RegularExpressions.Regex.Matches(text, pattern).Count;

    /// <summary>从测试输出目录往上找仓库根,再读指定文件。</summary>
    private static string ReadRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var cand = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(cand)) return File.ReadAllText(cand);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("找不到仓库文件: " + string.Join('/', parts));
    }
}
