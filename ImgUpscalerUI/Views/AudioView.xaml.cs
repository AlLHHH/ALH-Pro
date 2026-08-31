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
    private Windows.Media.Playback.MediaPlayer? _mediaPlayer;
    private Windows.Foundation.TypedEventHandler<Windows.Media.Playback.MediaPlaybackSession, object>? _previewHandler;

    /// <summary>状态变化(底部状态栏显示)。</summary>
    public event Action<string>? StatusChanged;

    public AudioView()
    {
        this.InitializeComponent();
        _mediaPlayer = new Windows.Media.Playback.MediaPlayer();
        _mediaPlayer.AutoPlay = false;   // 打开预览不自动播,由用户点 ▶
        _mediaPlayer.Volume = _lastVolume;   // 恢复上次音量
        _mediaPlayer.MediaOpened += (s, _) =>
        {
            if (_previewItem != null && _previewItem.TrimStart > 0.1 && s.PlaybackSession.CanSeek)
                s.PlaybackSession.Position = TimeSpan.FromSeconds(_previewItem.TrimStart);
        };
        _playStateHandler = PlayStateChanged;
        UpdateRunState();
    }

    // ---------- 音量(滑动条,拖到最左=静音) ----------
    private double _lastVolume = 1.0;   // 0~1

    private void VolSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_mediaPlayer == null) return;
        var v = e.NewValue / 100.0;
        _lastVolume = v;
        _mediaPlayer.Volume = v;
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
        if (_previewItem != null && selected.Contains(_previewItem)) ClosePreview();
        UpdateListButtons();
        AudioInfo.Text = _items.Count == 0 ? "未选择音频" : $"{_items.Count} 个音频";
        Log($"删除了 {selected.Length} 个音频(列表剩 {_items.Count} 个)");
    }

    private void ClearAudio_Click(object sender, RoutedEventArgs e)
    {
        if (_running) { Log("处理中不能清空(可先暂停/强制结束)"); return; }
        _items.Clear();
        AudioList.Items.Clear();
        ClosePreview();
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
    private void AudioList_SelectionChanged(object sender, SelectionChangedEventArgs e)
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
            _previewItem = it;
            PreviewPanel.Visibility = Visibility.Visible;   // 双击展开预览区
            var (dur, ch, sampleRate) = AudioService.Probe(it.Path);
            it.DurationSec = (float)(dur > 0 ? dur : it.DurationSec);
            PreviewName.Text = $"{it.Name}";
            PreviewMeta.Text = $"{FormatTime(it.DurationSec)} · {sampleRate}Hz · {(ch == 1 ? "单声道" : ch == 2 ? "立体声" : $"{ch} 声道")}";
            // 波形:解码 → 绘制
            var samples = await AudioService.DecodeWaveformAsync(it.Path, 1200);
            _waveSamples = samples;
            DrawWaveform();
            // 裁剪:把手显示(拖动),不再是滑条
            it.TrimEnd = it.TrimEnd > 0.1 ? it.TrimEnd : it.DurationSec;
            TrimStartThumb.Visibility = Visibility.Visible;
            TrimEndThumb.Visibility = Visibility.Visible;
            UpdateTrimUI();
            // 播放:自绘控制(纯 MediaPlayer,不再用 MediaPlayerElement 自带控件)
            if (_mediaPlayer != null)
            {
                var mp = _mediaPlayer;
                _previewHandler = (s, _) =>
                {
                    try
                    {
                        var pos = s.Position.TotalSeconds;
                        var end = it.TrimEnd > 0.1 && it.DurationSec > 0 ? it.TrimEnd : 0;
                        if (end > 0.1 && pos >= end - 0.05)
                            mp.Pause();
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            UpdatePlayLine(pos);
                            TimeText.Text = $"{FormatTime(pos)} / {FormatTime(it.DurationSec)}";
                        });
                    }
                    catch { }
                };
                mp.Source = MediaSource.CreateFromUri(new Uri(it.Path));
                mp.PlaybackSession.PositionChanged += _previewHandler;
                mp.PlaybackSession.PlaybackStateChanged += _playStateHandler;
                if (it.TrimStart > 0.1)
                    mp.PlaybackSession.Position = TimeSpan.FromSeconds(it.TrimStart);
                TimeText.Text = $"0:00 / {FormatTime(it.DurationSec)}";
            }
        }
        catch { }
    }

    private void PlayStateChanged(Windows.Media.Playback.MediaPlaybackSession s, object _)
    {
        var playing = s.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing;
        DispatcherQueue.TryEnqueue(() => PlayBtn.Content = playing ? "⏸" : "▶");
    }

    private readonly Windows.Foundation.TypedEventHandler<Windows.Media.Playback.MediaPlaybackSession, object> _playStateHandler;

    private void PlayBtn_Click(object sender, RoutedEventArgs e)
    {
        var mp = _mediaPlayer;
        if (mp == null) return;
        if (mp.PlaybackSession.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing)
        {
            mp.Pause();
            PlayBtn.Content = "▶";
        }
        else
        {
            var it = _previewItem;
            var pos = mp.PlaybackSession.Position.TotalSeconds;
            // 没在播:若已到裁剪末尾或起点之后,回到起点再听(不然点播放没反应)
            if (it != null)
            {
                var end = it.TrimEnd > 0.1 && it.DurationSec > 0 ? it.TrimEnd : 0;
                if ((end > 0.1 && pos >= end - 0.05) || pos < it.TrimStart - 0.1)
                    mp.PlaybackSession.Position = TimeSpan.FromSeconds(it.TrimStart > 0.1 ? it.TrimStart : 0);
            }
            mp.Play();
            PlayBtn.Content = "⏸";
        }
    }

    private void ResetBtn_Click(object sender, RoutedEventArgs e)
    {
        var mp = _mediaPlayer;
        if (mp == null || _previewItem == null) return;
        // 再听一次 = 从头(0:00)开始播放
        mp.PlaybackSession.Position = TimeSpan.Zero;
        mp.Play();
        PlayBtn.Content = "⏸";
        UpdatePlayLine(0);
    }

    // ---------- 波形绘制 ----------
    private float[] _waveSamples = Array.Empty<float>();

    private void WaveCanvas_SizeChanged(object sender, SizeChangedEventArgs e) { DrawWaveform(); UpdateTrimUI(); }

    private void DrawWaveform()
    {
        if (WaveCanvas == null || _waveSamples.Length == 0) return;
        var w = WaveCanvas.ActualWidth;
        var h = WaveCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;
        WaveCanvas.Children.Clear();
        int n = _waveSamples.Length;
        for (int i = 0; i < n; i++)
        {
            var rect = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = Math.Max(1, w / n - 0.5),
                Height = Math.Max(1, _waveSamples[i] * h * 0.9),
                Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 120, 160, 220)),
            };
            Canvas.SetLeft(rect, i * (w / n));
            Canvas.SetTop(rect, (h - rect.Height) / 2);
            WaveCanvas.Children.Add(rect);
        }
    }

    // ---------- 裁剪把手(拖动首尾,与视频页时间线一致) ----------
    private bool _dragStartThumb, _dragEndThumb;

    private void TrimThumb_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _dragStartThumb = ReferenceEquals(sender, TrimStartThumb);
        _dragEndThumb = ReferenceEquals(sender, TrimEndThumb);
        ((UIElement)sender).CapturePointer(e.Pointer);
        UpdateTrimFromPointer(e);
        e.Handled = true;
    }

    private void TrimThumb_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_dragStartThumb || _dragEndThumb)
        {
            UpdateTrimFromPointer(e);
            e.Handled = true;
        }
    }

    private void TrimThumb_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _dragStartThumb = false;
        _dragEndThumb = false;
        try { ((UIElement)sender).ReleasePointerCapture(e.Pointer); } catch { }
    }

    private void UpdateTrimFromPointer(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_previewItem == null || _previewItem.DurationSec <= 0) return;
        var w = WaveHost.ActualWidth;
        if (w <= 0) return;
        var sec = Math.Clamp(e.GetCurrentPoint(WaveHost).Position.X / w * _previewItem.DurationSec,
            0, _previewItem.DurationSec);
        if (_dragStartThumb)
            _previewItem.TrimStart = Math.Min(sec, Math.Max(0, _previewItem.TrimEnd - 0.1));
        else if (_dragEndThumb)
            _previewItem.TrimEnd = Math.Max(sec, Math.Min(_previewItem.DurationSec, _previewItem.TrimStart + 0.1));
        UpdateTrimUI();
    }

    private void UpdateTrimUI()
    {
        if (_previewItem == null || _previewItem.DurationSec <= 0 || WaveHost.ActualWidth <= 0) return;
        var w = WaveHost.ActualWidth;
        var sx = Math.Clamp(_previewItem.TrimStart / _previewItem.DurationSec * w, 0, w);
        var ex = Math.Clamp(_previewItem.TrimEnd / _previewItem.DurationSec * w, 0, w);
        // 把手中心对准裁剪边界(半宽 5,贴边时不会露出)
        TrimStartThumb.Margin = new Thickness(Math.Clamp(sx - 5, 0, Math.Max(0, w - 10)), 0, 0, 0);
        TrimEndThumb.Margin = new Thickness(Math.Clamp(ex - 5, 0, Math.Max(0, w - 10)), 0, 0, 0);
        TrimRange.Margin = new Thickness(sx, 0, 0, 0);
        TrimRange.Width = Math.Max(0, ex - sx);
        TrimRange.Visibility = Visibility.Visible;
        TrimHint.Text = $"裁剪: {FormatTime(_previewItem.TrimStart)} ~ {FormatTime(_previewItem.TrimEnd)} / 总 {FormatTime(_previewItem.DurationSec)}";
    }

    // ---------- 波形点击:拖动调整播放位置 ----------
    private bool _waveSeeking;
    private double _mutedVolumeBeforeSeek;

    private void WaveHost_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_previewItem == null || _previewItem.DurationSec <= 0) return;
        _waveSeeking = true;
        // 拖动进度条时静音,防止边拖边播的刮擦声;松手恢复
        _mutedVolumeBeforeSeek = _mediaPlayer?.Volume ?? 0;
        if (_mediaPlayer != null) _mediaPlayer.Volume = 0;
        WaveHost.CapturePointer(e.Pointer);
        SeekWave(e);
        e.Handled = true;
    }

    private void WaveHost_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_waveSeeking) return;
        SeekWave(e);
        e.Handled = true;
    }

    private void WaveHost_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _waveSeeking = false;
        try
        {
            WaveHost.ReleasePointerCapture(e.Pointer);
            if (_mediaPlayer != null) _mediaPlayer.Volume = _mutedVolumeBeforeSeek;   // 恢复音量
        }
        catch { }
    }

    private void SeekWave(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_previewItem == null || _previewItem.DurationSec <= 0 || WaveHost.ActualWidth <= 0) return;
        var sec = Math.Clamp(e.GetCurrentPoint(WaveHost).Position.X / WaveHost.ActualWidth * _previewItem.DurationSec,
            0, _previewItem.DurationSec);
        try
        {
            if (_mediaPlayer != null)
                _mediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(sec);
        }
        catch { }
        UpdatePlayLine(sec);
    }

    private void UpdatePlayLine(double pos)
    {
        if (_previewItem == null || _previewItem.DurationSec <= 0 || WaveHost == null) return;
        var w = WaveHost.ActualWidth;
        var x = w * pos / _previewItem.DurationSec;
        PlayLine.Visibility = Visibility.Visible;
        PlayLine.Margin = new Thickness(x, 0, 0, 0);
    }

    private void RemovePreviewHandler()
    {
        if (_previewHandler != null && _mediaPlayer != null)
        {
            try { _mediaPlayer.PlaybackSession.PositionChanged -= _previewHandler; } catch { }
            try { _mediaPlayer.PlaybackSession.PlaybackStateChanged -= _playStateHandler; } catch { }
            _previewHandler = null;
        }
    }

    private void ClosePreview()
    {
        RemovePreviewHandler();
        if (_mediaPlayer != null)
        {
            try { _mediaPlayer.Pause(); _mediaPlayer.Source = null; } catch { }
        }
        _previewItem = null;
        PlayBtn.Content = "▶";
        PreviewPanel.Visibility = Visibility.Collapsed;
    }

    private void PreviewCloseBtn_Click(object sender, RoutedEventArgs e) => ClosePreview();

    // ---------- 裁剪把手(波形上拖首尾)见上方 ----------

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
