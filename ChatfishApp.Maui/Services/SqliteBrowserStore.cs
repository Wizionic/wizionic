using System.Text.Json;
using ChatfishApp.Core.Browser;

namespace ChatfishApp.Maui.Services;

public sealed class SqliteBrowserStore : IBrowserStore
{
    private const string HistoryKey = "wasm-browser-history";
    private const string BookmarksKey = "wasm-browser-bookmarks";
    private const string FoldersKey = "wasm-browser-folders";
    private const string SettingsKey = "wasm-browser-settings";

    private const int MaxHistoryEntries = 500;

    private readonly SqliteSettingsDatabase _db;
    private List<BrowserHistoryEntry> _history = [];
    private List<BrowserBookmark> _bookmarks = [];
    private List<BrowserBookmarkFolder> _folders = [];
    private BrowserSettings _settings = new();

    public SqliteBrowserStore(SqliteSettingsDatabase db) => _db = db;

    public event Action? Changed;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var historyJson = await _db.GetStringAsync(HistoryKey, ct);
        if (!string.IsNullOrEmpty(historyJson))
        {
            var loaded = JsonSerializer.Deserialize<List<BrowserHistoryEntry>>(historyJson);
            if (loaded != null)
                _history = loaded;
        }

        var bookmarksJson = await _db.GetStringAsync(BookmarksKey, ct);
        if (!string.IsNullOrEmpty(bookmarksJson))
        {
            var loaded = JsonSerializer.Deserialize<List<BrowserBookmark>>(bookmarksJson);
            if (loaded != null)
                _bookmarks = loaded;
        }

        var foldersJson = await _db.GetStringAsync(FoldersKey, ct);
        if (!string.IsNullOrEmpty(foldersJson))
        {
            var loaded = JsonSerializer.Deserialize<List<BrowserBookmarkFolder>>(foldersJson);
            if (loaded != null)
                _folders = loaded;
        }

        EnsureDefaultFolder();

        var settingsJson = await _db.GetStringAsync(SettingsKey, ct);
        if (!string.IsNullOrEmpty(settingsJson))
        {
            var loaded = JsonSerializer.Deserialize<BrowserSettings>(settingsJson);
            if (loaded != null)
                _settings = loaded;
        }

        if (_settings.ShowBookmarksBar)
            EnsureBookmarksBarFolder();
    }

    public IReadOnlyList<BrowserHistoryEntry> GetHistory() =>
        _history.OrderByDescending(h => h.VisitedAtUtc).ToList();

    public IReadOnlyList<BrowserHistoryEntry> SearchHistory(string query, int maxResults = 10)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var q = query.Trim();
        return _history
            .Where(h =>
                h.Url.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(h.Title)
                    && h.Title.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(h => h.VisitedAtUtc)
            .Take(maxResults)
            .ToList();
    }

    public async Task AddHistoryEntryAsync(string url, string title, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)
            || url.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
            return;

        _history.RemoveAll(h => h.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
        _history.Insert(0, new BrowserHistoryEntry(url, title?.Trim() ?? "", DateTime.UtcNow));

        if (_history.Count > MaxHistoryEntries)
            _history = _history.Take(MaxHistoryEntries).ToList();

        await SaveHistoryAsync(ct);
        Changed?.Invoke();
    }

    public async Task ClearHistoryAsync(CancellationToken ct = default)
    {
        _history.Clear();
        await SaveHistoryAsync(ct);
        Changed?.Invoke();
    }

    public IReadOnlyList<BrowserBookmarkFolder> GetFolders() =>
        _folders.OrderBy(f => f.SortOrder).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public IReadOnlyList<BrowserBookmark> GetBookmarks(string? folderId = null)
    {
        var query = _bookmarks.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(folderId))
            query = query.Where(b => b.FolderId == folderId);

        return query
            .OrderBy(b => b.SortOrder)
            .ThenBy(b => b.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public BrowserBookmark? FindBookmarkByUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var trimmed = url.Trim();
        return _bookmarks.FirstOrDefault(b => b.Url.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<BrowserBookmark> AddBookmarkAsync(string url, string title, string folderId, CancellationToken ct = default)
    {
        EnsureDefaultFolder();
        if (string.IsNullOrWhiteSpace(folderId) || _folders.All(f => f.Id != folderId))
            folderId = BrowserBookmarkFolders.DefaultFolderId;

        var bookmark = new BrowserBookmark(
            Guid.NewGuid().ToString("N"),
            url.Trim(),
            title?.Trim() ?? "",
            folderId,
            DateTime.UtcNow,
            _bookmarks.Count(b => b.FolderId == folderId));

        _bookmarks.Add(bookmark);
        await SaveBookmarksAsync(ct);
        Changed?.Invoke();
        return bookmark;
    }

    public async Task MoveBookmarkAsync(
        string bookmarkId,
        string targetFolderId,
        string? beforeBookmarkId = null,
        CancellationToken ct = default)
    {
        var index = _bookmarks.FindIndex(b => b.Id == bookmarkId);
        if (index < 0 || string.IsNullOrWhiteSpace(targetFolderId))
            return;

        if (_folders.All(f => f.Id != targetFolderId))
            return;

        var bookmark = _bookmarks[index];
        var oldFolderId = bookmark.FolderId;

        var targetItems = _bookmarks
            .Where(b => b.FolderId == targetFolderId && b.Id != bookmarkId)
            .OrderBy(b => b.SortOrder)
            .ThenBy(b => b.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var moved = bookmark with { FolderId = targetFolderId };

        if (!string.IsNullOrWhiteSpace(beforeBookmarkId))
        {
            var insertIndex = targetItems.FindIndex(b => b.Id == beforeBookmarkId);
            if (insertIndex < 0)
                insertIndex = targetItems.Count;
            targetItems.Insert(insertIndex, moved);
        }
        else
        {
            targetItems.Add(moved);
        }

        for (var i = 0; i < targetItems.Count; i++)
        {
            var itemIndex = _bookmarks.FindIndex(b => b.Id == targetItems[i].Id);
            if (itemIndex < 0)
                continue;

            _bookmarks[itemIndex] = _bookmarks[itemIndex] with { FolderId = targetFolderId, SortOrder = i };
        }

        if (!oldFolderId.Equals(targetFolderId, StringComparison.Ordinal))
            await NormalizeFolderSortOrdersAsync(oldFolderId, ct);

        await SaveBookmarksAsync(ct);
        Changed?.Invoke();
    }

    public async Task UpdateBookmarkAsync(BrowserBookmark bookmark, CancellationToken ct = default)
    {
        var index = _bookmarks.FindIndex(b => b.Id == bookmark.Id);
        if (index < 0)
            return;

        var existing = _bookmarks[index];
        var folderChanged = !existing.FolderId.Equals(bookmark.FolderId, StringComparison.Ordinal);
        _bookmarks[index] = bookmark;

        if (folderChanged)
        {
            var newFolderCount = _bookmarks.Count(b =>
                b.FolderId == bookmark.FolderId && b.Id != bookmark.Id);
            _bookmarks[index] = bookmark with { SortOrder = newFolderCount };
            await NormalizeFolderSortOrdersAsync(existing.FolderId, ct);
        }

        await SaveBookmarksAsync(ct);
        Changed?.Invoke();
    }

    public async Task ReorderBookmarksAsync(string folderId, IReadOnlyList<string> orderedIds, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folderId) || orderedIds.Count == 0)
            return;

        for (var i = 0; i < orderedIds.Count; i++)
        {
            var id = orderedIds[i];
            var index = _bookmarks.FindIndex(b => b.Id == id && b.FolderId == folderId);
            if (index < 0)
                continue;

            var bookmark = _bookmarks[index];
            _bookmarks[index] = bookmark with { SortOrder = i };
        }

        await SaveBookmarksAsync(ct);
        Changed?.Invoke();
    }

    public async Task RemoveBookmarkAsync(string id, CancellationToken ct = default)
    {
        if (_bookmarks.RemoveAll(b => b.Id == id) > 0)
        {
            await SaveBookmarksAsync(ct);
            Changed?.Invoke();
        }
    }

    public async Task RemoveBookmarksAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0)
            return;

        var idSet = ids.ToHashSet(StringComparer.Ordinal);
        if (_bookmarks.RemoveAll(b => idSet.Contains(b.Id)) > 0)
        {
            await SaveBookmarksAsync(ct);
            Changed?.Invoke();
        }
    }

    public async Task<BrowserBookmarkFolder> AddFolderAsync(string name, string? parentFolderId = null, CancellationToken ct = default)
    {
        var trimmed = (name ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw new ArgumentException("Folder name is required.");

        var folder = new BrowserBookmarkFolder(
            Guid.NewGuid().ToString("N"),
            trimmed,
            parentFolderId,
            DateTime.UtcNow,
            _folders.Count);

        _folders.Add(folder);
        await SaveFoldersAsync(ct);
        Changed?.Invoke();
        return folder;
    }

    public async Task RenameFolderAsync(string id, string name, CancellationToken ct = default)
    {
        if (id is BrowserBookmarkFolders.DefaultFolderId or BrowserBookmarkFolders.BookmarksBarFolderId)
            return;

        var index = _folders.FindIndex(f => f.Id == id);
        if (index < 0)
            return;

        var trimmed = (name ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed))
            return;

        var existing = _folders[index];
        _folders[index] = existing with { Name = trimmed };
        await SaveFoldersAsync(ct);
        Changed?.Invoke();
    }

    public async Task RemoveFolderAsync(string id, CancellationToken ct = default)
    {
        if (id is BrowserBookmarkFolders.DefaultFolderId or BrowserBookmarkFolders.BookmarksBarFolderId)
            return;

        _folders.RemoveAll(f => f.Id == id);
        foreach (var bookmark in _bookmarks.Where(b => b.FolderId == id).ToList())
            _bookmarks.Remove(bookmark);

        await SaveFoldersAsync(ct);
        await SaveBookmarksAsync(ct);
        Changed?.Invoke();
    }

    public async Task ReorderFoldersAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default)
    {
        if (orderedIds.Count == 0)
            return;

        for (var i = 0; i < orderedIds.Count; i++)
        {
            var id = orderedIds[i];
            var index = _folders.FindIndex(f => f.Id == id);
            if (index < 0)
                continue;

            var folder = _folders[index];
            _folders[index] = folder with { SortOrder = i };
        }

        await SaveFoldersAsync(ct);
        Changed?.Invoke();
    }

    public BrowserSettings GetSettings() => _settings.Clone();

    public async Task SaveSettingsAsync(BrowserSettings settings, CancellationToken ct = default)
    {
        _settings = settings.Clone();
        if (_settings.ShowBookmarksBar)
            await EnsureBookmarksBarFolderAsync(ct);

        var json = JsonSerializer.Serialize(_settings);
        await _db.SetStringAsync(SettingsKey, json, ct);
        Changed?.Invoke();
    }

    public async Task<BrowserBookmarkFolder> EnsureBookmarksBarFolderAsync(CancellationToken ct = default)
    {
        EnsureBookmarksBarFolder();
        await SaveFoldersAsync(ct);
        return _folders.First(f => f.Id == BrowserBookmarkFolders.BookmarksBarFolderId);
    }

    private void EnsureDefaultFolder()
    {
        if (_folders.Any(f => f.Id == BrowserBookmarkFolders.DefaultFolderId))
            return;

        _folders.Insert(0, new BrowserBookmarkFolder(
            BrowserBookmarkFolders.DefaultFolderId,
            BrowserBookmarkFolders.DefaultFolderName,
            null,
            DateTime.UtcNow,
            0));
    }

    private void EnsureBookmarksBarFolder()
    {
        if (_folders.Any(f => f.Id == BrowserBookmarkFolders.BookmarksBarFolderId))
            return;

        _folders.Add(new BrowserBookmarkFolder(
            BrowserBookmarkFolders.BookmarksBarFolderId,
            BrowserBookmarkFolders.BookmarksBarFolderName,
            null,
            DateTime.UtcNow,
            1));
    }

    private async Task SaveHistoryAsync(CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(_history);
        await _db.SetStringAsync(HistoryKey, json, ct);
    }

    private async Task SaveBookmarksAsync(CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(_bookmarks);
        await _db.SetStringAsync(BookmarksKey, json, ct);
    }

    private async Task SaveFoldersAsync(CancellationToken ct)
    {
        EnsureDefaultFolder();
        var json = JsonSerializer.Serialize(_folders);
        await _db.SetStringAsync(FoldersKey, json, ct);
    }

    private async Task NormalizeFolderSortOrdersAsync(string folderId, CancellationToken ct)
    {
        var items = _bookmarks
            .Where(b => b.FolderId == folderId)
            .OrderBy(b => b.SortOrder)
            .ThenBy(b => b.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (var i = 0; i < items.Count; i++)
        {
            var index = _bookmarks.FindIndex(b => b.Id == items[i].Id);
            if (index < 0)
                continue;

            _bookmarks[index] = _bookmarks[index] with { SortOrder = i };
        }

        await SaveBookmarksAsync(ct);
    }
}