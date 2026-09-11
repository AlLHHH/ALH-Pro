# engine-build — 重建 Real-ESRGAN 引擎(50 系 NVIDIA 可用版)

这个目录让"官方 2022 版 Real-ESRGAN 在 50 系上出坏帧"的修复**可复现**。
背景、证据与验证数据见仓库根目录 `ENGINE_REALESRGAN_REBUILD.md`。

## 目录内容

| 文件 | 作用 |
|---|---|
| `CMakeLists.txt` | 构建脚本:编 ncnn(静态) + 官方 Real-ESRGAN 前端 → 一个自包含 exe |
| `cmake/Findglslang.cmake` | 让 ncnn 链接 MSYS2 的**静态** glslang(否则要随包发 glslang.dll 等) |
| `cmake/FindWebP.cmake` | 同上,静态 libwebp |
| `patch_frontend.py` | 前端唯一必打的补丁(MinGW 宽字符 `%s` → `%ls`,见下) |

## 一次性准备

```powershell
# 1. 工具链:MSYS2(清华镜像,实测 ~10 MB/s;GitHub/ghproxy/cmake.org 在本机只有 13~20 KB/s 且大文件必断)
curl.exe -L -o msys2.tar.xz https://mirrors.tuna.tsinghua.edu.cn/msys2/distrib/msys2-x86_64-latest.tar.xz
tar.exe -xf msys2.tar.xz            # 得到 msys64\
# 2. 把清华源放到 mirrorlist 最前(msys64\etc\pacman.d\mirrorlist.msys / mirrorlist.mingw)
#    Server = https://mirrors.tuna.tsinghua.edu.cn/msys2/msys/$arch/
#    Server = https://mirrors.tuna.tsinghua.edu.cn/msys2/mingw/$repo/
# 3. 装包
msys64\usr\bin\bash.exe -lc "pacman-key --init; pacman-key --populate msys2; pacman -Sy --noconfirm; \
  pacman -S --noconfirm --needed mingw-w64-x86_64-gcc mingw-w64-x86_64-cmake mingw-w64-x86_64-ninja \
  mingw-w64-x86_64-glslang mingw-w64-x86_64-vulkan-headers mingw-w64-x86_64-vulkan-loader mingw-w64-x86_64-libwebp"
# 4. ncnn 源码(2025/2026,含 Blackwell 的 VK_EXT_robustness2 修复)—— 解到任意目录
```

## 取前端源码(xinntao/Real-ESRGAN-ncnn-vulkan,MIT)

需要:`main.cpp`、`src/realesrgan.cpp`、`src/realesrgan.h`、`src/realesrgan_{pre,post}proc{,_tta}.comp`、
`src/stb_image.h`、`src/stb_image_write.h`、`src/webp_image.h`、`src/wic_image.h`、
`src/filesystem_utils.h`、`src/win32dirent.h`、`LICENSE`。
(`cpu.h` / `gpu.h` / `platform.h` 由 ncnn 源码提供,不要去找。)
可用的镜像前缀: `https://cdn.jsdelivr.net/gh/xinntao/Real-ESRGAN-ncnn-vulkan@master/`
或 `https://ghproxy.net/https://raw.githubusercontent.com/xinntao/Real-ESRGAN-ncnn-vulkan/master/`。

## 构建

```powershell
python tools\engine-build\patch_frontend.py <build 目录>\main.cpp     # 必打,幂等
msys64\usr\bin\bash.exe -lc "export PATH=/mingw64/bin:/usr/bin:`$PATH; cd /d/<build目录>; \
  cmake -G Ninja -S . -B out -DCMAKE_BUILD_TYPE=Release \
    -DNCNN_SOURCE_DIR=<ncnn 源码目录> -DMSYS_MINGW=<...>/msys64/mingw64; \
  cmake --build out -j 20"
# 产物 out/realesrgan-ncnn-vulkan.exe → engines\realesrgan\realesrgan-ncnn-vulkan-2026.exe
```

`CMakeLists.txt` 顶部的 `NCNN_SOURCE_DIR` / `MSYS_MINGW` 是 cache 变量,按本机路径改。

## 唯一的源码补丁(为什么必须打)

上游前端按 MSVC 写:`swprintf` 里 `%s` 吃 `wchar_t*`。MinGW 按 C99,`%s` 是**窄**字符串,宽参数要 `%ls`。
不打补丁直接用 MinGW 编译,模型路径会被截成首字节(`models` → `m`),引擎找不到 `<model>.param`,
输出**空/全黑** —— 与我们要修的 GPU 故障长得一模一样,极易误判。
原代码还把 `std::to_string(scale)`(窄字符串)喂给宽 `%s`,任何平台都是错的。补丁同时改掉这两点。

## 交付形态

- 前端 CLI 与官方**完全一致**(`-i -o -s -t -m -n -g -j -x -f -v`),调用方不需要任何改动,属直接替换。
- 全静态链接,exe 30.6 MB,只依赖系统 DLL(KERNEL32 / msvcrt / ole32 / OLEAUT32),
  不需要随包分发 glslang / SPIRV / webp / gomp 等任何 DLL。
- 软件侧:`EngineService.FindRealESRGAN()` 优先用 `realesrgan-ncnn-vulkan-2026.exe`;
  探测结论缓存键用 `EngineService.EngineId()` 区分 `realesrgan2026` 与 `realesrgan`,
  避免旧版在 50 系上的失败结论把新引擎一起判死。
