"""Compare NPU power modes with the installed portable chat workers."""

import argparse
from datetime import datetime
from importlib import import_module
import json
import os
from pathlib import Path
import re
from statistics import median
import subprocess
import sys


EXPERIMENTS = Path(__file__).resolve().parent
PORTABLE = EXPERIMENTS.parent / "dist" / "Phoenix-AI-Portable-win-x64"
PYTHON = PORTABLE / "App/runtime/python/python.exe"
XRT = Path(os.environ["SystemRoot"]) / "System32/AMD/xrt-smi.exe"
MODELS = ("lfm25-230m", "lfm25-350m")
MODE_ORDER = ("balanced", "performance", "performance", "balanced")
SHORT_PROMPT = "Write a numbered list of twelve practical tips for organizing a home office. Give one sentence per tip."
LONG_PROMPT = (
    "The capital of France is Paris. "
    + "This sentence is only background information for the exercise. " * 22
    + "Based on the first sentence, what is the capital of France? Answer with one word."
)


def xrt(*args):
    result = subprocess.run([str(XRT), *args], capture_output=True, text=True, timeout=15)
    if result.returncode:
        raise RuntimeError((result.stderr + result.stdout).strip())
    return result.stdout


def current_mode():
    report = xrt("examine", "--report", "platform")
    match = re.search(r"Power Mode\s*:\s*([^\r\n]+)", report)
    if not match:
        raise RuntimeError("AMD did not report the NPU power mode.")
    return re.sub(r"\W", "", match[1]).lower()


def set_mode(mode):
    xrt("configure", "--pmode", mode)
    actual = current_mode()
    if actual != mode:
        raise RuntimeError(f"Requested NPU mode {mode}, AMD reported {actual}.")


def worker(model):
    sys.path.insert(0, str(PORTABLE / "App/experiments"))
    os.environ["PHOENIX_MODEL_DIR"] = str(PORTABLE / "Models")
    programs = (PORTABLE / "App/cache/phoenix-portable-programs" if model == MODELS[0]
                else PORTABLE / "Models" / model / "npu-programs")
    if not (programs / "decode-64-head.xclbin").is_file():
        raise RuntimeError(f"Prebuilt NPU program is missing for {model}.")
    os.environ["PHOENIX_NPU_PROGRAMS"] = str(programs)
    os.environ["NPU_CHAT_X8_MODEL"] = model

    import npu_direct

    npu_direct.require_phoenix()
    chat = import_module("010_lfm25_npu_chat").NPUChat(log=lambda line: print(line, file=sys.stderr))
    chat.respond([{"role": "user", "content": SHORT_PROMPT}], 32)
    samples = []
    for kind, prompt, limit in (("decode", SHORT_PROMPT, 64), ("long-prompt", LONG_PROMPT, 8)):
        ids = chat.encode([{"role": "user", "content": prompt}])
        for _ in range(2):
            chat.consumed.clear()
            reply = chat.respond_ids(ids, limit)
            if kind == "decode" and reply["generated_tokens"] != limit:
                raise RuntimeError(f"{model} generated fewer than {limit} tokens.")
            samples.append({"kind": kind, **reply})
    print(json.dumps(samples))
    npu_direct.finish()


def compare(output):
    original = current_mode()
    if original not in {"default", "powersaver", "balanced", "performance", "turbo"}:
        raise RuntimeError(f"Unexpected original mode {original}.")
    if "No hardware contexts running" not in xrt("examine", "--report", "aie-partitions"):
        raise RuntimeError("The NPU is already in use. Wait for the other workload to stop.")

    report = {"started": datetime.now().astimezone().isoformat(), "original_mode": original, "blocks": []}
    try:
        for mode in MODE_ORDER:
            if current_mode() != mode:
                set_mode(mode)
            for model in MODELS:
                print(f"{mode}: {model}", flush=True)
                result = subprocess.run(
                    [str(PYTHON), "-B", __file__, "--worker", model],
                    capture_output=True, text=True, encoding="utf-8", timeout=75,
                )
                if result.returncode:
                    raise RuntimeError(result.stderr.strip() or f"Worker exited with code {result.returncode}.")
                if current_mode() != mode:
                    raise RuntimeError("The NPU mode changed during the test.")
                report["blocks"].append({"mode": mode, "model": model, "samples": json.loads(result.stdout)})
    finally:
        if current_mode() != original:
            set_mode(original)
        report["restored_mode"] = current_mode()
        output.write_text(json.dumps(report, indent=2), encoding="utf-8")

    for model in MODELS:
        for mode in dict.fromkeys(MODE_ORDER):
            samples = [sample for block in report["blocks"] if block["model"] == model
                       and block["mode"] == mode for sample in block["samples"]]
            decode = median(sample["decode_tokens_per_s"] for sample in samples if sample["kind"] == "decode")
            first = median(sample["time_to_first_token_s"] for sample in samples if sample["kind"] == "long-prompt")
            print(f"{model} / {mode}: {decode:.1f} tok/s, long-prompt first token {first:.3f} s")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--worker", choices=MODELS)
    parser.add_argument("--output", type=Path, default=EXPERIMENTS / "019_npu_power_modes_results.json")
    args = parser.parse_args()
    if args.worker:
        worker(args.worker)
    else:
        compare(args.output)
