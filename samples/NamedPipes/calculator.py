"""Calculator server using named pipes. It needs no extra packages."""
import argparse
import json
import os
import socket


def connect(name, mode):
    if os.name == "nt":
        return open(rf"\\.\pipe\{name}", mode, encoding="utf-8", newline="\n")
    # On Linux and macOS, .NET implements named pipes as Unix sockets
    temp = os.environ.get("TMPDIR", "/tmp")
    client = socket.socket(socket.AF_UNIX)
    client.connect(os.path.join(temp, f"CoreFxPipe_{name}"))
    return client.makefile(mode, encoding="utf-8", newline="\n")


parser = argparse.ArgumentParser()
parser.add_argument("--pipe-in", required=True)
parser.add_argument("--pipe-out", required=True)
args = parser.parse_args()

requests = connect(args.pipe_in, "r")
replies = connect(args.pipe_out, "w")


def reply(request_id, **fields):
    replies.write(json.dumps({"id": request_id, **fields}) + "\n")
    replies.flush()


for line in requests:
    line = line.strip()
    if not line or line == "__EXIT__":
        break

    # Stdout isn't the message channel here, so printing is harmless
    print(f"received: {line}", flush=True)

    request_id = None
    try:
        msg = json.loads(line)
        request_id = msg.get("id")
        method = msg.get("method", "")
        params = msg.get("params", {})

        if method == "ping":
            reply(request_id, status="ok", message="pong")
        elif method == "add":
            reply(request_id, status="ok", result=params.get("a", 0) + params.get("b", 0))
        elif method == "multiply":
            reply(request_id, status="ok", result=params.get("a", 0) * params.get("b", 0))
        elif method == "square":
            n = params.get("n", 0)
            reply(request_id, status="ok", result=n * n, formula=f"{n}^2 = {n*n}")
        else:
            reply(request_id, status="error", message=f"Unknown method: {method}")
    except json.JSONDecodeError:
        reply(request_id, status="error", message="Invalid JSON")
