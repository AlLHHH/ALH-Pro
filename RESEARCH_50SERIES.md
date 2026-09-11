# RTX 50 系（Blackwell）超分/补帧：兼容性与速度研究

> 状态：**研究进行中，未交付**。本文件是工作文档，随证据补齐更新。
> 目标机型：RTX 5060 Laptop 8GB + AMD 610M 核显；RTX 5070 Ti 16GB + AMD 核显。
> 本机实验台：RTX 4060 Laptop 8GB，驱动 572.83，Win11 26200。
> 所有实测数字都标注来源与条件；未验证的一律写"未验证"。

---

## 0. 结论摘要（先看这段）

| # | 结论 | 证据强度 |
|---|---|---|
| 1 | **"ncnn-Vulkan 在 50 系不可用"的根因是 NVIDIA 驱动的 cooperative-matrix 属性查询缺陷**，不是引擎版本、也不是我们的代码 | **一手确认**（NVIDIA 官方论坛 + ncnn 源码 + 二进制差分） |
| 2 | **"升级 ncnn 引擎"这条路不成立**：我们随包的 5 个引擎（含 2022 与 2025/2026 构建）**全部**会触发该查询，而且最新的 waifu2x 触发得最多；同时 **RIFE 与 Real-ESRGAN 的上游自 2022 年就已冻结**，根本没有"新引擎"可换 | **一手确认**（二进制差分 + releases 页面核实，均本机实测） |
| 2b | **驱动侧缺陷至少到 610.43.03（2026-07-14）仍未修复**，且失败形态从"属性查询崩溃"扩展到"`vkCreateDevice` 直接失败"；"Vulkan 层屏蔽"已有一次"层生效但未解决"的先例 | **一手确认**（NVIDIA 论坛两个帖） |
| 3 | **速度瓶颈不在运行时**。补帧（RIFE 1080p）DirectML 只比 ncnn 慢约 **1.25×**；分块超分（512）DirectML **115 ms** vs ncnn **118 ms**（持平） | **本机实测** |
| 4 | **ONNX CUDA EP 应放弃**：补帧慢 **4.6×**、超分 1024 分块慢 **3.4~4×**、1080p 整图**直接失败**，代价还要多 **3357 MB** 运行库 | **本机实测** |
| 5 | **"整图送进 DirectML 会爆"有明确阈值**：8GB 卡上 ≤1024 分块效率平坦（327~400 ms/Mpix），**1280 起崩塌 13.7~20×，1536+ 直接 OOM** | **本机实测** |
| 6 | 应用把 ONNX 分块**上限定在 768**，低于本机实测最优点 1024；16GB 卡应按显存放宽到约 1400~1536 | 实测 + 估算（外推部分已标注） |
| 7 | **补帧的真正缺口在应用侧**：50 系真机实测 282 ms/帧，而裸 DirectML 能力约 118~126 ms/帧 → 差约 **2.2×**；该缺口对应的修复（RIFE 自适应分块）已在 `c9c7747` 提交 | 实测 + 代码核对 |
| 8 | NGX / Optical Flow / RTX Video SDK：**前两者未见 50 系支持声明**，第三者支持 50 系但下载登录门禁、许可未知 → 均不建议作为主路径 | 一手确认 + 未验证项已标注 |

**一句话**：不要为 50 系换引擎、也不要换运行时；**保持"真机探测 + 失败落 ONNX DirectML"，把应用侧的分块与提交方式修好**。兼容性的最终解锁取决于 NVIDIA 驱动修复。

---

## 1. 兼容性：根因与证据链

### 1.1 一手来源：NVIDIA 官方论坛
《Blackwell (RTX 5050 Laptop, sm_120) Vulkan driver crashes in cooperative-matrix property queries》
<https://forums.developer.nvidia.com/t/369162> ，发布 2026-05-06，驱动 591.74。

原文要点（逐条转录）：
- NVIDIA Vulkan 驱动 `nvoglv64.dll` 在 cooperative-matrix 属性查询中**访问违例**；两个扩展都受影响：
  `vkGetPhysicalDeviceCooperativeMatrixPropertiesKHR`（VK_KHR_cooperative_matrix）、
  `vkGetPhysicalDeviceCooperativeMatrixFlexibleDimensionsPropertiesNV`（VK_NV_cooperative_matrix2）。
- **"驱动会宣称支持这两个扩展和全部相关特性位"**，于是调用方按标准两段式查询（先取数量、再取属性）→ 崩溃。
- 环境与用户机型拓扑一致：RTX 5050 Laptop（Blackwell sm_120）+ AMD 核显的混显笔记本，Windows 11 26200。
- 绕法证据：llama.cpp 用 `GGML_VK_DISABLE_COOPMAT=1` 跳过这些路径后**一切正常** ——
  明确说明"Blackwell 上其余 Vulkan 栈是好的，坏的只是 cooperative-matrix 属性查询"。
- 该帖回复仅为"会转给工程团队"，**没有修复确认**。

### 1.2 ncnn 源码：它恰好会去查
`Tencent/ncnn` master `src/gpu.cpp`（本机下载核对，255 KB）：
- 第 1530/1533/1546 行：`if (support_VK_KHR_cooperative_matrix && queryCooperativeMatrixFeatures.cooperativeMatrix)` → `vkGetPhysicalDeviceCooperativeMatrixPropertiesKHR(physicalDevice, &propertyCount, 0)`（正是会崩的两段式）
- 第 1601/1604/1617 行：`VK_NV_cooperative_matrix` v1 版本
- 第 1734/1737/1750 行：`vkGetPhysicalDeviceCooperativeMatrixFlexibleDimensionsPropertiesNV(...)`（论坛中的 Crash 2）
- 第 2878/2888/2893 行：用 `vkGetInstanceProcAddr(g_instance, "vkGetPhysicalDeviceCooperativeMatrix*")` 取名 —— 所以**函数名字符串必然出现在二进制里**，这也是下面差分判据的依据。
- **ncnn 不读任何环境变量**（对 `getenv` 的检索无命中）→ **无法用环境变量关掉该查询**。

### 1.3 二进制差分（本机实测，两轮）
**关键**：ncnn 是靠**精确匹配扩展名字符串**来决定分支的（`strcmp(exp.extensionName, "VK_..._cooperative_matrix")` → `support_VK_*`），
只有 `support_VK_*` 且对应特性位为真时才发查询。所以要扫的是**扩展名门**，不是查询函数名。

| 引擎 | `KHR` | `NV2` | **`NV(v1)`** | `NV vector` |
|---|---|---|---|---|
| `realesrgan-ncnn-vulkan.exe`（2022-04-24） | 无 | 无 | **有** | 无 |
| `waifu2x-ncnn-vulkan.exe`（2025-09-15） | **有** | **有** | **有** | **有** |
| `rife-ncnn-vulkan.exe`（2026-08-28） | 无 | 无 | **有** | 无 |
| `rife-ncnn-vulkan_old2022_backup.exe` | 无 | 无 | **有** | 无 |
| `upscayl-bin.exe`（2025-12-07） | 无 | 无 | **有** | 无 |

**这个差别有实际后果**：两个 NVIDIA 帖点名的崩溃是 **`...PropertiesKHR`** 与 **`...FlexibleDimensionsPropertiesNV`（NV2）**，
**都没有点名 `...PropertiesNV`（NV v1）**。因此：

| 引擎 | 在 Blackwell 上的风险推断 |
|---|---|
| **waifu2x 2025-09** | **最高** —— 会走 KHR 属性查询（Crash 1）；若驱动广告 NV2 还会走 flexible-dims（Crash 2）；建设备时还会启用 KHR 特性（帖 371808 的 `vkCreateDevice` 失败） |
| **realesrgan 2022 / rife 2026 / rife 2022** | **取决于驱动是否广告 `VK_NV_cooperative_matrix`**：广告了就会走 NV v1 查询，而该查询**未被点名**；即使查询能过，仍需确认启用 NV 特性位时 `vkCreateDevice` 是否也失败（未验证） |
| upscayl-bin | 同 realesrgan |

→ **最重要的可测结论**：视频超分（Real-ESRGAN）与补帧（RIFE）**有可能在 50 系上本来就可用**，
而动漫超分（waifu2x 2025）最可能失败。**我们的探测是按引擎分别判定的，所以一次真机运行即可逐引擎给出答案。**

### 1.3b ncnn 版本分界（逐 tag 实拉源码核实）
- **≤ `20210720`：完全没有合作矩阵代码**（连门字符串都没有）
- `20220729` ~ `20230517`：只有 NV 分支
- **≥ `20230816`：才有 KHR 分支**

→ 这解释了上表：我们的 realesrgan-2022 与 rife-2022 属于"NV only"那一代；waifu2x-2025 属于"含 KHR"的新一代。

### 1.3c 零成本诊断（建议第一步就做）
在 Blackwell 机器上跑一条命令：
```
vulkaninfo | findstr /i cooperative
```
- 若**没有** `VK_NV_cooperative_matrix` → 那 4 个"仅 NV 门"的引擎**根本不会发查询**，直接安全。
- 若有 → 按 §1.3 的风险推断，以实测为准。
- 本机已确认 `C:\Windows\System32\vulkaninfo.exe` **系统自带**（无需装 Vulkan SDK）；
  但**用户机器上是否有它未验证** —— 若没有，就用我们自己的探测（按引擎逐项）代替。

### 1.3d 已验证的"无查询"应急二进制（waifu2x 槽位）
`nihui/waifu2x-ncnn-vulkan` 的 **`20210521` / `20210210` / `20210102`** 三个 release 的 exe
**完全不含任何合作矩阵门字符串与查询函数名**（代理已下载、解包、`-h` 与 2025 版**逐行相同**、实跑出图 `exit=0`）。
代价（实测）：**慢约 32%**（1241 ms vs 941 ms），且**输出非逐像素相同**（10.563% 像素不同，最大通道差 6/255）。
- **Real-ESRGAN 槽位没有对应的"无查询"二进制**（xinntao 的 ncnn release 最早 20220424 就已含 NV 查询）。
- **RIFE 槽位同样没有**（官方最后一个是 20221029，已含 NV 查询）。


### 1.4 附带发现：两个引擎的 Vulkan 接口面完全相同
- 两者初始化时打印的能力报告**逐字相同**（`queueC/queueG/queueT`、`bug*`、`fp16-p/s/a`、`subgroup`）。
- 二进制里的 `VK_*` 字符串集合：各 26 个，**零差异**。
- → "2022 版与 2025 版在 Vulkan 接口需求上并无不同"，进一步削弱"引擎太旧"的解释。

### 1.4b 驱动侧时间线（第二个一手来源：该缺陷**至少到 2026-07-14 仍未修复**）
《[610.43.02 / RTX 5080] vkCreateDevice returns VK_ERROR_INITIALIZATION_FAILED when cooperativeMatrix feature is enabled》
<https://forums.developer.nvidia.com/t/371808> （2026-05-30 首发，最后活动 2026-07-14）

| 驱动 | 症状 | 出处 |
|---|---|---|
| 591.74 | coopmat **属性查询**访问违例（崩宿主进程） | 帖 369162（2026-05-06） |
| 595.80 | 同样受影响（报告者原话 "Affects both the 610.43.02 and 595.80 driver builds"） | 帖 371808 #2（2026-06-07） |
| **610.43.02** | 驱动 `vkGetPhysicalDeviceFeatures2` **宣称** `cooperativeMatrix=true`，但**一旦应用真的在 pNext 里启用它，`vkCreateDevice` 就返回 `VK_ERROR_INITIALIZATION_FAILED`** —— 明确违反 Vulkan 规范 | 帖 371808 #1（2026-05-30） |
| **610.43.03** | **仍然存在**；报告者定性为"610.x 分支影响 Blackwell（RTX 50 系）的 NVIDIA 专有驱动 bug"，并列举多款游戏受影响 | 帖 371808 #3（2026-07-14） |

关键含义：**ncnn 的两段动作都会踩**——①查属性（帖 369162 的崩溃）；②查完就启用该特性来建设备（帖 371808 的 `vkCreateDevice` 失败）。
`gpu.cpp:4059/4145/4147/4149` 会把 `VK_KHR_cooperative_matrix` / `VK_NV_cooperative_matrix` / `...matrix2` / `...vector` 全部 push 进 `enabledExtensions`，并在特性链里打开（`:5700-5736`）。

**对"Vulkan 层屏蔽"方案的直接否证**：帖 371808 #3 中有人用 **Vulkan Khronos Profiles 层屏蔽 cooperativeMatrix**，确认层已生效（`vulkaninfo` 可见），**但目标程序的崩溃并未解决** —— 因为它还请求了 `VK_NV_cooperative_matrix2` / `VK_NV_cooperative_vector` 这两个变体，层没盖住。
⇒ 若将来要做层方案，**必须同时屏蔽四个扩展名**（KHR + NV + NV2 + NV vector）并同时清零对应特性位；即便如此，对具体应用仍只能"实测为准"。

### 1.4c 上游引擎生态：大部分已冻结，"等新引擎"这条路不成立
用 GitHub releases 页面核实（本环境需 `curl --resolve github.com:443:140.82.112.3` 才能访问）：

| 上游 | 最新 release | 结论 |
|---|---|---|
| `nihui/waifu2x-ncnn-vulkan` | **20250915**（次新 20250802、20250504、20220728…） | 官方仍在维护；**我们随包的就是最新版** |
| `nihui/rife-ncnn-vulkan` | **20221029**（次新 20220728、20220330…） | **官方自 2022-10-29 冻结至今** |
| `nihui/realsr-ncnn-vulkan` | 20220728（此前经 API/搜索核实） | 2022 年冻结 |
| `xinntao/Real-ESRGAN-ncnn-vulkan` | v0.2.0 / 2022-04-24 | 官方冻结（就是我们用的那个） |
| `upscayl/upscayl-ncnn` | 20251207-174704 | 唯一活跃的 realesrgan 系分支；输出与 2022 版**逐像素相同**，但 `-s` 语义不同 |

**由此得到两条重要结论**：
1. **"等上游出一个兼容 Blackwell 的新引擎"不现实** —— RIFE 与 Real-ESRGAN 的上游都停在 2022 年，不会再来新版。
2. **我们 `engines/rife/rife-ncnn-vulkan.exe`（文件时间 2026-08-28）不是官方发布** —— 官方最后一个是 20221029（正是我们保留的 `rife-ncnn-vulkan_old2022_backup.exe`）。
   它比 2022 版多 `-q`（webp 质量）与 `-l`（枚举 GPU）两个参数（研究代理用 `-h` 原文核实），说明来自某个社区 fork，但 **`engines/` 被 gitignore、仓库内没有任何出处记录 → 来源不可追溯**。
   这在工程上是一个需要正视的问题：**一个随包分发、可执行、来源不明的二进制**。建议后续要么找到并记录其确切来源与许可证，要么退回官方 20221029 构建。
   （注：这不影响本文的兼容性结论 —— 因为它同样带 coopmat 查询，换回官方版也不会改变 50 系上的行为。）

### 1.4d 第三轮补充：源码级定论 + 版本边界 + 同机型印证

**① "有没有开关能关掉它" —— 定论：没有，只能改源码**
研究代理对全量 ncnn master 源码（4,289 文件）执行 `CMakeLists.txt + *.cmake` 检索 → **`cooperative` 0 命中**；`gpu.cpp` 里所有 `#if` 只有 `NCNN_VULKAN / NCNN_SYSTEM_GLSLANG / NCNN_VULKAN_LOADER / ENABLE_VALIDATION_LAYER / NCNN_SIMPLEVK / __ANDROID_API__ / __APPLE__`。
关键区分：**`opt.use_cooperative_matrix`（`src/option.cpp:65`，默认 true）管不到这件事** —— 它的全部使用点都在算子/Net 建 pipeline 层（`convolution_vulkan.cpp`、`net.cpp:1364/1738`、`pipelinecache.cpp:219`、`c_api.cpp:377/477`），而**设备初始化期的查询不读 `opt`**，只看从驱动查回的扩展位+特性位。
⇒ `net.opt.*` / CMake 参数 / 环境变量**都拦不住** `create_gpu_instance()` 里那两条会崩的查询。

**② 上游至今没有修复，反而在扩大探测面**
master（2026-09-11 取样）三条查询仍是无条件硬调用；PR **#6898（2026-08-11，merged）**又新增了 `vkGetPhysicalDeviceCooperativeMatrixProperties2EXT` 探测。**没有任何一条 PR 是"按 NVIDIA 驱动版本跳过 coopmat 属性查询"。**
**现成的关机点 = 上游自己的补丁范式**：`gpu.cpp:1265-1301`（AMD RDNA2，PR #6504，注释 "emulated cooperative matrix on amd rdna2 is slow"）
两行 `queryCooperativeMatrixFeatures.cooperativeMatrix = VK_FALSE;` / `queryCooperativeMatrixFeaturesNV.cooperativeMatrix = VK_FALSE;` —— **清零特性位即可让查询不再执行**（因为查询的条件就是这些特性位）。

**③ 逐 tag 拉源码核实的版本边界（决定"哪条查询何时开始存在"）**
| 查询 | 起始版本 |
|---|---|
| `vkGetPhysicalDeviceCooperativeMatrixPropertiesNV`（NV v1） | **2022 年就有** |
| `vkGetPhysicalDeviceCooperativeMatrixPropertiesKHR` | **20230517 ✗ → 20230816 ✓** |
| `vkGetPhysicalDeviceCooperativeMatrixFlexibleDimensionsPropertiesNV`（论坛点名的崩溃路径 2） | **仅 ≥ 20250916** |
| `vkGetPhysicalDeviceCooperativeVectorPropertiesNV` | **仅 ≥ 20250916** |
⇒ **老引擎最多命中一条（KHR），新引擎命中两条（KHR + coopmat2）**；"换新引擎 = 接入更多崩溃路径"再次被独立确认。

**④ ncnn 官方发布说明从未提过 50 系**（一手否定事实）：逐版本核对 20241226 / 20250428 / 20250503 / 20250916 / 20260113 / 20260526 的 release notes，**没有一条出现 Blackwell / sm_120 / RTX 50 / 50 系**。
（顺带：ncnn 在 NVIDIA **≥570 驱动分支**上另有两个**已确认并已修**的缺陷：padding stall → 20250428 修；缺 `VK_EXT/KHR_robustness2` → 20250916 修；症状是卡死 / device lost(-4)。这两个我们已通过取较新引擎避开。）

**⑤ 驱动修复状态：一手来源里找不到"已修"的证据**
| 驱动 | 发布日期 | 一手来源里的 coopmat/Vulkan 修复条目 |
|---|---|---|
| 591.74 | 2026-01-05（报告 369162 所用） | 无（唯一 Vulkan 条目是 LG OLED 黑屏，无关） |
| 610.43.02/03 | 2026-05-30 / 07-14（报告 371808） | **仍复现** |
| **610.62** | **2026-06-16**（★ 5060 用户手上） | release note 里没有 coopmat 条目 |
| **616.64** | **2026-09-03**（★ 5070Ti 用户手上） | release note 原文 "Fixed General Bugs N/A" |
| 616.92 | 2026-09-09（最新） | 无 |
NVIDIA 官方 Vulkan beta 驱动更新日志（2026-08-28，Windows 597.11）里 coopmat 相关**只有新增扩展/性能条目，没有属性查询崩溃或一致性修复**。
**注意保留：GRD release note 只列部分修复项，"没写" ≠ "没修"**；完整 release notes PDF 在本机被 **HTTP 403 主机级封锁**，无法逐条核对。
⇒ **判定：必须在真机做最小复现才能定论。**

**⑥ 同机型独立印证**：ncnn issue **#6843**（2026-07-22，仍 open）—— 环境 **RTX 5060 + Intel 核显 + ncnn 20260526 + 驱动 595.95**，在 `create_gpu_instance()` 触发 **`0xC0000005`**，与本文机制高度一致。
（无维护者确认、无符号化栈 → 只能算**强线索**，不能算定论。）

**⑦ "Vulkan 层屏蔽"方案的完整评估**
- **Khronos Vulkan Profiles 层**确实能做这件事（官方文档原文："can **override the values returned by your application's queries of the GPU**"；`tweaking a single feature is easy`）——资源已核实（`Vulkan-Profiles` codeload zip HTTP 200 / 3,392,936 B；文档页 HTTP 200）。
- **但只关一个变体不够**：必须同时关 **KHR + NV coopmat1 + NV coopmat2 + NV coopvector**（论坛 371808 第 3 帖的实测正是"层生效了、程序仍崩"，因为没盖全）。
- **应用级先例（更稳）**：llama.cpp 用 `GGML_VK_DISABLE_COOPMAT` / `GGML_VK_DISABLE_COOPMAT2` 两个环境变量在自己的代码里跳过查询（源码已核实：`ggml-vulkan.cpp:6584/6592`，且只有 `coopmat_support` 为真才调 KHR 查询）。
- Vulkan loader **没有**"屏蔽设备扩展/特性位"的 API（`VK_LOADER_LAYERS_DISABLE` 只能禁层）；未发现专门屏蔽 coopmat 的第三方层。

**⑧ 一条重要的范围澄清**：本机制（设备初始化期崩溃 / 退 CPU）**与"带状全黑帧"无关** —— 黑帧另属一类（更接近 ncnn #6935 的 lightmode blob 复用，或 real-esrgan `-j` 的 proc 线程共享同一 Net/Pipeline，后者我们已在 4060 上实测复现并修掉了）。
⇒ **探测失败时"崩"与"黑帧"必须分开归因**（见 §3.1 第 3 项）。

### 1.4e 第三轮补遗：shader 编译失败不会自动降级 + 适用范围澄清
- **一手源码**：master `src/pipeline.cpp:444-447` —— `if (retc != 0) { NCNN_LOGE("compile_spirv_module failed %d", retc); return retc; }`；`gpu.cpp:6281` 打印 `compile spir-v module failed`。全树检索确认：**不存在"编译失败后改用非 cooperative-matrix 路径重试"的逻辑**；必须由调用方显式设 `opt.use_cooperative_matrix = false`，而 realesrgan / waifu2x / rife **都没有设**。
- **适用范围澄清（避免误判报错形态）**：已有的 coopmat shader 编译失败案例（ncnn #6769 `gemm_cm.comp`、#6577 `sdpa_fa_cm.comp`，后者环境为 RTX 4060 Laptop + 驱动 581.83）都属 **bf16 / gemm / FlashAttention** 路径；**realesrgan / waifu2x 的 RRDB（fp16 storage、无 bf16、无 SDPA）不走这两条 shader** → 我们的引擎在 Blackwell 上更可能命中**属性查询崩溃**，而不是 shader 编译失败。
- 另一条自我修正：ncnn **#6457**（`subgroup_size=0` 死循环）经复核是 **荣耀 V10 / Mali-G72（Android）** 问题，**与 NVIDIA/Blackwell 无关**（研究代理已自行改写报告表述）。

### 1.4f "带状黑帧"的独立归因，得到一手维护者表态支持（与 GPU 厂商无关）
- **nihui 本人（ncnn #4774，2023-08-16）**原文：
  > `Net::create_extractor` is thread-safe, so you can share the same Net instance among threads for creating any extractor instance. **Most other data structures are not thread-safe**
- **而 real-esrgan-ncnn-vulkan 的 `main.cpp`**：`jobs_proc` 个 proc 线程按 GPU 共享同一个 `RealESRGAN` 实例，除共享 `ncnn::Net` 外还共享 **`realesrgan_preproc` / `realesrgan_postproc` 两个 `ncnn::Pipeline` 对象**（在 `load()` 里创建一次）——`Pipeline` 正属"other data structures"，**该并发共享未获维护者背书**。
⇒ 这为我们那条实测（`-j 1:4:1` 出带状坏帧、`-j 1:1:1` 零坏帧）提供了机制解释，**且与显卡厂商无关**：**"黑帧"不该被归因给 Blackwell**，归给 `-j` 并发共享更站得住。

### 1.5 这条断言的现状判定
代码库里的原话是「50 系上 2022 版 ncnn 引擎会崩」。经上述核查：
- **现象方向对**：在"驱动宣称 coopmat"的 Blackwell 机器上，ncnn 引擎确实会在初始化时崩掉宿主进程。
- **归因错**：不是"2022 版"的问题，而是**所有版本的 ncnn 都受影响**；真凶是驱动。
- **待验证的关键点**：用户驱动版本是 **610.62 / 616.64**（由 `32.0.16.1062` / `32.0.16.1664` 折算），**晚于**报告里的 591.74。
  → **NVIDIA 是否已修复，未验证**。这决定 50 系到底能不能跑 ncnn。

### 1.6 兼容性可选方案（按可行性排序）

| 方案 | 可行性 | 代价 | 状态 |
|---|---|---|---|
| **A. 保持探测 + 落 ONNX DirectML** | 立即可用；且实测代价很小（补帧仅慢 1.25×、512 分块超分持平） | 无 | **推荐**（当前代码已如此） |
| B. 用户更新到已修复的驱动 | **截至 610.43.03（2026-07-14）确认仍有此 bug**；用户手上的 610.62 / 616.64 是否含修复**未知** | 无 | **待真机实测**（一次探测即可判定） |
| C. Vulkan 层屏蔽 coopmat（四个变体） | 机制上可行，但**已有一次"层生效仍未解决"的先例**（见 §1.4b） | 需原生 DLL + 层清单；**本机无任何 C/C++ 编译器，无法验证** | 待评估（优先级下降） |
| **C2. 换用无查询的老引擎（仅 waifu2x 槽位可行）** | **已验证**：waifu2x `20210521` 无任何合作矩阵代码，`-h` 与 2025 版逐行相同，实跑出图 | **-32% 速度**；输出非逐像素相同（10.6% 像素差、最大 6/255） | **可立即用的应急**；Real-ESRGAN / RIFE 槽位**无对应版本** |
| **D. 自行编译"关掉 coopmat"的 ncnn + 引擎** | 可行，且**上游已有同形范式可照抄**：`gpu.cpp:1265-1301` 针对 AMD RDNA2 把 KHR 与 NV 两个特性位双双置 `VK_FALSE`；**无任何 CMake 开关**（grep `cooperative` 0 命中），必须改源码 | 需 MSVC/MinGW + CMake；**Vulkan SDK 不必需**（`NCNN_SIMPLEVK=ON` + glslang 走子模块）；**本机无工具链** | 正解，但工程量大 |
| **D2. 替换 video2x 的 `ncnn.dll`** | video2x 把 ncnn 作为独立 DLL 发布（9.99 MB，内含 ncnn `20240820`）；换一个文件可能一次修好它的三个后端 | 需同版本 ncnn + 同 MSVC/CRT 才二进制兼容 | 未验证 |
| E. NVIDIA NGX / Optical Flow / RTX VSR | 见 §2.4 与 §5 | NGX 再分发需商业发布前书面通知；OF 无 Blackwell 声明 | 不建议作为主路径 |
| **E2. Anime4KCPP `ac_cli.exe`（CUDA，唯一结构上免疫此 bug 的超分引擎）** | 导入表只有 `nvcuda.dll` + `OpenCL.dll`，**无 `vulkan-1.dll`** → 完全不过 Vulkan | **GPLv3**；算法是 Anime4K/ACNet/ArtCNN **不是 ESRGAN**；CLI 约定不兼容；fatbin **只见 sm_89**（sm_120 能否 JIT 未验证） | 兜底候选 |
| F. 官方修复 | —— | —— | 唯一根治（时间未知） |

---

## 2. 速度：实测数据

### 2.1 超分（waifu2x-cunet2x，2x）—— 同机三方对照
条件：RTX 4060 Laptop 8GB；同一 ONNX 模型；512×512 分块。

| 运行时 | 512 分块 | 备注 |
|---|---|---|
| ncnn-Vulkan（批量稳态） | **118 ms/块** | 模型一致、口径一致 |
| ONNX **DirectML** | **115 ms/块** | 与应用同一 EP |
| ONNX **CUDA EP** | 135 ms/块（慢 17%） | 需随包 3357 MB |
| ONNX CPU | ~1192 ms/块 | 参考 |

### 2.2 DirectML 分块尺寸曲线（找显存悬崖）
| 分块 | 每帧 | 效率 |
|---|---|---|
| 256 | 22 ms | 337 ms/Mpix |
| 512 | 101 ms | 385 ms/Mpix |
| 768 | 212 ms | 360 ms/Mpix |
| 896 | 273 ms | 340 ms/Mpix |
| **1024** | **343 ms** | **327 ms/Mpix（最优点）** |
| **1280** | **7450 ms**（复测 12060 ms） | **4547~7361 ms/Mpix（崩塌 13.7~20×）** |
| 1536 / 1920 | **OOM 失败** | — |

→ 8GB 卡的可承受上限在 **1024 与 1280 之间**；应用当前上限 **768 偏低**。
→ 16GB 卡的可承受边长按"显存需求随像素近似线性"外推 ≈ 1024×√2 ≈ **1400~1536**（**估算，需真机确认**）。

### 2.3 补帧（RIFE v4.13，1920×1080）—— 同机对照
| 路径 | 每帧 | 说明 |
|---|---|---|
| **ncnn-Vulkan 批量稳态** | **97~98 ms/帧** | 33 帧输入 → 32 次插帧，两次复测一致 |
| **DirectML**（按 57~61 ms/Mpix × 2.07 Mpix） | **≈118~126 ms/帧** | 裸能力，未含应用开销 |
| CUDA EP（281 ms/Mpix × 2.07） | ≈582 ms/帧（**慢 4.6~6×**） | 且 1080 整图直接 RuntimeException |
| **应用实测**（5060，20260910 22:14 构建） | **282 ms/帧（3.55 fps）** | 比裸 DML 慢约 **2.2×** |

→ **补帧上 ncnn 只比 DirectML 快约 1.25×**；真正的缺口在应用侧（分块）。
→ 应用侧对应修复：RIFE 自适应分块（1080p 默认整图），已在 `c9c7747` 提交并实测比 512 分块快 **2.7~2.9×**。

### 2.4 被实测否掉的方案
- **CUDA EP 的显存策略修复**（`arena_extend_strategy=kSameAsRequested` + `gpu_mem_limit`）：
  1024 分块从 1353 ms **恶化到 2637 ms**；1080p 整图仍然失败。→ 该假设不成立。
- **补帧改用 NVIDIA 光流（Optical Flow / FRUC）**：NVOFA 公开能力表只有 Turing/Ampere/Ada，**无 Blackwell 列**；FRUC 预编译 DLL 再分发明文未授权。→ 不建议。

### 2.5 一条与速度无关但重要的生态风险
— 由研究代理一手确认：ONNX Runtime 官方把 **DirectML 标记为 sustained engineering**（原文：`DirectML is in sustained engineering. For new Windows projects, consider WinML instead.`），
且 **ORT 的 DirectML 包停在 1.24.4，而 ORT 主线已到 1.30.0**。
→ 长期维护性上这是个风险项；但**以本机实测速度看，CUDA EP 并不构成替代理由**（慢 4.6×、+3.36 GB）。
→ 若未来要迁移，理由应是"生态与可维护性"，且必须先解决 CUDA EP 在该模型上的性能问题。

### 2.6 关于 CUDA EP 的"官方支持"补证（结论不变，仅记录）
即使 CUDA EP 在**官方支持与可再分发**两侧都站得住，也**不构成迁移理由**（本机实测已否），但记录如下以备将来因"生态/可维护性"重估时使用：
- **sm_120 是被 CUDA 编译器版本守卫的**：ORT `v1.21.0/cmake/CMakeLists.txt:1566-1571`
  `if (CMAKE_CUDA_COMPILER_VERSION VERSION_GREATER_EQUAL 12.8) set(... -gencode=arch=compute_120,code=sm_120) # B series`
  → 机制上解释了"为什么必须 CUDA ≥ 12.8"。
- **cuDNN 官方支持矩阵确认 CC 12.0 = Blackwell**（`docs.nvidia.com/deeplearning/cudnn/**backend**/latest/reference/support-matrix.html`，逐字列出 `12.1 12.0` / `NVIDIA Blackwell`）。
  ⚠️ 采集陷阱：`.../cudnn/latest/reference/support-matrix.html` 返回 200 但**是 JS 渲染页、无表格内容**，不能当证据。
- CUDA 运行库再分发有 CUDA EULA 明文；但 **cuDNN 的具体可分发清单未能验证**；**NVIDIA 不发布 Windows TensorRT wheel**（`tensorrt-cu12-libs` 只有 sdist），TensorRT 运行库体积**未能验证**。
- 一条**被研究代理主动拒绝**的"证据"（值得记录的严谨做法）：官方 wheel 内 `onnxruntime_providers_cuda.dll` 尺寸 1.20.0=711 MB → 1.21.0=304 MB → 1.30.0=176 MB，恰在引入 sm_120 的边界骤降，方向与直觉相反、无法解释 → 代理**拒绝**把它当作 sm_120 存在的旁证，仅作记录。
- **TensorRT 路线的额外负面信号**：ORT 内置的 **TensorRT RTX EP 已被官方标记 deprecated**，改为外部 ABI 插件（`NVIDIA/TensorRT-RTX-EP-ABI`）；该页面对 `Blackwell`/`sm_120`/`RTX 50` **命中 0 → 50 系支持未声明** → 这是一条正在重构的路径，对"兼容性最好"不加分（不影响 §2.4 已给出的"TensorRT 不占优"结论）。
- 附带确认：**TensorRT-RTX（独立产品）**的 Windows zip 可**免登录**下载（实测 302→301→200，92,767,060 B / 88.47 MB），可作量级参照；但**主线 TensorRT 仍为登录门禁**，其体积维持"未能验证"（不猜）。

---

## 3. 建议（按优先级）

1. **不要换 ncnn 引擎、不要换成 CUDA EP**（两者都被实测或一手证据否掉）。
2. **50 系保持"真机探测 → 通过走 ncnn / 失败落 ONNX DirectML"**（当前代码已如此）。
   探测失败的**形态**是重要判据：**初始化即崩（访问违例/退出码异常）** ⇒ 就是本文的驱动 bug；**出图但黑帧** ⇒ 是另一类问题（与 `-j` 并发档有关，我们已修）。
3. **把 DirectML 路径做对**（不依赖任何驱动修复，收益确定）：
   - 分块上限按显存放宽：8GB→1024，16GB→约 1400~1536（**先在真机验证上限**）
   - 保持 512 以下的保守下限给核显/小显存
4. **补帧用已提交的 RIFE 自适应分块**（`c9c7747`）——这正是 50 系实测 282 ms/帧 与裸能力 ~120 ms/帧之间那 2.2× 的来源。
5. **兼容性根治只有两条**：驱动修复（等 NVIDIA）或自建工具链做 Vulkan 层 / 自编 ncnn（需先确认是否值得）。

---

### 3.1 可执行改动清单（把结论落成"改哪里 / 预期 / 怎么验"）

| # | 改动 | 位置 | 预期收益 | 验证方式 | 风险 |
|---|---|---|---|---|---|
| 1 | **ONNX 分块上限按显存放宽**：8GB→1024；12GB→约 1200；16GB→约 1400~1536（并保留核显/小显存 512 下限） | `EsrganOnnxService.cs:850` 的 `Math.Max(512, SafeRender.GetTileSize())` 与 `SafeRender` 的 `RenderPolicy` | 本机实测：768→1024 由 360 → 327 ms/Mpix（**约 9%**），且块数减少、重叠浪费下降 | 本机可验 8GB 档；16GB 档需真机（**必测**，因为 1280 在 8GB 上会崩） | 分块越大越接近显存悬崖 → 必须按显存分档，不能一刀切放大 |
| 2 | **补帧沿用已提交的 RIFE 自适应分块**（1080p 默认整图） | `RifeOnnxService.ResolveInterpTile`（`c9c7747`） | 实测比 512 分块快 **2.7~2.9×**；对应 50 系实测 282 ms/帧 → 裸能力约 118~126 ms/帧 的那个缺口 | 本机可验（已验：新默认与整图基线字节一致） | 大图仍走分块，接缝已在 `c6…` 一轮排查过 |
| 3 | **探测失败的提示按"形态"分诊**：若失败是**初始化即崩/访问违例**且在 Blackwell 上 → 明确告知"这是 NVIDIA 已知驱动缺陷（附论坛链接），非软件问题，已自动改用 ONNX DirectML"；若是**出图但黑帧** → 报另一类问题 | `EngineService.EnsureNcnnProbeAsync` 的失败分支与 `VideoService` 的告警文案 | 用户不再把驱动 bug 当成软件 bug；也避免我们继续背锅 | 文案级改动，本机可验日志 | 无 |
| 4 | **记录/更换来源不明的 RIFE 引擎** | `engines/rife/rife-ncnn-vulkan.exe`（2026-08-28，非官方发布） | 供应链可追溯性 | 找到出处并写入仓库说明；或退回官方 20221029 | 换回官方版会让 RIFE 少 `-q`/`-l` 两个参数（应用未用到），功能等价 |
| 5 | （深水区，需你拍板）**自建工具链**做 Vulkan 层（须同时屏蔽 KHR + NV + NV2 + NV vector 四个变体）或自编 ncnn | 新增 | 可能恢复 50 系 ncnn（约 1.25× 补帧收益 / 超分持平） | 需真机；本机无编译器无法验证 | 收益有限、工程量大、且已有"层生效仍失败"的先例 |

**优先级建议**：#3（零风险、立刻消除用户误解）→ #0（`vulkaninfo` 零成本诊断，可能一步定性）→ #1（确定收益、本机可验大半）→ #4（卫生问题）→ #2 若尚未部署 → #6（自编译，正解但重）→ #5 暂缓。

| # | 改动（续） | 说明 |
|---|---|---|
| 0 | **在 Blackwell 上跑 `vulkaninfo \| findstr /i cooperative`** | 零成本；若 `VK_NV_cooperative_matrix` 不在列表里，4 个"仅 NV 门"的引擎直接安全。若用户机器没有 vulkaninfo，就用我们自己的按引擎探测代替 |
| 6 | **自编译"关掉 coopmat"的 ncnn + 引擎**（正解） | 补丁 = 照抄 `gpu.cpp:1265-1301` 的 AMD RDNA2 范式，在 `:1301` 之后插入两行把 KHR 与 NV 特性位双双置 `VK_FALSE`；源码布局 `src/CMakeLists.txt`、ncnn 是 `src/ncnn` 子模块；`cmake -S src -B build -G "Visual Studio 17 2022" -A x64`；rife/waifu2x 有 `USE_SYSTEM_NCNN` 可复用单独编好的 ncnn |
| 7 | **应急：waifu2x 槽位换 2021 版**（已验证无查询、CLI 完全兼容、实跑正常） | 只解决 waifu2x；代价 -32% 速度 + 轻微像素差异；Real-ESRGAN / RIFE 槽位无此选项 |

---

## 4. 仍未完成 / 未验证（不得当结论）

- **【已在本轮收口，不再是未知】** ~~ncnn 有没有 CMake 开关~~ → **没有**（grep `cooperative` 0 命中；`opt.use_cooperative_matrix` 管不到设备初始化）；~~上游是否已有修复 commit~~ → **没有，反而新增查询**；~~"Vulkan 层"是否可行~~ → **可行但必须同时屏蔽四个变体，且已有"层生效仍崩"先例**。
- **NVIDIA 是否已在 610.62 / 616.64 修复该 coopmat 缺陷** —— **未验证**（一手来源里找不到"已修"的证据，见 §1.4d ⑤；但 GRD 说明只列部分修复项，"没写"≠"没修"）。**必须在真机做最小复现才能定论。** 已确认的是：到 610.43.03（2026-07-14）仍复现。
- **"崩"与"黑帧"必须分开归因**：本机制 = 初始化期崩溃 / 退 CPU；**带状全黑帧不属于本机制**（属另一类，见 §1.4d ⑧）。探测日志里这两者的处置不同。
- 50 系真机上 ncnn 引擎的**实际失败形态**（崩 vs 黑帧）—— 未验证，需一次真机探测。
- 16GB 卡的 DML 分块上限 —— **估算**，需真机实测。
- 本机**无任何 C/C++ 编译器与 Vulkan SDK** → 方案 C/D 在本机**无法验证**。
- ORT CUDA EP 的 sm_120 运行确认：代理在官方 wheel 里找到 33 处 `-arch sm_120a`，但 ELF cubin 头扫描未复现 120 值，差异未解释；且**本机实测速度已否定该方案的现实价值**。
- cuDNN 的可再分发清单、TensorRT 运行库体积、NGX/RTX Video SDK 的许可细节 —— 未验证（后者下载需登录）。
- 速度结论只覆盖 `waifu2x-cunet2x` 与 `rife49` 两个模型；`RealESRGAN_x4plus` 未测。
