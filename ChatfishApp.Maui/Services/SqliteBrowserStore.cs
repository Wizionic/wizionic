using System.Text.Json;
using ChatfishApp.Core.Browser;

namespace ChatfishApp.Maui.Services;

public sealed class SqliteBrowserStore : IBrowserStore
{
    private const string HistoryKey = "wasm-browser-history";
    private const string BookmarksKey = "wasm-browser-bookmarks";
    private const string FoldersKey = "wasm-browser-folders";
    private const string SettingsKey = "wasm-browser-settings";
    private const string DefaultFolderId = "default";
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

    public async Task<BrowserBookmark> AddBookmarkAsync(string url, string title, string folderId, CancellationToken ct = default)
    {
        EnsureDefaultFolder();
        if (string.IsNullOrWhiteSpace(folderId) || _folders.All(f => f.Id != folderId))
            folderId = DefaultFolderId;

        var bookmark = new BrowserBookmark(
            Guid.NewGuid().ToString("N"),
            url.Trim(),
            string.IsNullOrWhiteSpace(title) ? url.Trim() : title.Trim(),
            folderId,
            DateTime.UtcNow,
            _bookmarks.Count(b => b.FolderId == folderId));

        _bookmarks.Add(bookmark);
        await SaveBookmarksAsync(ct);
        Changed?.Invoke();
        return bookmark;
    }

    public async Task UpdateBookmarkAsync(BrowserBookmark bookmark, CancellationToken ct = default)
    {
        var index = _bookmarks.FindIndex(b => b.Id == bookmark.Id);
        if (index < 0)
            return;

        _bookmarks[index] = bookmark;
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
        if (id == DefaultFolderId)
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
        if (id == DefaultFolderId)
            return;

        _folders.RemoveAll(f => f.Id == id);
        foreach (var bookmark in _bookmarks.Where(b => b.FolderId == id).ToList())
            _bookmarks.Remove(bookmark);

        await SaveFoldersAsync(ct);
        await SaveBookmarksAsync(ct);
        Changed?.Invoke();
    }

    public BrowserSettings GetSettings() => _settings.Clone();

    public async Task SaveSettingsAsync(BrowserSettings settings, CancellationToken ct = default)
    {
        _settings = settings.Clone();
        var json = JsonSerializer.Serialize(_settings);
        await _db.SetStringAsync(SettingsKey, json, ct);
        Changed?.Invoke();
    }

    private void EnsureDefaultFolder()
    {
        if (_folders.Any(f => f.Id == DefaultFolderId))
            return;

        _folders.Insert(0, new BrowserBookmarkFolder(DefaultFolderId, "Bookmarks", null, DateTime.UtcNow, 0));
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
}