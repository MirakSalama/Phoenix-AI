"""Convert Liquid's LFM3 text GGUF to BF16 tensors for the IRON worker."""

import argparse
import json
import math
from pathlib import Path
import struct

import gguf
import numpy as np
from ml_dtypes import bfloat16


ROOT_NAMES = {"token_embd": "model.embed_tokens", "output_norm": "model.embedding_norm"}
LAYER_NAMES = {
    "attn_norm": "operator_norm", "ffn_norm": "ffn_norm",
    "post_attention_norm": "post_operator_norm", "post_ffw_norm": "post_ffn_norm",
    "shortconv.conv": "conv.conv", "shortconv.in_proj": "conv.in_proj",
    "shortconv.out_proj": "conv.out_proj", "attn_q": "self_attn.q_proj",
    "attn_k": "self_attn.k_proj", "attn_v": "self_attn.v_proj",
    "attn_output": "self_attn.o_proj", "attn_gate": "self_attn.gate_proj",
    "attn_q_norm": "self_attn.q_norm", "attn_k_norm": "self_attn.k_norm",
    "ffn_gate": "feed_forward.gate_proj", "ffn_up": "feed_forward.up_proj",
    "ffn_down": "feed_forward.down_proj",
}


def layout(reader):
    if reader.fields["general.architecture"].contents() != "lfm3":
        raise ValueError("Expected a text LFM3 GGUF checkpoint.")
    for tensor in reader.tensors:
        if tensor.name == "rope_freqs.weight":
            continue  # IRON builds the equivalent zero-tail table from config.json.
        key, suffix = tensor.name.rsplit(".", 1)
        if key.startswith("blk."):
            _, layer, op = key.split(".", 2)
            name = f"model.layers.{layer}.{LAYER_NAMES[op]}.{suffix}"
        else:
            name = f"{ROOT_NAMES[key]}.{suffix}"
        shape = tuple(int(dim) for dim in tensor.shape[::-1])
        if key.endswith(".shortconv.conv"):
            shape = (shape[0], 1, shape[1])
        yield name, shape, tensor


def unpack(source, output):
    entries = list(layout(gguf.GGUFReader(source)))
    header = {}
    offset = 0
    for name, shape, _ in entries:
        size = 2 * math.prod(shape)
        header[name] = {"dtype": "BF16", "shape": shape, "data_offsets": [offset, offset + size]}
        offset += size
    encoded = json.dumps(header, separators=(",", ":")).encode()
    encoded += b" " * (-len(encoded) % 8)
    temporary = output.with_suffix(".safetensors.unpacking")
    with temporary.open("wb") as file:
        file.write(struct.pack("<Q", len(encoded)))
        file.write(encoded)
        for index, (name, shape, tensor) in enumerate(entries, 1):
            begin = file.tell()
            rows = tensor.data.reshape(-1, tensor.data.shape[-1])
            batch = max(1, 8_388_608 // shape[-1])
            for start in range(0, len(rows), batch):
                values = gguf.dequantize(rows[start:start + batch], tensor.tensor_type)
                file.write(np.asarray(values, dtype=bfloat16).tobytes())
            if file.tell() - begin != 2 * math.prod(shape):
                raise ValueError(f"{name} unpacked to the wrong size.")
            if index % 50 == 0 or index == len(entries):
                print(f"Unpacked {index}/{len(entries)} tensors", flush=True)
    temporary.replace(output)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    unpack(args.source, args.output)
