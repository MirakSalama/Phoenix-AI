using System.Text.RegularExpressions;

namespace NpuChat;

internal enum WorkspaceAction { Remember, Recall, Forget, Correct, SaveKnowledge, SearchKnowledge,
    CreateEntry, UpdateEntry, FindEntry, RunEntry, ListTools, SubmitMcp, ReviewMcp, ActivateMcp, ChromeOpenTabs }

internal sealed partial record WorkspaceRequest(WorkspaceAction Action, string Name = "", string Text = "",
    WorkspaceEntryKind Kind = WorkspaceEntryKind.Skill)
{
    internal const string ModelCall = "[[PHOENIX_TOOL ";
    internal const string ChromeName = "Chrome Browser";

    [GeneratedRegex(@"^(?:please\s+)?(?:/remember\s+|(?:(?:can|could|would) you\s+)?remember(?:\s+(?:that|this:))?\s+)(?<text>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Remember();

    [GeneratedRegex(@"^(?:please\s+)?(?:/forget\s+|forget\s+(?:that|memory)\s+)(?<text>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Forget();

    [GeneratedRegex(@"^(?:please\s+)?(?:correct|update|replace)\s+(?:my |the )?memory\s+(?:from\s+)?(?<name>.+?)\s+(?:to|with)\s+(?<text>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Correct();

    [GeneratedRegex(@"^(?:please\s+)?(?:what (?:do you remember|have you saved)(?: about(?:\s+me)?(?:\s+(?<name>.+))?)?|recall(?:\s+(?:my |the )?memory)?(?:\s+(?:about )?(?<name>.+))?|list (?:my |the )?memor(?:y|ies))$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Recall();

    [GeneratedRegex(@"^(?:please\s+)?(?:(?:save|add|create|update)\s+(?:(?:this|a|an|the)\s+)?(?:knowledge(?:\s+(?:note|entry))?|document|rag)(?:\s+(?:named|called|as))?\s+|/save\s+)(?<name>[^:\r\n]+?)(?:\s+(?:from|with|containing)\s+|\s*[:|]\s*)(?<text>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex SaveKnowledge();

    [GeneratedRegex(@"^(?:please\s+)?(?:(?:find|search|check|look in)\s+(?:my |the )?(?:knowledge(?: base)?|saved documents?|rag)(?:\s+for)?|/rag)\s+(?<name>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SearchKnowledge();

    [GeneratedRegex(@"^(?:please\s+)?(?:(?:can|could|would) you\s+)?(?<verb>create|make|save|write|build|implement|update|find|show|list|use|run)\s+(?:(?:a|an|my|the)\s+)?(?<kind>skill|tool|mcp(?:\s+server)?)(?:\s+(?:named|called))?(?:\s+(?<name>.+?))?(?:\s*(?:\||:)\s*|\s+(?:that|to)\s+)(?<text>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex EntryWithText();

    [GeneratedRegex(@"^(?:please\s+)?(?<verb>find|show|list|use|run)\s+(?:(?:my|the)\s+)?(?<kind>skills?|tools?|mcp(?:\s+servers?)?)(?:\s+(?:named|called))?(?:\s+(?<name>.+))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EntryName();

    [GeneratedRegex(@"^(?:(?:first\s+)?(?:check|search(?:es)?|look on)\s+(?:the )?(?:web|internet)\s+for|search online for)\s+(?<query>[^;\r\n]+?)(?:[.;]\s+(?:then|also)\b.*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex PublicSearch();

    [GeneratedRegex(@"^(?<verb>submit|activate)\s+(?:the\s+)?mcp(?:\s+draft)?\s+(?<name>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex McpDraftCommand();

    [GeneratedRegex(@"^/skill\s+(?<name>[^|\r\n]+)\|\s*(?<text>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex SkillCommand();

    [GeneratedRegex(@"(?:^|[.!?]\s+)(?:(?:so|then),?\s+)?(?:(?:please|(?:can|could|would) you|i want you to)\s+)*(?:create|write|build|make|implement|set up)\s+(?:an?\s+tool\s*,\s*)?(?:an?\s+)?(?:chrome\s+)?mcp(?:\s+(?:server|tool))?\b[^.!?\r\n]{0,260}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChromeMcpRequest();

    [GeneratedRegex(@"\b(?:open (?:chrome )?tabs|(?:tabs|websites|pages)(?: that)? (?:i have|are) open|signed[- ]in (?:chrome|tabs|session)|existing chrome (?:tabs|session))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OpenChromeTabs();

    [GeneratedRegex(@"^(?:please\s+)?(?:(?:can|could|would) you\s+)?(?:use|ask)\s+(?:google\s+)?chrome(?:\s+browser)?\s+(?:to\s+)?(?<text>.*https://.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChromeRead();

    internal static bool IsChromePurpose(string text) =>
        LocalWorkspace.MatchesTrigger(text, "chrome,google chrome,chrome browser");

    internal static WorkspaceRequest? Parse(string text)
    {
        string line = text.Trim();
        string question = line.TrimEnd('?');
        if (Remember().Match(line) is { Success: true } remember)
        {
            string note = Value(remember, "text");
            if (!note.EndsWith('?') || note.StartsWith("not to ", StringComparison.OrdinalIgnoreCase))
            {
                int followUp = note.IndexOf(". Can you write that in your memory", StringComparison.OrdinalIgnoreCase);
                return new(WorkspaceAction.Remember, Text: followUp >= 0 ? note[..(followUp + 1)] : note);
            }
        }
        if (Forget().Match(line) is { Success: true } forget)
            return new(WorkspaceAction.Forget, Text: Value(forget, "text"));
        if (Correct().Match(line) is { Success: true } correct)
            return new(WorkspaceAction.Correct, Value(correct, "name"), Value(correct, "text"));
        if (Recall().Match(question) is { Success: true } recall)
            return new(WorkspaceAction.Recall, Value(recall, "name"));
        if (SaveKnowledge().Match(line) is { Success: true } document)
            return new(WorkspaceAction.SaveKnowledge, Value(document, "name"), Value(document, "text"));
        // Keep the existing /rag path, which supplies retrieved passages to the answer model.
        if (!line.StartsWith("/rag ", StringComparison.OrdinalIgnoreCase) &&
            SearchKnowledge().Match(question) is { Success: true } search)
            return new(WorkspaceAction.SearchKnowledge, Value(search, "name"));
        if (question.Equals("review MCP drafts", StringComparison.OrdinalIgnoreCase))
            return new(WorkspaceAction.ReviewMcp);
        if (McpDraftCommand().Match(question) is { Success: true } mcp)
            return new(Value(mcp, "verb").Equals("submit", StringComparison.OrdinalIgnoreCase)
                ? WorkspaceAction.SubmitMcp : WorkspaceAction.ActivateMcp, Value(mcp, "name").TrimEnd('.'));
        if (SkillCommand().Match(line) is { Success: true } skill)
            return new(WorkspaceAction.RunEntry, Value(skill, "name"), Value(skill, "text"));
        if (ChromeRead().Match(line) is { Success: true } chromeRead)
            return new(WorkspaceAction.RunEntry, ChromeName, Value(chromeRead, "text"), WorkspaceEntryKind.McpDraft);
        var chrome = ChromeMcpRequest().Match(line);
        bool chromeCreation = chrome.Success && IsChromePurpose(line[chrome.Index..]);
        if (chromeCreation && OpenChromeTabs().IsMatch(line[chrome.Index..]))
            return new(WorkspaceAction.ChromeOpenTabs);
        Match entry = EntryWithText().Match(line);
        if (!entry.Success) entry = EntryName().Match(question);
        if (!entry.Success)
        {
            return chromeCreation
                ? new(WorkspaceAction.CreateEntry, ChromeName, "Read public HTTPS pages using Google Chrome.", WorkspaceEntryKind.McpDraft)
                : null;
        }
        string verb = Value(entry, "verb").ToLowerInvariant();
        string kind = Value(entry, "kind").ToLowerInvariant();
        string name = Value(entry, "name").TrimEnd('.');
        string content = Value(entry, "text");
        var entryKind = kind.StartsWith("mcp", StringComparison.Ordinal) ? WorkspaceEntryKind.McpDraft
            : kind.StartsWith("tool", StringComparison.Ordinal) ? WorkspaceEntryKind.Tool : WorkspaceEntryKind.Skill;
        var action = verb switch
        {
            "create" or "make" or "save" or "write" or "build" or "implement" => WorkspaceAction.CreateEntry,
            "update" => WorkspaceAction.UpdateEntry,
            "run" or "use" => WorkspaceAction.RunEntry,
            "list" when entryKind != WorkspaceEntryKind.Skill => WorkspaceAction.ListTools,
            _ => WorkspaceAction.FindEntry
        };
        return new(action, name, content, entryKind);
    }

    internal static string? SearchQuery(string instructions) =>
        PublicSearch().Match(instructions) is { Success: true } search ? Value(search, "query").TrimEnd('.') : null;

    internal static WorkspaceRequest? ReadModelCall(string response)
    {
        string text = response.Trim();
        if (text.Length > 350 || !text.StartsWith(ModelCall, StringComparison.Ordinal) ||
            !text.EndsWith("]]", StringComparison.Ordinal)) return null;
        var parts = text[ModelCall.Length..^2].Split('|', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || parts[1].Length == 0 || parts[1].Any(char.IsControl)) return null;
        return parts[0] switch
        {
            "memory" => new(WorkspaceAction.Recall, parts[1]),
            "knowledge" => new(WorkspaceAction.SearchKnowledge, parts[1]),
            _ => null
        };
    }

    private static string Value(Match match, string key) => match.Groups[key].Value.Trim();
}
