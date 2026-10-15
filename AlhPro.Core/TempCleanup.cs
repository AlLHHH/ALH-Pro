using System;
using System.Collections.Generic;
using System.IO;

namespace AlhPro.Core
{
    /// <summary>
    /// 【2026-10-15 真机事故 · 跨版本删活目录】"这个临时目录最近还在写吗"的判据。
    /// 为什么需要它:本软件(免费版)和 ALH Pro Max 会落在**同一个临时根**上 ——
    /// 两边都按"剩余空间最大的固定盘 + \ALHProTemp"自动选路径,本机就都是 `D:\ALHProTemp`。
    /// 而两边的启动清理都只看得见**自己进程**的处理状态(`EngineService.AnyProcessing`),
    /// 谁也看不见对方正在跑什么 ⇒ 谁后启动,谁就把对方**正在写的**工作目录当残留整棵删掉。
    /// 现场(2026-10-15 20:17):Max 正跑一趟 300 帧验证任务(超分刚跑完 118.8 秒,刚进补帧第一段),
    /// 本软件(免费版)此时被打开 —— 启动清理删掉 `D:\ALHProTemp\imgup_video_371e…` ⇒ Max 下一秒抛
    /// `DirectoryNotFoundException: Could not find a part of the path '…\imgup_video_371e…\seg_0_300_out'`,
    /// 整趟白跑 138 秒。用户以前看到的就是"任务莫名失败",日志里找不到谁删的。
    /// 判据换成"树里最新一次写入距今多久"(人人都能算,不依赖对方进程名/权限/便携版路径):
    /// 真在跑的任务每秒都在写帧,静置超过窗口才当残留。AlhPro.Max 里有同名同语义的实现,两边保持一致。
    /// </summary>
    public static class TempCleanup
    {
        /// <summary>多久没写过就不算"还在跑"(默认 4 小时):远长于任何"写帧 → 编码"之间的空档,
        /// 又短到残留不会长住(静置 4 小时以上的照旧当残留清)。</summary>
        public static readonly TimeSpan DefaultLiveWorkWindow = TimeSpan.FromHours(4);

        /// <summary>量一棵目录树的字节数。任何一层出错都只是少算那部分,绝不抛。</summary>
        public static long TryDirSize(string? dir) => TryDirSizeAndNewest(dir).Bytes;

        /// <summary>
        /// 量一棵目录树的字节数,并顺带回报树里**最新**一次写入的时间(判断"这棵树还在不在动")。
        /// 时间取树里所有文件/目录里最新的那个 LastWriteTimeUtc;取不到时是 <see cref="DateTime.MinValue"/>。
        /// 任何一层出错都只是少算/跳过,绝不抛;目录不存在时返回 (0, default)。
        /// </summary>
        public static (long Bytes, DateTime NewestUtc) TryDirSizeAndNewest(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return (0, default);
            long total = 0;
            DateTime newest = default;
            var stack = new Stack<string>();
            try
            {
                if (!Directory.Exists(dir)) return (0, default);
                stack.Push(dir);
                try { newest = Directory.GetLastWriteTimeUtc(dir); } catch { }
            }
            catch { return (0, default); }

            while (stack.Count > 0)
            {
                string cur = stack.Pop();
                try
                {
                    foreach (var f in Directory.EnumerateFiles(cur))
                    {
                        try
                        {
                            var fi = new FileInfo(f);
                            total += fi.Length;
                            if (fi.LastWriteTimeUtc > newest) newest = fi.LastWriteTimeUtc;
                        }
                        catch { }
                    }
                }
                catch { }
                try
                {
                    foreach (var sub in Directory.EnumerateDirectories(cur))
                    {
                        stack.Push(sub);
                        try
                        {
                            var dt = Directory.GetLastWriteTimeUtc(sub);
                            if (dt > newest) newest = dt;
                        }
                        catch { }
                    }
                }
                catch { }
            }
            return (total, newest);
        }

        /// <summary>
        /// 这棵临时目录树是不是"最近还在写"⇒ 启动清理不许删它(删了就等着看对面抛
        /// <c>DirectoryNotFoundException</c> 吧 —— 见类注释里 2026-10-15 那次)。
        /// <paramref name="newestUtc"/> 取树里最新一次写入的时间(见 <see cref="TryDirSizeAndNewest"/>)。
        /// 三条边界口径:①<paramref name="window"/> ≤ 0 = 关掉这道关、退回旧行为(照删);
        /// ②时间量不出来(<see cref="DateTime.MinValue"/>)时**保留** —— 宁可留着占盘,也不误删正在跑的任务;
        /// ③只有"确实旧了"才返回 false。用户手动点「立即清理」也走这条判据(他点清理的动机常常正是
        /// "任务看着慢/盘不够",那一刻另一个窗口/另一个版本八成正跑着)。
        /// </summary>
        public static bool ShouldKeepLiveWorkDir(DateTime newestUtc, DateTime nowUtc, TimeSpan window)
        {
            if (window <= TimeSpan.Zero) return false;
            if (newestUtc == default) return true;
            return nowUtc - newestUtc < window;
        }
    }
}
