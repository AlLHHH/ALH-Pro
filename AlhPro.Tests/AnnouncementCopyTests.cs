using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlhPro.Tests;

/// <summary>「界面口径」与「公告口径」不许各写各的(用户 2026-09-16 裁决:「1改」)。
/// 【为什么单测它】2026-09-16 那轮把下拉里的括号从 `（0.35s/帧）` 改成了 `（快）`,
/// 但**公告载体没跟着改** —— 于是软件里显示「快」、公告里还写「0.35s/帧」,
/// 而**编译、单测、跑功能全都不报错**,是用户自己看出来的。这种漂移只能靠断言守。
/// 【2026-10-14 收缩】原先这里还有两条"自训模型的名字/速度数字必须与界面逐字一致"的断言,
/// 依据是 `ExperimentalEsrgan` 那几个常量。四支自训 2x 模型已按用户要求整体删除
/// (用户:"还有那几个测试的模型可以删掉了")⇒ 常量和那两条断言一起删除。
/// 剩下两条与模型无关:一条禁"括号里写时间"的旧写法,一条禁"把已下线功能当新增/默认"。</summary>
public class AnnouncementCopyTests
{
    /// <summary>历史上真写出去过的"括号里放时间"写法(2026-09-15 那一版),三处载体里都不许再出现。</summary>
    private static readonly string[] RejectedTimeInParentheses =
    {
        "（0.35s/帧）", "（0.35 秒/帧）", "（1080p 约 0.35 秒/帧）",
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
