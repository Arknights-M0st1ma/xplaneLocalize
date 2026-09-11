"""Regenerate windows-bridge/app.ico from the same geometry as public/assets/icon.svg.

The web icon is a 512x512 rounded square (#08131c), a teal aircraft silhouette
(#55d6be) and an amber hub (#f4b860). Drawing it here keeps the exe icon sharp at
every size (supersampled then downscaled) and removes the need for an SVG
rasteriser on the build machine.

Usage:  python windows-bridge/tools/make-icon.py
"""

import struct
from pathlib import Path

from PIL import Image, ImageDraw

SIZES = [16, 24, 32, 48, 64, 128, 256]
SUPERSAMPLE = 1024

BACKGROUND = (8, 19, 28, 255)      # #08131c
AIRCRAFT = (85, 214, 190, 255)     # #55d6be
HUB = (244, 184, 96, 255)          # #f4b860
OUTLINE = (8, 19, 28, 255)

# public/assets/icon.svg path (512x512 viewBox): M256 58 294 215 443 278 431 315 286 290
# 270 437 242 437 226 290 81 315 69 278 218 215Z
AIRCRAFT_POINTS = [
    (256, 58), (294, 215), (443, 278), (431, 315), (286, 290),
    (270, 437), (242, 437), (226, 290), (81, 315), (69, 278), (218, 215),
]


def render(size: int) -> Image.Image:
    scale = SUPERSAMPLE / 512
    canvas = Image.new("RGBA", (SUPERSAMPLE, SUPERSAMPLE), (0, 0, 0, 0))
    draw = ImageDraw.Draw(canvas)
    draw.rounded_rectangle(
        [(0, 0), (SUPERSAMPLE - 1, SUPERSAMPLE - 1)],
        radius=int(112 * scale),
        fill=BACKGROUND,
    )
    draw.polygon([(x * scale, y * scale) for x, y in AIRCRAFT_POINTS], fill=AIRCRAFT)
    hub = 45 * scale
    ring = 18 * scale
    cx = cy = 256 * scale
    draw.ellipse([cx - hub, cy - hub, cx + hub, cy + hub], fill=HUB, outline=OUTLINE, width=int(ring))
    return canvas.resize((size, size), Image.LANCZOS)


def write_ico(path: Path, images: list[Image.Image]) -> None:
    """Write an ICO whose frames are PNGs (supported since Windows Vista)."""
    import io

    blobs = []
    for image in images:
        buffer = io.BytesIO()
        image.save(buffer, format="PNG")
        blobs.append(buffer.getvalue())

    header = struct.pack("<HHH", 0, 1, len(blobs))
    offset = len(header) + 16 * len(blobs)
    entries = b""
    for image, blob in zip(images, blobs):
        side = 0 if image.width >= 256 else image.width
        entries += struct.pack(
            "<BBBBHHII",
            side, side, 0, 0, 1, 32, len(blob), offset,
        )
        offset += len(blob)
    path.write_bytes(header + entries + b"".join(blobs))


def main() -> None:
    root = Path(__file__).resolve().parents[1]
    images = [render(size) for size in SIZES]
    write_ico(root / "app.ico", images)
    images[-1].save(root / "tools" / "icon-256-preview.png")
    print(f"wrote {root / 'app.ico'} with sizes {SIZES}")


if __name__ == "__main__":
    main()
