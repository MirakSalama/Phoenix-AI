using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace NpuChat;

internal enum DecisionRoute { Default, Local, Web, Publisher, Page, Documents, Memory, Skill }

internal sealed record ToolDecision(DecisionRoute Route = DecisionRoute.Default, LocalSkill? Skill = null, bool FromModel = true);

internal sealed class DecisionRouter : IDisposable
{
    private const string Checkpoint = "d1-omni-600M-Q8_0.gguf";
    private readonly string project;
    private Process? worker;
    private HttpClient? client;

    internal DecisionRouter(string project) => this.project = project;

    private static string Model(string project) => Path.Combine(PortableRuntime.ModelRoot(project), "d1-omni-600m", Checkpoint);
    private static string Server(string project) => Path.Combine(project, "cache", "llama-vulkan", "llama-server.exe");

    internal static bool IsInstalled(string project) => File.Exists(Model(project)) && File.Exists(Server(project));

    internal async Task<ToolDecision> ChooseAsync(string text, bool canSearch, bool hasPublisher, bool canReadPage,
        bool hasDocuments, bool hasMemory, IReadOnlyList<LocalSkill> skills, CancellationToken cancellation,
        string? localReference = null)
    {
        var elapsed = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        var token = deadline.Token;
        var connection = await StartAsync(token);
        var options = new Dictionary<string, string>
        {
            ["local"] = "Answer from conversation or general knowledge. Includes drafting, editing, hypothetical questions, " +
                        "and discussing a site without asking to fetch it."
        };
        if (canSearch)
            options.Add("web", hasPublisher
                ? "Search the named publisher for matching public information, reporting, articles or updates on a topic."
                : "Find current public information, recent coverage, or independent sources by searching the web.");
        if (canReadPage)
            options.Add("page", "Open and read the provided website or URL itself, then describe or answer from that page.");
        if (hasDocuments)
            options.Add("documents", "Look up the answer in a named or implied local file, itinerary, trip plan, schedule, policy, PDF, notes or knowledge collection. The latest request may refer to a subject from the previous reply.");
        if (hasMemory)
            options.Add("memory", "Recall a personal preference or fact the user previously told Phoenix. This applies only when the latest request asks what the user previously said or preferred.");

        var byOption = new Dictionary<string, LocalSkill>(StringComparer.Ordinal);
        foreach (var skill in skills.Take(4))
        {
            string key = $"skill_{byOption.Count + 1}";
            byOption.Add(key, skill);
            options.Add(key, LocalWorkspace.Limit($"{skill.Name}. {skill.Description}", 230));
        }
        var query = new
        {
            state = localReference is null ? text :
                JsonSerializer.Serialize(new { latest_request = text, previous_reply_for_reference = localReference }),
            questions = new
            {
                route = new { type = "choice", instructions = "What does the user’s latest request ask the assistant to do?", criteria = options }
            }
        };
        using var response = await connection.PostAsJsonAsync("v1/systemone", query, token);
        response.EnsureSuccessStatusCode();
        using var json = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: token)
            ?? throw new JsonException("Empty decision response.");
        var answer = json.RootElement.GetProperty("answers").GetProperty("route");
        string? choice = answer.GetProperty("choice").GetString();
        double confidence = answer.GetProperty("confidence").GetDouble();
        string label = choice is not null && options.ContainsKey(choice) ? choice : "unknown";
        DiagnosticLog.Write("d1-router", $"Choice {label}; confidence={confidence:F3}; web eligible={canSearch}; {elapsed.ElapsedMilliseconds} ms");
        return label switch
        {
            "local" when confidence >= 0.75 => new(DecisionRoute.Local),
            "web" when confidence >= 0.70 => new(hasPublisher ? DecisionRoute.Publisher : DecisionRoute.Web),
            "page" when confidence >= 0.75 => new(DecisionRoute.Page),
            "documents" when confidence >= 0.78 => new(DecisionRoute.Documents),
            "memory" when confidence >= 0.78 => new(DecisionRoute.Memory),
            _ when confidence >= 0.72 && byOption.TryGetValue(label, out var skill) => new(DecisionRoute.Skill, skill),
            _ => new()
        };
    }

    internal async Task<(WorkIntent? Intent, ReplySize? Size, RequestReadiness? Readiness)> ClassifyWorkAsync(
        string text, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var connection = await StartAsync(deadline.Token);
        var query = new
        {
            state = text,
            questions = new
            {
                work = new
                {
                    type = "choice", instructions = "Classify the work needed to answer this user request.",
                    criteria = new
                    {
                        quick = "Greeting, straightforward fact, tiny rewrite, or short routine answer.",
                        standard = "Ordinary explanation, summary, or creative writing with one main task.",
                        deep = "Complex technical reasoning, planning, debugging, or detailed analysis.",
                        split = "Several independent items to analyze or compare before combining the results.",
                        verify = "The user asks to audit, double-check, or independently verify a result or reasoning."
                    }
                },
                readiness = new
                {
                    type = "choice", instructions = "Can the assistant make useful progress on the latest request with the conversation and evidence supplied?",
                    criteria = new
                    {
                        ready = "The request is actionable. General questions, creative work and requests with a reasonable default are ready.",
                        blocked = "A specific essential input, referenced item, or destination is missing and cannot be resolved from context. A short clarifying question is necessary."
                    }
                },
                length = new
                {
                    type = "choice", instructions = "Estimate the length of the final answer the user wants. Judge answer length separately from difficulty.",
                    criteria = new
                    {
                        brief = "One or two sentences, a short fact, or a quick reply.",
                        normal = "A few paragraphs, a short list, or an ordinary explanation.",
                        detailed = "A substantial explanation, step-by-step instructions, or several sections.",
                        extended = "A long article, full draft, extensive code, or a comprehensive report."
                    }
                }
            }
        };
        using var response = await connection.PostAsJsonAsync("v1/systemone", query, deadline.Token);
        response.EnsureSuccessStatusCode();
        using var json = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: deadline.Token)
            ?? throw new JsonException("Empty work classification.");
        var answers = json.RootElement.GetProperty("answers");
        return (ConfidentChoice<WorkIntent>(answers, "work"), ConfidentChoice<ReplySize>(answers, "length"),
            ConfidentChoice<RequestReadiness>(answers, "readiness", 0.75));
    }

    private static T? ConfidentChoice<T>(JsonElement answers, string key, double threshold = 0.65) where T : struct, Enum =>
        answers.TryGetProperty(key, out var answer) && answer.ValueKind == JsonValueKind.Object &&
        answer.TryGetProperty("confidence", out var confidence) && confidence.ValueKind == JsonValueKind.Number &&
        confidence.TryGetDouble(out double score) && score >= threshold &&
        answer.TryGetProperty("choice", out var choice) && choice.ValueKind == JsonValueKind.String &&
        Enum.TryParse<T>(choice.GetString(), true, out var value) && Enum.IsDefined(value) ? value : null;

    private async Task<HttpClient> StartAsync(CancellationToken token)
    {
        if (worker is { HasExited: false } && client is not null) return client;
        Dispose();
        if (!IsInstalled(project)) throw new FileNotFoundException("The d1-omni-600M router is missing.");

        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        string key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var start = new ProcessStartInfo(Server(project))
        {
            WorkingDirectory = project, UseShellExecute = false, CreateNoWindow = true
        };
        foreach (string argument in new[]
        {
            "-m", Model(project), "--device", "none", "-ngl", "0", "--fit", "off",
            "-c", "4096", "-b", "4096", "-ub", "4096", "-np", "1",
            "--host", "127.0.0.1", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--api-key", key, "--offline", "--no-ui", "--log-disable"
        })
            start.ArgumentList.Add(argument);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the d1 router.");
        worker = process;
        DiagnosticLog.Write("d1-router", $"Started CPU worker PID {process.Id}");
        var connection = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/")
        };
        client = connection;
        connection.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (process.HasExited)
                throw new InvalidOperationException($"The d1 router exited with code {process.ExitCode}.");
            try
            {
                using var health = await connection.GetAsync("health", token);
                if (health.IsSuccessStatusCode) return connection;
            }
            catch (HttpRequestException) { }
            await Task.Delay(100, token);
        }
    }

    private Process? Stop()
    {
        client?.Dispose();
        client = null;
        var process = worker;
        worker = null;
        if (process is null) return null;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            DiagnosticLog.Write("d1-router", $"Worker stop: {error.GetType().Name}");
        }
        return process;
    }

    internal async Task StopForMemoryAsync()
    {
        using var process = Stop();
        if (process is null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        { DiagnosticLog.Write("memory", "D1 did not exit within the memory cleanup window."); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        { DiagnosticLog.Write("memory", $"Could not observe D1 exit: {error.GetType().Name}."); }
    }

    public void Dispose() => Stop()?.Dispose();
}
