"""One client watches another player: logs where it sees them (position, speed) several times a second,
and keeps turning to look at them and taking snapshots.

    python observe.py <observer client: quest|editor> <part of the watched player's name> <seconds> [snapshot prefix]

Log: out/<prefix>_log.txt ; snapshots: out/<prefix>_NN.jpg (with the time each was taken in the log).
"""
from __future__ import annotations

import base64
import math
import sys
import threading
import time

import mcp_client
from two_clients import connect

observer, who, seconds = sys.argv[1], sys.argv[2].lower(), float(sys.argv[3])
prefix = sys.argv[4] if len(sys.argv) > 4 else "observe"
t0 = time.time()
lines: list[str] = []
latest = {"p": None}
lock = threading.Lock()


def note(text: str) -> None:
    with lock:
        lines.append(text)
    print(text, flush=True)


def watched(h):
    for u in h.call("list_users").get("users", []):
        if not u["is_local"] and who in u["name"].lower():
            p = u["position"]
            return p["x"], p["y"], p["z"]
    return None


def sample() -> None:
    h = connect(observer)
    last = None
    while time.time() - t0 < seconds:
        now = time.time()
        p = watched(h)
        if p is None:
            note(f"{now - t0:6.2f}s  not in the room")
        else:
            latest["p"] = p
            if last and p != last[0]:
                speed = math.dist(p, last[0]) / (now - last[1])
                note(f"{now - t0:6.2f}s  ({p[0]:9.1f}, {p[1]:7.1f}, {p[2]:9.1f})  {speed:6.1f} m/s")
                last = (p, now)
            elif last is None:
                last = (p, now)
            else:
                note(f"{now - t0:6.2f}s  ({p[0]:9.1f}, {p[1]:7.1f}, {p[2]:9.1f})  no change")
        time.sleep(0.15)


def camera() -> None:
    h = connect(observer)
    index = 0
    while time.time() - t0 < seconds:
        p = latest["p"]
        if p is not None:
            try:
                h.call("move", {"look_at": {"x": p[0], "y": p[1] + 1.0, "z": p[2]}, "timeout_s": 2})
                for item in h.client.call("take_snapshot", {"width": 1280, "height": 720, "quality": 80}):
                    if item.get("type") == "image":
                        mcp_client.OUT.mkdir(exist_ok=True)
                        (mcp_client.OUT / f"{prefix}_{index:02d}.jpg").write_bytes(base64.b64decode(item["data"]))
                        note(f"{time.time() - t0:6.2f}s  snapshot {prefix}_{index:02d}.jpg")
                        index += 1
            except Exception as error:
                note(f"{time.time() - t0:6.2f}s  camera: {type(error).__name__}: {error}")
        time.sleep(0.6)


threads = [threading.Thread(target=sample), threading.Thread(target=camera)]
for t in threads:
    t.start()
for t in threads:
    t.join()
(mcp_client.OUT / f"{prefix}_log.txt").write_text("\n".join(lines) + "\n", encoding="utf-8")
