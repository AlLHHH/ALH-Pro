# 视频抠图 · 第二阶段（Service 层 + 独立页面）实现计划

**日期：** 2026-09-22
**前置：** 第一阶段已完成（`docs/2026-09-22-video-matting-plan.md` 的执行记录；回归 1131/1130）。
`AlhPro.Core/VideoMatting.cs`（alpha 后处理 / 时域滤波 / 合成）、`MattingOutputSpec.cs`、`DmlAttemptLedger.cs` 均已落地并有单测。
**设计依据：** `docs/2026-09-22-video-matting.md`（§四 模块拆分、§七 界面结构、§八 边界、§八 的"开工前探针帧实测 GPU"）。

**目标：** 让用户真的能用上视频抠图 —— 加视频 → 选输出方式（换背景 / 透明通道）→ 看到逐帧进度与预计用时 → 得到成品。
**本阶段交付：** ①`CutoutService` 的"只出蒙版"入口 ②`VideoMattingService`（帧循环/会话/临时盘/ffmpeg 两条输出路径）
③**独立的「视频抠图」页面 + 导航第 5 项** ④实测验收（真机跑出成品并验证 alpha）。

---

## 硬约束（每条都有理由，不是口味）

1. **独立页面，绝不并进图片抠图页**（用户 2026-09-22 明确要求）。现有「AI 抠图」页保持图片抠图不变；
   共享的只有 `CutoutService` 的模型注册表与参数**语义**，页面与任务列表各自独立。
2. **不许调 `CutoutAsync` / `PreviewMaskAsync` 来取蒙版** —— 两者都走完整 `RunCore`（含图片页的阈值/羽化/边缘增强）
   并**每帧写一个 PNG**。视频路径要用它们就是：重复后处理 + 1800 帧写数 GB 临时盘（第一阶段执行记录已写明）。
3. **开工前用探针帧实测 GPU 是否划算**（设计 §八）。实测教训：`birefnet-lite` 的 GPU 比 CPU 还慢 1.5~2 倍**且不报错**；
   `isnet` 则是 GPU 快 2.6 倍。判定"不划算"就直说并用 CPU，ETA 按实际档算 —— 不许"界面上写着 GPU、实际跑 CPU"。
4. **ETA 系数不许用错口径**：0.40 s/帧是**整条 `CutoutAsync`**（含图片页后处理与写盘）的数字。
   本阶段第一件事就是把"只出蒙版"入口的耗时测出来，ETA = 实测值 + 0.096 s/帧（三段算法固定加成，已实测）。
5. **时序滤波必须和推理在同一个串行循环里**：同一 ONNX 会话不能并发 `Run`（`CutoutService` 用信号量串行化），
   且滤波有状态 —— 并行帧循环会既撞车又错序。
6. **中途取消要留干净**：临时目录必须清（复用现有临时清理思路），不留残留帧。
7. **没实测验证过的不许说能用**：本阶段的验收必须是"真机跑出一段片子 + 用 ffprobe/抽 alpha 平面验证"，
   不是"代码写完、测试通过"。

---

## Task A: `CutoutService` 的"只出蒙版"入口（改）

> **执行记录（2026-09-22，已完成）**
> - 拆分点比计划更保守：**推理段整体搬进 `InferMaskCore`**（纯移动，DML 账本/熔断逻辑随之共享），
>   另外只抽"小蒙版位图 → 双三次放大 → 读回 alpha"这段（`ScaleMaskToAlpha`）。
>   计划里写的"拆分点落在 `ProcessMask` 内部"**没有照做**：`ProcessMask` 的上采样那一步与
>   "是否做形态学二值化"耦合在一起，硬拆会动到图片抠图页的热路径 —— 与其动它，不如新增
>   `UpsampleMask`（只放大、不做参数处理），并用下面这条更硬的验证来保证没改坏。
> - **逐字节验证通过**：重构前后各部署一次，用 `_qa/mattingbench` 跑同一张 1080p 探针图，
>   GPU(DML) 与 CPU 两条路各 3 次输出全部同哈希（`0AE75919BB2354ED` / `3B3314EC8EE60229`）✓
>   ⇒ 图片抠图页行为**零变化**（不是"看着一样"）。
> - **ETA 系数已定**（`--mask` 模式实测）：isnet+GPU 只出蒙版 256 ms/帧，加三段算法 96 ms ⇒
>   **0.35 秒/帧**（1800 帧 ≈ 10.5 分钟）；u2netp+GPU 66+96 ≈ 0.16 s/帧；isnet+CPU ≈ 1.03 s/帧。
>   完整路径比只出蒙版贵 120~134 ms/帧（图片页后处理 + 写 PNG），也正是视频路径要绕开它的原因。
> - 顺带记录一个指标口径坑：`--mask` 报的"蒙版覆盖率"是**未过阈值**的原始蒙版，与完整路径输出的
>   alpha 覆盖率不可直接比（别误判成"两个入口结果不一致"）。

**为什么先做它：** 它是视频循环的成本与正确性入口，也决定了 ETA 系数。

**Files:** `ImgUpscalerUI/CutoutService.cs`（改）

**拆分点（关键）：** 现在 `RunCore` 一路做到底（读图 → 缩放 → 推理 → `ProcessMask` 全参数后处理）。
把推理部分提出来，让两条路共用：

```
InferMaskCore(input, model, modelPath, gpuId, progress, ct) → float[,] mask   // 现有 try/catch + DML 账本/熔断那一段，原样搬
UpsampleMask(mask, w, h)                                    // 网络尺寸 → 原图像素尺寸（ProcessMask 里"放大"那一步）
RunCore(...)      = 读图 → InferMaskCore → UpsampleMask → 参数后处理（现状不变，图片页零影响）
CutoutMaskAsync() = 读图 → InferMaskCore → UpsampleMask → 直接返回 0~1 float[]（不写文件、不做参数后处理、不进 _rawMaskCache）
```

- [ ] **Step 1:** 提取 `InferMaskCore` / `UpsampleMask`，`RunCore` 改为调用它们（**行为不许变**）。
- [ ] **Step 2: 回归**：`dotnet build ImgUpscalerUI -c Release -p:Platform=x64 -p x64` + 全量测试（1131/1130 基线不变）。
- [ ] **Step 3: 行为不变的实测**（不能只看代码）：用 `_qa/mattingbench` 跑同一张探针图，对比改动前后的
      输出 PNG（**逐字节比对**，必须完全一致；DML 路径与 CPU 路径各跑一次）。
      > 为什么这么严：这段是图片抠图页唯一的生产路径，改错了就是"抠图页悄悄变样"。
- [ ] **Step 4:** 新增 `public static async Task<(float[] alpha, int w, int h)> CutoutMaskAsync(string input, string modelKey, int gpuId, IProgress<(int,string)>? progress = null, CancellationToken ct = default)`。
- [ ] **Step 5: 计时实测**（写进报告）：isnet/GPU 与 isnet/CPU 各跑 5 帧取稳态，得到**纯推理 + 上采样**的 ms/帧,
      写进 `_qa/视频抠图_性能实测_20260922.md` 的新小节，并据此定 ETA 系数常量。
- [ ] **Step 6:** 提交（含实测数字）。

---

## Task B: 帧序列的读写与临时盘（改 `VideoService`，最小暴露）

**Files:** `ImgUpscalerUI/VideoService.cs`（改：把 `ExtractFramesCoreAsync` 从 `private` 放宽到 `internal`）

- 拆帧**复用现有** `ExtractFramesCoreAsync(ffmpeg, inputVideo, trimArgs, vfExpr, framesDir, progress, ct, origCountEst)` ✓
  —— 它已经处理了 HDR→SDR 色调映射、非法色彩标记兜底、逐帧进度上报这些坑，重写一份就是重新踩一遍。
- 帧读回：沿用现有做法（GDI+ 读 JPG）✓ 不新造轮子；alpha 序列另存为**灰度 PNG**（JPG 无 alpha）。
- 临时目录：`Path.Combine(Path.GetTempPath(), "alhpro_matting_<guid>")`（照 `alhpro_noiseprobe_` 的既有约定），
  用完即删（含取消路径）。
- [ ] 验收：对同一段素材，拆帧产出的帧数与 `-vsync 0` 探测一致；取消后临时目录被清空（留证）。

---

## Task C: `VideoMattingService`（新）—— 帧循环与两条输出

**Files:** `ImgUpscalerUI/VideoMattingService.cs`（新）

**核心循环（串行，一帧一次）：**
```
拆帧(JPG) → 探测帧实测 GPU 是否划算(探针 2~3 帧,见硬约束 3)
  → for each frame:
        CutoutMaskAsync(jpg)            // 只出蒙版
        → VideoMatting.PostProcessAlpha(fg,bg,feather,morph)
        → AlphaTemporalFilter.Push()    // 切点用 LooksLikeSceneCut 判定后 Reset
        → ① 换背景:读背景图/色 → VideoMatting.Composite → 写处理帧
        → ② 透明通道:alpha 写灰度 PNG,原帧直接复用
        → 进度 + ETA(EtaText.ForRemainingByRate) + 取消检查
  → ffmpeg 合帧编码(MattingOutputSpecs 给的规格)
  → 清理临时盘
```

**必须处理的边界（设计 §八 已列，这里落成代码要求）：**

| 情况 | 要求 |
|---|---|
| 模型缺失 | 抛可操作错误（照图片页那句"请安装/恢复模型包"），不静默降级成"没抠图" |
| GPU 被判不划算/设备级失败 | 走 CPU + **只提示一次** + ETA 重算（`_dmlLedger` 已在 `CutoutService` 里生效，这里只需如实显示当前档） |
| 单帧读失败 | 跳过该帧并计数，全部失败才报错（长视频里偶发读失败不该整段崩） |
| 背景分辨率 ≠ 视频分辨率 | 背景图按"覆盖并居中裁剪"缩放；纯色直接铺 |
| 音频 | 换背景 `-c:a copy`（源音频不是 aac/mp3/ac3/eac3 才转 aac，照 `VideoService.cs:3715`）；透明通道按容器（Opus / PCM） |
| 输出文件已存在 | 自动加序号，不覆盖用户文件 |

- [ ] Step 1: 先写"能跑通 1 秒素材"的最小闭环（不做 ETA/不做边界），跑通再补 —— 避免一次性写完再调。
- [ ] Step 2: 逐条补边界 + 真机验证（每条都要有日志或产物为证）。
- [ ] Step 3: 换背景与透明通道各出一份真机产物，**用 ffprobe + 抽 alpha 平面数点**验证：
      换背景 = `yuv420p` 且无 alpha；透明通道 = `alpha_mode=1` 且透明区真的透明（读回时必须 `-c:v libvpx-vp9`，
      见第一阶段报告 §四）。
- [ ] Step 4: 提交。

---

## Task D: 独立的「视频抠图」页面 + 导航第 5 项（新）

**Files:** `ImgUpscalerUI/Views/VideoMattingView.xaml(.cs)`（新）、`Views/MainPage.xaml(.cs)`（改）、
`matting-settings.json`（新，照 `cutout-settings.json`）

- 导航：`NavList` 里 `ListViewItem x:Name="NavMatting" Tag="matting"`（现有四项 Tag = upscale/cutout/video/audio ✓），
  **放在「AI 抠图」之后**（两件相关的事挨着）。
- 页面结构照设计 §七（素材 / 输出方式 / 背景 / 模型 / 抠图参数 / 时序稳定 / 输出 + 右侧任务列表与日志）。
  参数控件与文案风格照 `CutoutView` / `VideoView` 复用现成样式（`SectionTitle` / `HintText` / ToolTip）。
- 模型下拉：**默认 `isnet-general-use`**（`CutoutService.DefaultModelKey` ✓ 与图片页同源，别再写死下标）。
- 容器下拉：**默认 MOV / ProRes 4444**（`MattingOutputSpecs.TransparentContainers[0]` ✓）。
- 透明通道那项旁边必须写明设计 §八 那句提示："要叠加请用剪辑软件（Premiere/剪映/DaVinci/OBS）"。
- 设置页那句现在**是错的**（"AI 抠图已强制使用 CPU…对抠图不生效"）→ 改成
  "图片抠图：CPU（避免占满显卡）；视频抠图：跟随这里选的计算设备"（设计 §十一 已列）。

- [ ] Step 1: 导航项 + 空页面骨架（能切过去、能切回来，其它页不受影响）。
- [ ] Step 2: 左栏参数区 + 记住上次（`matting-settings.json`）。
- [ ] Step 3: 接上 Task C 的服务层 + 任务列表/进度/ETA/日志。
- [ ] Step 4: **只读 UIA + 截图实测**（仓库铁律：绝不注入鼠标键盘）：
      `_qa\uia_drv` 切到新页 → 截图 → 逐项核对默认值（模型=ISNet、容器=MOV、时序稳定=默认档）。
- [ ] Step 5: 提交。

---

## Task E: 文档与"顾全大局"同步

- `docs/2026-09-22-video-matting.md`：§九 补第二阶段的实测数字；把"未做/已做"列表对齐现状。
- `website/*.html` 与 `RELEASE_NOTES.md` / `release_history.json`：**等本阶段真机验收通过后**再写
  （`AnnouncementCopyTests` 会守这三处文案一致性）。用户 2026-09-22 明确：**本阶段只本地部署，不打包不上传**。
- `docs/2026-09-22-video-matting-plan.md` 的执行记录里链到本计划。

---

## 风险与应对（都来自第一阶段的实测教训）

| 风险 | 证据 | 应对 |
|---|---|---|
| GPU 路"看着能用、其实慢一倍" | birefnet-lite 在 DML 上 7.4~11.5 s/帧 vs CPU 4.9 s，且不报错 | 硬约束 3：探针帧实测后再定档，并把实际档写进进度文案与日志 |
| 设备级失败污染整进程 | 887A0005 之后同进程 DML 全部退化成 CPU 速度 | 已修（`DmlAttemptLedger` + 设备级熔断）；本阶段只需**如实显示**当前档，不许假装还在用 GPU |
| 透明成品"别人看着是黑底" | 连 ffmpeg 原生 vp9 解码器都丢 alpha | 默认 MOV/ProRes 4444（已定）；读回一律带 `-c:v libvpx-vp9`；界面写明叠加方式 |
| 逐帧写文件把盘写爆 | `CutoutAsync` 每帧一个 PNG | 硬约束 2 + `CutoutMaskAsync` 只回内存；alpha 才落灰度 PNG（小得多） |
| 后处理成为新瓶颈 | 1080p 实测 82 ms/帧（极值档 184 ms），占 GPU 单帧 20% | ETA 里单列；将来优化优先动 feather/morph（已写进 `VideoMatting` 注释） |
| 进度条"假活跃" | 第一阶段修过的 `CompareBuildHint`、"推理中平滑推进"同类问题 | ETA 用 `EtaText.ForRemainingByRate`；每帧都上报真实帧号，不用定时器假装 |

---

## 验收标准（本阶段"完成"的定义）

1. 真机跑通：一段 ≥10 秒的素材，两种输出各出一份成品，**时长/帧率/音频与源一致**（ffprobe 核对）。
2. 透明通道成品：`alpha_mode=1`，抽 alpha 平面**透明区真的是 0**（数点与源一致），画面无鬼影、无逐帧闪烁。
3. 换背景成品：边缘无锯齿硬切、无黑边；`-c:a copy` 生效（音频流与源编码一致）。
4. 进度与 ETA：预计用时与实际相差在合理范围（用实测系数；偏差大就是系数错，改系数不是改文案）。
5. 取消：中途取消后无残留临时目录、无残留进程。
6. 界面：新页面只在导航第 5 项，图片抠图页功能与外观**零变化**（用第一阶段那套 UIA 截图对照）。
7. 全量测试：失败数仍为 1（既有 `NavLogoWebsiteTests`），总数 = 1131 + 本阶段新增。
