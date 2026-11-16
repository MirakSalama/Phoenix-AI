namespace NpuChat;

public sealed partial class MainWindow
{
    private async Task<string?> RunQualityPassAsync(ScheduledWorker worker, InferenceMessage[] input, int tokens,
        string role, object schema, CancellationToken cancel)
    {
        try
        {
            var result = await RunScheduledAsync(worker, input, tokens, false, role, false, null, cancel,
                schema: schema, autoContinue: false, timeoutSeconds: 28);
            return ReplyCutoff(result) is null ? result.GetProperty("response").GetString() : null;
        }
        catch (Exception error) when (!cancel.IsCancellationRequested)
        {
            DiagnosticLog.Write("review", $"{role} could not finish on {worker.Target.ShortDevice}: {error.GetType().Name}");
            return null;
        }
    }

    private async Task<(string Text, string? Cutoff)> CheckAnswerAsync(ScheduledWorker writer,
        ScheduledWorker? reviewer, InferenceMessage[] source, TurnRequirements? plan,
        System.Text.Json.JsonElement result, int limit, CancellationToken cancel)
    {
        string draft = result.GetProperty("response").GetString() ?? "";
        string? cutoff = ReplyCutoff(result);
        if (IsLocalToolRequest(draft)) return (draft, cutoff);
        SetPhase(Phase.Thinking, "Checking the draft against your request…");

        async Task<AnswerReview?> ReviewAsync(string answer, string? incomplete)
        {
            var candidates = reviewer is not null ? new[] { reviewer, writer } : new[] { writer };
            foreach (var worker in candidates)
            {
                cancel.ThrowIfCancellationRequested();
                if (!worker.Alive || TurnQuality.ReviewMessages(source, plan, answer, incomplete is not null, worker.Capacity) is not { } input)
                    continue;
                var text = await RunQualityPassAsync(worker, input, TurnQuality.ReviewTokens, "Checking answer",
                    TurnQuality.ReviewSchema, cancel);
                if (text is null || TurnQuality.ReadReview(text, answer, plan) is not { } review) continue;
                Record(new("Answer review", review.Status switch
                {
                    ReviewStatus.Complete => "Reviewed",
                    ReviewStatus.Missing => "Missing work found",
                    ReviewStatus.Incorrect => "Possible error found",
                    _ => "Uncertain"
                }, $"{ModelName(worker.Target.Model)} · {worker.Target.ShortDevice}"));
                return review;
            }
            Record(new("Answer review", "Unavailable"));
            return null;
        }

        bool unexpectedCall = IsUnexpectedToolRequest(draft);
        var initial = unexpectedCall
            ? new AnswerReview(ReviewStatus.Incorrect, "The model emitted an unsupported tool call instead of its answer.")
            : await ReviewAsync(draft, cutoff);
        if (initial?.Status == ReviewStatus.Complete && cutoff is null) return (draft, null);
        if (initial is null && cutoff is null) return (draft, null);

        bool append = initial?.Status != ReviewStatus.Incorrect && (cutoff is not null || initial?.Status == ReviewStatus.Missing);
        string issue = initial?.Issue is { Length: > 0 } concern ? concern : "The answer stopped early. Finish the remaining requested parts.";
        string note = cutoff ?? (append ? "Some requested details may be missing." : "The review could not confirm part of this answer.");
        int repairLimit = Math.Clamp(limit, 256, 1536);
        if (!writer.Alive || TurnQuality.RepairMessages(source, plan, draft, issue, append, repairLimit, writer.Capacity) is not { } messages)
            return (draft, note);

        SetPhase(Phase.Thinking, append ? "Finishing the remaining parts…" : "Revising a possible error…");
        AnnounceWork(append ? "I’ll finish the remaining parts." : "I’ll take a second pass to check the answer.");
        try
        {
            var next = await RunScheduledAsync(writer, messages, repairLimit, false, append ? "Finishing" : "Revising",
                false, null, cancel, schema: TurnQuality.RepairSchema, autoContinue: false, timeoutSeconds: 65);
            string? text = TurnQuality.ReadRepair(next.GetProperty("response").GetString() ?? "");
            if (text is null) return (draft, note);
            string revised = append ? TurnQuality.Join(draft, text) : text.Trim();
            string? nextCutoff = ReplyCutoff(next);
            if (revised == draft || string.IsNullOrWhiteSpace(revised) || IsLocalToolRequest(text) ||
                IsUnexpectedToolRequest(text) || (!append && nextCutoff is not null))
                return (draft, note);
            Record(new("Answer review", append ? "Continued" : "Revised"));
            var final = await ReviewAsync(revised, nextCutoff);
            if (final?.Status != ReviewStatus.Complete || nextCutoff is not null)
                return (revised, nextCutoff ?? "A second review could not confirm the complete answer.");
            return (revised, null);
        }
        catch (Exception error) when (!cancel.IsCancellationRequested)
        {
            DiagnosticLog.Write("review", $"Could not finish the second pass: {error.GetType().Name}");
            return (draft, note);
        }
    }

    private static bool IsLocalToolRequest(string text) =>
        text.TrimStart().StartsWith(WorkspaceRequest.ModelCall, StringComparison.Ordinal) ||
        text.TrimStart().StartsWith(McpClient.CallStart, StringComparison.Ordinal);

    private static bool IsUnexpectedToolRequest(string text) =>
        text.TrimStart().StartsWith("<tool_call>", StringComparison.OrdinalIgnoreCase) ||
        text.TrimStart().StartsWith("<function=", StringComparison.OrdinalIgnoreCase);
}
