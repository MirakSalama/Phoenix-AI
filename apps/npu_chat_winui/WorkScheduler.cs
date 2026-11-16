using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace NpuChat;

internal enum WorkIntent { Quick, Standard, Deep, Split, Verify, Support }
internal enum ReplySize { Brief, Normal, Detailed, Extended }
internal enum WorkMode { Manual, Smart, Parallel, SameModel }

internal sealed record InferenceMessage([property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);
internal sealed record WorkTarget(LocalModel Model, LocalBackend Backend, GpuDevice? Gpu, long Bytes, int ContextLimit)
{
    internal string Device => Gpu?.Name ?? Backend.ToString().ToUpperInvariant();
    internal string ShortDevice => Backend switch
    {
        LocalBackend.Integrated => "iGPU", LocalBackend.Discrete => "dGPU", _ => Device
    };
    internal long DeviceBytes => Bytes + (384L << 20) + ContextLimit * (48L << 10);
    internal long HostBytes => Backend switch
    {
        LocalBackend.Discrete => 512L << 20,
        LocalBackend.Npu => Bytes * 2 + (512L << 20),
        _ => DeviceBytes
    };
}

internal sealed record WorkProfile(LocalModel Model, LocalBackend Backend, string Device, int ContextLimit,
    bool Concurrent, int Samples, double Decode, double FirstMs, double LoadSeconds, int LoadedContext = 0,
    double? PromptRate = null);

internal sealed class WorkProfiles
{
    private readonly List<WorkProfile> rows = [];
    private readonly string file = Path.Combine(LocalWorkspace.Folder, "scheduler-profiles.json");

    internal WorkProfiles()
    {
        try
        {
            if (File.Exists(file))
                rows.AddRange((JsonSerializer.Deserialize<List<WorkProfile>>(File.ReadAllText(file)) ?? [])
                    .Where(row => row is not null && row.Samples > 0 && row.Decode > 0 &&
                        double.IsFinite(row.Decode) && double.IsFinite(row.FirstMs) && double.IsFinite(row.LoadSeconds)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            DiagnosticLog.Write("scheduler", $"Profiles unavailable: {error.GetType().Name}");
        }
    }

    internal static BenchmarkReport? Benchmarks()
    {
        try { return ModelBenchmark.ReadReport(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            DiagnosticLog.Write("scheduler", $"Benchmark unavailable: {error.GetType().Name}");
            return null;
        }
    }

    internal WorkProfile? Find(WorkTarget target, bool concurrent) => rows.FirstOrDefault(row => Matches(row, target, concurrent));

    internal void Observe(WorkTarget target, bool concurrent, double decode, double firstMs, double loadSeconds,
        int loadedContext, double? promptRate)
    {
        if (decode <= 0 || !double.IsFinite(decode) || !double.IsFinite(firstMs)) return;
        int index = rows.FindIndex(row => Matches(row, target, concurrent));
        var previous = index >= 0 ? rows[index] : null;
        double Blend(double value, double old) => previous is null || old <= 0 ? value : old * 0.75 + value * 0.25;
        var updated = new WorkProfile(target.Model, target.Backend, target.Device, target.ContextLimit, concurrent,
            Math.Min(1_000_000, (previous?.Samples ?? 0) + 1), Blend(decode, previous?.Decode ?? 0),
            Blend(firstMs, previous?.FirstMs ?? 0),
            loadSeconds > 0 ? Blend(loadSeconds, previous?.LoadSeconds ?? 0) : previous?.LoadSeconds ?? 0,
            loadedContext, promptRate is > 0 and var rate && double.IsFinite(rate)
                ? Blend(rate, previous?.PromptRate ?? 0) : previous?.PromptRate);
        if (index < 0) rows.Add(updated);
        else rows[index] = updated;
    }

    internal void Save()
    {
        string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(LocalWorkspace.Folder);
            File.WriteAllText(temporary, JsonSerializer.Serialize(rows));
            File.Move(temporary, file, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write("scheduler", $"Could not save profiles: {error.GetType().Name}");
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool Matches(WorkProfile row, WorkTarget target, bool concurrent) =>
        row.Model == target.Model && row.Backend == target.Backend && row.Device == target.Device &&
        row.ContextLimit == target.ContextLimit && row.Concurrent == concurrent;
}

internal static partial class WorkScheduler
{
    internal const int MaxWorkers = 4;
    internal const int NoteOutputTokens = 128;
    internal const int ContextReserve = 128;

    internal static int WorkerLimit(WorkMode mode, LocalBackend device, WorkIntent intent, ReplySize? size)
    {
        if (device != LocalBackend.Auto || mode == WorkMode.Manual || intent is WorkIntent.Quick or WorkIntent.Support)
            return 1;
        if (mode is WorkMode.Parallel or WorkMode.SameModel || intent == WorkIntent.Split) return MaxWorkers;
        if (intent is WorkIntent.Deep or WorkIntent.Verify || size is ReplySize.Detailed or ReplySize.Extended)
            return size == ReplySize.Extended ? 3 : 2;
        return 1;
    }

    [GeneratedRegex(@"\b(?<count>[\d,]{1,7})[ -]+(?<unit>words?|tokens?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RequestedLength();

    [GeneratedRegex(@"\b(?:in (?:one|a single) sentence|one[- ]sentence|briefly|keep (?:it|this) short)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BriefRequest();

    [GeneratedRegex(@"\b(?:detailed|comprehensive|long|full[- ]length|in[- ]depth)\s+(?:answer|explanation|guide|article|report|draft|analysis|plan|write[- ]up)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DetailedRequest();

    [GeneratedRegex(@"^\s*(?:hello|hi|hey|thanks|thank you|good (?:morning|afternoon|evening)|how are you)[!?. ]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex CasualOpening();

    internal static WorkIntent Infer(string text, int tokens)
    {
        if (PhoenixPersonality.CallsForEmpathy(text)) return WorkIntent.Support;
        if (text.Contains("compare", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("independently", StringComparison.OrdinalIgnoreCase)) return WorkIntent.Split;
        if (text.Contains("double-check", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("verify", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("audit", StringComparison.OrdinalIgnoreCase)) return WorkIntent.Verify;
        if (tokens > 1600 || text.Contains("debug", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("trade-offs", StringComparison.OrdinalIgnoreCase)) return WorkIntent.Deep;
        return text.Length < 90 ? WorkIntent.Quick : WorkIntent.Standard;
    }

    internal static bool NeedsNpuBoost(WorkIntent intent, int promptTokens, int outputTokens, bool thinking) =>
        thinking || promptTokens >= 256 || outputTokens >= 512 ||
        intent is WorkIntent.Deep or WorkIntent.Split or WorkIntent.Verify;

    internal static int OutputBudget(string text, WorkIntent intent, ReplySize? size, bool thinking)
    {
        int output = (size ?? (intent switch
        {
            WorkIntent.Quick or WorkIntent.Support => ReplySize.Brief,
            WorkIntent.Deep or WorkIntent.Split or WorkIntent.Verify => ReplySize.Detailed,
            _ => ReplySize.Normal
        })) switch { ReplySize.Brief => 192, ReplySize.Normal => 512, ReplySize.Detailed => 2048, _ => 3072 };
        if (BriefRequest().IsMatch(text)) output = 192;
        if (DetailedRequest().IsMatch(text)) output = Math.Max(output, 2048);
        if (RequestedLength().Match(text) is { Success: true } match &&
            int.TryParse(match.Groups["count"].Value.Replace(",", ""), out int count) && count > 0)
            output = Math.Clamp(match.Groups["unit"].Value.StartsWith("word", StringComparison.OrdinalIgnoreCase)
                ? count * 2 + 80 : count + 48, 128, 8192);
        return output + (thinking ? 384 : 0);
    }

    internal static int RequiredContext(int prompt, int output) => prompt + output + ContextReserve;

    internal static int ReplyRoom(int capacity, int prompt) => Math.Max(0, capacity - RequiredContext(prompt, 0));

    internal static int SynthesisContext(int required, int output, int workers) =>
        Math.Max(8192, required + Math.Max(output, 2048) + Math.Max(0, workers - 1) * 512);

    internal static string[][] AssignParts(IReadOnlyList<string> parts, IReadOnlyList<int> room)
    {
        var assignments = room.Select(_ => new List<string>()).ToArray();
        var weights = new int[room.Count];
        foreach (string part in parts.OrderByDescending(part => EstimateTokens([new("user", part)])))
        {
            // Fill each eligible slot once, then balance remaining work by the space available on each worker.
            int index = Enumerable.Range(0, room.Count).Where(i => room[i] > 0)
                .OrderBy(i => weights[i] / (double)room[i]).ThenByDescending(i => room[i]).FirstOrDefault(-1);
            if (index < 0) break;
            assignments[index].Add(part);
            weights[index] += EstimateTokens([new("user", part)]);
        }
        return assignments.Select(partsForWorker => partsForWorker.ToArray()).ToArray();
    }

    internal static IEnumerable<WorkTarget> Rank(IEnumerable<WorkTarget> targets, WorkIntent intent, WorkProfiles profiles,
        BenchmarkReport? benchmark, WorkTarget? warm, int promptTokens = 0, bool concurrent = false, int outputTokens = 0)
    {
        return targets.OrderBy(target =>
        {
            var profile = profiles.Find(target, concurrent) ?? profiles.Find(target, false);
            var saved = benchmark?.Results?.FirstOrDefault(row => row is not null && row.Model == target.Model &&
                (row.Backend == target.Backend || row.Backend is null && row.Device == target.Device) &&
                (target.Gpu is null || row.Device == target.Device) && row.Error is null && row.DecodeRate > 0);
            var rate = profile?.Decode ?? saved?.DecodeRate ?? (target.Backend switch
            {
                LocalBackend.Discrete => 75, LocalBackend.Integrated => 35,
                LocalBackend.Npu when target.Model == LocalModel.Lfm230 => 55,
                LocalBackend.Npu when target.Model == LocalModel.Lfm350 => 40,
                LocalBackend.Npu => 6, _ => 20
            });
            double load = target == warm ? 0 : profile?.LoadSeconds is > 0 and var measured
                ? measured : saved?.LoadSeconds ?? 2.5;
            double latency = (profile?.FirstMs ?? saved?.FirstTokenMs ?? 300) / 1000;
            if (profile?.PromptRate is > 0 and var prefill && double.IsFinite(prefill))
                latency = Math.Max(latency, promptTokens / prefill);
            bool quick = intent == WorkIntent.Quick;
            bool deep = intent is WorkIntent.Deep or WorkIntent.Split or WorkIntent.Verify or WorkIntent.Support;
            double modelCost = target.Model switch
            {
                LocalModel.Lfm230 => deep ? 20 : quick ? 0 : 2,
                LocalModel.Lfm350 => deep ? 12 : quick ? 1 : 0,
                LocalModel.Lfm3 => quick ? 3 : 0,
                _ => quick ? 4 : 1
            };
            double decode = outputTokens > 0 ? outputTokens * 0.45 : quick ? 36 : deep && intent != WorkIntent.Support ? 240 : 120;
            return load + latency + decode / rate + modelCost;
        });
    }

    internal static int EstimateTokens(IEnumerable<InferenceMessage> messages) =>
        messages.Sum(message => System.Text.Encoding.UTF8.GetByteCount(message.Content) / 3 + 12);

    internal static long AvailableHostBytes()
    {
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref memory)
            ? (long)Math.Min(memory.AvailablePhysical, memory.AvailableCommit)
            : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        internal uint Length, Load;
        internal ulong TotalPhysical, AvailablePhysical, TotalCommit, AvailableCommit;
        internal ulong TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus memory);
}
