using App.Core.Auth;
using App.Core.Storage;
using System.Text.Json;

namespace App.Maui.Services;

public class SqliteNoteStore : INoteStore
{
    private const string NotePrefix = "n-wasmchat-note-";

    private readonly IAuthService _auth;
    private readonly ICryptoService _crypto;
    private readonly SqliteHistoryDatabase _db;
    private readonly INoteAudioStore _audio;

    public SqliteNoteStore(
        IAuthService auth,
        ICryptoService crypto,
        SqliteHistoryDatabase db,
        INoteAudioStore audio)
    {
        _auth = auth;
        _crypto = crypto;
        _db = db;
        _audio = audio;
    }

    private string GetPrefix() => StorageNamespace.GetPrefix(_auth);

    private async Task<string> GetNoteKeyAsync() =>
        await _auth.GetOrCreateHistoryEncryptionKeyAsync();

    public async Task<List<LocalNote>> LoadIndexAsync(CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var metas = await _db.GetNoteMetasByNamespaceAsync(ns, ct);
        var live = metas.Where(m => string.IsNullOrEmpty(m.DeletedAt)).ToList();

        // Backfill stable sort orders once so sync-driven lastUpdated changes do not reshuffle the sidebar.
        if (live.Count > 0 && live.All(m => m.SortOrder == 0))
        {
            var ordered = live.OrderByDescending(m => m.LastUpdated).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var m = ordered[i] with { SortOrder = i };
                await _db.UpsertNoteMetaAsync(m, ct);
                ordered[i] = m;
            }
            live = ordered;
        }

        return live
            .OrderBy(m => m.SortOrder)
            .ThenByDescending(m => m.LastUpdated)
            .Select(m => new LocalNote(
                m.Id,
                string.IsNullOrWhiteSpace(m.Title) ? "(empty)" : m.Title,
                DateTime.Parse(m.LastUpdated),
                m.IsPasswordProtected,
                m.SortOrder,
                m.ProtectionChangedTicks))
            .ToList();
    }

    public async Task<List<SyncManifestEntry>> LoadManifestEntriesAsync(bool backfillMissingFingerprints = false, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var metas = await _db.GetNoteMetasByNamespaceAsync(ns, ct);
        var entries = new List<SyncManifestEntry>();

        foreach (var m in metas)
        {
            var title = string.IsNullOrWhiteSpace(m.Title) ? "(empty)" : m.Title;
            long? deletedAtTicks = null;
            if (!string.IsNullOrEmpty(m.DeletedAt))
                deletedAtTicks = DateTime.Parse(m.DeletedAt).Ticks;

            var fingerprint = deletedAtTicks.HasValue
                ? DeleteSyncPayload.AckValue(deletedAtTicks.Value)
                : m.ContentFingerprint ?? "";

            if (!deletedAtTicks.HasValue && backfillMissingFingerprints && string.IsNullOrEmpty(fingerprint))
            {
                var noteEntries = await LoadNoteAsync(m.Id, ct);
                fingerprint = SyncFingerprint.ForNote(m.Id, title, noteEntries, m.IsPasswordProtected, m.ProtectionChangedTicks);
                await _db.UpsertNoteMetaAsync(m with { ContentFingerprint = fingerprint }, ct);
            }

            entries.Add(new SyncManifestEntry(
                m.Id,
                title,
                DateTime.Parse(m.LastUpdated).Ticks,
                fingerprint,
                deletedAtTicks));
        }

        return entries;
    }

    public async Task<List<ChatMessage>> LoadNoteAsync(string id, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var storageKey = ns + NotePrefix + id;
        var encrypted = await _db.GetNoteContentAsync(storageKey, ct);
        if (string.IsNullOrEmpty(encrypted)) return new List<ChatMessage>();

        var keyB64 = await GetNoteKeyAsync();
        var json = encrypted;
        if (!string.IsNullOrEmpty(keyB64))
            json = await _crypto.DecryptAsync(keyB64, encrypted, ct);

        if (string.IsNullOrEmpty(json)) return new List<ChatMessage>();

        var messages = JsonSerializer.Deserialize<List<ChatMessage>>(json) ?? new List<ChatMessage>();
        return ChatMessageHelper.NormalizeAll(messages);
    }

    public async Task SaveNoteAsync(string id, List<ChatMessage> entries, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var storageKey = ns + NotePrefix + id;
        var json = JsonSerializer.Serialize(entries);
        var keyB64 = await GetNoteKeyAsync();
        var toStore = json;
        if (!string.IsNullOrEmpty(keyB64))
            toStore = await _crypto.EncryptAsync(keyB64, json, ct);

        await _db.SetNoteContentAsync(storageKey, toStore, ct);
    }

    public async Task<DateTime> DeleteNoteAsync(string id, CancellationToken ct = default)
    {
        var deletedAt = DateTime.UtcNow;
        var deletedAtIso = deletedAt.ToString("o");
        var ns = GetPrefix();
        var storageKey = ns + NotePrefix + id;
        var existing = await _db.GetNoteMetaByIdAsync(ns, id, ct);
        var title = existing?.Title ?? "(deleted)";
        bool syncEnabled = _auth.IsAuthenticated && !string.IsNullOrEmpty(_auth.Email);

        await _db.UpsertNoteMetaAsync(new SqliteHistoryDatabase.NoteMetaRow(
            existing?.StorageKey ?? storageKey,
            id,
            ns,
            title,
            deletedAtIso,
            syncEnabled,
            DeleteSyncPayload.AckValue(deletedAt.Ticks),
            deletedAtIso,
            existing?.IsPasswordProtected ?? false,
            existing?.SortOrder ?? 0,
            existing?.ProtectionChangedTicks ?? 0), ct);

        await _db.DeleteNoteContentAsync(existing?.StorageKey ?? storageKey, ct);
        try { await _audio.DeleteByNotebookAsync(id, ct); }
        catch (Exception ex) { Console.WriteLine($"[Notes] Failed to delete notebook audio: {ex.Message}"); }
        return deletedAt;
    }

    public async Task<string?> GetMetaTitleAsync(string id, CancellationToken ct = default)
    {
        var meta = await _db.GetNoteMetaByIdAsync(GetPrefix(), id, ct);
        return meta?.Title;
    }

    public async Task UpdateIndexAfterSaveAsync(string id, string title, List<ChatMessage>? entriesForFingerprint = null, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var storageKey = ns + NotePrefix + id;
        bool syncEnabled = _auth.IsAuthenticated && !string.IsNullOrEmpty(_auth.Email);
        var normalizedTitle = string.IsNullOrWhiteSpace(title) ? "(empty)" : title;

        var existing = await _db.GetNoteMetaByIdAsync(ns, id, ct);
        var entries = entriesForFingerprint ?? await LoadNoteAsync(id, ct);
        var isProtected = existing?.IsPasswordProtected ?? false;
        var protectionTicks = existing?.ProtectionChangedTicks ?? 0;
        var contentFingerprint = SyncFingerprint.ForNote(id, normalizedTitle, entries, isProtected, protectionTicks);

        var existingTitle = existing?.Title;
        var resolvedTitle = ChatMessageHelper.ResolveIncomingNoteTitle(normalizedTitle, existingTitle);
        int sortOrder;
        if (existing is null)
        {
            var index = await LoadIndexAsync(ct);
            sortOrder = index.Count == 0 ? 0 : index.Max(n => n.SortOrder) + 1;
        }
        else
        {
            sortOrder = existing.SortOrder;
        }

        await _db.UpsertNoteMetaAsync(new SqliteHistoryDatabase.NoteMetaRow(
            storageKey,
            id,
            ns,
            resolvedTitle,
            DateTime.UtcNow.ToString("o"),
            syncEnabled,
            contentFingerprint,
            null,
            isProtected,
            sortOrder,
            protectionTicks), ct);
    }

    public async Task SetPasswordProtectedAsync(string id, bool isProtected, long? protectionChangedTicks = null, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var existing = await _db.GetNoteMetaByIdAsync(ns, id, ct);
        if (existing == null)
            return;

        var title = string.IsNullOrWhiteSpace(existing.Title) ? "(empty)" : existing.Title;
        var entries = await LoadNoteAsync(id, ct);
        var ticks = protectionChangedTicks is > 0 ? protectionChangedTicks.Value : DateTime.UtcNow.Ticks;
        var fingerprint = SyncFingerprint.ForNote(id, title, entries, isProtected, ticks);

        await _db.UpsertNoteMetaAsync(existing with
        {
            IsPasswordProtected = isProtected,
            ProtectionChangedTicks = ticks,
            ContentFingerprint = fingerprint,
            LastUpdated = DateTime.UtcNow.ToString("o")
        }, ct);
    }

    public async Task ReorderNotesAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default)
    {
        if (orderedIds.Count == 0)
            return;

        var ns = GetPrefix();
        for (var i = 0; i < orderedIds.Count; i++)
        {
            var existing = await _db.GetNoteMetaByIdAsync(ns, orderedIds[i], ct);
            if (existing == null)
                continue;
            await _db.UpsertNoteMetaAsync(existing with { SortOrder = i }, ct);
        }
    }

    public async Task<bool> ShouldAcceptIncomingContentAsync(string id, List<ChatMessage> entries, CancellationToken ct = default)
    {
        var meta = await _db.GetNoteMetaByIdAsync(GetPrefix(), id, ct);
        if (meta == null || string.IsNullOrEmpty(meta.DeletedAt))
            return true;

        var deletedAtTicks = DateTime.Parse(meta.DeletedAt).Ticks;
        return ChatMessageHelper.GetLatestContentTicks(entries) > deletedAtTicks;
    }

    public async Task<bool> TryApplyRemoteDeleteAsync(string id, long deletedAtTicks, CancellationToken ct = default)
    {
        var meta = await _db.GetNoteMetaByIdAsync(GetPrefix(), id, ct);
        if (meta == null)
            return false;

        if (!string.IsNullOrEmpty(meta.DeletedAt))
        {
            var localDeletedAt = DateTime.Parse(meta.DeletedAt);
            if (localDeletedAt.Ticks >= deletedAtTicks)
                return false;
        }
        else
        {
            var contentTicks = ChatMessageHelper.GetLatestContentTicks(await LoadNoteAsync(id, ct));
            if (contentTicks > deletedAtTicks || DateTime.Parse(meta.LastUpdated).Ticks > deletedAtTicks)
                return false;
        }

        await DeleteNoteAsync(id, ct);
        return true;
    }
}