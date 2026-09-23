using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【2026-09-23 用户:"左右预览不协调"】上一轮(见 PreviewPlaybackDiagnosticsTests)只做到了
/// "把盲区补成可判定";这一轮按那些判定数据**改代码**。本文件把改动的**每条契约**钉住,
/// 免得下次重构时被无声回退 —— 尤其是"停住态对齐"这条:它修的是用户唯一给出量化的那一段
/// (两份诊断包 92 条看门狗样本里,停住态漂移 -1416ms ~ +546ms,而播放中只有 2 条样本 = 208ms)。
///
/// ================= 本轮查明的四件事(逐条对应下面的用例) =================
/// ① 「位置回调 N 次/5s」的统计口径:计数器在回调**最前面**自增,而**暂停态的 MediaPlayer 不产生
///    PositionChanged** ⇒ `0 次/5s` 在停住态是**正常现象,不是异常**(上一轮已因此撤回过一次误判)。
///    本轮不再拿它当告警,只保留"如实上报"。
/// ② 「进同步分支」的具体动作:那段代码的前提是 `clipPlaying && !clipAtEnd` —— **停住态/片尾时只做
///    `origMp.Pause()`,位置一个像素都不纠**。于是"停住态漂移"永远没人管。本轮加了收口点。
/// ③ 「单播放器=False 遮罩=True」**不是 bug**:2026-09-18/19 起「左右对比」的架构就是
///    Topaz 式遮罩(两条装同一条合成片、线=上层裁切),`_cmpSingle` 只在"有烘焙分割片"时才为 true,
///    那条路已被放弃 ⇒ 日志长期 `单播放器=False` 是**设计**,不是故障。但它**没有共享时钟**,
///    所以"消灭第二条时钟"的初衷没达成 —— 本轮的做法是"在每个入口把两条拉回同一时刻"。
/// ④ 定位合并器是**全局一个槽位**(四条播放器共用)⇒ 遮罩模式"两条一起定位"时第二条被覆盖/
///    被丢弃 ⇒ `片段[停 0] · 原片[停 2.834] · 漂移 2834ms`。本轮改成按播放器分槽(见 SeekCoalescerTests)。</summary>
public class PreviewPairSyncTests
{
    // ---------- ④ 定位合并:必须按播放器分槽 ----------

    /// <summary>旧的三个全局字典字段必须**彻底消失**,而且新实现必须真的被用上。</summary>
    [Fact]
    public void Seek_coalescing_is_per_player_not_one_global_slot()
    {
        var code = StripComments(ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs"));

        // ① 旧字段一个都不许残留(它们就是"一个槽位给四条播放器"的实现)
        Assert.DoesNotContain("_seekWantMp", code);
        Assert.DoesNotContain("_seekWantSec", code);
        Assert.DoesNotContain("_seekInFlight", code);
        Assert.DoesNotContain("_seekBeforePos", code);

        // ② 新实现:Core 里的分槽合并器
        Assert.Contains("private readonly AlhPro.Core.SeekCoalescer _seeks = new();", code);
        Assert.Contains("AlhPro.Core.SeekCoalescer.Decision.ApplyNow", code);
        Assert.Contains("_seeks.TakeAnyWant()", code);
        Assert.Contains("_seeks.NotePosition(", code);

        // ③ 【寄存之后必须起定时器】旧代码只在 RequestSeek 里起 ⇒ ApplySeekNow 的寄存分支把目标丢了;
        //    这一条直接防止"暂停态(没有位置回调)目标永久丢失"重新出现
        Assert.Contains("if (d == AlhPro.Core.SeekCoalescer.Decision.ApplyNow) DoApplySeek(mp, seconds, verify);",
            code);
        Assert.Contains("else StartSeekTimer();", code);

        // ④ 落地判定必须问"这条播放器"(旧签名只有一个 pos,因为当时只有一个全局槽)
        Assert.Contains("private void NoteSeekMaybeLanded(Windows.Media.Playback.MediaPlayer? mp, double pos)", code);
        Assert.Contains("NoteSeekMaybeLanded(_cmpPosMpCache, clipPos)", code);
        Assert.Contains("NoteSeekMaybeLanded(_barMpCache, pos)", code);

        // ⑤ 先整批取走再发(就地"取一条发一条"会在'还在飞'时把目标重新寄存 ⇒ 死循环)
        Assert.Contains("private void FlushPendingSeeks()", code);
        Assert.Contains("while ((w = _seeks.TakeAnyWant()) != null)", code);
    }

    // ---------- ② 停住态/片尾必须有收口点 ----------

    /// <summary>★ 本轮的核心修复:停住态(两条都没在播)时差得多了就主动对齐一次,
    /// 而且带三条自限(拖动中不插手 / 刚对齐过不再来 / 差 <120ms 不动)+ 退避。</summary>
    [Fact]
    public void Idle_paused_pair_is_realigned_with_self_limits()
    {
        var code = StripComments(ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs"));

        Assert.Contains("private void RealignMaskIfIdle(double pe, double po, bool clipPlaying, bool origPlaying)", code);
        // 看门狗里必须真的调它(位置回调在暂停态不触发 ⇒ 那里是唯一能发现"停住态不同步"的地方)
        Assert.Contains("RealignMaskIfIdle(clipPos, os.Position.TotalSeconds, clipPlaying, origPlaying);", code);
        // 自限三条
        Assert.Contains("if (clipPlaying || origPlaying) return;", code);
        Assert.Contains("if (_cmpSeekDrag || _ptlDrag != 0 || _dividerDrag) return;", code);
        Assert.Contains("if (Math.Abs(pe - po) < 0.12) { _maskAlignTries = 0; return; }", code);
        // 退避:连续 4 次没纠好就退到 10 秒一次(免得变成"每 0.7 秒两条各 seek 一次"的隐性开销)
        Assert.Contains("if (_maskAlignTries >= 4 && since < 10000) return;", code);

        // 对齐本身必须是**带校验的定位**(裸设 Position + 立刻 Play = 恒定偏移,本文件 2026-09-17 已判定过)
        Assert.Contains("private async Task AlignMaskPairAsync(bool toMin, bool thenPlay, string why)", code);
        // 【2026-09-23 并行】两条**同时**发起定位再一起等(串行 = 等待叠加 + "一条已到位、另一条还在原处"
        // 的可见窗口翻倍;实测抓到过片尾重播那一瞬的 3000ms 错位)
        Assert.Contains("var seekE = Math.Abs(pe - anchor) > 0.03 ? SeekAndVerifyAsync(e, anchor, 3) : Task.CompletedTask;", code);
        Assert.Contains("var seekO = Math.Abs(po - anchor) > 0.03 ? SeekAndVerifyAsync(o, anchor, 3) : Task.CompletedTask;", code);
        Assert.Contains("await Task.WhenAll(seekE, seekO).ConfigureAwait(true);", code);
        Assert.DoesNotContain("await SeekAndVerifyAsync(e, anchor, 3).ConfigureAwait(true);", code);
        Assert.DoesNotContain("await SeekAndVerifyAsync(o, anchor, 3).ConfigureAwait(true);", code);
        // 遮罩模式两条同轴 ⇒ 目标就是同一个秒数,**绝不加 _effStart**(加了就是凭空造出几秒的假偏差)
        Assert.Contains("double anchor = toMin ? Math.Min(pe, po) : pe;", code);
        // 片尾重播那条路(ReplayFromStartAsync)同样并行归零
        Assert.Contains("var seekE = SeekAndVerifyAsync(e, 0, 6);", code);
        Assert.Contains("var seekO = SeekAndVerifyAsync(o, 0, 6);", code);
        // 防重入:看门狗每 150ms 一跳,而一次对齐要 await 两次"带校验的定位"⇒ 没有这道闸门会起第二次
        // 对齐、两个对齐互相 seek(画面来回跳)✗;而且 `_maskAlignAt` 必须在**开头**就盖时间戳(冷却才生效)
        Assert.Contains("if (_maskAlignBusy) return;", code);
        Assert.Contains("finally { _maskAlignBusy = false; _maskAlignAt = Environment.TickCount64; }", code);
    }

    /// <summary>"两条可能停在不同时刻"的三个入口都要收口:起播前 / 暂停后 / 换倍率后。
    /// 【为什么每个都要】起播前不对齐 = 把上一次的偏差带进这一次;暂停后不对齐 = 那个偏差**永远留着**
    /// (位置回调不触发);换倍率后不对齐 = 写 PlaybackRate 让两条各自重定时 ⇒ 实测立刻多出 208ms。</summary>
    [Fact]
    public void Every_entry_point_that_can_leave_the_pair_apart_aligns()
    {
        var code = StripComments(ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs"));

        Assert.Contains("_ = AlignMaskPairAsync(toMin: false, thenPlay: true, \"起播前\");", code);
        Assert.Contains("_ = AlignMaskPairAsync(toMin: true, thenPlay: false, \"暂停后\");", code);
        Assert.Contains("if (_maskSplitActive) _ = AlignMaskPairAsync(toMin: true, thenPlay: false, \"换倍率后\");", code);

        // 起播那条路**不许**再退回"两条各自 Play()"(没有对齐就没有"左右协调"可言)
        Assert.Contains("PlayWatchStep(\"左右对比·补倍率(可能触发管线重定时)\");", code);
        Assert.Contains("PlayWatchStep(\"左右对比·两条 Play() 返回\");", code);
    }

    /// <summary>旧的那个"暂停后对齐"是**死方法**(定义了但没人调)⇒ 本轮把它换成真正被调用的实现。
    /// 这条钉的是"别再留一个看起来在管、实际从不执行的方法"。</summary>
    [Fact]
    public void The_old_dead_AlignPausedAsync_is_gone()
    {
        var code = StripComments(ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs"));
        Assert.DoesNotContain("private async Task AlignPausedAsync()", code);
    }

    /// <summary>【2026-09-23 顺手修的既有 bug】进「视频处理」页那一刻的那条日志会抛
    /// `NullReferenceException 0x80004003`(控件还没进可视树,写 TextBlock.Text 就抛),
    /// 每次启动一条 WARN,而且**那一行日志在界面日志框里是丢的**。
    /// 证据:日志里 `进入页面:视频处理` → `倍率切换:…` → `⚠ 界面刷新失败…视频日志追加/自动滚动`。
    /// 修法:进可视树之前先缓冲,Loaded 后补齐 ⇒ 不再抛、内容也不丢。</summary>
    [Fact]
    public void Log_lines_written_before_the_page_is_loaded_are_buffered_not_lost()
    {
        var code = StripComments(ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs"));
        Assert.Contains("private readonly List<string> _pendingLogLines = new();", code);
        Assert.Contains("if (!_uiLogReady)", code);
        Assert.Contains("lock (_pendingLogLines) { if (_pendingLogLines.Count < 50) _pendingLogLines.Add(msg); }", code);
        Assert.Contains("private void FlushPendingLogLines()", code);
        // Loaded 里必须**在最前面**置位并补齐(下面有 `if (_dupRefreshRun) return;` 的提前返回路径)
        int loaded = code.IndexOf("this.Loaded += async (_, _) =>", StringComparison.Ordinal);
        int flag = code.IndexOf("_uiLogReady = true;", loaded, StringComparison.Ordinal);
        int flush = code.IndexOf("try { FlushPendingLogLines(); } catch { }", loaded, StringComparison.Ordinal);
        int earlyReturn = code.IndexOf("if (_dupRefreshRun) return;", loaded, StringComparison.Ordinal);
        Assert.True(flag > loaded && flush > flag, "Loaded 里要置位并补齐");
        Assert.True(earlyReturn > flush, "置位与补齐必须在提前 return **之前**(否则第二次 Loaded 会漏)");
        // 原来的 try/catch 兜底保留(它是"界面刷新绝不许把任务带崩"的那道线,不许删)
        Assert.Contains("catch (Exception ex) { NoteUiRefreshFailure(\"视频日志追加/自动滚动\", ex); }", code);
        // 滚动那段不能被改坏(本轮误删过一次,这条用来兜住结构)
        Assert.Contains("App.UiBreadcrumb = \"日志滚动到底(延迟执行)\";", code);
    }

    /// <summary>★★ 本轮（2026-09-23 用测试缝 ALH_TEST_VIDEO 跑到一手现场后）找到的**真根因**：
    /// 给 `MediaPlayerElement.Source` 赋值会**创建/替换**它的 MediaPlayer，而媒体回调（位置同步、地面数据）
    /// 不能读控件属性（媒体线程上读会抛 0x8001010E）⇒ 只能读 UI 线程缓存的引用。
    /// **装片之后没重抓引用 = 那些回调从此对着孤儿会话说话**：
    /// 现场证据（同一行日志里三个信号同时出现）：画面两侧都在渲染、`片段[播 2.193/3s]`，
    /// 而 `原片[停 0/0s] · 漂移 -2193ms · 进同步分支=False`，且整段播放**一次硬对齐都没触发**
    /// （门槛 0.25s）⇒ 同步那段根本没工作。
    /// 为什么只有上层中招：下层的 `LoadEffectSource` 里跟了 `SetCompareSync(...)`（会顺手重抓），
    /// 而"仅原片"那条路恰恰不会走到它。</summary>
    [Fact]
    public void Player_references_are_recaptured_after_every_source_change()
    {
        var code = StripComments(ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs"));
        // 统一的重抓入口 + 判据(只看症状,不靠引用相等)
        Assert.Contains("private void RecacheMediaRefs(string why)", code);
        Assert.Contains("private bool OrigRefLooksStale(Windows.Media.Playback.MediaPlayer? liveOrig,", code);
        Assert.Contains("return liveDur > 0.05 && cachedDur <= 0.05;", code);
        // 每个装片入口后面都必须跟一次重抓 —— 少一处就会重演"只有一条被对齐"的静默失效
        foreach (var site in new[] { "RecacheMediaRefs(\"遮罩装片后(上层刚拿到 Source)\");",
                                     "RecacheMediaRefs(\"成片条装片后\");",
                                     "RecacheMediaRefs(\"对比片装进上层后\");",
                                     "RecacheMediaRefs(\"裁剪页装原片后\");",
                                     "RecacheMediaRefs(\"没结果时的对比视图装原片后\");",
                                     "RecacheMediaRefs(\"两文件并排装上原片后\");" })
            Assert.Contains(site, code);
        // 回调是**按会话**订阅的 ⇒ 换会话必须"先摘再挂"(SetCompareSync 在已订阅时会提前返回)
        Assert.Contains("SetCompareSync(false);\n            if (_compareMode) SetCompareSync(true);", code);
        // 看门狗永久对账(限流 2 秒),防这类 bug 再悄悄回来
        Assert.Contains("if (OrigRefLooksStale(om, os))", code);
        Assert.Contains("if (nowTick - _lastRefHealAt > 2000)", code);
    }

    /// <summary>★ 切视图进「左右对比」时,两条**必须互相咬合**。
    /// 真机日志实证:`切视图对齐(CmpPlayerBottom):差 302 ms → 跳过定位(容差内)` —— 0.5 秒容差把
    /// 302ms 的错位放过了,而并排画面里那是肉眼一眼能看出的不同帧(用户报的"不协调")。
    /// 而且旧写法让两条**各自**对到 clipT ⇒ 可以一个 +0.5、一个 −0.5,相差最多 1 秒。
    /// 现在:上层对到**下层此刻的位置**,容差一帧(30ms)。</summary>
    [Fact]
    public void Entering_the_split_view_makes_the_two_halves_bite_each_other()
    {
        var code = StripComments(ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs"));
        // 容差参数化(默认 0.5 秒照旧,给单视图用)
        Assert.Contains("private async Task AlignViewPlayerAsync(Microsoft.UI.Xaml.Controls.MediaPlayerElement? el, double targetSec, bool play, long gen,\n        double tolSec = 0.5)", code);
        Assert.Contains("bool didSeek = gap > tolSec;", code);
        Assert.Contains("private const double MaskPairToleranceSec = 0.03;", code);
        // 遮罩分支:上层对到下层的位置 + 一帧容差(不再两条各自对 clipT)
        Assert.Contains("anchor = res.Position.TotalSeconds;", code);
        Assert.Contains("await AlignViewPlayerAsync(nSrc, anchor, playing, gen, tolSec: MaskPairToleranceSec);", code);
        Assert.DoesNotContain("if (_maskSplitActive) await AlignViewPlayerAsync(nSrc, clipT, playing, gen);", code);
        // 日志要把容差写出来(否则下次又分不清"跳过定位"是因为多少毫秒的容差)
        Assert.Contains("(容差 {tolSec * 1000:0} ms)", code);
    }

    // ---------- ① / ② 埋点必须说实话 ----------

    /// <summary>`进同步分支` 以前是 `!_cmpSingle`(只说明"不是单播放器"),与"同步真的动手了"无关 ——
    /// 于是日志里能同时出现 `进同步分支=True` 和 `漂移 -1416ms`,读日志的人会以为"纠偏跑了但没纠住"。
    /// 现在:它只在**真的进了会动手的那一支**时置 true,另外加一条 `同步动作=…` 把"为什么没动手"写出来。</summary>
    [Fact]
    public void Sync_branch_flag_reports_whether_it_actually_acted()
    {
        var code = StripComments(ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs"));
        var raw = ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");

        // 旧的"骗人"写法必须消失
        Assert.DoesNotContain("_cmpSyncBranchEntered = !_cmpSingle;", code);
        // 默认 false + 真的动手时才置 true
        Assert.Contains("_cmpSyncBranchEntered = false;", code);
        Assert.Contains("_cmpSyncBranchEntered = true;   // 【2026-09-23】真的进了\"会动手\"的那一支", code);
        // 停住/片尾要如实标注(它们正是"漂移没人管"的那两种状态)
        Assert.Contains("_cmpSyncAction = clipAtEnd ? \"停住·片尾(只暂停,不对齐)\" : \"停住(只暂停,不对齐)\";", code);
        // 地面数据里带上它(上一轮的格式契约不许破:进同步分支=… 这一项要保留)
        Assert.Contains("· 进同步分支={_cmpSyncBranchEntered}", code);
        Assert.Contains("· 同步动作={_cmpSyncAction}", code);
        Assert.Contains("_cmpSyncAction = \"未触发\";", code);
        // 而"暂停态不触发位置回调"这件事必须写在代码里(否则下一个人又会把它当成异常)——
        // 这一条查原始源码(目的就是钉注释)
        Assert.Contains("暂停又不产生位置回调", raw);
    }

    /// <summary>硬对齐也要**回读确认**,并且先把该播放器的槽位清干净 ——
    /// 日志实证:硬对齐到 1.232s,3 秒后读回 0.117s(那次定位压根没落地,而合并器里可能还挂着更旧的目标)。
    /// 同时:遮罩态的门槛必须低于非遮罩态(±2% 微调每秒只追回 20ms,0.3 秒要 15 秒 ⇒
    /// 0.5s 的门槛意味着 0.25~0.5s 的偏差永远只靠"慢慢磨")。</summary>
    [Fact]
    public void Hard_realign_verifies_and_uses_a_tighter_threshold_in_mask_mode()
    {
        var code = StripComments(ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs"));
        Assert.Contains("double hardThreshold = _maskSplitActive ? 0.25 : 0.5;", code);
        Assert.Contains("_seeks.Clear(origMp);", code);
        Assert.Contains("_ = VerifySeekOnceAsync(origMp, realignTo);", code);
        // 上一轮钉住的同轴契约不许破
        Assert.Contains("double realignTo = _maskSplitActive ? clipPos : _effStart + clipPos;", code);
        Assert.Contains("try { op.Position = TimeSpan.FromSeconds(realignTo); } catch { }", code);
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
