"""Minimal client for the Altspace app's built-in MCP endpoint (streamable HTTP, JSON-RPC).

Used to look around a published world through the player's eyes on a headset or PC: read state,
teleport, turn and take snapshots. It never speaks or listens.

    python mcp_client.py state
    python mcp_client.py users
    python mcp_client.py snap <name> [width height]
    python mcp_client.py teleport <x> <y> <z> [yaw]
    python mcp_client.py turn <yaw>

The endpoint defaults to http://127.0.0.1:42080/mcp (an adb forward to the headset's port 42070);
override with ALTSPACE_MCP_URL. Snapshots are written to ./out/<name>.jpg.
"""
from __future__ import annotations

import base64
import json
import os
import sys
from pathlib import Path

import requests

URL = os.environ.get("ALTSPACE_MCP_URL", "http://127.0.0.1:42080/mcp")
OUT = Path(__file__).resolve().parent / "out"
HEADERS = {"Content-Type": "application/json", "Accept": "application/json, text/event-stream"}


class Client:
    def __init__(self) -> None:
        self.session = requests.Session()
        self.next_id = 1
        first = self.session.post(URL, headers=HEADERS, timeout=30, data=json.dumps({
            "jsonrpc": "2.0", "id": 0, "method": "initialize",
            "params": {"protocolVersion": "2025-06-18", "capabilities": {},
                       "clientInfo": {"name": "flightworld-check", "version": "1"}},
        }))
        first.raise_for_status()
        self.headers = dict(HEADERS)
        if "Mcp-Session-Id" in first.headers:
            self.headers["Mcp-Session-Id"] = first.headers["Mcp-Session-Id"]
        self.session.post(URL, headers=self.headers, timeout=30,
                          data=json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}))

    def call(self, tool: str, arguments: dict | None = None, timeout: float = 150.0) -> list:
        self.next_id += 1
        reply = self.session.post(URL, headers=self.headers, timeout=timeout, data=json.dumps({
            "jsonrpc": "2.0", "id": self.next_id, "method": "tools/call",
            "params": {"name": tool, "arguments": arguments or {}},
        }))
        reply.raise_for_status()
        body = reply.json()
        if "error" in body:
            raise RuntimeError(body["error"])
        return body["result"].get("content", [])


def text_of(content: list):
    for item in content:
        if item.get("type") == "text":
            try:
                return json.loads(item["text"])
            except json.JSONDecodeError:
                return item["text"]
    return None


def main() -> None:
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    command, args = sys.argv[1], sys.argv[2:]
    client = Client()
    if command == "state":
        state = text_of(client.call("get_state"))
        print(json.dumps({k: state.get(k) for k in ("player", "networking", "client")}, indent=1))
    elif command == "users":
        print(json.dumps(text_of(client.call("list_users")), indent=1))
    elif command == "snap":
        name = args[0]
        size = {"width": int(args[1]), "height": int(args[2])} if len(args) >= 3 else {"width": 1280, "height": 720}
        content = client.call("take_snapshot", {**size, "quality": 85})
        OUT.mkdir(exist_ok=True)
        for item in content:
            if item.get("type") == "image":
                path = OUT / f"{name}.jpg"
                path.write_bytes(base64.b64decode(item["data"]))
                print("saved", path)
                return
        print("no image returned:", text_of(content))
    elif command == "teleport":
        arguments = {"position": {"x": float(args[0]), "y": float(args[1]), "z": float(args[2])}}
        if len(args) > 3:
            arguments["yaw"] = float(args[3])
        print(json.dumps(text_of(client.call("teleport", arguments)), indent=1))
    elif command == "turn":
        print(json.dumps(text_of(client.call("move", {"yaw": float(args[0]), "timeout_s": 15})), indent=1))
    else:
        sys.exit(__doc__)


if __name__ == "__main__":
    main()
