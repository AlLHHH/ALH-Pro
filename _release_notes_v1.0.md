## ALH Pro v1.0 正式发布

图片 / 视频 AI 增强工具,全程本地处理 · 中文界面 · 完全免费。

### 功能

- **图片放大**:AI 超分(waifu2x / Real-ESRGAN / Real-CUGAN),1x~4x,批量处理,输出 JPG/PNG
- **AI 抠图**:一键去背景,6 款模型(U²-Net / ISNet / BiRefNet),笔刷 / 框选 / 阈值 / 羽化
- **视频处理**:超分(1x~4x)、光流补帧(RIFE 2x~16x)、智能去重、转场识别、时间线裁剪、批量处理
- **安全渲染**:显存/内存自适应分块,自动降级 GPU→CPU,设备降温休息
- **检查更新**:启动时静默检查 GitHub 新版本,有新版提示下载

### 需要下载什么?(看这个就知道)

| 文件 | 大小 | 需要吗? |
|---|---|---|
| **ALHPro_v1.0_Setup.exe** | 约 630MB | ✅ **必须** —— 软件本体,含全部引擎和运行库(干净电脑直接装) |
| **models_v1.0.zip** | 约 1.4GB | 🔵 **可选** —— 只影响"AI 抠图"功能,其他功能不需要 |

### 安装步骤

1. 点下方 **Assets** 里的 `ALHPro_v1.0_Setup.exe` 下载(约 630MB);
2. 双击安装,一路"下一步"即可(新版已自带全部依赖,不用装任何额外东西);
3. 要用 AI 抠图:安装到"选择附加任务"一步**勾选「下载并安装模型包」**(约 1.4GB,可选);
4. 首次启动弹"欢迎使用"窗口,显示本机设备检测,点"开始使用"进入。

**版本更新**:直接下载新安装包**覆盖安装**即可——引擎/模型/设置原样保留,不用重装模型;只有彻底卸载后重装才需要重新装模型。

### 下载慢或下载不了怎么办

**方法一:加速镜像(最简单,网页直接下)**
在下载链接前加 `https://ghproxy.com/` 前缀,例如:
```
https://ghproxy.com/https://github.com/AlLHHH/ALH-Pro/releases/download/v1.0/ALHPro_v1.0_Setup.exe
```
免费、不用注册。镜像也慢就换 `https://ghproxy.net/`、`https://gh-proxy.com/` 再试。

**方法二:挂到下载器(最稳,推荐)**
复制下面的**直链**,打开迅雷 / IDM / Motrix → 新建任务 → 粘贴 → 多线程下载,支持断点续传:

```
安装包直链:
https://github.com/AlLHHH/ALH-Pro/releases/download/v1.0/ALHPro_v1.0_Setup.exe

模型包直链:
https://github.com/AlLHHH/ALH-Pro/releases/download/v1.0/models_v1.0.zip
```

**方法三:错峰下载** — 晚间高峰慢,早/深夜通常快很多。

> 下载只需要这一下访问 GitHub;安装完软件**完全离线可用**,之后不需要任何加速器。

### 系统要求

Windows 10/11 x64。无独显也可用(自动 CPU 计算,速度较慢)。

### 说明

- 所有处理在本机完成,不上传任何数据
- 开源协议 MIT,详见 LICENSE;引擎/模型版权归各自作者,详见 THIRD_PARTY_NOTICES.txt
- 支持在软件内「关于 → 检查更新」随时查看新版本
