using AlhPro.Core;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>「界面口径」与「公告口径」不许各写各的(用户 2026-09-16 裁决:「1改」)。
/// 【为什么单测它】2026-09-16 那轮把下拉里的括号从 `（0.35s/帧）` 改成了 `（快）`
/// (唯一源 = <see cref="ExperimentalEsrgan.SpeedTier"/>),但**公告载体没跟着改** ——
/// 于是软件里显示「快」、公告里还写「0.35s/帧」,而**编译、单测、跑功能全都不报错**,
/// 是用户自己看出来的。这种漂移只能靠断言守。
/// 【口径来源】期望值一律取自 <see cref="ExperimentalEsrgan"/>(模型名与括号的唯一源),
/// 本文件**不手抄任何字面量** —— 抄了就又会各写各的。</summary>
public class AnnouncementCopyTests
{
    /// <summary>历史上真写出去过的"括号里放时间"写法(2026-09-15 那一版),三处载体里都不许再出现。</summary>
    private static readonly string[] RejectedTimeInParentheses =
    {
        "（0.35s/帧）", "（0.35 秒/帧）", "（1080p 约 0.35 秒/帧）",
    };

    /// <summary>软件内的两处公告载体(改了必须重跑 deploy.ps1 才进发布版)。</summary>
    public static TheoryData<string[]> SoftwareCarriers => new()
    {
        new[] { "RELEASE_NOTES.md" },                    // 软件内「更新详情」的数据源
        new[] { "release_history.json" },                // 更新弹窗 / 更新列表的数据源
    };

    /// <summary>三处公告载体(软件内两处 + 官网一处)。</summary>
    public static TheoryData<string[]> AllCarriers => new()
    {
        new[] { "RELEASE_NOTES.md" },
        new[] { "release_history.json" },
        new[] { "website", "changelog.html" },           // 官网更新日志
    };

    /// <summary>被用户否掉的"括号里写时间"写法,一处都不许再回来(回来了就是又一次界面/公告不一致)。</summary>
    [Theory]
    [MemberData(nameof(AllCarriers))]
    public void No_carrier_reintroduces_the_rejected_time_in_parentheses(string[] parts)
    {
        var text = ReadRepoFile(parts);
        foreach (var bad in RejectedTimeInParentheses)
            Assert.DoesNotContain(bad, text);
    }

    /// <summary>提到这几支自训模型时,公告里写的必须是**与界面逐字相同**的那一串(`现实 · alhreal2x（快）`)。
    /// 【为什么逐字】公告说的名字和用户在下拉里看到的名字一旦不一致,用户就找不到公告在说哪一项。</summary>
    [Theory]
    [MemberData(nameof(SoftwareCarriers))]
    public void Announcements_name_the_models_exactly_as_the_ui_does(string[] parts)
    {
        var text = ReadRepoFile(parts);
        Assert.Contains(ExperimentalEsrgan.MenuText(ExperimentalEsrgan.Real2x), text);
        Assert.Contains(ExperimentalEsrgan.MenuText(ExperimentalEsrgan.Game2x), text);
    }

    /// <summary>**数字不能因为换了括号就丢**:用户 2026-09-15 明确要过"如实说明数字",
    /// 现在秒/帧挪到了悬停提示与下拉下方那行 —— 公告里要如实交代"数字在哪",并且数字本体仍在。</summary>
    [Theory]
    [MemberData(nameof(SoftwareCarriers))]
    public void Announcements_still_carry_the_measured_speed_number(string[] parts)
    {
        var text = ReadRepoFile(parts);
        Assert.Contains("0.35", text);
        Assert.Contains(ExperimentalEsrgan.SpeedText.Replace("1080p 约 ", ""), text);
    }

    /// <summary>**公告里不许把"已下线的功能"当成"新增/默认"** ——
    /// 2026-09-21「降噪强度」的「自动（先体检素材）」下线时,三个载体都还写着"新增…并放在最上面、默认选中" ✗,
    /// 而当时的契约只管"模型名一致"与"时间写法",拦不住这类过时 ✗(用户当场指出"公告要更新了")⇒ 补这条。
    /// 【判据只针对措辞】**不禁止提到它** —— 新公告里本来就该写"原「自动（先体检素材）」已下线";
    /// 禁止的是"把它当现存功能"的四种说法。以后下线任何功能,顺手把它的"新增/默认"措辞加进来即可。</summary>
    [Theory]
    [MemberData(nameof(AllCarriers))]
    public void No_carrier_claims_a_removed_feature_as_new_or_default(string[] parts)
    {
        var text = ReadRepoFile(parts);
        foreach (var stale in new[]
                 {
                     "新增「自动（先体检素材）」",
                     "新增「**自动（先体检素材）**」",
                     "并放在**最上面**、默认选中",
                     "并放在最上面、默认选中",
                     "新增「自动（先体检素材）」」",
                 })
            Assert.DoesNotContain(stale, text);
    }

    private static string ReadRepoFile(params string[] parts)
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
