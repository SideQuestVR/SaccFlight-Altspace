"""Headset check: does pulling the right trigger in flight hurt the aircraft?

With the player already in the pilot seat (board.py), climbs on the left stick (helicopters rise on throttle
alone), then, high up, pulls the right trigger with the hand pointed somewhere: straight ahead at the sky, at a
passenger seat beside the pilot, and down at the pilot's own seat. After each click it logs what happened to the
aircraft (speed change, whether it was destroyed and went home) and to the player (still riding?).

    python trigger_test.py <vehicle> [target seat name, default PassengerSeat] [climb seconds, default 6]
"""
from __future__ import annotations

import math
import sys
import time
from pathlib import Path

from harness import Harness, pose_towards

vehicle = sys.argv[1]
seat_name = sys.argv[2] if len(sys.argv) > 2 else "PassengerSeat"
climb = float(sys.argv[3]) if len(sys.argv) > 3 else 6.0
h = Harness()

built_root = built_seat = built_pilot = None
for line in (Path(__file__).resolve().parents[2] / "Logs" / "seat_positions.txt").read_text().splitlines():
    if line.startswith("#"):
        continue
    name, part, position, heading = [field.strip() for field in line.split("|")]
    if name != vehicle:
        continue
    p = tuple(float(v) for v in position.split(","))
    if part == "(root)":
        built_root, yaw = p, float(heading.replace("yaw", ""))
    elif part == seat_name and built_seat is None:
        built_seat = p
    elif part == "PilotSeat":
        built_pilot = p


def body():
    return h.call("synced_objects", {"action": "list", "name": vehicle})["objects"][0]


def rotate_y(v, degrees):
    a = math.radians(degrees)
    return (v[0] * math.cos(a) + v[2] * math.sin(a), v[1], -v[0] * math.sin(a) + v[2] * math.cos(a))


def live_point(built, now):
    """Where a point built into the vehicle is now (yaw only: the aircraft is about level in a hover)."""
    local = tuple(built[i] - built_root[i] for i in range(3))
    turn = 2 * math.degrees(math.atan2(now["rotation"][1], now["rotation"][3])) - yaw if "rotation" in now else 0.0
    moved = rotate_y(local, turn)
    return tuple(now["position"][i] + moved[i] for i in range(3))


def state(label):
    b = body()
    p = h.where()["position"]
    print(f"{label:28s} {vehicle} at y {b['position'][1]:7.1f}  vel {b.get('velocity')}  riding {bool(b.get('local_rides'))}  "
          f"player at ({p['x']:.1f}, {p['y']:.1f}, {p['z']:.1f})", flush=True)
    return b


start = state("start")
print("raw:", {k: v for k, v in start.items() if k not in ("position",)})
h.sticks(0, 1, 0, 0)                       # collective up
time.sleep(climb)
h.sticks(0, 0.15, 0, 0)                    # about a hover
time.sleep(1.5)
up = state("climbed")
if up["position"][1] - start["position"][1] < 15:
    print("did not climb; stopping")
    h.sticks(0, 0, 0, 0)
    sys.exit(1)

where = h.where()
head = (where["head"]["x"], where["head"]["y"], where["head"]["z"])


def click_towards(label, target):
    reach = math.dist(head, target)
    hand = tuple(head[i] + (target[i] - head[i]) / max(reach, 0.01) * 0.4 for i in range(3))
    hand = (hand[0], hand[1] - 0.15, hand[2])
    h.call("pose_override", {"mode": "static", "space": "world", "head": pose_towards(head, target),
                             "right": pose_towards(hand, target)})
    time.sleep(0.6)
    before = state(f"before click: {label}")
    h.call("set_buttons", {"left": {"stick": [0, 0.15]}, "right": {"trigger": 1.0}, "duration_ms": 250})
    for i in range(6):
        time.sleep(0.5)
        state(f"  +{(i + 1) * 0.5:.1f} s")
    h.sticks(0, 0.15, 0, 0)
    after = body()
    return math.dist(after["position"], before["position"])


ahead = rotate_y((0, 0, 30), yaw)
click_towards("ahead, at the sky", tuple(head[i] + ahead[i] + (8 if i == 1 else 0) for i in range(3)))
now = body()
if built_seat is not None:
    seat = live_point(built_seat, now)
    click_towards(f"at {seat_name}", (seat[0], seat[1] + 0.45, seat[2]))
now = body()
if built_pilot is not None:
    pilot = live_point(built_pilot, now)
    click_towards("down at the pilot seat", (pilot[0], pilot[1] + 0.2, pilot[2]))
h.sticks(0, 0, 0, 0)
h.release()
state("end")
