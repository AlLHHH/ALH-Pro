using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Core;
using Windows.Storage.Pickers;

namespace ALHPro.Views;

public sealed partial class AudioView : UserControl
{
    public sealed class AudioItem
    {
        public string Path { get; set; } = "";
        public string Name => System.IO.Path.GetFileName(Path);
        public string Display { get; set; } = "";
        public bool IsDone { get; set; }
        public string Status { get; set; } = "";

        // 预览/裁剪
        public float DurationSec { get; set; }
        public double TrimStart { get; set; }
        public double TrimEnd { get; set; }   // >0 表示设置;0=到结尾
        public bool IsTrimmed => TrimStart > 0.1 || (DurationSec > 0 && TrimEnd > 0.1 && TrimEnd < DurationSec - 0.1);
    }

    private readonly List<AudioItem> _items = new();
    private bool _running;
    private CancellationTokenSource? _cts;
    private AudioItem? _previewItem;
    private Windows.Foundation.TypedEventHandler<Windows.Media.Playback.MediaPlaybackSession, object>? _previewHandler;

    /// <summary>状态变化(底部状态栏显示)。</summary>
    public event Action<string>? StatusChanged;

    public AudioView()
    {
        this.InitializeComponent();
        UpdateRunState();
    }

    private void Log(string msg) { LogText.Text = msg; StatusChanged?.Invoke(msg); }
    public static string FormatTime(double s) => s <= 0 ? "0:00" : $"{(int)s / 60}:{(int)s % 60:00}";

    private void UpdateRunState()
    {
        RunBtn.IsEnabled = _items.Count > 0 && !_running;
        CancelBtn.IsEnabled = _running;
        RunBtn.Content = _running ? "处理中..." : "开始处理";
    }

    private void Options_Changed(object sender, RoutedEventArgs e) { }

    // ---------- Delete 键删除选中 ----------
    private void AudioList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Delete)
        {
            e.Handled = true;
            if (_running) { Log("处理中不能删除(可先暂停/强制结束)"); return; }
            RemoveAudio_Click(sender, new RoutedEventArgs());
        }
    }

    private void RemoveAudio_Click(object sender, RoutedEventArgs e)
    {
        if (_running) { Log("处理中不能删除(可先暂停/强制结束)"); return; }
        var selected = AudioList.SelectedItems.Cast<AudioItem>().ToArray();
        if (selected.Length == 0) return;
        foreach (var it in selected)
        {
            _items.Remove(it);
            AudioList.Items.Remove(it);
        }
        UpdateListButtons();
        AudioInfo.Text = _items.Count == 0 ? "未选择音频" : $"{_items.Count} 个音频";
        Log($"删除了 {selected.Length} 个音频(列表剩 {_items.Count} 个)");
    }

    private void ClearAudio_Click(object sender, RoutedEventArgs e)
    {
        if (_running) { Log("处理中不能清空(可先暂停/强制结束)"); return; }
        _items.Clear();
        AudioList.Items.Clear();
        UpdateListButtons();
        AudioInfo.Text = "未选择音频";
        Log($"清空了音频列表");
    }

    private void ClearDoneAudio_Click(object sender, RoutedEventArgs e)
    {
        if (_running) { Log("处理中不能清除(可先暂停/强制结束)"); return; }
        var done = _items.Where(it => it.IsDone).ToArray();
        foreach (var it in done)
        {
            _items.Remove(it);
            AudioList.Items.Remove(it);
        }
        UpdateListButtons();
        AudioInfo.Text = _items.Count == 0 ? "未选择音频" : $"{_items.Count} 个音频";
        Log($"清除了 {done.Length} 个已完成");
    }

    private void UpdateListButtons()
    {
        bool hasList = _items.Count > 0;
        bool hasSel = AudioList.SelectedItems.Count > 0;
        RemoveAudioBtn.IsEnabled = hasSel && !_running;
        ClearAudioBtn.IsEnabled = hasList && !_running;
        ClearDoneAudioBtn.IsEnabled = _items.Any(it => it.IsDone) && !_running;
        UpdateRunState();
    }

    // ---------- 鼠标框选(橡皮筋,与视频/图片页一致) ----------
    private const double RbThreshold = 4;
    private bool _rbBanding;
    private bool _rbMoved;
    private Windows.Foundation.Point _rbStart;

    private void AudioListHost_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (IsPressOnAudioItem(e.GetCurrentPoint(AudioListHost).Position)) return;
        _rbBanding = true;
        _rbMoved = false;
        _rbStart = e.GetCurrentPoint(AudioListHost).Position;
        RbRectA.Visibility = Visibility.Visible;
        RbRectA.Width = 0;
        RbRectA.Height = 0;
        Canvas.SetLeft(RbRectA, _rbStart.X);
        Canvas.SetTop(RbRectA, _rbStart.Y);
        AudioListHost.CapturePointer(e.Pointer);
    }

    private bool IsPressOnAudioItem(Windows.Foundation.Point pt)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            if (AudioList.ContainerFromIndex(i) is FrameworkElement c && c.ActualWidth > 0)
            {
                var tl = c.TransformToVisual(AudioListHost).TransformPoint(new Windows.Foundation.Point(0, 0));
                var r = new Windows.Foundation.Rect(tl.X, tl.Y, c.ActualWidth, c.ActualHeight);
                if (r.Contains(pt)) return true;
            }
        }
        return false;
    }

    private void AudioListHost_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_rbBanding) return;
        var cur = e.GetCurrentPoint(AudioListHost).Position;
        if (!_rbMoved && Math.Abs(cur.X - _rbStart.X) < RbThreshold && Math.Abs(cur.Y - _rbStart.Y) < RbThreshold)
            return;
        _rbMoved = true;
        UpdateRbRectA(cur);
    }

    private void AudioListHost_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_rbBanding) return;
        _rbBanding = false;
        AudioListHost.ReleasePointerCapture(e.Pointer);
        if (_rbMoved)
        {
            UpdateRbRectA(e.GetCurrentPoint(AudioListHost).Position);
            ApplyRubberSelectionA();
        }
        else
        {
            AudioList.SelectedItems.Clear();
            UpdateListButtons();
        }
        RbRectA.Visibility = Visibility.Collapsed;
    }

    private void AudioListHost_PointerCaptureLost(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _rbBanding = false;
        RbRectA.Visibility = Visibility.Collapsed;
    }

    private void UpdateRbRectA(Windows.Foundation.Point cur)
    {
        double x = Math.Min(_rbStart.X, cur.X);
        double y = Math.Min(_rbStart.Y, cur.Y);
        Canvas.SetLeft(RbRectA, x);
        Canvas.SetTop(RbRectA, y);
        RbRectA.Width = Math.Abs(cur.X - _rbStart.X);
        RbRectA.Height = Math.Abs(cur.Y - _rbStart.Y);
        RbRectA.Visibility = RbRectA.Width > 2 && RbRectA.Height > 2
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyRubberSelectionA()
    {
        var rect = new Windows.Foundation.Rect(Canvas.GetLeft(RbRectA), Canvas.GetTop(RbRectA),
            RbRectA.Width, RbRectA.Height);
        if (rect.Width < 2 || rect.Height < 2) return;
        AudioList.SelectedItems.Clear();
        for (int i = 0; i < _items.Count; i++)
        {
            if (AudioList.ContainerFromIndex(i) is FrameworkElement c)
            {
                var tf = c.TransformToVisual(AudioListHost);
                var topLeft = tf.TransformPoint(new Windows.Foundation.Point(0, 0));
                var itemRect = new Windows.Foundation.Rect(topLeft.X, topLeft.Y, c.ActualWidth, c.ActualHeight);
                if (RectIntersects(itemRect, rect))
                    AudioList.SelectedItems.Add(_items[i]);
            }
        }
        UpdateListButtons();
    }

    private bool RectIntersects(Windows.Foundation.Rect a, Windows.Foundation.Rect b)
        => a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;

    // ---------- 拖拽添加 ----------
    private void DropAudio_DragOver(object sender, DragEventArgs e)
        => e.AcceptedOperation = DataPackageOperation.Copy;

    private async void DropAudio_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var files = items.OfType<Windows.Storage.StorageFile>()
                .Where(f => IsAudioExt(f.Path))
                .Select(f => f.Path).ToArray();
            if (files.Length > 0)
                AddFiles(files);
            else
                Log("拖入的文件不是支持的音频格式");
        }
    }

    private static bool IsAudioExt(string p) => new[] { ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg", ".opus", ".wma" }
        .Any(ext => p.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    private void AddFiles(string[] paths)
    {
        foreach (var p in paths)
        {
            var it = new AudioItem { Path = p };
            it.Display = it.Name;
            // 探测时长(用于裁剪滑块与展示)
            var (dur, _, _) = AudioService.Probe(p);
            it.DurationSec = (float)dur;
            _items.Add(it);
            AudioList.Items.Add(it);
        }
        AudioInfo.Text = $"{_items.Count} 个音频";
        Log($"已添加 {paths.Length} 个音频");
        UpdateRunState();
    }

    // ---------- 选择文件 ----------
    private async void PickAudioBtn_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeFilter.Add(".mp3");
        picker.FileTypeFilter.Add(".wav");
        picker.FileTypeFilter.Add(".flac");
        picker.FileTypeFilter.Add(".aac");
        picker.FileTypeFilter.Add(".m4a");
        picker.FileTypeFilter.Add(".ogg");
        picker.FileTypeFilter.Add(".opus");
        picker.FileTypeFilter.Add(".wma");
        var files = await picker.PickMultipleFilesAsync();
        if (files == null || files.Count == 0) return;
        AddFiles(files.Select(f => f.Path).ToArray());
    }

    // ---------- 预览/裁剪(双击才展开) ----------
    private async void AudioList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 单击只更新选中状态;预览区保留(双击会打开;这里不清除以免闪)
    }

    private void AudioList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement fe && fe.DataContext is AudioItem item)
            _ = ShowPreviewAsync(item);
    }

    private async Task ShowPreviewAsync(AudioItem it)
    {
        try
        {
            RemovePreviewHandler();
            PreviewPanel.Visibility = Visibility.Visible;   // 双击展开预览区
            PreviewPlayer.Source = MediaSource.CreateFromUri(new Uri(it.Path));
            PreviewName.Text = $"{it.Name} · {FormatTime(it.DurationSec)}";
            // 裁剪滑块:0~时长;默认终点=时长
            TrimStartSlider.Maximum = Math.Max(1, it.DurationSec);
            TrimEndSlider.Maximum = Math.Max(1, it.DurationSec);
            TrimStartSlider.Value = it.TrimStart;
            TrimEndSlider.Value = it.TrimEnd > 0.1 ? it.TrimEnd : it.DurationSec;
            TrimRow.Visibility = Visibility.Visible;
            TrimHint.Text = $"裁剪: {FormatTime(it.TrimStart)} ~ {FormatTime(it.TrimEnd > 0.1 ? it.TrimEnd : it.DurationSec)}";
            // 到 TrimEnd 自动暂停
            if (PreviewPlayer.MediaPlayer != null)
            {
                var mp = PreviewPlayer.MediaPlayer;
                _previewHandler = (s, _) =>
                {
                    try
                    {
                        var pos = s.Position.TotalSeconds;
                        var end = it.TrimEnd > 0.1 && it.DurationSec > 0 ? it.TrimEnd : 0;
                        if (end > 0.1 && pos >= end - 0.05)
                            s.PlaybackRate = 0;   // 到终点暂停
                    }
                    catch { }
                };
                mp.PlaybackSession.PositionChanged += _previewHandler;
                if (it.TrimStart > 0.1)
                    mp.PlaybackSession.Position = TimeSpan.FromSeconds(it.TrimStart);
            }
        }
        catch { }
    }

    private void RemovePreviewHandler()
    {
        if (_previewHandler != null && PreviewPlayer.MediaPlayer != null)
        {
            try { PreviewPlayer.MediaPlayer.PlaybackSession.PositionChanged -= _previewHandler; } catch { }
            _previewHandler = null;
        }
    }

    private void TrimStartSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_previewItem == null || TrimStartSlider == null || TrimEndSlider == null) return;
        if (TrimStartSlider.Value > TrimEndSlider.Value)
            TrimStartSlider.Value = TrimEndSlider.Value;
        _previewItem.TrimStart = TrimStartSlider.Value;
        TrimHint.Text = $"裁剪: {FormatTime(TrimStartSlider.Value)} ~ {FormatTime(TrimEndSlider.Value)}";
    }

    private void TrimEndSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_previewItem == null || TrimStartSlider == null || TrimEndSlider == null) return;
        if (TrimEndSlider.Value < TrimStartSlider.Value)
            TrimEndSlider.Value = TrimStartSlider.Value;
        _previewItem.TrimEnd = TrimEndSlider.Value;
        TrimHint.Text = $"裁剪: {FormatTime(TrimStartSlider.Value)} ~ {FormatTime(TrimEndSlider.Value)}";
    }

    // ---------- 开始处理 ----------
    private async void RunBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_running || _items.Count == 0) return;
        _running = true;
        _cts = new CancellationTokenSource();
        UpdateRunState();
        int total = _items.Count, done = 0, fail = 0;
        try
        {
            foreach (var item in _items)
            {
                if (_cts.IsCancellationRequested) break;
                Log($"处理中: {item.Name}");
                StatusChanged?.Invoke($"音频处理 {done + 1}/{total}: {item.Name}");
                try
                {
                    var outFmt = FmtRadios.SelectedIndex;   // 0=MP3 1=WAV 2=FLAC
                    var ext = outFmt == 0 ? ".mp3" : outFmt == 1 ? ".wav" : ".flac";
                    var outPath = System.IO.Path.Combine(
                        System.IO.Path.GetDirectoryName(item.Path)!,
                        System.IO.Path.GetFileNameWithoutExtension(item.Path) + "_增强" + ext);
                    await AudioService.EnhanceAsync(item.Path, outPath,
                        DenoiseRadios.SelectedIndex,
                        LoudnessCheck.IsChecked == true,
                        LowcutCheck.IsChecked == true,
                        EqCheck.IsChecked == true,
                        outFmt, 192, null,
                        new Progress<(int pct, string msg)>(t =>
                        {
                            AudioProgress.Value = t.pct;
                            AudioStatus.Text = t.msg;
                        }), _cts.Token,
                        item.TrimStart > 0.1 ? item.TrimStart : 0,
                        (item.TrimEnd > 0.1 && item.DurationSec > 0.2) ? item.TrimEnd : 0);
                    item.IsDone = true;
                    item.Status = "✅ 完成";
                    item.Display = item.Name + "  (已增强)";
                    done++;
                }
                catch (OperationCanceledException)
                {
                    Log("⚠ 已取消");
                    break;
                }
                catch (Exception ex)
                {
                    fail++;
                    item.Status = "❌ 失败:" + ex.Message.Split('\n')[0];
                    item.Display = item.Name + "  (失败)";
                    Log($"失败: {item.Name} — {ex.Message.Split('\n')[0]}");
                    AppLogger.Error($"音频处理失败: {item.Name}", ex);
                }
                AudioProgress.Value = (int)(done * 100.0 / total);
            }
        }
        finally
        {
            _running = false;
            _cts?.Dispose();
            _cts = null;
            UpdateRunState();
            Log($"任务结束:成功 {done},失败 {fail},共 {total}");
            StatusChanged?.Invoke($"音频处理完成:成功 {done} 失败 {fail}");
        }
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        Log("用户点击「强制结束」,正在停止...");
        _cts?.Cancel();
    }
}
