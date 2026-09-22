using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;

namespace ALHPro.Views;

/// <summary>视频抠图任务行(照 VideoView 的 VideoItem 精简版):缩略图 + 名称 + 状态角标 + 信息行 + 进度。
/// 【为什么不用 ListViewItem 塞字符串】视频处理页的行是富模板(120×68 缩略图 + 多行信息),
/// 用户明确要求"右侧一模一样",所以必须走 ItemTemplate + INotifyPropertyChanged 数据绑定。</summary>
public sealed class MattingItem : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; init; } = "";
    public string Name { get; init; } = "";

    private BitmapImage? _thumb;
    public BitmapImage? Thumb
    {
        get => _thumb;
        set { _thumb = value; Raise(nameof(Thumb)); }
    }

    private string _info = "待处理";
    /// <summary>第二行:分辨率/帧率等基础信息 + 当前状态文案。</summary>
    public string Info { get => _info; set { _info = value; Raise(nameof(Info)); } }

    private string _stateText = "待处理";
    public string StateText { get => _stateText; set { _stateText = value; Raise(nameof(StateText)); } }

    private string _stateBadgeBrush = "#5A6270";
    /// <summary>角标底色:待处理=灰、处理中=蓝、完成=绿、失败=红、取消=橙(与视频页角标同款圆角小块)。</summary>
    public string StateBadgeBrush { get => _stateBadgeBrush; set { _stateBadgeBrush = value; Raise(nameof(StateBadgeBrush)); } }

    private bool _showBadge = true;
    public bool ShowBadge { get => _showBadge; set { _showBadge = value; Raise(nameof(StateBadgeVisibility)); } }
    public Visibility StateBadgeVisibility => ShowBadge ? Visibility.Visible : Visibility.Collapsed;

    private string _outputInfo = "";
    /// <summary>第三行:成品路径/结果(蓝色,与视频页的 OutputInfo 同款)。</summary>
    public string OutputInfo
    {
        get => _outputInfo;
        set { _outputInfo = value; Raise(nameof(OutputInfo)); Raise(nameof(OutputInfoVisibility)); }
    }
    public Visibility OutputInfoVisibility => string.IsNullOrEmpty(_outputInfo) ? Visibility.Collapsed : Visibility.Visible;

    private double _progress;
    public double Progress { get => _progress; set { _progress = value; Raise(nameof(Progress)); Raise(nameof(ProgressVisibility)); } }
    public Visibility ProgressVisibility => _progress > 0 && _progress < 100 ? Visibility.Visible : Visibility.Collapsed;

    private double _doneItemOpacity = 1;
    /// <summary>已完成/已失败的行整体降透明度(与视频页"已完成"行的视觉一致)。</summary>
    public double DoneItemOpacity { get => _doneItemOpacity; set { _doneItemOpacity = value; Raise(nameof(DoneItemOpacity)); } }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}

/// <summary>视频抠图页(独立板块)。接线到 `VideoMattingService`。
///
/// 【为什么独立成页】用户明确要求:"要的是单独一个界面 视频抠图,原先的还是图片抠图"。
/// 【布局与右侧任务板照「视频处理」页】用户给了对照图并要求"改 要一模一样":
///   右侧 = 「等待任务」+ 工具栏(添加视频/删除选中/清空/清除已完成)+ 右侧提示 + 空态(拖入项目/组件自检);
///   任务行 = ItemTemplate 富模板(120×68 缩略图 + 名称 + 状态角标 + 信息行 + 进度条),缩略图用 ffmpeg 抽帧;
///   左栏 = 滑条行式(78/*/28)+ 重置调整;输出 = 路径框 + 选择输出位置 + 码率/格式组,都在「开始处理」上面。
/// 进度/结果同时走底部状态栏(StatusChanged),页面里不放日志框。
/// </summary>
public sealed partial class VideoMattingView : UserControl
{
    /// <summary>状态文本(底部状态栏)。MainPage 订阅它(与图片抠图页同一套)。</summary>
    public event Action<string>? StatusChanged;

    private readonly string[] _containers = AlhPro.Core.MattingOutputSpecs.TransparentContainers;
    private readonly ObservableCollection<MattingItem> _items = new();
    private readonly List<string> _done = new();
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
        MuteCheck.IsEnabled = false;   // 音频由输出方式决定,不让手动改
        MuteCheck.IsChecked = false;

        TaskList.ItemsSource = _items;

        ApplyModelPresets();
        UpdateLabels();
        UpdateModeVisibility();
        UpdateBgVisibility();
        RefreshFormatOptions();
        RefreshEngineHint();
        RefreshEmptyState();
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
        ApplyModelPresets();
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

    /// <summary>格式下拉随输出方式切换(选项取自规格表,不另写一份)。</summary>
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
        AddVideos(files.Select(f => f.Path));
    }

    // ---------- 拖放(照 VideoView 的 DropBorder_*,两个坑一并继承) ----------
    // ① e.Handled = true:不标记会被外层容器再处理一次 ⇒ 拖入一次添加两次;
    // ② 必须 await GetStorageItemsAsync():UI 线程同步等(Result)会死锁,表现为"拖不进去"。

    private void DropArea_DragOver(object sender, DragEventArgs e)
        => e.AcceptedOperation = DataPackageOperation.Copy;

    private async void DropArea_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_cts != null) { Status("视频抠图:处理中,暂不接受新拖入的文件"); return; }
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        var ext = new[] { ".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v", ".wmv", ".ts" };
        var files = items.OfType<Windows.Storage.StorageFile>()
            .Where(f => ext.Any(x => f.Path.EndsWith(x, StringComparison.OrdinalIgnoreCase)))
            .Select(f => f.Path).ToArray();
        if (files.Length == 0)
        {
            Status("视频抠图:拖入的文件不是支持的视频格式(mp4/mov/mkv/avi/webm/m4v/wmv/ts)");
            return;
        }
        AddVideos(files);
    }

    /// <summary>加入任务列表(去重),并后台生成缩略图。</summary>
    private void AddVideos(IEnumerable<string> paths)
    {
        int added = 0;
        foreach (var p in paths)
        {
            if (_items.Any(i => string.Equals(i.Path, p, StringComparison.OrdinalIgnoreCase))) continue;
            var item = new MattingItem { Path = p, Name = Path.GetFileName(p) };
            item.Info = ProbeInfo(p);
            _items.Add(item);
            added++;
            _ = GenerateThumbAsync(item);
        }
        RefreshEmptyState();
        Log($"+{added} 个视频(共 {_items.Count} 个)");
        Status(added > 0 ? $"视频抠图:已添加 {added} 个视频(共 {_items.Count} 个)" : "视频抠图:这些视频已经在列表里了");
    }

    /// <summary>行内第二行的基础信息(分辨率/帧率)。用已有的探测接口,不在这里现跑 ffprobe。</summary>
    private static string ProbeInfo(string path)
    {
        try
        {
            string fps = VideoService.ProbeFps(path) ?? "?";
            return $"帧率 {fps} · 待处理";
        }
        catch { return "待处理"; }
    }

    /// <summary>缩略图(照 VideoView.GenerateThumbAsync):ffmpeg 抽第 0.5 秒一帧到临时目录,
    /// 读成 BitmapImage 后删临时文件;失败静默(没有缩略图也要能用)。</summary>
    private async Task GenerateThumbAsync(MattingItem item)
    {
        try
        {
            var ffmpeg = VideoService.FfmpegPath;
            if (ffmpeg == null) return;
            var tmp = Path.Combine(EngineService.TempRoot, $"matting_thumb_{Guid.NewGuid():N}.jpg");
            await Task.Run(() =>
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = $"-y -ss 0.5 -i \"{ALHPro.AudioService.FfmpegSafePath(item.Path)}\" -frames:v 1 -vf \"scale=240:-2\" -q:v 3 \"{ALHPro.AudioService.FfmpegSafePath(tmp)}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                };
                using var p = System.Diagnostics.Process.Start(psi);
                if (p == null) return;
                _ = p.StandardError.ReadToEndAsync();
                p.WaitForExit();
            });
            if (!File.Exists(tmp) || new FileInfo(tmp).Length == 0) return;
            var bmp = new BitmapImage();
            using (var fs = new FileStream(tmp, FileMode.Open, FileAccess.Read))
                await bmp.SetSourceAsync(fs.AsRandomAccessStream());
            try { File.Delete(tmp); } catch { }
            item.Thumb = bmp;
        }
        catch { }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) return;
        _items.Clear();
        _done.Clear();
        RefreshEmptyState();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) return;
        foreach (var item in TaskList.SelectedItems.Cast<MattingItem>().ToList()) _items.Remove(item);
        RefreshEmptyState();
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) return;
        foreach (var p in _done.ToList())
        {
            var it = _items.FirstOrDefault(i => i.Path == p);
            if (it != null) _items.Remove(it);
        }
        _done.Clear();
        RefreshEmptyState();
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
        BgImageInfo.Text = Path.GetFileName(_bgImage);
    }

    private void RefreshEmptyState()
    {
        bool empty = _items.Count == 0;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        TaskList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        TaskTitle.Text = empty ? "等待任务" : $"任务列表 ({_items.Count})";
    }

    // ---------- 开始 / 取消 ----------

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0)
        {
            TaskTitle.Text = "请先添加视频。";
            Status("视频抠图:请先添加视频");
            return;
        }
        if (_cts != null) return;

        _done.Clear();
        _cts = new CancellationTokenSource();
        SetRunning(true);
        var ct = _cts.Token;
        MattingItem? current = null;
        var progress = new Progress<(int pct, string msg)>(p =>
        {
            TaskBar.Value = Math.Clamp(p.pct, 0, 100);
            TaskTitle.Text = p.msg;
            if (current != null)
            {
                current.Progress = p.pct;
                current.Info = $"{ProbeInfo(current.Path).Replace(" · 待处理", "")} · 处理中 {p.pct}%";
            }
            Status("视频抠图:" + p.msg);
        });

        try
        {
            foreach (var item in _items.ToList())
            {
                ct.ThrowIfCancellationRequested();
                current = item;
                item.StateText = "处理中"; item.StateBadgeBrush = "#2A6FD6";
                item.Progress = 1;
                TaskBar.Value = 0;
                var req = BuildRequest(item.Path);
                var result = await Task.Run(() => VideoMattingService.RunAsync(req, progress, ct), ct);
                TaskBar.Value = 100;
                item.Progress = 100;
                item.StateText = "完成"; item.StateBadgeBrush = "#2E7D32";
                item.DoneItemOpacity = 0.65;
                item.Info = $"{result.Frames} 帧 · {result.ElapsedSec:0.#} 秒 · {result.Device}";
                item.OutputInfo = Path.GetFileName(result.OutputPath);
                _done.Add(item.Path);
                TaskTitle.Text = "完成:" + result.OutputPath;
                Status($"视频抠图完成:{Path.GetFileName(result.OutputPath)} · {result.Device} · {result.ElapsedSec:0.#} 秒");
                Log($"完成:{Path.GetFileName(result.OutputPath)}({result.Frames} 帧 / {result.ElapsedSec:0.#} 秒 / {result.Device})");
                AppLogger.Info($"视频抠图完成:{result.OutputPath} 帧数={result.Frames} 设备={result.Device} 备注={result.Notes}");
            }
        }
        catch (OperationCanceledException)
        {
            if (current != null) { current.StateText = "已取消"; current.StateBadgeBrush = "#8A5A12"; current.Progress = 0; }
            TaskTitle.Text = "已取消(临时文件已清理)。";
            Status("视频抠图已取消");
        }
        catch (Exception ex)
        {
            if (current != null) { current.StateText = "失败"; current.StateBadgeBrush = "#B3261E"; current.Progress = 0; }
            AppLogger.Error($"视频抠图失败 HRESULT=0x{ex.HResult:X8}", ex);
            Log("失败:" + ex.Message);
            TaskTitle.Text = "处理失败:" + ex.Message;
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
        TaskTitle.Text = "已请求取消,等当前帧处理完就停...";
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

    /// <summary>左栏底部日志(照视频处理页:小框 + 常驻滚动条)。</summary>
    private void Log(string line)
    {
        try
        {
            VideoLogText.Text += (VideoLogText.Text.Length == 0 || VideoLogText.Text == "日志:等待任务..." ? "" : "\n") + line;
            VideoLogScroll.ChangeView(null, VideoLogScroll.ScrollableHeight, null, true);
        }
        catch { }
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
