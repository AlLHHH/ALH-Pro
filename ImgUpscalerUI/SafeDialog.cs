namespace ALHPro;

/// <summary>ContentDialog 的安全包装（2026-09-23 修 A1 / A10；2026-10-16 加串行闸门）。
///
/// 【为什么必须有】WinUI **同一时刻只允许一个 ContentDialog**：已有对话框开着时再调
/// `ShowAsync()` 会抛 `COMException 0x80000019`（"Only a single ContentDialog can be open at any time"）。
/// 如果那个调用点没有 try/catch，就是**未处理异常**：写一份崩溃诊断、用户只看到"点了没反应"。
///
/// 【三次真机崩溃都出自这里，所以是"一类问题"而不是"一个 bug"】
///   ① 任务跑完的「处理完成」开着 → 点「预览效果」→ `ShowPauseHintAsync` 抛（22:09 实测）；
///   ② 启动时的「更新公告」开着 → 直接点「开始处理」→ 跑完要弹「处理完成」→ `ShowResultAsync` 抛（22:23 实测）；
///   ③ 图片页/抠图页/图片工具页里同形的一串调用点（2026-10-16 复审发现，见下）。
/// 用户的行为完全正常（开着公告就点开始处理），所以**必须由程序兜住**，不能让用户承担。
///
/// 【2026-10-16 · 从"吞异常"升级成"排队 + 有界等待"】
/// 旧版只是 try/catch 把冲突吞掉：冲突时**第二个对话框永远不出现**，用户看不到结论
/// （比如"文件内容不是图片预设""已导入 N 个预设"），而且 `ContentDialogResult.None` 既可能是
/// "用户点了关闭"、也可能是"压根没弹出来"，调用方分不清 —— 视频页那处就把 None 当成"用户取消"，
/// 于是**直接把整批任务中止**（见 VideoView 的 4K 提醒）。现在：
///   · 所有走本类的调用共享一个闸门：前一个对话框没关就先等，关掉后自动接着弹（不再丢提示）；
///   · 等太久（<see cref="DefaultWaitMs"/>）就降级成"这次不弹了 + 写日志"，绝不无限期挂着 UI；
///   · 需要区分两种情况时用 <see cref="TryShowAsync"/>（返回 Shown 标志），别再拿 None 当"用户取消"。
///
/// 【用法】把 `await dlg.ShowAsync()` 换成 `await SafeDialog.ShowAsync(dlg, "处理完成")`
/// —— 第二个参数只进日志，说明"哪个弹窗没弹出来"。</summary>
internal static class SafeDialog
{
    /// <summary>等前一个对话框关闭的上限（毫秒）。超过就跳过这次弹窗，避免把调用方卡死。</summary>
    public const int DefaultWaitMs = 15000;

    private static readonly System.Threading.SemaphoreSlim _gate = new(1, 1);
    private static int _openCount;

    /// <summary>此刻是否已经有一个对话框开着（诊断用；也给测试与单元自验看）。</summary>
    public static bool IsOpen => System.Threading.Volatile.Read(ref _openCount) > 0;

    /// <summary>排队并弹出对话框；返回"是否真弹出来了 + 用户结果"。
    /// `Shown == false` 时 `Result` 恒为 None 且**不代表用户点了什么**（别据此取消任务）。</summary>
    public static async Task<(bool Shown, Microsoft.UI.Xaml.Controls.ContentDialogResult Result)> TryShowAsync(
        Microsoft.UI.Xaml.Controls.ContentDialog dlg, string what = "对话框", int waitMs = DefaultWaitMs)
    {
        bool entered = false;
        try
        {
            entered = await _gate.WaitAsync(waitMs > 0 ? waitMs : DefaultWaitMs).ConfigureAwait(true);
            if (!entered)
            {
                AppLogger.Warn($"{what}没能弹出来：前一个对话框一直没关，等了 {waitMs} 毫秒后跳过这次弹窗"
                    + "（不影响任务本身；相关结论仍会写进左下角日志与诊断日志）");
                return (false, Microsoft.UI.Xaml.Controls.ContentDialogResult.None);
            }
            System.Threading.Interlocked.Increment(ref _openCount);
            var result = await dlg.ShowAsync();
            return (true, result);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"{what}没能弹出来（多半是已有另一个对话框开着：WinUI 同时只允许一个）：{ex.Message}"
                + " ⇒ 已跳过这次弹窗，不影响任务本身；相关结论仍会写进左下角日志与诊断日志");
            return (false, Microsoft.UI.Xaml.Controls.ContentDialogResult.None);
        }
        finally
        {
            if (entered)
            {
                System.Threading.Interlocked.Decrement(ref _openCount);
                _gate.Release();
            }
        }
    }

    /// <summary>排队并弹出对话框，只要结果（旧签名，兼容既有调用点）。</summary>
    public static async Task<Microsoft.UI.Xaml.Controls.ContentDialogResult> ShowAsync(
        Microsoft.UI.Xaml.Controls.ContentDialog dlg, string what = "对话框")
        => (await TryShowAsync(dlg, what).ConfigureAwait(true)).Result;
}
