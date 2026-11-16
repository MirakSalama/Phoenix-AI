"""Choose a GGUF context from llama.cpp memory estimates without allocating it."""

import ctypes
from ctypes import wintypes
import re
import subprocess


MODEL_CONTEXTS = {
    "lfm25-230m": 128_000,
    "lfm25-350m": 128_000,
    "lfm3-1.7b": 262_144,
    "gemma4-e2b": 131_072,
    "qwen3.5-2b": 262_144,
}


class MemoryStatus(ctypes.Structure):
    _fields_ = [("length", wintypes.DWORD), ("load", wintypes.DWORD)] + [
        (name, ctypes.c_ulonglong) for name in
        ("total_phys", "free_phys", "total_commit", "free_commit", "total_virtual",
         "free_virtual", "free_extended_virtual")
    ]


def host_budget(allow_paging):
    status = MemoryStatus()
    status.length = ctypes.sizeof(status)
    if not ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(status)):
        raise OSError("Windows did not report available RAM.")
    mib = 1024 * 1024
    physical = status.free_phys // mib
    commit = status.free_commit // mib
    available = min(commit - 768, physical + (1024 if allow_paging else 0))
    return max(0, available - min(1536, max(384, available // 4)))


def estimate(executable, placement, context):
    result = subprocess.run([
        str(executable), *placement, "--ctx-size", str(context), "--fit-print", "on",
    ], check=True, capture_output=True, text=True, encoding="utf-8", errors="replace",
        timeout=25, creationflags=subprocess.CREATE_NO_WINDOW)
    rows = {}
    for name, weights, cache, compute in re.findall(
            r"(?m)^(Host|Vulkan\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s*$", result.stdout):
        rows[name] = int(weights) + int(cache) + int(compute)
    if "Host" not in rows:
        raise ValueError("llama.cpp did not report a host memory estimate.")
    return rows


def fit_context(executable, placement, device, gpu, model_id, context_limit=0):
    """Leave headroom for Windows and keep the requested device placement."""
    minimum, maximum = 1024, MODEL_CONTEXTS[model_id]
    if context_limit > 0:
        maximum = max(minimum, min(maximum, context_limit))
    if device != "none" and gpu is None:
        raise ValueError("The selected GPU did not report available memory.")
    ram = host_budget(allow_paging=gpu is None or not gpu["Integrated"])
    gpu_budget = None if gpu is None else max(0, gpu["FreeMiB"] - max(384, gpu["FreeMiB"] // 10))

    def costs(context):
        rows = estimate(executable, placement, context)
        if gpu_budget is None:
            return (rows["Host"],)
        device_memory = rows[device]
        host_memory = rows["Host"] + (device_memory if gpu["Integrated"] else 0)
        return host_memory, device_memory

    budgets = (ram,) if gpu_budget is None else (ram, gpu_budget)
    high = costs(maximum)
    if all(used <= available for used, available in zip(high, budgets)):
        return maximum
    low = costs(minimum)
    if any(used >= available for used, available in zip(low, budgets)):
        return minimum
    fraction = min((available - base) / (peak - base)
                   for base, peak, available in zip(low, high, budgets) if peak > available)
    candidate = max(minimum, int((minimum + fraction * (maximum - minimum)) // 256) * 256)
    if candidate > minimum and any(used > available for used, available in zip(costs(candidate), budgets)):
        candidate = max(minimum, candidate * 3 // 4 // 256 * 256)
    return candidate
