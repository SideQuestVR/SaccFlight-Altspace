"""Which scripts does a prefab use, and where do they come from?

    python prefab_scripts.py <prefab> <source root> [<source root> ...]

Follows nested prefabs (by GUID, through the .meta files found under the source roots), counts every
MonoBehaviour by its script GUID and names the script when a .cs.meta under a source root has that GUID.
Unknown GUIDs are scripts from packages that are not in any of the roots.
"""
from __future__ import annotations

import re
import sys
from collections import Counter
from pathlib import Path

prefab = Path(sys.argv[1])
roots = [Path(p) for p in sys.argv[2:]]

by_guid: dict[str, Path] = {}
for root in roots:
    for meta in root.rglob("*.meta"):
        try:
            head = meta.read_text(encoding="utf-8", errors="replace")[:300]
        except OSError:
            continue
        m = re.search(r"^guid: ([0-9a-f]{32})", head, re.M)
        if m:
            by_guid.setdefault(m.group(1), meta.with_suffix(""))

scripts: Counter[str] = Counter()
seen: set[Path] = set()
nested: Counter[str] = Counter()


def walk(path: Path) -> None:
    if path in seen or not path.exists():
        return
    seen.add(path)
    text = path.read_text(encoding="utf-8", errors="replace")
    for m in re.finditer(r"m_Script: \{fileID: (-?\d+), guid: ([0-9a-f]{32})", text):
        scripts[m.group(2)] += 1
    for m in re.finditer(r"m_SourcePrefab: \{fileID: \d+, guid: ([0-9a-f]{32})", text):
        nested[m.group(1)] += 1
        target = by_guid.get(m.group(1))
        if target is not None and target.suffix == ".prefab":
            walk(target)
        elif target is None:
            print(f"   nested prefab {m.group(1)} is not in the source roots")


walk(prefab)
print(f"{prefab.name}: {len(seen)} prefab file(s) read, {sum(scripts.values())} script components, {len(scripts)} distinct scripts")
known, unknown = [], []
for guid, count in scripts.most_common():
    path = by_guid.get(guid)
    (known if path is not None else unknown).append((count, guid, path))
for count, guid, path in known:
    root = next((r.name for r in roots if r in path.parents), "?")
    print(f"   {count:4d}  {path.name:44s} [{root}]")
print(f"{len(unknown)} scripts not found in the roots:")
for count, guid, _ in unknown:
    print(f"   {count:4d}  {guid}")
