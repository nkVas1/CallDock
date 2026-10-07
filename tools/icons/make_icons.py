"""Draws the CallDock icons: the app icon, the tray icons (idle, recording, paused) and the logo PNGs.

Run with Python 3 and Pillow:  python tools/icons/make_icons.py
Everything is drawn at 1024 px and downscaled, so small sizes stay crisp.
"""
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[2]
ASSETS = ROOT / "src" / "CallDock.App" / "Assets"
DOCS = ROOT / "docs" / "images"
EXTENSION = ROOT / "extension" / "icons"

BASE = 1024
TEAL_TOP = (36, 168, 140)
TEAL_BOTTOM = (13, 84, 78)
RED = (232, 72, 72)
AMBER = (232, 181, 58)
DARK = (52, 38, 6)
WHITE = (255, 255, 255)
# Waveform bars: (relative height) — a voice standing on the dock line.
BARS = [0.30, 0.58, 0.86, 0.58, 0.40]


def squircle_mask(size: int, radius: float) -> Image.Image:
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, size - 1, size - 1), radius=int(size * radius), fill=255)
    return mask


def gradient(size: int) -> Image.Image:
    img = Image.new("RGB", (size, size))
    draw = ImageDraw.Draw(img)
    for y in range(size):
        t = y / (size - 1)
        color = tuple(int(TEAL_TOP[i] + (TEAL_BOTTOM[i] - TEAL_TOP[i]) * t) for i in range(3))
        draw.line([(0, y), (size, y)], fill=color)
    return img


def mark(simple: bool = False) -> Image.Image:
    """The full-size mark: rounded square, waveform bars, the dock line under them."""
    img = Image.new("RGBA", (BASE, BASE), (0, 0, 0, 0))
    img.paste(gradient(BASE), (0, 0), squircle_mask(BASE, 0.23))
    draw = ImageDraw.Draw(img)
    bars = [0.36, 0.84, 0.52] if simple else BARS
    count = len(bars)
    width = BASE * (0.13 if simple else 0.092)
    gap = BASE * (0.085 if simple else 0.058)
    total = count * width + (count - 1) * gap
    left = (BASE - total) / 2
    floor = BASE * 0.70
    for i, h in enumerate(bars):
        x = left + i * (width + gap)
        top = floor - BASE * 0.52 * h
        draw.rounded_rectangle((x, top, x + width, floor), radius=int(width / 2), fill=WHITE)
    line = BASE * (0.05 if simple else 0.036)
    draw.rounded_rectangle((BASE * 0.2, floor + BASE * 0.07, BASE * 0.8, floor + BASE * 0.07 + line),
                           radius=int(line / 2), fill=(255, 255, 255, 215))
    return img


def with_record_dot(img: Image.Image) -> Image.Image:
    out = img.copy()
    draw = ImageDraw.Draw(out)
    r = BASE * 0.25
    cx, cy = BASE * 0.76, BASE * 0.24
    draw.ellipse((cx - r - BASE * 0.04, cy - r - BASE * 0.04, cx + r + BASE * 0.04, cy + r + BASE * 0.04), fill=WHITE)
    draw.ellipse((cx - r, cy - r, cx + r, cy + r), fill=RED)
    return out


def with_pause_badge(img: Image.Image) -> Image.Image:
    """The recording dot's place taken by an amber badge with two bars: the recording is paused."""
    out = img.copy()
    draw = ImageDraw.Draw(out)
    r = BASE * 0.25
    cx, cy = BASE * 0.76, BASE * 0.24
    draw.ellipse((cx - r - BASE * 0.04, cy - r - BASE * 0.04, cx + r + BASE * 0.04, cy + r + BASE * 0.04), fill=WHITE)
    draw.ellipse((cx - r, cy - r, cx + r, cy + r), fill=AMBER)
    bar, gap, height = r * 0.30, r * 0.26, r * 1.05
    for x in (cx - gap / 2 - bar, cx + gap / 2):
        draw.rounded_rectangle((x, cy - height / 2, x + bar, cy + height / 2), radius=int(bar / 3), fill=DARK)
    return out


def scaled(img: Image.Image, size: int) -> Image.Image:
    return img.resize((size, size), Image.Resampling.LANCZOS)


def save_ico(path: Path, full: Image.Image, small: Image.Image, sizes: list[int]) -> None:
    frames = [scaled(small if s <= 24 else full, s) for s in sizes]
    frames[-1].save(path, format="ICO", sizes=[(s, s) for s in sizes], append_images=frames[:-1])


def main() -> None:
    ASSETS.mkdir(parents=True, exist_ok=True)
    DOCS.mkdir(parents=True, exist_ok=True)
    EXTENSION.mkdir(parents=True, exist_ok=True)
    full, small = mark(), mark(simple=True)
    save_ico(ASSETS / "calldock.ico", full, small, [16, 20, 24, 32, 40, 48, 64, 128, 256])
    save_ico(ASSETS / "tray-idle.ico", full, small, [16, 20, 24, 32, 48])
    save_ico(ASSETS / "tray-recording.ico", with_record_dot(full), with_record_dot(small), [16, 20, 24, 32, 48])
    save_ico(ASSETS / "tray-paused.ico", with_pause_badge(full), with_pause_badge(small), [16, 20, 24, 32, 48])
    scaled(full, 64).save(ASSETS / "logo-64.png")
    scaled(full, 256).save(ASSETS / "logo-256.png")
    scaled(full, 256).save(DOCS / "logo.png")
    for s in (16, 32, 48, 128):
        scaled(small if s <= 24 else full, s).save(EXTENSION / f"icon-{s}.png")
    print("icons written to", ASSETS, DOCS, EXTENSION)


if __name__ == "__main__":
    main()
