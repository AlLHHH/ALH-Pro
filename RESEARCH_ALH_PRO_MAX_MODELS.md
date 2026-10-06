# ALH Pro Max(付费版)· 模型选型与上架方案(2026-10-04)

> 本文回答一个问题:**要上 Steam 卖 48 元,该加哪些模型、走什么后端、改哪些文件、上架前必须先解决什么。**
> 数据来源:①本仓库既有实测与代码级清单(`RESEARCH_NEW_MODELS_LANDING.md`、`RESEARCH_50SERIES.md`、
> `licenses/模型权重来源与校验值.md`、`THIRD_PARTY_NOTICES.txt`);②2026-10-04 对 OpenModelDB / HuggingFace /
> 各模型官方 README 的逐模型核实;③第三方推理基准(neosr 官方表、ailia 实测)。
> **许可结论一律标出处**;本文任何一条都不等于「可以商用」的最终放行 —— 放行前必须逐字核对原始声明。

---

## 0. 三行结论

1. **许可闸比技术闸更容易绊倒产品**:被广泛推荐的 `4x-UltraSharp`(游戏画面首选)实为 **CC-BY-NC-SA-4.0(禁商用)**;
   与之同架构、同速度的 `4xNomos8kSC` 是 **CC-BY-4.0**,替换**零工程成本**。
2. **只有两种架构能"零引擎改动"进包**:`RRDBNet`(= 现有 `realesrgan-x4plus` 同构)与
   `SRVGGNet Compact`(= 现有 `realesr-animevideov3` / `realesr-general-x4v3` 同构)。
   其余(HAT / DAT / SwinIR / SPAN)要么 ncnn 缺算子,要么 ORT+DirectML 算子缺失或慢一个数量级。
3. **付费版第一批建议只加 2 支 ncnn 超分模型**(`4xNomos8kSC`、`4xLSDIRCompactC3`)
   **+ 1 支可选 ONNX 人脸修复**(`GFPGAN v1.4`);**不碰** SPAN / HAT / DAT 的二进制来源与 AGPL 前端。

---

## 1. 决策点(等拍板,未定之前不动代码)

| # | 决策 | 影响面 |
|---|---|---|
| D1 | 是否转付费 | 决定许可策略(自有代码是否继续 MIT)、`声明.md` / 官网「免费公益」叙事、广告位去留 |
| D2 | 免费版砍到什么程度 | 决定功能切分(见 §4)与老用户口碑风险 |
| D3 | 定价与渠道 | 48 元 Steam 仅 / 官网直购仅 / **双轨**(推荐,见 §5.6) |

---

## 2. 终选候选表

速度口径:①**同构**=与现有在跑模型架构相同,直接照现有实测;②**第三方基准**=neosr 官方表(50 张 256×256,
取 3 次最快,越高越快);③**本机实测**=仓库既有 1080p 记录。

| 模型 | 许可(出处) | 架构 | 后端 | 权重体积 | 速度 | 接入成本 | 结论 |
|---|---|---|---|---|---|---|---|
| **4xNomos8kSC** | **CC-BY-4.0**(OpenModelDB `/models/4x-Nomos8kSC` 作者 Helaman) | RRDBNet 64nf23nb | **ncnn(现有引擎)** | pth 级 60~64 MB | 同 `x4plus`(第三方基准 ESRGAN 22.51 s/2.22 fps) | **0.5~1 天** | ✅ **第一批** |
| **4xLSDIRCompactC3** | **CC-BY-4.0**(OpenModelDB 同作者) | SRVGGNet **Compact** 64nf16nc | **ncnn(现有引擎)** | **4.8 MB** | Compact 档(基准 1.90 s/26.35 fps) | **0.5~1 天** | ✅ **第一批** |
| **GFPGAN v1.4** | **Apache-2.0**(官方 README 原文) | StyleGAN2 解码器 + 需人脸检测 | **ORT + DirectML** | 约 330 MB 级 | 只看人脸 crop,单脸约 100~300 ms | **3~5 天** | ✅ **可选第一批**(须先过检测器许可闸) |
| SPAN | 官方权重 BSD-3 / 社区的 ClearRealityV1 是 **CC-BY-NC-SA-4.0** ❌ | SPAN | ncnn 需自写算子;现成二进制都在 **AGPL 前端** | — | **实测 ~18~21× 于 RealESRGAN(唯一有正规实测的加速架构)** | 大 | ⏸ 后续研究(见 §3.4) |
| 4xNomos8kSCHAT-L | CC-BY-4.0 | HAT-L | **ORT + DirectML**(ncnn 无) | ONNX fp16 **154.1 MB** | 第三方基准 **0.28 fps**(HAT-L 177.75 s) | 中 | ⏸ 仅"单图极致质量"可选,默认不做 |
| 4xFaceUpDAT | CC-BY-4.0 | DAT | **ORT + DirectML(DML 大概率不可用)** | pth 147.5 MB | 第三方基准 DAT 0.52 fps | — | ❌ 除非 30 分钟加载验证通过 |
| SwinIR-M/L | Apache-2.0 | Swin | ncnn 无;DML 可 | — | 1.08 fps(SwinIR-M) | 大 | ❌ 不做 |
| 4x-UltraSharp | **CC-BY-NC-SA-4.0**(OpenModelDB Rights + HF 讨论区标题) | RRDBNet | ncnn | 63.9 MB | 同 x4plus | 0.5 天 | ❌ **禁商用,不进包** |

**为什么"同构"这么关键**:`realesrgan-ncnn-vulkan` 前端(`resrgan` 重编版)本身就是靠 ncnn 读 `.param`/`.bin` 加载网络,
只要层类型都在 ncnn 里,新权重就是"丢进模型目录 + 加下拉项"。这两支候选正好分别落在
`realesrgan-x4plus`(RRDBNet)与 `realesr-animevideov3`(Compact)的架构上 ——
**比"接入一个新架构"便宜一个数量级**。相反,HAT/DAT/SwinIR 的算子 ncnn 不支持
(chaiNNer 官方原话:NCNN does not support all the operations required;社区 NCNN 权重集只覆盖
ESRGAN 族 / Compact / SPAN / MSDAN)。

---

## 3. 逐支落地清单

> 通用清单见 `RESEARCH_NEW_MODELS_LANDING.md` §5-A(A1~A11)、§4(8 条静默回退点)、§5-D(四张必须同步的清单)。
> 下面只写每支的**差异项**。

### 3.1 4xNomos8kSC(ncnn,RRDBNet)

| 项 | 内容 |
|---|---|
| 许可与署名 | CC-BY-4.0 ⇒ `THIRD_PARTY_NOTICES.txt` 新增条目(模型名、作者 Helaman/Phhofm、来源 URL、许可证名 + 许可证全文进 `licenses/`) |
| 权重来源 | **自己转**:从 OpenModelDB 页指向的官方 `.pth` 用既有配方(torch → TorchScript → pnnx → ncnnoptimize)转 ncnn,照 `realesr-general-wdn-x4v3` 的先例;`licenses/模型权重来源与校验值.md` 记每条 SHA256。**不要取 AGPL 仓库里现成的转换产物**(数据虽源自 CC-BY-4.0 权重,但为避开"复制他人产物"的争议,自己转更干净) |
| 落地路径 | `engines/realesrgan/models/<Tag>.param|.bin`(官方目录,由 `AlhPro.Core/EsrganModelDir.cs` 决定) |
| 倍率 | 4x-only ⇒ 照现有 `realesrgan-x4plus` 的倍率档规则处理(`AlhPro.Core/EngineScalePolicy.cs`) |
| UI | 视频页下拉项 `Views/VideoView.xaml`(必须有 `Tag` + 含"模型大小"的 `ToolTip`)、显示名表 `Views/VideoView.xaml.cs`(`BuildUpEsrganModelNames`);图片页模型表 `EngineService.cs`(`AnimeModels`/`PhotoModels`) |
| 序号 | **一律末尾追加** + `AlhPro.Core/VideoModelOrder.cs` 的 `CurrentRev` +1(`Migrate` 恒等段必须写注释) |
| 探测闸 | `NcnnProbePlan.For`(Tag ≠ 权重名时换算)、`NcnnModelVerdicts.NonNcnnModels`(不经 ncnn 的条目) |
| 契约测试 | `InterpDropdownContractTests`(ESRGAN 项数写死)、`VideoModelOrderTests`(Count/CurrentRev/Tag 顺序对位)、`EsrganModelDirTests` |
| 文案 | `website/index.html`、`website/tutorial.html`、`使用教程.md`、`ImgUpscalerUI/README.md`、`RELEASE_NOTES.md`(新模型必须"新增:"一行) |
| 工期 | 0.5~1 天(取件 + 转换 + 接线 + 单测 + 真机测速) |

### 3.2 4xLSDIRCompactC3(ncnn,SRVGGNet Compact)

同上,差异:

- 体积极小(**4.8 MB**),适合做**"实拍/游戏 · 快速"档**,也是唯一能省掉显存顾虑的大图档。
- 架构与 `realesr-animevideov3` / `realesr-general-x4v3` 同族 ⇒ 现有引擎直接吃。
- 速度必须**自己测**:第三方只给了同族 Compact 基准(26.35 fps),本机 1080p 数字要照 §6 的方法实测后再写进 `ToolTip`。
- 缺点:小模型的细节/纹理恢复必然弱于 RRDBNet ⇒ 文案定位成"快速/预览",别写成"更强"。

### 3.3 GFPGAN v1.4(ORT + DirectML,可选)

| 项 | 内容 |
|---|---|
| 能力 | 只做**人脸修复**(低清/老照片人脸),与超分组合:先超分 → 再人脸修复;图片场景价值最高 |
| 许可闸 ⚠ | GFPGAN 本体 Apache-2.0(官方 README 原文);**但它依赖的人脸检测/对齐(facexlib 里的 RetinaFace)权重源自 insightface,后者对预训练模型声明仅限非商业研究** ⇒ 商用前必须换成许可干净的检测器(自训 / 许可明确的 ONNX 检测模型),**这一步没做完不许接线** |
| 权重 | GFPGAN v1.4 有社区 ONNX 导出(需按体积/SHA256 归档);ncnn 路线至今无成品(官方 Real-ESRGAN-ncnn-vulkan 的 TODO 还挂着 "Support face restoration - GFPGAN") |
| 工程要点 | ①**固定 shape 建 session**(人脸 crop 固定 512×512),正好绕开 DML 的动态 shape 惩罚;②DML 单 session **同一时刻只允许一个线程 Run**、无 memory pattern、`execution_mode=ORT_SEQUENTIAL`;③DML 的 `Resize` 曾出过明确报错(onnxruntime issue #18613,后续 `[DML] Resize 18 & 19` 修复)⇒ **导出时把 Resize/Resize 算子压在已修复的 opset 区间**,上线前用真实模型跑一遍算子覆盖;④环境门槛 Win10 1903+ / DX12 |
| 工期 | 3~5 天(检测器替换 + ONNX 管线 + UI 开关 + 真机验证) |

### 3.4 明确不做的四类(附理由)

| 不做 | 理由 |
|---|---|
| SPAN 进包(现阶段) | ncnn 侧需要自写算子;**现成的 ncnn 可执行都来自 AGPL 前端**(Upscayl / SPAN-ncnn / Universal-NCNN-Upscaler,见 `RESEARCH_NEW_MODELS_LANDING.md:184`)⇒ AGPL 二进制不能随商业包分发。它确实是"唯一有正规实测的 10× 以上加速架构"(ailia 实测 SPAN x4 139/168 ms vs RealESRGAN 2989 ms),值得**后续单独立项**:要么自训/自写 ncnn 层,要么走 ONNX 导出 + DML 固定 shape 验证 |
| HAT-L 进视频 | 第三方基准 0.28 fps ⇒ 1 小时 1080p24(86400 帧)按秒级算也是天数级;只能做"单图极致质量"开关,且 154 MB fp16 + 需要自写分块,**收益低** |
| DAT | DML 官方算子清单明确不支持 `Gridsample 20:5d` 与 `DeformConv`,DAT 的可变形注意力正好依赖它们 ⇒ **大概率不是"慢"而是"加载即失败"**。若一定要做人像档(4xFaceUpDAT 是 CC-BY-4.0),先花 30 分钟做"能否在 DML 上加载"的验证 |
| SwinIR | ncnn 缺算子(chaiNNer 官方结论)+ 1.08 fps + 不支持 fp16 ⇒ 无性价比 |

---

## 4. 免费版 vs Max 功能切分(建议,待 D2 拍板)

| 能力 | 免费版 | Max |
|---|---|---|
| 超分模型 | 现有 9 支 + 本方案新增的 2 支中**只给 1 支** | 全部 + 独占新模型(每季度一支) |
| 图片批量 | 有上限(如单次 ≤ 20 张) | 无上限 |
| 视频时长/分辨率 | 有上限(如单段 ≤ 3 分钟 / ≤1080p 输出) | 无上限(含 4K) |
| 抠图 / 音频分离 | 可给(引流) | 同 |
| 广告 | 保留(现有合规告知) | **彻底关闭** |
| 更新 | 同 | 同 + 新模型/新引擎优先 |

> 原则:**免费版必须是"能完整跑通一次"的完整体验**(否则口碑崩),付费点放在"量"和"新模型"上,
> 不放在"故意让免费版变差"上。

---

## 5. 上架前合规清单(按"不做完不许卖"排序)

| # | 事项 | 现状 | 动作 |
|---|---|---|---|
| 1 | **自有代码许可策略** | `README.md` 明写 MIT「任何人都可再授权甚至销售本软件」 | 免费版继续 MIT 可以,但**付费版必须是另一份许可(EULA/闭源)**;若不想让第三方直接转卖 Max 源码,后续版本改用专有许可 |
| 2 | **ffmpeg GPLv3** | 随包的是 BtbN 的 `gpl` 变体(`--enable-gpl --enable-version3`),包内带 `COPYING.GPLv3`,处理方式是"独立进程调用不链接" | 二选一:①换 **LGPL 构建**(推荐,最省事);②保留 GPL 构建则必须在包内提供/指向**该构建对应的完整源码与构建脚本**的获取途径。**这是最容易被抓的点** |
| 3 | **RIFE 引擎与 v4.13 权重来源缺口** | `licenses/模型权重来源与校验值.md` 自曝:rife-v4.13 下载出处未留档,引擎非官方发布(官方停 `20221029`);`RESEARCH_NEW_MODELS_LANDING.md:155-158` 同样列为待整改 | 付费版发布前:换官方来源或补齐出处与 SHA256;否则至少从付费包中移除该支 |
| 4 | **第三方声明与许可全文** | `THIRD_PARTY_NOTICES.txt` 19 条 + `licenses/` 目录 + 打包脚本三份一致性校验 | 每加一支模型/换一次引擎都要同步;新增 CC-BY-4.0 模型**必须署名**(这是该许可证的强制义务) |
| 5 | **代码签名** | `website/faq.html` 自认"没有购买商业代码签名证书" | Steam 渠道可缓解(经客户端分发不触发 SmartScreen);官网直购版建议补 OV 证书(约 ¥2000~4000/年) |
| 6 | **Steam 硬性项** | — | Steam Direct **$100/款**(达 $1,000 调整后收入后返还)、分成 **70/30**;上线前填**内容调查 + AI 内容披露**(写清"内置 AI 模型、全部本地推理、不上传用户素材");退款政策 2 小时/14 天 ⇒ 工具类要预设阈值(时长/批量) |
| 7 | **税务与主体** | — | 国区个人/公司注册 Steamworks:W-8BEN + 中美税收协定 **10% 预扣**;境内收入自行申报 |
| 8 | **文案一致性** | `声明.md` / 官网写"免费公益软件 + 广告维持成本" | 转付费后必须改口(否则与商店页矛盾);`AnnouncementCopyTests` 之类的一致性守卫要同步更新 |

### 5.6 渠道与定价(建议)

- **Steam 48 元**:≈ $6.7,工具类合理档位。先例:`Lossless Scaling`(国区 ¥29,AppID 993090)证明该品类有市场。
  但 30% 分成 + 10% 预扣后到手约 ¥25~28。
- **官网直购(支付宝/微信)** 到手接近 ¥47 ⇒ **双轨**:免费版留在官网做流量入口,Max 主推官网直购,
  Steam 作为国际渠道 + 自动更新 + 社区背书。
- 注意:两渠道版本功能必须一致(别让 Steam 用户觉得是"次等版")。

---

## 6. 落地顺序与工期

| 顺序 | 事项 | 为什么排这里 | 工期 | 主要风险 |
|---|---|---|---|---|
| 0 | D1~D3 拍板 | 决定许可与文案走向 | — | — |
| 1 | **4xNomos8kSC(ncnn)** | 纯新增、零引擎改动、许可干净、替换掉清单里那个不可商用的选择 | 0.5~1 天 | 自转 ncnn 的一致性(照既有 46~48 dB 比对流程) |
| 2 | **4xLSDIRCompactC3(ncnn)** | 同上,且体积 4.8 MB 最省打包 | 0.5~1 天 | 速度需自测,别照搬第三方数字 |
| 3 | 合规整改(ffmpeg LGPL 或 GPL 源码义务、RIFE 来源、Notices 同步) | 不解决就**不能卖** | 1~3 天 | ffmpeg 换构建要回归全流程(硬编/软编/8.2 共享库版) |
| 4 | GFPGAN v1.4 + 许可干净的检测器(ORT+DML) | 付费版最有"看得见"的新能力 | 3~5 天 | 检测器许可闸;DML 算子/Resize 版本坑 |
| 5 | 免费/付费切分 + EULA + 商店页 + 官网改口 | 上线前置 | 2~4 天 | 老用户口碑 |
| 6 | Steam 上架流程($100 / 商店页 / 内容调查 / AI 披露 / 税务) | 前置完成后再走 | 1~2 周(含审核) | 审核反馈周期不可控 |
| ⏸ | SPAN 专项、HAT-L 单图档 | 收益明确但有前置研究 | 后续 | 见 §3.4 |

---

## 7. 必须真机验证(不许拿估算当结论)

1. `4xLSDIRCompactC3` 相对 `RealESRGAN_x4plus`/`animevideov3` 的**真实倍数**(本机 1080p,照
   `RESEARCH_NEW_MODELS_LANDING.md` §1.1 的命令口径,冷跑两次)。
2. 新模型在 **50 系(Blackwell)** 与 **AMD / Intel Arc** 真机上的探测与成片行为(本机只有 4060)。
3. `GFPGAN v1.4` ONNX 在 **DirectML** 上的算子覆盖(重点 Resize 版本)与端到端单脸耗时。
4. (若做)`4xFaceUpDAT` 的 ONNX 能否在 DML 上加载 —— 30 分钟即可定生死。
5. 打包端到端:装一次包,确认新权重真的进了安装目录、下拉项真的出现(静默回退点 §4 那 8 条逐条过)。

单测可覆盖的部分照 `RESEARCH_NEW_MODELS_LANDING.md` §6 末尾那张表(项数/序号迁移/探测换算/目录映射/契约测试)。

---

## 8. 本文未验证、不许当结论

- **速度**:除仓库既有本机实测外,其余速度数字来自第三方基准(neosr / ailia)与**推算**,不是本机数字。
- **许可**:截至 2026-10-04 的公开声明(OpenModelDB 作者自填 + HF 讨论区 + 官方 README)。**OpenModelDB 页脚自述
  "Models carry their own licenses",该站字段不是法律文本** ⇒ 放行前回到作者原始声明逐字核对,并把结论写进
  `licenses/模型权重来源与校验值.md`。
- **`4xNomos8kSC` 的权重可下载性**:需要经 `gh-proxy.com` 前缀取件(本机直连 github/huggingface 不可达);
  具体文件与 SHA256 尚未取件,故体积一列写"pth 级 60~64 MB"。
- **Steam 侧**:Steamworks 官方页在本机是 JS 渲染(正文被截断),`$100` / `70/30` / AI 披露 / 税务数字来自公开资料,
  以 Steamworks 后台实际条款为准。
- **画质**:本方案只做"许可 + 速度 + 接入成本"判断,**没有做画质对比**;任何写进下拉 `ToolTip` 的画质措辞
  都要先按仓库既有卡片口径(540p→1080p 带真值 + 色偏/detail/边缘 PSNR)实测。
