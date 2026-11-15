using System;
using System.IO;
using System.Linq;
using AlhPro;
using Xunit;

namespace AlhPro.Tests
{
    /// <summary>【2026-11-15】拖进来的路径 → 要导入的文件。
    /// 用户先要「什么都能拖进去」,随后纠正「文件夹现在也收???我要的不是这个」,
    /// 所以规矩定成:文件原样收;**文件夹默认不收**(要递归必须显式开口子)。
    /// 用真的临时目录跑,不 mock 文件系统 —— 这段代码的坑全在枚举行为上。</summary>
    public class DroppedPathsTests : IDisposable
    {
        private readonly string _root;

        public DroppedPathsTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "alh_drop_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        private string Touch(string relative, string content = "x")
        {
            var p = Path.Combine(_root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, content);
            // 目录分隔符统一成 Windows 形式:枚举出来的路径是反斜杠,比较时不能用原文里的正斜杠。
            return Path.GetFullPath(p);
        }

        [Fact]
        public void A_single_file_is_taken_even_when_we_cannot_guess_its_type()
        {
            var odd = Touch("无关文件.weird");
            var got = DroppedPaths.Expand(new[] { odd }, images: false);

            // 单个文件不按后缀拦:收下,处理不了的时候由探测给出具体原因。
            Assert.Equal(new[] { odd }, got);
        }

        [Fact]
        public void A_dropped_folder_is_ignored_by_default()
        {
            Touch("素材/片段一.mp4");
            Touch("素材/子目录/片段二.mkv");

            // 2026-11-15 用户明确不要「拖文件夹自动摊开」,默认必须一个都不收。
            Assert.Empty(DroppedPaths.Expand(new[] { Path.Combine(_root, "素材") }, images: false));
        }

        [Fact]
        public void Folder_expansion_still_works_when_a_caller_asks_for_it()
        {
            var a = Touch("素材/片段一.mp4");
            var b = Touch("素材/子目录/片段二.mkv");
            Touch("素材/说明.txt");
            Touch("素材/子目录/封面.jpg");

            var got = DroppedPaths.Expand(new[] { Path.Combine(_root, "素材") }, images: false, includeFolders: true);

            Assert.Equal(2, got.Length);
            Assert.Contains(a, got);
            Assert.Contains(b, got);
        }

        [Fact]
        public void The_same_folder_twice_does_not_duplicate_entries()
        {
            Touch("素材/片段.mp4");
            var dir = Path.Combine(_root, "素材");
            var got = DroppedPaths.Expand(new[] { dir, dir }, images: false, includeFolders: true);

            Assert.Single(got);
        }

        [Fact]
        public void Images_and_videos_are_told_apart_by_the_calling_page()
        {
            var video = Touch("混合/片段.mp4");
            var image = Touch("混合/封面.png");
            var dir = Path.Combine(_root, "混合");

            Assert.Equal(new[] { video }, DroppedPaths.Expand(new[] { dir }, images: false, includeFolders: true));
            Assert.Equal(new[] { image }, DroppedPaths.Expand(new[] { dir }, images: true, includeFolders: true));
        }

        [Fact]
        public void A_huge_folder_is_capped_instead_of_hanging_the_ui()
        {
            Directory.CreateDirectory(Path.Combine(_root, "大目录"));
            for (int i = 0; i < 30; i++) Touch($"大目录/片段{i:D2}.mp4");

            var got = DroppedPaths.Expand(new[] { Path.Combine(_root, "大目录") }, images: false, maxFiles: 10, includeFolders: true);

            Assert.Equal(10, got.Length);
        }

        [Fact]
        public void Paths_that_no_longer_exist_are_skipped_silently()
        {
            var got = DroppedPaths.Expand(new[] { Path.Combine(_root, "早被删掉了.mp4"), "" }, images: false);
            Assert.Empty(got);
        }
    }
}