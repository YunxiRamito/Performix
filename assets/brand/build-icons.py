"""Performix icon build: one command rebuilds every shipped icon from one master bitmap.

Design source (the only image this script reads):
  assets/brand/gauge-26b/performix-icon-4k.png
    4096x4096 RGBA on a transparent canvas. A blue superellipse plate carrying a white
    speedometer: a three-quarter arc open at the bottom plus one blunt needle.
  The retired gauge-only.svg was a vector approximation traced from this bitmap; it is
  no longer a source for anything here.

Products (all overwritten):
  assets/brand/performix-app.png            1024 px app icon (plate + gauge)
  assets/brand/performix-app-{16,32,64,256}.png
  assets/brand/performix.ico                16/24/32/48/64/128/256
  assets/brand/performix-mark.png           64 px title-bar mark: gauge glyph only,
                                            no plate, recoloured #3B82F6
  assets/brand/performix-mark-mono.png      64 px single-colour white glyph
  assets/brand/preview-{16,32}px-x8.png     small frames blown up 8x, nearest neighbour
  assets/brand/icon-sizes-preview.png       16/32/48/64 at 4x, side by side
  src/Performix.App/Assets/appicon.ico      same bytes as assets/brand/performix.ico
  src/Performix.App/Assets/performix-mark.png
  src/Performix.Legacy.Wpf/appicon.ico      copy for the WPF fallback build

Rules encoded here
  * Every size is resampled from the 4096 master on its own, never chained down from a
    1024 render, so no size inherits another size's rounding error.
  * Resampling runs in premultiplied alpha. The master is transparent black outside the
    plate and resizing straight RGBA would drag a dark fringe along that edge.
  * The mark has no plate: a solid squircle at 16 px turns into a blob and swallows the
    glyph, so the white strokes are lifted as a matte and recoloured instead.
  * The 16 and 24 px frames get a mild unsharp pass; below that the arc otherwise
    dissolves into the gradient behind it.

Run: python -B build-icons.py [--verify]
"""
from __future__ import annotations

import struct
import sys
from pathlib import Path

import numpy as np
from PIL import Image

BRAND_DIR = Path(__file__).resolve().parent
REPO_DIR = BRAND_DIR.parents[1]
MASTER = BRAND_DIR / "gauge-26b" / "performix-icon-4k.png"

APP_ICO_SIZES = (16, 24, 32, 48, 64, 128, 256)
APP_PNG_SIZES = (1024, 256, 64, 32, 16)
ACCENT = (0x3B, 0x82, 0xF6)          # #3B82F6, the accent the app already uses
MARK_SIZE = 64
MARK_MARGIN = 0.08                   # ~8% clear on every side; at 16 px it still reads
SHARPEN_AT_OR_BELOW = 24             # unsharp mask for the two smallest frames
# Luminance window separating the white speedometer from the blue plate. The brightest
# plate pixels sit near luma 150, the strokes at 250, and the ramp lands in the empty
# valley between them so antialiased stroke edges keep their partial coverage.
GLYPH_LUMA_LO = 168.0
GLYPH_LUMA_HI = 236.0
GLYPH_CROP_FLOOR = 0.10              # ignore the faintest halo when measuring the box

APP_ICO_TARGETS = (
    BRAND_DIR / "performix.ico",
    REPO_DIR / "src" / "Performix.App" / "Assets" / "appicon.ico",
    REPO_DIR / "src" / "Performix.Legacy.Wpf" / "appicon.ico",
)
MARK_TARGETS = (
    BRAND_DIR / "performix-mark.png",
    REPO_DIR / "src" / "Performix.App" / "Assets" / "performix-mark.png",
)
# The mono glyph is the one the title bar actually paints: the shell tints it at runtime with the
# user's accent (src/Performix.App/Shell/AccentGlyphBrush.cs), so it ships inside the app next to
# the flat blue mark rather than only in the brand folder.
MARK_MONO_TARGETS = (
    BRAND_DIR / "performix-mark-mono.png",
    REPO_DIR / "src" / "Performix.App" / "Assets" / "performix-mark-mono.png",
)


# --------------------------------------------------------------------------- resampling
def _box_pool(channel: np.ndarray, factor: int) -> np.ndarray:
    if factor < 2:
        return channel
    height, width = channel.shape
    return channel.reshape(height // factor, factor, width // factor, factor).mean(axis=(1, 3))


def _resize_float(channel: np.ndarray, width: int, height: int) -> np.ndarray:
    """Box-reduce in whole steps to ~4x the target, then Lanczos for the last step."""
    factor = 1
    while (channel.shape[0] % (factor * 2) == 0 and channel.shape[1] % (factor * 2) == 0
           and channel.shape[0] // (factor * 2) >= height * 4
           and channel.shape[1] // (factor * 2) >= width * 4):
        factor *= 2
    pooled = _box_pool(channel, factor)
    out = Image.fromarray(pooled.astype(np.float32), mode="F").resize(
        (width, height), Image.LANCZOS)
    return np.asarray(out, dtype=np.float32)


def _blur(plane: np.ndarray, sigma: float) -> np.ndarray:
    """Separable Gaussian on a float plane (Pillow's filters reject mode 'F')."""
    radius = max(1, int(round(sigma * 3)))
    offsets = np.arange(-radius, radius + 1, dtype=np.float32)
    kernel = np.exp(-(offsets ** 2) / (2.0 * sigma * sigma))
    kernel /= kernel.sum()
    for axis in (0, 1):
        pad = [(radius, radius) if index == axis else (0, 0) for index in range(2)]
        padded = np.pad(plane, pad, mode="edge")
        acc = np.zeros_like(plane)
        for index, weight in enumerate(kernel):
            if axis == 0:
                acc += weight * padded[index:index + plane.shape[0], :]
            else:
                acc += weight * padded[:, index:index + plane.shape[1]]
        plane = acc
    return plane


def _unsharp(plane: np.ndarray, amount: float = 0.6, sigma: float = 0.55) -> np.ndarray:
    return plane + amount * (plane - _blur(plane, sigma))


def render_app(size: int) -> Image.Image:
    """One icon size, resampled straight from the master in premultiplied alpha."""
    pixels = np.asarray(MASTER_IMAGE, dtype=np.float32)
    alpha = pixels[..., 3] / 255.0
    planes = [pixels[..., channel] * alpha for channel in range(3)] + [alpha]
    planes = [_resize_float(plane, size, size) for plane in planes]
    if size <= SHARPEN_AT_OR_BELOW:
        planes = [_unsharp(plane) for plane in planes]

    out_alpha = np.clip(planes[3], 0.0, 1.0)
    safe = np.where(out_alpha > 1e-4, out_alpha, 1.0)
    rgb = [np.clip(planes[channel] / safe, 0.0, 255.0) for channel in range(3)]
    out = np.stack(rgb + [out_alpha * 255.0], axis=2)
    return Image.fromarray(np.rint(out).astype(np.uint8), mode="RGBA")


# ----------------------------------------------------------------------------- the mark
def glyph_matte() -> np.ndarray:
    """White strokes as a 0..1 coverage matte, blue plate dropped."""
    pixels = np.asarray(MASTER_IMAGE, dtype=np.float32)
    luma = 0.299 * pixels[..., 0] + 0.587 * pixels[..., 1] + 0.114 * pixels[..., 2]
    ramp = np.clip((luma - GLYPH_LUMA_LO) / (GLYPH_LUMA_HI - GLYPH_LUMA_LO), 0.0, 1.0)
    ramp = ramp * ramp * (3.0 - 2.0 * ramp)
    return ramp * (pixels[..., 3] / 255.0)


def render_mark(size: int = MARK_SIZE, colour: tuple[int, int, int] = ACCENT) -> Image.Image:
    matte = GLYPH_MATTE
    rows = np.nonzero((matte > GLYPH_CROP_FLOOR).any(axis=1))[0]
    cols = np.nonzero((matte > GLYPH_CROP_FLOOR).any(axis=0))[0]
    glyph = matte[rows[0]:rows[-1] + 1, cols[0]:cols[-1] + 1]

    inner = size - 2 * round(size * MARK_MARGIN)
    height, width = glyph.shape
    scale = inner / max(height, width)
    target_w = max(1, round(width * scale))
    target_h = max(1, round(height * scale))
    glyph = np.clip(_resize_float(glyph, target_w, target_h), 0.0, 1.0)

    canvas = np.zeros((size, size), dtype=np.float32)
    top = (size - target_h) // 2
    left = (size - target_w) // 2
    canvas[top:top + target_h, left:left + target_w] = glyph

    rgba = np.zeros((size, size, 4), dtype=np.uint8)
    rgba[..., 0], rgba[..., 1], rgba[..., 2] = colour
    rgba[..., 3] = np.rint(canvas * 255.0).astype(np.uint8)
    return Image.fromarray(rgba, mode="RGBA")


# ----------------------------------------------------------------------------------- ico
def _dib_frame(image: Image.Image) -> bytes:
    """32 bpp bottom-up DIB + 1 bpp AND mask, the layout every Windows shell decodes."""
    pixels = np.asarray(image.convert("RGBA"))
    height, width = pixels.shape[:2]
    header = struct.pack("<IiiHHIIiiII", 40, width, height * 2, 1, 32, 0, 0, 0, 0, 0, 0)

    row_bytes = ((width + 31) // 32) * 4
    rows = []
    for y in range(height - 1, -1, -1):
        packed = np.packbits(pixels[y, :, 3] < 8, bitorder="big").tobytes()
        rows.append(packed + b"\x00" * (row_bytes - len(packed)))
    return header + pixels[::-1, :, [2, 1, 0, 3]].tobytes() + b"".join(rows)


def write_ico(path: Path, frames: list[tuple[int, Image.Image]]) -> None:
    header = struct.pack("<HHH", 0, 1, len(frames))
    offset = 6 + 16 * len(frames)
    table, blobs = [], []
    for size, image in frames:
        blob = _dib_frame(image)
        table.append(struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32,
                                 len(blob), offset))
        blobs.append(blob)
        offset += len(blob)
    path.write_bytes(header + b"".join(table) + b"".join(blobs))


# -------------------------------------------------------------------------------- build
def build() -> None:
    written: list[str] = []
    app_icon = render_app(1024)
    app_icon.save(BRAND_DIR / "performix-app.png", optimize=True)
    written.append("performix-app.png")

    for size in APP_PNG_SIZES:
        if size == 1024:
            continue
        render_app(size).save(BRAND_DIR / f"performix-app-{size}.png", optimize=True)
        written.append(f"performix-app-{size}.png")

    frames = [(size, render_app(size)) for size in APP_ICO_SIZES]
    for target in APP_ICO_TARGETS:
        write_ico(target, frames)
        written.append(str(target.relative_to(REPO_DIR)))

    mark = render_mark()
    for target in MARK_TARGETS:
        mark.save(target, optimize=True)
        written.append(str(target.relative_to(REPO_DIR)))

    mono = render_mark(MARK_SIZE, (255, 255, 255))
    for target in MARK_MONO_TARGETS:
        mono.save(target, optimize=True)
        written.append(str(target.relative_to(REPO_DIR)))
    for size in (16, 32):
        frame = next(image for edge, image in frames if edge == size)
        frame.resize((size * 8, size * 8), Image.NEAREST).save(
            BRAND_DIR / f"preview-{size}px-x8.png", optimize=True)
        written.append(f"preview-{size}px-x8.png")

    scale = 4
    strip = Image.new("RGBA", (sum(s * scale for s in (16, 32, 48, 64)), 64 * scale),
                      (0, 0, 0, 0))
    left = 0
    for size in (16, 32, 48, 64):
        frame = next(image for edge, image in frames if edge == size)
        strip.alpha_composite(frame.resize((size * scale, size * scale), Image.NEAREST),
                              (left, 0))
        left += size * scale
    strip.save(BRAND_DIR / "icon-sizes-preview.png", optimize=True)
    written.append("icon-sizes-preview.png")

    for name in written:
        print("wrote", name)


# ------------------------------------------------------------------------------- verify
def _coverage(image: Image.Image) -> float:
    alpha = np.asarray(image.convert("RGBA"))[..., 3]
    return float((alpha > 128).mean())


def verify() -> int:
    problems: list[str] = []
    print("== ico ==")
    for path in APP_ICO_TARGETS:
        with Image.open(path) as ico:
            sizes = sorted(ico.info.get("sizes", set()))
            print(f"{path.relative_to(REPO_DIR)}: {len(sizes)} frames "
                  f"{[s[0] for s in sizes]} bytes={path.stat().st_size}")
            for size in sizes:
                ico.size = size
                ico.load()
                coverage = _coverage(ico.convert("RGBA"))
                print(f"    {size[0]:>3}px non-transparent={coverage * 100:5.1f}%")
                if coverage <= 0.02:
                    problems.append(f"{path.name} {size[0]}px empty")
                if size[0] == 16 and not 0.15 <= coverage <= 0.85:
                    problems.append(f"{path.name} 16px coverage {coverage:.3f}")

    print("== mark ==")
    for path in MARK_TARGETS + (BRAND_DIR / "performix-mark-mono.png",):
        with Image.open(path) as mark:
            mark = mark.convert("RGBA")
            small = mark.resize((16, 16), Image.LANCZOS)
            rgba = np.asarray(mark)
            solid = rgba[rgba[..., 3] > 200]
            colour = None if not len(solid) else solid[:, :3].mean(axis=0).round(1)
            print(f"{path.relative_to(REPO_DIR)}: {mark.size} bbox={mark.getbbox()} "
                  f"non-transparent@64={_coverage(mark) * 100:.1f}% "
                  f"@16={_coverage(small) * 100:.1f}% rgb={colour}")
            if _coverage(small) <= 0.02:
                problems.append(f"{path.name} blank at 16px")

    if problems:
        print("FAIL:", *problems, sep="\n  ")
        return 1
    print("verification: ok")
    return 0


MASTER_IMAGE = Image.open(MASTER).convert("RGBA")
GLYPH_MATTE = glyph_matte()

if __name__ == "__main__":
    build()
    if "--verify" in sys.argv[1:]:
        raise SystemExit(verify())
