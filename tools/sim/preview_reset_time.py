"""Render reset-time comparison using firmware glyphs and built-in sprite data."""
from datetime import datetime, timedelta, timezone
from pathlib import Path
import re
import sys

from PIL import Image, ImageDraw, ImageFont
from sim_screen import Sim

ROOT = Path(__file__).resolve().parents[2]
SOURCE = (ROOT / "firmware/src/main.cpp").read_text(encoding="utf-8")
GLYPHS = {}
block = SOURCE.split("const DotGlyph dotGlyphs[] = {")[1].split("\n};")[0]
for char, width, bits in re.findall(r"\{'(.)', (\d+), \{([^}]+)\}\}", block):
    GLYPHS[char] = (int(width), [int(v.strip(), 2) for v in bits.split(",")])
TINY = {}
block = SOURCE.split("const TinyGlyph tinyGlyphs[] = {")[1].split("\n};")[0]
for char, bits in re.findall(r"\{'(.)', \{([^}]+)\}\}", block):
    TINY[char] = [int(v.strip(), 2) for v in bits.split(",")]


def asset(name):
    source = (ROOT / f"firmware/include/img/{name}.h").read_text()
    w = int(re.search(r"#define \w+_W (\d+)", source)[1])
    h = int(re.search(r"#define \w+_H (\d+)", source)[1])
    body = re.search(r"\b" + name + r"_0\[\d+\] PROGMEM = \{(.*?)\};", source, re.S)[1]
    pixels = []
    for number in re.findall(r"\d+", body):
        raw = int(number)
        c = ((raw & 255) << 8) | (raw >> 8)
        pixels.append((((c >> 11) & 31) * 255 // 31, ((c >> 5) & 63) * 255 // 63, (c & 31) * 255 // 31))
    im = Image.new("RGB", (w, h))
    im.putdata(pixels)
    return im


def glyph(char):
    return GLYPHS.get(char, GLYPHS.get(char.upper(), (3, [0] * 7)))


def dot_width(text, pitch, radius, square=False):
    return sum((glyph(c)[0] - 1) * pitch + (2 if square else 2 * radius + 1) + pitch for c in text) - pitch


def dots(im, text, cx, y, pitch=2, radius=0, color=(255, 255, 255), square=False):
    draw = ImageDraw.Draw(im)
    x = cx - dot_width(text, pitch, radius, square) // 2
    for char in text:
        w, rows = glyph(char)
        for row, bits in enumerate(rows):
            for col in range(w):
                if bits & (1 << (w - 1 - col)):
                    dx, dy = x + col * pitch, y + row * pitch
                    if square:
                        draw.rectangle((dx, dy, dx + 1, dy + 1), fill=color)
                    elif radius:
                        draw.ellipse((dx, dy, dx + 2 * radius, dy + 2 * radius), fill=color)
                    else:
                        draw.point((dx, dy), fill=color)
        x += (w - 1) * pitch + (2 if square else 2 * radius + 1) + pitch


def tiny(im, text, cx, y):
    draw = ImageDraw.Draw(im)
    x = cx - (len(text) * 8 - 2) // 2
    for char in text:
        for row, bits in enumerate(TINY.get(char, [])):
            for col in range(3):
                if bits & (4 >> col):
                    draw.rectangle((x + col * 2, y + row * 2, x + col * 2 + 1, y + row * 2 + 1), fill=(211, 211, 211))
        x += 8


NOW = datetime(2026, 10, 29, 12, 11, tzinfo=timezone(timedelta(hours=8)))
R5, RW = 139, 3779


def pet(absolute):
    im = Image.new("RGB", (240, 240))
    # Firmware square ring, 36% clockwise from the top-left corner.
    draw = ImageDraw.Draw(im)
    draw.rectangle((4, 4, 235, 13), fill=(0, 255, 0))
    draw.rectangle((226, 4, 235, 105), fill=(0, 255, 0))
    sprite = asset("claude_sprite")
    im.paste(sprite, (120 - sprite.width // 2, 120 - sprite.height // 2))
    im.paste(asset("claude_logo"), (14, 18))
    for cx, label, minutes in [(116, "5H RESET", R5), (194, "WK RESET", RW)]:
        tiny(im, label, cx, 18)
        if absolute:
            at = NOW + timedelta(minutes=minutes)
            dots(im, at.strftime("%m/%d"), cx, 30)
            dots(im, at.strftime("%H:%M"), cx, 46)
        else:
            value = f"{minutes // 60}h{minutes % 60}m" if cx == 116 else f"{(minutes + 1439) // 1440}d"
            pitch = 3 if dot_width(value, 3, 1) <= 64 else 2
            dots(im, value, cx, 33, pitch, 1 if pitch == 3 else 0)
    for cx, label, value in [(70, "5h", "36%"), (170, "Wk", "52%")]:
        dots(im, label, cx, 183, color=(211, 211, 211), square=True)
        dots(im, value, cx, 199, 3, 1)
    return im


def main():
    out = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / "output/reset-time-comparison.png"
    out.parent.mkdir(parents=True, exist_ok=True)
    cl = dict(status="working", p5=36, pw=52, r5=R5, rw=RW)
    codex = dict(status="idle", pw=21, rw=9924)
    panels = [pet(False), pet(True)]
    panels += [Sim().draw(cl, codex, True, fmt, int(NOW.timestamp()), 480) for fmt in ["countdown", "datetime"]]
    font_path = "C:/Windows/Fonts/msyh.ttc"
    title = ImageFont.truetype(font_path, 30)
    label = ImageFont.truetype(font_path, 22)
    small = ImageFont.truetype(font_path, 17)
    sheet = Image.new("RGB", (1104, 1240), "#edf0f1")
    draw = ImageDraw.Draw(sheet)
    draw.text((48, 25), "重置时间显示 · 两种模式对照", font=title, fill="#142221")
    draw.text((48, 74), "按当前固件布局模拟，使用示例额度与内置桌宠；屏幕画面放大 2 倍。", font=small, fill="#536360")
    for i, panel in enumerate(panels):
        x, y = 48 + (i % 2) * 528, 154 + (i // 2) * 546
        prefix = "桌宠页" if i < 2 else "双栏额度页"
        suffix = "倒计时" if i % 2 == 0 else "具体日期时间"
        draw.text((x, y - 38), f"{prefix} · {suffix}", font=label, fill="#142221")
        sheet.paste(panel.resize((480, 480), Image.Resampling.NEAREST), (x, y))
        panel.save(out.with_name(f"reset-time-{'pet' if i < 2 else 'dual'}-{'countdown' if i % 2 == 0 else 'datetime'}.png"))
    draw.text((48, 1190), "示例：5 小时窗口于 10/29 14:30 重置，周窗口于 11/01 03:10 重置。", font=small, fill="#536360")
    sheet.save(out)
    print(out)


if __name__ == "__main__":
    main()
