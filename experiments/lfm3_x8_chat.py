"""Serve the fused LFM3-1.7B worker on the Phoenix/Hawk Point NPU."""

import argparse
from contextlib import redirect_stdout
from importlib import import_module
import json
import os
import sys
import traceback

from tokenizers import Tokenizer

from gemma4_npu_chat import ROOT, emit, report_status, serve
from lfm_iron_chat import lfm3_visible_text
from local_context import assistant_prefix

os.environ["NPU_CACHE_HOME"] = str(ROOT / "cache" / "lfm3-1.7b-x8-programs")
from lfm3_x8_model import MODEL, TIERS, X8Lfm3


def render_chat(messages, thinking=False):
    messages, prefix = assistant_prefix(messages)
    # Prior assistant turns carry only the final answer.
    parts = ["<|startoftext|>"]
    for message in messages:
        role, content = message["role"], message["content"]
        if role == "assistant":
            content = "<think></think>" + content
        parts.append(f"<|im_start|>{role}\n{content}<|im_end|>\n")
    parts.append("<|im_start|>assistant\n<think>" + ("" if thinking else "</think>") + prefix)
    return "".join(parts)


class FusedChat(import_module("010_lfm25_npu_chat").NPUChat):
    reasoning_format = "lfm3"

    def __init__(self, status=report_status, tiers=TIERS):
        if os.environ.get("PHOENIX_NPU_PROGRAMS"):
            import npu_direct as nd

            nd.require_phoenix()
        else:
            from aie.iron import get_current_device

            if type(get_current_device()).__name__ != "NPU1":
                raise RuntimeError("The fused LFM3 worker requires a Phoenix/Hawk Point NPU.")
        if not (MODEL / "model.safetensors").is_file():
            raise RuntimeError("The local LFM3 checkpoint is missing.")
        self.model = X8Lfm3(status, tiers)
        self.capacity = self.model.capacity
        self.eos_id = self.model.config["eos_token_id"]
        self.tokenizer = Tokenizer.from_file(str(MODEL / "tokenizer.json"))
        self.think_token = self.tokenizer.token_to_id("<think>")
        self.consumed = []

    def encode(self, messages, thinking=False):
        return self.tokenizer.encode(render_chat(messages, thinking), add_special_tokens=False).ids

    def respond(self, messages, limit=96, on_text=None, *, thinking=False):
        self.model.blocked_token = -1 if thinking or self.think_token is None else self.think_token
        ids = self.encode(messages, thinking)
        if thinking:
            return dict(self.respond_thinking(ids, limit, on_text), capacity=self.capacity)
        raw, shown = "", ""

        def stream(delta):
            nonlocal raw, shown
            raw += delta
            text = lfm3_visible_text(raw, streaming=True)
            if text and text != shown:
                replace = not text.startswith(shown)
                on_text(text if replace else text[len(shown):], replace)
                shown = text

        reply = self.respond_ids(ids, limit, on_text=stream if on_text is not None else None)
        return dict(reply, response=lfm3_visible_text(reply["response"]), capacity=self.capacity)


BENCHMARK = "Give me three short tips for organizing my notes."
PARIS = "What is the capital of France? Answer with one word."


def verify(chat):
    long = ("The capital of France is Paris. " + "This is only background context. " * 44 +
            "Based on the first sentence, what is the capital of France? Answer with one word.")
    results = []
    for prompt, limit in ((PARIS, 12), (BENCHMARK, 32), (long, 12)):
        reply = chat.respond([{"role": "user", "content": prompt}], limit)
        emit("reply", **reply)
        results.append(reply)
    if any("paris" not in results[i]["response"].casefold() for i in (0, 2)):
        raise RuntimeError("The fused LFM3 failed the short- or long-context answer check.")
    speed = results[1]["decode_tokens_per_s"] or 0
    if results[1]["generated_tokens"] != 32 or speed <= 1.8:
        raise RuntimeError("The fused LFM3 did not beat the existing worker's decode benchmark.")
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
        # Keep the XRT context alive through os._exit.
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
