"""Serve Liquid's larger text models through XDNA 1 only."""

import argparse
from contextlib import redirect_stdout
import functools
import json
import os
import sys
import traceback

from gemma4_npu_chat import IronChat, ROOT, configure, emit, prepare, report_status, serve, trace
from phoenix_paths import MODELS as MODEL_ROOT


MODELS = {"lfm25-350m": "LFM2.5 350M", "lfm3-1.7b": "LFM3 1.7B"}


def lfm3_visible_text(text, streaming=False):
    _, closed, tail = text.rpartition("</think>")
    answer = (tail.lstrip() if closed else text).split("<think>", 1)[0]
    start = answer.rfind("<")
    if streaming and start >= 0 and any(tag.startswith(answer[start:]) for tag in ("<think>", "</think>")):
        return answer[:start]
    return answer


class LiquidChat(IronChat):
    def __init__(self, model_id, status=report_status):
        self.name = MODELS[model_id]
        self.hide_reasoning = model_id == "lfm3-1.7b"
        self.checkpoint = MODEL_ROOT / model_id
        if not (self.checkpoint / "model.safetensors").is_file():
            raise RuntimeError(f"The {self.name} checkpoint is missing from {self.checkpoint}.")
        configure(f"{model_id}-npu-programs")
        from iron.common.image.packaging import require_device
        from lfm_iron_model import Liquid

        device = require_device()
        self.device = str(device)
        if not self.device.startswith("NPU1("):
            raise RuntimeError(f"Expected XDNA 1 (NPU1), found {self.device}.")
        trace(f"{self.name}: {self.device}, context {self.capacity}")
        status(f"Step 1: Opening the {self.name} checkpoint")
        self.model = Liquid.from_checkpoint(self.checkpoint, max_seq_len=self.capacity)
        if self.hide_reasoning:
            self.configure_thinking("lfm3")
            # The small chat context and voice response budget are for the
            # answer itself. Use Liquid's supported non-thinking chat mode.
            tokenizer = self.model.tokenizer
            tokenizer.chat_template = "{%- set enable_thinking = false -%}" + tokenizer.chat_template
            trace("LFM3 chat template: non-thinking mode")
        prepare(self.model, device, status)

    def visible_text(self, text, streaming=False):
        return lfm3_visible_text(text, streaming) if self.hide_reasoning else text


def verify(chat):
    prompt = [{"role": "user", "content": "What is the capital of France? Answer with one word."}]
    reply = chat.respond(prompt, limit=32)
    emit("reply", **reply)
    if "paris" not in reply["response"].casefold():
        raise RuntimeError(f"{chat.name} did not pass the NPU1 text generation check.")
    marker = chat.checkpoint / "verified-npu1.json"
    pending = marker.with_suffix(".tmp")
    pending.write_text(json.dumps(reply), encoding="utf-8")
    pending.replace(marker)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("model", choices=MODELS)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--verify", action="store_true")
    mode.add_argument("--prompt")
    args = parser.parse_args()
    factory = functools.partial(LiquidChat, args.model)
    try:
        with redirect_stdout(sys.stderr):
            if args.verify:
                verify(factory())
            elif args.prompt is not None:
                emit("reply", **factory().respond([{"role": "user", "content": args.prompt}]))
            else:
                serve(factory)
    except Exception as error:
        traceback.print_exc()
        emit("fatal", text=str(error))
        os._exit(1)
    os._exit(0)
