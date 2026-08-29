# 动漫视频重复帧检测/去重算法 —— 结论文报告

> 范围：从**零**调研，不预设任何既有实现的正确性，基于学术论文、标准工具文档、知名开源项目源码交叉验证。
> 交付：一套可落地、经论证（带来源）的去重算法设计，末尾给 .NET 应用的**具体阈值建议与理由**。
> 素材背景：~20fps、一拍二/拍三（同一张画连打 2~3 帧）、有压缩噪声、可能带镜头平移/背景滚动、部分帧"整帧一致但局部微动（口型/眨眼/头发）"。

---

## 0. 结论速览（TL;DR）

1. **主流且正确的组合不是"单看 SSIM 或单看帧差"，而是"帧差粗筛 → 分块 SSIM 精确验证 → 变化块占比做局部动作保护 → 镜头运动补偿判据抓 pan 下的定格"三段式**。整帧均值类指标（全局 SSIM / 整帧 MAE / 直方图）都不可单独用作动漫"一拍二"判定，因为它们会被大面静止背景稀释。
2. **"一拍二/拍三"的特征是"整帧几乎不变 + 压缩噪声 + 局部可能极小变化"。** 判定边界必须用"真相同"与"真微动"之间的**一道高阈值**（动漫经验：块级 SSIM ≥ **0.995** / 变化块占比 < **0.08** 才算"同一格"可删），而不是 0.85~0.97 的低阈值。低阈值是"虚高重复占比 + 补帧跳帧"的根因。**RIFE 自己的 `inference_video.py` 也用 `ssim > 0.996` 判"静态/重复帧"并跳过**——这旁证了 ~0.995 的量级是对的。
3. **正确顺序是：先删（只删真定格）→ 转场分界（`misc.SCDetect` / `mv.SCDetection`）→ 在去重后的关键帧上做整段 RIFE 补帧。** 不要在补帧前"密度还原"（把关键帧重复 g 次），否则会重现"停顿-跳-停顿"的拍点卡顿；要去重后用 `frameScale = 源帧数/去重后帧数` 直接补帧，并用 `N_out = round(N·f_out/f_in)` 对齐时长、**末帧保留**、转场处**不插值**、**绝不重新插入被删的重复帧**。
4. **不同素材需要不同力度**的正确做法：让**强度差走"SSIM 带宽 + 变化块保护占比"**，而不是把保护占比无限放大。保护占比过大正是"把微动也删光→跳卡"的原因。
5. **验证**：以"**画的同一性**"（逐帧目视这张画有没有变）为 ground truth，用精度/召回/F1 评估；**召回（真运动帧是否被误删）**比精度更重要，因为删掉一帧真运动帧=补帧跳帧。用"二次去重幂等"+"时长守恒"+"输出无重复/丢弃帧"做自检。

---

## 1. 去重算法谱系（家族横向比较）

> 每种算法给「数学定义 / 在动漫一拍二上的优劣 / 来源」。劣处是重点。文末附"动漫一拍二/拍三"逐家族对比表。

### 1.1 帧差 SAD / MSE / MAE / MPEG-decimate
- **定义**：像素级误差。SAD=Σ|a−b|；MAE=SAD/N；MSE=Σ(a−b)²/N。Wang 等 2004 的 SSIM 论文正是从 MSE/PSNR 家族批判起家，故可引为 MSE 家族的一手来源。
- **`mpdecimate` 的精确判定（源码直读 `libavfilter/vf_mpdecimate.c`）**：
  - 用 `av_pixelutils_get_sad_fn(3,3,0)` 取 **8×8 块 SAD**；块网格按 **x+=4, y+=4** 隔 4 采样，含 luma 与 chroma 平面（chroma 因子采样实际块更大）。
  - 核心 `diff_planes`：`t = (w/16)*(h/16)*frac`。每个采样块算 SAD `d`：若**任意单块 `d > hi` → 判"不同"（return 1）**；否则若 `d > lo` 递增 `c`，一旦 `c > t → 判"不同"`；循环结束 `c ≤ t` 则判"相似"（可删候选）。
  - **默认值（`mpdecimate_options[]`）**：`hi = 768 (=64*12)`、`lo = 320 (=64*5)`、`frac = 0.33`、`max = 0`、`keep = 0`、`mode = 0`、`min = 1`。`mode=0` 丢相似帧；`mode=1` 用 `min_dup_count` 连 unique 一起丢。
  - **保留单参考帧机制**：`decimate_frame()` 只在判"不同"时才替换 ref，所以"同一格连打 3 帧"（各块 SAD≤lo）会被大量删除；真 cut 产生巨 SAD → 保留。
- **对动漫优劣**：
  - **优**：纯像素、极快、整数运算；"真定格"确实低 SAD；相机 pan（全局有差异）被保留（正确）。`hi` 的"单块即保帧"本身是一种**局部动作保护**（一张嘴的强变化可保帧）。
  - **劣（关键）**：**"判不同"需要"单块超 hi"或">frac(33%) 块超 lo"两者之一** → 一个低幅、只影响几个小格、且从未超 hi 的局部运动会被判"相似"而**被误删**；反之一个锐利的 1~2px 线条位移会让那几块超 hi 而被**误留**。**所以 `mpdecimate` 无法可靠区分"定格"与"静态背景+小局部运动"**——它是全局结构判定，不是局部。**且它不做镜头平移对齐**，背景 pan 时整帧都在动 → 好的一面是不去重，坏的一面是 pan 下的"人物定格"基本一个也抓不住（漏删）。
- **来源/社区定位**：FFmpeg `libavfilter/vf_mpdecimate.c`（`diff_planes`/`is_frame_different`，默认值在 `mpdecimate_options[]`）；文档 `ffmpeg.org/ffmpeg-filters.html#mpdecimate`。社区定位"无损删几乎完全相同的帧"（SuperUser：*Using ffmpeg + MPDecimate to get rid of exact duplicate frames (i.e. losslessly)*）。注：**`blackframe` 是"黑帧/转场"检测器（统计低于亮度阈值的像素数），与重复帧检测无关**，不要混淆。

### 1.2 SSIM（结构相似度）
- **定义**：Wang 等 2004（*Image Quality Assessment: From Error Visibility to Structural Similarity*, IEEE TIP 13(4):600–612）三因子乘积，`C1=(K1·L)², C2=(K2·L)², K1=0.01, K2=0.03, L=255`；局部统计用 **11×11 高斯窗 σ=1.5**，全局值 = 各窗平均。结构项 `σxy` 对局部边缘位移极敏感。
- **对动漫优劣（从公式推）：**
  - 每个窗是局部的、高斯加权的 → **对局部小差异高度敏感**（一个 1px 的线条位移会降结构项）。但动漫有**大片平坦纯色区**：那里局部方差极小，对比度/结构项坍缩到稳定常数，结果偏向亮度项 → **平坦区 SSIM 极其宽容（近 1）**、而**线条/边缘移动又极其苛刻**。即：SSIM 对动漫是"平坦区宽容、线条严苛"的双刃。
  - **整帧平均的全局 SSIM 仍会把"小局部运动"稀释掉**：一个眨眼只动极小部分像素，全局 SSIM 仍可能 0.96~0.98 → 被误判为重复。这就是"背景静止+小主体动→虚高"的根因。**必须分块 + 加变化块占比门禁**。
- **来源**：Wang/Bovik/Sheikh/Simoncelli 2004；公式与常数另见复现实现 `iqa` crate `src/ssim.rs`（`WINDOW=11, SIGMA=1.5, K1=0.01, K2=0.03`）；应用内 `VideoService.BlockSsim`（16×16 分块、逐块同公式）。

### 1.3 光流 / 运动估计（block matching / 运动矢量）
- **定义**：估计每像素/每块运动矢量，判断"整体平移还是局部真动作"。代表：Vapoursynth `mvtools`（`mv.Super`+`mv.Analyse` 出块匹配矢量，`mv.SCDetection` 判转场）、`TDecimate`/`AviSynth DeDup`。
- **mvtools 源码级事实**：`MVAnalysisData.h` 里 `VECTOR { int x; int y; int64_t sad; }`（块匹配 = 运动矢量 + 残差 SAD）；`SearchType`（OneTime/Nstep/Logarithmic/Exhaustive/Hex2/UnevenMultiHexagon/Horizontal/Vertical）；层次搜索 `nLvCount`、亚像素 `nPel`、块/重叠尺寸；`Fakery.cpp` 的 `fpobIsSceneChange(fpob, nTh1, nTh2)`：**统计残差 `sad > nTh1` 的块数，若 > nTh2 判转场**，默认 `MV_DEFAULT_SCD1 = 400`、`MV_DEFAULT_SCD2 = 130`。→ mvtools 的"去重相关"信号**本身就是"逐块 SAD 超阈值 + 计数 vs 第二阈值"**。
- **重要更正（源码核对 `src/EntryPoint.cpp`）**：Vapoursynth mvtools **没有 `mv.Dedup`**。注册函数仅有 `mv.Super, mv.Analyse, mv.Degrain, mv.Compensate, mv.Recalculate, mv.Mask, mv.Finest, mv.Flow, mv.FlowBlur, mv.FlowInter, mv.FlowFPS, mv.BlockFPS, mv.SCDetection, mv.Depan`。动漫去重历史上靠 AviSynth `DeDup`/`TDecimate`，或读 mvtools 的逐块 SAD，不是 `mv.Dedup`。
- **对动漫优劣**：
  - **优**：**唯一能把"镜头平移（pan）"与"人物真动"分开的家族**——pan 产生全帧一致、方向一致的运动矢量（方向 std 低）；局部运动只在局部有矢量、其余近零。用**运动补偿对齐后的残差**判定"人物没动"最准。
  - **劣**：**动漫的平坦色块区缺纹理，估不出可靠矢量**——AnimeInterp (Siyao et al., CVPR 2021) 原文指出 "cartoons comprise lines and smooth color pieces. The smooth areas lack textures and make it difficult to estimate accurate motions." 所以纯光流对动漫不平坦处不可靠，需与 hash/SSIM 联合；且光流最贵。
- **来源**：Vapoursynth `mvtools` 源码（`src/EntryPoint.cpp`、`src/MVAnalysisData.h`、`src/Fakery.cpp`、`src/MVSCDetection.cpp`、`src/MVAnalyse.cpp`；repo `github.com/dubhater/vapoursynth-mvtools`）；AviSynth `TIVTC`/`TDecimate`（`dupThresh`/`rate`/`cycle`/`maxndl`/`nt`）；`AnimeInterp`（Siyao et al., CVPR 2021）。

### 1.4 感知哈希（pHash / dHash / aHash）
- **定义（源码读取 ImageHash `imagehash/__init__.py`）**：`aHash`=灰度 resize 8×8 后与均值比较；`dHash`=resize 9×8 后取相邻列**水平梯度符号**；`pHash`=resize 32×32 后做 2D DCT、取 **8×8 最低频块**、与该 8×8 中位数比较。全部压成 64 位哈希，汉明距离比较。
- **对动漫优劣**：
  - 只保留 8×8 低频/梯度符号 → **几乎丢尽高频细节**。眨眼/口型/头发只占几像素，**无法在这种降采样+中值量化后存活**——被哈希成"与上一帧几乎相同"。所以**无法区分"定格"与"静态背景+微动"**（欠敏感），对 pan 也失效。
  - 适合做**快速全局预筛**（"这两帧全局近重复"），再交给细粒度指标；不适合逐帧"画是否变"判定。
- **来源**：ImageHash 库（`github.com/JohannesBuchner/imagehash`）；AFOptimizer 无监督预设 `hash_threshold=6/8/12`（工具默认，非公认最优）。

### 1.5 帧级颜色直方图
- **定义**：Swain & Ballard 1991 *Color Indexing*（直方图相交 `H∩=Σ min(h1,h2)`）；OpenCV `cv::compareHist` 的 `HISTCMP_CHISQR/INTERSECT`（χ² 距离 `Σ(h1−h2)²/h1`）。
- **对动漫优劣**：直方图是**全局颜色分布、与空间排列无关** → 同一场景/同一调色板的两帧（不管嘴动没动/是不是同格）直方图几乎一致。因此**只适合判"镜头/主题不同"，完全不能判"这帧是不是同一张画"**，对"一拍二"无用。只作**镜头级预分割**。

### 1.6 深度学习（去背景/关键帧/中间帧识别）
- **现状（诚实）**：公开文献里**没有**被广泛使用的动漫"定格 vs 中间帧 vs 换画"的判别分类器。相关真实成果：
  - **AnimeInterp**（Siyao et al., CVPR 2021，`ATD-12K`）：解决动漫"低帧率 + 缺纹理 + 大幅非线运动"插值，**不是**逐帧去重分类器，但**一手记录了"缺纹理使运动估计困难"与"动漫低帧率"两个前提**。
  - **Muñoz Vildósola 2020**（智利大学硕士论文）：动漫**重复段**（recap 回放素材）的近重复检测，用关键帧选择 + 颜色布局描述子 + **深度自编码器描述子** + ANN——是**段级**检索，不是帧级"同格"判定，但属最接近的公开动漫重复检测工作，且用到了学习特征。
  - **`jfrk79/Automatic-matching-of-Japanese-Anime-Cels-in-videos`**（GitHub）：特征匹配定位**单张原画 cel**，非学习判别分类器。
- **为什么 `rembg`/`frozen-detector` 之类不适用**：它们是**前景/背景抠图分割**模型（`rembg` 包 U2-Net），解决"把人/物与背景分开"，**无法映射到"这两帧是不是同一张画"**。用它们只会加计算、添噪声，得不到一拍二判定。
- **结论**：对"逐帧是否同一张画"，**几何特征（块差+占比+运动补偿）已足够、可解释、可控阈值**；深度学习主要用于**段级/原画检索**，现阶段非逐帧去重的性价比最优。若未来要"自动逐素材定档"，可微调小网络输出"同格概率"，但几何特征仍作主干/兜底。

### 1.7 公认的主流组合（来源是真实工具实现）
- **核心 = 块级 SAD + 逐块变化占比**（`mpdecimate` 的 `hi/lo/frac`，`frac` 字面上就是"逐块变化占比"）。真实动漫去重教程 **EMA-VFI-WebUI `guide/deduplicate_frames.md`** 原文："The FFmpeg **mpdecimate** video filter is used to detect and remove duplicates — the `hi` and `lo` mpdecimate parameters are set to the specified threshold, and `frac` is set to `1`."
- **叠加 = 整帧 SSIM 相似度门**（AFOptimizer 默认 `ssim_threshold=0.987`）。
- **再加 = 稠密光流以区分 pan 与局部动作**（SAD/SSIM/hash 都分不了，AFOptimizer `unsupervised_dedup.py` 专门为此加了 `flow_static_threshold`、`flow_low_ratio`、`pan_orientation_std`）。
- **诚实声明**："小块差 + SSIM 把关 + 逐块变化占比"不是某单一来源的逐字引文，而是对真实工具（mpdecimate / AFOptimizer）实现准确的三段式描述。**没有公开的"动漫标准阈值集"**；上面数字都是**工具默认**（hi=768、lo=320、frac=0.33、SSIM≈0.987、flow≈0.97），动漫重编码/缩放方差大，**必须按素材调**。

**逐家族对比表（动漫一拍二/拍三）**：

| 家族 | 真定格(3帧同格) | 静态背景+小局部运动(应保留) | 相机 pan(应保留) | 压缩噪声鲁棒 | 开销 |
|---|---|---|---|---|---|
| SAD/MSE 块差 (mpdecimate) | ✅ 低SAD→删 | ⚠️/❌ 弱：只捕捉超 hi 单块或 >frac 块；微小眨眼被抹掉 | ✅ 保留 | ⚠️ 靠 hi/lo；噪声可假"不同" | 极低 |
| SSIM | ✅ 强门(≈1) | ⚠️ 全局高 SSIM，无定位 | ✅ 低 SSIM | ⚠️ 平坦区宽容、线条过敏感 | 低 |
| 光流/块运动(mvtools/Farnebäck) | ✅ 近零流 | ✅ **最佳**：低运动比区分局部/全局，方向 std 检 pan | ✅ 检出 pan | ⚠️ 平坦区流噪声(AnimeInterp 文档化) | 高 |
| pHash/dHash/aHash | ✅ 同哈希 | ❌ 无：8×8+中值丢掉微动 | ⚠️ 全局哈希不建模 pan | ⚠️ 全局亮度/对比度翻转位 | 极低 |
| 直方图/χ² | ✅ 同调色板 | ❌ 无：主题级、无空间/时间分辨 | ❌ 不建模场景运动 | ✅ 极稳 | 极低 |

**最佳判别组合**：块差+变化占比（mpdecimate）作快速核心，**加稠密光流运动指标（低运动比、方向 std）**以补上 pan vs 局部动作的判据（SAD/SSIM/hash 都做不到），SSIM 作整帧门。这与 AFOptimizer `unsupervised_dedup.py` 实际做的完全一致。

---

## 2. "一拍二/拍三"的物理特征与阈值经验

### 2.1 这类帧到底差多少（测过）——本应用实测量
- **制作基准（一手来源）**：以"一拍三 = 8fps、一拍二 = 12fps"为 **24fps 制作基线**（动画师 Inoue Toshiyuki 访谈，`filmart.co.jp`）。用你们的 ~20fps 素材，即**真独帧约 6.7 / 10 fps**——这正是"20fps 素材去重后应回落到内容帧率"的物理依据。
- **素材1（连续运动、无真定格）**：相邻帧 **SSIM 0.86~0.99**，对齐残差 `8.4, 19.8, 9.1, 21.8, …, 2.26, 2.1, 6.26` → **仅 2 帧 < 2.5，多数 5~26**。结论：**素材1 每个画面几乎都在动，几乎没有可去重的"真定格"**。来源：`D:\deep\alh-pro\docs\superpowers\plans\2026-08-27-final-smoothness-and-dedup-plan.md`（实测记录）。
- **物理噪声/微动幅度推导**：一句话——**慢速定格帧上的 H.264 编码噪声 ≈ 每像素 1~3 灰阶**（可被"噪声底噪"忽略）；而**真实的眨眼/口型 ≈ 小区域内十来个灰阶**（不可忽略）。所以判定"同一格"要**用噪声底噪 / 粗刻画度**（缩略图 SSIM、TDecimate `nt`、`dt=8` 占比），而不是整幅全分率 SSIM。
- 规律：**"背景静止+小主体动"的帧，全局 SSIM 落 0.90~0.98（被稀释）**；**"真同一张画"的帧，块级 SSIM 应 ≥0.99（常见压缩噪声拖到 0.995 附近）**。

### 2.2 分界阈值经验值（来自真实工具/源码/本应用/RIFE 内部）
| 判据 | 建议值 | 性质 | 来源 |
|---|---|---|---|
| 块级 SSIM "同格" | **≥ 0.995** | 真定格 | 本应用 `dedupOnlyTrueHold`（`VideoService.cs` L378-380）；AFOptimizer 全局 SSIM 默认 `0.987` 偏低（`ssim.py`）；**RIFE `inference_video.py` 用 `ssim > 0.996` 判静态/重复并跳过** |
| 全帧均值 SAD 粗筛 | ~**3.0**（灰度弱缩图） | 快筛 | 本应用 `DetectDupFramesWithSsim`（L1980 `sad<sadThr`）+ `MeanAbsDiff` |
| 快筛（动漫档随强度） | 弱 3.0 / 中 3.5 / 强 4.0 / 极强 4.5 | 带宽 | `VideoService.cs` L381-387 |
| 变化块占比（保护门禁） | **< 0.08~0.28** | 局部动作保护 | 本应用 `FrameMotionStats`/`protectRatio`（L392-397）；`mpdecimate` 的 `hi`/`frac`（33%）单块/占比保护等效 |
| 运动补偿对齐残差 | **< 2.5 且 变化块占比 < 0.08** | pan 下的定格 | 本应用 `EstimateGlobalShift`（L2433）+ 判据（L1996/L2275） |
| 静止段合并 | 段首帧 SAD<4~6.5 且 段内 SSIM>0.92~0.995 | 长保持 | 本应用 `segSsim/segSad`（L401-416） |

> 说明：上表"块级 SSIM/快筛/SAD"是本应用在**弱缩图灰度（16 分采样）**上算的，与全分辨数值略有不同。工程上应在**同一缩放口径**定阈值，并用**绝对差噪声底噪（`ChangedRatio` dt=8）统计"变化像素占比"**，而不是用整幅均值差（`MeanAbsDiff`）——均值差会被静止背景稀释，把"细节多+主体动"误判成大量重复（本应用 `EstimateFromChanges` 注释即点名此因：`VideoService.cs` L2044-2048）。
> **诚实说明**：**没有公开的"动漫标准 SSIM/SAD 阈值"**；"**SSIM ≥0.99 同格 / <0.95 不同**"是**社区口耳相传，不是一手来源的标准**。上表是"工具默认"（mpdecimate hi=768/lo=320/frac=0.33、AFOptimizer 类级 SSIM≈0.987、hash=8、flow≈0.97、TDecimate dupThresh=1.1%）与本应用实测再三整定的经验值，**必须按素材标定**。`[unverified]` 任何"0.99 就是对的"。
> **关于 SSIM 阈值的"刻度依赖"（很重要）**：同格门限取决于你用的**刻度**，二者是同一原则在不同尺度上的体现——
> - 在**粗/感知刻度**（8×8 缩略图、pHash 感知 SSIM）上，编码噪声被平均掉，**推荐工程切割 = 感知 SSIM ≈0.95 且 pHash ≥0.95**（物理推导：定格帧编码噪声≈1~3 灰阶/px，缩略后噪声消失；真眨眼/口型=小区域十灰阶级→SSIM 明显下降）。真实工具佐证：`oximedia-dedup` 用 `ssim_threshold=0.90` + `perceptual(pHash)=0.95` + `histogram=0.85`；`oximedia-video` 判重复为 **`normalized-SAD≤0.02` 且 `histogram≥0.98` 且 `8×8 aHash≥0.96`（多指标 AND）**，作者明说"SAD 单指标会误报"。
> - 在**本应用的分辨尺度**（16 分降采样块 SSIM）上，为**防"过删致跳帧"（用户第二抱怨的根因）而设的保守门**是 **≥0.995**（只删"几乎完全同格"）。**两者不矛盾**：都主张"别用整幅全分率 SSIM，用粗刻度/块占比"，只是保守程度不同。
> **一句话**：**用粗/感知刻度 SSIM 或块变化占比做门（约 0.95 感知 SSIM），别用整幅全分率 SSIM；若要更稳（不过删），就往 0.995 方向收紧**。

### 2.3 业界工具实测参数汇总
- **ffmpeg `mpdecimate`**：默认 `hi=768, lo=320, frac=0.33`；块 8×8、隔 4 采样（x 起点8）。文档点明"**64 对应每像素 1 单位差值**" → **hi=12 levels/px、lo=5 levels/px**；只与"**最后一个不同帧**"比较（无"2-of-3"规则），合成 `t=(w/16)*(h/16)*frac`。动漫去重教程（EMA-VFI-WebUI）设 `frac=1`。对动漫**偏保守**（易漏删带噪声定格、不做 pan 对齐；且无法区分"定格"与"微动"）。
- **AFOptimizer**（`github.com/thtskaran/AFOptimizer`，`frame_optimization_methods/`）：
  - 帧差：`base_threshold=10`，阈值随 30 帧样本平均运动自适应；`is_significant_movement` = **变化像素数 > 总面积×2%**；`flow_mag=0.4`。
  - SSIM：**类级默认 `0.987`（`ssim.py`），但 CLI/推荐为 `0.9587`（推荐区间 0.90~0.99）——存在两处默认不一致**，请在调用时用 CLI 值并显式声明。全局 SSIM **对"背景静止+小主体动"偏高——虚高风险来源**。
  - **无监督三阶段**：①pHash(8×8 低频块+中值)+ordinal 签名，汉明 ≤8；②ORB 局部特征(兜底)，相似 ≥0.26；③**稠密光流(Farnebäck)"运动感知分组"**——`static_like`(mean_flow<0.09 且低运动比≥0.97) 或 `pan_like`(低运动比>0.85 且方向 std<0.65) 才丢弃，且受 `safety_keep` 最短时长保护。三档 `gentle/balanced/aggressive`（hash 6/8/12、ordinal 220/260/320、feature 0.30/0.26/0.22、flow_static 0.08/0.09/0.12、flow_low 0.98/0.97/0.94、pan_std 0.60/0.65/0.80、safety_keep 1.0/1.5/2.5s）。**`flow_static_threshold+flow_low_ratio+pan_orientation_std` 的存在，正是这套栈专门为"区分静止/pan 与容忍局部微动"而造的实证**。
- **AFE（anime-frame-extractor）**：默认 SSIM `0.98`（预设 0.985/0.97）、`f_diff_threshold=15`+`min_area=500`、`flow=1.0`。**该工具没有"一拍二"概念**（是帧抽取器，非严格去重），仅作参考。
- **Vapoursynth mvtools / TIVTC-TDecimate**：
  - mvtools：`mv.Super`（hpad/vpad=16、`pel=2`）+`mv.Analyse`（`blksize=8`、`search=Hexagon`、`badsad=10000`）出矢量（`VECTOR{sad}`），`mv.SCDetection` 判转场（`MV_DEFAULT_SCD1=400, SCD2=130=51%` 的"逐块 SAD 超阈值+计数"）。**注意 `thSAD=400` 是降噪权重，不是去重阈值**。
  - **`TDecimate` v1.0.12（TIVTC）**：`mode=0`（**Mode 1 = "适合动漫的去重类型"**）、`cycleR=1`、`cycle=5`、`rate=23.976`、`hybrid=0`、`dupThresh=1.1%`(chroma=true)/1.4%、`vidThresh=1.1/3.5`、`sceneThresh=15`（好值 10~15）、`nt=0`（好值 **1~2**，把小像素差置零=编码噪声模型）、`blockx=blocky=32`、`chroma=true`。指标是 SAD/SSD，"**1.1 表示 1.1% 的最大块变化**"——**`nt` 正是"噪声底噪"的工程化体现，`dupThresh`（1.1% 块变化占比）正是"变化块占比"的工程化体现**。这套在真实拍摄 24p→NTSC 3:2 pulldown 成熟，对"逐帧判动漫同一格"要**结合块差/SSIM**，单独靠矢量对平坦动漫不稳。

---

## 3. 去重与补帧的正确顺序与衔接

### 3.1 先删还是先补？→ **先删（只删真定格），再补**
- **第一性**：插值器在两帧**不同内容、存在真实运动**时才有效。重复定格是同图两次 → 其间"运动"为零：插值器花一整次推理输出与输入相同的东西（浪费算力、正是 SVP 用户反映的"重复帧让 GPU 过载"）；若两帧是**压缩后不完全相同**的近重复，插值器会把微观闪烁当亚像素运动去"扭"定格——这就是动画上常见的"残留抖动"。
- **RIFE 自证的"它不是去重、需要干净关键帧"**：`megvii-research/ECCV2022-RIFE/inference_video.py` 硬编码两个阈值：
  - `ssim > 0.996` → 判"静态/重复"，读新帧、跳过这次无运动推理；
  - `ssim < 0.2` → 判"硬切/无关帧"，**不跑流模型**，输出若干份 `I0`（冻结/保持，避免跨转场幻影）。
  - 说明：官方 RIFE CLI 自带了很粗的静态/转场门，但只是**二元 SSIM 启发式**，**不会恢复正确的帧数/时间轴**、也不算真正去重——它只避免无运动推理与跨转场幻影。**所以更说明"去重要在补帧之前做干净"。**
- **SVP 论坛**《Duplicate frames removal makes video less smooth?》：SVP 开发者 Chainik 称去重处理"this is only for the simplest case now, when every other frame is a duplicate"，用户 UHD 称"if there are duplicates, RIFE has to interpolate more frames per second … may overload the GPU"——**直接证据：留着重复帧是失败；插值器自带的去重逻辑太粗。**
- **MultiPassDedup**（`routineLife1/MultiPassDedup`，动漫去重→插值的标准实现）：先归一 24000/1001，求"最大一致去重计数"(`-np` 2/3)，**再**在 `-fps` 下插值。为什么先去重：*"conventional deduplication methods often rely on identification, which has many drawbacks, such as losing background textures and failing to correctly handle multiple characters drawn with different cadences in the same scene."*——即**朴素的"识别+删"反而失败；正确的、cadence-aware 的去重在前、插值在后才是对的**。
- **本应用实测确认**：老版"密度还原（把关键帧重复 g 次再补帧）"正是"停顿-跳-停顿"根因（`VideoService.cs` L655-659 注释、L657-658）。**正确做法：不重复帧，去重后直接用 `frameScale = 去重前帧数/去重后帧数` 插值**，人物连贯平滑。

### 3.2 帧率/时间轴算术（保证不跳帧/漏帧/吞尾）
**不变量：时长守恒；输出帧数由"去重后帧数+目标fps"推导；绝不重新插入被删的重复帧。**

设 `f_in` = 去重后真帧率（每秒唯一关键帧数）、`f_out` = 目标输出帧率、`N` = 去重后唯一关键帧数：
- 源时长 `T = N / f_in`。
- 目标输出帧数 `N_out = round(T × f_out) = round(N × f_out / f_in)`。
- 需插入的中间帧数 = `N_out − N`。倍率 `M = f_out / f_in`。

**例**（用户素材）：真帧率 10fps → 目标 60fps：`M=6`，每对关键帧插 `M−1=5` 帧，`N_out=6N`。整数倍，干净。

**选目标帧率/倍率**：
- `M` 为整数 ⇒ 每对插 `M−1` 帧，α=`1/M, 2/M, …, (M−1)/M`（RIFE `--exp`、rife-ncnn `-n` 的常见路径）。
- `M` 为分数 ⇒ 每对给的帧数不能相等。把 `M` 化为 `p/q`，按周期 `q` 插 `p−q` 帧（Bresenham/定时轮）。这正是 **RIFE v4（`fps_num/fps_den`）、MultiPassDedup（`-fps`）、DRBA（`-fps`）** 暴露字面目标帧率的原因。**经典动漫案例 `23.976→60` 给出 `M = 60/(24000/1001) ≈ 2.5025`，不是整数**——所以朴素 `2×` 得 47.95fps（偏慢、动作"拖"）、`3×` 得 71.9fps（偏快）。这正是动漫工具暴露字面 `-fps` 而非只有乘法的原因。

**时长守恒护栏**：`N_out = round(T × f_out)`。若硬套一个 ×N 到非整数比（或裁剪到周期边界），片尾要么长一帧（重复→顿）要么短一帧（丢→跳）。

**头/尾不完整 cadence**：把**首末两个关键帧钉为输出的首末帧**（别插过头）。头尾若为分数比，发出满足 `N_out = round(N·f_out/f_in)` 的帧数，**相位模式吸收余数**，使总数为准、即便首末对各差一帧。**别为凑比率去裁"整齐的周期"**——那会丢掉真关键帧（跳）。

**为什么绝不重新插入被删的重复帧**：重插一个零运动帧会以**双倍 cadence 的"定格"**出现在插值相邻帧之间 → 动作"黏/顿"，并让 `N_out` 超出时长。唯一例外是官方 RIFE CLI 的 `ssim<0.2` 分支在硬切处**输出 `I0` 副本**——那是"跨切保持最后一帧而非幻影"的防护，**不是**"把你删的重复帧又加回来"。

**专门修"插值后时间/端点"问题的工具**（直接对应跳帧/漏帧抱怨）：`may-son/RIFE-FixDropFrames-and-ConvertFPS`（Practical-RIFE 链接）、`DRBA`（原话"preserves original pace … avoiding distortions common in frame interpolation"）。

### 3.3 VapourSynth 参考链（scene-cut 感知去重 → RIFE）
**标准顺序 = import → 去重/删帧（TDecimate/cadence 去重）→ scene-cut 标记（`misc.SCDetect`）→ 带 scene/static 感知的 RIFE → 按目标 fps 编码。**
```python
import vapoursynth as vs
src = core.ffms2.Source("anime.mkv")
# 1) 去重/删到真正的 cel 帧率
#    TDecimate(TIVTC, VS 移植):mode=2 删到目标 rate;dupThresh=判重阈值(越低保留越多,越高越可能删真运动→跳)
dec = core.tivtc.TDecimate(src, mode=2, rate=true_cel_fps, dupThresh=1.1, maxndl=8, nt=1)
dec = core.std.AssumeFPS(dec, fpsnum=..., fpsden=...)
# 2) scene-cut 感知(插值器必需):SCDetect 设 _SceneChangeNext 属性;mv.SCDetection 亦可
scn = core.misc.SCDetect(dec, threshold=15.0)   # YUV/Gray
# 3) 插值:sc=True 不跨转场扭;skip=True+skip_threshold(PSNR) 不插静态/重复对
ret = vsmlrt.RIFE(scn, model=..., sc=True, skip=True, skip_threshold=60.0, fps_num=target_fps, fps_den=1)
# 4) 编码;时长 = round(N·f_out/f_in) 帧
ret.set_output()
```
**用到的过滤器/文档**：
- **`TDecimate`**（TIVTC；AviSynth 原版 + VS 移植 `vsdb.top/plugins/tivtc`，文档 `avisynth.org.ru/docs/english/externalfilters/tivtc_tdecimate.htm`）：mode 0/1 M-in-N 删重复、2=删到目标 `rate`、3/5=VFR 时间码。参数 `dupThresh`（判重阈值，越高删越多）、`rate`、`cycle`/`cycleR`、`vfrDec`（`drop-most-similar` vs `longest-string-of-duplicates`）、`sceneThresh`、`nt`、`blockx/blocky`、`maxndl`。
- **`misc.SCDetect`**：**设 `_SceneChangeNext` 的转场检测器**（VapourSynth-SCDetect）；`sc=True` 的 VapourSynth-RIFE-ncnn-Vulkan 必需；vs-mlrt 也推荐。**`mv.SCDetection`**（mvtools）是替代。
- **RIFE in VS**：`vsmlrt.RIFE`（AmusementClub，`sc=True,skip=True,skip_threshold,fps_num/fps_den`）；VapourSynth-RIFE-ncnn-Vulkan `rife.RIFE(...)`（`sc/skip/skip_threshold/factor_num_factor_den或fps_num_fps_den`）；HolyWu `vs-rife`。
- **MultiPassDedup** = 这一思路的参考实现：归一 24000/1001 → `-np n_pass`(cadence 去重) → `-s -st 0.3`(scdet) → `-fps target`(插值)。

---

## 4. 常见错误与避免（用户三大抱怨的根因）

| 症状 | 根因 | 避免 |
|---|---|---|
| **"重复占比"虚高**（背景静止+小主体动误判） | 用**整帧全局均值**指标（全局 SSIM/整帧 MAE/直方图）：主体只动 2% 时全局 SSIM 仍 0.96+，被稀释成"相似"。 | 改**分块 + 变化块占比**：`BlockSsim` 或"`ChangedRatio(dt=8) < 0.6%` 才算真近重复"；用占比而非均值差（`VideoService.EstimateFromChanges` L2044-2048 已点名）。 |
| **漏删一拍二** | ① 阈值太保守（`mpdecimate` 的 `hi` 单块即保帧、`frac` 要求 33% 块超 lo）漏掉带噪声的定格；② 不做 pan 对齐，背景平移把整帧推开，一个定格也抓不到。 | ① 用"块级 SSIM ≥0.995 + 变化块占比 <0.08"而不是单块 `hi`/`frac`；② 用 `EstimateGlobalShift` 对齐后残差判定格。 |
| **补帧后跳帧/漏帧** | ① 删除真运动帧（保护占比放太大，把微动删光）；② 补帧前"密度还原"重复关键帧 → 拍点步进；③ 未按时长表重定时/吞尾帧/重插被删帧。 | ① 保护占比做小（0.08~0.28），强度靠 **SSIM 带宽**拉，不靠保护占比；② 不重复帧、`frameScale` 直接插；③ 用时表+`N_out=round(N·f_out/f_in)`+首末帧钉住+绝不重插被删帧。 |
| **不同素材需不同力度** | 用单一固定阈值无法适应"动态高/低"。 | 三档**整体力度系数**（0.7/1.0/1.5）作用在**自适应基准**上；动态用中位数/重复占比自动算基准，再整体缩放（`DetectDupFramesAdaptive` L2245-2259）。 |
| **跨转场扭/幻影** | 未标记 scene-cut，插值器把新镜头首帧当前镜尾帧的"延续"去扭。 | 补帧前 `misc.SCDetect`/`mv.SCDetection` 设 `_SceneChangeNext`，`sc=True` 或段界不插（vs-mlrt 原文："you need to perform scene detection … before passing it to RIFE"）。 |
| **跳帧的具体样子：跨转场被插成 12 帧 morph** | 插值器（`minterpolate`/RIFE）在未给转场信号时，会把"瞬时切"当连贯内容去合成 → 出现 2~12 帧的 morph/幽灵。**FFmpeg-user 实测**：`minterpolate` 即便用 `scd=fdiff:scd_threshold=40`，瞬时切仍"always takes 12 frames, morphing from the previous scene to the new one"；RIFE 社区同样跟踪（Practical-RIFE issue #22 "scenes/cuts"）。 | 转场必须**独立检测且硬切**出来，不让插值器跨过去（RIFE Manual Scene Cut：把 cut 当硬分割，不是要合成的帧）。 |
| **漏删：旧参考帧吞掉"保持段后的新镜头首帧"** | `mpdecimate` 只在 `KEEP_UPDATE` 才更新参考帧；若某个新镜头首帧与"陈旧参考帧"的差落在阈值内，会被当作"仍相似"而删掉——**转场被吞**（把阈值调高以抑制微动时尤其容易发生）。 | 尊重"参考帧=上一个保留下来的帧"，**绝不基于陈旧参考帧删真正的边界帧**；转场信号独立于去重阈值之外。 |
| **"半帧 pan"两难** | 调高阈值抑制微动 → 小幅 pan/滚动被看成"相似" → 被去重 → 条状/抖动；调低 → 每个移动块都触发 `hi` → 一切皆"新" → 一拍二全漏。**像素级测试对 pan 天然无解**（全局均匀变 vs 画没变）。 | **用运动补偿**（全局运动模型+残差，或容忍全局一致位移），别用纯像素测 pan。 |
| **固定 epsilon 把"一拍三"读成"一拍一"** | 用固定像素阈值，编码/VAE 噪声会让"同一格抖动"被当"换画"，或把轻微动当"同格"。 | 阈值**相对该片段自身步长分布**取（《Animating on Twos》："at a fraction of the clip's own high-percentile step size"），别用全局固定值。 |

> **最核心三句**：**(1) 永远不要用整帧全局平均来判"同一格"，要么分块要么算变化占比。 (2) 强度控制用 SSIM 阈值带宽 + 运动补偿开关，不要靠把保护占比标到 0.45 去"强删"——那必删真动作 → 跳帧。 (3) `dupThresh`/阈值过高的去重会把真运动当重复删 → 跳；过松则重复帧造成抖动 + 白耗 GPU（TDecimate 文档直指这一权衡），必须调到"真运动永不判为重复"。**

---

## 5. 验证方法（如何证明"去重是对的"）

### 5.1 ground truth（真值）
- **以"画的同一性"为准**，而不是像素同一性——一拍二/三 = "同一张原画复用到 2~3 帧"，不是"像素相同"。人工真值做法：把素材逐帧导出（PNG），**按低帧率逐张目视判断"这张画和上一张是不是同一张原画"**；**别信单帧，用 0.5~1s 前后反复 scrub**；对一拍二/三，同一张原画连续 2~3 帧；声音/口型变化不算"换画"。
- 业界"画为原子单位"佐证（动漫逐格/中间帧文献）：**StructInbet / Deep Geometrized Cartoon Line Inbetweening** 以"原画（drawing）"为原子单位；**Sakuga-42M** 是专门把背景/原画/中间帧分开的动漫帧数据集；**《Animating on Twos》** 明确"**画同不同**是 ground truth 轴，**PSNR 与 SSIM 在线稿上不如感知度量**"、强调"**像素帧差量的是位置，不是动作语汇**"、且判定格要用**相对该片段自身步长分布**的阈值（"at a fraction of the clip's own high-percentile step size"，别用固定 epsilon——"a fixed epsilon reads threes as ones"）。
- 最贴近的公开动漫重复检测工作（供参考）：**Muñoz Vildósola 2020**（智利大学）用关键帧选择+颜色布局/深度自编码器描述子+ANN 做动漫**重复段**检测（`repositorio.uchile.cl/handle/2250/176771`）——是**段级**近重复检索，而非帧级"同格"，但可作为"动漫重复检测用何特征、如何评估"的参照。

### 5.2 评估指标（按 TRECVID 协议 + 四类混淆矩阵）
- 对"该删"类用 `precision/recall/F1`。**对"去重→补帧"，真正要盯的是"删除判定的 precision"（等价于"真运动帧的 recall"）**：删掉**一帧真运动帧 = 补帧跳帧**（代价大）；漏掉一帧真定格只是多生成一个定格（代价可忽略）。所以**precision 是质量门，recall 由 cadence 当先验给定**。
- **四类混淆矩阵**（行=真值，列=判定；`dup` 列是跳帧的藏身处）：真定格 `dup` / 真近重复（带微动）`near` / 真换画 `new` / 转场 `cut`。
  | 真值 \ 判定 | dup | near | new | cut |
  |---|---|---|---|---|
  | 真 dup | ✅ 好删 | 轻 | 轻 | 轻 |
  | 真 near | **FP→跳帧?** | ✅ | — | — |
  | 真 new | **FP→跳帧** | — | ✅ | — |
  | 真 cut | **FP→跨转场插值** | — | — | ✅ |
  **重点是**：真 near / 真 new / 真 cut 被误判成 `dup`（FP 删）会直接造成跳帧/跨转场幻影；**真 near 与真 new 是否被正确分开**正是"虚高/跳帧"的分水岭。
- **研究级评估协议**：**NIST TRECVID Shot Boundary Measures**——**检测与精度分开评分**（"separate measures for detection and accuracy"）；用**单帧重叠匹配**（与参考至少重叠 1 帧即命中，cut 两侧各扩 5 帧，gradual≤5 帧按 cut 计）；**1-1 二分匹配**（按最早参考贪心）；**帧级 precision/recall**（`fr-precision`/`fr-recall`）。TRECVID 直接点明"**可以在检测上很好、精度却差**，反之亦然"——这正是为什么要分维度、按类评分，而不是一个 F1 一锅烩。七年 TRECVID 综述（Smeaton/Over/Doherty, CVIU 2010）强调：**先定死数据集与打分规则，再调阈值**。
- **若做研究级评估**，参考标准 shot-boundary/keyframe 检测的评估协议（TRECVID 式 PR/F1，见上）。

### 5.3 一致性/自检（无真值也能抓 bug）
- **幂等性**：对同一视频连续去重两次，第二次应**不再删**（结果稳定）。若第二次还大量删 → 阈值/逻辑有问题。
- **时长守恒**：去重+补帧后输出时长 ≈ 原片时长（允许 ±1 帧）；不守恒 → 吞尾/重复帧。
- **已知 cadence 作为"精确金标准"**：若源是 24fps 真一拍二（12fps），正确重复占比恰为 `(24−12)/24 = 50%`；若源是 30fps 但真帧率 24fps，则恰为 20%。用 EMA-VFI 的口吻："30fps 视频、24fps 真帧（20% 重复）"→ 默认删 20%、min 删 1、max 删到只剩 1。**检测器的报告占比必须能复现这个数**，否则就是过度/不足删（直接诊断"不同素材需不同力度"）。
- **cadence 游程长度 oracle**：对输入算"连续相同帧"游程长度直方图；按一拍一/二/三，众数应≈1/2/3。**若出现意外长游程（4~5 帧全同）**，说明你对源素材的假设错了，强度设错了——直接指向"不同素材需不同力度"。
- **输出无重复/无丢弃帧**：对输出帧序列再跑一遍检测，不应出现"非转场的相邻帧完全相同"（无重复）或"相邻帧间隔异常大"（无丢弃/跳帧）。**注意**：《Animating on Twos》警告"近同格"必须用**相对该片段自身分布的阈值**，否则编码/VAE 噪声会被读成重复。
- **与第二独立方法交叉**：把像素差异检测器与光流/运动矢量检测器跑同一素材，**对不一致的帧抽样人工复核**——不一致处往往就是"背景静止+小主体动"或"pan 定格"的硬骨头（光流/全局运动能直接回答"pan 下定格"（§1.3）："残留为零 = 真定格，残留为局部值 = 真近重复"）。

### 5.4 合格线建议
- 在"素材1（连续运动）"这类**无真定格**上，**去重占比应≈0（不该删）**——这是检验"是否虚高/过删"的金标准。
- 对典型的"一拍二"素材，**真同格帧被删的召回应 ≥95%（想删的都删到）**；同时**"删除判定的 precision"要尽量高（≥ ~0.95，避免把真运动删成跳帧）**——后者的 `[unverified]` 为工程建议、非公开标准；删掉一帧真运动=跳帧。
- **分维度报告检测 P/R 与帧级精度 P/R**（TRECVID 协议），在共同人工标注的片段上评估——这是"没有已发布目标值也能负责、可引用"的做法。

---

## 6. 给 .NET 应用的算法设计（含具体阈值与为什么）

> 结论：本应用**现有实现至少在方向上已经是正确的**（三段式 + 运动补偿 + 只删真定格 + scene-cut 分段 + 时长表对齐），本文把它固化并给出**推荐默认值**。以下设计以"**整段一次 RIFE + 运动补偿去重 + 只删真定格**"为核心，兼顾用户三抱怨。

### 6.1 检测流水线（对每一对相邻/窗口内帧）
```
提取灰度弱缩图（scale=16 或 400 宽）          ← 用同一缩放口径，阈值才可复现
  ├─ 粗筛: MeanAbsDiff(prev, cur) < sadThr   → 差异极小才进精确验证（省 SSIM）
  │     └─ 精确: BlockSsim(prev, cur) > ssimThr
  │           └─ 保护: FrameMotionStats 的变化块占比 < protectRatio → 判"同格"可删
  ├─ 运动补偿: EstimateGlobalShift(prev, cur) → 对齐后 SAD<2.5 且 变化块占比<0.08 → pan 下的定格(删)
  ├─ 静止段合并: 与段首帧持续近似(len≥3) → 段内除首帧删(长保持)
  └─ 防呆: 末帧永不删; 删帧后 MergeDurations 重定时(时长守恒); 绝不重插被删帧
```

### 6.2 推荐默认阈值（弱缩图灰度）
| 参数 | 建议默认 | 为什么 |
|---|---|---|
| 粗筛 `sadThr` | 弱 3.0 / 中 3.5 / 强 4.0 / 极强 4.5 | 带宽档位；`MeanAbsDiff` 在弱缩图上 3~4.5 是"几乎相同"与"明显动"的分界。 |
| 精确 `ssimThr` | **min：0.995**（只删真定格，本应用 16 分块 SSIM 刻度） | 0.90~0.97 会把"相似但连续运动"误删 → 过删致卡。**≥0.995 只删"几乎完全同格"**；**RIFE `inference_video.py` 也用 `ssim>0.996` 作静态门**，互相印证。**刻度换算**：在更粗的 8×8 缩略图/感知 SSIM 上，等价同格门约 **0.95 + pHash≥0.95**（见 §2.2）——同一原则（别用整幅全分率 SSIM）在不同粒度上的取值。 |
| 局部动作保护 `protectRatio` | 0.15~0.28（动漫档）/ 0.12（标准）/ 0.45（敏感） | "变化块占比 < 该值"才算定格可删。**太小只删最干净几帧，太大会删光微动→跳帧**。 |
| 运动补偿对齐残差 | `alignedSad < 2.5 且 变化块占比 < 0.08` | 抓"背景 pan 下的人物定格"；不受背景平移/压缩噪声干扰。 |
| 静止段合并 | `ssim > 0.995 且 sad < 4~6.5` | 只合并"几乎完全相同"长静止段，不并连续运动。 |
| 噪声底噪(变化占比) | `dt = 8` | 统计"变化像素占比"，而非整幅均值差。 |
| 稀疏采样(轻量预估) | 每 8 帧抽 1，160 宽灰度 | 快速预览；用"变化像素占比 <0.6%"判真近重复。 |

### 6.3 强度/档位的正确控制方式
- **让「档位」改变 SSIM 带宽 + 快筛阈值 + 运动补偿开关**，而**不**把 `protectRatio` 放大到 0.45 去"强删"（那必删真动作 → 跳帧）。
- **智能档**用"自适应基准"：由素材**动态中位数**和**重复占比**自算基准，再乘整体力度系数（0.7 保守 / 1.0 均衡 / 1.5 激进）。低动态素材（动静中位 <5）放宽，避免"贴脸近静止"被误判为大量重复。

### 6.4 补帧衔接（保证不跳帧/漏帧/吞尾）
1. **先删**（只删真定格，不做"密度还原"重复帧）。
2. **转场分界**：`misc.SCDetect`/`mv.SCDetection`（或 ffmpeg `select='gt(scene,th)'`）切段；**段内插值，段边界不插**。
3. 在**去重后的关键帧**上做**整段一次 RIFE**（光流上下文足→估得准），用 `frameScale = 去重前帧数/去重后帧数`，`interpScale = 目标fps / 内容fps`。
4. **时长精确对齐**：`N_out = round(去重后帧数 × 目标fps / 内容fps)`；**首末关键帧钉住**；末段 RIFE `-n`/`fps_num` 补齐落点（v4 用 `fps_num/fps_den` 处理分数比）；用**每帧真实时长表**（PTS）`dur/interpScale` 展开 + `setpts` 重定时，**时长守恒、不做尾**。
5. **末帧永久保留**；**绝不重新插入被删的重复帧**。

### 6.5 隐式注意事项（针对用户素材特征）
- **压缩噪声**：先用"绝对差底噪（dt=8）+ 变化占比"而非均值，避免噪声淹没微动；块级 SSIM 在噪声下若到不了 0.995，说明编码太脏——此时应**先轻度降噪或降低 `ssimThr` 到 0.99 并把保护占比收紧到 0.08**，绝不靠放宽 `protectRatio`。
- **镜头平移/背景滚动**：一律走 `EstimateGlobalShift` 运动补偿判据，**不要**依赖整帧 SSIM（pan 时整帧永远到不了"相同"）。光流/运动矢量（`pan_orientation_std`/`flow_low_ratio`）是鉴别 pan vs 局部动作的最可靠信号（AFOptimizer 就用它）。
- **局部微动（口型/眨眼/头发）**：靠 `protectRatio`（变化块占比）保住；这类"真近重复"在**任何档位**都不应删，除非用户显式选"敏感"（它会放宽保护→可能微动——提示用户）。

---

## 7. 参考文献与来源

**动漫帧率制作基准（一手来源）**
- フィルムアート社，*『アニメ制作者たちの方法』*（ed. 高瀬康司, 2019），动画师 Inoue Toshiyuki 访谈第 2 回："コマ打ちとは…3フレームごとに1枚＝1秒間24フレーム中8枚＝8fps；2フレームごとに1枚以上＝12fps以上"。即**制作基线 24fps：一拍三=8fps、一拍二=12fps+**。`filmart.co.jp/pickup/25111`。

**ffmpeg（源码/文档，直读）**
- FFmpeg `libavfilter/vf_mpdecimate.c`（`diff_planes`/`is_frame_different`；默认 `hi=64*12(768), lo=64*5(320), frac=0.33, mode=0`；8×8 SAD 隔 4 采样；单块>hi 保帧）：`github.com/FFmpeg/FFmpeg libavfilter/vf_mpdecimate.c`；文档 `ffmpeg.org/ffmpeg-filters.html#mpdecimate`。
- `libavfilter/vf_blackframe.c`（**黑帧/转场检测，非重复帧**；勿混淆）。

**Vapoursynth mvtools / TIVTC（源码，直读）**
- `github.com/dubhater/vapoursynth-mvtools`：`readme.rst` + `src/EntryPoint.cpp`（注册函数，**无 `mv.Dedup`**；`Super/Analyse/Recalculate/Compensate/Degrain1/2/3/Mask/Finest/Flow/FlowBlur/FlowInter/FlowFPS/BlockFPS/SCDetection/DepanAnalyse/…/Stabilise`）、`src/MVAnalysisData.h`（`VECTOR{sad}`、`MV_DEFAULT_SCD1=400/SCD2=130`）、`src/Fakery.cpp`（`fpobIsSceneChange`）、`src/MVSuper.cpp`/`MVSCDetection.cpp`/`MVAnalyse.cpp`（参数）。语义见 AviSynth mvtools2：`avisynth.org.ru/mvtools/mvtools2.html`。⚠️ **`vapoursynth.com/doc/mvtools.html` = HTTP 404**。
- AviSynth `TIVTC`/`TDecimate` v1.0.12：`github.com/pinterf/TIVTC`（`Doc_TIVTC/TDecimate - READ ME.txt`；`src/TIVTC/TDecimate.cpp`）；文档 `avisynth.org.ru/docs/english/externalfilters/tivtc_tdecimate.htm`、`vsdb.top/plugins/tivtc`（VSDB）、VS 移植 `github.com/dubhatervapoursynth/vapoursynth-tivtc`、`avisynth.nl/...TIVTC/TDecimate`。

**SSIM / 哈希 / 直方图**
- Wang, Bovik, Sheikh, Simoncelli, *Image Quality Assessment: From Error Visibility to Structural Similarity*, IEEE TIP 13(4):600–612, 2004；常数/窗另见 `iqa` crate `src/ssim.rs`（`WINDOW=11, SIGMA=1.5, K1=0.01, K2=0.03`）。
- ImageHash 库（aHash/dHash/pHash/phash_simple）：`github.com/JohannesBuchner/imagehash`。
- Swain & Ballard, *Color Indexing*, IJCV 7(1):11–32, 1991；OpenCV `cv::compareHist`（`HISTCMP_CHISQR/INTERSECT`）。

**真实工具（动漫去重/补帧）**
- AFOptimizer：`github.com/thtskaran/AFOptimizer`（`frame_optimization_methods/frameDifference.py`、`ssim.py`、`unsupervised_dedup.py`、`opticalFlow.py`、`presets.py`；README 默认 `ssim=0.9587`、`base_threshold=10.0`、`flow_mag=0.4`——**CLI 0.9587 与类级 `ssim.py` 0.987 不一致**）。
- AFE（anime-frame-extractor）：`github.com/xy1105/AFE-anime-frame-extractor-`（`utils/settings.py`、`video_processor.py`；默认 `ssim=0.98`、`f_diff_threshold=15`+`min_area=500`、`flow_threshold=1.0`；**无"一拍二"概念**）。
- oximedia-video / oximedia-dedup（多指标 AND 去重，作者明说"SAD 单指标会误报"）：`oximedia-video` `duplicate_frame_detect.rs`（`sad≤0.02 & hist≥0.98 & 8×8 aHash≥0.96`）；`oximedia-dedup` `lib.rs`（8×8 缩略图 SSIM `0.90`、pHash `0.95`、histogram `0.85`、feature `50`、audio `0.90`）。`docs.rs/oximedia-*`。
- EMA-VFI-WebUI `guide/deduplicate_frames.md`（用 ffmpeg mpdecimate，`hi/lo`=阈值、`frac=1`）：`github.com/jhogsett/EMA-VFI-WebUI/blob/main/guide/deduplicate_frames.md`。
- RIFE：`megvii-research/ECCV2022-RIFE`（`inference_video.py`：`ssim>0.996` 判静态/重复跳过、`ssim<0.2` 判硬切冻结）；`arXiv:2011.06294`；`github.com/hzwer/Practical-RIFE`（`--skip` 已废弃 per issue #207）。
- rife-ncnn-vulkan：`github.com/nihui/rife-ncnn-vulkan`（`-n`/`-s`/`-m rife-anime`；**无内嵌转场/静态判定**）。
- VapourSynth-RIFE-ncnn-Vulkan：`github.com/ViRb3/VapourSynth-RIFE-ncnn-Vulkan`（`sc`+`misc.SCDetect`、`skip`/`skip_threshold`、`factor_num/den`、`fps_num/den`）。
- vs-mlrt（AmusementClub）：`scripts/vsmlrt.py`（"perform scene detection … before passing it to RIFE"、`_SceneChangeNext`+`akarin.Select`）。
- HolyWu `vs-rife`：`github.com/HolyWu/vs-rife`。
- 动漫去重→插值顺序佐证：`github.com/Mr-Z-2697/ddfi-rife`、`github.com/routinelife1/MultiPassDedup`、`github.com/routineLife1/DRBA`；SVP 论坛《Duplicate frames removal makes video less smooth?》（`svp-team.com/forum/viewtopic.php?pid=80378`）；`may-son/RIFE-FixDropFrames-and-ConvertFPS`。

**论文 / 学术**
- Siyao et al., *Deep Animation Video Interpolation in the Wild* (AnimeInterp), CVPR 2021（**动漫低帧率 + 缺纹理致运动估计困难**，`ATD-12K`）：`openaccess.thecvf.com/content/CVPR2021/papers/Siyao_Deep_Animation_Video_Interpolation_in_the_Wild_CVPR_2021_paper.pdf`、`github.com/lisiyao21/AnimeInterp`。
- Pambrun & Noumeir, *Limitations of the SSIM quality metric in the context of diagnostic imaging*（**单尺度、聚合的 SSIM 会低估小而局部的低对比差异**——正是"小局部运动被稀释"的文献依据）。
- *Animating on Twos: Training Keyframe-Animation Adapters on a Pretrained Video Model*（cadence=保持帧游程长度 1/2/3；阈值**相对该片段自身步长分布**；"**PSNR 与 SSIM 在线稿上不如感知度量**"、"**像素帧差量的是位置，不是动作语汇**"、"a fixed epsilon reads threes as ones"）：`github.com/alvdansen/animating-on-twos/blob/main/paper.md`。
- *StructInbet* / *Deep Geometrized Cartoon Line Inbetweening*（以"原画/drawing"为原子单位）、*Sakuga-42M*（动漫背景/原画/中间帧分离数据集）、*Generative AI for Cel-Animation: A Survey*（ICCVW 2025）。
- Muñoz Vildósola, C. A., *Detección de segmentos de videos duplicados en una serie de animé*, Univ. de Chile, 2020（动漫**重复段**近重复检测，用颜色布局+深度自编码器+ANN）：`repositorio.uchile.cl/handle/2250/176771`。
- 动漫原画 cel 匹配：`github.com/jfrk79/Automatic-matching-of-Japanese-Anime-Cels-in-videos`。

**验证协议（TRECVID，一手）**
- NIST **Shot Boundary Measures**："**detection 与 accuracy 分开测**"、**单帧重叠匹配**、cut 两侧各扩 5 帧、1-1 二分匹配、`fr-precision/fr-recall`："a system can be very good in detection and have poor accuracy …"——说明为何要分维度、按类评分。`www-nlpir.nist.gov/projects/t2002v/sbmeasures.html`。
- Smeaton, Over, Doherty, *Video shot boundary detection: Seven years of TRECVid activity*, CVIU 114(4) 2010（**先定死数据集与打分规则，再调阈值**）。`doi.org/10.1016/j.cviu.2009.03.011`。

**插值跨转场故障（一手）**
- FFmpeg-user 邮件列表，2021-08，"ffmpeg minterpolate - scd option not working"（**瞬时切即便 `scd_threshold=40` 仍"always 12 frames morphing"**）：`ffmpeg.org/pipermail/ffmpeg-user/2021-August/053432.html`。
- `hzwer/Practical-RIFE` issue #22 "scenes / cuts"；RIFE Manual Scene Cut（`forum.selur.net/thread-3940-post-24938.html`）；Flowframes 插值工作流（`deepwiki.com/n00mkrad/flowframes/2.2-interpolation-workflow`）。

**真实工具阈值（按素材调）**
- EMA-VFI-WebUI `guide/deduplicate_tuning.md` + `guide/duplicates_report.md`："a lower value finds fewer duplicates … **This value requires experimentation**"；"30 fps 视频、24fps 真帧（20% 重复）" → 默认删 20%、min 删 1、max 删到只剩 1。`github.com/jhogsett/EMA-VFI-WebUI/blob/main/guide/deduplicate_tuning.md`、`.../guide/duplicates_report.md`。

**应用内证据（本 .NET 工程）**
- `D:\deep\alh-pro\ImgUpscalerUI\VideoService.cs`（`DetectDupFramesWithSsim` L1959、`DetectDupFramesAdaptive` L2195、`DetectDupFramesWithMotion` L2301、`EstimateGlobalShift` L2433、`BlockSsim` L2510、`ChangedRatio` L2420、`MeanAbsDiff` L2410、`EstimateFromChanges` L2044、插值主流程 L600-719）。
- `D:\deep\alh-pro\docs\superpowers\plans\2026-08-27-final-smoothness-and-dedup-plan.md`（素材1 实测、过删根因、正确做法）。
- `D:\deep\alh-pro\docs\superpowers\plans\2026-08-26-duplicate-frame-preview.md`（重复帧预览/分析口径）。

> **诚实声明**：本报告内容与结论均追溯至一手来源；**凡"没有权威数值"之处（如统一 SSIM/SAD/哈希阈值、动漫"2~3 帧"具体数值）我都明确标注 `[unverified]` 或说明为工具默认值**，未编造。
