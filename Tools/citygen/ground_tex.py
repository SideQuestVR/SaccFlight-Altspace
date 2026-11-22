"""Stage 3: paint the ground textures (one per 4 km land tile) and the shallow-water tint map.

A stylised top-down rendering of the vector map - roads, land use, parks, beaches, runways and
the roofs of low buildings. No aerial imagery is used.

Input : out/layers.pkl, out/land.pkl, out/buildings.pkl, out/manifest.json
Output: Assets/City/Generated/Ground/gnd_<gx>_<gy>.jpg, watertint.png ; out/overview.jpg
"""
from __future__ import annotations

import json
import pickle
import time

import numpy as np
from PIL import Image, ImageChops, ImageDraw, ImageFilter
from shapely import STRtree
from shapely.geometry import MultiPoint, box

import config as C
import heroes as hero_lib
from meshutil import SHADOW_PER_M, scaled_ring

SS = 2                                   # supersampling factor for anti-aliasing
PX = C.GROUND_TEX_PX * SS
T = C.GROUND_TILE
S = PX / T                               # pixels per metre while drawing

SAND = (218, 198, 158)
LANDUSE_RGBA = {
    "residential": (204, 196, 180, 150), "industrial": (186, 182, 174, 170),
    "commercial": (200, 197, 190, 160), "retail": (200, 197, 190, 160),
    "construction": (224, 210, 178, 150), "railway": (170, 166, 160, 170), "port": (180, 178, 172, 180),
}
SHORE_RGB = {"beach": (238, 226, 192), "scrub": (178, 172, 124), "wetland": (98, 122, 94)}
GREEN_RGB = {
    "grass": (124, 162, 88), "park": (106, 152, 82), "garden": (112, 156, 86), "golf_course": (120, 174, 94),
    "pitch": (98, 158, 88), "forest": (72, 112, 62), "meadow": (150, 168, 104), "recreation_ground": (118, 160, 90),
    "cemetery": (150, 160, 120), "farmland": (160, 170, 110), "stadium": (150, 160, 150), "track": (176, 110, 90),
}
WATER = (58, 138, 150)
ASPHALT, ASPHALT_MINOR, CONCRETE, RUNWAY, PAINT = (74, 76, 82), (122, 119, 112), (178, 178, 174), (60, 61, 66), (236, 236, 232)
ROAD_WIDTH = {
    "motorway": 26, "trunk": 22, "primary": 18, "secondary": 14, "tertiary": 11,
    "motorway_link": 9, "trunk_link": 9, "primary_link": 8, "secondary_link": 7, "tertiary_link": 6,
    "residential": 7, "unclassified": 7, "living_street": 5,
}
ROAD_ORDER = ["living_street", "residential", "unclassified", "tertiary_link", "secondary_link", "primary_link",
              "trunk_link", "motorway_link", "tertiary", "secondary", "primary", "trunk", "motorway"]
ROOFS = np.array([(232, 230, 224), (222, 212, 190), (198, 198, 196), (206, 190, 160), (214, 214, 210),
                  (150, 160, 172), (188, 140, 108), (226, 222, 210)])


def to_px(xy: np.ndarray, x0: float, y1: float) -> list:
    out = np.empty_like(xy)
    out[:, 0] = (xy[:, 0] - x0) * S
    out[:, 1] = (y1 - xy[:, 1]) * S
    return [tuple(p) for p in out]


def polys(geom):
    if geom.geom_type == "Polygon":
        return [geom]
    if geom.geom_type == "MultiPolygon":
        return list(geom.geoms)
    return []


def lines(geom):
    if geom.geom_type == "LineString":
        return [geom]
    if geom.geom_type == "MultiLineString":
        return list(geom.geoms)
    return []


def noise_field(rng, size=1024):
    """Tileable soft noise, 0..1, built from a few octaves of blurred random values."""
    acc = np.zeros((size, size), np.float32)
    for cells, weight in ((8, 0.5), (32, 0.3), (128, 0.2)):
        small = Image.fromarray((rng.random((cells, cells)) * 255).astype(np.uint8))
        big = np.asarray(small.resize((size, size), Image.BICUBIC), np.float32) / 255.0
        acc += big * weight
    return acc


class Painter:
    def __init__(self) -> None:
        with open(C.OUT / "layers.pkl", "rb") as fh:
            self.L = pickle.load(fh)
        with open(C.OUT / "buildings.pkl", "rb") as fh:
            self.B = pickle.load(fh)
        self.trees = {name: STRtree(layer["geom"]) for name, layer in self.L.items() if layer["geom"]}
        self.btree = STRtree(self.B["geom"])
        with open(C.OUT / "heroes.json", encoding="utf-8") as fh:
            self.heroes = json.load(fh)
        self.noise = noise_field(np.random.default_rng(7))

    def query(self, name, region):
        layer = self.L.get(name)
        if not layer:
            return []
        idx = self.trees[name].query(region)
        return [(layer["geom"][i], layer["tags"][i]) for i in sorted(idx)]

    def paint_tile(self, gx: int, gy: int) -> Image.Image:
        x0, y0 = C.WORLD_X0 + gx * T, C.WORLD_Y0 + gy * T
        x1, y1 = x0 + T, y0 + T
        region = box(x0 - 60, y0 - 60, x1 + 60, y1 + 60)
        img = Image.new("RGB", (PX, PX), SAND)

        # soft sand variation, continuous across tiles
        n = self.noise
        reps = 4
        tile_noise = np.tile(n, (reps, reps))
        ox = int(((x0 - C.WORLD_X0) / T * 1024 * reps / 4) % 1024)
        oy = int(((C.WORLD_Y1 - y1) / T * 1024 * reps / 4) % 1024)
        patch = tile_noise[oy:oy + 1024, ox:ox + 1024]
        shade = Image.fromarray(((0.90 + 0.18 * patch) * 200).clip(0, 255).astype(np.uint8)).resize((PX, PX), Image.BICUBIC)
        img = Image.composite(img, Image.new("RGB", (PX, PX), (176, 156, 120)), shade.point(lambda v: min(255, int(v * 1.2))))

        over = Image.new("RGBA", (PX, PX), (0, 0, 0, 0))
        d = ImageDraw.Draw(over)
        for geom, tags in self.query("landuse", region):
            rgba = LANDUSE_RGBA.get(tags.get("landuse"))
            if rgba:
                for p in polys(geom):
                    d.polygon(to_px(np.asarray(p.exterior.coords), x0, y1), fill=rgba)
        img.paste(over, (0, 0), over)

        d = ImageDraw.Draw(img)
        for geom, tags in self.query("shore", region):
            rgb = SHORE_RGB.get(tags.get("natural"))
            if rgb:
                for p in polys(geom):
                    d.polygon(to_px(np.asarray(p.exterior.coords), x0, y1), fill=rgb)
        for geom, tags in self.query("green", region):
            rgb = GREEN_RGB.get(tags.get("leisure")) or GREEN_RGB.get(tags.get("landuse"))
            if rgb:
                for p in polys(geom):
                    d.polygon(to_px(np.asarray(p.exterior.coords), x0, y1), fill=rgb)
        for geom, _ in self.query("water", region):
            for p in polys(geom):
                d.polygon(to_px(np.asarray(p.exterior.coords), x0, y1), fill=WATER)
        for geom, tags in self.query("shore_structs", region):
            for p in polys(geom):
                d.polygon(to_px(np.asarray(p.exterior.coords), x0, y1), fill=CONCRETE)

        # airport surfaces
        for geom, tags in self.query("aero_areas", region):
            if tags.get("aeroway") in ("apron", "helipad"):
                for p in polys(geom):
                    d.polygon(to_px(np.asarray(p.exterior.coords), x0, y1), fill=CONCRETE)
        aero = self.query("aero_lines", region)
        for geom, tags in aero:
            if tags.get("aeroway") == "taxiway":
                for ln in lines(geom):
                    d.line(to_px(np.asarray(ln.coords), x0, y1), fill=(92, 93, 98), width=int(23 * S), joint="curve")

        # roads: minor first so major ones draw over them
        roads = self.query("roads", region)
        by_class: dict[str, list] = {}
        for geom, tags in roads:
            if tags.get("tunnel") not in (None, "no"):
                continue
            by_class.setdefault(tags.get("highway"), []).append(geom)
        for cls in ROAD_ORDER:
            width = max(2, int(round(ROAD_WIDTH[cls] * S)))
            colour = ASPHALT_MINOR if cls in ("residential", "unclassified", "living_street") else ASPHALT
            for geom in by_class.get(cls, []):
                for ln in lines(geom):
                    d.line(to_px(np.asarray(ln.coords), x0, y1), fill=colour, width=width, joint="curve")
        for geom, tags in self.query("rail", region):
            if tags.get("tunnel") not in (None, "no"):
                continue
            elevated = tags.get("bridge") not in (None, "no")
            for ln in lines(geom):
                d.line(to_px(np.asarray(ln.coords), x0, y1), fill=(186, 184, 178) if elevated else (110, 104, 98),
                       width=int((9 if elevated else 4) * S), joint="curve")

        # runways with simple markings
        for geom, tags in aero:
            if tags.get("aeroway") != "runway":
                continue
            try:
                width = float(str(tags.get("width", "45")).split()[0])
            except ValueError:
                width = 45.0
            for ln in lines(geom):
                xy = np.asarray(ln.coords)
                d.line(to_px(xy, x0, y1), fill=RUNWAY, width=int(width * S), joint="curve")
                length = ln.length
                for dist in np.arange(120.0, length - 120.0, 60.0):      # centreline dashes
                    a, b = ln.interpolate(dist), ln.interpolate(dist + 30.0)
                    d.line(to_px(np.array([[a.x, a.y], [b.x, b.y]]), x0, y1), fill=PAINT, width=max(2, int(1.2 * S)))
                for end in (40.0, length - 40.0):                        # threshold bars
                    p, q = ln.interpolate(end), ln.interpolate(end + (4.0 if end < length / 2 else -4.0))
                    direction = np.array([q.x - p.x, q.y - p.y])
                    direction /= max(np.hypot(*direction), 1e-6)
                    normal = np.array([-direction[1], direction[0]]) * (width * 0.42)
                    bar = np.array([[p.x, p.y] - normal, [p.x, p.y] + normal])
                    d.line(to_px(bar, x0, y1), fill=PAINT, width=max(3, int(5 * S)))

        # Baked shadows, then roofs. Buildings with real geometry cast their full-length shadow
        # and leave only a dark footprint; the rest are painted as roofs with a short shadow.
        geoms, heights, rand = self.B["geom"], self.B["height"], self.B["rand"]
        solid = self.B["is3d"] | self.B["on_hero"]
        footprints = self.B["footprints"]
        shadow = Image.new("L", (PX, PX), 0)
        ds = ImageDraw.Draw(shadow)

        def cast(ring, height, tip_scale=1.0):
            tip = (scaled_ring(ring, tip_scale) if tip_scale != 1.0 else ring) + SHADOW_PER_M * height
            hull = MultiPoint(np.vstack([ring, tip])).convex_hull
            if hull.geom_type == "Polygon":
                ds.polygon(to_px(np.asarray(hull.exterior.coords), x0, y1), fill=255)

        far = box(x0 - 700, y0 - 700, x1 + 60, y1 + 60)          # tall shadows reach in from neighbouring tiles
        for i in np.sort(self.btree.query(far)):
            if solid[i] and i in footprints:
                cast(np.asarray(footprints[i].exterior.coords)[:-1], float(heights[i]))
        for hero in self.heroes:
            if x0 - 700 <= hero["x"] <= x1 + 60 and y0 - 700 <= hero["y"] <= y1 + 60:
                ring, _ = hero_lib.occluder(hero, hero["kind"])
                taper = {"needle": 0.2, "blade": 0.35, "twist": 0.8}.get(hero["kind"], 1.0)
                cast(ring, hero["height"], taper)

        idx = np.sort(self.btree.query(region))
        rings = {}
        for i in idx:
            g = geoms[i]
            poly = g if g.geom_type == "Polygon" else max(g.geoms, key=lambda q: q.area)
            xy = np.asarray(poly.exterior.coords)
            rings[i] = xy
            if not solid[i]:
                ds.polygon(to_px(xy + SHADOW_PER_M * min(float(heights[i]), 30.0), x0, y1), fill=255)
                ds.polygon(to_px(xy + SHADOW_PER_M * min(float(heights[i]), 30.0) * 0.5, x0, y1), fill=255)
        shadow = shadow.filter(ImageFilter.GaussianBlur(1.3 * SS))
        shaded = ImageChops.multiply(img, Image.new("RGB", (PX, PX), (178, 188, 204)))   # sky-lit only
        img = Image.composite(shaded, img, shadow)
        d = ImageDraw.Draw(img)
        for i in idx:
            if solid[i]:
                d.polygon(to_px(rings[i], x0, y1), fill=(92, 92, 96))
            else:
                base = ROOFS[int(rand[i][0] * len(ROOFS)) % len(ROOFS)].astype(float) * (0.93 + 0.12 * rand[i][1])
                d.polygon(to_px(rings[i], x0, y1), fill=tuple(int(v) for v in base.clip(0, 255)))
        return img.resize((C.GROUND_TEX_PX, C.GROUND_TEX_PX), Image.LANCZOS)


def water_tint(land) -> Image.Image:
    """Low-res map over the world rectangle: turquoise near shores and inside lagoons, deep blue offshore."""
    w = 2048
    h = int(round(w * (C.WORLD_Y1 - C.WORLD_Y0) / (C.WORLD_X1 - C.WORLD_X0)))
    s = w / (C.WORLD_X1 - C.WORLD_X0)
    mask = Image.new("L", (w, h), 0)
    d = ImageDraw.Draw(mask)
    for p in (land.geoms if land.geom_type == "MultiPolygon" else [land]):
        xy = np.asarray(p.exterior.coords)
        d.polygon([((x - C.WORLD_X0) * s, (C.WORLD_Y1 - y) * s) for x, y in xy], fill=255)
        for hole in p.interiors:
            xy = np.asarray(hole.coords)
            d.polygon([((x - C.WORLD_X0) * s, (C.WORLD_Y1 - y) * s) for x, y in xy], fill=0)
    near = np.asarray(mask.filter(ImageFilter.GaussianBlur(7)), np.float32) / 255.0
    far = np.asarray(mask.filter(ImageFilter.GaussianBlur(34)), np.float32) / 255.0
    shallow = np.clip(near * 0.9 + far * 1.3, 0.0, 1.0)
    shallow = shallow * shallow * (3 - 2 * shallow)
    deep, mid, lagoon = np.array([14, 58, 96]), np.array([22, 122, 150]), np.array([78, 190, 186])
    t = shallow[..., None]
    rgb = np.where(t < 0.5, deep + (mid - deep) * (t / 0.5), mid + (lagoon - mid) * ((t - 0.5) / 0.5))
    return Image.fromarray(rgb.clip(0, 255).astype(np.uint8))


def land_mask(land, width: int, height: int) -> Image.Image:
    s = width / (C.WORLD_X1 - C.WORLD_X0)
    mask = Image.new("L", (width, height), 0)
    d = ImageDraw.Draw(mask)
    for p in (land.geoms if land.geom_type == "MultiPolygon" else [land]):
        d.polygon([((x - C.WORLD_X0) * s, (C.WORLD_Y1 - y) * s) for x, y in np.asarray(p.exterior.coords)], fill=255)
        for hole in p.interiors:
            d.polygon([((x - C.WORLD_X0) * s, (C.WORLD_Y1 - y) * s) for x, y in np.asarray(hole.coords)], fill=0)
    return mask


def main() -> None:
    import sys

    t0 = time.time()
    only_overview = "overview" in sys.argv[1:]
    out_dir = C.UNITY_OUT / "Ground"
    out_dir.mkdir(parents=True, exist_ok=True)
    with open(C.OUT / "manifest.json", encoding="utf-8") as fh:
        manifest = json.load(fh)
    with open(C.OUT / "land.pkl", "rb") as fh:
        land = pickle.load(fh)

    painter = None if only_overview else Painter()
    nx = int(round((C.WORLD_X1 - C.WORLD_X0) / T))
    ny = int(round((C.WORLD_Y1 - C.WORLD_Y0) / T))
    thumb = 256
    painted = Image.new("RGB", (nx * thumb, ny * thumb), SAND)
    for key in sorted(manifest["ground"]):
        gx, gy = (int(v) for v in key.split("_"))
        path = out_dir / f"gnd_{gx}_{gy}.jpg"
        if only_overview:
            img = Image.open(path)
        else:
            img = painter.paint_tile(gx, gy)
            img.save(path, quality=90, subsampling=0)
            print(f"  gnd_{gx}_{gy} ({time.time() - t0:.0f}s)", flush=True)
        painted.paste(img.resize((thumb, thumb), Image.LANCZOS), (gx * thumb, (ny - 1 - gy) * thumb))

    tint = water_tint(land)
    tint.save(out_dir / "watertint.png")
    # Preview as it will look in the world: painted ground only where there is land, tinted sea elsewhere.
    sea = tint.resize(painted.size, Image.BICUBIC)
    overview = Image.composite(painted, sea, land_mask(land, *painted.size))
    overview.save(C.OUT / "overview.jpg", quality=88)
    print(f"painted {len(manifest['ground'])} ground tiles + water tint in {time.time() - t0:.0f}s", flush=True)


if __name__ == "__main__":
    main()
