# ALH Pro 速度优化研究:编码、帧序列往返与引擎侧提速

> 状态:**研究进行中**。本文件是工作文档,分两轮证据:
> **第 1 轮 = 本机一手实测**(可复现,命令与数字全列);**第 2 轮 = 一手文档/源码引用**(标注到文件+行号)。
> 本机实验台:RTX 4060 Laptop 8GB(Ada sm_89),i7-12650H 16 线程,驱动 **572.83**,Win11 26200,D: 为 Great Wall GT70 2TB **NVMe**。
> 目标机型:RTX 5060 Laptop 8GB(Blackwell sm_120)+ Ryzen 9 8940HX(32 线程)/ 驱动 **610.62**;RTX 5070 Ti 16GB + Ryzen 7 9800X3D / 驱动 **616.64**。
> 所有实测数字都标注条件;引用都标到"谁说的 + 哪一版 + 看到了什么";没验证的一律进 **§10「不确定/待实测」**。

---

## 0. 结论摘要(先看这段)

| # | 结论 | 预期收益 | 证据强度 |
|---|---|---|---|
| 1 | **"编码慢"的头号原因是随包 ffmpeg 要求 NVENC API 13.1(需驱动 ≥610),在驱动 <610 的机器上 NVENC 被静默禁用 → 全片落到 libx264 软编**。实测同一台机器同一批 4K JPG:`h264_nvenc p4` = **85.2 fps** vs `libx264 veryfast` = **70.1 fps**(**1.22×**);而软编路径真正的硬伤是它吃满 16 线程后无法再并行(见 #4),硬编在裸能力上则是 **207 fps vs 93 fps(2.2×)** | 有 NVENC 的机器上 4K 编码 **1.22×**;配合 NVDEC 到 **1.9×**;在驱动<610 的机器上等于从"软编"变"硬编" | **本机一手实测** + 一手源码/运行时日志 |
| 2 | **解决办法不是"降级 ffmpeg"而是"两条腿":** 驱动 ≥610(用户 610.62/616.64 已满足)用随包 2026 构建;驱动在 570~609 区间用 **API 13.0 的旧构建**(随包 `发布版\engines\ffmpeg` 的 n7.1 与 `ffmpeg8` 的 n8.1.2 本机实测都能在这台 572.83 机器上真正加载 NVENC 13.0) | 把可用机型从"驱动≥610"扩到"驱动≥570" | **本机一手实测**(`Loaded Nvenc version 13.0` + 硬编跑通) |
| 3 | **JPG 序列输入的吞吐天花板由"解 JPG"和"单进程"共同决定**:4K 下 SW 解 JPG + NVENC p1 = **86.5 fps**;**换 NVDEC `-c:v mjpeg_cuvid` = 131.7 fps(+52%)**;NVDEC 纯解 = 136 fps。**且单进程会卡死在这个天花板** → 分段并行 4 进程 = **174 fps(2.1×)** | **1.5~2.1×**(仅当帧序列必须落地时才需要) | **本机一手实测** |
| 3b | **"软解 JPG"在 ffmpeg 里是结构性单线程,调 `-threads` 永远无效**:`mjpegdec.c:2976` 的能力位**只有 `AV_CODEC_CAP_DR1`,没有 `FRAME_THREADS`/`SLICE_THREADS`**;且 `img2dec.c:399-418/413/465-469` 对**每一帧**做 `io_open()` + `avio_size()` + 按整张 JPEG 长度 `av_new_packet()` 全额拷贝 | 定性结论:排除一整类无效优化 | **一手源码** + 本机实测(`-threads 8` 无变化) |
| 4 | **软编路径不可通过"加线程/并行"救**:`libx264 veryfast` 4K 已吃满 16 线程到 70 fps,再并行 4 进程也只到约 85 fps(CPU 饱和)。**所以"编码慢"在软编机上是结构性天花板,唯一出路是 NVENC** | 定性结论(负向排除) | **本机一手实测** |
| 5 | **"帧统一转 JPG"这一步是净负收益,而且上游本来就不该这么干**:4K 实测 GDI+ 把 PNG 转成 JPG **72~84 ms/帧**(批内串行);而上游引擎**自己写 JPG 时用的是质量 100**(RIFE `stbi_write_jpg(...,100)`;realesrgan WIC `ImageQuality = 1.0f`)——应用却用 **q92 把已经压过一次的像素再压一遍**,既花 CPU 又降画质,换来的是"mux 时还得再把 JPG 解一遍"(4K 21~78 ms/帧) | 2 万帧省 **约 25~50 分钟**;顺带消掉 JPEG 世代损失 | **本机一手实测** + **一手源码**(`rife/src/main.cpp:215`、`realesrgan wic_image.h`) |
| 5b | **降体积的正确手段是无损 webp,不是有损 JPG**:RIFE README 逐字 "png is better supported, however **webp generally yields smaller file sizes, both are losslessly encoded**" | 体积与画质同时改善 | 一手 README |
| 5c | **`-j` 不要调高**:`heap_budget /= jobs_proc_per_gpu[gpuid[i]]`(上游注释 `// multiple gpu jobs share the same heap`)→ 提高 proc 线程数会让**自动 tile 从 400 掉到 200/100**,反而更慢;仓库内实测 `-j 1:4:1` 还产出 **87% 坏帧且退出码 0**。单卡推荐 **`-j 2:2:2`** | 避免负收益 + 避免静默坏帧 | **一手源码** `waifu2x/src/main.cpp:816-848` + 仓库实测 |
| 6 | **`-tune ll` / `-tune ull` 与 `-preset p1` 在 NVENC 上是免费的**:4K 实测 p4=85.2、p4+ll=87.6、p4+ull=87.6、p1=87.7 fps;而 p7=65.3、p7+multipass=fullres=56.3、`-rc-lookahead 32`=75.1(更慢)。**离线批处理场景下 p4/p1 是默认即最优;p7 系列是白给的 25~35% 损失** | p4→p7 回退可省 **25~35%** | **本机一手实测** |
| 7 | **AV1 NVENC 在本机不能当成"H.265 的替代品"**:实测 `av1_nvenc p4 = 83.1 fps`,比 `hevc_nvenc p4 = 85.6` 略慢,但**能编**;是否值得取决于**播放端兼容性**,不取决于速度 | 速度上与 HEVC 基本持平 | **本机一手实测**(仅速度;画质/兼容性未测) |
| 8 | **DirectML 已进入 sustained engineering**(逐字原文见 §9);ORT 的 DirectML 包停在 **1.24.4** 而主线已到 **1.30.0** —— 这是长期可维护性风险,但**不构成现在迁移的理由**(本机实测 CUDA EP 在 RIFE 上慢 4.6×) | 无即时收益;是技术债项 | **本轮已复核**(官方页面逐字 + NuGet flat-container) |
| 9 | **代码里两条"可疑注释"的复核结果:一条**证实**、一条**推翻**。**①证实**:`concat` demuxer 真的把时间轴量化到 25 fps(内层 image2 的 `framerate` 默认硬编码 `"25"`),所以 `-framerate <fr> -i pattern` 必须保留;**②推翻**:"去掉 `-preset` 就能在 50 系上硬编"不成立,失败点是 API 版本检查 | 避免两条错误排查方向 | **一手源码**(`img2dec.c:588`、`concatdec.c:804-810`)+ 本机实测 |
| 10 | **⚠️ 订正前轮的一处机制描述**:`RESEARCH_50SERIES.md` §1.2 称"ncnn 不读任何环境变量"是**错的**;`Tencent/ncnn` master `src/simplevk.cpp:101-121` 会读 `VK_ICD_FILENAMES`/`VK_DRIVER_FILES`/`NCNN_VULKAN_DRIVER`。但全树只有这 3 处、全是 loader/ICD 选择,**"没有可用的提速环境变量"这个结论不变** | 纯文档订正(避免他人 grep 到 `getenv` 后误判前轮结论) | **一手源码** |

**一句话**:先把"驱动版本 ↔ 随包 ffmpeg 的 NVENC API 版本"这条暗沟填平(它让整台机器从硬编掉到软编),再动 JPG 往返,最后才谈引擎侧分块。

---

## 1. 工具链的真实状态(本节全部可复现)

### 1.1 仓库里有 **3 个** ffmpeg,它们的 NVENC API 版本不一样

| 位置 | 版本 | ffmpeg 分支(avcodec 主版本) | 编译期 NVENC API | 本机(驱动 572.83)NVENC | 出处 |
|---|---|---|---|---|---|
| `engines\ffmpeg\ffmpeg.exe` | `N-126247-gb79d4c4c0a-20260823` | avcodec 61 / 静态单文件,139 MB | **13.1** | ❌ **失败** | 本机 `-version` + 报错原文 |
| `发布版\engines\ffmpeg\ffmpeg.exe` | `n7.1-20240930` | avcodec 61(DLL 版) | ≤12.2 | ✅ **成功** | 本机 `-version` + `-v verbose` |
| `发布版\engines\ffmpeg8\ffmpeg.exe` | `n8.1.2-50-g1a748fe2cd-20260903` | avcodec 62(DLL 版) | **13.0** | ✅ **成功** | 同上 |

**随包那个 2026 构建的确切报错(逐字)**:

```
[h264_nvenc @ ...] Driver does not support the required nvenc API version. Required: 13.1 Found: 13.0
[h264_nvenc @ ...] The minimum required Nvidia driver for nvenc is 610.00 or newer
```

**两个旧构建的确切成功日志(逐字,`-v verbose`)**:

```
[h264_nvenc @ ...] Loaded Nvenc version 13.0
[h264_nvenc @ ...] Nvenc initialized successfully
[h264_nvenc @ ...] 1 CUDA capable devices found
[h264_nvenc @ ...] [ GPU #0 - < NVIDIA GeForce RTX 4060 Laptop GPU > has Compute SM 8.9 ]
[h264_nvenc @ ...] supports NVENC
```

→ **这不是"探到有编码器但其实没用"**:日志明确 `Loaded Nvenc version 13.0` / `Nvenc initialized successfully`,并且下面 §2 的实跑吞吐(nvenc 119 fps vs libx264 70 fps)反证了它真的在硬编。

### 1.2 机制:一次 header 升版 = 最低驱动从 570 跳到 610

一手源码依据:

- FFmpeg 的运行时守门(逐字,master `libavcodec/nvenc.c:321-331`,本地已下载核对):
  ```c
  err = dl_fn->nvenc_dl->NvEncodeAPIGetMaxSupportedVersion(&nvenc_max_ver);
  av_log(avctx, AV_LOG_VERBOSE, "Loaded Nvenc version %d.%d\n", nvenc_max_ver >> 4, nvenc_max_ver & 0xf);
  if ((NVENCAPI_MAJOR_VERSION << 4 | NVENCAPI_MINOR_VERSION) > nvenc_max_ver) {
      nvenc_print_driver_requirement(avctx, AV_LOG_ERROR);   // 就是上面那条报错
  ```
  即:**编译期 header 版本 > 驱动能给的版本 ⇒ 直接拒绝**,没有"降级到低版本 API 试试"的路径。
- FFmpeg 自己维护的"API 版本 → 最低驱动"表(master `libavcodec/nvenc.c:257-289`):
  `13.1 → "610.00"`(Windows)、`13.0 → "570.0"`、`12.2 → "551.76"`、`12.1 → "531.61"`、`12.0 → "522.25"`、`11.1 → "471.41"`。
  逐分支对比(本地拉 3 个 tag 的 `nvenc.c` 核对):**n7.1 的最高分支是 12.2**,`13.0/13.1` 分支根本不存在(所以它永远不要求 610);**n8.0 与 master 都已经有 13.1**。
- 版本常量源头 `FFmpeg/nv-codec-headers@master include/ffnvcodec/nvEncodeAPI.h:118-119`:
  ```c
  #define NVENCAPI_MAJOR_VERSION 13
  #define NVENCAPI_MINOR_VERSION 1
  ```

→ **结论(机制层)**:从 nv-codec-headers `13.0` 跳到 `13.1`,使"能用 NVENC 的最低驱动"从 **570.0** 抬到 **610.00**。任何用 2026 年之后的新 nv-codec-headers 构建的 ffmpeg,在驱动 570~609 的机器上都会**整体失去 NVENC**。这正是本机(572.83)的现象。

### 1.3 应用侧为什么没救回来

`ImgUpscalerUI\VideoService.cs` 的 `EnsureHwProbeAsync`(3123 行起)确实会探测,但探测顺序是:

```csharp
foreach (var enc in new[] { "h264_nvenc", "h264_amf", "h264_qsv", "hevc_nvenc", "hevc_amf", "hevc_qsv" })
```
每一档只尝试 ①主 ffmpeg+真实参数 ②主 ffmpeg 去掉 `-preset` ③④**备用 ffmpeg**(`BackupFfmpegPath`)同样两种参数。

**问题在于"备用 ffmpeg"是哪个**:如果备用指向的是同为 13.1 的构建,那"放宽参数"这条路必然全灭,六个编码器全判坏 → `PickVideoEncoder` 落到 `libx264`(3204 行起)。而实测表明:**能救命的恰恰是 API 13.0 的旧构建**,而不是"去掉 `-preset`"。

⚠️ 附带纠正一条现有注释的因果(重要,别继续按它排查):
`VideoService.cs:3046-3049` 写「50 系上常见的失败恰恰是 `-preset p4` 被拒(exit -22),去掉 preset 就能硬编」。
本机实测这个因果**不成立**:拿能工作的旧构建试 `-preset p1/p4/p7` **全部正常**;拿失败的 2026 构建试,**去掉 `-preset` 照样失败**——因为失败发生在 `apiVersion` 检查阶段,与 preset 无关。
真正会因 `-preset` 报错的场景是**驱动比 ffmpeg 新但用户手填了非法档位**,与本文这条暗沟是两件事。

---

## 2. 端到端实测:4K 编码吞吐全表(本机一手)

**统一条件**:3840×2160 JPG 序列,240 帧(0.4 MB/帧,`testsrc2` 有细节),`-framerate 30`,输出 `-f null -`(隔离磁盘写),用 `-progress` 取 ffmpeg 自报 fps,同时记 wall 时间与帧数。机器 = 本机 RTX 4060 Laptop 8GB / i7-12650H 16 线程 / 驱动 572.83。
命令模板:`ffmpeg -hide_banner -nostdin -loglevel error -progress <f> -framerate 30 [-c:v mjpeg_cuvid] -i frames\frame_%06d.jpg <编码参数> -f null -`

### 2.1 三个"裸能力"参照(输入为 lavfi,无磁盘 IO、无 JPG 解码)

| 配置 | ffmpeg 自报 fps | 说明 |
|---|---|---|
| `h264_nvenc -preset p1 -cq 22` | **207.5** | **编码器本身的天花板** |
| `h264_nvenc -preset p4 -cq 22` | 121.4 | |
| `h264_nvenc -preset p7 -cq 22` | 69.5 | 比 p1 慢 **3.0×** |
| `av1_nvenc -preset p1 -cq 30` | 169.9 | |
| `libx264 -preset ultrafast -threads 16` | 214.2 | 16 线程全开 |
| `libx264 -preset veryfast -threads 16` | 93.2 | 这就是应用当前的软编档 |

**读数**:NVENC p1 的裸能力(207 fps)比 `libx264 veryfast`(93 fps)**快 2.2×**;但一旦输入变成 JPG 序列,两者都被拉到 70~90 区间(见 2.2)——**说明 JPG 输入路径才是瓶颈**。

### 2.2 JPG 序列输入(240 帧 4K):软件编码器 vs NVENC

| 配置 | wall(s) | 自报 fps | 相对 `libx264 veryfast` |
|---|---|---|---|
| `libx264 ultrafast crf22 th16` | 2.94 | 84.6 | 1.21× |
| **`libx264 veryfast crf22 th16`**(应用当前档) | 3.54 | **70.1** | 1.00× |
| `libx264 veryfast crf18 th16` | 3.54 | 69.9 | 1.00× |
| `libx264 fast crf20 th16` | 5.36 | 45.9 | 0.65× |
| `libx264 medium crf20 th16` | 5.74 | 42.9 | 0.61× |
| `libx265 veryfast crf22 th16` | 8.26 | 29.7 | 0.42× |
| `libsvtav1 preset10 crf35` | 5.65 | 43.9 | 0.63× |
| **`h264_nvenc p1 cq22`** | 2.81 | **87.7** | **1.25×** |
| **`h264_nvenc p4 cq22`**(应用当前档) | 2.94 | **85.2** | **1.22×** |
| `h264_nvenc p7 cq22` | 3.89 | 65.3 | 0.93× |
| `h264_nvenc p4 -tune ll` | 2.81 | 87.6 | 1.25× |
| `h264_nvenc p4 -tune ull` | 2.81 | 87.6 | 1.25× |
| `h264_nvenc p7 -multipass fullres` | 4.48 | 56.3 | 0.80× |
| `h264_nvenc p4 -bf 3 -b_ref_mode middle` | 2.95 | 84.8 | 1.21× |
| `h264_nvenc p4 -spatial-aq 1 -aq-strength 8` | 3.00 | 83.5 | 1.19× |
| `h264_nvenc p4 -rc-lookahead 32` | 3.42 | 75.1 | 1.07× |
| `h264_nvenc p4 -rc vbr -cq 22 -b:v 40M` | 2.94 | 84.9 | 1.21× |
| `hevc_nvenc p1 cq25` | 2.81 | 87.7 | 1.25× |
| `hevc_nvenc p4 cq25` | 2.91 | 85.6 | 1.22× |
| `hevc_nvenc p7 cq25` | 5.15 | 47.7 | 0.68× |
| `av1_nvenc p4 cq30` | 3.03 | 83.1 | 1.19× |
| `av1_nvenc p7 cq30` | 3.13 | 80.4 | 1.15× |

**三条可执行读数**:
1. **`p4` 就是默认、也是离线最优档**:`preset` 的 AVOption 默认值就是 `PRESET_P4`(`libavcodec/nvenc_h264.c:29`,逐字 `{ .i64 = PRESET_P4 }`),所以"去掉 `-preset`"是**空操作**;而不带 `-preset` 时 ffmpeg 的 `tune` 默认是 `NV_ENC_TUNING_INFO_HIGH_QUALITY`(`nvenc_h264.c:40`)。
2. **p7 系列是纯亏损**:p7=65.3、p7+multipass fullres=56.3,比 p4 慢 24~35%,在**离线**批处理里没有任何速度理由用它。`-rc-lookahead 32` 同样掉到 75.1。
3. **p1 只比 p4 快 3%**(87.7 vs 85.2);`-tune ll/ull` 只比 p4 快 2.8%(87.6 vs 85.2)。→ **换 preset/tune 不是大头,瓶颈在别处**。

### 2.3 瓶颈定位:JPG 解码 + 单进程

同一批 240 帧 4K,只换解码器:

| 配置 | wall(s) | 自报 fps | 相对 |
|---|---|---|---|
| SW 解 JPG + `h264_nvenc p1` | 2.85 | 86.5 | 1.00× |
| **NVDEC(`-c:v mjpeg_cuvid`)+ `h264_nvenc p1`** | 1.99 | **131.7** | **1.52×** |
| SW 解 JPG + `libx264 veryfast` | 3.50 | 71.0 | 0.82× |
| **NVDEC + `libx264 veryfast`** | 2.80 | **92.4** | **1.07×** |
| NVDEC 纯解码 → `-c:v copy` | 0.05 | (极高) | — |

→ **NVDEC 对 NVENC 路径是必需的配套(+52%),对软编路径只 +30% 且仍被 CPU 卡住**。

#### 为什么"软解 JPG"必然串行 —— 一手源码级证据(本轮实测之外新增)

**证据 1:ffmpeg 的 JPEG 解码器根本不支持帧线程。**
`libavcodec/mjpegdec.c`(FFmpeg master,本地下载核对)mpeg 解码器的能力声明**只有 DR1**:

```c
/* mjpegdec.c:2976 */
    .p.capabilities = AV_CODEC_CAP_DR1,
    .caps_internal  = FF_CODEC_CAP_INIT_CLEANUP |
                      FF_CODEC_CAP_SKIP_FRAME_FILL_PARAM |
                      FF_CODEC_CAP_ICC_PROFILES,
```
**没有 `AV_CODEC_CAP_FRAME_THREADS`,也没有 `AV_CODEC_CAP_SLICE_THREADS`**;HEVC/H.264 解码器都有前者。
→ 即使是多核机器,`-threads` / `-thread_type frame` 对 `mjpeg` 解码**完全无效**。
这直接解释了本机实测的 §"6) multithreaded JPEG decode check":给解复用器加 `-threads 8` 后 **87.4 → 69.9 fps(无变化,差异在噪声内)**。

**证据 2:image2 解复用器对每一帧都要"开文件 → 问大小 → 按整张 JPEG 的长度分配并读满"。**
`libavformat/img2dec.c`(FFmpeg master,本地下载核对)的 `ff_img_read_packet()`:

```c
/* img2dec.c:364 */
int ff_img_read_packet(AVFormatContext *s1, AVPacket *pkt)
...
/* :399-418 —— 每帧一次 io_open */
    } else if ((res = s1->io_open(s1, &f[i], filename.str, AVIO_FLAG_READ, NULL)) < 0) {
...
/* :413 —— 每帧一次 avio_size */
        size[i] = avio_size(f[i]);
...
/* :465-469 —— 按【整张 JPEG 的文件长度】分配 packet,随后读满 */
        total_size += size[i];
    }
    res = av_new_packet(pkt, total_size);
```
(唯一复用句柄的分支是 `if (s1->pb && ...)` @`:399`,仅当"当前帧文件名 == 上一次的 url"时成立;`frame_%06d.jpg` 的帧号**每帧都在变**,所以该复用**不成立**——每帧都是真实的 open/close。)

→ **对 2 万帧 × 5.73 MB 的 4K JPG 序列,这意味着约 20000 次 open/close、约 20000 次 `avio_size()`,以及约 114 GB 的"按整文件长度分配 + 拷贝"**。这条路径是**纯串行**的。
→ 两个证据合起来给出一条**结论性判断**:**"JPG 序列 → 软件解码"在 ffmpeg 里是结构性的单线程路径,不可能通过调 `-threads`/`-thread_queue_size` 提升**。要提速只有 **换成 NVDEC(`-c:v mjpeg_cuvid`)** 或 **换成内存里的原始帧(不落 JPG)**。

**证据 3:`-thread_queue_size` 不是解药。** 官方文档原文(ffmpeg.org `ffmpeg-all.html`,本轮下载核对,`:6205` 附近):

> `-thread_queue_size size (input/output)` … "By default ffmpeg only does this if multiple inputs are specified."

本流水线的 mux 命令**恰好有 2 个输入**(`-i "frames\frame_%06d.jpg"` 与 `-i input.mp4`),所以输入线程本来就存在。而队列里排的是**"整张 JPEG 的压缩字节"(packet),不是解码后的帧**——所以它只能吸收磁盘抖动,**不能把 JPEG 解码并行化**。
→ 结论:`-thread_queue_size 2048` **可以加**(零风险、吸收抖动),但**不要指望它解决"编码慢"**。

**多进程并行(240 帧切成 n 段,同时跑 n 个 ffmpeg,`h264_nvenc p1`)**:

| 配置 | wall(s) | 聚合 fps | 相对单进程 |
|---|---|---|---|
| 1 进程,SW 解码 | 2.86 | 83.9 | 1.00× |
| 2 进程,SW 解码 | 1.59 | 150.7 | 1.80× |
| **4 进程,SW 解码** | 1.38 | **174.1** | **2.08×** |
| 1 进程,NVDEC | 2.00 | 120.2 | 1.43× |
| 2 进程,NVDEC | 1.46 | 163.9 | 1.95× |
| 4 进程,NVDEC | 1.58 | 152.2 | 1.81× |

→ **单 ffmpeg 进程在 4K 上大约卡在 84~132 fps**;**分段并行 2~4 个进程能到 ~174 fps**;而 **NVDEC 让"2 进程"就够了**(163.9,接近 4 进程的 174)。4 进程 NVDEC 反而略降 = **NVENC 编码器自身开始饱和**。

> ⚠️ **与用户实测 39.6 fps 的差异要说清楚**:本机 SW 解 JPG 明显快于用户机器的 39.6 fps。可能原因:①素材熵不同(本机 `testsrc2` 平均 **0.4 MB/帧**,真实 4K 实拍帧常 3~8 MB,解码成本与码流字节数近似线性);②测试构造不同(`-f null` 与经编码器的帧消费路径差别)。**因此 §2.2 的绝对值不能直接外推到用户机器,但"NVENC > 软编""NVDEC 显著快于 SW 解 JPG""单进程有天花板"这三个方向性结论是稳的。** 用户机器上的 39.6 / 34.5 两个绝对值反而**更强烈地支持 §2.3 的方向**:如果 SW 解 JPG 只有 39.6 fps,那它就是 34.5 fps 软编的真实上限。

### 2.4 磁盘与"帧 JPG 化"的实测代价

**磁盘(NVMe,D: 盘,应用自动选中的临时盘)**:写 40 个 5.73 MB 文件(模拟 4K JPG)= **1685 MB/s(3.4 ms/帧)**;读回 = **1356 MB/s(4.2 ms/帧)**。
→ 在 NVMe 上,**磁盘不是瓶颈**(11.5 MB/帧往返 ≈ 7.6 ms/帧,相对编码 70~90 fps 的 11~14 ms/帧仍偏小,但已不可忽略)。
→ ⚠️ 但应用的 `EngineService.TempRoot`(EngineService.cs:793-807)是**"剩余空间最大的固定盘"**,没看介质类型。机械盘机型上这条会把 11.5 MB/帧 × 2 万帧 ≈ **224 GB** 的往返变成主导项。**本机无法验证机械盘场景。**

**"帧统一转 JPG"(`VideoService.ReencodeDirPngToJpg`,3314 行)**:本机 GDI+ 基准(复刻 `SaveJpegViaGdi`:转 24bppRgb + JPEG 质量 92,无 unsafe、逐帧串行):

| 尺寸 | PNG 编码 | PNG 解码 | **PNG→JPG 重编码** | 直接存 JPG(无 24bpp 转换) | JPG 解码 |
|---|---|---|---|---|---|
| 1920×1080 | 291 ms | 61 ms | **30.4 ms/帧** | 15.0 ms/帧 | 21 ms |
| 3840×2160 | 765~785 ms | 161~165 ms | **72~84 ms/帧** | 56.3 ms/帧 | 75~78 ms |
| 3840×2160(png 压缩档 2) | 765 ms | 161 ms | 72.2 ms/帧 | 56.3 ms/帧 | 75 ms |

**2 万帧外推(仅第 5 步,4K)**:72~84 ms × 20000 = **24~28 分钟**纯 CPU 串行,只为把磁盘占用从 26.4 MB/帧 降到 5.73 MB/帧。
**而这一步省下的磁盘,在 mux 时又要付出 JPG 解码**(4K 约 21~78 ms/帧;NVMe 上换来的只是 20 MB/帧的写省额)。→ **净负收益**。

> ⚠️ **与用户实测 0.17 s/帧(170 ms)的关系**:本机的 72~84 ms 是"**从内存位图**存 JPG";用户的 170 ms 很可能是"**读 PNG 解码** + 存 JPG"(本机 PNG 解码 161~165 ms,量级吻合)。两者不矛盾,而是**同一段代码的两个口径**——测量时务必写清是否包含 PNG 解码,否则差 2 倍。

---

## 3. 回答 A:有 610+ 驱动的 50 系机器上,4K 编码怎么配

**先说结论顺序**:①先确认走的是 NVENC 而不是 `libx264`;②preset 用 p4(默认即最优),不要 p7/lookahead/multipass;③**配套 NVDEC `-c:v mjpeg_cuvid`**(这是把 86.5 → 131.7 fps 的那一步);④再要更快就把帧序列分段并行(2~4 进程)。

### 3.1 推荐命令(驱动 ≥610,随包 2026 ffmpeg)

H.264(最广兼容):
```
ffmpeg -y -nostdin -framerate 30 -c:v mjpeg_cuvid -i "frames\frame_%06d.jpg" \
       -i "input.mp4" -map 0:v -map 1:a:0? -c:a copy -t <dur> \
       -c:v h264_nvenc -preset p4 -rc vbr -cq 20 -b:v 0 \
       -spatial-aq 1 -aq-strength 8 -bf 3 -b_ref_mode middle \
       -pix_fmt yuv420p -color_range tv -colorspace bt709 \
       -bsf:v h264_metadata=colour_primaries=1:transfer_characteristics=1:matrix_coefficients=1 \
       "out.mp4"
```
- `-cq 20` + `-b:v 0` = 纯 CQ 恒定质量(VBR-lookahead 由 `-cq` 驱动)。**若沿用应用现在的 `-b:v <k>K -maxrate -bufsize`,那是"给定了码率上界"的写法**,实测速度无差别(84.9 vs 85.2)但质量受码率约束,与"保质量"目标不一致。
- `-spatial-aq 1 -aq-strength 8`:实测代价仅 **1.9%**(85.2→83.5),为编码质量买这个价是划算的;`-temporal-aq` 本机未测(见 §7)。
- `-bf 3 -b_ref_mode middle`:实测代价 **0.5%**(85.2→84.8)。

HEVC(同码率更省空间):
```
-c:v hevc_nvenc -preset p4 -cq 25 -spatial-aq 1 -aq-strength 8 -pix_fmt yuv420p \
-bsf:v hevc_metadata=colour_primaries=1:transfer_characteristics=1:matrix_coefficients=1
```
实测:`hevc_nvenc p4 = 85.6 fps`(**与 H.264 的 85.2 持平**,不额外付速度)。→ **HEVC 在本机没有速度惩罚**,值得默认给 8GB 机型之上的用户。

AV1(只在明确要省空间/兼容播放端时):
```
-c:v av1_nvenc -preset p4 -cq 30 -pix_fmt yuv420p
```
实测:`av1_nvenc p4 = 83.1 fps`(比 HEVC 慢 3%)。**AV1 在本机速度上不占优**,价值在压缩率与生态,不在"更快"。

**明确不要用的**:`-preset p7`(65.3)、`-preset p7 -multipass fullres`(56.3)、`-rc-lookahead 32`(75.1)、`libx265 veryfast`(29.7)、`libx264 medium`(42.9)。

### 3.1b 「不要在这上面花时间」清单(负向结论,同样重要)

本轮实测 + 源码**排除了**以下几条常见但无效的方向,列出来是为了避免后续重复投入:

| 无效动作 | 为什么无效 | 依据 |
|---|---|---|
| 给 mux 加 `-threads N` / `-thread_type frame` 想并行解 JPG | `mjpeg` 解码器**没有** `AV_CODEC_CAP_FRAME_THREADS`/`SLICE_THREADS`,只能是单线程 | 一手源码 `mjpegdec.c:2976`;实测 `-threads 8` 前后 87.4 / 69.9 fps(噪声内) |
| 指望 `-thread_queue_size` 解决编码慢 | 它只让输入线程预取**压缩包**(整张 JPG),不能并行化解码;本命令已有 2 个输入,输入线程本来就在 | 官方文档 `ffmpeg-all.html` `-thread_queue_size` 段原文 + `img2dec.c` 包构造 |
| 写 `-preset p4` 以为在"提高速度" | `p4` **就是** `h264_nvenc` 的默认值,写不写一样;"去掉 `-preset` 反而更快"不成立 | 一手源码 `nvenc_h264.c:29` |
| 用 `-preset p5/p6/p7` 或 `-multipass fullres` "提质量又不慢" | 实测 p7 慢 24%、p7+multipass 慢 35%;`-multipass` 默认就是 `disabled` | 本机实测 + `nvenc_h264.c:154-159` |
| 在 image2 输入上用 `-hwaccel cuda` 硬解 JPG | image2 每帧产出的是独立 `AVPacket`→**软件** `mjpeg` 解码器;`-hwaccel` 只在带 `hw_device_ctx` 的解码器上生效。**正确写法是 `-c:v mjpeg_cuvid`** | 一手源码 `img2dec.c:440-441`(设置 `par->codec_id`)+ 本机实测(用 `mjpeg_cuvid` 才提速) |
| 换 AV1 为了"更快" | 实测 `av1_nvenc p4 = 83.1` < `hevc_nvenc p4 = 85.6`;AV1 的价值在压缩率/生态,不在速度 | 本机实测 |

### 3.2 瓶颈会转移到哪里

按本机实测,4K JPG 序列 + NVENC p4 的**实测吞吐 85 fps**,而:
- NVENC 裸能力(lavfi 输入)= **207 fps** → 编码器还有 2.4× 余量;
- NVDEC 纯解 JPG = **136 fps** → 解码还有 1.6× 余量;
- NVDEC + NVENC p1 = **131.7 fps** → **两者叠加后 131.7 就接近 NVDEC 的 136**,说明**下一道墙是 JPG 解码**;
- 分段并行 4 × SW 解码 + NVENC = **174 fps** → 越过 NVDEC 单流上限后,**再上一道墙是 NVENC 自身的 ~175~207 fps**。

→ **准确的结论是**:在 4K JPG 序列上,**"单 ffmpeg 进程的 JPG 解码"是第一道墙**(SW ~86 → NVDEC ~132);**NVENC 硬件编码器是最后一道墙**(~175~207)。**用户的怀疑方向是对的**:"换成 NVENC + 软解 JPG,瓶颈立刻变成 SW 解 JPG 的 39.6 fps",这时 **`-c:v mjpeg_cuvid` 就是必需配套**,否则 NVENC 的余量完全用不上。

### 3.3 但请注意:这份优化只值总时长的百分之几

用户的真机数据是「**编码占 9376 s / 总共 9806 s**」——**编码占 96%**。这本身就说明那台机器**在走软编**(或者是 8K/超长片)。所以本文最高优先级的动作不是调 preset,而是**让 NVENC 真的被用上**(§1、§4.1)。一旦用上,按本机比例 4K 下软编→硬编 ≈ **1.2~1.7×**(在 SW 解码不变的前提下),配合 NVDEC 可到 **1.9×**。

---

## 4. 回答 B:驱动 <610 的机器能不能拿到 NVENC?

**能。本机已经实证了。**

### 4.1 三条可执行路径(按推荐度)

| 路径 | 做法 | 本机证据 | 代价 |
|---|---|---|---|
| **① 主推:把 API 13.0 的旧构建作为"低驱动备用 ffmpeg"** | 把 `发布版\engines\ffmpeg`(n7.1,API ≤12.2)或 `engines\ffmpeg8`(n8.1.2,API 13.0)作为 `BackupFfmpegPath`,并在 `EnsureHwProbeAsync` 里**先用备用 ffmpeg 探 NVENC** | **本机驱动 572.83 上两个都跑通**(`Loaded Nvenc version 13.0` + 实编 85~88 fps) | 需带 DLL 目录(shared build);`av1_nvenc`/新档位可能缺失(见下) |
| ② 让用户升驱动到 ≥610 | 610.62 / 616.64 均已满足 | 用户都是 ≥610 → **他们的机器上问题不在驱动,而在别处**(见 §4.2) | 用户侧动作 |
| ③ 兜底:软编 + 分段并行 | `libx264 ultrafast crf22` + 2~4 进程 | 本机软编 4 进程聚合 ≈85 fps(CPU 饱和,收益有限) | 收益小,仅作保底 |

**①的可用性核实(本机 `-encoders` 逐行)**:n7.1 与 n8.1.2 **都含** `h264_nvenc` / `hevc_nvenc` / `av1_nvenc`;`-hwaccels` 都含 `cuda`;**都含 `mjpeg_cuvid`**(NVDEC 解 JPG 可用)。→ 旧构建并不缺本文需要的任何一件。

### 4.2 重要推论(写给排障用)

用户两台机器驱动 **610.62 / 616.64 都 ≥610**,所以随包 2026 ffmpeg 的 13.1 检查**应该通过**。那么"编码占 96%" 的解释只可能是别的:

- 备用/主 ffmpeg 选择逻辑把 NVENC 判成了坏编码器(需要看用户日志里 `硬件编码器探测:可用 [...]` 那一行);
- 用户选了强制软编,或 `codecPref` 落到 `libx265`(实测 29.7 fps,比 libx264 更慢);
- 用户的片源极端长(9600 s 的编码时长若按 70 fps 计 ≈ 67 万帧,不现实;按 30 fps ≈ 29 万帧,也不像 4x~8x 补帧的目标场景)。→ **这条必须靠用户日志定论,见 §7**。

---

## 5. 回答 C:免掉帧序列磁盘往返 / JPEG 往返

### 5.1 从代码看,当前往返有多重

`VideoService.cs` 里,一条 1080p→4K + 补帧 + 超分的任务会**至少写三份帧**:

| 阶段 | 目录 | 代码位置 |
|---|---|---|
| ffmpeg 抽帧 | `frames_in\frame_%06d.jpg` | 410 行 |
| RIFE 输出 | `frames_out\` → 合并进 `framesFinal` | 2415 行 `InterpSegmentAsync` |
| **补帧后 PNG→JPG 全目录重编码** | `framesFinal` | **1215 行 `ReencodeDirPngToJpg(framesFinal)`** |
| 超分按批 | `up_in_<start>\` / `up_out_<start>\`(每批复制进去、算完删掉) | 1436-1443、1601-1602 行 |
| **超分后 PNG→JPG 全目录重编码** | `framesFinal = upOutput` | **1710 行 `ReencodeDirPngToJpg(framesFinal)`** |
| mux 读回 | `-framerate <fr> -i framesFinal\frame_%06d.jpg` | 1961、1994 行 |

→ **第 5 步(§2.4 实测 72~84 ms/帧 @4K)会被触发到两次**(1215 与 1710),而且 `ReencodeDirPngToJpg` 内部每帧还要 `Directory.EnumerateFiles(dir,"*.png").ToArray()` 扫全目录。

### 5.2 可执行的三档方案(按代价从低到高)

**档 1(零架构改动,收益确定,且是本文最强的一手结论):让引擎直接吐 JPG/PNG,删掉第 5 步。**

一手源码证据(本轮本地下载逐行核对):

| 引擎 | 输出格式控制 | JPEG 质量 | 源码位置 |
|---|---|---|---|
| `rife-ncnn-vulkan` | `-f pattern-format`(`%08d.jpg/png/webp`,默认 `%08d.png`) | **100**(`stbi_write_jpg(..., 100)`) | `main.cpp:120`(帮助文本)、`:215`(写 JPG)、`:460`(默认 `%08d.png`)、`:652` 附近 |
| `realesrgan-ncnn-vulkan` | 帮助文本 `-f` 列出 png/webp/jpg | **1.0f**(WIC `PROPBAG2 "ImageQuality"` = `varValue.fltVal = 1.0f`) | `wic_image.h` 的 `wic_encode_jpeg_image()`(`option.pstrName = L"ImageQuality"` / `varValue.fltVal = 1.0f`) |

→ **上游两个引擎的 JPEG 写出质量都比应用自己的第 5 步高**:
- RIFE 写 JPG 用 **quality 100**;realesrgan 走 WIC 时用 **1.0f(=100)**;
- 而应用 `EngineService.SaveJpegViaGdi` 用的是 `VideoFrameJpgQuality` 的 **92** 档。
- 即:**应用把上游已经按 q100 写出的 JPG,再用 q92 重压一遍** —— 花了 72~84 ms/帧(§2.4),换来的却是**质量更差 + 体积几乎没变**(4K 实测 q92 是 5.73 MB/帧)。
- 更根本的问题:JPEG 是**有损**的,对"超分刚算出来的像素"再压一次是典型的**世代损失**;把体积优化的手段从"有损重压"换成**无损但更小的格式**才是正解。

**上游明确给出的无损替代**:RIFE README 逐字写
> `pattern-format` = the filename pattern and format of the image to be output, png is better supported, however **webp generally yields smaller file sizes, both are losslessly encoded**

→ 即**webp 无损且通常比 png 小**,是"降体积"的正确手段(应用现在用的 JPG 是"有损 + 额外 CPU + 还要再解一遍",三项全输)。

**预期**:消掉 2 次 × 72~84 ms/帧(4K)→ 2 万帧省 **约 50 分钟**(若两次都触发)。
**实施建议**:优先做"**引擎出 PNG → 不重编码 → ffmpeg 直接 mux**"或"**引擎出 webp,应用零转码**";若必须控制体积,再考虑"引擎出 JPG(q100)+ 应用不重压"。

**档 2(推荐):去掉 JPG 化,让 ffmpeg 读 PNG。**
- 代价:磁盘占用升到 26.4 MB/帧(@4K,本机实测),2 万帧 ≈ **528 GB** —— 需要按剩余空间分档启用(应用已有 `diskTight` 逻辑可复用,392 行)。
- 收益:省掉 72~84 ms/帧;但 mux 侧 PNG 解码(161~165 ms/帧)**比 JPG 解码(75 ms)更贵** → **净收益约为 0,甚至负**。
- → **所以档 2 单独看没有意义**;它只有配合"不落盘"才有意义。

**档 3(正解,工程量最大):不落盘 —— 管道化。**
- ffmpeg 侧:`-f rawvideo` / `-f image2pipe` 进管道是官方支持的用法(参数按 `-f image2pipe -vcodec mjpeg` 的写法喂给下游)。
- **但本流水线的三个引擎全是"目录进 / 目录出"的二进制**(`-i indir -o outdir`),它们**不接受 stdin/stdout 帧流**——这是硬约束,不是配置问题。
- 因此要真正消掉往返,只有:①**改造/包装一个能吃管道或共享内存的中间层**(例如自研帧服务,把引擎换成库调用),或 ②**内存盘**承载这些目录。
- 内存盘的量级算术(非实测):4K RGB24 = **23.7 MB/帧**、RGBA32 = **31.6 MB/帧**、yuv420p = **11.9 MB/帧**(本机基准打印);2 万帧 × 5.73 MB(JPG)≈ **112 GB**,× 26.4 MB(PNG)≈ **528 GB** → **整帧序列放不进内存**(本机物理内存未核,见 §7),内存盘只能作为"滚动批次窗口"用,不能作为整个任务的载体。
- → **本轮的诚实结论:在"引擎是目录进出的外部 exe"这个既定约束下,不存在零风险地消掉帧序列落盘的方案。**能立刻拿到的收益是**去掉那两次 JPG 化**(档 1),而不是去掉落盘本身。

### 5.3 与"编码慢"的关系(别把两件事混在一起)

§2.4 已经量化:**JPG 化是 72~84 ms/帧的 CPU 串行成本**,而 §2.3 说明 **JPG 序列喂给 NVENC 时解码是第一道墙**。这两件事指向**同一个动作**:
> **不要把帧转成 JPG。**它既花 72~84 ms/帧去编码,又在 mux 时花 21~78 ms/帧 去解码——**一来一回全是净成本**,而磁盘在 NVMe 上根本不是瓶颈。

---

## 6. 回答 D:除编码外的有据提速点

| # | 改动 | 位置 | 依据 | 预期 | 风险 |
|---|---|---|---|---|---|
| 1 | **让"备用 ffmpeg"覆盖 NVENC API 13.0** | `VideoService.EnsureHwProbeAsync`(3118-3190)、`BackupFfmpegPath` | 本机一手:13.1 构建在 572.83 上被拒;13.0 构建跑通 85~88 fps | 把可用机型从"驱动≥610"扩到"≥570";驱动<610 的机器从软编变硬编 | 需随包第二个 ffmpeg(仓库里已有) |
| 2 | **去掉两次 `ReencodeDirPngToJpg`** | `VideoService.cs:1215`、`1710`(函数 3314) | 本机一手:4K **72~84 ms/帧**,串行 | 2 万帧 **约 25~50 分钟** | JPG 变小、磁盘占用升;需按剩余空间分档 |
| 3 | **mux 加 `-c:v mjpeg_cuvid`** | `VideoService.cs:1994` 的 `muxInput` | 本机一手:86.5 → **131.7 fps(+52%)**;且旧构建都含该解码器 | 编码阶段 **1.5×** | 非 NVIDIA 机器无此解码器 → 必须"探到才加" |
| 4 | **多进程分段编码** | `VideoService.cs:2062` 单进程编码 | 本机一手:4 段并行 83.9 → **174.1 fps(2.08×)**;2 段 150.7 | 编码阶段 **1.8~2.1×** | 段边界 GOP 重置需拼接;仅在解码已用 NVDEC 后收益下降(120→164→152) |
| 5 | **NVENC preset 定为 p4 并禁止 p7/lookahead/multipass** | `EncoderArgs`(5257-5299) | 本机一手 + `nvenc_h264.c:29`(默认 P4);p7 -24%、p7+multipass -35%、lookahead -12% | 编码 **1.25~1.5×** | 无;p4 就是默认档 |
| 6 | **RIFE/超分引擎不逐帧启动进程** | `EngineService.UpscaleDirAsync` / `InterpolateVideoAsync` 已是"整目录一次调用" | 代码核对:按批/按段调用,不是逐帧 | 已做过,无新增收益 | — |
| 6b | **`-j` 保持 `2:2:2`,不要用 `4:4:4`** | 三引擎启动参数 | **§7 一手源码**:`heap_budget /= jobs_proc_per_gpu` → 提高 proc 线程会**缩小自动 tile**,净效果多为负;`-j 1:4:1` 在仓库实测里产出 87% 坏帧 | 避免负收益 + 避免静默坏帧 | 无(不动默认值最安全) |
| 7 | **补帧分块用自适应整图** | `RifeOnnxService.ResolveInterpTile` | 前轮实测(`RESEARCH_50SERIES.md` §3.1 #2):1080p 整图比 512 分块快 **2.7~2.9×** | 补帧 2.7~2.9×(已提交) | 大图仍走分块 |
| 8 | **ONNX 分块上限按显存放宽** | `EsrganOnnxService.cs:850` | 前轮实测:768→1024 由 360 → 327 ms/Mpix(**约 9%**) | 超分 **约 9%** | 8GB 上 1280 崩、1536 OOM |
| 9 | **临时目录按介质类型选盘,而不是按剩余空间** | `EngineService.TempRoot`(793-807) | 本机一手:同机 NVMe 1685 MB/s 写;**机械盘场景未测** | 机械盘机型上可能巨大 | 需先实测机械盘 |
| 10 | **ONNX EP:维持 DirectML,不迁 CUDA** | `EsrganOnnxService` / `RifeOnnxService` EP 选择 | 前轮本机实测:RIFE 上 CUDA EP 慢 **4.6×**、超分 1024 分块慢 3.4~4×,还要 +3357 MB 运行库 | 0(避免负收益) | — |

---

## 7. 回答 D 之二:三个引擎的 `-j` / `-t` 语义与安全取值(一手源码)

### `-j load:proc:save` 到底怎么解析

三个引擎共用的解析代码(本轮从上游源码核对):

| 字段 | 含义 | 解析方式 | 源码位置 |
|---|---|---|---|
| 第 1 段 | `jobs_load` = 图像**解码**线程数(OpenMP) | `%d` | `waifu2x/src/main.cpp:471-472`;`rife/src/main.cpp:494-495` |
| **中间段** | **每个设备的处理线程数,且是逗号分隔的"按设备列表"** | `parse_optarg_int_array(...)`,取**第一个冒号之后**的整段 | 同上 |
| 第 3 段 | `jobs_save` = 图像**编码**线程数 | `%*[^:]` **贪婪匹配到最后一个冒号**再 `%d` | 同上 |

→ 这就解释了官方 README 里为什么能写 `-g -1,-1,0,1 -j 2:4,4,2,1:4`:**中间段是 4 个设备各自的值**。
→ 而 `4:4:4` 在单 GPU 上意味着"4 个处理线程共享 1 个设备",与"每个设备 4 线程"是同一件事——**但下一条说明它会反过来变慢**。

### 为什么"提高 proc 线程"会**变慢**(决定性机制)

`waifu2x/src/main.cpp:818-822` 的上游注释与代码(逐字):

```c
// multiple gpu jobs share the same heap
heap_budget /= jobs_proc_per_gpu[gpuid[i]];
```

自动分块的 tile 尺寸是从 `get_heap_budget()`(可用显存预算)推出来的,**一旦同一张 GPU 上的处理线程数 >1,显存预算就被按线程数整除** → 自动 tile 从 400 掉到 200 再到 100(上游的档位阶梯)。
**tile 越小 = 重叠浪费越大 = 单帧越慢**。也就是说:

> **在单 GPU 上提高 `-j` 的中间字段,是用"更小的 tile"换"更多的并发",而这两者的净效果在上游的显存预算逻辑下通常是负的。**

再加上两条:

- `rife/src/main.cpp:860-863`:N 个 proc 线程**共享同一个 `RIFE*` 对象**(同一个 Net、同一条 Vulkan 队列);
- `rife/src/main.cpp:259`:工作队列深度**硬编码为 8**,上游自己标了 `// FIXME hardcode queue length`。

另外,设备侧线程的 `num_threads` 被强制为 1(`waifu2x:855`、`rife:823`)——**真正的 GPU 推理是单线程的**,`-j` 增加的是"同时提交的帧数",不是"算力"。

### 结论:`-j` 的安全取值

| 场景 | 取值 | 依据 |
|---|---|---|
| **单 GPU(本文目标机型都是单卡)** | **`-j 2:2:2`** | 与上游 README 自己给的"large-size images 用 2:2:2"一致;中间段保持 **2**(不要 4) |
| 只提高解码 | `-j 2:2:2` 已覆盖;`jobs_load` 可试 2→3 | `load` 是 CPU 侧 OpenMP,不影响显存预算 |
| 多设备 | 才用 `-g a,b,c -j L:p1,p2,p3:S` 这种写法 | 官方 README 原文 |
| **本 1080p→4K 工作负载** | **不要用 `4:4:4`** | 上面 `heap_budget /= jobs_proc_per_gpu` 机制 |

⚠️ 仓库内部还留着一条**实测反证**(前轮记录,与本文机制吻合):`-j 1:4:1` 的计算扫描出现 **87% 坏帧率** + `vkQueueSubmit failed` 且**退出码仍为 0**。
→ 所以 `-j` 调高的代价不只是"变慢",还包括**静默产出坏帧**。**在没做 A/B 之前不要动默认值。**

### 各引擎推荐参数(仅含有一手依据的项)

| 引擎 | 推荐 | 依据 | 能否确定 |
|---|---|---|---|
| `rife-ncnn-vulkan` | `-j 2:2:2`;输出格式见 §5.2;**不要 `-x`/`-z`/`-u`** | `-x`=spatial TTA、`-z`=temporal TTA、`-u`=UHD mode(README);三者都是"多算几次换质量",TTA 的代价倍率**本轮未测** | `-j` 可确定;TTA/UHD 代价**未测** |
| `realesrgan-ncnn-vulkan` | `-t 0`(自动分块)、`-j 2:2:2`、不用 `-x` | `-t 0` 走 `get_heap_budget()` 自动档;上游 README 的 `-x` 是 TTA | 自动分块的**绝对耗时未测** |
| `waifu2x-ncnn-vulkan` | `-t 0`、`-j 2:2:2` | 同上;waifu2x 的 optstring 里**没有 `-p`/fp16 开关**(负向证据:用户无法从 CLI 打开 fp16) | fp16 相关**无法从 CLI 控制** |

**注意**:`-t`(tile)在 **rife** 上**不存在**(rife 的 optstring 是 `…vxzuh`,无 `t`);`-t` 只属于 realesrgan/waifu2x。应用若给 rife 传 `-t` 需确认不会导致解析错位。

### 一个必须在报告里说清的**供应链问题**

随包的 `engines\rife\rife-ncnn-vulkan.exe`(2026-08-28)**不是上游发布版**:它含有 `-q`、`-l` 两个**上游 master 与 2022 备份都没有的**参数,而**该 fork 的身份本轮未能识别**(二进制里没有可指认的第三方仓库字符串)。前轮 `RESEARCH_50SERIES.md` 已把这条列为待办(§3.1 #4),本文补充确认:**它确实是 patched build,不是官方 release。**

---

## 8. 三条被**一手源码证实**的既有假设(不用再怀疑)

| 结论 | 一手依据 | 对本文的意义 |
|---|---|---|
| **`concat` demuxer 会把时间轴量化到 25 fps —— 仓库里那条注释是对的** | 内层 image2 子解复用器的 `framerate` **默认硬编码 25**:`img2dec.c:588` `{ "framerate", …, AV_OPT_TYPE_VIDEO_RATE, {.str = "25"} …}`;`:211-213` 直接用它设流时基 `avpriv_set_pts_info(st, 64, s->framerate.den, s->framerate.num)`。concat 侧:duration 用 `av_parse_time(..., 1)`(微秒)解析,再用 `av_rescale_q(..., inner_time_base)` 重标(`concatdec.c:511-519`、`:558-559`、`:804-810`) | **`-framerate <fr> -i pattern` 必须保留**,不要为了"性能"换成 `-f concat`;换算正好复现仓库日志里的 0.0333→0.04、0.1→0.08/0.12 |
| **waifu2x/realesrgan 的自动 tile 由显存预算决定,且同卡并发会分摊预算** | `waifu2x/src/main.cpp:816-848`(`heap_budget /= jobs_proc_per_gpu[...]`,tile 阶梯 400/200/100/32) | **不要去"优化"自动分块**;`-t 0` 就是对的 |
| **rife-ncnn-vulkan 只声明"多设备"并发,不声明单卡多进程并发** | 官方 README 只给了 `-g -1,-1,0,1 -j 2:4,4,2,1:4` 这一个并发示例 | **"单卡跑两个引擎进程能不能加速"没有任何上游背书** → 必须实测,不能假设 |

---

## 9. ONNX Runtime 侧的版本订正(本轮取到一手文档)

⚠️ **订正前轮/任务描述里的两处版本前提**:

| 之前的说法 | 本轮核到的**官方**说法 | 出处 |
|---|---|---|
| "ORT 1.30 要求 CUDA 13" | **CUDA 13.0 在 ORT 1.27 就成为默认**;明确标注 `CUDA 12.8 + cuDNN 9.x` 的区间是 **1.21.x ~ 1.26.x** | ORT 官方 CUDA EP 兼容性表 |
| "ORT CUDA EP 的 `-gencode=arch=compute_120,code=sm_120` 是 sm_120 的守卫" | **`main` 分支上找不到这条字面量**。当前 main 用 CMake `CUDA_ARCHITECTURES` 表达(`120-real`/`120-virtual`);`cmake/CMakeLists.txt` 里的 `CUDA_VERSION >= 12.8` 守的是 **FP4**,不是 gencode | `cmake/onnxruntime_providers_cuda.cmake`、`onnxruntime_cuda_source_filters.cmake`、`cmake/CMakeLists.txt` |

→ 这是**负向结论**(没找到那条字面量),按本文规矩如实记录,不引用没亲眼见到的行号。

**DirectML 状态(逐字原文,本轮核实)**:官方页 `https://onnxruntime.ai/docs/execution-providers/DirectML-ExecutionProvider.html` 的一个 `<p class="note">` 提示框(是 Note,**不是** Warning):
> **Note: DirectML is in sustained engineering.** DirectML continues to be supported, but new feature development has moved to WinML for Windows-based ONNX Runtime deployments. WinML provides the same ONNX Runtime APIs while dynamically selecting the best execution provider based on your hardware.

⚠️ 措辞校准:**文档从没说"推荐替换"或"将弃用"**,只说"新功能开发已转到 WinML"且"DirectML 继续被支持"。引用时不要夸大。
**佐证(一手 NuGet flat-container 元数据)**:`Microsoft.ML.OnnxRuntime.DirectML` 最新 = **1.24.4**;`Microsoft.ML.OnnxRuntime` / `.Gpu` 最新 = **1.30.0**(main 的 `VERSION_NUMBER` 已是 1.31.0,未发布)。

**TensorRT EP 的官方表到 1.22 / TRT 10.9 / CUDA 12.0-12.8,且没有任何 Blackwell 行** → 对 50 系不加分。

**一条便宜且值得做的实验**(文档支持,本轮未实测):CUDA EP 的 `cudnn_conv_algo_search` **默认是 `EXHAUSTIVE`** → 改 `HEURISTIC` 是解释"CUDA EP 在 RIFE 上慢 4.6×"的最便宜的一次实验;另外把前轮试过的 `arena_extend_strategy` **改回默认 `kNextPowerOfTwo`**(改 `kSameAsRequested` 实测是恶化,且文档对该选项**没有任何性能承诺**);8GB 卡上可试 `cudnn_conv_use_max_workspace=0`。
→ **但注意**:所有 DirectML/CUDA 的实测数字都来自本机 **Ada sm_89**;**Blackwell 上的 EP 相对次序完全没有测过** —— 这是上新卡后最该先跑的一件事。

---

## 10. 不确定 / 待实测(不得当结论)

### 10.1 本轮**未能**用一手来源收口的项

1. **DirectML "sustained engineering" 的逐字原文与确切 URL** —— 前轮研究代理确认过(原文 `DirectML is in sustained engineering. For new Windows projects, consider WinML instead.`,且 ORT 的 DirectML 包停在 1.24.4 而后主线已到 1.30.x),**本轮未复核**,引用时需重新核对页面的当前措辞与版本号。
2. **ORT 在 sm_120 上的 EP 横评(DirectML vs CUDA EP vs TensorRT EP vs Vitis/WebGPU/MIGraphX)与精确版本矩阵** —— 本轮未完成。已知锚点:ORT ≥1.21 的 sm_120 内核受 `CUDA ≥ 12.8` 守卫(`v1.21.0/cmake/CMakeLists.txt:1566-1571` 的 `-gencode=arch=compute_120,code=sm_120`);ORT 1.30 要求 CUDA 13。**其余待补。**
3. **fp16 vs fp32 模型转换对速度与画质的影响** —— 本轮未完成,不做结论。
4. **RIFE / realesrgan / waifu2x 的 `-j load:proc:save` 语义与安全取值** —— 本轮**已用源码坐实**(见 §7),替换掉之前的"未完成"。
   官方 README 原文(逐字,`nihui/rife-ncnn-vulkan` master):
   > `load:proc:save` = thread count for the three stages (image decoding + rife interpolation + image encoding), using larger values may increase GPU usage and consume more GPU memory. You can tune this configuration with `4:4:4` for many small-size images, and `2:2:2` for large-size images. The default setting usually works fine for most situations. If you find that your GPU is hungry, try increasing thread count to achieve faster processing.

   ⚠️ **README 这句话有个隐藏陷阱**:`-j` 的**中间字段不是"数量"而是"每个设备一个值"的列表**，且 `load`/`save` 字段的解析用的是 `swscanf(optarg, L"%d:%*[^:]:%d", …)`（`%*[^:]` 贪婪吃到**最后一个**冒号）。所以 `4:4:4` 与 `2:4,4,2,1:4` 都是合法写法，含义完全不同。详见 §7。
5. **`-t` tile / `-x` TTA / `-z` TTA / `-u` UHD 的源码级语义与代价** —— 本轮的 CLI 层确认:RIFE 官方 README 列出 `-x enable spatial tta mode`、`-z enable temporal tta mode`、`-u enable UHD mode`、`-t` 未在官方 README 的 `Full Usages` 里出现(realesrgan/waifu2x 的 `-t` 是 tile size)。**逐参数代价未测。**
6. **ncnn 是否读环境变量** —— ⚠️ **机制描述需订正**(结论方向不变)。
   前轮 `RESEARCH_50SERIES.md` §1.2 写「**ncnn 不读任何环境变量**(对 `getenv` 的检索无命中)」——**这一句是错的**。
   一手源码(`Tencent/ncnn` master,本轮下载核对的源码树):`src/simplevk.cpp:101-121` **确实读 3 个环境变量** ——
   `VK_ICD_FILENAMES`、`VK_DRIVER_FILES`、`NCNN_VULKAN_DRIVER`。
   但整个 `src/` 树里 `getenv` 只有这 3 处,且**全部是 Vulkan loader / ICD 选择相关**——**没有任何计算、线程、精度(fp16)相关的环境变量旋钮**。
   → **订正后的准确表述**:"ncnn **会**读环境变量,但只读驱动/loader 选择这 3 个;**不存在可用于提速或关掉某项查询的环境变量**。"
   → 对 §1.2 那条原始结论(「无法用环境变量关掉 cooperative-matrix 查询」)而言,**结论仍然成立**,但**理由要换成这一版**——否则别人一 grep 到 `getenv` 就会认为前轮结论造假。

### 10.2 需要**真机/新素材**才能定论

7. **用户两台 50 系机器上 `硬件编码器探测:` 那一行到底打印了什么** —— 这是"编码占 96%"的**唯一直接判据**。若显示 `可用 [h264_nvenc, ...]` 却仍慢,则瓶颈在别处(片源时长/帧数异常);若显示 `全部不可用`,则 §1 的机制就是根因。**必测。**
8. **用户 4K 素材的真实 JPG 体积与 SW 解码 fps 的复现** —— 本机 0.4 MB/帧 得 86.5 fps,用户报 39.6 fps。**用同一批真实 4K 帧对比 SW vs NVDEC,才能定"软解 JPG 到底是不是那一台的第一道墙"。**
9. **`-temporal-aq`、`-multipass qres`、`-bf` 档位的画质/速度曲线** —— 本机只测了 `spatial-aq`、`bf 3 b_ref_mode middle`、`multipass fullres`。
10. **AV1 NVENC 的画质与播放端兼容性** —— 本机只测了速度(83 fps)。
11. **机械盘机型的临时目录往返代价** —— 本机是 NVMe(D: 与 C: 同在一块 Great Wall GT70 2TB NVMe 上)。**未测。**
12. **本机物理内存总量** —— §5.2 的内存盘算术需要它;本轮未核(`Get-CimInstance Win32_PhysicalMemory` 未取)。
13. **4 段并行编码后的拼接是否无损(段边界 GOP/时间戳)** —— 本机只测了吞吐(-f null),没测拼接正确性。**必测后才能上线。**
14. **`ReencodeDirPngToJpg` 在真实任务里被触发的次数** —— 从代码看 1215 与 1710 都是无条件调用,但"目录里没有 .png 时空跑"的分支(注释写"目录已是 JPG……则空跑")意味着**实际触发次数取决于 RIFE 输出的是 png 还是 jpg**。**需要用一次真机任务日志确认,才能算准档 1 的收益(25 分钟 vs 50 分钟)。**
15. **`-thread_queue_size` 在 n7.1(而非 master)里的默认队列深度与实现落点** —— 本轮只核到官方**文档**原文与 master 的 `img2dec.c`;**"加了到底快多少"没有实测**(本轮没测,因为它按证据推断只能是抖动量级)。要坐实需在本机跑一组 `-thread_queue_size 2048` 的 A/B。
16. **Blackwell(GB20x / sm_120)的 NVENC 代数与官方支持矩阵** —— 本轮**没有取到 NVIDIA 官方页面**(`nvenc.c:257-289` 只有"API 版本 → 最低驱动"表,不含显卡代数)。因此"50 系上 H.264/HEVC/AV1 各由第几代 NVENC 承担"**未验证**。本文所有 NVENC 速度数字都来自本机 **Ada sm_89**,不能当作 Blackwell 的绝对值。
17. **`-temporal-aq`、`-surfaces` 的实际影响** —— 本轮未测(`-surfaces` 的语义本轮也未读到源码行)。

### 10.3 已知的**与现有代码注释冲突**之处(建议一并修)

18. `VideoService.cs:3046-3049` 的注释称"50 系上常见的失败是 `-preset p4` 被拒(exit -22),去掉 preset 就能硬编"。**本机实测否定该因果**:失败发生在 **NVENC API 版本检查**阶段,与 preset 无关;能工作的构建上 p1/p4/p7 全部正常,不能工作的构建上去掉 `-preset` 照旧失败。建议把注释改为"NVENC API 版本 vs 驱动版本"这条真实机制,否则后续排障会继续走错方向。
19. §8 已**证实**(而非推翻)仓库里"`concat` 会量化到 25 fps"的那条注释 —— 该注释可以保留,并建议把源码行号(`img2dec.c:588`、`concatdec.c:804-810`)补进去,便于以后有人怀疑时直接查。

---

## 附:复现方法

| 产物 | 路径(均为本轮自建,可删) |
|---|---|
| 编码吞吐基准脚本(全部数字来源) | `_research_tmp\bench_encode4k.ps1` |
| NVENC 裸能力 / 天花板上限脚本 | `_research_tmp\bench_ceiling.ps1` |
| 解码对比脚本 | `_research_tmp\bench_decode.ps1` |
| GDI+ PNG→JPG 基准(C# 源码) | `_research_tmp\bench_gdi.cs` |
| 旧 ffmpeg 的 ASCII 中转副本(供基准调用) | `C:\ffbench\{ff71,ff81,frames}` |
| 上游源码取证 | `_research_tmp\src_nvenc.c`、`src_nvenc_h264.c`、`src_rife_readme.md`、`src\A\nvEncodeAPI.h`、`src\A\nvenc_n71.c`、`src\A\nvenc_n80.c` |

**关键复现命令**(驱动 <610 的机器上应先看到失败、再用旧构建看到成功):
```powershell
# 1) 证明随包构建被 API 版本挡住
& 'D:\deep\alh-pro\engines\ffmpeg\ffmpeg.exe' -hide_banner -v verbose -f lavfi -i "testsrc2=size=1280x720:rate=30:duration=0.4" -c:v h264_nvenc -f null -
#    -> Driver does not support the required nvenc API version. Required: 13.1 Found: 13.0

# 2) 证明旧构建可用(注意:旧构建是 shared build,必须连 DLL 一起,且路径含中文时先拷到 ASCII 路径再调)
& 'C:\ffbench\ff71\ffmpeg.exe' -hide_banner -v verbose -f lavfi -i "testsrc2=size=1280x720:rate=30:duration=0.4" -c:v h264_nvenc -f null -
#    -> Loaded Nvenc version 13.0 / Nvenc initialized successfully / supports NVENC
```

---

## 来源清单

| 来源 | 它"拥有"什么结论 |
|---|---|
| 本机实测(本文 §1~§2 全部表格) | 所有吞吐数字、两条 ffmpeg 的 NVENC 成功/失败、磁盘 1685/1356 MB/s、GDI+ 72~84 ms/帧 |
| `libavcodec/nvenc.c`(FFmpeg master,本地下载核对 `:257-289`、`:321-331`) | "API 版本 → 最低驱动"映射表;`NvEncodeAPIGetMaxSupportedVersion` 的运行时硬门 |
| `libavcodec/nvenc_h264.c`(FFmpeg master,`:29`、`:40`、`:108-116`、`:144-161`) | `preset` 默认 P4;`tune` 默认 hq;`spatial-aq`/`temporal-aq`/`b_ref_mode`/`multipass` 的默认值 |
| `libavcodec/mjpegdec.c`(FFmpeg master,`:2976-2982`;本地下载核对) | **mjpeg 解码器只有 `AV_CODEC_CAP_DR1`,无帧线程/片线程** → "软解 JPG 无法并行"的根因 |
| `libavformat/img2dec.c`(FFmpeg master,`:364`、`:399-418`、`:413`、`:465-469`、`:440-441`;本地下载核对) | image2 每帧 `io_open()` + `avio_size()` + 按整文件长度 `av_new_packet()`;每帧被打 `AV_PKT_FLAG_KEY` |
| `ffmpeg.org/ffmpeg-all.html`(`-thread_queue_size` 段,`:6205` 附近;本轮下载核对) | 原文 "By default ffmpeg only does this if multiple inputs are specified." → 该选项只能吸收抖动,不能并行解码 |
| `FFmpeg/nv-codec-headers@master include/ffnvcodec/nvEncodeAPI.h:118-119` | 当前 header 是 `13.1`,即"新构建要求驱动 ≥610"的源头 |
| `FFmpeg@n7.1` / `FFmpeg@n8.0` 的 `nvenc.c`(本地对比) | n7.1 最高只到 12.2 分支;n8.0 已有 13.1 分支 → header 升版的时点 |
| `nihui/rife-ncnn-vulkan` README(master,本地下载) | `-j load:proc:save` 语义原文;`-x/-z/-u/-g/-m/-n/-s` 语义;`-f pattern-format` 支持 jpg/png/webp;官方 ffmpeg 配方(抽帧→补帧→`-framerate 48 -i %08d.png`→libx264) |
| `nihui/rife-ncnn-vulkan` `src/main.cpp`(本地下载核对 `:120`、`:215`、`:259`、`:460`、`:494-495`、`:823`、`:860-863`) | 输出格式与 JPEG 质量 `stbi_write_jpg(...,100)`;`-j` 的 `swscanf` 解析;硬编码队列深度 8;设备线程 `num_threads=1`;多 proc 共享同一 `RIFE*` |
| `nihui/waifu2x-ncnn-vulkan` `src/main.cpp`(本地下载核对 `:471-472`、`:816-848`、`:855`) | `-j` 解析;`heap_budget /= jobs_proc_per_gpu` 与自动 tile 阶梯 400/200/100/32;optstring 无 fp16 开关 |
| `xinntao/Real-ESRGAN` `realesrgan-ncnn-vulkan` 的 `wic_image.h`(本地下载核对 `wic_encode_image` / `wic_encode_jpeg_image`) | 输出容器走 WIC;**JPEG 质量硬编码 `ImageQuality = 1.0f`** |
| `Tencent/ncnn` master `src/simplevk.cpp:101-121`(本地下载核对) | **ncnn 确实读环境变量**(`VK_ICD_FILENAMES`/`VK_DRIVER_FILES`/`NCNN_VULKAN_DRIVER`),但全是 loader/ICD 选择,无计算/线程/fp16 旋钮 → 更正前轮"不读任何环境变量" |
| `libavformat/concatdec.c`(FFmpeg master,`:511-519`、`:558-559`、`:804-810`) | concat 的 duration 经 `av_parse_time(...,1)` → `av_rescale_q(inner_time_base)`;**证实**仓库"25 fps 量化"注释 |
| `libavformat/img2dec.c:588`(FFmpeg master) | image2 的 `framerate` 默认**硬编码 `"25"`**,`:211-213` 直接用它设流时基 |
| ONNX Runtime 官方 DirectML EP 文档 `https://onnxruntime.ai/docs/execution-providers/DirectML-ExecutionProvider.html` | **逐字** DirectML "sustained engineering" 原文(Note 框,非 Warning);nuget flat-container 佐证 DML 包 1.24.4 vs 主线 1.30.0 |
| ONNX Runtime 官方 CUDA EP 兼容性表 + `cmake/onnxruntime_providers_cuda.cmake`、`onnxruntime_cuda_source_filters.cmake`、`cmake/CMakeLists.txt` | **订正**:CUDA 13 默认始于 ORT **1.27**;`12.8+cuDNN 9.x` 区间是 **1.21.x–1.26.x**;main 上用 CMake `CUDA_ARCHITECTURES`(`120-real`/`120-virtual`)而非 `-gencode` 字面量;`CUDA_VERSION >= 12.8` 守的是 FP4 |
| 仓库源码 `ImgUpscalerUI\VideoService.cs`、`EngineService.cs` | 现流水线形态、`EncoderArgs` 现行参数、`EnsureHwProbeAsync` 探测顺序、`TempRoot` 选盘策略、`ReencodeDirPngToJpg` 调用点 |
| `RESEARCH_50SERIES.md`(同仓库前轮) | ONNX DirectML 分块扫描、CUDA EP 慢 4.6×、RIFE 自适应分块 2.7~2.9×、DirectML sustained engineering |

---

## §11 本地复测与落地(2026-09-11,主代理在本机复核:RTX 4060 Laptop 8GB / 驱动 572.83)

### 11.1 复测结论(与本文正文的异同)

| 复核项 | 本文说法 | 本地复测结果 | 结论 |
|---|---|---|---|
| NVENC API 版本门(§TOP1) | 随包 `engines\ffmpeg` 要 13.1(需驱动 ≥610);`发布版\engines\ffmpeg` 与 `ffmpeg8` 能在 572 上用 13.0 | **证实前半**:`engines\ffmpeg`(N-126247-20260823)`exit=-40 / Required: 13.1 / minimum driver 610.00`;`发布版\engines\ffmpeg`(n7.1-20240930)**实测 exit=0 成功** | 证实 |
| —— 补充 | —— | `engines\ffmpeg8` 在**仓库工作目录里不存在**(`BackupFfmpegPath` 返回 null,仅安装版 `发布版\` 下有) | 新发现 |
| 引擎写出格式的代价(§TOP2 相关) | 建议"去掉两次 `ReencodeDirPngToJpg`" | 4K 输出(1920×1080→3840×2160,waifu2x models-cunet 2x,同参数各跑 2 次取平均):**引擎写 PNG 2.98 秒/帧(5.6MB)、写 JPG 2.02 秒/帧(2.8MB)、写 webp 9.99 秒/帧** | 证实并量化(省 31%) |
| NVDEC 硬解(§TOP3) | 4K 86.5→131.7 fps(+52%) | **端到端仅 +2%**(软解码 39.6 fps / NVDEC 4595 fps,但 `libx264 veryfast` 34.5 → 35.1 fps)——软编时瓶颈是编码器本身,不是解码 | **限定条件**:NVDEC 只在硬编生效后才有意义,须与 §TOP1 配套,不能单独当提速项 |
| GDI+ 4K PNG→JPG 成本(§TOP2) | 72~84 ms/帧 | 独立复测 **0.17 秒/帧**(口径:含 24bppRgb 转换 + q96 编码;素材熵不同) | 同量级 |

### 11.2 已落地的改动(commit `748d33b`)

**超分段改为"引擎直出 JPG"**(`EngineService.UpscaleDirAsync` 新增 `outFormat` 参数,视频链路传 `"jpg"`):
- 零画质代价的纯提速 —— 视频链路本来就要把超分输出转成 JPG(q96)再合帧;改后省掉「引擎 PNG 压缩 + 应用侧 PNG 解码 + q96 重编码」整段,而引擎写的是 **q100**,画质更好。
- 4K 下每帧约省 1.0 秒(引擎侧 0.96s + 应用侧约 0.12s − JPG 判黑解码 0.05s)。
- 同步修掉**三处会因扩展名变化而静默失效**的地方(新增 `EngineService.EnumerateImageFiles`):
  进度看门狗按 `*.png` 数帧(改后永远数到 0 → 喂不了狗 → 可能误判挂起)、
  非原生倍率缩回循环枚举 `*.png`(改后整段跳过 → **3x 目标会输出 4x**)、目录黑帧巡检 `HasBlackPng`;
  以及 `VideoService.WatchDirProgressAsync` 同款计数。
- 图片/分块路径与 ONNX 稳定引擎仍走 PNG:`outFormat` 默认 `png`,行为一字不变。

### 11.3 仍未证实 / 未做

1. **用户那台"编码占 9376s/9806s"的根因仍未定** —— 需要用户日志里 `编码实测:编码器=…,帧数=…,耗时=…s` 与 `阶段耗时拆分:处理阶段…+编码/封装…` 两行;两台机器驱动 610.62/616.64 均 ≥610,机制与本机(572→软编)不同,**不做猜测**。
2. **超分直出 JPG 未做端到端跑片验证**:需真机跑一次视频任务,确认帧号连续、画质与黑帧防线正常(已静态核对 upOutput 重复槽回填按基名取 `.jpg`、`DefectiveFramesAllComeFromNearBlack` 按基名 jpg/png 双试、`WriteFallbackFrame` 写 `.jpg`、合帧模式 `frame_%06d.jpg` 均与 `.jpg` 一致)。
3. **补帧(rife)仍写 PNG**:其 `-f pattern-format` 同样支持 jpg,理论上可再省一段(本轮未动,避免一次改两个引擎)。
4. 本文的 NVENC 绝对数字全部来自 Ada(sm_89),**不是 Blackwell 实测值**。
