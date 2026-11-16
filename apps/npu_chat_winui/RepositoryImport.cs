using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NpuChat;

internal sealed record RepoSource(string Repository, string Commit, string Path);
internal sealed record RepoFinding(string Kind, string Name, string Path, string Status);
internal sealed record RepoInstallation(Guid Id, string Repository, string Commit, RepoFinding[] Findings);
internal sealed record RepoSkill(string Name, string Description, string Instructions, string Path);
internal sealed record RepoServer(string Name, string Path, string[] Command, string? Url, bool Enabled, bool NeedsSetup,
    string? LocalEntry = null, int EntryArgument = -1, string? Description = null);
internal sealed record RepoScan(string Repository, string Commit, IReadOnlyList<RepoSkill> Skills,
    IReadOnlyList<RepoServer> Servers, IReadOnlyDictionary<string, byte[]> Files, IReadOnlyList<RepoFinding> Findings);
internal sealed record RepoImportRequest(string Url, bool Install);
internal sealed record RepoDiscoveryRequest(string Terms);
internal sealed record RepoMatch(string Repository, string Description, int Stars);

internal sealed partial class RepositoryImport
{
    private const int MaxFiles = 80;
    private const int MaxAssetBytes = 256 * 1024;
    private const int MaxTotalBytes = 4 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly HttpClient Shared = new(new HttpClientHandler { AllowAutoRedirect = false });
    private readonly HttpClient http;

    internal RepositoryImport(HttpClient? http = null) => this.http = http ?? Shared;

    [GeneratedRegex(@"https://github\.com/[^\s<>""`]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GitHubLink();
    [GeneratedRegex(@"\b(?:scan|inspect|import|install|find|discover)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ScanIntent();
    [GeneratedRegex(@"\b(?:import|install)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InstallIntent();
    [GeneratedRegex(@"^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SkillName();
    [GeneratedRegex(@"^[a-zA-Z0-9_.@ +()\-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeSegment();
    [GeneratedRegex(@"^[a-f0-9]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex GitSha();
    [GeneratedRegex(@"^(?:(?:please|can you|could you|would you|i want you to)\s+)*(?:search|look|find|discover|install|import)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DiscoveryIntent();
    [GeneratedRegex(@"(?:^|[.!?]\s+)(?:(?:so|then),?\s+)?(?:(?:please|can you|could you|i want you to)\s+)*go (?:to|on) github\s+and\s+(?:get|find|look for|search for|discover)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GoToGitHub();
    [GeneratedRegex(@"\b(?:mcp|skills?|plugins?|rag|documentation|knowledge base)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArtifactIntent();
    [GeneratedRegex(@"\b(?:github|online|open[- ]source|repositor(?:y|ies)|repo|install|import)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExternalSource();
    [GeneratedRegex(@"^\s*(?:(?:please|can you|could you)\s+)*(?<verb>scan|inspect|import|install)\s+(?:the\s+)?(?:repo(?:sitory)?\s+|option\s+|number\s+|#\s*)?(?<which>[1-5]|first|second|third|fourth|fifth|it)(?:\s+one)?[.!?]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PickIntent();
    [GeneratedRegex(@"(?im)^[ ]{0,3}`{3,}jsonc?[ \t]*\r?\n(?<body>[\s\S]*?)\r?\n[ ]{0,3}`{3,}[ \t]*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex JsonExamples();

    internal static RepoDiscoveryRequest? ParseDiscovery(string text)
    {
        var github = GoToGitHub().Match(text);
        string request = github.Success ? text[(github.Index + github.Length)..] : text;
        if (!ArtifactIntent().IsMatch(request) || !(github.Success || DiscoveryIntent().IsMatch(text)) || !ExternalSource().IsMatch(text) ||
            GitHubLink().IsMatch(text) || CloudResearch.IsOfflineRequest(text)) return null;
        if (github.Success) request = request.Split(['.', '!', '?'], 2)[0];
        var words = Regex.Matches(request, @"[\p{L}][\p{L}\p{N}._-]*")
            .Select(match => match.Value.TrimEnd('.', '-'))
            .Where(word => !SearchStopWords.Contains(word))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(8);
        string terms = string.Join(' ', words);
        return terms.Length is > 0 and <= 200 ? new(terms) : null;
    }

    private static readonly HashSet<string> SearchStopWords = new(
        "please can could would you i want to find search look discover me an a the and or on for from that which with github online open-source opensource repo repository repositories install import ready existing usable working phoenix actual server servers tool tools taking best top get go".Split(' '),
        StringComparer.OrdinalIgnoreCase);

    internal static RepoImportRequest? ParseSelection(string text, IReadOnlyList<string>? options)
    {
        var match = PickIntent().Match(text);
        if (!match.Success || options is not { Count: > 0 }) return null;
        string which = match.Groups["which"].Value.ToLowerInvariant();
        int index = which switch
        {
            "first" => 0, "second" => 1, "third" => 2, "fourth" => 3, "fifth" => 4,
            "it" when options.Count == 1 => 0, "it" => -1, _ => which[0] - '1'
        };
        if (index < 0 || index >= options.Count) return null;
        string verb = match.Groups["verb"].Value.ToLowerInvariant();
        return new(options[index], verb is "import" or "install");
    }

    internal async Task<RepoMatch[]> SearchAsync(RepoDiscoveryRequest request, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        string query = Uri.EscapeDataString(request.Terms + " archived:false fork:false is:public");
        using var json = JsonDocument.Parse(await ReadAsync(
            $"https://api.github.com/search/repositories?q={query}&per_page=5", 512 * 1024, deadline.Token));
        return json.RootElement.GetProperty("items").EnumerateArray().Where(item =>
                ReadString(item, "full_name") is { } name && name.Split('/') is [var owner, var repo] &&
                SafeSegment().IsMatch(owner) && SafeSegment().IsMatch(repo))
            .Take(5).Select(item => new RepoMatch(ReadString(item, "full_name")!,
                LocalWorkspace.Limit(ReadString(item, "description")?.Replace('\n', ' ').Replace('\r', ' ') ?? "", 170),
                item.GetProperty("stargazers_count").GetInt32())).ToArray();
    }

    internal static RepoImportRequest? Parse(string text)
    {
        var link = GitHubLink().Match(text);
        if (!link.Success || !ScanIntent().IsMatch(text[..link.Index] + text[(link.Index + link.Length)..]))
            return null;
        var intent = text[..link.Index] + text[(link.Index + link.Length)..];
        return new(link.Value.TrimEnd('.', ',', ')', ']', '!', '?'), InstallIntent().IsMatch(intent));
    }

    private static (string Repository, string Ref) ParseUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0)
            throw new InvalidOperationException("Enter a public GitHub repository URL.");
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length < 2 || parts.Take(2).Any(p => !SafeSegment().IsMatch(p) || p is "." or ".."))
            throw new InvalidOperationException("Use a GitHub URL with an owner and repository name.");
        string repo = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        if (repo.Length == 0) throw new InvalidOperationException("The GitHub repository name is missing.");
        string revision = parts.Length == 2 ? "HEAD" :
            parts.Length >= 4 && parts[2] is "tree" or "commit" ? Uri.UnescapeDataString(string.Join('/', parts.Skip(3))) :
            throw new InvalidOperationException("Use the repository URL, a branch URL, or a commit URL.");
        if (revision.Length is 0 or > 220 || revision.Any(char.IsControl))
            throw new InvalidOperationException("The GitHub revision is invalid.");
        return ($"{parts[0]}/{repo}", revision);
    }

    internal async Task<RepoScan> ScanAsync(string url, CancellationToken cancellation)
    {
        var (repository, revision) = ParseUrl(url);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(65));
        var token = deadline.Token;
        string api = $"https://api.github.com/repos/{repository}";
        using var commit = JsonDocument.Parse(await ReadAsync(
            $"{api}/commits/{Uri.EscapeDataString(revision)}", 1_048_576, token));
        string sha = commit.RootElement.GetProperty("sha").GetString() ?? "";
        string treeSha = commit.RootElement.GetProperty("commit").GetProperty("tree").GetProperty("sha").GetString() ?? "";
        if (!GitSha().IsMatch(sha) || !GitSha().IsMatch(treeSha))
            throw new InvalidOperationException("GitHub returned an invalid commit or tree identifier.");
        using var tree = JsonDocument.Parse(await ReadAsync($"{api}/git/trees/{treeSha}?recursive=1", 8_000_000, token));
        if (tree.RootElement.GetProperty("truncated").GetBoolean())
            throw new InvalidOperationException("GitHub truncated this repository’s file list. Scan a smaller repository or branch.");
        var entries = tree.RootElement.GetProperty("tree").EnumerateArray()
            .Where(item => item.GetProperty("mode").GetString() is "100644" or "100755")
            .Select(item => (Path: item.GetProperty("path").GetString() ?? "", Size: item.GetProperty("size").GetInt64()))
            .Where(item => SafePath(item.Path))
            .ToDictionary(item => item.Path, item => item.Size, StringComparer.Ordinal);

        var skillPaths = entries.Keys.Where(path => FileName(path) == "SKILL.md").Order(StringComparer.Ordinal).ToArray();
        var configPaths = entries.Keys.Where(IsConfig).Order(StringComparer.Ordinal).ToArray();
        var instructionPaths = entries.Keys.Where(path => FileName(path) is "AGENTS.md" or "CLAUDE.md" ||
            path is "README.md" or "CONTRIBUTING.md" or ".github/copilot-instructions.md")
            .OrderBy(path => path.Count(c => c == '/')).ThenBy(path => path, StringComparer.Ordinal).ToArray();
        var pluginPaths = entries.Keys.Where(path => path.StartsWith(".opencode/plugins/", StringComparison.Ordinal) &&
            Path.GetExtension(path) is ".js" or ".ts" or ".mjs" || FileName(path) == "plugin.json")
            .Order(StringComparer.Ordinal).ToArray();
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var findings = new List<RepoFinding>();
        var skills = new List<RepoSkill>();
        var servers = new List<RepoServer>();
        long totalBytes = 0;

        async Task<byte[]?> Load(string path)
        {
            if (files.TryGetValue(path, out var existing)) return existing;
            long length = entries[path];
            if (length < 0 || length > MaxAssetBytes || files.Count >= MaxFiles || totalBytes + length > MaxTotalBytes)
            {
                findings.Add(new("File", FileName(path), path, "Skipped because of import size limits"));
                return null;
            }
            string rawPath = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
            var bytes = await ReadAsync($"https://raw.githubusercontent.com/{repository}/{sha}/{rawPath}",
                MaxAssetBytes, token);
            if (bytes.Length != length)
                throw new InvalidOperationException($"GitHub returned incomplete content for {path}.");
            totalBytes += bytes.Length;
            files.Add(path, bytes);
            return bytes;
        }

        foreach (var path in skillPaths)
        {
            var bytes = await Load(path);
            if (bytes is null) continue;
            try
            {
                var skill = ReadSkill(path, Utf8.GetString(bytes));
                skills.Add(skill);
                findings.Add(new("Skill", skill.Name, path, "Available to import"));
            }
            catch (Exception error) when (error is InvalidOperationException or DecoderFallbackException)
            {
                findings.Add(new("Skill", FileName(path), path, "Invalid or unsupported SKILL.md"));
            }
        }
        foreach (var path in configPaths)
        {
            var bytes = await Load(path);
            if (bytes is null) continue;
            try { ReadConfig(path, bytes, servers, findings); }
            catch (JsonException) { findings.Add(new("Configuration", FileName(path), path, "Invalid JSON or JSONC")); }
        }
        foreach (var path in instructionPaths.Take(12))
        {
            var bytes = await Load(path);
            if (bytes is null) continue;
            foreach (Match block in JsonExamples().Matches(Encoding.UTF8.GetString(bytes)).Take(18))
            {
                string example = block.Groups["body"].Value;
                if (!example.Contains("\"mcpServers\"", StringComparison.Ordinal) &&
                    !example.Contains("\"mcp\"", StringComparison.Ordinal)) continue;
                try { ReadConfig(path, Encoding.UTF8.GetBytes(example), servers, findings); }
                catch (JsonException) { }
            }
        }
        for (int i = 0; i < servers.Count; i++)
        {
            var server = servers[i];
            var (entry, argument) = LocalServerEntry(server, entries);
            if (entry is null || await Load(entry) is null) continue;
            string folder = Parent(entry);
            foreach (var path in entries.Keys.Where(path =>
                folder.Length > 0 && path.StartsWith(folder + "/", StringComparison.Ordinal) && IsServerSource(path) ||
                Parent(path) == folder && FileName(path) is "package.json" or "package-lock.json" or "pyproject.toml" or "requirements.txt")
                .Order(StringComparer.Ordinal))
                await Load(path);
            servers[i] = server with { LocalEntry = entry, EntryArgument = argument };
            int index = findings.FindIndex(item => item.Kind == "MCP server" && item.Name == server.Name && item.Path == server.Path);
            if (index >= 0 && server.Enabled) findings[index] = findings[index] with { Status = "Source found · Review before enabling" };
        }
        foreach (var path in instructionPaths)
        {
            if (await Load(path) is not null)
                findings.Add(new("Instructions", FileName(path), path, "Available as reference"));
        }
        foreach (var path in pluginPaths)
            findings.Add(new("Plugin", FileName(path), path, "Requires a compatible plugin host"));
        foreach (var skill in skills)
        {
            string folder = Parent(skill.Path);
            foreach (var path in entries.Keys.Where(path => folder.Length == 0 ? IsAssetFolder(path) : IsSkillAsset(folder, path))
                .Order(StringComparer.Ordinal))
                await Load(path);
        }
        return new(repository, sha, skills, servers, files, findings);
    }

    private static bool IsSkillAsset(string folder, string path)
    {
        if (!path.StartsWith(folder + "/", StringComparison.Ordinal)) return false;
        string relative = path[(folder.Length + 1)..];
        return !relative.Contains('/') || IsAssetFolder(relative);
    }

    private static bool IsAssetFolder(string path) => path.StartsWith("references/", StringComparison.Ordinal) ||
        path.StartsWith("scripts/", StringComparison.Ordinal) || path.StartsWith("assets/", StringComparison.Ordinal);

    private static bool IsServerSource(string path) => Path.GetExtension(path) is ".py" or ".js" or ".mjs" or ".cjs" or
        ".ts" or ".json" or ".toml" or ".txt" or ".md";

    private static (string? Path, int Argument) LocalServerEntry(RepoServer server, IReadOnlyDictionary<string, long> entries)
    {
        if (server.Url is not null || server.Command.Length < 2) return (null, -1);
        string launcher = Path.GetFileNameWithoutExtension(server.Command[0]).ToLowerInvariant();
        if (launcher is not ("python" or "python3" or "node")) return (null, -1);
        for (int index = 1; index < server.Command.Length; index++)
        {
            string relative = server.Command[index].Replace('\\', '/');
            if (relative.StartsWith("./", StringComparison.Ordinal)) relative = relative[2..];
            if (!SafePath(relative) || Path.GetExtension(relative) is not (".py" or ".js" or ".mjs" or ".cjs")) continue;
            if (entries.ContainsKey(relative)) return (relative, index - 1);
            string path = $"{Parent(server.Path)}/{relative}";
            if (entries.ContainsKey(path)) return (path, index - 1);
        }
        return (null, -1);
    }

    private static bool IsConfig(string path) => path is "opencode.json" or "opencode.jsonc" or ".mcp.json" or "mcp.json"
        or ".opencode/opencode.json" or ".opencode/opencode.jsonc" or ".vscode/mcp.json";

    internal static RepoSkill ReadSkill(string path, string source)
    {
        // A YAML frontmatter reader supports quoted strings and block descriptions without executing YAML tags.
        string normalized = source.TrimStart('\uFEFF').Replace("\r\n", "\n");
        int end = normalized.StartsWith("---\n", StringComparison.Ordinal)
            ? normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal) : -1;
        if (end < 0)
            throw new InvalidOperationException("SKILL.md needs YAML frontmatter.");
        var lines = normalized[4..end].Split('\n');
        string Read(string key)
        {
            int index = Array.FindIndex(lines, line => line.StartsWith(key + ":", StringComparison.Ordinal));
            if (index < 0) return "";
            string value = lines[index][(key.Length + 1)..].Trim();
            if (value is "|" or "|-" or "|+" or ">" or ">-" or ">+")
                return string.Join(value[0] == '|' ? "\n" : " ", lines.Skip(index + 1)
                    .TakeWhile(line => line.Length == 0 || char.IsWhiteSpace(line[0])).Select(line => line.Trim())).Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                try { return JsonSerializer.Deserialize<string>(value) ?? ""; }
                catch (JsonException) { throw new InvalidOperationException("Unsupported quoted skill metadata."); }
            }
            if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
                return value[1..^1].Replace("''", "'");
            int comment = value.IndexOf(" #", StringComparison.Ordinal);
            return comment < 0 ? value : value[..comment].TrimEnd();
        }
        string name = Read("name");
        string description = Read("description");
        string instructions = normalized[(end + 5)..].Trim();
        if (name.Length is 0 or > 64 || !SkillName().IsMatch(name) ||
            Parent(path).Length > 0 && FileName(Parent(path)) != name ||
            description.Length is 0 or > 1024 || instructions.Length == 0)
            throw new InvalidOperationException("The skill needs a valid name, description, and instructions.");
        return new(name, description, instructions, path);
    }

    private static void ReadConfig(string path, byte[] bytes, List<RepoServer> servers, List<RepoFinding> findings)
    {
        using var document = JsonDocument.Parse(bytes.AsMemory().Span.StartsWith(new byte[] { 239, 187, 191 })
            ? bytes.AsMemory(3) : bytes.AsMemory(), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (string key in new[] { "plugin", "plugins" })
            if (root.TryGetProperty(key, out var plugins) && plugins.ValueKind == JsonValueKind.Array)
                foreach (var plugin in plugins.EnumerateArray())
                {
                    string? name = plugin.ValueKind == JsonValueKind.String ? plugin.GetString() :
                        plugin.ValueKind == JsonValueKind.Array && plugin.GetArrayLength() > 0 &&
                        plugin[0].ValueKind == JsonValueKind.String ? plugin[0].GetString() : null;
                    if (name is { Length: > 0 and <= 240 }) findings.Add(new("Plugin", name, path, "Requires a compatible plugin host"));
                }
        JsonElement catalog = default;
        foreach (string key in new[] { "mcp", "mcpServers", "servers" })
            if (root.TryGetProperty(key, out catalog) && catalog.ValueKind == JsonValueKind.Object) break;
        if (catalog.ValueKind != JsonValueKind.Object) return;
        if (catalog.TryGetProperty("servers", out var nested) && nested.ValueKind == JsonValueKind.Object) catalog = nested;
        foreach (var server in catalog.EnumerateObject().Take(100))
        {
            if (server.Name.Length is 0 or > 80 || server.Value.ValueKind != JsonValueKind.Object) continue;
            if (servers.Any(existing => existing.Name.Equals(server.Name, StringComparison.OrdinalIgnoreCase))) continue;
            var entry = server.Value;
            string[] command = entry.TryGetProperty("command", out var cmd) && cmd.ValueKind == JsonValueKind.String
                ? [cmd.GetString()!, .. ReadStrings(entry, "args")] : ReadStrings(entry, "command");
            string? url = ReadString(entry, "url");
            bool enabled = !(entry.TryGetProperty("enabled", out var on) && on.ValueKind == JsonValueKind.False ||
                entry.TryGetProperty("disabled", out var off) && off.ValueKind == JsonValueKind.True);
            bool needsSetup = command.Length == 0 || entry.TryGetProperty("env", out _) ||
                entry.TryGetProperty("environment", out _) || entry.TryGetProperty("cwd", out _) ||
                command.Any(arg => arg.Contains("{env:", StringComparison.Ordinal) || arg.Contains("${", StringComparison.Ordinal));
            if (command.Length == 0 && url is null) continue;
            servers.Add(new(server.Name, path, command, url, enabled, needsSetup,
                Description: ReadString(entry, "description")));
            findings.Add(new("MCP server", server.Name, path, !enabled ? "Disabled in repository" :
                url is not null ? "Remote endpoint needs setup" : needsSetup ? "Needs local setup" : "Review before enabling"));
        }
    }

    private static string? ReadString(JsonElement root, string key) =>
        root.TryGetProperty(key, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    private static string[] ReadStrings(JsonElement root, string key) =>
        root.TryGetProperty(key, out var element) && element.ValueKind == JsonValueKind.Array &&
        element.GetArrayLength() <= 64 && element.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String)
            ? element.EnumerateArray().Select(item => item.GetString()!).ToArray() : [];

    private async Task<byte[]> ReadAsync(string url, int limit, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("PhoenixAI-RepositoryImport/1.0");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException("GitHub could not find this public repository, revision, or file.");
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException("GitHub declined the request. A public API rate limit may apply.");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit)
            throw new InvalidOperationException("A GitHub response exceeded the import size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var result = new MemoryStream();
        var buffer = new byte[8192];
        int size;
        while ((size = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (result.Length + size > limit) throw new InvalidOperationException("A GitHub response exceeded the import size limit.");
            result.Write(buffer, 0, size);
        }
        return result.ToArray();
    }

    internal static bool SafePath(string path) => path.Length is > 0 and <= 210 && path.Split('/').All(segment =>
        segment.Length is > 0 and <= 100 && SafeSegment().IsMatch(segment) && !segment.EndsWith('.') &&
        !segment.EndsWith(' ') && !IsReserved(segment.Split('.')[0]));

    private static bool IsReserved(string stem)
    {
        string name = stem.ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" ||
            name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) &&
            char.IsAsciiDigit(name[3]);
    }

    internal static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];
    internal static string Parent(string path) => path[..Math.Max(0, path.LastIndexOf('/'))];
}
