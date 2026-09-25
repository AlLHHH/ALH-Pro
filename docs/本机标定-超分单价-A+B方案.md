# 本机标定:超分单价不再用「开发机」的数字(A+B 方案)

> 用户 2026-09-25 提问:「这个时长检查是来自我的设备吗?」「那别人使用时,这个检查决定先超分还是先补帧也是用我的设备???」
> —— **是的,当前实现就是这样**,这是一个真缺陷。用户批准 A+B 并附加硬约束:**不要引入新的 bug**。
> 本文是施工契约(队长写);实现、验证、复核、集成各轮的验收都以本文为准。

---

## 一、缺陷(现状,逐处可指认)

| 位置 | 现状 | 问题 |
| --- | --- | --- |
| `AlhPro.Core/PipelineOrderPlan.cs:100-117` `UpscaleRates` | 超分单价表,注释逐条写「2026-09-13 实测」 | **全部是 RTX 4060 Laptop 这一台机器的实测**。表中每个数字都带出处,但出处是"本仓库真机",不是"用户这台机器" |
| 同上 `Decide(...)`(`:213`) | 命中表 → `Measured=true` → 按表里的数字判「先超分还是先补帧」 | 于是**别人的机器**用**我的机器的秒/帧**决定阶段顺序 |
| `ImgUpscalerUI/VideoService.cs:2204` | 生产路径唯一的判定调用点,只传模型/倍率/分辨率 | 没有任何"本机实测"输入 |
| `VideoService.cs:2204` 注释 | 「只读 Core 里的内置实测常量」 | 把缺陷写成了设计 |

**真机证据(2026-09-25)**:本机 Real-CUGAN 缺单价行时 `u=0`(当成免费)→ 判定选「补帧→超分」,让超分去跑补帧后的 1401 帧(≈39 分钟),正确顺序只需跑源 350 帧(≈10 分钟)。同一素材在**别人的机器**上,这个判定的对错完全由我的机器决定 —— 与用户提问完全一致。

**为什么不能靠"再补几行实测"解决**:表里数字再多也仍是**一台机器**的。笔记本 GPU 热降频实测就有 10.19 / 13.21 / 15.87 / 19.05 s/帧 四个样本(1.9× 离散);50 系 / AMD / Intel 从未在本机测过。**唯一正确的来源是用户自己这台机器的实测。**

---

## 二、A:判定只吃「本机实测单价」

### A1 新增 `AlhPro.Core/LocalPriceBook.cs`(纯逻辑,可单测)

```csharp
public readonly record struct LocalPrice(
    string ModelKey, int EngineScale,
    double SecondsPerFrame,     // 采样分辨率下的秒/帧(两点法算出,已扣掉每进程固定地板)
    long SamplePixels,          // 采样时的源面积(px)
    int SampleFrames,           // 两点法里"多点"那一次用的帧数 N
    double FloorSeconds,        // 两点法里"1 帧"那一次的耗时(含地板)
    double SampleSeconds,       // 两点法里 N 帧那一次的耗时
    string MeasuredAtUtc, string MachineKey, string Note)
{
    public double SecondsPerFrame1080p { get; }   // = SecondsPerFrame × (1080p 面积 / SamplePixels)
    public string Provenance { get; }             // 一行可审计出处(见 A4)
}

public static class LocalPriceBook
{
    public const int MinSampleFrames = 4;         // <4 帧的差值被噪声吃掉,拒收
    public static string Key(string modelKey, int engineScale);              // "realcugan-se@2x"
    public static LocalPrice? Find(IEnumerable<LocalPrice>? book, string? model, int engineScale);
    public static double? PerFrame1080p(IEnumerable<LocalPrice>? book, string? model, int engineScale, out string provenance);
    public static bool TryBuild(string? model, int engineScale, int sampleFrames, double floorSeconds, double sampleSeconds,
        long samplePixels, string machineKey, string measuredAtUtc, string note,
        out LocalPrice price, out string reject);
}
```

`TryBuild` 拒收条件(全部返回中文原因,不抛异常):
- `NormalizeModel(model) == null` 或 `engineScale < 1`;
- `sampleFrames < MinSampleFrames`;
- `floorSeconds` / `sampleSeconds` 非有限或 ≤0;`samplePixels <= 0`;`machineKey` 空白;
- **两点法退化**:`sampleSeconds <= floorSeconds`(等于没测出正斜率 → 噪声,必须拒收,**不许**回退成 `sampleSeconds/N`);
- 算出单价 ≤ `0.0005` 或 > `120` 秒/帧(明显异常,拒收)。

### A2 两点法(定标数学,`AlhPro.Core/CalibrationSample.cs`,纯函数)

引擎**每进程有固定地板**(启动 + 模型加载 + 首次着色器/管线创建)。本仓库早已实测到这点
(`PipelineOrderPlan.cs:36-38`:1 帧目录 = 1.01~1.19 s/帧,其中地板 0.75~0.92 s;40 帧目录 = 0.25~0.30 s/帧,
**地板约为单帧成本的 3 倍**),并据此写下「凡是 1~3 帧样本给出的单价都不可信」。
所以**不是**回避小样本,而是**把地板减掉**:

```
t1  = F + 1·p        (1 帧那一次)
tN  = F + N·p        (N 帧那一次)
⇒ p = (tN − t1) / (N − 1)          F = t1 − p
```

- `p` = 该模型 × 该引擎倍率 × 该源分辨率的**秒/帧**(已扣地板);
- 采样帧数 `N = Clamp((int)Math.Floor(BudgetSeconds / pEst), MinSampleFrames, 8)`,`BudgetSeconds = 30`,
  `pEst` = 内置表单价按源面积缩放(查不到就用 `1.0 s/帧` 这个偏悲观值)⇒ 便宜的模型多采几帧、贵的少采,总等待被 30 s 量级框住;
- 两次调用共 `N+1` 帧;Real-CUGAN 1080p 实测 1.65 s/帧 ⇒ 9 帧 + 2 次地板 ≈ 17 s(一次性,之后写盘复用)。

要点:**A2 让标定精度与帧数弱相关**(1 帧那次只贡献地板),所以 `MinSampleFrames = 4` 也不至于像旧口径那样被地板污染。

### A3 `PipelineOrderPlan.Decide(...)` 改判据优先级

生产入口新增可选参数(既有调用方与成百上千行单测**逐字不变**):

```csharp
public static Decision Decide(string? engine, string? model, double scale, int interpScale,
    int srcW, int srcH, int sourceFrames = 900, double areaScale = 0,
    double minSavingsPercent = UseBuiltInMinSavings,
    IEnumerable<LocalPrice>? localPrices = null);      // ← 新增
```

优先级与语义:

1. **本机实测命中**(`LocalPriceBook.Find` 且 `MachineKey` 与当前机器一致)→ `u = SecondsPerFrame1080p`,
   `Measured = true`,出处 = 本机实测那一行;
2. **未命中 → 一律旧顺序(补帧→超分)**,`Measured = false`,理由含
   `【本机未标定】<模型> @ <引擎倍率>x —— 内置表是他机实测,不作为本机判据,保守用旧顺序`;
3. `UpscaleRates` 内置表**保留但不再参与判定**;类注释必须改写为
   「他机实测(开发机 RTX 4060 Laptop),**只作资料与回归基线**,不是任何用户机器上的判据」。
   `LookupUpscaleSecondsPerFrame` 保留(资料/测试用),但生产判定路径不再取它的值。
4. `Decide(CostInput ...)` 重载签名不动(单测依赖),新旧入口都要能被单测覆盖。

> **口径解释(必须写进类注释)**:「保守」= 不拿没在**这台机器**上测过的数字去改阶段顺序;
> 未标定的正常路径由 B 在同一个任务里补上,所以「回退旧顺序」是**标定失败时的安全网**,不是常态。

### A4 出处文本(可审计)

本机命中时 `Reason` 必须含:`本机实测 <UTC+8 时间> 两点法(1 帧 <floor>s + <N> 帧 <sample>s,源 <W>×<H>)→ <p> s/帧@<采样分辨率>,折算 1080p <p1080> s/帧`。
机器指纹不匹配时必须写明 `另一台机器(<key 摘要>)的标定,已忽略`。

---

## 三、B:首次自动标定(同一个任务内,跑在判定之前)

### B1 落盘 `ImgUpscalerUI/CalibMemory.cs`

- 路径:`ParaPaths.SettingsFile("engine-prices.json")`(与 `PerfMemory` 同目录);
- 结构:`{ "schema": 1, "prices": [ LocalPrice... ] }`;按 `Key(modelKey, engineScale)` 去重,后写覆盖;
- 容错:文件缺失/损坏/字段非法 → 空表,**绝不抛**;先写临时文件再 `File.Move(..., overwrite)` 防写坏;
- `All()` / `Upsert(LocalPrice)` / `MachineKeyOf()`。

**机器指纹 `MachineKey`**:GPU 名称 + 引擎可执行文件标识(名字 + 长度 + 最后写入时间)。
换显卡 / 换引擎重编版 ⇒ 指纹变 ⇒ 旧标定**自动失效并重标**;不匹配的旧记录保留在文件里(可审计),但不参与判定。

### B2 标定器 `ImgUpscalerUI/UpscaleCalibrator.cs`

```csharp
internal static class UpscaleCalibrator
{
    public static Task<AlhPro.Core.LocalPrice?> MeasureAsync(
        string framesIn, int frameCount, string workDir, int srcW, int srcH,
        int sampleFrames, string machineKey,
        Func<string /*inDir*/, string /*outDir*/, CancellationToken, Task> runUpscale,
        IProgress<(int pct, string msg)>? progress, CancellationToken ct);
}
```

- 采样帧来源:**复用已拆好的源帧 `framesIn`**(JPG、源分辨率、已含降噪/裁剪),从中段等间隔取 `N+1` 张
  (避开片头/片尾黑场;取不满就如实报「素材帧数不足,跳过标定」);
- 临时目录 `<workDir>\calib_in` / `calib_out`;**无论成功失败都在 `finally` 里删干净**(含 `runUpscale` 中途抛异常);
- 计时用 `Stopwatch`;先 1 帧那次、再 N 帧那次;
- `runUpscale` 由调用方注入 = **本次真跑的那一条引擎入口**(见 B3),标定与生产不各写一份参数;
- **任何异常都吞掉并记日志**(退出码、OOM、引擎未就绪、取消)→ 返回 null,任务继续跑,绝不因为标定失败而中断;
- `ct` 全程传递;用户取消 ⇒ 直接返回 null;
- 日志:`AppLogger.Info` 写详情(两次耗时、N、采样面积、算出单价),UI 进度区写**一句短话**
  (走 `AlhPro.Core.LogShortText.ClampToChineseLimit`,例如 `· 正在按本机实测标定超分单价(Real-CUGAN 2x,9 帧)…`)。

### B3 「同一条入口」怎么保证(单一口径)

现状:`VideoService.cs:2743-2886` 的批次循环里,**每批**重新判 ncnn / ONNX
(`upGpu < 0` / `ShouldUseOnnxEsrgan()` / `waifuOnnx` / `ncnnUnreliable` / `fastMode`),
`ncnnUnreliable` 会在黑帧防御里**中途翻真**(`:2973`)⇒「本次走哪条路」是动态的。

做法:
- 把这条判据抽成 **纯函数** `AlhPro.Core/UpscaleBackendPlan.UseOnnx(engine, gpuId, onnxPreferredEsrgan, onnxPreferredWaifu2x, ncnnUnreliable, fastMode)`,
  真值表与现行 if/else **逐字等价**(realcugan 无 ONNX 通道 → 恒 false),单测把真值表钉住;
- 批次循环改用该纯函数选路(`onnxModelPath` 的解析逻辑保持原样),**这是唯一一处被改动的热路径代码,必须逐字等价**;
- 标定器用**同一纯函数 + 开跑前的取值**(`ncnnUnreliable=false`,即第 1 批的真实取法)决定走 ncnn 还是 ONNX,
  并用与批次循环同一套参数(引擎/模型/倍率/降噪档/GPU/分块/输出格式 `jpg`)发起 `EngineService.UpscaleDirAsync`
  或 `EsrganOnnxService.UpscaleDirAsync`。
  ⇒ 标定测到的就是**第 1 批真跑的那条路**;若第 1 批之后因黑帧切成 ONNX,那是"实测漂移"的既有行为,不在本轮范围。

### B4 接线(`VideoService.cs` 判定点 `:2200-2205`)

```
仅当 (doUpscale && frameInterp && upscaleRuns) —— 与判定同一个守卫:
  1. engineScale = EngineScalePolicy.Decide(engine, model, upScaleNow).EngineScale
  2. local = CalibMemory.All() 里按 (model, engineScale, 当前 MachineKey) 查
  3. 查不到 → await UpscaleCalibrator.MeasureAsync(..., sampleFrames: CalibrationSample.FramesFor(pEst, srcPixels) ...)
             → 成功则 CalibMemory.Upsert(...) 并复用
  4. PipelineOrderPlan.Decide(..., localPrices: CalibMemory.All())
  5. 未标定仍走旧顺序时,UI 日志区额外一句短话说明原因(不许静默)
```

- **只在"超分与补帧都要真跑"时标定**(其余情况顺序无意义,标了也白花时间);
- 标定耗时计入任务日志(阶段名如 `准备(超分单价标定)`),**不许**混进后续 ETA 的每帧速率(否则污染 `PerfMemory`);
- 用户取消 / 引擎不可用 / 素材帧不足 ⇒ 跳过并如实说明。

---

## 四、验收标准(施工方必须逐条给出证据)

1. `AlhPro.Core/LocalPriceBook.cs`、`AlhPro.Core/CalibrationSample.cs`、`AlhPro.Core/UpscaleBackendPlan.cs` 新增;纯逻辑,无文件 IO、无 UI 依赖。
2. `PipelineOrderPlan.Decide(...)` 新增 `localPrices` 参数;命中本机实测才 `Measured=true`;未标定 ⇒ 旧顺序 + 理由含 `【本机未标定】`;`UpscaleRates` 不再参与判定且类注释改写(见 A3.3)。
3. 两点法与拒收条件有**逐条单测**(含:退化样本 `tN<=t1` 拒收、N<4 拒收、单价上限、面积折算、机器指纹不匹配不采信、真值表等价)。
4. `ImgUpscalerUI/CalibMemory.cs` 落盘/容错/去重/机器指纹;`UpscaleCalibrator` 采样-计时-清理-吞异常全到位;`finally` 清理有证据。
5. 判定点接线:本机无标定 ⇒ 先标定再判定;标定失败 ⇒ 旧顺序 + 用户可见说明;两条路都不抛异常中断任务。
6. `UpscaleBackendPlan.UseOnnx` 真值表与现行批次循环 if/else **逐字等价**,且批次循环确实改用它(源码断言 + 单测)。
7. `dotnet build ImgUpscalerUI\ImgUpscalerUI.csproj -c Debug -p:Platform=x64 -v m` **0 错误**。
8. `dotnet test AlhPro.Tests\AlhPro.Tests.csproj -c Debug /t:Rebuild --nologo` **全绿**,且**没有删除/削弱既有断言**(条数只增不减;若必须改既有断言,在 output 里写明哪条、为什么)。
9. 文档:类注释与本文同步;新增/更新 `docs/本机标定-超分单价.md`(口径、公式、边界、失败路径、已知限制)。

**明确不做(non-goals)**:不动 `VideoPipeline.cs` 的 ETA 常数;不新增界面开关;不动版本号、不打包、不发布;
不再往内置表里"补他机实测";不改补帧锚点表;不动 `PerfMemory` 的记账口径。

---

## 五、已知限制(必须如实写进文档,不许含糊)

1. 标定测的是**第 1 批**那条路;若该批之后因黑帧/显存降到另一条路,单价会有偏差(既有行为,未修)。
2. `VideoPipeline.cs:116` 的 ETA 每帧常数**仍是开发机数字**(只影响"预计剩余时间",不影响阶段顺序);本机 `PerfMemory` 会在跑过一遍后用实测修正 —— 属另一条线,**本轮不改**。
3. 采样帧来自**本次素材**;换素材(分辨率/内容差异大)且面积折算不成立时会有误差。面积线性本身有本仓库实测支持
   (`PipelineOrderPlan.cs:29-38`:扣掉地板后比值 3.98 对 4.00,偏差 −0.5%~−7%),但仍**不是**严格线性。
4. 两点法用两次进程启动,GPU 热降频会让两次的"地板"不同(实测同机样本离散可达 1.9×);故标定结果按"一次快照"对待,
   **不**声称是永远准确的真理。

---

## 六、2026-09-25 修订(原文保留,本节追加)

t28 独立验证 + t29 质量门在放行前抓到两条**新引入**的缺陷(F1 high / F2 medium),另有 F3/F4 两条 low。
本节记录**原设计错在哪、现在用什么保证**,原文一律不改(可追溯);实现口径详见 `docs/本机标定-超分单价.md`。

### 6.1 B3 原设计错在哪(F1)

原文 B3 写的是「标定器用**同一纯函数 + 开跑前的取值**(`ncnnUnreliable=false`,即第 1 批的真实取法)决定走 ncnn 还是 ONNX」。
**这句是错的**:`ncnnUnreliable` 不是关键变量 —— 真正的后端开关是 `upGpu` / `waifuOnnx` / `upOnnxDml`,而它们在
**超分 GPU 探测块**里才被定稿(`upGpu = -1; upOnnxDml = true`、`waifuOnnx = true`),那块代码原本位于
**判定点之后约 300 行**;它还会**改写 `model`**(Real-CUGAN 单档探测失败 → 自动换降噪档)并可能 `throw`。
于是:标定用**原始 `gpuId`** 选后端 → 生产第一批可能走 ONNX;**探测失败的机器上首次运行必然撞上**
(`ShouldUseOnnx*` 读的是**已落盘**的探测结论,而标定只发生在"没标定过"的那一次)。最重分支:ncnn 在真实
分辨率**静默出黑帧且退出码 0**,黑帧更快 ⇒ 偏小的错单价被接受并**永久落盘** ⇒ 判定偏向「补帧→超分」——
正是 2026-09-25 那次 39 分钟误判的同一类。受影响面恰是本队的重点机型(50 系 / AMD / Intel / 探测失败但
DirectML 可用)。

### 6.2 现在用什么保证(I1~I4 + F3)

* **I1** 探测 + 设备定稿(`VideoService` 的 `if (upscaleRuns) { … }` 整段)**上移到标定与判定之前** ⇒
  标定测到的后端按构造就是第 1 批真跑的那个(同一个 `AlhPro.Core.UpscaleBackendPlan.UseOnnx` + 同一套参数);
* **I2** 单价**按后端分格存与查**(`LocalPriceBook` 的 `Backend` 键)。两层:判定喂进去的表先按后端筛过 +
  `PipelineOrderPlan.Decide(..., backend:)` 把后端传进 Core,由 `LocalPriceBook.Resolve` **机械过滤**;
  **显式传入但认不出的后端名 ⇒ 一格都不命中**(修掉了"认不出 = 不过滤"这个漏洞 —— 注意
  `UpscaleBackendPlan.Unknown` 本身就是空串,所以判据必须是"是否显式传了非 null");
* **I3** 样本输出为黑帧 / 0 字节空帧 / 帧数对不上 ⇒ 拒收不落盘;判黑**复用**批次循环那条
  `EngineService.IsBlackPng → FrameInspect.IsDefectiveFrame`,不新造第二套;
* **I4** 后端未确认(空串/空白/认不出)⇒ `TryBuild` 拒收、`Resolve` 不命中、`CalibMemory` 读回时直接丢弃
  该条记录 ⇒ 既不落盘、也绝不被采用;
* **F3** 除九条绝对阈值外,新增**相对噪声门槛** `CalibrationSample.MinDeltaRatio = 0.15`:
  两点法差值必须 ≥ 0.15 × 1 帧那次的耗时,否则与"两次进程启动的抖动"同量级 ⇒ 拒收(挡掉假精度);
* **F2** 标定墙钟**真的**从 `PerfMemory` 样本窗口扣掉(`ConsumeCalibratedSeconds()` 取一次清零),
  并把原先那句错日志与**钉住它的断言**一起改正 —— 日志与账两半成对,由接线测试同时钉住;
* **F4** `VideoPipeline.cs` 里"改内置单价表就能换顺序"的陈旧口径清零;`AlhPro.Tests/TestResults/` 进 `.gitignore`。

### 6.3 仍然没验证的(不许含糊)

* 真机上跑过一次完整的**两点法**(本轮只有单测 + 源码断言 + 反射直调 `MeasureAsync` 的运行时探针);
* **50 系 / AMD / Intel + ONNX 路径**下的标定数值(本机只有 RTX 4060 Laptop;真值表有单测,但"ONNX 那条路上的秒/帧"没有本机实测);
* 第 1 批之后因黑帧/显存切成 ONNX 时的单价漂移(见 6.2 I2:判定与落盘已不受污染,但**实际耗时**仍会偏)。
