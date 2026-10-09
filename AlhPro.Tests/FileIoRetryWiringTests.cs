using AlhPro.Core;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【接线契约 · 2026-10-09 · t65】帧文件"瞬时占用重试"必须**真的接在**关键落点上。
///
/// 判据由 <see cref="FileIoRetryTests"/> 钉死,这里钉接线:诊断包 `ALHPro_Diag_20261009_1830`
/// 那次事故的形态是 —— 判据(退避重试)有了也没用,因为出事那一步根本**没调用**它:
/// 「合帧前的帧数对齐」里一次裸 `File.Copy` 撞上单帧占用 ⇒ 异常冒泡 ⇒ 90 分 31 秒、10808 帧全部作废。
///
/// 所以这里不是"检查源码里出现了某个函数名",而是:
/// ① 那个**方法体**里必须只剩 `RetryFileCopy`(裸 `File.Copy(..., true)` 一个都不许留);
/// ② 整份 VideoService 的裸 `File.Copy/File.Move` 只允许出现在重试接线层自己身上;
/// ③ 退避循环本身只许有 **Core 那一份**(`IO_RETRY.Run`)—— 视频侧转发、引擎侧直接调,
///    宿主里再抄一遍就成了两套口径。
/// 真有人后来把这层摘掉,这三条会立刻变红(纯 `IO_RETRY` 单测看不出来)。</summary>
public class FileIoRetryWiringTests
{
    /// <summary>VideoService 里**允许**直接调 BCL 文件 API 的地方(白名单,每条都写理由):
    /// 重试接线层自身(动作名与 op 仍在这里,循环在 Core)、已废弃方法、只做探测/清理且失败无语义的落点。</summary>
    private static readonly string[] AllowedMethodAnchors =
    {
        "private static void RetryFileIo(",                 // 接线缝:必须转发到 AlhPro.Core.IO_RETRY.Run
        "private static async Task InterpKeyframeGapsAsync(", // [Obsolete] 历史回溯路径,不在任何活动调用链
    };

    /// <summary>★ 出事那一步(帧数对齐的"末帧补足")必须走重试。</summary>
    [Fact]
    public void The_frame_count_alignment_fill_goes_through_the_retry_helper()
    {
        string code = CodeOnly(Source());
        int start = code.IndexOf("if (seq.Length > 0 && seq.Length != expected)", StringComparison.Ordinal);
        Assert.True(start > 0, "找不到帧数对齐那一段(锚点失效 ⇒ 这条契约必须变红,不许静默放水)");
        string body = code.Substring(start, 1500);
        Assert.Contains("RetryFileCopy(last,", body);
        Assert.DoesNotContain("File.Copy(last,", body);
    }

    /// <summary>★ **补不上的帧不许把整条任务带走**。1830 就是死在这个循环里:
    /// 一帧被占 ⇒ 异常冒泡 ⇒ 90 分 31 秒、10808 帧全废、输出 0 个。
    /// 这段的语义只是"末帧定格补足",少一两帧 = 成片短几毫秒,必须软失败 + 记数。</summary>
    [Fact]
    public void Frame_count_fill_never_throws_away_the_whole_task()
    {
        string code = CodeOnly(Source());
        int start = code.IndexOf("if (seq.Length < expected)", StringComparison.Ordinal);
        Assert.True(start > 0, "找不到帧数对齐的\"末帧补足\"分支");
        string body = code.Substring(start, 1200);
        Assert.Contains("long filled = 0;", body);
        Assert.Contains("long shortBy = expected - seq.Length - filled;", body);   // 缺口按"还差多少"如实算
        Assert.Contains("filled++", body);            // 成功补了几帧要如实数
        Assert.DoesNotContain("failed++", body);      // 第一个补不上就收手(逐帧继续 = 永久占用时 N×7 秒静默停顿)
        Assert.Contains("catch (Exception ex)", body);
        Assert.DoesNotContain("catch (Exception ex) when", body);   // 不能只在"可自愈"时才接住 —— 那样长期占用照样丢任务
        Assert.Contains("AppLogger.Error(", body);    // 少帧必须吵,不许静默
        Assert.Contains("非占用类错误", body);         // 软失败也要分清"被占用"和"磁盘满/路径非法"
        Assert.DoesNotContain("(退避重试后仍被占用)", body);   // 不允许把没核实的死因写死进日志
    }

    /// <summary>★ 同类裸拷贝的另两处:尾帧容积(补 1~3 帧末帧定格)与 tempo 重采样的退化素材分支(n&lt;2)。
    /// 尾帧容积原来还"无条件报补了 add 帧" —— 补不上也报,把丢的帧藏进了日志里。</summary>
    [Fact]
    public void Tail_volume_and_tempo_degenerate_copies_go_through_the_retry_helper()
    {
        string code = CodeOnly(Source());

        string tail = Slice(code, "int add = (int)Math.Round(muxDur * outFps) - seqA.Count;", "tailGapSec = gap;");
        Assert.Contains("RetryFileCopy(lastF,", tail);
        Assert.DoesNotContain("File.Copy(lastF,", tail);
        Assert.Contains("int added = 0;", tail);
        Assert.Contains("尾帧容积:补 {added} 帧", tail);   // 报的是真实补上的帧数
        Assert.Contains("AppLogger.Warn(", tail);          // 补不上必须吵

        string tempo = Slice(code, "private static async Task<(int frameCount, double outFps)> RunTempoResampleAsync(",
            "double F = targetFps ?? inFps * interpScale;");
        Assert.Contains("RetryFileCopy(f, Path.Combine(framesFinal, Path.GetFileName(f)))", tempo);
        Assert.DoesNotContain("File.Copy(f, Path.Combine(framesFinal,", tempo);
    }

    /// <summary>★ 长任务里"把一帧交给最终目录"的每一个落点都要走重试(补帧分段 / 引擎输出搬运 / 逐帧交付)。</summary>
    [Fact]
    public void Frame_delivery_paths_go_through_the_retry_helper()
    {
        string code = CodeOnly(Source());
        string seg = Slice(code, "private static async Task<int> InterpSegmentAsync(",
            "private static bool TryDeliverFrame(");
        Assert.Contains("RetryFileCopy(Path.Combine(framesOut,", seg);
        Assert.Contains("RetryFileCopy(Path.Combine(segIn, \"frame_000001.jpg\")", seg);   // 单帧段直接复制

        // 逐帧交付(TryDeliverFrame):Move 与两级兜底 Copy 都走重试
        string deliver = Slice(code, "private static bool TryDeliverFrame(", "private static string DescribeFileError(");
        Assert.Contains("RetryFileMove(src, dst);", deliver);
        Assert.Contains("try { RetryFileCopy(src, dst); return true; }", deliver);          // 兜底①
        int sleep = deliver.IndexOf("System.Threading.Thread.Sleep(150);", StringComparison.Ordinal);
        Assert.True(sleep > 0, "找不到交付兜底②(150ms 等待)");
        Assert.Contains("RetryFileCopy(src, dst);", deliver.Substring(sleep, 200));         // 兜底②

        // 批输入:代表帧拷进引擎输入目录(旧写法是裸 File.Copy(upFiles[g.rep], …))
        Assert.Contains("RetryFileCopy(upFiles[g.rep],", code);
        Assert.DoesNotContain("File.Copy(upFiles[g.rep],", code);

        // 引擎输出搬运:同一处不许再有裸 File.Move(f, dst …)
        int conv = code.IndexOf("EngineService.ConvertPngToJpg(f, dst, VideoFrameJpgQuality);", StringComparison.Ordinal);
        Assert.True(conv > 0, "找不到引擎输出搬运那一段");
        string engineOut = code.Substring(conv, 400);
        Assert.Contains("RetryFileMove(f, dst);", engineOut);
        Assert.DoesNotContain("File.Move(f, dst", engineOut);
    }

    /// <summary>★ 成片落地(`outTmp` → 最终文件名)与裁剪输出也要走重试 —— 这里被杀软/播放器占用同样会丢成品。</summary>
    [Fact]
    public void Final_output_move_goes_through_the_retry_helper()
    {
        Assert.Contains("RetryFileMove(outTmp, outputVideo);", CodeOnly(Source()));
    }

    /// <summary>★ 白名单之外,不许再有裸 `File.Copy/File.Move`(裸调用 = 一次瞬时占用就能让长任务失败)。</summary>
    [Fact]
    public void No_unwired_bcl_file_moves_or_copies_outside_the_retry_layer()
    {
        string code = CodeOnly(Source());
        foreach (string anchor in AllowedMethodAnchors)
            Assert.Contains(anchor, code);   // 白名单锚点自己不能失效(否则这套检查会静默放水)

        int from = code.IndexOf("private static void CopyFrame(", StringComparison.Ordinal);
        int to = code.IndexOf("private static async Task<int> InterpSegmentAsync(", StringComparison.Ordinal);
        Assert.True(from > 0 && to > from, "必须能定位重试接线层(CopyFrame → InterpSegmentAsync 之间)");
        string wiring = code.Substring(from, to - from);
        Assert.Equal(1, CountOccurrences(wiring, "File.Copy(src, dst, true)"));
        Assert.Equal(1, CountOccurrences(wiring, "File.Move(src, dst, true)"));
        Assert.Equal(1, CountOccurrences(wiring, "File.Delete(path)"));

        // ③ 循环只有 Core 那一份:宿主里再出现一次退避循环 = 两套口径
        Assert.Contains("AlhPro.Core.IO_RETRY.Run(action, path, op, AppLogger.Info)", wiring);
        Assert.DoesNotContain("Thread.Sleep(AlhPro.Core.IO_RETRY.DelayMsFor", code);
        Assert.DoesNotContain("attempt > AlhPro.Core.IO_RETRY.MaxRetries", code);

        foreach (var (needle, label) in new[]
        {
            ("File.Copy(Path.Combine(", "File.Copy"),
            ("File.Move(Path.Combine(", "File.Move"),
        })
        {
            Assert.Equal(0, CountOccurrences(RemoveAll(wiring, label == "File.Copy"
                ? new[] { "File.Copy(src, dst, true)" }
                : new[] { "File.Move(src, dst, true)" }), needle));
        }
    }

    /// <summary>★ 把类头那句声明真正检查掉:整份 VideoService 里的裸 `File.Copy/File.Move` 只允许两种形态 ——
    /// ① 在重试接线层的 op 里(`CopyFrame` → `InterpSegmentAsync` 之间,即 `RetryFileCopy` 等的 lambda);
    /// ② 与 `try {` **同一行**的尽力而为拷贝(失败不影响正确性);
    /// 已废弃的 `InterpKeyframeGapsAsync` 整段豁免(它不在任何活动调用链上)。
    /// 以后有人在新落点上写一句裸 `File.Copy` 就想"反正有重试层兜着",这条会立刻红。</summary>
    [Fact]
    public void Every_raw_bcl_file_call_is_retried_or_same_line_best_effort()
    {
        string code = CodeOnly(Source());
        int deadFrom = code.IndexOf("private static async Task InterpKeyframeGapsAsync(", StringComparison.Ordinal);
        Assert.True(deadFrom > 0, "找不到已废弃的 InterpKeyframeGapsAsync(豁免区间算不出来 ⇒ 不许静默放水)");
        int deadTo = code.IndexOf("\n    private static ", deadFrom + 10, StringComparison.Ordinal);
        Assert.True(deadTo > deadFrom, "算不出已废弃方法的结尾");
        string live = code.Substring(0, deadFrom) + code.Substring(deadTo);

        int wiringFrom = live.IndexOf("private static void CopyFrame(", StringComparison.Ordinal);
        int wiringTo = live.IndexOf("private static async Task<int> InterpSegmentAsync(", StringComparison.Ordinal);
        Assert.True(wiringFrom > 0 && wiringTo > wiringFrom, "定位不到重试接线层");

        int checkedCalls = 0;
        foreach (string needle in new[] { "File.Copy(", "File.Move(" })
        {
            int i = 0;
            while ((i = live.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
            {
                int lineStart = live.LastIndexOf('\n', i) + 1;
                int lineEnd = live.IndexOf('\n', i);
                if (lineEnd < 0) lineEnd = live.Length;
                string line = live.Substring(lineStart, lineEnd - lineStart);
                bool inWiring = i >= wiringFrom && i < wiringTo;
                bool bestEffort = line.Contains("try {") || line.Contains("catch");
                Assert.True(inWiring || bestEffort,
                    $"裸 {needle} 既不在重试接线层、也不是同行的尽力而为拷贝(瞬时占用会直接冒泡):{line.Trim()}");
                checkedCalls++;
                i += needle.Length;
            }
        }
        // 扫到的数量下限 = 接线层的 2 处(复制/改名)+ 4 处"同行 try"的尽力而为拷贝;
        // 低于这个数说明掩码/定位写坏了,这条契约在放水,必须变红。
        Assert.True(checkedCalls >= 6, $"只扫到 {checkedCalls} 处裸拷贝 —— 扫描范围不对,这条契约在放水");
    }

    /// <summary>★ 引擎侧"每对帧暂存进 layer_in"的两个拷贝也必须在同一层重试。
    /// 这两行在**视频活路径**上(`VideoService` 调 `InterpLayerBatchAsync`),而调用方的 catch 只清临时
    /// 目录再 `throw` ⇒ 一次瞬时占用照样带走整条任务;它又是 VideoService 的 private 助手够不着的地方,
    /// 所以只能共用 Core 的 `Run`。</summary>
    [Fact]
    public void Engine_layer_batch_staging_copies_go_through_the_retry_layer()
    {
        string code = CodeOnly(ReadRepoFile("ImgUpscalerUI", "EngineService.cs"));
        string body = Slice(code, "public static async Task<List<string>> InterpLayerBatchAsync(", "var ttaArgs =");
        Assert.Contains("AlhPro.Core.IO_RETRY.Run(\"暂存帧\", a, () => File.Copy(a, fa, true), AppLogger.Info)", body);
        Assert.Contains("AlhPro.Core.IO_RETRY.Run(\"暂存帧\", b, () => File.Copy(b, fb, true), AppLogger.Info)", body);
        Assert.DoesNotContain("File.Copy(a, Path.Combine(inDir", body);
        Assert.DoesNotContain("File.Copy(b, Path.Combine(inDir", body);
        // 目标路径必须在 lambda 外算好,否则每次重试 ++n 会写到别的帧号上
        Assert.Contains("string fa = Path.Combine(inDir, $\"frame_{++n:D6}.png\");", body);
        Assert.Contains("string fb = Path.Combine(inDir, $\"frame_{++n:D6}.png\");", body);
    }

    /// <summary>★ 放弃重试时的说明必须带动作与路径(1830 的日志只有 .NET 英文原话 + 一个帧号,定位不了哪一步)。
    /// 2026-10-09 起重试骨架搬进 Core(引擎侧共用同一层),所以这几条断言指向 `AlhPro.Core\IO_RETRY.cs`。</summary>
    [Fact]
    public void Give_up_uses_the_core_wording_with_action_and_path()
    {
        string core = CodeOnly(Core());
        Assert.Contains("public static void Run(string action, string path, Action op, Action<string>? log = null)", core);
        Assert.Contains("throw new IOException(DescribeGiveUp(action, path, ex), ex)", core);
        Assert.Contains("log?.Invoke(DescribeRecovered(action, path, attempt - 1))", core);
    }

    /// <summary>★ 重试次数与退避不许写死(口径唯一来源是 Core 的常量,循环也只有 Core 那一份)。</summary>
    [Fact]
    public void Retry_count_comes_from_the_core_constant()
    {
        string core = CodeOnly(Core());
        Assert.Contains("if (attempt > MaxRetries)", core);
        Assert.Contains("Thread.Sleep(DelayMsFor(attempt))", core);
        Assert.DoesNotContain("attempt > 10", core);
        Assert.DoesNotContain("Thread.Sleep(50)", core);
    }

    /// <summary>★ 判据本身要真的接在重试循环上(判据删了或没接上,接线就只是空转)。</summary>
    [Fact]
    public void The_transient_violation_judgement_is_wired()
    {
        string core = CodeOnly(Core());
        Assert.Contains("catch (Exception ex) when (IsTransientShareViolation(ex))", core);
        Assert.Contains("Thread.Sleep(DelayMsFor(attempt))", core);
    }

    /// <summary>★ **自拷贝短路**必须还在:帧数对齐的"末帧补足"在源就是最后一帧时,`File.Copy(a, a, true)`
    /// 在 Windows 上必然报共享冲突(0x80070020)、退避一万次也不会成功 —— 只能直接跳过。
    /// 真有人把这层删掉,这条会红(不然那次事故又会原样复现)。</summary>
    [Fact]
    public void Self_copy_short_circuits_before_any_retry()
    {
        string code = CodeOnly(Source());
        string copy = Slice(code, "private static void RetryFileCopy(", "private static void RetryFileMove(");
        int same = copy.IndexOf("IsSameFile(src, dst)", StringComparison.Ordinal);
        int io = copy.IndexOf("RetryFileIo(\"复制帧\"", StringComparison.Ordinal);
        Assert.True(same > 0, "RetryFileCopy 里找不到自拷贝判据 IsSameFile(src, dst)");
        Assert.True(io > same, "自拷贝判据必须在真正做 IO(RetryFileIo)之前短路");
        Assert.Contains("if (IsSameFile(src, dst)) return;", copy);

        Assert.Contains("private static bool IsSameFile(string src, string dst)", code);
        Assert.Contains("Path.GetFullPath(src), Path.GetFullPath(dst)", code);
        Assert.Contains("StringComparison.OrdinalIgnoreCase", code);   // Windows 路径大小写不敏感
    }

    /// <summary>★ **首次**撞上瞬时占用必须落一行日志(带动作/路径/HResult)。
    /// 1830 的诊断包里只有 .NET 的英文原话与一个帧号 —— 连"是哪一步在读这一帧"都看不出来。</summary>
    [Fact]
    public void First_transient_hit_is_logged_with_action_and_path()
    {
        string core = CodeOnly(Core());
        Assert.Contains("log?.Invoke(DescribeTransient(action, path, attempt, ex))", core);
        int run = core.IndexOf("public static void Run(", StringComparison.Ordinal);
        Assert.True(run > 0, "Core 里找不到重试骨架 Run(...)");
        string body = core.Substring(run, Math.Min(1400, core.Length - run));
        Assert.Contains("if (attempt == 1)", body);   // 只落首次,不是每次重试都刷屏
    }

    // ────────────────────────────── helpers ──────────────────────────────

    private static string Source() => ReadRepoFile("ImgUpscalerUI", "VideoService.cs");

    /// <summary>重试骨架与判据的所在地(视频侧与引擎侧共用同一层)。</summary>
    private static string Core() => ReadRepoFile("AlhPro.Core", "IO_RETRY.cs");

    /// <summary>去掉整行注释(避免注释里提到的 `File.Copy(...)` 被当成真调用)。</summary>
    private static string CodeOnly(string text)
        => string.Join("\n", text.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));

    /// <summary>取两个锚点之间的源码片段(start 含、end 不含);两个锚点都必须存在,
    /// 且 end 必须在 start 之后(锚点失效时立刻变红,不许静默放水)。</summary>
    private static string Slice(string code, string start, string end)
    {
        int i = code.IndexOf(start, StringComparison.Ordinal);
        Assert.True(i > 0, $"找不到片段起点:{start}");
        int j = code.IndexOf(end, i + start.Length, StringComparison.Ordinal);
        Assert.True(j > i, $"找不到片段终点(或终点在起点之前):{end}");
        return code.Substring(i, j - i);
    }

    private static string RemoveAll(string text, string[] needles)
    {
        foreach (var n in needles) text = text.Replace(n, "");
        return text;
    }

    private static int CountOccurrences(string text, string needle)
    {
        int n = 0, i = 0;
        while ((i = text.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

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
