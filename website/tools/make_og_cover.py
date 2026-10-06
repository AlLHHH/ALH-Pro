# -*- coding: utf-8 -*-
r"""生成官网分享卡片 website/assets/img/og-cover.png(1200x630)。

用途:各页 <meta property="og:image"> / <meta name="twitter:image"> 的缩略图 ——
在 IM、社区、搜索结果里分享 alhpro.cn 链接时显示的那张图。

配色与官网 assets/css/style.css §1.1 一致:
  --bg #0a0a0b / --fg #f4f4f5 / --fg-soft #a9a9b0 / --muted #8f8f98 / --accent #7da2ff / --ok #5ecb9a

用法(在仓库根目录执行):
  python website/tools/make_og_cover.py website/assets/img/logo.png website/assets/img/og-cover.png

依赖 Pillow;字体用系统自带的微软雅黑(C:\Windows\Fonts\msyhbd.ttc / msyh.ttc)。
改了页面主标题或卖点文案后,重跑一次即可。
"""
import sys
from PIL import Image, ImageDraw, ImageFilter, ImageFont

BG = (10, 10, 11)
FG = (244, 244, 245)
FG_SOFT = (169, 169, 176)
MUTED = (143, 143, 152)
ACCENT = (125, 162, 255)
OK = (94, 203, 154)

W, H = 1200, 630
BOLD = r"C:\Windows\Fonts\msyhbd.ttc"
REG = r"C:\Windows\Fonts\msyh.ttc"


def font(path, size):
    return ImageFont.truetype(path, size, index=0)


def fit(draw, text, path, max_w, start, low=20):
    """从 start 号字往下缩,直到整行宽度 <= max_w。"""
    size = start
    while size > low:
        f = font(path, size)
        if draw.textlength(text, font=f) <= max_w:
            return f
        size -= 2
    return font(path, low)


def glow(base, cx, cy, r, color, alpha):
    """一团柔光,呼应官网首屏的 aurora 光斑。"""
    layer = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=color + (alpha,))
    layer = layer.filter(ImageFilter.GaussianBlur(r * 0.55))
    base.alpha_composite(layer)


def main():
    logo_path, out_path = sys.argv[1], sys.argv[2]
    img = Image.new("RGBA", (W, H), BG + (255,))

    glow(img, 190, 70, 300, ACCENT, 64)
    glow(img, 1070, 570, 340, ACCENT, 46)
    glow(img, 620, 700, 320, OK, 22)

    d = ImageDraw.Draw(img)

    # 左上:站点标志 + 品牌名
    logo = Image.open(logo_path).convert("RGBA").resize((64, 64), Image.LANCZOS)
    img.alpha_composite(logo, (80, 62))
    d.text((160, 72), "ALH Pro", font=font(BOLD, 40), fill=FG)
    d.text((330, 84), "本地 AI 处理", font=font(REG, 26), fill=MUTED)

    # 主标题
    t1 = "本地视频 / 图片 AI 处理工具"
    d.text((80, 226), t1, font=fit(d, t1, BOLD, 1040, 78), fill=FG)

    # 能力行
    t2 = "超分 · 补帧 · 抠图 · 音频增强"
    d.text((82, 340), t2, font=fit(d, t2, REG, 1040, 46), fill=FG_SOFT)

    # 卖点行(强调色)
    t3 = "全程在本机运行 · 不上传素材 · 免费版开源(MIT)"
    d.text((82, 412), t3, font=fit(d, t3, BOLD, 1040, 34), fill=ACCENT)

    # 底栏:左域名 / 右平台
    d.line([(80, 528), (1120, 528)], fill=(255, 255, 255, 26), width=2)
    d.text((82, 552), "alhpro.cn", font=font(BOLD, 34), fill=FG_SOFT)
    right = "Windows 10 / 11 · 64 位 · 免费下载"
    fr = font(REG, 28)
    d.text((1120 - d.textlength(right, font=fr), 558), right, font=fr, fill=MUTED)

    img.convert("RGB").save(out_path, "PNG", optimize=True)
    print("OK ->", out_path, img.size)


if __name__ == "__main__":
    main()
