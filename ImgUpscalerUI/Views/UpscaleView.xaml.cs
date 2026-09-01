using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;

namespace ALHPro.Views;

public sealed partial class UpscaleView : UserControl
{
    private bool _running;
    private string? _customOutDir;
    private CancellationTokenSource? _cts;
    private int _gpuCount;   // 枚举到的 GPU 数量(用于 gpuId 计算)
    private int _lastJpgQuality = 2;   // 上次 JPG 模式选中的码率档(0-4):切到 PNG 时把"无损"占位替换,保存时保留 JPG 真实档
    private bool _settingsLoaded;      // LoadSettings 完成后才允许保存(防构造期默认值覆盖用户设置)——修复"记不住格式"的守卫

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

    public UpscaleView()
    {
        this.InitializeComponent();
        NoiseCombo.SelectedIndex = 0;
        foreach (var m in EngineService.AnimeModels)
            ModelCombo.Items.Add(new ComboBoxItem { Content = m.Label });
        ModelCombo.SelectedIndex = 0;
        // 计算设备:统一在「设置」里选择(AppSettings.GpuIndex),页面不再显示下拉
        _gpuCount = GpuInfo.GetAdapterNames().Count;
        RefreshQualityCombo();   // 输出码率档位:按当前格式(PNG/JPG)填充对应选项

        // 模式联动:不支持的控件禁用并淡下去 + 提示,另给照片模式弥补选项
        ModeRadios.SelectionChanged += (_, _) =>
        {
            var isAnime = ModeRadios.SelectedIndex == 0;
            ModelCombo.IsEnabled = isAnime;
            NoiseCombo.IsEnabled = isAnime;
            ModelPanel.Opacity = isAnime ? 1.0 : 0.5;
            NoisePanel.Opacity = isAnime ? 1.0 : 0.5;
            NoiseHint.Visibility = isAnime ? Visibility.Collapsed : Visibility.Visible;
            PhotoDenoisePanel.Visibility = isAnime ? Visibility.Collapsed : Visibility.Visible;
            if (!isAnime)
            {
                ToolTipService.SetToolTip(ModelCombo, "waifu2x 模型仅动漫模式可用");
                ToolTipService.SetToolTip(NoiseCombo, "Real-ESRGAN 不支持降噪,请用下方「预处理降噪」");
            }
            else
            {
                ToolTipService.SetToolTip(ModelCombo, null);
                ToolTipService.SetToolTip(NoiseCombo, null);
            }
            UpdateScaleAvailability();   // 模型变化 → 倍率支持变化(如 waifu2x 无 4x 权重)
        };
        // 模型下拉变化同样刷新倍率可用性
        ModelCombo.SelectionChanged += (_, _) => UpdateScaleAvailability();
        // 增强滑块数值显示(各自独立,可叠加)
        DetailSlider.ValueChanged += (_, e) => DetailValue.Text = ((int)e.NewValue).ToString();
        SharpenSlider.ValueChanged += (_, e) => SharpenValue.Text = ((int)e.NewValue).ToString();
        ClaritySlider.ValueChanged += (_, e) => ClarityValue.Text = ((int)e.NewValue).ToString();
        DeblurSlider.ValueChanged += (_, e) => DeblurValue.Text = ((int)e.NewValue).ToString();
        UsmSlider.ValueChanged += (_, e) => UsmValue.Text = ((int)e.NewValue).ToString();
        EdgeSlider.ValueChanged += (_, e) => EdgeValue.Text = ((int)e.NewValue).ToString();
        DetailEnhanceSlider.ValueChanged += (_, e) => DetailEnhanceValue.Text = ((int)e.NewValue).ToString();
        DenoiseSlider.ValueChanged += (_, e) => DenoiseValue.Text = ((int)e.NewValue).ToString();
        AaSlider.ValueChanged += (_, e) => AaValue.Text = ((int)e.NewValue).ToString();
        DehazeSlider.ValueChanged += (_, e) => DehazeValue.Text = ((int)e.NewValue).ToString();

        ToolGrid.Items.CollectionChanged += (_, _) =>
        {
            UpdateRunState();
            UpdateFileInfo();
            // 暂停中删除未处理项目 → 进度条立即按剩余数量更新
            if (_running && _paused && _runItems != null)
                RefreshProgressBar(_runItems, "已暂停 · 可删除未处理的项目");
        };
        ToolGrid.ItemDoubleTapped += ToolGrid_ItemDoubleTapped;
        // 区域放大:裁剪浮层的"放大选区"按钮(用当前超分参数处理选区)
        ToolGrid.RegionUpscaleEnabled = true;
        ToolGrid.RegionUpscaleRequested += RegionUpscaleAsync;

        // 记住上次参数
        LoadSettings();
        // 照片模式「预处理降噪」未勾选 → 强度下拉禁用并变灰
        void SetPhotoDenoiseLevelEnabled(bool on)
        {
            DenoiseLevelCombo.IsEnabled = on;
            DenoiseLevelCombo.Opacity = on ? 1.0 : 0.5;
        }
        PreDenoiseCheck.Checked += (_, _) => SetPhotoDenoiseLevelEnabled(true);
        PreDenoiseCheck.Unchecked += (_, _) => SetPhotoDenoiseLevelEnabled(false);
        SetPhotoDenoiseLevelEnabled(PreDenoiseCheck.IsChecked == true);
        // 控件变化时保存
        ModeRadios.SelectionChanged += (_, _) => SaveSettings();
        ModelCombo.SelectionChanged += (_, _) => SaveSettings();
        ScaleRadios.SelectionChanged += (_, _) => SaveSettings();
        NoiseCombo.SelectionChanged += (_, _) => SaveSettings();
        TtaCheck.Checked += (_, _) => SaveSettings();
        TtaCheck.Unchecked += (_, _) => SaveSettings();
        FmtCombo.SelectionChanged += (_, _) => { RefreshQualityCombo(); SaveSettings(); };
        DetailSlider.ValueChanged += (_, _) => SaveSettings();
        SharpenSlider.ValueChanged += (_, _) => SaveSettings();
        ClaritySlider.ValueChanged += (_, _) => SaveSettings();
        DeblurSlider.ValueChanged += (_, _) => SaveSettings();
        UsmSlider.ValueChanged += (_, _) => SaveSettings();
        EdgeSlider.ValueChanged += (_, _) => SaveSettings();
        DetailEnhanceSlider.ValueChanged += (_, _) => SaveSettings();
        DenoiseSlider.ValueChanged += (_, _) => SaveSettings();
        AaSlider.ValueChanged += (_, _) => SaveSettings();
        DehazeSlider.ValueChanged += (_, _) => SaveSettings();
        PreDenoiseCheck.Checked += (_, _) => SaveSettings();
        PreDenoiseCheck.Unchecked += (_, _) => SaveSettings();
        DenoiseLevelCombo.SelectionChanged += (_, _) => SaveSettings();
        RememberCheck.Checked += (_, _) => SaveSettings();
        RememberCheck.Unchecked += (_, _) => SaveSettings();

        UpdateRunState();
    }

    // ---------- 参数记忆 ----------
    private static string SettingsFile => ParaPaths.SettingsFile("upscale-settings.json");

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsFile)) return;
            var d = System.Text.Json.JsonSerializer.Deserialize<UpscaleSettings>(
                File.ReadAllText(SettingsFile));
            if (d is null) return;
            // 诊断:记录设置文件读到的值与时间戳(排查"记不住格式/码率")
            AppLogger.Info($"[记忆] 图片设置加载: Fmt={d.Fmt}(0=JPG/1=PNG), ImgQualityMode={d.ImgQualityMode}, Remember={d.Remember}, 文件时间={File.GetLastWriteTime(SettingsFile):HH:mm:ss}");
            // 开关本身总是恢复;关闭时不恢复其他参数
            RememberCheck.IsChecked = d.Remember;
            if (!d.Remember) return;
            if (d.Mode is >= 0 and <= 1) ModeRadios.SelectedIndex = d.Mode;
            if (d.W2xModel is >= 0 && d.W2xModel < ModelCombo.Items.Count)
                ModelCombo.SelectedIndex = d.W2xModel;
            if (d.Scale is >= 0 and <= 4) ScaleRadios.SelectedIndex = d.Scale;
            if (d.Noise is >= 0 and <= 3) NoiseCombo.SelectedIndex = d.Noise;
            TtaCheck.IsChecked = d.Tta;
            SelectedOnlyCheck.IsChecked = d.SelectedOnly;
            // 计算设备已在全局设置(AppSettings),页面不再恢复旧 Gpu 值
            // 格式下拉顺序:0=JPG 1=PNG——存读一致(223 行存的就是 SelectedIndex),不再做旧语义迁移
            // (旧迁移 d.Fmt==0?1:0 会把新存的 1(PNG) 转回 0(JPG):用户选 PNG 重开变 JPG 的 bug 根源)
            if (d.Fmt is >= 0 and <= 1) FmtCombo.SelectedIndex = d.Fmt;
            // 关键:LoadSettings 在事件挂接前执行,设置 Fmt 不会触发 SelectionChanged→RefreshQualityCombo;
            // 必须手动刷新码率下拉(否则 PNG 时仍显示 JPG 的"默认(推荐)"档——用户反馈"PNG 出现 JPG 码率样式")
            RefreshQualityCombo();
            // 输出码率档位(仅 JPG 恢复;PNG 只有"无损"一项不恢复):
            // JPG 用 ImgQualityMode(0-4);旧版滑条值兼容映射;无有效档位保持"默认(推荐)"(RefreshQualityCombo 兜底)
            if (FmtCombo.SelectedIndex == 0)
            {
                if (d.ImgQualityMode is >= 0 and <= 4) ImgQualityCombo.SelectedIndex = d.ImgQualityMode;
                else if (d.ImgQuality is >= 1 and <= 100)
                {
                    // 旧版(滑条值 1~100)兼容映射到档位:≤75=低,≤85=中,≤95=默认,>95=超高
                    ImgQualityCombo.SelectedIndex = d.ImgQuality switch
                    {
                        <= 75 => 0,
                        <= 85 => 1,
                        <= 95 => 2,
                        _ => 3,
                    };
                }
                else ImgQualityCombo.SelectedIndex = 2;   // 默认(推荐)
            }
            if (d.Detail is >= 0 and <= 100) DetailSlider.Value = d.Detail;
            if (d.Sharpen is >= 0 and <= 100) SharpenSlider.Value = d.Sharpen;
            if (d.Clarity is >= 0 and <= 100) ClaritySlider.Value = d.Clarity;
            if (d.Deblur is >= 0 and <= 100) DeblurSlider.Value = d.Deblur;
            if (d.Usm is >= 0 and <= 100) UsmSlider.Value = d.Usm;
            if (d.Edge is >= 0 and <= 100) EdgeSlider.Value = d.Edge;
            if (d.DetailEnhance is >= 0 and <= 100) DetailEnhanceSlider.Value = d.DetailEnhance;
            if (d.Denoise is >= 0 and <= 100) DenoiseSlider.Value = d.Denoise;
            if (d.Aa is >= 0 and <= 100) AaSlider.Value = d.Aa;
            if (d.Dehaze is >= 0 and <= 100) DehazeSlider.Value = d.Dehaze;
            if (d.ImgQualityCustom is >= 1 and <= 100)
                ImgQualityCustomBox.Text = d.ImgQualityCustom.ToString(System.Globalization.CultureInfo.InvariantCulture);
            PreDenoiseCheck.IsChecked = d.PreDenoise;
            if (d.DenoiseLevel is >= 0 and <= 2) DenoiseLevelCombo.SelectedIndex = d.DenoiseLevel;
            if (!string.IsNullOrWhiteSpace(d.OutDir) && Directory.Exists(d.OutDir))
            {
                OutDirBox.Text = d.OutDir;
                _customOutDir = d.OutDir;
            }
            _settingsLoaded = true;   // 恢复完成,此后才允许保存(防构造期默认覆盖用户设置)
        }
        catch { /* 读取失败用默认值 */ }
        _settingsLoaded = true;   // 读取失败也放行(否则首次全新安装永远无法保存)
    }

    private void SaveSettings()
    {
        // 加载完成前禁止保存:构造期任何控件默认赋值可能触发事件→用默认值覆盖用户设置
        // (实测日志:启动时"进入页面→保存Fmt=0→加载Fmt=0"——保存抢先于加载,把用户 PNG(1) 覆盖回 JPG(0))
        if (!_settingsLoaded) return;
        try
        {
            var d = new UpscaleSettings
            {
                Remember = RememberCheck.IsChecked == true,
                Mode = ModeRadios.SelectedIndex,
                W2xModel = ModelCombo.SelectedIndex,
                Scale = ScaleRadios.SelectedIndex,
                Noise = NoiseCombo.SelectedIndex,
                Tta = TtaCheck.IsChecked == true,
                SelectedOnly = SelectedOnlyCheck.IsChecked == true,
                Fmt = FmtCombo.SelectedIndex,
                Detail = (int)DetailSlider.Value,
                Sharpen = (int)SharpenSlider.Value,
                Clarity = (int)ClaritySlider.Value,
                Deblur = (int)DeblurSlider.Value,
                Usm = (int)UsmSlider.Value,
                Edge = (int)EdgeSlider.Value,
                DetailEnhance = (int)DetailEnhanceSlider.Value,
                Denoise = (int)DenoiseSlider.Value,
                Aa = (int)AaSlider.Value,
                Dehaze = (int)DehazeSlider.Value,
                ImgQualityMode = FmtCombo.SelectedIndex == 0
                    ? (ImgQualityCombo.SelectedIndex is >= 0 and <= 4
                        ? ImgQualityCombo.SelectedIndex
                        : _lastJpgQuality)   // 下拉被清空的瞬间(Sel=-1):不落 2,用记忆档位
                    : _lastJpgQuality,   // PNG 时存上次 JPG 的真实档位(读回 JPG 时正确恢复,而非"低")
                ImgQualityCustom = ParseImgQualityCustom(),
                PreDenoise = PreDenoiseCheck.IsChecked == true,
                DenoiseLevel = DenoiseLevelCombo.SelectedIndex,
                OutDir = _customOutDir ?? "",
            };
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile,
                System.Text.Json.JsonSerializer.Serialize(d));
            // 诊断:记录每次保存(排查"改格式/码率后记不住")
            AppLogger.Info($"[记忆] 图片设置保存: Fmt={d.Fmt}, ImgQualityMode={d.ImgQualityMode}, QualityCombo.Sel={ImgQualityCombo.SelectedIndex}, _lastJpgQuality={_lastJpgQuality}, 时间={DateTime.Now:HH:mm:ss}");
        }
        catch { /* 保存失败忽略 */ }
    }

    private sealed class UpscaleSettings
    {
        public bool Remember { get; set; } = true;
        public int Mode { get; set; } = 0;
        public int W2xModel { get; set; } = 0;
        public int Scale { get; set; } = 1;
        public int Noise { get; set; } = 0;
        public bool Tta { get; set; } = false;
        public bool SelectedOnly { get; set; } = false;
        public int Fmt { get; set; } = 0;
        public int Detail { get; set; } = 50;
        public int Sharpen { get; set; } = 0;
        public int Clarity { get; set; } = 0;
        public int Deblur { get; set; } = 0;
        public int Usm { get; set; } = 0;
        public int Edge { get; set; } = 0;
        public int DetailEnhance { get; set; } = 0;
        public int Denoise { get; set; } = 0;
        public int Aa { get; set; } = 0;
        public int Dehaze { get; set; } = 0;
        public int ImgQualityMode { get; set; } = 2;   // JPG 输出码率档位:0低 1中 2默认 3超高 4自定义
        public int ImgQualityCustom { get; set; } = 92; // 自定义档位的质量值 1~100
        public int ImgQuality { get; set; } = 92;       // 旧版滑条值(兼容读取)
        public bool PreDenoise { get; set; } = false;
        public int DenoiseLevel { get; set; } = 0;
        public string OutDir { get; set; } = "";
    }

    /// <summary>当前计算设备:末项(CPU)返回 -1,其余为 GPU 编号。</summary>
    /// <summary>当前计算设备(全局设置):-1=CPU,≥0=GPU 编号(超出枚举数按 CPU 处理)。</summary>
    private int CurrentGpuId
        => AppSettings.GpuIndex >= 0 && AppSettings.GpuIndex < _gpuCount ? AppSettings.GpuIndex : -1;

    private void UpdateRunState()
    {
        RunBtn.IsEnabled = ToolGrid.Items.Count > 0 && !_running;
        // 耗时提示(黄色):启用耗时功能时,显示在"开始处理"下方(开什么显示什么)
        if (SpeedHint != null)
        {
            var slow = new System.Collections.Generic.List<string>();
            if (TtaCheck.IsChecked == true) slow.Add("高质量 TTA");
            if (ScaleRadios.SelectedIndex >= 3) slow.Add($"高倍率({ScaleRadios.SelectedIndex switch { 3 => "3x", _ => "4x" }})");
            bool enh = (int)SharpenSlider.Value > 0 || (int)DetailEnhanceSlider.Value > 0
                || (int)DenoiseSlider.Value > 0 || (int)AaSlider.Value > 0 || (int)DehazeSlider.Value > 0
                || (int)EdgeSlider.Value > 0;
            if (enh) slow.Add("细节增强");
            if (PreDenoiseCheck.IsChecked == true) slow.Add("预处理降噪");
            if (slow.Count > 0 && ToolGrid.Items.Count > 0 && !_running)
            {
                SpeedHint.Text = $"⚠ 已启用 {string.Join("、", slow)} 处理时间会增加";
                SpeedHint.Visibility = Visibility.Visible;
            }
            else SpeedHint.Visibility = Visibility.Collapsed;
        }
        // 引擎兼容自检(黄色同一提示区,优先于耗时提示;不限 50 系——任何 GPU 弱/不可用设备都提示):
        // Real-CUGAN(2022 ncnn)在 Blackwell/Vulkan 不可用设备无法 GPU → 建议换 waifu2x;照片模式已自动 ONNX
        if (ToolGrid.Items.Count > 0 && !_running)
        {
            if (ModeRadios.SelectedIndex == 0)
            {
                var idx = Math.Clamp(ModelCombo.SelectedIndex, 0, EngineService.AnimeModels.Length - 1);
                if (EngineService.AnimeModels[idx].Engine == "realcugan" && EngineService.OldNcnnGpuRisky())
                {
                    SpeedHint.Text = "⚠ 该设备与 Real-CUGAN(2022 版)不兼容,建议改用「waifu2x」(官方新版,更稳定)";
                    SpeedHint.Visibility = Visibility.Visible;
                }
            }
            else if (EngineService.ShouldUseOnnxEsrgan())
            {
                SpeedHint.Text = "✅ 已按此显卡自动选用稳定引擎处理(无需其他设置)";
                SpeedHint.Visibility = Visibility.Visible;
            }
        }
        PauseBtn.IsEnabled = _running && !_paused;
        ResumeBtn.IsEnabled = _running && _paused;
        UpdatePauseButtonVisual();
    }

    /// <summary>按当前模型刷新放大倍数可用性(引擎/模型原生权重决定):
    /// waifu2x 模型权重虽为 2x,但引擎实测 -s 3/-s 4 用级联(2x 跑两遍)输出正常、不崩溃,
    /// 故 3x/4x 已放开;Real-ESRGAN/Real-CUGAN 均有对应权重(或级联兜底),全亮。</summary>
    private void UpdateScaleAvailability()
    {
        if (Scale3xRadio == null || Scale4xRadio == null) return;
        // 当前引擎/模型:动漫模式 = ModelCombo 选中项;照片模式 = realesrgan
        // waifu2x 已实测支持级联 3x/4x,不再置灰;后续若新增仅支持 2x 的引擎可在此按 engine 判断
        SetRadioEnabled(Scale3xRadio, true);
        SetRadioEnabled(Scale4xRadio, true);
        if (ScaleHint != null)
            ScaleHint.Visibility = Visibility.Collapsed;
    }

    private static void SetRadioEnabled(RadioButton rb, bool on)
    {
        rb.IsEnabled = on;
        rb.Opacity = on ? 1.0 : 0.5;
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

    // 暂停:处理完当前项后停在下一项之前;暂停期间可删除未处理的项目
    private void PauseBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!_running || _paused) return;
        _paused = true;
        _resumeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ToolGrid.IsPaused = true;   // 解锁列表「删除」(只删未处理项)
        VideoService.SuspendActiveProcess();   // 冻结当前引擎进程:随点随停,零丢失
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

    /// <summary>进度回调 → UI:进度条显示整体进度(已处理图片数),只前进不回退。</summary>
    private int _progressIndex;     // 当前处理到第几张(0-based)
    private double _progressSegStart;  // 本阶段在单张内的起始权重
    private double _progressSegEnd;    // 本阶段在单张内的结束权重

    /// <summary>是否已处理完(成功或失败),用于动态总进度/暂停删除判断。</summary>
    private static bool IsItemDone(ImageItem it)
        => it.Progress >= 100 || it.StatusText.StartsWith("✗");

    private IProgress<(int pct, string msg)> CreateProgress()
        => new Progress<(int pct, string msg)>(t =>
        {
            double seg = _progressSegStart +
                (_progressSegEnd - _progressSegStart) * t.pct / 100.0;
            // 动态总数:暂停删除未处理项后,已完成数不变、剩余变少 → 进度条直接跳变更新
            int done = _runItems?.Count(it => IsItemDone(it) && ToolGrid.Items.Contains(it)) ?? 0;
            int active = _runItems?.Count(it => ToolGrid.Items.Contains(it) && !IsItemDone(it)) ?? 0;
            var overall = done + active > 0
                ? Math.Min(100.0, (done + seg / 100.0) / (done + active) * 100.0)
                : t.pct;
            TaskProgress.Value = Math.Max(TaskProgress.Value, (int)Math.Round(overall));
            TaskStatus.Text = done + active > 0
                ? $"({done + 1}/{done + active}) {t.msg}"
                : t.msg;
            SafeRender.ApplyRestUi(TaskStatus, CancelBtn, t.msg);   // 休息时:黄字加粗 + 按钮变「跳过休息」
            // 当前项的列表状态(暂停删除判断:处理中的项不可删)
            if (_runItems != null && _progressIndex >= 0 && _progressIndex < _runItems.Length)
            {
                var it = _runItems[_progressIndex];
                if (ToolGrid.Items.Contains(it) && !IsItemDone(it))
                {
                    it.Progress = Math.Max(it.Progress, (int)Math.Round(seg * 100));
                    it.StatusText = t.msg;
                }
            }
        });

    /// <summary>增强重置:恢复默认(无任何添加)。</summary>
    private void EnhanceResetBtn_Click(object sender, RoutedEventArgs e)
    {
        // 重置为默认:锐化 0,保留细节 50,其余 0
        SharpenSlider.Value = 0;
        DetailSlider.Value = 50;
        DetailEnhanceSlider.Value = 0;
        ClaritySlider.Value = 0;
        DeblurSlider.Value = 0;
        UsmSlider.Value = 0;
        EdgeSlider.Value = 0;
        DenoiseSlider.Value = 0;
        AaSlider.Value = 0;
        DehazeSlider.Value = 0;
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

    // ---------- 单击仅选中,不弹预览;双击打开大图预览 ----------
    private void ToolGrid_SelectionChanged(System.Collections.Generic.IReadOnlyList<ImageItem> items)
    {
        // 单击/框选只改变选中状态,预览由双击打开
    }

    private void ToolGrid_ItemDoubleTapped(ImageItem item)
    {
        try
        {
            PreviewImage.Source = new BitmapImage(new Uri(item.Path));
            PreviewOverlay.Visibility = Visibility.Visible;
        }
        catch (Exception) { }
    }

    private void PreviewClose_Click(object sender, RoutedEventArgs e)
        => PreviewOverlay.Visibility = Visibility.Collapsed;

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
        var files = await picker.PickMultipleFilesAsync();
        if (files != null && files.Count > 0)
        {
            await ToolGrid.AddImagesAsync(files.Select(f => f.Path));
            Log($"添加了 {files.Count} 张图片到列表");
        }
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
            OutDirBox.Text = folder.Path;
            _customOutDir = folder.Path;
            SaveSettings();
        }
    }

    // 手动编辑输出目录也生效(留空=源图目录)
    private void OutDirBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var t = OutDirBox.Text.Trim();
        _customOutDir = t.Length > 0 ? t : null;
        SaveSettings();
    }

    // ---------- 批量处理 ----------
    // 区域放大:只对框选区域做 AI 放大,产出新图加入列表
    private async void RegionUpscaleAsync(ImageItem item, int x, int y, int w, int h)
    {
        if (_running) return;
        var isAnime = ModeRadios.SelectedIndex == 0;
        string engine, model;
        if (isAnime)
        {
            var sel = EngineService.AnimeModels[Math.Clamp(ModelCombo.SelectedIndex, 0, EngineService.AnimeModels.Length - 1)];
            engine = sel.Engine;
            model = sel.Model;
        }
        else
        {
            engine = "realesrgan";
            model = "realesrgan-x4plus";
        }
        var scale = ScaleRadios.SelectedIndex switch { 0 => 1, 1 => 2, _ => ScaleRadios.SelectedIndex };
        var noise = NoiseCombo.SelectedIndex == 0 ? -1 : NoiseCombo.SelectedIndex - 1;   // 0=不降噪,1/2/3=弱/中/强(映射到 -n 0/1/2,整体偏轻避免揉成一团)
        var tta = TtaCheck.IsChecked == true;
        var gpuId = CurrentGpuId;
        var srcDir = Path.GetDirectoryName(item.OriginalPath.Length > 0 ? item.OriginalPath : item.Path)!;
        var baseDir = _customOutDir ?? srcDir;
        var outPath = UniquePath(baseDir,
            Path.GetFileNameWithoutExtension(item.Name) + $"_选区放大{scale}x_{ModelShort(engine)}.png");
        _running = true;
        _paused = false;
        _resumeTcs = null;
        ToolGrid.IsProcessing = true;
        PauseBtn.IsEnabled = false;   // 单张区域放大无暂停
        ResumeBtn.IsEnabled = false;
        PauseBtn.Style = null;        // 恢复普通样式(不留高亮残留)
        ResumeBtn.Style = null;
        try
        {
            Log($"→ 区域放大 {w}×{h} @({x},{y}) 倍数 {scale}x 引擎 {engine}/{model}");
            // 降温休息(每小时/温度墙):全软件覆盖,选区放大同样生效
            await SafeRender.RestIfDueAsync(0, null, CancellationToken.None);
            await EngineService.UpscaleRegionAsync(item.Path, outPath,
                x, y, w, h, engine, model, scale, noise, gpuId, tta);
            await ToolGrid.AddImagesAsync(new[] { outPath });
            StatusChanged?.Invoke($"选区放大完成 → {Path.GetFileName(outPath)}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
            await ShowErrorAsync("选区放大失败: " + ex.Message);
        }
        finally
        {
            _running = false;
            ToolGrid.IsProcessing = false;
            PauseBtn.IsEnabled = false;
            ResumeBtn.IsEnabled = false;
        }
    }

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
        // 引擎前置校验:所选算法缺引擎立即提示(不让它失败后才知道)
        var needEngine = ModeRadios.SelectedIndex == 0
            ? (EngineService.FindWaifu2x() is null ? "waifu2x 引擎" : null)
            : (EngineService.FindRealESRGAN() is null ? "Real-ESRGAN 引擎" : null);
        if (needEngine != null)
        {
            await ShowErrorAsync($"未找到{needEngine}(engines 目录缺失) — 请确认软件引擎包完整(程序目录 engines\\ 下应有 waifu2x / realesrgan 等文件夹),或重新安装/恢复引擎");
            return;
        }
        // 自定义码率:选了该项但没填/填了非法值 → 提示并拦截(避免按"默认"悄悄处理)
        if (ImgQualityCombo.SelectedIndex == 4 &&
            !(int.TryParse(ImgQualityCustomBox.Text.Trim(), out var qv) && qv >= 1 && qv <= 100))
        {
            await ShowErrorAsync("已选「自定义码率」,请在上方填入质量值(1~100),例如 92");
            return;
        }
        // 输出目录:多张时创建子文件夹——必须在 _running=true 之前(创建失败不再崩溃+永久卡死)
        var firstSrc = items[0].OriginalPath.Length > 0 ? items[0].OriginalPath : items[0].Path;
        var baseDir = _customOutDir ?? Path.GetDirectoryName(firstSrc)!;
        string outDir;
        if (items.Length >= 2)
        {
            var sub = $"放大输出_{DateTime.Now:yyyyMMdd_HHmmss}";
            outDir = Path.Combine(baseDir, sub);
        }
        else
        {
            outDir = baseDir;
        }
        try { Directory.CreateDirectory(outDir); }
        catch (Exception ex)
        {
            await ShowErrorAsync($"无法创建输出目录:{outDir}({ex.Message})");
            return;
        }
        _running = true;
        _paused = false;
        _resumeTcs = null;
        _runItems = items;
        var taskStart = DateTime.Now;   // 任务耗时统计
        foreach (var it in items) { it.Progress = 0; it.StatusText = ""; }   // 重跑时清掉上次状态
        ToolGrid.IsProcessing = true;
        RunBtn.IsEnabled = false;
        CancelBtn.IsEnabled = true;
        PauseBtn.IsEnabled = true;
        ResumeBtn.IsEnabled = false;
        UpdatePauseButtonVisual();   // 运行中未暂停:暂停按钮高亮蓝
        TaskProgress.Value = 0;
        TaskStatus.Text = "准备中...";

        var isAnime = ModeRadios.SelectedIndex == 0;
        string engine, model;
        if (isAnime)
        {
            var sel = EngineService.AnimeModels[Math.Clamp(ModelCombo.SelectedIndex, 0, EngineService.AnimeModels.Length - 1)];
            engine = sel.Engine;
            model = sel.Model;
        }
        else
        {
            engine = "realesrgan";
            model = "realesrgan-x4plus";   // 照片模式固定通用模型
        }
        bool upscaleShrink1x = ScaleRadios.SelectedIndex == 1;   // 1x 超分(2x 放大后缩回)
        var scale = ScaleRadios.SelectedIndex switch { 2 => 2, 3 => 3, 4 => 4, _ => 1 };
        var noise = NoiseCombo.SelectedIndex == 0 ? -1 : NoiseCombo.SelectedIndex - 1;   // 0=不降噪,1/2/3=弱/中/强(映射到 -n 0/1/2,整体偏轻避免揉成一团)
        var tta = TtaCheck.IsChecked == true;
        var gpuId = CurrentGpuId;
        var outExt = FmtCombo.SelectedIndex == 0 ? ".jpg" : ".png";   // 下拉顺序:0=JPG 1=PNG
        var sharpen = (int)SharpenSlider.Value;
        var detail = (int)DetailSlider.Value;
        var clarity = (int)ClaritySlider.Value;
        var deblur = (int)DeblurSlider.Value;
        var usm = (int)UsmSlider.Value;
        var edge = (int)EdgeSlider.Value;
        var detailEnhance = (int)DetailEnhanceSlider.Value;
        var denoise = (int)DenoiseSlider.Value;
        var aa = (int)AaSlider.Value;
        var dehaze = (int)DehazeSlider.Value;
        bool isPng = FmtCombo.SelectedIndex == 1;   // 下拉顺序:0=JPG 1=PNG
        // JPG 码率档位:0低(75) 1中(85) 2默认(92) 3超高(98) 4自定义(1~100)
        int imgQ = isPng ? 92 : ImgQualityCombo.SelectedIndex switch
        {
            0 => 75,
            1 => 85,
            3 => 98,
            4 => ParseImgQualityCustom(),
            _ => 92,
        };
        // PNG:始终原样无损输出(不重压缩,导出最快、画质无损、文件大小正常)——PNG 无画质差异,压缩只换更慢
        int pngCompress = isPng ? -1 : ImgQualityCombo.SelectedIndex switch
        {
            0 => 9,          // JPG 低:文件最小
            1 => 6,          // JPG 中
            2 => -1,         // JPG 默认:PNG 原样无损(选 JPG 格式时此处不生效)
            3 => 2,          // JPG 超高:文件大/保存快
            _ => 9 - (int)Math.Round(Math.Clamp(imgQ, 1, 100) / 100.0 * 7),   // JPG 自定义:反向映射 9~2
        };
        // 照片模式弥补:预处理降噪(先 waifu2x 照片模型降噪,再超分)
        var preDenoise = !isAnime && PreDenoiseCheck.IsChecked == true;
        var denoiseLevel = DenoiseLevelCombo.SelectedIndex + 1;   // 1-3

        // 输出目录已提前创建(见 _running 之前)
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var progress = CreateProgress();
        var outputFiles = new System.Collections.Generic.List<string>();   // 本次成功输出(弹窗高亮用)
        int okCount = 0, failCount = 0;
        try
        {
            int total = items.Length;
            TaskLogText.Text = "";
            // 任务前刷新空闲资源实测;并记录日志(开了其他软件后空闲骤降,分块档位要跟上)
            SafeRender.RefreshFreeResources();
            SafeRender.RefreshIdleCpu();   // 处理前采样系统占用→CPU 上限自适应(不卡其他软件)
            {
                double fr = SafeRender.FreeRamGB, fv = SafeRender.FreeVramGB;
                Log($"资源自检:空闲内存 {fr:0.#} GB / 空闲显存 {fv:0.#} GB → 分块 {SafeRender.GetTileSize()}");
                AppLogger.Info($"图片超分资源自检:空闲内存 {fr:0.#} GB / 空闲显存 {fv:0.#} GB → 分块 {SafeRender.GetTileSize()}");
            }
            // 输出码率显示:JPG=质量数值;PNG=无损原样(固定)
            var qualityDesc = outExt == ".jpg" ? $"输出质量={imgQ}" : "输出=无损(原样)";
            Log($"开始放大任务:共 {total} 张,引擎={engine}/{model},倍数={scale}x,格式={outExt.TrimStart('.')},设备={(gpuId >= 0 ? $"GPU {gpuId}" : "CPU (软件计算)")},{qualityDesc}");
            if (preDenoise) Log($"预处理降噪:已开启(强度 {"弱中强"[denoiseLevel - 1]})");
            Log($"输出目录:{outDir}");
            for (int i = 0; i < total; i++)
            {
                var item = items[i];
                _progressIndex = i;

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

                // 降温休息(每小时/温度墙):处理下一项前检查
                await SafeRender.RestIfDueAsync(i * 100 / Math.Max(1, total), progress, ct);

                // 输出名:自定义名优先,否则 原名_超分Nx_引擎简写;冲突自动加序号
                var baseName = !string.IsNullOrWhiteSpace(item.CustomName)
                    ? item.CustomName
                    : Path.GetFileNameWithoutExtension(item.Path) + $"_超分{scale}x_{ModelShort(engine)}";
                var outPath = UniquePath(outDir, baseName + outExt);

                string srcPath = item.Path;
                string? tmpDenoise = null;
                string? converted = null;   // 引擎解码失败时转码的临时输入
                bool succeeded = false;
                bool retried = false;
                Log($"→ ({i + 1}/{total}) 处理 {item.Name}");
                while (!succeeded)
                {
                    try
                    {
                        srcPath = converted ?? item.Path;
                        // 照片预处理降噪:waifu2x cunet 模型 1x 降噪(自带 1x 降噪模型)
                        if (preDenoise)
                        {
                            tmpDenoise = Path.Combine(Path.GetTempPath(),
                                $"imgup_denoise_{Guid.NewGuid():N}.png");
                            _progressSegStart = 0.0;
                            _progressSegEnd = 0.4;
                            progress.Report((0, "预处理降噪..."));
                            Log("  预处理降噪(waifu2x 1x)...");
                            await EngineService.UpscaleAsync(srcPath, tmpDenoise, "waifu2x",
                                "models-cunet", 1, denoiseLevel, gpuId, false, progress, ct);
                            srcPath = tmpDenoise;
                        }

                        _progressSegStart = preDenoise ? 0.4 : 0.0;
                        _progressSegEnd = 0.97;   // 给最后"画质增强"留 3%(否则超分就满 100%)
                        progress.Report((0, $"正在处理 {item.Name}..."));
                        // 智能自检选择:照片模式 + 50系(Blackwell,ncnn-Vulkan 会崩) + ONNX 模型存在
                        // → 走 ONNX 版(不走 Vulkan,稳定);否则 ncnn GPU(非 50 系更快更成熟)。
                        // 自检结果写日志+进度,用户一眼看懂走了哪条路。
                        bool useOnnx = engine == "realesrgan" && EngineService.ShouldUseOnnxEsrgan();
                        if (useOnnx)
                        {
                            Log("✅ 自检:已按当前显卡自动改用稳定引擎(直接处理,无需设置)");
                            progress.Report((0, "✅ 自检完毕:用稳定引擎处理..."));
                            await EsrganOnnxService.UpscaleAsync(srcPath, outPath, scale,
                                gpuId, progress, ct);
                        }
                        else
                        {
                            if (engine == "realesrgan" && EngineService.OldNcnnGpuRisky())
                                Log("⚠ 自检:当前显卡与老引擎不兼容且未找到稳定版,回退旧引擎(可能失败,建议改用 waifu2x)");
                            else
                                Log($"✅ 自检完毕:{(engine == "realesrgan" ? "ncnn GPU 引擎可用(快)" : "常规引擎")}");
                            await EngineService.UpscaleAsync(srcPath, outPath, engine,
                                model, scale, noise, gpuId, tta, progress, ct,
                                // 分块放大提速:图片超分逐块启动引擎,512 块=启动占约 2/3 时间;
                                // 1024 起步(块数约 1/4)省启动;显存不足由引擎自动降分块重试兜底,不会崩。
                                tileSize: SafeRender.GetTileSize() * 2,
                                upscaleShrink1x: upscaleShrink1x,
                                jpgQuality: imgQ / 100f, pngCompress: pngCompress);
                        }
                        succeeded = true;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        if (!retried)
                        {
                            retried = true;
                            try
                            {
                                // 输入可能不被引擎解码(部分 PNG/特殊编码),转码为标准 PNG 重试一次
                                converted = await EngineService.ConvertToStandardPngAsync(item.Path);
                                Log("  输入格式引擎无法解码,已转码为标准 PNG,重试...");
                                continue;
                            }
                            catch { /* 转码也失败,走失败流程 */ }
                        }
                        // 失败时清理本次已生成的不完整输出,避免"失败却有文件"的误解
                        try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
                        AppLogger.Error($"图片处理失败: {item.Name}", ex);
                        Log($"  ✗ 失败:{ex.Message}(不完整输出已清理)");
                        item.StatusText = "✗ 失败";
                        failCount++;
                        break;
                    }
                    finally
                    {
                        if (tmpDenoise != null)
                        {
                            try { File.Delete(tmpDenoise); } catch { /* 清理失败忽略 */ }
                            tmpDenoise = null;
                        }
                    }
                }
                // 清理转码临时输入
                if (converted != null)
                {
                    try { File.Delete(converted); } catch { /* 清理失败忽略 */ }
                }
                if (!succeeded) continue;   // 失败:继续下一张

                // 增强后处理:去雾 / 减少杂色 / 锐化 / 保留细节 / 细节增强 / 清晰 / 去模糊 / 钝化蒙版 / 边缘增强 / 边缘抗锯齿(任一开启即执行)
                if (sharpen > 0 || detail > 0 || detailEnhance > 0 || clarity > 0 || deblur > 0 || usm > 0 || edge > 0
                    || denoise > 0 || aa > 0 || dehaze > 0)
                {
                    _progressSegStart = 0.97;
                    _progressSegEnd = 1.0;
                    progress.Report((0, "画质增强..."));
                    var enhList = new System.Collections.Generic.List<string>();
                    if (dehaze > 0) enhList.Add($"去雾{dehaze}");
                    if (denoise > 0) enhList.Add($"减少杂色{denoise}");
                    if (detail > 0) enhList.Add($"保留细节{detail}");
                    if (detailEnhance > 0) enhList.Add($"细节增强{detailEnhance}");
                    if (clarity > 0) enhList.Add($"清晰{clarity}");
                    if (usm > 0) enhList.Add($"钝化蒙版{usm}");
                    if (deblur > 0) enhList.Add($"去模糊{deblur}");
                    if (edge > 0) enhList.Add($"边缘增强{edge}");
                    if (sharpen > 0) enhList.Add($"锐化{sharpen}");
                    if (aa > 0) enhList.Add($"边缘抗锯齿{aa}");
                    Log($"  画质增强({string.Join(" / ", enhList)})...");
                    try
                    {
                        await Task.Run(() => EngineService.EnhanceImage(outPath, sharpen, detail, clarity, deblur, usm, edge, detailEnhance, progress, denoise: denoise, aa: aa, dehaze: dehaze,
                            jpgQuality: imgQ / 100f, pngCompress: pngCompress), ct);
                    }
                    catch (Exception ex)
                    {
                        // 单张增强失败不中断整批(如文件被占用)
                        Log($"  ⚠ 增强失败(已跳过):{ex.Message}");
                    }
                }
                // 输出信息:分辨率 / 大小
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
            var taskSpan = DateTime.Now - taskStart;
            Log($"任务结束:成功 {okCount} 张,失败 {failCount} 张,耗时 {(int)taskSpan.TotalMinutes}分{taskSpan.Seconds}秒");
            StatusChanged?.Invoke($"完成 {okCount} 张 → {outDir}");
            await ShowResultAsync(okCount, outDir, outputFiles, failCount);
        }
        catch (OperationCanceledException)
        {
            TaskStatus.Text = "已取消";
            var cancelSpan = DateTime.Now - taskStart;
            Log($"任务已取消(已完成 {okCount} 张,失败 {failCount} 张,耗时 {(int)cancelSpan.TotalMinutes}分{cancelSpan.Seconds}秒)");
            StatusChanged?.Invoke("已取消");
        }
        catch (Exception ex)
        {
            TaskStatus.Text = "失败";
            AppLogger.Error("图片任务中断", ex);
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
            ToolGrid.IsProcessing = false;
            ToolGrid.IsPaused = false;
            CancelBtn.IsEnabled = false;
            UpdateRunState();
        }
    }

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

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        // 「强制结束」始终停止当前任务(包括休息中);跳过休息请用底部右侧专属「跳过休息」按钮
        TaskStatus.Text = "正在停止...";
        Log("用户点击「强制结束」,正在停止任务");
        _cts?.Cancel();
    }

    private void ImgQualityCombo_Changed(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
    {
        // 选「自定义码率...」(JPG 档)时显示质量输入行;其余档位隐藏
        if (ImgQualityCustomRow != null)
            ImgQualityCustomRow.Visibility = ImgQualityCombo.SelectedIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
        // JPG 下每次选档即记忆(0-4),保证切 PNG 再切回 JPG 不丢档
        if (!IsPngFormat && ImgQualityCombo.SelectedIndex is >= 0 and <= 4)
            _lastJpgQuality = ImgQualityCombo.SelectedIndex;
        SaveSettings();
    }

    private bool IsPngFormat => FmtCombo.SelectedIndex == 1;   // 下拉顺序:0=JPG 1=PNG

    /// <summary>按当前输出格式填充码率档位下拉:PNG=1 档(无损占位),JPG=5 档(低/中/默认/超高/自定义)。
    /// 格式切换时调用;JPG 档位各自记忆(切 PNG 不丢)。</summary>
    private void RefreshQualityCombo()
    {
        if (ImgQualityCombo == null) return;
        var isPng = IsPngFormat;
        // 记住当前选中,切格式后从对应档位恢复
        int prev = ImgQualityCombo.SelectedIndex;
        // 关键:PIN 模式只有 1 项占位"无损"(index=0),它不代表 JPG 档位——否则切回 JPG 时
        // 被当成"低(文件小)"(index=0)选中,并把 _lastJpgQuality 覆盖成 0(用户反馈"切JPG默认是低"的根因)
        bool wasPngPlaceholder = ImgQualityCombo.Items.Count <= 1;
        ImgQualityCombo.Items.Clear();
        if (isPng)
        {
            // PNG 无损:只有「无损」一项且不可调整(置灰)——PNG 无画质差异,压缩只换更慢
            ImgQualityCombo.Items.Add(new ComboBoxItem { Content = "无损" });
            ImgQualityCombo.SelectedIndex = 0;
            ImgQualityCombo.IsEnabled = false;
            ImgQualityCombo.Opacity = 0.55;
        }
        else
        {
            ImgQualityCombo.Items.Add(new ComboBoxItem { Content = "低 (文件小)" });
            ImgQualityCombo.Items.Add(new ComboBoxItem { Content = "中" });
            ImgQualityCombo.Items.Add(new ComboBoxItem { Content = "默认 (推荐)" });
            ImgQualityCombo.Items.Add(new ComboBoxItem { Content = "超高 (文件大)" });
            ImgQualityCombo.Items.Add(new ComboBoxItem { Content = "自定义码率..." });
            // 从 PNG 切回 JPG 时,prev 是 PNG 占位(0),不能用 → 用记忆的 JPG 档位;初次(prev<0)也用记忆(默认 2=推荐)
            int pick = wasPngPlaceholder ? _lastJpgQuality : prev;
            ImgQualityCombo.SelectedIndex = pick is >= 0 and <= 4 ? pick : 2;
            ImgQualityCombo.IsEnabled = true;
            ImgQualityCombo.Opacity = 1.0;
        }
        // 恢复显隐:JPG 自定义档才显示输入行
        if (ImgQualityCustomRow != null)
            ImgQualityCustomRow.Visibility = !isPng && ImgQualityCombo.SelectedIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ImgQualityCustomBox_TextChanged(object sender, Microsoft.UI.Xaml.Controls.TextChangedEventArgs e)
        => SaveSettings();

    /// <summary>解析自定义码率输入(1~100);非法/空返回 92(默认档)。</summary>
    private int ParseImgQualityCustom()
    {
        if (int.TryParse(ImgQualityCustomBox.Text.Trim(), out var v) && v >= 1 && v <= 100)
            return v;
        return 92;
    }

    /// <summary>模型英文简写(导出命名用)。</summary>
    public static string ModelShort(string key) => key switch
    {
        "realesrgan" => "esrgan",
        "realcugan" => "cugan",
        "isnet-general-use" => "isnet",
        "rife-v4.13" => "rife413",
        "rife-v4.26" => "rife426",
        "rife-v4.6" => "rife46",
        "rife-v2.3" => "rife23",
        "rife-HD" => "rife-hd",
        "rife-UHD" => "rife-uhd",
        _ => key,
    };

    /// <summary>生成不冲突路径:存在则追加 (2)、(3)...</summary>
    public static string UniquePath(string dir, string fileName)
    {        var candidate = Path.Combine(dir, fileName);
        if (!File.Exists(candidate)) return candidate;
        var ext = Path.GetExtension(fileName);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        for (int i = 2; ; i++)
        {
            candidate = Path.Combine(dir, $"{baseName} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private async Task ShowResultAsync(int count, string dir, System.Collections.Generic.List<string>? outputFiles = null, int failLately = 0)
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
        // 失败可见性:完成弹窗必须列出失败数,不静默(避免"已处理 0 张"却看不出原因)
        if (failLately > 0)
        {
            dlg.Title = $"处理完成(失败 {failLately} 张)";
            dlg.Content = new TextBlock
            {
                Text = $"已处理 {count} 张图片(失败 {failLately} 张)\n输出目录:\n{dir}{listText}\n\n失败原因见日志(每张失败都有记录)",
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
            };
        }
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
