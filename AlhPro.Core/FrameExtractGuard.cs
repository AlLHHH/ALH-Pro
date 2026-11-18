using System;

namespace ALHPro
{
    /// <summary>
    /// 拆帧阶段的「输出时间戳重编号」与「失败归因」。
    ///
    /// 【2026-11-18 · 诊断包 ALHPro_Diag_20261118_2044】
    /// 拆帧必须用 `-fps_mode passthrough`(一个解码帧 = 一个 jpg,永不复制、永不丢弃),但它的副作用是
    /// 把源文件的时间戳原样交给 image2 写入层,而 image2 要求时间戳严格递增。用户素材(剪过的短视频)
    /// 里存在重复/回跳的 PTS,于是 ffmpeg 写到两千多帧时直接失败:
    ///   `[image2] Application provided invalid, non monotonically increasing dts to muxer in stream 0: 547 >= 547`
    ///   → `[vost#0:0/mjpeg] Task finished with error: Invalid argument` → exit -22,整条视频作废。
    /// 解法:滤镜链末尾按帧序号重排时间戳 —— 帧一张不多不少、顺序不变,只是不再照抄源文件的坏刻度。
    ///
    /// 另一件事:这类失败原先被记成「硬解(d3d11va)失败」并给该编码打上「坏」标记,把用户引向显卡驱动,
    /// 属于误诊 —— 硬解根本没失败,失败发生在最后写图片那一步,回退软解只会同样失败。
    /// </summary>
    public static class FrameExtractGuard
    {
        /// <summary>按帧序号重排时间戳的滤镜。帧数、顺序都不变,只保证时间戳严格递增。</summary>
        public const string MonotonicStampFilter = "setpts=N/FRAME_RATE/TB";

        /// <summary>
        /// 把重编号滤镜接到现有滤镜链末尾。原链为空(纯拆帧、不缩放不降噪)时单独用它,
        /// 不能拼出 ",setpts=..." 这种以逗号开头的非法链。
        /// </summary>
        public static string WithMonotonicStamps(string? vfExpr)
        {
            var chain = (vfExpr ?? "").Trim();
            while (chain.EndsWith(",", StringComparison.Ordinal)) chain = chain.Substring(0, chain.Length - 1).TrimEnd();
            if (chain.Length == 0) return MonotonicStampFilter;
            return chain + "," + MonotonicStampFilter;
        }

        /// <summary>这条滤镜链是否已经带了重编号(避免重复叠加)。</summary>
        public static bool HasMonotonicStamps(string? vfExpr)
            => (vfExpr ?? "").IndexOf(MonotonicStampFilter, StringComparison.Ordinal) >= 0;

        /// <summary>
        /// stderr / 异常文本里是否出现「源时间戳重复或回跳」的特征。命中即说明问题在素材、不在显卡解码。
        /// </summary>
        public static bool IsSourceTimestampFault(string? text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            // ffmpeg 原文与几种常见变体(不同版本连字符写法不一样)
            return text.IndexOf("non monotonically increasing dts", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("non-monotonically increasing dts", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("non monotonically increasing timestamp", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("Invalid timestamps", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>给用户看的话:说清是素材的问题、我们已经怎么处理、他还能做什么。</summary>
        public static string DescribeSourceTimestampFault()
            => "这个视频的时间刻度有重复或回跳(剪过的短视频常见),系统在写帧时拒绝了它。"
             + "已按帧序号重排过仍然失败 —— 请先用别的工具把它导出成普通 MP4(重新编码一次)再放进来。";
    }
}