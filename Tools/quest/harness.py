"""Helpers for driving the Altspace app's test harness (pose override, buttons, snapshots) from the PC.

    python harness.py aim <hx> <hy> <hz> <cx> <cy> <cz> <tx> <ty> <tz>
        hold the head at h and the right controller at c, both aimed at the target t (world space)
    python harness.py click [ms]            press the right trigger
    python harness.py sticks <lx> <ly> <rx> <ry> [left_trigger]   hold the thumbsticks (0 0 0 0 releases them)
    python harness.py release               stop overriding poses and buttons
    python harness.py where                 player position, head position, yaw
    python harness.py call <tool> [json]    any other tool

Unity's conventions throughout: left-handed, Y up, +Z forward; quaternions as x, y, z, w.
"""
from __future__ import annotations

import json
import math
import sys

import mcp_client


def look_rotation(forward, up=(0.0, 1.0, 0.0)):
    """Unity's Quaternion.LookRotation: +Z along `forward`, +Y as close to `up` as possible."""
    def norm(v):
        m = math.sqrt(sum(c * c for c in v))
        return tuple(c / m for c in v)

    def cross(a, b):
        return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])

    f = norm(forward)
    r = norm(cross(up, f))
    u = cross(f, r)
    m00, m01, m02 = r[0], u[0], f[0]
    m10, m11, m12 = r[1], u[1], f[1]
    m20, m21, m22 = r[2], u[2], f[2]
    trace = m00 + m11 + m22
    if trace > 0:
        s = math.sqrt(trace + 1.0) * 2
        return ((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25 * s)
    if m00 > m11 and m00 > m22:
        s = math.sqrt(1.0 + m00 - m11 - m22) * 2
        return (0.25 * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s)
    if m11 > m22:
        s = math.sqrt(1.0 + m11 - m00 - m22) * 2
        return ((m01 + m10) / s, 0.25 * s, (m12 + m21) / s, (m02 - m20) / s)
    s = math.sqrt(1.0 + m22 - m00 - m11) * 2
    return ((m02 + m20) / s, (m12 + m21) / s, 0.25 * s, (m10 - m01) / s)


def pose_towards(position, target):
    q = look_rotation(tuple(t - p for p, t in zip(position, target)))
    return {"p": list(position), "q": [round(c, 5) for c in q]}


class Harness:
    def __init__(self) -> None:
        self.client = mcp_client.Client()

    def call(self, tool: str, arguments: dict | None = None):
        return mcp_client.text_of(self.client.call(tool, arguments or {}))

    def aim(self, head, controller, target):
        return self.call("pose_override", {"mode": "static", "space": "world", "head": pose_towards(head, target),
                                           "right": pose_towards(controller, target)})

    def click(self, ms: float = 250):
        return self.call("set_buttons", {"right": {"trigger": 1.0}, "duration_ms": ms})

    def sticks(self, lx: float, ly: float, rx: float, ry: float, left_trigger: float = 0.0):
        if not (lx or ly or rx or ry or left_trigger):
            return self.call("set_buttons", {"active": False})
        return self.call("set_buttons", {"left": {"stick": [lx, ly], "trigger": left_trigger}, "right": {"stick": [rx, ry]},
                                         "duration_ms": 0})

    def release(self):
        self.call("set_buttons", {"active": False})
        return self.call("pose_override", {"mode": "off"})

    def where(self):
        player = self.call("get_state")["player"]
        return {"position": player["position"], "head": player["head_position"], "yaw": player["yaw"]}


def main() -> None:
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    command, args = sys.argv[1], sys.argv[2:]
    h = Harness()
    if command == "aim":
        v = [float(a) for a in args]
        out = h.aim(v[0:3], v[3:6], v[6:9])
    elif command == "click":
        out = h.click(float(args[0]) if args else 250)
    elif command == "sticks":
        out = h.sticks(*[float(a) for a in args])
    elif command == "release":
        out = h.release()
    elif command == "where":
        out = h.where()
    elif command == "call":
        out = h.call(args[0], json.loads(args[1]) if len(args) > 1 else {})
    else:
        sys.exit(__doc__)
    print(out if isinstance(out, str) else json.dumps(out, indent=1))


if __name__ == "__main__":
    main()
