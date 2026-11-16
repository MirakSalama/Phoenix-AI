using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace NpuChat;

internal sealed record BenchmarkTarget(LocalModel Model, string ModelName, string Device, GpuDevice? Gpu,
    bool CpuOnly, bool Supported)
{
    internal LocalBackend Backend => CpuOnly ? LocalBackend.Cpu : Gpu is null ? LocalBackend.Npu :
        Gpu.Integrated ? LocalBackend.Integrated : LocalBackend.Discrete;
}
internal sealed record BenchmarkResult(LocalModel Model, string ModelName, string Device, double? Score,
    int Correct, double DecodeRate, double FirstTokenMs, int GeneratedTokens, double LoadSeconds, string? Error,
    LocalBackend? Backend = null, bool Unavailable = false)
{
    internal bool IsUnavailable => Unavailable || Error == "The local model or runtime is missing.";
    internal string Rating => Score switch
    {
        >= 90 => "A", >= 80 => "B", >= 65 => "C", >= 50 => "D", not null => "E", _ => "—"
    };
}
internal sealed record BenchmarkReport(DateTimeOffset Started, bool Canceled, List<BenchmarkResult> Results);

internal static class ModelBenchmark
{
    private static readonly (string Prompt, string Expected)[] Checks =
    [
        ("What is 19 plus 23? Answer with only the number.", "42"),
        ("What is the capital of France? Answer with one word.", "Paris"),
        ("How many days are in a week? Answer with only the number.", "7")
    ];
    private const string Generation =
        "Write a numbered list of twelve practical tips for organizing a home office. Give one sentence per tip.";
    private static string ReportPath => Path.Combine(LocalWorkspace.Folder, "benchmark.json");

    internal static BenchmarkReport? ReadReport() => File.Exists(ReportPath)
        ? JsonSerializer.Deserialize<BenchmarkReport>(File.ReadAllText(ReportPath)) : null;

    internal static void SaveReport(BenchmarkReport report)
    {
        Directory.CreateDirectory(LocalWorkspace.Folder);
        string pending = ReportPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(pending, JsonSerializer.Serialize(report));
        File.Move(pending, ReportPath, overwrite: true);
    }

    internal static async Task<BenchmarkResult> RunAsync(string project, BenchmarkTarget target,
        Action<string> status, CancellationToken cancel)
    {
        if (!target.Supported) return Failed(target, target.Backend == LocalBackend.Npu
            ? "The XDNA 1 checkpoint or its compiled programs are missing."
            : "The local GGUF file or runner is missing.", unavailable: true);
        var events = Channel.CreateUnbounded<JsonElement>();
        var load = Stopwatch.StartNew();
        LocalEngine? worker = null;
        int requestId = 0;
        bool lfm3Npu = target.Model == LocalModel.Lfm3 && target.Backend == LocalBackend.Npu;
        int checkBudget = lfm3Npu ? 128 : 16;
        try
        {
            worker = new LocalEngine(project, message =>
            {
                if (message.GetProperty("kind").GetString() != "chunk") events.Writer.TryWrite(message);
            }, error => events.Writer.TryComplete(new InvalidOperationException(error)),
                target.Model, target.Gpu, cpuOnly: target.CpuOnly, benchmark: true);
            var ready = await WaitForAsync("ready", null, TimeSpan.FromMinutes(5));
            if (ready.TryGetProperty("npu", out var npu) && !npu.GetBoolean())
                return Failed(target, "The NPU worker did not initialize.");
            double loadSeconds = load.Elapsed.TotalSeconds;
            status("Warm-up");
            await TryShortReply("Say hello in one short sentence.");
            int correct = 0;
            var latency = new List<double>();
            for (int i = 0; i < Checks.Length; i++)
            {
                status($"Answer check {i + 1}/{Checks.Length}");
                if (await TryShortReply(Checks[i].Prompt) is { } sample)
                {
                    string answer = sample.GetProperty("response").GetString() ?? "";
                    if (MatchesAnswer(answer, Checks[i].Expected)) correct++;
                    if (!string.IsNullOrWhiteSpace(answer)) latency.Add(FirstTokenMs(sample));
                }
            }
            status("Measuring sustained generation");
            var result = await Reply(Generation, 64);
            if (lfm3Npu && string.IsNullOrWhiteSpace(result.GetProperty("response").GetString()))
                result = await Reply(Generation, 160);
            if (string.IsNullOrWhiteSpace(result.GetProperty("response").GetString()))
                return Failed(target, "The generation test ended before a visible answer.");
            latency.Add(FirstTokenMs(result));
            int tokens = result.GetProperty("generated_tokens").GetInt32();
            var decode = result.GetProperty("decode_tokens_per_s");
            if (tokens < 8 || decode.ValueKind != JsonValueKind.Number || decode.GetDouble() <= 0)
                return Failed(target, $"Too few timed output tokens ({tokens}) for a speed rating.");
            double rate = decode.GetDouble();
            latency.Sort();
            double first = (latency[(latency.Count - 1) / 2] + latency[latency.Count / 2]) / 2;
            double score = Score(correct, rate, first);
            return new(target.Model, target.ModelName, target.Device, score, correct, rate, first, tokens, loadSeconds,
                null, target.Backend);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            DiagnosticLog.Write("benchmark", $"{target.ModelName} on {target.Device}: {error}");
            string reason = error is OperationCanceledException ? "The worker timed out." :
                error is ChannelClosedException { InnerException: { } cause } ? cause.Message : error.Message;
            return Failed(target, reason);
        }
        finally
        {
            if (worker is not null) await worker.StopAsync();
        }

        async Task<JsonElement> Reply(string prompt, int tokens)
        {
            int id = ++requestId;
            worker!.Send(new { kind = "chat", id, max_tokens = tokens, thinking = false,
                messages = new[] { new { role = "user", content = prompt } } });
            return await WaitForAsync("reply", id, TimeSpan.FromMinutes(2));
        }

        async Task<JsonElement?> TryShortReply(string prompt)
        {
            try { return await Reply(prompt, checkBudget); }
            catch (NoVisibleAnswer) { return null; }
        }

        async Task<JsonElement> WaitForAsync(string expected, int? id, TimeSpan duration)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(duration);
            while (true)
            {
                var message = await events.Reader.ReadAsync(timeout.Token);
                var kind = message.GetProperty("kind").GetString();
                if (kind == "fatal") throw new InvalidOperationException(message.GetProperty("text").GetString());
                if (kind == "status" && id is null)
                    status(message.GetProperty("text").GetString() ?? "Loading model");
                if (id is int current && (!message.TryGetProperty("id", out var incoming) || incoming.GetInt32() != current))
                    continue;
                if (kind == "error")
                {
                    string? error = message.GetProperty("text").GetString();
                    if (message.TryGetProperty("code", out var code) && code.GetString() == "no_visible_answer")
                        throw new NoVisibleAnswer(error);
                    throw new InvalidOperationException(error);
                }
                if (kind == expected) return message;
            }
        }
    }

    internal static bool MatchesAnswer(string text, string expected)
    {
        var cleaned = text.Trim().Trim('*', '_', (char)96, '"', '.', '!', ' ');
        if (expected == "7" && cleaned.Equals("seven", StringComparison.OrdinalIgnoreCase)) return true;
        return cleaned.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
               Regex.IsMatch(cleaned, @"^(?:the answer is |the capital of france is )" + Regex.Escape(expected) + "$",
                   RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    internal static double Score(int correct, double tokensPerSecond, double firstTokenMs) =>
        Math.Round(60.0 * correct / Checks.Length + 30 * Math.Clamp(tokensPerSecond / 60, 0, 1) +
                   10 * Math.Clamp(200 / Math.Max(1, firstTokenMs), 0, 1), 1);

    private static double FirstTokenMs(JsonElement reply) => reply.GetProperty("time_to_first_token_s").GetDouble() * 1000;
    private sealed class NoVisibleAnswer(string? message) : Exception(message) { }
    private static BenchmarkResult Failed(BenchmarkTarget target, string error, bool unavailable = false) =>
        new(target.Model, target.ModelName, target.Device, null, 0, 0, 0, 0, 0, error, target.Backend, unavailable);
}
