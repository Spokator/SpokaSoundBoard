"""Generates the SpokaSoundBoard logo (PNG) and multi-size Windows icon (ICO)."""
import math
import os
from PIL import Image, ImageDraw, ImageFilter

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS = os.path.join(ROOT, "src", "SpokaSoundBoard", "Assets")
S = 1024


def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))


def make_logo():
    c1, c2 = (124, 92, 255), (34, 211, 238)
    grad = Image.new("RGB", (S, S))
    px = grad.load()
    for y in range(S):
        for x in range(S):
            px[x, y] = lerp(c1, c2, (x + y) / (2 * S))

    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([40, 40, S - 40, S - 40], radius=230, fill=255)
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    img.paste(grad, (0, 0), mask)

    d = ImageDraw.Draw(img)
    white = (255, 255, 255, 255)
    # Sound-wave bars.
    heights = [0.22, 0.42, 0.62, 0.42, 0.22]
    bar_w, gap = 74, 46
    total = len(heights) * bar_w + (len(heights) - 1) * gap
    x0 = (S - total) // 2
    cy = S // 2 - 30
    for i, h in enumerate(heights):
        hh = int(S * h / 2)
        x = x0 + i * (bar_w + gap)
        d.rounded_rectangle([x, cy - hh, x + bar_w, cy + hh], radius=bar_w // 2, fill=white)
    # Headset-style arc under the bars.
    d.arc([S * 0.2, S * 0.22, S * 0.8, S * 0.86], start=25, end=155, fill=(255, 255, 255, 230), width=40)

    shadow = img.filter(ImageFilter.GaussianBlur(0))
    return shadow


logo = make_logo()
os.makedirs(ASSETS, exist_ok=True)
logo.resize((256, 256), Image.LANCZOS).save(os.path.join(ASSETS, "logo.png"))
logo.save(os.path.join(ASSETS, "app.ico"), sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
print("ok")
