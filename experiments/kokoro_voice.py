"""Local CPU speech synthesis for the chat window."""

import argparse
from base64 import b64encode
from concurrent.futures import ThreadPoolExecutor
from dataclasses import asdict, dataclass
import io
import json
import os
from pathlib import Path
import re
import sys
from time import perf_counter
import wave
import winsound

from phoenix_paths import MODELS, ROOT

VOICE_DIR = MODELS / "voice"
VOICE_PYTHON = Path(os.environ.get("PHOENIX_VOICE_PYTHON", VOICE_DIR / ".venv" / "Scripts" / "python.exe"))
MODEL = VOICE_DIR / "kokoro-v1.0.onnx"
VOICE_PACK = VOICE_DIR / "voices-v1.0.bin"
SETTINGS_FILE = Path(os.environ.get("PHOENIX_VOICE_SETTINGS", ROOT / "cache" / "voice-settings.json"))
VOICES = {"Heart (female)": "af_heart", "Michael (male)": "am_michael"}


@dataclass(frozen=True)
class VoiceSettings:
    voice: str = "af_heart"
    pitch: float = 0.0
    pace: float = 1.0
    pause: float = 2.5


def load_settings():
    try:
        settings = VoiceSettings(**json.loads(SETTINGS_FILE.read_text(encoding="utf-8")))
        if (settings.voice in VOICES.values() and -4 <= settings.pitch <= 4
                and 0.75 <= settings.pace <= 1.35 and 1 <= settings.pause <= 5):
            return settings
    except (OSError, ValueError, TypeError):
        pass
    return VoiceSettings()


def save_settings(settings):
    SETTINGS_FILE.parent.mkdir(parents=True, exist_ok=True)
    temporary = SETTINGS_FILE.with_suffix(f".{os.getpid()}.tmp")
    temporary.write_text(json.dumps(asdict(settings), indent=2) + "\n", encoding="utf-8")
    temporary.replace(SETTINGS_FILE)


def create_engine():
    from kokoro_onnx import Kokoro
    import onnxruntime as ort

    options = ort.SessionOptions()
    options.intra_op_num_threads = 4
    options.inter_op_num_threads = 1
    session = ort.InferenceSession(str(MODEL), sess_options=options, providers=["CPUExecutionProvider"])
    return Kokoro.from_session(session, str(VOICE_PACK))


def synthesize(engine, text, settings):
    audio, sample_rate = engine.create(text, voice=settings.voice, speed=settings.pace, lang="en-us")
    if settings.pitch:
        from pedalboard import time_stretch

        audio = time_stretch(audio, sample_rate, pitch_shift_in_semitones=settings.pitch).reshape(-1)
    return audio, sample_rate


def wave_bytes(audio, sample_rate):
    import numpy as np

    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as wav:
        wav.setparams((1, 2, sample_rate, 0, "NONE", "not compressed"))
        wav.writeframes((np.clip(audio, -1, 1) * 32767).astype("<i2").tobytes())
    return buffer.getvalue()


def play(audio, sample_rate, on_playback=None):
    import numpy as np

    # PlaySound opens a new stream for each sentence. Give the output device
    # time to start before the first phoneme, and keep the orb on that timeline.
    audio = np.pad(audio, (round(sample_rate * 0.30), 0))
    if on_playback:
        frame_size = sample_rate // 20
        frames = np.pad(audio, (0, -len(audio) % frame_size)).reshape(-1, frame_size)
        levels = np.sqrt(np.mean(frames ** 2, axis=1))
        scale = max(0.015, float(np.percentile(levels, 90))) if len(levels) else 1.0
        on_playback(np.clip(levels / scale, 0, 1).round(3).tolist())
    winsound.PlaySound(wave_bytes(audio, sample_rate), winsound.SND_MEMORY | winsound.SND_NODEFAULT)


_SENTENCE_END = re.compile(r'''[.!?]+[”’"')\]]*(?=\s|$)''')
_ABBREVIATION = re.compile(r"\b(?:mr|mrs|ms|dr|prof|sr|jr|st|vs|e\.g|i\.e|(?:[a-z]\.)+[a-z])\.$", re.IGNORECASE)


def spoken_chunks(text):
    start = 0
    for boundary in _SENTENCE_END.finditer(text):
        end = boundary.end()
        if _ABBREVIATION.search(text[max(start, end - 24):end]):
            continue
        sentence = " ".join(text[start:end].split())
        if any(char.isalnum() for char in sentence):
            yield sentence
        start = end
    tail = " ".join(text[start:].split())
    if any(char.isalnum() for char in tail):
        yield tail


def serve():
    def report(kind, **fields):
        print(json.dumps({"kind": kind, **fields}), flush=True)

    start = perf_counter()
    engine = create_engine()
    synthesize(engine, "Hello.", VoiceSettings())
    print(f"Kokoro CPU ready in {perf_counter() - start:.3f}s", file=sys.stderr, flush=True)
    report("ready")
    with ThreadPoolExecutor(max_workers=1) as synth:
        for line in sys.stdin:
            try:
                request = json.loads(line)
                settings = VoiceSettings(**request["voice"])
                if request.get("render"):
                    audio, rate = synthesize(engine, request["text"], settings)
                    report("audio", data=b64encode(wave_bytes(audio, rate)).decode("ascii") if len(audio) else None)
                    continue
                parts = iter(spoken_chunks(request["text"]))
                part = next(parts, None)
                if part:
                    start = perf_counter()
                    pending = synth.submit(synthesize, engine, part, settings)
                    first = True
                    while pending is not None:
                        audio, rate = pending.result()
                        following = next(parts, None)
                        pending = synth.submit(synthesize, engine, following, settings) if following else None
                        if first:
                            print(f"Kokoro first sentence in {perf_counter() - start:.3f}s", file=sys.stderr, flush=True)
                            first = False
                        if len(audio):
                            play(audio, rate, on_playback=lambda levels: report("playing", levels=levels))
                report("spoken")
            except Exception as error:
                # Exit on failure so a subsequent request starts with a fresh session.
                report("error", text=str(error))
                return


def main():
    sys.stdin.reconfigure(encoding="utf-8")
    if sys.argv[1:] == ["--worker"]:
        serve()
        return
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--voice", choices=VOICES.values(), required=True)
    parser.add_argument("--pitch", type=float, required=True)
    parser.add_argument("--pace", type=float, required=True)
    parser.add_argument("--playback-events", action="store_true")
    args = parser.parse_args()
    settings = VoiceSettings(args.voice, args.pitch, args.pace)
    text = sys.stdin.read().strip()
    if text:
        report = None
        if args.playback_events:
            def report(levels):
                print(json.dumps({"levels": levels}), flush=True)
        play(*synthesize(create_engine(), text, settings), on_playback=report)


if __name__ == "__main__":
    main()
