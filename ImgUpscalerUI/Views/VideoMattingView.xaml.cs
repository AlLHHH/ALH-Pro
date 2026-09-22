using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ALHPro.Views;

/// <summary>视频抠图页(独立板块,2026-09-22)。接线到 `VideoMattingService`。
///
/// 【为什么独立成页】用户明确要求:"要的是单独一个界面 视频抠图,原先的还是图片抠图"。
/// 两页共享的只有模型注册表(`CutoutService.Models`)与参数语义。
///
/// 【布局照「视频处理」页】用户给了三张对照图并要求"一模一样的风格布局":
///   ① 右侧 = 「等待任务」任务板:工具栏(添加视频/移除选中/清空/清除已完成)+ 右侧提示 + 空态(拖入项目);
///   ② 滑条 = 左标签 + 滑条 + 右数值 的行式,外加「重置调整」;
///   ③ 输出 = 路径框 + 「选择输出位置...」+「码率 / 格式」组,且都在「开始处理」**上面**。
/// 进度/结果走底部状态栏(StatusChanged,与图片抠图页同一套订阅),页面里不放日志框。
/// </summary>
public sealed partial class VideoMattingView : UserControl
{
    /// <summary>状态文本(底部状态栏)。MainPage 订阅它(与图片抠图页同一套)。</summary>
    public event Action<string>? StatusChanged;

    private readonly string[] _containers = AlhPro.Core.MattingOutputSpecs.TransparentContainers;
    private readonly List<string> _videos = new();
    private readonly List<string> _done = new();
    private readonly Dictionary<string, ListViewItem> _rows = new();
    private string _outDir = "";
    private string _bgImage = "";
    private CancellationTokenSource? _cts;

    /// <summary>XAML 是否已解析完。解析期 IsChecked="True" 就会触发 Checked,那时其它控件还没建出来
    /// (真机踩到过:整页加载失败,报 Failed to assign to property 'ToggleButton.IsChecked')。</summary>
    private bool _ready;

    public VideoMattingView()
    {
        InitializeComponent();

        foreach (var m in CutoutService.Models) ModelCombo.Items.Add(m.Label);
        ModelCombo.SelectedIndex = CutoutService.DefaultModelIndex;
        BitrateCombo.SelectedIndex = 0;
        MuteCheck.IsEnabled = false;   // 音频由输出方式决定(透明通道不带音频/换背景保留源音频),不让手动改
        MuteCheck.IsChecked = false;

        ApplyModelPresets();
        UpdateLabels();
        UpdateModeVisibility();
        UpdateBgVisibility();
        RefreshFormatOptions();
        RefreshEngineHint();
        _ready = true;
    }

    // ---------- 参数区 ----------

    private void ModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        ApplyModelPresets();
    }

    private void OutputMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        UpdateModeVisibility();
        RefreshFormatOptions();
    }

    private void FormatCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        RefreshCodecHint();
    }

    private void BgMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        UpdateBgVisibility();
    }

    private void Param_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_ready) return;
        UpdateLabels();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        ApplyModelPresets();          // 回到当前模型的预设档
        StabilitySlider.Value = 50;
        UpdateLabels();
        Status("已重置抠图参数");
    }

    private void ApplyModelPresets()
    {
        int i = Math.Clamp(ModelCombo.SelectedIndex, 0, CutoutService.Models.Length - 1);
        var m = CutoutService.Models[i];
        FgSlider.Value = m.FgPreset;
        BgSlider.Value = m.BgPreset;
        FeatherSlider.Value = m.FeatherPreset;
        MorphSlider.Value = m.MorphPreset;
        ModelHint.Text = m.InputSize >= 1024
            ? "输入 1024×1024:边缘更细、单帧更慢;本机 GPU 实测约 0.26 秒/帧,跑不动会自动改 CPU。"
            : "输入 320×320:最快,适合长视频;细节略弱于 ISNet。";
        UpdateLabels();
    }

    private void UpdateLabels()
    {
        FgVal.Text = FgSlider.Value.ToString("0");
        BgVal.Text = BgSlider.Value.ToString("0");
        FeatherVal.Text = FeatherSlider.Value.ToString("0");
        MorphVal.Text = MorphSlider.Value.ToString("0");
        StabilityVal.Text = StabilitySlider.Value.ToString("0");
    }

    private void UpdateModeVisibility()
    {
        bool transparent = ModeAlphaRadio.IsChecked == true;
        BgPanel.Visibility = transparent ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateBgVisibility()
    {
        bool image = BgImageRadio.IsChecked == true;
        BgColorRow.Visibility = image ? Visibility.Collapsed : Visibility.Visible;
        BgImageBtn.Visibility = image ? Visibility.Visible : Visibility.Collapsed;
        BgImageInfo.Visibility = image ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>格式下拉随输出方式切换:换背景只有 MP4;透明通道给 MOV/ProRes 4444 与 WebM/VP9-alpha。
    /// 选项直接来自 `MattingOutputSpecs.TransparentContainers`(规格表的第一个 = 默认档),不另写一份。</summary>
    private void RefreshFormatOptions()
    {
        bool transparent = ModeAlphaRadio.IsChecked == true;
        FormatCombo.Items.Clear();
        if (transparent)
        {
            foreach (var c in _containers)
                FormatCombo.Items.Add(c == "mov"
                    ? "MOV · ProRes 4444 (推荐,兼容性最好)"
                    : "WebM · VP9-alpha (体积小,部分软件不认透明)");
        }
        else
        {
            FormatCombo.Items.Add("MP4 (通用)");
        }
        FormatCombo.SelectedIndex = 0;
        RefreshCodecHint();
    }

    private void RefreshCodecHint()
    {
        bool transparent = ModeAlphaRadio.IsChecked == true;
        string fmt = FormatCombo.SelectedItem as string ?? "";
        CodecCombo.Items.Clear();
        CodecCombo.Items.Add(transparent
            ? (fmt.StartsWith("MOV") ? "ProRes 4444 (带 alpha)" : "VP9 (带 alpha)")
            : "跟随全局策略 (硬编优先)");
        CodecCombo.SelectedIndex = 0;
    }

    private void RefreshEngineHint()
    {
        try
        {
            bool ff = VideoService.FfmpegPath != null;
            bool model = EngineService.FindCutoutModel(CutoutService.GetModel(CutoutService.DefaultModelKey).FileName) != null;
            EngineHint.Text = $"组件: ffmpeg {(ff ? "✓" : "✗")} · 模型 {(model ? "✓" : "✗")}";
        }
        catch { EngineHint.Text = "组件: 自检失败(详见诊断日志)"; }
    }

    // ---------- 素材 / 输出 ----------

    private async void PickBtn_Click(object sender, RoutedEventArgs e)
    {
        AppLogger.UserAction("视频抠图:点击「添加视频」");
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        foreach (var ext in new[] { ".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v", ".wmv", ".ts" })
            picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var files = await picker.PickMultipleFilesAsync();
        if (files == null || files.Count == 0) return;
        foreach (var f in files)
            if (!_videos.Contains(f.Path)) _videos.Add(f.Path);
        RefreshList();
        Status($"视频抠图:已添加 {_videos.Count} 个视频");
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) return;
        _videos.Clear();
        _done.Clear();
        RefreshList();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) return;
        foreach (var item in TaskList.SelectedItems.Cast<ListViewItem>().ToList())
            if (item.Tag is string path) _videos.Remove(path);
        RefreshList();
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) return;
        foreach (var path in _done.ToList()) _videos.Remove(path);
        _done.Clear();
        RefreshList();
    }

    private async void OutDir_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;
        _outDir = folder.Path;
        OutDirBox.Text = _outDir;
    }

    private async void BgImage_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp" }) picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var file = await picker.PickSingleFileAsync();
        if (file == null) return;
        _bgImage = file.Path;
        BgImageInfo.Text = System.IO.Path.GetFileName(_bgImage);
    }

    // ---------- 任务列表 ----------

    private void RefreshList()
    {
        _rows.Clear();
        TaskList.Items.Clear();
        foreach (var v in _videos)
        {
            var item = new ListViewItem { Content = RowText(v, "待处理", -1), Tag = v };
            _rows[v] = item;
            TaskList.Items.Add(item);
        }
        EmptyHint.Visibility = _videos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TaskList.Visibility = _videos.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        TaskTitle.Text = _videos.Count == 0 ? "等待任务" : $"任务列表 ({_videos.Count})";
    }

    private static string RowText(string path, string state, int pct)
    {
        string name = System.IO.Path.GetFileName(path);
        return pct >= 0 ? $"{name}   ·   {state} {pct}%" : $"{name}   ·   {state}";
    }

    private void SetRow(string path, string state, int pct = -1)
    {
        if (_rows.TryGetValue(path, out var item)) item.Content = RowText(path, state, pct);
    }

    // ---------- 开始 / 取消 ----------

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_videos.Count == 0)
        {
            ProgressText.Text = "请先添加视频。";
            Status("视频抠图:请先添加视频");
            return;
        }
        if (_cts != null) return;

        _done.Clear();
        _cts = new CancellationTokenSource();
        SetRunning(true);
        var ct = _cts.Token;
        string current = "";
        var progress = new Progress<(int pct, string msg)>(p =>
        {
            TaskBar.Value = Math.Clamp(p.pct, 0, 100);
            ProgressText.Text = p.msg;
            if (current.Length > 0) SetRow(current, "处理中", p.pct);
            Status("视频抠图:" + p.msg);
        });

        try
        {
            foreach (var video in _videos.ToList())
            {
                ct.ThrowIfCancellationRequested();
                current = video;
                SetRow(video, "处理中", 0);
                TaskBar.Value = 0;
                var req = BuildRequest(video);
                var result = await Task.Run(() => VideoMattingService.RunAsync(req, progress, ct), ct);
                TaskBar.Value = 100;
                SetRow(video, $"完成 · {result.Frames} 帧 / {result.ElapsedSec:0.#} 秒 · {System.IO.Path.GetFileName(result.OutputPath)}");
                _done.Add(video);
                ProgressText.Text = "完成:" + result.OutputPath;
                Status($"视频抠图完成:{System.IO.Path.GetFileName(result.OutputPath)} · {result.Device} · {result.ElapsedSec:0.#} 秒");
                AppLogger.Info($"视频抠图完成:{result.OutputPath} 帧数={result.Frames} 设备={result.Device} 备注={result.Notes}");
            }
        }
        catch (OperationCanceledException)
        {
            if (current.Length > 0) SetRow(current, "已取消");
            ProgressText.Text = "已取消(临时文件已清理)。";
            Status("视频抠图已取消");
        }
        catch (Exception ex)
        {
            if (current.Length > 0) SetRow(current, "失败");
            AppLogger.Error($"视频抠图失败 HRESULT=0x{ex.HResult:X8}", ex);
            ProgressText.Text = "处理失败:" + ex.Message;
            Status("视频抠图失败:" + ex.Message);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetRunning(false);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        try { _cts?.Cancel(); } catch { }
        ProgressText.Text = "已请求取消,等当前帧处理完就停...";
        Status("视频抠图:正在取消...");
    }

    private void SetRunning(bool running)
    {
        StartBtn.IsEnabled = !running;
        CancelBtn.IsEnabled = running;
        PickBtn.IsEnabled = !running;
        RemoveBtn.IsEnabled = !running;
        ClearBtn.IsEnabled = !running;
        DoneBtn.IsEnabled = !running;
        OutDirBtn.IsEnabled = !running;
        ModeBgRadio.IsEnabled = !running;
        ModeAlphaRadio.IsEnabled = !running;
        FormatCombo.IsEnabled = !running;
    }

    private void Status(string text)
    {
        try { StatusChanged?.Invoke(text); } catch { }
    }

    private VideoMattingRequest BuildRequest(string video) => new(
        Input: video,
        OutDir: _outDir,
        ModelKey: CutoutService.Models[Math.Clamp(ModelCombo.SelectedIndex, 0, CutoutService.Models.Length - 1)].Key,
        GpuId: AppSettings.GpuIndex,
        Transparent: ModeAlphaRadio.IsChecked == true,
        Container: ModeAlphaRadio.IsChecked == true
            ? _containers[Math.Clamp(FormatCombo.SelectedIndex, 0, _containers.Length - 1)]
            : "mp4",
        BackgroundPath: BgImageRadio.IsChecked == true ? _bgImage : "",
        BackgroundColor: BgColorBox.Text,
        Fg: (int)FgSlider.Value,
        Bg: (int)BgSlider.Value,
        Feather: (int)FeatherSlider.Value,
        Edge: 0,
        Morph: (int)MorphSlider.Value,
        Stability: (int)StabilitySlider.Value);
}
