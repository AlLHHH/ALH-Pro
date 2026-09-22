using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【2026-09-23 Anime4K 收口 · 接线契约】纯逻辑(枚举/选择)由 <see cref="Anime4kVulkanDeviceTests"/> 钉住;
/// 这个文件钉的是**接线**:设备索引必须 ① 探测之前先定、② 探测与正式滤镜**同一个来源**、③ 抽样实测也带上。
///
/// 【为什么这三条要单独钉】这台 5060 的症状是"1x 修复探测无响应(超时被强杀)",而修法只有一条路:
/// 让 Anime4K 跑到独显上。如果只把设备参数用在**探测**上,用户会看到"探测通过"然后正式滤镜又回到核显 ——
/// 那比现在更难查(表面上已经没有错误信息了)。所以"同一来源、同一张卡"这件事必须是可检查的契约,
/// 而不是散在两个文件里的字符串巧合。</summary>
public class Anime4kDeviceWiringTests
{
    [Fact]
    public void Device_is_chosen_before_the_shader_probe()
    {
        var src = ReadRepoFile("ImgUpscalerUI", "EngineService.cs");
        var code = StripComments(src);
        int probe = code.IndexOf("public static async Task<bool> EnsureAnime4kProbeAsync(", StringComparison.Ordinal);
        int pick = code.IndexOf("await EnsureAnime4kVulkanDeviceAsync(ct).ConfigureAwait(false);", probe, StringComparison.Ordinal);
        int firstProbeCall = code.IndexOf("await ProbeAnime4kOnceAsync(ct)", probe, StringComparison.Ordinal);
        Assert.True(probe > 0 && pick > probe, "必须先在 EnsureAnime4kProbeAsync 里定设备");
        Assert.True(firstProbeCall > pick, "定设备必须**早于**第一次真跑探测(否则探测与正式滤镜可能不是同一张卡)");
        // 枚举命令来自 Core 的常量(不许各写一份)
        Assert.Contains("AlhPro.Core.Anime4kVulkanDevice.ListDevicesArgs", code);
        Assert.Contains("AlhPro.Core.Anime4kVulkanDevice.ParseGpuListing(log)", code);
        Assert.Contains("AlhPro.Core.Anime4kVulkanDevice.Pick(devices, prefer)", code);
        // 优先用"界面/设置里正在用的那张卡"的名字(AppSettings 里 GpuIndex 与 GpuName 是一起存的)
        Assert.Contains("prefer = AppSettings.GpuName;", code);
    }

    /// <summary>手动钩子的优先级最高(它既是排查手段,也是自动选错时的逃生口),没设才用自动检测。</summary>
    [Fact]
    public void Manual_hook_wins_over_auto_detection()
    {
        var code = StripComments(ReadRepoFile("ImgUpscalerUI", "EngineService.cs"));
        int hook = code.IndexOf("ALH_FORCE_ANIME4K_DEVICE", StringComparison.Ordinal);
        int auto = code.IndexOf("int? auto = Anime4kVulkanDeviceIndex;", StringComparison.Ordinal);
        Assert.True(hook > 0 && auto > hook, "手动钩子必须先判");
        Assert.Contains("if (auto.HasValue) return AlhPro.Core.Anime4kVulkanDevice.DeviceArgs(auto.Value);", code);
    }

    /// <summary>★ 正式合帧命令必须带上同一台设备 —— 而且设备参数是 ffmpeg 的**全局**选项,
    /// 必须出现在第一个 `-i` 之前(塞进 -vf 里是无效的)。</summary>
    [Fact]
    public void Production_mux_command_carries_the_same_device()
    {
        var code = StripComments(ReadRepoFile("ImgUpscalerUI", "VideoService.cs"));
        Assert.Contains("string animeDevArgs = \"\";", code);
        Assert.Contains("if (anime4k1x)", code);
        Assert.Contains("EngineService.Anime4kVulkanDeviceIndex is int ai ? AlhPro.Core.Anime4kVulkanDevice.DeviceArgs(ai) : \"\"", code);
        // 全局选项位置:拼在 muxBase 的 -y 之后、序列输入 -i 之前
        Assert.Contains("var muxBase = $\"-y {animeDevArgs}{muxInput} {trimArgs} -i \\\"{inputVideo}\\\" \";", code);
        // 抽样实测链(编码阶段拆分)也要带,否则"抽样的滤镜路径 ≠ 真实合帧的滤镜路径"
        Assert.Contains("if (chain.Contains(\"libplacebo\", StringComparison.OrdinalIgnoreCase))", code);
        Assert.Contains("$\"-nostdin -y -v error {devArgs}{hw}-framerate {fpsArg} \"", code);
        // 只在真的挂了这个滤镜时才加(不挂滤镜时一字不改)
        Assert.Contains("chain.Contains(\"libplacebo\"", code);
    }

    /// <summary>索引的拼法只有**一个**实现地方(AlhPro.Core),UI 侧不许再写一份字符串字面量。</summary>
    [Fact]
    public void Device_arg_string_is_defined_once()
    {
        var core = StripComments(ReadRepoFile("AlhPro.Core", "Anime4kVulkanDevice.cs"));
        var engine = StripComments(ReadRepoFile("ImgUpscalerUI", "EngineService.cs"));
        var svc = StripComments(ReadRepoFile("ImgUpscalerUI", "VideoService.cs"));
        Assert.Contains("\"-init_hw_device vulkan=alh:\" + index.ToString(CultureInfo.InvariantCulture) + \" -filter_hw_device alh \"", core);
        // 【钩子那条】(作者/用户直接给数字)也必须走同一个拼法:EngineService 里因为要同时写日志,
        // 拼在本地;VideoService 那边直接调 DeviceArgs ⇒ 形状不会跑偏。两处都不许出现写死的索引。
        Assert.Contains("\"-init_hw_device vulkan=alh:\" + f + \" -filter_hw_device alh \"", engine);
        Assert.Contains("AlhPro.Core.Anime4kVulkanDevice.DeviceArgs(int.Parse(forced.Trim()", svc);
        Assert.DoesNotContain("\"-init_hw_device vulkan=alh:0\"", engine + svc);
    }

    private static string StripComments(string text)
        => string.Join("\n", text.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));

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
