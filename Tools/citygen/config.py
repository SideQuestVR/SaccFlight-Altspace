"""Shared constants for the city pipeline (fetch -> process -> Blender build -> Unity import)."""
from pathlib import Path

ROOT = Path(__file__).resolve().parent
CACHE = ROOT / "cache"          # raw OpenStreetMap downloads (gitignored)
OUT = ROOT / "out"              # processed intermediates handed to Blender (gitignored)
UNITY_OUT = ROOT.parent.parent / "Assets" / "City" / "Generated"

# Geographic box (degrees). Roughly 40 km east-west by 32 km north-south.
SOUTH, WEST, NORTH, EAST = 25.03, 55.02, 25.32, 55.42

# Local origin: chosen so the marina, palm, downtown, creek and airport site are all
# within ~15 km of (0,0), keeping 32-bit float precision at or below ~1 mm.
ORIGIN_LAT, ORIGIN_LON = 25.175, 55.245

# World scale (1.0 = real size). Single knob if cockpit jitter at the map edge is objectionable.
SCALE = 1.0

# Playable world rectangle in local metres (x east, y north): exactly 40 x 32 km so the
# 2 / 4 / 8 km tile grids all divide it evenly.
WORLD_X0, WORLD_Y0, WORLD_X1, WORLD_Y1 = -22400.0, -16000.0, 17600.0, 16000.0

# Output tiling (metres, after projection).
BUILDING_TILE = 2000.0
GROUND_TILE = 4000.0
SKYLINE_TILE = 8000.0

# Heights (metres). Sea surface is y = 0; land sits slightly above it. Sea geometry is cut
# around the land (never underneath it) so nothing depends on fine depth precision.
LAND_H = 1.0
COAST_WALL_BOTTOM = -2.0
OUTER_EXTENT = 90000.0     # how far the surrounding sea / desert planes reach past the box

# Ground texture resolution per 4 km tile.
GROUND_TEX_PX = 2048

# Geometry budget knobs.
HERO_COUNT = 40            # tallest sites replaced by original hero towers
HERO_MIN_SPACING = 160.0
MIN_3D_HEIGHT = 18.0       # buildings at least this tall get individual geometry
MIN_3D_AREA = 1800.0       # ...as do low buildings with at least this footprint (malls, sheds)
LOD2_MIN_HEIGHT = 60.0     # far skyline keeps only buildings at least this tall
COLLIDER_MIN_HEIGHT = 25.0

PROJ4 = (
    f"+proj=tmerc +lat_0={ORIGIN_LAT} +lon_0={ORIGIN_LON} +k=1 +x_0=0 +y_0=0 "
    "+ellps=WGS84 +units=m +no_defs"
)

# Fixed sun used for all baked lighting (vertex colours and painted ground shadows).
# SUN_TOWARD is the horizontal unit vector pointing from the ground towards the sun (the sun
# sits in the south-west); SUN_ELEV_DEG is its elevation above the horizon.
SUN_TOWARD = (-0.830, -0.558)
SUN_ELEV_DEG = 50.0
SUN_RGB = (1.00, 0.95, 0.86)       # warm direct light
SKY_RGB = (0.84, 0.91, 1.00)       # cool ambient light
SUN_STRENGTH = 0.64
SKY_STRENGTH = 0.52

# Facade texture array: FACADE_STYLES layers, each tile covers FACADE_CELLS bays x floors.
FACADE_STYLES = 8
FACADE_CELLS = 8
FACADE_PX = 512
