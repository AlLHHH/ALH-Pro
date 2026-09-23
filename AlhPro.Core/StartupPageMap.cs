namespace AlhPro.Core;

/// <summary>「启动页编号 ↔ 左侧导航项下标」的唯一换算（2026-09-23 修 A2）。
///
/// 【病根 · 真机实测】两套编号**不是同一套**：
///   · 启动页编号（设置页下拉 / 上次退出界面 都用它）：0 图片放大、1 图片抠图、2 **视频处理**、3 音频处理、4 视频抠图；
///   · 左侧导航列表（XAML 里的书写顺序）：0 图片放大、1 图片抠图、2 **视频抠图**、3 视频处理、4 音频处理。
/// 旧代码 `NavList.SelectedIndex = page0;` 把前者直接当后者用 ⇒ 实测：
/// 设置里选「视频处理」→ 重启 **进的是「视频抠图」**（那个锁着的"开发中"页）；
/// 选「音频处理」→ 进「视频处理」；选「视频抠图」→ 因为读的上限只到 3，**永远存不进去**。
///
/// 纯逻辑、零副作用 ⇒ 可单测（见 <c>StartupPageMapTests</c>）。
/// **改这里的规矩**：以后加/挪左侧导航项，必须同时改 <see cref="NavIndexFor"/> —— 并跑那条单测。</summary>
public static class StartupPageMap
{
    /// <summary>启动页编号 → 左侧导航项下标（NavList.SelectedIndex 用的那个）。</summary>
    public static int NavIndexFor(int startupPage) => startupPage switch
    {
        0 => 0,   // 图片放大
        1 => 1,   // 图片抠图
        2 => 3,   // 视频处理（导航里排第 4 项 ⇒ 下标 3）
        3 => 4,   // 音频处理（导航里排第 5 项 ⇒ 下标 4）
        4 => 2,   // 视频抠图（导航里排第 3 项 ⇒ 下标 2）
        _ => 0,   // 非法/未知：回到"图片放大"，绝不乱跳
    };

    /// <summary>左侧导航 Tag（ShowView 用的） → 启动页编号；不是五个功能页（教程/设置/…）返回 -1 = **别记**。
    /// 【为什么要区分】原来关闭窗口那次保存用的是三元表达式，把"音频处理/视频抠图"都写成了 0（图片放大），
    /// 在教程页退出也会把上次页冲掉 ⇒ 返回 -1 让调用方跳过写入。</summary>
    public static int PageForTag(string? tag) => tag switch
    {
        "upscale" => 0,
        "cutout" => 1,
        "video" => 2,
        "audio" => 3,
        "matting" => 4,
        _ => -1,
    };

    /// <summary>合法的启动页编号（-1 = 「上次退出界面」）。</summary>
    public static bool IsValidPage(int p) => p >= -1 && p <= 4;

    /// <summary>合法的"上次退出界面"编号（只能是五个功能页，没有 -1）。</summary>
    public static bool IsValidLastPage(int p) => p >= 0 && p <= 4;
}
