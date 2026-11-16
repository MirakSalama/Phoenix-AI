using System.Text.Json;

namespace NpuChat;

internal static class ModelLibrary
{
    internal sealed record Entry(int Id, string Name, string Source, bool Hidden = false, string? SourceFile = null);
    internal sealed record Manifest(int NextId, Entry[] Entries, LocalModel[] Hidden);

    private const int FirstImport = 1000;
    private static readonly LocalModel[] BuiltIns = Enum.GetValues<LocalModel>();
    private static Manifest manifest = new(FirstImport, [], []);
    private static string root = "";

    internal static void Open(string modelRoot)
    {
        root = modelRoot;
        if (!File.Exists(ManifestPath)) return;
        try
        {
            var saved = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(ManifestPath));
            if (saved is null || saved.Entries.Any(entry => entry.Id < FirstImport || string.IsNullOrWhiteSpace(entry.Name)) ||
                saved.Entries.Select(entry => entry.Id).Distinct().Count() != saved.Entries.Length ||
                saved.NextId < FirstImport || saved.Entries.Any(entry => entry.Id >= saved.NextId))
                throw new InvalidDataException("Invalid model library.");
            manifest = saved;
        }
        catch (Exception error)
        {
            DiagnosticLog.Write("models", $"Could not open model library: {error.Message}");
            ReadOnly = true;
        }
    }

    internal static bool ReadOnly { get; private set; }
    private static string ManifestPath => Path.Combine(root, "phoenix-models.json");
    internal static bool IsBuiltIn(LocalModel model) => Array.IndexOf(BuiltIns, model) >= 0;
    internal static Entry? Find(LocalModel model) => manifest.Entries.FirstOrDefault(entry => entry.Id == (int)model);
    internal static bool IsKnown(LocalModel model) => IsBuiltIn(model) || Find(model) is not null;
    internal static bool IsVisible(LocalModel model) => IsBuiltIn(model)
        ? !manifest.Hidden.Contains(model) : Find(model) is { Hidden: false };
    internal static LocalModel[] All() => [.. BuiltIns, .. manifest.Entries.Select(entry => (LocalModel)entry.Id)];
    internal static LocalModel[] Visible() => All().Where(IsVisible).ToArray();
    internal static string ImportPath(LocalModel model) => Path.Combine(root, "Imported", $"{(int)model}.gguf");

    internal static void SetVisible(LocalModel model, bool visible)
    {
        if (!IsKnown(model)) throw new InvalidOperationException("This model is no longer registered.");
        var next = IsBuiltIn(model)
            ? manifest with { Hidden = visible ? manifest.Hidden.Where(value => value != model).ToArray()
                : [.. manifest.Hidden.Where(value => value != model), model] }
            : manifest with { Entries = manifest.Entries.Select(entry => entry.Id == (int)model ? entry with { Hidden = !visible } : entry).ToArray() };
        Save(next);
    }

    internal static async Task AddAsync(string name, string source, Func<string, CancellationToken, Task> copy,
        CancellationToken cancel, string? sourceFile = null)
    {
        if (ReadOnly) throw new InvalidOperationException("The model library could not be opened.");
        int id = manifest.NextId;
        string path = ImportPath((LocalModel)id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        CheckManagedPath(path);
        while (File.Exists(path))
        {
            id = checked(id + 1);
            path = ImportPath((LocalModel)id);
            CheckManagedPath(path);
        }
        string stage = path + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            await copy(stage, cancel);
            cancel.ThrowIfCancellationRequested();
            using (var stream = File.OpenRead(stage))
            {
                byte[] magic = new byte[4];
                if (stream.Read(magic) != 4 || !magic.AsSpan().SequenceEqual("GGUF"u8))
                    throw new InvalidDataException("That file is not a GGUF model.");
            }
            File.Move(stage, path);
            try
            {
                Save(manifest with
                {
                    NextId = checked(id + 1),
                    Entries = [.. manifest.Entries, new Entry(id, name, source, SourceFile: sourceFile)]
                });
            }
            catch { File.Delete(path); throw; }
        }
        finally { File.Delete(stage); }
    }

    internal static void CheckManagedPath(string path)
    {
        string relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("This file is outside the model folder.");
        string current = Path.GetFullPath(root);
        foreach (string part in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Linked model files and folders cannot be changed here.");
        }
    }

    private static void Save(Manifest next)
    {
        if (ReadOnly) throw new InvalidOperationException("The model library could not be opened.");
        Directory.CreateDirectory(root);
        CheckManagedPath(ManifestPath);
        string stage = ManifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(stage, JsonSerializer.Serialize(next));
            File.Move(stage, ManifestPath, overwrite: true);
            manifest = next;
        }
        finally { File.Delete(stage); }
    }
}
