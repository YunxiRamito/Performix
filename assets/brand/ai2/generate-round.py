#!/usr/bin/env python3
"""Performix app icon, round version.

Pipeline
--------
1. generate : call the image API (gpt-image-2.6-sunburst-special, 1024x1024),
              keep every attempt as candidates/NN-...png, measure each one.
2. measure  : circular gauge detector. Fits the gauge centre from the bright ink,
              then measures the *symmetric* ink bounding box of the ring about
              that centre. The gauge is open at the bottom, so a plain bbox would
              always be wider than tall; mirroring the ring extent about the true
              centre is what makes the test meaningful (a true circle -> w == h,
              a squashed ellipse -> w > h).
3. postprocess: pick one candidate, round the corners (22%, transparent),
              export 1024/512/256/64/32/16 PNG, multi-frame ICO, 8x previews.

Usage (from any cwd; paths default to this script's folder)
-----------------------------------------------------------
    $env:OMYAI_API_KEY = "sk-..."      # never stored in the script or logs
    python generate-round.py generate --max-attempts 6
    python generate-round.py postprocess --pick 3
    python generate-round.py measure <png> [png...]

Outputs live next to this script:
    candidates/NN-errX.XXpct.png     every attempt + its measured roundness
    candidates/measurements.json     full measurement record
    performix-round.png             1024 rounded-corner RGBA
    performix-round-512/256/64/32/16.png
    performix-round.ico             16/24/32/48/64/128/256, each from 1024
    preview-16px-x8.png, preview-32px-x8.png
    run-log.txt                     generation + validation log
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import io
import json
import math
import os
import re
import sys
import time
from pathlib import Path

import numpy as np
import requests
from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parent
CANDIDATES = ROOT / "candidates"
API_URL = "https://omyai.vip/v1/images/generations"
MODEL = "gpt-image-2.6-sunburst-special"
API_KEY_ENV = "OMYAI_API_KEY"
CANVAS = 1024
MAX_ERR = 0.03  # |w-h| / max(w,h)

PROMPT = (
    "Create a professional Windows app icon for Performix, a performance-tuning utility. "
    "Square 1024x1024, one opaque full-bleed PNG, no border, no padding, straight-on centred, "
    "all artwork inside the central 78 percent for later rounded-corner cropping. "
    "The single hero symbol is a perfectly circular gauge, like a ring drawn with a compass: "
    "the arc must be a true circle, equal width and height, NOT an ellipse, not squashed, "
    "no perspective, seen dead-on. "
    "The ring is nearly complete: about 270 degrees of thick even stroke, with one narrow opening "
    "facing straight down, so the whole gauge silhouette reads as a round dial. "
    "The ring is segmented into 4 equal-length arc pieces separated by four small clean gaps, "
    "like a zone meter. Rounded ends on every segment. "
    "no tick marks, no numbers, no text, no letters. "
    "Inside the ring, one needle: the needle is a rounded capsule with two rounded ends, no sharp tip, "
    "no point, no taper, no triangular arrowhead. The needle is white with a soft cool glow, pivoting "
    "from the centre and pointing up and to the right toward 1:30, staying well inside the ring. "
    "At the very centre at most one small rounded dot as pivot, nothing else. "
    "Modern Fluent Design: minimal, few broad solid geometric forms, generous dark negative space, "
    "crisp vector edges, high contrast, subtle layered depth, faint inner glow on the ring. "
    "Background: calm smooth near-black midnight blue to deep teal gradient. "
    "Ring: smooth gradient from bright cyan through turquoise to pale ice blue. "
    "Flat vector icon look, instantly readable at 16 and 32 pixels. "
    "No wing, no crest, no logo monogram, no gear, no circuit lines, no 3D bevel, no mockup, "
    "no watermark, no neon bloom, no perspective, no frame."
)

_LOG = []


def log(msg: str) -> None:
    line = f"[{time.strftime('%Y-%m-%d %H:%M:%S')}] {msg}"
    print(line, flush=True)
    _LOG.append(line)


def write_log() -> None:
    (ROOT / "run-log.txt").write_text("\n".join(_LOG) + "\n", encoding="utf-8")


# --------------------------------------------------------------------------- #
# image analysis
# --------------------------------------------------------------------------- #
def load_rgb(path: Path) -> Image.Image:
    im = Image.open(path)
    if im.mode in ("RGBA", "LA", "P"):
        im = im.convert("RGBA")
        bg = Image.new("RGBA", im.size, (6, 10, 18, 255))
        im = Image.alpha_composite(bg, im).convert("RGB")
    else:
        im = im.convert("RGB")
    return im


def otsu(values: np.ndarray) -> float:
    hist, edges = np.histogram(values, bins=256, range=(0.0, 255.0))
    hist = hist.astype(np.float64)
    total = hist.sum()
    if total == 0:
        return 128.0
    omega = np.cumsum(hist) / total
    mu = np.cumsum(hist * (edges[:-1] + 0.5)) / total
    mu_t = mu[-1]
    denom = omega * (1.0 - omega)
    denom[denom == 0] = 1e-9
    sigma = (mu_t * omega - mu) ** 2 / denom
    idx = int(np.argmax(sigma))
    return float(edges[idx] + 0.5)


def ring_points(path: Path):
    """Return (rgb, ink mask, intensity) plus fitted centre/radii for the gauge ring."""
    rgb = load_rgb(path)
    arr = np.asarray(rgb, dtype=np.float64)
    lum = 0.299 * arr[:, :, 0] + 0.587 * arr[:, :, 1] + 0.114 * arr[:, :, 2]
    t_otsu = otsu(lum.ravel())
    threshold = max(t_otsu, 110.0)  # ring is far brighter than the dark background
    mask = lum > threshold
    ys, xs = np.nonzero(mask)
    if xs.size < 500:
        raise RuntimeError(f"not enough bright ink: {xs.size}px (threshold {threshold:.0f})")
    cx = (xs.min() + xs.max()) / 2.0
    cy = (ys.min() + ys.max()) / 2.0
    pts_x = xs.astype(np.float64)
    pts_y = ys.astype(np.float64)
    radius = float("nan")
    for _ in range(40):
        r = np.hypot(pts_x - cx, pts_y - cy)
        med = float(np.median(r))
        keep = np.abs(r - med) < 0.20 * med
        x = pts_x[keep]
        y = pts_y[keep]
        if x.size < 200:
            raise RuntimeError("ring fit collapsed")
        A = np.column_stack([2.0 * x, 2.0 * y, np.ones(x.size)])
        b = x * x + y * y
        sol, *_ = np.linalg.lstsq(A, b, rcond=None)
        ncx, ncy = float(sol[0]), float(sol[1])
        radius = float(np.median(np.hypot(x - ncx, y - ncy)))
        if abs(ncx - cx) < 0.02 and abs(ncy - cy) < 0.02:
            cx, cy = ncx, ncy
            break
        cx, cy = ncx, ncy
    r_all = np.hypot(pts_x - cx, pts_y - cy)
    # isolate the ring band: peak of the radial ink histogram near the fitted radius
    hist = np.bincount(np.floor(r_all).astype(int), minlength=CANVAS + 8).astype(float)
    hist = np.convolve(hist, np.ones(7) / 7.0, mode="same")
    rr = np.arange(hist.size)
    window = (rr > 0.55 * radius) & (rr < 1.45 * radius)
    peak = int(np.argmax(np.where(window, hist, 0)))
    level = 0.15 * hist[peak]
    lo, hi = peak, peak
    while lo > 0 and hist[lo] > level:
        lo -= 1
    while hi < hist.size - 1 and hist[hi] > level:
        hi += 1
    band = (r_all > lo - 2) & (r_all < hi + 2)
    return {
        "rgb": rgb,
        "lum": lum,
        "mask": mask,
        "threshold": threshold,
        "center": (cx, cy),
        "radius_mid": radius,
        "ring_band": (float(lo), float(hi)),
        "ring_x": pts_x[band],
        "ring_y": pts_y[band],
        "ring_r": r_all[band],
        "ink_px": int(xs.size),
    }


def _outer_edge_profile(px: np.ndarray, py: np.ndarray, cx: float, cy: float):
    """Outer ring radius per degree bin; returns (dense interpolated radii, known mask)."""
    theta = np.degrees(np.arctan2(py - cy, px - cx)) % 360.0
    r = np.hypot(px - cx, py - cy)
    prof = np.full(360, np.nan)
    for b in range(360):
        d = np.abs(((theta - b + 180.0) % 360.0) - 180.0)
        sel = d <= 1.5
        if np.count_nonzero(sel) > 12:
            prof[b] = float(np.percentile(r[sel], 98))
    known = np.isfinite(prof)
    if known.sum() < 30:
        raise RuntimeError("ring coverage too low for an edge profile")
    idx = np.arange(360)
    dense = np.interp(idx, idx[known], prof[known], period=360)
    return dense, known


def _cardinal(prof: np.ndarray, deg: float, half: float = 6.0) -> float:
    d = np.abs(((np.arange(360) - deg + 180.0) % 360.0) - 180.0)
    return float(np.mean(prof[d <= half]))


def circularity(path: Path) -> dict:
    info = ring_points(path)
    cx, cy = info["center"]
    px, py = info["ring_x"], info["ring_y"]
    prof, known = _outer_edge_profile(px, py, cx, cy)

    r_right = _cardinal(prof, 0.0)
    r_left = _cardinal(prof, 180.0)
    r_top = _cardinal(prof, 270.0)
    r_bottom = _cardinal(prof, 90.0)
    bottom_bins = np.abs(((np.arange(360) - 90.0 + 180.0) % 360.0) - 180.0) <= 15.0
    has_bottom = bool(np.count_nonzero(bottom_bins & known) >= 6)
    w = r_right + r_left
    h = (r_top + r_bottom) if has_bottom else 2.0 * r_top
    err = abs(w - h) / max(w, h)

    # independent ellipse check: r(theta) = R0 + A cos2t + B sin2t + C cos t + D sin t
    th = np.radians(np.arange(360, dtype=float))
    A = np.column_stack([np.ones(360), np.cos(2 * th), np.sin(2 * th), np.cos(th), np.sin(th)])
    coef, *_ = np.linalg.lstsq(A, prof, rcond=None)
    R0, A2, B2 = float(coef[0]), float(coef[1]), float(coef[2])
    rho = math.hypot(A2, B2)
    axis_ratio = (R0 + rho) / (R0 - rho) if R0 > rho else float("inf")

    # segment / gap analysis: 1-degree occupancy of the ring band
    ys, xs = np.nonzero(info["mask"])
    rr = np.hypot(xs - cx, ys - cy)
    lo, hi = info["ring_band"]
    sel = (rr >= lo) & (rr <= hi)
    ang = np.degrees(np.arctan2(ys[sel] - cy, xs[sel] - cx)) % 360.0
    occ = np.zeros(360, dtype=bool)
    occ[np.unique(ang.astype(int) % 360)] = True
    closed = occ.copy()
    for i in range(360):
        if not occ[i] and occ[(i - 1) % 360] and occ[(i + 1) % 360]:
            closed[i] = True
    occ = closed
    ext = np.concatenate([occ, occ, occ])
    diff = np.diff(ext.astype(np.int8))
    starts = np.nonzero(diff == 1)[0] + 1
    ends = np.nonzero(diff == -1)[0] + 1
    segs = int(np.count_nonzero((starts >= 360) & (starts < 720)))
    gaps_n = int(np.count_nonzero((ends >= 360) & (ends < 720)))
    gap_widths = []
    for e in ends[(ends >= 360) & (ends < 720)]:
        nxt = starts[starts > e]
        if nxt.size:
            gap_widths.append(int(nxt[0] - e))

    # plain whole-ink bbox, for reference (an open-bottom arc is always wider than tall)
    ys_all, xs_all = np.nonzero(info["mask"])
    plain_w = float(xs_all.max() - xs_all.min() + 1)
    plain_h = float(ys_all.max() - ys_all.min() + 1)

    return {
        "file": path.name,
        "path": str(path),
        "threshold": round(info["threshold"], 1),
        "center": [round(cx, 1), round(cy, 1)],
        "radius_outer": round(r_top, 1),
        "ring_band": [round(lo, 1), round(hi, 1)],
        "w": round(w, 1),
        "h": round(h, 1),
        "bbox_w": round(w, 1),
        "bbox_h": round(h, 1),
        "roundness_error": round(err, 5),
        "roundness_error_pct": round(err * 100, 3),
        "axis_ratio_w_over_h": round(w / h, 4) if h else float("inf"),
        "ellipse_axis_ratio": round(axis_ratio, 4),
        "ellipse_err_pct": round((axis_ratio - 1.0) * 100, 3),
        "cardinal_r": {"right": round(r_right, 1), "bottom": round(r_bottom, 1),
                       "left": round(r_left, 1), "top": round(r_top, 1)},
        "has_bottom_ink": bool(has_bottom),
        "segments": segs,
        "gaps": gaps_n,
        "gap_widths_deg": gap_widths,
        "plain_ink_bbox": [round(plain_w, 1), round(plain_h, 1)],
        "ink_px": info["ink_px"],
        "pass": bool(err <= MAX_ERR),
    }


# --------------------------------------------------------------------------- #
# generation
# --------------------------------------------------------------------------- #
def api_key() -> str:
    key = os.environ.get(API_KEY_ENV, "").strip()
    if not key:
        sys.exit(f"missing {API_KEY_ENV} environment variable (key is deliberately not stored in this script)")
    return key


def _fetch_image(data: dict, key: str) -> tuple[Image.Image, str]:
    item = data["data"][0]
    if item.get("b64_json"):
        raw = base64.b64decode(item["b64_json"])
        return Image.open(io.BytesIO(raw)).convert("RGB"), "b64_json"
    if item.get("url"):
        r = requests.get(item["url"], timeout=(15, 180))
        r.raise_for_status()
        return Image.open(io.BytesIO(r.content)).convert("RGB"), "url"
    raise RuntimeError(f"response has neither b64_json nor url: {list(item)}")


def generate_one(key: str, attempt: int, http_tries: int = 4, model: str = MODEL):
    headers = {"Authorization": f"Bearer {key}", "Content-Type": "application/json"}
    payload = {"model": model, "prompt": PROMPT, "size": f"{CANVAS}x{CANVAS}", "n": 1}
    for t in range(1, http_tries + 1):
        t0 = time.time()
        try:
            r = requests.post(API_URL, headers=headers, json=payload, timeout=(20, 300))
        except Exception as exc:  # network / TLS
            log(f"attempt {attempt} POST try {t}: network error {exc!r}; retrying")
            time.sleep(6)
            continue
        dt = time.time() - t0
        log(f"attempt {attempt} POST try {t}: HTTP {r.status_code} in {dt:.1f}s, {len(r.content)} bytes")
        if r.status_code == 200:
            try:
                data = r.json()
                img, source = _fetch_image(data, key)
            except Exception as exc:
                log(f"attempt {attempt}: HTTP 200 but payload unusable: {exc!r}")
                time.sleep(6)
                continue
            if img.size != (CANVAS, CANVAS):
                log(f"attempt {attempt}: returned {img.size}, resizing to {CANVAS}")
                img = img.resize((CANVAS, CANVAS), Image.LANCZOS)
            return img, {
                "attempt": attempt,
                "model": model,
                "http_tries": t,
                "source": source,
                "elapsed_s": round(dt, 2),
                "response_keys": sorted(data.keys()),
            }
        body = re.sub(r"sk-[A-Za-z0-9]+", "sk-***", r.text)[:500].replace("\n", " ")
        log(f"attempt {attempt} POST try {t}: HTTP {r.status_code}; body: {body}")
        if r.status_code in (429, 500, 502, 503, 504):
            time.sleep(8)
            continue
        time.sleep(6)
    raise RuntimeError(f"attempt {attempt}: all {http_tries} HTTP tries failed")


def cmd_generate(args) -> None:
    key = api_key()
    CANDIDATES.mkdir(parents=True, exist_ok=True)
    store = load_measurements()
    results = store.get("attempts", [])
    nums = [r["attempt"] for r in results]
    for p in CANDIDATES.glob("[0-9][0-9]-err*.png"):
        m = re.match(r"(\d+)-err", p.name)
        if m:
            nums.append(int(m.group(1)))
    next_n = max(nums, default=0) + 1
    for i in range(args.max_attempts):
        n = next_n + i
        model = args.model or MODEL
        log(f"=== generation attempt {n} using {model} {CANVAS}x{CANVAS} (prompt {len(PROMPT)} chars) ===")
        img, meta = generate_one(key, n, http_tries=args.http_tries, model=model)
        raw = CANDIDATES / f"{n:02d}.png"
        img.save(raw, format="PNG")
        log(f"attempt {n}: saved {raw.name} sha256={hashlib.sha256(raw.read_bytes()).hexdigest()[:16]}")
        m = circularity(raw)
        m.update(meta)
        final = CANDIDATES / f"{n:02d}-err{m['roundness_error_pct']:.2f}pct.png"
        raw.rename(final)
        m["file"] = final.name
        m["path"] = str(final)
        m.update({"sha256": hashlib.sha256(final.read_bytes()).hexdigest()})
        log(
            f"attempt {n}: w={m['bbox_w']} h={m['bbox_h']} err={m['roundness_error_pct']}% "
            f"ratio={m['axis_ratio_w_over_h']} radial_cv={m['radial_cv']} segments={m['segments']} "
            f"gaps={m['gaps']} -> {'PASS' if m['pass'] else 'FAIL'}"
        )
        results.append(m)
        store["attempts"] = results
        save_measurements(store)
        if any(r["pass"] for r in results) and len([r for r in results if r.get("sha256")]) >= 2:
            log("at least two candidates generated and at least one passes roundness; stopping")
            break
        if m["pass"] and args.stop_on_first_pass:
            break
    write_log()


# --------------------------------------------------------------------------- #
# measurements store
# --------------------------------------------------------------------------- #
def load_measurements() -> dict:
    p = CANDIDATES / "measurements.json"
    if p.exists():
        try:
            return json.loads(p.read_text(encoding="utf-8"))
        except json.JSONDecodeError:
            pass
    return {"attempts": [], "selected": None}


def save_measurements(data: dict) -> None:
    (CANDIDATES / "measurements.json").write_text(json.dumps(data, indent=2), encoding="utf-8")


# --------------------------------------------------------------------------- #
# post processing
# --------------------------------------------------------------------------- #
def rounded_mask(size: int, radius_ratio: float, supersample: int = 4) -> Image.Image:
    big = size * supersample
    m = Image.new("L", (big, big), 0)
    r = int(round(radius_ratio * big))
    ImageDraw.Draw(m).rounded_rectangle([0, 0, big - 1, big - 1], radius=r, fill=255)
    return m.resize((size, size), Image.LANCZOS)


def cmd_postprocess(args) -> None:
    ms = load_measurements()
    attempts = ms.get("attempts", [])
    if not attempts:
        sys.exit("no candidates recorded; run generate first")
    pool = [a for a in attempts if a["pass"]] or attempts
    if args.pick is not None:
        chosen = next(a for a in attempts if a["attempt"] == args.pick)
    else:
        chosen = max(pool, key=lambda a: -(a["roundness_error"]))
    src = Path(chosen["path"])
    log(f"postprocess: source {src.name} (attempt {chosen['attempt']}, err {chosen['roundness_error_pct']}%)")
    base = load_rgb(src)
    if base.size != (CANVAS, CANVAS):
        base = base.resize((CANVAS, CANVAS), Image.LANCZOS)
    out = base.convert("RGBA")
    mask = rounded_mask(CANVAS, 0.22)
    out.putalpha(mask)
    out.save(ROOT / "performix-round.png")
    sizes = [512, 256, 64, 32, 16]
    for s in sizes:
        out.resize((s, s), Image.LANCZOS).save(ROOT / f"performix-round-{s}.png")
    ico_sizes = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
    out.save(ROOT / "performix-round.ico", format="ICO", sizes=ico_sizes)
    for s in (16, 32):
        small = out.resize((s, s), Image.LANCZOS)
        small.resize((s * 8, s * 8), Image.NEAREST).save(ROOT / f"preview-{s}px-x8.png")
    ms["selected"] = {
        "attempt": chosen["attempt"],
        "file": chosen["file"],
        "roundness_error_pct": chosen["roundness_error_pct"],
        "bbox_w": chosen["bbox_w"],
        "bbox_h": chosen["bbox_h"],
        "source_sha256": chosen["sha256"],
        "corner_radius_ratio": 0.22,
        "verification": verify_outputs(out),
    }
    save_measurements(ms)
    log("postprocess done")
    write_log()


def verify_outputs(base: Image.Image) -> dict:
    out = {}
    rnd = Image.open(ROOT / "performix-round.png")
    out["performix-round.png"] = {"size": rnd.size, "mode": rnd.mode, "corner_alpha": int(rnd.getpixel((0, 0))[3])}
    for s in (512, 256, 64, 32, 16):
        p = ROOT / f"performix-round-{s}.png"
        im = Image.open(p)
        out[p.name] = {"size": im.size, "mode": im.mode, "corner_alpha": int(im.getpixel((0, 0))[3])}
    ico = Image.open(ROOT / "performix-round.ico")
    frames = ico.info.get("sizes", [])
    out["performix-round.ico"] = {"frames": sorted(frames), "frame_count": len(frames)}
    for s in (16, 32):
        p = ROOT / f"preview-{s}px-x8.png"
        im = Image.open(p)
        out[p.name] = {"size": im.size}
    return out


def cmd_measure(args) -> None:
    results = []
    for f in args.files:
        p = Path(f)
        m = circularity(p)
        results.append(m)
        log(f"{p.name}: w={m['bbox_w']} h={m['bbox_h']} err={m['roundness_error_pct']}% segments={m['segments']}")
    print(json.dumps(results, indent=2))
    write_log()


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd")
    g = sub.add_parser("generate")
    g.add_argument("--max-attempts", type=int, default=6)
    g.add_argument("--http-tries", type=int, default=4)
    g.add_argument("--model", default=None)
    g.add_argument("--stop-on-first-pass", action="store_true", default=False)
    g.set_defaults(func=cmd_generate)
    p = sub.add_parser("postprocess")
    p.add_argument("--pick", type=int, default=None)
    p.set_defaults(func=cmd_postprocess)
    m = sub.add_parser("measure")
    m.add_argument("files", nargs="+")
    m.set_defaults(func=cmd_measure)
    args = ap.parse_args()
    if not args.cmd:
        ap.print_help()
        return
    args.func(args)


if __name__ == "__main__":
    main()
