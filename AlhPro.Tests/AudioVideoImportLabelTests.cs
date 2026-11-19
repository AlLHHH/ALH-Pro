using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests
{
    /// <summary>【2026-11-19】用户把「转场(2).mp4」拖进音频页,看到列表里还是 mp4 名字,问
    /// 「不应该来一个无损的转格式吗」。事实:音频页不复制源文件,点开始时才用 ffmpeg 抽音频;
    /// 但默认输出是 MP3(再压一次),这跟"无损提取"的期待不符。
    /// 现在:视频条目名字后面标「· 视频(导出时提取音频)」,并且默认把输出格式切成 WAV(pcm 无损)。</summary>
    public class AudioVideoImportLabelTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "ImgUpscalerUI", "Views", "MainPage.xaml")))
                d = d.Parent;
            if (d is null) throw new DirectoryNotFoundException("找不到仓库根目录");
            return d.FullName;
        }

        private static string AudioViewCs()
            => File.ReadAllText(Path.Combine(RepoRoot(), "ImgUpscalerUI", "Views", "AudioView.xaml.cs"));

        [Fact]
        public void Videos_in_the_audio_list_say_they_are_extracted_on_export()
        {
            var cs = AudioViewCs();
            Assert.Contains("AlhPro.DroppedPaths.VideoExtensions", cs);
            Assert.Contains("· 视频(导出时提取音频)", cs);
        }

        [Fact]
        public void Importing_a_video_switches_the_default_output_to_lossless_wav()
        {
            var cs = AudioViewCs();
            int videos = cs.IndexOf("int videos = 0;", StringComparison.Ordinal);
            Assert.True(videos > 0, "找不到视频计数");
            int idx = cs.IndexOf("FmtRadios.SelectedIndex = 1;", videos, StringComparison.Ordinal);
            Assert.True(idx > videos, "有视频时要把输出格式切到 WAV(索引 1 = WAV)");
            // 同一段里不能又把它改回 MP3/0
            var slice = cs.Substring(videos, Math.Min(900, cs.Length - videos));
            Assert.Contains("WAV", slice);
        }
    }
}