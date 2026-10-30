#!/usr/bin/env python3
"""Create two original PCasa background JPG textures (deterministic, no source reuse).
Requires numpy and Pillow; invoked only by the Windows artifact workflow.
"""
import math
import pathlib
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

W, H = 1400, 850
rng = np.random.default_rng(17061)


def field(octaves, seed):
    local = np.random.default_rng(seed)
    result = np.zeros((H, W), dtype=np.float32)
    total = 0.0
    for scale, weight in octaves:
        w, h = max(3, W // scale), max(3, H // scale)
        noise = local.integers(0, 256, (h, w), dtype=np.uint8)
        layer = Image.fromarray(noise, "L").resize((W, H), Image.Resampling.BICUBIC)
        result += np.asarray(layer, dtype=np.float32) * weight
        total += weight
    return result / total / 255.0 - 0.5


def cracks(seed, count, length, darkness=105):
    local = np.random.default_rng(seed)
    paper = Image.new("L", (W, H), 0)
    d = ImageDraw.Draw(paper)
    for _ in range(count):
        x, y = int(local.integers(0, W)), int(local.integers(0, H))
        theta = float(local.uniform(-math.pi, math.pi))
        points = [(x, y)]
        for segment in range(int(local.integers(6, 15))):
            theta += float(local.normal(0, 0.22))
            distance = float(local.uniform(0.5, length))
            x += int(distance * math.cos(theta))
            y += int(distance * math.sin(theta))
            points.append((x, y))
        d.line(points, fill=int(local.integers(darkness // 2, darkness)), width=int(local.integers(1, 3)), joint="curve")
    return np.asarray(paper.filter(ImageFilter.GaussianBlur(0.4)), dtype=np.float32) / 255.0


def save_texture(folder, blue):
    fine = field([(100, .5), (31, .6), (9, .45), (3, .27), (1, .09)], 724 if blue else 136)
    broad = field([(280, 1.3), (95, .7), (30, .3)], 999 if blue else 456)
    medium = field([(55, .5), (15, .8), (4, .35)], 55 if blue else 32)
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    if blue:
        weather = (np.sin(xx * .012 + yy * .025 + broad * 9) * .037 +
                   np.cos(yy * .033 - xx * .007 + medium * 6) * .023)
        variation = fine * .70 + broad * .58 + weather
        base = np.stack((14 + variation * 49, 39 + variation * 75, 57 + variation * 91), axis=-1)
        aging = cracks(1001, 110, 3, 64)
        base -= aging[:, :, None] * np.array([26, 36, 46])
        grit = (medium > .10).astype(np.float32) * (.08 + np.maximum(0, fine))
        base -= grit[:, :, None] * np.array([10, 16, 21])
    else:
        slab = np.sin(xx * .016 + yy * .042 + broad * 21)
        split = np.maximum(0, medium * 3.1 + slab * .28)
        erosion = np.maximum(0, -medium * 2.2 + np.sin(xx * .017 - yy * .061 + fine * 6) * .24)
        variation = fine * .85 + broad * .60 + split * .40 - erosion * .45
        base = np.stack((34 + variation * 90, 37 + variation * 93, 40 + variation * 98), axis=-1)
        cracks_map = cracks(1967, 155, 4, 100)
        base -= cracks_map[:, :, None] * np.array([52, 53, 55])
        edge_highlights = np.maximum(0, np.gradient(split, axis=1)) * 3
        base += edge_highlights[:, :, None] * 18
    grain = np.random.default_rng(989 if blue else 822).normal(0, 3.6 if blue else 4.4, (H,W,1))
    arr = np.uint8(np.clip(base + grain, 0, 255))
    name = "Stein - Nachtblau.jpg" if blue else "Stein - Schiefer dunkel.jpg"
    Image.fromarray(arr, "RGB").save(folder/name, "JPEG", quality=91, subsampling=0, optimize=True)
    print(f"Generated {folder/name}")


if __name__ == "__main__":
    folder = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else ".")
    folder.mkdir(parents=True, exist_ok=True)
    save_texture(folder, False)
    save_texture(folder, True)
