"""Export cached Windows XDNA 1 program variants for portable releases."""

import argparse
from importlib import import_module
import os
from pathlib import Path
from shutil import copyfile
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "experiments"))


def main(destination, model):
    os.environ.pop("PHOENIX_NPU_PROGRAMS", None)
    os.environ["NPU_CACHE_HOME"] = str(ROOT / "cache" / (
        "npu-programs" if model == "lfm25-230m" else f"{model}-x8-programs"))
    if model.startswith("lfm25"):
        os.environ["NPU_CHAT_X8_MODEL"] = model
        module = import_module("lfm25_x8_model")
        design = module.x8
    else:
        module = design = import_module({"lfm3-1.7b": "lfm3_x8_model", "gemma4-e2b": "gemma4_x8_model"}[model])
    from npu_direct import compile_design

    destination.mkdir(parents=True, exist_ok=True)
    for tier in module.TIERS:
        variants = [
            (f"decode-{tier}-head", design.token_design(tier, True)),
            (f"decode-{tier}-body", design.token_design(tier, False)),
        ]
        if model == "lfm25-230m":
            variants.append((f"prefill-{tier}", design.prefill_design(tier)))
        for name, variant in variants:
            xclbin, instructions = compile_design(variant)
            copyfile(xclbin, destination / f"{name}.xclbin")
            copyfile(instructions, destination / f"{name}.bin")
            print(f"Exported {name}", flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("destination", type=Path)
    parser.add_argument("--model", default="lfm25-230m", choices=("lfm25-230m", "lfm25-350m", "lfm3-1.7b", "gemma4-e2b"))
    args = parser.parse_args()
    main(args.destination, args.model)
