using System;
using System.IO;
using Xunit;

namespace AlhPro.Tests
{
    /// <summary>
    /// 【2026-11-19】用户三条反馈:①视频页「正在导入」那块太大;②音频页转 WAV 时完全没有占位;
    /// ③同一个动作容易失败,而且日志里看不出原因。
    /// 这里把三件事钉成契约:占位条必须是单行、音频页必须有同一个口径的条、
    /// WAV 转换必须多条命令兜底且把 ffmpeg 原文带进异常。
    /// </summary>
    public class ImportPlaceholderTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null)
            {
                if (Directory.Exists(Path.Combine(d.FullName, "AlhPro.Core"))) return d.FullName;
                d = d.Parent;
            }
            throw new InvalidOperationException("找不到仓库根目录");
        }

        private static string Read(params string[] parts)
            => File.ReadAllText(Path.Combine(RepoRoot(), Path.Combine(parts)));

        private static string VideoXaml() => Read("ImgUpscalerUI", "Views", "VideoView.xaml");
        private static string AudioXaml() => Read("ImgUpscalerUI", "Views", "AudioView.xaml");
        private static string AudioCode() => Read("ImgUpscalerUI", "Views", "AudioView.xaml.cs");
        private static string Service() => Read("ImgUpscalerUI", "AudioService.cs");

        [Fact]
        public void The_video_import_strip_is_one_compact_line()
        {
            var x = VideoXaml();
            // 两行字里那句「导入中,先不要关窗口」是撑高的主因
            Assert.DoesNotContain("先不要关窗口", x);
            Assert.Contains("x:Name=\"ImportText\"", x);
            // 按钮只留一个字,进度条窄且薄
            Assert.Contains("Content=\"取消\"", x);
            Assert.DoesNotContain("Content=\"取消导入\"", x);
            Assert.Contains("Height=\"3\"", x);
        }

        [Fact]
        public void The_audio_page_has_a_placeholder_while_extracting()
        {
            var x = AudioXaml();
            Assert.Contains("x:Name=\"AudioImportBox\"", x);
            Assert.Contains("x:Name=\"AudioImportText\"", x);
            Assert.Contains("x:Name=\"AudioImportBar\"", x);
            Assert.Contains("Visibility=\"Collapsed\"", x);

            // 代码里必须在真正提取之前亮出来,循环结束后收掉
            var c = AudioCode();
            int show = c.IndexOf("ShowImportProgress(", StringComparison.Ordinal);
            int extract = c.IndexOf("ExtractAudioToWavAsync(p)", StringComparison.Ordinal);
            int hide = c.IndexOf("HideImportProgress();", StringComparison.Ordinal);
            Assert.True(show > 0 && extract > show, "占位条要在提取之前就显示");
            Assert.True(hide > extract, "提取结束要收掉占位条");
            Assert.Contains("AudioImportBox.Visibility = Visibility.Visible", c);
        }

        [Fact]
        public void Wav_conversion_tries_several_commands_and_keeps_the_error()
        {
            var s = Service();
            // ① 明确只取第 1 条音轨(默认选流会挑到视频 ⇒ 写 wav 直接失败)
            Assert.Contains("-vn -map 0:a:0", s);
            // ② 还有一条放宽采样率/声道的退路
            Assert.Contains("-ar 48000 -ac 2", s);
            // ③ 最后一条不指定采样率,让 ffmpeg 自己决定
            Assert.Contains("-vn -map 0:a:0? -c:a pcm_s16le", s);
            // 失败的原始输出必须留下来,不许再只抛一句「转换 WAV 失败」
            Assert.Contains("FirstLines(errText, 3)", s);
            Assert.Contains("AppLogger.Warn", s);
            Assert.Contains("转换 WAV 失败(保持采样率):{lastErr}", s);
        }
    }
}