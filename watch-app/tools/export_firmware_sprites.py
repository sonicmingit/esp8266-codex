"""Losslessly convert the firmware's wire-order RGB565 frames to PNG resources.

No invented pet art; source attribution follows firmware/include/img and NOTICE.md.
"""
from pathlib import Path
import re
from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
DEST = ROOT / "watch-app/res/drawable-nodpi"


def export():
    DEST.mkdir(parents=True, exist_ok=True)
    for name in ("claude", "codex"):
        source = (ROOT / f"firmware/include/img/{name}_sprite.h").read_text(encoding="utf-8")
        width = int(re.search(r"_SPRITE_W (\d+)", source)[1])
        height = int(re.search(r"_SPRITE_H (\d+)", source)[1])
        frames = re.findall(rf"{name}_sprite_(\d+)\[\d+\] PROGMEM = \{{(.*?)\}};", source, re.S)
        assert len(frames) == 6, f"Unexpected {name} frame count"
        for index, body in frames:
            values = [int(v, 0) for v in re.findall(r"0x[0-9a-fA-F]+|\d+", body)]
            assert len(values) == width * height
            pixels = []
            for wire in values:
                rgb = ((wire & 255) << 8) | (wire >> 8)
                r, g, b = (rgb >> 11) & 31, (rgb >> 5) & 63, rgb & 31
                pixels.append(((r << 3) | (r >> 2), (g << 2) | (g >> 4), (b << 3) | (b >> 2)))
            image = Image.new("RGB", (width, height))
            image.putdata(pixels)
            image.save(DEST / f"{name}_{index}.png", optimize=True)
        print(f"Exported {len(frames)} {name} frames ({width}x{height})")


if __name__ == "__main__":
    export()
