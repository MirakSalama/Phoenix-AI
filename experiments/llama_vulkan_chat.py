"""Discover Vulkan adapters and run NPU Chat's GGUF models on one of them."""

import argparse
from pathlib import Path
from contextlib import redirect_stdout
from functools import partial
import json
import os
import re
import secrets
import socket
import subprocess
import sys
from time import monotonic, sleep
import traceback
from urllib.error import HTTPError, URLError
from urllib.request import Request, ProxyHandler, build_opener

from gemma4_npu_chat import NoVisibleAnswer, ROOT, emit, report_status, serve
from gguf_context import fit_context
from lfm_iron_chat import lfm3_visible_text
from local_context import context_reserve, generation_stop, response_budget
from phoenix_paths import MODELS as MODEL_ROOT


SERVER = ROOT / "cache" / "llama-vulkan" / "llama-server.exe"
MODELS = {
    "lfm25-230m": "LFM2.5-230M-Q4_K_M.gguf",
    "lfm25-350m": "LFM2.5-350M-Q4_K_M.gguf",
    "lfm3-1.7b": "LFM3-1.7B-QAD-Q4_0.gguf",
    "gemma4-e2b": "gemma-4-E2B-it-Q4_0.gguf",
    "qwen3.5-2b": "Qwen3.5-2B-Q4_K_M.gguf",
}


def discover_devices():
    if not SERVER.is_file():
        return []
    result = subprocess.run([str(SERVER.with_name("llama-bench.exe")), "--list-devices"],
                            capture_output=True, encoding="utf-8", errors="replace",
                            check=True, timeout=15, creationflags=subprocess.CREATE_NO_WINDOW)
    output = result.stdout + "\n" + result.stderr
    # ggml's UMA flag also works when vulkaninfo is absent. Prefer Vulkan's
    # physical device type when the system utility provides an exact name.
    uma = dict(re.findall(r"ggml_vulkan: (\d+) = [^\n]*\| uma: ([01])", output))
    types = {}
    try:
        info = subprocess.run(["vulkaninfo.exe", "--summary"], capture_output=True,
                              encoding="utf-8", errors="replace", timeout=5,
                              creationflags=subprocess.CREATE_NO_WINDOW)
        for block in re.split(r"^GPU\d+:\s*$", info.stdout, flags=re.MULTILINE)[1:]:
            name = re.search(r"^\s*deviceName\s*=\s*(.+)$", block, flags=re.MULTILINE)
            kind = re.search(r"^\s*deviceType\s*=\s*PHYSICAL_DEVICE_TYPE_(\w+)", block,
                             flags=re.MULTILINE)
            if name and kind:
                types[name[1].strip()] = kind[1]
    except (OSError, subprocess.TimeoutExpired):
        pass
    devices = []
    for index, name, free in re.findall(
            r"^\s*Vulkan(\d+): (.+) \(\d+ MiB, (\d+) MiB free\)\s*$", output, re.MULTILINE):
        kind = types.get(name)
        if kind is None and index in uma:
            kind = "INTEGRATED_GPU" if uma[index] == "1" else "DISCRETE_GPU"
        if kind in ("INTEGRATED_GPU", "DISCRETE_GPU"):
            devices.append({"Id": "Vulkan" + index, "Name": name,
                            "Integrated": kind == "INTEGRATED_GPU", "FreeMiB": int(free)})
    return devices


class VulkanChat:
    def __init__(self, model_id, device, device_name, *, benchmark=False, context_limit=0, model_file=None):
        self.server = None
        self.capacity = 4096 if model_id == "lfm25-230m" else 1024
        self.is_lfm3 = model_id == "lfm3-1.7b"
        self.think_token = None
        self.reasoning_format = "server" if model_id in ("lfm3-1.7b", "gemma4-e2b", "qwen3.5-2b") else None
        cpu_only = device == "none"
        self.device = device_name if cpu_only else f"{device_name} · Vulkan"
        if not cpu_only and not re.fullmatch(r"Vulkan\d+", device):
            raise ValueError("Expected a Vulkan device ID.")
        self.opener = build_opener(ProxyHandler({}))
        self.key = secrets.token_urlsafe(24)
        model = model_file if model_file is not None else MODEL_ROOT / model_id / MODELS[model_id]
        if not SERVER.is_file() or not model.is_file():
            raise RuntimeError("The local Vulkan runner or GGUF model is missing.")
        placement = [
            "--model", str(model), "--device", device, "--split-mode", "none",
            "--n-gpu-layers", "0" if cpu_only else "all", "--fit", "off",
            "--parallel", "1", "--batch-size", "512", "--ubatch-size", "512",
        ]
        if cpu_only and context_limit > 0:
            threads = str(min(4, max(1, (os.cpu_count() or 2) // 2)))
            placement += ["--threads", threads, "--threads-batch", threads]
        candidates = [self.capacity]
        if model_file is not None:
            requested = min(context_limit, 4096) if context_limit > 0 else 4096
            candidates = list(dict.fromkeys([max(1024, requested), 1024]))
        elif not benchmark:
            report_status(f"Estimating context capacity on {device_name}")
            try:
                gpu = None if cpu_only else next((gpu for gpu in discover_devices() if gpu["Id"] == device), None)
                best = fit_context(SERVER.with_name("llama-fit-params.exe"), placement, device, gpu, model_id, context_limit)
                smaller = max(1024, best // 2 // 256 * 256)
                candidates = list(dict.fromkeys([best, smaller, min(smaller, self.capacity), 1024]))
            except (OSError, ValueError, KeyError, subprocess.SubprocessError) as error:
                print(f"Context estimation unavailable; using {self.capacity} tokens: {error}", file=sys.stderr)
        for index, context in enumerate(candidates):
            report_status(f"Loading {device_name} · {context:,} context tokens")
            try:
                self.start_server(model, placement, context, model_file is None and model_id != "qwen3.5-2b")
                break
            except RunnerExited:
                if index == len(candidates) - 1:
                    raise
                report_status(f"{device_name} could not start with {context:,} tokens. Trying a smaller context.")

    def start_server(self, model, placement, context, bundled_template=True):
        with socket.socket() as port_socket:
            port_socket.bind(("127.0.0.1", 0))
            port = port_socket.getsockname()[1]
        self.base = f"http://127.0.0.1:{port}"
        try:
            self.server = subprocess.Popen([
                str(SERVER), *placement, "--ctx-size", str(context), "--no-context-shift",
                "--jinja", *(["--chat-template-file", str(model.parent / "chat_template.jinja")] if bundled_template else []),
                "--reasoning-format", "deepseek", "--no-reasoning-preserve", "--offline", "--no-ui",
                "--host", "127.0.0.1", "--port", str(port), "--api-key", self.key,
            ], stdin=subprocess.DEVNULL, stdout=sys.stderr, stderr=sys.stderr,
                creationflags=subprocess.CREATE_NO_WINDOW)
            deadline = monotonic() + 90
            while monotonic() < deadline:
                if self.server.poll() is not None:
                    raise RunnerExited(f"The local runner exited with code {self.server.returncode}.")
                try:
                    with self.request("/health", timeout=2) as response:
                        if json.load(response).get("status") == "ok" and self.server.poll() is None:
                            with self.request("/props", timeout=5) as properties:
                                actual = json.load(properties)["default_generation_settings"]["n_ctx"]
                            self.capacity = int(actual)
                            if self.is_lfm3:
                                with self.request("/tokenize", {"content": "<think>", "parse_special": True}, timeout=5) as tokens:
                                    ids = json.load(tokens)["tokens"]
                                    if len(ids) == 1:
                                        self.think_token = ids[0]
                            return
                except URLError as error:
                    if isinstance(error.reason, PermissionError):
                        raise RuntimeError("Windows denied localhost access for the local model runner.") from error
                except TimeoutError:
                    pass
                sleep(0.2)
            raise RuntimeError(f"{self.device} did not finish loading the model within 90 seconds.")
        except BaseException:
            self.close()
            raise

    def request(self, path, data=None, timeout=180):
        headers = {"Authorization": f"Bearer {self.key}", "Content-Type": "application/json"}
        body = None if data is None else json.dumps(data).encode("utf-8")
        return self.opener.open(Request(self.base + path, data=body, headers=headers), timeout=timeout)

    def measure_reply_budget(self, payload, limit):
        template = {key: payload[key] for key in ("messages", "tools", "chat_template_kwargs", "reasoning_effort")
                    if key in payload}
        try:
            with self.request("/apply-template", template, timeout=5) as response:
                prompt = json.load(response)["prompt"]
            with self.request("/tokenize", {"content": prompt, "add_special": True}, timeout=5) as response:
                prompt_tokens = len(json.load(response)["tokens"])
        except (URLError, TimeoutError, KeyError, TypeError, ValueError):
            # Older bundled runners can still enforce their native context size.
            return limit if limit > 0 else -1
        return response_budget(self.capacity, prompt_tokens, limit)

    def respond_tools(self, messages, tools, limit):
        payload = {
            "messages": messages, "max_tokens": limit, "temperature": 0,
            "chat_template_kwargs": {"enable_thinking": False}, "reasoning_effort": "none",
        }
        if tools:
            payload.update(tools=tools, tool_choice="auto")
        if self.think_token is not None:
            payload["logit_bias"] = {str(self.think_token): -100}
        payload["max_tokens"] = self.measure_reply_budget(payload, limit)
        start = monotonic()
        try:
            with self.request("/v1/chat/completions", payload, timeout=65) as response:
                result = json.load(response)
        except HTTPError as error:
            raise ValueError(f"The local model rejected the structured request (HTTP {error.code}).") from error
        choice = result["choices"][0]
        message = choice["message"]
        usage = result.get("usage", {})
        return {
            "message": message, "response": message.get("content") or "",
            "generation_stop": generation_stop(self.capacity, usage.get("prompt_tokens", 0),
                payload["max_tokens"], usage.get("completion_tokens", 0), ended=choice.get("finish_reason") != "length"),
            "generated_tokens": usage.get("completion_tokens", 0), "prompt_tokens": usage.get("prompt_tokens", 0),
            "elapsed_s": round(monotonic() - start, 3), "device": self.device, "capacity": self.capacity,
        }

    def respond(self, messages, limit=96, on_text=None, *, thinking=False, response_schema=None):
        payload = {
            "messages": messages, "max_tokens": limit if limit > 0 else -1, "temperature": 0, "stream": True,
            "stream_options": {"include_usage": True},
            "chat_template_kwargs": {"enable_thinking": thinking},
        }
        if not thinking:
            payload["reasoning_effort"] = "none"
            # LFM3 can open a fresh reasoning block even after a closed one in
            # the rendered prompt. Keep its output budget for the visible reply.
            if self.think_token is not None:
                payload["logit_bias"] = {str(self.think_token): -100}
        if response_schema is not None:
            payload["response_format"] = {"type": "json_object", "schema": response_schema}
        payload["max_tokens"] = self.measure_reply_budget(payload, limit)
        answer, thoughts = [], []
        shown = ""
        usage, timings = {}, {}
        start = monotonic()
        first = None
        finish_reason = None
        next_update = start
        try:
            with self.request("/v1/chat/completions", payload) as response:
                for line in response:
                    if not line.startswith(b"data: "):
                        continue
                    data = line[6:].strip()
                    if data == b"[DONE]":
                        break
                    event = json.loads(data)
                    if "error" in event:
                        raise ValueError(event["error"]["message"])
                    choices = event.get("choices") or []
                    if choices:
                        finish_reason = choices[0].get("finish_reason") or finish_reason
                        delta = choices[0].get("delta", {})
                        text = delta.get("content")
                        thought = delta.get("reasoning_content")
                        if (text or thought) and first is None:
                            first = monotonic() - start
                        if thought:
                            thoughts.append(thought)
                        if text:
                            answer.append(text)
                            if on_text:
                                if not self.is_lfm3 or not thinking and self.think_token is not None:
                                    on_text(text, False)
                                elif monotonic() >= next_update:
                                    next_update = monotonic() + 0.07
                                    visible = lfm3_visible_text("".join(answer), streaming=True)
                                    if visible and visible != shown:
                                        replace = not visible.startswith(shown)
                                        on_text(visible if replace else visible[len(shown):], replace)
                                        shown = visible
                    usage = event.get("usage") or usage
                    timings = event.get("timings") or timings
        except HTTPError as error:
            if error.code < 500:
                detail = json.load(error).get("error", {}).get("message", str(error))
                raise ValueError(detail) from error
            raise RuntimeError(f"Vulkan runner returned HTTP {error.code}.") from error
        if not usage:
            raise RuntimeError("The Vulkan runner closed before completing the reply.")
        text = "".join(answer)
        if self.is_lfm3:
            text = lfm3_visible_text(text)
        finishing_prefill = messages[-1]["role"] == "assistant" and finish_reason == "stop" and not thoughts
        if not text.strip() and not finishing_prefill:
            message = ("The token budget ended before a visible answer." if thinking or thoughts or
                       (self.is_lfm3 and "<think>" in "".join(answer)) else "The model returned no visible answer.")
            raise NoVisibleAnswer(message)
        total = usage["completion_tokens"]
        reply = {
            "response": text, "prompt_tokens": usage["prompt_tokens"],
            "generated_tokens": total, "capacity": self.capacity, "device": self.device,
            "time_to_first_token_s": round(first or 0.0, 3),
            "prompt_tokens_per_s": timings.get("prompt_per_second"),
            "decode_tokens_per_s": timings.get("predicted_per_second"),
            "generation_stop": ("context" if payload["max_tokens"] < limit or limit <= 0 or
                                usage["prompt_tokens"] + total >= self.capacity - context_reserve(self.capacity)
                                else "limit") if finish_reason == "length" else "eos",
        }
        cached = (usage.get("prompt_tokens_details") or {}).get("cached_tokens")
        if not isinstance(cached, int):
            cached = timings.get("cache_n")
        if isinstance(cached, int) and 0 <= cached <= usage["prompt_tokens"]:
            reply["cache_read_tokens"] = cached
            reply["cache_write_tokens"] = usage["prompt_tokens"] - cached
        if thinking:
            details = usage.get("completion_tokens_details") or {}
            count = details.get("reasoning_tokens")
            if count is None and thoughts:
                with self.request("/tokenize", {"content": "".join(thoughts)}) as response:
                    count = len(json.load(response)["tokens"])
            reply["thinking_tokens"] = min(total, count or 0)
        return reply

    def close(self):
        if self.server is not None and self.server.poll() is None:
            self.server.terminate()
            try:
                self.server.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.server.kill()
                self.server.wait()


class RunnerExited(RuntimeError):
    pass


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("model", choices=MODELS, nargs="?")
    parser.add_argument("--model-file", type=Path)
    parser.add_argument("--probe", action="store_true")
    parser.add_argument("--device")
    parser.add_argument("--device-name")
    parser.add_argument("--cpu", action="store_true")
    parser.add_argument("--benchmark", action="store_true")
    parser.add_argument("--context-limit", type=int, default=0)
    args = parser.parse_args()
    if args.probe:
        print(json.dumps(discover_devices()))
        sys.exit(0)
    if bool(args.model) == bool(args.model_file):
        parser.error("select one bundled model or one GGUF file")
    chat = None

    def start_chat():
        global chat
        if args.cpu:
            device, name = "none", "CPU"
        elif args.device and args.device_name:
            device, name = args.device, args.device_name
        else:
            integrated = [gpu for gpu in discover_devices() if gpu["Integrated"]]
            if not integrated:
                raise RuntimeError("No integrated Vulkan GPU was detected.")
            chosen = max(integrated, key=lambda gpu: gpu["FreeMiB"])
            device, name = chosen["Id"], chosen["Name"]
        chat = VulkanChat(args.model, device, name, benchmark=args.benchmark, context_limit=args.context_limit,
                          model_file=args.model_file)
        return chat

    try:
        with redirect_stdout(sys.stderr):
            serve(start_chat)
    except Exception as error:
        traceback.print_exc()
        emit("fatal", text=str(error))
        sys.exit(1)
    finally:
        if chat is not None:
            chat.close()
