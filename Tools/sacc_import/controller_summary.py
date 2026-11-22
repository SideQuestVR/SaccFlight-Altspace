"""Summarise a Unity .controller: layers, states, the clip each state plays, the parameter that drives it,
and (with --clips) the objects and properties every clip animates.

    python controller_summary.py <path/to/X.controller> [--clips] [--assets <Assets/Sacc>]
"""
import os
import re
import sys


def documents(text):
    """Yield (class_id, file_id, body) for each YAML document in a Unity asset."""
    for match in re.finditer(r"--- !u!(\d+) &(-?\d+)[^\n]*\n(.*?)(?=\n--- !u!|\Z)", text, re.S):
        yield match.group(1), match.group(2), match.group(3)


def guid_index(root):
    index = {}
    for folder, _, files in os.walk(root):
        for name in files:
            if name.endswith(".meta"):
                with open(os.path.join(folder, name), encoding="utf-8", errors="ignore") as handle:
                    found = re.search(r"guid: ([0-9a-f]{32})", handle.read(400))
                if found:
                    index[found.group(1)] = os.path.join(folder, name[:-5])
    return index


def clip_bindings(path):
    with open(path, encoding="utf-8", errors="ignore") as handle:
        text = handle.read()
    out = []
    for kind in ("m_RotationCurves", "m_EulerCurves", "m_PositionCurves", "m_ScaleCurves"):
        block = re.search(kind + r":(.*?)\n  m_[A-Z]", text, re.S)
        if block:
            for found in re.finditer(r"\n    path: ([^\n]*)", block.group(1)):
                out.append((found.group(1).strip(), kind[2:-6].lower()))
    block = re.search(r"m_FloatCurves:(.*?)\n  m_PPtrCurves", text, re.S)
    if block:
        for found in re.finditer(r"attribute: ([^\n]*)\n    path: ([^\n]*)\n    classID: (\d+)", block.group(1)):
            out.append((found.group(2).strip(), found.group(1).strip()))
    return sorted(set(out))


def main():
    path = sys.argv[1]
    want_clips = "--clips" in sys.argv
    assets = sys.argv[sys.argv.index("--assets") + 1] if "--assets" in sys.argv else os.path.join(os.path.dirname(path), "..", "..")
    with open(path, encoding="utf-8", errors="ignore") as handle:
        text = handle.read()
    docs = {fid: (cls, body) for cls, fid, body in documents(text)}
    guids = guid_index(assets) if True else {}

    def motion_name(body):
        found = re.search(r"m_Motion: \{fileID: (-?\d+)(?:, guid: ([0-9a-f]{32}))?", body)
        if not found or found.group(1) == "0":
            return "(none)", None
        if found.group(2):
            target = guids.get(found.group(2))
            return (os.path.basename(target) if target else "guid " + found.group(2)), target
        cls, tree = docs.get(found.group(1), ("", ""))
        if cls == "206":        # blend tree
            param = re.search(r"m_BlendParameter: (\S+)", tree)
            children = [os.path.basename(guids.get(g, g)) for g in re.findall(r"m_Motion: \{fileID: \d+, guid: ([0-9a-f]{32})", tree)]
            return f"blend[{param.group(1) if param else '?'}]({', '.join(children)})", [guids.get(g) for g in re.findall(r"m_Motion: \{fileID: \d+, guid: ([0-9a-f]{32})", tree)]
        return "local " + found.group(1), None

    used = {}
    controller = next(body for cls, body in docs.values() if cls == "91")
    for layer in re.finditer(r"m_Name: ([^\n]*)\n    m_StateMachine: \{fileID: (-?\d+)\}", controller):
        print(f"LAYER {layer.group(1)}")
        machine = docs[layer.group(2)][1]
        default = re.search(r"m_DefaultState: \{fileID: (-?\d+)\}", machine)
        for state_id in re.findall(r"m_State: \{fileID: (-?\d+)\}", machine):
            body = docs[state_id][1]
            name = re.search(r"m_Name: ([^\n]*)", body).group(1)
            motion, target = motion_name(body)
            time_param = re.search(r"m_TimeParameterActive: 1", body) and re.search(r"m_TimeParameter: (\S+)", body)
            extras = []
            if time_param:
                extras.append("time=" + time_param.group(1))
            if default and default.group(1) == state_id:
                extras.append("DEFAULT")
            print(f"  STATE {name}: {motion} {' '.join(extras)}")
            for item in (target if isinstance(target, list) else [target]):
                if item:
                    used[os.path.basename(item)] = item
            for transition_id in re.findall(r"- \{fileID: (-?\d+)\}", re.search(r"m_Transitions:(.*?)\n  m_[A-Z]", body, re.S).group(1) if re.search(r"m_Transitions:(.*?)\n  m_[A-Z]", body, re.S) else ""):
                tbody = docs.get(transition_id, ("", ""))[1]
                conditions = re.findall(r"m_ConditionMode: (\d+)\n\s+m_ConditionEvent: ([^\n]*)\n\s+m_EventTreshold: ([^\n]*)", tbody)
                dest = re.search(r"m_DstState: \{fileID: (-?\d+)\}", tbody)
                dest_name = re.search(r"m_Name: ([^\n]*)", docs[dest.group(1)][1]).group(1) if dest and dest.group(1) in docs else "exit"
                modes = {"1": "true", "2": "false", "3": ">", "4": "<", "6": "==", "7": "!="}
                print("      -> " + dest_name + "  when " + ", ".join(f"{event} {modes.get(mode, mode)} {threshold}" if mode in "3467" else f"{event} {modes.get(mode, mode)}" for mode, event, threshold in conditions))
    if want_clips:
        for name, clip in sorted(used.items()):
            print(f"CLIP {name}")
            for binding_path, attribute in clip_bindings(clip):
                print(f"    {binding_path or '(root)'} : {attribute}")


if __name__ == "__main__":
    main()
