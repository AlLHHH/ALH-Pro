// VulkanCheck.cs — 首次启动后台自检:实测引擎能否用 GPU(Vulkan)加速。
// 方法:拿 waifu2x 引擎跑一张 1×1 测试图(设备 -g 0),能出图 = GPU Vulkan 可用;
// 顺带解析引擎启动时打印的 Vulkan 设备列表(名称),生成给用户看的友好报告。
// 结果缓存到 AppSettings(报告文本),只在首次启动执行一次,之后直接读缓存。
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ALHPro;

public static class VulkanCheck
{
    /// <summary>是否已完成自检(进程内标记,避免重复跑)。</summary>
    public static bool Done { get; private set; }

    /// <summary>检测结果:是否有可用的 GPU(Vulkan)。</summary>
    public static bool GpuAvailable { get; private set; }

    /// <summary>友好报告(给用户看:当前设备 + 会有什么问题 + 建议)。</summary>
    public static string Report { get; private set; } = "";

    /// <summary>引擎实际枚举到的 Vulkan 设备(编号+名称)——与注册表顺序可能不同,是"真实序号"。
    /// MainPage 启动自检用它自动纠正计算设备编号。</summary>
    public static System.Collections.Generic.List<(int Id, string Name)> Devices { get; } = new();

    /// <summary>找 waifu2x 引擎路径(与 EngineService 同一目录布局)。</summary>
    private static string? FindWaifu2x()
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "engines", "waifu2x");
            if (!Directory.Exists(dir)) return null;
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var exe = Path.Combine(sub, "waifu2x-ncnn-vulkan.exe");
                if (File.Exists(exe)) return exe;
            }
        }
        catch { }
        return null;
    }

    /// <summary>后台执行自检(启动时调用一次;结果写入 AppSettings 缓存)。</summary>
    public static void RunOnce()
    {
        if (Done) return;
        Done = true;
        try
        {
            var exe = FindWaifu2x();
            if (exe == null)
            {
                GpuAvailable = false;
                Report = BuildReport(false, new System.Collections.Generic.List<(int, string)>(), "未找到 waifu2x 引擎,无法检测 GPU 加速支持");
                Cache();
                return;
            }
            // 生成 1×1 测试图(纯色 PNG):优先 GDI+;个别系统 GDI+ 抛异常(真机:0x800A01FF
            // "A generic error occurred in GDI+")会影响测试图生成——用内置 PNG 兜底,不因此误判"无 GPU"。
            var testPng = Path.Combine(EngineService.TempRoot, $"imgup_vk_{Guid.NewGuid():N}.png");
            var outPng = Path.Combine(EngineService.TempRoot, $"imgup_vkout_{Guid.NewGuid():N}.png");
            try
            {
                try
                {
                    using (var bmp = new System.Drawing.Bitmap(1, 1))
                    {
                        bmp.SetPixel(0, 0, System.Drawing.Color.Red);
                        bmp.Save(testPng, System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
                catch
                {
                    // GDI+ 异常兜底:内置 1×1 PNG(base64,与 GDI+ 无关,引擎照样能解码)
                    AppLogger.Warn("⚠ GPU 自检:GDI+ 生成测试图失败,已用内置测试图兜底(不影响检测)");
                    File.WriteAllBytes(testPng, Convert.FromBase64String(
                        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
                }
                // 引擎启动会打印 Vulkan 设备列表(形如 "[0 NVIDIA GeForce RTX 4060 Laptop GPU]  queueC=..."),
                // 解析出引擎实际识别的 GPU(编号+名称),比注册表顺序更真实。
                // 判定关键:引擎能枚举到 Vulkan 设备 = 有 GPU 可用;不要求某个具体编号出图成功
                // (多卡/AMD 机器编号可能错位,-g 0 失败不代表没 GPU,只是没选对编号)。
                var devices = new System.Collections.Generic.List<(int Id, string Name)>();
                // 第一次 -g 0 通常就能打印全部设备列表(引擎启动即枚举,读到即返回,0.2 秒级);
                var output = RunEngine(exe, testPng, outPng, 0, out _);
                ParseDevices(output, devices);
                if (devices.Count == 0)
                {
                    // 一个都枚举不到:再试 1~3 号(多卡/编号靠后),仍无才判无 GPU。
                    // 无 GPU 机器引擎会立刻失败退出,单次探测也很快,不会卡满超时。
                    for (int i = 1; i <= 3 && devices.Count == 0; i++)
                    {
                        var o2 = RunEngine(exe, testPng, outPng, i, out _);
                        ParseDevices(o2, devices);
                    }
                }
                // 引擎枚举到 Vulkan 设备即认为有 GPU(编号对错是另一回事,用户可在设置里换)
                GpuAvailable = devices.Count > 0;
                // 【修复】把引擎真实枚举的设备表同步到静态 Devices。之前只写入局部 devices(供报告),
                // 静态 Devices 恒为空 → MainPage 的 BuildGpuLabels 回退到注册表顺序;在 AMD 核显+NVIDIA 独显
                // 双卡机上,注册表顺序与引擎真实 -g 编号颠倒(“GPU 1 · NVIDIA”实际跑核显)。同步后下拉/报告/引擎一致。
                Devices.Clear();
                if (devices.Count > 0) Devices.AddRange(devices);
                Report = BuildReport(GpuAvailable, devices, "");
            }
            finally
            {
                try { File.Delete(testPng); } catch { }
                try { File.Delete(outPng); } catch { }
            }
        }
        catch (Exception ex)
        {
            GpuAvailable = false;
            Report = BuildReport(false, new System.Collections.Generic.List<(int, string)>(), "检测过程出错: " + ex.Message);
        }
        Cache();
    }

    /// <summary>跑一次引擎;只要引擎打印出 Vulkan 设备列表(形如 "[0 NVIDIA ...]  queueC=")即成功,
    /// 立即结束进程返回(检测只需要设备枚举,不等它处理完——首次 Vulkan 初始化可能很慢)。
    /// 返回引擎 stdout/stderr 全文(含设备列表)。</summary>
    private static string RunEngine(string exe, string input, string output, int gpuId, out bool ok)
    {
        ok = false;
        var sb = new StringBuilder();
        try
        {
            try { File.Delete(output); } catch { }
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"-i \"{input}\" -o \"{output}\" -s 2 -n 0 -g {gpuId} -t 64",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? ".",
            };
            using var p = Process.Start(psi);
            if (p == null) return sb.ToString();
            // 逐行读输出;发现设备列表行(引擎启动即打印)立即结束,不等处理完
            var deviceSeen = false;
            object sync = new object();
            var outTask = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    string? line;
                    while ((line = p.StandardOutput.ReadLine()) != null)
                    {
                        lock (sync) sb.AppendLine(line);
                        if (line.Contains("queueC=", StringComparison.Ordinal))
                            lock (sync) deviceSeen = true;
                    }
                }
                catch { /* 进程被杀后流关闭,忽略 */ }
            });
            var errTask = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    string? line;
                    while ((line = p.StandardError.ReadLine()) != null)
                    {
                        lock (sync) sb.AppendLine(line);
                        if (line.Contains("queueC=", StringComparison.Ordinal))
                            lock (sync) deviceSeen = true;
                    }
                }
                catch { /* 进程被杀后流关闭,忽略 */ }
            });
            // 等设备列表出现(通常 0.2~1 秒)或超时(4 秒);出现即杀进程,不等处理完
            var deadline = DateTime.UtcNow.AddSeconds(4);
            bool seen;
            lock (sync) seen = deviceSeen;
            while (!seen && DateTime.UtcNow < deadline && !p.HasExited)
            {
                System.Threading.Thread.Sleep(40);
                lock (sync) seen = deviceSeen;
            }
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            try { outTask.Wait(1000); errTask.Wait(1000); } catch { }
            ok = deviceSeen;   // 只要枚举到设备列表 = 有 GPU(Vulkan 可用)
        }
        catch { }
        return sb.ToString();
    }

    /// <summary>从引擎输出解析 Vulkan 设备列表:"[N 名称]  queueC=..."。</summary>
    private static void ParseDevices(string output,
        System.Collections.Generic.List<(int Id, string Name)> devices)
    {
        try
        {
            var re = new Regex(@"\[(\d+)\s+([^\]]+?)\]\s+queue", RegexOptions.Compiled);
            foreach (Match m in re.Matches(output))
            {
                if (int.TryParse(m.Groups[1].Value, out var id))
                {
                    var name = m.Groups[2].Value.Trim();
                    // 加固:过滤虚拟显示适配器/远程虚拟显卡(OrayIddDriver/GameViewer/基本显示 等),
                    // 它们无 Vulkan 计算能力,某些场景引擎可能枚举到,但绝不能当作"可用的计算卡"推荐。
                    // 用与 GpuInfo 相同的虚拟设备关键字过滤,保证与注册表枚举口径一致。
                    if (name.Length > 0 && !devices.Exists(d => d.Name == name) && !GpuInfo.IsVirtual(name))
                        devices.Add((id, name));
                }
            }
        }
        catch { }
    }

    /// <summary>识别 NVIDIA 显卡架构类别(用于针对性报告,而非笼统"N卡")。
    /// 基于型号字符串判断:50系=Blackwell,40系=Ada,30系=Ampere,20/16系=Turing,
    /// GTX 6/7/8/9/10系=老GTX,Quadro/RTX A/TITAN=专业卡。未知(Tesla/其他)返回 "unknown"。</summary>
    private static string NvidiaArch(string name)
    {
        if (name.Contains("Quadro", StringComparison.OrdinalIgnoreCase)
            || name.Contains("TITAN", StringComparison.OrdinalIgnoreCase)
            || name.Contains("RTX A", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Tesla", StringComparison.OrdinalIgnoreCase)
            // 原先这里写的是 name.Contains("A[0-9]{2,4}") —— 把正则当字面量比,永远不成立,
            // A100/A40 这类数据中心卡一路落到 "unknown"(报告里什么架构都不提)。
            || Regex.IsMatch(name, @"\bA\d{2,4}\b", RegexOptions.IgnoreCase))
            return "pro";
        // Blackwell 判定复用已单测的 AlhPro.Core.GpuName,不再各写一份正则:
        // 原先的 RTX\s*5[0-9]{2} 把 "RTX 5000 Ada"/"RTX 5880 Ada" 一起吞成 50 系,
        // 于是自检报告对这些卡谎称"已自动改用 ONNX DirectML 稳定路线"——
        // 而真正决定路由的 EngineService.IsBlackwellGpu 判它们不是 50 系、照走 ncnn。
        // 报告与实际行为互相矛盾,用户照报告排查会完全跑偏。
        if (AlhPro.Core.GpuName.IsBlackwell(name)) return "blackwell";   // 50系
        // 工作站卡不一定带 Quadro/RTX A 字样("NVIDIA RTX 5000 Ada Generation" 就是),
        // 上面修掉 Blackwell 误判后它们会一路落到 "unknown"(报告里对自己的显卡一言不发)。
        // 判据:没有 GeForce 这个消费级标记 + 名字里直接写了架构代号。
        // 必须排在 Blackwell 判定之后 —— 真是 50 系的卡,"有崩溃风险"比"是专业卡"重要得多。
        if (!name.Contains("GeForce", StringComparison.OrdinalIgnoreCase)
            && Regex.IsMatch(name, @"\b(Ada|Ampere|Turing|Hopper|Pascal|Volta)\b", RegexOptions.IgnoreCase))
            return "pro";
        if (Regex.IsMatch(name, @"RTX\s*4[0-9]{2}", RegexOptions.IgnoreCase)) return "ada";         // 40系
        if (Regex.IsMatch(name, @"RTX\s*3[0-9]{2}", RegexOptions.IgnoreCase)) return "ampere";     // 30系
        if (Regex.IsMatch(name, @"RTX\s*2[0-9]{2}", RegexOptions.IgnoreCase)) return "turing";     // 20系
        if (Regex.IsMatch(name, @"GTX\s*16[0-9]{2}", RegexOptions.IgnoreCase)) return "turing";    // 16系(图灵)
        if (Regex.IsMatch(name, @"GTX\s*(6|7|8|9|10)[0-9]{2}", RegexOptions.IgnoreCase)) return "oldgtx";  // 老GTX
        if (name.Contains("GeForce", StringComparison.OrdinalIgnoreCase)) return "nvidia";          // 其他GeForce
        return "unknown";
    }

    /// <summary>识别设备类型(用于报告:独显/核显/无独显)。返回 "nvidia"|"amd"|"intel_igpu"|"intel_arc"|"other"。</summary>
    private static string CardKind(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "other";
        if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
            || name.Contains("GeForce", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Quadro", StringComparison.OrdinalIgnoreCase)
            || name.Contains("TITAN", StringComparison.OrdinalIgnoreCase))
            return "nvidia";
        if (name.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
            || name.Contains("AMD", StringComparison.OrdinalIgnoreCase))
        {
            // AMD 核显:无独显型号(Graphics 结尾/无 RX 数字)或 APU 核显(680M/780M 等)
            if (name.Contains("Radeon Graphics", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(name, @"Radeon(\(TM\))?\s*(?:[3-9]\d{2}M|1\d{2}M)", RegexOptions.IgnoreCase))
                return "amd_igpu";
            return "amd";
        }
        if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase))
        {
            if (name.Contains("Arc", StringComparison.OrdinalIgnoreCase)) return "intel_arc";
            return "intel_igpu";   // UHD/Iris/HD Graphics 一律核显
        }
        return "other";
    }

    /// <summary>判断本机【主独立显卡】驱动是否过旧(会有硬件加速不可用/性能退化)。基于注册表驱动版本
    /// (NVIDIA/AMD/Intel 都能读到)。返回 true 且 out 给出可读提示。阈值保守,避免误伤:
    /// NVIDIA:硬编(nvenc)需 ≥610.00;AMD:Vulkan 需较新;Intel Arc:需较新。</summary>
    private static bool DriverTooOld(out string hint)
    {
        hint = "";
        try
        {
            var names = GpuInfo.GetAdapterNames();
            var vers = GpuInfo.GetDriverVersions();
            if (names.Count == 0 || names.Count != vers.Count) return false;
            for (int i = 0; i < names.Count; i++)
            {
                var n = names[i];
                var v = vers[i];
                if (string.IsNullOrWhiteSpace(v)) continue;
                if (n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                {
                    // 注册表驱动形如 32.0.15.6070 → 对外 560.70。阈值保守(仅提示很旧的驱动,避免误伤正常机器):
                    // 真正会给 nvenc/ONNX 带来问题的老驱动,通常对外 < 460(如 2022 年前的 46x 系)。
                    double? branch = ParseNvidiaBranch(v);
                    if (branch.HasValue && branch.Value < 460.0)
                    {
                        hint = $"{n} 驱动 {branch.Value:0.00}";
                        return true;
                    }
                }
                else if (n.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("AMD", StringComparison.OrdinalIgnoreCase))
                {
                    // AMD 独显:老驱动 Vulkan 兼容差,低于 23.x 提示(保守,避免误报)
                    double? maj = ParseFirstSegment(v);
                    if (maj.HasValue && maj.Value < 23.0)
                    {
                        hint = $"{n} 驱动 {v}";
                        return true;
                    }
                }
            }
        }
        catch { }
        return false;
    }

    /// <summary>NVIDIA 驱动注册表版本(如 32.0.15.6070)→ 对外分支号(如 560.70)。
    /// 观察:32.0.15.6070→560.70、32.0.15.7283→572.83。规则:取第 4 段 ABCD → 对外 500+AB.CD(AB=前两位,CD=后两位)。</summary>
    private static double? ParseNvidiaBranch(string ver)
    {
        var m = Regex.Match(ver, @"(?:^|\.)(\d{4})$");
        if (!m.Success) return null;
        if (!int.TryParse(m.Groups[1].Value, out var abcd) || abcd < 1000) return null;
        // ABCD → 对外 500+AB.CD(已验证:6070→560.70,7283→572.83)
        int ab = abcd / 100, cd = abcd % 100;
        double branch = 500.0 + ab;
        double minor = cd / 100.0;
        return branch + minor;
    }

    /// <summary>取版本字符串首段数字(如 "23.3.1" → 23;AMD 老驱动判断用)。</summary>
    private static double? ParseFirstSegment(string ver)
    {
        var m = Regex.Match(ver, @"(\d+(?:\.\d+)?)");
        return m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : (double?)null;
    }

    /// <summary>判断本机是否存在"不稳定/有黑帧风险"的显卡(基于多年诊断包经验):
    /// AMD 独显(补帧间歇 1帧)、RTX 50系(Blackwell ncnn 崩)、老 GTX(6/7/8/9/10 系 Vulkan 支持不全)、纯核显(共享显存高倍易不足)。
    /// 返回 true 且 out 给出可读提示;无则 false(可能是稳的 N 卡 20/30/40 系)。</summary>
    private static bool HasRiskyGpu(out string msg)
    {
        msg = "";
        string? risky = null;   // 记录第一个命中的风险描述
        try
        {
            var names = new System.Collections.Generic.List<string>();
            try { names.AddRange(regAllNames()); } catch { }
            bool amdDedicated = false, anyIgpu = false;
            string? nvArch = null;
            foreach (var n in names)
            {
                switch (CardKind(n))
                {
                    case "amd": amdDedicated = true; break;
                    case "amd_igpu": anyIgpu = true; break;      // AMD APU 核显同样是共享内存,风险与 Intel 核显一致
                    case "intel_igpu": anyIgpu = true; break;
                }
                if (CardKind(n) == "nvidia")
                {
                    var arch = NvidiaArch(n);
                    if (arch == "blackwell") nvArch = "blackwell";
                    else if (arch == "oldgtx") nvArch = "oldgtx";
                }
            }
            if (amdDedicated) risky = "AMD 独显补帧易间歇丢帧/黑帧";
            else if (nvArch == "blackwell") risky = "RTX 50 系 ncnn 超分易黑帧";
            else if (nvArch == "oldgtx") risky = "较老 GTX 系列部分 GPU 加速不支持";
            else if (anyIgpu && !amdDedicated && !names.Any(n => CardKind(n) == "nvidia" || CardKind(n) == "amd"))
                risky = "核显(共享显存)高倍率/大图易显存不足";
            if (risky != null)
            {
                msg = risky;
                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>收集本机所有 GPU 名(引擎枚举 + 注册表),供风险判断用。</summary>
    private static System.Collections.Generic.List<string> regAllNames()
    {
        var list = new System.Collections.Generic.List<string>();
        try { foreach (var (_, n) in ALHPro.VulkanCheck.Devices) if (!list.Contains(n)) list.Add(n); } catch { }
        try { foreach (var n in GpuInfo.GetAdapterNames()) if (!list.Contains(n)) list.Add(n); } catch { }
        return list;
    }

    /// <summary>DirectML(ONNX 加速)是否可用:有 ONNX 超分/补帧模型 + DirectML 探测确认到可用设备。
    /// Vulkan 与 DirectML 是两套独立运行时;Vulkan 不可用 ≠ DirectML 不可用,故用它修正"只能 CPU"误判。
    /// 仅当 DirectML 探测【已完成且找到设备】(DmlFallbackOk>=0)才报可用,否则保守按不可用(避免误导)。</summary>
    private static bool DmlAvailable()
    {
        try
        {
            // DirectML 探测确认有可用设备(DmlFallbackOk = _dmlFirstOk,>=0 表示找到可建 DirectML 会话的设备)
            bool dmlDevice = ALHPro.EsrganOnnxService.DmlFallbackOk >= 0;
            if (!dmlDevice) return false;
            // 有 ONNX 超分/补帧模型才可能走 ONNX
            return ALHPro.EsrganOnnxService.FindModel() != null
                || ALHPro.EsrganOnnxService.FindWaifu2xModel() != null
                || ALHPro.RifeOnnxService.Available();
        }
        catch { return false; }
    }

    /// <summary>生成设备自检报告(正规书面格式,无图标):逐项说明本机 GPU/显存/内存/CPU,
    /// 末尾给「建议使用哪个设备」+「此设备可能遇到的问题」。</summary>
    private static string BuildReport(bool gpuOk, System.Collections.Generic.List<(int, string)> devices, string err)
    {
        var regNames = GpuInfo.GetAdapterNames();
        bool hasIntel = false, hasAmd = false, hasNvidia = false;
        void Mark(string n)
        {
            if (n.Contains("Intel", StringComparison.OrdinalIgnoreCase)) hasIntel = true;
            if (n.Contains("AMD", StringComparison.OrdinalIgnoreCase) || n.Contains("Radeon", StringComparison.OrdinalIgnoreCase)) hasAmd = true;
            if (n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) hasNvidia = true;
        }
        foreach (var (_, n) in devices) Mark(n);
        foreach (var n in regNames) Mark(n);

        var sb = new StringBuilder();
        sb.Append("设备自检报告").Append('\n');

        // ===== 明确结论(一行,用户一眼看懂能不能用)=====
        if (!string.IsNullOrEmpty(err))
            sb.Append("结论:❌ 检测异常:无法确定本机能否正常运行,请更新显卡驱动后重新检测,或到窗口右下角导出诊断包反馈。\n");
        else if (!gpuOk)
            sb.Append("结论:❌ 未检测到可用显卡(GPU):本机只能 CPU 软件计算,图片放大/抠图可用,视频超分与补帧会非常慢。建议:更新显卡驱动(需支持 Vulkan),或确认显卡未被禁用。\n");
        else if (DriverTooOld(out string driverHint))
            sb.Append("结论:⚠️ 本机显卡可用,但显卡驱动偏旧(").Append(driverHint).Append(")。较新的显卡/引擎特性可能不可用,若处理中出现黑屏/崩溃/硬编失败,建议更新显卡驱动到最新版。\n");
        else if (HasRiskyGpu(out string riskyGpuMsg))
            sb.Append("结论:⚠️ 当前显卡可能并不完全支持 ALH Pro 的运行(").Append(riskyGpuMsg).Append(")。部分功能(高倍率补帧/超分)可能出现黑帧/崩溃/较慢,软件已自动优先用稳定路线;若仍异常,建议更新显卡驱动、降低倍率,或改用支持的显卡。\n");
        else
            sb.Append("结论:✅ 本电脑可以运行 ALH Pro。\n");

        // 计算设备
        if (!string.IsNullOrEmpty(err))
            sb.Append("计算设备:检测过程出现异常(").Append(err).Append(")\n");
        else if (devices.Count > 0)
            sb.Append("计算设备:").Append(string.Join(" / ", devices.Select(d => $"GPU {d.Item1} · {d.Item2}"))).Append('\n');
        else if (regNames.Count > 0)
            sb.Append("计算设备:").Append(string.Join(" / ", regNames.Select((n, i) => $"GPU {i} · {n}"))).Append('\n');
        else
            sb.Append("计算设备:未检测到可用的 GPU\n");

        // ===== 双枚举并排对照(诊断关键:注册表枚举序 vs 引擎枚举序,两者编号可能错位)=====
        // 这台双卡机(AMD核显+NVIDIA独显)上,注册表顺序常与 ncnn 引擎 -g 编号相反。
        // 把这套映射 + 各卡 Blackwell 判定一起打日志,下次诊断包一眼看清"哪套编号是 NVIDIA、50 系判定是否命中"。
        try
        {
            var logSb = new System.Text.StringBuilder();
            logSb.Append("GPU 双枚举对照:注册表枚举[");
            for (int i = 0; i < regNames.Count; i++) { if (i > 0) logSb.Append(" | "); logSb.Append($"#{i} {regNames[i]}"); }
            logSb.Append("] 引擎枚举[");
            for (int i = 0; i < devices.Count; i++) { if (i > 0) logSb.Append(" | "); logSb.Append($"#{devices[i].Item1} {devices[i].Item2}"); }
            logSb.Append("] Blackwell判定:");
            var _bwNames = new System.Collections.Generic.List<string>();
            try { _bwNames.AddRange(devices.Select(d => d.Item2)); } catch { }
            try { _bwNames.AddRange(regNames); } catch { }
            logSb.Append(AlhPro.Core.GpuName.AnyIsBlackwell(_bwNames) ? "是(将走 ONNX 稳定路线)" : "否(走 ncnn-GPU)");
            AppLogger.Info(logSb.ToString());
        }
        catch { /* 对照日志失败不影响主报告 */ }

        // 显卡驱动版本(NVIDIA/AMD/Intel 都从注册表读,与显卡同序)
        try
        {
            var drv = GpuInfo.GetDriverVersions();
            var drvPairs = new System.Collections.Generic.List<string>();
            for (int i = 0; i < drv.Count && i < regNames.Count; i++)
            {
                if (drv[i].Length > 0)
                    drvPairs.Add($"{regNames[i]} 驱动 {drv[i]}");
            }
            if (drvPairs.Count > 0)
                sb.Append("显卡驱动:").Append(string.Join(" / ", drvPairs)).Append('\n');
        }
        catch { }

        // 显存 / 内存 / CPU
        try { sb.Append($"显存:{SafeRender.TotalVramGB:0.#} GB(空闲 {SafeRender.FreeVramText})\n"); } catch { sb.Append("显存:未知\n"); }
        try { sb.Append($"系统内存:{SafeRender.TotalRamGB:0.#} GB\n"); } catch { }
        try { sb.Append($"处理器:{SafeRender.CpuName}({SafeRender.CpuCoreCount} 核)\n"); } catch { }

        // 可用性
        if (!string.IsNullOrEmpty(err))
            sb.Append("可用性:GPU 加速暂不可用,建议使用 CPU(软件计算)稳妥处理\n");
        else if (gpuOk)
            sb.Append("可用性:GPU 加速可用,可正常进行图片放大、AI 抠图与视频处理\n");
        else if (DmlAvailable())
            // Vulkan 不可用但 DirectML 可用:超分/补帧走 ONNX(DirectML GPU),不是纯 CPU——纠正此前"只能 CPU"误判
            sb.Append("可用性:Vulkan GPU 不可用,但 ONNX/DirectML 可用,超分与补帧走 ONNX(DirectML GPU)仍能 GPU 加速\n");
        else
            sb.Append("可用性:仅 CPU(软件计算)可用\n");

        // 建议(推荐设备)
        if (gpuOk)
        {
            if (hasNvidia && (hasIntel || hasAmd))
                sb.Append("建议:使用独立 NVIDIA 显卡(GPU 编号请按「引擎识别」选择)处理速度最快\n");
            else if (hasAmd && hasIntel)
                sb.Append("建议:使用独立 AMD 显卡处理速度最快\n");
            else
                sb.Append("建议:使用 GPU 处理速度最快;显存较小或处理大图时,可改用 CPU 保证稳定\n");
        }
        else if (!string.IsNullOrEmpty(err))
            sb.Append("建议:使用 CPU(软件计算)处理,最稳妥;图片放大与抠图可用,视频处理会明显变慢\n");
        else
            sb.Append("建议:使用 CPU(软件计算)处理;图片放大与抠图可用,视频处理会明显变慢\n");

        // ===== 注意(按本机真实设备生成,不套模板)=====
        sb.Append("注意:");
        var notes = new System.Collections.Generic.List<string>();
        if (!string.IsNullOrEmpty(err)) notes.Add("检测异常可能由显卡驱动不支持 Vulkan 引起,请更新驱动后重新检测");
        if (!gpuOk && string.IsNullOrEmpty(err))
        {
            notes.Add("未检测到 GPU 加速,视频超分与补帧耗时会很长,建议先用小片段测试");
            notes.Add("若安装有独立显卡却显示不可用,请更新显卡驱动(需支持 Vulkan)或检查显卡是否被禁用");
        }

        // 统计本机真实卡:独显(品牌+架构)/核显
        var allNames = new System.Collections.Generic.List<string>();
        foreach (var (_, n) in devices) if (!allNames.Contains(n)) allNames.Add(n);
        foreach (var n in regNames) if (!allNames.Contains(n)) allNames.Add(n);
        bool amdDedicated = false, intelArc = false, intelIgpu = false, amdIgpu = false;
        string? nvArch = null;   // 主 N 卡架构
        foreach (var n in allNames)
        {
            switch (CardKind(n))
            {
                case "amd": amdDedicated = true; break;
                case "amd_igpu": amdIgpu = true; break;
                case "intel_arc": intelArc = true; break;
                case "intel_igpu": intelIgpu = true; break;
            }
            if (CardKind(n) == "nvidia")
            {
                var arch = NvidiaArch(n);
                // 已知架构总是覆盖;只有还没记到任何架构时才退而记 "unknown"(多卡机以认得出的那张为准)
                if (arch != "unknown" || nvArch == null) nvArch = arch;
            }
        }

        if (nvArch == "blackwell")
            notes.Add("RTX 50 系(Blackwell):ncnn-Vulkan 在新驱动上有已知崩溃风险,软件已自动改用 ONNX DirectML 稳定路线,无需手动设置");
        else if (nvArch == "ada")
            notes.Add("RTX 40 系(Ada):主流架构,驱动成熟,ncnn-Vulkan 直接加速,稳定");
        else if (nvArch == "ampere")
            notes.Add("RTX 30 系(Ampere):性能与稳定性均衡,ncnn-Vulkan 加速顺畅");
        else if (nvArch == "turing")
            notes.Add("RTX 20/16 系(Turing):支持 GPU 加速,显存较小时处理大图会自动降低分块");
        else if (nvArch == "oldgtx")
            notes.Add("较老的 NVIDIA 型号(GTX 600/700/900 系)可能不支持完整 GPU 加速,遇到报错请改用 CPU");
        else if (nvArch == "pro")
            notes.Add("专业卡(Quadro/RTX A/TITAN):GPU 加速可用,显存通常较大,适合高倍率大图");

        if (amdDedicated)
            notes.Add("AMD 独显:Vulkan 驱动差异较大,若处理中出现黑屏/崩溃会自动改用 ONNX DirectML 或 CPU,无需手动设置");
        if (amdIgpu || (intelIgpu && !amdDedicated))
            notes.Add("核显使用共享内存,处理大图或高倍率时可能显存不足,建议勾选「快速模式」或改用 CPU");
        if (intelArc)
            notes.Add("Intel Arc 独显:支持 GPU 加速,驱动较新时稳定;个别旧驱动需更新后再试");

        if (gpuOk && notes.Count == 0)
            notes.Add("若处理中出现黑屏或崩溃,可尝试更新显卡驱动,或在计算设备中选择 CPU");
        if (notes.Count == 0) notes.Add("各项功能均可正常使用");
        sb.Append(string.Join(";", notes)).Append('\n');

        // ===== 各模型在本机的兼容性(按真实路由如实展示:走 ncnn 还是 ONNX、GPU 还是 CPU)=====
        sb.Append("模型兼容性:").Append('\n');
        bool blackwell = EngineService.IsBlackwellGpu();
        bool onnxEsrgan = EngineService.ShouldUseOnnxEsrgan();   // 50系/Vulkan不可用 → ONNX(DML 加速)
        bool onnxWaifu = EngineService.ShouldUseOnnxWaifu2x();
        bool onnxRife = RifeOnnxService.Available();

        // 图片/视频超分:走 ONNX(DirectML 显卡加速)还是 ncnn(直接 GPU)还是 CPU
        string esrganPic = onnxEsrgan ? "走 ONNX DirectML(显卡加速,稳定)"
            : (gpuOk ? "ncnn-Vulkan 直接 GPU,加速,稳定" : "CPU 软算,慢但稳");
        string esrganVid = onnxEsrgan ? "走 ONNX DirectML(显卡加速,稳定)"
            : (gpuOk ? "ncnn-Vulkan GPU 加速,快速;异常自动降级" : "CPU 软算,较慢但稳");

        sb.Append("· 图片超分(Real-ESRGAN):").Append(esrganPic).Append('\n');
        sb.Append("· 视频超分(Real-ESRGAN):").Append(esrganVid).Append('\n');
        // waifu2x:仅无独显(ShouldUseOnnxWaifu2x)走 ONNX;否则一律 ncnn——新版 waifu2x 引擎本身兼容 Blackwell,
        // 故 50 系(有独显)也用 ncnn 直跑,不走 ONNX(报表此前误写"Blackwell waifu2x→ONNX",已修正)
        if (onnxWaifu)
            sb.Append("· 动漫超分(waifu2x):走 ONNX DirectML(显卡加速,稳定)\n");
        else
            sb.Append("· 动漫超分(waifu2x):").Append(gpuOk ? "ncnn-Vulkan GPU 加速,快速流畅\n" : "CPU 软算,慢但稳\n");
        // 补帧:有 ONNX 模型走 ONNX;否则 ncnn
        sb.Append("· 视频补帧(RIFE):").Append(onnxRife ? "走 ONNX DirectML(稳定,GPU 加速)\n"
            : (blackwell ? "已自动适配稳定引擎(较慢)\n"
            : (gpuOk ? "ncnn-Vulkan GPU 加速,流畅稳定\n" : "CPU 软算,较慢但稳\n")));
        // 抠图/音频:CPU 恒定(无需 GPU)
        sb.Append("· AI 抠图:").Append(gpuOk ? "CPU 计算(强制),速度快,任何显卡均稳定\n" : "CPU 计算,可用,速度一般\n");
        sb.Append("· 音频处理:CPU 计算,任何设备均稳定\n");

        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>结果写缓存(下次启动直接显示,不再重测;记录版本号,升级自动作废)。
    /// 【只缓存"检测到 GPU"】"无 GPU"这个结论可能是瞬时的(引擎首次启动慢/杀软占用 exe/驱动刚装完),
    /// 而 GpuAvailable=false 会被 OldNcnnGpuRisky 拿去把整机锁到 ONNX 路线,直到版本号变化才解锁 ——
    /// 一次偶发探测失败不该有跨会话的代价。真·无 GPU 机器重测只有几次秒级尝试,且跑在后台不阻塞启动。</summary>
    private static void Cache()
    {
        if (!GpuAvailable) return;
        try
        {
            AppSettings.VulkanReport = Report;
            AppSettings.VulkanCheckDone = true;
            AppSettings.VulkanGpuOk = true;
            AppSettings.VulkanReportVersion = UpdateChecker.CurrentVersion;
            AppSettings.Save();
        }
        catch { }
    }

    /// <summary>首次启动时读取缓存(若有),进程内直接用;没有则后台跑一次。
    /// 版本升级时自动作废旧缓存重测(修复/新增报告内容要能生效,老用户也能看到)。</summary>
    public static void LoadOrRun()
    {
        string ver = UpdateChecker.CurrentVersion;
        // 缓存版本与当前版本一致才复用;不一致(升级了)→ 重测
        if (AppSettings.VulkanCheckDone && !string.IsNullOrEmpty(AppSettings.VulkanReport)
            && (AppSettings.VulkanReportVersion == ver || AppSettings.VulkanReportVersion == ""))
        {
            Done = true;
            // 读存下来的结论,不再从报告文本反推:BuildReport 只在【注册表也查不到显卡】时才写
            // "未检测到可用的 GPU",而"引擎缺失/检测异常"两种失败写的是别的句子 →
            // 反推会把它们当成"有 GPU",下次启动直接敢跑 ncnn-Vulkan。旧版本写的缓存没有这个字段(null)→ 未知 → 重测。
            GpuAvailable = AppSettings.VulkanGpuOk == true;
            Report = AppSettings.VulkanReport;
            // 结论不是"确定有 GPU"就后台重测一次:瞬时失败(引擎还没解出来/杀软占用 exe/驱动刚装完)
            // 会把整机锁在 ONNX 路线上,而 GpuAvailable 正是 OldNcnnGpuRisky 的判据之一。
            // 先显示缓存报告(界面不空白),测出 GPU 会自动纠正;负结果不回写缓存,所以不会越测越糟。
            if (!GpuAvailable) System.Threading.Tasks.Task.Run(ReProbe);
            return;
        }
        // 后台跑,不阻塞启动
        System.Threading.Tasks.Task.Run(RunOnce);
    }

    /// <summary>重新检测(设置界面「重新检测」按钮):清缓存、重跑所有检测并更新缓存与报告。
    /// 完成后触发 Completed 事件(供界面刷新显示)。</summary>
    public static event Action? Completed;
    public static void Recheck()
    {
        AppSettings.VulkanCheckDone = false;
        AppSettings.VulkanReport = "";
        AppSettings.VulkanReportVersion = "";
        AppSettings.VulkanGpuOk = null;
        try { AppSettings.Save(); } catch { }
        Done = false;
        Devices.Clear();
        System.Threading.Tasks.Task.Run(() =>
        {
            RunOnce();
            try { Completed?.Invoke(); } catch { }
        });
    }

    /// <summary>轻量重试一次探测(Vulkan 自检偶发抽风/引擎启动慢被误判为无 GPU 时,用于确认后再降级)。
    /// 不清 AppSettings 缓存(避免覆盖用户已保存的报告/选择),仅重新枚举设备一次。</summary>
    public static void ReProbe()
    {
        try { Done = false; RunOnce(); } catch { }
    }
}
