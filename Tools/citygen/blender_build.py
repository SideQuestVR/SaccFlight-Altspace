"""Stage 4 (runs inside Blender): assemble the city from out/meshes and export tiles for Unity.

Live session (through the Blender MCP):
    import sys, importlib
    sys.path.insert(0, r"C:\\workspace\\FlightWorld\\Tools\\citygen")
    import blender_build; importlib.reload(blender_build)
    blender_build.build()                 # whole city: scene + FBX tiles + city.blend
    blender_build.build(export=False)     # scene only, for looking around

Headless:
    blender --background --factory-startup --python blender_build.py -- [all|scene]

Scene layout: collection "City" with child collections Ground, Sea, Outer, Buildings_LOD0/1/2 and
Colliders. Objects sit at their real positions (x east, y north, z up, metres). Each tile is
exported with its origin at the tile corner; Unity places it from city_layout.json.

All lighting is baked (vertex colours on buildings, painted shadows on the ground), so the preview
materials are emissive: what you see here is what the unlit Unity shaders show.
"""
from __future__ import annotations

import json
import sys
import time
from pathlib import Path

import bpy
import numpy as np

HERE = Path(__file__).resolve().parent
if str(HERE) not in sys.path:
    sys.path.insert(0, str(HERE))
import config as C  # noqa: E402

MESH_DIR = C.OUT / "meshes"
FBX_DIR = C.UNITY_OUT / "Meshes"
GROUND_TEX_DIR = C.UNITY_OUT / "Ground"


def lin(c: float) -> float:
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


# ----------------------------------------------------------------------------------------------
def collection(name: str, parent=None):
    col = bpy.data.collections.get(name)
    if col is None:
        col = bpy.data.collections.new(name)
        (parent or bpy.context.scene.collection).children.link(col)
    return col


def clear_city() -> None:
    root = bpy.data.collections.get("City")
    if root is not None:
        for col in [root] + list(root.children_recursive):
            for obj in list(col.objects):
                mesh = obj.data
                bpy.data.objects.remove(obj, do_unlink=True)
                if mesh and mesh.users == 0:
                    bpy.data.meshes.remove(mesh)
        for col in list(root.children_recursive) + [root]:
            bpy.data.collections.remove(col)
    for mat in list(bpy.data.materials):
        if mat.name.startswith("M_City_") and mat.users == 0:
            bpy.data.materials.remove(mat)


def new_emissive(name: str):
    """Fresh material whose surface is a single Emission shader; returns (material, nodes, links, emission)."""
    old = bpy.data.materials.get(name)
    if old:
        bpy.data.materials.remove(old)
    mat = bpy.data.materials.new(name)
    if mat.node_tree is None:
        mat.use_nodes = True
    nodes, links = mat.node_tree.nodes, mat.node_tree.links
    nodes.clear()
    out = nodes.new("ShaderNodeOutputMaterial")
    emission = nodes.new("ShaderNodeEmission")
    links.new(emission.outputs["Emission"], out.inputs["Surface"])
    return mat, nodes, links, emission


def material_flat(name: str, srgb):
    mat, _, _, emission = new_emissive(name)
    emission.inputs["Color"].default_value = (lin(srgb[0]), lin(srgb[1]), lin(srgb[2]), 1.0)
    mat.diffuse_color = (*srgb, 1.0)
    return mat


def material_building():
    """Facade texture picked by the style stored in vertex alpha, multiplied by the baked vertex colour."""
    mat, nodes, links, emission = new_emissive("M_City_Building")
    attr = nodes.new("ShaderNodeVertexColor")
    attr.layer_name = "Col"
    uv = nodes.new("ShaderNodeUVMap")
    code = nodes.new("ShaderNodeMath")
    code.operation = "MULTIPLY"
    code.inputs[1].default_value = 16.0
    links.new(attr.outputs["Alpha"], code.inputs[0])

    colour = None
    for i in range(C.FACADE_STYLES):
        path = C.OUT / f"facade_{i}.png"
        if not path.exists():
            continue
        tex = nodes.new("ShaderNodeTexImage")
        tex.image = bpy.data.images.load(str(path), check_existing=True)
        tex.image.alpha_mode = "CHANNEL_PACKED"      # alpha is a glass mask, not transparency
        tex.extension = "REPEAT"
        links.new(uv.outputs["UV"], tex.inputs["Vector"])
        match = nodes.new("ShaderNodeMath")
        match.operation = "COMPARE"
        match.inputs[1].default_value = float(i + 1)
        match.inputs[2].default_value = 0.5
        links.new(code.outputs[0], match.inputs[0])
        mix = nodes.new("ShaderNodeMix")
        mix.data_type = "RGBA"
        links.new(match.outputs[0], mix.inputs[0])
        if colour is None:
            mix.inputs[6].default_value = (1.0, 1.0, 1.0, 1.0)      # roofs / ledges: plain
        else:
            links.new(colour, mix.inputs[6])
        links.new(tex.outputs["Color"], mix.inputs[7])
        colour = mix.outputs[2]

    tint = nodes.new("ShaderNodeMix")
    tint.data_type = "RGBA"
    tint.blend_type = "MULTIPLY"
    tint.inputs[0].default_value = 1.0
    links.new(attr.outputs["Color"], tint.inputs[6])
    if colour is not None:
        links.new(colour, tint.inputs[7])
    else:
        tint.inputs[7].default_value = (1.0, 1.0, 1.0, 1.0)
    links.new(tint.outputs[2], emission.inputs["Color"])
    return mat


def material_ground(key: str):
    mat, nodes, links, emission = new_emissive(f"M_City_Ground_{key}")
    path = GROUND_TEX_DIR / f"gnd_{key}.jpg"
    if path.exists():
        tex = nodes.new("ShaderNodeTexImage")
        tex.image = bpy.data.images.load(str(path), check_existing=True)
        tex.image.reload()
        tex.extension = "EXTEND"
        links.new(tex.outputs["Color"], emission.inputs["Color"])
    return mat


def material_sea():
    """Sea colour from the shallow-water tint map, addressed by world position."""
    mat, nodes, links, emission = new_emissive("M_City_Sea")
    path = GROUND_TEX_DIR / "watertint.png"
    if not path.exists():
        emission.inputs["Color"].default_value = (lin(0.06), lin(0.23), lin(0.38), 1.0)
        return mat
    geo = nodes.new("ShaderNodeNewGeometry")
    mapping = nodes.new("ShaderNodeMapping")
    mapping.vector_type = "POINT"
    w, h = C.WORLD_X1 - C.WORLD_X0, C.WORLD_Y1 - C.WORLD_Y0
    mapping.inputs["Location"].default_value = (-C.WORLD_X0 / w, -C.WORLD_Y0 / h, 0.0)
    mapping.inputs["Scale"].default_value = (1.0 / w, 1.0 / h, 1.0)
    tex = nodes.new("ShaderNodeTexImage")
    tex.image = bpy.data.images.load(str(path), check_existing=True)
    tex.image.reload()
    tex.extension = "EXTEND"
    links.new(geo.outputs["Position"], mapping.inputs["Vector"])
    links.new(mapping.outputs["Vector"], tex.inputs["Vector"])
    links.new(tex.outputs["Color"], emission.inputs["Color"])
    return mat


def make_object(name: str, npz_path: Path, col, mat, with_colour: bool):
    data = np.load(npz_path)
    pos, idx, uv = data["pos"], data["idx"].astype(np.int32), data["uv"]
    mesh = bpy.data.meshes.new(name)
    n_tri = len(idx)
    mesh.vertices.add(len(pos))
    mesh.vertices.foreach_set("co", pos.astype(np.float32).ravel())
    mesh.loops.add(n_tri * 3)
    mesh.loops.foreach_set("vertex_index", idx.ravel())
    mesh.polygons.add(n_tri)
    mesh.polygons.foreach_set("loop_start", np.arange(0, n_tri * 3, 3, dtype=np.int32))
    mesh.polygons.foreach_set("loop_total", np.full(n_tri, 3, dtype=np.int32))
    flat = idx.ravel()
    uv_layer = mesh.uv_layers.new(name="UVMap")
    uv_layer.data.foreach_set("uv", uv[flat].astype(np.float32).ravel())
    if with_colour:
        colour = mesh.color_attributes.new(name="Col", type="BYTE_COLOR", domain="CORNER")
        colour.data.foreach_set("color_srgb", (data["col"][flat].astype(np.float32) / 255.0).ravel())
    mesh.materials.append(mat)
    mesh.update()
    mesh.validate()
    obj = bpy.data.objects.new(name, mesh)
    ox, oy, oz = (float(v) for v in data["origin"])
    obj.location = (ox, oy, oz)
    col.objects.link(obj)
    return obj


def export_group(objects, filepath: Path) -> None:
    """Export objects sharing one tile origin, with that origin moved to (0,0,0) in the file."""
    saved = [obj.location.copy() for obj in objects]
    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.location = (0.0, 0.0, 0.0)
        obj.select_set(True)
        if not obj.select_get():
            raise RuntimeError(f"{obj.name} could not be selected for export (hidden collection?)")
    bpy.context.view_layer.objects.active = objects[0]
    bpy.ops.export_scene.fbx(
        filepath=str(filepath), use_selection=True, object_types={"MESH"},
        apply_scale_options="FBX_SCALE_UNITS", axis_forward="-Z", axis_up="Y", bake_space_transform=True,
        mesh_smooth_type="OFF", use_mesh_modifiers=False, colors_type="SRGB",
        add_leaf_bones=False, bake_anim=False, use_custom_props=False, path_mode="STRIP", embed_textures=False,
    )
    for obj, loc in zip(objects, saved):
        obj.location = loc
        obj.select_set(False)


# ----------------------------------------------------------------------------------------------
def build(export: bool = True, save_blend: bool = True, only: set | None = None) -> dict:
    """Build the scene. `only` may hold tile keys like {"12_8"} to limit building tiles (spikes)."""
    t0 = time.time()
    with open(C.OUT / "manifest.json", encoding="utf-8") as fh:
        manifest = json.load(fh)
    clear_city()
    root = collection("City")
    cols = {name: collection(name, root) for name in
            ("Ground", "Sea", "Outer", "Buildings_LOD0", "Buildings_LOD1", "Buildings_LOD2", "Colliders")}

    m_building = material_building()
    m_sea = material_sea()
    m_desert = material_flat("M_City_Desert", (0.80, 0.72, 0.56))
    m_col = material_flat("M_City_Collider", (1.0, 0.2, 0.8))
    if export:
        FBX_DIR.mkdir(parents=True, exist_ok=True)
        for stale in FBX_DIR.glob("*.fbx"):          # tiles that no longer exist must not linger
            stale.unlink()
    exported, tri_total = 0, 0

    def finish(objects, fbx_name):
        nonlocal exported, tri_total
        tri_total += sum(len(o.data.polygons) for o in objects)
        if export and objects:
            export_group(objects, FBX_DIR / f"{fbx_name}.fbx")
            exported += 1

    for key in sorted(manifest["ground"]):
        obj = make_object(f"gnd_{key}", MESH_DIR / f"gnd_{key}.npz", cols["Ground"], material_ground(key), False)
        finish([obj], f"gnd_{key}")
    for key in sorted(manifest["sea"]):
        obj = make_object(f"sea_{key}", MESH_DIR / f"sea_{key}.npz", cols["Sea"], m_sea, False)
        finish([obj], f"sea_{key}")
    outer = []
    for name, mat in (("outer_land", m_desert), ("outer_sea", m_sea)):
        if (MESH_DIR / f"{name}.npz").exists():
            outer.append(make_object(name, MESH_DIR / f"{name}.npz", cols["Outer"], mat, False))
    finish(outer, "outer")

    lod_cols = {"lod0": "Buildings_LOD0", "lod1": "Buildings_LOD1", "lod2": "Buildings_LOD2", "col": "Colliders"}
    for key in sorted(manifest["buildings"]):
        if only is not None and key not in only:
            continue
        group = []
        for lod, col_name in lod_cols.items():
            path = MESH_DIR / f"bld_{key}_{lod}.npz"
            if path.exists():
                suffix = "COL" if lod == "col" else lod.upper()
                group.append(make_object(f"bld_{key}_{suffix}", path, cols[col_name],
                                         m_col if lod == "col" else m_building, lod != "col"))
        finish(group, f"bld_{key}")

    # Hide the simplified and collision meshes only now: objects in hidden collections cannot be
    # selected, and the FBX export above works on the selection.
    for hidden in ("Buildings_LOD1", "Buildings_LOD2", "Colliders"):
        cols[hidden].hide_viewport = cols[hidden].hide_render = True

    if save_blend:
        bpy.ops.wm.save_as_mainfile(filepath=str(HERE / "city.blend"), check_existing=False)
    result = {"objects": sum(len(c.objects) for c in cols.values()), "triangles": tri_total,
              "fbx_files": exported, "seconds": round(time.time() - t0, 1)}
    print("blender_build:", result)
    return result


if __name__ == "__main__":
    args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    mode = args[0] if args else "all"
    build(export=(mode == "all"))
