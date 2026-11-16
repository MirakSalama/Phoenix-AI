using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NpuChat;

internal static partial class ToolAuthoring
{
    [GeneratedRegex(@"^(?:please\s+|(?:can|could|would) you\s+|i want you to\s+)*(?:create|write|build|make|generate|implement)\s+(?:me\s+)?(?:an?\s+)?(?:new\s+)?(?<kind>skill|mcp(?:\s+(?:server|tool))?|tool)\b\s*(?<purpose>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex CreationRequest();

    internal static bool? IsMcpRequest(string text)
    {
        var match = CreationRequest().Match(text);
        return match.Success ? !match.Groups["kind"].Value.Equals("skill", StringComparison.OrdinalIgnoreCase) : null;
    }

    internal static string Root => Path.Combine(LocalWorkspace.Folder, "Authored");

    internal static string Fingerprint(string source, string schema) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source + "\0" + schema)));

    internal static string Fingerprint(McpServer server) =>
        Fingerprint(File.ReadAllText(server.GeneratedModule!), File.ReadAllText(server.Arguments[^2]));

    internal static JsonElement Schema(params (string Name, object Type)[] fields) =>
        JsonSerializer.SerializeToElement(new
        {
            type = "object", properties = fields.ToDictionary(field => field.Name, field => field.Type),
            required = fields.Select(field => field.Name).ToArray(), additionalProperties = false
        });

    internal static object Text(string description) => new { type = "string", description };

    internal static string Required(JsonElement data, string property, int limit)
    {
        string text = data.GetProperty(property).GetString()?.Trim() ?? "";
        if (text.Length == 0 || text.Length > limit || text.Contains('\0'))
            throw new InvalidOperationException($"The generated {property} is empty or too long.");
        return text;
    }

    internal static string Name(JsonElement data)
    {
        string name = Required(data, "name", 64);
        if (!RepositoryImport.SafePath(name) || !Regex.IsMatch(name, "^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("Use a lowercase name with hyphens between words.");
        return name;
    }

    internal static LocalSkill SaveSkill(JsonElement data, LocalWorkspace workspace, Func<bool> save, string root)
    {
        string name = Name(data);
        string description = Required(data, "description", 1024);
        string instructions = Required(data, "instructions", 16_000);
        if (workspace.Skills.Any(skill => skill.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("A skill with that name already exists.");
        var id = Guid.NewGuid();
        string path = Path.Combine(root, id.ToString("N"), name, "SKILL.md");
        string body = $"---\nname: {name}\ndescription: {JsonSerializer.Serialize(description)}\n---\n\n{instructions}\n";
        var skill = new LocalSkill(id, name, $"{name}, {name.Replace('-', ' ')}", instructions,
            Description: description, SkillFile: path);
        Commit([new(path, body)], workspace.Skills, skill, save);
        return skill;
    }

    internal static async Task<McpServer> SaveMcpAsync(JsonElement data, LocalWorkspace workspace, Func<bool> save,
        string root, string python, string hostScript, string? profile, CancellationToken cancel)
    {
        string name = Name(data);
        string description = Required(data, "description", 1024);
        string source = Required(data, "python", 12_000);
        string schemaText = Required(data, "schema", 8000);
        using var schema = JsonDocument.Parse(schemaText);
        if (schema.RootElement.ValueKind != JsonValueKind.Object ||
            !schema.RootElement.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "object" ||
            !schema.RootElement.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("The tool needs an object schema with properties.");
        if (workspace.Servers.Any(server => server.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("An MCP server with that name already exists.");
        if (!File.Exists(python) || !File.Exists(hostScript))
            throw new InvalidOperationException("Phoenix’s local Python or MCP host is unavailable.");
        await CheckSyntaxAsync(python, source, props.EnumerateObject().Select(property => property.Name).ToArray(), cancel);
        cancel.ThrowIfCancellationRequested();
        if (workspace.Servers.Any(server => server.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("An MCP server with that name already exists.");
        Guid id = Guid.NewGuid();
        string folder = Path.Combine(root, id.ToString("N"), name);
        string module = Path.Combine(folder, "handler.py");
        string manifest = Path.Combine(folder, "tool.json");
        string config = JsonSerializer.Serialize(new
        {
            name = name.Replace('-', '_'), description, inputSchema = schema.RootElement
        }, new JsonSerializerOptions { WriteIndented = true });
        var server = new McpServer(id, name, python, ["-I", "-B", hostScript, manifest, module],
            $"{name}, {name.Replace('-', ' ')}", LanProfileKey: profile, Enabled: false, GeneratedModule: module,
            Description: description);
        Commit([new(module, source + "\n"), new(manifest, config + "\n")], workspace.Servers, server, save);
        return server;
    }

    private static async Task CheckSyntaxAsync(string python, string source, string[] keys, CancellationToken cancel)
    {
        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        foreach (var arg in new[] { "-I", "-B", "-X", "utf8", "-c",
            """
            import ast, json, sys
            data = json.load(sys.stdin)
            tree = ast.parse(data["source"])
            functions = [node for node in tree.body if isinstance(node, ast.FunctionDef) and node.name == "handle"]
            if len(functions) != 1 or len(functions[0].args.posonlyargs + functions[0].args.args) != 1:
                sys.exit("Define a single handle(arguments) function with one positional parameter.")
            function = functions[0]
            parameter = (function.args.posonlyargs + function.args.args)[0].arg
            for node in ast.walk(function):
                key = None
                if isinstance(node, ast.Subscript) and isinstance(node.value, ast.Name) and node.value.id == parameter:
                    key = node.slice
                elif (isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute) and
                      isinstance(node.func.value, ast.Name) and node.func.value.id == parameter and
                      node.func.attr in ("get", "pop", "setdefault") and node.args):
                    key = node.args[0]
                if isinstance(key, ast.Constant) and key.value not in data["keys"]:
                    sys.exit(f"Argument {key.value!r} is not a declared schema property. The input is a dict.")
            """ })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Python syntax checker did not start.");
        var errors = process.StandardError.ReadToEndAsync(cancel);
        try
        {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { source, keys }).AsMemory(), cancel);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancel);
            if (process.ExitCode != 0)
                throw new InvalidOperationException("The generated Python must parse and define handle(arguments). " +
                    LocalWorkspace.Limit((await errors).Split('\n').LastOrDefault(line => line.Trim().Length > 0) ?? "", 200));
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
    }

    private static void Commit<T>(KeyValuePair<string, string>[] files, List<T> entries, T entry, Func<bool> save)
    {
        var created = new List<string>();
        try
        {
            foreach (var (path, text) in files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                created.Add(path);
                file.Write(Encoding.UTF8.GetBytes(text));
            }
            entries.Add(entry);
            if (!save()) throw new IOException("Phoenix couldn’t save the new entry.");
        }
        catch
        {
            entries.Remove(entry);
            foreach (string path in created)
                try { File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}
