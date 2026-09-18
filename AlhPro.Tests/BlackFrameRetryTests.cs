using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>超分「黑帧降级」的接线契约(2026-09-16 真机事故后定稿)。
///
/// 【事故经过(真机,RTX 4060)】2026-09-16 21:22 那次视频任务:超分第 2 批 120 帧里有 **1 帧**被判黑,
/// 旧逻辑于是删掉整批输出、整批换 ONNX 重跑 —— 跑了 **22 分钟**(≈11 秒/帧;健康 DirectML 实测约 0.3 秒/帧),
/// 用户等了 26 分钟只能强制结束;同一形态 09-15 还出现过 4 次(代码注释里那笔"4060 黑帧重跑 282 分钟"同源)。
/// 更糟的是重跑仍黑时**整批回退源帧** ⇒ 那 120 帧整段画质掉档(只缩放、没真超分),好帧一起被牺牲。
///
/// 【修法(三条,都在这里钉住)】
///   ① **只重跑被判黑的那几帧**:同批好帧原样保留(不再删 batchOut、不再整批重算);
///   ② **重跑有硬预算**:到点即停、只把没救回来的帧回退源帧 —— 用户最多等两三分钟,不无限期干等;
///      且"超预算取消"必须与"用户取消"分开处理(用户取消要原样传播,不能被当成降级)。
///   ③ **零星黑帧不再把全片拖去 ONNX**:ncnn 只有达到"系统性"程度(整批空产 / 缺陷帧占比 ≥20%)才算不可靠。
///      旧逻辑"1 帧黑 = 后续全批改 ONNX"在 4060 上代价极大(实测可能 11 秒/帧)。
///
/// 【为什么用契约测试】这三条都是"组合条件错位 + 沉默":编译不报错、单测也测不到(要真跑一条带黑帧的
/// 去重素材 + 一张会出黑帧的卡),只能按本仓库既定手法把源码接线钉住(同 TempSpaceGateTests / SceneSwitchCoverageTests)。</summary>
public class BlackFrameRetryTests
{
    /// <summary>① 只重跑被判黑的帧:重跑走**独立的小输入/输出目录**,绝不删 batchOut、绝不再喂整批给 ONNX。</summary>
    [Fact]
    public void Only_the_defective_frames_are_retried_not_the_whole_batch()
    {
        var svc = ReadRepoFile("ImgUpscalerUI", "VideoService.cs");

        // 待重跑清单从 defectiveFrames 逐帧建立(而不是拿整批 batchIn 去重跑)
        Assert.Contains("var retrySrcs = new System.Collections.Generic.List<string>();", svc);
        Assert.Contains("foreach (var f in defectiveFrames)", svc);
        Assert.Contains("if (File.Exists(srcIn)) retrySrcs.Add(srcIn);", svc);

        // 【关键】"整批喂给 ONNX"只允许剩一处 = 正常就走 ONNX 的那条路(onnxModelPath != null);
        // 黑帧重跑必须是第二处、且走独立目录 retryIn/retryOut。旧写法(黑帧也拿 batchIn 整批重跑)会让这里变成 2。
        int wholeBatchFeeds = Regex_Matches(svc, @"UpscaleDirAsync\(batchIn, batchOut, upScale").Count;
        Assert.Equal(1, wholeBatchFeeds);
        // 重跑用的是独立目录(好帧已落 upOutput,不许被回头清掉)
        Assert.Contains("var retryIn = Path.Combine(workDir, $\"up_retry_in_{start}\");", svc);
        Assert.Contains("var retryOut = Path.Combine(workDir, $\"up_retry_out_{start}\");", svc);
        int retryFeeds = Regex_Matches(svc, @"UpscaleDirAsync\(retryIn, retryOut, upScale").Count;
        Assert.Equal(1, retryFeeds);

        // 逐帧认领结果:救回来的写回,没救回来的**只回退这一帧**(不是整批)
        Assert.Contains("int retriedOk = 0, retriedBack = 0;", svc);
        Assert.Contains("WriteFallbackFrame(rs, upOutput, upScale)", svc);
        // 无 ONNX 模型那条分支同样只回退这几帧(旧逻辑连"没得重跑"也整批作废)
        Assert.Contains("foreach (var rs in retrySrcs)", svc);
        Assert.Contains("try { WriteFallbackFrame(rs, upOutput, upScale); } catch { }", svc);
    }

    /// <summary>② 硬预算:有明确的预算算式 + CancelAfter 到点即停,且"超预算"与"用户取消"分开。</summary>
    [Fact]
    public void The_retry_has_a_hard_budget_and_does_not_swallow_user_cancel()
    {
        var svc = ReadRepoFile("ImgUpscalerUI", "VideoService.cs");

        // 预算算式(建会话 + 每帧 3 秒,下限 90、硬顶 240)—— 数字改了这条会红,提醒一并复核上面的注释口径
        Assert.Contains("int retryBudgetSec = Math.Clamp(90 + retrySrcs.Count * 3, 90, 240);", svc);
        // 真的接到了取消令牌上(不是只算了个数写进日志)
        Assert.Contains("using (var retryCts = CancellationTokenSource.CreateLinkedTokenSource(ct))", svc);
        Assert.Contains("retryCts.CancelAfter(TimeSpan.FromSeconds(retryBudgetSec));", svc);
        Assert.Contains("EsrganOnnxService.UpscaleDirAsync(retryIn, retryOut, upScale,", svc);
        Assert.Contains("retryCts.Token, onnxB,", svc);

        // 超预算 = 降级;用户取消 = 原样抛出。两者绝不能混(混了会把"用户取消"当成"降级成功")
        Assert.Contains("catch (OperationCanceledException) when (!ct.IsCancellationRequested)", svc);
        Assert.Contains("retryBudgetHit = true;", svc);
        Assert.Contains("if (ct.IsCancellationRequested) throw;", svc);
        Assert.Contains("if (retryBudgetHit)", svc);
        // 超预算必须有告知(用户不会去猜为什么"不动了")
        Assert.Contains("黑帧重跑超过预算", svc);
    }

    /// <summary>③ 零星黑帧不再把全片拖去 ONNX:ncnn 只有达到系统性程度才置位 ncnnUnreliable。</summary>
    [Fact]
    public void Sporadic_black_frames_do_not_flip_the_whole_video_to_onnx()
    {
        var svc = ReadRepoFile("ImgUpscalerUI", "VideoService.cs");

        // 判据:整批空产,或缺陷帧占本批 ≥20%(×5 ≥ 本批唯一帧数)
        Assert.Contains("if (!anyFrame || defectiveFrames.Count * 5 >= Math.Max(1, curPG.Count))", svc);
        Assert.Contains("ncnnUnreliable = true;", svc);
        // 整批重跑只留给"一帧都没产出"这种真故障
        Assert.Contains("bool wholeBatchRetry = !anyFrame || defectiveFrames.Count >= curPG.Count;", svc);
        // 置位时要说清依据(否则后人只看得到一个布尔值)
        Assert.Contains("ncnn-Vulkan 超分判定为系统性不可靠", svc);
    }

    private static System.Text.RegularExpressions.MatchCollection Regex_Matches(string text, string pattern)
        => System.Text.RegularExpressions.Regex.Matches(text, pattern);

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
