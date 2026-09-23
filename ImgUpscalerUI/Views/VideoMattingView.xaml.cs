using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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
    /// <summary>上一条已写进日志的进度文案(避免同一帧被重复上报两次时写两遍)。</summary>
    private string _lastLogged = "";

    /// <summary>【2026-09-23】最后一个**有效**的背景色(`#RRGGBB`)。
    /// 输入框里打错字时,归一化会退回它 —— 关键点:**绝不退化成黑色**
    /// (下游 `VideoMattingService.ParseColor` 只认 6 位十六进制,不归一化就会静默变黑,这正是要治的病)。</summary>
    private string _lastValidBgColor = "#1E3A5F";

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
        SetBgColor(BgColorBox.Text);   // 【2026-09-23】初始化右侧"当前色块",让它一开始就等于文本框里的颜色
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

    /// <summary>常用色块(白/黑/绿):只改颜色值,与调色板/文本框同一个入口。</summary>
    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string hex) SetBgColor(hex);
    }

    /// <summary>调色板选色 → 写回同一个十六进制文本框(另一条路改它也一样)。</summary>
    private void BgColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (!_ready) return;
        SetBgColor(AlhPro.Core.HexColor.Format(args.NewColor.R, args.NewColor.G, args.NewColor.B));
    }

    /// <summary>【唯一写入口】把背景色写进文本框 + 同步右侧色块。三个入口(色块/调色板/输入框归一化)都走这里,
    /// 所以"文本框里的值 = 色块显示的颜色 = 真正下发给处理端的值"永远一致。</summary>
    private void SetBgColor(string hex)
    {
        var norm = AlhPro.Core.HexColor.Normalize(hex);
        if (norm == null) return;                      // 非法值只可能来自归一化已判定过的路径,这里直接忽略
        _lastValidBgColor = norm;
        try
        {
            BgColorBox.Text = norm;
            if (SwatchCurrentFill != null)
            {
                AlhPro.Core.HexColor.TryParseRgb(norm, out var r, out var g, out var b);
                SwatchCurrentFill.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));
            }
            if (BgColorHint != null) BgColorHint.Visibility = Visibility.Collapsed;   // 设了有效值就把上次的警告收起来
        }
        catch { }
    }

    /// <summary>焦点离开输入框 / 按回车 ⇒ 归一化。见 <see cref="AlhPro.Core.HexColor"/> 的说明:
    /// 用户很自然会打 `fff`、`#F0A`、`0x1E3A5F`,而下游只认恰好 6 位 ⇒ 不归一化就**静默变黑**。
    /// 归一化不了(长度不对/有非法字符/带了注释)时**明确提示并按上一个有效值回退** ——
    /// 绝不把"打错了"变成"颜色悄悄变了"。</summary>
    private void NormalizeBgColorText()
    {
        if (!_ready) return;
        var raw = BgColorBox.Text;
        var norm = AlhPro.Core.HexColor.Normalize(raw);
        if (norm != null)
        {
            SetBgColor(norm);   // 写回规范写法(#RRGGBB 大写)—— 文本框里永远只有一种写法,肉眼可比对
            return;
        }
        SetBgColor(_lastValidBgColor);
        try
        {
            if (BgColorHint != null)
            {
                BgColorHint.Text = $"「{raw}」不是有效颜色(要 6 位十六进制,如 #1E3A5F;简写 #RGB 也行)"
                                 + $" ⇒ 已退回上一个有效值 {_lastValidBgColor}";
                BgColorHint.Visibility = Visibility.Visible;
            }
            Status(BgColorHint?.Text ?? "");
            AppLogger.Info($"[视频抠图] 背景色输入无效:{raw} ⇒ 退回 {_lastValidBgColor}");
        }
        catch { }
    }

    private void BgColorBox_LostFocus(object sender, RoutedEventArgs e) => NormalizeBgColorText();

    private void BgColorBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        NormalizeBgColorText();
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

    /// <summary>键盘删除(照视频页 VideoList_KeyDown 的惯例):Delete / Backspace 删掉选中项,
    /// 与「删除选中」按钮同一条逻辑(处理中不解锁)。</summary>
    private void TaskList_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (_cts != null) return;
        if (e.Key is not (Windows.System.VirtualKey.Delete or Windows.System.VirtualKey.Back)) return;
        foreach (var item in TaskList.SelectedItems.Cast<MattingItem>().ToList()) _items.Remove(item);
        RefreshEmptyState();
        e.Handled = true;
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
        TaskTitle.Text = empty ? "等待任务" : $"已处理 {_done.Count} / 剩余 {_items.Count - _done.Count}";
        UpdateButtons();
    }

    // ---------- 开始 / 取消 ----------

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0)
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
        MattingItem? current = null;
        var progress = new Progress<(int pct, string msg)>(p =>
        {
            TaskBar.Value = Math.Clamp(p.pct, 0, 100);
            ProgressText.Text = p.msg;
            if (p.msg != _lastLogged) { _lastLogged = p.msg; Log(p.msg); }   // 逐帧记录(同一帧的重复上报不重复写)
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
                ProgressText.Text = "完成:" + result.OutputPath;
                Status($"视频抠图完成:{Path.GetFileName(result.OutputPath)} · {result.Device} · {result.ElapsedSec:0.#} 秒");
                Log($"完成:{Path.GetFileName(result.OutputPath)}({result.Frames} 帧 / {result.ElapsedSec:0.#} 秒 / {result.Device})");
                AppLogger.Info($"视频抠图完成:{result.OutputPath} 帧数={result.Frames} 设备={result.Device} 备注={result.Notes}");
            }
        }
        catch (OperationCanceledException)
        {
            if (current != null) { current.StateText = "已取消"; current.StateBadgeBrush = "#8A5A12"; current.Progress = 0; }
            ProgressText.Text = "已取消(临时文件已清理)。";
            Status("视频抠图已取消");
        }
        catch (Exception ex)
        {
            if (current != null) { current.StateText = "失败"; current.StateBadgeBrush = "#B3261E"; current.Progress = 0; }
            AppLogger.Error($"视频抠图失败 HRESULT=0x{ex.HResult:X8}", ex);
            Log("失败:" + ex.Message);
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

    private void SetRunning(bool running) => UpdateButtons();

    // ==================== 框选(橡皮筋多选) ====================
    // 【2026-09-23 补的欠账】照抄 `VideoView` 的 `VideoGridHost` 那四个指针处理器(它与本页此前是
    // "最后一个真差异":视频页能框选、抠图页不能)。三条纪律与原版逐字一致:
    //   ① 按在**列表项**上不启动框选 ⇒ 交回 ListView 做"点击选中/拖拽排序"(否则拖动排序会被吃掉);
    //   ② 位移小于 4px 算单击 ⇒ 清空选中(点空白处取消选择,与 Windows 资源管理器一致);
    //   ③ 必须 CapturePointer:指针拖出宿主后仍要收到 Moved/Released,否则橡皮筋会"卡住不消失"。
    private const double RbThresholdM = 4;
    private bool _rbBandingM;
    private bool _rbMovedM;
    private Windows.Foundation.Point _rbStartM;

    private void TaskGridHost_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (IsPressOnTaskItem(e.GetCurrentPoint(TaskGridHost).Position)) return;
        _rbBandingM = true;
        _rbMovedM = false;
        _rbStartM = e.GetCurrentPoint(TaskGridHost).Position;
        RbRectM.Visibility = Visibility.Visible;
        RbRectM.Width = 0;
        RbRectM.Height = 0;
        Canvas.SetLeft(RbRectM, _rbStartM.X);
        Canvas.SetTop(RbRectM, _rbStartM.Y);
        TaskGridHost.CapturePointer(e.Pointer);
    }

    /// <summary>按下位置是否落在某个任务行上(落在行上就交给 ListView,避免与框选打架)。</summary>
    private bool IsPressOnTaskItem(Windows.Foundation.Point pt)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            if (TaskList.ContainerFromIndex(i) is FrameworkElement c && c.ActualWidth > 0)
            {
                var tl = c.TransformToVisual(TaskGridHost).TransformPoint(new Windows.Foundation.Point(0, 0));
                var r = new Windows.Foundation.Rect(tl.X, tl.Y, c.ActualWidth, c.ActualHeight);
                if (r.Contains(pt)) return true;
            }
        }
        return false;
    }

    private void TaskGridHost_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_rbBandingM) return;
        var cur = e.GetCurrentPoint(TaskGridHost).Position;
        if (!_rbMovedM && Math.Abs(cur.X - _rbStartM.X) < RbThresholdM && Math.Abs(cur.Y - _rbStartM.Y) < RbThresholdM)
            return;
        _rbMovedM = true;
        UpdateRbRectM(cur);
    }

    private void TaskGridHost_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_rbBandingM) return;
        _rbBandingM = false;
        try { TaskGridHost.ReleasePointerCapture(e.Pointer); } catch { }
        if (_rbMovedM)
        {
            UpdateRbRectM(e.GetCurrentPoint(TaskGridHost).Position);
            ApplyRubberSelectionM();
        }
        else
        {
            TaskList.SelectedItems.Clear();   // 单击空白 = 取消选中
            UpdateButtons();
        }
        RbRectM.Visibility = Visibility.Collapsed;
    }

    private void TaskGridHost_PointerCaptureLost(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _rbBandingM = false;
        try { RbRectM.Visibility = Visibility.Collapsed; } catch { }
    }

    private void UpdateRbRectM(Windows.Foundation.Point cur)
    {
        double x = Math.Min(_rbStartM.X, cur.X);
        double y = Math.Min(_rbStartM.Y, cur.Y);
        Canvas.SetLeft(RbRectM, x);
        Canvas.SetTop(RbRectM, y);
        RbRectM.Width = Math.Abs(cur.X - _rbStartM.X);
        RbRectM.Height = Math.Abs(cur.Y - _rbStartM.Y);
        RbRectM.Visibility = RbRectM.Width > 2 && RbRectM.Height > 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>橡皮筋与行相交即选中(相交判据,不是"完全包含"—— 与视频页一致)。</summary>
    private void ApplyRubberSelectionM()
    {
        var rect = new Windows.Foundation.Rect(Canvas.GetLeft(RbRectM), Canvas.GetTop(RbRectM),
            RbRectM.Width, RbRectM.Height);
        if (rect.Width < 2 || rect.Height < 2) return;
        TaskList.SelectedItems.Clear();
        for (int i = 0; i < _items.Count; i++)
        {
            if (TaskList.ContainerFromIndex(i) is FrameworkElement c)
            {
                var topLeft = c.TransformToVisual(TaskGridHost).TransformPoint(new Windows.Foundation.Point(0, 0));
                var itemRect = new Windows.Foundation.Rect(topLeft.X, topLeft.Y, c.ActualWidth, c.ActualHeight);
                if (RectIntersectsM(itemRect, rect)) TaskList.SelectedItems.Add(_items[i]);
            }
        }
        UpdateButtons();   // 框选完要让「删除选中」亮起来(否则框了一堆还是灰的,像没选中)
    }

    private static bool RectIntersectsM(Windows.Foundation.Rect a, Windows.Foundation.Rect b)
        => a.X < b.X + b.Width && a.X + a.Width > b.X
            && a.Y < b.Y + b.Height && a.Y + a.Height > b.Y;

    private void TaskList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    /// <summary>按钮灰的规则(照视频处理页):没选中就不给点「删除选中」,列表空不给点「清空」,
    /// 没有已完成项不给点「清除已完成」;运行时除「取消」外全部禁用。</summary>
    private void UpdateButtons()
    {
        bool running = _cts != null;
        StartBtn.IsEnabled = !running;
        CancelBtn.IsEnabled = running;
        PickBtn.IsEnabled = !running;
        RemoveBtn.IsEnabled = !running && TaskList.SelectedItems.Count > 0;
        ClearBtn.IsEnabled = !running && _items.Count > 0;
        DoneBtn.IsEnabled = !running && _done.Count > 0;
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
            var text = VideoLogText.Text;
            if (text == "日志:等待任务...") text = "";
            text = text.Length == 0 ? line : text + "\n" + line;
            // 【只留最近 500 行】逐帧记录意味着长视频会写几千行,TextBox 全留着会越写越卡;
            // 日志的价值在"最近发生了什么",所以从头部裁掉(与视频页滚动框的观感一致)。
            var lines = text.Split('\n');
            if (lines.Length > 500) text = string.Join("\n", lines, lines.Length - 500, 500);
            VideoLogText.Text = text;
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
        // 【2026-09-23】背景色一律**归一化后**再下发:用户可能还停在输入框里没失焦就点了「开始处理」,
        // 那时文本框里可能是 `fff` 这种简写 —— 直接下发会被下游判为非法并**静默变黑** ✗。
        BackgroundColor: AlhPro.Core.HexColor.NormalizeOr(BgColorBox.Text, _lastValidBgColor),
        Fg: (int)FgSlider.Value,
        Bg: (int)BgSlider.Value,
        Feather: (int)FeatherSlider.Value,
        Edge: 0,
        Morph: (int)MorphSlider.Value,
        Stability: (int)StabilitySlider.Value);
}
