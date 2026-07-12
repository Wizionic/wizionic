namespace ChatfishApp.Core.Browser;

/// <summary>Bookmark folder — structured for cross-device sync.</summary>
public record BrowserBookmarkFolder(
    string Id,
    string Name,
    string? ParentFolderId,
    DateTime CreatedAtUtc,
    int SortOrder = 0,
    DateTime? UpdatedAtUtc = null)
{
    public DateTime EffectiveUpdatedAtUtc => UpdatedAtUtc ?? CreatedAtUtc;
}
