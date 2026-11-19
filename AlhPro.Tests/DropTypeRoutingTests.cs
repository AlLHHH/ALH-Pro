using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests
{
    /// <summary>【2026-11-19】用户连着报了三件事:「图片还能放在视频页」「视频为什么可以放在图片页」
    /// 「视频拖到音频页要能提音频」——根因是 `AlhPro.Core/DroppedPaths.Expand` 对**单个文件**
    /// 故意不按后缀筛(先把文件收下,处理不了再由探测说原因),所以窗口级拖放如果只看 Expand 的返回,
    /// 就会把视频当图片送进图片页、把图片当视频送进视频页。
    /// 这里把「谁按后缀分类」「谁兜底拒收」两件事钉在源码契约上。</summary>
    public class DropTypeRoutingTests
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
        public void The_window_drop_sorts_files_by_extension_instead_of_trusting_Expand()
        {
            var cs = Read("ImgUpscalerUI", "Views", "MainPage.xaml.cs");
            Assert.Contains("var videoExts = new HashSet<string>(AlhPro.DroppedPaths.VideoExtensions", cs);
            Assert.Contains("var imageExts = new HashSet<string>(AlhPro.DroppedPaths.ImageExtensions", cs);
            Assert.Contains(".Where(p => videoExts.Contains(Path.GetExtension(p))).ToArray();", cs);
            Assert.Contains(".Where(p => imageExts.Contains(Path.GetExtension(p))).ToArray();", cs);
            // 两种都不是的时候要说一句,别静默
            Assert.Contains("既不是视频也不是图片", cs);
        }

        [Fact]
        public void The_image_page_refuses_videos_and_points_at_the_video_page()
        {
            var cs = Read("ImgUpscalerUI", "Views", "ImageToolGrid.xaml.cs");
            int guard = cs.IndexOf("if (!IsImageExt(ext))", StringComparison.Ordinal);
            Assert.True(guard > 0, "图片导入入口必须先判后缀");
            Assert.Contains("continue;", cs.Substring(guard, Math.Min(400, cs.Length - guard)));
            Assert.Contains("「视频处理」页", cs);
            // 守卫必须早于 webp/转码分支,否则视频会先被当成可转码文件
            int convert = cs.IndexOf("ConvertToPngAsync(", StringComparison.Ordinal);
            Assert.True(guard < convert, "拒收判断要放在转码之前");
        }

        [Fact]
        public void The_video_page_refuses_images_as_well()
        {
            var cs = Read("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
            Assert.Contains("AlhPro.DroppedPaths.ImageExtensions", cs);
            Assert.Contains("视频页跳过", cs);
        }
    }
}