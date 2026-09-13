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
    // ===== 算法模式:界面顺序 Real-ESRGAN(上,索引 0) / waifu2x(下,索引 1) =====
    // 存盘与内置预设沿用旧约定(0=waifu2x / 1=Real-ESRGAN),靠下面两个换算函数解耦,
    // 这样老用户存过的模式和已有预设不会被界面顺序调整翻转。
    private bool IsAnimeMode => ModeRadios.SelectedIndex == 1;          // waifu2x(动漫)
    private static int ModeToStored(int uiIndex) => uiIndex == 1 ? 0 : 1;  // ui 1=waifu → 存 0
    private static int ModeFromStored(int stored) => stored == 0 ? 1 : 0;  // 存 0=waifu → ui 1
    private int _lastJpgQuality = 2;   // 上次 JPG 模式选中的码率档(0-4):切到 PNG 时把"无损"占位替换,保存时保留 JPG 真实档
    private bool _settingsLoaded;      // LoadSettings 完成后才允许保存(防构造期默认值覆盖用户设置)——修复"记不住格式"的守卫
    private bool _suppressEvents;      // 应用预设/加载时抑制控件事件触发 SaveSettings(防覆盖)

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
        // ModelCombo 按模式动态填充:动漫=waifu2x 模型,照片=Real-ESRGAN 模型(选项显示模型名)
        PopulateModelCombo(isAnime: true);
        ModelCombo.SelectedIndex = 0;
        // 计算设备:统一在「设置」里选择(AppSettings.GpuIndex),页面不再显示下拉
        _gpuCount = GpuInfo.EngineDeviceCount;
        RefreshQualityCombo();   // 输出码率档位:按当前格式(PNG/JPG)填充对应选项
        EnsureBuiltinImgPresets();   // 确保自带图片预设存在(官方预设)

        // 【界面顺序:Real-ESRGAN(索引 0)在上、waifu2x(索引 1)在下】与视频页统一;
        // 存盘/预设沿用旧约定(0=waifu2x, 1=Real-ESRGAN),经 ModeFromStored/ModeToStored 换算,
        // 这样老用户存的选择和已有预设不会被顺序调整翻转。
        ModeRadios.SelectionChanged += (_, _) =>
        {
            var isAnime = ModeRadios.SelectedIndex == 1;
            AppLogger.UserAction($"图片:切换算法 → {(isAnime ? "waifu2x" : "Real-ESRGAN")}");
            ModelCombo.IsEnabled = true;
            NoiseCombo.IsEnabled = isAnime;   // Real-ESRGAN 不支持降噪,照片模式禁用
            ModelPanel.Opacity = 1.0;
            NoisePanel.Opacity = isAnime ? 1.0 : 0.5;
            NoiseHint.Visibility = isAnime ? Visibility.Collapsed : Visibility.Visible;
            PhotoDenoisePanel.Visibility = isAnime ? Visibility.Collapsed : Visibility.Visible;
            if (!isAnime)
                ToolTipService.SetToolTip(NoiseCombo, "Real-ESRGAN 不支持降噪,请用下方「预处理降噪」");
            else
                ToolTipService.SetToolTip(NoiseCombo, null);
            PopulateModelCombo(isAnime);
            UpdateScaleAvailability();   // 模型变化 → 倍率支持变化(如 waifu2x 无 4x 权重)
        };
        // 按模式填充模型下拉(选项显示模型名)
        void PopulateModelCombo(bool isAnime)
        {
            var saved = ModelCombo.SelectedIndex;
            ModelCombo.Items.Clear();
            if (isAnime)
                foreach (var m in EngineService.AnimeModels)
                    ModelCombo.Items.Add(new ComboBoxItem { Content = m.Label });   // Label=模型名
            else
                foreach (var m in EngineService.PhotoModels)
                    ModelCombo.Items.Add(new ComboBoxItem { Content = m.Label });   // Label=模型名
            // 尽量保留上次选中(跨模式按新列表 index 兜底);否则默认第一个
            ModelCombo.SelectedIndex = (saved >= 0 && saved < ModelCombo.Items.Count) ? saved : 0;
        }
        // 模型下拉变化同样刷新倍率可用性
        ModelCombo.SelectionChanged += (_, _) =>
        {
            AppLogger.UserAction($"图片:选择模型 → {(ModelCombo.SelectedItem as ComboBoxItem)?.Content}");
            UpdateScaleAvailability();
        };
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
        ScaleRadios.SelectionChanged += (_, _) => { SaveSettings(); RefreshOutSpec(); };
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
            if (!File.Exists(SettingsFile))
            {
                // 首次使用(无设置文件):必须放行保存(_settingsLoaded=true),否则用户第一次改参数就被
                // 下方 SaveSettings 的 !_settingsLoaded 守卫挡住 → 图片页永远记不住、文件永不创建(实测 upscale-settings.json 缺失)
                _settingsLoaded = true;
                return;
            }
            var d = System.Text.Json.JsonSerializer.Deserialize<UpscaleSettings>(
                File.ReadAllText(SettingsFile));
            if (d is null) return;
            // 诊断:记录设置文件读到的值与时间戳(排查"记不住格式/码率")
            AppLogger.Info($"[记忆] 图片设置加载: Fmt={d.Fmt}(0=JPG/1=PNG), ImgQualityMode={d.ImgQualityMode}, Remember={d.Remember}, 文件时间={File.GetLastWriteTime(SettingsFile):HH:mm:ss}");
            // 开关本身总是恢复;关闭时不恢复其他参数
            RememberCheck.IsChecked = d.Remember;
            if (!d.Remember) return;
            if (d.Mode is >= 0 and <= 1) ModeRadios.SelectedIndex = ModeFromStored(d.Mode);
            // 模型同 ApplyImgSettings:优先按模型名定位,找不到才退回下标(两处必须同一读法)
            int lmi = FindModelIndexByName(d.W2xModelName, d.Mode == 1);
            if (lmi < 0) lmi = d.W2xModel;
            if (lmi >= 0 && lmi < ModelCombo.Items.Count)
                ModelCombo.SelectedIndex = lmi;
            // 倍率存的就是 4 项单选索引(0=1x超分 1=2x 2=3x 3=4x,与 SaveSettings 写入侧一致),读侧必须原样 Clamp。
            // 再按"旧版五项"做 -1 映射就会把存 3(4x) 读成 2(3x) —— "4x 重启变 3x / 2x 变 1x" 的根因。
            // 旧文件里残留的 4(旧语义 4x)由 Clamp 收敛到 3=4x,方向正确(0~3 两套语义重叠,无法逐值区分)。
            if (d.Scale is >= 0 and <= 4)
                ScaleRadios.SelectedIndex = Math.Clamp(d.Scale, 0, 3);
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
        // 应用预设期间(_suppressEvents)也禁止保存:避免中途部分状态写盘,由 ApplyImgSettings 末尾统一保存
        if (!_settingsLoaded || _suppressEvents) return;
        try
        {
            var d = new UpscaleSettings
            {
                Remember = RememberCheck.IsChecked == true,
                Mode = ModeToStored(ModeRadios.SelectedIndex),
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

    /// <summary>当前模式选中模型的【引擎侧名】(models-cunet / realesrgan-x4plus …)。
    /// 存预设与存设置都写它,读取时优先按名定位 —— 见 UpscaleSettings.W2xModelName 的说明。</summary>
    private string SelectedModelName()
    {
        int i = ModelCombo.SelectedIndex;
        if (!IsAnimeMode)
            return i >= 0 && i < EngineService.PhotoModels.Length ? EngineService.PhotoModels[i].Name : "";
        return i >= 0 && i < EngineService.AnimeModels.Length ? EngineService.AnimeModels[i].Model : "";
    }

    /// <summary>按【模型名】在当前模式的模型表里找下标;-1 = 没找到(调用方退回存的下标)。
    /// photo = 当前是否照片模式(Real-ESRGAN),决定查哪张表。</summary>
    private static int FindModelIndexByName(string name, bool photo)
    {
        if (string.IsNullOrEmpty(name)) return -1;
        if (photo)
        {
            for (int i = 0; i < EngineService.PhotoModels.Length; i++)
                if (EngineService.PhotoModels[i].Name == name) return i;
        }
        else
        {
            for (int i = 0; i < EngineService.AnimeModels.Length; i++)
                if (EngineService.AnimeModels[i].Model == name) return i;
        }
        return -1;
    }

    /// <summary>把当前页面参数收集为一个快照(供「保存预设」复用;不含输出目录等位置偏好)。</summary>
    private UpscaleSettings CollectSettings() => new()
    {
        Mode = ModeToStored(ModeRadios.SelectedIndex),
        W2xModel = ModelCombo.SelectedIndex,
        W2xModelName = SelectedModelName(),
        Scale = ScaleRadios.SelectedIndex,
        Noise = NoiseCombo.SelectedIndex,
        Tta = TtaCheck.IsChecked == true,
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
            ? (ImgQualityCombo.SelectedIndex is >= 0 and <= 4 ? ImgQualityCombo.SelectedIndex : _lastJpgQuality)
            : _lastJpgQuality,
        ImgQualityCustom = ParseImgQualityCustom(),
        PreDenoise = PreDenoiseCheck.IsChecked == true,
        DenoiseLevel = DenoiseLevelCombo.SelectedIndex,
    };

    // ---------- 参数预设(图片) ----------
    /// <summary>一个图片参数预设:命名 + 保存时间 + 一套 UpscaleSettings 快照。上限 100 个。</summary>
    private sealed class UpscalePreset
    {
        public string Name { get; set; } = "";
        public string SavedAt { get; set; } = "";
        public bool IsOfficial { get; set; }   // 官方预设(程序内置):悬停显示"官方"、无删除日期;用户预设=普通条目
        /// <summary>官方预设【参数基线版本】(与视频页 VideoPreset.OfficialRev 同一机制)。
        /// 用途:内置预设改了默认参数后,要在【下一次启动时把老的官方预设覆盖成新基线】——
        /// 只在 OfficialRev &lt; 当前基线 时才覆盖一次,之后用户自己的改动会被尊重,直到下次基线提升。
        /// 约定:Rev = 0 表示"早期版本写入的老预设"(那时还没有 OfficialRev 字段)。</summary>
        public int OfficialRev { get; set; }
        public UpscaleSettings Params { get; set; } = new();
    }

    /// <summary>图片预设文件(file=img-presets.json);导出/导入用 .alhimg 后缀(与视频页 .alhpreset 区分)。</summary>
    private static string ImgPresetFile => ParaPaths.SettingsFile("img-presets.json");
    private const int MaxImgPresets = 100;
    private const string ImgPresetExt = ".alhimg";

    /// <summary>图片预设文件"存在但读不出来"时的只读保护(与视频页同一套,理由见 VideoView.ProtectCorruptPresetFile):
    /// 原来解析失败就返回空列表,紧接着内置预设检查会把内置写回去 → 用户自建预设被无声覆盖掉。
    /// 现在:备份成 .bak + 本次运行拒绝写该文件。</summary>
    private static bool _imgPresetFileUnreadable;

    private static void ProtectCorruptImgPresetFile(string why)
    {
        if (_imgPresetFileUnreadable) return;
        _imgPresetFileUnreadable = true;
        try
        {
            var bak = ImgPresetFile + ".bak";
            File.Copy(ImgPresetFile, bak, true);
            AppLogger.Warn($"⚠ 图片预设文件读不出来({why})——已备份为 {Path.GetFileName(bak)},本次运行不再写入该文件(避免覆盖你原有预设)");
        }
        catch (Exception ex) { AppLogger.Warn($"⚠ 图片预设文件读不出来({why}),且备份失败:{ex.Message.Split('\n')[0]}"); }
    }

    private static List<UpscalePreset> LoadImgPresets()
    {
        try
        {
            if (!File.Exists(ImgPresetFile)) return new();
            var text = File.ReadAllText(ImgPresetFile);
            if (string.IsNullOrWhiteSpace(text)) return new();   // 空文件=没有预设(不是损坏)
            var list = System.Text.Json.JsonSerializer.Deserialize<List<UpscalePreset>>(text);
            if (list == null) { ProtectCorruptImgPresetFile("内容为 null"); return new(); }
            return list;
        }
        catch (Exception ex) { ProtectCorruptImgPresetFile(ex.Message.Split('\n')[0]); return new(); }
    }

    private static void SaveImgPresets(List<UpscalePreset> list)
    {
        try
        {
            if (_imgPresetFileUnreadable)
            {
                AppLogger.Warn("图片预设文件本次运行处于只读保护(先前读不出来),已跳过这次写入");
                return;
            }
            if (list.Count == 0) { if (File.Exists(ImgPresetFile)) File.Delete(ImgPresetFile); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(ImgPresetFile)!);
            File.WriteAllText(ImgPresetFile, System.Text.Json.JsonSerializer.Serialize(list));
        }
        catch { }
    }

    /// <summary>官方内置图片预设定义(名字 + 参数基线版本 Rev + 一套默认参数)。
    /// 以后要加官方预设,在这里加一项即可,下次启动自动带上;想更新某项的默认参数,把【那一项】的 Rev 加 1
    /// (只加那一项,否则会连带覆盖用户对其它官方预设的自定义)。
    /// Scale 一律写【页面当前的 4 项单选索引】语义(0=1x超分,1=2x,2=3x,3=4x),与 ApplyImgSettings 的读法一致。
    /// 语义版本约定(Rev=0):早期版本写入的官方预设按"旧版五项"语义写 Scale(1=1x超分、3=3x),
    /// 与现在的读法【差一位】。Rev=1 是第一个携带正确倍率语义的基线,由 EnsureBuiltinImgPresets 覆盖纠正。
    /// Rev=2(「清晰MAX」):倍率 3x → 4x(realesrgan-x4plus 权重原生就是 4x)、输出码率档 默认 → 超高。
    /// Rev=2(「通用变清晰」):倍率 1x超分 → **2x**。理由:做 1x 时用户拿它跟「清晰MAX(4x)」比会觉得
    ///   "糊/像没变化"——它压根没放大;而 2x 又正是 waifu2x 模型的原生倍率(直出、不必缩回),画质与速度都最优。
    /// Rev 是【每个预设各自记】的(OfficialRev 存在预设里),两项都升到 2 互不影响;用户自建的预设一律不碰。
    /// Rev=3(两项都升):「减少杂色」的强度语义变了 —— 从"1~50 跑一遍中值 / 51~100 再跑一遍"的开关式映射
    ///   改成"0~100 线性控制中值力度"(100 = 旧 51+ 档)。旧基线里的 Denoise=20(通用变清晰)/35(清晰MAX)
    ///   在新映射下各只有 0.20 / 0.35 的力度,等于把降噪悄悄关掉了,所以按实测等效值改成 50。
    /// Rev=4(两项都升):**取值按实测收敛**(见 _qa\imgpost\imgpost_presets.txt)。旧基线点一下就把"强边缘
    ///   过冲像素"从基底的 0.83% 推到 22.9%/26.2%(压缩素材)、31.4%/33.6%(干净素材),PSNR 掉 4~7dB ——
    ///   用户反馈的"白边"主要来自这里。原因有二:①锐化族 6 档本质是同一个算子,旧基线一口气开了 5 档
    ///   (锐化/清晰/钝化蒙版/保留细节/细节增强),改动方向两两相关 0.83~0.98 = 同一个旋钮拧五遍;
    ///   ②「去模糊」「边缘增强」在"先超分再做后处理"的流程里基本纯亏(前者没有模糊可回收、后者单边过冲)。
    ///   新基线只留 **细节增强(实测同等清晰度下代价最小)+ 钝化蒙版(唯一带阈值保护的)**,再配边缘抗锯齿收尾;
    ///   实测过冲像素降到 0.42%~4.3%(通用变清晰)/ 0.34%~7.6%(清晰MAX),细节仍比基底高 1.6~1.9 倍。</summary>
    private static (string Name, int Rev, Func<UpscaleSettings> Make)[] BuiltinImgPresets() => new[]
    {
        ( "通用变清晰", 4, new Func<UpscaleSettings>(() => new UpscaleSettings
        {
            Remember = true, Mode = 0, W2xModel = 0, W2xModelName = "models-cunet", Scale = 1, Noise = 2, Tta = false, SelectedOnly = false,
            Fmt = 0, Detail = 0, Sharpen = 0, Clarity = 0, Deblur = 0, Usm = 20, Edge = 0, DetailEnhance = 50,
            Denoise = 50, Aa = 30, Dehaze = 0, ImgQualityMode = 2, ImgQualityCustom = 92, ImgQuality = 92,
            PreDenoise = true, DenoiseLevel = 0, OutDir = "",
        })),
        // 【Rev=2】倍率 3x → 4x(realesrgan-x4plus 权重原生就是 4x,4x 直出不再需要级联推演);
        // 输出码率档 默认(92) → 超高(98)。提 Rev 的【唯一目的】就是让老用户手里那个旧基线(Rev≤1)的
        // 「清晰MAX」在下次启动时被覆盖成这个新基线 —— 不改 Rev 的话上面那段 OfficialRev 判断不会触发,
        // 老用户永远停在 3x + 92,而列表里明明写着官方预设。
        ( "清晰MAX", 4, new Func<UpscaleSettings>(() => new UpscaleSettings
        {
            Remember = true, Mode = 1, W2xModel = 2, W2xModelName = "realesrgan-x4plus", Scale = 3, Noise = 3, Tta = false, SelectedOnly = false,
            Fmt = 0, Detail = 0, Sharpen = 0, Clarity = 0, Deblur = 0, Usm = 30, Edge = 0, DetailEnhance = 60,
            Denoise = 50, Aa = 50, Dehaze = 0, ImgQualityMode = 3, ImgQualityCustom = 92, ImgQuality = 98,
            PreDenoise = true, DenoiseLevel = 2, OutDir = "",
        })),
    };

    /// <summary>确保每个官方内置图片预设存在,并把【参数基线过旧】的官方预设更新到新基线。
    /// 规则(与视频页 EnsureBuiltinPresets 完全一致,同一套 Rev 机制):
    ///   · 缺失 → 用官方默认创建(标记官方 + 记下当前 Rev)。
    ///   · 同名但本来不是官方 → 只标记为官方,【不动参数】(用户自己攒的同名预设保留原样)。
    ///   · 同名、是官方、且 OfficialRev &lt; 当前 Rev → 用新基线【覆盖参数】并记下新 Rev(只覆盖这一次)。
    ///     这条是刻意为之:老用户手里的官方预设必须被更新,否则永远带着旧语义的档位。
    ///   · 用户自建的其它预设(名字不同)一律不碰、不删、不改参数。
    /// 【为什么从"字符串判断"改成"Rev 判断"——实测 BUG】此前这里靠 SavedAt 里是否含「内置」来决定要不要
    /// 做倍率换算。但早期版本写入的官方预设 SavedAt 是 "2026-09-04 22:12:03" 这种【不带「内置」】的格式,
    /// 判断直接落空 → 整段迁移被跳过(而且两条 IsOfficial 本就是 true,changed 恒为 false,连文件都没重写)。
    /// 真机后果:老用户升级后「通用变清晰」静默从 1x超分 变成 2x、「清晰MAX」从 3x 变成 4x ——
    /// 真机实测该用户的 img-presets.json 就是 Scale=1 / Scale=3,与官方定义 0 / 2 不一致,而用户没改过任何设置。
    /// 现在改成按 Rev 基线覆盖,不再依赖任何字符串约定。</summary>
    private void EnsureBuiltinImgPresets()
    {
        try
        {
            var list = LoadImgPresets();
            bool changed = false;
            int updated = 0;
            foreach (var (name, rev, make) in BuiltinImgPresets())
            {
                var existing = list.FirstOrDefault(x => x.Name == name);
                if (existing == null)
                {
                    // 缺失 → 用官方默认创建,标记官方 + 记下当前基线,排在已有预设之前(官方靠前)
                    list.Insert(0, new UpscalePreset
                    {
                        Name = name,
                        SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm") + " · 内置",
                        IsOfficial = true,
                        OfficialRev = rev,
                        Params = make(),
                    });
                    changed = true;
                    AppLogger.Info($"[内置预设] 已创建官方图片预设「{name}」(基线 Rev {rev})");
                    continue;
                }
                // 同名但本来不是官方 → 只标记为官方,不动参数(用户自己攒的同名预设保留原样)
                // 【必须 continue】原实现只置 IsOfficial 就往下走,而紧接着的 `OfficialRev < rev` 对用户自建
                // 预设必然成立(其 OfficialRev = 0)→ **用户攒的参数被官方基线整份覆盖**,还先被写进了官方模型名;
                // 日志却宣称"你自建的预设未动"。2026-09-12 自检发现(与视频页同一个 bug)。
                // 现在:标记为官方 + 把 Rev 补到当前值(表示已按当前基线结算过),然后 continue,参数一个都不动。
                if (!existing.IsOfficial)
                {
                    existing.IsOfficial = true;
                    existing.OfficialRev = rev;
                    changed = true;
                    AppLogger.Info($"[内置预设] 「{name}」是官方图片预设名,但这条是你自建的:已标记为官方," +
                        "参数保持你自己那套(不覆盖、不重置)");
                    continue;
                }
                // 【只补字段、不提 Rev】老文件里没有"模型名"这个字段(W2xModelName 是后加的)。
                // 官方预设按定义补上即可 —— 注意【不能靠提 Rev 来触发】:提 Rev 会连带把用户改过的
                // 其它参数一并覆盖,为补一个内部字段付这个代价不值得。用户自建预设不动(留索引回退)。
                var def = make();
                if (string.IsNullOrEmpty(existing.Params.W2xModelName) && !string.IsNullOrEmpty(def.W2xModelName))
                {
                    existing.Params.W2xModelName = def.W2xModelName;
                    changed = true;
                }
                // 基线过旧 → 用新基线覆盖参数(只覆盖这一次;之后用户自己的改动会被尊重,直到下次提升 Rev)
                if (existing.OfficialRev < rev)
                {
                    existing.Params = make();
                    existing.OfficialRev = rev;
                    changed = true;
                    updated++;
                    AppLogger.Info($"[内置预设] 已把官方图片预设「{name}」更新到新基线(Rev {rev}):"
                        + "该预设参数已按新版重置;其余官方预设与你自建的预设未动");
                }
            }
            if (changed)
            {
                SaveImgPresets(list);
                AppLogger.Info($"[内置预设] 官方图片预设检查完成(缺失已补 / 同名已标记官方 / 基线过旧已更新 {updated} 项;用户自建预设未动)");
            }
        }
        catch { }
    }

    /// <summary>把一套图片参数快照套回当前页面(抑制事件回调写盘)。</summary>
    private void ApplyImgSettings(UpscaleSettings d)
    {
        _suppressEvents = true;
        try
        {
            if (d.Mode is >= 0 and <= 1) ModeRadios.SelectedIndex = ModeFromStored(d.Mode);
            // 模型:【优先按模型名定位】,名字找不到(老文件没这个字段 / 模型已下架)才退回存的下标。
            // 只存下标的话,模型表增删一项就会让所有老预设/老设置静默指到别的模型上。
            int mi = FindModelIndexByName(d.W2xModelName, d.Mode == 1);
            if (mi < 0) mi = d.W2xModel;
            if (mi >= 0 && mi < ModelCombo.Items.Count) ModelCombo.SelectedIndex = mi;
            // 与 LoadSettings 同一读法:同一字段只允许一套语义。两处不一致会让同一个预设/设置值
            // 在"应用预设"与"重启恢复"下得到不同倍率(用户看到"预设每次套出来都不一样")
            if (d.Scale is >= 0 and <= 4) ScaleRadios.SelectedIndex = Math.Clamp(d.Scale, 0, 3);
            if (d.Noise is >= 0 and <= 3) NoiseCombo.SelectedIndex = d.Noise;
            TtaCheck.IsChecked = d.Tta;
            if (d.Fmt is >= 0 and <= 1) FmtCombo.SelectedIndex = d.Fmt;
            RefreshQualityCombo();
            if (FmtCombo.SelectedIndex == 0)
            {
                if (d.ImgQualityMode is >= 0 and <= 4) ImgQualityCombo.SelectedIndex = d.ImgQualityMode;
                else ImgQualityCombo.SelectedIndex = 2;
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
            PreDenoiseCheck.IsChecked = d.PreDenoise;
            if (d.DenoiseLevel is >= 0 and <= 2) DenoiseLevelCombo.SelectedIndex = d.DenoiseLevel;
        }
        finally { _suppressEvents = false; }
    }

    /// <summary>把当前图片参数保存为一个预设(命名对话框;上限 100)。</summary>
    private async void SavePresetBtn_Click(object sender, RoutedEventArgs e)
    {
        var list = LoadImgPresets();
        if (list.Count >= MaxImgPresets)
        {
            await ShowPresetHintAsync($"已达上限 {MaxImgPresets} 个预设,请先删除部分预设再新建。");
            return;
        }
        string defaultName = "预设 " + (list.Count + 1);
        var box = new TextBox { Text = defaultName };
        box.SelectAll();
        var dlgContent = new StackPanel { Spacing = 8 };
        dlgContent.Children.Add(new TextBlock { Text = "给这个图片预设起个名字:", FontSize = 12 });
        dlgContent.Children.Add(box);
        var dlg = new ContentDialog
        {
            Title = "保存为预设",
            Content = dlgContent,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        string name = string.IsNullOrWhiteSpace(box.Text) ? defaultName : box.Text.Trim();
        list.Add(new UpscalePreset { Name = name, SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Params = CollectSettings() });
        SaveImgPresets(list);
        await ShowPresetHintAsync($"已保存预设「{name}」。点「使用预设」可查看、应用、删除。");
    }

    /// <summary>打开图片预设窗口:列表选择、应用、删除、排序、导出/导入(.alhimg)。</summary>
    private async void OpenPresetWindowBtn_Click(object sender, RoutedEventArgs e)
    {
        var list = LoadImgPresets();
        if (list.Count == 0) { await ShowPresetHintAsync("还没有任何图片预设。先点「保存预设」保存一个。"); return; }
        string? pendingDel = null;
        bool exportMode = false;   // 导出多选模式:true=行显示复选框(勾选导出),false=垃圾桶
        var exportChecks = new System.Collections.Generic.List<(UpscalePreset p, Microsoft.UI.Xaml.Controls.CheckBox cb)>();
        var sortCombo = new ComboBox { HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch };
        sortCombo.Items.Add("按创建时间(默认)"); sortCombo.Items.Add("按名字 A→Z"); sortCombo.Items.Add("按最近修改");
        sortCombo.SelectedIndex = 0;
        var exportBtn = new Button { Content = "导出", FontSize = 12, Padding = new Microsoft.UI.Xaml.Thickness(10, 4, 10, 4) };
        var importBtn = new Button { Content = "导入", FontSize = 12, Padding = new Microsoft.UI.Xaml.Thickness(10, 4, 10, 4) };
        var topBar = new Grid { ColumnSpacing = 8 };
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = Microsoft.UI.Xaml.GridLength.Auto });
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(1, Microsoft.UI.Xaml.GridUnitType.Star) });
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = Microsoft.UI.Xaml.GridLength.Auto });
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = Microsoft.UI.Xaml.GridLength.Auto });
        var sortLabel = new TextBlock { Text = "排序:", VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center };
        Grid.SetColumn(sortLabel, 0); Grid.SetColumn(sortCombo, 1); Grid.SetColumn(exportBtn, 2); Grid.SetColumn(importBtn, 3);
        topBar.Children.Add(sortLabel); topBar.Children.Add(sortCombo); topBar.Children.Add(exportBtn); topBar.Children.Add(importBtn);
        var listView = new ListView { SelectionMode = Microsoft.UI.Xaml.Controls.ListViewSelectionMode.Single, MaxHeight = 360 };
        var closeBtn = new Button { Content = "关闭", HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch };
        var applyBtn = new Button { Content = "应用预设", HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch, Style = (Microsoft.UI.Xaml.Style)App.Current.Resources["AccentButtonStyle"] };
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
        inner.Children.Add(topBar); inner.Children.Add(listView); inner.Children.Add(bottomBar);
        ContentDialog dlg = new() { Title = "图片预设", Content = inner, CloseButtonText = "", XamlRoot = this.XamlRoot };

        // 【与视频页预设一致】每行套一层 item 内边距,非末行加分隔线 —— 视觉与视频页预设列表统一
        void AddRow(Microsoft.UI.Xaml.Controls.Grid r, int idx, int count)
        {
            var itemPanel = new StackPanel { Padding = new Microsoft.UI.Xaml.Thickness(12, 8, 4, 8) };
            itemPanel.Children.Add(r);
            if (idx < count - 1)
                itemPanel.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle { Height = 1, Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 40, 44, 52)), Margin = new Microsoft.UI.Xaml.Thickness(0, 6, 0, 0) });
            listView.Items.Add(itemPanel);
        }

        void RebuildList()
        {
            var cur = LoadImgPresets();
            exportChecks.Clear();   // 每次重建都清空勾选记录(会随行重建重新填充)
            switch (sortCombo.SelectedIndex)
            {
                case 1: cur = cur.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList(); break;
                case 2: cur = cur.OrderByDescending(x => x.SavedAt, StringComparer.OrdinalIgnoreCase).ToList(); break;
            }
            listView.Items.Clear();
            for (int i = 0; i < cur.Count; i++)
            {
                var presetName = cur[i].Name;
                // 【一行摘要 + 悬停完整详情】此前列表只有"名字 + 时间",用户看不出预设里到底是什么
                // (要问维护者才知道)。悬停形式与视频页预设的摘要一致。
                // 【与视频页预设一致】列表行只显示「名字 + [官方]」单行,参数详情放在悬停提示里。
                // (此前把参数摘要拼成了第二行,于是图片预设列表比视频页"多一块内容"、视觉不统一。)
                var name = new TextBlock
                {
                    Text = cur[i].Name + (cur[i].IsOfficial ? "  [官方]" : ""),
                    FontSize = 14,
                    VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
                };
                ToolTipService.SetToolTip(name, new TextBlock
                {
                    Text = BuildImgPresetSummary(cur[i]),
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                    MaxWidth = 320,
                });
                if (exportMode)
                {
                    // 导出多选模式:右侧显示【复选框】(勾选要导出的预设),替代垃圾桶。默认全选。
                    var cb = new Microsoft.UI.Xaml.Controls.CheckBox { VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center, IsChecked = false };
                    exportChecks.Add((cur[i], cb));
                    var rowEx = new Grid(); rowEx.ColumnDefinitions.Add(new ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(1, Microsoft.UI.Xaml.GridUnitType.Star) }); rowEx.ColumnDefinitions.Add(new ColumnDefinition { Width = Microsoft.UI.Xaml.GridLength.Auto });
                    rowEx.Children.Add(name); Grid.SetColumn(name, 0); rowEx.Children.Add(cb); Grid.SetColumn(cb, 1); AddRow(rowEx, i, cur.Count);
                    continue;
                }
                var delBtn = new Button { Content = "\uE74D", FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe MDL2 Assets"), FontSize = 13, Background = null, BorderThickness = new Microsoft.UI.Xaml.Thickness(0), Padding = new Microsoft.UI.Xaml.Thickness(6, 2, 6, 2), VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center, MinWidth = 0, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 229, 72, 77)) };
                ToolTipService.SetToolTip(delBtn, "删除该预设");
                if (pendingDel == presetName)
                {
                    name.Text = "确定要删除此预设吗?"; name.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 229, 72, 77));
                    var span = new StackPanel { Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal, Spacing = 6, VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center };
                    var ok = new Button { Content = "✓", FontSize = 14, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 168, 0)), MinWidth = 0, Padding = new Microsoft.UI.Xaml.Thickness(6, 2, 6, 2), Background = null, BorderThickness = new Microsoft.UI.Xaml.Thickness(0) };
                    var cancel = new Button { Content = "✕", FontSize = 14, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 229, 72, 77)), MinWidth = 0, Padding = new Microsoft.UI.Xaml.Thickness(6, 2, 6, 2), Background = null, BorderThickness = new Microsoft.UI.Xaml.Thickness(0) };
                    ok.Click += (_, _) => { var latest = LoadImgPresets(); var hit = latest.FirstOrDefault(x => x.Name == presetName); if (hit != null) { latest.Remove(hit); SaveImgPresets(latest); } pendingDel = null; if (latest.Count == 0) { try { dlg.Hide(); } catch { } return; } RebuildList(); };
                    cancel.Click += (_, _) => { pendingDel = null; RebuildList(); };
                    span.Children.Add(ok); span.Children.Add(cancel);
                    var rowG = new Grid(); rowG.ColumnDefinitions.Add(new ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(1, Microsoft.UI.Xaml.GridUnitType.Star) }); rowG.ColumnDefinitions.Add(new ColumnDefinition { Width = Microsoft.UI.Xaml.GridLength.Auto });
                    rowG.Children.Add(name); Grid.SetColumn(name, 0); rowG.Children.Add(span); Grid.SetColumn(span, 1); AddRow(rowG, i, cur.Count); continue;
                }
                delBtn.Click += (_, _) => { pendingDel = presetName; RebuildList(); };
                var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(1, Microsoft.UI.Xaml.GridUnitType.Star) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = Microsoft.UI.Xaml.GridLength.Auto });
                row.Children.Add(name); Grid.SetColumn(name, 0); row.Children.Add(delBtn); Grid.SetColumn(delBtn, 1); AddRow(row, i, cur.Count);
            }
        }
        RebuildList();
        sortCombo.SelectionChanged += (_, _) => RebuildList();
        // 导出指定预设为 .alhimg 文件(JSON 数组)。文件名带预设名(单个=该预设名;多个=第一个+"等N个")
        async Task ExportPresetsAsync(List<UpscalePreset> toExport)
        {
            if (toExport.Count == 0) return;
            var picker = new FileSavePicker();
            picker.FileTypeChoices.Add("ALH Pro 图片预设", new List<string> { ImgPresetExt });
            picker.SuggestedFileName = toExport.Count == 1
                ? SafePresetFileName(toExport[0].Name)
                : SafePresetFileName(toExport[0].Name) + $"等{toExport.Count}个";
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var file = await picker.PickSaveFileAsync();
            if (file == null) return;
            try { await System.IO.File.WriteAllTextAsync(file.Path, System.Text.Json.JsonSerializer.Serialize(toExport)); AppLogger.UserAction($"图片:导出 {toExport.Count} 个预设到 {file.Path}"); }
            catch (Exception ex) { AppLogger.Warn("导出图片预设失败:" + ex.Message); }
        }

        // 「应用预设」(右蓝):普通态=应用选中的预设后正常关窗;导出态=「确定导出」收集勾选导出后退回普通态
        applyBtn.Click += async (_, _) =>
        {
            if (exportMode)
            {
                var selected = exportChecks.Where(x => x.cb.IsChecked == true).Select(x => x.p).ToList();
                if (selected.Count == 0) { await ShowPresetHintAsync("还没勾选任何预设。先勾选要导出的预设,再点「确定导出」。"); return; }
                await ExportPresetsAsync(selected);
                exportMode = false; pendingDel = null; SetBottomMode(); RebuildList();
                return;
            }
            int s = listView.SelectedIndex; if (s < 0) return;
            var cur = LoadImgPresets(); var byName = sortCombo.SelectedIndex == 1 ? cur.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList()
                : sortCombo.SelectedIndex == 2 ? cur.OrderByDescending(x => x.SavedAt, StringComparer.OrdinalIgnoreCase).ToList() : cur;
            if (s < byName.Count) { ApplyImgSettings(byName[s].Params); SaveSettings(); }
            try { dlg.Hide(); } catch { }
        };
        // 「关闭」(左灰):普通态=直接关窗;导出态=取消导出,退回普通态
        closeBtn.Click += (_, _) =>
        {
            if (exportMode) { exportMode = false; pendingDel = null; SetBottomMode(); RebuildList(); return; }
            try { dlg.Hide(); } catch { }
        };
        // 导出:进入多选模式(行显示复选框),底部按钮切「取消/确定导出」
        exportBtn.Click += (_, _) =>
        {
            exportMode = true; pendingDel = null; SetBottomMode(); RebuildList();
        };
        importBtn.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".json"); picker.FileTypeFilter.Add(ImgPresetExt);
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;
            // 【格式校验】图片预设必须是 .alhimg(或内容含图片专属字段)。若用户误选了视频预设(.alhpreset),
            // 公开字段(如 Scale)会错读成图片参数且不报错——必须显式校验并明确提示,绝不静默混入。
            if (!file.FileType.Equals(ImgPresetExt, StringComparison.OrdinalIgnoreCase))
            {
                await ShowPresetHintAsync("文件不是图片预设格式。请导入「ALH Pro 图片预设」(.alhimg)文件;视频预设(.alhpreset)请在视频页导入。");
                return;
            }
            try
            {
                var json = await System.IO.File.ReadAllTextAsync(file.Path);
                // 再校验内容确实含图片专属字段(W2xModel/PreDenoise 等),防"后缀对但内容是视频预设"的伪装
                bool isImage = json.Contains("\"W2xModel\"", StringComparison.Ordinal)
                    || json.Contains("\"PreDenoise\"", StringComparison.Ordinal);
                if (!isImage)
                {
                    await ShowPresetHintAsync("文件内容不是图片预设(可能是视频预设或已损坏)。请导入图片预设(.alhimg)文件。");
                    return;
                }
                var imported = System.Text.Json.JsonSerializer.Deserialize<List<UpscalePreset>>(json) ?? new();
                var existing = LoadImgPresets(); int added = 0;
                foreach (var p in imported)
                {
                    if (existing.Count >= MaxImgPresets) break;
                    var nm = p.Name; int k = 1;
                    while (existing.Any(x => x.Name == nm)) nm = $"{p.Name} ({++k})";
                    p.Name = nm; existing.Add(p); added++;
                }
                SaveImgPresets(existing); RebuildList(); await ShowPresetHintAsync($"已导入 {added} 个图片预设。");
            }
            catch (Exception ex) { AppLogger.Warn("导入图片预设失败(格式不对):" + ex.Message); }
        };
        await dlg.ShowAsync();
    }

    /// <summary>预设名 → 安全文件名:去掉 Windows 文件名非法字符(\/:*?"<>|)及首尾空格,空则给默认名。</summary>
    internal static string SafePresetFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "预设";
        var bad = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder();
        foreach (var c in name.Trim()) sb.Append(Array.IndexOf(bad, c) >= 0 ? '_' : c);
        return sb.Length == 0 ? "预设" : sb.ToString();
    }

    /// <summary>图片预设的【完整参数摘要】(悬停提示,多行)。与视频页 BuildPresetSummary 同一目的:
    /// 此前图片预设列表只有"名字 + 保存时间",用户根本看不出预设里到底是什么(得去问维护者才知道),
    /// 而"预设名与实际参数对不上"恰恰是最难自己发现的一类问题。
    /// 【模式感知】照片模式(Real-ESRGAN)下"降噪级别"控件是禁用的、引擎也不支持 —— 若照原样打印
    /// "降噪: 强"就是在骗用户(那个值不生效)。这里如实标注"不适用"并指出真正生效的是预处理降噪。</summary>
    private static string BuildImgPresetSummary(UpscalePreset p)
    {
        var d = p.Params;
        bool photo = d.Mode == 1;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(p.IsOfficial ? $"「{p.Name}」[官方]" : $"「{p.Name}」({p.SavedAt})");
        sb.AppendLine("模式: " + (photo ? "Real-ESRGAN(照片)" : "waifu2x(动漫)"));
        sb.AppendLine("模型: " + ModelLabelOf(d, photo));
        sb.AppendLine("倍率: " + (d.Scale switch
        {
            0 => "1x 超分(2x 放大后缩回,分辨率不变)",
            1 => "2x",
            2 => "3x",
            3 => "4x",
            _ => $"倍率索引 {d.Scale}",
        }));
        sb.AppendLine(photo
            ? "降噪级别: 不适用(Real-ESRGAN 不支持;本预设用「预处理降噪」代替)"
            : "降噪级别: " + (d.Noise switch { 0 => "不降噪", 1 => "弱", 2 => "中", 3 => "强", _ => "?" }));
        sb.AppendLine("预处理降噪: " + (d.PreDenoise
            ? "开 · " + (d.DenoiseLevel switch { 0 => "弱", 1 => "中", 2 => "强", _ => "?" })
            : "关"));
        sb.AppendLine("高级增强(TTA): " + (d.Tta ? "开(耗时约 2~3 倍)" : "关"));
        sb.AppendLine("增强: " + $"去雾{d.Dehaze} 减少杂色{d.Denoise} 锐化{d.Sharpen} 清晰{d.Clarity} 钝化蒙版{d.Usm} " +
            $"保留细节{d.Detail} 细节增强{d.DetailEnhance} 去模糊{d.Deblur} 边缘增强{d.Edge} 边缘抗锯齿{d.Aa}");
        sb.AppendLine("输出: " + (d.Fmt == 1
            ? "PNG(无损)"
            : "JPG · " + (d.ImgQualityMode switch
            {
                0 => "低(75)", 1 => "中(85)", 2 => "默认(92)", 3 => "超高(98)",
                4 => $"自定义({d.ImgQualityCustom})", _ => "?",
            })));
        return sb.ToString().TrimEnd();
    }

    /// <summary>预设里记的模型名(老文件没有该字段 → 用下标从表里取,取不到给 "?")。</summary>
    private static string ModelNameOf(UpscaleSettings d, bool photo)
    {
        if (!string.IsNullOrEmpty(d.W2xModelName)) return d.W2xModelName;
        if (photo) return d.W2xModel >= 0 && d.W2xModel < EngineService.PhotoModels.Length ? EngineService.PhotoModels[d.W2xModel].Name : "?";
        return d.W2xModel >= 0 && d.W2xModel < EngineService.AnimeModels.Length ? EngineService.AnimeModels[d.W2xModel].Model : "?";
    }

    /// <summary>预设里模型的显示名(带体量与快慢标注);优先按名定位,找不到才用下标。</summary>
    private static string ModelLabelOf(UpscaleSettings d, bool photo)
    {
        int i = FindModelIndexByName(d.W2xModelName, photo);
        if (i < 0) i = d.W2xModel;
        if (photo) return i >= 0 && i < EngineService.PhotoModels.Length ? EngineService.PhotoModels[i].Label : "?";
        return i >= 0 && i < EngineService.AnimeModels.Length ? EngineService.AnimeModels[i].Label : "?";
    }

    private async Task ShowPresetHintAsync(string msg)
    {
        var dlg = new ContentDialog { Title = "参数预设", Content = new TextBlock { Text = msg, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap }, CloseButtonText = "知道了", XamlRoot = this.XamlRoot };
        await dlg.ShowAsync();
    }

    private sealed class UpscaleSettings
    {
        public bool Remember { get; set; } = true;
        public int Mode { get; set; } = 0;
        public int W2xModel { get; set; } = 0;
        /// <summary>模型名(models-cunet / realesrgan-x4plus …)。
        /// 【为什么必须存名而不是只存下标】下标会随模型表增删而【静默错位】:以后往 AnimeModels /
        /// PhotoModels 里加一项,所有老预设与老设置就会指到别的模型上,而用户什么都没改、界面上也看不出来。
        /// 读取侧一律优先按名定位,名字找不到(老文件没有这个字段)才退回上面的下标 —— 向后兼容。</summary>
        public string W2xModelName { get; set; } = "";
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

    /// <summary>当前计算设备(全局设置):-1=CPU,≥0=GPU 编号。
    /// 【尊重用户选择】= 用户在下拉框选的引擎 -g 编号(选独显就独显、选核显就核显);仅当编号无效/设备表未枚举时
    /// 才用 ResolveEngineGpu 的推荐(通常独显)兜底,绝不强制纠正用户选择。</summary>
    private int CurrentGpuId
    {
        // 【H1 · 2026-09-13】这里原来自己写了一份"编号是否在设备表里"的解析(与另两页各一份、彼此重复),
        // 现已删掉:判定只保留唯一权威入口 EngineService.ResolveEngineGpu(它内部调已单测的
        // AlhPro.Core.DeviceRouting.ResolveEngineDevice —— 含"编号不在表→换表内设备""撞号到核显→换最佳独显")。
        // 重复解析正是"选独显却跑核显"这类问题反复出现的成因:三份各改一半就分叉。
        get => EngineService.ResolveEngineGpu(AppSettings.GpuIndex);
    }

    private void UpdateRunState()
    {
        RunBtn.IsEnabled = ToolGrid.Items.Count > 0 && !_running;
        // 耗时提示(黄色):启用耗时功能时,显示在"开始处理"下方(开什么显示什么)
        if (SpeedHint != null)
        {
            var slow = new System.Collections.Generic.List<string>();
            if (TtaCheck.IsChecked == true) slow.Add("高级 TTA");
            if (ScaleRadios.SelectedIndex >= 2) slow.Add($"高倍率({ScaleRadios.SelectedIndex switch { 2 => "3x", _ => "4x" }})");
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
        // 照片模式 Real-ESRGAN(2022 ncnn)在 Blackwell/Vulkan 不可用设备无法 GPU → 已自动 ONNX;动漫 waifu2x 官方新版稳定
        if (ToolGrid.Items.Count > 0 && !_running)
        {
            if (!IsAnimeMode && EngineService.ShouldUseOnnxEsrgan())
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
    /// 故 3x/4x 已放开;Real-ESRGAN 有对应权重,全亮。</summary>
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
        AppLogger.UserAction("图片:点击「暂停」");
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
        AppLogger.UserAction("图片:点击「恢复」");
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
        AppLogger.UserAction("图片:点击「重置」增强参数");
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

    /// <summary>图片页「重置所有参数」:恢复全部默认(算法/模型/倍率/降噪/TTA/格式/增强/预处理降噪)。</summary>
    private void ImgResetBtn_Click(object sender, RoutedEventArgs e)
    {
        AppLogger.UserAction("图片:点击「重置所有参数」");
        _suppressEvents = true;
        try
        {
            ModeRadios.SelectedIndex = 1;          // 动漫(waifu2x,界面第二项)
            ModelCombo.SelectedIndex = 0;
            ScaleRadios.SelectedIndex = 0;         // 1x超分
            NoiseCombo.SelectedIndex = 0;
            TtaCheck.IsChecked = false;
            FmtCombo.SelectedIndex = 0;            // JPG
            RefreshQualityCombo();
            ImgQualityCombo.SelectedIndex = 2;     // 默认(推荐)
            PreDenoiseCheck.IsChecked = false;
            DenoiseLevelCombo.SelectedIndex = 0;
            // 增强滑块
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
        finally { _suppressEvents = false; }
        SaveSettings();
        Log("已重置所有参数为默认值");
    }

    /// <summary>高级(TTA)折叠展开:默认收起,点按钮展开(与视频页「高级参数」交互一致)。</summary>
    private void HighQualityToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        bool show = HighQualityPanel.Visibility != Visibility.Visible;
        HighQualityPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        HighQualityToggleBtn.Content = show ? "高级 ▴" : "高级 ▾";
    }

    /// <summary>追加一行日志(带时间戳),自动滚动到底部,超限自动清理最旧部分。</summary>
    private void Log(string msg)
    {
        AppLogger.Info(msg);   // 同步写诊断日志文件
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        // 【包起来】同抠图/视频页:文本刷新 + ChangeView 会抛 COMException/LayoutCycleException,
        // 日志文件已落盘,界面刷不动不能把任务带崩。
        try
        {
            var text = TaskLogText.Text;
            TaskLogText.Text = text == "日志:等待任务..." ? line : text + "\n" + line;
            // 自动清理:超过 200 行,删除最旧的一半
            var lines = TaskLogText.Text.Split('\n');
            if (lines.Length > 200)
                TaskLogText.Text = string.Join("\n", lines.Skip(80)) + "\n";
            TaskLogScroll.ChangeView(null, TaskLogScroll.ScrollableHeight, null, true);
        }
        catch (Exception ex) { VideoView.NoteUiRefreshFailure("图片日志追加/自动滚动", ex); }
    }

    // ---------- 单击仅选中,不弹预览;双击打开大图预览 ----------
    private void ToolGrid_SelectionChanged(System.Collections.Generic.IReadOnlyList<ImageItem> items)
    {
        // 单击/框选只改变选中状态,预览由双击打开
        RefreshOutSpec();
    }

    /// <summary>左下角「输出规格」提示:未处理时显示当前选中项将输出的分辨率(随倍率/选中项实时更新)。
    /// 处理中/无选中时隐藏,避免与进度状态混在一起。</summary>
    private void RefreshOutSpec()
    {
        try
        {
            if (_running) { OutSpecText.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed; return; }
            // 无效倍率:1x 超分(2x放大后缩回)= 输出不变;2x/3x/4x 对应倍率
            bool shrink1x = ScaleRadios.SelectedIndex == 0;
            double mult = shrink1x ? 1.0 : EngineScaleOf(ScaleRadios.SelectedIndex);   // 与真正导出用同一份映射,防"提示与实际不符"
            // 取选中项的第一张(无选中则取列表第一张)
            ImageItem? it = null;
            if (ToolGrid.SelectedItems.Count > 0) it = ToolGrid.SelectedItems[0];
            else if (ToolGrid.Items.Count > 0) it = ToolGrid.Items[0];
            if (it == null || it.PixelWidth <= 0 || it.PixelHeight <= 0)
            {
                OutSpecText.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                return;
            }
            int ow = (int)Math.Round(it.PixelWidth * mult);
            int oh = (int)Math.Round(it.PixelHeight * mult);
            // 1x 超分:缩回原尺寸
            if (shrink1x) { ow = it.PixelWidth; oh = it.PixelHeight; }
            OutSpecText.Text = $"输出: {ow}×{oh}px(源 {it.PixelWidth}×{it.PixelHeight},{mult:0}x)";
            OutSpecText.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        }
        catch { OutSpecText.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed; }
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
    {
        AppLogger.UserAction("图片:关闭预览");
        PreviewOverlay.Visibility = Visibility.Collapsed;
    }

    // ---------- 选择 ----------
    public void PickImage() => PickBtn_Click(this, new RoutedEventArgs());
    public void Run() => RunBtn_Click(this, new RoutedEventArgs());

    private async void PickBtn_Click(object sender, RoutedEventArgs e)
    {
        AppLogger.UserAction("图片:点击「添加图片」");
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".webp");
        picker.FileTypeFilter.Add(".bmp");
        picker.FileTypeFilter.Add(".tif");
        picker.FileTypeFilter.Add(".tiff");
        picker.FileTypeFilter.Add(".heic");
        picker.FileTypeFilter.Add(".heif");
        picker.FileTypeFilter.Add(".avif");
        picker.FileTypeFilter.Add(".gif");   // 选第一帧(引擎不支持动图)
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var files = await picker.PickMultipleFilesAsync();
        if (files != null && files.Count > 0)
        {
            await ToolGrid.AddImagesAsync(files.Select(f => f.Path));
            Log($"添加了 {files.Count} 张图片到列表");
            RefreshOutSpec();
        }
    }

    private async void BrowseOut_Click(object sender, RoutedEventArgs e)
    {
        AppLogger.UserAction("图片:选择输出文件夹");
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
    /// <summary>「放大倍数」单选项索引 → 引擎实际放大倍数:0=1x超分(先 2x 再缩回)、1=2x、2=3x、3=4x。
    /// 主流程、输出规格提示与「放大选区」必须共用这一份映射:两处各写一份曾经分叉 —— 选区整体错一档,
    /// 选 2x 实际只放大 1x(引擎在 scale≤1 时直接复制文件),而选区样张正是用户判断超分效果的依据。</summary>
    private static int EngineScaleOf(int scaleRadioIndex)
        => scaleRadioIndex switch { 1 => 2, 2 => 3, 3 => 4, _ => 2 };   // 0(1x超分)也要真 2x 放大,再由 upscaleShrink1x 缩回

    /// <summary>界面上的"引擎能力"提示(黄色):当前引擎不支持用户已选的参数时必须显式说明,不许静默忽略。</summary>
    private void ShowEngineCapHint(string msg)
    {
        EngineCapHint.Text = msg;
        EngineCapHint.Visibility = Visibility.Visible;
    }

    /// <summary>ONNX 分支出图后的收尾(主流程与黑块重试共用),兑现两条"预览=结果"承诺:
    /// ①「1x 超分」= 输出与源同尺寸 —— ONNX 只按 scale=2 出图,不缩回就是"选 1x 得到 2x";
    /// ② 输出扩展名与真实编码一致 —— ONNX 所有出口都写 PNG,直接叫 .jpg 会出现"JPG 文件里装 PNG 字节"
    ///    (格式契约被破坏,按魔数识别的看图软件/上传接口会判为坏文件)。
    /// onnxInputPath 是喂给 ONNX 的那张图(源尺寸基准);缩回与 JPG 转码合并成一次,避免二次有损压缩。
    /// 【纯计算,不碰控件】大图要整张解码+重编码,必须由调用方放进 Task.Run——在 UI 线程跑会卡界面,
    /// 而这里若自己写日志就会从后台线程碰 XAML(0x8001010E),故只返回一行日志文本交给 UI 线程打印。</summary>
    private static string? FinalizeOnnxOutput(string onnxInputPath, string outPath, bool shrink1x, int imgQ)
    {
        int sw = 0, sh = 0;
        if (shrink1x)
        {
            using var src = new System.Drawing.Bitmap(onnxInputPath);   // 源尺寸 = 1x 超分承诺的输出尺寸
            sw = src.Width;
            sh = src.Height;
        }
        bool isJpg = outPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                  || outPath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);
        var tmpPng = Path.Combine(EngineService.TempRoot, $"imgup_onnx_out_{Guid.NewGuid():N}.png");
        try
        {
            if (shrink1x) SaveShrunkPng(outPath, tmpPng, sw, sh);
            if (isJpg)
            {
                if (!shrink1x) File.Copy(outPath, tmpPng, overwrite: true);   // 不能边读边写同一文件
                EngineService.ConvertPngToJpg(tmpPng, outPath, imgQ / 100f);   // 按用户码率档位转真实 JPG
            }
            else if (shrink1x)
            {
                File.Move(tmpPng, outPath, overwrite: true);
            }
        }
        finally
        {
            try { if (File.Exists(tmpPng)) File.Delete(tmpPng); } catch { /* 清理失败忽略 */ }
        }
        return shrink1x ? $"  1x 超分:输出已缩回源尺寸 {sw}×{sh}"
                        : isJpg ? "  已按输出格式重编码为真实 JPG" : null;
    }

    /// <summary>把 srcPath 高保真缩放到 w×h 写入 destPngPath(始终 PNG,无损)。
    /// 失败即抛,绝不写占位图 —— 输出目录里出现一张灰图比明确失败更难排查。</summary>
    private static void SaveShrunkPng(string srcPath, string destPngPath, int w, int h)
    {
        using var src = new System.Drawing.Bitmap(srcPath);
        using var dst = new System.Drawing.Bitmap(Math.Max(1, w), Math.Max(1, h));
        using (var g = System.Drawing.Graphics.FromImage(dst))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.DrawImage(src, 0, 0, dst.Width, dst.Height);
        }
        dst.Save(destPngPath, System.Drawing.Imaging.ImageFormat.Png);
    }

    // 区域放大:只对框选区域做 AI 放大,产出新图加入列表
    private async void RegionUpscaleAsync(ImageItem item, int x, int y, int w, int h)
    {
        if (_running) return;
        var isAnime = IsAnimeMode;
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
            // 照片模式:按 ModelCombo 选的 Real-ESRGAN 模型(0=animevideov3 1=x4plus-anime 2=x4plus)
            model = EngineService.PhotoModels[Math.Clamp(ModelCombo.SelectedIndex, 0, EngineService.PhotoModels.Length - 1)].Name;
        }
        var scale = EngineScaleOf(ScaleRadios.SelectedIndex);   // 与主流程同一映射(选区不做 1x 缩回,故文件名里的倍数就是真实倍数)
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
        string? tmpExif = null;
        try
        {
            Log($"→ 区域放大 {w}×{h} @({x},{y}) 倍数 {scale}x 引擎 {engine}/{model}");
            // 降温休息(每小时/温度墙):全软件覆盖,选区放大同样生效
            await SafeRender.RestIfDueAsync(0, null, CancellationToken.None);
            // 【裁剪坐标必须先做 EXIF 方向标准化】框选坐标来自预览显示空间(BitmapImage 走 WIC,默认应用
            // EXIF 方向),而 UpscaleRegionAsync 内部是 System.Drawing 解码 —— 它不应用 EXIF。
            // 手机竖拍图(方向 6/8/3)直接用显示坐标去裁像素,会拿"竖着框的矩形"切"横躺的像素":
            // 轻则裁到错误区域、重则被 Math.Clamp 夹到边缘。与抠图页同一做法(NormalizeExif 返回临时文件)。
            string regionSrc = await Task.Run(() => EngineService.NormalizeExif(item.Path));
            if (!ReferenceEquals(regionSrc, item.Path))
            {
                tmpExif = regionSrc;
                Log("  检测到 EXIF 旋转,已先旋转为标准方向再裁剪选区");
            }
            await EngineService.UpscaleRegionAsync(regionSrc, outPath,
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
            try { if (tmpExif != null) File.Delete(tmpExif); } catch { /* 清理失败忽略 */ }
            _running = false;
            ToolGrid.IsProcessing = false;
            PauseBtn.IsEnabled = false;
            ResumeBtn.IsEnabled = false;
        }
    }

    private async void RunBtn_Click(object sender, RoutedEventArgs e)
    {
        AppLogger.UserAction("图片:点击「开始处理」");
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
        var needEngine = IsAnimeMode
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
        EngineCapHint.Text = "";                              // 清掉上一轮的引擎能力提示(每轮重新判定)
        EngineCapHint.Visibility = Visibility.Collapsed;

        var isAnime = IsAnimeMode;
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
            // 照片模式:按 ModelCombo 选的 Real-ESRGAN 模型(0=animevideov3 1=x4plus-anime 2=x4plus)
            model = EngineService.PhotoModels[Math.Clamp(ModelCombo.SelectedIndex, 0, EngineService.PhotoModels.Length - 1)].Name;
        }
        bool upscaleShrink1x = ScaleRadios.SelectedIndex == 0;   // 1x 超分(2x 放大后缩回)
        var scale = EngineScaleOf(ScaleRadios.SelectedIndex);   // 与「放大选区」共用同一映射,避免两处再次分叉
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
                double fr = SafeRender.FreeRamGB;
                // 空闲显存照实写"未实测"(仅 NVIDIA 可测),不再伪造数值误导排查
                Log($"资源自检:空闲内存 {fr:0.#} GB / 空闲显存 {SafeRender.FreeVramText} → 分块 {SafeRender.GetTileSize()}");
                AppLogger.Info($"图片超分资源自检:空闲内存 {fr:0.#} GB / 空闲显存 {SafeRender.FreeVramText} → 分块 {SafeRender.GetTileSize()}");
            }
            // 输出码率显示:JPG=质量数值;PNG=无损原样(固定)
            var qualityDesc = outExt == ".jpg" ? $"输出质量={imgQ}" : "输出=无损(原样)";
            Log($"开始放大任务:共 {total} 张,引擎={engine}/{model},倍数={scale}x,格式={outExt.TrimStart('.')},设备={(gpuId >= 0 ? $"GPU {gpuId}" : "CPU (软件计算)")},{qualityDesc},版本:v{UpdateChecker.CurrentVersion}");
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
                // 【修复 半成品提前出现】超分/增强先写【临时路径】,整张真正完成后再原子改名到最终文件名。
                // 否则超分完成、增强进行中时,输出目录已出现未增强的版本(用户看到"还没处理完就生成了")。
                var finalOut = UniquePath(outDir, baseName + outExt);   // 最终文件名(防重名)
                var outPath = Path.Combine(EngineService.TempRoot, $"imgup_proc_{Guid.NewGuid():N}{outExt}");   // 处理用临时路径

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
                            tmpDenoise = Path.Combine(EngineService.TempRoot,
                                $"imgup_denoise_{Guid.NewGuid():N}.png");
                            _progressSegStart = 0.0;
                            _progressSegEnd = 0.4;
                            progress.Report((0, "预处理降噪..."));
                            Log("  预处理降噪(waifu2x 1x)...");
                            // 预处理降噪也走设备路由:高风险设备(ncnn 不可用)→ ONNX 稳定引擎;否则 ncnn(带降噪档)
                            bool preOnnx = gpuId < 0
                                || EngineService.ShouldUseOnnxWaifu2x()
                                || !await EngineService.IsWaifu2xNcnnUsableAsync(gpuId, ct);
                            if (preOnnx && EsrganOnnxService.FindWaifu2xModel() != null)
                                await AbandonOnCancelAsync(
                                    EsrganOnnxService.UpscaleAsync(srcPath, tmpDenoise, 1,
                                        gpuId < 0 ? -1 : -2, progress, CancellationToken.None, EsrganOnnxService.FindWaifu2xModel()), ct);
                            else
                                await EngineService.UpscaleAsync(srcPath, tmpDenoise, "waifu2x",
                                    "models-cunet", 1, denoiseLevel, gpuId, false, progress, ct);
                            srcPath = tmpDenoise;
                        }

                        _progressSegStart = preDenoise ? 0.4 : 0.0;
                        _progressSegEnd = 0.97;   // 给最后"画质增强"留 3%(否则超分就满 100%)
                        progress.Report((0, $"正在处理 {item.Name}..."));
                        // 智能自检选择:【先真机探测、再按结果决定】,与视频路径共用同一份结论缓存(key = realesrgan|gpuId)。
                        // 【为什么必须在这里探测 —— 实测漏洞】此前这里只【读】结论,而图片路径上没有任何地方【写】结论,
                        // 于是 TryGetNcnnVerdict 永远返回 null → 回退旧启发式(Blackwell 一律算风险)→ 50 系照片模式
                        // 被硬编码走 ONNX。真机证据(诊断包 20260910_2352,RTX 5060 Laptop):该机 Vulkan 枚举正常、
                        // 引擎枚举正常、DirectML 建会话正常 —— 拦它的纯粹是"按型号猜"那条规则,ONNX 超分实测 ~2 秒/帧。
                        // 探测结论会落盘(ncnn-probe),同一设备 7 天内不再重复试跑。
                        // 【必须限定 engine == "realesrgan" —— 这里踩过坑】探测无条件执行过一次的后果:
                        // waifu2x 模式下 model 是 "models-cunet" 这类【waifu2x 的模型名】,而 realesrgan 引擎
                        // 拿到的是 `-m models -n {model}`(EngineService.cs:1264)→ 找不到该模型 → 探测必然失败 →
                        // 把"realesrgan 不可用"的【假结论】写进落盘缓存(ncnn-probe,TTL 7 天)→ 照片模式被错误地
                        // 永久推到 ONNX。而且这个探测在 waifu2x 路径上本来是毫无意义的。
                        bool esrganNcnnOk = true;
                        if (engine == "realesrgan")
                            esrganNcnnOk = await EngineService
                                .EnsureNcnnProbeAsync("realesrgan", gpuId, model, ct).ConfigureAwait(false);
                        string? onnxPath = null;
                        // 手动选 CPU(-1)时:waifu2x/realesrgan 的 ncnn CPU 模式在部分机器崩(实测 exit -1/-1073741819)→ 直接 ONNX(CPU 同样稳定,画质一致)
                        // 没有 ONNX 模型时不能把用户堵死:只能走 ncnn(哪怕探测说它不稳,也比什么都不做强)
                        if (engine == "realesrgan" && !esrganNcnnOk
                            && EsrganOnnxService.FindModel() != null)
                            onnxPath = EsrganOnnxService.ResolveEsrganOnnxPath(model);
                        else if (engine == "waifu2x"
                            && (EngineService.ShouldUseOnnxWaifu2x()
                                || gpuId < 0
                                || !await EngineService.IsWaifu2xNcnnUsableAsync(gpuId, ct)))
                            onnxPath = EsrganOnnxService.FindWaifu2xModel(model);
                        if (onnxPath != null)
                        {
                            Log("✅ 自检:已按当前显卡自动改用稳定引擎(直接处理,无需设置)");
                            progress.Report((0, "✅ 自检完毕:用稳定引擎处理..."));
                            // 【必须显式告知】ONNX 稳定引擎的权重是固定的:降噪档与 TTA 传进去也没有任何效果。
                            // 静默忽略 = 界面显示"已开降噪/TTA"而实际没做(预览≠结果),所以写日志 + 界面上方黄字提示。
                            var dropped = new List<string>();
                            if (isAnime && noise >= 0) dropped.Add($"降噪级别({NoiseCombo.SelectedIndex})");
                            if (tta) dropped.Add("高级增强(TTA)");
                            // 模型同理:ONNX 侧 waifu2x 目前只有 cunet 一份,选了 upconv_7_photo / upconv_7_anime
                            // 实际仍跑 cunet(实测确认会被静默忽略)→ 与"预览≠结果"同性质,必须告知。
                            if (engine == "waifu2x" && !string.IsNullOrEmpty(model))
                            {
                                string want = model.Replace("models-", "", StringComparison.OrdinalIgnoreCase)
                                                   .Replace("models_", "", StringComparison.OrdinalIgnoreCase);
                                if (!System.IO.Path.GetFileName(onnxPath).Contains(want, StringComparison.OrdinalIgnoreCase))
                                    dropped.Add($"所选模型({model},稳定引擎只有 cunet)");
                            }
                            if (dropped.Count > 0)
                            {
                                var capMsg = $"当前引擎(ONNX 稳定引擎)不支持 {string.Join(" / ", dropped)} — 已忽略,其余参数照常生效";
                                Log($"  ⚠ {capMsg}");
                                ShowEngineCapHint(capMsg);
                            }
                            await AbandonOnCancelAsync(
                                EsrganOnnxService.UpscaleAsync(srcPath, outPath, scale,
                                    gpuId < 0 ? -1 : -2, progress, CancellationToken.None, onnxPath), ct);   // 用户选 CPU(-1)则强制 CPU;否则按图大小自动选设备
                            // ONNX 出图后收尾:1x 超分缩回源尺寸 + 按扩展名(该 JPG 就写真 JPEG 字节)
                            var finLog = await Task.Run(() => FinalizeOnnxOutput(srcPath, outPath, upscaleShrink1x, imgQ));
                            if (finLog != null) Log(finLog);
                        }
                        else
                        {
                            if (engine == "realesrgan" && !esrganNcnnOk)
                                Log("⚠ 自检:当前显卡与老引擎不兼容且未找到稳定版,回退旧引擎(可能失败,建议改用 waifu2x)");
                                // 【把原因说清】探测带回的失败形态直接进日志 + 黄字提示:
                                // "初始化即崩"在 50 系上就是 NVIDIA 的驱动缺陷,不能让它看起来像我们的问题。
                                if (!string.IsNullOrEmpty(EngineService.LastProbeUserMessage))
                                {
                                    Log("  " + EngineService.LastProbeUserMessage);
                                    ShowEngineCapHint(EngineService.LastProbeUserMessage);
                                }
                            else
                                Log($"✅ 自检完毕:{(engine == "realesrgan" ? "ncnn GPU 引擎可用(快)" : "常规引擎")}");
                            await EngineService.UpscaleAsync(srcPath, outPath, engine,
                                model, scale, noise, gpuId, tta, progress, ct,
                                // 分块放大提速:分块大小用「安全渲染」墙(按显存自适应),不翻倍——
                                // 翻倍(GetTileSize()*2)会让小显存卡(6~8GB)OOM 掉 CPU,且用整图直跑的 `-t` 内部分块有接缝风险;
                                // 用安全墙后,更大图会走 App 自定义分块(重叠+羽化,无接缝),更稳。
                                tileSize: SafeRender.GetTileSize(),
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
                        // 黑块修复:ncnn GPU 持续黑块且 CPU 模式不可用(真机:RTX 2080 等) → 整张改用 ONNX 稳定引擎
                        if (!retried && ex.Message.Contains("BLACKOUT_NEED_ONNX", StringComparison.OrdinalIgnoreCase))
                        {
                            retried = true;
                            string? onnxRetry = engine == "waifu2x" ? EsrganOnnxService.FindWaifu2xModel()
                                : EsrganOnnxService.ResolveEsrganOnnxPath(model);
                            if (onnxRetry != null)
                            {
                                try
                                {
                                    Log("  ⚠ 黑块修复:该显卡 ncnn 引擎黑块且 CPU 不可用,自动改用 ONNX 稳定引擎重试...");
                                    var retrySrc = converted ?? item.Path;
                                    await EsrganOnnxService.UpscaleAsync(retrySrc, outPath, scale, gpuId < 0 ? -1 : -2, progress, ct, onnxRetry);
                                    // 同主分支:重试也是 ONNX 出图,1x 缩回与"JPG 里不能装 PNG 字节"同样要收尾
                                    var retryLog = await Task.Run(() => FinalizeOnnxOutput(retrySrc, outPath, upscaleShrink1x, imgQ));
                                    if (retryLog != null) Log(retryLog);
                                    succeeded = true;
                                }
                                catch { }
                            }
                        }
                        if (!retried && !succeeded)
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
                        if (!succeeded)
                        {
                            // 失败时清理本次已生成的不完整输出,避免"失败却有文件"的误解
                            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
                            AppLogger.Error($"图片处理失败: {item.Name}", ex);
                            Log($"  ✗ 失败:{ex.Message}(不完整输出已清理)");
                            item.StatusText = "✗ 失败";
                            failCount++;
                            break;
                        }
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
                    catch (OperationCanceledException) { throw; }   // 取消必须继续传播(否则被标"✓ 完成")
                    catch (Exception ex)
                    {
                        // 单张增强失败不中断整批(如文件被占用)
                        Log($"  ⚠ 增强失败(已跳过):{ex.Message}");
                    }
                }
                // 整张处理完成(超分+增强都结束)后,才原子移动到输出目录的最终文件名 → 目录只在真正完成时出现成品
                try { File.Move(outPath, finalOut, true); outPath = finalOut; }
                catch { /* move 失败(文件被占用等):保留临时路径,继续按当前 outPath 处理 */ }
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

    /// <summary>ONNX 推理无法中途打断:取消时放弃等待(孤儿推理后台几秒内自行收尾并丢弃),
    /// 保证「强制结束」对任何图都立即响应(真正强制)。</summary>
    private static async Task AbandonOnCancelAsync(Task t, CancellationToken ct)
    {
        try { await t.WaitAsync(ct); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* t 自己取消了,正常 */ throw; }
        // ct 触发 → WaitAsync 抛 OCE → 传播取消;底层 t(用 None)仍在后台跑完,结果被丢弃
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        AppLogger.UserAction("图片:点击「强制结束」");
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
        if (failLately == 0) MainPage.MaybeShowSponsorPrompt();   // 仅全部导出成功才弹赞助提示(有失败不打扰)
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
