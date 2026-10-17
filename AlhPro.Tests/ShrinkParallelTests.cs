using AlhPro.Core;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>【2026-10-17 用户反馈(m04853)】原话:「还有就是这个4x权重选2x倍数 这个缩放的问题 能不能缩放快一点
/// 为什么要逐帧缩放 缩放没有进度」。
/// 4x-only 权重必须按原生 4x 跑(非原生倍数会让整帧位移,见 EngineScalePolicy 的实测),跑完再把每帧缩回 2x;
/// 旧实现是 `foreach + Task.Run` 逐帧串行、整段只报一条不动的进度。修法:
///   ① 按 CPU 一半(2..8)有上限并行缩放;
///   ② 每缩完一帧刷新「超分 缩放回 2x 第 N 帧 / 共 M 帧」;
///   ③ 进度百分比落在本批引擎已跑完的位置(与引擎逐帧进度同口径,条不倒退)。
/// 本文件钉住:策略算术(可执行案例)+ 源码形态(防止有人改回串行 foreach)。</summary>
public class ShrinkParallelTests
{
    [Theory]
    [InlineData(1, 2)]      // 单核也给 2 路:缩放是阻塞的 CPU/IO 活,1 路等于退回串行
    [InlineData(2, 2)]
    [InlineData(4, 2)]
    [InlineData(6, 3)]
    [InlineData(8, 4)]
    [InlineData(16, 8)]
    [InlineData(32, 8)]     // 再多不加:每路吃一张 4K 解码位图,内存峰值与收益都不划算
    public void 并行度取CPU一半且夹在2到8(int cpus, int workers)
        => Assert.Equal(workers, ShrinkPolicy.Workers(cpus));

    [Theory]
    [InlineData(19, 66, 0, 1000, 19)]      // 视频超分区间:本批从全局第 0 帧开始
    [InlineData(19, 66, 500, 1000, 42)]    // 本批跑完时落在区间中段(19 + 47×500/1000 = 42.5 → 42)
    [InlineData(19, 66, 1000, 1000, 66)]   // 最后一帧正好是区间终点,不越过
    [InlineData(60, 66, 200, 100, 66)]     // 参数超界:夹在区间内,不让进度冲出去
    [InlineData(0, 0, 10, 100, 95)]        // 非视频调用没配区间:退回 95(旧实现的固定值)
    [InlineData(0, 66, 10, 100, 95)]       // pctLo 没配就不猜
    [InlineData(60, 40, 10, 100, 95)]      // 区间反了(配置错)也别算出怪数
    public void 缩回进度落在本批引擎已跑完的位置(int lo, int hi, int done, int total, int expect)
        => Assert.Equal(expect, ShrinkPolicy.Pct(lo, hi, done, total));

    [Fact]
    public void 缩回改成有上限并行_并且每帧刷新进度()
    {
        string code = CodeOnly(ReadRepoFile("ImgUpscalerUI", "EngineService.cs"));

        // 并行度走唯一来源(纯逻辑侧),不在 UI 里另写一套
        Assert.Contains("AlhPro.Core.ShrinkPolicy.Workers(Environment.ProcessorCount)", code);
        Assert.Contains("Parallel.ForEachAsync(", code);

        // 逐帧文案:能看出"第几帧 / 共几帧",用户要的进度就是这句
        Assert.Contains("超分 缩放回 ", code);
        Assert.Contains("第 {Volatile.Read(ref shrinkDone)} 帧 / 共 {shrinkFiles.Length} 帧", code);

        // 并发上报要加锁(进度实现不保证线程安全),且首末帧必报(force)
        Assert.Contains("lock (shrinkReportLock)", code);
        Assert.Contains("ReportShrink(force: true)", code);
        Assert.Contains("ReportShrink(force: false)", code);

        // 旧实现(串行 foreach + 无条件 Task.Run)不许回来。注意别用「精确调整」这条文案做全局断言:
        // 图片单图/分块路径(EngineService.cs:3235/3268/3297/3460)仍保留它,本测试只管视频批量的缩回块。
        Assert.DoesNotContain("foreach (var f in EnumerateImageFiles(outputDir))", code);
        Assert.DoesNotContain("Task.Run(() => ResizeImage(f, f, ratio)", code);
        // 缩回块的进度落点在 Core 策略里算(唯一来源),不在 UI 里另写一套
        Assert.Contains("AlhPro.Core.ShrinkPolicy.Pct(", code);
    }

    // ───────────────── 工具 ─────────────────

    /// <summary>剥掉 `//` 行注释(逐字符扫引号:字符串里的 `//` 不算注释)。不剥注释的源码断言会被
    /// "把那一行注释掉"骗过去(本仓库踩过,见 RememberParamsPresetTests 的红检记录)。</summary>
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
