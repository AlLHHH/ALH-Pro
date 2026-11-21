using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests
{
    /// <summary>
    /// 【2026-11-19 用户要求】视频拖进音频页时,导入当下就要转成 WAV 文件进列表 ——
    /// 列表里原来那条写着 xxx.mp4(「导出时提取音频」)看起来像没转换。
    /// 这几条是源码契约:提取必须发生在建列表项之前、列表项的路径必须是 wav、
    /// 失败时必须回落而不是让整个导入静默丢掉文件。
    /// </summary>
    public class AudioImportToWavTests
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
            => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

        private static string View()
            => Read("ImgUpscalerUI", "Views", "AudioView.xaml.cs");

        [Fact]
        public void A_dropped_video_is_converted_to_wav_before_the_item_is_added()
        {
            var s = View();
            int extract = s.IndexOf("string? wav = await ExtractAudioToWavAsync(p);", StringComparison.Ordinal);
            int addItem = s.IndexOf("var it = new AudioItem { Path = usePath };", StringComparison.Ordinal);
            Assert.True(extract > 0, "音频页没有调用 ExtractAudioToWavAsync");
            Assert.True(addItem > extract, "提取音频必须发生在建列表项之前,否则列表里还是那个视频");

            // 提取成功要换成 wav 的路径,不能继续用视频路径
            Assert.Contains("if (wav != null)", s);
            Assert.Contains("usePath = wav;", s);
        }

        [Fact]
        public void The_list_shows_the_wav_file_so_it_does_not_look_like_a_video()
        {
            var s = View();
            // 【2026-11-19】列表上显示的名字必须来自真正入列的那个文件(转换后的 wav),
            // 而不是源视频名:现在统一走 display 变量,失败才回落成「视频(提取失败…)」。
            Assert.Contains("it.Display = display;", s);
            Assert.Contains("System.IO.Path.GetFileName(usePath)", s);
            Assert.Contains("提取失败,点开始会再试", s);
            Assert.DoesNotContain("视频(导出时提取音频)", s);
        }

        [Fact]
        public void Extraction_is_lossless_and_reuses_the_same_file_next_time()
        {
            var s = View();
            Assert.Contains("AudioService.ConvertToWavSameRateAsync(videoPath, wav)", s);
            Assert.Contains("\"audio-src\"", s);
            Assert.Contains("PathFingerprint(videoPath)", s);
            // 复用的判据必须是「文件真的存在且有内容」,不能只看名字
            Assert.Contains("if (File.Exists(wav) && new FileInfo(wav).Length > 1024)", s);
        }

        [Fact]
        public void A_failed_extraction_falls_back_instead_of_dropping_the_file()
        {
            var s = View();
            Assert.Contains("catch (Exception ex)", s);
            Assert.Contains("AppLogger.Error(\"音频页:从视频提取音频失败\", ex);", s);
            Assert.Contains("return null;", s);
        }

        [Fact]
        public void The_import_entry_point_is_awaited_by_every_caller()
        {
            Assert.Contains("public async Task AcceptDroppedFilesAsync(string[] paths)", View());
            Assert.Contains("await AddFilesAsync(files);", View());
            Assert.Contains("await AddFilesAsync(files.Select(f => f.Path).ToArray());", View());

            var main = Read("ImgUpscalerUI", "Views", "MainPage.xaml.cs");
            Assert.Contains("await _audioView.AcceptDroppedFilesAsync(paths);", main);
            Assert.DoesNotContain("_audioView?.AcceptDroppedFilesAsync(paths);", main);
        }

        [Fact]
        public void Pasting_on_the_audio_page_reaches_the_audio_page()
        {
            // 【2026-11-19】实测踩到:在音频页按 Ctrl+V 时日志写「当前页面(audio)不收文件,已忽略」
            // —— 导入分派只写了 video/upscale,audio 落到 default。音频页自己明明有导入能力。
            var main = File.ReadAllText(Path.Combine(RepoRoot(), "ImgUpscalerUI", "Views", "MainPage.xaml.cs"));
            int audio = main.IndexOf("case \"audio\":", StringComparison.Ordinal);
            Assert.True(audio > 0, "导入分派里没有 audio 分支");
            int dispatch = main.IndexOf("_audioView.AcceptDroppedFilesAsync(paths)", audio, StringComparison.Ordinal);
            Assert.True(dispatch > audio, "audio 分支没有把文件交给音频页");
            Assert.Contains("case \"cutout\":", main);
        }
    }
}