"""Interactive LFM2.5-230M chat with all model arithmetic on the Phoenix NPU.

Uses the eight-core engine (lfm25_x8_model): prompt tokens run four at a time
per weight pass, and each reply token is one NPU submission for all 14 layers
and the vocabulary head. Greedy decoding, EOS stop, and NPU state
reuse across turns while the transcript keeps the same token prefix. Context
is limited to 4096 positions (the largest compiled KV tier).
"""

import argparse
import json
import sys
import time
from pathlib import Path

from tokenizers import Tokenizer

import npu_direct as nd
from lfm25_x8_model import MODEL, TIERS, X8Model
from local_reasoning import ReasoningReply
from local_context import assistant_prefix, generation_stop, response_budget

EOS_ID = 7


def render_chat(messages):
    messages, prefix = assistant_prefix(messages)
    parts = ["<|startoftext|>"]
    for message in messages:
        parts.append(f"<|im_start|>{message['role']}\n{message['content']}<|im_end|>\n")
    parts.append("<|im_start|>assistant\n" + prefix)
    return "".join(parts)


class NPUChat:
    eos_id = EOS_ID

    def __init__(self, log=print, tiers=TIERS):
        self.model = X8Model(tiers=tiers, log=log)
        self.tokenizer = Tokenizer.from_file(str(MODEL / "tokenizer.json"))
        self.consumed = []

    def encode(self, messages):
        return self.tokenizer.encode(render_chat(messages), add_special_tokens=False).ids

    def is_eos(self, token_id):
        return token_id == self.eos_id

    def respond(self, messages, max_new_tokens, on_text=None, *, cancelled=None):
        return self.respond_ids(self.encode(messages), max_new_tokens, on_text, cancelled=cancelled)

    def respond_thinking(self, ids, limit, on_text):
        reasoning = ReasoningReply(self.reasoning_format, self.tokenizer.token_to_id,
                                   self.tokenizer.decode, on_text)
        reply = self.respond_ids(ids, limit, on_token=reasoning.add)
        return reply | reasoning.result()

    def respond_ids(self, ids, max_new_tokens, on_text=None, *, on_token=None, cancelled=None):
        max_new_tokens = response_budget(self.model.capacity, len(ids), max_new_tokens)
        reuse = 0 < len(self.consumed) < len(ids) and ids[:len(self.consumed)] == self.consumed
        if not reuse:
            self.model.reset()
            self.consumed = []
        start = time.perf_counter()
        pending = ids[len(self.consumed):]
        try:
            next_id = self.model.prefill(pending, cancelled=cancelled)
        except InterruptedError:
            self.consumed = []
            raise
        self.consumed = list(ids)
        first_token = time.perf_counter() - start
        generated, shown = [], ""
        decode_start = time.perf_counter()
        decode_steps = 0
        while not self.is_eos(next_id) and len(generated) < max_new_tokens:
            if cancelled is not None and cancelled():
                raise InterruptedError("Generation interrupted")
            generated.append(next_id)
            if on_token is not None:
                on_token(next_id)
            if on_text is not None:
                text = self.tokenizer.decode(generated)
                if len(text) > len(shown) and not text.endswith("�"):
                    on_text(text[len(shown):])
                    shown = text
            if len(generated) == max_new_tokens:
                break
            self.consumed.append(next_id)
            next_id = self.model.step(next_id)
            decode_steps += 1
        decode = time.perf_counter() - decode_start
        return {
            "response": self.tokenizer.decode(generated),
            "generated_ids": generated,
            "prompt_tokens": len(ids),
            "prompt_tokens_processed": len(pending),
            "cache_read_tokens": len(ids) - len(pending),
            "cache_write_tokens": len(pending),
            "reused_npu_state": reuse,
            "generated_tokens": len(generated),
            "time_to_first_token_s": round(first_token, 3),
            "decode_tokens_per_s": round(decode_steps / decode, 1) if decode_steps else None,
            "stopped_on_eos": self.is_eos(next_id),
            "generation_stop": generation_stop(self.model.capacity, len(ids), max_new_tokens,
                                               len(generated), self.is_eos(next_id)),
            "device": "Phoenix NPU1",
        }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--prompt", help="Run one prompt and exit")
    parser.add_argument("--max-new-tokens", type=int, default=256)
    parser.add_argument("--json", action="store_true", help="With --prompt: print JSON")
    args = parser.parse_args()
    chat = NPUChat(log=lambda m: print(m, file=sys.stderr, flush=True))
    if args.prompt is not None:
        stream = None if args.json else (lambda t: print(t, end="", flush=True))
        result = chat.respond([{"role": "user", "content": args.prompt}],
                              args.max_new_tokens, on_text=stream)
        print(json.dumps(result, indent=1) if args.json else "", flush=True)
        nd.finish()
    model_name = "LFM2.5-350M" if MODEL.name == "lfm25-350m" else "LFM2.5-230M"
    print(f"{model_name} on the Phoenix NPU. Type /quit to exit, /reset to clear.", flush=True)
    history = []
    while True:
        try:
            prompt = input("You: ").strip()
        except (EOFError, KeyboardInterrupt):
            break
        if prompt.lower() in ("/quit", "/exit"):
            break
        if prompt.lower() == "/reset":
            history = []
            continue
        if not prompt:
            continue
        history.append({"role": "user", "content": prompt})
        print("NPU: ", end="", flush=True)
        try:
            result = chat.respond(history, args.max_new_tokens,
                                  on_text=lambda t: print(t, end="", flush=True))
        except ValueError as exc:
            print(exc)
            history = []
            continue
        print(f"\n({result['prompt_tokens_processed']} prompt tokens in "
              f"{result['time_to_first_token_s']} s, {result['generated_tokens']} tokens at "
              f"{result['decode_tokens_per_s']} tok/s, state "
              f"{'reused' if result['reused_npu_state'] else 'rebuilt'})", flush=True)
        history.append({"role": "assistant", "content": result["response"]})
    nd.finish()


if __name__ == "__main__":
    main()
