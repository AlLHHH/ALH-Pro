using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【2026-09-21 用户:"预览播放有延迟还会卡"】这次**先把"能不能测"补上,再谈改**。
///
/// ================== 为什么这个文件先讲证据而不是先讲结论 ==================
/// 用户这条反馈只有一句话。按 systematic-debugging 的 Iron Law,**没有根因不许改**;而我翻了他自己的运行日志
/// (`%LOCALAPPDATA%\ALHPro\diagnostic.log` 的 2026-09-21 23:02~23:04 那一段,共 117 行)之后发现:
/// 现有埋点**给不出可判定的数据** ——
///   · 起播那条 `③状态变→画面开始走` 挂在 `PositionChanged` 上,而**该事件的节拍本身就是 ~250ms**
///     (四次实测读数 234/250/250/250 ms ⇒ 量到的是节拍、不是延迟);而且它在第一次回调就把观察关掉,
///     之后画面真冻住也不会再报 ⇒ 这一项**既量不准也量不全** ✗
///   · 位置回调里的 `左右同步:近 5 秒…` **只在进了同步分支时才打** ⇒ 那次左右对比播放 13 秒,
///     日志里**一条都没有**;"回调没触发"与"走了另一条路"完全分不清 ✗
/// 所以本轮的动作分两半:**(A) 修一个读代码即可证实的错** ;**(B) 把上面两个盲区补成可判定**。
/// 本文件把这两半都钉住 —— 免得下次又变成"猜着改"。
///
/// ================== (A) 证实的错:硬对齐的目标与它自己的漂移判据不同轴 ==================
/// `_cmpPosHandler` 里:漂移**已经**按遮罩模式分了两支(遮罩:两条装的是同一条 0 基点合成片 ⇒ 直接比),
/// 但**硬对齐的目标**当时漏改,仍写成 `_effStart + clipPos` ✗。后果有两个,都在用户这次的环境里成立
/// (他这次 `_effStart` = 2.738s、合成片 4.7s):
///   ① 目标凭空多出 `_effStart` 秒 ⇒ 漂移不减反增,1.5 秒冷却一过就再触发 = **播放中每 1.5 秒硬 seek 一次**;
///   ② `clipPos > 4.7 − 2.738 ≈ 1.96s` 时目标**越过片尾** ⇒ 原片被丢到片尾/夹边 ⇒ 左右再也对不上。
/// 这正是上面那段 2026-09-18 注释自己记下的同一个坑("凭空造出假漂移 → 硬对齐不停触发 → 左边每次都卡 ~1 秒"),
/// 只是当时只改了"比较"、漏了"目标"。
///
/// ================== (B) 补的两个盲区 ==================
///   ① `[地面] 预览播放:` —— 每 5 秒一行,由 150ms 看门狗**无条件**打:位置回调频率 / 两个播放器的
///      状态·位置·时长 / 漂移 / 是否进了同步分支 / 两条各自的倍率。放在看门狗里而不是回调里,
///      正是为了"回调频率为 0"这种情形也能被如实报出来;
///   ② `④点下去→首帧提交渲染` —— 挂 `VideoFrameAvailable`(有帧交给渲染器的直接信号),
///      这才是"跟手"的真值;原来那条 ③ 保留但标注成"量程地板"。</summary>
public class PreviewPlaybackDiagnosticsTests
{
    // ---------- (A) 硬对齐目标必须与漂移判据同轴 ----------

    /// <summary>漂移与硬对齐目标**必须用同一个判据**(遮罩态两条同轴)。这一条直接否掉"只修比较、漏改目标"。</summary>
    [Fact]
    public void Mask_mode_realign_uses_the_same_axis_as_the_drift_it_corrects()
    {
        var code = StripComments(ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs"));

        // ① 漂移:遮罩态直接比(同一文件、同轴);非遮罩态才减 _effStart
        Assert.Contains("double drift = _maskSplitActive", code);
        Assert.Contains("? op.Position.TotalSeconds - clipPos", code);
        Assert.Contains(": op.Position.TotalSeconds - _effStart - clipPos;", code);

        // ② 硬对齐目标:同一个判据(这一行就是本轮修的)
        Assert.Contains("double realignTo = _maskSplitActive ? clipPos : _effStart + clipPos;", code);
        Assert.Contains("op.Position = TimeSpan.FromSeconds(realignTo);", code);

        // ③ 旧写法必须彻底消失 —— 它正是"凭空多出 _effStart 秒"的来源
        Assert.DoesNotContain("op.Position = TimeSpan.FromSeconds(_effStart + clipPos);", code);
        // ④ 日志也要把"这次用的是哪条轴"记下来(排查时一眼看出走的哪支)
        Assert.Contains("遮罩同轴={_maskSplitActive}", code);
    }

    /// <summary>同族检查:看门狗里那处"越界就拉回"也必须按轴分支 —— 否则遮罩态下它算的是一个
    /// **永远不可能超过**的界(`_effStart + _effRealLen` = 7.59s,而合成片只有 4.7s)⇒ 保险形同不存在。
    /// 【本轮只钉事实、不改行为】因为"遮罩态该不该有这条边界"取决于"区间限制"那条未完成项(见交接单),
    /// 不在本轮范围内 —— 钉住它,是为了让下一个人一眼看到"这里也没分轴"。</summary>
    [Fact]
    public void Watchdog_boundary_is_known_to_be_axis_naive_and_must_be_flagged()
    {
        var src = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
        var code = StripComments(src);
        // 事实:看门狗里的越界判据用的是预览轴
        Assert.Contains("if (boundLen > 0.05 && os.Position.TotalSeconds > _effStart + boundLen + 0.15)", code);
        // 而且它算出来的 want 白算了(算完没人用)—— 死变量,顺手钉住,免得被当成"已经用上了"
        Assert.Contains("double want = _effStart + (clipDur > 0.05 ? Math.Min(clipPos, clipDur) : clipPos);", code);
    }

    // ---------- (B) 两个盲区必须被补上 ----------

    /// <summary>起播的"真值"必须能测出**比 ③ 更细的分辨率**,而且不许再用那条**实测不触发**的方案。
    /// 【2026-09-22 自测逮到的自己的坑,照实记】第一版挂的是 `MediaPlayer.VideoFrameAvailable` ——
    /// 跑完整场(四视角 + 播放/暂停 + 慢放)日志里**一条 ④ 都没有** ⇒ 该事件需要
    /// `IsVideoFrameServerEnabled = true`(帧服务器模式),而本工程走的是普通视频面
    /// (`MediaPlayerElement` + SwapChainPanel),置位它会换掉整条渲染路径 —— 不值得为一个埋点动它。
    /// 现在改用 `CompositionTarget.Rendering`(每渲染帧一次 ≈16ms)轮询四条播放器的位置。
    /// 【这条契约要防的】① 又退回"事件驱动 + 200~250ms 地板";② 忘了在测到之后摘掉监听(长期开销)。</summary>
    [Fact]
    public void First_frame_latency_beats_the_250ms_event_floor()
    {
        var src = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
        var code = StripComments(src);

        Assert.Contains("PlayWatchArmFirstFrame()", code);                 // 起播时挂上
        Assert.Contains("Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += _frameTick;", code);
        Assert.Contains("Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= _frameTick;", code);  // 用完必须摘
        Assert.Contains("④点下去→位置开始前进 {now - _playT0} ms", code);
        // 四条播放器一起盯(first-fire 胜出)⇒ 调用点不需要判断"这次该挂谁"
        Assert.Contains("private Windows.Media.Playback.MediaPlaybackSession? FrameWatchSession(int i)", code);
        Assert.Contains("0 => EffectPlayer?.MediaPlayer,", code);
        Assert.Contains("_ => CmpPlayerBottom?.MediaPlayer,", code);
        // 快照"按下那一刻"的位置:只有越过它才算"开始走"
        Assert.Contains("_framePos0[i] = FrameWatchSession(i)?.Position.TotalSeconds ?? -1;", code);
        Assert.Contains("if (p > _framePos0[i] + 0.001)", code);
        // 超时自摘(5 秒后不再占每个渲染帧)
        Assert.Contains("_frameWatchDeadline = _playT0 + 5000;", code);
        // ① 不许再退回那条不触发的事件方案(注释里写"为什么弃用"是允许的)
        Assert.DoesNotContain("mp.VideoFrameAvailable +=", code);
        Assert.DoesNotContain("VideoFrameAvailable", code);
        // ② 旧口径 ③ 必须保留(它证明"状态确实变了")但标明量程地板,不许再被当成延迟数字引用
        Assert.Contains("PositionChanged 200~250ms 节拍限制,只能当地板看", code);
    }

    /// <summary>「地面数据」必须**无条件**每 5 秒一行,并且带上"回调频率"——这是区分
    /// "回调根本没跑"与"跑了但走了另一条路"的唯一手段(用户的日志就是卡在这个盲区上)。
    /// 【两个调用点缺一不可】看门狗在**单播放器**模式下会自己 Stop(那正是用户最常待的视图)⇒ 只在看门狗里打
    /// 就"最需要的地方没数据";只在位置回调里打又永远报不出"回调根本没触发" ⇒ 两边都调、共用一个 5 秒节流,
    /// 并用 `from` 说明是谁打的(这个字段本身就是"哪一侧还活着"的证据)。</summary>
    [Fact]
    public void Playback_ground_data_is_logged_unconditionally()
    {
        var code = StripComments(ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs"));

        // ① 计数器在位置回调的**最前面**自增(在任何分支之前 ⇒ 走哪条路都算得到)
        int ev = code.IndexOf("_cmpPosEvents++;", StringComparison.Ordinal);
        int branch = code.IndexOf("if (!_cmpSingle)", ev, StringComparison.Ordinal);
        Assert.True(ev > 0, "找不到位置回调计数器");
        Assert.True(branch > ev, "计数器必须在进入同步分支**之前**(否则又变成'没进分支就查不到')");

        // ② 共享的发射器:一个 5 秒节流,两个调用点
        Assert.Contains("private void MaybeLogGroundData(string from,", code);
        Assert.Contains("MaybeLogGroundData(\"位置回调\"", code);   // 单播放器时只有它还在跑
        Assert.Contains("MaybeLogGroundData(\"看门狗\"", code);     // 回调死了时只有它还能报 0 次

        // ③ 一行里必须同时有:谁打的 / 回调频率 / 视图与遮罩状态 / 两条播放器的状态与位置 / 漂移 / 是否进分支 / 倍率写入
        foreach (var token in new[] { "[地面] 预览播放({from}):位置回调 {_cmpPosEvents} 次/5s",
                                      "遮罩={_maskSplitActive}", "片段[", "原片[", "漂移 {driftMs:0}ms",
                                      "进同步分支={_cmpSyncBranchEntered}", "倍率写入/跳过 {_rateWrites}/{_rateSkip}",
                                      "上次位置回调 {(_cmpPosLastAt == 0 ? -1 : now - _cmpPosLastAt)} ms 前" })
            Assert.Contains(token, code);
        // ④ 打完清零(否则下一行报的是累计值,看不出"最近 5 秒")
        Assert.Contains("_cmpPosEvents = 0; _rateWrites = 0; _rateSkip = 0;", code);
    }

    /// <summary>遮罩模式不挂控制器这件事必须**写在代码里**(它是本轮查明的既成事实,也是一个结构性隐患):
    /// `_maskSplitActive` 分支会先 return ⇒ 后面那两行恒不可达 ⇒ 左右对比一直是"两个独立时钟"。
    /// 不写清楚的话,下一个人读到这里会以为"控制器管着左右对比"(那正是 §31/§38 推翻过的架构)。</summary>
    [Fact]
    public void The_fact_that_mask_mode_runs_without_the_controller_is_documented()
    {
        var code = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
        // 【查**原始**源码,不去注释】这一条要钉的恰恰是"注释里必须写清楚"这件事本身
        Assert.Contains("以下两行 **当前不可达**", code);
        Assert.Contains("遮罩模式(左右对比)从来没有", code);
        Assert.Contains("⚠ 不可达:见上方说明", code);
        // 而且它必须说清"为什么留着"(是一次用户在场才能验的撤回留下的挂点,不是废码)
        Assert.Contains("用户在场", code);
    }

    /// <summary>【用户 2026-09-21 二次澄清后的重点】症状是「按下播放/暂停后要等一下画面才动」,且**四个视图都有**
    /// ⇒ 病根必然在四条分支**共有**的那几步上。所以起播路必须能**分步计时**,而且每一步都要带着
    /// "距按下那一刻的累计毫秒"(用户感知的就是这个累计值)。
    /// 【为什么这条最要紧】四条起播分支共有、且已知代价最大的一步 = `ApplyCmpRateToAll(false)`
    /// (写 PlaybackRate 会让播放管线重定时,本文件实测"换一次倍率画面冻结 ~455ms"),而它正好落在 `Play()` **之前**。
    /// 之前日志只在"用户主动换倍率"时才记一行,**起播路上的这次写入完全不可见** ⇒ 无从判断是不是它。
    /// 现在:写/跳过都计数、写入单独记一行(带写耗时 + 起播累计),地面数据里也带上写入/跳过次数。</summary>
    [Fact]
    public void Play_path_is_step_timed_and_rate_writes_are_visible()
    {
        var code = StripComments(ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs"));

        // ① 分步计时器:按"距按下的累计毫秒"记,>30ms 才记(不刷屏)
        Assert.Contains("private void PlayWatchStep(string what)", code);
        Assert.Contains("· 起播分步[{what}] 累计 {d} ms", code);
        Assert.Contains("if (d > 30) AppLogger.Info($\"[性能] {_playTag}:· 起播分步", code);
        // ② 关键几步必须真的插上(按钮反馈 / 片尾归零 / 补倍率 / Play 返回)—— 少一步就分不清是谁吃的
        Assert.Contains("PlayWatchStep(\"单播放器·已给按钮反馈\")", code);
        Assert.Contains("PlayWatchStep(\"单播放器·片尾归零(seek)\")", code);
        Assert.Contains("PlayWatchStep(\"单播放器·补倍率(可能触发管线重定时)\")", code);
        Assert.Contains("PlayWatchStep(\"单播放器·Play() 返回\")", code);
        Assert.Contains("PlayWatchStep(\"左右对比·补倍率(可能触发管线重定时)\")", code);
        Assert.Contains("PlayWatchStep(\"左右对比·两条 Play() 返回\")", code);

        // ③ 倍率写入必须"写/跳过都可见",而且写入要带写耗时 + 起播累计
        Assert.Contains("_rateSkip++;", code);
        Assert.Contains("_rateWrites++;", code);
        Assert.Contains("[性能] 倍率写入 {MpName(mp)} {before:0.###}→{_cmpRate:0.###} · 写耗时 ", code);
        Assert.Contains("起播累计 {(_playWatch ? Environment.TickCount64 - _playT0 : -1)} ms", code);
        // ④ 地面数据里带上判据(写入次数 >0 就说明起播路上真的发生过管线重定时)
        Assert.Contains("倍率写入/跳过 {_rateWrites}/{_rateSkip}", code);
        // ⑤ 认播放器的名字(日志要能一眼看出是哪一条被写了)
        Assert.Contains("private string MpName(Windows.Media.Playback.MediaPlayer? mp)", code);
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
