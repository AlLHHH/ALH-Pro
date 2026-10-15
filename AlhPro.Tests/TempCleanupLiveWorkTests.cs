using System;
using System.IO;
using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests
{
    /// <summary>【2026-10-15 跨版本删活目录】"这棵临时目录还在不在写"的判据 + 量树工具。
    /// 为什么要它:本软件与 ALH Pro Max 会落在同一个临时根(两边都按"剩余空间最大的固定盘 + \ALHProTemp"
    /// 自动选,本机都是 `D:\ALHProTemp`),而两边的启动清理都只看得见自己进程的处理状态 ⇒ 谁后启动,
    /// 谁就把对方**正在跑的**工作目录当残留删掉(现场见 AlhPro.Core.TempCleanup 的类注释)。</summary>
    public class TempCleanupLiveWorkTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "alh_livework_test_" + Guid.NewGuid().ToString("N"));

        public TempCleanupLiveWorkTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private string MakeFile(string rel, int bytes)
        {
            var p = Path.Combine(_root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, new byte[bytes]);
            return p;
        }

        [Fact]
        public void 量目录大小并回报树里最新一次写入的时间()
        {
            var old = new DateTime(2026, 10, 1, 3, 0, 0, DateTimeKind.Utc);
            string a = MakeFile("a.bin", 16);
            string b = MakeFile(Path.Combine("sub", "b.bin"), 16);
            File.SetLastWriteTimeUtc(a, old);
            File.SetLastWriteTimeUtc(b, old);
            Directory.SetLastWriteTimeUtc(Path.Combine(_root, "sub"), old);
            Directory.SetLastWriteTimeUtc(_root, old);

            var res = TempCleanup.TryDirSizeAndNewest(_root);
            Assert.Equal(32, res.Bytes);
            Assert.Equal(old, res.NewestUtc);
            Assert.Equal(res.Bytes, TempCleanup.TryDirSize(_root));

            // 树里再落一个新文件 ⇒ 时间跟着前进(判据就是靠它区分"还在写"和"没人要的残留")
            string c = MakeFile(Path.Combine("sub", "c.bin"), 8);
            var justNow = DateTime.UtcNow.AddSeconds(-1);
            File.SetLastWriteTimeUtc(c, justNow);
            var res2 = TempCleanup.TryDirSizeAndNewest(_root);
            Assert.Equal(40, res2.Bytes);
            Assert.True(res2.NewestUtc >= justNow, $"最新写入时间应当前进,实际 {res2.NewestUtc:O}");

            var missing = TempCleanup.TryDirSizeAndNewest(Path.Combine(_root, "没有这个目录"));
            Assert.Equal(0, missing.Bytes);
            Assert.Equal(default, missing.NewestUtc);
        }

        /// <summary>判据本体:刚写过的目录不许被当残留清(2026-10-15 那次删掉的就是 1 分钟前还在写的目录)。</summary>
        [Fact]
        public void 活目录判据_最近还在写的临时目录不删()
        {
            var window = TempCleanup.DefaultLiveWorkWindow;
            var now = new DateTime(2026, 10, 15, 20, 17, 0, DateTimeKind.Utc);

            Assert.True(window > TimeSpan.Zero, "活动窗口关掉就等于退回旧行为(会把对面正在跑的任务删掉)");
            Assert.True(window.TotalHours >= 1, "窗口要远长于任何'写帧→编码'之间的空档,否则长片跑到一半也会被清");

            // 秒级/分钟级前还在写 ⇒ 保留(2026-10-15 真机那一脚:对面 1 分钟前还在写帧)
            Assert.True(TempCleanup.ShouldKeepLiveWorkDir(now, now, window));
            Assert.True(TempCleanup.ShouldKeepLiveWorkDir(now - TimeSpan.FromMinutes(90), now, window));
            // 静置超过窗口 ⇒ 当残留清(残留不会长住)
            Assert.False(TempCleanup.ShouldKeepLiveWorkDir(now - window - TimeSpan.FromMinutes(1), now, window));
            Assert.False(TempCleanup.ShouldKeepLiveWorkDir(now - TimeSpan.FromDays(3), now, window));
            // 压线(刚好等于窗口)按"够旧"算
            Assert.False(TempCleanup.ShouldKeepLiveWorkDir(now - window, now, window));
            // 时间量不出来 ⇒ 不删(宁可留着占盘,也不误删正在跑的任务)
            Assert.True(TempCleanup.ShouldKeepLiveWorkDir(default, now, window));
            // 窗口关掉 = 旧行为(照删)
            Assert.False(TempCleanup.ShouldKeepLiveWorkDir(now, now, TimeSpan.Zero));
            Assert.False(TempCleanup.ShouldKeepLiveWorkDir(now, now, TimeSpan.FromSeconds(-1)));
        }
    }
}
