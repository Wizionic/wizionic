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
            if (string.IsNullOrEmpty(ThumbnailBase64))
                return DataUrl;

            // Prepared thumbs from canvas are JPEG; full-image fallback (tool path when JS
            // thumb gen is unavailable) may be PNG/WebP — sniff so the grid does not break.
            var mime = SniffImageMime(ThumbnailBase64)
                       ?? (ContentType is { Length: > 0 } && ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                           ? ContentType
                           : "image/jpeg");
            return $"data:{mime};base64,{ThumbnailBase64}";
        }
    }

    /// <summary>Best-effort image MIME from base64 magic (no full decode).</summary>
    public static string? SniffImageMime(string base64)
    {
        if (string.IsNullOrEmpty(base64)) return null;
        // Skip optional data: URL prefix
        var b64 = base64;
        var comma = b64.IndexOf(',');
        if (b64.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
            b64 = b64[(comma + 1)..];

        // PNG → iVBORw0KGgo…  JPEG → /9j/  GIF → R0lGOD  WebP (RIFF…WEBP) → UklGR…
        if (b64.StartsWith("iVBOR", StringComparison.Ordinal)) return "image/png";
        if (b64.StartsWith("/9j/", StringComparison.Ordinal)) return "image/jpeg";
        if (b64.StartsWith("R0lGOD", StringComparison.Ordinal)) return "image/gif";
        if (b64.StartsWith("UklGR", StringComparison.Ordinal)) return "image/webp";
        return null;
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