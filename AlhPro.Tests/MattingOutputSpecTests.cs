using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>
/// 视频抠图两条输出路径的规格表契约(2026-09-22)。
///
/// 【为什么需要这张表】透明通道最怕"看着成功、其实没带 alpha":容器与 pix_fmt 必须成对正确
/// (mp4 装不了 alpha、vp9 不写 yuva420p 就是黑底),而 ffmpeg 不会因此报错 —— 用户与作者都很难察觉。
/// 把参数集中成表 + 单测钉住,比散在几百行命令行拼接里安全。
///
/// 【为什么换背景一路不写死编码器】应用里已经有一套成熟的编码器自适应策略
/// (`VideoService.EncoderArgs` / `SelectVideoEncoder`:厂商硬编 → 任一硬编 → libx264/libx265,
/// 还带探针实测)。抠图自己再写一份 libx264/libx265,就会出现"视频页走 NVENC、抠图页走软编"的
/// 双策略漂移,而且用户机器上有硬编时凭空慢几倍。所以换背景一路只钉【容器 + pix_fmt + 音频 + faststart】,
/// 编码器交给现有策略(UseGlobalEncoder = true)。
///
/// 【音频为什么两种写法】实测应用惯例(`VideoService.cs:3715`):能用就 `-c:a copy`,源音频不是
/// aac/mp3/ac3/eac3 才转 aac。而透明通道的两种容器都装不了 aac:WebM 只能 Opus/Vorbis,
/// MOV 走 PCM —— 照抄 `-c:a copy` 会直接输出失败或丢音轨,所以这一路必须显式指定音频编码。
/// </summary>
public class MattingOutputSpecTests
{
    // ---- 换背景:普通 mp4,不带 alpha,兼容性优先 ----

    [Fact]
    public void Background_leaves_the_video_encoder_to_the_global_policy()
    {
        var s = MattingOutputSpecs.ForBackground();
        Assert.True(s.UseGlobalEncoder);              // 不写死编码器:与视频页共用一套自适应策略
        Assert.Equal("", s.VideoCodec);               // 空 = 由调用方按 EncoderArgs 决定
        Assert.Equal("mp4", s.Container);
        Assert.Equal("yuv420p", s.PixelFormat);       // 不带 alpha(yuv420p 兼容性最好)
    }

    [Fact]
    public void Background_copies_audio_and_adds_faststart()
    {
        var s = MattingOutputSpecs.ForBackground();
        Assert.Equal("-c:a copy", s.AudioArgs);       // 与 VideoService.cs:3715 同一口径(不适合才转 aac)
        Assert.Contains("+faststart", s.ExtraVideoArgs);
    }

    // ---- 透明通道:必须带 alpha ----

    [Fact]
    public void Transparent_webm_is_vp9_alpha()
    {
        var s = MattingOutputSpecs.ForTransparent("webm");
        Assert.Equal("webm", s.Container);
        Assert.Equal("libvpx-vp9", s.VideoCodec);
        Assert.Equal("yuva420p", s.PixelFormat);      // 带 alpha 的 pix_fmt;实测本机 ffmpeg 支持
        Assert.False(s.UseGlobalEncoder);             // 全局策略只会给 yuv420p(丢 alpha)⇒ 不能走它
        Assert.Equal("-c:a libopus", s.AudioArgs);    // WebM 装不了 aac,必须 Opus
    }

    [Fact]
    public void Transparent_mov_is_prores4444()
    {
        var s = MattingOutputSpecs.ForTransparent("mov");
        Assert.Equal("mov", s.Container);
        Assert.Equal("prores_ks", s.VideoCodec);
        Assert.Equal("yuva444p10le", s.PixelFormat);  // ProRes 4444 才带 alpha,且是 10bit 4:4:4
        Assert.Equal("-c:a pcm_s16le", s.AudioArgs);
        Assert.Contains("4444", s.ExtraVideoArgs);    // 不写 profile 会编成 422(丢 alpha)
    }

    /// <summary>mp4/mkv 等容器装不了 alpha:必须回落,不许"静默输出一份没 alpha 的 mp4"。</summary>
    [Theory]
    [InlineData("mp4")]
    [InlineData("mkv")]
    [InlineData("avi")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("WEBM ")]      // 大小写/空格归一化后仍要认出来
    public void Unknown_container_falls_back_to_webm_or_is_normalized(string? container)
    {
        var s = MattingOutputSpecs.ForTransparent(container);
        Assert.Equal("webm", s.Container);
        Assert.Equal("yuva420p", s.PixelFormat);
    }

    /// <summary>无论输入什么,透明通道一路永远不能返回"装不了 alpha"的组合。</summary>
    [Theory]
    [InlineData("webm")]
    [InlineData("mov")]
    [InlineData("mp4")]
    [InlineData("prores")]
    [InlineData(null)]
    public void Transparent_never_returns_a_spec_without_alpha(string? container)
    {
        var s = MattingOutputSpecs.ForTransparent(container);
        Assert.Contains("yuva", s.PixelFormat);
        Assert.Contains(s.Container, new[] { "webm", "mov" });
        Assert.False(s.UseGlobalEncoder);
        Assert.True(s.VideoCodec.Length > 0);
    }

    /// <summary>给用户看的容器名(界面下拉)必须与实现认的两种一致,避免"选项叫 ProRes、实现不认"。</summary>
    [Theory]
    [InlineData("webm")]
    [InlineData("mov")]
    public void Every_advertised_container_is_implemented(string container)
    {
        Assert.Contains(container, MattingOutputSpecs.TransparentContainers);
        var s = MattingOutputSpecs.ForTransparent(container);
        Assert.Equal(container, s.Container);
    }
}
