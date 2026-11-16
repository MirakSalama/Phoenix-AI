"""JSON-lines bridge for the native WinUI app. Hardware work is serial."""

from contextlib import redirect_stdout
from base64 import b64decode
from importlib import import_module
import json
import os
from pathlib import Path
from queue import Empty, Queue
import subprocess
import sys
from tempfile import TemporaryFile
from threading import Event, Thread
from time import monotonic
import traceback

from dictate_input import CpuDictation, NpuDictation, NpuDictationError, installed_bundle, record
from kokoro_voice import MODEL, VOICE_PACK, VOICE_PYTHON, VoiceSettings, load_settings


OUTPUT = sys.stdout
ROOT = Path(__file__).resolve().parents[1]


def emit(kind, **fields):
    print(json.dumps(dict(kind=kind, **fields), ensure_ascii=True), file=OUTPUT, flush=True)


class KokoroWorker:
    def __init__(self):
        self.child = None
        self.available = all(path.is_file() for path in (VOICE_PYTHON, MODEL, VOICE_PACK))

    def start(self):
        if not self.available or self.child is not None and self.child.poll() is None:
            return
        self.close()
        events = Queue()
        self.child = subprocess.Popen(
            [str(VOICE_PYTHON), str(Path(__file__).with_name("kokoro_voice.py")), "--worker"],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, encoding="utf-8",
            creationflags=subprocess.CREATE_NO_WINDOW,
        )
        self.events = events
        self.reader = Thread(target=self.read, args=(self.child.stdout, events), daemon=True)
        self.reader.start()

    @staticmethod
    def read(stream, events):
        try:
            for line in stream:
                try:
                    events.put(json.loads(line))
                except ValueError:
                    continue
        finally:
            events.put(None)

    def speak(self, text, stop, settings, request_id, *, render=False):
        if stop.is_set():
            return
        if not self.available:
            if render:
                raise RuntimeError("Kokoro is unavailable.")
            speak_windows(text, stop, request_id)
            return
        try:
            self.start()
            self.child.stdin.write(json.dumps({"text": text, "voice": vars(settings), "render": render}) + "\n")
            self.child.stdin.flush()
            deadline = monotonic() + 300
            while not stop.is_set():
                if monotonic() >= deadline:
                    raise TimeoutError("Kokoro playback timed out.")
                try:
                    event = self.events.get(timeout=0.05)
                except Empty:
                    continue
                if event is None:
                    raise RuntimeError("Kokoro stopped unexpectedly.")
                if event["kind"] == "error":
                    raise RuntimeError(event["text"])
                if event["kind"] == "playing":
                    emit("playing", id=request_id, levels=event["levels"])
                elif event["kind"] == "audio":
                    return event["data"]
                elif event["kind"] == "spoken":
                    return
        except Exception:
            self.close()
            raise
        self.close()

    def close(self):
        if self.child is not None:
            if self.child.poll() is None:
                self.child.terminate()
            self.child.wait()
            self.reader.join()
            self.child.stdin.close()
            self.child.stdout.close()
            self.child = None


def speak_windows(text, stop, request_id):
    command = ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "RemoteSigned",
               "-File", str(ROOT / "scripts" / "windows_speech.ps1"), "-Action", "Speak"]
    if stop.is_set():
        return
    with TemporaryFile() as errors, subprocess.Popen(
        command, stdin=subprocess.PIPE, stdout=subprocess.DEVNULL,
        stderr=errors, encoding="utf-8", errors="replace", creationflags=subprocess.CREATE_NO_WINDOW,
    ) as child:
        emit("playing", id=request_id, levels=[])
        try:
            with child.stdin:
                child.stdin.write(text)
        except (BrokenPipeError, OSError):
            pass  # A failed child reports its error below.
        while child.poll() is None:
            if stop.wait(0.05):
                child.terminate()
                child.wait()
                break
        if child.returncode and not stop.is_set():
            errors.seek(0)
            error = errors.read().decode("utf-8", "replace").strip()
            raise RuntimeError(error.splitlines()[-1] if error else "Voice playback failed.")


def run(jobs, shutdown, voice_only=False, benchmark=False):
    engine_module = None
    chat = dictation = None
    speech_device = "NPU"
    voice = KokoroWorker()
    result = 0
    try:
        if not voice_only:
            try:
                engine_module = import_module("010_lfm25_npu_chat")
                if os.environ.get("PHOENIX_NPU_PROGRAMS"):
                    engine_module.nd.require_phoenix()
                else:
                    engine_module.nd.device()
                chat = engine_module.NPUChat(log=lambda text: emit("status", text=text))
            except Exception as error:
                traceback.print_exc(file=sys.stderr)
                emit("status", text=f"NPU startup failed: {error}")
        bundle = installed_bundle() if not benchmark else None
        if bundle is not None:
            if chat is not None:
                dictation = NpuDictation(bundle)
            elif (encoder := os.environ.get("PHOENIX_CPU_DICTATE")) and Path(encoder).is_file():
                dictation = CpuDictation(bundle, Path(encoder))
                speech_device = "CPU"
        Thread(target=read_jobs, args=(jobs, shutdown), daemon=True).start()
        emit("ready", capacity=chat.model.capacity if chat is not None else 0,
             dictation=dictation is not None, dictation_device=speech_device if dictation is not None else None,
             npu=chat is not None, web_voice=voice.available)
        for job, stop, cancel in iter(jobs.get, None):
            if shutdown.is_set():
                break
            request_id = job["id"]
            try:
                action = job["kind"]
                if action == "chat":
                    if chat is None:
                        raise RuntimeError("The NPU chat worker is unavailable.")
                    if cancel.is_set():
                        continue
                    try:
                        reply = chat.respond(job["messages"], job.get("max_tokens", 256),
                                             on_text=lambda text: emit("chunk", id=request_id, text=text),
                                             cancelled=cancel.is_set)
                    except InterruptedError:
                        if cancel.is_set():
                            continue
                        raise
                    emit("reply", id=request_id, **reply)
                elif action == "listen":
                    if dictation is None:
                        raise RuntimeError("The NPU Dictate model bundle could not be found.")
                    emit("phase", id=request_id, text="Listening")
                    if job.get("warm_voice") and not cancel.is_set():
                        try:
                            voice.start()
                        except OSError as error:
                            print(f"Kokoro preload failed: {error}", file=sys.stderr, flush=True)
                    pieces = [] if cancel.is_set() else record(
                        stop, pause=load_settings().pause, start_timeout=25, max_seconds=60)
                    if not pieces or cancel.is_set():
                        emit("heard", id=request_id, text="", npu_ms=0)
                        continue
                    emit("phase", id=request_id, text="Transcribing")
                    text, elapsed = dictation.transcribe(pieces)
                    emit("heard", id=request_id, text=text, npu_ms=round(elapsed * 1000), device=speech_device)
                elif action == "web_dictate":
                    if dictation is None:
                        raise RuntimeError("The local dictation model is unavailable.")
                    import numpy as np
                    from apps.npu_dictate.chunker import Chunker, SR

                    pcm = b64decode(job["pcm"], validate=True)
                    if not 0 < len(pcm) <= 30 * SR * 2 or len(pcm) % 2:
                        raise ValueError("Invalid dictation audio length.")
                    chunker = Chunker()
                    pieces = chunker.push(np.frombuffer(pcm, dtype="<i2").astype(np.float32) / 32768)
                    pieces.extend(chunker.finish())
                    text, elapsed = dictation.transcribe(pieces) if pieces else ("", 0)
                    emit("web_transcript", id=request_id, text=text, npu_ms=round(elapsed * 1000), device=speech_device)
                elif action == "speak":
                    settings = VoiceSettings(**job["voice"]) if "voice" in job else load_settings()
                    voice.speak(job["text"], stop, settings, request_id)
                    emit("spoken", id=request_id)
                elif action == "web_speak":
                    audio = voice.speak(job["text"], stop, load_settings(), request_id, render=True)
                    emit("web_audio", id=request_id, audio=audio)
                elif action == "release_voice":
                    voice.close()
            except NpuDictationError:
                raise
            except Exception as error:
                traceback.print_exc(file=sys.stderr)
                emit("error", id=request_id, text=str(error))
    except Exception as error:
        traceback.print_exc(file=sys.stderr)
        emit("fatal", text=str(error))
        result = 1
    finally:
        voice.close()
    if engine_module is not None:
        engine_module.nd.finish(result)
    raise SystemExit(result)


def read_jobs(jobs, shutdown):
    active_stop = Event()
    active_cancel = Event()
    try:
        for line in sys.stdin:
            job = json.loads(line)
            if job["kind"] == "stop":
                active_stop.set()
            elif job["kind"] == "cancel":
                active_cancel.set()
                active_stop.set()
            elif job["kind"] == "shutdown":
                break
            else:
                active_stop = Event()
                active_cancel = Event()
                jobs.put((job, active_stop, active_cancel))
    finally:
        shutdown.set()
        active_cancel.set()
        active_stop.set()
        jobs.put(None)


if __name__ == "__main__":
    with redirect_stdout(sys.stderr):
        run(Queue(), Event(), voice_only="--voice-only" in sys.argv, benchmark="--benchmark" in sys.argv)
