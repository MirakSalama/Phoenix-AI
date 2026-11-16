"""Desktop chat for the LFM2.5 Phoenix NPU engine."""

import argparse
from importlib import import_module
from pathlib import Path
from queue import Empty, Queue
import subprocess
from threading import Event, Thread
import tkinter as tk
from tkinter import messagebox, ttk
import traceback

from dictate_input import NpuDictation, NpuDictationError, installed_bundle, record
from kokoro_voice import (MODEL as VOICE_MODEL, VOICE_PACK, VOICE_PYTHON, VOICES,
                          VoiceSettings, load_settings, save_settings)


BACKGROUND = "#101722"
PANEL = "#192434"
TEXT = "#edf3fa"
MUTED = "#a6b8ce"
ACCENT = "#74e1bc"
FONT = ("Segoe UI", 11)
METRIC_NAMES = ("Prompt tokens", "Reply tokens", "Context used",
                "Decode speed", "First token", "NPU state")
SPEECH_SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "windows_speech.ps1"


class ChatWindow:
    def __init__(self, root, max_new_tokens):
        self.root = root
        self.max_new_tokens = max_new_tokens
        self.requests = Queue()
        self.events = Queue()
        self.engine_module = None
        self.engine = None
        self.ready = False
        self.closing = False
        self.failed = False
        self.capacity = None
        self.voice_process = None
        self.voice_cancelled = False
        self.voice_mode = tk.BooleanVar(value=False)
        self.voice_settings = load_settings()
        self.voice_dialog = None
        self.dictation_bundle = installed_bundle()
        self.dictation = None
        self.recording = None

        root.title("LFM2.5 NPU Chat")
        root.geometry("850x640")
        root.configure(bg=BACKGROUND)
        root.protocol("WM_DELETE_WINDOW", self.close)
        root.columnconfigure(0, weight=1)
        root.rowconfigure(1, weight=1)

        header = tk.Frame(root, bg=BACKGROUND)
        header.grid(row=0, column=0, sticky="ew", padx=22, pady=(15, 10))
        tk.Label(header, text="LFM2.5 Chat", font=("Segoe UI Semibold", 22),
                 bg=BACKGROUND, fg=TEXT).pack(side="left")
        tk.Label(header, text="PHOENIX NPU   /   230M", bg=PANEL, fg=ACCENT,
                 font=("Segoe UI Semibold", 10), padx=12, pady=5).pack(side="right")

        chat_panel = tk.Frame(root, bg=PANEL)
        chat_panel.grid(row=1, column=0, sticky="nsew", padx=22)
        self.transcript = tk.Text(chat_panel, bg=PANEL, fg=TEXT, font=FONT, wrap="word",
                                  width=1, height=1, bd=0, padx=18, pady=14, state="disabled",
                                  spacing1=2, spacing3=4, highlightthickness=0)
        scroll = ttk.Scrollbar(chat_panel, orient="vertical", command=self.transcript.yview)
        self.transcript.configure(yscrollcommand=scroll.set)
        scroll.pack(side="right", fill="y")
        self.transcript.pack(side="left", fill="both", expand=True)
        self.transcript.tag_configure("user", foreground="#a6c9ff", font=("Segoe UI Semibold", 10))
        self.transcript.tag_configure("assistant", foreground=ACCENT, font=("Segoe UI Semibold", 10))
        self.transcript.tag_configure("notice", foreground=MUTED)
        self.append("Send a message to start a local conversation.\n\n", "notice")

        stats = tk.Frame(root, bg=PANEL)
        stats.grid(row=2, column=0, sticky="ew", padx=22, pady=(8, 0))
        self.metrics = []
        for column in range(3):
            stats.columnconfigure(column, weight=1, uniform="metrics")
        for index, name in enumerate(METRIC_NAMES):
            row, column = divmod(index, 3)
            cell = tk.Frame(stats, bg=PANEL)
            cell.grid(row=row, column=column, sticky="ew", padx=12, pady=(4, 3))
            tk.Label(cell, text=name, font=("Segoe UI", 9), bg=PANEL, fg=MUTED).pack(anchor="w")
            value = tk.Label(cell, text="—", font=("Segoe UI Semibold", 11), bg=PANEL, fg=TEXT)
            value.pack(anchor="w")
            self.metrics.append(value)

        composer = tk.Frame(root, bg=BACKGROUND)
        composer.grid(row=3, column=0, sticky="ew", padx=22, pady=(12, 0))
        composer.columnconfigure(0, weight=1)
        self.prompt = tk.Text(composer, width=1, height=3, wrap="word", font=FONT, bg=PANEL, fg=TEXT,
                              insertbackground=TEXT, bd=0, padx=10, pady=8,
                              highlightthickness=1, highlightbackground="#34465d",
                              highlightcolor=ACCENT)
        self.prompt.grid(row=0, column=0, sticky="nsew")
        self.prompt.bind("<Return>", self.send_on_enter)
        actions = tk.Frame(composer, bg=BACKGROUND)
        actions.grid(row=0, column=1, sticky="ns", padx=(10, 0))
        self.send_button = tk.Button(actions, text="Send", command=self.send, font=FONT, width=10,
                                     bg=ACCENT, fg=BACKGROUND, activebackground="#9bf2d3",
                                     disabledforeground="#46695e", relief="flat", pady=3)
        self.send_button.pack(fill="x")
        self.reset_button = tk.Button(actions, text="New chat", command=self.reset, font=FONT,
                                      bg=PANEL, fg=TEXT, activebackground="#34465d",
                                      activeforeground=TEXT, disabledforeground=MUTED, relief="flat")
        self.reset_button.pack(fill="x", pady=(6, 0))

        voice_bar = tk.Frame(root, bg=BACKGROUND)
        voice_bar.grid(row=4, column=0, sticky="ew", padx=22, pady=(7, 0))
        self.mic_button = tk.Button(voice_bar, text="Mic", command=self.toggle_mic, font=FONT,
                                    bg=PANEL, fg=TEXT, activebackground="#34465d",
                                    activeforeground=TEXT, disabledforeground=MUTED,
                                    relief="flat", width=12)
        self.mic_button.pack(side="left")
        tk.Checkbutton(voice_bar, text="Voice mode", variable=self.voice_mode, bg=BACKGROUND,
                       fg=TEXT, activebackground=BACKGROUND, activeforeground=TEXT,
                       selectcolor=PANEL, font=FONT).pack(side="left", padx=(10, 3))
        self.stt_status = tk.Label(voice_bar,
                                  text="STT: NPU Dictate" if self.dictation_bundle else "STT: Windows",
                                  font=("Segoe UI", 9), bg=BACKGROUND, fg=MUTED)
        self.stt_status.pack(side="left", padx=(6, 0))
        tk.Button(voice_bar, text="Voice settings", command=self.open_voice_settings, font=FONT,
                  bg=PANEL, fg=TEXT, activebackground="#34465d", activeforeground=TEXT,
                  relief="flat", padx=9).pack(side="right")

        footer = tk.Frame(root, bg=BACKGROUND)
        footer.grid(row=5, column=0, sticky="ew", padx=22, pady=(7, 11))
        self.status = tk.Label(footer, text="Loading model…", font=("Segoe UI", 10),
                               bg=BACKGROUND, fg=MUTED)
        self.status.pack(side="left")
        tk.Label(footer, text="Enter to send  ·  Shift+Enter for a new line",
                 font=("Segoe UI", 9), bg=BACKGROUND, fg=MUTED).pack(side="right")
        root.update_idletasks()
        root.minsize(560, max(440, root.winfo_reqheight()))
        self.set_ready(False)
        self.worker = Thread(target=self.run_engine)
        self.worker.start()
        root.after(25, self.poll_events)

    def run_engine(self):
        try:
            self.engine_module = import_module("010_lfm25_npu_chat")
            self.engine = self.engine_module.NPUChat(log=lambda line: self.events.put(("status", line)))
            self.events.put(("ready", self.engine.model.capacity))
            history = []
            for action, payload in iter(self.requests.get, None):
                if action == "reset":
                    history.clear()
                    continue
                if action == "dictate":
                    self.transcribe(payload)
                    continue
                turn = {"role": "user", "content": payload}
                try:
                    result = self.engine.respond(
                        [*history, turn], self.max_new_tokens,
                        on_text=lambda chunk: self.events.put(("chunk", chunk)),
                    )
                except ValueError as error:
                    self.events.put(("rejected", str(error)))
                    continue
                history.extend([turn, {"role": "assistant", "content": result["response"]}])
                self.events.put(("reply", result))
        except Exception as error:
            traceback.print_exc()
            self.events.put(("error", str(error)))
        finally:
            self.events.put(("stopped", None))

    def transcribe(self, pieces):
        try:
            if self.dictation is None:
                self.dictation = NpuDictation(self.dictation_bundle)
            text, npu_seconds = self.dictation.transcribe(pieces)
        except NpuDictationError:
            raise
        except Exception as error:
            self.events.put(("voice", ("Listen", "", str(error))))
        else:
            self.events.put(("dictated", (text, npu_seconds)))

    def append(self, text, tag=None):
        self.transcript.configure(state="normal")
        self.transcript.insert("end", text, tag or ())
        self.transcript.configure(state="disabled")
        self.transcript.see("end")

    def set_ready(self, enabled):
        self.ready = enabled and not self.closing
        idle = self.ready and self.voice_process is None and self.recording is None
        state = "normal" if idle else "disabled"
        self.send_button.configure(state=state)
        self.reset_button.configure(state=state)
        mic_text = ("Transcribe" if self.recording is not None else
                    "Stop voice" if self.voice_process is not None else "Mic")
        self.mic_button.configure(state="normal" if self.ready and not self.voice_cancelled else "disabled",
                                  text=mic_text)

    def send_on_enter(self, event):
        if event.state & 0x0001:  # Tk's Shift modifier bit
            return None
        self.send()
        return "break"

    def send(self):
        if not self.ready or self.voice_process is not None or self.recording is not None:
            return
        prompt = self.prompt.get("1.0", "end-1c").strip()
        if not prompt:
            return
        self.set_ready(False)
        self.prompt.delete("1.0", "end")
        self.append("YOU\n", "user")
        self.append(f"{prompt}\n\n")
        self.append("LFM2.5\n", "assistant")
        self.transcript.mark_set("reply_start", "end-1c")
        self.transcript.mark_gravity("reply_start", "left")
        self.status.configure(text="Generating on the NPU…")
        self.requests.put(("chat", prompt))

    def reset(self):
        if not self.ready or self.voice_process is not None or self.recording is not None:
            return
        self.requests.put(("reset", None))
        self.transcript.configure(state="normal")
        self.transcript.delete("1.0", "end")
        self.transcript.configure(state="disabled")
        self.append("New conversation.\n\n", "notice")
        for metric in self.metrics:
            metric.configure(text="—")
        self.metrics[2].configure(text=f"0 / {self.capacity:,}")
        self.status.configure(text="Ready on Phoenix NPU")
        self.prompt.focus_set()

    def close(self):
        if self.closing:
            return
        self.closing = True
        if self.recording is not None:
            self.recording.set()
        self.stop_voice()
        self.set_ready(False)
        if not self.worker.is_alive():
            self.root.destroy()
            return
        self.status.configure(text="Closing after the NPU finishes…")
        self.requests.put(None)

    def toggle_mic(self):
        if self.voice_process is not None:
            self.stop_voice()
        elif self.recording is not None:
            self.recording.set()
            self.status.configure(text="Finishing recording…")
        elif self.ready:
            if self.dictation_bundle is None:
                self.start_voice("Listen")
                return
            self.recording = Event()
            self.set_ready(True)
            self.status.configure(text="Listening for NPU Dictate…")
            Thread(target=self.capture_dictation, args=(self.recording,), daemon=True).start()

    def capture_dictation(self, stop):
        try:
            pieces = record(stop)
        except Exception as error:
            self.events.put(("recorded", ([], str(error))))
        else:
            self.events.put(("recorded", (pieces, "")))

    def open_voice_settings(self):
        if self.voice_dialog is not None and self.voice_dialog.winfo_exists():
            self.voice_dialog.lift()
            return
        dialog = tk.Toplevel(self.root)
        self.voice_dialog = dialog
        dialog.title("Voice settings")
        dialog.transient(self.root)
        dialog.configure(bg=BACKGROUND)
        panel = tk.Frame(dialog, bg=BACKGROUND)
        panel.pack(fill="both", expand=True, padx=20, pady=15)
        tk.Label(panel, text="Kokoro · Local English voices", font=("Segoe UI Semibold", 13),
                 bg=BACKGROUND, fg=TEXT).pack(anchor="w")
        label = next(name for name, voice in VOICES.items() if voice == self.voice_settings.voice)
        voice = tk.StringVar(master=dialog, value=label)
        picker = ttk.Combobox(panel, textvariable=voice, values=tuple(VOICES), state="readonly")
        picker.pack(fill="x", pady=(10, 5))
        pitch = tk.DoubleVar(master=dialog, value=self.voice_settings.pitch)
        pace = tk.DoubleVar(master=dialog, value=self.voice_settings.pace)
        for name, value, low, high, step in (("Pitch (semitones)", pitch, -4, 4, 0.5),
                                             ("Pace (×)", pace, 0.75, 1.35, 0.05)):
            tk.Scale(panel, label=name, variable=value, from_=low, to=high, resolution=step,
                     digits=3, orient="horizontal", font=FONT, length=290, bg=BACKGROUND,
                     fg=TEXT, activebackground=ACCENT, troughcolor=PANEL,
                     highlightthickness=0).pack(fill="x", pady=(7, 0))

        def selected():
            return VoiceSettings(VOICES[voice.get()], pitch.get(), pace.get(), self.voice_settings.pause)

        def preview():
            if self.ready and self.voice_process is None and self.recording is None:
                self.start_voice("Speak", "Hi! This is how I sound with your current settings.", selected())
            else:
                messagebox.showinfo("Voice preview", "Wait for the current turn to finish.", parent=dialog)

        def save():
            settings = selected()
            try:
                save_settings(settings)
            except OSError as error:
                messagebox.showerror("Could not save voice settings", str(error), parent=dialog)
                return
            self.voice_settings = settings
            dialog.destroy()

        actions = tk.Frame(panel, bg=BACKGROUND)
        actions.pack(fill="x", pady=(12, 0))
        ttk.Button(actions, text="Preview voice", command=preview).pack(side="left")
        ttk.Button(actions, text="Save", command=save).pack(side="right")
        picker.focus_set()

    def start_voice(self, action, text="", settings=None):
        command = ["powershell.exe", "-NoLogo", "-NoProfile", "-ExecutionPolicy", "RemoteSigned",
                   "-File", str(SPEECH_SCRIPT), "-Action", action]
        status = "Listening on the default microphone…"
        if action == "Speak":
            if all(path.is_file() for path in (VOICE_PYTHON, VOICE_MODEL, VOICE_PACK)):
                settings = settings or self.voice_settings
                command = [str(VOICE_PYTHON), str(Path(__file__).with_name("kokoro_voice.py")),
                           "--voice", settings.voice, "--pitch", str(settings.pitch),
                           "--pace", str(settings.pace)]
                status = "Reading the reply with Kokoro…"
            else:
                status = "Kokoro files missing. Reading with the Windows voice…"
        try:
            process = subprocess.Popen(
                command,
                stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                encoding="utf-8", errors="replace", creationflags=subprocess.CREATE_NO_WINDOW,
            )
        except OSError as error:
            self.status.configure(text=f"Windows speech unavailable: {error.strerror}")
            return
        self.voice_process = process
        self.set_ready(True)
        self.status.configure(text=status)
        Thread(target=self.wait_for_voice, args=(process, action, text), daemon=True).start()

    def stop_voice(self):
        if self.voice_process is None or self.voice_cancelled:
            return
        self.voice_cancelled = True
        self.voice_process.terminate()
        self.set_ready(self.ready)
        self.status.configure(text="Stopping Windows speech…")

    def wait_for_voice(self, process, action, text):
        try:
            output, error = process.communicate(text, timeout=45 if action == "Listen" else 180)
            detail = error.strip().splitlines()[0] if error.strip() else "Windows speech could not finish."
            self.events.put(("voice", (action, output.strip(), detail if process.returncode else "")))
        except (OSError, subprocess.TimeoutExpired) as error:
            process.kill()
            process.communicate()
            detail = "Windows speech timed out." if isinstance(error, subprocess.TimeoutExpired) else str(error)
            self.events.put(("voice", (action, "", detail)))

    def finish_voice(self, action, text, error):
        cancelled = self.voice_cancelled
        self.voice_process = None
        self.voice_cancelled = False
        self.set_ready(True)
        self.status.configure(text="Ready on Phoenix NPU")
        if cancelled:
            return
        if error:
            self.status.configure(text="Speech could not finish")
            self.append(f"Voice: {error}\n", "notice")
        elif action == "Listen" and text:
            draft = self.prompt.get("1.0", "end-1c").strip()
            self.prompt.insert("end", f" {text}" if draft else text)
            self.prompt.focus_set()
            if self.voice_mode.get() and not draft:
                self.send()
            else:
                self.status.configure(text="Dictation ready to review")
        elif action == "Listen":
            self.status.configure(text="No speech recognized")

    def show_reply(self, text):
        # Replace streamed text with the complete tokenizer decode.
        self.transcript.configure(state="normal")
        self.transcript.delete("reply_start", "end-1c")
        self.transcript.configure(state="disabled")
        self.append(f"{text}\n\n")

    def update_metrics(self, reply):
        total = reply["prompt_tokens"]
        fresh = reply["prompt_tokens_processed"]
        generated = reply["generated_tokens"]
        speed = reply["decode_tokens_per_s"]
        first = reply["time_to_first_token_s"]
        values = (
            f"{fresh:,} new / {total:,} total",
            f"{generated:,} / {self.max_new_tokens:,}",
            f"{total + generated:,} / {self.capacity:,}",
            f"{speed:.1f} tok/s" if speed is not None else "—",
            f"{first:.3f} s",
            "Reused" if reply["reused_npu_state"] else "Rebuilt",
        )
        for metric, value in zip(self.metrics, values):
            metric.configure(text=value)

    def poll_events(self):
        while True:
            try:
                event, value = self.events.get_nowait()
            except Empty:
                break
            if self.closing:
                if event == "stopped":
                    self.root.destroy()
                    return
                continue
            if event == "ready":
                self.capacity = value
                self.metrics[2].configure(text=f"0 / {self.capacity:,}")
                self.status.configure(text="Ready on Phoenix NPU")
                self.set_ready(True)
                self.prompt.focus_set()
            elif event == "status":
                self.status.configure(text=f"Loading model… {value}")
            elif event == "chunk":
                self.append(value)
            elif event == "reply":
                self.show_reply(value["response"] or "(Empty reply)")
                self.update_metrics(value)
                status = "Ready on Phoenix NPU" if value["stopped_on_eos"] else "Reply limit reached"
                self.status.configure(text=status)
                self.set_ready(True)
                if self.voice_mode.get() and value["response"]:
                    self.start_voice("Speak", value["response"])
            elif event == "voice":
                self.finish_voice(*value)
            elif event == "recorded":
                self.recording = None
                pieces, error = value
                if error or not pieces:
                    self.finish_voice("Listen", "", error)
                else:
                    self.set_ready(False)
                    self.status.configure(text="Transcribing with NPU Dictate…")
                    self.requests.put(("dictate", pieces))
            elif event == "dictated":
                text, npu_seconds = value
                self.stt_status.configure(text=f"STT: NPU {npu_seconds * 1000:.0f} ms")
                self.finish_voice("Listen", text, "")
            elif event == "rejected":
                self.show_reply(value)
                self.status.configure(text="Message could not be processed")
                self.set_ready(True)
            elif event == "error":
                self.failed = True
                self.append(f"Chat stopped: {value}\n", "notice")
                self.status.configure(text="NPU chat unavailable")
                self.set_ready(False)
        self.root.after(25, self.poll_events)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--max-new-tokens", type=int, default=256)
    args = parser.parse_args()
    if args.max_new_tokens < 1:
        parser.error("--max-new-tokens must be positive")
    root = tk.Tk()
    app = ChatWindow(root, args.max_new_tokens)
    root.mainloop()
    app.worker.join()
    if app.engine_module is not None:
        app.engine_module.nd.finish(int(app.failed))
    if app.failed:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
