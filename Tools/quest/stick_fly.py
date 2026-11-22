"""Headset flight check with the THUMBSTICKS: with the player already in a pilot's seat (board.py), fly the
aircraft the way a VR player does - left stick up for throttle, right stick back to rotate and climb,
a roll, then a dive into the ground - and log what the player experiences.

Needs a client whose test harness forwards played-back sticks to the world (build of 2026-11-21 14:45 on).
Snapshots go to ./out/stick_<label>.jpg, the log to ./out/stick_log.txt.

    python stick_fly.py [rotate_speed m/s, default 80] [climb seconds, default 7] [label prefix]

Expected ending: the aircraft explodes and the player is returned to the arrival point.
"""
from __future__ import annotations

import base64
import math
import sys
import time

import mcp_client
from harness import Harness

ARRIVAL = (0.0, 0.0)                       # the world's arrival point (x, z)
ROTATE = float(sys.argv[1]) if len(sys.argv) > 1 else 80.0
CLIMB_SECONDS = float(sys.argv[2]) if len(sys.argv) > 2 else 7.0
PREFIX = sys.argv[3] if len(sys.argv) > 3 else "stick"
HELICOPTER = len(sys.argv) > 4 and sys.argv[4] == "heli"      # rises on throttle alone; flies forward nose-down
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
            (mcp_client.OUT / f"{PREFIX}_{label}.jpg").write_bytes(base64.b64decode(item["data"]))
            note(f"    snapshot {PREFIX}_{label}.jpg")


def position():
    p = h.where()["position"]
    return (p["x"], p["y"], p["z"]), time.time()


last, last_time = position()
start = last


def sample(label: str) -> tuple[float, float]:
    """(speed m/s, height above the start) since the previous sample."""
    global last, last_time
    now, stamp = position()
    speed = math.dist(now, last) / max(stamp - last_time, 1e-3)
    last, last_time = now, stamp
    note(f"{time.strftime('%H:%M:%S')} {label}: {speed:6.1f} m/s, {now[1] - start[1]:+7.1f} m, "
         f"{math.dist((now[0], now[2]), (start[0], start[2])):7.0f} m from the start")
    return speed, now[1] - start[1]


note(f"start at ({start[0]:.1f}, {start[1]:.2f}, {start[2]:.1f})")
snap("parked")

# ---- throttle: left stick forward moves the lever; hold it until the lever is fully forward ----
h.sticks(0, 1, 0, 0)
time.sleep(2.6)
h.sticks(0, 0, 0, 0.001)                # release the lever; keep the sticks "held" so playback stays active
speed = 0.0
deadline = time.time() + (3 if HELICOPTER else 25)
while speed < ROTATE and time.time() < deadline:
    time.sleep(0.5)
    speed, _ = sample("take-off run")
if speed < ROTATE and not HELICOPTER:
    note(f"RESULT: never reached {ROTATE:.0f} m/s on the ground (last {speed:.1f}) - sticks not reaching the world?")
    snap("stuck")
    h.sticks(0, 0, 0, 0)
    (mcp_client.OUT / f"{PREFIX}_log.txt").write_text("\n".join(lines))
    sys.exit(1)
snap("rolling")

# ---- rotate and climb: right stick back (a helicopter just rises, then noses down to move off) ----
h.sticks(0, 0, 0, 0.001 if HELICOPTER else -0.55)
end = time.time() + CLIMB_SECONDS
height = 0.0
while time.time() < end:
    time.sleep(0.7)
    speed, height = sample("climb")
h.sticks(0, 0, 0, 0.001)
snap("climb")
note(f"climbed to {height:+.0f} m")
if HELICOPTER:
    h.sticks(0, 0, 0, 0.35)
    for _ in range(8):
        time.sleep(0.7)
        speed, height = sample("nose down, moving off")
    h.sticks(0, 0, 0, 0.001)
    snap("forward")

# ---- a roll to the right and back level ----
h.sticks(0, 0, 0.8, 0.001)
time.sleep(1.2)
snap("roll")
h.sticks(0, 0, 0, 0.001)
time.sleep(1.0)
sample("after roll")

# ---- rudder and throttle back, to see the lever and pedals answer ----
h.sticks(0.6, -1, 0, 0.001)
time.sleep(1.0)
h.sticks(0, 0, 0, 0.001)
sample("throttle back, rudder")
snap("cruise")

# ---- dive into the ground ----
h.sticks(0, 1, 0, 0.75)
crashed = None
end = time.time() + 60
while time.time() < end:
    time.sleep(0.6)
    speed, height = sample("dive")
    now, _ = position()
    if math.dist((now[0], now[2]), ARRIVAL) < 6.0:
        crashed = time.time()
        break
h.sticks(0, 0, 0, 0)
time.sleep(1.0)
snap("after_crash")
where = h.where()
note(f"RESULT: {'returned to the arrival point after the crash' if crashed else 'NOT returned to the arrival point within 60 s'}; "
     f"player at ({where['position']['x']:.1f}, {where['position']['y']:.2f}, {where['position']['z']:.1f})")
(mcp_client.OUT / f"{PREFIX}_log.txt").write_text("\n".join(lines))
sys.exit(0 if crashed else 1)
