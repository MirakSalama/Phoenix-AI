"""Unpack the E2B text GGUF into the BF16 layout expected by AMD IRON."""

import argparse
import json
import math
from pathlib import Path
import re
import struct

import gguf
import numpy as np
from ml_dtypes import bfloat16


PREFIX = "model.language_model."
SHARED_KV = re.compile(r"layers\.(\d+)\.self_attn\.(?:k_norm|k_proj|v_proj)\.weight$")


def tensor_map(reader, original):
    arch = reader.fields["general.architecture"].contents()
    if arch != "gemma4":
        raise ValueError(f"Expected a Gemma 4 GGUF, found {arch!r}.")
    source = {tensor.name: tensor for tensor in reader.tensors}
    mapping = gguf.get_tensor_name_map(gguf.MODEL_ARCH.GEMMA4, 35)
    for name, spec in original.items():
        if not name.startswith(PREFIX):
            continue
        stripped = "model." + name[len(PREFIX):]
        if stripped.endswith(".layer_scalar"):
            stripped += ".weight"
        gguf_name = mapping.get_name(stripped, try_suffixes=(".weight",))
        wanted = tuple(spec["shape"])
        if gguf_name not in source:
            shared = SHARED_KV.fullmatch(name[len(PREFIX):])
            if shared and 15 <= int(shared[1]) < 35:
                # GGUF omits these unused weights. IRON's loader binds them
                # before replacing the shared layers' K/V with their owner's.
                yield name, wanted, None
                continue
            raise ValueError(f"Missing {name} ({gguf_name}) in the text GGUF.")
        tensor = source[gguf_name]
        shape = tuple(int(value) for value in tensor.shape[::-1])
        if shape != wanted or spec["dtype"] != "BF16":
            raise ValueError(f"{name} has shape {shape}, expected BF16 {wanted}.")
        yield name, shape, tensor


def unpack(gguf_file, original_layout, output):
    reader = gguf.GGUFReader(gguf_file)
    layout = json.loads(original_layout.read_text(encoding="utf-8"))
    entries = list(tensor_map(reader, layout))
    if not entries:
        raise ValueError("The original Gemma text layout has no weights.")
    offset = 0
    header = {}
    for name, shape, _ in entries:
        end = offset + math.prod(shape) * 2
        header[name] = {"dtype": "BF16", "shape": shape, "data_offsets": [offset, end]}
        offset = end
    metadata = json.dumps(header, separators=(",", ":")).encode("utf-8")
    metadata += b" " * (-len(metadata) % 8)
    pending = output.with_suffix(output.suffix + ".unpacking")
    with pending.open("wb") as file:
        file.write(struct.pack("<Q", len(metadata)))
        file.write(metadata)
        for index, (name, shape, tensor) in enumerate(entries, 1):
            start = file.tell()
            if tensor is None:
                file.write(bytes(math.prod(shape) * 2))
            else:
                batch = max(1, 8_388_608 // shape[-1])
                data = tensor.data.reshape(-1, tensor.data.shape[-1])
                for first in range(0, len(data), batch):
                    values = gguf.dequantize(data[first:first + batch], tensor.tensor_type)
                    file.write(np.asarray(values, dtype=bfloat16).tobytes())
            if file.tell() - start != math.prod(shape) * 2:
                raise ValueError(f"{name} unpacked to the wrong byte size.")
            if index % 50 == 0 or index == len(entries):
                print(f"Unpacked {index}/{len(entries)} text tensors", flush=True)
    pending.replace(output)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("gguf", type=Path)
    parser.add_argument("original_layout", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    unpack(args.gguf, args.original_layout, args.output)
