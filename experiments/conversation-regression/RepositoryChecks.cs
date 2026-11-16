using System.Net;
using System.Text;
using System.Text.Json;
using NpuChat;

internal static class RepositoryChecks
{
    internal static async Task RunAsync(string python, string clock, string host)
    {
        using var http = new HttpClient(new GitHubFixture(python, clock));
        var request = RepositoryImport.Parse("Scan https://github.com/example/field-tools and install its skills and MCPs.");
        Check(request is { Install: true }, "Chat recognizes the repository import.");
        Check(RepositoryImport.ParseDiscovery("Find a Chrome MCP server on GitHub.") is not null &&
            RepositoryImport.ParseDiscovery("Find a Chrome MCP offline only.") is null, "Discovery respects an explicit local scope.");
        Check(RepositoryImport.ParseSelection("install the second one", ["https://github.com/example/first", request!.Url]) == request,
            "A numbered choice resolves the current chat's selected repository.");
        var scan = await new RepositoryImport(http).ScanAsync(request!.Url, default);
        Check(scan.Skills.Count == 1 && scan.Servers.Count == 1 && scan.Findings.Any(item => item.Kind == "Plugin") &&
            scan.Findings.Any(item => item.Kind == "Instructions"), "Scan recognizes skills, MCP, instructions, and plugins.");
        Check(!scan.Files.ContainsKey("../escape.txt") && !RepositoryImport.SafePath("assets/CON.txt"), "Unsafe paths excluded.");
        var directory = Directory.CreateTempSubdirectory("phoenix-repo-check-");
        var local = new LocalWorkspace();
        try
        {
            try { RepositoryInstallation.Save(local, scan, directory.FullName, () => false); }
            catch (IOException) { }
            Check(local.Imports.Count == 0 && local.Skills.Count == 0 && local.Servers.Count == 0,
                "A failed save rolls back the entire import.");
            string saved = RepositoryInstallation.Save(local, scan, directory.FullName, () => true);
            Check(saved.Contains("1 skills") && local.Skills.Single().Name == "field-report", "Skill installed.");
            var receipt = local.Imports.Single();
            Check(RepositoryInstallation.ReadText(directory.FullName, receipt.Id,
                ".opencode/skills/field-report/references/format.md") == "Use a numbered list.", "References retained.");
            Check(!local.VisibleServers(null).Any(), "Imported server is disabled before review.");
            RepositoryInstallation.Save(local, scan, directory.FullName, () => true);
            Check(local.Imports.Count == 1 && local.Skills.Count == 1, "Repeated import is idempotent.");
            var server = local.Servers.Single() with { Enabled = true };
            Check(server.WorkingDirectory is not null && File.Exists(server.Arguments.Single()) &&
                Path.GetFullPath(server.Arguments.Single()).StartsWith(directory.FullName, StringComparison.OrdinalIgnoreCase),
                "Imported server source is acquired and the command points to the saved file.");
            var tool = (await McpClient.ListAsync(server, default)).Single();
            var events = new List<ToolActivity>();
            NativeTool[] tools =
            [
                new("load_skill", "Load the imported field-report skill", ToolAuthoring.Schema(("name", ToolAuthoring.Text("Name"))),
                    (_, _) => Task.FromResult(new NativeToolResult("Loaded", local.Skills.Single().Instructions))),
                new(tool.Name, tool.Description, tool.InputSchema, async (data, cancel) =>
                    new("Completed", await McpClient.CallAsync(server, new(tool.Name, data), cancel)))
            ];
            int step = 0;
            var result = await NativeToolLoop.RunAsync([], tools, (messages, catalog, _) =>
            {
                Check(catalog.Length == 2, "Native model sees the original schemas.");
                if (step++ == 0) return Task.FromResult(Call("a", "load_skill", """{"name":"field-report"}"""));
                var last = JsonSerializer.SerializeToElement(messages[^1]);
                Check(last.GetProperty("role").GetString() == "tool", "Each result is a tool message.");
                if (step == 2)
                {
                    Check(last.GetProperty("content").GetString()!.Contains("clock"), "Imported skill actually loaded.");
                    return Task.FromResult(Call("b", "local_time", "{}"));
                }
                string time = last.GetProperty("content").GetString()!;
                Check(DateTimeOffset.TryParse(time, out DateTimeOffset _), "Real MCP result returned.");
                return Task.FromResult(JsonSerializer.SerializeToElement(new { message = new { role = "assistant", content = time } }));
            }, events.Add, default);
            Check(events.Select(item => item.Status).SequenceEqual(new[] { "Loaded", "Completed" }), "Activity records both real steps.");
            Check(result.GetProperty("message").GetProperty("content").GetString()!.Length > 0, "Tool round trip completed.");
            int rejectedStep = 0;
            await NativeToolLoop.RunAsync([], tools, (messages, _, _) =>
                Task.FromResult(rejectedStep++ == 0 ? Call("z", "local_time", """{"unknown":2}""") :
                    JsonSerializer.SerializeToElement(new { message = new { role = "assistant", content = "No result" } })), events.Add, default);
            Check(events[^1].Status == "Rejected", "Invalid arguments were rejected before execution.");

            var skill = ToolAuthoring.SaveSkill(JsonSerializer.SerializeToElement(new
            { name = "draft-report", description = "Write a report", instructions = "Collect the facts. Then write a report." }),
                local, () => true, directory.FullName);
            Check(skill.SkillFile is { } path && RepositoryImport.ReadSkill("draft-report/SKILL.md", File.ReadAllText(path)).Name == skill.Name,
                "Authoring creates a standard SKILL.md.");
            var draft = await ToolAuthoring.SaveMcpAsync(JsonSerializer.SerializeToElement(new
            {
                name = "created-adder", description = "Add one to a number",
                schema = """{"type":"object","properties":{"x":{"type":"number"}},"required":["x"],"additionalProperties":false}""",
                python = "def handle(arguments):\n    return {'result': arguments['x'] + 1}"
            }), local, () => true, directory.FullName, python, host, "test-profile", default);
            Check(!draft.Enabled && draft.GeneratedModule is { } source && File.Exists(source), "A real MCP handler was saved disabled.");
            var enabled = draft with { Enabled = true, ReviewedFingerprint = ToolAuthoring.Fingerprint(draft) };
            local.Servers[local.Servers.IndexOf(draft)] = enabled;
            Check(!local.VisibleServers("other-profile").Contains(enabled) && local.VisibleServers("test-profile").Contains(enabled),
                "Tool is visible only to its LAN profile.");
            var answer = await McpClient.CallAsync(enabled, new("created_adder", JsonSerializer.SerializeToElement(new { x = 8 })), default);
            Check(JsonDocument.Parse(answer).RootElement.GetProperty("result").GetInt32() == 9, "Authored MCP handler runs through the app client.");
            Console.WriteLine("Repository scan, import rollback, skill references, MCP invocation, and authored artifact checks passed.");
        }
        finally
        {
            string absolute = Path.GetFullPath(directory.FullName);
            if (Path.GetDirectoryName(absolute) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) &&
                Path.GetFileName(absolute).StartsWith("phoenix-repo-check-", StringComparison.Ordinal))
                directory.Delete(recursive: true);
        }
    }

    private static JsonElement Call(string id, string name, string arguments) => JsonSerializer.SerializeToElement(new
    {
        message = new { role = "assistant", tool_calls = new[] { new { id, function = new { name, arguments } } } }
    });

    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }

    private sealed class GitHubFixture(string python, string clock) : HttpMessageHandler
    {
        private const string Commit = "1234567890123456789012345678901234567890";
        private const string Tree = "2345678901234567890123456789012345678901";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var files = new Dictionary<string, string>
            {
                ["opencode.jsonc"] = "/* OpenCode */ " + JsonSerializer.Serialize(new
                {
                    mcp = new { servers = new Dictionary<string, object> { ["local-clock"] = new { type = "local", command = new[] { python, "mcp/server.py" } } } },
                    plugins = new[] { "example-plugin" }
                }),
                ["AGENTS.md"] = "Instructions for the source workspace.",
                [".opencode/skills/field-report/SKILL.md"] = "---\nname: field-report\ndescription: >-\n  Write a field report with the local clock.\n---\n\nUse the local clock and references/format.md.",
                [".opencode/skills/field-report/references/format.md"] = "Use a numbered list.",
                ["mcp/server.py"] = File.ReadAllText(clock),
                ["../escape.txt"] = "Must not be read."
            };
            var url = request.RequestUri!;
            string result = url.AbsolutePath.EndsWith("/commits/HEAD") ? JsonSerializer.Serialize(new { sha = Commit, commit = new { tree = new { sha = Tree } } }) :
                url.AbsolutePath.EndsWith($"/git/trees/{Tree}") ? JsonSerializer.Serialize(new
                {
                    truncated = false,
                    tree = files.Select(pair => new { path = pair.Key, size = Encoding.UTF8.GetByteCount(pair.Value), mode = "100644" })
                }) : url.Host == "raw.githubusercontent.com" && url.AbsolutePath.StartsWith($"/example/field-tools/{Commit}/") ?
                files[Uri.UnescapeDataString(url.AbsolutePath[$"/example/field-tools/{Commit}/".Length..])] :
                throw new InvalidOperationException("Unexpected GitHub request.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(result)) });
        }
    }
}
