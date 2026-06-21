using ChatfishApp.Core.Auth;
using ChatfishApp.Core.Storage;
using Microsoft.JSInterop;
using System.Text.Json;

namespace ChatfishApp.Client.Services;

/// <summary>
/// Client-side storage for notes (parallel to WasmConversationStore).
/// Uses noteMetas + noteContents IDB object stores with AES-256-GCM encryption.
/// </summary>
public class WasmNoteStore : INoteStore
{
    private const string NotePrefix = "n-wasmchat-note-";

    private readonly IAuthService _auth;
    private readonly ICryptoService _crypto;
    private readonly IJSRuntime _js;

    public WasmNoteStore(IAuthService auth, ICryptoService crypto, IJSRuntime js)
    {
        _auth = auth;
        _crypto = crypto;
        _js = js;
    }

    private record StoredMeta(string key, string id, string @namespace, string title, string lastUpdated, bool syncEnabled, string? contentFingerprint, string? deletedAt);

    private string GetPrefix() => StorageNamespace.GetPrefix(_auth);

    private async Task<string> GetNoteKeyAsync() =>
        await _auth.GetOrCreateHistoryEncryptionKeyAsync();

    private async Task<StoredMeta?> GetMetaByIdAsync(string id)
    {
        var ns = GetPrefix();
        var metas = await _js.InvokeAsync<List<StoredMeta>>("idbGetNoteMetasByNamespace", ns);
        return metas.FirstOrDefault(m => string.Equals(m.id, id, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<List<SyncManifestEntry>> LoadManifestEntriesAsync(bool backfillMissingFingerprints = false, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var metas = await _js.InvokeAsync<List<StoredMeta>>("idbGetNoteMetasByNamespace", ns);
        var entries = new List<SyncManifestEntry>();

        foreach (var m in metas)
        {
            var title = string.IsNullOrWhiteSpace(m.title) ? "(empty)" : m.title;
            long? deletedAtTicks = null;
            if (!string.IsNullOrEmpty(m.deletedAt))
                deletedAtTicks = DateTime.Parse(m.deletedAt).Ticks;

            var fingerprint = deletedAtTicks.HasValue
                ? DeleteSyncPayload.AckValue(deletedAtTicks.Value)
                : m.contentFingerprint ?? "";

            if (!deletedAtTicks.HasValue && backfillMissingFingerprints && string.IsNullOrEmpty(fingerprint))
            {
                var noteEntries = await LoadNoteAsync(m.id, ct);
                fingerprint = SyncFingerprint.ForNote(m.id, title, noteEntries);
                await PersistContentFingerprintAsync(m, title, fingerprint, deletedAt: null);
            }

            entries.Add(new SyncManifestEntry(
                m.id,
                title,
                DateTime.Parse(m.lastUpdated).Ticks,
                fingerprint,
                deletedAtTicks));
        }

        return entries;
    }

    private async Task PersistContentFingerprintAsync(StoredMeta meta, string title, string fingerprint, string? deletedAt)
    {
        await _js.InvokeVoidAsync("idbPutNoteMeta", new
        {
            key = meta.key,
            id = meta.id,
            @namespace = meta.@namespace,
            title,
            lastUpdated = meta.lastUpdated,
            syncEnabled = meta.syncEnabled,
            contentFingerprint = fingerprint,
            deletedAt = deletedAt ?? ""
        });
    }

    public async Task<List<LocalNote>> LoadIndexAsync(CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var metas = await _js.InvokeAsync<List<StoredMeta>>("idbGetNoteMetasByNamespace", ns);
        return metas
            .Where(m => string.IsNullOrEmpty(m.deletedAt))
            .OrderByDescending(m => m.lastUpdated)
            .Select(m => new LocalNote(m.id, string.IsNullOrWhiteSpace(m.title) ? "(empty)" : m.title, DateTime.Parse(m.lastUpdated)))
            .ToList();
    }

    public async Task<List<ChatMessage>> LoadNoteAsync(string id, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var fullKey = ns + NotePrefix + id;
        var encrypted = await _js.InvokeAsync<string>("idbGetNoteContent", fullKey);
        if (string.IsNullOrEmpty(encrypted)) return new List<ChatMessage>();

        var keyB64 = await GetNoteKeyAsync();
        var json = encrypted;
        if (!string.IsNullOrEmpty(keyB64))
            json = await _crypto.DecryptAsync(keyB64, encrypted);

        if (string.IsNullOrEmpty(json)) return new List<ChatMessage>();

        var messages = JsonSerializer.Deserialize<List<ChatMessage>>(json) ?? new List<ChatMessage>();
        return ChatMessageHelper.NormalizeAll(messages);
    }

    public async Task<string?> GetMetaTitleAsync(string id, CancellationToken ct = default)
    {
        var meta = await GetMetaByIdAsync(id);
        return meta?.title;
    }

    public async Task SaveNoteAsync(string id, List<ChatMessage> entries, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var fullKey = ns + NotePrefix + id;
        var json = JsonSerializer.Serialize(entries);
        var keyB64 = await GetNoteKeyAsync();
        var toStore = json;
        if (!string.IsNullOrEmpty(keyB64))
            toStore = await _crypto.EncryptAsync(keyB64, json);
        await _js.InvokeVoidAsync("idbPutNoteContent", fullKey, toStore);
    }

    public async Task UpdateIndexAfterSaveAsync(string id, string title, List<ChatMessage>? entriesForFingerprint = null, CancellationToken ct = default)
    {
        var ns = GetPrefix();
        var metaKey = ns + NotePrefix + id;
        bool syncEnabled = _auth.IsAuthenticated && !string.IsNullOrEmpty(_auth.Email);
        var normalizedTitle = string.IsNullOrWhiteSpace(title) ? "(empty)" : title;

        var entries = entriesForFingerprint ?? await LoadNoteAsync(id, ct);
        var contentFingerprint = SyncFingerprint.ForNote(id, normalizedTitle, entries);

        var existingTitle = (await GetMetaByIdAsync(id))?.title;
        var resolvedTitle = ChatMessageHelper.ResolveIncomingNoteTitle(normalizedTitle, existingTitle);

        await _js.InvokeVoidAsync("idbPutNoteMeta", new
        {
            key = metaKey,
            id,
            @namespace = ns,
            title = resolvedTitle,
            lastUpdated = DateTime.UtcNow.ToString("o"),
            syncEnabled,
            contentFingerprint,
            deletedAt = ""
        });
    }

    private async Task<DateTime> TombstoneDeleteNoteAsync(string id, DateTime? deletedAtUtc = null)
    {
        var deletedAt = deletedAtUtc ?? DateTime.UtcNow;
        var deletedAtIso = deletedAt.ToString("o");
        var ns = GetPrefix();
        var metaKey = ns + NotePrefix + id;
        var existing = await GetMetaByIdAsync(id);
        var title = existing?.title ?? "(deleted)";
        bool syncEnabled = _auth.IsAuthenticated && !string.IsNullOrEmpty(_auth.Email);

        await _js.InvokeVoidAsync("idbPutNoteMeta", new
        {
            key = existing?.key ?? metaKey,
            id,
            @namespace = ns,
            title,
            lastUpdated = deletedAtIso,
            syncEnabled,
            contentFingerprint = DeleteSyncPayload.AckValue(deletedAt.Ticks),
            deletedAt = deletedAtIso
        });

        await _js.InvokeVoidAsync("idbDeleteNoteContent", existing?.key ?? metaKey);
        return deletedAt;
    }

    public Task<DateTime> DeleteNoteAsync(string id, CancellationToken ct = default) =>
        TombstoneDeleteNoteAsync(id);

    public async Task<bool> ShouldAcceptIncomingContentAsync(string id, List<ChatMessage> entries, CancellationToken ct = default)
    {
        var meta = await GetMetaByIdAsync(id);
        if (meta == null || string.IsNullOrEmpty(meta.deletedAt))
            return true;

        var deletedAtTicks = DateTime.Parse(meta.deletedAt).Ticks;
        return ChatMessageHelper.GetLatestContentTicks(entries) > deletedAtTicks;
    }

    public async Task<bool> TryApplyRemoteDeleteAsync(string id, long deletedAtTicks, CancellationToken ct = default)
    {
        var remoteDeletedAt = new DateTime(deletedAtTicks, DateTimeKind.Utc);
        var meta = await GetMetaByIdAsync(id);
        if (meta == null)
            return false;

        if (!string.IsNullOrEmpty(meta.deletedAt))
        {
            var localDeletedAt = DateTime.Parse(meta.deletedAt);
            if (localDeletedAt.Ticks >= deletedAtTicks)
                return false;
        }
        else
        {
            var contentTicks = ChatMessageHelper.GetLatestContentTicks(await LoadNoteAsync(id, ct));
            if (contentTicks > deletedAtTicks || DateTime.Parse(meta.lastUpdated).Ticks > deletedAtTicks)
                return false;
        }

        await TombstoneDeleteNoteAsync(id, remoteDeletedAt);
        return true;
    }
}