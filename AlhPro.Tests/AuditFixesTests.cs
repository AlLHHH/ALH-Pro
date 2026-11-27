using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>
/// 【2026-11-27 审查】1.4.11 发布前两轮代码审查揪出来的几处真缺陷的回归锁。
/// 惯例:界面的东西只能读源码做契约断言(测试工程只引用 AlhPro.Core),所以这里一半是 Core 行为、一半是源码契约。
/// </summary>
public class AuditFixesTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var p = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(p)) return p;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("找不到仓库文件:" + string.Join("/", parts));
    }

    private static string Read(string rel) => File.ReadAllText(RepoFile(rel.Split('/')));

    [Fact]
    public void 音频后缀有统一清单_且包含常见容器()
    {
        var exts = AlhPro.DroppedPaths.AudioExtensions;
        foreach (var e in new[] { ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg" })
            Assert.Contains(e, exts);
        // 图片清单补了 HEIC/HEIF(iPhone 直出照片),否则拖进来会被当成「不是图片」
        Assert.Contains(".heic", AlhPro.DroppedPaths.ImageExtensions);
        Assert.Contains(".heif", AlhPro.DroppedPaths.ImageExtensions);
    }

    [Fact]
    public void 图片页不再自己抄一份后缀白名单()
    {
        string s = Read("ImgUpscalerUI/Views/ImageToolGrid.xaml.cs");
        // 分散维护的白名单漏过 .gif/.jfif:gif 拖进图片页被整批丢弃,还提示「这是视频」
        Assert.Contains("AlhPro.DroppedPaths.ImageExtensions.Contains", s);
        // 本地再抄一份白名单必然漂移 —— 断言文件里不再出现自建的后缀数组
        Assert.DoesNotContain("\".gif\", \".jpg\"", s);
    }

    [Fact]
    public void 拖放分流认得音频_并且音频页吞得下视频和音频()
    {
        string s = Read("ImgUpscalerUI/Views/MainPage.xaml.cs");
        Assert.Contains("DroppedPaths.AudioExtensions", s);
        Assert.Contains("asAudio", s);
        // 音频页拖 mp3 必须进音频页,不能判成非法
        Assert.Contains("\"audio\"", s);
        // 没有 StorageItems(压缩包内/网盘在线文件)以前直接 return,松手毫无反应 —— 现在必须给提示
        Assert.Contains("DropNoLocalPathHint", s);
    }

    [Fact]
    public void 命令行与系统拖放的导入也会按音频分流()
    {
        string s = Read("ImgUpscalerUI/Views/MainPage.xaml.cs");
        int i = s.IndexOf("ImportFromShellAsync", StringComparison.Ordinal);
        Assert.True(i >= 0);
        string body = s.Substring(i, Math.Min(3000, s.Length - i));
        Assert.Contains("hasAudio", body);
        Assert.Contains("系统拖放(音频)", body);
    }

    [Fact]
    public void 设置文件是原子写入_并且读坏了能退回备份()
    {
        string s = Read("ImgUpscalerUI/AppSettings.cs");
        Assert.Contains("WriteAtomic", s);
        Assert.Contains("File.Replace(", s);
        Assert.Contains(".bak", s);
        // 启动计数每次开机都写,截断一次就把显卡/临时目录/问卷状态全丢
        Assert.DoesNotContain("File.WriteAllText(FilePath,", s);
    }

    [Fact]
    public void 音频缓存只认同一份源文件_并且会被定期清理()
    {
        string v = Read("ImgUpscalerUI/Views/AudioView.xaml.cs");
        Assert.Contains(".stamp", v);
        Assert.Contains("LastWriteTimeUtc", v);
        string app = Read("ImgUpscalerUI/App.xaml.cs");
        Assert.Contains("PruneAudioWavCache", app);
    }

    [Fact]
    public void 占盘估算与路线建议都带上每批并存帧()
    {
        string s = Read("ImgUpscalerUI/Views/VideoView.xaml.cs");
        // 界面原来传 batchFrames: 0,守门用真实批大小 ⇒「界面说够、点开始报临时磁盘不足」
        Assert.Contains("GetVideoBatchPlan(", s);
        Assert.Contains("NeedGbFor(", s);
        Assert.DoesNotContain("interpOn, interpScale, 1.0, 0)", s);
    }
}