// LFM3 on the same eight DMA streams as LFM2.5. Each core owns two
// query heads, one (shared with its neighbor) KV head, and 1/8 of the
// hidden and FFN channels. The two extra stages implement sandwich RMSNorm.
#include "x8_math.h"

namespace {

constexpr int HIDDEN = 2048, HEAD = 128, SLICE = 256, FFN = 7680, ROWS = 960;
constexpr int HOBJ = HIDDEN / OBJ, FOBJ = FFN / OBJ;
constexpr int HIN = 0, ACT = HIDDEN, WORK = ACT + FFN;
constexpr int AUX = WORK + HIDDEN, HDR = AUX + OBJ;
constexpr int CONV = 0, FULL = 1, SLIDING = 2, VOCAB = 3;
constexpr int STATE_OBJS = 3, TAP_OBJS = 4;
constexpr int CONV_ROWS = 3 * SLICE, ATT_ROWS = 4 * HEAD + 2;
constexpr int CACHE_WORDS = 2 * HEAD, WINDOW = 256;
constexpr int SILU_E0 = 112, SILU_E1 = 134;
constexpr int SILU_HALF = (SILU_E1 - SILU_E0) * 128;
constexpr int SOFTMAX_STRIDE = 16 + HEAD;
enum Control { N_IN, N_OUT, SEG, MODE, CORE, PICK, BASE, PAST, BLOCKS, BLOCKED_TOKEN };

// Round to BF16 between primitives, matching the existing IRON worker.
[[gnu::noinline]] void rms(const bfloat16 *x, const bfloat16 *gamma, bfloat16 *y, int n) {
  facc a0 = aie::zeros<accfloat, 16>(), a1 = aie::zeros<accfloat, 16>();
  for (int i = 0; i < n; i += 32) {
    const auto x0 = aie::load_v<16>(x + i), x1 = aie::load_v<16>(x + i + 16);
    a0 = aie::mac(a0, x0, x0);
    a1 = aie::mac(a1, x1, x1);
  }
  const float mean = aie::reduce_add(aie::add(a0.to_vector<float>(), a1.to_vector<float>())) *
                     (n == HEAD ? 1.0f / HEAD : 1.0f / HIDDEN);
  bfloat16 p0, p1, p2;
  split3(invsqrt(mean + 1.0e-5f), p0, p1, p2);
  for (int i = 0; i < n; i += 16) {
    const auto v = aie::load_v<16>(x + i);
    auto scaled = aie::mul(v, p0);
    scaled = aie::mac(scaled, v, p1);
    scaled = aie::mac(scaled, v, p2);
    aie::store_v(y + i, to_bf16(aie::mul(to_bf16(scaled), aie::load_v<16>(gamma + i))));
  }
}

[[gnu::noinline]] void rotate(bfloat16 *x, const bfloat16 *phase) {
  for (int i = 0; i < HEAD / 2; i += 16) {
    const auto a = aie::load_v<16>(x + i), b = aie::load_v<16>(x + i + HEAD / 2);
    const auto c = aie::load_v<16>(phase + i), s = aie::load_v<16>(phase + HEAD / 2 + i);
    const auto ac = to_bf16(aie::mul(a, c)), bs = to_bf16(aie::mul(b, s));
    const auto bc = to_bf16(aie::mul(b, c)), as = to_bf16(aie::mul(a, s));
    aie::store_v(x + i, to_bf16(aie::msc(aie::mul(ac, ones()), bs, ones())));
    aie::store_v(x + i + HEAD / 2, to_bf16(aie::mac(aie::mul(bc, ones()), as, ones())));
  }
}

[[gnu::noinline]] void prepare_attention(bfloat16 *m, float *state, int mode) {
  bfloat16 *qkv = m + WORK;
  const bfloat16 *phase = m + AUX + (mode == SLIDING ? HEAD : 0);
  for (int h = 0; h < 3; ++h) {
    bfloat16 *v = qkv + h * HEAD;
    rms(v, m + HDR + (h == 2 ? HEAD : 0), v, HEAD);
    rotate(v, phase);
  }
  for (int h = 0; h < 2; ++h) {
    float *s = state + h * SOFTMAX_STRIDE;
    s[0] = -1.0e30f;
    s[1] = 0.0f;
    zero_f(s + 16, HEAD);
  }
}

[[gnu::noinline]] float dot_head(const bfloat16 *a, const bfloat16 *b) {
  facc sum = aie::zeros<accfloat, 16>();
  for (int i = 0; i < HEAD; i += 16)
    sum = aie::mac(sum, aie::load_v<16>(a + i), aie::load_v<16>(b + i));
  return aie::reduce_add(sum.to_vector<float>());
}

[[gnu::noinline]] void attend(const bfloat16 *kv, int count, const bfloat16 *q, float *state) {
  alignas(64) float exponents[16] = {};
  for (int h = 0; h < 2; ++h) {
    float *s = state + h * SOFTMAX_STRIDE;
    float max_score = s[0];
    for (int j = 0; j < count; ++j) {
      const float score = dot_head(q + h * HEAD, kv + j * CACHE_WORDS) * 0.08838834765f;
      exponents[4 * h + j] = score;
      if (score > max_score)
        max_score = score;
    }
    for (int j = 0; j < count; ++j)
      exponents[4 * h + j] -= max_score;
    exponents[4 * h + 2] = s[0] - max_score;
    s[0] = max_score;
  }
  const fvec prob = exp16(aie::load_v<16>(exponents));
  facc p;
  p.from_vector(prob);
  const bvec hi = to_bf16(p);
  p.from_vector(aie::sub(prob, aie::mul(hi, ones()).to_vector<float>()));
  const bvec lo = to_bf16(p);
  for (int h = 0; h < 2; ++h) {
    float *s = state + h * SOFTMAX_STRIDE;
    const float scale = prob[4 * h + 2];
    s[1] *= scale;
    for (int j = 0; j < count; ++j)
      s[1] += prob[4 * h + j];
    for (int i = 0; i < HEAD; i += 16) {
      facc sum;
      const auto old = aie::load_v<16>(s + 16 + i);
      sum.from_vector(scale == 1.0f ? old : vmul(old, scale));
      for (int j = 0; j < count; ++j) {
        const auto v = aie::load_v<16>(kv + j * CACHE_WORDS + HEAD + i);
        sum = aie::mac(sum, v, hi[4 * h + j]);
        sum = aie::mac(sum, v, lo[4 * h + j]);
      }
      aie::store_v(s + 16 + i, sum.to_vector<float>());
    }
  }
}

[[gnu::noinline]] void attention_result(bfloat16 *m, float *state, bfloat16 *out) {
  const bfloat16 *q = m + WORK;
  attend(q + 2 * HEAD, 1, q, state);
  for (int h = 0; h < 2; ++h) {
    const float gate = static_cast<float>(q[4 * HEAD + h]);
    const float exponent = exp16(aie::broadcast<float, 16>(gate < 0 ? gate : -gate))[0];
    const bfloat16 sigmoid = static_cast<bfloat16>(
        (gate < 0 ? exponent : 1.0f) * reciprocal(1.0f + exponent));
    const float *s = state + h * SOFTMAX_STRIDE;
    const float inv = reciprocal(s[1]);
    for (int i = 0; i < HEAD; i += 16) {
      facc value;
      value.from_vector(vmul(aie::load_v<16>(s + 16 + i), inv));
      aie::store_v(out + h * HEAD + i, to_bf16(aie::mul(to_bf16(value), sigmoid)));
    }
  }
}

[[gnu::noinline]] void convolve(bfloat16 *m, bfloat16 *out) {
  const bfloat16 *x = m + WORK;
  const bfloat16 *taps = m + ACT;
  bfloat16 *state = m + ACT + TAP_OBJS * OBJ;
  for (int i = 0; i < SLICE; i += 16) {
    const auto bx = to_bf16(aie::mul(aie::load_v<16>(x + i), aie::load_v<16>(x + 2 * SLICE + i)));
    auto sum = aie::mul(aie::load_v<16>(state + i), aie::load_v<16>(taps + i));
    for (int t = 1; t < 6; ++t) {
      const auto previous = aie::load_v<16>(state + t * SLICE + i);
      sum = aie::mac(sum, previous, aie::load_v<16>(taps + t * SLICE + i));
      aie::store_v(state + (t - 1) * SLICE + i, previous);
    }
    sum = aie::mac(sum, bx, aie::load_v<16>(taps + 6 * SLICE + i));
    aie::store_v(state + 5 * SLICE + i, bx);
    aie::store_v(out + i, to_bf16(aie::mul(to_bf16(sum), aie::load_v<16>(x + SLICE + i))));
  }
}

[[gnu::noinline]] void gate_ffn(bfloat16 *x, const bfloat16 *table) {
  const auto *lookup = reinterpret_cast<const uint16_t *>(table);
  auto *bits = reinterpret_cast<uint16_t *>(x);
  for (int i = 0; i < ROWS; ++i) {
    const uint16_t b = bits[i];
    const int e = (b >> 7) & 255;
    if (e < SILU_E0)
      bits[i] = e > 1 ? static_cast<uint16_t>(b - 0x80) : b;
    else if (e >= SILU_E1)
      bits[i] = b & 0x8000 ? 0x8000 : b;
    else
      bits[i] = lookup[(b & 0x8000 ? SILU_HALF : 0) + ((e - SILU_E0) << 7) + (b & 127)];
  }
  for (int i = 0; i < ROWS; i += 16)
    aie::store_v(x + i, to_bf16(aie::mul(aie::load_v<16>(x + i), aie::load_v<16>(x + ROWS + i))));
}

// A row spans whole FIFO objects. Commit its BF16 result at the boundary.
[[gnu::noinline]] void row_part(const bfloat16 *o, const bfloat16 *x, float *sum,
                               bfloat16 *out, int col, int cols) {
  const float dot = dot512(o, x + col * OBJ);
  if (col == 0)
    *sum = dot;
  else
    *sum += dot;
  if (col == cols - 1)
    *out = static_cast<bfloat16>(*sum);
}

[[gnu::noinline]] void head_score(const bfloat16 *o, const bfloat16 *x, float *s,
                                 const int32_t *ctl, int i) {
  bfloat16 rounded;
  row_part(o, x, s + 2, &rounded, i % HOBJ, HOBJ);
  if (i % HOBJ == HOBJ - 1 && ctl[BASE] + i / HOBJ != ctl[BLOCKED_TOKEN] &&
      static_cast<float>(rounded) > s[0]) {
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

[[gnu::noinline]] void consume(const bfloat16 *o, bfloat16 *m, float *s, int32_t *ctl, int i) {
  bfloat16 *x = m + ACT, *work = m + WORK;
  const int mode = ctl[MODE];
  switch (ctl[SEG]) {
  case 0:
    if (i < HOBJ)
      copy(o, m + HIN + i * OBJ, OBJ);
    else if (i == HOBJ)
      copy(o, m + AUX, OBJ);
    else if (i < 2 * HOBJ + 1)
      copy(o, work + (i - HOBJ - 1) * OBJ, OBJ);
    else
      copy(o, m + HDR, OBJ);
    return;
  case 1: {
    if (mode == VOCAB) {
      head_score(o, x, s, ctl, i);
      return;
    }
    const int projections = HOBJ * (mode == CONV ? CONV_ROWS : ATT_ROWS);
    if (i < projections) {
      row_part(o, x, s, work + i / HOBJ, i % HOBJ, HOBJ);
      return;
    }
    const int tail = i - projections;
    if (mode == CONV) {
      copy(o, x + tail * OBJ, OBJ);
      return;
    }
    if (tail == 0)
      prepare_attention(m, s, mode);
    int first = tail * 2;
    const int oldest = mode == SLIDING ? ctl[PAST] - (WINDOW - 1) : 0;
    const int skip = first < oldest ? oldest - first : 0;
    const int left = ctl[PAST] - first;
    const int count = (left < 2 ? left : 2) - skip;
    if (count > 0)
      attend(o + skip * CACHE_WORDS, count, work, s);
    return;
  }
  case 2:
    if (mode == VOCAB)
      pick(o, work);
    else if (i < HOBJ)
      copy(o, x + i * OBJ, OBJ);
    else
      row_part(o, x, s, work + (i - HOBJ) / HOBJ, (i - HOBJ) % HOBJ, HOBJ);
    return;
  case 3:
    if (i < HOBJ)
      copy(o, x + i * OBJ, OBJ);
    else if (i < 3 * HOBJ) {
      const int offset = (i % HOBJ) * OBJ;
      copy(o, work + offset, OBJ);
      if (i == 2 * HOBJ - 1) {
        rms(x, work, x, HIDDEN);
        add(m + HIN, x, m + HIN, HIDDEN);
      } else if (i == 3 * HOBJ - 1) {
        rms(m + HIN, work, x, HIDDEN);
      }
    } else {
      const int part = i - 3 * HOBJ;
      const int row = part / HOBJ;
      row_part(o, x, s, work + (row >> 1) + ((row & 1) ? ROWS : 0), part % HOBJ, HOBJ);
    }
    return;
  case 4:
    if (i < FOBJ)
      copy(o, x + i * OBJ, OBJ);
    else
      row_part(o, x, s, work + (i - FOBJ) / FOBJ, (i - FOBJ) % FOBJ, FOBJ);
    return;
  default:
    copy(o, i < HOBJ ? x + i * OBJ : work + (i - HOBJ) * OBJ, OBJ);
  }
}

[[gnu::noinline]] void produce(bfloat16 *o, bfloat16 *m, float *s,
                               const bfloat16 *table, int32_t *ctl, int j) {
  bfloat16 *work = m + WORK;
  const int offset = ctl[CORE] * SLICE;
  for (int i = 0; i < OBJ; i += 16)
    aie::store_v(o + i, aie::zeros<bfloat16, 16>());
  switch (ctl[SEG]) {
  case 1:
    if (ctl[MODE] == VOCAB) {
      const int32_t row = ctl[BASE] + static_cast<int32_t>(s[1]);
      __builtin_memcpy(o, s, sizeof(float));
      __builtin_memcpy(o + 2, &row, sizeof(row));
    } else if (ctl[MODE] == CONV) {
      if (j == 0)
        convolve(m, o);
      else
        copy(m + ACT + (TAP_OBJS + j - 1) * OBJ, o, OBJ);
    } else if (j == 0) {
      attention_result(m, s, o);
    } else {
      copy(work + 2 * HEAD, o, CACHE_WORDS);
    }
    return;
  case 2:
  case 4:
    copy(work, o, SLICE);
    return;
  case 3:
    if (j == 0)
      gate_ffn(work, table);
    copy(work + j * OBJ, o, j == 0 ? OBJ : ROWS - OBJ);
    return;
  default:
    rms(m + ACT, work, m + ACT, HIDDEN);
    add(m + HIN + offset, m + ACT + offset, o, SLICE);
  }
}

[[gnu::noinline]] void advance(bfloat16 *m, float *s, int32_t *ctl) {
  if (ctl[SEG] == 0) {
    rms(m + HIN, m + WORK, m + ACT, HIDDEN);
    ctl[MODE] = read_i32(m + HDR + 500);
    ctl[BASE] = read_i32(m + HDR + 504);
    ctl[PAST] = read_i32(m + AUX + 256);
    ctl[BLOCKS] = read_i32(m + AUX + 258);
    ctl[BLOCKED_TOKEN] = read_i32(m + AUX + 260) - 1;
    s[0] = -3.4028235e38f;
    s[1] = 0.0f;
  }
  const int mode = ctl[MODE];
  const int seg = ctl[SEG] == 5 ? 0 : ctl[SEG] + 1;
  constexpr int in[6] = {2 * HOBJ + 2, 0, HOBJ * (1 + SLICE),
                         HOBJ * (3 + 2 * ROWS), FOBJ * (1 + SLICE), 2 * HOBJ};
  constexpr int out[6] = {0, 0, 1, 2, 1, 1};
  ctl[SEG] = seg;
  ctl[N_IN] = in[seg];
  ctl[N_OUT] = out[seg];
  if (seg == 1) {
    ctl[N_IN] = mode == CONV ? CONV_ROWS * HOBJ + TAP_OBJS + STATE_OBJS :
        mode == VOCAB ? 16000 * HOBJ : ATT_ROWS * HOBJ + ctl[BLOCKS];
    ctl[N_OUT] = mode == CONV ? 1 + STATE_OBJS : mode == VOCAB ? 1 : 2;
  } else if (mode == VOCAB && seg >= 2) {
    ctl[N_IN] = ctl[N_OUT] = seg == 2 ? ctl[PICK] : 0;
  }
}

}  // namespace

extern "C" void x8_lfm3_do(bfloat16 *obj, bfloat16 *mem, float *state,
                           const bfloat16 *table, int32_t *ctl, int32_t op, int32_t i) {
  const auto saved = aie::swap_rounding(aie::rounding_mode::conv_even);
  if (op == 0)
    consume(obj, mem, state, ctl, i);
  else if (op == 1)
    produce(obj, mem, state, table, ctl, i);
  else
    advance(mem, state, ctl);
  aie::set_rounding(saved);
}
