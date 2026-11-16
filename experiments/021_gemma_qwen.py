"""Compare the two chat models using Phoenix's local worker and mock tool results."""

import json
import os
from pathlib import Path
import sys
from time import perf_counter


ROOT = Path(__file__).resolve().parents[1]
PORTABLE = ROOT / "dist/Phoenix-AI-Portable-win-x64"
APP = PORTABLE / "App"
QWEN = ROOT / "cache/qwen3.5-2b/Qwen3.5-2B-Q4_K_M.gguf"
if not QWEN.is_file():
    QWEN = PORTABLE / "Models/qwen3.5-2b/Qwen3.5-2B-Q4_K_M.gguf"
os.environ["PHOENIX_MODEL_DIR"] = str(PORTABLE / "Models")
sys.path.insert(0, str(APP / "experiments"))
from llama_vulkan_chat import VulkanChat, discover_devices


def message(role, content):
    return {"role": role, "content": content}


def tool(name, description, *keys):
    return {"type": "function", "function": {
        "name": name, "description": description, "parameters": {
            "type": "object", "properties": {key: {"type": "string"} for key in keys},
            "required": list(keys), "additionalProperties": False}}}


TOOLS = [
    tool("weather", "Get a weather forecast for a city and day", "city", "day"),
    tool("exchange_rate", "Get the current exchange rate of a currency pair", "base", "quote"),
    tool("find_note", "Search saved notes by topic", "topic"),
]


def verify_tool(chat, question, expected, args, result, expected_text):
    messages = [message("system", "Use the appropriate available tool when needed. Report its actual result."),
                message("user", question)]
    calls = chat.respond_tools(messages, TOOLS, 144)["message"].get("tool_calls") or []
    if len(calls) != 1:
        return {"called": False, "answered": False}
    call = calls[0]
    function = call.get("function", {})
    try:
        values = json.loads(function.get("arguments", ""))
    except (ValueError, TypeError):
        return {"called": False, "answered": False}
    called = function.get("name") == expected and isinstance(values, dict) and values.keys() == args.keys() and all(
        str(values[key]).casefold() in alternatives for key, alternatives in args.items())
    if not called:
        return {"called": False, "answered": False, "received": function}
    messages.extend([{"role": "assistant", "content": "", "tool_calls": calls},
                     {"role": "tool", "tool_call_id": call["id"], "content": result}])
    reply = chat.respond_tools(messages, TOOLS, 96)["response"]
    return {"called": True, "answered": expected_text.casefold() in reply.casefold()}


def measure(name, device, label, full):
    start = perf_counter()
    chat = VulkanChat("gemma4-e2b" if name == "Gemma" else None, device, label,
                      benchmark=True, model_file=QWEN if name == "Qwen" else None, context_limit=1024)
    elapsed = perf_counter() - start
    try:
        reply = chat.respond([message("user", "Write a numbered list of 15 specific ways to organize a small office. "
                                      "Use two complete sentences per item.")], 112)
        numbers = {"load_s": round(elapsed, 2), "first_s": reply["time_to_first_token_s"],
                   "generation_tps": reply["decode_tokens_per_s"], "tokens": reply["generated_tokens"]}
        if full:
            followup = chat.respond([message("user", "Our next team visit is to Aswan. Remember the city."),
                                     message("assistant", "Got it. I'll remember."),
                                     message("user", "Which city is our next visit to?")], 40)
            numbers["followup"] = "aswan" in followup["response"].casefold()
            numbers["tools"] = [
                verify_tool(chat, "What's the weather in Cairo tomorrow?", "weather",
                            {"city": ("cairo",), "day": ("tomorrow",)}, '{"high_c": 29, "conditions": "sunny"}', "29"),
                verify_tool(chat, "How many Egyptian pounds can I get for one US dollar right now?",
                            "exchange_rate", {"base": ("usd",), "quote": ("egp",)}, '{"rate": 46.72}', "46.72"),
                verify_tool(chat, "Check my saved notes for my favorite coffee.", "find_note",
                            {"topic": ("coffee", "favorite coffee", "my favorite coffee")},
                            "Favorite coffee: Ethiopian Yirgacheffe.", "Yirgacheffe"),
            ]
        return numbers
    finally:
        chat.close()


if __name__ == "__main__":
    hardware = [("none", "CPU", False)]
    for gpu in discover_devices():
        hardware.append((gpu["Id"], "iGPU" if gpu["Integrated"] else "dGPU", not gpu["Integrated"]))
    for device, label, full in hardware:
        for model in ("Gemma", "Qwen"):
            print(json.dumps({"device": label, "model": model, **measure(model, device, label, full)}), flush=True)
