"""Small stdio MCP server users can copy and adapt for Phoenix AI."""

from datetime import datetime
import json
import sys


TOOL = {
    "name": "local_time",
    "description": "Get the current date, time and UTC offset on this PC.",
    "inputSchema": {"type": "object", "properties": {}, "additionalProperties": False},
}


def handle(method, params):
    if method == "initialize":
        return {
            "protocolVersion": "2025-11-25",
            "capabilities": {"tools": {}},
            "serverInfo": {"name": "phoenix-example", "version": "1.0.0"},
        }
    if method == "tools/list":
        return {"tools": [TOOL]}
    if method == "tools/call" and params.get("name") == TOOL["name"] and not params.get("arguments"):
        return {"content": [{"type": "text", "text": datetime.now().astimezone().isoformat(timespec="seconds")}]}
    if method == "ping":
        return {}
    raise ValueError("Unsupported request")


for line in sys.stdin:
    try:
        request = json.loads(line)
        if "id" not in request:
            continue
        response = {"jsonrpc": "2.0", "id": request["id"]}
        try:
            response["result"] = handle(request["method"], request.get("params", {}))
        except (KeyError, ValueError):
            response["error"] = {"code": -32601, "message": "Unsupported request"}
        print(json.dumps(response), flush=True)
    except (ValueError, TypeError):
        continue
