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

    // 主体标记持久化(按图片路径):框选 + 涂抹



    // 按住查看原图:隐藏框选/涂抹标记;若正在显示 AI 蒙版,临时切回原图,松开恢复。
    // 支持两个入口:预览图本身 或 「按住查看原图」按钮(共用同一逻辑)
    private bool _peeking;
    private bool _peekWasMask;   // 按住时是否正处于 AI 蒙版显示(松开需恢复蒙版)
    private BitmapImage? _lastSourceImage;   // 进入预览时的原图源(按住查看原图时判断/恢复用)
    private void PeekStart()
    {
        if (_peeking) return;
        _peeking = true;
        _peekWasMask = _maskPreviewShown;
        var item = _previewItem;
        if (item == null) return;
        bool isRawOriginal = _lastSourceImage != null && ReferenceEquals(PreviewImage.Source, _lastSourceImage);
        if (_maskPreviewShown || !isRawOriginal)
        {
            try { PreviewImage.Source = new BitmapImage(new Uri(item.Path)); } catch { }
        }
    }

    private void PeekEnd()
    {
        if (!_peeking) return;
        _peeking = false;
        if (_peekWasMask && _maskPreviewShown)
        {
            // 恢复蒙版显示
            var maskPath = _lastMaskPath;
            if (!string.IsNullOrEmpty(maskPath) && File.Exists(maskPath))
            {
                try { PreviewImage.Source = new BitmapImage(new Uri(maskPath)); } catch { }
            }
        }
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


    // 预览图片尺寸确定后:重置缩放比;彩色预览显示中则重对齐/重生成棋盘格
    private void PreviewImage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateCanvasRects();
        if (ChessBg.Visibility == Visibility.Visible)
        {
            _chessBrush = null;   // 尺寸变了:让棋盘按新尺寸重生成
            var cb = EnsureChessBrush();
            ChessBg.Background = cb;
            ChessBg.Visibility = cb != null ? Visibility.Visible : Visibility.Collapsed;
            if (cb != null) ApplyChessPosition();
        }
    }

    private void ToolGrid_ItemDoubleTapped(ImageItem item)
    {
        try
        {
            _previewItem = item;
            _maskPreviewShown = false;
            _peeking = false;
            PreviewHint.Text = "";
            var rawSrc = new BitmapImage(new Uri(item.Path));
            _lastSourceImage = rawSrc;
            PreviewImage.Source = rawSrc;
            PreviewOverlay.Visibility = Visibility.Visible;
        }
        catch (Exception) { }
    }

    private void PreviewClose_Click(object sender, RoutedEventArgs e)
    {
        _previewItem = null;
        PreviewOverlay.Visibility = Visibility.Collapsed;
    }

    // 棋盘格对齐图片显示区域(与 Image Uniform 居中一致:letterbox 不铺棋盘)
    private void ApplyChessPosition()
    {
        try
        {
            var item = _previewItem;
            if (item == null || item.PixelWidth <= 0 || PreviewImage.ActualWidth <= 0) return;
            double s = Math.Min(PreviewImage.ActualWidth / item.PixelWidth, PreviewImage.ActualHeight / item.PixelHeight);
            double w = item.PixelWidth * s, h = item.PixelHeight * s;
            double ox = (PreviewImage.ActualWidth - w) / 2, oy = (PreviewImage.ActualHeight - h) / 2;
            ChessBg.Width = w;
            ChessBg.Height = h;
            ChessBg.HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left;
            ChessBg.VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Top;
            ChessBg.Margin = new Microsoft.UI.Xaml.Thickness(ox, oy, 0, 0);
        }
        catch { }
    }

    // 棋盘格画刷(彩色预览透明背景的标准视觉):按预览区域尺寸整张生成(格子 16px,非平铺——WinUI3 无 TileMode)
    private Microsoft.UI.Xaml.Media.ImageBrush? _chessBrush;
    private Microsoft.UI.Xaml.Media.ImageBrush? EnsureChessBrush()
    {
        try
        {
            var xitem = _previewItem;
            double s = (xitem != null && xitem.PixelWidth > 0 && PreviewImage.ActualWidth > 0)
                ? Math.Min(PreviewImage.ActualWidth / xitem.PixelWidth, PreviewImage.ActualHeight / xitem.PixelHeight)
                : 1.0;
            int w = Math.Max(64, Math.Min(4096, (int)Math.Round((xitem?.PixelWidth ?? 1200) * s)));
            int h = Math.Max(64, Math.Min(4096, (int)Math.Round((xitem?.PixelHeight ?? 900) * s)));
            int s16 = 16;
            using var bmp = new System.Drawing.Bitmap(w, h);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                var c1 = System.Drawing.Color.FromArgb(255, 200, 200, 200);
                var c2 = System.Drawing.Color.FromArgb(255, 255, 255, 255);
                g.Clear(c1);
                using var b1 = new System.Drawing.SolidBrush(c2);
                for (int y = 0; y < h; y += s16)
                    for (int x = 0; x < w; x += s16)
                        if (((x / s16) + (y / s16)) % 2 == 0)
                            g.FillRectangle(b1, x, y, Math.Min(s16, w - x), Math.Min(s16, h - y));
            }
            var png = Path.Combine(Path.GetTempPath(), $"imgup_chess_{Guid.NewGuid():N}.png");
            bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            EngineService.RegisterTempFile(png);
            _chessBrush = new Microsoft.UI.Xaml.Media.ImageBrush
            {
                ImageSource = new BitmapImage(new Uri(png)),
                Stretch = Microsoft.UI.Xaml.Media.Stretch.None,
            };
            return _chessBrush;
        }
        catch { return null; }
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
            ChessBg.Visibility = Visibility.Collapsed;
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
            await CutoutService.PreviewMaskAsync(item.Path, maskPath, modelKey,
                (int)FgSlider.Value, (int)BgSlider.Value, CutoutGpuId,
                null, null, null, null, null, 0, null, 0,
                (int)FeatherSlider.Value, (int)EdgeSlider.Value,
                (int)MorphSlider.Value, AutoThresholdCheck.IsChecked == true);
            // 彩色预览:原图 × mask 透明度合成(主体彩色、背景透明=抠图成片效果)
            var colorPath = Path.Combine(Path.GetTempPath(), $"imgup_mask_color_{Guid.NewGuid():N}.png");
            EngineService.RegisterTempFile(colorPath);
            bool colorOk = false;
            var ff = VideoService.FfmpegPath;
            if (ff != null)
            {
                try
                {
                    await Task.Run(() =>
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = ff,
                            Arguments = $"-y -v error -i \"{item.Path}\" -i \"{maskPath}\" -filter_complex \"[0:v][1:v]alphamerge\" -frames:v 1 \"{colorPath}\"",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardError = true,
                        };
                        using var p = System.Diagnostics.Process.Start(psi);
                        if (p != null) { _ = p.StandardError.ReadToEndAsync(); p.WaitForExit(); }
                    });
                    colorOk = File.Exists(colorPath) && new FileInfo(colorPath).Length > 100;
                }
                catch { }
            }
            try { PreviewImage.Source = new BitmapImage(new Uri(colorOk ? colorPath : maskPath)); } catch { }
            // 彩色预览:主体后垫棋盘格(只垫图片显示区域,letterbox 保持深色背景)
            var cb = colorOk ? EnsureChessBrush() : null;
            ChessBg.Background = cb;
            ChessBg.Visibility = cb != null ? Visibility.Visible : Visibility.Collapsed;
            if (cb != null) ApplyChessPosition();
            _maskPreviewShown = true;
            PreviewHint.Text = colorOk
                ? "抠图预览(彩色:主体保留,背景透明) · 再点一次返回原图 · 调整参数自动刷新"
                : "抠图预览(黑白蒙版) · 再点一次返回原图 · 调整参数自动刷新";
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

    // 框选主体模式开关(与智能涂抹互斥,已废弃功能)

    // (框选主体 / 智能涂抹 / 保存标记 功能已移除 2026-08-29;如未来需要请从 git 历史恢复)

    // 记录预览缩放比(图片显示尺寸/原图像素尺寸)。
    private double _scale;
    private void UpdateCanvasRects()
    {
        var item = _previewItem;
        if (item == null || item.PixelWidth <= 0 || PreviewImage.ActualWidth <= 0)
        {
            _scale = 0;
            return;
        }
        _scale = Math.Min(PreviewImage.ActualWidth / item.PixelWidth, PreviewImage.ActualHeight / item.PixelHeight);
    }

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
            (preUpscale ? ",预处理超分 2x" : ""));
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
                    await CutoutService.CutoutAsync(srcPath, outPath, modelKey,
                        fgThreshold, bgThreshold, featherRadius, edgeStrength, CutoutGpuId,
                        morphStrength: (int)MorphSlider.Value,
                        autoThreshold: AutoThresholdCheck.IsChecked == true,
                        progress: progress, ct: ct);
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
