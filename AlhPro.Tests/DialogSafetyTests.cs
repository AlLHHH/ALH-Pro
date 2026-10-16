using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【2026-10-17 · 对话框安全】把"同一时刻只能有一个 ContentDialog"这一类崩溃钉在源码层。
///
/// 背景(2026-09-23 真机两份崩溃诊断,同一签名 `COMException 0x80000019: Only a single ContentDialog
/// can be open at any time`):用户开着公告点「开始处理」、或任务跑完的「处理完成」还开着就点「预览效果」,
/// 第二个弹窗直接抛未处理异常 —— 用户只看到"点了没反应",程序留下一个崩溃诊断文件。
/// 现场当时只把**那两个**调用点各自 try/catch 了,全仓其余同类调用点(复审时数出 12 处裸调用)仍然是雷。
///
/// 这组断言把 2026-10-17 的收口方式钉住:
///   ① 所有 `ContentDialog.ShowAsync()` 必须经过 `ALHPro.SafeDialog`(它内部是**一个共享闸门**:
///      前一个对话框没关就排队等,等到就打不开才降级写日志);
///   ② SafeDialog 必须真的带那个闸门(不是退回"只 try/catch");
///   ③ 视频页那两处**不能再把"没弹出来"当成"用户点了取消"** —— 旧写法会静默中止整批任务。
/// 【所有断言先剥 `//` 注释】见 CodeOnly:注释里为说清历史会原样引用旧写法,不剥就是假绿。</summary>
public class DialogSafetyTests
{
    /// <summary>① 除了 SafeDialog 自己,**任何文件都不许直接调 `dlg.ShowAsync()`**。
    /// 红了 = 有人新加/回退了一个裸调用点 ⇒ 那个点在"已有对话框开着"时就是一次未处理崩溃。</summary>
    [Fact]
    public void Every_content_dialog_goes_through_SafeDialog()
    {
        var offenders = new List<string>();
        foreach (var f in UiCsFiles())
        {
            if (Path.GetFileName(f).Equals("SafeDialog.cs", StringComparison.OrdinalIgnoreCase)) continue;
            var code = CodeOnly(File.ReadAllText(f));
            if (Regex.IsMatch(code, @"\.ShowAsync\s*\(\s*\)"))
                offenders.Add(RepoRel(f));
        }
        Assert.True(offenders.Count == 0,
            "这些文件还在直接调 ContentDialog.ShowAsync():\n" + string.Join("\n", offenders)
            + "\n⇒ 改成 await ALHPro.SafeDialog.ShowAsync(dlg, \"哪个弹窗\")"
            + "(需要区分「没弹出来」和「用户点了什么」时用 TryShowAsync)。");
    }

    /// <summary>② 闸门本身:必须是一个共享的 SemaphoreSlim + TryShowAsync,且 ShowAsync 只是它的包装。
    /// 红了 = 有人把 SafeDialog 退回"只有 try/catch"的旧语义(冲突时第二个弹窗直接消失、用户看不到结论)。</summary>
    [Fact]
    public void SafeDialog_serializes_dialogs_with_a_shared_gate()
    {
        var code = CodeOnly(ReadRepoFile("ImgUpscalerUI", "SafeDialog.cs"));
        Assert.Contains("SemaphoreSlim", code);
        Assert.Contains("TryShowAsync", code);
        Assert.Contains("WaitAsync", code);
        Assert.Contains("_gate.Release()", code);
        // 旧签名必须委托给新实现(否则调用方拿到的是"没有闸门"的版本)
        Assert.Matches(@"ShowAsync\([^)]*\)\s*=>\s*\(await\s+TryShowAsync", code);
    }

    /// <summary>③ 视频页:4K 提醒与 CPU 提醒必须用 TryShowAsync 并判 `shown`,
    /// 不许再把 `ContentDialogResult.None`("没弹出来")当成"用户点了取消" ——
    /// 那会在用户毫不知情的时候**静默中止整批任务**,并写一行"用户选择取消"的假日志。
    /// 红了 = 这两处被改回 ShowAsync + `!= Primary ⇒ return false`。</summary>
    [Fact]
    public void Video_page_does_not_mistake_a_missing_dialog_for_user_cancel()
    {
        var code = CodeOnly(ReadRepoFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs"));
        Assert.Contains("var (shown4k, r4k) = await ALHPro.SafeDialog.TryShowAsync(dlg4k", code);
        Assert.Contains("var (shownCpu, rCpu) = await ALHPro.SafeDialog.TryShowAsync(dlgCpu", code);
        Assert.Contains("if (!shown4k)", code);
        Assert.Contains("if (!shownCpu)", code);
        // 旧写法必须彻底消失
        Assert.DoesNotContain("var r4k = await ALHPro.SafeDialog.ShowAsync(dlg4k", code);
        Assert.DoesNotContain("var rCpu = await ALHPro.SafeDialog.ShowAsync(dlgCpu", code);
    }

    /// <summary>④ 两条"程序自己出事"的提示(运行中未处理异常 / 页面加载失败)也必须走闸门:
    /// 它们以前是 fire-and-forget,异步 COMException 没人观察 ⇒ 用户什么都看不到,
    /// 而这两条恰好最需要被看到(前者正是崩溃诊断对应的那条提示)。</summary>
    [Fact]
    public void Crash_and_page_load_notices_go_through_SafeDialog()
    {
        var app = CodeOnly(ReadRepoFile("ImgUpscalerUI", "App.xaml.cs"));
        Assert.Contains("ALHPro.SafeDialog.ShowAsync(dlgFatal", app);
        Assert.Contains("ALHPro.SafeDialog.ShowAsync(dlg, \"页面加载失败\")", app);
    }

    // ───────────────────────── 工具(与仓库既有源码级测试同风格) ─────────────────────────

    private static IEnumerable<string> UiCsFiles()
    {
        var root = RepoDir("ImgUpscalerUI");
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase)
                     && !p.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase)
                     && !p.Contains(@"\_bak_", StringComparison.OrdinalIgnoreCase)
                     && !p.Contains(@"\backup_", StringComparison.OrdinalIgnoreCase));
    }

    private static string RepoRel(string full)
    {
        var repo = RepoDir("");
        return full.StartsWith(repo, StringComparison.OrdinalIgnoreCase) ? full[repo.Length..] : full;
    }

    /// <summary>从测试输出目录往上找仓库根(以 `ImgUpscalerUI` 目录存在为准)。</summary>
    private static string RepoDir(string first)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var cand = first.Length == 0 ? dir.FullName : Path.Combine(dir.FullName, first);
            if (Directory.Exists(cand) && (first.Length > 0 || Directory.Exists(Path.Combine(dir.FullName, "ImgUpscalerUI"))))
                return first.Length == 0 ? dir.FullName + Path.DirectorySeparatorChar : cand;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("找不到仓库目录: " + first);
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

    /// <summary>剥掉 `//` 行注释(只在字符串字面量之外识别),理由见类注释。</summary>
    private static string CodeOnly(string src)
    {
        var sb = new System.Text.StringBuilder(src.Length);
        foreach (var line in src.Split('\n'))
        {
            bool inStr = false, inChar = false;
            int cut = line.Length;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inStr)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '"') inStr = false;
                    continue;
                }
                if (inChar)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '\'') inChar = false;
                    continue;
                }
                if (c == '"') { inStr = true; continue; }
                if (c == '\'') { inChar = true; continue; }
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') { cut = i; break; }
            }
            sb.Append(line[..cut]).Append('\n');
        }
        return sb.ToString();
    }
}
