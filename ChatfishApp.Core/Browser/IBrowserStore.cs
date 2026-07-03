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
    Task<BrowserBookmark> AddBookmarkAsync(string url, string title, string folderId, CancellationToken ct = default);
    Task UpdateBookmarkAsync(BrowserBookmark bookmark, CancellationToken ct = default);
    Task RemoveBookmarkAsync(string id, CancellationToken ct = default);
    Task<BrowserBookmarkFolder> AddFolderAsync(string name, string? parentFolderId = null, CancellationToken ct = default);
    Task RenameFolderAsync(string id, string name, CancellationToken ct = default);
    Task RemoveFolderAsync(string id, CancellationToken ct = default);

    BrowserSettings GetSettings();
    Task SaveSettingsAsync(BrowserSettings settings, CancellationToken ct = default);

    event Action? Changed;
}