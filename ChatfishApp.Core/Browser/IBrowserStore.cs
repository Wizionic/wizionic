namespace ChatfishApp.Core.Browser;

public interface IBrowserStore
{
    Task LoadAsync(CancellationToken ct = default);

    IReadOnlyList<BrowserHistoryEntry> GetHistory();
    IReadOnlyList<BrowserHistoryEntry> SearchHistory(string query, int maxResults = 10);
    Task AddHistoryEntryAsync(string url, string title, CancellationToken ct = default);
    Task ClearHistoryAsync(CancellationToken ct = default);

    IReadOnlyList<BrowserBookmarkFolder> GetFolders();
    IReadOnlyList<BrowserBookmark> GetBookmarks(string? folderId = null);
    BrowserBookmark? FindBookmarkByUrl(string url);
    Task<BrowserBookmark> AddBookmarkAsync(string url, string title, string folderId, CancellationToken ct = default);
    Task ReorderBookmarksAsync(string folderId, IReadOnlyList<string> orderedIds, CancellationToken ct = default);
    Task MoveBookmarkAsync(string bookmarkId, string targetFolderId, string? beforeBookmarkId = null, CancellationToken ct = default);
    Task UpdateBookmarkAsync(BrowserBookmark bookmark, CancellationToken ct = default);
    Task RemoveBookmarkAsync(string id, CancellationToken ct = default);
    Task RemoveBookmarksAsync(IReadOnlyList<string> ids, CancellationToken ct = default);
    Task<BrowserBookmarkFolder> AddFolderAsync(string name, string? parentFolderId = null, CancellationToken ct = default);
    Task RenameFolderAsync(string id, string name, CancellationToken ct = default);
    Task RemoveFolderAsync(string id, CancellationToken ct = default);
    Task ReorderFoldersAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default);

    BrowserSettings GetSettings();
    Task SaveSettingsAsync(BrowserSettings settings, CancellationToken ct = default);
    Task<BrowserBookmarkFolder> EnsureBookmarksBarFolderAsync(CancellationToken ct = default);

    event Action? Changed;
}