"""Bring the VAU v320neo airliner (MIT, (c) 2023 VRChat Aerospace University, https://github.com/vrcau/VAU320)
into this Unity project as data, the same way import_sacc.py brings in SaccFlightAndVehicles.

    Tools/citygen/.venv/Scripts/python.exe Tools/sacc_import/import_vau320.py            (assets + stand-ins)
    Tools/citygen/.venv/Scripts/python.exe Tools/sacc_import/import_vau320.py scripts    (stand-ins only)

What it does
  1. Starting from the aircraft's main prefab it follows every reference (nested prefabs, materials, textures,
     models, animations, sounds, fonts ...) and copies only those files, WITH their .meta files so the GUIDs
     still resolve, from Tools/vau320 into Assets/VAU320. The package holds 371 MB (abandoned models, layered
     paint sources, alternative liveries); the aircraft itself uses a fraction of it.
  2. For every script of the package it writes a data-only stand-in: a MonoBehaviour with the same GUID, class
     name and serialized fields and no code, so Unity can load the settings stored in the prefab. The originals
     are UdonSharp (VRChat) scripts and cannot compile here. Stand-ins never ship: the builder removes them.

The aircraft is built on SaccFlightAndVehicles; its Sacc components resolve to the stand-ins in Assets/Sacc.
Scripts from its other dependencies (EsnyaSFAddons, YuxiFlightInstruments, Virtual-CNS, the VRChat SDK) are not
brought in: their components show up as missing scripts and the builder strips them.
"""
from __future__ import annotations

import re
import shutil
import sys
from pathlib import Path

import import_sacc as sacc

ROOT = Path(__file__).resolve().parents[2]
PACKAGE = ROOT / "Tools" / "vau320" / "Packages" / "com.yuxiaviation.vau320neo"
SRC = PACKAGE / "Runtime"
DST = ROOT / "Assets" / "VAU320"
START = ["Prefab/v320neo_main 1217.prefab"]
TEXT_SUFFIXES = {".prefab", ".mat", ".controller", ".overridecontroller", ".anim", ".asset", ".spriteatlas", ".physicmaterial",
                 ".rendertexture", ".mask", ".playable", ".mixer", ".fontsettings", ".guiskin", ".cubemap", ".flare"}
NEVER = {".cs", ".asmdef", ".unity", ".blend", ".blend1"}
GUID = re.compile(r"guid: ([0-9a-f]{32})")


def guid_of(meta: Path) -> str | None:
    m = re.search(r"^guid: ([0-9a-f]{32})", meta.read_text(encoding="utf-8", errors="replace")[:400], re.M)
    return m.group(1) if m else None


def closure() -> tuple[list[Path], set[str]]:
    """Every file under SRC that the start prefabs need, and the GUIDs they reference that are not under SRC."""
    by_guid = {}
    for meta in SRC.rglob("*.meta"):
        target = meta.with_suffix("")
        if target.is_file():
            g = guid_of(meta)
            if g:
                by_guid[g] = target
    wanted: list[Path] = []
    seen: set[Path] = set()
    outside: set[str] = set()
    queue = [SRC / s for s in START]
    while queue:
        path = queue.pop()
        if path in seen:
            continue
        seen.add(path)
        if path.suffix.lower() in NEVER:
            continue
        wanted.append(path)
        texts = [path.with_name(path.name + ".meta")]            # a model's .meta remaps its materials to other assets
        if path.suffix.lower() in TEXT_SUFFIXES:
            texts.append(path)
        for text in texts:
            if not text.exists():
                continue
            for g in set(GUID.findall(text.read_text(encoding="utf-8", errors="replace"))):
                target = by_guid.get(g)
                if target is None:
                    outside.add(g)
                elif target not in seen:
                    queue.append(target)
    return wanted, outside


def copy_assets() -> None:
    wanted, outside = closure()
    if DST.exists():
        for child in DST.iterdir():
            if child.name not in ("Scripts", "Scripts.meta"):
                shutil.rmtree(child) if child.is_dir() else child.unlink()
    DST.mkdir(parents=True, exist_ok=True)
    total = 0
    by_kind: dict[str, list[int]] = {}
    for src in wanted:
        target = DST / src.relative_to(SRC)
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(src, target)
        meta = src.with_name(src.name + ".meta")
        if meta.exists():
            shutil.copy2(meta, target.with_name(target.name + ".meta"))
        size = src.stat().st_size
        total += size
        entry = by_kind.setdefault(src.suffix.lower(), [0, 0])
        entry[0] += 1
        entry[1] += size
    shutil.copy2(PACKAGE.parents[1] / "LICENSE", DST / "LICENSE.txt")
    print(f"copied {len(wanted)} files, {total / 1048576:.0f} MB: " +
          ", ".join(f"{count} {kind} ({size / 1048576:.0f} MB)" for kind, (count, size) in sorted(by_kind.items(), key=lambda kv: -kv[1][1])))
    print(f"{len(outside)} referenced GUIDs are not in the package (Unity built-ins, Sacc, other packages)")


def write_standins() -> None:
    shutil.rmtree(DST / "Scripts", ignore_errors=True)
    scripts = [p for p in SRC.rglob("*.cs") if "Editor" not in p.parts]
    parsed = {p: sacc.parse_script(p) for p in scripts}
    # classes a field may refer to: this package's own and Sacc's (both exist here as stand-ins), by full name
    full_name: dict[str, str] = {}
    clashes: set[str] = set()
    sacc_scripts = [p for p in sacc.SRC.rglob("*.cs") if "Editor" not in p.parts]
    for path, (namespace, cls, _, _) in list(parsed.items()) + [(p, sacc.parse_script(p)) for p in sacc_scripts]:
        if not cls or not path.with_name(path.name + ".meta").exists():
            continue
        name = f"global::{namespace}.{cls}" if namespace else f"global::{cls}"
        if cls in full_name and full_name[cls] != name:
            clashes.add(cls)
        full_name[cls] = name
    all_enums = {e for info in parsed.values() for e in info[3]}
    stubs = skipped = 0
    for path, (namespace, cls, fields, _) in parsed.items():
        meta = path.with_name(path.name + ".meta")
        if not cls or not meta.exists():
            continue
        lines = ["// Data-only stand-in generated by Tools/sacc_import/import_vau320.py from VAU v320neo",
                 "// (MIT, (c) 2023 VRChat Aerospace University). Same GUID and serialized fields as the original script, no code.",
                 "using UnityEngine;", "using UnityEngine.UI;", "using TMPro;", ""]
        indent = ""
        if namespace:
            lines += [f"namespace {namespace}", "{"]
            indent = "    "
        lines += [f"{indent}public class {cls} : MonoBehaviour", indent + "{"]
        seen = set()
        for type_name, name, init in fields:
            if name in seen or name == cls:
                continue
            seen.add(name)
            base = type_name.rstrip("[]")
            suffix = type_name[len(base):]
            if base in all_enums:
                base, init = "int", None
            elif sacc.COMPONENT_LIKE.match(base):
                base, init = "MonoBehaviour", None
            elif base in full_name and base not in clashes:
                base, init = full_name[base], None
            elif base in sacc.UNITY_TYPES:
                base = {"Text": "UnityEngine.UI.Text", "Image": "UnityEngine.UI.Image", "Slider": "UnityEngine.UI.Slider",
                        "Toggle": "UnityEngine.UI.Toggle", "Object": "UnityEngine.Object"}.get(base, base)
            else:
                skipped += 1
                continue
            if init is not None and (suffix or not sacc.LITERAL.match(init)):
                init = None
            lines.append(f"{indent}    public {base}{suffix} {name}{' = ' + init if init else ''};")
        lines += [indent + "}"]
        if namespace:
            lines += ["}"]
        target = DST / "Scripts" / path.relative_to(SRC)
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text("\n".join(lines) + "\n", encoding="utf-8")
        shutil.copy2(meta, target.with_name(target.name + ".meta"))
        stubs += 1
    print(f"wrote {stubs} script stand-ins ({skipped} fields of unknown type left out; {len(scripts) - stubs} files hold no class of their own name)")


if __name__ == "__main__":
    if not SRC.exists():
        raise SystemExit(f"{SRC} missing: git clone --depth 1 https://github.com/vrcau/VAU320 Tools/vau320")
    if not (len(sys.argv) > 1 and sys.argv[1] == "scripts"):
        copy_assets()
    write_standins()
