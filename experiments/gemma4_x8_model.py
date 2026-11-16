"""Direct Phoenix XRT programs for the Gemma 4 E2B text decoder."""

import hashlib
import json
import os
from time import perf_counter

if not os.environ.get("PHOENIX_NPU_PROGRAMS"):
    import aie.iron as iron
    from aie.helpers.taplib import TensorAccessPattern
    from aie.iron import Buffer, CompileTime, In, Out, Program, Runtime, TaskGroup, Worker
    from aie.iron.controlflow import range_
    from aie.iron.device import Tile
    from aie.iron.kernel import ExternalFunction
    from aie.utils import config as aie_config
from ml_dtypes import bfloat16
import numpy as np

from lfm25_checkpoint import BF16Checkpoint
from lfm25_x8 import CORES, CTL_T, KDIR, OBJ, OBJ_T, TAB_T, _fifos, _linear, _place
import npu_direct as nd
from phoenix_paths import MODELS

MODEL = MODELS / "gemma4-e2b"
HIDDEN, PLE, VOCAB = 1536, 256, 262144
LAYERS, KV_OWNERS, FFN = 35, 15, 6144
SLICE, PLE_SLICE = HIDDEN // CORES, PLE // CORES
SLIDING, FULL, OUTPUT = range(3)
MAX_CAPACITY = 1024
TIERS = (64, 256, MAX_CAPACITY)
KV_WORDS = 2 * OBJ
KV_STRIDE = MAX_CAPACITY * KV_WORDS
KV_LEN = KV_OWNERS * KV_STRIDE
APPEND_LEN = KV_LEN - KV_STRIDE + KV_WORDS
TABLE_LEN = HIDDEN * VOCAB
KERNEL = KDIR.parent / "x8_gemma4_core.cc"
MEM_T = np.ndarray[(HIDDEN + 2 * FFN + 4 * FFN // CORES + 2 * OBJ,), np.dtype[bfloat16]]
ACC_T = np.ndarray[(16 + OBJ,), np.dtype[np.float32]]


def slot(stage, core):
    return 2 * OBJ + (stage * CORES + core) * 3 * OBJ


ROW_BASE = slot(11, 0)
PLE_BASE = ROW_BASE + HIDDEN
IO_LEN = PLE_BASE + LAYERS * OBJ


def kind(index):
    return FULL if index % 5 == 4 else SLIDING


def head_size(index):
    return OBJ if kind(index) == FULL else PLE


def owner(index):
    if index < KV_OWNERS:
        return index
    return 14 if kind(index) == FULL else 13


def width(index):
    return FFN if index < KV_OWNERS else 2 * FFN


def parts(index):
    head, ffn = head_size(index), width(index)
    qkv = head + (2 * head // CORES if owner(index) == index else 0)
    # Each segment includes exactly the packed weights consumed at that stage.
    return (HIDDEN + OBJ + qkv * HIDDEN, 2 * OBJ, SLICE * CORES * head,
            (2 + 2 * ffn // CORES) * HIDDEN, SLICE * ffn,
            (1 + 2 * PLE_SLICE) * HIDDEN, SLICE * PLE, HIDDEN)


def weight_layout():
    # Keep each XRT BO below 2 GB and the kernel at six buffer arguments.
    lengths = [TABLE_LEN + CORES * (HIDDEN + OBJ), 0, 0]
    offsets = []
    for i in range(LAYERS):
        bank = 0 if i < 8 else 1 if i < 23 else 2
        offsets.append((bank, lengths[bank]))
        lengths[bank] += CORES * sum(parts(i))
    if max(lengths) * 2 >= 2_000_000_000:
        raise ValueError(f"Unexpected Gemma weight bank sizes {lengths}.")
    return offsets, tuple(lengths)


WEIGHTS, W_LEN = weight_layout()


def _core(inp, out, mem, acc, table, ctl, dummy, run):
    for _ in range_(9):
        for i in range_(ctl[0]):
            o = inp.acquire(1)
            run(o, mem, acc, table, ctl, 0, i)
            inp.release(1)
        for i in range_(ctl[1]):
            o = out.acquire(1)
            run(o, mem, acc, table, ctl, 1, i)
            out.release(1)
        run(dummy, mem, acc, table, ctl, 2, 0)


def _control(core):
    ctl = np.zeros(16, dtype=np.int32)
    ctl[0] = 2 * (HIDDEN // OBJ) + 2
    ctl[3] = core
    return ctl


def _gelu_table():
    exps = np.arange(112, 134, dtype=np.uint16)
    mant = np.arange(128, dtype=np.uint16)
    bits = (exps[:, None] << 7 | mant[None, :]).reshape(-1)
    x = np.concatenate((bits, bits | np.uint16(0x8000))).view(bfloat16).astype(np.float32)
    return (0.5 * x * (1 + np.tanh(np.sqrt(2 / np.pi) * (x + 0.044715 * x**3)))).astype(bfloat16)


def _gather(stage, count, lane=0):
    return TensorAccessPattern((IO_LEN,), slot(stage, 0) + lane, [1, 1, CORES, count],
                               [0, 0, 3 * OBJ, 1])


def _bf16_array(length):
    return np.ndarray[(length,), np.dtype[bfloat16]]


def token_design(capacity, with_head):
    kernel_version = hashlib.sha256(KERNEL.read_bytes() + (KDIR / "x8_math.h").read_bytes()).hexdigest()[:12]
    tag = f"gemma4x8_{capacity}_{int(with_head)}_{kernel_version}"

    @iron.jit(tag=tag)
    def token(w0: In, w1: In, w2: In, io: Out, kv: In, append: Out, *, tag: CompileTime[str]):
        run = ExternalFunction("x8_gemma4_do", source_file=str(KERNEL),
            arg_types=[OBJ_T, MEM_T, ACC_T, TAB_T, CTL_T, np.int32, np.int32],
            include_dirs=[aie_config.cxx_header_path(), str(KDIR)], compile_flags=["-Oz"])
        ins, outs = _fifos("g4")
        table = _gelu_table()
        workers = [Worker(_core, [ins[k].cons(), outs[k].prod(), Buffer(MEM_T),
            Buffer(ACC_T), Buffer(TAB_T, initial_value=table),
            Buffer(CTL_T, initial_value=_control(k)), Buffer(OBJ_T), run],
            tile=Tile(*_place(k)), dynamic_objfifo_lowering=True, stack_size=2048)
            for k in range(CORES)]

        def layer(index, weights, io, kv, append, prods, conss):
            bank, base = WEIGHTS[index]
            sizes = parts(index)
            core_len = sum(sizes)
            offsets = np.cumsum((0, *sizes[:-1])).tolist()
            head, ffn = head_size(index), width(index)
            cache_owner = owner(index)
            writes_cache = cache_owner == index
            region = cache_owner * KV_STRIDE

            def fill_weights(k, stage, group, start=0, length=None):
                offset = base + k * core_len + offsets[stage - 1] + start
                length = sizes[stage - 1] if length is None else length
                prods[k].fill(weights[bank], tap=_linear(W_LEN[bank], offset, length), group=group)

            g = TaskGroup()
            for k in range(CORES):
                source = _linear(IO_LEN, ROW_BASE, HIDDEN) if index == 0 else _gather(8, SLICE)
                prods[k].fill(io, tap=source, group=g)
                prods[k].fill(io, tap=_linear(IO_LEN, kind(index) * OBJ, OBJ), group=g)
                fill_weights(k, 1, g)
                if writes_cache:
                    conss[k].drain(io, tap=_linear(IO_LEN, slot(1, k), OBJ), group=g, wait=True)
            if writes_cache:
                g.finish()
                g = TaskGroup()
            for k in range(CORES):
                fill_weights(k, 2, g)
                if writes_cache:
                    for lane in (0, head // CORES):
                        prods[k].fill(io, tap=_gather(1, head // CORES, lane), group=g)
                else:
                    prods[k].fill(append, tap=_linear(APPEND_LEN, region, 2 * head), group=g)
                prods[k].fill(kv, tap=_linear(KV_LEN, region, capacity * KV_WORDS), group=g)
                conss[k].drain(io, tap=_linear(IO_LEN, slot(2, k), OBJ), group=g, wait=True)
                if writes_cache and k == 0:
                    conss[k].drain(append, tap=_linear(APPEND_LEN, region, 2 * head),
                                   group=g, wait=True)
            g.finish()

            for stage in range(3, 9):
                g = TaskGroup()
                for k in range(CORES):
                    if stage == 7:
                        for lane in (0, PLE_SLICE):
                            prods[k].fill(io, tap=_gather(6, PLE_SLICE, lane), group=g)
                        prods[k].fill(io, tap=_linear(IO_LEN, PLE_BASE + index * OBJ, OBJ), group=g)
                    else:
                        count = head if stage == 3 else ffn // CORES if stage == 5 else SLICE
                        prods[k].fill(io, tap=_gather(stage - 1, count), group=g)
                    if stage == 6:
                        fill_weights(k, stage, g, length=HIDDEN)
                        prods[k].fill(io, tap=_linear(IO_LEN, ROW_BASE, HIDDEN), group=g)
                        fill_weights(k, stage, g, start=HIDDEN, length=sizes[stage - 1] - HIDDEN)
                    else:
                        fill_weights(k, stage, g)
                    output = ((ffn // CORES + OBJ - 1) // OBJ) * OBJ if stage == 4 else OBJ
                    conss[k].drain(io, tap=_linear(IO_LEN, slot(stage, k), output), group=g, wait=True)
                g.finish()

        def head(w, io, prods, conss):
            g = TaskGroup()
            rows = VOCAB // CORES
            for k in range(CORES):
                prods[k].fill(io, tap=_gather(8, SLICE), group=g)
                prods[k].fill(io, tap=_linear(IO_LEN, 0, OBJ), group=g)
                prods[k].fill(w, tap=_linear(W_LEN[0], TABLE_LEN + k * (HIDDEN + OBJ),
                                             HIDDEN + OBJ), group=g)
                prods[k].fill(w, tap=_linear(W_LEN[0], k * rows * HIDDEN, rows * HIDDEN), group=g)
                conss[k].drain(io, tap=_linear(IO_LEN, slot(9, k), OBJ), group=g, wait=True)
            g.finish()
            g = TaskGroup()
            prods[0].fill(io, tap=_gather(9, 64), group=g)
            conss[0].drain(io, tap=_linear(IO_LEN, slot(10, 0), OBJ), group=g, wait=True)
            g.finish()

        def sequence(w0, w1, w2, io, kv, append, prods, conss):
            for index in range(LAYERS):
                layer(index, (w0, w1, w2), io, kv, append, prods, conss)
            if with_head:
                head(w0, io, prods, conss)

        rt = Runtime(sequence, [*(_bf16_array(n) for n in W_LEN), _bf16_array(IO_LEN),
            _bf16_array(KV_LEN), _bf16_array(APPEND_LEN),
            [ins[k].prod(tile=Tile(_place(k)[0], 0)) for k in range(CORES)],
            [outs[k].cons(tile=Tile(_place(k)[0], 0)) for k in range(CORES)]])
        return Program(iron.get_current_device(), rt, workers=workers).resolve_program()

    return token


PREFIX = "model.language_model."


def _header(layer_kind, own, ffn, vocab_base=0, first=False, scale=0):
    values = np.zeros(OBJ, dtype=bfloat16)
    values[0] = scale
    values[500:510] = np.asarray(
        [layer_kind, own, ffn, vocab_base, first], dtype=np.int32).view(bfloat16)
    return values


def _pack_layer(index, ck, dest, ple_projection):
    prefix = PREFIX + f"layers.{index}."

    def weight(name):
        return ck.map(prefix + name + ".weight")

    head, ffn = head_size(index), width(index)
    own = owner(index) == index
    scale = ck.map(prefix + "layer_scalar")[0]
    header = _header(kind(index), own, ffn, first=index == 0, scale=scale)
    input_norm = weight("input_layernorm")
    q, o = (weight(f"self_attn.{op}_proj") for op in ("q", "o"))
    qk_norm = np.zeros(2 * OBJ, dtype=bfloat16)
    qk_norm[:head] = weight("self_attn.q_norm")
    if own:
        k, v = (weight(f"self_attn.{op}_proj") for op in ("k", "v"))
        qk_norm[OBJ:OBJ + head] = weight("self_attn.k_norm")
    post, pre, post_ffn, post_ple = (weight(name) for name in (
        "post_attention_layernorm", "pre_feedforward_layernorm",
        "post_feedforward_layernorm", "post_per_layer_input_norm"))
    gate, up, down = (weight(f"mlp.{op}_proj") for op in ("gate", "up", "down"))
    ple_gate, ple_out = (weight(name) for name in ("per_layer_input_gate", "per_layer_projection"))
    for core, target in enumerate(dest.reshape(CORES, -1)):
        local_hidden = slice(core * SLICE, (core + 1) * SLICE)
        local_ple = slice(core * PLE_SLICE, (core + 1) * PLE_SLICE)
        local_q = slice(core * head, (core + 1) * head)
        local_ffn = slice(core * ffn // CORES, (core + 1) * ffn // CORES)
        qkv = (q[local_q],)
        if own:
            kv = slice(core * head // CORES, (core + 1) * head // CORES)
            qkv += (k[kv], v[kv])
        pair = np.stack((gate[local_ffn], up[local_ffn]), axis=1)
        values = (input_norm, header, *qkv, qk_norm, o[local_hidden], post, pre,
                  pair, down[local_hidden], post_ffn, ple_projection[local_ple],
                  ple_gate[local_ple], ple_out[local_hidden], post_ple)
        offset = 0
        for data in values:
            target[offset:offset + data.size] = data.reshape(-1)
            offset += data.size
        if offset != target.size:
            raise ValueError(f"Gemma layer {index} packed to {offset}, expected {target.size}.")


class X8Gemma4:
    def __init__(self, status, tiers=TIERS):
        self.tiers = tuple(tiers)
        self.capacity = self.tiers[-1]
        self.config = json.loads((MODEL / "config.json").read_text(encoding="utf-8"))
        text = self.config["text_config"]
        layer_types = tuple("full_attention" if kind(i) == FULL else "sliding_attention"
                            for i in range(LAYERS))
        if (tuple(text["layer_types"]) != layer_types or text["num_kv_shared_layers"] != 20
                or not text["use_double_wide_mlp"] or text["sliding_window"] != 512):
            raise ValueError("Checkpoint has an incompatible Gemma 4 E2B layer layout.")
        start = perf_counter()
        paths = {}
        for tier in self.tiers:
            for head in (True, False):
                status(f"Preparing Gemma NPU program ({tier} context, {'decode' if head else 'prefill'})")
                paths[tier, head] = nd.program_variant(tier, head, lambda: token_design(tier, head))
        self.program = nd.Program(paths[self.tiers[0], True][0])
        self.entries = {key: self.program.entry(insts) for key, (_, insts) in paths.items()}
        status(f"Gemma NPU programs ready in {perf_counter() - start:.1f} s")
        self.weights = tuple(nd.Buffer(length) for length in W_LEN)
        self.io, self.kv = nd.Buffer(IO_LEN), nd.Buffer(KV_LEN)
        self.table = self.weights[0].array[:TABLE_LEN].reshape(VOCAB, HIDDEN)
        self.ple_rows = self.io.array[PLE_BASE:].reshape(LAYERS, OBJ)
        ck = BF16Checkpoint(MODEL / "model.safetensors")
        self.ple_table = ck.map(PREFIX + "embed_tokens_per_layer.weight")
        self.ple_rows[:, :PLE] = ck.map(PREFIX + "per_layer_projection_norm.weight")
        self.pad = text["pad_token_id"]
        self.placeholders = {self.config.get(f"{modality}_token_id")
                             for modality in ("image", "audio", "video")}
        status("Packing Gemma weights into NPU memory", 0.0)
        self.table[:] = ck.map(PREFIX + "embed_tokens.weight")
        gamma = ck.map(PREFIX + "norm.weight")
        for core in range(CORES):
            offset = TABLE_LEN + core * (HIDDEN + OBJ)
            self.weights[0].array[offset:offset + HIDDEN] = gamma
            self.weights[0].array[offset + HIDDEN:offset + HIDDEN + OBJ] = _header(
                OUTPUT, False, FFN, core * (VOCAB // CORES))
        projection = ck.map(PREFIX + "per_layer_model_projection.weight").reshape(LAYERS, PLE, HIDDEN)
        for index, (bank, offset) in enumerate(WEIGHTS):
            end = offset + CORES * sum(parts(index))
            _pack_layer(index, ck, self.weights[bank].array[offset:end], projection[index])
            if (index + 1) % 5 == 0:
                status(f"Packed Gemma layers {index + 1}/{LAYERS}", (index + 1) / LAYERS)
        for buffer in self.weights:
            buffer.to_device()
        self.phases = self._phases(text["rope_parameters"])
        self.reset()
        status(f"Fused Gemma ready in {perf_counter() - start:.1f} s", 1.0)

    def _phases(self, rope):
        positions = np.arange(self.capacity, dtype=np.float32)[:, None]
        result = []
        for kind_name, head in (("sliding_attention", PLE), ("full_attention", OBJ)):
            cfg = rope[kind_name]
            exponents = np.arange(0, PLE, 2, dtype=np.float64) / head
            frequency = (1 / np.power(cfg["rope_theta"], exponents)).astype(np.float32)
            fraction = cfg.get("partial_rotary_factor", 1.0)
            frequency[int(head * fraction / 2):] = 0
            phase = positions * frequency
            result.append(np.concatenate((np.cos(phase), np.sin(phase)), axis=1))
        return np.stack(result, axis=1).astype(bfloat16)

    def reset(self):
        self.position = 0

    def prefill(self, ids, *, cancelled=None):
        for token in ids[:-1]:
            if cancelled is not None and cancelled():
                raise InterruptedError("Prompt interrupted")
            self.step(token, select=False)
        if cancelled is not None and cancelled():
            raise InterruptedError("Prompt interrupted")
        return self.step(ids[-1])

    def step(self, token, select=True):
        pos = self.position
        if pos >= self.capacity:
            raise ValueError(f"Gemma's {self.capacity}-token context is full.")
        tier = next(t for t in self.tiers if t > pos)
        ctrl = np.asarray([pos, tier], dtype=np.int32).view(bfloat16)
        for phase in (SLIDING, FULL):
            start = phase * OBJ
            self.io.array[start:start + PLE] = self.phases[pos, phase]
            self.io.array[start + PLE:start + PLE + 4] = ctrl
        self.io.to_device(0, 2 * OBJ)
        self.io.write(self.table[token], ROW_BASE)
        ple_token = self.pad if token in self.placeholders else token
        self.ple_rows[:, PLE:] = self.ple_table[ple_token].reshape(LAYERS, PLE)
        self.io.to_device(PLE_BASE, LAYERS * OBJ)
        append = self.kv.view(pos * KV_WORDS, APPEND_LEN)
        self.entries[tier, select](*self.weights, self.io, self.kv, append)
        self.position += 1
        if select:
            return int(self.io.from_device(slot(10, 0), 2).view(np.int32)[0])
