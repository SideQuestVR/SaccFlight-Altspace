"""Two-client multiplayer check of the pilots world: the headset and the client running in the Unity editor.

Each client is driven through its own MCP endpoint (same tools on both):
    quest   http://127.0.0.1:42080/mcp   (adb forward to the headset's 42070)
    editor  http://127.0.0.1:42070/mcp   (the Greenfield editor in Play mode)

    python two_clients.py status                     who is where, on both clients
    python two_clients.py objects                    who owns and simulates each aircraft, per client
    python two_clients.py watch <aircraft> [secs]    position of an aircraft as each client sees it, side by side
    python two_clients.py snap <client> <name>       snapshot from one client -> out/<name>.jpg
    python two_clients.py call <client> <tool> [json]

A page-side watcher (WatchTransform) reports aircraft positions; it is installed on first use.
"""
from __future__ import annotations

import base64
import json
import math
import sys
import time

import mcp_client
from harness import Harness

ENDPOINTS = {"quest": "http://127.0.0.1:42080/mcp", "editor": "http://127.0.0.1:42070/mcp"}
AIRCRAFT = ["Airliner 1", "Trainer 1", "Trainer 2", "Jet 1", "Jet 2", "Jet 3"]
WATCHER = (
    "if (!window.__fw) { window.__fw = {}; (async () => { const s = BS.Scene.GetInstance(); for (const n of %s) { "
    "try { const o = await s.Find(n); if (!o) continue; await o.WatchTransform([BS.PN.position], (t) => { "
    "window.__fw[n] = [t.position.x, t.position.y, t.position.z, Date.now()]; }); } catch (e) { window.__fw[n] = String(e); } } })(); } 'ok'"
) % json.dumps(AIRCRAFT)


def connect(name: str) -> Harness:
    """A harness bound to one client. mcp_client keeps its endpoint in a module global, so every call
    made through this harness first points that global back at its own client."""
    url = ENDPOINTS[name]
    mcp_client.URL = url
    h = Harness()
    send = h.client.call

    def call(tool, arguments=None, timeout=150.0):
        mcp_client.URL = url
        return send(tool, arguments, timeout)

    h.client.call = call
    return h


def clients() -> dict[str, Harness]:
    found = {}
    for name in ENDPOINTS:
        try:
            found[name] = connect(name)
        except Exception as error:                      # one client being down is a result, not a crash
            print(f"{name}: not reachable ({type(error).__name__})")
    return found


def positions(h: Harness) -> dict:
    h.call("page_eval", {"js": WATCHER})
    raw = h.call("page_eval", {"js": "JSON.stringify(window.__fw || {})"})
    return json.loads(raw) if isinstance(raw, str) and raw else (raw or {})


def main() -> None:
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    command, args = sys.argv[1], sys.argv[2:]
    if command == "status":
        for name, h in clients().items():
            state = h.call("get_state")
            p = state["player"]["position"]
            net = state.get("networking", {})
            users = h.call("list_users").get("users", [])
            print(f"{name}: at ({p['x']:.1f}, {p['y']:.1f}, {p['z']:.1f}), {net.get('state')}, room {net.get('room_key')}")
            for u in users:
                q = u["position"]
                print(f"    {'me   ' if u['is_local'] else 'other'} {u['name']}: ({q['x']:.1f}, {q['y']:.1f}, {q['z']:.1f})")
    elif command == "watch":
        aircraft, seconds = args[0], float(args[1]) if len(args) > 1 else 20.0
        both = clients()
        last = {}
        t0 = time.time()
        while time.time() - t0 < seconds:
            line = [f"{time.time() - t0:5.1f}s"]
            seen = {}
            for name, h in both.items():
                p = positions(h).get(aircraft)
                if not isinstance(p, list):
                    line.append(f"{name}: {p}")
                    continue
                seen[name] = p
                speed = 0.0
                if name in last and p[3] != last[name][3]:
                    speed = math.dist(p[:3], last[name][:3]) / ((p[3] - last[name][3]) / 1000.0)
                last[name] = p
                line.append(f"{name}: ({p[0]:8.1f}, {p[1]:6.1f}, {p[2]:8.1f}) {speed:6.1f} m/s")
            if len(seen) == 2:
                line.append(f"apart {math.dist(seen['quest'][:3], seen['editor'][:3]):6.1f} m")
            print("  ".join(line), flush=True)
            time.sleep(0.5)
    elif command == "objects":
        # who simulates each aircraft, as each client sees it (needs the client's synced_objects tool)
        for name, h in clients().items():
            try:
                listing = h.call("synced_objects", {"action": "list"})
            except Exception as error:
                print(f"{name}: synced_objects not available ({error})")
                continue
            rows = listing.get("objects", listing) if isinstance(listing, dict) else listing
            print(f"{name}:")
            for row in rows if isinstance(rows, list) else []:
                label = str(row.get("name", ""))
                if not any(a in label for a in AIRCRAFT):
                    continue
                keep = ("owner", "owned_here", "kinematic", "speed", "remote_rider", "local_rides", "local_drives",
                        "colliders_on", "detect_collisions", "take_on_collision")
                print("   ", label.ljust(12), {k: row[k] for k in keep if k in row})
            if not isinstance(rows, list):
                print("   ", json.dumps(listing)[:600])
    elif command == "snap":
        h = connect(args[0])
        for item in h.client.call("take_snapshot", {"width": 1280, "height": 720, "quality": 85}):
            if item.get("type") == "image":
                mcp_client.OUT.mkdir(exist_ok=True)
                path = mcp_client.OUT / f"{args[1]}.jpg"
                path.write_bytes(base64.b64decode(item["data"]))
                print("saved", path)
    elif command == "call":
        out = connect(args[0]).call(args[1], json.loads(args[2]) if len(args) > 2 else {})
        print(out if isinstance(out, str) else json.dumps(out, indent=1))
    else:
        sys.exit(__doc__)


if __name__ == "__main__":
    main()
