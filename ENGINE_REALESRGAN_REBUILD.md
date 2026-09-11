# Real-ESRGAN 引擎重建(50 系 NVIDIA 修复版)

## 为什么要重建

ALH Pro 里 `engines\realesrgan\realesrgan-ncnn-vulkan.exe` 是**官方 v0.2.0(2022-04-24)** 预编译版,
它静态链接的是 2022 年的 ncnn。2022 年的 ncnn Vulkan 后端**没有** `VK_EXT_robustness2` /
`VK_KHR_robustness2` 的处理(ncnn 直到 2025-09-09 才加入,commit `34429145d4`,PR #6296
"fix hangs with NVIDIA >565 drivers")。

后果(GPU 侧实测,5060 Laptop / 驱动 610.62):官方版在 50 系上**能跑完、退出码 0,但输出坏帧**
(整帧或条带近黑);软件只好降级到 ONNX DirectML —— 实测 **5.7 s/帧** vs ncnn **0.11 s/帧**,慢约 50 倍。
同一台机器上的 waifu2x 2025-09-15 引擎(自带 2025 ncnn)**走 ncnn 一切正常**,指纹对比:

| 引擎 | `VK_EXT_robustness2` | `VK_KHR_robustness2` | `robustBufferAccess2` | `nullDescriptor` |
|---|---|---|---|---|
| 官方 realesrgan v0.2.0(2022) | ✗ | ✗ | ✗ | ✗ |
| waifu2x 20250915(可用) | ✓ | ✓ | ✓ | ✓ |
| **本重建版 realesrgan(2026)** | ✓ | ✓ | ✓ | ✓ |

## 重建做法(已验证可行)

前端源码用的是**官方同一份 MIT 源码**(`xinntao/Real-ESRGAN-ncnn-vulkan`),只换掉 ncnn
(用 2025/2026 的源码重新编译),因此输出与官方版逐像素几乎一致。

1. **工具链**:MSYS2(清华 TUNA 镜像 `https://mirrors.tuna.tsinghua.edu.cn/msys2/distrib/msys2-x86_64-latest.tar.xz`,
   51 MB,实测 ~10 MB/s)→ 解包 → `pacman -S mingw-w64-x86_64-gcc cmake ninja glslang vulkan-headers vulkan-loader libwebp`
   (gcc 16.2 / cmake 4.4.3 / ninja 1.13.2 / glslang 16.3.0)。
   *注:GitHub 直连、ghproxy、cmake.org、ziglang.org 在本机只有 13~20 KB/s 且大文件必断;MSYS2 + 清华镜像是唯一可行的快路。*
2. **ncnn 源码**:2025/2026 的 ncnn(含上面的 robustness2 修复),用仓库里已有的 ncnn master 源码树。
3. **前端源码**:`main.cpp`、`src/realesrgan.cpp`、`src/realesrgan.h`、4 个 `*.comp`、
   `stb_image.h`、`stb_image_write.h`、`webp_image.h`、`wic_image.h`、`filesystem_utils.h`、`win32dirent.h`。
   `cpu.h` / `gpu.h` / `platform.h` 由 ncnn 源码提供。
4. **必须打的前端补丁**(只有一处,MinGW 与 MSVC 的宽字符 printf 语义差异):
   `swprintf` 里 `%s` 在 MinGW 下是**窄**字符串,必须写成 `%ls`(MSVC 恰好相反)。
   原代码 `swprintf(parampath, 256, L"%s/%s-x%s.param", ..., std::to_string(scale))` 在 MinGW 下会把
   模型名截成第一个字节(`models` → `m`)并拼出垃圾文件名 → 改 `%ls` 且 scale 用 `%d`。
   见 `patch_frontend.py`。
5. **静态链接**:glslang / SPIRV / SPIRV-Tools / webp / gomp / winpthread 全部静态链接
   (仓库里 `Findglslang.cmake` / `FindWebP.cmake` 指向 MSYS2 的 `.a`),最终 exe **30.6 MB,
   只依赖系统 DLL**(KERNEL32 / msvcrt / ole32 / OLEAUT32;vulkan-1.dll 由驱动提供),
   不需要随包分发任何第三方 DLL。

## 复现命令

```bash
# MSYS2 MINGW64 shell 里:
cmake -G Ninja -S <build 目录> -B out -DCMAKE_BUILD_TYPE=Release
cmake --build out -j 20
# 产物:out/realesrgan-ncnn-vulkan.exe → 复制成 engines\realesrgan\realesrgan-ncnn-vulkan-2026.exe
```

## 验证证据(4060 Laptop,与官方 v0.2.0 逐图对比)

| 项目 | 官方 2022 | 重建 2026 |
|---|---|---|
| x4plus, 1080p→4K | 16.04 s / 14190 KB | **14.91 s / 14198 KB** |
| animevideov3 x2, 1080p | 1.28 s / 3165 KB | **1.02 s / 3168 KB** |
| x4plus 小图对比 | — | PSNR **56.6 dB**,平均差 0.14,最大差 4 |
| 1080p→4K 对比 | — | PSNR **49.7 dB** |
| animevideov3 x2 对比 | — | PSNR **52.2 dB** |
| robustness2 指纹 | 0/4 | **4/4** |

→ 输出与官方版等价(差异仅为 ncnn 版本间的浮点/分块误差),速度略快,且具备 50 系所需的 Vulkan 健壮性处理。

## 软件侧接线

- 引擎文件:`engines\realesrgan\realesrgan-ncnn-vulkan-2026.exe`(官方 2022 版保留做兜底)。
- `EngineService.FindRealESRGAN()`:**有新版就优先用新版**。
- `EngineService.RealEsrganEngineId` / `EngineId()`:新版引擎的探测结论缓存键是 `realesrgan2026|<gpu>`,
  与旧版分开 —— 否则旧版在 50 系上"出坏帧"的真结论会把新引擎一起判死,又退回慢 50 倍的 ONNX。
- 前端 CLI 与官方完全一致(`-i -o -s -t -m -n -g -j -x -f -v`),所以调用方逐字不变,属**直接替换**。

## 许可

- 前端 `xinntao/Real-ESRGAN-ncnn-vulkan`:**MIT**(LICENSE 一并留档)。
- 推理库 `Tencent/ncnn`:**BSD-3-Clause**。
两者均允许再分发与修改,保留版权声明即可。
