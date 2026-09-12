# Real-ESRGAN ncnn-Vulkan 引擎在 NVIDIA Blackwell（RTX 50 系 / sm_120）上的可用性研究

> 状态：**本轮研究已完成，含对上一轮核心结论的修订**。
> 目标机型：RTX 5060 Laptop 8GB（Blackwell sm_120）+ AMD/Intel 核显。
> 本机实验台：RTX 4060 Laptop，驱动 **572.83**，Windows 11 26200（**无 Blackwell，无 C/C++ 编译器**）。
> 本文所有"本机实测"均指 4060 机器；**所有 Blackwell 相关结论都未在 Blackwell 实机验证**，已在 §7 逐条列出。
> 一手来源为官方仓库源码 / Release 资产 / 官方 API / NVIDIA 官方论坛；二手来源已标注。

---

## 0. 结论摘要（先看这段）

| # | 结论 | 证据强度 |
|---|---|---|
| 1 | **"2022 版引擎在 50 系跑不起来"的直接假说不是 cooperative matrix**，而是**引擎太旧、缺 `VK_EXT_robustness2`**。ncnn 官方 commit 原文：`fix hangs with NVIDIA >565 drivers`，PR 正文："It seems it was enabled by default in <=565 drivers, but in >=570 you need to explicitly enable it." | **一手**（ncnn commit + PR #6296 + issue #5920） |
| 2 | **二进制指纹把这条假说钉死了**：我们随包的 `realesrgan-ncnn-vulkan.exe`（2022）**完全不含** `VK_EXT_robustness2`/`VK_KHR_robustness2`/`robustBufferAccess2`/`nullDescriptor` 任何一个字符串；而 **2025 版 waifu2x 四者全有**。这正是"realesrgan 探测失败、waifu2x 能用"的唯一二进制差异方向。 | **本机实测**（两轮独立扫描，见 §3.2） |
| 3 | **而 cooperative matrix 方向对不上现象**：随包的 2022 realesrgan **只有 `VK_NV_cooperative_matrix` (v1) 门**，**既没有** NVIDIA 论坛点名的 `...PropertiesKHR`，**也没有** `...FlexibleDimensionsPropertiesNV`（NV2）。它**结构上无法**触发那两个已确认的崩溃。反倒是 waifu2x 2025 **四个门全有、四条查询全带**——若 coopmat 是主因，**waifu2x 该先崩**，但它没崩。 | **本机实测 + 一手源码** |
| 4 | **上游确实没有"新引擎"可换**。`xinntao/Real-ESRGAN-ncnn-vulkan` 最后一次 commit = **2022-04-24**，最后 release = v0.2.0；`nihui/realsr-ncnn-vulkan` = **2022-07-28**。 | **一手**（GitHub API commits/releases） |
| 5 | **Upscayl 的 `upscayl-bin` 不是"新引擎"**。它的 release 日期是 2025-12-07，但它把 ncnn 子模块**钉死在 2022-04-21 的 commit** `6125c9f4`，指纹与 2022 版 realesrgan **逐项相同**（仅 NV v1 门、无 robustness2）。换它**不会有任何改善**。 | **一手**（子模块 sha + 该 sha 的 gpu.cpp + 二进制指纹） |
| 6 | **有一条现成、已验证、MIT 许可、能加载 Real-ESRGAN 模型的替代引擎**：`nihui/realsr-ncnn-vulkan` 20220728。本机实测：它**能加载 `realesrgan-x4plus` 权重**（把 `.param/.bin` 改名为 `x4.*`、目录名含 `models-DF2K`），输出与官方 realesrgan **逐字节相同**（SHA256 一致）。代价：**许可 MIT ✓**，但**指纹与 2022 realesrgan 同类**，因此在 Blackwell 上**同样不解决问题**——价值仅在于"模型可互换"这一发现。 | **本机实测**（跑通 + 哈希对比） |
| 7 | **video2x / upscayl / vs-mlrt 全部是强 copyleft**：video2x = **AGPL-3.0**、upscayl-ncnn = **AGPL-3.0**、vs-mlrt = **GPL-3.0**。本项目**不能引入**，直接出局。 | **一手**（GitHub API license + LICENSE 文件） |
| 8 | **可执行方案只有一条：自编 ncnn + Real-ESRGAN 前端**。而"缺 robustness2"比"coopmat 查询"**更容易修**——上游 ncnn master **已经带修复**，只需 `git clone` 最新 ncnn + 3 行兜底开关。 | **一手源码 + 上游补丁范式** |

**一句话**：上一轮的"coopmat 是根因"方向**被本轮证据显著削弱**；真正的第一个嫌疑是**引擎年份太旧**（缺 `VK_EXT_robustness2`）。而这恰好是**好消息**——因为"新 ncnn 已经有修复"，自编方案不用发明补丁，只要 `clone 最新 ncnn` 即可，比"必须绕开 coopmat 查询"简单得多。

---

## 1. 已核实的事实基线（对任务背景的核实结果）

### 1.1 上游版本与日期 —— 全部核实

| 上游仓库 | 最后 commit | 最后 release | 一手来源 |
|---|---|---|---|
| `xinntao/Real-ESRGAN-ncnn-vulkan` | **2022-04-24** `update int to string` | **v0.2.0 / 2022-04-24T09:15:51Z** | `api.github.com/repos/.../commits`、`/releases` |
| `nihui/realsr-ncnn-vulkan` | **2022-07-28** `update common utility function` | **20220728 / 2022-07-28T14:17:19Z** | 同上 |
| `nihui/waifu2x-ncnn-vulkan` | —— | **20250915 / 2025-09-15T11:32:12Z**（次新 20250802、20250504） | 同上 |
| `nihui/rife-ncnn-vulkan` | —— | 20221029 / 2022-10-29（**自 2022 冻结至今**） | 同上 |
| `upscayl/upscayl-ncnn` | **2026-04-11** | **20251207-174704 / 2025-12-07T17:53:06Z** | 同上 |
| `Tencent/ncnn` | **2026-09-10**（活跃） | —— | 同上 |
| `nihui/ncnn`（二进制发布方） | —— | **20260526 / 2026-05-26T09:28:18Z** | 同上 |

⇒ **任务背景里的日期/版本全部正确**：realesrgan = v0.2.0 2022-04-24 ✓；waifu2x = 20250915 ✓；上游 realesrgan 系已停止更新 ✓。

### 1.2 随包二进制就是官方原件（无篡改）

本机下载官方 `realesrgan-ncnn-vulkan-v0.2.0-windows.zip`（**2,159,606 B**，与 API 报的 asset size 逐字节相符；`zipfile.testzip()` = OK），逐文件哈希比对：

| 文件 | SHA256 | 结论 |
|---|---|---|
| 官方 v0.2.0 exe（6,161,408 B） | `07e49f7cbb4ede01ae4dd4c399d3a7e5846e3d2085c3128eff881e55cb7b1a0c` | —— |
| `alh-pro/engines/realesrgan/realesrgan-ncnn-vulkan.exe`（6,161,408 B） | `07e49f7cbb4ede01ae4dd4c399d3a7e5846e3d2085c3128eff881e55cb7b1a0c` | **IDENTICAL** |

⇒ 随包的 realesrgan 引擎**是官方 v0.2.0 原件，未被改动**。（顺带：随包的 `vcomp140.dll` 与官方包内同名文件**大小不同** 181,680 vs 182,704 / 207,736——是另一来源的 VC 运行库，不影响引擎本体。）

### 1.3 任务给出的"根因方向"复核

上一轮结论为"ncnn 在 Blackwell 上查询 cooperative matrix 属性/特性会崩"。本轮复核：

| 上一轮断言 | 本轮复核结果 |
|---|---|
| `opt.use_cooperative_matrix=false` 只影响算子层，拦不住设备初始化 | **✓ 确认**。`src/gpu.cpp:1530` 的条件是 `support_VK_KHR_cooperative_matrix && queryCooperativeMatrixFeatures.cooperativeMatrix`，两变量都来自驱动查询，**不读 `opt`** |
| 没有任何 CMake 开关 / 环境变量能跳过 | **✓ 确认**。全树 `CMakeLists.txt + *.cmake` 检索 `cooperative` → **0 命中**；`src/*.cpp,*.h` 检索 `getenv` → 只有 3 处，全在 `src/simplevk.cpp:103/106/116`（`VK_ICD_FILENAMES` / `VK_DRIVER_FILES` / `NCNN_VULKAN_DRIVER`），**与合作矩阵无关** |
| 现成的"关机点"= `gpu.cpp:1265-1301`（AMD RDNA2 范式） | **✓ 确认**。原文 `// emulated cooperative matrix on amd rdna2 is slow`，块内两行 `queryCooperativeMatrixFeatures.cooperativeMatrix = VK_FALSE;` / `queryCooperativeMatrixFeaturesNV.cooperativeMatrix = VK_FALSE;` |
| 上游没有修复，反而扩大探测面 | **✓ 确认并加强**。本地 ncnn master（2026-09-10 快照）已有 **5 条**查询（比上一轮又多了 `...Properties2EXT`，PR #6898）与 **5 个门** |

**但**：这些都不构成"2022 版 realesrgan 崩掉"的**充分**解释——见 §2。

---

## 2. 现象与假说的冲突（本轮最重要的发现）

### 2.1 冲突点

| 引擎 | 有 `VK_KHR_cooperative_matrix` 门？ | 有 `VK_NV_cooperative_matrix2` 门？ | 发 `...PropertiesKHR` 查询？ | 发 `...FlexibleDimensionsPropertiesNV` 查询？ | 真机 | 结果 |
|---|---|---|---|---|---|---|
| realesrgan（2022，随包） | **否** | **否** | **否** | **否** | RTX 5060 | **探测失败** |
| waifu2x（2025，随包） | **是** | **是** | **是** | **是** | RTX 5060 | **未确认（可能可用）** |

NVIDIA 官方论坛两个帖点名的崩溃路径是 **`vkGetPhysicalDeviceCooperativeMatrixPropertiesKHR`**（帖 369162 崩溃 1）与 **`vkGetPhysicalDeviceCooperativeMatrixFlexibleDimensionsPropertiesNV`**（帖 369162 崩溃 2 / 帖 371808）。

- 2022 版 realesrgan **两个都不发**——它唯一发的合作矩阵查询是 `vkGetPhysicalDeviceCooperativeMatrixPropertiesNV`（**NV v1**，源码 `gpu.cpp:1604/1617`），而该查询**从未被那两个论坛帖点名**。
- 2025 版 waifu2x **两个都发**。

⇒ **"coopmat 属性查询"这一机制无法解释"发得多的那个反而能用、不发的那个反而失败"**。这不是否证 coopmat 在 Blackwell 上确实是驱动缺陷（论坛证据是真的），而是说：**它不是这台 5060 上 realesrgan 探测失败的原因**。

> 若要保留 coopmat 假说，唯一自洽的可能形式是：**驱动只在"启用 NV v1 特性位建设备"这一步失败**（帖 371808 的 `vkCreateDevice` → `VK_ERROR_INITIALIZATION_FAILED`），而 2022 版恰好会启用它（`gpu.cpp:4145-4146` push `VK_NV_cooperative_matrix` 进 `enabledExtensions`）。**这条可以在真机一条命令证伪/证实**，见 §7 待实测第 1 条。

### 2.2 更强的候选根因：缺 `VK_EXT_robustness2`

**ncnn 官方 commit（一手）**：

> `34429145d4` — **2025-09-09** — `discover VK_EXT_robustness2 and VK_KHR_robustness2, fix hangs with NVIDIA >565 drivers (#6296)`
> <https://github.com/Tencent/ncnn/pull/6296>

**PR #6296 正文原文（一手）**：

> This patch fixes #5920 for me. All other workarounds may work for yolo, but our private models still crash. Enabling robustness extension fixes NVIDIA inference for all models without any additional workarounds. **It seems it was enabled by default in <=565 drivers, but in >=570 you need to explicitly enable it.**

**关联 issue #5920（一手，2025-02-25 开、2025-09-09 由该 PR 关闭）** 标题译文：

> "在 AMD 2024 年 7 月以后的驱动上没效果，在 2025 年的 N 卡驱动上推理就卡死了，用老版本的显卡驱动就正常"
> <https://github.com/Tencent/ncnn/issues/5920>

**ncnn master 的实现（一手源码，本地快照）**：

| 位置 | 内容 |
|---|---|
| `src/gpu.cpp:830-831`、`872-873` | `strcmp(exp.extensionName, "VK_KHR_robustness2")` / `"VK_EXT_robustness2"` → 置 `support_VK_*` |
| `src/gpu.cpp:1115-1118`、`1444-1447` | 把 `VkPhysicalDeviceRobustness2FeaturesKHR` / `...PropertiesKHR` 接进 pNext 链 |
| `src/gpu.cpp:4085-4086`、`4127-4128` | 把 `VK_KHR_robustness2` / `VK_EXT_robustness2` push 进 `enabledExtensions` |
| **`src/gpu.cpp:5682-5687`** | **`DD_APPEND_FEATURE(robustBufferAccess2)` / `(robustImageAccess2)` / `(nullDescriptor)` —— 真正"显式启用"的地方** |

⇒ **2022-04-24 构建的 realesrgan 早于 2025-09-09 这个修复 3.4 年**，二进制里**一个 robustness2 相关字符串都没有**，因此在 **570 及以上**的 NVIDIA 驱动上正是 PR 正文描述的那种"inference 卡死/崩"的状态。**用户的 610.62（5060）与 616.64（5070Ti）都远在 570 之上。**

---

## 3. 二进制指纹：方法与实测结果

### 3.1 方法（可复用的可信判据）

**判据一：扩展名"门"字符串（主判据）**
ncnn 靠**精确匹配扩展名字符串**决定分支：`strcmp(exp.extensionName, "VK_KHR_cooperative_matrix") == 0` → 置 `support_VK_*`；之后**只有 `support_VK_*` 为真时才发查询**。所以：

- 若要判断"这个二进制会不会发 coopmat 属性查询"，**扫扩展名字符串比扫查询函数名更准**。
- 但 ncnn 同时用 `vkGetInstanceProcAddr(g_instance, "vkGetPhysicalDevice...")` 按**函数名**取名，所以**函数名字符串也必然在二进制里**——两者应一致，不一致说明该版本没那段代码。

**判据二：`VK_EXT_robustness2` / `robustBufferAccess2` / `nullDescriptor` 字符串**
这是判断"是否含 PR #6296 修复"的**充分指纹**（四个一起扫，任缺即旧）。

**判据三：PE 时间戳**
`IMAGE_FILE_HEADER.TimeDateStamp` = 真实构建时间。本机 6 个引擎全部可读，与 release 日期吻合到小时级。

**判据四：能不能真的跑**
本机是 Ada（sm_89），**不能**验证 Blackwell 行为，但能验证：进程是否起得来、能否加载指定模型、输出是否逐像素可复现。**指纹只能给"会不会走某条代码路径"的预判，不能给"在 sm_120 上是否可用"的结论。**

### 3.2 实测结果（本机扫描，全部 SHA256 已记录）

| 引擎 | 文件大小 | SHA256 | PE 时间戳（UTC） | `KHR_cm` | `NV_cm2` | `NV_cm(v1)` | `NV_cvec` | 查询函数名数量 | **`robustness2`** |
|---|---|---|---|---|---|---|---|---|---|
| **`realesrgan-ncnn-vulkan.exe`**（随包，=官方 v0.2.0） | 6,161,408 | `07e49f7c…1b1a0c` | **2022-04-24 09:07:15** | ✗ | ✗ | **✓** | ✗ | 1 种（NV v1，×2） | **✗ 全无** |
| `realsr-ncnn-vulkan.exe`（20220728 官方） | 6,104,064 | `9ea47d64…c3d369` | **2022-07-28 13:59:11** | ✗ | ✗ | **✓** | ✗ | 1 种（NV v1，×2） | **✗ 全无** |
| `upscayl-bin.exe`（20251207 官方） | 7,763,968 | `294d31be…70abe60` | **2025-12-07 17:52:47** | ✗ | ✗ | **✓** | ✗ | 1 种（NV v1，×2） | **✗ 全无** |
| `waifu2x-ncnn-vulkan.exe`（20250915 随包） | 5,094,912 | `7ef2efc4…87a733d` | **2025-09-15 11:18:48** | **✓** | **✓** | **✓** | **✓** | 4 种，各 ×2 | **✓ 全有** |
| `rife-ncnn-vulkan.exe`（2026-08-28，非官方） | —— | —— | —— | ✗ | ✗ | ✓ | ✗ | 1 种 | ✗ |
| `rife-ncnn-vulkan_old2022_backup.exe` | —— | —— | —— | ✗ | ✗ | ✓ | ✗ | 1 种 | ✗ |

**`VK_*` token 总数**：2022 realesrgan = **26**；2022 realsr = **26**；2025 upscayl = **26**（**这三者 token 集合逐项相同**）；waifu2x 2025 = **53**。

抽查明细：
- 2022 realesrgan：`VK_NV_cooperative_matrix` ×1、`vkGetPhysicalDeviceCooperativeMatrixPropertiesNV` ×2；`VK_EXT_robustness2` = **0**、`VK_KHR_robustness2` = **0**、`robustBufferAccess2` = **0**、`nullDescriptor` = **0**。
- 2025 waifu2x：`VK_EXT_robustness2` ✓、`VK_KHR_robustness2` ✓、`robustBufferAccess2` ✓、`nullDescriptor` ✓，另有 `VK_KHR_shader_bfloat16`、`VK_EXT_shader_float8`、`VK_KHR_shader_integer_dot_product` 等新一代必需项。
- **2025 upscayl 与 2022 realesrgan 的指纹逐项一致**——这是"换 upscayl 没用"的直接证据。

### 3.3 Upscayl `upscayl-bin` 的真实底座（一手）

`upscayl/upscayl-ncnn` 的 release 晚（2025-12-07），但它的 ncnn 是**子模块**，且**钉死在 2022 年**：

- `.gitmodules`（jsdelivr 取到）：`src/ncnn` → `git@github.com:Tencent/ncnn.git`
- `api.github.com/repos/upscayl/upscayl-ncnn/contents/src` → `ncnn` 子模块 **sha = `6125c9f47cd14b589de0521350668cf9d3d37e3c`**
- `api.github.com/repos/Tencent/ncnn/commits/6125c9f4…` → **date = 2022-04-21**，message = `add tips for disabling android ui and setting cpu/gpu performance mode`
- 取该 sha 的 `src/gpu.cpp`（168,858 B）逐串检查：`VK_KHR_cooperative_matrix` = **False**、`VK_NV_cooperative_matrix2` = **False**、`VK_NV_cooperative_vector` = **False**、`VK_EXT_robustness2` = **False**、`VK_KHR_robustness2` = **False**、`vkGetPhysicalDeviceCooperativeMatrixPropertiesKHR` = **False**

⇒ **upscayl 的 release 是"2022 年 ncnn + 2025 年前端 + 新算子"**，不是一个新引擎。指纹与 2022 realesrgan 相同也就对上了。

### 3.4 指纹方法的**能力边界**（必须明说）

| 能断言 | 不能断言 |
|---|---|
| 该二进制**是否会走** coopmat / robustness2 代码路径 | 在 sm_120 上**是否真的会崩** |
| 构建年份上下界（PE 时间戳 + 新扩展字符串） | 驱动是否已修该缺陷 |
| 两个二进制是否**同一批 ncnn 源码** | 具体崩在哪个 API（需真机复现） |
| 官方发布是否被篡改（哈希比对） | 性能 |

---

## 4. 现成替代引擎逐项核查

### 4.1 核查表

| 候选 | 能加载 Real-ESRGAN `.param/.bin`？ | 一手下载地址 | 版本 / 构建日期 | 内含 ncnn 版本（可判定部分） | Blackwell 预判 | 许可 | 结论 |
|---|---|---|---|---|---|---|---|
| **`upscayl-bin`** | ✓（同源前端） | <https://github.com/upscayl/upscayl-ncnn/releases/download/20251207-174704/upscayl-bin-20251207-174704-windows.zip>（2,421,760 B） | tag `20251207-174704`，发布 2025-12-07 | 子模块 sha `6125c9f4` = **2022-04-21** | **与 2022 realesrgan 同类，无改善** | **AGPL-3.0** | **出局**（许可 + 无改善） |
| `nihui/realsr-ncnn-vulkan` | **✓ 本机实测可加载**（§4.2） | <https://github.com/nihui/realsr-ncnn-vulkan/releases/download/20220728/realsr-ncnn-vulkan-20220728-windows.zip>（64,005,701 B） | tag `20220728`，发布 2022-07-28 | 2022-07 期 ncnn（仅 NV v1 门、无 KHR/robustness2） | **同类，不解决问题** | **MIT ✓** | 仅作"模型可互换"证据 |
| `video2x`（k4yt3x） | 自带 realesrgan 后端（libvideo2x），**但不是可单独提取的 CLI+`.param` 引擎** | <https://github.com/k4yt3x/video2x/releases/download/6.4.0/video2x-windows-amd64.zip>（196,989,277 B） | 6.4.0 / **2025-01-24**（最新） | 上一轮曾记录其 `ncnn.dll` 内含 ncnn `20240820`（**早于** PR #6296，故同样缺 robustness2） | **同类，不解决问题** | **AGPL-3.0** | **出局** |
| `nihui/ncnn` release assets | ✗ **不含任何 realsr/realesrgan 前端**；只有 `ncnn-<date>-windows-vs20xx[-shared].zip`（库 + `benchncnn` 等工具） | <https://github.com/nihui/ncnn/releases/tag/20260526> | 最新 **20260526 / 2026-05-26**（42 个 asset，含 `ncnn-20260526-windows-vs2022.zip` 70,623,733 B、`...-full-source.zip` 23,279,161 B） | **最新 ncnn（含 robustness2 修复）** | **库本身没问题，缺的是前端** | **BSD-3-Clause ✓** | **有用的构件**，非成品引擎 |
| 第三方 fork（给 50 系打过补丁的 realesrgan-ncnn-vulkan） | —— | —— | —— | —— | —— | —— | **未找到**（见 §7） |
| `vs-mlrt` / TensorRT | 需 `.onnx`→TensorRT engine 转换，**无 Real-ESRGAN ncnn CLI** | <https://github.com/AmusementClub/vs-mlrt/releases/tag/v16.2.test1>（资产含 `vsmlrt-windows-x64-tensorrt.7z.001/002` ≈ 2.7 GB） | v16.2.test1 / 2026-08-05 | —— | 需转换 + 体积大 | **GPL-3.0** | **出局**（许可 + 转换成本） |
| Anime4KCPP | ✗ 算法是 Anime4K/ACNet，**不是 ESRGAN** | —— | —— | —— | —— | **GPL-3.0** | **出局**（本项目已明确排除 GPL） |

### 4.2 本机实测：realsr 引擎**能够**加载 Real-ESRGAN 权重（意外发现）

我在 4060 上做了这个实验，因为发现两者的 `.param` 图**结构几乎相同**：

| 模型 | 头部 | 层数 | 层类型集合 |
|---|---|---|---|
| `realesrgan-x4plus.param` | `7767517` / `999 1782` | 999 | `Input, Convolution, Split, Concat, Eltwise, Interp, BinaryOp` |
| `realsr …/models-DF2K/x4.param` | `7767517` / `999 1782` | 999 | **同上（完全相同）** |

层名/层类型序列逐项比对：999 条中**仅 12 条差异**，且都是 `Resize_*` / `Conv_*` 的**编号错位**（子图层命名不同），**不是算子或计算图差异**。

**实测过程与结果**：

| 尝试 | 命令要点 | 结果 |
|---|---|---|
| 1 | `realsr-ncnn-vulkan.exe … -n realesrgan-x4plus` | **失败**：realsr **不支持 `-n`**（打印 usage，exit -1） |
| 2 | 把 `realesrgan-x4plus.param/bin` 改名成 `x4.param/x4.bin`，`-m <dir>` | **失败**：`unknown model dir type` —— realsr 对**目录名**做子串匹配（源码：`model.find("models-DF2K") != npos`） |
| 3 | 目录改名 `models-DF2K-realesrgan`，内含 `x4.param`/`x4.bin`（= realsr 硬编码的文件名） | **成功**：`exit=0`，输出 987,356 B；日志见 `[0 NVIDIA GeForce RTX 4060 Laptop GPU] queueC=2[8] … subgroup=32` |

**输出等价性（逐字节）**：

| 引擎 | 输出 | SHA256 |
|---|---|---|
| 随包 2022 realesrgan（`-n realesrgan-x4plus -s 4`） | 880×880 PNG，987,356 B | `54770F41E95725FD74F1C44028A7B91214C9A91737D17DBCABD43FFC172F363C` |
| `upscayl-bin` 20251207（`-z 4`） | 880×880，987,356 B | `54770F41…2F363C` |
| **`realsr-ncnn-vulkan` 20220728（加载 realesrgan-x4plus 权重）** | 880×880，987,356 B | `54770F41…2F363C` |

⇒ **三者输出逐字节相同**。这说明 realesrgan-ncnn-vulkan 前端与 realesr-ncnn-vulkan 前端（在 x4plus 模型 + `-s 4` 下）在本机是**位精确等价**的。

**但要泼冷水**：这个发现**不提供 Blackwell 上的解法**——realsr 20220728 的指纹与 2022 realesrgan **逐项相同**（仅 NV v1 门、无 robustness2），所以它**大概率同样在 5060 上失败**。它的价值在于：**证明 Real-ESRGAN 的 `.param/.bin` 可以被别的 ncnn 前端直接吃**——这对自编方案（§5）是一个有用的自由度（万一上游前端编译不顺，还有 MIT 的 realsr 前端可改）。

### 4.3 顺带核实的两个 CLI 兼容性事实

1. **upscayl-bin 的 `-s` 语义与 upstream 不同**（源码 `src/main.cpp:204-205`）：
   - upstream：`-s scale`（放大倍数，2/3/4）
   - upscayl：`-z model-scale`（按模型缩放）+ `-s output-scale`（自定义输出尺寸）
   ⇒ 若将来要用 upscayl 后端，**参数名必须重映射**。
2. **upscayl-bin 的 `-h` 比 upstream 多 `-r/-w/-c`**（resize/width/compress），其余 `-i/-o/-t/-m/-n/-g/-j/-x/-f/-v` 一致。

---

## 5. 自制方案（重点，可执行步骤）

### 5.1 先定"改什么"：两个候选假说，一次编译同时消掉

因为**根因未在真机定论**（§2），最经济做法是**一次编译把两个候选同时消掉**，然后一次真机运行即可"双盲"判定。

**补丁 A（必做）：保留并确认 robustness2 —— 其实不用改**
只要用 **≥ 2025-09-09** 的 ncnn（即所有 `nihui/ncnn` release ≥ `20250916`，或 `Tencent/ncnn` master），`VK_EXT/KHR_robustness2` 就已实现并在 `gpu.cpp:5682-5687` 显式启用。**这是"升级引擎"真正会带来的收益**。
⇒ **不需要写任何代码。**

**补丁 B（推荐一并做）：兜底关掉全部合作矩阵门**
在 ncnn 源码里**一处**即可关掉全部 5 条查询。位置：

- `src/gpu.cpp` 的 `GpuInfoPrivate::query_extension()` 内部，**`if (support_VK_KHR_robustness2) { … }` 块之后（本地快照 `:996-1000`）、函数 `return 0;`（`:1002`）之前**。

```cpp
    // ==== ALH Pro Blackwell workaround: disable NV-direct cooperative matrix probing ====
    // 该驱动在 cooperative matrix 属性查询上会访问越界（NVIDIA forums 369162 / 371808）。
    // 清零这些 "门" 即可让后面所有查询分支不再进入（它们都以 support_VK_* 为条件）。
    support_VK_KHR_cooperative_matrix       = 0;
    support_VK_EXT_cooperative_matrix_maintenance1 = 0;
    support_VK_NV_cooperative_matrix        = 0;
    support_VK_NV_cooperative_matrix2       = 0;
    support_VK_NV_cooperative_vector        = 0;
    // ================================================================================
```

**为什么这一处就够（逐条给出下游条件，一手行号，本地 master 2026-09-10 快照）**：

| 下游用途 | 行号 | 条件 | 置 0 后效果 |
|---|---|---|---|
| 特性链挂 `CooperativeMatrixFeaturesKHR` | `1053-1057` | `if (support_VK_KHR_cooperative_matrix)` | **不再挂**（`queryCooperativeMatrixFeatures` 保持 0） |
| 特性链挂 `...Maintenance1FeaturesEXT` | `1063-1067` | 同上门 | 不再挂 |
| 特性链挂 `CooperativeMatrixFeaturesNV` | `1073-1077` | `if (support_VK_NV_cooperative_matrix)` | **不再挂** |
| 特性链挂 `CooperativeMatrix2FeaturesNV` | `1083-1087` | `if (support_VK_NV_cooperative_matrix2)` | 不再挂 |
| 特性链挂 `CooperativeVectorFeaturesNV` | `1093-1097` | `if (support_VK_NV_cooperative_vector)` | 不再挂 |
| 属性链挂 `CooperativeMatrix2PropertiesNV` | `1474-1478` | 同上 | 不再挂 |
| 属性链挂 `CooperativeVectorPropertiesNV` | `1484-1488` | 同上 | 不再挂 |
| **KHR 两段式查询（崩点 1）** | **`1530-1533` / `1546`** | `support_VK_KHR_cooperative_matrix && query…cooperativeMatrix` | **永不执行** |
| **NV v1 两段式查询** | **`1601-1604` / `1617`** | `support_VK_NV_cooperative_matrix && queryCooperativeMatrixFeaturesNV.cooperativeMatrix` | **永不执行** |
| `...Properties2EXT` 查询 | `1669` / `1705` / `1722` | `support_VK_EXT_cooperative_matrix_maintenance1 && …` | 永不执行 |
| **NV2 flexible-dims 查询（崩点 2）** | **`1734-1737` / `1750`** | `support_VK_NV_cooperative_matrix2 && …FlexibleDimensions` | **永不执行** |
| NV cooperative-vector 查询 | `1759-1762` / `1775` | `support_VK_NV_cooperative_vector && …cooperativeVector` | 永不执行 |
| push 进 `enabledExtensions`（帖 371808 的 `vkCreateDevice` 失败源） | `4059-4060`、`4145-4150`、`4115-4116` | `info.support_VK_*()` | **不再 push** |
| 特性启用（`vkCreateDevice` 的 pNext 链） | `5700-5736` | 同上 | 不再启用 |
| `get_heap_budget` 相关 | `5992`、`5999`、`5293` | 同上 | 不再走 |

⇒ 这**同时**消掉：①两条论坛点名的崩溃查询；②`vkCreateDevice` 带 coopmat 特性位失败；③所有 NV 变体。**代价**：失去 coopmat 加速（对 RRDB 类超分**基本无影响**——上一轮已确认 realesrgan/waifu2x 的算子路径**本就不走** coopmat gemm/SDPA 那两条 shader）。

> **上游同形范式（可作补丁正当性依据）**：`src/gpu.cpp:1265-1301`，AMD RDNA2 特判，注释 `// emulated cooperative matrix on amd rdna2 is slow`，块内 `queryCooperativeMatrixFeatures.cooperativeMatrix = VK_FALSE;` / `queryCooperativeMatrixFeaturesNV.cooperativeMatrix = VK_FALSE;`。
> **注意差别**：RDNA2 那个补丁只清**特性位**（够用，因为查询条件里有特性位），且**只覆盖 KHR+NV v1 两个**；本方案清**门**，覆盖全部 5 个，且位置更靠前，`enabledExtensions` / 特性链也一并干净。

### 5.2 工具链与依赖（Windows x64）

| 组件 | 结论 | 依据 |
|---|---|---|
| 编译器 | **MSVC（Visual Studio 2022，含 "Desktop development with C++"）** 或 MinGW-w64 均可。**上游官方 Windows 资产就是用 MSVC 构建的**：`release.yml` 的 windows job 用 `cmake -A x64 ../src` + `cmake --build . --config Release`，并把 `C:\windows\system32\vcomp140.dll`（+ `vcomp140d.dll`）一起打包 | `xinntao/…/.github/workflows/release.yml`（一手） |
| **OpenMP 运行库** | **必须随包分发 `vcomp140.dll`**（引擎导入表里有它；官方 zip 就带了）。缺了会"进程起不来"——这本身就是一种 `StartupFailed` | 同上 + 官方 zip 内容 |
| CMake | ≥ 3.9（`src/CMakeLists.txt` 的 `cmake_minimum_required(VERSION 3.9)`，但实际建议 **3.20+**，因为 ncnn 新版 CMakeLists 用了较新语法） | `src/CMakeLists.txt`（一手） |
| **Vulkan SDK** | **两种走法**：<br>① **装 LunarG Vulkan SDK** → 前端 `find_package(Vulkan REQUIRED)` 与 `find_program(glslangValidator PATHS $ENV{VULKAN_SDK}/bin)` 都满足。**官方 Windows 资产用的是 Vulkan SDK 1.2.162.0**（`$env:VULKAN_SDK="$(pwd)/VulkanSDK"`）<br>② **不装 SDK**：ncnn 自带 `NCNN_SIMPLEVK`（`CMakeLists.txt:83`，**默认 ON**，"minimal in-house vulkan loader"）+ `glslang` 子模块（`.gitmodules` → `https://github.com/nihui/glslang`，从源码构建）。但**前端那行 `find_package(Vulkan REQUIRED)` 仍会失败** → 需要把 `REQUIRED` 去掉并指定 `Vulkan_INCLUDE_DIR`/`Vulkan_LIBRARY`，或直接从 Vulkan-Headers 拿头文件 | ncnn `CMakeLists.txt:82-84`、`.gitmodules`、resrgan `src/CMakeLists.txt`（均一手） |
| **`NCNN_SIMPLEVK` 是否可行** | **可行且是 ncnn 默认**（`option(NCNN_SIMPLEVK "minimal in-house vulkan loader" ON)`）。它自己有 VK 头文件，且额外支持 `NCNN_VULKAN_DRIVER` / `VK_ICD_FILENAMES` 环境变量指定驱动 | `src/simplevk.cpp:103/106/116`（一手） |
| glslang | **不必需系统版**。ncnn `NCNN_SYSTEM_GLSLANG` 默认 **OFF**（`:84`），会用子模块源码构建（`:862` 起的 `if(NOT NCNN_SYSTEM_GLSLANG)`）。前端 `find_program(glslangValidator)` 找不到只会 `message(STATUS ...NOTFOUND)`，**不报错**——因为前端的 `compile_shader` 宏只编译 realesrgan 的 4 个自定义 comp，不依赖 glslang 库 | ncnn `CMakeLists.txt:84/862`、resrgan `src/CMakeLists.txt`（一手） |
| libwebp | 子模块（`.gitmodules` → `webmproject/libwebp`），无系统依赖 | 一手 |
| 本机现状 | **cl/gcc/g++/cmake/clang 全部 NOT FOUND，无 Vulkan SDK，`C:\VulkanSDK` 不存在，`glslangValidator` 不存在**（`vulkaninfo.exe` 系统自带 ✓） ⇒ **本机无法编译验证** | 本机实测 |

### 5.3 逐步命令（Windows / MSVC）

```powershell
# ===== 0. 一次性：装 Visual Studio 2022 (Desktop C++) + CMake + Git =====
# 验证：
cmake --version            # 期望 >= 3.20
& "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"

# ===== 1. 取带修复的 ncnn 源码 =====
# 途径一（推荐，已验证可下载）：从 nihui/ncnn release 下 full-source
#   https://github.com/nihui/ncnn/releases/download/20260526/ncnn-20260526-full-source.zip  (23,279,161 B)
#   注意：本机 github.com 被 DNS 封锁，需走镜像，例如
#   https://ghproxy.net/https://github.com/nihui/ncnn/releases/download/20260526/ncnn-20260526-full-source.zip
# 途径二：从 Tencent/ncnn master 克隆（含最新修复，但需自行取 glslang 子模块）

# ===== 2. 取 Real-ESRGAN 前端（BSD/MIT 部分）=====
git clone https://github.com/xinntao/Real-ESRGAN-ncnn-vulkan.git
cd Real-ESRGAN-ncnn-vulkan
git submodule update --init --recursive          # 拉 src/ncnn + src/libwebp
# 【关键】把 src/ncnn 换成上面第 1 步的“新 ncnn”：
#   删掉 src/ncnn 内容，替换为 20260526 源码；
#   同时确认 src/ncnn/glslang 子模块也拉全（.gitmodules → https://github.com/nihui/glslang）

# ===== 3. 打补丁 B（关闭全部合作矩阵门）=====
# 文件：src/ncnn/src/gpu.cpp
# 位置：函数 GpuInfoPrivate::query_extension() 内，
#       `if (support_VK_KHR_robustness2) { ... }` 块之后、函数 `return 0;` 之前
# 插入：见 §5.1 的 5 行赋值
# 核对方式（打完必须自检，行号随版本漂移）：
Select-String -Path src\ncnn\src\gpu.cpp -Pattern 'support_VK_(KHR|NV|EXT)_cooperative'

# ===== 4. 配置 + 构建 =====
mkdir build; cd build
# 装过 Vulkan SDK 时：
cmake -A x64 -DNCNN_VULKAN=ON -DNCNN_SIMPLEVK=ON ../src
# 没装 Vulkan SDK 时（先按 §5.2 去掉前端 find_package(Vulkan REQUIRED)）：
# cmake -A x64 -DNCNN_VULKAN=ON -DNCNN_SIMPLEVK=ON -DVulkan_INCLUDE_DIR=<Vulkan-Headers/include> ../src
cmake --build . --config Release -j

# ===== 5. 打包（照抄官方 release.yml 的文件清单）=====
#   build\Release\realesrgan-ncnn-vulkan.exe
#   C:\windows\system32\vcomp140.dll   ← 必须带
#   vcomp140d.dll（官方也带了）
#   models\  （realesrgan-x4plus / realesrgan-x4plus-anime / realesr-animevideov3-x{2,3,4}）
#   README_windows.md、LICENSE
```

### 5.4 与上游前端的兼容性（`main.cpp` / 模型加载）

**结论：完全兼容，不需要改前端源码。**

| 项目 | 上游约定（一手源码） | 自编后 |
|---|---|---|
| `-i/-o/-s/-t/-m/-n/-g/-j/-f/-x/-v/-h` | `src/main.cpp:106-118` 定义齐全；`-j` 默认 `1:2:2` | **不变**（前端源码不动） |
| 模型目录名判定 | `model.find("models")` 或 `find("models2")` → `prepadding=10`（`:676-685`） | **不变**（所以 `-m models` 必须保留） |
| 模型文件名规则 | `realesr-animevideov3` → `<dir>/<name>-x<scale>.param/.bin`；**其他** → `<dir>/<name>.param/.bin`（`:701-709`） | **不变** |
| `-s` 语义 | 对 **x4 模型**（x4plus）时：`-s 2` = 跑 x4 模型后**缩到 2×**（本机实测：输入 220×220 → `-s 4` 出 880×880、`-s 2` 出 440×440） | **不变** |
| `ncnn::create_gpu_instance()` 调用时机 | 在**模型路径解析之后、文件存在性检查之前**（`:733`）；且**在任何模型加载之前** ⇒ **探测失败必然是 GPU 初始化阶段，与缺模型文件无关** | **不变**（这也解释了为什么"探测失败"能被 `ProbeDiagnosis.IsInitStageFailure` 判为初始化期） |
| `-g` 取默认 GPU | `ncnn::get_default_gpu_index()`（`:737`），基于 ncnn 的 `rough_score`（`:1304+`，含"设备本地显存 GB"加分项） | **注意**：混显笔记本上需确认选中的是 5060 而不是核显；ALH Pro 显式传 `-g {gpuId}`，安全 |

### 5.5 可验证性：编译产物怎么证明"在 Blackwell 上可跑"

**在本机（4060 / sm_89）能验证的**：

| # | 验证项 | 通过判据 |
|---|---|---|
| 1 | 构建成功且能启动 | `-h` 输出与官方 2022 版**逐行对比**（帮助文本应**完全一致**，因为前端源码未动） |
| 2 | **补丁真的生效** | 对新 exe 重跑 §3.1 指纹：`VK_KHR_cooperative_matrix` / `VK_NV_cooperative_matrix2` / `VK_NV_cooperative_vector` 门字符串**应全部消失或不再被引用**；查询函数名字符串可能仍在（`vkGetInstanceProcAddr` 注册段），需配合反汇编/或直接看 `enabledExtensions` 行为 |
| 3 | **robustness2 真的进来了** | 指纹里 `VK_EXT_robustness2` / `VK_KHR_robustness2` / `robustBufferAccess2` / `nullDescriptor` **必须全部出现**（这是"拿到新 ncnn"的硬证据） |
| 4 | 功能不回归 | 对同一输入跑出一张图，与随包 2022 引擎输出做哈希比对。**预期：逐像素相同**（本机已证 realesrgan / realsr / upscayl 三者一致）；若不同，说明算子选择变了（可能因新 ncnn 走了不同 kernel） |
| 5 | 参数兼容 | 用 ALH Pro 探测的真实参数 `-i … -o … -s 2 -m models -n realesrgan-x4plus -t 0 -g 0 -j 1:1:1` 跑通（**本机已用随包 2022 引擎验证该组参数 exit=0**） |
| 6 | 大帧不崩 | 用 1080×1920 生产帧尺寸跑（ALH Pro 探测就是这么做的），而不是小图 |

**在本机不能验证、必须真机做的**：

- 在 sm_120 + 610.x/616.x 驱动上 `create_gpu_instance()` 是否不再崩。
- 驱动是否仍会 `vkCreateDevice` 返回 `VK_ERROR_INITIALIZATION_FAILED`。
- 输出帧是否为"带状近黑"（那是另一类问题，与 `-j` 并发共享 `Pipeline` 有关，见上一轮 §1.4f）。

**推荐的判定实验设计（一次真机运行同时区分三个假说）**：

| 二进制 | 特征 | 若真机上…… |
|---|---|---|
| 随包 2022 realesrgan | NV v1 门 + **无** robustness2 | **崩** ⇒ 复现基线 |
| 用新 ncnn 编的、**仅**关闭 coopmat 门（保留 robustness2） | 无 coopmat + **有** robustness2 | **过** ⇒ 根因 = **缺 robustness2**（补丁 B 也非必需） |
| 用新 ncnn 编的、coopmat 门与 robustness2 都在 | 有 coopmat + **有** robustness2 | **过** ⇒ 根因 = **缺 robustness2**，且驱动已不崩 coopmat |
| 用**旧** ncnn 编的、仅关 coopmat 门 | 无 coopmat + **无** robustness2 | **崩** ⇒ 根因 = **缺 robustness2**（强证据） |

⇒ 只要编**两个**版本（新 ncnn / 旧 ncnn，各自关 coopmat 门），就能把两个假说彻底分开。**成本很低，建议直接编两个。**

### 5.6 许可结论

| 组件 | 许可 | 一手依据 | 可否随本项目分发 |
|---|---|---|---|
| **ncnn** | **BSD-3-Clause** | `LICENSE.txt` 原文："If you have downloaded a copy of the ncnn binary from Tencent, please note that the ncnn binary is licensed under the BSD 3-Clause License."；`src/gpu.cpp:2` 头注释 `SPDX-License-Identifier: BSD-3-Clause` | **可以**（注意 `LICENSE.txt` 提到内含第三方组件需各自合规，尤其是 `glslang`） |
| **Real-ESRGAN-ncnn-vulkan** | **MIT**（**不是 BSD-3**） | 仓库 `LICENSE` 原文 "The MIT License (MIT) / Copyright (c) 2021 Xintao Wang"；同文件下半部分是 `realsr-ncnn-vulkan` 的 MIT（Copyright (c) 2019 nihui） | **可以** |
| `nihui/realsr-ncnn-vulkan` | **MIT** | release 内 `LICENSE`："The MIT License (MIT) / Copyright (c) 2019 nihui" | 可以 |
| `nihui/waifu2x-ncnn-vulkan` | 随包内 `LICENSE`（MIT 系） | release 内 `LICENSE` | 可以（现用中） |
| glslang（ncnn 子模块，来自 `nihui/glslang`） | **须单独核对** | ✅ 未核（见 §7） | **待确认** |
| libwebp | **须单独核对** | ✅ 未核（见 §7） | **待确认** |
| `upscayl/upscayl-ncnn` | **AGPL-3.0** | GitHub API `license.spdx_id = AGPL-3.0` + release 内 `LICENSE` 开头 "GNU AFFERO GENERAL PUBLIC LICENSE Version 3" | **不可** |
| `k4yt3x/video2x` | **AGPL-3.0** | 同上 | **不可** |
| `AmusementClub/vs-mlrt` | **GPL-3.0** | GitHub API + `LICENSE` 开头 "GNU GENERAL PUBLIC LICENSE Version 3" | **不可** |
| Anime4KCPP | **GPL-3.0** | 任务已明确排除 | **不可** |

⇒ **自编路线的许可完全干净**：ncnn（BSD-3）+ Real-ESRGAN 前端（MIT）。**唯一待补的是 glslang 与 libwebp 的许可条款**（两者都是宽松许可，但应当在 `THIRD_PARTY_NOTICES.txt` 里补上）。

---

## 6. 兜底：让 ONNX Runtime + DirectML 在 Blackwell 上跑快

> 前提说明：**§5 的方案一旦成功，本节只作为并行保险。** 这些建议的**速度收益在本机（4060）已实测**，在 5060 上是否线性成立**未验证**。

### 6.1 上一轮已实测、可直接沿用的结论

| 项目 | 数值 | 说明 |
|---|---|---|
| 512 分块：ncnn vs DirectML | 118 ms vs **115 ms** | **持平** ⇒ 换运行时不是速度问题的关键 |
| 分块效率最优点（8GB 卡） | **1024**（327 ms/Mpix） | 768 → 1024 收益约 9% |
| 显存悬崖 | **1280 起崩塌 13.7~20×**；1536+ **OOM** | 8GB 卡上限在 1024~1280 之间 |
| CUDA EP | 1080p 整图**直接失败**；1024 分块慢 3.4~4×；+3357 MB 运行库 | **放弃** |

### 6.2 建议的调整（按收益排序）

| # | 措施 | 具体做法 | 一手依据 |
|---|---|---|---|
| 1 | **分块上限按显存放宽** | 8GB → **1024**（现为 768）；16GB → 约 1400~1536（**须真机先测上限**）。保留 512 下限给核显/小显存 | 本机实测曲线（上一轮）；实现位置 `EsrganOnnxService.cs` 的 `Math.Max(512, SafeRender.GetTileSize())` |
| 2 | **会话数与线程** | DirectML EP 的 session 数与 intra-op 线程要按"**单 GPU 队列**"设，不要按 CPU 核数放大；分块并行应靠**多个 session + 每 session 单线程**，而非一个 session 多线程 | ⚠️ **未找到 ORT 官方"DirectML 最优线程数"的一手文字**（见 §7）。建议做法是**本机 A/B 实测**而非照抄 |
| 3 | **fp16 转换** | 模型若已是 fp16 就用 fp16；**不建议为省显存把 fp32 模型强转 fp16**，除非实测精度可接受。ORT 对 fp16 支持见官方文档 | ORT 官方文档 Float16 章节 `docs/execution-providers/` → <https://onnxruntime.ai/docs/performance/model-optimizations/float16.html>（上一轮已抓 HTML） |
| 4 | **每帧拷贝开销** | 用 **IOBinding** 把输入/输出绑到设备内存，避免每分块 host↔device 往返 | ORT 官方 `docs/api/…/IO-Binding` → <https://onnxruntime.ai/docs/api/c/struct_ort_api.html> / 官方 IOBinding 文档（上一轮已抓 `ort_iobinding.html`） |
| 5 | **Graph 优化级别** | 保持默认 `ORT_ENABLE_ALL`；分块循环里**复用同一个 session**（不要每帧重建） | ORT 官方 Graph Optimizations 文档（上一轮已抓 `ort_graphopt.html`） |
| 6 | **不要改 CUDA EP 的显存策略** | `arena_extend_strategy=kSameAsRequested` + `gpu_mem_limit` 已被本机实测**恶化**（1353 → 2637 ms） | 本机实测（上一轮） |

### 6.3 生态风险（上一轮已记录，保留）

> ONNX Runtime 官方把 **DirectML 标记为 sustained engineering**：原文 "DirectML is in sustained engineering. For new Windows projects, consider WinML instead."
> 且 **ORT 的 DirectML 包停在 1.24.4，而 ORT 主线已到 1.30.0**。
> ⇒ 长期维护性是风险项，但**"改用 CUDA EP"不构成理由**（本机实测慢 3.4~4.6×）。

---

## 7. 不确定 / 待实测（不得当结论）

### 7.1 必须在 Blackwell 真机上做的最小实验

| # | 实验 | 目的 | 为什么必须真机 |
|---|---|---|---|
| **1** | 在 5060 上跑 `vulkaninfo \| findstr /i cooperative` | 确认驱动是否广告 `VK_NV_cooperative_matrix` / `VK_KHR_cooperative_matrix` / `VK_NV_cooperative_matrix2` / `VK_NV_cooperative_vector` | 若**不广告** `VK_NV_cooperative_matrix`，2022 realesrgan 连 NV v1 查询都不会发 ⇒ **coopmat 假说直接出局**，robustness2 假说成为唯一候选。**这是零成本、最高信息量的一步**（`C:\Windows\System32\vulkaninfo.exe` 本机已确认系统自带） |
| **2** | 在 5060 上直接跑随包引擎，**保留 stderr 与退出码**：`realesrgan-ncnn-vulkan.exe -i in.png -o out.png -s 2 -m models -n realesrgan-x4plus -t 0 -g 0 -j 1:1:1` | 拿到**失败形态**：`0xC0000005` / 非零退出码 / 退出码 0 但无输出 / 输出空文件 / 出图但带状近黑 | 四种形态指向**三个完全不同的根因**（见下表） |
| **3** | 读 ALH Pro 的日志：`[探测] realesrgan GPU(n)真机探测失败(...)` 的**括号里那一段** | 直接把 `ProbeFailureKind` 枚举名读出来 | 枚举名 = 失败形态 = 根因分类，**无需任何复现** |
| **4** | 同机同参数跑 waifu2x 2025 引擎 | 确认"waifu2x 可用"这一前提 | 任务里写的是"**可能**可用（未确认）" |

**实验 2 的形态 → 根因对照（依据 `AlhPro.Core/ProbeDiagnosis.cs` 的枚举与判据）**：

| `ProbeFailureKind` | 现象 | 指向 |
|---|---|---|
| `CrashExitCode`（含 `0xC0000005`） | 非零退出码 | **支持** coopmat 假说**或** robustness2 假说（两者都表现为初始化期崩） |
| `Hang` | 超时被强杀 | **强烈支持 robustness2 假说**（PR #6296 原文用的词就是 **"hangs"**） |
| `StartupFailed` | 进程起不来 | 多半是**缺 `vcomp140.dll`**（不是驱动问题） |
| `NoOutput` | 退出码 0 但无产出 | 另归因 |
| `EmptyOutput` | 产出 0 字节 | 另归因 |
| `DefectiveFrame` | 出图但整帧/条带近黑 | **与驱动无关**——`ProbeDiagnosis` 明确"坏帧不许甩锅给驱动"，指向 `-j` 并发共享 `ncnn::Pipeline`（上一轮已实测复现并修） |

> 注意：ALH Pro 的探测**已经**是 `-j 1:1:1`、1080×1920 生产帧、`-t 0`、真实模型，所以 `DefectiveFrame` 的可能性已被大幅压低；**若仍报 `DefectiveFrame`，说明是引擎侧的另一个 bug，不是 Blackwell**。

### 7.2 未找到 / 未能验证的事项

| # | 事项 | 状态 |
|---|---|---|
| 1 | **打了 50 系补丁的第三方 realesrgan-ncnn-vulkan fork** | **未找到**。已核查：`xinntao`（冻结）、`nihui`（冻结）、`upscayl`（AGPL + 2022 ncnn）、`video2x`（AGPL）、`vs-mlrt`（GPL）。**没有搜到任何发布"Blackwell 兼容版 realesrgan-ncnn-vulkan"的仓库或 release** |
| 2 | **NVIDIA 是否已在 610.62 / 616.64 修复 coopmat 缺陷** | **未验证**。一手来源里找不到"已修"证据；已确认的是到 **610.43.03（2026-07-14）仍复现**（论坛 371808）。⚠️ GRD release note 只列部分修复项，"没写"≠"没修" |
| 3 | **RTX 5060 真机上 ncnn 引擎的实际失败形态** | **未验证**（需实验 2/3） |
| 4 | **`vkGetPhysicalDeviceCooperativeMatrixPropertiesNV`（NV v1）在 Blackwell 驱动上是否也崩** | **未验证**。NVIDIA 两帖都**没有点名它**，但也没说它安全。**这是本报告最关键的未知量**——它决定 2022 realesrgan 到底走不走 coopmat 路径 |
| 5 | **ncnn 最新二进制里 coopmat 门是否真的被走了** | 未能从二进制**反汇编**证实（本机无调试器/反汇编工具链）。指纹只能证明**字符串在**，不能证明**分支被走** |
| 6 | **glslang 与 libwebp 的确切许可条款** | **未核**。两者均为宽松许可，但未逐字读其 LICENSE |
| 7 | **`realesrgan-v0.2.0` 与 `random` 构建所用 ncnn 的确切 tag** | **未能精确判定**。已知：PE 时间戳 **2022-04-24 09:07:15 UTC**；CI 用 **Vulkan SDK 1.2.162.0**（2020-12 发布）。指纹显示"仅 NV v1 门、无 KHR"⇒ ncnn 版本界于 `20220729` 之前（KHR 门自 `20230816` 才有）；最可能紧邻 `20220419`~`20220427`。**未拿到精确 tag** |
| 8 | **5060（8GB）上 DirectML 的分块最优点** | 上一轮在本机（8GB/4060）实测 1024；**5060 上未测**。16GB 档的 1400~1536 是**外推估算** |
| 9 | **ORT 官方对 DirectML 线程/会话数的建议** | **未找到一手文字**。§6.2 第 2 条只能给"实测优先"的建议 |
| 10 | **本机无任何 C/C++ 编译器、无 Vulkan SDK** | ⇒ **§5 的全部内容都未在本机编译验证**，仅为可执行步骤 + 源码级行号依据 |
| 11 | **未在 Blackwell 实机验证** | 本文所有关于 sm_120 的判断都是**一手资料 + 二进制指纹推断**，**没有任何一条是 Blackwell 实机实测** |

### 7.3 与上一轮结论的差异（请以本轮为准的部分）

| 上一轮说法 | 本轮结论 |
|---|---|
| "根因是 NVIDIA 驱动的 cooperative-matrix 属性查询缺陷" | **降级为"可能是次要因素"**。它无法解释"发查询多的 waifu2x 能用、不发的 realesrgan 失败"。**第一嫌疑改为"引擎早于 ncnn 的 `VK_EXT_robustness2` 修复（2025-09-09）"** |
| "换新引擎 = 接入更多崩溃路径，所以别换" | **部分推翻**。新引擎确实接入更多 coopmat 路径，**但同时也拿到 robustness2 修复**。在我们的场景（2022 引擎在 570+ 驱动上失败）里，**换新引擎的净收益是正的**——这正是 §5 方案的全部价值 |
| "`upscayl-bin` 是唯一活跃的 realesrgan 系分支" | **补充**：它活跃的是**前端**，ncnn 底座仍钉死在 **2022-04-21**（子模块 sha `6125c9f4`），**二进制指纹与 2022 realesrgan 逐项相同** ⇒ "活跃"带来**零**兼容性收益 |
| —— | **新增**：`realesr-ncnn-vulkan`（MIT）能加载 Real-ESRGAN 权重，输出位精确等价（§4.2） |
| —— | **新增**：ncnn 上游 issue **#6843**（RTX 5060 + Intel 核显 + ncnn 20260526 + 驱动 595.95）在 `create_gpu_instance()` 触发 **`0xC0000005`**，**仍 open**（2026-07-22 建，最后活动 2026-07-22）。⚠️ 该 issue 唯一的回复来自 **Dosu 机器人**（**二手**，非维护者表态），它推测崩点是 `vkGetPhysicalDeviceCooperativeVectorPropertiesNV`；**维护者至今未确认**。<https://github.com/Tencent/ncnn/issues/6843> |

---

## 8. 按可行性排序的方案清单（含代价与风险）

| 排序 | 方案 | 要做什么 | 能否在 Blackwell 跑 | 代价 | 风险 |
|---|---|---|---|---|---|
| **① 首选** | **自编 ncnn（新）+ Real-ESRGAN 前端，一次编两个版本（§5）** | 装 VS2022 + CMake；取 `ncnn-20260526-full-source`；打 5 行"关 coopmat 门"补丁；编 2 个二进制（新 ncnn / 旧 ncnn 各一个） | **最可能成功**：robustness2 修复 + coopmat 路径全关，两个候选根因同时消掉 | 一次性工具链 ~10 GB；首次编译 20~40 分钟；需自行维护 fork | 中：真机仍需实测；glslang 子模块需一并拉全；缺 `vcomp140.dll` 会"起不来" |
| **② 零成本诊断（立刻做，先于 ①）** | **真机跑 3 条命令**：`vulkaninfo \| findstr /i cooperative`、引擎裸跑留 stderr/退出码、读 ALH Pro 日志里的 `ProbeFailureKind` | 不写代码 | ——（不是方案，是**判定依据**） | **0** | 无。**这一步能把两个假说分开，直接决定 ① 是否值得做** |
| **③ 应急（若 ① 暂不能做）** | **在 5060 上装/保留一个 ≤ 565 的 NVIDIA 驱动** | 降级驱动 | 按 PR #6296 原文（"enabled by default in <=565"）**可望恢复** | 牺牲新驱动特性；5060 的初版驱动是否 ≤565 **未知** | 高：5060 可能没有 ≤565 的可用驱动；且与"用户要新驱动"矛盾。**仅作最后手段** |
| **④ 并行保险** | **把 DirectML 路径做对（§6.2）**：8GB 上限 768→1024；保持 session 复用；IOBinding | 改 `EsrganOnnxService` 分块上限（**本机可验**） | 不依赖驱动修复，**确定可用** | 无（本机实测 +9%）；16GB 档需真机定上限 | 低：分块越大越接近显存悬崖 ⇒ 必须按显存分档 |
| **⑤ 不推荐** | 换 `upscayl-bin` | 下载即用 | **不能**（ncnn 底座 2022-04-21，指纹同 2022 realesrgan） | —— | **AGPL-3.0，本项目不可引入**；且**零收益** |
| **⑥ 不推荐** | 换 `realesr-ncnn-vulkan` | 下载即用（MIT ✓） | **不能**（指纹同类，同样缺 robustness2） | —— | 低（但**无收益**）。唯一价值：证明模型可互换 |
| **⑦ 不推荐** | `video2x` / `vs-mlrt` / Anime4KCPP | —— | 未知 | 大体积 + 模型转换 | **AGPL-3.0 / GPL-3.0，全部出局** |
| **⑧ 唯一根治** | 等 NVIDIA 修驱动 | —— | —— | —— | 时间未知（到 610.43.03 仍未修） |

### 一句话建议

> **先做 ②（今天就能做，零成本，决定一切）**；**同时准备 ④（本机可验，确定收益）**；**把 ① 作为根治候选排上，并在真机上编两个版本一次把假说分开**。**⑤⑥⑦ 全部不要做**——不是"没收益"就是"许可不允许"，其中 ⑤ 两个问题都占。

---

## 附录 A：本轮实际下载/检查的产物（供复核）

| 文件 | 大小 | SHA256 / 状态 |
|---|---|---|
| `realesrgan-ncnn-vulkan-v0.2.0-windows.zip`（官方） | 2,159,606 B | 与 API asset size 一致；zip CRC OK |
| 其内 `realesrgan-ncnn-vulkan.exe` | 6,161,408 B | `07e49f7cbb4ede01ae4dd4c399d3a7e5846e3d2085c3128eff881e55cb7b1a0c`（= 随包文件，**IDENTICAL**） |
| `realsr-ncnn-vulkan-20220728-windows.zip`（官方） | 64,005,701 B | 与 API asset size 一致 |
| 其内 `realsr-ncnn-vulkan.exe` | 6,104,064 B | `9ea47d64acb070a03385fd82720f7cf4d1b8dc6655de3fea0736aede5cc3d369`；PE ts 2022-07-28 13:59:11 UTC |
| `upscayl-bin-20251207-174704-windows.zip`（官方） | 2,421,760 B | 与 API asset size 一致 |
| 其内 `upscayl-bin.exe` | 7,763,968 B | `294d31be8f29d047c0d91a8dcd5e739616ece56bce3188ac688f9a52d70abe60`；PE ts 2025-12-07 17:52:47 UTC |
| 随包 `waifu2x-ncnn-vulkan.exe` | 5,094,912 B | `7ef2efc4c54e1a963046b3a9b0aee6bca5b1487a7c907373035a10f4187a733d`；PE ts 2025-09-15 11:18:48 UTC |
| `Tencent/ncnn@6125c9f4…/src/gpu.cpp`（upscayl 钉的子模块） | 168,858 B | coopmat KHR/NV2/NVv + robustness2 门**全 False** |
| 本机 ncnn master 源码快照（2026-09-10） | `src/gpu.cpp` 6,474 行 | 所有行号引用均基于此快照 |

本机实测跑通记录（RTX 4060 Laptop，驱动 572.83）：

```
随包 realesrgan 2022  : exit=0  880×880  sha256 54770F41…2F363C
upscayl-bin 20251207  : exit=0  880×880  sha256 54770F41…2F363C
realsr 20220728       : exit=0  880×880  sha256 54770F41…2F363C   ← 加载 realesrgan-x4plus 权重
随包 realesrgan -s 2  : exit=0  440×440  （-s 语义：x4 模型后缩到 2×）
```

## 附录 B：环境限制（影响本次研究的可验证范围）

- `github.com` 在本环境 **DNS 被墙**（`github.com` 解析到 `20.205.243.166` 但 TCP 超时）；`api.github.com`、`cdn.jsdelivr.net`、`objects.githubusercontent.com` **可用**。
- Release 资产下载需走镜像，实测 **`ghproxy.net` 与 `gh-proxy.com` 可用**（后者 TLS 不稳）；`ghfast.top` / `hub.gitmirror.com` / `gh.llkk.cc` / `github.moeyy.xyz` 不可用。
- 本机 **无 cl/gcc/g++/cmake/clang**、**无 Vulkan SDK**、**`glslangValidator` 不存在**；`vulkaninfo.exe` 系统自带（`C:\Windows\System32\vulkaninfo.exe`）✓。
- 本机显卡 **RTX 4060 Laptop（Ada sm_89）**，**不是 Blackwell**。
