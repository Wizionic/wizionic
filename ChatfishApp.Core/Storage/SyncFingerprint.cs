using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatfishApp.Core.Browser;

namespace ChatfishApp.Core.Storage;

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

    public static string ForBookmark(BrowserBookmark bookmark) =>
        Compute(BookmarkSyncPayload.Serialize(bookmark));

    public static string ForBookmarkFolder(BrowserBookmarkFolder folder) =>
        Compute(BookmarkFolderSyncPayload.Serialize(folder));

    public static string ForSidebarApp(SidebarApp app) =>
        Compute(SidebarAppSyncPayload.Serialize(app));
}

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

public record DeleteSyncPayload(string Id, long DeletedAtTicks)
{
    public static string Serialize(string id, long deletedAtTicks) =>
        JsonSerializer.Serialize(new DeleteSyncPayload(id, deletedAtTicks));

    public static DeleteSyncPayload? Deserialize(string json) =>
        JsonSerializer.Deserialize<DeleteSyncPayload>(json);

    public static string AckValue(long deletedAtTicks) => $"deleted:{deletedAtTicks}";
}

public record NoteSyncPayload(string NoteId, string Title, List<ChatMessage> Entries)
{
    public static string Serialize(string noteId, string title, List<ChatMessage> entries) =>
        JsonSerializer.Serialize(new NoteSyncPayload(noteId, title, entries));

    public static NoteSyncPayload? Deserialize(string json) =>
        JsonSerializer.Deserialize<NoteSyncPayload>(json);
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