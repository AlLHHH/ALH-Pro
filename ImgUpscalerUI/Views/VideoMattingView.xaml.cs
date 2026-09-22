using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ALHPro.Views;

/// <summary>视频抠图页(独立板块,2026-09-22)。接线到 `VideoMattingService`,不再有"开发中"占位。
///
/// 【为什么独立成页】用户明确要求:"要的是单独一个界面 视频抠图,原先的还是图片抠图"。
/// 两页共享的只有模型注册表(`CutoutService.Models`)与参数语义;素材类型、任务列表、输出形态完全不同。
///
/// 【界面口径】控件与排版照 `CutoutView` / `VideoView`:同样的 SectionTitle/HintText/分隔线、
/// 同样的 FileOpenPicker + WinRT.Interop.InitializeWithWindow 用法、同样的"按钮禁用-跑完恢复"节奏。
/// 模型默认 = `CutoutService.DefaultModelKey`(按 key 不写下标)、容器默认 = 规格表第一项(MOV/ProRes 4444)。
/// </summary>
public sealed partial class VideoMattingView : UserControl
{
    private readonly string[] _containers = AlhPro.Core.MattingOutputSpecs.TransparentContainers;
    private readonly List<string> _videos = new();
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
        foreach (var c in _containers) ContainerCombo.Items.Add(ContainerLabel(c));
        ContainerCombo.SelectedIndex = 0;

        ApplyModelPresets();
        UpdateLabels();
        UpdateModeVisibility();
        UpdateBgVisibility();
        _ready = true;
    }

    private static string ContainerLabel(string key) => key switch
    {
        "mov" => "MOV · ProRes 4444(推荐,兼容性最好,体积大)",
        "webm" => "WebM · VP9-alpha(体积小,部分软件不认透明)",
        _ => key,
    };

    // ---------- 参数区事件 ----------

    private void ModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        ApplyModelPresets();
    }

    private void OutputMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        UpdateModeVisibility();
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

    private void ApplyModelPresets()
    {
        int i = Math.Clamp(ModelCombo.SelectedIndex, 0, CutoutService.Models.Length - 1);
        var m = CutoutService.Models[i];
        FgSlider.Value = m.FgPreset;
        BgSlider.Value = m.BgPreset;
        FeatherSlider.Value = m.FeatherPreset;
        MorphSlider.Value = m.MorphPreset;
        ModelHint.Text = m.InputSize >= 1024
            ? "该模型输入 1024×1024:边缘更细,但单帧更慢;本机 GPU 实测约 0.26 秒/帧,跑不动时会自动改 CPU。"
            : "该模型输入 320×320:最快,适合长视频;细节略弱于 ISNet。";
        UpdateLabels();
    }

    private void UpdateLabels()
    {
        FgLabel.Text = $"前景阈值 {FgSlider.Value:0}";
        BgLabel.Text = $"背景阈值 {BgSlider.Value:0}";
        FeatherLabel.Text = $"边缘羽化 {FeatherSlider.Value:0}";
        MorphLabel.Text = $"形态学清洗 {MorphSlider.Value:0}";
        StabilityLabel.Text = $"稳定强度 {StabilitySlider.Value:0}";
    }

    private void UpdateModeVisibility()
    {
        bool transparent = ModeAlphaRadio.IsChecked == true;
        BgPanel.Visibility = transparent ? Visibility.Collapsed : Visibility.Visible;
        ContainerPanel.Visibility = transparent ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateBgVisibility()
    {
        bool image = BgImageRadio.IsChecked == true;
        BgColorRow.Visibility = image ? Visibility.Collapsed : Visibility.Visible;
        BgImageBtn.Visibility = image ? Visibility.Visible : Visibility.Collapsed;
        BgImageInfo.Visibility = image ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- 选素材 / 选输出目录 / 选背景图 ----------

    private async void PickBtn_Click(object sender, RoutedEventArgs e)
    {
        AppLogger.UserAction("视频抠图:点击「添加视频」");
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        foreach (var ext in new[] { ".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v", ".wmv", ".ts" })
            picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var files = await picker.PickMultipleFilesAsync();
        if (files == null || files.Count == 0) return;
        _videos.Clear();
        foreach (var f in files) _videos.Add(f.Path);
        FileInfo.Text = _videos.Count == 1
            ? System.IO.Path.GetFileName(_videos[0])
            : $"已添加 {_videos.Count} 个视频";
        Log($"添加 {_videos.Count} 个视频");
    }

    private async void OutDir_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;
        _outDir = folder.Path;
        OutDirText.Text = _outDir;
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

    // ---------- 开始 / 取消 ----------

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_videos.Count == 0)
        {
            ProgressText.Text = "请先添加视频。";
            return;
        }
        if (_cts != null) return;

        _cts = new CancellationTokenSource();
        SetRunning(true);
        var ct = _cts.Token;
        var progress = new Progress<(int pct, string msg)>(p =>
        {
            TaskBar.Value = Math.Clamp(p.pct, 0, 100);
            ProgressText.Text = p.msg;
        });

        try
        {
            foreach (var video in _videos.ToList())
            {
                ct.ThrowIfCancellationRequested();
                Log($"开始:{System.IO.Path.GetFileName(video)}");
                var req = BuildRequest(video);
                var result = await Task.Run(() => VideoMattingService.RunAsync(req, progress, ct), ct);
                TaskBar.Value = 100;
                Log($"完成:{result.OutputPath}");
                Log($"  帧数 {result.Frames} · 用时 {result.ElapsedSec:0.#} 秒 · 设备 {result.Device}");
                if (!string.IsNullOrWhiteSpace(result.Notes)) Log($"  {result.Notes}");
                ProgressText.Text = "处理完成:" + System.IO.Path.GetFileName(result.OutputPath);
            }
        }
        catch (OperationCanceledException)
        {
            ProgressText.Text = "已取消(临时文件已清理)。";
            Log("用户取消");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"视频抠图失败 HRESULT=0x{ex.HResult:X8}", ex);
            ProgressText.Text = "处理失败:" + ex.Message;
            Log("失败:" + ex.Message);
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
        Log("已请求取消,等当前帧处理完就停...");
    }

    private void SetRunning(bool running)
    {
        StartBtn.IsEnabled = !running;
        CancelBtn.IsEnabled = running;
        PickBtn.IsEnabled = !running;
        OutDirBtn.IsEnabled = !running;
        ModeBgRadio.IsEnabled = !running;
        ModeAlphaRadio.IsEnabled = !running;
    }

    private VideoMattingRequest BuildRequest(string video) => new(
        Input: video,
        OutDir: _outDir,
        ModelKey: CutoutService.Models[Math.Clamp(ModelCombo.SelectedIndex, 0, CutoutService.Models.Length - 1)].Key,
        GpuId: AppSettings.GpuIndex,
        Transparent: ModeAlphaRadio.IsChecked == true,
        Container: _containers[Math.Clamp(ContainerCombo.SelectedIndex, 0, _containers.Length - 1)],
        BackgroundPath: BgImageRadio.IsChecked == true ? _bgImage : "",
        BackgroundColor: BgColorBox.Text,
        Fg: (int)FgSlider.Value,
        Bg: (int)BgSlider.Value,
        Feather: (int)FeatherSlider.Value,
        Edge: 0,
        Morph: (int)MorphSlider.Value,
        Stability: (int)StabilitySlider.Value);

    private void Log(string line)
    {
        LogBox.Text += (LogBox.Text.Length == 0 ? "" : Environment.NewLine) + $"[{DateTime.Now:HH:mm:ss}] {line}";
        LogBox.SelectionStart = LogBox.Text.Length;
    }
}
