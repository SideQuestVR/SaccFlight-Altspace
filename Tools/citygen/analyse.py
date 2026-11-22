"""Quick look at the extracted layers (not part of the pipeline)."""
import pickle, re
import numpy as np
import shapely
from shapely.ops import linemerge, unary_union
import config as C

L = pickle.load(open(C.OUT / "layers.pkl", "rb"))

def num(v):
    if v is None: return None
    m = re.match(r"\s*(-?\d+(?:\.\d+)?)", str(v))
    return float(m.group(1)) if m else None

b = L["buildings"]; g = np.array(b["geom"], dtype=object)
area = shapely.area(g)
h = np.array([num(t.get("height")) or np.nan for t in b["tags"]])
lv = np.array([num(t.get("building:levels")) or np.nan for t in b["tags"]])
est = np.where(~np.isnan(h), h, np.where(~np.isnan(lv), lv * 3.6 + 2, np.nan))
print("buildings", len(g), "| with height", int((~np.isnan(h)).sum()), "| with levels", int((~np.isnan(lv)).sum()), "| either", int((~np.isnan(est)).sum()))
for thr in (20, 50, 100, 150, 200, 300):
    print(f"  est height >= {thr:3d} m: {int((est >= thr).sum())}")
print("  tallest:", np.sort(est[~np.isnan(est)])[-12:].round(0))
print("  footprint area m2 percentiles 10/50/90/99:", np.percentile(area, [10, 50, 90, 99]).round(0), "| total km2", round(area.sum() / 1e6, 1))
print("  building tag top:", sorted(((v, k) for k, v in __import__('collections').Counter(t.get('building') for t in b['tags']).items()), reverse=True)[:8])
verts = shapely.get_num_coordinates(g)
print("  vertices total", int(verts.sum()), "median", int(np.median(verts)))
xs = shapely.bounds(g)
print("  extent x", xs[:, 0].min().round(0), xs[:, 2].max().round(0), "y", xs[:, 1].min().round(0), xs[:, 3].max().round(0))

c = L["coast"]["geom"]
merged = linemerge(unary_union(c))
parts = list(merged.geoms) if merged.geom_type == "MultiLineString" else [merged]
rings = [p for p in parts if p.is_ring]
opens = [p for p in parts if not p.is_ring]
print("coast ways", len(c), "-> merged", len(parts), "| closed rings", len(rings), "| open lines", len(opens))
print("  open line lengths km:", sorted((round(p.length / 1000, 1) for p in opens), reverse=True)[:10])
print("  ring areas km2 (largest):", sorted((round(shapely.Polygon(p).area / 1e6, 2) for p in rings), reverse=True)[:10])
print("  coast vertices:", sum(len(p.coords) for p in parts))

r = L["roads"]
import collections
cnt = collections.Counter(t.get("highway") for t in r["tags"])
ln = collections.Counter()
for geom, t in zip(r["geom"], r["tags"]): ln[t.get("highway")] += geom.length
print("roads by class (count, km):", {k: (cnt[k], round(ln[k] / 1000)) for k in sorted(cnt, key=lambda k: -ln[k])})
print("  bridges:", sum(1 for t in r["tags"] if t.get("bridge") not in (None, "no")), "| tunnels:", sum(1 for t in r["tags"] if t.get("tunnel") not in (None, "no")))
print("rail:", collections.Counter(t.get("railway") for t in L["rail"]["tags"]), "bridge:", sum(1 for t in L["rail"]["tags"] if t.get("bridge") not in (None, "no")))
print("aero lines:", collections.Counter(t.get("aeroway") for t in L["aero_lines"]["tags"]))
for geom, t in zip(L["aero_lines"]["geom"], L["aero_lines"]["tags"]):
    if t.get("aeroway") == "runway":
        cx, cy = geom.centroid.x, geom.centroid.y
        print(f"  runway ref={t.get('ref')} len={geom.length:.0f} m width={t.get('width')} centre=({cx:.0f},{cy:.0f}) surface={t.get('surface')}")
print("water polys:", len(L["water"]["geom"]), "area km2", round(sum(p.area for p in L["water"]["geom"]) / 1e6, 1))
print("shore:", collections.Counter(t.get("natural") for t in L["shore"]["tags"]))
print("green:", collections.Counter(t.get("leisure") or t.get("landuse") for t in L["green"]["tags"]).most_common(6))
