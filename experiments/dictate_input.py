"""Microphone input and the existing NPU Dictate encoder for desktop chat."""

import ctypes
import os
from pathlib import Path
import sys


ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))


def installed_bundle():
    local = Path(os.environ.get("LOCALAPPDATA", Path.home()))
    bundle = Path(os.environ.get("NPU_DICTATE_BUNDLE", local / "NPU Dictate" / "model-v1"))
    return bundle if (bundle / "manifest.json").is_file() else None


def record(stop, *, pause=1.0, start_timeout=8.0, max_seconds=30):
    """Capture mono audio and finish on a pause, a click, or the time limit."""
    import sounddevice as sd

    from apps.npu_dictate.chunker import BLOCK, MIN_RMS, MIN_VOICED, SR, Chunker

    chunker = Chunker()
    pieces = []
    voiced = quiet = 0
    # Dictate uses MME, which can convert the default device to 16 kHz mono.
    host = next((host for host in sd.query_hostapis() if host["name"] == "MME"), None)
    device = host["default_input_device"] if host and host["default_input_device"] >= 0 else None
    with sd.InputStream(device=device, samplerate=SR, blocksize=BLOCK, channels=1,
                        dtype="float32", latency="high") as stream:
        for index in range(max_seconds * SR // BLOCK):
            if stop.is_set():
                break
            block, _ = stream.read(BLOCK)
            pieces.extend(chunker.push(block[:, 0]))
            if chunker.level > max(3 * chunker.noise, MIN_RMS):
                voiced += 1
                quiet = 0
            else:
                quiet += 1
            if voiced >= MIN_VOICED and quiet * BLOCK >= pause * SR:
                break
            if voiced < MIN_VOICED and (index + 1) * BLOCK >= start_timeout * SR:
                break
    pieces.extend(chunker.finish())
    return pieces


class NpuDictationError(RuntimeError):
    """The device failed during a submission and must not be retried."""


class NpuDictation:
    def __init__(self, bundle):
        self.bundle = bundle
        self.encoder = None
        self.model = None

    def transcribe(self, pieces):
        from apps.npu_dictate import npu_runtime, win32
        from apps.npu_dictate.engine import Encoder, MUTEX, NEED_COMMIT_MB, memory_mb
        import numpy as np
        import parakeet_pipeline as pipeline

        handle = win32.create_mutex(MUTEX)
        existing = ctypes.get_last_error() == 183
        if not handle:
            raise RuntimeError("Could not reserve the NPU Dictate engine.")
        try:
            if existing:
                raise RuntimeError("NPU Dictate is already using its encoder. Quit its tray app and try again.")
            if self.model is None:
                problems = npu_runtime.verify(self.bundle)
                if problems:
                    raise RuntimeError("NPU Dictate files need repair: " + problems[0])
                if memory_mb()[1] < NEED_COMMIT_MB:
                    raise RuntimeError("There is not enough available memory to load NPU Dictate.")
                if self.encoder is None:
                    self.encoder = Encoder(pipeline, np, npu_runtime, self.bundle)
                self.model = pipeline.Parakeet(self.encoder, model_dir=self.bundle, threads=4)
            texts, npu_seconds = [], 0.0
            for piece in pieces:
                text, _ = self.model.transcribe(piece)
                if text.strip():
                    texts.append(text.strip())
                npu_seconds += self.encoder.npu_s
            return " ".join(texts), npu_seconds
        except Exception as error:
            if self.encoder is not None and self.encoder.npu.in_npu:
                raise NpuDictationError(f"NPU Dictate failed during a submission: {error}") from error
            raise
        finally:
            win32.kernel32.CloseHandle(handle)


class CpuDictation:
    """Local Parakeet with the int8 ONNX encoder when XDNA 1 is unavailable."""

    def __init__(self, bundle, encoder):
        self.bundle = bundle
        self.encoder = encoder
        self.model = None

    def transcribe(self, pieces):
        if self.model is None:
            import parakeet_pipeline as pipeline

            self.model = pipeline.Parakeet(self.encoder, model_dir=self.bundle, threads=4)
        texts, seconds = [], 0.0
        for piece in pieces:
            text, timings = self.model.transcribe(piece)
            if text.strip():
                texts.append(text.strip())
            seconds += timings["total"]
        return " ".join(texts), seconds
