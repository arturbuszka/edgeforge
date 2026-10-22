#!/usr/bin/env python3
"""Synthetic egg line: a belt carrying eggs of known size, for the line scenario.

Writes samples/eggs.mp4 and samples/eggs.truth.csv. The truth file is what the
station's verdicts get compared against: the size each egg was drawn at.

Needs numpy and ffmpeg on PATH. Deterministic: the same seed gives the same video.

    python3 tools/make_egg_sample.py
    EDGEFORGE_PROFILE=eggs dotnet run --project src/EdgeForge.Demo -- samples/eggs.mp4

Geometry matches profiles/eggs.json: 960 px across 320 mm of belt, so
Blob:MmPerPixel = 1/3.
"""

import argparse
import csv
import subprocess
from pathlib import Path

import numpy as np

WIDTH, HEIGHT, FPS = 960, 400, 25
MM_PER_PX = 1.0 / 3.0
BELT_SPEED_MM_S = 120.0
SPACING_MM = 100.0
UNDERSIZE_BELOW_MM = 42.0  # the profile's Line:Limits width_mm Min


def egg_mask(xx, yy, cx, cy, length_px, width_px, angle_deg, taper=0.12):
    """Egg outline: an ellipse whose half-width shrinks toward one end."""
    a = np.deg2rad(angle_deg)
    dx, dy = xx - cx, yy - cy
    u = dx * np.cos(a) + dy * np.sin(a)        # along the long axis
    v = -dx * np.sin(a) + dy * np.cos(a)       # across it
    half_l, half_w = length_px / 2.0, width_px / 2.0
    t = np.clip(u / half_l, -1.0, 1.0)
    local_w = half_w * (1.0 + taper * t)       # blunt end wider, pointed end narrower
    inside = (u / half_l) ** 2 + (v / np.maximum(local_w, 1e-6)) ** 2
    return inside, u / half_l, v / half_w


def render_egg(frame, cx, cy, length_mm, width_mm, angle, colour):
    length_px, width_px = length_mm / MM_PER_PX, width_mm / MM_PER_PX
    r = int(length_px / 2 + 4)
    x0, x1 = max(int(cx - r), 0), min(int(cx + r), WIDTH)
    y0, y1 = max(int(cy - r), 0), min(int(cy + r), HEIGHT)
    if x0 >= x1 or y0 >= y1:
        return

    yy, xx = np.mgrid[y0:y1, x0:x1].astype(np.float32)
    inside, nu, nv = egg_mask(xx, yy, cx, cy, length_px, width_px, angle)

    # Soft one-pixel edge, then shading: lit from the top left, darker at the rim.
    alpha = np.clip((1.0 - inside) * length_px / 4.0, 0.0, 1.0)[..., None]
    rim = np.clip(1.0 - inside, 0.0, 1.0) ** 0.35
    light = 0.62 + 0.38 * rim - 0.10 * nv - 0.05 * nu
    shade = np.clip(light, 0.35, 1.08)[..., None]

    egg = np.asarray(colour, np.float32)[None, None, :] * shade
    patch = frame[y0:y1, x0:x1]
    frame[y0:y1, x0:x1] = patch * (1 - alpha) + egg * alpha


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--eggs", type=int, default=30)
    parser.add_argument("--seed", type=int, default=7)
    parser.add_argument("--out", default="samples/eggs.mp4")
    args = parser.parse_args()

    rng = np.random.default_rng(args.seed)

    # Mostly M/L eggs, about one in five small. Width is the short axis.
    eggs = []
    for i in range(args.eggs):
        small = rng.random() < 0.2
        width = rng.uniform(37.5, 41.0) if small else rng.uniform(42.8, 46.5)
        length = width * rng.uniform(1.27, 1.36)
        angle = rng.uniform(-25, 25)
        brown = rng.random() < 0.6
        colour = (rng.uniform(95, 120), rng.uniform(140, 165), rng.uniform(190, 215)) if brown \
            else (rng.uniform(215, 232), rng.uniform(222, 238), rng.uniform(228, 242))  # BGR
        lane_y = HEIGHT / 2 + rng.uniform(-35, 35)
        eggs.append(dict(index=i + 1, length=length, width=width, angle=angle, colour=colour, y=lane_y))

    px_per_frame = BELT_SPEED_MM_S / MM_PER_PX / FPS
    spacing_px = SPACING_MM / MM_PER_PX
    first_x = -120.0
    total_frames = int((WIDTH + 240 + spacing_px * (len(eggs) - 1)) / px_per_frame)

    # Belt: dark rubber with a fixed weave that travels with the eggs.
    weave_len = WIDTH + int(px_per_frame * total_frames) + 8
    weave = (rng.normal(0, 4.0, (HEIGHT, weave_len)) +
             3.0 * np.sin(np.arange(weave_len) / 3.0)[None, :]).astype(np.float32)
    vignette = (1.0 - 0.18 * ((np.linspace(-1, 1, WIDTH)[None, :] ** 2) +
                              (np.linspace(-1, 1, HEIGHT)[:, None] ** 2))).astype(np.float32)

    out = Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    ffmpeg = subprocess.Popen(
        ["ffmpeg", "-y", "-loglevel", "error",
         "-f", "rawvideo", "-pix_fmt", "bgr24", "-s", f"{WIDTH}x{HEIGHT}", "-r", str(FPS), "-i", "-",
         "-c:v", "libx264", "-preset", "slow", "-crf", "24", "-pix_fmt", "yuv420p", str(out)],
        stdin=subprocess.PIPE)

    for f in range(total_frames):
        shift = int(f * px_per_frame)
        belt = 42.0 + weave[:, weave_len - WIDTH - shift - 1: weave_len - shift - 1]
        frame = np.repeat(belt[..., None], 3, axis=2) * np.array([1.0, 0.97, 0.93], np.float32)

        for k, egg in enumerate(eggs):
            cx = first_x + f * px_per_frame - k * spacing_px
            if -150 < cx < WIDTH + 150:
                render_egg(frame, cx, egg["y"], egg["length"], egg["width"], egg["angle"], egg["colour"])

        frame = frame * vignette[..., None] + rng.normal(0, 2.0, frame.shape).astype(np.float32)
        ffmpeg.stdin.write(np.clip(frame, 0, 255).astype(np.uint8).tobytes())

    ffmpeg.stdin.close()
    ffmpeg.wait()

    truth = out.with_suffix(".truth.csv")
    with truth.open("w", newline="") as fh:
        w = csv.writer(fh)
        w.writerow(["egg", "length_mm", "width_mm", "expected"])
        for egg in eggs:
            w.writerow([egg["index"], f"{egg['length']:.1f}", f"{egg['width']:.1f}",
                        "NOK Undersize" if egg["width"] < UNDERSIZE_BELOW_MM else "OK"])

    small = sum(e["width"] < UNDERSIZE_BELOW_MM for e in eggs)
    print(f"{out}: {total_frames} frames, {total_frames / FPS:.1f} s, "
          f"{len(eggs)} eggs, {small} undersize -> {truth}")


if __name__ == "__main__":
    main()
