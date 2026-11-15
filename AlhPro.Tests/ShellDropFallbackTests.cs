using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests
{
    /// <summary>【2026-11-15】用户反馈「很多人设备根本不能拖放进来,一部分又可以」——
    /// 那条反馈的根因是 OLE 拖放在提权窗口上被 UIPI 整条掐断,没有 API 可绕。
    /// 所以除了自愈降权,还必须各留一条 OLE 之外的路:
    ///   ①老式拖放 WM_DROPFILES(对高完整性窗口可以显式放行);
    ///   ②拖到 exe / 开始菜单 / 任务栏图标(= 命令行参数);
    ///   ③剪贴板 Ctrl+V。
    /// 这几条都是「换一条路」而不是「改文案」,所以用源码契约把它们钉住,免得以后重构时被删掉。</summary>
    public class ShellDropFallbackTests
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
        public void Old_style_drop_target_registers_the_messages_the_uipi_would_otherwise_block()
        {
            var cs = Read("ImgUpscalerUI", "ShellDrop.cs");
            Assert.Contains("WM_DROPFILES", cs);
            Assert.Contains("DragAcceptFiles(", cs);
            // 提权时必须显式放行,否则这条老路也进不来
            Assert.Contains("ChangeWindowMessageFilterEx(", cs);
            Assert.Contains("MSGFLT_ALLOW", cs);
            Assert.Contains("SetWindowSubclass(", cs);
            Assert.Contains("DragQueryFile(", cs);
            // 委托要长命:被 GC 掉之后子类化回调会指向已释放的 thunk
            Assert.Contains("private static SubclassProc? _proc;", cs);
        }

        [Fact]
        public void Application_hooks_the_old_style_drop_and_the_command_line_at_startup()
        {
            var cs = Read("ImgUpscalerUI", "App.xaml.cs");
            Assert.Contains("ShellDrop.FilesDropped +=", cs);
            Assert.Contains("ShellDrop.Attach(", cs);
            Assert.Contains("StartupFiles()", cs);
            Assert.Contains("page.ImportFromShellAsync(", cs);
            // 启动参数里只认存在的文件,不要把开关参数当文件
            Assert.Contains("System.IO.File.Exists(a)", cs);
        }

        [Fact]
        public void Page_picks_the_right_tab_for_files_that_arrive_outside_the_drag_pipeline()
        {
            var cs = Read("ImgUpscalerUI", "Views", "MainPage.xaml.cs");
            Assert.Contains("public async Task ImportFromShellAsync(string[] paths)", cs);
            Assert.Contains("AlhPro.DroppedPaths.VideoExtensions.Contains(", cs);
            Assert.Contains("ShowView(\"video\")", cs);
            Assert.Contains("ShowView(\"upscale\")", cs);
            // 仍然复用同一套导入:不要为弹外路径另写一份加载逻辑
            Assert.Contains("await ImportPathsAsync(paths,", cs);
        }

        [Fact]
        public void Command_line_import_does_not_silently_swallow_errors()
        {
            var cs = Read("ImgUpscalerUI", "Views", "MainPage.xaml.cs");
            Assert.Contains("catch (Exception ex)", cs);
            Assert.Contains("AppLogger.Error(\"系统拖放导入失败\", ex)", cs);
        }
    }
}