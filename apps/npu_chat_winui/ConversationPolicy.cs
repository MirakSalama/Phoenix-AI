using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace NpuChat;

internal enum PhoenixTone { Friendly, Firm, Chatty, Playful }
internal enum ReplyRating { Helpful = 1, Unhelpful = -1 }

internal sealed record ReplyExecution(string Model, string Device);

internal sealed record Turn([property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content, string? Speaker = null,
    IReadOnlyList<Citation>? Sources = null, DateTimeOffset? GoogleAnswerDate = null, string? SourceNotes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReplyRating? Rating = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DailyForecast? Forecast = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Cutoff = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ToolActivity>? Activity = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ToolServer = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? RepositoryOptions = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CurrencyQuote? Exchange = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? WeatherLocation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ChatAttachment>? Attachments = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReplyExecution? Execution = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PageEvidence? Page = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PublisherContext? Publisher = null)
{
    internal static Turn? ReplyAt(IReadOnlyList<Turn> turns, int index) =>
        index >= 0 && index < turns.Count && turns[index].Role == "assistant" ? turns[index] : null;

    internal static List<Turn>? BranchThrough(IReadOnlyList<Turn> turns, int index) =>
        ReplyAt(turns, index) is null ? null : turns.Take(index + 1).ToList();
}

internal static partial class PhoenixPersonality
{
    [GeneratedRegex(@"^\s*(?:(?:hey|hi|hello)[\s,]+)?(?:phoenix[\s,]+)?how (?:are you|are we)(?: doing)?(?: today)?[?!.]*\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CheckIn();

    [GeneratedRegex(@"^\s*(?<greeting>hey|hi|hello)(?:\s*,\s*|\s+)phoenix(?=\s*[,!?:.—–-]|\s*$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PhoenixAddress();

    internal static string UserPrompt(string text)
    {
        var address = PhoenixAddress().Match(text);
        if (!address.Success) return text;
        string message = AfterAddress(text, address);
        return message.Length > 0 ? message : address.Groups["greeting"].Value;
    }

    internal static string AssistantReply(string text) => PhoenixAddress().Replace(text, "${greeting}", 1);

    private static string AfterAddress(string text, Match address) =>
        text[address.Length..].TrimStart(' ', '\t', '\r', '\n', ',', '!', '?', ':', '.', '—', '–', '-');

    [GeneratedRegex(@"\b(?:i(?:['’]m| am| feel| felt| got)|i['’]ve been)\s+" +
                    @"(?:(?:really|very|so|pretty|a bit|feeling)\s+){0,3}" +
                    @"(?:frustrated|overwhelmed|sad|worried|upset|anxious|scared|stressed|exhausted|hurt|" +
                    @"discouraged|lonely|grieving|angry|devastated|happy|relieved|excited|proud|thrilled)\b|" +
                    @"\bi (?:had|have had|am having|['’]m having) a (?:really |very )?(?:hard|rough|terrible|tough) day\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Feeling();

    internal static string? Greeting(string text, PhoenixTone tone) =>
        !CheckIn().IsMatch(text) && (PhoenixAddress().Match(text) is not { Success: true } address ||
            AfterAddress(text, address).Length != 0) ? null : tone switch
        {
            PhoenixTone.Firm => "Hey. How can I help today?",
            PhoenixTone.Chatty => "Hey, good to hear from you. What’s been going on today?",
            PhoenixTone.Playful => "Hey! Phoenix here. What are we getting into today?",
            _ => "Hey, good to hear from you. How’s your day going?"
        };

    internal static bool CallsForEmpathy(string text) => Feeling().IsMatch(text);

    [GeneratedRegex(@"\b(?:who are you|what(?:['’]s| is) your name|what (?:should|can) i call you|what would be your name)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex NameQuestion();

    [GeneratedRegex(@"\b(?:what|which|list|show)\b[^.!?\r\n]{0,115}\bmodels?\b[^.!?\r\n]{0,65}\b(?:you (?:have|use|run)|phoenix|installed|available|in (?:this|the) app)\b|\b(?:list|show)\s+(?:the\s+)?(?:installed|available|local)\s+models\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex ModelQuestion();

    internal static string Prompt(PhoenixTone tone) =>
        "You are Phoenix, a conversational assistant. Address the user as you. Treat greetings as social openings. " +
        "When the user shares a feeling, acknowledge it in plain language and offer a relevant next step when helpful. " +
        "Match the seriousness of the situation and be honest about what you know and can do. " + (tone switch
        {
            PhoenixTone.Firm => "Be direct, calm, and brief.",
            PhoenixTone.Chatty => "Be warm and conversational, with room for a useful follow-up.",
            PhoenixTone.Playful => "Be upbeat and lightly witty when it fits. Be gentle and clear on serious topics.",
            _ => "Be warm, friendly, and concise."
        });
}

internal sealed record CurrencyPair(string Base, string Quote);
internal sealed record CurrencyQuote(CurrencyPair Pair, decimal Rate, DateOnly Date, Uri Source);

internal sealed partial record ResearchQuery(string Text, CurrencyPair? Pair = null, string? Clarification = null,
    bool IsFxLookup = false, CurrencyQuote? PreviousQuote = null)
{
    private const string WhichCurrencies = "Which two currencies should I look up?";

    internal bool UseProviderLookup(bool explicitWeb, bool directLookupsEnabled, bool localTool) =>
        explicitWeb || PreviousQuote is not null || directLookupsEnabled && !localTool && IsFxLookup;

    private static readonly HashSet<string> CurrencyCodes = CultureInfo.GetCultures(CultureTypes.SpecificCultures)
        .Select(culture => new RegionInfo(culture.Name).ISOCurrencySymbol)
        .Where(code => code is not "XXX" and not "XTS")
        .ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"\b(?:fx|forex|exchange)\s+rates?\b|\bcurrency\s+(?:rate|pair)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FxTerms();

    [GeneratedRegex(@"\b(?:the|latest|current|live|that|this)\s+rates?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RateReference();

    [GeneratedRegex(@"\b(?:latest|current|live|today|right now|most recent)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CurrentTerms();

    [GeneratedRegex(@"\b(?:historical|history|yesterday|last (?:week|month|year)|on \d{4}-\d{2}-\d{2}|in (?:19|20)\d{2})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HistoricalTerms();

    [GeneratedRegex(@"\b(?<base>[A-Z]{3})\s*(?:/|→|\b(?:to|into|versus|vs\.?|and)\b)\s*(?<quote>[A-Z]{3})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PairTerms();

    [GeneratedRegex(@"\b[A-Z]{3}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CurrencyMention();

    [GeneratedRegex(@"\b(?:Egyptian pounds?|(?:U\.?S\.?|American) dollars?|British pounds?|euros?|Japanese yen)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CurrencyNames();

    [GeneratedRegex(@"^\s*(?:(?:and|okay|alright|please|can you|could you|what about|do|go|tell me)\s+)*(?:the\s+)?(?:opposite(?: direction| way)?|reverse (?:it|that|the (?:pair|rate))|other way (?:around|round)|flip (?:it|that|the (?:pair|rate)))[?!. ]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReversePair();

    [GeneratedRegex(@"\b(?<base>[A-Z]{3})\s*(?:['’]s\s+)?(?:is\s+)?(?:worth(?:\s+of)?\s*[.,]?\s*(?:in\s+)?|\s+in\s+)(?<quote>[A-Z]{3})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WorthPair();

    [GeneratedRegex(@"\b(?<quote>[A-Z]{3})\s+(?:per|for)\s+(?:one\s+|1\s+)?(?<base>[A-Z]{3})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PerPair();

    [GeneratedRegex(@"\bone dollar\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OneDollar();

    [GeneratedRegex(@"\b(?:look (?:it|this|that) up|(?:search|look|check) (?:the )?(?:web|internet|online) for (?:it|this|that)|" +
                    @"(?:check|verify) (?:it|this|that) online)\b|^\s*(?:please\s+)?(?:check|search) (?:the )?(?:web|internet)(?:\s+please)?[?!. ]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReferencedLookup();

    private const string WhichWeatherPlace = "Which city should I check the weather for?";

    [GeneratedRegex(@"^\s*(?:and\s+)?(?:what(?:['’]s| is)\s+|tell me\s+)?(?:the\s+)?(?:(?:today['’]s|tomorrow['’]s)\s+(?:weather|forecast)|(?:weather|forecast)(?:\s+(?:today|tomorrow|this week|next week))?)[?!. ]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WeatherFollowUp();

    [GeneratedRegex(@"^\s*(?:please\s+)?(?:check|search|look on)\s+(?:the\s+)?(?:web|internet)\s+for\s+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WeatherSearchPrefix();

    [GeneratedRegex(@"\b(?:weather|forecast)\b[^?\r\n]{0,80}?\b(?:in|for|at)\s+(?!(?:today|tomorrow|this|next)\b)(?<place>[\p{L}\p{M}][\p{L}\p{M}\p{N} .,'’\-]{0,88})[?!. ]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WeatherPlace();

    internal static string? ForecastPlace(string text) =>
        WeatherPlace().Match(WeatherForecast.WithoutHourPhrase(text)) is { Success: true } match &&
        match.Groups["place"].Value.TrimEnd('.', ' ') is var place &&
        !place.Equals("my location", StringComparison.OrdinalIgnoreCase) &&
        !place.Equals("my area", StringComparison.OrdinalIgnoreCase) &&
        !place.Equals("here", StringComparison.OrdinalIgnoreCase) ? place : null;

    internal bool NeedsWeatherCity => Clarification == WhichWeatherPlace;

    [GeneratedRegex(@"^(?!(?:never mind|nevermind|cancel|forget it|stop|what|who|when|where|how|why|please)\b)(?:in\s+)?[\p{L}\p{M}][\p{L}\p{M} .,'’\-]{1,88}$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WeatherPlaceReply();

    internal static bool ContinuesExplicitLookup(string text, IReadOnlyList<Turn> prior)
    {
        if (prior.LastOrDefault() is not { Role: "assistant", Speaker: "PHOENIX" } clarification ||
            prior.LastOrDefault(turn => turn.Role == "user") is not { Content: var previous } ||
            !CloudResearch.IsWebRequest(previous) || CloudResearch.IsOfflineRequest(previous)) return false;
        return clarification.Content == WhichCurrencies && Pairs(text).Length == 1 ||
            clarification.Content == WhichWeatherPlace && WeatherPlaceReply().IsMatch(text.Trim());
    }

    private static string ExpandCurrencies(string text) => CurrencyNames().Replace(text, match =>
        match.Value.Split(' ')[0].ToLowerInvariant() switch
        {
            "egyptian" => "EGP", "british" => "GBP", "euro" or "euros" => "EUR", "japanese" => "JPY", _ => "USD"
        });

    private static CurrencyPair[] Pairs(string text) => PairTerms().Matches(ExpandCurrencies(text))
        .Select(match => new CurrencyPair(match.Groups["base"].Value.ToUpperInvariant(),
            match.Groups["quote"].Value.ToUpperInvariant()))
        .Where(pair => CurrencyCodes.Contains(pair.Base) && CurrencyCodes.Contains(pair.Quote) && pair.Base != pair.Quote)
        .Distinct().Take(2).ToArray();

    internal static ResearchQuery Resolve(string text, IReadOnlyList<Turn> prior, bool explicitWeb)
    {
        var pairs = Pairs(text);
        if (!HistoricalTerms().IsMatch(text) && prior.LastOrDefault() is { Role: "assistant", Exchange: { Rate: > 0 } quote } &&
            !CurrentTerms().IsMatch(text) && !explicitWeb)
        {
            var expanded = ExpandCurrencies(text);
            if (quote.Pair.Base == "USD" || quote.Pair.Quote == "USD") expanded = OneDollar().Replace(expanded, "USD");
            var directed = WorthPair().Match(expanded);
            if (!directed.Success) directed = PerPair().Match(expanded);
            CurrencyPair? followUp = directed.Success
                ? new(directed.Groups["base"].Value.ToUpperInvariant(), directed.Groups["quote"].Value.ToUpperInvariant())
                : pairs.Length == 1 ? pairs[0]
                : ReversePair().IsMatch(text) ? new(quote.Pair.Quote, quote.Pair.Base) : null;
            if (followUp == quote.Pair || followUp == new CurrencyPair(quote.Pair.Quote, quote.Pair.Base))
                return new(text, followUp, IsFxLookup: true, PreviousQuote: quote);
        }
        bool reference = ReferencedLookup().IsMatch(text);
        bool recent = CurrentTerms().IsMatch(text);
        bool replyingWithPair = pairs.Length == 1 &&
            prior.LastOrDefault() is { Role: "assistant", Content: WhichCurrencies, Speaker: "PHOENIX" };
        string? previous = prior.LastOrDefault(turn => turn.Role == "user")?.Content;
        if (previous is not null && !CloudResearch.IsOfflineRequest(previous) &&
            prior.LastOrDefault() is { Role: "assistant", Content: WhichWeatherPlace, Speaker: "PHOENIX" } &&
            WeatherPlaceReply().IsMatch(text.Trim()))
        {
            string place = text.Trim().TrimEnd('.');
            if (place.StartsWith("in ", StringComparison.OrdinalIgnoreCase)) place = place[3..];
            string period = previous.Contains("tomorrow", StringComparison.OrdinalIgnoreCase) ? "tomorrow"
                : previous.Contains("next week", StringComparison.OrdinalIgnoreCase) ? "next week"
                : previous.Contains("this week", StringComparison.OrdinalIgnoreCase) ? "this week" : "today";
            string? hours = WeatherForecast.HourPhrase(previous);
            return new(hours is null ? $"Weather {period} in {place}" : $"Weather in {place}, {hours}");
        }
        string weatherQuestion = explicitWeb ? WeatherSearchPrefix().Replace(text, "", 1) : text;
        if (WeatherFollowUp().IsMatch(weatherQuestion))
        {
            var subject = prior.Reverse().Where(turn => turn.Role == "user").Take(2)
                .Select(turn => turn.Content).SkipWhile(candidate => ReferencedLookup().IsMatch(candidate))
                .FirstOrDefault();
            if (subject is not null && !CloudResearch.IsOfflineRequest(subject) &&
                WeatherPlace().Match(subject) is { Success: true } weather)
                return new($"{weatherQuestion.TrimEnd('?', '!', '.', ' ')} in {weather.Groups["place"].Value.TrimEnd('.', ' ')}");
            return new(text, Clarification: WhichWeatherPlace);
        }
        if (WeatherForecast.IsHourQuestion(text) && ForecastPlace(text) is null &&
            prior.LastOrDefault() is not { WeatherLocation.Length: > 0 } and not { Forecast: not null })
            return new(text, Clarification: WhichWeatherPlace);
        var earlierPairs = PreviousCurrencyPairs(prior);
        if (!HistoricalTerms().IsMatch(text) && (replyingWithPair ||
            (recent || explicitWeb) && (FxTerms().IsMatch(text) || RateReference().IsMatch(text) &&
                (pairs.Length > 0 || earlierPairs.Length > 0) || reference && earlierPairs.Length > 0)))
        {
            if (pairs.Length == 0 &&
                !CurrencyMention().Matches(ExpandCurrencies(text)).Any(match => CurrencyCodes.Contains(match.Value.ToUpperInvariant())))
                pairs = earlierPairs;
            return pairs.Length == 1 ? new(text, Pair: pairs[0], IsFxLookup: true)
                : new(text, Clarification: WhichCurrencies, IsFxLookup: true);
        }
        if (explicitWeb && reference)
        {
            if (previous is { Length: <= 300 } &&
                (CloudResearch.IsWebRequest(previous) || CloudResearch.NeedsCurrentData(previous) ||
                    WeatherPlace().IsMatch(previous)) &&
                !CloudResearch.IsOfflineRequest(previous) && !ReferencedLookup().IsMatch(previous))
                return WeatherFollowUp().IsMatch(previous)
                    ? new(previous, Clarification: WhichWeatherPlace) : new(previous);
            return new(text, Clarification: "What would you like me to look up?");
        }
        return new(text);
    }

    private static CurrencyPair[] PreviousCurrencyPairs(IReadOnlyList<Turn> prior)
    {
        foreach (var turn in prior.Reverse().Where(turn => turn.Role == "user").Take(4))
        {
            if (CloudResearch.IsOfflineRequest(turn.Content)) break;
            var pairs = Pairs(turn.Content);
            if (pairs.Length > 0) return pairs;
            if (!FxTerms().IsMatch(turn.Content) && !RateReference().IsMatch(turn.Content) &&
                !ReferencedLookup().IsMatch(turn.Content))
                break;
        }
        return [];
    }
}
