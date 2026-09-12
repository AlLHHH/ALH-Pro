# 全面自检报告(2026-09-12)

> 由本会话做的一轮系统性自检整理。分四块:**①本轮已修**(都实测过)、**②待办-高危**、**③待办-中低**、
> **④文档与代码不一致**。所有条目都带文件锚点;行号会漂移,**代码锚点(函数名/字符串)才是可靠标识**。
> 规矩:没实测过的不说能用;不能用真机验的,标明"未验证"。

---

## ① 本轮已修(全部:构建 0 错误 + 测试 288/288 + 启动验证通过)

| # | 问题(用户/真机症状) | 位置(锚点) | 修法 |
|---|---|---|---|
| 1 | 50 系笔记本上"跑到一半弹错误框、任务像卡住",**只在开去重时出现**(诊断包实测:2026-09-12 20:35 与 21:20 两次 `COMException 0x80070490`;09-11 一次 `LayoutCycleException`) | `MainPage`/`VideoView` 的日志刷新与自动滚动、进度回调、`AnimateShowHide` | 全部包保护(刷不动就少刷一次,任务照跑);**整个进度回调**再兜一层;高度动画本体与收尾回调也包保护;崩溃诊断新增「最近界面操作」面包屑(这类异常常不带堆栈) |
| 2 | 看门狗判死并"已强制终止"后,任务**永久卡死** | `VideoService.RunAsync` / `RunCaptureAsync`、`EngineService.RunEngAsync` | 管道读取加 5 秒上限;进程收不回来时明确失败并提示重启(不再拿"可能还在写同一目录的旧进程"回退重跑) |
| 3 | 「手动-帧差+SSIM」去重**没有进度也不能取消**(看着像卡死、点停止没反应) | `VideoService.DetectDupFramesWithSsim` 等三个检测器 | 每 16 帧可取消 + 界面进度;**每 5 秒一条日志心跳 + 开始/完成各一条**(下次能直接看出停在第几帧) |
| 4 | 视频页「超分模型」下拉:轻量通用模型压在最末尾、标签带"轻量通用"、耗时档"快" | `VideoView.xaml`、`VideoView.xaml.cs`、`EngineService.PhotoModels` | 上移到红字「超慢」之前,标签改 `（5MB · 中）`、去掉"轻量通用";**一次性序号迁移**(3↔2)+ 官方预设提 Rev4;两页标签口径统一 |
| 5 | 开去重时默认档是「手动模式」(还要自己挑算法调滑条) | `VideoView.xaml`(`SelectedIndex`) + `VideoResetBtn_Click` 里那句强制 `= 2` | 两处都改成 `0`=智能检测 |
| 6 | 「检测兼容性」用 **320×240 小图**探测,而本项目实测过的失败形态恰恰是"小图能过、真帧才崩" | `VideoView` 开始处理前的 `IsEngineGpuUsableAsync(...)` 3 参重载 | 改调与流水线同一个入口 `EnsureNcnnProbeAsync`(纯 N 卡快速通道 / 50 系·A·I 按生产帧尺寸实测 / 结论缓存复用) |
| 7 | 预设排序切成"按名字/按最近修改"后,点「应用预设」**套用另一条预设** | `VideoView.RebuildList` vs `ResolveSortedIndex` | 抽出 `SortedPresets(sortIdx)` 作唯一排序来源,显示与取数用同一份 |
| 8 | 用户自建的同名预设被官方基线**整份覆盖参数**(日志还写着"用户自建预设未动") | `VideoView.EnsureBuiltinPresets`、`UpscaleView` 同处 | 同名但非官方 → 只标记官方 + 补 Rev,`continue`(参数一个字段都不动) |
| 9 | 预设文件损坏时,内置预设重建会**整份覆盖**用户预设(静默丢数据) | 两页 `LoadPresets`/`SavePresets` | 损坏 → 备份 `.bak` + 本次运行置**只读保护**(拒绝写入) |
| 10 | 「显存不足自动降分块」是**空转**(t 减半但 lambda 里硬编码 `-t 0`,只同参数重试 3 次) | `EngineService.RunEngAsync`(两个调用点) | 正常路径仍 `-t 0`(auto,与旧行为逐字一致);OOM 时真正改成 `-t 512→256→128` |
| 11 | 音频页「记住上次」永远失效(XAML 默认值在构造期先写盘覆盖了设置文件) | `AudioView._suppressSave` 初值 | 初值改 `true`(与抠图页同款修法) |
| 12 | 去重模式/动漫档位恢复时上界超过下拉项数 → `SelectedIndex=-1` → **勾着去重却被静默关掉** | `VideoView.ApplyVideoParams` | 上界改成真实项数(2 / 4),越界回退安全档 |
| 13 | 视频页「本次运行显示广告」默认勾选、措辞像"一个开着的功能" | `MainPage` 设置区 + 合规文案 | 改为**「本次运行不显示广告」默认不勾**;勾选只写内存、**重启自动恢复显示**;教程/隐私政策文字同步改准 |
| 14 | **取消一次任务就把整个会话的硬件编码判死**:`_hwProbed` 在探测**开始**就置 true,而循环里"用户取消即 return" → 闩锁封死、可用硬编列表却是空的 → 本会话之后所有任务静默走 CPU 软编(用户只看到"变慢",日志无线索,得重启软件) | `VideoService.EnsureHwProbeAsync` | 取消时**放开闩锁**(取消=没探完),下个任务重新探 |
| 15 | 日志「保留天数/大小」绕过 Clamp:文件里 `KeepDays=0` 会让启动即清空日志,而设置界面照旧显示"7 天/20 MB"(显示值与实际执行值不一致) | `AppLogger.LoadConfig` | 改为经带 Clamp 的 setter 赋值 |
| 16 | 「引擎/ffmpeg 按可用核分线程」可点、会写 false,但每次启动都被强制回 true → "改了不保存" | `SafeRender.Load` + `MainPage` 设置区 | 与该设定本意一致:**置灰只读**并说明为什么(想省 CPU 用「CPU 上限」),不再让界面骗人 |
| 17 | 预设回填"只在值合法时才赋值" → 上一个预设/手填的**残留值渗进本次运行**;内容帧率残留尤其致命(采样算法直接读它,会按错误帧率抽帧) | `VideoView.ApplyVideoParams` | 非法/为空一律**显式清空** |
| 18 | 版本号停在 1.3.4 → 装过 1.3.4 的用户**看不到新一轮更新公告** | `csproj` / `installer.iss` / `RELEASE_NOTES.md` | 三处一起提到 **1.3.5**;公告改为 v1.3.5 章节并注明"1.3.4 的修复也含在本版" |
| 19 | **"开了去重就在去重阶段卡死"的真相 = 界面布局循环,任务其实没卡**(50 系笔记本 2026-09-12 22:31 诊断包实证:`LayoutCycleException`,同一份日志里后台补帧仍推进到 171/285;崩溃文件里**没有**面包屑 → 异常由框架布局过程抛出,外层 try/catch 拦不到) | `VideoView.AnimateShowHideCore`(给 Height 做动画)、三处 `VideoLogScroll.ChangeView`(在布局过程中改滚动)、`App.UnhandledException` | ①不再给 Height 做动画(改纯淡入淡出);②"滚到底"推迟到下一帧(`TryEnqueue`);③**自愈**:检出布局循环即自动停用界面日志刷新(日志继续写文件、任务照跑完),重启恢复 |

---

## ② 待办 · 高危(未修,证据充分)

| # | 问题 | 证据(锚点) | 为什么我这次没改 |
|---|---|---|---|
| H1 | **v1.3.4 公告第 5 条宣称"设备判定统一到 DeviceRouting",但 `DeviceRouting.ResolveEngineDevice` 生产代码零调用** —— 真正在跑的是三份重复解析 + `ResolveEngineGpu`(注释明写"用户选核显就核显"),与 DeviceRouting 的"选核显→换独显"**规则相反**。双卡机上"选独显却跑核显"的修复**并未生效**,而 13 条单测全绿是**假信号** | `AlhPro.Core/DeviceRouting.cs:25/61`(仅被 `AlhPro.Tests` 调用,grep 全仓库确认);`EngineService.ResolveEngineGpu`;三个视图各自的解析副本;`VideoService` 任务级选卡 | 这是**多卡行为改变**,本机是单卡笔记本,真机验不了;盲改可能把单卡机搞坏。需:统一到一个入口 + 多卡真机验证(或至少让两者规则一致、单测覆盖生产入口) |
| H2 | `EngineService.ToDmlDevice` 名字匹配不到时 `return -1`(CPU),而调用链注释承诺"编号不在表 → 用推荐独显,绝不落 CPU";`VideoService` 的注释也与实现相反 | `EngineService.cs` `ToDmlDevice` / `ResolveDmlDevice`;`VideoService` 补帧 ONNX 分支 | 同 H1:涉及设备路由,需真机 |
| H3 | ONNX 会话池降级保留"第一个非 null"而非"第一个真 DML":第 0 路瞬时失败时会把唯一健康的 DML 会话 Dispose → 整批落 CPU,只有一条 Warn | `EsrganOnnxService` 会话池降级处 | 需在 50 系机器上复现"瞬时失败"才谈得上验证 |
| H4 | 运行期块级 OOM(0x8007000E)**没有"缩小 tile 重试"档**,连击 3 次后视频逐帧退化为源帧缩放(画质全丢、界面无提示) | `EsrganOnnxService` tile 按显存钉死(1024/核显 512) | 需要 OOM 真实触发环境;建议照①-10 的思路给 ONNX 侧也加"折半重试" |
| H5 | 软件 Vulkan 设备(llvmpipe/lavapipe/SwiftShader)不匹配任何关键词 → 进设备表;评分"其他"1 分 > 核显 0 分 → **可能被推荐/选中**,全程极慢零提示 | `VulkanCheck.cs` 设备分类、`GpuInfo.ScoreDeviceName` | 需要一个"装了软件光栅后端"的环境验;修法简单(加关键词过滤),可先改代码但本轮来不及验证 |
| H6 | 图片页 ONNX 分块**完全串行**、多图逐张串行;视频超分**每批**新建 2~3 个 DirectML 会话 | `EsrganOnnxService` 分块/会话生命周期 | 属"确定性提速"但改动面较大,建议下一轮专项做(带 4K 实测对比) |
| H7 | 编码阶段仍是单次单进程;文档实测"4 段并行编码 83.9→174 fps(2.08×)"从未落地 | `VideoService` 编码阶段;`RESEARCH_SPEED.md` §6 #4 | 段边界 GOP/时戳拼接正确性文档自注"必测后才能上线",风险高 |

---

## ③ 待办 · 中低(证据在审计原始表里,择要)

**兼容性**
- nvidia-smi 只读首行且不带 `-i` → 多 N 卡机恒用 GPU 0 的显存算墙(`SafeRender`)。
- 分块家族/核显判定固定取 `VulkanCheck.Devices[0]`,不是所选设备(`SafeRender`)。
- `realesrgan` 加 `-x`(TTA)在本文件里既有"实测会卡死"的记录、又有一条单图路径会加上它 → 图片页开 TTA 必撞已知卡死。
- `_hwProbed=true` 先置位、取消即 return → 本会话硬编列表永远为空 → 之后所有任务静默软编(`VideoService` 硬编探测)。
- 补帧 ONNX 的 `gpuId` 形参从未使用(一律取全局 `AppSettings.GpuIndex`)→ 任务级选卡被忽略。
- 成片帧数校验依赖 `nb_frames`,MKV 等返回 N/A 时静默跳过 → 校验退化成"含 video 即有效"(建议 `-count_packets`)。
- 非 ASCII 路径保护只在单图路径;目录模式 `-i/-o`、临时目录、探测图路径原样传给引擎 → 中文路径整批失败。
- ONNX 侧硬编码假设:输入名 `input`/`x` 不查 InputMetadata、倍率按文件名猜、16 位图静默降 8 位。
- 补帧帧 `ConvertPngToJpg` 失败的兜底 `File.Copy` 静默吞 → 缺帧号会让 `-i frame_%06d.jpg` 从该点截断。
- 「兼容模式」复选框只在视频页,但自检/设置页全局提示让核显机型"建议勾选兼容模式"。

**速度**
- `ffprobe -count_frames` 只为拿真实帧数**整片解码一遍**(可改 `-count_packets`)。
- 补帧输出 RIFE 写 PNG 后应用侧**单线程**转 JPG(超分侧已直出 JPG 省 31%);根治是 RIFE `-f ...jpg`。
- `BuildFrameDurationsAsync` 用 showinfo 把整片**再解一遍**只为拿时长表(可在拆帧那次一起开 showinfo 流式解析)。
- 转场识别/轻量预估把整个 JPG 序列再喂 ffmpeg 解一遍(mjpeg 解码结构性单线程)。
- 过热休息循环每 1 秒新建一次 nvidia-smi 进程(10 分钟休息约 600 次)。
- 帧目录反复整帧 `File.Copy`(RIFE 每段拷全部帧、超分每批拷代表帧)→ 同卷可用硬链接。
- 进度看门狗每 200ms 全目录 `EnumerateFiles().Count()`(上万帧时 5 次/秒 O(N))。
- 大图分块黑帧抽检与拼接各解码一遍同一批 PNG。
- tile 1280 + overlap 256 → stride 1024,`(1280/1024)²≈1.56` = 约 **+56% GPU 计算**纯为羽化重叠(可降到 64~128)。
- 去重分析单线程(12 核只用 1 核)。
- 非原生倍率缩回是逐帧串行 `Task.Run`;`FindExe` 每次递归搜索无缓存。

**预设(未修,来自预设专项审计)**
- 保存预设不查重名,而删除按名字匹配 → 重名时删掉另一条。
- 官方预设删了下次启动又建回来(且插到最前)。
- 预设回填"只在非空/非 0 时赋值"→ 上一个预设的残留会渗进本次(内容帧率最致命)。
- 预设把输出目录与设备序号一起存并导出,与"不含输出路径/设备"的文案矛盾。
- `TimeStep/Jello/DedupMotionComp/DedupOnlyTrueHold` 既不在采集也不在回填(死字段)。
- 图片页预设存了自定义码率框的值但从不回填;PNG 下会把"超高"档改写成"默认"。
- 图片页动漫(3 项)/照片(4 项)共用**一个** `W2xModel` 下标,切模式会写坏另一模式的选择。
- `SaveSettings` 从不写 `W2xModelName`(读侧的"按名定位"形同虚设)。
- 导入超上限静默截断;写盘失败被吞后仍弹"已保存"。

**设置/记忆(未修)**
- `AppSettings.GpuIndex = -1`(临时降级)会被任何一次 `AppSettings.Save()` 落盘 → **用户选的显卡被永久清掉**。
- 音频页「重置所有参数」的抑制标志无 try/finally,中途抛异常即永久停写。
- `AppLogger.LoadConfig` 绕过带 Clamp 的 setter(KeepDays=0 时界面显示 7 天、实际清空日志)。
- `SafeRender.SplitCores` 每次 Load 强制 `true`,而复选框可改并会写 false → "改了不保存"。
- 设置弹窗下拉已无 CPU 项,但同段文案仍写"无独显建议选 CPU";`GpuIndex<0` 时下拉显示的是推荐 GPU → 用户看到 A、逻辑按 B。
- `ApplyVideoParams` 恢复中断后仍放行保存 → 未恢复的控件默认值被当用户选择写回。

---

## ④ 文档与代码不一致(要改文案或改代码,别留着)

1. **RELEASE_NOTES v1.3.4 #5/#15**(设备判定统一)与代码不符 —— 见 H1,这是最严重的一条。
2. **RELEASE_NOTES v1.3.4 #9**(输出规格预警)写"超 4K **且** 帧率>240 才弹窗",代码里**没有帧率判定**,超 4K 就弹模态框。
3. `DENOISE_MATRIX.md` 的"弱/中/强 = s5p3r5 / s5p5r5 / s7p7r7"只在「仅空间」模式成立;默认**结合模式**下弱档与强档的 nlmeans 参数相同(另叠 hqdn3d)→ 该文档数据不能外推到默认体验。
4. 赞助冷却时间三处不一致:`AppSettings` 注释"2 小时" / 代码 24h·2h / RELEASE_NOTES"10 小时"。
5. 视频页「自定义分辨率」面板 XAML 里 `Visibility="Collapsed"` 但代码仍读 `SelectedIndex==4` → 该功能实际不可达(代码按 5 项写、下拉只有 4 项)。
6. `EngineService` 陈旧注释:称"超 900 万像素降级 UI 未披露"(实际已披露)。
7. 去重模式名映射表仍是 7 项链表,而下拉只有 3 项 → 预设摘要把"手动模式"显示成"标准"。
8. 补帧 TTA 传 `-x -z` 双 TTA,而 `RESEARCH_SPEED.md` 明确建议不要用(代价未测);"50 系硬编失败 = preset p4 被拒"的旧因果文档已否定,代码仍据此多跑一轮探测。
9. 同规则写两份:`VideoService` 里"v4.26 禁用 TTA"有两处实现,今天等价、改动必错一处;去重删帧数有一处 `AddRange` 重复累加(日志数字翻倍)。

---

## ⑤ 降级链与自检:逐条体检(2026-09-12 晚,专项清点)

背景:引擎换成 2025/2026 重编版(指纹含 `VK_EXT_robustness2`)之后,一批"因为 2022 版 ncnn 的毛病才存在"的
兜底与自检需要重新审视。结论分三类。

### 5.1 仍然必要(有实测依据,别动)
| 位置(锚点) | 为什么必要 |
|---|---|
| `FrameInspect.IsDefectiveFrame`(整帧 + 任一 1/3 条带)+ `DefectiveFramesAllComeFromNearBlack` | "下 2/3 全黑"与"存在量词整批豁免"两个形态都是真机漏检过的 |
| RIFE 探测:渐变+右移 2px、`Format24bppRgb`、`ProbeOutputIsSane` 四判据 | 黑→白硬切是病态输入(实测均值 2.4/74.4/90.3);32bpp 输入直出乱码(158.96/2120) |
| `RifeProbeKey` 带引擎指纹 + 尺寸档 | 换引擎必换键;1080p 结论不能给 4K 背书 |
| `GpuFault.IsPersistentDeviceError` / `TripDmlDead` / `DmlDeviceStrikes` 分表 | 887A0005/6 进程内不可恢复;音视频分表防互污 |
| 拆帧 `d3d11va`→软解(按编码闩锁、取消不置位) | 硬解被驱动挂死是"只能强制结束"的根因 |
| 合帧 `mjpeg_cuvid` 8 帧 + 6 秒探测 | 实测"20 帧命令永不结束"vs 软解 0.67s;单帧探测无感 |
| `EnsureHwProbeAsync` 真实参数 + 1280×720 + ≥5 帧 + 成品校验 | 1 帧探测恒失败 → 硬编被静默禁用;QSV 小图假成功 |
| `BrokenHwEncoders` 会话拉黑 + 取消放开 `_hwProbed` | 日志实证"取消"被当成"硬编坏"→ 整会话静默软编 |
| `_gpuRetryDepth` Interlocked + finally 归还 | 静态闩锁让"重试 3 次"整进程只兑现一次 |
| 引擎 30 秒无进展看门狗 + 管道 5 秒上限 + `ProcessStillRunning` 不许回退重跑 | "已强制终止却永久卡死"与"新旧进程交叉写坏帧"各是一次真机事故 |
| 备用 ffmpeg 缺失告警 | 572 驱动实测主 ffmpeg 报"需要 610 以上";漏拷=静默变慢 |

### 5.2 已收紧 / 已改准(本轮已改)
- **"50 系未测即判风险"** 改为**只对旧引擎**生效(判据 = 引擎文件名含 `2026`,即重编版)。
  **重要:这条桥本来就已名存实亡**(两个调用点 `ShouldUseOnnxEsrgan/Waifu2x` 都显式传 `false`),
  所以这是**防未来回归**,不是行为修复 —— 别把它当"修好了 50 系变慢"。
- 陈旧文案六处(`RTX 50 系 ncnn 超分易黑帧` / `-preset p4 被拒`因果 / NVDEC 30s vs 6s / `1×1 可用`其实 320×240 /
  "无独显建议选 CPU"(下拉已无该选项) / "建议勾选兼容模式"(开关只在视频页))→ 全部改准。

### 5.3 待办(改行为、需真机验证,本轮未动)
| # | 问题 | 锚点 | 影响 | 建议 |
|---|---|---|---|---|
| F1 | **超分探测缓存键缺"模型"** | `EngineManager.NcnnVerdictKey(engine,gpuId)`(仅 `EngineId+"|"+gpuId`) | x4plus(实测 24.1s、最易出坏帧)可被 animevideov3(1.7s)的"通过"结论背书 7 天 | key 加模型 + 二进制指纹(照抄 `RifeProbeKey`);**必须同时决定**"同一任务是否因此多探一次"(现在 waifu2x 与超分用不同模型、却共用一条结论) |
| F2 | **免探测快速通道判据看整机而非目标卡** | `EnsureNcnnProbeAsync` 的 `!IsBlackwellGpu() && !HasNonNvidiaGpu()` | **混显笔记本(绝大多数)** 永远走不了快通道 → 每个首次任务白等一次生产帧探测(最坏 60s;4060 上 x4plus 24.1s) | 改判"要用的那张卡的设备名";真失败仍有黑帧兜底 |
| F3 | 设备路由四份实现(见 H1) | `DeviceRouting` vs 三视图 + `ResolveEngineGpu` | 规则相反,单测测的是死代码 | 需多卡真机 |
| F4 | `ToDmlDevice` 名字匹配不到静默落 CPU | 与注释承诺相反(H2) | 用户看到 A、逻辑按 B | 先改注释+升级告警 |
| F5 | 生产帧探测 60 秒固定超时、失败 TTL 1 天 | `EnsureNcnnProbeAsync` | 慢卡被误判"不可用"且每天重试 | 按模型/面积分档;超时与真失败分开记账 |
| F6 | 黑块链末尾 `UpOneTileAsync(…, -1, …)`(ncnn-CPU 逐块重算) | `UpscaleTiledAsync` | 与"绝不自动转 CPU"的定案矛盾(质量降级 vs 慢但有效,需用户决策) | 产品决策后改 |
| F7 | `SafeRender.GetVideoTileSize`/`RenderPolicy.VideoTileSize` 仍按型号压 512/640,注释自认依据过时 | 同上 | 50 系 ncnn 已是快路径候选,保守 tile 自罚吞吐 | 真机数据到位前只改注释依据,别改数值 |
| F8 | `ProbeDiagnosis.Describe` 对 Blackwell 初始化即崩一律归因"驱动 cooperative-matrix 缺陷" | 同上 | 重编版引擎上的崩溃未必是 coopmat,可能冤枉驱动 | 按引擎版本限定归因 |

### 5.4 自检开销(每次启动 / 每个任务跑几次、多久)
- **每次启动**:Vulkan 探测 0.2~1s(枚举不到设备时最坏 ~13s)→ 自检挑卡(`FindBestWorkingGpuAsync`,每候选 320×240 活性检查,秒数**未记录**)→ DirectML 建会话探测(加载百 MB 模型,**未记录**);三件**串行 await**,所以"首次启动卡几秒"可解释。
- **每个视频任务**:ncnn 超分探测(每 `引擎|GPU` 首次;实测 animevideov3 1.7s / **x4plus 24.1s**,上限 3×60s;成功 TTL 7 天、失败 1 天)→ RIFE 探测(每"指纹|尺寸档|模型";实测 1080p 1.5s / 4K 2.2s)→ 硬件编码探测(每会话 1 次;6 编码器 × ≤4 组合 + NVDEC ≤6s,总时长**未记录**)。
- 视频页「检测兼容性」与流水线共用入口 → 第二次 0 成本(v1.3.5 #37 的效果)。
- 诊断包导出**有意**强制真测一次(≤60s×2),不是 bug。
- 已验:构建 0 错误、`AlhPro.Tests` 288/288、`deploy.ps1` 启动验证通过、预设序号迁移两次启动不横跳(真机日志)、去重心跳日志真出现、包内引擎哈希与构建产物一致。
- **未验**:任何"多卡机 / 软件光栅后端 / 12·16GB 显存档 / 4K 长片"相关的结论(无对应硬件);①-1 的崩溃修复**尚未在那台 50 系机器上验证**;①-3/5/6/7/8/9/12 属逻辑修复,只有代码级证据 + 本机不回归,没有"用户场景复现"级验证。
