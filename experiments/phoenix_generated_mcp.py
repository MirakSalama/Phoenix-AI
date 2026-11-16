"""Run a Phoenix-authored, user-enabled MCP handler over stdio."""

from contextlib import redirect_stdout
import json
from pathlib import Path
import runpy
import sys


def serve(manifest_path, module_path):
    tool = json.loads(Path(manifest_path).read_text(encoding="utf-8"))
    for line in sys.stdin:
        try:
            request = json.loads(line)
            if "id" not in request:
                continue
            response = {"jsonrpc": "2.0", "id": request["id"]}
            method = request.get("method")
            params = request.get("params", {})
            if method == "initialize":
                response["result"] = {
                    "protocolVersion": "2025-11-25",
                    "capabilities": {"tools": {}},
                    "serverInfo": {"name": tool["name"], "version": "1.0.0"},
                }
            elif method == "ping":
                response["result"] = {}
            elif method == "tools/list":
                response["result"] = {"tools": [tool]}
            elif method == "tools/call" and params.get("name") == tool["name"]:
                try:
                    with redirect_stdout(sys.stderr):
                        handle = runpy.run_path(module_path)["handle"]
                        result = handle(params.get("arguments", {}))
                    text = result if isinstance(result, str) else json.dumps(result, ensure_ascii=False)
                    response["result"] = {"content": [{"type": "text", "text": text}]}
                except Exception as error:
                    response["result"] = {"isError": True, "content": [{"type": "text", "text": str(error)}]}
            else:
                response["error"] = {"code": -32601, "message": "Unknown method or tool"}
            print(json.dumps(response), flush=True)
        except (ValueError, TypeError):
            continue


if __name__ == "__main__":
    serve(*sys.argv[1:])
