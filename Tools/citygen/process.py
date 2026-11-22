"""Stage 2: turn the extracted map layers into tiled mesh arrays with baked lighting.

Input : out/layers.pkl (from extract.py)
Output: out/meshes/*.npz   mesh arrays (positions relative to their tile origin, z up)
        out/land.pkl       land polygon (used by ground_tex.py)
        out/buildings.pkl  per-building footprint / height / flags (ground_tex.py)
        out/heroes.json    the hero tower sites and archetypes
        out/manifest.json  tile list, world rectangle, triangle statistics
        Assets/City/Generated/city_layout.json  small layout file read by the Unity importer

Axes here: x = east, y = north, z = up (Blender convention).  Sea level z = 0.
"""
from __future__ import annotations

import json
import math
import pickle
import re
import time

import numpy as np
import shapely
from shapely import STRtree
from shapely.geometry import Polygon, box
from shapely.geometry.polygon import orient
from shapely.ops import linemerge, polygonize, unary_union

import config as C
import facades
import heroes as hero_lib
from meshutil import (MESH_DIR, MeshBuf, SunBaker, add_cap, add_walls, polygons_of, ring_of, scaled_ring,
                      triangulate)

WORLD = box(C.WORLD_X0, C.WORLD_Y0, C.WORLD_X1, C.WORLD_Y1)

# Facade tints (sRGB 0-1). The facade textures carry the detail; these shift the hue per building.
TOWER_TINTS = np.array([
    (0.86, 0.93, 1.00), (1.00, 0.95, 0.86), (0.88, 1.00, 0.96), (1.00, 1.00, 1.00), (0.96, 0.88, 0.80),
    (0.80, 0.86, 0.96), (0.92, 0.96, 0.90), (1.00, 0.90, 0.82), (0.84, 0.90, 0.92), (0.98, 0.97, 0.92),
])
LOW_TINTS = np.array([
    (1.00, 0.99, 0.95), (0.98, 0.94, 0.86), (0.93, 0.93, 0.92), (1.00, 0.96, 0.88), (0.90, 0.90, 0.90),
    (0.96, 0.90, 0.82),
])
ROOF_TINTS = np.array([(0.78, 0.78, 0.77), (0.72, 0.71, 0.69), (0.84, 0.83, 0.80), (0.66, 0.68, 0.70)])


def num(value):
    if value is None:
        return math.nan
    m = re.match(r"\s*(-?\d+(?:\.\d+)?)", str(value))
    return float(m.group(1)) if m else math.nan


def simplify_footprint(geom, max_verts: int | None = None):
    """Simplified outline of the largest polygon. Large buildings keep more vertices, and the result
    never grows much beyond the real footprint (no bounding-rectangle fallback for complex shapes)."""
    polys = polygons_of(geom)
    if not polys:
        return None
    p = Polygon(max(polys, key=lambda g: g.area).exterior)
    if max_verts is None:
        max_verts = 8 if p.area < 1500 else 12 if p.area < 5000 else 20 if p.area < 20000 else 32
    chosen = None
    for tol in (1.5, 3.0, 5.0, 8.0, 12.0):
        s = p.simplify(tol, preserve_topology=True)
        if s.geom_type != "Polygon" or not s.is_valid or s.is_empty:
            continue
        chosen = s
        if len(s.exterior.coords) - 1 <= max_verts:
            break
    box_fit = p.minimum_rotated_rectangle
    if chosen is None or (box_fit.geom_type == "Polygon" and box_fit.area <= p.area * 1.12
                          and len(chosen.exterior.coords) - 1 > max_verts):
        chosen = box_fit                       # only when the building really is close to a rectangle
    if chosen.geom_type != "Polygon" or chosen.area < 4.0:
        return None
    return orient(chosen, 1.0)


def largest_polygon(geom, min_area: float):
    polys = [g for g in polygons_of(geom) if g.area >= min_area]
    if not polys:
        return None
    best = max(polys, key=lambda g: g.area)
    best = best.buffer(0)
    polys = polygons_of(best)
    return orient(max(polys, key=lambda g: g.area), 1.0) if polys else None


def resolve_overlaps(shapes: dict, heights: dict, fixed: list) -> dict:
    """Make footprints mutually exclusive so no two roofs (or same-facing walls) can ever coincide.

    Buildings are visited from tallest to lowest; each one loses whatever area a taller building
    (or a hero tower in `fixed`) already occupies. A podium under a tower therefore ends up with a
    hole where the tower stands, and a building swallowed by a taller one disappears.
    """
    order = sorted(shapes, key=lambda i: (-heights[i], i))
    rank = {i: r for r, i in enumerate(order)}
    keys = list(shapes)
    tree = STRtree([shapes[i] for i in keys])
    fixed_tree = STRtree(fixed) if fixed else None
    final = {}
    for i in order:
        fp = shapes[i]
        blockers = [final[keys[j]] for j in tree.query(fp, predicate="intersects")
                    if keys[j] != i and rank[keys[j]] < rank[i] and final.get(keys[j]) is not None]
        if fixed_tree is not None:
            blockers += [fixed[j] for j in fixed_tree.query(fp, predicate="intersects")]
        if blockers:
            cut = largest_polygon(fp.difference(unary_union(blockers)), max(40.0, 0.15 * fp.area))
            final[i] = cut
        else:
            final[i] = fp
    return final


def rect_of(geom):
    r = geom.minimum_rotated_rectangle
    if r.geom_type != "Polygon" or r.area < 4.0:
        return None
    return orient(r, 1.0)


# --------------------------------------------------------------------------------------
# land / sea
# --------------------------------------------------------------------------------------
def build_land(layers) -> shapely.Geometry:
    """Assemble the land polygon from coastline ways (OSM: land lies left of the way)."""
    pad = 800.0
    rect = box(C.WORLD_X0 - pad, C.WORLD_Y0 - pad, C.WORLD_X1 + pad, C.WORLD_Y1 + pad)
    merged = linemerge(unary_union(layers["coast"]["geom"]))
    lines = list(merged.geoms) if merged.geom_type == "MultiLineString" else [merged]
    clipped = [ln.intersection(rect) for ln in lines]
    faces = list(polygonize(unary_union([rect.boundary] + [c for c in clipped if not c.is_empty])))
    tree = STRtree(faces)
    votes = np.zeros((len(faces), 2))
    eps = 0.75
    for ln in lines:
        xy = np.asarray(ln.coords)
        a, b = xy[:-1], xy[1:]
        d = b - a
        seg = np.hypot(d[:, 0], d[:, 1])
        ok = seg > 1e-6
        a, b, d, seg = a[ok], b[ok], d[ok], seg[ok]
        mid = (a + b) / 2
        left = np.column_stack([-d[:, 1], d[:, 0]]) / seg[:, None]
        for side, pts in ((0, mid + left * eps), (1, mid - left * eps)):
            hit = tree.query(shapely.points(pts), predicate="within")
            np.add.at(votes[:, side], hit[1], seg[hit[0]])
    land = unary_union([f for f, v in zip(faces, votes) if v[0] > v[1]])

    lakes = [g for g in layers["water"]["geom"] if g.area >= 5000.0]
    if lakes:
        land = land.difference(unary_union(lakes).buffer(0))
    land = land.simplify(2.0, preserve_topology=True).buffer(0)

    cleaned = []
    for p in polygons_of(land):
        if p.area < 400.0:
            continue
        holes = [r for r in p.interiors if Polygon(r).area >= 400.0]
        cleaned.append(Polygon(p.exterior, holes))
    return unary_union(cleaned)


def on_border(pt, x0, y0, x1, y1, tol=1e-3):
    return (abs(pt[0] - x0) < tol, abs(pt[0] - x1) < tol, abs(pt[1] - y0) < tol, abs(pt[1] - y1) < tol)


def flat_patch(buf: MeshBuf, poly: Polygon, z: float, uv_fn) -> None:
    verts, tris = triangulate(poly)
    if len(tris) == 0:
        return
    pos = np.column_stack([verts, np.full(len(verts), z)])
    nrm = np.tile([0.0, 0.0, 1.0], (len(verts), 1))
    buf.add(pos, nrm, uv_fn(verts), np.ones((len(verts), 4)), tris)


def ground_tiles(land) -> dict:
    """Land surface per 4 km tile (UV = tile-local 0..1) with walls down into the sea at coasts."""
    T = C.GROUND_TILE
    stats = {}
    nx = int(round((C.WORLD_X1 - C.WORLD_X0) / T))
    ny = int(round((C.WORLD_Y1 - C.WORLD_Y0) / T))
    for gx in range(nx):
        for gy in range(ny):
            x0, y0 = C.WORLD_X0 + gx * T, C.WORLD_Y0 + gy * T
            x1, y1 = x0 + T, y0 + T
            piece = land.intersection(box(x0, y0, x1, y1))
            buf = MeshBuf((x0, y0))
            for poly in polygons_of(piece):
                poly = orient(poly, 1.0)
                flat_patch(buf, poly, C.LAND_H, lambda v: (v - [x0, y0]) / T)
                for ring in [poly.exterior] + list(poly.interiors):
                    xy = np.asarray(ring.coords)[:-1]
                    nxt = np.roll(xy, -1, axis=0)
                    for a, b in zip(xy, nxt):
                        fa, fb = on_border(a, x0, y0, x1, y1), on_border(b, x0, y0, x1, y1)
                        if any(p and q for p, q in zip(fa, fb)):
                            continue  # edge lies on the tile border: the neighbour tile continues it
                        d = b - a
                        length = float(np.hypot(*d))
                        if length < 1e-4:
                            continue
                        n = np.array([d[1], -d[0], 0.0]) / length
                        pos = np.array([[[a[0], a[1], C.COAST_WALL_BOTTOM], [b[0], b[1], C.COAST_WALL_BOTTOM],
                                         [b[0], b[1], C.LAND_H], [a[0], a[1], C.LAND_H]]])
                        ua = np.clip((a - [x0, y0]) / T, 0.002, 0.998)
                        ub = np.clip((b - [x0, y0]) / T, 0.002, 0.998)
                        buf.add_quads(pos, n[None, :], np.array([[ua, ub, ub, ua]]), np.ones((1, 4, 4)))
            n_tris = buf.save(f"gnd_{gx}_{gy}")
            if n_tris:
                stats[f"{gx}_{gy}"] = {"tris": n_tris, "origin": [x0, y0]}
    return stats


def sea_tiles(land) -> dict:
    """Sea surface cut around the land, in 8 km tiles."""
    T = C.SKYLINE_TILE
    sea = WORLD.difference(land)
    stats = {}
    nx = int(round((C.WORLD_X1 - C.WORLD_X0) / T))
    ny = int(round((C.WORLD_Y1 - C.WORLD_Y0) / T))
    for sx in range(nx):
        for sy in range(ny):
            x0, y0 = C.WORLD_X0 + sx * T, C.WORLD_Y0 + sy * T
            piece = sea.intersection(box(x0, y0, x0 + T, y0 + T))
            buf = MeshBuf((x0, y0))
            for poly in polygons_of(piece):
                if poly.area >= 1.0:
                    flat_patch(buf, poly, 0.0, lambda v: v / 1000.0)
            n_tris = buf.save(f"sea_{sx}_{sy}")
            if n_tris:
                stats[f"{sx}_{sy}"] = {"tris": n_tris, "origin": [x0, y0]}
    return stats


def single_transition(flags: np.ndarray) -> np.ndarray:
    """Closest land/sea pattern along a box edge that changes at most once.

    Harbours and breakwaters make the raw samples flicker; extruding those outward would draw
    stripes to the horizon. One clean coastline crossing per edge reads correctly in the haze.
    """
    n = len(flags)
    land_before = np.concatenate([[0], np.cumsum(flags)])
    k = np.arange(n + 1)
    sea_then_land = land_before[k] + ((n - k) - (land_before[n] - land_before[k]))
    land_then_sea = (k - land_before[k]) + (land_before[n] - land_before[k])
    if sea_then_land.min() <= land_then_sea.min():
        return np.arange(n) >= int(sea_then_land.argmin())
    return np.arange(n) < int(land_then_sea.argmin())


def outer_ring(land) -> dict:
    """Flat sea / desert planes continuing past the box edges out to the haze."""
    prepared = shapely.prepared.prep(land)
    step, inset, ext = 250.0, 6.0, C.OUTER_EXTENT
    X0, Y0, X1, Y1 = C.WORLD_X0, C.WORLD_Y0, C.WORLD_X1, C.WORLD_Y1
    bufs = {True: MeshBuf(), False: MeshBuf()}

    def quad(is_land, ax, ay, bx, by):
        z = C.LAND_H if is_land else 0.0
        x_lo, x_hi, y_lo, y_hi = min(ax, bx), max(ax, bx), min(ay, by), max(ay, by)
        pos = np.array([[x_lo, y_lo, z], [x_hi, y_lo, z], [x_hi, y_hi, z], [x_lo, y_hi, z]])
        nrm = np.tile([0.0, 0.0, 1.0], (4, 1))
        bufs[bool(is_land)].add(pos, nrm, pos[:, :2] / 1000.0, np.ones((4, 4)), np.array([[0, 1, 2], [0, 2, 3]]))

    def sample(points):
        return single_transition(np.array([prepared.contains(shapely.Point(x, y)) for x, y in points]))

    xs = np.arange(X0, X1, step)
    ys = np.arange(Y0, Y1, step)
    south = sample([(x + step / 2, Y0 + inset) for x in xs])
    north = sample([(x + step / 2, Y1 - inset) for x in xs])
    west = sample([(X0 + inset, y + step / 2) for y in ys])
    east = sample([(X1 - inset, y + step / 2) for y in ys])

    def runs(flags):
        start = 0
        for i in range(1, len(flags) + 1):
            if i == len(flags) or flags[i] != flags[start]:
                yield start, i, bool(flags[start])
                start = i

    for a, b, v in runs(south):
        quad(v, xs[a], Y0 - ext, xs[b - 1] + step, Y0)
    for a, b, v in runs(north):
        quad(v, xs[a], Y1, xs[b - 1] + step, Y1 + ext)
    for a, b, v in runs(west):
        quad(v, X0 - ext, ys[a], X0, ys[b - 1] + step)
    for a, b, v in runs(east):
        quad(v, X1, ys[a], X1 + ext, ys[b - 1] + step)
    # corners are land only when both neighbouring edge ends are land
    quad(south[0] and west[0], X0 - ext, Y0 - ext, X0, Y0)
    quad(south[-1] and east[0], X1, Y0 - ext, X1 + ext, Y0)
    quad(north[0] and west[-1], X0 - ext, Y1, X0, Y1 + ext)
    quad(north[-1] and east[-1], X1, Y1, X1 + ext, Y1 + ext)
    return {"outer_land": bufs[True].save("outer_land"), "outer_sea": bufs[False].save("outer_sea")}


# --------------------------------------------------------------------------------------
# buildings
# --------------------------------------------------------------------------------------
LOW_TYPES = {"house", "detached", "terrace", "villa", "bungalow", "semidetached_house", "farm", "cabin"}
MID_TYPES = {"apartments", "residential", "hotel", "dormitory", "office"}
SHED_TYPES = {"industrial", "warehouse", "retail", "commercial", "hangar", "supermarket", "school", "hospital"}
TINY_TYPES = {"carport", "roof", "shed", "hut", "garage", "garages", "kiosk", "service"}


def estimate_heights(geoms, tags, area, cent, rng):
    """Tagged height where present, otherwise inferred from tall neighbours or building type."""
    n = len(geoms)
    h_tag = np.array([num(t.get("height")) for t in tags])
    levels = np.array([num(t.get("building:levels")) for t in tags])
    est = np.where(~np.isnan(h_tag), h_tag, np.where(~np.isnan(levels), levels * 3.4 + 3.0, np.nan))
    est = np.where(est <= 0, np.nan, est)
    tagged = ~np.isnan(est)

    # Untagged compact footprints inside a cluster of tagged towers are probably towers too.
    known_tall = np.flatnonzero(tagged & (est >= 12.0))
    cand = np.flatnonzero(~tagged & (area >= 300.0) & (area <= 3500.0))
    inferred = np.zeros(n, bool)
    if len(known_tall) and len(cand):
        mrr_area = shapely.area(shapely.minimum_rotated_rectangle(geoms[cand]))
        cand = cand[area[cand] / np.maximum(mrr_area, 1.0) >= 0.6]
        tree = STRtree(cent[known_tall])
        pairs = tree.query(cent[cand], predicate="dwithin", distance=180.0)
        order = np.argsort(pairs[0], kind="stable")
        src, dst = pairs[0][order], pairs[1][order]
        starts = np.flatnonzero(np.r_[True, src[1:] != src[:-1]])
        ends = np.r_[starts[1:], len(src)]
        for s, e in zip(starts, ends):
            heights = est[known_tall[dst[s:e]]]
            if e - s >= 3 and np.median(heights) >= 35.0:
                i = cand[src[s]]
                est[i] = float(np.clip(rng.choice(heights) * rng.uniform(0.75, 1.05), 20.0, heights.max() * 1.05))
                inferred[i] = True

    kind = np.array([t.get("building", "yes") for t in tags])
    default = np.where(area < 120, 5.0, np.where(area < 600, 7.5, np.where(area < 3000, 9.0, 12.0)))
    default = np.where(np.isin(kind, list(LOW_TYPES)), 7.0, default)
    default = np.where(np.isin(kind, list(MID_TYPES)), 14.0, default)
    default = np.where(np.isin(kind, list(SHED_TYPES)), 9.5, default)
    default = np.where(np.isin(kind, list(TINY_TYPES)), 3.0, default)
    default = default * rng.uniform(0.85, 1.15, n)
    unknown = np.isnan(est)
    est[unknown] = default[unknown]
    return est, tagged, inferred


def pick_heroes(geoms, cent_xy, est, tagged, area):
    """Tallest tagged sites, spaced apart; these get original hero towers instead."""
    order = np.argsort(-np.where(tagged, est, 0.0))
    chosen = []
    for i in order:
        if len(chosen) >= C.HERO_COUNT or est[i] < 150.0:
            break
        if area[i] < 250.0:
            continue
        if all(np.hypot(*(cent_xy[i] - cent_xy[j])) >= C.HERO_MIN_SPACING for j in chosen):
            chosen.append(int(i))
    heroes = []
    for i in chosen:
        r = rect_of(geoms[i])
        if r is None:
            continue
        pts = np.asarray(r.exterior.coords)[:-1]
        e0, e1 = pts[1] - pts[0], pts[2] - pts[1]
        l0, l1 = float(np.hypot(*e0)), float(np.hypot(*e1))
        long_edge = e0 if l0 >= l1 else e1
        heroes.append({
            "index": i, "x": float(cent_xy[i][0]), "y": float(cent_xy[i][1]), "height": float(est[i]),
            "length": max(l0, l1), "width": min(l0, l1),
            "angle_deg": float(np.degrees(np.arctan2(long_edge[1], long_edge[0]))),
            "footprint_area": float(area[i]), "rect": pts.tolist(),
        })
    return heroes


class Look:
    """Per-building appearance."""

    def __init__(self, tint, roof, style, bay, floor, u_offset):
        self.tint, self.roof, self.style, self.bay, self.floor, self.u_offset = tint, roof, style, bay, floor, u_offset


def build_tower(buf: MeshBuf, fp: Polygon, h: float, look: Look, u, baker, sid, detailed: bool) -> None:
    """Extruded footprint; taller towers get setbacks / a mast for skyline variety (LOD0 only)."""
    ring = ring_of(fp)
    base, top = C.LAND_H, C.LAND_H + h

    def walls(r, z0, z1, grounded):
        add_walls(buf, r, z0, z1, look.tint, look.style, look.bay, look.floor, baker, sid, grounded,
                  split_shadows=detailed, u_offset=look.u_offset)

    if not detailed or h < 80.0 or u[0] > 0.72:
        walls(ring, base, top, True)
        add_cap(buf, fp, top, look.roof, baker, sid)
        return
    if u[0] < 0.42:                                   # single setback crown
        cuts = [(u[1] * 0.10 + 0.82, u[2] * 0.25 + 0.55)]
    elif u[0] < 0.62:                                 # double setback
        cuts = [(u[1] * 0.08 + 0.66, 0.80), (u[2] * 0.06 + 0.86, 0.52)]
    else:                                             # tapering stack of three
        cuts = [(0.55, 0.86), (0.75, 0.70), (0.90, 0.50)]
    z_prev, ring_prev, poly_prev = base, ring, fp
    for frac, scale in cuts:
        z = base + h * frac
        walls(ring_prev, z_prev, z, z_prev == base)
        add_cap(buf, poly_prev, z, look.roof, baker, sid)
        ring_prev = scaled_ring(ring, scale)
        poly_prev = Polygon(ring_prev)
        z_prev = z
    walls(ring_prev, z_prev, top, False)
    add_cap(buf, poly_prev, top, look.roof, baker, sid)
    if h >= 180.0 and u[3] < 0.45:                    # mast
        c = ring.mean(axis=0)
        r = max(1.2, 0.035 * math.sqrt(fp.area))
        mast = c + np.array([[-r, -r], [r, -r], [r, r], [-r, r]])
        add_walls(buf, mast, top, top + h * (0.08 + 0.10 * u[1]), (0.82, 0.82, 0.84), 7, 4.0, 4.0, None, sid, False)


def building_tiles(layers, rng) -> tuple[dict, list, dict]:
    b = layers["buildings"]
    geoms = np.array(b["geom"], dtype=object)
    tags = b["tags"]
    cent = shapely.centroid(geoms)
    cx, cy = shapely.get_x(cent), shapely.get_y(cent)
    keep = (cx >= C.WORLD_X0) & (cx < C.WORLD_X1) & (cy >= C.WORLD_Y0) & (cy < C.WORLD_Y1)
    geoms, cent, cx, cy = geoms[keep], cent[keep], cx[keep], cy[keep]
    tags = [t for t, k in zip(tags, keep) if k]
    area = shapely.area(geoms)
    cent_xy = np.column_stack([cx, cy])
    n = len(geoms)

    est, tagged, inferred = estimate_heights(geoms, tags, area, cent, rng)
    est = np.minimum(est, 520.0)
    heroes = pick_heroes(geoms, cent_xy, est, tagged, area)
    cycle = ("twist", "blade", "crown", "twin", "stack", "needle")
    for rank, hero in enumerate(heroes):
        hero["kind"] = "needle" if rank == 0 else cycle[(rank - 1) % len(cycle)]
        hero["rank"] = rank
        if rank == 0:
            hero["height"] = max(hero["height"], 700.0)   # the signature super-tall

    # Anything standing on a hero site is replaced by the hero tower.
    hero_zone = unary_union([Polygon(h["rect"]).buffer(8.0) for h in heroes]) if heroes else None
    on_hero = shapely.contains(hero_zone, cent) if hero_zone is not None else np.zeros(n, bool)

    is3d = ((est >= C.MIN_3D_HEIGHT) | (area >= C.MIN_3D_AREA)) & ~on_hero & (area >= 40.0)
    rand = rng.random((n, 6))
    T = C.BUILDING_TILE
    tx = np.floor((cx - C.WORLD_X0) / T).astype(int)
    ty = np.floor((cy - C.WORLD_Y0) / T).astype(int)

    # Pass 1: footprints. Every building gets a slightly different height so no two roofs are ever
    # exactly level, then overlapping footprints are cut apart (taller wins).
    est = est + (rand[:, 5] - 0.5) * 0.7 * (est > 6.0)
    raw, skipped = {}, 0
    for i in np.flatnonzero(is3d):
        fp = simplify_footprint(geoms[i], 12 if (est[i] >= C.MIN_3D_HEIGHT and area[i] < 5000) else None)
        if fp is None:
            skipped += 1
            is3d[i] = False
        else:
            raw[int(i)] = fp
    hero_plans = []
    for hero in heroes:
        ring, frac = hero_lib.occluder(hero, hero["kind"])
        hero_plans.append((Polygon(ring), frac))
    resolved = resolve_overlaps(raw, {i: float(est[i]) for i in raw}, [p.buffer(1.5) for p, _ in hero_plans])

    # Every surviving building and hero is a shadow caster for the light bake.
    baker = SunBaker()
    plans = {}
    swallowed = 0
    for i, fp in resolved.items():
        if fp is None:
            swallowed += 1
            is3d[i] = False
            continue
        plans[i] = (fp, baker.add(fp, C.LAND_H + float(est[i])))
    for hero, (plan, frac) in zip(heroes, hero_plans):
        hero["occluder"] = baker.add(plan, C.LAND_H + hero["height"] * frac)
    baker.freeze()

    tiles: dict[tuple[int, int], dict[str, MeshBuf]] = {}

    def bufs(ix, iy):
        key = (ix, iy)
        if key not in tiles:
            o = (C.WORLD_X0 + ix * T, C.WORLD_Y0 + iy * T)
            tiles[key] = {k: MeshBuf(o) for k in ("lod0", "lod1", "lod2", "col")}
        return tiles[key]

    # Pass 2: meshes with baked light. Every LOD is built from the same exclusive footprint; the
    # simpler levels only drop the setbacks, masts and shadow-line splits.
    counts = {"tower": 0, "lowrise3d": 0, "skipped": skipped, "swallowed_by_taller": swallowed}
    for i, (fp, sid) in plans.items():
        h = float(est[i])
        tall = h >= C.MIN_3D_HEIGHT
        u = rand[i]
        if tall:
            tint = TOWER_TINTS[int(u[2] * len(TOWER_TINTS)) % len(TOWER_TINTS)] * (0.94 + 0.06 * u[3])
            style = facades.TOWER_STYLES[int(u[4] * len(facades.TOWER_STYLES)) % len(facades.TOWER_STYLES)]
            bay, floor = 3.2 + 1.0 * u[1], 3.3 + 0.5 * u[5]
        else:
            tint = LOW_TINTS[int(u[2] * len(LOW_TINTS)) % len(LOW_TINTS)] * (0.95 + 0.05 * u[3])
            style = facades.LOW_STYLES[int(u[4] * len(facades.LOW_STYLES)) % len(facades.LOW_STYLES)]
            bay, floor = 4.5, 4.0
        roof = ROOF_TINTS[int(u[5] * len(ROOF_TINTS)) % len(ROOF_TINTS)]
        look = Look(tint, roof, style, bay, floor, float(u[0] * 8.0))
        t = bufs(tx[i], ty[i])
        build_tower(t["lod0"], fp, h, look, u, baker, sid, True)
        if tall:
            build_tower(t["lod1"], fp, h, look, u, baker, sid, False)
            if h >= C.LOD2_MIN_HEIGHT:
                build_tower(t["lod2"], fp, h, look, u, baker, sid, False)
            if h >= C.COLLIDER_MIN_HEIGHT:
                build_tower(t["col"], fp, h, look, u, None, sid, False)
            counts["tower"] += 1
        else:
            counts["lowrise3d"] += 1

    # Original hero towers on the tallest sites; they join their tile's meshes (same material).
    for hero in heroes:
        rank, kind = hero["rank"], hero["kind"]
        ix = int((hero["x"] - C.WORLD_X0) // T)
        iy = int((hero["y"] - C.WORLD_Y0) // T)
        hero["tile"] = [ix, iy]
        u = rng.random(4)
        tint = TOWER_TINTS[rank % len(TOWER_TINTS)]
        style = facades.HERO_STYLES[rank % len(facades.HERO_STYLES)]
        t = bufs(ix, iy)
        for detail, lod in enumerate(("lod0", "lod1", "lod2")):
            skin = hero_lib.Skin(t[lod], tint, ROOF_TINTS[0], style, 3.6, 3.9, C.LAND_H, baker, hero["occluder"])
            hero_lib.build_hero(skin, hero, kind, detail, u)
        collider = hero_lib.Skin(t["col"], tint, ROOF_TINTS[0], style, 3.6, 3.9, C.LAND_H, None, -1)
        hero_lib.build_hero(collider, hero, kind, 1, u)          # collision follows the shape

    stats = {}
    for (ix, iy), group in sorted(tiles.items()):
        entry = {"origin": [C.WORLD_X0 + ix * T, C.WORLD_Y0 + iy * T]}
        for lod, buf in group.items():
            entry[lod] = buf.save(f"bld_{ix}_{iy}_{lod}")
        stats[f"{ix}_{iy}"] = entry

    footprints = {i: Polygon(p[0].exterior) for i, p in plans.items()}
    with open(C.OUT / "buildings.pkl", "wb") as fh:
        pickle.dump({"geom": list(geoms), "height": est, "area": area, "is3d": is3d, "on_hero": on_hero,
                     "footprints": footprints, "cx": cx, "cy": cy, "rand": rand}, fh,
                    protocol=pickle.HIGHEST_PROTOCOL)

    counts.update({"total_in_world": n, "tagged": int(tagged.sum()), "inferred_tall": int(inferred.sum()),
                   "heroes": len(heroes)})
    return stats, heroes, counts


# --------------------------------------------------------------------------------------
def main() -> None:
    t0 = time.time()
    MESH_DIR.mkdir(parents=True, exist_ok=True)
    for old in MESH_DIR.glob("*.npz"):
        old.unlink()
    with open(C.OUT / "layers.pkl", "rb") as fh:
        layers = pickle.load(fh)
    rng = np.random.default_rng(20261120)

    land = build_land(layers)
    with open(C.OUT / "land.pkl", "wb") as fh:
        pickle.dump(land, fh, protocol=pickle.HIGHEST_PROTOCOL)
    land_in_world = land.intersection(WORLD)
    print(f"land: {land_in_world.area / 1e6:.0f} km2 of {WORLD.area / 1e6:.0f} km2, "
          f"{len(polygons_of(land_in_world))} pieces ({time.time() - t0:.0f}s)", flush=True)

    ground = ground_tiles(land)
    sea = sea_tiles(land)
    outer = outer_ring(land)
    print(f"ground tiles {len(ground)} ({sum(v['tris'] for v in ground.values())} tris), "
          f"sea tiles {len(sea)} ({sum(v['tris'] for v in sea.values())} tris), outer {outer} "
          f"({time.time() - t0:.0f}s)", flush=True)

    bstats, heroes, counts = building_tiles(layers, rng)
    totals = {lod: sum(v.get(lod, 0) for v in bstats.values()) for lod in ("lod0", "lod1", "lod2", "col")}
    print(f"buildings: {counts}", flush=True)
    print(f"building tiles {len(bstats)}; triangles {totals} ({time.time() - t0:.0f}s)", flush=True)

    with open(C.OUT / "heroes.json", "w", encoding="utf-8") as fh:
        json.dump(heroes, fh, indent=1)
    helipads = hero_lib.helipad_sites(heroes, C.LAND_H)
    runways = []
    for geom, tags in zip(layers["aero_lines"]["geom"], layers["aero_lines"]["tags"]):
        if tags.get("aeroway") == "runway" and geom.geom_type == "LineString" and geom.length >= 500.0:
            (ax, ay), (bx, by) = geom.coords[0], geom.coords[-1]
            if WORLD.contains(geom.centroid):
                width = num(tags.get("width"))
                runways.append({"ax": ax, "ay": ay, "bx": bx, "by": by, "length": geom.length,
                                "width": 45.0 if math.isnan(width) else width})
    tri = {**totals, "ground": sum(v["tris"] for v in ground.values()),
           "sea": sum(v["tris"] for v in sea.values()), "outer": sum(outer.values())}
    manifest = {
        "world": [C.WORLD_X0, C.WORLD_Y0, C.WORLD_X1, C.WORLD_Y1], "land_h": C.LAND_H, "scale": C.SCALE,
        "tile": {"building": C.BUILDING_TILE, "ground": C.GROUND_TILE, "sea": C.SKYLINE_TILE},
        "helipads": helipads, "runways": runways,
        "ground": ground, "sea": sea, "outer": outer, "buildings": bstats, "counts": counts, "triangles": tri,
    }
    with open(C.OUT / "manifest.json", "w", encoding="utf-8") as fh:
        json.dump(manifest, fh, indent=1)

    # Flat layout file for the Unity importer (Unity's JsonUtility cannot read dictionaries).
    C.UNITY_OUT.mkdir(parents=True, exist_ok=True)
    sun_elev = math.radians(C.SUN_ELEV_DEG)
    layout = {
        "worldX0": C.WORLD_X0, "worldY0": C.WORLD_Y0, "worldX1": C.WORLD_X1, "worldY1": C.WORLD_Y1,
        "landHeight": C.LAND_H, "buildingTile": C.BUILDING_TILE, "groundTile": C.GROUND_TILE,
        "seaTile": C.SKYLINE_TILE, "outerExtent": C.OUTER_EXTENT,
        "sunTowardX": C.SUN_TOWARD[0] * math.cos(sun_elev), "sunTowardUp": math.sin(sun_elev),
        "sunTowardY": C.SUN_TOWARD[1] * math.cos(sun_elev),
        "facadeStyles": C.FACADE_STYLES,
        "helipads": helipads, "runways": runways,
        "heroes": [{"x": h["x"], "y": h["y"], "height": h["height"], "kind": h["kind"]} for h in heroes],
    }
    with open(C.UNITY_OUT / "city_layout.json", "w", encoding="utf-8") as fh:
        json.dump(layout, fh, indent=1)

    unique = tri["lod0"] + tri["lod1"] + tri["lod2"] + tri["ground"] + tri["sea"] + tri["outer"]
    print(f"unique render triangles: {unique} (+ {tri['col']} collider); done in {time.time() - t0:.0f}s", flush=True)


if __name__ == "__main__":
    main()
