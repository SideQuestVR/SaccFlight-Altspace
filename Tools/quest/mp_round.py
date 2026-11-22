"""Steps of a two-client multiplayer round (editor client + Quest), each runnable on its own.

    python mp_round.py seat-editor "<aircraft>" [seat object name, default PilotSeat]
        editor player stands beside the seat, looks at it and clicks it with click_view
    python mp_round.py fly-editor <rotate m/s> <climb s>
        fly the aircraft the editor player pilots (same script as the Quest's fly_test.py)
    python mp_round.py record <start|stop> <synced object name, e.g. fw-Trainer_1> [label]
        start / stop the client's per-frame recording of one synced object on BOTH clients
    python mp_round.py drop <client> "<aircraft>" <metres right of centreline> <metres ahead of the root>
        teleport that client's player above the aircraft and report where it comes to rest

The editor needs the Greenfield client tools click_view, page_eval and synced_objects (2026-11-21 bundle).
"""
from __future__ import annotations

import json
import math
import os
import subprocess
import sys
import time
from pathlib import Path

from two_clients import ENDPOINTS, connect

HERE = Path(__file__).resolve().parent
SEATS = HERE.parents[1] / "Logs" / "seat_positions.txt"
GROUND = 1.0


def seat_of(aircraft: str, part: str):
    for line in SEATS.read_text().splitlines():
        name, obj, position, heading = [field.strip() for field in line.split("|")]
        if name == aircraft and obj == part:
            return tuple(float(v) for v in position.split(",")), float(heading.replace("yaw", ""))
    sys.exit(f"no '{part}' listed for '{aircraft}' in {SEATS}")


def axes(yaw: float):
    forward = (math.sin(math.radians(yaw)), math.cos(math.radians(yaw)))
    return forward, (forward[1], -forward[0])          # forward, right


def main() -> None:
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    command, args = sys.argv[1], sys.argv[2:]

    if command == "seat-editor":
        aircraft, part = args[0], args[1] if len(args) > 1 else "PilotSeat"
        seat, yaw = seat_of(aircraft, part)
        forward, right = axes(yaw)
        e = connect("editor")
        # on the ground, 2.2 m to the left of the seat and 1.2 m ahead of it (clear of the wing on every type)
        stand = (seat[0] - right[0] * 2.2 + forward[0] * 1.2, GROUND + 0.2, seat[2] - right[1] * 2.2 + forward[1] * 1.2)
        e.call("teleport", {"position": {"x": stand[0], "y": stand[1], "z": stand[2]}})
        time.sleep(1.5)
        aim = e.call("move", {"look_at": {"x": seat[0], "y": seat[1] - 0.05, "z": seat[2]}, "timeout_s": 4})
        print("aim:", aim.get("outcome"), "| would click:", json.dumps(e.call("click_view", {"dry_run": True}))[:300])
        print("click:", json.dumps(e.call("click_view", {}))[:300])
        time.sleep(2.0)
        status = e.call("seat_sync", {"action": "status"}).get("local", {})
        p = e.call("get_state")["player"]["position"]
        print(f"{time.strftime('%H:%M:%S')} editor player: seated {status.get('seated')}, at ({p['x']:.2f}, {p['y']:.2f}, {p['z']:.2f}); "
              f"seat is at ({seat[0]:.2f}, {seat[1]:.2f}, {seat[2]:.2f})")
        sys.exit(0 if status.get("seated") else 1)

    elif command == "fly-editor":
        env = dict(os.environ, ALTSPACE_MCP_URL=ENDPOINTS["editor"])
        sys.exit(subprocess.call([sys.executable, str(HERE / "fly_test.py"), *args], env=env, cwd=str(HERE)))

    elif command == "record":
        action, name = args[0], args[1]
        label = args[2] if len(args) > 2 else name
        for client in ENDPOINTS:
            try:
                h = connect(client)
                arguments = {"action": "record", "name": name, "label": f"{label}-{client}"} if action == "start" else {"action": "stop"}
                out = h.call("synced_objects", arguments)
                print(f"{time.strftime('%H:%M:%S')} {client}: {json.dumps(out)[:1800] if not isinstance(out, str) else out[:1800]}")
            except Exception as error:
                print(f"{client}: {type(error).__name__}: {error}")

    elif command == "drop":
        client, aircraft = args[0], args[1]
        lx, lz = float(args[2]), float(args[3])
        root, yaw = seat_of(aircraft, "(root)")
        forward, right = axes(yaw)
        h = connect(client)
        x, z = root[0] + right[0] * lx + forward[0] * lz, root[2] + right[1] * lx + forward[1] * lz
        h.call("teleport", {"position": {"x": x, "y": root[1] + 2.0, "z": z}})
        time.sleep(4.0)
        p = h.call("get_state")["player"]["position"]
        resting = "ON the aircraft" if p["y"] > GROUND + 1.2 else "on the ground (fell through or missed)"
        print(f"{time.strftime('%H:%M:%S')} {client} player dropped onto {aircraft} ({lx:+.1f} m right, {lz:+.1f} m ahead): rests at height {p['y']:.2f}, {resting}")

    else:
        sys.exit(__doc__)


if __name__ == "__main__":
    main()
