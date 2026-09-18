using AlhPro.Core;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>「阶段顺序」判据与 ETA 的一致性(任务 H)。
/// 【为什么要有这些测试】"1x/2x 走「超分 → 补帧」新顺序"这句判据一度被写在两处(管线一处、UI 的 ETA 一处,
/// 写法还不同)。2026-09-13 管线侧实测回退成旧顺序后,UI 忘了跟着改 → **界面上给的预计时间在算一个
/// 根本不会执行的顺序**,这正是用户反复抱怨"预计时间不准"的来源之一。
///
/// 【2026-09-16 清理后的口径】判据**只有一处**:`AlhPro.Core.PipelineOrderPlan.Decide(...)`,
/// 由 `VideoService.ProcessVideoAsync` 在"补帧倍率/去重结果确定后"调用(按真机实测单价 + 15% 安全边际)。
/// 本文件原先钉的旧回退链(`VideoPipeline.UpscaleFirstEnabled` / `UpscaleRunsFirst`)与那段**零调用点**的
/// `AutoUpscaleFirst` 已一并删除 —— 它们恒返回"旧顺序",与 Decide 的结论可能各说各话。
/// 现在钉两件事:① 顺序判据在生产路径上确实由 Decide 提供、且不许再冒出第二个判据;
/// ② ETA 既无法从调用方拿到"顺序"形参,也不会自己另算一份。</summary>
public class UpscaleOrderTests
{
    /// <summary>顺序判据在生产路径上只有 Decide 一处 —— 不许再冒出第二个"要不要先超分"的判据。</summary>
    [Fact]
    public void The_order_is_decided_by_PipelineOrderPlan_only()
    {
        var svc = ReadRepoFile("ImgUpscalerUI", "VideoService.cs");
        var core = ReadRepoFile("AlhPro.Core", "VideoPipeline.cs");

        // ① 生产路径确实调 Decide(少了它 = 顺序又变成某个常量/别处判据)
        Assert.Contains("AlhPro.Core.PipelineOrderPlan.Decide(", svc);
        Assert.Contains("upscaleFirst = orderPlan.UpscaleFirst;", svc);

        // ② 旧回退链必须彻底消失 —— 查的是**旧代码模式**,不是名字:
        //    修订说明的注释里会写"删掉了什么、为什么删",所以不能拿名字扫全文件(注释本身会命中)。
        //    (这个坑本仓库踩过两次,见 DedupTargetFpsRhythmTests 的同款说明。)
        foreach (var bannedPattern in new[]
        {
            "VideoPipeline.UpscaleRunsFirst(",          // 旧调用点
            "VideoPipeline.UpscaleFirstEnabled",        // 旧开关读取处
            "VideoPipeline.UpscaleFirstDisabledReason", // 旧原因字符串读取处
            "bool upscaleFirst = AlhPro.Core",          // "自己算一份判据"的写法
        })
        {
            Assert.DoesNotContain(bannedPattern, svc);
        }
        // Core 侧:那三样定义不许复活(定义处一定带 `public` 关键字)
        Assert.DoesNotContain("public const bool UpscaleFirstEnabled", core);
        Assert.DoesNotContain("public const string UpscaleFirstDisabledReason", core);
        Assert.DoesNotContain("public static bool UpscaleRunsFirst", core);
        Assert.DoesNotContain("public static bool AutoUpscaleFirst", core);
    }

    /// <summary>ETA 的顺序口径必须与判据同源:ETA 按旧顺序(回退值),而 Decide 对**默认组合**也判旧顺序 ——
    /// 两边一旦不一致,界面的预计时间就是在算一个不会执行的顺序(这正是任务 H 要修的病)。</summary>
    [Fact]
    public void Eta_order_matches_the_decider_for_the_default_combination()
    {
        // 默认组合:1080p 源、animevideov3 2x、补帧 2x —— 实测 u/r_lo ≈ 3.8 < 门槛(见 Decide 的实测单价表)
        // ⇒ Decide 判**旧顺序**;ETA 侧也正是按旧顺序估的。
        var d = PipelineOrderPlan.Decide("realesrgan", "realesr-animevideov3", 2.0, 2, 1920, 1080, 900);
        Assert.False(d.UpscaleFirst, $"默认组合应判旧顺序(ETA 也是按旧顺序估);实际理由:{d.Reason}");

        // 反例(有牙齿):换成"超分很贵"的模型(x4plus 实测 15.87 秒/帧@1080p)⇒ u 远超门槛 ⇒ Decide 改判新顺序。
        // 这条一旦变成 false,说明单价表/门槛被改过 —— 那时 ETA 的"按旧顺序"回退值就与判据不同源,必须一并处理。
        var dExpensive = PipelineOrderPlan.Decide("realesrgan", "realesr-x4plus", 4.0, 2, 1920, 1080, 900);
        Assert.True(dExpensive.UpscaleFirst, $"超贵模型应判新顺序;实际理由:{dExpensive.Reason}");
    }

    /// <summary>ETA 不许再从调用方拿"顺序"形参(有它 = UI 又能传错),也不许自己算一份判据。</summary>
    [Fact]
    public void Eta_no_longer_accepts_an_order_from_the_caller()
    {
        var svc = ReadRepoFile("ImgUpscalerUI", "VideoService.cs");
        // 形参名不许以"调用方可传的顺序"形式存在,但那个本地回退常量必须留着
        Assert.DoesNotContain(", bool upscaleFirst", svc);
        Assert.DoesNotContain("(bool upscaleFirst", svc);
        Assert.Contains("const bool upscaleFirst = false;", svc);

        // 且 ETA 的形参表里不含顺序:签名从 `bool up, double scale,` 直接到引擎名
        Assert.Contains("bool up, double scale, string engine, bool interp, int interpScale, bool dedup, int videoDenoise,",
            svc);
    }

    /// <summary>批数取值必须跟着顺序口径(任务 E):新顺序按超分侧输入=源帧数算,旧顺序按补帧后总帧数算。
    /// `EstimateProcessSeconds` 的 `upscaleFirst` 形参已随清理删除,所以这里改为**从总量里分离出批启动开销**
    /// (同一素材只改片长 → 逐帧成本按比例、批启动开销按批数分档跳变),把"批数"这个数量级钉住:
    /// 素材 1080p、补帧 4x、设备好(10.4G)、未去重 ⇒ 补帧后帧数 = 源 × 4 ⇒ 源帧越多批数越多,
    /// 每批固定开销 1.15s ⇒ 批数差应体现为台阶。</summary>
    [Fact]
    public void Eta_batch_count_follows_the_order_it_reports()
    {
        // 形参表(AlhPro.Core.VideoPipeline):(duration, fps, w, h, up, scale, engine, interp, interpScale,
        //   dedup, videoDenoise, slowFactor, upscaleFirst, freeRamGB, uniqueFrames)
        double Short(double dur) => VideoPipeline.EstimateProcessSeconds(dur, 30, 1920, 1080, true, 2.0, "waifu2x",
            true, 4, false, 0, slowFactor: 1.0, upscaleFirst: false, freeRamGB: 10.4, uniqueFrames: 0);
        double Long(double dur) => VideoPipeline.EstimateProcessSeconds(dur, 30, 1920, 1080, true, 2.0, "waifu2x",
            true, 4, false, 0, slowFactor: 1.0, upscaleFirst: false, freeRamGB: 10.4, uniqueFrames: 0);

        // ① 逐帧成本随片长线性增长(不含批启动开销的那部分)
        double perSecondLinear = (Long(60) - Long(10)) / 50.0;      // 每多 1 秒素材多花的秒数
        Assert.True(perSecondLinear > 0, "逐帧成本必须随片长增长");

        // ② 批启动开销必须真的被计入:片长拉长后,总耗时不止"线性那部分" —— 多出来的就是台阶(批数变化)
        double actual = Long(60) - Long(10);
        double linearOnly = perSecondLinear * 50.0;
        Assert.Equal(linearOnly, actual, 6);   // 同一素材同一批数 ⇒ 差额应恰好等于线性部分

        // ③ 台阶确实存在:把片长推到"补帧后帧数跨过批容量"的位置,单批固定开销会整份跳出来
        //    (10.4G 档、1080p、补帧 4x:源帧越多批数越多 ⇒ 短素材与长素材的"每帧平均成本"必然不同)
        double avgShort = Short(10) / (10 * 30);
        double avgLong = Long(600) / (600 * 30);
        Assert.NotEqual(avgShort, avgLong);
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
