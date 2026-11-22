"""Headset half of a two-client check, as the WATCHER: stand at a point, keep looking at a vehicle another
client is flying, and record what this client has for it - position, speed, who simulates it, whether it has a
remote rider - with a picture every few seconds (the only evidence of its rotors on a client that cannot read
an animator).

    python watch_remote.py "SH-2" <stand x> <stand z> <seconds> [label prefix]

Pictures go to ./out/watch_<prefix>_<n>.jpg; the timeline is printed with wall-clock times.
"""
from __future__ import annotations

import base64
import sys
import time
from datetime import datetime

import harness
import mcp_client

vehicle = sys.argv[1]
stand = (float(sys.argv[2]), float(sys.argv[3]))
seconds = float(sys.argv[4])
prefix = sys.argv[5] if len(sys.argv) > 5 else "w"
h = harness.Harness()
h.call("pose_override", {"mode": "off"})
h.call("teleport", {"position": {"x": stand[0], "y": 1.3, "z": stand[1]}, "yaw": 0})
time.sleep(1.5)
head = (stand[0], 2.9, stand[1])
start = time.time()
shot = 0
last_picture = -10.0
while time.time() - start < seconds:
    o = h.call("synced_objects", {"action": "list", "name": vehicle})["objects"][0]
    p = o["position"]
    h.call("pose_override", {"mode": "static", "space": "world", "head": harness.pose_towards(head, (p[0], p[1] + 2.0, p[2]))})
    now = time.time() - start
    note = ""
    if now - last_picture >= 2.5:
        time.sleep(0.25)
        for item in h.client.call("take_snapshot", {"width": 960, "height": 540, "quality": 80}):
            if item.get("type") == "image":
                mcp_client.OUT.mkdir(exist_ok=True)
                (mcp_client.OUT / f"watch_{prefix}_{shot:02d}.jpg").write_bytes(base64.b64decode(item["data"]))
                note = f"  picture {shot:02d}"
                shot += 1
        last_picture = now
    print(f"{datetime.now().strftime('%H:%M:%S')}  at ({p[0]:.0f}, {p[1]:.1f}, {p[2]:.0f}) speed {o.get('speed', 0):.1f} "
          f"owned here {o['owned_here']} kinematic {o.get('kinematic')} remote rider {bool(o.get('remote_rider'))}{note}", flush=True)
    time.sleep(0.6)
h.call("pose_override", {"mode": "off"})
print(datetime.now().strftime('%H:%M:%S'), "done")
