"""Gemma 4 E2B text chat through AMD IRON's XRT graph runtime."""

import argparse
from contextlib import contextmanager, redirect_stdout
import faulthandler
from functools import cache
import hashlib
import json
import math
import os
from pathlib import Path
import sys
from time import monotonic
import traceback

from local_reasoning import ReasoningReply
from local_context import assistant_prefix, generation_stop, response_budget


from phoenix_paths import MODELS, ROOT

MODEL = MODELS / "gemma4-e2b"
CONTEXT = 1024
OUTPUT = sys.stdout


def emit(kind, **fields):
    print(json.dumps(dict(kind=kind, **fields)), file=OUTPUT, flush=True)


def report_status(text, progress=None):
    emit("status", text=text, progress=progress)


def trace(message):
    if os.environ.get("NPU_CHAT_DIAGNOSTICS") == "1":
        print(message, file=sys.stderr, flush=True)


@contextmanager
def reuse_kernel_measurements():
    import aie.iron.kernel as kernel
    from aie.utils import config
    from iron.operators.mha import MHA

    # IRON stages the same object in a fresh temp directory for each layer.
    # Cache its LLVM analysis by content and entry, independent of that path.
    tool_names = ("readobj_path", "nm_path", "objcopy_path", "ar_path")
    tool_paths = {name: getattr(config, name) for name in tool_names}
    linked = kernel.linked
    core_bytes = MHA.core_bytes
    measurements = {}
    core_sizes = {}

    def measure(obj, entry):
        with obj.open("rb") as file:
            key = (hashlib.file_digest(file, "sha256").digest(), entry)
        if key not in measurements:
            measurements[key] = linked(obj, entry)
        return measurements[key]

    def measure_core(op, dev, B_q, B_kv, pv_cores):
        # The bundled MHA's core footprint depends on the kernel dimensions,
        # masking mode and device. Layer identity and cache length are irrelevant.
        key = (id(dev), op.d, op.causal, op.window, op.packed, B_q, B_kv, pv_cores)
        if key not in core_sizes:
            core_sizes[key] = (dev, core_bytes(op, dev, B_q, B_kv, pv_cores))
        return core_sizes[key][1]

    for name, resolve in tool_paths.items():
        setattr(config, name, cache(resolve))
    kernel.linked = measure
    MHA.core_bytes = measure_core
    try:
        yield
    finally:
        trace(f"IRON measurements: {len(core_sizes)} attention configurations, "
              f"{len(measurements)} unique kernel analyses")
        for name, resolve in tool_paths.items():
            setattr(config, name, resolve)
        kernel.linked = linked
        MHA.core_bytes = core_bytes


def prepare(model, device, status):
    versions = []
    shapes = model.shapes()
    steps = len(shapes) + 2
    try:
        # Plan both per-step graphs before allocating their shared XRT arena.
        with reuse_kernel_measurements():
            for index, inputs in enumerate(shapes, 2):
                name = "prompt" if "ids" in inputs else "decode"
                status(f"Step {index}/{steps}: Preparing the {name} graph")
                began = monotonic()
                version = model.compile(dev=device, full_elf=False, **inputs)
                versions.append(version)
                trace(f"{name.capitalize()} graph prepared in {monotonic() - began:.2f}s; "
                      f"{len(version.recording.steps)} operations")

        label = f"Step {steps}/{steps}: Loading weights into NPU memory"
        weights = {key: handle.nbytes for version in versions
                   for key, (_, handle) in version.recording.weights.items()}
        total = sum(weights.values())
        copied = 0
        last_update = monotonic()

        def uploaded(piece):
            nonlocal copied, last_update
            copied += piece.nbytes
            now = monotonic()
            if total and (now - last_update >= 0.5 or copied >= total):
                status(label, min(copied / total, 1.0))
                last_update = now

        status(label, 0.0)
        began = monotonic()
        trace(f"XRT weights to copy: {len(weights)} buffers, {total} bytes")
        for version in versions:
            version.load(release=uploaded)
        model.loaded(versions)
        trace(f"XRT setup and weight copy finished in {monotonic() - began:.2f}s; {copied} bytes copied")
    except BaseException:
        model.release()
        raise


def configure(program_cache="gemma-iron-programs"):
    packages = Path(sys.prefix) / "Lib" / "site-packages"
    mlir = packages / "mlir_aie"
    peano = packages / "llvm-aie"
    source = ROOT / "cache" / "amd-iron-models"
    if not (source / "iron" / "applications" / "gemma4").is_dir():
        raise RuntimeError("The AMD IRON Gemma model runner is missing.")
    sys.path.insert(0, str(source))
    os.environ["VIRTUAL_ENV"] = sys.prefix
    os.environ["MLIR_AIE_INSTALL_DIR"] = str(mlir)
    os.environ.setdefault("CXX", "cl.exe")
    if (peano / "bin" / "clang++.exe").is_file():
        os.environ["PEANO_INSTALL_DIR"] = str(peano)
    os.environ["NPU_CACHE_HOME"] = str(ROOT / "cache" / program_cache)
    os.environ["PATH"] = os.pathsep.join((str(Path(sys.prefix) / "Scripts"), str(mlir / "bin"),
                                          str(mlir / "lib"), os.environ["PATH"]))


class IronChat:
    capacity = CONTEXT
    name = "Local model"
    reasoning_format = None

    def visible_text(self, text, streaming=False):
        return text

    def configure_thinking(self, mode):
        self.reasoning_format = mode
        tokenizer = self.model.tokenizer
        template = "{%- set enable_thinking = true -%}" + tokenizer.chat_template
        self.thinking_tokenizer = type(tokenizer)(tokenizer.encoding, template, tokenizer.bos_token)

    def respond(self, messages, limit=96, on_text=None, *, thinking=False):
        tokenizer = self.thinking_tokenizer if thinking else self.model.tokenizer
        messages, prefix = assistant_prefix(messages)
        tokens = tokenizer.chat(messages) + tokenizer.encoding.encode(prefix)
        limit = response_budget(self.capacity, len(tokens), limit)
        generated = []
        shown = ""
        reasoning = None
        if thinking:
            def marker_id(marker):
                ids = tokenizer.encoding.encode(marker)
                return ids[0] if len(ids) == 1 else None

            reasoning = ReasoningReply(self.reasoning_format, marker_id, tokenizer.decode, on_text)
        for token in self.model.stream(tokens, limit):
            if token in self.model.eos:
                continue
            generated.append(token)
            if reasoning is not None:
                reasoning.add(token)
                continue
            text = self.visible_text(tokenizer.decode(generated), streaming=True)
            if on_text and text and text != shown and not text.endswith("\ufffd"):
                replace = not text.startswith(shown)
                on_text(text if replace else text[len(shown):], replace)
                shown = text
        stats = self.model.stats
        reply = reasoning.result() if reasoning is not None else {
            "response": self.visible_text(tokenizer.decode(generated))
        }
        return reply | {
            "prompt_tokens": len(tokens),
            "generated_tokens": len(generated),
            "generation_stop": generation_stop(self.capacity, len(tokens), limit, len(generated)),
            "time_to_first_token_s": round(stats.ttft, 3),
            "decode_tokens_per_s": round(stats.tokens_per_s, 1) if math.isfinite(stats.tokens_per_s) else None,
            "capacity": self.capacity,
            "device": self.device,
        }


class GemmaChat(IronChat):
    name = "Gemma 4 E2B"

    def __init__(self, status=report_status):
        configure()
        from gemma4_windows_model import WindowsGemma4
        from iron.common.image.packaging import require_device

        if not (MODEL / "model.safetensors").is_file():
            raise RuntimeError("The Gemma 4 E2B checkpoint download is incomplete.")
        device = require_device()
        self.device = str(device)
        trace(f"Gemma runtime: Python {sys.version.split()[0]}, device {self.device}, "
              f"context {CONTEXT}, checkpoint {MODEL / 'model.safetensors'}, cache {os.environ['NPU_CACHE_HOME']}")
        status("Step 1: Opening the Gemma 4 E2B checkpoint")
        self.model = WindowsGemma4.from_checkpoint(MODEL, max_seq_len=self.capacity)
        self.configure_thinking("gemma")
        prepare(self.model, device, status)


class NoVisibleAnswer(ValueError):
    pass


def serve(factory=GemmaChat):
    diagnostics = os.environ.get("NPU_CHAT_DIAGNOSTICS") == "1"
    if diagnostics:
        trace("Warmup diagnostics: recording a Python stack every 45s until the worker is ready")
        faulthandler.enable(file=sys.stderr)
        faulthandler.dump_traceback_later(45, repeat=True, file=sys.stderr)
    try:
        chat = factory()
    finally:
        if diagnostics:
            faulthandler.cancel_dump_traceback_later()
    emit("ready", capacity=chat.capacity)
    for line in sys.stdin:
        job = json.loads(line)
        if job["kind"] == "shutdown":
            break
        request_id = job["id"]
        try:
            if job["kind"] == "tool_chat":
                if not hasattr(chat, "respond_tools"):
                    raise ValueError("This worker does not support structured tool calls.")
                emit("reply", id=request_id, **chat.respond_tools(job["messages"], job["tools"], job["max_tokens"]))
                continue
            options = {}
            if "response_schema" in job:
                options["response_schema"] = job["response_schema"]
            if job.get("thinking") is True:
                if getattr(chat, "reasoning_format", None) is None:
                    raise ValueError("Thinking mode is unavailable for this model.")
                options["thinking"] = True
            result = chat.respond(job["messages"], job.get("max_tokens", 96),
                                  on_text=lambda text, replace: emit("chunk", id=request_id, text=text, replace=replace),
                                  **options)
            emit("reply", id=request_id, **result)
        except NoVisibleAnswer as error:
            emit("error", id=request_id, text=str(error), code="no_visible_answer")
        except ValueError as error:
            emit("error", id=request_id, text=str(error))


def verify(engine):
    prompt = "What is the capital of France? Answer with one word."
    result = engine.respond([{"role": "user", "content": prompt}], limit=12)
    emit("reply", **result)
    if "paris" not in result["response"].casefold() or not engine.device.startswith("NPU1"):
        raise RuntimeError("Gemma did not pass the XDNA 1 text generation check.")
    marker = MODEL / "verified-npu1.json"
    pending = marker.with_suffix(".tmp")
    pending.write_text(json.dumps(result), encoding="utf-8")
    pending.replace(marker)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    command = parser.add_mutually_exclusive_group()
    command.add_argument("--prompt")
    command.add_argument("--verify", action="store_true")
    parser.add_argument("--max-tokens", type=int, default=32)
    args = parser.parse_args()
    try:
        with redirect_stdout(sys.stderr):
            if args.verify:
                verify(GemmaChat())
            elif args.prompt is None:
                serve()
            else:
                engine = GemmaChat()
                emit("reply", **engine.respond([{"role": "user", "content": args.prompt}], args.max_tokens))
    except Exception as error:
        traceback.print_exc()
        emit("fatal", text=str(error))
        os._exit(1)
    os._exit(0)
