using System;
using System.IO;
using System.Linq;
using ALHPro;
using Xunit;

namespace AlhPro.Tests
{
    /// <summary>
    /// 拆帧的「输出时间戳重排」与「归因」。
    /// 真实事故:ALHPro_Diag_20261118_2044 —— 两个剪过的素材写到两千多帧时
    /// `Application provided invalid, non monotonically increasing dts to muxer in stream 0: 547 >= 547`,
    /// exit -22,而日志把它记成「硬解失败」,用户被引向显卡驱动。
    /// </summary>
    public class FrameExtractGuardTests
    {
        [Fact]
        public void The_stamp_filter_is_appended_after_the_existing_chain()
        {
            Assert.Equal("scale=1280:-2,setpts=N/FRAME_RATE/TB",
                FrameExtractGuard.WithMonotonicStamps("scale=1280:-2"));
        }

        [Fact]
        public void An_empty_chain_does_not_start_with_a_comma()
        {
            // 纯拆帧(不缩放、不降噪)时链是空的,拼出 ",setpts=..." 会被 ffmpeg 直接拒绝
            Assert.Equal(FrameExtractGuard.MonotonicStampFilter, FrameExtractGuard.WithMonotonicStamps(null));
            Assert.Equal(FrameExtractGuard.MonotonicStampFilter, FrameExtractGuard.WithMonotonicStamps(""));
            Assert.Equal(FrameExtractGuard.MonotonicStampFilter, FrameExtractGuard.WithMonotonicStamps("   "));
        }

        [Fact]
        public void A_trailing_comma_is_cleaned_before_appending()
        {
            Assert.Equal("scale=1280:-2,setpts=N/FRAME_RATE/TB",
                FrameExtractGuard.WithMonotonicStamps("scale=1280:-2,"));
        }

        [Fact]
        public void An_already_stamped_chain_is_reported_as_stamped()
        {
            Assert.True(FrameExtractGuard.HasMonotonicStamps("scale=1280:-2,setpts=N/FRAME_RATE/TB"));
            Assert.False(FrameExtractGuard.HasMonotonicStamps("scale=1280:-2"));
            Assert.False(FrameExtractGuard.HasMonotonicStamps(null));
        }

        [Fact]
        public void The_diagnostic_stderr_line_is_classified_as_a_source_timestamp_fault()
        {
            const string line = "[image2 @ 000001e5a2f0c0e0] Application provided invalid, non monotonically increasing dts to muxer in stream 0: 547 >= 547";
            Assert.True(FrameExtractGuard.IsSourceTimestampFault(line));
            // 不同 ffmpeg 版本的连字符写法
            Assert.True(FrameExtractGuard.IsSourceTimestampFault("non-monotonically increasing dts to muxer in stream 0"));
        }

        [Fact]
        public void A_plain_decode_failure_is_not_misread_as_a_timestamp_fault()
        {
            Assert.False(FrameExtractGuard.IsSourceTimestampFault("[h264 @ 0x1] Invalid data found when processing input"));
            Assert.False(FrameExtractGuard.IsSourceTimestampFault("Conversion failed!"));
            Assert.False(FrameExtractGuard.IsSourceTimestampFault(null));
        }

        [Fact]
        public void The_message_tells_the_user_the_source_is_at_fault_and_what_to_do()
        {
            var msg = FrameExtractGuard.DescribeSourceTimestampFault();
            Assert.Contains("时间刻度", msg);
            Assert.Contains("导出成普通 MP4", msg);
            // 不许把病因推给显卡/驱动
            Assert.DoesNotContain("显卡", msg);
            Assert.DoesNotContain("驱动", msg);
        }

        [Fact]
        public void Frame_extraction_uses_the_stamp_filter_for_both_hardware_and_software_paths()
        {
            var code = ReadFile("ImgUpscalerUI", "VideoService.cs");
            Assert.Contains("string stampVf = ALHPro.FrameExtractGuard.WithMonotonicStamps(vfExpr);", code);
            // 硬解、软解、日志三处都必须是重排后的链
            Assert.Equal(3, CountOccurrences(code, "{stampVf}"));
            // 旧的、会把源文件坏刻度原样透传的写法不能再出现
            Assert.DoesNotContain("-vf \\\"{vfExpr}\\\" -pix_fmt yuvj420p", code);
        }

        [Fact]
        public void A_timestamp_fault_is_judged_before_the_codec_is_marked_broken()
        {
            var code = ReadFile("ImgUpscalerUI", "VideoService.cs");
            var judge = code.IndexOf("ALHPro.FrameExtractGuard.IsSourceTimestampFault(ex.Message)", StringComparison.Ordinal);
            var latch = code.IndexOf("// 硬解失败 → 该编码标记坏,回退软解", StringComparison.Ordinal);
            Assert.True(judge > 0, "拆帧的 catch 里必须按 stderr 判定时间戳异常");
            Assert.True(latch > 0);
            Assert.True(judge < latch, "必须先判时间戳,再决定要不要给该编码打坏标记 —— 否则素材问题仍会被记成显卡解码失败");
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int n = 0, i = 0;
            while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
            return n;
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