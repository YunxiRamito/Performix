"""Geometric Performix icon generator: Python + Pillow + numpy, no AI images.

Run: python -B generate-icon.py
All files are saved beside this script. No source image is traced or sampled.
"""
from __future__ import annotations

import io
import math
from pathlib import Path
import struct

import numpy as np
from PIL import Image, ImageChops, ImageDraw, ImageFilter


OUTPUT_DIR = Path(__file__).resolve().parent
APP_SIZES = (1024, 256, 64, 32, 16)
ICO_SIZES = (16, 24, 32, 48, 64, 128, 256)
TOP = (8, 43, 51)
BOTTOM = (7, 12, 30)
ARC_START = 138.0
ARC_SWEEP = 264.0
ARC_STOPS = (
    (0.00, (32, 162, 255)), (0.30, (24, 213, 230)),
    (0.62, (94, 247, 222)), (0.82, (205, 250, 251)),
    (1.00, (228, 240, 255)),
)


def sampling_scale(size: int) -> int:
    """Antialias independent optical masters, never downscale the 1024 icon."""
    return 8 if size <= 32 else 6 if size <= 64 else 3 if size <= 256 else 2


def coordinates(side: int) -> tuple[np.ndarray, np.ndarray]:
    vertical, horizontal = np.mgrid[0:side, 0:side].astype(np.float32)
    return (horizontal + 0.5) / side, (vertical + 0.5) / side


def rgb_image(pixels: np.ndarray) -> Image.Image:
    return Image.fromarray(np.clip(np.rint(pixels), 0, 255).astype(np.uint8))


def rounded_mask(side: int, inset: float, radius: float) -> Image.Image:
    mask = Image.new("L", (side, side))
    ImageDraw.Draw(mask).rounded_rectangle(
        (inset * side, inset * side, (1 - inset) * side - 1,
         (1 - inset) * side - 1), radius=radius * side, fill=255,
    )
    return mask


def background(side: int, size: int) -> tuple[Image.Image, Image.Image]:
    horizontal, vertical = coordinates(side)
    blend = np.clip(vertical * 0.90 + horizontal * 0.10, 0, 1)
    top = np.array(TOP, dtype=np.float32)
    pixels = top + (np.array(BOTTOM, dtype=np.float32) - top) * blend[..., None]
    # Broad lighting replaces the reference's strong diagonal stripes.
    teal_glow = np.exp(-(((horizontal - 0.22) / 0.54) ** 2
                         + ((vertical - 0.20) / 0.52) ** 2) * 2)
    blue_glow = np.exp(-(((horizontal - 0.90) / 0.58) ** 2
                         + ((vertical - 0.88) / 0.52) ** 2) * 2)
    pixels += teal_glow[..., None] * np.array((0, 13, 17), dtype=np.float32)
    pixels += blue_glow[..., None] * np.array((3, 6, 20), dtype=np.float32)
    vignette = np.clip(((horizontal - 0.5) ** 2
                        + (vertical - 0.48) ** 2) * 0.33, 0, 0.19)
    pixels *= 1 - vignette[..., None]
    inset = 0.015 if size <= 32 else 0.025
    silhouette = rounded_mask(side, inset, 0.205)
    tile = rgb_image(pixels).convert("RGBA")
    tile.putalpha(silhouette)
    if size >= 64:
        inner = rounded_mask(side, inset + 0.0018, 0.2032)
        rim_mask = ImageChops.subtract(silhouette, inner)
        rim_pixels = np.zeros((side, side, 4), dtype=np.uint8)
        rim_pixels[..., :3] = (157, 228, 237)
        rim_pixels[..., 3] = np.clip(27 - vertical * 18, 0, 255).astype(np.uint8)
        rim = Image.fromarray(rim_pixels)
        rim.putalpha(ImageChops.multiply(rim.getchannel("A"), rim_mask))
        tile = Image.alpha_composite(tile, rim)
    return tile, silhouette


def dial_mask(side: int, size: int) -> Image.Image:
    mask = Image.new("L", (side, side))
    radius = 0.322 if size <= 24 else 0.326
    stroke = 0.084 if size <= 16 else 0.072 if size <= 32 else 0.060
    outer_radius, inner_radius = radius + stroke / 2, radius - stroke / 2
    angles = np.linspace(ARC_START, ARC_START + ARC_SWEEP, max(160, int(side * 1.2)))
    # Annular polygons produce precise radial flat end caps.
    outer = [((0.5 + outer_radius * math.cos(math.radians(angle))) * side,
              (0.505 + outer_radius * math.sin(math.radians(angle))) * side)
             for angle in angles]
    inner = [((0.5 + inner_radius * math.cos(math.radians(angle))) * side,
              (0.505 + inner_radius * math.sin(math.radians(angle))) * side)
             for angle in reversed(angles)]
    ImageDraw.Draw(mask).polygon(outer + inner, fill=255)
    return mask


def dial_gradient(side: int) -> Image.Image:
    horizontal, vertical = coordinates(side)
    angle = np.degrees(np.arctan2(vertical - 0.505, horizontal - 0.5))
    progress = ((angle - ARC_START) % 360) / ARC_SWEEP
    pixels = np.empty((side, side, 3), dtype=np.float32)
    for channel in range(3):
        pixels[..., channel] = np.interp(
            progress, [stop[0] for stop in ARC_STOPS],
            [stop[1][channel] for stop in ARC_STOPS],
        )
    radial = np.hypot(horizontal - 0.5, vertical - 0.505)
    highlight = np.exp(-((radial - 0.308) / 0.012) ** 2) * 0.09
    pixels += (255 - pixels) * highlight[..., None]
    return rgb_image(pixels).convert("RGBA")


def mark_mask(side: int, small: bool = False) -> Image.Image:
    """Original Ascend mark: one central needle and two detached swept vanes.

    New topology: single apex, continuous center shaft, detached inward/upward
    wings, no eye cutouts, no center slit, and no reference-style twin fangs.
    """
    mask = Image.new("L", (side, side))
    if small:
        # Small optical masters: wider shaft, four-point wings, larger gaps.
        needle = (
            (0.500, 0.310), (0.587, 0.471), (0.545, 0.455),
            (0.545, 0.660), (0.500, 0.707), (0.455, 0.660),
            (0.455, 0.455), (0.413, 0.471),
        )
        left_wing = (
            (0.287, 0.515), (0.400, 0.405),
            (0.400, 0.516), (0.322, 0.594),
        )
    else:
        needle = (
            (0.500, 0.318), (0.579, 0.466), (0.535, 0.450),
            (0.535, 0.661), (0.500, 0.704), (0.465, 0.661),
            (0.465, 0.450), (0.421, 0.466),
        )
        left_wing = (
            (0.285, 0.526), (0.413, 0.402), (0.413, 0.507),
            (0.349, 0.570), (0.349, 0.620),
        )
    right_wing = tuple((1 - horizontal, vertical)
                       for horizontal, vertical in left_wing)
    painter = ImageDraw.Draw(mask)
    for polygon in (needle, left_wing, right_wing):
        painter.polygon([(horizontal * side, vertical * side)
                         for horizontal, vertical in polygon], fill=255)
    return mask


def light_mark(side: int, mask: Image.Image, mono: bool = False) -> Image.Image:
    if mono:
        mark = Image.new("RGBA", (side, side), (255, 255, 255, 255))
    else:
        vertical = np.linspace(0, 1, side, dtype=np.float32)[:, None]
        strength = np.clip((vertical - 0.32) / 0.40, 0, 1)
        top = np.array((255, 255, 255), dtype=np.float32)
        bottom = np.array((211, 235, 249), dtype=np.float32)
        colors = top + (bottom - top) * strength[..., None]
        mark = rgb_image(np.broadcast_to(colors, (side, side, 3))).convert("RGBA")
    mark.putalpha(mask)
    return mark


def render_app(size: int) -> Image.Image:
    side = size * sampling_scale(size)
    tile, silhouette = background(side, size)
    dial = dial_mask(side, size)
    if size >= 64:
        # Small icons deliberately omit glow and shadow to protect clarity.
        halo_mask = dial.filter(ImageFilter.GaussianBlur(side * 0.023))
        halo_mask = halo_mask.point(lambda value: round(value * 0.16))
        halo = Image.new("RGBA", tile.size, (23, 216, 226, 0))
        halo.putalpha(ImageChops.multiply(halo_mask, silhouette))
        tile = Image.alpha_composite(tile, halo)
    arc = dial_gradient(side)
    arc.putalpha(dial)
    tile = Image.alpha_composite(tile, arc)
    mark = mark_mask(side, small=size <= 32)
    if size >= 64:
        shadow_mask = mark.filter(ImageFilter.GaussianBlur(side * 0.009))
        shadow_mask = ImageChops.offset(shadow_mask, 0, round(side * 0.006))
        shadow_mask = shadow_mask.point(lambda value: round(value * 0.38))
        shadow = Image.new("RGBA", tile.size, (0, 5, 15, 0))
        shadow.putalpha(ImageChops.multiply(shadow_mask, silhouette))
        tile = Image.alpha_composite(tile, shadow)
    tile = Image.alpha_composite(tile, light_mark(side, mark, mono=size <= 32))
    return tile.resize((size, size), Image.Resampling.LANCZOS)


def render_standalone_mark(size: int, mono: bool) -> Image.Image:
    side = size * sampling_scale(size)
    source_mask = mark_mask(side)
    cropped = source_mask.crop(source_mask.getbbox())
    scale = round(side * 0.82) / max(cropped.size)
    fitted = cropped.resize((round(cropped.width * scale), round(cropped.height * scale)),
                            Image.Resampling.LANCZOS)
    mask = Image.new("L", (side, side))
    mask.paste(fitted, ((side - fitted.width) // 2, (side - fitted.height) // 2))
    result = light_mark(side, mask, mono=mono).resize((size, size), Image.Resampling.LANCZOS)
    if mono:
        # RGB remains #FFFFFF at every pixel; edge smoothing changes alpha only.
        result = Image.merge("RGBA", (
            Image.new("L", result.size, 255), Image.new("L", result.size, 255),
            Image.new("L", result.size, 255), result.getchannel("A"),
        ))
    return result


def write_ico(path: Path, frames: dict[int, Image.Image]) -> None:
    """PNG-backed ICO with explicit entries; no library-selected resampling."""
    payloads, entries = [], []
    offset = 6 + 16 * len(frames)
    for size, frame in frames.items():
        buffer = io.BytesIO()
        frame.save(buffer, format="PNG", optimize=True)
        payload = buffer.getvalue()
        dimension = 0 if size == 256 else size
        entries.append(struct.pack("<BBBBHHII", dimension, dimension, 0, 0,
                                   1, 32, len(payload), offset))
        payloads.append(payload)
        offset += len(payload)
    path.write_bytes(struct.pack("<HHH", 0, 1, len(frames))
                     + b"".join(entries) + b"".join(payloads))


def validate(frames: dict[int, Image.Image]) -> None:
    raw = (OUTPUT_DIR / "performix.ico").read_bytes()
    reserved, image_type, count = struct.unpack_from("<HHH", raw)
    assert (reserved, image_type, count) == (0, 1, len(ICO_SIZES))
    decoded_sizes = []
    for index, expected_size in enumerate(ICO_SIZES):
        width, height, palette, unused, planes, bits, length, offset = (
            struct.unpack_from("<BBBBHHII", raw, 6 + index * 16)
        )
        dimensions = (width or 256, height or 256)
        assert dimensions == (expected_size, expected_size)
        assert (palette, unused, planes, bits) == (0, 0, 1, 32)
        with Image.open(io.BytesIO(raw[offset:offset + length])) as frame:
            assert frame.mode == "RGBA" and frame.size == dimensions
            assert np.array_equal(np.asarray(frame), np.asarray(frames[expected_size]))
        decoded_sizes.append(dimensions)
    with Image.open(OUTPUT_DIR / "performix.ico") as icon:
        assert icon.ico.sizes() == {(size, size) for size in ICO_SIZES}
        for size in ICO_SIZES:
            decoded = icon.ico.getimage((size, size)).convert("RGBA")
            assert np.array_equal(np.asarray(decoded), np.asarray(frames[size]))
    print(f"ICO verified: {count} frames, 32-bit RGBA, sizes={decoded_sizes}")
    pngs = {"performix-app.png": (1024, 1024)}
    pngs.update({f"performix-app-{size}.png": (size, size)
                 for size in APP_SIZES if size != 1024})
    pngs.update({"performix-mark.png": (512, 512),
                 "performix-mark-mono.png": (256, 256),
                 "preview-16px-x8.png": (128, 128),
                 "preview-32px-x8.png": (256, 256)})
    for filename, dimensions in pngs.items():
        with Image.open(OUTPUT_DIR / filename) as image:
            assert image.mode == "RGBA" and image.size == dimensions, filename
            alpha = np.asarray(image.getchannel("A"))
            assert alpha.min() == 0 and alpha.max() == 255, filename
            assert all(image.getpixel(position)[3] == 0 for position in (
                (0, 0), (image.width - 1, 0), (0, image.height - 1),
                (image.width - 1, image.height - 1),
            )), filename
            if filename == "performix-mark-mono.png":
                assert np.all(np.asarray(image)[..., :3] == 255), filename
    for size in (16, 32):
        with Image.open(OUTPUT_DIR / f"preview-{size}px-x8.png") as preview:
            expected = frames[size].resize((size * 8, size * 8), Image.Resampling.NEAREST)
            assert np.array_equal(np.asarray(preview), np.asarray(expected))
    print("PNG verified: sizes, RGBA alpha, transparent corners, pure-white mono.")
    print("16/32 previews exactly match ICO frames at 8x nearest-neighbor scale.")


def main() -> None:
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    frames = {size: render_app(size) for size in sorted(set(APP_SIZES + ICO_SIZES))}
    for size in APP_SIZES:
        filename = "performix-app.png" if size == 1024 else f"performix-app-{size}.png"
        frames[size].save(OUTPUT_DIR / filename, optimize=True)
    write_ico(OUTPUT_DIR / "performix.ico", {size: frames[size] for size in ICO_SIZES})
    render_standalone_mark(512, mono=False).save(OUTPUT_DIR / "performix-mark.png", optimize=True)
    render_standalone_mark(256, mono=True).save(OUTPUT_DIR / "performix-mark-mono.png", optimize=True)
    for size in (16, 32):
        frames[size].resize((size * 8, size * 8), Image.Resampling.NEAREST).save(
            OUTPUT_DIR / f"preview-{size}px-x8.png", optimize=True,
        )
    validate(frames)
    print("\nDeliverables (bytes):")
    filenames = ["generate-icon.py", "performix-app.png"]
    filenames += [f"performix-app-{size}.png" for size in APP_SIZES if size != 1024]
    filenames += ["performix.ico", "performix-mark.png", "performix-mark-mono.png",
                  "preview-16px-x8.png", "preview-32px-x8.png"]
    for filename in filenames:
        print(f"{filename:28s} {(OUTPUT_DIR / filename).stat().st_size:>10,d}")


if __name__ == "__main__":
    main()

