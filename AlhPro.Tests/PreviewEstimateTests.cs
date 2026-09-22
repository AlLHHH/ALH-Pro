using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace AlhPro.Tests;

/// <summary>「预览效果」这一轮的契约(2026-09-21 用户反馈:**"预览要等很久才开始"**)。
///
/// 【用户的两条明确要求,本文件就是它们的落地证明】
///   ① **不许降档** —— 用户点名"不要低质预览"。预览必须与「开始处理」走**同一条流水线、同一份参数快照**,
///      只跑用户点中的那一段。所以这里钉的是一条**结构**契约:`VideoService.ProcessVideoAsync` 在界面侧
///      **只有一处调用**,而且预览与正式处理对它的参数**只有三处分叉**(区间起止 / 允许帧数偏少 / 不接受暂停)
///      —— 多出第四处,就说明有人给预览开了小灶(那正是"预览看到的 ≠ 全片得到的"这类静默 bug 的入口)。
///   ② **要给"预计用时"** —— 等是你免不了的(真跑就要等),但"要等多久"必须提前说。上一版没有任何数字,
///      用户面对的就是一个纯转圈;实测那次 3 秒素材跑了 **532 秒**,而他事先完全不知道。
///
/// 【为什么第二组用"源码接线"而不是跑一遍】要验证"预览前显示了预计"得真起 WinUI 点一遍 + 跑一次真素材
/// (几十分钟),本仓库对这类 UI 组合条件的既定手法就是把接线钉住(同 SceneSwitchCoverageTests /
/// TempSpaceGateTests)。数字侧的口径已由 <see cref="EtaTextTests"/> 单独钉死。</summary>
public class PreviewEstimateTests
{
    /// <summary>① 预览开跑前必须报「预计用时」,而且用的是**与「开始处理」同一个**估算函数、
    /// **同一批参数快照**、**本次真正跑的区间长度**(不是整片时长)。</summary>
    [Fact]
    public void Preview_announces_estimated_time_before_it_runs()
    {
        var cs = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
        var code = StripComments(cs);

        // ① 必须复用「开始处理」那条路上同一个估算入口(同一个函数名,不是在预览里另写一套公式)
        Assert.True(Count(code, @"VideoService\.EstimateProcessSeconds\(") >= 2,
            "预览的预计用时必须复用 VideoService.EstimateProcessSeconds(与「开始处理」/诊断卡片同一个入口)");
        // 参数口径必须是同一批快照局部量(逐字相同的那一段实参),否则"预计"和"实际跑"就是两套口径
        Assert.True(Count(code, @"up, upscaleShrink1x \? 2\.0 : scale, engine, interp, interpScale, dedupOn,") >= 2,
            "预览估算必须与「开始处理」用同一批参数快照(同一段实参)");

        // ② 按**区间长度**折算:pv.Length(不是 item.Duration / 整片)
        int estPos = code.IndexOf("PreviewDeckStatus($\"正在生成预览({EffTime(pv.Start)} 起 {pv.Length:0.#} 秒):{note}\"",
            StringComparison.Ordinal);
        Assert.True(estPos > 0, "找不到「预览预计用时」那段提示");
        var estBlock = code.Substring(Math.Max(0, estPos - 1800), 1800);
        Assert.Contains("double dur0 = pv.Length;", estBlock);           // 只估这一段
        Assert.Contains("VideoService.EstimateProcessSeconds(dur0, fps0, w0, h0,", estBlock);
        Assert.Contains("pv != null", estBlock);                        // 只在预览这条路走(不打扰正式处理)

        // ③ 必须**在开跑之前**报出来(报在后面就等于没报):估算块在唯一那个流水线调用之前
        int call = code.IndexOf("await Task.Run(() => VideoService.ProcessVideoAsync(", StringComparison.Ordinal);
        Assert.True(call > 0, "找不到流水线调用点");
        Assert.True(estPos < call, "预计用时必须在开始跑之前显示(报在跑之后等于没报)");

        // ④ 三处都要写:状态行(立刻可见)+ 播放器中央提示(进度消息覆盖不掉)+ 日志(跑完能对账)
        Assert.Contains("PreviewDeckStatus($\"正在生成预览({EffTime(pv.Start)} 起 {pv.Length:0.#} 秒):{note}\"", code);
        Assert.Contains("EffectPlayerHint.Text = $\"正在生成预览…\\n{note}\"", code);
        Assert.Contains("Log($\"预览{note}:", code);
        // 数字文案统一走 Core 的纯函数(秒/分钟/小时三档 + 非法值返回空串),不在界面里另写一套
        Assert.Contains("AlhPro.Core.EtaText.Duration(est0)", code);
        // 估不出来就不显示(宁可只有原来那句"正在生成预览",也不给一个假数字)
        Assert.Contains("if (est0 > 0.5)", code);
    }

    /// <summary>② 预览**不降档**:整条流水线只有一处调用,预览与正式处理之间**只有三处分叉**。
    /// 这一条同时否掉两种退化:① 给预览单开一条"低质快跑"的路径;② 顺手让预览少跑一个阶段
    /// (比如不开超分/降倍率)——那会让用户"预览看到的"与"全片得到的"不一致。</summary>
    [Fact]
    public void Preview_shares_one_pipeline_with_the_real_run_and_never_downgrades()
    {
        var cs = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
        var code = StripComments(cs);

        // ① 流水线调用**只有一处** —— 不存在"预览专用"的第二条路径
        Assert.Equal(1, Count(code, @"VideoService\.ProcessVideoAsync\("));

        // ② 参数里凡是按预览分叉的地方,只能有这四行(`pv != null` 出现 4 次)
        int start = code.IndexOf("await Task.Run(() => VideoService.ProcessVideoAsync(", StringComparison.Ordinal);
        const string tail = "pauseWait: pv != null ? null : PauseWaitAsync));";
        int end = code.IndexOf(tail, start, StringComparison.Ordinal);
        Assert.True(start > 0 && end > start, "找不到流水线调用块");
        var callBlock = code.Substring(start, end - start + tail.Length);
        Assert.Equal(4, Count(callBlock, "pv != null"));
        // 四处逐条点名(顺序即语义):区间起点 / 区间终点 / 允许帧数偏少 / 不接受暂停
        Assert.Contains("pv != null ? pv.Start : tStart,", callBlock);
        Assert.Contains("pv != null ? pv.Start + pv.Length : tEnd,", callBlock);
        Assert.Contains("allowFewFrames: allowFew || pv != null,", callBlock);
        Assert.Contains("pauseWait: pv != null ? null : PauseWaitAsync));", callBlock);
        // 关键参数**必须**是同一批快照变量(不许出现"预览时换成低倍率/换模型"这类写法)
        Assert.Contains("engine, model, scale, up, interp, itemFps, interpScale, targetFps,", callBlock);
        Assert.Contains("anime4k1x: anime4k1x,", callBlock);
        Assert.Contains("upscaleShrink1x: upscaleShrink1x,", callBlock);
    }

    /// <summary>③ 预览**运行时要有逐帧进度**:流水线的进度消息必须原样落到预览面板那一行。
    /// 上一轮已经补齐了各阶段的逐帧行(拆帧/超分/补帧/1x 缩回/整理帧),这里把"这些消息真的会显示出来"
    /// 这一段接线钉住 —— 消息有了但没接到面板上,是同一类静默缺陷。</summary>
    [Fact]
    public void Preview_shows_per_frame_progress_from_every_stage()
    {
        var cs = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
        var code = StripComments(cs);
        // 预览的进度接收器:把消息原样写进预览页状态行(不刷主界面进度条,免得用户以为全片在跑)
        Assert.Contains("PreviewDeckStatus(t.msg)", code);
        Assert.Contains("EffectProgress.Value = Math.Clamp(t.pct, 0, 100)", code);
        // 预览用的就是预览自己的进度通道(不是主界面那个兜底 Progress)
        Assert.Contains("IProgress<(int pct, string msg)> progress = pv != null ? pv.Progress", code);

        // 各阶段的逐帧行必须都在(缺一条 = 那个阶段在预览里就是纯转圈)
        var svc = ReadRepoFile("ImgUpscalerUI", "VideoService.cs");
        Assert.Contains("拆帧 已处理 {n} 帧 / 共 {total} 帧", svc);                       // 拆帧
        Assert.Contains("超分 已处理 {doneFrames} 帧 / 共 {total} 帧", svc);              // 超分
        Assert.Contains("补帧 第 {gOut - 1} 帧 / 共 {totalFrames} 帧", svc);              // 补帧(RIFE 目录轮询)
        Assert.Contains("补帧 第 {dn} 帧 / 共 {totalOut} 帧", svc);                       // 补帧(ONNX 稳定引擎)
        Assert.Contains("1x 修复 缩回中 {d} 帧 / 共 {shFiles.Length} 帧", svc);          // 1x 缩回
        Assert.Contains("整理帧(JPG) 第 {d} / {pngs.Length} 帧", svc);                   // 合帧前的 JPG 整理
        // 【任务 ④ 的结论落在这里】去重判定是 **C# 逐帧循环**(不是 ffmpeg 滤镜链),而且**已经有**逐帧进度:
        Assert.Contains("去重分析", svc);                                                 // 两条检测路径的 stage 名
        Assert.Contains("第 {i} 帧 / 共 {_total} 帧(已判重 {deleted} 帧", svc);          // DedupHeartbeat 的逐帧心跳
    }

    private static int Count(string text, string pattern)
        => Regex.Matches(text, pattern).Count;

    /// <summary>去掉整行注释(只查活代码):沿革说明必须能留在源码里,不能因为"注释里提到了旧名字"就红。</summary>
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
