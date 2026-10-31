using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【2026-10-31 · 真机故障】RTX 5070 Ti 上「Real-CUGAN 用不了」:
/// 用户在 2026-10-31 的诊断包里,realcugan 每次都在 60 秒上限被当 hang 杀掉(4 次),
/// 而同一台机器的 realesrgan / waifu2x / RIFE 探测全部通过。
///
/// 根因不在显卡、不在模型、也不在参数(本机同参数直跑 2.6 秒出图),而在探测路径:
/// `ProcessStartInfo` 把 stdout/stderr 重定向了却**从不读取**。Real-CUGAN 即使不加 -v 也会
/// 逐层打印 [done-vk]/[fwd-vk]/[vkex] 几百行,Windows 管道缓冲一满,子进程就阻塞在写管道上
/// ⇒ 父进程看到的是"零输出、永不退出"。本机复现:同一命令,控制台直跑 2.6 秒完成;
/// 改成"重定向但不读" ⇒ 70 秒不退出。
///
/// 这个测试钉两件事(AlhPro.Tests 只引用 AlhPro.Core,WinUI 侧只能做源码契约):
///   ① 两处引擎探测(超分探测、RIFE 探测)必须排空管道,并且把被杀前的尾部输出写进日志
///      —— 否则下一次同样的故障,日志里还是"一个字节都没有",照样判不出是谁的问题;
///   ② 凡是重定向了标准流的进程,后面都得有读它的人(防止以后新加一条调用又踩同一个坑)。
/// **诚实边界**:契约只能证明"代码里有排空",证明不了"真机上 realcugan 现在能过" —— 后者要
/// 50 系机器实跑一次探测才算数。</summary>
public class ProbePipeDrainTests
{
    /// <summary>★ 超分探测与 RIFE 探测都必须有排空任务(两处各一条 out、一条 err)。</summary>
    [Fact]
    public void Probe_paths_drain_engine_stdout_and_stderr()
    {
        var code = StripComments(ReadRepoFile("ImgUpscalerUI", "EngineService.cs"));
        Assert.Equal(2, CountOf(code, "DrainTailAsync(p.StandardOutput"));
        Assert.Equal(2, CountOf(code, "DrainTailAsync(p.StandardError"));
        // 排空必须是"启动了就读",不能等进程退出后再读(那时 Real-CUGAN 已经堵死在写管道上)
        Assert.Contains("var drainOutTask = DrainTailAsync(p.StandardOutput);", code);
        Assert.Contains("var rfOutTask = DrainTailAsync(p.StandardOutput);", code);
    }

    /// <summary>★ 超时/被杀时必须把引擎最后说了什么写进日志 —— 这是这次故障唯一能自证的东西。</summary>
    [Fact]
    public void Hang_reports_the_tail_of_engine_output()
    {
        var code = StripComments(ReadRepoFile("ImgUpscalerUI", "EngineService.cs"));
        Assert.Contains("引擎被杀前的最后输出", code);
        Assert.Contains("引擎被杀前一个字节输出都没有", code);   // 零输出也要说清(那才是管道堵死的典型形态)
        Assert.Contains("DrainTailTextAsync(drainOutTask, drainErrTask)", code);
        Assert.Contains("DrainTailTextAsync(rfOutTask, rfErrTask)", code);
    }

    /// <summary>★ 通用契约:重定向了标准流,后面就得有人读它。
    /// 允许的三类读法:探测用的 DrainTailAsync、生产路径的 DrainAsync(逐块回调)、Startup 探针的 ReadToEnd。</summary>
    [Fact]
    public void Every_redirected_stream_has_a_reader()
    {
        var code = ReadRepoFile("ImgUpscalerUI", "EngineService.cs");
        var lines = code.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            foreach (var (flag, reader) in new[]
                     {
                         ("RedirectStandardOutput = true", "StandardOutput"),
                         ("RedirectStandardError = true", "StandardError"),
                     })
            {
                if (!lines[i].Contains(flag, StringComparison.Ordinal)) continue;
                // 允许的读法(同一处调用后面 130 行内出现其一即可):
                //   p.StandardOutput / pr.StandardOutput 上的 DrainTailAsync / DrainAsync / ReadToEnd*
                bool hasReader = false;
                for (int j = i; j < Math.Min(lines.Length, i + 131); j++)
                {
                    var l = lines[j];
                    if (l.Contains($".{reader}.ReadToEnd", StringComparison.Ordinal)
                        || l.Contains($"DrainTailAsync(p.{reader}", StringComparison.Ordinal)
                        || l.Contains($"DrainAsync(p.{reader}", StringComparison.Ordinal)
                        || l.Contains($"DrainAsync(proc.{reader}", StringComparison.Ordinal)
                        || l.Contains($"pr.{reader}.ReadToEnd", StringComparison.Ordinal))
                    {
                        hasReader = true;
                        break;
                    }
                }
                Assert.True(hasReader,
                    $"第 {i + 1} 行重定向了 {reader} 却没人读它 —— Real-CUGAN 那种逐层打印的引擎会把管道写满并永久阻塞"
                    + $"(2026-10-31 真机故障就是这样被误判成「显卡 hang」的)。行内容:{lines[i].Trim()}");
            }
        }
    }

    // ───────────────────────── 工具 ─────────────────────────

    private static int CountOf(string haystack, string needle)
    {
        int n = 0, at = 0;
        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { n++; at += needle.Length; }
        return n;
    }

    /// <summary>剥掉整行注释:上面几段说明里必然出现 "DrainTailAsync" 等字样,契约钉的是代码。</summary>
    private static string StripComments(string src)
        => string.Join("\n", src.Replace("\r\n", "\n").Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

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
