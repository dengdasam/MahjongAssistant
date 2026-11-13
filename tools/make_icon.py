"""Compose the app .ico from the open-licensed glyph.

Icon art: iconoven/mahjong-tile (MIT, via Iconify) — see assets/icons/NOTICE.md.
This script only does standard colouring/background treatment on top of that art.

Run: python tools/rasterize_icon.py && python tools/make_icon.py
"""
import io
import pathlib
import struct

from PIL import Image, ImageDraw

ROOT = pathlib.Path(__file__).resolve().parent.parent
OUT = ROOT / "src/Mahjong.Desktop/assets/app.ico"

TEAL_TOP = (0x2C, 0x46, 0x53)
TEAL_BOTTOM = (0x40, 0x64, 0x76)

SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
SMALL_BELOW = 40  # 16-32px use the filled-mark variant


def background(size: int) -> Image.Image:
    """Teal rounded square, vertically graded, on transparency."""
    W = size * 4
    grad = Image.new("RGB", (1, W))
    for y in range(W):
        t = y / max(1, W - 1)
        grad.putpixel((0, y), tuple(
            int(TEAL_TOP[i] + (TEAL_BOTTOM[i] - TEAL_TOP[i]) * t) for i in range(3)
        ))
    grad = grad.resize((W, W))

    mask = Image.new("L", (W, W), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        [0, 0, W - 1, W - 1], radius=int(W * 0.22), fill=255)
    img = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    img.paste(grad, (0, 0), mask)
    return img.resize((size, size), Image.LANCZOS)


def compose(size: int, glyph: Image.Image) -> Image.Image:
    """Glyph occupies ~66% of the square, centred."""
    img = background(size)
    side = round(size * 0.66)
    g = glyph.resize((side, side), Image.LANCZOS)
    off = ((size - side) // 2, (size - side) // 2)
    img.alpha_composite(g, off)
    return img


def main() -> None:
    normal = Image.open(ROOT / "reverse/icons/glyph_1024.png").convert("RGBA")
    small = Image.open(ROOT / "reverse/icons/glyph_small_1024.png").convert("RGBA")

    frames = []
    for s in SIZES:
        glyph = small if s < SMALL_BELOW else normal
        buf = io.BytesIO()
        compose(s, glyph).save(buf, format="PNG", optimize=True)
        frames.append((s, buf.getvalue()))

    header = struct.pack("<HHH", 0, 1, len(frames))
    offset = 6 + 16 * len(frames)
    directory = b""
    blob = b""
    for s, png in frames:
        dim = 0 if s >= 256 else s
        directory += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(png), offset)
        offset += len(png)
        blob += png

    OUT.write_bytes(header + directory + blob)
    print(f"{OUT}  {len(header + directory + blob)} bytes  sizes={SIZES}")


if __name__ == "__main__":
    main()
