using System.Collections.Generic;
using System.Linq;
using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>ncnn 探测结论的判定口径(2026-09-22)。
///
/// 【回归样本来自用户真实诊断包】`ALHPro_Diag_20260922_2237`(RTX 5060 Laptop)里的 `ncnn-probe.txt`:
///     realesrgan2026|0|anime4k   False   probe failed: 无响应(超时被强杀)
///     realesrgan2026|0|          True    probe ok; model=(default)
///     waifu2x|0|                 True    probe ok; model=(default)
/// 旧口径按"任意一支失败 = 整条引擎不可用"判 ⇒ realesrgan2026 整体走 ONNX ⇒
/// 四支自训模型(只有 ncnn 权重)**全部跑不了**(用户反馈"50 系跑 real 跑不了"的根因)。
/// 这些用例就是拿上面那份真实数据钉住新口径:**default 通过 ⇒ 引擎可用;只禁失败的那一支**。
/// </summary>
public class NcnnModelVerdictsTests
{
    /// <summary>用户诊断包里的真实三条结论。</summary>
    private static List<NcnnModelVerdicts.Entry> RealDiagPackage() => new()
    {
        new("realesrgan2026|0|anime4k", false, 1790087112),
        new("realesrgan2026|0|", true, 1790087896),
        new("waifu2x|0|", true, 1790087897),
    };

    [Fact]
    public void One_failed_model_does_not_disable_the_whole_engine()
    {
        var e = RealDiagPackage();
        Assert.True(NcnnModelVerdicts.EngineUsable(e, "realesrgan2026", 0));   // default 通过 ⇒ 引擎可用
    }

    [Fact]
    public void Only_the_failed_model_is_blocked()
    {
        var e = RealDiagPackage();
        Assert.False(NcnnModelVerdicts.IsModelUsable(e, "realesrgan2026", 0, "anime4k"));       // 它自己失败 ⇒ 禁
        Assert.True(NcnnModelVerdicts.IsModelUsable(e, "realesrgan2026", 0, "alhpro-real2x"));  // 自训模型回落引擎级 ⇒ 可用
        Assert.True(NcnnModelVerdicts.IsModelUsable(e, "realesrgan2026", 0, "alhpro-game2x"));
    }

    [Fact]
    public void Engine_failure_still_disables_everything()
    {
        var e = new List<NcnnModelVerdicts.Entry>
        {
            new("realesrgan2026|0|", false, 1),          // default 也失败 = 引擎真的跑不动
            new("realesrgan2026|0|anime4k", true, 1),
        };
        Assert.False(NcnnModelVerdicts.EngineUsable(e, "realesrgan2026", 0));
        Assert.False(NcnnModelVerdicts.IsModelUsable(e, "realesrgan2026", 0, "alhpro-real2x"));
    }

    [Fact]
    public void Untested_engine_returns_null_not_false()
    {
        var e = RealDiagPackage();
        Assert.Null(NcnnModelVerdicts.EngineUsable(e, "realesrgan", 0));            // 别的引擎 id
        Assert.Null(NcnnModelVerdicts.EngineUsable(e, "realesrgan2026", 1));        // 别的设备
        Assert.Null(NcnnModelVerdicts.IsModelUsable(new List<NcnnModelVerdicts.Entry>(), "x", 0, "y"));
    }

    [Fact]
    public void Verdicts_are_per_device()
    {
        var e = new List<NcnnModelVerdicts.Entry>
        {
            new("realesrgan2026|0|", false, 1),
            new("realesrgan2026|1|", true, 1),
        };
        Assert.False(NcnnModelVerdicts.EngineUsable(e, "realesrgan2026", 0));   // 核显/坏卡失败
        Assert.True(NcnnModelVerdicts.EngineUsable(e, "realesrgan2026", 1));    // 别把另一块卡也禁了
    }

    [Fact]
    public void Describe_reports_the_engine_usable_and_names_only_the_failed_model()
    {
        var s = NcnnModelVerdicts.Describe(RealDiagPackage(), "realesrgan2026", 0);
        Assert.Contains("实测可用", s);
        Assert.Contains("anime4k", s);
        Assert.DoesNotContain("实测不可用", s);
    }

    [Fact]
    public void Describe_says_unusable_when_default_failed()
    {
        var e = new List<NcnnModelVerdicts.Entry> { new("realesrgan2026|0|", false, 1) };
        Assert.Contains("实测不可用", NcnnModelVerdicts.Describe(e, "realesrgan2026", 0));
    }

    [Fact]
    public void Describe_says_untested_when_empty()
    {
        Assert.Contains("未测", NcnnModelVerdicts.Describe(new List<NcnnModelVerdicts.Entry>(), "realesrgan2026", 0));
    }
}
