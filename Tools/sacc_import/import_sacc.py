"""Bring SaccFlightAndVehicles (MIT, (c) 2021 Sacchan-VRC) into this Unity project as data.

    Tools/citygen/.venv/Scripts/python.exe Tools/sacc_import/import_sacc.py

What it does
  1. Copies the package's assets (models, textures, materials, animations, audio, prefabs, shaders) from
     Tools/sacc-full into Assets/Sacc, WITH their .meta files, so every reference inside the prefabs still
     resolves (Unity references assets by the GUID in the .meta).
  2. For every script in the package, writes a data-only stand-in: a MonoBehaviour with the same GUID, class
     name and serialized fields, and no code. The originals are UdonSharp (VRChat) scripts and cannot compile
     here; the stand-ins let Unity load each prefab's settings (flight tuning, references to seats, wheels,
     animators, sounds ...) so the aircraft builder can read them. Stand-ins never ship: the builder removes
     them from everything it puts in the scene, and the flight logic itself runs as a Visual Scripting graph.

Re-running replaces Assets/Sacc completely; `import_sacc.py scripts` only regenerates the script stand-ins.
"""
from __future__ import annotations

import re
import shutil
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / "Tools" / "sacc-full"
DST = ROOT / "Assets" / "Sacc"
COPY_DIRS = ["AssetFiles", "Prefabs", "Textures", "Shaders"]
COPY_FILES = ["LICENSE.md", "README.md"]
SKIP_SUFFIXES = {".cs", ".asmdef", ".unity"}

UNITY_TYPES = {
    "float", "int", "bool", "string", "double", "long", "uint", "byte", "short", "char",
    "Vector2", "Vector3", "Vector4", "Quaternion", "Color", "Color32", "Vector2Int", "Vector3Int", "Rect", "Bounds",
    "Transform", "GameObject", "Rigidbody", "Animator", "AudioSource", "AudioClip", "ParticleSystem", "Collider",
    "BoxCollider", "SphereCollider", "CapsuleCollider", "MeshCollider", "WheelCollider", "AnimationCurve", "LayerMask",
    "Material", "Renderer", "MeshRenderer", "SkinnedMeshRenderer", "Camera", "Light", "Texture", "Texture2D",
    "RenderTexture", "Sprite", "KeyCode", "TrailRenderer", "LineRenderer", "Mesh", "MeshFilter", "Gradient",
    "RectTransform", "Canvas", "Text", "Image", "Slider", "Toggle", "TextMeshPro", "TextMeshProUGUI", "TMP_Text",
    "ConstantForce", "Joint", "ConfigurableJoint", "HingeJoint", "AnimationClip", "RuntimeAnimatorController",
    "Component", "Behaviour", "MonoBehaviour", "Object", "PhysicMaterial", "PhysicsMaterial", "ReflectionProbe",
    "AudioReverbZone", "AudioLowPassFilter", "Projector", "Font",
}
# VRChat / Udon types that are components in the scene: referenced objects simply stay unresolved
COMPONENT_LIKE = re.compile(r"^(UdonSharpBehaviour|UdonBehaviour|VRC\w*|VRC\.[\w.]+|IUdonEventReceiver)$")
LITERAL = re.compile(r'^\s*(-?\d+(\.\d+)?[fFdDuUlL]*|true|false|"[^"\\]*"|-?\.\d+[fF]?|new\s+Vector[234]\s*\([-\d.,fF\s]*\)|new\s+Color\s*\([-\d.,fF\s]*\)|Vector3\.(zero|one|up|forward|right)|KeyCode\.\w+)\s*$')
DECL = re.compile(r"""^\s*
    (?P<mods>(?:(?:public|private|protected|internal|static|const|readonly|new|volatile)\s+)*)
    (?P<type>[A-Za-z_][\w.]*(?:<[^>;=(]+>)?(?:\s*\[\s*\])*)\s+
    (?P<names>[A-Za-z_]\w*(?:\s*=\s*[^;,]+)?(?:\s*,\s*[A-Za-z_]\w*(?:\s*=\s*[^;,]+)?)*)\s*;\s*$""", re.X)


SINGLE = re.compile(r"""^\s*
    (?P<mods>(?:(?:public|private|protected|internal|static|const|readonly|new|volatile)\s+)*)
    (?P<type>[A-Za-z_][\w.]*(?:<[^>;=(]+>)?(?:\s*\[\s*\])*)\s+
    (?P<name>[A-Za-z_]\w*)\s*=\s*(?P<init>[^;]+);\s*$""", re.X)


ARRAY_OPEN = re.compile(r"""^\s*
    (?P<mods>(?:(?:public|private|protected|internal|static|const|readonly|new|volatile)\s+)*)
    (?P<type>[A-Za-z_][\w.]*\s*\[\s*\])\s+
    (?P<name>[A-Za-z_]\w*)\s*=\s*(?:new\s+[\w.]+\s*\[\s*\]\s*)?\{""", re.X)


def strip_attributes(statement: str) -> str:
    """Remove leading [Attribute(...)] groups, including ones whose arguments contain brackets ([Tooltip("[N]")])."""
    text = statement.lstrip()
    while text.startswith("["):
        depth, in_string, i = 0, False, 0
        while i < len(text):
            c = text[i]
            if c == '"' and (i == 0 or text[i - 1] != chr(92)):
                in_string = not in_string
            elif not in_string and c == "[":
                depth += 1
            elif not in_string and c == "]":
                depth -= 1
                if depth == 0:
                    break
            i += 1
        if i >= len(text):
            return text
        text = text[i + 1:].lstrip()
    return text


def strip_comments(text: str) -> str:
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    out = []
    for line in text.splitlines():
        in_string, i, cut = False, 0, len(line)
        while i < len(line) - 1:
            c = line[i]
            if c == '"' and (i == 0 or line[i - 1] != "\\"):
                in_string = not in_string
            elif not in_string and c == "/" and line[i + 1] == "/":
                cut = i
                break
            i += 1
        out.append(line[:cut])
    return "\n".join(out)


def parse_script(path: Path):
    """Returns (namespace, class name, [(type, name, initialiser or None)], enum names declared in the file)."""
    text = strip_comments(path.read_text(encoding="utf-8-sig", errors="replace"))
    namespace = (re.search(r"\bnamespace\s+([\w.]+)", text) or [None, None])[1]
    match = re.search(r"\bclass\s+(" + re.escape(path.stem) + r")\b[^{]*\{", text)
    if not match:
        return namespace, None, [], []
    enums = re.findall(r"\benum\s+(\w+)", text)
    fields, depth, pending, body = [], 0, "", text[match.end():]
    statement = ""
    for line in body.splitlines():
        if line.lstrip().startswith("#"):       # #region / #if lines are not part of any statement
            continue
        if depth == 0:
            stripped = line.strip()
            if stripped.startswith("[") and stripped.endswith("]") and "(" not in stripped.split("]")[-1]:
                pending += " " + stripped
            elif stripped:
                statement = (statement + " " + stripped).strip()
                declaration = strip_attributes(statement)
                if statement.endswith(";") and "(" not in declaration.split("=")[0]:
                    attrs = pending + " " + statement[:len(statement) - len(declaration)]
                    single = None if DECL.match(declaration) else SINGLE.match(declaration)
                    if single:          # one field whose initialiser has commas in it, e.g. new Vector2(20f, 20f)
                        mods = single.group("mods").split()
                        serialised = ("public" in mods or "SerializeField" in attrs) and not ({"static", "const", "readonly"} & set(mods))
                        if serialised and "NonSerialized" not in attrs:
                            fields.append((single.group("type").replace(" ", ""), single.group("name"), single.group("init").strip()))
                    m = DECL.match(declaration)
                    if m:
                        mods = m.group("mods").split()
                        serialised = ("public" in mods or "SerializeField" in attrs) and not ({"static", "const", "readonly"} & set(mods))
                        if serialised and "NonSerialized" not in attrs:
                            for part in m.group("names").split(","):
                                name, _, init = part.partition("=")
                                fields.append((m.group("type").replace(" ", ""), name.strip(), init.strip() or None))
                    statement, pending = "", ""
                elif statement.endswith(";") or statement.endswith("}") or "{" in statement:
                    # an array field whose values are listed over several lines: public float[] steps = { 0, 10, ... };
                    listed = ARRAY_OPEN.match(strip_attributes(statement))
                    if listed:
                        mods = listed.group("mods").split()
                        attrs = pending + " " + statement
                        if ("public" in mods or "SerializeField" in attrs) and not ({"static", "const", "readonly"} & set(mods)) and "NonSerialized" not in attrs:
                            fields.append((listed.group("type").replace(" ", ""), listed.group("name"), None))
                    statement, pending = "", ""
        depth += line.count("{") - line.count("}")
        if depth < 0:
            break
    return namespace, path.stem, fields, enums


def main() -> None:
    if not SRC.exists():
        raise SystemExit(f"{SRC} missing: git clone --depth 1 https://github.com/Sacchan-VRC/SaccFlightAndVehicles Tools/sacc-full")
    scripts_only = len(sys.argv) > 1 and sys.argv[1] == "scripts"      # regenerate the stand-ins, leave the assets
    if scripts_only:
        shutil.rmtree(DST / "Scripts", ignore_errors=True)
    else:
        if DST.exists():
            shutil.rmtree(DST)
        DST.mkdir(parents=True)
    copied = 0
    for name in ([] if scripts_only else COPY_DIRS):
        for src in (SRC / name).rglob("*"):
            rel = src.relative_to(SRC)
            target = DST / rel
            if src.is_dir():
                target.mkdir(parents=True, exist_ok=True)
                continue
            real = src.name[:-5] if src.name.endswith(".meta") else src.name
            if Path(real).suffix.lower() in SKIP_SUFFIXES:
                continue
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(src, target)
            copied += 1
        meta = SRC / (name + ".meta")
        if meta.exists():
            shutil.copy2(meta, DST / meta.name)
    for name in ([] if scripts_only else COPY_FILES):
        shutil.copy2(SRC / name, DST / name)

    # ---- data-only stand-ins for the scripts ------------------------------------------------------------
    scripts = [p for p in SRC.rglob("*.cs") if "Editor" not in p.parts]
    parsed = {p: parse_script(p) for p in scripts}
    classes = {info[1] for info in parsed.values() if info[1]}
    all_enums = {e for info in parsed.values() for e in info[3]}
    stubs = skipped_fields = 0
    for path, (namespace, cls, fields, _) in parsed.items():
        meta = path.with_name(path.name + ".meta")
        if not cls or not meta.exists():
            continue
        lines = ["// Data-only stand-in generated by Tools/sacc_import/import_sacc.py from SaccFlightAndVehicles",
                 "// (MIT, (c) 2021 Sacchan-VRC). Same GUID and serialized fields as the original script, no code.",
                 "using UnityEngine;", "using UnityEngine.UI;", "using TMPro;", ""]
        indent = ""
        if namespace:
            lines += [f"namespace {namespace}", "{"]
            indent = "    "
        lines += [f"{indent}public class {cls} : MonoBehaviour", indent + "{"]
        seen = set()
        for type_name, name, init in fields:
            if name in seen:
                continue
            seen.add(name)
            base = type_name.rstrip("[]")
            suffix = type_name[len(base):]
            if base in all_enums:
                base, init = "int", None
            elif COMPONENT_LIKE.match(base):
                base, init = "MonoBehaviour", None
            elif base not in UNITY_TYPES and base not in classes:
                skipped_fields += 1
                continue
            if init is not None and (suffix or not LITERAL.match(init)):
                init = None
            lines.append(f"{indent}    public {base}{suffix} {name}{' = ' + init if init else ''};")
        lines += [indent + "}"]
        if namespace:
            lines += ["}"]
        target = DST / "Scripts" / path.relative_to(SRC / "Scripts") if (SRC / "Scripts") in path.parents else DST / "Scripts" / path.name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text("\n".join(lines) + "\n", encoding="utf-8")
        shutil.copy2(meta, target.with_name(target.name + ".meta"))
        stubs += 1
    print(f"copied {copied} asset files; wrote {stubs} script stand-ins ({skipped_fields} fields of unknown type left out)")


if __name__ == "__main__":
    main()
