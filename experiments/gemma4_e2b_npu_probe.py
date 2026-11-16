"""Measure a Gemma 4 E2B text MLP projection on Phoenix.

This runs a 128-token prefill-shaped matrix multiply with synthetic activations.
Pass the extracted BF16 gate projection tensor to use the checkpoint's weights.
"""

import argparse
from importlib import import_module
from pathlib import Path

import numpy as np
from ml_dtypes import bfloat16

import npu_direct as nd


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--weights", type=Path, help="Raw little-endian BF16 [6144, 1536] gate projection")
    args = parser.parse_args()
    weights = None
    if args.weights:
        weights = np.fromfile(args.weights, dtype=bfloat16).reshape(6144, 1536).T.copy()
    gemm = import_module("011_iron_gemm_probe")
    result = gemm.run(128, 1536, 6144, 16, 64, 64, "bf16", "f32", repeats=12, weights=weights)
    nd.finish(0 if result["max_err"] < 0.01 else 1)
