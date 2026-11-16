using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests
{
    /// <summary>【2026-11-16】抠图页以前完全不接拖放:窗口级处理器只分派给视频页和图片放大页,
    /// 把图片拖到抠图页等于什么都没发生(用户视角就是"拖不进去")。
    /// 这里钉住两条:窗口级分派必须包含抠图页,抠图页必须真的把路径交给自己的列表。</summary>
    public class CutoutDropTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "ImgUpscalerUI", "Views", "MainPage.xaml")))
                d = d.Parent;
            if (d is null) throw new DirectoryNotFoundException("找不到仓库根目录");
            return d.FullName;
        }

        private static string Read(params string[] parts)
        {
            var p = Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray());
            Assert.True(File.Exists(p), "文件不存在:" + p);
            return File.ReadAllText(p);
        }

        [Fact]
        public void The_window_level_drop_hands_images_to_the_cutout_page_too()
        {
            var page = Read("ImgUpscalerUI", "Views", "MainPage.xaml.cs");
            // 抠图页收的是图片,不是视频 —— 分页摊开时的类型判断必须把它算进来
            Assert.Contains("_currentTag is \"upscale\" or \"cutout\"", page);
            Assert.Contains("_cutView.AcceptDroppedFilesAsync(paths)", page);
        }

        [Fact]
        public void The_cutout_page_accepts_dropped_files_into_its_own_list()
        {
            var view = Read("ImgUpscalerUI", "Views", "CutoutView.xaml.cs");
            Assert.Contains("public Task AcceptDroppedFilesAsync(string[] paths) => ToolGrid.AddImagesAsync(paths);", view);
        }
    }
}