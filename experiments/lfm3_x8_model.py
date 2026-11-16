"""Fused BF16 LFM3-1.7B decode for Phoenix. One XRT submission per token."""

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
from lfm25_x8 import (CORES, CTL_T, KDIR, OBJ, OBJ_T, TAB_T, _fifos, _linear, _place,
                      silu_table)
import npu_direct as nd
from phoenix_paths import MODELS

MODEL = MODELS / "lfm3-1.7b"
HIDDEN, FFN, VOCAB = 2048, 7680, 128000
HEAD = 128
SLICE, FFN_ROWS, VOCAB_ROWS = HIDDEN // CORES, FFN // CORES, VOCAB // CORES
CONV, FULL, SLIDING, OUTPUT = range(4)
KINDS = (CONV, CONV, FULL, CONV, SLIDING, CONV, FULL, CONV, SLIDING, CONV,
         FULL, CONV, SLIDING, CONV, FULL, CONV, SLIDING, CONV, FULL, CONV,
         SLIDING, CONV, FULL, CONV)
KIND_NAMES = {CONV: "conv", FULL: "full_attention", SLIDING: "sliding_attention"}
MAX_CAPACITY = 1024
TIERS = (64, 256, MAX_CAPACITY)
STATE_LEN, TAP_LEN = 3 * OBJ, 4 * OBJ
KV_WORDS = 2 * HEAD
KV_STRIDE = MAX_CAPACITY * KV_WORDS + OBJ
KV_LEN = (len(KINDS) - KINDS.count(CONV)) * CORES * KV_STRIDE
APPEND_LEN = KV_LEN - KV_STRIDE + OBJ
TABLE_LEN = HIDDEN * VOCAB
KERNEL = KDIR.parent / "x8_lfm3_core.cc"
MEM_T = np.ndarray[(HIDDEN + FFN + HIDDEN + 2 * OBJ,), np.dtype[bfloat16]]
ACC_T = np.ndarray[(2 * (16 + HEAD),), np.dtype[np.float32]]


def slot(stage, core):
    return OBJ + (stage * CORES + core) * 2 * OBJ


STATE_BASE = slot(8, 0)
ROW_BASE = STATE_BASE + KINDS.count(CONV) * CORES * STATE_LEN
IO_LEN = ROW_BASE + HIDDEN


def conv_state(index, core):
    return STATE_BASE + (index * CORES + core) * STATE_LEN


def segments(kind):
    rows = 3 * SLICE if kind == CONV else 4 * HEAD + 2
    return (HIDDEN + OBJ + rows * HIDDEN + (TAP_LEN if kind == CONV else 0),
            SLICE * HIDDEN, (2 + 2 * FFN_ROWS) * HIDDEN,
            SLICE * FFN, HIDDEN)


def weight_layout():
    # Windows XRT handles these as two BOs, each under 2 GB. The first
    # includes the tied embedding/head table, so it holds fewer layers.
    lengths = [TABLE_LEN + CORES * (HIDDEN + OBJ), 0]
    layers = []
    for i, kind in enumerate(KINDS):
        bank = int(i >= 10)
        layers.append((bank, lengths[bank]))
        lengths[bank] += CORES * sum(segments(kind))
    return layers, tuple(lengths)


LAYERS, W_LEN = weight_layout()


def _core(inp, out, mem, acc, table, ctl, dummy, run):
    for _ in range_(6):
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
    ctl[4] = core
    ctl[5] = int(core == 0)
    return ctl


def _gather(stage, count):
    return TensorAccessPattern((IO_LEN,), slot(stage, 0), [1, 1, CORES, count],
                               [0, 0, 2 * OBJ, 1])


def _bf16_array(length):
    return np.ndarray[(length,), np.dtype[bfloat16]]


def token_design(capacity, with_head):
    kernel_version = hashlib.sha256(KERNEL.read_bytes() + (KDIR / "x8_math.h").read_bytes()).hexdigest()[:12]
    tag = f"lfm3x8_{capacity}_{int(with_head)}_{kernel_version}"

    @iron.jit(tag=tag)
    def token(w0: In, w1: In, io: Out, kv: In, append: Out, *, tag: CompileTime[str]):
        run = ExternalFunction("x8_lfm3_do", source_file=str(KERNEL),
            arg_types=[OBJ_T, MEM_T, ACC_T, TAB_T, CTL_T, np.int32, np.int32],
            include_dirs=[aie_config.cxx_header_path(), str(KDIR)], compile_flags=["-Oz"])
        ins, outs = _fifos("l3")
        table = silu_table()
        workers = [Worker(_core, [ins[k].cons(), outs[k].prod(), Buffer(MEM_T),
            Buffer(ACC_T), Buffer(TAB_T, initial_value=table),
            Buffer(CTL_T, initial_value=_control(k)), Buffer(OBJ_T), run],
            tile=Tile(*_place(k)), dynamic_objfifo_lowering=True, stack_size=2048)
            for k in range(CORES)]

        def layer(index, weights, io, kv, append, prods, conss):
            kind = KINDS[index]
            bank, base = LAYERS[index]
            parts = segments(kind)
            core_len = sum(parts)
            conv_index = KINDS[:index].count(CONV)
            att_index = index - conv_index
            g = TaskGroup()
            for k in range(CORES):
                source = _linear(IO_LEN, ROW_BASE, HIDDEN) if index == 0 else _gather(5, SLICE)
                prods[k].fill(io, tap=source, group=g)
                prods[k].fill(io, tap=_linear(IO_LEN, 0, OBJ), group=g)
                prods[k].fill(weights[bank], tap=_linear(W_LEN[bank], base + k * core_len, parts[0]),
                              group=g)
                conss[k].drain(io, tap=_linear(IO_LEN, slot(1, k), OBJ), group=g, wait=True)
                if kind == CONV:
                    state = _linear(IO_LEN, conv_state(conv_index, k), STATE_LEN)
                    prods[k].fill(io, tap=state, group=g)
                    conss[k].drain(io, tap=state, group=g, wait=True)
                else:
                    region = (att_index * CORES + k) * KV_STRIDE
                    prods[k].fill(kv, tap=_linear(KV_LEN, region, capacity * KV_WORDS), group=g)
                    conss[k].drain(append, tap=_linear(APPEND_LEN, region, OBJ), group=g, wait=True)
            g.finish()
            part_offset = parts[0]
            for stage in range(2, 6):
                g = TaskGroup()
                count = FFN_ROWS if stage == 4 else SLICE
                size = parts[stage - 1]
                for k in range(CORES):
                    prods[k].fill(io, tap=_gather(stage - 1, count), group=g)
                    prods[k].fill(weights[bank],
                        tap=_linear(W_LEN[bank], base + k * core_len + part_offset, size),
                        group=g)
                    conss[k].drain(io, tap=_linear(IO_LEN, slot(stage, k),
                                  2 * OBJ if stage == 3 else OBJ), group=g, wait=True)
                g.finish()
                part_offset += size

        def head(w, io, prods, conss):
            g = TaskGroup()
            for k in range(CORES):
                prods[k].fill(io, tap=_gather(5, SLICE), group=g)
                prods[k].fill(io, tap=_linear(IO_LEN, 0, OBJ), group=g)
                prods[k].fill(w, tap=_linear(W_LEN[0], TABLE_LEN + k * (HIDDEN + OBJ),
                                             HIDDEN + OBJ), group=g)
                prods[k].fill(w, tap=_linear(W_LEN[0], k * VOCAB_ROWS * HIDDEN,
                                             VOCAB_ROWS * HIDDEN), group=g)
                conss[k].drain(io, tap=_linear(IO_LEN, slot(6, k), OBJ), group=g, wait=True)
            g.finish()
            g = TaskGroup()
            prods[0].fill(io, tap=_gather(6, 64), group=g)
            conss[0].drain(io, tap=_linear(IO_LEN, slot(7, 0), OBJ), group=g, wait=True)
            g.finish()

        def sequence(w0, w1, io, kv, append, prods, conss):
            for index in range(len(KINDS)):
                layer(index, (w0, w1), io, kv, append, prods, conss)
            if with_head:
                head(w0, io, prods, conss)

        rt = Runtime(sequence, [_bf16_array(W_LEN[0]), _bf16_array(W_LEN[1]),
            _bf16_array(IO_LEN), _bf16_array(KV_LEN), _bf16_array(APPEND_LEN),
            [ins[k].prod(tile=Tile(_place(k)[0], 0)) for k in range(CORES)],
            [outs[k].cons(tile=Tile(_place(k)[0], 0)) for k in range(CORES)]])
        return Program(iron.get_current_device(), rt, workers=workers).resolve_program()

    return token


def _pack_layer(index, ck, dest):
    prefix = f"model.layers.{index}."

    def weight(name):
        return ck.map(prefix + name + ".weight")

    kind = KINDS[index]
    gamma = weight("operator_norm")
    if kind == CONV:
        conv = weight("conv.in_proj").reshape(3, CORES, SLICE, HIDDEN)
        taps = weight("conv.conv")[:, 0, :].reshape(CORES, SLICE, 7)
        output = weight("conv.out_proj")
    else:
        q, k, v, gate, output = (weight(f"self_attn.{op}_proj")
                                  for op in ("q", "k", "v", "gate", "o"))
        q_gamma, k_gamma = weight("self_attn.q_norm"), weight("self_attn.k_norm")
    post, pre, post_ffn = (weight(name) for name in ("post_operator_norm", "ffn_norm", "post_ffn_norm"))
    gate_ffn, up, down = (weight(f"feed_forward.{op}_proj") for op in ("gate", "up", "down"))
    for core, target in enumerate(dest.reshape(CORES, -1)):
        hidden = slice(core * SLICE, (core + 1) * SLICE)
        ffn = slice(core * FFN_ROWS, (core + 1) * FFN_ROWS)
        header = np.zeros(OBJ, dtype=bfloat16)
        header[500:502] = np.asarray([kind], dtype=np.int32).view(bfloat16)
        if kind == CONV:
            padded_taps = np.zeros(TAP_LEN, dtype=bfloat16)
            padded_taps[:7 * SLICE] = taps[core].T.reshape(-1)
            projection = (conv[:, core], padded_taps)
        else:
            header[:2 * HEAD] = np.concatenate((q_gamma, k_gamma))
            kv_head = slice((core // 2) * HEAD, (core // 2 + 1) * HEAD)
            projection = (q[hidden], k[kv_head], v[kv_head], gate[2 * core:2 * core + 2])
        pair = np.stack((gate_ffn[ffn], up[ffn]), axis=1)
        values = (gamma, header, *projection, output[hidden], post, pre, pair,
                  down[hidden], post_ffn)
        offset = 0
        for data in values:
            target[offset:offset + data.size] = data.reshape(-1)
            offset += data.size
        if offset != target.size:
            raise ValueError(f"Layer {index} packed to {offset}, expected {target.size} elements.")


class X8Lfm3:
    def __init__(self, log, tiers=TIERS):
        self.tiers = tuple(tiers)
        self.capacity = self.tiers[-1]
        self.blocked_token = -1
        self.config = json.loads((MODEL / "config.json").read_text(encoding="utf-8"))
        if tuple(self.config["layer_types"]) != tuple(KIND_NAMES[k] for k in KINDS):
            raise ValueError("Checkpoint has an incompatible LFM3 layer layout.")
        start = perf_counter()
        paths = {}
        for tier in self.tiers:
            for head in (True, False):
                log(f"Preparing fused LFM3 NPU program ({tier} context, "
                    f"{'decode' if head else 'prefill'})")
                paths[tier, head] = nd.program_variant(tier, head, lambda: token_design(tier, head))
        self.program = nd.Program(paths[self.tiers[0], True][0])
        self.entries = {key: self.program.entry(insts) for key, (_, insts) in paths.items()}
        log(f"Fused LFM3 programs ready in {perf_counter() - start:.1f} s")
        self.weights = tuple(nd.Buffer(length) for length in W_LEN)
        self.io, self.kv = nd.Buffer(IO_LEN), nd.Buffer(KV_LEN)
        self.table = self.weights[0].array[:TABLE_LEN].reshape(VOCAB, HIDDEN)
        log("Packing LFM3 weights into NPU memory")
        ck = BF16Checkpoint(MODEL / "model.safetensors")
        self.table[:] = ck.map("model.embed_tokens.weight")
        gamma = ck.load("model.embedding_norm.weight")
        for core in range(CORES):
            offset = TABLE_LEN + core * (HIDDEN + OBJ)
            block = self.weights[0].array[offset:offset + HIDDEN + OBJ]
            block[:HIDDEN] = gamma
            block[HIDDEN:] = 0
            block[HIDDEN + 500:HIDDEN + 506] = np.array(
                [OUTPUT, 0, core * VOCAB_ROWS], dtype=np.int32).view(bfloat16)
        for index, (bank, start_at) in enumerate(LAYERS):
            end = start_at + CORES * sum(segments(KINDS[index]))
            _pack_layer(index, ck, self.weights[bank].array[start_at:end])
            if (index + 1) % 6 == 0:
                log(f"Packed LFM3 layers {index + 1}/{len(KINDS)}")
        for buffer in self.weights:
            buffer.to_device()
        self.phases = self._phases()
        self.reset()
        log(f"Fused LFM3 ready in {perf_counter() - start:.1f} s")

    def _phases(self):
        pairs = np.arange(0, HEAD, 2, dtype=np.float32) / HEAD
        positions = np.arange(self.capacity, dtype=np.float32)[:, None]
        phases = []
        for kind in ("full_attention", "sliding_attention"):
            rope = self.config["rope_parameters"][kind]
            freq = np.float32(rope["rope_theta"]) ** (-pairs)
            freq[int(HEAD / 2 * rope.get("partial_rotary_factor", 1.0)):] = 0
            phase = positions * freq
            phases.extend((np.cos(phase), np.sin(phase)))
        return np.concatenate(phases, axis=1).astype(bfloat16)

    def reset(self):
        self.position = 0
        self.io.write(np.zeros(ROW_BASE - STATE_BASE, dtype=bfloat16), STATE_BASE)

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
            raise ValueError(f"LFM3's {self.capacity}-token context is full.")
        tier = next(t for t in self.tiers if t > pos)
        self.io.array[:256] = self.phases[pos]
        self.io.array[256:262] = np.asarray([pos, tier // 2, self.blocked_token + 1], dtype=np.int32).view(bfloat16)
        self.io.to_device(0, 262)
        self.io.write(self.table[token], ROW_BASE)
        append = self.kv.view(pos * KV_WORDS, APPEND_LEN)
        self.entries[tier, select](*self.weights, self.io, self.kv, append)
        self.position += 1
        if select:
            return int(self.io.from_device(slot(7, 0), 2).view(np.int32)[0])
