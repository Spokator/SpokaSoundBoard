"""Generates the Inno Setup wizard images (side banner and small header logo) at 100 % and 200 % scale."""
import os
from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "installer")
LOGO = Image.open(os.path.join(ROOT, "src", "SpokaSoundBoard", "Assets", "logo.png")).convert("RGBA")
BG = (15, 17, 23)


def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))


def banner(scale):
    w, h = 164 * scale, 314 * scale
    img = Image.new("RGB", (w, h), BG)
    px = img.load()
    top, bottom = (40, 30, 90), (10, 60, 80)
    for y in range(h):
        c = lerp(top, bottom, y / h)
        for x in range(w):
            px[x, y] = c
    d = ImageDraw.Draw(img)
    # Soft sound-wave bars in the background.
    for i, hh in enumerate([0.07, 0.13, 0.21, 0.13, 0.07, 0.11, 0.17]):
        bw = 10 * scale
        x = 14 * scale + i * 21 * scale
        bh = int(h * hh)
        d.rounded_rectangle([x, h - 40 * scale - bh, x + bw, h - 40 * scale], radius=bw // 2, fill=lerp((124, 92, 255), bottom, 0.6))
    logo = LOGO.resize((96 * scale, 96 * scale), Image.LANCZOS)
    img.paste(logo, ((w - logo.width) // 2, 46 * scale), logo)
    try:
        font = ImageFont.truetype("segoeuib.ttf", 17 * scale)
        small = ImageFont.truetype("segoeui.ttf", 11 * scale)
    except OSError:
        font = small = ImageFont.load_default()
    for text, f, y in (("SpokaSoundBoard", font, 158), ("Soundboard & voix", small, 182)):
        tw = d.textlength(text, font=f)
        d.text(((w - tw) / 2, y * scale), text, font=f, fill=(241, 243, 249) if f is font else (154, 163, 184))
    img.save(os.path.join(OUT, f"wizard-{scale * 100}.bmp"))


def small(scale):
    s = 55 * scale
    img = Image.new("RGB", (s, s), (255, 255, 255))
    logo = LOGO.resize((s, s), Image.LANCZOS)
    img.paste(logo, (0, 0), logo)
    img.save(os.path.join(OUT, f"wizard-small-{scale * 100}.bmp"))


for sc in (1, 2):
    banner(sc)
    small(sc)
print("ok")
