#!/usr/bin/env python3
"""Regenerate every launcher / store / docs icon from the brand master.

Source of truth: docs/brand/dsh-mobile-art-1024.png (square illustration, badge fills the square).
Outputs: src/DshMobile.Android/Resources/mipmap-*/appicon*.png and docs/brand/*.png.

Geometry of the master, in its own 1024 px canvas:
  disc centre (511.5, 511.5), disc radius 504  (the circular badge touches the canvas edge)
The launcher composition places that disc at 86/108 of the adaptive-icon canvas (Android's 108 dp layer,
72 dp visible circle), so the rim lettering survives every OEM mask with a small margin.

Usage:  python tooling/brand/build-icons.py [--master docs/brand/dsh-mobile-art-1024.png]
"""
from __future__ import annotations

import argparse
import os
from pathlib import Path

from PIL import Image, ImageDraw

REPO = Path(__file__).resolve().parents[2]
RES = REPO / "src" / "DshMobile.Android" / "Resources"
DOCS = REPO / "docs" / "brand"

CX = CY = 511.5          # disc centre in the 1024 master
R = 504.0                # disc radius in the 1024 master
RATIO = 72.0 / 108.0     # badge diameter inside the 108 dp adaptive-icon canvas (= visible 72 dp)
LEGACY_RATIO = 0.88      # unmasked pre-API-26 bitmap: the plate is always fully visible, so fill more of it
DOC_RATIO = 0.88         # docs / store images are shown unmasked and framed by their own plate
BACKGROUND = (216, 232, 247)   # #D8E8F7, matches the illustration background

DENSITIES = {"mdpi": 108, "hdpi": 162, "xhdpi": 216, "xxhdpi": 324, "xxxhdpi": 432}
LEGACY = {"mdpi": 48, "hdpi": 72, "xhdpi": 96, "xxhdpi": 144, "xxxhdpi": 192}


def disc(source: Image.Image, size: int) -> Image.Image:
    box = tuple(round(v) for v in (CX - R, CY - R, CX + R, CY + R))
    img = source.crop(box).resize((size, size), Image.LANCZOS).convert("RGBA")
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).ellipse((0, 0, size - 1, size - 1), fill=255)
    img.putalpha(mask)
    return img


def foreground(source: Image.Image, size: int) -> Image.Image:
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = max(1, round(size * RATIO))
    canvas.alpha_composite(disc(source, d), ((size - d) // 2, (size - d) // 2))
    return canvas


def background(size: int) -> Image.Image:
    return Image.new("RGBA", (size, size), BACKGROUND + (255,))


def legacy(source: Image.Image, size: int) -> Image.Image:
    """Pre-API-26 launcher bitmap: same composition with a rounded plate and transparent corners."""
    ss = size * 4
    canvas = Image.new("RGBA", (ss, ss), BACKGROUND + (255,))
    d = round(ss * LEGACY_RATIO)
    canvas.alpha_composite(disc(source, d), ((ss - d) // 2, (ss - d) // 2))
    mask = Image.new("L", (ss, ss), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, ss - 1, ss - 1), radius=round(ss * 0.18), fill=255)
    canvas.putalpha(mask)
    return canvas.resize((size, size), Image.LANCZOS)


def padded(source: Image.Image, size: int) -> Image.Image:
    canvas = Image.new("RGBA", (size, size), BACKGROUND + (255,))
    d = round(size * DOC_RATIO)
    canvas.alpha_composite(disc(source, d), ((size - d) // 2, (size - d) // 2))
    return canvas.convert("RGB")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--master", default=str(DOCS / "dsh-mobile-art-1024.png"))
    args = ap.parse_args()
    source = Image.open(args.master).convert("RGB")

    for name, px in DENSITIES.items():
        out = RES / f"mipmap-{name}"
        out.mkdir(parents=True, exist_ok=True)
        foreground(source, px).save(out / "appicon_foreground.png", optimize=True)
        background(px).save(out / "appicon_background.png", optimize=True)
        legacy(source, LEGACY[name]).save(out / "appicon.png", optimize=True)

    DOCS.mkdir(parents=True, exist_ok=True)
    padded(source, 1024).save(DOCS / "dsh-mobile-logo.png", optimize=True)
    padded(source, 512).save(DOCS / "dsh-mobile-icon-512.png", optimize=True)


if __name__ == "__main__":
    main()
