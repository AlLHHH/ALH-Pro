using System.Text.RegularExpressions;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.Text;

namespace ALHPro.Views;

/// <summary>使用教程页:读取软件目录下的 使用教程.md 渲染(纯本地,无网络)。
/// 支持标记: # / ## / ### 标题、- 列表、表格(| 行)、**粗体**、`代码`、--- 分隔线。
/// 更新教程只需替换 md 文件,无需重新编译。</summary>
public sealed partial class TutorialView : Page
{
    public TutorialView()
    {
        InitializeComponent();
        LoadTutorial();
    }

    private void LoadTutorial()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "使用教程.md");
        if (!File.Exists(path))
        {
            ContentStack.Children.Add(new TextBlock
            {
                Text = "未找到「使用教程.md」(应与 ALHPro.exe 同目录)。",
                FontSize = 13,
                Opacity = 0.7,
            });
            return;
        }

        try
        {
            Render(File.ReadAllLines(path));
        }
        catch (Exception ex)
        {
            ContentStack.Children.Add(new TextBlock
            {
                Text = "教程加载失败:" + ex.Message,
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 232, 163, 61)),
                TextWrapping = TextWrapping.Wrap,
            });
        }
    }

    private void Render(string[] lines)
    {
        int i = 0;
        while (i < lines.Length)
        {
            var raw = lines[i].TrimEnd('\r');
            var line = raw.Trim();

            // ---- 表格:连续以 | 开头的行(跳过 --- 分隔行) ----
            if (line.StartsWith("|"))
            {
                var rows = new List<string[]>();
                while (i < lines.Length)
                {
                    var t = lines[i].Trim();
                    if (!t.StartsWith("|")) break;
                    t = t.Trim('|').Trim();
                    // 分隔行 |---|---| 跳过
                    if (Regex.IsMatch(t, @"^[\s:\-|]+$") && t.Contains('-'))
                    {
                        i++; continue;
                    }
                    rows.Add(t.Split('|').Select(s => s.Trim()).ToArray());
                    i++;
                }
                if (rows.Count > 0) ContentStack.Children.Add(BuildTable(rows));
                continue;
            }

            // ---- 标题 ----
            if (line.StartsWith("# "))
            {
                var tb = MakeText(line[2..], 21, FontWeights.SemiBold, 0.95);
                tb.Margin = new Thickness(0, 10, 0, 2);
                ContentStack.Children.Add(tb);
                i++; continue;
            }
            if (line.StartsWith("## "))
            {
                var tb = MakeText(line[3..], 17, FontWeights.SemiBold, 0.95);
                tb.Margin = new Thickness(0, 14, 0, 0);
                ContentStack.Children.Add(tb);
                i++; continue;
            }
            if (line.StartsWith("### "))
            {
                var tb = MakeText(line[4..], 14, FontWeights.SemiBold, 0.95);
                tb.Margin = new Thickness(0, 10, 0, 0);
                ContentStack.Children.Add(tb);
                i++; continue;
            }

            // ---- 分隔线 ----
            if (line == "---" || line == "***")
            {
                ContentStack.Children.Add(new Border
                {
                    Height = 1,
                    Background = (Brush)Application.Current.Resources["AppBorderBrush"],
                    Margin = new Thickness(0, 8, 0, 2),
                    Opacity = 0.8,
                });
                i++; continue;
            }

            // ---- 列表 ----
            if (line.StartsWith("- ") || line.StartsWith("* "))
            {
                var item = MakeText("•  " + line[2..], 13, FontWeights.Normal, 0.88);
                item.Margin = new Thickness(8, 0, 0, 0);
                item.TextWrapping = TextWrapping.Wrap;
                ContentStack.Children.Add(item);
                i++; continue;
            }

            // ---- 空行跳过 ----
            if (line.Length == 0) { i++; continue; }

            // ---- 普通段落 ----
            var p = MakeText(line, 13, FontWeights.Normal, 0.88);
            p.TextWrapping = TextWrapping.Wrap;
            p.Margin = new Thickness(0, 4, 0, 0);
            p.LineHeight = 20;
            ContentStack.Children.Add(p);
            i++;
        }
    }

    /// <summary>段落文本,支持 **粗体** 与 `代码` 行内标记。</summary>
    private static TextBlock MakeText(string text, double size, FontWeight weight, double opacity)
    {
        var tb = new TextBlock { FontSize = size, Opacity = opacity };
        if (weight != FontWeights.Normal) tb.FontWeight = weight;
        // 按 **...** 与 `...` 拆分,生成加粗/代码 Run
        foreach (var part in Regex.Split(text, @"(\*\*[^*]+\*\*|`[^`]+`)"))
        {
            if (part.Length == 0) continue;
            if (part.StartsWith("**") && part.EndsWith("**") && part.Length > 4)
            {
                tb.Inlines.Add(new Run { Text = part[2..^2], FontWeight = FontWeights.SemiBold });
            }
            else if (part.StartsWith("`") && part.EndsWith("`") && part.Length > 2)
            {
                tb.Inlines.Add(new Run
                {
                    Text = part[1..^1],
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 120, 180, 255)),
                });
            }
            else
            {
                tb.Inlines.Add(new Run { Text = part });
            }
        }
        return tb;
    }

    /// <summary>表格 → 网格(首行表头加粗),自适应列宽。</summary>
    private static Border BuildTable(List<string[]> rows)
    {
        int cols = rows.Max(r => r.Length);
        var grid = new Grid { ColumnSpacing = 14, RowSpacing = 5 };
        grid.Padding = new Thickness(12, 9, 12, 9);
        for (int c = 0; c < cols; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition());
            var row = rows[r];
            for (int c = 0; c < cols; c++)
            {
                var cell = MakeText(c < row.Length ? row[c] : "", 12.5, r == 0 ? FontWeights.SemiBold : FontWeights.Normal,
                    r == 0 ? 0.95 : 0.85);
                cell.TextWrapping = TextWrapping.Wrap;
                Grid.SetColumn(cell, c);
                Grid.SetRow(cell, r);
                grid.Children.Add(cell);
            }
        }
        return new Border
        {
            Child = grid,
            Background = (Brush)Application.Current.Resources["AppPanel2Brush"],
            BorderBrush = (Brush)Application.Current.Resources["AppBorderBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 6, 0, 4),
            Opacity = 0.95,
        };
    }
}
