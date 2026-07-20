"""Test script for basic usage."""
import sys
import json

sys.stdin.reconfigure(errors='replace')
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
            exec(code, globals())
            print(json.dumps({"status": "ok"}), flush=True)
        else:
            print(json.dumps({"status": "error", "message": f"Unknown: {method}"}), flush=True)
    except Exception as e:
        print(json.dumps({"status": "error", "message": str(e)}), flush=True)
