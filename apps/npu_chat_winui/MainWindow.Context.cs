using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace NpuChat;

internal sealed record ContextUsage(LocalModel Model, string Device, int Capacity, int Input, int Output,
    int? Reasoning, int? CacheRead, int? CacheWrite, int? WindowUsed = null)
{
    internal int Used => WindowUsed ?? Input + Output;
}

public sealed partial class MainWindow
{
    private ContextUsage? lastContextUsage;
    private ReplyExecution? replyExecution;

    private static int? TokenCount(JsonElement message, string name) =>
        message.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out int count) && count >= 0 ? count : null;

    private static int? ReplyWindowUsage(JsonElement message) =>
        TokenCount(message, "context_prompt_tokens") + TokenCount(message, "context_generated_tokens");

    private void RecordContext(JsonElement message, int limit, LocalModel? model = null, string? device = null,
        bool? thinking = null)
    {
        LocalModel modelUsed = model ?? TurnModel;
        string deviceUsed = device ?? TurnDevice;
        replyExecution = new(ModelName(modelUsed), deviceUsed);
        if (limit <= 0 || TokenCount(message, "prompt_tokens") is not int input ||
            TokenCount(message, "generated_tokens") is not int output) return;
        int? used = ReplyWindowUsage(message);
        if (scheduledTurn is null)
        {
            RememberBreath((secondaryTurn ? modelEngine : engine)?.MeasuredPeak);
            RememberBreath(new(limit, used ?? input + output, Estimated: false));
        }
        if (activeLanProfile is not null) return;
        int? reasoning = TokenCount(message, "thinking_tokens") ?? ((thinking ?? ThinkingRequested) ? null : 0);
        lastContextUsage = new(modelUsed, deviceUsed, limit, input, output,
            reasoning is int thought ? Math.Min(thought, output) : null,
            TokenCount(message, "cache_read_tokens"), TokenCount(message, "cache_write_tokens"), used);
        UpdateContextCapsule();
    }

    private void UpdateContextCapsule()
    {
        if (ContextCapsule is null) return;
        int loaded = selectedModel == LocalModel.Lfm230 && activeBackend == LocalBackend.Npu
            ? capacity : selectedCapacity;
        string loadedDevice = TurnDevice;
        if (warmScheduled is { Alive: true, Capacity: > 0 } worker)
        {
            loaded = worker.Capacity;
            loadedDevice = worker.Target.Device;
        }
        var usage = lastContextUsage;
        int limit = usage?.Capacity ?? loaded;
        int used = usage?.Used ?? 0;
        ContextValue.Text = limit > 0 ? $"{used:N0} / {limit:N0}" : "0 / —";
        ContextProgress.Value = limit > 0 ? Math.Clamp(100d * used / limit, 0, 100) : 0;

        var details = new StackPanel { Spacing = 7 };
        details.Children.Add(new TextBlock
        {
            Text = usage is null ? "Chat context" : $"Last local turn · {ModelName(usage.Model)} · {usage.Device}",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        if (usage is not null)
        {
            details.Children.Add(new TextBlock
            {
                Text = $"Context limit  {limit:N0}\nUsed  {used:N0}   ·   Remaining  {Math.Max(0, limit - used):N0}\n" +
                    $"Input  {usage.Input:N0}   ·   Output  {usage.Output:N0}\n" +
                    $"Answer  {FormatCount(usage.Output - usage.Reasoning)}   ·   Reasoning  {FormatCount(usage.Reasoning)}\n" +
                    $"Cache read (reused input)  {FormatCount(usage.CacheRead)}\n" +
                    $"Cache write (new input)  {FormatCount(usage.CacheWrite)}"
            });
        }
        else
        {
            details.Children.Add(new TextBlock { Text = "Usage appears after the first local reply." });
        }
        details.Children.Add(new TextBlock
        {
            Text = loaded > 0 ? $"Active worker limit  {loaded:N0} · {loadedDevice}" : "Active worker limit  Pending"
        });
        var limits = new (string Model, string Npu, string Published)[]
        {
            ("Model limit", "NPU", "Published"),
            ("LFM2.5 230M", "4,096", "128,000"),
            ("LFM2.5 350M", "1,024", "128,000"),
            ("LFM3 1.7B", "1,024", "262,144"),
            ("Gemma 4 E2B", "1,024", "131,072")
        };
        details.Children.Add(new TextBlock
        {
            Text = string.Join("\n", limits.Select(row => $"{row.Model,-13} {row.Npu,7} {row.Published,9}")),
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 12
        });
        ToolTipService.SetToolTip(ContextCapsule, new ToolTip { Content = details, Placement = PlacementMode.Top });
        AutomationProperties.SetName(ContextCapsule, $"Context tokens {ContextValue.Text}");
        AutomationProperties.SetHelpText(ContextCapsule, string.Join("\n", details.Children.OfType<TextBlock>().Select(line => line.Text)));
    }

    private static string FormatCount(int? count) => count?.ToString("N0") ?? "Unreported";
}
