using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace NpuChat;

internal sealed class ScheduledWorker : IDisposable
{
    private readonly Channel<JsonElement> events = Channel.CreateUnbounded<JsonElement>();
    private readonly LocalEngine engine;
    private readonly Stopwatch loading = Stopwatch.StartNew();
    private int request;
    private bool disposed;
    internal WorkTarget Target { get; }
    internal int Capacity { get; private set; }
    internal double LoadSeconds { get; private set; }
    internal bool HasRequestedReply => request != 0;
    internal Task Stopped => engine.Stopped;
    internal bool Alive => !disposed && !Stopped.IsCompleted;
    internal ContextBreath? Breath => engine.Breath;
    internal ContextBreath? MeasuredPeak => engine.MeasuredPeak;
    internal void ResetBreath() => engine.ResetBreath();

    internal ScheduledWorker(string project, WorkTarget target)
    {
        Target = target;
        engine = new LocalEngine(project, message => events.Writer.TryWrite(message),
            error => events.Writer.TryComplete(new InvalidOperationException(error)), target.Model, target.Gpu,
            cpuOnly: target.Backend == LocalBackend.Cpu, chatOnly: true, contextLimit: target.ContextLimit);
    }

    internal async Task<JsonElement> ReplyAsync(IReadOnlyList<InferenceMessage> messages, int limit, bool thinking,
        Action<JsonElement>? chunk, CancellationToken cancel, bool autoContinue = false, object? schema = null)
    {
        await ReadyAsync(cancel);
        int room = WorkScheduler.ReplyRoom(Capacity, WorkScheduler.EstimateTokens(messages));
        if (room == 0) throw new InvalidOperationException("The worker has no room to answer with the current input.");
        int id = ++request;
        if (schema is not null && Target.Backend != LocalBackend.Npu)
            engine.Send(new { kind = "chat", id, messages, max_tokens = Math.Min(limit, room), response_schema = schema });
        else
            engine.Send(new { kind = "chat", id, messages, max_tokens = Math.Min(limit, room), thinking, auto_continue = autoContinue,
                planned_reply_tokens = limit });
        return await WaitAsync("reply", id, chunk, cancel);
    }

    internal async Task<JsonElement> ToolReplyAsync(IReadOnlyList<object> messages, object[] tools,
        int limit, CancellationToken cancel)
    {
        await ReadyAsync(cancel);
        int id = ++request;
        engine.Send(new { kind = "tool_chat", id, messages, tools, max_tokens = limit });
        return await WaitAsync("reply", id, null, cancel);
    }

    internal async Task ReadyAsync(CancellationToken cancel)
    {
        if (Capacity == 0)
        {
            var ready = await WaitAsync("ready", null, null, cancel);
            Capacity = ready.GetProperty("capacity").GetInt32();
            if (Capacity <= 0) throw new InvalidOperationException("The scheduled worker could not load its model.");
            LoadSeconds = loading.Elapsed.TotalSeconds;
        }
    }

    private async Task<JsonElement> WaitAsync(string expected, int? id, Action<JsonElement>? chunk, CancellationToken token)
    {
        while (true)
        {
            var message = await events.Reader.ReadAsync(token);
            var kind = message.GetProperty("kind").GetString();
            if (kind == "fatal") throw new InvalidOperationException(message.GetProperty("text").GetString());
            if (id.HasValue && (!message.TryGetProperty("id", out var incoming) || incoming.GetInt32() != id.Value))
                continue;
            if (kind == "error") throw new InvalidOperationException(message.GetProperty("text").GetString());
            if (kind == "chunk") chunk?.Invoke(message);
            if (kind == expected) return message;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        engine.Shutdown(stopImmediately: true);
    }
}
