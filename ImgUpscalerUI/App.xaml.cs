using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Navigation;
using Windows.Graphics;

namespace ALHPro
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? window;

        /// <summary>主窗口引用(供 FileOpenPicker 初始化等使用)。</summary>
        public static Window? MainWindow { get; private set; }
        private static bool _fatalDialogShown;   // 全局异常提示只弹一次(防刷屏)
        private static System.Threading.Mutex? _singleInstance;   // 单实例锁(Mutex,进程存活期间持有;退出自动释放)

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MemoryStatusEx
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

        /// <summary>启动时记录系统诊断信息(OS/CPU/内存/GPU/磁盘/App 版本),排障时一眼看到机器环境。</summary>
        private static void LogSystemDiagnostics()
        {
            try
            {
                var sb = new System.Text.StringBuilder("系统诊断: ");
                sb.Append($"App v{typeof(App).Assembly.GetName().Version} · ");
                sb.Append($"系统 {Environment.OSVersion.VersionString} ({(Environment.Is64BitOperatingSystem ? "x64" : "x86")}) · ");
                sb.Append($"CPU {Environment.ProcessorCount} 核 · ");
                try
                {
                    var mem = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
                    if (GlobalMemoryStatusEx(ref mem))
                        sb.Append($"内存 {mem.ullAvailPhys / 1073741824.0:0.0}G 空闲 / {mem.ullTotalPhys / 1073741824.0:0.0}G 总 · ");
                    else
                        sb.Append("内存 ? · ");
                }
                catch { sb.Append("内存 ? · "); }
                sb.Append($"启动目录 {Environment.CurrentDirectory}");
                AppLogger.Info(sb.ToString());

                // GPU 列表(注册表枚举,与下拉框一致)
                try
                {
                    var gpus = ALHPro.GpuInfo.GetAdapterNames();
                    for (int i = 0; i < gpus.Count; i++)
                        AppLogger.Info($"  GPU[{i}] {gpus[i]}");
                }
                catch { AppLogger.Info("  GPU 列表读取失败"); }

                // 输出目录磁盘剩余空间
                try
                {
                    var drive = Path.GetPathRoot(Environment.CurrentDirectory);
                    if (!string.IsNullOrEmpty(drive))
                    {
                        var di = new DriveInfo(drive);
                        AppLogger.Info($"  {drive} 剩余 {di.AvailableFreeSpace / 1073741824.0:0.0}G / {di.TotalSize / 1073741824.0:0.0}G");
                    }
                }
                catch { }

                // 启动兼容性守护:程序根目录若有 d3dcompiler_47.dll(旧版/第三方塞入),
                // 会覆盖系统 DLL → 启动报"无法定位程序输入点 _std_parallel_algorithms_hw_threads"
                // (本软件有意不随包分发此 DLL,系统自带新版;残留旧文件需删除)
                try
                {
                    var oldDx = Path.Combine(AppContext.BaseDirectory, "d3dcompiler_47.dll");
                    if (File.Exists(oldDx))
                        AppLogger.Warn("⚠ 检测到程序目录存在 d3dcompiler_47.dll(该 DLL 由系统自带最新版,程序目录的旧文件会冲突,导致部分机器启动报'无法定位程序输入点')——建议删除此文件后重启程序(本软件无需此文件)");
                }
                catch { }
            }
            catch (Exception ex) { AppLogger.Error("系统诊断记录失败", ex); }
        }

        /// <summary>活跃子进程注册表:应用退出时统一杀掉,防止处理中的引擎变成孤儿进程。</summary>
        public static class ActiveProcesses
        {
            private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, System.Diagnostics.Process> Procs = new();
            public static void Register(System.Diagnostics.Process p)
            {
                try { Procs[p.Id] = p; } catch { }
                // 开关1(资源上限保护):把子进程分配进 CPU 限制 Job(失败静默——进程可能在其它 Job)
                try { SafeRender.AssignToCpuJob(p.Handle); } catch { }
            }
            public static void Unregister(int id)
            {
                Procs.TryRemove(id, out _);
            }
            /// <summary>当前所有存活子进程快照(供"暂停=冻结/恢复=解冻"遍历全部,支持多路并发)。</summary>
            public static System.Collections.Generic.IReadOnlyList<System.Diagnostics.Process> Snapshot()
            {
                try { return Procs.Values.Where(v => !v.HasExited).ToList(); } catch { return Array.Empty<System.Diagnostics.Process>(); }
            }
            public static void KillAll()
            {
                foreach (var kv in Procs)
                {
                    try { if (!kv.Value.HasExited) kv.Value.Kill(entireProcessTree: true); } catch { }
                }
                Procs.Clear();
            }
        }

        /// <summary>清理临时文件残留(系统 %TEMP% 与用户自定义临时目录,imgup_*/alh_* 前缀,绝不碰用户文件)。
        /// 返回 (删除目录数, 删除文件数, 释放字节数),供"立即清理"反馈。</summary>
        internal static (int dirs, int files, long bytes) CleanupTempDirs()
        {
            int dirs = 0, files = 0;
            long bytes = 0;
            var roots = new System.Collections.Generic.List<string> { Path.GetTempPath() };
            try { var cfg = AppSettings.TempDir; if (!string.IsNullOrWhiteSpace(cfg) && Directory.Exists(cfg)) roots.Add(cfg); } catch { }
            foreach (var root in roots)
            {
                try
                {
                    foreach (var d in Directory.EnumerateDirectories(root, "imgup*"))
                    {
                        try { dirs++; bytes += DirSize(d); Directory.Delete(d, true); } catch { /* 占用中忽略,下次再清 */ }
                    }
                    foreach (var d in Directory.EnumerateDirectories(root, "alh_*"))
                    {
                        try { dirs++; bytes += DirSize(d); Directory.Delete(d, true); } catch { /* 占用中忽略,下次再清 */ }
                    }
                    foreach (var f in Directory.EnumerateFiles(root, "alh_*.wav"))
                    {
                        try { var fi = new FileInfo(f); bytes += fi.Length; File.Delete(f); files++; } catch { }
                    }
                    foreach (var f in Directory.EnumerateFiles(root, ".alh_pro_w.tmp"))
                    {
                        try { var fi = new FileInfo(f); bytes += fi.Length; File.Delete(f); files++; } catch { }
                    }
                }
                catch { }
            }
            return (dirs, files, bytes);
        }

        private static long DirSize(string dir)
        {
            long sum = 0;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { sum += new FileInfo(f).Length; } catch { }
                }
            }
            catch { }
            return sum;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        // ===== 窗口状态记忆(WinUIEx 同款方案):WINDOWPLACEMENT 原样保存/恢复 =====
        // 布局:记录每个显示器矩形做"指纹",恢复时显示器布局变了就拒绝恢复(防止窗口跑到已拔掉的屏上);
        // 保存:AppWindow.Changed 防抖后 GetWindowPlacement;关闭时用内存缓存兜底(绝不读销毁中的 AppWindow)。
        private struct WP_POINT { public int X, Y; }
        private struct WP_RECT { public int L, T, R, B; }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPLACEMENT
        {
            public int length;
            public int flags;
            public int showCmd;
            public WP_POINT ptMinPosition;
            public WP_POINT ptMaxPosition;
            public WP_RECT rcNormalPosition;
        }

        [DllImport("user32.dll")]
        private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);   // 单实例:把已运行窗口调回前台

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);   // 单实例:最小化还原

        private static void Win32_SafeShowWindow(IntPtr hWnd)
        {
            try { ShowWindow(hWnd, 9 /*SW_RESTORE*/); } catch { }
        }

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref WP_RECT lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        // WM_SHOWWINDOW 钩子(与 WinUIEx 同款):窗口显示流程开始的瞬间应用保存的窗口状态。
        // 实测:Activate 前 SetWindowPlacement 会被系统显示流程重排(窗口被居中),而 WM_SHOWWINDOW
        // 时机正好在窗口可见之前,应用后窗口一呈现就是正确位置/大小/最大化状态。
        private delegate IntPtr WindowSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam,
            IntPtr uIdSubclass, IntPtr dwRefData);

        [DllImport("comctl32.dll")]
        private static extern bool SetWindowSubclass(IntPtr hWnd, WindowSubclassProc pfnSubclass, IntPtr uIdSubclass, IntPtr dwRefData);

        [DllImport("comctl32.dll")]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        private const uint WM_SHOWWINDOW = 0x0018;

        private static WindowSubclassProc? _stateSubclass;   // 持有委托引用,防 GC 回收
        private static bool _windowStateApplied = false;

        /// <summary>subclass:窗口首次显示瞬间恢复保存的状态(仅一次)。</summary>
        private static IntPtr WindowStateSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam,
            IntPtr uIdSubclass, IntPtr dwRefData)
        {
            try
            {
                if (uMsg == WM_SHOWWINDOW && wParam == 1 && !_windowStateApplied)
                {
                    _windowStateApplied = true;
                    RestoreWindowStateAt(hWnd);
                }
            }
            catch { }
            return DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        /// <summary>当前显示器布局指纹(每个显示器的物理矩形)。</summary>
        private static List<(int L, int T, int R, int B)> GetMonitorFingerprint()
        {
            var list = new List<(int, int, int, int)>();
            try
            {
                MonitorEnumProc cb = (IntPtr _, IntPtr _, ref WP_RECT r, IntPtr _) => { list.Add((r.L, r.T, r.R, r.B)); return true; };
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
            }
            catch { }
            return list;
        }

        /// <summary>窗口圆角(仅 Windows 11 支持,Windows 10 静默无效)。</summary>
        private static void ApplyRoundedCorners(IntPtr hwnd)
        {
            try
            {
                // DWMWA_WINDOW_CORNER_PREFERENCE = 33;DWMWCP_ROUND = 2
                int pref = 2;
                DwmSetWindowAttribute(hwnd, 33, ref pref, sizeof(int));
            }
            catch { /* Win10 或旧系统不支持,忽略 */ }
        }

        public App()
        {
            this.InitializeComponent();
            // 全局未处理异常:记录到诊断日志并阻止崩溃(便于定位问题)。
            // 关键:窗口已就绪时给用户一个可见提示(否则"空白窗继续跑"用户不知发生什么)。
            UnhandledException += (_, e) =>
            {
                try
                {
                    var ex = e.Exception;
                    var extra = "";
                    try { extra = $" HRESULT=0x{ex.HResult:X8}"; } catch { }
                    AppLogger.Error($"未处理异常{extra}", ex);
                    if (!_fatalDialogShown)
                    {
                        _fatalDialogShown = true;
                        if (MainWindow?.Content is Microsoft.UI.Xaml.FrameworkElement fe && fe.XamlRoot != null)
                        {
                            _ = fe.DispatcherQueue.TryEnqueue(async () =>
                            {
                                try
                                {
                                    await new Microsoft.UI.Xaml.Controls.ContentDialog
                                    {
                                        Title = "程序遇到问题",
                                        Content = $"运行中发生异常:\n{ex.Message}\n\n已记录到诊断日志(设置 → 诊断日志),可导出诊断包反馈。",
                                        CloseButtonText = "知道了",
                                        XamlRoot = fe.XamlRoot,
                                    }.ShowAsync();
                                }
                                catch { }
                            });
                        }
                    }
                }
                catch { }
                e.Handled = true;
            };
            // 后台线程(Task.Run)异常也记录(不崩溃)
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                try
                {
                    AppLogger.Error("后台线程未处理异常", e.ExceptionObject as Exception);
                }
                catch { }
            };
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                try
                {
                    AppLogger.Error("任务未观察异常", e.Exception);
                }
                catch { }
                e.SetObserved();
            };
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            // ===== 单实例锁定(Mutex):只允许一个 ALH Pro 运行 =====
            // 第二次启动:尝试已有窗口(置前),本进程退出——杜绝多实例并存互相覆盖设置文件
            // (那正是"图片格式/码率记不住"的元凶;多个旧实例持续用默认值写盘)。
            bool createdNew;
            _singleInstance = new System.Threading.Mutex(true, "ALHPro_SingleInstance_Mutex", out createdNew);
            if (!createdNew)
            {
                try
                {
                    // 找到已运行窗口:置前(设置前台 + 最小化还原)
                    foreach (var p in System.Diagnostics.Process.GetProcessesByName("ALHPro"))
                    {
                        if (p.MainWindowHandle != IntPtr.Zero)
                        {
                            Win32_SafeShowWindow(p.MainWindowHandle);
                            SetForegroundWindow(p.MainWindowHandle);
                            break;
                        }
                    }
                }
                catch { }
                Environment.Exit(0);
                return;
            }
            // ===== 快速显示窗口:仅做窗口创建必需的同步工作,其余挪到后台/Activate 后 =====
            // (启动黑屏 1 秒的根源:窗口 Activate 前做了大量同步任务——日志/自检/清理/图标等,
            //  全部完成后才首帧渲染。下面只保留"显示窗口所必需"的,其余延迟执行。)
            AppLogger.LoadConfig();
            AppLogger.EnsureUtf8Bom();   // 旧日志转 UTF-8 BOM,修复中文乱码
            AppLogger.Info("========== 应用启动 ==========");

            window ??= new Window();
            // 系统标题栏(不扩展内容区):窗口由系统立刻绘制 → 无黑屏(流畅)。
            // 标题栏颜色:Win11 支持对系统标题栏着色(背景/按钮同深色),尽力设置,失败无碍。
            window.ExtendsContentIntoTitleBar = false;
            window.Title = "ALH Pro v" + UpdateChecker.CurrentVersion;
            MainWindow = window;

            // 关闭窗口时:杀掉所有处理子进程(防止引擎孤儿)+ 清理裁剪/转码/临时文件
            window.Closed += (_, _) =>
            {
                AppLogger.Info("========== 应用退出 ==========");
                EngineService.CleanupTempFiles();   // 清理本次运行注册的临时文件
                ActiveProcesses.KillAll();
                CroppedStorage.Clean();
                CleanupTempDirs();
            };

            // 快速显示窗口(窗口出现在屏幕上,后台再完成慢任务)
            if (window.Content is not Frame rootFrame)
            {
                rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed;
                window.Content = rootFrame;
            }
            // ===== 窗口一次到位 → 显示(不先显示再跳位,避免"两个窗口/闪跳/乱置顶") =====
            // 恢复上次窗口大小/位置/最大化:挂在 WM_SHOWWINDOW 钩子上(与 WinUIEx 同款)——窗口显示流程
            // 开始的瞬间用 Win32 SetWindowPlacement 应用,WINDOWPLACEMENT 原生含最大化标志 + 还原矩形。
            // (实测:Activate 前应用会被系统显示流程重排,钩子时机才可靠。)
            try
            {
                _stateSubclass ??= WindowStateSubclassProc;
                SetWindowSubclass(WinRT.Interop.WindowNative.GetWindowHandle(window), _stateSubclass, new IntPtr(1), IntPtr.Zero);
            }
            catch { }
            // 页面(含视图)构造完成后再 Activate:窗口一出现就是完整内容
            _ = rootFrame.Navigate(typeof(MainPage), e.Arguments);
            window.Activate();
            // ===== Activate 后的非关键启动任务:纯 IO 后台并行(日志清理/自检/临时文件清理) =====
            _ = System.Threading.Tasks.Task.Run((System.Action)(() =>
            {
                try { AppLogger.Cleanup(); } catch { }
                try { LogSystemDiagnostics(); } catch { }
                try { EngineService.CleanupTempFiles(); } catch { }   // 清理上次遗留临时文件
                try { VulkanCheck.LoadOrRun(); } catch { }             // Vulkan 自检(首次)
                try { CroppedStorage.Clean(); } catch { }              // 清理历史裁剪临时文件
            }));

            // 窗口外观(圆角/图标/标题栏深色):很快,Activate 后同步设置(不卡首帧)
            try { ApplyRoundedCorners(WinRT.Interop.WindowNative.GetWindowHandle(window)); } catch { }
            try
            {
                var iconPath = Path.Combine(AppContext.BaseDirectory, "ALHPro.ico");
                if (File.Exists(iconPath)) window.AppWindow.SetIcon(iconPath);
            }
            catch { }
            try
            {
                var tb = window.AppWindow.TitleBar;
                var bg = Windows.UI.Color.FromArgb(255, 20, 22, 27);      // #14161B
                var btnBg = Windows.UI.Color.FromArgb(255, 20, 22, 27);
                var fg = Windows.UI.Color.FromArgb(255, 232, 236, 242);   // #E8ECF2
                tb.BackgroundColor = bg;
                tb.ForegroundColor = fg;
                tb.InactiveBackgroundColor = bg;
                tb.InactiveForegroundColor = fg;
                tb.ButtonBackgroundColor = btnBg;
                tb.ButtonForegroundColor = fg;
                tb.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(255, 45, 48, 58);
                tb.ButtonHoverForegroundColor = fg;
                tb.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(255, 52, 55, 66);
                tb.ButtonPressedForegroundColor = fg;
                tb.ButtonInactiveBackgroundColor = btnBg;
                tb.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(255, 180, 186, 198);
                // 关键:强制深色主题(系统默认主题可能是浅色 → 最小化/快照动画期间系统用浅色标题栏→ 顶部变白)
                // PreferredTheme=Dark 告诉系统"这是深色应用",最小化动画也用深色标题栏。
                try { window.AppWindow.TitleBar.PreferredTheme = Microsoft.UI.Windowing.TitleBarTheme.Dark; } catch { }
                // 应用级主题也强制深色(与背景/正文一致)
                try { ((FrameworkElement)window.Content).RequestedTheme = ElementTheme.Dark; } catch { }
            }
            catch { }

            // ===== 窗口状态保存(WinUIEx 同款):Changed → 防抖 → GetWindowPlacement 缓存 + 落盘 =====
            // 注意:绝不在 Closed 里读 AppWindow.Size/Position——窗口销毁期间它返回异常值
            // (实测把正确位置覆盖成垃圾坐标,这正是"记不住位置"的根因)。
            _persistTimer = window.DispatcherQueue.CreateTimer();
            _persistTimer.Interval = TimeSpan.FromMilliseconds(500);
            _persistTimer.IsRepeating = false;
            _persistTimer.Tick += (_, _) => { try { SnapshotAndSaveWindowState(); } catch { } };

            window.AppWindow.Changed += (_, args) =>
            {
                if (args.DidPositionChange || args.DidSizeChange || args.DidPresenterChange)
                {
                    _persistTimer.Stop();
                    _persistTimer.Start();   // 防抖:连续拖动只写一次
                }
            };
            window.Closed += (_, _) =>
            {
                // 只落盘防抖缓存的最近状态,绝不能在关闭时实时读窗口——窗口销毁流程(还原→挪位→最小化)
                // 会把 showCmd 和还原矩形污染成"退场值",这正是最大化恢复失效的原因。
                try { SaveWindowStateOnClose(); }
                catch { }
            };
        }

        // ==================== 窗口状态记忆(WinUIEx 同款方案) ====================

        /// <summary>防抖定时器:窗口位置/尺寸/最大化变化后 500ms 才落盘,拖动不产生连续 IO。</summary>
        private static Microsoft.UI.Dispatching.DispatcherQueueTimer? _persistTimer;
        /// <summary>最近一次取到的窗口放置(关闭时兜底落盘用,不读销毁中的 AppWindow)。</summary>
        private static WINDOWPLACEMENT _lastPlacement;
        private static bool _hasPlacement = false;

        /// <summary>窗口状态文件(JSON):{ "monitors":[[l,t,r,b],...], "placement":"base64(WINDOWPLACEMENT)" }。</summary>
        private static string StateFile => ParaPaths.SettingsFile("window-state.json");
        /// <summary>窗口显示瞬间恢复(WM_SHOWWINDOW 钩子回调):显示器布局与保存时一致才恢复;
        /// 不一致(拔了屏/分辨率变了)则跳过,窗口用系统默认——避免跑到已拔掉的屏上。</summary>
        private static void RestoreWindowStateAt(IntPtr hwnd)
        {
            try
            {
                if (!File.Exists(StateFile)) return;
                var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(StateFile));
                if (!json.RootElement.TryGetProperty("monitors", out var mEl) ||
                    !json.RootElement.TryGetProperty("placement", out var pEl)) return;

                List<(int L, int T, int R, int B)> saved = new();
                foreach (var mon in mEl.EnumerateArray())
                    saved.Add((mon[0].GetInt32(), mon[1].GetInt32(), mon[2].GetInt32(), mon[3].GetInt32()));

                // 显示器指纹:数量或任一矩形变化 → 不恢复(窗口可能跑到已拔掉的屏上)
                var now = GetMonitorFingerprint();
                if (now.Count != saved.Count) { AppLogger.Info($"窗口状态: 显示器布局变化({now.Count} vs {saved.Count}),不恢复"); return; }
                for (int i = 0; i < saved.Count; i++)
                {
                    if (now[i] != saved[i]) { AppLogger.Info("窗口状态: 显示器布局变化,不恢复"); return; }
                }

                var placementBytes = Convert.FromBase64String(pEl.GetString()!);
                IntPtr buf = Marshal.AllocHGlobal(placementBytes.Length);
                try
                {
                    Marshal.Copy(placementBytes, 0, buf, placementBytes.Length);
                    var wp = (WINDOWPLACEMENT)Marshal.PtrToStructure(buf, typeof(WINDOWPLACEMENT))!;

                    // 只接受 最大化/普通 两种(照 WinUIEx:最小化的"还原到最大化"统一还原为最大化)
                    const int SW_SHOWNORMAL = 1, SW_SHOWMAXIMIZED = 3;
                    const uint WPF_RESTORETOMAXIMIZED = 0x2;
                    if (wp.showCmd == 2 /*SW_SHOWMINIMIZED*/ && (wp.flags & (int)WPF_RESTORETOMAXIMIZED) != 0)
                        wp.showCmd = SW_SHOWMAXIMIZED;
                    else if (wp.showCmd != SW_SHOWMAXIMIZED)
                        wp.showCmd = SW_SHOWNORMAL;

                    wp.length = Marshal.SizeOf<WINDOWPLACEMENT>();
                    bool ok = SetWindowPlacement(hwnd, ref wp);
                    _lastPlacement = wp; _hasPlacement = true;
                    AppLogger.Info($"窗口状态: 恢复 {wp.rcNormalPosition.L},{wp.rcNormalPosition.T} {wp.rcNormalPosition.R - wp.rcNormalPosition.L}x{wp.rcNormalPosition.B - wp.rcNormalPosition.T} showCmd={wp.showCmd} => {ok}");
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch (Exception ex) { AppLogger.Info("窗口状态: 恢复失败: " + ex.Message); }
        }

        /// <summary>防抖定时器落盘:实时取当前窗口放置(更新缓存)+ 显示器指纹,写入文件。</summary>
        private static void SnapshotAndSaveWindowState()
        {
            if (MainWindow is null) return;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow);
            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (GetWindowPlacement(hwnd, ref wp))
            {
                _lastPlacement = wp; _hasPlacement = true;
            }
            WriteWindowStateFile();
        }

        /// <summary>关闭兜底:只用防抖缓存的最近状态落盘,不实时读窗口(关闭流程会污染 showCmd/位置)。</summary>
        private static void SaveWindowStateOnClose()
        {
            if (_hasPlacement) WriteWindowStateFile();
        }

        /// <summary>把缓存的窗口放置 + 显示器布局指纹写入状态文件。</summary>
        private static void WriteWindowStateFile()
        {
            if (!_hasPlacement) return;
            var json = new System.Text.StringBuilder("{\"monitors\":[");
            var fingers = GetMonitorFingerprint();
            for (int i = 0; i < fingers.Count; i++)
            {
                if (i > 0) json.Append(',');
                json.Append($"[{fingers[i].L},{fingers[i].T},{fingers[i].R},{fingers[i].B}]");
            }
            int size = Marshal.SizeOf<WINDOWPLACEMENT>();
            byte[] bytes = new byte[size];
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(_lastPlacement, buf, false);
                Marshal.Copy(buf, bytes, 0, size);
            }
            finally { Marshal.FreeHGlobal(buf); }
            json.Append("],\"placement\":\"" + Convert.ToBase64String(bytes) + "\"}");

            var dir = Path.GetDirectoryName(StateFile)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(StateFile, json.ToString());
        }

        void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            // 页面加载失败(XAML 解析/构造异常,如之前视频页 ContextFlyout 崩溃):
            // 记录完整堆栈,不裸抛(裸抛=直接闪退,用户毫无头绪)
            try { AppLogger.Error($"页面加载失败:{e.SourcePageType?.FullName ?? "?"} HRESULT=0x{e.Exception?.HResult:X8}", e.Exception); } catch { }
            var root = MainWindow?.Content?.XamlRoot;   // Navigate 可能在 Activate 前执行,XamlRoot 可能为 null
            if (root == null)
            {
                try
                {
                    AppLogger.Warn($"页面加载失败(窗口未就绪,未弹窗提示,仅日志):{e.SourcePageType?.FullName}");
                }
                catch { }
                return;
            }
            try
            {
                var dlg = new Microsoft.UI.Xaml.Controls.ContentDialog
                {
                    Title = "页面加载失败",
                    Content = $"无法加载页面 {e.SourcePageType?.FullName}\n已记录到诊断日志,请重启应用重试。\n\n{e.Exception?.Message}",
                    CloseButtonText = "确定",
                    XamlRoot = root,
                };
                _ = dlg.ShowAsync();
            }
            catch { }
        }
    }
}
