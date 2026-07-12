using ChatfishApp.Core.Browser;
using ChatfishApp.Core.Storage;

namespace ChatfishApp.Client.Services;

public sealed class NullBrowserStore : IBrowserStore
{
    public event Action? Changed;

    public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;

    public IReadOnlyList<BrowserHistoryEntry> GetHistory() => [];
    public IReadOnlyList<BrowserHistoryEntry> SearchHistory(string query, int maxResults = 10) => [];
    public Task AddHistoryEntryAsync(string url, string title, CancellationToken ct = default) => Task.CompletedTask;
    public Task ClearHistoryAsync(CancellationToken ct = default) => Task.CompletedTask;

    public IReadOnlyList<BrowserBookmarkFolder> GetFolders() => [];
    public IReadOnlyList<BrowserBookmark> GetBookmarks(string? folderId = null) => [];
    public BrowserBookmark? FindBookmarkByUrl(string url) => null;
    public Task ReorderBookmarksAsync(string folderId, IReadOnlyList<string> orderedIds, CancellationToken ct = default) =>
        Task.CompletedTask;
    public Task MoveBookmarkAsync(string bookmarkId, string targetFolderId, string? beforeBookmarkId = null, CancellationToken ct = default) =>
        Task.CompletedTask;
    public Task<BrowserBookmark> AddBookmarkAsync(string url, string title, string folderId, string? iconUrl = null, CancellationToken ct = default) =>
        Task.FromResult(new BrowserBookmark("", url, title, folderId, DateTime.UtcNow, IconUrl: iconUrl));
    public Task UpdateBookmarkAsync(BrowserBookmark bookmark, CancellationToken ct = default) => Task.CompletedTask;
    public Task RemoveBookmarkAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    public Task RemoveBookmarksAsync(IReadOnlyList<string> ids, CancellationToken ct = default) => Task.CompletedTask;
    public Task<BrowserBookmarkFolder> AddFolderAsync(string name, string? parentFolderId = null, CancellationToken ct = default) =>
        Task.FromResult(new BrowserBookmarkFolder("", name, parentFolderId, DateTime.UtcNow));
    public Task RenameFolderAsync(string id, string name, CancellationToken ct = default) => Task.CompletedTask;
    public Task RemoveFolderAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReorderFoldersAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default) => Task.CompletedTask;

    public BrowserSettings GetSettings() => new();
    public Task SaveSettingsAsync(BrowserSettings settings, CancellationToken ct = default) => Task.CompletedTask;
    public Task<BrowserBookmarkFolder> EnsureBookmarksBarFolderAsync(CancellationToken ct = default) =>
        Task.FromResult(new BrowserBookmarkFolder(
            BrowserBookmarkFolders.BookmarksBarFolderId,
            BrowserBookmarkFolders.BookmarksBarFolderName,
            null,
            DateTime.UtcNow));

    public Task<List<SyncManifestEntry>> LoadBookmarkManifestEntriesAsync(bool backfillMissingFingerprints = false, CancellationToken ct = default) =>
        Task.FromResult(new List<SyncManifestEntry>());
    public Task<List<SyncManifestEntry>> LoadFolderManifestEntriesAsync(bool backfillMissingFingerprints = false, CancellationToken ct = default) =>
        Task.FromResult(new List<SyncManifestEntry>());
    public Task<BrowserBookmark?> GetBookmarkByIdAsync(string id, CancellationToken ct = default) =>
        Task.FromResult<BrowserBookmark?>(null);
    public Task<BrowserBookmarkFolder?> GetFolderByIdAsync(string id, CancellationToken ct = default) =>
        Task.FromResult<BrowserBookmarkFolder?>(null);
    public Task ApplyBookmarkPayloadAsync(BrowserBookmark bookmark, CancellationToken ct = default) => Task.CompletedTask;
    public Task ApplyFolderPayloadAsync(BrowserBookmarkFolder folder, CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> ShouldAcceptIncomingBookmarkAsync(BrowserBookmark bookmark, CancellationToken ct = default) =>
        Task.FromResult(false);
    public Task<bool> ShouldAcceptIncomingFolderAsync(BrowserBookmarkFolder folder, CancellationToken ct = default) =>
        Task.FromResult(false);
    public Task<DateTime> TombstoneBookmarkAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(DateTime.UtcNow);
    public Task<DateTime> TombstoneFolderAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(DateTime.UtcNow);
    public Task<bool> TryApplyRemoteBookmarkDeleteAsync(string id, long deletedAtTicks, CancellationToken ct = default) =>
        Task.FromResult(false);
    public Task<bool> TryApplyRemoteFolderDeleteAsync(string id, long deletedAtTicks, CancellationToken ct = default) =>
        Task.FromResult(false);
}
