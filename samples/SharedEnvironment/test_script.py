"""Test script for shared environment demo."""
import sys
import json
import os

print("READY", flush=True)

for line in sys.stdin:
    line = line.strip()
    if not line or line == "__EXIT__":
        break

    try:
        msg = json.loads(line)
        method = msg.get("method", "")

        if method == "info":
            import platform
            print(json.dumps({
                "status": "ok",
                "python": platform.python_version(),
                "platform": platform.platform(),
                "pid": os.getpid(),
                "executable": sys.executable
            }), flush=True)
        else:
            print(json.dumps({"status": "error", "message": f"Unknown: {method}"}), flush=True)
    except json.JSONDecodeError:
        print(json.dumps({"status": "error", "message": "Invalid JSON"}), flush=True)
