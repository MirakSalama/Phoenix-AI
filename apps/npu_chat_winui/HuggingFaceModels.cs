using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NpuChat;

internal static class HuggingFaceModels
{
    internal const long LargeFile = 2_000_000_000;
    private const string Hub = "https://huggingface.co";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 8_000_000 };
    private static readonly Regex RepoId = new(@"^[\w.-]+/[\w.-]+$", RegexOptions.CultureInvariant);
    private static readonly Regex SizeTerm = new(
        @"\b(?:(?<comparison>at least|more than|over|at most|less than|under|up to)\s+)?(?<value>\d+(?:\.\d+)?)\s*(?<unit>GB|MB|B|M)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal sealed record Model(string Id, long Parameters, ModelFile[]? Files = null);
    internal sealed record ModelFile(string Name, Uri Address, long Bytes, string? Sha256 = null);
    internal sealed record Repository(string Revision, ModelFile[] Files);
    internal sealed record Search(string Name, long MinParams, long MaxParams, long MinBytes, long MaxBytes)
    {
        internal bool FiltersFiles => MinBytes > 0 || MaxBytes < long.MaxValue;
        internal bool FiltersParameters => MinParams > 0 || MaxParams < long.MaxValue;
        internal bool Matches(ModelFile file) => file.Bytes >= MinBytes && file.Bytes <= MaxBytes;
    }

    internal static Search Parse(string query, int band)
    {
        (long minParams, long maxParams) = band switch
        {
            1 => (0L, 599_999_999L), 2 => (600_000_000L, 999_999_999L),
            3 => (1_000_000_000L, 9_999_999_999L), 4 => (10_000_000_000L, long.MaxValue),
            _ => (0L, long.MaxValue)
        };
        long minBytes = 0, maxBytes = long.MaxValue;
        // Full repo IDs and hyphenated filenames can already contain a model size as part of their name.
        string name = RepoId.IsMatch(query.Trim()) ? query : SizeTerm.Replace(query, match =>
        {
            if (match.Index > 0 && (query[match.Index - 1] is '-' or '/' or '_')) return match.Value;
            string unit = match.Groups["unit"].Value.ToUpperInvariant();
            string comparison = match.Groups["comparison"].Value.ToLowerInvariant();
            long value = (long)(Math.Min(double.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture), 1_000_000) *
                (unit.StartsWith('G') || unit == "B" ? 1_000_000_000 : 1_000_000));
            bool minimum = comparison is "at least" or "more than" or "over";
            bool bytes = unit.Length == 2;
            if (comparison is "more than" or "over") value++;
            if (comparison is "less than" or "under") value--;
            if (bytes && minimum) minBytes = Math.Max(minBytes, value);
            else if (bytes) maxBytes = Math.Min(maxBytes, value);
            else if (minimum) minParams = Math.Max(minParams, value);
            else maxParams = Math.Min(maxParams, value);
            return "";
        });
        return new(Regex.Replace(name.Trim(), @"\s+", " "), minParams, maxParams, minBytes, maxBytes);
    }

    internal static async Task<Model[]> FindAsync(Search search, int limit, CancellationToken cancel)
    {
        if (search.MinParams > search.MaxParams || search.MinBytes > search.MaxBytes) return [];
        List<string> query = ["filter=gguf", "gated=false", "sort=downloads", "direction=-1",
            $"limit={limit}", "expand%5B%5D=gguf", "expand%5B%5D=safetensors"];
        string name = search.Name;
        if (RepoId.IsMatch(name))
        {
            var parts = name.Split('/');
            query.Add("author=" + Uri.EscapeDataString(parts[0]));
            name = parts[1];
        }
        if (name.Length > 0) query.Add("search=" + Uri.EscapeDataString(name));
        List<string> constraints = [];
        // The Hub filter is coarse. Keep exact cutoffs in the local metadata check.
        if (search.MinParams > 0) constraints.Add($"min:{InBillions(search.MinParams, lower: true)}");
        if (search.MaxParams < long.MaxValue) constraints.Add($"max:{InBillions(search.MaxParams, lower: false)}");
        if (constraints.Count > 0) query.Add("num_parameters=" + Uri.EscapeDataString(string.Join(',', constraints)));
        using var document = await ReadAsync($"{Hub}/api/models?{string.Join('&', query)}", cancel);
        var models = document.RootElement.EnumerateArray().Select(item =>
            new Model(Field(item, "id") ?? "", Parameters(item)))
            .Where(model => RepoId.IsMatch(model.Id) && (!search.FiltersParameters ||
                (model.Parameters > 0 && model.Parameters >= search.MinParams && model.Parameters <= search.MaxParams)))
            .ToArray();
        if (!search.FiltersFiles) return models;
        using var slots = new SemaphoreSlim(4);
        return (await Task.WhenAll(models.Select(async model =>
        {
            await slots.WaitAsync(cancel);
            try
            {
                var files = (await FilesAsync(model.Id, cancel)).Where(search.Matches).ToArray();
                return model with { Files = files };
            }
            catch (HttpRequestException error) when (error.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Unauthorized)
            {
                return model with { Files = [] };
            }
            finally { slots.Release(); }
        }))).Where(model => model.Files is { Length: > 0 }).ToArray();
    }

    internal static bool IsRepositoryId(string repo) => RepoId.IsMatch(repo);

    internal static async Task<ModelFile[]> FilesAsync(string repo, CancellationToken cancel) =>
        (await RepositoryAsync(repo, cancel)).Files.Where(file => IsSingleModel(file.Name)).OrderBy(file => file.Bytes).ToArray();

    internal static async Task<Repository> RepositoryAsync(string repo, CancellationToken cancel)
    {
        if (!RepoId.IsMatch(repo)) throw new ArgumentException("Enter a Hugging Face publisher/model name.");
        string path = string.Join('/', repo.Split('/').Select(Uri.EscapeDataString));
        using var document = await ReadAsync($"{Hub}/api/models/{path}?blobs=true", cancel);
        var model = document.RootElement;
        string revision = Field(model, "sha") ?? "";
        if (!Regex.IsMatch(revision, "^[a-f0-9]{40}$") || !model.TryGetProperty("siblings", out var files))
            throw new InvalidDataException("The publisher did not report versioned file metadata.");
        var assets = files.EnumerateArray().Select(file =>
        {
            string name = Field(file, "rfilename") ?? "";
            bool hasLfs = file.TryGetProperty("lfs", out var lfs);
            long bytes = Count(file, "size");
            if (bytes == 0 && hasLfs) bytes = Count(lfs, "size");
            if (bytes <= 0 || name.Split('/').Any(part => part is "" or "." or "..")) return null;
            string sha = hasLfs ? Field(lfs, "sha256") ?? Field(lfs, "oid") ?? "" : "";
            string encoded = string.Join('/', name.Split('/').Select(Uri.EscapeDataString));
            return new ModelFile(name, new Uri($"{Hub}/{path}/resolve/{revision}/{encoded}?download=true"), bytes,
                Regex.IsMatch(sha, "^[a-fA-F0-9]{64}$") ? sha : null);
        }).OfType<ModelFile>().ToArray();
        return new(revision, assets);
    }

    internal static async Task<string[]> PublishedModelsAsync(string owner, string prefix, CancellationToken cancel)
    {
        string query = $"author={Uri.EscapeDataString(owner)}&search={Uri.EscapeDataString(prefix)}&limit=100";
        using var document = await ReadAsync($"{Hub}/api/models?{query}", cancel);
        return document.RootElement.EnumerateArray().Select(item => Field(item, "id") ?? "").Where(IsRepositoryId).ToArray();
    }

    internal static async Task<ModelFile> InspectUrlAsync(string address, CancellationToken cancel)
    {
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.UserInfo.Length != 0 || !IsSingleModel(Uri.UnescapeDataString(uri.AbsolutePath)))
            throw new ArgumentException("Enter a direct HTTPS link to one .gguf model file.");
        using var request = new HttpRequestMessage(HttpMethod.Head, uri);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is not long bytes || bytes <= 0)
            throw new InvalidOperationException("The source did not report the file size. Use another link or import a local file.");
        return new(Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath)), uri, bytes);
    }

    internal static bool IsSingleModel(string name) => name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) &&
        !Path.GetFileName(name).Contains("mmproj", StringComparison.OrdinalIgnoreCase) &&
        !Regex.IsMatch(name, @"\d{5}-of-\d{5}\.gguf$", RegexOptions.IgnoreCase);

    internal static async Task DownloadAsync(ModelFile file, string stage, IProgress<long> progress, CancellationToken cancel)
    {
        using var response = await Http.GetAsync(file.Address, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length != file.Bytes)
            throw new InvalidDataException("The file size changed. Review the download again.");
        await using var input = await response.Content.ReadAsStreamAsync(cancel);
        await using var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, useAsync: true);
        using var hash = file.Sha256 is not null ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        var buffer = new byte[131072];
        long total = 0;
        long lastUpdate = 0;
        using var readTimeout = new CancellationTokenSource();
        using var readCancel = CancellationTokenSource.CreateLinkedTokenSource(cancel, readTimeout.Token);
        while (true)
        {
            readTimeout.CancelAfter(TimeSpan.FromSeconds(60));
            int read = await input.ReadAsync(buffer, readCancel.Token);
            if (read == 0) break;
            total += read;
            if (total > file.Bytes) throw new InvalidDataException("The file is larger than the approved size.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancel);
            hash?.AppendData(buffer, 0, read);
            if (Environment.TickCount64 - lastUpdate >= 150)
            {
                progress.Report(total);
                lastUpdate = Environment.TickCount64;
            }
        }
        if (total != file.Bytes || (hash is not null &&
            !Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The download did not match the source file.");
        progress.Report(total);
    }

    internal static string Size(long bytes) => bytes >= 1_000_000_000 ? $"{bytes / 1e9:0.##} GB" : $"{bytes / 1e6:0.##} MB";
    private static string InBillions(long value, bool lower) =>
        ((lower ? Math.Floor(value / 1e6) : Math.Ceiling(value / 1e6)) / 1000)
            .ToString("0.###", CultureInfo.InvariantCulture) + "B";

    private static async Task<JsonDocument> ReadAsync(string address, CancellationToken cancel)
    {
        using var response = await Http.GetAsync(address, cancel);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancel);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancel);
    }

    private static long Parameters(JsonElement model) =>
        model.TryGetProperty("gguf", out var gguf) && Count(gguf, "total") is > 0 and var total ? total :
        model.TryGetProperty("safetensors", out var safe) ? Count(safe, "total") : 0;

    private static string? Field(JsonElement item, string key) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String
            ? field.GetString() : null;
    private static long Count(JsonElement item, string key) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var field) &&
        field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out long value) ? value : 0;
}
