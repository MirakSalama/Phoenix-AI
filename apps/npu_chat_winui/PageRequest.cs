using System.Text.RegularExpressions;

namespace NpuChat;

internal sealed record PageEvidence(Uri Address, string Text);

internal sealed partial record PageRequest(Uri Address, PageEvidence? Previous = null)
{
    internal static PageRequest? Resolve(string text, PageEvidence? previous)
    {
        if (PageAction().IsMatch(text) && ParseAddress(text) is { } address)
            return new(address);
        return previous is not null && PageFollowup().IsMatch(text.Trim()) ? new(previous.Address, previous) : null;
    }

    internal static Uri? ParseAddress(string text)
    {
        var links = PageAddress().Matches(NormalizeAddress(text));
        if (links.Count != 1) return null;
        string value = links[0].Value.TrimEnd('.', ',', ')', '!', ']', '?');
        if (!value.Contains("://", StringComparison.Ordinal)) value = "https://" + value;
        return ReadHttpsAddress(value);
    }

    internal static string ReplaceAddress(string text, string domain) =>
        PageAddress().Replace(NormalizeAddress(text), domain, 1);

    private static string NormalizeAddress(string text)
    {
        string normalized = WebsiteName().Replace(text, match =>
            match.Groups["intro"].Value + string.Concat(match.Groups["label"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)) + " dot ");
        return SpokenDot().Replace(normalized, ".");
    }

    internal static Uri? ReadHttpsAddress(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var address) && address.Scheme == Uri.UriSchemeHttps &&
        address.IsDefaultPort && address.UserInfo.Length == 0 && !address.IsLoopback &&
        address.HostNameType == UriHostNameType.Dns && address.IdnHost.Contains('.') ? address : null;

    [GeneratedRegex(@"(?<=[a-z0-9])\s+dot\s+(?=[a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SpokenDot();

    [GeneratedRegex(@"(?<intro>\b(?:the )?(?:web ?site|domain)\s*[:,]?\s+)(?<label>[a-z0-9-]+(?: +[a-z0-9-]+){1,2})\s+dot\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WebsiteName();

    [GeneratedRegex(@"(?<![\w@./-])(?:[a-z][a-z0-9+.-]*://[^\s<>""']+|(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+[a-z]{2,63}(?::\d+)?(?:[/?#][^\s<>""']*)?)(?![\w@-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PageAddress();

    [GeneratedRegex(@"\b(?:open|visit|read|browse|review|summari[sz]e|access|check|look at|look up|tell me about|what (?:is|does|do you think of))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PageAction();

    [GeneratedRegex(@"^(?:(?:and|so|okay|also)[,\s]+)*(?:(?:what|how|why|when|where|who)\b.*\b(?:it|its|they|their|that page|the (?:page|site|website))\b|(?:is|are|does|do|did|can|could|would|has|have)\s+(?:it|they|that page|the (?:page|site|website))\b|(?:tell me (?:more )?about|summari[sz]e|explain)\s+(?:it|that|the (?:page|site|website))\b|what about\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PageFollowup();
}
