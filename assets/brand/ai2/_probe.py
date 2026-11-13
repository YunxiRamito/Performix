import importlib.util
import sys
from pathlib import Path
import numpy as np

_spec = importlib.util.spec_from_file_location("gen", Path(__file__).resolve().parent / "generate-round.py")
G = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(G)

for f in sys.argv[1:]:
    p = Path(f)
    info = G.ring_points(p)
    cx, cy = info["center"]
    xs = info["ring_x"]; ys = info["ring_y"]; rr = info["ring_r"]
    r_all = np.hypot(xs - cx, ys - cy)
    print("=" * 70)
    print(p.name, "center", round(cx, 1), round(cy, 1), "radius_mid", round(info["radius_mid"], 1))

    # radial histogram -> ring band
    hi = np.floor(r_all).astype(int)
    hist = np.bincount(hi, minlength=1400)[:1400].astype(float)
    from numpy.lib.stride_tricks import sliding_window_view
    sm = np.convolve(hist, np.ones(7) / 7, mode="same")
    search = (np.arange(1400) > 0.55 * info["radius_mid"]) & (np.arange(1400) < 1.45 * info["radius_mid"])
    peak = int(np.argmax(np.where(search, sm, 0)))
    lev = 0.15 * sm[peak]
    lo = peak
    while lo > 0 and sm[lo] > lev:
        lo -= 1
    hi_r = peak
    while hi_r < 1399 and sm[hi_r] > lev:
        hi_r += 1
    print(f"ring peak r={peak} band=[{lo},{hi_r}] width={hi_r-lo}")
    # restrict to ring band and measure symmetric bbox
    band = (r_all > lo - 2) & (r_all < hi_r + 2)
    bx = np.abs(xs[band] - cx)
    by = np.abs(ys[band] - cy)
    w, h = 2 * bx.max(), 2 * by.max()
    print(f"ring-band symmetric bbox w={w:.1f} h={h:.1f} err={abs(w-h)/max(w,h)*100:.3f}%")
    # per-degree outer edge & ellipse fit
    ang = np.degrees(np.arctan2(ys - cy, xs - cx)) % 360
    prof = {}
    for b in range(360):
        d = np.abs(((ang - b + 180) % 360) - 180)
        sel = d <= 1.5
        if np.count_nonzero(sel) > 15:
            prof[b] = np.percentile(rr[sel], 98)
    degs = np.array(sorted(prof))
    vals = np.array([prof[d] for d in degs])
    th = np.radians(degs)
    A = np.column_stack([np.ones_like(th), np.cos(2 * th), np.sin(2 * th)])
    coef, res, *_ = np.linalg.lstsq(A, vals, rcond=None)
    R0, A2, B2 = coef
    print(f"ellipse fit: R0={R0:.1f} A2={A2:.2f} B2={B2:.2f} a/b={1+np.hypot(A2,B2):.4f} "
          f"axis_ratio={R0+np.hypot(A2,B2):.1f}/{(R0-np.hypot(A2,B2)):.1f} "
          f"= {(R0+np.hypot(A2,B2))/(R0-np.hypot(A2,B2)):.4f}")
    print(f"coverage bins={len(degs)} radial range={vals.min():.1f}..{vals.max():.1f}")
    # extents at cardinal directions
    for name, d0 in (("right", 0), ("bottom", 90), ("left", 180), ("top", 270)):
        near = [prof[d] for d in degs if np.abs(((d - d0 + 180) % 360) - 180) <= 6]
        print(f"  {name:6s} r_edge={np.mean(near) if near else float('nan'):.1f} (n={len(near)})")
