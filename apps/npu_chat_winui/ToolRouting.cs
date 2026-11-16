using System.Text.RegularExpressions;

namespace NpuChat;

internal static partial class ToolRouting
{
    // D1 chooses among actions the request already authorizes. Do not send local notes,
    // attachments, private chat history or skill contents to a provider.
    internal static bool AllowsWebDecision(string text, bool hasPublisher) =>
        AllowsPublicLookup(text) && (PublicEvidence().IsMatch(text) || hasPublisher && PublisherIntent().IsMatch(text));

    internal static bool AllowsPublicLookup(string text) =>
        !CloudResearch.IsOfflineRequest(text) && !PrivateOrEditing().IsMatch(text);

    internal static bool NamesLocalDocument(string text) => LocalDocumentSource().IsMatch(text);

    [GeneratedRegex(@"\b(?:according to|in|from|does)\s+(?:my|our|the)\s+(?:[\w-]+\s+){0,3}(?:itinerar(?:y|ies)|trip plan|schedule|documents?|files?|notes?|pdfs?|handbook)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LocalDocumentSource();

    internal static string? PublicQuestion(string text, PublisherContext? previousPublicSearch) =>
        Reference().IsMatch(text) ? previousPublicSearch?.Question : text;

    internal static string? LocalReference(string text, Turn? previous) =>
        previous is { Role: "assistant", Content.Length: > 0, GoogleAnswerDate: null } && ConversationalReference().IsMatch(text)
            ? LocalWorkspace.Limit(previous.Content, 420) : null;

    [GeneratedRegex(@"\b(?:does|did|is|was|would|could|can|has|have)\s+(?:it|that|they|those)\b|" +
                    @"\b(?:that|the same)\s+(?:one|option|activity|event|item|place|result|document)\b|" +
                    @"^\s*(?:and\s+)?(?:what about|how about|tell me more about)\s+(?:it|that|those)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConversationalReference();

    [GeneratedRegex(@"\b(?:do not|don['’]t|never|without)\s+(?:use|search|browse|go|check|look|consult|send|share)\b|\b(?:offline|stay local|private|(?:saved|imported|uploaded) (?:files|documents|notes)|(?:my|our) (?:files|documents|notes|messages|data|account)|pasted text|rewrite|rephrase|translate|draft|write (?:me )?(?:an? |the )?(?:email|message|prompt))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrivateOrEditing();

    [GeneratedRegex(@"\b(?:online|on the (?:web|internet)|(?:independent|public|credible|reliable|official|external) (?:sources?|reporting|coverage|reports?)|(?:has|have) (?:anyone|any outlets|any publications) (?:reported|covered|written))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PublicEvidence();

    [GeneratedRegex(@"\b(?:wrote|written|writing|reported|reporting|publish|published|covered|covering|saying|headlines|roundup|developments)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PublisherIntent();

    [GeneratedRegex(@"\b(?:substantiate|verify|check|confirm|corroborate|look up|look into)\s+(?:it|this|that)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Reference();
}

internal sealed partial record PublisherContext(PublisherSearch Search, string Question)
{
    internal PublisherContext? Followup(string text)
    {
        if (!SourceFollowup().IsMatch(text.Trim()) || PageRequest.ParseAddress(text) is not { } address ||
            address.AbsolutePath != "/" || address.Query.Length != 0 || address.Fragment.Length != 0 ||
            PublisherSearch.FromAddress(address, Search.Recent) is not { } replacement) return null;
        return new(replacement, PageRequest.ReplaceAddress(Question, replacement.Domain));
    }

    [GeneratedRegex(@"^(?:(?:and|okay|now)[,\s]+)*(?:what about|how about|try|from|on|the same (?:for|from|on))\s+(?:(?:the (?:site|website)|website)\s+)?(?:(?:https://)?[\w-]+(?:\s*(?:\.|\bdot\b)\s*[\w-]+)+)/?(?:\s+instead)?[.!? ]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SourceFollowup();
}
