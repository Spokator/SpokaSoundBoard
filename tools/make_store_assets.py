"""Generates the MSIX visual assets (tiles, Store logo, splash screen) into packaging/Assets.

Usage: python tools/make_store_assets.py

The logo is re-rendered at 1024 px with make_logo() from tools/make_icon.py (sharper than upscaling the
256 px src/SpokaSoundBoard/Assets/logo.png, which is used as a fallback). Tiles are transparent: Windows
fills them with the manifest BackgroundColor (#0b0d14). The splash screen is opaque, same colour.
"""
import os
from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "packaging", "Assets")
LOGO_PNG = os.path.join(ROOT, "src", "SpokaSoundBoard", "Assets", "logo.png")
MAKE_ICON = os.path.join(ROOT, "tools", "make_icon.py")
BACKGROUND = (0x0B, 0x0D, 0x14, 255)

SCALES = [100, 125, 150, 200, 400]
# Taskbar / Start / Explorer sizes for Square44x44Logo.targetsize-N (plated and unplated).
TARGET_SIZES = [16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256]


def load_logo():
    """Square RGBA logo, as large as possible."""
    try:
        with open(MAKE_ICON, encoding="utf-8-sig") as f:
            source = f.read()
        # Only the definitions: the end of make_icon.py writes logo.png / app.ico.
        definitions = source.split("\nlogo = make_logo()")[0]
        namespace = {"__file__": MAKE_ICON}
        exec(compile(definitions, MAKE_ICON, "exec"), namespace)
        return namespace["make_logo"]().convert("RGBA")
    except Exception as ex:  # noqa: BLE001 - fall back to the shipped PNG
        print(f"make_logo() unavailable ({ex}), using {LOGO_PNG}")
        return Image.open(LOGO_PNG).convert("RGBA")


def canvas(width, height, logo, logo_ratio, opaque=False):
    """Logo centred on a width x height canvas, its side = logo_ratio * min(width, height)."""
    img = Image.new("RGBA", (width, height), BACKGROUND if opaque else (0, 0, 0, 0))
    side = max(1, round(min(width, height) * logo_ratio))
    resized = logo.resize((side, side), Image.LANCZOS)
    img.alpha_composite(resized, ((width - side) // 2, (height - side) // 2))
    return img


def save(img, name):
    img.save(os.path.join(OUT, name), optimize=True)


def main():
    os.makedirs(OUT, exist_ok=True)
    for old in os.listdir(OUT):
        if old.lower().endswith(".png"):
            os.remove(os.path.join(OUT, old))
    logo = load_logo()

    # (base name, base width, base height, logo ratio, opaque, scales)
    specs = [
        ("Square44x44Logo", 44, 44, 1.0, False, SCALES),
        ("Square71x71Logo", 71, 71, 0.72, False, SCALES),
        ("Square150x150Logo", 150, 150, 0.6, False, SCALES),
        ("Wide310x150Logo", 310, 150, 0.6, False, SCALES),
        ("StoreLogo", 50, 50, 1.0, False, SCALES),
        ("SplashScreen", 620, 300, 0.5, True, [100, 125, 150, 200]),
    ]
    count = 0
    for name, w, h, ratio, opaque, scales in specs:
        for scale in scales:
            sw, sh = round(w * scale / 100), round(h * scale / 100)
            save(canvas(sw, sh, logo, ratio, opaque), f"{name}.scale-{scale}.png")
            count += 1

    for size in TARGET_SIZES:
        img = canvas(size, size, logo, 1.0)
        save(img, f"Square44x44Logo.targetsize-{size}.png")
        save(img, f"Square44x44Logo.targetsize-{size}_altform-unplated.png")
        save(img, f"Square44x44Logo.targetsize-{size}_altform-lightunplated.png")
        count += 3

    print(f"ok: {count} images in {OUT}")


if __name__ == "__main__":
    main()
