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

    public static string ForAlbumMeta(
        string albumId,
        string title,
        IReadOnlyList<AlbumImageRef> imageRefs,
        bool isPasswordProtected = false,
        long protectionChangedTicks = 0) =>
        Compute(GalleryAlbumMetaPayload.Serialize(
            albumId, title, imageRefs, isPasswordProtected, protectionChangedTicks));

    public static string ForAlbumImage(string albumId, GalleryImage image)
    {
        // Prefer content hash when full bytes are present (avoids multi-MB JSON serialization).
        if (!string.IsNullOrEmpty(image.DataBase64))
        {
            try
            {
                var raw = Convert.FromBase64String(image.DataBase64);
                var size = image.Size > 0 ? image.Size : raw.LongLength;
                return ForAlbumImageRaw(albumId, image.Id, size, raw);
            }
            catch
            {
                // fall through
            }
        }

        // Meta-only / no body — still need a stable value for deletes & empty stubs.
        return Compute(GalleryImageSyncPayload.Serialize(albumId, GalleryImageSyncPayload.WithoutThumbnail(image)));
    }

    /// <summary>Stable fingerprint from raw image bytes (SHA-256). Matches WASM galleryIngestUpload.</summary>
    public static string ForAlbumImageRaw(string albumId, string imageId, long size, byte[] rawBytes)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(rawBytes);
        return ForAlbumImageHash(albumId, imageId, size, Convert.ToBase64String(hash));
    }

    public static string ForAlbumImageHash(string albumId, string imageId, long size, string contentSha256Base64) =>
        Compute($"img|{albumId}|{imageId}|{size}|{contentSha256Base64}");

    public static string ForBookmark(BrowserBookmark bookmark) =>
        Compute(BookmarkSyncPayload.Serialize(bookmark));

    public static string ForBookmarkFolder(BrowserBookmarkFolder folder) =>
        Compute(BookmarkFolderSyncPayload.Serialize(folder));

    public static string ForSidebarApp(SidebarApp app) =>
        Compute(SidebarAppSyncPayload.Serialize(app));

    public static string ForCalendar(
        string calendarId,
        string name,
        string color,
        bool isVisible,
        string? description,
        long lastUpdatedTicks,
        bool isWorkflowCalendar = false) =>
        Compute(CalendarMetaSyncPayload.Serialize(
            calendarId, name, color, isVisible, description, lastUpdatedTicks, isWorkflowCalendar));

    public static string ForCalendarEvent(CalendarEvent evt) =>
        Compute(CalendarEventSyncPayload.Serialize(evt.CalendarId, evt));
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

/// <summary>Lightweight image pointer inside album meta (no base64).</summary>
public record AlbumImageRef(
    string Id,
    string ContentFingerprint,
    int SortOrder = 0,
    long? DeletedAtTicks = null);

/// <summary>Album metadata only — title, protection, ordered image fingerprints.</summary>
public record GalleryAlbumMetaPayload(
    string AlbumId,
    string Title,
    List<AlbumImageRef> Images,
    bool? IsPasswordProtected = null,
    long? ProtectionChangedTicks = null)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static string Serialize(
        string albumId,
        string title,
        IReadOnlyList<AlbumImageRef> images,
        bool isPasswordProtected = false,
        long protectionChangedTicks = 0) =>
        JsonSerializer.Serialize(
            new GalleryAlbumMetaPayload(
                albumId,
                title,
                images?.ToList() ?? new List<AlbumImageRef>(),
                isPasswordProtected,
                protectionChangedTicks > 0 ? protectionChangedTicks : null),
            JsonOpts);

    public static GalleryAlbumMetaPayload? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<GalleryAlbumMetaPayload>(json, JsonOpts);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Single image create/update over the wire.</summary>
public record GalleryImageSyncPayload(string AlbumId, GalleryImage Image)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Drop thumbnail bytes (for fingerprints only). Prefer keeping thumbs on wire and in local storage.</summary>
    public static GalleryImage WithoutThumbnail(GalleryImage image) =>
        string.IsNullOrEmpty(image.ThumbnailBase64) ? image : image with { ThumbnailBase64 = null };

    /// <summary>Legacy alias — prefer <see cref="WithoutThumbnail"/>.</summary>
    public static GalleryImage ForWire(GalleryImage image) => WithoutThumbnail(image);

    /// <summary>Serialize for wire/local payload. Includes thumbnail so peers can render the grid without re-decoding full images.</summary>
    public static string Serialize(string albumId, GalleryImage image) =>
        JsonSerializer.Serialize(new GalleryImageSyncPayload(albumId, image), JsonOpts);

    public static GalleryImageSyncPayload? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<GalleryImageSyncPayload>(json, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Stable manifest/ack id: albumId/imageId</summary>
    public static string CompositeId(string albumId, string imageId) => $"{albumId}/{imageId}";

    public static bool TrySplitCompositeId(string composite, out string albumId, out string imageId)
    {
        albumId = "";
        imageId = "";
        if (string.IsNullOrEmpty(composite))
            return false;
        var idx = composite.IndexOf('/');
        if (idx <= 0 || idx >= composite.Length - 1)
            return false;
        albumId = composite[..idx];
        imageId = composite[(idx + 1)..];
        return !string.IsNullOrEmpty(albumId) && !string.IsNullOrEmpty(imageId);
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
