using System.Text.Json;
using ChatfishApp.Core.Browser;
using ChatfishApp.Core.Storage;

namespace ChatfishApp.Maui.Services;

public sealed class SqliteBrowserStore : IBrowserStore
{
    private const string HistoryKey = "wasm-browser-history";
    private const string BookmarksKey = "wasm-browser-bookmarks";
    private const string FoldersKey = "wasm-browser-folders";
    private const string SettingsKey = "wasm-browser-settings";
    private const string BookmarkMetaKey = "wasm-browser-bookmark-meta";
    private const string FolderMetaKey = "wasm-browser-folder-meta";

    private const int MaxHistoryEntries = 500;

    private readonly SqliteSettingsDatabase _db;
    private List<BrowserHistoryEntry> _history = [];
    private List<BrowserBookmark> _bookmarks = [];
    private List<BrowserBookmarkFolder> _folders = [];
    private BrowserSettings _settings = new();
    private Dictionary<string, SyncMeta> _bookmarkMeta = new(StringComparer.Ordinal);
    private Dictionary<string, SyncMeta> _folderMeta = new(StringComparer.Ordinal);

    private sealed record SyncMeta(string ContentFingerprint, long LastUpdatedTicks, long? DeletedAtTicks = null);

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

        _bookmarkMeta = await LoadMetaAsync(BookmarkMetaKey, ct);
        _folderMeta = await LoadMetaAsync(FolderMetaKey, ct);
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

        var now = DateTime.UtcNow;
        var bookmark = new BrowserBookmark(
            Guid.NewGuid().ToString("N"),
            url.Trim(),
            title?.Trim() ?? "",
            folderId,
            now,
            _bookmarks.Count(b => b.FolderId == folderId),
            now);

        _bookmarks.Add(bookmark);
        UpsertLiveBookmarkMeta(bookmark);
        await SaveBookmarksAsync(ct);
        await SaveBookmarkMetaAsync(ct);
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
        var now = DateTime.UtcNow;

        var targetItems = _bookmarks
            .Where(b => b.FolderId == targetFolderId && b.Id != bookmarkId)
            .OrderBy(b => b.SortOrder)
            .ThenBy(b => b.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var moved = bookmark with { FolderId = targetFolderId, UpdatedAtUtc = now };

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

            var updated = _bookmarks[itemIndex] with
            {
                FolderId = targetFolderId,
                SortOrder = i,
                UpdatedAtUtc = now
            };
            _bookmarks[itemIndex] = updated;
            UpsertLiveBookmarkMeta(updated);
        }

        if (!oldFolderId.Equals(targetFolderId, StringComparison.Ordinal))
            await NormalizeFolderSortOrdersAsync(oldFolderId, ct);

        await SaveBookmarksAsync(ct);
        await SaveBookmarkMetaAsync(ct);
        Changed?.Invoke();
    }

    public async Task UpdateBookmarkAsync(BrowserBookmark bookmark, CancellationToken ct = default)
    {
        var index = _bookmarks.FindIndex(b => b.Id == bookmark.Id);
        if (index < 0)
            return;

        var existing = _bookmarks[index];
        var folderChanged = !existing.FolderId.Equals(bookmark.FolderId, StringComparison.Ordinal);
        var now = DateTime.UtcNow;
        var updated = bookmark with { UpdatedAtUtc = bookmark.UpdatedAtUtc ?? now };
        _bookmarks[index] = updated;

        if (folderChanged)
        {
            var newFolderCount = _bookmarks.Count(b =>
                b.FolderId == updated.FolderId && b.Id != updated.Id);
            updated = updated with { SortOrder = newFolderCount, UpdatedAtUtc = now };
            _bookmarks[index] = updated;
            await NormalizeFolderSortOrdersAsync(existing.FolderId, ct);
        }

        UpsertLiveBookmarkMeta(updated);
        await SaveBookmarksAsync(ct);
        await SaveBookmarkMetaAsync(ct);
        Changed?.Invoke();
    }

    public async Task ReorderBookmarksAsync(string folderId, IReadOnlyList<string> orderedIds, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folderId) || orderedIds.Count == 0)
            return;

        var now = DateTime.UtcNow;
        for (var i = 0; i < orderedIds.Count; i++)
        {
            var id = orderedIds[i];
            var index = _bookmarks.FindIndex(b => b.Id == id && b.FolderId == folderId);
            if (index < 0)
                continue;

            var updated = _bookmarks[index] with { SortOrder = i, UpdatedAtUtc = now };
            _bookmarks[index] = updated;
            UpsertLiveBookmarkMeta(updated);
        }

        await SaveBookmarksAsync(ct);
        await SaveBookmarkMetaAsync(ct);
        Changed?.Invoke();
    }

    public async Task RemoveBookmarkAsync(string id, CancellationToken ct = default)
    {
        await TombstoneBookmarkAsync(id, ct);
    }

    public async Task RemoveBookmarksAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0)
            return;

        foreach (var id in ids)
            await TombstoneBookmarkAsync(id, ct);
    }

    public async Task<BrowserBookmarkFolder> AddFolderAsync(string name, string? parentFolderId = null, CancellationToken ct = default)
    {
        var trimmed = (name ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw new ArgumentException("Folder name is required.");

        var now = DateTime.UtcNow;
        var folder = new BrowserBookmarkFolder(
            Guid.NewGuid().ToString("N"),
            trimmed,
            parentFolderId,
            now,
            _folders.Count,
            now);

        _folders.Add(folder);
        UpsertLiveFolderMeta(folder);
        await SaveFoldersAsync(ct);
        await SaveFolderMetaAsync(ct);
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
        var updated = existing with { Name = trimmed, UpdatedAtUtc = DateTime.UtcNow };
        _folders[index] = updated;
        UpsertLiveFolderMeta(updated);
        await SaveFoldersAsync(ct);
        await SaveFolderMetaAsync(ct);
        Changed?.Invoke();
    }

    public async Task RemoveFolderAsync(string id, CancellationToken ct = default)
    {
        if (id is BrowserBookmarkFolders.DefaultFolderId or BrowserBookmarkFolders.BookmarksBarFolderId)
            return;

        var bookmarkIds = _bookmarks.Where(b => b.FolderId == id).Select(b => b.Id).ToList();
        foreach (var bookmarkId in bookmarkIds)
            await TombstoneBookmarkAsync(bookmarkId, ct);

        await TombstoneFolderAsync(id, ct);
    }

    public async Task ReorderFoldersAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default)
    {
        if (orderedIds.Count == 0)
            return;

        var now = DateTime.UtcNow;
        for (var i = 0; i < orderedIds.Count; i++)
        {
            var id = orderedIds[i];
            var index = _folders.FindIndex(f => f.Id == id);
            if (index < 0)
                continue;

            var updated = _folders[index] with { SortOrder = i, UpdatedAtUtc = now };
            _folders[index] = updated;
            UpsertLiveFolderMeta(updated);
        }

        await SaveFoldersAsync(ct);
        await SaveFolderMetaAsync(ct);
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
        await SaveFolderMetaAsync(ct);
        return _folders.First(f => f.Id == BrowserBookmarkFolders.BookmarksBarFolderId);
    }

    // --- Sync ---

    public Task<List<SyncManifestEntry>> LoadBookmarkManifestEntriesAsync(
        bool backfillMissingFingerprints = false,
        CancellationToken ct = default)
    {
        var entries = new List<SyncManifestEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var bm in _bookmarks)
        {
            seen.Add(bm.Id);
            var fingerprint = SyncFingerprint.ForBookmark(bm);
            var ticks = bm.EffectiveUpdatedAtUtc.Ticks;
            if (backfillMissingFingerprints
                || !_bookmarkMeta.TryGetValue(bm.Id, out var meta)
                || meta.DeletedAtTicks.HasValue
                || meta.ContentFingerprint != fingerprint)
            {
                _bookmarkMeta[bm.Id] = new SyncMeta(fingerprint, ticks);
            }

            entries.Add(new SyncManifestEntry(
                bm.Id,
                string.IsNullOrWhiteSpace(bm.Title) ? bm.Url : bm.Title,
                ticks,
                fingerprint));
        }

        foreach (var (id, meta) in _bookmarkMeta)
        {
            if (seen.Contains(id) || !meta.DeletedAtTicks.HasValue)
                continue;

            entries.Add(new SyncManifestEntry(
                id,
                "(deleted)",
                meta.LastUpdatedTicks,
                DeleteSyncPayload.AckValue(meta.DeletedAtTicks.Value),
                meta.DeletedAtTicks));
        }

        return Task.FromResult(entries);
    }

    public Task<List<SyncManifestEntry>> LoadFolderManifestEntriesAsync(
        bool backfillMissingFingerprints = false,
        CancellationToken ct = default)
    {
        var entries = new List<SyncManifestEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var folder in _folders)
        {
            seen.Add(folder.Id);
            var fingerprint = SyncFingerprint.ForBookmarkFolder(folder);
            var ticks = folder.EffectiveUpdatedAtUtc.Ticks;
            if (backfillMissingFingerprints
                || !_folderMeta.TryGetValue(folder.Id, out var meta)
                || meta.DeletedAtTicks.HasValue
                || meta.ContentFingerprint != fingerprint)
            {
                _folderMeta[folder.Id] = new SyncMeta(fingerprint, ticks);
            }

            entries.Add(new SyncManifestEntry(
                folder.Id,
                folder.Name,
                ticks,
                fingerprint));
        }

        foreach (var (id, meta) in _folderMeta)
        {
            if (seen.Contains(id) || !meta.DeletedAtTicks.HasValue)
                continue;

            entries.Add(new SyncManifestEntry(
                id,
                "(deleted)",
                meta.LastUpdatedTicks,
                DeleteSyncPayload.AckValue(meta.DeletedAtTicks.Value),
                meta.DeletedAtTicks));
        }

        return Task.FromResult(entries);
    }

    public Task<BrowserBookmark?> GetBookmarkByIdAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(_bookmarks.FirstOrDefault(b => b.Id == id));

    public Task<BrowserBookmarkFolder?> GetFolderByIdAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(_folders.FirstOrDefault(f => f.Id == id));

    public async Task ApplyBookmarkPayloadAsync(BrowserBookmark bookmark, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(bookmark.Id))
            return;

        EnsureDefaultFolder();
        if (string.IsNullOrWhiteSpace(bookmark.FolderId) || _folders.All(f => f.Id != bookmark.FolderId))
            bookmark = bookmark with { FolderId = BrowserBookmarkFolders.DefaultFolderId };

        var index = _bookmarks.FindIndex(b => b.Id == bookmark.Id);
        if (index >= 0)
            _bookmarks[index] = bookmark;
        else
            _bookmarks.Add(bookmark);

        _bookmarkMeta.Remove(bookmark.Id);
        UpsertLiveBookmarkMeta(bookmark);
        await SaveBookmarksAsync(ct);
        await SaveBookmarkMetaAsync(ct);
        Changed?.Invoke();
    }

    public async Task ApplyFolderPayloadAsync(BrowserBookmarkFolder folder, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(folder.Id))
            return;

        var index = _folders.FindIndex(f => f.Id == folder.Id);
        if (index >= 0)
            _folders[index] = folder;
        else
            _folders.Add(folder);

        _folderMeta.Remove(folder.Id);
        UpsertLiveFolderMeta(folder);
        await SaveFoldersAsync(ct);
        await SaveFolderMetaAsync(ct);
        Changed?.Invoke();
    }

    public Task<bool> ShouldAcceptIncomingBookmarkAsync(BrowserBookmark bookmark, CancellationToken ct = default)
    {
        if (_bookmarkMeta.TryGetValue(bookmark.Id, out var meta) && meta.DeletedAtTicks.HasValue)
            return Task.FromResult(bookmark.EffectiveUpdatedAtUtc.Ticks > meta.DeletedAtTicks.Value);

        var local = _bookmarks.FirstOrDefault(b => b.Id == bookmark.Id);
        if (local == null)
            return Task.FromResult(true);

        return Task.FromResult(bookmark.EffectiveUpdatedAtUtc.Ticks >= local.EffectiveUpdatedAtUtc.Ticks);
    }

    public Task<bool> ShouldAcceptIncomingFolderAsync(BrowserBookmarkFolder folder, CancellationToken ct = default)
    {
        if (_folderMeta.TryGetValue(folder.Id, out var meta) && meta.DeletedAtTicks.HasValue)
            return Task.FromResult(folder.EffectiveUpdatedAtUtc.Ticks > meta.DeletedAtTicks.Value);

        var local = _folders.FirstOrDefault(f => f.Id == folder.Id);
        if (local == null)
            return Task.FromResult(true);

        return Task.FromResult(folder.EffectiveUpdatedAtUtc.Ticks >= local.EffectiveUpdatedAtUtc.Ticks);
    }

    public async Task<DateTime> TombstoneBookmarkAsync(string id, CancellationToken ct = default)
    {
        var deletedAt = DateTime.UtcNow;
        var removed = _bookmarks.RemoveAll(b => b.Id == id) > 0;
        if (!removed && _bookmarkMeta.TryGetValue(id, out var existing) && existing.DeletedAtTicks.HasValue)
            return new DateTime(existing.DeletedAtTicks.Value, DateTimeKind.Utc);

        _bookmarkMeta[id] = new SyncMeta(
            DeleteSyncPayload.AckValue(deletedAt.Ticks),
            deletedAt.Ticks,
            deletedAt.Ticks);

        if (removed)
            await SaveBookmarksAsync(ct);
        await SaveBookmarkMetaAsync(ct);
        if (removed)
            Changed?.Invoke();
        return deletedAt;
    }

    public async Task<DateTime> TombstoneFolderAsync(string id, CancellationToken ct = default)
    {
        if (id is BrowserBookmarkFolders.DefaultFolderId or BrowserBookmarkFolders.BookmarksBarFolderId)
            return DateTime.UtcNow;

        var deletedAt = DateTime.UtcNow;
        var removed = _folders.RemoveAll(f => f.Id == id) > 0;
        if (!removed && _folderMeta.TryGetValue(id, out var existing) && existing.DeletedAtTicks.HasValue)
            return new DateTime(existing.DeletedAtTicks.Value, DateTimeKind.Utc);

        _folderMeta[id] = new SyncMeta(
            DeleteSyncPayload.AckValue(deletedAt.Ticks),
            deletedAt.Ticks,
            deletedAt.Ticks);

        if (removed)
            await SaveFoldersAsync(ct);
        await SaveFolderMetaAsync(ct);
        if (removed)
            Changed?.Invoke();
        return deletedAt;
    }

    public async Task<bool> TryApplyRemoteBookmarkDeleteAsync(string id, long deletedAtTicks, CancellationToken ct = default)
    {
        if (_bookmarkMeta.TryGetValue(id, out var meta) && meta.DeletedAtTicks.HasValue)
        {
            if (meta.DeletedAtTicks.Value >= deletedAtTicks)
                return false;
        }
        else
        {
            var local = _bookmarks.FirstOrDefault(b => b.Id == id);
            if (local != null && local.EffectiveUpdatedAtUtc.Ticks > deletedAtTicks)
                return false;
            if (local == null && meta is null)
                return false;
        }

        _bookmarks.RemoveAll(b => b.Id == id);
        _bookmarkMeta[id] = new SyncMeta(
            DeleteSyncPayload.AckValue(deletedAtTicks),
            deletedAtTicks,
            deletedAtTicks);
        await SaveBookmarksAsync(ct);
        await SaveBookmarkMetaAsync(ct);
        Changed?.Invoke();
        return true;
    }

    public async Task<bool> TryApplyRemoteFolderDeleteAsync(string id, long deletedAtTicks, CancellationToken ct = default)
    {
        if (id is BrowserBookmarkFolders.DefaultFolderId or BrowserBookmarkFolders.BookmarksBarFolderId)
            return false;

        if (_folderMeta.TryGetValue(id, out var meta) && meta.DeletedAtTicks.HasValue)
        {
            if (meta.DeletedAtTicks.Value >= deletedAtTicks)
                return false;
        }
        else
        {
            var local = _folders.FirstOrDefault(f => f.Id == id);
            if (local != null && local.EffectiveUpdatedAtUtc.Ticks > deletedAtTicks)
                return false;
            if (local == null && meta is null)
                return false;
        }

        _folders.RemoveAll(f => f.Id == id);
        _folderMeta[id] = new SyncMeta(
            DeleteSyncPayload.AckValue(deletedAtTicks),
            deletedAtTicks,
            deletedAtTicks);
        await SaveFoldersAsync(ct);
        await SaveFolderMetaAsync(ct);
        Changed?.Invoke();
        return true;
    }

    private void UpsertLiveBookmarkMeta(BrowserBookmark bookmark)
    {
        _bookmarkMeta[bookmark.Id] = new SyncMeta(
            SyncFingerprint.ForBookmark(bookmark),
            bookmark.EffectiveUpdatedAtUtc.Ticks);
    }

    private void UpsertLiveFolderMeta(BrowserBookmarkFolder folder)
    {
        _folderMeta[folder.Id] = new SyncMeta(
            SyncFingerprint.ForBookmarkFolder(folder),
            folder.EffectiveUpdatedAtUtc.Ticks);
    }

    private void EnsureDefaultFolder()
    {
        if (_folders.Any(f => f.Id == BrowserBookmarkFolders.DefaultFolderId))
            return;

        var folder = new BrowserBookmarkFolder(
            BrowserBookmarkFolders.DefaultFolderId,
            BrowserBookmarkFolders.DefaultFolderName,
            null,
            DateTime.UtcNow,
            0,
            DateTime.UtcNow);
        _folders.Insert(0, folder);
        UpsertLiveFolderMeta(folder);
    }

    private void EnsureBookmarksBarFolder()
    {
        if (_folders.Any(f => f.Id == BrowserBookmarkFolders.BookmarksBarFolderId))
            return;

        var folder = new BrowserBookmarkFolder(
            BrowserBookmarkFolders.BookmarksBarFolderId,
            BrowserBookmarkFolders.BookmarksBarFolderName,
            null,
            DateTime.UtcNow,
            1,
            DateTime.UtcNow);
        _folders.Add(folder);
        UpsertLiveFolderMeta(folder);
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

    private async Task SaveBookmarkMetaAsync(CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(_bookmarkMeta);
        await _db.SetStringAsync(BookmarkMetaKey, json, ct);
    }

    private async Task SaveFolderMetaAsync(CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(_folderMeta);
        await _db.SetStringAsync(FolderMetaKey, json, ct);
    }

    private async Task<Dictionary<string, SyncMeta>> LoadMetaAsync(string key, CancellationToken ct)
    {
        var json = await _db.GetStringAsync(key, ct);
        if (string.IsNullOrEmpty(json))
            return new Dictionary<string, SyncMeta>(StringComparer.Ordinal);

        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, SyncMeta>>(json);
            return loaded != null
                ? new Dictionary<string, SyncMeta>(loaded, StringComparer.Ordinal)
                : new Dictionary<string, SyncMeta>(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, SyncMeta>(StringComparer.Ordinal);
        }
    }

    private async Task NormalizeFolderSortOrdersAsync(string folderId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
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

            var updated = _bookmarks[index] with { SortOrder = i, UpdatedAtUtc = now };
            _bookmarks[index] = updated;
            UpsertLiveBookmarkMeta(updated);
        }

        await SaveBookmarksAsync(ct);
        await SaveBookmarkMetaAsync(ct);
    }
}
