using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests
{
    /// <summary>【2026-11-19】用户报:「有些人的电脑就是拖不进来,会有红色叉叉,但就是 mp4 视频」。
    /// 红色禁止符有两个来源:①提权窗口被 UIPI 掐断(见 DeElevate 的自愈);
    /// ②我们自己把 AcceptedOperation 设成 None —— 从压缩包里、网盘「在线文件」、邮件附件
    /// 直接拖出来的东西没有本地路径,Windows 只给虚拟格式,老代码一看没有 StorageItems
    /// 就拒绝 ⇒ 光标变红叉。这条契约把 ② 钉死。</summary>
    public class DropRedCrossTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "ImgUpscalerUI", "Views", "MainPage.xaml")))
                d = d.Parent;
            if (d is null) throw new DirectoryNotFoundException("找不到仓库根目录");
            return d.FullName;
        }

        private static string MainPage()
        {
            var p = Path.Combine(RepoRoot(), "ImgUpscalerUI", "Views", "MainPage.xaml.cs");
            Assert.True(File.Exists(p), "文件不存在:" + p);
            return File.ReadAllText(p);
        }

        private static string DragOverBody()
        {
            var cs = MainPage();
            int start = cs.IndexOf("private void Root_DragOver", StringComparison.Ordinal);
            Assert.True(start > 0, "找不到 Root_DragOver");
            int end = cs.IndexOf("private void Root_DragLeave", start, StringComparison.Ordinal);
            Assert.True(end > start, "找不到 Root_DragOver 的结束位置");
            return cs.Substring(start, end - start);
        }

        private static string DropBody()
        {
            var cs = MainPage();
            int start = cs.IndexOf("private async void Root_Drop", StringComparison.Ordinal);
            Assert.True(start > 0, "找不到 Root_Drop");
            int end = cs.IndexOf("private void ShowView", start, StringComparison.Ordinal);
            Assert.True(end > start, "找不到 Root_Drop 的结束位置");
            return cs.Substring(start, end - start);
        }

        [Fact]
        public void Drag_over_never_turns_the_cursor_into_a_forbidden_sign()
        {
            var body = DragOverBody();
            Assert.DoesNotContain("DataPackageOperation.None", body);
            Assert.Contains("e.AcceptedOperation = DataPackageOperation.Copy;", body);
            // 压缩包内 / 网盘在线文件的虚拟格式也要认
            Assert.Contains("\"FileGroupDescriptorW\"", body);
            Assert.Contains("\"Shell IDList Array\"", body);
            Assert.Contains("\"FileDrop\"", body);
        }

        [Fact]
        public void Drop_that_has_no_local_path_says_what_to_do_instead_of_failing_silently()
        {
            var body = DropBody();
            // 读文件列表这一步必须单独兜住,并且把 HRESULT 写进日志
            Assert.Contains("GetStorageItemsAsync()", body);
            Assert.Contains("HRESULT=0x", body);
            // 空路径分支给出可操作的说法,而不是一句 return
            Assert.Contains("DropNoLocalPathHint", body);
            var cs = MainPage();
            Assert.Contains("先解压", cs);
            Assert.Contains("先下载到硬盘", cs);
        }

        [Fact]
        public void The_hint_is_shown_in_the_window_status_line_not_as_a_popup()
        {
            var cs = MainPage();
            Assert.Contains("private void SetStatusLine(string text)", cs);
            Assert.Contains("StatusText.Text = text;", cs);
        }
    }
}