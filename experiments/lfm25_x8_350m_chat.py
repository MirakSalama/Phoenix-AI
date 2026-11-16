"""Serve the fused eight-core LFM2.5-350M decoder on Phoenix."""

import argparse
from contextlib import redirect_stdout
from importlib import import_module
import json
import os
import sys
import traceback

from gemma4_npu_chat import ROOT, emit, report_status, serve
from phoenix_paths import MODELS


MODEL_ID = "lfm25-350m"
CHECKPOINT = MODELS / MODEL_ID


class FusedChat:
    def __init__(self, status=report_status):
        os.environ["NPU_CHAT_X8_MODEL"] = MODEL_ID
        os.environ["NPU_CACHE_HOME"] = str(ROOT / "cache" / "lfm25-350m-x8-programs")
        if os.environ.get("PHOENIX_NPU_PROGRAMS"):
            import npu_direct as nd

            nd.require_phoenix()
        else:
            from aie.iron import get_current_device

            if type(get_current_device()).__name__ != "NPU1":
                raise RuntimeError("The fused LFM2.5 350M worker requires a Phoenix/Hawk Point NPU.")
        if not (CHECKPOINT / "model.safetensors").is_file():
            raise RuntimeError("The LFM2.5 350M checkpoint is missing.")
        status("Preparing the fused 350M NPU program")
        self.chat = import_module("010_lfm25_npu_chat").NPUChat(log=status)
        self.capacity = self.chat.model.capacity

    def respond(self, messages, limit=96, on_text=None):
        stream = None if on_text is None else lambda text: on_text(text, False)
        reply = self.chat.respond(messages, limit, on_text=stream)
        return dict(reply, capacity=self.capacity)


def verify(chat):
    cases = [
        ("What is the capital of France? Answer with one word.", 16),
        ("Give me three short tips for organizing my notes.", 48),
        ("The capital of France is Paris. " + "This is only background context. " * 65 +
         "Based on the first sentence, what is the capital of France? Answer with one word.", 16),
    ]
    results = []
    for prompt, limit in cases:
        reply = chat.respond([{"role": "user", "content": prompt}], limit)
        emit("reply", **reply)
        results.append(reply)
    if any("paris" not in results[i]["response"].casefold() for i in (0, 2)):
        raise RuntimeError("The fused 350M decoder failed the short- or long-context answer check.")
    speed = results[1]["decode_tokens_per_s"] or 0
    if results[1]["generated_tokens"] < 16 or speed < 10:
        raise RuntimeError("The fused 350M decoder failed the sustained generation check.")
    marker = CHECKPOINT / "verified-x8-npu1.json"
    pending = marker.with_suffix(".tmp")
    pending.write_text(json.dumps({"device": results[1]["device"], "capacity": chat.capacity,
                                   "decode_tokens_per_s": speed}), encoding="utf-8")
    pending.replace(marker)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--verify", action="store_true")
    args = parser.parse_args()
    # Keep the pyxrt objects alive through os._exit, as the original x8
    # worker does. Destroying the last hardware context here can crash.
    chat = None

    def start_chat():
        global chat
        chat = FusedChat()
        return chat

    try:
        with redirect_stdout(sys.stderr):
            if args.verify:
                verify(start_chat())
            else:
                serve(start_chat)
    except Exception as error:
        traceback.print_exc()
        emit("fatal", text=str(error))
        os._exit(1)
    os._exit(0)
