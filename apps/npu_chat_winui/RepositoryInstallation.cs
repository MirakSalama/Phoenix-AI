using System.Text;

namespace NpuChat;

internal static class RepositoryInstallation
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static string Save(LocalWorkspace workspace, RepoScan scan, string root, Func<bool> save)
    {
        if (workspace.Imports.Any(item => item.Repository.Equals(scan.Repository, StringComparison.OrdinalIgnoreCase) &&
            item.Commit == scan.Commit))
            return $"This commit of {scan.Repository} is already in Tools → Imports.";
        var installationId = Guid.NewGuid();
        var copied = new List<string>();
        var skills = new List<LocalSkill>();
        var servers = new List<McpServer>();
        var findings = scan.Findings.ToList();
        foreach (var skill in scan.Skills)
        {
            if (workspace.Skills.Concat(skills).Any(item => item.Name.Equals(skill.Name, StringComparison.OrdinalIgnoreCase)))
            {
                Mark("Skill", skill.Name, skill.Path, "Name already installed");
                continue;
            }
            var origin = new RepoSource(scan.Repository, scan.Commit, skill.Path);
            skills.Add(new(Guid.NewGuid(), skill.Name, $"{skill.Name}, {skill.Name.Replace('-', ' ')}",
                skill.Instructions, Description: skill.Description, Origin: origin));
            Mark("Skill", skill.Name, skill.Path, "Ready");
        }
        foreach (var server in scan.Servers)
        {
            if (workspace.Servers.Concat(servers).Any(item => item.Name.Equals(server.Name, StringComparison.OrdinalIgnoreCase)))
            {
                Mark("MCP server", server.Name, server.Path, "Name already registered");
                continue;
            }
            var origin = new RepoSource(scan.Repository, scan.Commit, server.Path);
            string[] arguments = server.Command.Skip(1).ToArray();
            string? folder = null;
            if (server.LocalEntry is { } entry && server.EntryArgument >= 0 && server.EntryArgument < arguments.Length)
            {
                string path = AssetPath(root, installationId, entry);
                arguments[server.EntryArgument] = path;
                folder = Path.GetDirectoryName(path);
            }
            servers.Add(new(Guid.NewGuid(), server.Name, server.Command.FirstOrDefault() ?? "",
                arguments, $"{server.Name}, {server.Name.Replace('-', ' ')}", Enabled: false,
                Origin: origin, RemoteUrl: server.Url, NeedsSetup: server.NeedsSetup, WorkingDirectory: folder,
                Description: server.Description ?? scan.Skills.FirstOrDefault(skill => skill.Name == server.Name)?.Description));
            Mark("MCP server", server.Name, server.Path, server.Url is null ? "Review in MCP servers" : "Remote transport pending");
        }

        var receipt = new RepoInstallation(installationId, scan.Repository, scan.Commit, findings.ToArray());
        try
        {
            foreach (var (relative, bytes) in scan.Files)
            {
                string path = AssetPath(root, installationId, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                copied.Add(path);
                output.Write(bytes);
            }
            workspace.Skills.AddRange(skills);
            workspace.Servers.AddRange(servers);
            workspace.Imports.Add(receipt);
            if (!save()) throw new IOException("Phoenix could not save the import.");
        }
        catch
        {
            foreach (var skill in skills) workspace.Skills.Remove(skill);
            foreach (var server in servers) workspace.Servers.Remove(server);
            workspace.Imports.Remove(receipt);
            foreach (string path in copied)
                try { File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            throw;
        }

        string summary = $"Imported {scan.Repository} at {scan.Commit[..12]}. Added {skills.Count} skills and {servers.Count} MCP server entries.";
        return summary + " Open Tools → Imports for instructions and plugin status, or MCP servers to review and enable a server.";

        void Mark(string kind, string name, string path, string status)
        {
            int index = findings.FindIndex(item => item.Kind == kind && item.Name == name && item.Path == path);
            if (index >= 0) findings[index] = findings[index] with { Status = status };
        }
    }

    internal static string ReadText(string root, Guid id, string relative)
    {
        string path = AssetPath(root, id, relative);
        var info = new FileInfo(path);
        if (info.Length > 256 * 1024) throw new InvalidOperationException("The reference is too large.");
        return File.ReadAllText(path, Utf8);
    }

    private static string AssetPath(string root, Guid id, string relative)
    {
        if (!RepositoryImport.SafePath(relative)) throw new InvalidOperationException("The repository path is invalid.");
        string directory = Path.GetFullPath(Path.Combine(root, id.ToString("N")));
        string file = Path.GetFullPath(Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!file.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The repository path is outside the import.");
        for (string? part = file; part is not null && part.Length >= directory.Length; part = Path.GetDirectoryName(part))
            if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("The import path contains a link.");
        return file;
    }
}
