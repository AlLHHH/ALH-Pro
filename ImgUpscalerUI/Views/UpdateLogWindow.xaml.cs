// UpdateLogWindow.xaml.cs — 更新日志独立窗口(作者的话置顶卡片 + 左侧版本列表 + 右侧内容)
// 独立 Window 而非 ContentDialog:内容宽度完全可控(ContentDialog 有 548px 默认上限,正文会被压窄)。
// 两种打开方式:升级后首次启动(fromStartup=true,底部「开始使用」,记录版本号);左侧栏/关于页随时查看。
using System;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ALHPro.Views;

public sealed partial class UpdateLogWindow : Window
{
    private readonly bool _fromStartup;
    private System.Collections.Generic.List<(string v, string title, string notes)> _entries = new();
    private bool _centered;

    public UpdateLogWindow(bool fromStartup = false)
    {
        this.InitializeComponent();
        _fromStartup = fromStartup;
        Title = "更新日志 · ALH Pro";
        try { AppWindow.Resize(new Windows.Graphics.SizeInt32(920, 640)); } catch { }

        AuthorText.Text =
            "来自作者的话\n软件当前仍处于早期开发阶段,功能方向与工程稳定性仍在持续完善中。尽管有开源模型提供底层能力支撑," +
            "但上层的调用适配、性能优化与长期维护,依然面临较大的工程挑战。作为个人发起的公益项目,我将在力所能及的范围内持续改进。" +
            "若您在使用过程中受益,欢迎通过赞赏给予一点支持,帮助项目走得更远。感谢每一份善意的理解和信任。";

        BuildEntries();
        VersionList.SelectionChanged += (_, _) =>
        {
            int i = VersionList.SelectedIndex;
            if (i >= 0 && i < _entries.Count) ContentText.Text = _entries[i].notes;
        };
        if (_entries.Count > 0) VersionList.SelectedIndex = 0;

        if (fromStartup)
        {
            PrimaryBtn.Visibility = Visibility.Visible;
            PrimaryBtn.Content = "开始使用";
        }
        else
        {
            PrimaryBtn.Visibility = Visibility.Visible;
            PrimaryBtn.Content = "关闭";
        }
        CloseBtn.Click += (_, _) => Close();
        this.Activated += (_, _) => CenterOnce();
    }

    /// <summary>居中显示在主窗口上方(激活后一次即可)。</summary>
    private void CenterOnce()
    {
        if (_centered || App.MainWindow == null) return;
        _centered = true;
        try
        {
            var owner = App.MainWindow.AppWindow.Position;
            var ownerSize = App.MainWindow.AppWindow.Size;
            var mine = AppWindow.Size;
            AppWindow.Move(new Windows.Graphics.PointInt32(
                owner.X + Math.Max(0, (ownerSize.Width - mine.Width) / 2),
                owner.Y + Math.Max(0, (ownerSize.Height - mine.Height) / 2)));
        }
        catch { }
    }

    private void BuildEntries()
    {
        _entries = new System.Collections.Generic.List<(string v, string title, string notes)>();
        string cur = "未找到更新说明(RELEASE_NOTES.md 缺失)。";
        var notesPath = Path.Combine(AppContext.BaseDirectory, "RELEASE_NOTES.md");
        try { if (File.Exists(notesPath)) cur = File.ReadAllText(notesPath); } catch { }
        cur = CleanNotes(cur);
        _entries.Add(($"v{UpdateChecker.CurrentVersion}", "当前版本", cur));
        var histPath = Path.Combine(AppContext.BaseDirectory, "release_history.json");
        try
        {
            if (File.Exists(histPath))
            {
                var hist = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.List<HistoryItem>>(File.ReadAllText(histPath));
                if (hist != null)
                    foreach (var h in hist)
                        if (!_entries.Any(en => en.v == "v" + h.v))
                            _entries.Add(("v" + h.v, h.title, CleanNotes(h.notes)));
            }
        }
        catch { }
        foreach (var en in _entries)
            VersionList.Items.Add(new TextBlock
            {
                Text = $"{en.v} · {en.title}",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 5, 0, 5),
            });
    }

    /// <summary>清洗 Markdown 痕迹(去掉 ##/**/====/---/括号内容留在标题外层的小瑕疵等),正文干净。</summary>
    private static string CleanNotes(string t)
    {
        if (string.IsNullOrEmpty(t)) return t;
        var sb = new System.Text.StringBuilder();
        foreach (var raw in t.Split('\n'))
        {
            var l = raw.Trim();
            if (l.Length == 0) { sb.AppendLine(); continue; }
            if (l.All(c => c == '=' || c == '-' || c == '#' || c == ' ')) continue;
            l = l.Replace("## ", "").Replace("### ", "").Replace("**【", "【").Replace("】**", "】")
                 .Replace("**", "").Replace("`", "").Replace("❕", "").Replace("🎉", "").Trim();
            if (l.Length == 0) continue;
            sb.AppendLine(l);
        }
        return sb.ToString().TrimEnd();
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_fromStartup)
        {
            // 记录已展示版本:下次升级才会再弹
            try
            {
                AppSettings.LastShownVersion = UpdateChecker.CurrentVersion;
                AppSettings.Save();
            }
            catch { }
        }
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private sealed class HistoryItem { public string v { get; set; } = ""; public string title { get; set; } = ""; public string notes { get; set; } = ""; }
}
