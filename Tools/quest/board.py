"""Headset check: get into an aircraft's pilot seat the way a player does - stand beside it, point the
right controller at the seat, pull the trigger - then hold a normal seated head pose facing forward.

    python board.py "Jet 2" [eye height in tracking space, default 1.65] [seat object name, default PilotSeat]

Seat positions come from Logs/seat_positions.txt (Unity menu FlightWorld/Flight/Test/Report Seat Positions).
"""
from __future__ import annotations

import math
import sys
import time
from pathlib import Path

from harness import Harness

aircraft = sys.argv[1]
eye = float(sys.argv[2]) if len(sys.argv) > 2 else 1.65
seat_name = sys.argv[3] if len(sys.argv) > 3 else "PilotSeat"
seat = yaw = root = None
for line in (Path(__file__).resolve().parents[2] / "Logs" / "seat_positions.txt").read_text().splitlines():
    name, part, position, heading = [field.strip() for field in line.split("|")]
    if name == aircraft and part == seat_name:
        seat = tuple(float(v) for v in position.split(","))
        yaw = float(heading.replace("yaw", ""))
    if name == aircraft and part == "(root)":
        root = tuple(float(v) for v in position.split(","))
if seat is None:
    sys.exit(f"no seat '{seat_name}' listed for '{aircraft}'")

forward = (math.sin(math.radians(yaw)), math.cos(math.radians(yaw)))
left = (-forward[1], forward[0])
ground = 1.0
h = Harness()
h.call("pose_override", {"mode": "off"})
h.call("set_buttons", {"active": False})

# The file has each vehicle as it was built; at rest on its gear it sits a little differently. Move the
# seat by however far the vehicle itself is from where the file has it, and aim at the middle of the
# seat's click area (0.4 m above the seat's origin), not at its origin.
if root is not None:
    try:
        live = h.call("synced_objects", {"action": "list", "name": aircraft})["objects"][0]["position"]
        shift = tuple(live[i] - root[i] for i in range(3))
        if max(abs(c) for c in shift) < 5.0:
            seat = tuple(seat[i] + shift[i] for i in range(3))
            print(f"{aircraft} is ({shift[0]:+.2f}, {shift[1]:+.2f}, {shift[2]:+.2f}) m from where it was built")
        else:
            print(f"{aircraft} is far from its parking spot ({live}); using the built position anyway")
    except Exception as error:            # older clients have no synced_objects tool
        print(f"live position unavailable ({error}); using the built position")
AIM_ABOVE_SEAT = 0.45

# Stand beside the cockpit, point at the seat's click area and pull the trigger. Small vehicles have no room
# where big ones do, so several standing points are tried until the app says the player is riding.
target = (seat[0], seat[1] + AIM_ABOVE_SEAT, seat[2])


def riding() -> bool:
    try:
        return bool(h.call("synced_objects", {"action": "list", "name": aircraft})["objects"][0]["local_rides"])
    except Exception:
        return False


seated_by_app = False
for side, out, ahead in ((1, 1.4, 0.9), (1, 2.8, 0.4), (-1, 1.6, 0.9), (-1, 3.0, 0.4), (1, 3.8, 2.2), (-1, 3.8, 2.2)):
    stand = (seat[0] + left[0] * out * side + forward[0] * ahead, ground + 0.2, seat[2] + left[1] * out * side + forward[1] * ahead)
    h.call("pose_override", {"mode": "off"})
    h.call("teleport", {"position": {"x": stand[0], "y": stand[1], "z": stand[2]}, "yaw": (yaw + 90 * side) % 360})
    time.sleep(1.5)
    head = (stand[0], ground + 1.65, stand[2])
    # the hand stays within reach of the body: 0.45 m out from the head towards the seat, a little below eye level
    reach = math.dist(head, target)
    controller = tuple(head[i] + (target[i] - head[i]) / reach * 0.45 for i in range(3))
    controller = (controller[0], controller[1] - 0.15, controller[2])
    h.aim(head, controller, target)
    time.sleep(1.0)
    h.click(250)
    time.sleep(0.35)    # be in a normal seated pose before the seat fits itself to the pilot (0.7 s after sitting)
    if riding():
        seated_by_app = True
        print(f"boarded from {out} m to the {'left' if side > 0 else 'right'}, {ahead} m ahead of the seat")
        break

# face forward: the view yaw is the tracking root's yaw plus the head's own yaw in tracking space
root = h.call("tracking_status")["tracking_root"]
root_yaw = math.degrees(2 * math.atan2(root[4], root[6]))
half = math.radians((yaw - root_yaw) % 360) / 2
h.call("pose_override", {"mode": "static", "space": "tracking",
                         "head": {"p": [0, eye, 0], "q": [0, round(math.sin(half), 5), 0, round(math.cos(half), 5)]}})
time.sleep(2.0)
where = h.where()
p, hp = where["position"], where["head"]
offset = math.dist((hp["x"], hp["z"]), (seat[0], seat[2]))
print(f"{aircraft}: seat at {seat}, head now at ({hp['x']:.2f}, {hp['y']:.2f}, {hp['z']:.2f}), "
      f"{hp['y'] - seat[1]:+.2f} m above the seat origin, {offset:.2f} m from it sideways, view yaw {where['yaw']:.1f} (aircraft {yaw:.1f})")
ok = seated_by_app or offset < 0.6
print("SEATED" if ok else "NOT SEATED")
sys.exit(0 if ok else 1)
