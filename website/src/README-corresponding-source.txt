ALH Pro —— 随包 FFmpeg / FFmpeg8 的「对应源码」包说明
================================================================================
文件版本: 2026-10-11
对应下载页: https://alhpro.cn/download.html#source
本目录三个文件就是本节下载页上那三个源码包本体。

安装包里的 engines\ffmpeg\ 与 engines\ffmpeg8\ 是启用了 --enable-gpl --enable-version3
的 FFmpeg,以独立进程方式被调用,因此适用 GNU General Public License version 3(GPLv3)。
GPLv3 第 6 条要求:分发二进制的一方要给出「对应源码」(Corresponding Source)。
这些包就是随包那两个二进制「同一版本」的完整源码 —— 免费、无需注册、无需付费,
任何人都可以下载、重建、再分发。

--------------------------------------------------------------------------------
一、三个包(下载后请核对大小与 SHA256)
--------------------------------------------------------------------------------
1) ffmpeg-src-N-126247-gb79d4c4c0a.tar.gz
   17,678,332 字节(17.7 MB),10,706 个条目
   SHA256 9620C19A58467276819E7B098B216C7BAC7176308C3D2664537B0CFB04F511A9
   上游提交 b79d4c4c0a160fc46988e98505af6039a53ad53e
   源码树对象 3afd407281a06fae884ae5ee395194ac206466ab
   包内顶层目录 FFmpeg-b79d4c4c0a160fc46988e98505af6039a53ad53e/
   → 对应随包 engines\ffmpeg\ffmpeg.exe(145,810,432 B,
     SHA256 4FD34432B34097836A8347E04ABF8DF709F168CCD92D1994EBC8C53B5D31539F),
     版本串 N-126247-gb79d4c4c0a-20260823

2) ffmpeg-src-n8.1.2-50-g1a748fe2cd.tar.gz
   17,028,369 字节(17.0 MB),10,257 个条目
   SHA256 AA3546C3E0D17584FB7CD1682C6740EAC7CBD326CB80D0922921F3508BC9779D
   上游提交 1a748fe2cd43e3ead22fafb1b5b7d77f153898a8(位于 release/8.1 分支)
   源码树对象 e064592f5b4fd313a825734da8e7c9c4f0ffab5f
   包内顶层目录 FFmpeg-1a748fe2cd43e3ead22fafb1b5b7d77f153898a8/
   → 对应随包 engines\ffmpeg8\ffmpeg.exe(541,184 B,
     SHA256 EF4D9585DE831269ADCB027D830EB1E1F97E504E16D4C1EC6295764B92BAEFC6,
     共享构建:同目录另有 8 个同族 DLL),
     版本串 n8.1.2-50-g1a748fe2cd-20260903

3) btbn-ffmpeg-builds-scripts-20261011.tar.gz
   118,656 字节(116 KB)
   SHA256 79CCED651490230275BB99936CBF84D7FFF5DDE9938882DA539FFDB30D8C26A3
   BtbN/FFmpeg-Builds 快照提交 63ac3265b0789ea16c7b1f8351cd2ad195a28722
   (提交时间 2026-10-10T13:13:43+02:00)
   包内顶层目录 FFmpeg-Builds-63ac3265b0789ea16c7b1f8351cd2ad195a28722/
   → 把上面两个提交编成 Windows 二进制的那套构建脚本(含 Dockerfile 与 scripts/)

注:免费版与 ALH Pro Max 随包的 ffmpeg 二进制逐字节相同(实测 SHA256 一致),
    因此这一份对应源码同时覆盖两个产品的随包二进制。

--------------------------------------------------------------------------------
二、这些包是怎么来的(可自行复现)
--------------------------------------------------------------------------------
· 两个 FFmpeg 源码包:直接从 FFmpeg 官方 Git 服务器检出上面那两个提交的源码树后打包,
  未作任何改动 —— 命令等价于
      git clone --filter=blob:none https://git.ffmpeg.org/ffmpeg.git
      git -C ffmpeg fetch --depth 1 origin <完整提交SHA>
      git -C ffmpeg archive --format=tar.gz --prefix=FFmpeg-<完整提交SHA>/ \
          -o ffmpeg-src-<SHA前10位>.tar.gz <完整提交SHA>
  由于 tar 会记录打包时间,本站包的字节与 GitHub 自动归档
  (https://github.com/FFmpeg/FFmpeg/archive/<完整提交SHA>.tar.gz)不同,
  但文件树与内容完全相同。
· 构建脚本包:git clone --depth 1 https://github.com/BtbN/FFmpeg-Builds.git 后
  按上述同样的 git archive 方式打包。
· 上游入口(长期可引用的独立备份):
    FFmpeg 源码仓库      https://github.com/FFmpeg/FFmpeg
    构建 1 上游归档快照  https://github.com/FFmpeg/FFmpeg/archive/b79d4c4c0a160fc46988e98505af6039a53ad53e.tar.gz
    构建 2 上游归档快照  https://github.com/FFmpeg/FFmpeg/archive/1a748fe2cd43e3ead22fafb1b5b7d77f153898a8.tar.gz
    构建脚本仓库        https://github.com/BtbN/FFmpeg-Builds

--------------------------------------------------------------------------------
三、源码与随包二进制的对应关系(实测交叉验证)
--------------------------------------------------------------------------------
版本号不是靠猜的,是用随包二进制自报的库版本与源码里的版本头双向对上的:

  构建 1  ffmpeg -version 自报 libavutil 61.5.100 / libavcodec 63.8.101
          该提交源码 libavutil/version.h  = 61.5.100
          该提交源码 libavcodec/version.h = 63.8.101

  构建 2  ffmpeg -version 自报 libavutil 60.26.102 / libavcodec 62.28.102
          该提交源码 libavutil/version.h  = 60.26.102
          该提交源码 libavcodec/version.h = 62.28.102

两个源码包含完整源码树(含 configure 与全部 libav* 子目录),不是补丁或部分文件。

--------------------------------------------------------------------------------
四、如何用这些包重建
--------------------------------------------------------------------------------
解包后进入顶层目录,用下面两行「完整 configuration」逐字配置构建(这两行是随包
二进制 -version 输出的实测值,不是推断值),再用第三包里的 BtbN 构建脚本/交叉编译
工具链编译,即可得到与随包 ffmpeg 同配置的构建。

[构建 1] 主 ffmpeg —— 静态单文件(N-126247-gb79d4c4c0a-20260823)
  built with gcc 15.2.0 (crosstool-NG 1.28.0.23_185f348)
  libavutil 61.5.100 / libavcodec 63.8.101 / libavformat 63.6.100 /
  libavdevice 63.2.100 / libavfilter 12.3.101 / libswscale 10.2.100 /
  libswresample 7.2.100
configuration: --prefix=/ffbuild/prefix --pkg-config-flags=--static --pkg-config=pkg-config --cross-prefix=x86_64-w64-mingw32- --arch=x86_64 --target-os=mingw32 --enable-gpl --enable-version3 --disable-debug --disable-w32threads --enable-pthreads --enable-iconv --enable-zlib --enable-libxml2 --enable-libvmaf --enable-fontconfig --enable-libharfbuzz --enable-libfreetype --enable-libfribidi --enable-vulkan --enable-libvorbis --disable-libxcb --disable-xlib --disable-libpulse --enable-gmp --enable-lzma --enable-liblcevc-dec --enable-opencl --enable-amf --enable-libaom --enable-libaribb24 --enable-avisynth --enable-chromaprint --enable-libdav1d --enable-libdavs2 --enable-libdvdread --enable-libdvdnav --disable-libfdk-aac --enable-ffnvcodec --enable-cuda-llvm --enable-frei0r --enable-libgme --enable-libkvazaar --enable-libaribcaption --enable-libass --enable-libbluray --enable-libjxl --enable-libmp3lame --enable-libopus --enable-libplacebo --enable-librist --enable-libssh --enable-libtheora --enable-libvpx --enable-libwebp --enable-libzmq --enable-lv2 --enable-libvpl --enable-openal --enable-liboapv --enable-libopencore-amrnb --enable-libopencore-amrwb --enable-libopenh264 --enable-libopenjpeg --enable-libopenmpt --enable-librav1e --enable-librubberband --enable-schannel --enable-sdl2 --enable-libsnappy --enable-libsoxr --enable-libsrt --enable-libsvtav1 --enable-libtwolame --enable-libuavs3d --disable-libdrm --enable-vaapi --enable-libvidstab --enable-libvvenc --disable-whisper --enable-libx264 --enable-libx265 --enable-libxavs2 --enable-libxvid --enable-libzimg --enable-libzvbi --extra-cflags=-DLIBTWOLAME_STATIC --extra-cxxflags= --extra-libs=-lgomp --extra-ldflags=-pthread --extra-ldexeflags= --cc=x86_64-w64-mingw32-gcc --cxx=x86_64-w64-mingw32-g++ --ar=x86_64-w64-mingw32-gcc-ar --ranlib=x86_64-w64-mingw32-gcc-ranlib --nm=x86_64-w64-mingw32-gcc-nm --extra-version=20260823

[构建 2] 备用 ffmpeg8 —— 共享构建(n8.1.2-50-g1a748fe2cd-20260903)
  built with gcc 15.2.0 (crosstool-NG 1.28.0.23_185f348)
  libavutil 60.26.102 / libavcodec 62.28.102 / libavformat 62.12.102 /
  libavdevice 62.3.102 / libavfilter 11.14.102 / libswscale 9.5.102 /
  libswresample 6.3.102
  与构建 1 的配置差别:多了 --enable-shared --disable-static 与 --enable-libshaderc,
  extra-version 也不同。
configuration: --prefix=/ffbuild/prefix --pkg-config-flags=--static --pkg-config=pkg-config --cross-prefix=x86_64-w64-mingw32- --arch=x86_64 --target-os=mingw32 --enable-gpl --enable-version3 --disable-debug --enable-shared --disable-static --disable-w32threads --enable-pthreads --enable-iconv --enable-zlib --enable-libxml2 --enable-libvmaf --enable-fontconfig --enable-libharfbuzz --enable-libfreetype --enable-libfribidi --enable-vulkan --enable-libshaderc --enable-libvorbis --disable-libxcb --disable-xlib --disable-libpulse --enable-gmp --enable-lzma --enable-liblcevc-dec --enable-opencl --enable-amf --enable-libaom --enable-libaribb24 --enable-avisynth --enable-chromaprint --enable-libdav1d --enable-libdavs2 --enable-libdvdread --enable-libdvdnav --disable-libfdk-aac --enable-ffnvcodec --enable-cuda-llvm --enable-frei0r --enable-libgme --enable-libkvazaar --enable-libaribcaption --enable-libass --enable-libbluray --enable-libjxl --enable-libmp3lame --enable-libopus --enable-libplacebo --enable-librist --enable-libssh --enable-libtheora --enable-libvpx --enable-libwebp --enable-libzmq --enable-lv2 --enable-libvpl --enable-openal --enable-liboapv --enable-libopencore-amrnb --enable-libopencore-amrwb --enable-libopenh264 --enable-libopenjpeg --enable-libopenmpt --enable-librav1e --enable-librubberband --enable-schannel --enable-sdl2 --enable-libsnappy --enable-libsoxr --enable-libsrt --enable-libsvtav1 --enable-libtwolame --enable-libuavs3d --disable-libdrm --enable-vaapi --enable-libvidstab --enable-libvvenc --disable-whisper --enable-libx264 --enable-libx265 --enable-libxavs2 --enable-libxvid --enable-libzimg --enable-libzvbi --extra-cflags=-DLIBTWOLAME_STATIC --extra-cxxflags= --extra-libs=-lgomp --extra-ldflags=-pthread --extra-ldexeflags= --cc=x86_64-w64-mingw32-gcc --cxx=x86_64-w64-mingw32-g++ --ar=x86_64-w64-mingw32-gcc-ar --ranlib=x86_64-w64-mingw32-gcc-ranlib --nm=x86_64-w64-mingw32-gcc-nm --extra-version=20260903

--------------------------------------------------------------------------------
五、许可
--------------------------------------------------------------------------------
· 上述 FFmpeg 源码与构建脚本按其上游许可分发:FFmpeg 本体为 LGPLv2.1+,而本软件随包
  的两个构建启用了 --enable-gpl --enable-version3,整体按 GPLv3 处理。
· GPLv3 全文(官方):https://www.gnu.org/licenses/gpl-3.0.txt
  安装包内另有两份同样的文本:engines\ffmpeg\COPYING.GPLv3 与
  engines\ffmpeg8\COPYING.GPLv3;网站许可页:https://alhpro.cn/licenses.html
· 网站上的其他组件许可与出处见 https://alhpro.cn/licenses.html。

--------------------------------------------------------------------------------
六、下载后自查(Windows/macOS/Linux 均可)
--------------------------------------------------------------------------------
  sha256sum ffmpeg-src-N-126247-gb79d4c4c0a.tar.gz
  sha256sum ffmpeg-src-n8.1.2-50-g1a748fe2cd.tar.gz
  sha256sum btbn-ffmpeg-builds-scripts-20261011.tar.gz
  tar -tzf ffmpeg-src-N-126247-gb79d4c4c0a.tar.gz | head

若本站任一地址失效,我们会在同一页面 30 日内恢复或以等价的公开位置提供(仍免费、无需注册);
同时上面第二节列出的上游入口始终可用。
