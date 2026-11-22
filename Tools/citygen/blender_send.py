"""Send Python to the running Blender through its MCP extension's local socket.

Same wire format as the official Blender MCP server's `send_code` (JSON + NUL on localhost:9876),
for use when the MCP tools themselves are not loaded in the current session.

    python blender_send.py path/to/snippet.py
    echo "result = {'objects': len(bpy.data.objects)}" | python blender_send.py -
"""
from __future__ import annotations

import json
import os
import socket
import sys


def send_code(code: str, timeout: float = 600.0) -> dict:
    host = os.environ.get("BLENDER_MCP_HOST", "localhost")
    port = int(os.environ.get("BLENDER_MCP_PORT", "9876"))
    request = json.dumps({"type": "execute", "code": code, "strict_json": False}) + "\0"
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.settimeout(timeout)
        sock.connect((host, port))
        sock.sendall(request.encode("utf-8"))
        buf = bytearray()
        while b"\0" not in buf:
            chunk = sock.recv(65536)
            if not chunk:
                break
            buf.extend(chunk)
    line = bytes(buf).partition(b"\0")[0]
    return json.loads(line.decode("utf-8")) if line else {"status": "error", "message": "empty response"}


if __name__ == "__main__":
    source = sys.stdin.read() if sys.argv[1] == "-" else open(sys.argv[1], encoding="utf-8").read()
    print(json.dumps(send_code(source), indent=1)[:6000])
