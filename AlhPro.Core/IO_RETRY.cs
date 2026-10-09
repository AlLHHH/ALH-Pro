namespace AlhPro.Core;

/// <summary>【2026-10-09 · t65】帧文件读写的**瞬时占用重试**判据(纯逻辑,可单测)。
///
/// 【为什么有这份东西 —— 来自用户诊断包 `ALHPro_Diag_20261009_1830`】
/// 那台机器(RTX 5060 Ti / 7800X3D)跑一条 90 秒素材、超分 2x + 补帧到 120fps 的任务:
/// 拆帧/去重/补帧/超分全部跑完(超分 10808 帧实测 170 ms/帧),整整 **90 分 31 秒**,
/// 却在"合帧前的帧数对齐"那一步以一行
///   `The process cannot access the file 'D:\ALHProTemp\...\upscaled\frame_010809.jpg'
///    because it is being used by another process.`
/// 收尾 —— 任务是 `成功 0,失败 1,输出 0 个文件`,90 分钟的产物一个字节都没落地。
///
/// 【为什么会"被另一个进程占用"】这一层是 Windows 文件共享语义,不是本软件的 bug:
/// 帧目录里的 JPG 在整个流水线里被反复读写 —— 超分批次(多批并发/引擎子进程)落盘刚收尾、
/// ffmpeg 合帧/校验在读同一帧、杀软与索引器(Windows Defender / Everything / 云同步)会短暂
/// 打开刚写出的图片扫描。这些占用**都是毫秒~秒级的瞬时状态**,退避几十毫秒再试一次就过去了。
/// 旧代码对其中几处(`File.Copy` / `File.Move`)**没有**任何重试,一次瞬时占用直接冒泡到
/// `ProcessVideoAsync` 外层 ⇒ 整条任务按失败收尾(正是上面那 90 分钟)。
///
/// 【口径纪律】这里只放**判据与文案**(纯函数);`VideoService` 只做接线(重试循环 + 日志)。
/// "哪些 HResult 才算瞬时占用"必须有单测钉住(见 `FileIoRetryTests`):判宽了会把真正的
/// 「磁盘满 / 文件不存在 / 路径非法」也一起重试(白等),判窄了就等于没修(继续丢任务)。
/// </summary>
public static class IO_RETRY
{
    /// <summary>瞬时占用时**最多重试几次**(不含首次尝试)。12 次 × 退避合计约 8 秒 ——
    /// 足够覆盖杀软扫描/索引器句柄/引擎进程收尾这几类毫秒~秒级占用,又不会让用户觉得卡死
    /// (相对 1830 那次 90 分 31 秒的全废,多等 8 秒可以忽略)。</summary>
    public const int MaxRetries = 12;

    /// <summary>首次重试前的等待(毫秒)。之后按 1.6 倍递增(50→80→128→…),上限 <see cref="MaxDelayMs"/>。</summary>
    public const int BaseDelayMs = 50;

    /// <summary>单次重试等待上限(毫秒)。</summary>
    public const int MaxDelayMs = 1000;

    // Windows 文件共享冲突的两个 HResult(与语言无关的数值,比异常文案可靠 ——
    // 中文/英文系统上 Message 完全不同,但 HResult 恒为这两个值):
    //   0x80070020 = ERROR_SHARING_VIOLATION(另一个进程正打开着这个文件)
    //   0x80070021 = ERROR_LOCK_VIOLATION(另一个进程锁住了这个区域)
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);

    /// <summary>这个异常是不是"文件被另一个进程/线程瞬时占用",值得退避重试。
    /// 【只认这两类】<see cref="System.IO.IOException"/>(含 <see cref="System.IO.FileNotFoundException"/> 等派生)
    /// 与 <see cref="System.UnauthorizedAccessException"/>(只读属性/ACL 也会走这个 HResult)。</summary>
    public static bool IsTransientShareViolation(Exception? ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is not IOException && e is not UnauthorizedAccessException) continue;
            if (e.HResult == SharingViolation || e.HResult == LockViolation) return true;
        }
        return false;
    }

    /// <summary>第 <paramref name="attempt"/> 次失败后该等多久(毫秒,1 起算)。
    /// 50 → 80 → 128 → 205 → 328 → 525 → 840 → 1000 → 1000 …(指数退避 + 封顶)。</summary>
    public static int DelayMsFor(int attempt)
    {
        if (attempt < 1) attempt = 1;
        double d = BaseDelayMs;
        for (int i = 1; i < attempt; i++)
        {
            d *= 1.6;
            if (d >= MaxDelayMs) return MaxDelayMs;
        }
        return (int)Math.Round(d);
    }

    /// <summary>重试耗尽后的第一条面向日志的说明。**必须把动作与路径写进去**:
    /// 1830 那次的日志只有 .NET 的英文原话与一个帧号,连"是哪一步在读这一帧"都看不出来
    /// (副本 + 一长串参数列表里没有一次 `File.Copy` 的调用点)。</summary>
    public static string DescribeGiveUp(string action, string path, Exception ex)
        => $"⚠ 文件被占用,重试 {MaxRetries} 次后仍失败:{action} {Path.GetFileName(path)}({path})"
           + $" —— {ex.Message.Split('\n')[0]}";

    /// <summary>重试成功(发生过等待)时的说明,便于统计"这条机器上到底有多少次瞬时占用"。</summary>
    public static string DescribeRecovered(string action, string path, int retries)
        => $"ℹ {action} {Path.GetFileName(path)} 遇到瞬时占用,退避重试 {retries} 次后成功";

    /// <summary>**首次**撞上瞬时占用时的说明(准备第 <paramref name="attempt"/> 次尝试)。
    /// 1830 那次的日志里既没有动作也没有路径,只有 .NET 的英文原话 —— 事后只能靠猜。
    /// 这里把动作、路径、HResult(数值,跨语言可比)一起落进日志,便于以后统计
    /// "这台机器上到底发生了多少次瞬时占用、卡在哪一步"。</summary>
    public static string DescribeTransient(string action, string path, int attempt, Exception ex)
        => $"ℹ {action} {Path.GetFileName(path)} 被别的进程占着(第 {attempt} 次尝试失败,HResult=0x{ex.HResult:X8}),"
           + $"退避 {DelayMsFor(attempt)}ms 后重试(最多 {MaxRetries} 次):{path}";
}
