namespace ChatfishApp.Core.Browser;

/// <summary>User bookmark — structured for cross-device sync.</summary>
public record BrowserBookmark(
    string Id,
    string Url,
    string Title,
    string FolderId,
    DateTime CreatedAtUtc,
    int SortOrder = 0,
    DateTime? UpdatedAtUtc = null)
{
    public DateTime EffectiveUpdatedAtUtc => UpdatedAtUtc ?? CreatedAtUtc;
}
