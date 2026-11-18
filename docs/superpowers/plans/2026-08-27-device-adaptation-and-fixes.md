# ALH Pro 设备适配 + 正确性修复《改写方案》

> **For agentic workers:** 建议用 subagent-driven-development 或 executing-plans 逐任务执行。步骤用 `- [ ]` 复选框跟踪。
> 本项目**没有单元测试工程**，统一验证 = `dotnet build -c Release` 编译通过 + 手动跑 App 确认。纯函数写成 `internal static`，便于将来补测。

**Goal:** 把"设备适配"做成「引导(兼容模式改名+弱设备提示) → 画像(硬件分级预设) → 降级(无GPU/极弱仍可用)」三层，并修复之前发现的补帧/去重/暂停的正确性 bug，让差设备"从能用的边缘变成能稳定用完"。

**Architecture:** 复用现有 `SafeRender`(安全墙)/`GpuInfo`/`VulkanCheck`/引擎回退。新增一个「硬件画像 `DeviceProfile`」集中推导全部分档参数；把 `fastMode` 文案改为「兼容模式」并加"弱设备黄字提示+一键开启"；补帧 C 方案从"逐段 2 帧喂 RIFE"改回"整段一次 RIFE + setpts 重定时"。

**Tech Stack:** C# / WinUI 3 / .NET 8，项目 `ALHPro.ImgUpscalerUI`，文件在 `D:\deep\alh-pro\ImgUpscalerUI\`。

**Covered（合并前几轮全部问题）：** ①方案C补帧无效；②暂停=冻结失效；③智能去重内存爆炸；④预览≠处理；⑤setpts上限超命令行；⑥SampleGray脆弱；⑦Otsu死代码+注释说谎；⑧off-by-one；⑨ProbeFps null；⑩ETA不准；⑪弱设备降级路径缺失。

## Global Constraints

- 命名空间 `ALHPro.ImgUpscalerUI`；延续现有风格（可用 `using System.Collections.Generic;` 或完全限定）。
- 不改命名空间/不重构大文件；新逻辑放 `SafeRender.cs`(画像/弱设备判定)、`VideoService.cs`(补帧修复)、`Views\VideoView.xaml(.cs)`(UI)。
- `fastMode` 参数名可保留（内部逻辑不变），只改 UI 文案为「兼容模式」——最小改动、不破坏现有调用。
- 低配降级不得改变"默认不降低画质"的用户预期：所有自动降级只在**无GPU/极弱**时生效，且给可关闭/一次性确认。

---

# Part A　引导层：兼容模式改名 + 弱设备黄字提示

### Task A1：`SafeRender` 增加"弱设备判定 + 原因文案"

**Files:** Modify `D:\deep\alh-pro\ImgUpscalerUI\SafeRender.cs`（SafeRender 静态类内，`GetVideoConcurrency` 之后）

**Interfaces:**
- Consumes：`TotalVramGB`/`FreeVramGB`/`TotalRamGB`/`EffectiveCpuLevel`、`VulkanCheck.GpuAvailable`、`GpuInfo.GetAdapterNames()`。
- Produces：
  - `public static bool IsWeakDevice { get; }`（真弱才 true → 显示黄字）
  - `public static string WeakDeviceReason { get; }`（如 "无GPU / 显存仅4GB / 内存8GB"）
  - `public static void ComputeDeviceProfile()`（Part B 用，顺手在此定义并缓存）

```csharp
// SafeRender.cs 新增
public static bool IsWeakDevice => _weak ??= ComputeWeakDevice();
public static string WeakDeviceReason => _weakReason ??= ComputeWeakReason();
private static bool? _weak;
private static string? _weakReason;

private static bool ComputeWeakDevice()
{
    try
    {
        bool noGpu = !ALHPro.VulkanCheck.GpuAvailable;
        bool smallVram = TotalVramGB < 6;
        bool smallRam = TotalRamGB < 8;
        bool fewCores = CpuCoreCount <= 4;
        return noGpu || smallVram || smallRam || fewCores;
    }
    catch { return false; }
}
private static string ComputeWeakReason()
{
    var list = new System.Collections.Generic.List<string>();
    try
    {
        if (!ALHPro.VulkanCheck.GpuAvailable) list.Add("未检测到可用 GPU(Vulkan)");
        if (TotalVramGB < 6) list.Add($"显存仅 {TotalVramGB:0.#}GB");
        if (TotalRamGB < 8) list.Add($"内存 {TotalRamGB:0.#}GB");
        if (CpuCoreCount <= 4) list.Add("核心数较少");
    }
    catch { }
    return string.Join("、", list);
}
```

- [ ] **Step 1**：写入以上代码。确认 `VulkanCheck.GpuAvailable` 已存在（`Views\MainPage.xaml.cs:342` 用过 `VulkanCheck.GpuAvailable`）。
- [ ] **Step 2**：`dotnet build -c Release` 通过。
- [ ] **Step 3**：`git add -A; git commit -m "feat(render): 弱设备判定与原因文案(A1)"`

---

### Task A2：`VideoView.xaml` 改文案 + 加"黄色弱设备提示 + 一键开启"

**Files:** Modify `D:\deep\alh-pro\ImgUpscalerUI\Views\VideoView.xaml:810-823`

**Interfaces:** Consumes `VideoItem`/`RunBtn`/`RunHint`；Produce（供 A3 绑定的命名控件）：
- `TextBlock x:Name="CompatHint"`（黄字，`Visibility` 由代码控制）
- `Button x:Name="EnableCompatBtn"`（一键开启，Click=`EnableCompatBtn_Click`）

- [ ] **Step 1**：把 L810 开关文案与 ToolTip 改为「兼容模式」：

```xml
<CheckBox x:Name="FastModeCheck" Content="兼容模式（适合配置较低的设备）"
          ToolTipService.ToolTip="勾选后自动降低显存占用、防爆显存/卡死,画质与功能基本不受影响;配置好的设备可不开"
          Checked="Options_Changed" Unchecked="Options_Changed"/>
<!-- L812 的 FastModeHint 文案保留或微调 -->
```

- [ ] **Step 2**：在 `RunBtn`(L817) 之后、`RunHint`(L821) 之前加"弱设备黄字 + 一键开启"行：

```xml
<TextBlock x:Name="RunHint" Text="⚠ 至少启用一项处理(超分或补帧)" ... Visibility="Collapsed"/>
<!-- 新增:弱设备提示(黄色) -->
<StackPanel x:Name="CompatHintPanel" Orientation="Horizontal" Visibility="Collapsed" Spacing="6">
    <TextBlock x:Name="CompatHint" Foreground="#E8A33D" FontSize="11" TextWrapping="Wrap" TextAlignment="Center"/>
    <Button x:Name="EnableCompatBtn" Content="帮我开启" FontSize="11" Padding="8,3" Click="EnableCompatBtn_Click"/>
</StackPanel>
```

- [ ] **Step 3**：`dotnet build -c Release` 通过。

- [ ] **Step 4**：`git add -A; git commit -m "feat(video): 兼容模式文案与弱设备黄字提示位(A2)"`

---

### Task A3：`VideoView.xaml.cs` 接线黄字显示 + 一键开启

**Files:** Modify `D:\deep\alh-pro\ImgUpscalerUI\Views\VideoView.xaml.cs`

**Interfaces:** Consumes `SafeRender.IsWeakDevice/WeakDeviceReason`、`FastModeCheck`；Produce：`private void EnableCompatBtn_Click(...)`、在 `UpdateRunState()` 里刷新 `CompatHintPanel`。

- [ ] **Step 1**：在 `UpdateRunState()`（`Views\VideoView.xaml.cs:354` 附近，已处理 `RunHint`）里加：

```csharp
// 弱设备 + 未开兼容模式 → 显示黄字提示(建议开启);否则隐藏
if (SafeRender.IsWeakDevice && FastModeCheck.IsChecked != true && CompatHintPanel != null)
{
    CompatHintPanel.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
    CompatHint.Text = $"⚠ 检测到设备配置较低({SafeRender.WeakDeviceReason}),建议勾选「兼容模式」防止爆显存/卡顿。";
}
else if (CompatHintPanel != null)
    CompatHintPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
```

- [ ] **Step 2**：新增一键开启处理器 + 切换后刷新：

```csharp
private void EnableCompatBtn_Click(object sender, RoutedEventArgs e)
{
    FastModeCheck.IsChecked = true;      // 触发 Options_Changed → UpdateRunState 刷新,黄字自动消失
    Log("已为你开启「兼容模式」(弱设备保护)");
}
```

- [ ] **Step 3**：确保开关切换/`OnOptionChanged` 都会重新跑 `UpdateRunState`（现有事件已连 `Options_Changed`）。
- [ ] **Step 4**：`dotnet build -c Release` 通过；手动：模拟弱机器(或临时把 `IsWeakDevice` 强制 true)看黄字出现、点「帮我开启」黄字消失。
- [ ] **Step 5**：`git add -A; git commit -m "feat(video): 弱设备黄字提示与一键开启兼容模式(A3)"`

---

# Part B　画像层：硬件画像 + 分级预设

### Task B1：`SafeRender` 增加 `DeviceProfile` 枚举与推导，并让分档参数用它

**Files:** Modify `D:\deep\alh-pro\ImgUpscalerUI\SafeRender.cs`

**Interfaces:** Produces：
- `public enum DeviceProfile { UltraLow, Low, Balanced, High }`
- `public static DeviceProfile Profile { get; }`（按硬件推导，缓存）
- 把 `GetTileSize`/`GetVideoBatchSize`/`GetVideoConcurrency`/`GetEngineThreadArgs` 改为按 `Profile` 优先、保留回退。

```csharp
public enum DeviceProfile { UltraLow, Low, Balanced, High }
public static DeviceProfile Profile => _profile ??= ComputeProfile();
private static DeviceProfile? _profile;

private static DeviceProfile ComputeProfile()
{
    double v = TotalVramGB, r = TotalRamGB; int c = CpuCoreCount;
    bool gpu = true; try { gpu = ALHPro.VulkanCheck.GpuAvailable; } catch { }
    if (!gpu) return DeviceProfile.UltraLow;
    if (v >= 12 && r >= 32 && c >= 16) return DeviceProfile.High;
    if (v >= 6 && r >= 16 && c >= 8) return DeviceProfile.Balanced;
    if (v >= 3 && r >= 8) return DeviceProfile.Low;
    return DeviceProfile.UltraLow;
}
```

- [ ] **Step 1**：写入 `Profile`/`ComputeProfile`。
- [ ] **Step 2**：把 `GetTileSize()`(L172) 改为按档位（其余保留给老逻辑）：
  在 `GetTileSize` 开头 `switch (Profile) { case High: v=8; ... }` 或直接保留现有 VRAM 桶（画像与 VRAM 桶结果基本一致），只在 `IsWeakDevice` 场景使用 `Profile` 驱动**分辨率上限/降级**（Part C）。此处以"新增画像并让并发能感知 GPU 能力"为主。
- [ ] **Step 3**：`GetVideoConcurrency`(L198) 增加"GPU 算力节流"：High 才允许 3 路，Balanced 最多 2 路，Low/UltraLow 恒 1 路。

```csharp
// GetVideoConcurrency 顶部
if (Profile is DeviceProfile.UltraLow or DeviceProfile.Low) return 1;
// 原有 two/three 判断之前,High 才 three:
bool three = Profile == DeviceProfile.High && r >= 24 && v >= 10 && FreeVramGB >= 8 && cores >= 16;
```

- [ ] **Step 4**：`dotnet build -c Release` 通过。
- [ ] **Step 5**：`git add -A; git commit -m "feat(render): 硬件画像分级并让分档参数感知(B1)"`

---

# Part C　降级层：无GPU/极弱仍能"跑得动"

### Task C1：无GPU时自动降"工作分辨率上限" + 建议小片段

**Files:** Modify `D:\deep\alh-pro\ImgUpscalerUI\Views\VideoView.xaml.cs`；`VideoService.cs` 增加"分辨率上限"参数。

- [ ] **Step 1**：`VideoService.ProcessVideoAsync` 增加可选 `int? maxWorkHeight`；在"4.5 自定义分辨率"段前，若 `maxWorkHeight>0` 且处理帧高 > 上限，则先按要求等比缩到上限高度（弱设备用）。

```csharp
// 在补帧/超分之后、合帧之前(参考 L792-813 那一块),添加:
if (maxWorkHeight is > 0 && !(outHeight is > 0))
{
    var af = Directory.EnumerateFiles(framesFinal, "*.png").FirstOrDefault();
    if (af != null)
    {
        double h0 = new System.Drawing.Bitmap(af).Height;
        if (h0 > maxWorkHeight.Value)
        {
            double k = maxWorkHeight.Value / h0;
            ResizeAllPng(framesFinal, k);   // 新增纯函数:批量等比缩放
        }
    }
}
```

- [ ] **Step 2**：`VideoView.xaml.cs` 调用 `ProcessVideoAsync` 处（L2454 附近）加：`maxWorkHeight: SafeRender.IsWeakDevice ? 1080 : null`（无GPU更狠可到 960）。

- [ ] **Step 3**：`dotnet build -c Release`；手动：弱机处理高分辨率，输出被自动限制到上限。

- [ ] **Step 4**：`git add -A; git commit -m "feat(video): 无GPU自动限制工作分辨率(C1)"`

### Task C2：开始前"性能预算"提示(弱设备)

**Files:** Modify `D:\deep\alh-pro\ImgUpscalerUI\VideoService.cs`(`EstimateProcessSeconds` L71)与 `Views\VideoView.xaml.cs`(`RunBtn_Click`)。

- [ ] **Step 1**：修正 `EstimateProcessSeconds`，让补帧按"整段一次 RIFE"成本估算(修 C 之后才准)，并区分 `Profile`：

```csharp
// 补帧行(L80)改为:整段一次,不要逐段
s += frames * Math.Max(2, interpScale) * 0.09;
// 若 Profile==UltraLow(CPU兜底),放大系数:
if (SafeRender.Profile == SafeRender.DeviceProfile.UltraLow) s *= 6.0;
```

- [ ] **Step 2**：`RunBtn_Click` 开始处，若 `SafeRender.IsWeakDevice`，估算/提示：

```csharp
if (SafeRender.IsWeakDevice && items.Length > 0)
{
    var est = items.Sum(it => VideoService.EstimateProcessSeconds(...));
    if (est > 600)
        _ = ShowWarnAsync($"预计需要约 {est/60:0} 分钟,低配设备建议先用小片段/已开启兼容模式");
}
```

- [ ] **Step 3**：`dotnet build -c Release`；手动确认提示逻辑。

- [ ] **Step 4**：`git add -A; git commit -m "feat(video): 弱设备性能预算提示与ETA修正(C2)"`

### Task C3：磁盘空间检查

**Files:** Modify `D:\deep\alh-pro\ImgUpscalerUI\VideoService.cs`(`ProcessVideoAsync` 开头,workDir 创建后 L261 附近)。

```csharp
var drive = System.IO.DriveInfo.GetDriveInfo(Path.GetPathRoot(workDir)!);
if (drive.AvailableFreeSpace < 2L * 1024 * 1024 * 1024)   // <2GB 提示
    progress?.Report((0, $"⚠ 临时盘({workDir[..3]})剩余 {drive.AvailableFreeSpace / (1024<<20):0}GB,可能不够,建议清理磁盘"));
```

- [ ] 编译 + 手动/`git commit -m "feat(video): 临时磁盘空间检查(C3)"`

---

# Part D　正确性修复(合并前几轮 bug)

### Task D1（最重要）：方案C补帧改"整段一次RIFE + setpts重定时"

**Files:** Modify `D:\deep\alh-pro\ImgUpscalerUI\VideoService.cs`（`bAlignInterp` 分支 L631-645；`InterpKeyframeGapsAsync` L1336 停用/改写作整段）

**Why:** `InterpKeyframeGapsAsync` 逐段只喂 2 帧给 RIFE `-n`，实测真插值帧数 ≈ `n/2 - 1` → 每段只 1 帧真运动，输出"原帧+模糊单帧"，视觉=没补帧。而 `InterpSegmentAsync`(整段序列)能出真 4x(实测 `B_149_83.376.mp4`=149帧流畅)。

**Fix:** 让 C 路径复用"整段一次 RIFE + `finalDurs`/`setpts` 重定时"（即 else 分支的做法），放弃逐段 2 帧。

- [ ] **Step 1**：把 `bAlignInterp` 的 `if (bAlignInterp) { await InterpKeyframeGapsAsync(...) } else {...}` 改为**统一走 `InterpSegmentAsync`**：

```csharp
// 删除 bAlignInterp 分支对 InterpKeyframeGapsAsync 的调用,C 与 B 都走整段 RIFE。
// C 想要的"每态真实停留时长"由 finalDurs + BuildVfrSetptsExpr 重定时实现(该逻辑在 else 分支 L707-718 已有)。
// 因此:把 C(bAlignInterp)合并进 else 分支,并让 preserveRhythm/finalDurs 对 C 也生效。
```

即：把 `bool bAlignInterp = v4Model && !vfrPassthrough && frameCount > 1 && cMode && stDurs != null;` 设为 **false**（或删除该分支），使 C 落到 else 分支（整段 `InterpSegmentAsync` + `frameDurs` 展开 + setpts）。保留 C 的"目标帧数 = (origCount-1)×倍率+1"与 `fOut` 思路作为 else 分支 `globalTarget`。

- [ ] **Step 2**：确保 `preserveRhythm` 对 C 生效：`preserveRhythm`(L567) 目前 `= vfrPassthrough`；补充"去重删过帧或 cMode"时也置 true，让 `finalDurs`+`BuildVfrSetptsExpr` 走展开而非固定帧率：

```csharp
bool preserveRhythm = vfrPassthrough || cMode || (frameDurs != null && frameDurs.Count < origCountEst);
```

- [ ] **Step 3**：删除/停用 `InterpKeyframeGapsAsync`(L1336-1419) 里的逐段 2 帧 RIFE 调用，注释标记"已改为整段RIFE+setpts,勿复启用"。（保守起见可保留方法但不再调用。）

- [ ] **Step 4**：验证：`dotnet build -c Release`;用素材1 跑 C,输出应为 **149 帧 @ ~83.376fps**,且相邻帧 PSNR 显示**真实 4x 插值**(与 B_149 一致)。

- [ ] **Step 5**：`git add -A; git commit -m "fix(video): 方案C改为整段插值+setpts重定时,修复补帧无效(D1)"`

### Task D2：暂停=冻结修复

- [ ] **Step 1**：`VideoService.RunAsync`(L2370) 在 `App.ActiveProcesses.Register(p)`(L2392) 后加 `SetActiveChild(p);`；取消处(L2442)前先 `ResumeActiveProcess();` 再 kill；`finally` 补 `SetActiveChild(null);`（对齐 `EngineService.RunAsync` L189/L232/L241）。
- [ ] **Step 2**：`_activeChild`(L1542) 改为 `ConcurrentDictionary<int,Process>`，`SuspendActiveProcess`/`ResumeActiveProcess` 遍历全部（支持并发超分多 worker）。
- [ ] **Step 3**：`git commit -m "fix(video): 暂停=冻结对RIFE/拆帧/编码生效并支持并发(D2)"`

### Task D3：智能去重内存滚动窗口

- [ ] **Step 1**：`DetectDupFramesAdaptive`(L2160) 的 `grays` 列表加上窗口裁剪：`if (grays.Count > 24) grays.RemoveAt(0);`；但**段合并/第二遍**需要段首锚点与相邻对——用"滚动保留最近 N 帧 + 独立记录段首锚点灰度"重构，或改为**两遍流式**（第一遍仅算 sads+统计不入库，第二遍用最近窗口）。最小改动：把全量 `grays` 换成"只留最近 8 帧用于相邻对+静止段首锚点另存"。
- [ ] **Step 2**：`git commit -m "fix(video): 智能去重滚动窗口,防长视频内存爆(D3)"`

### Task D4：预览与处理共用阈值映射

- [ ] **Step 1**：在 `AnalyzeDupAsync`(L2106-2122) 中，动漫/敏感/标准的 `ssim/protect/segSsim/segSad` 改为与 `ProcessVideoAsync`(L357-382) **完全相同**的映射函数（抽成 `static (double ssim,double sad,double protect,double segSsim,double segSad) DedupParams(int mode,double animeThr)` 供两处调用）。
- [ ] **Step 2**：`git commit -m "fix(video): 预览与处理共用去重阈值映射(D4)"`

### Task D5：setpts 段数上限回退 + SampleGray 容错

- [ ] **Step 1**：`BuildVfrSetptsExpr`(L1905) 上限 1500 改回 **300-400**（防超 Windows 命令行 32767）；或改用 `-filter_script` 文件传表达式（推荐,彻底规避命令行长）。后者为更稳方案。
- [ ] **Step 2**：`SampleGray`(L2370) 的 `Bitmap` 优先用 `bmp.Clone(new Rectangle(0,0,w,h), PixelFormat.Format24bppRgb)` 转成确定 24bpp 再 LockBits,否则在非24bpp PNG 会抛异常；并包 try/catch 返回纯灰。
- [ ] **Step 3**：`git commit -m "fix(video): setpts用filter_script避免超命令行;SampleGray容错(D5)"`

### Task D6：清理项

- [ ] **Step 1**：删除 `OtsuSadThreshold`(L2335,死代码),并把 `DetectDupFramesAdaptive` 注释(L2154-2156)"Otsu 找断崖"改为实际"中位数自适应"。
- [ ] **Step 2**：统一 off-by-one：`DetectDupFramesAdaptive`(L2233)`drop.Add(i+2)` 与 `DetectDupFramesWithMotion`(L2278)/`WithSsim`(L1960)`drop.Add(i+1)` 统一为"删重复对的**后**一帧"语义,并加注释说明帧号约定(1-based)。
- [ ] **Step 3**：`ProbeFps`(L95) 的 `throw` 改为返回 null(与其它探测一致),调用处已判 null。
- [ ] **Step 4**：`git commit -m "chore(video): 清理死代码/注释,统一off-by-one,ProbeFps容错(D6)"`

---

## 自检（Spec Coverage）

- 引导层: A1(弱设备判定)+A2(文案+黄字位)+A3(接线+一键开启) ✅
- 画像层: B1(DeviceProfile+分档感知) ✅
- 降级层: C1(自动限分辨率)+C2(性能预算+ETA)+C3(磁盘检查) ✅
- 正确性: D1(补帧C)→D6(清理) 覆盖前几轮全部 bug ✅

## 风险/取舍（写给执行者）

1. **D1 是核心**：改完 C 后，`InterpKeyframeGapsAsync` 的逐段逻辑应停用；C 与 B 共用"整段RIFE+setpts"，`EstimageProcessSeconds` 也才准。
2. **A/B 预设不要动质量档**：降级只在 `IsWeakDevice` 时生效，强机无感。
3. **D5 用 `-filter_script` 需要把 setpts 表达式写成临时文件**，注意 ffmpeg 参数里 `-filter_script:v` 的用法；若嫌复杂，把段数上限降到 300 即可（够用）。
4. **验证素材**：`D:\deep\rife_selfcheck\in`(素材1,38帧) 可用来验证 D1——改后应产出 149 帧 @83.376，中间帧为真插值（用相邻帧 PSNR 对比确认，参考前几轮实测方法）。
