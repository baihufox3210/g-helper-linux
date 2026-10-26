#!/usr/bin/env python3
"""One-request G-Helper IPC example client (Python standard library only)."""
import argparse
import json
import os
import socket
import struct
import sys
import uuid


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("method", help="e.g. capabilities.get or gpu.set")
    parser.add_argument("params", nargs="?", default="{}", help='JSON object, e.g. \'{"mode":"eco"}\'')
    parser.add_argument("--timeout", type=float, default=300, help="seconds; GPU authorization may take minutes")
    args = parser.parse_args()
    params = json.loads(args.params)
    if not isinstance(params, dict):
        parser.error("params must be a JSON object")
    request_id = uuid.uuid4().hex
    request = json.dumps({"version": 1, "id": request_id, "method": args.method, "params": params}).encode() + b"\n"
    if len(request) > 8192:
        parser.error("request exceeds 8192 bytes")
    runtime = os.environ.get("XDG_RUNTIME_DIR")
    if not runtime:
        parser.error("XDG_RUNTIME_DIR is required")
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as client:
        client.settimeout(args.timeout)
        client.connect(os.path.join(runtime, "ghelper.sock"))
        _, uid, _ = struct.unpack("3i", client.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, 12))
        if uid != os.geteuid():
            raise RuntimeError("G-Helper socket belongs to a different user")
        client.sendall(request)
        with client.makefile("rb") as stream:
            line = stream.readline(1024 * 1024 + 1)
        if len(line) > 1024 * 1024 or not line.endswith(b"\n"):
            raise RuntimeError("incomplete or oversized response")
        response = json.loads(line)
        if response.get("id") != request_id or response.get("version") != 1:
            raise RuntimeError("response version or id mismatch")
        print(json.dumps(response, indent=2))
        if not response.get("ok"):
            return 1
        if response.get("result", {}).get("state") in ("failed", "blocked", "busy"):
            return 2
        return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, ValueError, RuntimeError) as error:
        print(f"ghelper-ipc: {error}", file=sys.stderr)
        sys.exit(1)
