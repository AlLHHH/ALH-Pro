using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>
/// 【2026-11-14 用户定调 · 第 1 条】发版流程要闭环。
/// 事故:三次让用户帮忙验证,用户跑的都是旧包(拖放、5060、v1.4.8),因为"改完没提交/没打包/没 tag"从来不报错。
/// 这里把发版闸门的存在与位置钉住:闸门没了,那条"改完忘了提交"的路就又会打开。
/// </summary>
public class ReleaseGateTests
{
    private static string RepoRoot
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "ImgUpscalerUI", "ImgUpscalerUI.csproj")))
                d = d.Parent;
            if (d == null) throw new InvalidOperationException("找不到仓库根目录");
            return d.FullName;
        }
    }

    private static string Script => File.ReadAllText(Path.Combine(RepoRoot, "发版.ps1"));

    [Fact]
    public void One_command_release_gate_exists()
    {
        Assert.True(File.Exists(Path.Combine(RepoRoot, "发版.ps1")), "缺少 发版.ps1:发版还是会靠人记。");
        var s = Script;
        Assert.Contains("-Version", s);
        Assert.Contains("-DryRun", s);
    }

    [Fact]
    public void Gate_refuses_to_pack_uncommitted_changes()
    {
        var s = Script;
        Assert.Contains("git status --porcelain", s);
        Assert.Contains("没提交", s);
        // 编排顺序:先查闸门,再动构建 —— 不许"先打包再检查"
        var gate = s.IndexOf("git status --porcelain", StringComparison.Ordinal);
        var deploy = s.IndexOf("deploy.ps1'", StringComparison.Ordinal);
        Assert.True(gate > 0 && deploy > gate, "工作区检查必须排在 deploy.ps1 之前");
    }

    [Fact]
    public void Gate_checks_version_and_all_four_announcement_carriers()
    {
        var s = Script;
        Assert.Contains("<Version>", s);
        Assert.Contains("installer.iss", s);
        Assert.Contains("installer_full.iss", s);
        Assert.Contains("installer_nocut.iss", s);
        Assert.Contains("RELEASE_NOTES.md", s);
        Assert.Contains("release_history.json", s);
        Assert.Contains("发布公告_v", s);
        Assert.Contains("website\\changelog.html", s);
    }

    [Fact]
    public void Gate_requires_a_signed_tag_pointing_at_this_commit()
    {
        var s = Script;
        Assert.Contains("git tag -s", s);
        Assert.Contains("refs/tags/v", s);
        Assert.Contains("本次提交是", s);   // tag 指错提交时要说清"tag 指哪、这次是哪"
    }

    [Fact]
    public void Gate_also_stages_the_site_files_for_upload()
    {
        var s = Script;
        Assert.Contains("官网上传_v", s);
        Assert.Contains("style.css", s);
        Assert.Contains("上传说明.txt", s);
    }
}