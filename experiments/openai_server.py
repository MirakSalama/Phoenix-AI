"""OpenAI-compatible HTTP server for LFM2.5-230M on the Phoenix NPU.

Wraps the eight-core engine from experiment 010. One NPU, one session, greedy
decoding: requests are serialized and the model's state is reused when the
token prefix matches the previous turn.

    GET  /v1/models
    POST /v1/chat/completions   (stream and non-stream)

Start it with scripts/serve_openai.ps1 so the XRT and IRON environments are
loaded. Tool calls are not implemented; `tools` in a request is ignored.
"""

import argparse
import importlib.util
import json
import sys
import threading
import time
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

HERE = Path(__file__).resolve().parent
MODEL_ID = "lfm2.5-230m-npu"
LOCK = threading.Lock()
CHAT = None


def log(message):
    print(message, file=sys.stderr, flush=True)


def load_chat():
    spec = importlib.util.spec_from_file_location("npu_chat", HERE / "010_lfm25_npu_chat.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module.NPUChat(log=log)


def flatten(content):
    if isinstance(content, str) or content is None:
        return content or ""
    parts = []
    for part in content:
        if isinstance(part, dict) and part.get("type") in (None, "text"):
            parts.append(part.get("text") or "")
    return "".join(parts)


def reply_chunks(text_id, created, model, delta, finish=None):
    return {"id": text_id, "object": "chat.completion.chunk", "created": created,
            "model": model,
            "choices": [{"index": 0, "delta": delta, "finish_reason": finish}]}


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):
        log(f"{self.address_string()} {fmt % args}")

    def send_json(self, obj, code=200):
        body = json.dumps(obj).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def error(self, code, message, kind="invalid_request_error"):
        self.send_json({"error": {"message": message, "type": kind}}, code)

    def do_GET(self):
        path = self.path.split("?", 1)[0].rstrip("/")
        if path in ("/v1/models", "/models"):
            return self.send_json({"object": "list", "data": [{
                "id": MODEL_ID, "object": "model", "created": int(time.time()),
                "owned_by": "phoenix-npu"}]})
        if path in ("/health", "/v1/health"):
            return self.send_json({"status": "ok", "model": MODEL_ID})
        self.error(404, f"No route for GET {self.path}")

    def do_POST(self):
        path = self.path.split("?", 1)[0].rstrip("/")
        length = int(self.headers.get("Content-Length") or 0)
        raw = self.rfile.read(length) if length else b""
        if path not in ("/v1/chat/completions", "/chat/completions"):
            return self.error(404, f"No route for POST {self.path}")
        try:
            request = json.loads(raw or b"{}")
            messages = [{"role": m.get("role", "user"), "content": flatten(m.get("content"))}
                        for m in request.get("messages") or []]
            if not messages:
                raise ValueError("messages is empty")
        except (ValueError, AttributeError, TypeError) as exc:
            return self.error(400, f"Bad request: {exc}")

        model = request.get("model") or MODEL_ID
        limit = request.get("max_completion_tokens") or request.get("max_tokens") or 256
        stream = bool(request.get("stream"))
        created = int(time.time())
        completion_id = f"chatcmpl-{uuid.uuid4().hex[:24]}"

        if stream:
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream")
            self.send_header("Cache-Control", "no-cache")
            self.send_header("Connection", "close")
            self.end_headers()
            self.close_connection = True

            def emit(payload):
                data = payload if isinstance(payload, str) else json.dumps(payload)
                self.wfile.write(f"data: {data}\n\n".encode("utf-8"))
                self.wfile.flush()

            try:
                emit(reply_chunks(completion_id, created, model, {"role": "assistant", "content": ""}))
                with LOCK:
                    result = CHAT.respond(messages, int(limit),
                                          on_text=lambda text: emit(reply_chunks(
                                              completion_id, created, model, {"content": text})))
                emit(reply_chunks(completion_id, created, model, {}, "stop"))
                emit("[DONE]")
            except ValueError as exc:
                emit({"error": {"message": str(exc), "type": "invalid_request_error"}})
                emit("[DONE]")
            except (BrokenPipeError, ConnectionResetError):
                log("client disconnected")
            except InterruptedError:
                emit("[DONE]")
            return

        try:
            with LOCK:
                result = CHAT.respond(messages, int(limit))
        except ValueError as exc:
            return self.error(400, str(exc))
        self.send_json({
            "id": completion_id, "object": "chat.completion", "created": created,
            "model": model,
            "choices": [{"index": 0, "finish_reason": "stop",
                         "message": {"role": "assistant", "content": result["response"]}}],
            "usage": {"prompt_tokens": result["prompt_tokens"],
                      "completion_tokens": result["generated_tokens"],
                      "total_tokens": result["prompt_tokens"] + result["generated_tokens"]},
            "phoenix": result,
        })


def main():
    global CHAT
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8645)
    args = parser.parse_args()

    log(f"Loading LFM2.5 230M on the Phoenix NPU (first run compiles programs)...")
    CHAT = load_chat()
    log(f"Context capacity: {CHAT.model.capacity} tokens")
    server = ThreadingHTTPServer((args.host, args.port), Handler)
    log(f"Listening on http://{args.host}:{args.port}/v1")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()
        import npu_direct as nd
        nd.finish()


if __name__ == "__main__":
    main()
