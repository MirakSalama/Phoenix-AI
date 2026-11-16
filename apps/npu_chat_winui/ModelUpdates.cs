using System.Collections.Concurrent;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NpuChat;

internal static partial class ModelUpdates
{
    internal sealed record Track(string Key, string Name, string Installed, Uri? Publisher,
        Func<CancellationToken, Task<string>>? Check);

    private sealed record Asset(string Name, long Bytes, string? Hash);
    private sealed record FilePair(string Local, string Published);
    private sealed record FileHash(long Bytes, DateTime Modified, string Hash);
    private static readonly ConcurrentDictionary<string, FileHash> Hashes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient GitHub = new() { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 2_000_000 };
    private const string ParakeetOnnx = "istupakov/parakeet-tdt-0.6b-v2-onnx";
    private const string KokoroReleases = "https://github.com/thewh1teagle/kokoro-onnx/releases";

    internal static Track[] Chat(string project, LocalModel model)
    {
        string gguf = LocalEngine.GgufPath(project, model);
        string key = $"chat:{(int)model}";
        if (ModelLibrary.Find(model) is { } imported)
        {
            var (repo, filename) = ImportedSource(imported);
            return [repo is null
                ? new Track(key, "Imported file", Installed([gguf]), null, null)
                : HubTrack(key, "Imported GGUF", repo, [new(gguf, filename!)])];
        }

        var (ggufRepo, npuRepo, converted) = model switch
        {
            LocalModel.Lfm230 => ("LiquidAI/LFM2.5-230M-GGUF", "LiquidAI/LFM2.5-230M", false),
            LocalModel.Lfm350 => ("LiquidAI/LFM2.5-350M-GGUF", "LiquidAI/LFM2.5-350M", false),
            LocalModel.Lfm3 => ("LiquidAI/LFM3-1.7B-GGUF", "LiquidAI/LFM3-1.7B", true),
            LocalModel.Gemma => ("ggml-org/gemma-4-E2B-it-GGUF", "google/gemma-4-E2B-it", true),
            LocalModel.Qwen35 => ("lmstudio-community/Qwen3.5-2B-GGUF", null, false),
            _ => throw new ArgumentOutOfRangeException(nameof(model))
        };
        var cpuGpu = HubTrack(key, "CPU / GPU", ggufRepo, [new(gguf, Path.GetFileName(gguf))]);
        if (npuRepo is null) return [cpuGpu];
        string npu = LocalEngine.WeightPath(project, model, LocalBackend.Npu);
        return [cpuGpu, HubTrack(key + ":npu", "NPU", npuRepo, [new(npu, "model.safetensors")], converted)];
    }

    internal static Track[] Services(string project)
    {
        string root = PortableRuntime.ModelRoot(project);
        string d1 = Path.Combine(root, "d1-omni-600m", "d1-omni-600M-Q8_0.gguf");
        string speech = Path.Combine(root, "parakeet");
        var parakeet = new FilePair[]
        {
            new(Path.Combine(speech, "encoder-model.int8.onnx"), "encoder-model.int8.onnx"),
            new(Path.Combine(speech, "decoder_joint-model.onnx"), "decoder_joint-model.onnx")
        };
        string voice = Path.Combine(root, "voice");
        var kokoro = new FilePair[]
        {
            new(Path.Combine(voice, "kokoro-v1.0.onnx"), "kokoro-v1.0.onnx"),
            new(Path.Combine(voice, "voices-v1.0.bin"), "voices-v1.0.bin")
        };
        return
        [
            HubTrack("router", "D1 Omni 600M · Router", "LiquidAI/d1-omni-600M-GGUF", [new(d1, Path.GetFileName(d1))]),
            new("dictation", "Parakeet · Speech to text", "v2 · CPU " + Installed(parakeet.Select(file => file.Local)) +
                (File.Exists(Path.Combine(speech, "manifest.json")) && File.Exists(Path.Combine(speech, "weights.bin"))
                    ? " · Phoenix NPU bundle installed" : ""),
                new Uri("https://huggingface.co/models?author=nvidia&search=parakeet-tdt-0.6b"), cancel => CheckParakeetAsync(parakeet, cancel)),
            new("speech", "Kokoro · Text to speech", "v1.0 English · " + Installed(kokoro.Select(file => file.Local)),
                new Uri(KokoroReleases), cancel => CheckKokoroAsync(kokoro, cancel))
        ];
    }

    private static Track HubTrack(string key, string label, string repo, FilePair[] files, bool converted = false)
    {
        bool present = files.All(file => File.Exists(file.Local));
        return new(key, label, Installed(files.Select(file => file.Local)), Hub(repo), present ? async cancel =>
        {
            var latest = await HuggingFaceModels.RepositoryAsync(repo, cancel);
            string revision = latest.Revision[..8];
            return converted
                ? $"Publisher revision {revision} · Phoenix-converted weights require a rebuild to update. Installed revision untracked."
                : await CompareAsync(files, HubAssets(latest), $"Revision {revision}", cancel);
        } : null);
    }

    private static async Task<string> CheckParakeetAsync(FilePair[] files, CancellationToken cancel)
    {
        var family = HuggingFaceModels.PublishedModelsAsync("nvidia", "parakeet-tdt-0.6b", cancel);
        var build = HuggingFaceModels.RepositoryAsync(ParakeetOnnx, cancel);
        await Task.WhenAll(family, build);
        var latest = (await family).Select(id => ParakeetVersion().Match(id))
            .Select(match => int.TryParse(match.Groups[1].Value, out int major) ? major : 0).DefaultIfEmpty().Max();
        var v2 = await build;
        string status = await CompareAsync(files, HubAssets(v2), $"v2 ONNX revision {v2.Revision[..8]}", cancel);
        if (latest > 2) status += $"\nNVIDIA v{latest} is available upstream. It needs Phoenix dictation support and an updated NPU bundle.";
        return status;
    }

    private static async Task<string> CheckKokoroAsync(FilePair[] files, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.github.com/repos/thewh1teagle/kokoro-onnx/releases?per_page=30");
        request.Headers.UserAgent.ParseAdd("Phoenix-AI-Model-Updates/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await GitHub.SendAsync(request, cancel);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
        var releases = document.RootElement.EnumerateArray().Where(item =>
                !item.GetProperty("draft").GetBoolean() && !item.GetProperty("prerelease").GetBoolean())
            .Select(item => (Version: ReadKokoroVersion(item.GetProperty("tag_name").GetString()), Item: item))
            .Where(release => release.Version is not null).OrderByDescending(release => release.Version).ToArray();
        foreach (var (version, item) in releases)
        {
            var assets = item.GetProperty("assets").EnumerateArray().Select(asset =>
            {
                string? digest = asset.TryGetProperty("digest", out var hash) ? hash.GetString() : null;
                return new Asset(asset.GetProperty("name").GetString() ?? "", asset.GetProperty("size").GetInt64(),
                    digest is { Length: 71 } && digest.StartsWith("sha256:", StringComparison.Ordinal) ? digest[7..] : null);
            }).ToArray();
            if (!files.All(file => assets.Any(asset => asset.Name == file.Published))) continue;
            string status = await CompareAsync(files, assets, $"Kokoro export v{version}", cancel);
            if (releases[0].Version > version) status += $"\nA newer export v{releases[0].Version} is available with different files.";
            return status;
        }
        return "Publisher did not list a release with the installed English model and voice pack.";
    }

    private static Version? ReadKokoroVersion(string? tag) => tag is not null &&
        tag.StartsWith("model-files-v", StringComparison.Ordinal) && Version.TryParse(tag[13..], out var version) ? version : null;

    private static Asset[] HubAssets(HuggingFaceModels.Repository repo) =>
        repo.Files.Select(file => new Asset(file.Name, file.Bytes, file.Sha256)).ToArray();

    private static async Task<string> CompareAsync(FilePair[] files, Asset[] assets, string version, CancellationToken cancel)
    {
        bool unverifiable = false;
        foreach (var file in files)
        {
            cancel.ThrowIfCancellationRequested();
            var matching = assets.Where(asset => asset.Name == file.Published).ToArray();
            if (matching.Length == 0 && !file.Published.Contains('/'))
                matching = assets.Where(asset => Path.GetFileName(asset.Name) == file.Published).ToArray();
            if (matching.Length != 1) return $"{version} · No unique match for {file.Published}.";
            var published = matching[0];
            var local = new FileInfo(file.Local);
            if (!local.Exists) return $"{version} · Missing {Path.GetFileName(file.Local)}.";
            if (local.Length != published.Bytes || (published.Hash is not null &&
                !string.Equals(await Sha256Async(local, cancel), published.Hash, StringComparison.OrdinalIgnoreCase)))
                return $"Publisher version differs · {version}. {file.Published} has different published bytes.";
            unverifiable |= published.Hash is null;
        }
        return unverifiable ? $"{version} · Size matches. Publisher checksum unavailable." : $"Up to date · {version}.";
    }

    private static async Task<string> Sha256Async(FileInfo file, CancellationToken cancel)
    {
        long bytes = file.Length;
        DateTime modified = file.LastWriteTimeUtc;
        if (Hashes.TryGetValue(file.FullName, out var cached) && cached.Bytes == bytes && cached.Modified == modified)
            return cached.Hash;
        await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 131072,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancel));
        file.Refresh();
        if (file.Length != bytes || file.LastWriteTimeUtc != modified) throw new IOException("The model file changed during the check.");
        Hashes[file.FullName] = new(bytes, modified, hash);
        return hash;
    }

    private static (string? Repo, string? File) ImportedSource(ModelLibrary.Entry entry)
    {
        if (HuggingFaceModels.IsRepositoryId(entry.Source)) return (entry.Source, entry.SourceFile ?? entry.Name + ".gguf");
        if (Uri.TryCreate(entry.Source, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
            uri.Host.Equals("huggingface.co", StringComparison.OrdinalIgnoreCase) && uri.UserInfo.Length == 0)
        {
            string[] parts = Uri.UnescapeDataString(uri.AbsolutePath).Trim('/').Split('/');
            if (parts.Length >= 5 && parts[2] == "resolve" && HuggingFaceModels.IsRepositoryId($"{parts[0]}/{parts[1]}"))
                return ($"{parts[0]}/{parts[1]}", string.Join('/', parts.Skip(4)));
        }
        return (null, null);
    }

    private static string Installed(IEnumerable<string> paths)
    {
        var files = paths.Select(path => new FileInfo(path)).ToArray();
        long bytes = files.Where(file => file.Exists).Sum(file => file.Length);
        return bytes == 0 ? "Not installed" : $"{(files.All(file => file.Exists) ? "Installed" : "Incomplete")} · {HuggingFaceModels.Size(bytes)}";
    }

    private static Uri Hub(string repo) => new($"https://huggingface.co/{repo}");

    [GeneratedRegex(@"^nvidia/parakeet-tdt-0\.6b-v(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ParakeetVersion();
}
