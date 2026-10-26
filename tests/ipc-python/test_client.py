"""Exercise the actual CLI against a temporary fake server, never G-Helper."""
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import threading
import unittest

CLIENT = Path(__file__).resolve().parents[2] / "tools" / "ghelper-ipc.py"


class ClientTests(unittest.TestCase):
    def exchange(self, response):
        with tempfile.TemporaryDirectory(prefix="ghelper-client-test-") as runtime:
            with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as server:
                server.bind(os.path.join(runtime, "ghelper.sock"))
                server.listen(1)
                requests = []

                def serve():
                    connection, _ = server.accept()
                    with connection, connection.makefile("rb") as reader:
                        request = json.loads(reader.readline())
                        requests.append(request)
                        reply = response(request)
                        connection.sendall(json.dumps(reply).encode() + b"\n")

                thread = threading.Thread(target=serve, daemon=True)
                thread.start()
                result = subprocess.run(
                    ["python3", str(CLIENT), "gpu.set", '{"mode":"eco"}', "--timeout", "2"],
                    env={**os.environ, "XDG_RUNTIME_DIR": runtime}, capture_output=True, text=True, timeout=5,
                )
                thread.join(timeout=3)
                self.assertFalse(thread.is_alive())
                self.assertEqual(requests[0]["version"], 1)
                self.assertEqual(requests[0]["params"], {"mode": "eco"})
                return result

    def test_pending(self):
        result = self.exchange(lambda r: {"version": 1, "id": r["id"], "ok": True, "result": {"state": "pending"}})
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(result.stdout)["result"]["state"], "pending")

    def test_blocked(self):
        result = self.exchange(lambda r: {"version": 1, "id": r["id"], "ok": True, "result": {"state": "blocked"}})
        self.assertEqual(result.returncode, 2, result.stderr)

    def test_protocol_error(self):
        result = self.exchange(lambda r: {"version": 1, "id": r["id"], "ok": False, "error": {"code": "unsupported"}})
        self.assertEqual(result.returncode, 1, result.stderr)

    def test_id_mismatch(self):
        result = self.exchange(lambda r: {"version": 1, "id": "wrong", "ok": True})
        self.assertEqual(result.returncode, 1)
        self.assertIn("mismatch", result.stderr)


if __name__ == "__main__":
    unittest.main()
