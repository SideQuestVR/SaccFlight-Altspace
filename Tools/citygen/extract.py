"""Stage 1: pull the layers the city needs out of the regional OpenStreetMap extract.

Input : cache/uae-latest.osm.pbf  (c) OpenStreetMap contributors, ODbL
Output: out/layers.pkl  -> {layer: {"geom": [shapely geometry in local metres], "tags": [dict]}}

Coordinates are projected to a local transverse-Mercator frame centred on config.ORIGIN_*,
x = east, y = north, in metres (later mapped to Unity X / Z).
"""
from __future__ import annotations

import pickle
import sys
import time

import numpy as np
import osmium
import shapely
from pyproj import Transformer
from shapely import wkb as shp_wkb
from shapely.geometry import LineString

import config as C

MARGIN = 0.02  # degrees kept around the box so features crossing the edge stay whole
S, W, N, E = C.SOUTH - MARGIN, C.WEST - MARGIN, C.NORTH + MARGIN, C.EAST + MARGIN

KEEP_TAGS = (
    "building", "building:part", "height", "building:levels", "min_height", "roof:shape",
    "highway", "bridge", "tunnel", "layer", "lanes", "width", "oneway",
    "railway", "aeroway", "ref", "surface",
    "natural", "water", "waterway", "landuse", "leisure", "man_made", "place",
)

ROAD_CLASSES = {
    "motorway", "trunk", "primary", "secondary", "tertiary",
    "motorway_link", "trunk_link", "primary_link", "secondary_link", "tertiary_link",
    "residential", "unclassified", "living_street",
}
RAIL_CLASSES = {"subway", "light_rail", "monorail", "tram", "rail"}
WATER_LANDUSE = {"basin", "reservoir"}
GREEN_LEISURE = {"park", "golf_course", "pitch", "garden", "stadium", "track"}
GREEN_LANDUSE = {"grass", "forest", "recreation_ground", "cemetery", "farmland", "meadow"}
BUILT_LANDUSE = {"residential", "commercial", "industrial", "retail", "construction", "railway", "port"}
SHORE_NATURAL = {"beach", "sand", "wetland", "scrub"}
SHORE_MANMADE = {"pier", "breakwater", "groyne"}
AERO_AREAS = {"apron", "terminal", "helipad", "hangar"}
AERO_LINES = {"runway", "taxiway"}


def slim(tags) -> dict:
    return {k: tags[k] for k in KEEP_TAGS if k in tags}


class Extractor(osmium.SimpleHandler):
    def __init__(self) -> None:
        super().__init__()
        self.wkb = osmium.geom.WKBFactory()
        self.layers: dict[str, dict[str, list]] = {}
        self.bad = 0

    def add(self, layer: str, geom, tags: dict) -> None:
        bucket = self.layers.setdefault(layer, {"geom": [], "tags": []})
        bucket["geom"].append(geom)
        bucket["tags"].append(tags)

    # ---- linear features -------------------------------------------------------------
    def way(self, w) -> None:
        t = w.tags
        layer = None
        if t.get("natural") == "coastline":
            layer = "coast"
        elif t.get("highway") in ROAD_CLASSES:
            layer = "roads"
        elif t.get("railway") in RAIL_CLASSES:
            layer = "rail"
        elif t.get("aeroway") in AERO_LINES:
            layer = "aero_lines"
        elif t.get("waterway") in ("canal", "river", "stream") and not w.is_closed():
            layer = "water_lines"
        elif t.get("man_made") in SHORE_MANMADE and not w.is_closed():
            layer = "shore_lines"
        if layer is None:
            return
        try:
            pts = [(n.lon, n.lat) for n in w.nodes]
        except osmium.InvalidLocationError:
            self.bad += 1
            return
        if len(pts) < 2 or not any(W <= x <= E and S <= y <= N for x, y in pts):
            return
        self.add(layer, LineString(pts), slim(t))

    # ---- polygon features (closed ways and multipolygon relations, already assembled) --
    def area(self, a) -> None:
        t = a.tags
        layer = None
        if "building" in t and t.get("building") != "no":
            layer = "buildings"
        elif "building:part" in t and t.get("building:part") != "no":
            layer = "building_parts"
        elif t.get("natural") == "water" or t.get("landuse") in WATER_LANDUSE or t.get("waterway") in ("dock", "riverbank"):
            layer = "water"
        elif t.get("natural") in SHORE_NATURAL:
            layer = "shore"
        elif t.get("leisure") in GREEN_LEISURE or t.get("landuse") in GREEN_LANDUSE:
            layer = "green"
        elif t.get("leisure") == "marina":
            layer = "marina"
        elif t.get("man_made") in SHORE_MANMADE:
            layer = "shore_structs"
        elif t.get("aeroway") in AERO_AREAS:
            layer = "aero_areas"
        elif t.get("landuse") in BUILT_LANDUSE:
            layer = "landuse"
        if layer is None:
            return
        try:
            inside = False
            for ring in a.outer_rings():
                for n in ring:
                    if W <= n.lon <= E and S <= n.lat <= N:
                        inside = True
                        break
                if inside:
                    break
            if not inside:
                return
            geom = shp_wkb.loads(self.wkb.create_multipolygon(a), hex=True)
        except Exception:
            self.bad += 1
            return
        self.add(layer, geom, slim(t))


def main() -> None:
    src = C.CACHE / "uae-latest.osm.pbf"
    if not src.exists():
        sys.exit(f"missing {src}")
    t0 = time.time()
    ex = Extractor()
    ex.apply_file(str(src), locations=True, idx="flex_mem")
    print(f"read pbf in {time.time() - t0:.0f}s, skipped {ex.bad} broken features", flush=True)

    tf = Transformer.from_crs("EPSG:4326", C.PROJ4, always_xy=True)

    def project(coords: np.ndarray) -> np.ndarray:
        x, y = tf.transform(coords[:, 0], coords[:, 1])
        return np.column_stack([x, y]) * C.SCALE

    out = {}
    for name, bucket in sorted(ex.layers.items()):
        geoms = shapely.transform(np.array(bucket["geom"], dtype=object), project)
        out[name] = {"geom": list(geoms), "tags": bucket["tags"]}
        print(f"  {name:15s} {len(geoms):7d}", flush=True)

    C.OUT.mkdir(parents=True, exist_ok=True)
    with open(C.OUT / "layers.pkl", "wb") as fh:
        pickle.dump(out, fh, protocol=pickle.HIGHEST_PROTOCOL)
    print(f"wrote {C.OUT / 'layers.pkl'} in {time.time() - t0:.0f}s total", flush=True)


if __name__ == "__main__":
    main()
