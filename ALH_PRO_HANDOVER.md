# ALH Pro 交接文档(给 Codex 接手)

> 写于 2026-09-07,由 DeepSeek Harness 会话整理。当前 `main` 分支,版本 **v1.3.2**。
> 本文件是"项目现状 + 发布流程 + 已知问题 + 陷阱"的权威备忘,接手维护请先读它。

---

## 一、项目是什么

ALH Pro 是一款**本地图片/视频处理桌面应用**(WinUI 3 / .NET 8 / x64),所有处理本地完成、隐私优先。

- **代码路径**:`D:\deep\alh-pro`(工作目录就是这个;注意运行/发布版在 `D:\deep\alh-pro\发布版`)
- **GitHub**:https://github.com/AlLHHH/ALH-Pro(远程 `origin`)
- **当前版本**:v1.3.2(`ImgUpscalerUI\ImgUpscalerUI.csproj` 的 `<Version>1.3.2</Version>`;发布版 exe FileVersion `1.3.2.0`)

### 四大功能板块
| 板块 | 功能 | 引擎 |
|---|---|---|
| 图片超分 | 动漫(waifu2x)/照片(Real-ESRGAN)放大 2x/3x/4x | ncnn-Vulkan 或 ONNX(DirectML) |
| AI 抠图 | 自动抠主体→透明 PNG,支持编辑 | ONNX(rembg:u2net/birefnet/isnet) |
| 视频超分/补帧 | 超分(Real-ESRGAN/waifu2x)+ 补帧(RIFE)+ 去重 + 后处理 | ncnn-Vulkan 或 ONNX(DirectML) |
| 音频 | 人声分离(Demucs)/升采样(LavaSR) | ONNX |

---

## 二、外部引擎与模型(都在 `发布版\engines\`,由 deploy 管理,勿手拷)

| 引擎目录 | 内容 | 说明 |
|---|---|---|
| `engines\waifu2x\` | `waifu2x-ncnn-vulkan.exe`(20250915 版),`waifu2x-cunet2x.onnx` | ncnn + ONNX 双路线 |
| `engines\realesrgan\` | `realesrgan-ncnn-vulkan.exe`,`RealESRGAN_x4plus.onnx`,`realesr-animevideov3.onnx` | 超分 |
| `engines\rife\` | `rife-ncnn-vulkan.exe`,`rife49.onnx` | 补帧(ncnn + ONNX 双路线) |
| `engines\ffmpeg\` / `engines\ffmpeg8\` | ffmpeg.exe / ffprobe.exe | 拆帧/合帧/编码;ffmpeg8=备用 |
| `engines\rembg\` | 抠图 6 模型(u2net/u2netp/birefnet/birefnet-lite/isnet-anime/isnet-general-use)+ `RealESRGAN_x4plus.onnx` | 注意 RealESRGAN_x4plus 是**超分用**,也放 rembg 目录 |
| `engines\demucs\` | htdemucs.onnx / htdemucs_ft_vocals.onnx | 人声分离 |
| `engines\lavasr\` | backbone/denoiser_core/spec_head.onnx | 音频升采样 |
| `engines\realesrgan\` | realesr-animevideov3.onnx(2.4MB 快模型) | 视频超分动漫快模型 |

> ⚠️ **rife-v4.26 已从发布版删除**(v1.3.2 起):TTA(-x/-z)实测卡死。补帧模型下拉里 v4.26 置灰不可选。推荐 v4.13。

---

## 三、核心代码结构(ImgUpscalerUI,各文件职责)

| 文件 | 职责 |
|---|---|
| **VideoService.cs**(~317KB) | 视频主流程,入口 `ProcessVideoAsync`。拆帧→去重→补帧→超分→后处理→合帧+编码。含去重算法、RIFE 补帧、黑帧检测、帧率/时长计算、编码器选择。 |
| **EngineService.cs**(~156KB) | 引擎调用后台。路径定位、GPU 探测(`IsBlackwellGpu`/`IsEngineGpuUsableAsync`/`IsRifeGpuUsableAsync`)、模型选择、引擎进度解析、取消(杀进程)、分块超分、后处理滤镜、降级链(`RunEngFallbackGpuAsync`)、DXGI 枚举(`ToDmlDevice`)、TempRoot、黑帧检测(`IsBlackPng`/`IsBlackPngStrict`)。 |
| **RifeOnnxService.cs**(~16KB) | RIFE ONNX 补帧(rife49.onnx)。DirectML 优先,失败自动 CPU。逐对插帧 `Interp`。 |
| **EsrganOnnxService.cs**(~46KB) | ONNX 超分(纯 C#)。为 Blackwell/无独显提供稳定实现。`FindModel`/`UpscaleDirAsync`(视频逐帧 2~3 路径并行)、分块+羽化、DirectML 优先。 |
| **VulkanCheck.cs**(~33KB) | 启动 GPU 自检(waifu2x 跑 1×1 测 Vulkan、枚举设备表存静态 `Devices`、生成报告、`DriverTooOld`/`HasRiskyGpu` 风险判断、缓存)。 |
| **CutoutService.cs**(~55KB) | ONNX 抠图(CPU 强制)。`Models[]` 注册表(u2net/birefnet/isnet 系列),`CutoutAsync`/`PreviewMaskAsync`。 |
| **AudioEnhanceService.cs**(~18KB) | HT-Demucs 音乐分离/降噪。人声/伴奏/鼓/贝斯/其他,卡拉OK/仅人声/重混。 |
| **GpuInfo.cs**(~12KB) | GPU 枚举:`GetAdapterNames`/`GetDriverVersions`/`IsIntegratedGPU`/`ScoreDeviceName`(NVIDIA=4>AMD独显=3>Arc=2>其他=1,核显=0)/`GetAdapterVramGb`/`GetRecommendedIndex`。 |

辅助(Non-引擎):`SafeRender.cs`(安全渲染墙:分块/显存/内存/休息)、`PerfMemory.cs`(性能记忆,进度校准)、`AppSettings.cs`(配置)、`AppLogger.cs`(日志)、`ParaPaths.cs`(路径约定 `%LOCALAPPDATA%\ALHPro\settings`)、`AudioService.cs`(ffmpeg wav)、`LavaSrService`/`AudioSrsDsp`(音频升采样)、`AdFetcher`/`TipFetcher`/`UpdateChecker`(广告/提示/更新)。

**Views\**:`VideoView`(视频)、`UpscaleView`(图片超分)、`CutoutView`(抠图)、`AudioView`(音频)、`MainPage`(主框架/启动自检/设备纠偏/设置)、`ImageToolGrid`(图片工具共享控件)、`TutorialView`(教程)。

---

## 四、视频处理流水线(阶段顺序)

`ProcessVideoAsync` 阶段→进度:拆帧 2~5,去重 2~5,补帧 10~45,超分 45~90,编码 96~100。

1. 预处理:中文路径转 8.3 短路径(`FfmpegSafePath`,防 ffmpeg GBK "Illegal byte sequence");估算临时空间 needGB(不足报错)。
2. 拆帧(ffmpeg)+ 自动检测 VFR + HDR 源转 BT.709 SDR。
3. 去重(可选):`dedupMode`(智能/动漫/标准 scene/手动),SAD 快筛 + 分块 SSIM 精确验证;防删光保护 `<15%` 抛 `DedupTooStrongException`。
4. 补帧(可选 RIFE):先 GPU 探测;按转场切段插帧;`IsV4Model`(v4 架构可精确 3x/任意,v2 只能 2 幂级联);任意 t 插帧。
5. 超分(可选):分批 + 并行(ONNX 2~3 路);1x 内部按 2x 超分后缩回(`upscaleShrink1x`);失败帧回退原帧。
6. `ReencodeDirPngToJpg`(PNG→JPG 降临时盘,省 200GB 的核心)。
7. 合帧+音频:`BuildPostFilter`(后处理滤镜)→ 果冻修复 → fps 重映射;编码器:硬编 **nvenc>amf>qsv** → CPU **libx264**;先写 .tmp 再原子改名;输出校验。

**关键方法**:`ProcessVideoAsync`/`ProbeFrameCount`/`ProbeSizeAsync`/`ProbeAudioCodec`/`BuildPostFilter`/`EncoderArgs`/`ReencodeDirPngToJpg`/`InterpSegmentAsync`/`RifeOnnxInterpDirAsync`/`IsV4Model`/`RunAsync`/`batchOutHasDefectiveFrame`/`DirNearBlack`。

**补帧降级链(用户指定"不落 CPU")**:选定独显 ncnn → ONNX(DirectML)→ 换卡 → 报错。`TryGpuAsync` 失败/黑帧/0帧/帧数残缺(输出<目标 50%)→ `TryDegradeAsync`:①ONNX ②换卡 ③报错(绝不回落 ncnn-CPU)。

---

## 四.1 引擎路线选择(ncnn vs ONNX vs CPU)

- **`IsBlackwellGpu()`**:RTX 50 系(名字匹配 `RTX 5[0-9]{2}`;测试钩子 `ALH_FORCE_BLACKWELL=1` 强制)。50 系上 2022 版 ncnn 引擎会崩 → 需 ONNX。
- **`OldNcnnGpuRisky()`** = `IsBlackwellGpu() || !VulkanCheck.GpuAvailable`(无独显/Vulkan 不可用,CPU 模式也崩)。
- **`ShouldUseOnnxEsrgan()`** = `IsBlackwellGpu() || OldNcnnGpuRisky()`(且 ONNX 模型在)。
- **`ShouldUseOnnxWaifu2x()`** = `!IsBlackwellGpu() && OldNcnnGpuRisky()`(仅无独显;50 系 waifu2x 20250915 新版引擎自身兼容,不走 ONNX)。
- **`ToDmlDevice(engineGpu)`**:ncnn `-g` 编号 → DirectML 设备号(按 DXGI 真枚举名匹配,防双卡机跑错卡;匹配不到宁可落 CPU)。

### 黑帧检测
- `IsBlackPng`(≥95% 像素近黑;空/0字节/解码失败也 true=缺陷帧)——GPU 探测失败判定、ONNX 补帧防御。
- `IsBlackPngStrict`(只判"真·近黑",空/未写完/解码失败 false)——补帧 RIFE 防御(防把未写完的瞬时空帧当黑帧误触发降级)。
- `batchOutHasDefectiveFrame`(超分输出目录有无缺陷帧);`DirNearBlack`(源帧本就近黑时**不降级**,防误杀)。
- `ncnnUnreliable` 标记:超分检测到黑帧置位,后续批次直接 ONNX。

---

## 五、发布/打包流程(照抄)

> 完整流程细节见 `发布交接总结_v1.1.2.md`,以下是核心。

### 1. 发布(发布二进制)
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File deploy.ps1
```
作用:①`dotnet publish -c Release -p:Platform=x64`(自包含,带 .NET 运行时);②同步到 `发布版\`(排除 engines);③启动验证(窗口标题 = "ALH Pro v1.3.2")。
⚠️ 别从 bin\Debug 拷(缺 .NET 运行时,双击报 "must install .NET")。

### 2. 版本号一致性
改版本要同步:`ImgUpscalerUI.csproj`(Version)、`installer.iss`、`installer_full.iss`、`installer_nocut.iss` 的 `MyAppVersion` + `ModelsUrl`(指向对应的 GitHub release tag)。

### 3. 打包安装包(Inno Setup 6,ISCC.exe)
- `iscc installer.iss` → `ALHPro_vX_X_X_Setup.exe`(标准版,~900MB,不含抠图模型)
- `iscc installer_full.iss` → `ALHPro_vX_X_X_Full_Setup.exe`(完整版 2.2GB,含全部模型/引擎)
- `iscc installer_nocut.iss` → `ALHPro_vX_X_X_Setup_NoCut.exe`(精简版 867MB,排除抠图 6 模型但**保留 RealESRGAN_x4plus 超分模型**)
- ISCC 路径:`C:\Users\AlL\AppData\Local\Programs\Inno Setup 6\ISCC.exe`

### 4. GitHub Release
- 打 tag:`git tag vX.X.X && git push origin vX.X.X`
- 建 release + 上传资产:**必须用 raw 姿势**(`curl --data-binary @文件`),不能用 multipart(会污染文件,导致"此应用无法运行")。上传后**下载回验 SHA256**。
- ⚠️ **GitHub 单资产上限 2GB**——完整版(2.2GB)上不了 GitHub,只能走**百度网盘**(旧链接 `pan.baidu.com/s/1_heewIWeoPpWQKPJv9blew?pwd=yxfd`,提取码 `yxfd`;网盘里是旧版,重传需新链接)。
- GitHub token:从 GCM 取(`protocol=https`+`host=github.com` pipe 到 `git credential-manager get`),`password=` 那行。当前是 `gho_` 起头的。

### 5. 上传脚本注意
- 所有 curl 加 `--ssl-no-revoke`(TLS 吊销检查被破坏)。
- 请求 JSON body 用 `[IO.File]::WriteAllText(..., UTF8Encoding($false))` 无 BOM(有 BOM → curl 报 "Problems parsing JSON")。
- 勿 `Invoke-RestMethod -Body "中文JSON"`(PS5.1 序列化会坏)。

---

## 六、本次会话(DeepSeek 维护)改动 — 已提交在 `main`

本会话做了一批**稳定性/诊断增强**,按优先级列:

**记住/参数修复**
- `85f1947`:视频页"记住"失效——加 `_settingsLoaded` 守卫,防构造/加载期 `SelectedIndex=-1` 写入 `video-settings.json` 污染。
- `59cbc92`:补帧倍率 12x/16x 记不住——恢复范围 `<=3`→`<=5`(下拉有 6 档)。

**视频稳定性**
- `4e12cf8`:视频帧 JPG 直走 GDI(System.Drawing),不走必失败的 WinRT BitmapEncoder(后台线程抛 0x88982F41,导致每次刷"WinRT JPG 编码不可用")。
- `17284ad`:补帧残缺自动降级 ONNX——RIFE 偶发"只输出 1 帧"(AMD 6750 间歇 140→1),检测到输出帧数远少于目标即触发降级 ONNX。

**整改(重要,撤销了过度防护)**
- `ee1f114`:撤销 AMD 独显强制走 ONNX + 补帧"残缺防御"(此前为 AMD 卡堆的),恢复 v1.1.3 稳定行为(ncnn 能用就用);保留真 bug 修复(独显被误判 CPU、探测超时、QSV 避开)。AMD 6750 补帧是间歇性问题,v1.1.3 时代 ncnn 能跑。

**自检/诊断增强(最近几轮)**
- `591c693`:自检报告顶部加"明确结论"行(✅可运行/⚠️驱动偏旧/❌无显卡)+ 驱动版本门槛(保守,仅 NVIDIA<460/AMD<23 才提示,避免误伤)。
- `ceb5c96`:诊断包加强——收集"崩溃诊断_*.txt"(闪退完整堆栈,之前漏打包)+ "最近任务摘要.txt"(视频任务结果/失败原因,WriteTaskSummary)。
- `d93e1ed`:自检对**不稳定显卡**(AMD 独显/RTX50系 Blackwell/老GTX/纯核显)显示"⚠️ 当前显卡可能并不完全支持 ALH Pro 的运行"。
- `1302cec`:自检修正"只能 CPU"误判——Vulkan 不可用 ≠ DirectML 不可用(超分/补帧走 ONNX/DirectML GPU)。

**其他被拒绝的方向**(保持现状,勿重做):
- AMD→ONNX 强制切换(被撤销)——AMD 补帧是间歇性,ncnn 有时正常。
- 自检"真出图验证"(跑引擎出图)→ 会慢 10~30 秒,用户明确不要,保持轻量。

---

## 七、已知问题 / 待解决(需要真机才能定位)

> ⚠️ 这些**根子在真机/驱动/引擎层面**,DeepSeek 无法仅凭代码解决,需要真机数据。

1. **闪退(某用户 RTX 3070)**:日志只到"设置保存"就断,无异常堆栈。**需要 Windows 事件查看器崩溃记录**(Application Error 1000 / .NET Runtime 1026)才能定位。诊断包已含崩溃堆栈(见上),下次导出即带。
2. **AMD 6750 补帧间歇 140→1 帧**:waifu2x-ncnn-vulkan / rife-ncnn-vulkan 的**引擎层共性问题**(同类软件 SVFI/Video2X 也有,GitHub issue #71/#1140)。已有"残缺自动降级 ONNX"兜底,但**没真机验证**。
3. **RTX 3070 超分黑帧**:ncnn-Vulkan 长视频连续处理 GPU 队列累积异常。已有 `ncnnUnreliable` 标记(黑帧→后续批 ONNX)。
4. **RTX 3070 nvenc 失败(7.49 秒/帧慢)**:驱动 560.70 偏旧。**代码治不了根子,须用户升级驱动到 ≥610**。
5. **"驱动最低版本"门槛**:阈值需要真机数据校准,DeepSeek 单看诊断包设阈值会误伤(4060@572.83 是好的)。

---

## 八、接手必读 / 铁律

1. **先读 `发布交接总结_v1.1.2.md`**(发布细节/坑)和 `README.md`、`RELEASE_NOTES.md`。
2. **隐私铁律**:不泄露用户隐私;不把对话/文件/密钥发外部;不把隐私写日志/记忆。
3. **改系统文件/注册表/全局配置前需用户同意**;工作区项目文件可直接改。
4. **`发布版\` 由 deploy.ps1 管理,勿手拷引擎**。ONNX 模型超分在 realesrgan/rife 目录,抠图 6 模型+RealESRGAN_x4plus 在 rembg(打包时装要注意区分)。
5. **"预览 = 结果"约束**:预览/估算类功能复用处理阶段同一套检测器,避免预览与实际不一致。

---

## 九、当前发布版状态

- `发布版\ALHPro.exe` = v1.3.2.0,最近 deploy 时间含前面大部分改动。
- `发布版\engines\rife\rife-v4.26` **已删除**。
- 桌面已有 `ALHPro_v1.3.2_Setup_NoCut.exe`(精简版,867MB)。
- GitHub release `v1.3.2` 已建,资产:标准版(808MB)+ models_v1.0.zip(1410MB)。完整版(2.2GB)在百度网盘。
- 未跟踪文件:`ALHPro_v1.3.2_Setup_NoCut.exe`、`installer_nocut.iss`、`打包工具\gen_ad_imgs.ps1`(未提交)。

> 本次会话最后 4 个自检/诊断 commit(`591c693`/`ceb5c96`/`d93e1ed`/`1302cec`)**尚未重新 deploy 到发布版**——接手时若发布,先 `deploy.ps1` 再打包。
