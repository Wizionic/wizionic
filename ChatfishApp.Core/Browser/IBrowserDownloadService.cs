namespace ChatfishApp.Core.Browser;

/// <summary>
/// Tracks embedded-browser downloads and provides open / reveal / delete actions.
/// Platform WebViews feed items into this service; the Shared toolbar renders them.
/// </summary>
public interface IBrowserDownloadService
{
    IReadOnlyList<BrowserDownloadItem> Downloads { get; }

    bool IsAnyInProgress { get; }

    event Action? Changed;

    /// <summary>Register a newly started download (platform layer).</summary>
    BrowserDownloadItem Begin(string url, string filePath, string? fileName = null);

    /// <summary>Update progress / state for a tracked download.</summary>
    void Update(string id, Action<BrowserDownloadItem> mutator);

    /// <summary>Mark complete and set final path if it changed.</summary>
    void Complete(string id, string? finalPath = null);

    void Fail(string id, string? message = null);

    void Cancel(string id);

    Task OpenAsync(string id, CancellationToken ct = default);

    Task ShowInFolderAsync(string id, CancellationToken ct = default);

    /// <summary>Delete the file from disk (if present) and remove from the list.</summary>
    Task DeleteAsync(string id, CancellationToken ct = default);

    void RemoveFromList(string id);

    void ClearCompleted();
}
