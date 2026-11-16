using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace NpuChat;

internal sealed record LanConversation(Guid ChatId, List<Turn> Turns, List<MemoryNote> Memory, string? CustomTitle = null);

internal sealed record LanProfileData(Guid ChatId, List<Turn> Turns, List<MemoryNote> Memory,
    List<LanConversation>? SavedChats = null, LocalWorkspace? ProfileWorkspace = null, string? CustomTitle = null);

// Conversation state is owned by the UI dispatcher. HTTP requests only resolve a profile.
internal sealed class LanProfile(string key, LanProfileData data)
{
    internal string Key { get; } = key;
    internal LanProfileData Data { get; private set; } = data with { ProfileWorkspace = UpgradeWorkspace(data) };
    internal LocalWorkspace Workspace => Data.ProfileWorkspace!;
    internal WebChatRequest? QueuedTurn { get; set; }
    internal string? Status { get; set; }
    internal int ContextStart { get; set; }

    internal bool SelectChat(Guid? openChatId, Action<string, LanProfileData> save)
    {
        if (openChatId == Data.ChatId) return true;
        var saved = Data.SavedChats ?? [];
        var next = openChatId is Guid id ? saved.Find(chat => chat.ChatId == id) : new LanConversation(Guid.NewGuid(), [], []);
        if (next is null) return false;

        OpenChat(next, save);
        return true;
    }

    internal bool BranchChat(int replyIndex, Action<string, LanProfileData> save)
    {
        if (Turn.BranchThrough(Data.Turns, replyIndex) is not { } turns) return false;
        OpenChat(new LanConversation(Guid.NewGuid(), turns, [], SavedChat.BranchTitle(Data.Turns, Data.CustomTitle)), save);
        return true;
    }

    internal bool RateReply(int replyIndex, ReplyRating? rating, Action<string, LanProfileData> save)
    {
        if (Turn.ReplyAt(Data.Turns, replyIndex) is not { } reply ||
            (rating is { } value && !Enum.IsDefined(value))) return false;
        if (reply.Rating == rating) return true;
        var turns = Data.Turns.ToList();
        turns[replyIndex] = reply with { Rating = rating };
        var rated = Data with { Turns = turns };
        save(Key, rated);
        Data = rated;
        return true;
    }

    private void OpenChat(LanConversation next, Action<string, LanProfileData> save)
    {
        List<LanConversation> archive = (Data.SavedChats ?? []).Where(chat => chat.ChatId != next.ChatId).ToList();
        if (Data.Turns.Count > 0 || Data.Memory.Count > 0)
            archive.Insert(0, new LanConversation(Data.ChatId, Data.Turns, Data.Memory, Data.CustomTitle));
        var selection = new LanProfileData(next.ChatId, next.Turns, next.Memory, archive, Workspace, next.CustomTitle);
        save(Key, selection);
        Data = selection;
        ContextStart = 0;
        Status = null;
    }

    private static LocalWorkspace UpgradeWorkspace(LanProfileData data) =>
        data.ProfileWorkspace ?? new LocalWorkspace
        {
            Memory = data.Memory.Concat((data.SavedChats ?? []).SelectMany(chat => chat.Memory))
                .DistinctBy(note => note.Text, StringComparer.OrdinalIgnoreCase).ToList()
        };
}

internal sealed class LanProfiles
{
    private readonly ConcurrentDictionary<string, LanProfile> profiles = new(StringComparer.Ordinal);
    internal static string Folder => Path.Combine(LocalWorkspace.Folder, "LAN");
    private static string ProfilePath(string key) => Path.Combine(Folder, "Profiles", key + ".json");

    internal string Pair()
    {
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var profile = new LanProfile(Hash(token), new LanProfileData(Guid.NewGuid(), [], []));
        Save(profile);
        profiles.TryAdd(profile.Key, profile);
        return token;
    }

    internal LanProfile? Find(string token)
    {
        if (token.Length != 64 || !token.All(Uri.IsHexDigit)) return null;
        string key = Hash(token);
        if (profiles.TryGetValue(key, out var existing)) return existing;
        string path = ProfilePath(key);
        if (!File.Exists(path)) return null;
        var data = ReadProfile(path);
        return profiles.GetOrAdd(key, _ => new LanProfile(key, data));
    }

    internal static IEnumerable<(string ProfileKey, LocalSkill Draft)> SubmittedMcpDrafts()
    {
        string folder = Path.Combine(Folder, "Profiles");
        if (!Directory.Exists(folder)) yield break;
        foreach (string path in Directory.EnumerateFiles(folder, "*.json"))
        {
            string key = Path.GetFileNameWithoutExtension(path);
            if (key.Length != 64 || !key.All(Uri.IsHexDigit)) continue;
            LanProfileData data;
            try { data = ReadProfile(path); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                DiagnosticLog.Write("workspace", $"Could not read a submitted MCP draft: {error.GetType().Name}");
                continue;
            }
            foreach (var draft in data.ProfileWorkspace?.Skills ?? [])
                if (draft is { Kind: WorkspaceEntryKind.McpDraft, SubmittedToHost: true })
                    yield return (key, draft);
        }
    }

    private static LanProfileData ReadProfile(string path)
    {
        var data = JsonSerializer.Deserialize<LanProfileData>(File.ReadAllText(path));
        if (data is null || !ValidChat(data.ChatId, data.Turns, data.Memory) ||
            (data.ProfileWorkspace is { } tools && (!tools.IsValid() || tools.Servers.Count != 0)) ||
            data.SavedChats?.Any(chat => chat is null || chat.ChatId == data.ChatId ||
                !ValidChat(chat.ChatId, chat.Turns, chat.Memory)) == true)
            throw new JsonException("The LAN profile is invalid.");
        return data;
    }

    private static bool ValidChat(Guid id, List<Turn>? turns, List<MemoryNote>? memory) =>
        id != Guid.Empty && turns is not null && memory is not null &&
        turns.All(turn => turn?.Content is not null && turn.Role is ("user" or "assistant")) &&
        memory.All(note => note?.Text is not null);

    internal static void Save(LanProfile profile) => Save(profile.Key, profile.Data);

    internal static void Save(string key, LanProfileData data)
    {
        string path = ProfilePath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(data));
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Convert.FromHexString(token)));
}
