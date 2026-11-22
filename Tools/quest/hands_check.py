"""Headset check, run after board.py: hold both hands in front of the chest (tracking space, as a seated
player would), look down at them and save the view; then report where the app has the hands in the world.

    python hands_check.py <label> <vehicle yaw> [eye height, default 1.65]

A hand that the app leaves behind when the seat moves inside its vehicle shows up as a hand far from the head.
"""
import base64
import math
import sys
import time

import mcp_client
from harness import Harness

label = sys.argv[1]
yaw = float(sys.argv[2])
eye = float(sys.argv[3]) if len(sys.argv) > 3 else 1.65
h = Harness()
root = h.call("tracking_status")["tracking_root"]
root_yaw = math.degrees(2 * math.atan2(root[4], root[6]))
turn = math.radians((yaw - root_yaw) % 360)


def tracking(right: float, up: float, ahead: float):
    """A point given relative to the seated head (right / up / ahead of it) as a tracking-space position."""
    return [round(right * math.cos(turn) + ahead * math.sin(turn), 4), round(eye + up, 4),
            round(-right * math.sin(turn) + ahead * math.cos(turn), 4)]


def facing(pitch_down: float):
    """Tracking-space rotation: the vehicle's heading, tipped down by pitch_down degrees."""
    half_yaw, half_pitch = turn / 2, math.radians(pitch_down) / 2
    # yaw (about Y) times pitch (about X)
    return [round(math.cos(half_yaw) * math.sin(half_pitch), 5), round(math.sin(half_yaw) * math.cos(half_pitch), 5),
            round(-math.sin(half_yaw) * math.sin(half_pitch), 5), round(math.cos(half_yaw) * math.cos(half_pitch), 5)]


h.call("pose_override", {"mode": "static", "space": "tracking",
                         "head": {"p": tracking(0, 0, 0), "q": facing(38)},
                         "left": {"p": tracking(-0.2, -0.38, 0.38), "q": facing(0)},
                         "right": {"p": tracking(0.2, -0.38, 0.38), "q": facing(0)}})
time.sleep(1.5)
for item in h.client.call("take_snapshot", {"width": 1280, "height": 720, "quality": 88}):
    if item.get("type") == "image":
        mcp_client.OUT.mkdir(exist_ok=True)
        path = mcp_client.OUT / f"hands_{label}.jpg"
        path.write_bytes(base64.b64decode(item["data"]))
        print(path)
state = h.call("get_state")
print(str(state)[:1500])
