# 新超分 / 补帧模型的兼容性与落地路径(t2 · engine-scout · 2026-09-24)

> 本文回答一个问题:**一支新模型要进这个软件,谁在哪一步改哪一行,以及它在四类机器上会走哪条路。**
> 载荷规矩:数字只有两种 —— **本机本次实测**(标了命令与次数)或**引用仓库既有实测**(标了文件与行)。
> 没有实测的写「未验证」,不写「应该没问题」。
> 本机实验台:RTX **4060** Laptop 8GB / 驱动 572.83(Ada,sm_89)。**本机不是 50 系**,50 系的结论全部来自
> 仓库既有真机记录(见 §3),本机只能验「能不能起、能不能出帧、多快」。

---

## 0. 结论摘要(先看这段)

| # | 结论 | 依据 |
|---|---|---|
| 1 | **Real-CUGAN 官方那份 zip(20220728)不能直接随包**:本机实测其 exe 指纹 `VK_EXT_robustness2` = **0 处**,与 Real-ESRGAN 官方 2022 版逐项同类 —— 这正是 50 系上「退出码 0、却出坏帧」的那一类。**必须照 `ENGINE_REALESRGAN_REBUILD.md` 的配方用 2025/2026 ncnn 重编**。另外它的 CLI 与 Real-ESRGAN **不兼容**(`-n` 是降噪档不是模型名),参数拼装不能照抄 | §1.2 二进制扫描(本机)、§2.1 实测 `-h`、`ENGINE_REALESRGAN_REBUILD.md:5-11`、`:20-41` |
| 1b | **Real-CUGAN 本机实测能跑**:1080p 2x = **617~628 ms/帧**(比 animevideov3 的 255.8 ms 慢约 **2.4×**);zip 45.98 MB / 解包后权重 39.6 MB | §2.1 实测 |
| 2 | **补帧新模型里,同一份 `rife-ncnn-vulkan-2026.exe` 已经能跑 v4.26**,权重就在 `engines/rife/rife-v4.26/`(10.9 MB),本次实测 **79.0~92.2 ms/输出帧 @1080p** —— 是**改动面最小**的一支候选 | §1.1 实测、`engines/rife/` 目录清单 |
| 3 | **IFRNet / GMFSS 这类非 v4 架构模型,不是「加个目录」就能进**:`VideoService.IsV4Model`(`VideoService.cs:8681`)决定 `-n 目标帧数` / `-s 时间步` / TTA `-x -z` 的用法,非 v4 走的是 v2 兜底语义 | `VideoService.cs:4812-4884`、`:4997-5059`、`:7913-7925` |
| 4 | **「伪模型毒化引擎结论」这个历史 bug 会再犯**,只要新下拉项的 Tag ≠ 引擎真实权重名,而 `NcnnProbePlan.For` / `NcnnModelVerdicts.NonNcnnModels` 没同步改 | `NcnnProbePlan.cs:1-46`、`NcnnModelVerdicts.cs:25-47`、`NcnnProbePoisoningTests.cs` |
| 5 | **Real-CUGAN 作为独立引擎回归,除了代码还有 3 处「它已经被删掉」的既成事实**要一起撤:安装器的 `[InstallDelete]` 会**再删一次**新装的 `engines\realcugan` | `installer.iss:110-113`、`installer_full.iss:94-96`、`installer_nocut.iss:71` |
| 6 | **四类机器上「哪支会拒绝」的机制已经存在**,不需要新造:ONNX 只有官方两支超分 + `rife49.onnx` 有权重 ⇒ 只有 ncnn 权重的模型(自训三支 / Real-CUGAN / IFRNet / GMFSS)在 ncnn 探测失败时**用不上**,必须走既有「如实告知」通道 | `ExperimentalEsrgan.OnnxFallbackNotice`(`ExperimentalEsrgan.cs:312`)、`VulkanCheck.cs:635-642` |

---

## 1. 本机实测基线(本次会话,RTX 4060 Laptop)

### 1.1 速度口径(可直接复现)

素材:`D:\deep\_batchrun\in\clip1080p_300f.mp4` 前 40 帧,抽帧为 **1920×1080 PNG(rgb24)**:

```powershell
$ff='D:\deep\alh-pro\engines\ffmpeg\ffmpeg.exe'
& $ff -i clip1080p_300f.mp4 -vf "select='lt(n\,40)'" -fps_mode passthrough -frames:v 40 in40\%04d.png
```
(注:随包 ffmpeg 是 8.x,`-vsync` 已移除,必须用 `-fps_mode passthrough`。)

**超分**(口径与 App 视频路径一致:`-s 2 -t 0 -g 0 -j 1:1:1 -f jpg`,冷进程):

```powershell
& engines\realesrgan\realesrgan-ncnn-vulkan-2026.exe -i in40 -o out -s 2 -t 0 -g 0 `
  -j 1:1:1 -m engines\realesrgan\models -n realesr-animevideov3 -f jpg
```

| 引擎 | 模型 | 耗时(两次冷跑) | ms/帧 |
|---|---|---|---|
| `realesrgan-ncnn-vulkan-2026.exe`(30.59 MB) | realesr-animevideov3 x2 | 10.23 s / 10.24 s | **255.8 / 255.9** |
| `realesrgan-ncnn-vulkan.exe`(官方 2022,5.88 MB) | 同上 | 11.93 s | **298.3**(慢 16.6%) |

→ 与仓库既标称一致(`VideoView.xaml:162` 写「约 0.26~0.30 秒/帧」);也复现了 `ENGINE_REALESRGAN_REBUILD.md:57`
「重建版比官方 2022 略快」这一条(实测 -14%,该文档写 1080p 单图 1.28→1.02 s)。

**补帧**(App 的目录批跑形态:`-n 目标帧数 -f frame_%06d.jpg -j 1:1:1:1`;40 帧 2x → `-n 79`):

```powershell
& engines\rife\rife-ncnn-vulkan-2026.exe -i in40 -o out -n 79 -m rife-v4.13 -g 0 -f "frame_%06d.jpg" -j 1:1:1:1
```

| 模型 | 权重体积 | 耗时(两次) | ms/输出帧 | 出厂是否随包 |
|---|---|---|---|---|
| `rife-v4.13`(默认) | 10.32 MB | 8.20 s / 7.20 s | **103.8 / 91.2** | ✅ `发布版\engines\rife\rife-v4.13` |
| `rife-v4.6` | 10.14 MB | 7.50 s / 7.93 s | **94.9 / 100.4** | ✅ |
| **`rife-v4.26`(已下架,权重仍在仓库)** | **10.90 MB** | 7.29 s / 6.24 s | **92.2 / 79.0** | ❌(只在 `engines\rife\`,不在 `发布版\`) |

→ 与 `RESEARCH_50SERIES.md:252` 的「ncnn 批量稳态 97~98 ms/帧」同档;波动 ±15% 是笔记本热降频,与仓库既有记录一致
(`ExperimentalEsrgan.cs:42-44` 记过同类波动)。

**未测**:TTA(`-x -z`,补帧 40 帧 × 79 输出超过 120 s 未跑完,已中止);4K;显存峰值(未挂计数器)。
(Real-CUGAN 的完整测速见 §2.1 —— 已跑通。)

### 1.2 引擎二进制的 Vulkan 门扫描(本机本次,`engines\` 原件)

方法:直接搜 exe 字节里的扩展名/查询函数名(`ncnn` 用精确字符串匹配门控,所以**这些串在不在**就等于「会不会发这条查询」,
判据来源 `RESEARCH_50SERIES.md:44-81`):

| 引擎 | MB | `VK_KHR_cooperative_matrix` | `VK_NV_cooperative_matrix2` | `VK_NV_cooperative_vector` | `VK_EXT_robustness2` | `...PropertiesKHR` | `...PropertiesNV` | `FlexibleDims...NV` |
|---|---|---|---|---|---|---|---|---|
| realesrgan **2022** 官方 | 5.88 | 0 | 0 | 0 | **0** | 0 | 2 | 0 |
| realesrgan **2026 重编** | 30.59 | 339 | 2 | 2 | **2** | 11 | 7 | 7 |
| rife 社区版(2026-08) | 6.91 | 0 | 0 | 0 | **0** | 0 | 2 | 0 |
| rife **2026 重编** | 34.71 | 364 | 2 | 2 | **2** | 11 | 7 | 7 |
| waifu2x 20250915 | 4.86 | 105 | 1 | 1 | **1** | 2 | 2 | 2 |

**两条读数**:
- 「有 `robustness2`」= 对 NVIDIA >565 驱动必需(ncnn 2025-09-09 `34429145d4` 才加),**这是重编的目的**(`ENGINE_REALESRGAN_REBUILD.md:5-11`)。
- 2026 重编**同时**带上了 coopmat2 / cooperative-vector 这两条 NVIDIA 论坛点名的崩溃路径(`RESEARCH_50SERIES.md:107-121`)。
  即「重编 = 修好一个坑、同时接入两个新坑」,它的价值由**真机探测**兑现,不能靠型号先验 ——
  现在的代码正是这么做的(`EngineService.cs:44-47`、`VulkanCheck.cs:615-624`)。

### 1.3 取件通道(本机本次,后面加模型的第 0 步)

| 通道 | 实测结果 |
|---|---|
| `github.com` 直连 | **能连通但只有 13~20 KB/s**;`Invoke-WebRequest` / `curl -C -` 都在 17~34 MB 处被切断(拿到的是无 EOCD 的残包,`Expand-Archive` 报 `End of Central Directory record could not be found`)—— 与 `ENGINE_REALESRGAN_REBUILD.md:28` 的老记录一致 |
| **`https://gh-proxy.com/<github 原始 URL>`** | **1.85 MB/s,43.84 MB / 29 秒下完,SHA256 一次到位** ← 本次唯一走通的路 |
| 其它镜像 | `ghproxy.net` HEAD 403;`ghfast.top` / `ghproxy.cc` / `gh.llkk.cc` / `github.moeyy.xyz` 全部超时;`hub.gitmirror.com` DNS 解析失败 |
| `curl` 注意 | 本机 `curl.exe` 需要 `--ssl-no-revoke`,否则 `schannel: CRYPT_E_NO_REVOCATION_CHECK` 直接失败 |
| BITS | 能下、能报出真实 `Content-Length`,但**本次实测 4 KB/s**(17 分钟只走 11 MB)**不可用**,别当主力 |

→ **结论**:凡是「取一支新模型的权重」的动作,先试 `gh-proxy.com`;**别用直连、别用 BITS**,并把 SHA256 记下来。
(这条对未来会话直接省掉一次 1 小时的试错。)

---

## 2. 逐支候选(四要素:来源 / 体积 / 本机可跑性 / 接入点)

> 许可闸由 t1(license-auditor)定。下面凡涉及许可的只**转述仓库已有的逐字核实结论**并标出处,不代替 t1 下结论。

### 2.1 Real-CUGAN(超分,本次用户点名的那支)

| 要素 | 内容 |
|---|---|
| **来源** | 前端 + 权重:`nihui/realcugan-ncnn-vulkan` release **`20220728`** 的 `realcugan-ncnn-vulkan-20220728-windows.zip`。算法上游:`bilibili/ailab` 的 `Real-CUGAN`。**实测可下**:直连 github.com 只有 13~20 KB/s(与 `ENGINE_REALESRGAN_REBUILD.md:28` 记录一致,`Invoke-WebRequest`/`curl -C -` 都在 17~34 MB 处被切断,报 `End of Central Directory record could not be found`);**镜像 `https://gh-proxy.com/<github原始URL>` 实测 1.85 MB/s、29 秒下完**,是本次唯一走通的取件路 |
| **体积** | zip **45,977,449 B(43.85 MB)**,SHA256 `C6E08D46C11704B1E3A1ADA9DDD591CB5005F52F132136C8633BA25DEF400E01`。解包后:`realcugan-ncnn-vulkan.exe` **6.35 MB** + `vcomp140.dll` 0.17 MB + **权重 39.6 MB**(`models-se` 2x/3x/4x × conservative/denoise1x..3x/no-denoise = 26.6 MB、`models-pro` 14.7 MB、`models-nose` 2.4 MB) |
| **本机可跑性** | **能跑,已实测**(RTX 4060 Laptop)。命令:`realcugan-ncnn-vulkan.exe -i in40 -o out -s 2 -n {0\|3} -m models-se -g 0 -f jpg -j 1:1:1`(40 帧 1920×1080 → 40 帧 **3840×2160**)。**617~628 ms/帧**:`-n 0` 619.5/616.7、`-n 3` 619.1/622.3、`-n -1`(默认 conservative)628.0(首次冷跑 1012);⇒ **比 animevideov3 2x(255.8 ms)慢约 2.4 倍**。与 animevideov3 输出的逐图 PSNR = **32.6 dB**(两支算法确实不同;这是"两支有多不一样",**不是**质量结论) |
| **CLI(实测 `-h`,与 Real-ESRGAN 不兼容)** | `-n` = **降噪档**(-1/0/1/2/3,default -1)、`-m` = **模型目录**(default `models-se`)、`-s` = 1/2/3/4、`-c` = syncgap-mode(Real-CUGAN 独有)、`-x` = TTA、`-j`、`-g`、`-f`。**`-n` 不是模型名** |
| **接入点** | 见 §5-C(作为**独立引擎**回归,共 10 处) |

**⚠ 两个必须先解决的前置:**

1. **许可**:仓库 2026-09-14 的逐字核实结论是 —— 代码 `Real-CUGAN/LICENSE` 是 MIT(c) 2022 bilibili,
   但 `Real-CUGAN/weights_v3/` **只有一个 README**(「Please download the weight files from netdisks」),
   **上游对权重没给任何书面许可**;可再分发的那份来自第三方移植 `nihui/realcugan-ncnn-vulkan`(MIT,c) 2019 nihui),
   「别人转的」≠ 权重作者授权 ⇒ 当时的结论是**不做推荐**(`AlhPro.Core/ExternalPractice.cs:269-284`)。
   这条是 t1 的判据输入,不是我的结论。
   (本次实测补充:`nihui` 那份 zip 里的 `LICENSE` 首行是 `The MIT License (MIT)` / `Copyright (c) 2019 nihui` ——
   **只覆盖前端与移植，不构成对 Real-CUGAN 权重的授权**;这一句要和上面那条一起读,别单看 LICENSE 就放行。)
2. **必须重编**(即便许可过关):实测该 exe 的 Vulkan 门指纹与 Real-ESRGAN **2022 官方版逐项同类** ——
   `VK_EXT_robustness2` = **0**、`VK_KHR_cooperative_matrix` = 0、`VK_NV_cooperative_matrix` = 1、
   `CooperativeMatrixPropertiesNV` = 2。也就是说它在 Blackwell 上的**失败形态**与 Real-ESRGAN 2022 版同类
   (`ENGINE_REALESRGAN_REBUILD.md:10-11`:5060 + 610.62 上「能跑完、退出码 0、输出坏帧」,软件只好降级 ONNX DirectML,
   实测 5.7 s/帧 vs ncnn 0.11 s/帧,**慢约 50 倍**)。配方直接照抄 `ENGINE_REALESRGAN_REBUILD.md:20-50`
   (MSYS2 + 2025/2026 ncnn 源码 + 官方前端源码 + **`%s`→`%ls` 那一处前端补丁**),产物命名沿用 `-2026` 后缀
   —— 这个后缀在代码里**有语义**,见 §4.2 第 2 条。

**另一个接线陷阱(已实测确认,不是猜测)**:Real-CUGAN 的 CLI 与 Real-ESRGAN **不兼容** ——
它的 `-n` 是**降噪档**(`-1/0/1/2/3`),不是模型名;模型用 `-m models-se|models-pro|models-nose`,
还有 `-c`(syncgap-mode)这个 realesrgan 没有的参数,`-x`(TTA)语义也不同。而本工程现有超分调用点把 `-n` 当模型名传
(`EngineService.cs:2741`、`:2775`、`:2828`)。**照抄 realesrgan 的 args 拼法一定会错**,要么引擎报错、要么静默按错档跑。
→ 必须为 realcugan 单独写 args 构造,并加一条「参数形态」单测(§6)。

### 2.2 RIFE 各版本(补帧)

| 候选 | 来源 / 权重在哪 | 体积 | 本机可跑性 | 出厂随包 |
|---|---|---|---|---|
| **rife-v4.26** | 社区 `rife-ncnn-vulkan` 模型 zoo;权重**已在仓库** `engines/rife/rife-v4.26/`(flownet.param 0.06 MB + .bin 10.83 MB) | **10.90 MB** | **能跑**:79.0~92.2 ms/输出帧 @1080p(§1.1) | ❌ 需搬进 `发布版\engines\rife\` |
| rife-v4.22 | 同上(仓库**没有**,需另取) | 约 10 MB 级 | 未测(权重不在本机) | ❌ |
| rife-v4.13 / v4.6 | 在册两支 | 10.32 / 10.14 MB | 能跑(§1.1) | ✅ |
| rife-v4(旧) | 仓库有(`engines/rife/rife-v4/`,9.87 MB) | 9.87 MB | 未测 | ❌ |
| 非 v4 老架构(v2.3/HD/UHD/anime) | 权重仍在 `engines/rife/`,**装机包副本已移到 `_retired_rife\`** | 33~57 MB/支 | 能跑(既有审计) | ❌ |

**必须带上的两条既有事实**:
- **v4.26 曾被主动下架**,理由写在 `VideoView.xaml:443-449`(2026-09-16 用户裁决「只留 13,再留一个兼容性强的」);
  且注释称「`engines\rife` 里根本没有这个目录」—— 这句**已经过时**:目录现在存在(`engines/rife/rife-v4.26/`)。
  要重新上架,必须同时改 `InterpDropdownContractTests.cs:28-31` 的 `RetiredInterpItems`(里面**列着 "RIFE v4.26"**)。
- **引擎来源不可追溯**:`engines/rife/rife-ncnn-vulkan.exe`(2026-08-28)不是官方发布(官方停在 `20221029`),
  来自某个社区 fork,`engines/` 被 gitignore、仓库无出处记录 ⇒ 已在 `RESEARCH_50SERIES.md:136-139` 列为待整改的
  「随包分发、来源不明的二进制」。**给它加新模型的权重,会把这个出处问题一起放大**:新权重同样来自该 fork 的 model zoo。
  → 落地顺序里应排在「把 rife 引擎换成/记录官方来源」之后,或至少与 t1 一起把出处理清。

### 2.3 IFRNet(补帧,主流候选)

| 要素 | 内容 |
|---|---|
| 来源 | `nihui/ifrnet-ncnn-vulkan`(与 realcugan/waifu2x 同一作者的 ncnn 移植线)。**仓库存在已核实**(镜像 HEAD = 200);但**release 的 tag 与附件名未确认** —— 按 realcugan 的 `20220728` 去猜 URL 得到 **404**,必须先在 releases 页面把 tag/附件名查实再下载 |
| 体积 | **未量**(未取得附件)。不能估 |
| 本机可跑性 | **未测**。卡点有两个:①附件名未确认;②**架构不兼容现有补帧接线**,见下 |
| 接入点 | 见 §5-B,但**必须先解决架构判定**:`IsV4Model` 为 false ⇒ `VideoService` 会按「非 v4」走 2x 级联 + 忽略 `-s`,而 IFRNet 的 CLI 参数与 RIFE 不完全相同(帧数/时间步语义需逐个真机核对) |

### 2.4 GMFSS(补帧,主流候选)

| 要素 | 内容 |
|---|---|
| 来源 | `Justin62628/gmfss-ncnn-vulkan`(社区移植;上游 GMFSS 是另一条学术线)。**仓库存在已核实**(镜像 HEAD = 200),release 附件未确认 |
| 体积 / 可跑性 | **未量 / 未测**。GMFSS 权重通常是多网络的 GB 级素材,体积必须下载后量,不能估 |
| 接入点 | 同 §5-B,但同样卡在架构与参数语义;许可风险高于 IFRNet(移植仓库的 LICENSE 需 t1 逐个确认) |

**顺带量到的一条参照数**:官方 `nihui/rife-ncnn-vulkan` release **`20221029`** 的 `windows.zip` = **411,540,241 B(411.55 MB)**(镜像 HEAD 实测)。
这就是「退回官方 RIFE 构建」的代价量级(`RESEARCH_50SERIES.md:305` 建议的整改项之一),也说明**官方 model zoo 一次就 400 MB 级** ——
按需取单支权重(而不是整包随包)才是对的。

### 2.5 超分侧其它候选的**现状挡板**(省得重复研究)

`EngineService.cs:1415-1418` 记录了 2026-09 那次联网盘点的结论:**效果更强的社区超分**
要么前端是 **AGPL**(Upscayl / SPAN-ncnn / Universal-NCNN-Upscaler)、要么权重**非商用**(NMKD 等)、
要么官方**只发 `.pth`**(如 `realesr-general-x4v3` —— 后来自己转 ncnn 才进来)。
→ 新超分候选进门前,**先判许可与「有没有现成 ncnn 权重」**,再谈接线;否则做完了也发不出去。

---

## 3. 四类机器:每支模型走哪条路 / 哪条会拒绝

> 机制总纲:超分与补帧都是「**先真机探测 → 通过走 ncnn(快);失败落 ONNX DirectML(稳)**」,
> 探测结论按 `引擎|设备|模型` 落 `%LOCALAPPDATA%\ALHPro\settings\ncnn-probe.txt`
> (`EngineService.cs:151-155`、`NcnnVerdictKey.cs:23`)。**唯一没有 ONNX 兜底的,是只有 ncnn 权重的模型。**

| 机器 | ncnn-Vulkan 路线 | ONNX 兜底路线 | 只有 ncnn 权重的模型(自训三支 / Real-CUGAN / IFRNet / GMFSS) | 依据 |
|---|---|---|---|---|
| **50 系(Blackwell)** | 探测**通过就走**(不许按型号先验禁用)。失败形态分两种:初始化即崩/访问违例 = 驱动 coopmat 缺陷;出图但黑帧 = `-j` 并发共享那类。**⚠ 注意:Real-CUGAN 官方那份没有 robustness2 指纹,正是 2022 代那种「exit 0 + 坏帧」的形态**(§1.2、§2.1) | 超分 `EsrganOnnxService`(RealESRGAN_x4plus / animevideov3 / waifu2x-cunet2x);补帧 `RifeOnnxService`(`rife49.onnx`) | **可能整支用不上** → 走「如实告知」:选了会被换成官方模型,并明确提示(`VulkanCheck.cs:635-642`、`ExperimentalEsrgan.cs:312`) | 机制:`RESEARCH_50SERIES.md:14-24`、`:107-121`;**真机反证**:`NcnnModelVerdicts.cs:5-13` 记录了 RTX **5060 Laptop** 真机 `ncnn-probe.txt` 三条 —— `realesrgan2026&#124;0&#124;`=True、`waifu2x&#124;0&#124;`=True ⇒ **该机 2026 重编引擎实测通过、随后真跑也通过**。即「50 系一律不可用」的旧断言已被真机推翻,代码现在的口径是对的(`EngineService.cs:42-47`) |
| **AMD 独显** | Vulkan 引擎本就是跨厂商的,探测通过即走;`DeviceRouting` 会**优先独显**、剔除 D3D12 转译层设备 | 同 50 系(超分/补帧 ONNX 都在) | 同 50 系 | `DeviceRouting.cs:61-96`、`GpuName.Score`(NVIDIA 4 / AMD 独显 3 / Arc 2 / 核显 0,`GpuInfo.cs:109-112`);**本机无 A 卡,未实测** |
| **Intel 独显(Arc)** | 同上;`VulkanCheck.cs:604-605` 的既有自检文案是「支持 GPU 加速,驱动较新时稳定」 | 同上 | 同 50 系 | `VulkanCheck.cs:604-605`;**本机无 I 卡,未实测** |
| **AMD / Intel 核显(无独显)** | **会被主动跳过**:`DeviceRouting.ResolveEngineDevice` 在「选中的是核显且表里另有独显」时换到独显;`FindBestWorkingGpuAsync` 只在**独显全不可用**时才把核显当底牌 | 核显 DML 可用(共享内存,分块一律 512:`RenderPolicy.OnnxTileSize`) | 同 50 系;若连核显也没有(纯无 GPU),探测必失败 ⇒ 全部用不上,报告写明「本机无可用 GPU」 | `DeviceRouting.cs:61-83`、`EngineService.cs:1182-1217`、`SafeRender.cs:1014-1063`;**真机反证(黑帧)**:`MainPage.xaml.cs:116-117` 记录「AMD 780M 核显跑 ncnn → GPU 队列异常 → 黑帧,连 ONNX/DirectML 回退也指向核显 → 两条路都黑,最后只能回退源帧」 |
| **无独显 / 无 GPU** | 引擎 GPU 路线不可用;`VulkanCheck.cs:1176-1178` 的既有判据把「无独显/驱动缺」当风险 | Vulkan 不可用 ≠ DirectML 不可用 —— 代码专门用它纠正「只能 CPU」误判(`VulkanCheck.cs:415`、`:526`) | **明确拒绝**:如实告知 + 不静默换模型 | `VulkanCheck.cs:415/526/1176-1178`、`CpuFallbackPolicy.cs:26-32`(**视频不落 CPU** 是硬约定,超分/补帧也一样) |

**「会拒绝」的确切含义**:不是崩溃,而是 —— 探测失败 ⇒ 该模型不可用 ⇒
超分侧由 `OnnxFallbackNotice` 告知「本批按另一支官方模型处理」,补帧侧由 `VideoService.cs:2035-2045` 的告警告知
「改用 ONNX 补帧路线」。**没有任何一条路径是静默的** —— 这是必须保持的性质。

---

## 4. 静默回退点清单(新模型接入时逐个过一遍)

历史 bug 不是「会不会犯」,而是「哪一处没同步就会犯」。按危险度排:

1. **伪模型毒化(`NcnnModelVerdicts.cs:25-47`)** —— 界面 Tag 被原样当模型名喂给 ncnn ⇒ 探 60 秒超时被杀 ⇒
   落一条 `引擎|设备|<Tag>=false` 的**假失败**。在**没有引擎级(default)结论**的机器上(独显+核显混显机,
   `EngineService.cs:669` 那句话点明了这条路的成因),假失败就是整条引擎的判据 ⇒ **所有超分被推到 ONNX**,
   实测 3880 ms/帧 vs 标称 260 ms/帧。**两道闸必须同时改**:
   - `NcnnProbePlan.For`(`NcnnProbePlan.cs:33-45`):Tag 不是引擎权重名时,换算出真权重名;
   - `NcnnModelVerdicts.NonNcnnModels`(`NcnnModelVerdicts.cs:38`):完全不经 ncnn 的条目(着色器 / 伪条目)登记进去。
   单测:`NcnnProbePoisoningTests.cs`、`NcnnModelVerdictsTests.cs`。
2. **引擎身份键**(`EngineService.cs:1472` `RealEsrganEngineId`、`:1475` `EngineId`、`:779-790` `EngineIsRebuilt2026`):
   换引擎文件却沿用旧键 ⇒ 旧版在 50 系上「出坏帧」的**真结论**会把新引擎一起判死 7 天(TTL 见 `EngineService.cs:80-81`)。
   → **新引擎文件名与身份键必须一起换**。`-2026` 后缀目前被 `EngineIsRebuilt2026` 当语义用,这不是文字游戏。
3. **`VideoService.RifePath` 的静默回退**(`VideoService.cs:63-80`):找不到 `rife-ncnn-vulkan-2026.exe` 就回退 2022 旧引擎,
   用户只觉得「补帧有时候出问题」。`打包.ps1:63` 的必带清单就是为拦它。
4. **模型目录缺失 ⇒ 静默坏帧**:引擎找不到权重时**不报错、画一张坏帧、exit 0**(`EsrganModelDir.cs:9-11` 记了两次同类事故)。
   `EsrganModelDir.For`(`:25`)是**唯一来源**:自训模型没登记进 `ExperimentalEsrgan`(含 `Retired`)就会被指到 `models\` 根目录。
5. **补帧模型目录缺失 ⇒ loud**:`VideoService.cs:4673-4674` 抛「缺少补帧模型:<name>」。这条是好的,别改坏。
6. **ONNX 侧静默换模型**:只有 ncnn 权重的模型走 ONNX 时**没有对应网络** ⇒ 必须经
   `ExperimentalEsrgan.OnnxFallbackNotice`(`ExperimentalEsrgan.cs:312`,两处调用点:
   `VideoService.cs:2718`、`UpscaleView.xaml.cs:1661`,契约测试 `ExperimentalModelRouteNoticeTests.cs:35-46`)。
7. **序号漂移**:超分下拉存的是**序号**(`VideoModelOrder`),**中途插项**会让老用户存的序号静默指向别的模型
   (Rev3 就是这么换位过来的;Rev6 那段的注释记录了一次「差点带着错打包上线」)。**加模型一律末尾追加 + Rev+1**。
   注意:图片页的模型是**按名字存**的(`UpscaleView.xaml.cs:894-895`),追加/插入都不会错位 —— 两侧规则不同,别互相套用。
8. **进程残留**:`EngineService.KillStaleEngines`(`EngineService.cs:1356-1384`)按**写死的 exe 名清单**回收残留引擎。
   新引擎(Real-CUGAN)漏加 ⇒ 任务结束后残留进程占着 GPU ⇒ 同机 NVENC 会话申请失败 ⇒
   整个任务退回 CPU 软编(实测 1 fps vs 5.5 fps,`:1349-1351`)。

---

## 5. 接入点清单(文件:行 / 函数,照着改)

### A. 超分新模型(走现有 Real-ESRGAN 引擎,ncnn 权重)

| # | 要改什么 | 位置 |
|---|---|---|
| A1 | 权重落地 | 官方:`engines/realesrgan/models/<Tag>.param|.bin`;自训:`engines/realesrgan/models/alhpro/` —— 目录由 `EsrganModelDir.cs:19/22/25` 唯一决定 |
| A2 | (自训才需要)登记模型常量 + 名字/提示/速度 | `AlhPro.Core/ExperimentalEsrgan.cs:83-137`(常量、`All`、`Retired`)、`:270-300`(ToolTip/Hint)、`:307` `LogSuffix` |
| A3 | 2x-only 权重判定 | `AlhPro.Core/EngineScalePolicy.IsX2OnlyModel`(给 2x 权重下发 `-s 4` 会出镜像平铺错帧、exit=0) |
| A4 | 视频页下拉项 | `ImgUpscalerUI/Views/VideoView.xaml:152-...`(每项必须有 `Tag` + `ToolTip` 且含「模型大小」;1x 条目例外) |
| A5 | 下拉显示名表 | `Views/VideoView.xaml.cs:3098-3111`(`BuildUpEsrganModelNames` / `UpEsrganModelName`) |
| A6 | 序号迁移 | `AlhPro.Core/VideoModelOrder.cs:44`(`CurrentRev` +1)、`:48`(`Count`)、`:62` `Migrate`(追加 ⇒ 恒等段,必须写注释说明为什么恒等) |
| A7 | 探测模型名换算 | `AlhPro.Core/NcnnProbePlan.cs:33-45`(Tag ≠ 权重名时) |
| A8 | 伪模型白名单 | `AlhPro.Core/NcnnModelVerdicts.cs:38`(Tag 完全不经 ncnn 时) |
| A9 | 引擎参数下发 | `EngineService.cs:2741`(waifu2x)、`:2775`(realesrgan 单图)、`:2828`(逐块);`VideoService` 的视频超分路径用 `-m EsrganModelDir.For(model) -n {model}` |
| A10 | 图片页模型表 | `EngineService.cs:1419-1424` `AnimeModels`、`:1430-1443` `PhotoModels`(图片页按名字存,追加安全) |
| A11 | 契约测试 | `AlhPro.Tests/InterpDropdownContractTests.cs:74`(**ESRGAN 项数写死 10**)、`VideoModelOrderTests.cs:169-170`(**Count/CurrentRev 写死 10**)、`:225-244`(Tag 顺序 + `ExperimentalEsrgan.All` 对位)、`EsrganModelDirTests.cs`、`UpscaleOrderTests.cs` |

### B. 补帧新模型(现有 RIFE 引擎,新增模型目录)

| # | 要改什么 | 位置 |
|---|---|---|
| B1 | 权重落地 | `engines/rife/<model>/`(**6 个文件:v4 架构是 flownet.*;老架构是 contextnet/flownet/fusionnet 三份**)+ **手工同步到 `发布版\engines\rife\`**(`engines/` 是 gitignore、`deploy.ps1` 也不同步 engines) |
| B2 | 下拉项 | `Views/VideoView.xaml:440-455`(`InterpModelCombo`;补帧项**不用 Tag**,靠顺序 + `Content`) |
| B3 | 序号 → 引擎模型目录名 | `Views/VideoView.xaml.cs:4420-4428` `SelectedInterpModel`、`:3073-3076` `InterpModelName` |
| B4 | 契约测试 | `AlhPro.Tests/InterpDropdownContractTests.cs:36`(`ExpectedInterpItemCount=2`)、`:28-31`(`RetiredInterpItems` —— **"RIFE v4.26" 在里面,要上架必须先删**)、`:47-48`(0/1 名字)、`:93-107`(映射目录必须真实存在) |
| B5 | **架构判定** | `VideoService.cs:8681` `IsV4Model` —— 非 v4 会走 v2 兜底(帧数/时长次优);`VideoService.cs:4812`(TTA `-x -z`)、`:4865`(目录批跑 `-n 目标帧数`)、`:4884`(二次补足)、`:4997` `:5059`(单对 `-s 时间步`)、`:7913-7925`(换卡重算) |
| B6 | 探测 | `EngineService.EnsureRifeNcnnProbeAsync` + `RifeProbeKey`(键含**模型名 + 引擎指纹 + 分辨率档**,换模型/换引擎必换键) |
| B7 | 打包 | `打包.ps1:62-63`(`engines\rife` 目录 + `rife-ncnn-vulkan-2026.exe` 必带) |

### C. 新引擎(Real-CUGAN 若作为独立引擎回归)

| # | 要改什么 | 位置 |
|---|---|---|
| C1 | **解掉三处「已移除」抛错** | `EngineService.cs:2758-2762`、`:2817-2821`、`:3341-3345`(`if (engine == "realcugan") throw ...`) |
| C2 | 引擎查找 | `EngineService.cs:1448-1458` `FindExe` → 新增 `FindRealCugan()`;`:1460/:1464/:1467` 是现成模板 |
| C3 | 引擎身份键 / 显示名 / 自检 | `EngineService.cs:1472` `RealEsrganEngineId`、`:1475` `EngineId`、`:1478-1482` `EngineLabel`、`:1509-1521` `CheckEngines` |
| C4 | **残留进程回收名单** | `EngineService.cs:1364-1369`(`names` 数组;漏加 ⇒ 占 GPU ⇒ 后续硬编失败退 CPU 软编) |
| C5 | 重编版识别 | `EngineService.cs:779-790` `EngineIsRebuilt2026`(若产物带 `2026` 后缀) |
| C6 | UI 引擎单选 | `Views/VideoView.xaml:132-136` `VideoEngineRadios`(现只有 2 项);`Views/VideoView.xaml.cs:1051-1055` `EngineToStored/EngineFromStored`(**存盘约定 0=waifu2x/1=realesrgan/2=realcugan(历史值)**,注释在 `:1051`、`:2450`) |
| C7 | 参数拼装(**不能照抄 realesrgan**) | 新增独立 args 构造:Real-CUGAN 的 `-n` 是降噪档、`-m` 才是模型目录(`models-se/pro/nose`),与 `EngineService.cs:2741/2775/2828` 的语义相反 |
| C8 | 自检报告 | `VulkanCheck.cs:612-652` `RouteOf`/模型兼容性段(要加 realcugan 行) |
| C9 | **安装器会删掉它** | `installer.iss:110-113`、`installer_full.iss:94-96`、`installer_nocut.iss:71` 的 `[InstallDelete] Type: filesandordirs; Name: "{app}\engines\realcugan"` —— **必须删掉这三条**,否则升级安装时刚装的引擎被卸载钩子再删一次 |
| C10 | 撤销「已移除」叙事 | `AlhPro.Core/ExternalPractice.cs:269-284`(许可结论)、`RELEASE_NOTES.md:574`、`website/changelog.html:368`、`ImgUpscalerUI/README.md:108`、`release_history.json:80`、`installer*.iss:111/94` 的注释 |
| C11 | 打包 | `打包.ps1:53-64` 必带清单 + `:31-50` 的「仓库有、发布版缺 → 自动补」两段(可照抄给新引擎) |

### D. 每次接入都要一起动的四张清单

| 清单 | 文件:位置 | 为什么 |
|---|---|---|
| **打包必带** | `打包.ps1:53-64`(`$required`);`installer.iss:97` / `installer_full.iss:91`(Excludes **不要**排除新权重);`installer*.iss` 的 `[InstallDelete]` | 缺文件 = 静默变慢/静默回退(ffmpeg8、RIFE 2026 都是活例) |
| **探针白名单** | `NcnnModelVerdicts.cs:38`、`NcnnProbePlan.cs:33-45`,`EngineService.cs:151-155/205/249/646-673` | 见 §4.1 |
| **契约测试** | `InterpDropdownContractTests.cs:73-74`(3 / **10**)、`:36`、`:93-107`;`VideoModelOrderTests.cs:169-170`、`:225-244`;`NcnnProbePoisoningTests.cs`;`ExperimentalModelRouteNoticeTests.cs`;`EsrganModelDirTests.cs` | 序号/项数/名字对位是编译期查不出来的 |
| **官网 / 教程文案** | `website/index.html`、`website/tutorial.html`、`website/licenses.html`(19 处模型相关行)、`website/changelog.html:368`;`使用教程.md:51-63`(超分模型段)、`:138-146`(补帧模型段);`ImgUpscalerUI/README.md:48-96`(`:88-95` 是 50 系专章);`THIRD_PARTY_NOTICES.txt`(整份,新模型必须新增条目)+ `licenses/` | 文案与下拉口径漂移在本仓库是常态(有 `AnnouncementCopyTests` 专治) |

---

## 6. 落地顺序建议(风险 × 工作量)

| 顺序 | 做什么 | 为什么排这里 | 工作量 | 主要风险 |
|---|---|---|---|---|
| **1** | **把 rife-v4.26 作为补帧第 3 支**(先过 t1 的权重来源关) | 改动面最小:权重已在仓库、2026 引擎**实测能跑**(79~92 ms/帧)、无新引擎/无新 exe、打包只是把目录搬进 `发布版` | 0.5 天 | ①"RIFE v4.26" 在 `RetiredInterpItems` 里,要改契约测试;②补帧项数 2→3 的所有引用;③引擎/权重来源不可追溯这条旧账会被放大 |
| **2** | **RIFE 自适应分块 / DirectML 侧调优**(若尚未部署) | 收益确定、不依赖任何驱动修复;`RESEARCH_50SERIES.md:290-294` 已给出处置 | 1 天 | 分块上限按显存分档,16GB 档**必须真机** |
| **3** | **Real-CUGAN:先许可、再重编、最后接引擎** | 用户点名,**取件与测速已在本机打通**(镜像 29 秒下完、1080p 2x 617~628 ms/帧),剩下的全是可控工作:许可闸 / 2025-2026 ncnn 重编 / 独立 args + 引擎回归 | **2~3 天**(取件+测速已完成) | ①许可可能直接否掉(t1);②重编要 MSYS2 工具链(`ENGINE_REALESRGAN_REBUILD.md:25-28` 已验证可行,但本机当前**没有编译器**);③`[InstallDelete]` 那三条漏删 = 功能白做;④它是三支候选里**唯一**明确会拖慢用户的(比 animevideov3 慢 2.4×)⇒ 下拉提示必须写实测速度,不能写成"更快" |
| **4** | **IFRNet** | 补帧第二支候选,但架构与参数语义与 RIFE 不同(§2.3) | 2~4 天 | `IsV4Model` 之外的整套参数语义要重新真机核对;权重来源/许可待 t1 |
| **5** | **GMFSS** | 体积与许可是最大不确定项,排在最后 | 估不动 | 权重可能是 GB 级;移植仓库许可需逐字确认 |

### 必须真机实测(单测覆盖不到)

1. **50 系两台真机**(用户手上的 RTX **5060 Laptop 8GB + 驱动 610.62**、**5070 Ti 16GB + 驱动 616.64**,
   机型见 `RESEARCH_50SERIES.md:4`):每支新模型跑一次探测 + 一段 10 秒成片,记 `ms/帧` 与失败形态(崩 / 坏帧 / 黑帧)。
   本机只有 4060,**这一条我给不出数字**。
2. **AMD / Intel 真机**(独显与核显各一台):ncnn 探测 + ONNX 回退各跑一遍;核显黑帧那条
   (`MainPage.xaml.cs:116-117`)要在新模型上重新确认。
3. **无独显 / 无 GPU 机器**:确认「明确拒绝 + 如实告知」而不是静默换模型。
4. **Real-CUGAN 在 Blackwell 上的行为 + 画质**:官方 zip 在 40 系上已实测能跑(617~628 ms/帧),
   但它的指纹是「2022 代、无 robustness2」⇒ **在 50 系上必须真机看它是崩、出坏帧还是正常**;
   同时按仓库卡片口径(540p→1080p 带真值)量色偏/detail/边缘 PSNR —— 这两件事决定它能不能进下拉。
   另需真机核对 `-m models-se/pro/nose` × `-s 2/3/4` × `-c syncgap-mode` 的组合(本机只跑了 `-s 2 -m models-se`)。
5. **4K / 大显存边界**:新超分模型在 4K 帧上的显存与崩塌点(`RESEARCH_50SERIES.md:235-247` 那张表要重做一遍)。
6. **打包端到端**:装一次包,确认 `engines\realcugan` **没有被 `[InstallDelete]` 删掉**(C9 的验收方式)。

### 单测可覆盖(不必真机)

- 下拉项数 / Tag 顺序 / 显示名 ↔ `VideoModelOrder` 迁移表(`VideoModelOrderTests`、`InterpDropdownContractTests`);
- 序号 → 引擎模型目录名映射,且目录真实存在(该测试在无 `发布版` 的全新克隆上会跳过);
- `NcnnProbePlan.For` / `NonNcnnModels` 的换算与白名单(伪模型毒化回归);
- `EsrganModelDir.For` 的目录选择 + 权重文件存在性;
- ONNX 回退告知的接线(`ExperimentalModelRouteNoticeTests`:断言 `VideoService` / `UpscaleView` 真的调了
  `OnnxFallbackNotice`);
- `KillStaleEngines` 名单包含新 exe 名(可纯文本断言);
- `打包.ps1` 必带清单包含新权重路径(同上);
- 安装器三条 `[InstallDelete]` 已删除(可纯文本断言 —— 这条能直接防住 §6 那个「功能白做」)。

---

## 7. 本文没验证的部分(不许当结论)

- **Real-CUGAN 的画质** —— 只测出「与 animevideov3 输出的 PSNR = 32.6 dB」(= 两支有多不一样),
  **没有**做质量对比:仓库既有的卡片口径(540p→1080p + 色偏/detail/边缘 PSNR/锯齿代理)必须在真机上补齐才有资格写进下拉提示。
- **Real-CUGAN 的 3x / 4x 原生档、`-c` syncgap-mode、TTA(`-x`)** —— 只跑了 `-s 2 -n {-1,0,3}`。
- **Real-CUGAN 在 8K / 4K 帧上的显存与耗时** —— 全部实测都是 1080p。
- **IFRNet / GMFSS** —— **体积未量、未跑**。两个仓库都存在(镜像 HEAD 200 已核实),但 release 的 tag/附件名没查实
  (猜 `20220728` → 404),许可是 t1 的事,我连二进制都没碰。
- **50 系 / AMD / Intel / 无独显 四类机器上的任何新模型** —— 本机是 4060,**全部未实测**;
  §3 写的是「会走哪条路」的代码依据与仓库既有真机记录,不是本次实测。
- **TTA 档(`-x -z`)** —— 补帧侧 40 帧规模下超时未跑完。
- **非 v4 架构补帧模型的参数语义** —— 只从代码侧确认 `IsV4Model` 会把它们分流,未真机验证引擎实际行为。
- **Real-CUGAN 重编后的行为** —— §2.1 的指纹矛盾(有 coopmat 查询、无 robustness2)是从**官方 zip** 量出来的;
  重编产物的指纹必须重扫一遍(判据:照 `ENGINE_REALESRGAN_REBUILD.md:61` 的 `robustness2 指纹 4/4`)。
- **「伪模型毒化」在**新**下拉项上是否会复现** —— 我只做了代码路径核对(§4.1),没有真机跑一遍复现(需要一台
  「独显+核显、且从没写过引擎级 default 结论」的机器 + 一份诊断包)。

---

## 附:本次实测的原始命令与产物

```
工作目录:D:\deep\_t2_bench
抽帧:     ffmpeg -i clip1080p_300f.mp4 -vf "select='lt(n\,40)'" -fps_mode passthrough -frames:v 40 in40\%04d.png
超分 2026: realesrgan-ncnn-vulkan-2026.exe -i in40 -o out -s 2 -t 0 -g 0 -j 1:1:1 -m <models> -n realesr-animevideov3 -f jpg
超分 2022: realesrgan-ncnn-vulkan.exe      -i in40 -o out -s 2 -t 0 -g 0 -j 1:1:1 -m <models> -n realesr-animevideov3 -f jpg
补帧:     rife-ncnn-vulkan-2026.exe -i in40 -o out -n 79 -m <rife-vX> -g 0 -f "frame_%06d.jpg" -j 1:1:1:1
Real-CUGAN 取件(直连不可用,走镜像):
  https://gh-proxy.com/https://github.com/nihui/realcugan-ncnn-vulkan/releases/download/20220728/realcugan-ncnn-vulkan-20220728-windows.zip
  (45,977,449 B / 29 s / SHA256 C6E08D46C11704B1E3A1ADA9DDD591CB5005F52F132136C8633BA25DEF400E01)
Real-CUGAN 测速:
  realcugan-ncnn-vulkan.exe -i in40 -o out -s 2 -n {0|3|-1} -m models-se -g 0 -f jpg -j 1:1:1
指纹扫描(本机本次,对任意引擎 exe 都适用):
  读 exe 字节 → 数这些串的出现次数:
  VK_EXT_robustness2 / VK_KHR_cooperative_matrix / VK_NV_cooperative_matrix2 / VK_NV_cooperative_vector /
  CooperativeMatrixPropertiesKHR / CooperativeMatrixPropertiesNV / FlexibleDimensionsPropertiesNV
  判据:robustness2 出现 = 该引擎已含 NVIDIA >565 驱动的健壮性修复(重编版才有);
        2022 代引擎三项全 0 ⇒ 在 Blackwell 上属「退出码 0 + 坏帧」那一类。
```

**本次留下的实测产物(都在 `_*` 临时目录里、不进 git,下一阶段可直接复用)**:

| 路径 | 内容 |
|---|---|
| `D:\deep\_t2_bench\dl\realcugan.zip` | Real-CUGAN 官方 20220728 windows 包(45,977,449 B,SHA256 `C6E08D46…0E01`) |
| `D:\deep\_t2_bench\realcugan\realcugan-ncnn-vulkan-20220728-windows\` | 已解包:exe 6.35 MB + `models-se` / `models-pro` / `models-nose`(权重 39.6 MB) |
| `D:\deep\_t2_bench\in40\` | 40 帧 1920×1080 PNG 基准素材 |
| `D:\deep\_t2_bench\out_esr_v3_*` / `out_esr_old2022` / `out_rife*` / `out_rc2x*` | 各引擎的实测输出(可用于回归比对) |

> ⚠ 这些产物**仅供本地实测**:许可闸没过之前,不得进 `发布版\`、不得进安装包、不得上传。
