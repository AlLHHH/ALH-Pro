namespace ALHPro;

/// <summary>ContentDialog 的安全包装（2026-09-23 修 A1 / A10）。
///
/// 【为什么必须有】WinUI **同一时刻只允许一个 ContentDialog**：已有对话框开着时再调
/// `ShowAsync()` 会抛 `COMException 0x80000019`（"Only a single ContentDialog can be open at any time"）。
/// 如果那个调用点没有 try/catch，就是**未处理异常**：写一份崩溃诊断、用户只看到"点了没反应"。
///
/// 【两次真机崩溃都出自这里，所以是"一类问题"而不是"一个 bug"】
///   ① 任务跑完的「处理完成」开着 → 点「预览效果」→ `ShowPauseHintAsync` 抛（22:09 实测）；
///   ② 启动时的「更新公告」开着 → 直接点「开始处理」→ 跑完要弹「处理完成」→ `ShowResultAsync` 抛（22:23 实测）。
/// 用户的行为完全正常（开着公告就点开始处理），所以**必须由程序兜住**，不能让用户承担。
///
/// 【用法】把 `await dlg.ShowAsync()` 换成 `await SafeDialog.ShowAsync(dlg, "处理完成")`
/// —— 第二个参数只进日志，说明"哪个弹窗没弹出来"。</summary>
internal static class SafeDialog
{
    public static async Task<Microsoft.UI.Xaml.Controls.ContentDialogResult> ShowAsync(
        Microsoft.UI.Xaml.Controls.ContentDialog dlg, string what = "对话框")
    {
        try
        {
            return await dlg.ShowAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"{what}没能弹出来（多半是已有另一个对话框开着：WinUI 同时只允许一个）：{ex.Message}"
                + " ⇒ 已跳过这次弹窗，不影响任务本身；相关结论仍会写进左下角日志与诊断日志");
            return Microsoft.UI.Xaml.Controls.ContentDialogResult.None;
        }
    }
}
