using System;
using System.IO;
using Xunit;

namespace AlhPro.Tests
{
    /// <summary>
    /// 【2026-11-22 用户裁决】「TTA 关掉 再也不显示 我感觉就是没有用的功能」。
    ///
    /// 依据(本机诊断包 ALHPro_Diag_20261122_1930 的实测):同一台 RTX 5060 Laptop,
    /// 开 TTA 时补帧 4854 ms/输出帧,不开时按面积锚点只有约 337 ms —— 贵 14 倍,
    /// 而画质差别用户看不出来。这个开关只会把任务拖成 69 分钟,所以整条下架:
    /// 界面上不再有它,处理端恒为关闭,写盘恒为 false(字段留着只为旧设置文件能反序列化)。
    ///
    /// 【为什么钉源码而不是钉行为】它是"功能有没有被彻底删掉"的契约:
    /// 只删界面不删处理端,就会变成"界面上看不到了、处理时还在偷偷加参数"的静默行为 —— 本仓库最忌讳这类问题。
    /// </summary>
    public class TtaRemovedTests
    {
        private static string RepoRoot()
        {
            var probe = new DirectoryInfo(AppContext.BaseDirectory);
            while (probe != null && !File.Exists(Path.Combine(probe.FullName, "ImgUpscalerUI", "Views", "VideoView.xaml")))
                probe = probe.Parent;
            Assert.NotNull(probe);
            return probe!.FullName;
        }

        [Fact]
        public void The_video_page_has_no_tta_switch_left()
        {
            string xaml = File.ReadAllText(Path.Combine(RepoRoot(), "ImgUpscalerUI", "Views", "VideoView.xaml"));
            Assert.DoesNotContain("TtaCheck", xaml);
            Assert.DoesNotContain("高质量 TTA", xaml);
            Assert.DoesNotContain("开启高质量", xaml);
            // 「高级参数」面板本来只剩这一个控件,删了它面板就成了空壳,连按钮一起撤掉。
            Assert.DoesNotContain("AdvancedToggleBtn", xaml);
        }

        [Fact]
        public void The_code_behind_never_touches_a_switch_that_no_longer_exists()
        {
            string cs = File.ReadAllText(Path.Combine(RepoRoot(), "ImgUpscalerUI", "Views", "VideoView.xaml.cs"));
            Assert.DoesNotContain("TtaCheck", cs);
            // 处理入口必须写死 false(不是"保留但默认关")。
            Assert.Contains("var tta = false;", cs);
            // 写盘也必须是 false,否则老设置里的 true 会被继续沿用。
            Assert.Contains("Tta = false,", cs);
        }

        [Fact]
        public void The_old_setting_field_survives_for_backward_compatibility()
        {
            // 字段保留 = 老 app-settings / 老预设仍能反序列化,不会因为少一个键而整份设置报废。
            string cs = File.ReadAllText(Path.Combine(RepoRoot(), "ImgUpscalerUI", "Views", "VideoView.xaml.cs"));
            Assert.Contains("public bool Tta { get; set; }", cs);
        }
    }
}