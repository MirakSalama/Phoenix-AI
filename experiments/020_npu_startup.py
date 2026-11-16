"""Compare the packaged NPU engine with the source engine using identical programs."""

import argparse
from importlib import import_module
import json
import os
from pathlib import Path
from statistics import median
import subprocess
import sys
from time import perf_counter


EXPERIMENTS = Path(__file__).resolve().parent
PORTABLE = EXPERIMENTS.parent / "dist" / "Phoenix-AI-Portable-win-x64"
APP = PORTABLE / "App"
PYTHON = APP / "runtime/python/python.exe"
MODELS = ("lfm25-230m", "lfm25-350m", "lfm3-1.7b")
MESSAGES = [{"role": "user", "content":
             "List 12 practical tips for organizing a home office. Give one sentence per tip."}]


def measure(model, implementation):
    sys.path.insert(0, str(EXPERIMENTS if implementation == "source" else APP / "experiments"))
    os.environ["PHOENIX_MODEL_DIR"] = str(PORTABLE / "Models")
    programs = (APP / "cache/phoenix-portable-programs" if model == MODELS[0]
                else PORTABLE / "Models" / model / "npu-programs")
    os.environ["PHOENIX_NPU_PROGRAMS"] = str(programs)
    os.environ["NPU_CHAT_X8_MODEL"] = MODELS[1] if model == MODELS[1] else MODELS[0]
    import npu_direct
    npu_direct.require_phoenix()
    start = perf_counter()
    if model == MODELS[2]:
        chat = import_module("lfm3_x8_chat").FusedChat(lambda *_: None)
    else:
        chat = import_module("010_lfm25_npu_chat").NPUChat(log=lambda *_: None)
    elapsed = perf_counter() - start
    reply = chat.respond(MESSAGES, 64)
    print(json.dumps({"startup_s": elapsed, "decode_tps": reply["decode_tokens_per_s"],
                      "ids": reply["generated_ids"]}))
    npu_direct.finish()


def compare(output):
    xrt = Path(os.environ["SystemRoot"]) / "System32/AMD/xrt-smi.exe"
    report = subprocess.check_output([str(xrt), "examine", "--report", "aie-partitions"], text=True)
    if "No hardware contexts running" not in report:
        raise RuntimeError("Wait for the active NPU workload to finish.")
    results = {}
    for model in MODELS:
        rows = {"packaged": [], "source": []}
        for version in ("packaged", "source", "source", "packaged"):
            run = subprocess.run([str(PYTHON), "-B", __file__, "--worker", model, version],
                                 capture_output=True, text=True, encoding="utf-8", timeout=90)
            if run.returncode:
                raise RuntimeError(run.stderr or run.stdout)
            rows[version].append(json.loads(run.stdout))
        samples = rows["packaged"] + rows["source"]
        if not samples[0]["ids"] or any(row["ids"] != samples[0]["ids"] for row in samples):
            raise RuntimeError(f"{model} output changed after weight packing.")
        results[model] = rows
        for version, values in rows.items():
            print(f"{model} {version}: {median(v['startup_s'] for v in values):.2f}s startup, "
                  f"{median(v['decode_tps'] for v in values):.1f} tok/s", flush=True)
    output.write_text(json.dumps(results, indent=2), encoding="utf-8")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--worker", nargs=2)
    parser.add_argument("--output", type=Path, default=EXPERIMENTS / "020_npu_startup_results.json")
    args = parser.parse_args()
    if args.worker:
        measure(*args.worker)
    else:
        compare(args.output)
