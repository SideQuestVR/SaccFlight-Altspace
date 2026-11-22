"""Log what a client's own player experiences for a while: position, speed, whether the seat rig says it is
seated, with a snapshot of its view every couple of seconds. Used for the passenger in a two-client flight.

    python ride_log.py <client: quest|editor> <seconds> [prefix]
"""
from __future__ import annotations

import base64
import math
import sys
import threading
import time

import mcp_client
from two_clients import connect

client, seconds = sys.argv[1], float(sys.argv[2])
prefix = sys.argv[3] if len(sys.argv) > 3 else "ride"
t0 = time.time()
lines: list[str] = []
lock = threading.Lock()


def note(text: str) -> None:
    with lock:
        lines.append(text)
    print(text, flush=True)


def sample() -> None:
    h = connect(client)
    last = None
    while time.time() - t0 < seconds:
        now = time.time()
        p = h.call("get_state")["player"]["position"]
        q = (p["x"], p["y"], p["z"])
        speed = math.dist(q, last[0]) / (now - last[1]) if last else 0.0
        last = (q, now)
        seated = ""
        try:
            seated = "seated" if h.call("seat_sync", {"action": "status"}).get("local", {}).get("seated") else "not seated"
        except Exception:
            pass
        note(f"{now - t0:6.2f}s  ({q[0]:9.1f}, {q[1]:7.1f}, {q[2]:9.1f})  {speed:6.1f} m/s  {seated}")
        time.sleep(0.2)


def camera() -> None:
    h = connect(client)
    index = 0
    while time.time() - t0 < seconds:
        try:
            for item in h.client.call("take_snapshot", {"width": 1280, "height": 720, "quality": 80}):
                if item.get("type") == "image":
                    mcp_client.OUT.mkdir(exist_ok=True)
                    (mcp_client.OUT / f"{prefix}_{index:02d}.jpg").write_bytes(base64.b64decode(item["data"]))
                    note(f"{time.time() - t0:6.2f}s  snapshot {prefix}_{index:02d}.jpg")
                    index += 1
        except Exception as error:
            note(f"{time.time() - t0:6.2f}s  camera: {type(error).__name__}: {error}")
        time.sleep(1.6)


threads = [threading.Thread(target=sample), threading.Thread(target=camera)]
for t in threads:
    t.start()
for t in threads:
    t.join()
(mcp_client.OUT / f"{prefix}_log.txt").write_text("\n".join(lines) + "\n", encoding="utf-8")
