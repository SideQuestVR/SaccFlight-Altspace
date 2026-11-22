"""Mesh buffers, triangulation and baked-light helpers shared by process.py and heroes.py.

Axes: x = east, y = north, z = up. Vertex colour RGB = facade tint x baked light (stored sRGB);
alpha = 0 for roofs / ledges (no facade texture), otherwise (style + 1) / 16.
"""
from __future__ import annotations

import math

import mapbox_earcut as earcut
import numpy as np
import shapely
from shapely import STRtree
from shapely.geometry import Polygon
from shapely.geometry.polygon import orient

import config as C

MESH_DIR = C.OUT / "meshes"

SUN_ELEV = math.radians(C.SUN_ELEV_DEG)
TAN_ELEV = math.tan(SUN_ELEV)
SUN_2D = np.asarray(C.SUN_TOWARD, float)
SUN_VEC = np.array([SUN_2D[0] * math.cos(SUN_ELEV), SUN_2D[1] * math.cos(SUN_ELEV), math.sin(SUN_ELEV)])
SHADOW_PER_M = -SUN_2D / TAN_ELEV            # ground offset of a shadow per metre of height
SUN_LIGHT = np.asarray(C.SUN_RGB) * C.SUN_STRENGTH
SKY_LIGHT = np.asarray(C.SKY_RGB) * C.SKY_STRENGTH


def srgb_to_linear(c):
    c = np.asarray(c, float)
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def linear_to_srgb(c):
    c = np.clip(np.asarray(c, float), 0.0, 1.0)
    return np.where(c <= 0.0031308, c * 12.92, 1.055 * np.power(c, 1 / 2.4) - 0.055)


def style_alpha(style: int) -> float:
    return (style + 1) / 16.0


def polygons_of(geom):
    if geom is None or geom.is_empty:
        return []
    if geom.geom_type == "Polygon":
        return [geom]
    if geom.geom_type in ("MultiPolygon", "GeometryCollection"):
        return [g for g in geom.geoms if g.geom_type == "Polygon" and not g.is_empty]
    return []


def triangulate(poly: Polygon):
    """Earcut a polygon (with holes). Returns (verts Nx2, tris Mx3) wound counter-clockwise."""
    rings = [np.asarray(poly.exterior.coords)[:-1]]
    rings += [np.asarray(r.coords)[:-1] for r in poly.interiors]
    rings = [r for r in rings if len(r) >= 3]
    if not rings:
        return np.zeros((0, 2)), np.zeros((0, 3), np.uint32)
    verts = np.vstack(rings).astype(np.float64)
    ends = np.cumsum([len(r) for r in rings]).astype(np.uint32)
    tris = earcut.triangulate_float64(verts, ends).reshape(-1, 3)
    a, b, c = verts[tris[:, 0]], verts[tris[:, 1]], verts[tris[:, 2]]
    cross = (b[:, 0] - a[:, 0]) * (c[:, 1] - a[:, 1]) - (b[:, 1] - a[:, 1]) * (c[:, 0] - a[:, 0])
    flip = cross < 0
    tris[flip] = tris[flip][:, ::-1]
    return verts, tris[np.abs(cross) > 1e-7]


def ring_of(poly: Polygon) -> np.ndarray:
    return np.asarray(orient(poly, 1.0).exterior.coords)[:-1]


def scaled_ring(ring: np.ndarray, factor: float) -> np.ndarray:
    c = ring.mean(axis=0)
    return c + (ring - c) * factor


class MeshBuf:
    """Accumulates flat-shaded geometry; positions are stored relative to `origin`."""

    def __init__(self, origin=(0.0, 0.0)) -> None:
        self.origin = np.array([origin[0], origin[1], 0.0])
        self.pos, self.nrm, self.uv, self.col, self.idx = [], [], [], [], []
        self.count = 0

    def add(self, pos, nrm, uv, col, tris) -> None:
        if len(tris) == 0:
            return
        self.pos.append(np.asarray(pos, np.float64) - self.origin)
        self.nrm.append(np.asarray(nrm, np.float32))
        self.uv.append(np.asarray(uv, np.float32))
        self.col.append(np.asarray(col, np.float32))
        self.idx.append(np.asarray(tris, np.int64) + self.count)
        self.count += len(pos)

    def add_quads(self, pos, nrm, uv, col) -> None:
        """pos/uv/col shaped (k, 4, c) with corners bottom-left, bottom-right, top-right, top-left."""
        k = len(pos)
        if k == 0:
            return
        base = np.arange(k)[:, None] * 4
        tris = np.vstack([base + np.array([0, 1, 2]), base + np.array([0, 2, 3])])
        self.add(pos.reshape(-1, 3), np.repeat(nrm, 4, axis=0), uv.reshape(-1, 2), col.reshape(-1, 4), tris)

    @property
    def tris(self) -> int:
        return int(sum(len(i) for i in self.idx))

    def save(self, name: str) -> int:
        if not self.idx:
            return 0
        np.savez_compressed(
            MESH_DIR / f"{name}.npz",
            pos=np.vstack(self.pos).astype(np.float32),
            nrm=np.vstack(self.nrm).astype(np.float32),
            uv=np.vstack(self.uv).astype(np.float32),
            col=np.clip(np.vstack(self.col) * 255.0 + 0.5, 0, 255).astype(np.uint8),
            idx=np.vstack(self.idx).astype(np.uint32),
            origin=self.origin.astype(np.float64),
        )
        return self.tris


class SunBaker:
    """Answers 'can this point see the sun?' against the extruded footprints of the 3D buildings."""

    def __init__(self) -> None:
        self.polys: list = []
        self.tops: list = []
        self.tree = None

    def add(self, poly, top_z: float) -> int:
        self.polys.append(poly)
        self.tops.append(top_z)
        return len(self.polys) - 1

    def freeze(self) -> None:
        self.poly_arr = np.array(self.polys, dtype=object)
        self.top_arr = np.array(self.tops, float)
        self.max_top = float(self.top_arr.max()) if len(self.tops) else 0.0
        self.tree = STRtree(self.polys)

    def visible(self, pts: np.ndarray, ignore: int = -1) -> np.ndarray:
        """pts (n, 3) -> bool (n,). `ignore` is the occluder index of the building the points belong to."""
        pts = np.asarray(pts, float).reshape(-1, 3)
        vis = np.ones(len(pts), bool)
        if self.tree is None or len(pts) == 0:
            return vis
        reach = np.clip((self.max_top - pts[:, 2]) / TAN_ELEV, 0.0, 750.0)
        idx = np.flatnonzero(reach > 0.5)
        if len(idx) == 0:
            return vis
        start = pts[idx, :2]
        segs = shapely.linestrings(np.stack([start, start + SUN_2D * reach[idx, None]], axis=1))
        pairs = self.tree.query(segs, predicate="intersects")
        if pairs.shape[1] == 0:
            return vis
        src, occ = pairs
        keep = occ != ignore
        src, occ = src[keep], occ[keep]
        if len(src) == 0:
            return vis
        entry = shapely.distance(shapely.points(start[src]), shapely.intersection(segs[src], self.poly_arr[occ]))
        blocked = self.top_arr[occ] > pts[idx[src], 2] + entry * TAN_ELEV + 0.5
        hit = np.zeros(len(idx), bool)
        hit[src[blocked]] = True
        vis[idx[hit]] = False
        return vis


def face_light(sun_dot, vis, ao):
    """Linear RGB light for faces: sky * ao + sun * max(0, N.S) * visibility. Inputs broadcast to (...,)."""
    sun = np.clip(np.asarray(sun_dot, float), 0.0, 1.0) * np.asarray(vis, float)
    return np.asarray(ao, float)[..., None] * SKY_LIGHT + sun[..., None] * SUN_LIGHT


def baked_colour(tint_srgb, light):
    """sRGB vertex colour for a facade tint under the given linear light."""
    return linear_to_srgb(srgb_to_linear(tint_srgb) * light)


def add_walls(buf: MeshBuf, ring: np.ndarray, z0: float, z1: float, tint, style: int, bay: float, floor: float,
              baker: SunBaker | None = None, self_id: int = -1, grounded: bool = True, split_shadows: bool = False,
              u_offset: float = 0.0) -> None:
    """Vertical facade quads around a counter-clockwise ring, with baked sun / sky light."""
    a = ring
    b = np.roll(ring, -1, axis=0)
    d = b - a
    seg = np.hypot(d[:, 0], d[:, 1])
    keep = seg > 1e-4
    a, b, d, seg = a[keep], b[keep], d[keep], seg[keep]
    k = len(a)
    if k == 0:
        return
    n2 = np.column_stack([d[:, 1], -d[:, 0]]) / seg[:, None]
    sun_dot = (n2 @ SUN_2D) * math.cos(SUN_ELEV)
    mid = (a + b) / 2 + n2 * 0.6
    lit_side = sun_dot > 0.02
    vis_b = np.ones(k, bool)
    vis_t = np.ones(k, bool)
    if baker is not None and lit_side.any():
        zb = min(z0 + 1.0, (z0 + z1) / 2)
        zt = max(z1 - 0.5, (z0 + z1) / 2)
        pts = np.vstack([np.column_stack([mid[lit_side], np.full(lit_side.sum(), zb)]),
                         np.column_stack([mid[lit_side], np.full(lit_side.sum(), zt)])])
        res = baker.visible(pts, self_id)
        vis_b[lit_side], vis_t[lit_side] = res[:lit_side.sum()], res[lit_side.sum():]
    ao_b = 0.70 if grounded else 0.88

    # Where the foot of a wall is in shadow but its top is lit, find the shadow line and split there.
    split_z = np.full(k, np.nan)
    if split_shadows and baker is not None and (z1 - z0) > 14.0:
        need = np.flatnonzero(lit_side & ~vis_b & vis_t)
        if len(need):
            lo = np.full(len(need), z0 + 1.0)
            hi = np.full(len(need), z1 - 0.5)
            for _ in range(5):
                zm = (lo + hi) / 2
                seen = baker.visible(np.column_stack([mid[need], zm]), self_id)
                hi = np.where(seen, zm, hi)
                lo = np.where(seen, lo, zm)
            split_z[need] = (lo + hi) / 2

    cells = float(C.FACADE_CELLS)
    u0 = u_offset + (np.cumsum(seg) - seg) / (bay * cells)
    u1 = u0 + seg / (bay * cells)
    alpha = style_alpha(style)
    nrm = np.column_stack([n2, np.zeros(k)])

    def emit(sel, zlo, zhi, vis_lo, vis_hi, ao_lo, ao_hi):
        m = len(sel)
        if m == 0:
            return
        zlo = np.broadcast_to(np.asarray(zlo, float), (m,))
        zhi = np.broadcast_to(np.asarray(zhi, float), (m,))
        pos = np.empty((m, 4, 3))
        pos[:, 0, :2], pos[:, 1, :2], pos[:, 2, :2], pos[:, 3, :2] = a[sel], b[sel], b[sel], a[sel]
        pos[:, 0, 2] = pos[:, 1, 2] = zlo
        pos[:, 2, 2] = pos[:, 3, 2] = zhi
        uv = np.empty((m, 4, 2))
        uv[:, 0, 0] = uv[:, 3, 0] = u0[sel]
        uv[:, 1, 0] = uv[:, 2, 0] = u1[sel]
        uv[:, 0, 1] = uv[:, 1, 1] = zlo / (floor * cells)
        uv[:, 2, 1] = uv[:, 3, 1] = zhi / (floor * cells)
        light_lo = face_light(sun_dot[sel], vis_lo, ao_lo)
        light_hi = face_light(sun_dot[sel], vis_hi, ao_hi)
        col = np.empty((m, 4, 4))
        col[:, 0, :3] = col[:, 1, :3] = baked_colour(tint, light_lo)
        col[:, 2, :3] = col[:, 3, :3] = baked_colour(tint, light_hi)
        col[:, :, 3] = alpha
        buf.add_quads(pos, nrm[sel], uv, col)

    whole = np.flatnonzero(np.isnan(split_z))
    emit(whole, z0, z1, vis_b[whole], vis_t[whole], np.full(len(whole), ao_b), np.ones(len(whole)))
    cut = np.flatnonzero(~np.isnan(split_z))
    if len(cut):
        zc = split_z[cut]
        frac = (zc - z0) / (z1 - z0)
        ao_mid = ao_b + (1.0 - ao_b) * frac
        emit(cut, z0, zc, np.zeros(len(cut)), np.zeros(len(cut)), np.full(len(cut), ao_b), ao_mid)
        emit(cut, zc, z1, np.ones(len(cut)), np.ones(len(cut)), ao_mid, np.ones(len(cut)))


def add_cap(buf: MeshBuf, poly: Polygon, z: float, tint, baker: SunBaker | None = None, self_id: int = -1,
            uv_scale: float = 0.25) -> None:
    """Horizontal upward-facing cap (roof). Alpha 0 marks 'no facade texture'."""
    verts, tris = triangulate(poly)
    if len(tris) == 0:
        return
    vis = True
    if baker is not None:
        c = poly.representative_point()
        vis = bool(baker.visible(np.array([[c.x, c.y, z + 0.5]]), self_id)[0])
    light = face_light(SUN_VEC[2], vis, 1.0)
    col = np.empty((len(verts), 4))
    col[:, :3] = baked_colour(tint, light)
    col[:, 3] = 0.0
    pos = np.column_stack([verts, np.full(len(verts), z)])
    nrm = np.tile([0.0, 0.0, 1.0], (len(verts), 1))
    buf.add(pos, nrm, verts * uv_scale, col, tris)
