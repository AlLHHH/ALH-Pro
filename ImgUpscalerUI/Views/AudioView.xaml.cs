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
    /// <summary>音频页设置(升采样率/降噪/AI分离/响亮/低切/清晰/输出格式),存 %LOCALAPPDATA%\ALHPro\settings\audio-settings.json。</summary>
    public class AudioSettings
    {
        public int Sr { get; set; } = 0;           // 0关 1柔和 2标准 3强力(增强:人声/伴奏分别优化重混)
        public bool Srs { get; set; }              // 采样率修复(AI 升采样率,低采样率源→48k)
        public int Denoise { get; set; } = 0;        // 0关 1弱 2中 3强
        public int Demucs { get; set; } = 0;         // 0关 1人声 2去人声 3分离 4自定义组合
        public double VolV { get; set; } = 100;      // 自定义组合:人声音量 %
        public double VolA { get; set; } = 100;      // 自定义组合:伴奏音量 %
        public double VolO1 { get; set; } = 100;
        public double VolO2 { get; set; } = 100;
        public bool Loudness { get; set; }
        public bool Lowcut { get; set; }
        public bool Eq { get; set; }
        public int OutputFmt { get; set; } = 0;      // 0=MP3 1=WAV 2=FLAC
    }

    private static string SettingsFile => ParaPaths.SettingsFile("audio-settings.json");
    private System.Collections.Generic.List<string> _allOutputs = new();   // 本次任务输出文件(完成弹窗用)

    /// <summary>唯一化输出路径:同名时自动加 (1)/(2)... 不覆盖(与图片/视频页一致)。</summary>
    public static string UniquePath(string dir, string fileName)
    {
        var candidate = Path.Combine(dir, fileName);
        if (!File.Exists(candidate)) return candidate;
        var ext = Path.GetExtension(fileName);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        for (int i = 2; ; i++)
        {
            candidate = Path.Combine(dir, $"{baseName} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
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
        LoadSettings();   // 恢复上次:降噪/AI分离/响亮/低切/清晰/输出格式
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

    private void Log(string msg)
    {
        AppLogger.Info(msg);   // 同步写诊断日志文件(与视频页一致)
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        LogText.Text = LogText.Text == "日志:等待任务..."
            ? line : LogText.Text + "\n" + line;
        var lines = LogText.Text.Split('\n');
        if (lines.Length > 200)
            LogText.Text = string.Join("\n", lines.Skip(80)) + "\n";
        try { LogScroll?.ChangeView(null, LogScroll.ScrollableHeight, null, true); } catch { }
        StatusChanged?.Invoke(msg);
    }
    public static string FormatTime(double s) => s <= 0 ? "0:00" : $"{(int)s / 60}:{(int)s % 60:00}";

    private void UpdateRunState()
    {
        RunBtn.IsEnabled = _items.Count > 0 && !_running;
        CancelBtn.IsEnabled = _running;
        RunBtn.Content = _running ? "处理中..." : "开始处理";
    }

    private void Options_Changed(object sender, RoutedEventArgs e)
    {
        // 「自定义组合」选中 → 显示轨道勾选 + 音量滑块面板
        if (CustomMixPanel != null)
            CustomMixPanel.Visibility = DemucsRadios.SelectedIndex == 4
                ? Visibility.Visible : Visibility.Collapsed;
        // 升采样率提示:效果——勾选后作用到最终输出(不单独出文件)
        bool srs = SrsCheck?.IsChecked == true;
        if (SrsHint != null)
        {
            SrsHint.Text = srs
                ? "已勾选:低采样率源(8k/16k/22k/32k)将升级到 48kHz,效果作用到最终输出;44.1k 全频带源会自动跳过"
                : (LavaSrService.Available()
                    ? "仅低采样率源(8k/16k/22k/32k)有效;44.1k 音乐已全频带,无需升采样率(用下方「增强」)"
                    : "升采样率模型未安装(需 engines/lavasr),仅低采样率源有效");
        }
        // 自定义组合提示:最终只输出 1 个混合文件(按音量);升采样率/增强作为效果并入
        if (CmHint != null && CustomMixPanel != null && CustomMixPanel.Visibility == Visibility.Visible)
        {
            bool accC = CmAcc.IsChecked == true;
            bool subC = CmOther1.IsChecked == true || CmOther2.IsChecked == true;
            CmHint.Text = "按各轨音量混合,最终输出 1 个「_自定义」文件(勾选的升采样率/增强一并作用)"
                + (accC && subC
                    ? "\n⚠ 伴奏=去掉人声的全部音乐(鼓/贝斯都在里面),同时勾「其他」= 那部分被算了两次;想单独提取某一块,只勾那个即可"
                    : (accC ? "\n(伴奏已含鼓/贝斯等全部乐器)" : ""));
        }
        SaveSettings();
    }

    /// <summary>自定义组合:轨道音量滑块(0~200%,100%=原音量)。</summary>
    private void CmVol_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        TextBlock? t = null;
        if (ReferenceEquals(sender, CmVolV)) t = CmVolVText;
        else if (ReferenceEquals(sender, CmVolA)) t = CmVolAText;
        else if (ReferenceEquals(sender, CmVolO1)) t = CmVolO1Text;
        else if (ReferenceEquals(sender, CmVolO2)) t = CmVolO2Text;
        if (t != null) t.Text = $"{(int)e.NewValue}%";
        SaveSettings();
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsFile)) return;
            var d = System.Text.Json.JsonSerializer.Deserialize<AudioSettings>(File.ReadAllText(SettingsFile));
            if (d is null) return;
            SrRadios.SelectedIndex = Math.Clamp(d.Sr, 0, 3);
            if (SrsCheck != null) SrsCheck.IsChecked = d.Srs;
            DenoiseRadios.SelectedIndex = Math.Clamp(d.Denoise, 0, 3);
            DemucsRadios.SelectedIndex = Math.Clamp(d.Demucs, 0, 4);
            if (CmVolV != null) CmVolV.Value = Math.Clamp(d.VolV, 0, 200);
            if (CmVolA != null) CmVolA.Value = Math.Clamp(d.VolA, 0, 200);
            if (CmVolO1 != null) CmVolO1.Value = Math.Clamp(d.VolO1, 0, 200);
            if (CmVolO2 != null) CmVolO2.Value = Math.Clamp(d.VolO2, 0, 200);
            LoudnessCheck.IsChecked = d.Loudness;
            LowcutCheck.IsChecked = d.Lowcut;
            EqCheck.IsChecked = d.Eq;
            FmtRadios.SelectedIndex = Math.Clamp(d.OutputFmt, 0, 2);
        }
        catch { /* 读取失败用默认 */ }
    }

    private void SaveSettings()
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SettingsFile)!);
            System.IO.File.WriteAllText(SettingsFile,
                System.Text.Json.JsonSerializer.Serialize(new AudioSettings
                {
                    Sr = SrRadios.SelectedIndex,
                    Srs = SrsCheck?.IsChecked == true,
                    Denoise = DenoiseRadios.SelectedIndex,
                    Demucs = DemucsRadios.SelectedIndex,
                    VolV = CmVolV?.Value ?? 100,
                    VolA = CmVolA?.Value ?? 100,
                    VolO1 = CmVolO1?.Value ?? 100,
                    VolO2 = CmVolO2?.Value ?? 100,
                    Loudness = LoudnessCheck.IsChecked == true,
                    Lowcut = LowcutCheck.IsChecked == true,
                    Eq = EqCheck.IsChecked == true,
                    OutputFmt = FmtRadios.SelectedIndex,
                }));
        }
        catch { /* 保存失败忽略 */ }
    }

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
        AppLogger.UserAction("音频:点击「删除选中」");
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
        AppLogger.UserAction("音频:点击「清空」列表");
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
        AppLogger.UserAction("音频:点击「清除已完成」");
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
            var (dur, _, _, _, _) = AudioService.Probe(p);
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
        AppLogger.UserAction("音频:点击「选择音频文件」");
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
            var (dur, ch, sampleRate, _, _) = AudioService.Probe(it.Path);
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
        DispatcherQueue.TryEnqueue(() => PlayIcon.Glyph = playing ? "\uE769" : "\uE768");
    }

    private readonly Windows.Foundation.TypedEventHandler<Windows.Media.Playback.MediaPlaybackSession, object> _playStateHandler;

    private void PlayBtn_Click(object sender, RoutedEventArgs e)
    {
        AppLogger.UserAction("音频:点击预览「播放/暂停」");
        var mp = _mediaPlayer;
        if (mp == null) return;
        if (mp.PlaybackSession.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing)
        {
            mp.Pause();
            PlayIcon.Glyph = "\uE768";
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
            PlayIcon.Glyph = "\uE769";
        }
    }

    private void ResetBtn_Click(object sender, RoutedEventArgs e)
    {
        AppLogger.UserAction("音频:点击「再听一次」");
        var mp = _mediaPlayer;
        if (mp == null || _previewItem == null) return;
        // 再听一次 = 从头(0:00)开始播放
        mp.PlaybackSession.Position = TimeSpan.Zero;
        mp.Play();
        PlayIcon.Glyph = "\uE769";
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
        PlayIcon.Glyph = "\uE768";
        PreviewPanel.Visibility = Visibility.Collapsed;
    }

    private void PreviewCloseBtn_Click(object sender, RoutedEventArgs e)
    {
        AppLogger.UserAction("音频:关闭预览");
        ClosePreview();
    }

    // ---------- 裁剪把手(波形上拖首尾)见上方 ----------

    // ---------- 开始处理 ----------
    private async void RunBtn_Click(object sender, RoutedEventArgs e)
    {
        AppLogger.UserAction("音频:点击「开始处理」");
        if (_running || _items.Count == 0) return;
        _running = true;
        _cts = new CancellationTokenSource();
        UpdateRunState();
        AudioProgress.Value = 0;
        AudioStatus.Text = $"正在开始处理 {_items.Count} 个音频...";
        int total = _items.Count, done = 0, fail = 0;
        try
        {
            foreach (var item in _items)
            {
                if (_cts.IsCancellationRequested) break;
                Log($"处理中: {item.Name}");
                StatusChanged?.Invoke($"音频处理 {done + 1}/{total}: {item.Name}");
                AudioStatus.Text = $"正在开始处理: {item.Name}...";
                try
                {
                    var outFmt = FmtRadios.SelectedIndex;   // 0=MP3 1=WAV 2=FLAC
                    var ext = outFmt == 0 ? ".mp3" : outFmt == 1 ? ".wav" : ".flac";
                    var outPath = UniquePath(System.IO.Path.GetDirectoryName(item.Path)!,
                        System.IO.Path.GetFileNameWithoutExtension(item.Path) + "_增强" + ext);
                    var sepOutputs = new System.Collections.Generic.List<string>();   // 本次输出(分离/增强),弹窗用
                    var prog = new Progress<(int pct, string msg)>(t =>
                    {
                        AudioProgress.Value = t.pct;
                        AudioStatus.Text = t.msg;
                    });
                    // ==== 板块独立:升采样率 / 增强 / AI 分离 可同时勾选,各自输出独立文件 ====
                    int demucsSel = DemucsRadios.SelectedIndex;   // 0=关 1=人声 2=去人声 3=分离(两文件) 4=自定义组合
                    int srSel = SrRadios.SelectedIndex;           // 0=关 1=柔和 2=标准 3=强力(增强)
                    bool doSrs = SrsCheck?.IsChecked == true && LavaSrService.Available();
                    bool wantSep = demucsSel > 0;
                    bool wantRemaster = srSel > 0;
                    double trS = item.TrimStart > 0.1 ? item.TrimStart : 0;
                    double trE = (item.TrimEnd > 0.1 && item.DurationSec > 0.2) ? item.TrimEnd : 0;
                    var srcDir = System.IO.Path.GetDirectoryName(item.Path)!;
                    var srcBase = System.IO.Path.GetFileNameWithoutExtension(item.Path);
                    int denoiseSel = DenoiseRadios.SelectedIndex;
                    bool loudSel = LoudnessCheck.IsChecked == true;
                    bool lowcutSel = LowcutCheck.IsChecked == true;
                    bool eqSel = EqCheck.IsChecked == true;

                    // 源采样率探测(AI 板块需要;分离/增强在 44.1k 完成,最终输出回到源率)
                    int srcRate = 0;
                    if (doSrs || wantSep || wantRemaster)
                    {
                        try { srcRate = AudioService.Probe(item.Path).SampleRate; } catch { srcRate = 0; }
                    }

                    // ---- ① 升采样率(独立):低采样率源 → 48kHz;44.1k 全频带源自动跳过 ----
                    string? srsWav = null;
                    bool srsApplied = false;
                    if (doSrs)
                    {
                        if (srcRate >= 44000 || srcRate <= 0)
                        {
                            Log($"⚠ 源已是 {srcRate}Hz(全频带),升采样率仅对低采样率源(8k/16k/22k/32k)有效——已跳过");
                        }
                        else
                        {
                            Log($"🔄 升采样率: {srcRate}Hz → 48kHz(AI 补高频)...");
                            AudioStatus.Text = $"升采样率中(补高频 → 48kHz): {item.Name}...";
                            var rawWav = System.IO.Path.Combine(EngineService.TempRoot, $"alh_src_{Guid.NewGuid():N}.wav");
                            srsWav = System.IO.Path.Combine(EngineService.TempRoot, $"alh_srs_{Guid.NewGuid():N}.wav");
                            await AudioService.ConvertToWavSameRateAsync(item.Path, rawWav);
                            var srBytes = await LavaSrService.UpscaleWavAsync(rawWav, srcRate, prog, _cts.Token);
                            System.IO.File.WriteAllBytes(srsWav, srBytes);
                            try { System.IO.File.Delete(rawWav); } catch { }
                            srsApplied = true;
                            Log("✅ 升采样率完成 → 48kHz");
                        }
                    }
                    // 分离/增强最终输出采样率:升采样率生效→48k;源≥44.1k 保持源率(48k 源处理后仍是 48k);仅低采样率源才用 44.1k
                    int aiOutRate = srsApplied ? 48000 : (srcRate >= 44100 ? srcRate : 44100);

                    // ---- ② 增强 / AI 分离:共用一次分轨(升采样率生效时,分轨用升采样率后的 48k 源,频带更宽) ----
                    bool didAi = false;
                    if (wantSep || wantRemaster)
                    {
                        var tmpWav = System.IO.Path.Combine(EngineService.TempRoot, $"alh_demucs_{Guid.NewGuid():N}.wav");
                        Log("转为 44.1kHz 立体声 WAV...");
                        AudioStatus.Text = $"正在准备: {item.Name}(转为 44.1kHz 立体声)...";
                        await AudioService.ConvertToWav44kAsync(srsApplied ? srsWav! : item.Path, tmpWav);
                        bool aiGpu = AppSettings.GpuIndex >= 0;   // 设置里选了显卡 → DML 加速;选了 CPU/无显卡 → CPU
                        Log(aiGpu ? "AI 分轨处理中(显卡加速,请耐心等待...)" : "AI 分轨处理中(CPU 较慢:约 1.5 分钟/分钟音频,可先离开本页处理其它任务)");
                        AudioStatus.Text = aiGpu ? "AI 分离中(显卡加速): ..." : $"AI 分离中(CPU 较慢): {item.Name}...";
                        // 【一次分轨】输出 4 轨(人声=轨3,伴奏=原曲−人声,其他1/2=轨1/2)——增强和分离共用,不重复推理
                        var aiDir = System.IO.Path.Combine(EngineService.TempRoot, $"alh_ai_{Guid.NewGuid():N}");
                        System.IO.Directory.CreateDirectory(aiDir);
                        var stemBase = System.IO.Path.Combine(aiDir, "stems");
                        await AudioEnhanceService.SeparateAsync(tmpWav, stemBase + ".wav", 7,
                            aiGpu ? AppSettings.GpuIndex : -1, 100f, prog, _cts.Token);   // 选卡则 DML,否则 CPU
                        var vWav = stemBase + "_人声.wav";
                        var aWav = stemBase + "_伴奏.wav";
                        var o1Wav = stemBase + "_其他1.wav";
                        var o2Wav = stemBase + "_其他2.wav";

                        // ===== 最终输出:链式——升采样率(上游已作用)/增强(效果并入),最终文件只由「AI分离」决定 =====
                        if (wantSep)
                        {
                            if (demucsSel == 1)   // 提取人声(增强并入:人声链优化)
                            {
                                var p = UniquePath(srcDir, srcBase + "_人声" + ext);
                                Log("AI 分离完成(人声)" + (wantRemaster ? ",应用增强(人声优化)" : "") + ",应用降噪/音色调整并转换 " + ext + "(" + aiOutRate + "Hz) ...");
                                AudioStatus.Text = "分离完成,转换输出(人声)...";
                                var src = vWav;
                                if (wantRemaster)
                                {
                                    var sw = System.IO.Path.Combine(aiDir, "opt_v.wav");
                                    await AudioService.OptimizeStemAsync(vWav, sw, true, srSel, prog, _cts.Token);
                                    src = sw;
                                }
                                await AudioService.EnhanceAsync(src, p, denoiseSel, loudSel, lowcutSel, eqSel,
                                    outFmt, 320, null, prog, _cts.Token, trS, trE, aiOutRate);
                                sepOutputs.Add(p);
                            }
                            else if (demucsSel == 2)   // 去人声(卡拉OK伴奏)(增强并入:伴奏链优化)
                            {
                                var p = UniquePath(srcDir, srcBase + "_伴奏" + ext);
                                Log("AI 分离完成(伴奏)" + (wantRemaster ? ",应用增强(伴奏优化)" : "") + ",应用降噪/音色调整并转换 " + ext + "(" + aiOutRate + "Hz) ...");
                                AudioStatus.Text = "分离完成,转换输出(伴奏)...";
                                var src = aWav;
                                if (wantRemaster)
                                {
                                    var sw = System.IO.Path.Combine(aiDir, "opt_a.wav");
                                    await AudioService.OptimizeStemAsync(aWav, sw, false, srSel, prog, _cts.Token);
                                    src = sw;
                                }
                                await AudioService.EnhanceAsync(src, p, denoiseSel, loudSel, lowcutSel, eqSel,
                                    outFmt, 320, null, prog, _cts.Token, trS, trE, aiOutRate);
                                sepOutputs.Add(p);
                            }
                            else if (demucsSel == 3)   // 分离两文件 → "音频名_分离"文件夹(人声/伴奏各走各的优化链)
                            {
                                var sepDir = System.IO.Path.Combine(srcDir, srcBase + "_分离");
                                System.IO.Directory.CreateDirectory(sepDir);
                                Log("AI 分离完成(两文件)" + (wantRemaster ? ",应用增强(人声/伴奏分别优化)" : "") + ",应用降噪/音色调整并转换 " + ext + "(" + aiOutRate + "Hz) ...");
                                AudioStatus.Text = "分离完成,转换输出(人声+伴奏 两个文件)...";
                                var fv = UniquePath(sepDir, "人声" + ext);
                                var fa = UniquePath(sepDir, "伴奏" + ext);
                                var sv = vWav;
                                var sa = aWav;
                                if (wantRemaster)
                                {
                                    var ov = System.IO.Path.Combine(aiDir, "opt_v.wav");
                                    var oa = System.IO.Path.Combine(aiDir, "opt_a.wav");
                                    await AudioService.OptimizeStemAsync(vWav, ov, true, srSel, prog, _cts.Token);
                                    await AudioService.OptimizeStemAsync(aWav, oa, false, srSel, prog, _cts.Token);
                                    sv = ov;
                                    sa = oa;
                                }
                                await AudioService.EnhanceAsync(sv, fv, denoiseSel, loudSel, lowcutSel, eqSel,
                                    outFmt, 320, null, prog, _cts.Token, trS, trE, aiOutRate);
                                await AudioService.EnhanceAsync(sa, fa, denoiseSel, loudSel, lowcutSel, eqSel,
                                    outFmt, 320, null, prog, _cts.Token, trS, trE, aiOutRate);
                                sepOutputs.Add(fv);
                                sepOutputs.Add(fa);
                            }
                            else   // demucsSel == 4 自定义组合:按各轨音量混合成 1 个文件(升采样率/增强作为效果并入)
                            {
                                // 勾选要输出的轨(bitmask 1=人声 2=伴奏 4=其他1 8=其他2)
                                int mask = 0;
                                if (CmVocals.IsChecked == true) mask |= 1;
                                if (CmAcc.IsChecked == true) mask |= 2;
                                if (CmOther1.IsChecked == true) mask |= 4;
                                if (CmOther2.IsChecked == true) mask |= 8;
                                if (mask == 0) { Log("⚠ 自定义组合未勾选任何轨道"); throw new OperationCanceledException(); }
                                var stems = new (string path, double gain, string nm)[]
                                {
                                    (vWav, Math.Clamp(CmVolV?.Value ?? 100, 0, 200) / 100.0, "人声"),
                                    (aWav, Math.Clamp(CmVolA?.Value ?? 100, 0, 200) / 100.0, "伴奏"),
                                    (o1Wav, Math.Clamp(CmVolO1?.Value ?? 100, 0, 200) / 100.0, "其他1"),
                                    (o2Wav, Math.Clamp(CmVolO2?.Value ?? 100, 0, 200) / 100.0, "其他2"),
                                };
                                var sel = new System.Collections.Generic.List<(string path, double gain, string nm)>();
                                for (int i = 0; i < 4; i++) if ((mask & (1 << i)) != 0) sel.Add(stems[i]);
                                Log("AI 分离完成(自定义组合):调音量" + (wantRemaster ? "+增强(人声/伴奏分别优化)" : "") + ",混合所选轨道...");
                                AudioStatus.Text = "分离完成,按音量混合中...";
                                // 每轨:音量滑块 → (增强)人声链/伴奏链优化 → 求和限幅 → 1 个文件
                                var work = new System.Collections.Generic.List<string>();
                                foreach (var s in sel)
                                {
                                    var gWav = System.IO.Path.Combine(aiDir, "stem_g_" + s.nm + ".wav");
                                    await AudioService.MixWavsAsync(new System.Collections.Generic.List<string> { s.path },
                                        gWav, prog, _cts.Token, new double[] { s.gain });
                                    var cur = gWav;
                                    if (wantRemaster)
                                    {
                                        var oWav = System.IO.Path.Combine(aiDir, "stem_o_" + s.nm + ".wav");
                                        await AudioService.OptimizeStemAsync(gWav, oWav, s.nm == "人声", srSel, prog, _cts.Token);
                                        cur = oWav;
                                    }
                                    work.Add(cur);
                                }
                                var mixWav = System.IO.Path.Combine(aiDir, "custom.wav");
                                await AudioService.MixWavsAsync(work, mixWav, prog, _cts.Token, null);
                                var mixOut = UniquePath(srcDir, srcBase + "_自定义" + ext);
                                Log("应用降噪/音色调整并转换 " + ext + "(" + aiOutRate + "Hz) ...");
                                await AudioService.EnhanceAsync(mixWav, mixOut, denoiseSel, loudSel, lowcutSel, eqSel,
                                    outFmt, 320, null, prog, _cts.Token, trS, trE, aiOutRate);
                                sepOutputs.Add(mixOut);
                            }
                        }
                        else if (wantRemaster)
                        {
                            // 无分离:增强 = 整曲(人声/伴奏分别优化后重混),只出 1 个「_增强」文件(升采样率作为效果已在上游)
                            Log("增强:人声/伴奏分别优化,重新混音...");
                            AudioStatus.Text = $"增强中(人声/伴奏优化重混): {item.Name}...";
                            var mixWav = System.IO.Path.Combine(aiDir, "remix.wav");
                            await AudioService.RemasterAsync(vWav, aWav, mixWav, srSel, prog, _cts.Token);
                            var enOut = UniquePath(srcDir, srcBase + "_增强" + ext);
                            Log("增强完成,应用降噪/音色调整并转换 " + ext + "(" + aiOutRate + "Hz) ...");
                            await AudioService.EnhanceAsync(mixWav, enOut, denoiseSel, loudSel, lowcutSel, eqSel,
                                outFmt, 320, null, prog, _cts.Token, trS, trE, aiOutRate);
                            sepOutputs.Add(enOut);
                        }
                        // 清理临时文件
                        try { System.IO.File.Delete(tmpWav); } catch { }
                        try { System.IO.Directory.Delete(aiDir, true); } catch { }
                        didAi = true;
                    }

                    // ---- ③ 升采样率只在"单独勾选"时输出「_升采样率」文件;与分离/增强一起时作为效果并入不单独出 ----
                    if (srsApplied && !wantSep && !wantRemaster)
                    {
                        var srsOut = UniquePath(srcDir, srcBase + "_升采样率" + ext);
                        Log("升采样率输出:应用降噪/音色调整并转换 " + ext + "(48000Hz) ...");
                        AudioStatus.Text = $"升采样率完成,转换输出: {item.Name}...";
                        await AudioService.EnhanceAsync(srsWav!, srsOut, denoiseSel, loudSel, lowcutSel, eqSel,
                            outFmt, 320, null, prog, _cts.Token, trS, trE, aiOutRate);
                        sepOutputs.Add(srsOut);
                    }
                    if (srsApplied)
                    {
                        try { System.IO.File.Delete(srsWav!); } catch { }
                    }

                    // ---- ④ 无任何 AI 生效时的普通增强(降噪/音色/裁剪) ----
                    if (!srsApplied && !didAi)
                    {
                        await AudioService.EnhanceAsync(item.Path, outPath,
                            denoiseSel,   // 0=关 1=弱 2=中 3=强(afftdn nf=-25/-30/-35)
                            loudSel, lowcutSel, eqSel,
                            outFmt, 320, null,   // MP3 用 320k 高品质(源 AAC 256k 时不再降档;WAV/FLAC 无损,此值忽略)
                            prog, _cts.Token, trS, trE);
                    }
                    item.IsDone = true;
                    item.Status = "✅ 完成";
                    item.Display = item.Name + "  (已处理)";
                    done++;
                    _allOutputs.AddRange(sepOutputs.Where(f => File.Exists(f)));
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
            AudioStatus.Text = done > 0 && fail == 0
                ? $"✅ 完成:成功 {done} 个" + (fail > 0 ? "" : "")
                : $"⚠ 完成:成功 {done},失败 {fail}";
            StatusChanged?.Invoke($"音频处理完成:成功 {done} 失败 {fail}");
            try
            {
                // 完成弹窗:打开输出文件夹(与图片/视频页一致)
                if (done > 0 && _allOutputs.Count > 0)
                {
                    var dir = System.IO.Path.GetDirectoryName(_allOutputs[0]) ?? "";
                    var dlg = new ContentDialog
                    {
                        Title = "处理完成",
                        Content = new TextBlock
                        {
                            Text = $"已处理 {done} 个音频\n输出:\n{string.Join("\n", _allOutputs.Take(8).Select(f => "· " + System.IO.Path.GetFileName(f)))}{( _allOutputs.Count > 8 ? $"\n...共 {_allOutputs.Count} 个" : "")}",
                            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                        },
                        PrimaryButtonText = "打开输出文件夹",
                        CloseButtonText = "关闭",
                        XamlRoot = this.XamlRoot,
                    };
                    var r = await dlg.ShowAsync();
                    if (r == ContentDialogResult.Primary)
                        ProcessStartHelper.OpenSelect(_allOutputs);
                }
            }
            catch { }
            _allOutputs = new();
        }
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        AppLogger.UserAction("音频:点击「强制结束」");
        Log("用户点击「强制结束」,正在停止...");
        _cts?.Cancel();
    }
}
