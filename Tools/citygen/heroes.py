"""Original hero towers for the tallest sites.

Each hero keeps the real site's position and height class but uses one of a few invented
archetypes built by lofting cross-section rings. They are deliberately not modelled on any
existing building. `detail` 0 / 1 / 2 gives the three LODs.
"""
from __future__ import annotations

import math

import numpy as np

import config as C
from meshutil import SUN_VEC, baked_colour, face_light, style_alpha

ARCHETYPES = ("needle", "twist", "blade", "twin", "stack", "crown")
PAD_SCALE = 0.98      # landing pad radius as a fraction of the shaft radius
PAD_RIM = 1.5         # pad thickness in metres
PAD_TINT = (0.34, 0.36, 0.38)


def ngon(rx: float, ry: float, n: int, rot: float = 0.0, phase: float = 0.0) -> np.ndarray:
    """Counter-clockwise n-gon with radii rx / ry, rotated by `rot` radians."""
    t = phase + np.arange(n) * (2 * math.pi / n)
    pts = np.column_stack([np.cos(t) * rx, np.sin(t) * ry])
    c, s = math.cos(rot), math.sin(rot)
    return pts @ np.array([[c, s], [-s, c]])


def rect(hx: float, hy: float, rot: float = 0.0) -> np.ndarray:
    pts = np.array([[-hx, -hy], [hx, -hy], [hx, hy], [-hx, hy]], float)
    c, s = math.cos(rot), math.sin(rot)
    return pts @ np.array([[c, s], [-s, c]])


class Skin:
    """Shared look and lighting context for one hero tower."""

    def __init__(self, buf, tint, roof_tint, style, bay, floor, base_z, baker=None, self_id=-1):
        self.buf, self.tint, self.roof_tint, self.style = buf, tint, roof_tint, style
        self.bay, self.floor, self.base_z, self.baker, self.self_id = bay, floor, base_z, baker, self_id

    def visible(self, pts):
        if self.baker is None:
            return np.ones(len(pts), bool)
        return self.baker.visible(pts, self.self_id)


def add_loft(skin: Skin, centre, sections, cap: bool = True, tint=None, roof_tint=None) -> None:
    """Skin consecutive (z, ring) sections. Near-horizontal quads (ledges) carry no facade texture."""
    tint = skin.tint if tint is None else tint
    roof_tint = skin.roof_tint if roof_tint is None else roof_tint
    cells = float(C.FACADE_CELLS)
    cx, cy = centre
    for (z0, r0), (z1, r1) in zip(sections[:-1], sections[1:]):
        k = len(r0)
        a0, b0 = r0, np.roll(r0, -1, axis=0)
        a1, b1 = r1, np.roll(r1, -1, axis=0)
        pos = np.empty((k, 4, 3))
        pos[:, 0, :2], pos[:, 1, :2], pos[:, 2, :2], pos[:, 3, :2] = a0, b0, b1, a1
        pos[:, 0, 2] = pos[:, 1, 2] = z0
        pos[:, 2, 2] = pos[:, 3, 2] = z1
        pos[:, :, 0] += cx
        pos[:, :, 1] += cy
        normal = np.cross(pos[:, 1] - pos[:, 0], pos[:, 3] - pos[:, 0])
        length = np.linalg.norm(normal, axis=1)
        ok = length > 1e-6
        if not ok.any():
            continue
        pos, normal, length = pos[ok], normal[ok], length[ok]
        normal /= length[:, None]
        seg = np.hypot(*(b0 - a0).T)
        u0 = ((np.cumsum(seg) - seg) / (skin.bay * cells))[ok]
        u1 = u0 + (seg / (skin.bay * cells))[ok]
        m = len(pos)
        uv = np.empty((m, 4, 2))
        uv[:, 0, 0] = uv[:, 3, 0] = u0
        uv[:, 1, 0] = uv[:, 2, 0] = u1
        uv[:, 0, 1] = uv[:, 1, 1] = z0 / (skin.floor * cells)
        uv[:, 2, 1] = uv[:, 3, 1] = z1 / (skin.floor * cells)

        ledge_up = normal[:, 2] > 0.7
        ledge_down = normal[:, 2] < -0.7
        wall = ~(ledge_up | ledge_down)
        sun_dot = normal @ SUN_VEC
        low_mid = (pos[:, 0] + pos[:, 1]) / 2 + normal * 0.6
        high_mid = (pos[:, 2] + pos[:, 3]) / 2 + normal * 0.6
        low_mid[:, 2] += np.where(wall, min(1.0, (z1 - z0) / 2), 0.4)
        high_mid[:, 2] -= np.where(wall, min(0.5, (z1 - z0) / 2), -0.4)
        vis = skin.visible(np.vstack([low_mid, high_mid]))
        vis_lo, vis_hi = vis[:m], vis[m:]
        ao_lo = np.where(ledge_down, 0.45, np.where(wall, 0.70 if z0 <= skin.base_z + 0.01 else 0.88, 1.0))
        ao_hi = np.where(ledge_down, 0.45, 1.0)
        light_lo = face_light(sun_dot, vis_lo, ao_lo)
        light_hi = face_light(sun_dot, vis_hi, ao_hi)
        tints = np.where(wall[:, None], np.asarray(tint, float), np.asarray(roof_tint, float))
        col = np.empty((m, 4, 4))
        col[:, 0, :3] = col[:, 1, :3] = baked_colour(tints, light_lo)
        col[:, 2, :3] = col[:, 3, :3] = baked_colour(tints, light_hi)
        col[:, :, 3] = np.where(wall, style_alpha(skin.style), 0.0)[:, None]
        skin.buf.add_quads(pos, normal, uv, col)
    if cap:
        z, ring = sections[-1]
        add_fan_cap(skin, centre, z, ring, roof_tint)


def add_fan_cap(skin: Skin, centre, z: float, ring: np.ndarray, tint) -> None:
    """Upward-facing cap for a convex counter-clockwise ring."""
    k = len(ring)
    if k < 3:
        return
    pts = ring + np.asarray(centre)
    mid = pts.mean(axis=0)
    vis = bool(skin.visible(np.array([[mid[0], mid[1], z + 0.5]]))[0])
    pos = np.column_stack([np.vstack([mid, pts]), np.full(k + 1, z)])
    nrm = np.tile([0.0, 0.0, 1.0], (k + 1, 1))
    col = np.empty((k + 1, 4))
    col[:, :3] = baked_colour(tint, face_light(SUN_VEC[2], vis, 1.0))
    col[:, 3] = 0.0
    i = np.arange(k)
    tris = np.column_stack([np.zeros(k, int), 1 + i, 1 + (i + 1) % k])
    skin.buf.add(pos, nrm, pos[:, :2] * 0.25, col, tris)


def tiers(ring: np.ndarray, z0: float, levels) -> list:
    """Stepped tiers: `levels` is [(top_z, scale), ...]; consecutive tiers are joined by a ledge."""
    sections = []
    z_prev = z0
    for z_top, scale in levels:
        r = ring * scale
        sections.append((z_prev, r))
        sections.append((z_top, r))
        z_prev = z_top
    return sections


def plan_dims(hero: dict) -> tuple[float, float]:
    half_w = float(np.clip(hero["width"] * 0.46, 15.0, 34.0))
    half_l = float(np.clip(hero["length"] * 0.46, half_w, 46.0))
    return half_w, half_l


def build_hero(skin: Skin, hero: dict, kind: str, detail: int, u) -> None:
    """Add one hero tower. `u` is a few per-site random numbers in 0..1."""
    h = hero["height"]
    base_z = skin.base_z
    centre = (hero["x"], hero["y"])
    rot = math.radians(hero["angle_deg"])
    half_w, half_l = plan_dims(hero)
    top = base_z + h

    if kind == "needle":                       # stepped obelisk with chamfered corners and a mast
        n = (8, 4, 4)[detail]
        r = max(half_w, half_l) * 1.15
        ring = ngon(r, r, n, rot, math.pi / n)
        steps = ([(0.30, 1.0), (0.52, 0.82), (0.70, 0.64), (0.84, 0.47), (0.93, 0.31)],
                 [(0.40, 1.0), (0.72, 0.68), (0.93, 0.36)],
                 [(0.55, 0.95), (0.93, 0.45)])[detail]
        add_loft(skin, centre, tiers(ring, base_z, [(base_z + h * f, s) for f, s in steps]))
        mast = ngon(r * 0.07, r * 0.07, 4, rot)
        add_loft(skin, centre, [(base_z + h * 0.93, mast), (top, mast * 0.35)],
                 tint=(0.80, 0.80, 0.82), roof_tint=(0.80, 0.80, 0.82))

    elif kind == "twist":                      # rounded square turning as it rises, slight taper
        n = (12, 8, 4)[detail]
        levels = (16, 6, 2)[detail]
        total = math.radians(95.0 + 40.0 * u[0])
        sections = []
        for i in range(levels + 1):
            f = i / levels
            ring = ngon(half_l * 1.05, half_w * 1.05, n, rot + total * f, math.pi / n) * (1.0 - 0.22 * f)
            sections.append((base_z + h * f, ring))
        add_loft(skin, centre, sections)

    elif kind == "blade":                      # slim ellipse that bulges, then leans to an offset tip
        n = (16, 8, 6)[detail]
        profile = ([(0.0, 1.0, 0.0), (0.18, 1.06, 0.0), (0.40, 1.08, 0.02), (0.62, 0.98, 0.08),
                    (0.80, 0.78, 0.20), (0.92, 0.52, 0.36), (1.0, 0.22, 0.52)],
                   [(0.0, 1.0, 0.0), (0.40, 1.08, 0.02), (0.80, 0.78, 0.20), (1.0, 0.22, 0.52)],
                   [(0.0, 1.0, 0.0), (0.70, 0.90, 0.10), (1.0, 0.25, 0.50)])[detail]
        axis = np.array([math.cos(rot), math.sin(rot)])
        sections = [(base_z + h * f, ngon(half_l * 1.1 * s, half_w * 0.8 * s, n, rot) + axis * half_l * shift)
                    for f, s, shift in profile]
        add_loft(skin, centre, sections)

    elif kind == "twin":                       # two slim slabs of unequal height joined by a high bridge
        axis = np.array([math.cos(rot), math.sin(rot)])
        gap = half_l * 0.62
        slab = rect(half_l * 0.34, half_w * 0.85, rot)
        for sign, frac in ((-1.0, 1.0), (1.0, 0.84)):
            c = (centre[0] + axis[0] * gap * sign, centre[1] + axis[1] * gap * sign)
            levels = [(base_z + h * frac * 0.9, 1.0), (base_z + h * frac, 0.7)] if detail < 2 else [(base_z + h * frac, 1.0)]
            add_loft(skin, c, tiers(slab, base_z, levels))
        if detail < 2:
            bridge = rect(gap * 1.02, half_w * 0.55, rot)
            add_loft(skin, centre, [(base_z + h * 0.60, bridge * 0.02), (base_z + h * 0.60, bridge),
                                    (base_z + h * 0.66, bridge)])

    elif kind == "stack":                      # shifted, slightly rotated boxes piled up
        count = (5, 3, 2)[detail]
        axis = np.array([math.cos(rot), math.sin(rot)])
        side = np.array([-axis[1], axis[0]])
        for i in range(count):
            f0, f1 = i / count, (i + 1) / count
            shift = axis * half_l * 0.16 * (1 if i % 2 else -1) + side * half_w * 0.14 * (1 if (i // 2) % 2 else -1)
            scale = 1.0 - 0.09 * i * (5 / count)
            ring = rect(half_l * 0.9 * scale, half_w * 0.9 * scale, rot + math.radians(6.0 * i * (5 / count)))
            sections = [(base_z + h * f0, ring), (base_z + h * f1, ring)]
            if i:
                sections.insert(0, (base_z + h * f0, ring * 0.02))      # underside where it overhangs
            add_loft(skin, (centre[0] + shift[0], centre[1] + shift[1]), sections)

    else:                                      # "crown": round shaft, flared ring near the top, landing pad
        n = (16, 8, 6)[detail]
        r = (half_w + half_l) * 0.5
        shaft = ngon(r, r, n, rot)
        rim = PAD_RIM / h
        profile = ([(0.0, 1.0), (0.86, 0.94), (0.90, 1.22), (0.94, 1.22), (0.95, 0.80), (1.0, 0.80),
                    (1.0, PAD_SCALE), (1.0 + rim, PAD_SCALE)],
                   [(0.0, 1.0), (0.88, 0.95), (0.92, 1.20), (1.0, 0.80), (1.0, PAD_SCALE), (1.0 + rim, PAD_SCALE)],
                   [(0.0, 1.0), (1.0 + rim, 0.88)])[detail]
        add_loft(skin, centre, [(base_z + h * f, shaft * s) for f, s in profile], roof_tint=PAD_TINT)


def occluder(hero: dict, kind: str):
    """(ring, height fraction) used as this hero's shadow caster for the light bake."""
    rot = math.radians(hero["angle_deg"])
    half_w, half_l = plan_dims(hero)
    if kind in ("needle", "crown"):
        r = max(half_w, half_l) if kind == "needle" else (half_w + half_l) * 0.5
        return ngon(r, r, 8, rot) + np.array([hero["x"], hero["y"]]), (0.62 if kind == "needle" else 1.0)
    grow = {"twist": 0.95, "blade": 0.9, "twin": 1.0, "stack": 0.9}[kind]
    frac = {"twist": 1.0, "blade": 0.8, "twin": 0.9, "stack": 1.0}[kind]
    return rect(half_l * grow, half_w * grow, rot) + np.array([hero["x"], hero["y"]]), frac


def helipad_sites(heroes: list, base_z: float) -> list:
    """Landing pads the gameplay layer can use (the flat tops of the 'crown' towers)."""
    pads = []
    for h in heroes:
        if h.get("kind") == "crown":
            half_w, half_l = plan_dims(h)
            pads.append({"x": h["x"], "y": h["y"], "z": base_z + h["height"] + PAD_RIM,
                         "radius": (half_w + half_l) * 0.5 * PAD_SCALE})
    return pads
