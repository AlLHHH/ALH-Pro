using AlhPro.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>「转场阈值」滑块 → 切点判据的映射(2026-09-21 用户要求"滑块加回来,并且要真的生效")。
///
/// 【为什么必须有这个文件】这条滑块 2026-09-15 被删的理由正是"它不影响判定"= 一条**假控件**。
/// 现在把它接回判据,最容易犯的错有三个,全都是静默的(编译过、界面看着正常、只是行为不对):
///   ① 默认档换算出来的判据**不等于**原来的内置值(帧差 25 / 强切 50 / 拉普拉斯比 0.60)
///      ⇒ 用户没动滑块,成片画面却变了 —— 这是本仓库最不能接受的一类回归;浮点尾巴
///      (`Slider` 会算成 0.30000000000000004)正是它的实际入口,所以第一组用例逐字钉死默认档;
///   ② 方向搞反(值越大切点越多)⇒ 用户"调大想少切"反而切得更碎、补帧引擎启动更多次,
///      正是他这次报的"转场补帧分批引擎启动太慢"的加剧版;
///   ③ 极值处判据退化成没有意义的值(阈值 0 = 每一对帧都算切点)。
///
/// 【本文件钉的四组】① 默认档逐字一致 + 浮点尾巴吸附;② 严格度单调(三条判据同向);
/// ③ 用**真实实测锚点**验证"调大真的少切、调小真的多切";④ 刻度边界与非法输入不炸。</summary>
public class SceneThresholdMapTests
{
    // ================== ① 默认档必须与今天的内置值逐字一致 ==================

    /// <summary>契约:滑块默认档(0.30)算出来的三个数**逐字等于** SceneCutJudge 的内置常量。
    /// 这是"不拉滑块 ⇒ 行为一个字节都不变"的纯逻辑保证。</summary>
    [Fact]
    public void Default_slider_hits_the_builtin_thresholds_exactly()
    {
        var j = SceneThresholdMap.For(SceneThresholdMap.DefaultSlider);
        Assert.Equal(SceneCutJudge.DiffThreshold, j.Diff);                  // 25.0
        Assert.Equal(SceneCutJudge.StrongDiffThreshold, j.StrongDiff);      // 50.0
        Assert.Equal(SceneCutJudge.LapDropRatio, j.LapDrop);                // 0.60
        Assert.Equal(SceneCutThresholds.BuiltIn, j);                        // 与"内置"这个值本身也逐字相等
        Assert.Equal(25.0, j.Diff);
        Assert.Equal(50.0, j.StrongDiff);
        Assert.Equal(0.6, j.LapDrop);
    }

    /// <summary>【真机入口】界面的 Slider 会把 0.30 算成 `0.15 + 3 × 0.05 = 0.30000000000000004`
    /// (浮点尾巴)。若直接拿它算倍率,帧差阈值会变成 25.000000000000004 —— 上面那条"逐字一致"就成了空话。
    /// 这里把**带尾巴的值**喂进去,断言仍然逐字命中 25/50/0.6。</summary>
    [Fact]
    public void Floating_point_tail_from_the_slider_is_absorbed()
    {
        double tailed = 0.15 + 3 * 0.05;                 // = 0.30000000000000004
        Assert.NotEqual(0.3, tailed);                    // 先确证尾巴真的存在(否则本用例是空转)
        var j = SceneThresholdMap.For(tailed);
        Assert.Equal(SceneCutJudge.DiffThreshold, j.Diff);
        Assert.Equal(SceneCutJudge.StrongDiffThreshold, j.StrongDiff);
        Assert.Equal(SceneCutJudge.LapDropRatio, j.LapDrop);
        Assert.Equal(0.3, SceneThresholdMap.Snap(tailed));
    }

    /// <summary>吸附只影响"拖到两格之间"的中间值,不会把整条刻度的方向搞乱:
    /// 每个刻度点都吸附回自己(界面 StepFrequency 与 Core 的 SliderStep 必须一致,否则拖不到默认档那个点)。</summary>
    [Fact]
    public void Every_step_snaps_to_itself()
    {
        for (int i = 0; i <= SceneThresholdMap.MaxSteps; i++)
        {
            double v = SceneThresholdMap.SliderMin + i * SceneThresholdMap.SliderStep;
            Assert.Equal(Math.Round(v, 2), SceneThresholdMap.Snap(v));
        }
        // 默认档必须正好落在某一格上(否则界面拖不到它)
        int idx = (int)Math.Round((SceneThresholdMap.DefaultSlider - SceneThresholdMap.SliderMin)
            / SceneThresholdMap.SliderStep);
        Assert.Equal(SceneThresholdMap.DefaultSlider,
            Math.Round(SceneThresholdMap.SliderMin + idx * SceneThresholdMap.SliderStep, 2));
    }

    // ================== ② 严格度必须单调(三条判据同向) ==================

    /// <summary>滑块越大 ⇒ 帧差/强切阈值只增不减、拉普拉斯下降比只减不增。
    /// 三条同向才是"值越大 = 切点越少"的充要条件(判据 = 帧差过闸 且(强切 或 清晰度骤降))。</summary>
    [Fact]
    public void Strictness_is_monotone_across_the_whole_scale()
    {
        var prev = SceneThresholdMap.For(SceneThresholdMap.SliderMin);
        for (double s = SceneThresholdMap.SliderMin + SceneThresholdMap.SliderStep;
             s <= SceneThresholdMap.SliderMax + 1e-9; s += SceneThresholdMap.SliderStep)
        {
            var cur = SceneThresholdMap.For(s);
            Assert.True(cur.Diff >= prev.Diff, $"滑块 {s:0.00} 的帧差阈值不得比上一档小");
            Assert.True(cur.StrongDiff >= prev.StrongDiff, $"滑块 {s:0.00} 的强切阈值不得比上一档小");
            Assert.True(cur.LapDrop <= prev.LapDrop, $"滑块 {s:0.00} 的拉普拉斯下降比不得比上一档大");
            prev = cur;
        }
        // 两端必须真的拉开差距(整条行程都在起作用,不能是"前 80% 没反应"的假滑块)
        var lo = SceneThresholdMap.For(SceneThresholdMap.SliderMin);
        var hi = SceneThresholdMap.For(SceneThresholdMap.SliderMax);
        Assert.True(hi.Diff >= lo.Diff * 2.5, $"最大档的帧差阈值({hi.Diff})应明显高于最小档({lo.Diff})");
        Assert.True(hi.LapDrop <= lo.LapDrop * 0.5, $"最大档的拉普拉斯下降比({hi.LapDrop})应明显低于最小档({lo.LapDrop})");
        Assert.Equal(0.5, SceneThresholdMap.Strictness(SceneThresholdMap.SliderMin));
        Assert.Equal(3.0, SceneThresholdMap.Strictness(SceneThresholdMap.SliderMax));
    }

    /// <summary>【行为侧】把三处**实测真硬切** + 五处**实测运动帧对**(都不是切点) + 一处极端变化混在一起,
    /// 真的跑一遍 <see cref="SceneCutJudge.Detect"/>:滑块越大,判出的切点只减不增。
    /// 【为什么必须有这一条】光看"阈值数字变大了"证明不了"切点真的变少了" —— 判据里还有
    /// "强切档"和"清晰度"两条支路,方向对不对只有真跑才知道。
    ///
    /// 【数据口径】<c>lapVar</c> 是**逐帧**的拉普拉斯能量(判据取 lap[i+1]/lap[i]),所以下面统一用
    /// 恒定的 1.0(每对帧的"清晰度比"=1.0)"——这样切点是否成立就完全由帧差那一维决定,读起来一目了然。
    /// 【diff 的出处(同 SceneCutInvarianceTests 记下的那批测量)】:
    ///   · 57.11 —— onepiece_demo.mp4 源帧 99→100,目视确认的真硬切(靠"帧差≥50 强切档"抓到);
    ///   · 86.13 / 80.01 —— syn_3cut.mp4 的两处硬切(全片次高 diff 只有 2.57,毫无歧义);
    ///   · 25.42 / 24.10 / 30 / 28 / 25 —— 同片运动帧对(镜头推移),**不是**切点;
    ///   · 1~3 —— 普通相邻帧;200 —— 极端变化(整帧变化,任何档位都必须判切,防"什么都不算切"的退化档)。</summary>
    [Fact]
    public void More_strict_slider_never_produces_more_cuts()
    {
        var diff = new List<double> { 1, 2, 30, 24, 25.42, 57.11, 3, 2, 24.10, 28, 25, 86.13, 2, 80.01, 1, 200 };
        var lap = new List<double>();
        for (int i = 0; i < diff.Count; i++) lap.Add(1.0);

        int Cuts(double s) => SceneCutJudge.Detect(diff, lap, minSceneLen: 1, thresholds: SceneThresholdMap.For(s)).Count;

        int last = int.MaxValue;
        for (double s = SceneThresholdMap.SliderMin; s <= SceneThresholdMap.SliderMax + 1e-9; s += SceneThresholdMap.SliderStep)
        {
            int n = Cuts(s);
            Assert.True(n <= last, $"滑块 {s:0.00} 判出的切点数({n})比更松的档({last})还多 —— 方向反了");
            last = n;
        }

        int lo = Cuts(SceneThresholdMap.SliderMin);
        int def = Cuts(SceneThresholdMap.DefaultSlider);
        int hi = Cuts(SceneThresholdMap.SliderMax);
        // 默认档 = 今天的行为:三处实测真硬切必须全在(靠"帧差≥50"那一档),其余一处都不许多判 ⇒ 恰好 4 处
        Assert.Equal(4, def);
        // 最小档(判据半倍 = 帧差 12.5 / 强切 25):连"镜头推移"(30 / 24 / 25.42 / 24.10 / 28 / 25 六处)都会被算成切
        // —— 这就是"更敏感"的字面意思(界面 tooltip 已说明"调小 = 更敏感")。
        Assert.True(lo > def, $"最灵敏档应比默认档多判出切点(实测 {lo} vs {def})");
        Assert.Equal(10, lo);
        // 最大档(判据三倍 = 帧差 75 / 强切 150):只剩那处 200 的极端变化,其余(含真硬切)全被"最少切点"口径放掉
        Assert.Equal(1, hi);
        Assert.True(hi < def);
        // 【不许退化成"什么都不算切"】任何档位都必须抓住 diff=200 的那一对(下标 15)
        Assert.Contains(15, SceneCutJudge.Detect(diff, lap, 1, SceneThresholdMap.For(SceneThresholdMap.SliderMax)));
        Assert.Contains(15, SceneCutJudge.Detect(diff, lap, 1, SceneThresholdMap.For(SceneThresholdMap.SliderMin)));
    }

    /// <summary>【真机实测锚点 · 两半:默认档必须全保住 / 顶档的代价必须如实钉住】
    ///
    /// 前半(**硬要求**):默认档(0.30)对三处实测真硬切都必须判切 —— 这就是"用户不动滑块 ⇒
    /// 行为一个字节都不变"在真实数据上的样子(那三处在今天的代码里正是靠这些数值被抓到的);
    /// 同时两处实测运动帧对(镜头推移 = 不是切点)必须**不**判切。
    ///
    /// 后半(**把代价写成规格**):这条滑块的语义就是"调大 = 主动少切" —— 用户要的是"快剪素材别在每一处
    /// 切换都分段,那样每段都要重新启动一次补帧引擎"。所以顶档放掉一部分真切**不是缺陷,是这个旋钮的作用**,
    /// 代价(那些位置照常插帧,可能出现一帧跨切混合)已经写在界面 tooltip 上。
    /// 这里把它钉住,是为了防止后来人把"漏切"当成 bug、又悄悄把方向改回去(那会让用户的问题原样复现)。</summary>
    [Fact]
    public void Default_tier_keeps_every_measured_real_cut_and_the_strict_tier_trades_them_away()
    {
        var def = SceneThresholdMap.For(SceneThresholdMap.DefaultSlider);

        // ---- 默认档 = 今天的行为:三处实测真硬切必须全在 ----
        // (57.11, 1.0, 0.81): onepiece_demo 99→100(目视确认真切;靠"帧差≥50 强切档"抓到,清晰度只掉了 19%)
        Assert.True(SceneCutJudge.IsCut(57.11, 1.0, 0.81, def), "默认档漏掉了实测真硬切 diff=57.11");
        // (86.13, 1.0, 0.40) / (80.01, 1.0, 0.35): syn_3cut 的两处硬切
        Assert.True(SceneCutJudge.IsCut(86.13, 1.0, 0.40, def), "默认档漏掉了实测真硬切 diff=86.13");
        Assert.True(SceneCutJudge.IsCut(80.01, 1.0, 0.35, def), "默认档漏掉了实测真硬切 diff=80.01");
        // 默认档:两处实测运动帧对(镜头推移,清晰度不降反升)不许判切
        Assert.False(SceneCutJudge.IsCut(25.42, 1.0, 1.09, def));
        Assert.False(SceneCutJudge.IsCut(24.10, 1.0, 0.94, def));

        // ---- 顶档的代价(如实钉住,别当 bug 修)----
        var hi = SceneThresholdMap.For(SceneThresholdMap.SliderMax);
        Assert.False(SceneCutJudge.IsCut(57.11, 1.0, 0.81, hi));   // 连帧差闸都过不了(顶档 75)
        Assert.False(SceneCutJudge.IsCut(80.01, 1.0, 0.35, hi));   // 过了帧差闸,但"清晰度只掉 65%"不够顶档要的 80%

        // ---- 这一侧在默认档与顶档永远不许错:运动帧对/普通帧不许被判成切 ----
        // ⚠ 最小档(判据半倍)刻意不在此列:那一档的语义就是"更敏感",25.42 这种贴着默认阈值(25)的
        //   镜头推移本来就会被算成切(上面那条行为用例已用实测数字钉住 lo=10)。要敏感就得接受这个代价。
        foreach (double s in new[] { SceneThresholdMap.DefaultSlider, SceneThresholdMap.SliderMax })
        {
            var th = SceneThresholdMap.For(s);
            Assert.False(SceneCutJudge.IsCut(25.42, 1.0, 1.09, th), $"滑块 {s:0.00} 把镜头推移误判成切");
            Assert.False(SceneCutJudge.IsCut(24.10, 1.0, 0.94, th), $"滑块 {s:0.00} 把镜头推移误判成切");
            Assert.False(SceneCutJudge.IsCut(2.0, 1.0, 1.0, th), $"滑块 {s:0.00} 把普通相邻帧误判成切");
        }

        // ---- 任何档位都不许退化成"什么都不算切"(两个方向都要活着)----
        for (double s = SceneThresholdMap.SliderMin; s <= SceneThresholdMap.SliderMax + 1e-9; s += SceneThresholdMap.SliderStep)
        {
            var th = SceneThresholdMap.For(s);
            // 帧差极小:永远不是切(下限必须真的在起作用)
            Assert.False(SceneCutJudge.IsCut(2.0, 1.0, 1.0, th), $"滑块 {s:0.00} 把普通相邻帧判成切");
            // 极端变化(整帧变化、清晰度几乎归零):必须是切
            Assert.True(SceneCutJudge.IsCut(200, 1.0, 0.02, th), $"滑块 {s:0.00} 把极端变化漏掉了(判定退化)");
            // "帧差极大 = 不必再看清晰度"这条支路任何档位都必须活着(下面喂的是清晰度**上升**的离谱值)
            Assert.True(SceneCutJudge.IsCut(th.StrongDiff + 1, 1.0, 5.0, th), $"滑块 {s:0.00} 的强切档失效了");
        }
    }

    // ================== ③ 不传 thresholds = 逐字等于改动前 ==================

    /// <summary>不传 thresholds(null)= 内置判据。这是"老调用点行为不变"的保证:
    /// VideoService 只在用户勾了「转场识别」时传值,其余路径(含全部既有单测)走的仍是内置常量。</summary>
    [Fact]
    public void Null_thresholds_behaves_exactly_like_the_builtin_constants()
    {
        var cases = new (double diff, double? lp, double? lc)[]
        {
            (24.9, 1.0, 0.1), (25.0, 1.0, 0.59), (25.0, 1.0, 0.61), (30.0, null, null),
            (49.9, 1.0, 1.5), (50.0, 1.0, 1.5), (100.0, 1.0, 2.0), (double.NaN, 1.0, 0.1),
        };
        foreach (var c in cases)
        {
            bool withNull = SceneCutJudge.IsCut(c.diff, c.lp, c.lc);
            bool withBuiltIn = SceneCutJudge.IsCut(c.diff, c.lp, c.lc, SceneCutThresholds.BuiltIn);
            Assert.Equal(withBuiltIn, withNull);
        }
        // Detect 同理:不传 thresholds 与传内置值,结果逐字一致
        var diff = new List<double> { 5, 26, 51, 24, 70 };
        var lap = new List<double> { 1, 1, 0.5, 1, 0.2 };
        Assert.Equal(SceneCutJudge.Detect(diff, lap, 1),
                     SceneCutJudge.Detect(diff, lap, 1, SceneCutThresholds.BuiltIn));
    }

    // ================== ④ 刻度边界与非法输入 ==================

    /// <summary>越界 / NaN / 无穷一律夹进刻度范围,不抛异常 —— 这条链上不该因为一个坏数字让整次处理失败。
    /// 老设置文件里可能是旧滑块时代留下的 0 / 0.05 / 1.0 这类值。</summary>
    [Fact]
    public void Out_of_range_and_invalid_values_are_clamped_not_thrown()
    {
        Assert.Equal(SceneThresholdMap.SliderMin, SceneThresholdMap.Snap(0));
        Assert.Equal(SceneThresholdMap.SliderMin, SceneThresholdMap.Snap(-5));
        Assert.Equal(SceneThresholdMap.SliderMax, SceneThresholdMap.Snap(1.0));
        Assert.Equal(SceneThresholdMap.SliderMax, SceneThresholdMap.Snap(99));
        Assert.Equal(SceneThresholdMap.DefaultSlider, SceneThresholdMap.Snap(double.NaN));
        Assert.Equal(SceneThresholdMap.DefaultSlider, SceneThresholdMap.Snap(double.PositiveInfinity));

        // 极端输入下判据仍然"有意义":帧差阈值不会是 0(那会让每一对帧都算切点)
        var lo = SceneThresholdMap.For(0);
        Assert.True(lo.Diff > 0, "最小档的帧差阈值也必须 > 0(0 = 每一对帧都判切,判定退化)");
        Assert.True(lo.StrongDiff > lo.Diff);
        Assert.InRange(lo.LapDrop, 0.2, 1.0);
        var hi = SceneThresholdMap.For(99);
        Assert.InRange(hi.LapDrop, 0.2, 1.0);
        Assert.True(hi.StrongDiff > hi.Diff);
        // 拉普拉斯下降比的上界 1.0 是有意义的夹取(≤1 = "清晰度没有上升"几乎恒真 ⇒ 这条判据不否决)
        Assert.Equal(1.0, lo.LapDrop);
    }

    /// <summary>日志口径(不参与判定):必须能一眼看出"这次是哪个档、算出来的判据是什么"。
    /// 用户报"我调了滑块到底生效没有"时,这一行是第一手材料。</summary>
    [Fact]
    public void Describe_reports_the_tier_and_the_numbers()
    {
        var s = SceneThresholdMap.Describe(SceneThresholdMap.DefaultSlider);
        Assert.Contains("0.30", s);
        Assert.Contains("默认档", s);
        Assert.Contains("25", s);
        Assert.Contains("50", s);
        // 两端要能看出方向(不然排查时读不出"这次是更严还是更松")
        Assert.Contains("更敏感", SceneThresholdMap.Describe(SceneThresholdMap.SliderMin));
        Assert.Contains("更严格", SceneThresholdMap.Describe(SceneThresholdMap.SliderMax));
    }

    // ================== ⑤ 选项记录的口径(同组参数,一起钉) ==================

    /// <summary>【2026-09-22 用户裁决后的口径】「转场识别」这一组选项现在只有**阈值**一项
    /// (「最短补帧段 60 帧」整块撤掉,理由见 SceneDefaultPolicyTests 里那条反向契约)。
    /// 这里钉住:记录里算出来的判据 = 阈值映射出来的那一组,不多不少 ——
    /// 防止有人"顺手"把别的东西塞回这个记录里(它当初就是被塞进第二项才膨胀的)。</summary>
    [Fact]
    public void Options_carry_exactly_the_threshold()
    {
        var opt = new SceneCutOptions(SceneThresholdMap.DefaultSlider);
        Assert.Equal(SceneThresholdMap.For(SceneThresholdMap.DefaultSlider), opt.Thresholds);
        Assert.Equal(SceneCutThresholds.BuiltIn, opt.Thresholds);      // 默认档 = 内置判据
        var hi = new SceneCutOptions(SceneThresholdMap.SliderMax);
        Assert.Equal(SceneThresholdMap.For(SceneThresholdMap.SliderMax), hi.Thresholds);
        // 记录只有一个位置参数(再塞第二项就会立刻在这里红)
        Assert.Single(typeof(SceneCutOptions).GetConstructors().Single().GetParameters());
    }

    /// <summary>SceneCutOptions 的日志口径必须说清"这次用的是哪个档、算出来的判据是什么"。</summary>
    [Fact]
    public void Options_text_describes_the_tier_and_numbers()
    {
        var def = new SceneCutOptions(SceneThresholdMap.DefaultSlider);
        Assert.Contains("0.30", def.Text);
        Assert.Contains("默认档", def.Text);
        Assert.Contains("25", def.Text);      // 帧差阈值
        Assert.Contains("50", def.Text);      // 强切阈值
        var hi = new SceneCutOptions(SceneThresholdMap.SliderMax);
        Assert.Contains("0.90", hi.Text);
        Assert.Contains("更严格", hi.Text);
        // 撤掉的那一项不许再从文案里冒出来
        Assert.DoesNotContain("最短", def.Text);
        Assert.DoesNotContain("最短", hi.Text);
    }
}
