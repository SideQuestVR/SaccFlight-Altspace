"""Headset check: sit in the pilot's seat of each vehicle in turn and take a picture of the view.

    python cockpit_tour.py ["Vehicle name" ...]        (default: every vehicle in Logs/seat_positions.txt)

Pictures go to ./out/tour_<vehicle>.jpg, one line per vehicle is printed (seated or not, where the eyes
ended up relative to the seat).
"""
from __future__ import annotations

import base64
import subprocess
import sys
import time
from pathlib import Path

import mcp_client
from harness import Harness

ARRIVAL = (0.0, 1.6, 0.0)
seats = Path(__file__).resolve().parents[2] / "Logs" / "seat_positions.txt"
names = sys.argv[1:] or sorted({line.split("|")[0].strip() for line in seats.read_text().splitlines()
                                if "| PilotSeat |" in line})
h = Harness()


def snap(label: str) -> None:
    for item in h.client.call("take_snapshot", {"width": 1280, "height": 720, "quality": 85}):
        if item.get("type") == "image":
            mcp_client.OUT.mkdir(exist_ok=True)
            (mcp_client.OUT / f"tour_{label}.jpg").write_bytes(base64.b64decode(item["data"]))


for name in names:
    result = subprocess.run([sys.executable, str(Path(__file__).with_name("board.py")), name], capture_output=True, text=True)
    lines = [line for line in result.stdout.strip().splitlines() if line]
    verdict = lines[-1] if lines else "no output"
    detail = lines[-2] if len(lines) > 1 else result.stderr.strip()[-200:]
    time.sleep(1.2)                      # let the seat fit itself to the pilot
    snap(name.replace(" ", "_"))
    print(f"{name}: {verdict} | {detail[:230]}", flush=True)
    # get out: jumping lets go of the seat; then walk back to the arrival point
    h.call("pose_override", {"mode": "off"})
    h.call("set_buttons", {"right": {"primary": True}, "duration_ms": 200})
    time.sleep(1.0)
    h.call("teleport", {"position": {"x": ARRIVAL[0], "y": ARRIVAL[1], "z": ARRIVAL[2]}, "yaw": 301.3})
    time.sleep(1.0)
h.release()
