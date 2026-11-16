// One query head and one slice of each GEMV per Phoenix tile.
// Attention owners project K/V cooperatively and publish the new position.
// Shared layers read that owner's current position and history.
#include "x8_math.h"

namespace {
constexpr int HIDDEN = 1536, HOBJ = 3, SLICE = 192, PLE = 256, PS = 32;
constexpr int FMAX = 12288, RMAX = FMAX / 8, WINDOW = 512;
constexpr int RES = 0, ACT = HIDDEN, WORK = ACT + FMAX;
constexpr int AUX = WORK + 2 * RMAX, HDR = AUX + OBJ;
constexpr int KV_MAX = 2 * OBJ, BLOCK = 4;
constexpr int E0 = 112, E1 = 134, HALF = (E1 - E0) * 128;
constexpr float EPS = 1.0e-6f;
enum Control { N_IN, N_OUT, SEG, CORE, KIND, OWNER, WIDTH, PAST, TIER, BASE, FIRST };
enum Kind { SLIDING, FULL, VOCAB };

[[gnu::noinline]] void rms(const bfloat16 *x, const bfloat16 *gamma, bfloat16 *y, int n,
                           float epsilon) {
  facc a0 = aie::zeros<accfloat, 16>(), a1 = aie::zeros<accfloat, 16>();
  for (int i = 0; i < n; i += 32) {
    const auto v0 = aie::load_v<16>(x + i), v1 = aie::load_v<16>(x + i + 16);
    a0 = aie::mac(a0, v0, v0);
    a1 = aie::mac(a1, v1, v1);
  }
  const float inverse = n == HIDDEN ? 1.0f / HIDDEN : n == PLE ? 1.0f / PLE : 1.0f / OBJ;
  bfloat16 p0, p1, p2;
  split3(invsqrt(aie::reduce_add(aie::add(a0.to_vector<float>(), a1.to_vector<float>())) *
                 inverse + epsilon), p0, p1, p2);
  for (int i = 0; i < n; i += 16) {
    const auto v = aie::load_v<16>(x + i);
    auto scaled = aie::mul(v, p0);
    scaled = aie::mac(scaled, v, p1);
    scaled = aie::mac(scaled, v, p2);
    auto rounded = to_bf16(scaled);
    if (gamma)
      rounded = to_bf16(aie::mul(rounded, aie::load_v<16>(gamma + i)));
    aie::store_v(y + i, rounded);
  }
}

[[gnu::noinline]] void multiply(bfloat16 *x, int n, bfloat16 scale) {
  for (int i = 0; i < n; i += 16)
    aie::store_v(x + i, to_bf16(aie::mul(aie::load_v<16>(x + i), scale)));
}

[[gnu::noinline]] void rotate(bfloat16 *x, const bfloat16 *phase, int width) {
  const int count = width == OBJ ? 64 : 128;
  for (int i = 0; i < count; i += 16) {
    const auto a = aie::load_v<16>(x + i), b = aie::load_v<16>(x + i + width / 2);
    const auto c = aie::load_v<16>(phase + i), s = aie::load_v<16>(phase + 128 + i);
    const auto ac = to_bf16(aie::mul(a, c)), bs = to_bf16(aie::mul(b, s));
    const auto bc = to_bf16(aie::mul(b, c)), as = to_bf16(aie::mul(a, s));
    aie::store_v(x + i, to_bf16(aie::msc(aie::mul(ac, ones()), bs, ones())));
    aie::store_v(x + width / 2 + i, to_bf16(aie::mac(aie::mul(bc, ones()), as, ones())));
  }
}

// One compact GEMV inner loop for both FIFO widths fits the tile's 16 KB
// program memory. This decoder spends most of its time streaming weights.
[[gnu::noinline]] float dot(const bfloat16 *w, const bfloat16 *x, int n) {
  facc a0 = aie::zeros<accfloat, 16>(), a1 = aie::zeros<accfloat, 16>();
#pragma clang loop unroll(disable)
  for (int i = 0; i < n; i += 32) {
    a0 = aie::mac(a0, aie::load_v<16>(w + i), aie::load_v<16>(x + i));
    a1 = aie::mac(a1, aie::load_v<16>(w + i + 16), aie::load_v<16>(x + i + 16));
  }
  return aie::reduce_add(aie::add(a0.to_vector<float>(), a1.to_vector<float>()));
}

// One head's online softmax. The work block stores four padded KV positions.
[[gnu::noinline]] void attend(const bfloat16 *kv, int count, int width, const bfloat16 *q,
                              float *state) {
  alignas(64) float exponents[16] = {};
  float best = state[0];
  for (int j = 0; j < count; ++j) {
    const auto *key = kv + j * KV_MAX;
    const float score = dot(q, key, width);
    exponents[j] = score;
    if (score > best)
      best = score;
  }
  for (int j = 0; j < count; ++j)
    exponents[j] -= best;
  exponents[BLOCK] = state[0] - best;
  state[0] = best;
  const fvec prob = exp16(aie::load_v<16>(exponents));
  facc p;
  p.from_vector(prob);
  const bvec hi = to_bf16(p);
  p.from_vector(aie::sub(prob, aie::mul(hi, ones()).to_vector<float>()));
  const bvec lo = to_bf16(p);
  const float scale = prob[BLOCK];
  state[1] *= scale;
  for (int j = 0; j < count; ++j)
    state[1] += prob[j];
  for (int i = 0; i < width; i += 16) {
    facc sum;
    const auto old = aie::load_v<16>(state + 16 + i);
    sum.from_vector(scale == 1.0f ? old : vmul(old, scale));
    for (int j = 0; j < count; ++j) {
      const auto v = aie::load_v<16>(kv + j * KV_MAX + width + i);
      sum = aie::mac(sum, v, hi[j]);
      sum = aie::mac(sum, v, lo[j]);
    }
    aie::store_v(state + 16 + i, sum.to_vector<float>());
  }
}

[[gnu::noinline]] void gelu(bfloat16 *x, int n, const bfloat16 *table) {
  const auto *lut = reinterpret_cast<const uint16_t *>(table);
  auto *bits = reinterpret_cast<uint16_t *>(x);
  for (int i = 0; i < n; ++i) {
    const uint16_t b = bits[i];
    const int e = (b >> 7) & 255;
    if (e < E0)
      bits[i] = e > 1 ? static_cast<uint16_t>(b - 0x80) : b;
    else if (e >= E1)
      bits[i] = b & 0x8000 ? 0x8000 : b;
    else
      bits[i] = lut[(b & 0x8000 ? HALF : 0) + ((e - E0) << 7) + (b & 127)];
  }
}

[[gnu::noinline]] void elementwise_gate(bfloat16 *a, const bfloat16 *b, int n) {
  for (int i = 0; i < n; i += 16)
    aie::store_v(a + i, to_bf16(aie::mul(aie::load_v<16>(a + i), aie::load_v<16>(b + i))));
}

[[gnu::noinline]] void row_part(const bfloat16 *o, const bfloat16 *x, float *sum,
                               bfloat16 *out, int col, int cols) {
  const float value = dot(o, x + col * OBJ, OBJ);
  if (col == 0)
    *sum = value;
  else
    *sum += value;
  if (col == cols - 1)
    *out = static_cast<bfloat16>(*sum);
}

[[gnu::noinline]] void head_score(const bfloat16 *o, const bfloat16 *x, float *s, int i) {
  // Soft-capping is monotonic. Compare the raw BF16 logits for greedy decode.
  bfloat16 rounded;
  row_part(o, x, s + 2, &rounded, i % HOBJ, HOBJ);
  if (i % HOBJ == HOBJ - 1 && static_cast<float>(rounded) > s[0]) {
    s[0] = static_cast<float>(rounded);
    s[1] = static_cast<float>(i / HOBJ);
  }
}

[[gnu::noinline]] void pick(const bfloat16 *o, bfloat16 *out) {
  float best = -3.4028235e38f;
  int32_t selected = 0;
  for (int i = 0; i < 8; ++i) {
    float score;
    __builtin_memcpy(&score, o + 64 * i, sizeof(score));
    if (score > best) {
      best = score;
      selected = read_i32(o + 64 * i + 2);
    }
  }
  __builtin_memcpy(out, &selected, sizeof(selected));
}

[[gnu::noinline]] void read_attention(const bfloat16 *o, bfloat16 *m, float *s,
                                      const int32_t *ctl, int i, int head) {
  bfloat16 *act = m + ACT, *q = m + WORK, *cur = q + OBJ;
  const int current_objects = 2 * head / OBJ;
  if (i == 0) {
    rms(q, o, q, head, EPS);
    rotate(q, m + AUX, head);
    return;
  }
  if (i == 1) {
    if (ctl[OWNER])
      copy(o, act + 8192, OBJ);
    return;
  }
  const int offset = i - 2;
  if (offset < current_objects) {
    copy(o, cur + offset * OBJ, OBJ);
    if (ctl[OWNER] && offset == current_objects - 1) {
      rms(cur, act + 8192, cur, head, EPS);
      rms(cur + head, nullptr, cur + head, head, EPS);
      rotate(cur, m + AUX, head);
    }
    return;
  }
  const int object = offset - current_objects;
  const int position = object / 2;
  const int oldest = head == OBJ ? 0 : ctl[PAST] - (WINDOW - 1);
  if (position >= oldest && position < ctl[PAST] && (head == OBJ || object % 2 == 0))
    copy(o, act + (object % (2 * BLOCK)) * OBJ, OBJ);
  if (object % (2 * BLOCK) == 2 * BLOCK - 1) {
    const int first = position - (BLOCK - 1);
    const int skip = first < oldest ? oldest - first : 0;
    const int remaining = ctl[PAST] - first;
    const int count = (remaining < BLOCK ? remaining : BLOCK) - skip;
    if (count > 0)
      attend(act + skip * KV_MAX, count, head, q, s);
  }
}

[[gnu::noinline]] void read_ple_inputs(const bfloat16 *o, bfloat16 *m, float *s, int i) {
  bfloat16 *act = m + ACT, *work = m + WORK;
  if (i < HOBJ)
    copy(o, act + i * OBJ, OBJ);
  else if (i < 2 * HOBJ) {
    copy(o, work + (i - HOBJ) * OBJ, OBJ);
    if (i == 2 * HOBJ - 1) {
      rms(act, work, act, HIDDEN, EPS);
      add(m + RES, act, m + RES, HIDDEN);
    }
  } else if (i < 3 * HOBJ) {
    copy(o, act + (i - 2 * HOBJ) * OBJ, OBJ);
    if (i == 3 * HOBJ - 1)
      multiply(act, HIDDEN, static_cast<bfloat16>(39.25f));
  } else {
    const int part = i - 3 * HOBJ;
    const auto *source = part < PS * HOBJ ? act : m + RES;
    row_part(o, source, s, work + part / HOBJ, part % HOBJ, HOBJ);
  }
}

[[gnu::noinline]] void read_ple_output(const bfloat16 *o, bfloat16 *m,
                                      const bfloat16 *lut, int i) {
  bfloat16 *act = m + ACT, *work = m + WORK;
  if (i == 0) {
    copy(o, act, OBJ);
  } else if (i == 1) {
    rms(act, o, act, PLE, EPS * HIDDEN);
    for (int j = 0; j < PLE; j += 16) {
      const auto token = to_bf16(aie::mul(aie::load_v<16>(o + PLE + j),
                                         static_cast<bfloat16>(16.0f)));
      aie::store_v(work + j, token);
    }
    add(act, work, act, PLE);
    multiply(act, PLE, static_cast<bfloat16>(0.70710678118f));
    gelu(act + PLE, PLE, lut);
    elementwise_gate(act, act + PLE, PLE);
  } else {
    const int row = 2 * (i - 2);
    work[row] = static_cast<bfloat16>(dot(o, act, PLE));
    work[row + 1] = static_cast<bfloat16>(dot(o + PLE, act, PLE));
  }
}

[[gnu::noinline]] void consume(const bfloat16 *o, bfloat16 *m, float *s,
                               const bfloat16 *lut, int32_t *ctl, int i) {
  bfloat16 *act = m + ACT, *work = m + WORK;
  const int kind = ctl[KIND], head = kind == FULL ? 512 : 256;
  const int rows = ctl[WIDTH] / 8;
  switch (ctl[SEG]) {
  case 0:
    if (i < HOBJ)
      copy(o, m + RES + i * OBJ, OBJ);
    else if (i == HOBJ)
      copy(o, m + AUX, OBJ);
    else if (i < 2 * HOBJ + 1)
      copy(o, work + (i - HOBJ - 1) * OBJ, OBJ);
    else
      copy(o, m + HDR, OBJ);
    return;
  case 1:
    if (kind == VOCAB)
      head_score(o, act, s, i);
    else
      row_part(o, act, s, work + i / HOBJ, i % HOBJ, HOBJ);
    return;
  case 2:
    if (kind == VOCAB)
      pick(o, work);
    else
      read_attention(o, m, s, ctl, i, head);
    return;
  case 3: {
    const int cols = 8 * head / OBJ;
    if (i < cols)
      copy(o, act + i * OBJ, OBJ);
    else {
      const int part = i - cols;
      const int row = cols == 8 ? part / 8 : part / 4;
      row_part(o, act, s, work + row, part - row * cols, cols);
    }
    return;
  }
  case 4:
    if (i < HOBJ)
      copy(o, act + i * OBJ, OBJ);
    else if (i < 3 * HOBJ) {
      copy(o, work + (i % HOBJ) * OBJ, OBJ);
      if (i == 2 * HOBJ - 1) {
        rms(act, work, act, HIDDEN, EPS);
        add(m + RES, act, m + RES, HIDDEN);
      } else if (i == 3 * HOBJ - 1) {
        rms(m + RES, work, act, HIDDEN, EPS);
      }
    } else {
      const int part = i - 3 * HOBJ, row = part / HOBJ;
      row_part(o, act, s, work + row / 2 + (row % 2 ? rows : 0), part % HOBJ, HOBJ);
    }
    return;
  case 5: {
    const int cols = ctl[WIDTH] / OBJ;
    if (i < cols)
      copy(o, act + i * OBJ, OBJ);
    else {
      const int part = i - cols;
      // The two compile-time divisors avoid a scalar division in the weight loop.
      const int row = cols == 24 ? part / 24 : part / 12;
      const int col = part - row * cols;
      row_part(o, act, s, work + row, col, cols);
    }
    return;
  }
  case 6:
    read_ple_inputs(o, m, s, i);
    return;
  case 7:
    read_ple_output(o, m, lut, i);
    return;
  default:
    copy(o, i < HOBJ ? act + i * OBJ : work + (i - HOBJ) * OBJ, OBJ);
  }
}

[[gnu::noinline]] void produce(bfloat16 *o, bfloat16 *m, float *s,
                               const bfloat16 *lut, int32_t *ctl, int j) {
  bfloat16 *act = m + ACT, *work = m + WORK;
  const int head = ctl[KIND] == FULL ? 512 : 256;
  const int rows = ctl[WIDTH] / 8;
  for (int i = 0; i < OBJ; i += 16)
    aie::store_v(o + i, aie::zeros<bfloat16, 16>());
  switch (ctl[SEG]) {
  case 1:
    if (ctl[KIND] == VOCAB) {
      const int32_t row = ctl[BASE] + static_cast<int32_t>(s[1]);
      __builtin_memcpy(o, s, sizeof(float));
      __builtin_memcpy(o + 2, &row, sizeof(row));
    } else {
      copy(work + head, o, head / 4);  // adjacent K and V slices
    }
    return;
  case 2:
    if (ctl[KIND] == VOCAB) {
      copy(work, o, 16);
    } else if (j == 0) {
      attend(work + OBJ, 1, head, work, s);
      const float inv = reciprocal(s[1]);
      for (int i = 0; i < head; i += 16) {
        facc value;
        value.from_vector(vmul(aie::load_v<16>(s + 16 + i), inv));
        aie::store_v(o + i, to_bf16(value));
      }
    } else {
      copy(work + j * OBJ, o, OBJ);
    }
    return;
  case 3:
  case 5:
  case 7:
    copy(work, o, SLICE);
    return;
  case 4:
    if (j == 0) {
      gelu(work, rows, lut);
      elementwise_gate(work, work + rows, rows);
    }
    copy(work + j * OBJ, o, rows == 768 && j == 1 ? 256 : OBJ);
    return;
  case 6:
    copy(work, o, 2 * PS);
    return;
  default: {
    const int offset = ctl[CORE] * SLICE;
    rms(act, work, act, HIDDEN, EPS);
    add(m + RES + offset, act + offset, o, SLICE);
    multiply(o, SLICE, m[HDR]);
  }
  }
}

[[gnu::noinline]] void advance(bfloat16 *m, float *s, int32_t *ctl) {
  if (ctl[SEG] == 0) {
    ctl[KIND] = read_i32(m + HDR + 500);
    ctl[OWNER] = read_i32(m + HDR + 502);
    ctl[WIDTH] = read_i32(m + HDR + 504);
    ctl[BASE] = read_i32(m + HDR + 506);
    ctl[FIRST] = read_i32(m + HDR + 508);
    ctl[PAST] = read_i32(m + AUX + 256);
    ctl[TIER] = read_i32(m + AUX + 258);
    if (ctl[FIRST])
      multiply(m + RES, HIDDEN, static_cast<bfloat16>(39.25f));
    rms(m + RES, m + WORK, m + ACT, HIDDEN, EPS);
    s[0] = -3.4028235e38f;
    s[1] = 0.0f;
  }
  const int head = ctl[KIND] == FULL ? 512 : 256;
  const int rows = ctl[WIDTH] / 8, cols = ctl[WIDTH] / OBJ;
  const int owner = ctl[OWNER], core = ctl[CORE];
  const int seg = ctl[SEG] == 8 ? 0 : ctl[SEG] + 1;
  const int in[9] = {2 * HOBJ + 2, (head + owner * head / 4) * HOBJ,
                     2 + 2 * head / OBJ + 2 * ctl[TIER],
                     (8 * head / OBJ) * (SLICE + 1), HOBJ * (3 + 2 * rows),
                     cols * (SLICE + 1), HOBJ * (3 + 2 * PS), 2 + SLICE / 2, 2 * HOBJ};
  const int out[9] = {0, owner, 1 + ((owner && core == 0) ? 2 * head / OBJ : 0), 1,
                      (rows + OBJ - 1) / OBJ, 1, 1, 1, 1};
  ctl[SEG] = seg;
  ctl[N_IN] = in[seg];
  ctl[N_OUT] = out[seg];
  if (ctl[KIND] == VOCAB && seg != 0) {
    ctl[N_IN] = seg == 1 ? 32768 * HOBJ : seg == 2 && core == 0;
    ctl[N_OUT] = seg == 1 || (seg == 2 && core == 0);
  } else if (seg == 2) {
    s[0] = -1.0e30f;
    s[1] = 0.0f;
    zero_f(s + 16, head);
  }
}

}  // namespace

extern "C" void x8_gemma4_do(bfloat16 *obj, bfloat16 *mem, float *state,
                             const bfloat16 *lut, int32_t *ctl, int32_t op, int32_t i) {
  const auto saved = aie::swap_rounding(aie::rounding_mode::conv_even);
  if (op == 0)
    consume(obj, mem, state, lut, ctl, i);
  else if (op == 1)
    produce(obj, mem, state, lut, ctl, i);
  else
    advance(mem, state, ctl);
  aie::set_rounding(saved);
}
