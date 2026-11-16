"""Serve the fused Gemma 4 E2B text decoder on Phoenix/Hawk Point."""

import argparse
from contextlib import redirect_stdout
from importlib import import_module
import json
import os
import sys
import traceback

from tokenizers import Tokenizer

from gemma4_npu_chat import ROOT, emit, report_status, serve
from local_context import assistant_prefix

os.environ["NPU_CACHE_HOME"] = str(ROOT / "cache" / "gemma4-e2b-x8-programs")
from gemma4_x8_model import MODEL, TIERS, X8Gemma4


def load_template():
    try:
        from jinja2.sandbox import ImmutableSandboxedEnvironment
    except ModuleNotFoundError as error:
        if error.name != "jinja2":
            raise
        # Reuse the template dependency installed with the original Gemma worker.
        sys.path.append(str(ROOT / "cache" / "gemma-iron-env" / "Lib" / "site-packages"))
        from jinja2.sandbox import ImmutableSandboxedEnvironment
    env = ImmutableSandboxedEnvironment(trim_blocks=True, lstrip_blocks=True,
                                        extensions=["jinja2.ext.loopcontrols"])
    return env.from_string((MODEL / "chat_template.jinja").read_text(encoding="utf-8"))


class FusedChat(import_module("010_lfm25_npu_chat").NPUChat):
    reasoning_format = "gemma"

    def __init__(self, status=report_status, tiers=TIERS):
        if os.environ.get("PHOENIX_NPU_PROGRAMS"):
            import npu_direct as nd

            nd.require_phoenix()
        else:
            from aie.iron import get_current_device

            if type(get_current_device()).__name__ != "NPU1":
                raise RuntimeError("The fused Gemma worker requires a Phoenix/Hawk Point NPU.")
        if not (MODEL / "model.safetensors").is_file():
            raise RuntimeError("The local Gemma 4 E2B checkpoint is missing.")
        self.template = load_template()
        self.tokenizer = Tokenizer.from_file(str(MODEL / "tokenizer.json"))
        self.model = X8Gemma4(status, tiers)
        self.capacity = self.model.capacity
        self.eos_ids = frozenset(self.model.config["eos_token_id"])
        self.bos_token = self.tokenizer.id_to_token(self.model.config["text_config"]["bos_token_id"])
        self.consumed = []

    def encode(self, messages, thinking=False):
        messages, prefix = assistant_prefix(messages)
        def raise_exception(message):
            raise ValueError(message)

        text = self.template.render(messages=messages, add_generation_prompt=True,
                                    bos_token=self.bos_token, raise_exception=raise_exception,
                                    enable_thinking=thinking)
        return self.tokenizer.encode(text + prefix, add_special_tokens=False).ids

    def is_eos(self, token_id):
        return token_id in self.eos_ids

    def respond(self, messages, limit=96, on_text=None, *, thinking=False):
        ids = self.encode(messages, thinking)
        if thinking:
            return dict(self.respond_thinking(ids, limit, on_text), capacity=self.capacity)
        stream = None if on_text is None else lambda text: on_text(text, False)
        reply = self.respond_ids(ids, limit, on_text=stream)
        return dict(reply, capacity=self.capacity)


PARIS = "What is the capital of France? Answer with one word."
BENCHMARK = "Give me three short tips for organizing my notes."


def verify(chat):
    long = ("The capital of France is Paris. " + "This is only background context. " * 83 +
            "Based on the first sentence, what is the capital of France? Answer with one word.")
    results = []
    for prompt, limit in ((PARIS, 12), (BENCHMARK, 32), (long, 12)):
        reply = chat.respond([{"role": "user", "content": prompt}], limit)
        emit("reply", **reply)
        results.append(reply)
    if any(results[i]["response"].strip().casefold().rstrip(".") != "paris" for i in (0, 2)):
        raise RuntimeError("Fused Gemma failed the short- or long-context answer check.")
    speed = results[1]["decode_tokens_per_s"] or 0
    if results[1]["generated_tokens"] != 32 or speed <= 1.2:
        raise RuntimeError("Fused Gemma did not beat the existing worker's decode benchmark.")
    marker = MODEL / "verified-x8-npu1.json"
    pending = marker.with_suffix(".tmp")
    pending.write_text(json.dumps({"device": results[1]["device"], "capacity": chat.capacity,
                                   "decode_tokens_per_s": speed}), encoding="utf-8")
    pending.replace(marker)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--verify", action="store_true")
    mode.add_argument("--smoke", action="store_true")
    args = parser.parse_args()
    chat = None

    def start_chat():
        # Keep the pyxrt context alive through process exit.
        global chat
        chat = FusedChat(tiers=(64,) if args.smoke else TIERS)
        return chat

    try:
        with redirect_stdout(sys.stderr):
            if args.verify:
                verify(start_chat())
            elif args.smoke:
                engine = start_chat()
                for prompt, limit in ((PARIS, 12), (BENCHMARK, 32)):
                    emit("reply", **engine.respond([{"role": "user", "content": prompt}], limit))
            else:
                serve(start_chat)
    except Exception as error:
        traceback.print_exc()
        emit("fatal", text=str(error))
        os._exit(1)
    os._exit(0)
