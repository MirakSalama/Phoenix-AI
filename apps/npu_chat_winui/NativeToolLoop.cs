using System.Text.Json;
using System.Text.Json.Nodes;

namespace NpuChat;

internal sealed record NativeTool(string Name, string Description, JsonElement Schema,
    Func<JsonElement, CancellationToken, Task<NativeToolResult>> Run);
internal sealed record NativeToolResult(string Status, string Text);
internal sealed record ToolActivity(string Name, string Status, string? Detail = null);

internal static class NativeToolLoop
{
    internal static async Task<JsonElement> RunAsync(List<object> messages, IReadOnlyList<NativeTool> tools,
        Func<IReadOnlyList<object>, object[], CancellationToken, Task<JsonElement>> reply,
        Action<ToolActivity> activity, CancellationToken cancel)
    {
        var available = tools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
        object[] catalog = tools.Select(tool => (object)new
        {
            type = "function",
            function = new { name = tool.Name, description = tool.Description, parameters = tool.Schema }
        }).ToArray();
        int calls = 0;
        const int maxCalls = 4;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            var result = await reply(messages, calls < maxCalls ? catalog : [], cancel);
            var answer = result.GetProperty("message");
            if (!answer.TryGetProperty("tool_calls", out var requested) ||
                requested.ValueKind != JsonValueKind.Array || requested.GetArrayLength() == 0)
                return result;
            if (calls >= maxCalls || requested.GetArrayLength() > maxCalls)
                throw new InvalidOperationException("The worker exceeded the tool call limit.");
            messages.Add(answer.Clone());
            foreach (var call in requested.EnumerateArray())
            {
                cancel.ThrowIfCancellationRequested();
                var function = call.GetProperty("function");
                string id = call.GetProperty("id").GetString() ?? "";
                string name = function.GetProperty("name").GetString() ?? "";
                string raw = function.GetProperty("arguments").GetString() ?? "";
                if (id.Length == 0 || !seen.Add(id))
                    throw new InvalidOperationException("The worker returned a duplicate or missing tool call ID.");
                NativeToolResult output;
                if (++calls > maxCalls || !available.TryGetValue(name, out var tool))
                    output = new("Rejected", "This tool is not available for this turn.");
                else if (!TryArguments(raw, tool.Schema, out var arguments))
                    output = new("Rejected", "Arguments did not match the tool schema. Check required fields and types.");
                else
                    output = await tool.Run(arguments, cancel);
                string? detail = output.Status is "Rejected" or "Failed" or "Unavailable" ? LocalWorkspace.Limit(output.Text, 230) : null;
                activity(new(name, output.Status, detail));
                messages.Add(new { role = "tool", tool_call_id = id, content = LocalWorkspace.Limit(output.Text, 3800) });
            }
        }
    }

    private static bool TryArguments(string json, JsonElement schema, out JsonElement arguments)
    {
        arguments = default;
        if (json.Length > 8000) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            var value = document.RootElement;
            if (value.ValueKind != JsonValueKind.Object || !MatchesSchema(value, schema)) return false;
            arguments = value.Clone();
            return true;
        }
        catch (JsonException) { return false; }
    }

    // Check the common argument shapes here. The MCP server validates its full schema at execution.
    private static bool MatchesSchema(JsonElement value, JsonElement schema)
    {
        if (schema.ValueKind == JsonValueKind.False) return false;
        if (schema.ValueKind != JsonValueKind.Object) return true;
        if (schema.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String &&
            !MatchesType(value, type.GetString())) return false;
        if (schema.TryGetProperty("enum", out var choices) && choices.ValueKind == JsonValueKind.Array &&
            !choices.EnumerateArray().Any(choice => JsonNode.DeepEquals(JsonNode.Parse(value.GetRawText()),
                JsonNode.Parse(choice.GetRawText())))) return false;
        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var itemSchema))
            return value.EnumerateArray().All(item => MatchesSchema(item, itemSchema));
        if (value.ValueKind != JsonValueKind.Object) return true;
        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array &&
            required.EnumerateArray().Any(key => key.ValueKind == JsonValueKind.String && !value.TryGetProperty(key.GetString()!, out _)))
            return false;
        bool properties = schema.TryGetProperty("properties", out var fields) && fields.ValueKind == JsonValueKind.Object;
        bool additional = schema.TryGetProperty("additionalProperties", out var extras);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
        {
            if (!seen.Add(field.Name)) return false;
            if (properties && fields.TryGetProperty(field.Name, out var property))
            {
                if (!MatchesSchema(field.Value, property)) return false;
            }
            else if (additional && !MatchesSchema(field.Value, extras)) return false;
        }
        return true;
    }

    private static bool MatchesType(JsonElement value, string? type) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "number" => value.ValueKind == JsonValueKind.Number,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true
    };
}
