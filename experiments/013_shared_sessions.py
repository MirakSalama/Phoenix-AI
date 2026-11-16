"""Benchmark independent sessions sharing one llama.cpp model on a local GPU."""

import argparse
from concurrent.futures import ThreadPoolExecutor
from contextlib import contextmanager
import ctypes as c
from ctypes import wintypes as w
from datetime import datetime, timezone
import json
import math
from pathlib import Path
import secrets
import shutil
import socket
from statistics import median
import subprocess
from tempfile import TemporaryFile
from threading import Event, Thread
from time import monotonic, sleep
from urllib.error import URLError
from urllib.request import ProxyHandler, Request, build_opener

from llama_vulkan_chat import MODELS, SERVER, discover_devices
from phoenix_paths import MODELS as MODEL_ROOT


MODEL_ID = "lfm25-230m"
CONTEXT_PER_SLOT = 2048
MIB = 1024 ** 2
TOPICS = (
    "backing up workshop notes", "organizing a home office", "packing for a short trip",
    "reducing food waste", "starting a vegetable garden", "maintaining a bicycle",
    "labeling family photos", "preparing for a power outage", "organizing a tool drawer",
    "planning a group picnic", "learning keyboard shortcuts", "setting up a study routine",
    "tracking household expenses", "recording a clear voice note", "planning a book exchange",
    "cleaning a computer desk",
)
QUALITY = (
    ("What is 19 plus 23?", "42", ("32", "41", "52")),
    ("What is 9 times 7?", "63", ("56", "67", "72")),
    ("What is 81 divided by 9?", "9", ("8", "10", "11")),
    ("Subtract 27 from 65.", "38", ("28", "32", "42")),
    ("The lockers contain red, blue, and green bags in that order. What is the second color?",
     "blue", ("red", "green", "yellow")),
    ("Mira is assigned desk 17 and Sam is assigned desk 23. Which desk number belongs to Sam?",
     "23", ("17", "20", "32")),
    ("Inventory: spoons 12, plates 8, bowls 5. How many bowls are in inventory?", "5", ("8", "12", "25")),
    ("The code for the south gate is K8. The code for the west gate is B2. Give the west gate code.",
     "B2", ("K8", "B8", "K2")),
    ("Dana arrived before Noor. Noor arrived before Eli. Who arrived last?", "Eli", ("Dana", "Noor", "Mira")),
    ("The triangle is inside the box. The circle is outside the box. Which shape is inside?",
     "triangle", ("circle", "square", "hexagon")),
    ("The meeting was moved from 11:30 to 13:00. Give the new time.", "13:00", ("11:30", "13:30", "12:00")),
    ("Which number is largest: 17, 23, 32, or 30?", "32", ("17", "23", "30")),
    ("Which option exactly matches the identifier PX-71?", "PX-71", ("XP-71", "PX-17", "PX-11")),
    ("Sort 8, 3, 5 from smallest to largest.", "3, 5, 8", ("3, 8, 5", "5, 3, 8", "8, 5, 3")),
    ("Which number is even?", "14", ("13", "15", "17")),
    ("What is the capital of France?", "Paris", ("Rome", "Berlin", "Madrid")),
)


class ProcessMemory(c.Structure):
    _fields_ = [
        ("size", w.DWORD), ("faults", w.DWORD), ("peak_working_set", c.c_size_t),
        ("working_set", c.c_size_t), ("peak_paged", c.c_size_t), ("paged", c.c_size_t),
        ("peak_nonpaged", c.c_size_t), ("nonpaged", c.c_size_t),
        ("pagefile", c.c_size_t), ("peak_pagefile", c.c_size_t),
    ]


class CounterItem(c.Structure):
    _fields_ = [("name", w.LPWSTR), ("status", w.DWORD), ("value", c.c_double)]


class MemorySampler:
    """Read Windows counters for only the server process."""

    def __init__(self, pid):
        self.pid = pid
        self.kernel = c.WinDLL("kernel32", use_last_error=True)
        self.kernel.OpenProcess.argtypes = (w.DWORD, w.BOOL, w.DWORD)
        self.kernel.OpenProcess.restype = w.HANDLE
        self.kernel.CloseHandle.argtypes = (w.HANDLE,)
        self.handle = self.kernel.OpenProcess(0x1000, False, pid)
        self.memory = c.WinDLL("psapi", use_last_error=True)
        self.memory.GetProcessMemoryInfo.argtypes = (w.HANDLE, c.POINTER(ProcessMemory), w.DWORD)
        self.pdh = c.WinDLL("pdh")
        self.pdh.PdhOpenQueryW.argtypes = (w.LPCWSTR, c.c_size_t, c.POINTER(w.HANDLE))
        self.pdh.PdhAddEnglishCounterW.argtypes = (w.HANDLE, w.LPCWSTR, c.c_size_t, c.POINTER(w.HANDLE))
        self.pdh.PdhCollectQueryData.argtypes = (w.HANDLE,)
        self.pdh.PdhGetFormattedCounterArrayW.argtypes = (
            w.HANDLE, w.DWORD, c.POINTER(w.DWORD), c.POINTER(w.DWORD), c.c_void_p)
        self.pdh.PdhGetFormattedCounterArrayW.restype = w.DWORD
        self.pdh.PdhCloseQuery.argtypes = (w.HANDLE,)
        self.query = w.HANDLE()
        self.counters = {}
        self.peaks = {"working_set_mib": None, "gpu_dedicated_mib": None, "gpu_shared_mib": None}
        if self.pdh.PdhOpenQueryW(None, 0, c.byref(self.query)) == 0:
            for field, counter_name in (("gpu_dedicated_mib", "Dedicated Usage"),
                                        ("gpu_shared_mib", "Shared Usage")):
                counter = w.HANDLE()
                path = rf"\GPU Process Memory(*)\{counter_name}"
                if self.pdh.PdhAddEnglishCounterW(self.query, path, 0, c.byref(counter)) == 0:
                    self.counters[field] = counter

    def sample(self):
        memory = ProcessMemory()
        memory.size = c.sizeof(memory)
        if self.handle and self.memory.GetProcessMemoryInfo(self.handle, c.byref(memory), memory.size):
            self.record("working_set_mib", memory.peak_working_set / MIB)
        if self.counters and self.pdh.PdhCollectQueryData(self.query) == 0:
            for field, counter in self.counters.items():
                size, count = w.DWORD(), w.DWORD()
                get_array = self.pdh.PdhGetFormattedCounterArrayW
                if get_array(counter, 0x200, c.byref(size), c.byref(count), None) != 0x800007D2:
                    continue
                buffer = c.create_string_buffer(size.value)
                if get_array(counter, 0x200, c.byref(size), c.byref(count), buffer) != 0:
                    continue
                items = (CounterItem * count.value).from_buffer(buffer)
                values = [item.value for item in items if item.status <= 1 and item.name
                          and item.name.lower().startswith(f"pid_{self.pid}_") and math.isfinite(item.value)]
                if values:
                    self.record(field, sum(values) / MIB)

    def record(self, field, value):
        previous = self.peaks[field]
        self.peaks[field] = round(value if previous is None else max(previous, value), 2)

    def close(self):
        if self.query:
            self.pdh.PdhCloseQuery(self.query)
        if self.handle:
            self.kernel.CloseHandle(self.handle)


class Runner:
    def __init__(self, base, key):
        self.base, self.key = base, key
        self.opener = build_opener(ProxyHandler({}))

    def request(self, path, payload=None, timeout=60):
        headers = {"Authorization": f"Bearer {self.key}", "Content-Type": "application/json"}
        body = json.dumps(payload).encode() if payload is not None else None
        return self.opener.open(Request(self.base + path, body, headers), timeout=timeout)

    def get(self, path, timeout=2):
        with self.request(path, timeout=timeout) as reply:
            return json.load(reply)

    def complete(self, prompt, limit, *, fixed=False, seed=1234, grammar=None):
        payload = {
            "messages": [{"role": "user", "content": prompt}],
            "max_tokens": limit, "temperature": 0, "seed": seed,
            "stream": True, "stream_options": {"include_usage": True},
            "ignore_eos": fixed, "cache_prompt": False,
        }
        if grammar is not None:
            payload["grammar"] = grammar
        content = []
        start = monotonic()
        first = None
        usage, timings = {}, {}
        with self.request("/v1/chat/completions", payload) as reply:
            for line in reply:
                if not line.startswith(b"data: "):
                    continue
                data = line[6:].strip()
                if data == b"[DONE]":
                    break
                event = json.loads(data)
                if "error" in event:
                    raise RuntimeError(event["error"].get("message", "Server error"))
                choices = event.get("choices") or []
                text = choices[0].get("delta", {}).get("content") if choices else None
                if text:
                    first = monotonic() - start if first is None else first
                    content.append(text)
                usage = event.get("usage") or usage
                timings = event.get("timings") or timings
        return {
            "text": "".join(content), "seconds": monotonic() - start, "first_token_seconds": first,
            "input_tokens": usage["prompt_tokens"], "output_tokens": usage["completion_tokens"],
            "prompt_per_second": timings.get("prompt_per_second"),
        }


@contextmanager
def launch(model, device, slots):
    with socket.socket() as port_socket:
        port_socket.bind(("127.0.0.1", 0))
        port = port_socket.getsockname()[1]
    key = secrets.token_urlsafe(24)
    runner = Runner(f"http://127.0.0.1:{port}", key)
    with TemporaryFile() as log:
        process = subprocess.Popen([
            str(SERVER), "--model", str(model), "--device", device, "--split-mode", "none",
            "--n-gpu-layers", "all", "--fit", "off", "--parallel", str(slots),
            "--ctx-size", str(CONTEXT_PER_SLOT * slots), "--no-kv-unified", "--cont-batching",
            "--batch-size", "512", "--ubatch-size", "512", "--cache-ram", "0", "--no-context-shift",
            "--chat-template-file", str(model.with_name("chat_template.jinja")), "--slots",
            "--offline", "--no-ui", "--host", "127.0.0.1", "--port", str(port), "--api-key", key,
        ], stdin=subprocess.DEVNULL, stdout=log, stderr=log, creationflags=subprocess.CREATE_NO_WINDOW)
        sampler = None
        failed = False
        start = monotonic()
        try:
            sampler = MemorySampler(process.pid)
            while monotonic() - start < 30 and process.poll() is None:
                try:
                    if runner.get("/health").get("status") == "ok":
                        break
                except (URLError, TimeoutError):
                    pass
                sleep(0.1)
            else:
                raise RuntimeError("The server did not become ready.")
            state = runner.get("/slots")
            if len(state) != slots or any(slot["n_ctx"] != CONTEXT_PER_SLOT for slot in state):
                raise RuntimeError(f"Unexpected slot configuration: {[(slot['id'], slot['n_ctx']) for slot in state]}")
            sampler.sample()
            yield runner, sampler, monotonic() - start
        except Exception:
            failed = True
            raise
        finally:
            if sampler is not None:
                sampler.close()
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()
            if failed:
                log.seek(max(0, log.seek(0, 2) - 3072))
                print(log.read().decode(errors="replace"), flush=True)


def batch(runner, sampler, prompts, slots, tokens, *, fixed=False, grammar=None):
    stop = Event()
    peak_busy = 0

    def sample():
        nonlocal peak_busy
        while not stop.is_set():
            sampler.sample()
            try:
                busy = sum(bool(slot.get("is_processing")) for slot in runner.get("/slots"))
                peak_busy = max(peak_busy, busy)
            except (URLError, TimeoutError):
                pass
            stop.wait(0.12)

    observer = Thread(target=sample)
    observer.start()
    try:
        with ThreadPoolExecutor(max_workers=slots) as pool:
            start = monotonic()
            futures = [pool.submit(runner.complete, prompt, tokens, fixed=fixed, seed=1234 + i, grammar=grammar)
                       for i, prompt in enumerate(prompts)]
            results = [future.result() for future in futures]
            elapsed = monotonic() - start
    finally:
        stop.set()
        observer.join()
    return results, elapsed, peak_busy


def run_case(model, device, slots, tokens):
    with launch(model, device, slots) as (runner, sampler, startup):
        batch(runner, sampler, ["Say hello."] * slots, slots, 8)
        prompts = [f"Write a detailed numbered checklist with full sentences for {topic}." for topic in TOPICS]
        cold, cold_elapsed, cold_busy = batch(runner, sampler, prompts, slots, tokens, fixed=True)
        generated, elapsed, busy = batch(runner, sampler, prompts, slots, tokens, fixed=True)
        counts = [result["output_tokens"] for result in generated]
        if any(result["output_tokens"] != tokens for result in cold + generated):
            raise RuntimeError(f"At least one fixed job ended before generating {tokens} tokens.")
        choices = []
        labels = "ABCD"
        for index, (question, answer, distractors) in enumerate(QUALITY):
            position = (index * 3 + 1) % 4
            options = list(distractors)
            options.insert(position, answer)
            lines = "\n".join(f"{label}. {option}" for label, option in zip(labels, options, strict=True))
            choices.append((f"{question}\n{lines}\nReply with one letter only.", labels[position]))
        answers, quality_elapsed, _ = batch(runner, sampler, [prompt for prompt, _ in choices], slots, 4,
                                             grammar="root ::= [ABCD]")
        checks = [{"prompt": prompt, "expected": expected, "answer": reply["text"].strip(),
                   "correct": reply["text"].strip() == expected}
                  for (prompt, expected), reply in zip(choices, answers, strict=True)]
        first = [result["first_token_seconds"] for result in generated if result["first_token_seconds"] is not None]
        return {
            "slots": slots, "startup_seconds": startup, "observed_busy_slots": busy,
            "wall_seconds": elapsed, "output_tokens": sum(counts), "aggregate_tokens_per_second": sum(counts) / elapsed,
            "first_workload_seconds": cold_elapsed, "first_workload_busy_slots": cold_busy,
            "first_workload_tokens_per_second": sum(counts) / cold_elapsed,
            "median_first_token_seconds": median(first) if first else None,
            "median_job_seconds": median(result["seconds"] for result in generated),
            "quality_seconds": quality_elapsed, "correct": sum(check["correct"] for check in checks),
            "checks": checks, "fixed_jobs": [{k: v for k, v in job.items() if k != "text"} for job in generated],
            "peak_memory": sampler.peaks,
        }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--slots", nargs="+", type=int, choices=(1, 2, 4, 8), default=[1, 2, 4, 8])
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument("--tokens", type=int, default=96)
    parser.add_argument("--device", help="Vulkan ID. Defaults to the discrete adapter with the most free memory.")
    parser.add_argument("--output", type=Path, default=Path(__file__).with_name("013_shared_sessions_results.json"))
    args = parser.parse_args()
    if args.repeats < 1 or not 8 <= args.tokens <= 512 or len(set(args.slots)) != len(args.slots):
        parser.error("Use at least one repeat, 8–512 tokens, and unique slot counts.")
    model = MODEL_ROOT / MODEL_ID / MODELS[MODEL_ID]
    if not model.is_file() or not model.with_name("chat_template.jinja").is_file():
        parser.error(f"Missing installed model or chat template: {model}")
    devices = discover_devices()
    device = next((gpu for gpu in devices if gpu["Id"] == args.device), None) if args.device else max(
        (gpu for gpu in devices if not gpu["Integrated"]), key=lambda gpu: gpu["FreeMiB"], default=None)
    if device is None:
        parser.error("The requested Vulkan device is unavailable.")
    nvidia_smi = shutil.which("nvidia-smi")
    driver = subprocess.run([nvidia_smi, "--query-gpu=name,driver_version,memory.total", "--format=csv,noheader"],
                            capture_output=True, text=True, timeout=10).stdout.strip() if nvidia_smi else None
    version = subprocess.run([str(SERVER), "--version"], capture_output=True, text=True, check=True).stderr.strip()
    report = {
        "started_utc": datetime.now(timezone.utc).isoformat(), "model": model.name, "model_bytes": model.stat().st_size,
        "runtime": version, "device": device, "nvidia_driver": driver, "context_per_slot": CONTEXT_PER_SLOT,
        "requested_slots": args.slots, "repeats": args.repeats, "fixed_jobs": len(TOPICS),
        "fixed_tokens_per_job": args.tokens, "quality_jobs": len(QUALITY), "runs": [],
    }
    for repeat in range(args.repeats):
        offset = repeat % len(args.slots)
        for slots in args.slots[offset:] + args.slots[:offset]:
            try:
                result = run_case(model, device["Id"], slots, args.tokens)
                print(f"Round {repeat + 1}, {slots} slots: {result['wall_seconds']:.2f}s, "
                      f"{result['aggregate_tokens_per_second']:.1f} tok/s, "
                      f"{result['correct']}/{len(QUALITY)} correct, peak {result['observed_busy_slots']} active", flush=True)
            except Exception as error:
                result = {"slots": slots, "error": f"{type(error).__name__}: {error}"}
                print(f"Round {repeat + 1}, {slots} slots: {result['error']}", flush=True)
            report["runs"].append({"round": repeat + 1, **result})
            pending = args.output.with_suffix(args.output.suffix + ".partial")
            pending.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
            pending.replace(args.output)
    return int(any("error" in run for run in report["runs"]))


if __name__ == "__main__":
    raise SystemExit(main())
