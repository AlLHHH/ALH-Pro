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

    // ───────────────────────── 【2026-09-24】没有 default 结论时的兜底口径 ─────────────────────────

    /// <summary>本机(4060 Laptop + 核显)真实缓存形状:**没有 default 行**,只有两条带模型的结论。
    /// 纯 N 卡 + 核显走不了"免探测快速通道",而每次探测都带模型 ⇒ 从来不写 default ⇒
    /// `EngineUsable` 返回 null,必须靠兜底口径。旧兜底"任一支失败即整条不可用"里的那支失败正是
    /// anime4k(走 libplacebo 着色器、被误当模型名喂给 ncnn 探测超时),于是整条 realesrgan 被判走 ONNX。</summary>
    private static List<NcnnModelVerdicts.Entry> ThisMachineNoDefaultRow() => new()
    {
        new("realesrgan2026|0|realesr-animevideov3", true, 1790166490),
        new("realesrgan2026|0|anime4k", false, 1790173376),
    };

    [Fact]
    public void Without_a_default_row_a_pseudo_model_failure_must_not_disable_the_engine()
    {
        var e = ThisMachineNoDefaultRow();
        Assert.Null(NcnnModelVerdicts.EngineUsable(e, "realesrgan2026", 0));                 // 没有 default 行
        Assert.True(NcnnModelVerdicts.ModelOnlyRisk(e, "realesrgan2026", 0));                // ⇒ 只看真模型:通过
        Assert.False(NcnnModelVerdicts.IsNonNcnnModel("realesr-animevideov3"));               // 真模型不受影响
        Assert.True(NcnnModelVerdicts.IsNonNcnnModel("anime4k"));                             // 伪模型
        Assert.True(NcnnModelVerdicts.IsNonNcnnModel("  Anime4K "));                          // 大小写/空白不敏感
        Assert.False(NcnnModelVerdicts.IsNonNcnnModel(null));
        Assert.False(NcnnModelVerdicts.IsNonNcnnModel(""));
    }

    /// <summary>兜底口径仍然是保守的:真模型里有失败 ⇒ 引擎判风险(不能因为"跳过了伪模型"就变成永远可用)。</summary>
    [Fact]
    public void A_real_model_failure_still_makes_the_engine_risky_without_a_default_row()
    {
        var e = new List<NcnnModelVerdicts.Entry>
        {
            new("realesrgan2026|0|realesr-animevideov3", true, 1),
            new("realesrgan2026|0|alhpro-real2x", false, 1),     // 真模型(只有 ncnn 权重)失败
        };
        Assert.False(NcnnModelVerdicts.ModelOnlyRisk(e, "realesrgan2026", 0));
    }

    /// <summary>只有伪模型结论时 = 一条真 ncnn 结论都没有 ⇒ 返回 null(当"未测",交给启发式),别乱判。</summary>
    [Fact]
    public void Only_pseudo_rows_means_untested()
    {
        var e = new List<NcnnModelVerdicts.Entry> { new("realesrgan2026|0|anime4k", false, 1) };
        Assert.Null(NcnnModelVerdicts.ModelOnlyRisk(e, "realesrgan2026", 0));
    }

    /// <summary>兜底口径按"引擎 + 设备"隔离:别把 10 号卡/别的引擎的结论算进来(键前缀成对)。</summary>
    [Fact]
    public void The_fallback_summary_is_scoped_to_engine_and_device()
    {
        var e = new List<NcnnModelVerdicts.Entry>
        {
            new("realesrgan2026|0|realesr-animevideov3", true, 1),
            new("realesrgan2026|10|alhpro-real2x", false, 1),   // 双位数设备号:不许被 engine|1 误吃
            new("waifu2x|0|models-cunet", false, 1),            // 别的引擎
        };
        Assert.True(NcnnModelVerdicts.ModelOnlyRisk(e, "realesrgan2026", 0));
        Assert.False(NcnnModelVerdicts.ModelOnlyRisk(e, "realesrgan2026", 10));
        Assert.Null(NcnnModelVerdicts.ModelOnlyRisk(e, "realesrgan2026", 1));
    }

    /// <summary>自检报告里要把伪模型标出来(否则看着像"ncnn 有一支跑不了")。</summary>
    [Fact]
    public void Describe_marks_pseudo_models_so_they_do_not_look_like_ncnn_failures()
    {
        var s = NcnnModelVerdicts.Describe(ThisMachineNoDefaultRow(), "realesrgan2026", 0);
        Assert.Contains("非 ncnn 模型", s);
    }
}
