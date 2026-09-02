using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ALHPro.Views;

public sealed partial class MainPage : Page
{
    private UpscaleView? _upView;
    private CutoutView? _cutView;
    private VideoView? _videoView;
    private AudioView? _audioView;
    private TutorialView? _tutorialView;
    private string _currentTag = "upscale";
    private int _startupPageIndex;   // 构造时解析的启动页(0/1/2),供 Loaded 弹窗逻辑引用

    public MainPage()
    {
        this.InitializeComponent();
        AppSettings.Load();      // 应用级开关(自动删除已完成项目等)
        SafeRender.Load();       // 加载"安全渲染"墙配置(自动/自定义)
        // 休息提示 + 显眼的「跳过休息」按钮:显示在窗口底部状态栏右侧(任务处理中休息降温时)
        SafeRender.RestUiChanged += resting =>
        {
            RestHint.Visibility = resting ? Visibility.Visible : Visibility.Collapsed;
            SkipRestBtn.Visibility = resting ? Visibility.Visible : Visibility.Collapsed;
        };
        LoadStartupPage();   // 默认启动页(-1=上次退出 0图片 1抠图 2视频)
        // 退出时记录最后一次使用的界面(「上次退出界面」启动模式保证准确;切换时也已记录,这里兜底)
        try { App.MainWindow.Closed += (_, _) => SaveLastPage(_currentTag == "video" ? 2 : _currentTag == "cutout" ? 1 : 0); } catch { }
        // 视图随构造一起就绪(同步):窗口 Activate 在 App 侧,Navigate(MainPage) 完成即已构造好
        // MainPage + 挂载视图 → 窗口一出现就是完整界面(用户要求:等渲染完再开窗口)
        {
            int page0 = _startupPage >= 0 ? _startupPage : LoadLastPage();
            _startupPageIndex = page0;
            NavList.SelectedIndex = page0;   // 触发 SelectionChanged → ShowView(唯一入口;下面不再重复调用)
            // 注意:曾在此处再手动 ShowView 一次 → 视图被创建两次(日志"进入页面:图片放大"出现2次),
            // 第二个实例用默认值覆盖第一个恢复的设置 → "图片记不住格式/码率"的真正元凶。已删。
        }
        Loaded += async (_, _) =>
        {
            // 首个弹窗/检查类任务(视图已在构造时挂载)
            // 内测声明:仅第一次启动显示;同意后记标记,以后不再弹;拒绝则退出
            if (!File.Exists(BetaAcceptedFile) && !await ShowBetaNoticeAsync())
            {
                App.MainWindow.Close();
                return;
            }
            if (!File.Exists(BetaAcceptedFile))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(BetaAcceptedFile)!);
                    File.WriteAllText(BetaAcceptedFile, "accepted");
                }
                catch { }
            }
            // 新版本更新弹窗:版本变化(或首次安装)后启动弹一次(同一"更新日志"结构,作者的话在最顶部)
            try
            {
                if (AppSettings.LastShownVersion != UpdateChecker.CurrentVersion)
                    _ = ShowUpdateLogAsync(fromStartup: true);
            }
            catch { }
            // 引擎可用性:仅缺失时提示,正常就绪不刷屏
            var ok = EngineService.CheckEngines(out var missing);
            StatusText.Text = ok ? "就绪" : "引擎缺失: " + missing;
            AppLogger.Info($"安全渲染:模式={(SafeRender.Mode == 0 ? "自动" : "自定义")}," +
                $"显存墙 {SafeRender.EffectiveVramGB:0.#} GB(总 {SafeRender.TotalVramGB:0.#} GB / 空闲 {SafeRender.FreeVramGB:0.#} GB)," +
                $"分块 {SafeRender.GetTileSize()},内存墙 {SafeRender.EffectiveRamGB:0.#} GB," +
                $"视频批 {SafeRender.GetVideoBatchSize()} 帧/批,CPU {SafeRender.EffectiveCpuLevel switch { 1 => "低", 2 => "中", _ => "高" }}({SafeRender.CpuCoreCount} 核)," +
                $"降温休息={(SafeRender.RestEnabled ? "开(1小时/15分钟)" : "关")}");
            // Vulkan 自检:后台跑完,无 GPU 自动切 CPU。弹窗「设备检测」只对低配设备(无GPU/显存<6/内存<8/核数≤4)
            // 自动弹一次友好提示;强机不弹(结果随时可在「设置 → 计算设备」查看),弹过也不再重复弹。
            // 【新增】真引擎自检:每次启动都后台跑一次(waifu2x 引擎枚举 Vulkan 设备),结果=日志+状态栏
            // (之前 RunOnce 只在设置页手动触发,启动从未自检过)。
            _ = Task.Run(async () =>
            {
                try
                {
                    VulkanCheck.RunOnce();
                    bool gpuOk = VulkanCheck.GpuAvailable;
                    AppLogger.Info("Vulkan 自检:" + (gpuOk ? "GPU 引擎可用(Vulkan 设备枚举成功)" : "GPU 引擎不可用(未枚举到 Vulkan 设备),建议设置中选 CPU") + VulkanCheck.Report);
                    // ===== 智能联动(自检结果 → 自动适配,日志+状态栏可见,不弹窗)=====
                    string? autoMsg = null;
                    if (!gpuOk)
                    {
                        if (AppSettings.GpuIndex >= 0)
                        {
                            // 仅本次会话切 CPU:【不写入设置文件】——驱动抽风/临时枚举失败时,
                            // 不会把用户选好的 GPU 覆盖掉;显卡恢复后下次启动自动回到用户选的设备
                            AppSettings.GpuIndex = -1;
                            autoMsg = "已自动适配(仅本次):未检测到可用 GPU → 本次处理设备已切为 CPU;你的设置未改动,显卡恢复正常后重启软件自动恢复";
                        }
                        else
                        {
                            autoMsg = "未检测到可用 GPU → 处理设备=CPU(软件计算)";
                        }
                    }
                    else if (VulkanCheck.Devices.Count > 0 && AppSettings.GpuIndex >= 0
                             && !VulkanCheck.Devices.Any(d => d.Id == AppSettings.GpuIndex))
                    {
                        // 用户当前编号不在引擎实际枚举列表 → 编号错位(注册表顺序≠Vulkan 顺序),自动纠正
                        // 纠正:按优先级逐个 1×1 实测,选第一个能用的独显(不只看名字,真机验证)
                        int best = await EngineService.FindBestWorkingGpuAsync();
                        if (best >= 0)
                        {
                            var nm = VulkanCheck.Devices.FirstOrDefault(d => d.Id == best).Name;
                            AppLogger.Info($"编号纠正:当前设备 {AppSettings.GpuIndex} 不在引擎实际枚举列表 {string.Join(",", VulkanCheck.Devices.Select(d => d.Id + ":" + d.Name))}," +
                                $"已按实测可用自动改为 GPU {best}({nm})");
                            AppSettings.GpuIndex = best;
                            try { AppSettings.Save(); } catch { }
                            autoMsg = $"已自动纠正设备编号 → GPU {best}(实测可用)";
                        }
                        else
                        {
                            AppLogger.Info("⚠ 编号纠正:引擎枚举设备均未通过 1×1 实测,保持当前选择(处理中会自动降级)");
                        }
                    }
                    else if (VulkanCheck.Devices.Count > 0 && AppSettings.GpuIndex >= 0
                             && VulkanCheck.Devices.Any(d => d.Id == AppSettings.GpuIndex)
                             && GpuInfo.IsIntegratedGPU(VulkanCheck.Devices.First(d => d.Id == AppSettings.GpuIndex).Name))
                    {
                        // 当前选中了核显(Intel/AMD 集成显卡,性能差且可能不适用部分引擎)
                        // → 按优先级实测,自动切到第一个能用的独显(真机:RTX5060 三卡机曾误选 Intel)
                        int best = await EngineService.FindBestWorkingGpuAsync();
                        if (best >= 0 && best != AppSettings.GpuIndex)
                        {
                            var oldN = VulkanCheck.Devices.First(d => d.Id == AppSettings.GpuIndex).Name;
                            var newN = VulkanCheck.Devices.First(d => d.Id == best).Name;
                            AppLogger.Info($"自动切换:当前设备 {oldN}(核显)→ 实测可用独显 GPU {best}({newN})");
                            AppSettings.GpuIndex = best;
                            try { AppSettings.Save(); } catch { }
                            autoMsg = $"已自动切换到独显 → GPU {best}(实测可用)";
                        }
                        else if (best < 0)
                        {
                            AppLogger.Info("⚠ 独显实测均不可用,继续使用当前核显(处理中会自动降级)");
                        }
                    }
                    if (autoMsg != null) AppLogger.Info("🚀 " + autoMsg);
                    string finalMsg = autoMsg ?? "就绪";
                    DispatcherQueue.TryEnqueue(() => { StatusText.Text = finalMsg; });
                }
                catch (Exception ex)
                {
                    AppLogger.Info("Vulkan 自检异常: " + ex.Message);
                }
            });
            // 更新检查:后台静默(有新版才弹提示条;失败/无网/已最新均无感)
            _ = CheckUpdateSilentAsync();
        };
    }

    /// <summary>生成设备下拉标签与推荐编号:优先【引擎实际枚举】(VulkanCheck.Devices,含引擎真实 -g 编号),
    /// 引擎未枚举时退回注册表顺序。引擎枚举才是用户真正能用的设备,避免"注册表选GPU1实际用GPU0"错位。</summary>
    private static (System.Collections.Generic.List<string> labels, int recommended) BuildGpuLabels()
    {
        var labels = new System.Collections.Generic.List<string>();
        try
        {
            // 引擎枚举(VulkanCheck 启动时用 waifu2x 引擎跑过,Devices = 引擎真实 -g 编号表)
            var devs = ALHPro.VulkanCheck.Devices;
            int recommended = -1;
            if (devs.Count > 0)
            {
                for (int i = 0; i < devs.Count; i++)
                {
                    var d = devs[i];
                    string mark = i == 0 ? "" : "";
                    labels.Add($"GPU {d.Id} · {d.Name}{mark}");
                    // 推荐:非 Intel/AMD 核显的第一张(通常就是独立 NVIDIA)
                    if (recommended < 0 && IsDiscreteGpu(d.Name)) recommended = d.Id;
                }
                if (recommended < 0) recommended = devs[0].Id;   // 全核显/未知:第一张
                return (labels, recommended);
            }
            // 引擎未枚举(检测没跑成):退回注册表顺序(旧逻辑)
            var (regLabels, regRec) = GpuInfo.BuildLabels();
            return (regLabels, regRec);
        }
        catch { return (labels, -1); }
    }

    /// <summary>判断是否独立显卡(非 Intel/AMD 核显):与 GpuInfo.ScoreDeviceName 同一套特征(含新版 "Intel(R) Graphics" 核显名)。</summary>
    private static bool IsDiscreteGpu(string name) => !GpuInfo.IsIntegratedGPU(name);

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is ListViewItem item && item.Tag is string tag)
            ShowView(tag);
    }

    // ---------- 更新检查 ----------
    /// <summary>启动静默检查:有新版本才显示提示条;失败/已最新不打扰。</summary>
    private async Task CheckUpdateSilentAsync()
    {
        var r = await UpdateChecker.CheckAsync().ConfigureAwait(false);
        if (r is not { HasNew: true }) return;   // 失败/已最新 → 无感
        var (_, tag, _) = r.Value;
        DispatcherQueue.TryEnqueue(() => ShowUpdateBar(tag));
    }

    private void ShowUpdateBar(string latestTag)
    {
        UpdateBarText.Text = $"发现新版本 {latestTag}(当前 v{UpdateChecker.CurrentVersion})";
        UpdateBar.Visibility = Visibility.Visible;
    }

    private void UpdateBarGo_Click(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(UpdateChecker.ReleasePageUrl) { UseShellExecute = true }); }
        catch { /* 打开失败忽略 */ }
        UpdateBar.Visibility = Visibility.Collapsed;
    }

    private void UpdateBarClose_Click(object sender, RoutedEventArgs e)
        => UpdateBar.Visibility = Visibility.Collapsed;

    private void ShowView(string tag)
    {
        _currentTag = tag;
        ContentRoot.Children.Clear();
        // 记录最近使用界面(「上次退出界面」启动模式用);切换即保存,退出时也保存(见 MainWindow_Closed)
        if (tag != "tutorial")
            SaveLastPage(tag == "upscale" ? 0 : tag == "video" ? 2 : tag == "audio" ? 3 : 1);
        if (tag == "upscale")
        {
            AppLogger.Info("进入页面:图片放大");
            _upView ??= new UpscaleView();
            _upView.StatusChanged -= OnStatusChanged;
            _upView.StatusChanged += OnStatusChanged;
            ContentRoot.Children.Add(_upView);
        }
        else if (tag == "video")
        {
            try
            {
                AppLogger.Info("进入页面:视频处理");
                _videoView ??= new VideoView();
                ContentRoot.Children.Add(_videoView);
            }
            catch (Exception ex)
            {
                AppLogger.Error($"视频页加载失败 HRESULT=0x{ex.HResult:X8}", ex);
                // 不闪退:显示错误信息,便于定位
                _videoView = null;
                ContentRoot.Children.Add(new TextBlock
                {
                    Text = "视频页加载失败(已记录到诊断日志):\n" + ex.Message,
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                    FontSize = 13,
                    Margin = new Microsoft.UI.Xaml.Thickness(20),
                });
            }
        }
        else if (tag == "audio")
        {
            AppLogger.Info("进入页面:音频处理");
            _audioView ??= new AudioView();
            _audioView.StatusChanged -= OnStatusChanged;
            _audioView.StatusChanged += OnStatusChanged;
            ContentRoot.Children.Add(_audioView);
        }
        else if (tag == "tutorial")
        {
            AppLogger.Info("进入页面:使用教程");
            _tutorialView ??= new TutorialView();
            ContentRoot.Children.Add(_tutorialView);
        }
        else
        {
            AppLogger.Info("进入页面:AI 抠图");
            _cutView ??= new CutoutView();
            _cutView.StatusChanged -= OnStatusChanged;
            _cutView.StatusChanged += OnStatusChanged;
            ContentRoot.Children.Add(_cutView);
        }
    }

    private void OnStatusChanged(string s) => StatusText.Text = s;

    /// <summary>左下角「使用教程」:进入教程页(不改变左侧功能导航高亮)。</summary>
    private void Tutorial_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        NavList.SelectedIndex = -1;
        ShowView("tutorial");
    }

    /// <summary>左侧边栏「更新日志」:各版本更新内容(往期历史),作者的话在最顶部。</summary>
    private void UpdateLog_Click(object sender, RoutedEventArgs e)
    {
        try { _ = ShowUpdateLogAsync(fromStartup: false); } catch { }
    }

    /// <summary>启动声明已同意的标记文件(仅首次显示弹窗)。</summary>
    private static string BetaAcceptedFile => ParaPaths.SettingsFile("beta-accepted.txt");

    private sealed class HistEntry { public string v { get; set; } = ""; public string title { get; set; } = ""; public string notes { get; set; } = ""; }

    /// <summary>清洗 Markdown 痕迹再显示(更新说明是 .md 写的,弹窗不露 ##/**/====/--- 等符号)。</summary>
    private static string CleanNotes(string t)
    {
        if (string.IsNullOrEmpty(t)) return t;
        var sb = new System.Text.StringBuilder();
        foreach (var raw in t.Split('\n'))
        {
            var l = raw.Trim();
            if (l.Length == 0) { sb.AppendLine(); continue; }
            // 分隔线(==== / ----)整行丢弃
            if (l.All(c => c == '=' || c == '-' || c == '#' || c == ' ')) continue;
            l = l.Replace("## ", "").Replace("### ", "").Replace("**【", "【").Replace("】**", "】")
                 .Replace("**", "").Replace("`", "").Replace("❕", "").Replace("🎉", "").Trim();
            if (l.Length == 0) continue;
            sb.AppendLine(l);
        }
        return sb.ToString().TrimEnd();
    }

    private bool _updatePopupShown;

    /// <summary>读取当前版本 + 往期历史(清洗 Markdown)。</summary>
    private System.Collections.Generic.List<(string v, string title, string notes)> BuildUpdateEntries()
    {
        var entries = new System.Collections.Generic.List<(string v, string title, string notes)>();
        string curNotes = "未找到更新说明(RELEASE_NOTES.md 缺失)。";
        var notesPath = Path.Combine(AppContext.BaseDirectory, "RELEASE_NOTES.md");
        try { if (File.Exists(notesPath)) curNotes = File.ReadAllText(notesPath); } catch { }
        curNotes = CleanNotes(curNotes);
        entries.Add(($"v{UpdateChecker.CurrentVersion}", "当前版本", curNotes));
        var histPath = Path.Combine(AppContext.BaseDirectory, "release_history.json");
        try
        {
            if (File.Exists(histPath))
            {
                var hist = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.List<HistEntry>>(File.ReadAllText(histPath));
                if (hist != null)
                    foreach (var h in hist)
                        if (!entries.Any(en => en.v == "v" + h.v))
                            entries.Add(("v" + h.v, h.title, CleanNotes(h.notes)));
            }
        }
        catch { }
        return entries;
    }

    /// <summary>安全取主题画笔(资源不存在时透明,不崩)。</summary>
    private static Microsoft.UI.Xaml.Media.Brush SafeBrush(string key)
    {
        try { return (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[key]; }
        catch { return new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(18, 255, 255, 255)); }
    }

    private static TextBlock AuthorWords() => new()
    {
        FontSize = 12,
        Opacity = 0.85,
        TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
        Text = "来自作者的话\n软件当前仍处于早期开发阶段,功能方向与工程稳定性仍在持续完善中。尽管有开源模型提供底层能力支撑," +
            "但上层的调用适配、性能优化与长期维护,依然面临较大的工程挑战。作为个人发起的公益项目,我将在力所能及的范围内持续改进。" +
            "若您在使用过程中受益,欢迎通过赞赏给予一点支持,帮助项目走得更远。感谢每一份善意的理解和信任。",
    };

    /// <summary>更新说明:作者的话 + 更新内容。
    /// fromStartup=true(升级后首次弹):只显示【当前版本】内容,无版本列表——简洁弹窗;
    /// 从左侧边栏「更新日志」打开:附加版本列表(往期历史可翻)。
    /// 注意:不修改 ContentDialog 的任何尺寸/位置属性——就是尺寸改动导致的偏位,默认即居中。</summary>
    private async Task ShowUpdateLogAsync(bool fromStartup)
    {
        if (fromStartup)
        {
            if (_updatePopupShown) return;
            _updatePopupShown = true;
        }
        try
        {
            var ver = UpdateChecker.CurrentVersion;
            var entries = BuildUpdateEntries();
            if (entries.Count == 0) return;

            var full = new StackPanel { Spacing = 10 };
            full.Children.Add(new Border
            {
                CornerRadius = new Microsoft.UI.Xaml.CornerRadius(8),
                Padding = new Microsoft.UI.Xaml.Thickness(12, 10, 12, 10),
                Background = SafeBrush("AppPanelBrush"),
                Child = AuthorWords(),
            });
            full.Children.Add(new Border
            {
                Height = 1,
                Background = SafeBrush("AppBorderBrush"),
            });

            if (fromStartup)
            {
                // 弹窗:只显示当前版本内容(无列表)
                full.Children.Add(new ScrollViewer
                {
                    Content = new TextBlock { FontSize = 12.5, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap, Text = entries[0].notes },
                    MaxHeight = 340,
                    VerticalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Auto,
                });
            }
            else
            {
                // 更新日志:作者的话 + 版本列表 + 内容(往期可翻)
                var listBox = new ListView
                {
                    Width = 224,
                    SelectionMode = ListViewSelectionMode.Single,
                    VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Top,
                };
                foreach (var en in entries)
                    listBox.Items.Add(new TextBlock { Text = $"{en.v} · {en.title}", FontSize = 12, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap, TextTrimming = Microsoft.UI.Xaml.TextTrimming.CharacterEllipsis, MaxWidth = 206, Margin = new Microsoft.UI.Xaml.Thickness(0, 4, 0, 4) });
                var notesBox = new TextBlock { FontSize = 13, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap, Text = entries[0].notes };
                listBox.SelectionChanged += (_, _) =>
                {
                    int i = listBox.SelectedIndex;
                    if (i >= 0 && i < entries.Count) notesBox.Text = entries[i].notes;
                };
                listBox.SelectedIndex = 0;
                var scroll = new ScrollViewer
                {
                    Content = notesBox,
                    MaxHeight = 380,
                    VerticalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Auto,
                };
                var grid = new Grid { ColumnSpacing = 12 };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(224) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(1, Microsoft.UI.Xaml.GridUnitType.Star) });
                Grid.SetColumn(listBox, 0);
                grid.Children.Add(listBox);
                Grid.SetColumn(scroll, 1);
                grid.Children.Add(scroll);
                full.Children.Add(grid);
            }

            // 默认尺寸(不改大小/位置,系统自动居中)
            var dlg = new ContentDialog
            {
                Title = fromStartup ? $"更新说明 · ALH Pro v{ver}" : "更新日志 · ALH Pro",
                Content = full,
                XamlRoot = this.XamlRoot,
            };
            if (fromStartup)
            {
                dlg.PrimaryButtonText = "开始使用";
                dlg.DefaultButton = ContentDialogButton.Primary;
            }
            else
            {
                dlg.CloseButtonText = "关闭";
            }
            await dlg.ShowAsync();
            if (fromStartup)
            {
                AppSettings.LastShownVersion = ver;
                AppSettings.Save();
            }
        }
        catch { /* 弹窗失败不影响使用 */ }
    }

    /// <summary>欢迎弹窗(所有设备第一次启动只弹一次,合并两件事):
    /// 1) 正式版启动说明(简短无内测措辞);2) 设备自检报告(低配/高配都显示,检测完自动更新)。
    /// 按钮 3 秒倒计时后可用;点「开始使用」进主界面,点「退出程序」关闭应用。</summary>
    private async Task<bool> ShowBetaNoticeAsync()
    {
        int remain = 3;
        bool agreed = false;
        var agreeBtn = new Button
        {
            Content = $"开始使用 ({remain})",
            IsEnabled = false,
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Right,
            Margin = new Microsoft.UI.Xaml.Thickness(0, 10, 0, 0),
        };
        agreeBtn.Click += (_, _) => { agreed = true; _betaNoticeDlg.Hide(); };

        // 设备自检报告区:检测中占位,完成后渲染完整报告
        var reportBlock = new TextBlock
        {
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
            FontSize = 11,
            Opacity = 0.9,
        };
        var dlg = new ContentDialog
        {
            Title = "欢迎使用 ALH Pro",
            XamlRoot = this.XamlRoot,
            CloseButtonText = "退出程序",
            DefaultButton = ContentDialogButton.None,
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"本程序为「ALH Pro v{UpdateChecker.CurrentVersion}」正式版:\n\n" +
                            "· 图片超分 / AI 抠图 / 视频超分补帧去重,全部本地处理;\n" +
                            "· 处理前建议备份重要素材(AI 处理可能有边缘瑕疵);\n" +
                            "· 引擎/模型版权归各自作者所有,详见 README 与许可声明。\n\n" +
                            "【免责声明】本软件为免费工具,仅用于个人合法用途。请勿用于侵权/违法用途;\n" +
                            "处理结果仅供参考,重要素材请务必自行备份,作者不承担由此产生的任何损失。\n\n" +
                            "首次启动会进行一次本机设备自检,以下结果来自当前电脑:",
                        TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                        FontSize = 12,
                    },
                    new Border
                    {
                        Height = 1,
                        Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppBorderBrush"],
                        Margin = new Microsoft.UI.Xaml.Thickness(0, 2, 0, 2),
                    },
                    reportBlock,
                    agreeBtn,
                },
            },
        };
        _betaNoticeDlg = dlg;   // 供按钮关闭

        // 渲染自检结果(完成=完整报告;未完成=占位)
        void RenderReport()
        {
            if (VulkanCheck.Done && !string.IsNullOrEmpty(VulkanCheck.Report))
                BuildReportContent(reportBlock, VulkanCheck.Report, out _);
            else
            {
                reportBlock.Inlines.Clear();
                reportBlock.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
                {
                    Text = "正在检测本机设备(GPU / 显卡驱动 / 显存 / 内存 / CPU),请稍候…",
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                });
            }
        }
        RenderReport();

        // 自检完成前轮询刷新(通常 1 秒内;最多等 20 秒,超时也显示兜底)
        var checkTimer = DispatcherQueue.CreateTimer();
        checkTimer.Interval = TimeSpan.FromMilliseconds(200);
        checkTimer.IsRepeating = true;
        int waited = 0;
        checkTimer.Tick += (_, _) =>
        {
            waited++;
            if (!VulkanCheck.Done && waited < 100) return;
            checkTimer.Stop();
            RenderReport();
        };
        checkTimer.Start();

        // 按钮倒计时(3 秒)
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.IsRepeating = true;
        timer.Tick += (_, _) =>
        {
            remain--;
            if (remain > 0)
            {
                agreeBtn.Content = $"开始使用 ({remain})";
            }
            else
            {
                timer.Stop();
                agreeBtn.Content = "开始使用";
                agreeBtn.IsEnabled = true;
            }
        };
        timer.Start();
        await dlg.ShowAsync();
        timer.Stop();
        checkTimer.Stop();
        _betaNoticeDlg = null;
        return agreed;
    }
    private ContentDialog? _betaNoticeDlg;

    /// <summary>把自检报告渲染进弹窗:去掉「设备自检报告」标题行(设置界面不受影响);
    /// 所有内容(计算设备/驱动/显存/内存/CPU/可用性/建议/提示)统一普通样式,不加粗不变色;
    /// 「注意:」整块内容作为「提示:」区块放最下面。</summary>
    private static void BuildReportContent(TextBlock block, string report, out TextBlock? summary)
    {
        summary = null;
        block.Inlines.Clear();
        var lines = report.Split('\n');
        var noteLines = new System.Collections.Generic.List<string>();   // 「注意:」后面的整块内容(可能多行)
        bool inNote = false;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r').TrimEnd();
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("设备自检报告", StringComparison.Ordinal)) continue;   // 弹窗不显示标题行
            if (line.StartsWith("注意:", StringComparison.Ordinal))
            {
                inNote = true;
                noteLines.Add(line.Substring("注意:".Length).TrimStart());
                continue;
            }
            if (inNote)
            {
                // 「注意:」后的连续行都属于提示内容(如无 GPU 场景的多行注意)
                noteLines.Add(line);
                continue;
            }
            block.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = line });
            block.Inlines.Add(new Microsoft.UI.Xaml.Documents.LineBreak());
        }
        // 提示区块:整块放最下面,与普通内容同样式(不加粗不变色),前面空一行分隔
        if (noteLines.Count > 0)
        {
            block.Inlines.Add(new Microsoft.UI.Xaml.Documents.LineBreak());
            block.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
            {
                Text = "提示:" + string.Join(";", noteLines),
            });
            block.Inlines.Add(new Microsoft.UI.Xaml.Documents.LineBreak());
        }
        // 补充一句更详细的说明(按设备状态)
        var extra = VulkanCheck.GpuAvailable
            ? "若处理大图/高倍率时提示显存不足,程序会自动降低分块重试;仍失败时可在「计算设备」里改用 CPU。"
            : "当前以 CPU 计算,速度会明显慢;若电脑有独立显卡却检测不到,请更新显卡驱动(需支持 Vulkan)后点「重新检测」。";
        block.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = extra, FontSize = 11 });
    }

    /// <summary>生成默认头像(蓝色圆底 + AL 字母),供关于弹窗显示;用户可替换为程序目录下的 avatar.jpg。</summary>
    private static void CreateDefaultAvatar(string path)
    {
        using var bmp = new System.Drawing.Bitmap(256, 256);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(System.Drawing.Color.Transparent);
        using var bg = new System.Drawing.Drawing2D.LinearGradientBrush(
            new System.Drawing.Point(0, 0), new System.Drawing.Point(256, 256),
            System.Drawing.Color.FromArgb(255, 79, 140, 239), System.Drawing.Color.FromArgb(255, 52, 96, 190));
        g.FillEllipse(bg, 0, 0, 256, 256);
        using var font = new System.Drawing.Font("Segoe UI", 92, System.Drawing.FontStyle.Bold,
            System.Drawing.GraphicsUnit.Pixel);
        using var sf = new System.Drawing.StringFormat { Alignment = System.Drawing.StringAlignment.Center, LineAlignment = System.Drawing.StringAlignment.Center };
        using var tb = new System.Drawing.SolidBrush(System.Drawing.Color.White);
        g.DrawString("AL", font, tb, new System.Drawing.RectangleF(0, -4, 256, 256), sf);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Jpeg);
    }

    // 底部状态栏「跳过休息」:和任务面板的「取消(休息时变跳过休息)」等效,休息时在底部显眼处一键跳过
    private void SkipRestBtn_Click(object sender, RoutedEventArgs e)
        => SafeRender.CurrentRestCts?.Cancel();

    /// <summary>显示"更新详情":读取随包 RELEASE_NOTES.md(当前版本修复/改进/新增)。</summary>
    private void ShowReleaseNotes()
    {
        string text;
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "RELEASE_NOTES.md");
            text = File.Exists(path) ? CleanNotes(File.ReadAllText(path)) : "未找到 RELEASE_NOTES.md(程序目录)。";
        }
        catch (Exception ex) { text = "读取更新详情失败: " + ex.Message; }
        var dlg = new ContentDialog
        {
            Title = "更新详情 · ALH Pro v" + UpdateChecker.CurrentVersion,
            Content = new ScrollViewer
            {
                MaxHeight = 520,
                VerticalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Auto,
                Content = new TextBlock { Text = text, FontSize = 12, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap },
            },
            PrimaryButtonText = "关闭",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        try { _ = dlg.ShowAsync(); } catch { }
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var content = new StackPanel { Spacing = 8 };

        // 标题 + 版本 + 署名
        content.Children.Add(new TextBlock
        {
            Text = "ALH Pro",
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        content.Children.Add(new TextBlock
        {
            Text = $"版本 v{UpdateChecker.CurrentVersion} · 构建 {File.GetLastWriteTime(typeof(MainPage).Assembly.Location):MM-dd HH:mm}",
            FontSize = 12,
            Opacity = 0.7,
        });
        // 更新详情:查看当前版本更新了什么(读取随包 RELEASE_NOTES.md)
        var notesLink = new Microsoft.UI.Xaml.Controls.HyperlinkButton
        {
            Content = "查看更新详情(v" + UpdateChecker.CurrentVersion + ")",
            FontSize = 11,
            Padding = new Microsoft.UI.Xaml.Thickness(0, 0, 0, 0),
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left,
        };
        notesLink.Click += (_, _) =>
        {
            // 更新日志(历史版本可翻,作者的话在最顶部)
            try { _ = ShowUpdateLogAsync(fromStartup: false); } catch { }
        };
        content.Children.Add(notesLink);
        // 手动检查更新:点击后显示结果;成功展示"已最新/发现新版本",失败才提示(启动静默检查不打扰)
        var updateRow = new StackPanel { Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal, Spacing = 10 };
        var updateBtn = new Button
        {
            Content = "检查更新",
            FontSize = 11,
            Padding = new Microsoft.UI.Xaml.Thickness(14, 4, 14, 4),
        };
        var updateResult = new TextBlock
        {
            FontSize = 11,
            Opacity = 0.8,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
        };
        updateBtn.Click += async (_, _) =>
        {
            updateBtn.IsEnabled = false;
            updateResult.Text = "检查中...";
            var r = await UpdateChecker.CheckAsync();
            if (r is null)
            {
                updateResult.Text = "网络或 GitHub 不通(国内直连慢/被限制),建议:①使用加速器或镜像 ②稍后重试 ③直接在 GitHub 仓库页面查看最新 Release";
                updateBtn.IsEnabled = true;
                return;
            }
            var (hasNew, tag, _) = r.Value;
            if (hasNew)
            {
                updateResult.Inlines.Clear();
                var hyper = new Microsoft.UI.Xaml.Documents.Hyperlink
                {
                    NavigateUri = new Uri(UpdateChecker.ReleasePageUrl),
                    UnderlineStyle = Microsoft.UI.Xaml.Documents.UnderlineStyle.Single,
                };
                hyper.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = $"发现新版本 {tag},点此打开下载页" });
                updateResult.Inlines.Add(hyper);
                // 操作指引(追加在 updateResult 下方;动态插入 content)
                var guide = new TextBlock
                {
                    FontSize = 10,
                    Opacity = 0.65,
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                    Text = $"怎么更新:\n1. 点上方链接打开 GitHub Release 页面;打不开就用加速器/镜像,或找软件群/网盘获取安装包。\n2. 下载「ALHPro_v{tag.TrimStart('v')}_Setup.exe」(安装包;别下源码 zip)。\n3. 双击安装,等完成即可——设置/记录都保留,可手动卸载旧版。",
                };
                content.Children.Add(guide);
            }
            else
            {
                updateResult.Text = "已是最新版本 ✓";
            }
            updateBtn.IsEnabled = true;
        };
        updateRow.Children.Add(updateBtn);
        updateRow.Children.Add(updateResult);
        content.Children.Add(updateRow);

        // 作者 + 头像(头像文件:程序目录 avatar.jpg;缺失时自动生成默认头像,名字始终显示)
        var authorRow = new StackPanel { Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal, Spacing = 16 };
        var avatarFile = Path.Combine(AppContext.BaseDirectory, "avatar.jpg");
        if (!File.Exists(avatarFile))
        {
            try { CreateDefaultAvatar(avatarFile); } catch { /* 生成失败不影响 */ }
        }
        var authorGroup = new StackPanel { Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal, Spacing = 10 };
        if (File.Exists(avatarFile))
        {
            authorGroup.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 44,
                Height = 44,
                StrokeThickness = 0,
                Fill = new Microsoft.UI.Xaml.Media.ImageBrush
                {
                    ImageSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(avatarFile)),
                    Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
                },
            });
        }
        var authorTexts = new StackPanel { VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center, Spacing = 2 };
        authorTexts.Children.Add(new TextBlock { Text = "AlL.H", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        authorTexts.Children.Add(new TextBlock { Text = "作者 · 免费公益", FontSize = 10, Opacity = 0.6 });
        authorGroup.Children.Add(authorTexts);
        authorRow.Children.Add(authorGroup);
        content.Children.Add(authorRow);

        // 「请作者喝咖啡」:与左下角入口同一打赏卡片(赞赏码大图 + 爱发电主页)
        var rewardBtn = new Button
        {
            Content = "☕ 请作者喝咖啡",
            FontSize = 12,
            Padding = new Microsoft.UI.Xaml.Thickness(14, 6, 14, 6),
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left,
            Margin = new Microsoft.UI.Xaml.Thickness(0, 4, 0, 0),
        };
        ToolTipService.SetToolTip(rewardBtn, "完全免费,打赏自愿(赞赏码 + 爱发电主页)");
        rewardBtn.Click += (_, _) => ShowCoffeeCard();
        content.Children.Add(rewardBtn);
        content.Children.Add(new Border
        {
            Height = 1,
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppBorderBrush"],
        });

        // 功能
        content.Children.Add(new TextBlock
        {
            Text = "功能:图片超分(waifu2x / Real-ESRGAN)、AI 抠图、视频超分 + 光流补帧、\n智能去重、转场识别、批量处理、音频增强。",
            FontSize = 12,
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
        });

        // 模型致谢(蓝色超链接,点击跳转项目官网)
        content.Children.Add(new TextBlock
        {
            Text = "模型与引擎致谢(点击名称可访问项目)",
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        var modelLinks = new (string name, string desc, string url)[]
        {
            ("waifu2x", "naoaki + nihui/ncnn", "https://github.com/nagadomi/waifu2x"),
            ("Real-ESRGAN(含 ONNX)", "Xintao Wang 等 + nihui/ncnn", "https://github.com/xinntao/Real-ESRGAN"),
            ("waifu2x(含 ONNX)", "nagadomi/nunif", "https://github.com/nagadomi/waifu2x"),
            ("RIFE(含 ONNX)", "Zhewei Huang 等 + nihui/ncnn", "https://github.com/hzwer/arXiv2020-RIFE"),
            ("U²-Net", "Qin 等", "https://github.com/xuebinqin/U-2-Net"),
            ("ISNet", "Xuebin Qin 等", "https://github.com/xuebinqin/DIS"),
            ("BiRefNet", "ZhengPeng7(BiRefNet)", "https://github.com/ZhengPeng7/BiRefNet"),
            ("rembg(模型封装)", "Daniel Gatis", "https://github.com/danielgatis/rembg"),
            ("ffmpeg", "FFmpeg 团队(BtbN 构建)", "https://ffmpeg.org"),
            ("ONNX Runtime", "Microsoft", "https://github.com/microsoft/onnxruntime"),
            ("Windows App SDK / WinUI 3", "Microsoft", "https://github.com/microsoft/WindowsAppSDK"),
            (".NET 8", "Microsoft", "https://github.com/dotnet/runtime"),
            ("ONNX 转换模型(waifu2x/动漫动画)", "deepghs / tidus2102", "https://huggingface.co"),
            ("Apollo(音乐修复,计划)", "清华大学 / 腾讯 AI Lab", "https://github.com/JusperLee/Apollo"),
        };
        foreach (var (name, desc, url) in modelLinks)
        {
            var line = new TextBlock { FontSize = 11, Opacity = 0.85, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap };
            line.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "· " });
            var link = new Microsoft.UI.Xaml.Documents.Hyperlink { NavigateUri = new Uri(url) };
            link.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = name });
            line.Inlines.Add(link);
            line.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = " — " + desc });
            content.Children.Add(line);
        }
        // 许可声明可直接点开
        var noticesPath = Path.Combine(AppContext.BaseDirectory, "THIRD_PARTY_NOTICES.txt");
        if (File.Exists(noticesPath))
        {
            var docLink = new HyperlinkButton
            {
                Content = "📄 完整许可声明(THIRD_PARTY_NOTICES.txt)",
                FontSize = 11,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 76, 141, 255)),
            };
            docLink.Click += (_, _) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(noticesPath)
                    {
                        UseShellExecute = true,
                    });
                }
                catch { }
            };
            content.Children.Add(docLink);
        }

        // 致谢:自动扫描程序目录 thanks/ 文件夹(文件名=名字,支持后续添加)
        var thanksDir = Path.Combine(AppContext.BaseDirectory, "thanks");
        if (Directory.Exists(thanksDir))
        {
            var thankFiles = Directory.EnumerateFiles(thanksDir)
                .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                    || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                    || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                    || f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
                    || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))   // 纯名字文件(无头像;文件名即名字)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (thankFiles.Length > 0)
            {
                content.Children.Add(new Border
                {
                    Height = 1,
                    Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppBorderBrush"],
                });
                content.Children.Add(new TextBlock
                {
                    Text = "特别致谢(不分先后)— 帮忙找 Bug、反馈问题,让 ALH Pro 越来越好",
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                });
                // 名单:只显示头像 + 名字,横向排列自动换行
                var thanksControl = new ItemsControl();
                thanksControl.ItemsPanel = (Microsoft.UI.Xaml.Controls.ItemsPanelTemplate)
                    Microsoft.UI.Xaml.Markup.XamlReader.Load(
                        "<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
                        "<ItemsWrapGrid Orientation='Horizontal' ItemWidth='150' ItemHeight='64'/>" +
                        "</ItemsPanelTemplate>");
                foreach (var f in thankFiles)
                {
                    var name = Path.GetFileNameWithoutExtension(f);
                    bool hasAvatar = !f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
                    var row = new StackPanel
                    {
                        Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal,
                        Spacing = 8,
                        VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
                    };
                    if (hasAvatar)
                    {
                        row.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
                        {
                            Width = 40,
                            Height = 40,
                            StrokeThickness = 0,
                            Fill = new Microsoft.UI.Xaml.Media.ImageBrush
                            {
                                ImageSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(f)),
                                Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
                            },
                        });
                    }
                    // 只显示名字(无头像/无圆点占位,简洁)
                    row.Children.Add(new TextBlock
                    {
                        Text = name,
                        FontSize = 12,
                        VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
                    });
                    thanksControl.Items.Add(row);
                }
                content.Children.Add(thanksControl);
            }
        }

        // 关于弹窗:全屏遮罩 + 中央圆角卡片(水平垂直精确居中)
        var popup = new Microsoft.UI.Xaml.Controls.Primitives.Popup { XamlRoot = this.XamlRoot };
        var overlay = new Grid
        {
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(150, 0, 0, 0)),
        };
        // 遮罩随窗口尺寸变化自适应(全屏/调整大小时不残留显示问题);
        // 卡片与滚动区高度跟随窗口:窗口过小时也能滚动看全(不超出屏幕被截断)
        Border? card = null;
        ScrollViewer? scroll = null;
        Action ResizeOverlay = () =>
        {
            var w = this.ActualWidth > 0 ? this.ActualWidth : this.XamlRoot.Size.Width;
            var h = this.ActualHeight > 0 ? this.ActualHeight : this.XamlRoot.Size.Height;
            overlay.Width = w;
            overlay.Height = h;
            if (card != null) card.MaxHeight = Math.Max(280, h - 32);
            if (scroll != null) scroll.MaxHeight = Math.Max(220, h - 100);
        };
        // 单例守卫:已有关于弹窗打开时直接关闭旧的(防连点叠加)
        if (_aboutPopup?.IsOpen == true) _aboutPopup.IsOpen = false;
        void OnSizeChanged(object s, SizeChangedEventArgs a) => ResizeOverlay();
        this.SizeChanged += OnSizeChanged;
        popup.Closed += (_, _) => this.SizeChanged -= OnSizeChanged;
        card = new Border
        {
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppPanelBrush"],
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppBorderBrush"],
            BorderThickness = new Microsoft.UI.Xaml.Thickness(1),
            CornerRadius = new Microsoft.UI.Xaml.CornerRadius(14),
            Width = 660,
            MaxHeight = 780,
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
        };
        var cardPanel = new StackPanel();
        var header = new Grid { ColumnSpacing = 8, Margin = new Microsoft.UI.Xaml.Thickness(18, 12, 10, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = "关于 ALH Pro",
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
        });
        var closeBtn = new Button
        {
            Content = "✕",
            FontSize = 12,
            Padding = new Microsoft.UI.Xaml.Thickness(10, 4, 10, 4),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Microsoft.UI.Xaml.Thickness(0),
        };
        closeBtn.Click += (_, _) => popup.IsOpen = false;
        Grid.SetColumn(closeBtn, 1);
        header.Children.Add(closeBtn);
        cardPanel.Children.Add(header);
        cardPanel.Children.Add(new Border
        {
            Height = 1,
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppBorderBrush"],
            Margin = new Microsoft.UI.Xaml.Thickness(12, 0, 12, 6),
        });
        scroll = new ScrollViewer
        {
            Content = content,
            MaxHeight = 660,
            Padding = new Microsoft.UI.Xaml.Thickness(18, 0, 22, 16),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        cardPanel.Children.Add(scroll);
        card.Child = cardPanel;
        overlay.Children.Add(card);
        ResizeOverlay();   // 首次按当前窗口尺寸设置卡片/滚动区高度
        // 点遮罩关闭;卡片内部点击不冒泡
        overlay.Tapped += (_, _) => popup.IsOpen = false;
        card.Tapped += (_, args) => args.Handled = true;
        popup.Child = overlay;
        _aboutPopup = popup;
        popup.IsOpen = true;
    }

    private Microsoft.UI.Xaml.Controls.Primitives.Popup? _aboutPopup;   // 关于弹窗(单例守卫)
    private Microsoft.UI.Xaml.Controls.Primitives.Popup? _logPopup;     // 日志弹窗(单例守卫)

    /// <summary>默认启动页:-1=上次退出界面(默认) 0=图片放大 1=AI 抠图 2=视频处理。</summary>
    private int _startupPage = -1;
    private static string StartupFile => ParaPaths.SettingsFile("startup-page.txt");
    // 最近一次使用的界面(切换即写,退出时也写):"上次退出界面"模式启动用
    private static string LastPageFile => ParaPaths.SettingsFile("last-page.txt");

    private void LoadStartupPage()
    {
        try
        {
            if (File.Exists(StartupFile) && int.TryParse(File.ReadAllText(StartupFile).Trim(), out var p)
                && p is >= -1 and <= 3)
                _startupPage = p;
        }
        catch { }
    }

    private void SaveStartupPage(int p)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StartupFile)!);
            File.WriteAllText(StartupFile, p.ToString());
        }
        catch { }
    }

    /// <summary>记录最近使用的界面(0图片 1抠图 2视频):切换页面与退出时都写,供「上次退出界面」启动。</summary>
    private void SaveLastPage(int page)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LastPageFile)!);
            File.WriteAllText(LastPageFile, page.ToString());
        }
        catch { }
    }

    private int LoadLastPage()
    {
        try
        {
            if (File.Exists(LastPageFile) && int.TryParse(File.ReadAllText(LastPageFile).Trim(), out var p)
                && p is >= 0 and <= 2)
                return p;
        }
        catch { }
        return 0;   // 无历史记录:默认图片放大
    }

    /// <summary>设置弹窗(诊断日志 + 后续更多设置项)。</summary>
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var content = new StackPanel { Spacing = 10 };

        // ================= 默认打开应用时界面(最顶部) =================
        content.Children.Add(new TextBlock
        {
            Text = "默认打开应用时界面",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        var pageCombo = new ComboBox { HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch };
        pageCombo.Items.Add(new ComboBoxItem { Content = "上次退出界面(默认)" });
        pageCombo.Items.Add(new ComboBoxItem { Content = "图片放大" });
        pageCombo.Items.Add(new ComboBoxItem { Content = "AI 抠图" });
        pageCombo.Items.Add(new ComboBoxItem { Content = "视频处理" });
        pageCombo.Items.Add(new ComboBoxItem { Content = "音频处理" });
        pageCombo.SelectedIndex = _startupPage + 1;   // 下拉索引 = 模式 + 1(-1→0,0→1,…)
        pageCombo.SelectionChanged += (_, _) =>
        {
            _startupPage = pageCombo.SelectedIndex - 1;   // 还原:0→-1(上次退出),1→0(图片),…
            SaveStartupPage(_startupPage);
            AppLogger.Info($"启动页面已设为:{_startupPage switch { -1 => "上次退出界面", 0 => "图片放大", 1 => "AI 抠图", 2 => "视频处理", _ => "音频处理" }}");
        };
        content.Children.Add(pageCombo);
        content.Children.Add(new TextBlock
        {
            Text = "打开应用时默认进入的界面,下次启动生效。",
            FontSize = 10, Opacity = 0.5,
        });
        content.Children.Add(new Border
        {
            Height = 1,
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppBorderBrush"],
        });

        // ================= 处理完成后自动删除项目 =================
        var autoRemove = new CheckBox
        {
            Content = "处理完成后自动删除项目",
            IsChecked = AppSettings.AutoRemoveDone,
        };
        ToolTipService.SetToolTip(autoRemove,
            "开启后:处理完成的项目(图片/视频)等 3 秒自动从列表删除(留 3 秒看完成信息);关闭则保留在列表,已完成的视频会变灰,点「重新激活」可再处理");
        autoRemove.Checked += (_, _) => { AppSettings.AutoRemoveDone = true; AppSettings.Save(); AppLogger.Info("已开启「完成后自动删除项目」(等 3 秒)"); };
        autoRemove.Unchecked += (_, _) => { AppSettings.AutoRemoveDone = false; AppSettings.Save(); AppLogger.Info("已关闭「完成后自动删除项目」"); };
        content.Children.Add(autoRemove);
        content.Children.Add(new TextBlock
        {
            Text = "开启后:跑完的图片/视频等 3 秒自动从列表删除(留 3 秒看结果信息);关闭则一直留在列表。",
            FontSize = 10, Opacity = 0.5,
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
        });
        content.Children.Add(new Border
        {
            Height = 1,
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppBorderBrush"],
        });

        // ================= 计算设备(全局:图片/抠图/视频共用) =================
        content.Children.Add(new TextBlock
        {
            Text = "计算设备",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        var gpuCombo = new ComboBox { HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch };
        // ===== 设备列表以【引擎实际枚举】为准(VulkanCheck.Devices,带引擎真实 -g 编号)=====
        // 注册表顺序≠引擎 -g 编号(实测:注册表[0 Intel][1 NVIDIA],引擎[1 NVIDIA][2 Intel])——
        // 用注册表顺序生成下拉会让用户选错卡。VulkanCheck 跑过 waifu2x 引擎枚举,是真实的设备表。
        var (gpuLabels, gpuRec) = BuildGpuLabels();
        int gpuCount = gpuLabels.Count;
        if (gpuLabels.Count > 0)
        {
            foreach (var l in gpuLabels)
                gpuCombo.Items.Add(new ComboBoxItem { Content = l });
        }
        else
        {
            // 枚举失败:仍提供 GPU 0/1 选项
            gpuCombo.Items.Add(new ComboBoxItem { Content = "GPU 0" });
            gpuCombo.Items.Add(new ComboBoxItem { Content = "GPU 1" });
            gpuCount = 2;
        }
        gpuCombo.Items.Add(new ComboBoxItem { Content = "CPU (软件计算)" });
        // 当前全局选择:-1=CPU(末项);≥0=GPU 编号(引擎枚举的编号)
        gpuCombo.SelectedIndex = AppSettings.GpuIndex >= 0 && AppSettings.GpuIndex < gpuCount
            ? AppSettings.GpuIndex : gpuCount;
        gpuCombo.SelectionChanged += (_, _) =>
        {
            // 末项=CPU
            AppSettings.GpuIndex = gpuCombo.SelectedIndex >= gpuCount ? -1 : gpuCombo.SelectedIndex;
            AppSettings.Save();
            AppLogger.Info($"计算设备已设为:{gpuCombo.SelectedItem?.ToString()}");
        };
        content.Children.Add(gpuCombo);
        content.Children.Add(new TextBlock
        {
            Text = "三个功能(图片放大 / AI 抠图 / 视频处理)统一使用这里选的计算设备。编号顺序可能与引擎实际识别的设备不一致(Windows 顺序 ≠ 引擎顺序):若选某编号处理崩/慢,换其它编号实测,日志「引擎启动...设备 -g X」会显示所选编号。无独显的电脑建议选 CPU(软件计算)。注意:AI 抠图已强制使用 CPU(GPU 推理会占满显卡导致整机卡),此处设置对抠图不生效。",
            FontSize = 10, Opacity = 0.5,
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
        });
        // 本机 GPU 加速自检报告(首次启动实测,常驻显示:告诉用户当前设备状态 + 会有什么问题)
        TextBlock? reportText = null;
        if (AppSettings.VulkanCheckDone && !string.IsNullOrEmpty(AppSettings.VulkanReport))
        {
            reportText = new TextBlock
            {
                Text = AppSettings.VulkanReport,
                FontSize = 11,
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                IsTextSelectionEnabled = true,   // 可复制分享给作者排查
            };
            content.Children.Add(new Border
            {
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(18, 120, 190, 130)),
                CornerRadius = new Microsoft.UI.Xaml.CornerRadius(6),
                Padding = new Microsoft.UI.Xaml.Thickness(10, 8, 10, 8),
                Margin = new Microsoft.UI.Xaml.Thickness(0, 4, 0, 0),
                Child = reportText,
            });
        }
        else
        {
            reportText = new TextBlock
            {
                Text = "首次启动正在后台检测本机 GPU 加速支持,检测结果会显示在这里。",
                FontSize = 10, Opacity = 0.6,
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
            };
            content.Children.Add(reportText);
        }
        // 重新检测按钮:重新跑一遍设备自检(GPU/驱动/显存/内存/CPU),完成后自动更新报告
        var recheckBtn = new Button
        {
            Content = "重新检测",
            FontSize = 11,
            Padding = new Microsoft.UI.Xaml.Thickness(12, 4, 12, 4),
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left,
            Margin = new Microsoft.UI.Xaml.Thickness(0, 6, 0, 0),
        };
        recheckBtn.Click += (_, _) =>
        {
            recheckBtn.IsEnabled = false;
            recheckBtn.Content = "检测中…";
            reportText.Text = "正在重新检测本机设备(GPU / 显卡驱动 / 显存 / 内存 / CPU)…";
            VulkanCheck.Completed += OnVulkanRecheckDone;
            VulkanCheck.Recheck();
            void OnVulkanRecheckDone()
            {
                VulkanCheck.Completed -= OnVulkanRecheckDone;
                try
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        reportText.Text = VulkanCheck.Report;
                        recheckBtn.IsEnabled = true;
                        recheckBtn.Content = "重新检测";
                    });
                }
                catch { }
            }
        };
        content.Children.Add(recheckBtn);
        content.Children.Add(new Border
        {
            Height = 1,
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppBorderBrush"],
        });

        // ================= 安全渲染(显存/内存/CPU 墙,放最上面) =================
        content.Children.Add(new TextBlock
        {
            Text = "安全渲染",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        content.Children.Add(new TextBlock
        {
            Text = "防止处理大图/视频时电脑卡死或闪退。选【自动】就行:程序按这台电脑的配置自动限制,换电脑会重新检测、自动调整。",
            FontSize = 12, Opacity = 0.75, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
        });

        // 模式:自动 / 手动
        var modeRadios = new RadioButtons { SelectedIndex = SafeRender.Mode };
        modeRadios.Items.Add(new RadioButton { Content = "自动" });
        modeRadios.Items.Add(new RadioButton { Content = "手动设置上限" });
        content.Children.Add(modeRadios);

        // 手动面板:三个可调安全墙,选中「手动设置上限」时才显示(放在休息/温度墙复选框上面)
        var manualPanel = new StackPanel { Spacing = 6, Visibility = SafeRender.Mode == 1 ? Visibility.Visible : Visibility.Collapsed };

        // 显存上限(滑条 + 数值 + 重置为设备最优值)
        var vramLabel = new TextBlock
        {
            Text = "显存上限:AI 最多用多少显存(GB)",
            FontSize = 11, Opacity = 0.8,
        };
        manualPanel.Children.Add(vramLabel);
        var vramRow = new Grid { ColumnSpacing = 8 };
        vramRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        vramRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        vramRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var vramSlider = new Slider
        {
            Minimum = 1,
            Maximum = Math.Min(24, Math.Max(2, (int)Math.Round(SafeRender.TotalVramGB))),
            Value = SafeRender.VramCapGB > 0
                ? Math.Clamp(SafeRender.VramCapGB, 1, Math.Min(24, (int)Math.Round(SafeRender.TotalVramGB)))
                : Math.Min(8, (int)Math.Round(SafeRender.TotalVramGB)),
            StepFrequency = 1,
            TickFrequency = 1,
            IsThumbToolTipEnabled = true,
        };
        var vramVal = new TextBlock
        {
            MinWidth = 30,
            HorizontalTextAlignment = Microsoft.UI.Xaml.TextAlignment.Center,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
        };
        var vramReset = new Button
        {
            Content = "重置", FontSize = 10,
            Padding = new Microsoft.UI.Xaml.Thickness(10, 4, 10, 4),
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(vramReset, "恢复为设备最优值");
        Grid.SetColumn(vramSlider, 0);
        Grid.SetColumn(vramVal, 1);
        Grid.SetColumn(vramReset, 2);
        vramRow.Children.Add(vramSlider);
        vramRow.Children.Add(vramVal);
        vramRow.Children.Add(vramReset);
        manualPanel.Children.Add(vramRow);

        // 内存上限(滑条 + 数值 + 重置为设备最优值)
        var ramLabel = new TextBlock
        {
            Text = "内存上限:处理时最多用多少内存(GB)",
            FontSize = 11, Opacity = 0.8,
        };
        manualPanel.Children.Add(ramLabel);
        var ramRow = new Grid { ColumnSpacing = 8 };
        ramRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ramRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ramRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var ramSlider = new Slider
        {
            Minimum = 2,
            Maximum = Math.Min(64, Math.Max(4, (int)Math.Round(SafeRender.TotalRamGB))),
            Value = SafeRender.RamCapGB > 0
                ? Math.Clamp(SafeRender.RamCapGB, 2, Math.Min(64, (int)Math.Round(SafeRender.TotalRamGB)))
                : Math.Min(16, (int)Math.Round(SafeRender.TotalRamGB)),
            StepFrequency = 1,
            TickFrequency = 1,
            IsThumbToolTipEnabled = true,
        };
        var ramVal = new TextBlock
        {
            MinWidth = 30,
            HorizontalTextAlignment = Microsoft.UI.Xaml.TextAlignment.Center,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
        };
        var ramReset = new Button
        {
            Content = "重置", FontSize = 10,
            Padding = new Microsoft.UI.Xaml.Thickness(10, 4, 10, 4),
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(ramReset, "恢复为设备最优值");
        Grid.SetColumn(ramSlider, 0);
        Grid.SetColumn(ramVal, 1);
        Grid.SetColumn(ramReset, 2);
        ramRow.Children.Add(ramSlider);
        ramRow.Children.Add(ramVal);
        ramRow.Children.Add(ramReset);
        manualPanel.Children.Add(ramRow);

        // 手动:CPU 占用 —— 这是"引擎自己的线程量"(单实例吃多少核),与下方「限制总 CPU 占用」(总%硬上限)是两回事
        var cpuLabel = new TextBlock
        {
            Text = "引擎线程量(单实例吃多少核)",
            FontSize = 11, Opacity = 0.8,
        };
        ToolTipService.SetToolTip(cpuLabel,
            "引擎自己的计算线程数:低=1 线程、中≈半核封顶 4、高≈半核封顶 8。控制的是「单个引擎实例用多少核」;想限制所有引擎+ffmpeg 的【总】CPU 百分比,请用下方「限制总 CPU 占用」(那是 Windows Job 硬上限)。");
        manualPanel.Children.Add(cpuLabel);
        var cpuRadios = new RadioButtons
        {
            SelectedIndex = SafeRender.CpuLevel > 0 ? SafeRender.CpuLevel - 1 : 1,
        };
        cpuRadios.Items.Add(new RadioButton { Content = "低" });
        cpuRadios.Items.Add(new RadioButton { Content = "中" });
        cpuRadios.Items.Add(new RadioButton { Content = "高" });
        manualPanel.Children.Add(cpuRadios);

        content.Children.Add(manualPanel);

        // 资源保护黄字(声明提前:lowPriCheck 事件会刷新它)——专属「系统流畅优先」的提示:
        // 勾选显示,取消消失。"限制总 CPU 占用"是强制开启的固定功能,不占用这个提示。
        var resHint = new TextBlock
        {
            Text = "已开启系统流畅优先:处理速度可能略降,但电脑更不容易卡顿。",
            FontSize = 11, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE8, 0xA3, 0x3D)),
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap, Visibility = Microsoft.UI.Xaml.Visibility.Collapsed,
        };
        void RefreshResHint()
            => resHint.Visibility = SafeRender.LowPriorityEnabled
                ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

        // 处理时降优先级开关(防整机卡;默认开)
        var lowPriCheck = new CheckBox
        {
            Content = "系统流畅优先 (建议开启)",
            FontSize = 12,
            IsChecked = SafeRender.LowPriorityEnabled,
        };
        ToolTipService.SetToolTip(lowPriCheck,
            "处理时把计算进程设为「低于正常」优先级,并预留 1~2 个 CPU 核心给系统/前台软件(处理器亲和性),线程数也相应收紧:即使 CPU 满载,浏览器和其他软件也不卡。代价是处理速度略慢。不卡电脑的现代做法");
        lowPriCheck.Checked += (_, _) => { SafeRender.LowPriorityEnabled = true; SafeRender.Save(); RefreshResHint(); };
        lowPriCheck.Unchecked += (_, _) => { SafeRender.LowPriorityEnabled = false; SafeRender.Save(); RefreshResHint(); };
        content.Children.Add(lowPriCheck);

        // ===== 资源上限保护(给其他程序留余量;3 个手动开关,默认关,觉得卡才勾)=====
        content.Children.Add(resHint);

        var limitCpuCheck = new CheckBox
        {
            Content = "限制总 CPU 占用",
            FontSize = 12, IsChecked = true, IsEnabled = false,   // 强制开启:给其他程序留余量,不允许关闭
        };
        ToolTipService.SetToolTip(limitCpuCheck,
            "把引擎和 ffmpeg 的总 CPU 占用限制在约 85%,保证其他程序至少有 15% 核可用;开启后即使多任务排队也不卡前台,速度略降。");

        // 手动模式:总 CPU 上限滑条(50~95);自动模式显示固定 85%
        var cpuCapRow = new Grid { ColumnSpacing = 8, Margin = new Microsoft.UI.Xaml.Thickness(24, 0, 0, 0), Visibility = SafeRender.LimitCpuJob ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed };
        cpuCapRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        cpuCapRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cpuCapRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        cpuCapRow.Children.Add(new TextBlock { Text = "总CPU上限", FontSize = 11, VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center });
        var cpuCapSlider = new Slider
        {
            Minimum = 50, Maximum = 95, StepFrequency = 1, Value = SafeRender.CpuCapPct, FontSize = 11,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(cpuCapSlider, "引擎+ffmpeg 的总 CPU 上限(50%~95%):给系统/前台留余量。自动模式固定 85%;切到自定义模式即可拖这条滑条(50~95)。");
        var cpuCapVal = new TextBlock { Text = $"{SafeRender.GetEffectiveCpuCapPct():0}%", MinWidth = 40, FontSize = 11, HorizontalTextAlignment = Microsoft.UI.Xaml.TextAlignment.Center, VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center };
        // 手动模式:滑条可拖、值实时显示;自动模式:锁定 85%,滑条禁用只作显示
        void RefreshCpuCap()
        {
            bool manual = SafeRender.Mode == 1;
            cpuCapSlider.IsEnabled = manual;
            cpuCapSlider.Opacity = manual ? 1.0 : 0.5;
            cpuCapVal.Text = $"{SafeRender.GetEffectiveCpuCapPct():0}%";
        }
        cpuCapSlider.ValueChanged += (_, _) =>
        {
            SafeRender.CpuCapPct = Math.Clamp(cpuCapSlider.Value, 50, 95);
            SafeRender.Save();
            cpuCapVal.Text = $"{SafeRender.GetEffectiveCpuCapPct():0}%";
        };
        Grid.SetColumn(cpuCapSlider, 1);
        Grid.SetColumn(cpuCapVal, 2);
        cpuCapRow.Children.Add(cpuCapSlider);
        cpuCapRow.Children.Add(cpuCapVal);
        RefreshCpuCap();

        limitCpuCheck.Checked += (_, _) => { SafeRender.LimitCpuJob = true; SafeRender.Save(); RefreshResHint(); cpuCapRow.Visibility = Microsoft.UI.Xaml.Visibility.Visible; };
        limitCpuCheck.Unchecked += (_, _) => { SafeRender.LimitCpuJob = true; SafeRender.Save(); RefreshResHint(); cpuCapRow.Visibility = Microsoft.UI.Xaml.Visibility.Visible; };   // 强制开:不可关
        content.Children.Add(limitCpuCheck);
        content.Children.Add(cpuCapRow);

        var splitCoresCheck = new CheckBox
        {
            Content = "引擎/ffmpeg 按可用核分线程",
            FontSize = 12, IsChecked = SafeRender.SplitCores,
        };
        ToolTipService.SetToolTip(splitCoresCheck,
            "把超分/补帧引擎线程数除以并发路数,并给每个实例分配独立核,避免多路引擎挤在同一批核上超订;同时给 ffmpeg 拆帧/编码限制线程。开启后后台占用更规整,不抢系统核。");
        splitCoresCheck.Checked += (_, _) => { SafeRender.SplitCores = true; SafeRender.Save(); RefreshResHint(); };
        splitCoresCheck.Unchecked += (_, _) => { SafeRender.SplitCores = false; SafeRender.Save(); RefreshResHint(); };
        content.Children.Add(splitCoresCheck);

        RefreshResHint();

        // 降温休息开关(可选)+ 可调间隔/时长
        var restCheck = new CheckBox
        {
            Content = "长时间处理时,按下面的间隔休息降温",
            FontSize = 12,
            IsChecked = SafeRender.RestEnabled,
        };
        content.Children.Add(restCheck);

        var restRow = new Grid { ColumnSpacing = 8 };
        restRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        restRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        restRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        restRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        restRow.Children.Add(new TextBlock { Text = "连续处理", FontSize = 12, VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center });
        var restIntervalCombo = new ComboBox { FontSize = 12 };
        restIntervalCombo.Items.Add(new ComboBoxItem { Content = "30 分钟", Tag = 30.0 });
        restIntervalCombo.Items.Add(new ComboBoxItem { Content = "1 小时", Tag = 60.0 });
        restIntervalCombo.SelectedIndex = SafeRender.RestIntervalMin >= 45 ? 1 : 0;
        restIntervalCombo.SelectionChanged += (_, _) =>
        {
            if (restIntervalCombo.SelectedItem is ComboBoxItem ci && ci.Tag is double m)
            {
                SafeRender.RestIntervalMin = m;
                SafeRender.Save();
            }
        };
        Grid.SetColumn(restIntervalCombo, 1);
        restRow.Children.Add(restIntervalCombo);
        var restDurLabel = new TextBlock { Text = "每次休息", FontSize = 12, VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center };
        Grid.SetColumn(restDurLabel, 2);
        restRow.Children.Add(restDurLabel);
        var restDurationCombo = new ComboBox { FontSize = 12 };
        foreach (var m in new[] { 10, 15, 30 })
            restDurationCombo.Items.Add(new ComboBoxItem { Content = $"{m} 分钟", Tag = m });
        restDurationCombo.SelectedIndex = SafeRender.RestDurationMin switch { 10 => 0, 30 => 2, _ => 1 };
        restDurationCombo.SelectionChanged += (_, _) =>
        {
            if (restDurationCombo.SelectedItem is ComboBoxItem ci && ci.Tag is int m)
            {
                SafeRender.RestDurationMin = m;
                SafeRender.Save();
            }
        };
        Grid.SetColumn(restDurationCombo, 3);
        restRow.Children.Add(restDurationCombo);
        content.Children.Add(restRow);

        // 温度墙开关(独立,默认关)
        var tempCheck = new CheckBox
        {
            Content = "显卡过热时自动暂停降温(超过 85°C 暂停 10 分钟)",
            FontSize = 12,
            IsChecked = SafeRender.TempWallEnabled,
        };
        // 无 N 卡(NVIDIA)时读不到温度,温度墙不会生效——开关置灰 + tooltip 说明
        bool hasNvidia = GpuInfo.GetAdapterNames().Any(n => n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));
        tempCheck.IsEnabled = hasNvidia;
        tempCheck.Opacity = hasNvidia ? 1.0 : 0.5;
        ToolTipService.SetToolTip(tempCheck, hasNvidia
            ? "N 卡温度超过 85°C 自动暂停 10 分钟,降到 70°C 提前恢复继续"
            : "未检测到 NVIDIA 显卡:本机读不到 GPU 温度,此功能不可用(AMD/Intel 显卡暂不支持温度读取)");
        content.Children.Add(tempCheck);



        // 当前生效结果
        var applyText = new TextBlock
        {
            Text = "",
            FontSize = 11, Opacity = 0.85, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
        };
        content.Children.Add(applyText);

        static string CpuName(int lv) => lv switch { 1 => "低", 2 => "中", _ => "高" };

        // 手动面板展开/收起动画:高度 0→实际高度 + 淡入淡出;
        // 展开 QuarticEase EaseOut(优雅),收回 LinearEase 匀速(EaseOut 尾部停滞是"顿"的元凶);
        // 下方内容随高度动画平滑推下/收上;状态没变不重播;新动画前停掉旧的防止竞争
        Microsoft.UI.Xaml.Media.Animation.Storyboard? lastPanelSb = null;
        void AnimatePanel(Microsoft.UI.Xaml.UIElement el, bool show)
        {
            if ((el.Visibility == Visibility.Visible) == show) return;
            var fe = el as Microsoft.UI.Xaml.FrameworkElement;
            if (fe == null) { el.Visibility = show ? Visibility.Visible : Visibility.Collapsed; return; }
            if (lastPanelSb != null) { try { lastPanelSb.Stop(); } catch { } }
            var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            lastPanelSb = sb;
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
                try { el.UpdateLayout(); } catch { }
                double target = fe.ActualHeight > 0 ? fe.ActualHeight : 80;
                fe.Height = 0;
                var ha = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                {
                    To = target, Duration = new Duration(TimeSpan.FromMilliseconds(220)),
                    EasingFunction = ease, EnableDependentAnimation = true,
                };
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(ha, fe);
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(ha, "Height");
                sb.Children.Add(ha);
                sb.Completed += (_, _) => fe.Height = double.NaN;
            }
            else
            {
                // 收起:高度动画到 0,结束后隐藏。淡出比高度收缩更快(内容先消失,末尾不"顿")
                double from = fe.ActualHeight > 0 ? fe.ActualHeight : fe.Height;
                var ha = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                {
                    From = from, To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(120)),
                    EasingFunction = ease, EnableDependentAnimation = true,
                };
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(ha, fe);
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(ha, "Height");
                sb.Children.Add(ha);
                sb.Completed += (_, _) =>
                {
                    fe.Height = double.NaN;
                    el.Visibility = Visibility.Collapsed;
                };
            }
            var oa = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                To = show ? 1 : 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(show ? 220 : 90)),
                EasingFunction = ease, EnableDependentAnimation = true,
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(oa, el);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(oa, "Opacity");
            sb.Children.Add(oa);
            sb.Begin();
        }

        void RefreshSafeRender()
        {
            SafeRender.Mode = modeRadios.SelectedIndex;
            SafeRender.RestEnabled = restCheck.IsChecked == true;
            SafeRender.TempWallEnabled = tempCheck.IsChecked == true;
            // 休息间隔/时长选择:启用休息才可选(置灰表示不生效)
            restIntervalCombo.IsEnabled = restCheck.IsChecked == true;
            restDurationCombo.IsEnabled = restCheck.IsChecked == true;
            restIntervalCombo.Opacity = restCheck.IsChecked == true ? 1.0 : 0.5;
            restDurationCombo.Opacity = restCheck.IsChecked == true ? 1.0 : 0.5;
            if (SafeRender.Mode == 1)
            {
                SafeRender.VramCapGB = (int)vramSlider.Value;
                SafeRender.RamCapGB = (int)ramSlider.Value;
                SafeRender.CpuLevel = cpuRadios.SelectedIndex + 1;
            }
            else
            {
                SafeRender.CpuLevel = 0;   // 自动:按本机重新推荐
            }
            AnimatePanel(manualPanel, SafeRender.Mode == 1);   // 展开/收起都有动画,下方内容跟着平滑移动
            vramVal.Text = vramSlider.Value.ToString("0");
            ramVal.Text = ramSlider.Value.ToString("0");
            RefreshCpuCap();   // 自动/自定义切换:CPU 上限滑条锁定 85%(自动)或恢复可拖(自定义)
            var modeTxt = SafeRender.Mode == 1 ? "" : "当前生效(自动):";
            applyText.Text = $"{modeTxt}显存墙 {SafeRender.EffectiveVramGB:0.#} GB → 分块 {SafeRender.GetTileSize()} · " +
                $"内存墙 {SafeRender.EffectiveRamGB:0.#} GB → 每批 {SafeRender.GetVideoBatchSize()} 帧 · CPU {CpuName(SafeRender.EffectiveCpuLevel)}";
            SafeRender.Save();
            AppLogger.Info($"安全渲染设置已保存:模式={(SafeRender.Mode == 0 ? "自动" : "自定义")}," +
                $"显存墙 {SafeRender.EffectiveVramGB:0.#} GB,内存墙 {SafeRender.EffectiveRamGB:0.#} GB," +
                $"CPU {SafeRender.EffectiveCpuLevel},休息={(SafeRender.RestEnabled ? $"{SafeRender.RestIntervalMin:0}分钟/{SafeRender.RestDurationMin}分钟" : "关")}," +
                $"温度墙={(SafeRender.TempWallEnabled ? "开" : "关")},后台流畅优先={(SafeRender.LowPriorityEnabled ? "开" : "关")}");
        }
        modeRadios.SelectionChanged += (_, _) => RefreshSafeRender();
        vramReset.Click += (_, _) => { vramSlider.Value = (int)Math.Round(SafeRender.TotalVramGB * 0.75); RefreshSafeRender(); };
        ramReset.Click += (_, _) => { ramSlider.Value = (int)Math.Round(SafeRender.TotalRamGB * 0.75); RefreshSafeRender(); };
        vramSlider.ValueChanged += (_, _) => { if (SafeRender.Mode == 1) RefreshSafeRender(); };
        ramSlider.ValueChanged += (_, _) => { if (SafeRender.Mode == 1) RefreshSafeRender(); };
        cpuRadios.SelectionChanged += (_, _) => { if (SafeRender.Mode == 1) RefreshSafeRender(); };
        restCheck.Checked += (_, _) => RefreshSafeRender();
        restCheck.Unchecked += (_, _) => RefreshSafeRender();
        tempCheck.Checked += (_, _) => RefreshSafeRender();
        tempCheck.Unchecked += (_, _) => RefreshSafeRender();
        RefreshSafeRender();

        // ================= 临时文件位置(处理缓存) =================
        content.Children.Add(new Border
        {
            Height = 1,
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppBorderBrush"],
        });
        content.Children.Add(new TextBlock
        {
            Text = "临时文件位置(处理缓存)",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        content.Children.Add(new TextBlock
        {
            Text = "任务完成自动清理,启动也会清理残留(只清理本软件临时文件)。",
            FontSize = 12, Opacity = 0.75, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
        });
        var tmpRadios = new RadioButtons
        {
            Items =
            {
                new RadioButton { Content = "自动(推荐,选剩余空间最大的盘)" },
                new RadioButton { Content = "指定位置" },
            },
        };
        var tmpPathText = new TextBlock { FontSize = 10, Opacity = 0.55, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap };
        var tmpPickBtn = new Button { Content = "浏览...", FontSize = 12, Padding = new Microsoft.UI.Xaml.Thickness(12, 6, 12, 6) };
        var tmpCleanBtn = new Button { Content = "立即清理残留", FontSize = 12, Padding = new Microsoft.UI.Xaml.Thickness(12, 6, 12, 6) };

        void RefreshTmpUi()
        {
            bool custom = !string.IsNullOrWhiteSpace(AppSettings.TempDir);
            tmpRadios.SelectedIndex = custom ? 1 : 0;
            tmpPathText.Text = "当前使用:" + (custom ? AppSettings.TempDir : "自动 · " + EngineService.TempRoot);
        }
        RefreshTmpUi();
        tmpRadios.SelectionChanged += (_, _) =>
        {
            // 选「自动」= 清掉自定义(选「指定位置」由「浏览...」填路径)
            if (tmpRadios.SelectedIndex == 0 && !string.IsNullOrWhiteSpace(AppSettings.TempDir))
            {
                AppSettings.TempDir = "";
                AppSettings.Save();
                RefreshTmpUi();
                AppLogger.Info("临时文件目录已恢复自动(剩余空间最大的盘)");
            }
        };
        tmpPickBtn.Click += async (_, _) =>
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FolderPicker();
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                picker.FileTypeFilter.Add("*");
                var folder = await picker.PickSingleFolderAsync();
                if (folder != null)
                {
                    AppSettings.TempDir = folder.Path;
                    AppSettings.Save();
                    RefreshTmpUi();
                    AppLogger.Info($"临时文件目录已设为: {folder.Path}");
                }
            }
            catch (Exception ex) { AppLogger.Info("选择临时目录失败: " + ex.Message); }
        };
        tmpCleanBtn.Click += async (_, _) =>
        {
            try
            {
                var (dirs, files, bytes) = App.CleanupTempDirs();
                AppLogger.Info($"已清理临时文件残留:目录 {dirs} 个、文件 {files} 个,释放 {bytes / 1048576.0:0.#} MB");
                var dlg = new ContentDialog
                {
                    Title = "清理完成",
                    Content = new TextBlock
                    {
                        Text = $"已清理:目录 {dirs} 个 · 文件 {files} 个,释放 {bytes / 1048576.0:0.#} MB\n当前临时位置:{(string.IsNullOrWhiteSpace(AppSettings.TempDir) ? "自动" : AppSettings.TempDir)}",
                        TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                    },
                    CloseButtonText = "好的",
                    XamlRoot = this.XamlRoot,
                };
                await dlg.ShowAsync();
            }
            catch { }
        };
        content.Children.Add(tmpRadios);
        content.Children.Add(tmpPathText);
        var tmpBtnRow = new StackPanel { Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal, Spacing = 8 };
        tmpBtnRow.Children.Add(tmpPickBtn);
        tmpBtnRow.Children.Add(tmpCleanBtn);
        content.Children.Add(tmpBtnRow);

        // ================= 诊断日志(放在最下面) =================
        content.Children.Add(new Border
        {
            Height = 1,
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppBorderBrush"],
        });
        content.Children.Add(new TextBlock
        {
            Text = "诊断日志",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        content.Children.Add(new TextBlock
        {
            Text = "日志会记录你的操作和报错(含原因)。出问题时,点下方「导出诊断包(zip)」把诊断包发给作者,即可快速定位。",
            FontSize = 12, Opacity = 0.75, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
        });

        // 打开按钮行
        var btnRow = new StackPanel { Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal, Spacing = 8 };
        var openBtn = new Button { Content = "打开日志文件", FontSize = 12, Padding = new Microsoft.UI.Xaml.Thickness(12, 6, 12, 6) };
        openBtn.Click += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppLogger.LogFile)
                { UseShellExecute = true });
            }
            catch { }
        };
        var openDirBtn = new Button { Content = "打开所在文件夹", FontSize = 12, Padding = new Microsoft.UI.Xaml.Thickness(12, 6, 12, 6) };
        openDirBtn.Click += (_, _) => AppLogger.OpenInExplorer();
        btnRow.Children.Add(openBtn);
        btnRow.Children.Add(openDirBtn);
        // 导出诊断包:日志 + 设备信息 + 设置,打成一个 zip——发作者即可快速定位(解决"用户不会找日志"问题)
        var exportBtn = new Button { Content = "导出诊断包(zip)", FontSize = 12, Padding = new Microsoft.UI.Xaml.Thickness(12, 6, 12, 6) };
        exportBtn.Click += async (_, _) =>
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileSavePicker();
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                picker.FileTypeChoices.Add("ZIP 压缩包", new System.Collections.Generic.List<string> { ".zip" });
                picker.SuggestedFileName = $"ALHPro_Diag_{DateTime.Now:yyyyMMdd_HHmm}";
                var file = await picker.PickSaveFileAsync();
                if (file == null) return;
                var tmpDir = System.IO.Path.Combine(ALHPro.EngineService.TempRoot, $"alh_diag_{Guid.NewGuid():N}");
                System.IO.Directory.CreateDirectory(tmpDir);
                try
                {
                    try { System.IO.File.Copy(AppLogger.LogFile, System.IO.Path.Combine(tmpDir, "diagnostic.log"), true); } catch { }
                    var info = new System.Text.StringBuilder();
                    info.AppendLine($"ALH Pro 诊断包 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    info.AppendLine($"版本: v{UpdateChecker.CurrentVersion} · 构建 {File.GetLastWriteTime(typeof(MainPage).Assembly.Location):MM-dd HH:mm}");
                    info.AppendLine($"系统: {Environment.OSVersion} · {(Environment.Is64BitOperatingSystem ? "64 位" : "32 位")}");
                    try { info.AppendLine($"硬件: {SafeRender.CpuName} · 显存 {SafeRender.TotalVramGB:0.#}GB / 内存 {SafeRender.TotalRamGB:0.#}GB"); } catch { }
                    try { foreach (var n in GpuInfo.GetAdapterNames()) info.AppendLine("GPU: " + n); } catch { }
                    try { foreach (var v in GpuInfo.GetDriverVersions()) info.AppendLine("驱动: " + v); } catch { }
                    info.AppendLine("计算设备设置: GPU " + AppSettings.GpuIndex);
                    try { info.AppendLine("Vulkan 自检报告:\n" + AppSettings.VulkanReport); } catch { }
                    info.AppendLine("临时文件目录: " + ALHPro.EngineService.TempRoot);
                    System.IO.File.WriteAllText(System.IO.Path.Combine(tmpDir, "设备信息.txt"), info.ToString());
                    // 设置文件全部带上(均为本地参数,无隐私)
                    try
                    {
                        var settingsDir = System.IO.Path.GetDirectoryName(ParaPaths.SettingsFile("app-settings.json"));
                        if (settingsDir != null && System.IO.Directory.Exists(settingsDir))
                            foreach (var s in System.IO.Directory.EnumerateFiles(settingsDir, "*.json"))
                                System.IO.File.Copy(s, System.IO.Path.Combine(tmpDir, System.IO.Path.GetFileName(s)), true);
                    }
                    catch { }
                    // 先在临时目录完整写好 zip(句柄关闭后再复制进保存位置)——避免"边写边读"导致压缩包损坏
                    var tmpZip = System.IO.Path.Combine(tmpDir, "ALHPro_Diag.zip");
                    using (var z = System.IO.Compression.ZipFile.Open(tmpZip, System.IO.Compression.ZipArchiveMode.Create))
                    {
                        foreach (var f in System.IO.Directory.EnumerateFiles(tmpDir))
                            System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(z, f, System.IO.Path.GetFileName(f));
                    }
                    System.IO.File.Copy(tmpZip, file.Path, overwrite: true);
                    var okDlg = new ContentDialog
                    {
                        Title = "诊断包已导出",
                        Content = new TextBlock
                        {
                            Text = $"已保存:\n{file.Path}\n\n把此文件发给作者即可快速定位问题。",
                            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                        },
                        CloseButtonText = "好的",
                        XamlRoot = this.XamlRoot,
                    };
                    await okDlg.ShowAsync();
                }
                finally
                {
                    try { System.IO.Directory.Delete(tmpDir, true); } catch { }
                }
            }
            catch (Exception ex) { AppLogger.Info("导出诊断包失败: " + ex.Message); }
        };
        btnRow.Children.Add(exportBtn);
        content.Children.Add(btnRow);

        // 位置 + 当前大小
        var sizeMb = AppLogger.CurrentSize / 1024.0 / 1024.0;
        content.Children.Add(new TextBlock
        {
            Text = $"位置:{AppLogger.LogFile}\n当前大小:{sizeMb:0.0} MB",
            FontSize = 10, Opacity = 0.5, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
        });

        content.Children.Add(new Border
        {
            Height = 1,
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppBorderBrush"],
        });

        // 按时间清理
        var timeClean = new CheckBox
        {
            Content = "按时间清理(只保留最近 N 天)",
            FontSize = 12,
            IsChecked = AppLogger.CleanByTime,
        };
        var daysCombo = new ComboBox { MinWidth = 90 };
        daysCombo.Items.Add(new ComboBoxItem { Content = "7 天" });
        daysCombo.Items.Add(new ComboBoxItem { Content = "14 天" });
        daysCombo.Items.Add(new ComboBoxItem { Content = "30 天" });
        daysCombo.SelectedIndex = AppLogger.KeepDays >= 30 ? 2 : (AppLogger.KeepDays >= 14 ? 1 : 0);
        var timeRow = new StackPanel { Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal, Spacing = 8, VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center };
        timeRow.Children.Add(timeClean);
        timeRow.Children.Add(daysCombo);
        content.Children.Add(timeRow);

        // 按大小清理
        var sizeClean = new CheckBox
        {
            Content = "按大小清理(超过则只留最新)",
            FontSize = 12,
            IsChecked = AppLogger.CleanBySize,
        };
        var mbCombo = new ComboBox { MinWidth = 90 };
        mbCombo.Items.Add(new ComboBoxItem { Content = "5 MB" });
        mbCombo.Items.Add(new ComboBoxItem { Content = "10 MB" });
        mbCombo.Items.Add(new ComboBoxItem { Content = "20 MB" });
        mbCombo.SelectedIndex = AppLogger.MaxSizeMb >= 20 ? 2 : (AppLogger.MaxSizeMb >= 10 ? 1 : 0);
        var sizeRow = new StackPanel { Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal, Spacing = 8, VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center };
        sizeRow.Children.Add(sizeClean);
        sizeRow.Children.Add(mbCombo);
        content.Children.Add(sizeRow);

        // 事件:变更即保存
        void ApplyAndSave()
        {
            AppLogger.CleanByTime = timeClean.IsChecked == true;
            AppLogger.CleanBySize = sizeClean.IsChecked == true;
            AppLogger.KeepDays = daysCombo.SelectedIndex == 2 ? 30 : (daysCombo.SelectedIndex == 1 ? 14 : 7);
            AppLogger.MaxSizeMb = mbCombo.SelectedIndex == 2 ? 20 : (mbCombo.SelectedIndex == 1 ? 10 : 5);
            AppLogger.SaveConfig();
        }
        timeClean.Checked += (_, _) => ApplyAndSave();
        timeClean.Unchecked += (_, _) => ApplyAndSave();
        sizeClean.Checked += (_, _) => ApplyAndSave();
        sizeClean.Unchecked += (_, _) => ApplyAndSave();
        daysCombo.SelectionChanged += (_, _) => ApplyAndSave();
        mbCombo.SelectionChanged += (_, _) => ApplyAndSave();

        // 立即清理按钮
        var cleanNow = new Button { Content = "立即清理", FontSize = 12, HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Right, Padding = new Microsoft.UI.Xaml.Thickness(12, 6, 12, 6) };
        cleanNow.Click += (_, _) =>
        {
            ApplyAndSave();
            AppLogger.Cleanup();
            AppLogger.Info("手动清理日志");
        };
        content.Children.Add(cleanNow);

        content.Children.Add(new TextBlock
        {
            Text = "两项清理相辅相成:满足任一条即触发。下次启动时自动清理。",
            FontSize = 10, Opacity = 0.5, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
        });

        ShowCardPopup(content, "设置", 560);
    }

    /// <summary>左下角「☕ 请作者喝咖啡」→ 打赏卡片弹窗(赞赏码图片 + 打赏平台链接)。</summary>
    private void CoffeeCard_Click(object sender, RoutedEventArgs e) => ShowCoffeeCard();

    /// <summary>左下角「💬 ALH Pro 社区」→ 打开爱发电电圈(官方交流社区)。</summary>
    private void Community_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "https://www.ifdian.net/group/eb504216a38e11f18b2852540025c377") { UseShellExecute = true });
        }
        catch { }
    }

    private void ShowCoffeeCard()
    {
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = "打赏完全自愿,仅代表对作者的支持与认可;不代表任何交易或回报承诺,感谢!\n\n" +
                   "⚠️ 未成年人请勿打赏;如你未满 18 岁,请先取得监护人同意。",
            FontSize = 12, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap, LineHeight = 19,
        });
        // 收款码:把图片放到「发布版\assets\coffee_qr.png」即自动显示(无需改代码)
        var qrPath = Path.Combine(AppContext.BaseDirectory, "assets", "coffee_qr.png");
        if (File.Exists(qrPath))
        {
            var qr = new Image
            {
                Width = 320, Height = 320,
                Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center,
                Source = new BitmapImage(new Uri(qrPath)),
            };
            content.Children.Add(qr);
        }
        else
        {
            content.Children.Add(new TextBlock
            {
                Text = "(收款码图片:放到发布版目录 assets\\coffee_qr.png 后自动显示)",
                FontSize = 10, Opacity = 0.45,
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center,
            });
        }
        content.Children.Add(new TextBlock
        {
            Text = "用手机扫描上图即可",
            FontSize = 10, Opacity = 0.55,
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center,
        });
        // 打赏平台:爱发电主页(官方图标+文字,点击直达)
        var ifdLink = new HyperlinkButton
        {
            NavigateUri = new Uri("https://www.ifdian.net/a/AlL666"),
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center,
        };
        var ifdLogoPath = Path.Combine(AppContext.BaseDirectory, "assets", "ifdian_logo.png");
        object ifdIcon;
        if (File.Exists(ifdLogoPath))
            ifdIcon = new Image { Width = 16, Height = 16, Source = new BitmapImage(new Uri(ifdLogoPath)) };
        else
            ifdIcon = new FontIcon { Glyph = "\uE8C7", FontSize = 15 };   // 回退:爱心
        ifdLink.Content = new StackPanel
        {
            Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                (Microsoft.UI.Xaml.UIElement)ifdIcon,
                new TextBlock { Text = "爱发电主页", FontSize = 13, VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center },
            },
        };
        content.Children.Add(ifdLink);
        ShowCardPopup(content, "☕ 请作者喝一杯咖啡", 640);
    }

    /// <summary>左下角状态栏单击 → 弹窗放大查看诊断日志(尾部)。</summary>
    private void StatusText_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        string text;
        try
        {
            text = File.Exists(AppLogger.LogFile) ? File.ReadAllText(AppLogger.LogFile) : "(暂无日志)";
        }
        catch { text = "(日志读取失败)"; }
        if (text.Length > 200000) text = text.Substring(text.Length - 200000);   // 只显示尾部,避免卡顿

        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 12,
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
            AcceptsReturn = true,
            MaxHeight = 540,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        var content = new StackPanel { Spacing = 6 };
        content.Children.Add(new TextBlock { Text = "日志内容(显示最近部分)", FontSize = 11, Opacity = 0.6 });
        content.Children.Add(box);
        ShowCardPopup(content, "诊断日志", 760);
    }

    /// <summary>居中圆角卡片弹窗(遮罩 + 标题 + 关闭按钮 + 可滚动内容)。</summary>
    private void ShowCardPopup(StackPanel content, string title, double width)
    {
        var popup = new Microsoft.UI.Xaml.Controls.Primitives.Popup { XamlRoot = this.XamlRoot };
        var overlay = new Grid
        {
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(150, 0, 0, 0)),
        };
        // 遮罩随窗口尺寸变化自适应;卡片/滚动区高度跟随窗口,窗口过小时也能滚动看全
        Border? card = null;
        ScrollViewer? scroll = null;
        Action ResizeOverlay = () =>
        {
            var w = this.ActualWidth > 0 ? this.ActualWidth : this.XamlRoot.Size.Width;
            var h = this.ActualHeight > 0 ? this.ActualHeight : this.XamlRoot.Size.Height;
            overlay.Width = w;
            overlay.Height = h;
            if (card != null) card.MaxHeight = Math.Max(280, h - 32);
            if (scroll != null) scroll.MaxHeight = Math.Max(220, h - 100);
        };
        if (_logPopup?.IsOpen == true) _logPopup.IsOpen = false;
        // 用页面 SizeChanged 跟踪窗口尺寸变化(最大化/还原/全屏时能拿到更新后的尺寸,避免遮罩盖不满)
        void OnSizeChanged(object s, SizeChangedEventArgs a) => ResizeOverlay();
        this.SizeChanged += OnSizeChanged;
        popup.Closed += (_, _) => this.SizeChanged -= OnSizeChanged;

        card = new Border
        {
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppPanelBrush"],
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppBorderBrush"],
            BorderThickness = new Microsoft.UI.Xaml.Thickness(1),
            CornerRadius = new Microsoft.UI.Xaml.CornerRadius(14),
            Width = width,
            MaxHeight = 700,
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
        };
        var cardPanel = new StackPanel();
        var header = new Grid { ColumnSpacing = 8, Margin = new Microsoft.UI.Xaml.Thickness(18, 12, 10, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
        });
        var closeBtn = new Button
        {
            Content = "✕",
            FontSize = 12,
            Padding = new Microsoft.UI.Xaml.Thickness(10, 4, 10, 4),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Microsoft.UI.Xaml.Thickness(0),
        };
        closeBtn.Click += (_, _) => popup.IsOpen = false;
        Grid.SetColumn(closeBtn, 1);
        header.Children.Add(closeBtn);
        cardPanel.Children.Add(header);
        cardPanel.Children.Add(new Border
        {
            Height = 1,
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AppBorderBrush"],
            Margin = new Microsoft.UI.Xaml.Thickness(12, 0, 12, 6),
        });
        scroll = new ScrollViewer
        {
            Content = content,
            MaxHeight = 620,
            Padding = new Microsoft.UI.Xaml.Thickness(18, 0, 22, 16),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        cardPanel.Children.Add(scroll);
        card.Child = cardPanel;
        overlay.Children.Add(card);
        ResizeOverlay();   // 首次按当前窗口尺寸设置卡片/滚动区高度
        overlay.Tapped += (_, _) => popup.IsOpen = false;
        card.Tapped += (_, args) => args.Handled = true;
        popup.Child = overlay;
        _logPopup = popup;
        popup.IsOpen = true;
    }
}
