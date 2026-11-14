using System;
using System.IO;
using System.Linq;
using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

/// <summary>
/// 2026-11-14 用户反馈:「明明是新版本,却还是显示要更新的横幅」。
/// 真凶是状态栏那条常驻提示「旧版本遇到的问题已在最新版修正,建议更新」——它写给旧用户,却人人可见。
/// 这里钉住三件事:门槛判断本身、提示文件里确实写了门槛、客户端确实按门槛过滤。
/// </summary>
public class TipVersionGateTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            if (Directory.Exists(Path.Combine(d.FullName, "AlhPro.Core")) &&
                Directory.Exists(Path.Combine(d.FullName, "ImgUpscalerUI")) &&
                Directory.Exists(Path.Combine(d.FullName, "hint")))
                return d.FullName;
            d = d.Parent;
        }
        return AppContext.BaseDirectory;
    }

    // ---------- 门槛判断 ----------

    [Theory]
    [InlineData(null, "1.4.9", true)]           // 没写门槛:所有版本都显示
    [InlineData("", "1.4.9", true)]
    [InlineData("1.4.9", "1.4.9", false)]       // 装到门槛版本就不显示(用户遇到的就是这条)
    [InlineData("1.4.9", "1.4.10", false)]
    [InlineData("1.4.9", "1.4.8", true)]        // 更旧的版本才显示
    [InlineData("v1.4.9", "1.4.8", true)]
    [InlineData("latest", "1.4.8", false)]      // 门槛写坏了:宁可不显示
    public void Gate_keeps_upgrade_nudges_away_from_machines_that_already_upgraded(
        string? onlyForOlderThan, string current, bool expected)
    {
        Assert.Equal(expected, UpdateTag.AppliesTo(onlyForOlderThan, current));
    }

    // ---------- 文件与代码真的用了它 ----------

    [Fact]
    public void The_upgrade_nudge_payload_is_gated_to_old_builds()
    {
        var path = Path.Combine(RepoRoot, "hint", "hint1.json");
        Assert.True(File.Exists(path), "找不到 hint/hint1.json");
        var json = File.ReadAllText(path);
        Assert.Contains("\"onlyForOlderThan\"", json);
        // 文案本身保留(旧用户还需要被告知),但必须带门槛
        var idx = json.IndexOf("onlyForOlderThan", StringComparison.Ordinal);
        Assert.Contains("1.4.9", json[idx..]);
    }

    [Fact]
    public void Client_filters_tips_by_version_before_showing_them()
    {
        var cs = File.ReadAllText(Path.Combine(RepoRoot, "ImgUpscalerUI", "TipFetcher.cs"));
        Assert.Contains("OnlyForOlderThan", cs);
        Assert.Contains("UpdateTag.AppliesTo(tip.OnlyForOlderThan, UpdateChecker.CurrentVersion)", cs);
        Assert.Contains("GetString(el, \"onlyForOlderThan\")", cs);
    }

    [Fact]
    public void No_hint_payload_asks_users_to_update_without_a_version_gate()
    {
        var dir = Path.Combine(RepoRoot, "hint");
        foreach (var f in Directory.GetFiles(dir, "hint*.json").OrderBy(x => x, StringComparer.Ordinal))
        {
            var json = File.ReadAllText(f);
            bool nags = json.Contains("建议更新") || json.Contains("请更新") || json.Contains("尽快升级");
            if (!nags) continue;
            Assert.True(json.Contains("onlyForOlderThan"),
                $"{Path.GetFileName(f)} 在劝人升级,却没有写 onlyForOlderThan —— 装到最新版的人也会看到");
        }
    }
}