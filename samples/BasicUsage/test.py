"""Test script for basic usage."""
import contextlib
import io
import json
import sys

print("READY", flush=True)

for line in sys.stdin:
    line = line.strip()
    if not line or line == "__EXIT__":
        break

    try:
        msg = json.loads(line)
        method = msg.get("method", "")

        if method == "run":
            code = msg.get("params", {}).get("code", "")
            # Stdout carries the replies, so capture anything the code prints
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                exec(code, globals())
            print(json.dumps({"status": "ok", "output": output.getvalue()}), flush=True)
        else:
            print(json.dumps({"status": "error", "message": f"Unknown: {method}"}), flush=True)
    except Exception as e:
        print(json.dumps({"status": "error", "message": str(e)}), flush=True)
