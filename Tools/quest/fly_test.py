"""Headset flight check: with the player already sitting in an aircraft's pilot seat, fly it - throttle
up, take off, climb, then dive into the ground - and log what the player experiences.

The controls go in through the world page, exactly as the desktop keyboard does (the page forwards
control values to the aircraft graph). The app's test harness cannot stand in for thumbsticks: the
controller events a world receives come from the real controllers only. Snapshots go to ./out/fly_<label>.jpg, the log to ./out/fly_log.txt.

    python fly_test.py [rotate_speed m/s, default 38] [climb seconds, default 9]

Expected ending: the aircraft explodes and the player is returned to the arrival point.
"""
from __future__ import annotations

import base64
import math
import sys
import time

import mcp_client
from harness import Harness

ARRIVAL = (14804.7, 7042.1)             # the world's arrival point (x, z)
ROTATE = float(sys.argv[1]) if len(sys.argv) > 1 else 38.0
CLIMB_SECONDS = float(sys.argv[2]) if len(sys.argv) > 2 else 9.0
h = Harness()
lines: list[str] = []


def note(text: str) -> None:
    print(text, flush=True)
    lines.append(text)


def snap(label: str) -> None:
    content = h.client.call("take_snapshot", {"width": 1280, "height": 720, "quality": 85})
    for item in content:
        if item.get("type") == "image":
            mcp_client.OUT.mkdir(exist_ok=True)
            (mcp_client.OUT / f"fly_{label}.jpg").write_bytes(base64.b64decode(item["data"]))
            note(f"    snapshot fly_{label}.jpg")


def control(throttle: float, pitch: float) -> None:
    """throttle: lever movement -1..1 per second scale; pitch: -1 nose up .. +1 nose down."""
    h.call("page_eval", {"js": "(function(){const s=BS.Scene.GetInstance();"
                               f"s.SendToVisualScripting('fw_throttle',{throttle});"
                               f"s.SendToVisualScripting('fw_pitch',{pitch});return 'ok';}})()"})


def position():
    p = h.call("get_state")["player"]["position"]
    return p["x"], p["y"], p["z"]


start = position()
t0 = time.time()
last, last_t = start, t0
phase = "throttle up"
control(1, 0)                             # throttle lever forward
climb_started = dive_started = None
snapped: set[str] = set()
end_reason = "timed out"
while time.time() - t0 < 150:
    time.sleep(0.45)
    now = time.time()
    p = position()
    dt = now - last_t
    step = math.dist(p, last)
    speed = step / dt
    t = now - t0
    altitude = p[1] - start[1]
    note(f"{t:5.1f}s  {phase:12s} speed {speed:6.1f} m/s  altitude {altitude:6.1f} m  from start {math.dist(p, start):7.1f} m")
    if phase in ("climb", "dive") and math.dist((p[0], p[2]), ARRIVAL) < 10 and math.dist((last[0], last[2]), ARRIVAL) > 100:
        end_reason = "back at the arrival point (the aircraft was destroyed)"
        break
    last, last_t = p, now

    if phase == "throttle up" and t > 2.6:
        phase = "takeoff roll"
        control(0, 0)
    elif phase == "takeoff roll" and speed > ROTATE:
        phase, climb_started = "climb", now
        control(0, -0.45)                 # nose up
    elif phase == "climb":
        if altitude > 8 and "airborne" not in snapped:
            snapped.add("airborne")
            snap("airborne")
        if now - climb_started > 3.0:
            control(0, -0.12)
        if now - climb_started > CLIMB_SECONDS:
            phase, dive_started = "dive", now
            snap("before_dive")
            control(0, 0.7)               # nose down
    elif phase == "dive" and now - dive_started > 60:
        end_reason = "still flying 60 s into the dive"
        break

control(0, 0)
time.sleep(1.0)
p = position()
note(f"END: {end_reason}; player now at {p[0]:.1f}, {p[1]:.1f}, {p[2]:.1f}")
snap("after")
(mcp_client.OUT / "fly_log.txt").write_text("\n".join(lines) + "\n", encoding="utf-8")
