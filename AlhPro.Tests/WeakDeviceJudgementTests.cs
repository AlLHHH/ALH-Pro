using AlhPro.Core;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【2026-10-17 · 依据用户真机诊断包(RTX 4060 Laptop / 8GB 显存 / 31.8GB 内存 / 10 核/16 线程)】
/// 用户打开视频页时,「开始前诊断」卡片写着 **「⚠ 设备配置较低(核显/小显存/内存小)」**,当场反问「我设备低？？？」。
/// 诊断日志给出了同一秒内的完整时序:
///   00:32:07 Vulkan 自检:设备表为空(缓存命中或尚未枚举),现场补一次引擎设备枚举
///   00:32:07 ⚠ 检测到设备配置较低(未检测到可用 GPU(Vulkan)),建议勾选「兼容模式」…
///   00:32:07 Vulkan 自检:补枚举成功,枚举到 1 个 Vulkan 设备
///   00:32:07 Vulkan 自检:GPU 引擎可用(Vulkan 设备枚举成功)
/// 两层根因:①旧判据 `VulkanCheck.Done &amp;&amp; !GpuAvailable` —— `Done` 在探测**进入时**就已置位,引擎还在枚举的
/// 那段时间里它是 true、`GpuAvailable` 还是 false,于是"还不知道"被读成了"确定没有 GPU";
/// ②结论被 `_weak ??=` / `_profile ??=` 缓存到进程结束,自检随后测出 GPU 也不会纠正。
/// 修法:把"有没有 GPU"落成 Core 里的三态(本文件用**可执行案例**钉住语义),UI 侧缓存改成跟着自检状态走
/// (源码契约见下面两条 Fact);措辞也从"设备配置较低"改回"设备偏弱 + 只列真的成立的理由"。
/// 这条红了 = 又有机器会在自检跑完之前被说成"设备低/没有显卡",或缓存又开始把一次早期误判留到进程结束。</summary>
public class WeakDeviceJudgementTests
{
    // 用户真机画像(诊断包实测):RTX 4060 Laptop,8GB 显存,31.8GB 内存,10 核 / 16 线程
    private const double RealVram = 8;
    private const double RealRam = 31.8;
    private const int RealCores = 10;

    // ───────────────── 一、三态语义(纯逻辑,可执行) ─────────────────

    [Fact]
    public void 自检没出结论时_好机器绝不许被判弱也不许说没有显卡()
    {
        // 没结论 = Unknown:不管是"还没测"还是"缓存里只有旧结论",都不能当成"没有 GPU"
        Assert.Equal(GpuPresence.Unknown, WeakDeviceRules.Resolve(probeConcluded: false, gpuAvailable: false));
        Assert.Equal(GpuPresence.Unknown, WeakDeviceRules.Resolve(probeConcluded: false, gpuAvailable: true));

        Assert.False(WeakDeviceRules.IsWeak(GpuPresence.Unknown, RealVram, RealRam, RealCores, onlyIntegratedGpu: false));
        string why = WeakDeviceRules.Describe(GpuPresence.Unknown, RealVram, RealRam, RealCores, onlyIntegratedGpu: false);
        Assert.Equal("", why);                              // 强机不刷屏
        Assert.DoesNotContain("未检测到可用 GPU", why);      // 这句错话就是用户看到的那一条
    }

    [Fact]
    public void 自检有结论且可用时_同一台机器照旧不弱()
    {
        Assert.Equal(GpuPresence.Usable, WeakDeviceRules.Resolve(probeConcluded: true, gpuAvailable: true));
        Assert.False(WeakDeviceRules.IsWeak(GpuPresence.Usable, RealVram, RealRam, RealCores, onlyIntegratedGpu: false));
        Assert.Equal("", WeakDeviceRules.Describe(GpuPresence.Usable, RealVram, RealRam, RealCores, onlyIntegratedGpu: false));
    }

    [Fact]
    public void 只有自检给出结论且结论是不可用时_才允许说没有可用显卡()
    {
        Assert.Equal(GpuPresence.Absent, WeakDeviceRules.Resolve(probeConcluded: true, gpuAvailable: false));
        Assert.True(WeakDeviceRules.IsWeak(GpuPresence.Absent, RealVram, RealRam, RealCores, onlyIntegratedGpu: false));
        string why = WeakDeviceRules.Describe(GpuPresence.Absent, RealVram, RealRam, RealCores, onlyIntegratedGpu: false);
        Assert.Contains("未检测到可用 GPU(Vulkan)", why);
        // IsNoGpu 只在 Absent 上为真(UI 侧"将用 CPU"那类文案的唯一开关)
        Assert.True(WeakDeviceRules.IsNoGpu(GpuPresence.Absent));
        Assert.False(WeakDeviceRules.IsNoGpu(GpuPresence.Unknown));
        Assert.False(WeakDeviceRules.IsNoGpu(GpuPresence.Usable));
    }

    // ───────────────── 二、真机/真案例:弱点判据本身照旧要准 ─────────────────

    [Fact]
    public void 没结论不等于不弱_硬件确实差时照旧判弱但绝不许甩锅给显卡()
    {
        // 那台 GTX 1050 Ti 机(诊断包实测:VRAM 4GB / 内存 7.9GB 标称 8GB / 8 核 / Vulkan **可用**)
        Assert.True(WeakDeviceRules.IsWeak(GpuPresence.Usable, 4, 7.9, 8, onlyIntegratedGpu: false));
        string why = WeakDeviceRules.Describe(GpuPresence.Usable, 4, 7.9, 8, onlyIntegratedGpu: false);
        Assert.Contains("显存仅 4GB", why);
        Assert.DoesNotContain("内存 7.9GB", why);              // 7.9 是标称 8GB 机的读数 ⇒ 归一后不算小(老代码正是在这里误报)
        Assert.DoesNotContain("未检测到可用 GPU", why);       // 它 GPU 明明可用,以前就是这样被说错的
        Assert.DoesNotContain("核心数较少", why);              // 8 核不算少

        // 自检还没结论时,弱点判据照样按即时读数走(不能因为"没测完"就把弱机器放过)
        Assert.True(WeakDeviceRules.IsWeak(GpuPresence.Unknown, 4, 7.9, 8, onlyIntegratedGpu: false));
        Assert.DoesNotContain("未检测到可用 GPU",
            WeakDeviceRules.Describe(GpuPresence.Unknown, 4, 7.9, 8, onlyIntegratedGpu: false));
    }

    [Fact]
    public void 显存与核数两条门槛各自独立生效()
    {
        Assert.True(WeakDeviceRules.IsWeak(GpuPresence.Usable, 5.9, RealRam, RealCores, onlyIntegratedGpu: false));
        Assert.False(WeakDeviceRules.IsWeak(GpuPresence.Usable, 6.0, RealRam, RealCores, onlyIntegratedGpu: false));
        Assert.True(WeakDeviceRules.IsWeak(GpuPresence.Usable, RealVram, RealRam, 4, onlyIntegratedGpu: false));
        Assert.False(WeakDeviceRules.IsWeak(GpuPresence.Usable, RealVram, RealRam, 5, onlyIntegratedGpu: false));
    }

    [Fact]
    public void 只有核显才算弱_混合本与独显本都不算()
    {
        Assert.True(WeakDeviceRules.IsWeak(GpuPresence.Usable, RealVram, RealRam, RealCores, onlyIntegratedGpu: true));
        Assert.Contains("核显(共享显存,较慢)",
            WeakDeviceRules.Describe(GpuPresence.Usable, RealVram, RealRam, RealCores, onlyIntegratedGpu: true));
        Assert.False(WeakDeviceRules.IsWeak(GpuPresence.Usable, RealVram, RealRam, RealCores, onlyIntegratedGpu: false));
    }

    [Fact]
    public void 内存门槛按标称容量算_7_9与15_9与31_797要分别归一到8_16_32()
    {
        Assert.Equal(8.0, WeakDeviceRules.NominalGb(7.9));
        Assert.Equal(16.0, WeakDeviceRules.NominalGb(15.9));
        Assert.Equal(32.0, WeakDeviceRules.NominalGb(31.797));
        Assert.Equal(8.0, WeakDeviceRules.NominalGb(7.5));      // 正好半整数 → AwayFromZero
        Assert.Equal(7.0, WeakDeviceRules.NominalGb(7.4));

        // 标称 8GB 机(实报 7.9)不该只因内存被判弱;报 7.4 的才是真的小
        Assert.False(WeakDeviceRules.IsWeak(GpuPresence.Usable, RealVram, 7.9, RealCores, onlyIntegratedGpu: false));
        Assert.True(WeakDeviceRules.IsWeak(GpuPresence.Usable, RealVram, 7.4, RealCores, onlyIntegratedGpu: false));
        Assert.Contains("内存 7.4GB",
            WeakDeviceRules.Describe(GpuPresence.Usable, RealVram, 7.4, RealCores, onlyIntegratedGpu: false));
    }

    // ───────────────── 三、接线契约(源码级) ─────────────────

    /// <summary>缓存必须跟着自检结论走:`_profile`/`_weak`/`_weakReason` 三处共用一把"缓存键",
    /// 键里必须有 `ProbeConcluded` 这一位 —— 否则自检随后测出 GPU 也纠正不了那次早期误判(用户真机就是
    /// 黄字与开始前诊断卡片一路错到进程结束)。</summary>
    [Fact]
    public void 硬件画像与弱设备结论的缓存要跟着自检状态失效()
    {
        string src = CodeOnly(ReadRepoFile("ImgUpscalerUI", "SafeRender.cs"));
        Assert.Contains("private static void RefreshVulkanState()", src);
        Assert.Contains("private static (bool Concluded, bool Available, int Devices) VulkanState()", src);
        // 缓存键含"有没有结论"这一位
        Assert.Contains("return (ALHPro.VulkanCheck.ProbeConcluded, ALHPro.VulkanCheck.GpuAvailable,", src);
        // 状态一变,三处缓存一起作废
        Assert.Contains("_profile = null;", src);
        Assert.Contains("_weak = null;", src);
        Assert.Contains("_weakReason = null;", src);
        // 三个读点都必须先过刷新(不能有谁绕过)
        Assert.Contains("public static DeviceProfile Profile { get { RefreshVulkanState(); return _profile ??= ComputeProfile(); } }", src);
        Assert.Contains("public static bool IsWeakDevice { get { RefreshVulkanState(); return _weak ??= ComputeWeakDevice(); } }", src);
        Assert.Contains("public static string WeakDeviceReason { get { RefreshVulkanState(); return _weakReason ??= ComputeWeakReason(); } }", src);
        // 分档也走三态(不是裸读 GpuAvailable)
        Assert.Contains("try { gpu = !ALHPro.VulkanCheck.NoGpuDetected; } catch { }", src);
    }

    /// <summary>"自检没跑完 ≠ 没有 GPU"这件事的判据本体:`ProbeConcluded` 进入探测时清零、**任何出口**
    /// (引擎缺失的提前 return / 正常结束 / 探测异常)都在 finally 里置位;三处重探(补枚举 / 重新检测 /
    /// 轻量重试)都要先清零,否则重探期间又会露出"没结论"的窗口。</summary>
    [Fact]
    public void 三处重探都要先清结论位_任何出口都要把结论位置位()
    {
        string vk = CodeOnly(ReadRepoFile("ImgUpscalerUI", "VulkanCheck.cs"));
        Assert.Contains("public static bool ProbeConcluded { get; private set; }", vk);
        Assert.Contains("public static bool NoGpuDetected => ProbeConcluded && !GpuAvailable;", vk);
        Assert.Contains("ProbeConcluded = false;", vk);
        Assert.Contains("finally { ProbeConcluded = true; }", vk);
        Assert.Contains("try { RunOnceCoreInner(); }", vk);
        // 补枚举(EnsureEnumerated)与轻量重试(ReProbe)各一处单行写法;Recheck 单独一处
        Assert.Equal(2, Count(vk, "try { Done = false; ProbeConcluded = false; RunOnce(); } catch { }"));
        Assert.Contains("ProbeConcluded = false;\n        Devices.Clear();", vk);
    }

    /// <summary>界面文案不许再"三类猜测一起报":旧句硬编码「(核显/小显存/内存小)」,而用户真机是
    /// 8GB 显存的独显本、31.8GB 内存、10 核 —— 三个猜测一个都不成立。现在只报真的成立的理由。</summary>
    [Fact]
    public void 界面只报真的成立的理由_不许把三类猜测写死进文案()
    {
        string vv = CodeOnly(ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs"));
        Assert.DoesNotContain("核显/小显存/内存小", vv);      // 旧硬编码
        Assert.DoesNotContain("检测到设备配置较低", vv);       // 旧口径
        Assert.Contains("检测到设备偏弱", vv);
        Assert.Contains("string weakWhy = SafeRender.WeakDeviceReason;", vv);
        Assert.Contains("⚠ 设备偏弱(", vv);
        // 补帧兼容性预警里那句"Vulkan 不可用"也要走三态
        Assert.Contains("try { noVulkan = VulkanCheck.NoGpuDetected; } catch { noVulkan = false; }", vv);
    }

    /// <summary>引擎侧两处"没测完就当没有 GPU"的启发式(按型号的风险回退 / ONNX 选设备)也要改成三态,
    /// 否则启动早期会把整机静默推到 CPU 或 ONNX 路线。</summary>
    [Fact]
    public void 引擎侧也不许拿没测完当没有显卡()
    {
        string es = CodeOnly(ReadRepoFile("ImgUpscalerUI", "EngineService.cs"));
        Assert.Contains("try { if (ALHPro.VulkanCheck.NoGpuDetected) return true; } catch { }", es);
        Assert.DoesNotContain("if (!ALHPro.VulkanCheck.GpuAvailable) return true;", es);

        string onnx = CodeOnly(ReadRepoFile("ImgUpscalerUI", "EsrganOnnxService.cs"));
        Assert.Contains("if (VulkanCheck.NoGpuDetected && !EngineService.IsBlackwellGpu()) return -1;", onnx);
        Assert.DoesNotContain("if (!VulkanCheck.GpuAvailable && !EngineService.IsBlackwellGpu()) return -1;", onnx);
    }

    /// <summary>【2026-10-17 用户裁决】4GB 显存一档(1050 Ti/1650 这类)属于"能跑但慢":视频页黄字照提,
    /// 但不该每次开始都弹「开始前诊断」卡片。卡片只留给 显存&lt;4GB / 只有核显 / 确认没有 GPU 的机器。</summary>
    [Fact]
    public void 四GB显卡只留黄字提示_不该每次开始都弹卡片()
    {
        // GTX 1050 Ti 的真实画像:显存 4GB / 内存 7.9GB / 8 核 / GPU 可用 ⇒ 黄字要有,卡片不要
        Assert.True(WeakDeviceRules.IsWeak(GpuPresence.Usable, 4, 7.9, 8, onlyIntegratedGpu: false));
        Assert.False(WeakDeviceRules.NeedsStartCard(GpuPresence.Usable, 4, 7.9, 8, onlyIntegratedGpu: false));

        // 5GB 也是一个道理(黄字照提,不弹卡片)
        Assert.True(WeakDeviceRules.IsWeak(GpuPresence.Usable, 5, 16, 8, onlyIntegratedGpu: false));
        Assert.False(WeakDeviceRules.NeedsStartCard(GpuPresence.Usable, 5, 16, 8, onlyIntegratedGpu: false));

        // 3GB 显存:该弹卡片了
        Assert.True(WeakDeviceRules.NeedsStartCard(GpuPresence.Usable, 3, 8, 8, onlyIntegratedGpu: false));
        // 只有核显(共享显存):弹
        Assert.True(WeakDeviceRules.NeedsStartCard(GpuPresence.Usable, 8, 32, 10, onlyIntegratedGpu: true));
        // 确认没有可用 GPU(真·无显卡机器:显存读数也是 0):弹
        Assert.True(WeakDeviceRules.NeedsStartCard(GpuPresence.Absent, 0, 32, 10, onlyIntegratedGpu: false));
        // 但"没枚举到却有 8GB 显存"不是低配机,不弹(第二道保险)
        Assert.False(WeakDeviceRules.NeedsStartCard(GpuPresence.Absent, 8, 32, 10, onlyIntegratedGpu: false));
        // 自检还没结论:不弹(4060 那台误报的源头)
        Assert.False(WeakDeviceRules.NeedsStartCard(GpuPresence.Unknown, 8, 31.8, 10, onlyIntegratedGpu: false));
        // 好机器当然不弹
        Assert.False(WeakDeviceRules.NeedsStartCard(GpuPresence.Usable, 8, 31.8, 10, onlyIntegratedGpu: false));
        // 只是内存小/核数少(显存够用):黄字提,卡片不弹
        Assert.True(WeakDeviceRules.IsWeak(GpuPresence.Usable, 8, 4, 4, onlyIntegratedGpu: false));
        Assert.False(WeakDeviceRules.NeedsStartCard(GpuPresence.Usable, 8, 4, 4, onlyIntegratedGpu: false));
        // 跑过 GPU 的机器:连 4GB 那档也永不弹
        Assert.False(WeakDeviceRules.NeedsStartCard(GpuPresence.Usable, 4, 7.9, 8,
            onlyIntegratedGpu: false, gpuEverConfirmed: true));
    }

    /// <summary>【2026-10-17 用户要求】"一定不要再出现我这种设备也会弹出的情况"。
    /// 这台机器跑过 GPU(4060 Laptop / 8GB / 32GB / 10 核),哪怕某次探测失败没枚举到,
    /// 也只能当"暂时不知道":不说没有显卡、不判弱、不弹卡片。</summary>
    [Fact]
    public void 跑过GPU的机器_这次没枚举到也不许说没显卡也不许弹卡片()
    {
        // 没跑过 GPU、这次也没枚举到 ⇒ 有结论的 Absent(真·无显卡机器照旧提醒)
        Assert.Equal(GpuPresence.Absent, WeakDeviceRules.Resolve(probeConcluded: true, gpuAvailable: false));
        Assert.True(WeakDeviceRules.IsWeak(GpuPresence.Absent, 0, 8, 8, onlyIntegratedGpu: false));
        Assert.True(WeakDeviceRules.NeedsStartCard(GpuPresence.Absent, 0, 8, 8, onlyIntegratedGpu: false));

        // 跑过 GPU 的机器这次没枚举到 ⇒ 绝不当作"没有显卡"
        var presence = WeakDeviceRules.Resolve(probeConcluded: true, gpuAvailable: false, gpuEverConfirmed: true);
        Assert.Equal(GpuPresence.Unknown, presence);
        Assert.False(WeakDeviceRules.IsNoGpu(presence));
        Assert.False(WeakDeviceRules.IsWeak(presence, 8, 31.8, 10, onlyIntegratedGpu: false));      // 用户那台
        Assert.False(WeakDeviceRules.NeedsStartCard(presence, 8, 31.8, 10, onlyIntegratedGpu: false));

        // 自检进行中照旧 Unknown(不论有没有跑过 GPU)
        Assert.Equal(GpuPresence.Unknown, WeakDeviceRules.Resolve(probeConcluded: false, gpuAvailable: true));
        Assert.Equal(GpuPresence.Unknown, WeakDeviceRules.Resolve(probeConcluded: false, gpuAvailable: false,
            gpuEverConfirmed: true));
    }

    /// <summary>两套判据不许再混用:黄字仍看 IsWeakDevice(显存&lt;6 一档也算),
    /// 开始前卡片看 NeedsStartCardForWeakDevice(显存&lt;4 / 只有核显 / 没 GPU),且"跑过 GPU"要跨会话记住。</summary>
    [Fact]
    public void 黄字与开始前卡片各用各的门槛()
    {
        string core = CodeOnly(ReadRepoFile("AlhPro.Core", "WeakDeviceRules.cs"));
        Assert.Contains("public const double CardVramGb = 4.0;", core);
        Assert.Contains("public static bool NeedsStartCard(GpuPresence presence, double vramGb, double ramGb, int cores, bool onlyIntegratedGpu,", core);
        Assert.Contains("if (gpuEverConfirmed) return false;", core);        // 跑过 GPU ⇒ 永不弹
        Assert.Contains("if (vramGb >= MinVramGb) return false;", core);     // 6GB 以上显存 ⇒ 不是低配机
        Assert.Contains("return gpuEverConfirmed ? GpuPresence.Unknown : GpuPresence.Absent;", core);

        string sr = CodeOnly(ReadRepoFile("ImgUpscalerUI", "SafeRender.cs"));
        Assert.Contains("public static bool NeedsStartCardForWeakDevice", sr);
        Assert.Contains("AlhPro.Core.WeakDeviceRules.NeedsStartCard(CurrentGpuPresence(), TotalVramGB, TotalRamGB,", sr);
        Assert.Contains("ALHPro.VulkanCheck.GpuEverConfirmed", sr);

        // "跑过 GPU"这件事要真的存下来(跨会话)。但用户说「重新检测就是要重新检测啊」:
        // 手动重检必须连这条记录一起清掉,这次测出什么就报什么。
        string vk = CodeOnly(ReadRepoFile("ImgUpscalerUI", "VulkanCheck.cs"));
        Assert.Contains("public static bool GpuEverConfirmed => AppSettings.GpuEverConfirmed;", vk);
        Assert.Contains("AppSettings.GpuEverConfirmed = true;", vk);
        int recheckAt = vk.IndexOf("public static void Recheck()", StringComparison.Ordinal);
        Assert.True(recheckAt > 0, "VulkanCheck.Recheck() 不见了");
        string recheckBody = vk.Substring(recheckAt, Math.Min(1500, vk.Length - recheckAt));
        Assert.Contains("AppSettings.GpuEverConfirmed = false;", recheckBody);   // 手动重检 = 真的重新测
        Assert.Contains("AppSettings.VulkanGpuOk = null;", recheckBody);          // 旧结论也不许留
        string settings = CodeOnly(ReadRepoFile("ImgUpscalerUI", "AppSettings.cs"));
        Assert.Contains("public static bool GpuEverConfirmed { get; set; }", settings);
        Assert.Contains("GpuEverConfirmed = d.GpuEverConfirmed;", settings);
        Assert.Contains("GpuEverConfirmed = GpuEverConfirmed,", settings);

        string vv = CodeOnly(ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs"));
        Assert.Contains("bool weakDevice = SafeRender.NeedsStartCardForWeakDevice && FastModeCheck.IsChecked != true;", vv);
        Assert.DoesNotContain("bool weakDevice = SafeRender.IsWeakDevice", vv);
        Assert.Contains("bool weak = SafeRender.IsWeakDevice && FastModeCheck.IsChecked != true;", vv);   // 黄字口径不变
    }

    // ───────────────── 工具 ─────────────────

    private static int Count(string src, string needle)
    {
        int n = 0, at = 0;
        while ((at = src.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { n++; at += needle.Length; }
        return n;
    }

    /// <summary>剥掉 `//` 行注释(逐字符扫引号:字符串里的 `//` 不算注释)。不剥注释的源码断言会被
    /// "把那一行注释掉"骗过去(本仓库踩过,见 RememberParamsPresetTests 的红检记录)。</summary>
    private static string CodeOnly(string src)
    {
        var sb = new System.Text.StringBuilder(src.Length);
        foreach (var line in src.Split('\n'))
        {
            bool inStr = false, inChar = false;
            int cut = line.Length;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inStr)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '"') inStr = false;
                    continue;
                }
                if (inChar)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '\'') inChar = false;
                    continue;
                }
                if (c == '"') { inStr = true; continue; }
                if (c == '\'') { inChar = true; continue; }
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') { cut = i; break; }
            }
            sb.Append(line[..cut]).Append('\n');
        }
        return sb.ToString();
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var cand = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(cand)) return File.ReadAllText(cand);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("找不到仓库文件: " + string.Join('/', parts));
    }
}
