using System.Net;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NpuChat;

internal sealed record Citation(string Title, Uri Url);
internal sealed record ResearchAnswer(string Text, IReadOnlyList<Citation> Sources, string? SearchSuggestions = null,
    DailyForecast? Forecast = null, string? Alternate = null, CurrencyQuote? Exchange = null, string? WeatherLocation = null,
    PageEvidence? Page = null);
internal sealed record ReplyContent(string Text, IReadOnlyList<Citation> Sources, string? SourceNotes);
internal enum ResearchProvider { Grok, Tavily, Google, Groq }

internal static partial class CloudResearch
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(90) };
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NpuChat");
    private static readonly string ProviderFile = Path.Combine(Folder, "research-provider.txt");
    private static readonly string AssistOffFile = Path.Combine(Folder, "grok-assist.disabled");

    private static string KeyFile(ResearchProvider provider) => Path.Combine(Folder, provider switch
    {
        ResearchProvider.Google => "gemini-key.bin",
        ResearchProvider.Tavily => "tavily-key.bin",
        ResearchProvider.Groq => "groq-key.bin",
        _ => "xai-key.bin"
    });

    internal static string Name(ResearchProvider provider) => provider switch
    {
        ResearchProvider.Google => "Google Search",
        ResearchProvider.Tavily => "Tavily",
        ResearchProvider.Groq => "Groq",
        _ => "Grok"
    };

    internal static ResearchProvider ReadProvider() =>
        File.Exists(ProviderFile) &&
        Enum.TryParse<ResearchProvider>(File.ReadAllText(ProviderFile), out var provider) && Enum.IsDefined(provider)
            ? provider : ResearchProvider.Grok;

    internal static void SaveProvider(ResearchProvider provider) =>
        WriteAtomically(ProviderFile, Encoding.UTF8.GetBytes(provider.ToString()));

    internal static bool AssistEnabled => !File.Exists(AssistOffFile);
    internal static void SetAssistEnabled(bool enabled)
    {
        if (enabled) File.Delete(AssistOffFile);
        else WriteAtomically(AssistOffFile, []);
    }

    [GeneratedRegex(@"^\s*(?:(?:hey[,! ]+|please\s+|(?:can|could|would|will) you\s+))*" +
                    @"(?:(?:search|browse|check|look on) (?:the )?(?:web|internet)|search online|go on the web|" +
                    @"google (?:for|this|that|what|who|where|when|how)|(?:check|search) google|web (?:search|research)|" +
                    @"look (?:this|that|it) up|(?:check|verify) (?:it|this|that) online|" +
                    @"look up\b[^\r\n]{0,100}\b(?:latest|current|recent|today)|" +
                    @"(?:search|look|check|research|find) .{1,160} (?:online|on the web|on the internet|using the web))\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex ResearchRequest();

    internal static bool IsWebRequest(string text) => ResearchRequest().IsMatch(text);

    [GeneratedRegex(@"^\s*(?:please\s+)?(?:(?:can|could|would) you\s+)?(?:ask|use|consult)\s+(?<provider>grok|groq|gemini|tavily|the cloud)\b(?:[\s,:]+(?<question>.+))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NamedResearchRequest();

    internal static (ResearchProvider Provider, string Question)? RequestedProvider(string text, ResearchProvider configured)
    {
        var match = NamedResearchRequest().Match(text);
        if (!match.Success) return null;
        var provider = match.Groups["provider"].Value.ToLowerInvariant() switch
        {
            "grok" => ResearchProvider.Grok,
            "groq" => ResearchProvider.Groq,
            "gemini" => ResearchProvider.Google,
            "tavily" => ResearchProvider.Tavily,
            _ => configured
        };
        return (provider, match.Groups["question"].Value.Trim());
    }

    [GeneratedRegex(@"(?:^\s*|[,.;!?]\s*)(?:please\s+)?(?:keep (?:it|this) (?:local|offline)|stay offline|offline only|" +
                    @"(?:don't|don’t|do not) (?:use (?:grok|groq|the cloud|the web|web search|google search|tavily)|" +
                    @"(?:browse|search|check) (?:the web|the internet|online)))\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OfflineRequest();

    internal static bool IsOfflineRequest(string text) => OfflineRequest().IsMatch(text);

    [GeneratedRegex(@"^\s*(?:(?:what|who|which|when|where|is|are|has|did|tell me|give me)\b[^\r\n]{0,160}" +
                    @"\b(?:last night|(?:weather|news|scores?|headlines|prices?)\s+(?:today|right now)|" +
                    @"(?:weather|forecast)\s+(?:tomorrow|this (?:week|weekend)|next (?:week|weekend)|in|for|at)|" +
                    @"(?:today['’]s|latest|current|most recent)\s+" +
                    @"(?:news|weather|headlines|scores?|prices?|versions?|releases?|updates?|results?|" +
                    @"forecasts?|president|prime minister|exchange rate|drivers?|builds?|status))\b|" +
                    @"(?:today['’]s|latest|current)\s+(?:news|weather|headlines|scores?|prices?|versions?|releases?)\b|" +
                    @"(?:the\s+)?tomorrow['’]s\s+(?:weather|forecast)\b|(?:weather|forecast)\s+(?:tomorrow|in|for|at)\b)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RecentQuestion();

    internal static bool NeedsCurrentData(string text) => RecentQuestion().IsMatch(text);

    [GeneratedRegex(@"[【〖\[]\d+\s*†[^】〗\]\r\n]{1,64}[】〗\]]|[【〖]\d+[】〗]")]
    private static partial Regex ReferenceMarker();

    [GeneratedRegex(@"(?im)^[ \t]*(?:#{1,6}[ \t]+)?(?:\*\*)?(?:sources|references|resources|links)(?:\*\*)?[ \t]*(?::[ \t]*(?:\*\*)?[ \t]*|(?=\r?$))")]
    private static partial Regex ResourceHeading();

    [GeneratedRegex(@"(?<code>\x60{3}[\s\S]*?\x60{3}|\x60[^\x60\r\n]+\x60)|\[(?<title>[^\]\r\n]+)\]\((?<url>https?://(?:[^\s()]|\([^\s()]*\))+)\)|<?(?<url>https?://[^\s<>\[\]""\x60]+)>?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReplyLink();

    [GeneratedRegex(@"(?m)^[ \t]*(?:[-*+]|\d+[.)])[ \t]*(?:\r?\n|$)")]
    private static partial Regex EmptyResourceBullet();

    internal static ReplyContent SeparateResources(string text, IReadOnlyList<Citation>? citations)
    {
        var links = new Dictionary<Uri, Citation>();
        foreach (var source in citations ?? [])
            if (source?.Url is { IsAbsoluteUri: true } url)
                AddCitation(links, url.AbsoluteUri, source.Title);
        var code = ReplyPresentation.CodeBlocks(text).ToArray();

        string ExtractProse(string value, bool inResources)
        {
            string cleaned = ReplyLink().Replace(value, match =>
            {
                if (match.Groups["code"].Success) return match.Value;
                string address = match.Groups["url"].Value;
                string title = match.Groups["title"].Value;
                string url = address;
                if (title.Length == 0)
                {
                    url = address.TrimEnd('.', ',', ';', ':', '!', '?');
                    while (url.EndsWith(')') && url.Count(c => c == ')') > url.Count(c => c == '('))
                        url = url[..^1];
                }
                if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https"))
                    return match.Value;
                AddCitation(links, url, title);
                return inResources ? "" : title.Length > 0 ? title : address[url.Length..];
            });
            return EmptyResourceBullet().Replace(ReferenceMarker().Replace(cleaned, ""), "");
        }

        var heading = ResourceHeading().Matches(text)
            .FirstOrDefault(match => !code.Any(block => block.Start <= match.Index && match.Index < block.End));
        string body = ReplyPresentation.MapProse(heading is null ? text : text[..heading.Index],
            part => ExtractProse(part, inResources: false), keepCode: true).Trim();
        string? notes = heading is not null
            ? ReplyPresentation.MapProse(text[(heading.Index + heading.Length)..],
                part => ExtractProse(part, inResources: true), keepCode: true).Trim()
            : null;
        return new(body, links.Values.ToList(), string.IsNullOrWhiteSpace(notes) ? null : notes);
    }

    internal static string TextForSpeech(string text) => ReplyPresentation.Speech(text);

    internal static string? ReadKey(ResearchProvider provider)
    {
        string file = KeyFile(provider);
        if (!File.Exists(file)) return null;
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(file), null,
                                                               DataProtectionScope.CurrentUser));
    }

    internal static void SaveKey(ResearchProvider provider, string key) =>
        WriteAtomically(KeyFile(provider), ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null,
                                                                DataProtectionScope.CurrentUser));

    private static void WriteAtomically(string file, byte[] data)
    {
        Directory.CreateDirectory(Folder);
        string pending = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(pending, data);
        File.Move(pending, file, overwrite: true);
    }

    internal static void RemoveKey(ResearchProvider provider) => File.Delete(KeyFile(provider));

    internal static async Task<ResearchAnswer> AskAsync(ResearchProvider provider, string question,
        string key, bool spoken, PhoenixTone tone, CancellationToken cancel, PublisherSearch? publisher = null)
    {
        using var request = BuildRequest(provider, question, key, spoken, tone, publisher);
        using var response = await Client.SendAsync(request, cancel);
        if (!response.IsSuccessStatusCode)
        {
            string name = Name(provider);
            throw new InvalidOperationException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => $"{name} denied access. Check the key and account permissions in Settings.",
                HttpStatusCode.TooManyRequests => $"{name} is rate-limited or the account has reached its quota.",
                HttpStatusCode.PaymentRequired => $"{name} requires an account with available credit.",
                _ => $"{name} returned HTTP {(int)response.StatusCode}."
            });
        }
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancel),
                                                           cancellationToken: cancel);
        return ReadAnswer(provider, document.RootElement, publisher);
    }

    internal static async Task<ResearchAnswer> ExchangeRateAsync(CurrencyPair pair, PhoenixTone tone, CancellationToken cancel)
    {
        var source = new Uri($"https://api.frankfurter.dev/v2/rate/{pair.Base}/{pair.Quote}");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        using var response = await Client.GetAsync(source, deadline.Token);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(deadline.Token),
            cancellationToken: deadline.Token);
        return ReadExchangeRate(pair, tone, document.RootElement, source, DateOnly.FromDateTime(DateTime.UtcNow));
    }

    internal static ResearchAnswer ReadExchangeRate(CurrencyPair pair, PhoenixTone tone, JsonElement payload,
        Uri source, DateOnly today)
    {
        var rate = Property(payload, "rate");
        if (ReadString(payload, "base") != pair.Base || ReadString(payload, "quote") != pair.Quote ||
            rate.ValueKind != JsonValueKind.Number || !rate.TryGetDecimal(out var value) || value <= 0 ||
            !DateOnly.TryParseExact(ReadString(payload, "date"), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date) || date > today || date < today.AddDays(-7))
            throw new InvalidOperationException("The currency source returned a mismatched or stale quote.");
        return PresentExchange(new(pair, value, date, source), tone);
    }

    internal static ResearchAnswer PresentExchange(CurrencyQuote quote, PhoenixTone tone)
    {
        string introduction = tone switch
        {
            PhoenixTone.Firm => "",
            PhoenixTone.Chatty => "Here’s what the latest published rate shows. ",
            PhoenixTone.Playful => "Here’s the currency snapshot. ",
            _ => ""
        };
        string amount = quote.Rate.ToString("0.############", CultureInfo.InvariantCulture);
        string day = quote.Date.ToString("MMMM d, yyyy", CultureInfo.GetCultureInfo("en-US"));
        return new($"{introduction}As of {day}, one {quote.Pair.Base} was worth {amount} {quote.Pair.Quote} " +
            "at the published daily reference rate.", [new Citation("Frankfurter reference rate", quote.Source)], Exchange: quote);
    }

    internal static HttpRequestMessage BuildRequest(ResearchProvider provider, string question, string key, bool spoken,
        PhoenixTone tone = PhoenixTone.Friendly, PublisherSearch? publisher = null)
    {
        string instruction = "Research the current question using web search and cite your sources. ";
        if (publisher is not null)
        {
            instruction += $"Use only articles or pages on {publisher.Domain} and its subdomains. " +
                "Search for relevant individual pages and cite their original URLs. If none match, say so. ";
            if (publisher.Recent)
                instruction += "Prefer the newest matching articles and include publication dates when verified. ";
        }
        string style = PhoenixPersonality.Prompt(tone) + (spoken ? " Write for speech. Keep it short." : "");
        if (provider == ResearchProvider.Google)
        {
            var google = new HttpRequestMessage(HttpMethod.Post,
                "https://generativelanguage.googleapis.com/v1beta/models/gemini-3.6-flash:generateContent");
            google.Headers.Add("x-goog-api-key", key);
            google.Content = JsonContent.Create(new
            {
                systemInstruction = new { parts = new[] { new { text = instruction + style } } },
                contents = new[] { new { role = "user", parts = new[] { new { text = question } } } },
                tools = new[] { new { googleSearch = new { } } }
            });
            return google;
        }

        var request = new HttpRequestMessage(HttpMethod.Post, provider switch
        {
            ResearchProvider.Tavily => "https://api.tavily.com/search",
            ResearchProvider.Groq => "https://api.groq.com/openai/v1/responses",
            _ => "https://api.x.ai/v1/responses"
        });
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = provider switch
        {
            ResearchProvider.Tavily => JsonContent.Create(TavilyRequest(question, publisher)),
            ResearchProvider.Groq => JsonContent.Create(new
            {
                model = "openai/gpt-oss-20b",
                reasoning = new { effort = "low" },
                instructions = instruction + style,
                input = question,
                tool_choice = "required",
                tools = new[] { new { type = "browser_search" } }
            }),
            _ => JsonContent.Create(new
            {
                model = "grok-4.7",
                store = false,
                reasoning = new { effort = spoken ? "low" : "medium" },
                instructions = instruction + style,
                input = new[] { new { role = "user", content = question } },
                tools = new[] { publisher is null ? (object)new { type = "web_search" } :
                    new { type = "web_search", filters = new { allowed_domains = new[] { publisher.Domain } } } }
            })
        };
        return request;
    }

    private static Dictionary<string, object> TavilyRequest(string question, PublisherSearch? publisher)
    {
        var request = new Dictionary<string, object>
        {
            ["query"] = question, ["search_depth"] = "basic", ["max_results"] = 5,
            ["include_answer"] = publisher is null ? (object)"basic" : false, ["auto_parameters"] = false
        };
        if (publisher is null) return request;
        request["include_domains"] = new[] { publisher.Domain };
        if (publisher.Recent)
        {
            request["time_range"] = "month";
            request["include_published_date"] = true;
            request["filter_by_published_date"] = true;
        }
        return request;
    }

    internal static ResearchAnswer ReadAnswer(ResearchProvider provider, JsonElement response, PublisherSearch? publisher = null)
    {
        if (publisher is not null && provider == ResearchProvider.Tavily)
            return ReadPublisherResults(response, publisher);
        var answer = provider switch
        {
            ResearchProvider.Google => ReadGoogle(response),
            ResearchProvider.Tavily => ReadTavily(response),
            _ => ReadResponses(provider, response)
        };
        if (publisher is null) return answer;
        var extracted = SeparateResources(answer.Text, answer.Sources);
        // Generated prose can combine claims from several sources. Require grounded citation
        // metadata and reject answers whose prose includes links to a different publisher.
        if (!answer.Sources.Any(source => publisher.Matches(source.Url) && source.Url.AbsolutePath != "/") ||
            extracted.Sources.Any(source => !publisher.Matches(source.Url)))
            throw publisher.NoVerifiedResults(Name(provider));
        return answer with { Text = extracted.Text, Sources = answer.Sources };
    }

    private static ResearchAnswer ReadPublisherResults(JsonElement response, PublisherSearch publisher)
    {
        var articles = new List<(Citation Source, DateTimeOffset? Date, string? Excerpt)>();
        foreach (var result in Elements(response, "results"))
        {
            if (!Uri.TryCreate(ReadString(result, "url"), UriKind.Absolute, out var url) ||
                !publisher.Matches(url) || url.AbsolutePath == "/" || articles.Any(article => article.Source.Url == url)) continue;
            var title = ReadString(result, "title");
            DateTimeOffset? date = DateTimeOffset.TryParse(ReadString(result, "published_date"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var published) ? published : null;
            if (publisher.Recent && (date is null || date.Value < DateTimeOffset.UtcNow.AddDays(-31) ||
                date.Value > DateTimeOffset.UtcNow.AddDays(1))) continue;
            string? excerpt = ReadString(result, "content");
            articles.Add((new Citation(string.IsNullOrWhiteSpace(title) ? url.Host : title, url), date, excerpt));
        }
        if (articles.Count == 0) throw publisher.NoVerifiedResults("Tavily");
        if (publisher.Recent) articles = articles.OrderByDescending(article => article.Date).ToList();
        var selected = articles.Take(5).ToArray();
        return new ResearchAnswer(string.Join("\n\n", selected.Select(article =>
            $"- {article.Source.Title}" +
            (article.Date is { } date ? $" ({date.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)})" : "") +
            (string.IsNullOrWhiteSpace(article.Excerpt) ? "" : $"\n  {LocalWorkspace.Limit(article.Excerpt, 300)}"))),
            selected.Select(article => article.Source).ToArray());
    }

    private static ResearchAnswer ReadResponses(ResearchProvider provider, JsonElement response)
    {
        var paragraphs = new List<string>();
        var links = new Dictionary<Uri, Citation>();
        foreach (var item in Elements(response, "output"))
        {
            if (ReadString(item, "type") != "message") continue;
            foreach (var part in Elements(item, "content"))
            {
                if (ReadString(part, "text") is { Length: > 0 } paragraph)
                    paragraphs.Add(paragraph);
                foreach (var annotation in Elements(part, "annotations"))
                    AddCitation(links, ReadString(annotation, "url"), ReadString(annotation, "title"));
            }
        }
        foreach (var item in Elements(response, "citations"))
        {
            if (item.ValueKind == JsonValueKind.String)
                AddCitation(links, item.GetString(), null);
        }
        if (paragraphs.Count == 0)
            throw new InvalidOperationException($"{Name(provider)} returned no text for that request.");
        return new ResearchAnswer(string.Join("\n\n", paragraphs), links.Values.ToList());
    }

    private static ResearchAnswer ReadGoogle(JsonElement response)
    {
        var candidate = Elements(response, "candidates").FirstOrDefault();
        var parts = Elements(Property(candidate, "content"), "parts")
            .Where(part => Property(part, "thought").ValueKind != JsonValueKind.True)
            .Select(part => ReadString(part, "text"));
        string answer = string.Concat(parts);
        var grounding = Property(candidate, "groundingMetadata");
        var links = new Dictionary<Uri, Citation>();
        foreach (var chunk in Elements(grounding, "groundingChunks"))
        {
            var web = Property(chunk, "web");
            AddCitation(links, ReadString(web, "uri"), ReadString(web, "title"));
        }
        if (string.IsNullOrWhiteSpace(answer))
            throw new InvalidOperationException("Google Search returned no answer for that request.");
        if (links.Count == 0)
            throw new InvalidOperationException("Gemini did not return any Google Search sources. Try a more specific search.");
        return new ResearchAnswer(answer, links.Values.ToList(),
            ReadString(Property(grounding, "searchEntryPoint"), "renderedContent"));
    }

    private static ResearchAnswer ReadTavily(JsonElement response)
    {
        var links = new Dictionary<Uri, Citation>();
        var excerpts = new List<string>();
        foreach (var result in Elements(response, "results"))
        {
            AddCitation(links, ReadString(result, "url"), ReadString(result, "title"));
            if (ReadString(result, "content") is { Length: > 0 } excerpt)
                excerpts.Add(excerpt.Length > 350 ? excerpt[..350] + "…" : excerpt);
        }
        if (ReadString(response, "answer") is { Length: > 0 } answer)
            return new ResearchAnswer(answer, links.Values.ToList());
        if (excerpts.Count == 0)
            throw new InvalidOperationException("Tavily found no results for that request.");
        return new ResearchAnswer(string.Join("\n\n", excerpts.Take(3)), links.Values.ToList());
    }

    private static void AddCitation(Dictionary<Uri, Citation> links, string? rawUrl, string? title)
    {
        if (Uri.TryCreate(rawUrl, UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
            links.TryAdd(url, new Citation(string.IsNullOrWhiteSpace(title) ? url.Host : title, url));
    }

    private static IEnumerable<JsonElement> Elements(JsonElement source, string property)
    {
        var value = Property(source, property);
        return value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : Enumerable.Empty<JsonElement>();
    }

    private static string? ReadString(JsonElement source, string property)
    {
        var value = Property(source, property);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static JsonElement Property(JsonElement source, string property) =>
        source.ValueKind == JsonValueKind.Object && source.TryGetProperty(property, out var value) ? value : default;
}
