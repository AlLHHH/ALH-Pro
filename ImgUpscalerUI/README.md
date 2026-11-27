# ALH Pro

本地运行的图片、视频、音频增强工具。图片超分、AI 抠图、视频超分与补帧、视频去除重复帧、音频升采样与分离。

所有处理在本机完成,不上传文件、不收集数据。界面为中文。

当前版本 v1.4.11。官网 https://alhpro.cn ｜ 常见问题 https://alhpro.cn/faq.html ｜ 更新日志 https://alhpro.cn/changelog.html

版权声明见 `COPYRIGHT.txt`,许可全文见 `LICENSE`(GNU GPL v3),第三方组件许可见 `THIRD_PARTY_NOTICES.txt`。

## 功能

### 图片

- 超分:动漫插画用 waifu2x,实拍照片用 Real-ESRGAN,倍率 1x 到 4x
- 画质增强:去雾、减少杂色、锐化、钝化蒙版、保留细节、去模糊、边缘增强、边缘抗锯齿
- 抠图:U²-Net、ISNet、BiRefNet 共六款模型,支持批量,多图自动分文件夹

### 视频

- 超分:实拍用 Real-ESRGAN,动漫用 Real-CUGAN,1x 修复到 4x
- 补帧:Real-RIFE,模型 v4.13 与 v4.6,输出帧率等于原帧率乘倍率
- 去除重复帧:见下一节
- 后处理:去频闪、去杂色、边缘抗锯齿、锐化、钝化蒙版
- 光流调整、转场识别与转场阈值、静音导出
- 批量管理:完成置灰、重新激活、删除、拖拽排序、只处理选中、完成后删除源文件

### 音频

- 升采样:8k / 16k / 22k / 32k 的素材补到 48kHz
- 增强:人声与伴奏分别优化后重混,分柔和、标准、强力三档
- 分离:基于 Demucs,可提取人声、去人声、分离导出两轨,或按自定义组合多轨输出并单独调整音量
- 降噪三档、响度归一、低切、清晰增强,输出 WAV / FLAC / MP3,带波形预览与首尾裁剪

### 运行保障

按本机显存、内存、CPU 自动定档,分块与批大小自适应,处理时降低进程优先级,可设置休息间隔。引擎在这张显卡上跑不起来时会换稳定路线或旧版编码器,并在开始前说明原因。

## 去除重复帧

这一项针对动画的作画节奏,做的是帧级去重。

动画常用一拍二、一拍三的方式作画,一个画面连续占 2 帧或 3 帧。这些重复的保持帧对观看没有作用,却会让补帧引擎当成连续动作,插出多余的中间帧,画面发糊、抖动。软件先把重复的保持帧删掉,算出素材真实的内容帧率,再按内容帧率补帧,最后按原时长输出。

所以这里的去重是删掉动画里重复的画面帧,与平台上判定搬运、规避重复检测那类处理无关,软件也没有这类功能。

三种模式:智能检测(自动估拍数,均衡、激进、保守三档)、动漫模式(去一拍二、一拍三、混合)、手动模式(内容帧率采样、帧差加 SSIM、重复帧检测、画面变化阈值)。只删确认为重复的作画帧,口型、眨眼、微动不删;没有重复帧的素材原样通过。

只勾去重、不勾补帧时,输出帧率等于素材的内容帧率,时长不变。

## 快速开始

1. 安装后打开程序,左侧选择功能页,添加素材,调整参数,点开始处理。
2. 动漫素材的常用组合是:启用去除重复帧(智能检测或动漫模式),再补帧(Real-RIFE v4.13)。

安装包分两种。标准版约 754 MB,抠图模型需要在安装时勾选「下载并安装模型包」,或稍后单独下载约 1.25 GB 的 `models_v1.1.zip`。完整版约 2.15 GB,含全部引擎与模型,装完即可使用。

完整版放在百度网盘:https://pan.baidu.com/s/1XfUXwR685bV2XgmU-dycCQ?pwd=6eym 提取码 `6eym`。

安装包未做代码签名,首次运行时 Windows SmartScreen 会提示,点「更多信息」再点「仍要运行」。完整版体积大,首次打开时安全软件会完整扫描一遍,双击后短暂没有反应属于正常。

升级直接覆盖安装,引擎、模型和设置都会保留。安装前请先完全退出 ALH Pro,否则覆盖安装会跳过被占用的文件。

## 引擎与模型

软件运行需要引擎程序与模型权重。发布版自带引擎;抠图模型在标准版里是可选项,完整版已包含。

### 抠图模型

只在使用标准版、且安装时没有勾选模型包的情况下需要手动补。

1. 下载 `models_v1.1.zip`(约 1.25 GB):https://github.com/AlLHHH/ALH-Pro/releases
2. 解压后把 5 个 `.onnx` 文件放到程序目录的 `engines\rembg\` 下:

```
程序目录\
  engines\
    rembg\
      birefnet-lite.onnx
      birefnet.onnx
      isnet-general-use.onnx
      u2net.onnx
      u2netp.onnx
```

注意是直接放在 `engines\rembg\`,不要再套一层 `models` 子目录。

装好后打开 AI 抠图页,顶部不再出现「未找到抠图模型」的提示即为正常。

### 引擎

发布版已自带以下目录,自行构建或手动补引擎时保持同样结构:

```
程序目录\engines\waifu2x\
程序目录\engines\realesrgan\
程序目录\engines\realcugan\
程序目录\engines\rife\
程序目录\engines\ffmpeg\ffmpeg.exe
程序目录\engines\rembg\
```

缺少引擎时,软件在处理前会提示具体缺哪一项。

### 显卡说明

软件启动时检测显卡。有可用独立显卡时走显卡加速;引擎在这张卡上无法使用时自动换稳定路线(DirectML),结果不受影响,速度慢一些。驱动过旧导致硬件编码不可用时,会换用旧版编码器。

补帧下拉里是 v4.13 与 v4.6 两支模型,架构相同。老架构的补帧模型已下架。视频页不含 waifu2x,它只用于图片超分。

无独立显卡时可以正常使用,只是处理走 CPU,速度慢很多。

## 构建

```
dotnet build ImgUpscalerUI.csproj -c Release -p:Platform=x64
```

需要 .NET 8 SDK 与 Windows 10/11 x64,产物自包含(含 Windows App SDK)。

## 许可与第三方组件

本软件本体采用 GNU GPL v3。可以使用、修改、再分发,也可以商用;再分发时保留版权声明与 GPL-3.0 原文,分发修改过的版本时用同样的许可提供修改后的完整源码。

FFmpeg 以独立子进程方式调用,未链接。随附构建与源码见 https://ffmpeg.org/download.html

Real-CUGAN 的权重与 ncnn 移植层均为 MIT 许可(权重 © 2022 bilibili,ncnn 移植 © 2019 nihui),许可原文见 `licenses\`。

其余组件(waifu2x-ncnn-vulkan、Real-ESRGAN-ncnn-vulkan、RIFE-ncnn-vulkan、U²-Net、ISNet、BiRefNet、rembg、HT-Demucs、LavaSR、ONNX Runtime、Windows App SDK、.NET)的许可与来源见 `THIRD_PARTY_NOTICES.txt`。

## 反馈

官方 QQ 群:767832870

问题反馈:https://github.com/AlLHHH/ALH-Pro/issues

提交问题时附上软件版本号与「导出诊断」生成的文件,定位会快很多。

作者:AlL.H