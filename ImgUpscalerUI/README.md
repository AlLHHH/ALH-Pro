# ALH Pro

免费 · 本地 · 开源 —— 图片/视频 AI 增强工具(图片超分、抠图、视频超分 + 补帧 + 去重)

**所有处理在本机完成,不上传、不收集任何用户数据。** 全程中文界面,新手也能用。

---

## ✨ 功能

### 🖼 图片
- AI 超分(动漫/通用,1x~4x,waifu2x / Real-CUGAN / Real-ESRGAN)
- 画质增强:去雾、减少杂色、锐化、清晰、钝化蒙版、保留细节、细节增强、去模糊、边缘增强、边缘抗锯齿
- 抠图(IS-Net / U²-Net,支持主体框选 + 智能涂抹、Ctrl+Z、橡皮擦)

### 🎬 视频
- AI 超分 + 补帧(RIFE),输出帧率按「原素材帧率 × 倍率」
- **去重(专为动漫)**:智能检测(自适应)、动漫模式(可调 弱/中/强)、标准/温和/敏感、手动模式(含**语义运动分析**:先看"谁在动",镜头平移/背景滚动判为冗余删,人物局部动作保留)
- 后处理:去频闪、去杂色、边缘抗锯齿、锐化/清晰/钝化蒙版等
- 果冻修复(减少果冻/运动模糊/去抖)、「动漫插值优化」一键预设、转场识别、自定义分辨率、静音导出
- 批量管理:完成变灰(默认不重跑)、重新激活、删除、拖拽排序、只处理选中、完成后自动删除(等 3 秒)

### 🛡 安全渲染
显存/内存/CPU 墙自动按本机配置;分块/批大小/并发自适应;处理时降进程优先级(系统流畅优先);休息与温度墙(可调间隔与时长)。

---

## 🚀 快速开始

1. 下载最新发布版(含全部引擎)或按下方「构建」自行编译;
2. 若单独下载引擎:把 `waifu2x / realcugan / realesrgan / rife / ffmpeg` 放进程序目录的 `engines\` 下;
3. 打开程序 → 添加素材 → 选模式 → 开始处理。

**动漫素材推荐组合**:启用去重 → 动漫模式 + 强度「中」→ 勾选「动漫插值优化」→ 补帧模型 `rife-v4.6`。

---

## 🧩 模型怎么装(新手必看)

软件处理需要**引擎(exe)**和**模型(权重文件,几百 MB~1GB)**。官方发布版**自带全部引擎**,但**抠图模型可选装**(安装包大,默认不强制)。

### 抠图模型(6 个 .onnx,约 1.6GB)

**方式 A(推荐)· 安装时勾选**:
安装程序到「选择附加任务」页 → 勾选 **「下载并安装模型包(来自 GitHub)」** → 安装完成自动下载并解压好。

**方式 B · 手动下载**:
1. 打开 GitHub Release 页:https://github.com/AlLHHH/ALH-Pro/releases
2. 找到最新版本的 **`models_v1.0.zip`**(约 1.6GB)下载(国内打不开 GitHub → 用加速器/镜像,或找软件群/网盘分享)
3. **解压到程序目录** `engines\rembg\` 文件夹下:
   ```
   程序目录\
     engines\
       rembg\
         birefnet-lite.onnx       ← 6 个 .onnx 直接放这里
         birefnet.onnx
         isnet-anime.onnx
         isnet-general-use.onnx
         u2net.onnx
         u2netp.onnx
   ```
   ⚠️ **注意:直接放 6 个 .onnx 到 `engines\rembg\`,不要再套一层 `models` 子文件夹**(旧版本才需要)。

**验证装好了**:打开软件 → AI 抠图页 → 左上角若**没有**黄色"⚠ 未找到抠图模型"提示条 = 装好了;若还有提示,就是没放对位置(见上面结构)。

### 引擎 exe(waifu2x/realesrgan/realcugan/rife/ffmpeg)
官方发布版已自带;仅当你**手动下载引擎包**时,按 Release 说明解压,确保程序目录是:
```
程序目录\engines\waifu2x\...        (引擎 exe + 模型目录)
程序目录\engines\realcugan\...
程序目录\engines\realesrgan\...
程序目录\engines\rife\...
程序目录\engines\ffmpeg\ffmpeg.exe
```
启动软件后,「设置」页或各页顶部会有引擎检测结果的提示;缺哪个会在处理时明确告诉你。

### 50 系显卡(Blackwell)特别说明
- 图片/视频超分:照片模式自动用兼容版(无需额外安装);动漫模式用 `waifu2x` 即可。
- 视频补帧:选 `rife v4.13 / v4.6`;不要用 `Real-CUGAN` 或 `rife 老模型`(旧引擎在 50 系会崩,软件会黄字提示你换)。
- 如果你需要 Real-ESRGAN 的兼容版模型(照片超分更稳),确保 `engines\rembg\RealESRGAN_x4plus.onnx` 存在(随发布版附带)。

---

## 🔨 构建

```
dotnet build ImgUpscalerUI.csproj -c Release -p:Platform=x64
```

需要:.NET 8 SDK、Windows 10/11 x64。输出自包含(带 Windows App SDK 1.8)。

---

## ⚖️ 版权与免责声明(重要)

- **Real-CUGAN**:模型版权归 **哔哩哔哩 (bilibili)** 所有,官方仓库**未附明确开源许可**(默认保留所有权利)。本软件仅将其作为可选引擎,**个人使用无碍**;任何**商业使用或大规模分发前,请自行向 bilibili 核实授权**。
- **DeepSeek**:本软件标注「AI 协作开发」所用鲸鱼图标为 DeepSeek 商标,仅作**归属标注**(描述性使用);商标归 DeepSeek 所有,与 DeepSeek 无官方关联。
- **FFmpeg**:随附构建为 GPL v2+(含 libx264),本软件以**独立子进程**方式调用(未链接)。分发时保留本声明与源码链接:https://ffmpeg.org/download.html
- 其余组件(waifu2x-ncnn-vulkan、Real-ESRGAN-ncnn-vulkan、RIFE-ncnn-vulkan、U²-Net、IS-Net、BiRefNet、rembg、ONNX Runtime、Windows App SDK、.NET)许可明细见 **`THIRD_PARTY_NOTICES.txt`**(随发布版分发)。

## 📄 许可

本软件本体:**MIT License**(见 `LICENSE`)。任何人都可以使用、修改、再分发,但**必须保留版权声明**;若出现去版权、冒名发布的"套壳"行为,版权方保留依法追究(DMCA)的权利。

---

作者:AlL.H · 免费公益 · AI 协作开发:DeepSeek
