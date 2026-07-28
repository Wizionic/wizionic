namespace ChatfishApp.Core.Storage;

public record LocalConvo(
    string Id,
    string Title,
    DateTime LastUpdated,
    int SortOrder = 0,
    bool IsPasswordProtected = false);

public record LocalNote(
    string Id,
    string Title,
    DateTime LastUpdated,
    bool IsPasswordProtected = false,
    int SortOrder = 0);

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