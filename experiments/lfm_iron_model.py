"""LFM2.5 and LFM3 text decoders built from IRON's NPU operators."""

import dataclasses

import numpy as np
from ml_dtypes import bfloat16
from aie.iron.kernels import activation

import iron
import iron.functional as F
from iron.common import UnaryElementwise
from iron.lm import (
    Attention, CausalConv1d, CausalLM, DecoderConfig, GatedMLP, KVCache,
    Linear, RMSNorm, Rope, rope_angles,
)


@dataclasses.dataclass(frozen=True)
class LiquidConfig(DecoderConfig):
    precision: iron.Precision = dataclasses.field(default=iron.Precision.HIGHEST,
        kw_only=True, metadata={"json": None})
    eps: float = dataclasses.field(default=1e-5, kw_only=True, metadata={"json": "norm_eps"})
    layer_types: tuple[str, ...] = dataclasses.field(kw_only=True)
    conv_L_cache: int = dataclasses.field(kw_only=True)
    conv_bias: bool = dataclasses.field(default=False, kw_only=True)
    model_type: str = dataclasses.field(kw_only=True)
    use_sandwich_norm: bool = dataclasses.field(default=False, kw_only=True)
    sliding_theta: float | None = dataclasses.field(default=None, kw_only=True)
    rotary_fraction: float = dataclasses.field(default=1.0, kw_only=True, metadata={"json": None})
    prefill_chunk: int = dataclasses.field(default=64, metadata={"json": None})

    @classmethod
    def from_json(cls, data, source="config.json"):
        data = dict(data)
        rope = data["rope_parameters"]
        if data["model_type"] == "lfm3":
            data["rope_theta"] = rope["full_attention"]["rope_theta"]
            data["sliding_theta"] = rope["sliding_attention"]["rope_theta"]
        else:
            data["rope_theta"] = rope["rope_theta"]
            data["tie_word_embeddings"] = data.get("tie_embedding", True)
            if data.get("block_auto_adjust_ff_dim"):
                width = int(2 * data["block_ff_dim"] / 3 * data["block_ffn_dim_multiplier"])
                multiple = data["block_multiple_of"]
                data["intermediate_size"] = (width + multiple - 1) // multiple * multiple
        data["layer_types"] = tuple(data["layer_types"])
        config = super().from_json(data, source)
        if config.model_type == "lfm3":
            fraction = rope["full_attention"].get("partial_rotary_factor", 1.0)
            config = dataclasses.replace(config, rotary_fraction=fraction)
        return config

    def angles(self):
        if self.rotary_fraction == 1.0:
            return super().angles()
        # LFM3 rotates the first pairs across the full head's two halves.
        # Its unrotated pairs have zero angle but still occupy the full table.
        pairs = int(self.rotary_fraction * self.head_dim) // 2
        return rope_angles(self.head_dim, self.max_seq_len, self.rope_theta,
                           lambda frequencies: np.where(np.arange(frequencies.size) < pairs, frequencies, 0))


class ShortConv(iron.Graph):
    def __init__(self, c):
        self.width = c.hidden_size
        self.in_proj = Linear(self.width, 3 * self.width, c.conv_bias, precision=c.precision)
        self.conv = CausalConv1d(c, self.width, c.conv_L_cache, bias=c.conv_bias)
        self.out_proj = Linear(self.width, self.width, c.conv_bias, precision=c.precision)

    def body(self, h, call):
        width = self.width
        b, c, x = self.in_proj.split(h, width, width, width)
        return self.out_proj(c * self.conv(b * x, call))


class Sigmoid(UnaryElementwise):
    def kernel(self):
        return activation.sigmoid(self.tile_size, use_lut=True)


class LiquidAttention(Attention):
    def __init__(self, c, i):
        super().__init__(c, i, norms="qk")
        self.gated = c.model_type == "lfm3"
        if c.layer_types[i] == "sliding_attention":
            self.rotary = None
        if self.gated:
            self.gate_proj = Linear(c.hidden_size, self.heads, False, precision=c.precision)

    def load_state_dict(self, tree, *, path=""):
        super().load_state_dict(tree, path=path)
        if self.gated:
            # The GEMV and activation kernels use aligned vectors. The unused
            # gate lanes are zeroed and excluded by the expansion projection.
            self.padded_gate = iron.param(np.pad(self.gate_proj.weight,
                ((0, self.head_dim - self.heads), (0, 0))))

    def output(self, o, x):
        if self.gated:
            gate = F.linear(x, self.padded_gate, precision=self.gate_proj.precision)
            expanded = F.linear(Sigmoid.step(gate), self.gate_expansion,
                                precision=self.gate_proj.precision)
            o = o * expanded
        return self.o_proj(o)


class LiquidLayer(iron.Graph):
    def __init__(self, c, i):
        self.operator_norm = RMSNorm(c.hidden_size, c.eps)
        self.ffn_norm = RMSNorm(c.hidden_size, c.eps)
        self.feed_forward = GatedMLP(c)
        self.rope = None
        self.kind = c.layer_types[i]
        if self.kind == "conv":
            self.conv = ShortConv(c)
        else:
            self.self_attn = LiquidAttention(c, i)
            self.cache = KVCache(c, c.window if self.kind == "sliding_attention" else None)
        if c.use_sandwich_norm:
            self.post_operator_norm = RMSNorm(c.hidden_size, c.eps)
            self.post_ffn_norm = RMSNorm(c.hidden_size, c.eps)
        else:
            self.post_operator_norm = self.post_ffn_norm = None

    def body(self, x, call):
        h = self.operator_norm(x)
        if self.kind == "conv":
            h = self.conv(h, call)
        else:
            h = self.self_attn(h, self.cache, call, call.ropes[self.rope])
        x = x + (h if self.post_operator_norm is None else self.post_operator_norm(h))
        h = self.feed_forward(self.ffn_norm(x))
        return x + (h if self.post_ffn_norm is None else self.post_ffn_norm(h))


class Liquid(CausalLM):
    config = LiquidConfig
    layer_class = LiquidLayer
    renames = {"model.embedding_norm.weight": "model.norm.weight"}

    def __init__(self, config, weights):
        super().__init__(config, weights)
        for layer in self.layers:
            if layer.kind == "conv":
                layer.rope = None
        if config.model_type == "lfm3":
            sliding = Rope(dataclasses.replace(config, rope_theta=config.sliding_theta, rotary_fraction=1.0))
            heads, width = config.num_attention_heads, config.head_dim
            expansion = np.zeros((heads * width, width), dtype=bfloat16)
            expansion[np.arange(heads * width), np.repeat(np.arange(heads), width)] = 1
            expansion = iron.param(expansion)
            for layer in self.layers:
                if layer.kind != "conv":
                    layer.self_attn.gate_expansion = expansion
                if layer.kind == "sliding_attention":
                    layer.rope = sliding

    def load_state_dict(self, tree, *, path=""):
        # LFM2 calls its MLP matrices w1 (gate), w2 (down), and w3 (up).
        if self.config.model_type == "lfm2":
            from iron.lm.checkpoint import renamed
            tree = renamed(tree, {"feed_forward.w1": "feed_forward.gate_proj",
                                  "feed_forward.w2": "feed_forward.down_proj",
                                  "feed_forward.w3": "feed_forward.up_proj",
                                  "self_attn.out_proj": "self_attn.o_proj",
                                  "self_attn.q_layernorm": "self_attn.q_norm",
                                  "self_attn.k_layernorm": "self_attn.k_norm"})
        super().load_state_dict(tree, path=path)
