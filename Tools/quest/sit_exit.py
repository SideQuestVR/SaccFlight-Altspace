"""Headset check of sitting down in a vehicle and getting out again, as a player whose head is not at the
middle of their play space (which is nearly every real player).

    python sit_exit.py <vehicle> <seat object name> [off-centre x] [off-centre z] [eye height]

Stands beside the seat with the head off-centre in tracking space by (x, z) metres, points, pulls the trigger,
stays at that same tracking-space spot (turned to face forward), then jumps to get out. Reports:
  - how far the vehicle moved when the player sat, while seated, and when they got out (a kick shows here)
  - where the head ended up relative to the seat
  - where the player stands after getting out and how fast they are moving
Seat positions come from Logs/seat_positions.txt (Unity menu FlightWorld/Flight/Test/Report Seat Positions).
"""
from __future__ import annotations

import math
import sys
import time
from pathlib import Path

from harness import Harness

vehicle, seat_name = sys.argv[1], sys.argv[2]
off = (float(sys.argv[3]), float(sys.argv[4])) if len(sys.argv) > 4 else (0.0, 0.0)
eye = float(sys.argv[5]) if len(sys.argv) > 5 else 1.65
seat = yaw = built = None
for line in (Path(__file__).resolve().parents[2] / "Logs" / "seat_positions.txt").read_text().splitlines():
    name, part, position, heading = [field.strip() for field in line.split("|")]
    if name == vehicle and part == seat_name and seat is None:
        seat = tuple(float(v) for v in position.split(","))
        yaw = float(heading.replace("yaw", ""))
    if name == vehicle and part == "(root)":
        built = tuple(float(v) for v in position.split(","))
if seat is None:
    sys.exit(f"no seat '{seat_name}' listed for '{vehicle}'")

h = Harness()
h.call("pose_override", {"mode": "off"})
h.call("set_buttons", {"active": False})


def body():
    return h.call("synced_objects", {"action": "list", "name": vehicle})["objects"][0]


def moved(a, b):
    return math.dist(a["position"], b["position"])


def root_pose():
    root = h.call("tracking_status")["tracking_root"]
    return root[:3], 2 * math.atan2(root[4], root[6])


def to_world(origin, turn, x, z):
    return origin[0] + x * math.cos(turn) + z * math.sin(turn), origin[2] - x * math.sin(turn) + z * math.cos(turn)


start = body()
shift = tuple(start["position"][i] - built[i] for i in range(3))
if max(abs(c) for c in shift) > 5.0:
    sys.exit(f"{vehicle} is not at its parking spot ({start['position']}); someone may be using it")
seat = tuple(seat[i] + shift[i] for i in range(3))
target = (seat[0], seat[1] + 0.45, seat[2])
forward = (math.sin(math.radians(yaw)), math.cos(math.radians(yaw)))
left = (-forward[1], forward[0])
ground = start["position"][1] - 0.5

seated = False
for side, out, ahead in ((1, 1.6, 0.6), (-1, 1.6, 0.6), (1, 2.6, 0.3), (-1, 2.6, 0.3)):
    stand = (seat[0] + left[0] * out * side + forward[0] * ahead, seat[2] + left[1] * out * side + forward[1] * ahead)
    h.call("pose_override", {"mode": "off"})
    h.call("teleport", {"position": {"x": stand[0], "y": ground + 0.2, "z": stand[1]}, "yaw": (yaw + 90 * side) % 360})
    time.sleep(1.5)
    origin, turn = root_pose()
    hx, hz = to_world(origin, turn, off[0], off[1])
    head = (hx, origin[1] + 1.65, hz)
    reach = math.dist(head, target)
    controller = tuple(head[i] + (target[i] - head[i]) / reach * 0.45 for i in range(3))
    controller = (controller[0], controller[1] - 0.15, controller[2])
    h.aim(head, controller, target)
    time.sleep(1.0)
    before = body()
    h.click(250)
    time.sleep(0.35)
    if body().get("local_rides"):
        seated = True
        print(f"sat from {out} m to the {'left' if side > 0 else 'right'}; head {math.hypot(*off):.2f} m off the play-space centre")
        break
if not seated:
    h.release()
    sys.exit("NOT SEATED")

# stay at the same spot of the play space, turned to face the way the vehicle faces
origin, turn = root_pose()
half = ((math.radians(yaw) - turn) % (2 * math.pi)) / 2
h.call("pose_override", {"mode": "static", "space": "tracking",
                         "head": {"p": [off[0], eye, off[1]], "q": [0, round(math.sin(half), 5), 0, round(math.cos(half), 5)]}})
worst = 0.0
for _ in range(8):
    time.sleep(0.5)
    worst = max(worst, moved(before, body()))
now = body()
where = h.where()
hp = where["head"]
live_seat = tuple(seat[i] + now["position"][i] - before["position"][i] for i in range(3))
print(f"{vehicle} moved {moved(before, now):.2f} m in the 4 s after the player sat (most {worst:.2f} m)")
print(f"head is {math.dist((hp['x'], hp['z']), (live_seat[0], live_seat[2])):.2f} m sideways from the seat and "
      f"{hp['y'] - live_seat[1]:+.2f} m above it; view yaw {where['yaw']:.1f} (vehicle {yaw:.1f})")

# get out: the jump button lets go of the seat
resting = body()
h.call("set_buttons", {"right": {"primary": True}, "duration_ms": 200})
time.sleep(0.4)
h.call("set_buttons", {"active": False})
trail = []
for _ in range(8):
    time.sleep(0.5)
    w = h.where()["position"]
    trail.append((w["x"], w["y"], w["z"]))
after = body()
out_at = trail[-1]
speed = math.dist(trail[-1], trail[-2]) / 0.5
furthest = max(math.dist(p, live_seat) for p in trail)
print(f"after getting out: player {math.dist((out_at[0], out_at[2]), (live_seat[0], live_seat[2])):.2f} m from the seat "
      f"(furthest {furthest:.2f} m), height {out_at[1] - ground:+.2f} m above the ground, moving {speed:.2f} m/s; "
      f"still riding: {bool(after.get('local_rides'))}")
print(f"{vehicle} moved {moved(resting, after):.2f} m when the player got out")
h.release()
ok = moved(before, now) < 0.3 and moved(resting, after) < 0.3 and furthest < 6 and not after.get("local_rides")
print("PASS" if ok else "CHECK")
