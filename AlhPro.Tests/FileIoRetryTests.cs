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
}
