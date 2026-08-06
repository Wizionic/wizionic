using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using App.Core.Browser;

namespace App.Core.Storage;

public static class SyncFingerprint
{
    public static string Compute(string contentJson)
    {
        if (string.IsNullOrEmpty(contentJson))
            return "empty";

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(contentJson));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    public static string ForConversation(
        string convoId,
        string title,
        List<ChatMessage> messages,
        bool isPasswordProtected = false,
        long protectionChangedTicks = 0) =>
        Compute(ConvoSyncPayload.Serialize(convoId, title, messages, titleIsCustom: null, isPasswordProtected, protectionChangedTicks));

    public static string ForNote(
        string noteId,
        string title,
        List<ChatMessage> entries,
        bool isPasswordProtected = false,
        long protectionChangedTicks = 0) =>
        Compute(NoteSyncPayload.Serialize(noteId, title, entries, isPasswordProtected, protectionChangedTicks));

    public static string ForBookmark(BrowserBookmark bookmark) =>
        Compute(BookmarkSyncPayload.Serialize(bookmark));

    public static string ForBookmarkFolder(BrowserBookmarkFolder folder) =>
        Compute(BookmarkFolderSyncPayload.Serialize(folder));

    public static string ForSidebarApp(SidebarApp app) =>
        Compute(SidebarAppSyncPayload.Serialize(app));
}

/// <summary>
/// Merges password-protection flags across peers.
/// Never demotes a local lock from a stale or legacy remote false.
/// </summary>
public static class PasswordProtectionSync
{
    /// <summary>
    /// Decide whether to apply a remote protection flag.
    /// </summary>
    /// <returns>True if local store should be updated to <paramref name="applyProtected"/> with <paramref name="applyTicks"/>.</returns>
    public static bool TryResolve(
        bool? remoteProtected,
        long? remoteTicks,
        bool localProtected,
        long localTicks,
        out bool applyProtected,
        out long applyTicks)
    {
        applyProtected = localProtected;
        applyTicks = localTicks;

        // Omitted field (older payload) — never change local lock state.
        if (remoteProtected is null)
            return false;

        var rTicks = remoteTicks is > 0 ? remoteTicks.Value : 0L;
        var lTicks = localTicks > 0 ? localTicks : 0L;

        // Legacy peer (no clock): only escalate to locked; never unlock.
        if (rTicks == 0)
        {
            if (remoteProtected == true && !localProtected)
            {
                applyProtected = true;
                // Stamp so later unlocks on this device can win over other legacy falses.
                applyTicks = DateTime.UtcNow.Ticks;
                return true;
            }

            return false;
        }

        // Newer intentional change wins (lock or unlock).
        if (rTicks > lTicks)
        {
            applyProtected = remoteProtected.Value;
            applyTicks = rTicks;
            return true;
        }

        if (rTicks < lTicks)
            return false;

        // Same clock: prefer locked (security) if they disagree.
        if (remoteProtected == true && !localProtected)
        {
            applyProtected = true;
            applyTicks = rTicks;
            return true;
        }

        return false;
    }
}

public record ConvoSyncPayload(
    string ConvoId,
    string Title,
    List<ChatMessage> Messages,
    bool? TitleIsCustom = null,
    /// <summary>
    /// Null when older peers omit the field — receiver must preserve local protection.
    /// Explicit true/false is interpreted with <see cref="ProtectionChangedTicks"/>.
    /// </summary>
    bool? IsPasswordProtected = null,
    /// <summary>
    /// UTC ticks of last intentional lock/unlock. Null/0 = legacy peer.
    /// </summary>
    long? ProtectionChangedTicks = null)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static string Serialize(
        string convoId,
        string title,
        List<ChatMessage> messages,
        bool? titleIsCustom = null,
        bool isPasswordProtected = false,
        long protectionChangedTicks = 0) =>
        JsonSerializer.Serialize(
            new ConvoSyncPayload(
                convoId,
                title,
                messages,
                titleIsCustom,
                isPasswordProtected,
                protectionChangedTicks > 0 ? protectionChangedTicks : null),
            JsonOpts);

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

public record DeleteSyncPayload(string Id, long DeletedAtTicks)
{
    public static string Serialize(string id, long deletedAtTicks) =>
        JsonSerializer.Serialize(new DeleteSyncPayload(id, deletedAtTicks));

    public static DeleteSyncPayload? Deserialize(string json) =>
        JsonSerializer.Deserialize<DeleteSyncPayload>(json);

    public static string AckValue(long deletedAtTicks) => $"deleted:{deletedAtTicks}";
}

public record NoteSyncPayload(
    string NoteId,
    string Title,
    List<ChatMessage> Entries,
    /// <summary>
    /// Null when older peers omit the field — receiver must preserve local protection.
    /// Explicit true/false is interpreted with <see cref="ProtectionChangedTicks"/>.
    /// </summary>
    bool? IsPasswordProtected = null,
    /// <summary>
    /// UTC ticks of last intentional lock/unlock. Null/0 = legacy peer.
    /// </summary>
    long? ProtectionChangedTicks = null)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static string Serialize(
        string noteId,
        string title,
        List<ChatMessage> entries,
        bool isPasswordProtected = false,
        long protectionChangedTicks = 0) =>
        JsonSerializer.Serialize(
            new NoteSyncPayload(
                noteId,
                title,
                entries,
                isPasswordProtected,
                protectionChangedTicks > 0 ? protectionChangedTicks : null),
            JsonOpts);

    public static NoteSyncPayload? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<NoteSyncPayload>(json, JsonOpts);
        }
        catch
        {
            return null;
        }
    }
}

public record BookmarkSyncPayload(BrowserBookmark Bookmark)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static string Serialize(BrowserBookmark bookmark) =>
        JsonSerializer.Serialize(new BookmarkSyncPayload(bookmark), JsonOpts);

    public static BookmarkSyncPayload? Deserialize(string json)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<BookmarkSyncPayload>(json, JsonOpts);
            return string.IsNullOrEmpty(payload?.Bookmark?.Id) ? null : payload;
        }
        catch
        {
            return null;
        }
    }
}

public record BookmarkFolderSyncPayload(BrowserBookmarkFolder Folder)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static string Serialize(BrowserBookmarkFolder folder) =>
        JsonSerializer.Serialize(new BookmarkFolderSyncPayload(folder), JsonOpts);

    public static BookmarkFolderSyncPayload? Deserialize(string json)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<BookmarkFolderSyncPayload>(json, JsonOpts);
            return string.IsNullOrEmpty(payload?.Folder?.Id) ? null : payload;
        }
        catch
        {
            return null;
        }
    }
}

public record SidebarAppSyncPayload(SidebarApp App)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static string Serialize(SidebarApp app) =>
        JsonSerializer.Serialize(new SidebarAppSyncPayload(app), JsonOpts);

    public static SidebarAppSyncPayload? Deserialize(string json)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<SidebarAppSyncPayload>(json, JsonOpts);
            return string.IsNullOrEmpty(payload?.App?.Id) ? null : payload;
        }
        catch
        {
            return null;
        }
    }
}
