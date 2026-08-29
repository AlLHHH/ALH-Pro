using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;
using Ellipse = Microsoft.UI.Xaml.Shapes.Ellipse;
using SolidColorBrush = Microsoft.UI.Xaml.Media.SolidColorBrush;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace ALHPro.Views;

public sealed partial class CutoutView : UserControl
{
    private bool _running;
    private string? _customOutDir;
    private CancellationTokenSource? _cts;
    private int _gpuCount;

    // ---- 暂停/恢复:暂停后停在下一张之前,可删除"未处理"的项目 ----
    private bool _paused;
    private TaskCompletionSource<bool>? _resumeTcs;
    private ImageItem[]? _runItems;   // 本次任务的快照(删除列表项不影响遍历)

    // 更新顶部文件信息提示(添加/清空图片后)
    private void UpdateFileInfo()
    {
        var n = ToolGrid.Items.Count;
        FileInfo.Text = n == 0 ? "未添加图片"
            : n == 1 ? $"{ToolGrid.Items[0].Name} · 1 张"
            : $"{n} 张图片";
    }

    public CutoutView()
    {
        this.InitializeComponent();
        ToolGrid.Items.CollectionChanged += (_, _) =>
        {
            UpdateRunState();
            UpdateFileInfo();
            // 暂停中删除未处理项目 → 进度条立即按剩余数量更新
            if (_running && _paused && _runItems != null)
                RefreshProgressBar(_runItems, "已暂停 · 可删除未处理的项目");
        };
        ToolGrid.ItemDoubleTapped += ToolGrid_ItemDoubleTapped;
        // 计算设备:统一在「设置」里选择(AppSettings.GpuIndex),页面不再显示下拉
        _gpuCount = GpuInfo.GetAdapterNames().Count;
        // 参数默认值(在 InitializeComponent 之后设置,避免 XAML 解析期事件)
        FgSlider.Value = 128;
        BgSlider.Value = 64;
        FeatherSlider.Value = 0;
        EdgeSlider.Value = 0;
        LoadSettings();
        UpdateOptions();
        UpdateRunState();
        // 「抠图前降噪」未勾选 → 降噪强度下拉与标签禁用并置灰(与超分页照片模式联动一致)
        void SetPreDenoiseLevelEnabled(bool on)
        {
            PreDenoiseLevelCombo.IsEnabled = on;
            PreDenoiseLevelCombo.Opacity = on ? 1.0 : 0.5;
            if (PreDenoiseLevelLabel != null)
                PreDenoiseLevelLabel.Opacity = on ? 0.7 : 0.35;
        }
        PreDenoiseCheck.Checked += (_, _) => SetPreDenoiseLevelEnabled(true);
        PreDenoiseCheck.Unchecked += (_, _) => SetPreDenoiseLevelEnabled(false);
        SetPreDenoiseLevelEnabled(PreDenoiseCheck.IsChecked == true);
    }

    // 参数滑条变化 → 刷新数值显示;记住开启时同步保存;蒙版预览显示中则自动刷新
    private void Params_Changed(object sender, RoutedEventArgs e)
        => OnParamsChanged();

    // 事件参数类型各异,XBF 反射连接要求精确签名,分别提供专用处理器:
    private void ParamsSlider_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        => OnParamsChanged();

    private void ParamsCombo_Changed(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
        => OnParamsChanged();

    private void OnParamsChanged()
    {
        if (FgVal == null) return;   // XAML 解析期事件保护
        UpdateOptions();
        if (RememberCheck != null && RememberCheck.IsChecked == true)
            SaveSettings();
        RefreshMaskDelayed();   // 调整参数 → AI 主体预览自动更新
    }

    private void UpdateOptions()
    {
        if (FgVal == null) return;   // XAML 解析期事件保护
        if (FgSlider != null) FgVal.Text = ((int)FgSlider.Value).ToString();
        if (BgSlider != null && BgVal != null) BgVal.Text = ((int)BgSlider.Value).ToString();
        if (FeatherSlider != null && FeatherVal != null) FeatherVal.Text = ((int)FeatherSlider.Value).ToString();
        if (EdgeSlider != null && EdgeVal != null) EdgeVal.Text = ((int)EdgeSlider.Value).ToString();
        if (MorphSlider != null && MorphVal != null) MorphVal.Text = ((int)MorphSlider.Value).ToString();
        // 自适应阈值勾选时:两个阈值滑条自动置灰失效(自动定界接管)
        bool autoThr = AutoThresholdCheck != null && AutoThresholdCheck.IsChecked == true;
        if (FgSlider != null) { FgSlider.IsEnabled = !autoThr; FgSlider.Opacity = autoThr ? 0.5 : 1.0; }
        if (BgSlider != null) { BgSlider.IsEnabled = !autoThr; BgSlider.Opacity = autoThr ? 0.5 : 1.0; }
        if (FgVal != null) FgVal.Opacity = autoThr ? 0.35 : 0.7;
        if (BgVal != null) BgVal.Opacity = autoThr ? 0.35 : 0.7;
    }

    // 当前计算设备:-1 = CPU(下拉最后一项)
    /// <summary>当前计算设备(全局设置):-1=CPU,≥0=GPU 编号(超出枚举数按 CPU 处理)。</summary>
    private int CurrentGpuId
        => AppSettings.GpuIndex >= 0 && AppSettings.GpuIndex < _gpuCount ? AppSettings.GpuIndex : -1;

    // 抠图推理设备:流畅模式(AppSettings.CutoutCpuOnly,默认开)用 CPU 软算——
    // DirectML GPU 推理会占满 GPU,而 WinUI 界面合成也用同一张 GPU → 窗口抖(卡)。
    // CPU 软算不碰 GPU,界面全程流畅,代价是慢(用户接受延长时间;且同图蒙版缓存后调参即时)。
    private int CutoutGpuId => AppSettings.CutoutCpuOnly ? -1 : CurrentGpuId;

    // 重置为当前模型的最优参数预设
    private void ResetBtn_Click(object sender, RoutedEventArgs e)
    {
        var m = CutoutService.Models[Math.Clamp(ModelCombo.SelectedIndex, 0, CutoutService.Models.Length - 1)];
        FgSlider.Value = m.FgPreset;
        BgSlider.Value = m.BgPreset;
        FeatherSlider.Value = m.FeatherPreset;
        EdgeSlider.Value = m.EdgePreset;
        MorphSlider.Value = m.MorphPreset;
        UpdateOptions();
        SaveSettings();
        Log($"已重置参数:模型={m.Label},前景={m.FgPreset},背景={m.BgPreset},羽化={m.FeatherPreset},增强={m.EdgePreset},边缘清理={m.MorphPreset}");
    }

    // 各滑条单独重置
    private void ResetFgBtn_Click(object sender, RoutedEventArgs e)
    {
        var m = CutoutService.Models[Math.Clamp(ModelCombo.SelectedIndex, 0, CutoutService.Models.Length - 1)];
        FgSlider.Value = m.FgPreset;
        UpdateOptions();
        SaveSettings();
    }

    private void ResetBgBtn_Click(object sender, RoutedEventArgs e)
    {
        var m = CutoutService.Models[Math.Clamp(ModelCombo.SelectedIndex, 0, CutoutService.Models.Length - 1)];
        BgSlider.Value = m.BgPreset;
        UpdateOptions();
        SaveSettings();
    }

    private void ResetFeatherBtn_Click(object sender, RoutedEventArgs e)
    {
        FeatherSlider.Value = 0;
        UpdateOptions();
        SaveSettings();
    }

    private void ResetEdgeBtn_Click(object sender, RoutedEventArgs e)
    {
        EdgeSlider.Value = 0;
        UpdateOptions();
        SaveSettings();
    }

    private void ResetMorphBtn_Click(object sender, RoutedEventArgs e)
    {
        var m = CutoutService.Models[Math.Clamp(ModelCombo.SelectedIndex, 0, CutoutService.Models.Length - 1)];
        MorphSlider.Value = m.MorphPreset;
        UpdateOptions();
        SaveSettings();
    }

    private void Remember_Changed(object sender, RoutedEventArgs e) => SaveSettings();

    // ---------- 参数记忆 ----------
    private sealed class CutoutSettings
    {
        public bool Remember { get; set; }
        public int Model { get; set; }
        public int Gpu { get; set; }
        public int Fg { get; set; }
        public int Bg { get; set; }
        public int Feather { get; set; }
        public int Edge { get; set; }
        public int Morph { get; set; }
        public bool AutoThreshold { get; set; }
        public bool PreDenoise { get; set; }
        public int PreDenoiseLevel { get; set; } = 1;
        public bool PreUpscale { get; set; }
        public int Tolerance { get; set; } = 20;
        public int Spread { get; set; } = 180;
    }

    private static string SettingsFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ALHPro", "cutout-settings.json");

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsFile)) return;
            var d = System.Text.Json.JsonSerializer.Deserialize<CutoutSettings>(File.ReadAllText(SettingsFile));
            if (d is null) return;
            RememberCheck.IsChecked = d.Remember;
            if (!d.Remember) return;
            if (d.Model is >= 0 && d.Model < ModelCombo.Items.Count) ModelCombo.SelectedIndex = d.Model;
            // 计算设备已在全局设置(AppSettings),页面不再恢复旧 Gpu 值
            if (d.Fg is >= 0 and <= 255) FgSlider.Value = d.Fg;
            if (d.Bg is >= 0 and <= 255) BgSlider.Value = d.Bg;
            if (d.Feather is >= 0 and <= 20) FeatherSlider.Value = d.Feather;
            if (d.Edge is >= 0 and <= 100) EdgeSlider.Value = d.Edge;
            if (d.Morph is >= 0 and <= 100) MorphSlider.Value = d.Morph;
            AutoThresholdCheck.IsChecked = d.AutoThreshold;
            PreDenoiseCheck.IsChecked = d.PreDenoise;
            if (d.PreDenoiseLevel is >= 0 and <= 2) PreDenoiseLevelCombo.SelectedIndex = d.PreDenoiseLevel;
            PreUpscaleCheck.IsChecked = d.PreUpscale;
            if (d.Tolerance is >= 0 and <= 100) ToleranceSlider.Value = d.Tolerance;
            if (d.Spread is >= 0 and <= 600) SpreadSlider.Value = d.Spread;
        }
        catch { /* 读取失败用默认值 */ }
    }

    private void SaveSettings()
    {
        try
        {
            var d = new CutoutSettings
            {
                Remember = RememberCheck.IsChecked == true,
                Model = ModelCombo.SelectedIndex,
                Gpu = AppSettings.GpuIndex,
                Fg = (int)FgSlider.Value,
                Bg = (int)BgSlider.Value,
                Feather = (int)FeatherSlider.Value,
                Edge = (int)EdgeSlider.Value,
                Morph = (int)MorphSlider.Value,
                AutoThreshold = AutoThresholdCheck.IsChecked == true,
                PreDenoise = PreDenoiseCheck.IsChecked == true,
                PreDenoiseLevel = PreDenoiseLevelCombo.SelectedIndex,
                PreUpscale = PreUpscaleCheck.IsChecked == true,
                Tolerance = (int)ToleranceSlider.Value,
                Spread = (int)SpreadSlider.Value,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, System.Text.Json.JsonSerializer.Serialize(d));
        }
        catch { /* 保存失败忽略 */ }
    }

    private void UpdateRunState()
    {
        RunBtn.IsEnabled = ToolGrid.Items.Count > 0 && !_running;
        PauseBtn.IsEnabled = _running && !_paused;
        ResumeBtn.IsEnabled = _running && _paused;
        UpdatePauseButtonVisual();
    }

    // 当前要突出的操作高亮蓝:运行中未暂停→「暂停」蓝;已暂停→「恢复」蓝
    private void UpdatePauseButtonVisual()
    {
        var accent = Application.Current.Resources.TryGetValue("AccentButtonStyle", out var s) && s is Style st ? st : null;
        if (_paused)
        {
            ResumeBtn.Style = accent;
            PauseBtn.Style = null;
        }
        else
        {
            PauseBtn.Style = accent;
            ResumeBtn.Style = null;
        }
    }

    // 暂停:处理完当前张后停在下一张之前;暂停期间可删除未处理的项目
    private void PauseBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!_running || _paused) return;
        _paused = true;
        _resumeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ToolGrid.IsPaused = true;   // 解锁列表「删除」(只删未处理项)
        VideoService.SuspendActiveProcess();   // 冻结当前处理进程:随点随停,零丢失
        TaskStatus.Text = "已暂停(进程已冻结,可点「恢复」继续)";
        Log("⏸ 已暂停:已冻结当前处理进程,点「恢复」从原处继续");
        UpdateRunState();
    }

    // 恢复:立即放行(解冻进程 + 放行等待的循环)
    private void ResumeBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!_running || !_paused) return;
        _paused = false;
        VideoService.ResumeActiveProcess();   // 解冻:从冻结点继续,不重算
        _resumeTcs?.TrySetResult(true);
        _resumeTcs = null;
        ToolGrid.IsPaused = false;
        TaskStatus.Text = "继续处理...";
        Log("▶ 已恢复,继续处理");
        UpdateRunState();
    }

    // ---------- 单击仅选中;双击打开大图预览 ----------
    private void ToolGrid_SelectionChanged(System.Collections.Generic.IReadOnlyList<ImageItem> items)
    {
        // 单击/框选只改变选中状态,预览由双击打开
    }

    // ---------- 预览:原图 / AI 主体蒙版 / 框选主体 / 智能涂抹 ----------
    private ImageItem? _previewItem;
    private bool _maskPreviewShown;      // 当前显示的是蒙版图
    private bool _selMode;               // 框选模式激活
    private bool _selDragging;
    private Windows.Foundation.Point _selStart, _selEnd;
    private (int x, int y, int w, int h)? _selPixels;   // 主体框选(原图像素坐标)
    private double _scale = 1;           // 预览缩放比(图片像素 → 画布坐标);画布尺寸=图片显示区域,坐标即图片坐标

    // 智能涂抹
    private bool _scribbleMode;          // 涂抹模式激活
    private bool _brushKeep = true;      // 当前笔刷:true=绿色保留,false=红色删除
    private bool _brushErase;            // 橡皮擦模式(优先于 keep/delete)
    private bool _erasing;               // 正在擦除中
    private int _brushSize = 18;         // 笔刷直径(画布像素)
    private readonly List<CutoutService.CutoutScribble> _scribbles = new();   // 当前预览图的涂抹(像素坐标)
    private List<(int X, int Y)>? _curStroke;   // 进行中的笔迹
    private readonly List<Ellipse> _curStrokeDots = new();   // 进行中笔迹的显示点
    // 撤回/重做(快照式:每次落笔/擦除/清除前存一份,撤回整步)
    private readonly Stack<List<CutoutService.CutoutScribble>> _undoStack = new();
    private readonly Stack<List<CutoutService.CutoutScribble>> _redoStack = new();

    // 主体标记持久化(按图片路径):框选 + 涂抹
    private sealed class MarksFile
    {
        public Dictionary<string, MarkEntry> Items { get; set; } = new();
    }
    private sealed class MarkEntry
    {
        public double[]? Box { get; set; }
        public List<MarkScribble>? Scribbles { get; set; }
    }
    private sealed class MarkScribble
    {
        public bool Keep { get; set; }
        public List<double[]> P { get; set; } = new();
    }
    private static string MarksFile_ => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ALHPro", "cutout-marks.json");

    private static MarksFile LoadMarks()
    {
        try
        {
            if (File.Exists(MarksFile_))
                return System.Text.Json.JsonSerializer.Deserialize<MarksFile>(File.ReadAllText(MarksFile_)) ?? new MarksFile();
        }
        catch { }
        return new MarksFile();
    }

    private static void SaveMarks(MarksFile m)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MarksFile_)!);
            File.WriteAllText(MarksFile_, System.Text.Json.JsonSerializer.Serialize(m));
        }
        catch { }
    }

    // 保存当前预览图的主体标记(框选+涂抹)到持久化文件
    private void PersistCurrentMarks()
    {
        var item = _previewItem;
        if (item == null) return;
        var m = LoadMarks();
        if (_selPixels == null && _scribbles.Count == 0)
        {
            m.Items.Remove(item.OriginalPath.Length > 0 ? item.OriginalPath : item.Path);
        }
        else
        {
            var e = new MarkEntry();
            if (_selPixels is (int bx, int by, int bw, int bh))
                e.Box = new double[] { bx, by, bw, bh };
            if (_scribbles.Count > 0)
            {
                e.Scribbles = _scribbles.Select(s => new MarkScribble
                {
                    Keep = s.Keep,
                    P = s.Points.Select(p => new double[] { p.X, p.Y }).ToList(),
                }).ToList();
            }
            m.Items[item.OriginalPath.Length > 0 ? item.OriginalPath : item.Path] = e;
        }
        SaveMarks(m);
        // 同步到列表项(缩略图标记):比例坐标
        item.SubjectBox = _selPixels is (int px, int py, int pw, int ph)
            ? (px / (double)Math.Max(1, item.PixelWidth), py / (double)Math.Max(1, item.PixelHeight), pw / (double)Math.Max(1, item.PixelWidth), ph / (double)Math.Max(1, item.PixelHeight))
            : null;
        item.Scribbles.Clear();
        double w0 = Math.Max(1, item.PixelWidth), h0 = Math.Max(1, item.PixelHeight);
        foreach (var s in _scribbles)
            item.Scribbles.Add((s.Keep, s.Points.Select(p => ((double)p.X / w0, (double)p.Y / h0)).ToList()));
        item.NotifyMarksChanged();
    }

    // 从持久化文件恢复当前预览图的主体标记
    private void RestoreMarks(ImageItem item)
    {
        _selPixels = null;
        _scribbles.Clear();
        var key = item.OriginalPath.Length > 0 ? item.OriginalPath : item.Path;
        var m = LoadMarks();
        if (m.Items.TryGetValue(key, out var e))
        {
            if (e.Box is { Length: 4 })
                _selPixels = ((int)e.Box[0], (int)e.Box[1], (int)e.Box[2], (int)e.Box[3]);
            if (e.Scribbles != null)
                foreach (var s in e.Scribbles)
                    _scribbles.Add(new CutoutService.CutoutScribble(s.Keep,
                        s.P.Select(p => ((int)p[0], (int)p[1])).ToList()));
        }
        ClearSelBtn.IsEnabled = _selPixels != null;
        ClearScribbleBtn.IsEnabled = _scribbles.Count > 0;
        // 恢复的标记要立即可见:对应画布设为可见(仅显示层,不进入框选/涂抹模式)
        SelCanvas.Visibility = _selPixels != null ? Visibility.Visible : Visibility.Collapsed;
        ScribbleCanvas.Visibility = _scribbles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // 缩略图标记同步(比例坐标)
        item.SubjectBox = _selPixels is (int px, int py, int pw, int ph)
            ? (px / (double)Math.Max(1, item.PixelWidth), py / (double)Math.Max(1, item.PixelHeight), pw / (double)Math.Max(1, item.PixelWidth), ph / (double)Math.Max(1, item.PixelHeight))
            : null;
        item.Scribbles.Clear();
        double w0 = Math.Max(1, item.PixelWidth), h0 = Math.Max(1, item.PixelHeight);
        foreach (var s in _scribbles)
            item.Scribbles.Add((s.Keep, s.Points.Select(p => ((double)p.X / w0, (double)p.Y / h0)).ToList()));
        item.NotifyMarksChanged();
        UpdateCanvasRects();   // 画布对齐图片显示区域(布局未就绪时由 SizeChanged 补)
        RenderSelOverlays();   // 在预览画布上重绘框选矩形与涂抹笔迹(布局未就绪时由 SizeChanged 补绘)
    }

    /// <summary>按像素坐标在预览画布上重绘框选矩形与涂抹笔迹(恢复标记/预览尺寸变化时调用)。
    /// 画布尺寸=图片显示区域,坐标即图片坐标,无偏移换算。</summary>
    private void RenderSelOverlays()
    {
        var item = _previewItem;
        if (item == null || _scale <= 0 || item.PixelWidth <= 0) return;
        // 框选矩形
        if (_selPixels is (int sx, int sy, int sw, int sh))
        {
            SelRect.Width = Math.Max(2, sw * _scale);
            SelRect.Height = Math.Max(2, sh * _scale);
            Canvas.SetLeft(SelRect, sx * _scale);
            Canvas.SetTop(SelRect, sy * _scale);
            SelRect.Visibility = Visibility.Visible;
        }
        else
        {
            SelRect.Visibility = Visibility.Collapsed;
        }
        // 涂抹笔迹重绘(画在子画布上,不影响其他元素)
        ScribbleDots.Children.Clear();
        foreach (var sb in _scribbles)
        {
            var color = sb.Keep
                ? Windows.UI.Color.FromArgb(170, 76, 200, 110)
                : Windows.UI.Color.FromArgb(170, 230, 80, 80);
            foreach (var (px, py) in sb.Points)
            {
                var dot = new Ellipse
                {
                    Width = _brushSize,
                    Height = _brushSize,
                    Fill = new SolidColorBrush(color),
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(dot, px * _scale - _brushSize / 2.0);
                Canvas.SetTop(dot, py * _scale - _brushSize / 2.0);
                ScribbleDots.Children.Add(dot);
            }
        }
    }

    // 按住查看原图:隐藏框选/涂抹标记;若正在显示 AI 蒙版,临时切回原图,松开恢复。
    // 支持两个入口:预览图本身 或 「按住查看原图」按钮(共用同一逻辑)
    private bool _peeking;
    private bool _peekWasMask;   // 按住时是否正处于 AI 蒙版显示(松开需恢复蒙版)
    private BitmapImage? _lastSourceImage;   // 进入预览时的原图源(按住查看原图时判断/恢复用)
    private void PeekStart()
    {
        if (_peeking) return;
        _peeking = true;
        SelRect.Visibility = Visibility.Collapsed;
        ScribbleDots.Visibility = Visibility.Collapsed;
        _peekWasMask = _maskPreviewShown;
        var item = _previewItem;
        if (item == null) return;
        bool isRawOriginal = _lastSourceImage != null && ReferenceEquals(PreviewImage.Source, _lastSourceImage);
        if (_maskPreviewShown || !isRawOriginal)
        {
            try { PreviewImage.Source = new BitmapImage(new Uri(item.Path)); } catch { }
        }
        PreviewHint.Text = _maskPreviewShown
            ? "已临时显示原图(标记隐藏),松开恢复蒙版"
            : "已隐藏标记(查看原图),松开恢复";
    }

    private void PeekEnd()
    {
        if (!_peeking) return;
        _peeking = false;
        ScribbleDots.Visibility = Visibility.Visible;
        RenderSelOverlays();
        if (_peekWasMask && _maskPreviewShown)
        {
            // 恢复蒙版显示
            var maskPath = _lastMaskPath;
            if (!string.IsNullOrEmpty(maskPath) && File.Exists(maskPath))
            {
                try { PreviewImage.Source = new BitmapImage(new Uri(maskPath)); } catch { }
            }
        }
        PreviewHint.Text = _selPixels != null || _scribbles.Count > 0
            ? "已恢复标记显示" : "";
    }

    // 「按住查看原图」按钮:按住隐藏框选/涂抹标记(或 AI 蒙版临时切回原图),松开恢复。
    // 捕获指针到按钮:按住期间指针被锁定,不会因移出/捕获丢失而误触发"松开"(表现为按住一闪即恢复)。
    private void PeekBtn_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        try { (sender as Microsoft.UI.Xaml.UIElement)?.CapturePointer(e.Pointer); } catch { }
        PeekStart();
    }

    private void PeekBtn_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        try { (sender as Microsoft.UI.Xaml.UIElement)?.ReleasePointerCapture(e.Pointer); } catch { }
        PeekEnd();
    }

    // 预览图本身也可按住查看原图(涂抹/框选模式时画布在最上层拦截,天然不冲突)
    private void PreviewImage_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        => PeekStart();

    private void PreviewImage_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        => PeekEnd();

    // 涂抹容差滑条:显示数值;蒙版预览显示中自动刷新
    private void Tolerance_Changed(object sender, RoutedEventArgs e)
        => OnToleranceChanged();

    // Slider.ValueChanged 专用(需精确签名)
    private void ToleranceSlider_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        => OnToleranceChanged();

    private void OnToleranceChanged()
    {
        if (ToleranceVal == null) return;
        ToleranceVal.Text = ((int)ToleranceSlider.Value).ToString();
        if (_scribbles.Count > 0)
            PreviewHint.Text = $"容差 {(int)ToleranceSlider.Value}:越大扩散越广,但始终被物体边缘挡住,不会跳到画面其他同色区域(处理时生效)";
        if (RememberCheck != null && RememberCheck.IsChecked == true)
            SaveSettings();   // 容差独立保存(勿依赖其他滑条顺带触发)
        RefreshMaskDelayed();   // 调整容差 → AI 主体预览自动更新
    }

    // 涂抹扩散距离上限滑条:显示数值;蒙版预览显示中自动刷新
    private void SpreadSlider_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (SpreadVal == null) return;
        int v = (int)SpreadSlider.Value;
        SpreadVal.Text = v <= 0 ? "不限" : v.ToString();
        if (RememberCheck != null && RememberCheck.IsChecked == true)
            SaveSettings();
        RefreshMaskDelayed();   // 调整扩散上限 → AI 主体预览自动更新
    }

    // 预览图片尺寸确定后:对齐画布与图片显示区域,并重绘标记
    private void PreviewImage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateCanvasRects();
        RenderSelOverlays();
    }

    private void ToolGrid_ItemDoubleTapped(ImageItem item)
    {
        try
        {
            _previewItem = item;
            _maskPreviewShown = false;
            _selMode = false;
            SelCanvas.Visibility = Visibility.Collapsed;
            SelRect.Visibility = Visibility.Collapsed;
            SelModeBtn.Content = "框选主体";
            _scribbleMode = false;
            ScribbleCanvas.Visibility = Visibility.Collapsed;
            ScribbleModeBtn.Content = "智能涂抹";
            ScribbleToolbar.Visibility = Visibility.Collapsed;
            _curStroke = null;
            _curStrokeDots.Clear();
            _lastDisplayPt = null;
            ScribbleDots.Children.Clear();
            ScribbleDots.Visibility = Visibility.Visible;
            _peeking = false;
            PreviewHint.Text = "";
            var rawSrc = new BitmapImage(new Uri(item.Path));
            _lastSourceImage = rawSrc;
            PreviewImage.Source = rawSrc;
            RestoreMarks(item);   // 恢复保存过的框选/涂抹
            if (_selPixels != null || _scribbles.Count > 0)
                PreviewHint.Text = "已恢复上次的主体标记(框选/涂抹),抠图时生效";
            PreviewOverlay.Visibility = Visibility.Visible;
        }
        catch (Exception) { }
    }

    private void PreviewClose_Click(object sender, RoutedEventArgs e)
    {
        PersistCurrentMarks();   // 关闭预览时保存框选/涂抹
        _previewItem = null;
        PreviewOverlay.Visibility = Visibility.Collapsed;
    }

    // AI 主体预览:显示模型识别的黑白蒙版(白=主体);再点恢复原图
    private async void MaskPreview_Click(object sender, RoutedEventArgs e)
    {
        var item = _previewItem;
        if (item == null || _running) return;
        if (_maskPreviewShown)
        {
            // 切回原图
            _maskPreviewShown = false;
            try { PreviewImage.Source = new BitmapImage(new Uri(item.Path)); } catch { }
            PreviewHint.Text = "";
            return;
        }
        await GenerateMaskAsync();
    }

    // 生成主体蒙版(参数调整后自动刷新用);防并发:同一时间只允许一个生成任务
    private bool _maskGenRunning;
    private bool _maskRefreshQueued;   // 生成期间参数又变了:生成完用最新参数再跑一次(调整即预览)
    private string? _lastMaskPath;   // 当前显示的蒙版文件路径(按住查看原图后恢复用)
    private async Task GenerateMaskAsync()
    {
        var item = _previewItem;
        if (item == null || _running) return;
        if (_maskGenRunning) { _maskRefreshQueued = true; return; }   // 正在生成:排队,完成后自动补跑
        _maskGenRunning = true;
        try
        {
            var modelKey = CutoutService.Models[Math.Clamp(ModelCombo.SelectedIndex, 0, CutoutService.Models.Length - 1)].Key;
            var maskPath = Path.Combine(Path.GetTempPath(), $"imgup_mask_{Guid.NewGuid():N}.png");
            EngineService.RegisterTempFile(maskPath);   // 注册:退出时统一清理,防 temp 累积
            _lastMaskPath = maskPath;
            MaskPreviewBtn.IsEnabled = false;
            MaskPreviewBtn.Content = "预览处理中…";
            PreviewHint.Text = "预览处理中…";
            // 主体预览叠加当前框选/涂抹/参数效果(所见即所得,与抠图输出一致);
            // 笔刷半径与最终抠图一致(预览与输出所见一致)
            var sel = _selPixels;
            var scr = _scribbles.Count > 0 ? _scribbles.ToList() : null;
            await CutoutService.PreviewMaskAsync(item.Path, maskPath, modelKey,
                (int)FgSlider.Value, (int)BgSlider.Value, CutoutGpuId,
                sel?.x, sel?.y, sel?.w, sel?.h, scr, (int)ToleranceSlider.Value,
                _scale > 0 ? _brushSize / 2.0 / _scale : null,
                (int)SpreadSlider.Value,
                (int)FeatherSlider.Value, (int)EdgeSlider.Value,
                (int)MorphSlider.Value, AutoThresholdCheck.IsChecked == true);
            try { PreviewImage.Source = new BitmapImage(new Uri(maskPath)); } catch { }
            _maskPreviewShown = true;
            var markNote = (sel != null ? "框选" : "") + (scr != null ? "+涂抹" : "");
            PreviewHint.Text = $"抠图预览(白=保留,黑=去除)" +
                (markNote.Length > 0 ? $",已叠加你的{markNote}效果" : "") +
                " · 再点一次返回原图 · 调整参数自动刷新";
            // 蒙版图临时文件:下次生成时被新文件替换,无需立即清理
        }
        catch (Exception ex)
        {
            PreviewHint.Text = "预览失败: " + ex.Message;
        }
        finally
        {
            MaskPreviewBtn.IsEnabled = true;
            MaskPreviewBtn.Content = "抠图预览";
            _maskGenRunning = false;
            // 生成期间参数/涂抹/框选又变了 → 用最新状态自动再跑一次(连续调整也始终跟上)
            if (_maskRefreshQueued)
            {
                _maskRefreshQueued = false;
                RefreshMaskDelayed();
            }
        }
    }

    // 参数调整(容差/阈值/羽化/增强)后:若蒙版预览正显示,自动刷新(0.4 秒防抖)
    private DispatcherQueueTimer? _maskRefreshTimer;
    private void RefreshMaskDelayed()
    {
        if (!_maskPreviewShown || _running) return;
        _maskRefreshTimer?.Stop();
        _maskRefreshTimer ??= CreateMaskRefreshTimer();
        _maskRefreshTimer.Start();
    }

    private DispatcherQueueTimer CreateMaskRefreshTimer()
    {
        var t = DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(400);
        t.IsRepeating = false;
        t.Tick += async (_, _) => await GenerateMaskAsync();
        return t;
    }

    // 框选主体模式开关(与智能涂抹互斥)
    private void SelMode_Click(object sender, RoutedEventArgs e)
    {
        _selMode = !_selMode;
        SelModeBtn.Content = _selMode ? "退出框选" : "框选主体";
        SelCanvas.Visibility = _selMode ? Visibility.Visible : Visibility.Collapsed;
        if (_selMode)
        {
            // 退出涂抹模式
            _scribbleMode = false;
            ScribbleCanvas.Visibility = Visibility.Collapsed;
            ScribbleModeBtn.Content = "智能涂抹";
            ScribbleToolbar.Visibility = Visibility.Collapsed;
        }
        if (!_selMode)
        {
            SelRect.Visibility = Visibility.Collapsed;
            _selDragging = false;
        }
        else if (_selPixels != null)
        {
            PreviewHint.Text = "已框选;可重新拖拽调整,或直接开始抠图";
        }
    }

    // 智能涂抹模式开关(与框选互斥):绿色=保留,红色=删除
    private void ScribbleMode_Click(object sender, RoutedEventArgs e)
    {
        _scribbleMode = !_scribbleMode;
        ScribbleModeBtn.Content = _scribbleMode ? "退出涂抹" : "智能涂抹";
        ScribbleCanvas.Visibility = _scribbleMode ? Visibility.Visible : Visibility.Collapsed;
        ScribbleToolbar.Visibility = _scribbleMode ? Visibility.Visible : Visibility.Collapsed;
        if (_scribbleMode)
        {
            HideToolbar();   // 初始淡出:悬停才显示,不挡画面
            _tbTimer?.Stop();
            // 退出框选模式
            _selMode = false;
            SelCanvas.Visibility = Visibility.Collapsed;
            SelRect.Visibility = Visibility.Collapsed;
            SelModeBtn.Content = "框选主体";
            PreviewHint.Text = _brushKeep
                ? "智能涂抹:🟢 绿色涂在要保留的物体上(松开后自动扩散到同色区域)"
                : "智能涂抹:🔴 红色涂在要去除的区域(松开后自动扩散到同色区域)";
        }
        else if (_selPixels != null || _scribbles.Count > 0)
        {
            // 退出涂抹模式:恢复已有标记的显示
            SelCanvas.Visibility = _selPixels != null ? Visibility.Visible : Visibility.Collapsed;
            RenderSelOverlays();
        }
        if (!_scribbleMode)
        {
            // 非涂抹模式:感应区不拦截鼠标(避免框选/涂抹死区)
            ToolbarHoverArea.IsHitTestVisible = false;
        }
    }

    // 笔刷切换:绿色保留 / 红色删除 / 橡皮擦
    private void BrushKeep_Click(object sender, RoutedEventArgs e)
    {
        _brushKeep = true;
        _brushErase = false;
        PreviewHint.Text = "🟢 保留笔:涂在要保留(抠出)的物体上 (Ctrl+Z 撤回)";
    }

    private void BrushDel_Click(object sender, RoutedEventArgs e)
    {
        _brushKeep = false;
        _brushErase = false;
        PreviewHint.Text = "🔴 删除笔:涂在要去除(透明)的区域 (Ctrl+Z 撤回)";
    }

    private void Eraser_Click(object sender, RoutedEventArgs e)
    {
        _brushErase = true;
        PreviewHint.Text = "🧽 橡皮擦:擦掉已涂抹的笔迹 (Ctrl+Z 可恢复)";
    }

    private void BrushSize_Changed(object sender, RoutedEventArgs e)
        => OnBrushSizeChanged();

    // Slider.ValueChanged 专用(需精确签名)
    private void BrushSizeSlider_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        => OnBrushSizeChanged();

    private void OnBrushSizeChanged()
    {
        _brushSize = (int)BrushSizeSlider.Value;
        RefreshMaskDelayed();   // 笔刷大小影响涂抹扩散半径 → AI 预览自动刷新
    }

    private void ClearScribble_Click(object sender, RoutedEventArgs e)
    {
        if (_scribbles.Count == 0) return;
        SnapshotScribbles();
        _scribbles.Clear();
        RefreshScribbleView();
        PreviewHint.Text = "已清除全部涂抹(将只按 AI 识别结果抠图),Ctrl+Z 可恢复";
        RefreshMaskDelayed();   // AI 预览显示中:涂抹变化自动刷新
    }

    // 落笔/擦除/清除前保存当前涂抹快照,作为一步可撤回
    private void SnapshotScribbles()
    {
        _undoStack.Push(_scribbles.ToList());
        _redoStack.Clear();
    }

    private void UndoScribble()
    {
        if (_undoStack.Count == 0) return;
        _redoStack.Push(_scribbles.ToList());
        _scribbles.Clear();
        _scribbles.AddRange(_undoStack.Pop());
        RefreshScribbleView();
        PreviewHint.Text = "已撤回上一步涂抹 (Ctrl+Shift+Z 重做)";
        RefreshMaskDelayed();   // AI 预览显示中:涂抹变化自动刷新
    }

    private void RedoScribble()
    {
        if (_redoStack.Count == 0) return;
        _undoStack.Push(_scribbles.ToList());
        _scribbles.Clear();
        _scribbles.AddRange(_redoStack.Pop());
        RefreshScribbleView();
        PreviewHint.Text = "已重做涂抹";
        RefreshMaskDelayed();   // AI 预览显示中:涂抹变化自动刷新
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => UndoScribble();
    private void Redo_Click(object sender, RoutedEventArgs e) => RedoScribble();

    // KeyboardAccelerator 触发(Ctrl+Z 撤回 / Ctrl+Shift+Z 重做)
    private void Undo_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        // 焦点在文本框时交给文本框自己的撤回(不抢)
        if (FocusInTextBox()) return;
        if (_undoStack.Count > 0)
        {
            UndoScribble();
            args.Handled = true;
        }
    }

    private void Redo_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        if (FocusInTextBox()) return;
        if (_redoStack.Count > 0)
        {
            RedoScribble();
            args.Handled = true;
        }
    }

    private static bool FocusInTextBox()
    {
        try
        {
            return Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement()
                is Microsoft.UI.Xaml.Controls.TextBox;
        }
        catch { return false; }
    }

    // 重绘涂抹显示 + 保存
    private void RefreshScribbleView()
    {
        ClearScribbleBtn.IsEnabled = _scribbles.Count > 0;
        RenderSelOverlays();
        PersistCurrentMarks();
    }

    // 橡皮擦:按画布坐标擦除附近已涂抹的点(换算到像素坐标)
    private void EraseAtCanvas(Windows.Foundation.Point pos)
    {
        var item = _previewItem;
        if (item == null || _scale <= 0) return;
        double cw = ScribbleCanvas.ActualWidth > 0 ? ScribbleCanvas.ActualWidth : SelCanvas.ActualWidth;
        double ch = ScribbleCanvas.ActualHeight > 0 ? ScribbleCanvas.ActualHeight : SelCanvas.ActualHeight;
        if (cw <= 0 || ch <= 0) return;
        pos.X = Math.Clamp(pos.X, 0, cw);
        pos.Y = Math.Clamp(pos.Y, 0, ch);
        int px = (int)(pos.X / _scale);
        int py = (int)(pos.Y / _scale);
        double rPx = Math.Max(1.0, _brushSize / _scale);   // 擦除半径(像素坐标)= 笔刷直径
        double r2 = rPx * rPx;
        for (int i = _scribbles.Count - 1; i >= 0; i--)
        {
            var sb = _scribbles[i];
            var kept = new List<(int X, int Y)>();
            bool changed = false;
            foreach (var p in sb.Points)
            {
                double dx = p.X - px, dy = p.Y - py;
                if (dx * dx + dy * dy <= r2) { changed = true; continue; }
                kept.Add(p);
            }
            if (kept.Count == 0)
                _scribbles.RemoveAt(i);
            else if (changed)
                _scribbles[i] = new CutoutService.CutoutScribble(sb.Keep, kept);
        }
    }

    // ---------- 涂抹工具条显隐:悬停 1 秒显示、移开 1 秒淡出、涂抹时立即透明(事件穿透) ----------
    private DispatcherQueueTimer? _tbTimer;
    private bool _tbTimerIsShow;
    private bool _tbShown = true;   // 当前工具条是否处于"完全显示"态(防重复动画/重复调用)

    private DispatcherQueueTimer CreateTbTimer()
    {
        var t = DispatcherQueue.CreateTimer();
        t.IsRepeating = false;
        t.Tick += (_, _) =>
        {
            if (_tbTimerIsShow) ShowToolbar();
            else HideToolbar();
        };
        return t;
    }

    private void ScheduleTb(bool show, double seconds)
    {
        _tbTimer?.Stop();
        _tbTimer ??= CreateTbTimer();
        _tbTimerIsShow = show;
        _tbTimer.Interval = TimeSpan.FromSeconds(seconds);
        _tbTimer.Start();
    }

    // 显示工具条:完全不透明(100%),自己接收鼠标事件(感应区让位)
    private void ShowToolbar()
    {
        if (_tbShown) return;
        _tbShown = true;
        ScribbleToolbar.IsHitTestVisible = true;
        ToolbarHoverArea.IsHitTestVisible = false;
        AnimateToolbarOpacity(1.0);
    }

    // 淡出工具条:平滑过渡到 55% 透明度(仍可看清按钮),事件穿透(感应区接管,涂抹不被挡)
    private void HideToolbar()
    {
        if (!_tbShown) return;
        _tbShown = false;
        ScribbleToolbar.IsHitTestVisible = false;
        ToolbarHoverArea.IsHitTestVisible = true;
        AnimateToolbarOpacity(0.55);
    }

    // 透明度平滑过渡(100% ↔ 30%,约 0.18 秒,自然渐变)
    private void AnimateToolbarOpacity(double to, double seconds = 0.18)
    {
        try
        {
            var anim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                To = to,
                Duration = new Duration(TimeSpan.FromSeconds(seconds)),
                EnableDependentAnimation = true,
            };
            var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            sb.Children.Add(anim);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(anim, ScribbleToolbar);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(anim, "Opacity");
            sb.Begin();
        }
        catch { ScribbleToolbar.Opacity = to; }
    }

    // 鼠标悬停在工具条上:取消隐藏,保持显示
    private void ScribbleToolbar_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _tbTimer?.Stop();
        ShowToolbar();
    }

    // 鼠标移出工具条:1 秒后淡出
    private void ScribbleToolbar_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        => ScheduleTb(false, 1.0);

    // 淡出状态下鼠标移到感应区:0.5 秒后显示工具条
    private void ToolbarHoverArea_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        => ScheduleTb(true, 0.5);

    // 鼠标离开感应区(工具条仍淡出):取消显示
    private void ToolbarHoverArea_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        => _tbTimer?.Stop();

    // 预览区指针移动 → 按指针位置直接判断是否悬停在工具条/感应区上。
    // 为什么不用 PointerEntered/Exited:工具条淡出时 IsHitTestVisible 被翻转,指针已停在
    // 原位不动时 WinUI 不会重发 PointerEntered,导致"鼠标放上去没反应,移出去再移回来才好"。
    // PointerMoved 无论命中测试如何都会持续触发,按坐标判断最可靠。
    private void PreviewRoot_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_scribbleMode || ScribbleToolbar.Visibility != Visibility.Visible) return;
        if (_erasing || _curStroke != null) return;   // 涂抹落笔中不抢焦点
        bool over = false;
        var tb = e.GetCurrentPoint(ScribbleToolbar).Position;
        if (tb.X >= 0 && tb.Y >= 0 && tb.X <= ScribbleToolbar.ActualWidth && tb.Y <= ScribbleToolbar.ActualHeight)
            over = true;
        else if (ToolbarHoverArea.Visibility == Visibility.Visible && ToolbarHoverArea.IsHitTestVisible)
        {
            var ha = e.GetCurrentPoint(ToolbarHoverArea).Position;
            if (ha.X >= 0 && ha.Y >= 0 && ha.X <= ToolbarHoverArea.ActualWidth && ha.Y <= ToolbarHoverArea.ActualHeight)
                over = true;
        }
        if (over)
        {
            _tbTimer?.Stop();
            ShowToolbar();
        }
        else if (_tbShown)
        {
            ScheduleTb(false, 1.0);
        }
    }

    // 涂抹交互:按下开始一笔(起点必须在图片区域内),移动画点并记录像素坐标,松开结束
    private void ScribbleCanvas_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_scribbleMode) return;
        var pos = e.GetCurrentPoint(ScribbleCanvas).Position;
        // 只能在图片区域内开始涂抹(画布=图片区域)
        if (pos.X < 0 || pos.Y < 0 || pos.X > ScribbleCanvas.ActualWidth || pos.Y > ScribbleCanvas.ActualHeight) return;
        _tbTimer?.Stop();
        HideToolbar();   // 涂抹时工具条立即淡出,不挡画面
        if (_brushErase)
        {
            SnapshotScribbles();   // 擦除前快照(撤回=撤销整次擦除)
            _erasing = true;
            ScribbleCanvas.CapturePointer(e.Pointer);
            EraseAtCanvas(pos);
            return;
        }
        SnapshotScribbles();   // 落笔前快照(撤回=撤销整笔)
        _curStroke = new List<(int, int)>();
        _curStrokeDots.Clear();
        _lastDisplayPt = null;   // 新一笔:插值基准重置
        ScribbleCanvas.CapturePointer(e.Pointer);
        AddScribblePoint(pos);
    }

    private void ScribbleCanvas_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_erasing)
        {
            EraseAtCanvas(e.GetCurrentPoint(ScribbleCanvas).Position);
            return;
        }
        if (_curStroke == null) return;
        AddScribblePoint(e.GetCurrentPoint(ScribbleCanvas).Position);
    }

    private void ScribbleCanvas_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_erasing)
        {
            _erasing = false;
            RefreshScribbleView();   // 擦完重绘 + 保存
            PreviewHint.Text = $"橡皮擦完成,当前共 {_scribbles.Count} 笔 (Ctrl+Z 可恢复)";
            try { ScribbleCanvas.ReleasePointerCapture(e.Pointer); } catch { }
            RefreshMaskDelayed();   // AI 预览显示中:涂抹变化自动刷新
            return;
        }
        if (_curStroke == null) return;
        if (_curStroke.Count > 0)
        {
            _scribbles.Add(new CutoutService.CutoutScribble(_brushKeep, _curStroke));
            ClearScribbleBtn.IsEnabled = true;
            PreviewHint.Text = $"已涂抹 {_scribbles.Count} 笔({(_brushKeep ? "保留" : "删除")});AI 会按颜色自动扩散,可继续涂或直接开始抠图 (Ctrl+Z 撤回)";
            PersistCurrentMarks();   // 每笔完成即保存
            RefreshMaskDelayed();   // AI 预览显示中:涂抹变化自动刷新
        }
        _curStroke = null;
        _lastDisplayPt = null;   // 一笔结束,插值基准清空
        _curStrokeDots.Clear();
        try { ScribbleCanvas.ReleasePointerCapture(e.Pointer); } catch { }
    }

    private void ScribbleCanvas_PointerCaptureLost(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _curStroke = null;
        if (_erasing) { _erasing = false; RefreshScribbleView(); }
    }

    // 画布坐标 → 像素坐标并画点(笔迹可视化)。
    // 画布尺寸=图片显示区域,坐标即图片坐标:显示跟手,记录=坐标/缩放,无偏移可能。
    // 滑动连续:与上一点间距超过步长时线性插值补点(快速划过也不断线);
    // 渲染用圆点但插值足够密,边缘平滑无马赛克。
    private void AddScribblePoint(Windows.Foundation.Point pos)
    {
        var item = _previewItem;
        if (item == null || _curStroke == null || _scale <= 0) return;
        double cw = ScribbleCanvas.ActualWidth > 0 ? ScribbleCanvas.ActualWidth : SelCanvas.ActualWidth;
        double ch = ScribbleCanvas.ActualHeight > 0 ? ScribbleCanvas.ActualHeight : SelCanvas.ActualHeight;
        if (cw <= 0 || ch <= 0) return;
        // 限制在图片显示区域内(画布=图片区域)
        pos.X = Math.Clamp(pos.X, 0, cw);
        pos.Y = Math.Clamp(pos.Y, 0, ch);

        // 与上一点插值补点:步长 = 笔刷直径的 1/3(足够密,快速划过也连续)
        double step = Math.Max(2.0, _brushSize / 3.0);
        if (_lastDisplayPt is { } lp)
        {
            double dx = pos.X - lp.X, dy = pos.Y - lp.Y;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist > step)
            {
                int n = (int)Math.Ceiling(dist / step);
                for (int k = 1; k <= n; k++)
                {
                    double t = (double)k / n;
                    DrawStrokePoint(new Windows.Foundation.Point(lp.X + dx * t, lp.Y + dy * t), item);
                }
                _lastDisplayPt = pos;
                return;
            }
        }
        DrawStrokePoint(pos, item);
        _lastDisplayPt = pos;
    }

    private Windows.Foundation.Point? _lastDisplayPt;   // 上一笔迹点(画布坐标,用于滑动插值补点)

    /// <summary>在画布上画一个笔迹点(绿/红圆点)+ 记录像素坐标。</summary>
    private void DrawStrokePoint(Windows.Foundation.Point pos, ALHPro.Views.ImageItem item)
    {
        double cw = ScribbleCanvas.ActualWidth > 0 ? ScribbleCanvas.ActualWidth : SelCanvas.ActualWidth;
        double ch = ScribbleCanvas.ActualHeight > 0 ? ScribbleCanvas.ActualHeight : SelCanvas.ActualHeight;
        pos.X = Math.Clamp(pos.X, 0, cw);
        pos.Y = Math.Clamp(pos.Y, 0, ch);
        var color = _brushKeep
            ? Windows.UI.Color.FromArgb(170, 76, 200, 110)
            : Windows.UI.Color.FromArgb(170, 230, 80, 80);
        var dot = new Ellipse
        {
            Width = _brushSize,
            Height = _brushSize,
            Fill = new SolidColorBrush(color),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(dot, pos.X - _brushSize / 2.0);
        Canvas.SetTop(dot, pos.Y - _brushSize / 2.0);
        ScribbleDots.Children.Add(dot);
        _curStrokeDots.Add(dot);
        // 换算像素坐标(画布内部坐标 0..w 即图片显示坐标,除以缩放比即原图像素)
        var (px, py) = CanvasToImage(pos.X, pos.Y, item);
        _curStroke.Add((px, py));
    }

    /// <summary>画布坐标 → 原图像素坐标(统一换算,框选与涂抹共用)。
    /// 画布尺寸=图片显示尺寸(UpdateCanvasRects 已定),内部坐标即图片显示坐标,除以 _scale 得原图像素。</summary>
    private (int X, int Y) CanvasToImage(double cx, double cy, ALHPro.Views.ImageItem item)
    {
        int px = (int)(cx / Math.Max(0.0001, _scale));
        int py = (int)(cy / Math.Max(0.0001, _scale));
        px = Math.Clamp(px, 0, Math.Max(0, item.PixelWidth - 1));
        py = Math.Clamp(py, 0, Math.Max(0, item.PixelHeight - 1));
        return (px, py);
    }

    // 显式保存标记(框选/涂抹已自动保存;此按钮给确认反馈)
    private void SaveMarks_Click(object sender, RoutedEventArgs e)
    {
        if (_previewItem == null) return;
        PersistCurrentMarks();
        var n = (_selPixels != null ? 1 : 0) + _scribbles.Count;
        SaveMarksBtn.Content = n > 0 ? "已保存 ✓" : "无标记可保存";
        PreviewHint.Text = n > 0 ? $"已保存 {(_selPixels != null ? "框选 + " : "")}{_scribbles.Count} 笔涂抹,缩略图上可见标记" : "当前没有框选或涂抹标记";
        var t = DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromSeconds(1.5);
        t.IsRepeating = false;
        t.Tick += (_, _) => SaveMarksBtn.Content = "保存标记";
        t.Start();
    }

    private void ClearSel_Click(object sender, RoutedEventArgs e)
    {
        _selPixels = null;
        SelRect.Visibility = Visibility.Collapsed;
        ClearSelBtn.IsEnabled = false;
        PreviewHint.Text = "已清除框选(将按全图抠取)";
        PersistCurrentMarks();
        RefreshMaskDelayed();   // AI 预览显示中:框选变化自动刷新
    }

    // 让框选/涂抹画布精确覆盖图片显示区域(画布坐标=图片坐标),并记录缩放比。
    // 在预览尺寸/图片变化时调用。
    private void UpdateCanvasRects()
    {
        var item = _previewItem;
        if (item == null || item.PixelWidth <= 0 || PreviewImage.ActualWidth <= 0)
        {
            _scale = 0;
            return;
        }
        double s = Math.Min(PreviewImage.ActualWidth / item.PixelWidth, PreviewImage.ActualHeight / item.PixelHeight);
        double w = item.PixelWidth * s, h = item.PixelHeight * s;
        double ox = (PreviewImage.ActualWidth - w) / 2, oy = (PreviewImage.ActualHeight - h) / 2;
        _scale = s;
        // 关键修复(蒙版偏移):PreviewRoot 是 Grid,Canvas.SetLeft/Top 附加属性只被 Canvas 父级识别,
        // 在 Grid 里被忽略 → 画布钉在 (0,0),而图片 Uniform 居中(letterbox 边距 ox,oy) →
        // 框选/涂抹像素坐标整体偏移 (ox,oy)/scale → 蒙版偏移。
        // Grid 里定位用 Margin + Left/Top 对齐(与 Canvas.SetLeft/Top 语义一致)。
        SelCanvas.Width = w;
        SelCanvas.Height = h;
        SelCanvas.HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left;
        SelCanvas.VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Top;
        SelCanvas.Margin = new Microsoft.UI.Xaml.Thickness(ox, oy, 0, 0);
        ScribbleCanvas.Width = w;
        ScribbleCanvas.Height = h;
        ScribbleCanvas.HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left;
        ScribbleCanvas.VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Top;
        ScribbleCanvas.Margin = new Microsoft.UI.Xaml.Thickness(ox, oy, 0, 0);
    }

    private void SelCanvas_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_selMode) return;
        var pos = e.GetCurrentPoint(SelCanvas).Position;
        // 只能在图片区域内开始框选(画布=图片区域)
        if (pos.X < 0 || pos.Y < 0 || pos.X > SelCanvas.ActualWidth || pos.Y > SelCanvas.ActualHeight) return;
        _selDragging = true;
        _selStart = _selEnd = pos;
        SelRect.Visibility = Visibility.Visible;
    }

    private void SelCanvas_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_selDragging) return;
        var pos = e.GetCurrentPoint(SelCanvas).Position;
        // 拖拽过程 Clamp 到图片显示区域(框不超出图片)
        pos.X = Math.Clamp(pos.X, 0, SelCanvas.ActualWidth);
        pos.Y = Math.Clamp(pos.Y, 0, SelCanvas.ActualHeight);
        _selEnd = pos;
        double x = Math.Min(_selStart.X, _selEnd.X), y = Math.Min(_selStart.Y, _selEnd.Y);
        double w2 = Math.Abs(_selEnd.X - _selStart.X), h2 = Math.Abs(_selEnd.Y - _selStart.Y);
        Canvas.SetLeft(SelRect, x);
        Canvas.SetTop(SelRect, y);
        SelRect.Width = w2;
        SelRect.Height = h2;
        SelRect.Visibility = w2 > 2 && h2 > 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SelCanvas_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_selDragging) return;
        _selDragging = false;
        if (SelRect.Visibility != Visibility.Visible) return;
        // 画布坐标 → 原图像素坐标(画布=图片显示区域,除以缩放比即像素)
        var item = _previewItem;
        if (item == null || _scale <= 0 || item.PixelWidth <= 0) return;
        int x = (int)Math.Floor(Math.Min(_selStart.X, _selEnd.X) / _scale);
        int y = (int)Math.Floor(Math.Min(_selStart.Y, _selEnd.Y) / _scale);
        int x2 = (int)Math.Ceiling(Math.Max(_selStart.X, _selEnd.X) / _scale);
        int y2 = (int)Math.Ceiling(Math.Max(_selStart.Y, _selEnd.Y) / _scale);
        x = Math.Clamp(x, 0, item.PixelWidth);
        y = Math.Clamp(y, 0, item.PixelHeight);
        x2 = Math.Clamp(x2, 0, item.PixelWidth);
        y2 = Math.Clamp(y2, 0, item.PixelHeight);
        if (x2 - x >= 8 && y2 - y >= 8)
        {
            _selPixels = (x, y, x2 - x, y2 - y);
            ClearSelBtn.IsEnabled = true;
            PreviewHint.Text = $"已框选 {x2 - x}×{y2 - y} 像素主体区域(区域外将被去除,已保存,缩略图可见标记)";
            PersistCurrentMarks();   // 框选完成即保存
            RefreshMaskDelayed();   // AI 预览显示中:框选变化自动刷新
        }
        else
        {
            _selPixels = null;
            PreviewHint.Text = "选区过小(至少 8×8 像素),请重新框选";
        }
    }

    private void SelCanvas_PointerCaptureLost(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        => _selDragging = false;

    // ---------- 选择 ----------
    public void PickImage() => PickBtn_Click(this, new RoutedEventArgs());
    public void Run() => RunBtn_Click(this, new RoutedEventArgs());

    private async void PickBtn_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".webp");
        picker.FileTypeFilter.Add(".bmp");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var files = picker.PickMultipleFilesAsync().AsTask().Result;
        if (files != null && files.Count > 0)
        {
            await ToolGrid.AddImagesAsync(files.Select(f => f.Path));
            Log($"添加了 {files.Count} 张图片到列表");
        }
    }

    private void BrowseOut_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var folder = picker.PickSingleFolderAsync().AsTask().Result;
        if (folder != null)
        {
            OutDirBox.Text = folder.Path;
            _customOutDir = folder.Path;
        }
    }

    // 取这张图的框选/涂抹标记(像素坐标):
    // 当前预览图用内存最新值;其它图片从持久化文件按路径读取(关闭预览后依然生效)
    private ((int x, int y, int w, int h)? sel, List<CutoutService.CutoutScribble>? scr) GetMarksForItem(ImageItem item)
    {
        if (ReferenceEquals(item, _previewItem))
            return (_selPixels, _scribbles.Count > 0 ? _scribbles.ToList() : null);
        var key = item.OriginalPath.Length > 0 ? item.OriginalPath : item.Path;
        var m = LoadMarks();
        if (!m.Items.TryGetValue(key, out var e)) return (null, null);
        (int, int, int, int)? sel = null;
        if (e.Box is { Length: 4 })
            sel = ((int)e.Box[0], (int)e.Box[1], (int)e.Box[2], (int)e.Box[3]);
        List<CutoutService.CutoutScribble>? scr = null;
        if (e.Scribbles is { Count: > 0 })
            scr = e.Scribbles.Select(s => new CutoutService.CutoutScribble(s.Keep,
                s.P.Select(p => ((int)p[0], (int)p[1])).ToList())).ToList();
        return (sel, scr);
    }

    // 手动编辑输出目录也生效(留空=源图目录)
    private void OutDirBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var t = OutDirBox.Text.Trim();
        _customOutDir = t.Length > 0 ? t : null;
    }

    // ---------- 批量抠图 ----------
    private async void RunBtn_Click(object sender, RoutedEventArgs e)
    {
        // 只处理选中的项(勾选后):否则处理全部
        bool onlySelected = SelectedOnlyCheck.IsChecked == true;
        var items = (onlySelected ? ToolGrid.SelectedItems : ToolGrid.Items).ToArray();
        if (onlySelected && items.Length == 0)
        {
            await ShowErrorAsync("请先在右侧选中要处理的图片(可框选多张)");
            return;
        }
        if (items.Length == 0 || _running) return;
        PersistCurrentMarks();   // 确保当前预览图的框选/涂抹已落盘(关闭预览后才点开始的情况)
        _running = true;
        _paused = false;
        _resumeTcs = null;
        _runItems = items;
        var taskStart = DateTime.Now;   // 任务耗时统计
        foreach (var it in items) { it.Progress = 0; it.StatusText = ""; }   // 重跑时清掉上次状态
        RunBtn.IsEnabled = false;
        CancelBtn.IsEnabled = true;
        PauseBtn.IsEnabled = true;
        ResumeBtn.IsEnabled = false;
        UpdatePauseButtonVisual();   // 运行中未暂停:暂停按钮高亮蓝
        ToolGrid.IsProcessing = true;   // 处理中锁死右侧列表的删除/清空等操作(暂停时解锁删除)
        TaskProgress.Value = 0;
        TaskStatus.Text = "准备中...";

        // 输出目录:多张时创建子文件夹(WebP 转码件优先用原始目录,避免落到应用私有目录)
        var firstSrc = items[0].OriginalPath.Length > 0 ? items[0].OriginalPath : items[0].Path;
        var baseDir = _customOutDir ?? Path.GetDirectoryName(firstSrc)!;
        string outDir;
        if (items.Length >= 2)
        {
            var sub = $"抠图输出_{DateTime.Now:yyyyMMdd_HHmmss}";
            outDir = Path.Combine(baseDir, sub);
            Directory.CreateDirectory(outDir);
        }
        else
        {
            outDir = baseDir;
        }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        // 抠图参数:模型 + 前后景阈值 + 羽化 + 边缘增强 + 预处理 + 主体框选
        var cutModel = CutoutService.GetModel(CutoutService.Models[Math.Clamp(ModelCombo.SelectedIndex, 0, CutoutService.Models.Length - 1)].Key);
        // 模型缺失前置校验:文件不存在立即明确报错(不再逐张失败后弹"完成 0 张"掩盖原因)
        if (EngineService.FindCutoutModel(cutModel.FileName) == null)
        {
            await ShowErrorAsync($"未找到抠图模型「{cutModel.Label}」({cutModel.FileName}) — 请把该模型文件放进 engines/rembg 目录,或换用其它模型");
            return;
        }
        var modelKey = cutModel.Key;
        var fgThreshold = (int)FgSlider.Value;
        var bgThreshold = (int)BgSlider.Value;
        var featherRadius = (int)FeatherSlider.Value;
        var edgeStrength = (int)EdgeSlider.Value;
        var preDenoise = PreDenoiseCheck.IsChecked == true;
        var preDenoiseLevel = Math.Max(1, Math.Min(3, PreDenoiseLevelCombo.SelectedIndex + 1));   // 防护:combo 无选中(-1)时按 1 档,不越界
        var preUpscale = PreUpscaleCheck.IsChecked == true;
        // 大图内存提示:抠图按原分辨率分配数组(48MP 峰值>1.3GB),超阈值(24MP)时提前提示用户
        try
        {
            using (var probe = new System.Drawing.Bitmap(items.Length > 0 ? items[0].Path : ""))
            {
                if (probe.Width > 0 && (long)probe.Width * probe.Height > 24_000_000)
                    Log($"⚠ 图片较大({probe.Width}×{probe.Height}),抠图占用内存可能超过 1GB — 若处理失败/卡顿,建议先用图片超分页缩小图片再抠");
            }
        }
        catch { }
        Log($"模型={modelKey},前景阈值={fgThreshold},背景阈值={bgThreshold},羽化={featherRadius},边缘增强={edgeStrength}" +
            (preDenoise ? $",预处理降噪({preDenoiseLevel})" : "") +
            (preUpscale ? ",预处理超分 2x" : "") +
            (_selPixels != null ? $",主体框选 {_selPixels.Value.w}×{_selPixels.Value.h}@{_selPixels.Value.x},{_selPixels.Value.y}" : ""));
        int progressIndex = 0;
        int okCount = 0, failCount = 0;
        var outputFiles = new System.Collections.Generic.List<string>();   // 本次成功输出(弹窗高亮用)
        IProgress<(int pct, string msg)> progress =
            new System.Progress<(int pct, string msg)>(t =>
            {
                // 整体进度 = (已完成张数 + 当前张内部进度) / 当前剩余总张数;
                // 暂停删除未处理项后,已完成数不变、剩余变少 → 进度条直接跳变更新
                int done = _runItems?.Count(it => IsItemDone(it) && ToolGrid.Items.Contains(it)) ?? 0;
                int active = _runItems?.Count(it => ToolGrid.Items.Contains(it) && !IsItemDone(it)) ?? 0;
                var overall = done + active > 0
                    ? Math.Min(100.0, (done + t.pct / 100.0) / (done + active) * 100.0)
                    : t.pct;
                TaskProgress.Value = Math.Max(TaskProgress.Value, (int)Math.Round(overall));
                TaskStatus.Text = done + active > 0
                    ? $"({done + 1}/{done + active}) {t.msg}"
                    : t.msg;
                SafeRender.ApplyRestUi(TaskStatus, CancelBtn, t.msg);   // 休息时:黄字加粗 + 按钮变「跳过休息」
                // 当前张的列表状态(暂停删除判断:处理中的项不可删)
                if (_runItems != null && progressIndex >= 0 && progressIndex < _runItems.Length)
                {
                    var it = _runItems[progressIndex];
                    if (ToolGrid.Items.Contains(it) && !IsItemDone(it))
                    {
                        it.Progress = Math.Max(it.Progress, t.pct);
                        it.StatusText = t.msg;
                    }
                }
            });
        try
        {
            int total = items.Length;
            TaskLogText.Text = "";
            Log($"开始抠图任务:共 {total} 张,设备={(CutoutGpuId >= 0 ? $"GPU {CutoutGpuId}" : "CPU (软件计算,流畅不卡)")}");
            Log($"输出目录:{outDir}");
            for (int i = 0; i < total; i++)
            {
                var item = items[i];
                progressIndex = i;

                // 暂停门控:暂停时停在这里,点「恢复」立即续上;取消会从这里抛出
                while (_resumeTcs != null)
                    await _resumeTcs.Task.WaitAsync(ct);
                // 暂停期间被删除的项目:直接跳过,不再处理
                if (!ToolGrid.Items.Contains(item))
                {
                    Log($"  已跳过:{item.Name}(暂停时已从列表删除)");
                    continue;
                }
                item.Progress = 0;
                item.StatusText = "等待处理...";

                // 降温休息(每小时/温度墙):处理下一张前检查
                await SafeRender.RestIfDueAsync(i * 100 / Math.Max(1, total), progress, ct);
                Log($"→ ({i + 1}/{total}) 处理 {item.Name}");
                progress.Report((0, $"正在抠图 {item.Name}..."));
                var baseName = !string.IsNullOrWhiteSpace(item.CustomName)
                    ? item.CustomName
                    : Path.GetFileNameWithoutExtension(item.Path) + "_抠图";
                var outPath = UpscaleView.UniquePath(outDir, baseName + "_" + UpscaleView.ModelShort(modelKey) + ".png");
                string? tmpDenoise = null, tmpUp = null, tmpExif = null;
                try
                {
                    // 预处理(可选):先降噪/超分,再抠图(输出分辨率=预处理后尺寸)
                    // 有 EXIF 旋转的图先标准化方向,否则标记坐标与处理结果错位
                    // NormalizeExif 内部会 new Bitmap(input) 解码整张图(仅为读 EXIF)—— 大图会卡住 UI 线程
                    // (这正是"开始处理时卡顿"的根因,每张都同步解码),故放到后台线程执行。
                    string srcPath = await Task.Run(() => EngineService.NormalizeExif(item.Path));
                    if (!ReferenceEquals(srcPath, item.Path))
                    {
                        tmpExif = srcPath;
                        Log("  检测到 EXIF 旋转,已先旋转为标准方向...");
                    }
                    if (preDenoise)
                    {
                        tmpDenoise = Path.Combine(Path.GetTempPath(), $"imgup_cut_denoise_{Guid.NewGuid():N}.png");
                        progress.Report((0, $"预处理降噪 {item.Name}..."));
                        Log($"  预处理降噪(waifu2x 1x,强度{"弱中强"[preDenoiseLevel - 1]})...");
                        await EngineService.UpscaleAsync(srcPath, tmpDenoise, "waifu2x",
                            "models-cunet", 1, preDenoiseLevel, CurrentGpuId, false, progress, ct);
                        srcPath = tmpDenoise;
                    }
                    if (preUpscale)
                    {
                        tmpUp = Path.Combine(Path.GetTempPath(), $"imgup_cut_up_{Guid.NewGuid():N}.png");
                        progress.Report((0, $"预处理超分 {item.Name}..."));
                        Log("  预处理超分(waifu2x 2x)...");
                        await EngineService.UpscaleAsync(srcPath, tmpUp, "waifu2x",
                            "models-cunet", 2, 0, CurrentGpuId, false, progress, ct);
                        srcPath = tmpUp;
                    }
                    // 主体框选/涂抹:按图片路径读取标记(预览关闭后依然生效);
                    // 若启用了预处理超分 2x,标记坐标同步放大到超分图坐标系
                    var (selRaw, scrRaw) = GetMarksForItem(item);
                    int markMul = preUpscale ? 2 : 1;
                    var sel = selRaw is (int sx, int sy, int sw, int sh)
                        ? (sx * markMul, sy * markMul, sw * markMul, sh * markMul)
                        : ((int, int, int, int)?)null;
                    List<CutoutService.CutoutScribble>? scr = null;
                    if (scrRaw != null)
                        scr = scrRaw.Select(s => new CutoutService.CutoutScribble(s.Keep,
                            s.Points.Select(p => (p.X * markMul, p.Y * markMul)).ToList())).ToList();
                    await CutoutService.CutoutAsync(srcPath, outPath, modelKey,
                        fgThreshold, bgThreshold, featherRadius, edgeStrength, CutoutGpuId,
                        sel?.Item1, sel?.Item2, sel?.Item3, sel?.Item4, scr,
                        (int)ToleranceSlider.Value, _scale > 0 ? _brushSize / 2.0 / _scale : null,
                        (int)SpreadSlider.Value,
                        (int)MorphSlider.Value, AutoThresholdCheck.IsChecked == true,
                        progress, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 失败时清理本次已生成的不完整输出,避免"失败却有文件"的误解
                    try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
                    AppLogger.Error($"抠图失败: {item.Name}", ex);
                    Log($"  ✗ 失败:{ex.Message}(不完整输出已清理)");
                    item.StatusText = "✗ 失败";
                    failCount++;
                    continue;   // 单张失败,继续下一张
                }
                finally
                {
                    try { if (tmpDenoise != null) File.Delete(tmpDenoise); } catch { }
                    try { if (tmpUp != null) File.Delete(tmpUp); } catch { }
                    try { if (tmpExif != null) File.Delete(tmpExif); } catch { }
                }
                item.Info = await Task.Run(() =>
                {
                    try
                    {
                        using var b = new System.Drawing.Bitmap(outPath);
                        return $"✓ {b.Width}×{b.Height} · {new FileInfo(outPath).Length / 1048576.0:0.0} MB";
                    }
                    catch { return $"✓ {Path.GetFileName(outPath)}"; }
                });
                item.Progress = 100;
                item.StatusText = "✓ 完成";
                ScheduleAutoRemove(item);   // 设置开启时:3 秒后自动删除该项目
                RefreshProgressBar(items, $"完成 {item.Name}");
                outputFiles.Add(outPath);   // 记录成功输出(弹窗高亮/列名用)
                Log($"  ✓ 完成 → {Path.GetFileName(outPath)}");
                okCount++;
            }
            TaskProgress.Value = 100;
            TaskStatus.Text = $"完成 {okCount} 张";
            Log($"任务结束:成功 {okCount} 张,失败 {failCount} 张,耗时 {(int)(DateTime.Now - taskStart).TotalMinutes}分{(DateTime.Now - taskStart).Seconds}秒");
            StatusChanged?.Invoke($"完成 {okCount} 张 → {outDir}");
            await ShowResultAsync(okCount, outDir, outputFiles);
        }
        catch (OperationCanceledException)
        {
            TaskStatus.Text = "已取消";
            Log($"任务已取消(已完成 {okCount} 张,失败 {failCount} 张,耗时 {(int)(DateTime.Now - taskStart).TotalMinutes}分{(DateTime.Now - taskStart).Seconds}秒)");
            StatusChanged?.Invoke("已取消");
        }
        catch (Exception ex)
        {
            TaskStatus.Text = "失败";
            AppLogger.Error("抠图任务中断", ex);
            Log($"任务中断:{ex.Message}");
            StatusChanged?.Invoke("失败: " + ex.Message);
            await ShowErrorAsync(ex.Message);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _running = false;
            _paused = false;
            _resumeTcs = null;
            _runItems = null;
            CancelBtn.IsEnabled = false;
            ToolGrid.IsProcessing = false;   // 处理结束,恢复右侧列表删除/清空
            ToolGrid.IsPaused = false;
            UpdateRunState();
        }
    }

    /// <summary>是否已处理完(成功或失败),用于动态总进度/暂停删除判断。</summary>
    private static bool IsItemDone(ImageItem it)
        => it.Progress >= 100 || it.StatusText.StartsWith("✗");

    /// <summary>按当前列表重新计算进度(暂停删除未处理项后,总数变小,进度条直接跳变更新)。</summary>
    private void RefreshProgressBar(ImageItem[] items, string statusText)
    {
        int done = items.Count(it => IsItemDone(it) && ToolGrid.Items.Contains(it));
        int active = items.Count(it => ToolGrid.Items.Contains(it) && !IsItemDone(it));
        if (done + active > 0)
        {
            TaskProgress.Value = Math.Max(TaskProgress.Value,
                (int)Math.Round(done * 100.0 / (done + active)));
            TaskStatus.Text = $"({done}/{done + active}) {statusText}";
        }
        else
        {
            TaskProgress.Value = 100;
            TaskStatus.Text = statusText;
        }
    }

    /// <summary>设置开启「完成后自动删除」时:项目完成 3 秒后自动从列表删除(留时间看完成信息)。</summary>
    private void ScheduleAutoRemove(ImageItem item)
    {
        if (!AppSettings.AutoRemoveDone) return;
        var t = DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromSeconds(3);
        t.IsRepeating = false;
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (AppSettings.AutoRemoveDone && ToolGrid.Items.Contains(item) && item.Progress >= 100)
            {
                ToolGrid.Items.Remove(item);
                UpdateFileInfo();
                UpdateRunState();
                Log($"已完成项目自动删除(等 3 秒):{item.Name}");
            }
        };
        t.Start();
    }

    /// <summary>追加一行日志(带时间戳),自动滚动到底部,超限自动清理最旧部分。</summary>
    private void Log(string msg)
    {
        AppLogger.Info(msg);   // 同步写诊断日志文件
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        var text = TaskLogText.Text;
        TaskLogText.Text = text == "日志:等待任务..." ? line : text + "\n" + line;
        // 自动清理:超过 200 行,删除最旧的一半
        var lines = TaskLogText.Text.Split('\n');
        if (lines.Length > 200)
            TaskLogText.Text = string.Join("\n", lines.Skip(80)) + "\n";
        TaskLogScroll.ChangeView(null, TaskLogScroll.ScrollableHeight, null, true);
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        // 「强制结束」始终停止当前任务(包括休息中);跳过休息请用底部右侧专属「跳过休息」按钮
        TaskStatus.Text = "正在停止...";
        Log("用户点击「强制结束」,正在停止任务");
        _cts?.Cancel();
    }

    private async Task ShowResultAsync(int count, string dir, System.Collections.Generic.List<string>? outputFiles = null)
    {
        outputFiles ??= new System.Collections.Generic.List<string>();
        var listText = outputFiles.Count > 0
            ? "\n\n输出文件:\n" + string.Join("\n", outputFiles.Take(10).Select(f => "· " + System.IO.Path.GetFileName(f)))
                + (outputFiles.Count > 10 ? "...(共 " + outputFiles.Count + " 个)" : "")
            : "";
        var dlg = new ContentDialog
        {
            Title = "处理完成",
            Content = new TextBlock
            {
                Text = $"已处理 {count} 张图片\n输出目录:\n{dir}{listText}",
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
            },
            PrimaryButtonText = "打开输出文件夹",
            CloseButtonText = "关闭",
            XamlRoot = this.XamlRoot,
        };
        var result = await dlg.ShowAsync();
        if (result == ContentDialogResult.Primary)
            ProcessStartHelper.OpenSelect(outputFiles.Count > 0 ? outputFiles : new System.Collections.Generic.List<string> { dir });
    }

    private async Task ShowErrorAsync(string msg)
    {
        var dlg = new ContentDialog
        {
            Title = "处理失败",
            Content = new TextBlock { Text = msg, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap },
            CloseButtonText = "关闭",
            XamlRoot = this.XamlRoot,
        };
        await dlg.ShowAsync();
    }

    public event Action<string>? StatusChanged;
}
