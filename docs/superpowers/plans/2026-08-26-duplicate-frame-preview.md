# 重复帧预览(右侧预览区显示重复帧)Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development (recommended) or executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在视频列表项上显示一个"重复帧占比 % + 内容帧率"徽标,并在右侧视频预览覆盖层用一条时间轴高亮"哪些时间段重复帧集中",配合一个"据此设置去重"按钮,让用户不用盲猜就能决定去重开/关与档位。

**Architecture:** 复用 `VideoService` 里已有的 `DetectDupFrames*` / `SampleGray` / `MeanAbsDiff` / `BuildFrameDurationsAsync` 检测逻辑——入列时用**轻量抽样预估**(每 8 帧取 1 + 小分辨率灰度 + 相邻 SAD),选中项时按需跑**全量分析**(同处理时的检测器),把结果存到 `VideoItem` 新增字段并绑定到 XAML。预览覆盖层的时间轴用代码后置往一个命名容器里塞 `Rectangle` 实现(与现有 `TrimRange`/`TrimThumb` 的后置做法一致)。

**Tech Stack:** C# / WinUI 3 (Microsoft.UI.Xaml) / .NET 8 / ffmpeg + rife-ncnn-vulkan 子进程,项目 `ALHPro.ImgUpscalerUI`,文件位于 `D:\deep\alh-pro\ImgUpscalerUI\`。

**Spec:** 本计划即实现规格。范围已与用户确认:①入列后台轻量预估;②选中时全文分析;③徽标(占比%+内容帧率)+ 预览区时间轴重复分布条;④"便于应用"= 一键按分析结果设置去重档位。数据与处理阶段**共用同一套检测逻辑**,避免"预览数字 ≠ 处理数字"。

## Global Constraints

- 本项目**没有单元测试工程**。所有"验证"步骤统一为:`dotnet build`(编译通过)+ 手动运行 App 截图/操作确认。纯逻辑函数(EstimateFromSads / BuildDupSegments)写成 `internal static` 纯函数,便于将来若有测试工程可单独测。
- 命名空间:所有新代码放在 `namespace ALHPro.ImgUpscalerUI;`。沿用现有"完全限定 `System.Collections.Generic.List<>`"或 `using System.Collections.Generic;` 皆可(与所在文件现有风格一致,`VideoService.cs` 多用完全限定)。
- 不新建大型文件;新逻辑放 `VideoService.cs`(检测/分析)与 `Views\VideoView.xaml.cs`(UI 绑定)。`VideoItem` 类就在 `VideoView.xaml.cs:20`。
- 保持现有命名/流程:去重档位 0=关,1=智能,2=动漫,3=标准,5=敏感,6=手动(见 `VideoView.xaml.cs` 的 `VideoSettings` 与 `VideoService.ProcessVideoAsync`)。`effectiveFps`(内容帧率)口径 = `inFps * frameCount / origCount`,预估时用 `inFps * (1 - dupRatio)` 对齐同一口径。
- 隐私:只本地分析源视频,不联网、不打日志外发。

---

### Task 1: 增加重复帧数据模型 + 纯分析助手(VideoService)

**Files:**
- Modify: `D:\deep\alh-pro\ImgUpscalerUI\VideoService.cs`(结尾附近,`DedupTooStrongException` 之后,加新类型与纯函数)

**Interfaces:**
- Consumes: 无。
- Produces(供 Task 2/3/5 使用):
  - `public sealed class DupProfile { public double RatioPct; public double? ContentFps; public string Summary; public System.Collections.Generic.List<DupSegInfo> Segs; }`
  - `public readonly record struct DupSegInfo(double Start, double End, double Ratio);`
  - `internal static (double ratioPct, double? contentFps) EstimateFromSads(System.Collections.Generic.List<double> sads, double inFps)`
  - `internal static System.Collections.Generic.List<DupSegInfo> BuildDupSegments(System.Collections.Generic.HashSet<int> drop, System.Collections.Generic.List<double> frameDurs, int buckets)`

- [ ] **Step 1: 添加类型与纯函数**

在 `VideoService.cs` 末尾(`RunCaptureAsync`/`RunAsync` 之后)追加:

```csharp
/// <summary>重复帧预览切片(时间轴上的一段,Start/End 秒,Ratio=该段内重复帧时间占比 0~1)。</summary>
public readonly record struct DupSegInfo(double Start, double End, double Ratio);

/// <summary>重复帧分析结果(供 UI 徽标 + 时间轴预览;与处理阶段口径一致)。</summary>
public sealed class DupProfile
{
    public double RatioPct;              // 全片重复帧占比(%) 0~100
    public double? ContentFps;           // 估算内容帧率(null=信息不足)
    public string Summary = "";          // 展示用文案,如 "重复 62% · 内容约 12fps · 建议去重-动漫"
    public System.Collections.Generic.List<DupSegInfo> Segs = new();   // 时间轴分布(可为空=未做全文分析)
}

/// <summary>纯函数:从相邻帧差 SAD 序列估算"重复帧占比% + 内容帧率"。
/// 口径与处理阶段一致:内容帧率 ≈ inFps * (1 - 重复占比)。
/// sadThr 用"几乎相同"阈值;无样本/全噪声时返回 (0, null)。</summary>
internal static (double ratioPct, double? contentFps) EstimateFromSads(
    System.Collections.Generic.List<double> sads, double inFps)
{
    if (sads.Count == 0 || inFps <= 0) return (0, null);
    double sadThr = 3.0;                      // 与 DetectDupFramesWithSsim 默认快筛一致
    int nearDup = 0;
    foreach (var s in sads) if (s < sadThr) nearDup++;
    double ratio = (double)nearDup / sads.Count;
    if (ratio < 0.02) return (0, null);       // 几乎无重复:不给内容帧率(信息不足)
    double ratioPct = ratio * 100.0;
    double? contentFps = inFps * (1.0 - ratio);
    return (ratioPct, contentFps);
}

/// <summary>纯函数:把"被删帧号集合 + 每帧真实时长"压成 N 个时间桶的重复占比序列,供时间轴画条。
/// 桶内 Ratio = 该桶时长里"被删(重复)帧"所占比例。buckets=时间桶数(建议 = round(时长秒*2))。</summary>
internal static System.Collections.Generic.List<DupSegInfo> BuildDupSegments(
    System.Collections.Generic.HashSet<int> drop,
    System.Collections.Generic.List<double> frameDurs, int buckets)
{
    var segs = new System.Collections.Generic.List<DupSegInfo>();
    if (frameDurs == null || frameDurs.Count == 0 || buckets <= 0) return segs;
    int n = frameDurs.Count;
    double total = 0;
    foreach (var d in frameDurs) total += d;
    if (total <= 0) return segs;
    var perBucket = new double[buckets];         // 桶内"重复时长"累计
    var bucketDur = new double[buckets];         // 桶内总时长
    double acc = 0;
    for (int i = 0; i < n; i++)
    {
        double d = frameDurs[i];
        int b = (int)System.Math.Min(buckets - 1, (int)(acc / total * buckets));
        bucketDur[b] += d;
        if (drop.Contains(i + 1)) perBucket[b] += d;   // 该帧是重复帧
        acc += d;
    }
    for (int b = 0; b < buckets; b++)
    {
        double start = total * b / buckets;
        double end = total * (b + 1) / buckets;
        double ratio = bucketDur[b] > 0 ? perBucket[b] / bucketDur[b] : 0;
        segs.Add(new DupSegInfo(start, end, ratio));
    }
    return segs;
}
```

- [ ] **Step 2: 验证编译**

Run: `dotnet build D:\deep\alh-pro\ImgUpscalerUI\ImgUpscalerUI.csproj -c Release`
Expected: 编译通过(新增类型无编译错误)。

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "feat(video): 增加重复帧预览数据模型与纯分析助手"
```

---

### Task 2: 新增 `ProbeDupLightAsync`(轻量预估)与 `AnalyzeDupAsync`(全文分析)(VideoService)

**Files:**
- Modify: `D:\deep\alh-pro\ImgUpscalerUI\VideoService.cs`(在 Task 1 的类型附近加两个 `public static` 异步方法)

**Interfaces:**
- Consumes:`DupProfile`/`DupSegInfo`、`EstimateFromSads`、`BuildDupSegments`、`DetectDupFramesWithSsim`(L1835)、`DetectDupFramesAdaptive`(L1890)、`SampleGray`(L2100)、`MeanAbsDiff`(L2131)、`RunAsync`/`RunCaptureAsync`、`FfmpegPath`。
- Produces:
  - `public static async Task<DupProfile?> ProbeDupLightAsync(string ffmpeg, string videoPath, CancellationToken ct)`
  - `public static async Task<DupProfile?> AnalyzeDupAsync(string ffmpeg, string videoPath, int dedupMode, double dedupAnimeThr, int dedupSmartMode, CancellationToken ct)`
  - 外加:类内 private 字段/方法 `ExtractDownscaledGraysAsync(...)` 与 `static string DedupModeToProfileText(double ratioPct, double? contentFps)`(供 Task 5 的"一键设置"文案用)。

- [ ] **Step 1: 写入轻量预估与分析两个方法**

在 `VideoService.cs` 追加:

```csharp
/// <summary>轻量预估重复帧:每 8 帧抽 1 + 缩小到 160 宽灰度,算相邻 SAD。
/// 快(样本≤120帧),返回 (占比%, 内容帧率?)。信息不足(几乎无重复)返回 null。</summary>
public static async Task<DupProfile?> ProbeDupLightAsync(string ffmpeg, string videoPath, System.Threading.CancellationToken ct)
{
    var dir = Path.Combine(Path.GetTempPath(), $"imgup_duplight_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try
    {
        var pattern = Path.Combine(dir, "f_%04d.png");
        // select 每 8 帧抽 1 再缩小灰度; -frames:v 120 限制样本量
        await RunAsync(ffmpeg,
            $"-y -i \"{videoPath}\" -vf \"select='not(mod(n,8))',scale=160:-1:flags=area,format=gray\" " +
            $"-frames:v 120 -vsync vfr \"{pattern}\"",
            null, ct, "预估", 0);
        var files = Directory.EnumerateFiles(dir, "*.png").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length < 3) return null;
        var inFps = double.TryParse(ProbeFps(videoPath), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var pf) && pf > 0 ? pf : 30.0;
        var sads = new System.Collections.Generic.List<double>();
        var prev = SampleGray(files[0], 8, out _, out _);
        for (int i = 1; i < files.Length; i++)
        {
            var cur = SampleGray(files[i], 8, out _, out _);
            sads.Add(MeanAbsDiff(prev, cur));
            prev = cur;
        }
        var (ratioPct, contentFps) = EstimateFromSads(sads, inFps);
        if (contentFps is null) return null;
        return new DupProfile
        {
            RatioPct = ratioPct,
            ContentFps = contentFps,
            Summary = $"重复约 {ratioPct:0}% · 内容约 {contentFps:0.##}fps(预估)",
            Segs = new System.Collections.Generic.List<DupSegInfo>()
        };
    }
    catch { return null; }
    finally { try { Directory.Delete(dir, true); } catch { } }
}

/// <summary>全文分析重复帧:与处理阶段共用同一检测器(动漫/标准/敏感/手动用 Ssim;智能用 Adaptive),
/// 并给出时间轴分布 Segs。返回 null 表示分析失败/信息不足。</summary>
public static async Task<DupProfile?> AnalyzeDupAsync(string ffmpeg, string videoPath, int dedupMode,
    double dedupAnimeThr, int dedupSmartMode, System.Threading.CancellationToken ct)
{
    var dir = Path.Combine(Path.GetTempPath(), $"imgup_dupfull_{Guid.NewGuid():N}");
    var frameDir = Path.Combine(dir, "frames");
    Directory.CreateDirectory(frameDir);
    try
    {
        var pattern = Path.Combine(frameDir, "frame_%06d.png");
        // 缩小到 400 宽灰度(够分析,省 IO/内存);与处理阶段缩放口径一致(处理用 trunc(iw/2)*2,这里用等比缩小)
        await RunAsync(ffmpeg,
            $"-y -i \"{videoPath}\" -vf \"scale=400:-1:flags=area,format=gray\" -qscale:v 2 \"{pattern}\"",
            null, ct, "分析", 0);
        var inFps = double.TryParse(ProbeFps(videoPath), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var pf) && pf > 0 ? pf : 30.0;
        // 时长表(真 PTS 时间轴,用于把"删帧集合"映射成"时间分布")
        var durs = await BuildFrameDurationsAsync(ffmpeg, videoPath, "", "scale=400:-1:flags=area,format=gray", ct);
        var drop = dedupMode == 1
            ? DetectDupFramesAdaptive(frameDir, null, 16, dedupSmartMode)
            : DetectDupFramesWithSsim(frameDir,
                dedupMode == 2 ? (dedupAnimeThr switch { 0.90 => 3.5, 0.88 => 4.0, 0.85 => 4.5, _ => 3.0 }) : (dedupMode == 5 ? 4.5 : 3.0),
                dedupMode == 2 ? (dedupAnimeThr switch { 0.90 => 0.94, 0.88 => 0.93, 0.85 => 0.92, _ => 0.96 })
                                : (dedupMode == 5 ? 0.87 : 0.97),
                dedupMode switch { 5 => 0.45, 2 => 0.10, _ => 0.12 },
                6, 16, 4,
                dedupMode == 2 ? (dedupAnimeThr switch { 0.85 => 0.93, 0.88 => 0.94, 0.90 => 0.94, _ => 0.95 }) : 0.0,
                dedupMode == 5 ? 6.5 : 5.0);
        int origCount = Directory.EnumerateFiles(frameDir, "*.png").Count();
        if (durs != null && durs.Count > origCount) MergeDurations(durs, drop, origCount);
        int kept = origCount - (drop.Count > origCount ? origCount : drop.Count);
        double ratioPct = origCount > 0 ? 100.0 * (origCount - kept) / origCount : 0;
        double? contentFps = ratioPct > 0.5 ? inFps * (1.0 - ratioPct / 100.0) : inFps;
        var segs = durs != null
            ? BuildDupSegments(drop, durs, (int)System.Math.Clamp(System.Math.Round(Total(durs) * 2.0), 8, 60))
            : new System.Collections.Generic.List<DupSegInfo>();
        return new DupProfile
        {
            RatioPct = ratioPct,
            ContentFps = contentFps,
            Summary = $"重复 {ratioPct:0}% · 内容约 {contentFps:0.##}fps",
            Segs = segs
        };
    }
    catch { return null; }
    finally { try { Directory.Delete(dir, true); } catch { } }
}

/// <summary>就地求 List<double> 之和(与分析调用点配合,避免去重中间状态污染)。</summary>
private static double Total(System.Collections.Generic.List<double> d)
{
    double s = 0; foreach (var x in d) s += x; return s;
}
```

> 注:`ProbeFps` 已在 `VideoService.cs:L95` 存在;`MergeDurations`(L1760)存在。`drop` 里的帧号是相对"分析帧序列(帧号 1-based,按 frame_%06d)"的;`BuildFrameDurationsAsync` 返回的时长表与同一滤镜链对齐,故映射一致。若 `durs` 与 `origCount` 长度不一致,`MergeDurations`/`BuildDupSegments` 会按长度 clamp,不会越界。

- [ ] **Step 2: 验证编译**

Run: `dotnet build D:\deep\alh-pro\ImgUpscalerUI\ImgUpscalerUI.csproj -c Release`
Expected: 通过。

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "feat(video): 新增重复帧轻量预估与全文分析(VideoService)"
```

---

### Task 3: `VideoItem` 增加重复帧字段 + INotifyPropertyChanged(VideoView.xaml.cs)

**Files:**
- Modify: `D:\deep\alh-pro\ImgUpscalerUI\Views\VideoView.xaml.cs:20`(VideoItem 类)

**Interfaces:**
- Consumes:`DupProfile`/`DupSegInfo`。
- Produces(供 Task 4/5 使用):
  - `public double? DupRatioPct { get; set; }`(INPC)
  - `public double? ContentFps { get; set; }`(INPC)
  - `public System.Collections.Generic.List<DupSegInfo> DupSegs { get; set; } = new();`(INPC)
  - `public string DupBadgeText { get; }`(只读计算)与 `public Microsoft.UI.Xaml.Visibility DupBadgeVisibility { get; }`(只读计算)
  - `public Microsoft.UI.Xaml.Media.SolidColorBrush DupBadgeBrush { get; }`(只读计算,按严重度着色)
  - `public string DupSummary { get; set; } = "";`(INPC)
  - `public bool DupAnalyzed { get; set; }`(非 INPC;仅内部标记)
  - `public void SetDupProfile(DupProfile? p)` —— 一次性写入并把结果转成徽标/文案(集中处理,避免散落)

- [ ] **Step 1: 在 VideoItem 类里加字段与设置方法**

在 `class VideoItem`(`VideoView.xaml.cs:20`)的私有字段区加:

```csharp
private double? _dupRatioPct;
private double? _contentFps;
private System.Collections.Generic.List<DupSegInfo> _dupSegs = new();
private string _dupSummary = "";
```

在类内的公共属性区加:

```csharp
public double? DupRatioPct { get => _dupRatioPct; set { _dupRatioPct = value; OnPropertyChanged(); } }
public double? ContentFps { get => _contentFps; set { _contentFps = value; OnPropertyChanged(); OnPropertyChanged(nameof(DupBadgeText)); } }
public System.Collections.Generic.List<DupSegInfo> DupSegs { get => _dupSegs; set { _dupSegs = value; OnPropertyChanged(); } }
public string DupSummary { get => _dupSummary; set { _dupSummary = value; OnPropertyChanged(); } }

public string DupBadgeText
{
    get
    {
        if (DupRatioPct is not double r || r < 1) return "";
        return ContentFps is double cf
            ? $"{r:0}%·≈{cf:0.##}fps"
            : $"{r:0}%重复";
    }
}

public Microsoft.UI.Xaml.Visibility DupBadgeVisibility =>
    DupRatioPct is double r && r >= 1
        ? Microsoft.UI.Xaml.Visibility.Visible
        : Microsoft.UI.Xaml.Visibility.Collapsed;

public Microsoft.UI.Xaml.Media.SolidColorBrush DupBadgeBrush
{
    get
    {
        double r = DupRatioPct ?? 0;
        var c = r >= 50 ? Microsoft.UI.Colors.Red
              : r >= 25 ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 230, 138, 0)).Color
              : Microsoft.UI.Colors.Green;
        return new Microsoft.UI.Xaml.Media.SolidColorBrush(c);
    }
}

public void SetDupProfile(DupProfile? p)
{
    DupAnalyzed = true;
    if (p is null) { DupRatioPct = null; ContentFps = null; DupSegs = new(); DupSummary = "分析失败/信息不足"; return; }
    DupRatioPct = p.RatioPct;
    ContentFps = p.ContentFps;
    DupSegs = p.Segs;
    DupSummary = p.Summary;
    OnPropertyChanged(nameof(DupBadgeText));
    OnPropertyChanged(nameof(DupBadgeVisibility));
    OnPropertyChanged(nameof(DupBadgeBrush));
}
```

> 注:`OnPropertyChanged()` 无参调用依赖 `CallerMemberName`(项目里 `ImageItem` 就是这么用的);若 `VideoItem` 现有 `OnPropertyChanged` 不带默认参数,就显式 `OnPropertyChanged(nameof(...))`。请先确认 `VideoView.xaml.cs` 里 `VideoItem` 现有 `OnPropertyChanged` 签名,保持一致。

- [ ] **Step 2: 触发 OnPropertyChanged 以刷新徽标**

如果 `SetDupProfile` 里 `OnPropertyChanged(nameof(DupBadgeBrush))` 需要同时刷新依赖项,`DupBadgeBrush` 的 getter 每次新分配 brush 会轻微浪费;为稳妥,在末尾对所有涉及属性都 notify(上面已覆盖)。

- [ ] **Step 3: 验证编译**

Run: `dotnet build D:\deep\alh-pro\ImgUpscalerUI\ImgUpscalerUI.csproj -c Release`
Expected: 通过。

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat(video): VideoItem 增加重复帧字段与徽标计算"
```

---

### Task 4: 列表项模板加"重复帧"徽标(VideoView.xaml)

**Files:**
- Modify: `D:\deep\alh-pro\ImgUpscalerUI\Views\VideoView.xaml:927-932`(在"可变帧率"徽标后加一个同款式徽标)

**Interfaces:**
- Consumes:`VideoItem.DupBadgeText / DupBadgeVisibility / DupBadgeBrush`。
- Produces: 无。

- [ ] **Step 1: 在 DataTemplate 的徽标行内追加重复帧徽标**

在 `VideoView.xaml` 第 932 行(`可变帧率` 徽标的 `</Border>`)之后插入:

```xml
<Border Background="{Binding DupBadgeBrush}" CornerRadius="4" Padding="5,2"
        VerticalAlignment="Center"
        Visibility="{Binding DupBadgeVisibility, FallbackValue=Collapsed}">
    <TextBlock Text="{Binding DupBadgeText}" FontSize="10" FontWeight="SemiBold"
               Foreground="White" VerticalAlignment="Center"/>
</Border>
<｜end▁of▁thinking｜>

- [ ] **Step 2: 验证编译 + 手动确认**

Run: `dotnet build D:\deep\alh-pro\ImgUpscalerUI\ImgUpscalerUI.csproj -c Release`
手动:运行 App,加入一个有大量重复帧的视频,确认列表项徽标行出现"xx%·≈xxfps"彩色徽标(需先完成 Task 6 的入列探测才有数据;此步只确认编译与绑定不报错)。

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "feat(video): 列表项模板增加重复帧徽标"
```

---

### Task 5: 预览覆盖层加"重复帧分布时间轴 + 分析/设置按钮"(VideoView.xaml + VideoView.xaml.cs)

**Files:**
- Modify: `D:\deep\alh-pro\ImgUpscalerUI\Views\VideoView.xaml:1007-1045`(在预览覆盖层 Grid.Row=2 内加一行重复帧分析 UI)
- Modify: `D:\deep\alh-pro\ImgUpscalerUI\Views\VideoView.xaml.cs`(PreviewClose / OpenPreview / 新增事件处理)

**Interfaces:**
- Consumes:`VideoItem.DupSegs/DupSummary/DupRatioPct/ContentFps/SetDupProfile`、`VideoService.AnalyzeDupAsync`、`VideoService.ProbeDupLightAsync`。
- Produces:
  - 私有字段 `System.Collections.Generic.List<System.Threading.CancellationTokenSource> _dupCts = new();`
  - 事件处理 `private async void AnalyzeDupBtn_Click(object sender, RoutedEventArgs e)`
  - 事件处理 `private void ApplyDupBtn_Click(object sender, RoutedEventArgs e)`
  - 方法 `private void RenderDupStrip(VideoItem item)`

- [ ] **Step 1: 在预览覆盖层 Grid.Row=2 内加 UI**

在 `VideoView.xaml:1026` 的"时间信息 + 裁剪操作"**之后**、`Timeline`(1028)之前(仍位于 Grid.Row=2),插入"重复帧分析"一行:

```xml
<Grid Grid.Row="0" RowSpacing="6" Margin="0,8,0,0">
  <Grid.RowDefinitions>
    <RowDefinition Height="Auto"/>
    <RowDefinition Height="Auto"/>
  </Grid.RowDefinitions>
  <Grid.ColumnDefinitions>
    <ColumnDefinition Width="*"/>
    <ColumnDefinition Width="Auto"/>
  </Grid.ColumnDefinitions>
  <TextBlock x:Name="DupAnalysisText" FontSize="11" Opacity="0.85"
             VerticalAlignment="Center" TextTrimming="CharacterEllipsis"/>
  <StackPanel Grid.Column="1" Orientation="Horizontal" Spacing="6">
    <Button x:Name="AnalyzeDupBtn" Content="分析重复帧" FontSize="11" Padding="8,4"
            Click="AnalyzeDupBtn_Click"/>
    <Button x:Name="ApplyDupBtn" Content="据此设置去重" FontSize="11" Padding="8,4"
            Click="ApplyDupBtn_Click" IsEnabled="False"/>
  </StackPanel>
</Grid>
<Grid x:Name="DupStrip" Grid.Row="1" Height="14" Margin="0,6,0,0"
      Background="#3A4150" CornerRadius="3">
  <!-- 后置填充:RenderDupStrip 往这里加 Rectangle -->
</Grid>
```

> 注意该行位于 Grid.Row=2 内,原来 Grid.Row=2 的子元素是直接放 `Grid.Row="0"`(TrimInfo)与 `Grid.Row="1"`(Timeline)。这里新增的块请放入 Grid.Row=2 的**第 3 个子节点层级**——若内层嵌套 Grid 更清晰,可用 `<Grid Grid.Row="?">` 包住;实现时以"既保留原 Trim 行,又不破坏 Grid.Row 布局"为准,可把重复帧块单独包一层 Grid。

- [ ] **Step 2: 代码后置——OpenPreview/PreviewClose 钩子 + RenderDupStrip + 事件处理**

在 `VideoView.xaml.cs` 的 `OpenPreview`(`:1671`)内、设置 `PreviewPlayer.Source` 之后加:

```csharp
DupAnalysisText.Text = item.DupAnalyzed
    ? item.DupSummary
    : (item.DupRatioPct is double r ? $"重复约 {r:0}% · 点「分析重复帧」查看分布" : "未检测到明显重复帧");
RenderDupStrip(item);
ApplyDupBtn.IsEnabled = item.DupRatioPct is double rr && rr >= 1;
_previewItem = item;
```

在 `PreviewClose_Click`(`:1722`)里、关掉播放器之后加:

```csharp
foreach (var c in _dupCts) c.Cancel();
_dupCts.Clear();
foreach (var child in DupStrip.Children.ToArray()) DupStrip.Children.Remove(child);
_previewItem = null;
```

新增字段与方法、事件处理:

```csharp
// 预览覆盖层当前展示的项(供"分析/设置去重"还原参数用)
private VideoItem? _previewItem;
private readonly System.Collections.Generic.List<System.Threading.CancellationTokenSource> _dupCts = new();

/// <summary>把 item.DupSegs 画成 DupStrip 内的紫色/红色矩形条(宽度∝时长,颜色∝重复占比)。</summary>
private void RenderDupStrip(VideoItem item)
{
    foreach (var c in DupStrip.Children.ToArray()) DupStrip.Children.Remove(c);
    if (item.DupSegs == null || item.DupSegs.Count == 0) return;
    // 基础灰条已被 XAML Background 提供;红色段叠在上面(按比例宽度)
    var total = item.DupSegs.Sum(s => s.End - s.Start);
    if (total <= 0) return;
    foreach (var s in item.DupSegs)
    {
        if (s.Ratio < 0.08) continue;   // 占比过低不画,避免满屏噪点
        var w = Math.Max(1.0, DupStrip.ActualWidth * (s.End - s.Start) / total);
        var left = DupStrip.ActualWidth * s.Start / total;
        var rect = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Height = 8,
            Width = w,
            Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Microsoft.UI.ColorHelper.FromArgb(255,
                    (byte)Math.Min(255, 120 + (int)(s.Ratio * 135)),
                    30, 30)),
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
            Margin = new Microsoft.UI.Xaml.Thickness(0, 0, 0, 0),
        };
        // 绝对定位:放到独立画布上,左侧偏移 = left
        rect.SetValue(Canvas.LeftProperty, left);
        if (!(DupStrip is Microsoft.UI.Xaml.Controls.Canvas))
        {
            // DupStrip 是普通 Grid → 改用 Image/Canvas 定位:这里用最简单可行方案
            // 直接把 DupStrip 换成 Canvas(见 Step 3 XAML 调整),或此处用左 Margin 近似。
        }
        // 简化:用 Canvas.SetLeft 需父级为 Canvas;若 DupStrip 是 Grid,则用下面近似定位
        DupStrip.Children.Add(rect);
    }
}

private async void AnalyzeDupBtn_Click(object sender, RoutedEventArgs e)
{
    if (_previewItem is not VideoItem item || item.Path is null) return;
    var ffmpeg = VideoService.FfmpegPath;
    if (ffmpeg is null) { DupAnalysisText.Text = "未找到 ffmpeg"; return; }
    var dedupModel = DedupModelCombo.SelectedIndex;
    var cts = new System.Threading.CancellationTokenSource();
    _dupCts.Add(cts);
    AnalyzeDupBtn.IsEnabled = false;
    DupAnalysisText.Text = "分析重复帧中…";
    try
    {
        // 与处理阶段同档位:用当前 UI 选中的去重模式/强度(否则用智能)
        var animeThr = DedupAnimeCombo.SelectedIndex switch { 2 => 0.88, 3 => 0.85, 0 => 0.90, _ => 0.90 };
        var smart = DedupSmartCombo.SelectedIndex switch { 1 => 1, 2 => 2, _ => 0 };
        var profile = await VideoService.AnalyzeDupAsync(ffmpeg, item.Path,
            dedupModel == 0 ? 1 : dedupModel, animeThr, smart, cts.Token);
        item.SetDupProfile(profile);
        DupAnalysisText.Text = item.DupSummary;
        RenderDupStrip(item);
        ApplyDupBtn.IsEnabled = item.DupRatioPct is double rr && rr >= 1;
    }
    catch (Exception ex) { DupAnalysisText.Text = "分析失败:" + ex.Message; }
    finally
    {
        _dupCts.Remove(cts);
        AnalyzeDupBtn.IsEnabled = true;
    }
}

/// <summary>一键套用去重档位:根据分析结果决定 去重开/关 + 智能(内容帧率低/占比高)或 动漫(占比中)。</summary>
private void ApplyDupBtn_Click(object sender, RoutedEventArgs e)
{
    if (_previewItem is not VideoItem item) return;
    double r = item.DupRatioPct ?? 0;
    double cf = item.ContentFps ?? 0;
    DedupCheck.IsChecked = r >= 1;
    if (r < 1) return;
    // 内容帧率低或占比很高 → 智能(自适应);否则动漫(保住微动)
    if (cf > 0 && cf <= 15) DedupModelCombo.SelectedIndex = 0;      // 智能
    else DedupModelCombo.SelectedIndex = 1;                          // 动漫
    Log($"已按分析结果设置去重({DedupModelCombo.SelectedItem}):重复 {r:0}% · 内容约 {cf:0.##}fps");
    OnOptionChanged();
}
```

> 注意 `RenderDupStrip` 里 `Canvas.SetLeftValue` 需要父级是 `Canvas`,否则不生效。**建议把 Step 1 里的 `<Grid x:Name="DupStrip" ...>` 直接声明成 `<Canvas x:Name="DupStrip" ...>`**,这样 `rect.SetValue(Canvas.LeftProperty, left)` 才有效。若保持 Grid,则用 `rect.Margin = new Thickness(left, 0, 0, 0)` 近似定位(但多个子元素用 Margin 不会叠加,会重叠)。**推荐用 Canvas。**

- [ ] **Step 3: 验证编译 + 手动确认**

Run: `dotnet build D:\deep\alh-pro\ImgUpscalerUI\ImgUpscalerUI.csproj -c Release`
手动:预览一个视频 → 出现"重复帧分析"行与灰条;点「分析重复帧」→ 条上出现红色段;再点「据此设置去重」→ 去重开关与档位被自动设置。

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat(video): 预览覆盖层增加重复帧时间轴与分析/一键设置去重"
```

---

### Task 6: 入列后台轻量预估 + OpenPreview 接线(VideoView.xaml.cs)

**Files:**
- Modify: `D:\deep\alh-pro\ImgUpscalerUI\Views\VideoView.xaml.cs:1224-1226`(在加入列表后引预估)
- Modify: `D:\deep\alh-pro\ImgUpscalerUI\Views\VideoView.xaml.cs:1239-1257`(新增 `ProbeDupAsync` 方法)

**Interfaces:**
- Consumes:`VideoService.ProbeDupLightAsync`、`VideoItem.SetDupProfile`。
- Produces: 无(纯接线)。

- [ ] **Step 1: 加入列表后触发轻量预估**

在 `VideoView.xaml.cs:1226`(`_ = GenerateThumbAsync(item);`)之后加:

```csharp
_ = ProbeDupAsync(item);   // 重复帧轻量预估(后台,不阻塞加入)
```

- [ ] **Step 2: 新增 ProbeDupAsync 方法**

在 `ProbeVfrAsync`(`:1247`)附近新增:

```csharp
/// <summary>入列后台轻量预估重复帧:估算占比+内容帧率,写进徽标;不动全量分析(选中时再详细)。</summary>
private async Task ProbeDupAsync(VideoItem item)
{
    if (item.Path is null) return;
    try
    {
        var ffmpeg = VideoService.FfmpegPath;
        if (ffmpeg is null) return;
        var profile = await VideoService.ProbeDupLightAsync(ffmpeg, item.Path, System.Threading.CancellationToken.None);
        if (profile is null) return;             // 几乎无重复/失败:不显示徽标
        // 只更新徽标信息,不覆盖 Segs(轻量预估无时间轴)
        item.DupRatioPct = profile.RatioPct;    // 触发 DupBadgeVisibility/Brush 刷新
        item.ContentFps = profile.ContentFps;
        item.DupSummary = profile.Summary;
        item.SetDupProfile(profile);             // 统一入口;SetDupProfile 会 SetDupAnalyzed=true
    }
    catch { /* 预估失败静默,不影响加入 */ }
}
```

> 注:`SetDupProfile` 会把 `DupProfiled` 当作已分析、并设置 `Segs=[]`。若希望"轻量预估≠已分析"(选中时才允许详细分析),把 `ProbeDupAsync` 里改为只设 `DupRatioPct/ContentFps/DupSummary`,**不**调 `SetDupProfile`,避免误标"已分析"。(二选一:本计划用 `SetDupProfile` 保持简单;若要区分,见 Task 5 `OpenPreview` 里 `DupAnalyzed` 判断逻辑。)**建议区分**:用 `ProbeDupAsync` 只设字段,`DupAnalyzed` 仍为 false,这样"点分析"才有意义。)

- [ ] **Step 3: 验证编译 + 手动确认**

Run: `dotnet build D:\deep\alh-pro\ImgUpscalerUI\ImgUpscalerUI.csproj -c Release`
手动:加入一个重复率高的视频 → 列表项徽标自动出现"xx%·≈xxfps";选中预览 → 右侧显示灰条,点「分析重复帧」出现红色分布。

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat(video): 入列后台轻量重复帧预估并接线预览"
```

---

## 自检(Spec Coverage)

- 入列后台轻量预估 → Task 6 + Task 2(`ProbeDupLightAsync`)。
- 选中时全文分析 → Task 5 + Task 2(`AnalyzeDupAsync`)。
- 徽标(占比%+内容帧率)→ Task 3(`DupBadgeText`) + Task 4(XAML)。
- 预览区时间轴重复分布条 → Task 5(`RenderDupStrip` + `DupStrip`)。
- 一键套用去重档位 → Task 5(`ApplyDupBtn_Click`)。
- 预览/处理共用同一检测逻辑 → Task 2 复用 `DetectDupFrames*`,口径一致。

## 已知取舍/风险(写给执行者)

1. **`DupStrip` 用 `Canvas` 才能 `Canvas.SetLeft` 定位**;若保持 Grid,Margin 定位不叠加。计划推荐 Canvas。
2. **`Analysis` 分辨率/检测参数**:分析用 400 宽灰度 + 与处理阶段相同的 SSIM/SAD 阈值。处理阶段在**全分辨率**上检测,分析在缩小帧上,个别素材阈值体会略有出入 → 徽标/时间轴是"预览参考",处理的 `LastDedupReport` 仍以实际为准。可在文案叠加"(预估)"降低误解。
3. **`VideoItem.OnPropertyChanged` 签名**:需确认现有签名是否支持无参 `CallerMemberName`;否则显式传属性名。
4. **`OpenPreview` 中 `_previewItem`**:`_previewItem` 字段在 `VideoView.xaml.cs` 已有(`:1669`),若已存在同名,复用即可,勿重复声明。
5. **预估耗时**:`ProbeDupLightAsync` 用 `select` + `-frames:v 120` 限样本,长视频也快;若某些机器解码慢,可在 `ProbeDupAsync` 里捕获超时静默。
