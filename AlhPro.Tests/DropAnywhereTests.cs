using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests
{
    /// <summary>【2026-11-19】用户要求:「拖到窗口任意位置」都要能进来。
    /// 以前 Root_Drop 只认「当前页正好是视频页/图片页/抠图页」,拖到音频页、设置页
    /// 松手会被记成「当前页面不接收拖入,已忽略」—— 用户看到的就是"拖了没反应"。
    /// 现在的口径:先按文件类型算出该去哪个页面,当前页合适就留在原页,
    /// 不合适就自己切过去再导入。这几条用源码契约钉住。</summary>
    public class DropAnywhereTests
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

        /// <summary>只取 Root_Drop 一个方法体,免得别处的同名片段让断言蒙对。</summary>
        private static string RootDropBody()
        {
            var cs = Read("ImgUpscalerUI", "Views", "MainPage.xaml.cs");
            int start = cs.IndexOf("private async void Root_Drop", StringComparison.Ordinal);
            Assert.True(start > 0, "找不到 Root_Drop");
            int end = cs.IndexOf("private void ShowView", start, StringComparison.Ordinal);
            Assert.True(end > start, "找不到 Root_Drop 的结束位置");
            return cs.Substring(start, end - start);
        }

        [Fact]
        public void Window_drop_decides_the_page_by_what_was_dropped()
        {
            var body = RootDropBody();
            Assert.Contains("AlhPro.DroppedPaths.Expand(raw, images: false)", body);
            Assert.Contains("AlhPro.DroppedPaths.Expand(raw, images: true)", body);
            // 按类型选页面:视频优先,图片退到图片页
            Assert.Contains("asVideos.Length > 0 && (curVideo || asImages.Length == 0)", body);
        }

        [Fact]
        public void Window_drop_switches_to_the_page_it_needs_instead_of_ignoring()
        {
            var body = RootDropBody();
            Assert.Contains("ShowView(target);", body);
            Assert.Contains("await Task.Delay(60);", body);
            Assert.DoesNotContain("不接收拖入", body);
            Assert.DoesNotContain("已忽略", body);
        }

        [Fact]
        public void Window_drop_stays_put_when_the_current_page_already_fits()
        {
            var body = RootDropBody();
            // 图片在图片页/抠图页松手 → 不跳页
            Assert.Contains("target = curImage ? _currentTag! : \"upscale\";", body);
            Assert.Contains("bool curImage = _currentTag is \"upscale\" or \"cutout\";", body);
        }

        [Fact]
        public void The_window_root_really_receives_drops()
        {
            var xaml = Read("ImgUpscalerUI", "Views", "MainPage.xaml");
            Assert.Contains("AllowDrop=\"True\"", xaml);
            Assert.Contains("Drop=\"Root_Drop\"", xaml);
            Assert.Contains("DragOver=\"Root_DragOver\"", xaml);
        }
    }
}