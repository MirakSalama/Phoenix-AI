"""Use host row lookups for Gemma's large per-layer embedding table on Windows."""

import iron
from iron.applications.gemma4.model import Gemma4
from iron.lm import CausalLM
from iron.operators import Copy
import numpy as np
from ml_dtypes import bfloat16


class WindowsGemma4(Gemma4):
    """Keep the decoder and LM head on XRT without pinning the full PLE table."""

    def load_state_dict(self, tree, *, path=""):
        CausalLM.load_state_dict(self, tree, path=path)
        config = self.config
        self.ple_table = self.model.embed_tokens_per_layer.weight
        self.model.embed_tokens_per_layer = None
        self.ple_rows = iron.state(
            (config.num_hidden_layers, config.prefill_chunk, config.ple_dim), bfloat16
        )
        self.ple_scale = float(bfloat16(np.sqrt(config.ple_dim)))

    def layer(self, call, i, weights, x):
        config = self.config
        projection = self.model.per_layer_model_projection.weight
        width = config.ple_dim
        ple = iron.functional.linear(call.embeddings, projection[i * width:(i + 1) * width],
                                     precision=config.precision)
        norm = self.model.per_layer_projection_norm.weight
        ple = iron.functional.rms_norm(ple, (width,), norm, config.eps * config.hidden_size)
        staged = self.ple_rows[i][:call.rows] if call.prompt else self.ple_rows[i, 0]
        token = Copy.step(staged)
        return weights(x, call, (ple + token.reshape(ple.shape)) * 2**-0.5)

    def __call__(self, ids=None, before=None, **values):
        config = self.config
        if ids is None:
            keys = np.asarray([values["token"]], dtype=np.int32)
            version = self.decode_step
        else:
            keys = np.asarray(ids[:values["rows"]], dtype=np.int32)
            version = self.prompt_chunk
        for placeholder in (config.image_token_id, config.audio_token_id, config.video_token_id):
            if placeholder is not None:
                keys = np.where(keys == placeholder, config.pad_token_id, keys)
        rows = np.asarray(self.ple_table[keys], dtype=np.float32) * self.ple_scale
        rows = rows.reshape(keys.size, config.num_hidden_layers, config.ple_dim).transpose(1, 0, 2)
        staging = np.zeros(self.ple_rows.shape, dtype=bfloat16)
        staging[:, :keys.size] = rows
        version.write(self.ple_rows, staging)
        return super().__call__(ids, before, **values)
