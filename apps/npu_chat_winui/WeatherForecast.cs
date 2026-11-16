using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NpuChat;

internal sealed record ForecastRequest(string Place, int DayOffset, bool Jacket = false, DailyForecast? Previous = null,
    int? Hours = null, bool HourRange = false);
internal sealed record DailyForecast(string Place, DateOnly Date, double Low, double High, int? RainChance, Uri Source);

internal static partial class WeatherForecast
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(14) };
    private static readonly (string Name, string Code)[] Countries = CultureInfo.GetCultures(CultureTypes.SpecificCultures)
        .Select(culture => new RegionInfo(culture.Name))
        .Select(region => (Name: region.EnglishName, Code: region.TwoLetterISORegionName))
        .Distinct().OrderByDescending(region => region.Name.Length).ToArray();

    [GeneratedRegex(@"\b(?:the day after tomorrow|tomorrow|today)\b(?:['’]s)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Day();

    [GeneratedRegex(@"\b(?:yesterday|historical|history|right now|currently|this week|next week|weekend|hourly|last week|(?:mon|tues|wednes|thurs|fri|satur|sun)day)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OtherPeriod();

    [GeneratedRegex(@"\b(?:jacket|coat|umbrella|raincoat|what (?:should|can) I wear|will it rain)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FollowUp();

    [GeneratedRegex(@"^(?:and\s+)?(?:what about\s+)?(?<day>today|tomorrow|the day after tomorrow)[?!. ]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DayFollowUp();

    [GeneratedRegex(@"^\s*(?:please\s+)?(?:(?:what\b|how\b|tell me\b|give me\b|can you\b|could you\b|check\b|search\b|look up\b|the\b)|(?:today|tomorrow)(?:['’]s)?\s+(?:weather|forecast)\b|(?:weather|forecast)\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForecastQuestion();

    [GeneratedRegex(@"\b(?<when>in (?:the (?:next|coming) )?|(?:over |for |during )?(?:the )?next |over (?:the )?coming |(?:the )?coming )(?<count>\d{1,2}|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve)\s+hours?\b(?:\s+from now)?|\b(?<count>\d{1,2}|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve)\s+hours?\s+from now\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HourPeriod();

    [GeneratedRegex(@"\b(?:weather|forecast)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WeatherWord();

    internal static string? HourPhrase(string text) => HourPeriod().Match(text) is { Success: true } match ? match.Value : null;

    internal static string WithoutHourPhrase(string text) => HourPeriod().Replace(text, "").TrimEnd(' ', ',', '.', '?', '!');

    internal static bool IsHourQuestion(string text) => HourPeriod().IsMatch(text) && WeatherWord().IsMatch(text);

    internal static ForecastRequest? Resolve(ResearchQuery query, string userText, IReadOnlyList<Turn> prior)
    {
        string text = query.Text;
        var last = prior.LastOrDefault();
        string? previousPlace = last?.WeatherLocation ?? last?.Forecast?.Place;
        if (HourPeriod().Match(text) is { Success: true } hour &&
            (IsHourQuestion(text) || previousPlace is not null && WithoutHourPhrase(text).Trim().ToLowerInvariant() is "" or "and" or "what about" or "how about"))
        {
            string? hourlyPlace = ResearchQuery.ForecastPlace(text) ?? previousPlace;
            if (hourlyPlace is null) return null;
            string count = hour.Groups["count"].Value.ToLowerInvariant();
            string[] words = ["", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve"];
            int hours = int.TryParse(count, out int numeric) ? numeric : Array.IndexOf(words, count);
            return new(hourlyPlace, 0, Hours: hours, HourRange: hour.Groups["when"].Value.Trim() is { Length: > 0 } when &&
                !when.Equals("in", StringComparison.OrdinalIgnoreCase));
        }
        if (OtherPeriod().IsMatch(text)) return null;
        string day = Day().Match(text).Value.ToLowerInvariant().TrimEnd('s', '\'', '’');
        int offset = day switch { "the day after tomorrow" => 2, "tomorrow" => 1, _ => 0 };
        if (ForecastQuestion().IsMatch(text) && ResearchQuery.ForecastPlace(text) is { } place)
        {
            string city = Day().Replace(place, "").Trim(' ', ',', '.');
            return city.Length == 0 ? null : new(city, offset);
        }
        if (prior.LastOrDefault() is not { Role: "assistant", Forecast: { } previous }) return null;
        if (DayFollowUp().IsMatch(userText)) return new(previous.Place, offset);
        if (FollowUp().IsMatch(userText) && userText.Length <= 150)
            return day.Length == 0 ? new(previous.Place, 0, true, previous) : new(previous.Place, offset, true);
        return null;
    }

    internal static (string City, string? Country) SplitPlace(string place)
    {
        foreach (var (name, code) in Countries)
        {
            if (place.EndsWith(" " + name, StringComparison.OrdinalIgnoreCase))
                return (place[..^(name.Length + 1)].TrimEnd(' ', ','), code);
            if (place.EndsWith(", " + code, StringComparison.OrdinalIgnoreCase))
                return (place[..^(code.Length + 2)].TrimEnd(), code);
        }
        return (place.Trim(), null);
    }

    internal static async Task<ResearchAnswer> LookupAsync(ForecastRequest request, CancellationToken cancel)
    {
        if (request.Previous is { } previous) return Present(previous, request.Jacket);
        var (city, country) = SplitPlace(request.Place);
        string geocode = "https://geocoding-api.open-meteo.com/v1/search?count=10&language=en&name=" +
            Uri.EscapeDataString(city) + (country is null ? "" : "&countryCode=" + country);
        using var locations = await ReadAsync(new Uri(geocode), cancel);
        if (!locations.RootElement.TryGetProperty("results", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"I couldn’t find {request.Place}. Try giving me the city and country.");
        var choices = rows.EnumerateArray().ToArray();
        if (country is not null)
            choices = choices.Where(row => String(row, "country_code").Equals(country, StringComparison.OrdinalIgnoreCase)).ToArray();
        var exact = choices.Where(row => String(row, "name").Equals(city, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (exact.Length > 0) choices = exact;
        if (choices.Length == 0) throw new InvalidOperationException($"I couldn’t find {request.Place}. Try giving me the city and country.");
        if (choices.Select(row => String(row, "country_code")).Distinct().Count() > 1)
            throw new InvalidOperationException($"There’s more than one place called {city}. Which country should I check?");
        var location = choices[0];
        string label = $"{String(location, "name")}, {String(location, "country")}".Trim(' ', ',');
        double latitude = location.GetProperty("latitude").GetDouble();
        double longitude = location.GetProperty("longitude").GetDouble();
        string endpoint = FormattableString.Invariant($"https://api.open-meteo.com/v1/forecast?latitude={latitude}&longitude={longitude}");
        if (request.Hours is int hours)
        {
            if (hours is < 1 or > 48) throw new InvalidOperationException("Choose a window of 1 to 48 hours for the hourly forecast.");
            var now = DateTimeOffset.UtcNow;
            var hourlySource = new Uri(endpoint + "&hourly=temperature_2m,precipitation_probability&timezone=auto&timeformat=unixtime" +
                $"&forecast_hours={hours + 2}");
            using var hourly = await ReadAsync(hourlySource, cancel);
            return ReadHourly(hourly.RootElement, label, hours, request.HourRange, now, hourlySource);
        }
        var source = new Uri(endpoint +
            "&daily=temperature_2m_min,temperature_2m_max,precipitation_probability_max&timezone=auto&forecast_days=3");
        using var forecast = await ReadAsync(source, cancel);
        return Present(ReadDaily(forecast.RootElement, label, request.DayOffset, source), request.Jacket);
    }

    internal static ResearchAnswer ReadHourly(JsonElement root, string place, int hours, bool range, DateTimeOffset now, Uri source)
    {
        var hourly = root.GetProperty("hourly");
        var times = hourly.GetProperty("time").EnumerateArray().Select(time => DateTimeOffset.FromUnixTimeSeconds(time.GetInt64())).ToArray();
        var temperature = hourly.GetProperty("temperature_2m");
        var precipitation = hourly.GetProperty("precipitation_probability");
        var target = now.AddHours(hours);
        var selected = Enumerable.Range(0, times.Length).Where(i => range && times[i] > now && times[i] <= target).ToArray();
        if (!range && times.Length > 0)
            selected = [Enumerable.Range(0, times.Length).MinBy(i => Math.Abs((times[i] - target).TotalMinutes))];
        if (selected.Length == 0 || !range && Math.Abs((times[selected[0]] - target).TotalMinutes) > 60)
            throw new InvalidOperationException("The weather source didn’t return data for that time window.");
        var zone = TimeZoneInfo.FindSystemTimeZoneById(root.GetProperty("timezone").GetString() ?? "UTC");
        string text = string.Join(" ", selected.Select(i =>
        {
            string time = TimeZoneInfo.ConvertTime(times[i], zone).ToString("h:mm tt", CultureInfo.InvariantCulture);
            double degrees = temperature[i].GetDouble();
            if (!double.IsFinite(degrees)) throw new JsonException("Invalid hourly temperature.");
            string rain = precipitation[i].ValueKind == JsonValueKind.Number && precipitation[i].GetInt32() is >= 0 and <= 100 and var chance
                ? $", with a {chance}% chance of precipitation" : "";
            return FormattableString.Invariant($"At {time} local time, expect {degrees:0.#} degrees Celsius{rain}.");
        }));
        return new($"Hourly forecast for {place}. {text}", [new Citation("Open-Meteo hourly forecast", source)], WeatherLocation: place);
    }

    private static async Task<JsonDocument> ReadAsync(Uri url, CancellationToken cancel)
    {
        using var response = await Client.GetAsync(url, cancel);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancel));
    }

    internal static DailyForecast ReadDaily(JsonElement root, string place, int offset, Uri source)
    {
        var daily = root.GetProperty("daily");
        string? dateText = daily.GetProperty("time")[offset].GetString();
        double low = daily.GetProperty("temperature_2m_min")[offset].GetDouble();
        double high = daily.GetProperty("temperature_2m_max")[offset].GetDouble();
        var rain = daily.GetProperty("precipitation_probability_max")[offset];
        int? chance = rain.ValueKind == JsonValueKind.Number ? rain.GetInt32() : null;
        if (!DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
            !double.IsFinite(low) || !double.IsFinite(high) || low > high || chance is < 0 or > 100)
            throw new JsonException("The forecast contained invalid data.");
        return new(place, date, low, high, chance, source);
    }

    internal static ResearchAnswer Present(DailyForecast forecast, bool jacket)
    {
        string date = forecast.Date.ToString("dddd, MMMM d", CultureInfo.InvariantCulture);
        string temperatures = FormattableString.Invariant($"a low of {forecast.Low:0.#} and a high of {forecast.High:0.#} degrees Celsius");
        string rain = forecast.RainChance is int chance ? $" The highest rain chance is {chance}%." : "";
        string advice = !jacket ? "" : forecast.RainChance >= 45 ? "I’d bring a light rain jacket. " :
            forecast.Low <= 18 ? "I’d bring a light jacket for the cooler hours. " : "A jacket is probably optional. ";
        string first = $"{advice}For {forecast.Place} on {date}, expect {temperatures}.{rain}";
        string second = $"{advice}{date} in {forecast.Place} has {temperatures}.{rain}";
        return new(first, [new Citation("Open-Meteo forecast", forecast.Source)], Forecast: forecast, Alternate: second);
    }

    private static string String(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
