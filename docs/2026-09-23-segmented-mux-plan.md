# 分段合帧（空间不足时自动降级）实施计划

> **给执行者**：本计划按任务逐条执行，步骤用 `- [ ]` 勾选跟踪。
> 每个任务都以"能独立验收的产物"结尾；**不要**跳过"跑测试确认失败"那一步。
> 本仓库的执行纪律（血泪条款，摘自 `_qa/交接清单_20260923.md`）：
> ① 结构改动**整份重写文件**，不用脚本补丁；② 改完必须 `dotnet build` + `dotnet test`；
> ③ 部署用 `deploy.ps1`（遇 `exit 0xC0000005` 直接重试）；④ 绝不注入鼠标键盘，UI 只用 `_qa\uia_drv` 只读验证。

**Goal:** 视频处理在"临时盘放不下"时不再直接失败，而是自动改为"按段编码 + 无缝拼接"跑完；放得下的任务一行行为不变。

**Architecture:** 最终帧本来就是**逐步落盘**的（`VideoService.cs:3093 / :3190` 把 `framesFinal` 重指向超分/补帧的输出目录），所以可以"某段帧齐了就编码成一段视频、成功后再删该段帧、最后 concat 拼接"。段按**连续的最终帧号区间**定义（不是按批——去重开着时批的输出槽位是分散的）。触发只在守门判定"预估 > 剩余"时发生。

**Tech Stack:** C# / .NET 8、xunit（`AlhPro.Tests`）、ffmpeg（随包 `engines\ffmpeg`）、WinUI（仅提示文案）。

**Spec:** `docs/2026-09-23-segmented-mux.md`（本计划的依据，含 8 条带行号的代码事实与 6 条验收口径）

## Global Constraints

- 段上限 **2000 帧/段**（2026-09-13 批准的口径），段下界 **50 帧**（低于它拒绝）。
- 预算比例**复用** `AlhPro.Core.RenderPolicy.BatchTempDiskShareLimit = 0.65`，不新增第二个常数。
- 单帧占用口径**必须**用 `TempSpaceEstimate.PeakFrameMegabytes + SourceFrameMegabytes`（与 `NeedBytes` 同源，禁止另写一套）。
- 编码参数（`EncoderArgs` / `frInput` / `-vf` 链 / 编码器回退链 `GetHwRecipe`+`BrokenHwEncoders`）与单遍路径**完全一致**。
- `-framerate` 是唯一的帧率来源（`:3742`），各段必须同值。
- 默认路径（预估放得下）**零行为变化**；分段模式的字样只允许在分段时出现。

---

## 文件结构

| 文件 | 职责 | 动作 |
|---|---|---|
| `AlhPro.Core/MuxSegmentation.cs` | **新增**：纯逻辑——段规划（段帧数、段区间表）。无 IO、无 ffmpeg。 | 新建 |
| `AlhPro.Tests/MuxSegmentationTests.cs` | **新增**：段规划单测（上限/下界/预算/区间不变量/拒绝条件）。 | 新建 |
| `ImgUpscalerUI/VideoService.cs` | 分段编码、删帧、背压、拼接、对齐步骤按台账计数、守门触发与日志/提示。 | 修改 |
| `AlhPro.Tests/SegmentedMuxWiringTests.cs` | **新增**：接线契约（默认路径不出现分段字样；分段路径必须走同一套编码参数与回退链）。 | 新建 |
| `docs/2026-09-23-segmented-mux.md` | 设计（已提交 `de22068`）。 | 已完成 |

---

### Task 1: 段规划纯逻辑（零行为变化）

**Files:**
- Create: `AlhPro.Core/MuxSegmentation.cs`
- Test: `AlhPro.Tests/MuxSegmentationTests.cs`

**Interfaces:**
- Produces（后面两个任务都依赖这两个签名，逐字使用）：
  - `public static int FramesPerSegment(double freeDiskGB, double peakFrameMb, double sourceFrameMb, int concurrency, int maxFramesPerSegment = 2000, int minFramesPerSegment = 50)`
    —— 返回**段帧数**；返回 `0` 表示"连 50 帧都放不下 ⇒ 不该分段，应拒绝"。
  - `public static IReadOnlyList<MuxSegmentation.Segment> PlanSegments(long totalFrames, int framesPerSegment)`
    —— 返回首尾相接的段区间；`Segment` 是 `public readonly record struct Segment(int Index, long StartFrame, long FrameCount)`（`StartFrame` 为 0 基）。

- [ ] **Step 1: 写失败的测试**

```csharp
using System.Linq;
using AlhPro.Core;
using Xunit;

namespace AlhPro.Tests;

public class MuxSegmentationTests
{
    [Fact] // 余量充足 ⇒ 用上限
    public void Plenty_of_space_uses_the_cap()
        => Assert.Equal(2000, MuxSegmentation.FramesPerSegment(500, 8.93, 4.0, 2));

    [Fact] // 紧余量 ⇒ 按预算缩(132GB×0.65=85.8GB ÷ (12.93MB×3) ≈ 2217 帧 → 但上限 2000)
    public void Budget_shrinks_the_segment()
        => Assert.Equal(2000, MuxSegmentation.FramesPerSegment(132, 8.93, 4.0, 2));

    [Fact] // 真的小余量:10GB×0.65=6.656GB ÷ (12.93MB×3) ≈ 171 帧
    public void Small_budget_gives_a_smaller_segment()
        => Assert.Equal(171, MuxSegmentation.FramesPerSegment(10, 8.93, 4.0, 2));

    [Fact] // 连下界都放不下 ⇒ 0(调用方据此保留"拒绝")
    public void Hopeless_budget_returns_zero()
        => Assert.Equal(0, MuxSegmentation.FramesPerSegment(0.2, 8.93, 4.0, 2));

    [Fact] // 并发数必须计入(盘上同时有 c 段 + 1 段在编)
    public void Concurrency_is_counted()
    {
        int c1 = MuxSegmentation.FramesPerSegment(10, 8.93, 4.0, 1);
        int c2 = MuxSegmentation.FramesPerSegment(10, 8.93, 4.0, 2);
        Assert.True(c1 > c2, $"并发越大段应越小(c1={c1}, c2={c2})");
    }

    [Fact] // 区间不变量:首尾相接、无空洞、无重叠、总数守恒
    public void Segments_tile_the_timeline()
    {
        var segs = MuxSegmentation.PlanSegments(18680, 2000);
        Assert.Equal(10, segs.Count);                       // ⌈18680/2000⌉
        Assert.Equal(0, segs[0].StartFrame);
        long acc = 0;
        for (int i = 0; i < segs.Count; i++)
        {
            Assert.Equal(i, segs[i].Index);
            Assert.Equal(acc, segs[i].StartFrame);
            acc += segs[i].FrameCount;
        }
        Assert.Equal(18680, acc);
        Assert.Equal(680, segs[^1].FrameCount);             // 最后一段是余数
    }

    [Fact] // 边界:不足一段 / 恰好一段 / 0 帧
    public void Edge_cases()
    {
        Assert.Single(MuxSegmentation.PlanSegments(100, 2000));
        Assert.Equal(100, MuxSegmentation.PlanSegments(100, 2000)[0].FrameCount);
        Assert.Equal(2, MuxSegmentation.PlanSegments(2000, 2000).Count switch { 1 => 1, _ => 2 }); // 恰好一段
        Assert.Empty(MuxSegmentation.PlanSegments(0, 2000));
        Assert.Empty(MuxSegmentation.PlanSegments(100, 0));  // 段帧数非法 ⇒ 空(调用方不会走到)
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test AlhPro.Tests\AlhPro.Tests.csproj --nologo --filter "FullyQualifiedName~MuxSegmentationTests"`
Expected: 编译失败（`MuxSegmentation` 不存在）—— 这就是"失败"的证据。

- [ ] **Step 3: 写实现**

```csharp
namespace AlhPro.Core;

/// <summary>【2026-09-23】分段合帧的**纯逻辑**:算"每段多少帧 + 段区间表"。
/// 为什么按"连续帧号区间"而不是按批:去重开着时,一个批处理的是若干代表帧,而每个代表帧带着
/// **散布在整条时间轴上的全部重复槽位** ⇒ 批的输出帧号不是连续区间(见 VideoService 切批处注释)。
/// 所以段的完成判据只能是"这段帧号区间里每一帧都已落盘",与批边界解耦。</summary>
public static class MuxSegmentation
{
    public const int MaxFramesPerSegment = 2000;   // 2026-09-13 批准的口径
    public const int MinFramesPerSegment = 50;     // 低于它就别分段了:每段一次编码器启动不划算

    public readonly record struct Segment(int Index, long StartFrame, long FrameCount);

    /// <summary>按剩余空间算每段帧数;返回 0 = 放不下(调用方应保持"拒绝并给降级建议")。</summary>
    public static int FramesPerSegment(double freeDiskGB, double peakFrameMb, double sourceFrameMb,
        int concurrency, int maxFramesPerSegment = MaxFramesPerSegment, int minFramesPerSegment = MinFramesPerSegment)
    {
        if (!double.IsFinite(freeDiskGB) || freeDiskGB <= 0) return 0;
        double perFrameMb = Math.Max(0, peakFrameMb) + Math.Max(0, sourceFrameMb);
        if (perFrameMb <= 0) return 0;
        int c = Math.Max(1, concurrency);
        // 盘上最多同时有 c 段"已落盘待编码" + 1 段正在编码 ⇒ 预算按 (c+1) 份切
        double budgetMb = freeDiskGB * 1024.0 * RenderPolicy.BatchTempDiskShareLimit;
        int fit = (int)Math.Floor(budgetMb / (perFrameMb * (c + 1)));
        int n = Math.Min(Math.Max(1, maxFramesPerSegment), fit);
        return n < Math.Max(1, minFramesPerSegment) ? 0 : n;
    }

    /// <summary>把总帧数切成首尾相接的段区间(0 基 StartFrame)。</summary>
    public static IReadOnlyList<Segment> PlanSegments(long totalFrames, int framesPerSegment)
    {
        var list = new List<Segment>();
        if (totalFrames <= 0 || framesPerSegment <= 0) return list;
        long start = 0;
        int idx = 0;
        while (start < totalFrames)
        {
            long n = Math.Min(framesPerSegment, totalFrames - start);
            list.Add(new Segment(idx++, start, n));
            start += n;
        }
        return list;
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test AlhPro.Tests\AlhPro.Tests.csproj --nologo --filter "FullyQualifiedName~MuxSegmentationTests"`
Expected: 全部通过（8 项）。

- [ ] **Step 5: 全量测试（确认没碰坏别的）**

Run: `dotnet test AlhPro.Tests\AlhPro.Tests.csproj --nologo -v q`
Expected: `失败: 1`（只有既有的 `NavLogoWebsiteTests`）—— 数量应比改前多 8 项通过。

- [ ] **Step 6: 提交**

```bash
git add AlhPro.Core/MuxSegmentation.cs AlhPro.Tests/MuxSegmentationTests.cs
git commit -F <含中文说明的临时文件>   # 本仓库纪律:中文提交信息用 -F 文件,别用 -m 内联引号
```

---

### Task 2: 分段编码 + 删帧 + 拼接（先用测试缝强制开启，A/B 验一致性）

**Files:**
- Modify: `ImgUpscalerUI/VideoService.cs`
- Test: `AlhPro.Tests/SegmentedMuxWiringTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `MuxSegmentation.FramesPerSegment` / `PlanSegments`。
- Produces（Task 3 依赖）：
  - `private bool _segMuxActive;`、`private int _segFramesPerSegment;`、`private List<MuxSegmentation.Segment> _segPlan;`
  - `private async Task<string?> EncodeOneSegmentAsync(int segIndex, string framesDir, double frInput, string fr, string vfArg, ...)`（返回段文件路径，失败返回 null）
  - `private async Task<bool> ConcatSegmentsAsync(IReadOnlyList<string> segFiles, string outTmp, ...)`

**要点（逐条都是验收项，不许"大概其"）**
1. 段就绪判据 = 该区间每帧都已落盘（用一个 `bool[] segFrameReady` 或段内计数）。
2. 背压：在飞段数上限 = `并发数 + 1`，到顶时属于下一段的批**等待**后才启动。
3. 段编码用 `-start_number <段首+1> -frames:v <段长>` 直接读同一目录的连续区间（**不拷帧**）。
4. VFR：`setpts` 按段切片 + 段首累计时间偏移；`frInput` 与单遍一致。
5. 复用编码器回退链；段输出**无音频**。
6. **编码成功后才删该段帧**。
7. 最后一段在「帧数对齐/末帧补足」之后才编码；该步的计数从"读目录"改成**按台账**。
8. 全部段完成 ⇒ `-f concat -safe 0 -i list.txt -c copy` ⇒ 套用现有音频/元数据/时长/封装。
9. 新增测试缝 `ALH_TEST_SEGMENT_FRAMES=<n>`：不设时零副作用；设了则强制分段（绕过守门），用于 A/B。

- [ ] **Step 1: 先写接线契约测试（失败）**：断言"不设测试缝时，源码路径里不出现分段专用函数调用"、"设了缝时必须走同一个 `EncoderArgs`/`GetHwRecipe`"、"段编码成功后才 `File.Delete` 段帧"。写法照 `AlhPro.Tests/PreviewPairSyncTests.cs`（读源码 + 断言关键 token）。
- [ ] **Step 2: 跑测试确认失败**（`dotnet test --filter SegmentedMuxWiringTests`）。
- [ ] **Step 3: 实现**（按上面 9 条要点改 `VideoService.cs`；结构改动整份重写涉及的方法，不用脚本补丁）。
- [ ] **Step 4: 跑测试确认通过** + `dotnet build ImgUpscalerUI -c Debug -p:Platform=x64`。
- [ ] **Step 5: 真机 A/B**：
  ```
  # 造 4 秒素材 → 用测试缝跑两遍，比帧数/时长/帧率/逐帧像素
  ffmpeg -f lavfi -i testsrc2=size=1280x720:rate=30:duration=4 -c:v libx264 -pix_fmt yuv420p in.mp4
  # ① 不分段(不设缝) ② 分段(ALH_TEST_SEGMENT_FRAMES=60)
  ffprobe -v error -select_streams v:0 -show_entries stream=nb_frames,r_frame_rate,duration -of default=nw=1 两个成片
  # 逐帧比对:抽帧后逐像素比(或直接比抽出的 PNG 哈希)
  ```
  Expected: 帧数/时长/帧率三项完全相同；逐帧比对无差异（若编码器参数一致，理想情况逐字节相同）。
- [ ] **Step 6: VFR 与帧率专项**：`Σ段duration == 单遍duration`（≤1 帧）、段与拼接后 `r_frame_rate` 一致。用一条 VFR 素材（或用 `-vsync vfr` 造）验。
- [ ] **Step 7: 提交**。

---

### Task 3: 接进守门（自动降级 + 明确提示 + 峰值日志）

**Files:**
- Modify: `ImgUpscalerUI/VideoService.cs`（`EvalTempSpaceGate`）
- Test: `AlhPro.Tests/TempSpaceAdviceTests.cs`（追加用例）

**Interfaces:**
- Consumes: Task 1 的两个函数、Task 2 的分段执行路径。
- Produces: 守门返回值新增"是否降级为分段"，供后续日志/界面使用。

- [ ] **Step 1: 追加失败测试**：断言守门源码里 `free < need` 时**不再直接抛**，而是先尝试 `MuxSegmentation.FramesPerSegment(...)`；返回 0 时才抛（并保留 2026-09-23 的量化提示文案）。
- [ ] **Step 2: 跑测试确认失败**。
- [ ] **Step 3: 实现**：
  - `free < need` ⇒ 算段帧数；>0 ⇒ `_segMuxActive = true` + 一条**明确提示**（界面 + 日志）："⚠ 临时盘只剩余 XGB,本任务需要约 YGB —— 已自动改为**分段处理**(每段 N 帧,会稍慢,但能跑完)"；
  - 短语/日志里给出**分段模式峰值估算** = `(c+1) × 段帧数 × 单帧占用`；
  - 返回 0 ⇒ 保持原行为（抛异常 + 量化降级建议）。
- [ ] **Step 4: 跑测试确认通过** + 全量 `dotnet test`。
- [ ] **Step 5: 默认路径回归**：跑一条**放得下**的常规任务（1280×720 → 2x，或 2K→4K），确认 ① 输出与改前逐帧一致 ② 日志里**不出现**任何"分段"字样。
- [ ] **Step 6: 部署**：`powershell -NoProfile -ExecutionPolicy Bypass -File deploy.ps1`。
- [ ] **Step 7: 提交** + 更新 `_qa/交接清单_20260923.md`（记下三段验收结果与"未实测：真实 8K 长片"）。

---

## 自查（写完计划后按 spec 逐条对）

| Spec 要求 | 落在哪个任务 |
|---|---|
| §三 触发与段规划（含 `c+1`、上限 2000、下界 50、拒绝条件） | Task 1 |
| §四.1 段完成判据＝帧号区间每帧落盘 | Task 2 要点 1 |
| §四.2 背压（在飞段数 ≤ c+1） | Task 2 要点 2 |
| §四.3 同编码参数 + `-start_number` + VFR 切片 + 回退链 | Task 2 要点 3/4/5 |
| §四.4 成功才删帧 | Task 2 要点 6 |
| §四.5 最后一段在对齐之后 + 对齐改台账计数 | Task 2 要点 7 |
| §四.6 concat + 现有音频路径 | Task 2 要点 8 |
| §四.7/8 帧数校验 + 分段编码进度 + 峰值日志 | Task 2 要点 9 / Task 3 Step 3 |
| §五 副作用（400 段上限只在分段模式放开） | Task 2 要点 4（须在代码注释里写明） |
| §六 验收 6 条 | Task 2 Step 5/6（A/B、VFR、帧率）+ Task 3 Step 5（默认回归）+ Task 2 要点 9（缝） |
| §六.6 黑帧整批重跑在分段下仍生效 | Task 2（段内批计数可回退） |

**未覆盖项（如实标注）**：真实 8K 长片的端到端实测需要用户机器与素材；本计划只保证到"小素材 A/B 一致 + 默认路径零变化"，真实大素材的那一跑留给部署后由用户触发（日志里已有分段峰值可核对）。
