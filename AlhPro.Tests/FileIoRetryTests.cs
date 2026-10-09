using AlhPro.Core;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【2026-10-09 · t65】帧文件"瞬时占用重试"判据的单测。
///
/// 背景:诊断包 `ALHPro_Diag_20261009_1830`(RTX 5060 Ti)跑了 90 分 31 秒、超分 10808 帧全做完,
/// 却因为"合帧前的帧数对齐"里一次 `File.Copy` 撞上
/// `The process cannot access the file '...\upscaled\frame_010809.jpg' because it is being used by
/// another process.`(HResult 0x80070020)⇒ 整条任务「成功 0,失败 1,输出 0 个」。
///
/// 这里钉住三件事:
/// ① 只有"共享冲突/锁冲突"这两个 HResult 才算瞬时占用(判宽了会把磁盘满/路径非法也一起白等);
/// ② 退避时长单调不减且封顶(不能无限涨);
/// ③ 放弃时的说明里**必须**有动作与文件路径(旧日志只有 .NET 英文原话与一个帧号,定位不了是哪一步)。</summary>
public class FileIoRetryTests
{
    /// <summary>把异常对象的 HResult 改成指定值(HResult 是 internal setter,只能反射)。
    /// 单测必须能造出"和真机一模一样"的 HResult —— 否则这组判据测的是"看起来像",不是"就是它"。</summary>
    private static T WithHResult<T>(T ex, int hresult) where T : Exception
    {
        typeof(Exception)
            .GetProperty("HResult", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .SetValue(ex, hresult);
        return ex;
    }

    private const int SharingViolation = unchecked((int)0x80070020);   // ERROR_SHARING_VIOLATION
    private const int LockViolation = unchecked((int)0x80070021);      // ERROR_LOCK_VIOLATION

    [Fact]
    public void IoException_sharing_violation_is_transient()
        => Assert.True(IO_RETRY.IsTransientShareViolation(
            WithHResult(new IOException("The process cannot access the file because it is being used by another process."), SharingViolation)));

    [Fact]
    public void IoException_lock_violation_is_transient()
        => Assert.True(IO_RETRY.IsTransientShareViolation(WithHResult(new IOException("locked"), LockViolation)));

    [Fact]
    public void UnauthorizedAccessException_sharing_violation_is_transient()
        => Assert.True(IO_RETRY.IsTransientShareViolation(
            WithHResult(new UnauthorizedAccessException("Access to the path is denied."), SharingViolation)));

    [Fact]
    public void Transient_violation_is_found_through_inner_exception_chain()
    {
        var inner = WithHResult(new IOException("being used by another process"), SharingViolation);
        var outer = new InvalidOperationException("帧数对齐失败", inner);
        Assert.True(IO_RETRY.IsTransientShareViolation(outer));
    }

    [Fact]
    public void Plain_ioexception_without_share_hresult_is_not_transient()
        => Assert.False(IO_RETRY.IsTransientShareViolation(new IOException("There is not enough space on the disk.")));

    [Fact]
    public void FileNotFound_is_not_transient()
        => Assert.False(IO_RETRY.IsTransientShareViolation(new FileNotFoundException("missing", @"D:\x\frame_000001.jpg")));

    [Fact]
    public void Other_exception_types_are_not_transient()
        => Assert.False(IO_RETRY.IsTransientShareViolation(new InvalidOperationException("无关错误")));

    [Fact]
    public void Null_is_not_transient()
        => Assert.False(IO_RETRY.IsTransientShareViolation(null));

    [Fact]
    public void Delay_is_positive_and_never_exceeds_cap()
    {
        for (int attempt = 1; attempt <= 40; attempt++)
        {
            int d = IO_RETRY.DelayMsFor(attempt);
            Assert.InRange(d, 1, IO_RETRY.MaxDelayMs);
        }
    }

    [Fact]
    public void Delay_is_non_decreasing_and_reaches_cap()
    {
        int prev = 0;
        for (int attempt = 1; attempt <= 30; attempt++)
        {
            int d = IO_RETRY.DelayMsFor(attempt);
            Assert.True(d >= prev, $"第 {attempt} 次退避({d}ms)比上一次({prev}ms)小 —— 退避必须单调不减");
            prev = d;
        }
        Assert.Equal(IO_RETRY.MaxDelayMs, IO_RETRY.DelayMsFor(30));
    }

    [Fact]
    public void Total_wait_is_bounded_and_long_enough_to_ride_out_a_scan()
    {
        // 10 次退避合计要"够长"(覆盖杀软/索引器毫秒~秒级句柄),但又不能长到用户以为卡死。
        int total = 0;
        for (int attempt = 1; attempt <= IO_RETRY.MaxRetries; attempt++) total += IO_RETRY.DelayMsFor(attempt);
        Assert.InRange(total, 1500, 15_000);
    }

    [Fact]
    public void Attempt_below_one_is_treated_as_first_attempt()
        => Assert.Equal(IO_RETRY.DelayMsFor(1), IO_RETRY.DelayMsFor(0));

    [Fact]
    public void GiveUp_message_carries_action_and_file_path()
    {
        var msg = IO_RETRY.DescribeGiveUp("复制帧", @"D:\ALHProTemp\work\upscaled\frame_010809.jpg",
            WithHResult(new IOException("being used by another process"), SharingViolation));
        Assert.Contains("复制帧", msg);
        Assert.Contains("frame_010809.jpg", msg);
        Assert.Contains(@"D:\ALHProTemp\work\upscaled\frame_010809.jpg", msg);
    }

    [Fact]
    public void Recovered_message_reports_retry_count()
    {
        var msg = IO_RETRY.DescribeRecovered("搬移文件", @"D:\out\mike 2_超分2x.mp4", 3);
        Assert.Contains("搬移文件", msg);
        Assert.Contains("mike 2_超分2x.mp4", msg);
        Assert.Contains("3", msg);
    }

    [Fact]
    public void Share_violation_hresults_match_windows_error_codes()
    {
        // 判据用的是与语言无关的数值:中文/英文 Windows 上 Message 不同,HResult 恒为这两个值。
        Assert.Equal(SharingViolation, unchecked((int)0x80070020));
        Assert.Equal(LockViolation, unchecked((int)0x80070021));
    }

    [Fact]
    public void Transient_message_carries_action_path_and_hresult()
    {
        // 首次撞上就要落一行日志:1830 那次的日志里既没有动作也没有路径,事后只能靠猜。
        var msg = IO_RETRY.DescribeTransient("复制帧", @"D:\ALHProTemp\work\upscaled\frame_010809.jpg", 1,
            WithHResult(new IOException("being used by another process"), SharingViolation));
        Assert.Contains("复制帧", msg);
        Assert.Contains("frame_010809.jpg", msg);
        Assert.Contains(@"D:\ALHProTemp\work\upscaled\frame_010809.jpg", msg);
        Assert.Contains("80070020", msg);                      // HResult 数值可比、跨语言可靠
        Assert.Contains(IO_RETRY.DelayMsFor(1).ToString(), msg); // 报出这次要退避多久
    }

    /// <summary>★ 真机锁,不是反射造的 HResult:判据认的必须是 Windows 真会抛的那个异常。
    /// (本机实测:`File.Copy` 撞上 `FileShare.None` 的源 = `IOException` / 0x80070020。)</summary>
    [Fact]
    public void Real_windows_sharing_violation_is_classified_as_transient()
    {
        string dir = Path.Combine(Path.GetTempPath(), "alh_io_retry_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string src = Path.Combine(dir, "frame_010809.jpg");
            string dst = Path.Combine(dir, "frame_010810.jpg");
            File.WriteAllText(src, "x");
            using (new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var ex = Record.Exception(() => File.Copy(src, dst, true));
                Assert.NotNull(ex);
                Assert.True(IO_RETRY.IsTransientShareViolation(ex),
                    $"真机上的占用没被判成瞬时占用:{ex.GetType().Name} HResult=0x{ex.HResult:X8} —— {ex.Message}");
            }
            Assert.False(File.Exists(dst));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>★ 反向也钉住:目的端被独占时 `File.Move` 报的是 ACCESS_DENIED(0x80070005),**故意**不重试
    /// (同一个值也是"只读文件/ACL 永久拒绝",判宽会让批量循环里每个文件白等一轮退避)。
    /// 以后谁想顺手把 0x5 加进重试集合,这条会先红。</summary>
    [Fact]
    public void Real_locked_destination_move_is_not_treated_as_transient()
    {
        if (!OperatingSystem.IsWindows()) return;   // 这套判据是 Windows 错误码口径,别的系统不适用
        string dir = Path.Combine(Path.GetTempPath(), "alh_io_retry_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string src = Path.Combine(dir, "from.mp4");
            string dst = Path.Combine(dir, "to.mp4");
            File.WriteAllText(src, "s");
            File.WriteAllText(dst, "d");
            using (new FileStream(dst, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var ex = Record.Exception(() => File.Move(src, dst, true));
                Assert.NotNull(ex);
                Assert.False(IO_RETRY.IsTransientShareViolation(ex),
                    $"目的端被独占被判成了「可重试」({ex.GetType().Name} HResult=0x{ex.HResult:X8})—— 这会牵连只读/ACL 永久拒绝");
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>★ 端到端(正面):真锁 250ms 后放开 ⇒ `Run` 必须自己等到放开并成功。
    /// 这是 1830 那类事故的**紧反馈回路** —— 判据单测只证明"认得出",这条证明"真的能骑过去"。</summary>
    [Fact]
    public void Run_rides_out_a_lock_that_is_released_mid_retry()
    {
        if (!OperatingSystem.IsWindows()) return;
        string dir = Path.Combine(Path.GetTempPath(), "alh_io_retry_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string src = Path.Combine(dir, "frame_010809.jpg");
        string dst = Path.Combine(dir, "frame_010810.jpg");
        File.WriteAllText(src, "frame");
        var log = new System.Collections.Generic.List<string>();
        try
        {
            using (var hold = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                System.Threading.Tasks.Task.Run(async () => { await System.Threading.Tasks.Task.Delay(250); hold.Dispose(); });
                IO_RETRY.Run("复制帧", src, () => File.Copy(src, dst, true), log.Add);
            }
            Assert.True(File.Exists(dst), "重试没把「锁 250ms 后放开」这次占用骑过去");
            Assert.Contains(log, l => l.Contains("复制帧"));   // 撞上过要留痕(诊断包里能数出这台机器有多少次占用)
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>★ 端到端(反面):锁着不放 ⇒ `Run` 必须在**有界**时间内放弃,抛出带"动作 + 完整路径"的
    /// `IOException`,而且这个包装异常仍要能被判据认出来(内层是真占用)—— 宿主那两处"补不上就算了"的
    /// 软失败靠 `IsTransientShareViolation(ex)` 分辨"被占用"与"磁盘满/路径非法",这条把该前提钉住。
    /// 代价是这一条要真等一轮退避(约 7 秒);换来"永远不会无限等"这个承诺由真机锁验证。</summary>
    [Fact]
    public void Run_gives_up_with_action_and_path_when_the_lock_never_ends()
    {
        if (!OperatingSystem.IsWindows()) return;
        string dir = Path.Combine(Path.GetTempPath(), "alh_io_retry_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string src = Path.Combine(dir, "frame_010809.jpg");
        string dst = Path.Combine(dir, "frame_010810.jpg");
        File.WriteAllText(src, "frame");
        var log = new System.Collections.Generic.List<string>();
        try
        {
            using (new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var ex = Record.Exception(() => IO_RETRY.Run("复制帧", src, () => File.Copy(src, dst, true), log.Add));
                sw.Stop();
                var io = Assert.IsType<IOException>(ex);
                Assert.Contains("复制帧", io.Message);
                Assert.Contains(src, io.Message);
                Assert.True(IO_RETRY.IsTransientShareViolation(io),
                    "放弃时抛的包装异常要能看出根因是占用(判据要穿透 InnerException)");
                Assert.InRange(sw.ElapsedMilliseconds, 2000, 20000);
            }
            Assert.False(File.Exists(dst));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
