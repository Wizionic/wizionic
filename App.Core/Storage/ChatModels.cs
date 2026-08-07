namespace App.Core.Storage;

public record LocalConvo(
    string Id,
    string Title,
    DateTime LastUpdated,
    int SortOrder = 0,
    bool IsPasswordProtected = false,
    /// <summary>UTC ticks of last intentional lock/unlock (0 = unknown / legacy).</summary>
    long ProtectionChangedTicks = 0);

public record LocalNote(
    string Id,
    string Title,
    DateTime LastUpdated,
    bool IsPasswordProtected = false,
    int SortOrder = 0,
    /// <summary>UTC ticks of last intentional lock/unlock (0 = unknown / legacy).</summary>
    long ProtectionChangedTicks = 0);

public record LocalAlbum(
    string Id,
    string Title,
    DateTime LastUpdated,
    bool IsPasswordProtected = false,
    int SortOrder = 0,
    /// <summary>UTC ticks of last intentional lock/unlock (0 = unknown / legacy).</summary>
    long ProtectionChangedTicks = 0);

/// <summary>Single image inside a gallery album (encrypted with the album body).</summary>
public record GalleryImage(
    string Id,
    string Name,
    string ContentType,
    string DataBase64,
    long Size,
    string? ThumbnailBase64 = null,
    int? Width = null,
    int? Height = null,
    DateTime? Timestamp = null,
    DateTime? ModifiedAt = null,
    DateTime? DeletedAt = null)
{
    public string? DataUrl => !string.IsNullOrEmpty(DataBase64) && ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        ? $"data:{ContentType};base64,{DataBase64}"
        : null;

    public string? ThumbnailUrl
    {
        get
        {
            if (!string.IsNullOrEmpty(ThumbnailBase64))
                return $"data:image/jpeg;base64,{ThumbnailBase64}";
            return DataUrl;
        }
    }
}

public record SyncManifestEntry(
    string Id,
    string Title,
    long LastUpdatedTicks,
    string ContentFingerprint,
    long? DeletedAtTicks = null)
{
    public bool IsDeleted => DeletedAtTicks.HasValue;
}

public record Attachment(string Name, string ContentType, string DataBase64, long Size)
{
    public string? DataUrl => ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        ? $"data:{ContentType};base64,{DataBase64}"
        : null;
}

public record ChatMessage(
    string? Role = null,
    string Content = "",
    string? ModelUsed = null,
    DateTime? Timestamp = null,
    string? User = null,
    string? ToolTrace = null,
    List<Attachment>? Attachments = null,
    string? ContentFormat = null,
    string? ItemId = null,
    DateTime? ModifiedAt = null,
    DateTime? DeletedAt = null,
    /// <summary>Compact generation metrics (TTFT, total time, tokens) for the UI footer.</summary>
    string? StatsLine = null);