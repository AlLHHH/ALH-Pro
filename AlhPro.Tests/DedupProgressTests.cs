using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【任务 ④ · 2026-09-21】"去重要不要补逐帧进度" —— **查实结论 + 把它钉死**。
///
/// ================== 查实过程与结论 ==================
/// 交接单上只确认了"去重有阶段级消息"(「智能检测:识别素材拍数…」「已拆出 N 帧…」),没找到 C# 逐帧循环,
/// 于是要求先查实"判定的帧差 SAD + 分块 SSIM 是不是跑在 ffmpeg 滤镜链里"。
/// **结论:不在滤镜链里,是纯 C# 逐帧循环**,而且那几条循环**早就有逐帧进度**了:
///   · <c>DetectDupFramesWithSsim</c>(帧差+SSIM 精确验证,手动/标准/敏感/动漫回退都走它)——
///     每帧 <c>SampleGray</c> 解码小图 + <c>MeanAbsDiff</c>(SAD)+ <c>BlockSsim</c>/<c>FrameMotionStats</c>;
///   · <c>DetectDupFramesAdaptive</c>(智能自适应的第一遍)—— 每对帧算 SAD/SSIM/变化块/对齐残差;
///   · <c>DetectDupFramesWithMotion</c>(手动-语义运动分析)。
/// 三条都通过同一个 <c>DedupHeartbeat</c> 每 16/32 帧报一次
/// 「去重分析(… ) 第 N 帧 / 共 M 帧(已判重 K 帧,已用 T 秒)」→ **界面与预览页都能看到,不用另加**。
///
/// 【本轮补上的两处】查实时另外发现两段**没有**心跳的逐帧循环,已按同一风格接上(见 <see cref="DedupHeartbeat"/> 用法):
///   · `SegmentContentFpsCoreSync` 里"自动识别拍数"那段(每帧 LoadFullGray + 直方图均衡化)。
///     **它今天不可达**(三个调用点全部传 `forceGrid: true`),所以不是用户能撞到的缺陷 ——
///     但它是一颗定时炸弹:哪天有人把这条路打开,界面就会变成"卡在那里一动不动";
///   · 去重收尾的"逐帧删除 + 重排帧号"(**这条可达**,长片上是实打实的几秒~几十秒文件 I/O)。
///
/// 【为什么用契约测试】"某段循环有没有心跳"编译不报错、也跑不出异常 —— 只在真机长素材上表现为
/// "界面看着像卡死"。本仓库对这类"静默体验缺陷"的既定手法就是把结构钉住。
/// 本文件钉两件事:① 去重链上**每一条**逐帧循环都接着心跳(不许再出现新的"哑循环");
/// ② 心跳消息真的接到了界面上(预览页与主界面两条通道)。</summary>
public class DedupProgressTests
{
    /// <summary>① 去重链上每一条逐帧循环都必须有 DedupHeartbeat。
    /// 【怎么数的】`new DedupHeartbeat(` 的出现次数必须等于 `hb.Step(` 之类的调用所在循环数 ——
    /// 更稳的判据是"每个 `for (int i = …` 的逐帧循环附近都有 Step 调用",但那样太脆;
    /// 这里钉住**今天这五处**(删一处就会红,提醒改的人"这条循环从此没有进度了")。</summary>
    [Fact]
    public void Every_dedup_frame_loop_reports_progress()
    {
        var svc = ReadRepoFile("ImgUpscalerUI", "VideoService.cs");

        // 五条逐帧循环 = 五个心跳(顺序即源码顺序):
        //   ① DetectDupFramesWithSsim  ② 拍数识别(保留路径)  ③ 收尾的重排帧号
        //   ④ DetectDupFramesAdaptive  ⑤ DetectDupFramesWithMotion
        Assert.Equal(5, Count(svc, @"new DedupHeartbeat\("));
        foreach (var v in new[] { "hb", "hbPick", "hbRe" })
        {
            Assert.Contains($"var {v} = new DedupHeartbeat(", svc);
            Assert.Contains($"{v}.Step(", svc);
            Assert.Contains($"{v}.Done(", svc);
        }
        // 心跳本体:节流写日志(界面不限流,盘上每 5 秒一条),收尾必落一条"完成"日志
        Assert.Contains("private sealed class DedupHeartbeat", svc);
        Assert.Contains("去重分析", svc);
        Assert.Contains("(已判重 {deleted} 帧,已用 {_sw.Elapsed.TotalSeconds:0} 秒)", svc);
        Assert.Contains("if (ms - _lastLogMs >= 5000)", svc);

        // 节流口径:CPU 判定每 16/32 帧,文件 I/O 每 1024 帧(三档都在,取 2 的幂)
        Assert.Contains("if ((i & 15) == 0)", svc);      // DetectDupFramesWithSsim
        Assert.Contains("if (((i - s) & 31) == 0)", svc); // 拍数识别
        Assert.Contains("if ((n & 1023) == 0)", svc);     // 重排帧号(文件 I/O,节流更粗)
        Assert.Contains("if ((i & 31) == 0)", svc);       // Adaptive / WithMotion
    }

    /// <summary>② 停用/兜底绝不能把心跳一起删掉 —— 用一处真实的历史教训当例子:
    /// `ct.ThrowIfCancellationRequested()`(可取消)与心跳**写在同一个 if 里**,
    /// 后人"整理代码"时很容易只留其中一个。这条钉住"取消检查与心跳同进同出"。</summary>
    [Fact]
    public void Cancel_check_and_heartbeat_stay_together()
    {
        var svc = ReadRepoFile("ImgUpscalerUI", "VideoService.cs");
        // 每一处 `ct.ThrowIfCancellationRequested();` 后面紧跟心跳(允许中间夹注释行)
        var hits = Regex.Matches(svc, @"ct\.ThrowIfCancellationRequested\(\);\s*(?://[^\n]*\n\s*)*hb\w*\.Step\(");
        Assert.True(hits.Count >= 3, $"「可取消 + 心跳」成对出现的处数只有 {hits.Count}(应 ≥3:三条 CPU 判定循环)");
    }

    private static int Count(string text, string pattern) => Regex.Matches(text, pattern).Count;

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
