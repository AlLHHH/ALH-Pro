# licenses/ —— 随包第三方许可原文与署名

本目录里的文件**会随软件一起分发**（打进安装包的 `licenses\` 目录，与 `THIRD_PARTY_NOTICES.txt` 配套）。
`打包.ps1` 会把本目录新增的文件自动补进 `发布版\licenses\`；必带清单里也钉住了其中两份关键文件。

| 文件 | 对应随包的什么 | 许可 / 版权 | 校验值 |
|---|---|---|---|
| `MIT.txt` / `BSD-3-Clause.txt` / `Apache-2.0.txt` / `GPLv3.txt` | 通用协议全文（索引用） | — | — |
| `Real-CUGAN-MIT-bilibili-2022.txt` | **Real-CUGAN 的算法与模型权重** | MIT, Copyright (c) 2022 bilibili | 1065 B，SHA256 `8CAD8CFDF94BAAF23519061AF913770E52476DDEC2A311E9510582E7BED13CBA` |
| `realcugan-ncnn-vulkan-MIT-nihui-2019.txt` | **Real-CUGAN 引擎包装层 + ncnn 权重文件** | MIT, Copyright (c) 2019 nihui | 1072 B，SHA256 `57157B0FC6954DD1645DC674CC99BEBBAB4CB84C5B306B910989777E3D0D6FBD` |
| `Practical-RIFE-MIT-hzwer-2021.txt` | **RIFE 补帧权重（含 2026-09-24 上架的 rife-v4.26）** | MIT, Copyright (c) 2021 hzwer | 1062 B，SHA256 `7932FB49341512B959B1744A6D9CBB39E5A1EC89DA438A34D0454D5D8DF9FECD` |
| `模型权重来源与校验值.md` | 每个**实际随包的权重/二进制文件**的来源 URL 与 SHA256 | — | — |
| *(无独立文件)* `anime4k-v4-a.glsl` | **Anime4K「1x 修复」着色器**(随 `engines\ffmpeg\shaders\` 与 `engines\ffmpeg8\shaders\` 分发) | MIT, Copyright (c) 2019-2021 bloc97 | 许可原文**就在着色器文件头部**;各 336,741 B(两个目录各一份,必须都带) |
| `DirectML-LICENSE-CODE-MIT-Microsoft.txt` | **DirectML 代码部分**(随包 `发布版\DirectML.dll`,约 18.7 MB,ONNX Runtime 的 DirectML 提供程序用它) | MIT, Copyright (c) Microsoft Corporation | 1093 B |
| `DirectML-LICENSE-TERMS-Microsoft.txt` | 随包的 `DirectML.dll` 本体 | 微软软件许可条款(`MICROSOFT SOFTWARE LICENSE TERMS — MICROSOFT DIRECTX MACHINE LEARNING (DIRECTML)`) | 10439 B |
| `DirectML-ThirdPartyNotices-Microsoft.txt` | DirectML 自身携带的第三方声明 | — | 4577 B |

## 为什么 Real-CUGAN 那两份许可文件是分开的

Real-CUGAN 的**算法/权重**与**引擎包装层**是两个不同的权利主体、两份不同的 MIT 文本：

* 算法与权重项目 `bilibili/ailab` 的 `Real-CUGAN/LICENSE`：`Copyright (c) 2022 bilibili`（1065 字节）。
  这份文本**同时随 bilibili 官方 ModelScope 权重页发布**（`bilibili/cv_bilibili_image-super-resolution`），
  与上游仓库根那份**逐字节相同**（SHA256 一致）—— 这就是"权重有书面 MIT 依据"的证据。
* 引擎包装层 `nihui/realcugan-ncnn-vulkan`（本项目实际取件的那份，模型随仓）：`Copyright (c) 2019 nihui`（1072 字节）。

两份都随包，且 `打包.ps1` 的必带清单会校验它们存在。逐条依据与逐字原文见
`docs/许可尽调_RealCUGAN与补帧模型_2026-09-24.md`。

## 校对这份目录的方法

```powershell
# ① 许可原文的字节数与哈希（必须与上表一致）
Get-ChildItem .\licenses\*.txt | ForEach-Object {
    "$($_.Name)  $($_.Length)  $((Get-FileHash $_.FullName -Algorithm SHA256).Hash)"
}
# ② 真正随包的权重/二进制（以 发布版\ 为准）
Get-ChildItem .\发布版\engines\realcugan -Recurse -File |
    ForEach-Object { "$($_.FullName)  $($_.Length)  $((Get-FileHash $_.FullName -Algorithm SHA256).Hash)" }
```
