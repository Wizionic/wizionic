using ChatfishApp.Core.Auth;
using ChatfishApp.Core.Storage;
using System.Text.Json;

namespace ChatfishApp.Maui.Services;

public class SqliteNoteStore : INoteStore
{
    private const string NotePrefix = "n-wasmchat-note-";

    private readonly IAuthService _auth;
    private readonly ICryptoService _crypto;
    private readonly SqliteHistoryDatabase _db;

    public SqliteNoteStore(IAuthService auth, ICryptoService crypto, SqliteHistoryDatabase db)
    {
        _auth = auth;
        _crypto = crypto;
        _db = db;
    }

    private string GetPrefix() => StorageNamespace.GetPrefix(_auth);

    private async Task<string> GetNoteKeyAsync() =>
        await _auth.GetOrCreateHistoryEncryptionKeyAsync();

    public async Task<List<LocalNote>> LoadIndexAsync(CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var metas = await _db.GetNoteMetasByNamespaceAsync(ns, ct);
        return metas
            .Where(m => string.IsNullOrEmpty(m.DeletedAt))
            .OrderByDescending(m => m.LastUpdated)
            .Select(m => new LocalNote(m.Id, string.IsNullOrWhiteSpace(m.Title) ? "(empty)" : m.Title, DateTime.Parse(m.LastUpdated)))
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
                fingerprint = SyncFingerprint.ForNote(m.Id, title, noteEntries);
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
            deletedAtIso), ct);

        await _db.DeleteNoteContentAsync(existing?.StorageKey ?? storageKey, ct);
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

        var entries = entriesForFingerprint ?? await LoadNoteAsync(id, ct);
        var contentFingerprint = SyncFingerprint.ForNote(id, normalizedTitle, entries);

        var existingTitle = (await _db.GetNoteMetaByIdAsync(ns, id, ct))?.Title;
        var resolvedTitle = ChatMessageHelper.ResolveIncomingNoteTitle(normalizedTitle, existingTitle);

        await _db.UpsertNoteMetaAsync(new SqliteHistoryDatabase.NoteMetaRow(
            storageKey,
            id,
            ns,
            resolvedTitle,
            DateTime.UtcNow.ToString("o"),
            syncEnabled,
            contentFingerprint,
            null), ct);
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