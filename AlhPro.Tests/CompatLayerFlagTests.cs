using System;
using System.IO;
using System.Linq;
using Xunit;
using ALHPro;

namespace AlhPro.Tests
{
    /// <summary>
    /// 「以管理员身份运行此程序」兼容性标记的解析 + 三处接线。
    ///
    /// 【为什么要有这组测试 · 2026-11-19】用户反馈「拖视频进去只有一个红叉」。
    /// 一半是虚拟文件没有本地路径,另一半是用户手工勾了这个兼容性项 → 程序永远管理员权限
    /// → 资源管理器拖放被 UIPI 掐断(无 API 可绕)。所以只能:①认得这串标记、②启动时清掉、
    /// ③安装时也清掉,别让旧标记传下去。
    /// </summary>
    public class CompatLayerFlagTests
    {
        [Theory]
        [InlineData("~ RUNASADMIN", true)]
        [InlineData("~RUNASADMIN", true)]
        [InlineData("~ runasadmin", true)]
        [InlineData("~ HIGHDPIAWARE RUNASADMIN", true)]
        [InlineData("~ HIGHDPIAWARE", false)]
        [InlineData("~", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Recognizes_run_as_admin_flag(string? value, bool expected)
        {
            Assert.Equal(expected, CompatLayerFlags.ContainsRunAsAdmin(value));
        }

        [Theory]
        [InlineData("~ HIGHDPIAWARE RUNASADMIN", "~ HIGHDPIAWARE")]
        [InlineData("RUNASADMIN ~ HIGHDPIAWARE", "~ HIGHDPIAWARE")]
        [InlineData("~  WIN8RTM   RUNASADMIN", "~ WIN8RTM")]
        public void Removing_the_flag_keeps_every_other_marker(string value, string expected)
        {
            Assert.Equal(expected, CompatLayerFlags.RemoveRunAsAdmin(value));
        }

        [Theory]
        [InlineData("~ RUNASADMIN")]
        [InlineData("RUNASADMIN")]
        [InlineData("~RUNASADMIN")]
        public void When_only_the_flag_was_there_the_whole_value_must_go(string value)
        {
            // null = 让调用方 DeleteValue,而不是留下一条没意义的 `~`
            Assert.Null(CompatLayerFlags.RemoveRunAsAdmin(value));
        }

        [Fact]
        public void A_value_without_the_flag_is_left_alone()
        {
            Assert.Equal("~ HIGHDPIAWARE", CompatLayerFlags.RemoveRunAsAdmin("~ HIGHDPIAWARE"));
        }

        [Fact]
        public void Startup_clears_the_flag_before_anything_else_runs()
        {
            var app = Read("ImgUpscalerUI", "App.xaml.cs");
            int onLaunched = app.IndexOf("StartupTrace(\"OnLaunched 进入\");", StringComparison.Ordinal);
            int repair = app.IndexOf("ElevationRepair.TryClearRunAsAdminFlag();", StringComparison.Ordinal);
            Assert.True(onLaunched > 0, "找不到 OnLaunched 入口");
            Assert.True(repair > onLaunched, "启动时必须在进入 OnLaunched 之后立刻清掉兼容性标记");
            // 清标记要做在"降权重开"之前:先别让它下次又以管理员身份起来
            int relaunch = app.IndexOf("DeElevate.TryRelaunchNormal()", StringComparison.Ordinal);
            Assert.True(repair < relaunch, "清兼容性标记必须早于降权重启");
        }

        [Fact]
        public void The_repair_writes_to_the_real_app_compat_layer_key()
        {
            var src = Read("ImgUpscalerUI", "ElevationRepair.cs");
            Assert.Contains(@"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers", src);
            Assert.Contains("CompatLayerFlags.RemoveRunAsAdmin", src);
            Assert.Contains("key.DeleteValue(exePath, throwOnMissingValue: false);", src);
            // 只在确实带 RUNASADMIN 时才动注册表
            Assert.Contains("CompatLayerFlags.ContainsRunAsAdmin(flags)", src);
        }

        [Theory]
        [InlineData("installer.iss")]
        [InlineData("installer_full.iss")]
        [InlineData("installer_nocut.iss")]
        public void Every_installer_deletes_a_stale_run_as_admin_marker(string iss)
        {
            var text = Read(iss);
            Assert.Contains("[Registry]", text);
            Assert.Contains(@"AppCompatFlags\Layers", text);
            Assert.Contains(@"ValueName: ""{app}\ALHPro.exe""; Flags: dontcreatekey deletevalue", text);
        }

        private static string Read(params string[] parts)
        {
            var root = RepoRoot();
            var path = Path.Combine(new[] { root }.Concat(parts).ToArray());
            Assert.True(File.Exists(path), "找不到文件:" + path);
            return File.ReadAllText(path);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ImgUpscalerUI", "Views", "MainPage.xaml")))
            {
                dir = dir.Parent;
            }
            Assert.NotNull(dir);
            return dir!.FullName;
        }
    }
}