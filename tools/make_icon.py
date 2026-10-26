"""生成鹰锤系统监控（EagleHammer Monitor）图标：圆角方块 + 蓝青渐变 + 白色心电监测线。
python tools/make_icon.py  →  SysMonitor/Assets/app.ico（16~256 多尺寸）和预览 PNG"""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter

OUT = Path(__file__).resolve().parent.parent / "SysMonitor" / "Assets"
S = 1024  # 先按 1024 画，再缩小，边缘更平滑

PULSE = [(0.13, 0.57), (0.33, 0.57), (0.42, 0.29), (0.55, 0.79), (0.64, 0.45), (0.70, 0.57), (0.84, 0.57)]


def gradient(size, top, bottom):
    """左上到右下的线性渐变。"""
    g = Image.new("RGB", (size, size))
    px = g.load()
    for y in range(size):
        for x in range(size):
            t = (x + y) / (2 * (size - 1))
            px[x, y] = tuple(round(a + (b - a) * t) for a, b in zip(top, bottom))
    return g


def render(simple: bool) -> Image.Image:
    """simple=True 给 16/24/32 用：去掉网格，线条加粗。"""
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    mask = Image.new("L", (S, S), 0)
    m = 0.04 * S
    ImageDraw.Draw(mask).rounded_rectangle((m, m, S - m, S - m), radius=0.22 * S, fill=255)
    img.paste(gradient(S, (37, 99, 235), (6, 182, 212)), (0, 0), mask)

    d = ImageDraw.Draw(img)
    if not simple:
        # 淡淡的网格，像监控图表的背景
        grid = Image.new("RGBA", (S, S), (0, 0, 0, 0))
        gd = ImageDraw.Draw(grid)
        for f in (0.32, 0.57, 0.82):
            gd.line([(0.13 * S, f * S), (0.87 * S, f * S)], fill=(255, 255, 255, 40), width=int(0.012 * S))
        img.alpha_composite(Image.composite(grid, Image.new("RGBA", (S, S)), mask))

    pts = [(x * S, y * S) for x, y in PULSE]
    w = int((0.115 if simple else 0.075) * S)
    # 线条下方的柔和阴影，让白线在浅色背景上也立得住
    shadow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(shadow).line([(x, y + 0.02 * S) for x, y in pts], fill=(10, 40, 110, 110), width=w, joint="curve")
    img.alpha_composite(shadow.filter(ImageFilter.GaussianBlur(0.02 * S)))
    d = ImageDraw.Draw(img)
    d.line(pts, fill="white", width=w, joint="curve")
    for x, y in (pts[0], pts[-1]):  # 圆头
        r = w / 2
        d.ellipse((x - r, y - r, x + r, y + r), fill="white")
    if not simple:
        # 末端的亮点
        x, y = pts[-1]
        r = 0.065 * S
        d.ellipse((x - r, y - r, x + r, y + r), fill=(165, 243, 252))
        r = 0.035 * S
        d.ellipse((x - r, y - r, x + r, y + r), fill="white")
    return img


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    full, simple = render(False), render(True)
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    frames = [(simple if s <= 32 else full).resize((s, s), Image.LANCZOS) for s in sizes]
    frames[-1].save(OUT / "app.ico", sizes=[(s, s) for s in sizes], append_images=frames[:-1])
    full.resize((256, 256), Image.LANCZOS).save(OUT / "app.png")

    # 预览：各尺寸放在深色和浅色底上
    pv = Image.new("RGBA", (900, 300), (32, 32, 36, 255))
    ImageDraw.Draw(pv).rectangle((0, 150, 900, 300), fill=(243, 243, 243, 255))
    x = 10
    for s, f in zip(sizes, frames):
        if s > 128:
            continue
        for y0 in (0, 150):
            pv.alpha_composite(f, (x, y0 + (150 - s) // 2))
        x += s + 20
    pv.save(Path(__file__).resolve().parent / "icon_preview.png")


if __name__ == "__main__":
    main()
