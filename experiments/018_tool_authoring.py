"""Exercise LFM3 tool creation and then call the generated stdio MCP server."""

import argparse
import ast
import json
from pathlib import Path
import re
import subprocess
import sys
from tempfile import TemporaryDirectory

from llama_vulkan_chat import VulkanChat


def author(chat, name, fields, user):
    tool = {"type": "function", "function": {
        "name": name, "description": "Save the finished artifact.",
        "parameters": {"type": "object", "properties": {
            key: {"type": "string", "description": description} for key, description in fields.items()},
            "required": list(fields), "additionalProperties": False},
    }}
    result = chat.respond_tools([
        {"role": "system", "content": f"Build the finished artifact and call {name} to save it. "
         "For Python, implement only def handle(arguments) and return a string or JSON value. "
         "The schema describes inputs only. The arguments parameter is a Python dict keyed by the input schema property names. "
         "Access values by string key, never by position. Do not include return fields in the input schema. "
         "This fixture needs arithmetic only, so write a single function with no imports."},
        {"role": "user", "content": user},
    ], [tool], 1152)
    calls = result["message"].get("tool_calls") or []
    if len(calls) != 1 or calls[0]["function"]["name"] != name:
        raise AssertionError(f"{name} did not return a structured artifact.")
    return json.loads(calls[0]["function"]["arguments"]), result["elapsed_s"]


def verify_arithmetic_only(source):
    tree = ast.parse(source)
    allowed = (
        ast.Module, ast.FunctionDef, ast.arguments, ast.arg, ast.Assign, ast.Return, ast.Dict,
        ast.Name, ast.Load, ast.Store, ast.Constant, ast.Subscript, ast.BinOp, ast.Mult, ast.Add,
        ast.Div, ast.Sub, ast.Expr, ast.JoinedStr, ast.FormattedValue, ast.Call, ast.If,
        ast.Compare, ast.Is, ast.IsNot, ast.In, ast.NotIn, ast.Not, ast.UnaryOp, ast.USub, ast.Raise, ast.Attribute,
    )
    if len(tree.body) != 1 or not isinstance(tree.body[0], ast.FunctionDef) or tree.body[0].name != "handle":
        raise AssertionError("The generated fixture must contain one handle function.")
    for node in ast.walk(tree):
        if not isinstance(node, allowed):
            raise AssertionError(f"Unexpected Python construct {type(node).__name__}: {source}")
        if isinstance(node, ast.Attribute) and (
            node.attr != "get" or not isinstance(node.value, ast.Name) or node.value.id != "arguments"
        ):
            raise AssertionError(f"Fixture used an unexpected attribute: {source}")
        if isinstance(node, ast.Call) and isinstance(node.func, ast.Name):
            if node.func.id not in {"round", "float", "int", "str", "ValueError"}:
                raise AssertionError(f"Fixture tried to call an unexpected function: {source}")
        elif isinstance(node, ast.Call) and not isinstance(node.func, ast.Attribute):
            raise AssertionError(f"Fixture tried to call an unexpected function: {source}")


def mcp_roundtrip(manifest, source, args):
    with TemporaryDirectory(prefix="phoenix-tool-proof-") as folder:
        directory = Path(folder)
        module_path, manifest_path = directory / "handler.py", directory / "tool.json"
        module_path.write_text(source, encoding="utf-8")
        manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
        messages = [
            {"id": 1, "method": "initialize", "params": {}},
            {"method": "notifications/initialized"},
            {"id": 2, "method": "tools/list", "params": {}},
            {"id": 3, "method": "tools/call", "params": {"name": manifest["name"], "arguments": args}},
        ]
        result = subprocess.run(
            [sys.executable, "-I", "-B", str(Path(__file__).with_name("phoenix_generated_mcp.py")),
             str(manifest_path), str(module_path)],
            input="".join(json.dumps({"jsonrpc": "2.0", **message}) + "\n" for message in messages),
            text=True, encoding="utf-8", capture_output=True, timeout=12, check=True,
        )
        replies = {item["id"]: item for item in map(json.loads, result.stdout.splitlines())}
        assert replies[2]["result"]["tools"] == [manifest]
        outcome = replies[3]["result"]
        assert not outcome.get("isError"), (outcome, source)
        return outcome["content"][0]["text"]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--device", default="Vulkan1")
    args = parser.parse_args()
    chat = VulkanChat("lfm3-1.7b", args.device, "Local GPU", context_limit=4096)
    try:
        skill, skill_time = author(chat, "save_skill", {
            "name": "Lowercase slug", "description": "When to use the skill",
            "instructions": "Reusable Markdown workflow with concrete steps",
        }, "Create a skill named inventory-report for preparing a three-part stock report from inventory counts.")
        assert skill["name"] == "inventory-report" and len(skill["instructions"]) > 80, skill
        draft, draft_time = author(chat, "save_mcp_draft", {
            "name": "Lowercase hyphenated slug", "description": "What the tool does",
            "schema": "JSON text of the INPUT argument schema. The only property is required numeric celsius. Do not put the output fahrenheit here.",
            "python": "Python source defining def handle(arguments):. Return Fahrenheit as a JSON object with key fahrenheit.",
        }, "Create an MCP tool named celsius-converter. It accepts one number named celsius and returns fahrenheit using celsius * 9 / 5 + 32.")
        assert draft["name"] == "celsius-converter", draft
        verify_arithmetic_only(draft["python"])
        manifest = {"name": draft["name"].replace("-", "_"), "description": draft["description"],
                    "inputSchema": json.loads(draft["schema"])}
        properties = manifest["inputSchema"]["properties"]
        assert len(properties) == 1, draft
        key, spec = next(iter(properties.items()))
        assert spec["type"] == "number" and "celsius" in key, draft
        outputs = [mcp_roundtrip(manifest, draft["python"], {key: number}) for number in (25, -40)]
        def fahrenheit(output):
            try:
                data = json.loads(output)
                if isinstance(data, dict):
                    return float(data["fahrenheit"])
                if isinstance(data, (int, float)):
                    return float(data)
            except (ValueError, KeyError, TypeError):
                pass
            values = re.findall(r"(?<!\w)-?\d+(?:\.\d+)?", output)
            if not values or "error" in output.casefold():
                raise AssertionError((output, draft))
            return float(values[-1])
        assert [fahrenheit(output) for output in outputs] == [77, -40], (outputs, draft)
        print(json.dumps({"skill_created": skill["name"], "skill_seconds": skill_time,
                          "mcp_created": draft["name"], "mcp_seconds": draft_time,
                          "mcp_outputs": outputs, "device": chat.device}))
    finally:
        chat.close()


if __name__ == "__main__":
    main()
