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
    /// <summary>探测到的源分辨率(0 = 还没探到)。**只用于界面提示**(如补帧兼容性预检),
    /// 由已有的探测流程写入 —— 不许在 UI 回调里现跑 ffprobe(那会卡界面)。</summary>
    public int ProbeWidth { get; set; }
    public int ProbeHeight { get; set; }

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
    private long _lastUiTick;   // 进度回调节流:100ms 内只刷一次界面(防长视频每帧报告淹没 UI 线程)
    private CancellationTokenSource? _cts;
    private string? _customOutDir;
    private bool _suppressEvents;
    private bool _settingsLoaded;   // LoadSettings 完成后才允许保存(防构造/加载期的 -1 值污染 video-settings.json)
    // 【启动崩溃修复 · 2026-09-14】XAML 解析是否已完成(解析期 = InitializeComponent() 还没返回)。
    //   InitializeComponent() 是**同步**加载 XBF:逐个元素创建 + 赋初值。只要元素在 XAML 里带了初值
    //   (IsChecked="True" / SelectedIndex="0" / Value=...),WinUI 就在赋值那一刻**同步**派发
    //   Checked/SelectionChanged/ValueChanged —— 而此刻"排在它后面"的控件**还没被创建**,事件链路里
    //   任何读那些控件的代码都是空引用。实测(带符号构建真启动,堆栈带行号):
    //     XAML:691 SmoothTimelineCheck IsChecked="True" → Options_Changed → OnOptionChanged()
    //     → UpdateOptions() → UpdateRunState() → VideoView.xaml.cs:528 `RunBtn.IsEnabled`
    //     (RunBtn 声明在 XAML:734,解析到 691 时还不存在)→ NullReferenceException
    //     → 异常从 IsChecked 的 setter 冒出 → WinUI 报 "Failed to assign to property
    //       'ToggleButton.IsChecked'" → XamlParseException 0x802B000A → 视频页加载失败
    //     → 进程退出码 0xC000027B(stowed exception)。
    //   (当时的触发源 SmoothTimelineCheck 已于 2026-09-15 从界面移除,但**同类风险依旧存在** ——
    //    例如 UpscaleToggle(XAML:41)仍带 IsChecked="True" + Options_Changed,故本标志位必须保留。)
    //   故:解析期一律不处理参数变化事件 —— 这样整条链路(含今后新增控件的读点)永远不会在控件没建好时
    //   被触发;InitializeComponent() 一返回就置 true,设置恢复与用户点勾选框/滑块/下拉的行为和日志完全不变。
    //   (为什么不用 _suppressEvents 的初值来挡:LoadSettings 有两条提前 return 的路径不会把它复位成 false,
    //    会让交互永久失效;本标志位在构造函数里无条件置 true,没有任何"忘了复位"的风险。)
    private bool _uiReady;
    private VideoItem? _selected;
    // 「输入帧率」框里那个值是从哪个视频探测来的(null = 无来源/用户手填):
    // 用于在开始处理时识别"框里还留着上一个视频的帧率"的残留(输入帧率参与节奏换算,残留会算错结果)。
    private VideoItem? _inputFpsOwner;
    // 【任务 P】"用户刚点过的那个视频"(含已完成的)。列表是多选模式,点一个"已选中"的项会变成"取消选中",
    // 而选择变化回调在"没选中"时按设计清空帧率框 —— 记住它才能在取消选中的情况下仍显示它的帧率。
    // 该字段只在"列表清空 / 删掉该项 / 列表已不含它"时被清掉(清掉后框才允许为空)。
    private VideoItem? _lastClickedItem;
    private int _gpuCount;

    // ---- 暂停/恢复:暂停后停在下一个视频之前,可删除"未处理"的项目 ----
    private bool _paused;
    private TaskCompletionSource<bool>? _resumeTcs;
    private VideoItem[]? _runItems;   // 本次任务的快照(删除列表项不影响遍历)

    public VideoView()
    {
        this.InitializeComponent();
        _uiReady = true;   // XAML 解析已完成(此后所有控件都已创建);见 _uiReady 字段说明
        // 【第 3 项】确保"DirectML 是否可用"这件事已经有结论(幂等兜底):
        // MainPage 的启动自检只在"存在超分 ONNX 模型(ESRGAN x4plus)"时才调用 EnsureDmlProbeAsync,
        // 而那台机器若只装了 waifu2x/动漫模型(视频超分实际用的就是它们),探测从未发生 →
        // DmlProbeCompleted 恒为 false → 视频页永远给不出"超分将落到 CPU"的醒目提示。
        // 本页自己补跑一次(后台线程,不阻塞 UI;MainPage 已跑过时立即返回),完成后再刷新内联提示。
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await ALHPro.EsrganOnnxService.EnsureDmlProbeAsync().ConfigureAwait(false);
                DispatcherQueue.TryEnqueue(() => _ = RefreshVideoOutSpec());
            }
            catch { }
        });
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

        // 屏幕刷新率探测:仅保留给提示用(2026-09-16 起「跟随屏幕刷新率」那一档已随下拉一起删除 ——
        // 用户要的是"两个单选按钮 + 一个输入框";这个数还用来在提示里提醒"填的值超过屏幕刷新率"）。
        try
        {
            var dm = new DEVMODEW { dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf<DEVMODEW>() };
            if (EnumDisplaySettingsW(null, -1, ref dm) && dm.dmDisplayFrequency > 24)
                _screenHz = dm.dmDisplayFrequency;
        }
        catch { }
        UpdateComponentStatus();
        // 计算设备:统一在「设置」里选择(AppSettings.GpuIndex),页面不再显示下拉
        _gpuCount = GpuInfo.EngineDeviceCount;
        // 参数默认值(InitializeComponent 后设置,避免 XAML 解析期事件)
        DedupHiSlider.Value = 12;      // 手动-重复帧检测:默认=动漫模式参数(研究校准值)
        DedupLoSlider.Value = 5;
        DedupFracSlider.Value = 0.33;
        DedupSceneSlider.Value = 0.01; // 手动-画面变化阈值
        DedupSadSlider.Value = 3.0;    // 手动-帧差+SSIM:快筛阈值
        DedupSsimSlider.Value = 0.97;  // 手动-帧差+SSIM:SSIM 阈值
        DedupAlgoCombo.SelectedIndex = 0;
        // 【转场识别】勾选框保留(默认不勾);阈值滑条已删 ⇒ 这里没有要重置的阈值控件。
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

    /// <summary>当前计算设备是否为核显(名字识别:Intel UHD/Iris/*Intel(R) Graphics* / AMD Radeon(TM) Graphics)。
    /// 用引擎真实枚举按 -g 编号取名字(不能用注册表顺序索引——AMD 核显+NVIDIA 独显双卡机上两者顺序相反)。</summary>
    private bool CurrentIsIntegratedGpu()
    {
        try
        {
            var idx = CurrentGpuId;
            if (idx < 0) return false;   // CPU 模式,不算核显
            var n = GpuInfo.GetEngineDeviceName(idx);
            if (n.Length == 0) return false;
            return GpuInfo.IsIntegratedGPU(n);
        }
        catch { }
        return false;
    }

    // 当前计算设备(全局设置):-1 = CPU;≥0 = GPU 编号。
    private int CurrentGpuId
    {
        // 【H1 · 2026-09-13 自检修】这里原来自己写了一份"编号是否在设备表里"的解析 —— 图片页/抠图页/本页各一份,
        // 三份彼此重复。现已删掉:判定只保留唯一权威入口 EngineService.ResolveEngineGpu,它内部调已单测的
        // AlhPro.Core.DeviceRouting.ResolveEngineDevice(含"编号不在表→换表内设备、绝不落 CPU"与
        // "撞号到核显且表里另有独显→换最佳独显")。三份重复解析正是"选独显却跑核显"反复出现的成因:
        // 各改一半就会分叉,而分叉时没有任何测试能发现 —— 上一版公告说的"统一到 DeviceRouting"就是这么落空的。
        get => EngineService.ResolveEngineGpu(AppSettings.GpuIndex);
    }

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
        // 【任务 M2 · 2026-09-13】去重可以单独跑(不超分、不补帧):只勾去重也允许开始处理。
        // 下游本来就支持"只去重导出"(拆帧→去重→按内容帧率合帧,时长不变),这里只是把入口放开。
        bool anyWork = UpscaleToggle.IsChecked == true || InterpToggle.IsChecked == true
            || DedupCheck.IsChecked == true;
        // 【2026-09-16 用户裁决:帧率框不许留空】启动期(构造/恢复设置)还没填值时也先锁住按钮 + 给提示。
        // 与 RunBtn_Click 里那道拦截是同一个判据,两道都在:这里负责"让用户看得见为什么不能点",
        // 那里负责"已经点下去也要拦住"(用户可能先填了值再清空)。
        bool targetFpsMissing = InterpToggle.IsChecked == true && IsTargetFpsMode() && SelectedTargetFps() is null;
        RunBtn.IsEnabled = _videos.Count > 0 && !_running && anyWork && !targetFpsMissing;
        // 「预览效果」:同样要求至少启用一项处理(预览跑的就是这套参数);只要有视频就能点 ——
        // 没在右侧选中时会取第一个待处理的,并可在预览面板里直接换
        if (PreviewBtn != null)
            PreviewBtn.IsEnabled = _videos.Count > 0 && !_running && anyWork && !targetFpsMissing;
        if (RunHint != null)
        {
            bool showRunHint = _videos.Count > 0 && !_running && (!anyWork || targetFpsMissing);
            RunHint.Visibility = showRunHint ? Visibility.Visible : Visibility.Collapsed;
            if (showRunHint)
                RunHint.Text = targetFpsMissing && anyWork
                    ? "输出帧率选了「指定帧率」,请填一个目标帧率(如 60 / 120),或改回「按倍率」"
                    : "⚠ 至少启用一项处理(超分、补帧或去重)";   // 与 XAML 里的默认文案逐字一致
        }
        // 只勾去重(超分/补帧都关)时说明会导出什么:帧率变成内容帧率、时长不变
        if (DedupOnlyHint != null)
            DedupOnlyHint.Visibility = _videos.Count > 0 && !_running && anyWork
                && UpscaleToggle.IsChecked != true && InterpToggle.IsChecked != true
                ? Visibility.Visible : Visibility.Collapsed;
        // 耗时提示(黄色):启用耗时的功能时,提示处理时间会增加(开什么显示什么)
        if (SpeedHint != null)
        {
            var slow = new System.Collections.Generic.List<string>();
            bool interp = InterpToggle.IsChecked == true;
            if (UpscaleToggle.IsChecked == true) slow.Add("超分");
            if (TtaCheck.IsChecked == true) slow.Add("高质量 TTA");
            if (interp && AlhPro.Core.InterpScaleMap.IsHighRate(CurrentScaleIndex())) slow.Add("高倍率补帧");
            if (interp && SceneCheck.IsChecked == true) slow.Add("转场识别");
            if (DenoiseToggle.IsChecked == true) slow.Add("视频降噪");
            if ((int)SharpenSlider.Value > 0 || (int)ClaritySlider.Value > 0 || (int)UsmSlider.Value > 0
                || (int)DetailSlider.Value > 0
                || PostAaSlider.Value > 0 || PostEdgeSlider.Value > 0)
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
            // 引擎兼容自检(不限 50 系):结论【一律以真机实测为准】,不按显卡型号猜。
            // · Real-ESRGAN:本机实测 ncnn 不可用 → 建议换 waifu2x
            // · RIFE:补帧下拉现在只剩 v4.13 / v4.6,两支本机实测都可用 ⇒ 不再需要按模型提示(旧模型分支已随下拉精简删除)
            string? compatMsg = null;
            bool upOn = UpscaleToggle.IsChecked == true;
            bool interpOn = InterpToggle.IsChecked == true;
            // 【以实测为准,不再按型号猜】只有真测出"本机 realesrgan ncnn 不可用"才提示;没测过不提示
            // (原先用 OldNcnnGpuRisky() → 50 系一律提示,而实际探测往往是能跑的,等于对用户说反话)
            if (upOn && SelectedEngineIsReal
                && EngineService.TryGetNcnnVerdict("realesrgan", AppSettings.GpuIndex) == false)
            {
                compatMsg = $"⚠ 本机实测「{EngineService.EngineLabel("realesrgan")}」无法用 GPU 加速,建议改用「waifu2x」(官方新版,更稳定)";
            }
            // 【2026-09-16 下拉精简】原本这里还有一条"实测本机非 v4 老模型(动漫/高清/超高清/经典兼容)的 ncnn
            // 补帧不可用"的提示;那 4 项已下架 ⇒ 条件恒不成立 ⇒ 整段删除(连同它的 else if 与所属花括号),
            // 不留永远进不去的死代码。引擎兼容提示只留上面 realesrgan 那一条
            // (它是唯一还可能实测失败的引擎;RIFE 只剩 v4.13/v4.6,两支都已通过本机实测)。
            // 【2026-09-16 用户裁决:黑帧检测已删除 ⇒ 兼容性提示成为**唯一防线**】
            // 补帧这条链路上,已知会"静默出黑帧"的组合必须在使用者**开始处理之前**就说清楚:
            //   ① 8K 级输入(实测 117/119 全黑)② RTX 50 系(Blackwell)③ 无独显 / Vulkan 不可用
            // 判据在 Core.InterpSizePolicy.JudgeEngineRisk(纯逻辑、有单测),和管线里那道预检**同源** ——
            // 界面说的和真跑时做的是同一件事,不会各说各话。
            if (compatMsg == null && interpOn)
            {
                try
                {
                    int pw = 0, ph = 0;
                    foreach (var v in _videos)
                        if (v.ProbeWidth > 0 && v.ProbeHeight > 0) { pw = v.ProbeWidth; ph = v.ProbeHeight; break; }
                    bool noVulkan;
                    try { noVulkan = !VulkanCheck.GpuAvailable; } catch { noVulkan = false; }
                    var risk = AlhPro.Core.InterpSizePolicy.JudgeEngineRisk(pw, ph,
                        gpuIs50Series: EngineService.IsBlackwellGpu(),
                        gpuRiskyOldNcnn: noVulkan,
                        onnxModelAvailable: RifeOnnxService.Available());
                    if (risk.Warn)
                        compatMsg = risk.UseStableEngine
                            ? $"※ 补帧兼容性:{risk.Reason}(已自动改用稳定引擎)"
                            : $"⚠ 补帧兼容性:{risk.Reason}";
                }
                catch { }
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
        // 预览不接受暂停(_previewRun != null):按钮一律不可点,避免"点了没反应"
        PauseBtn.IsEnabled = _running && !_paused && _previewRun == null;
        ResumeBtn.IsEnabled = _running && _paused && _previewRun == null;
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
        if (!_running || _paused || _previewRun != null) return;   // 预览期间「暂停」无意义
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
        if (!_running || !_paused || _previewRun != null) return;
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

    /// <summary>按超分模型给「边缘增强」推荐强度(用户 2026-09-15 定的:v3 用 0.3,其它四支用 0.6)。
    /// 【依据】实测 1080p→2x 游戏帧:官方 animevideov3 边缘宽度 2.23px、强边缘对比 52.3,
    /// 加 0.3 档后 2.15px / 58.0(+11%),过冲 1.05%→1.40%;其它几支模型边缘本来就偏弱,
    /// 用 0.6 档:2.24→2.16px、56.6→69.3(+22%),过冲 1.23%→1.96%。
    /// 模型名走 Tag(realesr-animevideov3 / realesr-general-x4v3 / …),和管线用的是同一个字符串。</summary>
    private static int RecommendedEdgeBoost(string? model)
        => model != null && model.Contains("animevideov3", StringComparison.OrdinalIgnoreCase) ? 30 : 60;

    /// <summary>切模型时把「边缘增强」带成推荐值。
    /// 【判据:当前值是不是"推荐值的集合"】等于 0 / 30 / 60 就认为用户没自定义过 → 跟着模型走;
    /// 其它数值(如用户手调成 45)一律保留。
    /// 【为什么不用"用户是否拖过滑条"的标记】实测踩坑:初始化/载入设置时会**程序化**给滑条赋 0,
    /// 那也会触发 Slider_Changed → 标记被误置 → 自动默认全被挡掉(真机 UIA 验证时发现切模型后仍是 0)。
    /// 现在改成只看数值,确定性强、与初始化顺序无关。</summary>
    private void ApplyRecommendedEdgeBoost()
    {
        if (PostEdgeSlider == null || VideoEsrganModelCombo == null) return;
        var cur = (int)PostEdgeSlider.Value;
        if (cur != 0 && cur != 30 && cur != 60) return;      // 用户自定义过 → 不动
        var tag = (VideoEsrganModelCombo.SelectedItem as Microsoft.UI.Xaml.Controls.ComboBoxItem)?.Tag as string;
        var want = RecommendedEdgeBoost(tag);
        if (cur != want) PostEdgeSlider.Value = want;
    }

    private void Combo_Changed(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(sender, VideoEsrganModelCombo)) ApplyRecommendedEdgeBoost();
        OnOptionChanged();
    }

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
    {
        // 用户手改「输入帧率」框 = 显式覆盖(要跟"程序探测来的值"区分开:探测值带 owner,
        // 开始处理时若发现 owner 不是本次要处理的视频就会被丢弃,手填值则原样保留)。
        // 必须判 _suppressEvents:程序写入(SetInputFpsText)同样会触发本回调。
        if (!_suppressEvents && ReferenceEquals(sender, InputFpsBox)) _inputFpsOwner = null;
        OnOptionChanged();
    }

    private void OnOptionChanged()
    {
        // 【启动崩溃修复 · 2026-09-14】XAML 解析期一律直接返回 —— 根因与证据见 _uiReady 字段说明。
        //   解析期(XBF 边创建边赋初值)派发的事件不能处理:此刻排在后面的控件还没建好,
        //   链路里读它们(RunBtn/PauseBtn/ResumeBtn/VideoList/SpeedHint… 都在 XAML 611 行之后)
        //   就是空引用,异常会从属性赋值里冒出来 → 整页加载失败 → 应用启动即崩。
        //   解析期不做参数联动/写盘是本页**既有语义**(修复前靠 InterpHint 哨兵拦掉 611 行之前的全部事件);
        //   本标志位把它变成"拦掉全部",于是新增控件再也不会引入这类崩溃。
        if (!_uiReady) return;
        if (_suppressEvents) return;
        // 保留历史哨兵(InterpHint 声明在 XAML 611 行):双保险,行为不变
        if (InterpHint == null) return;
        // 【2026-09-17】预览页开着且已经跑出过成片时,左侧参数一改就提示"参数已改动 · 点重新预览"
        // (方法自身判空,不看预览页也安全)
        MarkPreviewDirty();
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

    // ===== 超分引擎:界面顺序 Real-ESRGAN(上) / waifu2x(下) =====
    // 【为什么要这层映射】界面顺序按用户要求改成 real 在上,但**存盘沿用旧约定**
    // (0=waifu2x, 1=realesrgan, 2=更早的 Real-CUGAN):老用户存过的选择不会被顺序调整翻转。
    /// <summary>界面索引 → 存盘值(旧约定)。</summary>
    private static int EngineToStored(int uiIndex) => uiIndex == 1 ? 0 : 1;   // ui: 0=real,1=waifu
    /// <summary>存盘值(旧约定) → 界面索引。</summary>
    private static int EngineFromStored(int stored) => stored == 0 ? 1 : 0;   // 0=waifu → ui 1;1/2 → ui 0(real)
    /// <summary>当前选中的超分引擎是否是 Real-ESRGAN(界面上排第一个 = 索引 0)。</summary>
    private bool SelectedEngineIsReal => VideoEngineRadios.SelectedIndex == 0;
    /// <summary>选 waifu2x 显示 waifu2x 模型下拉,选 Real-ESRGAN 显示其模型下拉;并确保默认选中首个模型。</summary>
    private void UpdateVideoModelVisibility()
    {
        if (VideoWaifu2xModelCombo == null || VideoEsrganModelCombo == null) return;
        bool waifu2x = !SelectedEngineIsReal;
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

    // ===== 「输出倍率」这一组按钮 = 6 档倍率 + 「指定帧率」共 7 项(2026-09-16 用户第三次反馈定稿)=====
    // 用户原话:「就是放在倍率下面 就和倍率一样的感觉」「不要这个多出来的界面 就像是多出来一个倍率的单选按钮
    // 输入框置灰 选择这个指定帧率的时候输入框可以输入 而不是多此一举的多一个选倍率和选指定的按钮」。
    // ⇒ **没有**单独的模式按钮组:那 7 个单选按钮里,前 6 个是倍率、第 7 个是「指定帧率」;
    //   下面的输入框选倍率时**置灰**、选中「指定帧率」时才可输入。
    // 【序号契约】0~5 = 2x/3x/4x/8x/12x/16x 与 Core.InterpScaleMap 同一张表(它有单测);
    //   6 = 指定帧率 —— **不许**把 6 丢给 InterpScaleMap(它会按越界落回 2x),一律走 CurrentScaleIndex()。
    private const int TargetFpsScaleIndex = 6;
    /// <summary>切到「指定帧率」之前用户选的那个真实倍率序号(0~5)。切回去用它还原,不丢用户的选择。</summary>
    private int _lastScaleIndex;
    /// <summary>显示器刷新率(初始化时探测;0 = 没探到)。**只用于提示**(提醒"填的值超过屏幕刷新率")。</summary>
    private int _screenHz;

    /// <summary>当前选中的补帧模型是不是「通用画质」系列(V4 = 下拉前两项)。只有它支持任意目标帧率。</summary>
    private bool IsV4ModelSelected() => InterpModelCombo?.SelectedIndex is 0 or 1;

    /// <summary>「输出倍率」这组按钮当前代表的**真实倍率序号**(0~5)。
    /// 选中「指定帧率」(6)时返回用户上一个真实倍率 —— 因为那时倍率由目标帧率反推、这组按钮不再决定补帧倍率,
    /// 但"置灰但值保留"的语义要求我们记住它(用户切回来时还在)。</summary>
    private int CurrentScaleIndex()
    {
        int i = InterpScaleRadios?.SelectedIndex ?? 0;
        if (i == TargetFpsScaleIndex) return Math.Clamp(_lastScaleIndex, 0, 5);
        return i is >= 0 and <= 5 ? i : 0;
    }

    /// <summary>当前是不是选中了「指定帧率」那一项(控件缺失时按默认"没选"处理,防初始化期空引用)。</summary>
    private bool IsTargetFpsMode() => (InterpScaleRadios?.SelectedIndex ?? 0) == TargetFpsScaleIndex;

    /// <summary>选中的目标输出帧率(null = 没选「指定帧率」/ 框是空的 / 模型不支持)。
    /// **取值只此一处**,别在别处再解析控件。</summary>
    private double? SelectedTargetFps()
    {
        if (!IsTargetFpsMode()) return null;
        if (!IsV4ModelSelected()) return null;   // 老架构模型做不到任意目标帧率,按"没指定"处理(那一项也会被置灰)
        return double.TryParse(TargetFpsBox?.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var tf) && tf > 0
            ? tf : null;
    }

    /// <summary>把保存的帧率值回填到界面:有效值 → 选中「指定帧率」+ 填进框;否则 → 回到上一个真实倍率。</summary>
    private void SetTargetFpsSelection(bool on, string? raw)
    {
        if (!on || !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v <= 0)
        {
            if (InterpScaleRadios != null && IsTargetFpsMode())
                InterpScaleRadios.SelectedIndex = Math.Clamp(_lastScaleIndex, 0, 5);
            return;
        }
        if (InterpScaleRadios != null) InterpScaleRadios.SelectedIndex = TargetFpsScaleIndex;
        if (TargetFpsBox != null) TargetFpsBox.Text = v.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>「指定输出帧率」下面那行提示:把"会自动补多少倍 / 大约输出多少"提前摆出来给用户看。
    /// 【口径与处理端同源】所需倍率**只调** `VideoPipeline.InterpScaleForTargetFps`(Core 里那一份);
    /// 这里不再自己算,免得界面说"自动 4x"、实跑却是别的倍率。
    /// 【为什么必须写倍率】用户的原话是"选 200 帧就自动补帧到 200 往上的倍率"—— 倍率是软件算的,不告诉他就等于黑箱。</summary>
    private void UpdateTargetFpsHint(bool interp, bool v4Model)
    {
        if (TargetFpsHint == null) return;
        var inv = CultureInfo.InvariantCulture;
        double? tf = interp ? SelectedTargetFps() : null;
        // 【模式 ≠ 有效值】选了「指定帧率」但框是空的(或填了非正数):必须提示,不能静默当"按倍率"跑 ——
        // 用户裁决"不能留空、总得有个值",所以这里把"还没填"当成待完成项明确写出来。
        if (tf is not > 0)
        {
            if (IsTargetFpsMode() && interp)
            {
                TargetFpsHint.Visibility = Visibility.Visible;
                TargetFpsHint.Text = $"⚠ 请填一个目标帧率(如 60 / {(_screenHz > 0 ? _screenHz.ToString(CultureInfo.InvariantCulture) : "120")}),不填不能开始";
            }
            else
            {
                TargetFpsHint.Visibility = Visibility.Collapsed;
                TargetFpsHint.Text = "";
            }
            return;
        }
        TargetFpsHint.Visibility = Visibility.Visible;
        if (!v4Model)
        {
            TargetFpsHint.Text = "该模型不支持指定帧率(老架构只能 2x 级联):换「通用画质」系列的模型才可用";
            return;
        }
        double baseFps = 0;
        // 基准帧率:多视频「单独调整」模式下用输入帧率框;否则用该视频探测到的帧率(与处理端口径一致)
        if (FpsModeRadios.SelectedIndex == 2
            && double.TryParse(InputFpsBox.Text, NumberStyles.Float, inv, out var f) && f > 0)
            baseFps = f;
        else
            foreach (var v in _videos)
                if (double.TryParse(v.FpsProbe, NumberStyles.Float, inv, out var p) && p > 0) { baseFps = p; break; }
        if (baseFps <= 0)
        {
            TargetFpsHint.Text = $"输出 {tf.Value:0.##} fps(倍率按该视频的内容帧率自动算够)";
            return;
        }
        // 【唯一一份判据】倍率必须与处理端同源(2026-09-16 审计第 4 条):这里以前又内联了一遍
        // `max(2, min(8, ceil(目标 ÷ 基准)))` —— 与 VideoService 里那份各自维护,谁改了都会让界面与实跑对不上。
        int need = AlhPro.Core.VideoPipeline.InterpScaleForTargetFps(tf.Value, baseFps, baseFps, v4Model);
        double got = baseFps * need;
        // 【必须把"自动抬上去的那个倍率"单独说出来】原先这行只印 `{baseFps} × {need}`,而页面下方「输出倍率」
        // 那组单选还亮着用户手选的那个数(比如 2x)。用户看到的就是"界面写 2x、提示写 3x"——
        // 要么以为提示算错了,要么以为软件没按自己选的跑。两个数不一致时就把话说全:自动 Nx ≠ 下方所选 Mx。
        int chosen = AlhPro.Core.InterpScaleMap.Multiplier(CurrentScaleIndex());
        string auto = chosen != need ? $"(自动 {need}x,不是下方所选的 {chosen}x)" : $"({baseFps:0.##}fps × {need})";
        // 【末尾那句必须说清"不会先多补再裁尾"】`need` 是整数倍率(为了"够用"取上界),而实际产出帧数按
        // 目标帧率精确分配(分数步长),否则多补出来的帧只能从尾部删掉 = 尾部内容丢失。用户看不出这一层,
        // 只会发现"成片比源短一截",所以这里直接写明白。
        TargetFpsHint.Text = got >= tf.Value - 0.5
            ? $"自动 {need}x 补帧{auto} → 约 {got:0.##} fps;实际按目标帧率精确补足(不会先多补再裁尾)"
            : $"⚠ 指定 {tf.Value:0.##} fps 达不到:倍率上限 8x,最高约 {got:0.##} fps(成片仍标指定值,但多出来的是重复帧)";
    }

    private void UpdateOptions()
    {
        var up = UpscaleToggle.IsChecked == true;
        var interp = InterpToggle.IsChecked == true;
        // 【模式 ≠ 有效值】用户裁决:输入框**不许留空**。所以这里按"模式"判定(选了「指定帧率」就该把倍率置灰),
        // 空值由提示行与开始前的校验去拦 —— 不能因为"还没填"就让倍率按钮亮着,那会让用户以为两种模式同时生效。
        var target = IsTargetFpsMode() && interp;
        var scene = SceneCheck.IsChecked == true && interp;
        // 去重可单独使用,不依赖补帧开关(UI 联动与门控用同一条件)
        var dedup = DedupCheck.IsChecked == true;
        var dedupModel = DedupModelCombo.SelectedIndex;   // 0智能检测 1动漫模式 2手动模式 ——【下拉只有 3 项】,服务端 dedupMode = 本索引+1(1智能/2动漫/3手动)。
        // ⚠ 历史上曾有一版是 7 项(智能/动漫/标准/温和/敏感/手动/内容帧率),注释没跟着改过,别再照旧注释写分支
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
        // 【4x-only 权重模型 × 倍率】realesrgan-x4plus / x4plus-anime 只有 4x 权重:选 2x/3x 时引擎会按非原生倍率
        // 贴图,实测【画面整体位移】(1080p 源约偏 56×40 像素:与源帧相关 0.71;原生 4x 时 0.999)。
        // 所以不再置灰禁用(用户要求放开),而是照常可选 —— 由超分引擎层统一按【原生 4x】执行、再由 App 精确缩回
        // (与图片路径既有做法一致,见 EngineService 的视频/图片 dir 路径 engineScale 处理),几何与画质都正确,
        // 耗时可忽略(-s 2 = 17.5s vs -s 4 = 18.1s)。此处只负责把这件事讲清楚。
        string esrModel = (VideoEsrganModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
        // 【4x 专用权重】x4plus / x4plus-anime / 自转的 general-x4v3 / 自转的 general-wdn-x4v3 都只有 4x 权重:
        // 选 2x/3x 会内部按 4x 跑再缩回(画面正确,但耗时与 4x 相同)。提示直接放在模型下拉正下方(用户要求)。
        // 【2026-09-14 补 wdn】名字是 general-**wdn**-x4v3,既不含 "x4plus" 也不含 "general-x4v3",必须单独判,
        // 否则这条提示不显示、且 Core.EngineScalePolicy 会误判成普通模型(那条已同步修)。
        bool x4plusModel = up && SelectedEngineIsReal
            && (esrModel.Contains("x4plus") || esrModel.Contains("general-x4v3") || esrModel.Contains("wdn-x4v3"));
        // 【2026-09-15 Rev4 · 自训的两支(游戏 · game2x / 现实 · real2x,蓝色「测试」标)】在下拉正下方如实说明数字:
        // 提示只在悬停里(ToolTip)的话,用户不悬停就看不到,于是很容易以为"新加的两支更强"。
        // 文案与实测数字集中在 Core.ExperimentalEsrgan(不与 XAML 的 ToolTip 各写一份,免得两处数字对不上);
        // 措辞按用户要求只用**事实陈述**,不用"实验性/测试版"这类定性词。
        // 选 3x/4x 时再补一句:这两支只有 2x 原生权重,会被 Core.EngineScalePolicy 固定按 2x 跑再放大到目标。
        bool experimentalModel = up && SelectedEngineIsReal && AlhPro.Core.ExperimentalEsrgan.IsExperimental(esrModel);
        if (EsrganModelHint != null)
        {
            if (experimentalModel)
            {
                var eh = AlhPro.Core.ExperimentalEsrgan.Hint(esrModel);
                if (scaleIdx is 2 or 3)   // 3x/4x:引擎会按原生 2x 跑,再由 App 放大到目标
                    eh += $"(本次目标 {scaleIdx + 1}x:会按 2x 超分后再放大到 {scaleIdx + 1}x,不是原生 {scaleIdx + 1}x 权重)";
                EsrganModelHint.Text = eh;
                EsrganModelHint.Visibility = Visibility.Visible;
            }
            else if (x4plusModel)
            {
                // 【2026-09-13 用户要求】去掉 ⚠ 图标(红色告警样式也一并去掉,见 VideoView.xaml 里改用 HintText 样式)。
                // 两分支原本是两套说法,现统一为同一句,原因:
                //   ①「轻量通用」是旧称呼(v1.3.5 起该模型已改名「通用」,耗时档现为「快」);
                //   ②「4x 直出反而更划算」与实测不符 —— 2x 目标下引擎照样全量算 4x、耗时与直接出 4x 完全相同
                //      (本机 1080p 实测 general-x4v3 4.62 秒/帧、x4plus-anime 11.4 秒/帧、x4plus 33.8 秒/帧),
                //      唯一差别只是输出尺寸更大、写盘与编码更久。
                EsrganModelHint.Text = "该模型只有 4x 权重:选 2x/3x 会按 4x 超分后再缩回(画面不变形,耗时与 4x 相同)";
                EsrganModelHint.Visibility = Visibility.Visible;
            }
            else EsrganModelHint.Visibility = Visibility.Collapsed;
        }
        if (x4plusModel)
        {
            // 【2026-09-13 用户要求】「放大倍数」那一栏不再显示这段"4x 权重/缩回"的说明 —— 它和模型下拉正下方
            // 那条提示(EsrganModelHint)说的是同一件事,两处重复显示属于冗余。此处删掉赋值即可:
            // ScaleHint 保留上面 scaleIdx 分支生成的"倍率本身"的说明(1x~2x 较快、倍率越高越慢…)。
        }
        InterpModelCombo.IsEnabled = interp;
        // 非 2 的幂倍率(3x/12x/16x)仅 v4 架构模型支持;其余模型按 2x 级联(置灰+已选回退)。
        // 【2026-09-16 下拉精简】下拉只剩两支 v4 模型(v4.13 / v4.6)⇒ 下面两个门槛恒为"可用 / 不坏"。
        // 判断保留不动:一是改动面最小(牵动 TTA 与 3x/12x/16x、指定帧率共五处开关),二是将来若再放回
        // 非 v4 老模型(或 v4.26),门槛会自动重新生效 —— 而不是静默把限制放开。
        bool v4Model = InterpModelCombo.SelectedIndex is 0 or 1;
        // v4.26 的 TTA(-x/-z)实测卡死、非 v4 级联模型 TTA 无效;两类都已下架 ⇒ 本判断恒 false。
        bool v426TtaBroken = InterpModelCombo.SelectedIndex is 2 or 3 or 4 or 5 or 6;
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
        // 非 v4 模型够不到的档位 → 回落到 2x。这里把「指定帧率」(序号 6)也算进去:它本来就置灰,
        // 但不能留着选中态(否则下游 CurrentScaleIndex() 会按上一个真实倍率算,而界面还亮着"指定帧率"字样)。
        if (!v4Model && (AlhPro.Core.InterpScaleMap.NeedsV4Model(InterpScaleRadios.SelectedIndex)
                         || InterpScaleRadios.SelectedIndex == TargetFpsScaleIndex))
            InterpScaleRadios.SelectedIndex = 0;
        // 补帧模型提示:随所选模型更新,说明推荐 / 其它模型的问题(选 v4.13 提示推荐,选其它提示局限)
        {
            string hint = InterpModelCombo.SelectedIndex switch
            {
                0 => "推荐:通用画质 v4.13(最新,支持任意帧数精确补齐,最稳).",
                1 => "通用画质 v4.6:与 v4.13 同架构、功能一样,效果/稳定性略逊;留作 v4.13 出问题时的备用.",
                _ => "请选择补帧模型(下拉现在只有 v4.13 / v4.6 两支).",
            };
            InterpModelHint.Text = hint;
        }
        // 「输出倍率」这组按钮 = 6 档倍率 + 「指定帧率」。v2/老架构模型(只能 2 的幂级联)做不到任意目标帧率
        // ⇒ 「指定帧率」那一项置灰;若用户正选着它,回退到上一个真实倍率(值仍保留,换回 v4 模型还能用)。
        bool fpsPickable = interp && v4Model;
        if (TargetFpsRadio != null)
        {
            TargetFpsRadio.IsEnabled = fpsPickable;
            TargetFpsRadio.Opacity = fpsPickable ? 1.0 : 0.5;
        }
        if (!v4Model && IsTargetFpsMode() && InterpScaleRadios != null)
            InterpScaleRadios.SelectedIndex = Math.Clamp(_lastScaleIndex, 0, 5);
        bool targetMode = fpsPickable && IsTargetFpsMode();
        // 记下用户选的"真实倍率",供"切到指定帧率再切回来"还原(选中 6 时不覆盖)
        if (InterpScaleRadios != null && InterpScaleRadios.SelectedIndex is >= 0 and <= 5)
            _lastScaleIndex = InterpScaleRadios.SelectedIndex;
        // 这组按钮本身**始终可用**(否则点了「指定帧率」以后就再也点不回倍率了);
        // 「指定帧率」选中时把倍率那几项压暗,让"现在由帧率说了算"看得见 —— 用户要求的就是这个观感。
        InterpScaleRadios.IsEnabled = interp;
        InterpScaleRadios.Opacity = interp ? 1.0 : 0.5;
        // 帧率输入框:只有选中「指定帧率」时才能输入(选倍率时置灰)—— 用户原话「输入框置灰」
        TargetFpsBox.IsEnabled = targetMode;
        TargetFpsBox.Opacity = targetMode ? 1.0 : 0.5;
        // 把"会自动补多少倍、大约输出多少帧率"提前显示出来(这就是用户要的"自动倍率")
        UpdateTargetFpsHint(interp, v4Model);
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
        // 指定帧率时倍率无意义 → 置灰已经在上面的「输出帧率」块里统一处理(那里同时管 v4Model 闸门),
        // 这里**不要**再赋值一次 —— 那会覆盖掉"目标帧率模式下倍率置灰"的口径。
        // 去重可单独使用(不勾补帧也能只去重导出);但去重内容帧率依赖补帧逻辑,不开补帧时用"标准/手动"更直观
        DedupCheck.IsEnabled = true;
        DedupModelCombo.IsEnabled = dedup;
        // 动漫模式:动画帧率变种(一拍N)仅动漫模式显示
        DedupAnimeRow.Visibility = dedup && dedupModel == 1 ? Visibility.Visible : Visibility.Collapsed;
        // 智能策略:仅智能模式显示(均衡/激进/保守)
        DedupSmartRow.Visibility = dedup && dedupModel == 0 ? Visibility.Visible : Visibility.Collapsed;
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
        // （阈值滑条已删:没有可启用/禁用的阈值控件;`scene` 变量仍用于下面的提示文字）
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
        // （转场阈值数值框已随滑条一起删除）

        // 视频调整数值
        SharpenVal.Text = SharpenSlider.Value.ToString("0");
        ClarityVal.Text = ClaritySlider.Value.ToString("0");
        UsmVal.Text = UsmSlider.Value.ToString("0");
        DetailVal.Text = DetailSlider.Value.ToString("0");
        PostAaVal.Text = PostAaSlider.Value.ToString("0");
        PostEdgeVal.Text = PostEdgeSlider.Value.ToString("0");

        // 输出帧率提示
        var inv = CultureInfo.InvariantCulture;
        int fpsModeNow = FpsModeRadios.SelectedIndex;
        var inFps = fpsModeNow == 2
            && double.TryParse(InputFpsBox.Text, NumberStyles.Float, inv, out var f) && f > 0 ? f : 0;
        var m = AlhPro.Core.InterpScaleMap.Multiplier(CurrentScaleIndex());
        var extras = new System.Collections.Generic.List<string>();
        if (dedup) extras.Add($"去重({DedupModelCombo.SelectedItem})");
        if (scene) extras.Add("转场识别");
        if (TtaCheck.IsChecked == true) extras.Add("TTA");
        var extra = extras.Count > 0 ? " · " + string.Join(" · ", extras) : "";
        if (interp && inFps > 0)
        {
            if (target && SelectedTargetFps() is { } tfNow)
            {
                // 指定帧率预判(开始前就提示,不等到处理完):
                // 所需倍率 = 目标帧率 ÷ 输入帧率;显示当前倍率够不够(不够处理时会自动抬倍率凑够帧数)
                double needScale = tfNow / inFps;
                InterpHint.Text = $"输出:{inFps:0.##} × {m} = {inFps * m:0.##} → {tfNow:0.##} fps (指定){extra}";
                if (needScale > m + 0.01)
                {
                    int minInt = Math.Max(2, (int)Math.Ceiling(needScale - 0.01));
                    // 不再写"输出仍精确 N fps":勾了去重时合帧走可变帧率时间轴,输出是平均帧率(保节奏),写"精确"是骗人
                    InterpHint.Text += $"\n提示:当前 {m}x 帧数不够,处理时会自动按 {minInt}x 补帧凑够帧数";
                }
                else if (needScale < 1.0 - 0.01)
                {
                    // 【2026-09-16 修】旧文案说的是"补帧只能增帧,所以成片还是输入帧率那个数" —— 与事实相反:
                    //   处理侧 (VideoService.cs:1253 起) 对目标帧率**至少按 2x 补帧**,补完再挂 `fps` 滤镜
                    //   精确重映射回用户选的那个值(时长不变)。所以输出就是**用户选的帧率**,不是输入帧率。
                    //   旧文案源自"勾选框 + 手输数字"那一代(已换成"单选按钮切模式 + 一个输入框"),当时确实可能兜底回输入帧率;
                    //   ⚠ 这段说明刻意**不逐字复述**旧文案 —— DedupTargetFpsRhythmTests 会扫全文件确认那句已绝迹。
                    //   现在如实告知的只剩"代价":倍率下限 2x ⇒ 补出来的多余帧会被丢掉(白算,但成片与选值一致)。
                    InterpHint.Text += $"\n注意:指定 {tfNow:0.##} fps 低于输入帧率 {inFps:0.##} fps —— 仍会自动至少 2x 补帧,"
                        + $"补完再缩到 {tfNow:0.##} fps(输出就是 {tfNow:0.##} fps、时长不变;多条补出来的帧会被丢掉)";
                }
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
        // 【2026-09-16 下拉精简】原来这里会在"选了非 v4 老模型 + 3x"时提示"3x 需要 v4 模型";
        // 老模型已全部下架、剩下两支都是 v4 ⇒ 这个组合不可能出现,整段删除(含它下一行的语句)。
        // 3x 的可用性判断仍在上面 v4Model 那一处(它才是真正置灰开关的地方)。
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
        // 「重置」填的是默认值,不是某个视频的探测值 → 清掉来源标记,让它按用户设定生效(与重置前的行为一致)
        _inputFpsOwner = null;
        InterpScaleRadios.SelectedIndex = 0;    // 输出倍率 = 2x(「指定帧率」已并进这一组,选 0 即"不指定")
        _lastScaleIndex = 0;
        TargetFpsBox.Text = "";
        FpsModeRadios.SelectedIndex = 0;   // 视频帧率:默认「各视频默认帧率」
        FpsOffsetSlider.Value = 0;
        DedupCheck.IsChecked = false;
        // 【默认档 = 智能检测(2026-09-12 用户反馈)】此前这里与 XAML 的启动默认都写的是 2=手动模式,
        // 用户一开去重看到的就是"手动模式"(还要自己挑算法、调一堆滑条),而三种模式里智能检测才是该默认的那个
        // (自动识别拍数;识别不出来就原样保留、不乱删帧)。XAML 的 SelectedIndex 已同步改成 0。
        DedupModelCombo.SelectedIndex = 0;
        DedupAnimeCombo.SelectedIndex = 0;   // 动漫模式:动画帧率变种,默认一拍二(最常用)
        DedupAlgoCombo.SelectedIndex = 0;   // 手动算法默认=内容帧率采样(UI 第 1 项)
        DedupHiSlider.Value = 12;
        DedupLoSlider.Value = 5;
        DedupFracSlider.Value = 0.33;
        DedupSceneSlider.Value = 0.01;
        ContentFpsBox.Text = "";
        FpsOffsetSlider.Value = 0;
        // 【默认关(2026-09-15 用户定调)】重置 = 回到默认值,而「转场识别」现在的默认值就是**不勾**
        // (与 XAML 启动默认、AlhPro.Core.SceneDefaultPolicy.DefaultScene 三处必须一致)。
        // 这是"用户主动点重置"⇒ 回到默认(关)是正确语义;要保护的人自己勾上(勾了以后两条路径都生效)。
        SceneCheck.IsChecked = AlhPro.Core.SceneDefaultPolicy.DefaultScene;
        // （阈值滑条已删:没有可重置的阈值;处理端用内置 0.3,写盘也写它）
        TtaCheck.IsChecked = false;
        SharpenSlider.Value = 0;
        ClaritySlider.Value = 0;
        UsmSlider.Value = 0;
        DetailSlider.Value = 0;
        PostAaSlider.Value = 0;
        PostEdgeSlider.Value = 0;
        MotionBlurCombo.SelectedIndex = 0;
        DeShakeCheck.IsChecked = false;
        QualityCombo.SelectedIndex = 0;
        BitrateBox.Text = "";
        CodecCombo.SelectedIndex = 0;
        if (BitrateRow != null) BitrateRow.Visibility = Visibility.Collapsed;
        FormatCombo.SelectedIndex = 0;
        FastModeCheck.IsChecked = false;
        // 【平滑时间轴已从界面移除(2026-09-15 用户定调),固定为启用】重置不再需要管它 —— 没有控件可重置,
        // 处理侧恒传 smoothTimeline: true(见 RunBtn_Click 的快照变量)。
        // 补全剩余参数(真正"重置所有"):视频降噪/后处理杂色/抗锯齿/去频闪/VFR/去重智能/微动防线/静音
        DenoiseToggle.IsChecked = false;
        DenoiseStrongRadios.SelectedIndex = 0;
        if (DenoiseKindCombo != null) DenoiseKindCombo.SelectedIndex = 0;
        PostAaSlider.Value = 0;
        PostEdgeSlider.Value = 0;
        // （「可变帧率保护」面板已删:固定为自动 — 源是 VFR 就按真实时间戳排帧,没有可重置的开关）
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
        PostAaSlider.Value = 0;
        PostEdgeSlider.Value = 0;
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

    // （「转场阈值」的重置按钮与处理函数 ResetSceneBtn_Click 已随滑条一起删除:阈值是内置的 0.3,不需要重置）

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
        // 【序号迁移标记(2026-09-12 起,共两次调整)】视频超分模型下拉换过两次顺序:
        //   Rev1(09-12):general-x4v3 从序号 3 上移到 2(x4plus「超慢」对调);
        //   Rev2(09-13):general-x4v3 再上移到 1(x4plus-anime 对调),即 animevideov3 正下方。
        // 下拉存的是序号,老文件必须逐级换算;读到 Rev<2 就补做缺的那几步并写回 2 —— 只换一次,之后不再改动。
        public int ModelOrderRev { get; set; }
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
        // 【「转场识别」默认口径的**版本标记**(2026-09-15 定稿)】**不是**"要把 Scene 改成什么"的动作指令。
        // 当前口径(SceneDefaultPolicy Rev2):**默认关**,且升级**绝不改变用户已有的勾选状态**。
        // ⚠ 历史遗留提醒:Rev1 曾一度把默认改成"开"、并强制把老用户的 Scene=false 改成 true,该口径已被用户
        //   明确撤回(原话意思:转场识别要可开可关,不要替他决定)。所以这里的字段名与"迁移"二字容易让人误读成
        //   "会自动开" —— 实际 Migrate 的返回值**恒等于传入值**,工程里不存在任何把 Scene 置真的迁移路径。
        // 【它现在唯一的作用】版本标记:读到 SceneDefaultRev < AlhPro.Core.SceneDefaultPolicy.CurrentRev 时,
        // 只把 Rev 对齐到当前值并写回盘一次(免得每次启动都重写设置文件),Scene 本身一个字节都不动。
        // 见 MigrateSceneDefault(日志里明写"历史遗留值一律保留、不改用户勾选")。
        // 【向后兼容依据】老设置文件里**没有**这个字段 → System.Text.Json 不赋值 → 反序列化后取默认值 **0**
        // → 0 < CurrentRev 成立 → 正好触发**一次**Rev 对齐(仅对齐版本号)。
        // 新写出的文件一定带当前 Rev(见 SaveSettings/SavePresets 的"盖章"),所以不会反复迁移。
        // 禁止写死具体的 Rev 数字做判断,理由同 ModelOrderRev(写死 = Rev 一变就埋一颗定时炸弹)。
        public int SceneDefaultRev { get; set; }
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
        /// <summary>「边缘增强」强度 0-100(0=关)。实测依据见 VideoPostFilters.Build 注释;
        /// 默认按模型给(animevideov3 → 30,其它四支 → 60),用户手动调过则以用户为准。</summary>
        public int PostEdge { get; set; }
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
        /// <summary>【任务 S3 · 2026-09-15 起语义收窄】平滑时间轴:统一输出帧率并按场景切换对齐。
        /// **界面上的勾选框已移除,功能固定启用** —— 本字段只为两件事保留:
        ///   ① 反序列化兼容(老设置文件里有它,删字段会让旧文件多一个"多余属性",保留最省事);
        ///   ② 参数预设快照的字段不丢(预设=整套参数的快照,字段结构保持稳定)。
        /// 【读取时一律忽略】`ApplyVideoParams` 故意不读它(老文件里的 `false` 不再能把功能关掉);
        /// 【写盘时一律写 true】`CollectVideoParams` 恒写 true(见那里的注释),避免"删了控件却被老 false 关掉"。
        /// 处理侧由 `RunBtn_Click` 恒传 `smoothTimeline: true`。</summary>
        public bool SmoothTimeline { get; set; } = true;
        public bool Mute { get; set; }
        public bool VideoDenoiseOn { get; set; }
        public int VideoDenoiseStrong { get; set; }
        /// <summary>降噪方式:0=空间+时间结合(默认,兼容旧设置) 1=仅空间 nlmeans 2=仅时间 hqdn3d。</summary>
        public int DenoiseKind { get; set; }
    }

    private static string SettingsFile => ParaPaths.SettingsFile("video-settings.json");

    /// <summary>「转场识别」的**内置阈值**(0.30;滑块已于 2026-09-15 按用户裁决删除)。
    /// 它现在只有两个用途:① 作为"开关已打开"的载体传给处理端(`sceneThreshold` 非 null 即开启切点保护);
    /// ② 内置判据拿不到采样数据、回退到 ffmpeg `scene` 判据时的阈值。
    /// 与 `VideoSettings.SceneThr` 的默认值一致 —— 写盘时也写它,免得老文件里别的值反复被改写。</summary>
    private const double SceneThresholdBuiltIn = 0.3;

    // ---------- 参数预设 ----------
    /// <summary>一个视频参数预设:命名 + 保存时间 + 一套 VideoSettings 快照。上限 100 个。</summary>
    private sealed class VideoPreset
    {
        public string Name { get; set; } = "";
        public string SavedAt { get; set; } = "";
        public bool IsOfficial { get; set; }   // 官方预设(程序内置):悬停显示"官方"、不显示日期时间;用户预设=普通条目
        /// <summary>官方预设的"参数基线版本"。0 = 早期版本(没有这个字段时写入的老预设)。
        /// 用途:内置预设改了默认参数后,要在【下一次启动时把老的官方预设覆盖成新基线】——
        /// 否则老用户永远带着旧参数(例如已删除的去频闪/去杂色、或旧的一拍二去重档)。
        /// 只在 OfficialRev &lt; 当前基线 时才覆盖一次,之后用户自己的改动会被尊重,直到下次基线提升。</summary>
        public int OfficialRev { get; set; }
        public VideoSettings Params { get; set; } = new();
    }

    /// <summary>预设文件路径(%LOCALAPPDATA%\ALHPro\settings\video-presets.json)。</summary>
    private static string PresetFile => ParaPaths.SettingsFile("video-presets.json");

    /// <summary>视频超分模型下拉的**一次性序号迁移**。
    /// 【为什么必须做】这个下拉存的是**序号**:不迁移的话,老用户"存的 3 = 轻量模型"会静默变成
    /// "3 = 超慢(x4plus)"——1080p 实测 14.5 秒/帧、比 animevideov3 慢 17 倍,用户什么都没改却突然慢十几倍,
    /// 而界面上完全看不出异常(下拉里那一项就叫"通用")。
    /// 【为什么带标记】不能每次读都换:换完会写回文件,标记前进后就不再动 —— 否则每次启动来回横跳。
    /// 【序号映射本身已抽到 AlhPro.Core.VideoModelOrder】纯函数 + 单测(Rev0/1/2 → 3、幂等、越界兜底),
    /// 这里只负责读写设置字段;Rev 历史与每次换位的原因写在那个类的注释里。
    /// 【必须留日志】迁移一旦发生就**静默改了用户存下来的模型**(可能从"最快"变成"最慢"),
    /// 排查"为何突然慢十几倍"时唯一的线索就是这行日志;没发生迁移时**不写**(避免每次启动刷屏)。
    /// 三个调用点(设置加载 / 预设加载 / 预设导入)共用它,所以日志写在这里、不写在调用点。
    /// 返回 true 表示"做过改动"(调用方据此决定是否立即写回盘)。</summary>
    private static bool MigrateEsrganModelOrder(VideoSettings d)
    {
        if (d is null || d.ModelOrderRev >= AlhPro.Core.VideoModelOrder.CurrentRev) return false;
        int oldModel = d.UpEsrganModel, oldRev = d.ModelOrderRev;
        d.UpEsrganModel = AlhPro.Core.VideoModelOrder.Migrate(d.UpEsrganModel, d.ModelOrderRev, out int rev);
        d.ModelOrderRev = rev;
        AppLogger.Info($"[记忆] 超分模型序号已迁移(下拉顺序调整):序号 {oldModel} → {d.UpEsrganModel}、Rev {oldRev} → {rev}");
        return true;
    }

    /// <summary>「转场识别」的口径标记迁移(纯换算在 AlhPro.Core.SceneDefaultPolicy)。
    /// 【2026-09-15 用户最终定调 · 本方法的语义已反转】原先它会把老用户的 `Scene = false` **强制改成 true**
    /// (口径曾是"默认开,让老用户也吃到切点保护")。用户随后明确:**转场识别要可开可关,不要替他决定** ⇒
    /// 现在**只对齐版本号、绝不改用户的值**:升级前勾着的还是勾着、没勾的还是没勾(单测有契约钉住
    /// "工程里不存在任何把 Scene 置真的路径")。
    /// 【为什么还留 Rev】只当版本标记用:让"已按当前口径结算过"的文件不再被每次都改写一遍
    /// (没有它,每次启动都会重写一次设置文件)。Rev 只前进不回退。
    /// 【日志口径】只在版本真的前进时写一行,并且**明确写出"值没被改"**,免得日后有人看到这行
    /// 以为又是"偷偷打开"那种动作;没前进(含新用户无文件那条路)**不写**,避免每次启动刷屏。
    /// 返回 true 表示 Rev 前进过(调用方据此立刻写回盘)。</summary>
    private static bool MigrateSceneDefault(VideoSettings d)
    {
        if (d is null || d.SceneDefaultRev >= AlhPro.Core.SceneDefaultPolicy.CurrentRev) return false;
        bool oldScene = d.Scene;
        int oldRev = d.SceneDefaultRev;
        d.Scene = AlhPro.Core.SceneDefaultPolicy.Migrate(d.Scene, d.SceneDefaultRev, out int rev);
        d.SceneDefaultRev = rev;
        AppLogger.Info($"[记忆] 转场识别:口径标记已更新(默认关;历史遗留值一律保留、不改用户勾选)"
            + $"Scene {(oldScene ? "开" : "关")} → {(d.Scene ? "开" : "关")}、Rev {oldRev} → {rev}");
        return true;
    }

    /// <summary>预设文件"存在但读不出来"(写入被截断/手改坏)时的**只读保护**标志。
    /// 【为什么必须】原来解析失败就 catch { return new(); },紧接着 EnsureBuiltinPresets 看到"一条预设都没有"
    /// 就把内置预设写回去 —— 整份文件被覆盖,用户自己攒的预设**无声消失**(文件里的原内容就这么没了)。
    /// 现在:①先把坏文件留一份 .bak;②本次运行拒绝再写这个文件;③日志明确写出来。
    /// 宁可这一次预设列表是空的,也不覆盖掉唯一的那份数据。</summary>
    private static bool _presetFileUnreadable;

    /// <summary>预设文件损坏时的统一处置:备份 + 置只读保护 + 留痕(只做一次)。</summary>
    private static void ProtectCorruptPresetFile(string why)
    {
        if (_presetFileUnreadable) return;
        _presetFileUnreadable = true;
        try
        {
            var bak = PresetFile + ".bak";
            File.Copy(PresetFile, bak, true);
            AppLogger.Warn($"⚠ 视频预设文件读不出来({why})——已把它备份为 {Path.GetFileName(bak)},"
                + "本次运行不再写入该文件(避免把你原有预设覆盖掉);如需要可把 .bak 发来或重新导入");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"⚠ 视频预设文件读不出来({why}),且备份失败:{ex.Message.Split('\n')[0]}");
        }
    }

    /// <summary>读取全部预设(按创建时间排序;坏项跳过)。失败/空返回空列表。
    /// 【顺带迁移】预设里同样按序号存超分模型,所以读出来后要过一遍 MigrateEsrganModelOrder,
    /// 有改动就立刻写回(用户自建预设的"轻量模型"也要被正确换算,不能只迁移当前设置)。
    /// 【损坏保护】文件存在但解析失败时:备份成 .bak 并置只读保护(见 ProtectCorruptPresetFile / SavePresets)。</summary>
    private static List<VideoPreset> LoadPresets()
    {
        try
        {
            if (!File.Exists(PresetFile)) return new();
            var text = File.ReadAllText(PresetFile);
            if (string.IsNullOrWhiteSpace(text)) return new();   // 空文件=没有预设(不是损坏)
            var list = System.Text.Json.JsonSerializer.Deserialize<List<VideoPreset>>(text);
            if (list == null) { ProtectCorruptPresetFile("内容为 null"); return new(); }
            bool changed = false;
            foreach (var p in list)
                if (p?.Params != null && MigrateEsrganModelOrder(p.Params)) changed = true;
            if (changed) SavePresets(list);
            return list;
        }
        catch (Exception ex)
        {
            ProtectCorruptPresetFile(ex.Message.Split('\n')[0]);   // 文件损坏/无法反序列化 → 备份 + 只读保护(不崩溃、不覆盖)
            return new();
        }
    }

    /// <summary>把预设列表写盘。空列表则删除文件。
    /// 【盖章】写盘前给每个预设的 Params 盖上"新序号"标记:预设里的 VideoSettings 也是从界面收集来的
    /// (ModelOrderRev 默认 0),不盖章的话下次 LoadPresets 的迁移会把它当老文件再换一遍序号(来回横跳)。
    /// 【损坏保护】若本会话此前读这个文件失败过(_presetFileUnreadable),一律拒绝写入 —— 否则会把用户那份
    /// 读不出来但可能还能救的数据直接顶掉。</summary>
    private static void SavePresets(List<VideoPreset> list)
    {
        try
        {
            if (_presetFileUnreadable)
            {
                AppLogger.Warn("视频预设文件本次运行处于只读保护(先前读不出来),已跳过这次写入;重启软件后若文件已修好会恢复正常");
                return;
            }
            if (list.Count == 0) { if (File.Exists(PresetFile)) File.Delete(PresetFile); return; }
            // 盖章用当前 Rev(不能写死数字,理由同 SaveSettings 里那段:Rev 一变,写死的值会让新序号被再迁移一次)
            // 【SceneDefaultRev 一起盖】预设的快照里也有 Scene;预设**刻意不迁移**它(预设是用户主动保存的
            // 显式快照,不该被"默认口径"改动 —— 用户存的是勾着的,应用时就该是勾着的)。盖章只是把它钉成
            // "按当前口径写入"的版本标记,免得将来有人给它加迁移时误伤(本版迁移不改值,盖不盖都不影响结果)。
            foreach (var p in list) if (p?.Params != null)
            {
                p.Params.ModelOrderRev = AlhPro.Core.VideoModelOrder.CurrentRev;
                p.Params.SceneDefaultRev = AlhPro.Core.SceneDefaultPolicy.CurrentRev;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(PresetFile)!);
            File.WriteAllText(PresetFile, System.Text.Json.JsonSerializer.Serialize(list));
        }
        catch { }
    }

    private const int MaxPresets = 100;   // 上限 100 个预设

    /// <summary>官方内置预设定义(名字 + 参数基线版本 Rev + 一套默认参数)。
    /// 以后要加官方预设,在这里加一项即可,下次启动自动带上。
    /// 【Rev 怎么用】官方预设的参数基线一旦改动(增删项、调默认值),就把该项的 Rev 加 1 ——
    /// 下次启动时 EnsureBuiltinPresets 会把老用户的同名官方预设覆盖成新基线(只覆盖一次),
    /// 不然老用户永远带着旧参数。**只改这一项的 Rev,不要动其它项**,否则会连带覆盖用户对它们的自定义。
    /// 约定:Rev = 0 表示"早期版本写入的老预设"(那时还没有 OfficialRev 字段)。
    /// 【Rev 1 / Rev 2:超分引擎由 waifu2x 换成 Real-ESRGAN · realesr-animevideov3】
    /// 依据(2026-09-11 本机 4060 Laptop 实测,源 1920×1080 → 2x=4K,每帧):
    ///   realesrgan-x4plus 14.48s · realesrgan-x4plus-anime 4.84s · **realesr-animevideov3 0.84s** · waifu2x cunet 0.71s。
    /// 前两者都慢在"4x-only 权重":要 2x 也照样全量算 4x 再缩回(实测 -s 2 = 17.5s vs -s 4 = 18.1s,输出分别
    /// 3840×2160 与 7680×4320 —— **输出尺寸是对的,白付的是算力**)。animevideov3 同为 4x 权重但网络极轻,
    /// 同样的 2x=4K 输出快 17 倍。取舍:它是动漫向模型,写实素材若觉得不如 x4plus 可手动换回(或折中用
    /// realesrgan-x4plus-anime,约为 x4plus 的 3 倍速)。</summary>
    private static (string Name, int Rev, Func<VideoSettings> Make)[] BuiltinPresets() => new[]
    {
        // 【Rev 2 · 2026-09】后处理全部重做后同步预设值(每一档换了机制,见 VideoService.BuildPostFilter):
        //   钝化蒙版给最多(唯一实测不伤边缘结构的锐化档:边缘 SSIM 反升),保留细节用 CAS(全档最干净),
        //   边缘抗锯齿给到能真正生效的强度(旧参数实测空转),去模糊整项移除(视频侧 ffmpeg 无反卷积)。
        // 【Rev 3 · 2026-09】这个预设名字是「通用」,但原模型是动漫向的 animevideov3 —— 语义不符。
        // 现在有了自转的轻量通用模型 realesr-general-x4v3(4.85MB / 960x540→4K 1.7s / PSNR 33.13),
        // 它才是"通用"的正解,故本预设改用 it。
        // 【Rev 4 · 2026-09-12】下拉顺序调整:general-x4v3 上移到「超慢」之前(序号 3 → 2)。
        // ⚠ 这里必须跟着改成 2,并且**【必须提 Rev】** —— 否则老用户机器上那份 Rev3 的官方预设不会被刷新,
        //   它记的还是旧序号 3,而新列表里 3 已经是「超慢」(x4plus,慢 17 倍):用户点一下这个预设就突然变超慢。
        // 【Rev 5 · 2026-09-13】按用户要求:超分模型改用「动漫通用」那支 realesr-animevideov3(序号 0)。
        //   直接诱因:摘要里印着「通用·general-x4v3(轻量)」,用户看到"轻量"就认为这支不行 —— 那是 v1.3.5
        //   改名时漏改的旧标签(已在 UpEsrganModelNames 改成「(中)」),但模型本身照用户要求换掉。
        //   这一条与 Rev 3 的取舍相反(Rev 3 以"预设名叫「通用」就该配通用模型"为由换成 general-x4v3),
        //   本次以用户偏好为准。若要退回:把下面 UpEsrganModel 改回 2 并**再提一次 Rev**,不提则老机器不刷新。
        // 【Rev 6 · 2026-09-13】按用户要求:官方预设的「边缘抗锯齿」一律清零(PostAa 45 → 0)。
        //   理由:该档是当前唯一"纯为观感"的高成本项 —— 它跑在最终最大分辨率上(4x = 4320p),
        //   每帧要做"解码 → 逐像素 3×3 边缘平滑 → 重编码 JPG",4K 单帧约 1.19 秒(并行后约 0.1 秒/帧),
        //   用户实测长素材上这一步占掉可观时间。默认不再替用户开,想开的人在界面里自己拉这条滑条
        //   (「边缘抗锯齿」滑条与全部实现都保留,只是不再默认给 45)。
        //   【必须提 Rev · 与 Rev 4 同理】EnsureBuiltinPresets 只在 OfficialRev < rev 时才覆盖老用户的
        //   同名官方预设;不提 Rev,老机器上那份 Rev5 预设会继续带着 PostAa=45 生效,用户改了也白改。
        //   只提这两个真的改了 PostAa 的预设(通用画质增强/anime通用);「去重补帧4x」的 PostAa 本来就是 0,
        //   没改就不提 Rev —— 提了只会白白覆盖用户对它其它参数的自定义(见 EnsureBuiltinPresets 注释)。
        //   【口径收窄(用户 2026-09-13 明确)】只清「边缘抗锯齿」这一项 ——
        //   锐化 20 / 清晰 25 / 钝化蒙版 35 / 保留细节 40【全部保持原值,不许顺手清零】:
        //   用户说的"导出视频自带去雾"的观感来源正是清晰/锐化这类后处理(视频页本来就没有去雾功能,
        //   去雾只存在于图片页),那几档要保留;真正要去掉的只有最后那一项边缘抗锯齿。
        // 【Rev 7 · 2026-09-15】「转场识别」默认由关改开:预设里的 Scene 也从 false 跟着改成 true。
        //   【必须提 Rev · 与 Rev 4/6 同理】预设存的是"一整套参数快照",里面就带着 Scene=false;
        //   不提 Rev 的话,老机器上那份 Rev6 的老快照会在用户**点一下这个预设**时把刚打开的切点保护又关掉
        //   (而且界面上只是那个勾自己消失,用户不会意识到"点了预设 = 关掉了防鬼影")。
        //   代价照实说:这次覆盖会把用户对这**三个官方预设**的其它自定义一并重置(用户自建的预设一律不碰,
        //   见 EnsureBuiltinPresets 的规则)—— 与 Rev4/Rev6 那次同样的取舍,为的是不让预设把新默认悄悄推翻。
        // 【Rev 退回 6 · 2026-09-15 当天定稿】上面那条(Rev7 = Scene 改 true)随用户"转场识别默认关、要可开可关"
        //   的定调**整体撤销**:Scene 改回 false,Rev 也**退回到 6**。为什么退而不是再提到 8:
        //   ① 基线本身回到了 Rev6 的内容(Scene=false),再提 Rev 只会**第二次**白白覆盖用户对官方预设的自定义;
        //   ② 退回后,用户文件里那份 Rev6 的官方预设 `OfficialRev < rev` 不成立 → **不会被覆盖**,它本来存的
        //      就是 Scene=false,与新的定义逐字一致 ⇒ 方向怎么变都不会"点一下预设莫名打开/关掉转场识别";
        //   ③ 只有比 Rev6 更老(Rev5 及以前)的官方预设会按老规则被刷新一次到新基线,这正是原有设计意图。
        //   (注:Rev7 那份定义**从未发布**(未打包未部署),所以不存在"用户文件里已经是 Rev7"的机器。)
        ( "通用画质增强 不含补帧", 6, new Func<VideoSettings>(() => new VideoSettings
        {
            Remember = false, Up = true, Engine = 1, Scale = 1, Gpu = 0,
            Interp = false, Model = 0, UpWaifu2xModel = 0, UpEsrganModel = 0, InterpScale = 0,
            Target = false, TargetFps = "", VfrMode = 0, VfrExpanded = false, FpsBase = 0, FpsMode = 0, FpsOffset = 0, FpsExpanded = true,
            DedupOn = false, DedupModel = 0, DedupAnime = 0, DedupSmart = 0, DedupThr = 0.01,
            Scene = false, SceneThr = 0.3, TimeStep = 0.5, Tta = false, OutDir = "", CustomW = "1920", CustomH = "1080",
            DedupAlgo = 0, DedupHi = 12, DedupLo = 5, DedupFrac = 0.33, DedupSadThr = 3, DedupSsimThr = 0.97, ContentFps = 0,
            DedupMotionComp = true, DedupOnlyTrueHold = true, ManualProtectSmallMotion = true, DedupPhaseAlign = true,
            PostSharpen = 20, PostClarity = 25, PostUsm = 35, PostDetail = 40, PostDeblur = 0, PostAa = 0,
            Jello = 0, MotionBlur = 0, DeShake = false, Quality = 0, BitrateMbps = 0, Codec = 0, Format = 0,
            FastMode = false, Mute = false, VideoDenoiseOn = false, VideoDenoiseStrong = -1, DenoiseKind = 0,
        })),
        // 【Rev 1】去重由「智能 + 去除一拍二」改为「动漫模式 + 去除一拍四」。
        // 理由:动漫素材绝大多数是一拍二/一拍三,而"去除一拍四"才是把"一拍四的片子"还原成内容帧率的正解;
        // 用智能模式则依赖拍数识别,识别不出就原样保留(等于没去重)。改档后按内容帧率均匀采样,不会误删细节帧。
        // 同时清理已删除的 PostFlicker / PostDenoise(去频闪/去杂色两项已从管线移除,不再被读取)。
        // 【Rev 2】超分引擎同样换成 Real-ESRGAN · realesr-animevideov3(理由见上,实测 17 倍速、输出仍是 2x=4K)。
        // 【Rev 3 · 2026-09】后处理重做后同步:钝化蒙版 40(实测唯一不伤边缘的锐化档,动漫线条收益最大)、
        //   保留细节 40(CAS)、边缘抗锯齿 45(新参数才真的在削锯齿)、去模糊归零(视频侧已移除)、锐化收到 20。
        // 【Rev 4 · 2026-09】降噪方式入选预设:结合模式(空间+时间)已证实优于单一方式
        // (仅时间擦不掉单帧噪点:实测把 hqdn3d 空间参数翻倍,平坦噪点 0.77→0.77 无变化)。
        // 【Rev 5 · 2026-09-13】降噪改为默认【关闭】(用户反馈"降噪感太强、发假、塑料感、没有棱角")。
        // 实测依据(同一动漫帧 / 1080p+JPEG q35 输入 / 输出归一到 2160p 与 8K 真值比,棱角=真值强边缘上的平均梯度):
        //   不降噪 + animevideov3 : 棱角 78.3 / detail 36.2
        //   降噪(结合·弱)+ 同模型 : 棱角 71.7 / detail 33.1   ← 弱档就削掉 8.4% 棱角
        //   降噪(结合·中)+ x4plus-anime : 棱角 84.9(不降噪同模型 90.7)→ 削 6.4%
        // 结论:降噪对本预设的目标素材(动漫)是"净损棱角"的一步,故默认关掉;压缩严重/噪点明显的素材
        //       用户可自行在「视频降噪」里打开(开关与三档、三种方式都保留,只是不再默认替用户开)。
        // 【Rev 6 · 2026-09-13】按用户要求:「边缘抗锯齿」清零(45 → 0),与本文件另一个官方预设同批改。
        //   这一档在 4K/4320p 上每帧"解码 → 逐像素 3×3 边缘平滑 → 重编码 JPG",是纯观感项、又是最贵的一步;
        //   默认不再替用户开,想开的人自己拉滑条(实现与滑条都保留)。
        //   【必须提 Rev】不提则老机器上的 Rev5 预设继续按 PostAa=45 跑(见 EnsureBuiltinPresets)。
        //   【口径收窄】只清 AA:锐化 20 / 清晰 25 / 钝化蒙版 40 / 保留细节 40 保持原值(用户明确要保留)。
        // 【Rev 7 → 退回 6 · 2026-09-15 当天定稿】同上面那个预设:Scene 改回 false(「转场识别」最终定为默认关、
        //   由用户自己开关),Rev 一并退回 6 —— 退回的完整理由见上一个预设处那段(核心:基线回到 Rev6 的内容,
        //   退回就不会二次覆盖用户对官方预设的自定义,也不会让"点预设"改变转场识别的勾选状态)。
        ( "动漫通用", 6, new Func<VideoSettings>(() => new VideoSettings
        {
            Remember = true, Up = true, Engine = 1, Scale = 1, Gpu = 0,
            Interp = true, Model = 0, UpWaifu2xModel = 1, UpEsrganModel = 0, InterpScale = 2,
            Target = false, TargetFps = "", VfrMode = 0, VfrExpanded = false, FpsBase = 0, FpsMode = 0, FpsOffset = 0, FpsExpanded = true,
            DedupOn = true, DedupModel = 1, DedupAnime = 4, DedupSmart = 0, DedupThr = 0.01,
            Scene = false, SceneThr = 0.3, TimeStep = 0.5, Tta = false, OutDir = "", CustomW = "1920", CustomH = "1080",
            DedupAlgo = 3, DedupHi = 12, DedupLo = 5, DedupFrac = 0.33, DedupSadThr = 3, DedupSsimThr = 0.97, ContentFps = 0,
            DedupMotionComp = true, DedupOnlyTrueHold = true, ManualProtectSmallMotion = true, DedupPhaseAlign = true,
            PostSharpen = 20, PostClarity = 25, PostUsm = 40, PostDetail = 40, PostDeblur = 0, PostAa = 0,
            Jello = 0, MotionBlur = 0, DeShake = false, Quality = 0, BitrateMbps = 0, Codec = 0, Format = 0,
            FastMode = false, Mute = false, VideoDenoiseOn = false, VideoDenoiseStrong = -1, DenoiseKind = 0,
        })),
        // 【Rev 保持 1 · 2026-09-13 核过】这个预设的「边缘抗锯齿」本来就是 0(PostAa = 0,整组后处理全 0),
        //   本次"官方预设 AA 一律清零"对它【没有任何改动】,故【刻意不提 Rev】:
        //   提 Rev 只会把用户对它其它参数的自定义整份覆盖掉(EnsureBuiltinPresets 的覆盖规则),得不偿失。
        // 【Rev 2 → 退回 1 · 2026-09-15 当天定稿】当时为了"转场识别默认改开"提过一次 Rev(1 → 2);
        //   该口径已被用户撤回(默认关、可开可关)⇒ Scene 改回 false、Rev 退回 1,理由同上一个预设处那段。
        ( "去重补帧4x", 1, new Func<VideoSettings>(() => new VideoSettings
        {
            Remember = true, Up = false, Engine = 0, Scale = 1, Gpu = 0,
            Interp = true, Model = 0, UpWaifu2xModel = 0, UpEsrganModel = 0, InterpScale = 2,
            Target = false, TargetFps = "", VfrMode = 0, VfrExpanded = false, FpsBase = 0, FpsMode = 0, FpsOffset = 0, FpsExpanded = false,
            DedupOn = true, DedupModel = 0, DedupAnime = 0, DedupSmart = 1, DedupThr = 0.01,
            Scene = false, SceneThr = 0.3, TimeStep = 0.5, Tta = false, OutDir = "", CustomW = "1920", CustomH = "1080",
            DedupAlgo = 3, DedupHi = 12, DedupLo = 5, DedupFrac = 0.33, DedupSadThr = 3, DedupSsimThr = 0.97, ContentFps = 0,
            DedupMotionComp = true, DedupOnlyTrueHold = true, ManualProtectSmallMotion = true, DedupPhaseAlign = true,
            PostSharpen = 0, PostClarity = 0, PostUsm = 0, PostDetail = 0, PostDeblur = 0, PostAa = 0,
            Jello = 0, MotionBlur = 0, DeShake = false, Quality = 0, BitrateMbps = 0, Codec = 0, Format = 0,
            FastMode = false, Mute = false, VideoDenoiseOn = false, VideoDenoiseStrong = -1, DenoiseKind = 0,
        })),
    };

    /// <summary>确保每个官方内置预设存在,并把【参数基线过旧】的官方预设更新到新基线。
    /// 规则:
    ///   · 缺失 → 用官方默认创建(标记官方 + 记下当前 Rev)。
    ///   · 同名但本来不是官方 → 只标记为官方,【不动参数】(用户自己攒的同名预设保留原样)。
    ///   · 同名、是官方、且 OfficialRev &lt; 当前 Rev → 用新基线【覆盖参数】并记下新 Rev(只覆盖这一次)。
    ///     这条是刻意为之:老用户手里的"老动漫通用"必须被更新,否则永远带着旧档位(如旧的"智能+一拍二"去重)。
    ///   · 用户自建的其它预设(名字不同)一律不碰、不删、不改参数。
    /// 以后想再更新某项默认参数:把 BuiltinPresets() 里【那一项】的 Rev 加 1 即可 —— 只加那一项,
    /// 否则会连带覆盖用户对其它官方预设的自定义。</summary>
    private void EnsureBuiltinPresets()
    {
        try
        {
            var list = LoadPresets();
            bool changed = false;
            int updated = 0;
            foreach (var (name, rev, make) in BuiltinPresets())
            {
                var existing = list.FirstOrDefault(x => x.Name == name);
                if (existing == null)
                {
                    // 缺失 → 用官方默认参数创建,标记官方,排在已有预设之前(官方靠前)
                    list.Insert(0, new VideoPreset
                    {
                        Name = name,
                        SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm") + " · 内置",
                        IsOfficial = true,
                        OfficialRev = rev,
                        Params = make(),
                    });
                    changed = true;
                    AppLogger.Info($"[内置预设] 已创建官方预设「{name}」(基线 Rev {rev})");
                    continue;
                }
                if (!existing.IsOfficial)
                {
                    // 【规则见本方法上方注释:同名但不是官方 → 只标记,不动参数】
                    // 【为什么必须 continue】原实现只把 IsOfficial 置 true 就往下走,而紧接着的判断是
                    // `OfficialRev < rev` —— 用户自建预设的 OfficialRev 一定是 0,于是**条件必然成立**,
                    // 用户攒的那份参数被官方基线整份覆盖,同时被盖上 [官方] 标记;而日志还写着
                    // "用户自建预设未动"(实际动了)。2026-09-12 自检发现。
                    // 现在:标记为官方 + 把 Rev 补到当前值(表示"已按当前基线结算过,以后别再覆盖"),
                    // 然后 continue —— 参数一个字段都不动。
                    existing.IsOfficial = true;
                    existing.OfficialRev = rev;
                    changed = true;
                    AppLogger.Info($"[内置预设] 「{name}」是官方预设名,但这条是你自建的:已标记为官方," +
                        "参数保持你自己那套(不覆盖、不重置)");
                    continue;
                }
                if (existing.OfficialRev < rev)
                {
                    existing.Params = make();
                    existing.OfficialRev = rev;
                    changed = true;
                    updated++;
                    AppLogger.Info($"[内置预设] 已把官方预设「{name}」更新到新基线(Rev {rev}):"
                        + "该预设参数已按新版重置;其余官方预设与你自建的预设未动");
                }
            }
            if (changed)
            {
                SavePresets(list);
                AppLogger.Info($"[内置预设] 官方预设检查完成(缺失已补 / 同名已标记官方 / 基线过旧已更新 {updated} 项;用户自建预设未动)");
            }
        }
        catch { }
    }

    /// <summary>【2026-09-15 · 界面清理的兼容日志】老设置/老预设里可能带着**已被删除的选项**的旧值
    /// (`VfrMode=1` 不启用、`FpsBase=1` 匀速、`SmoothTimeline=false`、`SceneThr≠0.3`、`VfrExpanded=true`)。
    /// 这些值现在一律**按内建口径处理**(不采纳),所以只在"旧值与内建值不一致"时写一行 `[记忆] …`,
    /// 把"哪几项被忽略了"说清楚 —— 排查"我明明设过怎么没生效"时唯一的线索就是这行。
    /// **不写不发生**(值本来就是内建口径时不刷屏),**不需要新 Rev**(语义是"永久内建",写回内建值即幂等)。
    /// 返回 true 表示写过日志(供 QA 工具断言)。</summary>
    private static bool WarnIfLegacyTimelineOptions(VideoSettings d)
    {
        if (d is null) return false;
        var items = new System.Collections.Generic.List<string>();
        if (d.VfrMode != 0) items.Add($"可变帧率保护={d.VfrMode}(0=自动)");
        if (d.VfrExpanded) items.Add("可变帧率保护面板=展开");
        if (!d.SmoothTimeline) items.Add("平滑时间轴=关");
        if (d.FpsBase != 0) items.Add($"输出帧率基准={d.FpsBase}(0=真实时间轴)");
        if (Math.Abs(d.SceneThr - SceneThresholdBuiltIn) > 1e-9) items.Add($"转场阈值={d.SceneThr:0.###}(内置 {SceneThresholdBuiltIn:0.00})");
        if (items.Count == 0) return false;
        AppLogger.Info($"[记忆] 设置里有已删除的选项,已按内建口径忽略(这些选项现在固定/不需要用户设置):"
            + string.Join("、", items));
        return true;
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsFile))
            {
                _settingsLoaded = true;   // 首次使用(无文件):放行保存,否则永远记不住
                AppLogger.Warn("[记忆] 视频设置加载: 文件不存在(首次使用),放行保存");
                return;
            }
            var d = System.Text.Json.JsonSerializer.Deserialize<VideoSettings>(File.ReadAllText(SettingsFile));
            if (d is null) { _settingsLoaded = true; AppLogger.Warn("[记忆] 视频设置加载: 反序列化返回 null,放弃恢复"); return; }
            // 【界面清理的兼容日志】老设置里若带着"已被删掉的选项"的旧值,这里说清楚它们被忽略了(不写不发生)
            WarnIfLegacyTimelineOptions(d);
            // 超分模型下拉改过顺序:先把老序号换算成新序号,并立刻写回(只做一次,见 MigrateEsrganModelOrder)
            // 「转场识别」默认口径由关改开:同样是**一次性**迁移 + 立刻写回(见 MigrateSceneDefault)。
            // 【必须在 ApplyVideoParams 之前】下面恢复界面时会用 d.Scene 覆盖 SceneCheck,
            // 迁移得先改完 d.Scene,否则界面恢复的还是老值(false)。
            bool migrated = MigrateEsrganModelOrder(d);
            if (MigrateSceneDefault(d)) migrated = true;
            if (migrated)
            {
                // 迁移日志已在各自方法里统一写过(含前后值/前后 Rev),此处只负责立刻写回盘
                try { File.WriteAllText(SettingsFile, System.Text.Json.JsonSerializer.Serialize(d)); } catch { }
            }
            AppLogger.Info($"[记忆] 视频设置加载: Remember={d.Remember}, Up={d.Up}, Engine={d.Engine}, Scale={d.Scale}, Model={d.Model}, InterpScale={d.InterpScale}, Quality={d.Quality}, Format={d.Format}, Codec={d.Codec}, 文件时间={File.GetLastWriteTime(SettingsFile):HH:mm:ss}");
            _suppressEvents = true;
            VideoRememberCheck.IsChecked = d.Remember;
            if (d.Remember)
            {
                try
                {
                    ApplyVideoParams(d);
                    AppLogger.Info($"[记忆] 视频设置恢复完成: 界面 Engine={VideoEngineRadios.SelectedIndex}, Scale={VideoScaleRadios.SelectedIndex}, Model={InterpModelCombo.SelectedIndex}, Quality={QualityCombo.SelectedIndex}, Format={FormatCombo.SelectedIndex}, Codec={CodecCombo.SelectedIndex}");
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"[记忆] ApplyVideoParams 恢复中断: {ex.Message} | 堆栈 {ex.StackTrace}");
                }
            }
            else
            {
                AppLogger.Warn("[记忆] 视频设置 Remember=False,未恢复任何参数");
            }
            _suppressEvents = false;
            _settingsLoaded = true;   // 加载完成,此后才允许保存(防构造/加载期 -1 污染)
            UpdateOptions();   // 恢复后刷新 UI 状态(自定义分辨率面板显隐/提示/滑条数值等)
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[记忆] 视频设置加载异常: {ex.Message} | {ex.StackTrace}");
            _suppressEvents = false;
            _settingsLoaded = true;
        }
    }

    /// <summary>把一份 VideoSettings 快照应用到当前页面 UI(校验范围后赋值,避免越界)。
    /// 供「记住上次参数加载」(d.Remember)与「应用参数预设」共用。
    /// 调用方负责用 _suppressEvents 抑制事件回写(避免应用过程中触发 SaveSettings)。</summary>
    private void ApplyVideoParams(VideoSettings d)
    {
        UpscaleToggle.IsChecked = d.Up;
        // 兼容旧设置:存盘沿用旧约定 0=waifu2x / 1=Real-ESRGAN / 2=Real-CUGAN(已移除,归到 Real-ESRGAN)。
        // 界面顺序已改成 Real-ESRGAN 在上(索引 0)、waifu2x 在下(索引 1),所以要经 EngineFromStored 换算,
        // 否则老用户存的选择会被顺序调整翻转。
        if (d.Engine is >= 0 and <= 2) VideoEngineRadios.SelectedIndex = EngineFromStored(d.Engine);
        // 放大倍数索引已去掉「自定义分辨率」(4),旧设置里的 4 归到 2x(索引1),其余 0~3 照搬
        if (d.Scale is >= 0 and <= 3) VideoScaleRadios.SelectedIndex = d.Scale;
        else if (d.Scale == 4) VideoScaleRadios.SelectedIndex = 1;   // 旧「自定义分辨率」→ 2x
        if (!string.IsNullOrWhiteSpace(d.CustomW)) CustomWidthBox.Text = d.CustomW;
        if (!string.IsNullOrWhiteSpace(d.CustomH)) CustomHeightBox.Text = d.CustomH;
        if (d.PostSharpen is >= 0 and <= 100) SharpenSlider.Value = d.PostSharpen;
        if (d.PostClarity is >= 0 and <= 100) ClaritySlider.Value = d.PostClarity;
        if (d.PostUsm is >= 0 and <= 100) UsmSlider.Value = d.PostUsm;
        if (d.PostDetail is >= 0 and <= 100) DetailSlider.Value = d.PostDetail;
        // 【已移除 去模糊】视频页没有该滑杆了(ffmpeg 无反卷积,那档名不副实);旧设置里的值忽略即可。
        if (d.PostAa is >= 0 and <= 100) PostAaSlider.Value = d.PostAa;
        if (d.PostEdge is >= 0 and <= 100) PostEdgeSlider.Value = d.PostEdge;
        if (d.MotionBlur is >= 0 and <= 3) MotionBlurCombo.SelectedIndex = d.MotionBlur;
        DeShakeCheck.IsChecked = d.DeShake;
        if (d.Quality is >= 0 and <= 5) QualityCombo.SelectedIndex = d.Quality;
        if (d.BitrateMbps > 0) BitrateBox.Text = d.BitrateMbps.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        if (d.Codec is >= 0 and <= 1) CodecCombo.SelectedIndex = d.Codec;
        if (d.Format is 0 or 1) FormatCombo.SelectedIndex = d.Format;
        FastModeCheck.IsChecked = d.FastMode;
        // 【平滑时间轴:控件已移除(2026-09-15 用户定调)】**故意忽略 d.SmoothTimeline** ——
        // 老设置文件里存的 `false` 不再能把功能关掉:该功能固定启用(处理侧恒传 smoothTimeline: true)。
        // 字段本身保留在 VideoSettings 里(反序列化兼容、预设快照不丢字段),但读取时一律不采纳;
        // 写盘时由 CollectVideoParams 按"启用"盖章,避免"老 false 又把它关掉"。
        MuteCheck.IsChecked = d.Mute;
        DenoiseToggle.IsChecked = d.VideoDenoiseOn;
        if (d.VideoDenoiseStrong is >= 0 and <= 2) DenoiseStrongRadios.SelectedIndex = d.VideoDenoiseStrong;
        if (d.DenoiseKind is >= 0 and <= 2 && DenoiseKindCombo != null) DenoiseKindCombo.SelectedIndex = d.DenoiseKind;
        InterpToggle.IsChecked = d.Interp;
        if (d.Model is >= 0 && d.Model < InterpModelCombo.Items.Count) InterpModelCombo.SelectedIndex = d.Model;
        // 恢复超分模型(waifu2x / Real-ESRGAN,各自按引擎下拉索引,越界回退 0)
        if (VideoWaifu2xModelCombo.Items.Count > 0 && d.UpWaifu2xModel is >= 0 && d.UpWaifu2xModel < VideoWaifu2xModelCombo.Items.Count)
            VideoWaifu2xModelCombo.SelectedIndex = d.UpWaifu2xModel;
        else VideoWaifu2xModelCombo.SelectedIndex = 0;
        if (VideoEsrganModelCombo.Items.Count > 0 && d.UpEsrganModel is >= 0 && d.UpEsrganModel < VideoEsrganModelCombo.Items.Count)
            VideoEsrganModelCombo.SelectedIndex = d.UpEsrganModel;
        else VideoEsrganModelCombo.SelectedIndex = 0;
        // 【顺序有讲究:必须先落 `_lastScaleIndex`,再动下拉选中项】
        // 若这里直接 `InterpScaleRadios.SelectedIndex = d.InterpScale`,紧接着 `SetTargetFpsSelection` 把选中项
        // 切到「指定帧率」(6),而 `UpdateOptions()` 里那句"记下用户选的真实倍率(选中 6 时不覆盖)"就变成
        // "一个都不记" ⇒ `_lastScaleIndex` 停在初值 0。后果有两处,都是静默的:
        //   ① 重启后点回任一倍率(或换回不支持指定帧率的老模型)= 高倍率设置被悄悄降成 2x;
        //   ② `CollectVideoParams()` 用 `CurrentScaleIndex()` 存档 ⇒ 下次写盘把 2x 写进预设/记忆参数里。
        // 所以:先赋值 `_lastScaleIndex`(它就是"上一个真实倍率",与控件选中项解耦),再让选中项去驱动界面联动。
        if (d.InterpScale is >= 0 and <= 5)   // 补帧倍率 0~5=2x/3x/4x/8x/12x/16x(旧只<=3,漏了12x/16x导致选高倍率重开回默认2x)
        {
            _lastScaleIndex = d.InterpScale;
            InterpScaleRadios.SelectedIndex = d.InterpScale;
        }
        // 指定输出帧率:老设置里存的是"是否指定 + 那个数",回填到下拉(能对上屏幕刷新率/预设档就选那一档)
        SetTargetFpsSelection(d.Target, d.TargetFps);
        // 【2026-09-15 起:以下三个"已被删掉的选项"一律**忽略**老设置里的值】
        //   · d.VfrMode / d.VfrExpanded —— 「可变帧率保护」面板已删,固定为"自动";
        //   · d.FpsBase —— 「补帧输出帧率基准」已删,固定为"真实时间轴";
        //   · d.SmoothTimeline —— 「平滑时间轴」勾选框已删(见上面那行注释),固定为开;
        //   · d.SceneThr —— 「转场阈值」滑块已删,阈值用内置常量 SceneThresholdBuiltIn。
        // 字段本身保留(反序列化兼容);写盘时由 CollectVideoParams 写回内建值 ⇒ 老文件里的旧值不会被反复改写,
        // 也不会让"删了控件却没删功能"这类事故发生。有旧值时写一行 [记忆] 日志(见 WarnIfLegacyTimelineOptions)。
        if (d.FpsMode is 0 or 1 or 2) FpsModeRadios.SelectedIndex = d.FpsMode;
        if (d.FpsOffset is >= -20 and <= 0) FpsOffsetSlider.Value = d.FpsOffset;
        FpsPanel.Visibility = d.FpsExpanded ? Visibility.Visible : Visibility.Collapsed;
        FpsToggleBtn.Content = d.FpsExpanded ? "视频帧率 ▴" : "视频帧率 ▾";
        DedupCheck.IsChecked = d.DedupOn;   // 预设里去重开关,勾/不勾都要设置(否则关的预设不会取消勾选)
        // 【上界必须与下拉项数一致(2026-09-12 自检)】去重模式下拉只有 3 项(0智能/1动漫/2手动)、
        // 动漫档位只有 5 项。原来这里写的是 <=5 / <=6:老文件或别的预设里只要出现 3~5(早期版本确实有过更多档),
        // 就会给 ComboBox 赋一个越界下标 → WinUI 把 SelectedIndex 清成 -1 → 保存时写 -1、
        // 或运行时 `SelectedIndex + 1 = 0` 表示"去重关闭" → **用户勾着去重却被静默关掉**。
        // 越界值一律回退到安全档(模式=智能检测 0 / 动漫档=一拍二 0),不再静默关功能。
        if (d.DedupModel is >= 0 and <= 2)
            DedupModelCombo.SelectedIndex = d.DedupModel;   // 预设存当前去重模式索引,直接赋
        else
            DedupModelCombo.SelectedIndex = 0;
        if (d.DedupAnime is >= 0 and <= 4)
            DedupAnimeCombo.SelectedIndex = d.DedupAnime;   // 预设存当前档位索引,直接赋
        else
            DedupAnimeCombo.SelectedIndex = 0;
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
        // 【空值必须显式清空(2026-09-12 自检)】原来只有"值合法"时才赋值,快照里是 0/空就不管 →
        // 界面上**上一个预设(或手填)的残留值会渗进这一次运行**。内容帧率这一项尤其致命:
        // 手动-内容帧率采样算法读的就是这个输入框,残留一个旧值 = 按错误的帧率抽帧,画面被抽稀,
        // 而界面上看不出任何异常。现在:合法就填,非法/为空一律清空(回到"待填写"语义)。
        if (d.ContentFps is >= 1 and <= 120)
            ContentFpsBox.Text = d.ContentFps.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        else
            ContentFpsBox.Text = "";
        if (d.DedupThr is >= 0.001 and <= 0.5) DedupSceneSlider.Value = d.DedupThr;
        // 【d.Scene = 用户自己存的值,原样采纳】迁移(MigrateSceneDefault)只对齐版本号、**不改用户的勾选** ——
        // 升级前是勾的就还是勾的、没勾的就还是没勾的(默认值是 false,只作用于"没有历史值"的新用户/重置)。
        SceneCheck.IsChecked = d.Scene;
        // （转场阈值滑条已删 ⇒ d.SceneThr 忽略;阈值固定用内置常量 SceneThresholdBuiltIn = 0.3)
        // 时间步:旧/非法设置回退默认 0.5(滑条最小 0.05,字段缺失会停在 0.05,与默认/重置 0.5 不一致)。
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
            var cur = SortedPresets(sortCombo.SelectedIndex);
            exportChecks.Clear();   // 每次重建都清空勾选记录(会随行重建重新填充）
            switch (sortCombo.SelectedIndex)
            {
                case 1: cur = cur.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList(); break;
                case 2: cur = cur.OrderByDescending(x => x.SavedAt, StringComparer.OrdinalIgnoreCase).ToList(); break;
            }
            listView.Items.Clear();
            for (int i = 0; i < cur.Count; i++)            {
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
            // 【必须取"排序后"的那一份】listView 的行是按"当前排序方式"排出来的,里面第 s 行对应的是
            // 排序后列表的第 s 项。原先这里拿的是 LoadPresets() 的**原始顺序**列表,而 ResolveSortedIndex
            // 又把 s 原样返回 —— 于是只要用户把排序切成「按名字 A→Z」或「按最近修改」,点「应用预设」
            // 套用的就是**另一条预设**(2026-09-12 自检发现)。改用与 RebuildList 相同的排序再取。
            var cur = SortedPresets(sortCombo.SelectedIndex);
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

    /// <summary>按当前排序方式取预设列表(与预设窗口 listView 的显示顺序**严格一致**)。
    /// 【为什么必须抽成一处】"显示顺序"只能有一个定义:原先 RebuildList 自己排一次、而点「应用预设」时
    /// 又按原始(未排序)顺序去取,于是排序一切到「按名字」/「按最近修改」就会套错预设(2026-09-12 自检发现)。
    /// 现在两边都调它,显示序与取数序不可能再错位。新增排序方式:只改这里 + 排序下拉的文案。</summary>
    private static List<VideoPreset> SortedPresets(int sortIdx)
    {
        var cur = LoadPresets();
        return sortIdx switch
        {
            1 => cur.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            2 => cur.OrderByDescending(x => x.SavedAt, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => cur,   // 0 = 按创建时间(存储顺序)
        };
    }

    /// <summary>把"显示下标"映射回列表下标。入参必须是【与显示同一份排序】的列表(用 SortedPresets 取),
    /// 此时显示序就是列表序,直接返回 showIdx 即可。原先调用方传的是未排序列表 —— 那正是"套错预设"的根因。</summary>
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
            // 【导入也要过一遍序号迁移】旧版导出的 .alhpreset 里超分模型是**旧序号**(3=轻量通用),
            // 而 SavePresets 会统一盖"新序号"章;不先换算就会把"轻量"当成"超慢"存下来(点一下预设慢 17 倍)。
            // 新版导出的文件自带 ModelOrderRev=2,过这里不会被动。
            int migrated = 0;
            foreach (var p in imported)
                if (p?.Params != null && MigrateEsrganModelOrder(p.Params)) migrated++;
            if (migrated > 0) AppLogger.Info($"导入预设:已按新下拉顺序换算 {migrated} 个预设的超分模型序号(旧序 3=轻量通用 → 新序 2)");
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
            // 【界面清理的兼容日志】老预设里若带着"已被删掉的选项"的旧值,同样说清楚它们被忽略了
            // (预设是显式快照,别的参数照常套用;只有这几个已删除的选项按内建口径处理)
            WarnIfLegacyTimelineOptions(preset.Params);
            UpdateOptions();
            OnOptionChanged();   // 再触发一次完整联动:超分/补帧/引擎面板与模型下拉刷新,确保超分开关等真正生效
            Log($"已应用预设「{preset.Name}」");
            AppLogger.UserAction($"视频:应用预设「{preset.Name}」");
            await ShowPresetHintAsync($"已应用预设「{preset.Name}」——超分/后处理/补帧/去重等参数已按该预设套用。");
        }
        catch { _suppressEvents = false; }
    }

    /// <summary>去重模式显示名 —— 必须与 DedupModelCombo 的 7 项一一对应
    /// (0智能检测 1动漫模式 2标准 3温和 4敏感 5手动 6内容帧率;顺序见 VideoView.xaml 的去重模型下拉)。
    /// 【为什么单列一个函数】原来这段是内联三元表达式,只认 0/1、其余全写"手动",
    /// 于是"标准/温和/敏感/内容帧率"四种在预设悬停里全都被显示成"手动",用户看不出自己选了什么。</summary>
    private static string DedupModelName(int idx) => idx switch
    {
        0 => "智能检测",
        1 => "动漫模式",
        2 => "标准",
        3 => "温和",
        4 => "敏感",
        5 => "手动",
        6 => "内容帧率",
        _ => $"模式{idx}",
    };

    /// <summary>动漫去重档位显示名 —— 与 DedupAnimeCombo 的 5 项一一对应。
    /// 一拍N 是有限动画的绘制节奏(每 N 帧才有一帧真内容),选错档位就采不到内容帧。</summary>
    private static string DedupAnimeName(int idx) => idx switch
    {
        0 => "去除一拍二",
        1 => "去除一拍三",
        2 => "去除一拍二与一拍三",
        3 => "半拍二",
        4 => "去除一拍四",
        _ => $"档位{idx}",
    };

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
        // 【2026-09-13 修 · 用户报告「去重补帧4x 的提示显示成 2x」】原实现直接印 {d.InterpScale}x ——
        // 那是下拉【序号】(0~5)不是倍率:「去重补帧4x」存的序号 2 就被打成 "2x";序号为 0 时更会打成 "0x",
        // 用户会以为自己选错了档。现在统一走 InterpScaleMap.Label(与实跑共用同一份映射,另有单测钉住)。
        // 【2026-09-16 补 · 指定帧率这一档要写全】只印 "60 fps(指定)" 会漏掉一件用户必须知道的事:
        // 真正补几倍是**按每个视频的内容帧率现算**的(同一份预设套到 24fps 与 30fps 素材上倍率不同)。
        // 所以把当时手选的倍率一并写出来,并点明"自动":用户一眼能看出该预设与「补帧 2x」那类预设的区别。
        sb.AppendLine("补帧: " + (d.Interp
            ? $"{(d.Model < 0 ? "?" : InterpModelName(d.Model))} · " +
              (d.Target && double.TryParse(d.TargetFps, NumberStyles.Float, CultureInfo.InvariantCulture, out var tfPre) && tfPre > 0
                  ? $"{tfPre.ToString("0.##", CultureInfo.InvariantCulture)} fps(指定,倍率按各片帧率自动定)"
                  : AlhPro.Core.InterpScaleMap.Label(d.InterpScale)) +
              $"{(d.Tta ? " · TTA" : "")}"
            : "关闭"));
        // 去重摘要:模式共 7 项(智能检测/动漫模式/标准/温和/敏感/手动/内容帧率),原来只映射了 0 和 1、
        // 其余一律显示成"手动" —— 预设悬停里根本看不出到底选了什么(标准/温和/敏感/内容帧率全被叫"手动")。
        // 动漫模式另需带上拍型档位(去除一拍二/三/四…),那才是这项预设真正的区别所在。
        // 这正是"动漫通用"改档后必须能一眼看出来的地方。
        sb.AppendLine("去重: " + (d.DedupOn
            ? DedupModelName(d.DedupModel) + (d.DedupModel == 1 ? "·" + DedupAnimeName(d.DedupAnime) : "")
            : "关闭"));
        sb.AppendLine("后处理: " +
            $"锐化{d.PostSharpen} 清晰{d.PostClarity} 钝化蒙版{d.PostUsm} 保留细节{d.PostDetail} " +
            $"去模糊{d.PostDeblur} 边缘抗锯齿{d.PostAa} 边缘增强{d.PostEdge}");
        sb.AppendLine("码率: " + (d.Quality == 5 ? $"自定义 {d.BitrateMbps:0.#}Mbps" : d.Quality switch { 0 => "自动", 1 => "低", 2 => "中", 3 => "高", 4 => "极高", _ => "?" }));
        sb.AppendLine("格式: " + (d.Format == 1 ? "MKV" : "MP4") + " · " + (d.Codec == 1 ? "H.265" : "H.264"));
        if (d.FastMode) sb.AppendLine("兼容模式: 开");
        if (d.VideoDenoiseOn) sb.AppendLine("视频降噪: 开");
        return sb.ToString().TrimEnd();
    }

    // 【2026-09-16 下拉精简为两支】名字表同步只留两项;兜底给 0 的名字 —— 因为越界时真正跑的模型
    // 就是 SelectedInterpModel 兜底的 rife-v4.13,报表里写它的名字是对的(原先兜底 "?" 反而看不出实跑哪支)。
    private static string InterpModelName(int idx) => idx switch
    {
        0 => "通用最新(v4.13)", 1 => "通用(v4.6)", _ => "通用最新(v4.13)",
    };

    // 视频超分模型下拉选项文本(与 VideoView.xaml 里 ComboBoxItem.Content 一致;x4plus 那项是富文本+红字"超慢",
    // 这里只用于预设摘要显示,故仍是纯文本)
    private static string[] UpWaifu2xModelNames = { "通用·cunet", "动漫·upconv_7_anime", "现实·upconv_7_photo" };
    /// <summary>视频超分 Real-ESRGAN 模型的显示名(必须与 VideoView.xaml 里 ComboBoxItem 的**顺序**一一对应)。
    /// 【2026-09-12】补上第 4 项 general-x4v3:此前数组只有 3 项(漏了它),序号 3 会落到兜底值上、显示成别的模型;
    /// 同时顺序随下拉调整:2=general-x4v3、3=超慢(x4plus)。改这里必须与 XAML 同步改,否则显示与实跑不符。
    /// 【2026-09-13】去掉末尾的"(轻量)":v1.3.5 已把该项在下拉里改叫「通用 · realesr-general-x4v3(5MB · 中)」,
    /// 摘要却还印着「(轻量)」—— 用户正是看到"轻量"以为这支不行,才要求把预设模型换成动漫那支。摘要必须与下拉同口径。
    /// 【2026-09-14 Rev3】下拉改为**按速度与体积排**(快/小 → 慢/大):
    ///   0 动漫·animevideov3(4MB) 1 通用·general-x4v3(5MB) 2 通用·wdn-x4v3(5MB) 3 动漫·x4plus-anime(9MB) 4 通用·x4plus(41MB 超慢)。
    /// 换位对应 VideoModelOrder Rev3 的迁移(2→3、3→4、4→2);这里必须与 XAML 的项顺序严格一一对应,
    /// 否则预设摘要会印出与实际不同的模型(此前就踩过:数组漏了一项,序号 3 显示成别的模型)。
    /// 【2026-09-15 Rev4】末尾**追加**两支自训的 2x 模型(5 现实 · real2x、6 游戏 · game2x):
    ///   追加不改老序号 ⇒ VideoModelOrder Rev4 是恒等映射。
    ///   ⚠ 这两项**不再手抄字面量**,而是由 Core.ExperimentalEsrgan.SummaryText 生成 ——
    ///   之前手抄的那版名字里带了"实验/测试"字样,被用户当场纠正("不要写实验,括号里写速度"),
    ///   手抄的名字列表就是漂移源头(本仓库也踩过"数组漏一项、序号显示成别的模型")。用户定名与实测速度
    ///   都只有 Core 那一份,这里跟着走。</summary>
    private static readonly string[] UpEsrganModelNames = BuildUpEsrganModelNames();

    private static string[] BuildUpEsrganModelNames()
    {
        var names = new System.Collections.Generic.List<string>
        {
            "动漫·animevideov3", "通用·general-x4v3(快)", "通用·wdn-x4v3(快)", "动漫·x4plus-anime", "通用·x4plus(超慢)",
        };
        foreach (var m in AlhPro.Core.ExperimentalEsrgan.All)
            names.Add(AlhPro.Core.ExperimentalEsrgan.SummaryText(m));
        return names.ToArray();
    }
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
        // 加载完成前禁止保存:构造/加载期控件默认赋值或未恢复时 SelectedIndex=-1,会污染 video-settings.json
        // (日志实测 [记忆] 视频设置加载 Quality=-1/Format=-1/Codec=-1 的根因:构造期 SaveSettings 把 -1 写盘)
        if (!_settingsLoaded || _suppressEvents) return;
        try
        {
            var d = CollectVideoParams();
            // 【盖章:本版写出来的文件一律是**当前 Rev** 的序号】CollectVideoParams 每次都 new 一个 VideoSettings,
            // ModelOrderRev 默认 0;若不在这里盖章,下次启动 LoadSettings 的迁移会以为"这还是老文件"→
            // 又按老映射换一遍序号,模型在两次启动之间来回横跳(实测过一次,必须钉住)。
            // ⚠ 这里**必须**引用 VideoModelOrder.CurrentRev,不能写死数字 —— 2026-09-14 换顺序(Rev3)时,
            //   写死的 2 会让"用户新选的 wdn(新序 2)"在下次启动被当成 Rev2 的 2 再换一次 → 变成 x4plus-anime,
            //   即"什么都没改却换了模型"。写死版本号 = 埋一颗定时炸弹。
            d.ModelOrderRev = AlhPro.Core.VideoModelOrder.CurrentRev;
            // 【同样的盖章:SceneDefaultRev】**本版语义已改**:迁移只对齐版本号、不改用户的勾选值,
            // 所以盖章不再承担"防止被迁回去"的职责;保留它只是让"已结算过"这件事写进文件,
            // 免得每次启动都因为版本落后而重写一遍设置文件(与 ModelOrderRev 那份理由同源)。
            // ⚠ 同样禁止写死数字:Rev 一变,写死的值会让文件立刻"版本落后"。
            d.SceneDefaultRev = AlhPro.Core.SceneDefaultPolicy.CurrentRev;
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, System.Text.Json.JsonSerializer.Serialize(d));
        }
        catch { }
    }

    /// <summary>从当前页面 UI 收集全部视频处理参数为 VideoSettings 快照。
    /// 供「记住上次参数」与「保存为预设」共用(参数预设=用户主动命名保存的同一份快照)。</summary>
    private VideoSettings CollectVideoParams()
    {
        // 「指定输出帧率」写盘:下拉「不限」= 没指定;「跟随屏幕刷新率」存的是当时那个数(重开时按它对回那一档)
        double? targetFpsStored = SelectedTargetFps();
        return new VideoSettings
        {
            Remember = VideoRememberCheck.IsChecked == true,
            Up = UpscaleToggle.IsChecked == true,
            Engine = EngineToStored(VideoEngineRadios.SelectedIndex),   // 存盘用旧约定(0=waifu2x/1=real),与界面顺序解耦
            Scale = VideoScaleRadios.SelectedIndex,
            Gpu = AppSettings.GpuIndex,
            Interp = InterpToggle.IsChecked == true,
            Model = InterpModelCombo.SelectedIndex,
            UpWaifu2xModel = VideoWaifu2xModelCombo.SelectedIndex,   // 超分 waifu2x 模型
            UpEsrganModel = VideoEsrganModelCombo.SelectedIndex,    // 超分 Real-ESRGAN 模型
            // 【2026-09-16 修复 · 评审 Critical 8】这里必须存**真实倍率序号**(0~5),不许把"指定帧率"那一项(序号 6)存进去:
            //   ① 读取端只认 0~5(见加载处的 `is >= 0 and <= 5`),存 6 会被静默丢弃 ⇒ 重启后真实倍率回退成 2x;
            //   ② 预设摘要会拿它去 InterpScaleMap.Label(6) → 打印成 "2x"(又是一次"序号当倍率"的显示错误)。
            // 用 CurrentScaleIndex()(选中"指定帧率"时返回上一个真实倍率)。是否指定帧率由 Target/TargetFps 单独记录。
            InterpScale = CurrentScaleIndex(),
            Target = targetFpsStored is > 0,
            TargetFps = targetFpsStored?.ToString("0.###", CultureInfo.InvariantCulture) ?? "",
            // 【被删掉的三个选项:一律写**内建值**(不再读控件 —— 控件已不存在)】
            //   VfrMode=0(自动) / VfrExpanded=false(面板已删) / FpsBase=0(=真实时间轴插值)。
            // 这样老文件里曾经存过的 1/true 会被就地修正,不会"删了控件却被老值关掉"。
            VfrMode = 0,
            VfrExpanded = false,
            FpsBase = 0,
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
            // 【阈值写内置默认】滑条已删 ⇒ 写常量(不写死字面量之外的来源),老文件里别的值不会被反复改写。
            SceneThr = SceneThresholdBuiltIn,
            Tta = TtaCheck.IsChecked == true,
            OutDir = _customOutDir ?? "",
            CustomW = CustomWidthBox.Text,
            CustomH = CustomHeightBox.Text,
            PostSharpen = (int)SharpenSlider.Value,
            PostClarity = (int)ClaritySlider.Value,
            PostUsm = (int)UsmSlider.Value,
            PostDetail = (int)DetailSlider.Value,
            PostDeblur = 0,   // 视频页已移除「去模糊」(ffmpeg 无反卷积滤镜);字段保留仅为兼容旧设置文件
            PostAa = (int)PostAaSlider.Value,
            PostEdge = (int)PostEdgeSlider.Value,
            MotionBlur = MotionBlurCombo.SelectedIndex,
            DeShake = DeShakeCheck.IsChecked == true,
            Quality = QualityCombo.SelectedIndex >= 0 ? QualityCombo.SelectedIndex : 0,   // -1(未选中)兜底 0,防污染设置文件
            BitrateMbps = QualityCombo.SelectedIndex == 5 ? ParseBitrate() : 0,
            Codec = CodecCombo.SelectedIndex >= 0 ? CodecCombo.SelectedIndex : 0,
            Format = FormatCombo.SelectedIndex >= 0 ? FormatCombo.SelectedIndex : 0,
            FastMode = FastModeCheck.IsChecked == true,
            // 【2026-09-14 启动崩溃修复】这个复选框是较晚新增的,这里保留"控件未创建时按界面默认值记"的兜底
            //   (平滑时间轴默认开),避免万一有别的路径在控件没建好时收集参数、
            //   把 null 记成"关"而覆盖用户设置。
            //   ⚠ 注意:它**不是**本次启动崩溃的解 —— 真正抛异常的是 VideoView.xaml.cs:528
            //   `RunBtn.IsEnabled`(经 XAML:691 → OnOptionChanged → UpdateOptions → UpdateRunState),
            //   而 SaveSettings/CollectVideoParams 这条路在解析期已被 `!_settingsLoaded` 提前拦掉。
            //   根因证据与整体拦截见 _uiReady 字段说明。
            // 【平滑时间轴:恒为「开」(2026-09-15 用户定调)】控件已从界面移除,这里**不读控件**、一律写 true;
            // 这是"移除控件但功能照旧生效"的关键一步 —— 若仍读某个已不存在/被忽略的控件,老设置里的 false
            // 会被再次写盘,功能就被"删控件"顺手关掉了。字段保留只为兼容旧文件与预设快照。
            SmoothTimeline = true,
            Mute = MuteCheck.IsChecked == true,
            VideoDenoiseOn = DenoiseToggle.IsChecked == true,
            VideoDenoiseStrong = DenoiseToggle.IsChecked == true ? DenoiseStrongRadios.SelectedIndex : -1,
            DenoiseKind = DenoiseKindCombo?.SelectedIndex ?? 0,
        };
    }

    /// <summary>按元素缓存当前展开/收起 Storyboard,开始新动画前先停旧的(避免两个动画抢 Height 导致顿)。</summary>
    private readonly System.Collections.Generic.Dictionary<Microsoft.UI.Xaml.UIElement, Microsoft.UI.Xaml.Media.Animation.Storyboard> _showHideSbs = new();

    /// <summary>展开/收起动画:高度从 0 渐增/渐减 + 淡入淡出,把下方内容平稳推下去/收上来(不生硬跳动)。
    /// 状态没变时不重播(否则滑条拖动等触发 UpdateOptions 会让动画一闪一闪)。</summary>
    private void AnimateShowHide(Microsoft.UI.Xaml.UIElement el, bool show)
    {
        // 【整体兜底】这类"高度+淡入淡出"动画会在布局线程上跑:Begin() 与 Completed 回调都可能抛
        // COMException(0x80070490)或 LayoutCycleException(该机器 2026-09-11 崩过一次)。
        // 动画只是观感,失败就退化成"直接显示/隐藏" —— 绝不能让它把界面或任务带崩。
        try
        {
            AnimateShowHideCore(el, show);
        }
        catch (Exception ex)
        {
            NoteUiRefreshFailure("展开/收起动画", ex);
            try { el.Visibility = show ? Visibility.Visible : Visibility.Collapsed; } catch { }
            if (el is Microsoft.UI.Xaml.FrameworkElement fe2) { try { fe2.Height = double.NaN; } catch { } }
        }
    }

    private void AnimateShowHideCore(Microsoft.UI.Xaml.UIElement el, bool show)
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
            // 【2026-09-12 起:不再给 Height 做动画 —— 这是布局循环的经典成因】
            // 依据:那台 50 系笔记本 22:31 的崩溃诊断是 **LayoutCycleException**("Layout cycle detected"),
            // 而且崩溃文件里【没有】「最近界面操作」面包屑 → 说明异常是从 XAML 的布局过程本身抛出的,
            // 不是从我们某次控件调用里抛的,**外面包 try/catch 根本拦不到**。
            // 给 Height/Width 这类"参与测量排布"的属性做动画,会在动画每一帧都让下游重新测量;
            // 面板下面又挂着会自增长的日志区与滚动条,两者互相触发就成环 → WinUI 直接判布局循环崩掉。
            // 现在:显示/隐藏的高度变化一次性完成(布局只算一遍),观感交给透明度渐变(不参与布局,绝不会成环)。
            el.Visibility = Visibility.Visible;
            el.Opacity = 0;
            fe.Height = double.NaN;   // 恢复自适应高度(绝不再对它做动画)
        }
        else
        {
            // 收起:只淡出,高度同样交给布局一次搞定;淡出结束后再真正隐藏
            sb.Completed += (_, _) =>
            {
                try { el.Visibility = Visibility.Collapsed; }
                catch (Exception ex) { NoteUiRefreshFailure("收起动画收尾", ex); }
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

    // （「可变帧率保护」面板与它的展开/收起处理函数 VfrToggleBtn_Click 已于 2026-09-15 按用户裁决删除:
    //   固定为"自动" —— 源是 VFR 就按真实时间戳排帧,普通素材走常规路径,用户无须也不该为此决策。）

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

    /// <summary>【界面日志刷新抑制(2026-09-12)】一旦本次运行检出"布局循环"(LayoutCycleException),就把它置 true:
    /// 之后日志**只写文件、不再改界面文本框与滚动条** —— 因为继续高频改那个会自增长的日志区,
    /// 只会让布局再次成环,界面永久卡死(而那台 50 系笔记本的实测是:界面死了、后台任务其实还在正常跑,
    /// 用户只能重启软件把任务一起打断 —— 代价比"少刷几行界面日志"大得多)。
    /// 只在内存里生效,重启软件自动恢复。</summary>
    internal static volatile bool SuppressUiLogUpdates;
    /// <summary>把"日志滚到底部"推迟到当前布局收尾之后再执行(2026-09-12)。
    /// 【为什么必须推迟】直接在调用里 ChangeView:若此刻 XAML 正在测量/排布(日志区边填边量、又正值面板显隐),
    /// 就会在布局过程中再次改动滚动位置 → 布局失效 → 重测 → 成环,WinUI 抛 **LayoutCycleException**。
    /// 该异常是在框架布局代码里抛的,**外面包 try/catch 根本拦不到** —— 那台 50 系笔记本 22:31 的崩溃诊断
    /// 正是 LayoutCycleException,且崩溃文件里连「最近界面操作」面包屑都没有(因为异常不来自我们的调用栈)。
    /// TryEnqueue 让滚动发生在下一次空闲,脱离布局过程;入队回调里仍包 try/catch(队列里抛同样会成未处理异常)。
    /// 观感上无差别:滚到底只是"下一帧"完成,人眼看不出。</summary>
    private void ScrollLogToBottomDeferred()
    {
        try
        {
            App.UiBreadcrumb = "日志滚动到底(延迟执行)";
            var q = DispatcherQueue;
            if (q == null) return;
            q.TryEnqueue(() =>
            {
                try { VideoLogScroll.ChangeView(null, VideoLogScroll.ScrollableHeight, null, true); }
                catch (Exception ex) { NoteUiRefreshFailure("日志滚动到底(延迟)", ex); }
            });
        }
        catch { }
    }
    private void Log(string msg)
    {
        AppLogger.Info(msg);   // 同步写诊断日志文件
        // 【UI 刷新绝不许把任务带崩】下面两句是 WinRT/原生控件操作:文本越长布局越贵,
        // 而 ScrollViewer.ChangeView 在布局进行中/文本高速增长时会抛 COMException(实测 0x80070490
        // "找不到元素")或 LayoutCycleException —— 50 系笔记本一天内崩两次,两次都紧跟在"刷新日志行"
        // 之后,崩完界面不再更新、用户只看到"卡住"。日志文件已经落了盘(上面那行),界面刷不动就少刷一次,
        // 绝不能让它把正在跑的任务拖死。
        if (SuppressUiLogUpdates) return;   // 布局循环已检出:界面日志停刷(文件日志上面已落盘)
        try
        {
            App.UiBreadcrumb = "日志行追加(界面文本框)";
            var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            // 【一次赋值:先拼好、裁好,只改一次 Text(2026-09-12)】原来是"先追加、再裁剪",一次刷新里连着改两次 Text。
            // 这段文本是自动折行的,每改一次就要重新测量一次;日志快速变长时(开去重)正是把布局推向成环的推手之一。
            // 现在:字符串上拼好、裁好,只赋一次值。
            var next = VideoLogText.Text == "日志:等待任务..." ? line : VideoLogText.Text + "\n" + line;
            var lines = next.Split('\n');
            if (lines.Length > 200) next = string.Join("\n", lines.Skip(80)) + "\n";
            VideoLogText.Text = next;
            ScrollLogToBottomDeferred();
        }
        catch (Exception ex) { NoteUiRefreshFailure("视频日志追加/自动滚动", ex); }
    }

    /// <summary>界面刷新失败只记一次(避免每帧一条把日志刷爆),并记下"是哪一处 UI 操作":
    /// 崩溃诊断里会带上这条面包屑,下次再崩就能直接指到具体调用,不用像这次一样靠时间线推断。</summary>
    private static int _uiFailLogged;
    internal static void NoteUiRefreshFailure(string where, Exception ex)
    {
        try
        {
            App.UiBreadcrumb = where + " 抛 " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8");
            if (System.Threading.Interlocked.Increment(ref _uiFailLogged) <= 5)
                AppLogger.Warn($"⚠ 界面刷新失败,已忽略并继续(不影响处理结果):{where} — {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message.Split('\n')[0]}");
        }
        catch { }
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
                // 必须 await:下面「输入帧率」框会按 FpsProbe 同步,不等它就等于把旧帧率又写回界面
                await RefreshItemProbeAsync(existing);
                continue;
            }
            var info = await VideoService.ProbeVideoInfoAsync(p);
            var item = new VideoItem
            {
                Path = p,
                Name = Path.GetFileName(p),
                BaseInfo = info,
                FpsProbe = await Task.Run(() => VideoService.ProbeFps(p) ?? ""),   // 后台跑 ffprobe,避免逐文件卡 UI 线程
                Thumb = null,
            };
            _videos.Add(item);
            added++;
            // 异步生成缩略图 + 探测时长 + 探测是否为可变帧率(VFR,防变速)
            _ = GenerateThumbAsync(item);
            _ = LoadItemDurationAsync(item);
            _ = RefreshItemSizeAsync(item);   // 源分辨率(补帧兼容性预检要用;缓存到 item,不在 UI 回调里现探)
            _ = ProbeVfrAsync(item);
            _ = ProbeDupAsync(item);   // 入列:轻量预估重复帧(标"预估"),选中才全文分析
        }
        if (added > 0) Log($"添加了 {added} 个视频到列表");
        // 仅第一个视频(单视频)自动选中以填充帧率;多视频不自动选中
        if (_videos.Count == 1)
            VideoList.SelectedIndex = 0;
        // 「输入帧率」框必须归【新加入的待处理视频】所有,不能沿用上一个条目。
        // 只在"待处理(未完成)视频恰好一个"时同步:这时它是单视频语义,框里的值就是该视频实际用的输入帧率;
        // 而"上一个视频已完成变灰 + 新拖入一个"时列表有 2 项、框已被隐藏,但「开始处理」只处理未完成的那一个
        // → 框里的旧值会被当成新视频的输入帧率参与节奏换算(内容帧率 = 输入帧率 ÷ 拍数),直接算错去重/补帧。
        // FpsProbe 与入列时用的是同一个 VideoService.ProbeFps,口径一致;探测失败=空串(不沿用旧值)。
        // 这里会覆盖"拖入之前手填的值"—— 与既有语义一致(选中项一变该框就按新视频重写);拖入之后手填仍然有效。
        var actives = _videos.Where(v => !v.IsDone).ToArray();
        if (actives.Length == 1) SetInputFpsText(actives[0].FpsProbe, actives[0]);
        UpdateDropHint();
        UpdateRunState();
        UpdateOptions();   // 单/多视频 UI 切换
    }

    /// <summary>同名文件被覆盖导出后重拖入:刷新该项目的帧率/去重预估(不沿用旧数据)。</summary>
    private async Task RefreshItemProbeAsync(VideoItem item)
    {
        try
        {
            item.FpsProbe = await Task.Run(() => VideoService.ProbeFps(item.Path) ?? "");
            // 【补帧兼容性预检要用源分辨率】顺手在这里探一次并缓存 —— 这条路本来就在做 ffprobe,
            // 不额外起进程;绝不在 UpdateOptions 里现探(那会在每次点控件时卡界面)。
            _ = RefreshItemSizeAsync(item);
            // 【任务 P】这个视频正好是「输入帧率」框的来源(框里那个值的 owner,或用户刚点过的那个)→ 同步刷新框,
            // 否则重拖/覆盖同名文件后,框里会留着旧帧率(而它参与节奏换算)。
            if (ReferenceEquals(_inputFpsOwner, item) || ReferenceEquals(_lastClickedItem, item))
                SetInputFpsText(item.FpsProbe, item);
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

    /// <summary>后台探测源分辨率并缓存到 item(补帧兼容性预检要用)。
    /// 只在"入列/刷新"时跑一次,UI 回调只读缓存值 —— 绝不在 UpdateOptions 里现跑 ffprobe。</summary>
    private async Task RefreshItemSizeAsync(VideoItem item)
    {
        try
        {
            var (w, h) = await VideoService.ProbeSizeAsync(item.Path);
            if (w > 0 && h > 0) { item.ProbeWidth = w; item.ProbeHeight = h; }
        }
        catch { }
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
        // 按预估重复率推荐去重模式。下拉【只有 3 项】:0 智能检测 / 1 动漫模式 / 2 手动模式。
        // 【修复越界】这里原来在"重复很少"那一支赋 SelectedIndex = 3(越界)→ ComboBox 变成未选中(-1)
        // → 传下去 dedupMode = 0 → 【去重被静默关掉】,而日志还写着早已删除的"温和模式"。
        // 现在用「智能 + 保守策略」表达当年的"温和"意图:保守 = 置信度门槛 0.70 且必须是常见拍数才采用,
        // 也就是【几乎不误删】。三支的索引都在 0..2 内,且日志文案与实际一致。
        if (dup >= 25)
        {
            DedupModelCombo.SelectedIndex = 1;   // 动漫模式:按拍型均匀采样
            Log($"已按预览设置去重(重复≈{dup:0}%):动漫模式(按拍型均匀采样,不做逐帧判定)");
        }
        else if (dup >= 8)
        {
            DedupModelCombo.SelectedIndex = 0;   // 智能检测
            DedupSmartCombo.SelectedIndex = 0;   // 均衡
            Log($"已按预览设置去重(重复≈{dup:0}%):智能检测 · 均衡(识别得出拍数才采样)");
        }
        else
        {
            DedupModelCombo.SelectedIndex = 0;   // 智能检测
            DedupSmartCombo.SelectedIndex = 2;   // 保守:几乎不误删
            Log($"已按预览设置去重(重复≈{dup:0}%):智能检测 · 保守(重复很少,宁可不删也不误删细节)");
        }
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
        {
            // 【处理中不可预览】任务处理中,预览目标文件未就绪/被占用 → 明确提示,不尝试打开
            if (_running)
            {
                try
                {
                    _ = new Microsoft.UI.Xaml.Controls.ContentDialog
                    {
                        Title = "处理中",
                        Content = "视频正在处理中,暂不可预览。请等待处理完成后再双击预览。",
                        CloseButtonText = "好的",
                        XamlRoot = this.XamlRoot,
                    }.ShowAsync();
                }
                catch { }
                return;
            }
            OpenTrimPage(item);   // 双击 = 裁剪页(只有裁剪:时间线首尾两个把手,拖动时画面跟着跳)
        }
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
        // 【任务 P】删掉的正是"用户点过的那个视频"→ 清掉记忆,否则框会一直显示一个已删除视频的帧率
        if (ReferenceEquals(_lastClickedItem, item)) _lastClickedItem = null;
        // 删掉的正是「当前选中项」或「输入帧率框当前值的来源视频」→ 这两处派生值立刻清空。
        // 该框是"某个视频"的派生值,删完后若还留着(选中事件不保证重发),用户接着拖入新视频
        // 就会看到【上一个视频】的帧率 —— 而它参与节奏换算(内容帧率 = 输入帧率 ÷ 拍数),会算错去重/补帧。
        bool selGone = _selected == null || !_videos.Contains(_selected);
        if (selGone) _selected = null;
        if (selGone)
        {
            SetInputFpsText("", null);     // 空 = 无对应视频(处理时自动探测)
            VideoInfo.Text = "未选择视频";
        }
        else if (ReferenceEquals(_inputFpsOwner, item))
        {
            // 删掉的是"框里那个值的来源视频",但列表里仍有选中项 → 把框重新对齐到当前选中项
            // (不重新探测就会留着一个"已删除视频"的帧率,选中事件在这种情形下不会重发)
            _ = SyncInputFpsAsync(_selected);
        }
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

    /// <summary>检测是否 RTX 50 系列(Blackwell 架构)显卡:从 VulkanCheck 设备或系统枚举名称判断。
    /// 复用 AlhPro.Core.GpuName(有单测):先排除 Ada/Turing/Quadro,避免把 "RTX 5000 Ada"/"Quadro RTX 5000" 误判为 50 系。</summary>
    private static bool IsBlackwellGpu()
    {
        try
        {
            var names = new System.Collections.Generic.List<string>();
            names.AddRange(VulkanCheck.Devices.Select(d => d.Name));
            try { names.AddRange(GpuInfo.GetAdapterNames()); } catch { }
            return AlhPro.Core.GpuName.AnyIsBlackwell(names);
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
        _lastClickedItem = null;   // 【任务 P】列表清空 → 连"点过的那个视频"的记忆一起清掉,框才允许为空
        VideoInfo.Text = "未选择视频";
        SetInputFpsText("", null);   // 「输入帧率」是某个视频的派生值,列表清空就不许留在界面上(会带进下一个视频)
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

    private async void VideoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 多选模式:取最后点击的项作为"当前选中"
        var sel = VideoList.SelectedItems.Count > 0
            ? VideoList.SelectedItems[^1] as VideoItem : null;
        _selected = sel;
        if (sel != null) _lastClickedItem = sel;   // 【任务 P】记住用户点过的项(取消选中时还要用它)
        UpdateListButtons();
        VideoInfo.Text = sel != null ? $"{sel.Name}\n{sel.Info}" : "未选择视频";
        // 【任务 P · 用户报障修复】Multiple 模式下"点一个已选中的项"= 取消选中 → 旧代码在这里把框清空,
        // 用户看到的就是「激活/选中以后依然空的」。现在按纯策略决定显示来源:
        //   有选中项 → 用它;没选中但记得"刚点过的那一项" → **仍显示那一项的帧率**(已完成的视频也算);
        //   两者都没有(列表刚清空/删完) → 才留空(空 = 处理时按该视频自动探测,语义正确)。
        // 传【局部 sel】而不是字段 _selected:探测期间用户又改选中/删项时,
        // await 之后再读字段会把 A 的帧率写进 B 的输入框(异步竞态)。
        var src = AlhPro.Core.InputFpsSyncPolicy.Decide(VideoList.SelectedItems.Count, HasLastClickedInList());
        await SyncInputFpsAsync(src == AlhPro.Core.InputFpsSyncPolicy.Source.Selection ? sel : LastClickedInList());
        _ = RefreshVideoOutSpec();
    }

    /// <summary>_lastClickedItem 是否仍然在列表里(防"已删除的视频"的帧率留在框里)。</summary>
    private bool HasLastClickedInList() => _lastClickedItem != null && _videos.Contains(_lastClickedItem);
    private VideoItem? LastClickedInList() => HasLastClickedInList() ? _lastClickedItem : null;

    /// <summary>【任务 P】点击"激活"某个视频 → 把「输入帧率」框显示成该视频的实测帧率(含**已处理完成**的项)。
    /// 为什么必须单独有这条路径:①列表是多选模式,点一个"已选中"的项在 SelectionChanged 里表现为"取消选中",
    /// 而那里过去会清空该框 —— 这正是用户报的"激活以后依然空的";②已完成的项在旧代码里没有任何回填路径
    /// (回填只在"选中变化"和"入列时待处理恰好 1 个"时发生)。ItemClick 不依赖选中状态:点谁显示谁。
    /// 【归属不变】框的来源仍记成被点的那个视频(_inputFpsOwner):处理时的残留防线照旧要求
    /// owner 必须是本次要处理的第一个视频,否则忽略并写日志 —— 显示放开、取值口径一点没放宽。</summary>
    private async void VideoList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not VideoItem clicked) return;
        _lastClickedItem = clicked;
        if (string.IsNullOrEmpty(clicked.FpsProbe))
            await SyncInputFpsAsync(clicked);          // 缓存为空(入列时探测失败)→ 现探一次
        else
            SetInputFpsText(clicked.FpsProbe, clicked); // 用入列时探到的值(无 ffprobe 开销)
    }

    /// <summary>把「输入帧率」框同步为指定视频的实测帧率;<paramref name="item"/> 为 null = 清空该框。
    /// 【必须无条件写入,探测失败/为空就留空】:这个值参与节奏换算(内容帧率 = 输入帧率 ÷ 拍数),
    /// 沿用上一个视频的残留值不只是显示错,会真的算错去重/补帧 —— 空值=处理时自动探测(正确语义),
    /// 所以宁可为空也绝不留旧值。ffprobe 阻塞,放后台线程避免卡 UI。</summary>
    private async Task SyncInputFpsAsync(VideoItem? item)
    {
        string fps = "";
        if (item != null)
        {
            try { fps = await Task.Run(() => VideoService.ProbeFps(item.Path)) ?? ""; } catch { }
        }
        SetInputFpsText(fps, item);
    }

    /// <summary>直接写入「输入帧率」框(值已知时用,不再重跑 ffprobe)。空串 = 未知 —— 见上:宁可留空。
    /// owner = 这个值是从哪个视频探测来的(null = 无来源/用户手填)。记来源是为了在开始处理时识别
    /// "框里还留着上一个视频的帧率"这种残留(见 RunBtn 里的残留防线)。</summary>
    private void SetInputFpsText(string fps, VideoItem? owner)
    {
        // 先记来源再赋值:TextChanged 是同步回调,"用户手改"那条规则会清掉 owner,顺序反了会误判。
        _inputFpsOwner = owner;
        _suppressEvents = true;
        InputFpsBox.Text = fps;
        _suppressEvents = false;
        UpdateOptions();
    }

    // ===== 【第 3 项】"超分将落到 CPU"的醒目提示(内联红字为主;预计 >30 分钟才多一次确认) =====

    /// <summary>本次超分是否会落到 CPU。只判【确定的】场景,不猜:
    /// ① 用户显式选了 CPU(AppSettings.GpuIndex &lt; 0);
    /// ② 本次超分走 ONNX 路线,而 DirectML 已【实测完成且确认不可用】
    ///    (DmlProbeCompleted 且 DmlFallbackOk &lt; 0)—— ONNX 路线下 DML 建会话失败就会静默落到 CPU,
    ///    这正是真机诊断包里"8 秒/帧"的成因。
    /// 【为什么必须先看 DmlProbeCompleted】DmlFallbackOk==-1 有两种含义:还没探测(未知)/ 探完确认不可用。
    /// 把"未知"当"不可用"会误报"要用 CPU"吓用户,所以只在探测【完成并判定不可用】时才提示。
    /// ncnn-Vulkan 路线不受 DirectML 影响,不提示。</summary>
    private bool UpscaleWillFallbackToCpu()
    {
        try
        {
            if (UpscaleToggle.IsChecked != true) return false;
            if (AppSettings.GpuIndex < 0) return true;   // ① 用户主动选 CPU
            if (!ALHPro.EsrganOnnxService.DmlProbeCompleted || ALHPro.EsrganOnnxService.DmlFallbackOk >= 0)
                return false;                            // 未探完 / 探测说可用 → 不当作 CPU 场景(不误报)
            return OnnxUpscaleRouteLikely();             // ② 走 ONNX 路线 + DirectML 实测不可用
        }
        catch { return false; }
    }

    /// <summary>当前下拉选中的补帧模型名(引擎侧用名)。与 RunBtn 构造参数时用的是同一个映射,
    /// 抽出来是为了让"兼容提示"和"实际执行"永不脱钩(两处各写一份迟早分叉)。</summary>
    private string SelectedInterpModel => InterpModelCombo.SelectedIndex switch
    {
        0 => "rife-v4.13",
        1 => "rife-v4.6",
        // 【2026-09-16 下拉精简为两支】动漫/高清/超高清/经典兼容/v4.26 已下架;老设置里存的序号 2~6
        // 会被读设置时的范围检查(ReadSettings 里的 d.Model < Items.Count)挡掉 ⇒ 自动落到默认的 0(v4.13)。
        // 这里保留兜底,是为了越界时也绝不返回一个不存在的模型目录名。
        _ => "rife-v4.13",
    };

    /// <summary>本次超分是否会走 ONNX 路线(只用 EngineService 的公共判定函数,不复制 VideoService 内部状态):
    /// 兼容模式强制 ONNX;其余一律【以真机实测结论为准】——没测过时不按显卡型号断言(与 ShouldUseOnnx* 同口径)。
    /// 判定不出(引擎未枚举等)返回 false —— 宁可漏提示,也不误报"要用 CPU"。</summary>
    private bool OnnxUpscaleRouteLikely()
    {
        try
        {
            if (FastModeCheck.IsChecked == true) return true;   // 兼容模式:ncnn 路径同样改走 ONNX
            // 【删掉了 IsBlackwellGpu() ||】此前 50 系无条件返回 true → ETA/提示永远按 ONNX 慢路估算,
            // 即使实测证明 ncnn 可用。现在只看实测结论(ShouldUseOnnx* 内部:有结论用结论;
            // 没结论时只在"无独显 / Vulkan 不可用"这两种确实只能 CPU 的情况下才为真)。
            if (!SelectedEngineIsReal)
                return EngineService.ShouldUseOnnxWaifu2x();
            return EngineService.ShouldUseOnnxEsrgan();
        }
        catch { return false; }
    }

    /// <summary>当前 UI 参数对应的经验库指纹(与 RunBtn_Click 里 ETA 校准的构造口径一致:分辨率固定按 1080p 归一,
    /// 面积倍率由 EstimateCpuUpscaleSeconds 单独乘)。只用于查"同配置上次实测秒/帧"——
    /// 查不到就用保守常数,估不准的风险有界。</summary>
    private string PerfFingerprintForCpuEstimate(out string engine)
    {
        engine = SelectedEngineIsReal ? "realesrgan" : "waifu2x";
        double scale = VideoScaleRadios.SelectedIndex switch { 1 => 2, 2 => 3, 3 => 4, _ => 1 };
        if (VideoScaleRadios.SelectedIndex is 0 or 4) scale = 2;   // 1x 缩回 / 自定义:内部都按 2x 超分
        int interpScale = AlhPro.Core.InterpScaleMap.Multiplier(CurrentScaleIndex());
        bool dedupOn = DedupCheck.IsChecked == true;
        int vdenoise = DenoiseToggle.IsChecked == true ? DenoiseStrongRadios.SelectedIndex + 1 : 0;
        bool postFx = (int)SharpenSlider.Value + (int)ClaritySlider.Value + (int)UsmSlider.Value
            + (int)DetailSlider.Value + (int)PostAaSlider.Value + (int)PostEdgeSlider.Value > 0;
        return PerfMemory.Fingerprint(engine, scale, 1920, 1080, interpScale, dedupOn, vdenoise, postFx);
    }

    /// <summary>取"CPU 预估用的秒/帧(1080p 基准)":经验库同配置实测 与 保守常数 取【较大者】(只增不减)。
    /// 返回 (秒每帧基准, 来源说明)。</summary>
    private (double perFrameBase, string source, bool fromHistory) CpuPerFrameBase()
    {
        double floor = ALHPro.EsrganOnnxService.CpuSecondsPerFrame1080p;
        try
        {
            var hist = PerfMemory.PerFrameFor(PerfFingerprintForCpuEstimate(out _));
            if (hist.HasValue && hist.Value > floor)
                return (hist.Value, $"同配置上次实测 {hist.Value:0.##} 秒/帧(1080p 基准)", true);
        }
        catch { }
        return (floor, $"保守常数 {floor:0.#} 秒/帧(1080p 基准;出处见 EsrganOnnxService.CpuSecondsPerFrame1080p)", false);
    }

    /// <summary>刷新"超分将落到 CPU"的醒目红字(第 3 项①②:内联为主,不弹窗)。
    /// 在开始处理【之前】就能看到(与输出规格同一处提示位),所以用户不必等跑起来才知道。
    /// 数字来源:秒/帧 = max(经验库同配置实测, 保守常数) × 面积;帧数 = 时长 × 源帧率。
    /// 算不出数字(时长/尺寸探测失败)时只报"会落到 CPU",绝不编一个数出来。</summary>
    private void SetCpuFallbackHint(string? text)
    {
        try
        {
            if (CpuFallbackHint == null) return;
            if (string.IsNullOrEmpty(text))
            {
                CpuFallbackHint.Text = "";
                CpuFallbackHint.Visibility = Visibility.Collapsed;
                return;
            }
            CpuFallbackHint.Text = text;
            CpuFallbackHint.Visibility = Visibility.Visible;
        }
        catch { }
    }

    /// <summary>按当前选中视频刷新 CPU 回落提示(供 RefreshVideoOutSpec 在算完尺寸/帧率后调用)。</summary>
    private void RefreshCpuFallbackHint(double dur, double srcFps, int sw, int sh)
    {
        try
        {
            if (!UpscaleWillFallbackToCpu()) { SetCpuFallbackHint(null); return; }
            var est = EstimateCpuUpscale(dur, srcFps, sw, sh);
            if (est == null)
            {
                SetCpuFallbackHint("⚠ 本机 DirectML 不可用,超分将使用 CPU(速度极慢;当前视频时长/尺寸未探明,无法给出秒/帧与总时长预估)");
                return;
            }
            var (perFrame, stageMin, frames) = est.Value;
            SetCpuFallbackHint($"⚠ 本机 DirectML 不可用,超分将使用 CPU:约 {perFrame:0.#} 秒/帧 × {frames} 帧 → 该阶段预计约 {FormatMinutes(stageMin)}");
        }
        catch { }
    }

    /// <summary>预估"超分落到 CPU 后该阶段多久"。(perFrame 秒/帧, stageMin 分钟, frames 帧)。
    /// 返回 null = 信息不足(时长/尺寸/帧率未知),此时【不显示数字】(绝不编)。</summary>
    private (double perFrame, double stageMin, long frames)? EstimateCpuUpscale(double dur, double fps, int w, int h)
    {
        try
        {
            if (dur <= 0 || fps <= 0 || w <= 0 || h <= 0) return null;
            long frames = (long)Math.Ceiling(dur * fps);
            if (frames <= 0) return null;
            var (perFrameBase, _, _) = CpuPerFrameBase();
            double sec = ALHPro.EsrganOnnxService.EstimateCpuUpscaleSeconds(frames, w, h, perFrameBase, out var perFrame);
            return (perFrame, sec / 60.0, frames);
        }
        catch { return null; }
    }

    /// <summary>分钟数的可读写法(≥60 分钟写成"N 小时 M 分",避免"预计 187 分钟"这种要用户自己换算的数字)。</summary>
    private static string FormatMinutes(double minutes)
    {
        if (minutes < 1) return "不到 1 分钟";
        if (minutes < 60) return $"{minutes:0.#} 分钟";
        double h = Math.Floor(minutes / 60);
        double m = minutes - h * 60;
        return m < 1 ? $"{h:0} 小时" : $"{h:0} 小时 {m:0} 分";
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
                return;   // CPU 回落提示(CpuFallbackHint)不在这里隐藏:处理中它正是要给用户看的
            }
            // 取选中项第一;无选中取列表第一
            var sel = VideoList.SelectedItems.Cast<VideoItem>().LastOrDefault();
            VideoItem? it = sel ?? (_videos.Count > 0 ? _videos[0] : null);
            if (it == null)
            {
                VideoOutSpecText.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                SetCpuFallbackHint(null);   // 无视频可处理 → CPU 回落提示一并收起
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
            try { if (double.TryParse(await Task.Run(() => VideoService.ProbeFps(it.Path)), NumberStyles.Float, inv, out var pf) && pf > 0) srcFps = pf; } catch { }
            double? targetFps = SelectedTargetFps();
            bool interp = InterpToggle.IsChecked == true;
            int interpScale = AlhPro.Core.InterpScaleMap.Multiplier(CurrentScaleIndex());
            // 【补帧输出帧率基准已删除,固定"真实时间轴"】这里的预估帧率口径 = 源帧率 × 补帧倍率
            // (内部 fpsMode=2,即原来的"真实时间轴插值"档)。原先那个"匀速档"(内容帧率×倍率)的分支
            // 随下拉控件一起删除 —— 它只会在用户手动切档时生效,而那个档只会让输出节奏与原片不一致。
            double baseFps = srcFps ?? 0;
            double outFps = targetFps ?? (baseFps * (interp ? interpScale : 1));
            // 组装文本
            var parts = new System.Collections.Generic.List<string>();
            if (ow > 0 && oh > 0)
            {
                // 统一写「输出: W×H(源 w×h ×倍数)」——让超分倍率直接体现在分辨率里,不再另外写"Nx超分"。
                string srcNote = "";
                if (sw > 0 && sh > 0)
                {
                    if (up && customRes) srcNote = $"(源 {sw}×{sh} · 自定义)";
                    else if (up && shrink1x) srcNote = $"(源 {sw}×{sh} ×1·缩回)";
                    else if (up) srcNote = $"(源 {sw}×{sh} ×{mult:0.##})";
                    else srcNote = $"(源 {sw}×{sh})";
                }
                parts.Add($"输出: {ow}×{oh}{srcNote}");
            }
            else if (sw > 0 && sh > 0)
                parts.Add($"输出: 保持 {sw}×{sh}");
            if (outFps > 0)
            {
                // 【匀速档已删】提示里不再有"(…x补帧·匀速)"分支:输出基准固定"真实时间轴"。
                var fpsNote = targetFps != null ? "(指定)"
                    : interp ? $"({interpScale}x补帧)" : "";
                parts.Add($"{outFps:0.##}fps{fpsNote}");
            }
            if (parts.Count == 0)
            {
                VideoOutSpecText.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                // 输出规格没有可显示的内容,但"会落到 CPU"这件事仍然成立 → 照样提示(信息不足时不显示数字)
                RefreshCpuFallbackHint(it.Duration, srcFps ?? 0, sw, sh);
                return;
            }
            // 超限提示:按【总像素】判定是否超 4K(3840×2160≈829万像素),不按单边——避免宽高比极端的视频误报。
            bool over4k = (ow > 0 && oh > 0) && (double)ow * oh > 3840.0 * 2160.0;
            // 倍率已写进「输出: W×H(源 w×h ×N)」,这里不再重复;"1x缩回/自定义"也已并入分辨率项。
            var text = string.Join(" · ", parts);
            // 占用估算:临时帧峰值(与 C3 临时盘预检同口径:放大帧 JPG + 1.6 倍余量)+ 成片大小。
            // 输出帧数 = dur × outFps;输出单帧 JPG 按像素从 1080p(≈1MB)线性缩放,放大内容更平滑所以压到 0.18。
            string sizeNote = "";
            double dur = it.Duration;
            if (dur > 0 && ow > 0 && oh > 0 && outFps > 0)
            {
                long outFrames = (long)Math.Ceiling(dur * outFps);
                double tempFrameMB = Math.Max(0.5, 1.0 * ((double)ow * oh) / (1920.0 * 1080.0) * 0.18);
                double tempGB = outFrames * tempFrameMB * 1.6 / 1024.0;
                // 成片:H.264 CRF~22「自动」≈0.1 bit/像素/帧(1080p30≈8Mbps 的公认粗估)。
                // 自定义码率(Mbps)时直接用它的目标码率;否则按输出像素×帧率×0.1 估,再按码率档微调。
                double bpp = 0.10;
                double bitrateMbps = 0;
                if (QualityCombo.SelectedIndex == 5)
                {
                    double.TryParse(BitrateBox.Text, NumberStyles.Float, inv, out var bm);
                    bitrateMbps = bm > 0 ? bm : 0;
                }
                if (bitrateMbps <= 0)
                {
                    double br = bpp * ((double)ow * oh) * outFps / 1e6;   // bpp × 像素 × 帧率 / 1e6 = Mbps
                    // 码率档微调:0自动 1低 2中(默认) 3高 4极高 —— 只做量级修正,不承诺精确
                    double qf = QualityCombo.SelectedIndex switch { 1 => 0.7, 2 => 1.0, 3 => 1.4, 4 => 2.0, _ => 1.0 };
                    bitrateMbps = br * qf;
                }
                // Mbps × 秒 / 8 = MB(每秒多少 MB)
                double exportMB = bitrateMbps * dur / 8.0;
                sizeNote = $" · 临时帧≈{tempGB:0.#}GB · 成片≈{(exportMB >= 1024 ? exportMB / 1024.0 : exportMB):0.##}{(exportMB >= 1024 ? "GB" : "MB")}";
            }
            VideoOutSpecText.Text = (over4k ? "⚠ " : "") + text + sizeNote + (over4k ? "  ⚠ 超4K,可能很慢/占大量空间" : "");
            VideoOutSpecText.Foreground = over4k
                ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 229, 72, 77))   // 红
                : (Microsoft.UI.Xaml.Media.SolidColorBrush?)null;   // 恢复默认
            VideoOutSpecText.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            // 【第 3 项】同一处顺带刷新"超分将落到 CPU"的红字(用同一份实测尺寸/帧率/时长,不多探一次)
            RefreshCpuFallbackHint(dur, srcFps ?? 0, sw, sh);
        }
        catch { VideoOutSpecText.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed; }
    }

    // ---------- 预览页 / 裁剪页(2026-09-17 拆成两页:双击=裁剪(只有裁剪),「预览效果」=预览(只有预览)) ----------
    private VideoItem? _previewItem;
    private bool _trimMode;      // true=裁剪页 false=预览页

    /// <summary>两页共用的外壳:素材名/信息、装载原片、复位版式与视图。</summary>
    private void ShowOverlayShell(VideoItem item)
    {
        _previewItem = item;
        PreviewName.Text = item.Name;
        if (PreviewMeta != null) PreviewMeta.Text = string.IsNullOrWhiteSpace(item.BaseInfo) ? "" : item.BaseInfo;
        PreviewPlayer.Source = MediaSource.CreateFromUri(new Uri(item.Path));
        PreviewPlayerHost.Visibility = Visibility.Visible;
        _effZoomed = false;
        _compareMode = false;
        _dividerDrag = false;
        VideoPreviewOverlay.Visibility = Visibility.Visible;   // 先显示,再算裁切(否则 ActualWidth=0 算不出来)
        ApplyPlayerClip();   // 显式整幅裁切(不能置 null:视频面上不生效,会留下上次对比的半幅裁切)
        if (CompareSplitter != null) CompareSplitter.Visibility = Visibility.Collapsed;
        SetCompareSync(false);
        if (PreviewViewRadios != null) PreviewViewRadios.SelectedIndex = 0;
        try { PreviewPlayer.AreTransportControlsEnabled = true; } catch { }
        try { PreviewPlayer.TransportControls.Visibility = Visibility.Collapsed; } catch { }
        _previewCtrlTimer?.Stop();
        // 重复帧预览:显示已有的(预估/分析)结果,未分析则提示
        DupSummaryText.Text = string.IsNullOrEmpty(item.DupSummary) ? "(点「分析重复帧」看精细分布)" : item.DupSummary;
        RenderDupStrip(item);
    }

    /// <summary>双击视频 → 裁剪页:只有裁剪(时间线首尾两把手 + 拖动时画面跟着跳)。</summary>
    private void OpenTrimPage(VideoItem item)
    {
        _trimMode = true;
        ShowOverlayShell(item);
        // 裁剪页:清掉对比留下的裁切/分割线/自绘播放条,避免"画面只剩一半"(用户真机反馈)
        _compareMode = false;
        _dividerDrag = false;
        SetCompareSync(false);
        ApplyPlayerClip();
        if (CompareSplitter != null) CompareSplitter.Visibility = Visibility.Collapsed;
        if (CompareBar != null) CompareBar.Visibility = Visibility.Collapsed;
        try { PreviewPlayer.AreTransportControlsEnabled = true; } catch { }
        _dupOpen = false;
        ApplyDeckVisibility();
        // 只看原片(对比/结果都收起来)
        try { EffectPlayer.MediaPlayer?.Pause(); } catch { }
        EffectPlayerHost.Visibility = Visibility.Collapsed;
        if (EffectPlayerHint != null) EffectPlayerHint.Visibility = Visibility.Collapsed;
        // 裁剪范围:优先用已经应用过的
        _trimStart = item.TrimStart;
        _trimEnd = item.TrimEnd > 0 ? item.TrimEnd : item.Duration;
        _duration = item.Duration;
        if (_duration <= 0)
        {
            TrimInfo.Text = "加载时长...";
            _ = LoadDurationAsync(item);
        }
        else
        {
            BuildTrimTicks();
            UpdateTrimUI();
            SeekSourceTo(_trimStart, true);
        }
    }

    /// <summary>「预览效果」→ 预览页:只有预览(视图三选一 + 时间线 + 一行动作)。</summary>
    private async Task OpenPreviewPageAsync(VideoItem item)
    {
        _trimMode = false;
        ShowOverlayShell(item);
        _dupOpen = false;
        ApplyDeckVisibility();
        EffectPlayerHost.Visibility = Visibility.Collapsed;
        if (EffectPlayerHint != null) EffectPlayerHint.Visibility = Visibility.Visible;
        await InitEffectForAsync(item);
    }

    private async Task LoadDurationAsync(VideoItem item)
    {
        var dur = await VideoService.ProbeDurationSeconds(item.Path);
        if (dur <= 0) return;
        item.Duration = dur;
        _duration = dur;
        if (_trimEnd <= 0.1 || _trimEnd > dur) _trimEnd = dur;
        BuildTrimTicks();
        UpdateTrimUI();
    }

    private void PreviewClose_Click(object sender, RoutedEventArgs e)
    {
        PreviewPlayer.MediaPlayer?.Pause();
        PreviewPlayer.Source = null;
        ApplyPlayerClip();
        VideoPreviewOverlay.Visibility = Visibility.Collapsed;
        _previewCtrlTimer?.Stop();
        DupStrip.Children.Clear();
        DupSummaryText.Text = "";
        // 预览任务:关掉整个页面时一起收走(正在跑就先取消,别留个看不见的任务在后台)
        if (_effBusy) { try { _effCts?.Cancel(); } catch { } }
        _effZoomed = false;
        _dupOpen = false;
        _compareMode = false;
        _dividerDrag = false;
        SetCompareSync(false);
        if (CompareSplitter != null) CompareSplitter.Visibility = Visibility.Collapsed;
        CleanupEffectOutput();
        _previewItem = null;
    }

    // ---------- 处理效果预览:用左侧当前参数真跑一小段,结果就在同一个画面里播 ----------
    // 为什么是"真跑":预览走的就是 RunBatchAsync(= 「开始处理」那条流水线、那份参数快照),
    // 只有区间/输出位置/收尾不同。所以预览里看到的清晰度、补帧、去重、后处理,就是全片会得到的结果。
    private const double EffLenMin = 2.0;      // 预览长度下限(秒)
    private const double EffLenMax = 15.0;     // 预览长度上限(秒)
    private const double EffLenDefault = 3.0;  // 默认长度(秒,1080p+2x超分+补帧约 1~3 分钟)
    private const double EffTimelinePad = 6;  // 时间线两端留白(给把手,裁剪/预览两条时间线同一手感)
    private double _effStart;                  // 预览起点(秒,整片任意位置)
    private double _effLen = EffLenDefault;    // 预览长度(秒,2~15)
    private double _effDuration;               // 当前素材时长(0=未知)
    private double _effRealLen;                // 实际会截取的长度(受素材剩余时长限制)
    private double _effTimelineSpan = 30;      // 时间线代表的秒数(时长未知时先按 30 秒铺)
    private bool _effTooShort;                 // 素材太短,连最短的一段都截不出来
    private bool _effBusy;                     // 正在生成预览
    private bool _effZoomed;                   // 放大画面:收起控制区,画面占满整页
    private bool _viewSyncing;                 // 视图单选 ↔ ApplyPreviewView 防重入
    private bool _effHasResult;                // 已经跑出过成片(按钮文案变「重新预览」)
    private bool _effDirty;                    // 左侧参数改过 → 现有结果已过期,提示重新预览
    private int _ptlDrag;                      // 预览时间线正在拖谁:0=没拖 1=起点游标 2=右端把手
    private long _lastScrubTick;               // 定位节流(MediaPlayer 定位较贵)
    // 左右对比:分割线位置(0~1)+ 两边同步播放
    private bool _compareMode;
    private double _compareSplit = 0.5;
    private bool _dividerDrag;
    private bool _compareSyncOn;
    private bool _cmpSeekSync;                 // 自绘进度条 ↔ 播放位置 防重入
    private long _cmpUiTick;                   // 对比条刷新节流
    private long _splitterTouchTick;           // 刚拖过分割线的时刻(点画面播停要避开这一下)
    private Microsoft.UI.Xaml.Media.RectangleGeometry? _playerClip;   // 复用同一个裁切对象(避免每次 new 引起重采样抖动)
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _cmpWatchdog;   // 对比模式:UI 线程看门狗(约束原片)
    private CancellationTokenSource? _effCts;  // 预览取消(与主界面「强制结束」等效)
    private string? _effOutPath;               // 上次预览的临时成片(换预览/离开预览页时删除)
    private string? _effStarted;               // 已经起播过的成片路径(防重复起播)
    private Windows.Foundation.TypedEventHandler<Windows.Media.Playback.MediaPlayer, object>? _effOpenedHandler;
    private Windows.Foundation.TypedEventHandler<Windows.Media.Playback.MediaPlaybackSession, object>? _cmpPosHandler;
    private Windows.Foundation.TypedEventHandler<Windows.Media.Playback.MediaPlaybackSession, object>? _cmpStateHandler;

    /// <summary>四个面板的可见性只在这里决定:裁剪页=裁剪区(+重复帧可选)、预览页=预览区;放大=全收起。</summary>
    private void ApplyDeckVisibility()
    {
        try
        {
            if (TrimDeck != null) TrimDeck.Visibility = (!_effZoomed && _trimMode) ? Visibility.Visible : Visibility.Collapsed;
            if (PreviewDeck != null) PreviewDeck.Visibility = (!_effZoomed && !_trimMode) ? Visibility.Visible : Visibility.Collapsed;
            if (DupToolsPanel != null) DupToolsPanel.Visibility = (!_effZoomed && _trimMode && _dupOpen) ? Visibility.Visible : Visibility.Collapsed;
            if (DupToggleBtn != null)
            {
                DupToggleBtn.Visibility = _trimMode ? Visibility.Visible : Visibility.Collapsed;
                DupToggleBtn.Content = _dupOpen ? "重复帧 ▴" : "重复帧 ▾";
            }
            if (PreviewViewRadios != null)
                PreviewViewRadios.Visibility = (!_effZoomed && !_trimMode) ? Visibility.Visible : Visibility.Collapsed;
            if (EffectZoomBtn != null) EffectZoomBtn.Content = _effZoomed ? "还原" : "放大画面";
        }
        catch { }
    }

    /// <summary>「预览效果」按钮:打开预览页(默认把视频设成右侧选中的那个)。</summary>
    private async void RunPreviewBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_running || _runItems != null || _effBusy)
        {
            await ShowPauseHintAsync("正在处理/预览中 —— 请等这一段跑完再试。");
            return;
        }
        if (_videos.Count == 0)
        {
            await ShowPauseHintAsync("右侧还没有视频 —— 请先把要处理的视频拖进来。");
            return;
        }
        // 优先用右侧列表里选中的那个;一个都没选就取第一个还没处理的(页面里还能随时换)
        var pick = VideoList.SelectedItems.OfType<VideoItem>().FirstOrDefault()
                   ?? _videos.FirstOrDefault(v => !v.IsDone) ?? _videos[0];
        PruneStalePreviewFiles();   // 清掉上次异常退出(强杀/断电)留下的预览临时文件
        if (!ReferenceEquals(EffectVideoCombo.ItemsSource, _videos))
            EffectVideoCombo.ItemsSource = _videos;
        // 赋值会触发 EffectVideoCombo_SelectionChanged(读时长 + 重排时间线),不用在这里重复初始化
        if (!ReferenceEquals(EffectVideoCombo.SelectedItem, pick)) EffectVideoCombo.SelectedItem = pick;
        await OpenPreviewPageAsync(pick);
        Log($"已打开预览页:{pick.Name} —— 拖时间线选位置(2~15 秒一段),再点「开始预览」");
    }

    private async void EffectVideoCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EffectVideoCombo.SelectedItem is VideoItem it && !_trimMode) await InitEffectForAsync(it);
    }

    /// <summary>切换素材:读时长(未知时探测)→ 重排时间线、区间夹回可用范围。</summary>
    private async Task InitEffectForAsync(VideoItem item)
    {
        _effDuration = item.Duration;
        if (_effDuration <= 0.1)
        {
            PreviewDeckStatus("正在读取素材时长…");
            try
            {
                var d = await VideoService.ProbeDurationSeconds(item.Path);
                if (d > 0.1) { _effDuration = d; if (item.Duration <= 0.1) item.Duration = d; }
            }
            catch { }
        }
        _effStart = 0;
        _effLen = EffLenDefault;
        ClampEffRange();
        BuildPrevTicks();
        UpdatePreviewDeck();
        if (PreviewMeta != null) PreviewMeta.Text = string.IsNullOrWhiteSpace(item.BaseInfo) ? "" : item.BaseInfo;
        PreviewDeckStatus(_effTooShort
            ? $"这段素材太短(不到 {EffLenMin + 0.5:0.#} 秒),截不出预览片段 —— 直接「开始处理」整段即可。"
            : "拖时间线选位置(蓝线=起点,白把手=这一段多长),然后点「开始预览」:用左侧当前参数真跑这一段。");
    }

    /// <summary>把起点/长度夹进可用范围:起点 0 ~ (时长-2);长度 2~15 且不超出剩余时长。</summary>
    private void ClampEffRange()
    {
        double dur = _effDuration;
        _effTimelineSpan = dur > 0.1 ? dur : 30;
        _effTooShort = dur > 0.1 && dur < EffLenMin + 0.5;
        if (dur > 0.1) _effStart = Math.Clamp(_effStart, 0, Math.Max(0, dur - EffLenMin));
        else _effStart = Math.Max(0, _effStart);
        double avail = dur > 0.1 ? Math.Max(0, dur - _effStart) : double.MaxValue;
        _effLen = Math.Clamp(_effLen, EffLenMin, EffLenMax);
        if (avail < double.MaxValue) _effLen = Math.Min(_effLen, Math.Max(EffLenMin, avail));
        _effRealLen = Math.Min(_effLen, avail);
    }

    /// <summary>设置起点(秒)。seek=true 时让原片跟着跳到那一帧(拖动时间线的手感)。</summary>
    private void SetEffStart(double seconds, bool seek)
    {
        double old = _effStart;
        _effStart = seconds;
        ClampEffRange();
        if (_effHasResult && !_effBusy && Math.Abs(_effStart - old) > 0.001) _effDirty = true;   // 区间改过 → 结果已过期
        UpdatePreviewDeck();
        if (seek && Math.Abs(_effStart - old) > 0.001) SeekSourceTo(_effStart, false);
    }

    private void SetEffLen(double seconds)
    {
        _effLen = seconds;
        ClampEffRange();
        if (_effHasResult && !_effBusy) _effDirty = true;   // 区间改过 → 现有结果已过期
        UpdatePreviewDeck();
    }

    /// <summary>让原片跳到指定秒。force=true 立即定位(松手/点击时用),否则按 200ms 节流(拖动中画面按这个节奏刷新)。</summary>
    private void SeekSourceTo(double seconds, bool force)
    {
        long now = Environment.TickCount64;
        if (!force && now - _lastScrubTick < 200) return;
        _lastScrubTick = now;
        try
        {
            var mp = PreviewPlayer.MediaPlayer;
            if (mp == null) return;
            mp.Pause();   // 拖动时暂停,像剪辑软件一样"擦洗"
            mp.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Max(0, seconds));
        }
        catch { }
    }

    /// <summary>刷新控制区的数字/高亮/按钮文案,并重排时间线。</summary>
    private void UpdatePreviewDeck()
    {
        if (EffectRunBtn == null || PrevTimeline == null) return;
        if (EffectStartValueText != null) EffectStartValueText.Text = EffTime(_effStart);
        string avail = _effDuration > 0.1
            ? $"预览 {EffTime(_effStart)} ~ {EffTime(_effStart + _effRealLen)} · 共 {_effRealLen:0.#} 秒"
            : $"预览 {EffTime(_effStart)} 起 {_effRealLen:0.#} 秒";
        string note = _effDuration > 0.1 && _effDuration < EffLenMax + 0.5
            ? $" · 素材只有 {_effDuration:0.#} 秒,这一段就是整段"
            : (_effRealLen < _effLen - 0.05 ? $" · 素材只剩 {_effRealLen:0.#} 秒" : "");
        if (EffectRangeText != null) EffectRangeText.Text = avail + note;
        EffectRunBtn.Content = _effHasResult ? "重新预览" : "开始预览";
        EffectRunBtn.IsEnabled = !_effBusy && !_effTooShort && _effRealLen >= EffLenMin - 0.01;
        if (SavePreviewBtn != null) SavePreviewBtn.IsEnabled = _effHasResult && !_effBusy && !string.IsNullOrEmpty(_effOutPath);
        if (EffectDirtyHint != null)
            EffectDirtyHint.Visibility = (_effDirty && _effHasResult && !_effBusy) ? Visibility.Visible : Visibility.Collapsed;
        UpdateLenChipVisual();
        LayoutPrevTimeline();
    }

    /// <summary>长度快选按钮的高亮(当前长度所在的那个用强调色)。</summary>
    private void UpdateLenChipVisual()
    {
        var accent = Application.Current.Resources.TryGetValue("AccentButtonStyle", out var s) && s is Style st ? st : null;
        foreach (var b in new[] { EffectLenChip2, EffectLenChip3, EffectLenChip5, EffectLenChip10, EffectLenChip15 })
        {
            if (b == null) continue;
            bool on = double.TryParse(b.Tag as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                      && Math.Abs(v - _effLen) < 0.05;
            b.Style = on ? accent : null;
        }
    }

    /// <summary>把「起点/长度」画到预览时间线上:亮区=这一段,蓝线=起点,白把手=右端。</summary>
    private void LayoutPrevTimeline()
    {
        try
        {
            if (PrevTimeline == null || PrevRangeBar == null || PrevHeadGrip == null || PrevEndGrip == null) return;
            double w = PrevTimeline.ActualWidth - EffTimelinePad * 2;
            if (w <= 4) return;
            double span = Math.Max(0.5, _effTimelineSpan);
            double pps = w / span;
            double X(double t) => EffTimelinePad + Math.Clamp(t, 0, span) * pps;
            PrevRangeBar.Margin = new Thickness(X(_effStart), 20, 0, 0);
            PrevRangeBar.Width = Math.Max(3, _effRealLen * pps);
            PrevHeadGrip.Margin = new Thickness(X(_effStart) - 10, 0, 0, 0);
            PrevEndGrip.Margin = new Thickness(X(_effStart + _effRealLen) - 7, 16, 0, 0);
        }
        catch { }
    }

    /// <summary>时间线刻度:按宽度铺 6~10 个标签(时长越长,间隔越大)。裁剪/预览两条线共用。</summary>
    private static void BuildTicksInto(Canvas canvas, double width, double span)
    {
        try
        {
            if (canvas == null) return;
            canvas.Children.Clear();
            double w = width - EffTimelinePad * 2;
            if (w <= 40) return;
            span = Math.Max(0.5, span);
            double[] steps = { 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600 };
            double step = steps.FirstOrDefault(s => span / s <= 8, 3600);
            for (double t = 0; t <= span + 0.001; t += step)
            {
                var tb = new TextBlock { Text = EffTime(t), FontSize = 10, Opacity = 0.5 };
                Canvas.SetLeft(tb, EffTimelinePad + t / span * w - 14);
                Canvas.SetTop(tb, 0);
                canvas.Children.Add(tb);
            }
        }
        catch { }
    }

    private void BuildPrevTicks()
    {
        if (PrevTimeline == null || PrevTicks == null) return;
        BuildTicksInto(PrevTicks, PrevTimeline.ActualWidth, _effTimelineSpan);
    }

    private void PrevTimeline_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        BuildPrevTicks();
        LayoutPrevTimeline();
    }

    private void PrevTimeline_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (PrevTimeline == null || _effTooShort) return;
        try
        {
            double x = e.GetCurrentPoint(PrevTimeline).Position.X;
            double span = Math.Max(0.5, _effTimelineSpan);
            double w = PrevTimeline.ActualWidth - EffTimelinePad * 2;
            if (w <= 4) return;
            double pps = w / span;
            double headX = EffTimelinePad + _effStart * pps;
            double endX = EffTimelinePad + (_effStart + _effRealLen) * pps;
            // 命中判定:先看是不是抓把手/游标,否则"点哪跳哪"(剪辑软件的常规手感)
            if (Math.Abs(x - endX) <= 16) _ptlDrag = 2;
            else if (Math.Abs(x - headX) <= 16) _ptlDrag = 1;
            else
            {
                _ptlDrag = 1;
                SetEffStart((x - EffTimelinePad) / pps, true);
            }
            PrevTimeline.CapturePointer(e.Pointer);
            PrevTimeline.Focus(FocusState.Pointer);
        }
        catch { }
    }

    private void PrevTimeline_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_ptlDrag == 0) return;
        try
        {
            double x = e.GetCurrentPoint(PrevTimeline).Position.X;
            double span = Math.Max(0.5, _effTimelineSpan);
            double w = PrevTimeline.ActualWidth - EffTimelinePad * 2;
            if (w <= 4) return;
            double t = Math.Clamp((x - EffTimelinePad) / (w / span), 0, span);
            if (_ptlDrag == 1) SetEffStart(t, true);
            else SetEffLen(Math.Round(Math.Max(EffLenMin, t - _effStart) * 10) / 10);   // 长度 0.1 秒步进
        }
        catch { }
    }

    private void PrevTimeline_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_ptlDrag == 1) SeekSourceTo(_effStart, true);
        _ptlDrag = 0;
        try { PrevTimeline?.ReleasePointerCapture(e.Pointer); } catch { }
    }

    private static string EffTime(double s)
        => TimeSpan.FromSeconds(Math.Max(0, s)).ToString(@"mm\:ss\.f");

    /// <summary>时间线上的键盘微调:←/→ 一帧,PageUp/PageDown 一秒(剪辑软件的常规手感)。</summary>
    private void PrevTimeline_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        double frame = 1.0 / EffFrameRate();
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Left: SetEffStart(_effStart - frame, true); e.Handled = true; break;
            case Windows.System.VirtualKey.Right: SetEffStart(_effStart + frame, true); e.Handled = true; break;
            case Windows.System.VirtualKey.PageUp: SetEffStart(_effStart - 1.0, true); e.Handled = true; break;
            case Windows.System.VirtualKey.PageDown: SetEffStart(_effStart + 1.0, true); e.Handled = true; break;
        }
    }

    /// <summary>素材帧率(用于 ←/→ 一帧步进);探不到就按 30fps 算。</summary>
    private double EffFrameRate()
    {
        try
        {
            if (double.TryParse(_previewItem?.FpsProbe, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f > 1)
                return f;
        }
        catch { }
        return 30.0;
    }

    /// <summary>起点 ±0.1 秒(精确微调,拖动很难拖到 0.1 秒)。</summary>
    private void EffectStartNudge_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string ts) return;
        if (!double.TryParse(ts, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return;
        SetEffStart(Math.Round((_effStart + d) * 10) / 10, true);
    }

    /// <summary>长度快选(2/3/5/10/15 秒)。</summary>
    private void EffectLenChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string ts) return;
        if (!double.TryParse(ts, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return;
        SetEffLen(v);
    }

    /// <summary>视图三选一:0=看原片 1=看处理效果 2=左右对比。</summary>
    private void PreviewViewRadios_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewSyncing) return;
        ApplyPreviewView();
    }

    private void ApplyPreviewView()
    {
        try
        {
            if (_trimMode) return;   // 裁剪页永远只看原片
            int mode = PreviewViewRadios?.SelectedIndex ?? 0;
            bool hasResult = _effHasResult || !string.IsNullOrEmpty(_effOutPath);
            _compareMode = mode == 2;
            PreviewPlayerHost.Visibility = mode == 1 ? Visibility.Collapsed : Visibility.Visible;
            EffectPlayerHost.Visibility = mode == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (CompareSplitter != null) CompareSplitter.Visibility = _compareMode ? Visibility.Visible : Visibility.Collapsed;
            if (CmpLabelLeft != null) CmpLabelLeft.Visibility = _compareMode ? Visibility.Visible : Visibility.Collapsed;
            if (CmpLabelRight != null) CmpLabelRight.Visibility = _compareMode ? Visibility.Visible : Visibility.Collapsed;
            if (CompareBar != null)
            {
                if (_compareMode) ShowCompareBarTemporarily();   // 进对比:淡入 + 起 2.5 秒计时
                else
                {
                    try { _cmpBarTimer?.Stop(); } catch { }
                    CompareBar.Visibility = Visibility.Collapsed;
                    CompareBar.Opacity = 0;
                    CompareBar.IsHitTestVisible = false;
                }
            }
            // 提示文字在【最底层】:没有结果时把结果区也收起来,免得空播放器(黑底)把提示盖住
            if (EffectPlayerHint != null)
            {
                if (mode == 0 || hasResult) EffectPlayerHint.Visibility = Visibility.Collapsed;
                else
                {
                    EffectPlayerHint.Text = mode == 2
                        ? "这一半会显示处理后的效果\n先点下面的「开始预览」跑出结果,再回来看对比"
                        : "点下面「开始预览」,处理结果会在这里播放";
                    EffectPlayerHint.Visibility = Visibility.Visible;
                }
            }
            // 结果还没生成时,结果区不显示(否则那半会是一块黑底,把底层提示盖掉)
            if (!hasResult && mode != 0) EffectPlayerHost.Visibility = Visibility.Collapsed;
            ApplyPlayerClip();
            SetCompareSync(_compareMode);
            // 对比模式:两个播放器自带的播放键都关掉,只用下面那条自绘控件(用户反馈"两套播放键"就是这个)
            try { PreviewPlayer.AreTransportControlsEnabled = !_compareMode; } catch { }
            try { EffectPlayer.AreTransportControlsEnabled = !_compareMode; } catch { }
            if (_compareMode) PreviewPlayer.TransportControls.Visibility = Visibility.Collapsed;
            // 藏起来的那个必须暂停,否则两路声音一起响
            if (mode == 0) EffectPlayer.MediaPlayer?.Pause();
            else PreviewPlayer.MediaPlayer?.Pause();
            // 【2026-09-17 用户反馈:切换"看原片/看处理效果"时被调回开头】——切换视图**不许动任何位置**:
            // 两个播放器各自保持自己的位置(它们本来就是同一时刻),按播放时才由 AlignAndPlayAsync 对齐。
            if (_compareMode) RefreshCompareBar();
            // 【2026-09-17 用户反馈:切换后看到的是不同画面】切换时把【即将显示的那个】播放器
            // 对到【另一个的当前位置】(不是回到预览起点)——这样两个视图永远同一画面。
            if (hasResult) _ = AlignModeSwitchAsync(mode);
        }
        catch { }
    }

    /// <summary>
    /// 进对比模式时先把自绘播放条读一次:片段播完后不再有位置回调,
    /// 不读一次就会一直显示 0:00/0:00(用户看到的就是"播放面板没反应")。
    /// </summary>
    private void RefreshCompareBar()
    {
        try
        {
            if (CmpSeek == null) return;
            var se = EffectPlayer.MediaPlayer?.PlaybackSession;
            double pos = se?.Position.TotalSeconds ?? 0;
            double d = se?.NaturalDuration.TotalSeconds ?? 0;
            _cmpSeekSync = true;
            try
            {
                CmpSeek.Value = d > 0.05 ? Math.Clamp(pos / d * 100, 0, 100) : 0;
                if (CmpTime != null) CmpTime.Text = $"{EffTime(pos)} / {EffTime(d)}";
                SetCmpPlayGlyph(se != null && se.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing);
            }
            finally { _cmpSeekSync = false; }
        }
        catch { }
    }

    /// <summary>
    /// 对比模式的看门狗(UI 线程,150ms 一跳)。为什么要有它:
    /// 媒体回调跑在媒体线程上、又容易在播放/暂停切换时漏掉,结果就是"左边原片会突破预览区间把整片播完"
    /// (用户真机反馈)。这里在 UI 线程上定期读一次真实状态并强制约束,行为可预期、也不依赖回调是否送到:
    ///   ① 片段在播 → 原片跟着播,漂移超 0.25 秒就定位回去;
    ///   ② 片段停了 / 播到末尾 → 原片立刻暂停(绝不越过区间);
    ///   ③ 播放条同步刷新。
    /// </summary>
    private void EnsureCompareWatchdog(bool on)
    {
        try
        {
            if (on)
            {
                _cmpWatchdog ??= DispatcherQueue.CreateTimer();
                _cmpWatchdog.Interval = TimeSpan.FromMilliseconds(150);
                _cmpWatchdog.IsRepeating = true;
                _cmpWatchdog.Tick -= CmpWatchdog_Tick;
                _cmpWatchdog.Tick += CmpWatchdog_Tick;
                _cmpWatchdog.Start();
            }
            else _cmpWatchdog?.Stop();
        }
        catch { }
    }

    private void CmpWatchdog_Tick(object? sender, object e)
    {
        if (!_compareMode) { _cmpWatchdog?.Stop(); return; }
        try
        {
            var eff = EffectPlayer.MediaPlayer;
            var se = eff?.PlaybackSession;
            var om = PreviewPlayer.MediaPlayer;
            var os = om?.PlaybackSession;
            if (se == null || om == null || os == null) return;
            double clipPos = se.Position.TotalSeconds;
            double clipDur = se.NaturalDuration.TotalSeconds;
            bool clipPlaying = se.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing;
            bool clipAtEnd = clipDur > 0.05 && clipPos >= clipDur - 0.03;
            double want = _effStart + (clipDur > 0.05 ? Math.Min(clipPos, clipDur) : clipPos);
            if (_duration > 0.1) want = Math.Min(want, Math.Max(0, _duration - 0.05));
            bool origPlaying = os.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing;
            // 【2026-09-17 用户反馈:左边还是多跑一段】边界改用【这次预览的时长】(_effRealLen,滑块上那个长度)——
            // 播放器报的 NaturalDuration 有时读不到,一读不到保险就失效。越过边界立刻暂停并拉回。
            double boundLen = _effRealLen > 0.3 ? _effRealLen : clipDur;
            if (boundLen > 0.05 && os.Position.TotalSeconds > _effStart + boundLen + 0.15)
            {
                om.Pause();
                try { os.Position = TimeSpan.FromSeconds(_effStart + boundLen); } catch { }
                origPlaying = false;
            }
            if (clipPlaying && !clipAtEnd)
            {
                // 只保证"原片别停":对齐在按下播放时做(AlignAndPlayAsync)。
                // 【真机教训】这里如果持续 seek 纠偏,会把媒体管线拖垮 —— 实测片段卡在 1.8 秒播不动。
                if (!origPlaying) om.Play();
            }
            else if (origPlaying) om.Pause();   // ② 越界/片段停了 → 立刻停住原片
            // ③ 播放条
            if (CmpSeek != null)
            {
                _cmpSeekSync = true;
                try
                {
                    if (clipDur > 0.05) CmpSeek.Value = Math.Clamp(clipPos / clipDur * 100, 0, 100);
                    if (CmpTime != null) CmpTime.Text = $"{EffTime(clipPos)} / {EffTime(clipDur > 0 ? clipDur : 0)}";
                }
                finally { _cmpSeekSync = false; }
            }
            SetCmpPlayGlyph(clipPlaying);
        }
        catch { }
    }

    /// <summary>把界面更新切回 UI 线程(媒体回调跑在媒体线程上,直接碰控件会抛异常)。</summary>
    private void OnUiThread(Action action)
    {
        try
        {
            var dq = DispatcherQueue;
            if (dq == null || dq.HasThreadAccess) { action(); return; }
            dq.TryEnqueue(() => { try { action(); } catch { } });
        }
        catch { }
    }

    // ---------- 对比模式控制条:淡入淡出 + 播放中自动隐藏(业界惯例,参考 Video.js 的 show/hide controls)
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _cmpBarTimer;   // 自动隐藏计时(2.5 秒,用户指定)

    /// <summary>淡入(180ms)/淡出(300ms)用透明度动画 —— 不再用 Visibility 硬切(那是"咔"一下,也是用户看到的"渐显一下")。</summary>
    private void FadeCompareBar(bool show)
    {
        try
        {
            if (CompareBar == null) return;
            if (show)
            {
                CompareBar.Visibility = Visibility.Visible;
                CompareBar.IsHitTestVisible = true;
            }
            var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            var da = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                To = show ? 1 : 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(show ? 180 : 300)),
                EnableDependentAnimation = true,
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(da, CompareBar);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(da, "Opacity");
            sb.Children.Add(da);
            if (!show)
            {
                // 淡出完成后才真正收起来(且此时不接受点击,免得点到看不见的按钮)
                sb.Completed += (_, _) =>
                {
                    try
                    {
                        if (CompareBar.Opacity < 0.05) { CompareBar.Visibility = Visibility.Collapsed; CompareBar.IsHitTestVisible = false; }
                    }
                    catch { }
                };
            }
            sb.Begin();
        }
        catch { }
    }

    /// <summary>有交互(鼠标移动 / 暂停 / 拖进度)就显示控制条,并重置 2.5 秒自动隐藏计时。</summary>
    private void ShowCompareBarTemporarily()
    {
        FadeCompareBar(true);
        try
        {
            _cmpBarTimer ??= DispatcherQueue.CreateTimer();
            _cmpBarTimer.Interval = TimeSpan.FromMilliseconds(2500);   // 用户指定 2.5 秒
            _cmpBarTimer.IsRepeating = false;
            _cmpBarTimer.Tick -= CmpBarTimer_Tick;
            _cmpBarTimer.Tick += CmpBarTimer_Tick;
            _cmpBarTimer.Start();
        }
        catch { }
    }

    private void CmpBarTimer_Tick(object? sender, object e)
    {
        try
        {
            if (!_compareMode) { _cmpBarTimer?.Stop(); return; }
            bool playing = EffectPlayer.MediaPlayer?.PlaybackSession.PlaybackState
                == Windows.Media.Playback.MediaPlaybackState.Playing;
            if (playing) FadeCompareBar(false);       // 播放中 + 2.5 秒没动 → 淡出
            else ShowCompareBarTemporarily();         // 暂停中 → 常显(用户随时能找到播放键)
        }
        catch { }
    }

    /// <summary>鼠标在画面上有动作 → 控制条淡入(播放中则重新计时 2.5 秒)。</summary>
    private void PlayerArea_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_compareMode) return;
        ShowCompareBarTemporarily();
    }

    // ---------- 对比模式的自绘播放控件(一套,同时控制左右两边,盖在中线上面) ----------
    private void SetCmpPlayGlyph(bool playing)
    {
        try { if (CmpPlayIcon != null) CmpPlayIcon.Glyph = playing ? "\uE769" : "\uE768"; } catch { }
    }

    /// <summary>播放/暂停:结果片段与原片一起(用户要求"一个播放键管两边")。</summary>
    private void CmpPlayBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var eff = EffectPlayer.MediaPlayer;
            var se = eff?.PlaybackSession;
            if (eff == null || se == null) return;
            var origMp = PreviewPlayer.MediaPlayer;
            var orig = origMp?.PlaybackSession;
            if (se.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing)
            {
                eff.Pause();
                origMp?.Pause();
                SetCmpPlayGlyph(false);
            }
            else
            {
                if (se.NaturalDuration.TotalSeconds > 0.05 && se.Position >= se.NaturalDuration - TimeSpan.FromMilliseconds(80))
                    se.Position = TimeSpan.Zero;   // 播完了再点 = 重头播
                _ = AlignAndPlayAsync();   // 对齐两个画面后再同时播(否则左边会超前)
            }
        }
        catch { }
    }

    /// <summary>点画面 = 播放/暂停(像普通播放器一样;刚拖过分割线的那一下不算)。</summary>
    private void PlayerArea_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (!_compareMode) return;
        if (Environment.TickCount64 - _splitterTouchTick < 450) return;   // 刚拖分割线
        try
        {
            if (CompareBar != null && CompareBar.Visibility == Visibility.Visible)
            {
                var p = e.GetPosition(CompareBar);
                if (p.X >= 0 && p.X <= CompareBar.ActualWidth && p.Y >= 0 && p.Y <= CompareBar.ActualHeight)
                    return;   // 点在播放条上(按钮/进度条自己处理)
            }
        }
        catch { }
        CmpPlayBtn_Click(sender, e);
    }

    private void CmpSeek_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_cmpSeekSync || !_compareMode) return;
        try
        {
            var se = EffectPlayer.MediaPlayer?.PlaybackSession;
            if (se == null) return;
            double d = se.NaturalDuration.TotalSeconds;
            if (d <= 0.05) return;
            double t = Math.Clamp(CmpSeek.Value, 0, 100) / 100.0 * d;
            se.Position = TimeSpan.FromSeconds(t);
            SeekSourceTo(_effStart + t, true);   // 原片立即跟上(不等漂移判定)
        }
        catch { }
    }

    // ---------- 左右对比:左边原片 / 右边处理结果,中间一条可拖的竖线;两边同步播放 ----------
    private void PlayerArea_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyPlayerClip();

    private void ApplyCompareSplit() => ApplyPlayerClip();

    /// <summary>
    /// 画面裁切:对比模式下把原片裁到分割线左边(右边露出下面的处理结果);其它情况裁成"整幅"。
    /// 【真机踩坑】不能靠 `Clip = null` 取消:视频面(SwapChainPanel)上置 null 不生效,
    /// 结果离开对比后画面还留着上一次的裁切 —— 用户看到的就是"裁剪界面只有一半"。
    /// 所以这里永远显式赋一个新矩形(整幅 or 半幅),并在尺寸变化时重算。
    /// </summary>
    private void ApplyPlayerClip()
    {
        try
        {
            if (PreviewPlayerHost == null) return;
            double w = PlayerArea?.ActualWidth ?? 0, h = PlayerArea?.ActualHeight ?? 0;
            if (w <= 8 || h <= 8) return;
            double rawX = _compareMode ? Math.Clamp(_compareSplit, 0.03, 0.97) * w : w;
            // 【2026-09-17 用户反馈:拖分割线时左边原片有轻微缩放/抖动感】
            // 根因:分割线位置是小数像素,视频面按小数像素裁切会不停重采样/半像素插值 → 看着像在缩放。
            // 修法:裁切矩形取整到整像素,并复用同一个 RectangleGeometry 实例(不再每次 new)。
            double x = Math.Round(rawX);
            double hh = Math.Round(h);
            _playerClip ??= new Microsoft.UI.Xaml.Media.RectangleGeometry();
            _playerClip.Rect = new Windows.Foundation.Rect(0, 0, x, hh);
            PreviewPlayerHost.Clip = _playerClip;
            if (CompareSplitter != null)
                CompareSplitter.Margin = new Thickness(x - 14, 0, 0, 0);
        }
        catch { }
    }

    private void CompareSplitter_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_compareMode) return;
        _dividerDrag = true;
        _splitterTouchTick = Environment.TickCount64;
        try { CompareSplitter.CapturePointer(e.Pointer); } catch { }
        UpdateCompareFromPointer(e);
        e.Handled = true;
    }

    private void CompareSplitter_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_dividerDrag) return;
        _splitterTouchTick = Environment.TickCount64;
        UpdateCompareFromPointer(e);
        e.Handled = true;
    }

    private void CompareSplitter_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _dividerDrag = false;
        _splitterTouchTick = Environment.TickCount64;
        try { CompareSplitter?.ReleasePointerCapture(e.Pointer); } catch { }
    }

    private void UpdateCompareFromPointer(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        try
        {
            double w = PlayerArea.ActualWidth;
            if (w <= 40) return;
            _compareSplit = Math.Clamp(e.GetCurrentPoint(PlayerArea).Position.X / w, 0.03, 0.97);
            ApplyCompareSplit();
        }
        catch { }
    }

    /// <summary>
    /// 对比模式的同步:结果播放器是"主",原片跟着走 —— 位置差超过 0.18 秒就对齐,播放/暂停也跟随。
    /// 这样左右两边永远是同一时刻的画面,才比得出清晰度和补帧效果。
    /// </summary>
    private void SetCompareSync(bool on)
    {
        try
        {
            var mp = EffectPlayer.MediaPlayer;
            if (mp == null) return;
            if (on && !_compareSyncOn)
            {
                _cmpPosHandler = (s, _) =>
                {
                    // 【线程】MediaPlaybackSession.PositionChanged 在媒体线程上触发,直接改控件会抛
                    // "调用方线程已编组"这类异常(消息为空)→ 播放条永远不刷新、后面同步也走不到。
                    // 所以:取数据 → 界面更新一律 DispatcherQueue 回 UI 线程;媒体 API 直接调(线程无关)。
                    double clipPos, clipDur;
                    bool clipPlaying;
                    try
                    {
                        if (!_compareMode) return;
                        clipPos = s.Position.TotalSeconds;
                        clipDur = s.NaturalDuration.TotalSeconds;
                        clipPlaying = s.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing;
                    }
                    catch { return; }
                    // ① 自绘播放条(120ms 节流)
                    long now = Environment.TickCount64;
                    if (now - _cmpUiTick > 120)
                    {
                        _cmpUiTick = now;
                        OnUiThread(() =>
                        {
                            if (CmpSeek == null) return;
                            _cmpSeekSync = true;
                            try
                            {
                                if (clipDur > 0.05) CmpSeek.Value = Math.Clamp(clipPos / clipDur * 100, 0, 100);
                                if (CmpTime != null) CmpTime.Text = $"{EffTime(clipPos)} / {EffTime(clipDur > 0 ? clipDur : 0)}";
                            }
                            finally { _cmpSeekSync = false; }
                        });
                    }
                    // ② 原片同步。【2026-09-17 方案D(用户选定:不许有任何新增耗时)】
                    //    旧做法是"播放中 seek 纠偏" ✗ —— seek 会把媒体管线拖死(实测片段卡在 1.8 秒),
                    //    而且两边各自 seek 落地时刻不同 → "卡死 / 单边卡 / 时间对不上 / 滑动不协调"。
                    //    新做法:**只用播放速率微调**(最多 ±2%)把原片拉回片段的时间点 ——
                    //    零额外解码、零额外 seek、零额外耗时;偏差 ≤60ms 就恢复 1.0 倍速(不会一直微调)。
                    try
                    {
                        var origMp = PreviewPlayer.MediaPlayer;
                        var op = origMp?.PlaybackSession;
                        if (origMp != null && op != null)
                        {
                            bool origPlaying = op.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing;
                            bool clipAtEnd = clipDur > 0.05 && clipPos >= clipDur - 0.03;
                            if (clipPlaying && !clipAtEnd)
                            {
                                if (!origPlaying) origMp.Play();
                                // 硬保险:原片越界(超过 预览起点+预览时长+0.15s)才动一次 seek,把位置拉回边界。
                                double boundLen = _effRealLen > 0.3 ? _effRealLen : clipDur;
                                if (boundLen > 0.05 && op.Position.TotalSeconds > _effStart + boundLen + 0.15)
                                {
                                    origMp.Pause();
                                    try { op.Position = TimeSpan.FromSeconds(_effStart + boundLen); } catch { }
                                }
                                else
                                {
                                    // 速率微调:drift > 0 = 原片跑快了 → 稍微降速;drift < 0 = 原片落后 → 稍微提速
                                    double drift = op.Position.TotalSeconds - _effStart - clipPos;
                                    double rate = 1.0;
                                    if (Math.Abs(drift) > 0.06)
                                        rate = 1.0 - Math.Sign(drift) * Math.Min(0.02, Math.Abs(drift) * 0.05);
                                    if (Math.Abs(op.PlaybackRate - rate) > 0.0005) { try { op.PlaybackRate = rate; } catch { } }
                                }
                            }
                            else
                            {
                                if (origPlaying) origMp.Pause();   // 片段停了/播完 → 原片立刻停(不越过区间)
                                if (Math.Abs(op.PlaybackRate - 1.0) > 0.0005) { try { op.PlaybackRate = 1.0; } catch { } }
                            }
                        }
                    }
                    catch { }
                };
                _cmpStateHandler = (s, _) =>
                {
                    try
                    {
                        if (!_compareMode) return;
                        var st = s.PlaybackState;
                        // 播放图标跟随真实状态 + 播放中重起 2.5 秒自动隐藏;暂停/播完 → 淡入并常显
                        OnUiThread(() =>
                        {
                            SetCmpPlayGlyph(st == Windows.Media.Playback.MediaPlaybackState.Playing);
                            ShowCompareBarTemporarily();
                            if (st != Windows.Media.Playback.MediaPlaybackState.Playing) _cmpBarTimer?.Stop();
                        });
                        var op = PreviewPlayer.MediaPlayer;
                        if (op == null) return;
                        // 原片只跟随"播/停",位置由按下播放时的对齐负责(播放中纠偏会拖慢管线)
                        bool origPlaying = op.PlaybackSession.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing;
                        if (st == Windows.Media.Playback.MediaPlaybackState.Playing)
                        {
                            if (!origPlaying) op.Play();
                        }
                        else if (origPlaying) op.Pause();
                    }
                    catch { }
                };
                mp.PlaybackSession.PositionChanged += _cmpPosHandler;
                mp.PlaybackSession.PlaybackStateChanged += _cmpStateHandler;
                _compareSyncOn = true;
                EnsureCompareWatchdog(true);
            }
            else if (!on && _compareSyncOn)
            {
                try { mp.PlaybackSession.PositionChanged -= _cmpPosHandler; } catch { }
                try { mp.PlaybackSession.PlaybackStateChanged -= _cmpStateHandler; } catch { }
                _compareSyncOn = false;
                EnsureCompareWatchdog(false);
            }
        }
        catch { }
    }

    /// <summary>放大画面:收起控制区,画面占满整页(再点一次回来)。</summary>
    private void EffectZoomBtn_Click(object sender, RoutedEventArgs e)
    {
        _effZoomed = !_effZoomed;
        if (_effZoomed) _dupOpen = false;
        ApplyDeckVisibility();
    }

    private async void EffectRunBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_effBusy) return;
        if (EffectVideoCombo.SelectedItem is not VideoItem item) return;
        await StartEffectPreviewAsync(item, _effStart, _effRealLen);
    }

    /// <summary>真跑这一小段:经 RunPreviewAsync → RunBatchAsync(与「开始处理」同一条流水线)。</summary>
    private async Task StartEffectPreviewAsync(VideoItem item, double startSec, double lenSec)
    {
        CleanupEffectResult();   // 先收掉上一次的临时成片(不堆积)
        Log($"========== 预览处理 开始 ==========");
        Log($"预览:{item.Name} · {EffTime(startSec)} 起 {lenSec:0.#} 秒(用左侧当前参数真跑)");
        _effBusy = true;
        _effCts = new CancellationTokenSource();
        EffectCancelBtn.IsEnabled = true;
        EffectRunBtn.IsEnabled = false;
        EffectProgress.Value = 0;
        PreviewDeckStatus($"正在生成预览({EffTime(startSec)} 起 {lenSec:0.#} 秒)…");
        EffectPlayerHint.Text = "正在生成预览…";
        EffectPlayerHint.Visibility = Visibility.Visible;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var prog = new Progress<(int pct, string msg)>(t =>
        {
            try { EffectProgress.Value = Math.Clamp(t.pct, 0, 100); PreviewDeckStatus(t.msg); }
            catch { }
        });
        try
        {
            var outPath = await RunPreviewAsync(item, startSec, lenSec, prog, _effCts.Token);
            if (outPath == null)
            {
                // 没跑起来:参数校验没通过(超分/补帧都没开、引擎缺失、帧率框空…)或被取消 —— 具体原因在左下角日志
                PreviewDeckStatus((_effCts?.IsCancellationRequested ?? false)
                    ? "已取消预览。"
                    : "预览没有生成 —— 请检查左侧参数(超分/补帧是否已启用、引擎是否完整),原因见左下角日志。");
                EffectPlayerHint.Text = "点「开始预览」,处理结果会在这里播放";
            }
            else
            {
                _effOutPath = outPath;
                _effHasResult = true;
                _effDirty = false;
                EffectProgress.Value = 100;
                double mb = 0;
                try { mb = new FileInfo(outPath).Length / 1048576.0; } catch { }
                PreviewDeckStatus($"✓ 预览已就绪({mb:0.##} MB · 用时 {sw.Elapsed.TotalSeconds:0} 秒)"
                    + (_effRealLen < lenSec - 0.05 ? "(已按素材剩余长度截短)" : ""));
                PlayEffectResult(outPath);
            }
        }
        catch (OperationCanceledException) { PreviewDeckStatus("已取消预览。"); }
        catch (Exception ex)
        {
            AppLogger.Error("处理效果预览失败", ex);
            PreviewDeckStatus("✗ 预览失败:" + ex.Message);
            EffectPlayerHint.Text = "预览失败(详见左下角日志)";
        }
        finally
        {
            _effBusy = false;
            EffectCancelBtn.IsEnabled = false;
            try { _effCts?.Dispose(); } catch { }
            _effCts = null;
            Log($"========== 预览处理 结束(用时 {sw.Elapsed.TotalSeconds:0} 秒)==========");   // 与上面那条配对,把本次预览的日志包起来
            UpdatePreviewDeck();   // 恢复「开始预览/重新预览」的可用状态与文案
        }
    }

    /// <summary>播放预览成片:自动切到「左右对比」,等媒资打开后两边同时从头播。</summary>
    private void PlayEffectResult(string path)
    {
        try
        {
            _viewSyncing = true;
            // 预览跑完直接给「左右对比」:左边原片、右边处理结果,同一时刻,一眼看差别(用户要求)
            try { if (PreviewViewRadios != null) PreviewViewRadios.SelectedIndex = 2; } catch { }
            _viewSyncing = false;
            ApplyPreviewView();   // 让结果画面显示出来(对比模式下也走这里)
            EffectPlayerHint.Visibility = Visibility.Collapsed;
            _effStarted = null;   // 换新片:允许重新起播
            EffectPlayer.Source = MediaSource.CreateFromUri(new Uri(path));
            SetCompareSync(_compareMode);
            StartEffectPlaybackWhenReady(path, 0);
        }
        catch (Exception ex) { PreviewDeckStatus("预览已生成,但播放失败:" + ex.Message); }
    }

    /// <summary>
    /// 等结果画面真正可用再起播。
    /// 【真机踩坑】自动切到对比模式时,结果区是刚从 Collapsed 变可见的,MediaPlayer 还没被创建出来
    /// (`MediaPlayerElement.MediaPlayer` 为 null)—— 原来这里直接 return,表现为"预览完不播、播放条停在 0:00/0:00"。
    /// 所以这里轮询等它建出来(最多约 2 秒),再订阅 MediaOpened 起播。
    /// </summary>
    private void StartEffectPlaybackWhenReady(string path, int tries)
    {
        try
        {
            var mp = EffectPlayer.MediaPlayer;
            if (mp == null)
            {
                if (tries < 14)
                {
                    var t = DispatcherQueue.CreateTimer();
                    t.Interval = TimeSpan.FromMilliseconds(150);
                    t.IsRepeating = false;
                    t.Tick += (_, _) => StartEffectPlaybackWhenReady(path, tries + 1);
                    t.Start();
                }
                return;
            }
            if (_effOpenedHandler != null) { try { mp.MediaOpened -= _effOpenedHandler; } catch { } }
            _effOpenedHandler = (s, _) => StartBothPlayers(path);
            mp.MediaOpened += _effOpenedHandler;
            try
            {
                mp.MediaFailed -= EffMediaFailed;
                mp.MediaFailed += EffMediaFailed;
            }
            catch { }
            SetCompareSync(_compareMode);   // MediaPlayer 现在才存在,这里补订位置/状态同步(否则播放条永远不刷新)
            StartBothPlayers(path);   // 兜底:MediaOpened 可能已经过去了
        }
        catch { }
    }

    private void EffMediaFailed(Windows.Media.Playback.MediaPlayer sender, Windows.Media.Playback.MediaPlayerFailedEventArgs args)
        => AppLogger.Info($"[cmp] MediaFailed: {args.Error} {args.ErrorMessage} ext={args.ExtendedErrorCode?.Message}");

    /// <summary>
    /// 起播(对比模式)= 对齐后再同时播:暂停两个 → 各自定位 → 等 seek 真正落地 → 一起播。
    /// 【真机踩坑】直接"设位置然后立刻 Play"会从旧位置起步(seek 还没落地),表现为左边比右边超前 1 秒多;
    /// 暂停状态下连续 seek 也会被播放器吞掉(试过,画面压根不动),所以必须用这个序列。
    /// </summary>
    /// <summary>
    /// 定位并【确认真的落地】:反复设位置,直到读回的 Position 与目标相差 &lt; 60ms(最多约 1 秒)。
    /// 【2026-09-17 用户反馈:左边比右边早 0.几秒】根因就是原来"设一次位置 + 死等 350ms 就播",
    /// 那次 seek 没落地就播起来了 → 留下一个恒定偏移。这里改成"读回来确认到位"再继续,偏移就没有了。
    /// </summary>
    private static async Task SeekAndVerifyAsync(Windows.Media.Playback.MediaPlayer? mp, double seconds)
    {
        if (mp == null) return;
        try
        {
            var se = mp.PlaybackSession;
            for (int i = 0; i < 8; i++)
            {
                try { se.Position = TimeSpan.FromSeconds(Math.Max(0, seconds)); } catch { }
                await Task.Delay(120);
                try { if (Math.Abs(se.Position.TotalSeconds - seconds) < 0.06) return; } catch { }
            }
        }
        catch { }
    }

    /// <summary>切到「看处理效果」时:把原片对到预览起点、片段回到 0 —— 两种视图显示同一时刻(用户反馈时间不一致)。</summary>
    private async Task AlignModeSwitchAsync(int mode)
    {
        try
        {
            var eff = EffectPlayer.MediaPlayer;
            var om = PreviewPlayer.MediaPlayer;
            if (eff == null || om == null) return;
            if (mode == 1)
            {
                // 显示"处理效果":原片对到 预览起点 + 片段当前时间 → 与片段同一画面
                double clipNow = 0;
                try { clipNow = eff.PlaybackSession.Position.TotalSeconds; } catch { }
                await SeekAndVerifyAsync(om, _effStart + clipNow);
            }
            else
            {
                // 显示"原片":片段对到 原片当前位置 - 预览起点(夹在 0 以上)→ 与原片同一画面
                double origNow = 0;
                try { origNow = om.PlaybackSession.Position.TotalSeconds; } catch { }
                await SeekAndVerifyAsync(eff, Math.Max(0, origNow - _effStart));
            }
        }
        catch { }
    }

    private async Task AlignAndPlayAsync()
    {
        try
        {
            var eff = EffectPlayer.MediaPlayer;
            var se = eff?.PlaybackSession;
            var om = PreviewPlayer.MediaPlayer;
            var os = om?.PlaybackSession;
            if (eff == null || se == null || om == null || os == null) return;
            eff.Pause();
            om.Pause();
            // 【2026-09-17 用户反馈:不要回到视频开头】片段是"主":**绝不重置片段时间**,
            // 只把原片对到"预览起点 + 片段当前位置",然后两边同时播 —— 这样从中间接着播也不会跳回开头。
            double clipNow = 0;
            try { clipNow = se.Position.TotalSeconds; } catch { }
            await SeekAndVerifyAsync(om, _effStart + clipNow);
            eff.Play();
            if (_compareMode) om.Play();
            OnUiThread(() => SetCmpPlayGlyph(true));
        }
        catch { }
    }

    /// <summary>起播:结果从头播;对比模式下与对齐序列一起走。</summary>
    private void StartBothPlayers(string path)
    {
        if (_effStarted == path) return;   // 避免 MediaOpened 与兜底各起播一次(会看到开头突然重来一下)
        _effStarted = path;
        if (_compareMode) { _ = AlignAndPlayAsync(); return; }
        try
        {
            var mp = EffectPlayer.MediaPlayer;
            if (mp != null)
            {
                mp.PlaybackSession.Position = TimeSpan.Zero;
                mp.Play();
            }
        }
        catch { }
    }

    private void EffectCancelBtn_Click(object sender, RoutedEventArgs e)
    {
        PreviewDeckStatus("正在取消…");
        try { _effCts?.Cancel(); } catch { }
    }

    /// <summary>「保存此预览」:把这次的预览成片另存到你选的位置(系统保存窗口)。</summary>
    private async void SavePreview_Click(object sender, RoutedEventArgs e)
    {
        AppLogger.Info($"[save] 点击进入: hasResult={_effHasResult} outPath={_effOutPath ?? "(null)"}");
        if (string.IsNullOrEmpty(_effOutPath) || !File.Exists(_effOutPath))
        {
            PreviewDeckStatus("还没有可保存的预览 —— 先点「开始预览」跑一段。");
            return;
        }
        try
        {
            string srcName = Path.GetFileNameWithoutExtension(_previewItem?.Path ?? "视频");
            string rangeTxt = EffTime(_effStart).Replace(":", "-").Replace(".", "_");
            string suggested = $"{srcName}_预览_{rangeTxt}_{_effRealLen:0.#}秒";
            string ext = Path.GetExtension(_effOutPath);
            if (string.IsNullOrEmpty(ext)) ext = ".mp4";
            string typeName = ext.ToLowerInvariant() == ".mkv" ? "MKV 视频" : "MP4 视频";
            string? destPath = null;

            // ① 新版保存对话框(WinAppSDK 自带,未打包应用可用)。
            // 【真机踩坑】老版 Windows.Storage.Pickers.FileSavePicker 在未打包应用里会**静默返回 null**
            // (对话框根本不出现、也不抛异常),所以主用新版;新版不可用时再退回"选文件夹"。
            try
            {
                var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(App.MainWindow.AppWindow.Id)
                {
                    SuggestedFileName = suggested,
                };
                picker.FileTypeChoices.Add(typeName, new System.Collections.Generic.List<string> { ext });
                var res = await picker.PickSaveFileAsync();
                AppLogger.Info("[save] 新版保存框返回: " + (res?.Path ?? "(null)"));
                if (res != null && !string.IsNullOrEmpty(res.Path)) destPath = res.Path;
            }
            catch (Exception ex) { AppLogger.Info("[save] 新版保存框异常: " + ex.GetType().Name + " " + ex.Message); }

            // ② 兜底:选一个文件夹,用建议文件名存进去
            if (destPath == null)
            {
                var folder = new Windows.Storage.Pickers.FolderPicker();
                folder.FileTypeFilter.Add("*");
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(folder, hwnd);
                var dir = await folder.PickSingleFolderAsync();
                AppLogger.Info("[save] 选文件夹返回: " + (dir?.Path ?? "(null)"));
                if (dir == null) return;   // 用户取消
                destPath = Path.Combine(dir.Path, suggested + ext);
                for (int i = 2; File.Exists(destPath) && i < 100; i++)
                    destPath = Path.Combine(dir.Path, $"{suggested} ({i}){ext}");
            }

            // 暂停播放再复制:播放器正占着这个临时文件(否则可能复制失败)
            bool wasPlaying = false;
            try
            {
                var se = EffectPlayer.MediaPlayer?.PlaybackSession;
                wasPlaying = se != null && se.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing;
                if (wasPlaying) EffectPlayer.MediaPlayer?.Pause();
            }
            catch { }
            File.Copy(_effOutPath, destPath, true);
            try { if (wasPlaying) EffectPlayer.MediaPlayer?.Play(); } catch { }
            PreviewDeckStatus($"✓ 已保存:{destPath}");
            Log($"预览已保存到:{destPath}");
        }
        catch (Exception ex)
        {
            AppLogger.Error("保存预览失败", ex);
            PreviewDeckStatus("保存失败:" + ex.Message);
        }
    }

    /// <summary>收掉预览成片(临时文件,不留残余)并停播。</summary>
    private void CleanupEffectResult()
    {
        try { EffectPlayer.MediaPlayer?.Pause(); } catch { }
        try { EffectPlayer.Source = null; } catch { }
        EffectPlayerHint.Visibility = Visibility.Visible;
        EffectProgress.Value = 0;
        _effHasResult = false;
        _effDirty = false;
        var p = _effOutPath;
        _effOutPath = null;
        if (!string.IsNullOrEmpty(p))
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { }
        }
        _compareMode = false;
        SetCompareSync(false);
        if (CompareSplitter != null) CompareSplitter.Visibility = Visibility.Collapsed;
        ApplyPlayerClip();
    }

    // 兼容旧名(关闭页面时调用)
    private void CleanupEffectOutput() => CleanupEffectResult();

    /// <summary>左侧参数改过:现有预览结果已过期 → 提示「重新预览」(由 OnOptionChanged 调用)。</summary>
    private void MarkPreviewDirty()
    {
        if (_effBusy || !_effHasResult) return;
        _effDirty = true;
        UpdatePreviewDeck();
    }

    private void PreviewDeckStatus(string text)
    {
        try { if (EffectStatusText != null) EffectStatusText.Text = text; } catch { }
    }

    /// <summary>
    /// 清掉上次异常退出(被强杀/断电)留下的预览临时文件 —— 正常路径由 CleanupEffectResult 删除,
    /// 但进程没走到那里时文件会一直躺在 %TEMP% 里。只删 2 小时前的:万一真有第二个实例在播它的预览,也不会被误删。
    /// </summary>
    private static void PruneStalePreviewFiles()
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "ALHPro", "preview");
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, "preview_*"))
            {
                try { if (DateTime.Now - File.GetLastWriteTime(f) > TimeSpan.FromHours(2)) File.Delete(f); } catch { }
            }
        }
        catch { }
    }

    // 播放器自带的传输条:鼠标移入显示、静止/移开 2 秒后隐藏(不挡画面)
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _previewCtrlTimer;

    private void PreviewPlayerHost_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_compareMode) return;   // 对比模式只用自绘那条播放控件,不再弹出原片自带的控件
        try { PreviewPlayer.TransportControls.Visibility = Visibility.Visible; } catch { }
        RestartHideTimer();
    }

    private void RestartHideTimer()
    {
        _previewCtrlTimer?.Stop();
        _previewCtrlTimer = DispatcherQueue.CreateTimer();
        _previewCtrlTimer.Interval = TimeSpan.FromMilliseconds(2000);
        _previewCtrlTimer.IsRepeating = false;
        _previewCtrlTimer.Tick += (_, _) =>
        {
            try { PreviewPlayer.TransportControls.Visibility = Visibility.Collapsed; } catch { }
        };
        _previewCtrlTimer.Start();
    }

    private void PreviewPlayerHost_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        => RestartHideTimer();

    // ---------- 时间线裁剪(裁剪页:只有裁剪,首尾两个把手都能拖,拖动时画面实时跟着跳) ----------
    private double _trimStart;
    private double _trimEnd;
    private double _duration;
    private int _trimDrag;      // 0=没拖 1=拖入点 2=拖出点
    private bool _dupOpen;      // 裁剪页底部的「重复帧」工具行是否展开(默认收起)

    private void TrimTimeline_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        BuildTrimTicks();
        UpdateTrimUI();
    }

    /// <summary>时间→像素(裁剪时间线)。</summary>
    private double TrimX(double t)
    {
        double w = TrimTimeline.ActualWidth - EffTimelinePad * 2;
        if (w <= 4 || _duration <= 0.1) return EffTimelinePad;
        return EffTimelinePad + Math.Clamp(t, 0, _duration) / _duration * w;
    }

    /// <summary>像素→时间(裁剪时间线)。</summary>
    private double TrimTimeAt(double x)
    {
        double w = TrimTimeline.ActualWidth - EffTimelinePad * 2;
        if (w <= 4 || _duration <= 0.1) return 0;
        return Math.Clamp((x - EffTimelinePad) / w * _duration, 0, _duration);
    }

    private void TrimTimeline_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (TrimTimeline == null || _duration <= 0.1) return;
        try
        {
            double x = e.GetCurrentPoint(TrimTimeline).Position.X;
            // 离哪个把手近就抓哪个(手抓把手的手感);两个都远时按点击位置就近选一个并直接拖过去(点哪跳哪)
            _trimDrag = Math.Abs(x - TrimX(_trimStart)) <= Math.Abs(x - TrimX(_trimEnd)) ? 1 : 2;
            TrimTimeline.CapturePointer(e.Pointer);
            TrimTimeline.Focus(FocusState.Pointer);
            UpdateTrimFromPointer(e);
            e.Handled = true;
        }
        catch { }
    }

    private void TrimTimeline_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_trimDrag == 0) return;
        UpdateTrimFromPointer(e);
        e.Handled = true;
    }

    private void TrimTimeline_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_trimDrag == 1) SeekSourceTo(_trimStart, true);
        else if (_trimDrag == 2) SeekSourceTo(Math.Max(0, _trimEnd - 0.05), true);
        _trimDrag = 0;
        try { TrimTimeline?.ReleasePointerCapture(e.Pointer); } catch { }
    }

    /// <summary>拖动中:更新入/出点,并让原片跟着跳到刚拖到的那个点(用户要的"拖动时画面跟着走")。</summary>
    private void UpdateTrimFromPointer(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_duration <= 0.1) return;
        double sec = TrimTimeAt(e.GetCurrentPoint(TrimTimeline).Position.X);
        if (_trimDrag == 1)
        {
            _trimStart = Math.Min(sec, Math.Max(0, _trimEnd - 0.1));
            SeekSourceTo(_trimStart, false);
        }
        else if (_trimDrag == 2)
        {
            _trimEnd = Math.Max(sec, Math.Min(_duration, _trimStart + 0.1));
            SeekSourceTo(Math.Max(_trimStart, _trimEnd - 0.05), false);
        }
        UpdateTrimUI();
    }

    private void UpdateTrimUI()
    {
        if (TrimTimeline == null) return;
        if (_duration <= 0.1 || TrimTimeline.ActualWidth <= 4)
        {
            if (TrimInfo != null && _duration <= 0.1) TrimInfo.Text = "加载时长...";
            return;
        }
        double sx = TrimX(_trimStart), ex = TrimX(_trimEnd);
        TrimStartThumb.Margin = new Thickness(sx - 8, 15, 0, 0);
        TrimEndThumb.Margin = new Thickness(ex - 8, 15, 0, 0);
        TrimRange.Margin = new Thickness(sx, 20, 0, 0);
        TrimRange.Width = Math.Max(0, ex - sx);
        var trimmed = _trimStart > 0.1 || _trimEnd < _duration - 0.1;
        TrimInfo.Text = trimmed
            ? $"保留 {FormatTime(_trimStart)} ~ {FormatTime(_trimEnd)}(共 {_trimEnd - _trimStart:0.#} 秒)· 总长 {FormatTime(_duration)}"
            : $"未裁剪 · 总长 {FormatTime(_duration)} · 整段保留";
    }

    /// <summary>裁剪时间线的刻度(与预览时间线同一套铺法)。</summary>
    private void BuildTrimTicks()
    {
        if (TrimTimeline == null || TrimTicks == null) return;
        BuildTicksInto(TrimTicks, TrimTimeline.ActualWidth, _duration > 0.1 ? _duration : 30);
    }

    private void ClearTrim_Click(object sender, RoutedEventArgs e)
    {
        _trimStart = 0;
        _trimEnd = _duration;
        UpdateTrimUI();
        SeekSourceTo(0, true);
    }

    /// <summary>「重复帧」工具行(裁剪页最下面一行,默认收起 —— 它必须有入口,否则这功能就没人找得到了)。</summary>
    private void ToggleDup_Click(object sender, RoutedEventArgs e)
    {
        _dupOpen = !_dupOpen;
        ApplyDeckVisibility();
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
        // 画面立即跳到裁剪起点
        SeekSourceTo(_trimStart, true);
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
    /// <summary>「开始处理」前的诊断卡片:扫描输入视频,估算【预计占用临时盘】与【预计耗时】,
    /// 命中硬风险(会爆盘 / 高倍率补帧+资源紧 / 弱设备走CPU)才弹卡片给出建议。用户点「取消/改参数」则不启动(返回 false)。
    /// 复用 VideoService.Probe* 与 EstimateProcessSeconds,占盘公式与 C3 临时盘预检一致。</summary>
    private async Task<bool> ShowPreflightDiagAsync(VideoItem[] items)
    {
        try
        {
            bool interpOn = InterpToggle.IsChecked == true;
            bool upOn = UpscaleToggle.IsChecked == true;
            bool dedupOn = DedupCheck.IsChecked == true;
            int interpScale = AlhPro.Core.InterpScaleMap.Multiplier(CurrentScaleIndex());
            int engIdx = VideoEngineRadios.SelectedIndex;
            string engine = SelectedEngineIsReal ? "realesrgan" : "waifu2x";
            // 倍率:0=1x(2x缩回) 1=2x 2=3x 3=4x 4=自定义(内部按2x)
            int scale = VideoScaleRadios.SelectedIndex switch { 1 => 2, 2 => 3, 3 => 4, _ => 1 };
            bool upscaleShrink1x = VideoScaleRadios.SelectedIndex == 0;
            if (VideoScaleRadios.SelectedIndex == 4) scale = 2;   // 自定义分辨率:内部按 2x 超分再缩放(与 pipeline 一致,避免占盘/耗时低估)
            if (upOn && upscaleShrink1x) scale = 2;
            bool highRate = interpScale >= 4;   // 4x 及以上
            double totalNeedGB = 0, totalSec = 0;
            // ===== 超 4K 判定所需的 UI 值,必须在 UI 线程先读成局部量 =====
            // XAML 对象有线程亲和:后台线程读控件会抛 RPC_E_WRONG_THREAD(0x8001010E)。
            // 历史上这里正是在下面 Task.Run 的 lambda 内读 VideoScaleRadios.SelectedIndex /
            // CustomWidthBox.Text 来算"输出超 4K",异常被 lambda 里的 catch{} 逐个吞掉,
            // 于是超分开启时整段扫描静默失效:超 4K 名单恒空(弹窗从未真正生效过)、
            // totalSec/totalNeedGB 恒 0(爆盘预检永不触发,诊断框还显示"约 0 分钟/约 0 GB"的假数字)。
            // 所以这里先取快照,lambda 内只许用这些局部量,不许再碰任何控件。
            bool customResUi = VideoScaleRadios.SelectedIndex == 4;   // 4=自定义分辨率
            int.TryParse(CustomWidthBox.Text, out int customWUi);
            int.TryParse(CustomHeightBox.Text, out int customHUi);
            // 超 4K 名单(形如 "名字(3840×4320)"):只装确实超 4K 的项,供下方确认弹窗列出
            var over4k = new System.Collections.Generic.List<string>();
            // 后台扫描每个视频(不卡 UI)
            await Task.Run(async () =>
            {
                foreach (var it in items)
                {
                    try
                    {
                        double dur = await VideoService.ProbeDurationSeconds(it.Path).ConfigureAwait(false);
                        var (w, h) = await VideoService.ProbeSizeAsync(it.Path).ConfigureAwait(false);
                        // 输出尺寸口径与内联红字(RefreshVideoOutSpec)完全一致:超分开启且非 1x 缩回 → 源×倍率;
                        // 自定义分辨率 → 用用户填的值;1x 缩回 → 源尺寸(补帧只改帧率,不影响尺寸)。
                        int outW = w, outH = h;
                        if (upOn)
                        {
                            if (customResUi)
                            {
                                if (customWUi > 0 && customHUi > 0) { outW = customWUi; outH = customHUi; }
                            }
                            else if (!upscaleShrink1x)
                            {
                                outW = (int)Math.Round((double)w * scale); outH = (int)Math.Round((double)h * scale);
                            }
                        }
                        // 按【总像素】判定(3840×2160≈829 万),不按单边 —— 否则 10×33333 这类极端宽高比会误报。
                        // 尺寸探测与时长无关,所以这条判定放在"时长未知就跳过"之前:内联红字同样只看尺寸,
                        // 时长探不出来的视频也超 4K,漏掉它两处提示就不一致了。
                        if (outW > 0 && outH > 0 && (double)outW * outH > 3840.0 * 2160.0)
                            over4k.Add($"{it.Name}({outW}×{outH})");
                        if (dur <= 0) continue;   // 时长未知:下面两项估算(耗时/占盘)都依赖时长
                        double fps = 30;
                        try { if (double.TryParse(VideoService.ProbeFps(it.Path), NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pf) && pf > 0) fps = pf; } catch { }
                        // 【H · 2026-09-13 / 2026-09-16 清理】阶段顺序不再由界面自己算,ETA 也不接受顺序形参:
                        // 真正生效的顺序由 AlhPro.Core.PipelineOrderPlan.Decide(...) 在处理途中判定,
                        // ETA 按旧顺序(回退值)估 —— 界面既无从传错、也无法另写一份判据。
                        totalSec += VideoService.EstimateProcessSeconds(dur, fps, w, h,
                            upOn, scale, engine, interpOn, interpScale, dedupOn, 0, postFx: false,
                            freeRamGB: SafeRender.FreeRamGB);   // 传空闲内存 → 估算里计入"每批引擎启动开销 × 批数"
                        // 占盘(JPG 中间帧峰值,与 C3 一致):源帧≈1MB/1080p,放大后×倍率²×0.18
                        double srcMB = 1.0 * ((double)w * h) / (1920.0 * 1080.0); if (srcMB < 0.5) srcMB = 0.5;
                        double outMult = upOn ? (upscaleShrink1x ? 2.0 : Math.Max(1.0, scale)) : 1.0;
                        double outMB = Math.Max(srcMB, srcMB * outMult * outMult * 0.18); if (outMB < 0.5) outMB = 0.5;
                        long baseFrames = (long)Math.Ceiling(Math.Max(1.0, dur * fps));
                        long peakFrames = interpOn ? (long)Math.Ceiling((double)baseFrames * interpScale) : baseFrames;
                        totalNeedGB += peakFrames * outMB * 1.6 / 1024.0;
                    }
                    catch { }
                }
            }).ConfigureAwait(true);

            // ===== 超 4K 确认弹窗(用户要求加回:超 4K 必须让用户主动确认才继续) =====
            // 只对确实超 4K 的项弹;一个都没有就完全不弹(不为了"保险"每次都打断用户)。
            // 该弹窗此前从未真正生效过:判定依赖的 outW/outH 算在跨线程的 Task.Run 里读 XAML 控件,
            // 抛 0x8001010E 被 catch{} 吞掉 → 名单恒空。现在判定已挪到 UI 线程取快照(见本方法开头),
            // 才真正有防线。它与内联红字(RefreshVideoOutSpec)同口径,一个在开始前拦、一个常驻提示。
            if (over4k.Count > 0)
            {
                var dlg4k = new ContentDialog
                {
                    Title = "⚠ 输出规格超过 4K",
                    Content = new StackPanel
                    {
                        Spacing = 8,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = "以下视频的输出分辨率超过 4K(按总像素 >3840×2160 判定):\n\n　"
                                    + string.Join("\n　", over4k)
                                    + "\n\n超 4K 会占用极大量显存/临时磁盘、处理非常慢,甚至中途失败。是否仍要继续?",
                                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                            },
                            new TextBlock { Text = "也可先降低超分倍率或改小自定义分辨率再试。", FontSize = 11, Opacity = 0.6, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap },
                        },
                    },
                    PrimaryButtonText = "仍要继续",
                    CloseButtonText = "取消",
                    DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Primary,
                    XamlRoot = this.XamlRoot,   // 必填:缺它 ShowAsync 直接抛异常(弹窗根本出不来)
                    // 按钮配色(用户指定):「仍要继续」红底白字 /「取消」蓝底白字
                    PrimaryButtonStyle = ButtonStyle(Windows.UI.Color.FromArgb(255, 217, 48, 48), Windows.UI.Color.FromArgb(255, 255, 255, 255)),
                    CloseButtonStyle = ButtonStyle(Windows.UI.Color.FromArgb(255, 0, 103, 192), Windows.UI.Color.FromArgb(255, 255, 255, 255)),
                };
                var r4k = await dlg4k.ShowAsync();
                if (r4k != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
                {
                    Log("⚠ 检测到输出超 4K,用户选择「取消」,已停止处理。");
                    return false;
                }
                Log($"⚠ 输出超 4K,用户选择「仍要继续」:{string.Join(" / ", over4k)}");
            }

            // ===== 【第 3 项】DirectML 不可用 → 超分将落到 CPU:开始前就给出【量级】并醒目提示 =====
            // 用户要求:①内联醒目提示为主(不弹窗);②给具体预估;③只在预计超阈值(30 分钟)时才多一次确认;
            // ④必须在开始处理【前】算出来 —— 这里正是既有的"处理前诊断"处,与 4K 确认同一个时机。
            // 现状问题:原代码只有一行日志 +"速度会变得特别慢",没有任何量级,用户跑起来才发现 8 秒/帧。
            // 【先确保探测有结论】幂等;MainPage 已探过立即返回。不做这一步的话,在"启动自检跳过了探测"的
            // 机器上 DmlProbeCompleted 会是 false,判定退化为"不提示",第 3 项在这类机器上等于没做。
            try { await ALHPro.EsrganOnnxService.EnsureDmlProbeAsync().ConfigureAwait(true); } catch { }
            if (UpscaleWillFallbackToCpu())
            {
                string cpuEngine = SelectedEngineIsReal ? "realesrgan" : "waifu2x";
                var (perFrameBase, perFrameSrc, _) = CpuPerFrameBase();
                long cpuFrames = 0;
                double cpuMinutes = 0, cpuPerFrameMax = 0;
                int cpuW = 0, cpuH = 0;
                foreach (var it in items)
                {
                    try
                    {
                        double d = await VideoService.ProbeDurationSeconds(it.Path);
                        var (w, h) = await VideoService.ProbeSizeAsync(it.Path);
                        double fps = 30;
                        try
                        {
                            if (double.TryParse(await Task.Run(() => VideoService.ProbeFps(it.Path)), NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out var pf) && pf > 0)
                                fps = pf;
                        }
                        catch { }
                        if (d <= 0 || w <= 0 || h <= 0) continue;
                        long fr = (long)Math.Ceiling(d * fps);
                        if (fr <= 0) continue;
                        // 秒/帧 = max(经验库同配置实测, 保守常数) × 面积;帧数 = 时长 × 源帧率(补帧在超分【之后】,不放大超分帧数)
                        double sec = ALHPro.EsrganOnnxService.EstimateCpuUpscaleSeconds(fr, w, h, perFrameBase, out var per);
                        cpuFrames += fr;
                        cpuMinutes += sec / 60.0;
                        cpuPerFrameMax = Math.Max(cpuPerFrameMax, per);
                        if (w * h > cpuW * cpuH) { cpuW = w; cpuH = h; }
                    }
                    catch { }
                }
                string detail = cpuFrames > 0
                    ? $"约 {cpuPerFrameMax:0.#} 秒/帧(最大尺寸 {cpuW}×{cpuH};{perFrameSrc})、共 {cpuFrames} 帧 → 超分阶段预计约 {FormatMinutes(cpuMinutes)}"
                    : "秒/帧与总时长无法估算(视频时长/尺寸/帧率未探明)——按经验,CPU 逐帧超分每帧需数秒到数十秒";
                string summary = "⚠ 本机 DirectML 不可用,超分将使用 CPU:" + detail;
                Log(summary);
                AppLogger.Warn(summary + $"(设备={ALHPro.EsrganOnnxService.DmlUnavailableReason};共 {items.Length} 个视频)");
                SetCpuFallbackHint(summary);   // 内联红字:开始前就能看到,处理中也常显
                // ③ 仅当预计超过 30 分钟,才允许【一次】确认(沿用既有「仍要继续/取消」模式:主按钮红、取消蓝)
                const double cpuConfirmThresholdMin = 30.0;
                if (cpuMinutes > cpuConfirmThresholdMin)
                {
                    var dlgCpu = new ContentDialog
                    {
                        Title = "⚠ 超分将使用 CPU,预计很慢",
                        Content = new StackPanel
                        {
                            Spacing = 8,
                            Children =
                            {
                                new TextBlock
                                {
                                    Text = "本机 DirectML(GPU 加速)不可用,超分只能使用 CPU 计算。\n\n" + detail
                                        + "\n\n(秒/帧来源:" + perFrameSrc + ";该数值为保守估计,实际可能更快或更慢。)\n"
                                        + "DirectML 不可用的原因:" + ALHPro.EsrganOnnxService.DmlUnavailableReason + "\n\n"
                                        + "是否仍要继续?",
                                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                                },
                                new TextBlock
                                {
                                    Text = "建议:先取消,更新显卡驱动后重启软件再试;或减少视频数量(只处理需要的片段)、"
                                        + "降低输出分辨率/超分倍率,把总时间压下来。",
                                    FontSize = 11, Opacity = 0.6, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                                },
                            },
                        },
                        PrimaryButtonText = "仍要继续",
                        CloseButtonText = "取消",
                        DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Primary,
                        XamlRoot = this.XamlRoot,
                        PrimaryButtonStyle = ButtonStyle(Windows.UI.Color.FromArgb(255, 217, 48, 48), Windows.UI.Color.FromArgb(255, 255, 255, 255)),
                        CloseButtonStyle = ButtonStyle(Windows.UI.Color.FromArgb(255, 0, 103, 192), Windows.UI.Color.FromArgb(255, 255, 255, 255)),
                    };
                    var rCpu = await dlgCpu.ShowAsync();
                    if (rCpu != ContentDialogResult.Primary)
                    {
                        Log("⚠ 超分将用 CPU 且预计超过 30 分钟,用户选择「取消」,已停止处理。");
                        return false;
                    }
                    Log("⚠ 超分将用 CPU 且预计超过 30 分钟,用户选择「仍要继续」。");
                }
            }

            // 硬风险1:会爆盘(预计占 > 当前临时盘剩余)
            bool diskRisk = false;
            string needTxt = $"{totalNeedGB:0.#} GB";
            try
            {
                var tempRoot = ALHPro.EngineService.TempRoot;
                var di = new System.IO.DriveInfo(tempRoot);
                double freeGB = di.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
                if (totalNeedGB > 0 && totalNeedGB > freeGB * 0.9) diskRisk = true;
            }
            catch { }

            bool weakGpu = SafeRender.Profile == SafeRender.DeviceProfile.UltraLow
                || CurrentIsIntegratedGpu() || SafeRender.TotalVramGB < 6.5;
            bool resourceRisk = interpOn && (highRate || (SelectedTargetFps() is { } tfRes && tfRes >= 90))
                && weakGpu;
            bool weakDevice = SafeRender.IsWeakDevice && FastModeCheck.IsChecked != true;

            // 无硬风险 → 不弹,直接开始
            if (!diskRisk && !resourceRisk && !weakDevice) return true;

            var lines = new System.Collections.Generic.List<string>();
            lines.Add($"预计处理耗时:约 {totalSec / 60:0.#} 分钟");
            lines.Add($"预计占用临时盘:约 {needTxt}");
            if (diskRisk)
                lines.Add("⚠ 空间不足:预计占用超过临时盘可用空间,可能中途爆盘。建议:清理磁盘 / 降低超分或补帧倍率 / 换剩余空间更大的盘。");
            if (resourceRisk)
                lines.Add("⚠ 高倍率补帧 + 设备偏弱:可能因显存不足中途出错。建议:点「一键开启兼容模式」自动降分块/批大小,或改用 2x。");
            if (weakDevice)
                lines.Add("⚠ 设备配置较低(核显/小显存/内存小),处理会明显偏慢。建议:开启「兼容模式」或先跑几秒小片段确认。");

            var dlg = new ContentDialog
            {
                Title = "开始前诊断 · ALH Pro",
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = string.Join("\n", lines), TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap },
                        new TextBlock { Text = "运行期间可用「暂停/取消」随时停止;不会损坏源视频。", FontSize = 11, Opacity = 0.6, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap },
                    },
                },
                PrimaryButtonText = "知道了,开始",
                SecondaryButtonText = resourceRisk || weakDevice ? "一键开启兼容模式" : null,
                CloseButtonText = "先改参数",
                DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot,
            };
            var r = await dlg.ShowAsync();
            if (r == Microsoft.UI.Xaml.Controls.ContentDialogResult.Secondary)
            {
                FastModeCheck.IsChecked = true;   // 一键开启兼容模式(源头降资源)
                Log("⚠ 已开启「兼容模式」(降低分块/批大小,处理更稳)。");
                if (CompatHint != null) CompatHint.Text = "⚠ 已开启「兼容模式」:降低分块/批大小,处理更稳更省资源。";
                if (CompatHintPanel != null) CompatHintPanel.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                return true;
            }
            return r == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary;
        }
        catch { return true; }   // 诊断出错不拦截,照常处理
    }

    /// <summary>构造一个纯色按钮 Style:ContentDialog 的默认按钮配色由主题接管,
    /// 而超 4K 弹窗要求「仍要继续」红底 /「取消」蓝底(风险动作要一眼可辨),只能自己设 Background/Foreground。
    /// BorderBrush 也一并设成同色 —— ContentDialog 按钮默认带描边,不设会露出主题色描边形成"双色边"。</summary>
    private static Microsoft.UI.Xaml.Style ButtonStyle(Windows.UI.Color bg, Windows.UI.Color fg)
    {
        var st = new Microsoft.UI.Xaml.Style(typeof(Microsoft.UI.Xaml.Controls.Button));
        st.Setters.Add(new Microsoft.UI.Xaml.Setter(Microsoft.UI.Xaml.Controls.Control.BackgroundProperty, new Microsoft.UI.Xaml.Media.SolidColorBrush(bg)));
        st.Setters.Add(new Microsoft.UI.Xaml.Setter(Microsoft.UI.Xaml.Controls.Control.ForegroundProperty, new Microsoft.UI.Xaml.Media.SolidColorBrush(fg)));
        st.Setters.Add(new Microsoft.UI.Xaml.Setter(Microsoft.UI.Xaml.Controls.Control.BorderBrushProperty, new Microsoft.UI.Xaml.Media.SolidColorBrush(bg)));
        return st;
    }

    /// <summary>ETA 用的粗阶段键:只用来识别"是否换了处理阶段"(换阶段时剩余时间本来就该跳变,
    /// 允许重置单调基准)。认不出返回空串 —— 空串不触发重置,避免个别阶段内消息把基准反复清零。
    /// 先判"编码":编码阶段的消息(编码 N/M、压缩编码器:…、混合编码…)都归到此键,
    /// 而拆帧/超分阶段的消息不会含"编码"二字。</summary>
    private static string EtaStageKey(string msg)
    {
        if (msg.Contains("编码", StringComparison.Ordinal)) return "enc";
        if (msg.Contains("拆帧", StringComparison.Ordinal)) return "split";
        if (msg.Contains("插帧", StringComparison.Ordinal) || msg.Contains("补帧", StringComparison.Ordinal)) return "interp";
        if (msg.Contains("超分", StringComparison.Ordinal)) return "up";
        if (msg.Contains("后处理", StringComparison.Ordinal)) return "post";
        return "";
    }

    /// <summary>阶段在"整体进度百分比"里的区间(闭区间概念:lo = 起点、hi = 终点)。
    /// 【2026-09-16 用户反馈"补帧才完条就快到底"】不再在本文件写死 —— 与
    /// `VideoService.StageProgressPct`、`EngineService` 的逐帧兜底同源,一律读 Core.ProgressBands
    /// (按真机实测耗时分配,唯一一份判据)。用于"当前阶段剩余"下限推算:阶段内比例 = (pctFine − lo) ÷ (hi − lo)。
    /// 认不出返回 (0,0) = 不参与下限(拿不准就不给)。</summary>
    private static (double lo, double hi) StageSpanOf(string stageKey)
        => AlhPro.Core.ProgressBands.OfKey(stageKey);

    private async void RunBtn_Click(object sender, RoutedEventArgs e) => await RunBatchAsync(null);

    /// <summary>
    /// 「预览效果」一次预览的上下文。预览与正式处理走【同一条流水线、同一份参数快照】
    /// (见 RunBatchAsync),差别只有三处:
    ///   ① 只跑用户点中的那一个视频的 [Start, Start+Length] 区间(该项在列表里的全片裁剪不参与);
    ///   ② 成片输出到临时目录,跑完交回界面直接播放、由界面负责删除;不写列表状态、不写 ETA 经验库;
    ///   ③ 不弹「处理完成」结果窗。
    /// 这样"预览看到的"就是"全片将会得到的"——预览不降质、不走简化参数。
    /// </summary>
    private sealed class PreviewRun
    {
        public VideoItem Item = null!;
        public double Start;
        public double Length;
        public IProgress<(int pct, string msg)> Progress = null!;
        public CancellationToken Token;
        /// <summary>预览结束交回临时成片路径;跑失败时为异常;参数校验没通过/被取消 → 永不完成。</summary>
        public TaskCompletionSource<string> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // 预览不该改动右侧列表里这一项的显示状态(完成标记/进度),收尾时按这三个值原样还原
        public string SavedStatus = "";
        public string SavedEta = "";
        public double SavedProgress;
    }

    /// <summary>当前正在跑的预览(null = 不是预览):让「暂停/恢复」在预览期间失效(预览不接受暂停)。</summary>
    private PreviewRun? _previewRun;

    /// <summary>
    /// 用当前界面参数真跑一小段,返回临时成片路径。失败抛异常;参数校验没通过/被取消 → 返回 null。
    /// 复用 RunBatchAsync ⇒ 预览与「开始处理」逐字同参数、同流程。
    /// </summary>
    private async Task<string?> RunPreviewAsync(VideoItem item, double startSec, double lengthSec,
        IProgress<(int pct, string msg)> progress, CancellationToken token)
    {
        if (_running || _runItems != null || _previewRun != null) return null;
        var ctx = new PreviewRun
        {
            Item = item,
            Start = Math.Max(0, startSec),
            Length = Math.Max(0.2, lengthSec),
            Progress = progress,
            Token = token,
            SavedStatus = item.StatusText,
            SavedEta = item.EtaText,
            SavedProgress = item.Progress,
        };
        _previewRun = ctx;
        try { await RunBatchAsync(ctx); }
        finally { _previewRun = null; }
        return ctx.Done.Task.Status == TaskStatus.RanToCompletion ? ctx.Done.Task.Result : null;
    }

    /// <summary>
    /// 【唯一处理路径】「开始处理」(pv = null)与「预览效果」(pv != null)共用:
    /// 参数快照、引擎校验、流水线调用都只在这里读一次,避免两条路径参数漂移(预览与成片不一致)。
    /// </summary>
    private async Task RunBatchAsync(PreviewRun? pv)
    {
        // 防重入:处理中(含前置诊断扫描期间)禁止再次点击,避免并发启动两套处理循环
        if (_running || _runItems != null) return;
        // 只处理选中的项(勾选后):否则处理全部未完成的(已完成/灰色的默认跳过,不重复跑;点「重新处理」可调起)
        // (预览只跑用户点中的那一个视频,「只处理选中的项目」这个开关对它没有意义)
        bool onlySelected = pv == null && SelectedOnlyCheck.IsChecked == true;
        var items = pv != null
            ? new[] { pv.Item }
            : (onlySelected
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
        // 【用户裁决:帧率框不许留空】选了「指定帧率」却没填(或填了非正数)→ 拦下并说清,不静默当"按倍率"跑。
        // 放在这里(而不是只靠置灰按钮)是因为用户可能先填了值再清空 —— 那种情况下按钮仍可点。
        if (interp && IsTargetFpsMode() && SelectedTargetFps() is null)
        {
            Log("⚠ 输出帧率选了「指定帧率」但没填数值 —— 请填一个目标帧率(如 60 / 120),或改回「按倍率」。");
            await ShowPauseHintAsync("输出帧率选了「指定帧率」,但输入框是空的。\n请填一个目标帧率(如 60 / 120),或改回「按倍率」。");
            return;
        }
        // 引擎前置校验:缺引擎立即提示(超分/补帧分别查,含 ffmpeg)
        {
            var missing = new System.Collections.Generic.List<string>();
            if (up)
            {
                if (!SelectedEngineIsReal && EngineService.FindWaifu2x() is null) missing.Add("waifu2x 引擎");
                if (SelectedEngineIsReal && EngineService.FindRealESRGAN() is null) missing.Add("Real-ESRGAN 引擎");
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
            // 序号→倍率映射统一在 AlhPro.Core.InterpScaleMap(有单测)。原写 "SelectedIndex is 3 or 4"(=8x/12x)
            // 把 16x 漏了 —— 最高倍率反而没有耗时提示,故改用倍率判定(≥8x 即高倍率)。
            bool highRate = AlhPro.Core.InterpScaleMap.IsHighRate(CurrentScaleIndex());
            bool highTarget = SelectedTargetFps() is { } tfHi && tfHi >= 90;
            if (weakGpu && (highRate || highTarget))
            {
                var dlg = new ContentDialog
                {
                    Title = "高倍率补帧提醒",
                    Content = new TextBlock
                    {
                        Text = CurrentIsIntegratedGpu()
                            ? "当前用核显(共享内存),高倍率补帧(4x 及以上)可能极慢甚至失败。\n建议:改 2x,或点「一键开启兼容模式」自动加大缓冲防爆显存。"
                            : $"当前设备偏弱(显存仅 {SafeRender.TotalVramGB:0.#}GB),高倍率补帧(4x 及以上)可能因显存不足中途出错。\n建议:点「一键开启兼容模式」自动降低分块/批大小,或改 2x。",
                        TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                    },
                    PrimaryButtonText = "知道了,开始",
                    SecondaryButtonText = "一键开启兼容模式",   // 源头杜绝:自动降资源,避免中途显存耗尽致帧序错乱
                    CloseButtonText = "我先改参数",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = this.XamlRoot,
                };
                try
                {
                    var r = await dlg.ShowAsync();
                    if (r == ContentDialogResult.Secondary)
                    {
                        // 源头杜绝:自动开启兼容模式(降分块/批大小/单批),并黄字提醒用户已生效
                        FastModeCheck.IsChecked = true;
                        Log("⚠ 已开启「兼容模式」(自动降低分块/批大小,防止高倍率补帧中途显存耗尽)。建议先跑几秒小片段确认稳定。");
                        if (CompatHint != null) CompatHint.Text = "⚠ 已开启「兼容模式」:降低分块/批大小,防止高倍率补帧中途显存不足出错。";
                        if (CompatHintPanel != null) CompatHintPanel.Visibility = Visibility.Visible;
                    }
                    else if (r != ContentDialogResult.Primary) return;   // 点「我先改参数」→ 停止本次
                }
                catch { }
            }
        }
        // ===== Real-ESRGAN 在本机 GPU 上【实测】跑不通时提前提示 =====
        // 【不再按型号猜】原先这里是 "IsBlackwellGpu() 就弹窗",等于 50 系一律假定不兼容:
        // 引擎路由已改成真机探测(EngineService.EnsureNcnnProbeAsync),提示也必须以【实测结论】为准 ——
        // 只有已经测出"这台机的 realesrgan ncnn 不可用"(TryGetNcnnVerdict == false)才弹窗;
        // 没测过就交给运行时探测去定并明确告知,避免"先弹窗说不兼容、结果跑得比 waifu2x 还快"的说反话。
        if (up && IsBlackwellGpu() && SelectedEngineIsReal
            && EngineService.TryGetNcnnVerdict("realesrgan", AppSettings.GpuIndex) == false)
        {
            if (await AskBlackwellOldEngineAsync("Real-ESRGAN"))
                VideoEngineRadios.SelectedIndex = 1;   // 好,换成 waifu2x(界面上第二项;兼容 50 系,且最快)
        }
        // 自定义码率:选了该项但没填/填了非法值 → 提示并拦截(避免按"自动"悄悄处理)
        if (QualityCombo.SelectedIndex == 5 && ParseBitrate() <= 0)
        {
            await ShowPauseHintAsync("已选「自定义码率」,请在上方填入目标码率(Mbps),例如 8(1080p 常见清晰码率)");
            return;
        }

        var multi = pv == null && items.Length > 1;   // 预览永远是单视频:不建独立批次输出文件夹

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
        // 预览:输出到 %TEMP%\ALHPro\preview(跑完交回界面播放、随后删除),绝不往素材目录里扔文件
        var baseDir = pv != null
            ? Path.Combine(Path.GetTempPath(), "ALHPro", "preview")
            : (_customOutDir ?? Path.GetDirectoryName(items[0].Path)!);
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
        // 开始前诊断卡片(硬风险:会爆盘/高倍率补帧+资源紧/弱设备):用户取消则不启动。
        // 先置 _running=true + 禁用 RunBtn,防止诊断扫描(非模态长await)期间用户再次点击并发启动。
        _running = true;
        _runItems = items;
        RunBtn.IsEnabled = false;
        bool startUp = true;
        // 前置诊断卡片(爆盘/资源紧/弱设备)只对正式批处理有意义:预览就一小段,先问一堆风险只会碍事
        if (pv == null)
        {
            try { startUp = await ShowPreflightDiagAsync(items).ConfigureAwait(true); }
            catch { startUp = true; }
        }
        if (!startUp)
        {
            _running = false;
            _runItems = null;
            RunBtn.IsEnabled = true;   // 用户取消/改参数:恢复可点,不启动
            return;
        }
        _paused = false;
        _resumeTcs = null;
        // 预览:不动列表里该项的进度/状态(那是正式处理的显示位,预览改它会让用户以为已经处理完了)
        if (pv == null)
            foreach (var it in items) { it.Progress = 0; it.StatusText = ""; it.EtaText = ""; }   // 重跑时清掉上次状态
        RunBtn.IsEnabled = false;
        CancelBtn.IsEnabled = true;
        // 预览期间不给「暂停/恢复」(预览不接受暂停,让它们亮着只会误导)。「强制结束」保持可用。
        if (pv == null)
        {
            PauseBtn.IsEnabled = true;
            ResumeBtn.IsEnabled = false;
            UpdatePauseButtonVisual();   // 运行中未暂停:暂停按钮高亮蓝
            VideoProgress.Value = 0;
        }
        UpdateListButtons();   // 处理中锁死右侧列表的删除/清空按钮(暂停时解锁删除)

        // 从下拉 Tag 取模型名(Content 含体量/快慢展示,Tag 才是纯模型名)
        string SelModel(ComboBox cb, string fallback)
        {
            var it = cb.SelectedItem as ComboBoxItem;
            var tag = it?.Tag as string;
            return !string.IsNullOrEmpty(tag) ? tag : fallback;
        }
        var (engine, model) = VideoEngineRadios.SelectedIndex switch
        {
            // Real-ESRGAN(界面上排第一):从模型下拉 Tag 读模型名(默认 realesr-animevideov3)
            0 => ("realesrgan", SelModel(VideoEsrganModelCombo, "realesr-animevideov3")),
            // waifu2x(界面第二项):从模型下拉 Tag 读模型名(默认 models-cunet)
            _ => ("waifu2x", SelModel(VideoWaifu2xModelCombo, "models-cunet")),
        };
        // 视频降噪由现有「启用视频降噪 + 强度(弱/中/强)」统一驱动,不再单开一个 waifu2x 专用下拉(割裂):
        // waifu2x 引擎 → 强弱档直接当它的自带降噪 -n(模型更对症、不额外耗时);
        // 其它情况 → 拆帧阶段 nlmeans。映射与执行都在 VideoService 里完成。
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
        {
            inFps = double.TryParse(InputFpsBox.Text, NumberStyles.Float, inv, out f) && f > 0 ? f : null;
            // 【残留防线】框里的值若是从【别的视频】探测来的(上一个视频的残留值),绝不能当本视频的输入帧率用:
            // 输入帧率参与节奏换算(内容帧率 = 输入帧率 ÷ 拍数),用错会直接算错去重/补帧 —— 不只是显示错。
            // 常见触发:上一个视频处理完变灰(未删除)+ 新拖入一个 → 列表有 2 项、框已隐藏,但本次只处理未完成的
            // 那一个,框里的旧值就会被当成它的输入帧率。用户手填的值 owner 为 null,不受影响。
            if (inFps != null && _inputFpsOwner != null && !ReferenceEquals(_inputFpsOwner, items[0]))
            {
                Log($"⚠ 「输入帧率」框里的 {inFps:0.##}fps 是视频「{_inputFpsOwner.Name}」的探测值,与本次要处理的「{items[0].Name}」不是同一个 —— 已忽略,改按本视频自身帧率自动探测。");
                inFps = null;
            }
        }
        else if (fpsMode == 1)
            fpsOffset = FpsOffsetSlider.Value;
        var interpScale = AlhPro.Core.InterpScaleMap.Multiplier(CurrentScaleIndex());
        double? targetFps = SelectedTargetFps();
        var interpModel = SelectedInterpModel;
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
        // 【转场识别只留勾选框(2026-09-15)]阈值滑块已删:非 null = 已勾选(交给处理端开启切点保护),
        // 数值用内置常量(判据本身用的是 Core.SceneCutJudge 的内置阈值,这个数只在回退 ffmpeg scene 时用到)。
        double? sceneThreshold = SceneCheck.IsChecked == true && interp ? SceneThresholdBuiltIn : null;
        double? timeStep = null;   // 时间步功能已移除(引擎目录模式下 -s 无效),补帧用引擎默认时间步
        var tta = TtaCheck.IsChecked == true;
        if (!interp) targetFps = null;   // 指定输出帧率只在补帧时有意义(与去重可单独用不同)

        // 多视频输出目录/校验已提前到 _running 之前(见上方 baseDir 计算)
        // 预览:与预览面板的「取消」联动(任一处取消都能停),其余与正式处理逐字一致
        var cts = pv != null
            ? CancellationTokenSource.CreateLinkedTokenSource(pv.Token)
            : new CancellationTokenSource();
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
        if (up && SelectedEngineIsReal)
        {
            // 【口径统一到与处理流水线同一个入口(2026-09-12 自检发现)】这里原来调的是
            // EngineService.IsEngineGpuUsableAsync(engine, gpuId, ct) 这个 3 参重载 = fullFrame:false =
            // **320×240 小图**探测(15 秒)。而本项目自己实测过的失败形态恰恰是:
            //   "320×240 能跑、真帧尺寸(1080×1920)才静默出空帧/黑帧,退出码还是 0"(见 EngineService 里
            //   fullFrame 的注释与 VideoService 的调用点)—— 也就是说:小图探测通过 ≠ 能用,
            //   这个"兼容性检测"会给用户一个不可靠的 OK,而真正的问题要等到流水线里才暴露。
            // 改为调用与流水线【完全相同】的入口 EnsureNcnnProbeAsync,于是三件事一起对上:
            //   ①纯 NVIDIA 非 50 系走快速通道(秒回,不白等);②50 系/AMD/Intel 按生产帧尺寸 + 真实模型实测;
            //   ③结论按"引擎|GPU|模型"缓存 —— 流水线随后再查一次会直接命中缓存,不会重复探测两遍。
            TaskSummary.Text = "正在检测 Real-ESRGAN 显卡兼容性(首次约 15~60 秒,结论会记住)…";
            try { Log("正在检测 Real-ESRGAN 显卡兼容性(按生产帧尺寸实测,首次较慢,结论会记住)…"); } catch { }
            bool usable = await EngineService.EnsureNcnnProbeAsync("realesrgan", gpuId,
                SelModel(VideoEsrganModelCombo, "realesrgan-x4plus"), cts.Token);
            if (!usable)
            {
                var useWaifu = await AskBlackwellCompatibleAsync("Real-ESRGAN");
                var tcs = new System.Threading.Tasks.TaskCompletionSource();
                _ = DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        if (useWaifu && SelectedEngineIsReal)
                            VideoEngineRadios.SelectedIndex = 1;   // 换成 waifu2x(界面第二项;兼容+最快)
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
        // 【补帧输出帧率基准固定为"真实时间轴插值"】(下拉已删,内部 fpsMode=2)
        var fpsBaseNow = 2;
        var outExtNow = FormatCombo.SelectedIndex == 1 ? ".mkv" : ".mp4";
        var muteNow = MuteCheck.IsChecked == true;
        var mblurNow = MotionBlurCombo.SelectedIndex;
        var deshakeNow = DeShakeCheck.IsChecked == true;
        var vdenoiseNow = DenoiseToggle.IsChecked == true ? DenoiseStrongRadios.SelectedIndex + 1 : 0;
        var denoiseKindNow = DenoiseKindCombo?.SelectedIndex ?? 0;   // 0=两者 1=仅空间 2=仅时间
        var qualityNow = QualityCombo.SelectedIndex == 5 ? 0 : QualityCombo.SelectedIndex;
        var fastNow = FastModeCheck.IsChecked == true;
        // 【平滑时间轴:恒开(2026-09-15 用户定调,界面已移除该勾选框)】不再是用户可见选项 ——
        // 它不是偏好而是"输出时间轴与源片对齐"的正确性修正:只在源时间轴不均匀(有缺口)时才动手,
        // 源本来就均匀时逐字走老路径(实测 CFR 无缺口素材日志:`时间轴:无缺口,按原样`)。
        // 关掉它具体会失去什么,见 SceneDefaultPolicy/VideoView.xaml 里那段说明与 flatten 路径的注释。
        var smoothTimelineNow = true;
        var codecNow = CodecCombo.SelectedIndex == 1 ? 2 : 0;   // 0=H.264,1=H.265
        var bitrateNow = ParseBitrate();
        // 【可变帧率保护面板已删 ⇒ 不再读 VfrModeRadios】固定"自动":是否按真实时间戳排帧由处理端
        // 按**源素材探测**(item.IsVfr)决定,不再有用户开关。
        int postSP = (int)SharpenSlider.Value, postCL = (int)ClaritySlider.Value, postUM = (int)UsmSlider.Value,
            postDT = (int)DetailSlider.Value, postDB = 0,
            postAA = (int)PostAaSlider.Value,
            postEdge = (int)PostEdgeSlider.Value;   // 【边缘增强】0=关;默认由模型决定(见 RecommendedEdgeBoost)
        InitTaskStages(up, interp, dedupOn, sceneThreshold != null);
        _taskTotalCount = items.Length;
        _taskDoneCount = 0;
        // 预览不动主界面底部状态行(它是正式处理的显示位;预览期间去改会让用户以为全片正在跑)
        if (pv == null) TaskSummary.Text = $"等待处理:共 {items.Length} 个视频";
        int progressIndex = 0;
        // 任务前刷新空闲资源实测(开了其他软件后空闲骤降,批次档位要跟上);并记录日志便于排查
        SafeRender.RefreshFreeResources();
        SafeRender.RefreshIdleCpu();   // 处理前采样系统占用(引擎未启动,读数=其他软件真实占用)→ CPU 上限自适应
        {
            double fr = SafeRender.FreeRamGB;
            // 【口径 2026-09-13 变更】每批帧数不再"只看空闲内存"的一个固定值:还看【源帧数】与【补帧后总帧数】
            // (短素材不分批 / 设备好+长片 200~400 / 设备差 50)。这里只报"内存档基准"与设备档位;
            // 真正生效的每批帧数与批数,在每个任务处理前写进日志(「超分批决策:…命中规则:…」),不再在这里冒充。
            var tier = AlhPro.Core.RenderPolicy.TierFor(fr);
            string tierTxt = tier switch
            {
                AlhPro.Core.RenderPolicy.DeviceTier.Strong => "设备好",
                AlhPro.Core.RenderPolicy.DeviceTier.Normal => "设备正常",
                _ => "设备差",
            };
            int bs = SafeRender.GetVideoBatchSize();
            Log($"资源自检:空闲内存 {fr:0.#} GB / 空闲显存 {SafeRender.FreeVramText} → {tierTxt};内存档基准 {bs} 帧/批(实际每批 50~400,按素材长度定)");
            AppLogger.Info($"资源自检:空闲内存 {fr:0.#} GB / 空闲显存 {SafeRender.FreeVramText} → {tierTxt}(内存档基准 {bs} 帧/批;实际每批帧数/批数见各任务的「超分批决策」日志)");
        }
        // 预计时间:全局平均速度(已用时间 ÷ 已完成进度 → 总时长估计,再减已用 = 剩余)
        // 预计总时长初始估算:根据启用的处理项 + 每个视频的时长/帧率/分辨率,
        // 一开始就显示合理数值(偏保守,随时间慢慢对齐),不是从小变大校准
        double etaInitTotal = 0;
        var perfKey = PerfMemory.Fingerprint(engine, upscaleShrink1x ? 2.0 : scale, 1920, 1080,
            interpScale, dedupOn, vdenoiseNow, postSP + postCL + postUM + postDB + postAA + postEdge > 0);
        double? perFrameHist = PerfMemory.PerFrameFor(perfKey);   // 同配置上次实测(秒/帧,1080p 基准)
        int totalFramesEst = 0;
        foreach (var it in items)
        {
            try
            {
                var dur = await VideoService.ProbeDurationSeconds(it.Path);
                var fpsS = await Task.Run(() => VideoService.ProbeFps(it.Path));
                double fps = double.TryParse(fpsS, NumberStyles.Float, inv, out var pf) && pf > 0 ? pf : 30;
                var (w, h) = await VideoService.ProbeSizeAsync(it.Path);
                totalFramesEst += (int)Math.Max(1, dur * fps);
                // 【H · 2026-09-13 / 2026-09-16 清理】顺序交给单一判据(见 VideoService.EstimateProcessSeconds 的注释):
                // 界面不再自己算"是否走新顺序"(那份判据会与管线各写一份,回退时必然漏改一处)。
                etaInitTotal += VideoService.EstimateProcessSeconds(dur, fps, w, h,
                    up, upscaleShrink1x ? 2.0 : scale, engine, interp, interpScale, dedupOn,
                    DenoiseToggle.IsChecked == true ? DenoiseStrongRadios.SelectedIndex + 1 : 0,
                    postSP + postCL + postUM + postDB + postAA + postEdge > 0,
                    SafeRender.FreeRamGB);   // 传空闲内存 → 估算里计入"每批引擎启动开销 × 批数"
            }
            catch { etaInitTotal += 60; }
        }
        // 经验库校准:同配置有实测记录 → 按实测秒/帧重算。
        // 【取较大者,不做加权平均】:初始 ETA 宁可能偏保守 —— 一开始显示得乐观,后面只会一路"涨",
        // 观感就是"剩余时间越等越久";偏保守则单调往下收敛,越跑越准。(旧的 0.5/0.5 会把偏低的公式值拉进来。)
        if (perFrameHist.HasValue && totalFramesEst > 0)
        {
            double estHistory = 0;
            foreach (var it in items)
            {
                try
                {
                    var fpsS = await Task.Run(() => VideoService.ProbeFps(it.Path));
                    double fps = double.TryParse(fpsS, NumberStyles.Float, inv, out var pf) && pf > 0 ? pf : 30;
                    var (w, h) = await VideoService.ProbeSizeAsync(it.Path);
                    double areaN = Math.Max(0.25, (double)w * h / 2_073_600.0);
                    estHistory += (int)Math.Max(1, (it.Duration > 0 ? it.Duration : 1) * fps) * perFrameHist.Value * areaN;
                }
                catch { }
            }
            if (estHistory > 1 && estHistory > etaInitTotal)
            {
                etaInitTotal = estHistory;
                AppLogger.Info($"ETA 经验库:配置[{perfKey}] 上次实测 {perFrameHist.Value:0.###} 秒/帧 → 取保守值 {etaInitTotal:0} 秒(≥公式估算)");
            }
            else
            {
                AppLogger.Info($"ETA 经验库:配置[{perfKey}] 上次实测 {perFrameHist.Value:0.###} 秒/帧(估算 {estHistory:0} 秒)低于公式估算,按公式 {etaInitTotal:0} 秒(取较大者)");
            }
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
        DateTime lastStepFileLogAt = DateTime.MinValue;   // 文件日志节流:阶段内每 30 秒补一行进度(诊断用)
        string? stepLogFull = null;                   // 当前已显示的步骤完整行([hh:mm:ss] ▶ ...)
        var taskStart = DateTime.Now;
        DateTime lastEtaAt = DateTime.MinValue;
        DateTime lastPanelAt = DateTime.MinValue;   // 详情面板刷新节流(防高频报告刷 UI 卡顿)
        DateTime lastSpeedAt = DateTime.MinValue;   // 近期速度样本节流
        double lastEtaShown = -1;   // 上次显示的剩余(秒):轻 EMA 平滑,只压抖动
        // 当前处理阶段键(拆帧/补帧/超分/编码):阶段切换时才允许重置上面的单调基准 ——
        // 各阶段快慢本来差一个量级(4K 任务实测编码占 9376s/总共 9806s),不重置会让 ETA 停在旧阶段的乐观值上。
        string etaStageKey = "";
        // 【K2 · 2026-09-13】当前阶段的计时起点(阶段切换时重置):用于算"当前阶段剩余",
        // 作为"本片剩余/整批剩余"的下限 —— 否则按"已用÷进度"外推会在慢阶段末尾崩到 0
        // (真机:编码阶段自己还要 3.7 分钟,本片剩余却显示 0:30)。
        DateTime etaStageStartAt = DateTime.Now;
        double etaStageStartIdle = 0;
        // 编码阶段计时:经验库只记"处理阶段"耗时,编码/封装必须单独切出来(见任务结束处记账)。
        // 按【每段增量】累加而不是记"第一次进入编码的时刻":多视频批次里后续视频还要回到处理阶段,
        // 只记起点会把它们的处理时间也一起当成编码扣掉,处理阶段耗时就只剩第一个视频的。
        double encSecondsAcc = 0;             // 已封口的编码段累计
        DateTime? encSegStart = null;         // 当前正在进行、尚未封口的编码段起点
        // 整批剩余与本片剩余是两套数,分开显示:batchEtaTxt 拼到顶部状态行,单项 EtaText 只算当前这一片
        string batchEtaTxt = "";
        int itemStartIdleFor = -1;      // 当前单项 ETA 归属的 progressIndex,切视频时复位平滑历史
        double itemStartIdle = 0;       // 该片开始时的累计休息秒数(算纯处理耗时要减掉)
        double lastItemEtaShown = -1;
        bool inRest = false;
        DateTime restStartAt = DateTime.MinValue;
        double idleSeconds = 0;
        // 预览:进度/日志改由预览面板显示(不刷主界面进度条 —— 那会让用户以为全片正在处理)
        IProgress<(int pct, string msg)> progress = pv != null ? pv.Progress : new Progress<(int pct, string msg)>(t =>
        {
            // ===== 兜底:整段进度刷新不许把任务带崩 =====
            // Progress<T> 的回调跑在 UI 线程,里面全是原生控件操作(改文本/进度条/滚动/面板)。
            // 实测(50 系笔记本,只开去重时必现):这里抛出的 COMException(0x80070490 找不到元素)
            // 会走全局未处理异常 → 弹"程序遇到问题"→ 界面停止更新、任务被用户关掉后报一堆假错。
            // 去重开着时日志行更多、刷新更频繁,撞上布局的窗口就更多 —— 这才是"只有开去重才出现"的原因。
            // 单点都加了保护之后仍在此处兜一层:任何一项刷新失败都只是"这次少刷一点",任务继续跑。
            try
            {
            // 编码阶段计时:必须放在下面节流 return 之前 —— 漏采样,任务结束时就会把编码耗时
            // 一起算进"每帧推理成本"(实测把 0.24 秒/帧记成 7.04 秒/帧,偏差 29 倍)。
            // 判据用进度分区(96=编码起始,与 VideoService.StageProgressPct 的分段一致),不猜字符串。
            // 进入编码开始计一段,回到处理阶段(pct<96,即下一个视频开工)把这封口累加。
            if (t.pct >= 96.0)
            {
                encSegStart ??= DateTime.Now;
            }
            else if (encSegStart != null)
            {
                encSecondsAcc += (DateTime.Now - encSegStart.Value).TotalSeconds;
                encSegStart = null;
            }
            // 【R1 · 用户要求:清盘/清理这类信息要显示在左下角日志区、并且"无感"】
            // 带 `· ` 前缀的消息在【节流 return 之前】处理:
            //   · 不会被下面那条 100ms 节流吞掉(真机确认过"本批已释放 N 帧"这类提示会被吞,用户压根没见过);
            //   · 只往日志区追加一行(与 ▶ 阶段 / ✓ 完成 同一块),不动步骤行、不动进度条、不改 pct → 不抢注意力;
            //   · 前缀是低调的 `· `,不是警告级别(不进 ⚠ 黄字那套)。
            if (t.msg.StartsWith("· ", StringComparison.Ordinal))
            {
                Log(t.msg);
                return;
            }
            // ===== 节流(修复长视频"未响应"):补帧/超分每帧都 Report,Progress<T> 不合并、
            // 每个回调都跑重活(正则+字符串+O(N²) 计数+日志重建),100k+ 帧会把 UI 线程塞死。
            // 100ms 内只刷一次界面(约 10 次/秒,足够平滑),但最后 99% 总处理(收尾不漏)。
            if (t.pct < 99.0 && Environment.TickCount64 - _lastUiTick < 100) return;
            _lastUiTick = Environment.TickCount64;
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
                // 帧号防越界分两档:
                //  · 小幅超出(≤5%,引擎段内消息偶发"已处理 76/75"的收尾误差)→ 按 M 封顶,不出现"超总数"观感;
                //  · 大幅超出 → 预估本身偏低,必须如实显示真实帧号并标注"仍在出帧"。原先一律封顶,
                //    步骤行就永远停在"917/917"一动不动 —— 这正是"看着像死机"的来源(实测有用户连续
                //    8 天在拆帧 917/917 处点强制结束,而 ffmpeg 一直在写帧、看门狗也一直在被正常喂)。
                bool overEst = false;
                if (emNow < 0) emNow = 0;
                if (emTotal < emNow)
                {
                    if (emNow <= emTotal * 1.05) emTotal = emNow;
                    else overEst = true;
                }
                string cntTxt = overEst ? $"已处理 {emNow}/{emTotal},已超预估·仍在出帧" : $"已处理 {emNow}/{emTotal}";
                // 日志显示当前步骤:实时更新最后一行(500ms 节流);文件日志=阶段首行 + 每 30 秒一行(防刷爆又可诊断)
                if (lastLoggedStep != emStage)
                {
                    lastLoggedStep = emStage;
                    stepLogFull = null;   // 新阶段:下一步走"追加"分支
                    lastStepLogAt = DateTime.MinValue;
                    lastStepFileLogAt = DateTime.MinValue;
                }
                string stepNewFull = $"[{DateTime.Now:HH:mm:ss}] ▶ {emStage} 中({cntTxt})";
                var fpsM = System.Text.RegularExpressions.Regex.Match(t.msg, @"\(目标\s*(\d+(?:\.\d+)?)\s*fps\)");
                if (fpsM.Success)
                    stepNewFull = stepNewFull[..^1] + $"·{fpsM.Groups[1].Value}fps)";
                if (stepLogFull == null || DateTime.Now - lastStepLogAt >= TimeSpan.FromMilliseconds(500))
                {
                    bool first = stepLogFull == null;
                    lastStepLogAt = DateTime.Now;
                    // 文件日志:阶段首行 + 每 30 秒补一行。原先只记首行,任务卡住时整个阶段在诊断包里
                    // 只剩一条采样,无法区分"真僵死"与"慢但仍在跑"(实测有用户同一文件连卡 7 次都定位不了)。
                    // AppLogger 每行自带时间戳,故最后一条进度行的时间+帧号即冻结点。
                    if (first || DateTime.Now - lastStepFileLogAt >= TimeSpan.FromSeconds(30))
                    {
                        lastStepFileLogAt = DateTime.Now;
                        AppLogger.Info(first
                            ? $"▶ {emStage} 中({cntTxt})"
                            : $"… {emStage} 仍在进行({cntTxt})");
                    }
                    // 【包 try/catch】这几句是 UI 线程上的原生控件操作(改文本 + ScrollViewer.ChangeView),
                    // 实测会在布局繁忙时抛 COMException 0x80070490 / LayoutCycleException。崩在这里的后果不是
                    // "少刷一行日志",而是任务再也收不到界面更新、用户只看到"卡住",收尾时还会报一堆找不到路径的假错。
                    try
                    {
                        if (SuppressUiLogUpdates) stepLogFull = stepNewFull;   // 布局循环已检出:只记状态、不碰界面
                        else if (!first && stepLogFull != null
                            && VideoLogText.Text.EndsWith(stepLogFull, StringComparison.Ordinal))
                        {
                            // 原地替换最后一行(实时进度,不刷屏)
                            VideoLogText.Text = VideoLogText.Text.Substring(0, VideoLogText.Text.Length - stepLogFull.Length) + stepNewFull;
                        }
                        else
                        {
                            // 同样"一次赋值":先拼好、裁好,只改一次 Text(理由见 Log() 里的说明)
                            var snext = (VideoLogText.Text == "日志:等待任务..." ? "" : VideoLogText.Text + "\n") + stepNewFull;
                            var stepLines = snext.Split('\n');
                            if (stepLines.Length > 200) snext = string.Join("\n", stepLines.Skip(80)) + "\n";
                            VideoLogText.Text = snext;
                            ScrollLogToBottomDeferred();
                        }
                    }
                    catch (Exception ex) { NoteUiRefreshFailure("步骤行刷新/自动滚动", ex); }
                    stepLogFull = stepNewFull;
                }
                if (emTotal > 0)
                {
                    // 比例封顶 1.0:emNow 现在允许如实超出 emTotal(见上方 overEst),不封顶会让
                    // 超分段的 pctFine 冲到 100 以上(45+45×1.3=103.5)污染进度条。文字如实、比例封顶。
                    double ratio = Math.Min(1.0, (double)emNow / emTotal);
                    // 【区间来自 Core.ProgressBands】原先这里按阶段名硬编码(补帧 10+35、超分 45+45…),
                    // 与 VideoService / EngineService 各写一份 —— 用户反馈"补帧才完成、条就快到后面去了"
                    // 正是这几份不一致 + 权重与真实耗时脱节造成的。现在只有一份判据。
                    double fine = AlhPro.Core.ProgressBands.OfStageName(emStage) is var (fLo, fHi) && fHi > fLo
                        ? fLo + (fHi - fLo) * ratio
                        : -1;
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
                // 同"步骤行刷新":UI 原生操作必须包起来(见上面那段注释与 NoteUiRefreshFailure)
                try
                {
                    if (VideoLogText.Text.EndsWith(stepLogFull, StringComparison.Ordinal))
                        VideoLogText.Text = VideoLogText.Text.Substring(0, VideoLogText.Text.Length - stepLogFull.Length) + doneFull;
                    else
                    {
                        VideoLogText.Text = (VideoLogText.Text == "日志:等待任务..." ? "" : VideoLogText.Text + "\n") + doneFull;
                        ScrollLogToBottomDeferred();
                    }
                }
                catch (Exception ex) { NoteUiRefreshFailure("阶段完成行刷新/自动滚动", ex); }
                stepLogFull = doneFull;
                lastLoggedStep = null;   // 下一阶段重新出现步骤行
            }
            // 整体进度 = (已完成数 + 当前视频内部进度) / 当前剩余总数;
            // 暂停删除未处理项后,已完成数不变、剩余变少 → 进度条直接跳变更新
            // items 就是本批要处理的视频(items ⊆ _videos),故去掉 && _videos.Contains(否则每帧 O(N²),卡死)。
            int done = items.Count(IsItemDone);
            int active = items.Count(it => !IsItemDone(it));
            var overall = done + active > 0
                ? Math.Min(100.0, (done + pctFine / 100.0) / (done + active) * 100.0)
                : pctFine;
            VideoProgress.Value = Math.Max(VideoProgress.Value, overall);   // 浮点:无取整平台
            VideoStatus.Text = (done + active > 0 ? $"({done + 1}/{done + active}) {t.msg}" : t.msg) + batchEtaTxt;
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
                int doneAll = items.Count(IsItemDone);
                double etaProgress = Math.Min(99.9,
                    (doneAll + t.pct / 100.0) / Math.Max(1, items.Length) * 100.0);
                double initRemain = etaInitTotal - workElapsed;   // 初始估算的剩余(偏保守,线性递减)
                // ===== 【K2】当前阶段剩余(整片剩余的下限)=====
                // 阶段切换(如 超分→编码)是【合法跳变】:进度占比法在慢阶段会把剩余"追认"上去,
                // 此时重置单调基准(只认得出阶段才重置,空串不动,避免个别消息反复清零)。
                // 注意这段【不放在下面的 1 秒节流里】:"本片剩余"也要用它当下限,必须在外层作用域可见;
                // 顺带让阶段切换的检测不受 1 秒节流影响(更及时)。
                {
                    var stageKeyNow = EtaStageKey(t.msg);
                    if (stageKeyNow.Length > 0 && stageKeyNow != etaStageKey)
                    {
                        etaStageKey = stageKeyNow;
                        lastEtaShown = -1;
                        lastItemEtaShown = -1;
                        // 【K2】新阶段:重置"本阶段剩余"的计时起点(阶段切换是合法的基准重置点)
                        etaStageStartAt = now;
                        etaStageStartIdle = idleSeconds;
                    }
                }
                double stageRemain = 0;
                {
                    var (lo, hi) = StageSpanOf(etaStageKey);
                    if (hi > lo && pctFine >= lo)
                    {
                        double ratio = Math.Clamp((pctFine - lo) / (hi - lo), 0, 1);
                        double stageNet = (now - etaStageStartAt).TotalSeconds - Math.Max(0, idleSeconds - etaStageStartIdle);
                        stageRemain = AlhPro.Core.EtaText.StageRemainingSeconds(ratio, stageNet);
                    }
                }
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
                    // 单调(同阶段内只许变小)+ 【K2 硬下限】(整片剩余 ≥ 当前阶段剩余)——
                    // 规则抽到 Core.EtaText.ClampWholeRemaining(有单测)。下限优先于单调:
                    // 阶段还要几分钟是实测的确定信息,不能被"只许变小"这个观感优化压死。
                    remain = AlhPro.Core.EtaText.ClampWholeRemaining(remain, stageRemain, lastEtaShown);
                    lastEtaShown = remain;
                    batchEtaTxt = done + active > 1 && remain > 5
                        ? $" · 整批剩余 {FormatTime(remain)}"
                        : "";
                }
                else if (etaProgress < 2 && initRemain > 8 && workElapsed > 3)
                {
                    // 早期(进度<2%)没有帧数消息:用初始估算线性递减展示;同样只许减小
                    double remain = initRemain;
                    remain = AlhPro.Core.EtaText.ClampWholeRemaining(remain, 0, lastEtaShown);
                    lastEtaShown = remain;
                    batchEtaTxt = done + active > 1 && remain > 5 ? $" · 整批剩余 {FormatTime(remain)}" : "";
                }
                // ===== 本片剩余:只算当前这一个视频 =====
                // 用片内进度 pctFine 与该片自己的 StartTime,并减掉该片开始之后累计的降温休息时间。
                if (progressIndex != itemStartIdleFor)
                {
                    itemStartIdleFor = progressIndex;
                    itemStartIdle = idleSeconds;
                    lastItemEtaShown = -1;
                }
                if (it.IsProcessing && pctFine >= 1.0)
                {
                    double itemElapsed = (now - it.StartTime).TotalSeconds
                        - Math.Max(0, idleSeconds - itemStartIdle);
                    if (itemElapsed > 3)
                    {
                        double itemRemain = itemElapsed * (100.0 / Math.Min(99.9, pctFine) - 1.0);
                        if (lastItemEtaShown > 0 && itemRemain > 0)
                            itemRemain = 0.7 * itemRemain + 0.3 * lastItemEtaShown;
                        // 【K2】单调 + 硬下限:本片剩余同样不得低于"当前阶段剩余"(与整批同一规则、同一函数)——
                        // 真机 bug:编码阶段还有 3.7 分钟,本片剩余却显示 0:30。
                        itemRemain = AlhPro.Core.EtaText.ClampWholeRemaining(itemRemain, stageRemain, lastItemEtaShown);
                        lastItemEtaShown = itemRemain;
                        it.EtaText = itemRemain > 5 ? "本片剩余 " + FormatTime(itemRemain) : "";
                    }
                }
            }
            // 任务详情面板(节流:每帧报告只取 500ms 一次,防高频刷新拖慢界面)
            if (DateTime.Now - lastPanelAt >= TimeSpan.FromMilliseconds(500))
            {
                lastPanelAt = DateTime.Now;
                UpdateTaskPanel(t.msg);
            }
            SafeRender.ApplyRestUi(VideoStatus, CancelBtn, t.msg);   // 休息时:黄字加粗 + 按钮变「跳过休息」
            }
            catch (Exception ex) { NoteUiRefreshFailure("任务进度刷新(整体)", ex); }
        });

        int okCount = 0, failCount = 0;
        _failReasons.Clear();   // 本次任务的失败原因,清空重来
        var outputFiles = new System.Collections.Generic.List<string>();   // 本次成功生成的输出文件(弹窗高亮用)
        VideoLogText.Text = "";
        // GPU 型号(按引擎真实 -g 编号取;不能用注册表标签按引擎 id 索引——双卡机上顺序相反)
        string gpuName = "";
        try { gpuName = GpuInfo.GetEngineDeviceName(gpuId); } catch { }
        string devStr = gpuId >= 0 ? (gpuName.Length > 0 ? gpuName : $"GPU {gpuId}") : "CPU (软件计算)";
        Log($"开始处理:共 {items.Length} 个视频,计算设备:{devStr},版本:v{UpdateChecker.CurrentVersion}");
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
            // 【2026-09-15 Rev4】两支自训模型在日志里也带上"名字 + 实测速度"的括号(用户要求:这几处的括号内容
            // 写速度,不写"测试"这类定性词)。日志是排查"这次到底用的哪支模型、该有多快"的唯一凭据。
            $"超分={(up ? $"开({model}{AlhPro.Core.ExperimentalEsrgan.LogSuffix(model)}·{scaleLabel})" : "关")}" + (up && customRes ? $"·输出{outWidth}×{outHeight}" : "") + " | " +
            $"补帧={(interp ? $"{interpModel}·{interpScale}x{(tta ? "·TTA" : "")}·时间步{(timeStep ?? 0):0.00}" : "关")} | " +
            $"去重={dedupDesc} | " +
            $"转场识别={(sceneThreshold != null ? $"{sceneThreshold:0.00}" : "关")} | " +
            $"目标帧率={(targetFps is > 0 ? $"{targetFps:0.##}fps" : "随倍率")} | " +
            $"输出基准=真实时间轴(原帧率×倍率)(内置) | " +
            $"兼容模式={(FastModeCheck.IsChecked == true ? "开" : "关")} | " +   // 文案与界面控件名一致(界面叫「兼容模式」,内部字段仍叫 FastMode)
            // 【平滑时间轴:2026-09-15 起从界面移除、固定启用】日志里不再有"开/关"两态,但仍要留下这一项,
            // 否则排查"这次时间轴是怎么排的"就少了一条线索(读日志的人只需知道它是内置启用的)。
            $"平滑时间轴=开(内置) | " +
            // 【可变帧率保护面板已删 ⇒ 日志改为打印内建口径】仍保留这一项、信息不丢:
            // "自动"= 源是 VFR 就按真实时间戳排帧;括号里报告本次列表里是否检出 VFR 素材(与改动前同一口径)。
            $"VFR=自动({(items.Any(i => i.IsVfr) ? "检测到可变帧率" : "未检测到")};内置)");
        var trimmedCount = items.Count(i => i.IsTrimmed);
        if (trimmedCount > 0)
            Log($"裁剪:{trimmedCount} 个视频已应用裁剪范围(导出为裁剪后内容)");
        var postList = new System.Collections.Generic.List<string>();
        if ((int)SharpenSlider.Value > 0) postList.Add($"锐化{(int)SharpenSlider.Value}");
        if ((int)ClaritySlider.Value > 0) postList.Add($"清晰{(int)ClaritySlider.Value}");
        if ((int)UsmSlider.Value > 0) postList.Add($"钝化蒙版{(int)UsmSlider.Value}");
        if ((int)DetailSlider.Value > 0) postList.Add($"保留细节{(int)DetailSlider.Value}");
        if ((int)PostAaSlider.Value > 0) postList.Add($"边缘抗锯齿{(int)PostAaSlider.Value}");
        if ((int)PostEdgeSlider.Value > 0) postList.Add($"边缘增强{(int)PostEdgeSlider.Value}");
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
                // 【2026-09-16 用户实测:文件名与实际不符】原来只写**所选**倍率,而开了「指定帧率」时
                // 处理端会把它自动抬高以凑够帧数(实测:选 2x、目标 60fps ⇒ 实际按 3x 跑),
                // 于是文件名写着"补帧2x",成片其实是 3x —— 对账时看不出真相。
                // 这里用**与处理端同一个判据**(VideoPipeline.InterpScaleForTargetFps)算出生效倍率:
                // 两者不同时都写出来(`补帧2x(自动3x)`),不静默改写用户的选择,也不再留假信息。
                int effScaleForName = interpScale;
                if (interp && targetFps is > 0)
                {
                    try
                    {
                        double srcFpsForName = 0;
                        if (double.TryParse(item.FpsProbe, NumberStyles.Float, CultureInfo.InvariantCulture, out var fp0) && fp0 > 0)
                            srcFpsForName = fp0;
                        effScaleForName = AlhPro.Core.VideoPipeline.InterpScaleForTargetFps(
                            targetFps.Value, srcFpsForName, srcFpsForName,
                            IsV4ModelSelected());   // 与处理端同一判据(补帧模型是不是 v4 架构)
                    }
                    catch { effScaleForName = interpScale; }
                }
                // 【2026-09-16 用户反馈】指定了「输出帧率」时,名字里**只写帧率、不写倍率**:
                // 那个倍率只是为了凑到目标帧率而算出来的**内部中间量**(60fps 用 3x),写进文件名既误导又啰嗦。
                // 旧写法还会叠成三段重复 —— 真机实测(09-16 19:18 那次,指定 60fps)生成的名字是
                // 「…_补帧2x(自动3x)_rife413_60fps.mp4」。现在 = 「…_补帧60fps_rife413.mp4」。
                // 没指定帧率时才写实际倍率(与旧行为逐字一致)。
                string interpLabelForName = targetFps is > 0
                    ? $"{targetFps.Value:0.##}fps"
                    : (effScaleForName != interpScale ? $"{interpScale}x(自动{effScaleForName}x)" : $"{interpScale}x");
                var suffix = (up ? $"_超分{scaleLabel}_{UpscaleView.ModelShort(engine)}" : "")
                    + (customRes ? $"_自定义{outWidth}x{outHeight}" : "")
                    + (interp ? $"_补帧{interpLabelForName}_{UpscaleView.ModelShort(interpModel)}" : "")
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
                            // 保留补帧标记(指定帧率时写帧率、否则写倍率)与超分,去掉其他
                            outName = Path.GetFileNameWithoutExtension(item.Path)[..Math.Min(50, Path.GetFileNameWithoutExtension(item.Path).Length)]
                                + (interp ? (targetFps is > 0 ? $"_补帧{targetFps.Value:0.##}fps" : $"_补帧{interpScale}x") : "")
                                + (up ? $"_超分" : "") + outExt;
                            Log("⚠ 输出文件名仍过长,已最短化(带补帧/超分标记)");
                        }
                    }
                }
                catch { }
                // 预览:输出到临时目录(baseDir 已切到 %TEMP%\ALHPro\preview),文件名带时间戳,跑完由界面删除
                var outPath = pv != null
                    ? Path.Combine(baseDir, $"preview_{DateTime.Now:yyyyMMdd_HHmmss_fff}{outExtNow}")
                    : UpscaleView.UniquePath(baseDir, outName);
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
                    // 【必须在后台线程上跑整条流水线 —— 修"阶段中间短暂未响应"】
                    // 原先这里是 `await VideoService.ProcessVideoAsync(...)` 直接调用:RunBtn_Click 是 async void,
                    // 所以它带着 UI 线程上下文;而 ProcessVideoAsync 内部 131 处 await 里只有 28 处写了
                    // ConfigureAwait(false) —— 于是剩下 ~100 处续体【全部回到 UI 线程】执行。
                    // 而阶段切换处恰好都是重活:清源帧/中间帧目录(逐文件 File.Delete)、
                    // Directory.EnumerateFiles(...).Count() 数万帧、Directory.Delete(workDir, true)…
                    // 表现就是"补帧完换超分等中间操作短暂未响应"(真机日志的 [临时清理] 正落在这个位置;
                    // 5080 帧那种任务里这一段会明显卡住)。
                    // 放到线程池后:进度仍靠 IProgress 回报(Progress<T> 自带回 UI 线程),界面刷新不受影响;
                    // await 之后的 item.StatusText 等 UI 赋值仍在 UI 线程执行(ProcessOneAsync 自身带 UI 上下文)。
                    await Task.Run(() => VideoService.ProcessVideoAsync(item.Path, outPath,
                        engine, model, scale, up, interp, itemFps, interpScale, targetFps,
                        dedupOn ? dedupModel : 0, dedupThreshold, interpModel, sceneThreshold, timeStep, tta,
                        // 预览:只处理 [起点, 起点+长度] 这一段(按素材原始时间轴,与列表里的全片裁剪无关)
                        pv != null ? pv.Start : tStart,
                        pv != null ? pv.Start + pv.Length : tEnd,
                        gpuId,
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
                        // 输出帧率基准:下拉已于 2026-09-15 删除,固定"真实时间轴插值" ⇒ fpsMode 恒为 2
                        // (原 FpsBaseCombo 的另一个档"匀速帧速率插值"= fpsMode 1,会破坏原片定格节奏,已取消)
                        fpsMode: fpsBaseNow,
                        contentFps: contentFpsNow,
                        animeHoldN: animeHoldN,
                        tempoResample: false,   // 节奏重采样(实验)已下线:用标准补帧(同一引擎、更快、同样平滑)
                        postSharpen: postSP, postClarity: postCL,
                        postUsm: postUM, postDetail: postDT,
                        postDeblur: postDB,
                        postAa: postAA,
            postEdge: postEdge,
                        mute: muteNow,
                        postMotionBlur: mblurNow,
                        postDeshake: deshakeNow,
                        videoDenoise: vdenoiseNow,
                        denoiseKind: denoiseKindNow,
                        quality: qualityNow,
                        fastMode: fastNow,
                        smoothTimeline: smoothTimelineNow,   // 【S3】平滑时间轴:统一输出帧率 + 场景切换对齐
                        upscaleShrink1x: upscaleShrink1x,                        codecPref: codecNow,
                        customBitrateMbps: bitrateNow,
                        // 可变帧率(VFR)拆帧:固定"自动"(面板已删)= 加入列表时已探测(IsVfr),是 VFR 素材就
                        // 按原节奏逐帧提取(时间轴保真);普通素材无影响。**没有"不启用"这个入口了**。
                        vfrPassthrough: item.IsVfr,
                        // 预览:允许帧数偏少(3 秒本来就少,不该被"去重后帧太少"的防删光保护拦下)
                        allowFewFrames: allowFew || pv != null,
                        // 预览不接受暂停:传 null 直接跳过暂停等待点
                        pauseWait: pv != null ? null : PauseWaitAsync));
                    // 预览:成片直接交回预览面板播放 —— 不标完成、不自动移除、不写列表状态
                    // (全片处理的那套收尾全在 else 分支里,逐字未改)
                    // ⚠ 这里【不要】再 Report 进度:Progress 回调是投递到 UI 队列异步执行的,
                    //   它会在预览面板写好"✓ 预览已就绪(…MB · 用时 …秒)"之后才跑,把好信息覆盖掉(已真机复现)。
                    if (pv != null)
                    {
                        pv.Done.TrySetResult(outPath);
                    }
                    else
                    {
                    item.Progress = 100;
                    item.StatusText = "✓ 完成";
                    item.EtaText = "";   // 完成时清空预计时间
                    item.IsDone = true;   // 完成后项目变灰,默认不再重复处理
                    ScheduleAutoRemove(item);   // 设置开启时:3 秒后自动删除该项目
                    RefreshVideoProgress(items, $"完成 {item.Name}");
                    // 输出信息:帧率 / 分辨率 / 大小(去重结果用简短版,蓝色小字不截断;细节见日志)
                    try
                    {
                        // 注:ProbeVideoInfoAsync 内部已经是 return await Task.Run(...),本身不堵 UI 线程,无需再包一层
                        var outInfo = await VideoService.ProbeVideoInfoAsync(outPath);
                        var mb = new FileInfo(outPath).Length / 1048576.0;
                        var dedupShort = VideoService.LastDedupShort;
                        var dedupNote = VideoService.LastDedupReport;
                        item.OutputInfo = $"输出:{outInfo} · {mb:0.0} MB" +
                            (dedupShort != null ? $" · {dedupShort}" : "");
                        if (dedupNote != null) Log($"  {dedupNote}");   // 完整细节写入日志区,不再一闪而过
                        // 【2026-09-16 用户裁决:不再显示「含全黑片段 / 黑场自检」】
                        // 素材本来就会有黑幕、夜景、淡入淡出 —— 在列表项上挂 ⚠ 会把正常素材说成出错
                        // (用户原话:「有些人视频里面就是会有黑幕」)。事实仍由 VideoService 记一条**信息行**,
                        // 但界面不再把它当异常提示、也不再要求用户导出诊断包。
                    }
                    catch { }
                    UpdateTaskPanel("完成", finished: true);
                    outputFiles.Add(outPath);   // 记录成功输出(弹窗高亮/列名用)
                    Log($"  ✓ {Path.GetFileName(outPath)}");
                    Log($"    计算设备:{devStr} · 压缩编码器:{VideoService.LastVideoEncoderInfo}");
                    okCount++;
                    }   // ← if (pv != null) / else 的收尾(预览分支不走到这里)
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
                            var probe = await Task.Run(() => VideoService.ProbeFps(item.Path));
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
                        if (pv != null)
                        {
                            // 预览:用户放弃"仍要进行" → 结束本次预览(原因交回面板显示)
                            pv.Done.TrySetException(ex);
                            return;
                        }
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
                    AppLogger.Error($"视频处理失败: {item.Name}", ex);
                    if (pv != null)
                    {
                        // 预览:错误原样交回预览面板显示(不写列表状态、不计入失败统计、不弹结果窗)
                        pv.Done.TrySetException(ex);
                        return;
                    }
                    item.StatusText = "✗ 失败";
                    Log($"  ✗ 失败:{ex.Message}");
                    failCount++;
                }
            }
            // 预览:到此为止 —— 不刷主界面进度条、不写"最近一次任务摘要"、不记 ETA 经验库、不弹「处理完成」结果窗。
            // (finally 照样会跑:复位 _running/释放 cts/还原该项在列表里的显示状态)
            if (pv != null) return;
            VideoProgress.Value = 100;
            VideoStatus.Text = $"完成 {okCount} 个";
            var taskSpan = DateTime.Now - taskStart;
            Log($"任务结束:成功 {okCount},失败 {failCount},耗时 {(int)taskSpan.TotalMinutes}分{taskSpan.Seconds}秒,输出 {outputFiles.Count} 个文件");
            // 写入"最近一次任务摘要"(诊断包带上,快速定位黑帧/补帧/编码等问题,不用在超长日志里翻)
            try
            {
                var summary = new System.Text.StringBuilder();
                summary.AppendLine($"ALH Pro 视频任务 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                summary.AppendLine($"结果:成功 {okCount},失败 {failCount},耗时 {taskSpan.TotalSeconds:0.#}s,输出 {outputFiles.Count} 个");
                foreach (var it in items)
                {
                    try
                    {
                        summary.AppendLine($"  文件: {System.IO.Path.GetFileName(it.Path)}");
                        summary.AppendLine($"    状态: {it.StatusText}");
                        if (it.OutputInfo != null && it.OutputInfo.Length > 0) summary.AppendLine($"    输出: {it.OutputInfo}");
                    }
                    catch { }
                }
                if (_failReasons.Count > 0)
                {
                    summary.AppendLine("失败原因:");
                    foreach (var r in _failReasons) summary.AppendLine("  · " + r);
                }
                AppLogger.WriteTaskSummary(summary.ToString());
            }
            catch { }
            // 耗时经验库:全部成功才算有效样本(失败会扭曲每帧成本),记录"秒/帧"(按面积归一)。
            // 【只记"处理阶段"】taskSpan 涵盖拆帧+去重+补帧+超分+后处理+编码+封装+ffprobe 全过程,
            // 按"每帧推理成本"记账必须把编码/封装扣掉:真实诊断包里记录 7.04 秒/帧,而实际超分只有
            // 0.24 秒/帧(偏差 29 倍)—— 那次 4K 任务编码占 9376 秒 / 总共 9806 秒,编码被算成了推理成本。
            // 编码耗时 = 已封口的编码段 + 任务结束时仍在进行的那一段(封装/校验都在这段内,一并排除)
            double encSeconds = encSecondsAcc + (encSegStart.HasValue
                ? Math.Max(0, (DateTime.Now - encSegStart.Value).TotalSeconds)
                : 0);
            double processSeconds = Math.Max(0, taskSpan.TotalSeconds - encSeconds);
            AppLogger.Info($"阶段耗时拆分:处理阶段(拆帧/去重/补帧/超分/后处理){processSeconds:0.#} 秒 + 编码/封装 {encSeconds:0.#} 秒 = 总 {taskSpan.TotalSeconds:0.#} 秒");
            if (okCount > 0 && failCount == 0 && totalFramesEst > 0 && processSeconds > 10)
            {
                // 面积归一只按【帧数加权】:实际成本 = Σ(帧数ᵢ × 每帧成本 × 面积ᵢ),各视频面积不同时
                // 算术平均会系统性偏离(混合分辨率批次尤其明显)。权重用该片自己的帧数,口径与记录端一致。
                double areaWeighted = 0, framesWeight = 0;
                foreach (var it in items)
                {
                    try
                    {
                        var (w, h) = await VideoService.ProbeSizeAsync(it.Path);
                        var fpsS = await Task.Run(() => VideoService.ProbeFps(it.Path));
                        double fps = double.TryParse(fpsS, NumberStyles.Float, inv, out var pf2) && pf2 > 0 ? pf2 : 30;
                        double dur = it.Duration > 0 ? it.Duration : await VideoService.ProbeDurationSeconds(it.Path);
                        double frames = Math.Max(1.0, dur * fps);
                        areaWeighted += frames * Math.Max(0.25, (double)w * h / 2_073_600.0);
                        framesWeight += frames;
                    }
                    catch { }
                }
                double avgAreaN = framesWeight > 0 ? areaWeighted / framesWeight : 1.0;   // 探测全失败时回退 1080p 基准
                PerfMemory.Record(perfKey, processSeconds, totalFramesEst, avgAreaN);
                AppLogger.Info($"ETA 经验库:记录配置[{perfKey}] 处理阶段实测 {processSeconds:0} 秒/{totalFramesEst} 帧(已排除编码 {encSeconds:0} 秒,加权面积 {avgAreaN:0.##}) → {processSeconds / totalFramesEst / Math.Max(0.25, avgAreaN):0.###} 秒/帧");
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
            if (pv != null)
            {
                // 预览:还原这一项原有的显示(预览过程写进去的"等待处理.../进度"不该留在列表上)
                pv.Item.SetProcessing(false);
                pv.Item.StatusText = pv.SavedStatus;
                pv.Item.EtaText = pv.SavedEta;
                pv.Item.Progress = pv.SavedProgress;
            }
            else foreach (var it in _videos)
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
        if (fail <= 0) MainPage.MaybeShowSponsorPrompt();   // 仅全部导出成功才弹赞助提示(有失败不打扰)
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        // 「强制结束」始终停止当前任务(包括休息中);跳过休息请用底部右侧专属「跳过休息」按钮
        VideoStatus.Text = "正在停止...";
        Log("用户点击「强制结束」,正在停止任务");
        _cts?.Cancel();
    }
}
