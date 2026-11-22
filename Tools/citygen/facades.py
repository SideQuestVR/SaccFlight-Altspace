"""Stage 2b: paint the facade texture array used by every building.

Eight invented cladding / window styles, each a seamlessly tiling 512 px square covering
8 bays x 8 floors. RGB is a neutral-toned facade that the building's vertex colour tints;
alpha marks glass so the shader can add sky reflection there.

Output: Assets/City/Generated/Facades/facades_array.png (4 x 2 grid, imported as a Texture2DArray)
        out/facade_<i>.png (single layers for the Blender preview material)
"""
from __future__ import annotations

import numpy as np
from PIL import Image, ImageDraw

import config as C

PX = C.FACADE_PX
CELLS = C.FACADE_CELLS
CELL = PX // CELLS
SS = 4  # supersample


def canvas(wall):
    img = Image.new("RGB", (PX * SS, PX * SS), wall)
    mask = Image.new("L", (PX * SS, PX * SS), 0)
    return img, ImageDraw.Draw(img), mask, ImageDraw.Draw(mask)


def glass_shade(rng, base=(138, 164, 188), spread=24):
    """Slightly different glass per window so facades do not look stamped; a few are lit."""
    c = np.array(base, float) + spread * rng.uniform(-1, 1)
    if rng.random() < 0.10:
        c = c * 0.55 + np.array([250, 226, 170]) * 0.45
    return tuple(int(v) for v in c.clip(0, 255))


def rect(d, dm, x0, y0, x1, y1, colour, glass=0):
    box = [x0 * SS, y0 * SS, x1 * SS - 1, y1 * SS - 1]
    d.rectangle(box, fill=colour)
    dm.rectangle(box, fill=glass)


def finish(img, mask, rng, grain=5.0):
    img = img.resize((PX, PX), Image.LANCZOS)
    mask = mask.resize((PX, PX), Image.LANCZOS)
    arr = np.asarray(img, np.float32)
    arr = arr + rng.normal(0, grain, (PX, PX, 1))
    streak_src = Image.fromarray((rng.random((1, PX // 4)) * 255).astype(np.uint8))
    streak = np.asarray(streak_src.resize((PX, 1), Image.BICUBIC), np.float32) / 255.0   # faint vertical weathering
    arr = arr * (0.97 + 0.06 * streak).reshape(1, PX, 1)
    out = Image.fromarray(arr.clip(0, 255).astype(np.uint8)).convert("RGBA")
    out.putalpha(mask)
    return out


def style_curtain(rng):          # 0: all-glass curtain wall with a fine mullion grid
    img, d, mask, dm = canvas((176, 184, 192))
    for by in range(CELLS):
        for bx in range(CELLS):
            x, y = bx * CELL, by * CELL
            rect(d, dm, x + 2, y + 2, x + CELL - 2, y + CELL - 9, glass_shade(rng, (132, 166, 196)), 255)
            rect(d, dm, x + 2, y + CELL - 9, x + CELL - 2, y + CELL - 2, (150, 166, 182), 150)   # spandrel glass
    return finish(img, mask, rng, 3.0)


def style_punched(rng):          # 1: solid wall with regular punched windows
    img, d, mask, dm = canvas((214, 208, 198))
    for by in range(CELLS):
        for bx in range(CELLS):
            x, y = bx * CELL, by * CELL
            rect(d, dm, x + 16, y + 12, x + CELL - 16, y + CELL - 16, (150, 146, 140))            # reveal
            rect(d, dm, x + 18, y + 14, x + CELL - 18, y + CELL - 18, glass_shade(rng), 255)
        rect(d, dm, 0, by * CELL + CELL - 3, PX, by * CELL + CELL, (190, 184, 174))                 # floor line
    return finish(img, mask, rng)


def style_ribbon(rng):           # 2: continuous window bands between solid spandrels
    img, d, mask, dm = canvas((222, 220, 214))
    for by in range(CELLS):
        y = by * CELL
        for bx in range(CELLS):
            x = bx * CELL
            rect(d, dm, x + 1, y + 10, x + CELL - 1, y + 38, glass_shade(rng, (126, 152, 178), 18), 255)
        rect(d, dm, 0, y + 38, PX, y + 41, (176, 174, 168))
    return finish(img, mask, rng)


def style_fins(rng):             # 3: tall glazing strips between vertical piers
    img, d, mask, dm = canvas((196, 190, 180))
    for bx in range(CELLS):
        x = bx * CELL
        for by in range(CELLS):
            y = by * CELL
            rect(d, dm, x + 12, y, x + CELL - 12, y + CELL - 6, glass_shade(rng, (134, 158, 178)), 255)
            rect(d, dm, x + 12, y + CELL - 6, x + CELL - 12, y + CELL, (128, 126, 122), 60)
        rect(d, dm, x, 0, x + 6, PX, (226, 222, 214))                                               # projecting fin
        rect(d, dm, x + CELL - 6, 0, x + CELL, PX, (206, 202, 194))
    return finish(img, mask, rng)


def style_balcony(rng):          # 4: residential - windows, doors and balcony slabs
    img, d, mask, dm = canvas((226, 216, 198))
    for by in range(CELLS):
        y = by * CELL
        for bx in range(CELLS):
            x = bx * CELL
            if bx % 2 == 0:
                rect(d, dm, x + 10, y + 10, x + CELL - 10, y + CELL - 12, glass_shade(rng, (130, 150, 168)), 255)
            else:
                rect(d, dm, x + 20, y + 16, x + CELL - 20, y + CELL - 22, glass_shade(rng, (130, 150, 168)), 255)
        for bx in range(0, CELLS, 2):
            x = bx * CELL
            rect(d, dm, x + 4, y + CELL - 12, x + CELL - 4, y + CELL - 6, (238, 236, 230))          # balcony front
            rect(d, dm, x + 4, y + CELL - 6, x + CELL - 4, y + CELL - 2, (120, 114, 104))           # slab shadow
    return finish(img, mask, rng)


def style_megaframe(rng):        # 5: bold frame every two bays and two floors, glass infill
    img, d, mask, dm = canvas((150, 168, 186))
    for by in range(CELLS):
        for bx in range(CELLS):
            x, y = bx * CELL, by * CELL
            rect(d, dm, x + 1, y + 1, x + CELL - 1, y + CELL - 1, glass_shade(rng, (130, 162, 194), 20), 255)
    frame = (232, 230, 224)
    for i in range(0, CELLS, 2):                       # frame members are split across the tile seam
        for lo, hi in ((i * CELL, i * CELL + 6), (i * CELL + 2 * CELL - 6, i * CELL + 2 * CELL)):
            rect(d, dm, lo, 0, hi, PX, frame)
            rect(d, dm, 0, lo, PX, hi, frame)
    return finish(img, mask, rng, 3.0)


def style_diagrid(rng):          # 6: glass skin crossed by a diagonal lattice
    img, d, mask, dm = canvas((148, 168, 188))
    for by in range(CELLS):
        for bx in range(CELLS):
            x, y = bx * CELL, by * CELL
            rect(d, dm, x + 1, y + 1, x + CELL - 1, y + CELL - 4, glass_shade(rng, (126, 164, 200), 16), 255)
    size = PX * SS
    half = size // 2
    for k in range(-2, 4):                             # diagonals repeat every half tile, so they tile seamlessly
        for pts in ([(k * half, 0), (k * half + size, size)], [(k * half + size, 0), (k * half, size)]):
            d.line(pts, fill=(228, 226, 220), width=9 * SS)
            dm.line(pts, fill=0, width=9 * SS)
    return finish(img, mask, rng, 3.0)


def style_panels(rng):           # 7: plain cladding panels for sheds, malls and podiums
    img, d, mask, dm = canvas((196, 192, 184))
    for by in range(CELLS):
        for bx in range(CELLS):
            x, y = bx * CELL, by * CELL
            tone = int(214 + rng.uniform(-10, 10))
            rect(d, dm, x + 1, y + 1, x + CELL - 1, y + CELL - 1, (tone, tone - 3, tone - 9))
    for bx in range(1, CELLS, 3):
        y = (CELLS - 1) * CELL
        rect(d, dm, bx * CELL + 8, y + 14, bx * CELL + CELL - 8, y + CELL - 2, glass_shade(rng, (112, 130, 146), 10), 255)
    return finish(img, mask, rng)


STYLES = (style_curtain, style_punched, style_ribbon, style_fins, style_balcony, style_megaframe, style_diagrid, style_panels)
# which styles suit which kind of building (indices into STYLES)
TOWER_STYLES = (0, 0, 2, 3, 5, 1, 4, 4)
HERO_STYLES = (0, 5, 6, 3, 6, 0)
LOW_STYLES = (7, 7, 1, 2)


def main() -> None:
    rng = np.random.default_rng(31)
    out_dir = C.UNITY_OUT / "Facades"
    out_dir.mkdir(parents=True, exist_ok=True)
    cols, rows = 4, 2
    sheet = Image.new("RGBA", (PX * cols, PX * rows))
    for i, fn in enumerate(STYLES):
        tile = fn(rng)
        tile.save(C.OUT / f"facade_{i}.png")
        sheet.paste(tile, ((i % cols) * PX, (i // cols) * PX))
    sheet.save(out_dir / "facades_array.png")
    sheet.convert("RGB").resize((1024, 512), Image.LANCZOS).save(C.OUT / "facades_preview.jpg", quality=90)
    print(f"painted {len(STYLES)} facade styles -> {out_dir / 'facades_array.png'}")


if __name__ == "__main__":
    main()
