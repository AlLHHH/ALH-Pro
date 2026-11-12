using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests
{
    /// <summary>
    /// 【2026-11-12 用户反馈「视频拖入不进去,放上去有个叉」】拖放的三个已知失败面,用源码契约钉住:
    /// ①拖到列表「某一行上面」时,Drop 会被项容器吃掉 ⇒ 行模板自己必须接一份拖放;
    /// ②拖进来的后缀不在白名单里(原来只有 5 种)⇒ 现在覆盖 m4v/ts/m2ts/flv/wmv/mpg/mpeg;
    /// ③被拒时不能只写日志(日志框默认收起)⇒ 必须调用 ShowDropHint 写到界面状态行。
    /// </summary>
    public class VideoDropTests
    {
        [Fact]
        public void List_row_itself_accepts_drops_so_dropping_on_a_video_works()
        {
            var xaml = ReadFile("ImgUpscalerUI", "Views", "VideoView.xaml");
            // 行模板根 Grid:同一个 Grid 上同时有 AllowDrop 与 Drop 处理
            var idx = xaml.IndexOf("ColumnSpacing=\"10\" Padding=\"4\"", StringComparison.Ordinal);
            Assert.True(idx > 0, "找不到行模板根 Grid");
            var slice = xaml.Substring(idx, Math.Min(600, xaml.Length - idx));
            Assert.Contains("AllowDrop=\"True\"", slice);
            Assert.Contains("Drop=\"DropBorder_Drop\"", slice);
            Assert.Contains("DragOver=\"DropBorder_DragOver\"", slice);
        }

        [Fact]
        public void More_container_formats_are_accepted_than_the_old_five()
        {
            var cs = ReadFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
            Assert.Contains("VideoDropExts", cs);
            foreach (var ext in new[] { ".m4v", ".ts", ".m2ts", ".flv", ".wmv", ".mpg", ".mpeg" })
                Assert.Contains(ext, cs);
            // 【2026-11-12】拖放不再按后缀拦截:任何文件都要收下,处理不了由后续探测给原因。
            Assert.Contains("不再按后缀拦截", cs);
            Assert.DoesNotContain("格式不支持\";", cs);
        }

        [Fact]
        public void A_rejected_drop_says_so_on_screen_not_only_in_the_log()
        {
            var cs = ReadFile("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
            Assert.Contains("ShowDropHint", cs);
            Assert.Contains("拖入被拒:", cs);
        }

        private static string ReadFile(params string[] parts)
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
}
