using System.Text.Json;
using System.Text.Json.Serialization;

namespace NpuChat;

internal sealed record SavedChat(Guid Id, DateTimeOffset Updated, LocalModel Model, List<Turn> Turns,
    ContextUsage? Context = null, string? CustomTitle = null, string? Device = null)
{
    [JsonIgnore]
    internal string Title => SessionTitle(Turns, CustomTitle);

    [JsonIgnore]
    internal string DisplayTitle => string.IsNullOrWhiteSpace(Device) ? Title : $"{Device} · {Title}";

    internal static string SessionTitle(IReadOnlyList<Turn> turns, string? customTitle)
    {
        if (!string.IsNullOrWhiteSpace(customTitle)) return customTitle;
        if (turns.Count == 0) return "New chat";
        string first = string.Join(" ", turns[0].Content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return first.Length <= 75 ? first : first[..75] + "…";
    }

    internal static string BranchTitle(IReadOnlyList<Turn> turns, string? customTitle) =>
        "Branch of " + LocalWorkspace.Limit(SessionTitle(turns, customTitle), 85);
}

internal sealed class ChatHistory
{
    private readonly string folder;

    internal ChatHistory(string? folder = null) => this.folder = folder ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NpuChat", "Chats");

    internal void Save(SavedChat chat)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"{chat.Id:N}.json");
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(chat));
        File.Move(temporary, path, overwrite: true);
    }

    internal void Delete(Guid id) => File.Delete(Path.Combine(folder, $"{id:N}.json"));

    internal List<SavedChat> ReadAll()
    {
        var chats = new List<SavedChat>();
        if (!Directory.Exists(folder)) return chats;
        var expiry = DateTimeOffset.UtcNow.AddYears(-2);
        foreach (string file in Directory.EnumerateFiles(folder, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<SavedChat>(File.ReadAllText(file)) is not { Turns.Count: > 0 } chat ||
                    !string.Equals(Path.GetFileNameWithoutExtension(file), chat.Id.ToString("N"), StringComparison.OrdinalIgnoreCase) ||
                    !ModelLibrary.IsKnown(chat.Model) || chat.Turns[0]?.Role != "user" ||
                    chat.Turns.Any(turn => turn is null || turn.Content is null || turn.Role is not ("user" or "assistant")))
                    continue;
                if (chat.Turns.Any(turn => turn.GoogleAnswerDate < expiry))
                {
                    for (int i = 0; i < chat.Turns.Count; i++)
                        if (chat.Turns[i].GoogleAnswerDate < expiry)
                            chat.Turns[i] = chat.Turns[i] with { Content = "(Google Search answer expired)", GoogleAnswerDate = null };
                    Save(chat);
                }
                chats.Add(chat);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                DiagnosticLog.Write("history", $"Could not read {Path.GetFileName(file)}: {error.GetType().Name}");
            }
        }
        return chats.OrderByDescending(chat => chat.Updated).ToList();
    }
}
