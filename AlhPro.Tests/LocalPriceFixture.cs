using AlhPro.Core;
using System.Collections.Generic;

namespace AlhPro.Tests;

/// <summary>单测用的「本机标定表」构造器(2026-09-25 A+B)。
///
/// 【为什么要有它】判据现在只吃**本机实测单帧耗时**(`LocalPriceBook`);既有那些钉数字的断言
/// (animevideov3 2x = 0.2605、x4plus = 15.87 …)测的是**成本模型的数学**,不该因为"换成本机口径"就丢掉。
/// 所以这里把内置表(他机资料)里的数字**包装成一条本机标定**(采样面积取 1080p,折算后数值相等),
/// 让老断言用新机制继续成立 —— 每一处改动都在 t27 的 output 里列明。
///
/// 【机器指纹】用固定串,和真实 `CalibMemory.MachineKeyOf()` 无关(单测不碰注册表/GPU)。</summary>
internal static class LocalPriceFixture
{
    /// <summary>"本机"(单测约定)。</summary>
    public const string MachineKey = "单测机|GeForce RTX 4060 Laptop GPU|realesrgan:realesrgan-ncnn-vulkan-2026.exe@1@1";

    /// <summary>"另一台机器"(用来验证指纹不匹配时不采信)。</summary>
    public const string OtherMachineKey = "别人的机器|Radeon RX 7900 XTX";

    /// <summary>单测默认的后端(ncnn-Vulkan)。**后端进键**是 2026-09-25 修订 F1 的核心,
    /// 每条夹具都必须显式带一个后端(空后端会被 `TryBuild` 拒收,也不可能出现在文件里)。</summary>
    public const string Backend = UpscaleBackendPlan.NcnnVulkan;

    /// <summary>构造一条"采样面积正好是 1080p"的本机标定 ⇒ `SecondsPerFrame1080p == secondsPerFrame1080p`。
    /// 两次耗时就按两点法自洽地编出来(floor 0.9s + N 帧 × 单帧耗时),所以 `Provenance` 里的数字也是真的能对上的。</summary>
    public static LocalPrice At1080p(string engineModelName, int engineScale, double secondsPerFrame1080p,
        string machineKey = MachineKey, int sampleFrames = 6, string backend = Backend)
    {
        const double floor = 0.9;
        return new LocalPrice(PipelineOrderPlan.NormalizeModel(engineModelName)!, engineScale, secondsPerFrame1080p,
            1920L * 1080, sampleFrames, floor, floor + sampleFrames * secondsPerFrame1080p,
            "2026-09-25 12:00:00", machineKey, backend, "单测构造");
    }

    /// <summary>取**内置表(他机资料)**里那一格的数字,包装成本机标定 —— 用于让老断言在新机制下继续成立。</summary>
    public static IReadOnlyList<LocalPrice> Built(string engineModelName, int engineScale, string machineKey = MachineKey,
        string backend = Backend)
        => new[]
        {
            At1080p(engineModelName, engineScale,
                PipelineOrderPlan.LookupUpscaleSecondsPerFrame(engineModelName, engineScale, out _)!.Value, machineKey,
                backend: backend),
        };

    /// <summary>任意采样面积的一条(用于面积折算测试)。</summary>
    public static LocalPrice AtPixels(string engineModelName, int engineScale, double secondsPerFrame,
        long samplePixels, string machineKey = MachineKey, string backend = Backend)
        => new(PipelineOrderPlan.NormalizeModel(engineModelName)!, engineScale, secondsPerFrame, samplePixels, 6,
            0.9, 0.9 + 6 * secondsPerFrame, "2026-09-25 12:00:00", machineKey, backend, "单测构造");

    public static IReadOnlyList<LocalPrice> Book(params LocalPrice[] prices) => prices;
}
