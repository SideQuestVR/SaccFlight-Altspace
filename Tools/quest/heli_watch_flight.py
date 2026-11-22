"""Headset half of a two-client check: fly a helicopter while ANOTHER client watches it.

    python heli_watch_flight.py "SH-2" [name of the watching player to wait for] [seconds to wait, default 240]

Run board.py for the helicopter first. Phases, each printed with the wall-clock time so the watcher's
timeline can be laid beside it: seated on the ground, lift-off, hover (throttle worked up and down to hold
height), forward flight, bail-out by the jump button in the air, and what the vehicle does afterwards.
"""
from __future__ import annotations

import math
import sys
import time
from datetime import datetime

from harness import Harness

vehicle = sys.argv[1]
watcher = sys.argv[2] if len(sys.argv) > 2 else ""
patience = float(sys.argv[3]) if len(sys.argv) > 3 else 240.0
h = Harness()


def stamp() -> str:
    return datetime.now().strftime("%H:%M:%S")


def craft():
    return h.call("synced_objects", {"action": "list", "name": vehicle})["objects"][0]


def say(text: str) -> None:
    c = craft()
    p = c["position"]
    print(f"{stamp()}  {text}  | {vehicle} at ({p[0]:.0f}, {p[1]:.1f}, {p[2]:.0f}) speed {c.get('speed', 0):.1f} owned here {c['owned_here']} riding {c['local_rides']}", flush=True)


if not craft()["local_rides"]:
    sys.exit("not seated: run board.py first")
ground = craft()["position"][1]
say("seated on the ground")

# wait for the watcher to arrive
waited = 0.0
while watcher and waited < patience:
    names = [u["name"] for u in h.call("list_users")["users"]]
    if watcher in names:
        break
    time.sleep(3)
    waited += 3
print(f"{stamp()}  watcher {'present: ' + watcher if watcher and waited < patience else 'not seen; flying anyway'}", flush=True)
time.sleep(6)                                    # let the watcher see the seated, rotors-turning state
say("lifting off (throttle up)")


def hold_height(target: float, seconds: float, forward: float = 0.0) -> None:
    """Throttle lever worked from the climb rate to stay near `target` metres above the ground."""
    last_y, last_t = craft()["position"][1], time.time()
    end = time.time() + seconds
    while time.time() < end:
        time.sleep(0.25)
        y, now = craft()["position"][1], time.time()
        climb = (y - last_y) / max(now - last_t, 0.05)
        last_y, last_t = y, now
        wanted = max(-3.0, min(3.0, (ground + target - y) * 0.6))        # climb rate to aim for
        lever = max(-1.0, min(1.0, (wanted - climb) * 0.5))
        h.sticks(0.0, lever, 0.0, forward)


h.sticks(0.0, 1.0, 0.0, 0.0)
end = time.time() + 14
while time.time() < end and craft()["position"][1] < ground + 18:
    time.sleep(0.3)
say("up; holding a hover")
hold_height(20.0, 16.0)
say("hover held 16 s; flying forward")
hold_height(24.0, 9.0, forward=0.45)             # right stick forward = nose down
say("forward flight 9 s; BAILING OUT (jump)")
h.sticks(0.0, 0.0, 0.0, 0.0)
h.call("set_buttons", {"right": {"primary": True}, "duration_ms": 200})
time.sleep(0.5)
h.call("set_buttons", {"active": False})
for _ in range(7):
    say("after the bail-out")
    time.sleep(2.5)
h.release()
print(f"{stamp()}  done", flush=True)
