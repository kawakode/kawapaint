#!/usr/bin/env python3
"""Regenerates the committed macOS packaging assets (needs Pillow; runs on any OS).

    python3 mac/dmg/make-assets.py

- KawaPaint.icns: the app/volume icon, built from shared/KawaPaint.App/Assets/icon.ico.
- background.png / background@2x.png: the DMG window background (660x400 points) with an
  arrow from the app icon (left) to the Applications link (right). The icon positions in
  dmgbuild-settings.py must stay in sync with APP_X / APPS_X / ICON_Y below.
"""
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent

WIDTH, HEIGHT = 660, 400
APP_X, APPS_X, ICON_Y = 170, 490, 190
ICON_SIZE = 128


def font(size: int) -> ImageFont.ImageFont:
    for candidate in ("/System/Library/Fonts/Helvetica.ttc",
                      "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
                      "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf"):
        if Path(candidate).exists():
            return ImageFont.truetype(candidate, size)
    return ImageFont.load_default()


def background(scale: int) -> Image.Image:
    w, h = WIDTH * scale, HEIGHT * scale
    img = Image.new("RGB", (w, h))
    # Soft vertical gradient so Finder's icon labels (dark text) stay readable.
    top, bottom = (250, 250, 252), (226, 230, 238)
    px = img.load()
    for y in range(h):
        t = y / (h - 1)
        c = tuple(round(a + (b - a) * t) for a, b in zip(top, bottom))
        for x in range(w):
            px[x, y] = c
    d = ImageDraw.Draw(img)

    # Arrow between the two icons.
    half = ICON_SIZE // 2 + 22
    x0, x1, y = (APP_X + half) * scale, (APPS_X - half) * scale, ICON_Y * scale
    color = (120, 128, 150)
    thickness = 6 * scale
    head = 18 * scale
    d.line([(x0, y), (x1 - head, y)], fill=color, width=thickness)
    d.polygon([(x1, y), (x1 - head - 4 * scale, y - head), (x1 - head - 4 * scale, y + head)], fill=color)

    title = font(22 * scale)
    text = "Drag KawaPaint to Applications"
    tw = d.textlength(text, font=title)
    d.text(((w - tw) / 2, 52 * scale), text, font=title, fill=(60, 64, 80))
    sub = font(13 * scale)
    text = "Glissez KawaPaint dans le dossier Applications"
    tw = d.textlength(text, font=sub)
    d.text(((w - tw) / 2, 86 * scale), text, font=sub, fill=(110, 116, 134))
    return img


def icns() -> None:
    ico = Image.open(ROOT / "shared/KawaPaint.App/Assets/icon.ico")
    ico.size = max(ico.info["sizes"])
    ico.load()
    src = ico.convert("RGBA")
    sizes = [16, 32, 64, 128, 256, 512, 1024]
    images = [src.resize((s, s), Image.LANCZOS) for s in sizes]
    images[-1].save(HERE / "KawaPaint.icns", append_images=images[:-1])


if __name__ == "__main__":
    background(1).save(HERE / "background.png", optimize=True)
    background(2).save(HERE / "background@2x.png", optimize=True)
    icns()
    print("Wrote", ", ".join(p.name for p in sorted(HERE.glob("*")) if p.suffix in (".png", ".icns")))
