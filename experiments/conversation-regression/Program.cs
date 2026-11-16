using System.Text.Json;
using NpuChat;

var pair = new CurrencyPair("EGP", "USD");
const string rateQuestion = "So, Phoenix, can you give me the latest FX rate between EGP and USD.";
const string followUp = "Can you search the web for the latest FX rates and tell me what it was?";
const string missingData = "I’m sorry, but I don’t have real‑time market data or the ability to pull live FX rates.";
Turn[] history = [new("user", "Hey Phoenix, how are you doing today?"), new("assistant", "Hey!"),
    new("user", rateQuestion), new("assistant", missingData)];

var first = ResearchQuery.Resolve(rateQuestion, [], explicitWeb: false);
Check(first.Pair == pair && first.UseProviderLookup(false, true, false), "Current FX routes directly");
Check(ResearchQuery.Resolve("What's the latest exchange rate between Egyptian pounds and US dollars?", [], false).Pair == pair,
    "Spoken currency names resolve to ISO codes");
Check(!first.UseProviderLookup(false, false, false), "Offline respects the web setting");
Check(!first.UseProviderLookup(false, true, true), "Automatic research preserves a local tool match");

Check(CloudResearch.IsWebRequest(followUp), "Recognizes the explicit follow-up");
var follow = ResearchQuery.Resolve(followUp, history, explicitWeb: true);
Check(follow.Pair == pair && follow.UseProviderLookup(true, false, false), "Web follow-up inherits EGP/USD");
Check(ResearchQuery.Resolve("What’s the latest rate?", [..history, new("user", followUp)], false).Pair == pair,
    "Repeated follow-up inherits the pair");
Check(ResearchQuery.Resolve(followUp, [..history, new("user", "Help me name my dog")], true).Clarification is not null,
    "Topic change clears the active pair");
Check(ResearchQuery.Resolve("Search the web for the latest FX rate for gbp", history, true).Clarification is not null,
    "An incomplete new pair needs clarification");
Check(ResearchQuery.Resolve("Find the historical FX rate for EGP/USD in 2023", history, true).Pair is null,
    "Historical research stays on the generic path");
Turn[] clarification = [new("user", followUp),
    new("assistant", ResearchQuery.Resolve(followUp, [], true).Clarification!, "PHOENIX")];
Check(ResearchQuery.ContinuesExplicitLookup("EGP and USD", clarification) &&
    ResearchQuery.Resolve("EGP and USD", clarification, true).Pair == pair, "A clarification preserves permission and direction");

var day = new DateOnly(2026, 11, 10);
var source = new Uri("https://api.frankfurter.dev/v2/rate/EGP/USD");
using var valid = JsonDocument.Parse("""{"base":"EGP","quote":"USD","rate":0.02,"date":"2026-11-10"}""");
var answer = CloudResearch.ReadExchangeRate(pair, PhoenixTone.Friendly, valid.RootElement, source, day);
Check(answer.Text.Contains("November 10, 2026") && answer.Text.Contains("one EGP") &&
    answer.Sources.Single().Url == source, "Reference answer contains the pair, date and source");
foreach (string invalid in new[]
{
    """{"base":"EUR","quote":"USD","rate":1.12,"date":"2026-11-10"}""",
    """{"base":"EGP","quote":"USD","rate":0.02,"date":"2026-10-25"}""",
    """{"base":"EGP","quote":"USD","rate":0.02}""",
    """{"base":"EGP","quote":"USD","rate":0,"date":"2026-11-10"}"""
})
{
    using var payload = JsonDocument.Parse(invalid);
    try
    {
        _ = CloudResearch.ReadExchangeRate(pair, PhoenixTone.Friendly, payload.RootElement, source, day);
    }
    catch (InvalidOperationException) { continue; }
    throw new Exception("Accepted an invalid reference quote.");
}

Check(Enum.GetValues<PhoenixTone>().Select(PhoenixPersonality.Prompt).Distinct().Count() == 4,
    "All four presets have distinct instructions");
Check(Enum.GetValues<PhoenixTone>().Select(tone => PhoenixPersonality.Greeting(history[0].Content, tone))
    .Where(greeting => greeting is not null).Distinct().Count() == 4, "The saved greeting follows all four presets");
Check(PhoenixPersonality.Greeting("Hey Phoenix, how are you doing today? Can you explain XDNA?", PhoenixTone.Friendly) is null,
    "A substantive follow-up still goes to the model");
Check(PhoenixPersonality.UserPrompt("Hey Phoenix, I hope you can hear me.") == "I hope you can hear me.",
    "The user's greeting does not become their name in the model prompt");
Check(PhoenixPersonality.Greeting("Hi, Phoenix!", PhoenixTone.Friendly) is not null,
    "A greeting addressed to Phoenix gets a conversational reply");
Check(PhoenixPersonality.AssistantReply("Hey Phoenix! I'm happy to hear you're here.") == "Hey! I'm happy to hear you're here.",
    "An echoed address is removed from the assistant's reply");
Check(PhoenixPersonality.UserPrompt("Is Hey Phoenix a good wake phrase?") == "Is Hey Phoenix a good wake phrase?" &&
    PhoenixPersonality.AssistantReply("\"Hey Phoenix\" is a possible wake phrase.") == "\"Hey Phoenix\" is a possible wake phrase.",
    "Ordinary and quoted references to Phoenix are preserved");
Check(PhoenixPersonality.CallsForEmpathy("I’m frustrated. The app keeps crashing."),
    "A frustrated user is eligible for a supportive reply");
using var web = CloudResearch.BuildRequest(ResearchProvider.Groq, "A public question", "fixture-key", true, PhoenixTone.Playful);
string body = await web.Content!.ReadAsStringAsync();
Check(body.Contains("A public question") && body.Contains("lightly witty") && body.Contains("Write for speech") &&
    !body.Contains(rateQuestion), "Research gets the chosen tone and just the resolved query");
var resources = CloudResearch.SeparateResources(
    "The country[1†L40-L44]〖1†L58-L60〗 publishes a [daily report](https://example.com/report).\n\n" +
    "**Sources:**\n- https://example.com/report\n- [Data notes](https://example.com/notes)\n- Central bank bulletin, November 2026",
    [new Citation("Daily report", new Uri("https://example.com/report"))]);
Check(resources.Text == "The country publishes a daily report." &&
    resources.Sources.Select(link => link.Title).SequenceEqual(new[] { "Daily report", "Data notes" }) &&
    resources.SourceNotes == "- Central bank bulletin, November 2026", "Source markers, links and notes are separate from the spoken body");
Check(CloudResearch.SeparateResources("See item [1] or use the [x, y] coordinates.", null).Text ==
    "See item [1] or use the [x, y] coordinates.", "Ordinary square brackets remain in the reply");
const string example = "Run \u0060curl https://example.com/api\u0060 to check it.";
Check(CloudResearch.SeparateResources(example, null).Text == example &&
    !CloudResearch.TextForSpeech(example).Contains("https"), "Links in commands stay usable in chat and out of speech");
string formatted = "## Main **message**\r\n> “I’m ready.”\r\n- Use the **short** version.\r\n\r\n" +
    "\u0060\u0060\u0060\u0060c#\r\n// Sources:\r\nConsole.WriteLine(\"hello\");\r\n\u0060\u0060\u0060\r\n\u0060\u0060\u0060\u0060\r\n\r\n" +
    "~~~python\r\nprint('hidden')\r\n~~~\r\n\r\nThen continue.\r\n\r\nSources:\r\n- https://example.com";
var formattedReply = CloudResearch.SeparateResources(formatted, null);
var presented = ReplyPresentation.Split(formattedReply.Text);
Check(presented.Snippets.Count == 2 && presented.Snippets[0].Language == "c#" &&
    presented.Snippets[0].Code.Contains("Console.WriteLine") && presented.Snippets[1].Code == "print('hidden')" &&
    presented.Text.Contains("Then continue.") && !presented.Text.Contains("Console.WriteLine"),
    "Desktop and LAN display retain separate Markdown code snippets");
Check(CloudResearch.TextForSpeech(formattedReply.Text) == "Main message\nI’m ready.\nUse the short version.\nThen continue.",
    "Speech reads prose without formatting, code or resources");
Check(CloudResearch.TextForSpeech("~~~js\nconst x = 1;\n~~~") == "" &&
    CloudResearch.TextForSpeech("Keep this sentence.\n\u0060\u0060\u0060sh\nprintf hello") == "Keep this sentence.",
    "Code-only and incomplete fenced snippets stay silent");
Check(CloudResearch.TextForSpeech("Continue after [reference](https://example.com) and \u0060print('x')\u0060.") == "Continue after and.",
    "Inline code and Markdown links stay silent");
const string weather = "What's the weather tomorrow in alexandria egypt";
const string weatherRefusal = "I don't have access to real-time weather data, but I can help you find reliable sources.";
Turn[] weatherHistory = [new("user", weather), new("assistant", weatherRefusal)];
Check(!ResearchQuery.Resolve(weather, [], false).UseProviderLookup(false, true, false), "Forecasts do not start a cloud provider lookup");
foreach (string weatherFollowUp in new[] { "Check the web for it please", "Check the web" })
{
    var lookup = ResearchQuery.Resolve(weatherFollowUp, weatherHistory, true);
    Check(lookup.Text == weather && lookup.Clarification is null && lookup.Pair is null,
        "Explicit follow-up reuses Alexandria and tomorrow");
}
var weatherRetry = ResearchQuery.Resolve("Tomorrow's weather",
    [..weatherHistory, new("user", "Check the web for it please"), new("assistant", "What would you like me to look up?")], false);
Check(weatherRetry.Text.Contains("alexandria egypt") && WeatherForecast.Resolve(weatherRetry, "Tomorrow's weather", weatherHistory) is { DayOffset: 1 },
    "Weather fragment can recover the location after an old failed search");
Check(!ResearchQuery.Resolve("Check the web for it please", [new("user", "Keep this offline. " + weather)], true)
    .Text.Contains("alexandria", StringComparison.OrdinalIgnoreCase), "An offline prior turn is never reused as a web query");
var askCity = ResearchQuery.Resolve("What's the weather tomorrow?", [], false);
Check(askCity.NeedsWeatherCity, "Weather without a location asks for a city");
var city = ResearchQuery.Resolve("Alexandria, Egypt", [new("user", "What's the weather tomorrow?"),
    new("assistant", askCity.Clarification!, "PHOENIX")], false);
Check(city.Text == "Weather tomorrow in Alexandria, Egypt" && WeatherForecast.Resolve(city, "Alexandria, Egypt", []) is { DayOffset: 1 },
    "Weather clarification preserves the date and location");
var directForecast = WeatherForecast.Resolve(ResearchQuery.Resolve(weather, [], false), weather, []);
Check(directForecast is { Place: "alexandria egypt", DayOffset: 1 } &&
    WeatherForecast.SplitPlace(directForecast.Place) == ("alexandria", "EG"), "Direct forecast geocodes the Egyptian city and uses its tomorrow");
Check(WeatherForecast.Resolve(new("Write a story about the weather in Alexandria"), "Write a story about the weather in Alexandria", []) is null,
    "A creative task does not call the forecast service");
using var forecastJson = JsonDocument.Parse("""
    {"daily":{"time":["2026-11-12","2026-11-13"],"temperature_2m_min":[20,16],"temperature_2m_max":[26,23],"precipitation_probability_max":[0,50]}}
    """);
var forecast = WeatherForecast.ReadDaily(forecastJson.RootElement, "Alexandria, Egypt", 1, new Uri("https://open-meteo.com/en/docs"));
Turn[] forecastConversation = [new("user", weather), new("assistant", "Forecast", Forecast: forecast)];
const string jacket = "Will I need a jacket?";
var jacketRequest = WeatherForecast.Resolve(new(jacket), jacket, forecastConversation);
Check(jacketRequest is { Jacket: true, Previous.Date.Day: 13 } &&
    WeatherForecast.Present(jacketRequest.Previous!, true).Text.Contains("rain jacket"), "Jacket advice keeps the forecast and date");
Check(WeatherForecast.Resolve(new(jacket), jacket, [..forecastConversation, new("assistant", "Let’s talk about something else.")]) is null,
    "A different conversation topic does not silently reuse the forecast");
Check(CloudResearch.RequestedProvider("Ask Grok about tomorrow", ResearchProvider.Groq)?.Provider == ResearchProvider.Grok &&
    CloudResearch.RequestedProvider(weather, ResearchProvider.Grok) is null, "Only a named cloud request overrides direct weather");
Check(ConversationSpeech.ForVoice("The first sentence is complete. The second stops after", true) ==
    "The first sentence is complete. The reply was cut short. The partial answer is in the chat.", "Voice does not read the unfinished tail");
Check(ConversationSpeech.ForVoice("A complete short answer.", false) == "A complete short answer.",
    "Complete short replies are read normally");

var oldChat = Guid.NewGuid();
var privateProfile = new LanProfile("first", new(oldChat, [new("user", "Old chat")],
    [new(Guid.NewGuid(), "I prefer Celsius.")]));
var otherProfile = new LanProfile("other", new(Guid.NewGuid(), [], []));
var local = privateProfile.Workspace;
bool SaveOk() => true;
string Workspace(string input) => local.Apply(WorkspaceRequest.Parse(input) ??
    throw new InvalidOperationException("No workspace request: " + input), SaveOk);
Check(Workspace("What do you remember about me?").Contains("Celsius"), "Legacy LAN notes migrate");
Check(Workspace("Remember that I prefer short briefings.").Contains("Saved"), "Writes a note from chat");
Check(Workspace("Correct memory from I prefer short briefings to I prefer three bullets.").Contains("Updated"), "Updates a note from chat");
Check(Workspace("Remember that I use fixture blue.").Contains("Saved") &&
    Workspace("Forget that I use fixture blue.").Contains("Forgot"), "Writes and removes an exact note");
Check(Workspace("Save a knowledge note called Test manual: The test clock is in Alexandria, Egypt?").Contains("Saved") &&
    Workspace("Search my knowledge for Alexandria").Contains("Egypt?"), "Saves and retrieves the complete user text");
Check(Workspace("Create a skill called Morning briefing that searches the web for tomorrow's weather in Alexandria, Egypt; then use my Celsius preference.").Contains("Saved") &&
    WorkspaceRequest.Parse("Run skill Morning briefing") is { Action: WorkspaceAction.RunEntry, Name: "Morning briefing" } &&
    local.FindSkill("", "Morning briefing") is { PublicQuery: "tomorrow's weather in Alexandria, Egypt" }, "Saves and locates a skill with a scoped public query");
Check(Workspace("Create a tool called Latest forecast that searches the web for weather in Alexandria, Egypt").Contains("Saved") &&
    local.FindSkill("", "Latest forecast") is { Kind: WorkspaceEntryKind.Tool }, "Creates a reusable tool");
Check(Workspace("Create an MCP called Clock that gives me the local time").Contains("draft") &&
    Workspace("Submit MCP Clock").Contains("Submitted") &&
    local.Skills.Any(item => item is { Kind: WorkspaceEntryKind.McpDraft, SubmittedToHost: true }) &&
    local.FindSkill("Clock", null) is null, "MCP draft requires submission and cannot run as a skill");
const string chromeIncident = "I meant by mcp a model context protocol, it should be. In your system. So, can you write an MCP for browser use, for Google Chrome?";
Check(WorkspaceRequest.Parse(chromeIncident) is { Action: WorkspaceAction.CreateEntry, Name: "Chrome Browser", Kind: WorkspaceEntryKind.McpDraft },
    "The exact Chrome incident routes to creation");
Check(WorkspaceRequest.Parse("Use Chrome to read https://example.com/") is { Action: WorkspaceAction.RunEntry, Name: "Chrome Browser", Kind: WorkspaceEntryKind.McpDraft },
    "An explicit Chrome request routes to the approved browser adapter");
Check(WorkspaceRequest.Parse("Explain how to write an MCP for Google Chrome.") is null,
    "Requests for an explanation do not create a server");
const string liveTabsRequest = "Hey Phoenix, I want you to write a tool, a MCP tool, uh for using Google Chrome browsing. " +
    "To do searches and check websites that I have open. Can you create that tool, please?";
Check(WorkspaceRequest.Parse(PhoenixPersonality.UserPrompt(liveTabsRequest)) is { Action: WorkspaceAction.ChromeOpenTabs } &&
    WorkspaceRequest.Parse("Create a Chrome MCP for public pages") is { Action: WorkspaceAction.CreateEntry },
    "The observed open-tabs request surfaces the connector requirement and public-page requests can create the built-in MCP");
Check(!otherProfile.Workspace.Memory.Any() && !otherProfile.Workspace.Knowledge.Any() && !otherProfile.Workspace.Skills.Any(),
    "A second LAN profile has no access to the first profile's data");
Check(!local.BuildContext("Explain binary arithmetic", null, false).Instructions.Contains("Celsius"),
    "Irrelevant memory is not injected into the prompt");
Check(local.BuildContext("Weather in Alexandria, Egypt. Use Celsius.", local.FindSkill("", "Morning briefing"), false)
    .Instructions.Contains("I prefer Celsius."), "A workflow can use both matching memory and a local passage");
Check(WorkspaceRequest.ReadModelCall("[[PHOENIX_TOOL knowledge | Alexandria]]") is { Action: WorkspaceAction.SearchKnowledge } &&
    WorkspaceRequest.ReadModelCall("[[PHOENIX_TOOL remember | Injected note]]") is null, "Model proposals can retrieve private data only");
Check(local.Apply(new(WorkspaceAction.Remember, Text: "Rollback marker"), () => false).Contains("couldn’t") &&
    local.Memory.All(note => note.Text != "Rollback marker"), "Failed writes are rolled back");
LanProfileData? persisted = null;
Check(privateProfile.SelectChat(null, (key, data) => persisted = data) && privateProfile.Data.ChatId != oldChat &&
    privateProfile.Data.SavedChats?.Single().ChatId == oldChat && privateProfile.Workspace.Memory.Any(note => note.Text.Contains("Celsius")),
    "A new chat archives the prior one and keeps profile memory");
var restored = new LanProfile("first", JsonSerializer.Deserialize<LanProfileData>(JsonSerializer.Serialize(persisted))!);
Check(restored.Workspace.Knowledge.Count == 1 && restored.Workspace.Skills.Count == 3 &&
    restored.Workspace.Memory.Any(note => note.Text.Contains("three bullets")), "Profile tools and memory survive serialization");
var scopedServer = new McpServer(Guid.NewGuid(), "Clock", "python", ["fixture"], "", LanProfileKey: "first", ApprovedClock: true);
var desktop = new LocalWorkspace { Servers = [scopedServer, new(Guid.NewGuid(), "Private", "python", [], "")] };
Check(desktop.VisibleServers(null).Count() == 2 && desktop.VisibleServers("first").Single() == scopedServer &&
    !desktop.VisibleServers("other").Any(), "MCP publication stays within its approved LAN profile");

var conversation = new LanProfile("ratings-test", new(Guid.NewGuid(),
    [new("user", "First"), new("assistant", "First reply"), new("user", "Second"), new("assistant", "Second reply")], []));
var originalId = conversation.Data.ChatId;
Check(!conversation.RateReply(0, ReplyRating.Helpful, (_, _) => throw new Exception("Unexpected save")) &&
    !conversation.RateReply(1, (ReplyRating)99, (_, _) => throw new Exception("Unexpected save")), "Only real replies and supported ratings are accepted");
try
{
    conversation.RateReply(1, ReplyRating.Helpful, (_, _) => throw new IOException("Test failure"));
    throw new InvalidOperationException("Failed save should be reported");
}
catch (IOException)
{
    Check(conversation.Data.Turns[1].Rating is null, "A failed rating save leaves the active chat intact");
}
Check(conversation.RateReply(1, ReplyRating.Helpful, (_, data) => persisted = data) &&
    JsonSerializer.Deserialize<LanProfileData>(JsonSerializer.Serialize(persisted))!.Turns[1].Rating == ReplyRating.Helpful,
    "Ratings persist through serialization");
Check(!conversation.BranchChat(10, (_, _) => throw new Exception("Unexpected save")) &&
    conversation.BranchChat(1, (_, data) => persisted = data) && conversation.Data.ChatId != originalId &&
    conversation.Data.CustomTitle == "Branch of First" && conversation.Data.Turns.Count == 2 &&
    conversation.Data.SavedChats!.Single().Turns.Count == 4, "Branch includes the chosen reply and keeps the entire original");
Check(conversation.RateReply(1, null, (_, _) => { }) && conversation.Data.Turns[1].Rating is null &&
    conversation.Data.SavedChats!.Single().Turns[1].Rating == ReplyRating.Helpful, "Branch and original ratings can change independently");
Check(conversation.SelectChat(originalId, (_, _) => { }) && conversation.Data.Turns.Count == 4 &&
    conversation.Data.SavedChats!.Single().CustomTitle == "Branch of First", "Both chats stay available in profile history");

Console.WriteLine("Conversation, weather, workspace, and profile regression checks passed.");
if (args is ["--live"])
{
    var live = await CloudResearch.ExchangeRateAsync(pair, PhoenixTone.Friendly, default);
    Check(live.Sources.Single().Url == source && live.Text.Contains("one EGP"), "Live EGP/USD source validation");
    Console.WriteLine("Live EGP/USD source validation passed.");
}
if (args is ["--forecast-live"])
{
    var live = await WeatherForecast.LookupAsync(directForecast!, default);
    Check(live.Forecast is { Place: "Alexandria, Egypt" } && live.Sources.Count == 1, "Live forecast identifies Alexandria, Egypt");
    var liveJacket = await WeatherForecast.LookupAsync(new(live.Forecast!.Place, 0, true, live.Forecast), default);
    Check(liveJacket.Forecast == live.Forecast, "Jacket follow-up uses the same live forecast");
    Console.WriteLine($"Live forecast and jacket follow-up passed for {live.Forecast.Place} on {live.Forecast.Date:yyyy-MM-dd}.");
}
if (args is ["--mcp", string python, string script])
{
    var clock = scopedServer with { Command = python, Arguments = [script] };
    Check((await McpClient.ListAsync(clock, default)).Any(tool => tool.Name == "local_time"), "MCP tool discovery");
    var result = await McpClient.CallAsync(clock, new("local_time", JsonSerializer.SerializeToElement(new { })), default);
    Check(DateTimeOffset.TryParse(result, out _), "MCP tool returns the local date and time");
    Console.WriteLine("Bundled MCP clock discovery and call passed.");
}
if (args is ["--tool-import", string toolPython, string clockPath, string generatedHost])
    await RepositoryChecks.RunAsync(toolPython, clockPath, generatedHost);
if (args is ["--repo-live", string url])
{
    var scan = await new RepositoryImport().ScanAsync(url, default);
    Check(scan.Findings.Count > 0 && scan.Commit.Length == 40, "Live GitHub scan is pinned and finds supported files");
    Console.WriteLine($"Live GitHub scan {scan.Repository} at {scan.Commit[..12]} found {scan.Skills.Count} skills and {scan.Servers.Count} MCP configurations.");
}
if (args is ["--repo-search-live"])
{
    var discovery = RepositoryImport.ParseDiscovery("Find a Chrome DevTools MCP server on GitHub.")!;
    var repositories = await new RepositoryImport().SearchAsync(discovery, default);
    Check(repositories.Any(repo => repo.Repository.Equals("ChromeDevTools/chrome-devtools-mcp", StringComparison.OrdinalIgnoreCase)),
        "Repository discovery finds the official Chrome DevTools MCP");
    Console.WriteLine("Live GitHub search found the official Chrome DevTools MCP.");
}

static void Check(bool condition, string reason)
{
    if (!condition) throw new Exception(reason);
}

namespace NpuChat
{
    internal enum LocalModel { Lfm230 }
    internal sealed record ContextUsage;

    internal static class DiagnosticLog
    {
        internal static void Write(string area, string message) { }
    }
}
