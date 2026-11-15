using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests
{
    /// <summary>【2026-11-15】覆盖安装前必须确认旧版本已退出。
    ///
    /// 起因:用户反馈「更新到 1.4.9 之后界面还显示 1.4.8」。最像的成因不是更新检查出错,
    /// 而是安装时 ALH Pro 还开着 —— Windows 锁住 ALHPro.exe / ALHPro.dll,安装程序只能跳过,
    /// 装完打开还是旧版本。所以三个安装脚本都要在开装前把「请先退出」这句话讲清楚。</summary>
    public class InstallerUpgradeGuardTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null)
            {
                if (File.Exists(Path.Combine(d.FullName, "ImgUpscalerUI", "Views", "MainPage.xaml"))) return d.FullName;
                d = d.Parent;
            }
            throw new FileNotFoundException("找不到仓库根目录");
        }

        [Theory]
        [InlineData("installer.iss")]
        [InlineData("installer_full.iss")]
        [InlineData("installer_nocut.iss")]
        public void Every_installer_stops_when_the_old_version_is_still_running(string file)
        {
            var s = File.ReadAllText(Path.Combine(RepoRoot(), file));

            Assert.Contains("function PrepareToInstall(", s);
            Assert.Contains("ALHPro.exe", s);              // 真的去查进程,不是只写一句提示
            Assert.Contains("tasklist", s);
            Assert.Contains("请先完全退出", s);
        }
    }
}