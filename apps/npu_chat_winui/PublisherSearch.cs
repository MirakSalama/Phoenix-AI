using System.Text.RegularExpressions;

namespace NpuChat;

internal sealed partial record PublisherSearch(string Domain, bool Recent)
{
    internal static PublisherSearch? Parse(string question)
    {
        if (PageRequest.ParseAddress(question) is not { } address) return null;
        bool recent = Freshness().IsMatch(question);
        bool searchesSite = SearchAction().IsMatch(question);
        bool publisherOnly = address.AbsolutePath == "/" && address.Query.Length == 0 && address.Fragment.Length == 0;
        bool asksForResults = publisherOnly && SearchSubject().IsMatch(question) &&
            (recent || ResultsRequest().IsMatch(question));
        if (!searchesSite && !asksForResults) return null;
        return FromAddress(address, recent);
    }

    internal static PublisherSearch? FromAddress(Uri address, bool recent)
    {
        string domain = address.IdnHost.TrimEnd('.');
        if (domain.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            domain.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            domain.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
            domain.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase)) return null;
        if (domain.StartsWith("www.", StringComparison.OrdinalIgnoreCase) && domain.IndexOf('.', 4) >= 0) domain = domain[4..];
        return new(domain, recent);
    }

    internal bool Matches(Uri url)
    {
        if (url.Scheme is not ("https" or "http") || !url.IsDefaultPort || url.UserInfo.Length > 0) return false;
        string host = url.IdnHost.TrimEnd('.');
        return host.Equals(Domain, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + Domain, StringComparison.OrdinalIgnoreCase);
    }

    internal InvalidOperationException NoVerifiedResults(string provider) =>
        new($"{provider} didn’t return results I could verify from {Domain}.");

    [GeneratedRegex(@"\b(?:latest|newest|current|recent|today(?:['’]s)?|this (?:week|month)|right now)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Freshness();

    internal static bool IsRecent(string text) => Freshness().IsMatch(text);

    [GeneratedRegex(@"\b(?:news|stories|headlines|articles|reports|coverage|posts|updates|releases|results|information|documentation|guides|tutorials)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SearchSubject();

    [GeneratedRegex(@"\b(?:search|find|look up|look for)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SearchAction();

    [GeneratedRegex(@"\b(?:get|show|give|pull|list|check)\b.{0,180}\b(?:on|from|about|for)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ResultsRequest();
}
