"""Calculator server using stdin/stdout."""
import sys
import json

# Handle BOM
sys.stdin.reconfigure(errors='replace')

print("READY", flush=True)

for line in sys.stdin:
    line = line.strip()
    if not line or line == "__EXIT__":
        break

    try:
        msg = json.loads(line)
        method = msg.get("method", "")
        params = msg.get("params", {})

        if method == "ping":
            print(json.dumps({"status": "ok", "message": "pong"}), flush=True)

        elif method == "add":
            print(json.dumps({"status": "ok", "result": params.get("a", 0) + params.get("b", 0)}), flush=True)

        elif method == "multiply":
            print(json.dumps({"status": "ok", "result": params.get("a", 0) * params.get("b", 0)}), flush=True)

        elif method == "square":
            n = params.get("n", 0)
            print(json.dumps({"status": "ok", "result": n * n, "formula": f"{n}^2 = {n*n}"}), flush=True)

        else:
            print(json.dumps({"status": "error", "message": f"Unknown method: {method}"}), flush=True)

    except json.JSONDecodeError:
        print(json.dumps({"status": "error", "message": "Invalid JSON"}), flush=True)
