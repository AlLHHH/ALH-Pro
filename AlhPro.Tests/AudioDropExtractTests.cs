using System;
using System.IO;
using Xunit;

namespace AlhPro.Tests
{
    /// <summary>
    /// 2026-11-19 两项用户要求:
    /// ① 视频页只收视频 —— 图片拖进视频页会被挡掉并提示去图片页(以前会当成视频加进列表)。
    /// ② 视频拖到音频页 = 直接提取音频,不再切去视频页。
    ///
    /// UI 层没法在单元测试里真拖放,所以这里读源码做契约:关键分支写在哪、按什么条件走。
    /// </summary>
    public class AudioDropExtractTests
    {
        static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null)
            {
                if (Directory.Exists(Path.Combine(d.FullName, "ImgUpscalerUI", "Views")))
                    return d.FullName;
                d = d.Parent;
            }
            throw new InvalidOperationException("找不到仓库根目录");
        }

        static string Read(params string[] parts)
        {
            var p = Path.Combine(new[] { RepoRoot() }.ConcatArrays(parts));
            return File.ReadAllText(p);
        }

        [Fact]
        public void Videos_dropped_on_the_audio_page_extract_audio_instead_of_jumping_away()
        {
            var src = Read("ImgUpscalerUI", "Views", "MainPage.xaml.cs");
            Assert.Contains("bool curAudio = _currentTag == \"audio\";", src);
            // 2026-11-27:音频页现在视频和纯音频都收(mp3 拖到空白处以前被判非法)
            Assert.Contains("if (curAudio && (asVideos.Length > 0 || asAudio.Length > 0)) target = \"audio\";", src);
            // 音频目标必须排在最前面,否则会先被「视频→视频页」那条接走
            var audioIdx = src.IndexOf("if (curAudio && (asVideos.Length > 0 || asAudio.Length > 0)) target = \"audio\";", StringComparison.Ordinal);
            var videoIdx = src.IndexOf("else if (asVideos.Length > 0 && (curVideo || asImages.Length == 0)) target = \"video\";", StringComparison.Ordinal);
            Assert.True(audioIdx > 0 && videoIdx > audioIdx, "音频页的判断必须在视频页之前");
        }

        [Fact]
        public void The_audio_page_dispatch_reuses_the_same_import_path_as_the_picker()
        {
            var src = Read("ImgUpscalerUI", "Views", "MainPage.xaml.cs");
            Assert.Contains("await _audioView.AcceptDroppedFilesAsync(paths);", src);
            // 音频目标传的是「视频 + 音频」两类的并集(视频里提取音轨、音频直接进列表)
            Assert.Contains("paths = asVideos.Concat(asAudio)", src);
        }

        [Fact]
        public void The_audio_page_accepts_files_through_one_entry_point()
        {
            var src = Read("ImgUpscalerUI", "Views", "AudioView.xaml.cs");
            Assert.Contains("public async Task AcceptDroppedFilesAsync(string[] paths)", src);
            var idx = src.IndexOf("public async Task AcceptDroppedFilesAsync(string[] paths)", StringComparison.Ordinal);
            var body = src.Substring(idx, Math.Min(900, src.Length - idx));
            Assert.Contains("await AddFilesAsync(paths);", body);
            // 出错必须落日志,不能静默
            Assert.Contains("AppLogger.Error(\"音频页拖放导入失败\", ex);", body);
        }

        [Fact]
        public void The_video_page_refuses_images_and_points_at_the_right_page()
        {
            var src = Read("ImgUpscalerUI", "Views", "VideoView.xaml.cs");
            Assert.Contains("AlhPro.DroppedPaths.ImageExtensions", src);
            Assert.Contains("拖到「图片超分」或「智能抠图」页", src);
            // 全是图片时必须直接返回,不能继续走进 ffprobe 探测
            Assert.Contains("if (paths.Length == 0) return;", src);
        }
    }

    static class ArrayConcatExt
    {
        public static string[] ConcatArrays(this string[] head, string[] tail)
        {
            var all = new string[head.Length + tail.Length];
            Array.Copy(head, all, head.Length);
            Array.Copy(tail, 0, all, head.Length, tail.Length);
            return all;
        }
    }
}