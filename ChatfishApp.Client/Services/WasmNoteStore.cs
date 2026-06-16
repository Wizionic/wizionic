using Microsoft.JSInterop;
using System.Text;
using System.Text.Json;
using static ChatfishApp.Client.Services.WasmConversationStore;

namespace ChatfishApp.Client.Services;

/// <summary>
/// Client-side storage for notes (parallel to WasmConversationStore but for user notes).
/// Uses noteMetas + noteContents IDB object stores with AES-256-GCM encryption.
/// Each note contains a list of messages stored identically to conversation ChatMessage records.
/// </summary>
public class WasmNoteStore
{
    private const string NotePrefix = "n-wasmchat-note-";

    private readonly WasmAuthService _auth;
    private readonly WasmCryptoService _crypto;

    public WasmNoteStore(WasmAuthService auth, WasmCryptoService crypto)
    {
        _auth = auth;
        _crypto = crypto;
    }

    public record LocalNote(string Id, string Title, DateTime LastUpdated);

    public record SyncManifestEntry(string Id, string Title, long LastUpdatedTicks, string ContentFingerprint, long? DeletedAtTicks = null)
    {
        public bool IsDeleted => DeletedAtTicks.HasValue;
    }

    private record StoredMeta(string key, string id, string @namespace, string title, string lastUpdated, bool syncEnabled, string? contentFingerprint, string? deletedAt);

    public async Task<List<SyncManifestEntry>> LoadManifestEntriesAsync(IJSRuntime js, bool backfillMissingFingerprints = false)
    {
        var ns = GetPrefix();
        var metas = await js.InvokeAsync<List<StoredMeta>>("idbGetNoteMetasByNamespace", ns);
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
                var noteEntries = await LoadNoteAsync(js, m.id);
                fingerprint = SyncFingerprint.ForNote(m.id, title, noteEntries);
                await PersistContentFingerprintAsync(js, m, title, fingerprint, deletedAt: null);
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

    private async Task PersistContentFingerprintAsync(IJSRuntime js, StoredMeta meta, string title, string fingerprint, string? deletedAt)
    {
        await js.InvokeVoidAsync("idbPutNoteMeta", new
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

    private static string GetStableHash(string input) => 
        string.IsNullOrEmpty(input) ? "00000000" : BitConverter.ToString(System.Security.Cryptography.SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(input)), 0, 4).Replace("-", "").ToLowerInvariant();

    private string GetPrefix()
    {
        if (_auth.IsAuthenticated)
        {
            if (!string.IsNullOrEmpty(_auth.UserId)) return $"u-{_auth.UserId}-";
            if (!string.IsNullOrEmpty(_auth.Email)) return $"e-{GetStableHash(_auth.Email)}-";
        }
        return "wasmchat-";
    }

    private async Task<string> GetNoteKeyAsync(IJSRuntime js) => await _auth.GetOrCreateHistoryEncryptionKeyAsync(js);

    private async Task<StoredMeta?> GetMetaByIdAsync(IJSRuntime js, string id)
    {
        var ns = GetPrefix();
        var metas = await js.InvokeAsync<List<StoredMeta>>("idbGetNoteMetasByNamespace", ns);
        return metas.FirstOrDefault(m => string.Equals(m.id, id, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<List<LocalNote>> LoadIndexAsync(IJSRuntime js)
    {
        var ns = GetPrefix();
        var metas = await js.InvokeAsync<List<StoredMeta>>("idbGetNoteMetasByNamespace", ns);
        return metas
            .Where(m => string.IsNullOrEmpty(m.deletedAt))
            .OrderByDescending(m => m.lastUpdated)
            .Select(m => new LocalNote(m.id, string.IsNullOrWhiteSpace(m.title) ? "(empty)" : m.title, DateTime.Parse(m.lastUpdated)))
            .ToList();
    }

    public async Task<List<ChatMessage>> LoadNoteAsync(IJSRuntime js, string id)
    {
        var ns = GetPrefix();
        var fullKey = ns + NotePrefix + id;
        var encrypted = await js.InvokeAsync<string>("idbGetNoteContent", fullKey);
        if (string.IsNullOrEmpty(encrypted)) return new List<ChatMessage>();

        var keyB64 = await GetNoteKeyAsync(js);
        string json = encrypted;
        if (!string.IsNullOrEmpty(keyB64))
            json = await _crypto.DecryptAsync(keyB64, encrypted);

        if (string.IsNullOrEmpty(json)) return new List<ChatMessage>();

        var messages = JsonSerializer.Deserialize<List<ChatMessage>>(json) ?? new List<ChatMessage>();
        return ChatMessageHelper.NormalizeAll(messages);
    }

    public async Task<string?> GetMetaTitleAsync(IJSRuntime js, string id)
    {
        var meta = await GetMetaByIdAsync(js, id);
        return meta?.title;
    }

    public async Task SaveNoteAsync(IJSRuntime js, string id, List<ChatMessage> entries)
    {
        var ns = GetPrefix();
        var fullKey = ns + NotePrefix + id;
        var json = JsonSerializer.Serialize(entries);
        var keyB64 = await GetNoteKeyAsync(js);
        string toStore = json;
        if (!string.IsNullOrEmpty(keyB64))
            toStore = await _crypto.EncryptAsync(keyB64, json);
        await js.InvokeVoidAsync("idbPutNoteContent", fullKey, toStore);
    }

    public async Task UpdateIndexAfterSaveAsync(IJSRuntime js, string id, string title, List<ChatMessage>? entriesForFingerprint = null)
    {
        var ns = GetPrefix();
        var metaKey = ns + NotePrefix + id;
        bool syncEnabled = _auth.IsAuthenticated && !string.IsNullOrEmpty(_auth.Email);
        var normalizedTitle = string.IsNullOrWhiteSpace(title) ? "(empty)" : title;

        var entries = entriesForFingerprint ?? await LoadNoteAsync(js, id);
        var contentFingerprint = SyncFingerprint.ForNote(id, normalizedTitle, entries);

        var existingTitle = (await GetMetaByIdAsync(js, id))?.title;
        var resolvedTitle = ChatMessageHelper.ResolveIncomingNoteTitle(normalizedTitle, existingTitle);

        await js.InvokeVoidAsync("idbPutNoteMeta", new {
            key = metaKey, id, @namespace = ns,
            title = resolvedTitle,
            lastUpdated = DateTime.UtcNow.ToString("o"), syncEnabled,
            contentFingerprint,
            deletedAt = "" });
    }

    /// <summary>
    /// Soft-delete: keep tombstone meta, remove content blob. Returns UTC delete time for sync.
    /// </summary>
    public async Task<DateTime> TombstoneDeleteNoteAsync(IJSRuntime js, string id, DateTime? deletedAtUtc = null)
    {
        var deletedAt = deletedAtUtc ?? DateTime.UtcNow;
        var deletedAtIso = deletedAt.ToString("o");
        var ns = GetPrefix();
        var metaKey = ns + NotePrefix + id;
        var existing = await GetMetaByIdAsync(js, id);
        var title = existing?.title ?? "(deleted)";
        bool syncEnabled = _auth.IsAuthenticated && !string.IsNullOrEmpty(_auth.Email);

        await js.InvokeVoidAsync("idbPutNoteMeta", new
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

        await js.InvokeVoidAsync("idbDeleteNoteContent", existing?.key ?? metaKey);
        return deletedAt;
    }

    public Task<DateTime> DeleteNoteAsync(IJSRuntime js, string id) =>
        TombstoneDeleteNoteAsync(js, id);

    /// <summary>
    /// Apply a remote delete if the tombstone is newer than local content (or local is already deleted).
    /// </summary>
    public async Task<bool> ShouldAcceptIncomingContentAsync(IJSRuntime js, string id, List<ChatMessage> entries)
    {
        var meta = await GetMetaByIdAsync(js, id);
        if (meta == null || string.IsNullOrEmpty(meta.deletedAt))
            return true;

        var deletedAtTicks = DateTime.Parse(meta.deletedAt).Ticks;
        return ChatMessageHelper.GetLatestContentTicks(entries) > deletedAtTicks;
    }

    public async Task<bool> TryApplyRemoteDeleteAsync(IJSRuntime js, string id, long deletedAtTicks)
    {
        var remoteDeletedAt = new DateTime(deletedAtTicks, DateTimeKind.Utc);
        var meta = await GetMetaByIdAsync(js, id);
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
            var contentTicks = ChatMessageHelper.GetLatestContentTicks(await LoadNoteAsync(js, id));
            if (contentTicks > deletedAtTicks || DateTime.Parse(meta.lastUpdated).Ticks > deletedAtTicks)
                return false;
        }

        await TombstoneDeleteNoteAsync(js, id, remoteDeletedAt);
        return true;
    }
}