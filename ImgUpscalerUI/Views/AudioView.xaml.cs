using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
    }

    private readonly List<AudioItem> _items = new();
    private bool _running;
    private CancellationTokenSource? _cts;

    /// <summary>状态变化(底部状态栏显示)。</summary>
    public event Action<string>? StatusChanged;

    public AudioView()
    {
        this.InitializeComponent();
        UpdateRunState();
    }

    private void Log(string msg) { LogText.Text = msg; StatusChanged?.Invoke(msg); }

    private void UpdateRunState()
    {
        RunBtn.IsEnabled = _items.Count > 0 && !_running;
        CancelBtn.IsEnabled = _running;
        RunBtn.Content = _running ? "处理中..." : "开始处理";
    }

    private void Options_Changed(object sender, RoutedEventArgs e) { }

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
        foreach (var f in files)
        {
            var it = new AudioItem { Path = f.Path };
            it.Display = it.Name;
            _items.Add(it);
            AudioList.Items.Add(it);
        }
        AudioInfo.Text = $"{_items.Count} 个音频";
        Log($"已添加 {files.Count} 个音频");
        UpdateRunState();
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
                        }), _cts.Token);
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
