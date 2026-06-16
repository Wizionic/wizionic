using Microsoft.JSInterop;
using System.Text.Json;

namespace ChatfishApp.Client.Services;

/// <summary>
/// Client-side storage for conversation history (multi-convo, titles, messages).
/// 
/// Data now lives in IndexedDB ("chatfish-conversations" DB) for much larger capacity
/// than localStorage and a clean schema that supports future selective cross-device sync.
/// 
/// All *content* blobs (the JSON of List&lt;ChatMessage&gt;) are *always* AES-GCM encrypted
/// before being written:
///   - Authenticated users: use the server-provided per-user key (enables sync across devices).
///   - Guest / unauthenticated: a local device-only key is generated on first use and stored
///     inside the IDB. This satisfies the requirement that even unauthenticated chats must
///     be encrypted at rest in the browser.
/// 
/// Lightweight metadata (title, lastUpdated, syncEnabled flag) is stored in cleartext so the
/// list of conversations is visible even before the encryption key is ready.
/// 
/// The "syncEnabled" flag per conversation makes the structure ready for "sync all or only some".
/// </summary>
public class WasmConversationStore
{
    private const string LastConvoKey = "wasmchat-last-convo";
    private const string ConvoPrefix = "wasmchat-convo-";

    private readonly WasmAuthService _auth;
    private readonly WasmCryptoService _crypto;

    public WasmConversationStore(WasmAuthService auth, WasmCryptoService crypto)
    {
        _auth = auth;
        _crypto = crypto;

        // When the user logs in (or out) the consumer (Chat) can decide to
        // reload the list under the new namespace. We still notify.
        _auth.OnChanged += () => { /* consumer decides when to reload */ };
    }

    public record LocalConvo(string Id, string Title, DateTime LastUpdated);

    public record SyncManifestEntry(string Id, string Title, long LastUpdatedTicks, string ContentFingerprint, long? DeletedAtTicks = null)
    {
        public bool IsDeleted => DeletedAtTicks.HasValue;
    }

    /// <summary>
    /// Represents a file attachment uploaded by the user in a conversation turn.
    /// DataBase64 holds the full content (for vision models we send the bytes; for display we use data: urls for images).
    /// </summary>
    public record Attachment(string Name, string ContentType, string DataBase64, long Size)
    {
        /// <summary>Convenience data URL for image thumbnails (null for non-images like PDF).</summary>
        public string? DataUrl => ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            ? $"data:{ContentType};base64,{DataBase64}"
            : null;
    }

    private static string GetStableHash(string input)
    {
        if (string.IsNullOrEmpty(input)) return "00000000";
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
        return BitConverter.ToString(bytes, 0, 4).Replace("-", "").ToLowerInvariant();
    }

    private string GetPrefix()
    {
        if (_auth.IsAuthenticated)
        {
            if (!string.IsNullOrEmpty(_auth.UserId))
                return $"u-{_auth.UserId}-";  // stable across refreshes/processes
            if (!string.IsNullOrEmpty(_auth.Email))
                return $"e-{GetStableHash(_auth.Email)}-";
        }
        return "wasmchat-";  // guest (compat with any very old guest data)
    }

    /// <summary>
    /// Always returns a usable AES-GCM base64 key for encrypting/decrypting
    /// *history content* in the current namespace (guest or authed).
    /// This is what makes "all chats (including unauthenticated) are encrypted in IDB" true.
    /// </summary>
    private async Task<string> GetHistoryKeyAsync(IJSRuntime js)
    {
        return await _auth.GetOrCreateHistoryEncryptionKeyAsync(js);
    }

    private async Task<string?> ReadContentAsync(IJSRuntime js, string fullKey)
    {
        var keyB64 = await GetHistoryKeyAsync(js);
        var stored = await js.InvokeAsync<string>("idbGetContent", fullKey);
        if (string.IsNullOrEmpty(stored) || string.IsNullOrEmpty(keyB64))
            return stored;

        return await _crypto.DecryptAsync(keyB64, stored);
    }

    private async Task WriteContentAsync(IJSRuntime js, string fullKey, string json)
    {
        var keyB64 = await GetHistoryKeyAsync(js);
        string toStore = json;
        if (!string.IsNullOrEmpty(keyB64))
        {
            toStore = await _crypto.EncryptAsync(keyB64, json);
        }
        await js.InvokeVoidAsync("idbPutContent", fullKey, toStore);
    }

    public async Task<List<LocalConvo>> LoadIndexAsync(IJSRuntime js)
    {
        var ns = GetPrefix();
        // Load lightweight meta objects (no content blobs). They are stored in cleartext
        // so the list is always visible even if we don't have the encryption key yet.
        var metas = await js.InvokeAsync<List<StoredMeta>>("idbGetMetasByNamespace", ns);

        if (_auth.IsAuthenticated)
        {
            // Recovery for data saved during the period when namespace used unstable
            // email.GetHashCode() (different value after every refresh).
            // We pull in any old "wasmchat-..." or "e-..." metas (their .key will be used
            // later for content load if the computed ns doesn't match).
            try
            {
                var allMetas = await js.InvokeAsync<List<StoredMeta>>("idbGetAllMetas");
                var legacy = allMetas
                    .Where(m => !string.IsNullOrEmpty(m.@namespace) &&
                                (m.@namespace.StartsWith("wasmchat-") || m.@namespace.StartsWith("e-")) &&
                                m.@namespace != ns)
                    .ToList();

                var currentIds = metas.Select(m => m.id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var lm in legacy)
                {
                    if (!currentIds.Contains(lm.id))
                    {
                        metas.Add(lm);
                        currentIds.Add(lm.id);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WasmConvStore] Legacy meta recovery skipped: {ex.Message}");
            }
        }

        var result = metas
            .Where(m => string.IsNullOrEmpty(m.deletedAt))
            .OrderByDescending(m => m.lastUpdated)
            .Select(m => new LocalConvo(m.id, m.title ?? "(empty)", DateTime.Parse(m.lastUpdated)))
            .ToList();

        return result;
    }

    // Internal shape coming back from the JS idbGetMetasByNamespace helper.
    // Matches the property names we use when putting (lowercase for namespace etc.).
    private record StoredMeta(string key, string id, string @namespace, string title, string lastUpdated, bool syncEnabled, string? contentFingerprint, string? deletedAt, bool? titleIsCustom);

    private static bool HasCustomTitle(StoredMeta? meta) => meta?.titleIsCustom == true;

    public async Task<List<SyncManifestEntry>> LoadManifestEntriesAsync(IJSRuntime js, bool backfillMissingFingerprints = false)
    {
        var ns = GetPrefix();
        var metas = await js.InvokeAsync<List<StoredMeta>>("idbGetMetasByNamespace", ns);
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
                var messages = await LoadConversationAsync(js, m.id);
                fingerprint = SyncFingerprint.ForConversation(m.id, title, messages);
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

    private async Task<StoredMeta?> GetMetaByIdAsync(IJSRuntime js, string id)
    {
        var ns = GetPrefix();
        var metas = await js.InvokeAsync<List<StoredMeta>>("idbGetMetasByNamespace", ns);
        var meta = metas.FirstOrDefault(m => string.Equals(m.id, id, StringComparison.OrdinalIgnoreCase));
        if (meta != null)
            return meta;

        try
        {
            var allMetas = await js.InvokeAsync<List<StoredMeta>>("idbGetAllMetas");
            return allMetas.FirstOrDefault(m => string.Equals(m.id, id, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private async Task PersistContentFingerprintAsync(IJSRuntime js, StoredMeta meta, string title, string fingerprint, string? deletedAt)
    {
        await js.InvokeVoidAsync("idbPutMeta", new
        {
            key = meta.key,
            id = meta.id,
            @namespace = meta.@namespace,
            title,
            lastUpdated = meta.lastUpdated,
            syncEnabled = meta.syncEnabled,
            contentFingerprint = fingerprint,
            deletedAt = deletedAt ?? "",
            titleIsCustom = meta.titleIsCustom ?? false
        });
    }

    /// <summary>
    /// Soft-delete: keep tombstone meta, remove content blob. Returns UTC delete time for sync.
    /// </summary>
    public async Task<DateTime> TombstoneDeleteConversationAsync(IJSRuntime js, string id, DateTime? deletedAtUtc = null)
    {
        var deletedAt = deletedAtUtc ?? DateTime.UtcNow;
        var deletedAtIso = deletedAt.ToString("o");
        var ns = GetPrefix();
        var metaKey = ns + ConvoPrefix + id;
        var existing = await GetMetaByIdAsync(js, id);
        var title = existing?.title ?? "(deleted)";
        bool syncEnabled = _auth.IsAuthenticated && !string.IsNullOrEmpty(_auth.Email);

        await js.InvokeVoidAsync("idbPutMeta", new
        {
            key = existing?.key ?? metaKey,
            id,
            @namespace = ns,
            title,
            lastUpdated = deletedAtIso,
            syncEnabled,
            contentFingerprint = DeleteSyncPayload.AckValue(deletedAt.Ticks),
            deletedAt = deletedAtIso,
            titleIsCustom = existing?.titleIsCustom ?? false
        });

        await js.InvokeVoidAsync("idbDeleteConvoContent", existing?.key ?? metaKey);

        try
        {
            var allMetas = await js.InvokeAsync<List<StoredMeta>>("idbGetAllMetas");
            foreach (var m in allMetas.Where(m => string.Equals(m.id, id, StringComparison.OrdinalIgnoreCase)))
            {
                if (!string.IsNullOrEmpty(m.key))
                    await js.InvokeVoidAsync("idbDeleteConvoContent", m.key);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WasmConvStore] Legacy content cleanup for {id}: {ex.Message}");
        }

        return deletedAt;
    }

    /// <summary>
    /// Apply a remote delete if the tombstone is newer than local content (or local is already deleted).
    /// </summary>
    public async Task<bool> ShouldAcceptIncomingContentAsync(IJSRuntime js, string id, List<ChatMessage> messages)
    {
        var meta = await GetMetaByIdAsync(js, id);
        if (meta == null || string.IsNullOrEmpty(meta.deletedAt))
            return true;

        var deletedAtTicks = DateTime.Parse(meta.deletedAt).Ticks;
        return ChatMessageHelper.GetLatestContentTicks(messages) > deletedAtTicks;
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
            var contentTicks = ChatMessageHelper.GetLatestContentTicks(await LoadConversationAsync(js, id));
            if (contentTicks > deletedAtTicks || DateTime.Parse(meta.lastUpdated).Ticks > deletedAtTicks)
                return false;
        }

        await TombstoneDeleteConversationAsync(js, id, remoteDeletedAt);
        return true;
    }

    public async Task<string?> GetLastConvoIdAsync(IJSRuntime js)
    {
        var ns = GetPrefix();
        var settingKey = ns + LastConvoKey;
        return await js.InvokeAsync<string>("idbGetSetting", settingKey);
    }

    public async Task SetLastConvoIdAsync(IJSRuntime js, string id)
    {
        var ns = GetPrefix();
        var settingKey = ns + LastConvoKey;
        await js.InvokeVoidAsync("idbPutSetting", settingKey, id);
    }

    public async Task<List<ChatMessage>> LoadConversationAsync(IJSRuntime js, string id)
    {
        var ns = GetPrefix();
        var fullKey = ns + ConvoPrefix + id;
        var json = await ReadContentAsync(js, fullKey);
        if (string.IsNullOrEmpty(json))
        {
            // Legacy recovery: the convo may have been saved under an old namespace
            // (due to previous unstable GetHashCode). Look up the meta by id and
            // use whatever storage key it recorded.
            try
            {
                var allMetas = await js.InvokeAsync<List<StoredMeta>>("idbGetAllMetas");
                var meta = allMetas.FirstOrDefault(m => string.Equals(m.id, id, StringComparison.OrdinalIgnoreCase));
                if (meta != null && !string.IsNullOrEmpty(meta.key) && meta.key != fullKey)
                {
                    json = await ReadContentAsync(js, meta.key);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WasmConvStore] Legacy content key lookup failed for {id}: {ex.Message}");
            }
        }
        if (string.IsNullOrEmpty(json))
            return new List<ChatMessage>();

        var messages = JsonSerializer.Deserialize<List<ChatMessage>>(json) ?? new();
        return ChatMessageHelper.NormalizeAll(messages);
    }

    public async Task SaveConversationAsync(IJSRuntime js, string id, List<ChatMessage> messages)
    {
        var ns = GetPrefix();
        var fullKey = ns + ConvoPrefix + id;
        var json = JsonSerializer.Serialize(messages);
        await WriteContentAsync(js, fullKey, json);
    }

    public Task<DateTime> DeleteConversationAsync(IJSRuntime js, string id) =>
        TombstoneDeleteConversationAsync(js, id);

    public async Task<string?> GetMetaTitleAsync(IJSRuntime js, string id)
    {
        var meta = await GetMetaByIdAsync(js, id);
        return meta?.title;
    }

    public async Task<(string Title, bool TitleIsCustom)> GetMetaTitleInfoAsync(IJSRuntime js, string id)
    {
        var meta = await GetMetaByIdAsync(js, id);
        var title = string.IsNullOrWhiteSpace(meta?.title) ? "(empty)" : meta.title;
        return (title, HasCustomTitle(meta));
    }

    public async Task SetConversationTitleAsync(IJSRuntime js, string id, string title)
    {
        var ns = GetPrefix();
        var metaKey = ns + ConvoPrefix + id;
        var existing = await GetMetaByIdAsync(js, id);
        var normalizedTitle = NormalizeCustomTitle(title);
        var now = DateTime.UtcNow.ToString("o");
        bool syncEnabled = existing?.syncEnabled
            ?? (_auth.IsAuthenticated && !string.IsNullOrEmpty(_auth.Email));
        var messages = await LoadConversationAsync(js, id);
        var contentFingerprint = SyncFingerprint.ForConversation(id, normalizedTitle, messages);

        await js.InvokeVoidAsync("idbPutMeta", new
        {
            key = existing?.key ?? metaKey,
            id,
            @namespace = ns,
            title = normalizedTitle,
            lastUpdated = now,
            syncEnabled,
            contentFingerprint,
            deletedAt = existing?.deletedAt ?? "",
            titleIsCustom = true
        });
    }

    public async Task UpdateIndexAfterSaveAsync(IJSRuntime js, string id, List<ChatMessage> messages, List<LocalConvo> currentIndex)
    {
        var existing = await GetMetaByIdAsync(js, id);

        string title;
        bool titleIsCustom;
        if (HasCustomTitle(existing))
        {
            title = string.IsNullOrWhiteSpace(existing!.title) ? "(empty)" : existing.title;
            titleIsCustom = true;
        }
        else
        {
            var raw = messages.FirstOrDefault(m => ChatMessageHelper.IsVisible(m) && (m.Role == "user" || m.User == "LocalUser"))?.Content
                      ?? messages.FirstOrDefault(m => ChatMessageHelper.IsVisible(m))?.Content;
            title = string.IsNullOrWhiteSpace(raw) ? "(empty)" : StripHtmlForTitle(raw);
            titleIsCustom = false;
        }

        var ns = GetPrefix();
        var metaKey = ns + ConvoPrefix + id;
        var now = DateTime.UtcNow.ToString("o");

        bool syncEnabled = _auth.IsAuthenticated && !string.IsNullOrEmpty(_auth.Email);

        var contentFingerprint = SyncFingerprint.ForConversation(id, title, messages);

        await js.InvokeVoidAsync("idbPutMeta", new
        {
            key = existing?.key ?? metaKey,
            id,
            @namespace = ns,
            title,
            lastUpdated = now,
            syncEnabled,
            contentFingerprint,
            deletedAt = existing?.deletedAt ?? "",
            titleIsCustom
        });

        await SetLastConvoIdAsync(js, id);
    }

    /// <summary>
    /// Persists the per-conversation "should this convo participate in cross-device sync?"
    /// flag. This is the main knob that makes the storage schema "flexible for sync all or only some".
    /// Only meaningful for authenticated namespaces.
    /// </summary>
    public async Task SetConversationSyncEnabledAsync(IJSRuntime js, string id, bool enabled)
    {
        var ns = GetPrefix();
        var metaKey = ns + ConvoPrefix + id;

        // Read existing meta if present (so we don't lose title/lastUpdated).
        var existing = (await js.InvokeAsync<List<StoredMeta>>("idbGetMetasByNamespace", ns))
                       .FirstOrDefault(m => m.id == id);

        var now = (existing?.lastUpdated ?? DateTime.UtcNow.ToString("o"));

        var meta = new
        {
            key = metaKey,
            id = id,
            @namespace = ns,
            title = existing?.title ?? "(empty)",
            lastUpdated = now,
            syncEnabled = enabled,
            contentFingerprint = existing?.contentFingerprint ?? "",
            deletedAt = existing?.deletedAt ?? "",
            titleIsCustom = existing?.titleIsCustom ?? false
        };

        await js.InvokeVoidAsync("idbPutMeta", meta);
    }

    private static string StripHtmlForTitle(string htmlOrText)
    {
        if (string.IsNullOrWhiteSpace(htmlOrText)) return "(empty)";
        var plain = System.Text.RegularExpressions.Regex.Replace(htmlOrText, "<.*?>", string.Empty);
        plain = System.Net.WebUtility.HtmlDecode(plain).Trim();
        if (plain.Length > 30) plain = plain.Substring(0, 30) + "...";
        return plain;
    }

    private static string NormalizeCustomTitle(string title)
    {
        var trimmed = title.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return "(empty)";
        return trimmed.Length > 80 ? trimmed.Substring(0, 80) : trimmed;
    }

    // ChatMessage shape used for WASM local (and live sync) storage.
    // We keep the old "User" field for backward compat with existing localStorage data.
    // New code (and the live sync path) should prefer Role + raw Content (matching the
    // server Message entity) so that cross-device sync and the main hosted chat can
    // eventually share the same logical format.
    public record ChatMessage(
        string? Role = null,
        string Content = "",
        string? ModelUsed = null,
        DateTime? Timestamp = null,
        string? User = null,
        string? ToolTrace = null,
        List<Attachment>? Attachments = null,
        string? ContentFormat = null,
        string? ItemId = null,
        DateTime? ModifiedAt = null,
        DateTime? DeletedAt = null);
}
