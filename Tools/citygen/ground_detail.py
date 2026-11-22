"""Make the ground's detail texture: a small, seamlessly tiling grey grain (sand / tarmac texture) that the
ground shader multiplies over the map-rendered ground colours close to the viewer.

    Tools/citygen/.venv/Scripts/python.exe Tools/citygen/ground_detail.py

Writes Assets/City/Textures/ground_detail.png (256 x 256, mid-grey = no change).
"""
from pathlib import Path

import numpy as np
from PIL import Image

SIZE = 256
rng = np.random.default_rng(7)


def tiling_noise(cells: int) -> np.ndarray:
    """Smooth value noise that repeats exactly: a random grid of `cells` x `cells`, cosine-interpolated."""
    grid = rng.random((cells, cells))
    t = np.arange(SIZE) * cells / SIZE
    i0 = np.floor(t).astype(int) % cells
    i1 = (i0 + 1) % cells
    f = t - np.floor(t)
    f = (1 - np.cos(f * np.pi)) / 2
    rows = grid[i0][:, i0] * (1 - f)[None, :] + grid[i0][:, i1] * f[None, :]
    rows1 = grid[i1][:, i0] * (1 - f)[None, :] + grid[i1][:, i1] * f[None, :]
    return rows * (1 - f)[:, None] + rows1 * f[:, None]


# broad blotches, medium grain and fine speckle; centred on 0.5 so the average brightness is unchanged
image = 0.5 + 0.20 * (tiling_noise(4) - 0.5) + 0.22 * (tiling_noise(16) - 0.5) + 0.20 * (tiling_noise(64) - 0.5) \
        + 0.10 * (rng.random((SIZE, SIZE)) - 0.5)
image = np.clip(image, 0, 1)
out = Path(__file__).resolve().parents[2] / "Assets" / "City" / "Textures" / "ground_detail.png"
Image.fromarray((image * 255).astype(np.uint8), "L").save(out)
print(f"{out}: mean {image.mean():.3f}, range {image.min():.2f}..{image.max():.2f}")
