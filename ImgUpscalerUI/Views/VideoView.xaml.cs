// VideoView.xaml.cs — 视频超分 + 补帧 + 裁剪板块
using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Core;
using Windows.Storage.Pickers;

namespace ALHPro.Views;

/// <summary>视频列表项:缩略图 + 名称 + 信息 + 裁剪状态(支持 UI 自动刷新)。</summary>
public sealed class VideoItem : System.ComponentModel.INotifyPropertyChanged
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public string BaseInfo { get; init; } = "";
    public double Duration { get; set; }

    private BitmapImage? _thumb;
    public BitmapImage? Thumb
    {
        get => _thumb;
        set { _thumb = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Thumb))); }
    }

    // 裁剪范围(秒);0=未裁剪
    private double _trimStart;
    private double _trimEnd;
    public double TrimStart { get => _trimStart; set { _trimStart = value; RaiseTrim(); } }
    public double TrimEnd { get => _trimEnd; set { _trimEnd = value; RaiseTrim(); } }

    // 单独指定的输入帧率(null=用原帧率/偏移)
    private double? _customFps;
    public double? CustomFps { get => _customFps; set { _customFps = value; RaiseTrim(); PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(FpsEditText))); } }

    // 探测到的原帧率文本(用于显示/恢复)
    private string _fpsProbe = "";
    public string FpsProbe
    {
        get => _fpsProbe;
        set
        {
            _fpsProbe = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(FpsProbe)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(FpsEditText)));
        }
    }

    // 是否为可变帧率(VFR)素材:加入列表时后台探测;True 时 Info 标注 + 自动启用 VFR 拆帧
    private bool _isVfr;
    public bool IsVfr
    {
        get => _isVfr;
        set
        {
            _isVfr = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsVfr)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(VfrBadgeVisibility)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Info)));
        }
    }

    public Microsoft.UI.Xaml.Visibility VfrBadgeVisibility
        => IsVfr ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    // 列表项帧率编辑框文本(暂存输入,点"保存"后正式应用)
    private string? _draftFps;
    public string FpsEditText
    {
        get => CustomFps?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
            ?? _draftFps ?? FpsProbe;
        set
        {
            _draftFps = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(FpsEditText)));
        }
    }

    /// <summary>把暂存输入正式应用到 CustomFps。</summary>
    public void CommitFps()
    {
        if (double.TryParse(_draftFps, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var f) && f > 0)
            CustomFps = f;
        else if (string.IsNullOrWhiteSpace(_draftFps))
            CustomFps = null;
    }

    /// <summary>清空暂存输入(恢复显示原帧率)。</summary>
    public void ClearDraft()
    {
        _draftFps = null;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(FpsEditText)));
    }

    // 单独调整模式下的帧率编辑行可见性(由视图统一控制)
    private Microsoft.UI.Xaml.Visibility _fpsEditVisibility = Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility FpsEditVisibility
    {
        get => _fpsEditVisibility;
        set { _fpsEditVisibility = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(FpsEditVisibility))); }
    }

    // 帧率编辑框锁定:保存/统一应用后 true(只能看,想改先点「恢复」解锁)
    private bool _fpsEditEnabled = true;
    public bool FpsEditEnabled
    {
        get => _fpsEditEnabled;
        set { _fpsEditEnabled = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(FpsEditEnabled))); }
    }

    // 处理进度(0-100)与状态小字,处理时显示在列表项上
    private double _progress;
    public double Progress
    {
        get => _progress;
        set { _progress = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Progress))); }
    }

    private bool _isProcessing;
    public bool IsProcessing
    {
        get => _isProcessing;
        set { _isProcessing = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsProcessing))); }
    }

    private bool _isDone;
    /// <summary>已完成(项目变灰):默认不再处理;点「重新处理」按钮调起。</summary>
    public bool IsDone
    {
        get => _isDone;
        set
        {
            _isDone = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsDone)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DoneItemOpacity)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ReRunBtnVisibility)));
        }
    }

    /// <summary>已完成项变暗(灰掉),未完成的正常。</summary>
    public double DoneItemOpacity => _isDone ? 0.45 : 1.0;

    /// <summary>「重新处理」按钮:仅已完成(灰)时显示;且必须是真实视频项(有路径),压制空列表/幽灵项浮出「删除」。</summary>
    public Microsoft.UI.Xaml.Visibility ReRunBtnVisibility
        => _isDone && !string.IsNullOrEmpty(Path) ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility ProgressVisibility
        => IsProcessing ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(StatusText))); }
    }

    // 处理完成后的输出信息(帧率/分辨率/大小)
    private string _outputInfo = "";
    public string OutputInfo
    {
        get => _outputInfo;
        set { _outputInfo = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(OutputInfo))); }
    }

    // 预计剩余时间(处理中显示,如"预计剩余 12:34")
    private string _etaText = "";
    public string EtaText
    {
        get => _etaText;
        set { _etaText = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(EtaText))); }
    }

    /// <summary>本视频开始处理的时间(ETA 估算基准)。</summary>
    public DateTime StartTime { get; set; }

    /// <summary>是否还有未执行的任务(暂停时判断能否删除:未处理且未开始才算)。</summary>
    public bool IsPending => !IsProcessing && Progress <= 0 && StatusText.Length == 0;

    public bool IsTrimmed => TrimStart > 0.1 || (Duration > 0 && TrimEnd > 0.1 && TrimEnd < Duration - 0.1);

    public Microsoft.UI.Xaml.Visibility TrimBadgeVisibility
        => IsTrimmed ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public string Info
    {
        get
        {
            var parts = new System.Collections.Generic.List<string> { BaseInfo };
            if (IsTrimmed)
                parts.Add($"已裁剪 {VideoView.FormatTime(TrimStart)}~{VideoView.FormatTime(TrimEnd)}");
            // 帧率显示改由下方醒目的"有效输入帧率"徽标承担(随模式/偏移/手动实时联动),此处不再重复。
            return string.Join(" · ", parts);
        }
    }

    private void RaiseTrim()
    {
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Info)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(TrimBadgeVisibility)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(FpsEditText)));
    }

    // 有效输入帧率(视图按"默认/偏移/单独调整"模式实时计算并回填):随滑条/模式/手动值联动刷新,预览更直观。
    private double? _effFps;
    public void SetEffFps(double? v)
    {
        if (Math.Abs((_effFps ?? 0) - (v ?? 0)) < 0.0001) return;
        _effFps = v;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(EffFpsText)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(EffFpsVisibility)));
    }
    public string EffFpsText => _effFps is > 0 ? $"帧率 {_effFps.Value:0.##}" : "";
    public Microsoft.UI.Xaml.Visibility EffFpsVisibility
        => _effFps is > 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>IsProcessing 变化时同步进度条可见性;开始时重置 ETA。</summary>
    public void SetProcessing(bool on)
    {
        if (on) { StartTime = DateTime.Now; EtaText = ""; }
        IsProcessing = on;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ProgressVisibility)));
    }

    // ---- 重复帧预览(轻量预估+全文分析) ----
    private double _dupRatioPct;
    public double DupRatioPct { get => _dupRatioPct; set { _dupRatioPct = value; RaiseDup(); } }
    private double _contentFps;
    public double ContentFps { get => _contentFps; set { _contentFps = value; RaiseDup(); } }
    public System.Collections.Generic.List<VideoService.DupSegInfo> DupSegs { get; set; } = new();
    private string _dupSummary = "";
    public string DupSummary { get => _dupSummary; set { _dupSummary = value; RaiseDup(); } }
    private string _dupBadgeText = "";
    public string DupBadgeText { get => _dupBadgeText; set { _dupBadgeText = value; RaiseDup(); } }
    private Microsoft.UI.Xaml.Visibility _dupBadgeVisibility = Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility DupBadgeVisibility { get => _dupBadgeVisibility; set { _dupBadgeVisibility = value; RaiseDup(); } }
    private Microsoft.UI.Xaml.Media.SolidColorBrush? _dupBadgeBrush;
    public Microsoft.UI.Xaml.Media.SolidColorBrush? DupBadgeBrush { get => _dupBadgeBrush; set { _dupBadgeBrush = value; RaiseDup(); } }

    private void RaiseDup()
    {
        var e = new System.ComponentModel.PropertyChangedEventArgs(nameof(DupRatioPct));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DupBadgeText)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DupBadgeVisibility)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DupBadgeBrush)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DupSummary)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Info)));
    }

    /// <summary>写入重复帧画像 → 更新徽标文本/颜色/可见性。</summary>
    public void SetDupProfile(VideoService.DupProfile p)
    {
        DupRatioPct = p.DupRatioPct;
        ContentFps = p.ContentFps;
        DupSegs = p.Segs;
        DupSummary = p.Summary;
        double dup = p.DupRatioPct;
        // 徽标口吻:"内容≈20fps(删67%)"——先说结果(去重后内容多少帧率),括号里是删了多少,
        // 普通用户一看就懂;没有内容帧率时才退回只显示重复百分比。
        string badge = p.ContentFps > 0.5
            ? $"内容≈{p.ContentFps:0.#}fps(删{dup:0}%)" + (p.Estimated ? "·预估" : "")
            : $"重复≈{dup:0}%" + (p.Estimated ? "·预估" : "");
        if (p.Estimated)
        {
            DupBadgeText = badge;
            DupBadgeBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x6C, 0x5C, 0xE7));   // 紫(预估)
        }
        else
        {
            DupBadgeText = badge;
            var (r, g, b) = dup >= 30 ? (0xD9, 0x53, 0x4F) : dup >= 12 ? (0xE8, 0xA3, 0x3D) : (0x3F, 0xA4, 0x5A);
            DupBadgeBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, (byte)r, (byte)g, (byte)b));
        }
        DupBadgeVisibility = dup > 0.5 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public sealed partial class VideoView : UserControl
{
    private readonly ObservableCollection<VideoItem> _videos = new();
    private bool _dupRefreshRun;   // 进入页面自动重估(防重复:每次页面实例只跑一次)

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct DEVMODEW
    {
        public short dmDeviceNameOffset;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string lpszDeviceName, int iModeNum, ref DEVMODEW lpDevMode);
    private readonly System.Collections.Generic.List<string> _failReasons = new();   // 本次任务每个失败项 + 原因(供完成弹窗展示)
    private bool _running;
    private CancellationTokenSource? _cts;
    private string? _customOutDir;
    private bool _suppressEvents;
    private VideoItem? _selected;
    private int _gpuCount;

    // ---- 暂停/恢复:暂停后停在下一个视频之前,可删除"未处理"的项目 ----
    private bool _paused;
    private TaskCompletionSource<bool>? _resumeTcs;
    private VideoItem[]? _runItems;   // 本次任务的快照(删除列表项不影响遍历)

    public VideoView()
    {
        this.InitializeComponent();
        // 进入页面自动重估列表项的"内容≈Xfps"徽标:列表恢复/旧项目不会再"没有显示"
        // (预估只在"拖入列表"时触发,重启后旧项不重算 → 徽标空白;这里统一补上,后台串行,不卡界面)
        this.Loaded += async (_, _) =>
        {
            if (_dupRefreshRun) return;
            _dupRefreshRun = true;
            try
            {
                foreach (var it in _videos.ToList())
                {
                    try { await ProbeDupAsync(it); } catch { }
                    await System.Threading.Tasks.Task.Delay(50);
                }
            }
            catch { }
        };
        // 日志区滚轮只滚日志(含 handledEventsToo:ScrollViewer 内部已处理也拦得到);
        // 滚到顶/底也吃掉滚轮,不带动外层页面滚动(用户习惯:鼠标在日志里滚动时"只滚日志")
        VideoLogScroll.AddHandler(Microsoft.UI.Xaml.UIElement.PointerWheelChangedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler((s, e) =>
            {
                if (e.Handled) return;
                try
                {
                    double delta = e.GetCurrentPoint(VideoLogScroll).Properties.MouseWheelDelta;   // >0=向上,<0=向下
                    double maxOff = VideoLogScroll.ScrollableHeight;
                    double target = Math.Max(0, Math.Min(maxOff, VideoLogScroll.VerticalOffset - delta));
                    VideoLogScroll.ChangeView(null, target, null, true);
                }
                catch { /* 布局未就绪时忽略 */ }
                e.Handled = true;
            }),
            handledEventsToo: true);
        VideoList.ItemsSource = _videos;
        // Del 快捷键:全局(框选后焦点不在 VideoGridHost 内也能删;限定 ScopeOwner 会导致
        // 框选释放后 Del 失效)。排除输入框焦点(避免删文本时误删列表项)。
        // KeyboardAcceleratorPlacementMode=Hidden(宿主 UIElement 属性):
        // 关掉 Delete 键加速器的自动悬停提示(即"删除"浮字根源),快捷键仍生效。
        VideoGridHost.KeyboardAcceleratorPlacementMode =
            Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;
        var delAcc = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Delete };
        delAcc.Invoked += (_, e) =>
        {
            if (IsTextInputFocused()) { e.Handled = false; return; }
            if (VideoList.SelectedItems.Count > 0)
            {
                RemoveVideo_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
        };
        VideoGridHost.KeyboardAccelerators.Add(delAcc);

        // 屏幕刷新率检测:提示"输出帧率≈屏幕刷新率最流畅"(60/120/144/165/240Hz 屏,
        // 高帧率输出不整除时播放器不均匀丢帧=抖动;填=刷新率则 1 帧对 1 刷新)
        try
        {
            var dm = new DEVMODEW { dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf<DEVMODEW>() };
            if (EnumDisplaySettingsW(null, -1, ref dm) && dm.dmDisplayFrequency > 24)
                ScreenFpsHint.Text = $"屏幕 {dm.dmDisplayFrequency}Hz·输出填 {dm.dmDisplayFrequency} 最流畅";
        }
        catch { }
        ScreenFpsHint.IsHitTestVisible = false;
        UpdateComponentStatus();
        // 计算设备:统一在「设置」里选择(AppSettings.GpuIndex),页面不再显示下拉
        _gpuCount = GpuInfo.GetAdapterNames().Count;
        // 参数默认值(InitializeComponent 后设置,避免 XAML 解析期事件)
        DedupHiSlider.Value = 12;      // 手动-重复帧检测:默认=动漫模式参数(研究校准值)
        DedupLoSlider.Value = 5;
        DedupFracSlider.Value = 0.33;
        DedupSceneSlider.Value = 0.01; // 手动-画面变化阈值
        DedupSadSlider.Value = 3.0;    // 手动-帧差+SSIM:快筛阈值
        DedupSsimSlider.Value = 0.97;  // 手动-帧差+SSIM:SSIM 阈值
        DedupAlgoCombo.SelectedIndex = 0;
        SceneSlider.Value = 0.3;
        TimeStepSlider.Value = 0.5;
        QualityCombo.SelectedIndex = 0;
        FormatCombo.SelectedIndex = 0;
        CodecCombo.SelectedIndex = 0;   // 默认 H.264(必须在 InitializeComponent 后设置,否则解析期触发事件崩页面)
        LoadSettings();
        EnsureBuiltinPresets();   // 确保自带预设(画质通用增强)存在
        UpdateOptions();
        UpdateDropHint();
        // 视频降噪联动:未勾选「启用视频降噪」时,强度置灰禁用
        void SetDenoiseUi(bool on)
        {
            DenoiseStrongRadios.IsEnabled = on;
            DenoiseStrongRadios.Opacity = on ? 1.0 : 0.5;
            DenoiseStrongLabel.Opacity = on ? 1.0 : 0.5;
        }
        DenoiseToggle.Checked += (_, _) => SetDenoiseUi(true);
        DenoiseToggle.Unchecked += (_, _) => SetDenoiseUi(false);
        SetDenoiseUi(DenoiseToggle.IsChecked == true);
    }

    /// <summary>当前计算设备是否为核显(名字识别:Intel UHD/Iris/*Intel(R) Graphics* / AMD Radeon(TM) Graphics)。</summary>
    private bool CurrentIsIntegratedGpu()
    {
        try
        {
            var idx = CurrentGpuId;
            if (idx < 0) return false;   // CPU 模式,不算核显
            var names = GpuInfo.GetAdapterNames();
            if (idx < names.Count)
            {
                var n = names[idx];
                if (n.Contains("AMD Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase)) return true;
                return n.Contains("Intel", StringComparison.OrdinalIgnoreCase)
                    && (n.Contains("UHD", StringComparison.OrdinalIgnoreCase)
                        || n.Contains("Iris", StringComparison.OrdinalIgnoreCase)
                        || n.Contains("HD Graphics", StringComparison.OrdinalIgnoreCase)
                        || n.Contains("Intel(R) Graphics", StringComparison.OrdinalIgnoreCase));
            }
        }
        catch { }
        return false;
    }

    // 当前计算设备(全局设置):-1 = CPU;≥0 = GPU 编号(超出枚举数按 CPU 处理)
    private int CurrentGpuId
        => AppSettings.GpuIndex >= 0 && AppSettings.GpuIndex < _gpuCount ? AppSettings.GpuIndex : -1;

    /// <summary>焦点是否在文本输入控件上(此时 Del/PasDel 应交给输入框,不触发列表删除)。</summary>
    private bool IsTextInputFocused()
    {
        var f = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) as Microsoft.UI.Xaml.DependencyObject;
        return f switch
        {
            TextBox or Microsoft.UI.Xaml.Controls.PasswordBox or Microsoft.UI.Xaml.Controls.RichEditBox => true,
            _ => false,
        };
    }

    private void UpdateComponentStatus()
    {
        var (ffmpeg, rife) = VideoService.CheckComponents();
        var parts = new System.Collections.Generic.List<string>
        {
            ffmpeg ? "ffmpeg ✓" : "ffmpeg ✗",
            rife ? "RIFE ✓" : "RIFE ✗",
        };
        CompStatus.Text = $"组件:{string.Join(" · ", parts)}\n" +
            (ffmpeg ? "" : "缺少 ffmpeg(视频处理必需)请放入 engines/ffmpeg/\n") +
            (rife ? "" : "缺少 RIFE(补帧必需)请放入 engines/rife/");
    }

    private void UpdateDropHint()
    {
        VideoDropHint.Visibility = _videos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // 注意:不能折叠 ListView 也不能关 IsHitTestVisible——空列表还要能拖入(video 列表空时可拖)。
        // WinUI 空列表"幽灵项"悬停浮出模板「删除」按钮:用 FallbackValue=Collapsed 抑制(模板层)。
        // 若仍冒「删除」,根治需在模板层把该按钮绑定到一个"真实完成项才可见"的强条件(而非仅 IsDone)。
    }

    private void UpdateRunState()
    {
        bool anyWork = UpscaleToggle.IsChecked == true || InterpToggle.IsChecked == true;
        RunBtn.IsEnabled = _videos.Count > 0 && !_running && anyWork;
        // 提示放到"开始处理"按钮下方:两项都关时提醒,避免用户找不到原因
        if (RunHint != null)
            RunHint.Visibility = _videos.Count > 0 && !_running && !anyWork
                ? Visibility.Visible : Visibility.Collapsed;
        // 耗时提示(黄色):启用耗时的功能时,提示处理时间会增加(开什么显示什么)
        if (SpeedHint != null)
        {
            var slow = new System.Collections.Generic.List<string>();
            bool interp = InterpToggle.IsChecked == true;
            if (UpscaleToggle.IsChecked == true) slow.Add("超分");
            if (interp && InterpModelCombo.SelectedIndex is 3 or 4 or 5 or 6) slow.Add("非 v4 补帧模型");   // anime/HD/UHD/v2.3 只能级联
            if (TtaCheck.IsChecked == true) slow.Add("高质量 TTA");
            if (interp && InterpScaleRadios.SelectedIndex is 3 or 4) slow.Add("高倍率补帧");
            if (interp && SceneCheck.IsChecked == true) slow.Add("转场识别");
            if (DenoiseToggle.IsChecked == true) slow.Add("视频降噪");
            if ((int)SharpenSlider.Value > 0 || (int)ClaritySlider.Value > 0 || (int)UsmSlider.Value > 0
                || (int)DetailSlider.Value > 0 || (int)DeblurSlider.Value > 0
                || FlickerSlider.Value > 0 || PostDenoiseSlider.Value > 0 || PostAaSlider.Value > 0)
                slow.Add("后处理");
            if (interp && MotionBlurCombo.SelectedIndex > 0) slow.Add("运动模糊");
            if (interp && DeShakeCheck.IsChecked == true) slow.Add("画面去抖");
            if (slow.Count > 0 && anyWork && !_running)
            {
                SpeedHint.Text = $"⚠ 已启用 {string.Join("、", slow)} 处理时间会增加";
                SpeedHint.Visibility = Visibility.Visible;
            }
            else SpeedHint.Visibility = Visibility.Collapsed;
        }
        // 弱设备 + 未开兼容模式 → 显示黄字提示(建议开启);否则隐藏
        if (CompatHintPanel != null)
        {
            bool weak = SafeRender.IsWeakDevice && FastModeCheck.IsChecked != true;
            // 引擎兼容自检优先(不限 50 系):旧 ncnn 引擎(realesrgan 2022)在
            // Blackwell/Vulkan 不可用设备上 GPU 崩 → 建议 waifu2x;rife 老模型在 Blackwell 不稳 → 建议 v4.13/v4.6
            string? compatMsg = null;
            bool upOn = UpscaleToggle.IsChecked == true;
            bool interpOn = InterpToggle.IsChecked == true;
            if (upOn && VideoEngineRadios.SelectedIndex == 1 && EngineService.OldNcnnGpuRisky())
            {
                compatMsg = $"⚠ 当前显卡与「Real-ESRGAN」(2022 版)不兼容,建议改用「waifu2x」(官方新版,更稳定)";
            }
            else if (interpOn && InterpModelCombo.SelectedIndex is 3 or 4 or 5 or 6 && EngineService.OldRifeModelRisky())
            {
                var oldModel = InterpModelCombo.SelectedIndex switch { 3 => "动漫专用(RIFE Anime)", 4 => "高清(RIFE HD)", 5 => "超高清(RIFE UHD)", _ => "经典兼容(RIFE v2.3)" };
                compatMsg = $"⚠ 当前显卡与「{oldModel}」旧模型兼容性差,建议改用「通用画质最新 v4.13/v4.6」(更稳定)";
            }
            if (compatMsg != null)
            {
                CompatHintPanel.Visibility = Visibility.Visible;
                if (CompatHint != null) CompatHint.Text = compatMsg;
                // 日志只记一次(提示条本身常显;避免切页面/点控件反复刷屏日志)
                if (!_compatWarnLogged)
                {
                    _compatWarnLogged = true;
                    AppLogger.Info(compatMsg);
                }
            }
            else
            {
                CompatHintPanel.Visibility = weak ? Visibility.Visible : Visibility.Collapsed;
                if (weak && CompatHint != null)
                {
                    CompatHint.Text = $"⚠ 检测到设备配置较低({SafeRender.WeakDeviceReason}),建议勾选「兼容模式」防止爆显存/卡顿。";
                    if (!_compatWarnLogged)
                    {
                        _compatWarnLogged = true;
                        AppLogger.Info($"⚠ 检测到设备配置较低({SafeRender.WeakDeviceReason}),建议勾选「兼容模式」防止爆显存/卡顿");
                    }
                }
            }
        }
        PauseBtn.IsEnabled = _running && !_paused;
        ResumeBtn.IsEnabled = _running && _paused;
        UpdatePauseButtonVisual();
    }


    // 弱设备提示:一键开启「兼容模式」
    private void EnableCompatBtn_Click(object sender, RoutedEventArgs e)
    {
        FastModeCheck.IsChecked = true;      // 触发 Options_Changed → UpdateRunState 刷新,黄字自动消失
        Log("已为你开启「兼容模式」(弱设备保护)");
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

    // 暂停:当前批次(几秒~十几秒)跑完即停;暂停期间可删除未处理的项目
    private void PauseBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!_running || _paused) return;
        _paused = true;
        _resumeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        VideoService.SuspendActiveProcess();   // 冻结当前子进程:随点随停,进度零丢失
        VideoStatus.Text = "已暂停(进程已冻结,可点「恢复」继续)";
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
        VideoStatus.Text = "继续处理...";
        Log("▶ 已恢复,继续处理");
        UpdateRunState();
    }

    /// <summary>是否已处理完(成功或失败),用于动态总进度/暂停删除判断。</summary>
    private static bool IsItemDone(VideoItem v)
        => v.Progress >= 100 || v.StatusText.StartsWith("✗");

    // 所有参数变化统一刷新(CheckBox Checked/Unchecked 等 RoutedEventArgs 事件)
    private void Options_Changed(object sender, RoutedEventArgs e)
        => OnOptionChanged();

    // 以下事件参数类型各不相同,XBF 反射连接要求精确签名,必须分别提供专用处理器:
    private void Slider_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        => OnOptionChanged();

    private void Combo_Changed(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
        => OnOptionChanged();

    private static void SetScaleRadioEnabled(RadioButton rb, bool on)
    {
        if (rb == null) return;
        rb.IsEnabled = on;
        rb.Opacity = on ? 1.0 : 0.5;
    }

    // 码率下拉:选"自定义码率..."时显示码率输入行;其余档位隐藏
    private void QualityCombo_Changed(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
    {
        if (BitrateRow != null)
            BitrateRow.Visibility = QualityCombo.SelectedIndex == 5 ? Visibility.Visible : Visibility.Collapsed;
        OnOptionChanged();
    }

    // 自定义码率输入变化:内容有效则保存,无效提示但不拦截(运行前再校验)
    private void BitrateBox_TextChanged(object sender, Microsoft.UI.Xaml.Controls.TextChangedEventArgs e)
        => OnOptionChanged();

    /// <summary>解析自定义码率输入(Mbps);非法/空返回 0(0=未启用自定义码率)。</summary>
    private double ParseBitrate()
    {
        if (double.TryParse(BitrateBox.Text.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v)
            && v >= 0.1 && v <= 200)
            return v;
        return 0;
    }

    // 内容帧率「保存」按钮:立即写盘 + 提示(输入时已自动防抖保存,此为显式确认)
    private void ContentFpsSaveBtn_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (double.TryParse(ContentFpsBox.Text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var cfv) && cfv > 0)
        {
            ScheduleSave();
            ContentFpsHint.Text = $"✓ 已保存 {cfv:0.##} fps";
        }
        else
        {
            ContentFpsHint.Text = "请先填写有效数字";
        }
    }

    /// <summary>相位自动对齐:动漫/手动两个开关互通(同一设置,值相同不触发事件,无递归)。
    /// 用 Click(而非 Checked/Unchecked):Checked 会在 XAML 解析期/设置恢复期被赋值触发,
    /// 此时另一个控件可能尚未创建 → 空引用导致「视频页加载失败」(XamlParseException 0x802B000A 教训)。</summary>
    private void PhaseAlign_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        if (DedupPhaseAlignAnimeCheck == null || DedupPhaseAlignManualCheck == null) return;
        bool v = sender == DedupPhaseAlignAnimeCheck
            ? DedupPhaseAlignAnimeCheck.IsChecked == true
            : DedupPhaseAlignManualCheck.IsChecked == true;
        if (DedupPhaseAlignAnimeCheck.IsChecked != v) DedupPhaseAlignAnimeCheck.IsChecked = v;
        if (DedupPhaseAlignManualCheck.IsChecked != v) DedupPhaseAlignManualCheck.IsChecked = v;
        ScheduleSave();
    }

    private void Text_Changed(object sender, Microsoft.UI.Xaml.Controls.TextChangedEventArgs e)
        => OnOptionChanged();

    private void OnOptionChanged()
    {
        if (_suppressEvents) return;
        // XAML 解析期 Slider.Value 等赋值会提前触发事件,此时后续控件未创建
        if (InterpHint == null) return;
        // 处理中修改参数:只提示一次(本批是开始时快照,不追溯;避免刷屏)
        if (_cts != null && !_midRunWarned)
        {
            _midRunWarned = true;
            Log("⚠ 处理中修改了参数:本批已按「开始处理」时的快照执行,不追溯;新参数将在下次「开始处理」生效。");
        }
        UpdateOptions();
        UpdateAnimeFpsHint();   // 动漫档位变化 → 刷新"内容帧率≈X fps"提示
        UpdateVideoModelVisibility();   // 引擎切换 → 显示/隐藏对应引擎的模型下拉
        ScheduleSave();   // 参数记忆:变化后防抖写盘
    }

    /// <summary>选 waifu2x 显示 waifu2x 模型下拉,选 Real-ESRGAN 显示其模型下拉;并确保默认选中首个模型。</summary>
    private void UpdateVideoModelVisibility()
    {
        if (VideoWaifu2xModelCombo == null || VideoEsrganModelCombo == null) return;
        bool waifu2x = VideoEngineRadios.SelectedIndex == 0;
        VideoWaifu2xModelCombo.Visibility = waifu2x ? Visibility.Visible : Visibility.Collapsed;
        VideoEsrganModelCombo.Visibility = waifu2x ? Visibility.Collapsed : Visibility.Visible;
        // 确保各下拉有默认选中项(首次/恢复时)
        if (VideoWaifu2xModelCombo.SelectedIndex < 0) VideoWaifu2xModelCombo.SelectedIndex = 0;
        if (VideoEsrganModelCombo.SelectedIndex < 0) VideoEsrganModelCombo.SelectedIndex = 0;
    }

    /// <summary>动漫去重档位 → 内容帧率提示:内容帧率 = 输入帧率 ÷ 拍N(31:0→2, 3, 2.5, 1.6, 4)。</summary>
    private void UpdateAnimeFpsHint()
    {
        try
        {
            if (AnimeFpsHint == null) return;
            double inFps = 30;
            var selected = _videos.Count > 0 && VideoList.SelectedIndex >= 0 && VideoList.SelectedIndex < _videos.Count
                ? _videos[VideoList.SelectedIndex] : null;
            if (selected != null && double.TryParse(selected.FpsProbe,
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pf) && pf > 0)
                inFps = pf;
            double n = DedupAnimeCombo.SelectedIndex switch { 0 => 2, 1 => 3, 2 => 2.5, 3 => 1.6, _ => 4 };
            double fc = inFps / n;
            AnimeFpsHint.Text = $"内容帧率 ≈ {fc:0.#} fps(输入 {inFps:0.##} fps ÷ 拍{n:0.##})";
        }
        catch
        {
            if (AnimeFpsHint != null) AnimeFpsHint.Text = "";
        }
    }

    // 参数写盘防抖(滑条拖动会高频触发 Options_Changed,合并为一次保存)
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _saveTimer;
    private void ScheduleSave()
    {
        _saveTimer ??= CreateSaveTimer();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateSaveTimer()
    {
        var t = DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(500);
        t.IsRepeating = false;
        t.Tick += (_, _) => SaveSettings();
        return t;
    }

    private void UpdateOptions()
    {
        var up = UpscaleToggle.IsChecked == true;
        var interp = InterpToggle.IsChecked == true;
        var target = TargetFpsCheck.IsChecked == true && interp;
        var scene = SceneCheck.IsChecked == true && interp;
        // 去重可单独使用,不依赖补帧开关(UI 联动与门控用同一条件)
        var dedup = DedupCheck.IsChecked == true;
        var dedupModel = DedupModelCombo.SelectedIndex;   // 0智能 1动漫 2标准 3温和 4敏感 5手动 6内容帧率
        var multi = _videos.Count > 1;

        VideoEngineRadios.IsEnabled = up;
        VideoScaleRadios.IsEnabled = up;
        // 超分模型:未启用超分时一并置灰(与引擎/倍率一致,避免"没开超分还能选模型"的困惑)
        VideoModelLabel.Opacity = up ? 1.0 : 0.5;
        VideoWaifu2xModelCombo.IsEnabled = up;
        VideoWaifu2xModelCombo.Opacity = up ? 1.0 : 0.5;
        VideoEsrganModelCombo.IsEnabled = up;
        VideoEsrganModelCombo.Opacity = up ? 1.0 : 0.5;
        // 自定义分辨率面板 + 倍率后果提示(随选择动态变化);索引:0=1x超分 1=2x 2=3x 3=4x 4=自定义
        var scaleIdx = VideoScaleRadios.SelectedIndex;
        CustomSizePanel.Visibility = up && scaleIdx == 4 ? Visibility.Visible : Visibility.Collapsed;
        ScaleHint.Text = scaleIdx switch
        {
            0 => "1x 超分:先 2x 超分再缩回原尺寸,画质比直接放大更好,速度比 2x 略慢",
            2 => "⚠ 3x:耗时约 2 倍,高分辨率源明显变慢,建议 1080p 以下源使用",
            3 => "⚠ 4x:耗时约 4 倍,显存占用高,4K 源可能卡顿甚至爆显存,建议先试 2x",
            4 => "自定义输出分辨率:内部按 2x 超分,再精确缩放到指定宽×高(适合统一输出规格)",
            _ => "1x~2x 速度较快;倍率越高越慢、显存占用越大。3x 内部按引擎支持倍数处理",
        };
        // 超分倍率可用性:waifu2x 模型权重虽为 2x,但引擎实测 -s 3/-s 4 用级联输出正常、不崩,已放开;
        // Real-ESRGAN 有对应权重,全亮
        SetScaleRadioEnabled(VScale3xRadio, true);
        SetScaleRadioEnabled(VScale4xRadio, true);
        InterpModelCombo.IsEnabled = interp;
        // 非 2 的幂倍率(3x/12x/16x)仅 v4 架构模型(通用画质 v4.13/v4.6/v4.26)支持;其余模型按 2x 级联:
        // v2 模型选择时把这些倍率置灰,已选自动回退 2x;指定输出帧率也置灰(v2 无法精确实现非 2 幂目标帧率)
        bool v4Model = InterpModelCombo.SelectedIndex is 0 or 1 or 2;
        // v4.26(索引2)的 TTA(-x/-z)实测均卡死(引擎兼容问题):禁用「高质量 TTA」勾选并取消已勾选
        bool v426TtaBroken = InterpModelCombo.SelectedIndex == 2;
        TtaCheck.IsEnabled = interp && !v426TtaBroken;
        TtaCheck.Opacity = interp && !v426TtaBroken ? 1.0 : 0.5;
        if (v426TtaBroken && TtaCheck.IsChecked == true)
            TtaCheck.IsChecked = false;
        Scale3xRadio.IsEnabled = v4Model;
        Scale3xRadio.Opacity = v4Model ? 1.0 : 0.5;
        Scale12xRadio.IsEnabled = v4Model;
        Scale12xRadio.Opacity = v4Model ? 1.0 : 0.5;
        Scale16xRadio.IsEnabled = v4Model;
        Scale16xRadio.Opacity = v4Model ? 1.0 : 0.5;
        if (!v4Model && InterpScaleRadios.SelectedIndex is 1 or 4 or 5)
            InterpScaleRadios.SelectedIndex = 0;
        // 指定输出帧率:v2 模型(只能 2 的幂级联)无法精确实现任意目标帧率 → 置灰并提示
        TargetFpsCheck.IsEnabled = interp && v4Model;
        TargetFpsCheck.Opacity = interp && v4Model ? 1.0 : 0.5;
        TargetFpsBox.IsEnabled = interp && v4Model && TargetFpsCheck.IsChecked == true;
        TargetFpsBox.Opacity = interp && v4Model ? 1.0 : 0.5;
        if (!v4Model && TargetFpsCheck.IsChecked == true)
            TargetFpsCheck.IsChecked = false;
        // 果冻修复(运动模糊/去抖)只在补帧时有意义:不补帧置灰
        MotionBlurCombo.IsEnabled = interp;
        DeShakeCheck.IsEnabled = interp;
        MotionBlurCombo.Opacity = interp ? 1.0 : 0.5;
        DeShakeCheck.Opacity = interp ? 1.0 : 0.5;
        // 果冻修复开启时提示「会增加导出时间」(运动模糊最慢):任一开启即显示
        JellySlowHint.Visibility = interp && (MotionBlurCombo.SelectedIndex > 0 || DeShakeCheck.IsChecked == true)
            ? Visibility.Visible : Visibility.Collapsed;
        // 视频帧率:三选一(0=各视频默认帧率 1=帧率偏移 2=单独调整)仅【多视频】才展开选择;
        // 单视频只直接改「输入帧率」框,不出现那 3 个模式选项(那是多视频才有意义的"批量"概念)。
        int fpsMode = FpsModeRadios.SelectedIndex;
        bool single = !multi;
        // 切到「单独调整各视频帧率」(模式2)自动进入逐条编辑,免去再点一次按钮;切走再切回会重新进入。
        if (multi && fpsMode == 2 && _lastFpsMode != 2)
            _fpsIndividualMode = true;
        // 关键修复:离开「单独调整」模式(切到默认/帧率偏移)必须清除独立调整状态,
        // 否则帧率偏移滑条被误判为"单独调整已失效"而禁用(用户实测"第二个模式用不了"的根因)
        if (fpsMode != 2)
            _fpsIndividualMode = false;
        _lastFpsMode = fpsMode;
        // 同步各视频「帧率编辑行」可见性(与 FpsIndividualBtn_Click 一致)
        for (int i = 0; i < _videos.Count; i++)
            _videos[i].FpsEditVisibility = _fpsIndividualMode && multi && fpsMode == 2
                ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        FpsModeRadios.Visibility = single ? Visibility.Collapsed : Visibility.Visible;
        // 实时刷新每项的"有效输入帧率"徽标:默认=原帧率;偏移=原-|偏移|;单独调整=CustomFps。随滑条/模式立即联动。
        double offset = FpsOffsetSlider.Value;
        foreach (var it in _videos)
        {
            double probe = 0;
            double.TryParse(it.FpsProbe, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out probe);
            double? eff = null;
            if (fpsMode == 2) eff = it.CustomFps is > 0 ? it.CustomFps : (probe > 0 ? probe : null);
            else if (fpsMode == 1 && probe > 0) eff = Math.Max(1, probe + offset);
            it.SetEffFps(eff);
        }
        // 单视频:只显示「输入帧率」框(默认=该视频探测帧率,可写数字覆盖);多视频走下方模式选择。
        FpsDefaultHint.Visibility = single
            ? Visibility.Visible
            : (fpsMode == 0 ? Visibility.Visible : Visibility.Collapsed);
        FpsDefaultHint.Text = single
            ? "该视频按自身原始帧率处理(自动探测);如需覆盖,直接在下方「输入帧率」框改成其它值。"
            : "各视频按自身原始帧率处理(自动探测),无需任何设置。";
        FpsSingleRow.Visibility = single ? Visibility.Visible : Visibility.Collapsed;
        InputFpsBox.Visibility = single ? Visibility.Visible : Visibility.Collapsed;
        FpsOffsetPanel.Visibility = multi && fpsMode == 1 && interp ? Visibility.Visible : Visibility.Collapsed;
        FpsOffsetVal.Visibility = multi && fpsMode == 1 && interp ? Visibility.Visible : Visibility.Collapsed;
        FpsIndividualBtn.Visibility = multi && fpsMode == 2 && interp ? Visibility.Visible : Visibility.Collapsed;
        SaveFpsBtn.Visibility = multi && fpsMode == 2 && interp && _fpsIndividualMode
            ? Visibility.Visible : Visibility.Collapsed;
        // 多视频:批量统一帧率(仅「单独调整」模式显示;已固定"统一帧率",无展开选项)
        AllFpsRow.Visibility = multi && fpsMode == 2 ? Visibility.Visible : Visibility.Collapsed;
        // 单独调整模式:偏移滑条失效(置灰)
        FpsOffsetSlider.IsEnabled = multi && fpsMode == 1 && interp && !_fpsIndividualMode;
        FpsOffsetVal.Text = _fpsIndividualMode
            ? "单独调整模式:偏移已失效,直接在右侧每个视频上输入帧率"
            : FpsOffsetSlider.Value == 0
                ? "0 = 各视频用原帧率;拖滑条统一减帧率,或选「单独调整」逐个设置"
                : $"当前 {FpsOffsetSlider.Value:0}:各视频原帧率 {FpsOffsetSlider.Value:0}(如 24→{24 + FpsOffsetSlider.Value:0})";
        // 指定输出帧率时,输出倍率不再有意义 → 置灰;指定帧率可用性由上方 v4Model 逻辑统一控制
        InterpScaleRadios.IsEnabled = interp && !target;
        // 去重可单独使用(不勾补帧也能只去重导出);但去重内容帧率依赖补帧逻辑,不开补帧时用"标准/手动"更直观
        DedupCheck.IsEnabled = true;
        DedupModelCombo.IsEnabled = dedup;
        // 动漫模式:动画帧率变种(一拍N)仅动漫模式显示
        DedupAnimeRow.Visibility = dedup && dedupModel == 1 ? Visibility.Visible : Visibility.Collapsed;
        // 智能策略:仅智能模式显示(均衡/激进/保守)
        DedupSmartRow.Visibility = dedup && dedupModel == 0 ? Visibility.Visible : Visibility.Collapsed;
        // 时间步(仅 v4 模型):其它模型置灰(级联无时间步概念)
        TimeStepSlider.IsEnabled = interp && v4Model;
        TimeStepSlider.Opacity = interp && v4Model ? 1.0 : 0.5;
        // 去重手动面板:仅"手动模式"展开显示,其他模式收起隐藏(带动画)
        AnimateShowHide(DedupManualPanel, dedup && dedupModel == 2);
        // 内容帧率采样:手动模式(dedupModel==2)默认算法(UI 第 1 项,核心语义 3)时显示(行在手动面板内,随面板带动画)
        int algoUiIdx = Math.Clamp(DedupAlgoCombo.SelectedIndex, 0, _algoUiToCore.Length - 1);
        int algoCoreNow = _algoUiToCore[algoUiIdx];
        bool showFc = dedup && dedupModel == 2 && algoCoreNow == 3;
        ContentFpsRow.Visibility = showFc ? Visibility.Visible : Visibility.Collapsed;
        // 相位自动对齐(随各自面板显示):动漫网格 / 手动-内容帧率采样
        DedupPhaseAlignAnimeCheck.Visibility = dedup && dedupModel == 1 ? Visibility.Visible : Visibility.Collapsed;
        DedupPhaseAlignManualCheck.Visibility = showFc ? Visibility.Visible : Visibility.Collapsed;

        if (dedup && dedupModel == 2)   // 手动:按算法显示(用核心语义判断)
        {
            DedupAlgoCombo.IsEnabled = true;
            DedupAlgoCombo.Opacity = 1.0;
            DedupHiRow.Visibility = algoCoreNow == 0 ? Visibility.Visible : Visibility.Collapsed;
            DedupLoRow.Visibility = algoCoreNow == 0 ? Visibility.Visible : Visibility.Collapsed;
            DedupFracRow.Visibility = algoCoreNow == 0 ? Visibility.Visible : Visibility.Collapsed;
            DedupSceneRow.Visibility = algoCoreNow == 1 ? Visibility.Visible : Visibility.Collapsed;
            DedupSadRow.Visibility = algoCoreNow == 2 ? Visibility.Visible : Visibility.Collapsed;
            DedupSsimRow.Visibility = algoCoreNow == 2 ? Visibility.Visible : Visibility.Collapsed;
            ManualProtectSmallMotionCheck.Visibility = algoCoreNow == 2 ? Visibility.Visible : Visibility.Collapsed;
            // 内容帧率采样(core 3):上面的行全部隐藏,只显示内容帧率行(showFc 控制)
        }
        SceneCheck.IsEnabled = interp;
        SceneSlider.IsEnabled = scene;
        TimeStepSlider.IsEnabled = interp && v4Model;   // 时间步仅 v4 模型(rife-v4.6)支持:其它模型置灰
        TimeStepSlider.Opacity = interp && v4Model ? 1.0 : 0.5;
        // 快速模式:忽略 TTA(置灰提示)
        var fast = FastModeCheck.IsChecked == true;
        TtaCheck.IsEnabled = interp && !fast;
        TtaCheck.Opacity = fast ? 0.5 : 1.0;
        FastModeHint.Text = fast
            ? "已启用:GPU 硬解拆帧、tile 减半、单批、帧批减半+批后释放内存、忽略 TTA、硬编合帧;去重/转场/果冻修复/后处理/自定义分辨率/码率/格式均不受影响"
            : "给配置差的电脑用的:GPU 硬解拆帧、tile 减半(显存约降 4 倍)、单批处理防爆显存、帧批减半+批后释放内存、忽略 TTA、硬编合帧;去重/转场/果冻修复/后处理/自定义分辨率/码率/格式均不受影响";

        // 选择去重模式时自动把内置预设同步到手滑条(方便切到手动后继续微调);只在模式切换时生效
        if (dedup && dedupModel != _lastDedupModel)
        {
            if (dedupModel == 1) { DedupHiSlider.Value = 12; DedupLoSlider.Value = 5; DedupFracSlider.Value = 0.33; }   // 动漫模式预设(研究校准值)
        }
        _lastDedupModel = dedupModel;

        DedupHiVal.Text = DedupHiSlider.Value.ToString("0", CultureInfo.InvariantCulture);
        DedupLoVal.Text = DedupLoSlider.Value.ToString("0", CultureInfo.InvariantCulture);
        DedupFracVal.Text = DedupFracSlider.Value.ToString("0.00", CultureInfo.InvariantCulture);
        DedupSceneVal.Text = DedupSceneSlider.Value.ToString("0.000", CultureInfo.InvariantCulture);
        DedupSadVal.Text = DedupSadSlider.Value.ToString("0.0", CultureInfo.InvariantCulture);
        DedupSsimVal.Text = DedupSsimSlider.Value.ToString("0.000", CultureInfo.InvariantCulture);
        SceneVal.Text = SceneSlider.Value.ToString("0.00", CultureInfo.InvariantCulture);
        TimeStepVal.Text = TimeStepSlider.Value.ToString("0.00", CultureInfo.InvariantCulture);

        // 视频调整数值
        SharpenVal.Text = SharpenSlider.Value.ToString("0");
        ClarityVal.Text = ClaritySlider.Value.ToString("0");
        UsmVal.Text = UsmSlider.Value.ToString("0");
        DetailVal.Text = DetailSlider.Value.ToString("0");
        DeblurVal.Text = DeblurSlider.Value.ToString("0");
        FlickerVal.Text = FlickerSlider.Value.ToString("0");
        PostDenoiseVal.Text = PostDenoiseSlider.Value.ToString("0");
        PostAaVal.Text = PostAaSlider.Value.ToString("0");

        // 输出帧率提示
        var inv = CultureInfo.InvariantCulture;
        int fpsModeNow = FpsModeRadios.SelectedIndex;
        var inFps = fpsModeNow == 2
            && double.TryParse(InputFpsBox.Text, NumberStyles.Float, inv, out var f) && f > 0 ? f : 0;
        var m = InterpScaleRadios.SelectedIndex switch { 1 => 3, 2 => 4, 3 => 8, _ => 2 };
        var extras = new System.Collections.Generic.List<string>();
        if (dedup) extras.Add($"去重({DedupModelCombo.SelectedItem})");
        if (scene) extras.Add($"转场 {SceneSlider.Value:0.00}");
        if (TtaCheck.IsChecked == true) extras.Add("TTA");
        var extra = extras.Count > 0 ? " · " + string.Join(" · ", extras) : "";
        if (interp && inFps > 0)
        {
            if (target && double.TryParse(TargetFpsBox.Text, NumberStyles.Float, inv, out var tf) && tf > 0)
            {
                // 指定帧率预判(开始前就提示,不等到处理完):
                // 所需倍率 = 目标帧率 ÷ 输入帧率;显示当前倍率是否够(不够会自动凑帧数,输出仍精确目标)
                double needScale = tf / inFps;
                InterpHint.Text = $"输出:{inFps:0.##} × {m} = {inFps * m:0.##} → {tf:0.##} fps (指定){extra}";
                if (needScale > m + 0.01)
                {
                    int minInt = Math.Max(2, (int)Math.Ceiling(needScale - 0.01));
                    InterpHint.Text += $"\n提示:当前 {m}x 帧数不够,处理时会自动按 {minInt}x 补帧凑数(最小整数,输出仍精确 {tf:0.##} fps)";
                }
                else if (needScale < 1.0 - 0.01)
                    InterpHint.Text += $"\n注意:指定 {tf:0.##} fps 低于输入帧率 {inFps:0.##} fps,补帧只能增帧,输出仍为 {inFps:0.##} fps";
            }
            else
                InterpHint.Text = $"输出:{inFps:0.##} × {m} = {inFps * m:0.##} fps{extra}";
        }
        else if (interp && multi)
        {
            var off = fpsModeNow == 1 ? FpsOffsetSlider.Value : 0;
            InterpHint.Text = off == 0 && fpsModeNow == 0
                ? $"多视频:各视频用原帧率 × {m}{extra}"
                : off == 0
                    ? $"多视频:各视频用原帧率 × {m}{extra}"
                    : $"多视频:各视频原帧率 {off:0} × {m}{extra}";
        }
        else
        {
            // 参数区的输出帧率提示不再显示"至少启用一项处理"——该提醒已移到「开始处理」按钮下方(RunHint)
            InterpHint.Text = up ? "输出帧率 = 输入帧率" : "";
        }
        // 3x 补帧仅 v4 架构模型支持:提前提示,避免任务批量失败
        if (interp && m == 3 && InterpModelCombo.SelectedIndex is 3 or 4 or 5 or 6)
            InterpHint.Text += "\n⚠ 3x 补帧需要 v4 架构模型,请选择 通用画质最新 (RIFE v4.13) 或 通用画质 (RIFE v4.6)";
        UpdateRunState();
        _ = RefreshVideoOutSpec();   // 超分/补帧/目标帧率变化时刷新左下角输出规格
    }

    // 重置为默认参数
    private void VideoResetBtn_Click(object sender, RoutedEventArgs e)
    {
        _suppressEvents = true;
        UpscaleToggle.IsChecked = true;
        VideoEngineRadios.SelectedIndex = 0;
        VideoScaleRadios.SelectedIndex = 1;   // 默认 2x
        CustomWidthBox.Text = "1920";
        CustomHeightBox.Text = "1080";
        InterpToggle.IsChecked = false;
        InterpModelCombo.SelectedIndex = 0;
        InputFpsBox.Text = "30";
        InterpScaleRadios.SelectedIndex = 0;
        TargetFpsCheck.IsChecked = false;
        TargetFpsBox.Text = "";
        FpsModeRadios.SelectedIndex = 0;   // 视频帧率:默认「各视频默认帧率」
        FpsOffsetSlider.Value = 0;
        DedupCheck.IsChecked = false;
        DedupModelCombo.SelectedIndex = 2;   // 去重模型默认=手动模式(算法默认=内容帧率采样,见 XAML)
        DedupAnimeCombo.SelectedIndex = 0;   // 动漫模式:动画帧率变种,默认一拍二(最常用)
        DedupAlgoCombo.SelectedIndex = 0;   // 手动算法默认=内容帧率采样(UI 第 1 项)
        DedupHiSlider.Value = 12;
        DedupLoSlider.Value = 5;
        DedupFracSlider.Value = 0.33;
        DedupSceneSlider.Value = 0.01;
        ContentFpsBox.Text = "";
        FpsOffsetSlider.Value = 0;
        SceneCheck.IsChecked = false;
        SceneSlider.Value = 0.3;
        TimeStepSlider.Value = 0.5;
        TtaCheck.IsChecked = false;
        SharpenSlider.Value = 0;
        ClaritySlider.Value = 0;
        UsmSlider.Value = 0;
        DetailSlider.Value = 0;
        DeblurSlider.Value = 0;
        MotionBlurCombo.SelectedIndex = 0;
        DeShakeCheck.IsChecked = false;
        QualityCombo.SelectedIndex = 0;
        BitrateBox.Text = "";
        CodecCombo.SelectedIndex = 0;
        if (BitrateRow != null) BitrateRow.Visibility = Visibility.Collapsed;
        FormatCombo.SelectedIndex = 0;
        FastModeCheck.IsChecked = false;
        // 补全剩余参数(真正"重置所有"):视频降噪/后处理杂色/抗锯齿/去频闪/VFR/去重智能/微动防线/静音
        DenoiseToggle.IsChecked = false;
        DenoiseStrongRadios.SelectedIndex = 0;
        FlickerSlider.Value = 0;
        PostDenoiseSlider.Value = 0;
        PostAaSlider.Value = 0;
        VfrModeRadios.SelectedIndex = 0;
        DedupSmartCombo.SelectedIndex = 0;
        ManualProtectSmallMotionCheck.IsChecked = true;
        MuteCheck.IsChecked = false;
        if (AllFpsBox != null) AllFpsBox.Text = "30";   // 多视频输入帧率框
        _suppressEvents = false;
        UpdateOptions();
        SaveSettings();
        Log("已重置所有参数为默认值");
    }

    // 只重置视频调整(后处理)参数
    private void PostResetBtn_Click(object sender, RoutedEventArgs e)
    {
        _suppressEvents = true;
        SharpenSlider.Value = 0;
        ClaritySlider.Value = 0;
        UsmSlider.Value = 0;
        DetailSlider.Value = 0;
        DeblurSlider.Value = 0;
        FlickerSlider.Value = 0;
        PostDenoiseSlider.Value = 0;
        PostAaSlider.Value = 0;
        _suppressEvents = false;
        UpdateOptions();
        SaveSettings();
        Log("已重置视频调整参数");
    }

    // 各滑条板块的单独重置
    private void QualityReset_Click(object sender, RoutedEventArgs e)
    {
        _suppressEvents = true;
        QualityCombo.SelectedIndex = 0;
        BitrateBox.Text = "";
        _suppressEvents = false;
        if (BitrateRow != null) BitrateRow.Visibility = Visibility.Collapsed;
        UpdateOptions();
        SaveSettings();
        Log("码率已重置为自动");
    }

    // 去重手动面板的"重置"按钮:每个按钮只重置自己那一项(按钮 Tag 指定),不再一键清整套
    private void ResetDedupBtn_Click(object sender, RoutedEventArgs e)
    {
        string key = (sender as FrameworkElement)?.Tag as string ?? "";
        _suppressEvents = true;
        switch (key)
        {
            case "hi": DedupHiSlider.Value = 12; break;
            case "lo": DedupLoSlider.Value = 5; break;
            case "frac": DedupFracSlider.Value = 0.33; break;
            case "scene": DedupSceneSlider.Value = 0.01; break;
            case "sad": DedupSadSlider.Value = 3.0; break;
            case "ssim": DedupSsimSlider.Value = 0.97; break;
            default:
                // 无 Tag(兜底):整组回默认
                DedupHiSlider.Value = 12; DedupLoSlider.Value = 5; DedupFracSlider.Value = 0.33;
                DedupSceneSlider.Value = 0.01; DedupSadSlider.Value = 3.0; DedupSsimSlider.Value = 0.97;
                break;
        }
        _suppressEvents = false;
        UpdateOptions();
        SaveSettings();
        Log($"去重参数「{key}」已重置为默认");
    }

    private void ResetSceneBtn_Click(object sender, RoutedEventArgs e)
    {
        _suppressEvents = true;
        SceneSlider.Value = 0.3;
        _suppressEvents = false;
        UpdateOptions();
        SaveSettings();
    }

    private void ResetTimeStepBtn_Click(object sender, RoutedEventArgs e)
    {
        _suppressEvents = true;
        TimeStepSlider.Value = 0.5;
        _suppressEvents = false;
        UpdateOptions();
        SaveSettings();
    }

    // 多视频「统一输入帧率」:一键把所有视频的输入帧率设为同一个值,并锁定右侧编辑(想个别改:点「恢复」解锁)
    private void AllFpsApplyBtn_Click(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(AllFpsBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f > 0)
        {
            int n = 0;
            foreach (var it in _videos)
            {
                if (!(it.CustomFps is > 0 && Math.Abs(it.CustomFps.Value - f) < 0.01))
                {
                    it.CustomFps = f;
                    it.ClearDraft();   // 清暂存输入,让右侧帧率框立即显示已应用的值(否则看似"没生效")
                    n++;
                }
                it.FpsEditEnabled = false;   // 统一后锁定:只能看,个别调整先点「恢复」
            }
            Log($"统一输入帧率:所有视频设为 {f:0.##} fps(共 {_videos.Count} 个,已锁定;个别调整请先点「恢复」)");
            UpdateOptions();
        }
        else
        {
            Log("⚠ 统一帧率无效:请输入正数(如 24/30/60)");
        }
    }

    private void ResetOffsetBtn_Click(object sender, RoutedEventArgs e)
    {
        _suppressEvents = true;
        FpsOffsetSlider.Value = 0;
        _suppressEvents = false;
        UpdateOptions();
        SaveSettings();
    }

    private void Remember_Changed(object sender, RoutedEventArgs e)
    {
        // 关键:加载设置期间(控件恢复中)不能触发保存,否则会把默认值覆盖写盘
        if (_suppressEvents) return;
        SaveSettings();
    }

    // ---------- 参数记忆 ----------
    // 兼容旧设置:果冻/运动模糊早期是 bool 开关,现为 0-3 档位;旧值 true→弱(1),false→关(0)
    private sealed class BoolOrIntConverter : System.Text.Json.Serialization.JsonConverter<int>
    {
        public override int Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert,
            System.Text.Json.JsonSerializerOptions options)
        {
            if (reader.TokenType == System.Text.Json.JsonTokenType.True) return 1;
            if (reader.TokenType == System.Text.Json.JsonTokenType.False) return 0;
            return reader.GetInt32();
        }

        public override void Write(System.Text.Json.Utf8JsonWriter writer, int value,
            System.Text.Json.JsonSerializerOptions options)
            => writer.WriteNumberValue(value);
    }

    private sealed class VideoSettings
    {
        public bool Remember { get; set; }
        public bool Up { get; set; } = true;
        public int Engine { get; set; }
        public int Scale { get; set; } = 1;
        public int Gpu { get; set; }
        public bool Interp { get; set; }
        public int Model { get; set; }         // 补帧模型索引(InterpModelCombo)
        public int UpWaifu2xModel { get; set; }   // 视频超分 waifu2x 模型索引(VideoWaifu2xModelCombo)
        public int UpEsrganModel { get; set; }    // 视频超分 Real-ESRGAN 模型索引(VideoEsrganModelCombo)
        public int InterpScale { get; set; }
        public bool Target { get; set; }
        public string TargetFps { get; set; } = "";
        // 可变帧率检测优化:VfrMode=0 自动(检测到才启用) 1 不启用;VfrExpanded=面板是否展开(默认收起)
        public int VfrMode { get; set; }
        public bool VfrExpanded { get; set; }
        // 补帧输出帧率基准:0=真实时间轴插值(推荐) 1=匀速帧速率插值
        public int FpsBase { get; set; }
        // 视频帧率面板:FpsMode=0 各视频默认帧率 1 帧率偏移 2 单独调整;FpsExpanded=面板是否展开
        public int FpsMode { get; set; }
        public double FpsOffset { get; set; }
        public bool FpsExpanded { get; set; } = true;   // 默认展开视频帧率面板(用户点「视频帧率」可收起)
        public bool DedupOn { get; set; }
        public int DedupModel { get; set; }   // 0智能 1动漫 2手动
        public int DedupAnime { get; set; } = 0;   // 动漫模式:0=去除一拍二(默认) 1=去除一拍二与一拍三(混合)
        public int DedupSmart { get; set; }        // 智能策略:0均衡(默认) 1激进 2保守
        public double DedupThr { get; set; } = 0.01;
        public bool Scene { get; set; }
        public double SceneThr { get; set; } = 0.3;
        public double TimeStep { get; set; } = 0.5;
        public bool Tta { get; set; } = false;   // TTA(高质量)默认关:v1.0 起改,默认开会让新机器慢 5~7 倍(用户感知"补帧慢/卡");要画质用户手动开
        public string OutDir { get; set; } = "";
        public string CustomW { get; set; } = "1920";
        public string CustomH { get; set; } = "1080";
        // 手动去重:算法(0=重复帧检测 1=画面变化阈值 2=帧差+SSIM)+ 自由参数
        public int DedupAlgo { get; set; } = 0;
        public int DedupHi { get; set; } = 12;
        public int DedupLo { get; set; } = 5;
        public double DedupFrac { get; set; } = 0.33;
        public double DedupSadThr { get; set; } = 3.0;
        public double DedupSsimThr { get; set; } = 0.97;
        // (以下高级字段已无 UI/无读写,2026-08-29 清理:处理参数均为硬编码默认值,删除零影响)
        public double ContentFps { get; set; }      // 内容帧率模式:用户指定内容帧率(0=处理时自动检测)
        public bool DedupMotionComp { get; set; } = true;    // 镜头运动补偿(背景 pan 下识别人物定格):研究推荐默认开
        public bool DedupOnlyTrueHold { get; set; } = true;  // 只删"真定格"(SSIM≥0.995):研究推荐默认开(低阈值是"虚高+跳帧"根因)
        public bool ManualProtectSmallMotion { get; set; } = true;  // 手动模式"微动防线":默认开(防口型/眨眼误删)
        public bool DedupPhaseAlign { get; set; } = true;   // 网格模式"相位自动对齐":默认开(高置信才启用)
        public int PostSharpen { get; set; }
        public int PostClarity { get; set; }
        public int PostUsm { get; set; }
        public int PostDetail { get; set; }
        public int PostDeblur { get; set; }
        public int PostFlicker { get; set; }
        public int PostDenoise { get; set; }
        public int PostAa { get; set; }
        [System.Text.Json.Serialization.JsonConverter(typeof(BoolOrIntConverter))]
        public int Jello { get; set; }
        [System.Text.Json.Serialization.JsonConverter(typeof(BoolOrIntConverter))]
        public int MotionBlur { get; set; }
        public bool DeShake { get; set; }
        public int Quality { get; set; }
        public double BitrateMbps { get; set; }
        public int Codec { get; set; }
        public int Format { get; set; }
        public bool FastMode { get; set; }
        public bool Mute { get; set; }
        public bool VideoDenoiseOn { get; set; }
        public int VideoDenoiseStrong { get; set; }
    }

    private static string SettingsFile => ParaPaths.SettingsFile("video-settings.json");

    // ---------- 参数预设 ----------
    /// <summary>一个视频参数预设:命名 + 保存时间 + 一套 VideoSettings 快照。上限 100 个。</summary>
    private sealed class VideoPreset
    {
        public string Name { get; set; } = "";
        public string SavedAt { get; set; } = "";
        public bool IsOfficial { get; set; }   // 官方预设(程序内置):悬停显示"官方"、不显示日期时间;用户预设=普通条目
        public VideoSettings Params { get; set; } = new();
    }

    /// <summary>预设文件路径(%LOCALAPPDATA%\ALHPro\settings\video-presets.json)。</summary>
    private static string PresetFile => ParaPaths.SettingsFile("video-presets.json");

    /// <summary>读取全部预设(按创建时间排序;坏项跳过)。失败/空返回空列表。</summary>
    private static List<VideoPreset> LoadPresets()
    {
        try
        {
            if (!File.Exists(PresetFile)) return new();
            var list = System.Text.Json.JsonSerializer.Deserialize<List<VideoPreset>>(File.ReadAllText(PresetFile));
            return list ?? new();
        }
        catch { return new(); }   // 文件损坏/无法反序列化 → 视为空,不崩溃
    }

    /// <summary>把预设列表写盘。空列表则删除文件。</summary>
    private static void SavePresets(List<VideoPreset> list)
    {
        try
        {
            if (list.Count == 0) { if (File.Exists(PresetFile)) File.Delete(PresetFile); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(PresetFile)!);
            File.WriteAllText(PresetFile, System.Text.Json.JsonSerializer.Serialize(list));
        }
        catch { }
    }

    private const int MaxPresets = 100;   // 上限 100 个预设

    /// <summary>官方内置预设定义(名字 + 一套默认参数)。以后要加官方预设,在这里加一项即可,下次更新自动带上。</summary>
    private static (string Name, Func<VideoSettings> Make)[] BuiltinPresets() => new[]
    {
        ( "通用画质增强 不含补帧", new Func<VideoSettings>(() => new VideoSettings
        {
            Remember = false, Up = true, Engine = 0, Scale = 1, Gpu = 0,
            Interp = false, Model = 0, UpWaifu2xModel = 0, UpEsrganModel = 0, InterpScale = 0,
            Target = false, TargetFps = "", VfrMode = 0, VfrExpanded = false, FpsBase = 0, FpsMode = 0, FpsOffset = 0, FpsExpanded = true,
            DedupOn = false, DedupModel = 0, DedupAnime = 0, DedupSmart = 0, DedupThr = 0.01,
            Scene = false, SceneThr = 0.3, TimeStep = 0.5, Tta = false, OutDir = "", CustomW = "1920", CustomH = "1080",
            DedupAlgo = 0, DedupHi = 12, DedupLo = 5, DedupFrac = 0.33, DedupSadThr = 3, DedupSsimThr = 0.97, ContentFps = 0,
            DedupMotionComp = true, DedupOnlyTrueHold = true, ManualProtectSmallMotion = true, DedupPhaseAlign = true,
            PostSharpen = 25, PostClarity = 15, PostUsm = 20, PostDetail = 30, PostDeblur = 15, PostFlicker = 0, PostDenoise = 10, PostAa = 30,
            Jello = 0, MotionBlur = 0, DeShake = false, Quality = 0, BitrateMbps = 0, Codec = 0, Format = 0,
            FastMode = false, Mute = false, VideoDenoiseOn = false, VideoDenoiseStrong = -1,
        })),
        ( "动漫通用", new Func<VideoSettings>(() => new VideoSettings
        {
            Remember = true, Up = true, Engine = 0, Scale = 1, Gpu = 0,
            Interp = true, Model = 0, UpWaifu2xModel = 1, UpEsrganModel = 0, InterpScale = 2,
            Target = false, TargetFps = "", VfrMode = 0, VfrExpanded = false, FpsBase = 0, FpsMode = 0, FpsOffset = 0, FpsExpanded = true,
            DedupOn = true, DedupModel = 0, DedupAnime = 0, DedupSmart = 0, DedupThr = 0.01,
            Scene = false, SceneThr = 0.3, TimeStep = 0.5, Tta = false, OutDir = "", CustomW = "1920", CustomH = "1080",
            DedupAlgo = 3, DedupHi = 12, DedupLo = 5, DedupFrac = 0.33, DedupSadThr = 3, DedupSsimThr = 0.97, ContentFps = 0,
            DedupMotionComp = true, DedupOnlyTrueHold = true, ManualProtectSmallMotion = true, DedupPhaseAlign = true,
            PostSharpen = 20, PostClarity = 20, PostUsm = 20, PostDetail = 30, PostDeblur = 20, PostFlicker = 5, PostDenoise = 10, PostAa = 50,
            Jello = 0, MotionBlur = 0, DeShake = false, Quality = 0, BitrateMbps = 0, Codec = 0, Format = 0,
            FastMode = false, Mute = false, VideoDenoiseOn = true, VideoDenoiseStrong = 1,
        })),
    };

    /// <summary>确保每个官方内置预设存在:缺失则用官方默认创建(标记官方);已有同名则标记为官方(把用户保存的同款变成官方)。
    /// 绝不覆盖/删除用户已有预设。以后加官方预设只需在 BuiltinPresets() 加一项。</summary>
    private void EnsureBuiltinPresets()
    {
        try
        {
            var list = LoadPresets();
            bool changed = false;
            foreach (var (name, make) in BuiltinPresets())
            {
                var existing = list.FirstOrDefault(x => x.Name == name);
                if (existing != null)
                {
                    if (!existing.IsOfficial) { existing.IsOfficial = true; changed = true; }
                    continue;
                }
                // 缺失 → 用官方默认参数创建,标记官方,排在已有预设之前(官方靠前)
                var p = new VideoPreset
                {
                    Name = name,
                    SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm") + " · 内置",
                    IsOfficial = true,
                    Params = make(),
                };
                list.Insert(0, p);
                changed = true;
                AppLogger.Info($"[内置预设] 已创建官方预设「{name}」");
            }
            if (changed)
            {
                SavePresets(list);
                AppLogger.Info("[内置预设] 官方预设检查完成(缺失已补/同名已标记官方,用户预设未动)");
            }
        }
        catch { }
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsFile)) return;
            var d = System.Text.Json.JsonSerializer.Deserialize<VideoSettings>(File.ReadAllText(SettingsFile));
            if (d is null) return;
            // 诊断:记录设置文件读到的值与时间戳(排查"记不住码率/格式")
            AppLogger.Info($"[记忆] 视频设置加载: Quality={d.Quality}, Format={d.Format}, Codec={d.Codec}, Remember={d.Remember}, 文件时间={File.GetLastWriteTime(SettingsFile):HH:mm:ss}");
            _suppressEvents = true;
            VideoRememberCheck.IsChecked = d.Remember;
            if (d.Remember) ApplyVideoParams(d);
            _suppressEvents = false;
            UpdateOptions();   // 恢复后刷新 UI 状态(自定义分辨率面板显隐/提示/滑条数值等)
        }
        catch { _suppressEvents = false; }
    }

    /// <summary>把一份 VideoSettings 快照应用到当前页面 UI(校验范围后赋值,避免越界)。
    /// 供「记住上次参数加载」(d.Remember)与「应用参数预设」共用。
    /// 调用方负责用 _suppressEvents 抑制事件回写(避免应用过程中触发 SaveSettings)。</summary>
    private void ApplyVideoParams(VideoSettings d)
    {
        UpscaleToggle.IsChecked = d.Up;
        // 兼容旧设置:旧值 2(Real-CUGAN,已移除)→ 1(Real-ESRGAN);0=waifu2x 1=Real-ESRGAN
        if (d.Engine == 2) VideoEngineRadios.SelectedIndex = 1;
        else if (d.Engine is >= 0 and <= 1) VideoEngineRadios.SelectedIndex = d.Engine;
        if (d.Scale is >= 0 and <= 4)
            VideoScaleRadios.SelectedIndex = d.Scale;   // 预设存的是当前真实倍率索引(0=1x,1=2x,2=3x,3=4x,4=自定义)
        if (!string.IsNullOrWhiteSpace(d.CustomW)) CustomWidthBox.Text = d.CustomW;
        if (!string.IsNullOrWhiteSpace(d.CustomH)) CustomHeightBox.Text = d.CustomH;
        if (d.PostSharpen is >= 0 and <= 100) SharpenSlider.Value = d.PostSharpen;
        if (d.PostClarity is >= 0 and <= 100) ClaritySlider.Value = d.PostClarity;
        if (d.PostUsm is >= 0 and <= 100) UsmSlider.Value = d.PostUsm;
        if (d.PostDetail is >= 0 and <= 100) DetailSlider.Value = d.PostDetail;
        if (d.PostDeblur is >= 0 and <= 100) DeblurSlider.Value = d.PostDeblur;
        if (d.PostFlicker is >= 0 and <= 100) FlickerSlider.Value = d.PostFlicker;
        if (d.PostDenoise is >= 0 and <= 100) PostDenoiseSlider.Value = d.PostDenoise;
        if (d.PostAa is >= 0 and <= 100) PostAaSlider.Value = d.PostAa;
        if (d.MotionBlur is >= 0 and <= 3) MotionBlurCombo.SelectedIndex = d.MotionBlur;
        DeShakeCheck.IsChecked = d.DeShake;
        if (d.Quality is >= 0 and <= 5) QualityCombo.SelectedIndex = d.Quality;
        if (d.BitrateMbps > 0) BitrateBox.Text = d.BitrateMbps.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        if (d.Codec is >= 0 and <= 1) CodecCombo.SelectedIndex = d.Codec;
        if (d.Format is 0 or 1) FormatCombo.SelectedIndex = d.Format;
        FastModeCheck.IsChecked = d.FastMode;
        MuteCheck.IsChecked = d.Mute;
        DenoiseToggle.IsChecked = d.VideoDenoiseOn;
        if (d.VideoDenoiseStrong is >= 0 and <= 2) DenoiseStrongRadios.SelectedIndex = d.VideoDenoiseStrong;
        InterpToggle.IsChecked = d.Interp;
        if (d.Model is >= 0 && d.Model < InterpModelCombo.Items.Count) InterpModelCombo.SelectedIndex = d.Model;
        // 恢复超分模型(waifu2x / Real-ESRGAN,各自按引擎下拉索引,越界回退 0)
        if (VideoWaifu2xModelCombo.Items.Count > 0 && d.UpWaifu2xModel is >= 0 && d.UpWaifu2xModel < VideoWaifu2xModelCombo.Items.Count)
            VideoWaifu2xModelCombo.SelectedIndex = d.UpWaifu2xModel;
        else VideoWaifu2xModelCombo.SelectedIndex = 0;
        if (VideoEsrganModelCombo.Items.Count > 0 && d.UpEsrganModel is >= 0 && d.UpEsrganModel < VideoEsrganModelCombo.Items.Count)
            VideoEsrganModelCombo.SelectedIndex = d.UpEsrganModel;
        else VideoEsrganModelCombo.SelectedIndex = 0;
        if (d.InterpScale is >= 0 and <= 3) InterpScaleRadios.SelectedIndex = d.InterpScale;
        TargetFpsCheck.IsChecked = d.Target;
        if (!string.IsNullOrWhiteSpace(d.TargetFps)) TargetFpsBox.Text = d.TargetFps;
        if (d.VfrMode is 0 or 1) VfrModeRadios.SelectedIndex = d.VfrMode;
        VfrPanel.Visibility = d.VfrExpanded ? Visibility.Visible : Visibility.Collapsed;
        VfrToggleBtn.Content = d.VfrExpanded ? "可变帧率保护 ▴" : "可变帧率保护 ▾";
        if (d.FpsBase is 0 or 1) FpsBaseCombo.SelectedIndex = d.FpsBase;
        if (d.FpsMode is 0 or 1 or 2) FpsModeRadios.SelectedIndex = d.FpsMode;
        if (d.FpsOffset is >= -20 and <= 0) FpsOffsetSlider.Value = d.FpsOffset;
        FpsPanel.Visibility = d.FpsExpanded ? Visibility.Visible : Visibility.Collapsed;
        FpsToggleBtn.Content = d.FpsExpanded ? "视频帧率 ▴" : "视频帧率 ▾";
        DedupCheck.IsChecked = d.DedupOn;   // 预设里去重开关,勾/不勾都要设置(否则关的预设不会取消勾选)
        if (d.DedupModel is >= 0 and <= 5)
            DedupModelCombo.SelectedIndex = d.DedupModel;   // 预设存当前去重模式索引,直接赋
        if (d.DedupAnime is >= 0 and <= 6)
            DedupAnimeCombo.SelectedIndex = d.DedupAnime;   // 预设存当前档位索引,直接赋
        if (d.DedupSmart is 0 or 1 or 2) DedupSmartCombo.SelectedIndex = d.DedupSmart;
        if (d.DedupAlgo is >= 0 and <= 3) DedupAlgoCombo.SelectedIndex = _algoCoreToUi[d.DedupAlgo];
        if (d.DedupHi is >= 4 and <= 24) DedupHiSlider.Value = d.DedupHi;
        if (d.DedupLo is >= 2 and <= 10) DedupLoSlider.Value = d.DedupLo;
        if (d.DedupFrac is >= 0.1 and <= 0.6) DedupFracSlider.Value = d.DedupFrac;
        if (d.DedupSadThr is >= 0.5 and <= 10) DedupSadSlider.Value = d.DedupSadThr;
        if (d.DedupSsimThr is >= 0.9 and <= 0.999) DedupSsimSlider.Value = d.DedupSsimThr;
        ManualProtectSmallMotionCheck.IsChecked = d.ManualProtectSmallMotion;
        var pa = d.DedupPhaseAlign;
        DedupPhaseAlignAnimeCheck.IsChecked = pa;
        DedupPhaseAlignManualCheck.IsChecked = pa;
        if (d.ContentFps is >= 1 and <= 120)
            ContentFpsBox.Text = d.ContentFps.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        if (d.DedupThr is >= 0.001 and <= 0.5) DedupSceneSlider.Value = d.DedupThr;
        SceneCheck.IsChecked = d.Scene;
        // 转场阈值:旧/非法设置(如字段缺失反序列化为 0)回退默认 0.3。阈值 0 会被处理端当作"不检测转场"(见 VideoService sceneThreshold is > 0),导致勾选转场识别却无效果。
        SceneSlider.Value = d.SceneThr is > 0 and <= 1 ? d.SceneThr : 0.3;
        // 时间步:旧/非法设置回退默认 0.5(滑条最小 0.05,字段缺失会停在 0.05,与默认/重置 0.5 不一致)。
        TimeStepSlider.Value = d.TimeStep is >= 0.05 and <= 0.95 ? d.TimeStep : 0.5;
        TtaCheck.IsChecked = d.Tta;
        if (!string.IsNullOrWhiteSpace(d.OutDir) && Directory.Exists(d.OutDir))
        {
            OutFileBox.Text = d.OutDir;
            _customOutDir = d.OutDir;
        }
    }

    // ---------- 参数预设 UI 逻辑 ----------
    /// <summary>把当前页面全部参数存为一个预设(命名对话框;上限 100)。</summary>
    private async void SavePresetBtn_Click(object sender, RoutedEventArgs e)
    {
        var list = LoadPresets();
        if (list.Count >= MaxPresets)
        {
            AppLogger.UserAction("视频:存为预设被拒(已达上限 100)");
            await ShowPresetHintAsync($"已达上限 {MaxPresets} 个预设,请先删除部分预设再新建。");
            return;
        }
        // 命名对话框(带默认名"预设 N")
        string defaultName = "预设 " + (list.Count + 1);
        var box = new TextBox { Text = defaultName, PlaceholderText = "给它起个名字(如:动漫 4x 补帧优)" };
        var dlg = new ContentDialog
        {
            Title = "保存为预设",
            Content = new StackPanel
            {
                Spacing = 10,
                Children = {
                    new TextBlock { Text = "记录当前全部处理参数(超分/后处理/补帧/去重/码率/格式等,不含输出路径)。", TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap },
                    box,
                },
            },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        try { if (await dlg.ShowAsync() != ContentDialogResult.Primary) return; } catch { return; }
        string name = box.Text.Trim();
        if (name.Length == 0) name = defaultName;
        var preset = new VideoPreset
        {
            Name = name,
            SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            Params = CollectVideoParams(),
        };
        list.Add(preset);
        SavePresets(list);
        AppLogger.UserAction($"视频:存为预设「{name}」(共 {list.Count} 个)");
        await ShowPresetHintAsync($"已保存预设「{name}」。点「使用预设」可查看、应用、删除。");
    }

    /// <summary>打开预设窗口:一个独立弹窗,可滚动列表选择预设(点选高亮)、排序、应用、删除。
    /// 注意:ContentDialog 不能嵌套显示,应用/删除的确认与反馈都先关闭本窗口再弹(避免同窗口冲突)。</summary>
    private async void OpenPresetWindowBtn_Click(object sender, RoutedEventArgs e)
    {
        var list = LoadPresets();
        if (list.Count == 0)
        {
            await ShowPresetHintAsync("还没有任何预设。先点「保存预设」保存一个。");
            return;
        }
        string? pendingDel = null;   // 行内删除二次确认:记录待确认的预设名(非 null = 已点一次删除)
        bool exportMode = false;     // 导出多选模式:true=行显示复选框(勾选导出),false=垃圾桶
        var exportChecks = new System.Collections.Generic.List<(VideoPreset p, Microsoft.UI.Xaml.Controls.CheckBox cb)>();
        var sortCombo = new ComboBox { HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch };
        sortCombo.Items.Add("按创建时间(默认)");
        sortCombo.Items.Add("按名字 A→Z");
        sortCombo.Items.Add("按最近修改");
        sortCombo.SelectedIndex = 0;
        var topBar = new Grid { ColumnSpacing = 8 };
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = Microsoft.UI.Xaml.GridLength.Auto });
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(1, Microsoft.UI.Xaml.GridUnitType.Star) });
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = Microsoft.UI.Xaml.GridLength.Auto });
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = Microsoft.UI.Xaml.GridLength.Auto });
        var sortLabel = new TextBlock { Text = "排序:", VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center };
        var exportBtn = new Button { Content = "导出", FontSize = 12, Padding = new Microsoft.UI.Xaml.Thickness(10, 4, 10, 4) };
        var importBtn = new Button { Content = "导入", FontSize = 12, Padding = new Microsoft.UI.Xaml.Thickness(10, 4, 10, 4) };
        Grid.SetColumn(sortLabel, 0); Grid.SetColumn(sortCombo, 1); Grid.SetColumn(exportBtn, 2); Grid.SetColumn(importBtn, 3);
        topBar.Children.Add(sortLabel); topBar.Children.Add(sortCombo); topBar.Children.Add(exportBtn); topBar.Children.Add(importBtn);

        // 预设列表:用 ListView 系统原生选中高亮(保留 WinUI3 自带蓝色选中标识);
        // 不重设 ItemContainerStyle(否则会丢掉系统原生选中视觉),item 内容自己控制 padding
        var listView = new ListView { SelectionMode = Microsoft.UI.Xaml.Controls.ListViewSelectionMode.Single, MaxHeight = 360 };

        // 底部按钮:左「关闭」(灰) 右「应用预设」(蓝),自己控制位置/颜色(不用系统按钮,避免位置反转)
        var closeBtn = new Button { Content = "关闭", HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch };
        var applyBtn = new Button
        {
            Content = "应用预设",
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch,
            Style = (Microsoft.UI.Xaml.Style)App.Current.Resources["AccentButtonStyle"],
        };
        var bottomBar = new Grid { ColumnSpacing = 8 };
        bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(1, Microsoft.UI.Xaml.GridUnitType.Star) });
        bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(1, Microsoft.UI.Xaml.GridUnitType.Star) });
        Grid.SetColumn(closeBtn, 0); Grid.SetColumn(applyBtn, 1);
        bottomBar.Children.Add(closeBtn); bottomBar.Children.Add(applyBtn);

        // 底部按钮随模式切换:普通态=[关闭|应用预设],导出态=[取消|确定导出]
        void SetBottomMode()
        {
            if (exportMode)
            {
                closeBtn.Content = "取消";
                applyBtn.Content = "确定导出";
            }
            else
            {
                closeBtn.Content = "关闭";
                applyBtn.Content = "应用预设";
            }
        }

        var inner = new StackPanel { Spacing = 12 };
        inner.Children.Add(topBar);
        inner.Children.Add(listView);
        inner.Children.Add(bottomBar);

        // 系统按钮留空(自定义按钮接管)
        ContentDialog dlg = new()
        {
            Title = "选项",
            Content = inner,
            CloseButtonText = "",
            XamlRoot = this.XamlRoot,
        };

        // 刷新列表:每行=预设名(撑满)+ 最右小删除图标;行间分隔线(放在 item 内底部);悬停看摘要(限宽换行)
        void RebuildList()
        {
            var cur = LoadPresets();
            exportChecks.Clear();   // 每次重建都清空勾选记录(会随行重建重新填充)
            switch (sortCombo.SelectedIndex)
            {
                case 1: cur = cur.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList(); break;
                case 2: cur = cur.OrderByDescending(x => x.SavedAt, StringComparer.OrdinalIgnoreCase).ToList(); break;
            }
            listView.Items.Clear();
            for (int i = 0; i < cur.Count; i++)
            {
                var itemPanel = new StackPanel { Padding = new Microsoft.UI.Xaml.Thickness(12, 8, 4, 8) };
                // 行:预设名(撑满)+ 最右小删除图标
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(1, Microsoft.UI.Xaml.GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = Microsoft.UI.Xaml.GridLength.Auto });
                // 行右侧:普通态=删除按钮;确认态=「确定删除?」+ ✓/✕
                var name = new TextBlock { Text = cur[i].Name + (cur[i].IsOfficial ? "  [官方]" : ""), FontSize = 14, VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center };
                var tipText = new TextBlock { Text = BuildPresetSummary(cur[i]), TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap, MaxWidth = 300 };
                ToolTipService.SetToolTip(name, tipText);
                var presetName = cur[i].Name;
                if (pendingDel == presetName)
                {
                    // 确认态:预设名被覆盖为「确定要删除此预设吗?」,右侧放 ✓(确认)/✕(取消)
                    name.Text = "确定要删除此预设吗?";
                    name.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 229, 72, 77));
                    var confirmSpan = new StackPanel { Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal, Spacing = 6, VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center };
                    var okBtn = new Button { Content = "✓", FontSize = 14, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 168, 0)), MinWidth = 0, Padding = new Microsoft.UI.Xaml.Thickness(6, 2, 6, 2), Background = null, BorderThickness = new Microsoft.UI.Xaml.Thickness(0) };
                    var cancelBtn = new Button { Content = "✕", FontSize = 14, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 229, 72, 77)), MinWidth = 0, Padding = new Microsoft.UI.Xaml.Thickness(6, 2, 6, 2), Background = null, BorderThickness = new Microsoft.UI.Xaml.Thickness(0) };
                    ToolTipService.SetToolTip(okBtn, "确认删除");
                    ToolTipService.SetToolTip(cancelBtn, "取消");
                    okBtn.Click += (_, _) =>
                    {
                        var latest = LoadPresets();
                        var hit = latest.FirstOrDefault(x => x.Name == presetName);
                        if (hit != null) { latest.Remove(hit); SavePresets(latest); }
                        AppLogger.UserAction($"视频:删除预设「{presetName}」");
                        pendingDel = null;
                        if (latest.Count == 0) { try { dlg.Hide(); } catch { } return; }
                        RebuildList();
                    };
                    cancelBtn.Click += (_, _) => { pendingDel = null; RebuildList(); };
                    confirmSpan.Children.Add(okBtn); confirmSpan.Children.Add(cancelBtn);
                    Grid.SetColumn(confirmSpan, 1);
                    row.Children.Add(confirmSpan);
                }
                else
                {
                    if (exportMode)
                    {
                        // 导出多选模式:右侧显示【复选框】(勾选要导出的预设),替代垃圾桶
                        var cb = new Microsoft.UI.Xaml.Controls.CheckBox
                        {
                            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
                            IsChecked = false,   // 默认不选,由用户勾选要导出的预设
                        };
                        exportChecks.Add((cur[i], cb));
                        Grid.SetColumn(cb, 1);
                        row.Children.Add(cb);
                    }
                    else
                    {
                        // 普通态:垃圾桶图标(点一下进入确认态)
                        var delBtn = new Button
                        {
                            Content = "\uE74D",
                            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe MDL2 Assets"),
                            FontSize = 13,
                            Background = null,
                            BorderThickness = new Microsoft.UI.Xaml.Thickness(0),
                            Padding = new Microsoft.UI.Xaml.Thickness(6, 2, 6, 2),
                            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
                            MinWidth = 0,
                        };
                        delBtn.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 229, 72, 77));
                        ToolTipService.SetToolTip(delBtn, "删除该预设");
                        Grid.SetColumn(delBtn, 1);
                        delBtn.Click += (_, _) => { pendingDel = presetName; RebuildList(); };
                        row.Children.Add(delBtn);
                    }
                }
                row.Children.Add(name);
                Grid.SetColumn(name, 0);
                itemPanel.Children.Add(row);
                // 行间分隔线(非最后一行):放在 item 底部,与高亮边界对齐
                if (i < cur.Count - 1)
                    itemPanel.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle { Height = 1, Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 40, 44, 52)), Margin = new Microsoft.UI.Xaml.Thickness(0, 6, 0, 0) });
                listView.Items.Add(itemPanel);
            }
        }
        RebuildList();
        sortCombo.SelectionChanged += (_, _) => RebuildList();

        // 「应用预设」(右蓝):应用选中的预设后正常关窗
        applyBtn.Click += async (_, _) =>
        {
            if (exportMode)
            {
                // 「确定导出」:收集勾选的预设导出,然后退回普通态
                var selected = exportChecks.Where(x => x.cb.IsChecked == true).Select(x => x.p).ToList();
                if (selected.Count == 0)
                {
                    await ShowPresetHintAsync("还没勾选任何预设。先勾选要导出的预设,再点「确定导出」。");
                    return;
                }
                await ExportPresetsAsync(selected, selected.Count == 1);
                exportMode = false;
                pendingDel = null;
                SetBottomMode();
                RebuildList();
                return;
            }
            int s = listView.SelectedIndex;
            if (s < 0) return;
            var cur = LoadPresets();
            int idx = ResolveSortedIndex(sortCombo.SelectedIndex, s, cur);
            if (idx >= 0 && idx < cur.Count) ApplyPreset(cur[idx]);
            try { dlg.Hide(); } catch { }
        };
        // 「关闭」(左灰):普通态=直接关窗;导出态=取消导出,退回普通态
        closeBtn.Click += (_, _) =>
        {
            if (exportMode)
            {
                exportMode = false;
                pendingDel = null;
                SetBottomMode();
                RebuildList();
                return;
            }
            try { dlg.Hide(); } catch { }
        };
        // 导出:进入多选模式(行显示复选框),底部按钮切「取消/确定导出」
        exportBtn.Click += (_, _) =>
        {
            exportMode = true;
            pendingDel = null;
            SetBottomMode();
            RebuildList();
        };
        // 导入:选文件导入,【不关窗】留在预设界面,刷新列表(导入后的新项出现在列表里即反馈)
        importBtn.Click += async (_, _) =>
        {
            int n = await ImportPresetsAsync();
            pendingDel = null;
            if (n < 0) return;   // 格式校验失败:方法内已弹提示,不再刷新列表
            RebuildList();
            if (n > 0) await ShowPresetHintAsync($"已导入 {n} 个视频预设。");
        };
        try { await dlg.ShowAsync(); } catch { }
    }

    /// <summary>把"排序后列表的显示下标"映射回原始预设下标(listView 显示序 = 排序后的 list,故直接用 showIdx)。</summary>
    private static int ResolveSortedIndex(int sortIdx, int showIdx, List<VideoPreset> sorted)
        => showIdx;

    /// <summary>导出预设为一个 .alhpreset 文件(JSON 数组)。导出内容只含处理参数,不含输出路径/设备。</summary>
    private async Task ExportPresetsAsync(List<VideoPreset> presets, bool onlyOne)
    {
        if (presets.Count == 0) return;   // 无内容直接返回(窗口保持)
        var picker = new FileSavePicker();
        picker.FileTypeChoices.Add("ALH Pro 预设", new List<string> { ".alhpreset" });
        // 导出文件名带预设名:单个=该预设名;全部=第一个预设名+"等N个"。清洗非法文件名字符(\/:*?"<>|)
        picker.SuggestedFileName = presets.Count == 1
            ? UpscaleView.SafePresetFileName(presets[0].Name)
            : UpscaleView.SafePresetFileName(presets[0].Name) + $"等{presets.Count}个";
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var file = await picker.PickSaveFileAsync();
        if (file == null) return;
        try
        {
            // 只保留要导出的处理参数(外层 VideoPreset 存 Name/SavedAt/Params;Params 已不含输出路径/设备)
            var json = System.Text.Json.JsonSerializer.Serialize(presets);
            await File.WriteAllTextAsync(file.Path, json);
            AppLogger.UserAction($"视频:导出 {presets.Count} 个预设到 {file.Path}");
        }
        catch (Exception ex) { AppLogger.Warn("导出预设失败:" + ex.Message); }
    }

    /// <summary>导入 .alhpreset 文件(JSON 数组),合并到已有预设(重名自动加后缀;超上限 100 只取前 100)。返回导入数量。</summary>
    private async Task<int> ImportPresetsAsync()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".alhpreset");
        picker.FileTypeFilter.Add(".json");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var file = await picker.PickSingleFileAsync();
        if (file == null) return 0;
        // 【格式校验】视频预设必须是 .alhpreset(或内容含视频专属字段)。若误选图片预设(.alhimg),
        // 公开字段(如 Scale/Tta)会错读成视频参数且不报错——必须显式校验并明确提示,绝不静默混入。
        if (!file.FileType.Equals(".alhpreset", StringComparison.OrdinalIgnoreCase))
        {
            await ShowPresetHintAsync("文件不是视频预设格式。请导入「ALH Pro 视频预设」(.alhpreset)文件;图片预设(.alhimg)请在图片页导入。");
            return -1;
        }
        try
        {
            var json = await File.ReadAllTextAsync(file.Path);
            // 再校验内容确实含视频专属字段(Up/Interp 等),防"后缀对但内容是图片预设"的伪装
            bool isVideo = json.Contains("\"Up\"", StringComparison.Ordinal)
                || json.Contains("\"Interp\"", StringComparison.Ordinal)
                || json.Contains("\"DedupOn\"", StringComparison.Ordinal);
            if (!isVideo)
            {
                await ShowPresetHintAsync("文件内容不是视频预设(可能是图片预设或已损坏)。请导入视频预设(.alhpreset)文件。");
                return -1;
            }
            var imported = System.Text.Json.JsonSerializer.Deserialize<List<VideoPreset>>(json) ?? new List<VideoPreset>();
            if (imported.Count == 0) { AppLogger.Warn("导入预设:文件无内容"); return 0; }
            var existing = LoadPresets();
            int added = 0;
            foreach (var p in imported)
            {
                if (existing.Count >= MaxPresets) break;
                // 去重:同名加后缀 " (2)", " (3)"...
                var name = p.Name;
                int n = 2;
                while (existing.Any(x => x.Name == name)) { name = $"{p.Name} ({n})"; n++; }
                p.Name = name;
                if (p.SavedAt.Length == 0) p.SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                existing.Add(p);
                added++;
            }
            SavePresets(existing);
            AppLogger.UserAction($"视频:导入 {added} 个预设(来自 {file.Path})");
            return added;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("导入预设失败(格式不对):" + ex.Message);
            return 0;
        }
    }

    /// <summary>应用一份预设:把快照套回当前页面(抑制事件回写),并给出应用反馈。</summary>
    private async void ApplyPreset(VideoPreset preset)
    {
        try
        {
            _suppressEvents = true;
            ApplyVideoParams(preset.Params);
            _suppressEvents = false;
            UpdateOptions();
            OnOptionChanged();   // 再触发一次完整联动:超分/补帧/引擎面板与模型下拉刷新,确保超分开关等真正生效
            Log($"已应用预设「{preset.Name}」");
            AppLogger.UserAction($"视频:应用预设「{preset.Name}」");
            await ShowPresetHintAsync($"已应用预设「{preset.Name}」——超分/后处理/补帧/去重等参数已按该预设套用。");
        }
        catch { _suppressEvents = false; }
    }

    /// <summary>生成预设参数摘要(悬停提示):引擎/倍率/补帧/去重/后处理/码率等,多行文本。</summary>
    private static string BuildPresetSummary(VideoPreset p)
    {
        var d = p.Params;
        var sb = new System.Text.StringBuilder();
        // 官方预设:显示「官方」标记、不显示日期时间;用户自建预设显示保存时间
        sb.AppendLine(p.IsOfficial
            ? $"「{p.Name}」[官方]"
            : $"「{p.Name}」({p.SavedAt})");
        sb.AppendLine("超分: " + (d.Up
            ? $"{(d.Engine == 1 ? "Real-ESRGAN" : "waifu2x")} · 倍率 {d.Scale switch { 0 => "1x", 1 => "2x", 2 => "3x", 3 => "4x", _ => "自定义" }} · 模型 {(d.Engine == 1 ? UpEsrganModelName(d.UpEsrganModel) : UpWaifu2xModelName(d.UpWaifu2xModel))}"
            : "关闭"));
        sb.AppendLine("补帧: " + (d.Interp
            ? $"{(d.Model < 0 ? "?" : InterpModelName(d.Model))} · {d.InterpScale}x{(d.Tta ? " · TTA" : "")}"
            : "关闭"));
        sb.AppendLine("去重: " + (d.DedupOn ? $"{(d.DedupModel == 0 ? "智能" : d.DedupModel == 1 ? "动漫" : "手动")}" : "关闭"));
        sb.AppendLine("后处理: " +
            $"锐化{d.PostSharpen} 清晰{d.PostClarity} 钝化蒙版{d.PostUsm} 保留细节{d.PostDetail} " +
            $"去模糊{d.PostDeblur} 去频闪{d.PostFlicker} 去杂色{d.PostDenoise} 边缘抗锯齿{d.PostAa}");
        sb.AppendLine("码率: " + (d.Quality == 5 ? $"自定义 {d.BitrateMbps:0.#}Mbps" : d.Quality switch { 0 => "自动", 1 => "低", 2 => "中", 3 => "高", 4 => "极高", _ => "?" }));
        sb.AppendLine("格式: " + (d.Format == 1 ? "MKV" : "MP4") + " · " + (d.Codec == 1 ? "H.265" : "H.264"));
        if (d.FastMode) sb.AppendLine("兼容模式: 开");
        if (d.VideoDenoiseOn) sb.AppendLine("视频降噪: 开");
        return sb.ToString().TrimEnd();
    }

    private static string InterpModelName(int idx) => idx switch
    {
        0 => "通用最新(v4.13)", 1 => "通用(v4.6)", 2 => "通用再新(v4.26)",
        3 => "动漫专用", 4 => "高清", 5 => "超高清", _ => "?",
    };

    // 视频超分模型下拉选项文本(与 VideoView.xaml 里 ComboBoxItem.Content 一致)
    private static string[] UpWaifu2xModelNames = { "通用·cunet", "动漫·upconv_7_anime", "现实·upconv_7_photo" };
    private static string[] UpEsrganModelNames = { "动漫·animevideov3", "动漫·x4plus-anime", "通用·x4plus" };
    private static string UpWaifu2xModelName(int idx) => idx >= 0 && idx < UpWaifu2xModelNames.Length ? UpWaifu2xModelNames[idx] : "通用·cunet";
    private static string UpEsrganModelName(int idx) => idx >= 0 && idx < UpEsrganModelNames.Length ? UpEsrganModelNames[idx] : "动漫·animevideov3";

    /// <summary>删除确认对话框:点「删除」返回 true。</summary>
    private async Task<bool> ConfirmDeletePresetAsync(string name)
    {
        var dlg = new ContentDialog
        {
            Title = "删除预设",
            Content = new TextBlock { Text = $"确定删除预设「{name}」吗?此操作不可恢复。", TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap },
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot,
        };
        try { return await dlg.ShowAsync() == ContentDialogResult.Primary; }
        catch { return false; }
    }

    /// <summary>轻量提示对话框(占位/上限/保存成功等)。</summary>
    private async Task ShowPresetHintAsync(string msg)
    {
        var dlg = new ContentDialog
        {
            Title = "参数预设",
            Content = new TextBlock { Text = msg, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap },
            CloseButtonText = "知道了",
            XamlRoot = this.XamlRoot,
        };
        try { await dlg.ShowAsync(); } catch { }
    }

    private void SaveSettings()
    {
        try
        {
            var d = CollectVideoParams();
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, System.Text.Json.JsonSerializer.Serialize(d));
        }
        catch { }
    }

    /// <summary>从当前页面 UI 收集全部视频处理参数为 VideoSettings 快照。
    /// 供「记住上次参数」与「保存为预设」共用(参数预设=用户主动命名保存的同一份快照)。</summary>
    private VideoSettings CollectVideoParams()
    {
        return new VideoSettings
        {
            Remember = VideoRememberCheck.IsChecked == true,
            Up = UpscaleToggle.IsChecked == true,
            Engine = VideoEngineRadios.SelectedIndex,
            Scale = VideoScaleRadios.SelectedIndex,
            Gpu = AppSettings.GpuIndex,
            Interp = InterpToggle.IsChecked == true,
            Model = InterpModelCombo.SelectedIndex,
            UpWaifu2xModel = VideoWaifu2xModelCombo.SelectedIndex,   // 超分 waifu2x 模型
            UpEsrganModel = VideoEsrganModelCombo.SelectedIndex,    // 超分 Real-ESRGAN 模型
            InterpScale = InterpScaleRadios.SelectedIndex,
            Target = TargetFpsCheck.IsChecked == true,
            TargetFps = TargetFpsBox.Text,
            VfrMode = VfrModeRadios.SelectedIndex,
            VfrExpanded = VfrPanel.Visibility == Visibility.Visible,
            FpsBase = FpsBaseCombo.SelectedIndex,
            FpsMode = FpsModeRadios.SelectedIndex,
            FpsOffset = FpsOffsetSlider.Value,
            FpsExpanded = FpsPanel.Visibility == Visibility.Visible,
            DedupOn = DedupCheck.IsChecked == true,
            DedupModel = DedupModelCombo.SelectedIndex,
            DedupAnime = DedupAnimeCombo.SelectedIndex,
            DedupSmart = DedupSmartCombo.SelectedIndex,
            DedupAlgo = _algoUiToCore[Math.Clamp(DedupAlgoCombo.SelectedIndex, 0, _algoUiToCore.Length - 1)],   // 存核心语义(0=重复帧检测 1=变化阈值 2=帧差+SSIM 3=内容帧率采样)
            DedupHi = (int)DedupHiSlider.Value,
            DedupLo = (int)DedupLoSlider.Value,
            DedupFrac = DedupFracSlider.Value,
            DedupSadThr = DedupSadSlider.Value,
            DedupSsimThr = DedupSsimSlider.Value,
            ManualProtectSmallMotion = ManualProtectSmallMotionCheck.IsChecked == true,
            DedupPhaseAlign = DedupPhaseAlignManualCheck.IsChecked == true,
            ContentFps = double.TryParse(ContentFpsBox.Text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var cfv) && cfv > 0 ? cfv : 0,
            DedupThr = DedupSceneSlider.Value,
            Scene = SceneCheck.IsChecked == true,
            SceneThr = SceneSlider.Value,
            TimeStep = TimeStepSlider.Value,
            Tta = TtaCheck.IsChecked == true,
            OutDir = _customOutDir ?? "",
            CustomW = CustomWidthBox.Text,
            CustomH = CustomHeightBox.Text,
            PostSharpen = (int)SharpenSlider.Value,
            PostClarity = (int)ClaritySlider.Value,
            PostUsm = (int)UsmSlider.Value,
            PostDetail = (int)DetailSlider.Value,
            PostDeblur = (int)DeblurSlider.Value,
            PostFlicker = (int)FlickerSlider.Value,
            PostDenoise = (int)PostDenoiseSlider.Value,
            PostAa = (int)PostAaSlider.Value,
            MotionBlur = MotionBlurCombo.SelectedIndex,
            DeShake = DeShakeCheck.IsChecked == true,
            Quality = QualityCombo.SelectedIndex,
            BitrateMbps = QualityCombo.SelectedIndex == 5 ? ParseBitrate() : 0,
            Codec = CodecCombo.SelectedIndex,
            Format = FormatCombo.SelectedIndex,
            FastMode = FastModeCheck.IsChecked == true,
            Mute = MuteCheck.IsChecked == true,
            VideoDenoiseOn = DenoiseToggle.IsChecked == true,
            VideoDenoiseStrong = DenoiseToggle.IsChecked == true ? DenoiseStrongRadios.SelectedIndex : -1,
        };
    }

    /// <summary>按元素缓存当前展开/收起 Storyboard,开始新动画前先停旧的(避免两个动画抢 Height 导致顿)。</summary>
    private readonly System.Collections.Generic.Dictionary<Microsoft.UI.Xaml.UIElement, Microsoft.UI.Xaml.Media.Animation.Storyboard> _showHideSbs = new();

    /// <summary>展开/收起动画:高度从 0 渐增/渐减 + 淡入淡出,把下方内容平稳推下去/收上来(不生硬跳动)。
    /// 状态没变时不重播(否则滑条拖动等触发 UpdateOptions 会让动画一闪一闪)。</summary>
    private void AnimateShowHide(Microsoft.UI.Xaml.UIElement el, bool show)
    {
        if ((el.Visibility == Visibility.Visible) == show) return;   // 目标状态已达成,跳过动画
        var fe = el as Microsoft.UI.Xaml.FrameworkElement;
        if (fe == null) { el.Visibility = show ? Visibility.Visible : Visibility.Collapsed; return; }
        if (_showHideSbs.TryGetValue(el, out var oldSb)) { try { oldSb.Stop(); } catch { } }
        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        _showHideSbs[el] = sb;
        // 曲线:展开用 QuarticEase EaseOut(先快后慢,优雅落定);
        // 收回不设曲线=默认线性匀速——EaseOut 尾部极平缓(最后 30% 时间几乎不动),是"顿"的元凶
        var ease = show
            ? (Microsoft.UI.Xaml.Media.Animation.EasingFunctionBase?)new Microsoft.UI.Xaml.Media.Animation.QuarticEase
            {
                EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut,
            }
            : null;
        if (show)
        {
            el.Visibility = Visibility.Visible;
            el.Opacity = 0;
            // 先按自然高度布局一次,拿到目标高度,再置 0 开始展开动画
            try { el.UpdateLayout(); } catch { }
            double target = fe.ActualHeight > 0 ? fe.ActualHeight : 80;
            fe.Height = 0;
            var ha = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                To = target, Duration = new Duration(TimeSpan.FromMilliseconds(220)),
                EasingFunction = ease,
                EnableDependentAnimation = true,
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(ha, fe);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(ha, "Height");
            sb.Children.Add(ha);
            sb.Completed += (_, _) => fe.Height = double.NaN;   // 展开完成恢复自适应高度
        }
        else
        {
            // 收起:高度动画到 0,结束后隐藏。淡出比高度收缩更快(内容先消失,
            // 末尾高度收缩时已基本透明,不会"顿"一下)
            double from = fe.ActualHeight > 0 ? fe.ActualHeight : fe.Height;
            var ha = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = from, To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(120)),
                EasingFunction = ease,
                EnableDependentAnimation = true,
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(ha, fe);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(ha, "Height");
            sb.Children.Add(ha);
            sb.Completed += (_, _) =>
            {
                fe.Height = double.NaN;   // 恢复自适应高度
                el.Visibility = Visibility.Collapsed;
            };
        }
        var oa = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            To = show ? 1 : 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(show ? 220 : 90)),
            EasingFunction = ease,
            EnableDependentAnimation = true,
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(oa, el);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(oa, "Opacity");
        sb.Children.Add(oa);
        sb.Begin();
    }

    /// <summary>「高级参数」展开/收起:与手动面板同一套高度+曲线动画,下方内容平滑推下/收上。</summary>
    private void AdvancedToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        bool show = AdvancedPanel.Visibility != Visibility.Visible;
        AnimateShowHide(AdvancedPanel, show);
        AdvancedToggleBtn.Content = show ? "高级参数 ▴" : "高级参数 ▾";
    }

    /// <summary>「可变帧率保护」展开/收起:默认「自动」(加入列表时后台检测,VFR 素材自动按原节奏处理),
    /// 收起也生效;展开可查看/手动选「不启用」。不影响素材检测标注。</summary>
    private void VfrToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        bool show = VfrPanel.Visibility != Visibility.Visible;
        AnimateShowHide(VfrPanel, show);
        VfrToggleBtn.Content = show ? "可变帧率保护 ▴" : "可变帧率保护 ▾";
        SaveSettings();   // 记住展开状态
    }

    /// <summary>「视频帧率」展开/收起:默认收起(各视频默认帧率,无需设置);展开后三选一:</summary>
    private void FpsToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        bool show = FpsPanel.Visibility != Visibility.Visible;
        AnimateShowHide(FpsPanel, show);
        FpsToggleBtn.Content = show ? "视频帧率 ▴" : "视频帧率 ▾";
        SaveSettings();   // 记住展开状态
    }

    /// <summary>输出文件名中的去重后缀:模式名 + 强度(智能无强度;动漫弱/中/强;手动带算法名)。</summary>
    private string DedupSuffix(int dedupModel, double dedupAnimeThr, double contentFps, double animeHoldN)
    {
        string name = dedupModel switch
        {
            1 => "智能",
            2 => "动漫-" + (animeHoldN switch
            {
                1 => "全动画", 1.6 => "半拍二(15fps)", 2 => "一拍二",
                2.5 => "混合拍二+三", 3 => "一拍三", 4 => "一拍四", _ => "拍N",
            }),
            _ => _algoUiToCore[Math.Clamp(DedupAlgoCombo.SelectedIndex, 0, 3)] switch
            {
                1 => "手动-变化阈值",
                2 => "手动-帧差SSIM",
                3 => "手动-内容帧率" + (contentFps > 0 ? contentFps.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "fps" : ""),
                _ => "手动-重复帧",
            },
        };
        return "_去重-" + name;
    }

    private void Log(string msg)
    {
        AppLogger.Info(msg);   // 同步写诊断日志文件
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        VideoLogText.Text = VideoLogText.Text == "日志:等待任务..."
            ? line : VideoLogText.Text + "\n" + line;
        var lines = VideoLogText.Text.Split('\n');
        if (lines.Length > 200)
            VideoLogText.Text = string.Join("\n", lines.Skip(80)) + "\n";
        VideoLogScroll.ChangeView(null, VideoLogScroll.ScrollableHeight, null, true);
    }

    // ---------- 视频添加 ----------
    private async Task AddVideosAsync(string[] paths)
    {
        int added = 0;
        foreach (var p in paths)
        {
            var existing = _videos.FirstOrDefault(v => v.Path == p);
            if (existing != null)
            {
                // 同名文件被覆盖导出(同名重命名导出很常见):刷新探测信息,不沿用旧数据
                _ = RefreshItemProbeAsync(existing);
                continue;
            }
            var info = await VideoService.ProbeVideoInfoAsync(p);
            var item = new VideoItem
            {
                Path = p,
                Name = Path.GetFileName(p),
                BaseInfo = info,
                FpsProbe = VideoService.ProbeFps(p) ?? "",
                Thumb = null,
            };
            _videos.Add(item);
            added++;
            // 异步生成缩略图 + 探测时长 + 探测是否为可变帧率(VFR,防变速)
            _ = GenerateThumbAsync(item);
            _ = LoadItemDurationAsync(item);
            _ = ProbeVfrAsync(item);
            _ = ProbeDupAsync(item);   // 入列:轻量预估重复帧(标"预估"),选中才全文分析
        }
        if (added > 0) Log($"添加了 {added} 个视频到列表");
        // 仅第一个视频(单视频)自动选中以填充帧率;多视频不自动选中
        if (_videos.Count == 1)
            VideoList.SelectedIndex = 0;
        UpdateDropHint();
        UpdateRunState();
        UpdateOptions();   // 单/多视频 UI 切换
    }

    /// <summary>同名文件被覆盖导出后重拖入:刷新该项目的帧率/去重预估(不沿用旧数据)。</summary>
    private async Task RefreshItemProbeAsync(VideoItem item)
    {
        try
        {
            item.FpsProbe = VideoService.ProbeFps(item.Path) ?? "";
            _ = LoadItemDurationAsync(item);
            _ = ProbeVfrAsync(item);
            _ = ProbeDupAsync(item);
        }
        catch { }
    }

    private async Task LoadItemDurationAsync(VideoItem item)
    {
        var dur = await VideoService.ProbeDurationSeconds(item.Path);
        if (dur > 0) item.Duration = dur;
    }

    /// <summary>后台探测素材是否为可变帧率(VFR):是 → 列表标注「可变帧率」,
    /// 「自动检测」模式下处理时自动按原节奏拆帧(防时快时慢)。</summary>
    private async Task ProbeVfrAsync(VideoItem item)
    {
        try
        {
            bool vfr = await VideoService.ProbeVfrAsync(item.Path);
            if (vfr) item.IsVfr = true;
        }
        catch { }
    }

    // ---- 重复帧预览:入列轻量预估(标"预估");选中全文分析(与处理同口径) ----
    // 手动算法:UI 顺序(内容帧率采样=默认/帧差+SSIM/重复帧检测/画面变化阈值)
    // ↔ 核心语义(0=重复帧检测 1=画面变化阈值 2=帧差+SSIM 3=内容帧率采样,与服务端/设置文件一致)。
    private static readonly int[] _algoUiToCore = { 3, 2, 0, 1 };
    private static readonly int[] _algoCoreToUi = { 2, 3, 1, 0 };
    private (int dedupMode, double dedupAnimeThr, int dedupSmartMode) GetDedupParams()
    {
        int dedupMode = DedupModelCombo.SelectedIndex + 1;   // 1智能 2动漫 3手动(手动内含内容帧率采样算法)
        double dedupAnimeThr = 0.0;   // 动漫模式已改为"一拍N"预设,SSIM 强度档已废弃(仅历史字段保留)
        int dedupSmartMode = DedupSmartCombo.SelectedIndex;
        return (dedupMode, dedupAnimeThr, dedupSmartMode);
    }

    private async Task ProbeDupAsync(VideoItem item)
    {
        try
        {
            var (dm, dThr, dSmart) = GetDedupParams();
            // 用户定案:徽标 = 原视频真实内容帧率的【自动识别】(参考),与手填值/算法无关——
            // 全部去重模式统一走"节奏探测预估"(快且口径一致);
            // 旧"逐帧全文分析"不再用于入列预估(慢,且手动算法徽标口径不一/容易失败空白)。
            {
                double probe = 0;
                double.TryParse(item.FpsProbe, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out probe);
                if (probe <= 0) probe = 30;
                var p = await Task.Run(() => VideoService.ProbeRhythmAsync(item.Path, probe, CancellationToken.None));
                item.SetDupProfile(p);
                if (p.ContentFps > 0.01)
                {
                    item.DupBadgeText = p.DupRatioPct > 0.5
                        ? $"内容≈{p.ContentFps:0.#}fps(删{p.DupRatioPct:0}%)·预估"
                        : $"内容≈{p.ContentFps:0.#}fps·预估";
                }
                else
                {
                    // 预估失败也留痕(不空白):常见=极短视频/画面全静态(估算帧率≈0.5fps 兜底)
                    item.DupBadgeText = string.IsNullOrWhiteSpace(p.Summary) || p.Summary.Contains("预估失败")
                        ? "无法预估"
                        : $"无法预估({p.Summary.Replace("预估:", "")})";
                }
                item.DupBadgeVisibility = Microsoft.UI.Xaml.Visibility.Visible;
                return;
            }
        }
        catch { }
    }

    /// <summary>动漫模式(一拍N)/ 手动-内容帧率采样 的预览画像:由预设直接得出内容帧率,与处理管线同口径。
    /// 一拍N:内容帧率 = 素材帧率 ÷ N;手动-内容帧率采样:取输入框手动值(空=0,提示待填写)。</summary>
    private VideoService.DupProfile BuildFcProfile(int dm, string fpsProbe)
    {
        double probe = 0;
        double.TryParse(fpsProbe, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out probe);
        if (probe <= 0) probe = 30;
        if (dm == 2)
        {
            double n = DedupAnimeCombo.SelectedIndex switch { 0 => 2, 1 => 3, 2 => 2.5, 3 => 1.6, _ => 4 };
            double fc = n >= 2 ? probe / n : probe;
            string holdTxt = n switch { 2 => "一拍二", 3 => "一拍三", 2.5 => "混合一拍二+三", 1.6 => "半拍二(≈15fps)", _ => "全动画" };
            return new VideoService.DupProfile
            {
                DupRatioPct = 0, ContentFps = fc, Estimated = true, Segs = new(),
                Summary = $"动画帧率:{holdTxt} → 内容帧率 ≈{fc:0.##} fps(素材 {probe:0.##}fps),按此均匀采样+补帧",
            };
        }
        // dm == 3(手动-内容帧率采样):读输入框手动值
        var fc7 = double.TryParse(ContentFpsBox.Text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var cf) && cf > 0 ? cf : 0;
        return new VideoService.DupProfile
        {
            DupRatioPct = 0, ContentFps = fc7, Estimated = true, Segs = new(),
            Summary = fc7 > 0
                ? $"内容帧率(手动) {fc7:0.##} fps:按此均匀采样+补帧"
                : "内容帧率未填写:请先填写素材真实内容帧率(如 12),或改用「动漫模式」选一拍N",
        };
    }

    /// <summary>在预览覆盖层的重复帧条上用红色矩形标注重复集中时段。</summary>
    private void RenderDupStrip(VideoItem item)
    {
        DupStrip.Children.Clear();
        double w = DupStrip.ActualWidth;
        if (w <= 10) return;
        DupStrip.Children.Add(new Microsoft.UI.Xaml.Controls.TextBlock
        {
            Text = "重复帧时间分布",
            FontSize = 9, Opacity = 0.45, VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
        });
        if (item.DupSegs.Count == 0 || item.Duration <= 0) return;
        double dur = item.Duration;
        foreach (var s in item.DupSegs)
        {
            var r = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Height = 6,
                Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xD9, 0x53, 0x4F)),
                RadiusX = 2, RadiusY = 2,
                VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Bottom,
            };
            double x0 = s.Start / dur * w;
            double x1 = s.End / dur * w;
            Microsoft.UI.Xaml.Controls.Canvas.SetLeft(r, Math.Max(0, x0));
            r.Width = Math.Max(3, x1 - x0);
            DupStrip.Children.Add(r);
        }
    }

    private void DupStrip_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_previewItem != null) RenderDupStrip(_previewItem);
    }

    /// <summary>动漫模式「动画帧率(一拍N)」/「内容帧率」:预览即结果,无需后台分析;其它模式仍走全文分析。</summary>
    private async void AnalyzeDupBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_previewItem == null) return;
        var item = _previewItem;
        DupSummaryText.Text = "分析中...";
        AnalyzeDupBtn.IsEnabled = false;
        try
        {
            var (m, athr, sm) = GetDedupParams();
            int algoCoreAt = _algoUiToCore[Math.Clamp(DedupAlgoCombo.SelectedIndex, 0, 3)];
            if (m == 2 || (m == 3 && algoCoreAt == 3))
            {
                var p = BuildFcProfile(m, item.FpsProbe);
                item.SetDupProfile(p);
                DupSummaryText.Text = p.Summary;
                if (p.ContentFps > 0.01)
                {
                    item.DupBadgeText = $"内容≈{p.ContentFps:0.#}fps";
                    item.DupBadgeVisibility = Microsoft.UI.Xaml.Visibility.Visible;
                }
                return;
            }
            var p2 = await VideoService.AnalyzeDupAsync(item.Path, m, athr, sm,
                false, false);
            item.SetDupProfile(p2);
            DupSummaryText.Text = p2.Summary;
            RenderDupStrip(item);
        }
        catch { DupSummaryText.Text = "分析失败"; }
        finally { AnalyzeDupBtn.IsEnabled = true; }
    }

    private void ApplyDupBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_previewItem == null) return;
        double dup = _previewItem.DupRatioPct;
        DedupCheck.IsChecked = true;
        // 重复很多→动漫(一拍二/拍三);中等→智能;很少→温和(几乎不删)
        DedupModelCombo.SelectedIndex = dup >= 25 ? 1 : dup >= 8 ? 0 : 3;
        Log($"已按预览设置去重(重复≈{dup:0}%):{(dup >= 25 ? "动漫模式" : dup >= 8 ? "智能模式" : "温和模式")}");
        UpdateOptions();
        SaveSettings();
    }

    private async Task GenerateThumbAsync(VideoItem item)
    {
        try
        {
            var ffmpeg = VideoService.FfmpegPath;
            if (ffmpeg == null) return;
            var tmp = Path.Combine(EngineService.TempRoot, $"imgup_thumb_{Guid.NewGuid():N}.jpg");
            // 进程启动/等待放后台线程(ffmpeg 取缩略图慢/卡时不冻结 UI)
            await Task.Run(() =>
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = $"-y -ss 0.5 -i \"{item.Path}\" -frames:v 1 -vf \"scale=240:-2\" -q:v 3 \"{tmp}\"",
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
            {
                await bmp.SetSourceAsync(fs.AsRandomAccessStream());
            }
            try { File.Delete(tmp); } catch { }
            // 属性通知会自动刷新列表缩略图
            item.Thumb = bmp;
        }
        catch { }
    }

    private void VideoList_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        // 只在「列表项」上双击才打开预览;双击空白不响应(避免误开之前选中的项)
        if (e.OriginalSource is FrameworkElement fe && fe.DataContext is VideoItem item)
            OpenPreview(item);
    }

    // ---------- 视频框选(与图片放大一致) ----------
    private const double RbThreshold = 4;
    private bool _rbBanding;
    private bool _rbMoved;
    private Windows.Foundation.Point _rbStart;

    private void VideoGridHost_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        // 指针按在任何列表项上:交回 ListView(点击选中 / 拖拽排序),不启动橡皮筋框选
        if (IsPressOnVideoItem(e.GetCurrentPoint(VideoGridHost).Position)) return;
        _rbBanding = true;
        _rbMoved = false;
        _rbStart = e.GetCurrentPoint(VideoGridHost).Position;
        RbRectV.Visibility = Visibility.Visible;
        RbRectV.Width = 0;
        RbRectV.Height = 0;
        Canvas.SetLeft(RbRectV, _rbStart.X);
        Canvas.SetTop(RbRectV, _rbStart.Y);
        VideoGridHost.CapturePointer(e.Pointer);
    }

    /// <summary>按下位置是否落在某个视频列表项上(拖拽排序/点击选中应交给 ListView,避免与框选打架)。</summary>
    private bool IsPressOnVideoItem(Windows.Foundation.Point pt)
    {
        for (int i = 0; i < _videos.Count; i++)
        {
            if (VideoList.ContainerFromIndex(i) is FrameworkElement c && c.ActualWidth > 0)
            {
                var tl = c.TransformToVisual(VideoGridHost).TransformPoint(new Windows.Foundation.Point(0, 0));
                var r = new Windows.Foundation.Rect(tl.X, tl.Y, c.ActualWidth, c.ActualHeight);
                if (r.Contains(pt)) return true;
            }
        }
        return false;
    }

    private void VideoGridHost_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_rbBanding) return;
        var cur = e.GetCurrentPoint(VideoGridHost).Position;
        if (!_rbMoved && Math.Abs(cur.X - _rbStart.X) < RbThreshold && Math.Abs(cur.Y - _rbStart.Y) < RbThreshold)
            return;
        _rbMoved = true;
        UpdateRbRectV(cur);
    }

    private void VideoGridHost_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_rbBanding) return;
        _rbBanding = false;
        VideoGridHost.ReleasePointerCapture(e.Pointer);
        if (_rbMoved)
        {
            UpdateRbRectV(e.GetCurrentPoint(VideoGridHost).Position);
            ApplyRubberSelectionV();
        }
        else
        {
            // 单击空白:取消选中(不再保留之前的选择)
            VideoList.SelectedItems.Clear();
            UpdateListButtons();
        }
        RbRectV.Visibility = Visibility.Collapsed;
    }

    private void VideoGridHost_PointerCaptureLost(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _rbBanding = false;
        RbRectV.Visibility = Visibility.Collapsed;
    }

    private void UpdateRbRectV(Windows.Foundation.Point cur)
    {
        double x = Math.Min(_rbStart.X, cur.X);
        double y = Math.Min(_rbStart.Y, cur.Y);
        Canvas.SetLeft(RbRectV, x);
        Canvas.SetTop(RbRectV, y);
        RbRectV.Width = Math.Abs(cur.X - _rbStart.X);
        RbRectV.Height = Math.Abs(cur.Y - _rbStart.Y);
        RbRectV.Visibility = RbRectV.Width > 2 && RbRectV.Height > 2
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyRubberSelectionV()
    {
        var rect = new Windows.Foundation.Rect(Canvas.GetLeft(RbRectV), Canvas.GetTop(RbRectV),
            RbRectV.Width, RbRectV.Height);
        if (rect.Width < 2 || rect.Height < 2) return;
        VideoList.SelectedItems.Clear();
        for (int i = 0; i < _videos.Count; i++)
        {
            if (VideoList.ContainerFromIndex(i) is FrameworkElement c)
            {
                var tf = c.TransformToVisual(VideoGridHost);
                var topLeft = tf.TransformPoint(new Windows.Foundation.Point(0, 0));
                var itemRect = new Windows.Foundation.Rect(topLeft.X, topLeft.Y, c.ActualWidth, c.ActualHeight);
                if (RectIntersects(itemRect, rect))
                    VideoList.SelectedItems.Add(_videos[i]);
            }
        }
    }

    private static bool RectIntersects(Windows.Foundation.Rect a, Windows.Foundation.Rect b)
        => a.X < b.X + b.Width && a.X + a.Width > b.X
            && a.Y < b.Y + b.Height && a.Y + a.Height > b.Y;

    // ---------- 删除/清空 ----------
    // Del 键删除选中的视频(列表获得焦点时);灰色(已完成)项目任何时刻都可删
    private void VideoList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Delete)
        {
            e.Handled = true;
            if (VideoList.SelectedItems.Count > 0)
                RemoveVideo_Click(sender, e);   // 删除规则统一由 RemoveVideo_Click 判定(灰项随时可删,其余处理中需暂停)
        }
    }

    private async void RemoveVideo_Click(object sender, RoutedEventArgs e)
    {
        var selected = VideoList.SelectedItems.Cast<VideoItem>().ToArray();
        if (selected.Length == 0) return;
        // 已完成(灰)的项目任何时候都可删除(它们不参与当前任务);未完成的在任务处理中受锁限制
        var doneSel = selected.Where(v => v.IsDone).ToArray();
        var rest = selected.Where(v => !v.IsDone).ToArray();
        if (_running && rest.Length > 0)
        {
            if (!_paused)
            {
                if (doneSel.Length > 0)
                {
                    foreach (var v in doneSel) RemoveVideoItem(v);
                    ApplyListRefresh();
                    await ShowPauseHintAsync($"已删除 {doneSel.Length} 个已完成(灰)项目;\n其余项目处理中,需先暂停才能删除未执行的。");
                }
                else
                {
                    await ShowPauseHintAsync("任务处理中,需先暂停才能删除未执行的项目。\n(已完成/灰色的项目可以直接删除)");
                }
                return;
            }
            // 暂停中:灰项 + 未处理项可删;已处理/处理中的提示
            var pending = rest.Where(it => it.IsPending).ToArray();
            var blocked = rest.Where(it => !it.IsPending).ToArray();
            foreach (var v in doneSel) RemoveVideoItem(v);
            foreach (var v in pending) RemoveVideoItem(v);
            ApplyListRefresh();
            RefreshVideoProgress(_runItems ?? _videos.ToArray(), "已暂停 · 可删除未处理的项目");   // 删除后进度条立即更新
            if (blocked.Length > 0)
                await ShowPauseHintAsync($"已删除 {doneSel.Length + pending.Length} 个项目;\n其余 {blocked.Length} 个已处理/处理中的项目不能删除。");
            else if (doneSel.Length + pending.Length == 0)
                await ShowPauseHintAsync("选中的项目已处理或正在处理,不能删除;\n只能删除还没处理的项目(暂停状态下)。");
            return;
        }
        foreach (var item in selected) RemoveVideoItem(item);
        ApplyListRefresh();
        if (selected.Length > 0)
        {
            VideoInfo.Text = "未选择视频";
            Log($"删除了 {selected.Length} 个视频");
        }
    }

    private void RemoveVideoItem(VideoItem item)
    {
        // 先置回未完成态:模板的 ReRunBtnVisibility 绑定随之立即 Collapsed,
        // 避免 WinUI 列表移除项后残留"幽灵项"悬停时浮出「删除」按钮(模板绑定值未刷新)
        item.IsDone = false;
        _videos.Remove(item);
        if (ReferenceEquals(_selected, item)) _selected = null;
        if (ReferenceEquals(_previewItem, item)) _previewItem = null;
    }

    private void ApplyListRefresh()
    {
        UpdateDropHint();
        UpdateRunState();
        UpdateListButtons();
        UpdateOptions();   // 单/多视频 UI 切换(帧率输入↔偏移滑条)
    }

    // 已完成(灰)项目上的「重新激活」:解除灰色,下次「开始处理」会包含它
    // 右键菜单:重新处理 / 删除(悬浮"删除"按钮已移除此方式,避免 WinUI 幽灵项误显)
    private VideoItem? GetFlyoutItem(object sender)
        => (sender as FrameworkElement)?.DataContext as VideoItem;

    private void ItemRerun_Click(object sender, RoutedEventArgs e)
    {
        var item = GetFlyoutItem(sender);
        if (item == null) return;
        if (!item.IsDone) { Log($"\"{item.Name}\" 未完成,无需重新处理"); return; }
        _suppressEvents = true;
        item.IsDone = false;
        _suppressEvents = false;
        item.Progress = 0; item.StatusText = "等待处理..."; item.EtaText = ""; item.OutputInfo = "";
        UpdateOptions(); UpdateListButtons(); UpdateRunState();
        Log($"已将 \"{item.Name}\" 置为未完成,可直接再点「开始处理」");
    }

    private void ItemDelete_Click(object sender, RoutedEventArgs e)
    {
        var item = GetFlyoutItem(sender);
        if (item == null) return;
        // 删除规则与 RemoveVideo_Click 一致:已完成(灰)随时删;未完成需未在处理或已暂停
        if (item.IsDone || !_running || _paused) RemoveVideoItem(item);
        else Log("⚠ 任务处理中,需先暂停才能删除未执行的项目");
    }

    private void ReRunVideoBtn_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is VideoItem item && item.IsDone)
        {
            item.IsDone = false;
            Log($"已重新激活已完成的项目:{item.Name}(下次「开始处理」会处理它)");
        }
    }

    // 已完成(灰)项目上的「删除」:直接移除该灰项(它不参与当前任务,任何时刻可删)
    private void DoneDeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is VideoItem item && item.IsDone)
        {
            RemoveVideoItem(item);
            ApplyListRefresh();
            if (VideoList.SelectedItems.Count == 0)
                VideoInfo.Text = "未选择视频";
            Log($"已删除已完成项目:{item.Name}");
        }
    }

    // 顶部「清除所有已完成项目」:一次清掉所有灰色项目
    private void ClearDoneBtn_Click(object sender, RoutedEventArgs e)
    {
        var done = _videos.Where(v => v.IsDone).ToArray();
        foreach (var item in done) RemoveVideoItem(item);
        if (done.Length > 0)
        {
            if (VideoList.SelectedItems.Count == 0)
                VideoInfo.Text = "未选择视频";
            Log($"已清除 {done.Length} 个已完成(灰)的项目");
        }
        ApplyListRefresh();
    }

    // 设置开启「完成后自动删除」时:项目完成 3 秒后自动从列表删除(留时间看完成信息)
    private void ScheduleAutoRemove(VideoItem item)
    {
        if (!AppSettings.AutoRemoveDone) return;
        var t = DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromSeconds(3);
        t.IsRepeating = false;
        t.Tick += (_, _) =>
        {
            t.Stop();
            // 3 秒后仍是"已完成"且还在列表才删(期间被重新激活/手动处理则保留)
            if (AppSettings.AutoRemoveDone && item.IsDone && _videos.Contains(item))
            {
                RemoveVideoItem(item);
                ApplyListRefresh();
                Log($"已完成项目自动删除(等 3 秒):{item.Name}");
            }
        };
        t.Start();
    }

    private async Task ShowPauseHintAsync(string msg)
    {
        var dlg = new ContentDialog
        {
            Title = "提示",
            Content = new TextBlock { Text = msg, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap },
            CloseButtonText = "确定",
            XamlRoot = this.XamlRoot,
        };
        await dlg.ShowAsync();
    }

    /// <summary>检测是否 RTX 50 系列(Blackwell 架构)显卡:从 VulkanCheck 设备或系统枚举名称判断。</summary>
    private static bool IsBlackwellGpu()
    {
        try
        {
            var names = new System.Collections.Generic.List<string>();
            names.AddRange(VulkanCheck.Devices.Select(d => d.Name));
            try { names.AddRange(GpuInfo.GetAdapterNames()); } catch { }
            return names.Any(n => System.Text.RegularExpressions.Regex.IsMatch(n, @"RTX 5[0-9]{2}", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        }
        catch { return false; }
    }

    /// <summary>RTX 50 系 + 旧引擎(2022 版 ncnn)提前提示:「好的」= 换 waifu2x(官方新版,兼容 50 系且快);
    /// 「仍然继续」= 保持原引擎(处理中探测失败会自动换卡/CPU,不影响输出)。</summary>
    private async Task<bool> AskBlackwellOldEngineAsync(string engineLabel)
    {
        var dlg = new ContentDialog
        {
            Title = "RTX 50 系兼容提示",
            Content = new TextBlock
            {
                Text = $"检测到 RTX 50 系显卡。当前超分引擎「{engineLabel}」是较旧版本(2022 年)," +
                    "在 50 系上可能无法用 GPU 计算(会慢或自动降级)。\n\n" +
                    "「好的」= 换用 waifu2x(官方 2025 新版,完全兼容 50 系,且速度最快)\n" +
                    "「仍然继续」= 保持当前引擎(不通时会自动改用其它 GPU,再不行则 CPU,不影响输出)",
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
            },
            PrimaryButtonText = "好的",
            CloseButtonText = "仍然继续",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        try
        {
            var r = await dlg.ShowAsync();
            if (r == ContentDialogResult.Primary)
            {
                Log("已按 50 系兼容提示换用 waifu2x 超分");
                return true;
            }
            Log("用户选择保留旧引擎,50 系上可能降级 CPU(可手动改 waifu2x)");
            return false;
        }
        catch { return false; }
    }

    /// <summary>当前引擎 GPU 不可用提示:「好的」= 改用 waifu2x(兼容+最快);「仍然继续」= 保持当前引擎(处理中自动降级 GPU→CPU)。</summary>
    private async Task<bool> AskBlackwellCompatibleAsync(string engineLabel)
    {
        var dlg = new ContentDialog
        {
            Title = "当前引擎无法用 GPU",
            Content = new TextBlock
            {
                Text = $"检测到当前超分引擎「{engineLabel}」在你的显卡上无法用 GPU 计算" +
                    "(显卡过新/过旧或驱动不兼容,AI 超分会很慢甚至失败)。\n\n" +
                    "「好的」= 换用 waifu2x(兼容性好,且速度最快)\n" +
                    "「仍然继续」= 保持当前引擎(不通时会自动改用其它 GPU,再不行则 CPU,不影响输出)",
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
            },
            PrimaryButtonText = "好的",
            CloseButtonText = "仍然继续",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        try
        {
            var r = await dlg.ShowAsync();
            if (r == ContentDialogResult.Primary)
            {
                Log("已按 50 系兼容提示切换超分引擎为 waifu2x(最快)");
                return true;
            }
            Log("用户选择保持当前引擎,50 系上可能降级 CPU(可到设置改)");
            return false;
        }
        catch { return false; }
    }

    /// <summary>「去重后帧数过少」确认:用户点「仍要进行」则继续(跳过防删光保护),否则取消。</summary>
    private async Task<bool> AskDedupTooStrongAsync(string message)
    {
        var dlg = new ContentDialog
        {
            Title = "去重后帧数过少",
            Content = new TextBlock
            {
                Text = message + "\n\n继续处理可能得到只有几帧的\"坏\"视频(播放卡/没有补帧效果)。仍要继续吗?",
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
            },
            PrimaryButtonText = "仍要进行",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot,
        };
        return await dlg.ShowAsync() == ContentDialogResult.Primary;
    }

    private void ClearVideos_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;   // 处理中锁死删除
        int wasCount = _videos.Count;
        foreach (var it in _videos) it.IsDone = false;   // 先重置模板状态,防幽灵项残留
        _videos.Clear();
        _selected = null;
        _previewItem = null;
        VideoInfo.Text = "未选择视频";
        if (wasCount > 0) Log($"清空了视频列表(共 {wasCount} 个)");
        // 退出单独调整模式
        _fpsIndividualMode = false;
        FpsIndividualBtn.Content = "单独调整各视频帧率";
        SaveFpsBtn.Visibility = Visibility.Collapsed;
        UpdateDropHint();
        UpdateRunState();
        UpdateListButtons();
        UpdateOptions();   // 单/多视频 UI 切换
    }

    // ---------- 单独调整各视频帧率模式 ----------
    private bool _fpsIndividualMode;
    private int _lastFpsMode = -1;   // 记录上次帧率模式:切到"单独调整"时自动进入逐条编辑
    private int _lastDedupModel = -1;   // 去重模式切换检测(自动设定推荐阈值用)
    private bool _midRunWarned;         // 处理中改参数只提示一次(本批按快照执行)
    private static bool _compatWarnLogged;   // 兼容提示日志只写一次(提示条常显,日志不刷屏)

    private void FpsIndividualBtn_Click(object sender, RoutedEventArgs e)
    {
        _fpsIndividualMode = !_fpsIndividualMode;
        FpsIndividualBtn.Content = _fpsIndividualMode ? "退出单独调整" : "单独调整各视频帧率";
        SaveFpsBtn.Visibility = _fpsIndividualMode && FpsModeRadios.SelectedIndex == 2
            ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        foreach (var item in _videos)
        {
            item.FpsEditVisibility = _fpsIndividualMode
                ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
            if (!_fpsIndividualMode) item.FpsEditEnabled = true;   // 退出单独调整 = 解锁;下次进入可重新编辑
        }
        UpdateOptions();
        Log(_fpsIndividualMode ? "已开启单独帧率调整:在右侧每个视频上直接输入帧率,完成后点「保存帧率设置」" : "已退出单独帧率调整");
    }

    private void FpsReset_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is VideoItem item)
        {
            item.CustomFps = null;
            item.ClearDraft();
            item.FpsEditEnabled = true;   // 解锁:可再次输入并保存
            Log($"已恢复默认帧率(解锁编辑)→ {item.Name}");
        }
    }

    // 保存:把右侧各视频输入的帧率正式应用,并锁定编辑(想改:点「恢复」解锁)
    private async void SaveFpsBtn_Click(object sender, RoutedEventArgs e)
    {
        int saved = 0;
        foreach (var item in _videos)
        {
            var before = item.CustomFps;
            item.CommitFps();
            item.FpsEditEnabled = false;   // 保存后锁定:只能看,想改先点「恢复」
            if (!Equals(before, item.CustomFps)) saved++;
        }
        Log(saved > 0 ? $"已保存 {saved} 个视频的帧率设置(已锁定,想改点「恢复」)" : "帧率设置无变化(已锁定,想改点「恢复」)");
        SaveFpsBtn.Content = saved > 0 ? "已保存 ✓" : "已保存(无变化)";
        await Task.Delay(2000);
        SaveFpsBtn.Content = "保存帧率设置";
    }

    // 全部输入帧率已并入「视频帧率 → 单独调整各视频帧率」面板(SaveFpsBtn 保存),旧的独立按钮已移除

    private void UpdateListButtons()
    {
        // 空列表:删除类按钮隐藏(不显示"没有项目也能点的空按钮");
        // 有项目才显示,并按状态启用/禁用。
        bool hasVideos = _videos.Count > 0;
        RemoveVideoBtn.Visibility = hasVideos ? Visibility.Visible : Visibility.Collapsed;
        ClearVideosBtn.Visibility = hasVideos ? Visibility.Visible : Visibility.Collapsed;
        ClearDoneBtn.Visibility = hasVideos ? Visibility.Visible : Visibility.Collapsed;
        // 处理中(未暂停)锁死删除;但选中里有「已完成(灰)」项时解锁(它们不参与当前任务,任何时候可删)
        var sel = VideoList.SelectedItems.OfType<VideoItem>().ToArray();
        RemoveVideoBtn.IsEnabled = VideoList.SelectedItem != null &&
            ((!_running || _paused) || sel.Any(v => v.IsDone));
        ClearVideosBtn.IsEnabled = !_running && hasVideos;
        ClearDoneBtn.IsEnabled = _videos.Any(v => v.IsDone);   // 有已完成(灰)项目时才可清除
    }

    private void VideoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 多选模式:取最后点击的项作为"当前选中"
        _selected = VideoList.SelectedItems.Count > 0
            ? VideoList.SelectedItems[^1] as VideoItem : null;
        UpdateListButtons();
        if (_selected != null)
        {
            VideoInfo.Text = $"{_selected.Name}\n{_selected.Info}";
            // 输入帧率自动 = 该视频的实际帧率
            var fps = VideoService.ProbeFps(_selected.Path);
            if (fps != null)
            {
                _suppressEvents = true;
                InputFpsBox.Text = fps;
                _suppressEvents = false;
                UpdateOptions();
            }
        }
        else
        {
            VideoInfo.Text = "未选择视频";
        }
        _ = RefreshVideoOutSpec();
    }

    /// <summary>左下角「输出规格」提示:未处理时显示将输出的分辨率+帧率(随超分/补帧/目标帧率实时更新)。
    /// 处理中/音频页/无视频时隐藏。帧率 = 源帧率×补帧倍率(或用户指定目标帧率)。</summary>
    private async System.Threading.Tasks.Task RefreshVideoOutSpec()
    {
        try
        {
            var inv = CultureInfo.InvariantCulture;
            if (_running)
            {
                VideoOutSpecText.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                return;
            }
            // 取选中项第一;无选中取列表第一
            var sel = VideoList.SelectedItems.Cast<VideoItem>().LastOrDefault();
            VideoItem? it = sel ?? (_videos.Count > 0 ? _videos[0] : null);
            if (it == null)
            {
                VideoOutSpecText.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                return;
            }
            // 源分辨率(失败给 0,显示时省略)
            int sw = 0, sh = 0;
            try { (sw, sh) = await VideoService.ProbeSizeAsync(it.Path); } catch { }
            // 输出分辨率:0=1x缩回 1=2x 2=3x 3=4x 4=自定义
            bool shrink1x = VideoScaleRadios.SelectedIndex == 0;
            bool customRes = VideoScaleRadios.SelectedIndex == 4;
            double mult = VideoScaleRadios.SelectedIndex switch { 1 => 2.0, 2 => 3.0, 3 => 4.0, _ => 1.0 };
            var up = UpscaleToggle.IsChecked == true;
            int ow = sw, oh = sh;
            if (up)
            {
                if (customRes)
                {
                    int.TryParse(CustomWidthBox.Text, out var cw); int.TryParse(CustomHeightBox.Text, out var ch);
                    if (cw > 0 && ch > 0) { ow = cw; oh = ch; }
                }
                else if (!shrink1x)
                {
                    ow = (int)Math.Round(sw * mult); oh = (int)Math.Round(sh * mult);
                }
            }
            // 帧率:优先目标帧率框;否则 源帧率×(补帧倍率)。
            double? srcFps = null;
            try { if (double.TryParse(VideoService.ProbeFps(it.Path), NumberStyles.Float, inv, out var pf) && pf > 0) srcFps = pf; } catch { }
            double? targetFps = (TargetFpsCheck.IsChecked == true
                && double.TryParse(TargetFpsBox.Text, NumberStyles.Float, inv, out var tf) && tf > 0) ? tf : null;
            bool interp = InterpToggle.IsChecked == true;
            int interpScale = InterpScaleRadios.SelectedIndex switch { 1 => 3, 2 => 4, 3 => 8, _ => 2 };
            // 帧率基准:0=真实时间轴(源帧率×倍率) 1=匀速(内容帧率×倍率)。匀速模式用内容帧率,不是源帧率。
            bool uniform = FpsBaseCombo.SelectedIndex == 1;
            double baseFps = srcFps ?? 0;
            if (uniform)
            {
                // 内容帧率:优先用视频项已估算的 ContentFps(去重后),否则手动 ContentFpsBox,再回退源帧率
                if (it.ContentFps > 0.5) baseFps = it.ContentFps;
                else if (double.TryParse(ContentFpsBox.Text, NumberStyles.Float, inv, out var cf) && cf > 0) baseFps = cf;
                else if (srcFps is > 0) baseFps = srcFps.Value;
            }
            double outFps = targetFps ?? (baseFps * (interp ? interpScale : 1));
            // 组装文本
            var parts = new System.Collections.Generic.List<string>();
            if (ow > 0 && oh > 0)
                parts.Add($"输出: {ow}×{oh}{(sw > 0 && sh > 0 ? $"(源 {sw}×{sh})" : "")}");
            else if (sw > 0 && sh > 0)
                parts.Add($"输出: 保持 {sw}×{sh}");
            if (outFps > 0)
            {
                var fpsNote = targetFps != null ? "(指定)" : uniform
                    ? $"({interpScale}x补帧·匀速)"
                    : interp ? $"({interpScale}x补帧)" : "";
                parts.Add($"{outFps:0.##}fps{fpsNote}");
            }
            if (parts.Count == 0)
            {
                VideoOutSpecText.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                return;
            }
            var scaleLabel = shrink1x ? "1x缩回" : customRes ? "自定义" : $"{mult:0.##}x";
            VideoOutSpecText.Text = string.Join(" · ", parts) + $" · {scaleLabel}";
            VideoOutSpecText.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        }
        catch { VideoOutSpecText.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed; }
    }

    // ---------- 预览播放 ----------
    private VideoItem? _previewItem;

    private void OpenPreview(VideoItem item)
    {
        _previewItem = item;
        PreviewName.Text = item.Name;
        PreviewPlayer.Source = MediaSource.CreateFromUri(new Uri(item.Path));
        VideoPreviewOverlay.Visibility = Visibility.Visible;
        // 控制条自动隐藏:初始不显示,鼠标移入显示、静止/移开 2 秒后隐藏(不挡画面)
        try { PreviewPlayer.TransportControls.Visibility = Visibility.Collapsed; } catch { }
        _previewCtrlTimer?.Stop();
        BottomCtrlPanel.Visibility = Visibility.Collapsed;   // 底部面板(时间线/裁剪/重复帧)同样默认隐藏
        // 重复帧预览:显示已有的(预估/分析)结果,未分析则提示
        DupSummaryText.Text = string.IsNullOrEmpty(item.DupSummary) ? "(点击「分析重复帧」查看精细分布)" : item.DupSummary;
        RenderDupStrip(item);
        // 初始化时间线(优先用已应用的裁剪;时长未知时异步探测)
        _trimStart = item.TrimStart;
        _trimEnd = item.TrimEnd > 0 ? item.TrimEnd : item.Duration;
        _duration = item.Duration;
        TrimInfo.Text = _duration > 0 ? "" : "加载时长...";
        if (_duration <= 0)
            _ = LoadDurationAsync(item);
        else
            UpdateTrimUI();
        // 预览播放裁剪段:从开始处起播,到结束处暂停
        // 注意:先退订旧回调,防止重复订阅累积(旧回调会错误暂停新视频)
        try
        {
            var mp = PreviewPlayer.MediaPlayer;
            if (_previewHandler != null)
            {
                try { mp.PlaybackSession.PositionChanged -= _previewHandler; } catch { }
            }
            _previewHandler = (_, __) =>
            {
                if (mp.Source == null) return;
                var pos = mp.PlaybackSession.Position.TotalSeconds;
                var end = item.TrimEnd > 0.1 && item.Duration > 0 ? item.TrimEnd : 0;
                if (end > 0 && pos >= end)
                    mp.Pause();
            };
            mp.PlaybackSession.PositionChanged += _previewHandler;
            if (item.TrimStart > 0.1)
                mp.PlaybackSession.Position = TimeSpan.FromSeconds(item.TrimStart);
        }
        catch { }
    }

    private Windows.Foundation.TypedEventHandler<Windows.Media.Playback.MediaPlaybackSession, object>? _previewHandler;   // 预览暂停回调(避免重复订阅)

    private async Task LoadDurationAsync(VideoItem item)
    {
        var dur = await VideoService.ProbeDurationSeconds(item.Path);
        if (dur <= 0) return;
        item.Duration = dur;
        _duration = dur;
        if (_trimEnd <= 0.1 || _trimEnd > dur) _trimEnd = dur;
        UpdateTrimUI();
    }

    private void PreviewClose_Click(object sender, RoutedEventArgs e)
    {
        PreviewPlayer.MediaPlayer?.Pause();
        PreviewPlayer.Source = null;
        VideoPreviewOverlay.Visibility = Visibility.Collapsed;
        BottomCtrlPanel.Visibility = Visibility.Collapsed;
        _hoverBottomCtrl = false;
        _previewCtrlTimer?.Stop();
        DupStrip.Children.Clear();
        DupSummaryText.Text = "";
        _previewItem = null;
    }

    // 预览控制条自动隐藏:鼠标移入立即显示,静止/移开 2 秒后隐藏(不挡画面)
    // 底部面板(BottomCtrlPanel)与播放器自带的 TransportControls 同步显示/隐藏
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _previewCtrlTimer;
    private bool _hoverBottomCtrl;   // 鼠标在底部面板上:不去隐藏,保证能拖时间线

    private void PreviewPlayerHost_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        try { PreviewPlayer.TransportControls.Visibility = Visibility.Visible; } catch { }
        BottomCtrlPanel.Visibility = Visibility.Visible;
        RestartHideTimer();
    }

    /// <summary>重启 2 秒隐藏计时:鼠标在底部面板上时(可拖时间线)暂停隐藏。</summary>
    private void RestartHideTimer()
    {
        _previewCtrlTimer?.Stop();
        _previewCtrlTimer = DispatcherQueue.CreateTimer();
        _previewCtrlTimer.Interval = TimeSpan.FromMilliseconds(2000);
        _previewCtrlTimer.IsRepeating = false;
        _previewCtrlTimer.Tick += (_, _) =>
        {
            if (_hoverBottomCtrl) return;   // 鼠标在面板上:不隐藏
            try { PreviewPlayer.TransportControls.Visibility = Visibility.Collapsed; } catch { }
            BottomCtrlPanel.Visibility = Visibility.Collapsed;
        };
        _previewCtrlTimer.Start();
    }

    private void PreviewPlayerHost_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        // 不立即隐藏:2 秒计时兜底(鼠标可能正在去底部面板的路上)
        RestartHideTimer();
    }

    // 鼠标进入/移出底部面板:悬停期间保持显示(可拖时间线),移开后继续 2 秒计时
    private void BottomCtrlPanel_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        => _hoverBottomCtrl = true;

    private void BottomCtrlPanel_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _hoverBottomCtrl = false;
        _previewCtrlTimer?.Start();
    }

    // ---------- 时间线裁剪 ----------
    private double _trimStart;
    private double _trimEnd;
    private double _duration;
    private bool _dragStartThumb;
    private bool _dragEndThumb;

    private void Timeline_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTrimUI();

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
        if (_duration <= 0) return;
        var usable = Timeline.ActualWidth - 28;
        if (usable <= 0) return;
        var sec = Math.Clamp((e.GetCurrentPoint(Timeline).Position.X - 14) / usable * _duration, 0, _duration);
        if (_dragStartThumb)
            _trimStart = Math.Min(sec, Math.Max(0, _trimEnd - 0.1));
        else if (_dragEndThumb)
            _trimEnd = Math.Max(sec, Math.Min(_duration, _trimStart + 0.1));
        UpdateTrimUI();
    }

    private void UpdateTrimUI()
    {
        if (_duration <= 0 || Timeline.ActualWidth <= 0) return;
        var usable = Timeline.ActualWidth - 28;
        var sx = 14 + _trimStart / _duration * usable;
        var ex = 14 + _trimEnd / _duration * usable;
        TrimStartThumb.Margin = new Thickness(sx - 7, 0, 0, 0);
        TrimEndThumb.Margin = new Thickness(ex - 7, 0, 0, 0);
        TrimRange.Margin = new Thickness(sx, 0, 0, 0);
        TrimRange.Width = Math.Max(0, ex - sx);
        var trimmed = _trimStart > 0.1 || _trimEnd < _duration - 0.1;
        TrimInfo.Text = trimmed
            ? $"裁剪 {FormatTime(_trimStart)} ~ {FormatTime(_trimEnd)} / 总 {FormatTime(_duration)}"
            : $"未裁剪 · 总时长 {FormatTime(_duration)}";
    }

    private void ClearTrim_Click(object sender, RoutedEventArgs e)
    {
        _trimStart = 0;
        _trimEnd = _duration;
        UpdateTrimUI();
    }

    // 应用裁剪:软件内生效(列表标记 + 预览播放裁剪段),不生成文件;导出时输出裁剪后的视频
    private void ApplyTrim_Click(object sender, RoutedEventArgs e)
    {
        if (_previewItem == null || _duration <= 0) return;
        if (_trimStart <= 0.1 && _trimEnd >= _duration - 0.1)
        {
            _previewItem.TrimStart = 0;
            _previewItem.TrimEnd = 0;
            Log($"已清除裁剪:{_previewItem.Name}");
            return;
        }
        _previewItem.TrimStart = _trimStart;
        _previewItem.TrimEnd = _trimEnd;
        Log($"已应用裁剪 {FormatTime(_trimStart)} ~ {FormatTime(_trimEnd)} → {_previewItem.Name}");
        // 预览立即跳到裁剪起点
        try
        {
            var mp = PreviewPlayer.MediaPlayer;
            if (mp.Source != null)
                mp.PlaybackSession.Position = TimeSpan.FromSeconds(_trimStart);
        }
        catch { }
    }

    private async Task ShowMsgAsync(string title, string msg)
    {
        var dlg = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = msg, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap },
            CloseButtonText = "关闭",
            XamlRoot = this.XamlRoot,
        };
        await dlg.ShowAsync();
    }

    public static string FormatTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.Hours > 0 ? $"{ts.Hours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes}:{ts.Seconds:D2}";
    }

    // ---------- 选择 ----------
    private async void PickVideoBtn_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".mp4");
        picker.FileTypeFilter.Add(".mkv");
        picker.FileTypeFilter.Add(".mov");
        picker.FileTypeFilter.Add(".webm");
        picker.FileTypeFilter.Add(".avi");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var files = await picker.PickMultipleFilesAsync();
        if (files != null && files.Count > 0)
            await AddVideosAsync(files.Select(f => f.Path).ToArray());
    }

    private async void BrowseOut_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var folder = await picker.PickSingleFolderAsync();
        if (folder != null)
        {
            OutFileBox.Text = folder.Path;
            _customOutDir = folder.Path;
            SaveSettings();
        }
    }

    // 手动编辑输出目录也生效(留空=源视频目录)
    private void OutFileBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents) return;
        var t = OutFileBox.Text.Trim();
        _customOutDir = t.Length > 0 ? t : null;
        ScheduleSave();
    }

    private void DropBorder_DragOver(object sender, DragEventArgs e)
        => e.AcceptedOperation = DataPackageOperation.Copy;

    private async void DropBorder_Drop(object sender, DragEventArgs e)
    {
        // 关键:标记事件已处理,阻止冒泡到外层容器重复触发(否则拖入一次会添加两次)
        e.Handled = true;
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            // 注意:必须 await(不能 .Result)——UI 线程同步等待会死锁,表现为"拖不进去"
            var items = await e.DataView.GetStorageItemsAsync();
            var files = items.OfType<Windows.Storage.StorageFile>()
                .Where(f => f.Path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                    || f.Path.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase)
                    || f.Path.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)
                    || f.Path.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)
                    || f.Path.EndsWith(".avi", StringComparison.OrdinalIgnoreCase))
                .Select(f => f.Path).ToArray();
            if (files.Length > 0)
                await AddVideosAsync(files);
            else
                Log("拖入的文件不是支持的视频格式(mp4/mkv/mov/webm/avi)");
        }
    }

    // ---------- 任务摘要(顶部一行:已处理几个 / 剩余几个) ----------
    private int _taskDoneCount = 0, _taskTotalCount = 0;
    private string _taskCurrentMsg = "";

    // 初始化任务摘要(总视频数)
    private void InitTaskStages(bool up, bool interp, bool dedupOn, bool sceneOn)
    {
        _taskDoneCount = 0;
        _taskCurrentMsg = "";
    }

    // 更新顶部摘要:已处理 N 个 / 剩余 M 个 + 当前视频内部进度消息
    private void UpdateTaskPanel(string msg, bool finished = false)
    {
        _taskCurrentMsg = msg;
        if (finished)
        {
            _taskDoneCount++;
            TaskSummary.Text = $"✓ 已完成 {_taskDoneCount} 个视频" +
                (_taskTotalCount > _taskDoneCount ? $" · 剩余 {_taskTotalCount - _taskDoneCount} 个" : "") +
                " · " + msg;
        }
        else if (_taskTotalCount > 0)
        {
            TaskSummary.Text = $"已处理 {_taskDoneCount} 个视频 · 剩余 {_taskTotalCount - _taskDoneCount} 个 · {msg}";
        }
        else
        {
            TaskSummary.Text = msg;
        }
    }

    // ---------- 处理 ----------
    private async void RunBtn_Click(object sender, RoutedEventArgs e)
    {
        // 只处理选中的项(勾选后):否则处理全部未完成的(已完成/灰色的默认跳过,不重复跑;点「重新处理」可调起)
        bool onlySelected = SelectedOnlyCheck.IsChecked == true;
        var items = (onlySelected
            ? VideoList.SelectedItems.Cast<VideoItem>()
            : _videos.Where(v => !v.IsDone)).ToArray();
        if (onlySelected && items.Length == 0)
        {
            Log("⚠ 「只处理选中的项目」已勾选,但右侧没有选中任何视频(或被处理完的自动跳过)——请先在列表选中待处理项。");
            await ShowPauseHintAsync("请先在右侧选中要处理的视频(可框选/多选)");
            return;
        }
        if (items.Length == 0)
        {
            Log("⚠ 没有需要处理的视频(全部已完成;如需重跑请在列表项上点「重新处理」)。");
            return;
        }
        if (_running) return;
        var inv = CultureInfo.InvariantCulture;
        var up = UpscaleToggle.IsChecked == true;
        var interp = InterpToggle.IsChecked == true;
        if (!up && !interp)
        {
            Log("⚠ 超分和补帧都未启用,没有可执行的处理项(请勾选「启用超分」或「启用补帧」)");
            return;
        }
        // 引擎前置校验:缺引擎立即提示(超分/补帧分别查,含 ffmpeg)
        {
            var missing = new System.Collections.Generic.List<string>();
            if (up)
            {
                int eng = VideoEngineRadios.SelectedIndex;
                if (eng == 0 && EngineService.FindWaifu2x() is null) missing.Add("waifu2x 引擎");
                if (eng == 1 && EngineService.FindRealESRGAN() is null) missing.Add("Real-ESRGAN 引擎");
            }
            if (interp && VideoService.RifePath is null) missing.Add("RIFE 补帧引擎");
            if (VideoService.FfmpegPath is null) missing.Add("ffmpeg");
            if (missing.Count > 0)
            {
                await ShowPauseHintAsync($"未找到 {string.Join("、", missing)}(engines 目录缺失) — 请确认软件引擎包完整(程序目录 engines\\ 下应有对应文件夹),或重新安装/恢复引擎");
                return;
            }
        }
        // 无 GPU/极弱设备:仅"提示"(不改用户设置)——视频AI用 CPU 极慢,提醒用户,决定权交给用户
        if (SafeRender.Profile == SafeRender.DeviceProfile.UltraLow || !ALHPro.VulkanCheck.GpuAvailable)
        {
            Log("⚠ 无 GPU/弱设备:视频超分/补帧将用 CPU 计算,可能非常慢。建议(可选):降低输出分辨率、补帧用 2x、先跑几秒的小片段、或勾选「兼容模式」。");
        }
        // ===== 高倍率补帧预警:核显/小显存跑 4x 及以上大概率极慢或失败(不拦,知情即可) =====
        if (interp)
        {
            bool weakGpu = SafeRender.Profile == SafeRender.DeviceProfile.UltraLow
                || CurrentIsIntegratedGpu() || SafeRender.TotalVramGB < 6.5;   // 放宽:<8 → <6.5,避免 8GB 4060 误判为弱
            bool highRate = InterpScaleRadios.SelectedIndex >= 2;   // 0=2x 1=3x 2=4x 3=8x...
            bool highTarget = TargetFpsCheck.IsChecked == true
                && double.TryParse(TargetFpsBox.Text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var tf2) && tf2 >= 90;
            if (weakGpu && (highRate || highTarget))
            {
                var dlg = new ContentDialog
                {
                    Title = "高倍率补帧提醒",
                    Content = new TextBlock
                    {
                        Text = CurrentIsIntegratedGpu()
                            ? "当前用核显(共享内存),高倍率补帧(4x 及以上)可能极慢甚至失败。\n建议:补帧倍率改 2x,或勾选「兼容模式」后先跑几秒小片段试试。"
                            : $"当前设备偏弱(显存仅 {SafeRender.TotalVramGB:0.#}GB),高倍率补帧(4x 及以上)可能极慢甚至失败。\n建议:补帧倍率改 2x,或勾选「兼容模式」后先跑几秒小片段试试。",
                        TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                    },
                    PrimaryButtonText = "知道了,开始",
                    CloseButtonText = "我先改参数",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = this.XamlRoot,
                };
                try { var r = await dlg.ShowAsync(); if (r != ContentDialogResult.Primary) return; } catch { }
            }
        }
        // ===== RTX 50 系 + 旧引擎(realesrgan,2022 版 ncnn)提前提示 =====
        // 50 系上 real 引擎可能降级 CPU;waifu2x(官方 20250915 版)兼容。让用户知情,而非跑起来才发现掉速。
        // 探测(超分前 1×1 实测)仍会兜底;这里只是提前告知+给选择。
        if (up && IsBlackwellGpu() && VideoEngineRadios.SelectedIndex == 1)
        {
            if (await AskBlackwellOldEngineAsync("Real-ESRGAN"))
                VideoEngineRadios.SelectedIndex = 0;   // 好,换成 waifu2x(兼容 50 系,且最快)
        }
        // 自定义码率:选了该项但没填/填了非法值 → 提示并拦截(避免按"自动"悄悄处理)
        if (QualityCombo.SelectedIndex == 5 && ParseBitrate() <= 0)
        {
            await ShowPauseHintAsync("已选「自定义码率」,请在上方填入目标码率(Mbps),例如 8(1080p 常见清晰码率)");
            return;
        }

        var multi = items.Length > 1;

        // 手动-内容帧率采样:输入留空 → 在 _running 置真【之前】拦截(防止界面永久卡死)
        {
            var dOn2 = DedupCheck.IsChecked == true;
            var dModel2 = DedupModelCombo.SelectedIndex + 1;
            var dAlgo2 = _algoUiToCore[Math.Clamp(DedupAlgoCombo.SelectedIndex, 0, 3)];
            if (dOn2 && dModel2 == 3 && dAlgo2 == 3 &&
                !(double.TryParse(ContentFpsBox.Text, NumberStyles.Float, inv, out var cf2) && cf2 > 0))
            {
                Log("手动-内容帧率采样:请先填写素材真实内容帧率(如 12);也可用快捷按钮按源帧率算,或改用「动漫模式」选一拍N");
                return;
            }
        }
        var fpsOffset = 0.0;

        VideoService.LastDedupReport = null;   // 清掉上次的报告,避免误显示

        // 多视频:自动创建独立输出文件夹(避免文件混杂)——创建/校验必须在 _running=true 之前(早退不卡死)
        var baseDir = _customOutDir ?? Path.GetDirectoryName(items[0].Path)!;
        if (multi)
        {
            baseDir = Path.Combine(baseDir, $"视频输出_{DateTime.Now:yyyyMMdd_HHmmss}");
            Log($"多视频模式:输出到文件夹 {Path.GetFileName(baseDir)}" +
                (fpsOffset == 0 ? "(各视频用原帧率)" : $"(各视频帧率 {fpsOffset:0})"));
        }
        try { Directory.CreateDirectory(baseDir); }
        catch (Exception ex)
        {
            Log($"输出目录不可用:{baseDir}({ex.Message})");
            return;
        }
        VideoService.LastDedupShort = null;
        _running = true;
        _paused = false;
        _resumeTcs = null;
        _runItems = items;
        foreach (var it in items) { it.Progress = 0; it.StatusText = ""; it.EtaText = ""; }   // 重跑时清掉上次状态
        RunBtn.IsEnabled = false;
        CancelBtn.IsEnabled = true;
        PauseBtn.IsEnabled = true;
        ResumeBtn.IsEnabled = false;
        UpdatePauseButtonVisual();   // 运行中未暂停:暂停按钮高亮蓝
        UpdateListButtons();   // 处理中锁死右侧列表的删除/清空按钮(暂停时解锁删除)
        VideoProgress.Value = 0;

        // 从下拉 Tag 取模型名(Content 含体量/快慢展示,Tag 才是纯模型名)
        string SelModel(ComboBox cb, string fallback)
        {
            var it = cb.SelectedItem as ComboBoxItem;
            var tag = it?.Tag as string;
            return !string.IsNullOrEmpty(tag) ? tag : fallback;
        }
        var (engine, model) = VideoEngineRadios.SelectedIndex switch
        {
            // waifu2x:从模型下拉 Tag 读模型名(默认 models-cunet)
            0 => ("waifu2x", SelModel(VideoWaifu2xModelCombo, "models-cunet")),
            // Real-ESRGAN:从模型下拉 Tag 读模型名(默认 realesr-animevideov3)
            _ => ("realesrgan", SelModel(VideoEsrganModelCombo, "realesr-animevideov3")),
        };
        // 倍率:0=1x超分(2x放大后缩回) 1=2x 2=3x 3=4x 4=自定义分辨率
        bool upscaleShrink1x = false;
        var scale = VideoScaleRadios.SelectedIndex switch
        {
            1 => 2,
            2 => 3,
            3 => 4,
            _ => 1,
        };
        int? outWidth = null, outHeight = null;
        var customRes = false;
        if (VideoScaleRadios.SelectedIndex == 0)
        {
            // 1x超分:输出尺寸不变,但内部先 2x 超分再缩回 1x(画质比直接放大更好)
            upscaleShrink1x = true;
        }
        else if (VideoScaleRadios.SelectedIndex == 4)
        {
            scale = 2;   // 自定义:内部按 2x 超分,再缩放到指定尺寸
            var cwOk = int.TryParse(CustomWidthBox.Text, out var cw) && cw > 0;
            var chOk = int.TryParse(CustomHeightBox.Text, out var ch) && ch > 0;
            customRes = cwOk && chOk;
            if (customRes) { outWidth = cw; outHeight = ch; }
            else Log("⚠ 自定义分辨率无效(宽/高需为正整数),已按 2x 输出");
        }
        var scaleLabel = upscaleShrink1x ? "1x(2x超分后缩回)" : $"{scale:0.##}x";
        // 视频帧率:0=各视频默认帧率(原帧率) 1=帧率偏移(统一减) 2=单独调整(单视频输入框/多视频右侧逐个)
        double? inFps = null;
        int fpsMode = FpsModeRadios.SelectedIndex;
        // 单视频:只显示「输入帧率」框(默认=探测值,可写数字覆盖),直接取框值,不区分模式;
        // 多视频:模式1(偏移)用滑条;模式2(单独调整)按右侧 CustomFps(见下方逐条取值)。
        double f;
        if (!multi)
            inFps = double.TryParse(InputFpsBox.Text, NumberStyles.Float, inv, out f) && f > 0 ? f : null;
        else if (fpsMode == 1)
            fpsOffset = FpsOffsetSlider.Value;
        var interpScale = InterpScaleRadios.SelectedIndex switch { 1 => 3, 2 => 4, 3 => 8, _ => 2 };
        double? targetFps = (TargetFpsCheck.IsChecked == true
            && double.TryParse(TargetFpsBox.Text, NumberStyles.Float, inv, out var tf) && tf > 0)
            ? tf : null;
        var interpModel = InterpModelCombo.SelectedIndex switch
        {
            0 => "rife-v4.13",
            1 => "rife-v4.6",
            2 => "rife-v4.26",
            3 => "rife-anime",
            4 => "rife-HD",
            5 => "rife-UHD",
            6 => "rife-v2.3",
            _ => "rife-v4.13",
        };
        // 去重可单独使用(不勾补帧也能只去重导出);转场/指定输出帧率仅补帧时有效
        var dedupOn = DedupCheck.IsChecked == true;
        var dedupModel = DedupModelCombo.SelectedIndex + 1;   // 服务端:1智能 2动漫 3手动(内含内容帧率采样)
        // 手动模式:算法(核心语义 0=重复帧检测 1=画面变化阈值 2=帧差+SSIM 3=内容帧率采样;UI 顺序见 _algoUiToCore)
        var dedupAlgo = _algoUiToCore[Math.Clamp(DedupAlgoCombo.SelectedIndex, 0, 3)];
        // 动漫模式:动画帧率变种档(0=一拍二,1=一拍三,2=混合拍二+三,3=半拍二≈15fps,4=全动画);
        // 手动-内容帧率采样:纯手动输入
        double animeHoldN = dedupModel == 2
            ? DedupAnimeCombo.SelectedIndex switch { 0 => 2, 1 => 3, 2 => 2.5, 3 => 1.6, _ => 4 }
            : 0;
        double contentFpsNow = 0;
        if (dedupModel == 3 && dedupAlgo == 3 &&
            double.TryParse(ContentFpsBox.Text, NumberStyles.Float, inv, out var cff) && cff > 0)
            contentFpsNow = cff;
        // 内容帧率留空的拦截已提前到 _running 之前(此处不再 return,避免界面永久卡死)
        var dedupHi = (int)DedupHiSlider.Value;
        var dedupLo = (int)DedupLoSlider.Value;
        var dedupFrac = DedupFracSlider.Value;
        var dedupSadThr = DedupSadSlider.Value;
        var dedupSsimThr = DedupSsimSlider.Value;
        var dedupPanThr = 8;   // 手动-语义运动分析:镜头运动阈值(高级项已移除,保留固定默认)
        var dedupPanOn = false;
        var dedupAnimeThr = 0.0;   // 动漫模式已改为"一拍N"预设,SSIM 强度档已废弃
        var dedupThreshold = DedupSceneSlider.Value;   // 手动-scene 阈值(其他算法忽略)
        double? sceneThreshold = SceneCheck.IsChecked == true && interp ? SceneSlider.Value : null;
        double? timeStep = interp ? TimeStepSlider.Value : null;
        var tta = TtaCheck.IsChecked == true;
        if (!interp && TargetFpsCheck.IsChecked == true) targetFps = null;

        // 多视频输出目录/校验已提前到 _running 之前(见上方 baseDir 计算)
        var cts = new CancellationTokenSource();
        _cts = cts;
        _midRunWarned = false;
        var gpuId = CurrentGpuId;
        // ===== 超分引擎 GPU 兼容探测(全设备,不猜型号) =====
        // 任何显卡(50系/AMD/Intel/老驱动)只要当前引擎 realesrgan 在 GPU 上跑不通,
        // 处理前提示:「好的」→ 换 waifu2x(兼容最快);「仍然继续」→ 保持(处理中自动降级其他GPU→CPU)
        // ⚠ 此处【禁止】ConfigureAwait(false):探测后还要接着读下面一大段 UI 控件(参数快照),
        //   留在后台线程会抛 0x8001010E(已真机复现:选 Real-ESRGAN 视频必崩)——await 不带
        //   ConfigureAwait(false),让方法自然地回到 UI 线程;内部改 SelectedIndex 的 DispatcherQueue
        //   兜底保留(双保险,即使未来路径变化也不跨线程改控件)。
        if (up && VideoEngineRadios.SelectedIndex == 1)
        {
            bool usable = await EngineService.IsEngineGpuUsableAsync("realesrgan", gpuId, cts.Token);
            if (!usable)
            {
                var useWaifu = await AskBlackwellCompatibleAsync("Real-ESRGAN");
                var tcs = new System.Threading.Tasks.TaskCompletionSource();
                _ = DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        if (useWaifu && VideoEngineRadios.SelectedIndex != 0)
                            VideoEngineRadios.SelectedIndex = 0;   // 换成 waifu2x(兼容+最快)
                    }
                    finally { tcs.TrySetResult(); }
                });
                await tcs.Task;
            }
        }
        // ===== 参数快照(关键):处理中切换界面【不影响本批】——以下全部在开始时一次性读取,
        // 循环/ProcessOneAsync 只使用快照变量;不这样做,处理中改编码/后处理/VFR 等会
        // 让批内后面的视频悄悄用新值(批级日志与实际不符)。
        var dedupSmartModeNow = DedupSmartCombo.SelectedIndex;
        var manualProtectNow = ManualProtectSmallMotionCheck.IsChecked == true;
        var phaseAlignNow = DedupPhaseAlignManualCheck.IsChecked == true;
        var fpsBaseNow = new[] { 2, 1 }[Math.Clamp(FpsBaseCombo.SelectedIndex, 0, 1)];
        var outExtNow = FormatCombo.SelectedIndex == 1 ? ".mkv" : ".mp4";
        var muteNow = MuteCheck.IsChecked == true;
        var mblurNow = MotionBlurCombo.SelectedIndex;
        var deshakeNow = DeShakeCheck.IsChecked == true;
        var vdenoiseNow = DenoiseToggle.IsChecked == true ? DenoiseStrongRadios.SelectedIndex + 1 : 0;
        var qualityNow = QualityCombo.SelectedIndex == 5 ? 0 : QualityCombo.SelectedIndex;
        var fastNow = FastModeCheck.IsChecked == true;
        var codecNow = CodecCombo.SelectedIndex == 1 ? 2 : 0;   // 0=H.264,1=H.265
        var bitrateNow = ParseBitrate();
        var vfrModeNow = VfrModeRadios.SelectedIndex;
        int postSP = (int)SharpenSlider.Value, postCL = (int)ClaritySlider.Value, postUM = (int)UsmSlider.Value,
            postDT = (int)DetailSlider.Value, postDB = (int)DeblurSlider.Value, postFL = (int)FlickerSlider.Value,
            postDN = (int)PostDenoiseSlider.Value, postAA = (int)PostAaSlider.Value;
        InitTaskStages(up, interp, dedupOn, sceneThreshold != null);
        _taskTotalCount = items.Length;
        _taskDoneCount = 0;
        TaskSummary.Text = $"等待处理:共 {items.Length} 个视频";
        int progressIndex = 0;
        // 任务前刷新空闲资源实测(开了其他软件后空闲骤降,批次档位要跟上);并记录日志便于排查
        SafeRender.RefreshFreeResources();
        SafeRender.RefreshIdleCpu();   // 处理前采样系统占用(引擎未启动,读数=其他软件真实占用)→ CPU 上限自适应
        {
            double fr = SafeRender.FreeRamGB, fv = SafeRender.FreeVramGB;
            int bs = SafeRender.GetVideoBatchSize();
            Log($"资源自检:空闲内存 {fr:0.#} GB / 空闲显存 {fv:0.#} GB → 视频批 {bs} 帧/批");
            AppLogger.Info($"资源自检:空闲内存 {fr:0.#} GB / 空闲显存 {fv:0.#} GB → 视频批 {bs} 帧/批");
        }
        // 预计时间:全局平均速度(已用时间 ÷ 已完成进度 → 总时长估计,再减已用 = 剩余)
        // 预计总时长初始估算:根据启用的处理项 + 每个视频的时长/帧率/分辨率,
        // 一开始就显示合理数值(偏保守,随时间慢慢对齐),不是从小变大校准
        double etaInitTotal = 0;
        var perfKey = PerfMemory.Fingerprint(engine, upscaleShrink1x ? 2.0 : scale, 1920, 1080,
            interpScale, dedupOn, vdenoiseNow, postSP + postCL + postUM + postDB + postFL + postDN + postAA > 0);
        double? perFrameHist = PerfMemory.PerFrameFor(perfKey);   // 同配置上次实测(秒/帧,1080p 基准)
        int totalFramesEst = 0;
        foreach (var it in items)
        {
            try
            {
                var dur = await VideoService.ProbeDurationSeconds(it.Path);
                var fpsS = VideoService.ProbeFps(it.Path);
                double fps = double.TryParse(fpsS, NumberStyles.Float, inv, out var pf) && pf > 0 ? pf : 30;
                var (w, h) = await VideoService.ProbeSizeAsync(it.Path);
                totalFramesEst += (int)Math.Max(1, dur * fps);
                etaInitTotal += VideoService.EstimateProcessSeconds(dur, fps, w, h,
                    up, upscaleShrink1x ? 2.0 : scale, engine, interp, interpScale, dedupOn,
                    DenoiseToggle.IsChecked == true ? DenoiseStrongRadios.SelectedIndex + 1 : 0);
            }
            catch { etaInitTotal += 60; }
        }
        // 经验库校准:同配置有实测记录 → 按实测秒/帧重算(权重 50%,避免单次异常把估算带飞)
        if (perFrameHist.HasValue && totalFramesEst > 0)
        {
            double estHistory = 0;
            foreach (var it in items)
            {
                try
                {
                    var fpsS = VideoService.ProbeFps(it.Path);
                    double fps = double.TryParse(fpsS, NumberStyles.Float, inv, out var pf) && pf > 0 ? pf : 30;
                    var (w, h) = await VideoService.ProbeSizeAsync(it.Path);
                    double areaN = Math.Max(0.25, (double)w * h / 2_073_600.0);
                    estHistory += (int)Math.Max(1, (it.Duration > 0 ? it.Duration : 1) * fps) * perFrameHist.Value * areaN;
                }
                catch { }
            }
            if (estHistory > 1)
                etaInitTotal = 0.5 * etaInitTotal + 0.5 * estHistory;
            AppLogger.Info($"ETA 经验库:配置[{perfKey}] 上次实测 {perFrameHist.Value:0.###} 秒/帧 → 校准为 {etaInitTotal:0} 秒");
        }

        // 预计剩余(ETA):整体进度占比法——已用时间 ÷ 进度% → 总时长,减已用 = 剩余。
        // 优点:跨阶段(超分/补帧/编码)连续平滑,进度条到 99% 时 ETA 必然趋近 0,
        // 不会出现"补帧剩 1 分钟→超分变 8 分钟""编码 55 帧还剩 34 秒"的跳变/滞后。
        // 阶段消息解析「第 N 帧 / 共 M 帧」:用于日志步骤行(▶ 超分 中(已处理 N/M))与阶段内精细进度
        var etaRegex = new System.Text.RegularExpressions.Regex(
            @"^(?<stage>[\u4e00-\u9fffA-Za-z0-9 ]+?)\s*(?:已处理|第)\s*(?<now>\d+)\s*(?:帧|块|层)\s*/\s*共\s*(?<total>\d+)\s*(?:帧|块|层)",
            System.Text.RegularExpressions.RegexOptions.Compiled);
        string? lastLoggedStep = null;
        DateTime lastStepLogAt = DateTime.MinValue;   // 步骤行节流:500ms 内原地更新
        string? stepLogFull = null;                   // 当前已显示的步骤完整行([hh:mm:ss] ▶ ...)
        var taskStart = DateTime.Now;
        DateTime lastEtaAt = DateTime.MinValue;
        DateTime lastPanelAt = DateTime.MinValue;   // 详情面板刷新节流(防高频报告刷 UI 卡顿)
        DateTime lastSpeedAt = DateTime.MinValue;   // 近期速度样本节流
        double lastEtaShown = -1;   // 上次显示的剩余(秒):轻 EMA 平滑,只压抖动
        bool inRest = false;
        DateTime restStartAt = DateTime.MinValue;
        double idleSeconds = 0;
        var progress = new Progress<(int pct, string msg)>(t =>
        {
            // ===== 平滑进度:阶段内用"帧/块计数比"连续换算(修复整数取整的一顿一顿) =====
            // 消息如"超分 已处理 29/64 块"(视频超分按块报)、"补帧 第 N 帧 / 共 M 帧";
            // 换算区间与 VideoService.StageProgressPct 一致(拆帧2-5/补帧10-45/超分45-90/编码96-100),
            // 但用浮点 → 进度条每帧/每块都平滑推进,阶段切换处正好衔接(终点=下一阶段起点)。
            var em = etaRegex.Match(t.msg);
            double pctFine = t.pct;
            int emNow = 0, emTotal = 0;
            string emStage = "";
            if (em.Success)
            {
                emNow = int.Parse(em.Groups["now"].Value, inv);
                emTotal = int.Parse(em.Groups["total"].Value, inv);
                emStage = em.Groups["stage"].Value.Trim();
                // 帧号防越界:引擎段内消息偶发"已处理 N > 共 M"(76/75)→ 显示按 M 封顶,不出现"超总数"
                if (emNow < 0) emNow = 0;
                if (emTotal < emNow) emTotal = emNow;
                // 日志显示当前步骤:实时更新最后一行(500ms 节流);文件日志每阶段仅记首行(防刷爆)
                if (lastLoggedStep != emStage)
                {
                    lastLoggedStep = emStage;
                    stepLogFull = null;   // 新阶段:下一步走"追加"分支
                    lastStepLogAt = DateTime.MinValue;
                }
                string stepNewFull = $"[{DateTime.Now:HH:mm:ss}] ▶ {emStage} 中(已处理 {emNow}/{emTotal})";
                var fpsM = System.Text.RegularExpressions.Regex.Match(t.msg, @"\(目标\s*(\d+(?:\.\d+)?)\s*fps\)");
                if (fpsM.Success)
                    stepNewFull = stepNewFull[..^1] + $"·{fpsM.Groups[1].Value}fps)";
                if (stepLogFull == null || DateTime.Now - lastStepLogAt >= TimeSpan.FromMilliseconds(500))
                {
                    bool first = stepLogFull == null;
                    lastStepLogAt = DateTime.Now;
                    if (first)
                        AppLogger.Info($"▶ {emStage} 中(已处理 {emNow}/{emTotal})");   // 文件日志:每阶段仅首行
                    if (!first && stepLogFull != null
                        && VideoLogText.Text.EndsWith(stepLogFull, StringComparison.Ordinal))
                    {
                        // 原地替换最后一行(实时进度,不刷屏)
                        VideoLogText.Text = VideoLogText.Text.Substring(0, VideoLogText.Text.Length - stepLogFull.Length) + stepNewFull;
                    }
                    else
                    {
                        VideoLogText.Text = (VideoLogText.Text == "日志:等待任务..." ? "" : VideoLogText.Text + "\n") + stepNewFull;
                        VideoLogScroll.ChangeView(null, VideoLogScroll.ScrollableHeight, null, true);
                    }
                    stepLogFull = stepNewFull;
                }
                if (emTotal > 0)
                {
                    double ratio = (double)emNow / emTotal;
                    double fine = emStage switch
                    {
                        "拆帧" => 2 + 3 * ratio,
                        "补帧" => 10 + 35 * ratio,
                        "按源时间轴插帧" => 10 + 35 * ratio,
                        "超分" => 45 + 45 * ratio,
                        "编码" => 96 + 4 * ratio,
                        _ => -1,
                    };
                    if (fine >= 0) pctFine = fine;
                }
            }
            // 阶段结束消息(不含"第N/共M"):把当前步骤行就地转成 "✓ 完成语",不再停留在旧数字
            if (!em.Success && lastLoggedStep != null && stepLogFull != null
                && (t.msg.Contains("完成", StringComparison.Ordinal)
                    || t.msg.StartsWith("去重", StringComparison.Ordinal)
                    || t.msg.StartsWith("已拆出", StringComparison.Ordinal)
                    || t.msg.StartsWith("压缩编码器:", StringComparison.Ordinal)))
            {
                string doneFull = $"[{DateTime.Now:HH:mm:ss}] ✓ {t.msg}";
                if (VideoLogText.Text.EndsWith(stepLogFull, StringComparison.Ordinal))
                    VideoLogText.Text = VideoLogText.Text.Substring(0, VideoLogText.Text.Length - stepLogFull.Length) + doneFull;
                else
                {
                    VideoLogText.Text = (VideoLogText.Text == "日志:等待任务..." ? "" : VideoLogText.Text + "\n") + doneFull;
                    VideoLogScroll.ChangeView(null, VideoLogScroll.ScrollableHeight, null, true);
                }
                stepLogFull = doneFull;
                lastLoggedStep = null;   // 下一阶段重新出现步骤行
            }
            // 整体进度 = (已完成数 + 当前视频内部进度) / 当前剩余总数;
            // 暂停删除未处理项后,已完成数不变、剩余变少 → 进度条直接跳变更新
            int done = items.Count(it => IsItemDone(it) && _videos.Contains(it));
            int active = items.Count(it => _videos.Contains(it) && !IsItemDone(it));
            var overall = done + active > 0
                ? Math.Min(100.0, (done + pctFine / 100.0) / (done + active) * 100.0)
                : pctFine;
            VideoProgress.Value = Math.Max(VideoProgress.Value, overall);   // 浮点:无取整平台
            VideoStatus.Text = done + active > 0 ? $"({done + 1}/{done + active}) {t.msg}" : t.msg;
            // 去重关键信息由"阶段结束消息转写"(上方"完成"匹配)统一写入日志区,避免重复显示两行
            // 当前视频的列表项进度条 + 状态小字 + 预计剩余时间
            if (progressIndex < items.Length)
            {
                var it = items[progressIndex];
                it.Progress = Math.Max(it.Progress, pctFine);
                it.StatusText = t.msg;
                var now = DateTime.Now;
                // 休息计时:进入休息记起点,恢复时把休息时长计入 idle(ETA 只算纯处理时间)
                if (t.msg.Contains("休息", StringComparison.Ordinal))
                {
                    if (!inRest) { inRest = true; restStartAt = now; }
                }
                else if (inRest)
                {
                    idleSeconds += (now - restStartAt).TotalSeconds;
                    inRest = false;
                }
                var workElapsed = (now - taskStart).TotalSeconds - idleSeconds;
                // ETA 专用单调进度:分母固定=总视频数(完成/切换不回退,杜绝 100→50 的回退闪动),封顶 99.9
                int doneAll = items.Count(it => IsItemDone(it) && _videos.Contains(it));
                double etaProgress = Math.Min(99.9,
                    (doneAll + t.pct / 100.0) / Math.Max(1, items.Length) * 100.0);
                double initRemain = etaInitTotal - workElapsed;   // 初始估算的剩余(偏保守,线性递减)
                // ===== ETA:整体进度占比(main 方案,跨阶段平滑,无跳变) =====
                // 已用时间 ÷ 进度% → 总时长,再减已用 = 剩余。补帧→超分→编码换阶段时,
                // 进度占比连续(pct 不回退),ETA 单调下降,不会出现"补帧 1 分钟→超分 8 分钟"的跳变;
                // 即阶段快慢差异已经折算进进度里(慢阶段进度走得慢 → ETA 自然变大,符合真实)。
                // 休息时间已从 workElapsed 剔除。
                if (now - lastEtaAt >= TimeSpan.FromSeconds(1))
                {
                    lastEtaAt = now;
                    double remain;
                    if (etaProgress >= 1.0 && workElapsed > 3)
                    {
                        remain = workElapsed * (100.0 / etaProgress - 1.0);
                    }
                    else
                    {
                        remain = initRemain;
                    }
                    // 轻平滑:70% 真实 + 30% 历史(偏重真实,避免"编码快结束还显示 34 秒"的滞后)
                    if (lastEtaShown > 0 && remain > 0)
                        remain = 0.7 * remain + 0.3 * lastEtaShown;
                    lastEtaShown = remain;
                    it.EtaText = "预计剩余 " + FormatTime(Math.Max(remain, 5));   // 完成前一直显示
                }
                else if (etaProgress < 2 && initRemain > 8 && workElapsed > 3)
                {
                    // 早期(进度<2%)没有帧数消息:用初始估算线性递减展示
                    double remain = lastEtaShown > 0 && initRemain > lastEtaShown
                        ? Math.Min(initRemain, lastEtaShown * 1.03 + 10)
                        : initRemain;
                    lastEtaShown = remain;
                    it.EtaText = "预计剩余 " + FormatTime(remain);
                }
            }
            // 任务详情面板(节流:每帧报告只取 500ms 一次,防高频刷新拖慢界面)
            if (DateTime.Now - lastPanelAt >= TimeSpan.FromMilliseconds(500))
            {
                lastPanelAt = DateTime.Now;
                UpdateTaskPanel(t.msg);
            }
            SafeRender.ApplyRestUi(VideoStatus, CancelBtn, t.msg);   // 休息时:黄字加粗 + 按钮变「跳过休息」
        });

        int okCount = 0, failCount = 0;
        _failReasons.Clear();   // 本次任务的失败原因,清空重来
        var outputFiles = new System.Collections.Generic.List<string>();   // 本次成功生成的输出文件(弹窗高亮用)
        VideoLogText.Text = "";
        // GPU 型号(与引擎 -g 编号对应;注册表轻量读取)
        string gpuName = "";
        try { var (gpuLabels, _) = GpuInfo.BuildLabels(); if (gpuLabels.Count > 0) gpuName = gpuLabels[Math.Min(Math.Max(gpuId, 0), gpuLabels.Count - 1)]; } catch { }
        string devStr = gpuId >= 0 ? (gpuName.Length > 0 ? gpuName : $"GPU {gpuId}") : "CPU (软件计算)";
        Log($"开始处理:共 {items.Length} 个视频,计算设备:{devStr}");
        Log($"输出设置:编码格式={(CodecCombo.SelectedIndex == 1 ? "H.265" : "H.264")},封装={(FormatCombo.SelectedIndex == 1 ? "MKV" : "MP4")}," +
            $"码率={(QualityCombo.SelectedIndex == 5 ? $"自定义 {ParseBitrate():0.#} Mbps" : new[] { "自动", "低", "中", "高", "极高" }[Math.Min(QualityCombo.SelectedIndex, 4)])}," +
            $"静音={(MuteCheck.IsChecked == true ? "是" : "否")}");
        // 去重人类可读描述(实际算法的完整参数)
        string dedupDesc = dedupOn
            ? dedupModel == 3
                ? "手动-" + (dedupAlgo switch
                {
                    3 => $"内容帧率采样 {contentFpsNow:0.##}fps" + (DedupPhaseAlignManualCheck.IsChecked == true ? "+相位对齐" : ""),
                    2 => $"帧差+SSIM(快筛{dedupSadThr:0.0}/相似{dedupSsimThr:0.000})" + (ManualProtectSmallMotionCheck.IsChecked == true ? "+微动防线" : ""),
                    1 => $"变化阈值 {dedupThreshold:0.000}",
                    _ => $"重复帧检测(判线{dedupHi}/{dedupLo} 比例{dedupFrac:0.00})",
                })
                : dedupModel switch
                {
                    1 => $"智能(策略{new[] { "均衡", "激进", "保守" }[Math.Min(DedupSmartCombo.SelectedIndex, 2)]})",
                    2 => $"动漫-一拍{animeHoldN:0.##}" + (DedupPhaseAlignAnimeCheck.IsChecked == true ? "+相位对齐" : ""),
                    _ => "关",
                }
            : "关";
        Log($"▶ 参数:设备={devStr} | " +
            $"超分={(up ? $"开({model}·{scaleLabel})" : "关")}" + (up && customRes ? $"·输出{outWidth}×{outHeight}" : "") + " | " +
            $"补帧={(interp ? $"{interpModel}·{interpScale}x{(tta ? "·TTA" : "")}·时间步{(timeStep ?? 0):0.00}" : "关")} | " +
            $"去重={dedupDesc} | " +
            $"转场识别={(sceneThreshold != null ? $"{sceneThreshold:0.00}" : "关")} | " +
            $"目标帧率={(targetFps is > 0 ? $"{targetFps:0.##}fps" : "随倍率")} | " +
            $"输出基准={(FpsBaseCombo.SelectedIndex == 0 ? "真实时间轴(原帧率×倍率)" : "匀速(内容×倍率)")} | " +
            $"快速模式={(FastModeCheck.IsChecked == true ? "开" : "关")} | " +
            $"VFR={(VfrModeRadios.SelectedIndex == 0 ? $"自动({(items.Any(i => i.IsVfr) ? "检测到可变帧率" : "未检测到")})" : "不启用")}");
        var trimmedCount = items.Count(i => i.IsTrimmed);
        if (trimmedCount > 0)
            Log($"裁剪:{trimmedCount} 个视频已应用裁剪范围(导出为裁剪后内容)");
        var postList = new System.Collections.Generic.List<string>();
        if ((int)SharpenSlider.Value > 0) postList.Add($"锐化{(int)SharpenSlider.Value}");
        if ((int)ClaritySlider.Value > 0) postList.Add($"清晰{(int)ClaritySlider.Value}");
        if ((int)UsmSlider.Value > 0) postList.Add($"钝化蒙版{(int)UsmSlider.Value}");
        if ((int)DetailSlider.Value > 0) postList.Add($"保留细节{(int)DetailSlider.Value}");
        if ((int)DeblurSlider.Value > 0) postList.Add($"去模糊{(int)DeblurSlider.Value}");
        if ((int)FlickerSlider.Value > 0) postList.Add($"去频闪{(int)FlickerSlider.Value}");
        if ((int)PostDenoiseSlider.Value > 0) postList.Add($"去杂色{(int)PostDenoiseSlider.Value}");
        if ((int)PostAaSlider.Value > 0) postList.Add($"边缘抗锯齿{(int)PostAaSlider.Value}");
        if (postList.Count > 0) Log("后处理:" + string.Join(",", postList));
        // 果冻修复(运动模糊/画面去抖,CPU 逐帧滤镜,单独记录便于诊断耗时)
        var jelloParts = new System.Collections.Generic.List<string>();
        if (MotionBlurCombo.SelectedIndex > 0) jelloParts.Add($"运动模糊{"弱中强"[MotionBlurCombo.SelectedIndex - 1]}");
        if (DeShakeCheck.IsChecked == true) jelloParts.Add("画面去抖");
        if (jelloParts.Count > 0) Log("果冻修复:" + string.Join(",", jelloParts) + "(CPU 逐帧滤镜,耗时随分辨率/帧数增加)");

        try
        {
            for (int i = 0; i < items.Length; i++)
            {
                var item = items[i];
                progressIndex = i;

                // 暂停门控:暂停时停在这里,点「恢复」立即续上;取消会从这里抛出
                while (_resumeTcs != null)
                    await _resumeTcs.Task.WaitAsync(cts.Token);
                // 暂停期间被删除的项目:直接跳过,不再处理
                if (!_videos.Contains(item))
                {
                    Log($"  已跳过:{item.Name}(暂停时已从列表删除)");
                    continue;
                }

                // 降温休息(每小时/温度墙):处理下一个视频前检查
                await SafeRender.RestIfDueAsync(i * 100 / Math.Max(1, items.Length), progress, cts.Token);
                item.SetProcessing(true);
                item.Progress = 0;
                item.StatusText = "等待处理...";
                cts.Token.ThrowIfCancellationRequested();
                var suffix = (up ? $"_超分{scaleLabel}_{UpscaleView.ModelShort(engine)}" : "")
                    + (customRes ? $"_自定义{outWidth}x{outHeight}" : "")
                    + (interp ? $"_补帧{interpScale}x_{UpscaleView.ModelShort(interpModel)}" + (targetFps != null ? $"_{targetFps:0.##}fps" : "") : "")
                    + (dedupOn ? DedupSuffix(dedupModel, dedupAnimeThr, contentFpsNow, animeHoldN) : "")
                    + (sceneThreshold != null ? "_转场" : "");
                var outExt = outExtNow;
                var outName = Path.GetFileNameWithoutExtension(item.Path) + suffix + outExt;
                // 输出路径过长自动缩短(Windows 260 字符限制:中文文件名+长后缀+深目录会静默失败)
                try
                {
                    var full = Path.Combine(baseDir, outName);
                    if (full.Length > 220)
                    {
                        // 截断文件名部分(保留后缀与关键信息),如原素材名超过 120 字符则截断
                        var baseName = Path.GetFileNameWithoutExtension(item.Path);
                        if (baseName.Length > 80)
                        {
                            var shortBase = baseName[..80];
                            outName = shortBase + suffix + outExt;
                            Log($"⚠ 输出文件名过长,已缩短素材名({baseName.Length} 字符→80),避免路径超限失败");
                        }
                        // 仍超:再截断 suffix(去掉非关键部分)
                        if (Path.Combine(baseDir, outName).Length > 220 && suffix.Length > 40)
                        {
                            // 保留补帧倍率与引擎,去掉其他
                            outName = Path.GetFileNameWithoutExtension(item.Path)[..Math.Min(50, Path.GetFileNameWithoutExtension(item.Path).Length)]
                                + (interp ? $"_补帧{interpScale}x" : "")
                                + (up ? $"_超分" : "") + outExt;
                            Log("⚠ 输出文件名仍过长,已最短化(带补帧/超分标记)");
                        }
                    }
                }
                catch { }
                var outPath = UpscaleView.UniquePath(baseDir, outName);
                Log($"→ ({i + 1}/{items.Length}) {item.Name}");
                Log($"   输出 → {outPath}");
                // 暂停等待:暂停时停在「当前批次/段」之间(几秒~十几秒),恢复立即续上;取消可从中退出
                async Task PauseWaitAsync()
                {
                    if (_paused && _resumeTcs != null)
                        await _resumeTcs.Task.WaitAsync(cts.Token);
                }
                // 单个视频完整处理(局部函数:「去重帧过少,仍要进行」时用 allowFew 跳过保护重跑)
                async Task ProcessOneAsync(bool allowFew, double? tStart, double? tEnd, double? itemFps)
                {
                    await VideoService.ProcessVideoAsync(item.Path, outPath,
                        engine, model, scale, up, interp, itemFps, interpScale, targetFps,
                        dedupOn ? dedupModel : 0, dedupThreshold, interpModel, sceneThreshold, timeStep, tta,
                        tStart, tEnd, gpuId,
                        outWidth, outHeight,
                        progress, cts.Token,
                        dedupAlgo: dedupAlgo, dedupHi: dedupHi, dedupLo: dedupLo, dedupFrac: dedupFrac,
                        dedupSadThr: dedupSadThr, dedupSsimThr: dedupSsimThr, dedupPanThr: dedupPanThr,
                        dedupPanOn: dedupPanOn, dedupAnimeThr: dedupAnimeThr,
                        dedupSmartMode: dedupSmartModeNow,
                        motionCompDedup: false,
                        dedupOnlyTrueHold: false,
                        manualProtectSmallMotion: manualProtectNow,
                        phaseAlign: phaseAlignNow,
                        // 输出帧率基准:FpsBaseCombo 顺序=真实时间轴插值(推荐)/匀速帧速率插值 → 内部 fpsMode 2(C)/1(A)
                        fpsMode: fpsBaseNow,
                        contentFps: contentFpsNow,
                        animeHoldN: animeHoldN,
                        tempoResample: false,   // 节奏重采样(实验)已下线:用标准补帧(同一引擎、更快、同样平滑)
                        postSharpen: postSP, postClarity: postCL,
                        postUsm: postUM, postDetail: postDT,
                        postDeblur: postDB,
                        postFlicker: postFL,
                        postDenoise: postDN,
                        postAa: postAA,
                        mute: muteNow,
                        postMotionBlur: mblurNow,
                        postDeshake: deshakeNow,
                        videoDenoise: vdenoiseNow,
                        quality: qualityNow,
                        fastMode: fastNow,
                        upscaleShrink1x: upscaleShrink1x,
                        codecPref: codecNow,
                        customBitrateMbps: bitrateNow,
                        // 可变帧率(VFR)拆帧:默认「自动」= 加入列表时已探测(IsVfr),是 VFR 素材就自动
                        // 按原节奏逐帧提取(时间轴保真);面板收起也生效,用户无须手动开启。
                        // 仅当用户显式选「不启用」时按常规方式处理。
                        vfrPassthrough: vfrModeNow == 0 && item.IsVfr,
                        allowFewFrames: allowFew,
                        pauseWait: PauseWaitAsync);
                    item.Progress = 100;
                    item.StatusText = "✓ 完成";
                    item.EtaText = "";   // 完成时清空预计时间
                    item.IsDone = true;   // 完成后项目变灰,默认不再重复处理
                    ScheduleAutoRemove(item);   // 设置开启时:3 秒后自动删除该项目
                    RefreshVideoProgress(items, $"完成 {item.Name}");
                    // 输出信息:帧率 / 分辨率 / 大小(去重结果用简短版,蓝色小字不截断;细节见日志)
                    try
                    {
                        var outInfo = await VideoService.ProbeVideoInfoAsync(outPath);
                        var mb = new FileInfo(outPath).Length / 1048576.0;
                        var dedupShort = VideoService.LastDedupShort;
                        var dedupNote = VideoService.LastDedupReport;
                        item.OutputInfo = $"输出:{outInfo} · {mb:0.0} MB" +
                            (dedupShort != null ? $" · {dedupShort}" : "");
                        if (dedupNote != null) Log($"  {dedupNote}");   // 完整细节写入日志区,不再一闪而过,不再一闪而过
                    }
                    catch { }
                    UpdateTaskPanel("完成", finished: true);
                    outputFiles.Add(outPath);   // 记录成功输出(弹窗高亮/列名用)
                    Log($"  ✓ {Path.GetFileName(outPath)}");
                    Log($"    计算设备:{devStr} · 压缩编码器:{VideoService.LastVideoEncoderInfo}");
                    okCount++;
                }
                double? tStart = null, tEnd = null, itemFpsNow = inFps;
                try
                {
                    // 每个视频独立应用自己的裁剪范围
                    // 0.1s 阈值改 0.02:用户短裁剪(如去片尾 50ms)不再静默失效;Duration 未加载完时,
                    // 只要有 TrimEnd 值就用(否则 tEnd 被静默置 null → 结束裁剪失效——用户实测)。
                    tStart = item.TrimStart > 0.02 ? item.TrimStart : null;
                    tEnd = (item.TrimEnd > 0.02 && item.TrimEnd < (item.Duration > 0 ? item.Duration - 0.02 : double.MaxValue))
                        ? item.TrimEnd : null;
                    // 多视频:按所选「视频帧率」模式取值——模式2(单独调整)才用右侧保存的 CustomFps;
                    // 模式0(默认)用各视频原帧率;模式1(偏移)用原帧率+偏移。
                    // (之前 CustomFps 始终优先:切回「默认帧率」后仍被旧值覆盖,模式语义不干净)
                    if (multi)
                    {
                        if (fpsMode == 2 && item.CustomFps is > 0)
                            itemFpsNow = item.CustomFps;
                        else if (fpsMode == 1)
                        {
                            var probe = VideoService.ProbeFps(item.Path);
                            itemFpsNow = double.TryParse(probe, NumberStyles.Float, inv, out var pf) && pf > 0
                                ? Math.Max(1, pf + fpsOffset) : null;
                        }
                        else
                            itemFpsNow = null;   // 模式0:各视频用原帧率
                    }
                    await ProcessOneAsync(false, tStart, tEnd, itemFpsNow);
                }
                catch (OperationCanceledException) { throw; }
                catch (DedupTooStrongException ex)
                {
                    // 防删光保护触发:用户确认「仍要进行」后跳过保护继续处理
                    if (await AskDedupTooStrongAsync(ex.Message))
                    {
                        Log("⚠ 用户确认「仍要进行」:跳过防删光保护继续处理");
                        try
                        {
                            await ProcessOneAsync(true, tStart, tEnd, itemFpsNow);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex2)
                        {
                            // 重跑失败:该项标失败,继续下一个(不中断整个任务)
                            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
                            item.StatusText = "✗ 失败";
                            AppLogger.Error($"视频处理失败: {item.Name}", ex2);
                            Log($"  ✗ 失败:{ex2.Message}");
                            _failReasons.Add($"{item.Name}: {ex2.Message}");
                            failCount++;
                        }
                    }
                    else
                    {
                        try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
                        item.StatusText = "✗ 失败";
                        AppLogger.Error($"视频处理失败: {item.Name}", ex);
                        Log($"  ✗ 失败:{ex.Message}");
                        _failReasons.Add($"{item.Name}: {ex.Message}");
                        failCount++;
                    }
                }
                catch (Exception ex)
                {
                    try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
                    item.StatusText = "✗ 失败";
                    AppLogger.Error($"视频处理失败: {item.Name}", ex);
                    Log($"  ✗ 失败:{ex.Message}");
                    failCount++;
                }
            }
            VideoProgress.Value = 100;
            VideoStatus.Text = $"完成 {okCount} 个";
            var taskSpan = DateTime.Now - taskStart;
            Log($"任务结束:成功 {okCount},失败 {failCount},耗时 {(int)taskSpan.TotalMinutes}分{taskSpan.Seconds}秒,输出 {outputFiles.Count} 个文件");
            // 耗时经验库:全部成功才算有效样本(失败会扭曲每帧成本),记录"秒/帧"(按总面积归一)
            if (okCount > 0 && failCount == 0 && totalFramesEst > 0 && taskSpan.TotalSeconds > 10)
            {
                double avgAreaN = 0;
                foreach (var it in items)
                {
                    try
                    {
                        var (w, h) = await VideoService.ProbeSizeAsync(it.Path);
                        avgAreaN += Math.Max(0.25, (double)w * h / 2_073_600.0);
                    }
                    catch { }
                }
                if (items.Length > 0) avgAreaN /= items.Length;
                PerfMemory.Record(perfKey, taskSpan.TotalSeconds, totalFramesEst, avgAreaN);
                AppLogger.Info($"ETA 经验库:记录配置[{perfKey}] 实测 {taskSpan.TotalSeconds:0} 秒/{totalFramesEst} 帧 → {taskSpan.TotalSeconds / totalFramesEst / Math.Max(0.25, avgAreaN):0.###} 秒/帧");
            }
            TaskSummary.Text = failCount > 0
                ? $"完成:成功 {okCount} 个,失败 {failCount} 个"
                : $"✓ 全部完成({okCount} 个视频)";
            await ShowResultAsync(okCount, failCount, baseDir, outputFiles);
        }
        catch (OperationCanceledException)
        {
            VideoStatus.Text = "已取消";
            var cancelSpan = DateTime.Now - taskStart;
            Log($"⚠ 已取消:成功 {okCount},失败 {failCount},未处理 {Math.Max(0, _taskTotalCount - okCount - failCount)} 个,耗时 {(int)cancelSpan.TotalMinutes}分{cancelSpan.Seconds}秒");
            TaskSummary.Text = $"已取消:成功 {okCount} 个,失败 {failCount} 个,未处理 {Math.Max(0, _taskTotalCount - okCount - failCount)} 个";
        }
        finally
        {
            // 清理各视频项的处理状态
            foreach (var it in _videos)
            {
                it.SetProcessing(false);
                if (it.StatusText == "等待处理..." || it.StatusText.StartsWith("✗") || it.StatusText.StartsWith("✓"))
                    it.StatusText = "";
            }
            cts.Dispose();
            _cts = null;
            _running = false;
            _paused = false;
            _resumeTcs = null;
            _runItems = null;
            CancelBtn.IsEnabled = false;
            UpdateListButtons();   // 处理结束,恢复右侧列表删除/清空按钮
            UpdateRunState();      // 暂停/恢复按钮复位
            UpdateOptions();
        }
    }

    /// <summary>按当前列表重新计算进度(暂停删除未处理项后,总数变小,进度条直接跳变更新)。</summary>
    private void RefreshVideoProgress(VideoItem[] items, string statusText)
    {
        int done = items.Count(it => IsItemDone(it) && _videos.Contains(it));
        int active = items.Count(it => _videos.Contains(it) && !IsItemDone(it));
        if (done + active > 0)
        {
            VideoProgress.Value = Math.Max(VideoProgress.Value,
                (int)Math.Round(done * 100.0 / (done + active)));
            VideoStatus.Text = $"({done}/{done + active}) {statusText}";
        }
        else
        {
            VideoProgress.Value = 100;
            VideoStatus.Text = statusText;
        }
    }

    private async Task ShowResultAsync(int ok, int fail, string dir, System.Collections.Generic.List<string>? outputFiles = null)
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
                Text = $"成功 {ok} 个{(fail > 0 ? $",失败 {fail} 个" : "")}\n输出目录:\n{dir}{listText}" +
                    (fail > 0 && _failReasons.Count > 0
                        ? "\n\n失败原因:\n· " + string.Join("\n· ", _failReasons)
                        : ""),
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
            },
            PrimaryButtonText = "打开输出文件夹",
            CloseButtonText = "关闭",
            XamlRoot = this.XamlRoot,
        };
        // 保护:任务完成时窗口可能已关闭(XamlRoot 为 null)→ 不再弹窗,避免未处理异常
        if (dlg.XamlRoot == null) return;
        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
            ProcessStartHelper.OpenSelect(outputFiles.Count > 0 ? outputFiles : new System.Collections.Generic.List<string> { dir });
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        // 「强制结束」始终停止当前任务(包括休息中);跳过休息请用底部右侧专属「跳过休息」按钮
        VideoStatus.Text = "正在停止...";
        Log("用户点击「强制结束」,正在停止任务");
        _cts?.Cancel();
    }
}
