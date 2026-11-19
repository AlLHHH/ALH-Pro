using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests
{
    /// <summary>
    /// 【2026-11-19】视频导入音频页时要当场转成 WAV;但**没有音轨的视频**硬转必然失败
    /// (实测:`ffmpeg -i 无音轨.mp4 -ac 2 -c:a pcm_s16le` 报 `Output file does not contain any stream`)。
    /// 所以先用探测拿声道数,0 声道就直接标注「没有音轨」,不去白跑一次 ffmpeg。
    /// </summary>
    public class AudioNoTrackTests
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

        private static string View()
            => File.ReadAllText(Path.Combine(RepoRoot(), "ImgUpscalerUI", "Views", "AudioView.xaml.cs"));

        [Fact]
        public void A_video_without_audio_is_not_sent_through_the_extractor()
        {
            var s = View();
            int probe = s.IndexOf("var (vdur, ch, _, _, _) = AudioService.Probe(p);", StringComparison.Ordinal);
            int guard = s.IndexOf("if (ch <= 0)", StringComparison.Ordinal);
            int extract = s.IndexOf("string? wav = await ExtractAudioToWavAsync(p);", StringComparison.Ordinal);
            Assert.True(probe > 0, "没有先探测源视频的声道数");
            Assert.True(guard > probe, "缺少「没有音轨」的判断");
            Assert.True(extract > guard, "无音轨的视频不能走到提取那一步");
        }

        [Fact]
        public void A_video_without_audio_says_so_instead_of_pretending_it_failed()
        {
            var s = View();
            Assert.Contains("视频(没有音轨,不需要提取)", s);
            Assert.Contains("视频没有音轨,保持原样", s);
        }

        [Fact]
        public void The_duration_still_comes_out_even_when_nothing_was_extracted()
        {
            var s = View();
            Assert.Contains("itNoAudio.DurationSec = (float)vdur;", s);
            Assert.Contains("it.DurationSec = (float)(dur > 0 ? dur : probedDur);", s);
        }
    }
}