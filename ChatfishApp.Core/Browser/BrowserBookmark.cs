namespace ChatfishApp.Core.Browser;

/// <summary>User bookmark — structured for cross-device sync.</summary>
public record BrowserBookmark(
    string Id,
    string Url,
    string Title,
    string FolderId,
    DateTime CreatedAtUtc,
    int SortOrder = 0,
    DateTime? UpdatedAtUtc = null,
    /// <summary>Absolute http(s) or data: URL for the site icon (captured from the page when possible).</summary>
    string? IconUrl = null)
{
    public DateTime EffectiveUpdatedAtUtc => UpdatedAtUtc ?? CreatedAtUtc;
}
