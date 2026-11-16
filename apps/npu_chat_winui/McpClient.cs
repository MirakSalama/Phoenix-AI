using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NpuChat;

internal sealed record McpTool(string Name, string Description, JsonElement InputSchema);
internal sealed record McpCall(string Name, JsonElement Arguments);

internal sealed partial class McpClient : IDisposable
{
    internal const string CallStart = "[[MCP_CALL";
    internal const string NoText = "(The tool returned no text.)";
    private static readonly string[] Versions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];
    private readonly Process process;
    private int requestId;

    private McpClient(McpServer server)
    {
        if (!server.Enabled) throw new InvalidOperationException("This MCP server is disabled.");
        if (server.GeneratedModule is not null && server.ReviewedFingerprint != ToolAuthoring.Fingerprint(server))
            throw new InvalidOperationException("This draft changed since review. Open its source in Tools and review it again.");
        var start = new ProcessStartInfo(server.Command)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = server.WorkingDirectory ?? Path.GetDirectoryName(server.Command)!
        };
        foreach (string argument in server.Arguments) start.ArgumentList.Add(argument);
        start.Environment.Remove("NPU_CHAT_LOG");
        process = Process.Start(start) ?? throw new InvalidOperationException("The MCP server could not start.");
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
    }

    internal static async Task<IReadOnlyList<McpTool>> ListAsync(McpServer server, CancellationToken cancel)
    {
        using var timeout = Timeout(cancel);
        using var client = new McpClient(server);
        await client.InitializeAsync(timeout.Token);
        return await client.ListAsync(timeout.Token);
    }

    internal static async Task<string> CallAsync(McpServer server, McpCall call, CancellationToken cancel, int maxChars = 600)
    {
        var result = await CallResultAsync(server, call, cancel);
        string text = result.TryGetProperty("content", out var parts) && parts.ValueKind == JsonValueKind.Array
            ? string.Join("\n", parts.EnumerateArray().Where(part => ReadString(part, "type") == "text")
                .Select(part => ReadString(part, "text"))) : "";
        if (string.IsNullOrWhiteSpace(text) && result.TryGetProperty("structuredContent", out var structured))
            text = structured.GetRawText();
        return string.IsNullOrWhiteSpace(text) ? NoText : LocalWorkspace.Limit(text, maxChars);
    }

    internal static async Task<JsonElement> CallResultAsync(McpServer server, McpCall call, CancellationToken cancel)
    {
        using var timeout = Timeout(cancel);
        using var client = new McpClient(server);
        await client.InitializeAsync(timeout.Token);
        if (!(await client.ListAsync(timeout.Token)).Any(tool => tool.Name == call.Name))
            throw new InvalidOperationException("The MCP server no longer exposes that tool.");
        var result = await client.RequestAsync("tools/call", new { name = call.Name, arguments = call.Arguments }, timeout.Token);
        if (result.TryGetProperty("isError", out var failed) && failed.ValueKind == JsonValueKind.True)
            throw new InvalidOperationException("The MCP server reported a tool error.");
        return result;
    }

    private static CancellationTokenSource Timeout(CancellationToken parent)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(parent);
        timeout.CancelAfter(TimeSpan.FromSeconds(35));
        return timeout;
    }

    private async Task InitializeAsync(CancellationToken cancel)
    {
        var result = await RequestAsync("initialize", new
        {
            protocolVersion = Versions[0], capabilities = new { },
            clientInfo = new { name = "Phoenix AI", version = "1.0" }
        }, cancel);
        if (!Versions.Contains(ReadString(result, "protocolVersion")))
            throw new InvalidOperationException("The MCP server negotiated an unsupported protocol version.");
        if (!result.TryGetProperty("capabilities", out var capabilities) ||
            !capabilities.TryGetProperty("tools", out _))
            throw new InvalidOperationException("This MCP server does not expose tools.");
        await WriteAsync(new { jsonrpc = "2.0", method = "notifications/initialized" }, cancel);
    }

    private async Task<List<McpTool>> ListAsync(CancellationToken cancel)
    {
        var tools = new List<McpTool>();
        string? cursor = null;
        for (int page = 0; page < 10; page++)
        {
            object parameters = cursor is null ? new { } : new { cursor };
            var result = await RequestAsync("tools/list", parameters, cancel);
            foreach (var tool in result.GetProperty("tools").EnumerateArray())
                if (ReadString(tool, "name") is { } name && ToolName().IsMatch(name) &&
                    tool.TryGetProperty("inputSchema", out var schema) && schema.ValueKind == JsonValueKind.Object)
                    tools.Add(new McpTool(name, ReadString(tool, "description") ?? "", schema));
            cursor = ReadString(result, "nextCursor");
            if (string.IsNullOrEmpty(cursor)) break;
        }
        return tools;
    }

    private async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken cancel)
    {
        int id = ++requestId;
        await WriteAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, cancel);
        while (await process.StandardOutput.ReadLineAsync(cancel) is { } line)
        {
            if (line.Length > 1_000_000) throw new InvalidOperationException("The MCP response was too large.");
            using var document = JsonDocument.Parse(line);
            var message = document.RootElement;
            if (message.ValueKind != JsonValueKind.Object) continue;
            if (message.TryGetProperty("method", out var serverMethod))
            {
                if (message.TryGetProperty("id", out var serverId))
                    await WriteAsync(serverMethod.GetString() == "ping"
                        ? new { jsonrpc = "2.0", id = serverId, result = new { } }
                        : (object)new { jsonrpc = "2.0", id = serverId, error = new { code = -32601, message = "Unsupported method" } },
                        cancel);
                continue;
            }
            if (!message.TryGetProperty("id", out var responseId) || responseId.ValueKind != JsonValueKind.Number ||
                !responseId.TryGetInt32(out int replyId) || id != replyId) continue;
            if (message.TryGetProperty("error", out var error))
                throw new InvalidOperationException($"The MCP server rejected {method} (code {error.GetProperty("code")}).");
            if (message.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
                return result.Clone();
            throw new InvalidOperationException($"The MCP server returned an invalid {method} response.");
        }
        throw new InvalidOperationException("The MCP server exited before it responded.");
    }

    private async Task WriteAsync(object message, CancellationToken cancel)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), cancel);
        await process.StandardInput.FlushAsync(cancel);
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [GeneratedRegex(@"^[A-Za-z0-9_.-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex ToolName();

    internal static bool TryParseCall(string text, out McpCall? call)
    {
        call = null;
        text = text.Trim();
        if (!text.StartsWith(CallStart + " ", StringComparison.Ordinal) ||
            !text.EndsWith("]]", StringComparison.Ordinal) || text.Length > 4000) return false;
        string body = text[(CallStart.Length + 1)..^2];
        int split = body.IndexOf(' ');
        if (split < 1 || !ToolName().IsMatch(body[..split])) return false;
        try
        {
            using var json = JsonDocument.Parse(body[(split + 1)..]);
            if (json.RootElement.ValueKind != JsonValueKind.Object) return false;
            call = new McpCall(body[..split], json.RootElement.Clone());
            return true;
        }
        catch (JsonException) { return false; }
    }

    public void Dispose()
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            DiagnosticLog.Write("mcp", $"Could not stop MCP server: {error.GetType().Name}");
        }
        process.Dispose();
    }
}
