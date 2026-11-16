using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NpuChat;

internal enum LocalModel { Lfm230, Lfm350, Lfm3, Gemma, Qwen35 }
internal enum LocalBackend { Auto, Npu, Integrated, Discrete, Cpu }
internal sealed record GpuDevice(string Id, string Name, bool Integrated, int FreeMiB);

internal sealed class LocalEngine
{
    private readonly Process process;
    private readonly int pid;
    private readonly string logSource;
    private readonly Action<JsonElement> onEvent;
    private readonly Action<string> onExit;
    private string diagnostic = "";
    private volatile bool closing;
    private volatile int loadedCapacity;
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object sendGate = new();
    private readonly object continuationGate = new();
    private readonly object breathGate = new();
    private int? breathRequest;
    private int breathPrompt, breathBytes;
    private ContextBreath? breath, measuredPeak;
    private readonly Dictionary<int, ContinuationState> continuations = [];
    private const int MaxContinuationRounds = 7;
    private const int MaxTotalReplyTokens = 8192;
    internal Task Stopped => stopped.Task;
    internal ContextBreath? Breath { get { lock (breathGate) return breath; } }
    internal ContextBreath? MeasuredPeak { get { lock (breathGate) return measuredPeak; } }

    internal void ResetBreath()
    {
        lock (breathGate)
        {
            breathRequest = null;
            breath = measuredPeak = null;
        }
    }

    private sealed class ContinuationState(JsonObject request, int limit)
    {
        internal readonly JsonObject Request = request;
        internal readonly int ChunkLimit = Math.Max(limit, ReadInt(request, "planned_reply_tokens"));
        internal readonly int TotalLimit = (int)Math.Min(MaxTotalReplyTokens,
            (long)Math.Max(limit, ReadInt(request, "planned_reply_tokens")) * 4);
        internal readonly StringBuilder Response = new();
        internal readonly StringBuilder SegmentStream = new();
        internal bool Prefill = true;
        internal int Rounds;
        internal int Generated;
        internal int InitialPrompt;
        internal int InitialProcessed;
        internal double FirstTokenSeconds;
        internal int ThinkingTokens;
        internal int TotalPrompt;
        internal double DecodeSeconds;
        internal int TimedTokens;
        internal bool HasThinkingTokens;
        internal JsonObject? LastPart;
    }

    internal static string FindProject()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "experiments", "winui_bridge.py")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("Keep the WinUI build inside the NPU chat project.");
    }

    internal LocalEngine(string project, Action<JsonElement> onEvent, Action<string> onExit,
        LocalModel model = LocalModel.Lfm230, GpuDevice? gpu = null, bool voiceOnly = false,
        bool cpuOnly = false, bool benchmark = false, bool chatOnly = false, int contextLimit = 0)
    {
        this.onEvent = onEvent;
        this.onExit = onExit;
        bool gguf = gpu is not null || cpuOnly;
        bool imported = !ModelLibrary.IsBuiltIn(model);
        if (!gguf && !SupportsNpu(model)) throw new InvalidOperationException("This model uses the CPU or GPU runner.");
        bool builtIn = model == LocalModel.Lfm230 && !gguf;
        string? originalPython = Environment.GetEnvironmentVariable("NPU_CHAT_PYTHON");
        if (gguf && !HasGpuModel(project, model))
            throw new InvalidOperationException("The local GGUF runner or model is missing.");
        string? fusedScript = !gguf ? FusedScript(project, model, originalPython) : null;
        logSource = builtIn ? "lfm/voice" :
            (imported ? $"imported-{(int)model}" : CheckpointName(model)) +
            (cpuOnly ? "-cpu" : gpu is not null ? $"-{gpu.Id}" : fusedScript is null ? "" : "-x8");
        string? python = gguf || voiceOnly ? LocalPython :
            builtIn || fusedScript is not null ? originalPython : IronPython(project);
        if (python is null || !File.Exists(python))
            throw new InvalidOperationException(builtIn ? "Open Run-NPU-Chat-WinUI.bat to load the local NPU runtime."
                : "The IRON runtime has not been installed.");
        var start = new ProcessStartInfo
        {
            FileName = python,
            WorkingDirectory = project,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("-X");
        start.ArgumentList.Add("utf8");
        string script = gguf ? "llama_vulkan_chat.py" : fusedScript ?? (model switch
        {
            LocalModel.Lfm230 => "winui_bridge.py",
            LocalModel.Gemma => "gemma4_npu_chat.py",
            _ => "lfm_iron_chat.py"
        });
        start.ArgumentList.Add(Path.Combine(project, "experiments", script));
        if (imported)
        {
            start.ArgumentList.Add("--model-file");
            start.ArgumentList.Add(GgufPath(project, model));
        }
        else if (gguf || (fusedScript is null && model is (LocalModel.Lfm350 or LocalModel.Lfm3)))
            start.ArgumentList.Add(CheckpointName(model));
        if (gpu is not null)
        {
            start.ArgumentList.Add("--device");
            start.ArgumentList.Add(gpu.Id);
            start.ArgumentList.Add("--device-name");
            start.ArgumentList.Add(gpu.Name);
        }
        if (cpuOnly) start.ArgumentList.Add("--cpu");
        if (voiceOnly) start.ArgumentList.Add("--voice-only");
        if ((benchmark && (builtIn || gguf)) || (chatOnly && builtIn)) start.ArgumentList.Add("--benchmark");
        if (gguf && contextLimit > 0)
        {
            start.ArgumentList.Add("--context-limit");
            start.ArgumentList.Add(contextLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (!gguf && !builtIn && IsPortable(project))
            start.Environment["PHOENIX_NPU_PROGRAMS"] = PortablePrograms(project, model);
        if (!builtIn) start.Environment["NPU_CHAT_DIAGNOSTICS"] = "1";
        DiagnosticLog.Write(logSource, $"Starting worker using {python}");
        process = Process.Start(start) ?? throw new InvalidOperationException("The local engine could not start.");
        pid = process.Id;
        DiagnosticLog.Write(logSource, $"Started worker PID {pid}");
        process.StandardInput.AutoFlush = true;
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                diagnostic = args.Data;
                DiagnosticLog.Write(logSource + " stderr", $"PID {pid} {args.Data}");
            }
        };
        process.BeginErrorReadLine();
        _ = ReadOutputAsync();
    }

    private static string IronPython(string project) =>
        Path.Combine(project, "cache", "gemma-iron-env", "Scripts", "python.exe");

    private static string? FusedScript(string project, LocalModel model, string? python)
    {
        if (model is not (LocalModel.Lfm350 or LocalModel.Lfm3 or LocalModel.Gemma) ||
            !File.Exists(python))
            return null;
        string checkpoint = Path.Combine(PortableRuntime.ModelRoot(project), CheckpointName(model));
        if (!File.Exists(Path.Combine(checkpoint, "model.safetensors")))
            return null;
        string script = model switch
        {
            LocalModel.Lfm350 => "lfm25_x8_350m_chat.py",
            LocalModel.Lfm3 => "lfm3_x8_chat.py",
            LocalModel.Gemma => "gemma4_x8_chat.py",
            _ => throw new ArgumentOutOfRangeException(nameof(model))
        };
        if (!IsPortable(project))
            return File.Exists(Path.Combine(checkpoint, "verified-x8-npu1.json")) ? script : null;
        var programs = PortablePrograms(project, model);
        bool complete = File.Exists(Path.Combine(project, "experiments", script)) &&
            File.Exists(Path.Combine(checkpoint, "tokenizer.json")) &&
            (model == LocalModel.Lfm350 || File.Exists(Path.Combine(checkpoint, "config.json"))) &&
            (model != LocalModel.Gemma || File.Exists(Path.Combine(checkpoint, "chat_template.jinja"))) &&
            new[] { 64, 256, 1024 }.All(tier => new[] { "head", "body" }.All(variant =>
                File.Exists(Path.Combine(programs, $"decode-{tier}-{variant}.xclbin")) &&
                File.Exists(Path.Combine(programs, $"decode-{tier}-{variant}.bin"))));
        return complete ? script : null;
    }

    private static bool IsPortable(string project) => File.Exists(Path.Combine(project, "phoenix-portable.release"));

    private static string PortablePrograms(string project, LocalModel model) =>
        Path.Combine(PortableRuntime.ModelRoot(project), CheckpointName(model), "npu-programs");

    private static string CheckpointName(LocalModel model) => model switch
    {
        LocalModel.Lfm230 => "lfm25-230m",
        LocalModel.Gemma => "gemma4-e2b",
        LocalModel.Lfm350 => "lfm25-350m",
        LocalModel.Lfm3 => "lfm3-1.7b",
        LocalModel.Qwen35 => "qwen3.5-2b",
        _ => throw new ArgumentOutOfRangeException(nameof(model))
    };

    internal static string? LocalPython => Environment.GetEnvironmentVariable("NPU_CHAT_GPU_PYTHON")
        ?? Environment.GetEnvironmentVariable("NPU_CHAT_PYTHON");

    internal static bool HasNpuRuntime => File.Exists(Environment.GetEnvironmentVariable("NPU_CHAT_PYTHON"));
    internal static bool SupportsNpu(LocalModel model) =>
        model is LocalModel.Lfm230 or LocalModel.Lfm350 or LocalModel.Lfm3 or LocalModel.Gemma;

    internal static string GgufPath(string project, LocalModel model)
    {
        if (!ModelLibrary.IsBuiltIn(model)) return ModelLibrary.ImportPath(model);
        string gguf = model switch
        {
            LocalModel.Lfm230 => "LFM2.5-230M-Q4_K_M.gguf",
            LocalModel.Lfm350 => "LFM2.5-350M-Q4_K_M.gguf",
            LocalModel.Lfm3 => "LFM3-1.7B-QAD-Q4_0.gguf",
            LocalModel.Gemma => "gemma-4-E2B-it-Q4_0.gguf",
            LocalModel.Qwen35 => "Qwen3.5-2B-Q4_K_M.gguf",
            _ => throw new ArgumentOutOfRangeException(nameof(model))
        };
        return Path.Combine(PortableRuntime.ModelRoot(project), CheckpointName(model), gguf);
    }

    internal static string WeightPath(string project, LocalModel model, LocalBackend backend) =>
        backend == LocalBackend.Npu
            ? Path.Combine(PortableRuntime.ModelRoot(project), CheckpointName(model), "model.safetensors")
            : GgufPath(project, model);

    internal static long ModelBytes(string project, LocalModel model, LocalBackend backend) =>
        new FileInfo(WeightPath(project, model, backend)).Length;

    internal static bool HasGpuModel(string project, LocalModel model) =>
        File.Exists(LocalPython) &&
            File.Exists(Path.Combine(project, "cache", "llama-vulkan", "llama-server.exe")) &&
            File.Exists(GgufPath(project, model));

    internal static async Task<GpuDevice[]> ProbeGpusAsync(string project, CancellationToken closing)
    {
        if (!File.Exists(LocalPython) ||
            !File.Exists(Path.Combine(project, "cache", "llama-vulkan", "llama-bench.exe")))
            return [];
        var start = new ProcessStartInfo(LocalPython!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WorkingDirectory = project
        };
        start.ArgumentList.Add(Path.Combine(project, "experiments", "llama_vulkan_chat.py"));
        start.ArgumentList.Add("--probe");
        try
        {
            using var probe = Process.Start(start) ?? throw new InvalidOperationException("Could not start Vulkan probe.");
            var output = probe.StandardOutput.ReadToEndAsync();
            var errors = probe.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(closing);
            timeout.CancelAfter(TimeSpan.FromSeconds(23));
            try { await probe.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { if (!probe.HasExited) probe.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                throw;
            }
            if (probe.ExitCode == 0)
                return JsonSerializer.Deserialize<GpuDevice[]>(await output) ?? [];
            DiagnosticLog.Write("hardware", $"Vulkan probe exited {probe.ExitCode}. {await errors}");
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or
                                     System.ComponentModel.Win32Exception or JsonException or OperationCanceledException)
        {
            DiagnosticLog.Write("hardware", $"Vulkan probe: {error.Message}");
        }
        return [];
    }

    internal static bool IsVerified(string project, LocalModel model)
    {
        if (FusedScript(project, model, Environment.GetEnvironmentVariable("NPU_CHAT_PYTHON")) is not null)
            return true;
        if (IsPortable(project)) return false;
        string checkpoint = Path.Combine(PortableRuntime.ModelRoot(project), CheckpointName(model));
        return File.Exists(IronPython(project)) && File.Exists(Path.Combine(checkpoint, "model.safetensors")) &&
            File.Exists(Path.Combine(checkpoint, "verified-npu1.json"));
    }

    internal void Send<T>(T request)
    {
        var command = JsonSerializer.SerializeToElement(request);
        int? continuationId = null;
        if (command.TryGetProperty("kind", out var kind) && kind.GetString() is "stop" or "cancel")
        {
            lock (continuationGate) continuations.Clear();
        }
        else if (command.TryGetProperty("kind", out kind) && kind.GetString() == "chat" &&
                 command.TryGetProperty("auto_continue", out var automatic) && automatic.ValueKind == JsonValueKind.True &&
                 command.TryGetProperty("id", out var id) && id.TryGetInt32(out int requestId) &&
                 command.TryGetProperty("max_tokens", out var maxTokens) && maxTokens.TryGetInt32(out int limit) && limit > 0 &&
                 command.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
        {
            continuationId = requestId;
            var state = new ContinuationState(JsonNode.Parse(command.GetRawText())!.AsObject(), limit);
            lock (continuationGate) continuations[requestId] = state;
        }
        try { WriteCommand(command); }
        catch
        {
            if (continuationId is int id)
                lock (continuationGate) continuations.Remove(id);
            throw;
        }
    }

    private void WriteCommand(JsonElement command)
    {
        StartBreath(command);
        TraceMessage(command, "send");
        lock (sendGate) process.StandardInput.WriteLine(command.GetRawText());
    }

    private void StartBreath(JsonElement command)
    {
        if (!command.TryGetProperty("kind", out var kind)) return;
        if (kind.GetString() is "cancel" or "stop" or "shutdown")
        {
            ResetBreath();
            return;
        }
        int capacity = loadedCapacity;
        if (capacity <= 0 || kind.GetString() is not ("chat" or "tool_chat") ||
            !command.TryGetProperty("id", out var id) || !id.TryGetInt32(out int request) ||
            !command.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array) return;

        int estimated = messages.EnumerateArray().Sum(item =>
            (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("content", out var value) && value.ValueKind == JsonValueKind.String
                ? Encoding.UTF8.GetByteCount(value.GetString() ?? "") : Encoding.UTF8.GetByteCount(item.GetRawText())) / 3 + 12);
        if (command.TryGetProperty("tools", out var tools)) estimated += Encoding.UTF8.GetByteCount(tools.GetRawText()) / 3;
        lock (breathGate)
        {
            if (breathRequest != request) measuredPeak = null;
            breathRequest = request;
            breathBytes = 0;
            breathPrompt = estimated;
            breath = new(capacity, estimated, Estimated: true);
        }
    }

    private void ObserveBreath(JsonElement message)
    {
        if (!message.TryGetProperty("id", out var id) || !id.TryGetInt32(out int request)) return;
        lock (breathGate)
        {
            if (breathRequest != request || breath is not { } current) return;
            switch (message.GetProperty("kind").GetString())
            {
                case "chunk" when message.TryGetProperty("text", out var text):
                    int bytes = Encoding.UTF8.GetByteCount(text.GetString() ?? "");
                    breathBytes = message.TryGetProperty("replace", out var replace) && replace.ValueKind == JsonValueKind.True
                        ? bytes : breathBytes + bytes;
                    breath = new(current.Capacity, breathPrompt + breathBytes / 3, Estimated: true);
                    break;
                case "reply" when message.TryGetProperty("prompt_tokens", out var prompt) && prompt.TryGetInt32(out int input) &&
                                  message.TryGetProperty("generated_tokens", out var generated) && generated.TryGetInt32(out int output):
                    int capacity = message.TryGetProperty("capacity", out var size) && size.TryGetInt32(out int actual) && actual > 0
                        ? actual : current.Capacity;
                    breath = new(capacity, input + output, Estimated: false);
                    if (measuredPeak is null || breath.Level > measuredPeak.Level ||
                        breath.Level == measuredPeak.Level && breath.Percent >= measuredPeak.Percent) measuredPeak = breath;
                    break;
            }
        }
    }

    private static readonly string[] LoggedFields =
        ["id", "progress", "capacity", "dictation", "npu", "npu_ms", "prompt_tokens", "prompt_tokens_processed",
         "generated_tokens", "time_to_first_token_s", "decode_tokens_per_s", "reused_npu_state", "consult_grok",
         "grok_assist", "max_tokens", "thinking", "thinking_tokens", "cache_read_tokens", "cache_write_tokens",
         "generation_stop", "device", "auto_continue", "continuation", "continuation_count", "total_prompt_tokens",
         "continuation_compacted"];

    private void TraceMessage(JsonElement message, string direction)
    {
        if (!message.TryGetProperty("kind", out var type) || type.ValueKind != JsonValueKind.String) return;
        string? kind = type.GetString();
        if (kind == "chunk") return;
        var details = new StringBuilder($"PID {pid} {direction} {kind}");
        foreach (string name in LoggedFields)
            if (message.TryGetProperty(name, out var value)) details.Append($" {name}={value.GetRawText()}");
        if (kind == "reply" && message.TryGetProperty("prompt_tokens", out var prompt) && prompt.TryGetInt32(out int used) &&
            message.TryGetProperty("generated_tokens", out var generated) && generated.TryGetInt32(out int output) &&
            (message.TryGetProperty("capacity", out var capacity) && capacity.TryGetInt32(out int loaded) ? loaded : loadedCapacity) is > 0 and var window)
            details.Append($" context_room={Math.Max(0, window - used - output)}");
        if (kind is "status" or "phase" or "fatal" or "error" && message.TryGetProperty("text", out var text))
            details.Append($" · {text.GetString()}");
        DiagnosticLog.Write(logSource, details.ToString());
    }

    internal void Shutdown(bool stopImmediately = false)
    {
        closing = true;
        DiagnosticLog.Write(logSource, $"Stopping worker PID {pid}; terminate tree={stopImmediately}");
        try
        {
            if (!process.HasExited)
            {
                if (stopImmediately)
                {
                    process.Kill(entireProcessTree: true);
                    DiagnosticLog.Write(logSource, $"Termination requested for PID {pid} and descendants");
                }
                else
                    Send(new { kind = "shutdown" });
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            DiagnosticLog.Write(logSource, $"Worker stop: {error.GetType().Name}: {error.Message}");
        }
        finally
        {
            try { process.StandardInput.Close(); }
            catch (IOException) { }
            catch (InvalidOperationException) { }
        }
    }

    internal async Task StopAsync()
    {
        Shutdown();
        try { await Stopped.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException)
        {
            Shutdown(stopImmediately: true);
            await Stopped.WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    private async Task ReadOutputAsync()
    {
        string lastOutput = "";
        string? exitError = null;
        try
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is string line)
            {
                try
                {
                    using var message = JsonDocument.Parse(line);
                    if (message.RootElement.ValueKind == JsonValueKind.Object &&
                        message.RootElement.TryGetProperty("kind", out _))
                    {
                        TraceMessage(message.RootElement, "received");
                        ObserveBreath(message.RootElement);
                        if (!closing && HandleContinuation(message.RootElement, out var forwarded))
                            onEvent(forwarded);
                    }
                }
                catch (JsonException)
                {
                    if (!string.IsNullOrWhiteSpace(line)) lastOutput = line;
                }
            }
            await process.WaitForExitAsync().ConfigureAwait(false);
            int exitCode = process.ExitCode;
            DiagnosticLog.Write(logSource, $"Worker PID {pid} exited with code {exitCode} (0x{exitCode:X8}); closing={closing}");
            if (exitCode != 0) exitError = $"The {logSource} worker exited with code 0x{exitCode:X8}. See Settings → Open diagnostic logs.";
        }
        catch (IOException error)
        {
            diagnostic = error.Message;
            DiagnosticLog.Write(logSource, $"Worker pipe failed: {error}");
        }
        try
        {
            if (!closing)
                onExit(exitError ?? (string.IsNullOrWhiteSpace(diagnostic) ? lastOutput : diagnostic));
        }
        finally
        {
            process.Dispose();
            stopped.TrySetResult();
        }
    }

    private bool HandleContinuation(JsonElement message, out JsonElement forwarded)
    {
        forwarded = message.Clone();
        if (message.GetProperty("kind").GetString() == "ready" && message.TryGetProperty("capacity", out var capacity))
            loadedCapacity = capacity.GetInt32();
        if (!message.TryGetProperty("id", out var idValue) || !idValue.TryGetInt32(out int id) ||
            !message.TryGetProperty("kind", out var kindValue)) return true;
        string? kind = kindValue.GetString();
        lock (continuationGate)
        {
            if (!continuations.TryGetValue(id, out var state)) return true;
            if (kind == "chunk")
            {
                if (state.Rounds == 0) return true;
                string text = message.TryGetProperty("text", out var chunk) ? chunk.GetString() ?? "" : "";
                bool replace = message.TryGetProperty("replace", out var replaceValue) && replaceValue.ValueKind == JsonValueKind.True;
                if (state.Prefill && !replace) return true;
                if (replace) state.SegmentStream.Clear();
                state.SegmentStream.Append(text);

                forwarded = ReplySnapshot(id, MergeContinuation(state.Response.ToString(),
                    state.SegmentStream.ToString(), state.Prefill));
                return true;
            }
            if (kind == "error")
            {
                if (state.LastPart is { } last && state.Response.Length > 0)
                {
                    if (state.Prefill && !closing)
                    {
                        state.Prefill = false;
                        state.SegmentStream.Clear();
                        if (TryBuildContinuation(state, loadedCapacity, contextFull: true, out var retry, sameRound: true))
                        {
                            try
                            {
                                WriteCommand(retry);
                                forwarded = ReplySnapshot(id, state.Response.ToString());
                                return true;
                            }
                            catch (Exception error) when (error is IOException or InvalidOperationException)
                            {
                                DiagnosticLog.Write(logSource, $"Could not retry continuation for id={id}: {error.GetType().Name}.");
                            }
                        }
                    }
                    DiagnosticLog.Write(logSource, $"Continuation failed for id={id}; preserving the partial answer.");
                    forwarded = CompleteContinuation(state, last, "continuation_error");
                }
                continuations.Remove(id);
                return true;
            }
            if (kind != "reply") return true;

            var part = JsonNode.Parse(message.GetRawText())!.AsObject();
            string response = part["response"]?.GetValue<string>().TrimEnd('\uFFFD') ?? "";
            if (state.Rounds == 0)
            {
                state.InitialPrompt = ReadInt(part, "prompt_tokens");
                state.InitialProcessed = ReadInt(part, "prompt_tokens_processed");
                state.FirstTokenSeconds = part["time_to_first_token_s"]?.GetValue<double>() ?? 0;
            }
            state.TotalPrompt += ReadInt(part, "prompt_tokens");
            int generated = ReadInt(part, "generated_tokens");
            state.Generated += generated;
            string previous = state.Response.ToString();
            string merged = MergeContinuation(previous, response, state.Prefill);
            state.Response.Clear();
            state.Response.Append(merged);
            state.LastPart = part;
            if (part["thinking_tokens"] is JsonValue reasoning && reasoning.TryGetValue<int>(out int thinkingTokens))
            {
                state.HasThinkingTokens = true;
                state.ThinkingTokens += thinkingTokens;
            }
            if (part["decode_tokens_per_s"] is JsonValue speed && speed.TryGetValue<double>(out double rate) && rate > 0 && double.IsFinite(rate))
            {
                state.DecodeSeconds += generated / rate;
                state.TimedTokens += generated;
            }

            string? stop = part["generation_stop"]?.GetValue<string>();
            bool repeated = state.Rounds > 0 && response.Trim().Length >= 48 && merged == previous;
            bool canContinue = stop is "limit" or "context" && merged.Length > previous.Length &&
                state.Rounds < MaxContinuationRounds && state.Generated < state.TotalLimit && !closing;
            // Retain a complete unit when a natural boundary is close, so the next window can redo the partial unit.
            if (stop is "limit" or "context" && CompletePrefix(merged) is { } boundary && boundary.Length >= previous.TrimEnd().Length &&
                boundary.Length < merged.TrimEnd().Length)
            {
                state.Prefill = false;
                state.Response.Clear();
                state.Response.Append(merged = boundary);
            }
            if (canContinue && TryBuildContinuation(state, loadedCapacity, stop == "context", out var next))
            {
                state.Rounds++;
                state.SegmentStream.Clear();
                try
                {
                    WriteCommand(next);
                    DiagnosticLog.Write(logSource, $"Continuing reply id={id}; continuation={state.Rounds}, tokens={state.Generated}/{state.TotalLimit}.");
                    forwarded = ReplySnapshot(id, merged);
                    return true;
                }
                catch (Exception error) when (error is IOException or InvalidOperationException)
                {
                    DiagnosticLog.Write(logSource, $"Could not send continuation for id={id}: {error.GetType().Name}.");
                    continuations.Remove(id);
                    forwarded = CompleteContinuation(state, part, "continuation_error");
                    return true;
                }
            }

            continuations.Remove(id);
            forwarded = state.Rounds == 0 && merged == response ? message.Clone()
                : CompleteContinuation(state, part, repeated ? "repeated" : stop);
            return true;
        }
    }

    private static JsonElement ReplySnapshot(int id, string text) =>
        JsonSerializer.SerializeToElement(new { kind = "chunk", id, text, replace = true });

    private static JsonElement CompleteContinuation(ContinuationState state, JsonObject part, string? stop)
    {
        part["response"] = state.Response.ToString();
        part["generation_stop"] = stop;
        part["context_prompt_tokens"] = ReadInt(part, "prompt_tokens");
        part["context_generated_tokens"] = ReadInt(part, "generated_tokens");
        part["generated_tokens"] = state.Generated;
        part["prompt_tokens"] = state.InitialPrompt;
        part["prompt_tokens_processed"] = state.InitialProcessed;
        part["time_to_first_token_s"] = state.FirstTokenSeconds;
        part.Remove("prompt_tokens_per_s");
        part["total_prompt_tokens"] = state.TotalPrompt;
        part["continuation_count"] = state.Rounds;
        if (state.HasThinkingTokens) part["thinking_tokens"] = state.ThinkingTokens;
        if (state.DecodeSeconds > 0) part["decode_tokens_per_s"] = state.TimedTokens / state.DecodeSeconds;
        return JsonSerializer.SerializeToElement(part);
    }

    private static int ReadInt(JsonObject message, string name) =>
        message[name] is JsonValue value && value.TryGetValue<int>(out int number) ? Math.Max(0, number) : 0;

    private static readonly Regex SectionEnd = new(@"(?m)\|[ \t]*\r?\n|\r?\n[ \t]*\r?\n|[.!?。！？](?=\s|$)");

    internal static string? CompletePrefix(string reply)
    {
        if (reply.Contains("```", StringComparison.Ordinal) || reply.Contains("~~~", StringComparison.Ordinal) ||
            reply.TrimStart().StartsWith('<')) return null;
        if (reply.TrimEnd().EndsWith('|')) return reply.TrimEnd();
        int start = Math.Max(0, reply.Length - 480);
        foreach (Match boundary in SectionEnd.Matches(reply, start).Reverse())
        {
            if (boundary.Length == 1)
            {
                int lineStart = reply.LastIndexOf('\n', boundary.Index) + 1;
                if (reply.AsSpan(lineStart, boundary.Index - lineStart).TrimStart().StartsWith("|", StringComparison.Ordinal))
                    continue;
                if (boundary.Value == "." && boundary.Index == reply.Length - 1 && boundary.Index > 0 &&
                    char.IsDigit(reply[boundary.Index - 1])) continue;
            }
            return reply[..(boundary.Index + boundary.Length)].TrimEnd();
        }
        return null;
    }

    private static string MergeContinuation(string previous, string next, bool prefill)
    {
        if (previous.Length == 0 || next.Length == 0) return previous + next;
        if (next.Trim().Length >= 48 && previous.Contains(next.Trim(), StringComparison.Ordinal)) return previous;
        int maximum = Math.Min(4096, Math.Min(previous.Length, next.Length));
        for (int overlap = maximum; overlap >= 32; overlap--)
        {
            if (string.CompareOrdinal(previous, previous.Length - overlap, next, 0, overlap) == 0 &&
                (overlap == previous.Length || !char.IsLetterOrDigit(previous[previous.Length - overlap - 1])) &&
                (overlap == next.Length || !char.IsLetterOrDigit(next[overlap])))
                return previous + next[overlap..];
        }
        if (!prefill && !char.IsWhiteSpace(previous[^1]) && !char.IsWhiteSpace(next[0])) return previous + "\n" + next;
        return previous + next;
    }

    private static bool TryBuildContinuation(ContinuationState state, int capacity, bool contextFull, out JsonElement command,
        bool sameRound = false)
    {
        command = default;
        int remaining = state.TotalLimit - state.Generated;
        int wanted = Math.Min(state.ChunkLimit, remaining);
        if (wanted <= 0 || capacity <= 0 || state.Request["messages"] is not JsonArray original) return false;
        var context = original.OfType<JsonObject>().Select(item => new InferenceMessage(
            item["role"]!.GetValue<string>(), item["content"]!.GetValue<string>())).ToList();
        string response = state.Response.ToString();
        const string instruction = "Continue the original task from the preceding answer. Keep every finished item and figure unchanged. " +
            "Write only the missing material, starting at a new sentence, list item, or table row. Complete each unit before starting another.";
        var followup = new InferenceMessage("user", instruction);
        int reserve = Math.Min(wanted, 256);
        int extraPrompt = Math.Max(0, state.InitialPrompt - WorkScheduler.EstimateTokens(context));
        int protectedStart = context.FindLastIndex(item => item.Role == "user");
        if (protectedStart < 0) return false;
        // Keep the original task and its evidence. Shorten old turns and the generated prefix first.
        int responseBudget = contextFull ? Math.Min(capacity / 4, 512) : capacity;
        for (;;)
        {
            int beginning = Math.Max(0, response.Length - responseBudget * 3);
            if (beginning > 0 && char.IsLowSurrogate(response[beginning])) beginning++;
            string prefix = response[beginning..];
            var messages = new List<InferenceMessage>(context) { new("assistant", prefix) };
            if (!state.Prefill) messages.Add(followup);
            int room = WorkScheduler.ReplyRoom(capacity, WorkScheduler.EstimateTokens(messages) + extraPrompt);
            if (room >= reserve)
            {
                var next = (JsonObject)state.Request.DeepClone();
                next["messages"] = JsonSerializer.SerializeToNode(messages);
                next["max_tokens"] = Math.Min(wanted, room);
                next["thinking"] = false; // Continue the visible answer without opening another reasoning block.
                next["continuation"] = sameRound ? state.Rounds : state.Rounds + 1;
                next["continuation_compacted"] = beginning > 0 || context.Count != original.Count;
                command = JsonSerializer.SerializeToElement(next);
                return true;
            }
            int first = context[0].Role == "system" ? 1 : 0;
            if (protectedStart > first)
            {
                int remove = Math.Min(2, protectedStart - first);
                context.RemoveRange(first, remove);
                protectedStart -= remove;
            }
            else if (responseBudget > 64)
            {
                responseBudget /= 2;
            }
            else return false;
        }
    }
}
