using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>「转场识别」开关的**覆盖面与判据唯一性**契约(2026-09-15 用户明确要求)。
///
/// 【两条硬要求,都在这里钉住】
///   ① **开关管所有路径**:勾上 → 普通分段路径与"按真实时间戳排帧"那条路都做切点保护;不勾 → 都不做。
///      (历史上出现过"某一条路忽略开关":那条路无条件检测切点 ⇒ 用户"关了却还有保护"。)
///   ② **切点判据只能有一套实现**:判据 = `AlhPro.Core.SceneCutJudge`,度量 = `Core.SceneCutMetrics`;
///      全流程**只采样一次、只判定一次**,结果存进共享表给两条路用。
///      (历史上两条路各自调了一次 `ComputeSceneCutMetricsAsync` —— 同一份判据、两次全片解码、两套口径。)
///
/// 这类"某处悄悄走另一套"的缺陷编译与运行都不报错,只能把源码接线钉住。</summary>
public class SceneSwitchCoverageTests
{
    /// <summary>① **判据只有一套、只算一次**:全流程只有一处 `await ComputeSceneCutMetricsAsync(` 与一处
    /// `SceneCutJudge.Detect(`(都在主流程的转场检测里);"按真实时间戳排帧"那条路**不再自己检测**,
    /// 而是吃主流程传进去的共享切点表 `sceneCutPairs`。</summary>
    [Fact]
    public void One_judge_one_sampling_shared_by_both_paths()
    {
        var svc = ReadRepoFile("ImgUpscalerUI", "VideoService.cs");

        // 采样与判定各只有一处(方法【定义】不算,所以按 "await …(" 数)
        Assert.Equal(1, Regex_Count(svc, "await ComputeSceneCutMetricsAsync\\("));
        Assert.Equal(1, Regex_Count(svc, "SceneCutJudge\\.Detect\\("));

        // 共享切点表:声明在 InterpStageAsync 局部函数之前(否则局部函数捕获不到),并且在 3) 块里被填充
        int decl = svc.IndexOf("var sceneCutPairs = new System.Collections.Generic.List<int>();", StringComparison.Ordinal);
        int interpFunc = svc.IndexOf("async Task InterpStageAsync(", StringComparison.Ordinal);
        Assert.True(decl > 0 && interpFunc > 0 && decl < interpFunc, "共享切点表必须在 InterpStageAsync 之前声明");
        Assert.Contains("sceneCutPairs.Clear();", svc);
        Assert.Contains("foreach (var c in cuts) sceneCutPairs.Add(c - 1);", svc);   // c=i+1 → i 口径换算

        // "按真实时间戳排帧"那条路:签名收共享表、**不再**有采样/判据调用
        var flattenBody = ExtractMember(svc, "private static async Task<(int written, int cuts, int forcedCopies, bool anyBlack)> FlattenTimelineAsync(");
        Assert.Contains("IReadOnlyList<int> cutPairs", flattenBody);
        Assert.DoesNotContain("ComputeSceneCutMetricsAsync", flattenBody);
        Assert.DoesNotContain("SceneCutJudge.Detect", flattenBody);
        Assert.Contains("var cuts = cutPairs;", flattenBody);
        // 调用点必须把共享表传进去(不许在这里又造一份)
        Assert.Contains("sceneCutPairs,", svc);

        // 普通分段路径:整块检测挂在同一个开关之下
        int gate = svc.IndexOf("if (sceneThreshold is > 0)", StringComparison.Ordinal);
        Assert.True(gate > 0);
        int detect = svc.IndexOf("AlhPro.Core.SceneCutJudge.Detect(", StringComparison.Ordinal);
        Assert.True(detect > gate, "普通路径的判据调用必须在开关判断之后");
    }

    /// <summary>② **开关状态在日志里看得见**(用户明确要求:`转场识别=0.30` / `关`)。
    /// 钉:任务参数行、切成片的合成完成行、以及切点对齐行都带开关/切点信息。</summary>
    [Fact]
    public void Switch_state_is_visible_in_the_logs()
    {
        var view = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
        Assert.Contains("$\"转场识别={(sceneThreshold != null ? $\"{sceneThreshold:0.00}\" : \"关\")} | \"", view);

        var svc = ReadRepoFile("ImgUpscalerUI", "VideoService.cs");
        Assert.Contains("转场识别={(sceneThreshold is > 0 ? $\"开({sceneThreshold.Value:0.00})\" : \"关\")}", svc);
        Assert.Contains("转场识别:切点 {cuts.Count} 处", svc);                     // 普通路径:切点处数
        Assert.Contains("时间轴:切点对齐用主流程同一份判据", svc);                 // 排帧那条路:同一份判据
    }

    /// <summary>③ **开关必须还在、可开可关**(用户定调:不要做成内建强制)。</summary>
    [Fact]
    public void The_switch_stays_user_controllable()
    {
        var xaml = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml");
        int i = xaml.IndexOf("x:Name=\"SceneCheck\"", StringComparison.Ordinal);
        Assert.True(i > 0, "XAML 里找不到 SceneCheck(开关被删了?)");
        int end = xaml.IndexOf('>', i);
        var tag = xaml.Substring(i, end - i);
        Assert.Contains("Checked=\"Options_Changed\"", tag);
        Assert.Contains("Unchecked=\"Options_Changed\"", tag);   // 取消勾选必须能回写设置

        var view = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
        Assert.Contains("SceneCheck.IsChecked = d.Scene;", view);                 // 恢复用户存的值
        Assert.Contains("Scene = SceneCheck.IsChecked == true,", view);           // 存盘
        Assert.Contains("public bool Scene { get; set; }", view);                 // 字段还在(老设置兼容)
    }

    /// <summary>④ 开关的 ToolTip 必须把**开/关两种后果**都讲清(用实测数据说)。</summary>
    [Fact]
    public void The_tooltip_states_both_consequences_with_measured_numbers()
    {
        var xaml = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml");
        int i = xaml.IndexOf("x:Name=\"SceneCheck\"", StringComparison.Ordinal);
        Assert.True(i > 0);
        int end = xaml.IndexOf("/>", i, StringComparison.Ordinal);
        var block = xaml.Substring(i, end - i);

        Assert.Contains("取消勾选", block);      // 关掉的后果
        Assert.Contains("鬼影", block);          // 关掉会出鬼影
        Assert.Contains("少插一帧", block);      // 开的代价
        Assert.Contains("19.72", block);         // 实测:不勾时的混合帧帧差
        Assert.Contains("82.62", block);         // 实测:勾上后的硬切帧差
        Assert.Contains("179 帧", block);        // 实测:帧数/时长守恒
        // 【2026-09-15 转场阈值滑块删除后的提示文字】必须写明"阈值是内置的、不需要调"(用户指定的措辞)
        Assert.Contains("内置", block);
        Assert.Contains("不需要调", block);
    }

    /// <summary>⑤ **"按真实时间戳排帧"只对 VFR 源自动生效**(用户最终裁决:CFR 走常规路径、不做时序重采样;
    /// 判不出 VFR 时按 CFR 保守处理),并且每任务打印一行 VFR/CFR 结论。</summary>
    [Fact]
    public void Real_timestamp_resampling_only_for_VFR_sources()
    {
        var svc = ReadRepoFile("ImgUpscalerUI", "VideoService.cs");

        // 判据复用已有的源级 VFR 结论(vfrPassthrough ← ProbeVfrAsync 的时间戳判据),不自造第二套
        Assert.Contains("bool sourceIsVfr = vfrPassthrough;", svc);
        Assert.Contains("if (plan.Flatten && sourceIsVfr && smoothTimeline)", svc);
        // 两道门:既要"源是 VFR",又要时长表里真的检出缺口(plan.Flatten)
        Assert.Contains("AppLogger.Info(sourceIsVfr", svc);
        // 每任务一行的两句结论(用户指定的措辞)
        Assert.Contains("时间轴:源为 VFR → 按真实时间戳排帧", svc);
        Assert.Contains("时间轴:源为 CFR → 常规处理", svc);
    }

    private static int Regex_Count(string text, string pattern)
        => System.Text.RegularExpressions.Regex.Matches(text, pattern).Count;

    /// <summary>从源码里截出一段成员(从签名起到下一个成员注释`/// <summary>`为止)。</summary>
    private static string ExtractMember(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start > 0, "找不到成员签名: " + signature);
        int end = source.IndexOf("\n    /// <summary>", start + signature.Length, StringComparison.Ordinal);
        if (end < 0) end = Math.Min(source.Length, start + 20000);
        return source.Substring(start, end - start);
    }

    /// <summary>从测试输出目录往上找仓库根,再读指定文件。</summary>
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
