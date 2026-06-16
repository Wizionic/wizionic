using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static ChatfishApp.Client.Services.WasmConversationStore;

namespace ChatfishApp.Client.Services;

/// <summary>
/// Lightweight content fingerprint for sync manifest / delta comparisons.
/// Fingerprints must match the exact JSON bytes sent over the DataChannel.
/// </summary>
public static class SyncFingerprint
{
    public static string Compute(string contentJson)
    {
        if (string.IsNullOrEmpty(contentJson))
            return "empty";

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(contentJson));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    public static string ForConversation(string convoId, string title, List<ChatMessage> messages) =>
        Compute(ConvoSyncPayload.Serialize(convoId, title, messages));

    public static string ForNote(string noteId, string title, List<ChatMessage> entries) =>
        Compute(NoteSyncPayload.Serialize(noteId, title, entries));
}

/// <summary>Wire format for conversation sync payloads (shared by sync service and fingerprinting).</summary>
public record ConvoSyncPayload(string ConvoId, string Title, List<ChatMessage> Messages, bool? TitleIsCustom = null)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static string Serialize(string convoId, string title, List<ChatMessage> messages, bool? titleIsCustom = null) =>
        JsonSerializer.Serialize(new ConvoSyncPayload(convoId, title, messages, titleIsCustom), JsonOpts);

    public static ConvoSyncPayload? Deserialize(string json) => TryDeserialize(json);

    public static ConvoSyncPayload? TryDeserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith('{'))
            return null;

        try
        {
            var payload = JsonSerializer.Deserialize<ConvoSyncPayload>(json, JsonOpts);
            return string.IsNullOrEmpty(payload?.ConvoId) ? null : payload;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Wire format for whole-item delete sync (conversation or note).</summary>
public record DeleteSyncPayload(string Id, long DeletedAtTicks)
{
    public static string Serialize(string id, long deletedAtTicks) =>
        JsonSerializer.Serialize(new DeleteSyncPayload(id, deletedAtTicks));

    public static DeleteSyncPayload? Deserialize(string json) =>
        JsonSerializer.Deserialize<DeleteSyncPayload>(json);

    public static string AckValue(long deletedAtTicks) => $"deleted:{deletedAtTicks}";
}

/// <summary>Wire format for note sync payloads (shared by sync service and fingerprinting).</summary>
public record NoteSyncPayload(string NoteId, string Title, List<ChatMessage> Entries)
{
    public static string Serialize(string noteId, string title, List<ChatMessage> entries) =>
        JsonSerializer.Serialize(new NoteSyncPayload(noteId, title, entries));

    public static NoteSyncPayload? Deserialize(string json) =>
        JsonSerializer.Deserialize<NoteSyncPayload>(json);
}