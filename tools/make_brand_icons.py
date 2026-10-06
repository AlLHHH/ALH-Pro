# -*- coding: utf-8 -*-
"""从 assets/icon.ico 生成全套品牌图标 —— 黑底白字(免费版 ALH Pro)。

背景(2026-10-06):
    品牌图标只有一个母版 —— ``assets/icon.ico``(黑底圆角方块 + 白色 AP)。
    它同时是 EXE 图标(``ImgUpscalerUI.csproj`` 的 ``ApplicationIcon``)、
    三个安装器的图标(``installer*.iss`` 的 ``SetupIconFile``)和窗口图标
    (``App.xaml.cs`` 运行时 ``AppWindow.SetIcon``)。

    但网站与应用内导航此前用的是**另一份白底黑字**的 logo(三处同文件:
    ``website/assets/img/logo.png``、``ImgUpscalerUI/ALHPro_icon.png``、
    ``ImgUpscalerUI/Assets/ALHPro_icon.png``),跟 EXE 图标不一致。本脚本
    把这一份也统一成母版的黑底白字。

颜色口径(用户 2026-10-06 定):
    * 免费版 ALH Pro      = **黑底白字**  ← 默认(``--variant free``)
    * 专业版 ALH Pro Max  = **白底黑字**  ← 将来用 ``--variant max`` 生成(把母版反色)

用法(在仓库根目录执行):
    python tools/make_brand_icons.py                # 生成免费版(黑底白字),覆盖现行资产
    python tools/make_brand_icons.py --variant max  # 生成专业版配色(白底黑字),写到 -max 后缀文件,
                                                    # 不覆盖免费版资产;正式启用 Max 时再改名替换

依赖 Pillow。脚本只做「缩放 + 导出」,不重新排版字母 —— 母版长什么样,生成出来就是什么样,
所以各处图标的字形、间距永远一致。
"""
import argparse
import os
import struct
import sys

from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MASTER = os.path.join(ROOT, "assets", "icon.ico")

# (输出路径, 边长)
PNG_OUTPUTS = [
    (os.path.join(ROOT, "website", "assets", "img", "logo.png"), 256),
    (os.path.join(ROOT, "website", "assets", "img", "favicon-32.png"), 32),
    (os.path.join(ROOT, "website", "assets", "img", "apple-touch-icon.png"), 180),
    (os.path.join(ROOT, "ImgUpscalerUI", "ALHPro_icon.png"), 256),
    (os.path.join(ROOT, "ImgUpscalerUI", "Assets", "ALHPro_icon.png"), 256),
]
ICO_OUTPUT = os.path.join(ROOT, "website", "favicon.ico")
ICO_SIZES = [16, 32, 48, 64, 128, 256]


def load_master() -> Image.Image:
    im = Image.open(MASTER)
    sizes = sorted(im.ico.sizes()) if hasattr(im, "ico") else []
    im.size = max(sizes) if sizes else im.size
    return im.convert("RGBA")


def invert_rgb(im: Image.Image) -> Image.Image:
    """黑底白字 ←→ 白底黑字(保留 alpha,圆角不糊)。"""
    r, g, b, a = im.split()
    rgb = Image.merge("RGB", (r, g, b))
    from PIL import ImageOps

    rgb = ImageOps.invert(rgb)
    out = Image.merge("RGBA", (*rgb.split(), a))
    return out


def scaled(im: Image.Image, size: int) -> Image.Image:
    return im.resize((size, size), Image.LANCZOS)


def variant_path(path: str, variant: str) -> str:
    """免费版用原路径;专业版一律带 -max 后缀,永不覆盖免费版资产。"""
    if variant == "free":
        return path
    stem, ext = os.path.splitext(path)
    return f"{stem}-max{ext}"


def write_ico(frames, path: str) -> None:
    """手写 ICO 容器(每帧用 PNG 存),避免依赖 Pillow 内部的重采样实现。"""
    blobs = []
    for f in frames:
        buf = os.path.join(os.environ.get("TEMP", "."), "_ico_frame.png")
        f.save(buf, "PNG", optimize=True)
        with open(buf, "rb") as fh:
            blobs.append(fh.read())
        os.remove(buf)

    n = len(blobs)
    header = struct.pack("<HHH", 0, 1, n)
    offset = 6 + 16 * n
    entries, body = b"", b""
    for f, blob in zip(frames, blobs):
        w, h = f.size
        entries += struct.pack(
            "<BBBBHHII", w if w < 256 else 0, h if h < 256 else 0, 0, 0, 1, 32, len(blob), offset
        )
        offset += len(blob)
        body += blob
    with open(path, "wb") as fh:
        fh.write(header + entries + body)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--variant", choices=["free", "max"], default="free")
    args = ap.parse_args()

    if not os.path.exists(MASTER):
        print(f"❌ 找不到母版:{MASTER}")
        return 1

    master = load_master()
    if args.variant == "max":
        master = invert_rgb(master)
        print("变体:专业版 ALH Pro Max(白底黑字,反色自母版)")

    for path, size in PNG_OUTPUTS:
        path = variant_path(path, args.variant)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        scaled(master, size).save(path, "PNG", optimize=True)
        print(f"  PNG {size:>3}×{size:<3} {os.path.relpath(path, ROOT)}  ({os.path.getsize(path)} B)")

    ico_out = variant_path(ICO_OUTPUT, args.variant)
    os.makedirs(os.path.dirname(ico_out), exist_ok=True)
    frames = [scaled(master, s) for s in ICO_SIZES]
    write_ico(frames, ico_out)
    print(
        f"  ICO {','.join(str(s) for s in ICO_SIZES)}  {os.path.relpath(ico_out, ROOT)}"
        f"  ({os.path.getsize(ico_out)} B)"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
