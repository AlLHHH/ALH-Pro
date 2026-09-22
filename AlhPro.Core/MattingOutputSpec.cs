namespace AlhPro.Core;

/// <summary>视频抠图的两种输出形态(界面上的两个出口)。</summary>
public enum MattingOutputKind
{
    /// <summary>换背景:前景与背景合成后走普通 RGB 管线(保留音频)。</summary>
    Background,
    /// <summary>透明通道:前景 + alpha 合成成带 alpha 的视频(给剪辑软件/OBS 用)。</summary>
    Transparent,
}

/// <summary>一路输出的编码规格。字段刻意都做成"ffmpeg 参数片段",调用方直接拼,不再二次解释。
///
/// <paramref name="UseGlobalEncoder"/>=true 表示【不要自己指定视频编码器】,交给应用既有的
/// 自适应策略(`VideoService.EncoderArgs` / `SelectVideoEncoder`:厂商硬编 → 任一硬编 → libx264/libx265),
/// 此时 <see cref="VideoCodec"/> 为空。换背景一路必须这样:否则会出现"视频页走 NVENC、抠图页走软编"
/// 的双策略漂移,用户机器上有硬编时凭空慢几倍。</summary>
public sealed record MattingOutputSpec(
    string Container,
    string VideoCodec,
    string PixelFormat,
    string AudioArgs,
    string ExtraVideoArgs,
    bool UseGlobalEncoder);

/// <summary>视频抠图的输出规格决策表(设计见 docs/2026-09-22-video-matting.md §四)。
/// 【实测依据】本机 `发布版/engines/ffmpeg` 实测(2026-09-22,160×120 左半不透明/右半全透明的 RGBA 源图,
/// 编码后抽出 alpha 平面数点数:两种容器都是 2400 透明 / 2400 不透明,与源图完全一致):
///   · WebM + libvpx-vp9 + yuva420p:文件里有 `alpha_mode=1`、alpha 数据真实存在 ✓
///   · MOV + prores_ks + `-profile:v 4444`:alpha 直接可解 ✓(实测 ffmpeg 实际输出 yuva444p12le,
///     即便请求 10le;alpha 不受影响,故不强求位深)
/// 【⚠ 读回时必须强制 libvpx 解码器】同一份 WebM,**ffmpeg 的原生 `vp9` 解码器会静默丢弃 alpha**
/// (只报 yuv420p,不报错),只有 `-c:v libvpx-vp9` 才解出 yuva420p。所以将来任何"把成品再读一遍"的
/// 环节(预览、重封装、对比)都要带 `-c:v libvpx-vp9`,否则会得到一份看起来不透明的画面 ✗。
/// 【音频口径】换背景沿用应用惯例 `-c:a copy`(VideoService.cs:3715);
/// 透明通道两种容器都装不了 aac ⇒ 必须显式指定(WebM→Opus,MOV→PCM),否则直接失败或丢音轨。</summary>
public static class MattingOutputSpecs
{
    /// <summary>透明通道支持的容器(界面下拉就用这一份,避免"选项叫 ProRes、实现不认")。</summary>
    public static readonly string[] TransparentContainers = { "webm", "mov" };

    /// <summary>换背景:普通 mp4、不带 alpha(兼容性优先:微信/抖音/B站/OBS 都能播)。
    /// 编码器交给全局策略(见 <see cref="MattingOutputSpec.UseGlobalEncoder"/>),这里只钉死
    /// pix_fmt=yuv420p 与 faststart(网页/播放器不必下完整个文件)。</summary>
    public static MattingOutputSpec ForBackground()
        => new(
            Container: "mp4",
            VideoCodec: "",
            PixelFormat: "yuv420p",
            AudioArgs: "-c:a copy",
            ExtraVideoArgs: "-movflags +faststart",
            UseGlobalEncoder: true);

    /// <summary>透明通道:vp9-alpha(体积小、OBS 与常见剪辑软件认)或 ProRes 4444(画质最高、体积约十倍)。
    /// 【mp4 必须回落 webm】mp4/mkv 标准容器装不了 alpha,照原样输出会得到"看着是黑底"的假透明。
    /// 【vp9 的 -auto-alt-ref 0】VP9 的 alt-ref 帧会让部分播放器把 alpha 边渲染成黑边,故关掉。</summary>
    public static MattingOutputSpec ForTransparent(string? container)
        => (container ?? "").Trim().ToLowerInvariant() switch
        {
            "mov" => new(
                Container: "mov",
                VideoCodec: "prores_ks",
                PixelFormat: "yuva444p10le",
                AudioArgs: "-c:a pcm_s16le",
                ExtraVideoArgs: "-profile:v 4444",
                UseGlobalEncoder: false),

            // "webm" 与任何未知/装不了 alpha 的容器都落这里
            _ => new(
                Container: "webm",
                VideoCodec: "libvpx-vp9",
                PixelFormat: "yuva420p",
                AudioArgs: "-c:a libopus",
                ExtraVideoArgs: "-auto-alt-ref 0",
                UseGlobalEncoder: false),
        };
}
