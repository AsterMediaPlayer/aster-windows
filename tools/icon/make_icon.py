"""Stacks Aster's layered (Apple TV-style) icon into a square app icon.

    pip install pillow
    python3 make_icon.py back.png middle.png front.png ../../src/Aster.Desktop/Assets

Writes aster.png (1024 px) and aster.ico (16-256 px): a rounded square, the
galaxy dimmed a little behind the mark. Small sizes soften the galaxy and
draw the mark bigger, so it still reads in a taskbar.
"""
import sys
from PIL import Image, ImageDraw, ImageEnhance, ImageFilter

back_path, middle_path, front_path, out = sys.argv[1:5]
RADIUS = 0.2237  # the corner, as a share of the side

back = Image.open(back_path).convert("RGBA")
layers = [Image.open(p).convert("RGBA") for p in (middle_path, front_path)]
w, h = back.size
side = min(w, h)
square = back.crop(((w - side) // 2, (h - side) // 2, (w + side) // 2, (h + side) // 2))


def render(size, mark, dim, blur):
    canvas = square.resize((size, size), Image.LANCZOS)
    if blur:
        canvas = canvas.filter(ImageFilter.GaussianBlur(size * blur))
    canvas = ImageEnhance.Brightness(canvas).enhance(dim)
    for layer in layers:
        scale = size / side * mark
        scaled = layer.resize((round(layer.width * scale), round(layer.height * scale)), Image.LANCZOS)
        x, y = (size - scaled.width) // 2, (size - scaled.height) // 2
        canvas.alpha_composite(scaled.crop((max(0, -x), max(0, -y), max(0, -x) + min(size, scaled.width), max(0, -y) + min(size, scaled.height))),
                               (max(0, x), max(0, y)))
    big = size * 4
    mask = Image.new("L", (big, big), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, big - 1, big - 1), radius=round(big * RADIUS), fill=255)
    canvas.putalpha(mask.resize((size, size), Image.LANCZOS))
    return canvas


def icon(size):
    return render(size, 0.84, 0.85, 0) if size > 64 else render(size, 0.98, 0.7, 0.03)


icon(1024).save(f"{out}/aster.png")
sizes = [16, 24, 32, 48, 64, 128, 256]
frames = [icon(s) for s in sizes]
frames[-1].save(f"{out}/aster.ico", sizes=[(s, s) for s in sizes], append_images=frames[:-1])
