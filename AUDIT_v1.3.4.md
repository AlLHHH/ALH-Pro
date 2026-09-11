# ALH Pro v1.3.4 全面自检报告

> 审查日期:2026-09-10 · 基线:`301ed27`(与 origin/main 同步) · 构建:`dotnet build -c Release -p:Platform=x64` = **0 错误 / 22 警告**(未退化)
> 方法:9 路并行深度审查(核心算法层 / 设备引擎层 / ONNX 推理 / 视频流水线 / 视频UI / 图片抠图 / 音频 / 应用壳 / 跨模块横向),每条结论均给到 `文件:行号`。
> **修复前请先读「零、最重要的那一条」。**

---

## 修复进度(滚动更新)

### ✅ 已修 + 已部署

| 轮次 | commit | 内容 | 验证 |
|---|---|---|---|
| 第 0 步 | `07929ac` | DXGI vtable 槽位 `11→12`、`9→10` + 两个 COM 声明按真实顺序重排 | ✅ 本机探针实测(12/10 返回真实卡名) |
| 第 0 步 | `07929ac` | 删除 `IDXGIFactory6` + GPU 偏好 API(实测 AccessViolation 崩溃隐患) | ✅ 实测确认崩溃面消除 |
| 第 0 步 | `07929ac` | 补帧 ONNX 设备号**二次映射** | ⚠️ **待主力机日志验收**(见第零节) |
| 第 0 步 | `07929ac` | `GpuName`/`GpuInfo` 的 Vega 正则(RX Vega 56/64 曾被判核显) | ✅ 10 个真实卡名实测全对 + 17 条测试 |
| 第 0 步 | `07929ac` | DXGI 枚举为空时不再静默(区分"调用失败/成功但0张卡"并告警) | ✅ 防复发 |
| 第 2 批 | `edecc27` | **C-5** 黑帧防线:存在量词 → 逐帧对应(+ 规则抽进 Core,3 条测试钉住) | ✅ 构建/单测 |
| 第 2 批 | `edecc27` | **C-6** `ValidateVideoFileAsync` 加 `minFrames`(探测=5,真实输出=1) | ✅ 构建/单测 |
| 第 2 批 | `edecc27` | **C-7** 删除从未生效的超4K 弹窗 + 修掉跨线程读控件(爆盘预检恢复生效) | ✅ 构建/单测 |

当前基线:**构建 0 错误 / 22 警告(未退化)** · **单测 182/182**(起始 162)· 部署启动验证通过。

### ⬜ 未修(按建议顺序)

**第三批(用户直接可感,改动都小)**:C-11 / C-12 抠图页(记住上次失效、崩溃+卡死);C-8 / C-9 / C-10 超分页(选区倍率错档、ONNX 丢 1x/降噪/TTA/码率、存读 off-by-one)
**第四批(正确性)**:C-1 超分输出 NaN/Inf 防线;C-3 会话池失败静默落 CPU;C-4 CPU 会话缓存进 GPU 键
**第五批(需重构/需你复测)**:C-2 连击表编号空间统一(需连同"别每帧重算 DXGI"一起做);音频 C1 谱轴、C2 重采样相位(需听感复测)、C3 取消杀不掉 ffmpeg、C4 MediaPlayer 释放后复用
**其它**:Core 的 `GpuFault` 补设备挂起文案、`BuildVfrSetptsExpr` 非有限时长死循环;I-13/I-14/I-15 预览=结果三处失真;第三节发布一致性(hint1.json / release_history.json / installer `ModelsUrl`);第四节 ETA 口径;约 25 条 Important;以及若干死代码与冗余文案

### ⏸ 需要你提供的信息
**主力机探针结果**(桌面 `ALHPro_设备映射探针\ALHPro_设备映射探针.exe` → 看 `[3]` 是否落到 `NVIDIA GeForce RTX 5070 Ti`)——第零节二次映射的实际落点只能靠它确认。

---

## 零、最重要的那一条:RIFE 补帧设备号被映射两次 → 补帧实际跑在核显上

**这是"选独显跑的是核显"的实测根因,且至今仍在。**

### 证据链(git 已证明)

`bc554b5`——就是那个声称 *"锁定NVIDIA独显绝不跑核显"* 的提交——**在同一次提交里同时改了两处**,把一次映射变成了两次:

```diff
# RifeOnnxService.cs:43
-                int dm = EngineService.ToDmlDevice(gpuId);
+                int dm = EngineService.ResolveDmlDevice(gpuId);
```

同时 `VideoService.cs:2280` 新增 `dmlGpu = EngineService.ResolveDmlDevice(AppSettings.GpuIndex)`,并把**已经是 DirectML 号**的值传给 `CreateSessions(concurrency, dmlGpu)`(`VideoService.cs:2288`)→ `RifeOnnxService.cs:76` → `BuildSession` → **第 43 行又映射一次**。

`ResolveDmlDevice` **不幂等**:`ResolveEngineGpu` 在编号存在于引擎表时**原样返回**(`EngineService.cs:198`),所以第二次调用会把"DML 号"当成"引擎号"再解释一遍。双卡机上引擎号与 DXGI 号都在 {0,1},都能"命中引擎表",于是第二次不是空操作。

### 在主力机(5070 Ti + AMD 核显)上算一遍

已知事实:引擎序**独显在前**(`-g 0 = RTX 5070 Ti`、`-g 1 = AMD Radeon`),DXGI/注册表序**核显在前**(`#0 = AMD`、`#1 = NVIDIA`)。

| 步骤 | 结果 |
|---|---|
| 选 NVIDIA → `GpuIndex = 0` | |
| `ResolveDmlDevice(0)` = `ToDmlDevice(引擎0=NVIDIA)` | → **1** ✅ |
| `CreateSessions(2, 1)` → `BuildSession(1)` | |
| `ResolveDmlDevice(1)` → `ResolveEngineGpu(1)`=1(**引擎1 = AMD Radeon**) → `ToDmlDevice(1)` | → **0** ❌ |
| `AppendExecutionProvider_DML(0)` | **AMD 核显** |

**补帧会话建在 AMD 核显上,而所有日志与设备号都显示 "1/独显"。**

### 为什么这解释了你说的"1.3.0 跑的是独显 正常 但是现在是核显"

`bc554b5` 之前 `RifeOnnxService` 只有 `ToDmlDevice(gpuId)` **一次**映射、`VideoService` 传引擎号 → 一次映射 → 正确。这就是 1.3.0 的正常行为。**是那次"修复"改坏的**(修 A 引入 B),所以 `6fd2e8e`/`9567545`/`301ed27` 都没修到它——那几次都在改"名字/偏好/显存"的**匹配质量**,没人发现编号空间被用了两次。

`EsrganOnnxService` 侧**没有**这个问题(它 `:292-293` 解析一次、`:303` 直接 `AppendExecutionProvider_DML(dmDevice)`),**只有补帧这条路错**。

### 修法

统一编号空间,**只允许解析一次**:
1. `RifeOnnxService.cs:43`:形参改名 `dmlDevice`,直接 `opts.AppendExecutionProvider_DML(dmlDevice)`,**删掉第二次 `ResolveDmlDevice`**;
2. 保留 `VideoService.cs:2280` 的解析(它负责"尊重用户 + 无效编号兜底");
3. 审 `RifeOnnxService.GetSession`/`Interp` 的调用方传的是哪个空间(它们也走 `BuildSession`);
4. 顺带规范化连击表的键空间(见 C-2)。

---

## 零之二、DXGI 枚举层**从未工作过** —— 上面所有"映射修复"都建立在一个空函数上(已本机实测证明)

### 实测数据(我在本机写了一个独立探针直接调 vtable 槽位)

```
CreateDXGIFactory1 hr=0x00000000 factory=ok
  factory slot 11 as EnumAdapters1(0): hr=0x887A0001 adapter=NULL      ← 失败
  factory slot 12 as EnumAdapters1(0): hr=0x00000000 adapter=ok        ← 成功
      adapter slot 9  as GetDesc1: hr=0x887A0004 Description=''        ← 失败
      adapter slot 10 as GetDesc1: hr=0x00000000 Description='NVIDIA GeForce RTX 4060 Laptop GPU'
```

**结论(实测,非推断):**
1. **`SlotEnumAdapters1 = 11` 是错的,应为 `12`**(11 是 `IDXGIFactory::CreateSoftwareAdapter(HMODULE, ...)`;传 module=0 正好返回 `0x887A0001` = `DXGI_ERROR_INVALID_CALL`)。
2. **`SlotGetDesc1 = 9` 是错的,应为 `10`**(9 是 `IDXGIAdapter::CheckInterfaceSupport`,返回 `0x887A0004` = `DXGI_ERROR_UNSUPPORTED`);slot 10 读出的是**真实显卡名**,且正是本开发机的 RTX 4060 Laptop —— 说明 **DXGI 枚举在本环境完全可用**。
3. `::317` 的槽位注释把 `IDXGIObject` 记成只有 3 个方法(漏了 **`GetPrivateData`**,真实占用 slot 5),所以从 `GetParent` 起**全部错位一位**。COM 声明同样错:`IDXGIAdapter1`(`::272-273`)把 `GetDesc`/`EnumOutputs` **写反**、末尾还多了一个不属于该接口的 `GetDevice`;`IDXGIFactory1`(`::278-289`)漏了 `CreateSwapChain`/`CreateSoftwareAdapter` 两个占位,于是 `EnumAdapters1` 落在 slot 10 = `CreateSwapChain` —— 调用点传 8 字节栈槽当约 100 字节的 `DXGI_SWAP_CHAIN_DESC` 用,属**跨签名 UB**。`IDXGIFactory6`(`::383-421`)同样漏 `GetPrivateData`。

### 我必须纠正自己先前的判断(重要)

本会话早前我用一个独立 DXGI 探针测到 `EnumAdapters1(0)` 返回 `0x887A0001`,当时我**结论是"本沙箱禁止适配器枚举,DXGI 行为无法在本机验证"**。**那个结论是错的** —— 真正原因是**我自己也是用 slot 11 调的**,而 slot 11 根本不是 `EnumAdapters1`。两个"独立"路径给出同一个错误码,恰恰因为它们**犯了同一个槽位错误**。现在用 slot 12 一次就成功。抱歉,这条误判让你以为"必须去真机才能验证",实际本机就能验。

### 后果链(这才是最严重的地方)

`TryEnumerateDxgiAdapters()`(COM 首选 + vtable 兜底)**两条路都调错槽位 → 恒返回空表** → `ToDmlDevice`(`::119-181`)的 ① DXGI 名字匹配、①b 官方 GPU 偏好、①c 显存识别**三条全部走不到** → **每一次**都落到 ② 注册表序名匹配,也就是代码自己在 `::160` 标注的 **【风险路径】**。同理 `TryGetDxgiVramGb()` 恒 null → `SafeRender.ProbeTotalVramGb`(`SafeRender.cs:251`)那条"唯一跨厂商显存真值"整条失效,回落到注册表或保守 4.0GB。

**换句话说:这一整轮围绕"避开注册表序风险"做的所有工作(名字匹配/GPU 偏好/显存识别/三重保险)一行都没有真正执行过。** `DescribeDmlMapping()` 现在必然一直打印"(枚举失败)"——修好槽位后,它就是**一键验收手段**。

### 附带发现:手写的 `IDXGIFactory6` 是**崩溃隐患**,不是"优雅失败"

实测:`IDXGIFactory6` 上按 `EnumAdapterByGpuPreference` 签名调 slot 29 与 slot 27 → **进程直接 AccessViolation 终止(exit -1073741819)**,不是返回错误码;slot 28 返回 `0x887A0001` 未崩;slot 30 返回 `0x8007000E`(明显是读到了 vtable 之外的内存)。也就是说这张声明一旦**跨签名调对**,是会把整个进程带走的(`.NET` 的 `AccessViolationException` 默认不可 catch)。`TryGetAdapterIndexByGpuPreference`(`::429-482`)在 ① 名字匹配失败后**一定会被调到**,所以这是真实可达的崩溃面。

**建议:`IDXGIFactory6` 那段声明 + `TryGetAdapterIndexByGpuPreference` 整体删掉**,不要试图修槽位 —— ①修好 12/10 后名字匹配已经真正生效,GPU 偏好这条兜底的价值大幅下降;②它的收益远小于一次 UB 调用崩掉进程的风险。

### 与"第零节"的关系(两条缺陷叠加)

- 实测确定:`ToDmlDevice` **一直**会走注册表序匹配(`EngineService.cs:162-173`),返回值 = 注册表里同名卡的索引。
- 你的机器注册表序是 `[#0 AMD][#1 NVIDIA]`,引擎序是 `[0 RTX 5070 Ti][1 AMD Radeon]`。
- 补帧路径(`VideoService.cs:2280` → `CreateSessions` → `RifeOnnxService.cs:43`)把这个值**又映射了一次**,第二次会把"注册表号"当"引擎号"重解释。

两条缺陷都真实存在(第零节的二次映射已由 git diff 证明,本节的槽位错误已由实测证明),但**它们叠加后到底把会话建在哪张卡上,取决于 DXGI 真实枚举序** —— 而这个序恰恰是程序从来没有能力观测的。所以你机器上的最终表现,必须以**修好槽位后**新日志行 `GPU→DirectML 映射对照:` 为准,我不能再凭推断下结论。

**正确修复顺序:先修槽位(12/10)+ 删掉 IDXGIFactory6 → 看到真实 DXGI 序 → 再修二次映射 → 用日志验收。**

---

## 一、Critical(必须修)

### C-1 [ONNX] 超分输出无 NaN/Inf 防线 → 静默写出黑块
- `EsrganOnnxService.cs:850-863`(唯一落图点 `RunTile` 的量化循环);对照 RIFE 侧**有**防线 `RifeOnnxService.cs:374-376`
- NaN 经 `(int)Math.Round(b*255f)` 在 .NET 8/x64 得 `int.MinValue`,`Clamp(...,0,255)` 后 = 0 = **黑**;±Inf 得白/黑([.NET 9 浮点转整数饱和](https://learn.microsoft.com/en-us/DOTNET/core/compatibility/jit/9.0/fp-to-integer))。
- 违反硬约束「NaN/Inf 该帧作废」。**整帧全黑**在视频路径有判黑兜底(`VideoService.cs:1490-1505`),但**部分块变黑**(1080p = 12 块,个别 tile 溢出)不满足 `IsBlackPng` 的"≥95% 近黑"(`EngineService.cs:2054-2057`);**图片页路径完全无判黑兜底** → 黑块直接进成图/成片,日志无痕。
- 修:量化循环内 `float.IsNaN/IsInfinity` → `throw InvalidOperationException`,交给既有降级链(`:372` 源图缩放);并补 `dims[2]/[3]` 正负与上界校验(RIEF 侧 `:212` 已有)。

### C-2 [ONNX] 连击表的键混用两套编号空间 → 核显失败会误杀独显
- 记录/查询处:`EsrganOnnxService.cs:783/815/731`、`RifeOnnxService.cs:145/149/162/89/121`
- `RunTile` 侧记的键是**引擎编号**;RIFE 活路径传的是**已解析的 DML 号**(`VideoService.cs:2280→2326`)。两者共用 `_dmlStrikes`(`EsrganOnnxService.cs:94`)。
- 双卡机上:图片页在核显连吃 3 次失败 → 把"DML 设备 0"(=独显)标成不可用 → `RifeOnnxService.cs:89-92` 直接抛 → **剩余整段视频静默变成复制原帧**,而独显完全健康。单卡机同号所以现在看不出来——**换机器才炸**。
- 修:记录/查询入口统一用 `ResolveDmlDevice` 之后的 DML 号,或键改成 `(编号空间, 设备号)`。

### C-3 [ONNX] 会话池创建失败 → 静默整批落 CPU
- `EsrganOnnxService.cs:298-326`,零日志的入口是 `:325 catch { sessions[s] = null; }`
- 吞掉任何非持续性异常(OOM、`dmDevice<0` 的非法设备号、provider 注册失败)。随后 `:351-352` 该 worker 以 `auto ? -2 : gpuId` 调用 → `-2`;`RunTile` 里 `gpuId >= 0` 判定(`:724/:754`)全为假 → 建 **CPU 会话** → 该 worker 剩余全部帧跑 CPU。
- 且 `dmDevice` 本就可能是 -1:`ToDmlDevice` 双卡名字匹配失败时**故意返回 -1**(`EngineService.cs:176-178`)、`DmlFallbackOk` 未探测时也是 -1 → `AppendExecutionProvider_DML(-1)` 必然失败 → 3 路 × 240 帧全 CPU。这就是"设置里写 GPU、实际跑了一整夜"。
- 修:`wantGpu && dmDevice < 0` 直接抛;`catch` 至少记日志,且任一 worker 建会话失败即把本批降为 `concurrency=1` 并明确告警;绝不允许"GPU 声明 + CPU 会话"静默共存。

### C-4 [ONNX] CPU 会话被缓存进 GPU key → 本进程永久悄悄跑 CPU(已独立复核)
- `EsrganOnnxService.cs:753-759` + `:783`
- DML append 抛异常时只打一条 warning(`:757`),随后 `:759` 照旧建**纯 CPU 会话**,并被 `_sessions.GetOrAdd(key=(modelPath, gpuId))` **缓存到 GPU 键下**。之后同 key 全部命中它;`:783` 还在**CPU 推理成功后** `ClearDmlStrikes(gpuId)` → 设备级熔断**永远无法触发**,`DmlDeviceUnusable`/`AnyDmlDeviceUnusable` 一直报健康 → 视频/补帧路径持续重试那块死掉的卡。
- **可达性已核实**:`UpscaleView.xaml.cs:1280/1309/1346` 都不传 `sessionOverride`,正好走这条缓存路径。
- 与视频路径 `:306-308` 明令的"绝不静默建 CPU 会话"**自相矛盾**(那条路径还会对 `IsPersistentDeviceError` 重抛,这条不会)。
- 修:DML append 失败即抛,或落到独立 `(modelPath,-1)` 键并明确日志;`ToDmlDevice(gpuId) < 0` 时直接抛;`ClearDmlStrikes` 只在会话确实在 DML 上时才调。

### C-5 [视频] 黑帧防线用"存在量词" → 黑帧可以进成片(已亲自复核)
- `VideoService.cs:2753-2754`(`DirNearBlack`),调用点 `:1504` 与 `:2462-2463`
- 代码是 **"任一帧近黑即 return true"**(注释却自称"≥95% 像素"):`if (IsNearBlack(sums.ToArray(), total)) return true;` 在遍历循环内。
- 而 `:1504` 是 `(anyDefective || !anyFrame) && !DirNearBlack(batchIn)` → **一批 64 帧里只要有一帧是黑场(片头黑/淡入淡出/夜戏/闪黑),本批 GPU 输出的全部黑帧都被当成"素材本来如此"放行**,原样转 JPG 进输出,且 `ncnnUnreliable` 不置位 → 后续批次继续用坏引擎。
- 违反产品级硬约束,且失效时**完全静默**(无日志无提示,用户只看到成片某几秒全黑)。
- 修:`ConvertPngToJpg` 已逐帧返回 `isBlack`(`:1496-1497`)——把被判黑的帧名收集起来,回退前**只对这些帧**检查其对应源帧是否也近黑;退一步也要求 `DirNearBlack` ≥80% 帧近黑(并排除 0 字节帧)。

### C-6 [视频] `帧数 < 5 即无效` 把合法短成片判死,并删掉已编码结果 + 谎报原因(已亲自复核)
- `VideoService.cs:5130-5132`(`nb < 5` → `return false`),调用点 `:2064-2065`/`:2086-2087`,删除点 `:2105`(`File.Delete(outTmp)` 已核实存在)
- 该规则原始用途是**编码器探测**(`3120-3123` 注释"探测要多编几帧(≥5)"),但真实输出走同一个校验。
- 任何 ≤4 帧的成片(单帧视频、极短视频、裁出 <5 帧、`allowFewFrames=true` 用户确认"去重过强仍要进行"后的短输出)都被判无效 → 抛"视频合成失败:输出文件无效(无法被解码)" → **并把已经编码好的 `outTmp` 删掉**。用户既拿不到文件又拿到错误诊断,白等一整轮编码。
- 修:`ValidateVideoFileAsync` 加 `int minFrames = 1`;探测传 5(`:3123`),真实输出传 1(`:2064/2086`),只保留"存在、非 0 字节、ffprobe 能读出 video 流"。

### C-7 [视频UI] 跨线程读 XAML 控件 → 超4K 拦截**从未生效**(已亲自复核,解释"我从来没有见过这个弹窗")
- `VideoView.xaml.cs:3456`(`await Task.Run(...)`)内 `:3469` 读 `VideoScaleRadios.SelectedIndex`、`:3473-3474` 读 `CustomWidthBox/CustomHeightBox.Text`
- WinUI3 XAML 对象有线程亲和,后台访问抛 `RPC_E_WRONG_THREAD (0x8001010E)`。**本仓库自己真机复现过同一故障**:`VideoView.xaml.cs:3879`「留在后台线程会抛 0x8001010E(已真机复现:选 Real-ESRGAN 视频必崩)」。
- 异常被 `:3493 catch { }` 逐个吞掉 → `over4k` 恒空、`totalSec/totalNeedGB` 恒 0:
  - `:3499-3532` 的"输出超 4K"确认框**永不弹出**;
  - `:3542` 的**爆盘判断也永不触发**(`totalNeedGB > 0` 恒假);
  - `:3566` 诊断框若弹出会显示 `:3557-3558` 的"约 0 分钟 / 约 0 GB"**假数字**。
- **精确触发条件**:仅当 `upOn == true` 时 `:3469` 的左侧才被求值 → **"超分"开启时整段扫描静默失效**;而超分开启恰恰是超 4K 风险最需要提示的场景。超分关闭时只有源本身 >4K 才可能命中。
- **附带结论**:`:3497-3532` 这个弹窗同时违反你明确的要求「不要什么弹窗了…放在进度条旁边」,且 `RELEASE_NOTES.md:25` 公布的既定设计就是"不弹窗、改内联红字"。它现在是**坏掉且多余**的代码。
- 修:① 删除 `:3497-3532`(内联红字 `:3094-3095` 已在生效,满足你的要求);② 若要保留爆盘预检,把 `Task.Run` 前所需的所有 UI 值提升为局部变量后传入。

### C-8 [图片] 选区放大倍率整体错一档:选 2x 得到"完全不放大"的裁切
- `UpscaleView.xaml.cs:1045`(对照主流程正确写法 `:1163`)
- `ScaleRadios.SelectedIndex switch { 0 => 2, _ => ScaleRadios.SelectedIndex }` → 1(2x)=1、2(3x)=2、3(4x)=3。而 `EngineService.UpscaleAsync` 在 `scale<=1.001` 时 waifu2x/realesrgan 都是 **`File.Copy` 直接复制**(`EngineService.cs:1700-1707/1736-1740`)。
- 后果:选 2x 点「放大选区」得到 1:1 裁切(文件名还写 `_选区放大1x_`,`:1052`),3x 得 2x、4x 得 3x——而它正是用户判断超分效果的样张。
- 修:改成 `{1=>2, 2=>3, 3=>4, _=>2}`,并把倍率映射抽成一个方法给两处共用。

### C-9 [图片] ONNX 分支丢弃 1x超分/降噪/TTA/码率,且 JPG 里可能是 PNG 字节
- `UpscaleView.xaml.cs:1296-1311`(丢弃在 `:1308-1310`)
- ONNX 调用缺 `upscaleShrink1x`/`noise`/`tta`/`jpgQuality`/`pngCompress`;`EsrganOnnxService.RunCore` 只按 `ow=sw*scale` 出图(`:429`),waifu2x ONNX 是 2x 权重,不符时用**双三次** `SaveScaled`(`:978-987`);所有出口都是 `ImageFormat.Png`(`:457/503/544/675/986`)。
- 后果(50 系/Blackwell 或用户选 CPU 时**自动命中**,用户不知情):
  1. 选「1x 超分」实际输出 **2x**,而 `RefreshOutSpec`(`:949-950`)正承诺输出=源尺寸;
  2. 「降噪级别」「高级 TTA」完全无效(TTA 还弹了"时间增加 2~3 倍"的警告);
  3. 「4x」= 2x 神经 + 2x 双三次(ncnn 是级联);
  4. 选 JPG 且增强滑条全为 0 时,`.jpg` 里装的是 PNG 字节(`EngineService.cs:3310` 自己承认过这个坑)。
- 修:加 `upscaleShrink1x`;对 `noise/tta` 显式告知"当前引擎不支持,已忽略";ONNX 出图后无条件按扩展名收尾重编码。

### C-10 [图片] 倍率存读 off-by-one:4x 重启变 3x
- `UpscaleView.xaml.cs:177-181`(读)/`:248`(写)/`:409`(预设)
- 写的是新语义索引(0..3),读的却按"旧版五项"映射 `Math.Min(d.Scale - 1, 3)` → 存 1→读 0、存 2→读 1、存 3→读 2。内置预设恰好按旧语义书写,所以看起来正常,掩盖了问题。
- 后果:4x→重启变 3x;2x→变 1x(叠加 C-9 就是 2x 输出)。输出分辨率被静默改变。
- 修:读侧 `Math.Clamp(d.Scale, 0, 3)`,旧文件迁移用独立版本号字段,别用同一字段双语义。

### C-11 [抠图] 构造期就用默认值覆盖用户设置 →「记住上次」永久失效
- `CutoutView.xaml.cs:41`/`:55`,配合 `CutoutView.xaml:133-134`
- XAML 里 `AutoThresholdCheck IsChecked="True"` 在 `InitializeComponent()` 解析时就触发 `Params_Changed → OnParamsChanged → SaveSettings`;此时 `_suppressEvents` 仍是 false(`:55` 才置 true),且 `FgVal` 先于该 CheckBox 生成,`if (FgVal == null) return;` 拦不住。
- 后果:**每次启动**都用默认值(含 `Remember=false`、`OutDir=""`)重写 `cutout-settings.json`,紧接着 `LoadSettings()`(`:61`)读到的就是这份默认值 → `Remember=false` 直接 `return`(`:263`)→ 用户的前景/背景/模型/输出目录**永远恢复不了**。这就是"记住参数失效"的真因(注释 `:53-54` 声称已修,位置放晚了)。
- 修:`private bool _suppressEvents = true;` 字段初始化(或 `InitializeComponent()` 之前赋值),`LoadSettings()` 返回后再置 false。

### C-12 [抠图] `_running = true` 之后有未保护路径 → async void 崩溃 + 界面永久卡死
- `CutoutView.xaml.cs:698`(置位)对比 `:715-726`(裸调用)
- `Path.Combine` + `Directory.CreateDirectory`(`:720-721`)在 try 之外,且 `OutDirBox_TextChanged` 对任意文本都赋给 `_customOutDir`(`:671-676`,加载时却有 `:276` 校验)。路径非法/只读/盘满即抛。
- 后果:异常从 `async void` 逃逸 → **进程崩溃**;即使不崩,`_running` 已 true 而 `finally`(`:907`)在更内侧 try 里永不执行 → 列表锁死、RunBtn 永久灰。`UpscaleView.xaml.cs:1114-1132` 是正确写法(注释明说是为修这个 bug),抠图页漏了。
- 修:目录创建 + 路径合法性校验移到 `_running = true` 之前,失败提示已知错误并 return;校验 `_customOutDir` 非法字符。

### C-13 [抠图] 选区放大裁剪坐标不做 EXIF 方向标准化 → 手机竖拍图裁错区域
- `UpscaleView.xaml.cs:1066`(配合 `ImageToolGrid.xaml.cs:680-682`/`:128-136`)
- 框选坐标来自预览显示空间(`BitmapImage` 走 WIC 默认 `RespectExifOrientation`),而 `EngineService.UpscaleRegionAsync` 内部是 `new System.Drawing.Bitmap(input)`(`EngineService.cs:3346`)——**System.Drawing 不应用 EXIF**(仓库自己在 `CutoutService.cs:876` 写明)。
- 后果:EXIF 6/8/3 的照片框选区域与真正裁剪区域不一致(转置/镜像),轻则裁错、重则被 `Math.Clamp` 夹到边缘。抠图页专门做了 `NormalizeExif`(`CutoutView.xaml.cs:820`),超分页整页都没做。
- 修:选区放大前 `NormalizeExif`,用返回路径裁剪并清理临时文件。

---

## 二、Important(应该修)

### 设备与编号
- **I-1** 音频"选卡"静默落 CPU 且**零日志**:`AudioEnhanceService.cs:81` 的 catch 是空注释,`AudioView.xaml.cs:875` 传原始 `AppSettings.GpuIndex`;`ToDmlDevice` 在"设备表 ≤1 项"或"编号不在表中"时**原样返回**(`EngineService.cs:125-127`),失效编号直达 `AppendExecutionProvider_DML` → 抛 → 被吞 → CPU,而 `_gpuSession` 这个名字在骗人。修:改走 `ResolveDmlDevice` 并记日志。
  *(同类写法 `CutoutService.cs:270-271` 目前是**不可达死代码**——所有调用方都传 `CutoutGpuId = -1`(`CutoutView.xaml.cs:175`),但仍是潜在陷阱。)*
- **I-2** `RifeOnnxService.cs:43` 的设备号二次映射(=第零节,见上)。
- **I-3** 音频不查 `DmlDeviceDead`、也不对 `GpuFault.IsPersistentDeviceError` 短路(视频侧有 5 处 `TripDmlDead`),设备被摘除后音频还会多试最多 2 个分块才落 CPU。
- **I-4** 异常路径会话泄漏:`RifeOnnxService.cs:72-78` 中途抛则已建会话全泄漏,且 `VideoService.cs:2288-2290` 失败后**再调一次**、泄漏翻倍;`EsrganOnnxService.cs:319-324` 重抛时外层 `try/finally`(`:327/:402`)尚未开始,`sessions[0..s-1]` 不会被 Dispose。修:`catch { 逐个 Dispose 已建的; throw; }`。

### 视频流水线
- **I-5** 手动-帧差+SSIM 漏传 `motionComp`,静默用默认 `true`,UI 明确传的 `false` 被忽略(`VideoService.cs:3556` vs 调用 `:657-661`;UI `VideoView.xaml.cs:4349` 硬编码 false)。后果:多删"背景 pan + 主体定格"帧,且 `EstimateGlobalShift`(289 位移 × 全采样图,**每对相邻帧**都跑)在长视频上是分钟级浪费。修:调用处补 `motionComp: motionCompDedup`。
- **I-6** 去重报告帧数被**重复累加一次**(复制粘贴 bug):`VideoService.cs:664` 与 `:672` 同一 `dropM` 被 `AddRange` 两次(`:670-680` 内层 `if` 恒真、`else` 是死代码可反证)。用户看到的"删 N 帧 / X→Y 帧"是错的(2×),且 `:893-897` 的"最集中在"时间区间最多偏 2 倍。`LastDedupShort` 正是 UI 里那行蓝色小字(`VideoView.xaml.cs:4390-4393`)。修:删 `:664`。
- **I-7** 回退帧尺寸按 `Math.Round(scale)` 猜(`VideoService.cs:2787-2788`):引擎实际倍数不是 `scale`(`EngineService.cs:2164-2167` 取 2 的幂/`Ceil`,再按 `ratio` 精确缩放)→ 1.5x 时回退帧是 2× 而最终输出是 1.5×。混排分辨率时 ffmpeg 按第一帧声明流头,**退出码 0、不报错、日志无痕,成片某些秒花屏**——正是 `:2762-2765` 自己警告的场景。修:复用同一套 `engineScale + scale/engineScale` 计算(抽成共享纯函数)。
- **I-8** 帧号缺口会被 image2 截断,而"回填 `continue` + 0 字节 JPG"正好造缺口:`:1569`(`!File.Exists(repJpg)` → `continue`)让该组**全部重复槽位一个文件都没有** → 断号 → 成片从该点被截断。修:改为对 `g.slots` 全槽位 `WriteFallbackFrame`;合帧前加"帧号连续性 + 文件长度 > 0"自检。
- **I-9** 超分进度两套口径混用(`:1399-1404` 前缀和 vs `:1584-1586` 已完成数):3 路并发时实测 0→64→128(启动上报)后首个批次完成报 64 → **进度从 128 掉回 64**,ETA 偏乐观。修:统一单调钳制,或不让 `batchStartSlot` 参与百分比/ETA。
- **I-10** VFR 素材在"智能/动漫/手动-内容帧率"网格去重下时间轴保真被**静默关闭**(`:561/:592/:612` 三条网格路径都 `frameDurs = null` → `:1889-1896` 退化成均匀表):**总时长对得上,节奏对不上**(VFR 被拉平成 CFR),而用户勾的正是"按原节奏处理"。修:`finalDurs[i] = frameDurs[idx[i]]`(`keep/tempoSrcIdx` 已有)。
- **I-11** 手动-重复帧(mpdecimate)模式的"韵律源帧/补回"是**死路径**(已用本机 ffmpeg 实测:`mpdecimate,…,metadata=print` 输出**零行**;同素材 `showinfo` 有 40 帧)→ 恒 `idx.Count < 2` → 永远走 `:698-699` 回退,节奏还原能力等于零。修:改用 `showinfo`。
- **I-12** 临时盘预估**系统性高估 → 合法任务被直接拒绝**(`:370-400`,`needBytes = peakFrames × outFrameMB × 1.6`,硬 throw)。`outFrameMB` 用**超分后**尺寸却乘**全部补帧帧数**,但实际是"先补帧→再超分"、超分帧与补帧帧只在超分过程短暂共存(批内即删)。1080p/10min/2x超分/2x补帧:预估 ≈57GB vs 实际峰值 ≈20GB → 剩余 30~50GB 的盘被拒掉本可跑完的任务,且无法"继续"。修:按阶段分别估峰值;硬拒绝只留"单批都放不下"。

### 视频 UI / 体验(你明确反感的冗余与弹窗)
- **I-13** **临时帧占用公式与处理端不一致,预览最多低估 5.6 倍**:预览 `VideoView.xaml.cs:3068-3073` 用 `1.0*ow*oh/1080p*0.18` 且 `dur` 未扣裁剪;处理端 `VideoService.cs:376-381` 用 `max(srcFrameMB, srcFrameMB*outMult²*0.18)`。**4K 源只补帧**:预览 0.72MB vs 实际 4.0MB。用户按预览清盘,跑到一半被 `VideoService.cs:397-400` 中止。直接违反「预览=结果」。修:抽一个共用静态函数。
- **I-14** 去重(不补帧)时**预览帧率是错的**:`:3031` `interp=false` 时用源帧率,实际 `VideoService.cs:1740-1742` 用的是**去重后的内容帧率**(30fps 去重到 12fps,预览写 30fps,成品 12fps)。
- **I-15** 单视频 + 补帧时参数区写"输出帧率 = 输入帧率"(自相矛盾):`:983-985` 只在 `fpsModeNow==2` 读 `InputFpsBox`,而该选择器是多视频专属(`:877` 单视频折叠)→ `inFps` 恒 0。同屏与 `:3051-3054` 的"60fps(2x补帧)"打架;`:1000-1006` 的"倍率不够/低于输入帧率"预警在单视频下**永不出现**(最该提示的情况)。
- **I-16** 预设窗口排序后"应用预设"套用的是**另一个预设**:`:1656-1665` 按 `sortCombo` 排序填充,`:1769-1773` 却用**未排序**的 `LoadPresets()` + `ResolveSortedIndex`,而 `:1810-1811` 的 `ResolveSortedIndex` 是 `=> showIdx` **空壳**。切"按名字/最近修改"后点第 1 行 → 全套参数(超分/补帧/去重/后处理)错配,用户很难察觉。且 `:1894-1908` 在预设窗口未关时就弹确认,被 ContentDialog 单例限制 + `:1974 catch{}` 吞掉,用户连"应用了哪个预设"都看不到。
- **I-17** **弹窗泛滥,且与内联提示重复**(你明确要求"不要什么弹窗"):
  - `:3499-3532` 超4K 阻塞弹窗 → **坏掉且多余**(见 C-7),且 `:3094` 已有内联红字、`RELEASE_NOTES.md:25` 公布的设计就是"不弹窗";
  - `:3566-3593` "开始前诊断"弹窗 与 `VideoView.xaml:722-727` 内联 `CompatHintPanel` 说的是同一件事,weakDevice 时**每次开始都弹**;
  - `:3670-3699` "高倍率补帧提醒"是同一份信息的**第三次**出现;
  - `:4520` + `:4569-4596` 完成弹窗 + 紧接着的赞助弹窗(**同一时刻两个弹窗**);信息已在 `TaskSummary`/日志/列表项里。
  - 建议:只留内联(输出规格行 + 进度条旁"打开输出文件夹"按钮)。
- **I-18** **冗余文案**(同一信息反复出现,具体位置):
  - `VideoView.xaml:216-218` 长段模型说明 vs `VideoView.xaml.cs:838-845`(每次改参数都用等价长句覆盖前者)+ 下拉框 `:206` tooltip = **同一段话三遍**;
  - TTA:`:583` tooltip 与 `:585` 黄字重复;
  - 兼容模式:`:675` tooltip + `:677-678`(`FastModeHint`,又被 `:952-954` 替换成 100+ 字版本)+ `:541-583` = **三处**;
  - 去重/转场:`:532-533` vs `:339/341-343`/`:535`,外加 `:334-337` 区块的"去重模型""去重"两个标题;
  - VFR:`:329-331` vs `:321-322` vs `:226-227`;
  - 帧率:`:222-223` + `:233-235` + `:891-896`;
  - 果冻/耗时警告:`:574-576` 与 `:532-533` 两条黄字同屏;
  - `:714-716/718-720/722-727` 四个提示块挤在"开始处理"上方。
- **I-19** **临时盘不可用警告每批刷屏**(3070 诊断包里 >100 行)。
- **I-20** 诊断扫描期间**无法取消/暂停**:`:3752-3769` 置 `_running=true` 禁用 RunBtn,而 `_cts` 直到 `:3871` 才创建(`CancelBtn_Click:4603` 是 `_cts?.Cancel()` → 空操作)。任一 probe 卡住(休眠盘/网络盘/坏文件)用户**唯一出路是杀进程**。
- **I-21** 每次参数变化都用 fire-and-forget 重跑**两个** ffprobe(`:1029` + `:647-651` + `:2995/3016`),无序号守卫 → 拖滑条每秒几十个进程,且旧结果可覆盖新结果(显示过期规格)。
- **I-22** 日志**无条件滚到底**(`:2188`、`:4076`):用户上翻看历史时任何一条日志/进度都把他拽回底部。
- **I-23** 设置恢复越界会**静默中断后续全部恢复**:`:1504-1507`(`DedupModelCombo` 仅 3 项却允许 0..5、`DedupAnimeCombo` 仅 5 项却允许 0..6,旧字段枚举范围更大),异常被 `:1430-1433` 捕获后 `ApplyVideoParams` 从该行起整体中断 → 用户只看到"记住上次没生效"。与 `:1484-1491` 逐个查 `Items.Count` 的写法不一致本身就是证据。
- **I-24** 多个 `async void` 事件处理器无 try/catch(`:3335/3350/3377/2947/2599/2913` + `:1798-1805`):异常由 `App.xaml.cs:327-360` 兜底 → 用户看到"程序遇到问题"弹窗 + 半完成状态。修:统一包 try/catch + 日志。
- **I-25** WebP/HEIC **可被重复添加**(`ImageToolGrid.xaml.cs:95`+`:154`+`:109`):转码产物走 `UniquePath` 每次新路径,而去重按**转码后路径**比较 → 同一张 `.webp` 加两次得到两个条目 + 私有目录残留多份 PNG。修:改比 `OriginalPath`。
- **I-26** 大目录添加是 **O(N²) 且无进度反馈**(`ImageToolGrid.xaml.cs:62`+`:143`+`:194-219`+`:123-137`):每次 `Items.Add` 触发全量 `UpdateListState`,且每张图全图解码 → 数百张时界面"像没反应"。
- **I-27** 缩略图/裁剪预览按**原图尺寸解码**(`ImageToolGrid.xaml.cs:142`/`:680-683`,未设 `DecodePixelWidth`)→ 数百张大图内存暴涨。修:`DecodePixelWidth = 128`。

### 图片/抠图
- **I-28** 「抠图前降噪 / 抠图前超分」**不进预览**(`CutoutView.xaml.cs:559-563` 只用 `item.Path`+阈值,对比 `:826-843` 导出前会跑)→ 预览≠结果。
- **I-29** 彩色预览在大图上被裁剪/比例失真(`CutoutView.xaml.cs:482`/`:508-510`):棋盘底图各边**独立**取 `min(4096,边长)`,而 ffmpeg `overlay` 不缩放叠加层 → 8000×4000 被压成 4096×4000 且只显示左上角。修:按 `s = min(1, 4096/max(w,h))` 等比缩放。
- **I-30** 抠图页"手动输入输出目录"**完全无效**(`CutoutView.xaml:50-52` 缺 `TextChanged`,而 `CutoutView.xaml.cs:671` 已实现该方法)。
- **I-31** 抠图完成弹窗**不报失败数**(`:891`/`:988-1010`,`failCount` 只进日志):批量 5 张失败 3 张时只说"已处理 2 张图片"(超分页 `:1646-1654` 专门做过失败可见性)。
- **I-32** 内存上限过宽 + 同一张图多次全尺寸解码(`CutoutService.cs:861-870` 单边≤16384、≤1.6 亿像素;`:98` 与 `:183` 解码两次;`:881` EXIF 探测又开一次 `new Bitmap`;`:1059-1095` `SaveWithAlpha` 再分配 w×h 32bpp + 源副本)→ 1.6 亿像素时峰值 ≈2.5GB,8GB 机在推理跑了几十秒后才 OOM。
- **I-33** `ExtractMask` 对 <3 维输出取 `tensor[0]`(`CutoutService.cs:410-415`)→ 整图同一个值 → 全透明或全不透明,且**静默出错**(配合 `:396` 的 `named ??= results.Last()` 兜底)。
- **I-34** 前后景阈值 `fg<=bg` 时静默退化(`CutoutService.cs:457`/`:545-555`):被强行 `fg=bg+0.01f`,分母变 0.01 → 背景阈值拉到 255 得到**全透明**,用户只看到"抠没了"而无任何提示。
- **I-35** 非法自定义名让**整批中断**(`UpscaleView.xaml.cs:1246-1252` 在单张 try 之外;抠图页同构 `:809-812`):`item.CustomName` 直接进 `Path.Combine` 无字符过滤(仓库已有 `SafePresetFileName` 却没用在这里)→ 一张图改名含 `:` 或表情符 → 抛 `ArgumentException` → 整批终止而非单张失败继续。
- **I-36** 掩码预处理用**双线性而非 LANCZOS**(`CutoutService.cs:301` `new Bitmap(bmp, new Size(size,size))` 走 GDI+ 默认 Bilinear):4000px→1024 属欠采样,细结构(头发/网线/栅栏)产生摩尔纹进入 1024² 输入 → 高分辨率原图边缘判断变差。注释声称对齐 rembg 的 LANCZOS,实际不是。
- **I-37** 选区放大不套增强/不按所选格式输出(`UpscaleView.xaml.cs:1051-1052`/`:1066-1067`/`:1392-1421`,固定 `.png`、不走 `EnhanceImage`)→ 不能当导出的样张;且**没有主流程的 ONNX 兜底与黑块重试**(主流程有 `:1296-1351`,选区直连 ncnn——而 50 系恰是会崩/出黑块的设备)。
- **I-38** 整批超分不做 EXIF 标准化且 OutSpec 用转置尺寸(`UpscaleView.xaml.cs:1264`/`:946-950`)→ 竖拍手机照超分后在资源管理器里**横躺**,提示的宽×高与实际相反。
- **I-39** JPG 码率在「1x 超分」模式下被忽略(`UpscaleView.xaml.cs:1162-1163` → `EngineService.cs:3314` `SaveJpegViaWinRT` 默认 `quality=0.92f`;1x 分支 `return` 前不再调 `EnsureFinalOutput`)。
- **I-40** `_lastJpgQuality` 不参与恢复(`UpscaleView.xaml.cs:188-209`):设置里存着真实档位(`:267`)却只在 `Fmt==JPG` 时读回下拉 → 设"超高(3)"后以 PNG 退出,下次启动切 JPG 变"默认(2)"并**落盘覆盖**(正是"记不住码率")。

---

## 三、应用内「更新历史」错乱 + 线上自相矛盾(已亲自核实)

- `release_history.json` 最新一条是 **v1.3.0**:1.3.1/1.3.2/1.3.3 **完全没有条目**,下拉从 1.3.4 直接跳到 1.3.0。
- `MainPage.xaml.cs:1166-1169` 把**整份** `RELEASE_NOTES.md`(含全部历史版本)塞进"当前版本"条目(`CleanNotes` `:1139-1155` 只清 Markdown、**从不截断**)→ v1.3.0/v1.2.x 的正文**重复出现两次**(既在"当前版本"里,又有自己的条目)。你厌恶冗余文案。
- **线上矛盾(已核实)**:`hint/hint1.json` 与 `origin/main` **无差异(已推送)**,文案是"当前最新版为1.3.4",但 **GitHub 上并不存在 v1.3.4 release** → 更新检查判定"已是最新",而它的下载按钮指向 v1.3.3。≤1.3.3 的用户**同时**收到"该升到 1.3.4"和"你已是最新"。
- 另:`installer.iss`/`installer_full.iss`/`installer_nocut.iss` 的 `ModelsUrl` 都指向不存在的 `v1.3.4/models_v1.0.zip` → 安装包"下载模型"会 404(你已知,发布前需处理)。

---

## 四、ETA / 经验库口径错误

- `VideoView.xaml.cs:4501-4514`:`taskSpan.TotalSeconds` 含拆帧+去重+补帧+超分+后处理+**编码+封装+ffprobe**,却按"纯每帧推理成本"记账(`÷ totalFramesEst`);且 `avgAreaN` 用各视频面积的**算术平均**而非按帧数加权 → 混合分辨率批次算错。短片段(固定开销占主导)会写入严重偏大的"秒/帧",下一轮初始 ETA 又按 50% 权重采用它 → **"预计剩余时间"系统性偏大**。修:只记推理段耗时(或扣掉固定开销),面积用 Σ(framesᵢ·areaNᵢ)/Σframesᵢ。
- `PerfMemory.cs:65-73` **无锁读字典**而 `Record` 在锁内写(`:83-93`)→ `Dictionary` 非并发读写,异常被吞后校准静默失效。修:加锁或用 `ConcurrentDictionary`。

## 五、次要

- **M-1** 共享 HttpClient 的 User-Agent 每请求累加:`TipFetcher.cs:88`、`AdFetcher.cs:134` 对**共享静态** `_http` 反复 `DefaultRequestHeaders.UserAgent.ParseAdd(...)`——该 API 是"追加"不是"设置",UA 会变成 `ALHPro/1.3.4 ALHPro/1.3.4 …` 不断增长(10 分钟轮询 × 最多 20 个 hint × 4 个端点),浪费带宽并有超长头被拒风险。修:静态初始化时设置一次。(`UpdateChecker.cs:57` 每次新建 HttpClient,无此问题。)
- **M-2** `VideoService.cs:2117-2122` 输出校验比较 `outFps` 而非最终标称 `frBase`(`:1966-1990`)→ 帧率保险生效后**必然误报 ⚠**。
- **M-3** `VideoService.cs:2125-2129`:`Report((100,"完成 ⚠ …"))` 紧接着 `Report((100,"完成"))`,UI 只节流 <99% → **告警被覆盖,用户永远看不到输出异常**。
- **M-4** `VideoService.cs:3787` 的 `imgup_rhythm_*.raw` 全仓库**无删除语句** → 每次入列预估在临时目录留 ≈0.3MB,长期累积。
- **M-5** `VideoService.cs:410-415` `workDir` 三连 `CreateDirectory` 在 `:417` 的 try 之前,满盘抛错时残留空目录。
- **M-6** `VideoService.cs:2309` 每对帧都调 `InterpFraming.StartIndex`(内部是 `for i<p` 累加)→ O(pairs²),1.8 万对 ≈1.6 亿次加法。改一次前缀数组。
- **M-7** `VideoService.cs:313-314` 把入参覆盖为 `FfmpegSafePath(...)` 并 `return`:`PathUtil.cs:27-35` 对**尚不存在**的输出只能缩短父目录(文件名仍中文)→ 返回给 UI 是 DOS 短名(`C:\USERS\XIAOHU~1\…`)且未真正规避 GBK 问题。建议只对传给 ffmpeg 的字符串短路径化。
- **M-8** 死代码约 250 行:`VideoService.cs:2192/2223/2795/3672/4773/4925/2617` 全项目无调用;`dedupMode == 4/5` 与 `:795` 的 `0.005` 分支不可达(`VideoView.xaml.cs:3843` 只产出 1/2/3)。
- **M-9** `VideoView.xaml:58-64` 只有 4 项、**无"自定义分辨率"档**,而 `VideoView.xaml.cs:805/2997-3007/3447-3448/3801-3813/1460-1462/812` 全在处理 `SelectedIndex==4` → 整条 `CustomSizePanel` 是**不可达死代码**(这也是 C-7 里 `CustomWidthBox` 永不被读到的原因)。
- **M-10** 过期/矛盾 tooltip:`VideoView.xaml:744-747`(CancelBtn 说"休息时变跳过休息",`SafeRender.cs:605-612` 已改)、`:735-738`(暂停说"处理完当前视频后停下",实际 `:619` 是**立即冻结子进程**)。
- **M-11** `CutoutView.xaml.cs:786` 日志断言"本功能强制使用 CPU…设置里选 GPU 对抠图无效",但预处理降噪/超分用的是 `CurrentGpuId`(可能走 GPU)——会被用户当真的错误信息。
- **M-12** `ImageInfoService.cs`:① `ReadColorSpace`(`:67-99`)只解析 PNG,非 PNG 一律返回 `"未标记 (默认 sRGB)"` → 带 Adobe RGB ICC 的 JPEG 被**错误断言**;② `:83` `int len` 由 4 字节裸拼,`lenBuf[0]≥0x80` 变负数 → `Seek` 反向可能死循环(加 `len<0 || len>fs.Length` 守卫);③ `AverageRgb`(`:104-116`)用 `GetPixel` 且**忽略 alpha** → 抠图透明 PNG 把透明区(常为黑)算进平均色,面板 RGB 偏暗;同一文件被打开 3~4 次。
- **M-13** `UpscaleView.xaml.cs:1423-1424` `File.Move` 失败被吞 → "✓ 完成"的成品留在 `TempRoot`(`imgup_proc_<guid>`),弹窗与"打开输出文件夹"指向临时目录怪名文件,且不被 `App.xaml.cs:141-163` 清理(只清 `imgup*` 目录)→ 长期残留。
- **M-14** `CutoutService.cs:255` `_rawMaskCache` 已无任何读写(死字段);`:584/622-790` `ApplyScribbles` 是死代码(其中 `:627-629` 的 24MP `throw` 永不触发,`:789` 还会无条件 `GaussianBlur(radius=5)`)。
- **M-15** `VideoView.xaml.cs:4472` 无条件 `VideoProgress.Value=100`,即使全批失败也满格;取消/失败项保留半截进度值。
- **M-16** `VideoView` 无 `Unloaded` 处理,而 `MainPage.xaml.cs:1057-1078` 切页只是把视图从 `ContentRoot` 摘掉(实例缓存)→ 切页后视频任务继续跑、进度写已卸载控件,用户看不到也无法取消。

---

## 六、已核实为"做对了"的部分(别改)

- **补帧并行 worker↔session 严格 1:1**(`EsrganOnnxService.cs:295-296/337-343/351`,`RifeOnnxService.cs:72-78` + `VideoService.cs:2291`),符合 DirectML 非线程安全要求。
- **"先熔断、后早退"顺序正确**(`EsrganOnnxService.cs:785-804`),`sessionOverride` 早退分支之前先 `TripDmlDead`——这个顺序是硬约束成立的关键。
- **批次异常协调**避免"设备故障被当用户取消"(`:330-336/363-365/397-400`),`Volatile`/`Interlocked` 用法正确。
- **降级输出尺寸纪律**(`EsrganOnnxService.cs:410-422` 按 源×scale 精确缩放而非直接 Copy)、`RifeOnnxService.cs:374-376` 的 NaN 源头拦截。
- **音频/视频熔断分表是彻底的**:3 个音频控制点(`AudioEnhanceService.cs:72/132/140`)全传 `DmlDomain.Audio`,且音频从不设置进程级 `_dmlDead` → "音频毒化视频"(B2)不会再发生。
- **三个页面的 `CurrentGpuId` 实现一致**(`VideoView.xaml.cs:461-475`、`UpscaleView.xaml.cs:693-707`、`CutoutView.xaml.cs:157-171`),都是"尊重用户 + 无效编号兜底推荐",无强制纠正。
- **视频页状态机/取消/进度单调/参数快照**扎实:`:3611-3612` 早退、`:3752-3754` 在任何 await 前置位、`:4529-4548` finally 全量复位;暂停/取消用 `NtSuspendProcess` 且取消时**先解冻再 Kill**(`VideoService.cs:5326/5491`)不会挂死。
- **临时盘生命周期管理**(`VideoService.cs:360-409`、`:1200-1213`、`:1593-1629`、`:2131-2138`):补帧后立即删、每批完成即删、finally 兜底删,长视频峰值从"几十 GB 全量并存"压到"分批同屏"。
- **时长/帧数自愈**(`:1800-1920`、`:1963-1990`):`(真帧数-1)×倍率+1` 对齐帧数 + setpts 铺 VFR + `realFps = fcFinal/muxDur` 折回帧率,让成片时长与源容器分毫对齐。
- **抠图 alpha 数学干净**(`CutoutService.cs:1059-1095` 正确的 **straight alpha**,RGB 一字不动、DPI 继承源图)、`:430/545-555/592-603/1087` 全链路无溢出负值;**预览与导出共用同一 `RunCore`**(`CutoutView.xaml.cs:544-588` vs `844-848`)。
- **超分"同帧只算一次"**(`VideoService.cs:1315-1380`/`:1558-1584`):先按文件长度分组再组内 SHA-256,重复槽位强制同批;哈希空串明确按"无重复"处理。
- **超分/增强"先写临时路径、整张完成才 Move"**(`UpscaleView.xaml.cs:1249-1252` + `:1422-1424`)→ 输出目录不会出现半成品。

---

## 七、建议修复顺序

**立即(正确性 / 你最痛的体验):**
1. **第零节 + C-2 + C-4** —— 设备号只解析一次 + 编号空间规范化 + 不让 CPU 会话占 GPU 键(一个 PR 内做,互相咬合)
2. **C-5**(黑帧防线改成"对应帧")+ **C-6**(`minFrames` 参数化)
3. **C-3**(会话池失败不静默落 CPU)
4. **C-1**(超分输出 NaN/Inf 防线)

**紧随其后(用户直接看得到):**
5. **C-7**(删掉坏掉的超4K弹窗,保留内联红字)+ **I-17**(删重复弹窗)+ **I-18**(去重文案)——一次满足你的三条要求
6. **I-13 / I-14 / I-15**(预览=结果三处失真)
7. **C-11 / C-12**(抠图页:记住上次 + 崩溃卡死)
8. **C-8 / C-10 / C-9**(超分页:选区倍率、存读 off-by-one、ONNX 分支丢参数)
9. 第三节(更新历史/版本一致性)

**同一轮带上:** I-5、I-6、I-7(视频正确性三小改),I-1、I-3、I-4(设备与泄漏),I-16(预设错配),I-20、I-21、I-22(卡死与打断)。

**随手清:** 第四节 ETA、第五节的死代码与文案/工具提示、M-4 临时残留。

---

## 八、核心算法层(AlhPro.Core + AlhPro.Tests)

**Critical**
- **被漏掉的最高频设备挂起文案**:`GpuFault.cs:27-37` 收了 HRESULT 码、`DEVICE_REMOVED/HUNG`、`DmlCommandRecorder`、中文本地化,但**没有** D3D12 运行时的标准原文 `The GPU device instance has been suspended`(及 `GetDeviceRemovedReason`)——这条文案里不含 `0x887A0005`,所以**不命中任何现有规则**。后果就是本函数注释自述的那个场景:调用方逐帧重试必然失败的 DML 调用、每次失败新建 CPU 会话,一段视频几小时(`VideoService.cs:2326-2339` 每对帧重试一次;8 万对 = 8 万次)。修:补两条 `Contains` + **同时按数值判** `(uint)e.HResult is 0x887A0005u or 0x887A0006u or 0x887A0007u or 0x887A0020u`,并加测试用例。
- **`BuildVfrSetptsExpr` 遇非有限时长永久死循环 + 无界扩张**:`VideoPipeline.cs:84-91` 内层 `while (i < durs.Count && Math.Abs(durs[i] - d) < 1e-5) i++;` 在 `d = NaN/±Inf` 时**永不推进**(`Math.Abs(NaN) < 1e-5` 恒假),`segs` 无界增长直到 OOM;`:92-93` 的段数上限判断在循环**之后**,永远到不了;`:106` 的 `catch` 也拦不住(不是异常)。**现状不可达**(`BuildFrameDurationsAsync` 有 `Math.Max(0.0005, diff)`),但这是契约漏洞。修:入口 `foreach (var x in durs) if (!double.IsFinite(x) || x <= 0) return null;`。

**Important**
- **`GpuName.IsIntegrated` 把 RX Vega 56/64 判成核显(已实测证明)**:`GpuName.cs:60-62` 的 `Vega\s*(?:3|4|5|6|7|8|9|10|11)(?!\s*(?:56|64))` 里交替组只吃掉**一位**数字,前瞻随即在剩下的 `"6"`/`"4"` 上求值 → 前瞻**恒成功**。我用 .NET 正则实测确认:
  | 名称 | 期望 | 实际 |
  |---|---|---|
  | `AMD Radeon RX Vega 56` / `64` / `56 8GB` / `64 Liquid` | 独显 | **判为核显** ✗ |
  后果全是"用户可见的错误行为且无异常可查":`SafeRender.cs:738-742` 把整机判成没有独显 → `IsWeakDevice=true`、提示"核显(共享显存,较慢)";`VideoView.xaml.cs:3662-3676` 弹**假的**"当前用核显…极慢"引导用户开兼容模式;`EngineService.cs:139-140` 因此 `wantDiscrete=false`,去问 Windows 要**省电(核显)**偏好——这是**通往"选独显跑核显"的第二条独立路径**。
  **已验证的修法**:`Vega\s*(?:10|11|[3-9])(?!\d)`,我用 10 个真实卡名实测 **10/10 全部正确**(Vega 56/64/56 8GB/64 Liquid → 独显;Vega 3/8/11/`Radeon(TM) Vega 8 Graphics` → 核显;RX 580 与 `Radeon(TM) Graphics` 不受影响)。**该模式在仓库里有 2 份,必须同步改**:`AlhPro.Core/GpuName.cs` 与 `ImgUpscalerUI/GpuInfo.cs`。
- **`DeviceRouting.ResolveEngineDevice` 生产代码零调用,却带着 16 处绿色测试**:全仓库只命中定义(`DeviceRouting.cs:25-39/61-83`)与 `DeviceRoutingTests.cs`;真正在跑的是 `EngineService.ResolveDmlDevice` 与 `GpuInfo.GetRecommendedEngineId`。注释与测试把它称作"选独显却跑核显的根治点",**测试全绿会给人"已修"的错觉** —— 这是本次审查里**最强的假安全信号**。必须二选一:接上,或连同测试一起删。
- **`BuildVfrSetptsExpr` 最后一段没有"表尾之外"的兜底**(`VideoPipeline.cs:101-103`):每段都写成 `lt(N,e)*gte(N,s)*(...)`,末段 `e = durs.Count` → 送进来的帧数只要比 `durs.Count` 多 1,那些帧**所有项都是 0 → PTS=0**(与首帧同刻)。而 `finalDurs` 是按"当时磁盘帧数"对齐、setpts 又在滤镜链**末尾**(`:1929-1937` 还有 `minterpolate`/`fps` 重采样),±1 帧是常态。修:末段去掉 `lt` 上界。
- **`PathUtil.CeilPowerOfTwo` 在 n > 2³⁰ 或 +Inf 时死循环**(`PathUtil.cs:42-47`):`p` 溢出回绕到 0 后 `0 < n` 恒真。现状不可达(调用点都传 2..16),但一旦接上"自定义倍率"就挂死 UI 线程。修:`if (n > 1073741824) throw ...` 或封顶。
- **`FrameInspect.ForEachSample` 在最小边 ≤4px 时采样点为 0**(`FrameInspect.cs:31-42`):网格从 `step`(最小 4)开始 → 4×4 及更小图 `total == 0` → `IsNearBlack` 返回 false = **非黑**,黑帧防线在这些帧上静默失效。现状基本不可达。修:网格为空时兜底采中心点。
- **`RenderPolicy` 的 NaN 落到最激进档位**(`RenderPolicy.cs:36-38/52`):`VideoTileSize(NaN)` → 所有比较为假 → **768**(最大分块);`VideoBatchSize(NaN)` → **240**(最大批)。而该类的设计原则是"不确定就保守";`0`/负数都正确落 256/25。修:开头 `if (!double.IsFinite(vramGB) || vramGB <= 0) return 256;`。

**Minor**
- `VideoPipeline.cs:27-28` 先 `int×int` 再转 double(`interpScale=16` 时约 1.34 亿帧溢出),与 `:23` 的正确写法不一致。
- `VideoPipeline.cs:18`(`VideoService.cs:89` 同款)`(int)Math.Max(1, duration*fps)` 越界转换结果未定义(x64 通常得 `int.MinValue` → 负帧数 → 负秒数 ETA);需 ≈9.9 小时@60fps 才可达。
- `VideoPipeline.cs:32-34` 超分常数与注释基线差 2 倍(注释写 1080p 单帧 0.18s,代码 2x 时实为 0.36s)→ 注释与代码二者之一错,后人按注释调常数会越调越偏。
- `VideoPipeline.cs:93` 用"400 段"代理"命令行长度":实测 400 段在真实 6 位帧号下已达 **26,168 字符**(Windows 上限 32767),叠加其它参数只剩 ~15% 余量,超限时会在**整段视频处理完之后的合帧阶段**抛"命令行过长"。修:直接判 `sb.Length > 20000`。
- **单测质量**:`VideoPipeline.EstimateProcessSeconds` 只有单调性断言、**无数值断言与退化输入用例**(公式常数改 2 倍也不会失败);`BuildVfrSetptsExpr` 的分段数学**完全无保护**;`MergeDurations` 未测删首帧/越界编号;`PathUtil.FfmpegSafePath` 只测了主分支,`:27-35` 那个"输出文件还没创建"的兜底分支(几乎为该它而写)**无用例**;`DeviceRoutingTests.cs:141-142` 的 `id == 2 || id == 3` 未钉住"取最小";`InterpFramingTests.cs:42` 的 `Assert.Equal(idx, StartIndex(...) + 0)` 与循环里的 `idx` **同源、没有独立 oracle**;`GpuName` 的 Vega 分支**零覆盖**。

---

## 九、设备与引擎层(DXGI 之外)

**Important**
- **`devs.Count <= 1` 把"还没枚举(0)"当成"单卡(1)"**:`EngineService.cs:125` → ncnn 编号被原样当 DirectML `device_id`。而 `Devices` 为空是**正常可达状态**(`VulkanCheck.cs:644-665` 命中缓存时只恢复 Report/GpuAvailable、从不填 `Devices`;补填只在 MainPage 自检的后台任务里)→ 该窗口内启动的任务**映射被整体跳过**,双卡机直接跑错卡且日志无痕。修:`Count==0` 时记 Warn 并返回 -1。
- **`EngineService.cs:180` 的 `catch { return engineGpu; }`** 吞掉映射失败并返回未映射编号,与 `:176-178` 自己的注释"宁可落 CPU,不静默跑核显"**直接矛盾**,且不留任何日志(静默用错卡的最后线索被抹掉)。
- **空闲心跳在"喂狗",导致真 hang 永远不判死**:`EngineService.cs:936-953` 每 3 秒推进 `maxPct` **并刷新 `lastOutTicks`**,而看门狗判据正是 `sinceOut`(`:1020/:1031`);且 `watchDir == null` 时帧停滞判据被主动跳过(`:1039`)。→ **只要引擎吐过一行输出,之后真挂死也不会被杀**(单张图/分块直跑等最常见路径全在此列),同时假进度被推到 98%。修:心跳只改 `maxPct`,看门狗改用独立的 `lastRealOutputTicks`。
- **全局 `_gpuRetryDepth` 把"并发"当"递归"**:`EngineService.cs:69/1387-1418` 用 `Interlocked.CompareExchange` 当递归哨兵,但视频并发 2~3 路是**并行**调用 → B 批看到 A 批的深度就直接放弃重试,**只有一路享有"同设备重试 3 次"**。修:`AsyncLocal<int>` 或按设备维度的 `ConcurrentDictionary`。
- **`SafeRender.Profile` 缺 `VulkanCheck.Done` 守卫且终身不失效**:`SafeRender.cs:700-713`(对照 `:722-748` 的 `_weak` 有守卫);而 `VideoService.EstimateProcessSeconds`(`VideoService.cs:82`)一选文件就读它。→ 自检完成前首次读取(编号纠正路径窗口可达一分多钟)会把强机**永久**钉成 `UltraLow`:并发恒 1、ETA ×6、「重新检测」后也不恢复。修:未 Done 时返回不缓存的 Balanced,并在 `Recheck()` 里失效 `_profile`/`_weak`。
- **把"空闲显存 0"当成"测不到",反而放开并发**:`SafeRender.cs:234-241` 的 `ProbeNvidiaSmi` 用 `mb > 0` 过滤,`EnsureFreeVramProbed` 因此把"nvidia-smi 报 0 MiB 空闲"记成 `Measured=false`;而 `:341-342` 的 `!FreeVramMeasured || FreeVramGB >= 3/8` 在"未实测"时是**放行** → 显存被吃干时反而允许 2~3 路并发。修:探测成功即置 `Measured=1`(值可以是 0)。
- **`GetVideoTileSize` 用 `Devices[0].Name` 判显卡家族**(`SafeRender.cs:300-309`),而非本次实际选中的设备。双卡机(Vulkan 序 0 常是核显)上跑独显的作业按核显/AMD 家族选 tile,"家族感知"白做。
- **`EngineService.cs:1023-1029`** 启动超时按档取 90s/30s,日志却固定写"启动 30 秒无输出" → 诊断包时间线与真实判据不符。

**Minor**
- `GpuInfo.cs:45` 去重、`:71`/`:248` 不去重 → 同名条目出现时 `DriverTooOld` 静默失效、驱动配对错位、`GetDiscreteVramGb` 可能取到**另一张卡**的显存并污染 `TotalVramGB`。修:抽统一的 `EnumerateAdapters()`。
- `EngineService.cs:490+508`、`435+475`:`CreateDXGIFactory1` 的原始引用**从未释放**(每次映射/诊断漏一个 factory)。
- `EngineService.cs:290-291` 未标 `ExactSpelling`(靠回退到函数名才成功)。
- `EngineService.cs:176-178` 返回的 **-1 被直接喂给 ORT**:`CutoutService.cs:270`、`AudioEnhanceService.cs:81` 把 `-1` 传给 `AppendExecutionProvider_DML`,而 ORT DML EP 里 `device_id` 会走 `EnumAdapters1(device_id, &adapter)`,**没有 -1 = 默认适配器**的特例 → 必然抛 → 被 `catch {}` 吞 → 静默 CPU,原因被异常信息掩盖。
- `VulkanCheck.cs:17-27/116-117/678/690`:`Done/GpuAvailable/Devices` 无同步,`ReProbe` 复位 `Done` 可绕过闸门导致**并行探测**;`Clear()+AddRange` 期间消费者会看到空表或抛"集合被修改"并静默退回注册表序。
- `SafeRender.cs:218-226` nvidia-smi 只取首行 → 双 N 卡机取的是 **GPU0** 的空闲显存。
- `EngineService.cs:1818` vs `:2028` 分块临时目录(`imgup_tiles_*`,可达数百 MB~数 GB)**只在成功路径清理**,失败/取消路径留到下次启动。
- `GpuInfo.cs:90-96` AMD 核显判定是**黑名单式**(未验证),新 APU 命名可能被当独显给 3 分。修:改白名单式判独显。

---

## 十、音频与应用外壳

**Critical**
- **`AudioSrsDsp` 谱轴错误:AI"补出来"的高频在最后合并阶段被成段丢弃**(`AudioSrsDsp.cs:275-283`,配 `:286-299 RfftD`、`:238-252`)。`RfftD` 把长度 n 的信号零填充到 np(next pow2)做 **np 点** FFT,却只保留 `n/2+1` 个 bin;而 `SpectralMerge` 的频率轴按 `freqs[k]=k*sr/n` 算 → 差 **np/n 倍(1~2 倍)**,同时"保留 n/2+1 个 bin"等价于把输出硬限带到 `(n/np)×fs/2`。**实测(44.1k、17.64kHz 正弦)**:n=4096(恰为 2 的幂)→ 衰减 0 dB;n=4097 → **−68.5 dB**;**n=352800(正是作者自测用例"8 秒 16k→48k"的长度)→ −83.4 dB,保留上限只有 14.8kHz**。分频点也落到预期的 0.5~0.67 倍(16k 源本应交叉在 8kHz,实际约 5.4kHz)→ **5~8kHz 真实原声被 AI 猜测替换**,恰是该功能最该避免的。修:改用时域分频(本文件 `:26-37` 已有窗化 sinc 生成器,低通+互补高通相加),或改成固定 2 的幂块 STFT 交叉 + 重叠等功率淡入淡出。
- **`ResamplePoly` 丢弃小数相位 → 等价"低通 + 最近邻(零阶保持)"**(`AudioSrsDsp.cs:38-51`):`center = (int)Math.Round(pos)` 后 kernel 只在整数抽头上取样,凡 `round(pos)` 相同的输出样本**逐位相同**。**实测(1kHz 正弦)**:
  | 变换 | 相邻重复样本占比 | 对理想正弦 SNR |
  |---|---|---|
  | 16k→44.1k(LavaSR 第一级) | **63.7%** | **18.9 dB** |
  | 16k→48k | **66.7%** | **19.4 dB** |
  | 44.1k→48k(LavaSR 末级) | 8.1% | **27.7 dB** |
  | 48k→16k(下采样) | 0% | 62.2 dB(正常) |
  | 同算法按 `(k-f)` 求核 + 按相位归一 | 0% | **87.9 dB** |
  → **每一次 LavaSR 输出都带约 −28dB 的镜像/量化失真**,前级约 −19dB,听感是颗粒感/齿音发毛("AI 升采样率反而变糊")。**下采样路径正常,别动**。修:`int c=(int)Math.Floor(pos); double f=pos-c;` kernel 按 `(k-f)*cutoff` 求值并按该相位归一。
- **取消/强制结束永远杀不掉 ffmpeg(`p.Kill` 是死代码)**:`AudioService.cs:223-228`(同构 `:284-289`、`:365-370`、`:445-450`)。`while (!p.HasExited && !ct.IsCancellationRequested) await Task.Delay(100, ct);` —— 取消时 `Task.Delay` 是**抛** `OperationCanceledException`,直接跳过紧随其后的 `if (ct.IsCancellationRequested) { p.Kill(...); }`;该分支只在"进程恰好已退出且刚被取消"的竞态下才可能执行。后果:用户点「强制结束」→ 界面显示"已取消"、批次 break,但 ffmpeg **继续把整个文件转完**(输出目录照旧出现成品,用户以为没处理却看到了结果);反复重来还会叠多个 ffmpeg。而 AI 分轨段(`AudioEnhanceService.cs:118`)是真取消,两段行为不一致。修:`await p.WaitForExitAsync(ct)` + `finally { if (!p.HasExited) p.Kill(true); }`,四处同改。
- **音频页 MediaPlayer 被 Dispose 后页面实例却被缓存复用 → 预览全废 + 未捕获异常弹全局错误框**:`AudioView.xaml.cs:91-92` 在 `Unloaded` 里只 `_mediaPlayer?.Dispose()`,不置 null、**也没有任何重建路径**;而 `MainPage.xaml.cs:1097` 是 `_audioView ??= new AudioView();`。切走再切回后双击预览:波形能画(在异常之前),但播放/暂停/拖音量直接抛 `RO_E_CLOSED`/`ObjectDisposedException` → 落到 `App.xaml.cs:327` 全局兜底弹"程序遇到问题"。`PlayBtn_Click`(`:571`)、`ResetBtn_Click`(`:597`)、`VolSlider_ValueChanged`(`:99`)、`WaveHost_PointerPressed`(`:703`, `?.` 防不住"非空但已释放")**都没有 try/catch**。修:Dispose 后置 null 并惰性重建(或把释放挪到窗口 Closed),并给这 4 处加 try/catch。

**Important**
- **AI 分离的 DML 挂载失败被静默吞掉,UI 却宣称"显卡加速"**:`AudioEnhanceService.cs:80-84` 的 `catch { /* DML 不可用回退 CPU */ }` 之后无条件建会话 → `ToDmlDevice` 返回 -1 或 DML 不可用时安静跑 CPU,而 `onCpu` 仍为 false、`AudioView.xaml.cs:867-869` 状态文字是"AI 分离中(**显卡加速**)"、日志写"显卡加速,请耐心等待",用户等一小时也不会知道在跑 CPU。(与第二节 I-1 同一根因。)
- **所有 GPU Run 异常都被当作 DML 抖动记连击**:`AudioEnhanceService.cs:134-142` 对**任何**异常都 `NoteDmlTransientFailure(..., Audio)` → 输入名不匹配(`:130/:156` 硬编码 `"mix"`,而 `LavaSrService.cs:105` 用 `InputMetadata.Keys.First()`)、形状错误、显存 OOM、模型损坏都会被算成"GPU 抖动",连续 3 个分块后该 GPU 在音频域被本进程判死,日志却写"GPU 推理失败/已达上限",把真实原因掩盖掉。
- **降噪"弱/中/强"三档实测完全无效(甚至方向相反)**:`AudioService.cs:154-158` 三档只改 `afftdn=nf`(−25/−30/−35),但 afftdn 的最大降噪量由 **`nr`(默认 12dB)**决定。**用仓库自带 ffmpeg 实测**:白噪声(mean −38.2dB)下 `nf=-20/-25/-30` 输出**全是 −50.2dB**(正好撞 nr 上限),`nf=-35` 反而略弱,只有 `nf=-50` 才明显不同;改 `nr` 才有单调分档(`nr=6/12/24/40` → −44.2/−50.2/−62.2/−78.3dB)。而 `AudioView.xaml:134-136` 明确承诺"强(重降噪)"——用户调"强"得不到任何变化。修:按档位改 `nr`(如 8/14/24dB),要更强再叠 `tn=1`。
- **自检口径自相矛盾,并把"DirectML 可用但 Vulkan 失败"的机器静默降回 CPU**:`MainPage.xaml.cs:100` 步骤④在**没有 ONNX 模型**时也打勾 ✓,而同屏报告 `:312-313`/`:421` 用 `DmlFallbackOk>=0` 写"DirectML/ONNX 加速:**不可用**(红)";`:104-106` 又用 `VulkanCheck.Devices` 判 `curValid`。**最严重**的是 `:144-152` 在 Vulkan 判无 GPU 时把 `AppSettings.GpuIndex = -1`,而 `EsrganOnnxService.cs:204` 的 `if (AppSettings.GpuIndex < 0) return -1;` **短路掉了 `:205-220` 那段专为修此问题写的"用真实 DML 探测取代 Vulkan 判定"逻辑** → 整进程超分/补帧/音频分离全部走 CPU,用户只看到假的"未检测到可用 GPU"。(与 I-1 同类复发。)
- **用 `OperationCanceledException` 做参数校验 → 中断整批 + 误报"已取消"**:`AudioView.xaml.cs:950` 在未勾选任何轨道时 `throw new OperationCanceledException()`,被 `:1036 catch (OperationCanceledException) { Log("⚠ 已取消"); break; }` 接住 → **break 终止整个批次**(后面 N 个文件一个都不处理),日志里紧跟着一句与上文矛盾的"已取消";而且该校验发生在 Demucs 推理(`:874`)**之后**,白等几分钟才被拒。修:校验提前、改抛 `InvalidOperationException` 并计入 `fail++` 继续下一个。
- **UI 线程同步阻塞**:`AudioView.xaml.cs:445-460` 的 `AddFiles` 对每个文件**同步**调 `AudioService.Probe(p)`(每文件启动一次 ffmpeg 并解码前 30 秒)→ 拖入 20 个文件 UI 冻结数秒到十几秒;`:519-527` 预览的分桶循环也在 UI 线程。
- **广告/提示的自适应拉取在断网时不会停**:`AdFetcher.cs:99-116`(`TipFetcher.cs:55-70` 同构)在"全端点失败且无 404"时返回**空串**,调用方只 `continue`,只有真 404 才 `break` → 无网环境下每个文件都要把 4 个端点各等一次 5s 超时(最长 20×4×5s),每 10 分钟重复一轮;且 `FetchAllAsync` 没有 `CancellationToken`。修:整轮加预算 + "所有端点都失败"即停止。
- **远程 JSON 的 link 直接 `Process.Start(UseShellExecute=true)`,而数据来自第三方镜像**:`MainPage.xaml.cs:880`/`:1038`,数据源是 `cdn.jsdelivr.net`/`gh-proxy.com`/`ghproxy.net`(`AdFetcher.cs:122-128`)→ 被替换的内容可下发 `file:///`、`ms-settings:`、UNC 等任意目标;隐私政策(`:1633`)只声明"从 GitHub 拉取"。修:校验 `Uri.TryCreate` + `scheme==https` + host 白名单。
- **无滤镜时 `keepRate` 被整段丢弃:48k 源静默输出 44.1k,日志却写"48000Hz"**:`AudioService.cs:181-183` 的 `var af = filters.Count > 0 ? $"-af ..." : "";` —— 用户把所有效果都关掉时 `filters` 为空,`aresample=<aiOutRate>` 不会被应用;而 AI 分轨固定产出 44.1k → 48k/32k 源最终得到 44.1k 文件,`:887/:920/:981` 的日志与状态栏却都声称"(48000Hz)"。
- **设置写入非原子,崩溃即丢全部设置**:`AppSettings.cs:100-129`(同族 `AudioView.xaml.cs:230-252`、`MainPage.xaml.cs:2089-2108`、`AppLogger.cs:77-84`)全是 `File.WriteAllText` 直接覆盖。写窗口内崩溃/断电/被强杀 → JSON 截断 → `AppSettings.cs:94 catch {}` 静默回落默认:`GpuIndex` 回到 0(**不是**推荐独显)、`SelfCheckDone=false`(下次又弹自检并重跑 GPU 实测)、`TempDir` 丢失。修:写 `.tmp` 后 `File.Replace`;读取失败时逐字段容错而非整体回默认。
- **日志清理只在启动时跑一次**:`AppLogger.cs:165-213` + `App.xaml.cs:495` → 连续处理十几小时的会话里日志会突破 `MaxSizeMb`(上限也才 20MB);且每条 `Info` 都 `Task.Run` + 整文件 append,与同步的 `Warn/Error` **行序错位**。修:按行数/字节阈值持锁触发 `Cleanup`,行内加序号。
- **"上次退出界面"对音频页失效**:`MainPage.xaml.cs:2100-2120` 的 `LoadLastPage` 只接受 `p is >= 0 and <= 2`,而 `SaveLastPage` 对音频页写 `3`(`:1063`)→ 静默回落到图片页。修:改成 `<= 3`。
- **`LavaSR` 全曲一次性处理、无任何长度/内存上限**:`LavaSrService.cs:33-53`/`:120-135` 同时持有逐声道 float 数组 + `Complex[nFrames,1025]` + `double[outLen]` + `SpectralMerge` 的**全曲 np 点 FFT**(10 分钟 44.1k 曲 np≈33.5M → 单个 `Complex[]` 537MB,同时存在多个 + `double[np]` 268MB)→ 峰值轻易 1.5~2GB,OOM 后 .NET 几乎不可恢复。对照 `AudioEnhanceService.cs:102-106` 有 2.5GB 预检,**LavaSR 一点没有**。修:与 C1 一起改分块 + 入口内存预检。
- **"音频到底用没用显卡"三处说法互相矛盾,还建议用户选一个已不存在的选项**:自检报告(`VulkanCheck.cs:619`,经 `MainPage.xaml.cs:319-320` 展示)写"音频处理:**CPU 计算**,任何设备均稳定";设置里 `:2224` 写"音频增强/分离(Demucs)会**优先用这里选的显卡(DirectML)加速**";而代码 `AudioView.xaml.cs:867-875` 确实按 `GpuIndex` 走 DML。同一段文字还写"无独显的电脑建议选 CPU",但 `:2207-2212` 的下拉**已不再提供 CPU 选项** → 用户照做只能选到一个可能不存在的显卡号并被写进设置。

**Minor**
- `AudioService.cs:55-89` Probe 解析脆弱:正则 `(\d+)\s*Hz` 取**最后**一个匹配、`7.1` 被映射成 6 声道、不检查 `ExitCode`、失败返回全 0(界面显示"0Hz · 0 声道");且 `:59` 只解码**前 30 秒**测电平,却被 `:170-171` 用来决定"源已足够响 → 跳过 loudnorm"——前 30s 安静、后段很响的文件会被误判并压小音量。修:`ffprobe -show_streams` 取权威流信息;响度用全曲 `ebur128`/`astats`。
- `AudioService.cs:130`(同 `:143/:251/:335/:402`)进度基准 `static double DurSec` 是可变静态字段(当前顺序调用未触发,并行即互相污染)。
- `AudioService.cs:479-491` 两个连续 `<summary>`(注释重复),文档说"保持声道数"而实现写死 `-ac 2`。
- `AudioSrsDsp.cs:50`/`:282`、`LavaSrService.cs:197`、`AudioEnhanceService.cs:364` 到处 `Math.Clamp` 硬限幅:窗化 sinc 有负瓣、ISTFT/OLA 会超调 → 满幅素材被削波。修:输出前按峰值整体缩放或接 `alimiter`。
- `AudioSrsDsp.cs:43-49` 越界取样用"复制端点样本"补边 → 首尾各 ~32/ratio 个样本被污染(轻微 click)。
- `AudioSrsDsp.cs:73`/`:158` 帧数少算(scipy `padded=True` 是 `1+len//hop`)→ 末尾最多 512 样本(≈11.6ms)没被分析,ISTFT 后成静音尾巴;`:305 HannWindow(n)` 在 n≤1 时除零 → NaN(当前恒 2048,潜在)。
- `LavaSrService.cs:36-37/74-77` 未校验 `sr>0`:WAV 头采样率为 0 时 `down=0` → double 除零 → `new float[(long)Math.Round(∞)]` 抛 OverflowException(需伪造损坏 WAV 头,**未验证可达**)。
- `AudioView.xaml.cs:53-66` + `AudioView.xaml:250`:`AudioItem` 没实现 `INotifyPropertyChanged`,模板只绑 `Display` → `:1032/:1045` 设置的"(已处理)/(失败)"**不刷新**(虚拟化下时有时无),`item.Status` 没有显示位置 → 用户看不出哪个文件失败,只能翻那个 180px 的日志框。
- `AudioView.xaml.cs:1044-1047` 失败提示不可行动:`ex.Message.Split('\n')[0]` 只取第一行,而 `AudioService.cs:232-234` 特意保留的 ffmpeg stderr **尾部 400 字**(真实原因全在那里)不在第一行 → UI 只剩"ffmpeg 处理失败(exit 1):"。
- `AudioView.xaml.cs:128-167`/`:228-252`:`Options_Changed` 对若干控件用非空解引用(其他都用了 `?.`);`_suppressSave` 初值为 false(同类隐患:`LoadSettings` 之前任何控件事件都会把默认值写盘覆盖用户设置)。**未验证**构造期是否真触发。
- `MainPage.xaml.cs:679-683`/`:917-951`:`StopAdActivity`/`StopTipActivity`/`StartTipActivity` **从未被调用**(死代码)→ 取消勾选"本次运行显示广告"后广告虽隐藏,但每 10 分钟仍继续联网拉取,`_adCts`/`_tipCts` 也从不 Dispose。
- `MainPage.xaml.cs:455-459`/`:564-604`:`CheckUpdateSilentAsync` 是 `while(true)` 无限循环,页面没有 `Unloaded` 清理(广告/提示轮询同样)→ 页面若被重建会叠加循环。
- `MainPage.xaml.cs:41-220`:`Loaded` 的 `async void` lambda **没有整体 try/catch**(`:67-74` 的 `CheckEngines`/`SafeRender.*` 都没包)→ 任一步抛异常就跳过后面全部初始化(自检/更新/广告/提示),用户只看到全局弹窗。
- `MainPage.xaml.cs:83-212` 启动把重活**串行 await**(逐个建 4 个 64MB 模型的 DML 会话、逐个 1×1 实测、`:273` 再固定等 1 秒),`:95` 注释"探测极轻"与事实不符 → 首启内容明显变慢。
- `MainPage.xaml.cs:461-463` `("视频补帧", rifeExe && (rifeOnnx || rifeExe))` 后半**恒真** → 只有 `rife.exe` 无模型也报"可用"(自检漏报)。
- `AppSettings.cs:32` + `MainPage.xaml.cs:3109-3154`:`SponsorPromptTime` 语义错位——存的是"未来时间点"却按 `Now - x < 10h` 判断 → 实际冷却 **20 小时**(注释与 ToolTip 都写 10 小时)。
- 冗余文案/弹窗:`MainPage.xaml.cs:2165-2168` 与 `:2170-2175` 几乎逐字重复;`:3063-3064` 与 `:3081-3083` 重复;`:2222-2227` 是 200+ 字长段落塞在下拉框下(还含已不存在的 CPU 建议);`AudioView.xaml:36/38`、`:52-53`、`:144-153` 每个选项都"ToolTip + 正文"各讲一遍;`AudioView.xaml.cs:1066-1083` 每次跑完(哪怕 1 个文件)都弹"处理完成"。
- `TutorialView.xaml.cs:23-51` 构造函数里同步把整份 md 渲染成 UI 元素(无虚拟化)→ 长教程切页卡;`:71` 的分隔行判定 `^[\s:\-|]+$` 会把"某单元格只写 `-`"的正文行误删。
- `AudioEnhanceService.cs:55-58` XML 注释说 `vocalStrength=0~1`,实现按 `/100`(所有调用点传 `100f`)→ 契约错位,将来按注释传 `1f` 会得到"伴奏≈原曲"。

---

## 十一、更新后的修复顺序(优先级最高在前)

**第 0 步(必须最先做,否则后面全是盲修):**
1. **修 DXGI 槽位 `SlotEnumAdapters1 11→12`、`SlotGetDesc1 9→10`;删掉 `IDXGIFactory6` 声明与 `TryGetAdapterIndexByGpuPreference`(崩溃隐患)**;按真实顺序重排两个 COM 声明(或删掉 COM 路径只留 vtable)。
2. 跑一次,读日志行 `GPU→DirectML 映射对照:` —— 这是**第一次**能看到真实 DXGI 序。**在这一步之前,任何"选独显跑核显"的结论都只是推断。**
3. 依据真实 DXGI 序修**二次映射**(第零节),并统一连击表的编号空间(C-2)。

**第 1 批(正确性,改完立刻可复测):**
4. C-5 黑帧存在量词 → 改为"对应帧";C-6 `ValidateVideoFileAsync` 的 `minFrames` 参数化
5. C-1 超分输出 NaN/Inf 防线;C-3 会话池失败不静默落 CPU;C-4 不让 CPU 会话占 GPU 键
6. C-8/C-9/C-10 超分页(选区倍率、ONNX 丢参数、存读 off-by-one);C-11/C-12/C-13 抠图页(记住上次、崩溃卡死、EXIF)
7. 音频 C1/C2(`AudioSrsDsp` 谱轴 + 重采样相位,**同一个文件**)+ C3(取消杀不掉 ffmpeg,四处)+ C4(MediaPlayer 惰性重建)
8. `GpuFault` 补 `has been suspended` + 按 HResult 数值判

**第 2 批(用户直接看得到):**
9. C-7 删坏掉的超4K弹窗 + I-17 删重复弹窗 + I-18 去重文案 + I-4(设备/编号相关)
10. I-13/I-14/I-15 预览=结果三处失真
11. 第三节(更新历史/版本一致性)、第四节(ETA 口径)
12. `GpuName` Vega 修正(Core + `GpuInfo` 两份同步)+ `DeviceRouting` 死代码去留拍板
13. I-3(音频不查 `DmlDeviceDead`)、I-5/I-6/I-7(视频正确性三小改)、I-11(UA 累加)、I-9(降噪档位改 `nr`)、I-8(自检口径统一)

**随手清:** 第五节的死代码与文案/工具提示、`SafeRender`/`EngineService` 的 Minor、`MainPage` 的死代码与启动串行化。

---

*本报告每条结论都标注了 `文件:行号`。标注「未验证」的是推断而非实测。**DXGI 部分已由本机实测推翻了我早前的"沙箱禁止枚举"判断**,相关结论现在是实测事实;但"选独显跑核显"在**你机器上**的最终表现仍需修好槽位后用日志确认。*

