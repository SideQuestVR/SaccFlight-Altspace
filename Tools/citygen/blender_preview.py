"""Render a few preview stills of city.blend for visual checks (not part of the export pipeline).

    blender --background --factory-startup city.blend --python blender_preview.py
Writes out/render_<name>.jpg.
"""
from __future__ import annotations

import math
import sys
from pathlib import Path

import bpy
from mathutils import Vector

HERE = Path(__file__).resolve().parent
if str(HERE) not in sys.path:
    sys.path.insert(0, str(HERE))
import config as C  # noqa: E402

VIEWS = {
    # name: (camera position, look-at target, lens mm)
    "palm_marina": ((-17500.0, -4500.0, 900.0), (-10200.0, -9400.0, 150.0), 28.0),
    "downtown": ((-400.0, -600.0, 420.0), (2939.0, 2460.0, 330.0), 30.0),
    "creek_airport": ((3500.0, 1500.0, 2200.0), (11000.0, 8500.0, 0.0), 26.0),
    "overview": ((-9000.0, -24000.0, 14000.0), (-2000.0, -2000.0, 0.0), 30.0),
    "low_marina": ((-12200.0, -10900.0, 160.0), (-9700.0, -9450.0, 180.0), 24.0),
    "towers_close": ((-9150.0, -10050.0, 190.0), (-9750.0, -9450.0, 170.0), 26.0),
    "downtown_close": ((2250.0, 1650.0, 150.0), (2939.0, 2460.0, 260.0), 24.0),
    "street_shadows": ((2000.0, 3600.0, 900.0), (2939.0, 2460.0, 0.0), 35.0),
}


def main() -> None:
    scene = bpy.context.scene
    for name in ("Cube", "Light"):
        obj = bpy.data.objects.get(name)
        if obj:
            bpy.data.objects.remove(obj, do_unlink=True)

    sun_data = bpy.data.lights.new("PreviewSun", "SUN")
    sun_data.energy = 4.0
    sun = bpy.data.objects.new("PreviewSun", sun_data)
    sun.rotation_euler = (math.radians(52.0), 0.0, math.radians(-125.0))
    scene.collection.objects.link(sun)

    world = scene.world or bpy.data.worlds.new("PreviewWorld")
    scene.world = world
    if world.node_tree:
        bg = world.node_tree.nodes.get("Background")
        if bg:
            bg.inputs["Color"].default_value = (0.62, 0.74, 0.86, 1.0)
            bg.inputs["Strength"].default_value = 1.1

    cam_data = bpy.data.cameras.new("PreviewCam")
    cam_data.clip_start, cam_data.clip_end = 1.0, 120000.0
    cam = bpy.data.objects.new("PreviewCam", cam_data)
    scene.collection.objects.link(cam)
    scene.camera = cam

    for engine in ("BLENDER_EEVEE", "BLENDER_EEVEE_NEXT", "BLENDER_WORKBENCH"):
        try:
            scene.render.engine = engine
            break
        except TypeError:
            continue
    if hasattr(scene, "eevee"):
        try:
            scene.eevee.taa_render_samples = 16
        except Exception:
            pass
    scene.render.resolution_x, scene.render.resolution_y = 1600, 900
    scene.render.image_settings.file_format = "JPEG"
    scene.render.image_settings.quality = 88
    scene.view_settings.view_transform = "Standard"

    wanted = [a for a in sys.argv[sys.argv.index("--") + 1:]] if "--" in sys.argv else list(VIEWS)
    for name in wanted:
        pos, target, lens = VIEWS[name]
        cam.location = pos
        direction = Vector(target) - Vector(pos)
        cam.rotation_euler = direction.to_track_quat("-Z", "Y").to_euler()
        cam_data.lens = lens
        scene.render.filepath = str(C.OUT / f"render_{name}.jpg")
        bpy.ops.render.render(write_still=True)
        print("rendered", name, "with", scene.render.engine)


if __name__ == "__main__":
    main()
