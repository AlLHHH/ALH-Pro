using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>
/// 【2026-10-17】两处"低概率但代价极大"的修复的守卫测试:
/// ①设置里「临时文件位置」只允许**空目录**或**软件自己的目录**(判据 = <see cref="AlhPro.Core.CustomTempRoot"/>)——
///   挑了别人的普通文件夹当临时根,清理就会去扫它,而免费版与 ALH Pro Max 认同一个根
///   (见 <see cref="AlhPro.Core.TempCleanup"/> 里 2026-10-15 那次跨版本删活目录的事故)。
/// ②视频成片一旦落盘并通过解码校验,失败收尾**不许**再删它 —— 以前"成片刚移好、紧接着收尾抛异常"
///   会把刚做好的成片删掉(用户白跑一整趟)。
/// 口径与同目录 TempCleanupAndInterpProgressTests 一致:纯逻辑直接断言语义,接线部分读源码逐字钉住。
/// </summary>
public class TempDirAndOutputKeepTests
{
    // ============ 一、临时目录准入(纯逻辑) ============

    [Fact]
    public void 临时目录准入_只有空目录或软件自己的目录才算可以()
    {
        var ok = AlhPro.Core.CustomTempRoot.Verdict.Ok;
        // 空目录:用户专为临时文件新建的
        Assert.Equal(ok, AlhPro.Core.CustomTempRoot.Check(exists: true, isDriveRoot: false, isEmpty: true, hasMarker: false));
        // 有标记文件:软件自己的目录 —— 哪怕里头还有上次跑剩的临时文件也照收(否则第二次就选不回来了)
        Assert.Equal(ok, AlhPro.Core.CustomTempRoot.Check(exists: true, isDriveRoot: false, isEmpty: false, hasMarker: true));
        // 非空且没标记 = 用户的普通文件夹:拒绝(这条就是这次修的核心)
        Assert.Equal(AlhPro.Core.CustomTempRoot.Verdict.NotEmpty,
            AlhPro.Core.CustomTempRoot.Check(exists: true, isDriveRoot: false, isEmpty: false, hasMarker: false));
        // 不存在:拒绝(而且不去管"空不空")
        Assert.Equal(AlhPro.Core.CustomTempRoot.Verdict.Missing,
            AlhPro.Core.CustomTempRoot.Check(exists: false, isDriveRoot: false, isEmpty: true, hasMarker: true));
        // 盘根:拒绝(普通程序在盘根写文件会被系统拒绝),且优先于"有标记"——手工往盘根放个标记文件也不行
        Assert.Equal(AlhPro.Core.CustomTempRoot.Verdict.DriveRoot,
            AlhPro.Core.CustomTempRoot.Check(exists: true, isDriveRoot: true, isEmpty: true, hasMarker: false));
        Assert.Equal(AlhPro.Core.CustomTempRoot.Verdict.DriveRoot,
            AlhPro.Core.CustomTempRoot.Check(exists: true, isDriveRoot: true, isEmpty: false, hasMarker: true));
    }

    [Fact]
    public void 临时目录拒绝理由_每种都写得出人话_且非空的情况点名了可用的子目录名()
    {
        Assert.Equal("", AlhPro.Core.CustomTempRoot.Describe(AlhPro.Core.CustomTempRoot.Verdict.Ok));
        foreach (var v in new[]
                 {
                     AlhPro.Core.CustomTempRoot.Verdict.Missing,
                     AlhPro.Core.CustomTempRoot.Verdict.DriveRoot,
                     AlhPro.Core.CustomTempRoot.Verdict.NotEmpty,
                 })
            Assert.False(string.IsNullOrWhiteSpace(AlhPro.Core.CustomTempRoot.Describe(v)));
        // 拒绝"非空"时必须告诉用户出路(在它里面新建专用子目录),否则等于不让他用这块盘
        Assert.Contains(AlhPro.Core.CustomTempRoot.SuggestedSubDirName,
            AlhPro.Core.CustomTempRoot.Describe(AlhPro.Core.CustomTempRoot.Verdict.NotEmpty));
    }

    // ============ 二、接线:设置页「浏览...」必须过准入闸门 ============

    [Fact]
    public void 设置页选临时目录_必须过准入闸门_不许挑到就写进设置()
    {
        string mp = StripLineComments(ReadRepoFile("ImgUpscalerUI", "Views", "MainPage.xaml.cs"));
        Assert.Contains("AlhPro.Core.CustomTempRoot.Check(", mp);
        Assert.Contains("AlhPro.Core.CustomTempRoot.MarkerFileName", mp);
        Assert.Contains("AlhPro.Core.CustomTempRoot.SuggestedSubDirName", mp);
        // 旧代码就是这一行:挑到目录直接写设置(那时用户的任意文件夹都能当临时根)。去掉闸门它就会回来。
        Assert.DoesNotContain("AppSettings.TempDir = folder.Path;", mp);
        // 写设置只有一处(接受闸门里);另一处是选「自动」时的清空
        Assert.Equal(1, Regex_Count(mp, @"AppSettings\.TempDir = dir;"));
        Assert.Contains("AppSettings.TempDir = \"\";", mp);
        // 认下目录时必须落一个标记文件(下次再挑它、或另一个版本来扫,才知道这目录是本软件的)
        Assert.Contains("AppSettings.Save();", mp);
    }

    // ============ 三、接线:成片落盘后的收尾异常不再把这一趟判失败 ============

    [Fact]
    public void 成片落盘后_收尾异常不再判失败_也不删掉成片()
    {
        string vs = StripLineComments(ReadRepoFile("ImgUpscalerUI", "VideoService.cs"));
        Assert.Contains("public static bool LastOutputCompleted", vs);
        // 每趟开头先清零:否则上一趟的 true 会让本趟真失败时也不敢删输出
        int iReset = vs.IndexOf("LastOutputCompleted = false;", StringComparison.Ordinal);
        int iMove = vs.IndexOf("RetryFileMove(outTmp, outputVideo);", StringComparison.Ordinal);
        int iFlag = vs.IndexOf("outputMoved = true;", StringComparison.Ordinal);
        Assert.True(iReset > 0 && iMove > iReset, "清零必须在合帧之前");
        Assert.True(iFlag > iMove, "落盘之后紧接着就要置位(不是事后补)");
        Assert.Contains("LastOutputCompleted = true;", vs);
        // 收尾异常被就地拦下(只记告警),取消照旧往外传(取消是"停下来",不是"失败")
        int iGuard = vs.IndexOf("catch (Exception ex) when (outputMoved)", StringComparison.Ordinal);
        Assert.True(iGuard > iMove, "这道 catch 必须守在落盘之后");
        int iOce = vs.LastIndexOf("catch (OperationCanceledException) { throw; }", iGuard, StringComparison.Ordinal);
        Assert.True(iOce > 0 && iGuard - iOce < 300, "取消要在收尾兜底之前单独放行");
    }

    [Fact]
    public void 界面失败收尾_成片已产出时不删输出_并按完成处理()
    {
        string vv = StripLineComments(ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs"));
        Assert.Contains("void DropFailedOutput()", vv);
        Assert.Contains("VideoService.LastOutputCompleted && File.Exists(outPath)", vv);
        // 三个失败收尾(重跑失败/用户放弃「仍要进行」/通用失败)都必须走闸门:
        // 裸删只剩闸门内部那一处;若有人改回直删,计数就变 2 ⇒ 红检立刻暴露
        Assert.Equal(1, Regex_Count(vv, @"File\.Delete\(outPath\)"));
        Assert.Equal(3, Regex_Count(vv, @"DropFailedOutput\(\);"));
        // 通用失败分支:成片已产出时按"完成(收尾告警)"处理 —— 成片进输出列表、计入成功数、预览分支把成片交回面板
        Assert.Contains("✓ 完成(收尾告警)", vv);
        Assert.Contains("⚠ 收尾出错,但预览成片已产出:", vv);
    }

    // ============ 工具(与 TempCleanupAndInterpProgressTests 同款) ============

    /// <summary>从测试程序集目录往上找仓库文件(测试是在 bin 里跑的,仓库根在若干层之上)。</summary>
    private static string ReadRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var p = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(p)) return File.ReadAllText(p);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("找不到仓库文件: " + string.Join("/", parts));
    }

    /// <summary>砍掉 // 之后的内容:注释里可以解释原因,不该被当成代码命中(否则"注释掉一行也算绿")。</summary>
    private static string StripLineComments(string src)
    {
        var lines = src.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            int idx = lines[i].IndexOf("//", StringComparison.Ordinal);
            if (idx >= 0) lines[i] = lines[i].Substring(0, idx);
        }
        return string.Join("\n", lines);
    }

    private static int Regex_Count(string text, string pattern)
        => System.Text.RegularExpressions.Regex.Matches(text, pattern).Count;
}
