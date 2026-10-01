"""Generates the app icon and MSIX logo assets from code (no binary design source to maintain).

Usage: python3 tools/make_icons.py   (requires Pillow)
"""
from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
BG = (24, 28, 38, 255)
ACCENT = (64, 196, 140, 255)
FG = (220, 226, 236, 255)


def glyph(size: int, pad_ratio: float = 0.0, transparent: bool = False) -> Image.Image:
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    pad = int(size * pad_ratio)
    box = (pad, pad, size - pad - 1, size - pad - 1)
    inner = size - 2 * pad
    if not transparent:
        d.rounded_rectangle(box, radius=max(2, inner // 6), fill=BG)
    stroke = max(2, inner // 11)
    # ">" chevron
    x0, y0 = pad + inner * 0.22, pad + inner * 0.30
    xm, ym = pad + inner * 0.46, pad + inner * 0.50
    y1 = pad + inner * 0.70
    d.line([(x0, y0), (xm, ym), (x0, y1)], fill=ACCENT, width=stroke, joint="curve")
    # "_" cursor
    d.line([(pad + inner * 0.52, y1), (pad + inner * 0.78, y1)], fill=FG, width=stroke)
    return img


def main() -> None:
    assets = ROOT / "src" / "Ussh.App" / "Assets"
    assets.mkdir(parents=True, exist_ok=True)
    glyph(256).save(assets / "ussh.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
    glyph(256).save(assets / "ussh.png")

    msix = ROOT / "packaging" / "msix" / "Images"
    msix.mkdir(parents=True, exist_ok=True)
    glyph(50).save(msix / "StoreLogo.png")
    glyph(44).save(msix / "Square44x44Logo.png")
    glyph(44, transparent=True).save(msix / "Square44x44Logo.targetsize-44_altform-unplated.png")
    glyph(150, pad_ratio=0.18).save(msix / "Square150x150Logo.png")
    wide = Image.new("RGBA", (310, 150), BG)
    wide.alpha_composite(glyph(150, pad_ratio=0.18), (80, 0))
    wide.save(msix / "Wide310x150Logo.png")


if __name__ == "__main__":
    main()
