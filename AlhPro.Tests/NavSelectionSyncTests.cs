using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests
{
    /// <summary>【2026-11-19】用户:「它会自己切对页那个 切了 但是左边选功能界面没有变啊」。
    /// ShowView 以前只换右边内容,左侧 NavList 的选中项不动 ⇒ 视觉上停在上一个功能。
    /// 现在 ShowView 会按 tag 反查导航项并同步选中,并用 _syncingNav 挡掉 SelectionChanged 重入。</summary>
    public class NavSelectionSyncTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "ImgUpscalerUI", "Views", "MainPage.xaml")))
                d = d.Parent;
            if (d is null) throw new DirectoryNotFoundException("找不到仓库根目录");
            return d.FullName;
        }

        private static string MainPageCs()
            => File.ReadAllText(Path.Combine(RepoRoot(), "ImgUpscalerUI", "Views", "MainPage.xaml.cs"));

        [Fact]
        public void Showing_a_page_also_moves_the_left_nav_selection()
        {
            var cs = MainPageCs();
            int show = cs.IndexOf("private void ShowView(string tag)", StringComparison.Ordinal);
            Assert.True(show > 0, "找不到 ShowView");
            int call = cs.IndexOf("SyncNavTo(tag);", show, StringComparison.Ordinal);
            int clear = cs.IndexOf("ContentRoot.Children.Clear();", show, StringComparison.Ordinal);
            Assert.True(call > show, "ShowView 里必须调用 SyncNavTo");
            Assert.True(call < clear, "同步导航要在换内容之前(顺序反了会出现旧选中项的闪现)");
        }

        [Fact]
        public void The_sync_picks_the_item_whose_tag_matches_and_does_not_loop()
        {
            var cs = MainPageCs();
            int fn = cs.IndexOf("private void SyncNavTo(string tag)", StringComparison.Ordinal);
            Assert.True(fn > 0, "找不到 SyncNavTo");
            var body = cs.Substring(fn, Math.Min(900, cs.Length - fn));
            Assert.Contains("(li.Tag as string) == tag", body);
            Assert.Contains("NavList.SelectedItem = li;", body);
            Assert.Contains("_syncingNav = true;", body);
            // 处理器必须挡掉自己发出的选中事件,否则 ShowView 会被再调一次
            int handler = cs.IndexOf("private void NavList_SelectionChanged", StringComparison.Ordinal);
            Assert.True(handler > 0);
            Assert.Contains("if (_syncingNav) return;", cs.Substring(handler, 400));
        }
    }
}