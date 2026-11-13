import hashlib
import io
import json
import struct
import sys
from pathlib import Path

from PIL import Image, ImageFilter


PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"
PNG_SIZES = (512, 256, 64, 32, 16)
ICO_SIZES = (16, 24, 32, 48, 64, 128, 256)
SHARPENING = {"radius": 0.45, "percent": 65, "threshold": 2}
ICON_HEADER = struct.Struct("<HHH")
ICON_ENTRY = struct.Struct("<BBBBHHII")


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def alpha_info(image):
    with image.convert("RGBA") as rgba:
        with rgba.getchannel("A") as alpha:
            histogram = alpha.histogram()
    populated = [index for index, count in enumerate(histogram) if count]
    return {
        "has_alpha_channel": "A" in image.getbands(),
        "has_transparency_metadata": "transparency" in image.info,
        "minimum": min(populated),
        "maximum": max(populated),
        "transparent_pixels": histogram[0],
        "partial_alpha_pixels": sum(histogram[1:255]),
        "opaque_pixels": histogram[255],
    }


def image_info(image, data):
    return {
        "bytes": len(data),
        "sha256": sha256(data),
        "size": list(image.size),
        "mode": image.mode,
        "alpha": alpha_info(image),
    }


def decode_png(data, expected_size):
    require(data.startswith(PNG_SIGNATURE), "Missing PNG signature")
    with Image.open(io.BytesIO(data)) as probe:
        require(probe.format == "PNG", "Image format must be PNG")
        require(probe.size == expected_size, "Unexpected PNG dimensions")
        require(not getattr(probe, "is_animated", False), "Animated PNG is not supported")
        probe.verify()
    with Image.open(io.BytesIO(data)) as decoded:
        decoded.load()
        return decoded.copy()


def resize_from_source(source, size):
    resized = source.resize((size, size), Image.Resampling.LANCZOS)
    if size in (16, 32):
        with resized.convert("RGB") as rgb:
            sharpened = rgb.filter(ImageFilter.UnsharpMask(**SHARPENING))
        with resized.getchannel("A") as alpha:
            sharpened.putalpha(alpha)
        resized.close()
        return sharpened
    return resized


def encode_png(image):
    buffer = io.BytesIO()
    image.save(buffer, format="PNG")
    return buffer.getvalue()


def write_output(path, data, source_path):
    require(path.parent == source_path.parent, "Output must stay in the ai directory")
    require(not path.is_symlink(), "Refusing to write through an output symlink")
    require(path != source_path, "Refusing to overwrite the source")
    if path.exists():
        require(not path.samefile(source_path), "Output aliases the source file")
        require(path.stat().st_nlink == 1, "Refusing to overwrite a hard-linked output")
    path.write_bytes(data)


def save_and_validate_png(path, image, source_path):
    expected_data = encode_png(image)
    write_output(path, expected_data, source_path)
    actual_data = path.read_bytes()
    require(actual_data == expected_data, "Written PNG differs from encoded PNG")
    with decode_png(actual_data, image.size) as decoded:
        require(decoded.mode == "RGBA", "Output PNG must be RGBA")
        require(decoded.tobytes() == image.tobytes(), "PNG pixel round-trip failed")
        return {"path": str(path), "validated": True, **image_info(decoded, actual_data)}


def encode_ico(frames):
    offset = ICON_HEADER.size + ICON_ENTRY.size * len(frames)
    entries = []
    for size, payload in frames:
        dimension = 0 if size == 256 else size
        entries.append(ICON_ENTRY.pack(dimension, dimension, 0, 0, 1, 32, len(payload), offset))
        offset += len(payload)
    return (
        ICON_HEADER.pack(0, 1, len(frames))
        + b"".join(entries)
        + b"".join(payload for size, payload in frames)
    )


def validate_ico(path, expected_frames):
    data = path.read_bytes()
    require(len(data) >= ICON_HEADER.size, "Truncated ICO header")
    reserved, icon_type, frame_count = ICON_HEADER.unpack_from(data)
    require((reserved, icon_type, frame_count) == (0, 1, 7), "Invalid ICO header or frame count")
    require(tuple(size for size, payload in expected_frames) == ICO_SIZES, "Unexpected ICO sizes")
    offset = ICON_HEADER.size + ICON_ENTRY.size * frame_count
    require(len(data) >= offset, "Truncated ICO directory")
    reports = []
    for index, (expected_size, expected_payload) in enumerate(expected_frames):
        entry = ICON_ENTRY.unpack_from(data, ICON_HEADER.size + index * ICON_ENTRY.size)
        width, height, colors, entry_reserved, planes, bit_count, byte_count, image_offset = entry
        expected_dimension = 0 if expected_size == 256 else expected_size
        require((width, height) == (expected_dimension, expected_dimension), "Invalid ICO dimensions")
        require((colors, entry_reserved, planes, bit_count) == (0, 0, 1, 32), "Invalid ICO entry")
        require(image_offset == offset, "ICO payload offsets must be contiguous")
        require(byte_count == len(expected_payload), "Invalid ICO payload length")
        require(image_offset + byte_count <= len(data), "ICO payload exceeds file bounds")
        payload = data[image_offset:image_offset + byte_count]
        require(payload == expected_payload, "ICO PNG payload was altered")
        with decode_png(payload, (expected_size, expected_size)) as decoded:
            require(decoded.mode == "RGBA", "ICO frame must be RGBA")
            reports.append({
                "index": index,
                "directory_width": width,
                "directory_height": height,
                "offset": image_offset,
                "planes": planes,
                "bit_count": bit_count,
                "png_payload_unchanged": True,
                "validated": True,
                **image_info(decoded, payload),
            })
        offset += byte_count
    require(offset == len(data), "Unexpected trailing ICO data")
    with Image.open(path) as icon:
        require(icon.format == "ICO", "Pillow did not recognize ICO format")
        require(icon.ico.sizes() == {(size, size) for size in ICO_SIZES}, "Pillow ICO size mismatch")
        for size, payload in expected_frames:
            with icon.ico.getimage((size, size)) as frame:
                frame.load()
                require(frame.size == (size, size), "Pillow decoded an incorrect ICO size")
                with decode_png(payload, (size, size)) as expected:
                    with frame.convert("RGBA") as actual:
                        require(actual.tobytes() == expected.tobytes(), "Pillow ICO pixel mismatch")
    return {
        "path": str(path),
        "bytes": len(data),
        "sha256": sha256(data),
        "frame_count": frame_count,
        "validated": True,
        "frames": reports,
    }


def generate_assets(directory, source_path, source_bytes):
    with decode_png(source_bytes, (1024, 1024)) as original:
        source_report = {"path": str(source_path), **image_info(original, source_bytes)}
        source = original.convert("RGBA")
    report = {
        "validated": False,
        "source": source_report,
        "processing": {
            "resize": "Every PNG and ICO frame is resized directly from the 1024x1024 source",
            "resampling": "LANCZOS",
            "unsharp_mask": {"sizes": [16, 32], "channels": "RGB only; alpha unchanged", **SHARPENING},
            "ico_encoding": "Individually encoded PNG payloads in ICONDIR/ICONDIRENTRY",
            "preview_8x_resampling": "NEAREST",
            "size_preview": "512, 256, 64, 32, 16 left-to-right; native size; 16px gaps and margins",
        },
        "pngs": {},
    }
    resized_images = {}
    try:
        for size in PNG_SIZES:
            resized_images[size] = resize_from_source(source, size)
            filename = "performix-ai-{}.png".format(size)
            report["pngs"][filename] = save_and_validate_png(
                directory / filename, resized_images[size], source_path
            )
        frames = []
        for size in ICO_SIZES:
            with resize_from_source(source, size) as frame:
                frames.append((size, encode_png(frame)))
        ico_path = directory / "performix-ai.ico"
        write_output(ico_path, encode_ico(frames), source_path)
        report["ico"] = validate_ico(ico_path, frames)
        filename = "performix-ai-16-preview-8x.png"
        with resized_images[16].resize((128, 128), Image.Resampling.NEAREST) as preview:
            report["pngs"][filename] = save_and_validate_png(directory / filename, preview, source_path)
        margin = 16
        preview_size = (sum(PNG_SIZES) + margin * (len(PNG_SIZES) + 1), max(PNG_SIZES) + 2 * margin)
        with Image.new("RGBA", preview_size, (0, 0, 0, 0)) as preview:
            horizontal = margin
            for size in PNG_SIZES:
                preview.paste(resized_images[size], (horizontal, (preview.height - size) // 2))
                horizontal += size + margin
            filename = "performix-ai-size-preview.png"
            report["pngs"][filename] = save_and_validate_png(directory / filename, preview, source_path)
    finally:
        source.close()
        for resized in resized_images.values():
            resized.close()
    return report


def main():
    directory = Path(__file__).resolve().parent
    source_path = directory / "performix-ai.png"
    source_bytes = source_path.read_bytes()
    original_hash = sha256(source_bytes)
    try:
        report = generate_assets(directory, source_path, source_bytes)
    finally:
        current_bytes = source_path.read_bytes()
        require(sha256(current_bytes) == original_hash, "Source SHA-256 changed during processing")
        require(current_bytes == source_bytes, "Source bytes changed during processing")
    report["source"]["sha256_after"] = sha256(current_bytes)
    report["source"]["hash_unchanged"] = True
    report["source"]["validated"] = True
    report["validated"] = True
    report_path = directory / "ai-icon-validation.json"
    report["validation_report_path"] = str(report_path)
    serialized = json.dumps(report, ensure_ascii=True, indent=2) + "\n"
    write_output(report_path, serialized.encode("ascii"), source_path)
    require(report_path.read_bytes() == serialized.encode("ascii"), "Validation report write failed")
    sys.stdout.write(serialized)


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        sys.stdout.write(json.dumps({
            "validated": False,
            "error_type": type(error).__name__,
            "error": str(error),
        }, ensure_ascii=True) + "\n")
        sys.exit(1)
