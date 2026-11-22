"""Hold the head at a point looking at a target and save what the headset sees.

    python look.py <label> <hx> <hy> <hz> <tx> <ty> <tz>     head at h, looking at t (world space)
    python look.py <label>                                    just save the current view

Pictures go to Tools/quest/out/look_<label>.jpg. The pose override is released afterwards.
"""
import base64
import sys
import time

import harness
import mcp_client

label = sys.argv[1]
h = harness.Harness()
if len(sys.argv) >= 8:
    head = tuple(float(v) for v in sys.argv[2:5])
    target = tuple(float(v) for v in sys.argv[5:8])
    h.call("pose_override", {"mode": "static", "space": "world", "head": harness.pose_towards(head, target)})
    time.sleep(1.2)
for item in h.client.call("take_snapshot", {"width": 1280, "height": 720, "quality": 88}):
    if item.get("type") == "image":
        mcp_client.OUT.mkdir(exist_ok=True)
        path = mcp_client.OUT / f"look_{label}.jpg"
        path.write_bytes(base64.b64decode(item["data"]))
        print(path)
if len(sys.argv) >= 8:
    h.call("pose_override", {"mode": "off"})
